// Decision 0011 (run W5, E0): baselines and drift signals (mockups evals-drift: _DriftSuite; evals-run's drift banner; modal-baseline).
//
// A baseline is per suite and target: "Set baseline" picks a run and the targets to re-baseline, and later runs of the suite compare each
// target with its own current baseline. Setting a new one replaces the current one for those targets (the old row stays, Replaced set) and
// clears their open signals; earlier runs and baselines stay in history.
//
// After every run, Drift Watch (E3) compares each target with its baseline and records one DriftSignal per rule that fired, using the
// approved plan's table, which DriftRules.InitialLevel encodes:
//
//   broken case       passed all k before, fails all k now                      suspect
//   pass rate         exact one-sided sign test, p < 0.05, minimum effect size  suspect
//   judge score       the same test on judge scores                             suspect
//   latency           ±50%, outside the baseline's 10th–90th percentile         info
//   tokens/s          ±50%, outside the baseline's 10th–90th percentile         info
//   cost              ±25%, split into price change and verbosity              suspect
//   response model    responseModel changed                                     drift
//   answer fingerprint SimHash outside the baseline's own spread                suspect
//
// A suspect signal becomes drift only when the next run of the suite raises the same rule for the same target again (Confirm); a changed
// response model is drift at once. Info never becomes suspect or drift. A target's level is the worst of its open signals (ok when none).
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Evals;

/// <summary>How far a target has moved from its baseline. Sent as <c>ok</c>, <c>info</c>, <c>suspect</c>, <c>drift</c>; ordered from
/// least to most.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DriftLevel>))]
public enum DriftLevel
{
    /// <summary>No change against the baseline.</summary>
    [JsonStringEnumMemberName("ok")] Ok,

    /// <summary>A speed change only; never becomes suspect or drift.</summary>
    [JsonStringEnumMemberName("info")] Info,

    /// <summary>A change the next run must confirm before it is drift.</summary>
    [JsonStringEnumMemberName("suspect")] Suspect,

    /// <summary>A confirmed change (or a changed response model).</summary>
    [JsonStringEnumMemberName("drift")] Drift,
}

/// <summary>The rule that raised a drift signal. Sent as <c>broken-case</c>, <c>pass-rate</c>, <c>judge-score</c>, <c>latency</c>,
/// <c>tokens-per-second</c>, <c>cost</c>, <c>response-model</c>, <c>fingerprint</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DriftRule>))]
public enum DriftRule
{
    /// <summary>A case that passed all k repeats in the baseline fails all k now.</summary>
    [JsonStringEnumMemberName("broken-case")] BrokenCase,

    /// <summary>The pass rate fell: exact one-sided sign test over cases, p &lt; 0.05, with a minimum effect size.</summary>
    [JsonStringEnumMemberName("pass-rate")] PassRate,

    /// <summary>The judge score fell, by the same test.</summary>
    [JsonStringEnumMemberName("judge-score")] JudgeScore,

    /// <summary>The median latency moved ±50% and outside the baseline's 10th–90th percentile range.</summary>
    [JsonStringEnumMemberName("latency")] Latency,

    /// <summary>The median output tokens per second moved ±50% and outside the baseline's 10th–90th percentile range.</summary>
    [JsonStringEnumMemberName("tokens-per-second")] TokensPerSecond,

    /// <summary>The cost per run moved ±25% (split into price change and verbosity).</summary>
    [JsonStringEnumMemberName("cost")] Cost,

    /// <summary>The model that answered (<c>responseModel</c>) changed.</summary>
    [JsonStringEnumMemberName("response-model")] ResponseModel,

    /// <summary>The answers' SimHash fingerprints moved outside the baseline's own spread.</summary>
    [JsonStringEnumMemberName("fingerprint")] Fingerprint,
}

/// <summary>The approved plan's drift table.</summary>
public static class DriftRules
{
    /// <summary>Every rule, in the order the pages list them.</summary>
    public static IReadOnlyList<DriftRule> All { get; } =
        [DriftRule.ResponseModel, DriftRule.BrokenCase, DriftRule.PassRate, DriftRule.JudgeScore, DriftRule.Fingerprint, DriftRule.Cost,
         DriftRule.Latency, DriftRule.TokensPerSecond];

    /// <summary>The p-value below which the sign test is significant.</summary>
    public const double SignificanceLevel = 0.05;

    /// <summary>The share a latency or tokens/s median must move to count (±50%).</summary>
    public const double SpeedChange = 0.5;

    /// <summary>The share the cost per run must move to count (±25%).</summary>
    public const double CostChange = 0.25;

    /// <summary>The level a rule raises when it first fires: response model drift, latency and tokens/s info, the rest suspect.</summary>
    public static DriftLevel InitialLevel(DriftRule rule) => rule switch
    {
        DriftRule.ResponseModel => DriftLevel.Drift,
        DriftRule.Latency or DriftRule.TokensPerSecond => DriftLevel.Info,
        _ => DriftLevel.Suspect,
    };

    /// <summary>Whether a rule's signal waits for the next run to confirm it (it starts as suspect).</summary>
    public static bool NeedsConfirmation(DriftRule rule) => InitialLevel(rule) == DriftLevel.Suspect;

    /// <summary>How the pages name a rule.</summary>
    public static string Label(DriftRule rule) => rule switch
    {
        DriftRule.BrokenCase => "Broken case",
        DriftRule.PassRate => "Pass rate",
        DriftRule.JudgeScore => "Judge score",
        DriftRule.Latency => "Latency",
        DriftRule.TokensPerSecond => "Tokens/s",
        DriftRule.Cost => "Cost",
        DriftRule.ResponseModel => "Response model",
        DriftRule.Fingerprint => "Answer fingerprint",
        _ => rule.ToString(),
    };

    /// <summary>The worst level of <paramref name="signals"/> that are open (not cleared); ok when none.</summary>
    public static DriftLevel Worst(IEnumerable<DriftSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var worst = DriftLevel.Ok;
        foreach (var signal in signals)
            if (signal.Cleared is null && signal.Level > worst) worst = signal.Level;
        return worst;
    }
}

/// <summary>A suite target's accepted baseline: the run later runs compare it with.</summary>
public sealed record Baseline
{
    /// <summary>The suite id.</summary>
    public required string SuiteId { get; init; }

    /// <summary>The target id (<see cref="SuiteTarget.Id"/>).</summary>
    public required string TargetId { get; init; }

    /// <summary>The suite run it is (<see cref="EvalIds.Run"/>).</summary>
    public required string RunId { get; init; }

    /// <summary>The target's identity when it was accepted.</summary>
    public required ModelIdentity Identity { get; init; }

    /// <summary>The response models the target answered as in that run, most answers first (what a response-model change is against).</summary>
    public IReadOnlyList<string> ResponseModels { get; init; } = [];

    /// <summary>Whether the cases' reference outputs were replaced with this run's answers when it was accepted.</summary>
    public bool ReplacedReferences { get; init; }

    /// <summary>When it was accepted.</summary>
    public DateTimeOffset Accepted { get; init; }

    /// <summary>When a newer baseline replaced it, or null while it is current.</summary>
    public DateTimeOffset? Replaced { get; init; }

    /// <summary>Whether it is the target's current baseline.</summary>
    [JsonIgnore]
    public bool Current => Replaced is null;
}

/// <summary>One drift signal: a rule that fired for one target in one run.</summary>
public sealed record DriftSignal
{
    /// <summary>The longest message.</summary>
    public const int MaxMessage = 1000;

    /// <summary>The signal's id in the store, or 0 before it is saved.</summary>
    public long Id { get; init; }

    /// <summary>The suite id.</summary>
    public required string SuiteId { get; init; }

    /// <summary>The target id.</summary>
    public required string TargetId { get; init; }

    /// <summary>The run that raised it (or, once confirmed, the run that confirmed it).</summary>
    public required string RunId { get; init; }

    /// <summary>The baseline run it compared with.</summary>
    public required string BaselineRunId { get; init; }

    /// <summary>The run that saw it first ("Run #20 saw it first and run #21 saw it again"); equal to <see cref="RunId"/> until confirmed.</summary>
    public string? FirstRunId { get; init; }

    /// <summary>The rule.</summary>
    public DriftRule Rule { get; init; }

    /// <summary>Its level.</summary>
    public DriftLevel Level { get; init; }

    /// <summary>What the page says (<c>Pass rate fell from 0.92 to 0.71 (22 → 17 of 24 answers). 5 of 8 cases got worse and none improved
    /// (one-sided sign test, p = 0.031).</c>).</summary>
    public required string Message { get; init; }

    /// <summary>The one-sided p-value, for the sign-test rules, or null.</summary>
    public double? PValue { get; init; }

    /// <summary>The size of the change as a signed share (−0.23 for a 23% fall), or null.</summary>
    public double? Effect { get; init; }

    /// <summary>The numbers behind it, for the page and the download (rule-specific), or null.</summary>
    public JsonObject? Detail { get; init; }

    /// <summary>When it was raised.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When a new baseline cleared it, or null while it is open.</summary>
    public DateTimeOffset? Cleared { get; init; }

    /// <summary>Whether it is a suspect signal waiting for the next run.</summary>
    [JsonIgnore]
    public bool AwaitingConfirmation => Level == DriftLevel.Suspect && Cleared is null;

    /// <summary>A new signal for <paramref name="rule"/> at the rule's first level.</summary>
    public static DriftSignal Raise(string suiteId, string targetId, string runId, string baselineRunId, DriftRule rule, string message,
        DateTimeOffset now, double? pValue = null, double? effect = null, JsonObject? detail = null) =>
        new DriftSignal
        {
            SuiteId = suiteId,
            TargetId = targetId,
            RunId = runId,
            BaselineRunId = baselineRunId,
            FirstRunId = runId,
            Rule = rule,
            Level = DriftRules.InitialLevel(rule),
            Message = message,
            PValue = pValue,
            Effect = effect,
            Detail = detail,
            Created = now,
        }.Normalize();

    /// <summary>This suspect signal confirmed by <paramref name="next"/>, the same rule for the same target in the next run: drift, with
    /// the next run's message and numbers, remembering which run saw it first.</summary>
    /// <exception cref="InvalidOperationException">This signal is not suspect, or <paramref name="next"/> is another rule or target.</exception>
    public DriftSignal Confirm(DriftSignal next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!AwaitingConfirmation) throw new InvalidOperationException("Only an open suspect signal can be confirmed.");
        if (next.Rule != Rule || next.TargetId != TargetId || next.SuiteId != SuiteId) throw new InvalidOperationException("A signal is confirmed by the same rule for the same target.");
        if (next.RunId == RunId) throw new InvalidOperationException("A signal is confirmed by the next run, not its own.");
        return next with { Level = DriftLevel.Drift, FirstRunId = FirstRunId ?? RunId, Id = 0 };
    }

    /// <summary>The same signal, checked: ids valid, a message on one paragraph within <see cref="MaxMessage"/>, a level the rule can reach.</summary>
    /// <exception cref="ArgumentException">Something is not allowed.</exception>
    public DriftSignal Normalize()
    {
        EvalIds.Require(EvalIds.Suite, SuiteId, "suite");
        EvalIds.Require(EvalIds.Run, RunId, "suite run");
        EvalIds.Require(EvalIds.Run, BaselineRunId, "suite run");
        if (FirstRunId is not null) EvalIds.Require(EvalIds.Run, FirstRunId, "suite run");
        if (string.IsNullOrWhiteSpace(TargetId)) throw new ArgumentException("A drift signal names its target.");
        if (!Enum.IsDefined(Rule) || !Enum.IsDefined(Level)) throw new ArgumentException("Unknown drift rule or level.");
        var initial = DriftRules.InitialLevel(Rule);
        if (Level != DriftLevel.Ok && Level != initial && !(initial == DriftLevel.Suspect && Level == DriftLevel.Drift))
            throw new ArgumentException($"The {DriftRules.Label(Rule).ToLowerInvariant()} rule cannot raise {Level.ToString().ToLowerInvariant()}.");
        var message = Message?.Trim() ?? "";
        if (message.Length == 0) throw new ArgumentException("A drift signal says what changed.");
        if (message.Length > MaxMessage) message = message[..MaxMessage];
        if (PValue is { } p && (!double.IsFinite(p) || p is < 0 or > 1)) throw new ArgumentException("A p-value is 0 to 1.");
        if (Effect is { } effect && !double.IsFinite(effect)) throw new ArgumentException("An effect is a finite number.");
        return this with { Message = message };
    }
}
