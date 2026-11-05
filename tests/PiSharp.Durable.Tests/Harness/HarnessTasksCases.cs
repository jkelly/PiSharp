// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-tasks.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and these:
// - Upstream runs this file on MemoryStorage (ControlledStorage where it holds commits); here every case runs on the storage the group is
//   given, wrapped in ControlledStorage where upstream uses it (HarnessBackendCases.cs adds JSONL and SQLite passes).
// - "observes a failed agent resolution": the settings getter that throws is a HarnessSettings subclass overriding Extensions.
// - "reads committed documents ...": the flush that lets the step after the phase end the invocation is the InvocationEnded barrier.
// - "resumes a waiting task ...": the flush after the third phase start is an Eventually for the waiting state it then asserts.
// - "does not signal a running abort handler ...", "keeps a terminal outcome committed by an abort handler ...": the flush that lets the
//   run start is a deferred the run handler resolves on entry, so the abort reaches a run invocation as upstream intends.
// - "orders runtime commits ...", "stops a watch ...": setTimeout(..., 0) is a Task.Delay(1) continuation.
// - "signals and joins the run ...": upstream's second abortTask() reaches the line before the abort invocation commits, by microtask
//   order; here the abort handler waits for a deferred released after that call, so the second call still sees a live task.
// - Expectations upstream asserts inside handlers are collected and asserted by the test body, so a failure names itself instead of
//   surfacing as a faulted task.
// - Pi v1.5.0 sync (area sleep): f75b3240a replaces runtime.sleep with sleepUntil, so the ended-invocation check calls SleepUntilAsync and
//   the two in-phase sleep cases are gone (HarnessSleepCases.cs covers durable sleep).
// - Pi v1.5.0 sync (area telemetry): upstream's withSignal derives through withAbortSignal(signal, context) since 2ec55d808, which keeps
//   the context's values and telemetry span; DurableContext.WithCancel() already derives the same way, so the cases are unchanged.
// - Pi v1.5.0 sync (area observers, 8b0caefc2): the ended-invocation watch listener records value.text, a watch's value being never null.
// - Pi v1.5.0 sync (area restarts, 440a5fcdb's task close cases, b3a29f111's end state): a close writes the tasks it stopped back as
//   pending, so five task-close cases expect that last commit: "stops without outcomes ...", "writes no fault when close seals ...",
//   "starts no abort handler whose reservation settles ...", "rejects a runtime commit that was queued ..." and "joins a reservation
//   that settles ...". storage.commits.at(-2) is ControlledStorage.Commits[^2].
// - Pi v1.5.0 sync (area group-commit-scheduler, e5c6182ba): "writes no fault when close seals ..." and "rejects a runtime commit that was
//   queued ..." hold the Session line with a commit whose callback waits (HoldLine, upstream holdLine), as a held storage call no longer
//   holds the line.
// - Pi v1.5.0 sync (area publication-caches, 8b0caefc2 and e5c6182ba): the document-scan count of "commits results with entries ..." wraps
//   the group's storage in DocumentScans (upstream CountingStorage over MemoryStorage) and reads the Session's IndexSizes (upstream
//   documentCacheSizes). The "publication at adoption" block runs on the group's storage: "memory" is ControlledStorage over it and
//   "work-after-adopt" LateResolvingStorage (SessionSupport.cs), the storage name ending the case id. In "signals a newly marked run ...",
//   the abort handler waits until the order is asserted, as the abort invocation's terminal commit would otherwise race the assertion on
//   another thread. "stores a host listener's own commit before the reconcile commit ..." reads the stored commits after close, as no commit
//   lands then. `publication.kind === "stored"` is !IsMemory, harness.closed is Closed, and infos are OpenedRoot.Infos. With this block
//   every v1.5.0 test of the file is here (49 cases on each backend), so the citation moves to v1.5.0.
// - "commits results with entries ...": the child task runs beside the writer's answer phase and ends first by upstream's microtask order;
//   here it is on the thread pool, so the case waits for it before it counts the task documents its end drops (a busy runner read 1).
// - PiSharp's own case, no upstream counterpart: "stores a host listener's own commit before the reconcile commit while a busy pool holds
//   the scheduler's work" is the case above with the interleaving a loaded runner gave it on SQL Server. Two holds: HeldPool, set as the
//   scheduler's Dispatch, keeps every piece of work the scheduler hands the thread pool from just before the parent's `completing` commit
//   on, and ControlledStorage.HoldCommits holds the child's terminal commit in its storage call; the pool's work then runs
//   (HeldPool.Release), and the commit lands. The case waits for the scheduler to quiesce before it sets the hold, so no pass is pending
//   then, and asks for an idle wait right after, whose reconcile pass a busy pool would keep pending with the `completing` commit's request.
//   Upstream enqueues a pass in a microtask, before anything the test does next; a pass that reaches the line only while the child's
//   terminal commit is stored waits at its gate ahead of the host listener's commit, and stores the parent's terminal record first.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static Program;
using static SessionTests;

internal static partial class HarnessTests
{
    private static readonly JsonValue RunStep = O("""{"phase":"run"}""");

    /// <summary>A held Session line (upstream <c>holdLine</c>'s result): <see cref="Entered"/> completes once the holding commit's callback
    /// runs; the jobs queued after it start once <see cref="Release"/> is called.</summary>
    internal sealed record LineHold(Task Entered, Action Release);

    /// <summary>Holds the Session line with a commit whose callback waits: the jobs queued after it start once it is released (upstream
    /// <c>holdLine</c>, Pi v1.5.0 e5c6182ba).</summary>
    internal static LineHold HoldLine(IHarness harness)
    {
        var entered = new Deferred();
        var release = new Deferred();
        _ = harness.CommitAsync(async _ =>
        {
            entered.Resolve();
            await release.Task;
        }, Context);
        return new LineHold(entered.Task, release.Resolve);
    }

    /// <summary>A busy thread pool for the scheduler (DurableTaskScheduler.Dispatch): it holds the work the scheduler hands the pool until
    /// <see cref="Release"/>, which runs it on the calling thread, in order, as a worker that frees up would; later work goes to the pool.</summary>
    private sealed class HeldPool
    {
        private readonly Lock gate = new();
        private List<Action>? held = [];

        public void Post(Action work)
        {
            lock (gate)
            {
                if (held is not null)
                {
                    held.Add(work);
                    return;
                }
            }
            ThreadPool.UnsafeQueueUserWorkItem(static work => work(), work, preferLocal: false);
        }

        public void Release()
        {
            List<Action> works;
            lock (gate)
            {
                works = held ?? [];
                held = null;
            }
            foreach (var work in works) work();
        }
    }

    /// <summary>A one-phase task; the default abort handler settles aborted with reason "test" (upstream <c>oneStep</c>).</summary>
    private static DurableTask OneStep(string name, Func<RunningTask, ITaskRuntime, DurableContext, Task> run,
        Func<ITaskRuntime, DurableContext, Task>? abort = null) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => RunStep,
        Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (task, runtime, ctx) => run(task, runtime, ctx) },
        Abort = (_, runtime, ctx) => (abort ?? ((r, c) => r.CommitAsync((_, _) => AbortedWith("test"), c)))(runtime, ctx),
    });

    /// <summary>A one-phase task that waits for <paramref name="gate"/> and completes with null (upstream <c>gated</c>).</summary>
    private static DurableTask GatedTask(string name, Task gate) => OneStep(name, async (_, runtime, ctx) =>
    {
        await gate;
        await runtime.CommitAsync((_, _) => Completed(null), ctx);
    });

    private static Task<TaskId> Start(IConversation conversation, DurableTask task, bool background = false) =>
        conversation.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null,
            new TaskOptions(TaskOwnership.Conversation) { Background = background ? true : null }), Context);

    private sealed record OpenedRoot(IHarness Harness, IRegistry Registry, SyncList<Exception> Reports, IConversation Root)
    {
        /// <summary>What each report was about, at the same index (upstream <c>infos</c>).</summary>
        public SyncList<ReportInfo> Infos { get; init; } = new();
    }

    private static async Task<OpenedRoot> OpenRoot(IStorage storage, IReadOnlyList<DurableTask> tasks, Func<double>? now = null,
        HarnessSettings? settings = null, PiSharp.Telemetry.ITelemetryContext? telemetry = null)
    {
        var opened = await OpenTasks(storage, tasks, now: now, settings: settings, telemetry: telemetry);
        var root = await opened.Harness.RootAsync(Context);
        return new OpenedRoot(opened.Harness, opened.Registry, opened.Reports, root) { Infos = opened.Infos };
    }

    /// <summary>Starts <c>AbortTaskAsync()</c> and resolves once its mark is durable, before it has joined the run (upstream
    /// <c>markDurably</c>).</summary>
    private static async Task<Task<TaskAbortResult>> MarkDurably(IHarness harness, TaskId id)
    {
        var aborting = harness.AbortTaskAsync(id, Context);
        while ((await harness.GetTaskAsync(id, Context))?.AbortRequested != true) await Flush();
        return aborting;
    }

    private static JsonValue Outcome(TaskRecord record) => ((SettlingTaskState)record.State).Outcome.ToJson();

    private static double CheckpointNumber(RunningTask task, string key) => N(((JsonObjectValue)task.Checkpoint)[key]);

    /// <summary>Settings whose extension selection throws once <see cref="Fail"/> is set.</summary>
    private sealed class FailingSettings : HarnessSettings
    {
        public volatile bool Fail;
        public override IReadOnlyList<Extension>? Extensions
        {
            get => Fail ? throw new InvalidOperationException("settings broke") : null;
            init { }
        }
    }

    private sealed class PingHooks
    {
        public Action? Ping { get; init; }
    }

    internal static IEnumerable<(string, Func<Task>)> TaskCases(string prefix, Func<IStorage> storage)
    {
        ControlledStorage Controlled() => new(storage());

        // ─── task phases ───

        yield return Case(prefix + "task-phases.continues-one-invocation-through-checkpoint-progress-and-completes-with-a-typed-result", async () =>
        {
            var seen = new SyncList<double>();
            var runtimes = new ConcurrentDictionary<ITaskRuntime, bool>();
            var counter = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.counter",
                Version = 1,
                Initial = _ => O("""{"phase":"count","n":0}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["count"] = async (task, runtime, ctx) =>
                    {
                        seen.Add(CheckpointNumber(task, "n"));
                        runtimes[runtime] = true;
                        await runtime.CommitAsync((_, current) =>
                        {
                            var n = CheckpointNumber(current, "n") + 1;
                            return n == N(((JsonObjectValue)task.Input)["to"]) ? Completed(n) : NextTaskState.Running(Obj(("phase", "count"), ("n", n)));
                        }, ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [counter]);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(counter, O("""{"to":3}"""), new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            var receipt = await harness.WaitForTaskAsync(id, Context);
            var result = ((SettlingTaskState)receipt.State).Outcome is CompletedTaskOutcome completed ? completed.Result : null;
            SameJson("3", result, "result");
            Seq([0, 1, 2], seen.Snapshot(), "seen");
            Equal(1, runtimes.Count, "runtimes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.faults-a-phase-without-durable-progress-and-a-throwing-phase", async () =>
        {
            var idle = OneStep("test.idle", (_, _, _) => Task.CompletedTask);
            var documentOnly = OneStep("test.document-only", async (_, runtime, ctx) =>
            {
                // A commit that returns no state is not progress.
                await runtime.CommitAsync((_, _) => (TaskState?)null, ctx);
            });
            var throws = OneStep("test.throws", (_, _, _) => throw new InvalidOperationException("boom"));
            var (harness, _, _, root) = await OpenRoot(storage(), [idle, documentOnly, throws]);
            TaskId[] ids = [await Start(root, idle), await Start(root, documentOnly), await Start(root, throws)];
            harness.Resume();
            var outcomes = await Task.WhenAll(ids.Select(async id => Outcome(await harness.WaitForTaskAsync(id, Context))));
            SameJson("""
                [{"status":"faulted","error":{"message":"Task test.idle phase run returned without durable progress"}},
                 {"status":"faulted","error":{"message":"Task test.document-only phase run returned without durable progress"}},
                 {"status":"faulted","error":{"message":"boom"}}]
                """, JsonValue.Array(outcomes), "outcomes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.keeps-a-committed-terminal-outcome-when-the-handler-throws-afterwards", async () =>
        {
            Exception? lateCommit = null;
            var done = OneStep("test.done", async (_, runtime, ctx) =>
            {
                await runtime.CommitAsync((_, _) => Completed("ok"), ctx);
                try { await runtime.CommitAsync((_, _) => (TaskState?)null, ctx); }
                catch (Exception error) { Volatile.Write(ref lateCommit, error); }
                throw new InvalidOperationException("after terminal");
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [done]);
            var id = await Start(root, done);
            harness.Resume();
            SameJson("""{"status":"completed","result":"ok"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            await Eventually(() => Volatile.Read(ref lateCommit) is not null);
            Rejects(lateCommit!, $"Task {id} is terminal", "late commit");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.compares-checkpoints-by-value-including-arrays", async () =>
        {
            var collect = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.collect",
                Version = 1,
                Initial = _ => O("""{"phase":"collect","items":[]}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["collect"] = async (task, runtime, ctx) =>
                    {
                        var items = (JsonArrayValue)((JsonObjectValue)task.Checkpoint)["items"];
                        // Two rounds of progress, then an equal copy of the checkpoint, which is no progress.
                        var next = items.Count < 2 ? JsonValue.Array([.. items, $"item{items.Count}"]) : JsonValue.Array([.. items]);
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(Obj(("phase", "collect"), ("items", next))), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [collect]);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(collect, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            SameJson("""{"status":"faulted","error":{"message":"Task test.collect phase collect returned without durable progress"}}""",
                Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.commits-results-with-entries-atomically-keeps-memos-until-terminal-and-retires-task-documents", async () =>
        {
            var progress = Documents.DefineTaskDoc("test.task-progress", 1, () => O("""{"lines":[]}"""), checkpointWhen: (_, _, _) => false);
            var answer = Entries.DefineEntry("answer");
            var child = OneStep("test.child", async (_, runtime, ctx) => await runtime.CommitAsync((_, _) => Completed(null), ctx));
            TaskId? childId = null;
            JsonValue? progressSeen = null;
            var failures = new SyncList<string>();
            void Expect(bool condition, string what) { if (!condition) failures.Add(what); }
            var writer = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.writer",
                Version = 1,
                Initial = _ => O("""{"phase":"write"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["write"] = async (task, runtime, ctx) =>
                    {
                        Expect(await runtime.MemoAsync("choice", ctx) is null, "no memo yet");
                        var winners = await Task.WhenAll(runtime.MemoAsync("choice", "a", ctx), runtime.MemoAsync("choice", "b", ctx));
                        Expect(winners.All(winner => JsonValue.DeepEquals(winner, "a")), "first writer wins");
                        Expect(JsonValue.DeepEquals(await runtime.MemoAsync("choice", ctx), "a"), "memo read");
                        // Memo names never resolve to inherited object properties.
                        Expect(await runtime.MemoAsync("toString", ctx) is null, "toString memo absent");
                        Expect(JsonValue.DeepEquals(await runtime.MemoAsync("toString", "own", ctx), "own"), "toString memo");
                        await runtime.CommitAsync(async (tx, _) =>
                        {
                            (await tx.DocAsync(progress, task.Id)).GetArray("lines").Add("wrote");
                            // Task creation defaults to the task's own conversation.
                            childId = await tx.CreateTaskAsync(child, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation));
                            return NextTaskState.Running(O("""{"phase":"answer"}"""));
                        }, ctx);
                    },
                    ["answer"] = async (task, runtime, ctx) =>
                    {
                        Expect(JsonValue.DeepEquals(task.Memos, O("""{"choice":"a","toString":"own"}""")), "memos");
                        await runtime.CommitAsync(async (tx, _) =>
                        {
                            progressSeen = (await tx.DocAsync(progress, task.Id)).GetArray("lines").ToValue();
                        }, ctx);
                        await runtime.CommitAsync(async (tx, current) =>
                        {
                            var entry = await tx.AppendEntryAsync(current.ConversationId, new EntryDraft { Kind = "answer", Model = JsonValue.Array(User("done")) });
                            return Completed(Obj(("entryId", entry.Id.ToJson())));
                        }, ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var counting = new DocumentScans(storage());
            var (harness, _, _, root) = await OpenRoot(counting, [writer, child]);
            var conversation = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var id = await conversation.CommitAsync(tx => tx.CreateTaskAsync(writer, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            var receipt = await harness.WaitForTaskAsync(id, Context);
            Seq([], failures.Snapshot(), "phase expectations");
            Check(!((JsonObjectValue)receipt.State.ToJson()).ContainsKey("checkpoint"), "a receipt state has no checkpoint");
            Check(!receipt.ToJson().ContainsKey("memos"), "a receipt has no memos");
            var outcome = ((SettlingTaskState)receipt.State).Outcome as CompletedTaskOutcome ?? throw new InvalidOperationException("expected completion");
            var entryId = new EntryId((long)N(((JsonObjectValue)outcome.Result!)["entryId"]));
            var entry = await harness.CommitAsync(tx => tx.GetEntryAsync(entryId), Context);
            Check(answer.Is(entry), "answer entry");
            Equal(conversation.Id, entry?.ConversationId, "entry conversation");
            SameJson("""["wrote"]""", progressSeen, "progress seen");
            Check(await harness.SnapshotAsync(progress, id, Context) is null, "task document retired");
            var childReceipt = await harness.WaitForTaskAsync(childId!.Value, Context);
            // The Session indexed the documents of the tasks it created, so no terminal commit scanned for them, and it dropped each task's
            // entry at its end.
            Equal(0, counting.Scans, "document scans");
            Equal(0, ((DurableHarness)harness).IndexSizes.TaskDocuments, "task documents");
            Equal(receipt, await harness.GetTaskAsync(id, Context), "getTask");
            Equal(conversation.Id, childReceipt.ConversationId, "child conversation");
            Check(root.Id != conversation.Id, "distinct conversations");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.resumes-a-waiting-task-once-every-task-in-on-is-terminal-whatever-the-outcome", async () =>
        {
            var gate = new Deferred();
            var order = new SyncList<string>();
            TaskId[] on = [];
            string[] outcomes = [];
            var waiter = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.waiter",
                Version = 1,
                Initial = _ => O("""{"phase":"wait"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = async (_, runtime, ctx) =>
                    {
                        order.Add("wait");
                        await runtime.CommitAsync((_, _) => NextTaskState.Waiting(O("""{"phase":"resume"}"""), on, JoinPolicy.AllSettled), ctx);
                    },
                    ["resume"] = async (_, runtime, ctx) =>
                    {
                        order.Add("resume");
                        outcomes = [.. (await runtime.OutcomesAsync(on, ctx)).Select(outcome => WireName(outcome.Status))];
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
            });
            var first = OneStep("test.first", async (_, runtime, ctx) =>
            {
                order.Add("first");
                await gate.Task;
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var faulting = OneStep("test.faulting", (_, _, _) =>
            {
                order.Add("faulting");
                throw new InvalidOperationException("fails");
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [first, faulting, waiter]);
            var firstId = await Start(root, first);
            var faultingId = await Start(root, faulting);
            on = [firstId, faultingId];
            var waiterId = await root.CommitAsync(tx => tx.CreateTaskAsync(waiter, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            await Eventually(() => order.Count == 3);
            await Eventually(async () => (await harness.GetTaskAsync(waiterId, Context))?.State.Status == DurableTaskStatus.Waiting);
            Seq(["faulting", "first", "wait"], order.Snapshot().Order(StringComparer.Ordinal), "order");
            var state = (await harness.GetTaskAsync(waiterId, Context))!.State as WaitingTaskState;
            Check(state is not null && state.On.SequenceEqual(on), "waiting on both tasks");
            gate.Resolve();
            await harness.WaitForTaskAsync(waiterId, Context);
            Equal("resume", order.Snapshot()[^1], "last phase");
            Seq(["completed", "faulted"], outcomes, "outcomes");
            await harness.CloseAsync(Context);
        });

        // #10549
        yield return Case(prefix + "task-phases.stamps-startedat-at-the-first-run-and-endedat-when-terminal-keeping-startedat-through-a-wait", async () =>
        {
            double clock = 1_000;
            var gate = new Deferred();
            TaskId[] on = [];
            var waiter = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.timed-waiter",
                Version = 1,
                Initial = _ => O("""{"phase":"wait"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Waiting(O("""{"phase":"resume"}"""), on, JoinPolicy.AllSettled), ctx),
                    ["resume"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(null), ctx),
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
            });
            var held = GatedTask("test.timed-held", gate.Task);
            var (harness, _, _, root) = await OpenRoot(storage(), [waiter, held], now: () => Volatile.Read(ref clock));
            var heldId = await Start(root, held);
            on = [heldId];
            var waiterId = await root.CommitAsync(tx => tx.CreateTaskAsync(waiter, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            async Task<string> Times() => string.Join(";", (await harness.InspectAsync(Context)).Tasks.Select(task =>
                $"{task.Record.Id}:{task.Record.StartedAt?.ToString() ?? "-"}:{task.Record.EndedAt?.ToString() ?? "-"}"));
            Equal($"{heldId}:-:-;{waiterId}:-:-", await Times(), "times before");

            Volatile.Write(ref clock, 2_000);
            harness.Resume();
            await Eventually(async () => (await harness.GetTaskAsync(waiterId, Context))?.State.Status == DurableTaskStatus.Waiting);
            Equal($"{heldId}:2000:-;{waiterId}:2000:-", await Times(), "times while waiting");

            // The waiter runs again at 5_000; its start stays the first run.
            Volatile.Write(ref clock, 5_000);
            gate.Resolve();
            var settledWaiter = await harness.WaitForTaskAsync(waiterId, Context);
            var settledHeld = await harness.GetTaskAsync(heldId, Context);
            Equal((2_000d, 5_000d), (settledHeld?.StartedAt ?? 0, settledHeld?.EndedAt ?? 0), "held times");
            Equal((2_000d, 5_000d), (settledWaiter.StartedAt ?? 0, settledWaiter.EndedAt ?? 0), "waiter times");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.keeps-one-registry-snapshot-per-phase-and-refreshes-it-at-the-phase-boundary", async () =>
        {
            var seen = new SyncList<string>();
            var phaseGate = new Deferred();
            var entered = new Deferred();
            static string ToolNames(IRegistrySnapshot snapshot) => string.Join(",", snapshot.Tools().Select(item => item.Tool.Name));
            var snapshots = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.snapshots",
                Version = 1,
                Initial = _ => O("""{"phase":"a"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["a"] = async (_, runtime, ctx) =>
                    {
                        var before = runtime.Registry;
                        entered.Resolve();
                        await phaseGate.Task;
                        seen.Add($"a:[{ToolNames(runtime.Registry)}]:{ReferenceEquals(runtime.Registry, before)}");
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"b"}""")), ctx);
                    },
                    ["b"] = async (_, runtime, ctx) =>
                    {
                        seen.Add($"b:[{ToolNames(runtime.Registry)}]:True");
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var (harness, registry, _, root) = await OpenRoot(storage(), [snapshots]);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(snapshots, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            await entered.Task;
            AddTool(registry, Define.Tool(new ToolRegistration
            {
                Name = "late",
                Description = "late",
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult()),
            }));
            phaseGate.Resolve();
            await harness.WaitForTaskAsync(id, Context);
            Seq(["a:[]:True", "b:[late]:True"], seen.Snapshot(), "seen");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.resolves-the-agent-at-first-use-in-a-phase-from-its-snapshot-keeps-it-for-the-phase-and-anew-at-the-next", async () =>
        {
            var pings = new SyncList<string>();
            var seen = new SyncList<string>();
            var beforeUse = new Deferred();
            var afterUse = new Deferred();
            var waitingBeforeUse = new Deferred();
            var waitingAfterUse = new Deferred();
            async Task<string> Ping(ITaskRuntime runtime)
            {
                pings.Clear();
                await runtime.Hooks.EachAsync<PingHooks, Action>(hooks => hooks.Ping, handler =>
                {
                    handler();
                    return Task.CompletedTask;
                });
                return string.Join(",", pings.Snapshot());
            }
            var phases = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.agent-phases",
                Version = 1,
                Initial = _ => O("""{"phase":"a"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["a"] = async (_, runtime, ctx) =>
                    {
                        waitingBeforeUse.Resolve();
                        await beforeUse.Task;
                        var first = await runtime.AgentAsync(ctx);
                        seen.Add($"a:[{await Ping(runtime)}]:{first.ThinkingLevel}");
                        waitingAfterUse.Resolve();
                        await afterUse.Task;
                        var second = await runtime.AgentAsync(ctx);
                        seen.Add($"a:[{await Ping(runtime)}]:{second.ThinkingLevel}:same={ReferenceEquals(second, first)}");
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"b"}""")), ctx);
                    },
                    ["b"] = async (_, runtime, ctx) =>
                    {
                        var agent = await runtime.AgentAsync(ctx);
                        seen.Add($"b:[{await Ping(runtime)}]:{agent.ThinkingLevel}");
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
                Hooks = new PingHooks(),
            });
            var (harness, registry, _, root) = await OpenRoot(storage(), [phases]);
            AddHooks(registry, phases, new PingHooks { Ping = () => pings.Add("before") });
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(phases, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();

            // Configured during the phase but before its first use: the lazy resolution reads it.
            await waitingBeforeUse.Task;
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = "low" }, Context);
            beforeUse.Resolve();
            // Configured and installed after the first use: not seen until the next phase.
            await waitingAfterUse.Task;
            AddHooks(registry, phases, new PingHooks { Ping = () => pings.Add("late") });
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = "high" }, Context);
            afterUse.Resolve();

            await harness.WaitForTaskAsync(id, Context);
            Seq(["a:[before]:low", "a:[before]:low:same=True", "b:[before,late]:high"], seen.Snapshot(), "seen");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.rejects-an-agent-wait-whose-caller-context-is-cancelled-without-affecting-the-phases-resolution", async () =>
        {
            Exception? cancelled = null;
            string? resolved = null;
            var agentTask = OneStep("test.agent-cancel", async (_, runtime, ctx) =>
            {
                var caller = ctx.WithCancel();
                caller.Cancel(new InvalidOperationException("caller gone"));
                try { await runtime.AgentAsync(caller.Context); }
                catch (Exception error) { cancelled = error; }
                resolved = (await runtime.AgentAsync(ctx)).ThinkingLevel;
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [agentTask]);
            await harness.WaitForTaskAsync(await Start(root, agentTask), Context);
            Equal("caller gone", cancelled?.Message, "cancelled wait");
            Equal("off", resolved, "resolved");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-phases.observes-a-failed-agent-resolution-whose-only-caller-stopped-waiting", async () =>
        {
            var settings = new FailingSettings();
            Exception? resolved = null;
            var agentTask = OneStep("test.agent-failure", async (_, runtime, ctx) =>
            {
                var caller = ctx.WithCancel();
                caller.Cancel(new InvalidOperationException("caller gone"));
                settings.Fail = true;
                try { await runtime.AgentAsync(caller.Context); } catch { }
                // Let an unobserved failure surface before the task ends.
                await Task.Delay(1);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                try { await runtime.AgentAsync(ctx); }
                catch (Exception error) { resolved = error; }
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [agentTask], settings: settings);
            await harness.WaitForTaskAsync(await Start(root, agentTask), Context);
            Equal("settings broke", resolved?.Message, "resolution failure");
            await harness.CloseAsync(Context);
        });

        // ─── task runtime ───

        yield return Case(prefix + "task-runtime.rejects-runtime-operations-after-the-invocation-ends-and-stops-its-watches", async () =>
        {
            var notes = Documents.DefineSessionDoc("test.task-notes", 1, () => O("""{"text":""}"""), checkpointWhen: (_, _, _) => false);
            ITaskRuntime? captured = null;
            Task<WatchEnd>? watchClosed = null;
            var delivered = new SyncList<string>();
            var absentDoc = Documents.DefineSessionDoc("test.task-absent", 1, () => O("""{"text":""}"""), checkpointWhen: (_, _, _) => false);
            object? absent = "unset";
            DurableContext? handlerContext = null;
            var watcher = OneStep("test.watcher", async (_, runtime, ctx) =>
            {
                captured = runtime;
                handlerContext = ctx;
                absent = await runtime.WatchDocAsync(absentDoc, ctx);
                var watch = await runtime.WatchDocAsync(notes, ctx);
                watch!.Start((call, _) =>
                {
                    delivered.Add(S(Change(call).Value["text"]));
                    return Task.CompletedTask;
                });
                watchClosed = watch.Closed;
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [watcher]);
            await harness.CommitAsync(async tx => (await tx.DocAsync(notes))["text"] = "hello", Context);
            var id = await Start(root, watcher);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            Check(absent is null, "an absent document has no watch");
            // The watch stops when the step after the phase ends the invocation; later commits deliver nothing.
            Equal(WatchEnd.Stopped, await watchClosed!, "watch end");
            await harness.CommitAsync(async tx => (await tx.DocAsync(notes))["text"] = "after", Context);
            await Flush();
            Seq([], delivered.Snapshot(), "delivered");
            var runtime = captured!;
            await RejectsAsync(() => runtime.CommitAsync((_, _) => { }, Context), "invocation has ended", "commit");
            await RejectsAsync(() => runtime.MemoAsync("x", Context), "invocation has ended", "memo read");
            await RejectsAsync(() => runtime.MemoAsync("x", 1, Context), "invocation has ended", "memo write");
            await RejectsAsync(() => runtime.SleepUntilAsync(0, RunStep, Context), "invocation has ended", "sleep");
            await RejectsAsync(() => runtime.WatchDocAsync(notes, Context), "invocation has ended", "watch");
            // The handler's own context is cancelled by now; the ended invocation still wins.
            await RejectsAsync(() => runtime.AgentAsync(handlerContext!), "invocation has ended", "agent");
            Throws(() => runtime.Now(), "invocation has ended", "now");
            Throws(() => runtime.Report(new InvalidOperationException("late")), "invocation has ended", "report");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-runtime.reads-committed-documents-and-context-through-the-runtime-and-forwards-the-clock-and-reports", async () =>
        {
            var notes = Documents.DefineRewindableConversationDoc("test.runtime-notes", 1, DocumentFork.AsOf, () => O("""{"text":""}"""),
                checkpointWhen: (_, _, _) => false);
            ITaskRuntime? captured = null;
            var seen = new SyncList<JsonValue?>();
            var reader = OneStep("test.reader", async (_, runtime, ctx) =>
            {
                captured = runtime;
                var entries = (await runtime.ContextAsync(runtime.ConversationId, ctx)).Entries;
                var (first, second) = (entries[0].Id, entries[1].Id);
                seen.Add((await runtime.SnapshotAsync(notes, runtime.ConversationId, ctx))?["text"]);
                seen.Add((await runtime.SnapshotAsOfAsync(notes, runtime.ConversationId, first, ctx))?["text"]);
                seen.Add((await runtime.ContextAsync(runtime.ConversationId, ctx, first)).Entries.Length);
                seen.Add((await runtime.ContextAsync(runtime.ConversationId, ctx, second)).Messages.Length);
                seen.Add(runtime.Now());
                runtime.Report(new InvalidOperationException("reported"));
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var (harness, _, reports, root) = await OpenRoot(storage(), [reader], now: () => 1234);
            foreach (var text in new[] { "one", "two" })
            {
                await root.CommitAsync(async tx =>
                {
                    (await tx.DocAsync(notes, root.Id))["text"] = text;
                    await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "message", Model = JsonValue.Array(User(text)) });
                }, Context);
            }
            var id = await Start(root, reader);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            SameJson("""["two","one",1,2,1234]""", JsonValue.Array(seen.Snapshot().Select(value => value ?? JsonValue.Null)), "seen");
            Seq(["reported"], reports.Snapshot().Select(error => error.Message), "reports");
            // The step after the phase ends the invocation.
            await InvocationEnded(harness, id);
            await RejectsAsync(() => captured!.SnapshotAsync(notes, root.Id, Context), "invocation has ended", "snapshot");
            await RejectsAsync(() => captured!.ContextAsync(root.Id, Context), "invocation has ended", "context");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-runtime.orders-runtime-commits-against-the-step-one-queued-before-it-lands-one-after-it-rejects", async () =>
        {
            var controlled = Controlled();
            Task? before = null;
            Task? after = null;
            Gate? held = null;
            IHarness? harnessRef = null;
            var detached = OneStep("test.detached", (_, runtime, _) =>
            {
                // Hold the line, queue a commit without awaiting it, and return; the step queues behind that commit.
                held = controlled.HoldCommits();
                _ = harnessRef!.CommitAsync(async tx => { await tx.AppendEntryAsync(runtime.ConversationId, new EntryDraft { Kind = "blocker" }); }, Context);
                before = runtime.CommitAsync((_, _) => Completed("before"), Context);
                // Queued after the handler returned, so after the step.
                _ = Task.Delay(1).ContinueWith(_ => after = runtime.CommitAsync((_, _) => Completed("after"), Context));
                return Task.CompletedTask;
            });
            var (harness, _, _, root) = await OpenRoot(controlled, [detached]);
            harnessRef = harness;
            var id = await Start(root, detached);
            harness.Resume();
            await Eventually(() => held is not null);
            await held!.Entered;
            await Eventually(() => after is not null);
            held.Release();
            await before!;
            SameJson("""{"status":"completed","result":"before"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            var error = await ThrowsAsync<Exception>(() => after!, "after");
            Check(error.Message.Contains("invocation has ended") || error.Message.Contains("is terminal"), $"after: {error.Message}");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-runtime.stops-a-watch-whose-acquisition-finishes-after-the-invocation-ended", async () =>
        {
            var notes = Documents.DefineSessionDoc("test.late-watch", 1, () => O("""{"text":""}"""), checkpointWhen: (_, _, _) => false);
            var controlled = Controlled();
            Task<object?>? watching = null;
            Gate? held = null;
            IHarness? harnessRef = null;
            var late = OneStep("test.late-watch", (_, runtime, _) =>
            {
                held = controlled.HoldCommits();
                _ = harnessRef!.CommitAsync(async tx => { await tx.AppendEntryAsync(runtime.ConversationId, new EntryDraft { Kind = "blocker" }); }, Context);
                // Starts after the handler returned, so its line job queues behind the step that ends the invocation.
                _ = Task.Delay(1).ContinueWith(_ => watching = Catch(() => runtime.WatchDocAsync(notes, Context)));
                return Task.CompletedTask;
            });
            var (harness, _, _, root) = await OpenRoot(controlled, [late]);
            harnessRef = harness;
            await harness.CommitAsync(async tx => (await tx.DocAsync(notes))["text"] = "x", Context);
            var id = await Start(root, late);
            harness.Resume();
            await Eventually(() => held is not null);
            await held!.Entered;
            await Eventually(() => watching is not null);
            held.Release();
            Check((await watching!)?.ToString()?.Contains("invocation has ended") == true, "late watch rejects");
            Equal(TaskOutcomeStatus.Faulted, ((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status, "outcome");
            await harness.CloseAsync(Context);
        });

        // ─── task scheduling ───

        yield return Case(prefix + "task-scheduling.fails-the-harness-on-a-failed-reservation-commit-reopening-runs-the-task", async () =>
        {
            var runs = 0;
            var once = OneStep("test.once", async (_, runtime, ctx) =>
            {
                Interlocked.Increment(ref runs);
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var controlled = Controlled();
            controlled.KeepOpenForReopen = true;
            var (harness, _, reports, root) = await OpenRoot(controlled, [once]);
            var id = await Start(root, once);
            // WaitForTaskAsync resumes scheduling; upstream's kick waits for a microtask, so the failure is set, and its harness.resume() runs,
            // before the reservation commits. Here the kick runs on the thread pool at once, so the failure is set first, and the explicit
            // Resume() comes before the wait: called after it, it could find the reservation already failed and throw SessionFailed itself.
            var failure = new IOException("disk gone");
            controlled.FailNextCommit(failure);
            harness.Resume();
            var waiting = harness.WaitForTaskAsync(id, Context);
            var end = await harness.Closed;
            Equal(SessionEnd.Failed(failure), end, "closed");
            Seq([failure], reports.Snapshot(), "reports");
            // The pending wait and every later call get SessionFailed with the storage error.
            Same(failure, (await ThrowsAsync<SessionFailedException>(() => waiting, "pending wait")).InnerException, "wait cause");
            Same(failure, (await ThrowsAsync<SessionFailedException>(() => harness.GetTaskAsync(id, Context), "getTask")).InnerException, "getTask cause");
            await ThrowsAsync<SessionFailedException>(() => root.SubmitAsync(SubmissionDraft.Input("x"), Context), "submit");
            Equal(0, Volatile.Read(ref runs), "runs");
            await harness.CloseAsync(Context);

            var reopened = await OpenRoot(controlled.Reopen(), [once]);
            reopened.Harness.Resume();
            Equal(TaskOutcomeStatus.Completed, ((SettlingTaskState)(await reopened.Harness.WaitForTaskAsync(id, Context)).State).Outcome.Status,
                "reopened outcome");
            Equal(1, Volatile.Read(ref runs), "runs after reopen");
            await reopened.Harness.CloseAsync(Context);
            await controlled.Inner.CloseAsync(Context);
        });

        yield return Case(prefix + "task-scheduling.fails-the-harness-instead-of-running-a-task-again-when-its-fault-write-fails", async () =>
        {
            var runs = 0;
            var controlled = Controlled();
            controlled.KeepOpenForReopen = true;
            var failure = new IOException("disk gone");
            var throws = OneStep("test.failed-fault", (_, _, _) =>
            {
                // The next commit is the step's fault write.
                if (Interlocked.Increment(ref runs) == 1) controlled.FailNextCommit(failure);
                throw new InvalidOperationException("boom");
            });
            var (harness, _, reports, root) = await OpenRoot(controlled, [throws]);
            var id = await Start(root, throws);
            harness.Resume();
            Equal(SessionEnd.Failed(failure), await harness.Closed, "closed");
            Equal(1, Volatile.Read(ref runs), "runs");
            Seq([failure], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);

            // Reopening recovers the task as after a crash: still running, so its phase runs again.
            var reopened = await OpenRoot(controlled.Reopen(), [throws]);
            reopened.Harness.Resume();
            SameJson("""{"status":"faulted","error":{"message":"boom"}}""", Outcome(await reopened.Harness.WaitForTaskAsync(id, Context)), "outcome");
            Equal(2, Volatile.Read(ref runs), "runs after reopen");
            await reopened.Harness.CloseAsync(Context);
            await controlled.Inner.CloseAsync(Context);
        });

        yield return Case(prefix + "task-scheduling.waits-for-harness-and-conversation-idleness-counting-blocked-work-and-ignoring-background-tasks", async () =>
        {
            var gates = new ConcurrentDictionary<TaskId, TaskCompletionSource>();
            var gated = OneStep("test.gated", async (task, runtime, ctx) =>
            {
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                gates[task.Id] = gate;
                await gate.Task;
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [gated]);
            await harness.WaitForIdleAsync(Context);
            var other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var foreground = await Start(root, gated);
            var background = await Start(root, gated, background: true);
            var elsewhere = await Start(other, gated);
            // Work that has not started yet is live; cancelling a wait only rejects that wait.
            var cancelled = Context.WithCancel();
            var cancelledWait = harness.WaitForIdleAsync(cancelled.Context);
            cancelled.Cancel(new InvalidOperationException("stop waiting"));
            await RejectsAsync(() => cancelledWait, "stop waiting", "cancelled wait");
            var aborted = Context.WithCancel();
            aborted.Cancel(new InvalidOperationException("already cancelled"));
            await RejectsAsync(() => root.WaitForIdleAsync(aborted.Context), "already cancelled", "already cancelled wait");
            harness.Resume();
            await Eventually(() => gates.Count == 3);
            var rootIdle = root.WaitForIdleAsync(Context);
            var harnessIdle = harness.WaitForIdleAsync(Context);
            gates[foreground].SetResult();
            await rootIdle;
            Check(!await Settled(harnessIdle), "the Harness is not idle yet");
            gates[elsewhere].SetResult();
            await harnessIdle;
            Equal(DurableTaskStatus.Running, (await harness.GetTaskAsync(background, Context))?.State.Status, "background status");
            gates[background].SetResult();
            await harness.WaitForTaskAsync(background, Context);
            await harness.CloseAsync(Context);
            await RejectsAsync(() => harness.WaitForIdleAsync(Context), "closed", "harness wait after close");
            await RejectsAsync(() => root.WaitForIdleAsync(Context), "closed", "conversation wait after close");
        });

        // #10546
        yield return Case(prefix + "task-scheduling.pages-the-newest-tasks-first-without-scanning-older-ones", async () =>
        {
            var idle = OneStep("test.idle", (_, _, _) => Task.CompletedTask);
            var (harness, _, _, root) = await OpenRoot(storage(), [idle]);
            var ids = new List<TaskId>();
            for (var index = 0; index < 5; index++) ids.Add(await Start(root, idle));
            var first = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Order = ScanOrder.Descending }, 2), Context);
            Seq([ids[4], ids[3]], first.Items.Select(item => item.Id), "first page");
            var second = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery(), 2, first.Next), Context);
            Seq([ids[2], ids[1]], second.Items.Select(item => item.Id), "second page");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-scheduling.rejects-unknown-tasks-and-reports-terminal-tasks", async () =>
        {
            var done = OneStep("test.quick", async (_, runtime, ctx) => await runtime.CommitAsync((_, _) => Completed(null), ctx));
            var (harness, _, _, root) = await OpenRoot(storage(), [done]);
            var unknown = Ids.FromNumber<TaskId>(999_999);
            Check(await harness.GetTaskAsync(unknown, Context) is null, "unknown task");
            await RejectsAsync(() => harness.WaitForTaskAsync(unknown, Context), "does not exist", "wait");
            await RejectsAsync(() => harness.AbortTaskAsync(unknown, Context), "does not exist", "abort");
            var id = await Start(root, done);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            Equal(DurableTaskStatus.Terminal, (await harness.WaitForTaskAsync(id, Context)).State.Status, "terminal wait");
            Equal(TaskAbortResult.Terminal, await harness.AbortTaskAsync(id, Context), "abort terminal");
            await harness.CloseAsync(Context);
            Throws(() => harness.Resume(), "closed", "resume after close");
        });

        yield return Case(prefix + "task-scheduling.rejects-task-waits-cancelled-or-closed-while-queued-on-the-line-and-pending-waits-on-close", async () =>
        {
            var gate = new Deferred();
            var blocking = GatedTask("test.wait-close", gate.Task);
            var controlled = Controlled();
            var (harness, _, _, root) = await OpenRoot(controlled, [blocking]);
            var id = await Start(root, blocking);

            // Hold the line with a commit, then queue waits behind it.
            var held = controlled.HoldCommits();
            var blocker = harness.CommitAsync(async tx => { await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "blocker" }); }, Context);
            await held.Entered;
            var controller = Context.WithCancel();
            var cancelledWhileQueued = harness.WaitForTaskAsync(id, controller.Context);
            controller.Cancel(new InvalidOperationException("wait cancelled"));
            held.Release();
            await blocker;
            await RejectsAsync(() => cancelledWhileQueued, "wait cancelled", "cancelled while queued");

            var pending = harness.WaitForTaskAsync(id, Context);
            var idle = harness.WaitForIdleAsync(Context);
            var conversationIdle = root.WaitForIdleAsync(Context);
            await Flush();
            var heldAgain = controlled.HoldCommits();
            var blockerAgain = harness.CommitAsync(async tx => { await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "blocker" }); }, Context);
            await heldAgain.Entered;
            var closedWhileQueued = harness.WaitForTaskAsync(id, Context);
            var closing = harness.CloseAsync(Context);
            heldAgain.Release();
            await blockerAgain;
            await RejectsAsync(() => closedWhileQueued, "closed", "closed while queued");
            await RejectsAsync(() => pending, "closed", "pending");
            await RejectsAsync(() => idle, "closed", "idle");
            await RejectsAsync(() => conversationIdle, "closed", "conversation idle");
            gate.Resolve();
            await closing;
        });

        // ─── task abort ───

        yield return Case(prefix + "task-abort.rejects-the-runs-commits-and-memo-writes-after-the-mark-and-settles-through-a-fresh-abort-invocation", async () =>
        {
            var reached = new Deferred();
            var errors = new SyncList<string>();
            JsonValue? memoRead = null;
            var abortRuntimes = new SyncList<ITaskRuntime>();
            ITaskRuntime? runRuntime = null;
            var failures = new SyncList<string>();
            var marked = OneStep("test.marked",
                async (_, runtime, ctx) =>
                {
                    runRuntime = runtime;
                    await runtime.MemoAsync("kept", 1, ctx);
                    reached.Resolve();
                    // Keep working after the signal: every later write of this run must reject.
                    try { await Aborted(runtime.Signal); } catch { }
                    memoRead = await runtime.MemoAsync("kept", Context);
                    try { await runtime.MemoAsync("late", 2, Context); } catch (Exception error) { errors.Add(error.Message); }
                    try { await runtime.CommitAsync((_, _) => Completed(null), Context); } catch (Exception error) { errors.Add(error.Message); }
                },
                async (runtime, ctx) =>
                {
                    abortRuntimes.Add(runtime);
                    await runtime.CommitAsync((_, current) =>
                    {
                        if (!current.AbortRequested) failures.Add("abortRequested");
                        if (!JsonValue.DeepEquals(current.Memos, O("""{"kept":1}"""))) failures.Add("memos");
                        return AbortedWith("mark");
                    }, ctx);
                });
            var (harness, _, _, root) = await OpenRoot(storage(), [marked]);
            var id = await Start(root, marked);
            harness.Resume();
            await reached.Task;
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort");
            SameJson("""{"status":"aborted","reason":"mark"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            SameJson("1", memoRead, "memo read");
            Seq([$"Task {id} has a durable abort mark", $"Task {id} has a durable abort mark"], errors.Snapshot(), "errors");
            Seq([], failures.Snapshot(), "abort handler expectations");
            Equal(1, abortRuntimes.Count, "abort runtimes");
            Check(!ReferenceEquals(abortRuntimes[0], runRuntime), "a fresh runtime");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-abort.starts-no-further-phase-after-a-mark-that-lands-during-a-phase-with-progress", async () =>
        {
            var reached = new Deferred();
            var proceed = new Deferred();
            var phases = new SyncList<string>();
            var two = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.mark-boundary",
                Version = 1,
                Initial = _ => O("""{"phase":"one"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["one"] = async (_, runtime, ctx) =>
                    {
                        phases.Add("one");
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"two"}""")), ctx);
                        reached.Resolve();
                        // Ignores the signal and returns normally after the mark.
                        await proceed.Task;
                    },
                    ["two"] = (_, _, _) =>
                    {
                        phases.Add("two");
                        return Task.CompletedTask;
                    },
                },
                Abort = async (_, runtime, ctx) =>
                {
                    phases.Add("abort");
                    await runtime.CommitAsync((_, _) => AbortedWith("boundary"), ctx);
                },
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [two]);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(two, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            await reached.Task;
            var aborting = await MarkDurably(harness, id);
            proceed.Resolve();
            Equal(TaskAbortResult.Marked, await aborting, "abort");
            SameJson("""{"status":"aborted","reason":"boundary"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            Seq(["one", "abort"], phases.Snapshot(), "phases");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-abort.signals-and-joins-the-run-before-returning-then-runs-the-abort-handler", async () =>
        {
            var reached = new Deferred();
            var runEnded = false;
            // Holds the default abort handler until the second abort, which upstream reaches before the abort invocation by microtask order.
            var abortGate = new Deferred();
            var signalledTask = OneStep("test.signalled", async (_, runtime, _) =>
            {
                reached.Resolve();
                try { await Aborted(runtime.Signal); }
                finally { Volatile.Write(ref runEnded, true); }
            }, async (runtime, ctx) =>
            {
                await abortGate.Task;
                await runtime.CommitAsync((_, _) => AbortedWith("test"), ctx);
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [signalledTask]);
            var id = await Start(root, signalledTask);
            harness.Resume();
            await reached.Task;
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort");
            Check(Volatile.Read(ref runEnded), "the run ended before abort returned");
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort again");
            abortGate.Resolve();
            SameJson("""{"status":"aborted","reason":"test"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-abort.aborts-waiting-work-before-its-wait-ends-and-faults-abort-handlers-that-throw-or-settle-nothing", async () =>
        {
            var gate = new Deferred();
            var first = GatedTask("test.dependency", gate.Task);
            TaskId firstId = default;
            Task Wait(RunningTask _, ITaskRuntime runtime, DurableContext ctx) =>
                runtime.CommitAsync((_, _) => NextTaskState.Waiting(RunStep, [firstId], JoinPolicy.AllSettled), ctx);
            var lazy = OneStep("test.lazy-abort", Wait, (_, _) => Task.CompletedTask);
            var throwing = OneStep("test.throwing-abort", Wait, (_, _) => throw new InvalidOperationException("abort failed"));
            var (harness, _, _, root) = await OpenRoot(storage(), [first, lazy, throwing]);
            firstId = await Start(root, first);
            var lazyId = await Start(root, lazy);
            var throwingId = await Start(root, throwing);
            harness.Resume();
            await Eventually(async () => (await harness.GetTaskAsync(throwingId, Context))?.State.Status == DurableTaskStatus.Waiting);
            await Eventually(async () => (await harness.GetTaskAsync(lazyId, Context))?.State.Status == DurableTaskStatus.Waiting);
            await harness.AbortTaskAsync(lazyId, Context);
            await harness.AbortTaskAsync(throwingId, Context);
            SameJson($$$"""{"status":"faulted","error":{"message":"Abort handler of task {{{lazyId}}} returned without a terminal outcome"}}""",
                Outcome(await harness.WaitForTaskAsync(lazyId, Context)), "lazy outcome");
            SameJson("""{"status":"faulted","error":{"message":"abort failed"}}""", Outcome(await harness.WaitForTaskAsync(throwingId, Context)), "throwing outcome");
            Equal(DurableTaskStatus.Running, (await harness.GetTaskAsync(firstId, Context))?.State.Status, "dependency status");
            gate.Resolve();
            await harness.WaitForTaskAsync(firstId, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-abort.does-not-signal-a-running-abort-handler-when-aborted-again-and-a-cancelled-caller-leaves-the-mark-durable", async () =>
        {
            var started = new Deferred();
            var reached = new Deferred();
            var proceed = new Deferred();
            var runRelease = new Deferred();
            bool? abortSignalled = null;
            var aborts = 0;
            var run = OneStep("test.abort-again",
                async (_, _, _) =>
                {
                    started.Resolve();
                    // Ignores the signal, so the first caller is still joining when it gives up.
                    await runRelease.Task;
                },
                async (runtime, ctx) =>
                {
                    Interlocked.Increment(ref aborts);
                    reached.Resolve();
                    await proceed.Task;
                    abortSignalled = runtime.Signal.IsCancellationRequested;
                    await runtime.CommitAsync((_, _) => AbortedWith("once"), ctx);
                });
            var (harness, _, _, root) = await OpenRoot(storage(), [run]);
            var id = await Start(root, run);
            harness.Resume();
            await started.Task;
            // This caller gives up while joining; the mark and the abort invocation are unaffected.
            var controller = Context.WithCancel();
            var cancelled = harness.AbortTaskAsync(id, controller.Context);
            while ((await harness.GetTaskAsync(id, Context))?.AbortRequested != true) await Flush();
            controller.Cancel(new InvalidOperationException("caller gave up"));
            await RejectsAsync(() => cancelled, "caller gave up", "cancelled caller");
            runRelease.Resolve();
            await reached.Task;
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort again");
            proceed.Resolve();
            SameJson("""{"status":"aborted","reason":"once"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            Equal(false, abortSignalled, "abort handler signal");
            Equal(1, Volatile.Read(ref aborts), "aborts");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-abort.keeps-a-terminal-outcome-committed-by-an-abort-handler-that-throws-afterwards", async () =>
        {
            var started = new Deferred();
            var run = OneStep("test.abort-then-throw",
                async (_, runtime, _) =>
                {
                    started.Resolve();
                    await Aborted(runtime.Signal);
                },
                async (runtime, ctx) =>
                {
                    await runtime.CommitAsync((_, _) => AbortedWith("done"), ctx);
                    throw new InvalidOperationException("after terminal");
                });
            var (harness, _, _, root) = await OpenRoot(storage(), [run]);
            var id = await Start(root, run);
            harness.Resume();
            await started.Task;
            await harness.AbortTaskAsync(id, Context);
            SameJson("""{"status":"aborted","reason":"done"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-abort.never-starts-phase-one-when-the-abort-lands-while-the-reservation-settles", async () =>
        {
            var ran = false;
            var reserved = OneStep("test.reserved", async (_, runtime, _) =>
            {
                Volatile.Write(ref ran, true);
                await Aborted(runtime.Signal);
            });
            var controlled = Controlled();
            var (harness, _, _, root) = await OpenRoot(controlled, [reserved]);
            var id = await Start(root, reserved);
            var held = controlled.HoldCommits();
            harness.Resume();
            await held.Entered;
            // The reservation commit is in storage; the abort mark commit queues behind it.
            var aborting = harness.AbortTaskAsync(id, Context);
            held.Release();
            Equal(TaskAbortResult.Marked, await aborting, "abort");
            SameJson("""{"status":"aborted","reason":"test"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            Check(!Volatile.Read(ref ran), "phase one never started");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-abort.lets-an-abort-mark-win-over-a-fault-that-races-it", async () =>
        {
            var controlled = Controlled();
            Task<TaskAbortResult>? marking = null;
            Gate? held = null;
            IHarness? harnessRef = null;
            var racing = OneStep("test.racing-fault", async (task, _, _) =>
            {
                var gate = controlled.HoldCommits();
                held = gate;
                marking = harnessRef!.AbortTaskAsync(task.Id, Context);
                await gate.Entered;
                // The mark is in storage but not committed when the handler throws, so the fault commit queues behind it.
                throw new InvalidOperationException("would fault");
            });
            var (harness, _, _, root) = await OpenRoot(controlled, [racing]);
            harnessRef = harness;
            var id = await Start(root, racing);
            harness.Resume();
            await Eventually(() => held is not null);
            await held!.Entered;
            await Flush();
            held.Release();
            SameJson("""{"status":"aborted","reason":"test"}""", Outcome(await harness.WaitForTaskAsync(id, Context)), "outcome");
            Equal(TaskAbortResult.Marked, await marking!, "marking");
            await harness.CloseAsync(Context);
        });

        // ─── task close ───

        yield return Case(prefix + "task-close.stops-without-outcomes-and-starts-no-fresh-phase-or-abort-invocation-while-closing", async () =>
        {
            var reached = new Deferred();
            var proceed = new Deferred();
            var phases = new SyncList<string>();
            var abortRan = false;
            var two = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.two",
                Version = 1,
                Initial = _ => O("""{"phase":"one"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["one"] = async (_, runtime, ctx) =>
                    {
                        phases.Add("one");
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"two"}""")), ctx);
                        reached.Resolve();
                        // Ignores signals and returns normally once released; the closing rule wins over the next phase.
                        await proceed.Task;
                    },
                    ["two"] = (_, _, _) =>
                    {
                        phases.Add("two");
                        return Task.CompletedTask;
                    },
                },
                Abort = (_, _, _) =>
                {
                    Volatile.Write(ref abortRan, true);
                    return Task.CompletedTask;
                },
            });
            var controlled = Controlled();
            var (harness, _, _, root) = await OpenRoot(controlled, [two]);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(two, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            await reached.Task;
            var aborting = await MarkDurably(harness, id);
            var record = await harness.GetTaskAsync(id, Context);
            var commits = controlled.CommitCount;
            var closing = harness.CloseAsync(Context);
            proceed.Resolve();
            await closing;
            Equal(TaskAbortResult.Marked, await aborting, "abort");
            // No outcome: close writes only the stopped task back as pending, so a reopen counts no restart.
            Equal(commits + 1, controlled.CommitCount, "only the write-back while closing");
            var written = controlled.LastCommit;
            Check(written.Length == 1 && written[0] is TaskWrite { Value: { AbortRequested: true, State: PendingTaskState pending } back } &&
                back.Id == id && JsonValue.DeepEquals(pending.Checkpoint, O("""{"phase":"two"}""")),
                $"close writes the marked task back as pending at phase two: {string.Join(", ", written)}");
            Seq(["one"], phases.Snapshot(), "phases");
            Check(!Volatile.Read(ref abortRan), "no abort handler");
            Check(record is { AbortRequested: true, State: RunningTaskState running } && JsonValue.DeepEquals(running.Checkpoint, O("""{"phase":"two"}""")),
                "marked record still running at phase two");
            await RejectsAsync(() => harness.CommitAsync(_ => { }, Context), "closed", "commit after close");
        });

        yield return Case(prefix + "task-close.seals-admission-before-signalling-handlers-and-stops-watches-before-joining-them", async () =>
        {
            var notes = Documents.DefineSessionDoc("test.close-notes", 1, () => O("""{"text":""}"""), checkpointWhen: (_, _, _) => false);
            var reached = new Deferred();
            Task? fromListener = null;
            IHarness? harnessRef = null;
            WatchEnd? watchEnd = null;
            var stubborn = OneStep("test.stubborn", async (_, runtime, _) =>
            {
                // Uses a context close does not cancel, then waits for the watch to close.
                var watch = await runtime.WatchDocAsync(notes, Context);
                runtime.Signal.Register(() => fromListener = harnessRef!.CommitAsync(_ => { }, Context));
                reached.Resolve();
                watchEnd = await watch!.Closed;
            });
            var (harness, _, _, root) = await OpenRoot(storage(), [stubborn]);
            harnessRef = harness;
            await harness.CommitAsync(async tx => (await tx.DocAsync(notes))["text"] = "x", Context);
            await Start(root, stubborn);
            harness.Resume();
            await reached.Task;
            await harness.CloseAsync(Context);
            await RejectsAsync(() => fromListener!, "closed", "commit from the signal listener");
            Equal(WatchEnd.Ended, watchEnd, "watch end");
        });

        yield return Case(prefix + "task-close.writes-no-fault-when-close-seals-while-the-step-after-a-failed-phase-is-queued", async () =>
        {
            var controlled = Controlled();
            LineHold? held = null;
            IHarness? harnessRef = null;
            var throws = OneStep("test.close-fault", (_, _, _) =>
            {
                // The step after this phase queues behind a job that holds the line.
                held = HoldLine(harnessRef!);
                throw new InvalidOperationException("would fault");
            });
            var (harness, _, _, root) = await OpenRoot(controlled, [throws]);
            harnessRef = harness;
            var id = await Start(root, throws);
            harness.Resume();
            await Eventually(() => held is not null);
            await held!.Entered;
            await Flush();
            var closing = harness.CloseAsync(Context);
            held.Release();
            await closing;
            // Created, reserved, and written back as pending by close: no fault.
            Seq(["pending", "running", "pending"], TaskWrites(controlled, id).Select(record => WireName(record.State.Status)), "task writes");
        });

        yield return Case(prefix + "task-close.starts-no-abort-handler-whose-reservation-settles-while-closing", async () =>
        {
            var ran = false;
            var marked = OneStep("test.close-abort-reservation", (_, _, _) => Task.CompletedTask, (_, _) =>
            {
                Volatile.Write(ref ran, true);
                return Task.CompletedTask;
            });
            var controlled = Controlled();
            var (harness, _, _, root) = await OpenRoot(controlled, [marked]);
            var id = await Start(root, marked);
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort");
            var held = controlled.HoldCommits();
            harness.Resume();
            await held.Entered;
            var closing = harness.CloseAsync(Context);
            held.Release();
            await closing;
            Check(!Volatile.Read(ref ran), "no abort handler");
            // The reservation landed; close wrote the task back as pending.
            var writes = controlled.Commits[^2].OfType<TaskWrite>().Select(write => write.Value).ToList();
            Check(writes.Count == 1 && writes[0].Id == id && writes[0].AbortRequested && writes[0].State.Status == DurableTaskStatus.Running,
                $"the commit before the last reserves the marked task: {string.Join(", ", writes)}");
            var written = controlled.LastCommit;
            Check(written.Length == 1 && written[0] is TaskWrite { Value: { AbortRequested: true, State.Status: DurableTaskStatus.Pending } back } && back.Id == id,
                $"close writes the marked task back as pending: {string.Join(", ", written)}");
        });

        yield return Case(prefix + "task-close.rejects-a-runtime-commit-that-was-queued-on-the-line-when-close-sealed-it", async () =>
        {
            var controlled = Controlled();
            Task<object?>? queued = null;
            LineHold? held = null;
            IHarness? harnessRef = null;
            var proceed = new Deferred();
            var queuedTask = OneStep("test.close-queued-commit", async (_, runtime, _) =>
            {
                held = HoldLine(harnessRef!);
                queued = Catch<object?>(async () => { await runtime.CommitAsync((_, _) => Completed(null), Context); return null; });
                // Ignores the close signal, so the invocation is still alive when the queued commit reaches the line.
                await proceed.Task;
            });
            var (harness, _, _, root) = await OpenRoot(controlled, [queuedTask]);
            harnessRef = harness;
            var id = await Start(root, queuedTask);
            harness.Resume();
            await Eventually(() => held is not null);
            await held!.Entered;
            await Flush();
            var closing = harness.CloseAsync(Context);
            held.Release();
            Check((await queued!)?.ToString()?.Contains("Harness is closed") == true, "queued commit rejects");
            proceed.Resolve();
            await closing;
            // Created, reserved, and written back as pending by close; the queued outcome never landed.
            Seq(["pending", "running", "pending"], TaskWrites(controlled, id).Select(record => WireName(record.State.Status)), "task writes");
        });

        yield return Case(prefix + "task-close.starts-no-next-phase-when-close-seals-during-a-step-that-decided-to-continue", async () =>
        {
            var phases = new SyncList<string>();
            var registry = Registry.Create();
            IHarness? harnessRef = null;
            Task? closing = null;
            var closeOnSnapshot = false;
            // The step refreshes the snapshot after progress, inside its line callback and after its closing check.
            var reader = new DelegateReader(() =>
            {
                if (Volatile.Read(ref closeOnSnapshot))
                {
                    Volatile.Write(ref closeOnSnapshot, false);
                    closing = harnessRef!.CloseAsync(Context);
                }
                return registry.Snapshot();
            }, registry.Subscribe);
            var two = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.close-in-step",
                Version = 1,
                Initial = _ => O("""{"phase":"one"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["one"] = async (_, runtime, ctx) =>
                    {
                        phases.Add("one");
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"two"}""")), ctx);
                        Volatile.Write(ref closeOnSnapshot, true);
                    },
                    ["two"] = (_, _, _) =>
                    {
                        phases.Add("two");
                        return Task.CompletedTask;
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            AddTask(registry, two);
            var harness = await Harness.OpenAsync(storage(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = reader }, Context);
            harnessRef = harness;
            var root = await harness.RootAsync(Context);
            await root.CommitAsync(tx => tx.CreateTaskAsync(two, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            await Eventually(() => closing is not null);
            await closing!;
            Seq(["one"], phases.Snapshot(), "phases");
        });

        yield return Case(prefix + "task-close.joins-a-reservation-that-settles-while-closing-without-starting-its-handler", async () =>
        {
            var ran = false;
            var never = OneStep("test.close-reservation", (_, _, _) =>
            {
                Volatile.Write(ref ran, true);
                return Task.CompletedTask;
            });
            var controlled = Controlled();
            var (harness, _, _, root) = await OpenRoot(controlled, [never]);
            var id = await Start(root, never);
            var held = controlled.HoldCommits();
            harness.Resume();
            await held.Entered;
            var closing = harness.CloseAsync(Context);
            held.Release();
            await closing;
            Check(!Volatile.Read(ref ran), "no handler");
            // The reservation landed; close wrote the task back as pending.
            var writes = controlled.Commits[^2].OfType<TaskWrite>().Select(write => write.Value).ToList();
            Check(writes.Count == 1 && writes[0].Id == id && writes[0].State.Status == DurableTaskStatus.Running,
                $"the commit before the last reserves the task: {string.Join(", ", writes)}");
            var written = controlled.LastCommit;
            Check(written.Length == 1 && written[0] is TaskWrite { Value: { State.Status: DurableTaskStatus.Pending } back } && back.Id == id,
                $"close writes the task back as pending: {string.Join(", ", written)}");
        });

        // ─── publication at adoption ───

        // Both storages: one that resolves right after adopting, one that still works after it (JSONL's sidecar reclaim).
        var storages = new (string Name, Func<ControlledStorage> Open)[]
        {
            ("memory", Controlled),
            ("work-after-adopt", () => new LateResolvingStorage(storage())),
        };

        yield return Case(prefix + "publication-at-adoption.resumes-task-and-idle-waits-only-once-observecommits-observers-saw-the-terminal-commit", async () =>
        {
            var gate = new Deferred();
            var gated = GatedTask("test.wake-gated", gate.Task);
            var (harness, _, _, root) = await OpenRoot(new LateResolvingStorage(storage()), [gated]);
            var seen = new ConcurrentDictionary<TaskId, bool>();
            harness.ObserveCommits(new CommitObserver((publication, _) =>
            {
                foreach (var change in publication.Changes)
                    if (change is TaskWrite { Value: { State.Status: DurableTaskStatus.Terminal } record }) seen[record.Id] = true;
            }, _ => { }));
            var id = await Start(root, gated);
            harness.Resume();
            async Task<bool> Seen(Task wait)
            {
                await wait;
                return seen.ContainsKey(id);
            }
            var task = Seen(harness.WaitForTaskAsync(id, Context));
            var idle = Seen(harness.WaitForIdleAsync(Context));
            var conversationIdle = Seen(root.WaitForIdleAsync(Context));
            gate.Resolve();
            Seq([true, true, true], await Task.WhenAll(task, idle, conversationIdle), "seen when each wait resumed");
            await harness.CloseAsync(Context);
        });

        foreach (var (name, open) in storages)
        {
            yield return Case(prefix + $"publication-at-adoption.runs-host-listeners-before-the-waits-a-terminal-commit-resolves-{name}", async () =>
            {
                var controlled = open();
                var gate = new Deferred();
                var gated = GatedTask("test.order-gated", gate.Task);
                var (harness, _, _, root) = await OpenRoot(controlled, [gated]);
                var order = new SyncList<string>();
                var id = await Start(root, gated);
                harness.ObserveCommits(new CommitObserver((publication, _) =>
                {
                    if (Terminal(publication, id)) order.Add($"host adopting={Lower(controlled.Adopting)}");
                }, _ => { }));
                harness.Resume();
                var waited = Then(harness.WaitForTaskAsync(id, Context), () => order.Add("wait"));
                gate.Resolve();
                await waited;
                Seq(["host adopting=false", "wait"], order.Snapshot(), "order");
                await harness.CloseAsync(Context);
            });

            yield return Case(prefix + $"publication-at-adoption.signals-a-newly-marked-run-after-host-listeners-saw-the-mark-outside-the-storages-adopt-{name}", async () =>
            {
                var controlled = open();
                var reached = new Deferred();
                var release = new Deferred();
                var order = new SyncList<string>();
                // The abort handler waits until the order is asserted, so its terminal commit, which keeps the mark, adds no "host" before it.
                var signalled = OneStep("test.order-signalled", async (_, runtime, _) =>
                {
                    runtime.Signal.Register(() => order.Add($"abort adopting={Lower(controlled.Adopting)}"));
                    reached.Resolve();
                    await Aborted(runtime.Signal);
                }, async (runtime, ctx) =>
                {
                    await release.Task;
                    await runtime.CommitAsync((_, _) => AbortedWith("test"), ctx);
                });
                var (harness, _, _, root) = await OpenRoot(controlled, [signalled]);
                var id = await Start(root, signalled);
                harness.ObserveCommits(new CommitObserver((publication, _) =>
                {
                    foreach (var change in publication.Changes)
                        if (change is TaskWrite { Value: { AbortRequested: true } record } && record.Id == id) order.Add("host");
                }, _ => { }));
                harness.Resume();
                await reached.Task;
                Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort");
                Seq(["host", "abort adopting=false"], order.Snapshot(), "order");
                release.Resolve();
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "publication-at-adoption.resolves-a-waitforidle-called-while-the-storage-stores-the-commit-at-its-publication-after-host-listeners", async () =>
        {
            var late = new LateResolvingStorage(storage());
            var gate = new Deferred();
            var gated = GatedTask("test.gap-idle", gate.Task);
            var (harness, _, _, root) = await OpenRoot(late, [gated]);
            var order = new SyncList<string>();
            var id = await Start(root, gated);
            harness.ObserveCommits(new CommitObserver((publication, _) =>
            {
                if (Terminal(publication, id)) order.Add("host");
            }, _ => { }));
            var gapped = new Deferred();
            Task? idle = null;
            late.Gap = writes =>
            {
                if (!EndsTask(writes)) return false;
                idle = Then(harness.WaitForIdleAsync(Context), () => order.Add($"idle committing={Lower(late.Committing)}"));
                gapped.Resolve();
                return true;
            };
            harness.Resume();
            gate.Resolve();
            await gapped.Task;
            await idle!;
            Seq(["host", "idle committing=false"], order.Snapshot(), "order");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "publication-at-adoption.publishes-a-commit-the-storage-rejects-after-adopting-it-resolves-its-waits-then-fails-the-session", async () =>
        {
            var late = new LateResolvingStorage(storage());
            var telemetry = new PiSharp.Telemetry.InMemoryTelemetryContext();
            var gate = new Deferred();
            var gated = GatedTask("test.reject-after-adopt", gate.Task);
            var opened = await OpenRoot(late, [gated], telemetry: telemetry);
            var (harness, _, _, root) = opened;
            var id = await Start(root, gated);
            var seen = new SyncList<string>();
            harness.ObserveCommits(new CommitObserver((publication, _) =>
            {
                if (Terminal(publication, id)) seen.Add("terminal");
            }, _ => { }));
            late.RejectAfterAdopt = EndsTask;
            harness.Resume();
            var waited = harness.WaitForTaskAsync(id, Context);
            gate.Resolve();
            SameJson("""{"status":"completed","result":null}""", Outcome(await waited), "outcome");
            Seq(["terminal"], seen.Snapshot(), "seen");
            var end = await harness.Closed;
            Equal(SessionEndKind.Failed, end.Kind, "end");
            Equal("reclaim failed", end.Error?.Message, "end error");
            Check(opened.Infos.Snapshot().Contains(ReportInfo.StorageFailed), "storage_failed reported");
            Seq([1.0, -1.0], ActiveTasks(telemetry), "pi.durable.tasks.active");
        });

        yield return Case(prefix + "publication-at-adoption.counts-a-task-created-by-a-commit-the-storage-rejects-after-adopting-it-and-its-close-balances-it", async () =>
        {
            var late = new LateResolvingStorage(storage());
            var telemetry = new PiSharp.Telemetry.InMemoryTelemetryContext();
            var idleTask = OneStep("test.reject-created", (_, _, _) => Task.CompletedTask);
            var (harness, _, _, root) = await OpenRoot(late, [idleTask], telemetry: telemetry);
            late.RejectAfterAdopt = writes => writes.Any(write => write is TaskWrite);
            await RejectsAsync(() => Start(root, idleTask), "reclaim failed", "start");
            Equal(SessionEndKind.Failed, (await harness.Closed).Kind, "end");
            Seq([1.0, -1.0], ActiveTasks(telemetry), "pi.durable.tasks.active");
        });

        yield return Case(prefix + "publication-at-adoption.keeps-a-task-wait-resolved-when-a-host-listener-closes-the-harness-on-the-terminal-commit", async () =>
        {
            var gate = new Deferred();
            var gated = GatedTask("test.close-in-listener", gate.Task);
            var (harness, _, _, root) = await OpenRoot(storage(), [gated]);
            var id = await Start(root, gated);
            Task? closing = null;
            harness.ObserveCommits(new CommitObserver((publication, _) =>
            {
                if (Terminal(publication, id)) closing = harness.CloseAsync(Context);
            }, _ => { }));
            harness.Resume();
            var waited = harness.WaitForTaskAsync(id, Context);
            gate.Resolve();
            SameJson("""{"status":"completed","result":null}""", Outcome(await waited), "outcome");
            await closing!;
        });

        yield return Case(prefix + "publication-at-adoption.ends-a-task-wait-when-the-harness-closes-while-the-terminal-commit-is-stored-and-still-stores-it", async () =>
        {
            var late = new LateResolvingStorage(storage()) { KeepOpenForReopen = true };
            var gate = new Deferred();
            var gated = GatedTask("test.close-in-gap", gate.Task);
            var (harness, _, _, root) = await OpenRoot(late, [gated]);
            var id = await Start(root, gated);
            var gapped = new Deferred();
            Task? closing = null;
            late.Gap = writes =>
            {
                if (!EndsTask(writes)) return false;
                closing = harness.CloseAsync(Context);
                gapped.Resolve();
                return true;
            };
            harness.Resume();
            var waited = Catch(async () => Outcome(await harness.WaitForTaskAsync(id, Context)));
            gate.Resolve();
            await gapped.Task;
            await closing!;
            // Close ends the waits before it drains the commit in flight, which is published and stored all the same.
            var result = await waited;
            Check(result is Exception { Message: var message } && message.Contains("closed"), $"the wait ends with close: {result}");
            Equal(SessionEndKind.Closed, (await harness.Closed).Kind, "end");
            var reopened = await OpenRoot(late.Reopen(), [gated]);
            Equal(DurableTaskStatus.Terminal, (await reopened.Harness.GetTaskAsync(id, Context))?.State.Status, "status after reopen");
            await reopened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "publication-at-adoption.fails-the-session-with-the-storage-error-when-it-rejects-after-adopting-and-reports-an-observers-throw", async () =>
        {
            var late = new LateResolvingStorage(storage());
            var opened = await OpenRoot(late, []);
            var (harness, _, reports, root) = opened;
            var bug = new InvalidOperationException("observer bug");
            FollowCommits(harness, (publication, _) =>
            {
                if (!publication.IsMemory && publication.Changes.Any(change => change is EntryWrite)) throw bug;
            });
            late.RejectAfterAdopt = writes => writes.Any(write => write is EntryWrite);
            await RejectsAsync(() => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = JsonObjectValue.Empty }),
                Context), "reclaim failed", "commit");
            var end = await harness.Closed;
            Equal(SessionEndKind.Failed, end.Kind, "end");
            Equal("reclaim failed", end.Error?.Message, "end error");
            var index = reports.Snapshot().IndexOf(bug);
            Check(index >= 0, "the observer's throw is reported");
            Equal(ReportInfo.ListenerError, opened.Infos[index], "observer info");
            Check(opened.Infos.Snapshot().Contains(ReportInfo.StorageFailed), "storage_failed reported");
        });

        yield return Case(prefix + "publication-at-adoption.stores-a-host-listeners-own-commit-before-the-reconcile-commit-that-the-same-publication-asks-for", async () =>
        {
            var controlled = Controlled();
            var gate = new Deferred();
            var child = GatedTask("test.order-child", gate.Task);
            var parent = OneStep("test.order-parent", async (task, runtime, ctx) =>
            {
                await runtime.CommitAsync(async (tx, _) =>
                {
                    await tx.CreateTaskAsync(child, JsonValue.Null, new TaskOptions(TaskOwnership.Task(task.Id)));
                }, ctx);
                // The child is still live, so the outcome is held as `completing` until a reconcile pass finalizes it.
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var (harness, _, _, root) = await OpenRoot(controlled, [parent, child]);
            var parentId = await Start(root, parent);
            harness.Resume();
            await Eventually(async () => (await harness.GetTaskAsync(parentId, Context))?.State.Status == DurableTaskStatus.Completing);
            harness.ObserveCommits(new CommitObserver((publication, observerContext) =>
            {
                var ended = publication.Changes.Any(change =>
                    change is TaskWrite { Value: var record } && record.Id != parentId && record.State.Status == DurableTaskStatus.Terminal);
                if (ended) _ = root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = JsonObjectValue.Empty }), Context);
            }, _ => { }));
            var before = controlled.CommitCount;
            gate.Resolve();
            await harness.WaitForTaskAsync(parentId, Context);
            // Read after close: no commit can land meanwhile.
            await harness.CloseAsync(Context);
            var kinds = controlled.Commits.Skip(before).Select(writes =>
            {
                if (writes.Any(write => write is EntryWrite)) return "host entry";
                if (writes.OfType<TaskWrite>().FirstOrDefault() is not { Value: { State.Status: DurableTaskStatus.Terminal } task }) return "other";
                return task.Id == parentId ? "parent terminal" : "child terminal";
            });
            Seq(["child terminal", "host entry", "parent terminal"], kinds.Where(kind => kind != "other"), "stored order");
        });

        // PiSharp's own (see the file header).
        yield return Case(prefix + "publication-at-adoption.stores-a-host-listeners-own-commit-before-the-reconcile-commit-while-a-busy-pool-holds-the-schedulers-work", async () =>
        {
            var controlled = Controlled();
            var gate = new Deferred();
            var finish = new Deferred();
            var created = new Deferred<TaskId>();
            var child = GatedTask("test.busy-pool-child", gate.Task);
            var parent = OneStep("test.busy-pool-parent", async (task, runtime, ctx) =>
            {
                await runtime.CommitAsync(async (tx, _) =>
                {
                    created.Resolve(await tx.CreateTaskAsync(child, JsonValue.Null, new TaskOptions(TaskOwnership.Task(task.Id))));
                }, ctx);
                await finish.Task;
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            });
            var (harness, _, _, root) = await OpenRoot(controlled, [parent, child]);
            var parentId = await Start(root, parent);
            harness.Resume();
            var childId = await created.Task;
            await Eventually(async () => (await harness.GetTaskAsync(childId, Context))?.State.Status == DurableTaskStatus.Running);
            // No pass is pending, so none the pool started on its own takes the requests below.
            var scheduler = ((DurableHarness)harness).Scheduler;
            await scheduler.QuiesceAsync();
            // From here on, the pool starts nothing the scheduler hands it. An idle wait asks for a reconcile pass outside a publication,
            // so the pass is pending when the parent's `completing` commit asks for one.
            var pool = new HeldPool();
            scheduler.Dispatch = pool.Post;
            var idle = harness.WaitForIdleAsync(Context);
            finish.Resolve();
            await Eventually(async () => (await harness.GetTaskAsync(parentId, Context))?.State.Status == DurableTaskStatus.Completing);
            await InvocationEnded(harness, parentId);
            harness.ObserveCommits(new CommitObserver((publication, observerContext) =>
            {
                var ended = publication.Changes.Any(change =>
                    change is TaskWrite { Value: var record } && record.Id != parentId && record.State.Status == DurableTaskStatus.Terminal);
                if (ended) _ = root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = JsonObjectValue.Empty }), Context);
            }, _ => { }));
            var before = controlled.CommitCount;
            var storing = controlled.HoldCommits();
            gate.Resolve();
            await storing.Entered;
            // The pool frees up while the child's terminal commit is stored, then the commit lands.
            pool.Release();
            storing.Release();
            await harness.WaitForTaskAsync(parentId, Context);
            await idle;
            await harness.CloseAsync(Context);
            var kinds = controlled.Commits.Skip(before).Select(writes =>
            {
                if (writes.Any(write => write is EntryWrite)) return "host entry";
                if (writes.OfType<TaskWrite>().FirstOrDefault() is not { Value: { State.Status: DurableTaskStatus.Terminal } task }) return "other";
                return task.Id == parentId ? "parent terminal" : "child terminal";
            });
            Seq(["child terminal", "host entry", "parent terminal"], kinds.Where(kind => kind != "other"), "stored order");
        });

        yield return Case(prefix + "publication-at-adoption.calls-a-host-listener-added-on-a-harness-during-a-publication-only-when-one-was-registered-at-it", async () =>
        {
            var controlled = Controlled();
            var (harness, _, _, root) = await OpenRoot(controlled, []);
            Task<EntryRecord> Note() => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = JsonObjectValue.Empty }), Context);
            var late = new SyncList<Seq>();
            CommitObservation? stopLate = null;
            // Runs once, during the next publication, after its dispatch was queued or not.
            Action? during = null;
            FollowCommits(harness, (_, _) => Interlocked.Exchange(ref during, null)?.Invoke());
            // No host listener at the publication, although the Harness's own observers run: no dispatch, so the late one misses it.
            Volatile.Write(ref during, () =>
            {
                stopLate = harness.ObserveCommits(new CommitObserver((publication, _) => late.Add(publication.Seq), _ => { }));
            });
            await Note();
            await Flush();
            Seq([], late.Snapshot(), "late, at the publication it was added in");
            await Note();
            Equal(1, late.Count, "late, at the next");
            stopLate!.Detach();
            // One registered at adopt: the dispatch reads the listeners when it runs, the late one included.
            harness.ObserveCommits(new CommitObserver((_, _) => { }, _ => { }));
            var second = new SyncList<Seq>();
            Volatile.Write(ref during, () =>
            {
                harness.ObserveCommits(new CommitObserver((publication, _) => second.Add(publication.Seq), _ => { }));
            });
            await Note();
            Equal(1, second.Count, "second, at the publication it was added in");
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>Whether <paramref name="publication"/> makes task <paramref name="id"/> terminal (upstream <c>terminal</c>).</summary>
    private static bool Terminal(CommitPublication publication, TaskId id) =>
        publication.Changes.Any(change => change is TaskWrite { Value: var record } && record.Id == id && record.State.Status == DurableTaskStatus.Terminal);

    /// <summary>Whether <paramref name="writes"/> make a task terminal (upstream <c>endsTask</c>).</summary>
    private static bool EndsTask(IReadOnlyList<StorageWrite> writes) =>
        writes.Any(write => write is TaskWrite { Value.State.Status: DurableTaskStatus.Terminal });

    /// <summary>Runs <paramref name="then"/> once <paramref name="wait"/> resumes (upstream's <c>.then(() => order.push(...))</c>).</summary>
    private static async Task Then(Task wait, Action then)
    {
        await wait;
        then();
    }

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>The values of the <c>pi.durable.tasks.active</c> counter, in order.</summary>
    private static List<double> ActiveTasks(PiSharp.Telemetry.InMemoryTelemetryContext telemetry) =>
        [.. telemetry.GetMetrics().Where(metric => metric.Name == "pi.durable.tasks.active").Select(metric => metric.Value)];

    /// <summary>Controlled storage counting its document scans (upstream's <c>CountingStorage</c> over MemoryStorage).</summary>
    private sealed class DocumentScans(IStorage inner) : ControlledStorage(inner)
    {
        private int scans;

        public int Scans => Volatile.Read(ref scans);

        public override Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
        {
            Interlocked.Increment(ref scans);
            return base.ScanDocumentsAsync(query, limit, cursor, context);
        }
    }

    /// <summary>Every committed write of task <paramref name="id"/>, in commit order.</summary>
    private static List<TaskRecord> TaskWrites(ControlledStorage storage, TaskId id)
    {
        int count = storage.CommitCount;
        var records = new List<TaskRecord>();
        // Read after close: no commit can land meanwhile.
        foreach (var batch in storage.Commits.Take(count))
            records.AddRange(batch.OfType<TaskWrite>().Select(write => write.Value).Where(record => record.Id == id));
        return records;
    }

    /// <summary>The value, or the error, of an operation (upstream's <c>.catch((error) => error)</c>).</summary>
    private static async Task<object?> Catch<T>(Func<Task<T>> run)
    {
        try { return await run(); }
        catch (Exception error) { return error; }
    }

    private static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>A registry reader built from delegates (upstream's object-literal readers).</summary>
    private sealed class DelegateReader(Func<IRegistrySnapshot> snapshot, Func<Action, IDisposable> subscribe) : IRegistryReader
    {
        public IRegistrySnapshot Snapshot() => snapshot();
        public IDisposable Subscribe(Action listener) => subscribe(listener);
    }
}
