// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/error-body.ts.
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Expected texts were captured by running @earendil-works/pi-ai@1.1.0 (openai 7.19.0, @anthropic-ai/sdk 0.129.0,
// @google/genai 2.21.0) stream() against a local HTTP server returning the same status, content type and body:
// openai-completions shows formatProviderError(normalizeProviderError(error)) (+ "\n" + error.error.metadata.raw when
// not already shown), openai-responses the same with the "OpenAI API error" prefix, anthropic-messages error.message,
// google-generative-ai the @google/genai ApiError message (JSON.stringify of the error body).
internal static partial class Program
{
    private sealed record ErrorTextCase(string Name, int Status, string? ContentType, string Body, string? Completions, string? Responses,
        string? Anthropic, string? Google, string? Reason = null);

    private static readonly string Pad = new('x', 4100);

    private static readonly ErrorTextCase[] ErrorTextCases =
    [
        new("gateway-html", 502, "text/html", "<html><body>Bad gateway</body></html>",
            "502 <html><body>Bad gateway</body></html>", "OpenAI API error (502): 502 <html><body>Bad gateway</body></html>",
            "502 <html><body>Bad gateway</body></html>", """{"error":{"message":"<html><body>Bad gateway</body></html>","code":502,"status":"Bad Gateway"}}"""),
        new("empty", 502, null, "", "502 status code (no body)", "OpenAI API error (502): 502 status code (no body)",
            "502 status code (no body)", """{"error":{"message":"","code":502,"status":"Bad Gateway"}}"""),
        new("empty-custom-reason", 502, null, "", "502 status code (no body)", null, null,
            """{"error":{"message":"","code":502,"status":"Gateway Down"}}""", "Gateway Down"),
        new("openai-error", 400, "application/json", """{"error":{"message":"bad","type":"x"}}""",
            """400: {"message":"bad","type":"x"}""", """OpenAI API error (400): {"message":"bad","type":"x"}""",
            """400 {"error":{"message":"bad","type":"x"}}""", """{"error":{"message":"bad","type":"x"}}"""),
        new("no-error-member", 400, "application/json", """{"message":"denied","code":"fixture"}""",
            """400: {"message":"denied","code":"fixture"}""", """OpenAI API error (400): {"message":"denied","code":"fixture"}""",
            "400 denied", """{"message":"denied","code":"fixture"}"""),
        new("error-without-message", 400, "application/json", """{"error":{"code":"fixture"}}""",
            """400 {"code":"fixture"}""", """OpenAI API error (400): 400 {"code":"fixture"}""",
            """400 {"error":{"code":"fixture"}}""", """{"error":{"code":"fixture"}}"""),
        new("anthropic-error", 400, "application/json", """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long"}}""",
            """400: {"type":"invalid_request_error","message":"prompt is too long"}""",
            """OpenAI API error (400): {"type":"invalid_request_error","message":"prompt is too long"}""",
            """400 {"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long"}}""",
            """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long"}}"""),
        new("plain-text-untrimmed", 503, "text/plain", " denied ", "503  denied ", "OpenAI API error (503): 503  denied ", "503  denied ",
            """{"error":{"message":" denied ","code":503,"status":"Service Unavailable"}}"""),
        new("json-null", 400, "application/json", "null", "400 null", "OpenAI API error (400): 400 null", "400 null", "null"),
        new("json-array", 400, "application/json", "[1,2]", "400 [1,2]", null, "400 [1,2]", null),
        new("error-null", 400, "application/json", """{"error":null}""", """400 {"error":null}""", null, """400 {"error":null}""", null),
        new("error-string", 400, "application/json", """{"error":"nope"}""", "400 \"nope\"", null, """400 {"error":"nope"}""", null),
        new("duplicate-names-and-numbers", 400, "application/json", """{"error":{"message":"a","x":1.50,"message":"b"}}""",
            """400: {"message":"b","x":1.5}""", """OpenAI API error (400): {"message":"b","x":1.5}""",
            """400 {"error":{"message":"b","x":1.5}}""", """{"error":{"message":"b","x":1.5}}"""),
        new("metadata-raw-shown", 403, "application/json", """{"error":{"message":"Provider returned error","code":403,"metadata":{"raw":"upstream WAF"}}}""",
            """403: {"message":"Provider returned error","code":403,"metadata":{"raw":"upstream WAF"}}""", null, null, null),
        new("metadata-raw-appended", 403, "application/json", """{"error":{"message":"Provider returned error","metadata":{"raw":"line \"q\""}}}""",
            "403: {\"message\":\"Provider returned error\",\"metadata\":{\"raw\":\"line \\\"q\\\"\"}}\nline \"q\"", null, null, null),
        new("metadata-raw-array", 403, "application/json", """{"error":{"message":"x","metadata":{"raw":[1,null,{"a":1}]}}}""",
            "403: {\"message\":\"x\",\"metadata\":{\"raw\":[1,null,{\"a\":1}]}}\n1,,[object Object]", null, null, null),
        new("long-body-truncated", 400, "application/json", "{\"error\":{\"message\":\"m\",\"pad\":\"" + Pad + "\"}}",
            "400: " + ("{\"message\":\"m\",\"pad\":\"" + Pad + "\"}")[..4000] + "... [truncated 124 chars]",
            "OpenAI API error (400): " + ("{\"message\":\"m\",\"pad\":\"" + Pad + "\"}")[..4000] + "... [truncated 124 chars]", null, null),
        new("leading-bom-dropped", 502, "text/html", "\uFEFF<p>x</p>\uFEFF", "502 <p>x</p>\uFEFF", null, "502 <p>x</p>\uFEFF",
            "{\"error\":{\"message\":\"<p>x</p>\uFEFF\",\"code\":502,\"status\":\"Bad Gateway\"}}"),
    ];

    private static HttpResponseMessage ErrorResponse(ErrorTextCase test)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(test.Body));
        if (test.ContentType is { } type) content.Headers.TryAddWithoutValidation("Content-Type", type);
        var response = new HttpResponseMessage((HttpStatusCode)test.Status) { Content = content };
        if (test.Reason is { } reason) response.ReasonPhrase = reason;
        return response;
    }

    private static string ErrorText(ChatResult result)
    {
        Equal(StopReason.Error, result.Message.StopReason);
        return result.Message.ExtraProperties!.Values["errorMessage"].Value.GetString()!;
    }

    private static async Task<string> CompletionsError(Func<HttpResponseMessage> respond)
    {
        var model = new ModelDescriptor("error-model", "openai-completions", "openrouter");
        using var handler = new Handler(_ => Task.FromResult(respond()));
        using var provider = NativeProviderFactory.CreateCompletions(model, new("https://openrouter.ai/api/v1/chat/completions"), Key, new(Reasoning: false),
            new(MaxTokensField: "max_tokens", SupportsStore: false), handler);
        return ErrorText(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline));
    }

    private static async Task<string> ResponsesError(Func<HttpResponseMessage> respond)
    {
        var model = new ModelDescriptor("gpt-error", "openai-responses", "openai");
        using var handler = new Handler(_ => Task.FromResult(respond()));
        using var provider = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), Key, new(false), null, handler);
        return ErrorText(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline));
    }

    private static async Task<string> AnthropicError(Func<HttpResponseMessage> respond, bool hooks)
    {
        using var handler = new Handler(_ => Task.FromResult(respond()));
        using var provider = NativeProviderFactory.CreateAnthropic(Haiku, new("https://api.anthropic.com/"), Key,
            new(MaximumTokens: 4096, ModelReasoning: false), null, handler, null,
            hooks ? new AnthropicMessagesHooks(OnResponse: (_, _, _) => Task.CompletedTask) : null);
        return ErrorText(await new ChatClient(provider).CompleteAsync(new(Haiku, [Ask], 1)).WaitAsync(Deadline));
    }

    private static async Task<string> GoogleError(Func<HttpResponseMessage> respond)
    {
        using var handler = new Handler(_ => Task.FromResult(respond()));
        using var client = new HttpClient(handler);
        var transport = new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key));
        return ErrorText(await new ChatClient(transport).CompleteAsync(new(Gemini, [Ask], 1)).WaitAsync(Deadline));
    }

    private static async Task ProviderStatusErrorTexts()
    {
        foreach (var test in ErrorTextCases)
        {
            void Same(string? expected, string actual, string api)
            { if (expected is not null && expected != actual) throw new InvalidOperationException($"{test.Name} {api}{Environment.NewLine}Expected {expected}{Environment.NewLine}Actual   {actual}"); }
            if (test.Completions is not null) Same(test.Completions, await CompletionsError(() => ErrorResponse(test)), "openai-completions");
            if (test.Responses is not null) Same(test.Responses, await ResponsesError(() => ErrorResponse(test)), "openai-responses");
            if (test.Anthropic is not null)
            {
                Same(test.Anthropic, await AnthropicError(() => ErrorResponse(test), hooks: false), "anthropic-messages");
                Same(test.Anthropic, await AnthropicError(() => ErrorResponse(test), hooks: true), "anthropic-messages+hooks");
            }
            if (test.Google is not null) Same(test.Google, await GoogleError(() => ErrorResponse(test)), "google-generative-ai");
        }
    }

    private static async Task PiMessagesDiagnosticBodyTruncation()
    {
        // pi-messages.ts truncateDiagnosticString: a non-JSON body over 8192 characters keeps 8192 and appends U+2026.
        var model = new ModelDescriptor("inert", "pi-messages", "authored");
        var metadata = JsonData.Parse("""{"id":"inert","api":"pi-messages","provider":"authored","baseUrl":"https://pi-messages.invalid/v1"}""");
        var body = new string('y', 8200);
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body) }));
        using var client = new HttpClient(handler);
        var transport = new PiMessagesHttpSseTransport(client, model, new PiMessagesOptions(metadata, Key) { EnvironmentLookup = _ => null });
        var result = await new ChatClient(transport).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline);
        Equal("502 Bad Gateway: " + body, ErrorText(result));
        var details = result.Message.ExtraProperties!.Values["diagnostics"].Value[0].GetProperty("details");
        Equal(new string('y', 8192) + (char)0x2026, details.GetProperty("body").GetString());
        Equal("Bad Gateway", details.GetProperty("statusText").GetString());
    }

    private static async Task ProviderStreamErrorTexts()
    {
        HttpResponseMessage Stream(string raw) => new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") };
        // openai SDK Stream: data.error, or for a named error event data?.error ?? data, becomes a status-less APIError.
        foreach (var (raw, expected) in new[]
        {
            ("data: {\"error\":{\"message\":\"overloaded\",\"code\":529,\"metadata\":{\"raw\":\"upstream busy\"}}}\n\n", "overloaded\nupstream busy"),
            ("data: {\"error\":{\"code\":529}}\n\n", "{\"code\":529}"),
            ("event: error\ndata: {\"message\":\"boom\"}\n\n", "boom"),
            ("event: error\ndata: {}\n\n", "{}"),
            ("event: error\ndata: {\"error\":null,\"message\":\"m\"}\n\n", "m"),
        })
            Equal(expected, await CompletionsError(() => Stream(raw)));
        // anthropic-messages iterateAnthropicEvents: a named error event throws new Error(sse.data).
        const string started = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture-haiku\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n";
        const string overloaded = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";
        foreach (var hooks in new[] { false, true })
            foreach (var (raw, expected) in new[]
            {
                ("event: error\ndata: " + overloaded + "\n\n", overloaded),
                ("event: error\ndata: not json\n\n", "not json"),
                (started + "event: error\ndata: " + overloaded + "\n\n", overloaded),
            })
                Equal(expected, await AnthropicError(() => Stream(raw), hooks));
    }
}
