// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-structured.test.ts, describe "tool rounds": the three
// tests the tool task (P9) unblocks, "owns its tool tasks, waits for them, and hands the run to a conversation-owned generation", "aborts a
// parallel round with its generation: tools first, then the generation, with every result written" and "answers the unstarted calls of an
// aborted sequential round with aborted results, in call order", and the one generation alone (P8) unblocked, "keeps run control with a
// faulted generation until its owned work drains, and retries a rejected final commit", which Pi v1.2.0 turned into "... and fails the
// Harness on a failed final commit" (area b of the sync). "ends a turn at the generation's hold, before its
// successor's turn starts" and the "tool rounds and events" tests listen to agent events and are in HarnessStructuredEventsCases.cs.
// Adaptations beyond those of HarnessStructuredSupport.cs and ChatSupport.cs: invalidFinalStream is GenerationTests.InvalidFinalStream (a
// non-finite usage count instead of a function, see HarnessGenerationCases.cs); upstream's module-level blockingTool and toolCalls are
// BlockingRoundTool and ToolCalls below; `Promise.all([one.started, two.started])` is Task.WhenAll; "aborts a parallel round ..." orders the
// two tools' aborts with a barrier (see there).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class StructuredTests
{
    /// <summary>A sequential tool that writes "partial" and blocks until its invocation is cancelled (upstream <c>blockingTool</c>).</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="started">Receives the tool task's ID when the call starts.</param>
    /// <param name="aborting">Awaited once the call is aborted, before it rejects; a test-only ordering barrier.</param>
    private static (Task Started, ToolRegistration Registration) BlockingRoundTool(string name, Action<TaskId>? started = null,
        Func<Task>? aborting = null)
    {
        var reached = new HarnessTests.Deferred();
        return (reached.Task, Define.Tool(new ToolRegistration
        {
            Name = name,
            Description = $"The {name} tool",
            ExecutionMode = ToolExecutionMode.Sequential,
            Execute = async (_, api, callContext) =>
            {
                api.Output("partial");
                started?.Invoke(api.TaskId);
                reached.Resolve();
                try
                {
                    await Aborted(callContext.CancellationToken);
                }
                catch (OperationCanceledException) when (aborting is not null)
                {
                    await aborting();
                    throw;
                }
                throw new InvalidOperationException("unreachable");
            },
        }));
    }

    /// <summary>A tool-calling answer with one call per <c>(name, id)</c> (upstream <c>toolCalls</c>).</summary>
    private static JsonObjectValue ToolCalls(params (string Name, string Id)[] ids) =>
        FauxAssistantMessage(ids.Select(call => FauxToolCall(call.Name, JsonObjectValue.Empty, call.Id)), stopReason: "toolUse");

    private static IEnumerable<(string, Func<Task>)> ToolRounds(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "owns-its-tool-tasks-waits-for-them-and-hands-the-run-to-a-conversation-owned-generation", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "noop",
                Description = "Does nothing",
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty }),
            }));
            setup.Faux.SetResponses(ToolCalls(("noop", "c1"), ("noop", "c2")), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "status");
            var tasks = (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 20), Context)).Items;
            var generations = tasks.Where(task => task.Kind == "pi.generation").ToList();
            var (first, second) = (generations[0], generations[1]);
            Seq([first.Id, first.Id], tasks.Where(task => task.Kind == "pi.tool").Select(task => task.Owner!.Value), "tool owners");
            Check(first.Owner is null, "first generation is conversation-owned");
            Check(second.Owner is null, "second generation is conversation-owned");
            var assistant = (await AllEntries(root)).First(entry => entry.Kind == "pi.assistant");
            SameJson("{\"status\":\"terminal\",\"outcome\":{\"status\":\"completed\",\"result\":{\"entryId\":" + assistant.Id.Value + "}}}",
                first.State.ToJson(), "first state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-parallel-round-with-its-generation-tools-first-then-the-generation-with-every-result-written", async () =>
        {
            var setup = Setup();
            IHarness? opened = null;
            TaskId? first = null;
            var one = BlockingRoundTool("one", started: id => first = id);
            // Barrier: upstream's abort signal runs both tools' listeners in registration order and every later step of their abort follows in
            // microtask FIFO order, so c1's result is appended before c2's. Here the two invocations end and abort on the thread pool, so tool
            // two lets its abort through only once tool one's task is terminal, with its result written.
            var two = BlockingRoundTool("two", aborting: () => opened!.WaitForTaskAsync(first!.Value, Context));
            AddTool(setup.Registry, one.Registration with { ExecutionMode = ToolExecutionMode.Parallel });
            AddTool(setup.Registry, two.Registration with { ExecutionMode = ToolExecutionMode.Parallel });
            setup.Faux.SetResponses(ToolCalls(("one", "c1"), ("two", "c2")));
            var (harness, root) = await OpenChat(await fresh(), setup);
            opened = harness;
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Task.WhenAll(one.Started, two.Started);
            var generation = new TaskId((long)N(((JsonObjectValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["run"])["taskId"]));
            await harness.AbortTaskAsync(generation, Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
            var results = (await AllEntries(root)).Where(entry => entry.Kind == "pi.tool-result").ToList();
            Seq(["c1", "c2"], results.Select(entry => S(((JsonObjectValue)entry.Model![0])["toolCallId"])), "results");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            Equal(TaskOutcomeStatus.Aborted, ((SettlingTaskState)(await harness.WaitForTaskAsync(generation, Context)).State).Outcome.Status, "generation");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-the-unstarted-calls-of-an-aborted-sequential-round-with-aborted-results-in-call-order", async () =>
        {
            var setup = Setup();
            var one = BlockingRoundTool("one");
            AddTool(setup.Registry, one.Registration);
            AddTool(setup.Registry, BlockingRoundTool("two").Registration with { Name = "two" });
            setup.Faux.SetResponses(ToolCalls(("one", "c1"), ("two", "c2"), ("two", "c3")));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await one.Started;
            var slots = (JsonArrayValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["tools"];
            Seq([true, false, false], slots.Select(slot => ((JsonObjectValue)slot).ContainsKey("taskId")), "slots with a task");
            var generation = new TaskId((long)N(((JsonObjectValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["run"])["taskId"]));
            await harness.AbortTaskAsync(generation, Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
            var results = (await AllEntries(root)).Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0]).ToList();
            Seq(["c1", "c2", "c3"], results.Select(result => S(result["toolCallId"])), "call ids");
            Seq([true, true, true], results.Select(result => result["isError"] is JsonBooleanValue { Value: true }), "isError");
            SameJson("[{\"type\":\"text\",\"text\":\"<harness>\\n[error] Tool two was aborted\\n</harness>\"}]", results[1]["content"], "unstarted content");
            var tools = (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id, Kind = "pi.tool" }, 20), Context)).Items;
            Equal(1, tools.Count(), "tool tasks");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-run-control-with-a-faulted-generation-until-its-owned-work-drains-and-fails-the-harness-on-a-failed-final-commit", async () =>
        {
            var nodes = new Nodes();
            var setupBase = Setup();
            var setup = setupBase.WithModels(new ModelsOverride(setupBase.Models) { StreamSimpleOverride = (_, _, _, token) => GenerationTests.InvalidFinalStream(token) });
            AddTask(setup.Registry, nodes.Node);
            IHarness? opened = null;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                BeforeRequest = async (_, api, callContext) =>
                {
                    await opened!.CommitAsync(tx => tx.CreateTaskAsync(nodes.Node, NodeInput("hooked"), Owned(api.TaskId)), callContext);
                    return null;
                },
            });
            nodes.Script("hooked", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.hooked").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            var controlled = new ControlledStorage(await fresh()) { KeepOpenForReopen = true };
            var storage = new FinalizeFailingStorage(controlled);
            var (harness, root) = await OpenChat(storage, setup);
            opened = harness;
            async Task<TaskId?> RunTask() =>
                await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context) is { } live && live.TryGetValue("run", out var run)
                    ? new TaskId((long)N(((JsonObjectValue)run)["taskId"])) : null;
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await Eventually(async () => await RunTask() is { } id && (await State(harness, id)).Status == DurableTaskStatus.Completing);
            var generation = (await RunTask())!.Value;
            MatchJson("""{"status":"completing","outcome":{"status":"faulted"}}""", (await State(harness, generation)).ToJson(), "generation state");
            Check(!await Settled(submission.WaitAsync(Context)), "the submission waits");
            Equal(generation, await RunTask(), "run control");
            // The faulted outcome is cancellation intent: the hooked work is aborted, then the run settles.
            await Eventually(() => nodes.Logged("abort:hooked"));
            storage.Fail = generation;
            nodes.Open("abort.hooked");
            // The final commit, with the run's cleanup, fails: nothing of the cleanup lands, and the Harness fails.
            Equal(new SessionEnd(SessionEndReason.Failed, storage.Failure), await harness.Closed, "closed");
            Same(storage.Failure, (await ThrowsAsync<SessionFailedException>(() => submission.WaitAsync(Context), "wait")).InnerException, "cause");
            await harness.CloseAsync(Context);
            // Reopening finalizes the run; the partial becomes one aborted entry.
            controlled.Reopen();
            var (reopened, reopenedRoot) = await OpenChat(storage, setup);
            opened = reopened;
            var again = (await reopened.SubmissionAsync(submission.Id, Context))!;
            MatchJson("""{"status":"unanswered","reason":"faulted"}""", (await again.WaitAsync(Context)).ToJson(), "settled");
            SameJson("{}", await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Context), "live");
            Seq(["pi.user", "pi.assistant"], await Kinds(reopenedRoot), "kinds");
            await reopened.CloseAsync(Context);
            await controlled.Inner.CloseAsync(Context);
        });
    }
}
