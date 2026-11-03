// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/openrouter-cost.ts.
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.ProviderShared;

/// <summary>Where a usage cost's total came from (types.ts <c>Usage.cost.totalFromProvider</c>).</summary>
internal static class ProviderCost
{
    private static readonly JsonData True = JsonData.Parse("true"), False = JsonData.Parse("false");

    /// <summary>models.ts calculateCost ends with <c>usage.cost.totalFromProvider = false</c>, and applyOpenRouterReportedCost sets it
    /// true when the billed total replaces the catalog one. The field follows the five cost fields.</summary>
    internal static JsonFields TotalFromProvider(bool reported) => JsonFields.Empty.Set("totalFromProvider", reported ? True : False);

    /// <summary>isOpenRouterModel: the provider is openrouter or the base URL names openrouter.ai.</summary>
    internal static bool IsOpenRouter(string? provider, string? baseUrl) =>
        provider == "openrouter" || baseUrl?.Contains("openrouter.ai", StringComparison.Ordinal) == true;

    /// <summary>applyOpenRouterReportedCost: OpenRouter's billed total when the usage carries complete, valid accounting, otherwise
    /// null and the catalog estimate stays. A total System.Decimal cannot hold also keeps the estimate.</summary>
    internal static double? OpenRouterReportedTotal(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object || !Cost(usage, "cost", out var cost) || !usage.TryGetProperty("is_byok", out var byok))
            return null;
        if (byok.ValueKind == JsonValueKind.False) return Admit(cost);
        if (byok.ValueKind != JsonValueKind.True) return null;
        // is_byok true: OpenRouter's fee plus what the user's upstream provider account billed.
        if (!usage.TryGetProperty("cost_details", out var details) || details.ValueKind != JsonValueKind.Object) return null;
        double upstream;
        if (details.TryGetProperty("upstream_inference_cost", out var reported) && reported.ValueKind == JsonValueKind.Null)
        {
            if (!Cost(details, "upstream_inference_prompt_cost", out var prompt) || !Cost(details, "upstream_inference_completions_cost", out var completions))
                return null;
            upstream = prompt + completions;
            if (!double.IsFinite(upstream)) return null;
        }
        else if (!Cost(details, "upstream_inference_cost", out upstream)) return null;
        return Admit(cost + upstream);
    }

    // isValidCost: a finite, nonnegative JavaScript number.
    private static bool Cost(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number &&
            number.TryGetDouble(out value) && double.IsFinite(value) && value >= 0;
    }

    private static double? Admit(double total) =>
        double.IsFinite(total) && JsonData.Parse(JsonNumber.Text(total)).Value.TryGetDecimal(out _) ? total : null;
}
