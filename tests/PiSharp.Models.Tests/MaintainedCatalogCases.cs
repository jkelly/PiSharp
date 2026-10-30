using System.Text.Json.Nodes;
using PiSharp.Cli.Models;

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
            var path = Path.Combine(root, "bad.json"); await File.WriteAllTextAsync(path, "{broken");
            var baseline = ModelRegistry.Create(new() { MaintainedCatalogPath = Path.Combine(root, "absent") });
            var registry = ModelRegistry.Create(new() { MaintainedCatalogPath = path });
            Equal(baseline.GetAll().Count, registry.GetAll().Count, "baseline retained");
            Check(registry.GetError() is not null, "malformed maintained catalog must be reported");
        }))
    ];
}
