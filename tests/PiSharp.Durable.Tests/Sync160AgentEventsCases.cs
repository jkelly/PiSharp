// PiSharp-only (no upstream Pi source): what PiSharp.Durable.Hosting's agent events (AgentEvents.cs, v1.2.0's events kept for the device
// wire after Pi v1.5.0 removed them) show of the Harness states Pi v1.6.0 adds, as sync160.agent-events.<case>: a tool that parks and its
// wake are tool_execution_update events with `status`, never a second tool_execution_start; a call parked at its gates before it ran
// starts `sleeping` and gets its arguments with its wake; a stream attached while a call is parked sees the slot in its snapshot and an
// update at the wake; a run whose answer waits for its onYield tasks stays the run on the wire and says so with run_yield. The cases run
// here, beside AgentEventsCases.cs, for this suite's harness support (see that file's header).
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160AgentEventsCases() => Sync160AgentEventTests.Cases();
}

/// <summary>The sync160.agent-events.* cases.</summary>
internal static class Sync160AgentEventTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly SignalToken Done = Signals.DefineSignal(new SignalDefinition
    {
        Name = "test.events.done",
        Version = 1,
        Schema = O("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}"""),
    });

    private static readonly JsonObjectValue TopicParameters =
        O("""{"type":"object","properties":{"topic":{"type":"string"}},"required":["topic"],"additionalProperties":false}""");

    private static JsonObjectValue Call(string name, string id, string topic = "x") =>
        FauxAssistantMessage([FauxToolCall(name, Obj(("topic", topic)), id)], stopReason: "toolUse");

    /// <summary>A replay-safe tool that prints, then parks on <c>Done</c> under <c>done</c>, and answers with the value once woken.</summary>
    private static ToolRegistration Parking(string name = "park") => Define.Tool(new ToolRegistration
    {
        Name = name,
        Description = "Parks until it is told",
        Parameters = TopicParameters,
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, ctx) =>
        {
            var state = await api.ReadSignalAsync(Done, "done", ctx);
            if (state?.Value is JsonObjectValue value)
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"{S(((JsonObjectValue)arguments)["topic"])}: {S(value["text"])}")) };
            api.Output("working\n");
            await api.DetailsAsync(Obj(("step", 1)), ctx);
            return ToolPark.Sleeping(signals: [Done.Key("done")]);
        },
    });

    private static async Task<TaskId> ParkedTask(IHarness harness, IConversation root, string status = "sleeping", string list = "tools")
    {
        JsonObjectValue? slot = null;
        await Eventually(async () =>
        {
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
            slot = live is not null && live.TryGetValue(list, out var slots) && slots is JsonArrayValue { Count: > 0 } found
                ? found.OfType<JsonObjectValue>().FirstOrDefault(candidate => S(candidate["status"]) == status) : null;
            return slot is not null;
        });
        await Eventually(() => !harness.Wake().Active);
        return new TaskId((long)((JsonNumberValue)slot!["taskId"]).Value);
    }

    private static List<string> ToolEvents(IEnumerable<AgentEvent> events, string? callId = null) =>
        [.. events.OfType<ToolEvent>().Where(item => callId is null || item.ToolCallId == callId).Select(item => item.ToJson().ToJson())];

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "sync160.agent-events.";

        yield return Case(prefix + "a-tool-that-parks-has-one-start-and-its-park-and-wake-are-updates-with-status", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Parking());
            setup.Faux.SetResponses(Call("park", "c1"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var listening = await EventTests.Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var task = await ParkedTask(harness, root);
            await ViewTests.Drained(harness);
            var parked = listening.Events().OfType<ToolEvent>().ToList();
            Equal(1, parked.Count(item => item is ToolExecutionStartEvent), "starts while parked");
            MatchExactly("""{"type":"tool_execution_start","toolCallId":"c1","toolName":"park","taskId":"<number>","args":{"topic":"x"}}""",
                parked[0].ToJson(), "start");
            // The park: the slot shows no output while parked, so the window the call filled is cleared, and the status says why nothing runs.
            var park = parked.OfType<ToolExecutionUpdateEvent>().Last();
            Equal("sleeping", park.Status, "the park's status");
            SameJson("""{"set":""}""", park.Output?.ToJson(), "the park clears the output window");
            Check(park.Args is null, "a park carries no arguments");
            Equal("""["type","toolCallId","toolName","taskId","output","status"]""",
                JsonValue.Array(park.ToJson().Keys.Select(key => (JsonValue)key)).ToJson(), "the park's keys");
            // Nothing follows a park until the wake: a parked call holds no invocation.
            var before = listening.Events().Count;
            Equal(SignalResult.Delivered, await harness.SignalAsync(task, Done, "done", Obj(("text", "told")), Context), "delivery");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            await ViewTests.Drained(harness);
            var all = listening.Events();
            var after = all.Skip(before).OfType<ToolEvent>().ToList();
            Equal(0, after.Count(item => item is ToolExecutionStartEvent), "starts after the wake");
            MatchExactly("""{"type":"tool_execution_update","toolCallId":"c1","toolName":"park","taskId":"<number>","status":"running","args":{"topic":"x"}}""",
                after[0].ToJson(), "the wake");
            Check(after[^1] is ToolExecutionEndEvent { Entry: not null }, "the call ends with its result entry");
            Equal(1, all.OfType<ToolExecutionStartEvent>().Count(), "one start in all");
            Equal(1, all.OfType<ToolExecutionEndEvent>().Count(), "one end in all");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "a-call-parked-at-its-gates-starts-sleeping-and-gets-its-arguments-with-its-wake", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "run",
                Description = "Runs once approved",
                Parameters = TopicParameters,
                Execute = (arguments, _, _) =>
                    Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText("ran " + S(((JsonObjectValue)arguments)["topic"]))) }),
            }));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "gate",
                Hooks =
                [
                    Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                    {
                        BeforeTool = async (call, api, ctx) => await api.ReadSignalAsync(Done, "ok", ctx) is null
                            ? new BeforeToolDecision(Sleep: new GateSleep(Signals: ImmutableArray.Create(Done.Key("ok"))))
                            // The gate replaces the arguments on the pass that lets the call run.
                            : new BeforeToolDecision(Arguments: Obj(("topic", "approved"))),
                    }),
                ],
            }));
            setup.Faux.SetResponses(Call("run", "c1", "asked"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var listening = await EventTests.Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var task = await ParkedTask(harness, root);
            await ViewTests.Drained(harness);
            // Parked before it ran: the call is live, so it has its start, which says it sleeps; it has no intent, so no arguments yet.
            SameJson(JsonValue.Array($$$"""{"type":"tool_execution_start","toolCallId":"c1","toolName":"run","taskId":{{{task.Value}}},"args":{},"status":"sleeping"}""")
                .ToJson(), JsonValue.Array(ToolEvents(listening.Events()).Select(text => (JsonValue)text)), "events while gated");
            await harness.SignalAsync(task, Done, "ok", Obj(("text", "yes")), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            await ViewTests.Drained(harness);
            var tool = listening.Events().OfType<ToolEvent>().ToList();
            Equal(1, tool.Count(item => item is ToolExecutionStartEvent), "one start");
            // The wake carries the arguments the call runs with: the gate's replacement.
            SameJson($$$"""{"type":"tool_execution_update","toolCallId":"c1","toolName":"run","taskId":{{{task.Value}}},"status":"running","args":{"topic":"approved"}}""",
                tool[1].ToJson(), "the wake");
            Check(tool[^1] is ToolExecutionEndEvent, "end");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "a-stream-attached-while-a-call-is-parked-sees-the-slot-in-its-snapshot-and-an-update-at-the-wake", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Parking());
            setup.Faux.SetResponses(Call("park", "c1"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var task = await ParkedTask(harness, root);
            var listening = await EventTests.Listen(harness, root);
            var slot = (JsonObjectValue)listening.Stream.Snapshot.Tools[0];
            Equal("sleeping", S(slot["status"]), "the snapshot's slot");
            Check(!slot.ContainsKey("output"), "a parked slot shows no output");
            await harness.SignalAsync(task, Done, "done", Obj(("text", "told")), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            await ViewTests.Drained(harness);
            var tool = listening.Events().OfType<ToolEvent>().ToList();
            // The snapshot listed the call: its wake is an update, not a start.
            Equal(0, tool.Count(item => item is ToolExecutionStartEvent), "starts");
            MatchJson("""{"type":"tool_execution_update","toolCallId":"c1","status":"running","args":{"topic":"x"}}""", tool[0].ToJson(), "the wake");
            Check(tool[^1] is ToolExecutionEndEvent, "end");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "nested-calls-that-park-and-the-call-that-waits-on-them-each-have-one-start", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Parking("research"));
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "both",
                Description = "Waits on two nested calls",
                Parameters = TopicParameters,
                Replay = ToolReplay.Safe,
                Execute = async (_, api, ctx) =>
                {
                    var a = await api.StartToolAsync("research", Obj(("topic", "a")), ctx, new NestedCallOptions { Key = "a" });
                    var b = await api.StartToolAsync("research", Obj(("topic", "b")), ctx, new NestedCallOptions { Key = "b" });
                    if (api.Wake is null) return ToolPark.Waiting([a.TaskId, b.TaskId], JoinPolicy.AllSettled);
                    var results = await Task.WhenAll(a.ResultAsync(ctx), b.ResultAsync(ctx));
                    return new ToolExecutionResult { Output = JsonValue.Array(FauxText(string.Join(" + ", results.Select(result => S(result.StructuredOutput))))) };
                },
            }));
            setup.Faux.SetResponses(Call("both", "c1"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var listening = await EventTests.Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await ParkedTask(harness, root, "waiting");
            JsonArrayValue nested = JsonArrayValue.Empty;
            await Eventually(async () =>
            {
                nested = (await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))?["nestedTools"] as JsonArrayValue ?? JsonArrayValue.Empty;
                return nested.Count == 2 && nested.All(slot => S(((JsonObjectValue)slot)["status"]) == "sleeping");
            });
            foreach (var slot in nested.Cast<JsonObjectValue>())
                await harness.SignalAsync(new TaskId((long)((JsonNumberValue)slot["taskId"]).Value), Done, "done", Obj(("text", "told")), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            await ViewTests.Drained(harness);
            var all = listening.Events().OfType<ToolEvent>().ToList();
            foreach (var callId in new[] { "c1", "c1/a", "c1/b" })
            {
                var own = all.Where(item => item.ToolCallId == callId).ToList();
                Equal(1, own.Count(item => item is ToolExecutionStartEvent), callId + ": starts");
                Equal(1, own.Count(item => item is ToolExecutionEndEvent), callId + ": ends");
                Check(own[0] is ToolExecutionStartEvent && own[^1] is ToolExecutionEndEvent, callId + ": start first, end last");
                var statuses = own.OfType<ToolExecutionUpdateEvent>().Where(item => item.Status is not null).Select(item => item.Status).ToList();
                Equal(callId == "c1" ? "waiting,running" : "sleeping,running", string.Join(",", statuses), callId + ": statuses");
            }
            // A nested call's wake carries the arguments its slot shows.
            var woke = all.OfType<ToolExecutionUpdateEvent>().First(item => item.ToolCallId == "c1/a" && item.Status == "running");
            SameJson("""{"topic":"a"}""", woke.Args, "nested wake args");
            Equal("c1", woke.ParentToolCallId, "nested wake parent");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "a-run-whose-answer-waits-for-its-onyield-tasks-stays-the-run-and-says-so-with-run-yield", async () =>
        {
            var releases = new SyncList<TaskCompletionSource>();
            var review = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.events.review",
                Version = 1,
                Initial = _ => O("""{"phase":"review"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["review"] = async (task, runtime, ctx) =>
                    {
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        releases.Add(release);
                        await release.Task.WaitAsync(ctx.CancellationToken);
                        // The first review sends the agent back; the second lets the answer stand.
                        var decision = releases.Snapshot().Count == 1 ? new YieldDecision("Say more.") : new YieldDecision();
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(decision.ToJson()), ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
            var setup = Setup();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "reviewer",
                Tasks = [review],
                Hooks =
                [
                    Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                    {
                        OnYield = (_, _, _) => Task.FromResult<OnYieldResult?>(OnYieldResult.Tasks([Define.HookTask(review, JsonValue.Null)])),
                    }),
                ],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("Yes."), FauxAssistantMessage("Yes, because."));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var listening = await EventTests.Listen(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Eventually(() => releases.Snapshot().Count == 1);
            await ViewTests.Drained(harness);
            // The answer is in the transcript and the run goes on: no run_end, and run_yield names the answer.
            var first = listening.Events();
            Equal(0, first.Count(item => item is RunEndEvent), "no run_end while the answer is reviewed");
            var yielded = first.OfType<RunYieldEvent>().Single();
            var answer = first.OfType<MessageEndEvent>().Last().Entry.Id;
            SameJson($$$"""{"type":"run_yield","inputs":[{{{submission.Id.Value}}}],"answer":{{{answer.Value}}}}""", yielded.ToJson(), "run_yield");
            // A stream attached now reads the same from its snapshot.
            var late = await EventTests.Listen(harness, root);
            SameJson($$$"""{"inputs":[{{{submission.Id.Value}}}],"yield":{"answer":{{{answer.Value}}}}}""", late.Stream.Snapshot.ToJson()["run"], "snapshot run");
            await late.Stream.StopAsync();
            releases.Snapshot()[0].TrySetResult();
            await Eventually(() => releases.Snapshot().Count == 2);
            await ViewTests.Drained(harness);
            // The review continued the run: run_yield without an answer, then the second answer's.
            var yields = listening.Events().OfType<RunYieldEvent>().ToList();
            Equal(3, yields.Count, "run_yield events");
            SameJson($$$"""{"type":"run_yield","inputs":[{{{submission.Id.Value}}}]}""", yields[1].ToJson(), "the continuation");
            Check(yields[2].Answer is { } second && second.Value > answer.Value, "the second answer");
            Equal(0, listening.Events().Count(item => item is RunEndEvent), "still no run_end");
            releases.Snapshot()[1].TrySetResult();
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            await root.WaitForIdleAsync(Context);
            await ViewTests.Drained(harness);
            var all = listening.Events();
            Equal(1, all.Count(item => item is RunStartEvent), "one run_start");
            Equal(1, all.Count(item => item is RunEndEvent), "one run_end");
            Equal(3, all.OfType<RunYieldEvent>().Count(), "the run's end is its run_end, not another run_yield");
            Check(all.FindLastIndex(item => item is RunYieldEvent) < all.FindLastIndex(item => item is RunEndEvent), "run_end last");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "tool-events-without-parks-are-as-before", async () =>
        {
            // The v1.2.0 shapes are unchanged for a call that never parks: no status, no args on updates.
            var setup = Setup();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "print",
                Description = "Prints",
                Parameters = TopicParameters,
                Execute = async (_, api, ctx) =>
                {
                    await api.DetailsAsync(Obj(("step", 1)), ctx);
                    return new ToolExecutionResult { Output = JsonValue.Array(FauxText("printed")) };
                },
            }));
            setup.Faux.SetResponses(Call("print", "c1"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var listening = await EventTests.Listen(harness, root);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await ViewTests.Drained(harness);
            var tool = listening.Events().OfType<ToolEvent>().ToList();
            Equal("""["type","toolCallId","toolName","taskId","args"]""", JsonValue.Array(tool[0].ToJson().Keys.Select(key => (JsonValue)key)).ToJson(), "start keys");
            foreach (var update in tool.OfType<ToolExecutionUpdateEvent>())
                Check(update.Status is null && update.Args is null && !update.ToJson().ContainsKey("status"), "an update of a running call has no status");
            Equal(0, listening.Events().Count(item => item is RunYieldEvent), "no run_yield without onYield tasks");
            Check(!listening.Stream.Snapshot.ToJson().ContainsKey("run"), "the first snapshot has no run");
            await harness.CloseAsync(Context);
        });
    }
}
