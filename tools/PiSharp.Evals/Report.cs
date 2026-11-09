namespace PiSharp.Evals;

public sealed record EvalMetrics(double? InputTokens = null, double? OutputTokens = null, double? CacheReadTokens = null,
    double? CacheWriteTokens = null, double? TotalTokens = null, double? ToolCalls = null, double? TotalMs = null, double? EstimatedCostUsd = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valid => Values.All(v => v is null || double.IsFinite(v.Value) && v >= 0);
    [System.Text.Json.Serialization.JsonIgnore]
    public double?[] Values => [InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, TotalTokens, ToolCalls, TotalMs, EstimatedCostUsd];
    public static string[] Names { get; } = ["inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "totalTokens", "toolCalls", "totalMs", "estimatedCostUsd"];
}
public sealed record EvalResult(EvalTask Task, EvalStatus Status, double? Score = null, EvalMetrics? Metrics = null, string? Error = null);
public sealed record BlockedPair(string PairId, string[] Reasons);
public sealed record MetricComparison(int EligiblePairs, double? ControlMean, double? TreatmentMean, double? MeanDelta);
public sealed record MetricTotal(int AvailableRuns, double? Total);
public sealed record ArmTotals(DocumentationArm Arm, int Runs, SortedDictionary<string, MetricTotal> Metrics);
public sealed record CategoryComparison(string Category, int TotalPairs, int EligiblePairs, int BlockedPairs,
    double? ControlMean, double? TreatmentMean, double? Lift, SortedDictionary<string, MetricComparison> Metrics);
public sealed record EvalReport(int SchemaVersion, string ProtocolDigest, string Evidence, CategoryComparison[] Comparisons,
    BlockedPair[] BlockedPairs, ArmTotals[] OperationalTotals, EvalResult[] Results)
{
    public int ExitCode => BlockedPairs.Length == 0 ? 0 : 1;
    public static EvalReport Create(IReadOnlyList<EvalTask> plan, IEnumerable<EvalResult> observations, string evidence = "synthetic fixture; no provider execution")
    {
        if (plan.Count == 0 || plan.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != plan.Count)
            throw new ArgumentException("Plan is empty or contains duplicate arms.");
        var results = observations.OrderBy(r => r.Task.Id, StringComparer.Ordinal).ToArray();
        var expected = plan.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var blocked = new List<BlockedPair>();
        var eligible = new List<(EvalTask Task, EvalResult Control, EvalResult Treatment)>();
        foreach (var group in plan.GroupBy(t => t.PairId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var reasons = new List<string>();
            var runs = results.Where(r => r.Task.PairId == group.Key).ToArray();
            foreach (var arm in Enum.GetValues<DocumentationArm>())
            {
                var planned = group.Where(t => t.Arm == arm).ToArray();
                var observed = runs.Where(r => r.Task.Arm == arm).ToArray();
                if (planned.Length != 1 || observed.Length != 1) { reasons.Add($"{arm}: missing or duplicate arm"); continue; }
                var r = observed[0];
                if (r.Task != planned[0]) reasons.Add($"{arm}: case content differs from plan");
                if (r.Status != EvalStatus.scored) reasons.Add($"{arm}: {r.Status}");
                if (r.Score is not { } score || !double.IsFinite(score) || score < 0 || score > 1) reasons.Add($"{arm}: invalid score");
                if (r.Metrics?.Valid == false) reasons.Add($"{arm}: invalid metrics");
            }
            if (runs.Any(r => !Enum.IsDefined(r.Task.Arm))) reasons.Add("invalid arm");
            if (reasons.Count != 0) blocked.Add(new(group.Key, reasons.ToArray()));
            else eligible.Add((group.First(), runs.Single(r => r.Task.Arm == DocumentationArm.without_docs), runs.Single(r => r.Task.Arm == DocumentationArm.with_docs)));
        }
        foreach (var unexpected in results.Where(r => !expected.Contains(r.Task.Id)).GroupBy(r => r.Task.PairId))
            blocked.Add(new(unexpected.Key, ["unexpected observation"]));
        var comparisons = plan.Select(t => t.Case.Category).Distinct().Order(StringComparer.Ordinal).Select(category =>
        {
            var pairs = eligible.Where(p => p.Task.Case.Category == category).ToArray();
            var total = plan.Where(t => t.Case.Category == category).Select(t => t.PairId).Distinct().Count();
            var metrics = new SortedDictionary<string, MetricComparison>(StringComparer.Ordinal);
            for (int i = 0; i < EvalMetrics.Names.Length; i++)
            {
                var values = pairs.Select(p => (C: p.Control.Metrics?.Values[i], T: p.Treatment.Metrics?.Values[i])).Where(p => p.C is not null && p.T is not null).ToArray();
                metrics[EvalMetrics.Names[i]] = new(values.Length, Mean(values.Select(p => p.C)), Mean(values.Select(p => p.T)), Mean(values.Select(p => p.T - p.C)));
            }
            var c = Mean(pairs.Select(p => p.Control.Score)); var t = Mean(pairs.Select(p => p.Treatment.Score));
            return new CategoryComparison(category, total, pairs.Length, total - pairs.Length, c, t, t - c, metrics);
        }).ToArray();
        var totals = Enum.GetValues<DocumentationArm>().Select(arm =>
        {
            var runs = results.Where(r => r.Task.Arm == arm && expected.Contains(r.Task.Id)).ToArray();
            var metrics = new SortedDictionary<string, MetricTotal>(StringComparer.Ordinal);
            for (int i = 0; i < EvalMetrics.Names.Length; i++)
            {
                var values = runs.Where(r => r.Metrics?.Valid == true).Select(r => r.Metrics!.Values[i]).Where(v => v.HasValue).ToArray();
                metrics[EvalMetrics.Names[i]] = new(values.Length, values.Length == 0 ? null : values.Sum());
            }
            return new ArmTotals(arm, runs.Length, metrics);
        }).ToArray();
        var protocol = new { schemaVersion = 1, scoring = "ordinal-trimmed-answer-v1", schedule = "alternating-per-repetition-v1", evidence, tasks = plan.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray() };
        return new(1, EvalJson.Hash(EvalJson.Serialize(protocol)), evidence, comparisons, blocked.OrderBy(b => b.PairId, StringComparer.Ordinal).ToArray(), totals, results);
    }
    private static double? Mean(IEnumerable<double?> source) { var values = source.ToArray(); return values.Length == 0 ? null : values.Average(); }
}
