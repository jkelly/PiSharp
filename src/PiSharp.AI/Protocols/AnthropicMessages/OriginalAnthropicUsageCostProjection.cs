using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

// Opt-in source snapshot projection. Native TokenUsage.Cost remains decimal.
internal static class OriginalAnthropicUsageCostProjection
{
    internal static JsonData Create(AnthropicTokenRates rates, TokenUsage usage, double cacheWrite1h)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(usage);
        // Counts are the JavaScript numbers the stream reported (fractions included).
        if (!double.IsFinite(usage.Input) || !double.IsFinite(usage.Output) || !double.IsFinite(usage.CacheRead) || !double.IsFinite(usage.CacheWrite) ||
            !double.IsFinite(cacheWrite1h))
            throw new ArgumentOutOfRangeException(nameof(usage));

        // Convert operands before arithmetic, as original JavaScript Number does.
        // In particular, do not cast the already-rounded native decimal costs.
        double inputRate = Number(rates.Input), outputRate = Number(rates.Output);
        double readRate = Number(rates.CacheRead), writeRate = Number(rates.CacheWrite);
        // models.ts calculateCost compares Number token sums with each tier's Number threshold.
        if (PromptLengthPricing.TrySelect(rates.Tiers.IsDefault ? [] : rates.Tiers, candidate => Number(candidate.InputTokensAbove),
            usage.Input, usage.CacheRead, usage.CacheWrite, out var tier))
        { inputRate = Number(tier.Input); outputRate = Number(tier.Output); readRate = Number(tier.CacheRead); writeRate = Number(tier.CacheWrite); }
        double longWrite = cacheWrite1h;
        double shortWrite = usage.CacheWrite - longWrite;
        double input = (inputRate / 1_000_000d) * usage.Input;
        double output = (outputRate / 1_000_000d) * usage.Output;
        double cacheRead = (readRate / 1_000_000d) * usage.CacheRead;
        double cacheWrite = (writeRate * shortWrite + inputRate * 2d * longWrite) / 1_000_000d;
        double total = input + output;
        total = total + cacheRead;
        total = total + cacheWrite;
        return JsonData.Parse(JsonSerializer.Serialize(new { input, output, cacheRead, cacheWrite, total }));
    }

    // Parse the decimal JSON value as Number rather than an intermediate arithmetic conversion.
    private static double Number(decimal value) => JsonSerializer.SerializeToElement(value).GetDouble();
}
