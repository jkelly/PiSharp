// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/21-subagent-foreground.ts. Its product code is the same
// foreground subagent as Pi v1.1.0's packages/coding-agent/src/experimental/durable/subagent.ts, which v1.2.0 deleted
// (src/PiSharp.Cli/Durable/DurableSubagent.cs, PiSharp-only since; decision 0005). Upstream
// uses OpenAI when OPENAI_API_KEY is set and pi-ai's faux provider otherwise; here ScriptedModels always stands in, so the example runs
// offline. Two ordering barriers keep Node's event-loop order, under which the UI attaches to the child before the child answers and prints
// the child's answer before the parent's: the child's reply waits until the UI attached to the child, and the parent's final reply until the
// child's answer was printed. On a file backend the storage is then reopened and the child conversation and transcripts read back.
// Pi v1.5.0 sync (area events, f3255de76): the UI watches each conversation's view, as v1.5.0's 21-subagent-foreground.ts does: it prints
// the text and tool calls of the pi.assistant entries appended since the newest it saw, and attaches to the child a running subagent call
// names in its pi.live slot's details, where v1.2.0 printed watchEvents' message_end and tool_execution_start events (removed). Area
// tools-schema: the subagent tool's parameters are pi-schema's obj({ task: str({ description }) }) as JSON Schema. Area vocabulary
// (5f312dac4): each view listener takes the tagged call and skips one that is not a value. The example keeps its v1.2.0 name,
// 22-subagent-foreground, as the other renumbered examples do (b9fb2b854).
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
                        """{"type":"object","properties":{"task":{"type":"string","description":"What the subagent should do"}},"required":["task"],"additionalProperties":false}"""),
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

    // 22: a foreground subagent tool, with the UI attaching to the child through the call's running details (v1.5.0's 21).
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

        // ─── UI: the parent's transcript, with each subagent's indented under its call ───

        var gate = new SemaphoreSlim(1, 1);
        // Print an assistant entry's text and tool calls.
        async Task Print(string indent, EntryRecord entry)
        {
            if (entry.Kind != "pi.assistant" || entry.Model?[0] is not JsonObjectValue message || message["role"] is not JsonStringValue { Value: "assistant" }) return;
            foreach (var block in ((JsonArrayValue)message["content"]).OfType<JsonObjectValue>())
            {
                if (block["type"] is JsonStringValue { Value: "text" } && block["text"] is JsonStringValue { Value: { Length: > 0 } text })
                {
                    await output.WriteLineAsync($"{indent}assistant: {text}");
                    if (indent.Length > 0) childPrinted.TrySetResult();
                    else parentPrinted.TrySetResult();
                }
                if (block["type"] is JsonStringValue { Value: "toolCall" })
                    await output.WriteLineAsync($"{indent}tool {((JsonStringValue)block["name"]).Value}({block["arguments"].ToJson()})");
            }
        }
        var attached = new HashSet<ConversationId>();
        async Task Attach(ConversationId id, string indent)
        {
            attached.Add(id);
            var watch = await (await harness.ConversationAsync(id, Context))!.WatchAsync(Context);
            var newest = watch.Value.Entries.IsEmpty ? 0 : watch.Value.Entries[^1].Id.Value;
            watch.Start(async (call, _) =>
            {
                if (call is not WatchValueCall<ConversationView> { Value: var value }) return;
                await gate.WaitAsync();
                try
                {
                    foreach (var entry in value.Entries) if (entry.Id.Value > newest) await Print(indent, entry);
                    if (!value.Entries.IsEmpty) newest = value.Entries[^1].Id.Value;
                    // A running subagent call names its child conversation in its details.
                    var slots = value.Doc("pi.live") is { } live && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue array
                        ? array.OfType<JsonObjectValue>() : [];
                    foreach (var slot in slots)
                    {
                        if (!slot.TryGetValue("details", out var details) || details is not JsonObjectValue named ||
                            !named.TryGetValue("conversationId", out var conversationId) || conversationId is not JsonNumberValue number) continue;
                        var child = new ConversationId((long)number.Value);
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
        // Watch callbacks run after their commit; let the last ones print.
        await parentPrinted.Task;
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
