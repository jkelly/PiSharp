using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Contracts;

// IMPL-A1 provider APIs and authentication. Authored expectations written from the Pi v1.1.0
// (abe508e1b89912adde45528136c3221eb69acdd7) sources packages/ai/src/api/bedrock-converse-stream.ts, api/openai-codex-responses.ts,
// api/github-copilot-headers.ts, api/cloudflare.ts, providers/*.ts and auth/**, plus AWS's published Signature Version 4 test
// suite vectors (awslabs/aws-c-auth tests/aws-signing-test-suite/v4) and the documented signing-key derivation example. Nothing
// here executes upstream code or is an upstream capture; every HTTP peer is an in-process fake and no browser is opened.
internal static partial class Program
{
    private const string SourceSha = "abe508e1b89912adde45528136c3221eb69acdd7";
    private static int bodyComparisons;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new (string Id, Func<Task> Run)[]
        {
            ("sigv4.aws-signing-test-suite-vectors", Sync(SigV4Vectors)),
            ("sigv4.documented-signing-key-derivation", Sync(SigningKeyExample)),
            ("sigv4.bedrock-model-id-is-escaped-twice-in-the-canonical-path", Sync(SigV4BedrockPath)),
            ("eventstream.every-split-point-and-typed-headers", EventStreamSplits),
            ("eventstream.prelude-and-message-crc-failures-and-truncation", EventStreamFailures),
            ("bedrock.claude-budget-thinking-tools-images-and-cache-points-full-request", BedrockClaudeRequest),
            ("bedrock.adaptive-opus-gpt-oss-gpt-and-govcloud-request-fields", BedrockReasoningFields),
            ("bedrock.stream-text-thinking-tool-usage-cost-and-stop-reasons", BedrockStreamEvents),
            ("bedrock.redacted-reasoning-and-blocks-without-stop", BedrockRedactedReasoning),
            ("bedrock.tool-arguments-finalize-through-parse-streaming-json", BedrockToolArguments),
            ("bedrock.nameless-and-id-less-tool-calls-kept-and-replayed", BedrockNamelessToolCalls),
            ("bedrock.http-errors-retries-stream-exceptions-and-diagnostics", BedrockErrors),
            ("bedrock.bearer-token-region-arn-and-endpoint-selection", BedrockEndpoints),
            ("aws.credential-chain-env-profiles-assume-role-sso-container-imds", AwsCredentialChainCases),
            ("codex.full-request-body-and-headers", CodexRequest),
            ("codex.sse-events-end-turn-tier-pricing-and-errors", CodexStream),
            ("oauth.codex-browser-paste-device-code-and-refresh", CodexOAuthFlows),
            ("oauth.chatgpt-dynamic-client-login-and-refresh", ChatGPTOAuthFlow),
            ("oauth.copilot-enterprise-device-login-models-policy-and-refresh", CopilotOAuthFlow),
            ("oauth.openrouter-kimi-meta-xai-radius-flows", SubscriptionOAuthFlows),
            ("oauth.loopback-server-routes-pages-and-device-poller", LoopbackAndPoller),
            ("copilot.dynamic-headers-and-bearer-auth-on-every-api", CopilotRequests),
            ("cloudflare.workers-ai-and-gateway-urls-and-auth", CloudflareRequests),
            ("opencode.session-header-policy", Sync(OpenCodeHeaders)),
            ("opencode.request-session-id-sets-the-session-headers", OpenCodeSessionFromRequest),
            ("budget.4-5-mb-images-reach-copilot-bedrock-and-codex", LargeImageRequests),
            ("authjson.json-provider-fields-and-api-key-entries", AuthJsonFields),
            ("authjson.stored-command-keys-resolve-for-every-provider", StoredCommandKeys),
            ("live.provider-route-selection-and-per-request-auth", LiveRoutes),
            ("live.long-transcript-2000-messages-with-loadout-records-every-family", LongTranscriptEveryFamily),
            ("login.every-provider-cli-flow-api-key-and-oauth", CliLogin),
        };
        cases = [.. cases, .. GoogleAdcCases, .. CodexWebSocketCases, .. ZstdCases, .. JsonLeftoverCases];
        if (Environment.GetEnvironmentVariable("PROVIDERAPIS_FILTER") is { Length: > 0 } filter) cases = [.. cases.Where(test => test.Id.Contains(filter, StringComparison.Ordinal))];
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(120)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { suite = "provider-apis-1.1.0", sourceSha = SourceSha, status = "AUTHORED NATIVE; NOT UPSTREAM CAPTURES", failures,
            bodyComparisons, zstdReference = ZstdReferenceStatus, genuineSourceCasesCaptured = 0, results };
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        else Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };

    private sealed class CheckException(string message) : Exception(message);
    private static void Check(bool condition, string message) { if (!condition) throw new CheckException(message); }
    private static void Equal<T>(T expected, T actual, string what = "value")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new CheckException($"{what}: expected <{expected}> but was <{actual}>");
    }
    /// <summary>Compares two JSON documents structurally (property order included).</summary>
    private static void JsonEqual(string expected, string actual, string what)
    {
        bodyComparisons++;
        var left = JsonNode.Parse(expected)!.ToJsonString(); var right = JsonNode.Parse(actual)!.ToJsonString();
        if (left != right) throw new CheckException($"{what}:\nexpected {left}\nactual   {right}");
    }
    /// <summary>Compares two JSON documents structurally, ignoring object property order.</summary>
    private static void JsonSame(string expected, string actual, string what)
    {
        bodyComparisons++;
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)))
            throw new CheckException($"{what}:\nexpected {JsonNode.Parse(expected)!.ToJsonString()}\nactual   {JsonNode.Parse(actual)!.ToJsonString()}");
    }
    private static async Task<T> Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T expected) { return expected; }
        catch (Exception other) { throw new CheckException($"expected {typeof(T).Name} but got {other.GetType().Name}: {other.Message}"); }
        throw new CheckException($"expected {typeof(T).Name}");
    }

    private static string Temp(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-provider-apis", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    internal sealed class FixedTime(long unixMilliseconds) : TimeProvider
    {
        public long Now = unixMilliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref Now));
    }

    /// <summary>A recorded request: method, URL, headers (lower-cased names) and body text.</summary>
    internal sealed record Recorded(string Method, string Url, ImmutableDictionary<string, string> Headers, string Body, byte[] BodyBytes)
    {
        public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
    }

    /// <summary>A scripted fake HTTP peer: each request is recorded and answered by the first matching responder.</summary>
    internal sealed class FakeHttp : HttpMessageHandler
    {
        private readonly List<(Func<Recorded, bool> Match, Func<Recorded, HttpResponseMessage> Respond, bool Once)> responders = [];
        public readonly ConcurrentQueue<Recorded> Requests = new();
        public FakeHttp On(Func<Recorded, bool> match, Func<Recorded, HttpResponseMessage> respond, bool once = false)
        { lock (responders) responders.Add((match, respond, once)); return this; }
        public FakeHttp OnUrl(string prefix, Func<Recorded, HttpResponseMessage> respond, bool once = false) =>
            On(request => request.Url.StartsWith(prefix, StringComparison.Ordinal), respond, once);
        public List<Recorded> All => [.. Requests];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .GroupBy(pair => pair.Key.ToLowerInvariant()).ToImmutableDictionary(group => group.Key, group => string.Join(", ", group.SelectMany(pair => pair.Value)));
            // A zstd body (the Codex SSE request) is recorded decoded; BodyBytes keeps the wire bytes.
            var text = headers.GetValueOrDefault("content-encoding") == "zstd" ? Encoding.UTF8.GetString(PiSharp.AI.Compression.ZstdDecoder.Decompress(bytes)) : Encoding.UTF8.GetString(bytes);
            var recorded = new Recorded(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, text, bytes);
            Requests.Enqueue(recorded);
            Func<Recorded, HttpResponseMessage>? respond = null;
            lock (responders)
                for (var index = 0; index < responders.Count; index++)
                    if (responders[index].Match(recorded))
                    {
                        respond = responders[index].Respond;
                        if (responders[index].Once) responders.RemoveAt(index);
                        break;
                    }
            if (respond is null) return new(HttpStatusCode.NotFound) { Content = new StringContent("no fake responder for " + recorded.Url) };
            var response = respond(recorded); response.RequestMessage = request; return response;
        }
    }

    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Text(string body, HttpStatusCode status, string contentType = "text/plain") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
    internal static HttpResponseMessage Sse(params string[] events) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(data => "data: " + data + "\n\n")), Encoding.UTF8, "text/event-stream") };

    internal static TranscriptEntry Entry(string json)
    {
        var data = JsonData.Parse(json);
        return new(data.Value.GetProperty("role").GetString()!, data);
    }

    internal static async Task<List<StreamEvent>> Collect(IChatTransport transport, ChatRequest request, CancellationToken token = default)
    {
        var events = new List<StreamEvent>();
        await foreach (var frame in transport.StreamAsync(request, token)) events.Add(frame);
        return events;
    }

    internal static string Wire(AssistantMessage message) => PiWireJson.WriteMessage(message).ToString();
    internal static string ErrorMessage(StreamEvent terminal) =>
        ((StreamTerminalEvent)terminal).Message.ExtraProperties!.TryGet("errorMessage", out var value) ? value!.Value.GetString()! : "";
}
