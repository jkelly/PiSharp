using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Authored expectations from pinned getCompat, not original/SDK execution captures.
internal static class ResponsesModelCacheCompatibilityTests
{
    private static readonly ModelDescriptor Model = new("model-cache", "openai-responses", "openai");
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static JsonData Metadata(string? compat, bool reasoning = false)
    {
        var value = new JsonObject { ["id"] = Model.Id, ["api"] = Model.Api, ["provider"] = Model.Provider,
            ["reasoning"] = reasoning, ["maxTokens"] = 4096, ["contextWindow"] = 100000 };
        if (compat is not null) value["compat"] = JsonNode.Parse(compat);
        return JsonData.Parse(value.ToJsonString());
    }
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("responses-model-cache.authoritative-flags-through-direct-and-thinking", Matrix);
        yield return ("responses-model-cache.missing-null-defaults-and-no-metadata-profile", Defaults);
        yield return ("responses-model-cache.malformed-flags-and-model-identity-before-send", Admission);
        yield return ("responses-model-cache.captured-retention-input-and-pre-cancel", CapturedAndCancel);
    }
    private static void Payload(string body, string retention, bool longSupported, bool explicitMode)
    {
        var value = JsonData.Parse(body).Value;
        Check(value.TryGetProperty("prompt_cache_key", out var key) == (retention != "none"), "Metadata changed cache key preference");
        if (retention != "none") Check(key.GetString() == "model-session", "Cache identity changed");
        var longEnabled = retention == "long" && longSupported;
        Check(value.TryGetProperty("prompt_cache_retention", out var ttl) == (longEnabled && !explicitMode), "Model24h compatibility ignored");
        if (longEnabled && !explicitMode) Check(ttl.GetString() == "24h", "Wrong long retention value");
        var expectedOptions = explicitMode && (retention == "none" || longEnabled);
        Check(value.TryGetProperty("prompt_cache_options", out var options) == expectedOptions, "Model explicit cache compatibility ignored");
        if (expectedOptions)
        {
            var fields = options.EnumerateObject().ToArray();
            Check(fields.Length == 1 && fields[0].Name == (retention == "none" ? "mode" : "ttl") &&
                fields[0].Value.GetString() == (retention == "none" ? "explicit" : "30m"), "Model cache modes mixed");
        }
    }
    private static async Task Matrix()
    {
        foreach (var longSupported in new[] { false, true })
        foreach (var explicitMode in new[] { false, true })
        foreach (var retention in new[] { "none", "short", "long" })
        foreach (var reasoning in new[] { false, true })
        {
            var metadata = Metadata(new JsonObject { ["supportsLongCacheRetention"] = longSupported,
                ["supportsExplicitPromptCacheMode"] = explicitMode }.ToJsonString(), reasoning);
            var before = metadata.ToString();
            // Deliberately conflicting direct flags prove that model metadata is authoritative.
            var options = new ResponsesKeyAuthRequestOptions(SessionId: "model-session", CacheRetention: retention,
                SupportsLongCacheRetention: !longSupported, SupportsExplicitPromptCacheMode: !explicitMode);
            using var handler = new Handler();
            using (var provider = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(reasoning), options, handler, metadata))
            {
                var client = new ChatClient(provider);
                foreach (var level in reasoning ? new string?[] { null, "off", "high" } : [null, "off"])
                {
                    var result = await client.CompleteAsync(new(Model, [], 1) { ThinkingLevel = level });
                    Check(result.Failure is null && result.Message.StopReason == StopReason.Stop, "Actual provider stream failed");
                    Payload(handler.Bodies[^1], retention, longSupported, explicitMode);
                }
                Check(handler.Bodies.Count == (reasoning ? 3 : 2), "Thinking profiles changed send count");
            }
            Check(!handler.Disposed && metadata.ToString() == before, "Metadata or borrowed handler ownership changed");
            Check(options.SupportsLongCacheRetention == !longSupported && options.SupportsExplicitPromptCacheMode == !explicitMode,
                "Caller request options mutated");
        }
    }
    private static async Task Defaults()
    {
        foreach (var compat in new string?[] { null, "null", "{}", """{"supportsLongCacheRetention":null,"supportsExplicitPromptCacheMode":null}""" })
        {
            using var handler = new Handler();
            var options = new ResponsesKeyAuthRequestOptions(SessionId: "model-session", CacheRetention: "long",
                SupportsLongCacheRetention: false, SupportsExplicitPromptCacheMode: true);
            using var provider = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), options, handler, Metadata(compat));
            var result = await new ChatClient(provider).CompleteAsync(new(Model, []));
            Check(result.Failure is null, "Missing/null model flags rejected");
            Payload(handler.Bodies.Single(), "long", true, false);
        }
        foreach (var longSupported in new[] { false, true })
        foreach (var explicitMode in new[] { false, true })
        {
            using var handler = new Handler();
            var options = new ResponsesKeyAuthRequestOptions(SessionId: "model-session", CacheRetention: "long",
                SupportsLongCacheRetention: longSupported, SupportsExplicitPromptCacheMode: explicitMode);
            using var provider = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), options, handler);
            var result = await new ChatClient(provider).CompleteAsync(new(Model, []));
            Check(result.Failure is null, "Metadata-free explicit profile changed");
            Payload(handler.Bodies.Single(), "long", longSupported, explicitMode);
        }
        using var defaultsHandler = new Handler();
        using var defaults = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), handler: defaultsHandler, modelMetadata: Metadata("{}"));
        var defaultsResult = await new ChatClient(defaults).CompleteAsync(new(Model, []));
        Check(defaultsResult.Failure is null, "Null request options rejected with metadata");
        var payload = JsonData.Parse(defaultsHandler.Bodies.Single()).Value;
        Check(!payload.TryGetProperty("prompt_cache_key", out _) && !payload.TryGetProperty("prompt_cache_retention", out _) &&
            !payload.TryGetProperty("prompt_cache_options", out _), "Metadata introduced caching without caller preference/session");
    }
    private static Task Admission()
    {
        using var handler = new Handler();
        foreach (var compat in new[] { "false", "0", "[]", "\"invalid\"" })
        {
            try { using var rejected = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), handler: handler,
                modelMetadata: Metadata(compat)); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Malformed compatibility object admitted");
        }
        foreach (var flag in new[] { "supportsLongCacheRetention", "supportsExplicitPromptCacheMode" })
        foreach (var invalid in new[] { "0", "1", "\"false\"", "\"true\"", "[]", "{}" })
        {
            try { using var rejected = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), handler: handler,
                modelMetadata: Metadata("{\"" + flag + "\":" + invalid + "}")); }
            catch (ArgumentException error) { Check(error.InnerException is null && !error.Message.Contains(invalid, StringComparison.Ordinal), "Metadata diagnostic retained arbitrary value"); continue; }
            throw new InvalidOperationException("Malformed typed cache flag admitted");
        }
        foreach (var field in new[] { "id", "api", "provider", "reasoning" })
        {
            var raw = JsonNode.Parse(Metadata("""{"supportsLongCacheRetention":false}""").ToString())!;
            raw[field] = field == "reasoning" ? JsonValue.Create(true) : JsonValue.Create("foreign");
            try { using var rejected = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), handler: handler,
                modelMetadata: JsonData.Parse(raw.ToJsonString())); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Cache metadata bypassed existing same-model admission");
        }
        Check(handler.Bodies.Count == 0 && !handler.Disposed, "Invalid metadata acquired HTTP or disposed borrowed handler");
        return Task.CompletedTask;
    }
    private static async Task CapturedAndCancel()
    {
        var metadata = Metadata("""{"supportsLongCacheRetention":true,"supportsExplicitPromptCacheMode":true}""");
        foreach (var requested in new string?[] { null, "none", "short" })
        {
            var environment = new ProviderEnvironmentSnapshot(process: new Dictionary<string, string?> { ["PI_CACHE_RETENTION"] = "long" });
            var options = new ResponsesKeyAuthRequestOptions(SessionId: "model-session", CacheRetention: requested, Environment: environment);
            using var handler = new Handler();
            using var provider = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), options, handler, metadata);
            var client = new ChatClient(provider);
            var result = await client.CompleteAsync(new(Model, []) { ThinkingLevel = "off" });
            Check(result.Failure is null, "Captured cache input lost during metadata binding");
            Payload(handler.Bodies.Single(), requested ?? "long", true, true);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var aborted = await client.CompleteAsync(new(Model, []), canceled.Token);
            Check(aborted.Failure?.Kind == ChatFailureKind.Cancelled && handler.Bodies.Count == 1, "Pre-cancel acquired model profile request");
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<string> Bodies = []; internal bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Headers.Authorization?.Parameter == "sk-inert-key", "Metadata changed explicit key");
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
