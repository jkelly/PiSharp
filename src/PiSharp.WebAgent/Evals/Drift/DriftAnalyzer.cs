// Decision 0011 (run W5, E3): Drift Watch's rules, the approved plan's table exactly (DriftRules.InitialLevel says each rule's level):
//
//   broken case        a case passed all k repeats in the baseline and fails all k now                         suspect
//   pass rate          exact one-sided sign test over cases, p < 0.05, and the pass rate fell by 0.10 or more   suspect
//   judge score        the same test on cases' mean judge scores, and the mean fell by 0.5 points or more       suspect
//   latency            the median moved ±50% and lies outside the baseline's 10th–90th percentile range         info
//   tokens/s           the same, on output tokens per second                                                  info
//   cost               the cost per run moved ±25%, split into the price change and verbosity                   suspect
//   response model     the target answered as a model the baseline never saw (responseModel)                   drift
//   answer fingerprint most compared cases answered outside the baseline's own SimHash spread                 suspect
//
// Which answers count (fix W5 drift 1): the pass-rate, broken-case and judge-score rules count only answers that say something about the
// model (Decisive): an answer whose checks decided (passed or failed), or a call that was sent and failed (a provider error fails). A call
// that never reached the target (the Spend Guard refused it, it did not start, or its record is missing: SuiteResult.Sent false) and an
// answer the checks could not decide (a judge that was refused, failed, or was unsure and sent it to review) are missing answers, as if the
// case had fewer repeats: they never make a case worse, never break a case, and a judge score counts only from a judge that decided. A
// lost race for a hold, or a cap reached halfway through a run, is therefore never drift.
//
// Every rule compares the cases both runs asked, on one target, against that target's accepted baseline run. A suspect signal becomes drift
// only when the next analysed run of the suite raises the same rule for the same target again (DriftSignal.Confirm; for a broken case, the
// same case must be broken again); otherwise that run closes it as not confirmed. A response model change is drift at once, and the next
// run that sees it again carries it on with the run that saw it first ("Run #20 saw it first and run #21 saw it again"). Drift stays open
// until a new baseline clears it (Clear). Info never becomes suspect or drift: each run closes the last run's info signals and raises its
// own. Closed signals keep their row with Cleared set and Detail's "closedAs" (confirmed, seen-again, not-confirmed, superseded, baseline)
// and "closedBy" (the run or baseline that closed it), so the history stays readable.
//
// Choices the plan leaves open, all in DriftThresholds: the minimum effect sizes (0.10 of pass rate, 0.5 judge points), at least 3 samples
// per side before the speed and cost rules compare medians or means, at least 3 repeats per side for a broken case, at least 3 baseline
// answers for a case's fingerprint spread, and a floor of 3 bits on that spread. The last two were measured: with 16 cases, a flaky one
// (passing 60% of the time) passes 2 of 2 and fails 2 of 2 often enough that k 2 gave false drift in about 3% of stable trials, and a spread
// from a single pair of answers is too narrow; at k 3 a mixed suite gives false drift in about 0.55% of trials (6,000 seeded trials of a
// fresh baseline and two runs) and a fall from 0.9 to 0.5 is drift by the second run in about 97%.
// Analyze is a pure function of its input: the results' order never changes the outcome, and analysing a run again changes nothing
// (signals the run already raised are recognised). The suite runner (E4) analyses each complete run per target with a current baseline,
// stores Raised (insert) and Closed (update), and shows Open.
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>The thresholds Drift Watch uses beyond the plan's table (<see cref="DriftRules"/> holds the table's own).</summary>
public sealed record DriftThresholds
{
    /// <summary>The thresholds Drift Watch runs with.</summary>
    public static DriftThresholds Default { get; } = new();

    /// <summary>The sign test's significance level (<see cref="DriftRules.SignificanceLevel"/>).</summary>
    public double SignificanceLevel { get; init; } = DriftRules.SignificanceLevel;

    /// <summary>The least fall in pass rate (as a share of answers) that counts: 0.10.</summary>
    public double MinPassRateDrop { get; init; } = 0.10;

    /// <summary>The least fall in mean judge score (on its 1–5 scale) that counts: 0.5.</summary>
    public double MinJudgeScoreDrop { get; init; } = 0.5;

    /// <summary>The share a speed median must move (<see cref="DriftRules.SpeedChange"/>, ±50%).</summary>
    public double SpeedChange { get; init; } = DriftRules.SpeedChange;

    /// <summary>The share the cost per run must move (<see cref="DriftRules.CostChange"/>, ±25%).</summary>
    public double CostChange { get; init; } = DriftRules.CostChange;

    /// <summary>The fewest samples on each side before the latency, tokens/s and cost rules compare: 3.</summary>
    public int MinSamples { get; init; } = 3;

    /// <summary>The fewest repeats on each side for the broken-case rule: 3 (the default k; at k 2 a flaky case looks broken too often).</summary>
    public int MinBrokenCaseRepeats { get; init; } = 3;

    /// <summary>The fewest finished baseline answers of a case for its fingerprint spread: 3 (one pair of answers is too narrow a spread).</summary>
    public int MinFingerprintAnswers { get; init; } = 3;

    /// <summary>The smallest spread a case's fingerprints are allowed, in bits: 3 (identical baseline answers still allow small edits).</summary>
    public int MinFingerprintBits { get; init; } = 3;
}

/// <summary>What one analysis compares: one target's run against its accepted baseline run.</summary>
/// <param name="Run">The run analysed (complete).</param>
/// <param name="Results">Its results (other targets' results are ignored).</param>
/// <param name="Baseline">The target's current baseline (<see cref="Baseline.TargetId"/> is the target analysed).</param>
/// <param name="BaselineRun">The baseline's run.</param>
/// <param name="BaselineResults">The baseline run's results.</param>
public sealed record DriftInput(SuiteRun Run, IReadOnlyList<SuiteResult> Results, Baseline Baseline, SuiteRun BaselineRun, IReadOnlyList<SuiteResult> BaselineResults)
{
    /// <summary>The target's open signals from earlier runs of the suite (signals of other targets or suites, and closed ones, are ignored).</summary>
    public IReadOnlyList<DriftSignal> Open { get; init; } = [];

    /// <summary>Case names by id, for messages (a case without one is named by its id).</summary>
    public IReadOnlyDictionary<string, string>? CaseNames { get; init; }

    /// <summary>The target model's catalog price when the baseline ran (<see cref="PriceWatch.PriceAt"/>), or null when unknown.</summary>
    public CatalogCost? BaselinePrice { get; init; }

    /// <summary>The target model's catalog price when this run ran, or null when unknown. With both, a cost change is split into the price
    /// change and verbosity.</summary>
    public CatalogCost? CurrentPrice { get; init; }

    /// <summary>The target analysed.</summary>
    public string TargetId => Baseline.TargetId;
}

/// <summary>What one rule found in one run, before it is reconciled with the open signals.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Message">What the page says.</param>
/// <param name="PValue">The sign test's p-value, or null.</param>
/// <param name="Effect">The change as a signed share, or null.</param>
/// <param name="Detail">The numbers behind it.</param>
public sealed record DriftFinding(DriftRule Rule, string Message, double? PValue = null, double? Effect = null, JsonObject? Detail = null)
{
    /// <summary>The cases it concerns (a broken case's cases; the fingerprint's moved cases), or empty.</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];
}

/// <summary>The result of analysing one target's run: the signals to store and the target's level after them.</summary>
public sealed record DriftAnalysis
{
    /// <summary>The suite.</summary>
    public required string SuiteId { get; init; }

    /// <summary>The target.</summary>
    public required string TargetId { get; init; }

    /// <summary>The run analysed.</summary>
    public required string RunId { get; init; }

    /// <summary>The baseline run it was compared with.</summary>
    public required string BaselineRunId { get; init; }

    /// <summary>The numbers.</summary>
    public required DriftComparison Comparison { get; init; }

    /// <summary>What each rule found this run (in <see cref="DriftRules.All"/> order).</summary>
    public IReadOnlyList<DriftFinding> Findings { get; init; } = [];

    /// <summary>New signals to insert: new suspect, info and drift signals, and confirmed or seen-again drift.</summary>
    public IReadOnlyList<DriftSignal> Raised { get; init; } = [];

    /// <summary>Open signals this run closes (Cleared set, Detail's <c>closedAs</c> and <c>closedBy</c>), to update by id.</summary>
    public IReadOnlyList<DriftSignal> Closed { get; init; } = [];

    /// <summary>The target's open signals once <see cref="Raised"/> and <see cref="Closed"/> are applied.</summary>
    public IReadOnlyList<DriftSignal> Open { get; init; } = [];

    /// <summary>The target's level: the worst of <see cref="Open"/>, ok when none.</summary>
    public DriftLevel Level { get; init; }

    /// <summary>Whether the run had already been analysed (its signals were among the open ones), so nothing changed.</summary>
    public bool AlreadyAnalysed { get; init; }
}

/// <summary>Why a signal was closed (Detail's <c>closedAs</c>).</summary>
public static class DriftClosed
{
    /// <summary>The next run confirmed this suspect signal; a drift signal carries it on.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>A later run saw this drift again; a new drift signal carries it on.</summary>
    public const string SeenAgain = "seen-again";

    /// <summary>The next run did not raise this suspect signal's rule again.</summary>
    public const string NotConfirmed = "not-confirmed";

    /// <summary>A later run's measurement replaced it (info signals, and a duplicate of a rule).</summary>
    public const string Superseded = "superseded";

    /// <summary>A new baseline cleared it.</summary>
    public const string Baseline = "baseline";

    /// <summary>Detail's key for the reason.</summary>
    public const string ReasonKey = "closedAs";

    /// <summary>Detail's key for the run (or baseline run) that closed it.</summary>
    public const string ByKey = "closedBy";

    /// <summary>The reason <paramref name="signal"/> was closed, or null while it is open.</summary>
    public static string? Reason(DriftSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return signal.Cleared is null ? null : signal.Detail?[ReasonKey]?.GetValue<string>();
    }
}

/// <summary>Drift Watch's analysis (the plan's table). Pure and thread-safe.</summary>
public static class DriftAnalyzer
{
    /// <summary>The numbers of <paramref name="input"/>'s target against its baseline (no rule applied): what a drift row's "Other signals"
    /// shows.</summary>
    /// <exception cref="ArgumentException">The runs, baseline and results do not belong together.</exception>
    public static DriftComparison Compare(DriftInput input, DriftThresholds? thresholds = null)
    {
        Validate(input);
        return Measure(input, thresholds ?? DriftThresholds.Default);
    }

    /// <summary>Whether <paramref name="result"/> counts toward the pass-rate, broken-case and judge-score rules: an answer whose checks
    /// decided (passed or failed), or a call that was sent and failed (it fails). A call that never reached the target (not
    /// <see cref="SuiteResult.Sent"/>: refused by the Spend Guard, not started, its record missing), an answer under review or whose checks
    /// could not run (a judge refused or failed), and an unfinished or cancelled one do not count: they are missing answers.</summary>
    public static bool Decisive(SuiteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            ResultStatus.Done => result.Verdict is CheckVerdict.Passed or CheckVerdict.Failed,
            ResultStatus.Error => result.Sent,
            _ => false,
        };
    }

    /// <summary>Analyses one target's run against its baseline: what every rule finds, reconciled with the target's open signals (confirm,
    /// carry on, close), at <paramref name="now"/>.</summary>
    /// <exception cref="ArgumentException">The runs, baseline and results do not belong together.</exception>
    public static DriftAnalysis Analyze(DriftInput input, DateTimeOffset now, DriftThresholds? thresholds = null)
    {
        Validate(input);
        var limits = thresholds ?? DriftThresholds.Default;
        var suiteId = input.Run.SuiteId;
        var targetId = input.TargetId;
        var runId = input.Run.Id;
        var baselineRunId = input.BaselineRun.Id;
        var comparison = Measure(input, limits);
        var open = input.Open.Where(signal => signal.Cleared is null && signal.SuiteId == suiteId && signal.TargetId == targetId)
            .OrderBy(signal => signal.Created).ThenBy(signal => signal.Id).ToList();
        var analysis = new DriftAnalysis { SuiteId = suiteId, TargetId = targetId, RunId = runId, BaselineRunId = baselineRunId, Comparison = comparison };
        if (runId == baselineRunId) return analysis with { Open = open, Level = DriftRules.Worst(open) };
        var findings = Findings(input, comparison, limits);
        if (open.Any(signal => signal.RunId == runId))
            return analysis with { Findings = findings, Open = open, Level = DriftRules.Worst(open), AlreadyAnalysed = true };

        var byRule = findings.ToDictionary(finding => finding.Rule);
        var raised = new List<DriftSignal>();
        var closed = new List<ClosedSignal>();
        var carried = new HashSet<DriftRule>();
        DriftSignal New(DriftFinding finding) =>
            DriftSignal.Raise(suiteId, targetId, runId, baselineRunId, finding.Rule, finding.Message, now, finding.PValue, finding.Effect, Detail(finding));
        foreach (var signal in open)
        {
            var finding = byRule.GetValueOrDefault(signal.Rule);
            var matches = finding is not null && !carried.Contains(signal.Rule) && SameCases(signal, finding);
            switch (signal.Level)
            {
                case DriftLevel.Suspect when matches:
                    raised.Add(signal.Confirm(New(finding!)));
                    closed.Add(Close(signal, DriftClosed.Confirmed, runId, now));
                    carried.Add(signal.Rule);
                    break;
                case DriftLevel.Suspect:
                    closed.Add(Close(signal, carried.Contains(signal.Rule) ? DriftClosed.Superseded : DriftClosed.NotConfirmed, runId, now));
                    break;
                case DriftLevel.Drift when matches:
                    raised.Add(New(finding!) with { Level = DriftLevel.Drift, FirstRunId = signal.FirstRunId ?? signal.RunId });
                    closed.Add(Close(signal, DriftClosed.SeenAgain, runId, now));
                    carried.Add(signal.Rule);
                    break;
                case DriftLevel.Drift:
                    break; // drift stays until a new baseline clears it
                default:
                    closed.Add(Close(signal, DriftClosed.Superseded, runId, now));
                    break;
            }
        }
        foreach (var finding in findings)
            if (!carried.Contains(finding.Rule)) raised.Add(New(finding));
        var stillOpen = open.Where(signal => !closed.Any(done => ReferenceEquals(done.Original, signal))).Concat(raised).ToList();
        return analysis with
        {
            Findings = findings,
            Raised = raised,
            Closed = [.. closed.Select(done => done.Closed)],
            Open = stillOpen,
            Level = DriftRules.Worst(stillOpen),
        };
    }

    /// <summary><paramref name="open"/> closed by a new baseline (run <paramref name="baselineRunId"/>) at <paramref name="now"/>: "Setting a
    /// new one ... clears their open signals". Signals already closed are left out.</summary>
    public static IReadOnlyList<DriftSignal> Clear(IEnumerable<DriftSignal> open, string baselineRunId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(open);
        EvalIds.Require(EvalIds.Run, baselineRunId, "suite run");
        return [.. open.Where(signal => signal.Cleared is null).Select(signal => Close(signal, DriftClosed.Baseline, baselineRunId, now).Closed)];
    }

    // ---- reconciliation ----

    private sealed record ClosedSignal(DriftSignal Original, DriftSignal Closed);

    private static ClosedSignal Close(DriftSignal signal, string reason, string by, DateTimeOffset now)
    {
        var detail = signal.Detail?.DeepClone() as JsonObject ?? new JsonObject();
        detail[DriftClosed.ReasonKey] = reason;
        detail[DriftClosed.ByKey] = by;
        return new ClosedSignal(signal, signal with { Cleared = now, Detail = detail });
    }

    /// <summary>A broken case is confirmed only by the same case breaking again; every other rule by the rule alone.</summary>
    private static bool SameCases(DriftSignal signal, DriftFinding finding)
    {
        if (signal.Rule != DriftRule.BrokenCase) return true;
        var before = signal.Detail?["cases"] is JsonArray cases ? cases.Select(node => node?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) : [];
        return finding.Cases.Any(before.Contains);
    }

    private static JsonObject Detail(DriftFinding finding)
    {
        var detail = finding.Detail?.DeepClone() as JsonObject ?? new JsonObject();
        if (finding.Cases.Count > 0 && detail["cases"] is null) detail["cases"] = new JsonArray([.. finding.Cases.Select(id => (JsonNode?)JsonValue.Create(id))]);
        return detail;
    }

    private static void Validate(DriftInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Run);
        ArgumentNullException.ThrowIfNull(input.Baseline);
        ArgumentNullException.ThrowIfNull(input.BaselineRun);
        ArgumentNullException.ThrowIfNull(input.Results);
        ArgumentNullException.ThrowIfNull(input.BaselineResults);
        ArgumentNullException.ThrowIfNull(input.Open);
        if (input.Run.SuiteId != input.Baseline.SuiteId || input.BaselineRun.SuiteId != input.Baseline.SuiteId)
            throw new ArgumentException("The run, its baseline and the baseline's run belong to one suite.");
        if (input.BaselineRun.Id != input.Baseline.RunId) throw new ArgumentException("The baseline run is not the baseline's run.");
        if (string.IsNullOrWhiteSpace(input.TargetId)) throw new ArgumentException("The baseline names its target.");
    }

    // ---- measuring ----

    private static DriftComparison Measure(DriftInput input, DriftThresholds limits)
    {
        var target = input.TargetId;
        var before = input.BaselineResults.Where(result => result.RunId == input.BaselineRun.Id && result.TargetId == target).ToList();
        var after = input.Results.Where(result => result.RunId == input.Run.Id && result.TargetId == target).ToList();
        var beforeByCase = ByCase(before);
        var afterByCase = ByCase(after);
        var cases = beforeByCase.Keys.Intersect(afterByCase.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        int paired = 0, passedBefore = 0, answersBefore = 0, passedAfter = 0, answersAfter = 0, worse = 0, better = 0;
        int leftOutBefore = 0, leftOutAfter = 0;
        int judgeCases = 0, judgeWorse = 0, judgeBetter = 0;
        double judgeSumBefore = 0, judgeSumAfter = 0;
        int judgeCountBefore = 0, judgeCountAfter = 0;
        var broken = new List<string>();
        var moved = new List<string>();
        var distances = new List<double>();
        var spreads = new List<double>();
        foreach (var id in cases)
        {
            var then = beforeByCase[id];
            var now = afterByCase[id];
            // Only decisive answers count (Decisive): a call that never reached the target, or a judge that could not decide, is missing.
            var countedThen = then.Where(Decisive).ToList();
            var countedNow = now.Where(Decisive).ToList();
            leftOutBefore += then.Count(result => result.IsFinished) - countedThen.Count;
            leftOutAfter += now.Count(result => result.IsFinished) - countedNow.Count;
            if (countedThen.Count > 0 && countedNow.Count > 0)
            {
                paired++;
                var passThen = countedThen.Count(result => result.Passed);
                var passNow = countedNow.Count(result => result.Passed);
                passedBefore += passThen;
                answersBefore += countedThen.Count;
                passedAfter += passNow;
                answersAfter += countedNow.Count;
                // Compare shares as exact fractions: passNow / countedNow against passThen / countedThen.
                var sign = ((long)passNow * countedThen.Count).CompareTo((long)passThen * countedNow.Count);
                if (sign < 0) worse++;
                else if (sign > 0) better++;
            }

            var scoresThen = JudgeScores(countedThen);
            var scoresNow = JudgeScores(countedNow);
            if (scoresThen.Count > 0 && scoresNow.Count > 0)
            {
                judgeCases++;
                judgeSumBefore += scoresThen.Sum();
                judgeCountBefore += scoresThen.Count;
                judgeSumAfter += scoresNow.Sum();
                judgeCountAfter += scoresNow.Count;
                var meanThen = scoresThen.Average();
                var meanNow = scoresNow.Average();
                if (meanNow < meanThen - 1e-9) judgeWorse++;
                else if (meanNow > meanThen + 1e-9) judgeBetter++;
            }

            // Broken: every repeat on both sides counted (none missing), all passed then, and none passes now.
            if (input.BaselineRun.K >= limits.MinBrokenCaseRepeats && input.Run.K >= limits.MinBrokenCaseRepeats &&
                countedThen.Count >= input.BaselineRun.K && countedNow.Count >= input.Run.K &&
                countedThen.All(result => result.Passed) && countedNow.All(result => !result.Passed))
                broken.Add(id);

            var printsThen = then.Where(result => result.Status == ResultStatus.Done && result.Fingerprint is not null).Select(result => result.Fingerprint!.Value).Order().ToList();
            var printsNow = now.Where(result => result.Status == ResultStatus.Done && result.Fingerprint is not null).Select(result => result.Fingerprint!.Value).Order().ToList();
            if (printsThen.Count >= limits.MinFingerprintAnswers && printsNow.Count >= 1)
            {
                var spread = 0;
                for (var first = 0; first < printsThen.Count; first++)
                    for (var second = first + 1; second < printsThen.Count; second++)
                        spread = Math.Max(spread, AnswerFingerprint.Distance(printsThen[first], printsThen[second]));
                var distance = DriftStatistics.Median(printsNow.Select(print => (double)printsThen.Min(other => AnswerFingerprint.Distance(print, other))))!.Value;
                spreads.Add(spread);
                distances.Add(distance);
                if (distance > Math.Max(spread, limits.MinFingerprintBits)) moved.Add(id);
            }
        }

        var common = cases.ToHashSet(StringComparer.Ordinal);
        var latencyThen = Values(before, common, result => result.DurationMs);
        var latencyNow = Values(after, common, result => result.DurationMs);
        var speedThen = Values(before, common, result => result.TokensPerSecond is > 0 ? result.TokensPerSecond : null);
        var speedNow = Values(after, common, result => result.TokensPerSecond is > 0 ? result.TokensPerSecond : null);
        // A call that never reached the target cost nothing because nothing was sent: it is left out, as it is from the pass rate.
        var costThen = before.Where(result => common.Contains(result.CaseId) && Reached(result) && result.CostMicros is not null).ToList();
        var costNow = after.Where(result => common.Contains(result.CaseId) && Reached(result) && result.CostMicros is not null).ToList();
        var answersPerRun = (long)cases.Count * Math.Max(1, input.Run.K);
        long? PerRun(List<SuiteResult> costs) => costs.Count == 0 ? null
            : (long)Math.Round((decimal)costs.Sum(result => result.CostMicros!.Value) / costs.Count * answersPerRun, MidpointRounding.AwayFromZero);

        return new DriftComparison
        {
            Cases = cases.Count,
            PairedCases = paired,
            PassedBefore = passedBefore,
            AnswersBefore = answersBefore,
            PassedAfter = passedAfter,
            AnswersAfter = answersAfter,
            LeftOutBefore = leftOutBefore,
            LeftOutAfter = leftOutAfter,
            CasesWorse = worse,
            CasesBetter = better,
            PassRateP = DriftStatistics.SignTest(worse, better),
            JudgeCases = judgeCases,
            JudgeBefore = judgeCountBefore == 0 ? null : judgeSumBefore / judgeCountBefore,
            JudgeAfter = judgeCountAfter == 0 ? null : judgeSumAfter / judgeCountAfter,
            JudgeWorse = judgeWorse,
            JudgeBetter = judgeBetter,
            JudgeP = DriftStatistics.SignTest(judgeWorse, judgeBetter),
            BrokenCases = broken,
            LatencySamplesBefore = latencyThen.Count,
            LatencySamplesAfter = latencyNow.Count,
            LatencyBeforeMs = DriftStatistics.Median(latencyThen),
            LatencyAfterMs = DriftStatistics.Median(latencyNow),
            LatencyLowMs = DriftStatistics.Percentile(latencyThen, 0.1),
            LatencyHighMs = DriftStatistics.Percentile(latencyThen, 0.9),
            SpeedSamplesBefore = speedThen.Count,
            SpeedSamplesAfter = speedNow.Count,
            TokensPerSecondBefore = DriftStatistics.Median(speedThen),
            TokensPerSecondAfter = DriftStatistics.Median(speedNow),
            TokensPerSecondLow = DriftStatistics.Percentile(speedThen, 0.1),
            TokensPerSecondHigh = DriftStatistics.Percentile(speedThen, 0.9),
            CostSamplesBefore = costThen.Count,
            CostSamplesAfter = costNow.Count,
            CostPerRunBefore = PerRun(costThen),
            CostPerRunAfter = PerRun(costNow),
            ListPrice = costNow.Count > 0 ? costNow.All(result => result.ListPrice) : costThen.Count > 0 && costThen.All(result => result.ListPrice),
            InputTokensBefore = Mean(Values(before, common, result => result.InputTokens)),
            InputTokensAfter = Mean(Values(after, common, result => result.InputTokens)),
            OutputTokensBefore = Mean(Values(before, common, result => result.OutputTokens)),
            OutputTokensAfter = Mean(Values(after, common, result => result.OutputTokens)),
            ResponseModelsBefore = input.Baseline.ResponseModels.Count > 0 ? [.. input.Baseline.ResponseModels] : ResponseModels(before),
            ResponseModelsAfter = ResponseModels(after),
            FingerprintCases = distances.Count,
            MovedCases = moved,
            FingerprintDistance = DriftStatistics.Median(distances),
            FingerprintSpread = DriftStatistics.Median(spreads),
        };
    }

    private static Dictionary<string, List<SuiteResult>> ByCase(IEnumerable<SuiteResult> results) =>
        results.GroupBy(result => result.CaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(result => result.Repetition).ToList(), StringComparer.Ordinal);

    /// <summary>The judge scores of <paramref name="counted"/> answers, from judges that decided (passed or failed): an unsure judge's score
    /// (review) and a judge that could not run (error) are left out.</summary>
    private static List<double> JudgeScores(IEnumerable<SuiteResult> counted) =>
        [.. counted.SelectMany(result => result.Checks)
            .Where(check => check.Kind == CheckKind.Judge && (check.Verdict is CheckVerdict.Passed or CheckVerdict.Failed) &&
                check.Score is { } score && double.IsFinite(score))
            .Select(check => check.Score!.Value)];

    /// <summary>Whether <paramref name="result"/>'s call reached the target: answered, or sent and failed.</summary>
    private static bool Reached(SuiteResult result) => result.Status == ResultStatus.Done || (result.Status == ResultStatus.Error && result.Sent);

    private static List<double> Values(IEnumerable<SuiteResult> results, HashSet<string> cases, Func<SuiteResult, double?> value) =>
        [.. results.Where(result => result.Status == ResultStatus.Done && cases.Contains(result.CaseId)).Select(value)
            .Where(number => number is { } finite && double.IsFinite(finite)).Select(number => number!.Value).Order()];

    private static double? Mean(IReadOnlyCollection<double> values) => values.Count == 0 ? null : values.Average();

    private static List<string> ResponseModels(IEnumerable<SuiteResult> results) =>
        [.. results.Where(result => result.Status == ResultStatus.Done && !string.IsNullOrWhiteSpace(result.ResponseModel))
            .GroupBy(result => result.ResponseModel!.Trim(), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key)];

    // ---- rules ----

    private static List<DriftFinding> Findings(DriftInput input, DriftComparison numbers, DriftThresholds limits)
    {
        var found = new List<DriftFinding>();
        foreach (var rule in DriftRules.All)
            if (Find(rule, input, numbers, limits) is { } finding) found.Add(finding);
        return found;
    }

    private static DriftFinding? Find(DriftRule rule, DriftInput input, DriftComparison numbers, DriftThresholds limits) => rule switch
    {
        DriftRule.ResponseModel => ResponseModelRule(numbers),
        DriftRule.BrokenCase => BrokenCaseRule(input, numbers),
        DriftRule.PassRate => PassRateRule(numbers, limits),
        DriftRule.JudgeScore => JudgeScoreRule(numbers, limits),
        DriftRule.Fingerprint => FingerprintRule(numbers),
        DriftRule.Cost => CostRule(input, numbers, limits),
        DriftRule.Latency => SpeedRule(DriftRule.Latency, numbers.LatencySamplesBefore, numbers.LatencySamplesAfter, numbers.LatencyBeforeMs,
            numbers.LatencyAfterMs, numbers.LatencyLowMs, numbers.LatencyHighMs, limits),
        DriftRule.TokensPerSecond => SpeedRule(DriftRule.TokensPerSecond, numbers.SpeedSamplesBefore, numbers.SpeedSamplesAfter,
            numbers.TokensPerSecondBefore, numbers.TokensPerSecondAfter, numbers.TokensPerSecondLow, numbers.TokensPerSecondHigh, limits),
        _ => null,
    };

    private static DriftFinding? ResponseModelRule(DriftComparison numbers)
    {
        var before = numbers.ResponseModelsBefore;
        var after = numbers.ResponseModelsAfter;
        if (before.Count == 0 || after.Count == 0) return null;
        var added = after.Where(model => !before.Contains(model, StringComparer.Ordinal)).ToList();
        if (added.Count == 0) return null;
        var still = after.Where(model => before.Contains(model, StringComparer.Ordinal)).ToList();
        var message = still.Count == 0
            ? $"The response model changed from {Or(before)} to {DriftText.List(added)}."
            : $"The response model changed: some answers came from {DriftText.List(added)} instead of {Or(before)}.";
        return new DriftFinding(DriftRule.ResponseModel, message, Detail: new JsonObject
        {
            ["before"] = Strings(before),
            ["after"] = Strings(after),
            ["added"] = Strings(added),
        });
    }

    private static DriftFinding? BrokenCaseRule(DriftInput input, DriftComparison numbers)
    {
        if (numbers.BrokenCases.Count == 0) return null;
        var names = numbers.BrokenCases.Select(id => DriftText.Quote(Name(input, id))).ToList();
        var message = numbers.BrokenCases.Count == 1
            ? Invariant($"{names[0]} passed all {input.BaselineRun.K} repeats in the baseline and fails all {input.Run.K} now.")
            : Invariant($"{numbers.BrokenCases.Count} cases passed all their repeats in the baseline and fail all of them now: {DriftText.List(names)}.");
        return new DriftFinding(DriftRule.BrokenCase, message, Detail: new JsonObject
        {
            ["cases"] = Strings(numbers.BrokenCases),
            ["kBefore"] = input.BaselineRun.K,
            ["kAfter"] = input.Run.K,
        })
        { Cases = numbers.BrokenCases };
    }

    private static DriftFinding? PassRateRule(DriftComparison numbers, DriftThresholds limits)
    {
        if (numbers.PassRateBefore is not { } before || numbers.PassRateAfter is not { } after) return null;
        if (numbers.CasesWorse == 0 || numbers.PassRateP >= limits.SignificanceLevel || before - after < limits.MinPassRateDrop - 1e-12) return null;
        var improved = numbers.CasesBetter == 0 ? "none" : numbers.CasesBetter.ToString(CultureInfo.InvariantCulture);
        var message = Invariant($"Pass rate fell from {before:0.00} to {after:0.00} ({DriftComparison.Counts(numbers.PassedBefore, numbers.AnswersBefore, numbers.PassedAfter, numbers.AnswersAfter)} answers). ")
            + Invariant($"{numbers.CasesWorse} of {numbers.PairedCases} cases got worse and {improved} improved (one-sided sign test, {DriftText.PValue(numbers.PassRateP)}).");
        return new DriftFinding(DriftRule.PassRate, message, numbers.PassRateP, before > 0 ? Round((after - before) / before) : null, new JsonObject
        {
            ["before"] = Round(before),
            ["after"] = Round(after),
            ["passedBefore"] = numbers.PassedBefore,
            ["answersBefore"] = numbers.AnswersBefore,
            ["passedAfter"] = numbers.PassedAfter,
            ["answersAfter"] = numbers.AnswersAfter,
            ["cases"] = numbers.PairedCases,
            ["worse"] = numbers.CasesWorse,
            ["better"] = numbers.CasesBetter,
            ["leftOutBefore"] = numbers.LeftOutBefore,
            ["leftOutAfter"] = numbers.LeftOutAfter,
        });
    }

    private static DriftFinding? JudgeScoreRule(DriftComparison numbers, DriftThresholds limits)
    {
        if (numbers.JudgeBefore is not { } before || numbers.JudgeAfter is not { } after) return null;
        if (numbers.JudgeWorse == 0 || numbers.JudgeP >= limits.SignificanceLevel || before - after < limits.MinJudgeScoreDrop - 1e-12) return null;
        var higher = numbers.JudgeBetter == 0 ? "none" : numbers.JudgeBetter.ToString(CultureInfo.InvariantCulture);
        var message = Invariant($"Judge score fell from {before:0.0} to {after:0.0} (out of 5). ")
            + Invariant($"{numbers.JudgeWorse} of {numbers.JudgeCases} judged cases scored lower and {higher} higher (one-sided sign test, {DriftText.PValue(numbers.JudgeP)}).");
        return new DriftFinding(DriftRule.JudgeScore, message, numbers.JudgeP, before > 0 ? Round((after - before) / before) : null, new JsonObject
        {
            ["before"] = Round(before),
            ["after"] = Round(after),
            ["cases"] = numbers.JudgeCases,
            ["worse"] = numbers.JudgeWorse,
            ["better"] = numbers.JudgeBetter,
        });
    }

    private static DriftFinding? FingerprintRule(DriftComparison numbers)
    {
        var compared = numbers.FingerprintCases;
        var moved = numbers.MovedCases.Count;
        if (compared == 0 || moved * 2 <= compared) return null;
        var message = Invariant($"Answers changed: {moved} of {compared} cases answered outside the baseline's own spread ")
            + Invariant($"(SimHash distance {numbers.FingerprintDistance:0.#} bits against a spread of {numbers.FingerprintSpread:0.#}).");
        return new DriftFinding(DriftRule.Fingerprint, message, Effect: Round((double)moved / compared), Detail: new JsonObject
        {
            ["compared"] = compared,
            ["moved"] = moved,
            ["distance"] = numbers.FingerprintDistance,
            ["spread"] = numbers.FingerprintSpread,
        })
        { Cases = numbers.MovedCases };
    }

    private static DriftFinding? CostRule(DriftInput input, DriftComparison numbers, DriftThresholds limits)
    {
        if (numbers.CostSamplesBefore < limits.MinSamples || numbers.CostSamplesAfter < limits.MinSamples) return null;
        if (numbers.CostPerRunBefore is not { } before || numbers.CostPerRunAfter is not { } after || before <= 0) return null;
        var change = (double)after / before - 1;
        if (Math.Abs(change) < limits.CostChange - 1e-12) return null;
        var detail = new JsonObject
        {
            ["beforeMicros"] = before,
            ["afterMicros"] = after,
            ["listPrice"] = numbers.ListPrice,
            ["inputTokensBefore"] = Round(numbers.InputTokensBefore),
            ["inputTokensAfter"] = Round(numbers.InputTokensAfter),
            ["outputTokensBefore"] = Round(numbers.OutputTokensBefore),
            ["outputTokensAfter"] = Round(numbers.OutputTokensAfter),
        };
        var tokens = numbers.OutputTokensBefore is { } outThen && numbers.OutputTokensAfter is { } outNow
            ? Invariant($"output tokens per answer {outThen:0} → {outNow:0}")
            : null;
        string split;
        if (input.BaselinePrice is { } then && input.CurrentPrice is { } now && numbers.InputTokensAfter is { } inputs && numbers.OutputTokensAfter is { } outputs)
        {
            // Rates are US dollars per million tokens, which is micro-USD per token: this run's tokens at the new rates less at the old ones.
            var answers = (double)numbers.Cases * Math.Max(1, input.Run.K);
            var price = (long)Math.Round((inputs * (now.Input - then.Input) + outputs * (now.Output - then.Output)) * answers, MidpointRounding.AwayFromZero);
            var verbosity = after - before - price;
            detail["priceMicros"] = price;
            detail["verbosityMicros"] = verbosity;
            split = $"price change {DriftText.SignedMoney(price)}, verbosity {DriftText.SignedMoney(verbosity)}" + (tokens is null ? "" : "; " + tokens);
        }
        else split = (tokens is null ? "" : tokens + "; ") + "no price history to split it into price and verbosity";
        var message = $"Cost per run {DriftText.Percent(change)}: {DriftText.Money(before, numbers.ListPrice)} → {DriftText.Money(after, numbers.ListPrice)} ({split}).";
        return new DriftFinding(DriftRule.Cost, message, Effect: Round(change), Detail: detail);
    }

    private static DriftFinding? SpeedRule(DriftRule rule, int samplesBefore, int samplesAfter, double? median, double? now, double? low, double? high,
        DriftThresholds limits)
    {
        if (samplesBefore < limits.MinSamples || samplesAfter < limits.MinSamples) return null;
        if (median is not { } before || now is not { } after || low is not { } p10 || high is not { } p90 || before <= 0) return null;
        var change = after / before - 1;
        if (Math.Abs(change) < limits.SpeedChange - 1e-12 || (after >= p10 && after <= p90)) return null;
        var message = rule == DriftRule.Latency
            ? $"Latency {DriftText.Percent(change)}: median {DriftText.Duration(before)} → {DriftText.Duration(after)}, outside the baseline's {DriftText.DurationRange(p10, p90)} range (10th to 90th percentile)."
            : $"Tokens/s {DriftText.Percent(change)}: median {DriftText.Speed(before)} → {DriftText.Speed(after)}, outside the baseline's {DriftText.Speed(p10)}–{DriftText.Speed(p90)} range (10th to 90th percentile).";
        return new DriftFinding(rule, message, Effect: Round(change), Detail: new JsonObject
        {
            ["before"] = Round(before),
            ["after"] = Round(after),
            ["p10"] = Round(p10),
            ["p90"] = Round(p90),
        });
    }

    private static string Name(DriftInput input, string caseId) =>
        input.CaseNames is { } names && names.TryGetValue(caseId, out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : caseId;

    private static string Or(IReadOnlyList<string> models) => models.Count switch
    {
        1 => models[0],
        2 => models[0] + " or " + models[1],
        _ => string.Join(", ", models.Take(models.Count - 1)) + " or " + models[^1],
    };

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]);

    private static double Round(double value) => Math.Round(value, 6);

    private static double? Round(double? value) => value is { } number ? Math.Round(number, 6) : null;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
