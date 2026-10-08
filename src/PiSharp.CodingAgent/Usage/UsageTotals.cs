// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/usage-totals.ts.
using System.Globalization;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Usage;

/// <summary>usage-totals.ts <c>UsageTotals</c>: running sums with JavaScript number (binary64) arithmetic.</summary>
public sealed class UsageTotals
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double CacheWrite { get; set; }
    public double Cost { get; set; }
}

/// <summary>usage-totals.ts <c>UsageCostBreakdownEntry</c>.</summary>
public sealed record UsageCostBreakdownEntry(string Key, double Cost, double Tokens);

public static class UsageTotalsCalculator
{
    /// <summary>The bucket for tool-result, branch-summary and compaction usage.</summary>
    public const string ToolsAndSummariesKey = "Tools/summaries";

    public static UsageTotals CreateUsageTotals() => new();

    public static void AddUsageToTotals(UsageTotals totals, TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(totals); ArgumentNullException.ThrowIfNull(usage);
        totals.Input += usage.Input; totals.Output += usage.Output; totals.CacheRead += usage.CacheRead; totals.CacheWrite += usage.CacheWrite;
        totals.Cost += (double)usage.Cost.Total;
    }

    /// <summary>Sum of two usages, keeping the optional token splits (<c>cacheWrite1h</c>, <c>reasoning</c>) when either side
    /// reports them. Like the source, the result carries no other extra usage or cost fields.</summary>
    public static TokenUsage CombineUsage(TokenUsage first, TokenUsage second)
    {
        ArgumentNullException.ThrowIfNull(first); ArgumentNullException.ThrowIfNull(second);
        var extras = JsonFields.Empty;
        foreach (var name in new[] { "cacheWrite1h", "reasoning" })
        {
            var (a, hasA) = Split(first, name); var (b, hasB) = Split(second, name);
            if (hasA || hasB) extras = extras.Set(name, JsonData.Parse(Number(a + b)));
        }
        return new(first.Input + second.Input, first.Output + second.Output, first.CacheRead + second.CacheRead,
            first.CacheWrite + second.CacheWrite, first.TotalTokens + second.TotalTokens,
            new(first.Cost.Input + second.Cost.Input, first.Cost.Output + second.Cost.Output, first.Cost.CacheRead + second.Cost.CacheRead,
                first.Cost.CacheWrite + second.Cost.CacheWrite, first.Cost.Total + second.Cost.Total),
            extras.Values.Count == 0 ? null : extras);
    }

    /// <summary>Group model-attributed usage by <c>provider/model</c> (assistant messages use <c>responseModel</c> when present)
    /// and all other usage into <see cref="ToolsAndSummariesKey"/>; empty groups are dropped and the rest sorted by cost,
    /// highest first (stable, as Array.prototype.sort).</summary>
    public static IReadOnlyList<UsageCostBreakdownEntry> GetUsageCostBreakdown(IEnumerable<SessionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var totalsByKey = new List<(string Key, UsageTotals Totals)>(); var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!TryAttribute(entry, out var key, out var usage)) continue;
            if (!index.TryGetValue(key, out var position)) { position = totalsByKey.Count; index[key] = position; totalsByKey.Add((key, new())); }
            var totals = totalsByKey[position].Totals;
            totals.Input += UsageWire.Number(usage, "input"); totals.Output += UsageWire.Number(usage, "output");
            totals.CacheRead += UsageWire.Number(usage, "cacheRead"); totals.CacheWrite += UsageWire.Number(usage, "cacheWrite");
            totals.Cost += UsageWire.CostTotal(usage);
        }
        return [.. totalsByKey.Select(item => new UsageCostBreakdownEntry(item.Key, item.Totals.Cost,
                item.Totals.Input + item.Totals.Output + item.Totals.CacheRead + item.Totals.CacheWrite))
            .Where(item => item.Cost > 0 || item.Tokens > 0).OrderByDescending(item => item.Cost)];
    }

    private static bool TryAttribute(SessionEntry entry, out string key, out JsonElement usage)
    {
        key = ""; usage = default; var body = entry.WireBody.Value;
        if (entry.Kind == SessionEntryKind.Message && body.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            var role = message.TryGetProperty("role", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (role == "assistant" && message.TryGetProperty("usage", out usage))
            {
                key = UsageWire.String(message, "provider") + "/" + (UsageWire.OptionalString(message, "responseModel") ?? UsageWire.String(message, "model"));
                return usage.ValueKind == JsonValueKind.Object;
            }
            if (role == "toolResult" && message.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
            { key = ToolsAndSummariesKey; return true; }
            return false;
        }
        if (entry.Kind == SessionEntryKind.Usage && body.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
        { key = UsageWire.String(body, "provider") + "/" + UsageWire.String(body, "model"); return true; }
        if (entry.Kind is SessionEntryKind.BranchSummary or SessionEntryKind.Compaction && body.TryGetProperty("usage", out usage) &&
            usage.ValueKind == JsonValueKind.Object)
        { key = ToolsAndSummariesKey; return true; }
        return false;
    }

    private static (long Value, bool Present) Split(TokenUsage usage, string name) =>
        usage.ExtraProperties is { } extras && extras.TryGet(name, out var value) && value is not null && value.Value.ValueKind == JsonValueKind.Number
            ? (value.Value.TryGetInt64(out var count) ? count : (long)value.Value.GetDouble(), true) : (0, false);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Reads Pi usage objects from session wire bodies with JavaScript number semantics.</summary>
internal static class UsageWire
{
    internal static double Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number ? number.GetDouble() : 0;

    internal static double CostTotal(JsonElement usage) =>
        usage.TryGetProperty("cost", out var cost) ? Number(cost, "total") : 0;

    internal static double Cost(JsonElement usage, string name) =>
        usage.TryGetProperty("cost", out var cost) ? Number(cost, name) : 0;

    internal static string String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "undefined";

    internal static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
}
