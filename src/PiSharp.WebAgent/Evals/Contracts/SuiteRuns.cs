// Decision 0011 (run W5, E0): a run of a suite and its results (mockup evals-run: _EvalRunHeader, _RunGrid). A run asks every case on every
// target k times; each (case, target, repetition) is one SuiteResult, which is one call through the run engine (its runs.db run id is
// CallRunId). The run keeps a copy of the targets and k it ran with, so editing the suite later never changes what an old run means.
//
// Counting, as the run page shows it: an answer passes when every check passed (CheckResult.Combine is Passed); a result's score is the
// share of its checks that passed, and a cell's score (case × target) is the mean over its k repeats ("0.83" for 5 of 6 checks); a target's
// pass rate is passed answers over answers ("35/36 passed"). An answer the call could not give (an error) fails. Judge "review" verdicts
// do not pass. A case with no checks passes when it got an answer, and has no score. Drift Watch counts more strictly (DriftAnalyzer.Decisive):
// an error whose call was never sent (SuiteResult.Sent false) and an answer whose checks could not decide (a judge refused, failed or sent
// to review) are missing answers there, not failures, since neither says anything about the model.
//
// Each run is numbered within its suite (#1, #2, ...); a scheduled run also records its slot (unique per suite: one run per slot, decision
// 0009). Slots the scheduler skipped (the previous run still going, the worst case over the cap, an unknown price) are recorded as
// ScheduleSlot rows, not as runs, so run numbers have no gaps.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Evals;

/// <summary>What started a suite run. Sent as <c>manual</c>, <c>schedule</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SuiteRunTrigger>))]
public enum SuiteRunTrigger
{
    /// <summary>Run now, from the page.</summary>
    [JsonStringEnumMemberName("manual")] Manual,

    /// <summary>A schedule slot.</summary>
    [JsonStringEnumMemberName("schedule")] Schedule,
}

/// <summary>Where a suite run is. Sent as <c>queued</c>, <c>running</c>, <c>complete</c>, <c>cancelled</c>, <c>failed</c>,
/// <c>interrupted</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SuiteRunStatus>))]
public enum SuiteRunStatus
{
    /// <summary>Waiting for its reservation.</summary>
    [JsonStringEnumMemberName("queued")] Queued,

    /// <summary>Answers are being asked.</summary>
    [JsonStringEnumMemberName("running")] Running,

    /// <summary>Every answer is in and checked.</summary>
    [JsonStringEnumMemberName("complete")] Complete,

    /// <summary>Cancelled by the user; its finished results stay.</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,

    /// <summary>It could not run (the Spend Guard refused it, or no target could be asked); <see cref="SuiteRun.Problem"/> says why.</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>The app stopped while it was going: its finished results stay, its unfinished ones are cancelled, its reservation is
    /// released, and its slot is never run again.</summary>
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}

/// <summary>Where one answer of a run is. Sent as <c>queued</c>, <c>running</c>, <c>done</c>, <c>error</c>, <c>cancelled</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultStatus>))]
public enum ResultStatus
{
    /// <summary>Not asked yet.</summary>
    [JsonStringEnumMemberName("queued")] Queued,

    /// <summary>Being answered ("streaming").</summary>
    [JsonStringEnumMemberName("running")] Running,

    /// <summary>Answered and checked.</summary>
    [JsonStringEnumMemberName("done")] Done,

    /// <summary>The call failed; the answer fails.</summary>
    [JsonStringEnumMemberName("error")] Error,

    /// <summary>Never answered: the run was cancelled or interrupted first.</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>What the scheduler did with a slot. Sent as <c>started</c>, <c>skipped-running</c>, <c>skipped-over-cap</c>,
/// <c>skipped-unknown-price</c>, <c>skipped-refused</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SlotOutcome>))]
public enum SlotOutcome
{
    /// <summary>A run started (<see cref="ScheduleSlot.RunId"/>).</summary>
    [JsonStringEnumMemberName("started")] Started,

    /// <summary>Skipped: the previous run was still going.</summary>
    [JsonStringEnumMemberName("skipped-running")] SkippedRunning,

    /// <summary>Skipped: the worst case did not fit the cap (the schedule is paused).</summary>
    [JsonStringEnumMemberName("skipped-over-cap")] SkippedOverCap,

    /// <summary>Skipped: a target or judge had no known price.</summary>
    [JsonStringEnumMemberName("skipped-unknown-price")] SkippedUnknownPrice,

    /// <summary>Skipped: the Spend Guard refused the reservation (another cap was reached).</summary>
    [JsonStringEnumMemberName("skipped-refused")] SkippedRefused,
}

/// <summary>One schedule slot the scheduler has decided on (one row per suite and slot).</summary>
/// <param name="SuiteId">The suite id.</param>
/// <param name="Slot">The slot (<see cref="SuiteSchedule.Latest"/>).</param>
/// <param name="Outcome">What happened.</param>
/// <param name="RunId">The run it started, or null when skipped.</param>
/// <param name="Detail">Why it was skipped, for the page, or null.</param>
/// <param name="At">When it was decided.</param>
public sealed record ScheduleSlot(string SuiteId, DateTimeOffset Slot, SlotOutcome Outcome, string? RunId, string? Detail, DateTimeOffset At);

/// <summary>One run of a suite.</summary>
public sealed record SuiteRun
{
    /// <summary>The run id (<see cref="EvalIds.Run"/>).</summary>
    public required string Id { get; init; }

    /// <summary>The suite id.</summary>
    public required string SuiteId { get; init; }

    /// <summary>Its number within the suite, from 1 ("Run #21").</summary>
    public int Number { get; init; }

    /// <summary>What started it.</summary>
    public SuiteRunTrigger Trigger { get; init; }

    /// <summary>The schedule slot it ran for, or null for a run started by hand.</summary>
    public DateTimeOffset? Slot { get; init; }

    /// <summary>Where it is.</summary>
    public SuiteRunStatus Status { get; init; } = SuiteRunStatus.Queued;

    /// <summary>The targets it ran on, as they were when it started.</summary>
    public IReadOnlyList<SuiteTarget> Targets { get; init; } = [];

    /// <summary>The ids of the cases it ran, as they were when it started.</summary>
    public IReadOnlyList<string> CaseIds { get; init; } = [];

    /// <summary>Repetitions of each case on each target.</summary>
    public int K { get; init; } = Suite.DefaultK;

    /// <summary>Answers it asks: cases × targets × k.</summary>
    [JsonIgnore]
    public int Answers => CaseIds.Count * Targets.Count * K;

    /// <summary>The run-engine group of its calls (the Spend Guard reservation's group), or null before it starts.</summary>
    public string? GroupId { get; init; }

    /// <summary>The metered worst case reserved for it, in micro-USD.</summary>
    public long ReservedMicros { get; init; }

    /// <summary>The metered cost settled so far, in micro-USD (judge calls included).</summary>
    public long CostMicros { get; init; }

    /// <summary>The cost of its subscription answers at list price, in micro-USD (never counted toward a cap).</summary>
    public long ListMicros { get; init; }

    /// <summary>Why it failed or was cancelled, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When its first call started, or null.</summary>
    public DateTimeOffset? Started { get; init; }

    /// <summary>When it ended, or null while it is going.</summary>
    public DateTimeOffset? Finished { get; init; }

    /// <summary>Whether it is still going (queued or running).</summary>
    [JsonIgnore]
    public bool Active => Status is SuiteRunStatus.Queued or SuiteRunStatus.Running;

    /// <summary>The target of this run with id <paramref name="targetId"/>, or null.</summary>
    public SuiteTarget? Target(string? targetId) => Targets.FirstOrDefault(target => target.Id == targetId);

    /// <summary>A new run of <paramref name="suite"/> over <paramref name="caseIds"/>, numbered <paramref name="number"/>.</summary>
    /// <exception cref="ArgumentException">The suite cannot run (<see cref="Suite.RunProblem"/>), or the trigger and slot do not match.</exception>
    public static SuiteRun Start(Suite suite, IReadOnlyList<string> caseIds, int number, SuiteRunTrigger trigger, DateTimeOffset? slot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(caseIds);
        if (suite.RunProblem(caseIds.Count) is { } problem) throw new ArgumentException(problem);
        if (number < 1) throw new ArgumentException("Runs are numbered from 1.");
        if ((trigger == SuiteRunTrigger.Schedule) != slot.HasValue) throw new ArgumentException("A scheduled run has a slot, and only a scheduled run.");
        if (caseIds.Distinct(StringComparer.Ordinal).Count() != caseIds.Count) throw new ArgumentException("A case is listed twice.");
        foreach (var id in caseIds) EvalIds.Require(EvalIds.Case, id, "test case");
        return new SuiteRun
        {
            Id = EvalIds.New(EvalIds.Run),
            SuiteId = suite.Id,
            Number = number,
            Trigger = trigger,
            Slot = slot,
            Targets = [.. suite.Targets],
            CaseIds = [.. caseIds],
            K = suite.K,
            Created = now,
        };
    }
}

/// <summary>One answer of a suite run: one case, on one target, one repetition.</summary>
public sealed record SuiteResult
{
    /// <summary>The longest answer text kept (longer answers are cut, and the full one stays with its call while History keeps it).</summary>
    public const int MaxTextLength = 100_000;

    /// <summary>The suite run id.</summary>
    public required string RunId { get; init; }

    /// <summary>The test case id.</summary>
    public required string CaseId { get; init; }

    /// <summary>The target id (<see cref="SuiteTarget.Id"/>).</summary>
    public required string TargetId { get; init; }

    /// <summary>Which repetition, from 0 to k − 1.</summary>
    public int Repetition { get; init; }

    /// <summary>Where it is.</summary>
    public ResultStatus Status { get; init; } = ResultStatus.Queued;

    /// <summary>The run-engine run id of its call (open it in the inspector), or null before it starts.</summary>
    public string? CallRunId { get; init; }

    /// <summary>Whether the target call was sent: the run engine started it and its record was read back (an answer, a provider error or a
    /// stop). False until then, and for an error that never reached the target: the Spend Guard refused the call (or it was stopped as never
    /// sent), the call did not start, or its record is missing. Such an error says nothing about the model, so Drift Watch leaves it out as a
    /// missing answer (fix W5 drift 1).</summary>
    public bool Sent { get; init; }

    /// <summary>The checks' results, in the case's check order (empty until it is checked).</summary>
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];

    /// <summary>The combined verdict (<see cref="CheckResult.Combine"/>), or null until it is checked.</summary>
    public CheckVerdict? Verdict { get; init; }

    /// <summary>The share of its checks that passed (0 to 1), or null when the case has none or it is not checked yet.</summary>
    public double? Score { get; init; }

    /// <summary>The answer's text, cut at <see cref="MaxTextLength"/>, or null.</summary>
    public string? Text { get; init; }

    /// <summary>The 64-bit SimHash of the answer (Drift's answer fingerprint), or null.</summary>
    public long? Fingerprint { get; init; }

    /// <summary>The model that answered, as the provider named it (<c>responseModel</c>), or null when it did not say.</summary>
    public string? ResponseModel { get; init; }

    /// <summary>Why the answer ended (<c>stop</c>, <c>length</c>, <c>toolUse</c>, ...), or null.</summary>
    public string? StopReason { get; init; }

    /// <summary>Input tokens, or null when unknown.</summary>
    public double? InputTokens { get; init; }

    /// <summary>Output tokens, or null when unknown.</summary>
    public double? OutputTokens { get; init; }

    /// <summary>What it cost, in micro-USD (the list price for a subscription target), or null when unknown.</summary>
    public long? CostMicros { get; init; }

    /// <summary>Whether <see cref="CostMicros"/> is a list price (subscription).</summary>
    public bool ListPrice { get; init; }

    /// <summary>How long the call took, in milliseconds, or null.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Time to the first token, in milliseconds, or null.</summary>
    public long? TtftMs { get; init; }

    /// <summary>Output tokens per second, or null.</summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>Why the call failed, or null.</summary>
    public string? Error { get; init; }

    /// <summary>When it finished, or null.</summary>
    public DateTimeOffset? Finished { get; init; }

    /// <summary>Whether it is finished (done or error).</summary>
    [JsonIgnore]
    public bool IsFinished => Status is ResultStatus.Done or ResultStatus.Error;

    /// <summary>Whether the answer passes: it was answered, and every check passed.</summary>
    [JsonIgnore]
    public bool Passed => Status == ResultStatus.Done && Verdict == CheckVerdict.Passed;

    /// <summary>The same result, checked: <see cref="Verdict"/> and <see cref="Score"/> from <paramref name="checks"/>, an error failing.</summary>
    public SuiteResult WithChecks(IReadOnlyList<CheckResult> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (Status == ResultStatus.Error) return this with { Checks = [.. checks], Verdict = CheckVerdict.Failed, Score = checks.Count == 0 ? null : 0 };
        return this with { Checks = [.. checks], Verdict = CheckResult.Combine(checks), Score = ScoreOf(checks) };
    }

    /// <summary>The share of <paramref name="checks"/> that passed, or null when there are none.</summary>
    public static double? ScoreOf(IReadOnlyCollection<CheckResult> checks) =>
        checks.Count == 0 ? null : (double)checks.Count(check => check.Verdict == CheckVerdict.Passed) / checks.Count;

    /// <summary><paramref name="text"/> cut to <see cref="MaxTextLength"/> (whole characters).</summary>
    public static string? Cut(string? text)
    {
        if (text is null || text.Length <= MaxTextLength) return text;
        var length = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return text[..length];
    }
}

/// <summary>A target's totals in one run (the run page's footer rows and Drift's "other signals").</summary>
/// <param name="TargetId">The target id.</param>
/// <param name="Answers">Answers the run asks of it.</param>
/// <param name="Done">Answers finished (done or error).</param>
/// <param name="Passed">Answers that passed.</param>
/// <param name="MedianLatencyMs">The median duration of its answered calls, or null.</param>
/// <param name="MedianTokensPerSecond">The median output tokens per second, or null.</param>
/// <param name="CostMicros">The metered cost, in micro-USD.</param>
/// <param name="ListMicros">The list-price cost of subscription answers, in micro-USD.</param>
/// <param name="ResponseModels">The response models seen, most answers first.</param>
public sealed record TargetSummary(string TargetId, int Answers, int Done, int Passed, long? MedianLatencyMs, double? MedianTokensPerSecond,
    long CostMicros, long ListMicros, IReadOnlyList<string> ResponseModels)
{
    /// <summary>Passed over finished answers (0 to 1), or null before any finished.</summary>
    public double? PassRate => Done == 0 ? null : (double)Passed / Done;

    /// <summary>The totals of <paramref name="results"/> for <paramref name="targetId"/>, out of <paramref name="answers"/> asked.</summary>
    public static TargetSummary Of(string targetId, int answers, IEnumerable<SuiteResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var mine = results.Where(result => result.TargetId == targetId).ToList();
        var finished = mine.Where(result => result.Status is ResultStatus.Done or ResultStatus.Error).ToList();
        var answered = finished.Where(result => result.Status == ResultStatus.Done).ToList();
        return new TargetSummary(
            targetId,
            answers,
            finished.Count,
            finished.Count(result => result.Passed),
            Median(answered.Where(result => result.DurationMs is not null).Select(result => (double)result.DurationMs!.Value)) is { } latency ? (long)Math.Round(latency) : null,
            Median(answered.Where(result => result.TokensPerSecond is not null).Select(result => result.TokensPerSecond!.Value)),
            mine.Where(result => !result.ListPrice).Sum(result => result.CostMicros ?? 0),
            mine.Where(result => result.ListPrice).Sum(result => result.CostMicros ?? 0),
            [.. answered.Where(result => !string.IsNullOrEmpty(result.ResponseModel)).GroupBy(result => result.ResponseModel!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key)]);
    }

    /// <summary>The median of <paramref name="values"/> (the mean of the middle two for an even count), or null when there are none.</summary>
    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToList();
        if (sorted.Count == 0) return null;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

/// <summary>One cell of the run grid: a case on a target, over its k repeats.</summary>
/// <param name="CaseId">The case id.</param>
/// <param name="TargetId">The target id.</param>
/// <param name="Repeats">The repeats, by repetition (missing ones are not made yet).</param>
/// <param name="Passed">Repeats that passed.</param>
/// <param name="Score">The mean score of its finished, scored repeats, or null.</param>
/// <param name="MedianLatencyMs">The median duration of its answered repeats, or null.</param>
/// <param name="CostMicros">The cost of its repeats (metered and list together; <paramref name="ListPrice"/> says which).</param>
/// <param name="ListPrice">Whether its cost is a list price.</param>
public sealed record SuiteCell(string CaseId, string TargetId, IReadOnlyList<SuiteResult> Repeats, int Passed, double? Score, long? MedianLatencyMs,
    long CostMicros, bool ListPrice)
{
    /// <summary>The cell of <paramref name="caseId"/> on <paramref name="targetId"/> from <paramref name="results"/>.</summary>
    public static SuiteCell Of(string caseId, string targetId, IEnumerable<SuiteResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var repeats = results.Where(result => result.CaseId == caseId && result.TargetId == targetId).OrderBy(result => result.Repetition).ToList();
        var scores = repeats.Where(result => result.IsFinished && result.Score is not null).Select(result => result.Score!.Value).ToList();
        var latency = TargetSummary.Median(repeats.Where(result => result.Status == ResultStatus.Done && result.DurationMs is not null)
            .Select(result => (double)result.DurationMs!.Value));
        return new SuiteCell(caseId, targetId, repeats, repeats.Count(result => result.Passed), scores.Count == 0 ? null : scores.Average(),
            latency is { } ms ? (long)Math.Round(ms) : null, repeats.Sum(result => result.CostMicros ?? 0), repeats.Any(result => result.ListPrice));
    }
}
