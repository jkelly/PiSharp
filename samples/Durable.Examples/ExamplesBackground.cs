// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/examples/23-subagent-background.ts. Upstream uses OpenAI when
// OPENAI_API_KEY is set and otherwise pi-ai's faux provider, routing each request by its last message and streaming at 50 tokens per second.
// Here PacedModels always stands in with the same route and pacing (token size fixed at 4, upstream's is drawn between 3 and 5), and the
// Harness clock, the streaming and the example's waits all run on an ExampleClock that the example drives like Node's event loop: one timer
// at a time, everything it started settled before the next. So the subagent still answers while the main agent works, the long answer is
// still stopped halfway, and the restart still lands while a subagent works. Upstream keeps the storage in a temporary SQLite file; here that
// holds on the memory run, and a file backend restarts on the runner's storage. The transcript prints without ANSI colors, as upstream does
// when stdout is not a terminal.
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // ─── Product code ────────────────────────────────────────────────────────────

    // Each subagent is its own conversation with its own transcript, so it remembers earlier messages. The main conversation keeps a small
    // document that maps subagent names to their conversations ({conversationId, reported: answers already reported}) and the reporter task
    // of each subagent tool call. A fork of the main conversation starts without subagents.
    private static readonly ConversationDocToken Subagents = Documents.DefineConversationDoc("app.subagents", 1, DocumentFork.Initial,
        () => JsonValue.Object([new("agents", JsonObjectValue.Empty), new("reporters", JsonObjectValue.Empty)]));

    private static TaskPhaseHandler Terminal(Func<TaskState> state) => (_, runtime, taskContext) => runtime.CommitAsync((_, _) => state(), taskContext);

    // A subagent must outlive the main agent's turns, so its conversation is owned by an anchor: a background task that finishes at once. A
    // background task is a boundary: the main agent's Esc and idle waits stop there, but an abort with Background still reaches past it.
    private static readonly DurableTask Anchor = Tasks.DefineTask(new TaskDefinition
    {
        Name = "app.subagent-anchor",
        Version = 1,
        Initial = _ => JsonValue.Object([new("phase", "done")]),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["done"] = Terminal(() => NextTaskState.Completed(JsonValue.Null)) },
        Abort = Terminal(() => NextTaskState.Aborted()),
    });

    private static string Str(JsonValue value, string key) =>
        value is JsonObjectValue item && item.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : "";

    private static Extension CreateSubagentTools()
    {
        // A reporter delivers one message to a subagent and reports the answer to the main agent. It is a background task too. Each phase ends
        // with a saved checkpoint, and request IDs make a repeated submission return the first one.
        var reporter = Tasks.DefineTask(new TaskDefinition
        {
            Name = "app.subagent-reporter",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "deliver")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                // Send the message, wait for its answer, and decide what to report.
                ["deliver"] = async (task, runtime, taskContext) =>
                {
                    var input = (JsonObjectValue)task.Input;
                    var name = Str(input, "name");
                    var conversationId = new ConversationId((long)((JsonNumberValue)input["conversationId"]).Value);
                    // While the subagent is busy, a steer reaches it at its next step and a follow-up after its current answer.
                    var subagent = (await runtime.ConversationAsync(conversationId, taskContext))!;
                    var followUp = input["followUp"] is JsonBooleanValue { Value: true };
                    var submission = await subagent.SubmitAsync(SubmissionDraft.Input(Str(input, "message"), followUp ? WhenBusy.FollowUp : WhenBusy.Steer,
                        $"subagent:{task.Id.Value}"), taskContext);
                    var settled = await submission.WaitAsync(taskContext);
                    // One commit decides the report and records the answer as delivered, so a restart does not decide again.
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        TaskState Next(string? report) => NextTaskState.Running(report is null
                            ? JsonValue.Object([new("phase", "report")])
                            : JsonValue.Object([new("phase", "report"), new("report", report)]));
                        // Aborted: stopped, or withdrawn while queued. Nothing to report.
                        if (settled.Status == SubmissionStatus.Unanswered)
                            return Next(settled.Reason == "aborted" ? null : $"[subagent {name} failed: {settled.Reason}]");
                        if (settled.Type != SubmissionType.Input || settled.Answer is not { } answer) return Next(null);
                        var reported = (await tx.DocAsync(Subagents, runtime.ConversationId)).GetObject("agents").GetObject(name).GetArray("reported");
                        for (var index = 0; index < reported.Count; index++)
                            if (reported.Get(index) is JsonNumberValue number && (long)number.Value == answer.Value) return Next(null);
                        reported.Add(answer.ToJson());
                        var entry = await tx.GetEntryAsync(Entries.AssistantEntry, answer);
                        return Next($"[subagent {name} answered, no reply needed] {TextOf(entry?.Model?[0] as JsonObjectValue)}");
                    }, taskContext);
                },
                // Post the report as a follow-up input: it starts a turn when the main agent is idle, or waits for its current answer.
                ["report"] = async (task, runtime, taskContext) =>
                {
                    if (task.Checkpoint is JsonObjectValue checkpoint && checkpoint.TryGetValue("report", out var report))
                    {
                        var main = (await runtime.ConversationAsync(runtime.ConversationId, taskContext))!;
                        await main.SubmitAsync(SubmissionDraft.Input(report, WhenBusy.FollowUp, $"subagent-report:{task.Id.Value}"), taskContext);
                    }
                    await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), taskContext);
                },
            },
            Abort = Terminal(() => NextTaskState.Aborted()),
        });

        Extension tools = null!;
        var subagentTool = Define.Tool(new ToolRegistration
        {
            Name = "subagent",
            Description = "Manage persistent subagents that work in the background. Actions: spawn (name, message), send (name, message; " +
                "followUp: true queues it after the current answer instead of steering), stop (name: aborts its current work), " +
                "status (name, or all subagents without one). Answers are reported back to you when they arrive.",
            Parameters = (JsonObjectValue)JsonValue.Parse("""
                {"type":"object","required":["action"],"properties":{"action":{"anyOf":[{"const":"spawn","type":"string"},{"const":"send","type":"string"},{"const":"stop","type":"string"},{"const":"status","type":"string"}]},"name":{"type":"string"},"message":{"type":"string"},"followUp":{"type":"boolean"}}}
                """),
            // A call interrupted by a crash is not rerun: repeating `stop` could stop newer work.
            Replay = ToolReplay.Unsafe,
            Execute = async (args, api, callContext) =>
            {
                var call = (JsonObjectValue)args;
                var action = Str(call, "action");
                string? name = call.ContainsKey("name") ? Str(call, "name") : null;
                string? message = call.ContainsKey("message") ? Str(call, "message") : null;
                var followUp = call.TryGetValue("followUp", out var flag) && flag is JsonBooleanValue { Value: true };
                ToolExecutionResult Reply(string text, ConversationId? conversationId = null) => new()
                {
                    Content = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)])]),
                    // A UI can attach to the subagent's conversation through the call's details.
                    Details = conversationId is { } id && name is not null ? JsonValue.Object([new("name", name), new("conversationId", id.ToJson())]) : null,
                };
                var registry = await api.SnapshotAsync(Subagents, api.ConversationId, callContext);
                var agents = registry?["agents"] as JsonObjectValue ?? JsonObjectValue.Empty;
                ConversationId Of(JsonValue agent) => new((long)((JsonNumberValue)((JsonObjectValue)agent)["conversationId"]).Value);

                if (action == "status")
                {
                    var names = name is null ? agents.Keys.ToList() : [name];
                    var lines = new List<string>();
                    foreach (var each in names)
                    {
                        if (!agents.TryGetValue(each, out var found)) continue;
                        // A conversation is busy while it has a run: from an input until its final answer.
                        var busy = (await api.SnapshotAsync(Live.LiveDoc, Of(found), callContext))?.ContainsKey("run") == true;
                        lines.Add($"{each}: {(busy ? "working" : "idle")}");
                    }
                    return Reply(lines.Count == 0 ? "No subagents." : string.Join("\n", lines));
                }
                if (name is null) return Reply($"{action} needs a name.");
                agents.TryGetValue(name, out var agent);
                if (action != "spawn" && agent is null) return Reply($"No subagent named {name}.");

                if (action == "stop")
                {
                    // Aborts the subagent's current answer and tools and drops its queued messages. It stays usable.
                    await (await api.ConversationAsync(Of(agent!), callContext))!.AbortAsync(callContext);
                    return Reply($"Stopped {name}.", Of(agent!));
                }
                if (message is null) return Reply($"{action} needs a message.");

                // spawn and send: one commit starts a reporter for the message.
                var result = await api.CommitAsync(async tx =>
                {
                    var state = await tx.DocAsync(Subagents, api.ConversationId);
                    // Both tasks belong to the main conversation and are background: its Esc and idle waits skip them.
                    var background = new TaskOptions(TaskOwnership.Conversation) { Background = true };
                    if (action == "spawn")
                    {
                        if (state.GetObject("agents").ContainsKey(name)) return $"{name} already exists; use send.";
                        var anchor = await tx.CreateTaskAsync(Anchor, JsonValue.Null, background);
                        // Owned by a task of the main conversation: starts as a copy of the main agent.
                        var child = await tx.CreateConversationAsync(ConversationOwnership.Task(anchor));
                        // Subagents cannot start subagents, and know who they are.
                        await Agents.ConfigureAsync(tx, child.Id, new AgentChange
                        {
                            Extensions = ExtensionSelection.Edit(remove: [tools]),
                            Instructions = $"You are the subagent \"{name}\". Answer the main agent's requests.",
                        });
                        state.GetObject("agents").Set(name, JsonValue.Object([new("conversationId", child.Id.ToJson()), new("reported", JsonValue.Array())]));
                    }
                    var conversationId = state.GetObject("agents").GetObject(name)["conversationId"];
                    var input = JsonValue.Object([new("name", name), new("conversationId", conversationId), new("message", message),
                        new("followUp", action == "send" && followUp)]);
                    var reporterId = await tx.CreateTaskAsync(reporter, input, background);
                    state.GetObject("reporters").Set(api.TaskId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), reporterId.ToJson());
                    return action == "send" ? $"Sent to {name}." : $"Started {name}.";
                }, callContext);
                var current = (await api.SnapshotAsync(Subagents, api.ConversationId, callContext))?["agents"] is JsonObjectValue all && all.TryGetValue(name, out var now)
                    ? Of(now)
                    : (ConversationId?)null;
                return Reply(result, current);
            },
        });
        // The task definitions come with the extension, so pending reporters resume after a restart once the host installs it again.
        tools = new Extension { Name = "subagent-tools", Tasks = [Anchor, reporter], Tools = [subagentTool] };
        return tools;
    }

    // The main agent and the subagent share one scripted model, which answers each request by its last message.
    private static ScriptedReply RouteSubagents(ImmutableArray<JsonValue> messages)
    {
        static ScriptedReply Call(params (string Key, string Value)[] input) => ScriptedReply.ToolCall("subagent",
            JsonValue.Object(input.Select(field => new KeyValuePair<string, JsonValue>(field.Key, field.Value))));
        // System messages carry prompt changes; the request is about the message before them.
        var last = (JsonObjectValue)messages.Last(message => Str(message, "role") != "system");
        var text = last["content"] is JsonStringValue plain ? plain.Value : TextOf(last);
        // The main agent repeats what the tool said.
        if (Str(last, "role") == "toolResult") return $"OK. {text}";
        // The main agent.
        if (text.Contains("Start a subagent", StringComparison.Ordinal))
            return Call(("action", "spawn"), ("name", "reader"), ("message", "Summarize the plot of Moby Dick."));
        if (text.Contains("whale's name", StringComparison.Ordinal)) return Call(("action", "send"), ("name", "reader"), ("message", "What is the whale called?"));
        if (text.Contains("every chapter", StringComparison.Ordinal))
            return Call(("action", "send"), ("name", "reader"), ("message", "Now go through all chapters in detail."));
        if (text.Contains("Stop reader", StringComparison.Ordinal)) return Call(("action", "stop"), ("name", "reader"));
        if (text.Contains("my subagents", StringComparison.Ordinal)) return Call(("action", "status"));
        if (text.Contains("[subagent", StringComparison.Ordinal)) return "Noted.";
        // The subagent: short answers, and a long chapter walk-through that is stopped halfway.
        if (text.Contains("Summarize the plot", StringComparison.Ordinal)) return "A whale, a captain, an obsession.";
        if (text.Contains("whale called", StringComparison.Ordinal)) return "Moby Dick.";
        return string.Join("\n", Enumerable.Range(1, 135).Select(index => $"Chapter {index}: more whaling."));
    }

    // 23: persistent background subagents. One subagent tool lets the main agent start named subagents, message them (steer or follow up),
    // stop them mid-answer, and list them. Subagents keep working while the main agent answers the user, and each answer is delivered back
    // to the main agent as a new message once it arrives. Everything survives a restart: the example closes the Harness while a subagent
    // works and reopens it.
    private static async Task SubagentBackgroundAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        string? directory = null;
        var probe = await open();
        await probe.CloseAsync(Context);
        if (probe is MemoryStorage)
        {
            directory = ExampleDirectory("pi-durable-subagents-");
            var databasePath = Path.Combine(directory, "session.sqlite");
            open = async () => await SqliteStorage.OpenAsync(databasePath);
        }
        var clock = new ExampleClock();
        var models = new PacedModels(clock, 50, 4, RouteSubagents);
        var registry = Registry.Create();
        registry.Install(CreateSubagentTools());
        async Task<(IHarness Harness, IConversation Root)> Open()
        {
            var opened = await Harness.OpenAsync(clock.Observe(await open()),
                new HarnessOptions { Models = models, Registry = registry, TimeProvider = clock }, Context);
            return (opened, await opened.RootAsync(Context, new AgentChange { Model = Faux }));
        }

        // ─── UI: the main conversation's transcript, as a user would see it ───

        static string ContentText(JsonValue content) => content is JsonStringValue text
            ? text.Value
            : string.Concat(((JsonArrayValue)content).OfType<JsonObjectValue>().Select(part => part.TryGetValue("text", out var value) && value is JsonStringValue text ? text.Value : ""));
        var reportPattern = new Regex(@"^\[subagent (\S+) ([^\]]*)\] ?(.*)$", RegexOptions.Singleline);
        // Prints the main conversation's messages as they are committed. Subagents work in their own conversations.
        async Task Follow(IHarness harness, ConversationId id)
        {
            var stream = await harness.WatchEventsAsync(id, Context);
            stream.Start(async (events, _) =>
            {
                foreach (var agentEvent in events)
                {
                    if (agentEvent is not MessageEndEvent end || end.Entry.Model?[0] is not JsonObjectValue message) continue;
                    switch (Str(message, "role"))
                    {
                        case "user":
                        {
                            // Input is either the user or a subagent's report, which arrives whenever the subagent is done.
                            var text = ContentText(message["content"]);
                            var report = reportPattern.Match(text);
                            await output.WriteLineAsync(report.Success
                                ? $"\n> {report.Groups[1].Value}: {(report.Groups[3].Value is { Length: > 0 } said ? said : report.Groups[2].Value)}"
                                : $"\n> {text}");
                            break;
                        }
                        case "assistant":
                            foreach (var part in ((JsonArrayValue)message["content"]).OfType<JsonObjectValue>().Where(part => Str(part, "type") == "toolCall"))
                            {
                                var arguments = (JsonObjectValue)part["arguments"];
                                var sent = arguments.ContainsKey("message") ? " " + Json(arguments["message"]) : "";
                                var name = arguments.ContainsKey("name") ? " " + Str(arguments, "name") : "";
                                await output.WriteLineAsync($"  {Str(part, "name")} {Str(arguments, "action")}{name}{sent}");
                            }
                            if (TextOf(message) != "") await output.WriteLineAsync(TextOf(message));
                            break;
                        case "toolResult":
                            await output.WriteLineAsync($"  → {ContentText(message["content"])}");
                            break;
                    }
                }
            });
        }

        var (harness, root) = await Open();
        await Follow(harness, root.Id);

        // Say something to the main agent and wait for its answer.
        async Task Say(string text) =>
            await clock.RunUntilAsync((await root.SubmitAsync(SubmissionDraft.Input(text), Context)).WaitAsync(Context));
        async Task<JsonObjectValue> ReadRegistry() =>
            await harness.SnapshotAsync(Subagents, root.Id, Context) ?? JsonValue.Object([new("agents", JsonObjectValue.Empty), new("reporters", JsonObjectValue.Empty)]);
        // Wait until every message to a subagent was answered and reported, and the main agent has reacted.
        async Task Settle()
        {
            foreach (var id in ((JsonObjectValue)(await ReadRegistry())["reporters"]).Values)
                await clock.RunUntilAsync(harness.WaitForTaskAsync(new TaskId((long)((JsonNumberValue)id).Value), Context));
            await clock.RunUntilAsync(root.WaitForIdleAsync(Context));
            // Event callbacks run after their commit; let the last ones print.
            await clock.RunForAsync(50);
        }
        // Poll check for up to 10 seconds of the example clock.
        async Task Until(Func<Task<bool>> check)
        {
            for (var tries = 0; tries < 1000 && !await check(); tries++) await clock.RunForAsync(10);
        }
        async Task<bool> Working(string name)
        {
            if ((await ReadRegistry())["agents"] is not JsonObjectValue agents || !agents.TryGetValue(name, out var agent)) return false;
            var conversationId = new ConversationId((long)((JsonNumberValue)((JsonObjectValue)agent)["conversationId"]).Value);
            return (await harness.SnapshotAsync(Live.LiveDoc, conversationId, Context))?.ContainsKey("run") == true;
        }

        // The main agent answers at once; the subagent's answer is reported back when it arrives.
        await Say("Start a subagent named reader that summarizes Moby Dick.");
        await Settle();

        // A long request, stopped while the subagent is still answering.
        await Say("Ask reader to summarize every chapter.");
        await Until(() => Working("reader"));
        await Say("Stop reader.");
        await Settle();

        await Say("What are my subagents doing?");
        await Settle();

        // The process stops while the subagent works on a message; after the restart its answer still arrives.
        async Task<int> Reporters() => ((JsonObjectValue)(await ReadRegistry())["reporters"]).Count;
        var before = await Reporters();
        await root.SubmitAsync(SubmissionDraft.Input("Ask reader for the whale's name."), Context);
        await Until(async () => await Reporters() > before);
        await harness.CloseAsync(Context);
        await output.WriteLineAsync("\n  (process restarts)");
        (harness, root) = await Open();
        await Follow(harness, root.Id);
        await Settle();

        await harness.CloseAsync(Context);
        if (directory is not null)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
