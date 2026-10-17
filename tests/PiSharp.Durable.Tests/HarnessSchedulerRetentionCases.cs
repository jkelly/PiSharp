// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-scheduler-retention.test.ts. Scheduler memory is bounded
// by live work: an ended task or a conversation edge is kept only while live work is below it, and a chain dropped that way is loaded again
// from storage when work appears below it. Verbatim on SQLite, as upstream, apart from the adaptations of HarnessSchedulerSupport.cs (the
// index sizes are read once the scheduler's pending passes have run) and these:
// - "does not scan queued submissions for an input when nothing cancels" lets any reconcile the input would cause run with upstream's
//   20 ms wait and then the same quiescence barrier, so a scan the thread pool started late is still counted.
// - toMatchObject on the sizes compares the named fields.
// - The spawner and the rejected task read `input.into` from the record their commit rereads (RunningTask in the commit callback), not from
//   the phase's task argument. The case points them at C after they started their phase; upstream's step happens to read the record after
//   that change, while here the reservation and the step run on the thread pool and usually read the earlier record.
// - "aborts work created in a forgotten conversation below an abort-marked owner before it runs": P must still be cancelling when `late`
//   is created. Upstream's `keep` finishes its abort only after the case's commits through microtask order; here `keep`'s abort handler
//   waits for its gate abort.keep, opened once `late` has been checked, and the case then also waits for P's outcome.
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
    private static IEnumerable<(string, Func<Task>)> HarnessSchedulerRetentionCases() => SchedulerTests.RetentionCases();
}

internal static partial class SchedulerTests
{
    internal static IEnumerable<(string, Func<Task>)> RetentionCases()
    {
        const string prefix = "harness.scheduler-retention.";

        yield return Case(prefix + "forgets-ended-subagent-chains-once-their-work-ends", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            var parent = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("parent"), OwnConversation()), Context);
            await fixture.SubagentCycle(harness, root, parent, 0);
            var baseline = await Sizes(harness);
            for (var n = 1; n <= 200; n++) await fixture.SubagentCycle(harness, root, parent, n);
            Equal(baseline, await Sizes(harness), "index sizes");
            fixture.Gates.Open("parent");
            await harness.WaitForTaskAsync(parent, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "holds-a-live-owner-for-work-later-created-in-a-forgotten-conversation-loading-its-chain-again", async () =>
        {
            var fixture = new RetentionFixture();
            var storage = await OpenSqlite(SqlitePath("retention-reload"));
            var reads = 0;
            storage.OnTask = _ => Interlocked.Increment(ref reads);
            storage.OnConversation = _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.CompletedTask;
            };
            var (harness, root) = await fixture.Open(storage);
            // Live P -> ended B -> ended A -> conversation C, empty: forgotten.
            var (p, b, c) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var a = await tx.CreateTaskAsync(fixture.Hold, Named("a"), OwnedBy(b));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                return (p, b, c.Id);
            }, Context);
            fixture.Gates.Open("a");
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            var before = await Sizes(harness);
            var loaded = Volatile.Read(ref reads);
            var conversation = (await harness.ConversationAsync(c, Context))!;
            var d = await conversation.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation()), Context);
            fixture.Gates.Open("p");
            await Eventually(async () => (await RecordOf(harness, p)).State.Status == DurableTaskStatus.Completing);
            Check(Volatile.Read(ref reads) > loaded, "the chain was read again");
            fixture.Gates.Open("d");
            Equal("completed", await OutcomeOf(harness, d), "d outcome");
            Equal("completed", await OutcomeOf(harness, p), "p outcome");
            Check((await Sizes(harness)).Settled <= before.Settled, "no more settled tasks than before");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "holds-an-owner-for-work-its-own-terminal-commit-creates-below-a-forgotten-chain", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            // Live P (a spawner) -> ended B -> ended A -> conversation C, empty: forgotten. P's terminal commit creates D in C.
            var (p, b, c) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Spawner, Obj(("name", "p"), ("into", 0)), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var a = await tx.CreateTaskAsync(fixture.Hold, Named("a"), OwnedBy(b));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                return (p, b, c.Id);
            }, Context);
            // Point the spawner at C, now that its ID is known.
            await SetTasks(harness, async tx =>
            {
                var record = (await tx.GetTaskAsync(p))!;
                tx.SetTask(record with { Input = Obj(("name", "p"), ("into", c.Value)) });
            });
            fixture.Gates.Open("a");
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            fixture.Gates.Open("p");
            await Eventually(async () => (await RecordOf(harness, p)).State.Status != DurableTaskStatus.Running);
            Equal(DurableTaskStatus.Completing, (await RecordOf(harness, p)).State.Status, "p holds");
            fixture.Gates.Open("inside.p");
            Equal("completed", await OutcomeOf(harness, p), "p outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "withdraws-an-input-queued-in-a-forgotten-conversation-below-an-abort-marked-owner", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            // Live P -> ended B -> conversation C with no task; C gets a queued input while P is cancelling.
            var (p, b, c, keep) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                // A live child keeps P holding its abort until the input is withdrawn and the child ends.
                var keep = await tx.CreateTaskAsync(fixture.Hold, Named("keep"), OwnedBy(p));
                return (p, b, c.Id, keep);
            }, Context);
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            var conversation = (await harness.ConversationAsync(c, Context))!;
            // Busy, so the input queues instead of starting a run.
            await OwnershipTests.Busy(conversation, keep);
            var queued = (await conversation.SubmitAsync(SubmissionDraft.Input("later"), Context)).Id;
            await harness.AbortTaskAsync(p, Context);
            await Eventually(async () => (await OwnershipTests.RecordOf(harness, queued)).Status == SubmissionStatus.Unanswered);
            OwnershipTests.UnansweredAborted(await OwnershipTests.RecordOf(harness, queued), "queued input");
            await harness.WaitForTaskAsync(p, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-work-created-in-a-forgotten-conversation-below-an-abort-marked-owner-before-it-runs", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            var (p, b, c, keep) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                var keep = await tx.CreateTaskAsync(fixture.Hold, SchedulerGates.SlowAbort("keep"), OwnedBy(p));
                return (p, b, c.Id, keep);
            }, Context);
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            // P cancels; its live child keeps it holding. (Its abort handler waits for abort.keep, opened once `late` is done: see the file
            // header.)
            await SetTasks(harness, async tx =>
            {
                var record = (await tx.GetTaskAsync(p))!;
                tx.SetTask(record with { AbortRequested = true });
            });
            await Eventually(async () => (await RecordOf(harness, keep)).AbortRequested);
            var conversation = (await harness.ConversationAsync(c, Context))!;
            var late = await conversation.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("late"), OwnConversation()), Context);
            Equal("aborted", await OutcomeOf(harness, late), "late outcome");
            Equal(0, fixture.Gates.Runs("late"), "late never ran");
            fixture.Gates.Open("abort.keep");
            Equal("aborted", await OutcomeOf(harness, p), "p outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-scope-busy-while-the-chain-of-new-work-below-a-forgotten-conversation-loads", async () =>
        {
            var fixture = new RetentionFixture();
            var storage = await OpenSqlite(SqlitePath("retention-busy"));
            var (harness, root) = await fixture.Open(storage);
            var (b, c) = await root.CommitAsync(async tx =>
            {
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnConversation());
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                return (b, c.Id);
            }, Context);
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            // Hold the chain load's read of C: the second read once holding, after the creating commit's own.
            var held = new Deferred();
            var reached = new Deferred();
            var holding = false;
            var readsOfC = 0;
            storage.OnConversation = async id =>
            {
                if (Volatile.Read(ref holding) && id == c && Interlocked.Increment(ref readsOfC) == 2)
                {
                    reached.Resolve();
                    await held.Task;
                }
            };
            var conversation = (await harness.ConversationAsync(c, Context))!;
            Volatile.Write(ref holding, true);
            await conversation.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation()), Context);
            await reached.Task;
            Check(!await Settled(harness.WaitForIdleAsync(Context)), "busy while the chain loads");
            held.Resolve();
            fixture.Gates.Open("d");
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-chain-when-one-commit-ends-its-last-task-and-creates-new-work-below-it", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            // Live P -> ended B -> conversation C with background task `old`, which keeps the chain known without holding B.
            var (p, b, c, old) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                var old = await tx.CreateTaskAsync(fixture.Hold, Named("old"), OwnConversation(c.Id, background: true));
                return (p, b, c.Id, old);
            }, Context);
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            // One commit: the old task ends, which leaves nothing live below B, and a replacement starts in C.
            TaskId replacement = default;
            await SetTasks(harness, async tx =>
            {
                var oldRecord = (await tx.GetTaskAsync(old))!;
                replacement = await tx.CreateTaskAsync(fixture.Hold, Named("new"), OwnConversation(c));
                tx.SetTask(oldRecord with { State = NextTaskState.Completed(JsonValue.Null) });
            });
            // The chain stayed known through the publication, so P holds for the replacement.
            fixture.Gates.Open("p");
            await Eventually(async () => (await RecordOf(harness, p)).State.Status == DurableTaskStatus.Completing);
            fixture.Gates.Open("new");
            await harness.WaitForTaskAsync(replacement, Context);
            Equal("completed", await OutcomeOf(harness, p), "p outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "drops-what-a-rejected-commits-chain-load-read-without-a-later-publication", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            // Live P owns ended B, which owns conversation C: forgotten. P's terminal commit stages work in C, which loads C's chain, and a
            // child of P, which the Transaction rejects, as P is ending.
            var (p, b, c) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Rejected, Obj(("into", 0)), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                return (p, b, c.Id);
            }, Context);
            await SetTasks(harness, async tx =>
            {
                var record = (await tx.GetTaskAsync(p))!;
                tx.SetTask(record with { Input = Obj(("into", c.Value)) });
            });
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            var baseline = await Sizes(harness);
            fixture.Gates.Open("rejected");
            await Eventually(() => fixture.Rejections.Count > 0);
            // The rejected commit published nothing; the sweep job still cleans up what its chain load read.
            await Eventually(async () => (await Sizes(harness)).DropQueue == 0);
            Equal(baseline, await Sizes(harness), "index sizes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "drops-what-a-read-loaded-after-reopen-without-a-later-publication", async () =>
        {
            var fixture = new RetentionFixture();
            var path = SqlitePath("retention-reopen");
            var (harness, root) = await fixture.Open(await OpenSqlite(path));
            // A live task D in conversation C, owned by ended B: after reopen D's chain is not loaded.
            var (b, d) = await root.CommitAsync(async tx =>
            {
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnConversation());
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                var d = await tx.CreateTaskAsync(fixture.Hold, Named("d"), OwnConversation(c.Id));
                return (b, d);
            }, Context);
            fixture.Gates.Open("b");
            await Eventually(async () => (await RecordOf(harness, b)).State.Status == DurableTaskStatus.Completing);
            await harness.CloseAsync(Context);
            fixture.Gates.Clear();
            (harness, _) = await fixture.Open(await OpenSqlite(path));
            await harness.InspectAsync(Context);
            // D is still live, so its chain stays; once D ends, everything below the root goes, published or not.
            Equal(0, (await Sizes(harness)).Unloaded, "unloaded");
            fixture.Gates.Open("d");
            await harness.WaitForTaskAsync(d, Context);
            await harness.WaitForTaskAsync(b, Context);
            await Eventually(async () => (await Sizes(harness)).DropQueue == 0);
            var sizes = await Sizes(harness);
            Equal((0, 0, 0), (sizes.Settled, sizes.Below, sizes.Unloaded), "settled, below and unloaded");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "withdraws-an-input-queued-two-ended-levels-below-an-owner-whose-intent-appears-later", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            // Live P -> ended B -> ended A -> conversation C with no task, and a live child keeping P holding.
            var (p, b, c, keep) = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Hold, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnedBy(p));
                var a = await tx.CreateTaskAsync(fixture.Hold, Named("a"), OwnedBy(b));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                var keep = await tx.CreateTaskAsync(fixture.Hold, Named("keep"), OwnedBy(p));
                return (p, b, c.Id, keep);
            }, Context);
            fixture.Gates.Open("a");
            fixture.Gates.Open("b");
            await harness.WaitForTaskAsync(b, Context);
            var conversation = (await harness.ConversationAsync(c, Context))!;
            await OwnershipTests.Busy(conversation, keep);
            // Queued while nothing cancels: no withdrawal scan.
            var queued = (await conversation.SubmitAsync(SubmissionDraft.Input("later"), Context)).Id;
            await Eventually(async () => (await Sizes(harness)).DropQueue == 0);
            // P fails while its child runs: it holds the failed outcome, which is cancellation intent.
            await SetTasks(harness, async tx =>
            {
                var record = (await tx.GetTaskAsync(p))!;
                tx.SetTask(record with { State = new CompletingTaskState(new FailedTaskOutcome(new TaskOutcomeError("x"))) });
            });
            await Eventually(async () => (await OwnershipTests.RecordOf(harness, queued)).Status == SubmissionStatus.Unanswered);
            OwnershipTests.UnansweredAborted(await OwnershipTests.RecordOf(harness, queued), "queued input");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "forgets-a-20000-deep-chain-of-ended-tasks-without-recursion", async () =>
        {
            var fixture = new RetentionFixture();
            var (harness, root) = await fixture.Open();
            const int depth = 20_000;
            var baseline = await Sizes(harness);
            var (links, leaf) = await root.CommitAsync(async tx =>
            {
                var links = new List<TaskId>();
                var owner = await tx.CreateTaskAsync(fixture.Hold, Named("link0"), OwnConversation());
                links.Add(owner);
                for (var n = 1; n < depth; n++)
                {
                    owner = await tx.CreateTaskAsync(fixture.Hold, Named($"link{n}"), OwnedBy(owner));
                    links.Add(owner);
                }
                var leaf = await tx.CreateTaskAsync(fixture.Hold, Named("leaf"), OwnedBy(owner));
                return (links, leaf);
            }, Context);
            for (var n = 0; n < depth; n++) fixture.Gates.Open($"link{n}");
            fixture.Gates.Open("leaf");
            await harness.WaitForTaskAsync(leaf, Context);
            await harness.WaitForTaskAsync(links[0], Context);
            await harness.WaitForIdleAsync(Context);
            Equal(baseline, await Sizes(harness), "index sizes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-no-more-after-all-work-ended-than-a-fresh-reopen", async () =>
        {
            var fixture = new RetentionFixture();
            var path = SqlitePath("retention-fresh");
            var (harness, root) = await fixture.Open(await OpenSqlite(path));
            var parent = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("parent"), OwnConversation()), Context);
            for (var n = 0; n < 20; n++) await fixture.SubagentCycle(harness, root, parent, n);
            fixture.Gates.Open("parent");
            await harness.WaitForTaskAsync(parent, Context);
            await harness.WaitForIdleAsync(Context);
            var ended = await Sizes(harness);
            await harness.CloseAsync(Context);
            (harness, _) = await fixture.Open(await OpenSqlite(path));
            await harness.WaitForIdleAsync(Context);
            Equal(ended, await Sizes(harness), "index sizes after all work ended and after a fresh reopen");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-scan-queued-submissions-for-an-input-when-nothing-cancels", async () =>
        {
            var fixture = new RetentionFixture();
            var storage = await OpenSqlite(SqlitePath("retention-scan"));
            var scans = 0;
            storage.OnScanSubmissions = query =>
            {
                if (query.Status == SubmissionStatus.Queued) Interlocked.Increment(ref scans);
            };
            var (harness, root) = await fixture.Open(storage);
            var busy = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Hold, Named("busy"), OwnConversation()), Context);
            await OwnershipTests.Busy(root, busy);
            await ((DurableHarness)harness).Scheduler.QuiesceAsync();
            var before = Volatile.Read(ref scans);
            await root.SubmitAsync(SubmissionDraft.Input("queued"), Context);
            // Let any reconcile it would cause run.
            await Task.Delay(20);
            await ((DurableHarness)harness).Scheduler.QuiesceAsync();
            Equal(before, Volatile.Read(ref scans), "queued submission scans");
            fixture.Gates.Open("busy");
            await harness.CloseAsync(Context);
        });

        // A wait on an ended task closes no cycle (#10411), also when the ended task owns the waiter's conversation and the scheduler forgot
        // it: the wait resumes at once.
        yield return Case(prefix + "lets-a-task-wait-on-a-forgotten-ended-owner", async () =>
        {
            var fixture = new RetentionFixture();
            var opened = await OpenTasks(await OpenSqlite(SqlitePath("retention-waiter")), [fixture.Hold, fixture.Waiter]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            var (b, c) = await root.CommitAsync(async tx =>
            {
                var b = await tx.CreateTaskAsync(fixture.Hold, Named("b"), OwnConversation());
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(b));
                return (b, c.Id);
            }, Context);
            fixture.Gates.Open("b");
            await opened.Harness.WaitForTaskAsync(b, Context);
            var conversation = (await opened.Harness.ConversationAsync(c, Context))!;
            var waiter = await conversation.CommitAsync(tx => tx.CreateTaskAsync(fixture.Waiter, Obj(("on", JsonValue.Array(b.ToJson()))),
                OwnConversation()), Context);
            Equal("completed", await OutcomeOf(opened.Harness, waiter), "waiter outcome");
            await opened.Harness.CloseAsync(Context);
        });
    }

    /// <summary>harness-scheduler-retention's tasks, gates and helpers.</summary>
    private sealed class RetentionFixture
    {
        public RetentionFixture()
        {
            Hold = Gates.Hold("test.retention.hold");
            // Holds until its gate opens, then creates a held task `inside.<name>` in conversation `input.into` and ends.
            Spawner = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.retention.spawner",
                Version = 1,
                Initial = _ => O("""{"phase":"hold"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["hold"] = async (task, runtime, ctx) =>
                    {
                        var name = NameOf(task.Input);
                        if (!await SchedulerGates.Opened(Gates.Gate(name).Task, runtime.Signal)) return;
                        await runtime.CommitAsync(async (tx, current) =>
                        {
                            await tx.CreateTaskAsync(Hold, Named($"inside.{name}"), OwnConversation(Into(current)));
                            return (TaskState?)NextTaskState.Completed(JsonValue.Null);
                        }, ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
            // Holds until `rejected` opens, then tries one terminal commit that creates a task in conversation `into` and a child of its own,
            // which the Transaction rejects as the task is ending. Catches the rejection and holds.
            Rejected = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.retention.rejected",
                Version = 1,
                Initial = _ => O("""{"phase":"hold"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["hold"] = async (task, runtime, ctx) =>
                    {
                        if (!await SchedulerGates.Opened(Gates.Gate("rejected").Task, runtime.Signal)) return;
                        try
                        {
                            await runtime.CommitAsync(async (tx, current) =>
                            {
                                await tx.CreateTaskAsync(Hold, Named("staged"), OwnConversation(Into(current)));
                                await tx.CreateTaskAsync(Hold, Named("child"), OwnedBy(current.Id));
                                return (TaskState?)NextTaskState.Completed(JsonValue.Null);
                            }, ctx);
                        }
                        catch (Exception error)
                        {
                            Rejections.Add(error);
                        }
                        await SchedulerGates.Opened(new TaskCompletionSource().Task, runtime.Signal);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
            // Waits on the tasks in its input, then completes.
            Waiter = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.retention.waiter",
                Version = 1,
                Initial = _ => O("""{"phase":"wait"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Waiting(O("""{"phase":"done"}"""),
                        ((JsonArrayValue)((JsonObjectValue)task.Input)["on"]).Select(id => new TaskId((long)N(id))), JoinPolicy.AllSettled), ctx),
                    ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx),
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
        }

        /// <summary>The conversation <c>input.into</c> names, read from the record the commit rereads: the case sets it after the task
        /// started its phase, and the phase's own record is the one reserved before that (see the file header).</summary>
        private static ConversationId Into(RunningTask current) => new((long)N(((JsonObjectValue)current.Input)["into"]));

        public SchedulerGates Gates { get; } = new();
        public DurableTask Hold { get; }
        public DurableTask Spawner { get; }
        public DurableTask Rejected { get; }
        public DurableTask Waiter { get; }
        /// <summary>Rejected commits it caught (upstream <c>rejections</c>).</summary>
        public SyncList<Exception> Rejections { get; } = new();

        /// <summary>Upstream <c>open(storage)</c>: a resumed Harness on <paramref name="storage"/>, or on a fresh SQLite file.</summary>
        public async Task<(IHarness Harness, IConversation Root)> Open(IStorage? storage = null)
        {
            var opened = await OpenTasks(storage ?? await OpenSqlite(SqlitePath("retention")), [Hold, Spawner, Rejected]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            return (opened.Harness, root);
        }

        /// <summary>Under live <paramref name="parent"/>: tool task T owning conversation C, a task in C; all of them end. Returns C
        /// (upstream <c>subagentCycle</c>).</summary>
        public async Task<ConversationId> SubagentCycle(IHarness harness, IConversation root, TaskId parent, int n)
        {
            var (tool, inner, child) = await root.CommitAsync(async tx =>
            {
                var tool = await tx.CreateTaskAsync(Hold, Named($"tool{n}"), OwnedBy(parent));
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(tool));
                var inner = await tx.CreateTaskAsync(Hold, Named($"inner{n}"), OwnConversation(child.Id));
                return (tool, inner, child.Id);
            }, Context);
            Gates.Open($"inner{n}");
            Gates.Open($"tool{n}");
            await harness.WaitForTaskAsync(inner, Context);
            await harness.WaitForTaskAsync(tool, Context);
            return child;
        }
    }
}
