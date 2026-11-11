// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/api/anthropic-messages.ts, packages/ai/src/api/openai-responses.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

internal static partial class Program
{
    private static async Task ProviderRouteCacheRetention()
    {
        foreach (var api in new[] { "anthropic-messages", "openai-completions", "openai-responses" })
        foreach (var (retention, summary) in new (string?, bool)[] { (null, false), ("short", false), ("long", false), ("none", false), ("long", true) })
        {
            var anthropic = api == "anthropic-messages";
            var model = new ModelDescriptor("fixture-model", api, anthropic ? "anthropic" : "openai");
            var metadata = JsonData.Parse(new JsonObject
            {
                ["id"] = model.Id, ["name"] = model.Id, ["api"] = api, ["provider"] = model.Provider,
                ["baseUrl"] = anthropic ? "https://api.anthropic.com" : "https://api.openai.com/v1",
                ["reasoning"] = false, ["input"] = new JsonArray("text"), ["contextWindow"] = 200000, ["maxTokens"] = 100,
                ["cost"] = new JsonObject { ["input"] = 1, ["output"] = 1, ["cacheRead"] = 1, ["cacheWrite"] = 1 },
                ["compat"] = new JsonObject { ["supportsLongCacheRetention"] = true }
            }.ToJsonString());
            using var endpoint = new StrictEndpoint(_ => StrictRejection("fixture rejected after recording"));
            using var provider = NativeProviderFactory.CreateProviderRoute(model, metadata,
                new(_ => ValueTask.FromResult(new ProviderRequestAuth(Key)))
                { MaxTokens = 20, SessionId = "route-cache-fixture", CacheRetention = retention, Summary = summary }, endpoint);
            await foreach (var _ in provider.Transport.StreamAsync(new(model, [Entry("""{"role":"user","content":"Hello","timestamp":1}""")], 1))) { }
            Equal(1, endpoint.Payloads.Count);
            var payload = endpoint.Payloads[0];
            var active = !summary && retention != "none";
            var longer = active && retention == "long";
            if (anthropic)
            {
                var json = payload.ToJsonString();
                Equal(active, json.Contains("\"cache_control\":", StringComparison.Ordinal));
                Equal(longer, json.Contains("\"ttl\":\"1h\"", StringComparison.Ordinal));
            }
            else
            {
                Equal(active, payload.ContainsKey("prompt_cache_key"));
                Equal(longer ? "24h" : null, (string?)payload["prompt_cache_retention"]);
            }
        }
    }
}
