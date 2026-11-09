using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.AI.Protocols.Bedrock;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.GoogleVertex;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

// Authored expectations from upstream 7d073328 packages/ai/src/api/simple-options.ts.
// No captured-source execution or live-provider evidence is claimed.
internal static class Program
{
    private const int ModelCap = 131072;
    private static readonly Uri Endpoint = new("https://synthetic.invalid");
    private sealed record Case(string Name, int Window, double Input, int? Cap, double Expected);
    private static readonly Case[] Cases =
    [
        new("ratio-margin", 200000, 100000, null, 85000),
        new("ceil-ratio", 200000, 100001, null, 84998),
        new("fractional-input", 200000, 100000.5, null, 84998.5),
        new("ratio-boundary", 147768, 100000, null, 32768),
        new("fallback-boundary", 147767, 100000, null, 32768),
        new("fallback-output-cap", 200000, 160000, null, 32768),
        new("fallback-fixed-room", 150000, 120000, null, 25904),
        new("small-empty-window", 32768, 0, null, 28672),
        new("small-window", 32768, 25000, null, 3672),
        new("tiny-window", 1000, 0, null, 1),
        new("overfull-window", 32768, 40000, null, 1),
        new("unknown-window", 0, 100000, null, ModelCap),
        new("negative-window", -1, 100000, null, ModelCap),
        new("explicit-small-cap", 200000, 160000, 10000, 10000),
        new("explicit-large-cap", 200000, 160000, 40000, 32768),
        new("explicit-over-model-cap", 1000000, 100000, 200000, 200000),
        new("model-cap", 1000000, 100000, null, ModelCap)
    ];

    public static async Task<int> Main()
    {
        using var client = new HttpClient(new RejectSend());
        var count = 0;
        foreach (var provider in new[] { "completions", "anthropic", "azure", "google", "vertex", "mistral", "bedrock" })
        foreach (var test in Cases)
        {
            var actual = await Resolve(client, provider, test);
            // Converse's AWS integer wire field truncates the resolved JavaScript number.
            Equal(provider == "bedrock" ? Math.Truncate(test.Expected) : test.Expected, actual, provider + "/" + test.Name);
            count++;
        }
        foreach (var provider in new[] { "anthropic", "bedrock" })
        {
            // Legacy budget thinking expands the base cap, then reclamps to context and reserves 1024 answer tokens.
            foreach (var (test, expectedMax, expectedBudget) in new[]
            {
                (new Case("thinking-ratio", 200000, 100000, null, 0), 85000d, 16384d),
                (new Case("thinking-fallback", 200000, 160000, null, 0), 32768d, 16384d),
                (new Case("thinking-answer-reserve", 32768, 25000, null, 0), 3672d, 2648d),
                (new Case("thinking-no-room", 1000, 0, null, 0), 1d, 0d),
                (new Case("thinking-explicit-cap-expands", 200000, 100000, 2000, 0), 18384d, 16384d)
            })
            {
                var (max, budget) = await ResolveThinking(client, provider, test, "high", null);
                Equal(expectedMax, max, provider + "/" + test.Name);
                Equal(expectedBudget, budget, provider + "/" + test.Name + "/budget");
                count++;
            }
            var custom = new Case("custom-thinking-budget", 32768, 25000, null, 0);
            var customResult = await ResolveThinking(client, provider, custom, "high", 2000);
            Equal(3672, customResult.Max, provider + "/custom-max");
            Equal(2000, customResult.Budget, provider + "/custom-budget");
            count++;
        }
        Console.WriteLine($"PASS: {count} authored cross-provider output-budget cases; no network requests.");
        return 0;
    }

    private static ModelDescriptor Model(string provider) => provider switch
    {
        "completions" => new("synthetic", "openai-completions", "openai"),
        "anthropic" => new("claude-synthetic", "anthropic-messages", "anthropic"),
        "azure" => new("synthetic", "azure-openai-responses", "azure-openai-responses"),
        "google" => new("gemini-2.5-flash", "google-generative-ai", "google"),
        "vertex" => new("gemini-2.5-flash", "google-vertex", "google-vertex"),
        "mistral" => new("synthetic", "mistral-conversations", "mistral"),
        _ => new("anthropic.claude-sonnet-4-5", "bedrock-converse-stream", "amazon-bedrock")
    };

    private static JsonData Metadata(ModelDescriptor model, Case test) => JsonData.Parse(JsonSerializer.Serialize(new
    {
        id = model.Id, api = model.Api, provider = model.Provider, name = model.Id,
        baseUrl = model.Api == "azure-openai-responses" ? "https://synthetic.openai.azure.com/openai/v1" : Endpoint.AbsoluteUri,
        contextWindow = test.Window, maxTokens = ModelCap, reasoning = true, input = new[] { "text" },
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 }
    }));

    private static ChatRequest Request(ModelDescriptor model, Case test) => new(model,
    [new("assistant", JsonData.Parse(JsonSerializer.Serialize(new
    {
        role = "assistant", content = Array.Empty<object>(), timestamp = 1, stopReason = "stop",
        api = model.Api, provider = model.Provider, model = model.Id,
        usage = new { input = test.Input, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = test.Input,
            cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 } }
    })))]);

    private static async Task<double> Resolve(HttpClient client, string provider, Case test)
    {
        var model = Model(provider); var metadata = Metadata(model, test); var request = Request(model, test);
        return provider switch
        {
            "completions" => new CompletionsSimpleRequestFactory(client, Endpoint, model,
                new(metadata) { DirectOptions = new() { MaxTokens = test.Cap } }).Resolve(request).MaxTokens,
            "anthropic" => new AnthropicMessagesSimpleRequestFactory(client, Endpoint, model,
                new(metadata, new(ModelCap)) { MaxTokens = test.Cap }).Resolve(request).MaxTokens,
            "azure" => new AzureResponsesSimpleTransport(client, model,
                new(new(metadata, new(true)) { MaxTokens = test.Cap }, "synthetic-key")).Resolve(request).MaxTokens,
            "google" => new GoogleSimpleRequestFactory(client, model,
                new(new(metadata) { MaxTokens = test.Cap })).Resolve(request).MaxTokens,
            "vertex" => new GoogleVertexSimpleTransport(client, model,
                new(new(Endpoint, "synthetic-token", new(metadata) { MaxTokens = test.Cap }), "synthetic", "us-central1")).Resolve(request).MaxTokens,
            "mistral" => new MistralSimpleHttpSseTransport(client, model, metadata,
                new(Endpoint, true, new(0, 0, 0, 0)) { MaxTokens = test.Cap }).Resolve(request).MaxTokens,
            _ => (await Bedrock(client, model, metadata, request, test.Cap, null, null)).Max
        };
    }

    private static async Task<(double Max, double Budget)> ResolveThinking(HttpClient client, string provider, Case test, string level, int? custom)
    {
        var model = Model(provider); var metadata = Metadata(model, test); var request = Request(model, test);
        if (provider == "bedrock") return await Bedrock(client, model, metadata, request, test.Cap, level, custom);
        var resolved = new AnthropicMessagesSimpleRequestFactory(client, Endpoint, model,
            new(metadata, new(ModelCap), level) { MaxTokens = test.Cap,
                ThinkingBudgets = custom is { } value ? JsonData.Parse($"{{\"high\":{value}}}") : null }).Resolve(request);
        return (resolved.MaxTokens, resolved.ThinkingBudgetTokens);
    }

    private static async Task<(double Max, double Budget)> Bedrock(HttpClient client, ModelDescriptor model, JsonData metadata,
        ChatRequest request, int? cap, string? level, int? custom)
    {
        var environment = new AwsEnvironment(_ => null, "", client, readFile: _ => null);
        var transport = new BedrockConverseStreamTransport(client, model, metadata,
            new() { Region = "us-east-1", BearerToken = "synthetic-token", MaxTokens = cap,
                ThinkingBudgets = custom is { } value ? ImmutableDictionary<string, int>.Empty.Add("high", value) : null }, environment, level);
        using var http = await transport.CreateRequestAsync(request);
        using var json = JsonDocument.Parse(await http.Content!.ReadAsStringAsync());
        var root = json.RootElement;
        return (root.GetProperty("inferenceConfig").GetProperty("maxTokens").GetDouble(), level is null ? 0 :
            root.GetProperty("additionalModelRequestFields").GetProperty("thinking").GetProperty("budget_tokens").GetDouble());
    }

    private static void Equal(double expected, double actual, string name)
    {
        if (expected != actual) throw new InvalidOperationException($"{name}: expected {expected}, received {actual}.");
    }
    private sealed class RejectSend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Budget resolution must never send HTTP.");
    }
}
