// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-scheduler-indexes.test.ts. The scheduler's in-memory
// indexes: cases where a wrong index changes behaviour, each against what the committed records imply (spec §5.4, §5.5), and
// TaskRuntime.ownedTasks. Verbatim on SQLite, as upstream, apart from the adaptations of HarnessSchedulerSupport.cs and these:
// - Probe's runtime is kept as an ITaskRuntime, whose OwnedTasksAsync is upstream's ownedTasks.
// - "rejects once its invocation has ended" reads the error message: upstream's toThrow("invocation has ended") matches a substring.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static HarnessTests;
using static Program;
using static SchedulerTests;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessSchedulerIndexesCases() => SchedulerTests.IndexesCases();
}

internal static partial class SchedulerTests
{
    internal static IEnumerable<(string, Func<Task>)> IndexesCases()
    {
        const string prefix = "harness.scheduler-indexes.";

        yield return Case(prefix + "takes-a-cascades-reason-from-the-nearest-cancelling-owner", async () =>
        {
            // top (abort requested) -> middle (restart-marked) -> leaf: the leaf gets middle's `restart` first, as the nearest cancelling
            // owner, and the request only once middle carries it.
            var gates = new SchedulerGates();
            var hold = gates.Hold("test.index.hold");
            var path = SqlitePath("indexes-reason");
            var session = Session.Create(await OpenSqlite(path));
            var (top, middle, leaf) = await SessionCommit(session, async tx =>
            {
                var conversation = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                var top = await tx.CreateTaskAsync(hold, Named("top"), OwnConversation(conversation.Id));
                var middle = await tx.CreateTaskAsync(hold, Named("middle"), OwnedBy(top));
                var leaf = await tx.CreateTaskAsync(hold, Named("leaf"), OwnedBy(middle));
                return (top, middle, leaf);
            });
            await SessionCommit(session, async tx =>
            {
                var topRecord = (await tx.GetTaskAsync(top))!;
                var middleRecord = (await tx.GetTaskAsync(middle))!;
                tx.SetTask(topRecord with { AbortRequested = true });
                tx.SetTask(middleRecord with { AbortRequested = true, AbortReason = TaskAbortReason.Restart });
                return true;
            });
            await session.CloseAsync(Context);
            // Record every mark of the leaf, in commit order.
            var storage = await OpenSqlite(path);
            var reasons = new SyncList<TaskAbortReason?>();
            storage.OnCommit = writes =>
            {
                foreach (var write in writes)
                    if (write is TaskWrite { Value: var record } && record.Id == leaf && record.AbortRequested) reasons.Add(record.AbortReason);
                return Task.CompletedTask;
            };
            var (harness, _, _) = await OpenTasks(storage, [hold]);
            harness.Resume();
            foreach (var id in new[] { leaf, middle, top }) await harness.WaitForTaskAsync(id, Context);
            Equal<TaskAbortReason?>(TaskAbortReason.Restart, reasons[0], "first mark of the leaf");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "finalizes-a-chain-of-held-owners-in-the-commit-that-frees-the-last-one", async () =>
        {
            var gates = new SchedulerGates();
            var hold = gates.Hold("test.index.hold");
            var storage = await OpenSqlite(SqlitePath("indexes-chain"));
            // The commit, by count, that wrote each terminal record.
            var endedIn = new ConcurrentDictionary<TaskId, int>();
            var commits = 0;
            storage.OnCommit = writes =>
            {
                var count = Interlocked.Increment(ref commits);
                foreach (var write in writes)
                    if (write is TaskWrite { Value: var record } && record.State.Status == DurableTaskStatus.Terminal) endedIn[record.Id] = count;
                return Task.CompletedTask;
            };
            var (harness, _, _) = await OpenTasks(storage, [hold]);
            var root = await harness.RootAsync(Context);
            harness.Resume();
            var (top, middle, leaf) = await root.CommitAsync(async tx =>
            {
                var top = await tx.CreateTaskAsync(hold, Named("top"), OwnConversation());
                var middle = await tx.CreateTaskAsync(hold, Named("middle"), OwnedBy(top));
                var leaf = await tx.CreateTaskAsync(hold, Named("leaf"), OwnedBy(middle));
                return (top, middle, leaf);
            }, Context);
            gates.Open("top");
            gates.Open("middle");
            await Eventually(async () => (await RecordOf(harness, top)).State.Status == DurableTaskStatus.Completing);
            await Eventually(async () => (await RecordOf(harness, middle)).State.Status == DurableTaskStatus.Completing);
            gates.Open("leaf");
            Equal("completed", await OutcomeOf(harness, top), "top outcome");
            // Both owners end in one finalize commit, after the leaf's.
            Equal(endedIn[top], endedIn[middle], "top and middle end in one commit");
            Check(endedIn[middle] > endedIn[leaf], "the owners end after the leaf");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-scope-busy-after-reopen-for-work-below-an-ended-task-whose-chain-is-not-loaded-yet", async () =>
        {
            var gates = new SchedulerGates();
            var hold = gates.Hold("test.index.hold");
            var path = SqlitePath("indexes-busy");
            var opened = await OpenTasks(await OpenSqlite(path), [hold]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            // root conversation -> owner (ends) -> conversation C -> inner (created after owner ended), as a subagent call's.
            var (owner, child) = await root.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(hold, Named("owner"), OwnConversation());
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                return (owner, child.Id);
            }, Context);
            gates.Open("owner");
            await opened.Harness.WaitForTaskAsync(owner, Context);
            var conversation = (await opened.Harness.ConversationAsync(child, Context))!;
            var inner = await conversation.CommitAsync(tx => tx.CreateTaskAsync(hold, Named("inner"), OwnConversation()), Context);
            await opened.Harness.CloseAsync(Context);
            gates.Clear();
            // Hold the read of C's record, so the chain above `inner` stays unloaded while idle is decided.
            var storage = await OpenSqlite(path);
            var held = new Deferred();
            storage.OnConversation = async id =>
            {
                if (id == child) await held.Task;
            };
            opened = await OpenTasks(storage, [hold]);
            // A task whose chain is not loaded counts as inside every scope. (The held read blocks the Session line, so the root handle
            // comes after.)
            Check(!await Settled(opened.Harness.WaitForIdleAsync(Context)), "the scope is busy");
            held.Resolve();
            root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            gates.Open("inner");
            await opened.Harness.WaitForTaskAsync(inner, Context);
            await root.WaitForIdleAsync(Context);
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ownedtasks.lists-the-live-tasks-a-task-owns-directly-in-id-order-also-after-a-reopen", async () =>
        {
            var probes = new Probes();
            var path = SqlitePath("indexes-owned");
            var opened = await OpenTasks(await OpenSqlite(path), [probes.Hold, probes.Probe]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            // The probe owns: two held tasks, one that ends, a grandchild below the first, and a conversation with a task.
            var (probe, first, ends, second) = await root.CommitAsync(async tx =>
            {
                var probe = await tx.CreateTaskAsync(probes.Probe, Named("probe"), OwnConversation());
                var first = await tx.CreateTaskAsync(probes.Hold, Named("first"), OwnedBy(probe));
                var ends = await tx.CreateTaskAsync(probes.Hold, Named("ends"), OwnedBy(probe));
                var second = await tx.CreateTaskAsync(probes.Hold, Named("second"), OwnedBy(probe));
                await tx.CreateTaskAsync(probes.Hold, Named("grandchild"), OwnedBy(first));
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(probe));
                await tx.CreateTaskAsync(probes.Hold, Named("inside"), OwnConversation(child.Id));
                return (probe, first, ends, second);
            }, Context);
            probes.Gates.Open("ends");
            await opened.Harness.WaitForTaskAsync(ends, Context);
            // The first ends too, but holds its outcome as `completing` while its grandchild runs: still live, so listed.
            probes.Gates.Open("first");
            await Eventually(async () => (await RecordOf(opened.Harness, first)).State.Status == DurableTaskStatus.Completing);
            probes.Gates.Open("look.probe");
            await Eventually(() => probes.Owned.ContainsKey("probe"));
            Equal(string.Join(",", first, second), string.Join(",", probes.Owned["probe"]), "owned tasks");
            await opened.Harness.CloseAsync(Context);

            // After a reopen the rerun sees the same tasks, which an earlier invocation created.
            probes.Gates.Clear();
            probes.Owned.Clear();
            opened = await OpenTasks(await OpenSqlite(path), [probes.Hold, probes.Probe]);
            opened.Harness.Resume();
            probes.Gates.Open("look.probe");
            await Eventually(() => probes.Owned.ContainsKey("probe"));
            Equal(string.Join(",", first, second), string.Join(",", probes.Owned["probe"]), "owned tasks after reopen");
            _ = probe;
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ownedtasks.rejects-once-its-invocation-has-ended", async () =>
        {
            var probes = new Probes();
            var opened = await OpenTasks(await OpenSqlite(SqlitePath("indexes-ended")), [probes.Hold, probes.Probe]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            var probe = await root.CommitAsync(tx => tx.CreateTaskAsync(probes.Probe, Named("probe"), OwnConversation()), Context);
            await Eventually(() => probes.Runtimes.ContainsKey("probe"));
            var runtime = probes.Runtimes["probe"];
            // Aborting joins the run invocation, which has ended by then.
            await opened.Harness.AbortTaskAsync(probe, Context);
            var error = await ThrowsAsync<InvalidOperationException>(() => runtime.OwnedTasksAsync(Context), "ownedTasks after the invocation");
            Check(error.Message.Contains("invocation has ended", StringComparison.Ordinal), error.Message);
            await opened.Harness.CloseAsync(Context);
        });
    }

    /// <summary>harness-scheduler-indexes' Hold and Probe with their gates, the owned tasks each probe run saw, and its runtime.</summary>
    private sealed class Probes
    {
        public Probes()
        {
            Hold = Gates.Hold("test.index.hold");
            // Records its owned tasks once its `look` gate opens, then holds until its own gate opens.
            Probe = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.index.probe",
                Version = 1,
                Initial = _ => O("""{"phase":"look"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["look"] = async (task, runtime, ctx) =>
                    {
                        var name = NameOf(task.Input);
                        Runtimes[name] = runtime;
                        if (!await SchedulerGates.Opened(Gates.Gate($"look.{name}").Task, runtime.Signal)) return;
                        Owned[name] = [.. (await runtime.OwnedTasksAsync(ctx)).Select(record => record.Id)];
                        if (!await SchedulerGates.Opened(Gates.Gate(name).Task, runtime.Signal)) return;
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
        }

        public SchedulerGates Gates { get; } = new();
        public DurableTask Hold { get; }
        public DurableTask Probe { get; }
        public ConcurrentDictionary<string, List<TaskId>> Owned { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, ITaskRuntime> Runtimes { get; } = new(StringComparer.Ordinal);
    }
}
