// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-group-commit.test.ts, new in Pi v1.5.0 (e5c6182ba, sync
// area group-commit-scheduler). Group commit and the scheduler (docs/group-commit.md §6): decisions read the scheduler's memory in regions
// that wait for the unpublished commits whose keys they read, memberships written at admission, outcomes, and close. Verbatim on
// ControlledStorage over MemoryStorage, as upstream, apart from the adaptations of HarnessSupport.cs, SessionSupport.cs and
// HarnessSchedulerSupport.cs, and these, each for a step upstream's single thread orders and the thread pool does not:
// - Gates and run counts are per case (GroupCommitFixture), not module state cleared after each test. gc.hold is SchedulerGates.Hold.
// - flush() (a macrotask) where a racing job must have reached its gate is a wait for the Session's count of gate hits (Session.GateHits)
//   to grow: the job is at its gate, which stays shut while the call in flight is held. Before holding storage those cases wait until the
//   scheduler is quiet (DurableTaskScheduler.QuiesceAsync: no reservation, reconcile or sweep job scheduled or running, every commit
//   published), so no job an earlier publication queued from the thread pool reaches the gate first and holds the racing job behind it,
//   which upstream's single thread runs before the case goes on. Where a commit must have been admitted behind the
//   call in flight before the case goes on, the case waits for the count of keys with an unpublished writer (Session.LineSizes.Unpublished)
//   to grow. Commits the case itself makes are enqueued in call order, so a line read it starts after one runs after that one's line part.
//   Where upstream flushes for a negative check or only to let work progress, Flush() (a short real delay) stands in.
// - "holds an owner whose end follows a creation below its child's pending end ...": D's creation walks its chain through B, whose end is
//   unpublished, so it waits at B's gate holding the line, and P's end queues behind it (Session.LineBacklog). D is then running once its
//   reservation ran, which is awaited (Eventually) where upstream reads it once P shows completing.
// - "runs a wait continuation of one commit before the next commit of the same call is published": the hop between the two parts of one call
//   (a macrotask upstream, a Task.Yield here; see Writer.cs) orders nothing on the thread pool, so the hop after task one's terminal part
//   waits (Session.PublicationHop, at most 5 s) until the continuation of waitForTask(one) ran.
// - The index sizes are read once the scheduler's pending passes have run (SchedulerTests.Sizes); IndexSizes.Pins is upstream's pins.
//   While the creation's storage call is held, which SchedulerTests.Sizes would wait for, "keeps the nodes a chain load installed ..."
//   waits instead until the sweep its admission scheduled ran its line part (DurableTaskScheduler.SweepScheduled, then Session.LineTickAsync)
//   and reads DurableHarness.SchedulerIndexSizes directly, as upstream.
// - A runtime commit the case makes for a probe (raceMark) uses the probe's ITaskRuntime, exposed by its phase.
// - "reads no task record from Storage for more live and awaited tasks than the record cache's count" is area publication-caches': PiSharp
//   has no record cache (CachedStorage) yet, so the scheduler's reads of awaited outcomes still reach Storage.
// - "ends a task wait when the Harness closes while the terminal commit is stored, and still stores it" is PiSharp's counterpart of
//   harness-tasks.test.ts's case of that name (durable@1.5.0 Changed#6), on a held storage call instead of LateResolvingStorage: that block
//   of harness-tasks ("publication at adoption") is area publication-caches'.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    private static IEnumerable<(string, Func<Task>)> HarnessGroupCommitCases() => HarnessGroupCommitTests.Cases();
}

internal static class HarnessGroupCommitTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly JsonValue Hold1 = O("""{"phase":"hold","n":1}""");

    private static ControlledStorage NewStorage() => new(new MemoryStorage()) { KeepOpenForReopen = true };

    private static async Task<DurableTaskStatus?> StatusAsync(IHarness harness, TaskId id) => (await harness.GetTaskAsync(id, Context))?.State.Status;

    private static DurableHarness Durable(IHarness harness) => (DurableHarness)harness;

    /// <summary>A line read that touches <paramref name="key"/> first and then reports whether <paramref name="created"/>'s creation was
    /// published (upstream's <c>session.readOnLine(async () => { const gate = session.touch(key); ... })</c>).</summary>
    private static Task<bool> ReadAfterTouch(IHarness harness, string key, Func<TaskId?> created, ConcurrentDictionary<TaskId, bool> published)
    {
        var session = Durable(harness);
        return session.ReadOnLineAsync(async () =>
        {
            if (session.Touch(key) is { } gate) await gate;
            return created() is { } id && published.ContainsKey(id);
        });
    }

    /// <summary>The task records each stored publication changed (upstream's <c>followCommits</c> collecting <c>change.value.id</c>).</summary>
    private static ConcurrentDictionary<TaskId, bool> PublishedTasks(IHarness harness)
    {
        var published = new ConcurrentDictionary<TaskId, bool>();
        FollowCommits(harness, (publication, _) =>
        {
            if (publication.IsMemory) return;
            foreach (var change in publication.Changes.OfType<TaskWrite>()) published[change.Value.Id] = true;
        });
        return published;
    }

    /// <summary>Waits until the scheduler has no pass or sweep scheduled or running and every commit is published, so the next storage call
    /// is the case's own and no job a publication queued runs into its gate first (see the file header).</summary>
    private static Task Quiet(IHarness harness) => Durable(harness).Scheduler.QuiesceAsync();

    /// <summary>Waits until a commit admitted after this call registered a key with an unpublished writer.</summary>
    private static async Task AdmittedAfter(IHarness harness, int unpublished) =>
        await Eventually(() => Durable(harness).LineSizes.Unpublished > unpublished);

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "harness.group-commit.";

        // ─── group commit: scheduler regions ────────────────────────────────

        yield return Case(prefix + "holds-a-live-owner-whose-end-races-a-creation-below-its-ended-tasks-live-p-ended-b-and-a-empty-c", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            harness.Resume();
            var tree = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var a = await tx.CreateTaskAsync(fixture.Hold, Named("a"), OwnedBy(b));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                return (P: p, B: b, A: a, C: c.Id);
            }, Context);
            fixture.Gates.Open("a");
            await harness.WaitForTaskAsync(tree.A, Context);
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(tree.B, Context);
            // The creation of D in C is admitted and in flight when P commits its end.
            await Quiet(harness);
            var held = storage.HoldCommits();
            var c = (await harness.ConversationAsync(tree.C, Context))!;
            var creating = c.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation()), Context);
            await held.Entered;
            // P's decision reads what the creation writes, so it waits for its publication, holding the line.
            var hits = Durable(harness).GateHits;
            fixture.Gates.Open("p");
            await Eventually(() => Durable(harness).GateHits > hits);
            held.Release();
            var d = await creating;
            await Eventually(async () => await StatusAsync(harness, tree.P) == DurableTaskStatus.Completing);
            fixture.Gates.Open("d");
            await harness.WaitForTaskAsync(d, Context);
            Equal("completed", await OutcomeOf(harness, tree.P), "p outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "holds-an-owner-whose-end-follows-a-creation-below-its-childs-pending-end-live-p-ending-b-conversation-c", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            harness.Resume();
            var tree = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                return (P: p, B: b, C: c.Id);
            }, Context);
            var c = (await harness.ConversationAsync(tree.C, Context))!;
            await Eventually(async () => await StatusAsync(harness, tree.B) == DurableTaskStatus.Running);
            // A call in flight, so what follows is admitted and stays unpublished, in this order.
            await Quiet(harness);
            var held = storage.HoldCommits();
            var blocker = root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            await held.Entered;
            // B ends; then D is created in C while B still shows live; then P ends.
            var unpublished = Durable(harness).LineSizes.Unpublished;
            fixture.Gates.Open("b");
            await AdmittedAfter(harness, unpublished);
            var hits = Durable(harness).GateHits;
            var creating = c.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation()), Context);
            // D's memberships walk through B, whose end is unpublished: the creation waits at B's gate, holding the line.
            await Eventually(() => Durable(harness).GateHits > hits);
            fixture.Gates.Open("p");
            await Eventually(() => Durable(harness).LineBacklog >= 1);
            held.Release();
            await blocker;
            var d = await creating;
            // In line order D's creation precedes P's decision: P holds while D is live.
            await Eventually(async () => await StatusAsync(harness, tree.P) == DurableTaskStatus.Completing);
            await Eventually(async () => await StatusAsync(harness, d) == DurableTaskStatus.Running);
            fixture.Gates.Open("d");
            Equal("completed", await OutcomeOf(harness, tree.P), "p outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-a-child-before-its-next-phase-when-its-owners-abort-mark-is-unpublished-at-its-step", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold, fixture.TwoPhases])).Harness;
            var root = await harness.RootAsync(Context);
            harness.Resume();
            var (owner, child) = await root.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(fixture.Hold, Named("o"), OwnConversation());
                var child = await tx.CreateTaskAsync(fixture.TwoPhases, Named("t"), OwnedBy(owner));
                return (owner, child);
            }, Context);
            await Eventually(async () =>
            {
                var record = await harness.GetTaskAsync(child, Context);
                return record?.State is RunningTaskState running && S(((JsonObjectValue)running.Checkpoint)["phase"]) == "two";
            });
            await Quiet(harness);
            var held = storage.HoldCommits();
            var marking = harness.AbortTaskAsync(owner, Context);
            await held.Entered;
            // The child's phase returns: its step reads the intent set, which the unpublished mark changes.
            var hits = Durable(harness).GateHits;
            fixture.Gates.Open("t.return");
            await Eventually(() => Durable(harness).GateHits > hits);
            Equal(0, fixture.Runs("t.two"), "t.two before the mark");
            held.Release();
            Equal(TaskAbortResult.Marked, await marking, "marking");
            Equal("aborted", await OutcomeOf(harness, child), "child outcome");
            Equal("aborted", await OutcomeOf(harness, owner), "owner outcome");
            Equal(0, fixture.Runs("t.two"), "t.two");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "refuses-the-second-of-two-opposite-waits-admitted-behind-each-other", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Waits])).Harness;
            var root = await harness.RootAsync(Context);
            var (x, y) = await root.CommitAsync(async tx =>
            {
                var x = await tx.CreateTaskAsync(fixture.Waits, Named("x"), OwnConversation());
                var y = await tx.CreateTaskAsync(fixture.Waits, Named("y"), OwnConversation());
                return (x, y);
            }, Context);
            // Each waits on the other, named in a memo.
            await SetTasks(harness, async tx =>
            {
                // Every table read before the first write.
                var records = new[] { (await tx.GetTaskAsync(x))!, (await tx.GetTaskAsync(y))! };
                tx.SetTask(records[0] with { Memos = Obj(("on", y.ToJson())) });
                tx.SetTask(records[1] with { Memos = Obj(("on", x.ToJson())) });
            });
            harness.Resume();
            await Eventually(async () => await StatusAsync(harness, x) == DurableTaskStatus.Running &&
                await StatusAsync(harness, y) == DurableTaskStatus.Running);
            await Quiet(harness);
            var held = storage.HoldCommits();
            fixture.Gates.Open("x");
            await held.Entered;
            // Y's wait reads X's record, which X's unpublished wait changes.
            var hits = Durable(harness).GateHits;
            fixture.Gates.Open("y");
            await Eventually(() => Durable(harness).GateHits > hits);
            held.Release();
            await harness.WaitForTaskAsync(y, Context);
            Check(fixture.Refusals.TryGetValue("y", out var refusal) && refusal.ToString().Contains("wait cycle"),
                $"y refused for a wait cycle: {refusal}");
            Check(!fixture.Refusals.ContainsKey("x"), "x not refused");
            Equal("completed", await OutcomeOf(harness, x), "x outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "starts-no-invocation-on-a-reservation-before-the-reservation-is-published", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("r"), OwnConversation()), Context);
            // The reservation's write is in flight: a scheduler commit with writes is never delivered before it is published.
            var held = storage.HoldCommits();
            harness.Resume();
            await held.Entered;
            await Flush();
            Equal(0, fixture.Runs("r"), "runs before the reservation is published");
            held.Release();
            await Eventually(() => fixture.Runs("r") == 1);
            Equal(DurableTaskStatus.Running, await StatusAsync(harness, id), "status");
            fixture.Gates.Open("r");
            await harness.WaitForTaskAsync(id, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-a-migration-once-although-its-reservation-waits-for-an-unpublished-write-of-the-task", async () =>
        {
            var storage = NewStorage();
            var migrations = 0;
            var v2 = Tasks.DefineTask(new TaskDefinition
            {
                Name = "gc.migrated",
                Version = 2,
                Initial = _ => O("""{"phase":"hold"}"""),
                Migrate = (input, checkpoint, _) =>
                {
                    Interlocked.Increment(ref migrations);
                    return new TaskMigrationResult(input, checkpoint);
                },
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["hold"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "v2", Tasks = [v2] }));
            var harness = (await OpenTasks(storage, [], registry)).Harness;
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(v2, Named("m"), OwnConversation()), Context);
            // Stored at version 1, and written again by a commit that is in flight when the reservation reads it.
            await Quiet(harness);
            var held = storage.HoldCommits();
            var writing = SetTasks(harness, async tx =>
            {
                var record = (await tx.GetTaskAsync(id))!;
                tx.SetTask(record with { Version = 1, Memos = Obj(("touched", true)) });
            });
            await held.Entered;
            var hits = Durable(harness).GateHits;
            harness.Resume();
            // The reservation reads the task, whose write is unpublished: it waits at the task's gate.
            await Eventually(() => Durable(harness).GateHits > hits);
            Equal(0, Volatile.Read(ref migrations), "migrations before the write is published");
            held.Release();
            await writing;
            Equal("completed", await OutcomeOf(harness, id), "outcome");
            Equal(1, Volatile.Read(ref migrations), "migrations");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-a-failed-migration-once-although-its-reservation-waits-for-an-unpublished-write-of-the-task", async () =>
        {
            var storage = NewStorage();
            var migrations = 0;
            var v2 = Tasks.DefineTask(new TaskDefinition
            {
                Name = "gc.unmigratable",
                Version = 2,
                Initial = _ => O("""{"phase":"hold"}"""),
                Migrate = (_, _, _) =>
                {
                    Interlocked.Increment(ref migrations);
                    throw new InvalidOperationException("cannot migrate");
                },
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["hold"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "v2", Tasks = [v2] }));
            var opened = await OpenTasks(storage, [], registry);
            var harness = opened.Harness;
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(v2, Named("m"), OwnConversation()), Context);
            var held = storage.HoldCommits();
            var writing = SetTasks(harness, async tx =>
            {
                var record = (await tx.GetTaskAsync(id))!;
                tx.SetTask(record with { Version = 1 });
            });
            await held.Entered;
            harness.Resume();
            await Flush();
            held.Release();
            await writing;
            await Eventually(() => Volatile.Read(ref migrations) > 0);
            await Flush();
            Equal(1, Volatile.Read(ref migrations), "migrations");
            Equal(1, opened.Infos.Snapshot().Count(info => info.Kind == ReportKind.MigrationFailed), "migration_failed reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-reader-of-the-roots-wait-for-a-creation-under-a-root-a-pending-end-would-unlist", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            harness.Resume();
            // Root R is listed for its last live task, T.
            var last = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("t"), OwnConversation()), Context);
            await Eventually(async () => await StatusAsync(harness, last) == DurableTaskStatus.Running);
            var published = PublishedTasks(harness);
            // T's end is in flight; a creation under R is admitted after it, while R still shows listed.
            var held = storage.HoldCommits();
            fixture.Gates.Open("t");
            await held.Entered;
            var created = new StrongBox<TaskId?>();
            var creating = root.CommitAsync(async tx =>
            {
                var id = await tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation());
                lock (created) created.Value = id;
                return id;
            }, Context);
            // A reader of the roots on the line (global idle's key), queued after the creation, waits until the creation is published too.
            var reading = ReadAfterTouch(harness, LineKeys.Roots, () => { lock (created) return created.Value; }, published);
            held.Release();
            await creating;
            Check(await reading, "the reader saw the creation published");
            fixture.Gates.Open("d");
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "walks-a-creations-memberships-past-an-ancestor-whose-pending-end-is-unpublished", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            harness.Resume();
            // Live P, in the root conversation, owns the empty conversation C.
            var (p, c) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(p));
                return (p, c.Id);
            }, Context);
            await Eventually(async () => await StatusAsync(harness, p) == DurableTaskStatus.Running);
            var published = PublishedTasks(harness);
            // P's end is in flight; a creation in C is admitted after it. Its chain must not stop at P, which shows live.
            var conversation = (await harness.ConversationAsync(c, Context))!;
            var held = storage.HoldCommits();
            fixture.Gates.Open("p");
            await held.Entered;
            var created = new StrongBox<TaskId?>();
            var creating = conversation.CommitAsync(async tx =>
            {
                var id = await tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation());
                lock (created) created.Value = id;
                return id;
            }, Context);
            var reading = ReadAfterTouch(harness, LineKeys.Roots, () => { lock (created) return created.Value; }, published);
            held.Release();
            await creating;
            Check(await reading, "the reader saw the creation published");
            fixture.Gates.Open("d");
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reserves-the-live-work-of-the-owner-above-an-ancestor-whose-end-is-pending-for-a-creation-below-it", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            harness.Resume();
            // Live G owns P, which owns the empty conversation C.
            var (g, p, c) = await root.CommitAsync(async tx =>
            {
                var g = await tx.CreateTaskAsync(fixture.Hold, Named("g"), OwnConversation());
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnedBy(g));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(p));
                return (g, p, c.Id);
            }, Context);
            await Eventually(async () => await StatusAsync(harness, p) == DurableTaskStatus.Running);
            var published = PublishedTasks(harness);
            // P's end is in flight; a creation in C is admitted after it, while P still shows live.
            var conversation = (await harness.ConversationAsync(c, Context))!;
            var held = storage.HoldCommits();
            fixture.Gates.Open("p");
            await held.Entered;
            var created = new StrongBox<TaskId?>();
            var creating = conversation.CommitAsync(async tx =>
            {
                var id = await tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation());
                lock (created) created.Value = id;
                return id;
            }, Context);
            // A reader of G's live work (as G's end decision is) waits until the creation is published too.
            var reading = ReadAfterTouch(harness, LineKeys.Owned(g), () => { lock (created) return created.Value; }, published);
            held.Release();
            await creating;
            Check(await reading, "the reader saw the creation published");
            fixture.Gates.Open("d");
            fixture.Gates.Open("g");
            await harness.WaitForTaskAsync(g, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-nodes-a-chain-load-installed-for-an-unpublished-creation-through-the-sweep", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var opened = await OpenTasks(storage, [fixture.Hold]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            var (b, cId) = await root.CommitAsync(async tx =>
            {
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnConversation());
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                return (b, c.Id);
            }, Context);
            fixture.Gates.Open("b");
            await opened.Harness.WaitForTaskAsync(b, Context);
            await opened.Harness.CloseAsync(Context);
            // After reopen, B and C's edge are known only once a chain load reads them.
            opened = await OpenTasks(storage.Reopen(), [fixture.Hold]);
            var harness = opened.Harness;
            Equal(0, (await Sizes(harness)).Settled, "settled after reopen");
            var c = (await harness.ConversationAsync(cId, Context))!;
            var held = storage.HoldCommits();
            var creating = c.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation()), Context);
            await held.Entered;
            // The admission loaded B; the sweep that load scheduled has run on the line since, and kept it.
            await harness.WaitForIdleAsync(Context);
            await Eventually(() => !Durable(harness).Scheduler.SweepScheduled);
            await Durable(harness).LineTickAsync();
            var during = Durable(harness).SchedulerIndexSizes;
            Equal(1, during.Settled, "settled while the creation is unpublished");
            Check(during.Pins > 0, $"pins while the creation is unpublished: {during.Pins}");
            held.Release();
            await creating;
            // Published below a known chain: nothing is left to load, and no pin is left.
            var after = await Sizes(harness);
            Equal(0, after.Unloaded, "unloaded");
            Equal(0, after.Pins, "pins");
            Check(!await Settled(harness.WaitForIdleAsync(Context)), "busy while d is live");
            harness.Resume();
            fixture.Gates.Open("d");
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        });

        // ─── group commit: records a decision copies ────────────────────────

        yield return Case(prefix + "marks-a-child-in-a-cascade-from-its-current-record-not-one-its-pending-commit-replaces", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Probe])).Harness;
            var root = await harness.RootAsync(Context);
            var (parent, child) = await root.CommitAsync(async tx =>
            {
                var parent = await tx.CreateTaskAsync(fixture.Probe, Named("p"), OwnConversation());
                var child = await tx.CreateTaskAsync(fixture.Probe, Named("c"), OwnedBy(parent));
                return (parent, child);
            }, Context);
            harness.Resume();
            await Eventually(() => fixture.Runtimes.ContainsKey("c"));
            await Flush();
            await fixture.RaceMark(storage, harness, "c", async () => await harness.AbortTaskAsync(parent, Context));
            await Eventually(async () => (await harness.GetTaskAsync(child, Context))?.AbortRequested == true);
            Equal(1.0, CheckpointN(await harness.GetTaskAsync(child, Context)), "child checkpoint n");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "marks-the-other-tasks-of-a-failfast-wait-from-their-current-records", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Probe, fixture.Waiter])).Harness;
            var root = await harness.RootAsync(Context);
            await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Waiter, JsonValue.Null, OwnConversation()), Context);
            harness.Resume();
            await Eventually(() => fixture.Runtimes.ContainsKey("x") && fixture.Runtimes.ContainsKey("y"));
            await Flush();
            var y = fixture.Runtimes["y"].TaskId;
            // X fails while its commit is in flight; the pass that marks Y runs while Y's own commit is pending.
            await fixture.RaceMark(storage, harness, "y", () =>
            {
                fixture.Gates.Open("x");
                return Task.CompletedTask;
            });
            await Eventually(async () => (await harness.GetTaskAsync(y, Context))?.AbortRequested == true);
            Equal(1.0, CheckpointN(await harness.GetTaskAsync(y, Context)), "y checkpoint n");
            await harness.CloseAsync(Context);
        });

        // ─── group commit: outcomes and publication through the Harness ─────

        yield return Case(prefix + "finds-the-submission-an-unpublished-commit-admitted-with-the-same-request-id", async () =>
        {
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [])).Harness;
            var root = await harness.RootAsync(Context);
            var draft = SubmissionDraft.Write(new EntryDraft { Kind = "note", Data = JsonObjectValue.Empty }, "r1");
            await Quiet(harness);
            var held = storage.HoldCommits();
            var first = root.SubmitAsync(draft, Context);
            await held.Entered;
            // The second reads the request ID the first wrote: it waits at its gate.
            var hits = Durable(harness).GateHits;
            var second = root.SubmitAsync(draft, Context);
            await Eventually(() => Durable(harness).GateHits > hits);
            held.Release();
            Equal((await first).Id, (await second).Id, "submission");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-a-wait-continuation-of-one-commit-before-the-next-commit-of-the-same-call-is-published", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            var other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var one = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("one"), OwnConversation()), Context);
            var two = await other.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("two"), OwnConversation()), Context);
            harness.Resume();
            await Eventually(async () => await StatusAsync(harness, one) == DurableTaskStatus.Running &&
                await StatusAsync(harness, two) == DurableTaskStatus.Running);
            var ended = new ConcurrentDictionary<TaskId, bool>();
            FollowCommits(harness, (publication, _) =>
            {
                if (publication.IsMemory) return;
                foreach (var change in publication.Changes.OfType<TaskWrite>())
                    if (change.Value.State.Status == DurableTaskStatus.Terminal) ended[change.Value.Id] = true;
            });
            // Both end in one call, behind a commit in flight.
            await Quiet(harness);
            var held = storage.HoldCommits();
            var blocker = root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            await held.Entered;
            bool? seen = null;
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waited = harness.WaitForTaskAsync(one, Context).ContinueWith(_ =>
            {
                seen = ended.ContainsKey(two);
                resumed.TrySetResult();
            }, TaskScheduler.Default);
            // The hop after one's terminal part waits until what it resolved ran (see the file header).
            Durable(harness).PublicationHop = part =>
                part.Spec.Writes?.Any(write => write is TaskWrite { Value: { State.Status: DurableTaskStatus.Terminal } record } && record.Id == one) == true
                    ? Task.WhenAny(resumed.Task, Task.Delay(5_000))
                    : Task.CompletedTask;
            var unpublished = Durable(harness).LineSizes.Unpublished;
            fixture.Gates.Open("one");
            await AdmittedAfter(harness, unpublished);
            unpublished = Durable(harness).LineSizes.Unpublished;
            fixture.Gates.Open("two");
            await AdmittedAfter(harness, unpublished);
            held.Release();
            await blocker;
            await waited;
            await harness.WaitForTaskAsync(two, Context);
            Equal(false, seen, "two's end published when one's wait resumed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "signals-an-invocation-when-it-closes-during-a-call-in-flight-and-stores-that-call-before-storage-closes", async () =>
        {
            var storage = NewStorage();
            var signalled = new Deferred();
            var listens = Tasks.DefineTask(new TaskDefinition
            {
                Name = "gc.listens",
                Version = 1,
                Initial = _ => O("""{"phase":"wait"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = async (_, runtime, _) =>
                    {
                        try { await Aborted(runtime.Signal); }
                        catch (OperationCanceledException) { signalled.Resolve(); }
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var harness = (await OpenTasks(storage, [listens])).Harness;
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(listens, JsonValue.Null, OwnConversation()), Context);
            harness.Resume();
            await Eventually(async () => await StatusAsync(harness, id) == DurableTaskStatus.Running);
            var held = storage.HoldCommits();
            var entry = root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            await held.Entered;
            var closing = harness.CloseAsync(Context);
            await signalled.Task;
            Check(!await Settled(closing), "close waits for the call in flight");
            held.Release();
            await entry;
            await closing;
            var reopened = (await OpenTasks(storage.Reopen(), [listens])).Harness;
            var conversation = (await reopened.ConversationAsync(root.Id, Context))!;
            Check((await conversation.EntriesAsync(new EntryQueryBounds(), 10, null, Context)).Items.Any(item => item.Kind == "note"), "the note is stored");
            // Close wrote the task it stopped back as pending.
            Equal(DurableTaskStatus.Pending, await StatusAsync(reopened, id), "status after reopen");
            await reopened.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-a-task-wait-when-the-harness-closes-while-the-terminal-commit-is-stored-and-still-stores-it", async () =>
        {
            var fixture = new GroupCommitFixture();
            var storage = NewStorage();
            var harness = (await OpenTasks(storage, [fixture.Hold])).Harness;
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("end"), OwnConversation()), Context);
            harness.Resume();
            await Eventually(async () => await StatusAsync(harness, id) == DurableTaskStatus.Running);
            var waited = Catch<TaskRecord>(() => harness.WaitForTaskAsync(id, Context));
            // The terminal commit is the call in flight when close starts.
            var held = storage.HoldCommits();
            fixture.Gates.Open("end");
            await held.Entered;
            var closing = harness.CloseAsync(Context);
            // Close ends the waits before it drains the commit in flight, which is published and stored all the same.
            Check((await waited)?.ToString()?.Contains("closed") == true, $"the wait ends with the closed error: {await waited}");
            Check(!await Settled(closing), "close waits for the call in flight");
            held.Release();
            await closing;
            Equal(SessionEndKind.Closed, (await harness.Closed).Kind, "closed");
            var reopened = (await OpenTasks(storage.Reopen(), [fixture.Hold])).Harness;
            Equal(DurableTaskStatus.Terminal, await StatusAsync(reopened, id), "status after reopen");
            await reopened.CloseAsync(Context);
        });
    }

    /// <summary>The checkpoint's <c>n</c> of a running record (upstream <c>toMatchObject({ checkpoint: { n } })</c>).</summary>
    private static double CheckpointN(TaskRecord? record) =>
        record?.State is LiveTaskState live && ((JsonObjectValue)live.Checkpoint).TryGetValue("n", out var n) ? N(n) : double.NaN;

    /// <summary>The value or the error of <paramref name="run"/> (upstream's <c>.then(value => value, error => error)</c>).</summary>
    private static async Task<object?> Catch<T>(Func<Task<T>> run)
    {
        try { return await run(); }
        catch (Exception error) { return error; }
    }
}

/// <summary>One case's gates, run counts, refusals, probe runtimes and task definitions (upstream's module state of
/// harness-group-commit.test.ts).</summary>
internal sealed class GroupCommitFixture
{
    private readonly ConcurrentDictionary<string, int> runs = new(StringComparer.Ordinal);

    public GroupCommitFixture()
    {
        Hold = Gates.Hold("gc.hold");
        // Commits its next phase, returns once <name>.return opens; the next phase counts its run and completes (upstream TwoPhases).
        TwoPhases = Tasks.DefineTask(new TaskDefinition
        {
            Name = "gc.two-phases",
            Version = 1,
            Initial = _ => SessionTests.O("""{"phase":"one"}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["one"] = async (task, runtime, ctx) =>
                {
                    await runtime.CommitAsync((_, _) => NextTaskState.Running(SessionTests.O("""{"phase":"two"}""")), ctx);
                    await Gates.Gate($"{SchedulerTests.NameOf(task.Input)}.return").Task;
                },
                ["two"] = async (task, runtime, ctx) =>
                {
                    Count($"{SchedulerTests.NameOf(task.Input)}.two");
                    await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx);
                },
            },
            Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
        });
        // Once its gate opens, waits on the task in its memo; records why a refused wait was refused, then completes (upstream Waits).
        Waits = Tasks.DefineTask(new TaskDefinition
        {
            Name = "gc.waits",
            Version = 1,
            Initial = _ => SessionTests.O("""{"phase":"wait"}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["wait"] = async (task, runtime, ctx) =>
                {
                    var name = SchedulerTests.NameOf(task.Input);
                    await Gates.Gate(name).Task;
                    var on = new TaskId((long)SessionTests.N(await runtime.MemoAsync("on", ctx)));
                    try
                    {
                        await runtime.CommitAsync((_, _) => NextTaskState.Waiting(SessionTests.O("""{"phase":"done"}"""), [on], JoinPolicy.AllSettled), ctx);
                    }
                    catch (Exception error)
                    {
                        Refusals[name] = error;
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx);
                    }
                },
                ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx),
            },
            Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
        });
        // Holds until aborted, its runtime exposed; fails once its gate opens. Its abort handler holds too (upstream Probe).
        Probe = Tasks.DefineTask(new TaskDefinition
        {
            Name = "gc.probe",
            Version = 1,
            Initial = _ => SessionTests.O("""{"phase":"hold","n":0}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["hold"] = async (task, runtime, ctx) =>
                {
                    var name = SchedulerTests.NameOf(task.Input);
                    Runtimes[name] = runtime;
                    var failing = Gates.Gate(name).Task;
                    var aborted = UntilAborted(runtime.Signal);
                    if (await Task.WhenAny(failing, aborted) != failing) return;
                    await runtime.CommitAsync((_, _) => NextTaskState.Failed("gate"), ctx);
                },
            },
            Abort = (_, runtime, _) => UntilAborted(runtime.Signal),
        });
        // Creates probes x and y and waits on them failFast (upstream Waiter).
        Waiter = Tasks.DefineTask(new TaskDefinition
        {
            Name = "gc.fail-fast",
            Version = 1,
            Initial = _ => SessionTests.O("""{"phase":"spawn"}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["spawn"] = (_, runtime, ctx) => runtime.CommitAsync(async (tx, current) =>
                {
                    var on = new List<TaskId>();
                    foreach (var name in new[] { "x", "y" })
                        on.Add(await tx.CreateTaskAsync(Probe, SchedulerTests.Named(name), SchedulerTests.OwnedBy(current.Id)));
                    return (TaskState?)NextTaskState.Waiting(SessionTests.O("""{"phase":"done"}"""), on, JoinPolicy.FailFast);
                }, ctx),
                ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx),
            },
            Abort = (_, runtime, _) => UntilAborted(runtime.Signal),
        });
    }

    public SchedulerGates Gates { get; } = new();

    /// <summary>Upstream <c>Hold</c> (gc.hold).</summary>
    public DurableTask Hold { get; }

    /// <summary>Upstream <c>TwoPhases</c>.</summary>
    public DurableTask TwoPhases { get; }

    /// <summary>Upstream <c>Waits</c>.</summary>
    public DurableTask Waits { get; }

    /// <summary>Upstream <c>Probe</c>.</summary>
    public DurableTask Probe { get; }

    /// <summary>Upstream <c>Waiter</c> (gc.fail-fast).</summary>
    public DurableTask Waiter { get; }

    /// <summary>Why each refused wait was refused, by task name (upstream <c>refusals</c>).</summary>
    public ConcurrentDictionary<string, Exception> Refusals { get; } = new(StringComparer.Ordinal);

    /// <summary>Runtimes of running probes, by name, so a case commits for them (upstream <c>runtimes</c>).</summary>
    public ConcurrentDictionary<string, ITaskRuntime> Runtimes { get; } = new(StringComparer.Ordinal);

    /// <summary>How often a named phase ran (upstream <c>ran(name)</c>): gc.hold's runs by task name, and the counts of <see cref="Count"/>.</summary>
    public int Runs(string name) => runs.GetValueOrDefault(name) + Gates.Runs(name);

    private void Count(string name) => runs.AddOrUpdate(name, 1, (_, count) => count + 1);

    /// <summary>Completes once <paramref name="signal"/> aborts (upstream <c>untilAborted</c>, which resolves rather than rejects).</summary>
    private static Task UntilAborted(CancellationToken signal)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.Register(() => done.TrySetResult());
        return done.Task;
    }

    /// <summary>Advances probe <paramref name="name"/> to <c>n = 1</c> while the mark-writing commit is in flight, and runs a reconcile pass
    /// meanwhile (upstream <c>raceMark</c>).</summary>
    public async Task RaceMark(ControlledStorage storage, IHarness harness, string name, Func<Task> mark)
    {
        var heldMark = storage.HoldCommits();
        var marking = mark();
        await heldMark.Entered;
        var prepared = false;
        var advancing = Runtimes[name].CommitAsync((_, _) =>
        {
            Volatile.Write(ref prepared, true);
            return (TaskState?)NextTaskState.Running(SessionTests.O("""{"phase":"hold","n":1}"""));
        }, DurableContext.Background);
        await HarnessTests.Eventually(() => Volatile.Read(ref prepared));
        await HarnessTests.Flush();
        // Global idle schedules a reconcile pass, which reaches the line while both are unpublished.
        _ = harness.WaitForIdleAsync(DurableContext.Background).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        await HarnessTests.Flush();
        var heldAdvance = storage.HoldCommits();
        heldMark.Release();
        await heldAdvance.Entered;
        await HarnessTests.Flush();
        heldAdvance.Release();
        await Task.WhenAll(marking, advancing);
        await HarnessTests.Flush();
    }
}
