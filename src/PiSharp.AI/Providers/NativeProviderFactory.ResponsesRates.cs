using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

public static partial class NativeProviderFactory
{
    // Pi d86654 models.ts calculateCost: model.cost base rates per million tokens.
    // Preserve source tier order: the first equal matching threshold wins.
    private static ResponsesTokenRates? ResponsesRatesForModel(JsonData? metadata)
    {
        if (metadata is null || !metadata.Value.TryGetProperty("cost", out var cost) || cost.ValueKind == JsonValueKind.Null) return null;
        try
        {
            if (cost.ValueKind != JsonValueKind.Object) throw new ArgumentException();

            decimal Rate(JsonElement table, string name)
            {
                if (!table.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetDecimal(out var result) || result < 0) throw new ArgumentException();
                return result;
            }
            var admittedTiers = ImmutableArray.CreateBuilder<ResponsesTokenRateTier>();
            if (cost.TryGetProperty("tiers", out var tiers) && tiers.ValueKind != JsonValueKind.Null)
            {
                if (tiers.ValueKind != JsonValueKind.Array) throw new ArgumentException();
                foreach (var tier in tiers.EnumerateArray())
                {
                    if (tier.ValueKind != JsonValueKind.Object || !tier.TryGetProperty("inputTokensAbove", out var threshold) ||
                        threshold.ValueKind != JsonValueKind.Number || !threshold.TryGetDecimal(out var above)) throw new ArgumentException();
                    admittedTiers.Add(new(above, Rate(tier, "input"), Rate(tier, "output"), Rate(tier, "cacheRead"), Rate(tier, "cacheWrite")));
                }
            }
            return new(Rate(cost, "input"), Rate(cost, "output"), Rate(cost, "cacheRead"), Rate(cost, "cacheWrite")) { Tiers = admittedTiers.ToImmutable() };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw new ArgumentException("Unsupported native Responses model cost metadata."); }
    }
}
