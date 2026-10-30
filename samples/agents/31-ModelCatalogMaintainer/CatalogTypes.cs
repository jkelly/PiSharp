using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.ModelCatalogMaintainer;

public sealed record MaintainerConfig
{
    public int SchemaVersion { get; init; } = 1;
    public string OverlayPath { get; init; } = "catalog-maintained/models.overlay.json";
    public string? BaselineCatalogDirectory { get; init; } = "src/PiSharp.Cli/Models";
    public string[] ExcludedProviders { get; init; } = [];
    public ProviderConfig[] Providers { get; init; } = [];
    public GitHubConfig? GitHub { get; init; }
    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported config schemaVersion.");
        if (Providers is null || ExcludedProviders is null) throw new InvalidDataException("Provider lists cannot be null.");
        Paths.ValidateRelative(OverlayPath);
        if (BaselineCatalogDirectory is not null) Paths.ValidateRelative(BaselineCatalogDirectory);
        if (!OverlayPath.StartsWith("catalog-maintained/", StringComparison.Ordinal) || !OverlayPath.EndsWith(".overlay.json", StringComparison.Ordinal))
            throw new InvalidDataException("OverlayPath must be catalog-maintained/*.overlay.json; pinned shards cannot be targets.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Providers)
        {
            if (p is null || string.IsNullOrEmpty(p.Id) || !Regex.IsMatch(p.Id, "^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant) || !ids.Add(p.Id))
                throw new InvalidDataException("Provider ids must be unique safe identifiers.");
            // Excluded and disabled providers are never validated for credentials or fetched.
            if (p.Enabled && !ExcludedProviders.Contains(p.Id, StringComparer.Ordinal)) p.Validate();
        }
    }
}

public sealed record ProviderConfig
{
    public string Id { get; init; } = "";
    public bool Enabled { get; init; }
    public string Adapter { get; init; } = "structured-json";
    public string Endpoint { get; init; } = "";
    public string[] OfficialHosts { get; init; } = [];
    public string? ApiKeyEnvironmentVariable { get; init; }
    public string ModelsPointer { get; init; } = "/data";
    public string? JsonScriptId { get; init; }
    public string[] ExcludedModels { get; init; } = [];
    public string? BaselineTier { get; init; }
    public string BaselineRegion { get; init; } = "unspecified";
    public Dictionary<string, string> MetadataPointers { get; init; } = new(StringComparer.Ordinal)
    {
        ["id"] = "/id", ["name"] = "/name", ["api"] = "/api", ["type"] = "/type",
        ["baseUrl"] = "/baseUrl", ["contextWindow"] = "/contextWindow", ["maxTokens"] = "/maxTokens",
        ["input"] = "/input", ["output"] = "/output", ["supportedParameters"] = "/supportedParameters"
    };
    public string PricesPointer { get; init; } = "/prices";
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException($"{Id}: endpoint requires HTTPS without credentials, query, or fragment.");
        if (OfficialHosts is null || !OfficialHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Id}: endpoint host must be explicitly declared in officialHosts.");
        if (Adapter == "openrouter" && (Id != "openrouter" || uri.Host != "openrouter.ai" || uri.AbsolutePath != "/api/v1/models"))
            throw new InvalidDataException("The OpenRouter adapter only accepts its official models endpoint.");
        if (Adapter is not ("openrouter" or "structured-json" or "official-page-json"))
            throw new InvalidDataException($"{Id}: unsupported adapter {Adapter}.");
        if (Adapter == "official-page-json" && string.IsNullOrWhiteSpace(JsonScriptId))
            throw new InvalidDataException($"{Id}: official-page-json requires jsonScriptId; free-text price extraction is intentionally unsupported.");
        if (ExcludedModels is null || MetadataPointers is null) throw new InvalidDataException($"{Id}: model exclusions and metadata pointers cannot be null.");
    }
}

public sealed record GitHubConfig
{
    public string Repository { get; init; } = "";
    public string BaseBranch { get; init; } = "main";
    public string Branch { get; init; } = "automation/model-catalog";
    public string TokenEnvironmentVariable { get; init; } = "GITHUB_TOKEN";
    public void Validate()
    {
        if (!Regex.IsMatch(Repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("github.repository must explicitly name owner/repository.");
        foreach (var b in new[] { BaseBranch, Branch })
            if (!Regex.IsMatch(b, "^[A-Za-z0-9][A-Za-z0-9/_-]*$", RegexOptions.CultureInvariant) || b.Contains("//", StringComparison.Ordinal) || b.EndsWith('/'))
                throw new InvalidDataException("Branch names must contain only letters, numbers, slash, underscore or hyphen.");
        if (!Branch.StartsWith("automation/", StringComparison.Ordinal) || Branch == BaseBranch)
            throw new InvalidDataException("Maintainer branch must be a distinct automation/* branch.");
    }
}

public static class CatalogJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Empty JSON document.");
    public static JsonNode? Sort(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Sort).ToArray()),
        JsonValue number when number.GetValueKind() == JsonValueKind.Number && decimal.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            => JsonValue.Create(CanonicalDecimal(amount)),
        _ => value?.DeepClone()
    };
    public static decimal CanonicalDecimal(decimal value) => decimal.Parse(value.ToString("G29", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    public static string Canonical(JsonNode? value) => Sort(value)?.ToJsonString(Options) + "\n";
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static JsonNode? At(JsonNode? node, string pointer)
    {
        if (pointer == "") return node;
        if (!pointer.StartsWith('/')) throw new InvalidDataException("JSON pointers must start with '/'.");
        foreach (var raw in pointer[1..].Split('/'))
        {
            var part = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj => obj[part],
                JsonArray arr when int.TryParse(part, out var index) && index >= 0 && index < arr.Count => arr[index],
                _ => null
            };
        }
        return node;
    }
    public static string RequiredString(JsonObject obj, string name) => obj[name]?.GetValue<string>() is { Length: > 0 } text
        ? text : throw new InvalidDataException($"Required string missing: {name}.");
    public static decimal? Decimal(JsonNode? value)
    {
        if (value is null) return null;
        var str = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        return decimal.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount >= 0
            ? amount : throw new InvalidDataException("Price must be a nonnegative decimal or null.");
    }
    public static JsonObject EmptyOverlay() => new() { ["schemaVersion"] = 1, ["providers"] = new JsonObject() };
    public static void ValidateOverlay(JsonObject overlay)
    {
        if (overlay["schemaVersion"]?.GetValue<int>() != 1 || overlay["providers"] is not JsonObject providers)
            throw new InvalidDataException("Unsupported or malformed overlay.");
        foreach (var provider in providers)
        {
            if (provider.Value is not JsonObject models) throw new InvalidDataException("Expected provider model map.");
            foreach (var pair in models)
            {
                if (pair.Value is not JsonObject model || model["metadata"] is not JsonObject metadata ||
                    metadata["id"]?.GetValue<string>() != pair.Key || metadata["provider"]?.GetValue<string>() != provider.Key || model["prices"] is not JsonArray prices)
                    throw new InvalidDataException("Overlay model identity or prices are malformed.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in prices)
                {
                    var normalized = Price.Normalize(p?.AsObject() ?? throw new InvalidDataException("Invalid price."));
                    if (!keys.Add(Price.Key(normalized))) throw new InvalidDataException("Duplicate overlay price dimension.");
                }
            }
        }
    }
}

public static class Price
{
    public static JsonObject Normalize(JsonObject raw)
    {
        foreach (var key in raw.Select(x => x.Key))
            if (key is not ("kind" or "currency" or "unit" or "amount" or "tier" or "region" or "conditions" or "provenance"))
                throw new InvalidDataException($"Unsupported price field '{key}'; put pricing conditions in the explicit conditions object.");
        if (raw["conditions"] is not null and not JsonObject) throw new InvalidDataException("Price conditions must be an object.");
        var currency = CatalogJson.RequiredString(raw, "currency").ToUpperInvariant();
        if (!Regex.IsMatch(currency, "^[A-Z]{3}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Currency must be a three-letter code; FX conversion is never implicit.");
        var unit = CatalogJson.RequiredString(raw, "unit");
        var factor = unit switch
        {
            "token" => 1_000_000m, "thousand-tokens" => 1_000m, "million-tokens" => 1m,
            "image" or "request" or "search" or "second" or "minute" or "character" => 1m,
            _ => throw new InvalidDataException($"Unsupported billing unit '{unit}'.")
        };
        var amount = CatalogJson.Decimal(raw["amount"]);
        return new JsonObject
        {
            ["kind"] = CatalogJson.RequiredString(raw, "kind"), ["currency"] = currency,
            ["unit"] = unit is "token" or "thousand-tokens" ? "million-tokens" : unit,
            ["amount"] = amount is null ? null : CatalogJson.CanonicalDecimal(amount.Value * factor),
            ["tier"] = raw["tier"]?.GetValue<string>() ?? "unspecified",
            ["region"] = raw["region"]?.GetValue<string>() ?? "unspecified",
            ["conditions"] = raw["conditions"]?.DeepClone() ?? new JsonObject()
        };
    }
    public static string Key(JsonObject price)
    {
        var identity = (JsonObject)price.DeepClone(); identity.Remove("amount"); identity.Remove("provenance");
        return CatalogJson.Canonical(identity);
    }
}

public static class Paths
{
    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(x => x is "" or "." or ".." or ".git")) throw new InvalidDataException("Expected a safe repository-relative path.");
    }
    public static string Under(string root, string relative)
    {
        ValidateRelative(relative);
        // SampleContext checks root containment and symlinks/reparse points.
        return new SampleContext(root, new JsonObject()).ResolvePath(relative);
    }
}

public sealed record SourceDocument(string Url, string Body, string RetrievedAt, int StatusCode = 200, string? ETag = null)
{
    public string Sha256 => CatalogJson.Hash(Body);
}
public sealed record Observation(string Provider, string Id, JsonObject Metadata, IReadOnlyList<JsonObject> Prices, JsonObject Provenance);
public sealed record PriceChange(string Provider, string Model, JsonObject Dimension, decimal? OldAmount, decimal NewAmount, string SourceUrl)
{
    public decimal? Absolute => OldAmount is null ? null : NewAmount - OldAmount;
    public decimal? Percent => OldAmount is null or 0 ? null : (NewAmount - OldAmount) / OldAmount * 100m;
}
public sealed record ScanResult(JsonObject Overlay, bool Complete, bool Changed, IReadOnlyList<PriceChange> PriceChanges,
    IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, IReadOnlyList<JsonObject> Evidence)
{
    public string Fingerprint => CatalogJson.Hash(JsonSerializer.Serialize(PriceChanges, CatalogJson.Options));
    public JsonObject Report() => new()
    {
        ["complete"] = Complete, ["changed"] = Changed, ["priceChanges"] = JsonSerializer.SerializeToNode(PriceChanges, CatalogJson.Options),
        ["changes"] = JsonSerializer.SerializeToNode(Changes), ["warnings"] = JsonSerializer.SerializeToNode(Warnings),
        ["errors"] = JsonSerializer.SerializeToNode(Errors), ["evidence"] = new JsonArray(Evidence.Select(x => x.DeepClone()).ToArray())
    };
}
