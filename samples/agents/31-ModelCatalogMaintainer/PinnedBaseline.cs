// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
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
            providers[provider.Id] = Project(provider, catalog, relative, config.GitHub, notes);
            notes.Add($"{provider.Id}: seeded {providers[provider.Id]!.AsObject().Count} maintained identities from {catalog.Models.Length} validated read-only pinned definitions at {relative} ({catalog.ProviderJsonSha256}).");
        }
        return seeded;
    }

    public static JsonObject Project(ProviderConfig provider, FrozenModelCatalog catalog, string relativePath, GitHubConfig? github = null,
        IList<string>? notes = null)
    {
        var models = new JsonObject();
        foreach (var group in catalog.Models.GroupBy(m => m.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // The maintained schema has one identity per provider/id, while pinned shards can expose that id
            // through multiple model types. Prefer the chat route supported by the runtime consumer.
            var definitions = group.OrderBy(m => m.Type == CatalogModelType.Chat ? 0 : 1)
                .ThenBy(m => m.Type).ThenBy(m => m.DeclaredApi, StringComparer.Ordinal).ToArray();
            var model = definitions[0];
            var raw = JsonNode.Parse(model.Raw.ToString())!.AsObject();
            var cost = raw["cost"]!.AsObject(); raw.Remove("cost");
            raw["runtimeCandidate"] = model.Type == CatalogModelType.Chat && model.DeclaredApi is "openai-completions" or "openai-responses" or "anthropic-messages" or "google-generative-ai";
            // Preserve extra pinned costing contracts as evidence; only understood dimensions become maintained rates.
            raw["pinnedCost"] = cost.DeepClone();
            if (definitions.Length > 1)
            {
                raw["pinnedAlternates"] = new JsonArray(definitions.Skip(1).Select(m => JsonNode.Parse(m.Raw.ToString())).ToArray());
                notes?.Add($"{provider.Id}/{model.Id}: selected pinned {model.Type.ToString().ToLowerInvariant()} route {model.DeclaredApi}; retained {definitions.Length - 1} alternate typed definition(s) in metadata.pinnedAlternates.");
            }
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
