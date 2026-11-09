using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.AI.Providers;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

// Authored offline regression matrix from frozen upstream 7d073328e44337d3292a7db9b0f0308f679c5e97.
// This intentionally exercises post-1.5 compatibility changes; it is not an upstream execution capture.
internal static class Program
{
    private const string Key = "inert-offline-test-key";
    private static readonly ModelDescriptor Completions = new("model", "openai-completions", "openai");
    private static readonly ModelDescriptor Responses = new("model", "openai-responses", "openai");
    private static readonly ModelDescriptor Azure = new("model", "azure-openai-responses", "azure");
    private static int assertions;
    private static void Check(bool value, string message)
    { assertions++; if (!value) throw new InvalidOperationException(message); }
    private static TranscriptEntry Entry(string text)
    { var data = JsonData.Parse(text); return new(data.Value.GetProperty("role").GetString()!, data); }
    private static ImmutableArray<TranscriptEntry> History() =>
    [
        new("assistant", PiWireJson.WriteMessage(new(Completions.Api, Completions.Provider, Completions.Id, 1,
            [new ToolCallContent("call", "inspect", JsonData.EmptyObject)], TokenUsage.Zero, StopReason.ToolUse))),
        Entry("""{"role":"toolResult","toolCallId":"call","toolName":"inspect","content":[{"type":"text","text":"result"}],"isError":false,"timestamp":2}"""),
        Entry("""{"role":"user","content":"next","timestamp":3}""")
    ];
    private static JsonData Metadata(ModelDescriptor model, string baseUrl, JsonObject? compat = null)
        => JsonData.Parse(new JsonObject
        {
            ["id"] = model.Id, ["api"] = model.Api, ["provider"] = model.Provider, ["baseUrl"] = baseUrl,
            ["reasoning"] = false, ["input"] = new JsonArray("text"), ["contextWindow"] = 100000, ["maxTokens"] = 4096,
            ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 },
            ["compat"] = compat ?? new JsonObject()
        }.ToJsonString());
    private static async Task<JsonElement> Body(HttpRequestMessage request)
    { using (request) using (var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync())) return json.RootElement.Clone(); }
    private static async Task Main()
    {
        await ToolsAndContent(); await CompletionsCache(); await ResponsesCacheAndHeaders(); await AzureCache(); await SimplePropagation(); Retry();
        Console.WriteLine($"PASS OpenAI compatibility gap matrix: {assertions} assertions; offline authored expectations.");
    }
    private static async Task ToolsAndContent()
    {
        var endpoint = new Uri("https://proxy.invalid/v1/chat/completions");
        foreach (var modelFlag in new bool?[] { null, false, true })
        foreach (var optionFlag in new bool?[] { null, false, true })
        foreach (var withHistory in new[] { false, true })
        {
            var compat = new JsonObject(); if (modelFlag.HasValue) compat["requiresToolsWithToolHistory"] = modelFlag.Value;
            var options = new CompletionsKeyAuthRequestOptions { RequiresToolsWithToolHistory = optionFlag, ModelMetadata = Metadata(Completions, "https://proxy.invalid/v1", compat) };
            var factory = new CompletionsKeyAuthRequestFactory(endpoint, Completions, options: options);
            var body = await Body(factory.Create(new(Completions, withHistory ? History() : []), Key));
            Check(body.TryGetProperty("tools", out var tools) == (withHistory && (optionFlag ?? modelFlag ?? false)), "tools history option precedence");
            if (body.TryGetProperty("tools", out tools)) Check(tools.GetArrayLength() == 0, "history compatibility sends empty tools");
        }
        var declared = Entry("""{"role":"system","content":"","toolsAdded":[{"name":"inspect","description":"Inspect","parameters":{"type":"object","properties":{}}}],"timestamp":0}""");
        var declaredBody = await Body(new CompletionsKeyAuthRequestFactory(endpoint, Completions).Create(new(Completions, [declared]), Key));
        Check(declaredBody.GetProperty("tools").GetArrayLength() == 1, "nonempty tools unchanged");
        foreach (var modelFlag in new[] { false, true })
        foreach (var optionFlag in new bool?[] { null, false, true })
        foreach (var synthetic in new[] { false, true })
        {
            var metadata = Metadata(Completions, "https://proxy.invalid/v1", new() { ["requiresNonNullAssistantContent"] = modelFlag, ["requiresAssistantAfterToolResult"] = synthetic });
            var factory = new CompletionsKeyAuthRequestFactory(endpoint, Completions,
                new() { RequiresNonNullAssistantContent = optionFlag }, new() { ModelMetadata = metadata });
            var messages = (await Body(factory.Create(new(Completions, History()), Key))).GetProperty("messages");
            Check(messages.GetArrayLength() == (synthetic ? 4 : 3), "nonnull content must not introduce synthetic assistant");
            var content = messages[0].GetProperty("content");
            Check(content.ValueKind == (synthetic || (optionFlag ?? modelFlag) ? JsonValueKind.String : JsonValueKind.Null), "nonnull assistant content precedence");
            if (content.ValueKind == JsonValueKind.String) Check(content.GetString() == "", "textless content must be empty");
        }
    }
    private static async Task CompletionsCache()
    {
        foreach (var host in new[] { "https://api.openai.com/v1", "https://proxy.invalid/v1" })
        foreach (var retention in Enum.GetValues<CompletionsCacheRetention>())
        foreach (var supported in new[] { false, true })
        foreach (var modelMode in new string?[] { null, "auto", "enabled", "disabled" })
        foreach (var optionMode in new string?[] { null, "auto", "enabled", "disabled" })
        {
            var compat = new JsonObject { ["supportsLongCacheRetention"] = supported };
            if (modelMode is not null) compat["promptCacheKeyMode"] = modelMode;
            var options = new CompletionsKeyAuthRequestOptions(SessionId: "session", CacheRetention: retention)
            { PromptCacheKeyMode = optionMode, ModelMetadata = Metadata(Completions, host, compat) };
            var body = await Body(new CompletionsKeyAuthRequestFactory(new(host + "/chat/completions"), Completions, options: options).Create(new(Completions, []), Key));
            var mode = optionMode ?? modelMode ?? "auto";
            var expected = retention != CompletionsCacheRetention.None && (mode == "enabled" || mode == "auto" && (host.Contains("api.openai.com") || retention == CompletionsCacheRetention.Long && supported));
            Check(body.TryGetProperty("prompt_cache_key", out _) == expected, "completions cache matrix");
            Check(body.TryGetProperty("prompt_cache_retention", out _) == (retention == CompletionsCacheRetention.Long && supported), "disabled key must preserve retention");
        }
        var explicitOptions = new CompletionsKeyAuthRequestOptions(SessionId: "session", SendSessionAffinityHeaders: true, Headers: JsonData.Parse("""{"session_id":"caller"}"""))
        { PromptCacheKeyMode = "disabled", SamplingParams = JsonData.Parse("""{"prompt_cache_key":"sampling"}""") };
        using var request = new CompletionsKeyAuthRequestFactory(new("https://proxy.invalid/v1/chat/completions"), Completions, options: explicitOptions).Create(new(Completions, []), Key);
        Check(request.Headers.GetValues("session_id").Single() == "caller" && !request.Headers.Contains("session-id"), "Completions header spelling and caller precedence");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("prompt_cache_key").GetString() == "sampling", "sampling remains last");
    }
    private static async Task ResponsesCacheAndHeaders()
    {
        foreach (var mode in new string?[] { null, "auto", "enabled", "disabled" })
        foreach (var retention in new[] { "none", "short", "long" })
        {
            var options = new ResponsesKeyAuthRequestOptions(SessionId: "session", CacheRetention: retention) { PromptCacheKeyMode = mode };
            using var request = new ResponsesKeyAuthRequestFactory(new("https://proxy.invalid/v1/responses"), Responses, new(false), options).Create(new(Responses, []), Key);
            Check(request.Headers.Contains("session-id") == (retention != "none"), "Responses session-id header");
            Check(!request.Headers.Contains("session_id"), "Responses must not generate underscore header");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(json.RootElement.TryGetProperty("prompt_cache_key", out _) == (retention != "none" && mode != "disabled"), "Responses cache direct matrix");
        }
        foreach (var modelMode in new[] { "auto", "enabled", "disabled" })
        foreach (var optionMode in new string?[] { null, "auto", "enabled", "disabled" })
        {
            using var handler = new Capture();
            var options = new ResponsesKeyAuthRequestOptions(CacheRetention: "short") { PromptCacheKeyMode = optionMode, Headers = JsonData.Parse("""{"session-id":"caller","x-client-request-id":null}""") };
            using var provider = NativeProviderFactory.CreateResponses(Responses, new("https://proxy.invalid/v1/responses"), Key, new(false), options, handler,
                Metadata(Responses, "https://proxy.invalid/v1", new() { ["promptCacheKeyMode"] = modelMode }));
            var result = await new ChatClient(provider).CompleteAsync(new(Responses, []) { SessionId = "session", ThinkingLevel = "off" });
            Check(result.Failure is null, "native Responses offline completion");
            Check(handler.Payload!.Value.TryGetProperty("prompt_cache_key", out _) == ((optionMode ?? modelMode) != "disabled"), "Responses native metadata and scoped request propagation");
            Check(handler.SessionHeader == "caller" && !handler.ClientHeader, "Responses caller header precedence/removal");
        }
    }
    private static async Task AzureCache()
    {
        foreach (var modelMode in new[] { "auto", "enabled", "disabled" })
        foreach (var optionMode in new string?[] { null, "auto", "enabled", "disabled" })
        {
            var metadata = Metadata(Azure, "https://resource.openai.azure.com/openai/v1", new() { ["promptCacheKeyMode"] = modelMode });
            var options = new AzureResponsesOptions(metadata, new(false)) { PromptCacheKeyMode = optionMode };
            var factory = new AzureResponsesRequestFactory(Azure, options);
            var body = factory.ProjectPayload(new(Azure, []) { SessionId = "session" }).Value;
            Check(body.TryGetProperty("prompt_cache_key", out _) == ((optionMode ?? modelMode) != "disabled"), "Azure model/direct/scoped cache propagation");
            using var request = await factory.CreateAsync(new(Azure, []) { SessionId = "session" }, Key);
            Check(!request.Headers.Contains("session-id") && !request.Headers.Contains("session_id"), "Azure keeps existing session header behavior");
        }
        foreach (var invalid in new[] { "AUTO", "on", "" })
        {
            try { _ = new ResponsesKeyAuthRequestFactory(new("https://proxy.invalid/responses"), Responses, new(false), new() { PromptCacheKeyMode = invalid }); throw new Exception("accepted invalid mode"); }
            catch (ResponsesKeyAuthRequestException) { assertions++; }
            try { _ = new CompletionsKeyAuthRequestFactory(new("https://proxy.invalid/chat/completions"), Completions, options: new() { PromptCacheKeyMode = invalid }); throw new Exception("accepted invalid mode"); }
            catch (CompletionsRequestException) { assertions++; }
        }
    }
    private static async Task SimplePropagation()
    {
        foreach (var enabled in new[] { false, true })
        {
            var compat = new JsonObject { ["promptCacheKeyMode"] = enabled ? "enabled" : "disabled", ["requiresToolsWithToolHistory"] = enabled, ["requiresNonNullAssistantContent"] = enabled };
            using var handler = new Capture(completions: true); using var client = new HttpClient(handler);
            var transport = new CompletionsSimpleRequestFactory(client, new("https://proxy.invalid/v1/chat/completions"), Completions,
                new(Metadata(Completions, "https://proxy.invalid/v1", compat)) { ApiKey = Key });
            await foreach (var _ in transport.StreamAsync(new(Completions, History()) { SessionId = "session" })) { }
            var payload = handler.Payload!.Value;
            Check(payload.TryGetProperty("prompt_cache_key", out _) == enabled, "Simple Completions cache mode propagation");
            Check(payload.TryGetProperty("tools", out _) == enabled, "Simple Completions tools compatibility propagation");
            Check(payload.GetProperty("messages")[0].GetProperty("content").ValueKind == (enabled ? JsonValueKind.String : JsonValueKind.Null), "Simple Completions content compatibility propagation");
            using var azureHandler = new Capture(); using var azureClient = new HttpClient(azureHandler);
            var azureOptions = new AzureResponsesOptions(Metadata(Azure, "https://resource.openai.azure.com/openai/v1", new() { ["promptCacheKeyMode"] = enabled ? "enabled" : "disabled" }), new(false));
            var azureTransport = new AzureResponsesSimpleTransport(azureClient, Azure, new(azureOptions, Key));
            await foreach (var _ in azureTransport.StreamAsync(new(Azure, []) { SessionId = "session" })) { }
            Check(azureHandler.Payload!.Value.TryGetProperty("prompt_cache_key", out _) == enabled, "Simple Azure cache mode propagation");
        }
    }
    private static void Retry()
    {
        var method = typeof(DurableJson).Assembly.GetType("PiSharp.Durable.Internal.PiAi.PiAiRetry", true)!.GetMethod("IsRetryableAssistantError", BindingFlags.Static | BindingFlags.Public)!;
        var phrase = "Claude Code's stream from Anthropic was cut short after the reply had begun";
        foreach (var (text, expected) in new[] { (phrase, true), (phrase.ToUpperInvariant(), true), ("cut short after the reply had begun", true),
            (phrase + " insufficient_quota", false), (phrase + " billing", false), (phrase + " quota exceeded", false),
            ("reply was cut short", false), ("cut short after the reply started", false), ("invalid JSON input", false) })
        foreach (var stopped in new[] { false, true })
        {
            var message = DurableJson.Object([new("stopReason", stopped ? "stop" : "error"), new("errorMessage", text)]);
            var durable = (bool)method.Invoke(null, [message])!;
            Check(durable == (expected && !stopped), "Durable exact retry phrase and exclusions");
            Check(AgentRetryPolicy.IsRetryableError(stopped ? StopReason.Stop : StopReason.Error, text) == durable, "retry classifier parity");
        }
    }
    private sealed class Capture(bool completions = false) : HttpMessageHandler
    {
        internal JsonElement? Payload; internal string? SessionHeader; internal bool ClientHeader;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Payload = json.RootElement.Clone();
            SessionHeader = request.Headers.TryGetValues("session-id", out var values) ? values.Single() : null;
            ClientHeader = request.Headers.Contains("x-client-request-id");
            return new(HttpStatusCode.OK) { Content = new StringContent(completions ? "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n" : "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"response-fixture\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0,\"total_tokens\":1}}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
