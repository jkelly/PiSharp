// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.ModelCatalogMaintainer;

public interface ISourceFetcher
{
    Task<SourceDocument> FetchAsync(ProviderConfig config, CancellationToken cancellationToken);
}

public sealed class HttpSourceFetcher(HttpClient client) : ISourceFetcher
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(45) };
    public async Task<SourceDocument> FetchAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        config.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Get, config.Endpoint);
        request.Headers.UserAgent.ParseAdd("PiSharp-CatalogMaintainer/1.0");
        request.Headers.Accept.ParseAdd(config.Adapter == "official-page-json" ? "text/html" : "application/json");
        if (config.ApiKeyEnvironmentVariable is { Length: > 0 } variable)
        {
            var key = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException($"Missing configured environment variable {variable}.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"Source returned HTTP {(int)response.StatusCode}; no model data accepted.");
        const int maximum = 32 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Source exceeds 32 MiB limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximum) throw new InvalidDataException("Source exceeds 32 MiB limit.");
            buffer.Write(chunk, 0, read);
        }
        var body = new UTF8Encoding(false, true).GetString(buffer.ToArray());
        return new SourceDocument(config.Endpoint, body, DateTimeOffset.UtcNow.ToString("O"), (int)response.StatusCode,
            response.Headers.ETag?.ToString());
    }
}

public sealed class FixtureFetcher(IReadOnlyDictionary<string, string> documents) : ISourceFetcher
{
    public int Requests { get; private set; }
    public Task<SourceDocument> FetchAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Requests++;
        if (!documents.TryGetValue(config.Id, out var body)) throw new InvalidDataException("No fixture response for enabled provider.");
        return Task.FromResult(new SourceDocument(config.Endpoint, body, "2026-01-01T00:00:00Z"));
    }
}

public static class SourceParser
{
    private static readonly HashSet<string> MetadataFields = new(StringComparer.Ordinal)
    { "id", "name", "api", "type", "baseUrl", "contextWindow", "maxTokens", "input", "output", "supportedParameters", "description", "reasoning", "expirationDate", "knowledgeCutoff" };
    private static readonly HashSet<string> KnownApis = new(StringComparer.Ordinal)
    { "openai-completions", "openai-responses", "anthropic-messages", "google-generative-ai" };

    public static IReadOnlyList<Observation> Parse(ProviderConfig config, SourceDocument document, List<string> warnings)
    {
        var body = document.Body;
        if (config.Adapter == "official-page-json") body = ExtractScript(body, config.JsonScriptId!);
        // Reject duplicate keys rather than accepting JSON's last-value-wins ambiguity.
        using (var parsed = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 })) CheckDuplicates(parsed.RootElement);
        var root = JsonNode.Parse(body) ?? throw new InvalidDataException("Empty provider response.");
        if (root["error"] is not null || root["next"] is not null || root["next_cursor"] is not null || root["has_more"]?.GetValue<bool>() == true)
            throw new InvalidDataException("Error or paginated provider response is incomplete; configure a complete snapshot endpoint.");
        var rows = CatalogJson.At(root, config.ModelsPointer) as JsonArray ?? throw new InvalidDataException("Configured models pointer must identify an array.");
        if (rows.Count == 0) throw new InvalidDataException("Empty model response is treated as incomplete; existing catalog retained.");
        var result = new List<Observation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index] as JsonObject ?? throw new InvalidDataException("Model row must be an object.");
            var metadata = config.Adapter == "openrouter" ? OpenRouterMetadata(row) : StructuredMetadata(config, row);
            var id = CatalogJson.RequiredString(metadata, "id");
            if (id.Length > 512 || id.Any(char.IsControl) || !seen.Add(id)) throw new InvalidDataException("Duplicate or invalid model identity.");
            if (config.ExcludedModels.Contains(id, StringComparer.Ordinal)) continue;
            metadata["provider"] = config.Id;
            var api = metadata["api"]?.GetValue<string>();
            metadata["runtimeCandidate"] = api is not null && KnownApis.Contains(api) && metadata["type"]?.GetValue<string>() == "chat";
            if (metadata["runtimeCandidate"]?.GetValue<bool>() != true)
                warnings.Add($"{config.Id}/{id}: metadata retained; protocol or model type requires explicit runtime support review.");
            var prices = config.Adapter == "openrouter" ? OpenRouterPrices(row, config.Id, id, warnings) : StructuredPrices(config, row);
            if (prices.Count == 0 || prices.Any(p => p["amount"] is null))
                warnings.Add($"{config.Id}/{id}: one or more prices are unknown; prior known prices, if any, are retained.");
            var priceKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var price in prices) if (!priceKeys.Add(Price.Key(price))) throw new InvalidDataException("Duplicate price dimension in source.");
            result.Add(new Observation(config.Id, id, metadata, prices, new JsonObject
            {
                ["url"] = document.Url, ["sha256"] = document.Sha256, ["adapter"] = config.Adapter,
                ["pointer"] = config.ModelsPointer + "/" + index
            }));
        }
        return result;
    }

    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw new InvalidDataException("Duplicate source JSON property.");
                CheckDuplicates(p.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) CheckDuplicates(item);
    }
    private static JsonObject StructuredMetadata(ProviderConfig config, JsonObject row)
    {
        var metadata = new JsonObject();
        foreach (var (field, pointer) in config.MetadataPointers)
        {
            if (!MetadataFields.Contains(field)) throw new InvalidDataException($"Unsupported metadata target {field}.");
            var value = CatalogJson.At(row, pointer);
            if (value is not null) metadata[field] = value.DeepClone();
        }
        ValidateMetadata(metadata);
        return metadata;
    }
    private static void ValidateMetadata(JsonObject metadata)
    {
        foreach (var field in new[] { "id", "name", "api", "type", "baseUrl", "description", "expirationDate", "knowledgeCutoff" })
            if (metadata[field] is { } value && (value is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
                throw new InvalidDataException($"Metadata {field} must be a string.");
        foreach (var field in new[] { "contextWindow", "maxTokens" })
            if (metadata[field] is { } value && (value is not JsonValue scalar || !scalar.TryGetValue<long>(out var count) || count < 0))
                throw new InvalidDataException($"Metadata {field} must be a nonnegative integer.");
        foreach (var field in new[] { "input", "output", "supportedParameters" })
        {
            if (metadata[field] is not { } value) continue;
            if (value is not JsonArray array || array.Any(x => x is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
                throw new InvalidDataException($"Metadata {field} must be a string array.");
            metadata[field] = new JsonArray(array.Select(x => x!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        }
    }
    private static JsonObject OpenRouterMetadata(JsonObject row)
    {
        var metadata = new JsonObject { ["api"] = "openai-completions", ["baseUrl"] = "https://openrouter.ai/api/v1" };
        var map = new Dictionary<string, string>
        {
            ["id"] = "/id", ["name"] = "/name", ["description"] = "/description", ["contextWindow"] = "/context_length",
            ["maxTokens"] = "/top_provider/max_completion_tokens", ["input"] = "/architecture/input_modalities",
            ["output"] = "/architecture/output_modalities", ["supportedParameters"] = "/supported_parameters",
            ["expirationDate"] = "/expiration_date", ["knowledgeCutoff"] = "/knowledge_cutoff"
        };
        foreach (var (field, pointer) in map) if (CatalogJson.At(row, pointer) is { } value) metadata[field] = value.DeepClone();
        metadata["type"] = metadata["output"] is JsonArray output && output.Count == 1 && output[0]?.GetValue<string>() == "text" ? "chat" : "metadata";
        ValidateMetadata(metadata);
        return metadata;
    }
    private static List<JsonObject> StructuredPrices(ProviderConfig config, JsonObject row)
    {
        if (CatalogJson.At(row, config.PricesPointer) is null) return [];
        var prices = CatalogJson.At(row, config.PricesPointer) as JsonArray ?? throw new InvalidDataException("Prices must be an array of explicit dimensions.");
        return prices.Select(p => Price.Normalize(p?.AsObject() ?? throw new InvalidDataException("Price must be an object."))).ToList();
    }
    private static readonly Dictionary<string, (string Kind, string Unit)> OpenRouterUnits = new(StringComparer.Ordinal)
    {
        ["prompt"] = ("input", "token"), ["completion"] = ("output", "token"),
        ["input_cache_read"] = ("cache-read", "token"), ["input_cache_write"] = ("cache-write-5m", "token"),
        ["input_cache_write_1h"] = ("cache-write-1h", "token"), ["input_audio_cache"] = ("audio-cache-read", "token"),
        ["internal_reasoning"] = ("reasoning", "token"), ["audio"] = ("audio-input", "token"),
        ["audio_output"] = ("audio-output", "token"), ["image_token"] = ("image-input-token", "token"),
        ["image"] = ("image-input", "image"), ["image_output"] = ("image-output", "image"),
        ["request"] = ("request", "request"), ["web_search"] = ("web-search", "search")
    };
    private static List<JsonObject> OpenRouterPrices(JsonObject row, string provider, string id, List<string> warnings)
    {
        if (row["pricing"] is null) return [];
        if (row["pricing"] is not JsonObject pricing) throw new InvalidDataException("OpenRouter pricing must be an object.");
        var discount = CatalogJson.Decimal(pricing["discount"]) ?? 0m;
        if (discount > 1m) throw new InvalidDataException("OpenRouter discount must be a fraction from 0 to 1.");
        var result = new List<JsonObject>();
        AddRates(pricing, "catalog-default", new JsonObject());
        if (pricing["overrides"] is JsonArray overrides)
            for (var i = 0; i < overrides.Count; i++)
            {
                if (overrides[i] is not JsonObject item) throw new InvalidDataException("Invalid pricing override.");
                // Preserve all conditions and their precedence; never collapse long-context/time prices into the default rate.
                var conditions = new JsonObject();
                foreach (var (key, value) in item)
                    if (!OpenRouterUnits.ContainsKey(key))
                    {
                        if (key is not ("min_prompt_tokens" or "utc_start" or "utc_end" or "utc_days"))
                            throw new InvalidDataException($"OpenRouter override field '{key}' requires adapter review.");
                        conditions[key] = value?.DeepClone();
                    }
                conditions["precedence"] = i;
                AddRates(item, "catalog-override", conditions);
            }
        else if (pricing["overrides"] is not null) throw new InvalidDataException("Invalid pricing overrides array.");
        foreach (var (key, value) in pricing)
            if (value is not null && !OpenRouterUnits.ContainsKey(key) && key is not ("overrides" or "discount"))
                throw new InvalidDataException($"OpenRouter pricing field '{key}' requires adapter review; refusing an incomplete price update.");
        if (!pricing.ContainsKey("prompt") || !pricing.ContainsKey("completion")) warnings.Add($"{provider}/{id}: input/output pricing incomplete.");
        return result;

        void AddRates(JsonObject values, string tier, JsonObject conditions)
        {
            foreach (var (field, dimension) in OpenRouterUnits)
            {
                if (!values.TryGetPropertyValue(field, out var amount)) continue;
                // OpenRouter's -1 sentinel means nonstandard/unknown, never free.
                if (amount is not null && amount.ToString() == "-1") amount = null;
                var discounted = CatalogJson.Decimal(amount) * (1m - discount);
                result.Add(Price.Normalize(new JsonObject
                {
                    ["kind"] = dimension.Kind, ["currency"] = "USD", ["unit"] = dimension.Unit,
                    ["amount"] = discounted, ["tier"] = tier, ["region"] = "unspecified", ["conditions"] = conditions.DeepClone()
                }));
            }
        }
    }
    private static string ExtractScript(string html, string id)
    {
        var matches = Regex.Matches(html, "<script\\b(?<attrs>[^>]*)>(?<body>.*?)</script\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        var candidates = new List<string>();
        foreach (Match match in matches)
        {
            var attrs = match.Groups["attrs"].Value;
            var foundId = Regex.Match(attrs, "\\bid\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var type = Regex.Match(attrs, "\\btype\\s*=\\s*[\"']application/json[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (foundId.Success && WebUtility.HtmlDecode(foundId.Groups["value"].Value) == id && type.Success) candidates.Add(match.Groups["body"].Value);
        }
        return candidates.Count == 1 ? candidates[0] : throw new InvalidDataException("Official page has no unique configured application/json script; manual pricing review required.");
    }
}
