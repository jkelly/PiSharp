// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-blocked.test.ts (a30c2a81d, 440a5fcdb, 5ba1cbaf3),
// ported in the Pi v1.5.0 sync (area reports): open's recovery pass keeps the host's wake active, and the task graph shows why a task
// cannot run (TaskGraphNode.blocked) and follows registry changes and failed migrations. Verbatim apart from the adaptations in
// HarnessSupport.cs, and these:
// - Only this area's cases are ported. The restart-limit cases ("faults an extension's task cut off restartLimit times ...", "counts no
//   restart for a task a close stopped", "keeps an abort mark admitted before close in the record close writes back", "faults a task whose
//   abort handler ...", "reads restartLimit when it reserves the task ...", "runs the abort handler of a task at the limit", "does not count
//   the same checkpoint committed again as progress", "starts the count over when the task makes progress") are area restarts', with
//   crashLoop and openWith's kill(); openWith here opens the file and collects the wakes.
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

    private sealed record OpenedWith(IHarness Harness, SyncList<Wake> Wakes, SyncList<ReportInfo> Infos);

    /// <summary>Opens a Harness over the file at <paramref name="path"/>, collecting its wakes (upstream <c>openWith</c>, without the
    /// restart areas' kill()).</summary>
    private static async Task<OpenedWith> OpenWith(Func<string, Task<IStorage>> openFile, string path, IRegistry registry)
    {
        var wakes = new SyncList<Wake>();
        var opened = await OpenTasks(await openFile(path), [], registry, onWake: wakes.Add);
        return new OpenedWith(opened.Harness, wakes, opened.Infos);
    }

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
            Equal(SessionEndReason.Failed, (await harness.Closed).Reason, "closed");
            // The host was told work is underway, and never that it ended: its recovery alarm stays.
            await Flush();
            Seq([new Wake(true)], wakes.Snapshot(), "wakes");
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
            graph.Start((_, _, _) => Task.CompletedTask);
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
            graph.Start((_, _, _) => Task.CompletedTask);
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
            graph.Start((_, _, _) => Task.CompletedTask);
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
