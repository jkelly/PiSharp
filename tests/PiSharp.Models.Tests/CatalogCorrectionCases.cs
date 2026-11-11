// PiSharp-only (no upstream Pi source): C19 reviewed catalog corrections and request regressions.
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Providers;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;
using PiSharp.Contracts;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CatalogCorrectionCases()
    {
        yield return ("catalog-c19.runtime-overlay-exact-scope", Sync(() =>
        {
            var current = ModelRegistry.Create(new() { MaintainedCatalogPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".absent") });
            var expected = CurrentModelCatalog.Providers.SelectMany(p => CurrentModelCatalog.Get(p)!.Models)
                .Where(m => m.Type == CatalogModelType.Chat).ToArray();
            Equal(1643, expected.Length, "released current chat count");
            Equal(expected.Length, current.GetAll().Count, "no removed historical entries leak into current catalog");
            foreach (var row in expected)
            {
                var actual = current.GetAll().Single(m => m.Provider == row.Provider && m.Id == row.Id && m.Type == row.Type);
                Check(JsonNode.DeepEquals(JsonNode.Parse(row.Raw.ToString()), actual.CloneJson()), row.Provider + "/" + row.Id + " exact released metadata");
            }
        }));
        yield return ("catalog-c19.anthropic-request-capabilities", Sync(() =>
        {
            var registry = ModelRegistry.Create(new());
            using var client = new HttpClient();
            foreach (var id in new[] { "claude-sonnet-5", "claude-sonnet-4-5", "claude-opus-5-5", "claude-haiku-5-5" })
            {
                var model = registry.GetAll().Single(m => m.Provider == "opencode" && m.Id == id);
                var descriptor = new ModelDescriptor(model.Id, model.Api, model.Provider);
                var options = new AnthropicMessagesSimpleOptions(JsonData.Parse(model.ToJsonString()),
                    new(MaximumTokens: 128, Temperature: 0.7m), id.Contains("5-5") ? "high" : null);
                var factory = new AnthropicMessagesSimpleRequestFactory(client, new Uri(model.BaseUrl), descriptor, options);
                var payload = factory.Prepare(Request(descriptor), "fixture-key").Payload.Value;
                Equal(id == "claude-sonnet-4-5", payload.TryGetProperty("temperature", out _), id + " temperature eligibility");
                if (id.Contains("5-5"))
                {
                    Equal("drop_block", payload.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString(), id + " thinking binding");
                    Check(payload.GetProperty("messages").ToString().Contains("output_config", StringComparison.Ordinal), id + " per-message effort");
                }
            }
        }));
        yield return ("catalog-c19.direct-catalog-temperature", async () =>
        {
            var registry = ModelRegistry.Create(new());
            foreach (var (provider, id) in new[] { ("anthropic", "claude-sonnet-5"), ("opencode", "claude-sonnet-5"), ("opencode", "claude-sonnet-4-5") })
            {
                var model = registry.GetAll().Single(m => m.Provider == provider && m.Id == id);
                var descriptor = new ModelDescriptor(id, model.Api, provider);
                var endpoint = new LiveEndpoint(seen => Respond(seen, id));
                var metadata = JsonData.Parse(model.ToJsonString());
                var projection = new AnthropicMessagesRequestOptions(128, ModelReasoning: model.Reasoning, ThinkingEnabled: false, Temperature: 0.7m);
                using var bound = provider == "anthropic"
                    ? NativeProviderFactory.CreateAnthropic(descriptor, new Uri(model.BaseUrl), "fixture-key", projection, handler: endpoint, modelMetadata: metadata)
                    : NativeProviderFactory.CreateCatalogAnthropic(descriptor, model.BaseUrl, "fixture-key", projection, new(), metadata, endpoint);
                await Drive(bound, descriptor);
                var seen = endpoint.Snapshot();
                Equal(1, seen.Length, "direct factory sends once");
                var payload = JsonNode.Parse(seen[0].Body!)!.AsObject();
                Equal(id == "claude-sonnet-4-5", payload.ContainsKey("temperature"), provider + "/" + id + " direct temperature eligibility");
            }
        });
    }
}
