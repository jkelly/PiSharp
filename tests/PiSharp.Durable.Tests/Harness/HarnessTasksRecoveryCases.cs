// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-tasks-recovery.test.ts. Verbatim apart from the
// adaptations in HarnessSupport.cs and HarnessRecoverySupport.cs, and these:
// - Crashes are simulated as upstream does: the crashed Harness is abandoned without close, its held commit never lands
//   (ControlledStorage.Crash) and its blocked handlers never return; a new Harness opens the same storage instance.
// - String(report) is the report's type name and message; (report as Error).cause is the cause the scheduler stores in Exception.Data.
// - "preserves an abort mark that races the handover commit ...": the commit log is read after close instead of before it; close writes no
//   task state, and storage.commits is not safe to read while commits may still land.
// None of the 24 tests needs generation, tools or compaction; all are ported.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class HarnessRecoveryTests
{
    private const string Crash = "test.crash";

    /// <summary>A task whose run ignores its signal and whose abort handler blocks while <paramref name="blockAbort"/> is set.</summary>
    private static DurableTask CrashTask(SyncList<string> log, bool blockAbort) => Tasks.DefineTask(new TaskDefinition
    {
        Name = Crash,
        Version = 1,
        Initial = _ => RunStep,
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (_, _, _) =>
            {
                log.Add("run");
                await Never();
            },
        },
        Abort = async (_, runtime, ctx) =>
        {
            log.Add("abort");
            if (blockAbort) await Never();
            await runtime.CommitAsync((_, _) => AbortedWith("recovered"), ctx);
        },
    });

    /// <summary>Crashes the held commit and opens a new Harness over the same storage (upstream <c>recover</c>).</summary>
    private static async Task<(IHarness Harness, TaskId Id)> Recover(ControlledStorage storage, SyncList<string> log)
    {
        storage.Crash();
        var (harness, _, _) = await OpenTasks(storage, [CrashTask(log, blockAbort: false)]);
        var page = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = Crash }, 1), Context);
        return (harness, page.Items[0].Id);
    }

    /// <summary>A crash task whose run invocation is active (upstream <c>crashedRun</c>).</summary>
    private static async Task<(ControlledStorage Storage, IHarness Harness, TaskId Id)> CrashedRun(Backend backend, SyncList<string> log)
    {
        var storage = new ControlledStorage(backend.Fresh());
        var (harness, _, _) = await OpenTasks(storage, [CrashTask(log, blockAbort: true)]);
        var id = await CreateIn(harness, CrashTask(log, blockAbort: true));
        harness.Resume();
        await Eventually(() => log.Count == 1);
        return (storage, harness, id);
    }

    private sealed record Gates(Task? A = null, Task? B = null, Action? OnEnd = null);

    private static DurableTask HandoverTask(string label, int version, SyncList<string> log, Gates? gates = null, TaskMigration? migrate = null)
    {
        TaskPhaseHandler Advance(string phase, string next) => async (_, runtime, ctx) =>
        {
            log.Add($"{label}:{phase} start");
            var gate = phase == "a" ? gates?.A : gates?.B;
            if (gate is not null) await gate;
            await runtime.CommitAsync((_, _) => NextTaskState.Running(Obj(("phase", next))), ctx);
            // Leave room for a wrongly dispatched successor before this invocation ends.
            await Flush();
            gates?.OnEnd?.Invoke();
            log.Add($"{label}:{phase} end");
        };
        return Tasks.DefineTask(new TaskDefinition
        {
            Name = "test.handover",
            Version = version,
            Initial = _ => O("""{"phase":"a"}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["a"] = Advance("a", "b"),
                ["b"] = Advance("b", "c"),
                ["c"] = async (_, runtime, ctx) =>
                {
                    log.Add($"{label}:c");
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            },
            Abort = async (_, runtime, ctx) =>
            {
                log.Add($"{label}:abort");
                await runtime.CommitAsync((_, _) => AbortedWith(label), ctx);
            },
            Migrate = migrate,
        });
    }

    private sealed record Handover(IHarness Harness, IRegistry Registry, SyncList<Exception> Reports, IDisposable Old, TaskId Id);

    private static async Task<Handover> StartHandover(SyncList<string> log, Gates gates, IStorage storage)
    {
        var registry = Registry.Create();
        var old = AddTask(registry, HandoverTask("old", 1, log, gates));
        var (harness, _, reports) = await OpenTasks(storage, [], registry);
        var id = await CreateIn(harness, HandoverTask("old", 1, log));
        harness.Resume();
        await Eventually(() => log.Count == 1);
        return new Handover(harness, registry, reports, old, id);
    }

    internal static IEnumerable<(string, Func<Task>)> RecoveryCases(string prefix, Backend backend)
    {
        // ─── task recovery ───

        if (backend.Reopenable is { } reopenable)
        {
            yield return Case(prefix + "task-recovery.resumes-an-intent-effect-outcome-task-interrupted-after-its-intent-across-close-and-reopen", async () =>
            {
                var open = reopenable();
                var service = new TransferService();
                var transfer = TransferTask(service, new Interrupt());

                var first = await OpenTasks(await open(), [transfer]);
                var root = await first.Harness.RootAsync(Context);
                var id = await root.CommitAsync(tx => tx.CreateTaskAsync(transfer, O("""{"amount":7}"""), new TaskOptions(TaskOwnership.Conversation)), Context);
                first.Harness.Resume();
                await Eventually(() => service.Calls == 1);
                await first.Harness.CloseAsync(Context);

                var second = await OpenTasks(await open(), [transfer]);
                // Open reconciled `running` to `pending` and kept the checkpoint and memos; nothing ran yet.
                MatchJson("""
                    {"abortRequested":false,"memos":{"requested":7},"state":{"status":"pending","checkpoint":{"phase":"apply","key":"transfer-ID"}}}
                    """.Replace("ID", id.ToString(), StringComparison.Ordinal), (await second.Harness.GetTaskAsync(id, Context))?.ToJson(), "reopened");
                Equal(1, service.Calls, "calls after reopen");
                second.Harness.Resume();
                var receipt = await second.Harness.WaitForTaskAsync(id, Context);
                SameJson("""{"status":"completed","result":{"receipt":70}}""", OutcomeOf(receipt), "outcome");
                Equal(2, service.Calls, "calls");
                Equal(1, service.AppliedCount, "applied");
                await second.Harness.CloseAsync(Context);

                var third = await OpenTasks(await open(), [transfer]);
                SameJson(receipt.ToJson().ToJson(), (await third.Harness.GetTaskAsync(id, Context))?.ToJson(), "receipt after reopen");
                await third.Harness.CloseAsync(Context);
            });

            // #10549
            yield return Case(prefix + "task-recovery.keeps-a-tasks-startedat-across-close-and-reopen-and-stamps-endedat-when-it-settles", async () =>
            {
                var open = reopenable();
                var service = new TransferService();
                var transfer = TransferTask(service, new Interrupt());
                double clock = 2_000;
                double Now() => Volatile.Read(ref clock);

                var first = await OpenTasks(await open(), [transfer], now: Now);
                var root = await first.Harness.RootAsync(Context);
                var id = await root.CommitAsync(tx => tx.CreateTaskAsync(transfer, O("""{"amount":7}"""), new TaskOptions(TaskOwnership.Conversation)), Context);
                first.Harness.Resume();
                await Eventually(() => service.Calls == 1);
                Volatile.Write(ref clock, 3_000);
                await first.Harness.CloseAsync(Context);

                Volatile.Write(ref clock, 9_000);
                var second = await OpenTasks(await open(), [transfer], now: Now);
                var reopened = await second.Harness.GetTaskAsync(id, Context);
                Equal("pending:2000:-", $"{(reopened is null ? "-" : Wire(reopened.State.Status))}:{reopened?.StartedAt?.ToString() ?? "-"}:{reopened?.EndedAt?.ToString() ?? "-"}",
                    "reopened");
                second.Harness.Resume();
                var receipt = await second.Harness.WaitForTaskAsync(id, Context);
                Equal((2_000d, 9_000d), (receipt.StartedAt ?? 0, receipt.EndedAt ?? 0), "receipt times");
                await second.Harness.CloseAsync(Context);
            });

            yield return Case(prefix + "task-recovery.resumes-abort-work-after-close-at-every-direct-task-abort-stage", async () =>
            {
                var open = reopenable();
                var log = new SyncList<string>();
                var blockAbort = true;
                var abortReached = new Deferred();
                var runRelease = new Deferred();
                var abortable = Tasks.DefineTask(new TaskDefinition
                {
                    Name = "test.abortable",
                    Version = 1,
                    Initial = _ => RunStep,
                    Phases = new Dictionary<string, TaskPhaseHandler>
                    {
                        ["run"] = async (_, _, _) =>
                        {
                            log.Add("run");
                            // Ignores the abort signal until released, so the mark is durable while the run is active.
                            await runRelease.Task;
                        },
                    },
                    Abort = async (_, runtime, ctx) =>
                    {
                        log.Add("abort");
                        if (Volatile.Read(ref blockAbort))
                        {
                            abortReached.Resolve();
                            await Aborted(runtime.Signal);
                        }
                        await runtime.CommitAsync((_, _) => AbortedWith("stop"), ctx);
                    },
                });

                // Stage 1: the mark is committed while the run invocation is still active.
                var opened = await OpenTasks(await open(), [abortable]);
                var id = await CreateIn(opened.Harness, abortable);
                opened.Harness.Resume();
                await Eventually(() => log.Count == 1);
                var aborting = opened.Harness.AbortTaskAsync(id, Context);
                while ((await opened.Harness.GetTaskAsync(id, Context))?.AbortRequested != true) await Flush();
                var closing = opened.Harness.CloseAsync(Context);
                runRelease.Resolve();
                await closing;
                Equal(TaskAbortResult.Marked, await aborting, "stage 1 abort");
                Seq(["run"], log.Snapshot(), "stage 1 log");

                // Stage 2: reopen dispatches the abort invocation, never the run; close while it is active.
                opened = await OpenTasks(await open(), [abortable]);
                MatchJson("""{"abortRequested":true,"state":{"status":"pending"}}""", (await opened.Harness.GetTaskAsync(id, Context))?.ToJson(), "stage 2 record");
                opened.Harness.Resume();
                await abortReached.Task;
                await opened.Harness.CloseAsync(Context);
                Seq(["run", "abort"], log.Snapshot(), "stage 2 log");

                // Stage 3: a fresh abort invocation settles the task.
                Volatile.Write(ref blockAbort, false);
                opened = await OpenTasks(await open(), [abortable]);
                opened.Harness.Resume();
                SameJson("""{"status":"aborted","reason":"stop"}""", OutcomeOf(await opened.Harness.WaitForTaskAsync(id, Context)), "stage 3 outcome");
                await opened.Harness.CloseAsync(Context);
                Seq(["run", "abort", "abort"], log.Snapshot(), "stage 3 log");

                // Stage 4: the terminal receipt survives reopen and nothing runs again.
                opened = await OpenTasks(await open(), [abortable]);
                opened.Harness.Resume();
                await Flush();
                Equal(TaskAbortResult.Terminal, await opened.Harness.AbortTaskAsync(id, Context), "stage 4 abort");
                await opened.Harness.CloseAsync(Context);
                Seq(["run", "abort", "abort"], log.Snapshot(), "stage 4 log");
            });
        }

        // ─── task crash recovery ───
        // Crash simulation: the crashed Harness is abandoned without close, its held storage commit never lands, and its blocked handlers
        // never return. A new Harness then opens the same storage.

        yield return Case(prefix + "task-crash-recovery.crash-while-the-mark-commit-is-in-storage-the-run-resumes", async () =>
        {
            var log = new SyncList<string>();
            var (storage, harness, id) = await CrashedRun(backend, log);
            var held = storage.HoldCommits();
            _ = harness.AbortTaskAsync(id, Context);
            await held.Entered;
            var recovered = await Recover(storage, log);
            MatchJson("""{"abortRequested":false,"state":{"status":"pending"}}""", (await recovered.Harness.GetTaskAsync(id, Context))?.ToJson(), "recovered");
            recovered.Harness.Resume();
            await Eventually(() => log.Count == 2);
            Seq(["run", "run"], log.Snapshot(), "log");
        });

        yield return Case(prefix + "task-crash-recovery.crash-after-the-mark-before-the-run-joins-only-the-abort-handler-runs", async () =>
        {
            var log = new SyncList<string>();
            var (storage, harness, id) = await CrashedRun(backend, log);
            // The run ignores its signal, so abortTask never finishes joining it.
            var joined = harness.AbortTaskAsync(id, Context);
            while ((await harness.GetTaskAsync(id, Context))?.AbortRequested != true) await Flush();
            Check(!await Settled(joined), "abortTask has not joined the run");
            var recovered = await Recover(storage, log);
            MatchJson("""{"abortRequested":true,"state":{"status":"pending"}}""", (await recovered.Harness.GetTaskAsync(id, Context))?.ToJson(), "recovered");
            recovered.Harness.Resume();
            SameJson("""{"status":"aborted","reason":"recovered"}""", OutcomeOf(await recovered.Harness.WaitForTaskAsync(id, Context)), "outcome");
            Seq(["run", "abort"], log.Snapshot(), "log");
            await recovered.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-crash-recovery.crash-while-the-abort-handler-runs-a-fresh-abort-invocation-settles-the-task", async () =>
        {
            var log = new SyncList<string>();
            var storage = new ControlledStorage(backend.Fresh());
            var crash = CrashTask(log, blockAbort: true);
            var (harness, _, _) = await OpenTasks(storage, [crash]);
            var id = await CreateIn(harness, crash);
            await harness.AbortTaskAsync(id, Context);
            harness.Resume();
            await Eventually(() => log.Count == 1);
            Seq(["abort"], log.Snapshot(), "log before crash");
            MatchJson("""{"state":{"status":"running"}}""", (await harness.GetTaskAsync(id, Context))?.ToJson(), "abort invocation reserved");
            var recovered = await Recover(storage, log);
            recovered.Harness.Resume();
            SameJson("""{"status":"aborted","reason":"recovered"}""", OutcomeOf(await recovered.Harness.WaitForTaskAsync(id, Context)), "outcome");
            Seq(["abort", "abort"], log.Snapshot(), "log");
            await recovered.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-crash-recovery.crash-while-the-abort-outcome-is-in-storage-the-abort-handler-runs-again", async () =>
        {
            var log = new SyncList<string>();
            var storage = new ControlledStorage(backend.Fresh());
            var reached = new Deferred();
            var proceed = new Deferred();
            var crash = Tasks.DefineTask(new TaskDefinition
            {
                Name = Crash,
                Version = 1,
                Initial = _ => RunStep,
                Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
                Abort = async (_, runtime, ctx) =>
                {
                    log.Add("abort");
                    reached.Resolve();
                    await proceed.Task;
                    await runtime.CommitAsync((_, _) => AbortedWith("lost"), ctx);
                },
            });
            var (harness, _, _) = await OpenTasks(storage, [crash]);
            var id = await CreateIn(harness, crash);
            await harness.AbortTaskAsync(id, Context);
            harness.Resume();
            await reached.Task;
            var held = storage.HoldCommits();
            proceed.Resolve();
            await held.Entered;
            var recovered = await Recover(storage, log);
            recovered.Harness.Resume();
            SameJson("""{"status":"aborted","reason":"recovered"}""", OutcomeOf(await recovered.Harness.WaitForTaskAsync(id, Context)), "outcome");
            Seq(["abort", "abort"], log.Snapshot(), "log");
            await recovered.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-crash-recovery.crash-after-the-terminal-outcome-nothing-runs-again", async () =>
        {
            var log = new SyncList<string>();
            var storage = new ControlledStorage(backend.Fresh());
            var crash = CrashTask(log, blockAbort: false);
            var (harness, _, _) = await OpenTasks(storage, [crash]);
            var id = await CreateIn(harness, crash);
            await harness.AbortTaskAsync(id, Context);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            var recovered = await Recover(storage, log);
            recovered.Harness.Resume();
            await Flush();
            Seq(["abort"], log.Snapshot(), "log");
            SameJson("""{"status":"terminal","outcome":{"status":"aborted","reason":"recovered"}}""",
                (await recovered.Harness.GetTaskAsync(id, Context))?.State.ToJson(), "state");
            await recovered.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "task-crash-recovery.crash-while-the-reservation-commit-is-in-storage-the-task-is-still-pending", async () =>
        {
            var log = new SyncList<string>();
            var storage = new ControlledStorage(backend.Fresh());
            var crash = CrashTask(log, blockAbort: false);
            var (harness, _, _) = await OpenTasks(storage, [crash]);
            var id = await CreateIn(harness, crash);
            var held = storage.HoldCommits();
            harness.Resume();
            await held.Entered;
            var recovered = await Recover(storage, log);
            Equal(DurableTaskStatus.Pending, (await recovered.Harness.GetTaskAsync(id, Context))?.State.Status, "status");
            Seq([], log.Snapshot(), "log before abort");
            await recovered.Harness.AbortTaskAsync(id, Context);
            recovered.Harness.Resume();
            await recovered.Harness.WaitForTaskAsync(id, Context);
            Seq(["abort"], log.Snapshot(), "log");
            await recovered.Harness.CloseAsync(Context);
        });

        // ─── blocked tasks ───

        yield return Case(prefix + "blocked-tasks.keeps-a-task-with-a-missing-definition-pending-and-live-for-idle-waits-until-registration", async () =>
        {
            var v1 = Versioned(1, "v1");
            var (harness, registry, _) = await OpenTasks(backend.Fresh(), []);
            var id = await CreateIn(harness, v1);
            harness.Resume();
            await Flush();
            Equal(DurableTaskStatus.Pending, (await harness.GetTaskAsync(id, Context))?.State.Status, "status");
            var idle = harness.WaitForIdleAsync(Context);
            await Flush();
            AddTask(registry, v1);
            await idle;
            SameJson("""{"status":"terminal","outcome":{"status":"completed","result":"v1"}}""", (await harness.GetTaskAsync(id, Context))?.State.ToJson(), "state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "blocked-tasks.keeps-a-task-stored-by-a-newer-version-pending-until-a-fitting-definition-is-registered", async () =>
        {
            var registry = Registry.Create();
            AddTask(registry, Versioned(1, "old"));
            var (harness, _, _) = await OpenTasks(backend.Fresh(), [], registry);
            var id = await CreateIn(harness, Versioned(2, "new"));
            harness.Resume();
            await Flush();
            Equal(DurableTaskStatus.Pending, (await harness.GetTaskAsync(id, Context))?.State.Status, "status");
            // The same extension name replaces the old one in place.
            AddTask(registry, Versioned(2, "new"));
            SameJson("""{"status":"completed","result":"new"}""", OutcomeOf(await harness.WaitForTaskAsync(id, Context)), "outcome");
            await harness.CloseAsync(Context);
        });

        if (backend.Reopenable is { } reopenMigrate)
        {
            yield return Case(prefix + "blocked-tasks.migrates-at-reservation-leaves-the-record-unchanged-when-migration-fails-and-retries-only-for-a-new-definition", async () =>
            {
                var open = reopenMigrate();
                var opened = await OpenTasks(await open(), []);
                var id = await CreateIn(opened.Harness, Versioned(1, "v1"));
                await opened.Harness.CloseAsync(Context);

                var registry = Registry.Create();
                var failures = 0;
                AddTask(registry, Versioned(2, "v2", (_, _, _) =>
                {
                    Interlocked.Increment(ref failures);
                    throw new InvalidOperationException("cannot migrate");
                }));
                opened = await OpenTasks(await open(), [], registry);
                opened.Harness.Resume();
                await Eventually(() => opened.Reports.Count == 1);
                // An unrelated registry change wakes the scheduler without retrying the same failed definition.
                AddTool(registry, Define.Tool(new ToolRegistration
                {
                    Name = "unrelated",
                    Description = "unrelated",
                    Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Content = JsonArrayValue.Empty }),
                }));
                await Flush();
                Equal(1, Volatile.Read(ref failures), "failures");
                Equal(1, opened.Reports.Count, "reports");
                var described = Describe(opened.Reports[0]);
                Check(described.Contains("cannot migrate", StringComparison.Ordinal), $"report: {described}");
                MatchJson("""{"version":1,"state":{"status":"pending","checkpoint":{"phase":"run"}}}""",
                    (await opened.Harness.GetTaskAsync(id, Context))?.ToJson(), "blocked record");

                var migrations = new SyncList<int>();
                // The same extension name replaces the old one in place.
                AddTask(registry, Versioned(2, "v2", (_, checkpoint, fromVersion) =>
                {
                    migrations.Add(fromVersion);
                    return new TaskMigrationResult(JsonValue.Null, checkpoint);
                }));
                var receipt = await opened.Harness.WaitForTaskAsync(id, Context);
                MatchJson("""{"version":2,"state":{"outcome":{"status":"completed","result":"v2"}}}""", receipt.ToJson(), "receipt");
                Seq([1], migrations.Snapshot(), "migrations");
                await opened.Harness.CloseAsync(Context);
            });

            yield return Case(prefix + "blocked-tasks.blocks-an-older-record-whose-newer-definition-has-no-migration", async () =>
            {
                var open = reopenMigrate();
                var opened = await OpenTasks(await open(), []);
                var id = await CreateIn(opened.Harness, Versioned(1, "v1"));
                await opened.Harness.CloseAsync(Context);
                opened = await OpenTasks(await open(), [Versioned(2, "v2")]);
                opened.Harness.Resume();
                await Eventually(() => opened.Reports.Count == 1);
                var described = Describe(opened.Reports[0]);
                Check(described.Contains("has no migration from 1", StringComparison.Ordinal), $"report: {described}");
                MatchJson("""{"version":1,"state":{"status":"pending"}}""", (await opened.Harness.GetTaskAsync(id, Context))?.ToJson(), "record");
                await opened.Harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "blocked-tasks.settles-an-aborted-blocked-task-as-orphaned-and-retires-its-documents", async () =>
        {
            var scratch = Documents.DefineTaskDoc("test.orphan-scratch", 1, () => O("""{"n":0}"""));
            var (harness, _, _) = await OpenTasks(backend.Fresh(), []);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(async tx =>
            {
                var created = await tx.CreateTaskAsync(Versioned(1, "x"), JsonValue.Null, new TaskOptions(TaskOwnership.Conversation));
                (await tx.DocAsync(scratch, created))["n"] = 1;
                return created;
            }, Context);
            // Before resume: the marking commit settles the blocked task directly.
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort");
            SameJson("""{"status":"terminal","outcome":{"status":"orphaned","reason":"missing_task"}}""",
                (await harness.GetTaskAsync(id, Context))?.State.ToJson(), "state");
            Check(await harness.SnapshotAsync(scratch, id, Context) is null, "task document retired");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "blocked-tasks.orphans-a-marked-task-whose-definition-disappeared-while-its-run-was-active", async () =>
        {
            var reached = new Deferred();
            var running = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.vanishing",
                Version = 1,
                Initial = _ => RunStep,
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, runtime, _) =>
                    {
                        reached.Resolve();
                        await Aborted(runtime.Signal);
                    },
                },
                Abort = (_, _, _) => Task.FromException(new InvalidOperationException("must not run")),
            });
            var registry = Registry.Create();
            var registration = AddTask(registry, running);
            var (harness, _, _) = await OpenTasks(backend.Fresh(), [], registry);
            var id = await CreateIn(harness, running);
            harness.Resume();
            await reached.Task;
            registration.Dispose();
            // An active run means the mark does not orphan directly; the scheduler orphans once the run has ended.
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(id, Context), "abort");
            SameJson("""{"status":"orphaned","reason":"missing_task"}""", OutcomeOf(await harness.WaitForTaskAsync(id, Context)), "outcome");
            await harness.CloseAsync(Context);
        });

        if (backend.Reopenable is { } reopenOrphan)
        {
            yield return Case(prefix + "blocked-tasks.orphans-a-reopened-abort-marked-task-without-a-definition-once-scheduling-resumes", async () =>
            {
                var open = reopenOrphan();
                var opened = await OpenTasks(await open(), [Versioned(1, "x")]);
                var id = await CreateIn(opened.Harness, Versioned(1, "x"));
                Equal(TaskAbortResult.Marked, await opened.Harness.AbortTaskAsync(id, Context), "abort");
                await opened.Harness.CloseAsync(Context);

                opened = await OpenTasks(await open(), []);
                MatchJson("""{"abortRequested":true,"state":{"status":"pending"}}""", (await opened.Harness.GetTaskAsync(id, Context))?.ToJson(), "reopened");
                var idle = opened.Harness.WaitForIdleAsync(Context);
                opened.Harness.Resume();
                await idle;
                SameJson("""{"status":"terminal","outcome":{"status":"orphaned","reason":"missing_task"}}""",
                    (await opened.Harness.GetTaskAsync(id, Context))?.State.ToJson(), "state");
                await opened.Harness.CloseAsync(Context);
            });
        }

        // ─── definition handover ───

        yield return Case(prefix + "definition-handover.hands-over-at-the-next-phase-boundary-to-a-same-version-replacement-without-overlap", async () =>
        {
            var log = new SyncList<string>();
            var gate = new Deferred();
            var (harness, registry, _, _, id) = await StartHandover(log, new Gates(A: gate.Task), backend.Fresh());
            // The same extension name replaces the old one in place.
            AddTask(registry, HandoverTask("new", 1, log));
            gate.Resolve();
            await harness.WaitForTaskAsync(id, Context);
            Seq(["old:a start", "old:a end", "new:b start", "new:b end", "new:c"], log.Snapshot(), "log");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "definition-handover.keeps-the-memos-across-a-handover", async () =>
        {
            var log = new SyncList<string>();
            var gate = new Deferred();
            var reached = new Deferred();
            DurableTask MemoTask(string label) => Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.handover-memo",
                Version = 1,
                Initial = _ => O("""{"phase":"a"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["a"] = async (_, runtime, ctx) =>
                    {
                        await runtime.MemoAsync("picked", label, ctx);
                        reached.Resolve();
                        await gate.Task;
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"b"}""")), ctx);
                    },
                    ["b"] = async (_, runtime, ctx) =>
                    {
                        // The candidate loses to the memo the old definition stored.
                        var picked = await runtime.MemoAsync("picked", label, ctx);
                        log.Add($"{label}:b {S(picked)}");
                        await runtime.CommitAsync((_, _) => Completed(picked), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var registry = Registry.Create();
            AddTask(registry, MemoTask("old"));
            var (harness, _, _) = await OpenTasks(backend.Fresh(), [], registry);
            var id = await CreateIn(harness, MemoTask("old"));
            harness.Resume();
            await reached.Task;
            AddTask(registry, MemoTask("new"));
            gate.Resolve();
            var receipt = await harness.WaitForTaskAsync(id, Context);
            SameJson("""{"status":"completed","result":"old"}""", OutcomeOf(receipt), "outcome");
            Seq(["new:b old"], log.Snapshot(), "log");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "definition-handover.hands-over-to-a-newer-version-with-a-migration", async () =>
        {
            var log = new SyncList<string>();
            var gate = new Deferred();
            var (harness, registry, _, _, id) = await StartHandover(log, new Gates(A: gate.Task), backend.Fresh());
            // The same extension name replaces the old one in place.
            AddTask(registry, HandoverTask("v2", 2, log, migrate: (_, _, _) => new TaskMigrationResult(JsonValue.Null, O("""{"phase":"c"}"""))));
            gate.Resolve();
            var receipt = await harness.WaitForTaskAsync(id, Context);
            Equal(2, receipt.Version, "version");
            Seq(["old:a start", "old:a end", "v2:c"], log.Snapshot(), "log");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "definition-handover.hands-over-to-a-newer-version-whose-migration-fails-and-leaves-the-task-blocked", async () =>
        {
            var log = new SyncList<string>();
            var gate = new Deferred();
            var (harness, registry, reports, _, id) = await StartHandover(log, new Gates(A: gate.Task), backend.Fresh());
            // The same extension name replaces the old one in place.
            AddTask(registry, HandoverTask("broken", 2, log, migrate: (_, _, _) => throw new InvalidOperationException("broken migration")));
            gate.Resolve();
            await Eventually(() => reports.Count == 1);
            await Flush();
            MatchJson("""{"version":1,"state":{"status":"pending","checkpoint":{"phase":"b"}}}""", (await harness.GetTaskAsync(id, Context))?.ToJson(), "record");
            Seq(["old:a start", "old:a end"], log.Snapshot(), "log");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "definition-handover.keeps-running-under-the-old-definition-when-the-replacement-is-missing-or-cannot-take-the-task", async () =>
        {
            var log = new SyncList<string>();
            var gateA = new Deferred();
            var gateB = new Deferred();
            var (harness, registry, reports, old, id) = await StartHandover(log, new Gates(A: gateA.Task, B: gateB.Task), backend.Fresh());
            old.Dispose();
            gateA.Resolve();
            await Eventually(() => log.Snapshot().Contains("old:b start"));
            // A newer definition without a migration cannot take the task either.
            AddTask(registry, HandoverTask("incompatible", 2, log));
            gateB.Resolve();
            await harness.WaitForTaskAsync(id, Context);
            Seq(["old:a start", "old:a end", "old:b start", "old:b end", "old:c"], log.Snapshot(), "log");
            Seq(["missing_task", "incompatible_task"], reports.Snapshot().Select(report => Cause(report) as string), "report causes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "definition-handover.rejects-a-runtime-commit-of-the-old-invocation-queued-behind-its-handover-commit", async () =>
        {
            var log = new SyncList<string>();
            var gate = new Deferred();
            var storage = new ControlledStorage(backend.Fresh());
            Gate? held = null;
            ITaskRuntime? oldRuntime = null;
            IHarness? harnessRef = null;
            var registry = Registry.Create();
            var old = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.handover",
                Version = 1,
                Initial = _ => O("""{"phase":"a"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["a"] = async (task, runtime, ctx) =>
                    {
                        Volatile.Write(ref oldRuntime, runtime);
                        await gate.Task;
                        await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"b"}""")), ctx);
                        // Hold the line with an unrelated commit so the handover commit queues behind it.
                        Volatile.Write(ref held, storage.HoldCommits());
                        _ = harnessRef!.CommitAsync(async tx => { await tx.AppendEntryAsync(task.ConversationId, new EntryDraft { Kind = "blocker" }); }, ctx);
                    },
                    ["b"] = (_, _, _) => Task.CompletedTask,
                    ["c"] = (_, _, _) => Task.CompletedTask,
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            AddTask(registry, old);
            var (harness, _, _) = await OpenTasks(storage, [], registry);
            harnessRef = harness;
            var id = await CreateIn(harness, old);
            harness.Resume();
            await Eventually(() => Volatile.Read(ref oldRuntime) is not null);
            // The same extension name replaces the old one in place.
            AddTask(registry, HandoverTask("new", 1, log));
            gate.Resolve();
            await Eventually(() => Volatile.Read(ref held) is not null);
            await held!.Entered;
            await Flush();
            // Queued behind the handover commit while the invocation has not ended yet.
            var late = oldRuntime!.CommitAsync((_, _) => Completed(null), Context);
            held.Release();
            await RejectsAsync(() => late, "invocation has ended", "late commit");
            await harness.WaitForTaskAsync(id, Context);
            Seq(["new:b start", "new:b end", "new:c"], log.Snapshot(), "log");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "definition-handover.preserves-an-abort-mark-that-races-the-handover-commit-the-new-definition-aborts", async () =>
        {
            var log = new SyncList<string>();
            var gate = new Deferred();
            var storage = new ControlledStorage(backend.Fresh());
            Gate? held = null;
            // The progress commit landed; hold the next commit, the handover, and queue the abort mark behind it.
            var gates = new Gates(A: gate.Task, OnEnd: () =>
            {
                if (Volatile.Read(ref held) is null) Volatile.Write(ref held, storage.HoldCommits());
            });
            var (harness, registry, _, _, id) = await StartHandover(log, gates, storage);
            // The same extension name replaces the old one in place.
            AddTask(registry, HandoverTask("new", 1, log));
            gate.Resolve();
            await Eventually(() => Volatile.Read(ref held) is not null);
            await held!.Entered;
            var aborting = harness.AbortTaskAsync(id, Context);
            held.Release();
            Equal(TaskAbortResult.Marked, await aborting, "abort");
            SameJson("""{"status":"aborted","reason":"new"}""", OutcomeOf(await harness.WaitForTaskAsync(id, Context)), "outcome");
            Seq(["old:a start", "old:a end", "new:abort"], log.Snapshot(), "log");
            await harness.CloseAsync(Context);
            var states = WritesOf(storage, id).Select(record => $"{Wire(record.State.Status)}{(record.AbortRequested ? "+mark" : "")}");
            // created, reserved, progress, handover, mark, abort reservation, aborted
            Seq(["pending", "running", "running", "pending", "pending+mark", "running+mark", "terminal+mark"], states, "task writes");
        });

        // ─── Harness open ───

        yield return Case(prefix + "harness-open.releases-its-registry-subscription-and-closes-the-session-when-open-fails", async () =>
        {
            var storage = new ControlledStorage(backend.Fresh());
            var log = new SyncList<string>();
            var stuck = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.stuck",
                Version = 1,
                Initial = _ => RunStep,
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, _, _) =>
                    {
                        log.Add("run");
                        await Never();
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            // Leave a running task behind so open has a reconciliation commit to fail.
            var first = await OpenTasks(storage, [stuck]);
            await CreateIn(first.Harness, stuck);
            first.Harness.Resume();
            await Eventually(() => log.Count == 1);

            var registry = Registry.Create();
            var reader = new CountingReader(registry);
            storage.FailNextCommit(new InvalidOperationException("disk full"));
            await RejectsAsync(() => Harness.OpenAsync(storage, new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = reader }, Context),
                "disk full", "open");
            Equal(0, reader.Subscriptions, "subscriptions");
            await ThrowsAsync<Exception>(() => storage.GetTaskAsync(new TaskId(1), Context), "storage closed");
        });
    }
}
