// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-structured.test.ts, the task-runtime describes: task
// ownership, waiting, completing, abort order, boundaries, recovery, definitions and waits, conversation abort and boundaries, and recovery
// through ownership edges. Verbatim apart from the adaptations in HarnessSupport.cs and HarnessStructuredSupport.cs, and these:
// - Backends. Upstream runs these on MemoryStorage and reopens a SQLite file for the reopen cases. HarnessStructuredCases() does the same
//   (ids harness.structured.<describe>.<test>); HarnessStructuredJsonlCases() reruns every case on JSONL, reopen cases reopening a JSONL
//   directory; HarnessStructuredSqliteCases() reruns the cases that need no reopen on a fresh SQLite file (its reopen cases would repeat
//   the MemoryStorage group's).
// - Parameterized tests ("fails fast when a child ends ...", "aborts the work below ...") are one case per parameter.
// - `expect(...).rejects.toThrow()` for a TaskOptions without ownership: C# requires the ownership, so the case passes a null one, which
//   rejects with a NullReferenceException where upstream's undefined ownership rejects with a TypeError.
// - Where a script reads a variable the test assigns only after `await start(...)` returns, upstream relies on the parent's run starting
//   in a later macrotask; handlers here run on other threads, so the script first awaits a deferred the test resolves after assigning it.
// - IDs a handler assigns inside a commit callback are awaited with StructuredTests.Committed, which also waits for the record (see there).
// - `expect(await settled(promise)).toBe(true)` awaits the promise itself (bounded by the case timeout), since Settled is only a negative
//   check here.
//
// Tool rounds. HarnessStructuredToolRoundsCases.cs (harness.structured.tool-rounds.*) ports "keeps run control with a faulted generation
// until its owned work drains, and retries a rejected final commit" (generation, P8) and the three tests the tool task unblocks (P9): "owns
// its tool tasks, waits for them, and hands the run to a conversation-owned generation"; "aborts a parallel round with its generation: tools
// first, then the generation, with every result written"; "answers the unstarted calls of an aborted sequential round with aborted results,
// in call order". HarnessStructuredEventsCases.cs ports the tests that listen to agent events (watchEvents, P10): "ends a turn at the
// generation's hold, before its successor's turn starts" (harness.structured.tool-rounds.*) and the four tests of describe "tool rounds
// and events" (harness.structured.tool-rounds-and-events.*).
// "reopens a checkout waiting on live payments and finishes it", the last test of upstream's recovery describe (just before "tool rounds"),
// needs no model and is ported.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static HarnessTests;
using static Program;
using static SessionTests;
using static StructuredTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessStructuredCases() =>
        StructuredTests.Cases("harness.structured.", () => Task.FromResult<IStorage>(new MemoryStorage()), SqliteLocation);

    private static IEnumerable<(string, Func<Task>)> HarnessStructuredJsonlCases() =>
        StructuredTests.Cases("harness.structured.jsonl.", () => JsonlLocation()(), JsonlLocation);

    private static IEnumerable<(string, Func<Task>)> HarnessStructuredSqliteCases() =>
        StructuredTests.Cases("harness.structured.sqlite.", () => SqliteLocation()(), null);

    private static OpenLocation SqliteLocation()
    {
        var path = Path.Combine(Temp("structured-sqlite"), "session.sqlite");
        return async () => await SqliteStorage.OpenAsync(path);
    }

    private static OpenLocation JsonlLocation()
    {
        var directory = Temp("structured-jsonl");
        return async () => await JsonlStorage.OpenAsync(directory, DurableContext.Background);
    }
}

internal static partial class StructuredTests
{
    /// <summary>
    /// The harness.structured.* cases on storages from <paramref name="fresh"/>; reopen cases use locations from
    /// <paramref name="reopenable"/> and are left out without one.
    /// </summary>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh, Func<OpenLocation>? reopenable)
    {
        IEnumerable<(string, Func<Task>)> all =
        [
            .. Ownership(prefix + "task-ownership.", fresh),
            .. Waiting(prefix + "waiting.", fresh),
            .. Completing(prefix + "completing.", fresh),
            .. AbortOrder(prefix + "abort-order.", fresh),
            .. Boundaries(prefix + "boundaries.", fresh),
            .. ToolRounds(prefix + "tool-rounds.", fresh),
            .. ToolRoundTurns(prefix + "tool-rounds.", fresh),
            .. ToolRoundsAndEvents(prefix + "tool-rounds-and-events.", fresh),
            .. DefinitionsAndWaits(prefix + "definitions-and-waits.", fresh),
            .. ConversationAbort(prefix + "conversation-abort-and-boundaries.", fresh),
        ];
        if (reopenable is not null)
        {
            all = [.. all, .. Recovery(prefix + "recovery.", reopenable), .. RecoveryEdges(prefix + "recovery-through-ownership-edges.", reopenable)];
        }
        return all;
    }

    // ─── Ownership ──────────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> Ownership(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "creates-a-child-in-its-owners-conversation-with-its-owner-recorded", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var child = new Slot();
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    child.Id = await nodes.Spawn(tx, runtime.TaskId, "child");
                    return WaitOn([child.Id.Value], JoinPolicy.AllSettled);
                }, ctx),
            });
            var parent = await nodes.Start(root, "parent");
            await Committed(harness, () => child.Id);
            var record = (await harness.GetTaskAsync(child.Id!.Value, Context))!;
            Equal(parent, record.Owner, "owner");
            Equal(root.Id, record.ConversationId, "conversation");
            Equal(false, record.Background, "background");
            Equal(null, (await harness.GetTaskAsync(parent, Context))!.Owner, "parent owner");
            nodes.Open("child");
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-no-ownership-a-missing-owner-a-child-in-another-conversation-and-a-background-child", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var parent = await nodes.Start(root, "parent");
            var other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            Task<TaskId> Create(TaskOptions options) => root.CommitAsync(tx => tx.CreateTaskAsync(nodes.Node, NodeInput("x"), options), Context);
            await ThrowsAsync<Exception>(() => Create(new TaskOptions((TaskOwnership)null!)), "no ownership");
            await RejectsAsync(() => Create(Owned(new TaskId(999_999))), "does not exist", "missing owner");
            await RejectsAsync(() => Create(Owned(parent) with { ConversationId = other.Id }), "owner's conversation", "other conversation");
            await RejectsAsync(() => Create(Owned(parent) with { Background = true }), "cannot be background", "background child");
            // The owner's conversation is the default, even from a commit bound to another conversation.
            var child = await other.CommitAsync(tx => nodes.Spawn(tx, parent, "child"), Context);
            Equal(root.Id, (await harness.GetTaskAsync(child, Context))!.ConversationId, "child conversation");
            nodes.Open("parent");
            nodes.Open("child");
            await harness.WaitForTaskAsync(parent, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-new-owned-work-below-an-owner-that-is-completing-terminal-or-abort-marked", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    await nodes.Spawn(tx, runtime.TaskId, "child");
                    return ResumeAt(1);
                }, ctx),
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Completing);
            Task CreateChild() => root.CommitAsync(tx => nodes.Spawn(tx, parent, "late"), Context);
            Task CreateConversation() => root.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Task(parent)), Context);
            await RejectsAsync(CreateChild, "is completing", "child below completing");
            await RejectsAsync(CreateConversation, "is completing", "conversation below completing");
            nodes.Open("child");
            await harness.WaitForTaskAsync(parent, Context);
            await RejectsAsync(CreateChild, "is terminal", "child below terminal");
            await RejectsAsync(CreateConversation, "is terminal", "conversation below terminal");

            nodes.Script("slow", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.slow").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            var slow = await nodes.Start(root, "slow");
            await Eventually(() => nodes.Logged("run:slow"));
            await harness.AbortTaskAsync(slow, Context);
            await RejectsAsync(() => root.CommitAsync(tx => nodes.Spawn(tx, slow, "late"), Context), "is abort-marked", "child below abort-marked");
            nodes.Open("abort.slow");
            Equal("aborted", await OutcomeOf(harness, slow), "slow outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cannot-create-a-child-in-its-finishing-commit-but-work-it-starts-in-an-owned-conversation-holds-it", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            nodes.Script("eager", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    await nodes.Spawn(tx, runtime.TaskId, "never");
                    return End(Ending.Completed, "eager");
                }, ctx),
            });
            var eager = await nodes.Start(root, "eager");
            Faulted(OutcomeIn(await harness.WaitForTaskAsync(eager, Context)), "is completing", "eager outcome");
            // The rejected commit wrote nothing.
            var tasks = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "test.node" }, 20), Context);
            Seq(["eager"], tasks.Items.Select(task => S(((JsonObjectValue)task.Input)["name"])), "node names");

            var child = new ConversationSlot();
            nodes.Script("host", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    child.Id = (await tx.CreateConversationAsync(ConversationOwnership.Task(runtime.TaskId))).Id;
                    return ResumeAt(1);
                }, ctx),
                Resume = (runtime, ctx, _) => runtime.CommitAsync(async (tx, _) =>
                {
                    await tx.CreateTaskAsync(nodes.Node, NodeInput("inner"), In(child.Id!.Value));
                    return End(Ending.Completed, "host");
                }, ctx),
            });
            var host = await nodes.Start(root, "host");
            await Eventually(async () => await StatusOf(harness, host) == DurableTaskStatus.Completing);
            Check(!await Settled(harness.WaitForTaskAsync(host, Context)), "host still held");
            nodes.Open("inner");
            Equal("completed", await OutcomeOf(harness, host), "host outcome");
            await harness.CloseAsync(Context);
        });
    }

    // ─── Waiting ────────────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> Waiting(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "resumes-once-every-awaited-task-is-terminal-and-reads-their-outcomes-in-order-allsettled", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var found = new Found();
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    foreach (var name in new[] { "ok", "fails", "throws", "aborted" }) found.Add(await nodes.Spawn(tx, runtime.TaskId, name));
                    found.Add(await tx.CreateTaskAsync(Unregistered, NodeInput("orphan"), Owned(runtime.TaskId)));
                    return WaitOn(found.Ids, JoinPolicy.AllSettled);
                }, ctx),
                Resume = async (runtime, ctx, _) =>
                {
                    found.Outcomes = [.. (await runtime.OutcomesAsync(found.Ids, ctx)).Select(outcome => Wire(outcome.Status))];
                    await runtime.CommitAsync((_, _) => End(Ending.Completed, "parent"), ctx);
                },
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(() => found.Ids.Count == 5);
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Waiting);
            var ids = found.Ids;
            nodes.Open("ok");
            nodes.Open("fails", Ending.Failed);
            nodes.Open("throws", Ending.Throw);
            await harness.AbortTaskAsync(ids[3], Context);
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(ids[4], Context), "orphan abort");
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            Seq(["completed", "failed", "faulted", "aborted", "orphaned"], found.Outcomes, "outcomes");
            // allSettled never marks siblings.
            Seq(["abort:aborted"], nodes.Aborts, "aborts");
            await harness.CloseAsync(Context);
        });

        foreach (var ending in new[] { Ending.Failed, Ending.Throw })
        {
            var label = ending == Ending.Throw ? "faulted" : "failed";
            yield return Case(prefix + $"fails-fast-when-a-child-ends-{label}-its-live-siblings-are-aborted-the-parent-is-not", async () =>
            {
                var nodes = new Nodes();
                var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
                var found = nodes.ParentOf("checkout", ["p1", "p2", "p3", "p4"], JoinPolicy.FailFast);
                var parent = await nodes.Start(root, "checkout");
                await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Waiting);
                nodes.Open("p2", ending);
                Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
                Seq(["aborted", label, "aborted", "aborted"], found.Outcomes, "outcomes");
                Equal(false, (await harness.GetTaskAsync(parent, Context))!.AbortRequested, "parent abort mark");
                Seq(["abort:p1", "abort:p3", "abort:p4"], nodes.Aborts.Order(StringComparer.Ordinal), "aborts");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "fails-fast-on-a-held-failure-before-the-failing-child-drains", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var found = new Found();
            nodes.Script("p1", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    await nodes.Spawn(tx, runtime.TaskId, "grandchild");
                    return ResumeAt(1);
                }, ctx),
                Resume = (runtime, ctx, _) => runtime.CommitAsync((_, _) => End(Ending.Failed, "p1"), ctx),
            });
            nodes.Script("grandchild", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.grandchild").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            var outside = new Slot();
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    foreach (var name in new[] { "p1", "p2" }) found.Add(await nodes.Spawn(tx, runtime.TaskId, name));
                    outside.Id = await nodes.Spawn(tx, runtime.TaskId, "outside");
                    return WaitOn(found.Ids, JoinPolicy.FailFast);
                }, ctx),
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(() => found.Ids.Count == 2);
            var ids = found.Ids;
            // p1 holds `failed` while its grandchild's abort handler runs; p2 is already aborted.
            Equal("aborted", await OutcomeOf(harness, ids[1]), "p2 outcome");
            var p1 = await State(harness, ids[0]);
            Equal(DurableTaskStatus.Completing, p1.Status, "p1 status");
            Equal(TaskOutcomeStatus.Failed, OutcomeIn(p1).Status, "p1 held outcome");
            Equal(DurableTaskStatus.Waiting, await StatusOf(harness, parent), "parent status");
            nodes.Open("abort.grandchild");
            await Eventually(() => nodes.Logged("resume:parent"));
            // Only the other tasks in `on` are marked: not the failed one, not the parent, not a child outside `on`.
            async Task<bool> Marked(TaskId id) => (await harness.GetTaskAsync(id, Context))!.AbortRequested;
            Equal("failed", await OutcomeOf(harness, ids[0]), "p1 outcome");
            Seq([false, true, false], [await Marked(ids[0]), await Marked(ids[1]), await Marked(outside.Id!.Value)], "marks");
            Equal(false, await Marked(parent), "parent mark");
            Equal(DurableTaskStatus.Running, await StatusOf(harness, outside.Id!.Value), "outside status");
            // Finished, the parent holds for the child outside `on`.
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Completing);
            nodes.Open("outside");
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "waits-with-allsettled-on-tasks-it-does-not-own-including-already-terminal-ones", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var done = await nodes.Start(root, "done");
            nodes.Open("done");
            await harness.WaitForTaskAsync(done, Context);
            var live = await nodes.Start(root, "live");
            List<string> outcomes = [];
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([done, live], JoinPolicy.AllSettled), ctx),
                Resume = async (runtime, ctx, _) =>
                {
                    outcomes = [.. (await runtime.OutcomesAsync([done, live], ctx)).Select(outcome => Wire(outcome.Status))];
                    await runtime.CommitAsync((_, _) => End(Ending.Completed, "parent"), ctx);
                },
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Waiting);
            // A task it does not own is not its work: the parent may finish while it lives, but here it waits for it.
            nodes.Open("live", Ending.Failed);
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            Seq(["completed", "failed"], outcomes, "outcomes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-waits-on-itself-its-owner-a-missing-task-and-failfast-on-a-task-it-does-not-own", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var other = await nodes.Start(root, "other");
            (string Name, Func<ITaskRuntime, TaskId[]> On, JoinPolicy Policy, string Message)[] cases =
            [
                ("self", runtime => [runtime.TaskId], JoinPolicy.AllSettled, "is a wait cycle"),
                ("missing", _ => [new TaskId(999_999)], JoinPolicy.AllSettled, "does not exist"),
                ("foreign", _ => [other], JoinPolicy.FailFast, "only on tasks it owns"),
            ];
            foreach (var (name, on, policy, message) in cases)
            {
                nodes.Script(name, new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn(on(runtime), policy), ctx) });
                var id = await nodes.Start(root, name);
                Faulted(OutcomeIn(await harness.WaitForTaskAsync(id, Context)), message, name);
            }
            // A child waiting on its owner could never resume.
            var child = new Slot();
            // Upstream's child reads `parent` after `await start(...)` assigned it, a macrotask before the run; here a deferred orders it.
            var parentKnown = new Deferred<TaskId>();
            nodes.Script("child", new Behavior
            {
                Run = async (runtime, ctx) =>
                {
                    var owner = await parentKnown.Task;
                    await runtime.CommitAsync((_, _) => WaitOn([owner], JoinPolicy.AllSettled), ctx);
                },
            });
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    child.Id = await nodes.Spawn(tx, runtime.TaskId, "child");
                    return WaitOn([child.Id.Value], JoinPolicy.AllSettled);
                }, ctx),
            });
            var parent = await nodes.Start(root, "parent");
            parentKnown.Resolve(parent);
            Equal(TaskOutcomeStatus.Completed, OutcomeIn(await harness.WaitForTaskAsync(parent, Context)).Status, "parent outcome");
            Faulted(OutcomeIn(await harness.WaitForTaskAsync(child.Id!.Value, Context)), "is a wait cycle", "child");
            nodes.Open("other");
            await harness.CloseAsync(Context);
        });

        // #10411; this test and the next two are from #10533 by @odysseus0.
        yield return Case(prefix + "rejects-the-wait-that-closes-a-cycle", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var a = new Slot();
            var b = new Slot();
            nodes.Script("a", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([b.Id!.Value], JoinPolicy.AllSettled), ctx) });
            nodes.Script("b", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([a.Id!.Value], JoinPolicy.AllSettled), ctx) });
            await root.CommitAsync(async tx =>
            {
                a.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("a"), OwnConversation);
                b.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("b"), OwnConversation);
            }, Context);
            // a -> b and b -> a are waits, and whichever comes second closes the cycle.
            var outcomes = await Task.WhenAll(OutcomeOf(harness, a.Id!.Value), OutcomeOf(harness, b.Id!.Value));
            Seq(["completed", "faulted"], [.. outcomes.Order(StringComparer.Ordinal)], "outcomes");
            await harness.CloseAsync(Context);
        });

        // #10411
        yield return Case(prefix + "rejects-the-wait-that-closes-a-cycle-through-a-held-owner", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var held = new Slot();
            var other = new Slot();
            var child = nodes.SpawnAndFinish("held", "child");
            nodes.Script("child", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([other.Id!.Value], JoinPolicy.AllSettled), ctx) });
            nodes.Script("other", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([held.Id!.Value], JoinPolicy.AllSettled), ctx) });
            await root.CommitAsync(async tx =>
            {
                held.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("held"), OwnConversation);
                other.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("other"), OwnConversation);
            }, Context);
            // held -> child is a hold; child -> other -> held are waits, and whichever wait comes second closes the cycle.
            Equal("completed", await OutcomeOf(harness, held.Id!.Value), "held outcome");
            var outcomes = await Task.WhenAll(OutcomeOf(harness, child.Id!.Value), OutcomeOf(harness, other.Id!.Value));
            Seq(["completed", "faulted"], [.. outcomes.Order(StringComparer.Ordinal)], "outcomes");
            await harness.CloseAsync(Context);
        });

        // #10411
        yield return Case(prefix + "rejects-a-cycle-through-holds-two-levels-deep", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var held = new Slot();
            var other = new Slot();
            var child = nodes.SpawnAndFinish("held", "child");
            var grandchild = nodes.SpawnAndFinish("child", "grandchild");
            nodes.Script("grandchild", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([other.Id!.Value], JoinPolicy.AllSettled), ctx) });
            nodes.Script("other", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([held.Id!.Value], JoinPolicy.AllSettled), ctx) });
            await root.CommitAsync(async tx =>
            {
                held.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("held"), OwnConversation);
                other.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("other"), OwnConversation);
            }, Context);
            // held -> child -> grandchild are holds; grandchild -> other -> held are waits.
            await Eventually(() => grandchild.Id is not null);
            var outcomes = await Task.WhenAll(OutcomeOf(harness, grandchild.Id!.Value), OutcomeOf(harness, other.Id!.Value));
            Seq(["completed", "faulted"], [.. outcomes.Order(StringComparer.Ordinal)], "outcomes");
            Equal("completed", await OutcomeOf(harness, child.Id!.Value), "child outcome");
            await harness.CloseAsync(Context);
        });

        // #10411
        yield return Case(prefix + "lets-a-background-task-wait-on-the-owner-of-its-conversation", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var owner = new Slot();
            nodes.Script("background", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([owner.Id!.Value], JoinPolicy.AllSettled), ctx) });
            var background = await root.CommitAsync(async tx =>
            {
                owner.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("owner"), OwnConversation);
                var conversation = await tx.CreateConversationAsync(ConversationOwnership.Task(owner.Id.Value));
                return await tx.CreateTaskAsync(nodes.Node, NodeInput("background"), In(conversation.Id) with { Background = true });
            }, Context);
            // background -> owner is a wait but owner -> background is no hold, so there is no cycle.
            await Eventually(async () => await StatusOf(harness, background) == DurableTaskStatus.Waiting);
            nodes.Open("owner");
            Equal("completed", await OutcomeOf(harness, background), "background outcome");
            await harness.CloseAsync(Context);
        });

        // #10411: the issue's orchestrator, worker, and reviewer.
        yield return Case(prefix + "rejects-a-cycle-of-a-hold-and-two-waits-that-each-look-reasonable", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var orchestrator = new Slot();
            var worker = new Slot();
            var reviewer = new Slot();
            var reviewerWaits = new HarnessTests.Deferred();
            // Upstream's worker reads `ids.reviewer` after `await start(...)` assigned it, a macrotask before the run; here a deferred orders it.
            var reviewerKnown = new HarnessTests.Deferred();
            // The orchestrator spawns a worker and finishes; the running worker holds it as completing.
            nodes.Script("orchestrator", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    worker.Id = await nodes.Spawn(tx, runtime.TaskId, "worker");
                    return ResumeAt(1);
                }, ctx),
            });
            // The reviewer waits for the orchestrator's result.
            nodes.Script("reviewer", new Behavior
            {
                Run = async (runtime, ctx) =>
                {
                    await runtime.CommitAsync((_, _) => WaitOn([orchestrator.Id!.Value], JoinPolicy.AllSettled), ctx);
                    reviewerWaits.Resolve();
                },
            });
            // The worker waits for the reviewer, closing the cycle.
            nodes.Script("worker", new Behavior
            {
                Run = async (runtime, ctx) =>
                {
                    await reviewerWaits.Task;
                    await reviewerKnown.Task;
                    await runtime.CommitAsync((_, _) => WaitOn([reviewer.Id!.Value], JoinPolicy.AllSettled), ctx);
                },
            });
            orchestrator.Id = await nodes.Start(root, "orchestrator");
            await Eventually(async () => await StatusOf(harness, orchestrator.Id!.Value) == DurableTaskStatus.Completing);
            reviewer.Id = await nodes.Start(root, "reviewer");
            reviewerKnown.Resolve();
            var outcomes = await Task.WhenAll(new[] { orchestrator, worker, reviewer }.Select(slot => OutcomeOf(harness, slot.Id!.Value)));
            Seq(["completed", "faulted", "completed"], [.. outcomes], "outcomes");
            var outcome = OutcomeIn(await harness.WaitForTaskAsync(worker.Id!.Value, Context));
            var text = outcome.ToJson().ToJson();
            var cycle = $"{worker.Id} -> {reviewer.Id} -> {orchestrator.Id} -> {worker.Id} is a wait cycle";
            Check(text.Contains(cycle, StringComparison.Ordinal), $"expected <{cycle}> in {text}");
            await harness.CloseAsync(Context);
        });

        // #10411
        yield return Case(prefix + "allows-a-diamond-of-waits", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var top = new Slot();
            var left = new Slot();
            var right = new Slot();
            var bottom = new Slot();
            // A diamond: top waits on left and right, both wait on bottom. No cycle.
            nodes.Script("top", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([left.Id!.Value, right.Id!.Value], JoinPolicy.AllSettled), ctx) });
            nodes.Script("left", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([bottom.Id!.Value], JoinPolicy.AllSettled), ctx) });
            nodes.Script("right", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([bottom.Id!.Value], JoinPolicy.AllSettled), ctx) });
            await root.CommitAsync(async tx =>
            {
                bottom.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("bottom"), OwnConversation);
                left.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("left"), OwnConversation);
                right.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("right"), OwnConversation);
                top.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("top"), OwnConversation);
            }, Context);
            nodes.Open("bottom");
            Equal("completed", await OutcomeOf(harness, top.Id!.Value), "top outcome");
            await harness.CloseAsync(Context);
        });

        // #10411
        yield return Case(prefix + "follows-no-wait-of-an-abort-marked-task-which-waits-only-for-its-owned-work", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var a = new Slot();
            var b = new Slot();
            var c = new Slot();
            var cleanup = new HarnessTests.Deferred();
            // b owns c and waits on a; aborting b leaves c's cleanup pending, so b waits only for c.
            nodes.Script("b", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    c.Id = await nodes.Spawn(tx, runtime.TaskId, "c");
                    return WaitOn([a.Id!.Value], JoinPolicy.AllSettled);
                }, ctx),
            });
            nodes.Script("c", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await cleanup.Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Terminal(new AbortedTaskOutcome(null, "c")), ctx);
                },
            });
            var aWaits = new HarnessTests.Deferred();
            nodes.Script("a", new Behavior
            {
                Run = async (runtime, ctx) =>
                {
                    await aWaits.Task;
                    await runtime.CommitAsync((_, _) => WaitOn([b.Id!.Value], JoinPolicy.AllSettled), ctx);
                },
                Resume = (runtime, ctx, _) => runtime.CommitAsync((_, _) => End(Ending.Completed, "a"), ctx),
            });
            await root.CommitAsync(async tx =>
            {
                a.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("a"), OwnConversation);
                b.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("b"), OwnConversation);
            }, Context);
            await Eventually(async () => await StatusOf(harness, b.Id!.Value) == DurableTaskStatus.Waiting);
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(b.Id!.Value, Context), "abort b");
            await Eventually(() => nodes.Logged("abort:c"));
            // a -> b would close a -> b -> a through b's stale wait; b now waits only for c.
            aWaits.Resolve();
            await Eventually(async () => await StatusOf(harness, a.Id!.Value) == DurableTaskStatus.Waiting);
            cleanup.Resolve();
            Equal("aborted", await OutcomeOf(harness, b.Id!.Value), "b outcome");
            Equal("completed", await OutcomeOf(harness, a.Id!.Value), "a outcome");
            await harness.CloseAsync(Context);
        });

        // #10411
        yield return Case(prefix + "rejects-a-longer-cycle-of-waits", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            string[] names = ["x", "y", "z"];
            var ids = names.ToDictionary(name => name, _ => new Slot(), StringComparer.Ordinal);
            for (var index = 0; index < names.Length; index++)
            {
                var next = ids[names[(index + 1) % names.Length]];
                nodes.Script(names[index], new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([next.Id!.Value], JoinPolicy.AllSettled), ctx) });
            }
            await root.CommitAsync(async tx =>
            {
                foreach (var name in names) ids[name].Id = await tx.CreateTaskAsync(nodes.Node, NodeInput(name), OwnConversation);
            }, Context);
            var outcomes = await Task.WhenAll(names.Select(name => OutcomeOf(harness, ids[name].Id!.Value)));
            Equal(1, outcomes.Count(outcome => outcome == "faulted"), "faulted");
            Equal(2, outcomes.Count(outcome => outcome == "completed"), "completed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-at-the-next-pass-when-it-waits-on-nothing", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            nodes.Script("parent", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([], JoinPolicy.FailFast), ctx) });
            Equal("completed", await OutcomeOf(harness, await nodes.Start(root, "parent")), "parent outcome");
            Seq(["run:parent", "resume:parent"], nodes.Lines, "log");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-running-phases-after-spawning-and-waits-on-subsets-in-sequence", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var found = new Found();
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    foreach (var name in new[] { "a", "b", "c" }) found.Add(await nodes.Spawn(tx, runtime.TaskId, name));
                    return ResumeAt(0);
                }, ctx),
                Resume = async (runtime, ctx, round) =>
                {
                    nodes.Log.Add($"round:{round}");
                    var spawned = found.Ids;
                    if (round == 0) await runtime.CommitAsync((_, _) => WaitOn([spawned[0]], JoinPolicy.AllSettled, 1), ctx);
                    else if (round == 1) await runtime.CommitAsync((_, _) => WaitOn([spawned[1], spawned[2]], JoinPolicy.FailFast, 2), ctx);
                    else await runtime.CommitAsync((_, _) => End(Ending.Completed, "parent"), ctx);
                },
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(() => nodes.Logged("round:0"));
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Waiting);
            var ids = found.Ids;
            nodes.Open("b");
            // Upstream: expect(await settled(waitForTask(b))).toBe(true).
            await harness.WaitForTaskAsync(ids[1], Context);
            Equal(DurableTaskStatus.Waiting, await StatusOf(harness, parent), "parent status");
            nodes.Open("a");
            await Eventually(() => nodes.Logged("round:1"));
            nodes.Open("c");
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            Seq(["round:0", "round:1", "round:2"], nodes.Lines.Where(line => line.StartsWith("round:", StringComparison.Ordinal)), "rounds");
            await harness.CloseAsync(Context);
        });
    }

    // ─── Completing ─────────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> Completing(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "holds-a-finished-task-until-its-owned-work-drains-waiters-and-task-documents-wait-for-the-final-commit", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var child = new Slot();
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    (await tx.DocAsync(TaskNotes, runtime.TaskId)).Set("text", "notes");
                    child.Id = await nodes.Spawn(tx, runtime.TaskId, "child");
                    return ResumeAt(1);
                }, ctx),
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Completing);
            SameJson("""{"status":"completing","outcome":{"status":"completed","result":"parent"}}""", (await State(harness, parent)).ToJson(), "held state");
            var waiter = harness.WaitForTaskAsync(parent, Context);
            Check(!await Settled(waiter), "the waiter waits for the final commit");
            SameJson("""{"text":"notes"}""", await harness.SnapshotAsync(TaskNotes, parent, Context), "task notes while held");
            SameJson("""{"kind":"completing"}""", await Inspected(harness, parent), "inspection");
            Check(!await Settled(root.WaitForIdleAsync(Context)), "the root is busy");
            nodes.Open("child");
            SameJson("""{"status":"completed","result":"parent"}""", OutcomeIn(await waiter).ToJson(), "final outcome");
            Check(await harness.SnapshotAsync(TaskNotes, parent, Context) is null, "task notes retired");
            await root.WaitForIdleAsync(Context);
            Check(child.Id is not null, "child spawned");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-holding-for-ordinary-work-created-during-the-hold", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var conversation = new ConversationSlot();
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    conversation.Id = (await tx.CreateConversationAsync(ConversationOwnership.Task(runtime.TaskId))).Id;
                    await tx.CreateTaskAsync(nodes.Node, NodeInput("first"), In(conversation.Id.Value));
                    return ResumeAt(1);
                }, ctx),
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Completing);
            var child = (await harness.ConversationAsync(conversation.Id!.Value, Context))!;
            await nodes.Start(child, "second");
            nodes.Open("first");
            await Eventually(() => nodes.Logged("run:second"));
            Equal(DurableTaskStatus.Completing, await StatusOf(harness, parent), "parent status");
            nodes.Open("second");
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            await harness.CloseAsync(Context);
        });

        foreach (var (label, ending) in new[] { ("a-held-failure", Ending.Failed), ("a-held-scheduler-fault", Ending.Throw) })
        {
            yield return Case(prefix + $"aborts-the-work-below-{label}-then-finishes-with-the-held-outcome", async () =>
            {
                var nodes = new Nodes();
                var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
                var child = new Slot();
                nodes.Script("parent", new Behavior
                {
                    Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                    {
                        child.Id = await nodes.Spawn(tx, runtime.TaskId, "child");
                        return ResumeAt(1);
                    }, ctx),
                    Resume = async (runtime, ctx, _) =>
                    {
                        if (ending == Ending.Throw) throw new InvalidOperationException("parent threw");
                        await runtime.CommitAsync((_, _) => End(Ending.Failed, "parent"), ctx);
                    },
                });
                var parent = await nodes.Start(root, "parent");
                await Committed(harness, () => child.Id);
                Equal("aborted", await OutcomeOf(harness, child.Id!.Value), "child outcome");
                Equal(ending == Ending.Throw ? "faulted" : "failed", await OutcomeOf(harness, parent), "parent outcome");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "only-marks-a-completing-task-when-aborted-the-work-below-is-aborted-the-held-outcome-stays", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var child = nodes.SpawnAndFinish("parent", "child");
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Completing);
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(parent, Context), "abort");
            Equal("aborted", await OutcomeOf(harness, child.Id!.Value), "child outcome");
            var settledParent = await harness.WaitForTaskAsync(parent, Context);
            SameJson("""{"status":"completed","result":"parent"}""", OutcomeIn(settledParent).ToJson(), "parent outcome");
            Equal(true, settledParent.AbortRequested, "parent abort mark");
            Check(!nodes.Logged("abort:parent"), "no abort handler for the parent");
            Equal(TaskAbortResult.Terminal, await harness.AbortTaskAsync(parent, Context), "abort again");
            await harness.CloseAsync(Context);
        });
    }

    // ─── Abort order ────────────────────────────────────────────────────────

    /// <summary>Scripts <paramref name="name"/> to spawn <paramref name="child"/> and wait on it; its abort handler records the child's
    /// status first (upstream <c>chain</c>).</summary>
    private static Slot Chain(Nodes nodes, Func<IHarness> harness, string name, string child)
    {
        var found = new Slot();
        nodes.Script(name, new Behavior
        {
            Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
            {
                found.Id = await nodes.Spawn(tx, runtime.TaskId, child);
                return WaitOn([found.Id.Value], JoinPolicy.AllSettled);
            }, ctx),
            Abort = async (runtime, ctx) =>
            {
                nodes.Log.Add($"{name} saw {Wire((await harness().GetTaskAsync(found.Id!.Value, Context))!.State.Status)}");
                await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
            },
        });
        return found;
    }

    private static IEnumerable<(string, Func<Task>)> AbortOrder(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "runs-abort-handlers-bottom-up-across-three-levels-each-after-the-level-below-is-terminal", async () =>
        {
            var nodes = new Nodes();
            Opened? opened = null;
            var b = Chain(nodes, () => opened!.Harness, "a", "b");
            var c = Chain(nodes, () => opened!.Harness, "b", "c");
            opened = await nodes.OpenNodes(await fresh());
            var (harness, root, _, _) = opened;
            var a = await nodes.Start(root, "a");
            await Eventually(() => nodes.Logged("run:c"));
            await Committed(harness, () => c.Id);
            await Eventually(async () => await StatusOf(harness, c.Id!.Value) == DurableTaskStatus.Running);
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(a, Context), "abort");
            Equal("aborted", await OutcomeOf(harness, a), "a outcome");
            Seq(["abort:c", "abort:b", "b saw terminal", "abort:a", "a saw terminal"],
                nodes.Lines.Where(line => line.StartsWith("abort:", StringComparison.Ordinal) || line.Contains(" saw ", StringComparison.Ordinal)), "order");
            Check(b.Id is not null, "b spawned");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-wait-for-a-task-it-waits-on-but-does-not-own", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var other = await nodes.Start(root, "other");
            nodes.Script("parent", new Behavior { Run = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([other], JoinPolicy.AllSettled), ctx) });
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Waiting);
            await harness.AbortTaskAsync(parent, Context);
            Equal("aborted", await OutcomeOf(harness, parent), "parent outcome");
            Equal(DurableTaskStatus.Running, await StatusOf(harness, other), "other status");
            nodes.Open("other");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-an-abort-marked-task-as-waiting-for-its-live-owned-work", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var child = new Slot();
            nodes.Script("child", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.child").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            nodes.Script("parent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    child.Id = await nodes.Spawn(tx, runtime.TaskId, "child");
                    return WaitOn([child.Id.Value], JoinPolicy.AllSettled);
                }, ctx),
            });
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Waiting);
            await harness.AbortTaskAsync(parent, Context);
            await Eventually(() => nodes.Logged("abort:child"));
            SameJson($$"""{"kind":"waiting","on":[{{child.Id!.Value.Value}}]}""", await Inspected(harness, parent), "inspection");
            nodes.Open("abort.child");
            Equal("aborted", await OutcomeOf(harness, parent), "parent outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "faults-an-abort-handler-that-tries-to-wait", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            nodes.Script("parent", new Behavior { Abort = (runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn([], JoinPolicy.AllSettled), ctx) });
            var parent = await nodes.Start(root, "parent");
            await Eventually(() => nodes.Logged("run:parent"));
            await harness.AbortTaskAsync(parent, Context);
            Faulted(OutcomeIn(await harness.WaitForTaskAsync(parent, Context)), "cannot wait", "parent outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "orphans-a-blocked-task-only-after-its-owned-work-drained", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var (owner, child) = await root.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(Unregistered, NodeInput("owner"), OwnConversation);
                return (owner, await nodes.Spawn(tx, owner, "child"));
            }, Context);
            await Eventually(() => nodes.Logged("run:child"));
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(owner, Context), "abort");
            Equal("aborted", await OutcomeOf(harness, child), "child outcome");
            SameJson("""{"status":"orphaned","reason":"missing_task"}""", OutcomeIn(await harness.WaitForTaskAsync(owner, Context)).ToJson(), "owner outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cascades-through-task-and-conversation-edges-bottom-up", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var b = new Slot();
            var x = new Slot();
            nodes.Script("a", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    b.Id = await nodes.Spawn(tx, runtime.TaskId, "b");
                    return WaitOn([b.Id.Value], JoinPolicy.AllSettled);
                }, ctx),
            });
            nodes.Script("b", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    var conversation = await tx.CreateConversationAsync(ConversationOwnership.Task(runtime.TaskId));
                    x.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("x"), In(conversation.Id));
                    return ResumeAt(1);
                }, ctx),
                Resume = (runtime, _, _) => Aborted(runtime.Signal),
            });
            var a = await nodes.Start(root, "a");
            await Eventually(() => nodes.Logged("run:x"));
            await harness.AbortTaskAsync(a, Context);
            Equal("aborted", await OutcomeOf(harness, a), "a outcome");
            Equal("aborted", await OutcomeOf(harness, x.Id!.Value), "x outcome");
            Seq(["abort:x", "abort:b", "abort:a"], nodes.Aborts, "aborts");
            await harness.CloseAsync(Context);
        });
    }

    // ─── Background and terminal owners ─────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> Boundaries(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "keeps-background-work-through-conversation-abort-background-true-aborts-it-and-waits", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var foreground = await nodes.Start(root, "foreground");
            var child = new Slot();
            var below = new Slot();
            nodes.Script("background", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    child.Id = await nodes.Spawn(tx, runtime.TaskId, "child");
                    var conversation = await tx.CreateConversationAsync(ConversationOwnership.Task(runtime.TaskId));
                    below.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("below"), In(conversation.Id));
                    return WaitOn([child.Id.Value], JoinPolicy.AllSettled);
                }, ctx),
            });
            var background = await nodes.Start(root, "background", Background);
            await Eventually(() => nodes.Logged("run:below") && nodes.Logged("run:child"));
            await root.AbortAsync(Context);
            Equal("aborted", await OutcomeOf(harness, foreground), "foreground outcome");
            foreach (var id in new[] { background, child.Id!.Value, below.Id!.Value })
                Check(await StatusOf(harness, id) != DurableTaskStatus.Terminal, $"task {id} survives");
            await root.AbortAsync(Context, new ConversationAbortOptions { Background = true });
            foreach (var id in new[] { background, child.Id!.Value, below.Id!.Value })
                Equal(DurableTaskStatus.Terminal, await StatusOf(harness, id), $"task {id} status");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "never-cascades-from-a-terminal-owner-an-aborted-subagents-conversation-runs-new-work-normally", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var conversation = new ConversationSlot();
            nodes.Script("agent", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    conversation.Id = (await tx.CreateConversationAsync(ConversationOwnership.Task(runtime.TaskId))).Id;
                    return ResumeAt(1);
                }, ctx),
                Resume = (runtime, _, _) => Aborted(runtime.Signal),
            });
            var agent = await nodes.Start(root, "agent");
            await Eventually(() => nodes.Logged("resume:agent"));
            await harness.AbortTaskAsync(agent, Context);
            Equal("aborted", await OutcomeOf(harness, agent), "agent outcome");
            var child = (await harness.ConversationAsync(conversation.Id!.Value, Context))!;
            var question = await nodes.Start(child, "question");
            nodes.Open("question");
            Equal("completed", await OutcomeOf(harness, question), "question outcome");
            await harness.CloseAsync(Context);
        });
    }

    // ─── Recovery ───────────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> Recovery(string prefix, Func<OpenLocation> reopenable)
    {
        yield return Case(prefix + "resumes-a-parent-whose-awaited-children-finished-before-the-crash", async () =>
        {
            var nodes = new Nodes();
            var location = reopenable();
            var (parent, children) = await Seed(nodes, location, ["c1"], async (set, seeded) =>
            {
                await set(seeded.Children[0], WithState(CompletedDone));
                await set(seeded.Parent, WithState(SeededWait(seeded.Children, JoinPolicy.AllSettled)));
            });
            var (harness, _, _, _) = await nodes.OpenNodes(await location());
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            Seq(["resume:parent"], nodes.Lines, "log");
            Equal(1, children.Count, "children");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "marks-failfast-siblings-a-crash-left-unmarked", async () =>
        {
            var nodes = new Nodes();
            var location = reopenable();
            var (parent, children) = await Seed(nodes, location, ["c1", "c2"], async (set, seeded) =>
            {
                await set(seeded.Children[0], WithState(FailedDeclined));
                await set(seeded.Parent, WithState(SeededWait(seeded.Children, JoinPolicy.FailFast)));
            });
            var (harness, _, _, _) = await nodes.OpenNodes(await location());
            Equal("aborted", await OutcomeOf(harness, children[1]), "c2 outcome");
            Equal("completed", await OutcomeOf(harness, parent), "parent outcome");
            Check(!nodes.Logged("run:c2"), "c2 never ran");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "finalizes-a-held-outcome-whose-work-drained-before-the-crash-and-keeps-one-whose-work-lives", async () =>
        {
            var nodes = new Nodes();
            var held = new CompletingTaskState(new CompletedTaskOutcome("parent"));
            var location = reopenable();
            var (parent, children) = await Seed(nodes, location, ["c1"], async (set, seeded) => await set(seeded.Parent, WithState(held)));
            var opened = await nodes.OpenNodes(await location());
            await Eventually(() => nodes.Logged("run:c1"));
            SameJson(held.ToJson().ToJson(), (await State(opened.Harness, parent)).ToJson(), "held state");
            await opened.Harness.CloseAsync(Context);

            opened = await nodes.OpenNodes(await location());
            nodes.Open("c1");
            Equal("completed", await OutcomeOf(opened.Harness, children[0]), "c1 outcome");
            Equal("completed", await OutcomeOf(opened.Harness, parent), "parent outcome");
            await opened.Harness.CloseAsync(Context);

            var drained = reopenable();
            var second = await Seed(nodes, drained, ["c1"], async (set, seeded) =>
            {
                await set(seeded.Children[0], WithState(CompletedDone));
                await set(seeded.Parent, WithState(held));
            });
            opened = await nodes.OpenNodes(await drained());
            Equal("completed", await OutcomeOf(opened.Harness, second.Parent), "drained parent outcome");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-a-bottom-up-abort-a-crash-interrupted-before-the-cascade", async () =>
        {
            var nodes = new Nodes();
            var location = reopenable();
            var (parent, children) = await Seed(nodes, location, ["c1"], async (set, seeded) =>
                await set(seeded.Parent, record => record with
                {
                    AbortRequested = true,
                    State = SeededWait(seeded.Children, JoinPolicy.AllSettled),
                }));
            var (harness, _, _, _) = await nodes.OpenNodes(await location());
            Equal("aborted", await OutcomeOf(harness, parent), "parent outcome");
            Equal("aborted", await OutcomeOf(harness, children[0]), "c1 outcome");
            Seq(["abort:c1", "abort:parent"], nodes.Aborts, "aborts");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reopens-a-checkout-waiting-on-live-payments-and-finishes-it", async () =>
        {
            var nodes = new Nodes();
            var location = reopenable();
            var found = nodes.ParentOf("checkout", ["p1", "p2"], JoinPolicy.FailFast);
            var opened = await nodes.OpenNodes(await location());
            var parent = await nodes.Start(opened.Root, "checkout");
            await Eventually(async () => await StatusOf(opened.Harness, parent) == DurableTaskStatus.Waiting);
            await Eventually(() => nodes.Logged("run:p1") && nodes.Logged("run:p2"));
            await opened.Harness.CloseAsync(Context);

            opened = await nodes.OpenNodes(await location());
            found.Replace(((WaitingTaskState)await State(opened.Harness, parent)).On);
            nodes.Open("p1");
            nodes.Open("p2");
            Equal("completed", await OutcomeOf(opened.Harness, parent), "parent outcome");
            Seq(["completed", "completed"], found.Outcomes, "outcomes");
            await opened.Harness.CloseAsync(Context);
        });
    }

    // ─── Definitions and waits ──────────────────────────────────────────────

    /// <summary>A waiter of its own kind, so its definition can be removed or replaced (upstream <c>versioned</c>).</summary>
    private static DurableTask Versioned(int version) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.versioned",
        Version = version,
        Initial = _ => O("""{"phase":"wait"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["wait"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Waiting(O("""{"phase":"resume"}"""),
                ((JsonArrayValue)((JsonObjectValue)task.Input)["on"]).Select(id => new TaskId((long)N(id))), JoinPolicy.AllSettled), ctx),
            ["resume"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed("v1"), ctx),
            ["migrated"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed("v2"), ctx),
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
        Migrate = version == 2 ? (input, _, _) => new TaskMigrationResult(input, O("""{"phase":"migrated"}""")) : null,
    });

    private static JsonObjectValue WaiterInput(TaskId on) => Obj(("on", JsonValue.Array(on.ToJson())));

    private static IEnumerable<(string, Func<Task>)> DefinitionsAndWaits(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "keeps-a-waiting-task-blocked-without-its-definition-and-resumes-it-migrated-under-a-newer-one", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, registry, _) = await nodes.OpenNodes(await fresh());
            var registration = AddTask(registry, Versioned(1));
            var other = await nodes.Start(root, "other");
            var waiter = await root.CommitAsync(tx => tx.CreateTaskAsync(Versioned(1), WaiterInput(other), OwnConversation), Context);
            await Eventually(async () => await StatusOf(harness, waiter) == DurableTaskStatus.Waiting);
            registration.Dispose();
            nodes.Open("other");
            await harness.WaitForTaskAsync(other, Context);
            SameJson("""{"kind":"blocked","reason":"missing_task"}""", await Inspected(harness, waiter), "inspection");
            Equal(DurableTaskStatus.Waiting, await StatusOf(harness, waiter), "waiter status");
            AddTask(registry, Versioned(2));
            SameJson("""{"status":"completed","result":"v2"}""", OutcomeIn(await harness.WaitForTaskAsync(waiter, Context)).ToJson(), "waiter outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "orphans-an-aborted-waiting-task-without-its-definition-at-once-leaving-the-task-it-waits-on-running", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, registry, _) = await nodes.OpenNodes(await fresh());
            var registration = AddTask(registry, Versioned(1));
            var other = await nodes.Start(root, "other");
            var waiter = await root.CommitAsync(tx => tx.CreateTaskAsync(Versioned(1), WaiterInput(other), OwnConversation), Context);
            await Eventually(async () => await StatusOf(harness, waiter) == DurableTaskStatus.Waiting);
            registration.Dispose();
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(waiter, Context), "abort");
            SameJson("""{"status":"orphaned","reason":"missing_task"}""", OutcomeIn(await State(harness, waiter)).ToJson(), "waiter outcome");
            Equal(DurableTaskStatus.Running, await StatusOf(harness, other), "other status");
            nodes.Open("other");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "never-migrates-a-held-outcome-a-newer-definition-leaves-it-and-its-version-alone", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, registry, reports) = await nodes.OpenNodes(await fresh());
            DurableTask Holder(int version) => Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.holder",
                Version = version,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (task, runtime, ctx) =>
                    {
                        await runtime.CommitAsync(async (tx, _) => { await nodes.Spawn(tx, task.Id, "child"); }, ctx);
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed("held"), ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
                Migrate = (_, _, _) => throw new InvalidOperationException("never migrates"),
            });
            AddTask(registry, Holder(1));
            var parent = await root.CommitAsync(tx => tx.CreateTaskAsync(Holder(1), JsonValue.Null, OwnConversation), Context);
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Completing);
            // The same extension name replaces the old one in place.
            AddTask(registry, Holder(2));
            nodes.Open("child");
            var settledParent = await harness.WaitForTaskAsync(parent, Context);
            SameJson("""{"status":"completed","result":"held"}""", OutcomeIn(settledParent).ToJson(), "parent outcome");
            Equal(1, settledParent.Version, "version");
            Seq([], reports.Snapshot().Select(error => error.Message), "reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "treats-a-held-task-as-live-outcomes-rejects-and-a-task-waiting-on-it-resumes-at-its-final-commit", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            nodes.SpawnAndFinish("held", "child");
            var held = await nodes.Start(root, "held");
            await Eventually(async () => await StatusOf(harness, held) == DurableTaskStatus.Completing);
            string? rejection = null;
            nodes.Script("reader", new Behavior
            {
                Run = async (runtime, ctx) =>
                {
                    try
                    {
                        await runtime.OutcomesAsync([held], ctx);
                        Volatile.Write(ref rejection, "resolved");
                    }
                    catch (Exception error)
                    {
                        Volatile.Write(ref rejection, error.Message);
                    }
                    await runtime.CommitAsync((_, _) => WaitOn([held], JoinPolicy.AllSettled), ctx);
                },
            });
            var reader = await nodes.Start(root, "reader");
            await Eventually(async () => await StatusOf(harness, reader) == DurableTaskStatus.Waiting);
            Equal($"Task {held} is not terminal", Volatile.Read(ref rejection), "rejection");
            nodes.Open("child");
            Equal("completed", await OutcomeOf(harness, reader), "reader outcome");
            Check(nodes.Logged("resume:reader"), "the reader resumed");
            await harness.CloseAsync(Context);
        });
    }

    // ─── Conversation abort and boundaries ──────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> ConversationAbort(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "marks-a-held-completed-task-with-conversation-abort-the-work-below-is-aborted-the-outcome-stays", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            var child = nodes.SpawnAndFinish("held", "child");
            var held = await nodes.Start(root, "held");
            await Eventually(async () => await StatusOf(harness, held) == DurableTaskStatus.Completing);
            await root.AbortAsync(Context);
            Equal("aborted", await OutcomeOf(harness, child.Id!.Value), "child outcome");
            var record = await harness.WaitForTaskAsync(held, Context);
            Equal(TaskOutcomeStatus.Completed, OutcomeIn(record).Status, "held outcome");
            Equal(true, record.AbortRequested, "held abort mark");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "marks-and-awaits-only-the-background-work-reached-when-background-true-is-admitted", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            nodes.Script("first", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.first").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            var first = await nodes.Start(root, "first", Background);
            await Eventually(() => nodes.Logged("run:first"));
            var aborting = root.AbortAsync(Context, new ConversationAbortOptions { Background = true });
            await Eventually(async () => (await harness.GetTaskAsync(first, Context))!.AbortRequested);
            var later = await nodes.Start(root, "later", Background);
            nodes.Open("abort.first");
            await aborting;
            Check(await StatusOf(harness, later) != DurableTaskStatus.Terminal, "later survives");
            Equal(false, (await harness.GetTaskAsync(later, Context))!.AbortRequested, "later abort mark");
            await harness.AbortTaskAsync(later, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stops-a-cascade-at-an-unmarked-background-task-but-not-at-a-marked-one", async () =>
        {
            var nodes = new Nodes();
            var (harness, root, _, _) = await nodes.OpenNodes(await fresh());
            nodes.Script("owner", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.owner").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            var tree = await root.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(nodes.Node, NodeInput("owner"), OwnConversation);
                var outer = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                var background = await tx.CreateTaskAsync(nodes.Node, NodeInput("background"), In(outer.Id) with { Background = true });
                var inner = await tx.CreateConversationAsync(ConversationOwnership.Task(background));
                return (Owner: owner, Outer: outer.Id, Background: background, Inner: inner.Id);
            }, Context);
            await Eventually(() => nodes.Logged("run:owner") && nodes.Logged("run:background"));
            // The owner's abort handler starts at once: background work is not its ordinary owned work.
            await harness.AbortTaskAsync(tree.Owner, Context);
            await Eventually(() => nodes.Logged("abort:owner"));
            var inner = (await harness.ConversationAsync(tree.Inner, Context))!;
            var outer = (await harness.ConversationAsync(tree.Outer, Context))!;
            var shielded = await nodes.Start(inner, "shielded");
            var exposed = await nodes.Start(outer, "exposed");
            Equal("aborted", await OutcomeOf(harness, exposed), "exposed outcome");
            Equal(false, (await harness.GetTaskAsync(shielded, Context))!.AbortRequested, "shielded abort mark");
            // Marked directly, the background task cascades into its own subtree.
            await harness.AbortTaskAsync(tree.Background, Context);
            Equal("aborted", await OutcomeOf(harness, shielded), "shielded outcome");
            Equal("aborted", await OutcomeOf(harness, tree.Background), "background outcome");
            nodes.Open("abort.owner");
            Equal("aborted", await OutcomeOf(harness, tree.Owner), "owner outcome");
            await harness.CloseAsync(Context);
        });

        // Pi v1.2.0 replaced "retries a finalization the Storage rejected with the next commit": a failed commit is final (area b).
        yield return Case(prefix + "fails-the-harness-on-a-failed-finalization-which-reopening-applies", async () =>
        {
            var nodes = new Nodes();
            var controlled = new ControlledStorage(await fresh()) { KeepOpenForReopen = true };
            var storage = new FinalizeFailingStorage(controlled);
            var (harness, root, _, reports) = await nodes.OpenNodes(storage);
            nodes.SpawnAndFinish("parent", "child");
            var parent = await nodes.Start(root, "parent");
            await Eventually(async () => await StatusOf(harness, parent) == DurableTaskStatus.Completing);
            storage.Fail = parent;
            nodes.Open("child");
            Equal(new SessionEnd(SessionEndReason.Failed, storage.Failure), await harness.Closed, "closed");
            Seq([storage.Failure], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
            controlled.Reopen();
            var reopened = await nodes.OpenNodes(storage);
            Equal("completed", await OutcomeOf(reopened.Harness, parent), "parent outcome");
            await reopened.Harness.CloseAsync(Context);
            await controlled.Inner.CloseAsync(Context);
        });
    }

    // ─── Recovery through ownership edges ───────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> RecoveryEdges(string prefix, Func<OpenLocation> reopenable)
    {
        yield return Case(prefix + "shows-an-abort-marked-owner-waiting-for-work-in-its-owned-conversation-before-resume-after-reopen", async () =>
        {
            var nodes = new Nodes();
            var location = reopenable();
            var session = new PiSharp.Durable.Session(await location());
            var tree = await session.CommitAsync(async tx =>
            {
                var conversation = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                var owner = await tx.CreateTaskAsync(nodes.Node, NodeInput("owner"), In(conversation.Id));
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                var inner = await tx.CreateTaskAsync(nodes.Node, NodeInput("inner"), In(child.Id));
                return (Owner: owner, Inner: inner);
            }, Context);
            await session.CommitWithAsync(async tx =>
            {
                var record = (await tx.GetTaskAsync(tree.Owner))!;
                tx.SetTask(record with { AbortRequested = true });
            }, Context);
            await session.CloseAsync(Context);

            var (harness, _, _) = await OpenTasks(await location(), [nodes.Node]);
            SameJson($$"""{"kind":"waiting","on":[{{tree.Inner.Value}}]}""", await Inspected(harness, tree.Owner), "inspection");
            harness.Resume();
            Equal("aborted", await OutcomeOf(harness, tree.Owner), "owner outcome");
            Seq(["abort:inner", "abort:owner"], nodes.Aborts, "aborts");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-root-busy-after-reopen-for-work-below-a-child-tasks-owned-conversation", async () =>
        {
            var nodes = new Nodes();
            var location = reopenable();
            var inner = new Slot();
            nodes.Script("child", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    var conversation = await tx.CreateConversationAsync(ConversationOwnership.Task(runtime.TaskId));
                    inner.Id = await tx.CreateTaskAsync(nodes.Node, NodeInput("inner"), In(conversation.Id));
                    return ResumeAt(1);
                }, ctx),
            });
            nodes.ParentOf("parent", ["child"], JoinPolicy.AllSettled);
            var opened = await nodes.OpenNodes(await location());
            var parent = await nodes.Start(opened.Root, "parent");
            await Eventually(() => nodes.Logged("run:inner"));
            await opened.Harness.CloseAsync(Context);

            opened = await nodes.OpenNodes(await location());
            Check(!await Settled(opened.Root.WaitForIdleAsync(Context)), "the root is busy");
            nodes.Open("inner");
            await opened.Root.WaitForIdleAsync(Context);
            Equal("completed", await OutcomeOf(opened.Harness, parent), "parent outcome");
            Check(inner.Id is not null, "inner created");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-finished-background-ancestor-a-boundary-after-reopen-which-background-true-crosses", async () =>
        {
            var nodes = new Nodes();
            var location = reopenable();
            var conversation = new ConversationSlot();
            nodes.Script("child", new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    conversation.Id = (await tx.CreateConversationAsync(ConversationOwnership.Task(runtime.TaskId))).Id;
                    return ResumeAt(1);
                }, ctx),
            });
            nodes.ParentOf("background", ["child"], JoinPolicy.AllSettled);
            var opened = await nodes.OpenNodes(await location());
            var background = await nodes.Start(opened.Root, "background", Background);
            await opened.Harness.WaitForTaskAsync(background, Context);
            await opened.Harness.CloseAsync(Context);

            opened = await nodes.OpenNodes(await location());
            var below = await nodes.Start((await opened.Harness.ConversationAsync(conversation.Id!.Value, Context))!, "below");
            await Eventually(() => nodes.Logged("run:below"));
            await opened.Root.WaitForIdleAsync(Context);
            await opened.Root.AbortAsync(Context);
            Equal(DurableTaskStatus.Running, await StatusOf(opened.Harness, below), "below status");
            await opened.Root.AbortAsync(Context, new ConversationAbortOptions { Background = true });
            Equal("aborted", await OutcomeOf(opened.Harness, below), "below outcome");
            await opened.Harness.CloseAsync(Context);
        });
    }
}
