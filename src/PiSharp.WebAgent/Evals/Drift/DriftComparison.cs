// Decision 0011 (run W5, E3): the numbers behind a target's drift row (mockup evals-drift _DriftSuite: the row's message, "Other signals",
// "No changes against the baseline. Pass rate 0.97 → 0.97."). One target of one run against its baseline run, over the cases both runs
// asked (a case added or removed since is left out of every rule, so editing a suite never looks like drift). Counting follows
// SuiteResult (an answer passes when every check passed; a call that was sent and failed fails) over the answers that say something about
// the model (DriftAnalyzer.Decisive): a call that never reached the target and an answer the checks could not decide (a judge refused,
// failed or unsure) are left out, and LeftOutBefore and LeftOutAfter count them. DriftAnalyzer.Compare fills it; the rules
// (DriftAnalyzer.Analyze) read it, and the pages show its texts.
using System.Globalization;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>One target's latest run against its baseline run, as numbers.</summary>
public sealed record DriftComparison
{
    /// <summary>Cases both runs asked (the cases every rule compares).</summary>
    public int Cases { get; init; }

    /// <summary>Cases with a counted answer in both runs (the pass-rate sign test's cases; <see cref="DriftAnalyzer.Decisive"/>).</summary>
    public int PairedCases { get; init; }

    /// <summary>Baseline answers that passed, of <see cref="AnswersBefore"/> (paired cases).</summary>
    public int PassedBefore { get; init; }

    /// <summary>Baseline answers counted (paired cases).</summary>
    public int AnswersBefore { get; init; }

    /// <summary>Answers that passed now, of <see cref="AnswersAfter"/> (paired cases).</summary>
    public int PassedAfter { get; init; }

    /// <summary>Answers counted now (paired cases).</summary>
    public int AnswersAfter { get; init; }

    /// <summary>Finished baseline answers of the compared cases left out as missing: calls that never reached the target, and answers the
    /// checks could not decide (<see cref="DriftAnalyzer.Decisive"/>).</summary>
    public int LeftOutBefore { get; init; }

    /// <summary>Finished answers of the compared cases left out as missing now.</summary>
    public int LeftOutAfter { get; init; }

    /// <summary>The baseline's pass rate (0 to 1), or null without answers.</summary>
    [JsonIgnore]
    public double? PassRateBefore => AnswersBefore == 0 ? null : (double)PassedBefore / AnswersBefore;

    /// <summary>The pass rate now (0 to 1), or null without answers.</summary>
    [JsonIgnore]
    public double? PassRateAfter => AnswersAfter == 0 ? null : (double)PassedAfter / AnswersAfter;

    /// <summary>Paired cases whose pass share fell.</summary>
    public int CasesWorse { get; init; }

    /// <summary>Paired cases whose pass share rose.</summary>
    public int CasesBetter { get; init; }

    /// <summary>The sign test's one-sided p-value over <see cref="CasesWorse"/> and <see cref="CasesBetter"/> (1 when none moved).</summary>
    public double PassRateP { get; init; } = 1;

    /// <summary>Cases with judge scores in both runs.</summary>
    public int JudgeCases { get; init; }

    /// <summary>The baseline's mean judge score (1 to 5) over <see cref="JudgeCases"/>, or null.</summary>
    public double? JudgeBefore { get; init; }

    /// <summary>The mean judge score now, or null.</summary>
    public double? JudgeAfter { get; init; }

    /// <summary>Judged cases whose mean score fell.</summary>
    public int JudgeWorse { get; init; }

    /// <summary>Judged cases whose mean score rose.</summary>
    public int JudgeBetter { get; init; }

    /// <summary>The judge scores' sign-test p-value (1 when none moved).</summary>
    public double JudgeP { get; init; } = 1;

    /// <summary>Cases that passed all their repeats in the baseline and fail all of them now.</summary>
    public IReadOnlyList<string> BrokenCases { get; init; } = [];

    /// <summary>Answered calls timed in the baseline (latency samples).</summary>
    public int LatencySamplesBefore { get; init; }

    /// <summary>Answered calls timed now.</summary>
    public int LatencySamplesAfter { get; init; }

    /// <summary>The baseline's median latency, in milliseconds, or null.</summary>
    public double? LatencyBeforeMs { get; init; }

    /// <summary>The median latency now, or null.</summary>
    public double? LatencyAfterMs { get; init; }

    /// <summary>The baseline's 10th percentile latency, or null.</summary>
    public double? LatencyLowMs { get; init; }

    /// <summary>The baseline's 90th percentile latency, or null.</summary>
    public double? LatencyHighMs { get; init; }

    /// <summary>Answered calls with a tokens/s figure in the baseline.</summary>
    public int SpeedSamplesBefore { get; init; }

    /// <summary>Answered calls with a tokens/s figure now.</summary>
    public int SpeedSamplesAfter { get; init; }

    /// <summary>The baseline's median output tokens per second, or null.</summary>
    public double? TokensPerSecondBefore { get; init; }

    /// <summary>The median output tokens per second now, or null.</summary>
    public double? TokensPerSecondAfter { get; init; }

    /// <summary>The baseline's 10th percentile tokens/s, or null.</summary>
    public double? TokensPerSecondLow { get; init; }

    /// <summary>The baseline's 90th percentile tokens/s, or null.</summary>
    public double? TokensPerSecondHigh { get; init; }

    /// <summary>Answers with a cost in the baseline.</summary>
    public int CostSamplesBefore { get; init; }

    /// <summary>Answers with a cost now.</summary>
    public int CostSamplesAfter { get; init; }

    /// <summary>The baseline's cost per run, in micro-USD: its mean cost per answer × the answers this run asked of these cases.</summary>
    public long? CostPerRunBefore { get; init; }

    /// <summary>The cost per run now, in micro-USD (the same count of answers).</summary>
    public long? CostPerRunAfter { get; init; }

    /// <summary>Whether the costs are list prices (a subscription target, never counted toward caps).</summary>
    public bool ListPrice { get; init; }

    /// <summary>The baseline's mean input tokens per answer, or null.</summary>
    public double? InputTokensBefore { get; init; }

    /// <summary>The mean input tokens per answer now, or null.</summary>
    public double? InputTokensAfter { get; init; }

    /// <summary>The baseline's mean output tokens per answer, or null.</summary>
    public double? OutputTokensBefore { get; init; }

    /// <summary>The mean output tokens per answer now, or null.</summary>
    public double? OutputTokensAfter { get; init; }

    /// <summary>The response models of the baseline (most answers first).</summary>
    public IReadOnlyList<string> ResponseModelsBefore { get; init; } = [];

    /// <summary>The response models now (most answers first).</summary>
    public IReadOnlyList<string> ResponseModelsAfter { get; init; } = [];

    /// <summary>Cases whose fingerprints could be compared (<see cref="DriftThresholds.MinFingerprintAnswers"/> or more baseline answers, one or
    /// more now).</summary>
    public int FingerprintCases { get; init; }

    /// <summary>Of those, the cases that answered outside the baseline's own spread.</summary>
    public IReadOnlyList<string> MovedCases { get; init; } = [];

    /// <summary>The median, over compared cases, of the answers' SimHash distance to the nearest baseline answer, in bits.</summary>
    public double? FingerprintDistance { get; init; }

    /// <summary>The median, over compared cases, of the baseline's own spread (the largest distance between two of its answers), in bits.</summary>
    public double? FingerprintSpread { get; init; }

    /// <summary>"Other signals" for a target's row: the pass rate, median latency and cost per run that did not raise a signal
    /// (<c>Pass rate 0.92 → 0.89 (33 → 32 of 36), not significant. Median latency 4.2 s → 4.3 s. Cost per run $0.16 → $0.16.</c>); empty when
    /// there is nothing to show.</summary>
    /// <param name="raised">The rules that raised a signal in this run (their numbers are in the signal's own message).</param>
    public string OtherSignals(IReadOnlyCollection<DriftRule> raised)
    {
        ArgumentNullException.ThrowIfNull(raised);
        var parts = new List<string>();
        if (!raised.Contains(DriftRule.PassRate) && PassRateBefore is { } before && PassRateAfter is { } after)
            parts.Add(Invariant($"Pass rate {before:0.00} → {after:0.00} ({Counts(PassedBefore, AnswersBefore, PassedAfter, AnswersAfter)})")
                + (after < before ? ", not significant." : "."));
        if (!raised.Contains(DriftRule.Latency) && LatencyBeforeMs is { } slow && LatencyAfterMs is { } now)
            parts.Add($"Median latency {DriftText.Duration(slow)} → {DriftText.Duration(now)}.");
        if (!raised.Contains(DriftRule.Cost) && CostPerRunBefore is { } costBefore && CostPerRunAfter is { } costAfter)
            parts.Add($"Cost per run {DriftText.Money(costBefore, ListPrice)} → {DriftText.Money(costAfter, ListPrice)}.");
        return string.Join(" ", parts);
    }

    /// <summary>The row of a target with no signal: <c>No changes against the baseline. Pass rate 0.97 → 0.97.</c></summary>
    [JsonIgnore]
    public string OkText => PassRateBefore is { } before && PassRateAfter is { } after
        ? Invariant($"No changes against the baseline. Pass rate {before:0.00} → {after:0.00}.")
        : "No changes against the baseline.";

    /// <summary>Passed counts before and after: <c>33 → 32 of 36</c>, or <c>33 of 36 → 32 of 35</c> when the totals differ.</summary>
    internal static string Counts(int passedBefore, int answersBefore, int passedAfter, int answersAfter) => answersBefore == answersAfter
        ? Invariant($"{passedBefore} → {passedAfter} of {answersAfter}")
        : Invariant($"{passedBefore} of {answersBefore} → {passedAfter} of {answersAfter}");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>How Drift Watch writes numbers.</summary>
public static class DriftText
{
    /// <summary>A duration: <c>5.0 s</c>, or <c>850 ms</c> under a second.</summary>
    public static string Duration(double milliseconds) => milliseconds >= 1000
        ? (milliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : Math.Round(milliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>A range of durations in one unit: <c>4.2–6.0 s</c>, or <c>300–850 ms</c>.</summary>
    public static string DurationRange(double low, double high) => high >= 1000
        ? (low / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "–" + (high / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : Math.Round(low).ToString("0", CultureInfo.InvariantCulture) + "–" + Math.Round(high).ToString("0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>Tokens per second: <c>80</c>, or <c>4.5</c> under 10.</summary>
    public static string Speed(double tokensPerSecond) =>
        tokensPerSecond.ToString(tokensPerSecond < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);

    /// <summary>An amount: <c>$0.16</c> (four decimals under a cent: <c>$0.0042</c>), or <c>≈ $0.17 list</c> for a list price.</summary>
    public static string Money(long micros, bool list = false)
    {
        var decimals = Math.Abs(micros) < 10_000 ? 4 : 2;
        return list ? SpendMoney.FormatList(micros, decimals) : SpendMoney.Format(micros, decimals);
    }

    /// <summary>A signed amount: <c>+$0.03</c>, <c>−$0.02</c>.</summary>
    public static string SignedMoney(long micros) => (micros < 0 ? "−" : "+") + Money(Math.Abs(micros));

    /// <summary>A signed percentage of a share: <c>+62%</c>, <c>−55%</c>.</summary>
    public static string Percent(double share)
    {
        var percent = Math.Round(share * 100);
        return (percent < 0 ? "−" : "+") + Math.Abs(percent).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>A p-value: <c>p = 0.031</c>, or <c>p &lt; 0.001</c>.</summary>
    public static string PValue(double p) => p < 0.001 ? "p < 0.001" : "p = " + p.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>A case's name in a message: <c>“SemVer 2.0.0”</c>.</summary>
    public static string Quote(string name) => "“" + name + "”";

    /// <summary>A list in a sentence: <c>A</c>, <c>A and B</c>, <c>A, B and C</c>; after <paramref name="most"/> items, <c>and 2 more</c>.</summary>
    public static string List(IReadOnlyList<string> items, int most = 5)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return "";
        var shown = items.Take(most).ToList();
        if (items.Count > most) shown.Add($"{items.Count - most} more");
        return shown.Count == 1 ? shown[0] : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
    }

    /// <summary>Where a signal stands, for its row: <c>Run #20 saw it first and run #21 saw it again.</c> for drift a later run saw again,
    /// <c>Waiting for the next run to confirm it.</c> for an open suspect signal, else empty.</summary>
    /// <param name="signal">The signal.</param>
    /// <param name="firstRunNumber">The number of the run that saw it first (<see cref="DriftSignal.FirstRunId"/>), when known.</param>
    /// <param name="runNumber">The number of the run that raised this row (<see cref="DriftSignal.RunId"/>), when known.</param>
    public static string Lineage(DriftSignal signal, int? firstRunNumber, int? runNumber)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.AwaitingConfirmation) return "Waiting for the next run to confirm it.";
        if (signal.Level == DriftLevel.Drift && signal.FirstRunId is { } first && first != signal.RunId && firstRunNumber is { } from && runNumber is { } to)
            return string.Create(CultureInfo.InvariantCulture, $"Run #{from} saw it first and run #{to} saw it again.");
        return "";
    }
}
