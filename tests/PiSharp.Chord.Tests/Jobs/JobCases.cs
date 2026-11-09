using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Jobs;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Native expectations authored from v1.5.0 next/jobs/job.test.ts and examples 19 and 28.
internal static partial class Program
{
    private static JobDefinition J1Definition(int? limit = null, StateDefinition? state = null, JobPersistence? persist = null, long version = 1,
        IReadOnlyDictionary<long, JobAdapter>? accepts = null) => JobDefinition.Define("test.work", new()
        { Version = version, Args = Obj(("n", Int())), Result = Int(), Concurrency = limit, State = state, Persist = persist, Accepts = accepts });
    private static StateDefinition J1State(bool persist = false) => StateDefinition.Define("test.progress", new()
    { Version = 1, Schema = Obj(("value", Int())), Initial = () => new() { ["value"] = 0 }, Persist = persist,
        CheckpointWhen = persist ? (_, _, _) => false : null });
    private static async Task<(ChordHost Host, ISelf Self, IJobHandle Job, List<HostError> Errors)> J1Host(JobDefinition definition,
        Func<ISelf, JobImplementation> implementation, ChordStore? store = null, TimeProvider? time = null)
    {
        var errors = new List<HostError>(); ISelf? owner = null; IJobHandle? job = null;
        var host = Hosts.Create(new() { Store = store, TimeProvider = time ?? TimeProvider.System, OnError = e => errors.Add(e),
            ReadyTimeout = TimeSpan.FromSeconds(5), CloseTimeout = TimeSpan.FromMilliseconds(200) });
        var type = Contract.Local("test.jobs", new() { Version = 1, Params = new(), Members = new() });
        await host.LoadAsync([new("jobs", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
        {
            owner = self; job = self.Job(definition, implementation(self)); return new(new Dictionary<string, object?>());
        })))], ChordContext.Background);
        if (owner is null || job is null) throw new InvalidOperationException("Job factory did not activate");
        return (host, owner, job, errors);
    }
    private static TaskCompletionSource J1Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task J1Seed(ChordStore store, JobDefinition definition, JobRun run)
    {
        var record = PersistedRuns.Encode(run);
        return store.CommitAsync([StoreInternal.CreateDocument(new("test.jobs", "chord.jobs", definition.Name, run.Id),
            new(1, "chord.job-run.1", record), ChordJson.Stringify(record), _ => { })], CommitFence.Open, ChordContext.Background);
    }
    private static async Task<Exception> J1Rejection(Task task)
    {
        try { await task; } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected a rejection");
    }
    private static async Task J1Until(Func<bool> condition, string? transition = null)
    {
        for (var attempt = 0; attempt < 200; attempt++) { if (condition()) return; await Task.Delay(5); }
        throw new InvalidOperationException("Timed out awaiting job transition" + (transition is null ? "" : ": " + transition));
    }
    private sealed class J1RetryClock : TimeProvider
    {
        private readonly FakeTimers timers = new();
        private readonly Lock gate = new();
        private readonly List<TimeSpan> scheduled = [];
        public override DateTimeOffset GetUtcNow() => timers.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = timers.CreateTimer(callback, state, dueTime, period);
            lock (gate) scheduled.Add(dueTime);
            return timer;
        }
        internal bool Scheduled(TimeSpan delay) { lock (gate) return scheduled.Contains(delay); }
        internal void Advance(TimeSpan delay) => timers.AdvanceTimersByTime(delay.TotalMilliseconds);
    }
    private static IEnumerable<(string, Func<Task>)> JobsDefinitionCases() =>
    [
        Case("jobs.definition.closed-json-and-declaration-policy", () =>
        {
            Refuses(() => JobDefinition.Define("Bad", new() { Version = 1, Args = Obj(), Result = Int() }), "job name");
            Refuses(() => JobDefinition.Define("test", new() { Version = 0, Args = Obj(), Result = Int() }), "positive integer");
            Refuses(() => JobDefinition.Define("test", new() { Version = 1, Args = Bytes(), Result = Int() }), "no bytes");
            Refuses(() => J1Definition(0), "positive integer");
            Refuses(() => J1Definition(state: StateDefinition.Define("test.keyed", new() { Version = 1, Keyed = true, Schema = Obj(), Initial = () => new() })), "singleton");
            Refuses(() => J1Definition(state: J1State(), persist: new(JobResume.Rerun)), "persisted");
            Refuses(() => JobDefinition.Define("test", new() { Version = 1, Args = Obj(), Result = Bytes(), Persist = new(JobResume.Rerun) }), "no bytes");
            Refuses(() => J1Definition(accepts: new Dictionary<long, JobAdapter> { [1] = new(x => x, x => x) }), "older version");
        }),
        Case("jobs.definition.fingerprint-normal-form", () =>
        {
            var first = J1Definition(); var annotated = Obj(("n", Int())); annotated["description"] = "ignored annotation";
            Equal("af60f04c59e02db7", first.Fingerprint, "pinned v1.5.0 hash64 corpus input");
            var second = JobDefinition.Define("different.name", new() { Version = 9, Args = annotated, Result = Int(), Concurrency = 3 });
            Equal(first.Fingerprint, second.Fingerprint, "name version annotations concurrency omitted from fingerprint");
            var normal = new JsonObject { ["args"] = ContractsInternal.SnapshotArguments(first.Args, "args").Normal.DeepClone(),
                ["result"] = ContractsInternal.SnapshotSchema(first.Result, "result").Normal.DeepClone(), ["state"] = null };
            Equal(ContractsInternal.Hash64(ChordJson.Stringify(normal)), first.Fingerprint, "exact hash input order");
            Check(first.Fingerprint != J1Definition(state: J1State()).Fingerprint, "state fingerprint included");
        }),
    ];
    private static IEnumerable<(string, Func<Task>)> JobsJobCases() =>
    [
        Case("jobs.job.same-transaction-validation-dedupe-concurrency", async () =>
        {
            var gate = J1Signal(); var effects = 0;
            var f = await J1Host(J1Definition(1), _ => new() { Run = async (args, _) => { effects++; await gate.Task; return args!["n"]!.GetValue<int>(); } });
            await using var host = f.Host;
            IJobRun? first = null;
            await f.Self.TxAsync(tx =>
            {
                first = tx.Start(f.Job, new JsonObject { ["n"] = 1 }, new("one"));
                Equal(first, tx.Start(f.Job, new JsonObject { ["n"] = 99 }, new("one")), "same tx dedupe");
                StoreRefused(() => tx.Start(f.Job, new JsonObject { ["n"] = "bad" }, new("one")), "invalid", "args");
                StoreRefused(() => tx.Start(f.Job, new JsonObject { ["n"] = 2 }), "unavailable", "busy");
                Equal(0, effects, "effects wait for commit"); return Task.FromResult(true);
            }, ChordContext.Background);
            await J1Until(() => effects == 1); gate.TrySetResult();
            var result = await first!.OutcomeAsync(ChordContext.Background);
            Equal(1, (int)result.Result!, "dedupe keeps first arguments");
            Equal(AbortResult.Terminal, await f.Job.AbortAsync(first.Id, ChordContext.Background), "retired run terminal");
        }),
        Case("jobs.job.discard-has-no-effect", async () =>
        {
            var effects = 0; var f = await J1Host(J1Definition(), _ => new() { Run = (_, _) => { effects++; return Task.FromResult<object?>(1); } });
            await using var host = f.Host; IJobRun? leaked = null;
            try { await f.Self.TxAsync<bool>(tx => { leaked = tx.Start(f.Job, new JsonObject { ["n"] = 1 }); throw new Exception("discard"); }, ChordContext.Background); }
            catch (Exception error) when (error.Message == "discard") { }
            Equal(0, effects, "discarded transaction never invokes");
            var rejection = await J1Rejection(leaked!.OutcomeAsync(ChordContext.Background));
            Equal("discarded", ((ChordError)rejection).Reason, "leaked handle rejected");
        }),
        Case("jobs.job.finish-chains-same-key-at-limit", async () =>
        {
            IJobHandle? job = null; IJobRun? successor = null; ISingletonHandle? log = null; var values = new List<int>();
            var f = await J1Host(J1Definition(1), self =>
            {
                log = self.State(J1State());
                return new()
                {
                    Run = (args, _) => { var n = args!["n"]!.GetValue<int>(); values.Add(n); return Task.FromResult<object?>(n); },
                    Finish = async (outcome, tx) =>
                    {
                        var draft = await tx.DraftAsync(log); draft["value"] = draft["value"]!.GetValue<int>() + 1;
                        if ((int)outcome.Result! == 1) successor = tx.Start(job!, new JsonObject { ["n"] = 2 }, new("key"));
                    },
                };
            });
            await using var host = f.Host; job = f.Job;
            var first = await job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background, new("key"));
            await first.OutcomeAsync(ChordContext.Background); await successor!.OutcomeAsync(ChordContext.Background);
            Sequence([1, 2], values, "finish excludes itself from dedupe and concurrency");
            Equal(2, log!.Value!["value"]!.GetValue<int>(), "finish state writes commit with successor and retirement");
        }),
        Case("jobs.job.abort-can-flush-final-state", async () =>
        {
            var entered = J1Signal(); var f = await J1Host(J1Definition(state: J1State()), _ => new()
            {
                Run = async (_, ctx) =>
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ctx.AbortSignal); } catch (OperationCanceledException) { }
                    await ctx.State!.ChangeAsync(draft => draft["value"] = 7, ctx.Context);
                    throw new OperationCanceledException();
                },
            });
            await using var host = f.Host;
            var run = await f.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background); await entered.Task;
            Equal(AbortResult.Marked, await f.Job.AbortAsync(run.Id, ChordContext.Background), "abort waits for run");
            var outcome = await run.OutcomeAsync(ChordContext.Background);
            Equal("aborted", outcome.Error!.Kind, "aborted failure"); Equal(7, outcome.State!["value"]!.GetValue<int>(), "final progress survives abort");
        }),
        Case("jobs.job.finish-retries-blocks-and-abort-drops", async () =>
        {
            var clock = new J1RetryClock(); var calls = 0;
            var f = await J1Host(J1Definition(), _ => new() { Run = (_, _) => Task.FromResult<object?>(1),
                Finish = (_, _) => { calls++; throw new Exception("finish failed"); } }, time: clock);
            await using var host = f.Host;
            var run = await f.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background);
            for (var n = 1; n <= 5; n++)
            {
                await J1Until(() => calls >= n, $"finish attempt {n}");
                if (n == 5) break;
                var delay = TimeSpan.FromMilliseconds(1000 * Math.Pow(2, n - 1));
                // Host readiness/drain deadlines also use this provider. Wait for the specific backoff
                // registration rather than allowing an unrelated timer to advance the clock too early.
                await J1Until(() => clock.Scheduled(delay), $"retry backoff {delay.TotalMilliseconds} ms");
                clock.Advance(delay);
            }
            await J1Until(() => f.Job.Running.Value![0]!["phase"]!.GetValue<string>() == "blocked");
            Equal(5, f.Errors.Count(x => x.Kind == "job_finish_failed"), "five finish reports");
            Equal(AbortResult.Marked, await f.Job.AbortAsync(run.Id, ChordContext.Background), "blocked drop");
            Equal("aborted", ((ChordError)await J1Rejection(run.OutcomeAsync(ChordContext.Background))).Code, "drop rejects waiter");
        }),
        Case("jobs.job.run-state-fenced-at-settlement", async () =>
        {
            RunState? state = null; var f = await J1Host(J1Definition(state: J1State()), _ => new() { Run = (_, ctx) => { state = ctx.State; return Task.FromResult<object?>(1); } });
            await using var host = f.Host; var run = await f.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background);
            await run.OutcomeAsync(ChordContext.Background);
            Equal("released", ((ChordError)await J1Rejection(state!.ChangeAsync(d => d["value"] = 1, ChordContext.Background))).Code, "state fenced");
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> JobsPersistenceCases() =>
    [
        Case("jobs.persistence.reserve-defer-clean-reopen", async () =>
        {
            await using var store = await MemoryStore(); var entered = J1Signal(); string? id = null;
            var def = J1Definition(state: J1State(true), persist: new(JobResume.Rerun));
            var first = await J1Host(def, _ => new() { Run = async (_, ctx) =>
            {
                var record = await new Persistence(store, "test.jobs", "chord.jobs", def.Name).ReadAsync(ctx.JobId, ChordContext.Background);
                Equal("chord.job-run.1", record!.Record.Fingerprint, "record header");
                Equal("running", record.Value!["phase"]!.GetValue<string>(), "durably reserved before effect");
                await ctx.State!.ChangeAsync(d => d["value"] = 8, ctx.Context, CommitPersistence.Deferred(double.PositiveInfinity));
                entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ctx.AbortSignal); } catch (OperationCanceledException) { }
                return 0;
            } }, store);
            var run = await first.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background); id = run.Id; await entered.Task;
            await first.Host.CloseAsync(ChordContext.Background);
            var stopped = await new Persistence(store, "test.jobs", "chord.jobs", def.Name).ReadAsync(id, ChordContext.Background);
            Equal("stopped", stopped!.Value!["phase"]!.GetValue<string>(), "clean stop stored");
            var secondEntered = J1Signal(); var finish = J1Signal(); var resumedValue = -1;
            var second = await J1Host(def, _ => new() { Run = async (_, ctx) => { resumedValue = ctx.State!.Value["value"]!.GetValue<int>(); secondEntered.TrySetResult(); await finish.Task; return resumedValue; } }, store);
            await using var host = second.Host; await secondEntered.Task;
            var outcome = second.Job.OutcomeAsync(id, ChordContext.Background); finish.TrySetResult();
            Equal(8, (int)(await outcome).Result!, "held state flushed and restored");
            Check(await new Persistence(store, "test.jobs", "chord.jobs", def.Name).ReadAsync(id, ChordContext.Background) is null, "settled record retired");
        }),
        Case("jobs.persistence.restart-limit-without-effect", async () =>
        {
            await using var store = await MemoryStore(); var definition = J1Definition(persist: new(JobResume.Rerun));
            var run = new JobRun("crashed", definition.Version, definition.Fingerprint, null, new JsonObject { ["n"] = 3 }, "running") { Restarts = 4 };
            var record = PersistedRuns.Encode(run); var writes = new List<IStagedWrite>
            { StoreInternal.CreateDocument(new("test.jobs", "chord.jobs", definition.Name, run.Id), new(1, "chord.job-run.1", record), ChordJson.Stringify(record), _ => { }) };
            await store.CommitAsync(writes, new(() => true, (_, _) => { }), ChordContext.Background);
            var effects = 0; JobOutcome? finished = null;
            var f = await J1Host(definition, _ => new() { Run = (_, _) => { effects++; return Task.FromResult<object?>(1); }, Finish = (o, _) => { finished = o; return Task.CompletedTask; } }, store);
            await using var host = f.Host; await J1Until(() => finished is not null);
            Equal(0, effects, "fifth interruption does not invoke"); Equal("restart_limit", finished!.Error!.Kind, "restart outcome"); Equal(5, finished.Error.Restarts, "interruption count");
        }),
        Case("jobs.persistence.incompatible-version-blocked-and-abort-retired", async () =>
        {
            await using var store = await MemoryStore(); var old = J1Definition(persist: new(JobResume.Rerun));
            var run = new JobRun("old", old.Version, "incompatible", null, new JsonObject { ["n"] = 3 }, "pending"); var record = PersistedRuns.Encode(run);
            await store.CommitAsync([StoreInternal.CreateDocument(new("test.jobs", "chord.jobs", old.Name, run.Id), new(1, "chord.job-run.1", record), ChordJson.Stringify(record), _ => { })],
                new(() => true, (_, _) => { }), ChordContext.Background);
            var effects = 0; var f = await J1Host(old, _ => new() { Run = (_, _) => { effects++; return Task.FromResult<object?>(1); } }, store);
            await using var host = f.Host; Equal(0, effects, "mismatched fingerprint not invoked");
            Equal("blocked", f.Job.Running.Value![0]!["phase"]!.GetValue<string>(), "version block");
            Equal(AbortResult.Marked, await f.Job.AbortAsync(run.Id, ChordContext.Background), "blocked retirement");
            Check(await new Persistence(store, "test.jobs", "chord.jobs", old.Name).ReadAsync(run.Id, ChordContext.Background) is null, "blocked record retired");
        }),
        Case("jobs.persistence.abandon-and-version-adaptation", async () =>
        {
            await using (var store = await MemoryStore())
            {
                var definition = J1Definition(persist: new(JobResume.Abandon));
                await J1Seed(store, definition, new("abandoned", 1, definition.Fingerprint, null, new JsonObject { ["n"] = 2 }, "pending"));
                var effects = 0; JobOutcome? finished = null;
                var f = await J1Host(definition, _ => new() { Run = (_, _) => { effects++; return Task.FromResult<object?>(1); },
                    Finish = (o, _) => { finished = o; return Task.CompletedTask; } }, store);
                await using var host = f.Host; await J1Until(() => finished is not null);
                Equal(0, effects, "abandon does not rerun"); Equal("restart", finished!.Error!.Reason, "abandon reason");
            }
            await using (var store = await MemoryStore())
            {
                var old = J1Definition(persist: new(JobResume.Rerun));
                await J1Seed(store, old, new("adapted", 1, old.Fingerprint, null, new JsonObject { ["n"] = 2 }, "pending"));
                var current = J1Definition(version: 2, persist: new(JobResume.Rerun), accepts: new Dictionary<long, JobAdapter>
                { [1] = new(args => new JsonObject { ["n"] = args!["n"]!.GetValue<int>() + 10 }, result => result) });
                JobOutcome? finished = null;
                var f = await J1Host(current, _ => new() { Run = (args, _) => Task.FromResult<object?>(args!["n"]!.GetValue<int>()),
                    Finish = (o, _) => { finished = o; return Task.CompletedTask; } }, store);
                await using var host = f.Host; await J1Until(() => finished is not null);
                Equal(12, (int)finished!.Result!, "old args mapped for current run"); Equal(12, finished.Args!["n"]!.GetValue<int>(), "finish sees mapped args");
            }
        }),
        Case("jobs.persistence.refused-reservation-has-no-effect", async () =>
        {
            await using var store = await MemoryStore(); var definition = J1Definition(persist: new(JobResume.Rerun));
            var effects = 0; var f = await J1Host(definition, _ => new() { Run = (_, _) => { effects++; return Task.FromResult<object?>(1); } }, store);
            await using var host = f.Host; var commits = 0;
            store.CommitFences = fence => ++commits == 2 ? new CommitFence(() => false, fence.Adopt) : fence;
            var run = await f.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background);
            await J1Until(() => f.Errors.Any(e => e.Kind == "job_record_failed")); store.CommitFences = null;
            Equal(0, effects, "reservation must commit before invocation");
            var record = await new Persistence(store, "test.jobs", "chord.jobs", definition.Name).ReadAsync(run.Id, ChordContext.Background);
            Equal("pending", record!.Value!["phase"]!.GetValue<string>(), "refused reservation leaves durable pending intent");
        }),
    ];

    // .NET-specific regression: user cancellation registrations may throw. They cannot break the job's
    // lifecycle bookkeeping or prevent the host from finishing its remaining teardown.
    private static IEnumerable<(string, Func<Task>)> JobsCancellationCases() =>
    [
        Case("jobs.cancellation.abort-reports-callback-failure-and-settles", async () =>
        {
            var entered = J1Signal();
            var f = await J1Host(J1Definition(), _ => new()
            {
                Run = async (_, ctx) =>
                {
                    using var registration = ctx.AbortSignal.Register(() => throw new InvalidOperationException("abort registration failed"));
                    entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ctx.AbortSignal); return 1;
                },
            });
            await using var host = f.Host;
            var run = await f.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background); await entered.Task;
            Equal(AbortResult.Marked, await f.Job.AbortAsync(run.Id, ChordContext.Background), "throwing callback does not reject abort");
            var outcome = await run.OutcomeAsync(ChordContext.Background);
            Equal("aborted", outcome.Error!.Kind, "run still settles as aborted");
            var reported = f.Errors.Single(e => e.Kind == "cancellation_failed" && e.Job == "test.work");
            Equal(run.Id, reported.Id, "failure identifies the run");
            Check(reported.Error!.ToString().Contains("abort registration failed", StringComparison.Ordinal), "original callback failure retained");
            Equal(0, host.Activity.Value.Jobs, "abort still releases job activity");
        }),
        Case("jobs.cancellation.reload-reports-callback-failure-and-restarts", async () =>
        {
            var entered = J1Signal(); var closed = 0; var definition = J1Definition();
            var f = await J1Host(definition, self =>
            {
                self.Open(_ => Task.FromResult(1), (_, _, _) => { closed++; return Task.CompletedTask; });
                return new()
                {
                    Run = async (_, ctx) =>
                    {
                        using var registration = ctx.AbortSignal.Register(() => throw new InvalidOperationException("reload registration failed"));
                        entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ctx.AbortSignal); return 1;
                    },
                };
            });
            await using var host = f.Host;
            var run = await f.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background); await entered.Task;
            var finished = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            IJobHandle? current = null;
            var type = Contract.Local("test.jobs", new() { Version = 1, Params = new(), Members = new() });
            var replacement = new PluginFacet("jobs", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                self.Open(_ => Task.FromResult(2), (_, _, _) => Task.CompletedTask);
                current = self.Job(definition, new()
                {
                    Run = (_, _) => Task.FromResult<object?>(2),
                    Finish = (outcome, _) => { finished.TrySetResult(outcome); return Task.CompletedTask; },
                });
                return new(new Dictionary<string, object?>());
            })));
            var reload = await host.ReloadAsync([replacement], ChordContext.Background);
            Equal("restarted", reload.Objects.Single().Fate, "native resource forces restart");
            var outcome = await finished.Task; await run.OutcomeAsync(ChordContext.Background);
            Equal("reload", outcome.Error!.Reason, "cut outcome preserved");
            Equal(1, closed, "old native still closes");
            Check(f.Errors.Any(e => e.Kind == "cancellation_failed" && e.Id == run.Id), "reload callback failure reported");
            var next = await current!.StartAsync(new JsonObject { ["n"] = 2 }, ChordContext.Background);
            Equal(2, (int)(await next.OutcomeAsync(ChordContext.Background)).Result!, "replacement remains operational");
        }),
        Case("jobs.cancellation.close-reports-callback-failure-and-releases-store", async () =>
        {
            await using var store = await MemoryStore(); var entered = J1Signal(); var closed = 0;
            var definition = J1Definition(persist: new(JobResume.Rerun));
            var f = await J1Host(definition, self =>
            {
                self.Open(_ => Task.FromResult(1), (_, _, _) => { closed++; return Task.CompletedTask; });
                return new()
                {
                    Run = async (_, ctx) =>
                    {
                        using var registration = ctx.AbortSignal.Register(() => throw new InvalidOperationException("close registration failed"));
                        entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ctx.AbortSignal); return 1;
                    },
                };
            }, store);
            await using var host = f.Host;
            var run = await f.Job.StartAsync(new JsonObject { ["n"] = 1 }, ChordContext.Background); await entered.Task;
            await host.CloseAsync(ChordContext.Background);
            var rejected = (ChordError)await J1Rejection(run.OutcomeAsync(ChordContext.Background));
            Equal("closed", rejected.Reason, "waiter receives original close reason");
            Equal(0, host.Activity.Value.Jobs, "close clears job activity");
            Equal(1, closed, "remaining native teardown completes");
            Check(f.Errors.Any(e => e.Kind == "cancellation_failed" && e.Id == run.Id), "close callback failure reported");
            var record = await new Persistence(store, "test.jobs", "chord.jobs", definition.Name).ReadAsync(run.Id, ChordContext.Background);
            Equal("stopped", record!.Value!["phase"]!.GetValue<string>(), "close writer still persists stop");
            // A second host can attach to the retained store only after the first detached it.
            await using var replacement = Hosts.Create(new() { Store = store, OnError = _ => { } });
        }),
    ];
}
