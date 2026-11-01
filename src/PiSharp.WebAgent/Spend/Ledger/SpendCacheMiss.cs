// Decision 0011 (run W3, S2): the prompt cache meter's "wasted" rule, CacheStats' miss rule (src/PiSharp.CodingAgent/Usage/CacheStats.cs,
// DetectMiss) applied to ledger rows instead of session entries: a request missed the cache when the prompt tokens the previous request of
// the same run already sent (so the provider should still hold them) were billed again instead of read from the cache. Missed tokens =
// min(previous prompt, this prompt) - cache-read tokens, counted only above CacheStats' 1,024-token noise floor, and a request with no cache
// activity counts only when the run reported cache activity before (on providers that never report caching it means nothing). The extra
// cost is the missed tokens x (the rate paid for input and cache writes - the cache-read rate), at the row's rates. The ledger finds the
// previous request when a row settles: the latest row of the run that reported usage, created before this one and ended before it was
// created (so parallel requests of one run never count as each other's previous). Cache-warm requests set the previous prompt but are not
// misses themselves: all of their spend counts as waste.
namespace PiSharp.WebAgent.Spend;

/// <summary>The cache-miss rule of the prompt cache meter.</summary>
public static class SpendCacheMiss
{
    /// <summary>Per-request misses at or below this many tokens are cache breakpoint noise (CacheStats' noise floor).</summary>
    public const double NoiseFloorTokens = 1024;

    /// <summary>The prompt tokens of <paramref name="usage"/>: input + cache read + cache write.</summary>
    public static double PromptTokens(SpendUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;
    }

    /// <summary>The prompt tokens of <paramref name="usage"/> that should have been read from the cache, or 0 when it is not a miss:
    /// min(<paramref name="previousPromptTokens"/>, prompt) - cache read, above <see cref="NoiseFloorTokens"/>.</summary>
    /// <param name="previousPromptTokens">The prompt tokens of the run's previous request, or null when there is none.</param>
    /// <param name="usage">What this request reported.</param>
    /// <param name="reportedCache">Whether an earlier request of the run reported cache activity (reads or writes), or was a cache warm.</param>
    public static double MissedTokens(double? previousPromptTokens, SpendUsage usage, bool reportedCache)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var prompt = PromptTokens(usage);
        if (previousPromptTokens is not > 0 || prompt <= 0) return 0;
        if (usage.CacheReadTokens + usage.CacheWriteTokens == 0 && !reportedCache) return 0;
        var missed = Math.Min(previousPromptTokens.Value, prompt) - usage.CacheReadTokens;
        return missed > NoiseFloorTokens ? missed : 0;
    }

    /// <summary>What a miss cost beyond a cache hit, in micro-USD rounded to the nearest: <see cref="MissedTokens"/> x (the rate paid per
    /// input and cache-write token - the cache-read rate) at <paramref name="price"/>; 0 when it is not a miss.</summary>
    public static long MissedMicros(double? previousPromptTokens, SpendUsage usage, SpendPrice price, bool reportedCache)
    {
        ArgumentNullException.ThrowIfNull(price);
        var missed = MissedTokens(previousPromptTokens, usage, reportedCache);
        if (missed <= 0) return 0;
        // Missed tokens can only land in the input or cache-write buckets: they are billed at those buckets' average rate.
        var paidTokens = usage.InputTokens + usage.CacheWriteTokens;
        var paidPerToken = paidTokens > 0
            ? (SpendMoney.Cost(usage.InputTokens, price.Input) + SpendMoney.Cost(usage.CacheWriteTokens, price.CacheWrite)) / (decimal)paidTokens
            : 0m;
        var extra = paidPerToken - (decimal)price.CacheRead;
        return extra <= 0 ? 0 : SpendMoney.Round((decimal)missed * extra);
    }
}
