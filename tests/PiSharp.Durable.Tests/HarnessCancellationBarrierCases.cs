// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-cancellation-barrier.test.ts: work below a cancelled
// owner starts no run phase, even before its cascade mark commits, and restart marks give way to requests. Verbatim on MemoryStorage, as
// upstream, apart from the adaptations of HarnessSchedulerSupport.cs and these:
// - HoldMarks (upstream `class HoldMarks extends MemoryStorage`) is a HookedStorage over MemoryStorage.
// - Upstream writes records straight to the storage (storage.commit([...])) with spreads; here StorageWrite.Task (TaskWrite) of the record
//   with the changed fields.
// - "does not start its next phase while the cascade that marks it has not committed" lets pending work progress with five flushes, as
//   upstream does with five flush() calls; ranNext is read after them and again once the abort has finished.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static HarnessTests;
using static Program;
using static SchedulerTests;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessCancellationBarrierCases() => SchedulerTests.BarrierCases();
}

internal static partial class SchedulerTests
{
    internal static IEnumerable<(string, Func<Task>)> BarrierCases()
    {
        const string prefix = "harness.cancellation-barrier.";

        yield return Case(prefix + "does-not-start-its-next-phase-while-the-cascade-that-marks-it-has-not-committed", async () =>
        {
            var storage = new HookedStorage(new MemoryStorage());
            TaskId? target = null;
            var held = new Deferred();
            var release = new Deferred();
            // Holds every commit that abort-marks `target`, while set, until released.
            storage.OnCommit = async writes =>
            {
                if (target is { } id && writes.Any(write => write is TaskWrite { Value: var record } && record.Id == id && record.AbortRequested))
                {
                    held.Resolve();
                    await release.Task;
                }
            };
            var entered = new Deferred();
            var advance = new Deferred();
            var ranNext = 0;
            var dormant = Dormant();
            var child = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.child",
                Version = 1,
                Initial = _ => O("""{"phase":"first"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["first"] = async (_, runtime, ctx) =>
                    {
                        entered.Resolve();
                        await advance.Task;
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"next"}""")), ctx);
                    },
                    ["next"] = async (_, runtime, _) =>
                    {
                        Interlocked.Exchange(ref ranNext, 1);
                        await Aborted(runtime.Signal);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
            var (harness, _, _) = await OpenTasks(storage, [dormant, child]);
            var root = await harness.RootAsync(Context);
            var parent = await root.CommitAsync(tx => tx.CreateTaskAsync(dormant, JsonValue.Null, OwnConversation()), Context);
            var childId = await root.CommitAsync(tx => tx.CreateTaskAsync(child, JsonValue.Null, OwnedBy(parent)), Context);
            target = childId;
            try
            {
                harness.Resume();
                await entered.Task;
                var aborting = harness.AbortTaskAsync(parent, Context);
                await held.Task;
                // The parent's request is durable, the cascade's mark of the child is not: its phase must not advance.
                advance.Resolve();
                for (var i = 0; i < 5; i++) await Flush();
                Equal(0, Volatile.Read(ref ranNext), "the next phase did not run");
                release.Resolve();
                Equal("aborted", await OutcomeOf(harness, childId), "child outcome");
                await aborting;
                Equal(0, Volatile.Read(ref ranNext), "the next phase never ran");
            }
            finally
            {
                advance.Resolve();
                release.Resolve();
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "lets-a-failfast-request-in-the-same-pass-override-a-restart-mark", async () =>
        {
            var storage = new MemoryStorage();
            var dormant = Dormant();
            var session = Session.Create(storage);
            var conversation = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            var parent = await session.CommitAsync(tx => tx.CreateTaskAsync(dormant, JsonValue.Null, OwnConversation(conversation.Id)), Context);
            var failed = await session.CommitAsync(tx => tx.CreateTaskAsync(dormant, JsonValue.Null, OwnedBy(parent)), Context);
            var child = await session.CommitAsync(tx => tx.CreateTaskAsync(dormant, JsonValue.Null, OwnedBy(parent)), Context);
            // A restart-abandoned owner that waits failFast on a failed child and a live one.
            var owner = (await storage.GetTaskAsync(parent, Context))!;
            var failedRecord = (await storage.GetTaskAsync(failed, Context))!;
            var waiting = NextTaskState.Waiting(O("""{"phase":"work"}"""), [failed, child], JoinPolicy.FailFast);
            await storage.CommitAsync(
            [
                new TaskWrite(owner with { AbortRequested = true, AbortReason = TaskAbortReason.Restart, State = waiting }),
                new TaskWrite(failedRecord with { State = NextTaskState.Failed("failed") }),
            ], Context);
            // The child's definition is missing: a restart mark would keep it waiting, a request orphans it.
            var (harness, _, _) = await OpenTasks(storage, []);
            try
            {
                harness.Resume();
                await Eventually(async () => (await harness.GetTaskAsync(child, Context))?.State.Status == DurableTaskStatus.Terminal);
                var outcome = ((TerminalTaskState)(await RecordOf(harness, child)).State).Outcome;
                Check(outcome is OrphanedTaskOutcome { Reason: "missing_task" }, $"orphaned missing_task, actual {outcome.ToJson().ToJson()}");
            }
            finally
            {
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "upgrades-restart-marks-down-the-tree-when-an-abort-is-requested-above-them", async () =>
        {
            var storage = new MemoryStorage();
            var dormant = Dormant();
            var session = Session.Create(storage);
            var conversation = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            var top = await session.CommitAsync(tx => tx.CreateTaskAsync(dormant, JsonValue.Null, OwnConversation(conversation.Id)), Context);
            var middle = await session.CommitAsync(tx => tx.CreateTaskAsync(dormant, JsonValue.Null, OwnedBy(top, abandonOnRestart: true)), Context);
            var missing = Dormant("test.missing");
            var leaf = await session.CommitAsync(tx => tx.CreateTaskAsync(missing, JsonValue.Null, OwnedBy(middle)), Context);
            // Only Dormant is registered: the leaf waits for its definition under the restart cascade.
            var (harness, _, _) = await OpenTasks(storage, [dormant]);
            try
            {
                harness.Resume();
                await Eventually(async () => (await harness.GetTaskAsync(leaf, Context))?.AbortReason == TaskAbortReason.Restart);
                await Eventually(async () =>
                {
                    var inspected = (await harness.InspectAsync(Context)).Tasks.FirstOrDefault(task => task.Record.Id == leaf);
                    return inspected?.State.Kind == TaskInspectionKind.Blocked;
                });
                Check((await RecordOf(harness, leaf)).State.Status != DurableTaskStatus.Terminal, "the leaf waits");
                // A request on the top task reaches the leaf two levels down, which is then orphaned.
                await harness.AbortTaskAsync(top, Context);
                foreach (var (id, status) in new[] { (leaf, "orphaned"), (middle, "aborted"), (top, "aborted") })
                    Equal(status, await OutcomeOf(harness, id), $"task {id}");
            }
            finally
            {
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "marks-a-flagged-task-that-holds-its-outcome-aborting-its-owned-work-and-keeps-the-outcome", async () =>
        {
            var storage = new MemoryStorage();
            var dormant = Dormant();
            var session = Session.Create(storage);
            var conversation = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            var holder = await session.CommitAsync(tx =>
                tx.CreateTaskAsync(dormant, JsonValue.Null, OwnConversation(conversation.Id, abandonOnRestart: true)), Context);
            var child = await session.CommitAsync(tx => tx.CreateTaskAsync(dormant, JsonValue.Null, OwnedBy(holder)), Context);
            var record = (await storage.GetTaskAsync(holder, Context))!;
            await storage.CommitAsync([new TaskWrite(record with { State = new CompletingTaskState(new CompletedTaskOutcome(JsonValue.Null)) })], Context);
            var (harness, _, _) = await OpenTasks(storage, [dormant]);
            try
            {
                harness.Resume();
                Equal("aborted", await OutcomeOf(harness, child), "child outcome");
                var settled = await harness.WaitForTaskAsync(holder, Context);
                Equal<TaskAbortReason?>(TaskAbortReason.Restart, settled.AbortReason, "holder abort reason");
                Equal("completed", StructuredTests.Wire(((TerminalTaskState)settled.State).Outcome.Status), "holder outcome");
                // Beyond upstream's case: the mark appends abortReason as upstream's spread does, before endedAt (compare upstream's
                // abandon-on-restart capture, Fixtures/v1.2.0/upstream-sessions/abandon-on-restart/resumed.json).
                Seq(["id", "conversationId", "kind", "version", "input", "background", "abortRequested", "abandonOnRestart", "state", "abortReason",
                    "endedAt"], settled.ToJson().Keys, "holder key order");
            }
            finally
            {
                await harness.CloseAsync(Context);
            }
        });
    }
}
