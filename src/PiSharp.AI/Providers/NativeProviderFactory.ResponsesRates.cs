using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI.Protocols;
using PiSharp.AI.Protocols.AnthropicMessages;
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
            var (input, output, cacheRead, cacheWrite, tiers) = PromptLengthPricing.ReadCost(cost);
            return new(input, output, cacheRead, cacheWrite)
            { Tiers = [.. tiers.Select(tier => new ResponsesTokenRateTier(tier.InputTokensAbove, tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite))] };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw new ArgumentException("Unsupported native Responses model cost metadata."); }
    }

    // Pi abe508 anthropic-messages.ts prices usage with calculateCost(model) from model.cost, tiers included, or from the
    // cost of the compat.allowedFallbackModels entry (first match) naming the model that answered.
    /// <summary>Binds catalog <c>cost</c> (with prompt-length tiers) and <c>compat.allowedFallbackModels</c> costs to Messages options.</summary>
    public static AnthropicMessagesOptions? AnthropicMessagesOptionsForModel(JsonData? metadata, AnthropicMessagesOptions? options = null)
    {
        if (metadata is null || !metadata.Value.TryGetProperty("cost", out var cost) || cost.ValueKind == JsonValueKind.Null) return options;
        try
        {
            static AnthropicTokenRates Rates(JsonElement table)
            {
                var (input, output, cacheRead, cacheWrite, tiers) = PromptLengthPricing.ReadCost(table);
                return new(input, output, cacheRead, cacheWrite) { Tiers = tiers };
            }
            var rates = Rates(cost); var fallbacks = ImmutableArray.CreateBuilder<AnthropicFallbackModel>();
            if (metadata.Value.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object &&
                compat.TryGetProperty("allowedFallbackModels", out var allowed) && allowed.ValueKind != JsonValueKind.Null)
                foreach (var fallback in allowed.EnumerateArray())
                {
                    var provider = fallback.GetProperty("provider").GetString()!; var model = fallback.GetProperty("model").GetString()!;
                    // A first match without a cost keeps the model's own rates.
                    if (!fallbacks.Any(known => known.Provider == provider && known.Model == model))
                        fallbacks.Add(new(provider, model, fallback.TryGetProperty("cost", out var fallbackCost) && fallbackCost.ValueKind != JsonValueKind.Null
                            ? Rates(fallbackCost) : rates));
                }
            return (options ?? new()) with { Rates = rates, AllowedFallbackModels = fallbacks.ToImmutable() };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new ArgumentException("Unsupported native Anthropic model cost metadata."); }
    }
}
