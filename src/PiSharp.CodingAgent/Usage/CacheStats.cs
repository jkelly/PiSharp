// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/cache-stats.ts.
using System.Text.Json;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Usage;

/// <summary>A counted cache miss on a single assistant message.</summary>
/// <param name="MissedTokens">Prompt tokens that were in the previous turn's prompt but not read from cache.</param>
/// <param name="MissedCost">Extra dollars paid vs. a full cache hit; 0 when pricing is unknown.</param>
/// <param name="IdleMs">Milliseconds since the previous request (which last refreshed the cache).</param>
/// <param name="ModelChanged">True when the model changed relative to the previous request.</param>
public sealed record CacheMiss(double MissedTokens, double MissedCost, double IdleMs, bool ModelChanged);

/// <param name="MissCount">Number of counted misses (turns above the noise floor).</param>
public sealed record CacheWasteTotals(double MissedTokens, double MissedCost, int MissCount);

/// <summary>cache-stats.ts <c>ModelPriceSource</c>: minimal pricing lookup, satisfied by the model runtime. Rates are $/million tokens.</summary>
public interface IModelPriceSource
{
    ModelCost? GetModelCost(string provider, string modelId);
}

/// <summary>A <see cref="IModelPriceSource"/> over a delegate.</summary>
public sealed class DelegateModelPriceSource(Func<string, string, ModelCost?> lookup) : IModelPriceSource
{
    public ModelCost? GetModelCost(string provider, string modelId) => lookup(provider, modelId);
}

public static class CacheStats
{
    /// <summary>Prompt-cache TTL: idle gaps longer than this are worth mentioning as the likely cause of a miss.
    /// Anthropic's default cache TTL is 5 minutes.</summary>
    public const double CacheTtlMs = 5 * 60 * 1000;

    /// <summary>Per-turn misses at or below this are cache breakpoint granularity noise.</summary>
    internal const double NoiseFloorTokens = 1024;

    /// <summary>The fields of an assistant message the scan reads, with JavaScript number semantics.</summary>
    internal readonly record struct Turn(string Provider, string Model, double Timestamp, double Input, double CacheRead, double CacheWrite,
        double CostInput, double CostCacheRead, double CostCacheWrite)
    {
        internal double PromptTokens => Input + CacheRead + CacheWrite;
        internal string ModelKey => Provider + "/" + Model;

        internal static Turn From(AssistantMessage message) => new(message.Provider, message.Model, message.Timestamp,
            message.Usage.Input, message.Usage.CacheRead, message.Usage.CacheWrite,
            (double)message.Usage.Cost.Input, (double)message.Usage.Cost.CacheRead, (double)message.Usage.Cost.CacheWrite);

        internal static Turn From(JsonElement message)
        {
            var usage = message.TryGetProperty("usage", out var value) ? value : default;
            return new(UsageWire.String(message, "provider"), UsageWire.String(message, "model"), UsageWire.Number(message, "timestamp"),
                UsageWire.Number(usage, "input"), UsageWire.Number(usage, "cacheRead"), UsageWire.Number(usage, "cacheWrite"),
                Cost(usage, "input"), Cost(usage, "cacheRead"), Cost(usage, "cacheWrite"));
            static double Cost(JsonElement usage, string name) => usage.ValueKind == JsonValueKind.Object ? UsageWire.Cost(usage, name) : 0;
        }
    }

    /// <summary>The last request seen by the scan; everything in its prompt should be cached. <see cref="ReportedCache"/> is
    /// sticky: some earlier request in this scan segment reported cache activity.</summary>
    private sealed record PreviousRequest(double PromptTokens, string ModelKey, double Timestamp, bool ReportedCache);

    /// <summary>Cumulative cache waste across a session: prompt tokens that should have been cache reads (they were in the
    /// previous turn's prompt) but were re-billed.</summary>
    public static CacheWasteTotals ComputeCacheWaste(IEnumerable<SessionEntry> entries, IModelPriceSource models) => Scan(entries, models).Totals;

    /// <summary>All counted cache misses across a session, keyed by the session entry (by reference) whose assistant message
    /// paid for them. The source keys by the message object; PiSharp's messages are values, so the entry is the identity.</summary>
    public static IReadOnlyDictionary<SessionEntry, CacheMiss> CollectCacheMisses(IEnumerable<SessionEntry> entries, IModelPriceSource models) =>
        Scan(entries, models).Misses;

    /// <summary>Detect a cache miss on a just-completed assistant message. <paramref name="entries"/> must not yet contain the
    /// message (message_end fires before persistence).</summary>
    public static CacheMiss? DetectCacheMiss(IEnumerable<SessionEntry> entries, AssistantMessage message, IModelPriceSource models)
    {
        ArgumentNullException.ThrowIfNull(message);
        return DetectMiss(Scan(entries, models).Previous, Turn.From(message), models);
    }

    private static CacheMiss? DetectMiss(PreviousRequest? previous, Turn message, IModelPriceSource models)
    {
        var promptTokens = message.PromptTokens;
        // A zero-cache turn only counts when cache activity was reported before: on cache-read-only providers that is a
        // total miss, while on providers that never report caching it means nothing.
        if (previous is null || promptTokens <= 0 || (message.CacheRead + message.CacheWrite == 0 && !previous.ReportedCache)) return null;
        var missedTokens = Math.Min(previous.PromptTokens, promptTokens) - message.CacheRead;
        if (missedTokens <= NoiseFloorTokens) return null;
        // Extra cost = missed tokens billed at the actual paid rate (input/cacheWrite, incl. write premium) instead of the
        // cache-read rate. Missed tokens can only land in the input or cacheWrite buckets.
        var paidTokens = message.Input + message.CacheWrite;
        var paidPerToken = paidTokens > 0 ? (message.CostInput + message.CostCacheWrite) / paidTokens : 0;
        var readPerToken = message.CacheRead > 0 ? message.CostCacheRead / message.CacheRead
            : (models.GetModelCost(message.Provider, message.Model)?.CacheRead ?? 0) / 1_000_000;
        return new(missedTokens, missedTokens * Math.Max(0, paidPerToken - readPerToken), Math.Max(0, message.Timestamp - previous.Timestamp),
            message.ModelKey != previous.ModelKey);
    }

    private static PreviousRequest? AsPreviousRequest(Turn message, bool reportedCache)
    {
        var promptTokens = message.PromptTokens;
        if (promptTokens <= 0) return null;
        return new(promptTokens, message.ModelKey, message.Timestamp, reportedCache || message.CacheRead + message.CacheWrite > 0);
    }

    private static (PreviousRequest? Previous, CacheWasteTotals Totals, Dictionary<SessionEntry, CacheMiss> Misses) Scan(
        IEnumerable<SessionEntry> entries, IModelPriceSource models)
    {
        ArgumentNullException.ThrowIfNull(entries); ArgumentNullException.ThrowIfNull(models);
        PreviousRequest? previous = null; double missedTokens = 0, missedCost = 0; var missCount = 0;
        var misses = new Dictionary<SessionEntry, CacheMiss>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            var body = entry.WireBody.Value;
            // The context legitimately changed; the next turn's prompt is new content, not re-billed content. Model switches
            // are NOT exempt: they re-bill the full prompt and should be counted.
            if (entry.Kind is SessionEntryKind.Compaction or SessionEntryKind.BranchSummary) { previous = null; continue; }
            if (entry.Kind == SessionEntryKind.Usage && UsageWire.OptionalString(body, "kind") == "cache_warm")
            {
                var usage = body.GetProperty("usage");
                var promptTokens = UsageWire.Number(usage, "input") + UsageWire.Number(usage, "cacheRead") + UsageWire.Number(usage, "cacheWrite");
                if (promptTokens > 0)
                    previous = new(promptTokens, UsageWire.String(body, "provider") + "/" + UsageWire.String(body, "model"),
                        JsDate.Parse(entry.Timestamp), true);
            }
            else if (entry.Kind == SessionEntryKind.Message && body.TryGetProperty("message", out var message) &&
                UsageWire.OptionalString(message, "role") == "assistant")
            {
                var turn = Turn.From(message);
                if (DetectMiss(previous, turn, models) is { } miss)
                { missedTokens += miss.MissedTokens; missedCost += miss.MissedCost; missCount++; misses[entry] = miss; }
                previous = AsPreviousRequest(turn, previous?.ReportedCache ?? false) ?? previous;
            }
        }
        return (previous, new(missedTokens, missedCost, missCount), misses);
    }
}

/// <summary>JavaScript <c>Date.parse</c> for the ISO timestamps Pi writes: milliseconds since the epoch, NaN when unparseable.
/// Date-time forms without an offset are local time and date-only forms are UTC, as ECMAScript specifies.</summary>
internal static class JsDate
{
    private static readonly string[] Offset = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK"];
    private static readonly string[] Local = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm"];
    private static readonly string[] DateOnly = ["yyyy-MM-dd", "yyyy-MM", "yyyy"];

    internal static double Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return double.NaN;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if ((value.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(value, "[+-][0-9]{2}:[0-9]{2}$")) &&
            DateTimeOffset.TryParseExact(value, Offset, culture, System.Globalization.DateTimeStyles.None, out var withOffset))
            return withOffset.ToUnixTimeMilliseconds();
        if (DateTimeOffset.TryParseExact(value, DateOnly, culture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            return date.ToUnixTimeMilliseconds();
        if (DateTimeOffset.TryParseExact(value, Local, culture, System.Globalization.DateTimeStyles.AssumeLocal, out var local))
            return local.ToUnixTimeMilliseconds();
        return DateTimeOffset.TryParse(value, culture, System.Globalization.DateTimeStyles.AssumeLocal, out var other)
            ? other.ToUnixTimeMilliseconds() : double.NaN;
    }

    /// <summary><c>Date.prototype.toISOString</c>.</summary>
    internal static string ToIsoString(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
