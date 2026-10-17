// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-ownership.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs, ChatSupport.cs and HarnessOwnershipSupport.cs, and these:
// - Backends. Upstream runs these on MemoryStorage and reopens a SQLite file for the reopen cases. HarnessOwnershipCases() does the same
//   (ids harness.ownership.<test>); HarnessOwnershipJsonlCases() reruns every case on JSONL, reopen cases reopening a JSONL directory;
//   HarnessOwnershipSqliteCases() reruns the cases that need no reopen on a fresh SQLite file (its reopen cases would repeat the
//   MemoryStorage group's).
// - The parameterized "derives marks a crash left unapplied below ... at open" is one case per parameter.
// - "rejects a handle operation queued on the line ...": upstream calls abortTask, whose mark commit queues behind the held commit at once,
//   and then lets the tool queue its submit. Handlers here run on other threads, so the case waits until the mark is queued on the Session
//   line (Session.LineBacklog, a deterministic barrier) before it lets the tool go.
// - Tools and supervisors append the IDs they see from other threads, so those lists are SyncLists.
// - Pi v1.2.0's queuedNote: writes queued while busy carry a model message, as a data-only write is appended at once (area i).
// - Pi v1.2.0's two cases that count Set iterations (upstream patches Set.prototype[Symbol.iterator]) count the members the scheduler's own
//   sets yield (DurableTaskScheduler.SetEntries), under the same bounds, after the scheduler's pending passes have run (QuiesceAsync).
//   "finalizes many held siblings ..." starts counting before the leaves' commit, so its count also covers the tracking that upstream's
//   leaves out, and first waits until every sibling holds.
// - "tracks ownership chains deeper than the call stack" (20,000 tasks in one commit) runs on MemoryStorage only, as upstream's does.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static HarnessTests;
using static OwnershipTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessOwnershipCases() =>
        OwnershipTests.Cases("harness.ownership.", () => Task.FromResult<IStorage>(new MemoryStorage()), OwnershipSqliteLocation);

    private static IEnumerable<(string, Func<Task>)> HarnessOwnershipJsonlCases() =>
        OwnershipTests.Cases("harness.ownership.jsonl.", () => OwnershipJsonlLocation()(), OwnershipJsonlLocation);

    private static IEnumerable<(string, Func<Task>)> HarnessOwnershipSqliteCases() =>
        OwnershipTests.Cases("harness.ownership.sqlite.", () => OwnershipSqliteLocation()(), null);

    private static StructuredTests.OpenLocation OwnershipSqliteLocation()
    {
        var path = Path.Combine(Temp("ownership-sqlite"), "session.sqlite");
        return async () => await SqliteStorage.OpenAsync(path);
    }

    private static StructuredTests.OpenLocation OwnershipJsonlLocation()
    {
        var directory = Temp("ownership-jsonl");
        return async () => await JsonlStorage.OpenAsync(directory, DurableContext.Background);
    }
}

internal static partial class OwnershipTests
{
    /// <summary>A write that affects model context, so it queues while busy; a data-only write would be appended at once (upstream v1.2.0
    /// <c>queuedNote</c>).</summary>
    private static EntryDraft QueuedNote(string text) =>
        new() { Kind = "note", Model = JsonValue.Array(Obj(("role", "user"), ("content", text), ("timestamp", 0))) };

    /// <summary>
    /// The harness.ownership.* cases on storages from <paramref name="fresh"/>; reopen cases use locations from <paramref name="reopenable"/>
    /// and are left out without one.
    /// </summary>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh, Func<StructuredTests.OpenLocation>? reopenable)
    {
        IEnumerable<(string, Func<Task>)> all = [.. Ownership(prefix, fresh), .. OwnedConversations(prefix, fresh)];
        if (reopenable is not null) all = [.. all, .. OwnershipReopen(prefix, reopenable), .. OwnedConversationsReopen(prefix, reopenable)];
        return all;
    }

    // ─── ownership ──────────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> Ownership(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "keeps-a-conversation-busy-while-its-owned-foreground-subtree-has-live-work-holding-the-completed-owner", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "owner");
            holds.Open("owner");
            await Eventually(async () => await StatusOf(harness, tree.Owner) == DurableTaskStatus.Completing);
            var idle = root.WaitForIdleAsync(Context);
            Check(!await Settled(idle), "root still busy");
            Check(!await Settled(harness.WaitForIdleAsync(Context)), "harness still busy");
            holds.Open("owner.inner");
            await idle;
            await harness.WaitForIdleAsync(Context);
            Equal("completed", HeldOutcome(await StateOf(harness, tree.Owner)), "owner outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stops-idle-traversal-at-a-background-owner", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "background", background: true);
            await root.WaitForIdleAsync(Context);
            await harness.WaitForIdleAsync(Context);
            // The background child is its own scope.
            var child = (await harness.ConversationAsync(tree.Child, Context))!;
            Check(!await Settled(child.WaitForIdleAsync(Context)), "background child busy");
            await child.AbortAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cascades-an-abort-mark-to-the-owned-foreground-subtree-and-withdraws-its-queued-inputs", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "owner");
            var nested = (await harness.ConversationAsync(tree.Child, Context))!;
            var deeper = await holds.OwnedChild(nested, "deeper");
            var shielded = await holds.OwnedChild(nested, "shielded", background: true);
            // Busy conversations queue submissions; the inner task stands in for the child's run.
            await Busy(nested, tree.Inner);
            var queued = (await nested.SubmitAsync(SubmissionDraft.Input("later"), Context)).Id;
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(tree.Owner, Context), "abort");
            foreach (var id in new[] { tree.Owner, tree.Inner, deeper.Owner, deeper.Inner })
                Equal("aborted", await OutcomeOf(harness, id), $"task {id}");
            // A nested background owner is a boundary.
            Check(await StatusOf(harness, shielded.Owner) != DurableTaskStatus.Terminal, "shielded owner live");
            Check(await StatusOf(harness, shielded.Inner) != DurableTaskStatus.Terminal, "shielded inner live");
            UnansweredAborted(await RecordOf(harness, queued), "queued input");
            await harness.AbortTaskAsync(shielded.Owner, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cascades-a-failed-owner-but-not-a-completed-one", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var failed = await holds.OwnedChild(root, "failed");
            var completedTree = await holds.OwnedChild(root, "done");
            holds.Open("failed", Ending.Failed);
            holds.Open("done");
            Equal("aborted", await OutcomeOf(harness, failed.Inner), "failed inner");
            Equal("failed", await OutcomeOf(harness, failed.Owner), "failed owner");
            await Eventually(async () => await StatusOf(harness, completedTree.Owner) == DurableTaskStatus.Completing);
            Check(await StatusOf(harness, completedTree.Inner) != DurableTaskStatus.Terminal, "completed owner's inner live");
            holds.Open("done.inner");
            Equal("completed", await OutcomeOf(harness, completedTree.Owner), "completed owner");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-background-task-directly-with-its-ordinary-subtree", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "background", background: true);
            await harness.AbortTaskAsync(tree.Owner, Context);
            Equal("aborted", await OutcomeOf(harness, tree.Inner), "inner");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-conversation-queued-inputs-withdrawn-writes-kept-foreground-work-aborted-background-kept", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var foreground = await holds.OwnedChild(root, "foreground");
            var background = await holds.OwnedChild(root, "background", background: true);
            await Busy(root, foreground.Owner);
            var input = (await root.SubmitAsync(SubmissionDraft.Input("later"), Context)).Id;
            var write = (await root.SubmitAsync(SubmissionDraft.Write(QueuedNote("w")), Context)).Id;
            await root.AbortAsync(Context);
            foreach (var id in new[] { foreground.Owner, foreground.Inner })
                Equal(DurableTaskStatus.Terminal, await StatusOf(harness, id), $"task {id}");
            Check(await StatusOf(harness, background.Owner) != DurableTaskStatus.Terminal, "background owner live");
            Check(await StatusOf(harness, background.Inner) != DurableTaskStatus.Terminal, "background inner live");
            Equal("aborted", (await RecordOf(harness, input)).Reason, "input reason");
            Equal(SubmissionStatus.Queued, (await RecordOf(harness, write)).Status, "write status");
            await harness.AbortTaskAsync(background.Owner, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-work-created-below-a-held-failed-owner-but-not-below-a-terminal-one", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "owner", slowInner: true);
            holds.Open("owner", Ending.Failed);
            await Eventually(async () => (await harness.GetTaskAsync(tree.Inner, Context))!.AbortRequested);
            Equal(DurableTaskStatus.Completing, await StatusOf(harness, tree.Owner), "owner status");
            var child = (await harness.ConversationAsync(tree.Child, Context))!;
            Task<TaskId> Create(string name) => child.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input(name), OwnConversation()), Context);
            var during = await Create("during");
            Equal("aborted", await OutcomeOf(harness, during), "during");
            holds.Open("abort.owner.inner");
            Equal("failed", await OutcomeOf(harness, tree.Owner), "owner outcome");
            // A terminal owner never cascades: interrogating its conversation runs normally.
            var after = await Create("after");
            holds.Open("after");
            Equal("completed", await OutcomeOf(harness, after), "after");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "withdraws-queued-inputs-below-the-aborted-task-but-keeps-its-own-conversations-queue-and-queued-writes", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "owner");
            var child = (await harness.ConversationAsync(tree.Child, Context))!;
            await Busy(root, tree.Owner);
            await Busy(child, tree.Inner);
            var own = (await root.SubmitAsync(SubmissionDraft.Input("own"), Context)).Id;
            var below = (await child.SubmitAsync(SubmissionDraft.Input("below"), Context)).Id;
            var write = (await child.SubmitAsync(SubmissionDraft.Write(QueuedNote("w")), Context)).Id;
            await harness.AbortTaskAsync(tree.Owner, Context);
            await harness.WaitForTaskAsync(tree.Inner, Context);
            Equal(SubmissionStatus.Queued, (await RecordOf(harness, own)).Status, "own");
            Equal(SubmissionStatus.Unanswered, (await RecordOf(harness, below)).Status, "below");
            Equal(SubmissionStatus.Queued, (await RecordOf(harness, write)).Status, "write");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-nested-background-owners-subtree-when-its-cancelled-background-owner-is-aborted", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var outer = await holds.OwnedChild(root, "outer", background: true);
            var inner = await holds.OwnedChild((await harness.ConversationAsync(outer.Child, Context))!, "inner", background: true);
            await harness.AbortTaskAsync(outer.Owner, Context);
            Equal("aborted", await OutcomeOf(harness, outer.Inner), "outer inner");
            Check(await StatusOf(harness, inner.Owner) != DurableTaskStatus.Terminal, "nested owner live");
            Check(await StatusOf(harness, inner.Inner) != DurableTaskStatus.Terminal, "nested inner live");
            await harness.AbortTaskAsync(inner.Owner, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "withdraws-an-input-queued-below-a-held-failed-owner-after-its-cascade", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "owner", slowInner: true);
            var child = (await harness.ConversationAsync(tree.Child, Context))!;
            // The inner task stands in for the child's run, which stays busy after the cascade.
            await Busy(child, tree.Inner);
            holds.Open("owner", Ending.Failed);
            await Eventually(async () => (await harness.GetTaskAsync(tree.Inner, Context))!.AbortRequested);
            var late = await child.SubmitAsync(SubmissionDraft.Input("late"), Context);
            UnansweredAborted(await late.WaitAsync(Context), "late");
            holds.Open("abort.owner.inner");
            await harness.WaitForTaskAsync(tree.Owner, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cascades-from-an-owner-the-scheduler-orphans", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var (owner, inner) = await root.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(StructuredTests.Unregistered, Obj(("name", "unregistered")), OwnConversation());
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                return (owner, await tx.CreateTaskAsync(holds.Hold, Holds.Input("below"), OwnConversation(child.Id)));
            }, Context);
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(owner, Context), "abort");
            SameJson("""{"status":"orphaned","reason":"missing_task"}""",
                ((SettlingTaskState)(await harness.WaitForTaskAsync(owner, Context)).State).Outcome.ToJson(), "owner outcome");
            Equal("aborted", await OutcomeOf(harness, inner), "inner");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cancels-only-the-callers-wait-never-the-shared-work", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var tree = await holds.OwnedChild(root, "owner");
            var waiting = Context.WithCancel();
            var idle = root.WaitForIdleAsync(waiting.Context);
            waiting.Cancel(new InvalidOperationException("stop waiting"));
            await RejectsAsync(() => idle, "stop waiting", "cancelled wait");
            Check(await StatusOf(harness, tree.Inner) != DurableTaskStatus.Terminal, "inner live");

            // Cancelling an abort after its commit leaves the marks in place.
            var slow = await holds.OwnedChild(root, "slow");
            await SetTasks(harness, root, async tx =>
            {
                var record = (await tx.GetTaskAsync(slow.Owner))!;
                tx.SetTask(record with { Input = Holds.Input("slow", slowAbort: true) });
            });
            var aborting = Context.WithCancel();
            var abort = root.AbortAsync(aborting.Context);
            await Eventually(async () => (await harness.GetTaskAsync(slow.Owner, Context))!.AbortRequested);
            aborting.Cancel(new InvalidOperationException("stop aborting"));
            await RejectsAsync(() => abort, "stop aborting", "cancelled abort");
            holds.Open("abort.slow");
            foreach (var id in new[] { tree.Owner, tree.Inner, slow.Owner, slow.Inner })
                Equal("aborted", await OutcomeOf(harness, id), $"task {id}");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-waiting-child-whose-awaited-task-completes-in-the-commit-that-marks-its-owner", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var (owner, dependency, blocked) = await root.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(holds.Hold, Holds.Input("owner"), OwnConversation());
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                var dependency = await tx.CreateTaskAsync(holds.Hold, Holds.Input("dependency"), OwnConversation());
                var blocked = await tx.CreateTaskAsync(holds.Waiter, Holds.WaiterInput(dependency), OwnConversation(child.Id));
                return (owner, dependency, blocked);
            }, Context);
            await Eventually(() => holds.Runs("dependency") == 1 && holds.Runs("owner") == 1);
            await Eventually(async () => await StatusOf(harness, blocked) == DurableTaskStatus.Waiting);
            // One commit completes the dependency and marks the owner.
            await SetTasks(harness, root, async tx =>
            {
                var completedDependency = (await tx.GetTaskAsync(dependency))!;
                var marked = (await tx.GetTaskAsync(owner))!;
                tx.SetTask(completedDependency with { State = NextTaskState.Completed(JsonValue.Null) });
                tx.SetTask(marked with { AbortRequested = true });
            });
            Equal("aborted", await OutcomeOf(harness, blocked), "blocked");
            await harness.CloseAsync(Context);
        });

        // Pi v1.2.0 replaced "retries a cascade whose commit the Storage rejected": a failed commit is final (area b of the sync).
        yield return Case(prefix + "fails-the-harness-on-a-failed-cascade-commit-and-reopening-applies-the-cascade", async () =>
        {
            var holds = new Holds();
            var controlled = new ControlledStorage(await fresh()) { KeepOpenForReopen = true };
            var storage = new MarkFailingStorage(controlled);
            var (harness, root, _, reports) = await holds.OpenHarness(storage);
            var tree = await holds.OwnedChild(root, "owner");
            storage.Fail = tree.Inner;
            holds.Open("owner", Ending.Failed);
            Equal(new SessionEnd(SessionEndReason.Failed, storage.Failure), await harness.Closed, "closed");
            Seq([storage.Failure], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
            controlled.Reopen();
            var reopened = await holds.OpenHarness(storage);
            Equal("aborted", await OutcomeOf(reopened.Harness, tree.Inner), "inner");
            await reopened.Harness.CloseAsync(Context);
            await controlled.Inner.CloseAsync(Context);
        });

        // ─── Pi v1.2.0: the ownership index (area c) ───

        yield return Case(prefix + "holds-an-owner-for-work-in-a-conversation-its-ended-child-task-owns", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var (owner, child, conversation) = await root.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(holds.Hold, Holds.Input("owner"), OwnConversation());
                var child = await tx.CreateTaskAsync(holds.Hold, Holds.Input("child"), new TaskOptions(TaskOwnership.Task(owner)));
                var conversation = await tx.CreateConversationAsync(ConversationOwnership.Task(child));
                return (owner, child, conversation.Id);
            }, Context);
            holds.Open("child");
            Equal("completed", await OutcomeOf(harness, child), "child outcome");
            // The ended child's conversation still belongs to the owner above it.
            var handle = (await harness.ConversationAsync(conversation, Context))!;
            await handle.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input("late"), OwnConversation()), Context);
            holds.Open("owner");
            await Eventually(async () => await StatusOf(harness, owner) == DurableTaskStatus.Completing);
            holds.Open("late");
            Equal("completed", await OutcomeOf(harness, owner), "owner outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cascades-through-ended-tasks-into-an-owned-background-subtree-when-aborting-across-background", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            // root conversation -> A -> B -> conversation -> background C -> D
            var (a, b, c, d) = await root.CommitAsync(async tx =>
            {
                var a = await tx.CreateTaskAsync(holds.Hold, Holds.Input("a"), OwnConversation());
                var b = await tx.CreateTaskAsync(holds.Hold, Holds.Input("b"), new TaskOptions(TaskOwnership.Task(a)));
                var conversation = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                var c = await tx.CreateTaskAsync(holds.Hold, Holds.Input("c"), OwnConversation(conversation.Id, background: true));
                var d = await tx.CreateTaskAsync(holds.Hold, Holds.Input("d"), new TaskOptions(TaskOwnership.Task(c)));
                return (a, b, c, d);
            }, Context);
            holds.Open("b");
            holds.Open("a");
            Equal("completed", await OutcomeOf(harness, a), "a outcome");
            await root.AbortAsync(Context, new ConversationAbortOptions { Background = true });
            foreach (var id in new[] { c, d }) Equal("aborted", await OutcomeOf(harness, id), $"task {id}");
            _ = b;
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-ownership-queries-off-finished-subtrees", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            // Ended chains under a live owner, each ending in an empty owned conversation.
            var owner = await root.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input("owner"), OwnConversation()), Context);
            const int chains = 50;
            var tasks = new List<TaskId>();
            for (var n = 0; n < chains; n++)
            {
                var index = n;
                var (first, second) = await root.CommitAsync(async tx =>
                {
                    var first = await tx.CreateTaskAsync(holds.Hold, Holds.Input($"c{index}.0"), new TaskOptions(TaskOwnership.Task(owner)));
                    var second = await tx.CreateTaskAsync(holds.Hold, Holds.Input($"c{index}.1"), new TaskOptions(TaskOwnership.Task(first)));
                    await tx.CreateConversationAsync(ConversationOwnership.Task(second));
                    return (first, second);
                }, Context);
                tasks.Add(first);
                tasks.Add(second);
            }
            for (var n = 0; n < chains; n++)
            {
                holds.Open($"c{n}.1");
                holds.Open($"c{n}.0");
            }
            foreach (var id in tasks) await harness.WaitForTaskAsync(id, Context);
            // Set iterations while the owner settles, a measure of the walks down: one through every ended task iterates hundreds of entries;
            // one over live work alone, none below the owner. Here the scheduler's own sets count the members they yield.
            var scheduler = ((DurableHarness)harness).Scheduler;
            await scheduler.QuiesceAsync();
            var before = scheduler.SetEntries;
            holds.Open("owner");
            Equal("completed", await OutcomeOf(harness, owner), "owner outcome");
            await scheduler.QuiesceAsync();
            var entries = scheduler.SetEntries - before;
            Check(entries < chains, $"set entries {entries} < {chains}");
            await harness.CloseAsync(Context);
        });

        if (prefix == "harness.ownership.")
        {
            // Upstream runs these on MemoryStorage only, as this group does: 20,000 tasks in one commit.
            yield return Case(prefix + "tracks-ownership-chains-deeper-than-the-call-stack", async () =>
            {
                var holds = new Holds();
                var (harness, root, _, _) = await holds.OpenHarness(await fresh());
                // 20,000 tasks, each owning the next, created in one commit above a live leaf; they end top-down and hold.
                const int depth = 20_000;
                var (top, leaf) = await root.CommitAsync(async tx =>
                {
                    var top = await tx.CreateTaskAsync(holds.Hold, Holds.Input("top"), OwnConversation());
                    var owner = top;
                    for (var n = 0; n < depth; n++)
                        owner = await tx.CreateTaskAsync(holds.Hold, Holds.Input($"link{n}"), new TaskOptions(TaskOwnership.Task(owner)));
                    var leaf = await tx.CreateTaskAsync(holds.Hold, Holds.Input("leaf"), new TaskOptions(TaskOwnership.Task(owner)));
                    return (top, leaf);
                }, Context);
                for (var n = 0; n < depth; n++) holds.Open($"link{n}");
                holds.Open("top");
                await Eventually(async () => await StatusOf(harness, top) == DurableTaskStatus.Completing);
                holds.Open("leaf");
                Equal("completed", await OutcomeOf(harness, leaf), "leaf outcome");
                Equal("completed", await OutcomeOf(harness, top), "top outcome");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "holds-an-owner-for-work-created-later-below-ended-tasks-without-a-reopen", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            // Live P owns B, which owns A, which owns the empty conversation C; B and A end, C stays.
            var (p, b, c) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(holds.Hold, Holds.Input("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(holds.Hold, Holds.Input("b"), new TaskOptions(TaskOwnership.Task(p)));
                var a = await tx.CreateTaskAsync(holds.Hold, Holds.Input("a"), new TaskOptions(TaskOwnership.Task(b)));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                return (p, b, c.Id);
            }, Context);
            holds.Open("a");
            holds.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            var conversation = (await harness.ConversationAsync(c, Context))!;
            var d = await conversation.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input("d"), OwnConversation()), Context);
            holds.Open("p");
            await Eventually(async () => await StatusOf(harness, p) == DurableTaskStatus.Completing);
            holds.Open("d");
            Equal("completed", await OutcomeOf(harness, d), "d outcome");
            Equal("completed", await OutcomeOf(harness, p), "p outcome");
            await harness.CloseAsync(Context);
        });

        // Live P owns `depth` ended tasks in a chain, the last owning conversation C, empty when they ended. Work started in C later is still
        // P's: every case below must reach it through the ended tasks (upstream endedBridge).
        async Task<(IHarness Harness, IConversation Root, TaskId P, Func<string, Task<TaskId>> Create)> EndedBridge(Holds holds, int depth)
        {
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var (p, links, c) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(holds.Hold, Holds.Input("p", slowAbort: true), OwnConversation());
                var links = new List<TaskId>();
                var owner = p;
                for (var n = 0; n < depth; n++)
                {
                    owner = await tx.CreateTaskAsync(holds.Hold, Holds.Input($"link{n}"), new TaskOptions(TaskOwnership.Task(owner)));
                    links.Add(owner);
                }
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                return (p, links, c.Id);
            }, Context);
            for (var n = depth - 1; n >= 0; n--) holds.Open($"link{n}");
            foreach (var id in links) await harness.WaitForTaskAsync(id, Context);
            var conversation = (await harness.ConversationAsync(c, Context))!;
            return (harness, root, p, name => conversation.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input(name), OwnConversation()), Context));
        }

        foreach (var depth in new[] { 2, 5 })
        {
            yield return Case(prefix + $"aborts-work-below-{depth}-ended-tasks-with-their-live-owner-which-waits-for-it", async () =>
            {
                var holds = new Holds();
                var (harness, _, p, create) = await EndedBridge(holds, depth);
                var d = await create("d");
                await harness.AbortTaskAsync(p, Context);
                Equal("aborted", await OutcomeOf(harness, d), "d outcome");
                // P's abort handler ran only after D ended.
                holds.Open("abort.p");
                Equal("aborted", await OutcomeOf(harness, p), "p outcome");
                await harness.CloseAsync(Context);
            });

            yield return Case(prefix + $"keeps-the-root-busy-and-aborts-with-it-work-below-{depth}-ended-tasks", async () =>
            {
                var holds = new Holds();
                var (harness, root, p, create) = await EndedBridge(holds, depth);
                holds.Open("p");
                await harness.WaitForTaskAsync(p, Context);
                // P has ended: D and D2 hang below ended tasks only, and are still the root conversation's work.
                var d = await create("d");
                var d2 = await create("d2");
                Check(!await Settled(root.WaitForIdleAsync(Context)), "root busy");
                Check(!await Settled(harness.WaitForIdleAsync(Context)), "harness busy");
                await root.AbortAsync(Context);
                foreach (var id in new[] { d, d2 }) Equal("aborted", await OutcomeOf(harness, id), $"task {id}");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "reaches-work-below-ended-tasks-across-two-owned-conversations", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            // Live P -> ended B -> conversation C -> ended X -> ended A -> conversation C2; work starts in C2 later. X owns no conversation, only
            // the task A, so only owning a known task keeps it.
            var (p, b, c2) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(holds.Hold, Holds.Input("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(holds.Hold, Holds.Input("b"), new TaskOptions(TaskOwnership.Task(p)));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                var x = await tx.CreateTaskAsync(holds.Hold, Holds.Input("x"), OwnConversation(c.Id));
                var a = await tx.CreateTaskAsync(holds.Hold, Holds.Input("a"), new TaskOptions(TaskOwnership.Task(x)));
                var c2 = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                return (p, b, c2.Id);
            }, Context);
            holds.Open("a");
            holds.Open("x");
            holds.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            var conversation = (await harness.ConversationAsync(c2, Context))!;
            var d = await conversation.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input("d"), OwnConversation()), Context);
            holds.Open("p");
            await Eventually(async () => await StatusOf(harness, p) == DurableTaskStatus.Completing);
            Check(!await Settled(root.WaitForIdleAsync(Context)), "root busy");
            await root.AbortAsync(Context);
            Equal("aborted", await OutcomeOf(harness, d), "d outcome");
            Equal("completed", await OutcomeOf(harness, p), "p outcome");
            await harness.CloseAsync(Context);
        });

        foreach (var ending in new[] { "completes", "is aborted" })
        {
            yield return Case(prefix + $"frees-an-owner-that-{ending.Replace(' ', '-')}-once-the-last-work-below-ended-tasks-ends-after-a-background-task-there", async () =>
            {
                var holds = new Holds();
                var (harness, root, _, _) = await holds.OpenHarness(await fresh());
                // Live P owns B, which owns A, which owns conversation C with background Z and, later, foreground D.
                var (p, b, c, z) = await root.CommitAsync(async tx =>
                {
                    var p = await tx.CreateTaskAsync(holds.Hold, Holds.Input("p"), OwnConversation());
                    var b = await tx.CreateTaskAsync(holds.Hold, Holds.Input("b"), new TaskOptions(TaskOwnership.Task(p)));
                    var a = await tx.CreateTaskAsync(holds.Hold, Holds.Input("a"), new TaskOptions(TaskOwnership.Task(b)));
                    var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                    var z = await tx.CreateTaskAsync(holds.Hold, Holds.Input("z"), OwnConversation(c.Id, background: true));
                    return (p, b, c.Id, z);
                }, Context);
                holds.Open("a");
                holds.Open("b");
                await harness.WaitForTaskAsync(b, Context);
                var conversation = (await harness.ConversationAsync(c, Context))!;
                var d = await conversation.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input("d", slowAbort: true), OwnConversation()), Context);
                if (ending == "completes")
                {
                    holds.Open("p");
                    await Eventually(async () => await StatusOf(harness, p) == DurableTaskStatus.Completing);
                }
                else
                {
                    // P waits for D's abort before its own abort handler runs.
                    await harness.AbortTaskAsync(p, Context);
                    await Eventually(async () => (await harness.GetTaskAsync(d, Context))!.AbortRequested);
                }
                // Z ends first, D last.
                holds.Open("z");
                await harness.WaitForTaskAsync(z, Context);
                if (ending == "completes") holds.Open("d");
                else holds.Open("abort.d");
                await harness.WaitForTaskAsync(d, Context);
                Equal(ending == "completes" ? "completed" : "aborted", await OutcomeOf(harness, p), "p outcome");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "finalizes-many-held-siblings-that-end-together-in-one-pass", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            // Top holds 1000 held children, each holding one live leaf; the leaves end in one commit.
            const int count = 1000;
            var (top, leaves) = await root.CommitAsync(async tx =>
            {
                var top = await tx.CreateTaskAsync(holds.Hold, Holds.Input("top"), OwnConversation());
                var siblings = new List<TaskId>();
                for (var n = 0; n < count; n++)
                    siblings.Add(await tx.CreateTaskAsync(holds.Hold, Holds.Input($"s{n}"), new TaskOptions(TaskOwnership.Task(top))));
                // Leaves in reverse sibling order: the last sibling is freed first, so a holder checked after each freed sibling would scan the
                // siblings already finalized each time.
                var leaves = new List<TaskId>();
                for (var n = count - 1; n >= 0; n--)
                    leaves.Add(await tx.CreateTaskAsync(holds.Hold, Holds.Input($"l{n}"), new TaskOptions(TaskOwnership.Task(siblings[n]))));
                return (top, leaves);
            }, Context);
            for (var n = 0; n < count; n++) holds.Open($"s{n}");
            holds.Open("top");
            await Eventually(async () => await StatusOf(harness, top) == DurableTaskStatus.Completing);
            // Every sibling holds too before the leaves end.
            await Eventually(async () => (await harness.InspectAsync(Context)).Tasks.Count(task => task.Record.State.Status == DurableTaskStatus.Completing) == count + 1);
            // Set entries iterated while it all finalizes: one pass is linear, a recheck per sibling quadratic (~500,000). Counted here from
            // before the leaves' commit, whose tracking upstream's count leaves out, so this bound is the stricter one.
            var scheduler = ((DurableHarness)harness).Scheduler;
            await scheduler.QuiesceAsync();
            var before = scheduler.SetEntries;
            await SetTasks(harness, root, async tx =>
            {
                // Reads before the first write. Ended last to first, so a holder checked after each sibling would scan the ones already
                // finalized every time.
                var records = new List<TaskRecord>();
                foreach (var id in leaves) records.Add((await tx.GetTaskAsync(id))!);
                foreach (var record in records) tx.SetTask(record with { State = NextTaskState.Completed(JsonValue.Null) });
            });
            Equal("completed", await OutcomeOf(harness, top), "top outcome");
            await scheduler.QuiesceAsync();
            var entries = scheduler.SetEntries - before;
            Check(entries < count * 20, $"set entries {entries} < {count * 20}");
            await harness.CloseAsync(Context);
        });

        // Pins the Transaction rule that a conversation cannot be created in the commit that ends its owner task.
        yield return Case(prefix + "rejects-a-conversation-created-in-the-commit-that-ends-its-owner-task", async () =>
        {
            var holds = new Holds();
            var (harness, root, _, _) = await holds.OpenHarness(await fresh());
            var spawner = await root.CommitAsync(tx => tx.CreateTaskAsync(holds.Spawner, Holds.Input("spawned"), OwnConversation()), Context);
            var outcome = ((SettlingTaskState)(await harness.WaitForTaskAsync(spawner, Context)).State).Outcome;
            Check(outcome is FaultedTaskOutcome, $"faulted, actual {outcome.ToJson().ToJson()}");
            var message = ((FaultedTaskOutcome)outcome).Error.Message;
            Check(message == $"Conversation owner task {spawner} is completing" || message == $"Conversation owner task {spawner} is terminal",
                $"message: {message}");
            // toEqual: the outcome is exactly { status, error: { message } }.
            SameJson(Obj(("status", "faulted"), ("error", Obj(("message", message)))).ToJson(), outcome.ToJson(), "outcome");
            await harness.CloseAsync(Context);
        });
    }

    private static IEnumerable<(string, Func<Task>)> OwnershipReopen(string prefix, Func<StructuredTests.OpenLocation> reopenable)
    {
        yield return Case(prefix + "decides-idle-after-reopen-from-owner-edges-it-has-to-load-first", async () =>
        {
            var holds = new Holds();
            var location = reopenable();
            var opened = await holds.OpenHarness(await location());
            var foreground = await holds.OwnedChild(opened.Root, "fg");
            var deeper = await holds.OwnedChild((await opened.Harness.ConversationAsync(foreground.Child, Context))!, "fg2");
            var background = await holds.OwnedChild(opened.Root, "bg", background: true);
            holds.Open("fg");
            holds.Open("fg2");
            // Both owners hold their outcomes while the work below them runs.
            await Eventually(async () => await StatusOf(opened.Harness, foreground.Owner) == DurableTaskStatus.Completing);
            await Eventually(async () => await StatusOf(opened.Harness, deeper.Owner) == DurableTaskStatus.Completing);
            await opened.Harness.CloseAsync(Context);

            opened = await holds.OpenHarness(await location());
            // Two levels below completed foreground owners, the inner tasks keep the root busy.
            Check(!await Settled(opened.Root.WaitForIdleAsync(Context)), "root busy");
            Check(!await Settled(opened.Harness.WaitForIdleAsync(Context)), "harness busy");
            holds.Open("fg.inner");
            holds.Open("fg2.inner");
            // The background subtree does not count.
            await opened.Root.WaitForIdleAsync(Context);
            await opened.Harness.WaitForIdleAsync(Context);
            Equal(DurableTaskStatus.Terminal, await StatusOf(opened.Harness, foreground.Owner), "foreground owner");
            Check(await StatusOf(opened.Harness, background.Inner) != DurableTaskStatus.Terminal, "background inner live");
            await opened.Harness.AbortTaskAsync(background.Owner, Context);
            await opened.Harness.CloseAsync(Context);
        });

        foreach (var (label, abortRequested, state) in new[]
        {
            ("a-held-failed-owner", false, """{"status":"completing","outcome":{"status":"failed","error":{"message":"crash"}}}"""),
            ("an-abort-marked-owner", true, """{"status":"pending","checkpoint":{"phase":"hold"}}"""),
        })
        {
            yield return Case(prefix + $"derives-marks-a-crash-left-unapplied-below-{label}-at-open", async () =>
            {
                var holds = new Holds();
                var location = reopenable();
                // Without a Harness, nothing derives marks: a cancelled owner with a live task below it.
                var session = new PiSharp.Durable.Session(await location());
                var tree = await session.CommitAsync(async tx =>
                {
                    var root = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                    var owner = await tx.CreateTaskAsync(holds.Hold, Holds.Input("gone"), OwnConversation(root.Id));
                    var child = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                    return (Owner: owner, Inner: await tx.CreateTaskAsync(holds.Hold, Holds.Input("orphan"), OwnConversation(child.Id)));
                }, Context);
                await session.CommitWithAsync(async tx =>
                {
                    var record = (await tx.GetTaskAsync(tree.Owner))!;
                    tx.SetTask(record with { AbortRequested = abortRequested, State = TaskState.FromJson(J(state)) });
                }, Context);
                await session.CloseAsync(Context);

                var (harness, _, _, _) = await holds.OpenHarness(await location());
                Equal("aborted", await OutcomeOf(harness, tree.Inner), "inner");
                // The owner finishes only after the work below it.
                Equal(abortRequested ? "aborted" : "failed", await OutcomeOf(harness, tree.Owner), "owner");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "marks-work-admitted-after-reopen-below-a-cancelled-owner-whose-edge-was-not-loaded", async () =>
        {
            var holds = new Holds();
            var location = reopenable();
            var opened = await holds.OpenHarness(await location());
            var tree = await holds.OwnedChild(opened.Root, "owner", slowOwner: true);
            holds.Open("owner.inner");
            await opened.Harness.WaitForTaskAsync(tree.Inner, Context);
            // The owner stays live and cancelled in its slow abort handler.
            await opened.Harness.AbortTaskAsync(tree.Owner, Context);
            await opened.Harness.CloseAsync(Context);

            // The child is empty at open, so nothing loads its edge until new work arrives.
            opened = await holds.OpenHarness(await location());
            var child = (await opened.Harness.ConversationAsync(tree.Child, Context))!;
            var late = await child.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input("late"), OwnConversation()), Context);
            Equal("aborted", await OutcomeOf(opened.Harness, late), "late");
            holds.Open("abort.owner");
            Equal("aborted", await OutcomeOf(opened.Harness, tree.Owner), "owner");
            await opened.Harness.CloseAsync(Context);
        });

        // Pi v1.2.0 replaced "retries marks found through an edge loaded after reopen when their commit is rejected".
        yield return Case(prefix + "applies-marks-found-through-an-edge-loaded-after-reopen-once-a-failed-cascade-commit-is-reopened", async () =>
        {
            var holds = new Holds();
            var location = reopenable();
            var opened = await holds.OpenHarness(await location());
            var tree = await holds.OwnedChild(opened.Root, "owner", slowOwner: true);
            holds.Open("owner.inner");
            await opened.Harness.WaitForTaskAsync(tree.Inner, Context);
            await opened.Harness.AbortTaskAsync(tree.Owner, Context);
            await opened.Harness.CloseAsync(Context);

            var storage = new MarkFailingStorage(await location());
            opened = await holds.OpenHarness(storage);
            var child = (await opened.Harness.ConversationAsync(tree.Child, Context))!;
            var late = await child.CommitAsync(async tx =>
            {
                var id = await tx.CreateTaskAsync(holds.Hold, Holds.Input("late"), OwnConversation());
                storage.Fail = id;
                return id;
            }, Context);
            // The failed cascade commit fails the Harness. Closed settles once its invocations have ended, and the owner's abort handler
            // ignores its signal: let it return.
            holds.Open("abort.owner");
            Equal(SessionEndReason.Failed, (await opened.Harness.Closed).Reason, "closed");
            await opened.Harness.CloseAsync(Context);
            // Reopening derives the mark again.
            opened = await holds.OpenHarness(await location());
            Equal("aborted", await OutcomeOf(opened.Harness, late), "late");
            await opened.Harness.CloseAsync(Context);
        });

        // Pi v1.2.0 (area c): the ownership index loads ended chains from storage again.
        yield return Case(prefix + "holds-an-owner-for-work-a-commit-creates-below-ended-tasks-loaded-from-storage", async () =>
        {
            var holds = new Holds();
            var location = reopenable();
            var opened = await holds.OpenHarness(await location());
            // Live P owns B, which owns A, which owns the empty conversation C; B and A end.
            var (p, a, b, c) = await opened.Root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(holds.Hold, Holds.Input("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(holds.Hold, Holds.Input("b"), new TaskOptions(TaskOwnership.Task(p)));
                var a = await tx.CreateTaskAsync(holds.Hold, Holds.Input("a"), new TaskOptions(TaskOwnership.Task(b)));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                return (p, a, b, c.Id);
            }, Context);
            holds.Open("a");
            holds.Open("b");
            await opened.Harness.WaitForTaskAsync(a, Context);
            await opened.Harness.WaitForTaskAsync(b, Context);
            await opened.Harness.CloseAsync(Context);
            // After reopen, B and A are known only once a chain load reads them.
            opened = await holds.OpenHarness(await location());
            var conversation = (await opened.Harness.ConversationAsync(c, Context))!;
            var d = await conversation.CommitAsync(tx => tx.CreateTaskAsync(holds.Hold, Holds.Input("d"), OwnConversation()), Context);
            holds.Open("p");
            await Eventually(async () => await StatusOf(opened.Harness, p) == DurableTaskStatus.Completing);
            holds.Open("d");
            Equal("completed", await OutcomeOf(opened.Harness, d), "d outcome");
            Equal("completed", await OutcomeOf(opened.Harness, p), "p outcome");
            await opened.Harness.CloseAsync(Context);
        });
    }

    // ─── owned conversations from tools and supervisors ─────────────────────

    /// <summary>Upstream's <c>Type.Object({})</c> tool with <paramref name="execute"/>.</summary>
    private static ToolRegistration Delegating(string name, string description, ToolExecute execute, ToolReplay? replay = null,
        JsonObjectValue? parameters = null) =>
        Define.Tool(new ToolRegistration
        {
            Name = name,
            Description = description,
            Parameters = parameters ?? ToolRegistration.EmptyParameters,
            Execute = execute,
            Replay = replay,
        });

    /// <summary>A tool-calling answer with one call.</summary>
    private static JsonObjectValue CallOf(string name, JsonObjectValue args, string id) =>
        FauxAssistantMessage([FauxToolCall(name, args, id)], stopReason: "toolUse");

    /// <summary>A tool result with one text block.</summary>
    private static ToolExecutionResult TextResult(string text) => new() { Output = JsonValue.Array(FauxText(text)) };

    /// <summary>Upstream <c>configure(tx, id, { model: { provider: "faux", modelId: "faux-1" } })</c>.</summary>
    private static Task ConfigureFaux(ITx tx, ConversationId id) => Agents.ConfigureAsync(tx, id, new AgentChange { Model = new ModelRef("faux", "faux-1") });

    private static IEnumerable<(string, Func<Task>)> OwnedConversations(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "gives-a-tool-an-invocation-bound-handle-whose-submissions-stay-durable-after-the-call-ends", async () =>
        {
            var setup = Setup();
            IConversationHandle? handle = null;
            var missingChecked = false;
            IConversationHandle? missing = null;
            ISubmission? submission = null;
            AddTool(setup.Registry, Delegating("delegate", "Delegates", async (_, api, callContext) =>
            {
                var child = await api.CommitAsync(async tx =>
                {
                    var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                    await ConfigureFaux(tx, created.Id);
                    return created.Id;
                }, callContext);
                missing = await api.ConversationAsync(new ConversationId(99_999), callContext);
                missingChecked = true;
                handle = (await api.ConversationAsync(child, callContext))!;
                submission = await handle.SubmitAsync(SubmissionDraft.Input("child task", requestId: "child"), callContext);
                var settled = await submission.WaitAsync(callContext);
                return TextResult(settled.Status == SubmissionStatus.Done ? "done" : "unanswered");
            }));
            setup.Faux.SetResponses(CallOf("delegate", JsonObjectValue.Empty, "c1"), FauxAssistantMessage("child answer"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Check(missingChecked && missing is null, "missing conversation");
            // The call ended: the handle and its submission reject, while the submission record remains.
            await RejectsAsync(() => handle!.SubmitAsync(SubmissionDraft.Input("again"), Context), "invocation has ended", "submit");
            await RejectsAsync(() => handle!.WaitForIdleAsync(Context), "invocation has ended", "waitForIdle");
            await RejectsAsync(() => handle!.AbortAsync(Context), "invocation has ended", "abort");
            await RejectsAsync(() => submission!.WaitAsync(Context), "invocation has ended", "wait");
            // Nothing was admitted or marked after the call ended.
            var childConversation = (await harness.ConversationAsync(handle!.Id, Context))!;
            var childEntries = (await childConversation.EntriesAsync(new EntryQueryBounds(), 100, null, Context)).Items;
            Equal(1, childEntries.Count(entry => entry.Kind == "pi.user"), "child user entries");
            var childTasks = (await harness.InspectAsync(Context)).Tasks.Where(task => task.Record.ConversationId == handle!.Id).ToList();
            Equal(0, childTasks.Count, "child tasks");
            Equal(SubmissionStatus.Done, (await RecordOf(harness, submission!.Id)).Status, "submission status");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-a-handle-operation-queued-on-the-line-when-the-invocation-ends-before-it-runs", async () =>
        {
            var setup = Setup();
            var ready = new Deferred<(ConversationId Child, TaskId TaskId)>();
            var go = new HarnessTests.Deferred();
            var queued = new Deferred<Task>();
            AddTool(setup.Registry, Delegating("delegate", "Delegates late", async (arguments, api, callContext) =>
            {
                var child = await api.CommitAsync(async tx => (await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId))).Id, callContext);
                var handle = (await api.ConversationAsync(child, callContext))!;
                ready.Resolve((child, api.TaskId));
                await go.Task;
                // Called while the invocation is alive, with a context that is not the call's: the handle binds it to the invocation. It
                // reaches the line only after the abort mark.
                var submitting = handle.SubmitAsync(SubmissionDraft.Input("late"), Context);
                _ = submitting.ContinueWith(static task => task.Exception, TaskScheduler.Default);
                queued.Resolve(submitting);
                await Aborted(callContext.CancellationToken);
                throw new InvalidOperationException("unreachable");
            }));
            setup.Faux.SetResponses(CallOf("delegate", JsonObjectValue.Empty, "c1"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var (child, taskId) = await ready.Task;
            // Hold the Session line, queue the abort mark behind it, then let the tool queue its submit.
            var release = new HarnessTests.Deferred();
            var entered = new HarnessTests.Deferred();
            var holding = root.CommitAsync(async _ =>
            {
                entered.Resolve();
                await release.Task;
            }, Context);
            await entered.Task;
            var aborting = harness.AbortTaskAsync(taskId, Context);
            // Deterministic barrier: the mark commit waits on the line behind the held commit.
            await Eventually(() => ((DurableHarness)harness).LineBacklog >= 1);
            go.Resolve();
            var submitting = await queued.Task;
            release.Resolve();
            await holding;
            await aborting;
            await ThrowsAsync<Exception>(() => submitting, "late submit");
            var childConversation = (await harness.ConversationAsync(child, Context))!;
            Equal(0, (await childConversation.EntriesAsync(new EntryQueryBounds(), 10, null, Context)).Items.Length, "child entries");
            Equal(0, (await harness.InspectAsync(Context)).Submissions.Count(record => record.ConversationId == child), "child submissions");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-subagent-run-with-its-parents-conversation-rejecting-the-waiting-tool", async () =>
        {
            var setup = Setup();
            var child = new StructuredTests.ConversationSlot();
            var toolWait = new Deferred<string>();
            AddTool(setup.Registry, Delegating("delegate", "Delegates", async (_, api, callContext) =>
            {
                child.Id = await api.CommitAsync(async tx =>
                {
                    var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                    await ConfigureFaux(tx, created.Id);
                    return created.Id;
                }, callContext);
                var submission = await (await api.ConversationAsync(child.Id!.Value, callContext))!.SubmitAsync(SubmissionDraft.Input("child task"), callContext);
                try
                {
                    var settled = await submission.WaitAsync(callContext);
                    return TextResult(settled.Status == SubmissionStatus.Done ? "done" : "unanswered");
                }
                catch (Exception error)
                {
                    toolWait.Resolve(error.Message);
                    throw;
                }
            }));
            var childRun = Gated(FauxAssistantMessage("never"));
            setup.Faux.SetResponses(CallOf("delegate", JsonObjectValue.Empty, "c1"), childRun.Step);
            var (harness, root) = await OpenChat(await fresh(), setup);
            var input = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await childRun.Reached;
            await root.AbortAsync(Context);
            UnansweredAborted(await input.WaitAsync(Context), "input");
            Check(!string.IsNullOrEmpty(await toolWait.Task), "tool wait rejected");
            var childConversation = (await harness.ConversationAsync(child.Id!.Value, Context))!;
            await childConversation.WaitForIdleAsync(Context);
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, child.Id!.Value, Context), "child live");
            Equal(0, (await harness.InspectAsync(Context)).Submissions.Count(record => record.ConversationId == child.Id), "child inputs");
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-a-running-tool-abort-its-owned-child-and-continue", async () =>
        {
            var setup = Setup();
            var childRun = Gated(FauxAssistantMessage("never"));
            AddTool(setup.Registry, Delegating("delegate", "Delegates and cancels", async (_, api, callContext) =>
            {
                var child = await api.CommitAsync(async tx =>
                {
                    var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                    await ConfigureFaux(tx, created.Id);
                    return created.Id;
                }, callContext);
                var handle = (await api.ConversationAsync(child, callContext))!;
                var submission = await handle.SubmitAsync(SubmissionDraft.Input("child task"), callContext);
                await childRun.Reached;
                await handle.AbortAsync(callContext);
                var settledChild = await submission.WaitAsync(callContext);
                return TextResult($"child {(settledChild.Status == SubmissionStatus.Done ? "done" : "unanswered")}");
            }));
            setup.Faux.SetResponses(CallOf("delegate", JsonObjectValue.Empty, "c1"), childRun.Step, FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "status");
            await harness.CloseAsync(Context);
        });
    }

    private static IEnumerable<(string, Func<Task>)> OwnedConversationsReopen(string prefix, Func<StructuredTests.OpenLocation> reopenable)
    {
        yield return Case(prefix + "aborts-the-children-of-a-tool-call-that-throws-or-is-interrupted-while-the-run-continues", async () =>
        {
            var location = reopenable();
            var holds = new Holds();
            var children = new SyncList<TaskId>();
            var toolRunning = new HarnessTests.Deferred();
            var setup = Setup();
            AddTask(setup.Registry, holds.Hold);
            AddTool(setup.Registry, Delegating("spawn", "Starts owned work, then throws or hangs", async (args, api, callContext) =>
            {
                var child = await api.CommitAsync(async tx =>
                {
                    var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                    return await tx.CreateTaskAsync(holds.Hold, Holds.Input($"child.{api.CallId}"), OwnConversation(created.Id));
                }, callContext);
                children.Add(child);
                if (S(((JsonObjectValue)args)["mode"]) == "throw") throw new InvalidOperationException("spawn failed");
                toolRunning.Resolve();
                await Aborted(callContext.CancellationToken);
                throw new InvalidOperationException("unreachable");
            }, parameters: O("""{"type":"object","properties":{"mode":{"type":"string"}},"required":["mode"]}""")));
            JsonObjectValue Call(string mode, string id) => CallOf("spawn", Obj(("mode", mode)), id);
            setup.Faux.SetResponses(Call("throw", "c1"), FauxAssistantMessage("after throw"), Call("hang", "c2"));
            var opened = await OpenChat(await location(), setup);
            Equal(SubmissionStatus.Done, (await (await opened.Root.SubmitAsync(SubmissionDraft.Input("one"), Context)).WaitAsync(Context)).Status, "first");
            Equal("aborted", await OutcomeOf(opened.Harness, children[0]), "first child");

            // The second call hangs until the process stops; on reopen it is interrupted.
            var second = (await opened.Root.SubmitAsync(SubmissionDraft.Input("two"), Context)).Id;
            await toolRunning.Task;
            await opened.Harness.CloseAsync(Context);
            setup.Faux.SetResponses(FauxAssistantMessage("after interrupt"));
            opened = await OpenChat(await location(), setup);
            opened.Harness.Resume();
            Equal("aborted", await OutcomeOf(opened.Harness, children[1]), "second child");
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(second, Context))!.WaitAsync(Context)).Status, "second");
            var tools = (await opened.Harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.tool" }, 10), Context)).Items;
            Seq(["failed", "failed"], tools.Select(task => task.State is TerminalTaskState terminal ? StructuredTests.Wire(terminal.Outcome.Status) : "live"),
                "tool outcomes");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reruns-a-replay-safe-subagent-tool-after-a-restart-with-the-same-child-and-submission", async () =>
        {
            var location = reopenable();
            var children = new SyncList<ConversationId>();
            var setup = Setup();
            AddTool(setup.Registry, Delegating("subagent", "Delegates", async (_, api, callContext) =>
            {
                var child = await api.CommitAsync(async tx =>
                {
                    var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1)).Items.FirstOrDefault();
                    if (existing is not null) return existing.Id;
                    var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                    await ConfigureFaux(tx, created.Id);
                    return created.Id;
                }, callContext);
                children.Add(child);
                var request = SubmissionDraft.Input("child task", requestId: $"subagent:{api.TaskId}");
                var submission = await (await api.ConversationAsync(child, callContext))!.SubmitAsync(request, callContext);
                var settled = await submission.WaitAsync(callContext);
                return TextResult(settled.Status == SubmissionStatus.Done ? "done" : "unanswered");
            }, replay: ToolReplay.Safe));
            var childRun = Gated(FauxAssistantMessage("never"));
            setup.Faux.SetResponses(CallOf("subagent", JsonObjectValue.Empty, "c1"), childRun.Step);
            var opened = await OpenChat(await location(), setup);
            var input = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await childRun.Reached;
            await opened.Harness.CloseAsync(Context);

            setup.Faux.SetResponses(FauxAssistantMessage("child answer"), FauxAssistantMessage("done"));
            opened = await OpenChat(await location(), setup);
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(input, Context))!.WaitAsync(Context)).Status, "input");
            Equal(2, children.Count, "children");
            Equal(children[0], children[1], "same child");
            var child = (await opened.Harness.ConversationAsync(children[0], Context))!;
            var entries = (await child.EntriesAsync(new EntryQueryBounds(), 100, null, Context)).Items;
            Equal(1, entries.Count(entry => entry.Kind == "pi.user"), "child user entries");
            var results = (await opened.Root.ContextAsync(Context)).Messages.Select(message => (JsonObjectValue)message)
                .Where(message => S(message["role"]) == "toolResult");
            Seq([false], results.Select(message => message["isError"] is JsonBooleanValue { Value: true }), "isError");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-a-background-supervisor-resubmit-after-a-restart-without-submitting-twice", async () =>
        {
            var location = reopenable();
            var children = Documents.DefineConversationDoc("test.children", 1, DocumentFork.Initial, () => JsonObjectValue.Empty);
            var supervisor = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.supervisor",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (task, runtime, ctx) =>
                    {
                        var parent = new ConversationId((long)N(((JsonObjectValue)task.Input)["parent"]));
                        var id = new ConversationId((long)N((await runtime.SnapshotAsync(children, parent, ctx))!["child"]));
                        var child = (await runtime.ConversationAsync(id, ctx))!;
                        var submission = await child.SubmitAsync(SubmissionDraft.Input("work", requestId: "stable"), ctx);
                        var settled = await submission.WaitAsync(ctx);
                        if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input)
                            throw new InvalidOperationException(settled.Status.ToString());
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(Obj(("answer", settled.Answer!.Value.ToJson()))), ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
            var setup = Setup();
            AddTask(setup.Registry, supervisor);
            var first = Gated(FauxAssistantMessage("never"));
            setup.Faux.SetResponses(first.Step, FauxAssistantMessage("answer"));
            var opened = await OpenChat(await location(), setup);
            var root = opened.Root;
            var (supervisorId, childId) = await root.CommitAsync(async tx =>
            {
                var id = await tx.CreateTaskAsync(supervisor, Obj(("parent", root.Id.ToJson())), OwnConversation(background: true));
                var created = await tx.CreateConversationAsync(ConversationOwnership.Task(id));
                await ConfigureFaux(tx, created.Id);
                (await tx.DocAsync(children, root.Id)).Set("child", created.Id.ToJson());
                return (id, created.Id);
            }, Context);
            opened.Harness.Resume();
            // The submission is admitted and its generation requested; then the process stops.
            await first.Reached;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(await location(), setup);
            opened.Harness.Resume();
            Equal("completed", await OutcomeOf(opened.Harness, supervisorId), "supervisor");
            var childConversation = (await opened.Harness.ConversationAsync(childId, Context))!;
            var entries = (await childConversation.EntriesAsync(new EntryQueryBounds(), 100, null, Context)).Items;
            Equal(1, entries.Count(entry => entry.Kind == "pi.user"), "child user entries");
            // The root never waited for the background supervisor.
            await opened.Root.WaitForIdleAsync(Context);
            await opened.Harness.CloseAsync(Context);
        });
    }
}
