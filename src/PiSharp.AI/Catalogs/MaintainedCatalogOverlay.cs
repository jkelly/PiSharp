// PiSharp-only (no upstream Pi source): optional maintained model catalog support.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Catalogs;

public sealed record MaintainedCatalogResult(ImmutableArray<JsonData> Models, ImmutableArray<string> Warnings);

/// <summary>Applies separately maintained, evidence-backed chat metadata without altering pinned source catalogs.</summary>
public static class MaintainedCatalogOverlay
{
    public static MaintainedCatalogResult Apply(string provider, IEnumerable<JsonData> baseline, JsonData overlay,
        IEnumerable<string> supportedApis)
    {
        var models = baseline.Select(b => JsonNode.Parse(b.ToString())!.AsObject()).ToList();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var apis = supportedApis.ToHashSet(StringComparer.Ordinal);
        if (JsonNode.Parse(overlay.ToString()) is not JsonObject root ||
            root["schemaVersion"]?.GetValue<int>() != 1 || root["providers"] is not JsonObject providers)
            throw new FormatException("Invalid maintained catalog schema.");
        if (!providers.TryGetPropertyValue(provider, out var providerEntries)) return Result();
        if (providerEntries is not JsonObject entries) throw new FormatException("Invalid maintained provider catalog.");
        foreach (var (id, node) in entries)
        {
            try
            {
                if (node is not JsonObject entry || entry["metadata"] is not JsonObject metadata || entry["prices"] is not JsonArray prices ||
                    Text(metadata, "id") != id || Text(metadata, "provider") != provider)
                    throw new FormatException("identity or prices missing");
                if (Text(metadata, "type") != "chat") { warnings.Add($"{provider}/{id}: model type is not a supported chat entry."); continue; }
                var index = models.FindIndex(m => Text(m, "id") == id && Text(m, "type", "chat") == "chat");
                var existing = index >= 0;
                var target = existing ? (JsonObject)models[index].DeepClone() : (JsonObject)metadata.DeepClone();
                if (!apis.Contains(Text(target, "api"))) throw new FormatException("API has no registered provider transport");
                if (!existing && metadata["runtimeCandidate"]?.GetValue<bool>() != true) throw new FormatException("model is not a runtime candidate");
                target.Remove("runtimeCandidate"); target.Remove("pinnedCost"); target.Remove("supportedParameters");
                target["type"] = "chat";
                if (metadata["name"] is JsonValue name) target["name"] = name.DeepClone();
                foreach (var property in new[] { "contextWindow", "maxTokens" })
                    if (Number(metadata[property]) is > 0) target[property] = metadata[property]!.DeepClone();
                if (!existing)
                {
                    if (target["reasoning"] is null) target["reasoning"] = false;
                    if (target["input"] is not JsonArray input || input.Count == 0 || input.Any(i => i?.GetValue<string>() is not ("text" or "image")))
                        throw new FormatException("input capabilities are incomplete");
                    if (!Uri.TryCreate(Text(target, "baseUrl"), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
                        throw new FormatException("provider endpoint is incomplete");
                }
                if (Number(target["contextWindow"]) is not > 0 || Number(target["maxTokens"]) is not > 0)
                    throw new FormatException("token limits are incomplete");
                var cost = target["cost"] is JsonObject oldCost ? (JsonObject)oldCost.DeepClone() : new JsonObject();
                var map = new Dictionary<string, string[]>(StringComparer.Ordinal)
                { ["input"] = ["input"], ["output"] = ["output"], ["cacheRead"] = ["cache-read"], ["cacheWrite"] = ["cache-write", "cache-write-5m"] };
                foreach (var (field, kinds) in map)
                {
                    var candidates = prices.OfType<JsonObject>().Where(p => kinds.Contains(Text(p, "kind")) && Text(p, "currency") == "USD" &&
                        Text(p, "unit") == "million-tokens" && Text(p, "region", "unspecified") == "unspecified" &&
                        Text(p, "tier", "unspecified") is "unspecified" or "catalog-default" &&
                        p["conditions"] is not JsonObject { Count: > 0 }).ToList();
                    if (candidates.Count > 1) throw new FormatException("ambiguous base pricing");
                    if (candidates.Count == 1 && Number(candidates[0]["amount"]) is decimal amount && amount >= 0)
                    {
                        if (cost["tiers"] is JsonArray { Count: > 0 } && Number(cost[field]) != amount)
                            throw new FormatException("updating a tiered model needs a reviewed native tier mapping");
                        cost[field] = amount;
                    }
                }
                if (map.Keys.Any(field => Number(cost[field]) is null or < 0)) throw new FormatException("pricing is incomplete");
                // A tiered source must remain separately represented; never apply just its base rate to an old tier table.
                if (prices.OfType<JsonObject>().Any(p => Text(p, "currency") == "USD" && Text(p, "unit") == "million-tokens" && p["conditions"] is JsonObject { Count: > 0 }))
                    throw new FormatException("conditional pricing needs a reviewed native tier mapping");
                target["cost"] = cost;
                var shard = new JsonObject { [Text(target, "api")] = new JsonObject { ["chat:" + id] = target.DeepClone() } };
                _ = FrozenModelCatalog.ReadProviderJson(provider, System.Text.Encoding.UTF8.GetBytes(shard.ToJsonString()));
                if (existing) models[index] = target; else models.Add(target);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or CatalogReadException)
            { warnings.Add($"{provider}/{id}: maintained entry not applied ({ex.Message})."); }
        }
        return Result();

        MaintainedCatalogResult Result() => new(models.Select(m => JsonData.Parse(m.ToJsonString())).ToImmutableArray(), warnings.ToImmutable());
    }
    private static string Text(JsonObject obj, string name, string fallback = "") => obj[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : fallback;
    private static decimal? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<decimal>(out var number) ? number : null;
}
