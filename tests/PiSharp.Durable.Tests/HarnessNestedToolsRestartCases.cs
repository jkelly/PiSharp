// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-nested-tools-restart.test.ts ("nested tool calls across
// a restart"), as harness.nested-tools-restart.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - `openNodeSqliteStorage(path)` is SqliteStorage.OpenAsync over a file in a fresh temporary directory.
// - The tools' Parameters are the JSON TypeBox prints for `Type.Object({ text: Type.Optional(Type.String()) })` (NestedToolsTests).
// - A beforeTool hook's `aborted(hookContext.abortSignal!)` waits on the hook context's cancellation, which close aborts.
// - A record's `abortReason` toBeUndefined is a null TaskRecord.AbortReason, `abandonOnRestart` toBeUndefined a false one (written only when
//   true).
// Authored, first: the hook that gives api.createTask() children and nested calls of a caller that does not resume abandonOnRestart
// (ToolExecutionApi.ChildTaskOptions), as upstream's `{ abandonOnRestart: true, ...options }` does.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using static ChatTests;
using static HarnessTests;
using static NestedToolsTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessNestedToolsRestartCases() => SchedulerTests.NestedToolsRestartCases();
}

internal static partial class SchedulerTests
{
    private static string RestartPath() => Path.Combine(Temp("nested-restart"), "session.sqlite");

    private static async Task<OpenedChat> OpenRestart(string path, ChatSetup setup) => await OpenChat(await SqliteStorage.OpenAsync(path), setup);

    private static async Task<List<TaskRecord>> NestedTasks(IHarness harness) => [.. (await ToolTasks(harness)).Where(IsNested)];

    /// <summary>Tool end events from now on.</summary>
    private static async Task<(SyncList<ToolExecutionEndEvent> Ends, Func<Task> Stop)> CollectEnds(IHarness harness, ConversationId conversation)
    {
        var ends = new SyncList<ToolExecutionEndEvent>();
        var stream = await harness.WatchEventsAsync(conversation, Context);
        stream.Start((batch, _) =>
        {
            foreach (var item in batch) if (item is ToolExecutionEndEvent end) ends.Add(end);
            return Task.CompletedTask;
        });
        return (ends, async () => await stream.StopAsync());
    }

    private static List<string> CodesOf(NestedToolExecutionResult? result) =>
        [.. (result?.Diagnostics ?? []).Where(diagnostic => diagnostic.Code is not null).Select(diagnostic => diagnostic.Code!)];

    private static string? ReasonOf(TaskRecord? record) => record?.AbortReason is { } reason ? StructuredTests.Wire(reason) : null;

    /// <summary>A task whose one phase records itself and waits until aborted; its abort handler records the reason and ends aborted.</summary>
    private static DurableTask Worker(string name, SyncList<string>? phases, SyncList<string?>? aborts, Deferred started) =>
        Tasks.DefineTask(new TaskDefinition
        {
            Name = name,
            Version = 1,
            Initial = _ => O("""{"phase":"work"}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["work"] = async (_, runtime, _) =>
                {
                    phases?.Add("work");
                    started.Resolve();
                    await Aborted(runtime.Signal);
                },
            },
            Abort = async (task, runtime, abortContext) =>
            {
                aborts?.Add(task.AbortReason is { } reason ? StructuredTests.Wire(reason) : null);
                await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), abortContext);
            },
        });

    /// <summary>A tool that creates <paramref name="worker"/> as a child task it owns and waits for it in memory.</summary>
    private static ToolRegistration Spawn(DurableTask worker, Action<TaskId> created) => Tool("spawn", async (_, api, callContext) =>
    {
        var id = await api.CreateTaskAsync(worker, JsonValue.Null, new TaskOptions(TaskOwnership.Task(api.TaskId)), callContext);
        created(id);
        await api.WaitForTaskAsync(id, callContext);
        return new ToolExecutionResult();
    });

    private static ToolRegistration Batch(params string[] names) => Tool("batch", async (_, api, callContext) =>
    {
        await Task.WhenAll(names.Select(name => api.ExecuteToolAsync(name, JsonObjectValue.Empty, callContext)));
        return new ToolExecutionResult();
    }, tool => tool with { Parameters = ToolRegistration.EmptyParameters });

    internal static IEnumerable<(string, Func<Task>)> NestedToolsRestartCases()
    {
        const string prefix = "harness.nested-tools-restart.";

        yield return Case(prefix + "child-task-options-default-abandon-on-restart-for-callers-that-do-not-resume", () =>
        {
            var child = new TaskOptions(TaskOwnership.Task(new TaskId(7)));
            Equal(true, ToolExecutionApi.ChildTaskOptions(child, resumes: false).AbandonOnRestart, "a child of a caller that does not resume");
            Equal<bool?>(null, ToolExecutionApi.ChildTaskOptions(child, resumes: true).AbandonOnRestart, "a child of a replay-safe caller");
            Equal(false, ToolExecutionApi.ChildTaskOptions(child with { AbandonOnRestart = false }, resumes: false).AbandonOnRestart, "explicit");
            var top = new TaskOptions(TaskOwnership.Conversation);
            Equal<bool?>(null, ToolExecutionApi.ChildTaskOptions(top, resumes: false).AbandonOnRestart, "a conversation-owned task");
        });

        yield return Case(prefix + "abandons-a-replay-unsafe-caller-s-unfinished-nested-calls-before-they-run-again", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var runs = new int[2];
            var running = new Deferred();
            var gated = new Deferred();
            // Replay-safe, so only the abandonment keeps it from rerunning.
            AddTool(setup.Registry, Tool("started", async (_, api, callContext) =>
            {
                Interlocked.Increment(ref runs[0]);
                api.Output("partial\n");
                await api.DetailsAsync(Obj(("step", 1)), callContext);
                running.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }, tool => tool with { Replay = ToolReplay.Safe }));
            AddTool(setup.Registry, Tool("gated", (_, _, _) =>
            {
                Interlocked.Increment(ref runs[1]);
                return Task.FromResult(new ToolExecutionResult());
            }));
            // Holds `gated` in its `call` phase, before intent, until the Harness closes.
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (hookCall, _, hookContext) =>
                {
                    if (S(hookCall["name"]) != "gated") return null;
                    gated.Resolve();
                    await Aborted(hookContext.CancellationToken);
                    return null;
                },
            });
            AddTool(setup.Registry, Batch("started", "gated"));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await Task.WhenAll(running.Task, gated.Task);
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            var (ends, stop) = await CollectEnds(opened.Harness, opened.Root.Id);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            await opened.Harness.WaitForIdleAsync(Context);
            await stop();
            Equal("1 0", $"{runs[0]} {runs[1]}", "runs");
            foreach (var task in await NestedTasks(opened.Harness))
            {
                Check(task.AbandonOnRestart && task.AbortRequested && ReasonOf(task) == "restart", $"task {task.Id}: {task.ToJson().ToJson()}");
                MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", task.State.ToJson(), $"task {task.Id} state");
            }
            NestedToolExecutionResult? End(string callId) => ends.Snapshot().FirstOrDefault(end => end.ToolCallId == callId)?.Result;
            Seq(["interrupted"], CodesOf(End("c1/1")), "started codes");
            MatchJson("""{"isError":true,"details":{"step":1}}""", End("c1/1")!.ToJson(), "started");
            Equal<JsonValue?>(null, End("c1/1")!.StructuredOutput, "no structuredOutput");
            Seq(["abandoned"], CodesOf(End("c1/2")), "gated codes");
            // The caller reports the interruption.
            MatchJson("""{"toolCallId":"c1","isError":true}""", Results(await AllEntries(opened.Root))[0], "result");
            SameJson("{}", await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context), "live");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "abandons-the-work-an-abandoned-nested-call-owns-which-never-runs-a-phase-again", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var phases = new SyncList<string>();
            var aborts = new SyncList<string?>();
            var started = new Deferred();
            var worker = Worker("test.worker", phases, aborts, started);
            setup.Registry.Install(Define.Extension(new Extension { Name = "worker", Tasks = [worker] }));
            TaskId? workerId = null;
            AddTool(setup.Registry, Spawn(worker, id => workerId = id));
            AddTool(setup.Registry, Batch("spawn"));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            await opened.Harness.WaitForIdleAsync(Context);
            Seq(["work"], phases.Snapshot(), "phases");
            // The cascade passed the restart reason on.
            Seq(["restart"], aborts.Snapshot(), "aborts");
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", (await opened.Harness.GetTaskAsync(workerId!.Value, Context))!.State.ToJson(), "worker");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-an-abandoned-task-whose-definition-is-missing-waiting-until-it-is-installed-then-runs-its-abort", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var aborts = new SyncList<string?>();
            var started = new Deferred();
            var worker = Worker("test.late", null, aborts, started);
            var extension = Define.Extension(new Extension { Name = "late", Tasks = [worker] });
            setup.Registry.Install(extension);
            TaskId? workerId = null;
            AddTool(setup.Registry, Spawn(worker, id => workerId = id));
            AddTool(setup.Registry, Batch("spawn"));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            await opened.Harness.CloseAsync(Context);

            setup.Registry.Uninstall(extension);
            opened = await OpenRestart(path, setup);
            opened.Harness.Resume();
            await Eventually(async () => (await opened.Harness.InspectAsync(Context)).Tasks
                .FirstOrDefault(task => task.Record.Id == workerId)?.State.Kind == TaskInspectionKind.Blocked);
            Check((await opened.Harness.GetTaskAsync(workerId!.Value, Context))!.State.Status != DurableTaskStatus.Terminal, "worker waits");
            Seq([], aborts.Snapshot(), "no abort yet");
            setup.Registry.Install(extension);
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            Seq(["restart"], aborts.Snapshot(), "aborts");
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", (await opened.Harness.GetTaskAsync(workerId.Value, Context))!.State.ToJson(), "worker");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "orphans-an-abandoned-task-whose-definition-is-missing-once-an-abort-is-requested", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var started = new Deferred();
            var worker = Worker("test.gone", null, null, started);
            var extension = Define.Extension(new Extension { Name = "gone", Tasks = [worker] });
            setup.Registry.Install(extension);
            TaskId? workerId = null;
            AddTool(setup.Registry, Spawn(worker, id => workerId = id));
            AddTool(setup.Registry, Batch("spawn"));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            await opened.Harness.CloseAsync(Context);

            setup.Registry.Uninstall(extension);
            opened = await OpenRestart(path, setup);
            opened.Harness.Resume();
            await Eventually(async () => ReasonOf(await opened.Harness.GetTaskAsync(workerId!.Value, Context)) == "restart");
            await opened.Root.AbortAsync(Context);
            MatchJson("""{"status":"terminal","outcome":{"status":"orphaned","reason":"missing_task"}}""",
                (await opened.Harness.GetTaskAsync(workerId!.Value, Context))!.State.ToJson(), "worker");
            Equal(SubmissionStatus.Unanswered, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "marks-nothing-when-the-reopened-harness-never-starts-scheduling", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var running = new Deferred();
            AddTool(setup.Registry, Tool("hang", async (_, _, callContext) =>
            {
                running.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Batch("hang"));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenRestart(path, setup);
            await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await running.Task;
            await opened.Harness.CloseAsync(Context);

            // An inspection-only open: no resume, no submission.
            opened = await OpenRestart(path, setup);
            Equal(HarnessScheduling.Paused, (await opened.Harness.InspectAsync(Context)).Scheduling, "scheduling");
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            var nested = (await NestedTasks(opened.Harness))[0];
            Check(nested.AbandonOnRestart && !nested.AbortRequested, "nested: " + nested.ToJson().ToJson());
            Equal(DurableTaskStatus.Pending, nested.State.Status, "status");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-a-replay-safe-caller-s-unfinished-nested-calls-recover-and-reattaches-to-them-by-key", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var runs = new int[3];
            var safeRunning = new Deferred();
            var unsafeRunning = new Deferred();
            AddTool(setup.Registry, Tool("safe", async (_, _, callContext) =>
            {
                var run = Interlocked.Increment(ref runs[0]);
                if (run == 1)
                {
                    safeRunning.Resolve();
                    await Aborted(callContext.CancellationToken);
                }
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"safe run {run}")) };
            }, tool => tool with { Replay = ToolReplay.Safe }));
            AddTool(setup.Registry, Tool("unsafe", async (_, _, callContext) =>
            {
                Interlocked.Increment(ref runs[1]);
                unsafeRunning.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            NestedToolExecutionResult[] results = [];
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                Interlocked.Increment(ref runs[2]);
                results = await Task.WhenAll(api.ExecuteToolAsync("safe", JsonObjectValue.Empty, callContext),
                    api.ExecuteToolAsync("unsafe", JsonObjectValue.Empty, callContext));
                return new ToolExecutionResult();
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await Task.WhenAll(safeRunning.Task, unsafeRunning.Task);
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            Equal("2 1 2", $"{runs[0]} {runs[1]} {runs[2]}", "runs (safe, unsafe, caller)");
            // Default keys follow call order, so the rerun found both calls.
            var nested = await NestedTasks(opened.Harness);
            Equal(2, nested.Count, "nested tasks");
            foreach (var task in nested) Check(!task.AbandonOnRestart && task.AbortReason is null, "not abandoned: " + task.ToJson().ToJson());
            SameJson("\"safe run 2\"", results[0].StructuredOutput, "safe");
            Seq(["interrupted"], CodesOf(results[1]), "unsafe codes");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "clears-the-slots-of-nested-calls-an-earlier-attempt-finished-when-the-rerun-makes-none", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var finished = new Deferred();
            var attempts = 0;
            AddTool(setup.Registry, Tool("quick", (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText("ok")) })));
            AddTool(setup.Registry, Tool("caller", async (_, api, callContext) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    await api.ExecuteToolAsync("quick", JsonObjectValue.Empty, callContext);
                    finished.Resolve();
                    await Aborted(callContext.CancellationToken);
                }
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText("done")) };
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("caller"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await finished.Task;
            // The finished nested call's slot is still listed, below its running caller.
            Equal(1, ((JsonArrayValue)(await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context))!["nestedTools"]).Count, "slots");
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            int? slotsAfterCaller = null;
            opened.Harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in publication.Changes)
                {
                    if (change is not TaskWrite { Value: { Kind: "pi.tool" } task } || IsNested(task) || task.State.Status != DurableTaskStatus.Terminal) continue;
                    var live = publication.Changes.OfType<DocumentCommitChange>().FirstOrDefault(each => each.Record.Kind == "pi.live");
                    slotsAfterCaller = live?.Value is { } value && value.TryGetValue("nestedTools", out var slots) ? ((JsonArrayValue)slots).Count : 0;
                }
            });
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            Equal(2, attempts, "attempts");
            // The caller's terminal commit removed the slot, though the rerun made no nested call.
            Equal<int?>(0, slotsAfterCaller, "slots after the caller");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-nested-call-an-earlier-attempt-of-a-replay-safe-caller-left-running-and-the-rerun-never-made-again", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var running = new Deferred();
            var attempts = 0;
            AddTool(setup.Registry, Tool("slow", async (_, _, callContext) =>
            {
                running.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }, tool => tool with { Replay = ToolReplay.Safe }));
            AddTool(setup.Registry, Tool("caller", async (_, api, callContext) =>
            {
                // Only the first attempt calls `slow`; the rerun after the restart returns at once.
                if (Interlocked.Increment(ref attempts) == 1)
                    await api.ExecuteToolAsync("slow", JsonObjectValue.Empty, callContext, new NestedCallOptions { Key = "slow" });
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText("done")) };
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("caller"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await running.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            Equal(2, attempts, "attempts");
            // The rerun's settlement found the leftover call among the tasks it owns and aborted it.
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", (await NestedTasks(opened.Harness))[0].State.ToJson(), "leftover");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reattaches-a-replay-safe-caller-by-default-and-explicit-keys-and-rejects-a-rerun-that-calls-differently", async () =>
        {
            foreach (var order in new[] { "same", "swapped" })
            {
                var path = RestartPath();
                var setup = Setup();
                var runs = new SyncList<string>();
                AddTool(setup.Registry, Tool("echo", (args, _, _) =>
                {
                    runs.Add(Arg(args));
                    return Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText($"echo {Arg(args)}")) });
                }));
                var blocked = new Deferred();
                var callerRuns = 0;
                List<string> seen = [];
                AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
                {
                    var run = Interlocked.Increment(ref callerRuns);
                    var first = run == 2 && order == "swapped" ? "b" : "a";
                    var one = await api.ExecuteToolAsync("echo", Obj(("text", first)), callContext);
                    var named = await api.ExecuteToolAsync("echo", Obj(("text", "named")), callContext, new NestedCallOptions { Key = "named" });
                    if (run == 1)
                    {
                        blocked.Resolve();
                        await Aborted(callContext.CancellationToken);
                    }
                    seen = [Text(one), Text(named)];
                    return new ToolExecutionResult();
                }, tool => tool with { Replay = ToolReplay.Safe }));
                setup.Faux.SetResponses(Call("batch"), Done);
                var opened = await OpenRestart(path, setup);
                var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
                await blocked.Task;
                await opened.Harness.CloseAsync(Context);

                opened = await OpenRestart(path, setup);
                opened.Harness.Resume();
                Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, order + " status");
                var result = Results(await AllEntries(opened.Root))[0];
                if (order == "same")
                {
                    Seq(["a", "named"], runs.Snapshot(), "same runs");
                    Seq(["echo a", "echo named"], seen, "same seen");
                    Equal(false, result["isError"] is JsonBooleanValue { Value: true }, "same isError");
                }
                else
                {
                    // Key 1 was made with other arguments: the rerun throws instead of reusing a different call's result.
                    Seq(["a", "named"], runs.Snapshot(), "swapped runs");
                    Equal(true, result["isError"] is JsonBooleanValue { Value: true }, "swapped isError");
                    Check(result["content"].ToJson().Contains("Nested call c1/1 was already made with another tool", StringComparison.Ordinal),
                        "swapped content: " + result["content"].ToJson());
                }
                await opened.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "abandons-the-child-tasks-a-replay-unsafe-tool-created-and-awaited-in-memory", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var phases = new SyncList<string>();
            var started = new Deferred();
            var worker = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.direct-worker",
                Version = 1,
                Initial = _ => O("""{"phase":"work"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["work"] = async (_, runtime, _) =>
                    {
                        phases.Add("work");
                        started.Resolve();
                        await Aborted(runtime.Signal);
                    },
                },
                Abort = async (task, runtime, abortContext) =>
                {
                    phases.Add($"abort {(task.AbortReason is { } reason ? StructuredTests.Wire(reason) : "undefined")}");
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), abortContext);
                },
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "direct-worker", Tasks = [worker] }));
            TaskId? workerId = null;
            AddTool(setup.Registry, Tool("work", async (_, api, callContext) =>
            {
                var id = await api.CreateTaskAsync(worker, JsonValue.Null, new TaskOptions(TaskOwnership.Task(api.TaskId)), callContext);
                workerId = id;
                await api.WaitForTaskAsync(id, callContext);
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("work"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "submission");
            Seq(["work", "abort restart"], phases.Snapshot(), "phases");
            // Authored: the default comes from api.CreateTaskAsync.
            Check((await opened.Harness.GetTaskAsync(workerId!.Value, Context))!.AbandonOnRestart, "the child is abandonOnRestart");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "leaves-background-work-under-an-abandoned-nested-call-running-and-settles-without-it", async () =>
        {
            var path = RestartPath();
            var setup = Setup();
            var started = new Deferred();
            var background = Worker("test.background", null, null, started);
            setup.Registry.Install(Define.Extension(new Extension { Name = "background", Tasks = [background] }));
            TaskId? backgroundId = null;
            AddTool(setup.Registry, Tool("spawn", async (_, api, callContext) =>
            {
                // A conversation the call owns, with background work in it, as a persistent subagent has.
                backgroundId = await api.CommitAsync(async tx =>
                {
                    var child = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                    return await tx.CreateTaskAsync(background, JsonValue.Null,
                        new TaskOptions(TaskOwnership.Conversation) { ConversationId = child.Id, Background = true });
                }, callContext);
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Batch("spawn"));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenRestart(path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenRestart(path, setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            var spawn = (await NestedTasks(opened.Harness))[0];
            Check(ReasonOf(spawn) == "restart" && spawn.State.Status == DurableTaskStatus.Terminal, "spawn: " + spawn.ToJson().ToJson());
            var record = (await opened.Harness.GetTaskAsync(backgroundId!.Value, Context))!;
            Check(!record.AbortRequested && record.State.Status == DurableTaskStatus.Running, "background runs: " + record.ToJson().ToJson());
            await opened.Harness.AbortTaskAsync(backgroundId.Value, Context);
            await opened.Harness.CloseAsync(Context);
        });
    }
}
