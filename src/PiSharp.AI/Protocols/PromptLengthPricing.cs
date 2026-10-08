// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/models.ts.
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;

namespace PiSharp.AI.Protocols;

/// <summary>A request-wide prompt-length pricing tier (types.ts <c>ModelCostTier</c>); rates per million tokens.</summary>
public sealed record TokenRateTier(decimal InputTokensAbove, decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);

/// <summary>The tier selection of models.ts <c>calculateCost</c>, shared by every native cost path.</summary>
internal static class PromptLengthPricing
{
    // calculateCost: inputTokens = usage.input + usage.cacheRead + usage.cacheWrite (cache reads and writes count).
    // A tier applies when inputTokens is strictly above its inputTokensAbove, so a request of exactly the threshold
    // keeps the lower rates. The greatest matching threshold prices the whole request; among equal thresholds the
    // first in source order wins. A threshold of -1 or below never matches (matchedThreshold starts at -1).
    internal static bool TrySelect<TTier, TNumber>(IEnumerable<TTier> tiers, Func<TTier, TNumber> threshold,
        TNumber input, TNumber cacheRead, TNumber cacheWrite, out TTier selected) where TNumber : INumber<TNumber>
    {
        var inputTokens = input + cacheRead + cacheWrite; var matched = -TNumber.One; var found = false; selected = default!;
        foreach (var tier in tiers)
        {
            var above = threshold(tier);
            if (inputTokens > above && above > matched) { selected = tier; matched = above; found = true; }
        }
        return found;
    }

    internal static (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite) Select(
        decimal input, decimal output, decimal cacheRead, decimal cacheWrite, ImmutableArray<TokenRateTier> tiers,
        long inputTokens, long cacheReadTokens, long cacheWriteTokens) =>
        TrySelect(tiers.IsDefault ? [] : tiers, candidate => candidate.InputTokensAbove, (decimal)inputTokens, cacheReadTokens, cacheWriteTokens, out var tier)
            ? (tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite) : (input, output, cacheRead, cacheWrite);

    internal static bool Valid(ImmutableArray<TokenRateTier> tiers) =>
        !tiers.IsDefault && tiers.All(tier => tier is not null && tier.Input >= 0 && tier.Output >= 0 && tier.CacheRead >= 0 && tier.CacheWrite >= 0);

    /// <summary>Reads catalog <c>cost</c> metadata: base rates and source-ordered <c>tiers</c>. Throws <see cref="ArgumentException"/>.</summary>
    internal static (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, ImmutableArray<TokenRateTier> Tiers) ReadCost(JsonElement cost)
    {
        if (cost.ValueKind != JsonValueKind.Object) throw new ArgumentException();
        static decimal Rate(JsonElement table, string name) =>
            table.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result) && result >= 0
                ? result : throw new ArgumentException();
        var tiers = ImmutableArray.CreateBuilder<TokenRateTier>();
        if (cost.TryGetProperty("tiers", out var declared) && declared.ValueKind != JsonValueKind.Null)
        {
            if (declared.ValueKind != JsonValueKind.Array) throw new ArgumentException();
            foreach (var tier in declared.EnumerateArray())
            {
                if (tier.ValueKind != JsonValueKind.Object || !tier.TryGetProperty("inputTokensAbove", out var threshold) ||
                    threshold.ValueKind != JsonValueKind.Number || !threshold.TryGetDecimal(out var above)) throw new ArgumentException();
                tiers.Add(new(above, Rate(tier, "input"), Rate(tier, "output"), Rate(tier, "cacheRead"), Rate(tier, "cacheWrite")));
            }
        }
        return (Rate(cost, "input"), Rate(cost, "output"), Rate(cost, "cacheRead"), Rate(cost, "cacheWrite"), tiers.ToImmutable());
    }
}
