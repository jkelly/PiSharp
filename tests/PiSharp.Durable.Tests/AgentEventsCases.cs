// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-events.test.ts, and the tests of
// packages/durable/test/harness-structured.test.ts that listen to agent events (watchEvents, P10): "ends a turn at the generation's hold,
// before its successor's turn starts" of describe "tool rounds", and describe "tool rounds and events".
//
// PiSharp-only since the Pi v1.5.0 sync (area events, decision 0013): f3255de76 removed watchEvents, its event types and both groups of
// tests with spec §9.4 (v1.2.0's numbering); v1.5.0 has no events to compare. PiSharp keeps the projection in PiSharp.Durable.Hosting
// (AgentEvents.cs) for the device wire (decision 0008), and these cases keep their v1.2.0 expectations as authored PiSharp-only behaviour,
// not upstream evidence: agent-events.* on MemoryStorage, agent-events.jsonl.* on a fresh JSONL directory and agent-events.sqlite.* on a
// fresh SQLite file (harness.events.* and harness.structured.*.tool-rounds(-and-events).* before), agent-events.mssql.* on SQL Server
// (Storage/SqlServerStorageCases.cs), and the structured ones on Chord's store as agent-events.store.* (StoreStorageNode.cs, as the
// structured group's store run had them). The structured tests' v1.5.0 forms, which read the view and the task graph instead, are
// harness.structured.tool-rounds.hands-run-control-* and harness.structured.tool-rounds-as-the-view-shows-them.* (HarnessStructuredCases.cs).
// They run here, in PiSharp.Durable.Tests, because they need this suite's harness support (ChatSupport.cs, HarnessSupport.cs, Session line
// barriers and the internal commit observer), which PiSharp.Durable.Hosting.Tests cannot reach.
//
// Verbatim apart from the adaptations in ChatSupport.cs, HarnessSupport.cs, HarnessStructuredSupport.cs and HarnessViewCases.cs, and these:
// - Events are typed records; toEqual / toMatchObject on an event compare its ToJson() with upstream's object.
// - drained() is ViewTests.Drained (a Session line barrier: every batch a commit enqueued before it produced has been delivered). waitFor
//   is Eventually; it only bounds how long a slow machine may take to stream, run a tool, or reach a state.
// - Tools sleep with Task.Delay where upstream uses setTimeout: their output must cross the progress throttle in real time, as upstream's
//   does; the assertions compare event streams with the committed values whatever the timing.
// - applyChanges and the partial rebuild work on System.Text.Json nodes, as upstream's structuredClone and mutation do on objects.
// - An aborted controller is a DurableCancellation cancelled with Exception("cancelled").
// - Pi v1.5.0 sync (area partial-commits): the "streamed partials" cases upstream added to harness-events.test.ts (f715a15c1, da412b039,
//   1e42501bb) moved, with upstream, to harness-view-run.test.ts (HarnessViewRunCases.cs, harness.view-run.streamed-partials.*), where
//   they read the conversation view.
// - Pi v1.5.0 sync (area deferred-commits): the cases that follow commits as views do (pacing the stream, or reading what a commit
//   published) observe them as memory adopts them (SessionTests.ObserveCommits, upstream 6c2fd73b0's internal observeCommits), since
//   SubscribeCommits now sees deferred commits only once they are stored.
// - Pi v1.5.0 sync (area watch-folding, d8d99950a): the stream is Chord's NativeWatch, so pending batches are not folded as state watches are
//   (delivers-each-of-100-pending-batches-in-its-own-call-in-order-unlike-state-watches).
// - The structured tests: "ends a turn at the generation's hold ...": upstream's last `waitFor(() => turns().length === 4)` after the held
//   generation finishes holds at once; here a Session line barrier lets every batch the final commits produced arrive first, and then the
//   count must still be 4. "holds a faulted tool's slot and task_failed ...": upstream's "broken" tool returns a function in `details`,
//   which is not strict JSON; the result here carries a null tool name in `control.addTools`, which makes the same result commit throw (see
//   HarnessToolsRecoveryCases.cs).
using System.Collections.Immutable;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AgentEventsCases() => AgentEventsOn("agent-events.", () => Task.FromResult<IStorage>(new MemoryStorage()));

    private static IEnumerable<(string, Func<Task>)> AgentEventsJsonlCases() =>
        AgentEventsOn("agent-events.jsonl.", async () => await JsonlStorage.OpenAsync(Temp("events-jsonl"), DurableContext.Background));

    private static IEnumerable<(string, Func<Task>)> AgentEventsSqliteCases() =>
        AgentEventsOn("agent-events.sqlite.", async () => await SqliteStorage.OpenAsync(Path.Combine(Temp("events-sqlite"), "events.sqlite")));

    private static IEnumerable<(string, Func<Task>)> AgentEventsOn(string prefix, Func<Task<IStorage>> fresh) =>
        [.. EventTests.Cases(prefix, fresh), .. StructuredTests.EventCases(prefix, fresh)];
}

/// <summary>The agent-events.* cases of harness-events.test.ts and their support.</summary>
internal static partial class EventTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    internal sealed record Listening(IAgentEventStream Stream, SyncList<List<AgentEvent>> Batches)
    {
        public List<AgentEvent> Events() => [.. Batches.Snapshot().SelectMany(batch => batch)];
    }

    /// <summary>Attaches and starts an event stream that records every delivered batch (upstream <c>listen</c>).</summary>
    internal static async Task<Listening> Listen(IHarness harness, IConversation conversation)
    {
        var stream = await harness.WatchEventsAsync(conversation.Id, Context);
        var batches = new SyncList<List<AgentEvent>>();
        stream.Start((events, _) =>
        {
            batches.Add([.. events]);
            return Task.CompletedTask;
        });
        return new Listening(stream, batches);
    }

    private static ChatSetup Slow() => Setup(new FauxOptions { TokensPerSecond = 400, MinTokenSize = 1, MaxTokenSize = 1 });

    private static string Role(JsonValue message) => S(((JsonObjectValue)message)["role"]);

    /// <summary>Rebuilds the streamed text of block 0 from <c>message_start</c> and the text deltas that follow it (upstream
    /// <c>streamedText</c>).</summary>
    private static string StreamedText(IEnumerable<AgentEvent> events)
    {
        var text = "";
        foreach (var item in events)
        {
            if (item is MessageStartEvent start && Role(start.Message) == "assistant")
            {
                var content = (JsonArrayValue)start.Message["content"];
                text = content.Count > 0 && content[0] is JsonObjectValue block && block["type"] is JsonStringValue { Value: "text" } ? S(block["text"]) : "";
            }
            if (item is not MessageUpdateEvent update) continue;
            foreach (var change in update.Changes)
            {
                if (change is BlockStartChange { Kind: "text_start", ContentIndex: 0 } started && started.Block["type"] is JsonStringValue { Value: "text" })
                    text = S(started.Block["text"]);
                if (change is TextDeltaChange { Kind: "text_delta", ContentIndex: 0 } delta) text += delta.Delta;
            }
        }
        return text;
    }

    /// <summary>Applies message changes to a copy of <paramref name="message"/>, as an events-only consumer would (upstream
    /// <c>applyChanges</c>).</summary>
    internal static JsonObjectValue ApplyChanges(JsonObjectValue message, IEnumerable<MessageChange> changes)
    {
        static JsonNode Node(JsonValue value) => JsonNode.Parse(value.ToJson())!;
        var next = Node(message).AsObject();
        foreach (var change in changes)
        {
            switch (change)
            {
                case WholeMessageChange whole:
                    next = Node(whole.Message).AsObject();
                    break;
                case BlockChange block:
                    next["content"]![block.ContentIndex] = Node(block.Block);
                    break;
                case BlockStartChange start:
                    next["content"]!.AsArray().Insert(start.ContentIndex, Node(start.Block));
                    break;
                case TextDeltaChange delta:
                {
                    var target = next["content"]![delta.ContentIndex]!.AsObject();
                    var field = delta.Kind == "text_delta" ? "text" : "thinking";
                    target[field] = (string)target[field]! + delta.Delta;
                    break;
                }
                case ToolCallDeltaChange call:
                {
                    var target = next["content"]![call.ContentIndex]!["arguments"]!;
                    foreach (var segment in call.Path[..^1]) target = segment.Key is { } key ? target[key]! : target[(int)segment.Index]!;
                    var last = call.Path[^1];
                    if (last.Key is { } lastKey) target[lastKey] = (string)target[lastKey]! + call.Delta;
                    else target[(int)last.Index] = (string)target[(int)last.Index]! + call.Delta;
                    break;
                }
            }
        }
        return (JsonObjectValue)JsonValue.Parse(next.ToJsonString());
    }

    /// <summary>Committed partials of the Harness's generations, one per commit that has one (upstream <c>partialsOf</c>).</summary>
    private static SyncList<JsonObjectValue> PartialsOf(IHarness harness)
    {
        var partials = new SyncList<JsonObjectValue>();
        ObserveCommits(harness, (publication, _) =>
        {
            foreach (var change in DocumentChanges(publication))
            {
                if (change.Record.Kind == "pi.live" && change.Value is { } live && live.TryGetValue("generation", out var generation) &&
                    generation is JsonObjectValue state && state.TryGetValue("message", out var message))
                    partials.Add((JsonObjectValue)message);
            }
        });
        return partials;
    }

    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    private static ToolRegistration ToolOf(string name, string description, ToolExecute execute, JsonObjectValue? parameters = null) =>
        Define.Tool(new ToolRegistration { Name = name, Description = description, Parameters = parameters ?? NoParameters, Execute = execute });

    private static async Task<TaskId> RunTask(IHarness harness, IConversation root) =>
        new((long)N(((JsonObjectValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["run"])["taskId"]));

    private static async Task<T> Race<T>(Task release, CancellationToken signal, Func<T> result)
    {
        await await Task.WhenAny(release, HarnessTests.Aborted(signal));
        return result();
    }

    private static FauxStep Held(Task release, JsonObjectValue answer) =>
        new FauxResponseFactory(request => Race(release, request.Signal, () => answer));

    private static readonly ConversationCreateOptions Ownerless = new(ConversationOwnership.Ownerless);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh) =>
        [.. Events(prefix, fresh), .. ResetCases(prefix, fresh, root => root.ResetAsync(null, Context))];

    private static IEnumerable<(string, Func<Task>)> Events(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "streams-a-run-as-lifecycle-events-and-text-deltas-that-rebuild-the-answer", async () =>
        {
            var setup = Slow();
            var text = string.Concat(Enumerable.Repeat("streamed answer text ", 20));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText(text)]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var listening = await Listen(harness, root);
            MatchJson("""{"type":"snapshot","entries":[],"tools":[],"inbox":[]}""", listening.Stream.Snapshot.ToJson(), "snapshot");
            var partials = new SyncList<string>();
            ObserveCommits(harness, (publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                {
                    if (change.Record.Kind == "pi.live" && change.Value is { } live && live.TryGetValue("generation", out var generation) &&
                        generation is JsonObjectValue state && state.TryGetValue("message", out var message))
                        partials.Add(TextOf(message) ?? "");
                }
            });
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await submission.WaitAsync(Context);
            await ViewTests.Drained(harness);
            var all = listening.Events();
            var types = all.Select(item => item.Type).Where((type, index) => index == 0 || type != all[index - 1].Type).ToList();
            Seq(["message_start", "message_end", "submission", "run_start", "turn_start", "message_start", "message_update", "message_end",
                "turn_end", "run_end", "submission", "usage_changed"], types, "types");
            Equal("{\"type\":\"run_start\",\"inputs\":[" + submission.Id.Value + "]}", all.First(item => item is RunStartEvent).ToJson().ToJson(), "run_start");
            // After each event, the rebuilt text equals the committed partial of that commit.
            var rebuilt = all.Select((item, index) => (item, index))
                .Where(pair => (pair.item is MessageStartEvent start && Role(start.Message) == "assistant") || pair.item is MessageUpdateEvent)
                .Select(pair => StreamedText(all.Take(pair.index + 1))).ToList();
            Seq(partials.Snapshot(), rebuilt, "rebuilt partials");
            Check(partials.Count > 1, "more than one partial");
            var end = (MessageEndEvent)all.Last(item => item is MessageEndEvent);
            Equal(text, TextOf(end.Entry.Model![0]), "answer");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-tool-start-output-appends-and-the-result-entry", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HarnessTests.AddTool(setup.Registry, ToolOf("print", "Prints", async (_, api, _) =>
            {
                api.Output("one\n");
                await Task.Delay(150);
                api.Output("two\n");
                await gate.Task;
                return new ToolExecutionResult();
            }, O("""{"type":"object","properties":{"n":{"type":"number"}},"required":["n"]}""")));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("print", Obj(("n", 1)), "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var listening = await Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            // The output events rebuild the slot's retained window.
            string Output()
            {
                var window = "";
                foreach (var item in listening.Events())
                    if (item is ToolExecutionUpdateEvent { Output: { } output }) window = output.Apply(window);
                return window;
            }
            await HarnessTests.Eventually(() => Output() == "one\ntwo\n");
            gate.Resolve();
            await submission.WaitAsync(Context);
            await ViewTests.Drained(harness);
            var tool = listening.Events().Where(item => item.Type.StartsWith("tool_execution", StringComparison.Ordinal)).ToList();
            MatchExactly("""{"type":"tool_execution_start","toolCallId":"c1","toolName":"print","taskId":"<number>","args":{"n":1}}""", tool[0].ToJson(), "start");
            Equal("""["type","toolCallId","toolName","taskId","args"]""", JsonValue.Array(tool[0].ToJson().Keys.Select(key => (JsonValue)key)).ToJson(), "start keys");
            var end = tool[^1];
            MatchJson("""{"type":"tool_execution_end","toolCallId":"c1","entry":{"kind":"pi.tool-result"}}""", end.ToJson(), "end");
            // As in the coding agent, the tool ends directly before its result message.
            var all = listening.Events();
            var endIndex = all.IndexOf(end);
            Seq(["tool_execution_end", "message_start", "message_end"], all.Skip(endIndex).Take(3).Select(item => item.Type), "result order");
            MatchJson("""{"message":{"role":"toolResult","toolCallId":"c1"}}""", all[endIndex + 1].ToJson(), "result message");
            // Two turns: the tool round, and the answer.
            Equal(2, all.Count(item => item is TurnStartEvent), "turn starts");
            Equal(2, all.Count(item => item is TurnEndEvent), "turn ends");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-queued-submissions-inbox-changes-and-retries", async () =>
        {
            var setup = Setup();
            var release = new HarnessTests.Deferred();
            var error = FauxAssistantMessage([], stopReason: "error", errorMessage: "503 Service Unavailable");
            setup.Faux.SetResponses(Held(release.Task, FauxAssistantMessage([FauxText("first")])), error, FauxAssistantMessage([FauxText("second")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 1, BaseDelayMs = 1 };
            var listening = await Listen(harness, root);
            await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("f"), Context);
            await ViewTests.Drained(harness);
            Seq(["{\"type\":\"inbox_update\",\"items\":[{\"id\":" + followUp.Id.Value + ",\"mode\":\"followUp\"}]}"],
                listening.Events().OfType<InboxUpdateEvent>().Select(item => item.ToJson().ToJson()), "inbox updates");
            release.Resolve();
            await followUp.WaitAsync(Context);
            await ViewTests.Drained(harness);
            var types = listening.Events().Select(item => item.Type).ToList();
            Check(types.Contains("auto_retry_start"), "auto_retry_start");
            Check(types.Contains("auto_retry_end"), "auto_retry_end");
            Equal(2, types.Count(type => type == "run_start"), "run starts");
            Seq([SubmissionStatus.Placed, SubmissionStatus.Queued, SubmissionStatus.Done, SubmissionStatus.Placed, SubmissionStatus.Done],
                listening.Events().OfType<SubmissionEvent>().Select(item => item.Record.Status), "submission statuses");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        // Pi v1.5.0 (area watch-folding, d8d99950a): the stream is a NativeWatch, so pending batches are not folded as state watches are.
        yield return Case(prefix + "delivers-each-of-100-pending-batches-in-its-own-call-in-order-unlike-state-watches", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            for (var index = 0; index < 100; index++)
            {
                var data = JsonValue.Object([new("index", index)]);
                await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = data }), Context);
            }
            var batches = new SyncList<List<AgentEvent>>();
            stream.Start((events, _) =>
            {
                batches.Add([.. events]);
                return Task.CompletedTask;
            });
            await ViewTests.Drained(harness);
            var all = batches.Snapshot();
            Equal(100, all.Count, "batches");
            static JsonValue DataOf(AgentEvent item) => item is EntryAppendedEvent appended ? appended.Entry.Data ?? JsonValue.Null : item.ToJson();
            Seq(Enumerable.Range(0, 100).Select(index => "[{\"index\":" + index + "}]"),
                all.Select(events => JsonValue.Array(events.Select(DataOf)).ToJson()), "each batch in order");
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "replaces-undelivered-batches-with-one-snapshot-after-100-pending-batches", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            for (var index = 0; index < 101; index++) await ViewTests.Note(root, "note");
            var batches = new SyncList<List<AgentEvent>>();
            stream.Start((events, _) =>
            {
                batches.Add([.. events]);
                return Task.CompletedTask;
            });
            await ViewTests.Drained(harness);
            var all = batches.Snapshot();
            Equal(1, all.Count, "batches");
            Equal(1, all[0].Count, "events");
            Equal(101, ((SnapshotEvent)all[0][0]).Entries.Length, "snapshot entries");
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rebuilds-every-committed-partial-of-thinking-text-and-tool-call-arguments-from-message-changes", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 150, MinTokenSize = 1, MaxTokenSize = 1 });
            var message = FauxAssistantMessage(
            [
                FauxThinking(string.Concat(Enumerable.Repeat("thinking about it ", 10))),
                FauxText(string.Concat(Enumerable.Repeat("some text ", 10))),
                FauxToolCall("missing", Obj(("path", string.Concat(Enumerable.Repeat("a/long/path/", 10))), ("note", new string('x', 60))), "c1"),
            ], stopReason: "toolUse");
            setup.Faux.SetResponses(message, FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var partials = PartialsOf(harness);
            var listening = await Listen(harness, root);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await ViewTests.Drained(harness);
            var rebuilt = new List<JsonObjectValue>();
            JsonObjectValue? current = null;
            // The streamed tool-calling message, up to its end; the short final answer commits no partial.
            foreach (var item in listening.Events())
            {
                if (item is MessageEndEvent { Entry.Kind: "pi.assistant" }) break;
                if (item is MessageStartEvent start && Role(start.Message) == "assistant") current = start.Message;
                else if (item is MessageUpdateEvent update) current = ApplyChanges(current!, update.Changes);
                else continue;
                rebuilt.Add(current!);
            }
            var committed = partials.Snapshot();
            Check(committed.Count > 2, "more than two partials");
            Equal(committed.Count, rebuilt.Count, "rebuilt partials");
            for (var index = 0; index < committed.Count; index++)
                SameJson(committed[index]["content"].ToJson(), rebuilt[index]["content"], $"partial {index}");
            var kinds = listening.Events().OfType<MessageUpdateEvent>().SelectMany(update => update.Changes).Select(change => change.Type).ToHashSet();
            Check(kinds.Contains("thinking_delta") || kinds.Contains("text_delta"), "deltas");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rebuilds-a-sliding-tail-window-from-output-trims-and-appends", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HarnessTests.AddTool(setup.Registry, ToolOf("tail", "Prints lines", async (_, api, _) =>
            {
                for (var line = 0; line < 6; line++)
                {
                    api.Output($"line {line}\n");
                    await Task.Delay(120);
                }
                await gate.Task;
                return new ToolExecutionResult();
            }) with { OutputLimits = new ToolOutputLimits { MaxLines = 3, Retain = ToolOutputRetention.Tail } });
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("tail", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var outputs = new SyncList<string>();
            ObserveCommits(harness, (publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                {
                    if (change.Record.Kind != "pi.live" || change.Value is not { } live || !live.TryGetValue("tools", out var tools) ||
                        tools is not JsonArrayValue { Count: > 0 } slots || ((JsonObjectValue)slots[0]).TryGetValue("output", out var output) is false) continue;
                    var text = S(output);
                    var seen = outputs.Snapshot();
                    if (seen.Count == 0 || seen[^1] != text) outputs.Add(text);
                }
            });
            var listening = await Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await HarnessTests.Eventually(() => outputs.Snapshot() is { Count: > 0 } seen && seen[^1].EndsWith("line 5\n", StringComparison.Ordinal));
            gate.Resolve();
            await submission.WaitAsync(Context);
            await ViewTests.Drained(harness);
            var rebuilt = new List<string>();
            var window = "";
            foreach (var item in listening.Events())
            {
                if (item is not ToolExecutionUpdateEvent { Output: { } output }) continue;
                window = output.Apply(window);
                rebuilt.Add(window);
            }
            Seq(outputs.Snapshot(), rebuilt, "windows");
            Check(listening.Events().Any(item => item is ToolExecutionUpdateEvent { Output: ToolOutputDelta { TrimStart: not null } }), "a trim");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "emits-one-exact-batch-when-a-run-ends-and-a-queued-follow-up-starts-the-next", async () =>
        {
            var setup = Setup();
            var release = new HarnessTests.Deferred();
            setup.Faux.SetResponses(Held(release.Task, FauxAssistantMessage([FauxText("first")])), FauxAssistantMessage([FauxText("second")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("f"), Context);
            var listening = await Listen(harness, root);
            release.Resolve();
            await followUp.WaitAsync(Context);
            await ViewTests.Drained(harness);
            var boundary = listening.Batches.Snapshot().First(batch => batch.Any(item => item is RunEndEvent));
            Seq(["message_start", "message_end", "message_start", "message_end", "turn_end", "run_end", "submission", "submission", "inbox_update",
                "usage_changed", "run_start", "turn_start"], boundary.Select(item => item.Type), "boundary batch");
            Seq([input.Id, followUp.Id], boundary.OfType<SubmissionEvent>().Select(item => item.Record.Id), "submissions");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-a-call-that-never-runs-and-a-tool-aborted-with-its-generation", async () =>
        {
            var setup = Setup();
            HarnessTests.AddTool(setup.Registry, ToolOf("wait", "Waits until aborted", async (_, _, callContext) =>
            {
                await HarnessTests.Aborted(callContext.CancellationToken);
                throw new InvalidOperationException("unreachable");
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("ghost", JsonObjectValue.Empty, "c1"), FauxToolCall("wait", JsonObjectValue.Empty, "c2")],
                stopReason: "toolUse"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var listening = await Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await HarnessTests.Eventually(() => listening.Events().Any(item => item is ToolExecutionStartEvent));
            // Aborting the generation aborts its round: the tool ends with its aborted result first (spec §8.5).
            await harness.AbortTaskAsync(await RunTask(harness, root), Context);
            await submission.WaitAsync(Context);
            await ViewTests.Drained(harness);
            // The call not offered ends after its calling message and directly before its result message.
            var round = listening.Events().Select(item => item switch
            {
                MessageEndEvent end => $"end:{end.Entry.Kind}",
                MessageStartEvent start => $"start:{Role(start.Message)}",
                _ => item.Type,
            }).ToList();
            var ghostEnd = round.IndexOf("tool_execution_end");
            Seq(["end:pi.assistant", "tool_execution_end", "start:toolResult", "end:pi.tool-result"], round.Skip(ghostEnd - 1).Take(4), "ghost end");
            var tool = listening.Events().Where(item => item.Type.StartsWith("tool_execution", StringComparison.Ordinal)).Select(item => item switch
            {
                ToolExecutionEndEvent end => $"{end.Type}:{end.ToolCallId}:{end.Entry is not null}",
                ToolExecutionStartEvent start => $"{start.Type}:{start.ToolCallId}:False",
                _ => item.Type,
            });
            Seq(["tool_execution_end:c1:True", "tool_execution_start:c2:False", "tool_execution_end:c2:True"], tool, "tool events");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-an-attachment-cancelled-while-it-waits-for-the-session-line", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var held = ViewTests.Hold(root);
            var controller = Context.WithCancel();
            var attaching = harness.WatchEventsAsync(root.Id, controller.Context);
            controller.Cancel(new Exception("cancelled"));
            held.Release();
            await held.Blocking;
            await RejectsAsync(() => attaching, "cancelled", "cancelled attachment");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "applies-deltas-after-an-overflow-snapshot-that-holds-an-in-flight-partial", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var partial = FauxAssistantMessage([FauxText("hel")]);
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            await root.CommitAsync(async tx => (await tx.DocAsync(Live.LiveDoc, root.Id)).Set("generation", Obj(("attempt", 1), ("message", partial))), Context);
            for (var index = 0; index < 101; index++) await ViewTests.Note(root, "note");
            var batches = new SyncList<List<AgentEvent>>();
            stream.Start((events, _) =>
            {
                batches.Add([.. events]);
                return Task.CompletedTask;
            });
            await ViewTests.Drained(harness);
            var snapshot = (SnapshotEvent)batches[0][0];
            await root.CommitAsync(async tx =>
            {
                var block = (await tx.DocAsync(Live.LiveDoc, root.Id)).GetObject("generation").GetObject("message").GetArray("content").GetObject(0);
                block.Set("text", S(block.Get("text")) + "lo");
            }, Context);
            await ViewTests.Drained(harness);
            var update = batches.Snapshot()[^1][0] as MessageUpdateEvent ?? throw new InvalidOperationException($"Unexpected {batches.Snapshot()[^1][0].Type}");
            Equal("""[{"type":"text_delta","contentIndex":0,"delta":"lo"}]""", JsonValue.Array(update.Changes.Select(change => (JsonValue)change.ToJson())).ToJson(),
                "changes");
            var rebuilt = ApplyChanges((JsonObjectValue)snapshot.Generation!["message"], update.Changes);
            SameJson("""[{"type":"text","text":"hello"}]""", rebuilt["content"], "rebuilt");
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-usage-only-updates-cleared-tool-progress-tools-ending-without-entries-and-deferred-polls", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var listening = await Listen(harness, root);
            Task Change(Action<PiSharp.Durable.Chord.Delta.DraftObject> edit) =>
                root.CommitAsync(async tx => edit(await tx.DocAsync(Live.LiveDoc, root.Id)), Context);
            var partial = FauxAssistantMessage([FauxText("partial")]);
            await Change(live => live.Set("generation", Obj(("attempt", 1), ("message", partial))));
            // A usage-only change sends the usage and no changes.
            await Change(live => live.GetObject("generation").GetObject("message").GetObject("usage").Set("input", 42));
            await Change(live => live.Set("generation", Obj(("attempt", 1), ("deferred", Obj(("pollAt", 1))))));
            await Change(live => live.GetObject("generation").GetObject("deferred").Set("pollAt", 2));
            await Change(live => live.Set("tools", JsonValue.Array(Obj(("callId", "c1"), ("name", "t"), ("status", "running"), ("details", Obj(("n", 1))),
                ("diagnostics", JsonArrayValue.Empty)))));
            // A safe replay clears the running slot's progress.
            await Change(live =>
            {
                live.GetArray("tools").GetObject(0).Delete("details");
                live.GetArray("tools").GetObject(0).Delete("diagnostics");
            });
            // A fault marks the slot done without an entry.
            await Change(live => live.GetArray("tools").GetObject(0).Set("status", "done"));
            await root.CommitAsync(tx => tx.RetireDocAsync(UsageLedger.UsageDoc, root.Id), Context);
            await root.CommitAsync(tx => tx.RetireDocAsync(Agents.AgentDoc, root.Id), Context);
            await ViewTests.Drained(harness);
            var usage = ((JsonObjectValue)partial["usage"]).SetItem("input", 42);
            var expected = JsonValue.Array(
                JsonValue.Array([Obj(("type", "message_start"), ("message", partial))]),
                JsonValue.Array([Obj(("type", "message_update"), ("usage", usage), ("changes", JsonArrayValue.Empty))]),
                J("""[{"type":"deferred_poll","pollAt":1}]"""),
                J("""[{"type":"deferred_poll","pollAt":2}]"""),
                J("""[{"type":"tool_execution_start","toolCallId":"c1","toolName":"t","args":{}}]"""),
                J("""[{"type":"tool_execution_update","toolCallId":"c1","toolName":"t","details":null,"diagnostics":[]}]"""),
                J("""[{"type":"tool_execution_end","toolCallId":"c1","toolName":"t"}]"""),
                // Retired documents read as their initial values.
                J("""[{"type":"usage_changed","usage":{"models":{},"tools":{}}}]"""),
                J("""[{"type":"agent_changed","agent":{}}]"""));
            var actual = JsonValue.Array(listening.Batches.Snapshot().Select(batch =>
                (JsonValue)JsonValue.Array(batch.Where(item => item is not TaskFailedEvent).Select(item => (JsonValue)item.ToJson()))));
            SameJson(expected.ToJson(), actual, "batches");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-stream-with-the-harness", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            await harness.CloseAsync(Context);
            Equal(WatchEnd.Ended, await stream.Closed, "stream end");
        });

        yield return Case(prefix + "starts-a-message-at-the-first-committed-partial-and-ends-it-with-the-converted-entry-on-abort", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 100, MinTokenSize = 1, MaxTokenSize = 1 });
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText(new string('x', 400))]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var listening = await Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await HarnessTests.Eventually(() => listening.Events().Any(item => item is MessageStartEvent start && Role(start.Message) == "assistant"));
            await harness.AbortTaskAsync(await RunTask(harness, root), Context);
            await submission.WaitAsync(Context);
            await ViewTests.Drained(harness);
            Equal(1, listening.Events().Count(item => item is MessageEndEvent { Entry.Kind: "pi.assistant" }), "assistant ends");
            Equal(1, listening.Events().Count(item => item is MessageStartEvent start && Role(start.Message) == "assistant"), "assistant starts");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>The case that calls <c>root.reset()</c>, parameterized by the reset.</summary>
    internal static IEnumerable<(string, Func<Task>)> ResetCases(string prefix, Func<Task<IStorage>> fresh, Func<IConversation, Task> reset)
    {
        yield return Case(prefix + "reports-a-steer-without-run-events-a-reset-as-an-appended-entry-and-nothing-for-other-conversations", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HarnessTests.AddTool(setup.Registry, ToolOf("hold", "Waits", async (_, _, _) =>
            {
                await gate.Task;
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var other = await harness.CreateConversationAsync(Ownerless, Context);
            var listening = await Listen(harness, root);
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            await HarnessTests.Eventually(() => listening.Events().Any(item => item is ToolExecutionStartEvent));
            await root.SubmitAsync(SubmissionDraft.Input("s", WhenBusy.Steer), Context);
            await ViewTests.Drained(harness);
            var before = listening.Batches.Count;
            await ViewTests.Note(other, "note");
            await ViewTests.Drained(harness);
            Equal(before, listening.Batches.Count, "batches after another conversation's commit");
            gate.Resolve();
            await input.WaitAsync(Context);
            await reset(root);
            await ViewTests.Drained(harness);
            Equal(1, listening.Events().Count(item => item is RunStartEvent), "run starts");
            Equal(1, listening.Events().Count(item => item is RunEndEvent), "run ends");
            Seq(["entry_appended", "submission"], listening.Batches.Snapshot()[^1].Select(item => item.Type), "reset batch");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });
    }
}

/// <summary>The agent-events.tool-rounds.* and agent-events.tool-rounds-and-events.* cases: harness-structured.test.ts's v1.2.0 tests that
/// listen to agent events.</summary>
internal static partial class StructuredTests
{
    /// <summary>The structured tests that listen to agent events, on storages from <paramref name="fresh"/>.</summary>
    internal static IEnumerable<(string, Func<Task>)> EventCases(string prefix, Func<Task<IStorage>> fresh) =>
        [.. EventToolRoundTurns(prefix + "tool-rounds.", fresh), .. ToolRoundsAndEvents(prefix + "tool-rounds-and-events.", fresh)];

    /// <summary>Attaches and starts an event stream that records every event (upstream <c>listen</c> of "tool rounds and events").</summary>
    private static async Task<(IAgentEventStream Stream, SyncList<AgentEvent> Events)> ListenEvents(IHarness harness, IConversation conversation)
    {
        var stream = await harness.WatchEventsAsync(conversation.Id, Context);
        var events = new SyncList<AgentEvent>();
        stream.Start((batch, _) =>
        {
            foreach (var item in batch) events.Add(item);
            return Task.CompletedTask;
        });
        return (stream, events);
    }

    /// <summary>Event types, with tool ends and message ends labelled by call ID and whether an entry came along (upstream <c>labels</c>).</summary>
    private static List<string> Labels(IEnumerable<AgentEvent> events) => [.. events.SelectMany(item => item switch
    {
        ToolExecutionEndEvent end => [$"end:{end.ToolCallId}:{(end.Entry is not null ? "true" : "false")}"],
        MessageEndEvent message => message.Entry.Model is { Count: > 0 } model && model[0] is JsonObjectValue first
            ? [S(first["role"]) == "toolResult" ? $"result:{S(first["toolCallId"])}" : $"message:{S(first["role"])}"]
            : ["message:undefined"],
        _ => Array.Empty<string>(),
    })];

    private static IEnumerable<(string, Func<Task>)> EventToolRoundTurns(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "ends-a-turn-at-the-generations-hold-before-its-successors-turn-starts", async () =>
        {
            var nodes = new Nodes();
            var setup = Setup();
            AddTask(setup.Registry, nodes.Node);
            AddTool(setup.Registry, Noop());
            // An extension's hook starts work owned by the generation, which holds it while the next turn runs.
            IHarness? opened = null;
            HookedAfterTools(setup, nodes, () => opened!);
            setup.Faux.SetResponses(ToolCalls(("noop", "c1")), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            opened = harness;
            var (stream, events) = await ListenEvents(harness, root);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var first = await FirstGeneration(harness);
            Equal(DurableTaskStatus.Completing, await StatusOf(harness, first), "first generation");
            List<string> Turns() => [.. events.Snapshot().Where(item => item.Type.StartsWith("turn_", StringComparison.Ordinal)).Select(item => item.Type)];
            await Eventually(() => Turns().Count == 4);
            Seq(["turn_start", "turn_end", "turn_start", "turn_end"], Turns(), "turns");
            nodes.Open("hooked");
            Equal("completed", await OutcomeOf(harness, first), "first outcome");
            await ViewTests.Drained(harness);
            Equal(4, Turns().Count, "turns after the hold");
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });
    }

    private static IEnumerable<(string, Func<Task>)> ToolRoundsAndEvents(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "ends-an-aborted-sequential-rounds-unstarted-calls-with-their-result-entries-right-before-them", async () =>
        {
            var setup = Setup();
            var one = BlockingRoundTool("one");
            AddTool(setup.Registry, one.Registration);
            AddTool(setup.Registry, BlockingRoundTool("two").Registration with { Name = "two" });
            setup.Faux.SetResponses(ToolCalls(("one", "c1"), ("two", "c2"), ("two", "c3")));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var (stream, events) = await ListenEvents(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await one.Started;
            var generation = new TaskId((long)N(((JsonObjectValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["run"])["taskId"]));
            await harness.AbortTaskAsync(generation, Context);
            await submission.WaitAsync(Context);
            await Eventually(() => Labels(events.Snapshot()).Contains("result:c3"));
            Seq(["end:c1:true", "result:c1", "end:c2:true", "result:c2", "end:c3:true", "result:c3"],
                Labels(events.Snapshot()).Where(label => !label.StartsWith("message:", StringComparison.Ordinal)), "labels");
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "emits-one-turn-end-per-generation-also-for-a-stream-attached-while-it-holds", async () =>
        {
            var nodes = new Nodes();
            var setup = Setup();
            AddTask(setup.Registry, nodes.Node);
            AddTool(setup.Registry, Noop());
            IHarness? opened = null;
            HookedAfterTools(setup, nodes, () => opened!);
            setup.Faux.SetResponses(ToolCalls(("noop", "c1")), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            opened = harness;
            var early = await ListenEvents(harness, root);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var late = await ListenEvents(harness, root);
            nodes.Open("hooked");
            var first = await FirstGeneration(harness);
            await harness.WaitForTaskAsync(first, Context);
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            await Eventually(() => late.Events.Snapshot().Any(item => item is EntryAppendedEvent));
            static int TurnEnds(SyncList<AgentEvent> events) => events.Snapshot().Count(item => item is TurnEndEvent);
            Equal(2, TurnEnds(early.Events), "early turn ends");
            Equal(0, TurnEnds(late.Events), "late turn ends");
            await early.Stream.StopAsync();
            await late.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-a-tool-that-owns-live-work-finish-its-call-at-the-hold-while-the-generation-waits-for-its-final-commit", async () =>
        {
            var nodes = new Nodes();
            var setup = Setup();
            AddTask(setup.Registry, nodes.Node);
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "delegate",
                Description = "Starts work in a conversation it owns",
                Execute = async (_, api, callContext) =>
                {
                    await api.CommitAsync(async tx =>
                    {
                        var child = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                        await tx.CreateTaskAsync(nodes.Node, NodeInput("sub"), In(child.Id));
                    }, callContext);
                    return new ToolExecutionResult { Output = JsonValue.Array(Obj(("type", "text"), ("text", "started"))) };
                },
            }));
            setup.Faux.SetResponses(ToolCalls(("delegate", "c1")), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var (stream, events) = await ListenEvents(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            async Task<JsonObjectValue?> FirstSlot() =>
                await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context) is { } live && live.TryGetValue("tools", out var tools) &&
                tools is JsonArrayValue { Count: > 0 } slots ? (JsonObjectValue)slots[0] : null;
            await Eventually(async () => await FirstSlot() is { } slot && slot["status"] is JsonStringValue { Value: "done" });
            var live = (await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!;
            var slot = (JsonObjectValue)((JsonArrayValue)live["tools"])[0];
            var tool = new TaskId((long)N(slot["taskId"]));
            Check(slot.ContainsKey("entry"), "the slot has its entry");
            Equal(DurableTaskStatus.Completing, await StatusOf(harness, tool), "tool status");
            Equal(DurableTaskStatus.Waiting, await StatusOf(harness, new TaskId((long)N(((JsonObjectValue)live["run"])["taskId"]))), "generation status");
            await Eventually(() => Labels(events.Snapshot()).Contains("end:c1:true"));
            Check(!await Settled(submission.WaitAsync(Context)), "the submission waits");
            nodes.Open("sub");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Equal("completed", await OutcomeOf(harness, tool), "tool outcome");
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "holds-a-faulted-tools-slot-and-task-failed-until-the-work-it-owns-drained", async () =>
        {
            var nodes = new Nodes();
            var setup = Setup();
            AddTask(setup.Registry, nodes.Node);
            nodes.Script("held", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.held").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "broken",
                Description = "Starts owned work, then returns a result that is not strict JSON",
                Execute = async (_, api, callContext) =>
                {
                    await api.CommitAsync(tx => tx.CreateTaskAsync(nodes.Node, NodeInput("held"), Owned(api.TaskId)), callContext);
                    return new ToolExecutionResult { Output = JsonArrayValue.Empty, Control = new ToolControl { AddTools = [null!] } };
                },
            }));
            setup.Faux.SetResponses(ToolCalls(("broken", "c1")), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var (stream, events) = await ListenEvents(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Eventually(() => nodes.Logged("abort:held"));
            var live = (await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!;
            var slot = (JsonObjectValue)((JsonArrayValue)live["tools"])[0];
            var tool = new TaskId((long)N(slot["taskId"]));
            MatchJson("""{"status":"completing","outcome":{"status":"faulted"}}""", (await State(harness, tool)).ToJson(), "tool state");
            Check(slot["status"] is not JsonStringValue { Value: "done" }, "the slot is not done");
            Check(!events.Snapshot().Any(item => item is TaskFailedEvent), "no task_failed yet");
            nodes.Open("abort.held");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Equal("faulted", await OutcomeOf(harness, tool), "tool outcome");
            await Eventually(() => events.Snapshot().Any(item => item is TaskFailedEvent));
            Check(Labels(events.Snapshot()).Contains("end:c1:false"), "the tool ends without an entry: " + string.Join(", ", Labels(events.Snapshot())));
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });
    }
}
