// PiSharp-only (no upstream Pi source): explicit postbaseline catalog corrections and additions.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.AI.Catalogs;

/// <summary>Always-on reviewed changes layered over verified historical shards, before host/user overrides.
/// The returned digest identifies the projected JSON, never the historical package shard.</summary>
public static class ReviewedModelCatalog
{
    private static readonly JsonObject Bundle = Load();
    public const string UpstreamCommit = "7d073328e44337d3292a7db9b0f0308f679c5e97";
    public static ImmutableArray<string> AdditionalProviders { get; } = ["qwen-token-plan-individual-cn"];

    public static FrozenModelCatalog? Apply(string provider, FrozenModelCatalog? baseline)
    {
        var corrections = Bundle["corrections"]?[provider] as JsonObject;
        var additions = Bundle["additions"]?[provider] as JsonArray;
        if (corrections is null && additions is null) return baseline;
        var shard = baseline is null ? new JsonObject() : JsonNode.Parse(baseline.Raw.ToString())!.AsObject();
        if (corrections is not null)
            foreach (var (id, entry) in corrections)
            {
                var model = shard.SelectMany(group => group.Value!.AsObject()).SingleOrDefault(row => row.Key == "chat:" + id).Value as JsonObject
                    ?? throw new InvalidOperationException("Reviewed correction has no historical model: " + provider + "/" + id);
                var metadata = entry!["metadata"]!.AsObject();
                if (metadata["contextWindow"] is { } context) model["contextWindow"] = context.DeepClone();
                foreach (var field in new[] { "compat", "thinkingLevelMap" })
                    if (metadata[field] is JsonObject changes)
                    {
                        var target = model[field] as JsonObject ?? new JsonObject();
                        foreach (var (key, value) in changes) target[key] = value?.DeepClone();
                        model[field] = target;
                    }
            }
        if (additions is not null)
            foreach (var entry in additions)
            {
                var model = entry!.AsObject();
                var api = model["api"]!.GetValue<string>();
                var key = model["type"]!.GetValue<string>() + ":" + model["id"]!.GetValue<string>();
                var group = shard[api] as JsonObject ?? new JsonObject();
                if (group.ContainsKey(key)) throw new InvalidOperationException("Reviewed addition duplicates a historical model: " + provider + "/" + key);
                group[key] = model.DeepClone(); shard[api] = group;
            }
        return FrozenModelCatalog.ReadProviderJson(provider, Encoding.UTF8.GetBytes(shard.ToJsonString()));
    }

    private static JsonObject Load()
    {
        using var stream = typeof(ReviewedModelCatalog).Assembly.GetManifestResourceStream("PiSharp.AI.Catalogs.reviewed-2026-11-08.json")
            ?? throw new InvalidOperationException("Reviewed catalog resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }
}
