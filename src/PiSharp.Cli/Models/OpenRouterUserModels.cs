// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/providers/openrouter-api.ts (openRouterCost, perMillion) and
// packages/ai/src/providers/openrouter.ts (personalizeOpenRouterCatalog, #10353).
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

/// <summary>OpenRouter's per-key model list (<c>GET /models/user</c>) and its $/token prices.</summary>
internal static partial class OpenRouterUserModels
{
    internal const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(15_000);

    /// <summary>
    /// Source personalizeOpenRouterCatalog: restrict a catalog to the chat models the key may use, with the key's limits and prices.
    /// Image and classifier models are returned unchanged; a missing limit keeps the catalog value. The request uses the provider's
    /// configured base URL (regional endpoints filter by region). 401, 403 and 404 (for example a proxy configured in models.json)
    /// return the catalog unchanged; without a key nothing is requested.
    /// </summary>
    internal static async Task<IReadOnlyList<RegistryModel>> PersonalizeAsync(IReadOnlyList<RegistryModel> catalog, string? key, string? baseUrl,
        Func<HttpClient> createClient, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key)) return catalog;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, TrimTrailingSlashes(baseUrl ?? DefaultBaseUrl) + "/models/user");
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("authorization", "Bearer " + key);
        using var client = createClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return catalog;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenRouter model list request failed: {(int)response.StatusCode}");
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        JsonNode? body;
        try { body = JsonTree.Parse(text); }
        catch (System.Text.Json.JsonException error)
        { throw new FormatException(PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(text, error.Message), error); }
        if (body is not JsonObject root || !root.ContainsKey("data") || root["data"] is not JsonArray data)
            throw new InvalidOperationException("Invalid OpenRouter model list response");
        var allowed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in data)
            if (entry is JsonObject model && model["id"] is JsonValue id && id.TryGetValue<string>(out var name)) allowed[name] = model;
        var result = new List<RegistryModel>();
        foreach (var model in catalog)
        {
            if (model.Type != CatalogModelType.Chat) { result.Add(model); continue; }
            if (!allowed.TryGetValue(model.Id, out var entry)) continue;
            var topProvider = entry["top_provider"] as JsonObject;
            result.Add(model.With(json =>
            {
                // `||`: OpenRouter reports a missing limit as absent, null or 0, like the generator's fallback.
                if (FirstTruthy(topProvider?["context_length"], entry["context_length"]) is { } context) json["contextWindow"] = context.DeepClone();
                if (FirstTruthy(topProvider?["max_completion_tokens"]) is { } maximum) json["maxTokens"] = maximum.DeepClone();
                if (Truthy(entry["pricing"])) json["cost"] = Cost(entry["pricing"] as JsonObject);
            }));
        }
        return result;
    }

    /// <summary>Source openRouterCost: $/token prices as $/1M tokens. Missing, invalid and negative base prices are 0; prompt-length
    /// overrides become request-wide tiers whose unlisted rates keep the base price; time-of-day overrides are skipped.</summary>
    internal static JsonObject Cost(JsonObject? pricing)
    {
        var input = PerMillion(pricing?["prompt"], 0); var output = PerMillion(pricing?["completion"], 0);
        var cacheRead = PerMillion(pricing?["input_cache_read"], 0); var cacheWrite = PerMillion(pricing?["input_cache_write"], 0);
        var cost = new JsonObject { ["input"] = input, ["output"] = output, ["cacheRead"] = cacheRead, ["cacheWrite"] = cacheWrite };
        var tiers = new JsonArray();
        // A non-array overrides value is read as no overrides.
        foreach (var node in pricing?["overrides"] as JsonArray ?? [])
        {
            var tier = node as JsonObject;
            if (tier is null || !tier.ContainsKey("min_prompt_tokens") || tier.ContainsKey("utc_start") || tier.ContainsKey("utc_end") || tier.ContainsKey("utc_days"))
                continue;
            tiers.Add(new JsonObject
            {
                ["inputTokensAbove"] = tier["min_prompt_tokens"]?.DeepClone(),
                ["input"] = PerMillion(tier["prompt"], input), ["output"] = PerMillion(tier["completion"], output),
                ["cacheRead"] = PerMillion(tier["input_cache_read"], cacheRead), ["cacheWrite"] = PerMillion(tier["input_cache_write"], cacheWrite),
            });
        }
        if (tiers.Count > 0) cost["tiers"] = tiers;
        return cost;
    }

    /// <summary>Source perMillion: <c>Number((parseFloat(value) * 1e6).toFixed(6))</c> for a price of 0 or more; missing, invalid and
    /// negative prices use <paramref name="fallback"/> (OpenRouter reports -1 for router models such as <c>openrouter/auto</c>).</summary>
    internal static double PerMillion(JsonNode? value, double fallback)
    {
        // parseFloat(String(value)): a JSON number reads as itself, a string by its numeric prefix, anything else as NaN.
        var perToken = value is not JsonValue scalar ? double.NaN : scalar.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => JsonTree.Double(scalar),
            System.Text.Json.JsonValueKind.String => ParseFloat(scalar.GetValue<string>()),
            _ => double.NaN
        };
        // A non-finite product has no JSON number; it is read as missing.
        return perToken >= 0 && double.IsFinite(perToken * 1_000_000) ? ToFixed6(perToken * 1_000_000) : fallback;
    }

    /// <summary>JavaScript parseFloat: the longest decimal (or Infinity) prefix after leading white space.</summary>
    internal static double ParseFloat(string text)
    {
        var start = 0;
        while (start < text.Length && IsJsWhiteSpace(text[start])) start++;
        var match = FloatPrefix().Match(text[start..]);
        if (!match.Success) return double.NaN;
        if (match.Groups["inf"].Success) return match.Value[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        return double.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>ECMAScript WhiteSpace and LineTerminator code points.</summary>
    private static bool IsJsWhiteSpace(char value) => value is '\t' or '\n' or '\v' or '\f' or '\r' || value == (char)0xFEFF || value == (char)0x2028 ||
        value == (char)0x2029 || CharUnicodeInfo.GetUnicodeCategory(value) == UnicodeCategory.SpaceSeparator;

    [GeneratedRegex(@"^[+-]?(?:(?<inf>Infinity)|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPrefix();

    /// <summary><c>Number(x.toFixed(6))</c> for a finite x of 0 or more: x rounded at its exact binary value to six decimals, ties up.</summary>
    internal static double ToFixed6(double value)
    {
        if (value >= 1e21) return value; // toFixed returns String(x), which Number reads back unchanged.
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF); var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        exponent -= 1075;
        var scaled = new BigInteger(mantissa) * 1_000_000;
        BigInteger rounded;
        if (exponent >= 0) rounded = scaled << exponent;
        else
        {
            var divisor = BigInteger.One << -exponent;
            rounded = BigInteger.DivRem(scaled, divisor, out var remainder);
            if (remainder * 2 >= divisor) rounded += 1;
        }
        return double.Parse(rounded.ToString(CultureInfo.InvariantCulture) + "E-6", NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static JsonNode? FirstTruthy(params JsonNode?[] values) => values.FirstOrDefault(Truthy);

    /// <summary>JavaScript truthiness of a JSON value.</summary>
    private static bool Truthy(JsonNode? value) => value is not JsonValue scalar ? value is not null : scalar.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null => false,
        System.Text.Json.JsonValueKind.Number => JsonTree.Double(scalar) != 0,
        System.Text.Json.JsonValueKind.String => scalar.GetValue<string>().Length > 0,
        _ => true
    };

    private static string TrimTrailingSlashes(string value) => value.TrimEnd('/');
}
