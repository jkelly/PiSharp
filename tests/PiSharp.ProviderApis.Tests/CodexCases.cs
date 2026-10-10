using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.OpenAICodexResponses;
using PiSharp.Contracts;

internal static partial class Program
{
    internal static string Jwt(string payload) => "eyJhbGciOiJub25lIn0." +
        System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + ".signature";
    internal static readonly string CodexToken = Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-123"},"exp":1}""");

    internal static FrozenCatalogModel CatalogRow(string provider, string id)
    {
        var catalog = PiSharp.Cli.Models.BuiltinModelCatalog.Get(provider);
        Check(catalog.TryGetModel(CatalogModelType.Chat, id, out var model), $"{provider}/{id} is in the pinned catalog");
        return model!;
    }

    private static ModelDescriptor Descriptor(FrozenCatalogModel row) => new(row.Id, row.DeclaredApi, row.Provider);

    private static (OpenAICodexResponsesTransport Transport, FakeHttp Http, ModelDescriptor Model, List<TimeSpan> Delays) Codex(OpenAICodexResponsesOptions? options = null,
        string id = "gpt-5.5", string token = "")
    {
        var row = CatalogRow("openai-codex", id); var http = new FakeHttp(); var delays = new List<TimeSpan>();
        // These cases pin the SSE wire ("sse"); the default "auto" tries the WebSocket first (CodexWebSocketCases).
        options = (options ?? new()) with { Delay = (wait, _) => { delays.Add(wait); return Task.CompletedTask; }, Time = new FixedTime(1_800_000_000_000),
            Transport = options?.Transport ?? (() => "sse") };
        var transport = new OpenAICodexResponsesTransport(new HttpClient(http, disposeHandler: false), Descriptor(row), row.Raw, options,
            _ => ValueTask.FromResult(token.Length == 0 ? CodexToken : token));
        return (transport, http, Descriptor(row), delays);
    }

    private static ImmutableArray<TranscriptEntry> CodexTranscript() =>
    [
        Entry("""{"role":"system","content":"Be brief.","toolsAdded":[{"name":"read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}],"timestamp":1}"""),
        Entry("""{"role":"user","content":"Hi","timestamp":2}""")
    ];

    private static async Task CodexRequest()
    {
        var (transport, http, model, _) = Codex(new() { SessionId = "sess-1", ModelHeaders = ImmutableDictionary<string, string>.Empty.Add("X-Model", "m"),
            Headers = ImmutableDictionary<string, string?>.Empty.Add("x-extra", "e").Add("originator", null) });
        http.OnUrl("https://chatgpt.com/", _ => Sse("""{"type":"response.completed","response":{"id":"r","status":"completed","output":[]}}"""));
        var events = await Collect(transport, new(model, CodexTranscript(), 3) { ThinkingLevel = "high" });
        Check(events[^1] is StreamDone, "done");
        var request = http.All.Single();
        Equal("https://chatgpt.com/backend-api/codex/responses", request.Url, "url");
        JsonEqual("""
            {"model":"gpt-5.5","store":false,"stream":true,"instructions":"Be brief.",
             "input":[{"role":"user","content":[{"type":"input_text","text":"Hi"}]}],
             "text":{"verbosity":"low"},"include":["reasoning.encrypted_content"],"prompt_cache_key":"sess-1","tool_choice":"auto","parallel_tool_calls":true,
             "tools":[{"type":"function","name":"read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]},"strict":null}],
             "reasoning":{"effort":"high","summary":"auto"}}
            """, request.Body, "codex body");
        Check(request.Header("authorization") == "Bearer " + CodexToken && request.Header("chatgpt-account-id") == "acct-123" &&
            request.Header("openai-beta") == "responses=experimental" && request.Header("accept") == "text/event-stream" &&
            request.Header("content-type")!.StartsWith("application/json", StringComparison.Ordinal) && request.Header("session-id") == "sess-1" &&
            request.Header("x-client-request-id") == "sess-1" && request.Header("user-agent") == "pi/1.1.0" && request.Header("x-model") == "m" &&
            request.Header("x-extra") == "e" && request.Header("originator") is null && request.Header("content-encoding") is null, "codex headers");
        // Off: reasoning effort none; no session: no cache key; a "required" tool choice and service tier travel as given.
        var off = Codex(new() { ToolChoice = "required", ServiceTier = "flex", TextVerbosity = "high", Temperature = 0.2 });
        Check(off.Transport.BuildBody(new(off.Model, CodexTranscript(), 3) { ThinkingLevel = "off" }).ToJsonString() ==
            JsonDocument.Parse("""{"model":"gpt-5.5","store":false,"stream":true,"instructions":"Be brief.","input":[{"role":"user","content":[{"type":"input_text","text":"Hi"}]}],"text":{"verbosity":"high"},"include":["reasoning.encrypted_content"],"tool_choice":"required","parallel_tool_calls":true,"temperature":0.2,"service_tier":"flex","tools":[{"type":"function","name":"read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]},"strict":null}],"reasoning":{"effort":"none"}}""").RootElement.GetRawText(),
            "off body");
        // minimal clamps through the thinkingLevelMap (minimal -> low); a missing system prompt uses the default instructions.
        var spark = Codex(id: "gpt-5.3-codex-spark");
        var minimal = spark.Transport.BuildBody(new(spark.Model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 3) { ThinkingLevel = "minimal" });
        Equal("""{"effort":"low","summary":"auto"}""", minimal["reasoning"]!.ToJsonString(), "minimal mapped");
        Equal("You are a helpful assistant.", minimal["instructions"]!.GetValue<string>(), "default instructions");
        Equal("https://example.com/codex/responses", OpenAICodexResponsesTransport.ResolveUrl("https://example.com/codex/"), "codex url suffix");
        Equal("https://example.com/x/codex/responses", OpenAICodexResponsesTransport.ResolveUrl("https://example.com/x"), "codex url");
        Equal("Failed to extract accountId from token", (await Throws<InvalidOperationException>(() => Task.FromResult(OpenAICodexResponsesTransport.ExtractAccountId("not-a-jwt")))).Message, "jwt");
    }

    private static async Task CodexStream()
    {
        var (transport, http, model, _) = Codex(new() { ServiceTier = "priority" });
        http.OnUrl("https://chatgpt.com/", _ => Sse(
            """{"type":"response.created","response":{"id":"r1","status":"in_progress"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg-1","content":[]}}""",
            """{"type":"response.output_text.delta","output_index":0,"item_id":"msg-1","delta":"Hello"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg-1","content":[{"type":"output_text","text":"Hello"}]}}""",
            """{"type":"response.done","response":{"id":"r1","status":"completed","end_turn":true,"service_tier":"default","output":[],"usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":100},"output_tokens":50,"total_tokens":1050}}}""",
            """{"type":"response.output_text.delta","output_index":0,"item_id":"msg-1","delta":"ignored after the terminal"}"""));
        var events = await Collect(transport, new(model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 9));
        var done = (StreamDone)events[^1];
        Equal(StopReason.Stop, done.Reason, "stop");
        Equal("true", done.Message.ExtraProperties!.Values["endTurn"].ToString(), "end turn");
        Check(done.Message.Content.Single() is TextContent { Text: "Hello" }, "text");
        Equal(900L, done.Message.Usage.Input, "input excludes cached");
        // models.ts calculateCost then applyServiceTierPricing, in binary64 Numbers.
        Equal(0.015125000000000001m, done.Message.Usage.Cost.Total, "priority pricing for gpt-5.5 (x2.5)");
        // Incomplete for max_output_tokens settles as length.
        var incomplete = Codex();
        incomplete.Http.OnUrl("https://", _ => Sse("""{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}}"""));
        Equal(StopReason.Length, ((StreamDone)(await Collect(incomplete.Transport, new(incomplete.Model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 9)))[^1]).Reason, "incomplete");
        // Errors: a usage limit (friendly text), an error event, response.failed, and a failed/cancelled status.
        async Task<string> Failure(HttpResponseMessage response, OpenAICodexResponsesOptions? options = null)
        {
            var fixture = Codex(options);
            fixture.Http.OnUrl("https://", _ => response);
            var result = await Collect(fixture.Transport, new(fixture.Model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 9));
            Check(result[^1] is StreamError, "error terminal");
            return ErrorMessage(result[^1]);
        }
        Equal("You have hit your ChatGPT usage limit (plus plan). Try again in ~10 min.", await Failure(Json(
            """{"error":{"type":"usage_limit_reached","message":"limit","plan_type":"PLUS","resets_at":1800000600}}""", (HttpStatusCode)429)), "usage limit");
        Equal("bad request body", await Failure(Json("""{"error":{"code":"invalid","message":"bad request body"}}""", HttpStatusCode.BadRequest)), "http error message");
        Equal("Codex error: boom", await Failure(Sse("""{"type":"error","code":"server_error","message":"boom"}""")), "error event");
        Equal("Codex error: rate_limited", await Failure(Sse("""{"type":"error","error":{"code":"rate_limited"}}""")), "nested error code");
        Equal("failed hard", await Failure(Sse("""{"type":"response.failed","response":{"status":"failed","error":{"code":"x","message":"failed hard"}}}""")), "response.failed");
        Equal("An unknown error occurred", await Failure(Sse("""{"type":"response.completed","response":{"status":"cancelled","output":[]}}""")), "cancelled status");
        // Retries (maxRetries): a 503 is retried after 1 s; retry-after-ms is honoured and bounded by maxRetryDelayMs.
        var retried = Codex(new() { MaxRetries = 2 });
        var calls = 0;
        retried.Http.OnUrl("https://", _ => ++calls == 1 ? Text("upstream connect error", HttpStatusCode.ServiceUnavailable)
            : calls == 2 ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("rate limited"), Headers = { { "retry-after-ms", "250" } } }
            : Sse("""{"type":"response.completed","response":{"status":"completed","output":[]}}"""));
        Check((await Collect(retried.Transport, new(retried.Model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 9)))[^1] is StreamDone, "retried to success");
        Equal("00:00:01,00:00:00.2500000", string.Join(",", retried.Delays), "retry delays");
        var tooLong = Codex(new() { MaxRetries = 1, MaxRetryDelayMilliseconds = 1000 });
        tooLong.Http.OnUrl("https://", _ => new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("rate limited"), Headers = { { "retry-after", "120" } } });
        Equal("Server requested 120s retry delay (max: 1s)", ErrorMessage((await Collect(tooLong.Transport, new(tooLong.Model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 9)))[^1]), "retry delay cap");
    }
}
