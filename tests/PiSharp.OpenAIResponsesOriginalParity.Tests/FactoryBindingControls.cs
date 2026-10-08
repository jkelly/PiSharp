using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

// Intentionally exposes the frozen factory gaps until its owner composes the proposed binding patch.
internal static class FactoryBindingControls
{
    private static readonly ModelDescriptor Model = new("fixture", "openai-responses", "openai");
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private const string Rates = "{\"input\":2,\"output\":3,\"cacheRead\":5,\"cacheWrite\":7}";
    private static void Check(bool value, [CallerLineNumber] int line = 0)
    { if (!value) throw new InvalidOperationException($"Responses factory binding assertion at line {line}."); }
    private static JsonData Metadata(string? map = "{}", string? cost = Rates)
    {
        var row = new JsonObject { ["id"] = Model.Id, ["api"] = Model.Api, ["provider"] = Model.Provider,
            ["reasoning"] = true, ["contextWindow"] = 8192, ["maxTokens"] = 100 };
        if (map != "missing") row["thinkingLevelMap"] = map is null ? null : JsonNode.Parse(map);
        if (cost != "missing") row["cost"] = cost is null ? null : JsonNode.Parse(cost);
        return JsonData.Parse(row.ToJsonString());
    }
    private static string? Effort(JsonData payload) => payload.Value.TryGetProperty("reasoning", out var reasoning)
        ? reasoning.GetProperty("effort").GetString() : null;

    internal static async Task FallbackMap()
    {
        foreach (var (map, expected) in new (string?, string?)[] { ("missing", "none"), (null, "none"),
            ("{}", "none"), ("{\"off\":null}", null), ("{\"off\":\"low\"}", "low") })
        {
            var (payload, _) = await Capture(Metadata(map), new());
            Check(Effort(payload) == expected);
        }
    }
    internal static async Task ModelMap()
    {
        var options = new ResponsesKeyAuthRequestOptions(ReasoningEffort: "high", ThinkingLevelMap: JsonData.Parse("{\"off\":\"high\",\"high\":\"low\"}"));
        foreach (var level in new string?[] { null, "high" })
        {
            var (payload, _) = await Capture(Metadata("{\"high\":\"medium\"}"), options, level);
            Check(Effort(payload) == "medium");
        }
        foreach (var map in new string?[] { "missing", null, "{}", "{\"high\":null}" })
        {
            // Original nullish model entries fall back to requested effort; model missing/null clears native override.
            var (payload, _) = await Capture(Metadata(map), options);
            Check(Effort(payload) == "high");
        }
        var (disabled, _) = await Capture(Metadata("{\"off\":null}"), options with { ReasoningEffort = null });
        Check(Effort(disabled) is null);
    }
    internal static async Task NativeMap()
    {
        var options = new ResponsesKeyAuthRequestOptions(ThinkingLevelMap: JsonData.Parse("{\"off\":null,\"high\":\"low\"}"));
        foreach (var level in new string?[] { null, "off" })
        {
            var (payload, _) = await Capture(null, options, level);
            Check(Effort(payload) is null);
        }
        var (explicitEffort, _) = await Capture(null, options with { ReasoningEffort = "high" });
        Check(Effort(explicitEffort) == "low");
    }
    internal static async Task CostBranches()
    {
        foreach (var level in new string?[] { null, "off", "high" })
        {
            var (_, result) = await Capture(Metadata(), new(), level);
            Check(result.Message.Usage.Input == 900_000 && result.Message.Usage.Output == 2_000_000 && result.Message.Usage.CacheRead == 100_000);
            Check(result.Message.Usage.Cost.Input == 1.8m && result.Message.Usage.Cost.Output == 6m &&
                result.Message.Usage.Cost.CacheRead == 0.5m && result.Message.Usage.Cost.CacheWrite == 0m && result.Message.Usage.Cost.Total == 8.3m);
        }
    }
    internal static async Task CostTiers()
    {
        // Response service tier overrides request fallback; input-threshold model pricing tiers are distinct.
        foreach (var (requested, response, expected) in new (string, string?, decimal)[] {
            ("flex", null, 4.15m), ("flex", "priority", 16.6m), ("priority", "flex", 4.15m), ("priority", "default", 8.3m) })
        {
            var (_, result) = await Capture(Metadata(), new(ServiceTier: requested), responseTier: response);
            Check(result.Message.Usage.Cost.Total == expected);
        }
    }
    internal static async Task MissingCosts()
    {
        foreach (var metadata in new JsonData?[] { null, Metadata(cost: "missing"), Metadata(cost: null),
            Metadata(cost: "{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"tiers\":[]}") })
        {
            var (_, result) = await Capture(metadata, new());
            Check(result.Message.Usage.TotalTokens == 3_000_000 && result.Message.Usage.Cost.Total == 0m);
        }
    }
    internal static async Task InvalidCosts()
    {
        foreach (var cost in new[] { "[]", "{\"input\":null}", "{\"input\":-1,\"output\":3,\"cacheRead\":5,\"cacheWrite\":7}",
            "{\"input\":2,\"output\":3,\"cacheRead\":5}", "{\"input\":2,\"output\":3,\"cacheRead\":5,\"cacheWrite\":7,\"tiers\":[{\"inputTokensAbove\":100}]}" })
        {
            using var handler = new CaptureHandler(null); var calls = 0; var rejected = false;
            try { using var provider = NativeProviderFactory.CreateResponses(Model, Endpoint, "SYNTHETIC", new(true),
                new() { OnPayload = (_, _, _) => { calls++; return ValueTask.FromResult<JsonData?>(null); } }, handler, Metadata(cost: cost)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected && calls == 0 && handler.Payload is null);
        }
        // Valid complete threshold rates are now original behavior, not unsupported pricing.
        foreach (var (tiers, expected) in new (string, decimal)[] {
            ("[{\"inputTokensAbove\":1000000,\"input\":4,\"output\":6,\"cacheRead\":10,\"cacheWrite\":14}]", 8.3m),
            ("[{\"inputTokensAbove\":999999,\"input\":4,\"output\":6,\"cacheRead\":10,\"cacheWrite\":14}]", 16.6m) })
        {
            var row = Rates[..^1] + ",\"tiers\":" + tiers + "}";
            var (_, result) = await Capture(Metadata(cost: row), new());
            Check(result.Message.Usage.Cost.Total == expected);
        }
    }

    private static async Task<(JsonData, ChatResult)> Capture(JsonData? metadata, ResponsesKeyAuthRequestOptions options,
        string? level = null, string? responseTier = null)
    {
        using var handler = new CaptureHandler(responseTier);
        var payloadCalls = 0; var responseCalls = 0; var rawCalls = 0; JsonData? hookPayload = null;
        var policy = options with {
            OnPayload = (payload, _, _) => { payloadCalls++; hookPayload = payload; return ValueTask.FromResult<JsonData?>(null); },
            OnResponse = (_, _, _) => { responseCalls++; return ValueTask.CompletedTask; },
            OnProviderStreamEvent = (_, _, _) => { rawCalls++; return ValueTask.CompletedTask; } };
        using var provider = NativeProviderFactory.CreateResponses(Model, Endpoint, "SYNTHETIC", new(true), policy, handler, metadata);
        var record = new OriginalTaskRecord("factory-public-complete-original"); Controls.Originals.Add(record);
        ChatResult result;
        try { var original = new ChatClient(provider).CompleteAsync(new(Model, []) { ThinkingLevel = level }); record.Original = original; result = await original; }
        catch (Exception error) { record.Direct = error; throw; } finally { record.Capture(); }
        Check(result.Failure is null && payloadCalls == 1 && responseCalls == 1 && rawCalls == 1);
        Check(handler.Payload is not null && hookPayload is not null && handler.Payload.ToString() == hookPayload.ToString());
        return (handler.Payload!, result);
    }
    private sealed class CaptureHandler(string? tier) : HttpMessageHandler
    {
        internal JsonData? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payload = JsonData.Parse(await request.Content!.ReadAsStringAsync(token));
            var response = new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(),
                ["usage"] = new JsonObject { ["input_tokens"] = 1_000_000, ["output_tokens"] = 2_000_000, ["total_tokens"] = 3_000_000,
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 100_000 } } };
            if (tier is not null) response["service_tier"] = tier;
            var terminal = new JsonObject { ["type"] = "response.completed", ["response"] = response };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + terminal.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
