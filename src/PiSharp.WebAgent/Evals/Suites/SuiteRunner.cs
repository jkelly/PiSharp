// Decision 0011 (run W5, E4): a suite run (mockups evals-run, evals-suite's "Run now"; brief rule 4). A run asks every case on every
// target k times through the run engine (source drift, its group the suite run's id, so History and the Spend Guard's run key group it),
// checks each answer with E2's CheckRunner (judge calls are guarded and recorded too, under the same group), and when it is complete,
// analyses each target against its accepted baseline with E3's DriftAnalyzer and stores the signals.
//
// One group reservation per suite run. Before anything is sent, the runner reserves the worst case of every call the run will make (each
// answer, and each judge call) as one all-or-nothing Spend Guard group under the run's key ("hold-<run>-<n>" ops, source drift): "Spend
// Guard reserved the worst case first. Results fill in as answers finish." When the guard refuses the group, nothing is sent and no run
// is stored (a manual run answers with the confirm modal's spend refusal; a scheduled one records a skipped slot). The run engine reserves
// each call itself as it sends it, so the runner releases a call's hold just before the engine reserves it, and a judge's holds just before
// the answer is checked; the run's total reservation never grows past the group's. Leftover holds (a cancelled or failed run) are released
// at the end, and after a crash at the next start (recovery). The suite-run cap is checked before reserving: a run whose worst case is
// above it is refused, manual or scheduled (it never runs over the cap).
//
// Answers run four at a time; a result goes queued → running → done or error (cancelled when the run stops first). The account of each
// target is resolved once when the run starts and pinned on every call, so a run never switches accounts halfway. The app stopping marks
// a running run interrupted (its finished results stay, its unfinished ones are cancelled, its holds released); a run left active by a
// crash is marked interrupted when the app starts again, and its slot is never run again (the slot row stays).
using System.Collections.Concurrent;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Store;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Suites;

/// <summary>What starts a suite run.</summary>
public sealed record SuiteRunRequest
{
    /// <summary>The run id to use (<see cref="EvalIds.Run"/>), so a retried "Run now" finds the run it started; null for a new one.</summary>
    public string? RunId { get; init; }

    /// <summary>The cases to run (a "Run this case"), in any order, or null for every case of the suite.</summary>
    public IReadOnlyList<string>? CaseIds { get; init; }

    /// <summary>What started it.</summary>
    public SuiteRunTrigger Trigger { get; init; } = SuiteRunTrigger.Manual;

    /// <summary>The schedule slot, for a scheduled run.</summary>
    public DateTimeOffset? Slot { get; init; }

    /// <summary>The user's answer to the Spend Guard (proceed once, or cap the output), for a manual run the guard asked about.</summary>
    public RunSpendAnswer? Spend { get; init; }
}

/// <summary>Why a suite run did not start (other than the Spend Guard).</summary>
/// <param name="Code"><c>running</c>, <c>over-cap</c>, <c>unknown-price</c>, <c>cannot-run</c> or <c>slot-taken</c>.</param>
/// <param name="Message">What the page says.</param>
public sealed record SuiteRunRefusal(string Code, string Message)
{
    /// <summary>A run of the suite is still going.</summary>
    public const string Running = "running";

    /// <summary>The slot already has a decision.</summary>
    public const string SlotTaken = "slot-taken";
}

/// <summary>What a suite run request did.</summary>
/// <param name="Posted">Whether a run started (or, for a retried request, had started).</param>
/// <param name="Run">The run.</param>
/// <param name="Refused">Why it did not start, other than the Spend Guard.</param>
/// <param name="Spend">The Spend Guard's refusal of the run's reservation (nothing was sent).</param>
public sealed record SuiteRunStart(bool Posted, SuiteRun? Run, SuiteRunRefusal? Refused, RunRefusal? Spend);

/// <summary>Runs suites. A singleton.</summary>
public sealed class SuiteRunner : IAsyncDisposable
{
    /// <summary>How many answers of one run are asked at once.</summary>
    public const int Concurrency = 4;

    /// <summary>The prefix of a run's hold ops: <c>hold-&lt;run&gt;-&lt;n&gt;</c>.</summary>
    public const string HoldPrefix = "hold-";

    /// <summary>What an interrupted run says.</summary>
    public const string InterruptedProblem = "The app stopped while this run was going. Its finished answers stay; the rest were not asked, and it was not run again.";

    private readonly EvalStore store;
    private readonly RunEngine engine;
    private readonly RunStore runs;
    private readonly CheckRunner checks;
    private readonly ISuiteCostEstimator estimator;
    private readonly IServiceProvider services;
    private readonly PriceWatch? watch;
    private readonly TimeProvider time;
    private readonly ILogger log;
    private readonly ConcurrentDictionary<string, Execution> running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim startGate = new(1, 1);
    private int recovered;
    private int disposed;

    /// <summary>A runner over the app's services.</summary>
    public SuiteRunner(EvalStore store, RunEngine engine, RunStore runs, CheckRunner checks, ISuiteCostEstimator estimator, IServiceProvider services,
        PriceWatch? watch = null, TimeProvider? time = null, ILogger<SuiteRunner>? log = null, IHostApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(services);
        this.store = store;
        this.engine = engine;
        this.runs = runs;
        this.checks = checks;
        this.estimator = estimator;
        this.services = services;
        this.watch = watch;
        this.time = time ?? TimeProvider.System;
        this.log = (ILogger?)log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        lifetime?.ApplicationStopping.Register(() => StopAll(interrupted: true));
    }

    /// <summary>Raised after a run ends (complete, cancelled, failed or interrupted) and its drift is stored. A handler must not throw.</summary>
    public event Action<SuiteRun>? Finished;

    /// <summary>Whether the run <paramref name="runId"/> is going in this process.</summary>
    public bool IsRunning(string runId) => running.ContainsKey(runId);

    /// <summary>A task that completes when the run <paramref name="runId"/> has ended and its drift is stored, or null when it is not going
    /// here (for tests and shutdown).</summary>
    public Task? Completion(string runId) => running.TryGetValue(runId, out var execution) ? execution.Done.Task : null;

    /// <summary>Cancels the run <paramref name="runId"/>: its calls in flight are cancelled, nothing more is asked, its finished answers stay.</summary>
    /// <returns>Whether it was going here.</returns>
    public bool Cancel(string runId)
    {
        if (!running.TryGetValue(runId, out var execution)) return false;
        execution.UserCancelled = true;
        try { execution.Cancel.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>Starts a run of the suite <paramref name="suiteId"/>: checked, its worst case reserved as one group, then asked in the
    /// background.</summary>
    /// <exception cref="KeyNotFoundException">There is no such suite.</exception>
    /// <exception cref="ArgumentException">A case id is not the suite's, the run id is not valid, or the spend answer is not allowed.</exception>
    public async Task<SuiteRunStart> StartAsync(string suiteId, SuiteRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartLockedAsync(suiteId, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            startGate.Release();
        }
    }

    private async Task<SuiteRunStart> StartLockedAsync(string suiteId, SuiteRunRequest request, CancellationToken cancellationToken)
    {
        var suite = store.Suite(suiteId) ?? throw new KeyNotFoundException($"There is no suite {suiteId}.");
        var runId = request.RunId is null ? EvalIds.New(EvalIds.Run) : EvalIds.Require(EvalIds.Run, request.RunId, "suite run");
        if (store.Run(runId) is { } existing)
            return existing.SuiteId == suite.Id ? new SuiteRunStart(true, existing, null, null) : throw new ArgumentException("That run id is another suite's.");
        var all = store.Cases(suite.Id);
        var cases = all;
        if (request.CaseIds is { } wanted)
        {
            var set = new HashSet<string>(wanted, StringComparer.Ordinal);
            foreach (var id in set)
                if (all.All(testCase => testCase.Id != id)) throw new ArgumentException($"The test case {id} is not in this suite.");
            cases = [.. all.Where(testCase => set.Contains(testCase.Id))];
        }
        if (suite.RunProblem(cases.Count) is { } problem) return Refused(ScheduleRefusal.CannotRun, problem);
        var draft = SuiteRun.Start(suite, [.. cases.Select(testCase => testCase.Id)], 1, request.Trigger, request.Slot, time.GetUtcNow()) with { Id = runId };
        // Checked again, in the same transaction, when the run is inserted; checking here first reserves nothing for a decided slot.
        if (request.Slot is { } slot && store.Slot(suite.Id, slot) is not null) return Refused(SuiteRunRefusal.SlotTaken, "This schedule slot already has a decision.");
        if (store.ActiveRun(suite.Id) is { } active) return Refused(SuiteRunRefusal.Running, $"Run #{active.Number} of this suite is still going.");
        var estimate = estimator.Estimate(suite, cases);
        if (request.Trigger == SuiteRunTrigger.Schedule && estimate.HasUnknownPrice)
            return Refused(ScheduleRefusal.UnknownPrice, "A target or judge has no known price, and a scheduled run cannot ask.");
        if (suite.CapMicros is { } cap && !estimate.Fits(cap)) return Refused(ScheduleRefusal.OverCap, OverCapMessage(estimate.WorstMicros, cap));

        // Plan every call, with each target's account pinned.
        var items = new List<WorkItem>();
        var calls = new List<SpendCall>();
        var targets = new List<(SuiteTarget Target, string? Account)>();
        foreach (var target in suite.Targets)
        {
            try { targets.Add((target, await AccountAsync(target.Provider, target.Model, target.Account, cancellationToken).ConfigureAwait(false))); }
            catch (ArgumentException error) { return Refused(ScheduleRefusal.CannotRun, $"{target.Provider}/{target.Model}: {error.Message}"); }
        }
        var judgeAccounts = new Dictionary<(string, string, string?), string?>();
        foreach (var testCase in cases)
        {
            var judges = CheckRunner.JudgeChecks(testCase);
            foreach (var (target, account) in targets)
            {
                RunRequestPlan plan;
                try
                {
                    plan = engine.Plan(target.Apply(testCase.Spec) with { Account = account, GroupId = runId, Title = testCase.Name, ParentId = null });
                    if (!plan.Allowed) throw new RunOptionException(plan);
                }
                catch (ArgumentException error)
                {
                    return Refused(ScheduleRefusal.CannotRun, $"“{testCase.Name}” on {target.Provider}/{target.Model}: {error.Message}");
                }
                for (var repetition = 0; repetition < suite.K; repetition++)
                {
                    var hold = calls.Count;
                    calls.Add(Hold(runId, calls.Count, plan.Model.Provider, plan.Model.Id, account, CostEstimator.InputBytes(plan.Messages), plan.Spec.MaxTokens));
                    var judgeHolds = new List<int>();
                    foreach (var check in judges)
                    {
                        var judge = check.Judge!;
                        var key = (judge.Provider, judge.Model, judge.Account);
                        if (!judgeAccounts.TryGetValue(key, out var judgeAccount))
                        {
                            try { judgeAccount = await AccountAsync(judge.Provider, judge.Model, judge.Account, cancellationToken).ConfigureAwait(false); }
                            catch (ArgumentException) { judgeAccount = judge.Account; }
                            judgeAccounts[key] = judgeAccount;
                        }
                        judgeHolds.Add(calls.Count);
                        calls.Add(Hold(runId, calls.Count, judge.Provider, judge.Model, judgeAccount, RubricJudge.MaxInputBytes, JudgeSpec.MaxOutputTokens));
                    }
                    items.Add(new WorkItem(testCase, target, repetition, plan.Spec, hold, judgeHolds));
                }
            }
        }

        // One group reservation for the whole run.
        var executor = services.GetRequiredService<GuardedRunExecutor>();
        if (request.Spend is { } answer)
            await executor.Guard.ApproveStoredAsync(new SpendApproval(runId, answer.Choice) { MaxOutputTokens = answer.MaxOutputTokens }.Normalize(), cancellationToken)
                .ConfigureAwait(false);
        var group = await executor.ReserveGroupAsync(calls, cancellationToken).ConfigureAwait(false);
        if (!group.Reserved)
        {
            await group.DisposeAsync().ConfigureAwait(false);
            var refusing = group.Reservation.Items.FirstOrDefault(item => !item.Reserved);
            var decision = group.Reservation.Refused ?? refusing?.GroupDecision ?? refusing?.Decision ?? group.Leases[0].Decision;
            return new SuiteRunStart(false, null, null, new RunRefusal(decision.ErrorCode ?? SpendErrors.Ask, SpendErrors.Describe(decision), refusing?.Call ?? calls[0], decision));
        }
        var reserved = group.Reservation.Items.Sum(item => item.Reservation.Reservation is { Billing: BillingKind.Metered, ReservedMicros: { } micros } ? micros : 0);
        if (suite.CapMicros is { } runCap && reserved > runCap)
        {
            await group.DisposeAsync().ConfigureAwait(false);
            return Refused(ScheduleRefusal.OverCap, OverCapMessage(reserved, runCap));
        }

        var now = time.GetUtcNow();
        var run = draft with { Status = SuiteRunStatus.Running, GroupId = runId, ReservedMicros = reserved, Created = now, Started = now };
        var queued = items.Select(item => Queued(runId, item)).ToList();
        SuiteRun? inserted;
        try
        {
            inserted = store.InsertRun(run, queued, now);
        }
        catch
        {
            await group.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (inserted is null)
        {
            await group.DisposeAsync().ConfigureAwait(false);
            return Refused(SuiteRunRefusal.SlotTaken, "This schedule slot already has a decision.");
        }
        var execution = new Execution(inserted, suite, items, group, request.Spend, cases.Count == all.Count);
        running[runId] = execution;
        log.LogInformation("Suite {Suite} run #{Number} started: {Answers} answers, {Reserved} reserved.", suite.Name, inserted.Number, items.Count,
            DriftText.Money(reserved));
        execution.Task = Task.Run(() => ExecuteAsync(execution), CancellationToken.None);
        return new SuiteRunStart(true, inserted, null, null);
    }

    /// <summary>Marks every run left queued or running by an earlier process interrupted (its unfinished results cancelled, its holds
    /// released). Runs once per process; later calls do nothing.</summary>
    /// <returns>How many runs it marked.</returns>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref recovered, 1) != 0) return 0;
        var count = 0;
        foreach (var run in store.ActiveRuns())
        {
            if (running.ContainsKey(run.Id)) continue;
            var now = time.GetUtcNow();
            store.CancelUnfinished(run.Id, now);
            store.UpdateRun(Totals(run, store.Results(run.Id)) with { Status = SuiteRunStatus.Interrupted, Problem = InterruptedProblem, Finished = now });
            await ReleaseHoldsAsync(run.Id, cancellationToken).ConfigureAwait(false);
            log.LogWarning("Suite run {Run} (#{Number}) was going when the app stopped; it is marked interrupted.", run.Id, run.Number);
            count++;
        }
        return count;
    }

    /// <summary>Analyses the complete run <paramref name="runId"/> against each target's current baseline and stores the signals (what the
    /// runner does after every complete run of all its suite's cases; analysing a run again changes nothing).</summary>
    public IReadOnlyList<DriftAnalysis> AnalyzeDrift(string runId) =>
        store.Run(runId) is { Status: SuiteRunStatus.Complete } run ? AnalyzeDrift(run) : [];

    /// <summary>Analyses <paramref name="run"/>, complete with its results stored, and stores the signals. The runner calls it before it
    /// stores the run as complete, so a page that sees the run end sees its signals too.</summary>
    private List<DriftAnalysis> AnalyzeDrift(SuiteRun run)
    {
        var results = store.Results(run.Id);
        var names = store.Cases(run.SuiteId).ToDictionary(testCase => testCase.Id, testCase => testCase.Name, StringComparer.Ordinal);
        var analyses = new List<DriftAnalysis>();
        var now = time.GetUtcNow();
        foreach (var target in run.Targets)
        {
            if (store.CurrentBaseline(run.SuiteId, target.Id) is not { } baseline || baseline.RunId == run.Id) continue;
            if (store.Run(baseline.RunId) is not { } baselineRun) continue;
            var input = new DriftInput(run, results, baseline, baselineRun, store.Results(baselineRun.Id))
            {
                Open = store.Signals(run.SuiteId, target.Id),
                CaseNames = names,
                BaselinePrice = Price(target, baselineRun.Started ?? baselineRun.Created),
                CurrentPrice = Price(target, run.Started ?? run.Created),
            };
            var analysis = DriftAnalyzer.Analyze(input, now);
            if (!analysis.AlreadyAnalysed && (analysis.Raised.Count > 0 || analysis.Closed.Count > 0)) store.ApplyDrift(analysis.Raised, analysis.Closed);
            analyses.Add(analysis);
        }
        return analyses;
    }

    /// <summary>Stops every run (they end interrupted) and waits up to 10 seconds for them.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        StopAll(interrupted: true);
        var tasks = running.Values.Select(execution => execution.Done.Task).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { log.LogWarning("Suite runs were still ending when the app stopped."); }
        startGate.Dispose();
    }

    // ----- execution -----

    private async Task ExecuteAsync(Execution execution)
    {
        var token = execution.Cancel.Token;
        try
        {
            await Parallel.ForEachAsync(execution.Items, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = token },
                async (item, itemToken) => await AnswerAsync(execution, item, itemToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            execution.Failure = error.Message;
            log.LogError(error, "Suite run {Run} failed.", execution.Run.Id);
        }
        SuiteRun? finished = null;
        try
        {
            await execution.Group.DisposeAsync().ConfigureAwait(false);
            finished = Finish(execution);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.LogError(error, "Suite run {Run} could not be finished.", execution.Run.Id);
        }
        finally
        {
            running.TryRemove(execution.Run.Id, out _);
            execution.Cancel.Dispose();
            if (finished is not null)
            {
                try { Finished?.Invoke(finished); }
                catch (Exception error) when (error is not OutOfMemoryException) { log.LogWarning(error, "A suite run handler failed."); }
            }
            execution.Done.TrySetResult();
        }
    }

    private async Task AnswerAsync(Execution execution, WorkItem item, CancellationToken token)
    {
        var lease = execution.Group.Leases[item.Hold];
        await lease.ReleaseAsync("Sent as its own call: the run engine reserves it.").ConfigureAwait(false);
        var result = Queued(execution.Run.Id, item);
        if (token.IsCancellationRequested) return;
        store.SaveResult(result with { Status = ResultStatus.Running });
        RunStart start;
        try
        {
            start = await engine.StartAsync(item.Spec, execution.Spend, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            store.SaveResult(result with { Status = ResultStatus.Cancelled, Finished = time.GetUtcNow() });
            return;
        }
        catch (ArgumentException error)
        {
            await ReleaseJudgeHoldsAsync(execution, item).ConfigureAwait(false);
            Save(execution, Failed(result, error.Message));
            return;
        }
        if (!start.Posted || start.Id is not { } callId)
        {
            await ReleaseJudgeHoldsAsync(execution, item).ConfigureAwait(false);
            Save(execution, Failed(result, start.Refusal is { } refusal ? $"{refusal.Message} Nothing was sent." : "The call did not start."));
            return;
        }
        store.SaveResult(result with { Status = ResultStatus.Running, CallRunId = callId });
        using (token.Register(() => engine.Cancel(callId)))
        {
            if (engine.Completion(callId) is { } completion)
            {
                try { await completion.ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { log.LogDebug(error, "Call {Call} ended with an error.", callId); }
            }
        }
        await ReleaseJudgeHoldsAsync(execution, item).ConfigureAwait(false);
        var record = runs.Get(callId);
        if (record is null)
        {
            Save(execution, Failed(result with { CallRunId = callId }, "The call's record is missing."));
            return;
        }
        var answer = EvalAnswer.FromRun(record);
        var stopped = token.IsCancellationRequested;
        var status = record.Status == RunStatus.Completed ? ResultStatus.Done
            : stopped && record.Status is RunStatus.Aborted or RunStatus.Interrupted ? ResultStatus.Cancelled : ResultStatus.Error;
        var callError = status == ResultStatus.Error ? answer.Error ?? record.Error ?? "The call failed." : null;
        var finished = result with
        {
            Status = status,
            CallRunId = callId,
            // Sent unless the Spend Guard stopped the call before it went out (its error starts with the guard's prefix and says nothing
            // was sent); Drift Watch leaves out errors that never reached the target (fix W5 drift 1).
            Sent = callError?.StartsWith(SpendGuard.ErrorPrefix, StringComparison.Ordinal) != true,
            Text = SuiteResult.Cut(answer.Text),
            Fingerprint = status == ResultStatus.Done ? AnswerFingerprint.Of(answer.Text) : null,
            ResponseModel = record.ResponseModel ?? (record.Output is null ? null : answer.ResponseModel),
            StopReason = record.StopReason,
            InputTokens = record.Usage.Input,
            OutputTokens = record.Usage.Output,
            CostMicros = record.CostMicros,
            ListPrice = answer.ListPrice,
            DurationMs = record.DurationMs,
            TtftMs = record.TtftMs,
            TokensPerSecond = record.TokensPerSecond,
            Error = callError,
            Finished = time.GetUtcNow(),
        };
        if (status == ResultStatus.Cancelled)
        {
            Save(execution, finished);
            return;
        }
        IReadOnlyList<CheckResult> results;
        if (stopped) results = NotJudged(await checks.RunAsync(item.Case, answer, null, CancellationToken.None).ConfigureAwait(false));
        else
        {
            try
            {
                results = await checks.RunAsync(item.Case, answer, new JudgeContext
                {
                    Target = item.Target.Identity,
                    GroupId = execution.Run.Id,
                    Spend = execution.Spend,
                    ParentRunId = callId,
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                results = NotJudged(await checks.RunAsync(item.Case, answer, null, CancellationToken.None).ConfigureAwait(false));
            }
        }
        long judgeMetered = 0, judgeList = 0;
        foreach (var check in results)
        {
            if (check.JudgeRunId is null || runs.Get(check.JudgeRunId) is not { CostMicros: { } cost } judged) continue;
            if (string.Equals(judged.Billing, "subscription", StringComparison.Ordinal)) judgeList += cost;
            else if (!string.Equals(judged.Billing, "free", StringComparison.Ordinal)) judgeMetered += cost;
        }
        lock (execution.Gate)
        {
            execution.JudgeMetered += judgeMetered;
            execution.JudgeList += judgeList;
        }
        Save(execution, finished.WithChecks(results));
    }

    private void Save(Execution execution, SuiteResult result)
    {
        store.SaveResult(result);
        lock (execution.Gate)
        {
            if (!result.ListPrice) execution.AnswerMetered += result.CostMicros ?? 0;
            else execution.AnswerList += result.CostMicros ?? 0;
            execution.Run = execution.Run with
            {
                CostMicros = execution.AnswerMetered + execution.JudgeMetered,
                ListMicros = execution.AnswerList + execution.JudgeList,
            };
            store.UpdateRun(execution.Run);
        }
    }

    private SuiteRun Finish(Execution execution)
    {
        var now = time.GetUtcNow();
        store.CancelUnfinished(execution.Run.Id, now);
        var results = store.Results(execution.Run.Id);
        SuiteRun run;
        lock (execution.Gate) run = execution.Run;
        var finished = results.Where(result => result.IsFinished).ToList();
        (SuiteRunStatus Status, string? Problem) end = execution switch
        {
            { Interrupted: true } => (SuiteRunStatus.Interrupted, InterruptedProblem),
            { UserCancelled: true } => (SuiteRunStatus.Cancelled, "Cancelled. Its finished answers stay."),
            { Failure: { } failure } => (SuiteRunStatus.Failed, "The run stopped: " + failure),
            _ when finished.Count > 0 && finished.All(result => result.Status == ResultStatus.Error) =>
                (SuiteRunStatus.Failed, "No target could be asked: " + finished[0].Error),
            _ => (SuiteRunStatus.Complete, null),
        };
        var done = run with { Status = end.Status, Problem = end.Problem, Finished = now };
        // Drift first (W5 integration): the signals are stored before the run reads as complete, so the run and suite pages never show a
        // finished run without them. A failed analysis is logged and the run still ends.
        if (done.Status == SuiteRunStatus.Complete && execution.Full)
        {
            try { AnalyzeDrift(done); }
            catch (Exception error) when (error is not OutOfMemoryException) { log.LogError(error, "Suite run {Run}'s drift analysis failed.", done.Id); }
        }
        store.UpdateRun(done);
        log.LogInformation("Suite run {Run} (#{Number}) ended {Status}.", done.Id, done.Number, EvalJson.Wire(done.Status));
        return done;
    }

    // ----- helpers -----

    private void StopAll(bool interrupted)
    {
        foreach (var execution in running.Values)
        {
            if (interrupted) execution.Interrupted = true;
            try { execution.Cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task ReleaseJudgeHoldsAsync(Execution execution, WorkItem item)
    {
        foreach (var hold in item.JudgeHolds)
            await execution.Group.Leases[hold].ReleaseAsync("Sent as its own call: the judge reserves it.").ConfigureAwait(false);
    }

    private async Task ReleaseHoldsAsync(string runId, CancellationToken cancellationToken)
    {
        var ledger = services.GetService<ISpendLedger>();
        if (ledger is null) return;
        var prefix = HoldPrefix + runId + "-";
        var rows = new List<Reservation>();
        for (var offset = 0; ; offset += SpendLedgerQuery.MaxLimit)
        {
            var page = await ledger.QueryAsync(new SpendLedgerQuery { Run = runId, Limit = SpendLedgerQuery.MaxLimit, Offset = offset }, cancellationToken)
                .ConfigureAwait(false);
            rows.AddRange(page.Items);
            if (page.Items.Count < SpendLedgerQuery.MaxLimit) break;
        }
        foreach (var row in rows.Where(row => row.Op.StartsWith(prefix, StringComparison.Ordinal)))
        {
            try
            {
                if (row.State == ReservationState.Reserved)
                    await ledger.ReleaseAsync(row.Op, "The suite run stopped before this call was sent.", cancellationToken).ConfigureAwait(false);
                else if (row.State == ReservationState.Uncertain)
                    await ledger.ResolveAsync(row.Op, new SpendResolution(ResolveChoice.Release), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                log.LogWarning(error, "The hold {Op} of suite run {Run} could not be released.", row.Op, runId);
            }
        }
    }

    private async Task<string?> AccountAsync(string provider, string model, string? account, CancellationToken cancellationToken)
    {
        if (engine.Models.GetModel(provider, model) is { Api: RunModelOptions.FauxApi }) return null;
        var accounts = services.GetService<IChatAccounts>();
        if (account is { } id)
        {
            if (accounts is null) return id;
            return (await accounts.FindAsync(provider, id, cancellationToken).ConfigureAwait(false))?.Id
                ?? throw new ArgumentException($"There is no {provider} account {id}; choose another in Settings › Accounts.");
        }
        return accounts is null ? null : (await accounts.BindAsync(provider, cancellationToken).ConfigureAwait(false))?.Id;
    }

    private CatalogCost? Price(SuiteTarget target, DateTimeOffset at)
    {
        try { return watch?.PriceAt(target.Provider, target.Model, at)?.Cost; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static SpendCall Hold(string runId, int index, string provider, string model, string? account, long inputBytes, int? maxTokens) => new()
    {
        Provider = provider,
        Model = model,
        Account = account,
        Source = SpendSource.Drift,
        Run = runId,
        Op = HoldPrefix + runId + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        InputBytes = inputBytes,
        MaxTokens = maxTokens,
        Note = "Suite run hold: released as the call is sent.",
    };

    private static SuiteResult Queued(string runId, WorkItem item) => new()
    {
        RunId = runId,
        CaseId = item.Case.Id,
        TargetId = item.Target.Id,
        Repetition = item.Repetition,
    };

    private SuiteResult Failed(SuiteResult result, string error) =>
        (result with { Status = ResultStatus.Error, Error = error, Finished = time.GetUtcNow() }).WithChecks([]);

    private static IReadOnlyList<CheckResult> NotJudged(IReadOnlyList<CheckResult> results) =>
        [.. results.Select(result => result.Kind == CheckKind.Judge ? result with { Verdict = CheckVerdict.Error, Detail = "Not judged: the run was cancelled." } : result)];

    private static SuiteRun Totals(SuiteRun run, IReadOnlyList<SuiteResult> results) => run with
    {
        CostMicros = Math.Max(run.CostMicros, results.Where(result => !result.ListPrice).Sum(result => result.CostMicros ?? 0)),
        ListMicros = Math.Max(run.ListMicros, results.Where(result => result.ListPrice).Sum(result => result.CostMicros ?? 0)),
    };

    private static string OverCapMessage(long worst, long cap) =>
        $"The worst case, {DriftText.Money(worst)}, is above the {DriftText.Money(cap)} suite-run cap. Raise the cap, lower k, or run fewer cases.";

    private static SuiteRunStart Refused(string code, string message) => new(false, null, new SuiteRunRefusal(code, message), null);

    private sealed record WorkItem(TestCase Case, SuiteTarget Target, int Repetition, RunSpec Spec, int Hold, IReadOnlyList<int> JudgeHolds);

    private sealed class Execution(SuiteRun run, Suite suite, IReadOnlyList<WorkItem> items, GuardedLeaseGroup group, RunSpendAnswer? spend, bool full)
    {
        public Lock Gate { get; } = new();
        public SuiteRun Run { get; set; } = run;
        public Suite Suite { get; } = suite;
        public IReadOnlyList<WorkItem> Items { get; } = items;
        public GuardedLeaseGroup Group { get; } = group;
        public RunSpendAnswer? Spend { get; } = spend;
        public bool Full { get; } = full;
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Task { get; set; }
        public volatile bool Interrupted;
        public volatile bool UserCancelled;
        public string? Failure { get; set; }
        public long JudgeMetered { get; set; }
        public long JudgeList { get; set; }
        public long AnswerMetered { get; set; }
        public long AnswerList { get; set; }
    }
}
