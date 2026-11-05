// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-blocked.test.ts (a30c2a81d, 440a5fcdb, 5ba1cbaf3),
// ported in the Pi v1.5.0 sync (area reports): open's recovery pass keeps the host's wake active, and the task graph shows why a task
// cannot run (TaskGraphNode.blocked) and follows registry changes and failed migrations. Verbatim apart from the adaptations in
// HarnessSupport.cs, and these:
// - All 12 cases are ported. Area reports ported the recovery, graph and registry cases; area restarts (b3a29f111, over a30c2a81d's and
//   440a5fcdb's crash-loop guard) the eight restart-limit cases, with crashLoop and openWith's kill(). kill() is KillableStorage
//   (HarnessRestartFailureCases.cs): once killed, every call of the storage hangs, so the Harness writes nothing more, not even the pending
//   records a close writes for the tasks it stops; the killed storages are closed after the case (KilledStorages.Case), as upstream's
//   afterEach does. openWith's settings object is a HarnessSettings; the case that raises restartLimit between opens shares a
//   MutableSettings whose getter reads the current value, as upstream mutates the object it passed. A phase or abort handler that waits for
//   `runtime.signal`'s abort event is UntilAborted, which resolves (not rejects) when the signal aborts.
// - openNodeSqliteStorage is SqliteStorage.OpenAsync over a file in a fresh temporary directory. The store pass (StoreStorageNode.cs)
//   reruns the cases as harness.blocked.store.*, opening every file as a StoreStorage, as upstream's vitest.store.config.ts does.
// - commitWrites over the raw storage is PiSharp.Durable.Testing.CommitWrites.CommitWritesAsync; the record drops its memos, as upstream's
//   strips them first.
// - The failing storage's Proxy is ControlledStorage.FailNextCommit, which refuses the next commit: the Session fails as upstream's
//   rejected commit fails it.
// - A Wake is a record: toEqual([{ active: true }]) is [new Wake(true)]. onWake runs on a thread-pool thread (see harness-sleep's port), so
//   the recovery case flushes before it reads the wakes: a wake a late job delivers would count.
// - graph.value.tasks[String(id)]?.blocked is graph.Value.Node(id)?.Blocked: a task that ended and left the graph has no reason either.
// - graph.start(async () => {}) starts the watch with a listener that does nothing; graph.value is its Value.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Testing;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessBlockedCases() =>
        BlockedTests.Cases("harness.blocked.", async path => await SqliteStorage.OpenAsync(path));
}

/// <summary>The harness.blocked.* cases of this area (upstream harness-blocked.test.ts).</summary>
internal static class BlockedTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-blocked"), "session.sqlite");

    private static readonly TaskOptions ConversationOwned = new(TaskOwnership.Conversation);

    private static DurableTask Idle(string name, int version = 1, TaskMigration? migrate = null) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = version,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
        Abort = (_, _, _) => Task.CompletedTask,
        Migrate = migrate,
    });

    private sealed record OpenedWith(IHarness Harness, SyncList<Wake> Wakes, SyncList<ReportInfo> Infos)
    {
        /// <summary>Simulates a crash: every later call of its storage hangs (upstream <c>kill</c>).</summary>
        public Action Kill { get; init; } = static () => throw new InvalidOperationException("Opened without a killable storage");
    }

    /// <summary>Opens a Harness over the file at <paramref name="path"/>, collecting its wakes (upstream <c>openWith</c>). With
    /// <paramref name="killed"/>, its storage is a <see cref="KillableStorage"/> and <c>Kill</c> simulates a crash.</summary>
    private static async Task<OpenedWith> OpenWith(Func<string, Task<IStorage>> openFile, string path, IRegistry registry,
        KilledStorages? killed = null, HarnessSettings? settings = null)
    {
        var wakes = new SyncList<Wake>();
        var storage = await openFile(path);
        Action? kill = null;
        if (killed is not null)
        {
            var (killable, killIt) = killed.Killable(storage);
            storage = killable;
            kill = killIt;
        }
        var opened = await OpenTasks(storage, [], registry, settings: settings, onWake: wakes.Add);
        var result = new OpenedWith(opened.Harness, wakes, opened.Infos);
        return kill is null ? result : result with { Kill = kill };
    }

    /// <summary>Resolves once <paramref name="signal"/> aborts (upstream's <c>new Promise((resolve) =&gt;
    /// runtime.signal.addEventListener("abort", () =&gt; resolve()))</c>).</summary>
    private static Task UntilAborted(CancellationToken signal)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.Register(() => aborted.TrySetResult());
        return aborted.Task;
    }

    /// <summary>Upstream crashLoop's <c>state</c>: whether the task hangs in its phase, and how often the phase started.</summary>
    private sealed class LoopState
    {
        private int started;
        private volatile bool crashing = true;
        public bool Crashing { get => crashing; set => crashing = value; }
        public int Started => Volatile.Read(ref started);
        public void Start() => Interlocked.Increment(ref started);
    }

    /// <summary>A task that hangs in its phase while <c>Crashing</c>, as one killed every time would, and completes otherwise (upstream
    /// <c>crashLoop</c>); <paramref name="abort"/> replaces its abort handler, as upstream's <c>{ ...crashLoop(state).definition, abort }</c>.</summary>
    private static DurableTask CrashLoop(LoopState state, TaskPhaseHandler? abort = null) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.crash-loop",
        Version = 1,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (_, runtime, ctx) =>
            {
                state.Start();
                if (state.Crashing) await UntilAborted(runtime.Signal);
                else await runtime.CommitAsync((_, _) => (TaskState)Completed(JsonValue.Null), ctx);
            },
        },
        Abort = abort ?? ((_, _, _) => Task.CompletedTask),
    });

    /// <summary>Host settings whose <c>RestartLimit</c> can change between resolutions (upstream's mutable settings object).</summary>
    private sealed class MutableSettings(double? restartLimit) : HarnessSettings
    {
        private readonly Lock gate = new();
        private double? limit = restartLimit;
        public override double? RestartLimit
        {
            get { lock (gate) return limit; }
            init { }
        }
        public void SetRestartLimit(double? value)
        {
            lock (gate) limit = value;
        }
    }

    private static HarnessSettings Limit(double restartLimit) => new() { RestartLimit = restartLimit };

    private static TaskOutcome OutcomeOf(TaskRecord record) => ((TerminalTaskState)record.State).Outcome;

    /// <summary>Whether <paramref name="outcome"/> is a fault whose detail says the restart limit ended it.</summary>
    private static bool FaultedAtLimit(TaskOutcome outcome) =>
        outcome is FaultedTaskOutcome { Error.Detail: JsonObjectValue detail } && detail.TryGetValue("reason", out var reason) &&
        reason is JsonStringValue { Value: "restart_limit" };

    private static Task<TaskId> Create(IHarness harness, DurableTask task, TaskOptions? options = null) => harness.RootAsync(Context)
        .ContinueWith(root => root.Result.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, options ?? ConversationOwned), Context)).Unwrap();

    /// <param name="prefix">The case ID prefix: harness.blocked., or harness.blocked.store. for the store pass (StoreStorageNode.cs).</param>
    /// <param name="openFile">Upstream's openNodeSqliteStorage: opens the storage over the file at a path.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<string, Task<IStorage>> openFile)
    {
        yield return Case(prefix + "recovery-at-open.keeps-the-host-s-wake-active-until-open-s-recovery-pass-has-landed", async () =>
        {
            var path = SqlitePath();
            var held = Idle("test.held");
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "held", Tasks = [held] }));
            var first = await OpenWith(openFile, path, registry);
            var id = await Create(first.Harness, held);
            await first.Harness.CloseAsync(Context);
            // A crash left the task holding its outcome: the next open's recovery pass makes it terminal.
            var raw = await openFile(path);
            var record = (await raw.GetTaskAsync(id, Context))!;
            await raw.CommitWritesAsync([new TaskWrite(record with
            {
                Memos = null,
                State = new CompletingTaskState(new CompletedTaskOutcome(JsonValue.Null)),
            })], Context);
            await raw.CloseAsync(Context);

            // That pass's commit fails, as on a platform reset.
            var failing = new ControlledStorage(await openFile(path));
            failing.FailNextCommit(new InvalidOperationException("reset"));
            var wakes = new SyncList<Wake>();
            var (harness, _, _) = await OpenTasks(failing, [], registry, onWake: wakes.Add);
            Equal(SessionEndKind.Failed, (await harness.Closed).Kind, "closed");
            // The host was told work is underway, and never that it ended: its recovery alarm stays.
            await Flush();
            Seq([new Wake(true)], wakes.Snapshot(), "wakes");
        });

        // #11139
        yield return KilledStorages.Case(prefix + "tasks-that-cannot-run.faults-an-extension-s-task-cut-off-restartlimit-times-in-a-row-instead-of-running-it-again", async killed =>
        {
            var path = SqlitePath();
            var state = new LoopState();
            var registry = Registry.Create();
            var loop = CrashLoop(state);
            registry.Install(Define.Extension(new Extension { Name = "loop", Tasks = [loop] }));
            var opened = await OpenWith(openFile, path, registry, killed, Limit(3));
            var root = await opened.Harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(loop, JsonValue.Null, ConversationOwned), Context);
            // Each open runs the phase, and each crash cuts it off: three restarts.
            for (var run = 1; run <= 3; run++)
            {
                opened.Harness.Resume();
                var expected = run;
                await Eventually(() => state.Started == expected);
                opened.Kill(); // a crash: a close would not count
                opened = await OpenWith(openFile, path, registry, killed, Limit(3));
            }
            var (harness, _, infos) = opened;
            Equal(3, (await harness.GetTaskAsync(id, Context))?.Restarts, "restarts");
            // Nothing is parked: the task is ready, and the host is asked to run, which ends it.
            var inspection = await harness.InspectAsync(Context);
            var inspected = inspection.Tasks.FirstOrDefault(task => task.Record.Id == id)?.State;
            Check(inspected is { Kind: TaskInspectionKind.Pending, Migrates: false }, $"inspection: {inspected}");
            Equal(true, harness.Wake().Active, "wake");
            harness.Resume();
            var settled = await harness.WaitForTaskAsync(id, Context);
            SameJson($$$$"""{"status":"faulted","error":{"message":"Task {{{{id}}}} (test.crash-loop) was cut off 3 times without progress","detail":{"reason":"restart_limit"}}}""",
                OutcomeOf(settled).ToJson(), "outcome");
            Equal(3, state.Started, "started");
            await Eventually(() => infos.Count > 0);
            Seq([new ReportInfo(ReportKind.TaskFaulted)
            {
                Task = new ReportTask(id, "test.crash-loop", root.Id),
                Extension = "loop",
                Reason = ReportReason.RestartLimit,
            }], infos.Snapshot(), "infos");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "tasks-that-cannot-run.counts-no-restart-for-a-task-a-close-stopped", async () =>
        {
            var path = SqlitePath();
            var state = new LoopState();
            var registry = Registry.Create();
            var loop = CrashLoop(state);
            registry.Install(Define.Extension(new Extension { Name = "loop", Tasks = [loop] }));
            var opened = await OpenWith(openFile, path, registry, settings: Limit(1));
            var id = await Create(opened.Harness, loop);
            opened.Harness.Resume();
            await Eventually(() => state.Started == 1);
            await opened.Harness.CloseAsync(Context);
            opened = await OpenWith(openFile, path, registry, settings: Limit(1));
            var record = await opened.Harness.GetTaskAsync(id, Context);
            Equal(DurableTaskStatus.Pending, record?.State.Status, "status");
            Equal(null, record?.Restarts, "restarts");
            state.Crashing = false;
            Equal(TaskOutcomeStatus.Completed, OutcomeOf(await opened.Harness.WaitForTaskAsync(id, Context)).Status, "outcome");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "tasks-that-cannot-run.keeps-an-abort-mark-admitted-before-close-in-the-record-close-writes-back", async () =>
        {
            var path = SqlitePath();
            var state = new LoopState();
            var registry = Registry.Create();
            var loop = CrashLoop(state);
            registry.Install(Define.Extension(new Extension { Name = "loop", Tasks = [loop] }));
            var (harness, _, _) = await OpenWith(openFile, path, registry);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(loop, JsonValue.Null, ConversationOwned), Context);
            harness.Resume();
            await Eventually(() => state.Started == 1);
            // Hold the line; the abort mark is admitted behind it, then close begins.
            var gate = new HarnessTests.Deferred();
            var holding = root.CommitAsync(new Func<ITx, Task>(_ => gate.Task), Context);
            var aborting = harness.AbortTaskAsync(id, Context);
            var closing = harness.CloseAsync(Context);
            // The invocation ends while the line is held; its write-back waits behind the abort mark.
            await Flush();
            gate.Resolve();
            await holding;
            Equal(TaskAbortResult.Marked, await aborting, "abort");
            await closing;
            var raw = await openFile(path);
            var record = await raw.GetTaskAsync(id, Context);
            Check(record is { AbortRequested: true, State: PendingTaskState }, $"record: {record?.ToJson().ToJson()}");
            await raw.CloseAsync(Context);
        });

        yield return KilledStorages.Case(prefix + "tasks-that-cannot-run.faults-a-task-whose-abort-handler-is-cut-off-restartlimit-times", async killed =>
        {
            var path = SqlitePath();
            var aborts = 0;
            var stuck = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.stuck-abort",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, runtime, _) => UntilAborted(runtime.Signal) },
                Abort = (_, runtime, _) =>
                {
                    Interlocked.Increment(ref aborts);
                    return UntilAborted(runtime.Signal);
                },
            });
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "stuck", Tasks = [stuck] }));
            var opened = await OpenWith(openFile, path, registry, killed, Limit(1));
            var id = await Create(opened.Harness, stuck);
            opened.Harness.Resume();
            _ = opened.Harness.AbortTaskAsync(id, Context);
            await Eventually(() => Volatile.Read(ref aborts) == 1);
            opened.Kill(); // a crash in the abort handler
            opened = await OpenWith(openFile, path, registry, killed, Limit(1));
            var settled = await opened.Harness.WaitForTaskAsync(id, Context);
            Check(FaultedAtLimit(OutcomeOf(settled)), $"outcome: {OutcomeOf(settled).ToJson().ToJson()}");
            // The receipt keeps no restart count.
            Equal(null, settled.Restarts, "restarts");
            Equal(1, Volatile.Read(ref aborts), "aborts");
            var infos = opened.Infos;
            await Eventually(() => infos.Count > 0);
            var reported = infos.Snapshot();
            Check(reported is [{ Kind: ReportKind.TaskFaulted, Reason: ReportReason.RestartLimit } info] && info.Task?.Id == id,
                $"infos: {string.Join(", ", reported)}");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(prefix + "tasks-that-cannot-run.reads-restartlimit-when-it-reserves-the-task-a-raised-one-runs-it-again", async killed =>
        {
            var path = SqlitePath();
            var state = new LoopState();
            var registry = Registry.Create();
            var loop = CrashLoop(state);
            registry.Install(Define.Extension(new Extension { Name = "loop", Tasks = [loop] }));
            var settings = new MutableSettings(1);
            var opened = await OpenWith(openFile, path, registry, killed, settings);
            var id = await Create(opened.Harness, loop);
            opened.Harness.Resume();
            await Eventually(() => state.Started == 1);
            opened.Kill(); // a crash: a close would not count
            opened = await OpenWith(openFile, path, registry, killed, settings);
            Equal(1, (await opened.Harness.GetTaskAsync(id, Context))?.Restarts, "restarts");
            state.Crashing = false;
            // The host's live settings change before the task is reserved; it runs instead of ending.
            settings.SetRestartLimit(double.PositiveInfinity);
            opened.Harness.Resume();
            Equal(TaskOutcomeStatus.Completed, OutcomeOf(await opened.Harness.WaitForTaskAsync(id, Context)).Status, "outcome");
            Equal(2, state.Started, "started");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(prefix + "tasks-that-cannot-run.runs-the-abort-handler-of-a-task-at-the-limit", async killed =>
        {
            var path = SqlitePath();
            var state = new LoopState();
            var aborted = new SyncList<TaskId>();
            var registry = Registry.Create();
            var loop = CrashLoop(state, async (task, runtime, ctx) =>
            {
                aborted.Add(task.Id);
                await runtime.CommitAsync((_, _) => (TaskState)NextTaskState.Aborted(), ctx);
            });
            registry.Install(Define.Extension(new Extension { Name = "loop", Tasks = [loop] }));
            var opened = await OpenWith(openFile, path, registry, killed, Limit(1));
            var id = await Create(opened.Harness, loop);
            opened.Harness.Resume();
            await Eventually(() => state.Started == 1);
            opened.Kill(); // a crash: a close would not count
            opened = await OpenWith(openFile, path, registry, killed, Limit(1));
            await opened.Harness.AbortTaskAsync(id, Context);
            Equal(TaskOutcomeStatus.Aborted, OutcomeOf(await opened.Harness.WaitForTaskAsync(id, Context)).Status, "outcome");
            Seq([id], aborted.Snapshot(), "aborted");
            Equal(1, state.Started, "started");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(prefix + "tasks-that-cannot-run.does-not-count-the-same-checkpoint-committed-again-as-progress", async killed =>
        {
            var path = SqlitePath();
            var started = 0;
            var intent = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.intent",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (task, runtime, ctx) =>
                    {
                        Interlocked.Increment(ref started);
                        // Intent before an effect, then the effect never returns: the checkpoint is unchanged.
                        await runtime.CommitAsync((_, _) => (TaskState)NextTaskState.Running(task.Checkpoint), ctx);
                        await UntilAborted(runtime.Signal);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "intent", Tasks = [intent] }));
            var opened = await OpenWith(openFile, path, registry, killed, Limit(2));
            var id = await Create(opened.Harness, intent);
            for (var run = 1; run <= 2; run++)
            {
                opened.Harness.Resume();
                var expected = run;
                await Eventually(() => Volatile.Read(ref started) == expected);
                var current = opened.Harness;
                await Eventually(async () => (await current.GetTaskAsync(id, Context))?.State.Status == DurableTaskStatus.Running);
                opened.Kill(); // a crash: a close would not count
                opened = await OpenWith(openFile, path, registry, killed, Limit(2));
            }
            Equal(2, (await opened.Harness.GetTaskAsync(id, Context))?.Restarts, "restarts");
            opened.Harness.Resume();
            var outcome = OutcomeOf(await opened.Harness.WaitForTaskAsync(id, Context));
            Check(FaultedAtLimit(outcome), $"outcome: {outcome.ToJson().ToJson()}");
            Equal(2, Volatile.Read(ref started), "started");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(prefix + "tasks-that-cannot-run.starts-the-count-over-when-the-task-makes-progress", async killed =>
        {
            var path = SqlitePath();
            var state = new LoopState();
            var steps = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.steps",
                Version = 1,
                Initial = _ => O("""{"phase":"one"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["one"] = async (_, runtime, ctx) =>
                    {
                        state.Start();
                        if (state.Crashing) await UntilAborted(runtime.Signal);
                        else await runtime.CommitAsync((_, _) => (TaskState)NextTaskState.Running(O("""{"phase":"two"}""")), ctx);
                    },
                    ["two"] = async (_, runtime, _) =>
                    {
                        state.Start();
                        await UntilAborted(runtime.Signal);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "steps", Tasks = [steps] }));
            var opened = await OpenWith(openFile, path, registry, killed, Limit(2));
            var id = await Create(opened.Harness, steps);
            opened.Harness.Resume();
            await Eventually(() => state.Started == 1);
            opened.Kill(); // a crash: a close would not count
            opened = await OpenWith(openFile, path, registry, killed, Limit(2));
            Equal(1, (await opened.Harness.GetTaskAsync(id, Context))?.Restarts, "restarts");
            // Progress: phase one commits phase two, which resets the count.
            state.Crashing = false;
            opened.Harness.Resume();
            await Eventually(() => state.Started == 3);
            Equal(null, (await opened.Harness.GetTaskAsync(id, Context))?.Restarts, "restarts after progress");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "tasks-that-cannot-run.tells-the-graph-and-the-host-when-a-migration-fails", async () =>
        {
            var path = SqlitePath();
            var v1 = Idle("test.migrating");
            var v2 = Idle("test.migrating", 2, (_, _, _) => throw new InvalidOperationException("cannot migrate"));
            var firstRegistry = Registry.Create();
            firstRegistry.Install(Define.Extension(new Extension { Name = "v1", Tasks = [v1] }));
            var opened = await OpenWith(openFile, path, firstRegistry);
            // Created, never run: scheduling stays off.
            var id = await Create(opened.Harness, v1);
            await opened.Harness.CloseAsync(Context);
            var second = Registry.Create();
            second.Install(Define.Extension(new Extension { Name = "v2", Tasks = [v2] }));
            opened = await OpenWith(openFile, path, second);
            var graph = await opened.Harness.WatchTaskGraphAsync(Context);
            // A started watch keeps its value current.
            graph.Start((_, _) => Task.CompletedTask);
            opened.Harness.Resume();
            await Eventually(() => graph.Value.Node(id)?.Blocked == BlockedReason.MigrationFailed);
            await Eventually(() => !opened.Harness.Wake().Active);
            Equal(new Wake(false), opened.Harness.Wake(), "wake");
            await Eventually(() => opened.Wakes.Snapshot() is { Count: > 0 } wakes && !wakes[^1].Active);
            await graph.StopAsync();
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "tasks-that-cannot-run.shows-an-abandoned-task-waiting-for-its-definition-as-blocked", async () =>
        {
            var path = SqlitePath();
            var abandoned = Idle("test.abandoned");
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "abandoned", Tasks = [abandoned] }));
            var opened = await OpenWith(openFile, path, registry);
            var id = await Create(opened.Harness, abandoned, ConversationOwned with { AbandonOnRestart = true });
            await opened.Harness.CloseAsync(Context);
            // The next open marks it `restart`; without its definition, it waits for one to clean up.
            opened = await OpenWith(openFile, path, Registry.Create());
            var graph = await opened.Harness.WatchTaskGraphAsync(Context);
            // A started watch keeps its value current.
            graph.Start((_, _) => Task.CompletedTask);
            opened.Harness.Resume();
            await Eventually(async () => (await opened.Harness.GetTaskAsync(id, Context))?.AbortReason == TaskAbortReason.Restart);
            await Eventually(() => graph.Value.Node(id)?.Blocked == BlockedReason.MissingTask);
            await graph.StopAsync();
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "tasks-that-cannot-run.shows-a-task-without-a-definition-as-blocked-and-wakes-the-host-once-a-registry-change-unblocks-it", async () =>
        {
            var missing = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.missing",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(JsonValue.Null), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var registry = Registry.Create();
            var path = SqlitePath();
            var (harness, wakes, _) = await OpenWith(openFile, path, registry);
            var graph = await harness.WatchTaskGraphAsync(Context);
            // A started watch keeps its value current.
            graph.Start((_, _) => Task.CompletedTask);
            var id = await Create(harness, missing);
            harness.Resume();
            await Eventually(() => graph.Value.Node(id)?.Blocked == BlockedReason.MissingTask);
            await Eventually(() => !harness.Wake().Active);
            registry.Install(Define.Extension(new Extension { Name = "late", Tasks = [missing] }));
            // The graph follows the registry; the host hears it can run work now.
            await Eventually(() => graph.Value.Node(id)?.Blocked is null);
            await Eventually(() => wakes.Snapshot().Any(wake => wake.Active));
            Equal(TaskOutcomeStatus.Completed, ((TerminalTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status, "outcome");
            await graph.StopAsync();
            await harness.CloseAsync(Context);
        });
    }
}
