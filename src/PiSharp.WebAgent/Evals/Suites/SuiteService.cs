// Decision 0011 (run W5, E4): what the suite pages and the API change about suites and test cases (mockups evals-suites, evals-suite,
// modal-suite-schedule, modal-baseline, modal-test-case's "add to a suite"), on EvalStore.
//
//   - Suites and cases are created idempotently: a create that carries the id of a row that exists returns that row unchanged, so a retried
//     POST never makes a second one. Cases are checked as E2's CheckRunner saves them (a judge's rubric validated as a judgment policy).
//   - A schedule is turned on only with a suite-run cap that its worst case fits (SuiteEstimate.Fits): scheduled runs cannot ask, so the cap
//     blocks. An unknown price, a worst case over the cap or a suite that cannot run is refused with the modal's words ("The worst case,
//     $1.62, is above the $1.50 cap. Raise the cap to at least $1.62, lower k, or set a lower maxTokens on the cases."); nothing is saved
//     then. Saving it sets EnabledAt to now, so a slot that had already passed never fires; turning it off keeps its settings.
//   - The typical cost is the median metered cost of the suite's last 20 complete runs (null before there is one).
//   - A schedule that stops fitting is paused, never run over its cap: a price change pauses it (EvalScheduler, with Price Watch's words), and
//     so does an edit (a case or target added, k raised, the cap lowered) here. A paused schedule runs nothing until an edit makes it fit
//     again, which resumes it from now (missed slots never fire).
//   - Accepting a baseline makes a complete run the current baseline of the chosen targets (the old one is kept, Replaced set), records
//     each target's response models in that run, closes their open signals (DriftAnalyzer.Clear), and optionally replaces each case's
//     reference output with that run's answer (the first chosen target's first repeat).
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Store;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals.Suites;

/// <summary>Why a schedule (or a run) was refused.</summary>
/// <param name="Code"><c>over-cap</c>, <c>unknown-price</c>, <c>no-cap</c> or <c>cannot-run</c>.</param>
/// <param name="Message">What the page says.</param>
/// <param name="WorstMicros">The worst case, when it was worked out.</param>
/// <param name="CapMicros">The cap it was compared with, or null.</param>
public sealed record ScheduleRefusal(string Code, string Message, long? WorstMicros = null, long? CapMicros = null)
{
    /// <summary>The worst case is above the suite-run cap.</summary>
    public const string OverCap = "over-cap";

    /// <summary>A target or judge has no known price.</summary>
    public const string UnknownPrice = "unknown-price";

    /// <summary>No suite-run cap was given.</summary>
    public const string NoCap = "no-cap";

    /// <summary>The suite cannot run (no case or no target).</summary>
    public const string CannotRun = "cannot-run";
}

/// <summary>What saving a schedule did.</summary>
/// <param name="Saved">Whether it was saved.</param>
/// <param name="Suite">The suite as it is now.</param>
/// <param name="Estimate">Its worst and typical cost.</param>
/// <param name="Next">The next slot, when the schedule is on.</param>
/// <param name="Refused">Why it was refused, when it was.</param>
public sealed record ScheduleSave(bool Saved, Suite Suite, SuiteEstimate Estimate, DateTimeOffset? Next, ScheduleRefusal? Refused);

/// <summary>What accepting a baseline did.</summary>
/// <param name="Accepted">The new current baselines (targets whose baseline already was that run are left out).</param>
/// <param name="Cleared">The open signals it closed.</param>
/// <param name="ReferencesReplaced">How many cases got that run's answer as their reference output.</param>
public sealed record BaselineAccept(IReadOnlyList<Baseline> Accepted, IReadOnlyList<DriftSignal> Cleared, int ReferencesReplaced);

/// <summary>A suite as the suites list shows it (mockup evals-suites _SuiteCard).</summary>
/// <param name="Suite">The suite.</param>
/// <param name="Cases">Its case count.</param>
/// <param name="Estimate">Its worst and typical cost per run.</param>
/// <param name="Next">Its next slot, when its schedule is active.</param>
/// <param name="LastRun">Its newest run, or null.</param>
/// <param name="ActiveRun">Its run still going, or null.</param>
/// <param name="Baselines">Its current baselines.</param>
/// <param name="Signals">Its open drift signals.</param>
public sealed record SuiteOverview(Suite Suite, int Cases, SuiteEstimate Estimate, DateTimeOffset? Next, SuiteRun? LastRun, SuiteRun? ActiveRun,
    IReadOnlyList<Baseline> Baselines, IReadOnlyList<DriftSignal> Signals)
{
    /// <summary>The worst open signal's level (ok when none).</summary>
    public DriftLevel Level => DriftRules.Worst(Signals);

    /// <summary>Answers per run.</summary>
    public int AnswersPerRun => Suite.AnswersPerRun(Cases);
}

/// <summary>Suites, cases, schedules and baselines. A singleton; safe from many threads.</summary>
public sealed class SuiteService
{
    /// <summary>How many complete runs the typical cost is the median of.</summary>
    public const int TypicalRuns = 20;

    private readonly EvalStore store;
    private readonly CheckRunner checks;
    private readonly ISuiteCostEstimator estimator;
    private readonly RunStore runs;
    private readonly PriceWatch? watch;
    private readonly TimeProvider time;
    private readonly ILogger? log;
    private readonly Lock gate = new();

    /// <summary>The service over <paramref name="store"/>.</summary>
    public SuiteService(EvalStore store, CheckRunner checks, ISuiteCostEstimator estimator, RunStore runs, PriceWatch? watch = null, TimeProvider? time = null,
        ILogger<SuiteService>? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(runs);
        this.store = store;
        this.checks = checks;
        this.estimator = estimator;
        this.runs = runs;
        this.watch = watch;
        this.time = time ?? TimeProvider.System;
        this.log = log;
    }

    /// <summary>The store.</summary>
    public EvalStore Store => store;

    // ----- suites -----

    /// <summary>Creates <paramref name="suite"/> (normalized; no schedule: a schedule is turned on with <see cref="SetSchedule"/>), or returns
    /// the suite that already has its id.</summary>
    /// <returns>The suite, and whether it was made now.</returns>
    /// <exception cref="ArgumentException">The suite is not valid.</exception>
    public (Suite Suite, bool Created) CreateSuite(Suite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        var now = time.GetUtcNow();
        var normalized = (suite with { Schedule = suite.Schedule is null ? null : suite.Schedule with { Enabled = false, Paused = null }, Created = now, Updated = now })
            .Normalize();
        lock (gate)
        {
            if (store.TryInsertSuite(normalized)) return (normalized, true);
            return (store.Suite(normalized.Id) ?? throw new InvalidOperationException("The suite was not saved."), false);
        }
    }

    /// <summary>Changes the suite <paramref name="id"/> with <paramref name="change"/> (its id, schedule and creation time are kept), then
    /// pauses or resumes its schedule when the change makes its worst case stop or start fitting its cap.</summary>
    /// <exception cref="KeyNotFoundException">There is no such suite.</exception>
    /// <exception cref="ArgumentException">The change is not valid.</exception>
    public Suite UpdateSuite(string id, Func<Suite, Suite> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            var current = RequireSuite(id);
            var now = time.GetUtcNow();
            var changed = change(current) with { Id = current.Id, Schedule = current.Schedule, Created = current.Created, Updated = now };
            var saved = Reconcile(changed.Normalize(), store.Cases(current.Id), now);
            store.UpdateSuite(saved);
            return saved;
        }
    }

    /// <summary>Deletes the suite <paramref name="id"/> with its cases, runs, results, baselines and signals.</summary>
    /// <exception cref="InvalidOperationException">A run of it is still going.</exception>
    public bool DeleteSuite(string id)
    {
        lock (gate)
        {
            if (store.ActiveRun(id) is { } active) throw new InvalidOperationException($"Run #{active.Number} of this suite is still going: cancel it first.");
            return store.DeleteSuite(id);
        }
    }

    /// <summary>The suite <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">There is none.</exception>
    public Suite RequireSuite(string id) =>
        store.Suite(id) ?? throw new KeyNotFoundException($"There is no suite {id}.");

    // ----- cases -----

    /// <summary>Creates <paramref name="testCase"/> last in its suite (validated as CheckRunner saves it), or returns the case that already
    /// has its id.</summary>
    /// <exception cref="KeyNotFoundException">Its suite does not exist.</exception>
    /// <exception cref="ArgumentException">The case is not valid, or its id is another suite's case.</exception>
    public (TestCase Case, bool Created) CreateCase(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var now = time.GetUtcNow();
        var normalized = checks.Validate(testCase with { Created = now, Updated = now });
        lock (gate)
        {
            if (store.Case(normalized.Id) is { } existing)
                return existing.SuiteId == normalized.SuiteId ? (existing, false) : throw new ArgumentException("That test case id belongs to another suite.");
            var suite = RequireSuite(normalized.SuiteId);
            store.TryInsertCase(normalized);
            ReconcileAndSave(suite, now);
            return (normalized, true);
        }
    }

    /// <summary>Changes the case <paramref name="id"/> with <paramref name="change"/> (its id, suite, origin and creation time are kept).</summary>
    /// <exception cref="KeyNotFoundException">There is no such case.</exception>
    /// <exception cref="ArgumentException">The change is not valid.</exception>
    public TestCase UpdateCase(string id, Func<TestCase, TestCase> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            var current = store.Case(id) ?? throw new KeyNotFoundException($"There is no test case {id}.");
            var now = time.GetUtcNow();
            var changed = checks.Validate(change(current) with
            {
                Id = current.Id, SuiteId = current.SuiteId, Origin = current.Origin, Created = current.Created, Updated = now,
            });
            store.UpdateCase(changed);
            if (store.Suite(current.SuiteId) is { } suite) ReconcileAndSave(suite, now);
            return changed;
        }
    }

    /// <summary>Removes the case <paramref name="id"/> from its suite (its results in earlier runs stay).</summary>
    public bool DeleteCase(string id)
    {
        lock (gate)
        {
            var current = store.Case(id);
            if (current is null || !store.DeleteCase(id)) return false;
            if (store.Suite(current.SuiteId) is { } suite) ReconcileAndSave(suite, time.GetUtcNow());
            return true;
        }
    }

    /// <summary>Moves the case <paramref name="id"/> to <paramref name="position"/> (0 first) in its suite.</summary>
    public bool MoveCase(string id, int position) => store.MoveCase(id, position);

    // ----- estimates -----

    /// <summary>The worst case of one run of <paramref name="suite"/> with <paramref name="cases"/>, with the typical cost of its last
    /// <see cref="TypicalRuns"/> complete runs.</summary>
    public SuiteEstimate Estimate(Suite suite, IReadOnlyList<TestCase> cases)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(cases);
        return estimator.Estimate(suite, cases) with { TypicalMicros = Typical(suite.Id) };
    }

    /// <summary>The median metered cost of the last <see cref="TypicalRuns"/> complete runs of <paramref name="suiteId"/>, or null.</summary>
    public long? Typical(string suiteId) =>
        TargetSummary.Median(store.RecentCosts(suiteId, TypicalRuns).Select(cost => (double)cost)) is { } median ? (long)Math.Round(median) : null;

    /// <summary>The suite's estimate now and, with <paramref name="extra"/>, after adding that case (modal-test-case's over-cap pane).</summary>
    public (SuiteEstimate Before, SuiteEstimate? After) EstimateWith(Suite suite, TestCase? extra)
    {
        ArgumentNullException.ThrowIfNull(suite);
        var cases = store.Cases(suite.Id);
        var before = Estimate(suite, cases);
        if (extra is null) return (before, null);
        var normalized = checks.Validate(extra with { SuiteId = suite.Id });
        return (before, Estimate(suite, [.. cases.Where(testCase => testCase.Id != normalized.Id), normalized]));
    }

    /// <summary>Why the active schedule of <paramref name="suite"/> must pause now (an unknown price, or a worst case above its cap), with
    /// Price Watch's words (a price rise in its last check is named), or null when it fits.</summary>
    public SchedulePause? PauseFor(Suite suite, IReadOnlyList<TestCase> cases, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(cases);
        if (suite.Schedule is not { Enabled: true } schedule) return null;
        var active = suite with { Schedule = schedule with { Paused = null } };
        if (watch is not null)
        {
            try { return watch.SchedulesToPause([new SuiteCases(active, cases)], now).FirstOrDefault()?.Pause; }
            catch (InvalidOperationException) { }
        }
        var estimate = estimator.Estimate(active, cases);
        if (estimate.HasUnknownPrice)
        {
            var unknown = UnknownModels(estimate);
            return new SchedulePause(SchedulePause.UnknownPrice, $"{DriftText.List(unknown)} {(unknown.Count == 1 ? "has" : "have")} no known price, and a scheduled run cannot ask.", now);
        }
        return suite.CapMicros is { } cap && !estimate.Fits(cap)
            ? new SchedulePause(SchedulePause.OverCap, $"Its worst case of {DriftText.Money(estimate.WorstMicros)} is above its {DriftText.Money(cap)} suite-run cap.", now)
            : null;
    }

    /// <summary>Pauses the active schedule of the suite <paramref name="suiteId"/> with <paramref name="pause"/>.</summary>
    /// <returns>The suite as saved, or null when it has no active schedule (nothing changed).</returns>
    public Suite? Pause(string suiteId, SchedulePause pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        lock (gate)
        {
            if (store.Suite(suiteId) is not { Schedule: { Active: true } schedule } suite) return null;
            var paused = suite with { Schedule = schedule with { Paused = pause }, Updated = time.GetUtcNow() };
            store.UpdateSuite(paused);
            log?.LogInformation("Paused the schedule of suite {Suite}: {Reason}", suite.Name, pause.Reason);
            return paused;
        }
    }

    // ----- schedule -----

    /// <summary>Turns the schedule of the suite <paramref name="suiteId"/> on with <paramref name="schedule"/> and the suite-run cap
    /// <paramref name="capMicros"/> (null keeps the suite's), or off when <paramref name="schedule"/> is null or not enabled.</summary>
    /// <exception cref="KeyNotFoundException">There is no such suite.</exception>
    /// <exception cref="ArgumentException">The schedule or cap is not valid.</exception>
    public ScheduleSave SetSchedule(string suiteId, SuiteSchedule? schedule, long? capMicros)
    {
        lock (gate)
        {
            var suite = RequireSuite(suiteId);
            var cases = store.Cases(suite.Id);
            var now = time.GetUtcNow();
            if (schedule is not { Enabled: true })
            {
                var off = suite with
                {
                    Schedule = (schedule ?? suite.Schedule) is { } kept ? kept with { Enabled = false, Paused = null } : null,
                    CapMicros = capMicros ?? suite.CapMicros,
                    Updated = now,
                };
                off = off.Normalize();
                store.UpdateSuite(off);
                return new ScheduleSave(true, off, Estimate(off, cases), null, null);
            }
            var cap = capMicros ?? suite.CapMicros;
            var candidate = (suite with { Schedule = schedule.Normalize() with { Enabled = false, Paused = null }, CapMicros = cap }).Normalize();
            var estimate = Estimate(candidate, cases);
            if (Refusal(candidate, cases.Count, estimate, cap) is { } refused) return new ScheduleSave(false, suite, estimate, null, refused);
            var on = candidate with { Schedule = candidate.Schedule! with { Enabled = true, EnabledAt = now, Paused = null }, Updated = now };
            on = on.Normalize();
            store.UpdateSuite(on);
            log?.LogInformation("Suite {Suite} runs {Schedule} with a {Cap} suite-run cap.", on.Name, on.Schedule!.Label, DriftText.Money(cap!.Value));
            return new ScheduleSave(true, on, estimate, on.Schedule!.Next(now), null);
        }
    }

    /// <summary>Why a schedule of <paramref name="suite"/> with <paramref name="cases"/> cases cannot be turned on with the cap
    /// <paramref name="capMicros"/>, or null when it can.</summary>
    public static ScheduleRefusal? Refusal(Suite suite, int cases, SuiteEstimate estimate, long? capMicros)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(estimate);
        if (suite.RunProblem(cases) is { } problem) return new ScheduleRefusal(ScheduleRefusal.CannotRun, problem);
        if (capMicros is not { } cap)
            return new ScheduleRefusal(ScheduleRefusal.NoCap, "A schedule needs a suite-run cap: scheduled runs can't ask, so the cap blocks.", estimate.WorstMicros);
        if (estimate.HasUnknownPrice)
        {
            var unknown = UnknownModels(estimate);
            return new ScheduleRefusal(ScheduleRefusal.UnknownPrice,
                $"{DriftText.List(unknown)} {(unknown.Count == 1 ? "has" : "have")} no known price, and a scheduled run cannot ask. Enter a price in Settings › Spend first.",
                null, cap);
        }
        if (estimate.Fits(cap)) return null;
        var atLeast = (estimate.WorstMicros + 9_999) / 10_000 * 10_000;
        return new ScheduleRefusal(ScheduleRefusal.OverCap,
            $"The worst case, {DriftText.Money(estimate.WorstMicros)}, is above the {DriftText.Money(cap)} cap. Raise the cap to at least {DriftText.Money(atLeast)}, " +
            "lower k, or set a lower maxTokens on the cases.", estimate.WorstMicros, cap);
    }

    // ----- baselines -----

    /// <summary>Makes the complete run <paramref name="runId"/> the baseline of <paramref name="targetIds"/> (every target of the run when
    /// null) and clears their open signals; with <paramref name="replaceReferences"/>, each case's reference output becomes that run's answer.</summary>
    /// <exception cref="KeyNotFoundException">There is no such suite or run.</exception>
    /// <exception cref="ArgumentException">The run is not a complete run of the suite, or a target is not one of its targets.</exception>
    public BaselineAccept AcceptBaseline(string suiteId, string runId, IReadOnlyList<string>? targetIds, bool replaceReferences)
    {
        lock (gate)
        {
            var suite = RequireSuite(suiteId);
            var run = store.Run(runId) ?? throw new KeyNotFoundException($"There is no suite run {runId}.");
            if (run.SuiteId != suite.Id) throw new ArgumentException("That run is another suite's.");
            if (run.Status != SuiteRunStatus.Complete) throw new ArgumentException("Only a complete run can be a baseline.");
            var chosen = new List<SuiteTarget>();
            foreach (var id in targetIds is { Count: > 0 } ? targetIds.Distinct(StringComparer.Ordinal) : run.Targets.Select(target => target.Id))
                chosen.Add(run.Target(id) ?? throw new ArgumentException($"The run has no target {id}."));
            var now = time.GetUtcNow();
            var results = store.Results(run.Id);
            var answers = run.CaseIds.Count * run.K;
            var accepted = new List<Baseline>();
            var cleared = new List<DriftSignal>();
            foreach (var target in chosen)
            {
                if (store.CurrentBaseline(suite.Id, target.Id) is { } current && current.RunId == run.Id && !replaceReferences) continue;
                accepted.Add(new Baseline
                {
                    SuiteId = suite.Id,
                    TargetId = target.Id,
                    RunId = run.Id,
                    Identity = target.Identity,
                    ResponseModels = TargetSummary.Of(target.Id, answers, results).ResponseModels,
                    ReplacedReferences = replaceReferences,
                    Accepted = now,
                });
                cleared.AddRange(DriftAnalyzer.Clear(store.Signals(suite.Id, target.Id), run.Id, now));
            }
            store.AcceptBaselines(accepted, cleared);
            var replaced = replaceReferences ? ReplaceReferences(run, chosen, results, now) : 0;
            return new BaselineAccept(accepted, cleared, replaced);
        }
    }

    private int ReplaceReferences(SuiteRun run, IReadOnlyList<SuiteTarget> chosen, IReadOnlyList<SuiteResult> results, DateTimeOffset now)
    {
        var replaced = 0;
        foreach (var caseId in run.CaseIds)
        {
            if (store.Case(caseId) is not { } testCase) continue;
            var answer = chosen.Select(target => results.Where(result => result.CaseId == caseId && result.TargetId == target.Id && result.Status == ResultStatus.Done)
                    .OrderBy(result => result.Repetition).FirstOrDefault())
                .FirstOrDefault(result => result?.CallRunId is not null);
            if (answer is null || runs.Get(answer.CallRunId!) is not { } record) continue;
            try
            {
                store.UpdateCase(testCase with { Reference = TestCaseReference.FromRun(record).Normalize(), Updated = now });
                replaced++;
            }
            catch (ArgumentException error)
            {
                log?.LogWarning("The reference output of case {Case} was not replaced: {Reason}", testCase.Name, error.Message);
            }
        }
        return replaced;
    }

    // ----- overviews -----

    /// <summary>The suite <paramref name="suite"/> as the suites list shows it.</summary>
    public SuiteOverview Overview(Suite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        var cases = store.Cases(suite.Id);
        var now = time.GetUtcNow();
        DateTimeOffset? next = null;
        if (suite.Schedule is { Active: true } schedule)
        {
            try { next = schedule.Next(now); }
            catch (InvalidOperationException) { }
        }
        return new SuiteOverview(suite, cases.Count, Estimate(suite, cases), next, store.Runs(suite.Id, 1).FirstOrDefault(), store.ActiveRun(suite.Id),
            store.Baselines(suite.Id), store.Signals(suite.Id));
    }

    // ----- helpers -----

    /// <summary><paramref name="suite"/> with its schedule paused when it no longer fits, or resumed from <paramref name="now"/> when a paused
    /// one fits again; unchanged otherwise.</summary>
    private Suite Reconcile(Suite suite, IReadOnlyList<TestCase> cases, DateTimeOffset now)
    {
        if (suite.Schedule is not { Enabled: true } schedule) return suite;
        var pause = PauseFor(suite, cases, now);
        if (schedule.Paused is null && pause is not null)
        {
            log?.LogInformation("Paused the schedule of suite {Suite}: {Reason}", suite.Name, pause.Reason);
            return suite with { Schedule = schedule with { Paused = pause } };
        }
        if (schedule.Paused is not null && pause is null && suite.RunProblem(cases.Count) is null)
            return suite with { Schedule = schedule with { Paused = null, EnabledAt = now } };
        return suite;
    }

    private void ReconcileAndSave(Suite suite, DateTimeOffset now)
    {
        var reconciled = Reconcile(suite, store.Cases(suite.Id), now);
        if (!ReferenceEquals(reconciled, suite)) store.UpdateSuite(reconciled with { Updated = now });
    }

    private static List<string> UnknownModels(SuiteEstimate estimate) =>
        [.. estimate.Lines.Where(line => line.Billing == BillingKind.Unknown).Select(line => line.Provider + "/" + line.Model).Distinct(StringComparer.Ordinal)];
}
