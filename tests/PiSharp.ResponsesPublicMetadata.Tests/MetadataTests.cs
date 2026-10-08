using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

internal static class MetadataTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() => [
        ("responses.public-fallback-model-off-null-map", Fallback),
        ("responses.public-explicit-effort-model-map", Explicit),
        ("responses.public-model-cost-fallback-and-selected", Costs),
        ("responses.public-model-cost-tier-threshold-order", Tiers)
    ];
    private static readonly ModelDescriptor Model = new("fixture", "openai-responses", "openai");
    private static JsonData Metadata(string map) => JsonData.Parse("{\"id\":\"fixture\",\"api\":\"openai-responses\",\"provider\":\"openai\",\"reasoning\":true,\"contextWindow\":8192,\"maxTokens\":2048,\"thinkingLevelMap\":" + map + ",\"cost\":{\"input\":2000000,\"output\":250000,\"cacheRead\":500000,\"cacheWrite\":125000}}");
    private static void Check(bool value, string anchor) { if (!value) throw new IOException(anchor); }
    private sealed class Handler : HttpMessageHandler
    {
        internal JsonData? Wire; internal Task<HttpResponseMessage>? Original; internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Original = Capture(request, token); return Original; }
        private async Task<HttpResponseMessage> Capture(HttpRequestMessage request, CancellationToken token)
        {
            Wire = JsonData.Parse(await (request.Content ?? throw new IOException("Missing request content.")).ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"fixture-response\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":5,\"output_tokens\":2,\"total_tokens\":7,\"input_tokens_details\":{\"cached_tokens\":2,\"cache_write_tokens\":1}}}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private static async Task<StreamDone> Run(Handler handler, JsonData metadata, ResponsesKeyAuthRequestOptions? options = null, string? selected = null)
    {
        using var provider = NativeProviderFactory.CreateResponses(Model, new("https://api.openai.com/v1/responses"),
            "synthetic-injected-key", new(Reasoning: true), options, handler, metadata);
        var request = new ChatRequest(Model, [], 1) { ThinkingLevel = selected };
        var original = Drain(provider, request); Exception? direct = null; StreamDone? done = null;
        var failures = new List<Exception>();
        try { done = await original; } catch (Exception error) { direct = error; failures.Add(error); }
        finally
        {
            if (original.Exception is { } aggregate) failures.Add(aggregate);
            if (handler.Original is { } send)
            {
                try { await send; } catch (Exception error) { failures.Add(error); }
                if (send.Exception is { } sendAggregate) failures.Add(sendAggregate);
            }
        }
        if (failures.Count != 0) throw new AggregateException("Actual public route drain/send fault inventory.", failures.Distinct<Exception>(ReferenceEqualityComparer.Instance));
        Check(direct is null && original.IsCompletedSuccessfully && handler.Original?.IsCompletedSuccessfully == true && handler.Calls == 1, "original-drain-and-send-settled-once");
        return done ?? throw new IOException("No authoritative terminal.");
    }
    private static async Task<StreamDone> Drain(IChatTransport transport, ChatRequest request)
    {
        StreamDone? done = null;
        await foreach (var observation in transport.StreamAsync(request))
        { if (observation is StreamError error) throw new IOException("Public Responses stream failed: " + error); if (observation is StreamDone completed) done = completed; }
        return done ?? throw new IOException("Missing terminal.");
    }
    private static async Task Fallback()
    {
        using var handler = new Handler(); await Run(handler, Metadata("{\"off\":null,\"high\":\"medium\"}"));
        Check(!handler.Wire!.Value.TryGetProperty("reasoning", out _), "null-default-model-map-omits-reasoning");
    }
    private static async Task Explicit()
    {
        using var handler = new Handler(); await Run(handler, Metadata("{\"off\":\"none\",\"high\":\"medium\"}"), new(ReasoningEffort: "high"));
        Check(handler.Wire!.Value.GetProperty("reasoning").GetProperty("effort").GetString() == "medium", "explicit-fallback-effort-uses-model-map");
    }
    private static async Task Costs()
    {
        foreach (var selected in new string?[] { null, "off", "high" })
        {
            using var handler = new Handler(); var done = await Run(handler, Metadata("{\"off\":\"none\",\"high\":\"medium\"}"), selected: selected);
            var cost = done.Message.Usage.Cost;
            Check(cost.Input == 4m && cost.Output == .5m && cost.CacheRead == 1m && cost.CacheWrite == .125m && cost.Total == 5.625m, "public-rate-binding-all-thinking-routes");
        }
    }
    private static async Task Tiers()
    {
        string Tier(decimal above, int multiplier) => System.Text.Json.JsonSerializer.Serialize(new
        { inputTokensAbove = above, input = 2000000 * multiplier, output = 250000 * multiplier, cacheRead = 500000 * multiplier, cacheWrite = 125000 * multiplier });
        foreach (var scenario in new[] {
            (Tiers: "null", Multiplier: 1),
            (Tiers: "[" + Tier(5, 2) + "]", Multiplier: 1),
            (Tiers: "[" + Tier(4, 2) + "]", Multiplier: 2),
            (Tiers: "[" + Tier(6, 2) + "]", Multiplier: 1),
            (Tiers: "[" + Tier(4, 3) + "," + Tier(3, 2) + "," + Tier(4, 7) + "," + Tier(5, 9) + "]", Multiplier: 3),
            (Tiers: "[" + Tier(4.5m, 2) + "]", Multiplier: 2) })
        {
            var original = Metadata("{\"off\":\"none\",\"high\":\"medium\"}").ToString();
            var metadata = JsonData.Parse(original[..^2] + ",\"tiers\":" + scenario.Tiers + "}}");
            foreach (var selected in new string?[] { null, "off", "high" })
            {
                using var handler = new Handler(); var done = await Run(handler, metadata, selected: selected); var cost = done.Message.Usage.Cost;
                Check(cost.Input == 4m * scenario.Multiplier && cost.Output == .5m * scenario.Multiplier &&
                    cost.CacheRead == 1m * scenario.Multiplier && cost.CacheWrite == .125m * scenario.Multiplier &&
                    cost.Total == 5.625m * scenario.Multiplier, "strict-threshold-total-input-first-equal-tier-all-routes");
            }
        }
    }
}
