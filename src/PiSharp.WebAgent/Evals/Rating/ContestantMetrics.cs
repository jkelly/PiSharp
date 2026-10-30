// Decision 0011 (run W5, E1): the leaderboard's cost and speed columns (mockup evals-leaderboard: Median cost, TTFT p50, Tokens/s in request
// tags; Cost per run, Run time, Check in agent tags; the cost cell's tooltip "Median answer 1,240 input and 2,100 output tokens"). They come
// from the run records of the answers the user voted on in that tag, never from a model's catalog: one ContestantMetrics per contestant of a
// counted battle (ArenaMetrics reads runs.db, and agent compare's store for an agent run's check, turns and files), and ModelMetrics takes
// their medians. A subscription answer's cost is its list price ("≈ $0.0235 list"), so ListPrice says when any of the costs is one.
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals;

/// <summary>What one contestant's answer (request mode) or agent run (agent mode) cost and how fast it was.</summary>
public sealed record ContestantMetrics
{
    /// <summary>The cost in micro-USD (an agent run's: its calls' total; list price when <see cref="ListPrice"/>), or null when unknown.</summary>
    public long? CostMicros { get; init; }

    /// <summary>Whether <see cref="CostMicros"/> is a subscription's list price.</summary>
    public bool ListPrice { get; init; }

    /// <summary>Milliseconds to the first token, or null (agent runs have none).</summary>
    public long? TtftMs { get; init; }

    /// <summary>Output tokens per second, or null.</summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>Milliseconds from start to end (an agent run's run time), or null.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Input tokens, or null when none were reported.</summary>
    public double? InputTokens { get; init; }

    /// <summary>Output tokens, or null when none were reported.</summary>
    public double? OutputTokens { get; init; }

    /// <summary>An agent run's turns, or null.</summary>
    public int? Turns { get; init; }

    /// <summary>The files an agent run changed, or null.</summary>
    public int? FilesChanged { get; init; }

    /// <summary>How an agent run's check command ended (<see cref="AgentCheckState"/>: passed, failed, timeout, error, stopped), or null when
    /// it had none.</summary>
    public string? Check { get; init; }

    /// <summary>The check's test summary ("38 passed"), or null.</summary>
    public string? CheckSummary { get; init; }

    /// <summary>The metrics of a request-mode answer: its settled cost (list price for a subscription), time to first token, tokens per
    /// second, duration and usage.</summary>
    public static ContestantMetrics FromRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new ContestantMetrics
        {
            CostMicros = run.CostMicros,
            ListPrice = IsList(run.Billing),
            TtftMs = run.TtftMs,
            TokensPerSecond = run.TokensPerSecond is { } speed && double.IsFinite(speed) && speed > 0 ? speed : null,
            DurationMs = run.DurationMs,
            InputTokens = run.Usage.Input > 0 ? run.Usage.Input : null,
            OutputTokens = run.Usage.Output > 0 ? run.Usage.Output : null,
        };
    }

    /// <summary>The metrics of an agent-mode run: <paramref name="model"/> is the model's runs.db row (its run time and billing),
    /// <paramref name="calls"/> the totals of its calls (cost and usage), <paramref name="state"/> its agent compare state (check and files
    /// changed) and <paramref name="turns"/> its turns; each may be missing.</summary>
    public static ContestantMetrics FromAgentRun(RunRecord model, RunTotals? calls, AgentCompareModelState? state, int? turns)
    {
        ArgumentNullException.ThrowIfNull(model);
        var counted = calls is { Count: > 0 };
        return new ContestantMetrics
        {
            CostMicros = counted ? calls!.CostMicros : model.CostMicros,
            ListPrice = IsList(model.Billing),
            DurationMs = model.DurationMs,
            InputTokens = counted && calls!.InputTokens > 0 ? calls.InputTokens : null,
            OutputTokens = counted && calls!.OutputTokens > 0 ? calls.OutputTokens : null,
            Turns = turns is > 0 ? turns : null,
            FilesChanged = state?.Diff?.Files,
            Check = state?.Check?.State,
            CheckSummary = state?.Check?.Summary,
        };
    }

    private static bool IsList(string? billing) => string.Equals(billing, "subscription", StringComparison.Ordinal);
}

/// <summary>A leaderboard row's cost and speed: medians over the answers (or agent runs) voted on in the tag.</summary>
public sealed record ModelMetrics
{
    /// <summary>No answers measured.</summary>
    public static ModelMetrics None { get; } = new();

    /// <summary>Answers (or runs) measured: those whose records are still stored.</summary>
    public int Answers { get; init; }

    /// <summary>The median cost in micro-USD, or null when no cost is known.</summary>
    public long? MedianCostMicros { get; init; }

    /// <summary>Whether some of the costs are list prices (a subscription's): the page shows "≈ ... list".</summary>
    public bool ListPrice { get; init; }

    /// <summary>Whether some costs are list prices and some metered (the model was used through two kinds of account).</summary>
    public bool MixedBilling { get; init; }

    /// <summary>The median input tokens, or null.</summary>
    public double? MedianInputTokens { get; init; }

    /// <summary>The median output tokens, or null.</summary>
    public double? MedianOutputTokens { get; init; }

    /// <summary>The median time to first token in milliseconds (TTFT p50), or null.</summary>
    public long? MedianTtftMs { get; init; }

    /// <summary>The median output tokens per second, or null.</summary>
    public double? MedianTokensPerSecond { get; init; }

    /// <summary>The median duration in milliseconds (an agent tag's run time), or null.</summary>
    public long? MedianDurationMs { get; init; }

    /// <summary>The median turns of an agent run, or null.</summary>
    public double? MedianTurns { get; init; }

    /// <summary>The median files an agent run changed, or null.</summary>
    public double? MedianFilesChanged { get; init; }

    /// <summary>Agent runs whose check command ran.</summary>
    public int Checks { get; init; }

    /// <summary>Agent runs whose check passed.</summary>
    public int ChecksPassed { get; init; }

    /// <summary>How the latest voted run's check ended (<see cref="AgentCheckState"/>), or null.</summary>
    public string? LatestCheck { get; init; }

    /// <summary>The latest check's test summary, or null.</summary>
    public string? LatestCheckSummary { get; init; }

    /// <summary>The medians of <paramref name="answers"/>, oldest first (the last one's check is <see cref="LatestCheck"/>).</summary>
    public static ModelMetrics Of(IEnumerable<ContestantMetrics> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var list = answers.Where(answer => answer is not null).ToList();
        if (list.Count == 0) return None;
        var costs = list.Where(answer => answer.CostMicros is not null).ToList();
        var checks = list.Where(answer => answer.Check is not null).ToList();
        return new ModelMetrics
        {
            Answers = list.Count,
            MedianCostMicros = Rounded(TargetSummary.Median(costs.Select(answer => (double)answer.CostMicros!.Value))),
            ListPrice = costs.Any(answer => answer.ListPrice),
            MixedBilling = costs.Any(answer => answer.ListPrice) && costs.Any(answer => !answer.ListPrice),
            MedianInputTokens = TargetSummary.Median(list.Where(answer => answer.InputTokens is not null).Select(answer => answer.InputTokens!.Value)),
            MedianOutputTokens = TargetSummary.Median(list.Where(answer => answer.OutputTokens is not null).Select(answer => answer.OutputTokens!.Value)),
            MedianTtftMs = Rounded(TargetSummary.Median(list.Where(answer => answer.TtftMs is not null).Select(answer => (double)answer.TtftMs!.Value))),
            MedianTokensPerSecond = TargetSummary.Median(list.Where(answer => answer.TokensPerSecond is not null).Select(answer => answer.TokensPerSecond!.Value)),
            MedianDurationMs = Rounded(TargetSummary.Median(list.Where(answer => answer.DurationMs is not null).Select(answer => (double)answer.DurationMs!.Value))),
            MedianTurns = TargetSummary.Median(list.Where(answer => answer.Turns is not null).Select(answer => (double)answer.Turns!.Value)),
            MedianFilesChanged = TargetSummary.Median(list.Where(answer => answer.FilesChanged is not null).Select(answer => (double)answer.FilesChanged!.Value)),
            Checks = checks.Count,
            ChecksPassed = checks.Count(answer => answer.Check == AgentCheckState.Passed),
            LatestCheck = checks.LastOrDefault()?.Check,
            LatestCheckSummary = checks.LastOrDefault()?.CheckSummary,
        };
    }

    private static long? Rounded(double? value) => value is { } number ? (long)Math.Round(number, MidpointRounding.AwayFromZero) : null;
}
