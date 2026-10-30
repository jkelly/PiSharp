using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;

namespace PiSharp.Samples.ModelCatalogMaintainer;

/// <summary>Read-only projection of existing validated provider shards into comparable maintained prices.</summary>
public static class PinnedBaseline
{
    public static async Task<JsonObject> SeedMissingProvidersAsync(MaintainerConfig config, JsonObject overlay,
        string repositoryRoot, IList<string> notes, CancellationToken token = default)
    {
        config.Validate(); CatalogJson.ValidateOverlay(overlay);
        var seeded = (JsonObject)overlay.DeepClone();
        if (config.BaselineCatalogDirectory is null) return seeded;
        var providers = seeded["providers"]!.AsObject();
        foreach (var provider in config.Providers.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (!provider.Enabled || config.ExcludedProviders.Contains(provider.Id, StringComparer.Ordinal)) continue;
            if (providers.ContainsKey(provider.Id)) continue;
            var relative = config.BaselineCatalogDirectory + "/" + provider.Id + ".json";
            var path = Paths.Under(repositoryRoot, relative);
            if (!File.Exists(path))
            {
                notes.Add($"{provider.Id}: no existing overlay or pinned shard at {relative}; previous prices are unknown.");
                continue;
            }
            var bytes = await File.ReadAllBytesAsync(path, token);
            // The public reader enforces full identity/shape, uniqueness, limits and valid finite numeric metadata.
            // It also computes the source hash; the shard is never rewritten and its frozen hash manifest is untouched.
            FrozenModelCatalog catalog;
            try { catalog = FrozenModelCatalog.ReadProviderJson(provider.Id, bytes); }
            catch (CatalogReadException ex) { throw new InvalidDataException($"Invalid pinned catalog {relative} ({ex.Failure}); no maintenance performed.", ex); }
            providers[provider.Id] = Project(provider, catalog, relative, config.GitHub);
            notes.Add($"{provider.Id}: seeded {catalog.Models.Length} models from validated read-only pinned shard {relative} ({catalog.ProviderJsonSha256}).");
        }
        return seeded;
    }

    public static JsonObject Project(ProviderConfig provider, FrozenModelCatalog catalog, string relativePath, GitHubConfig? github = null)
    {
        var models = new JsonObject();
        foreach (var model in catalog.Models.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (models.ContainsKey(model.Id)) throw new InvalidDataException("Pinned shard reuses an id across model types; explicit overlay migration is required.");
            var raw = JsonNode.Parse(model.Raw.ToString())!.AsObject();
            var cost = raw["cost"]!.AsObject(); raw.Remove("cost");
            raw["runtimeCandidate"] = model.Type == CatalogModelType.Chat && model.DeclaredApi is "openai-completions" or "openai-responses" or "anthropic-messages" or "google-generative-ai";
            // Preserve extra pinned costing contracts as evidence; only understood dimensions become maintained rates.
            raw["pinnedCost"] = cost.DeepClone();
            var provenance = new JsonObject
            {
                ["url"] = github is null ? "repository:" + relativePath : $"https://github.com/{github.Repository}/blob/{github.BaseBranch}/{relativePath}",
                ["sha256"] = catalog.ProviderJsonSha256, ["adapter"] = "pinned-catalog",
                ["pointer"] = "/" + Pointer(model.DeclaredApi) + "/" + Pointer(raw["type"] + ":" + model.Id)
            };
            var prices = new List<JsonObject>();
            Add(cost, provider.BaselineTier ?? (provider.Adapter == "openrouter" ? "catalog-default" : "unspecified"), new JsonObject());
            if (cost["tiers"] is JsonArray tiers)
                for (var index = 0; index < tiers.Count; index++)
                {
                    if (tiers[index] is not JsonObject tier) throw new InvalidDataException("Invalid pinned price tier.");
                    if (tier["inputTokensAbove"] is null) continue; // Its original contract remains in pinnedCost.
                    var conditions = provider.Adapter == "openrouter"
                        ? new JsonObject { ["min_prompt_tokens"] = tier["inputTokensAbove"]!.DeepClone(), ["precedence"] = index }
                        : new JsonObject { ["inputTokensAbove"] = tier["inputTokensAbove"]!.DeepClone() };
                    Add(tier, provider.Adapter == "openrouter" ? "catalog-override" : "pinned-context-tier", conditions);
                }
            models[model.Id] = new JsonObject
            {
                ["metadata"] = raw,
                ["prices"] = new JsonArray(prices.OrderBy(Price.Key, StringComparer.Ordinal).Select(p => (JsonNode?)p).ToArray()),
                ["provenance"] = provenance
            };

            void Add(JsonObject sourceCost, string tier, JsonObject conditions)
            {
                foreach (var (field, kind) in new[] { ("input", "input"), ("output", "output"), ("cacheRead", "cache-read"), ("cacheWrite", provider.Adapter == "openrouter" ? "cache-write-5m" : "cache-write") })
                {
                    if (!sourceCost.ContainsKey(field)) continue;
                    var price = Price.Normalize(new JsonObject
                    {
                        ["kind"] = kind, ["amount"] = sourceCost[field]?.DeepClone(), ["currency"] = "USD", ["unit"] = "million-tokens",
                        ["tier"] = tier, ["region"] = provider.BaselineRegion, ["conditions"] = conditions.DeepClone()
                    });
                    price["provenance"] = provenance.DeepClone(); prices.Add(price);
                }
            }
        }
        return models;
    }
    private static string Pointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
