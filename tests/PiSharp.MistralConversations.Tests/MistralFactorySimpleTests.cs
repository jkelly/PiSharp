using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal static class MistralFactorySimpleTests
{
    private static readonly ModelDescriptor Model = new("factory-fixture", "mistral-conversations", "mistral");
    private static readonly Uri Endpoint = new("https://api.mistral.ai/");
    private static MistralTextOptions Options => new(Endpoint, true, new(0, 0, 0, 0), "offline-fixture");
    private static JsonData Metadata(double window = 5000, double maximum = 1000) => JsonData.Parse(JsonSerializer.Serialize(new
    { id = Model.Id, api = Model.Api, provider = Model.Provider, contextWindow = window, maxTokens = maximum, reasoning = false }));
    private static TranscriptEntry User(string text, long timestamp = 1) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp })));
    // Pi 1.1.0 estimate.ts: 3.5 characters per token, so 200 characters estimate ceil(200 / 3.5) = 58 tokens (50 at four).
    private static ChatRequest Request => new(Model, [User(new string('x', 200))]);
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mistral-factory actual direct and Simple consumers have separate output budgets", FactoryBudget),
        ("mistral-simple default explicit safety floor and nonpositive context limits", BudgetTable),
        ("mistral-simple usage anchors respect timestamps and failed assistant exclusion", UsageAnchors),
        ("mistral-factory cache affinity and explicit model request header precedence", HeaderAffinity),
        ("mistral-direct reasoning options and unsolicited thinking retain actual events", DirectThinking),
        ("mistral-simple complete model reasoning headers and off mapping override direct controls", SimpleMetadata),
        ("mistral-simple absent null and supplied metadata headers never retain stale caller model headers", MistralSimpleModelHeadersTests.Run),
        ("mistral-factory identity metadata and multicast refusal occur before effects", Refusals),
        ("mistral-direct every tool choice survives hook and actual wire", ToolChoices)
    ];
    private static async Task FactoryBudget()
    {
        var maxima = new List<double>();
        using var handler = new Handler(async request =>
        {
            Check(request.RequestUri == new Uri("https://api.mistral.ai/v1/chat/completions") && request.Headers.Authorization?.ToString() == "Bearer fake-key");
            maxima.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value.GetProperty("max_tokens").GetDouble());
            return Response();
        });
        var supplied = Options with { MaxTokens = 1000 };
        var row = Metadata(); var frozen = row.ToString();
        using (var direct = NativeProviderFactory.CreateMistral(Model, Endpoint, "fake-key", supplied, handler))
            Check(await Done(direct.Transport, Request));
        using (var simple = NativeProviderFactory.CreateMistralSimple(Model, Endpoint, "fake-key", row, supplied, handler))
            Check(await Done(simple.Transport, Request));
        Check(maxima.SequenceEqual([1000d, 846d]) && supplied.MaxTokens == 1000 && row.ToString() == frozen && !handler.Disposed);
        // Both clients have been disposed, but the injected handler remains owned and usable by this fixture.
        using var third = NativeProviderFactory.CreateMistral(Model, Endpoint, "fake-key", supplied, handler);
        Check(await Done(third.Transport, Request) && handler.Calls == 3);
    }
    private static Task BudgetTable()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No HTTP in resolution fixture.")));
        foreach (var (window, supplied, expected) in new (double, double?, double)[]
        { (5000, null, 846), (5000, 100, 100), (4000, null, 1), (0, null, 1000), (-1, 0, 1), (5000, 0, 0) })
        {
            var transport = new MistralSimpleHttpSseTransport(client, Model, Metadata(window), Options with { ApiKey = "fake-key", MaxTokens = supplied });
            Check(transport.Resolve(Request) == new MistralSimpleResolution(58, expected));
        }
        return Task.CompletedTask;
    }
    private static Task UsageAnchors()
    {
        using var client = new HttpClient();
        var transport = new MistralSimpleHttpSseTransport(client, Model, Metadata(10000), Options with { ApiKey = "fake-key" });
        TranscriptEntry Assistant(long timestamp, string stop, long total) => new("assistant", JsonData.Parse(JsonSerializer.Serialize(new
        { role = "assistant", timestamp, stopReason = stop, content = new[] { new { type = "text", text = "abcd" } },
            usage = new { totalTokens = total, input = 80, output = 20, cacheRead = 0, cacheWrite = 0 } })));
        Check(transport.Resolve(new(Model, [User("abcd"), Assistant(2, "stop", 120), User("abcdefgh", 3)])).ContextTokens == 123);
        Check(transport.Resolve(new(Model, [User("abcd"), Assistant(2, "stop", 0), User("abcdefgh", 3)])).ContextTokens == 103);
        Check(transport.Resolve(new(Model, [User("abcd", 10), Assistant(2, "stop", 120), User("abcdefgh", 11)])).ContextTokens == 7);
        foreach (var stop in new[] { "error", "aborted" })
            Check(transport.Resolve(new(Model, [User("abcd"), Assistant(2, stop, 120), User("abcdefgh", 3)])).ContextTokens == 7);
        return Task.CompletedTask;
    }
    private static async Task HeaderAffinity()
    {
        foreach (var mode in new[] { "auto", "override", "delete", "disabled" })
        {
            using var handler = new Handler(async request =>
            {
                var wire = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
                Check(wire.TryGetProperty("prompt_cache_key", out _) == (mode != "disabled"));
                var exists = request.Headers.TryGetValues("x-affinity", out var values);
                Check(exists == (mode is "auto" or "override"));
                if (exists) Check(values!.Single() == (mode == "auto" ? "session" : "request"));
                if (mode is "override" or "delete") Check(request.Headers.GetValues("X-Fixture").Single() == "request");
                return Response();
            });
            var options = Options with { SessionId = "session", CachePrompt = mode != "disabled" };
            if (mode is "override" or "delete") options = options with
            {
                ModelHeaders = ImmutableDictionary<string, string?>.Empty.Add("X-Affinity", "model").Add("X-Fixture", "model"),
                Headers = ImmutableDictionary<string, string?>.Empty.Add("x-affinity", mode == "delete" ? null : "request").Add("x-fixture", "request")
            };
            using var provider = NativeProviderFactory.CreateMistral(Model, Endpoint, "fake-key", options, handler);
            Check(await Done(provider.Transport, Request));
        }
    }
    private static async Task DirectThinking()
    {
        using var handler = new Handler(async request =>
        {
            var wire = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
            Check(wire.GetProperty("prompt_mode").GetString() == "reasoning" && wire.GetProperty("reasoning_effort").GetString() == "low" &&
                wire.GetProperty("max_tokens").GetDouble() == 1000);
            return Response("[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"unsolicited\"}]}]");
        });
        using var provider = NativeProviderFactory.CreateMistral(Model, Endpoint, "fake-key",
            Options with { MaxTokens = 1000, PromptMode = "reasoning", ReasoningEffort = "low" }, handler);
        var events = new List<StreamEvent>(); await foreach (var item in provider.StreamAsync(Request)) events.Add(item);
        Check(events[^1] is StreamDone && events.OfType<ThinkingStarted>().Count() == 1 && events.OfType<ThinkingEnded>().Single().Content == "unsolicited" &&
            provider.GetSupportedThinkingLevels(Model).SequenceEqual(["off"]));
    }
    private static Task Refusals()
    {
        var callbacks = 0; using var handler = new Handler(_ => throw new InvalidOperationException("No refused HTTP."));
        var options = Options with { OnPayload = (_, _, _) => { callbacks++; return ValueTask.FromResult<JsonData?>(null); } };
        foreach (var row in new[] { JsonData.Parse("{}"), JsonData.Parse(Metadata().ToString().Replace(Model.Id, "other", StringComparison.Ordinal)) })
        {
            try { using var refused = NativeProviderFactory.CreateMistralSimple(Model, Endpoint, "fake-key", row, options, handler); throw new InvalidOperationException("Expected refusal."); }
            catch (MistralTextException) { }
        }
        try { using var refused = NativeProviderFactory.CreateMistral(Model, new("https://other.invalid/"), "fake-key", options, handler); throw new InvalidOperationException("Expected refusal."); }
        catch (ArgumentException) { }
        var first = options.OnPayload!;
        try { using var refused = NativeProviderFactory.CreateMistral(Model, Endpoint, "fake-key", options with { OnPayload = first + first }, handler); throw new InvalidOperationException("Expected refusal."); }
        catch (MistralTextException) { }
        Check(callbacks == 0 && handler.Calls == 0 && !handler.Disposed); return Task.CompletedTask;
    }
    private static async Task SimpleMetadata()
    {
        var row = JsonData.Parse(JsonSerializer.Serialize(new
        {
            id = Model.Id, api = Model.Api, provider = Model.Provider, contextWindow = 20_000, maxTokens = 1000, reasoning = true,
            thinkingLevelMap = new Dictionary<string, string?> { ["off"] = "none", ["high"] = "medium" },
            headers = new Dictionary<string, string> { ["X-Metadata"] = "row" }
        }));
        foreach (var level in new string?[] { null, "off", "high" })
        {
            using var handler = new Handler(async request =>
            {
                var wire = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
                Check(wire.GetProperty("reasoning_effort").GetString() == (level == "high" ? "medium" : "none") &&
                    !wire.TryGetProperty("prompt_mode", out _) && wire.GetProperty("max_tokens").GetDouble() == 1000 &&
                    request.Headers.GetValues("X-Metadata").Single() == "request");
                return Response();
            });
            using var provider = NativeProviderFactory.CreateMistralSimple(Model, Endpoint, "fake-key", row,
                Options with { PromptMode = "reasoning", ReasoningEffort = "low",
                    Headers = ImmutableDictionary<string, string?>.Empty.Add("x-metadata", "request") }, handler);
            Check(await Done(provider.Transport, Request with { ThinkingLevel = level }) && provider.GetSupportedThinkingLevels(Model).Contains("high"));
        }
    }
    private static async Task ToolChoices()
    {
        foreach (var choice in new[] { "\"auto\"", "\"none\"", "\"any\"", "\"required\"", "{\"type\":\"function\",\"function\":{\"name\":\"lookup\"}}" })
        {
            var expected = JsonData.Parse(choice); var observed = false;
            using var handler = new Handler(async request =>
            {
                var wire = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
                Check(JsonElement.DeepEquals(wire.GetProperty("tool_choice"), expected.Value) && !wire.TryGetProperty("toolChoice", out _)); return Response();
            });
            using var provider = NativeProviderFactory.CreateMistral(Model, Endpoint, "fake-key", Options with { ToolChoice = expected,
                OnPayload = (payload, _, _) => { observed = JsonElement.DeepEquals(payload.Value.GetProperty("toolChoice"), expected.Value); return ValueTask.FromResult<JsonData?>(null); } }, handler);
            Check(await Done(provider.Transport, Request) && observed);
        }
    }
    private static HttpResponseMessage Response(string content = "\"answer\"") => new(HttpStatusCode.OK)
    { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":" + content + "},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    private static async Task<bool> Done(IChatTransport transport, ChatRequest request)
    { StreamEvent? last = null; await foreach (var item in transport.StreamAsync(request)) last = item; return last is StreamDone; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls; public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Mistral authored fixture assertion failed."); }
}
