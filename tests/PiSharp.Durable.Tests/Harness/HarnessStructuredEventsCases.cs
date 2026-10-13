// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-structured.test.ts, the tests that listen to agent events
// (watchEvents, P10): "ends a turn at the generation's hold, before its successor's turn starts" of describe "tool rounds", and describe
// "tool rounds and events". Adaptations beyond those of HarnessStructuredSupport.cs, HarnessStructuredToolRoundsCases.cs and
// HarnessEventsCases.cs:
// - "ends a turn at the generation's hold ...": upstream's last `waitFor(() => turns().length === 4)` after the held generation finishes holds
//   at once; here a Session line barrier lets every batch the final commits produced arrive first, and then the count must still be 4.
// - "holds a faulted tool's slot and task_failed ...": upstream's "broken" tool returns a function in `details`, which is not strict JSON; the
//   result here carries a null tool name in `control.addTools`, which makes the same result commit throw (see HarnessToolsRecoveryCases.cs).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class StructuredTests
{
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

    private static ToolRegistration Noop() => Define.Tool(new ToolRegistration
    {
        Name = "noop",
        Description = "Does nothing",
        Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Content = JsonArrayValue.Empty }),
    });

    /// <summary>Installs the hook that starts work owned by the generation after its tools, which the generation then holds.</summary>
    private static void HookedAfterTools(ChatSetup setup, Nodes nodes, Func<IHarness> opened) =>
        AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
        {
            AfterTools = async (_, _, api, callContext) =>
                await opened().CommitAsync(tx => tx.CreateTaskAsync(nodes.Node, NodeInput("hooked"), Owned(api.TaskId)), callContext),
        });

    private static async Task<TaskId> FirstGeneration(IHarness harness) =>
        (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.generation" }, 1), Context)).Items[0].Id;

    private static IEnumerable<(string, Func<Task>)> ToolRoundTurns(string prefix, Func<Task<IStorage>> fresh)
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
                    return new ToolExecutionResult { Content = JsonValue.Array(Obj(("type", "text"), ("text", "started"))) };
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
                    return new ToolExecutionResult { Content = JsonArrayValue.Empty, Control = new ToolControl { AddTools = [null!] } };
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
