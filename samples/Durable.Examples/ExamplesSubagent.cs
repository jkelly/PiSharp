// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/22-subagent-foreground.ts. Its product code is the same
// foreground subagent as packages/coding-agent/src/experimental/durable/subagent.ts (src/PiSharp.Cli/Durable/DurableSubagent.cs). Upstream
// uses OpenAI when OPENAI_API_KEY is set and pi-ai's faux provider otherwise; here ScriptedModels always stands in, so the example runs
// offline. Two ordering barriers keep Node's event-loop order, under which the UI attaches to the child before the child answers and prints
// the child's answer before the parent's: the child's reply waits until the UI attached to the child, and the parent's final reply until the
// child's answer was printed. On a file backend the storage is then reopened and the child conversation and transcripts read back.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // ─── Product code: the subagent extension ───────────────────────────────────

    private static async Task<string> AnswerTextAsync(IToolExecutionApi api, EntryId answer, DurableContext callContext)
    {
        var entry = await api.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), callContext);
        return TextOf(entry?.Model?[0] as JsonObjectValue);
    }

    private static string TextOf(JsonObjectValue? message) => message is not null && message.TryGetValue("content", out var content) &&
        content is JsonArrayValue blocks
            ? string.Concat(blocks.OfType<JsonObjectValue>().Select(block => block["type"] is JsonStringValue { Value: "text" } ? ((JsonStringValue)block["text"]).Value : ""))
            : "";

    private static Extension CreateSubagent()
    {
        Extension subagent = null!;
        subagent = Define.Extension(new Extension
        {
            Name = "subagent",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "subagent",
                    Description = "Delegate a self-contained task to a subagent and get its answer back.",
                    Parameters = (JsonObjectValue)JsonValue.Parse(
                        """{"type":"object","required":["task"],"properties":{"task":{"type":"string","description":"What the subagent should do"}}}"""),
                    // Safe to rerun after a crash: a rerun finds the child it already created and the submission it already made.
                    Replay = ToolReplay.Safe,
                    Execute = async (args, api, callContext) =>
                    {
                        var task = ((JsonStringValue)((JsonObjectValue)args)["task"]).Value;
                        // The child is owned by this tool call's task, so aborting the call aborts the child, and the call finishes only once the
                        // child's work is done.
                        var child = await api.CommitAsync(async tx =>
                        {
                            // Ownership records the child: a rerun of this call finds it instead of creating another.
                            var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1)).Items.FirstOrDefault();
                            if (existing is not null) return existing.Id;
                            // Starts as a copy of this conversation's agent: model, thinking level, cwd, extensions, tools.
                            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                            // Without this extension, the child is not offered this tool.
                            await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Extensions = ExtensionSelection.Edit(remove: [subagent]) });
                            return created.Id;
                        }, callContext);
                        // A UI watching the parent sees this and can attach to the child.
                        await api.DetailsAsync(JsonValue.Object([new("conversationId", child.ToJson())]), callContext);

                        var handle = (await api.ConversationAsync(child, callContext))!;
                        // The request ID makes a rerun get back the submission it made before the crash.
                        var request = SubmissionDraft.Input(task, requestId: $"subagent:{api.TaskId.Value}");
                        var settled = await (await handle.SubmitAsync(request, callContext)).WaitAsync(callContext);
                        if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input)
                            throw new InvalidOperationException($"Subagent failed: {(settled.Status == SubmissionStatus.Unanswered ? "unanswered" : "done")}");
                        var text = await AnswerTextAsync(api, settled.Answer!.Value, callContext);
                        return new ToolExecutionResult
                        {
                            Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)])]),
                            Details = JsonValue.Object([new("conversationId", child.ToJson())]),
                        };
                    },
                }),
            ],
        });
        return subagent;
    }

    // 22: a foreground subagent tool, with the UI attaching to the child through the call's running details.
    private static async Task SubagentForegroundAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // ─── Host setup ─────────────────────────────────────────────────────────────

        // The parent delegates, the child answers, and the parent reports.
        var childAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childPrinted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentPrinted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ScriptedModels(
            ScriptedReply.ToolCall("subagent", JsonValue.Object([new("task", "Name three prime numbers.")]), "call-1"),
            ScriptedReply.Held("2, 3, and 5.", childAttached.Task),
            ScriptedReply.Held("The subagent says: 2, 3, and 5.", childPrinted.Task));
        var registry = Registry.Create();
        registry.Install(CreateSubagent());
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        // ─── UI: the parent's events, with each subagent's events indented under its call ───

        var gate = new SemaphoreSlim(1, 1);
        async Task Print(string indent, AgentEvent agentEvent)
        {
            if (agentEvent is MessageEndEvent { Entry.Kind: "pi.assistant" } end)
            {
                var text = TextOf(end.Entry.Model?[0] as JsonObjectValue);
                if (text != "") await output.WriteLineAsync($"{indent}assistant: {text}");
                if (indent.Length > 0) childPrinted.TrySetResult();
                else if (text != "") parentPrinted.TrySetResult();
            }
            else if (agentEvent is ToolExecutionStartEvent start) await output.WriteLineAsync($"{indent}tool {start.ToolName}({start.Args.ToJson()})");
        }
        var attached = new HashSet<ConversationId>();
        var streams = new List<IAgentEventStream>();
        async Task Attach(ConversationId id, string indent)
        {
            attached.Add(id);
            var stream = await harness.WatchEventsAsync(id, Context);
            streams.Add(stream);
            stream.Start(async (events, _) =>
            {
                await gate.WaitAsync();
                try
                {
                    foreach (var agentEvent in events)
                    {
                        await Print(indent, agentEvent);
                        if (agentEvent is not ToolExecutionUpdateEvent { Details: JsonObjectValue details } || !details.TryGetValue("conversationId", out var value)) continue;
                        var child = new ConversationId((long)((JsonNumberValue)value).Value);
                        if (attached.Contains(child)) continue;
                        await Attach(child, indent + "  ");
                        childAttached.TrySetResult();
                    }
                }
                finally { gate.Release(); }
            });
        }
        await Attach(root.Id, "");

        var submission = await root.SubmitAsync(SubmissionDraft.Input("Use the subagent tool to find three prime numbers, then tell me what it said."), Context);
        await submission.WaitAsync(Context);
        await harness.WaitForIdleAsync(Context);
        // Event callbacks run after their commit; let the last one print.
        await parentPrinted.Task;
        foreach (var stream in streams) await stream.StopAsync();
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var child = (await reopened.CommitAsync(tx => tx.ScanConversationsAsync(new ConversationQuery { OwnerConversationId = root.Id }, 10), Context)).Items.Single();
            await output.WriteLineAsync("child conversation: " + Json(child.ToJson()));
            await output.WriteLineAsync("parent transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20)));
            await output.WriteLineAsync("child transcript: " + Kinds(await TranscriptAsync(reopened, child.Id, 20)));
        });
    }
}
