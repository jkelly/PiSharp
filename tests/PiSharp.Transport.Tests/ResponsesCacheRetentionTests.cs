using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Authored expectations from pinned Pi openai-responses.ts, not captured SDK outcomes.
internal static class ResponsesCacheRetentionTests
{
    private static readonly ModelDescriptor Model = new("cache-model", "openai-responses", "openai");
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private static ChatRequest Request(string? thinking = null) => new(Model, [], 1) { ThinkingLevel = thinking };
    private static ResponsesKeyAuthRequestFactory Factory(ResponsesKeyAuthRequestOptions options) => new(Endpoint, Model, new(false), options);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("responses-cache.retention-compatibility-matrix-and-default", Matrix);
        yield return ("responses-cache.disabled-session-and-invalid-admission", Admission);
        yield return ("responses-cache.captured-environment-default-and-explicit-precedence", EnvironmentDefaults);
        yield return ("responses-cache.aggregate-payload-byte-boundary", ByteBoundary);
        yield return ("responses-cache.explicit-model-profile-and-identity-admission", ModelProfiles);
        yield return ("responses-cache.native-provider-thinking-repeat-and-cancel", Provider);
    }
    private static IEnumerable<ResponsesKeyAuthRequestOptions> Options()
    {
        foreach (var retention in new[] { "none", "short", "long" })
        foreach (var supportsLong in new[] { false, true })
        foreach (var explicitMode in new[] { false, true })
            yield return new(SessionId: "cache-session", CacheRetention: retention,
                SupportsLongCacheRetention: supportsLong, SupportsExplicitPromptCacheMode: explicitMode);
    }
    private static void CheckPayload(JsonData data, ResponsesKeyAuthRequestOptions options)
    {
        var payload = data.Value;
        Check(payload.GetProperty("stream").GetBoolean() && !payload.GetProperty("store").GetBoolean(), "Cache policy changed stream/store");
        var hasKey = payload.TryGetProperty("prompt_cache_key", out var key);
        Check(hasKey == (options.CacheRetention != "none" && options.SessionId is not null), "Cache key omission mismatch");
        if (hasKey) Check(key.GetString() == options.SessionId, "Cache key changed");
        var longEnabled = options.CacheRetention == "long" && options.SupportsLongCacheRetention;
        var hasRetention = payload.TryGetProperty("prompt_cache_retention", out var retention);
        Check(hasRetention == (longEnabled && !options.SupportsExplicitPromptCacheMode), "24h retention mismatch");
        if (hasRetention) Check(retention.GetString() == "24h", "Wrong retention duration");
        var hasOptions = payload.TryGetProperty("prompt_cache_options", out var cache);
        Check(hasOptions == (options.SupportsExplicitPromptCacheMode && (options.CacheRetention == "none" || longEnabled)), "Explicit cache options mismatch");
        if (hasOptions)
        {
            var fields = cache.EnumerateObject().ToArray();
            Check(fields.Length == 1, "Cache modes mixed");
            Check(options.CacheRetention == "none" ? fields[0].Name == "mode" && fields[0].Value.GetString() == "explicit" :
                fields[0].Name == "ttl" && fields[0].Value.GetString() == "30m", "Wrong explicit cache policy");
        }
    }
    private static async Task Matrix()
    {
        foreach (var options in Options().Concat([new ResponsesKeyAuthRequestOptions(SessionId: "cache-session")]))
        {
            var factory = Factory(options);
            using var first = factory.Create(Request(), "sk-inert-key");
            using var second = factory.Create(Request(), "sk-inert-key");
            var body = await first.Content!.ReadAsStringAsync();
            CheckPayload(JsonData.Parse(body), options);
            Check(body == await second.Content!.ReadAsStringAsync() && !ReferenceEquals(first.Content, second.Content), "Factory reuse changed body ownership");
        }
        foreach (var options in Options().Select(x => x with { SessionId = null }))
        {
            using var http = Factory(options).Create(Request(), "sk-inert-key");
            CheckPayload(JsonData.Parse(await http.Content!.ReadAsStringAsync()), options);
        }
    }
    private static async Task Admission()
    {
        foreach (var retention in new[] { "", "LONG", "24h", "other" })
        {
            try { Factory(new(CacheRetention: retention!)); }
            catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.UnsupportedOptions) { continue; }
            throw new InvalidOperationException("Unsupported retention accepted");
        }
        // Source does not inspect or clamp the unused session ID when caching is disabled.
        foreach (var session in new[] { new string('s', 5000), "\uD800" })
        {
            var options = new ResponsesKeyAuthRequestOptions(SessionId: session, CacheRetention: "none", SupportsExplicitPromptCacheMode: true);
            using var http = Factory(options).Create(Request(), "sk-inert-key");
            CheckPayload(JsonData.Parse(await http.Content!.ReadAsStringAsync()), options);
        }
    }
    private static async Task EnvironmentDefaults()
    {
        var scopes = new (string? Scoped, string? Process, string Expected)[]
        {
            (null, null, "short"), ("long", null, "long"), (null, "long", "long"),
            ("", "long", "long"), ("short", "long", "short"), ("LONG", "long", "short"),
            ("none", null, "short"), (null, "LONG", "short")
        };
        foreach (var (scoped, process, expected) in scopes)
        foreach (var requested in new string?[] { null, "none", "short", "long" })
        {
            var scopedInputs = new Dictionary<string, string?> { ["PI_CACHE_RETENTION"] = scoped };
            var processInputs = new Dictionary<string, string?> { ["PI_CACHE_RETENTION"] = process };
            var environment = new ProviderEnvironmentSnapshot(scopedInputs, processInputs);
            // Snapshot owns its copied inputs, including when request factories are reused.
            scopedInputs["PI_CACHE_RETENTION"] = "changed"; processInputs["PI_CACHE_RETENTION"] = "changed";
            var options = new ResponsesKeyAuthRequestOptions(SessionId: "cache-session", CacheRetention: requested, Environment: environment);
            var factory = Factory(options);
            using var first = factory.Create(Request(), "sk-inert-key");
            using var second = factory.Create(Request(), "sk-inert-key");
            var effective = options with { CacheRetention = requested ?? expected };
            CheckPayload(JsonData.Parse(await first.Content!.ReadAsStringAsync()), effective);
            CheckPayload(JsonData.Parse(await second.Content!.ReadAsStringAsync()), effective);
        }
    }
    private static async Task ByteBoundary()
    {
        foreach (var options in Options())
        {
            using var baseline = Factory(options).Create(Request(), "sk-inert-key");
            var bytes = (await baseline.Content!.ReadAsByteArrayAsync()).Length;
            using var exact = Factory(options with { MaximumPayloadBytes = bytes }).Create(Request(), "sk-inert-key");
            Check((await exact.Content!.ReadAsByteArrayAsync()).Length == bytes, "Exact cache payload budget rejected");
            try { using var rejected = Factory(options with { MaximumPayloadBytes = bytes - 1 }).Create(Request(), "sk-inert-key"); }
            catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.ResourceLimit) { continue; }
            throw new InvalidOperationException("Cache fields escaped aggregate byte accounting");
        }
    }
    private static async Task Provider()
    {
        var captured = new ResponsesKeyAuthRequestOptions(SessionId: "cache-session",
            Environment: new ProviderEnvironmentSnapshot(process: new Dictionary<string, string?> { ["PI_CACHE_RETENTION"] = "long" }));
        foreach (var (options, expected) in Options().Select(x => (x, x)).Append((captured, captured with { CacheRetention = "long" })))
        {
            using var handler = new Handler(expected);
            using (var provider = NativeProviderFactory.CreateResponses(Model, Endpoint, "sk-inert-key", new(false), options, handler))
            {
                var client = new ChatClient(provider);
                foreach (var thinking in new string?[] { null, "off" })
                {
                    var result = await client.CompleteAsync(Request(thinking));
                    Check(result.Failure is null && result.Message.StopReason == StopReason.Stop, "Injected provider stream failed");
                }
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                var aborted = await client.CompleteAsync(Request(), canceled.Token);
                Check(aborted.Failure?.Kind == ChatFailureKind.Cancelled && handler.Sends == 2, "Pre-cancel acquired HTTP");
            }
            Check(!handler.Disposed, "Provider disposed borrowed handler");
        }
    }
    private static async Task ModelProfiles()
    {
        foreach (var provider in new[] { "openai", "xai", "azure-openai", "github-copilot" })
        {
            var model = Model with { Provider = provider };
            var options = new ResponsesKeyAuthRequestOptions(CacheRetention: "long", SessionId: "cache-session");
            var factory = new ResponsesKeyAuthRequestFactory(Endpoint, model, new(false), options);
            using var http = factory.Create(new(model, [], 1), "sk-inert-key");
            CheckPayload(JsonData.Parse(await http.Content!.ReadAsStringAsync()), options);
            foreach (var wrong in new[] { model with { Id = "other" }, model with { Api = "other" }, model with { Provider = "other" } })
            {
                try { using var rejected = factory.Create(new(wrong, [], 1), "sk-inert-key"); }
                catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.InvalidRequest) { continue; }
                throw new InvalidOperationException("Cache policy bypassed request identity admission");
            }
        }
    }
    private sealed class Handler(ResponsesKeyAuthRequestOptions options) : HttpMessageHandler
    {
        internal int Sends; internal bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            CheckPayload(JsonData.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)), options);
            Check(request.Headers.Authorization?.Parameter == "sk-inert-key", "Cache policy changed explicit authentication");
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
