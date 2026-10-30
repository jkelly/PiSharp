// PiSharp-only (no upstream Pi source): authored tests for the optional maintained model catalog.
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;
using PiSharp.Contracts;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> MaintainedCatalogCases() =>
    [
        ("maintained.reviewed-price-layer-preserves-route-and-user-overrides", () => WithTemp("maintained", async root =>
        {
            var baseline = ModelRegistry.Create(new() { MaintainedCatalogPath = Path.Combine(root, "absent") });
            var original = baseline.GetAll().First(m => m.Provider == "openai" && m.CloneJson()["cost"]?["tiers"] is null);
            var metadata = original.CloneJson(); metadata.Remove("cost"); metadata["runtimeCandidate"] = true;
            metadata["api"] = "unknown-protocol"; metadata["baseUrl"] = "https://untrusted.invalid";
            var prices = new JsonArray(new JsonObject { ["kind"] = "input", ["currency"] = "USD", ["unit"] = "million-tokens", ["amount"] = 42.5m,
                ["tier"] = "catalog-default", ["region"] = "unspecified", ["conditions"] = new JsonObject() });
            var overlay = new JsonObject { ["schemaVersion"] = 1, ["providers"] = new JsonObject { ["openai"] = new JsonObject
                { [original.Id] = new JsonObject { ["metadata"] = metadata, ["prices"] = prices } } } };
            var path = Path.Combine(root, "maintained.json"); await File.WriteAllTextAsync(path, overlay.ToJsonString());
            var changed = ModelRegistry.Create(new() { MaintainedCatalogPath = path }).GetAll().Single(m => m.Provider == "openai" && m.Id == original.Id);
            Equal(42.5, changed.CloneJson()["cost"]!["input"]!.GetValue<double>(), "updated input price");
            Equal(original.Api, changed.Api, "pinned route retained"); Equal(original.BaseUrl, changed.BaseUrl, "endpoint retained");
            var overrides = new JsonObject { ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["modelOverrides"] = new JsonObject
                { [original.Id] = new JsonObject { ["cost"] = new JsonObject { ["input"] = 99 } } } } } };
            var userPath = Path.Combine(root, "models.json"); await File.WriteAllTextAsync(userPath, overrides.ToJsonString());
            var userModel = ModelRegistry.Create(new() { MaintainedCatalogPath = path, ModelsPath = userPath }).GetAll().Single(m => m.Provider == "openai" && m.Id == original.Id);
            Equal(99d, userModel.CloneJson()["cost"]!["input"]!.GetValue<double>(), "user override retains precedence");
            Check(original.Pinned is not null, "original catalog provenance was mutated");
        })),
        ("maintained.invalid-overlay-does-not-destroy-baseline", () => WithTemp("maintained-invalid", async root =>
        {
            var path = Path.Combine(root, "bad.json");
            var baseline = ModelRegistry.Create(new() { MaintainedCatalogPath = Path.Combine(root, "absent") });
            foreach (var content in new[] { "{broken", "null", "[]", "{\"schemaVersion\":1,\"providers\":{\"openai\":[]}}" })
            {
                await File.WriteAllTextAsync(path, content);
                var registry = ModelRegistry.Create(new() { MaintainedCatalogPath = path });
                Equal(baseline.GetAll().Count, registry.GetAll().Count, "baseline retained for " + content);
                Check(registry.GetError() is not null, "malformed maintained catalog must be reported: " + content);
            }
        })),
        ("maintained.flat-price-update-cannot-preserve-stale-tiers", () => WithTemp("maintained-tiers", root =>
        {
            var registry = ModelRegistry.Create(new() { MaintainedCatalogPath = Path.Combine(root, "absent") });
            var original = registry.GetAll().First(m => m.Provider == "openai" && m.CloneJson()["type"]?.GetValue<string>() == "chat").CloneJson();
            original["cost"] = new JsonObject
            {
                ["input"] = 1, ["output"] = 2, ["cacheRead"] = 0, ["cacheWrite"] = 0,
                ["tiers"] = new JsonArray(new JsonObject { ["inputTokensAbove"] = 1000, ["input"] = 2, ["output"] = 4, ["cacheRead"] = 0, ["cacheWrite"] = 0 })
            };
            var metadata = (JsonObject)original.DeepClone(); metadata.Remove("cost");
            var price = new JsonObject { ["kind"] = "input", ["currency"] = "USD", ["unit"] = "million-tokens", ["amount"] = 5,
                ["tier"] = "catalog-default", ["region"] = "unspecified", ["conditions"] = new JsonObject() };
            var overlay = new JsonObject { ["schemaVersion"] = 1, ["providers"] = new JsonObject { ["openai"] = new JsonObject
                { [original["id"]!.GetValue<string>()] = new JsonObject { ["metadata"] = metadata, ["prices"] = new JsonArray(price) } } } };
            var baseline = JsonData.Parse(original.ToJsonString());
            var result = MaintainedCatalogOverlay.Apply("openai", [baseline], JsonData.Parse(overlay.ToJsonString()), [original["api"]!.GetValue<string>()]);
            Equal(baseline.ToString(), result.Models.Single().ToString(), "unmapped tiered rates retain full pinned model");
            Check(result.Warnings.Any(w => w.Contains("tier", StringComparison.Ordinal)), "tier mapping warning required");
            price["amount"] = 1;
            var unchanged = MaintainedCatalogOverlay.Apply("openai", [baseline], JsonData.Parse(overlay.ToJsonString()), [original["api"]!.GetValue<string>()]);
            Check(unchanged.Warnings.IsEmpty, "identical base price does not require a new tier mapping");
            return Task.CompletedTask;
        }))
    ];
}
