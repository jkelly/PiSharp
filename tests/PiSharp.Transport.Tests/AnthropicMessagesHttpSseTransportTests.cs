using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Transports;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class AnthropicMessagesHttpSseTransportTests
{
    private static readonly ModelDescriptor Model = new("authored-anthropic", "anthropic-messages", "anthropic");
    private const string Key = "authored-inert-key-noncredential";
    private const string Start = """{"type":"message_start","message":{"id":"msg-authored","role":"assistant","model":"authored-anthropic","content":[],"usage":{"input_tokens":11,"output_tokens":0,"cache_read_input_tokens":2,"cache_creation_input_tokens":3}}}""";
    private const string Stop = """{"type":"message_stop"}""";
    private const string TextStart = """{"type":"content_block_start","index":4,"content_block":{"type":"text","text":""}}""";
    private const string TextEnd = """{"type":"content_block_stop","index":4}""";
    private const string ToolStart = """{"type":"content_block_start","index":9,"content_block":{"type":"tool_use","id":"call-authored","name":"inspect","input":{}}}""";
    private const string ToolEnd = """{"type":"content_block_stop","index":9}""";
    private const string Arguments = """{"value":1.0,"opaque":"001","nil":null}""";
    private const string Declaration = """{"role":"system","content":"Inspect once.","toolsAdded":[{"name":"inspect","description":"Authored inert declaration.","parameters":{"type":"object","properties":{"value":{"type":"number","minimum":0.5}},"required":[]}}],"timestamp":123}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("anthropic-http.source-informed-complete-key-requests-and-header-replay", CompleteRequests);
        yield return ("anthropic-http.actual-turn-tools-next-request-wait-for-owned-cleanup-and-commit", ToolRoundTrip);
        yield return ("anthropic-http.named-events-eof-done-and-strict-admission", FramingAndAdmission);
        yield return ("anthropic-http.request-and-stream-budgets-before-effects", BudgetsAndConfiguration);
        yield return ("anthropic-http.cooperative-cancellation-and-early-disposal-await-cleanup", CancellationAndEarlyDispose);
        yield return ("anthropic-http.factory-send-read-status-cleanup-faults-and-fresh-ownership", FaultsAndFreshRequests);
        yield return ("anthropic-http.three-genuine-sdk-bodies-numeric-observations-and-explicit-metadata-headers", GenuineSdkRequests);
        yield return ("anthropic-http.content-type-routing-all-header-sources-and-casing-with-null-json-override", ContentTypeRouting);
        yield return ("anthropic-http.fallback-request-marker-pricing-and-error-settlement", FallbackRequestAndSettlement);
    }

    private static async Task FallbackRequestAndSettlement()
    {
        const string fallbackModel = "authored-fallback";
        const string start = """{"type":"message_start","message":{"id":"fallback-response","role":"assistant","model":"authored-fallback","content":[],"usage":{"input_tokens":100,"output_tokens":0}}}""";
        const string marker = """{"type":"content_block_start","index":0,"content_block":{"type":"fallback","from":{"model":"authored-anthropic"},"to":{"model":"spoofed"}}}""";
        var options = new AnthropicMessagesOptions(Rates: new(30, 50),
            AllowedFallbackModels: [new("anthropic", fallbackModel, new(3, 5))]);
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new Uri("https://authored.invalid"), Model,
            new(64, AllowedFallbackModels: [fallbackModel]));
        foreach (var complete in new[] { true, false })
        {
            var wire = complete ? Sse(start, marker, TextStart, TextEnd,
                Finish("end_turn").Replace("\"output_tokens\":2", "\"output_tokens\":20"), Stop) : Sse(start, marker);
            var response = new StreamProbeContent(new ProbeStream(Bytes(wire))); var sends = 0;
            Exception? observationFailure = null; HttpRequestMessage? owned = null;
            using var handler = new FakeHttpHandler(async (request, token) =>
            {
                sends++;
                try
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    Equal(fallbackModel, body.RootElement.GetProperty("fallbacks")[0].GetProperty("model").GetString());
                    Equal(Model.Id, body.RootElement.GetProperty("model").GetString());
                    Check(!body.RootElement.TryGetProperty("betas", out _), "Fallback beta leaked into request JSON.");
                    Check(Header(request, "anthropic-beta").Split(',').Contains("server-side-fallback-2026-07-01"), "Fallback beta was omitted.");
                }
                catch (Exception error) { observationFailure = error; }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = response };
            });
            using var client = new HttpClient(handler);
            var transport = new AnthropicMessagesHttpSseTransport(client, (request, token) => owned = factory.Create(request, Key, token), messagesOptions: options);
            var result = await new ChatClient(transport, capacity: 1).CompleteAsync(Request());
            if (observationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationFailure).Throw();
            Equal(1, sends); Equal(Model.Id, result.Message.Model); Equal(fallbackModel, Metadata(result.Message, "responseModel"));
            Equal(0.0003m, result.Message.Usage.Cost.Input);
            if (complete)
            {
                Check(result.Failure is null, "Complete HTTP fallback did not settle successfully.");
                Equal(0.0001m, result.Message.Usage.Cost.Output); Equal(0.0004m, result.Message.Usage.Cost.Total);
                Equal(1, result.Message.Content.Length); Check(result.Message.Content[0] is TextContent, "Marker became replayable content.");
            }
            else { Failed(result, "UnexpectedEof"); Equal(0, result.Message.Content.Length); Equal(0.0003m, result.Message.Usage.Cost.Total); }
            Check(response.Disposed, "Fallback terminal skipped response cleanup.");
            await ThrowsAsync<ObjectDisposedException>(() => owned!.Content!.ReadAsStringAsync());
        }
        using var partial = new Fixture(new ProbeStream(Bytes(Sse(Start, TextStart, TextEnd, marker))), messagesOptions: options);
        var failed = await new ChatClient(partial.Transport).CompleteAsync(Request()); Failed(failed, "MalformedStream");
        Equal(1, failed.Message.Content.Length); Check(partial.Response.Disposed, "Mid-output fallback skipped cleanup.");
    }

    private static async Task CompleteRequests()
    {
        HeaderFieldSerializationControls();
        // Authored expectations derived from unchanged buildParams/createClient + SDK beta.messages.create; no captured-golden claim.
        var original = Request(); var untouched = original.Messages.Select(message => message.WireBody.ToString()).ToArray();
        var options = new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: -1, SessionId: "owned-session", SendSessionAffinityHeaders: true,
            ModelHeaders: JsonData.Parse("""{"x-model":"model-owned","x-remove":"remove-with-null"}"""),
            Headers: JsonData.Parse("""{"x-model":"option-owned","x-remove":null,"anthropic-beta":" alpha , beta, alpha "}"""));
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new Uri("https://authored.invalid/tenant/"), Model,
            new(64, CacheRetention: AnthropicCacheRetention.Long), options);
        using var request = factory.Create(original, Key);
        Equal(HttpMethod.Post, request.Method); Equal("https://authored.invalid/tenant/v1/messages?beta=true", request.RequestUri!.AbsoluteUri);
        Equal(Key, Header(request, "x-api-key")); Equal("2023-06-01", Header(request, "anthropic-version"));
        Equal("true", Header(request, "anthropic-dangerous-direct-browser-access")); Equal("application/json", Header(request, "accept"));
        Equal("option-owned", Header(request, "x-model")); Equal("owned-session", Header(request, "x-session-affinity"));
        Equal("alpha,beta", Header(request, "anthropic-beta")); Check(!request.Headers.Contains("x-remove"), "Null header removal was lost.");
        Equal("application/json", request.Content!.Headers.ContentType!.ToString());
        using var actual = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        using var expected = JsonDocument.Parse("""{"model":"authored-anthropic","messages":[{"role":"user","content":[{"type":"text","text":"Reply with \u03c0\nline 2","cache_control":{"type":"ephemeral","ttl":"1h"}}]}],"max_tokens":-1,"stream":true,"system":[{"type":"text","text":"Keep 001.","cache_control":{"type":"ephemeral","ttl":"1h"}}]}""");
        Check(Same(expected.RootElement, actual.RootElement), "Complete source-informed request differs.");
        Check(!actual.RootElement.TryGetProperty("betas", out _), "SDK beta header field remained in JSON body.");
        for (var index = 0; index < untouched.Length; index++) Equal(untouched[index], original.Messages[index].WireBody.ToString());
        foreach (var (value, lexeme) in new (double, string)[] { (0, "0"), (-0d, "0"), (-1, "-1"), (0.5, "0.5"), (1e-6, "0.000001"), (1e-7, "1e-7") })
        {
            using var direct = new AnthropicMessagesKeyAuthRequestFactory(new Uri("https://authored.invalid"), Model,
                new(64, CacheRetention: AnthropicCacheRetention.None), new(MaxTokens: value)).Create(original, Key);
            using var body = JsonDocument.Parse(await direct.Content!.ReadAsStringAsync());
            Equal(lexeme, body.RootElement.GetProperty("max_tokens").GetRawText());
        }
        using var suppressed = new AnthropicMessagesKeyAuthRequestFactory(new Uri("https://authored.invalid"), Model,
            new(64, ModelReasoning: true, ThinkingEnabled: true), new(Headers: JsonData.Parse("{\"anthropic-beta\":null}"))).Create(original, Key);
        Check(!suppressed.Headers.Contains("anthropic-beta"), "Explicit null beta did not suppress automatic beta selection.");
    }

    private static async Task ContentTypeRouting()
    {
        var chat = Request(); var unchanged = chat.Messages.Select(message => message.WireBody.ToString()).ToArray();
        var endpoint = new Uri("https://authored.invalid/tenant/");
        using var baseline = new AnthropicMessagesKeyAuthRequestFactory(endpoint, Model, new(64)).Create(chat, Key);
        var expectedHeaders = Headers(baseline);
        var expectedBody = JsonData.Parse(await baseline.Content!.ReadAsStringAsync());
        Equal("application/json", expectedHeaders["content-type"]);
        foreach (var source in new[] { "runtime", "model", "options" })
        foreach (var name in new[] { "content-type", "Content-Type", "CONTENT-TYPE", "CoNtEnT-tYpE" })
        foreach (var value in new string?[] { "text/plain", null })
        {
            var input = JsonData.Parse(JsonSerializer.Serialize(new Dictionary<string, string?> { [name] = value }));
            var original = input.ToString();
            var options = source switch
            {
                "runtime" => new AnthropicMessagesKeyAuthRequestOptions(RuntimeHeaders: input, MaximumHeaders: expectedHeaders.Count),
                "model" => new AnthropicMessagesKeyAuthRequestOptions(ModelHeaders: input, MaximumHeaders: expectedHeaders.Count),
                _ => new AnthropicMessagesKeyAuthRequestOptions(Headers: input, MaximumHeaders: expectedHeaders.Count)
            };
            var factory = new AnthropicMessagesKeyAuthRequestFactory(endpoint, Model, new(64), options);
            // Dictionary value replacement retains the first name spelling; publication must route that spelling as a content header.
            using (var direct = factory.Create(chat, Key))
            {
                var observed = Headers(direct);
                Check(observed.Count == expectedHeaders.Count && expectedHeaders.All(pair =>
                    observed.TryGetValue(pair.Key, out var actual) && actual == pair.Value), "Complete direct header inventory changed by content-type casing.");
                Check(!direct.Headers.Any(header => header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)),
                    "Content-Type was published in general request headers.");
                Equal("application/json", direct.Content!.Headers.ContentType!.ToString());
                Check(Same(expectedBody.Value, JsonData.Parse(await direct.Content.ReadAsStringAsync()).Value),
                    "Content header input changed the complete JSON body.");
            }
            Exception? observationFailure = null; HttpRequestMessage? sent = null;
            var response = new StreamProbeContent(new ProbeStream(Bytes(TextWire())));
            using var handler = new FakeHttpHandler(async (request, token) =>
            {
                try
                {
                    sent = request; Equal(HttpMethod.Post, request.Method); Equal(baseline.RequestUri, request.RequestUri);
                    // Keep the complete pre-read observation; no case-dependent filtering or Content-Length stripping.
                    var observed = Headers(request);
                    Check(observed.Count == expectedHeaders.Count && expectedHeaders.All(pair =>
                        observed.TryGetValue(pair.Key, out var actual) && actual == pair.Value), "Complete fake-handler header inventory differs.");
                    Equal("application/json", request.Content!.Headers.ContentType!.ToString());
                    Check(Same(expectedBody.Value, JsonData.Parse(await request.Content.ReadAsStringAsync(token)).Value),
                        "Complete fake-handler body differs.");
                    return new(HttpStatusCode.OK) { Content = response };
                }
                catch (Exception error) { observationFailure = error; throw; }
            });
            using var client = new HttpClient(handler);
            var result = await new ChatClient(new AnthropicMessagesHttpSseTransport(client,
                (request, token) => factory.Create(request, Key, token))).CompleteAsync(chat);
            if (observationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationFailure).Throw();
            Check(result.Failure is null && result.Message.StopReason == StopReason.Stop, "Valid differently cased content header rejected the full request.");
            Equal(1, handler.SendCalls); Check(response.Disposed && !handler.Disposed, "Header regression changed response or borrowed-client ownership.");
            await ThrowsAsync<ObjectDisposedException>(() => sent!.Content!.ReadAsStringAsync());
            Equal(original, input.ToString());
        }
        var merged = new AnthropicMessagesKeyAuthRequestFactory(endpoint, Model, new(64), new(
            RuntimeHeaders: JsonData.Parse("""{"Content-Type":"text/plain","x-merge":"runtime","x-remove":"runtime"}"""),
            ModelHeaders: JsonData.Parse("""{"CONTENT-TYPE":"application/octet-stream","x-merge":"model"}"""),
            Headers: JsonData.Parse("""{"CoNtEnT-tYpE":null,"x-merge":"options","x-remove":null}""")));
        using var mixed = merged.Create(chat, Key);
        var allHeaders = Headers(mixed);
        Equal("application/json", allHeaders["content-type"]); Equal("options", allHeaders["x-merge"]);
        Check(!allHeaders.ContainsKey("x-remove") && allHeaders.Count == expectedHeaders.Count + 1,
            "Mixed-case content routing changed merge precedence or null removals.");
        Check(Same(expectedBody.Value, JsonData.Parse(await mixed.Content!.ReadAsStringAsync()).Value), "Mixed-case merge changed the body.");
        var bounded = new AnthropicMessagesKeyAuthRequestFactory(endpoint, Model, new(64), new(
            Headers: JsonData.Parse("""{"Content-Type":"text/plain"}"""), MaximumHeaders: expectedHeaders.Count - 1));
        Equal(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit,
            Throws<AnthropicMessagesKeyAuthRequestException>(() => bounded.Create(chat, Key)).Failure);
        for (var index = 0; index < unchanged.Length; index++) Equal(unchanged[index], chat.Messages[index].WireBody.ToString());
    }

    private static async Task ToolRoundTrip()
    {
        var firstBody = new HttpCleanupGateStream(Bytes(Sse(Start, ToolStart, ToolDelta(Arguments), ToolEnd, Finish("tool_use"), Stop)));
        var secondBody = new ProbeStream(Bytes(TextWire()));
        var responses = new[] { new StreamProbeContent(firstBody), new StreamProbeContent(secondBody) };
        var observations = new List<(HttpRequestMessage Request, string Body)>();
        using var handler = new FakeHttpHandler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token); observations.Add((request, body));
            return new(HttpStatusCode.OK) { Content = responses[observations.Count - 1] };
        });
        using var client = new HttpClient(handler);
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new Uri("https://authored.invalid"), Model, new(64));
        var transport = new AnthropicMessagesHttpSseTransport(client, (request, token) => factory.Create(request, Key, token),
            new(Framing: new(ReadBufferBytes: 1, RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent)));
        var effects = new Executor(); var commit = Gate(); var releaseCommit = Gate();
        var sink = new Sink(async observation =>
        {
            if (observation is AssistantMessageEnded)
            {
                Check(firstBody.Disposed && responses[0].Disposed, "Assistant commit preceded HTTP body/response cleanup.");
                await ThrowsAsync<ObjectDisposedException>(() => observations[0].Request.Content!.ReadAsStringAsync());
                commit.TrySetResult(); await releaseCommit.Task;
            }
        });
        var runner = new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("inspect", effects)]));
        var first = new ChatRequest(Model, [Entry(Declaration), Entry("""{"role":"user","content":"Use inspect.","timestamp":123}""")], 123);
        var running = runner.RunAsync(first, sink);
        try
        {
            await firstBody.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted && !commit.Task.IsCompleted && effects.Calls == 0, "Effects escaped HTTP terminal cleanup.");
            Check(!responses[0].Disposed, "Response disposed before asynchronous body settlement.");
            _ = await observations[0].Request.Content!.ReadAsStringAsync(); // Request remains alive during body cleanup.
            firstBody.ReleaseCleanup.TrySetResult(); await commit.Task.WaitAsync(Deadline);
            Equal(0, effects.Calls); releaseCommit.TrySetResult();
            var turn = await running.WaitAsync(Deadline);
            Check(turn.Chat.Failure is null && turn.Tools.ShouldContinue, "Actual tool turn did not authorize continuation.");
            Equal(1, effects.Calls); Equal(Arguments, effects.Arguments!.ToString());
            var assistant = new TranscriptEntry("assistant", PiWireJson.WriteMessage(turn.Chat.Message));
            var result = Entry("""{"role":"toolResult","toolCallId":"call-authored","toolName":"inspect","content":[{"type":"text","text":"owned result"}],"isError":false,"timestamp":124}""");
            var next = new ChatRequest(Model, first.Messages.Add(assistant).Add(result), 125);
            var final = await new TurnRunner(new ChatClient(transport), new ToolBatchScheduler([])).RunAsync(next, new Sink(_ => Task.CompletedTask));
            Check(final.Chat.Failure is null, "Actual result-to-next-request failed.");
            Equal("Observed \u03c0 \U0001f642", ((TextContent)final.Chat.Message.Content.Single()).Text);
            Equal(11L, final.Chat.Message.Usage.Input); Equal(2L, final.Chat.Message.Usage.Output); Equal(18L, final.Chat.Message.Usage.TotalTokens);
            Equal(2, handler.SendCalls); Check(!handler.Disposed, "Borrowed HTTP client was disposed.");
            using var replay = JsonDocument.Parse(observations[1].Body); var messages = replay.RootElement.GetProperty("messages");
            Equal(3, messages.GetArrayLength()); Equal("assistant", messages[1].GetProperty("role").GetString());
            var call = messages[1].GetProperty("content")[0]; Equal("call-authored", call.GetProperty("id").GetString());
            Equal("1", call.GetProperty("input").GetProperty("value").GetRawText());
            Equal("001", call.GetProperty("input").GetProperty("opaque").GetString()); Equal(JsonValueKind.Null, call.GetProperty("input").GetProperty("nil").ValueKind);
            var replayResult = messages[2].GetProperty("content")[0]; Equal("tool_result", replayResult.GetProperty("type").GetString());
            Equal("call-authored", replayResult.GetProperty("tool_use_id").GetString()); Equal("owned result", replayResult.GetProperty("content").GetString());
            Equal(Arguments, ((ToolCallContent)turn.Chat.Message.Content.Single()).Arguments.ToString());
            Equal("call-authored", result.WireBody.Value.GetProperty("toolCallId").GetString());
        }
        finally { firstBody.ReleaseCleanup.TrySetResult(); releaseCommit.TrySetResult(); try { await running; } catch { } }
    }

    private static async Task FramingAndAdmission()
    {
        var wire = TextWire();
        for (var split = 0; split <= Bytes(wire).Length; split++)
        {
            // Both sides of every byte boundary include split UTF-8, CR/LF and JSON escape positions.
            using var fixture = new Fixture(new ProbeStream(Bytes(wire), Math.Max(split, 1), Math.Max(Bytes(wire).Length - split, 1)));
            var result = await new ChatClient(fixture.Transport).CompleteAsync(Request());
            Check(result.Failure is null, "Valid fragmented named-event stream failed at byte boundary.");
            Equal("Observed \u03c0 \U0001f642", ((TextContent)result.Message.Content.Single()).Text);
        }
        foreach (var positive in new[]
        {
            wire.TrimEnd('\r', '\n'), // Public reader flushes its last partial line and pending event at EOF.
            Frame("unknown-future", "{private malformed ignored") + Frame("message", "[DONE]") + wire,
            Frame("message_delta", Start) + Sse(Finish("end_turn"), Stop), // Named acceptance precedes DTO type dispatch.
            wire + Frame("message", "[DONE]") + Frame("unknown", "{ignored after successful stop")
        })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(positive)));
            Check((await new ChatClient(fixture.Transport).CompleteAsync(Request())).Failure is null, "Source event selection/EOF rule changed.");
        }
        foreach (var (invalid, category) in new (string, string)[]
        {
            (Frame("message_start", "{private-invalid"), "SourceFailed"),
            (Frame("message_start", "[]"), "SourceFailed"),
            (Frame("message_start", Start.Replace("\"id\":\"msg-authored\"", "\"id\":\"private\",\"\\u0069d\":\"duplicate\"", StringComparison.Ordinal)), "SourceFailed"),
            (Frame("message_start", Start.Replace("msg-authored", "\\uD800", StringComparison.Ordinal)), "SourceFailed"),
            (Frame("message_start", "[DONE]"), "SourceFailed"),
            (Sse(Start, Finish("end_turn")) + Frame("message", "[DONE]"), "UnexpectedEof"),
            (Frame("message", Start) + Sse(Finish("end_turn"), Stop), "MalformedStream"),
            (Sse(Start, ToolStart, ToolDelta("{\"private\":"), ToolEnd, Finish("tool_use"), Stop), "MalformedStream"),
            (Frame("error", "private provider body; intentionally not JSON"), "ProviderError")
        })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(invalid)));
            var effects = new Executor(); var outcome = await new TurnRunner(new ChatClient(fixture.Transport), new ToolBatchScheduler([new("inspect", effects)]))
                .RunAsync(Request(), new Sink(_ => Task.CompletedTask));
            Failed(outcome.Chat, category); Equal(0, effects.Calls); Check(fixture.Response.Disposed, "Admission failure retained response.");
            // anthropic-messages.ts iterateAnthropicEvents: a named error event is shown as its raw data (new Error(sse.data)).
            if (category == "ProviderError") Equal("private provider body; intentionally not JSON", Metadata(outcome.Chat.Message, "errorMessage"));
            else Check(!outcome.Chat.Failure!.Message.Contains("private", StringComparison.Ordinal), "Rejected source text entered a diagnostic.");
        }
        using (var fixture = new Fixture(new ProbeStream([.. Bytes("event: message_start\ndata: "), 0xff, .. Bytes("\n\n")])))
            Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()), "SourceFailed");
    }

    private static async Task BudgetsAndConfiguration()
    {
        var events = new[] { Start, Finish("end_turn"), Stop }; var count = events.Length; var longest = events.Max(value => value.Length); var total = events.Sum(value => value.Length);
        using (var fixture = new Fixture(new ProbeStream(Bytes(Sse(events))), new(MaximumDataEvents: count,
            MaximumDataCharacters: longest, MaximumTotalDataCharacters: total, MaximumJsonDepth: 3)))
            Check((await new ChatClient(fixture.Transport).CompleteAsync(Request())).Failure is null, "Inclusive admission bounds rejected a valid stream.");
        foreach (var options in new AnthropicMessagesHttpSseOptions[]
        {
            new(MaximumDataEvents: count - 1), new(MaximumDataCharacters: longest - 1), new(MaximumTotalDataCharacters: total - 1),
            new(MaximumJsonDepth: 2), new(Framing: new(MaximumLineCharacters: 8, RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent))
        })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(Sse(events))), options);
            Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()), "SourceFailed");
            Check(fixture.Response.Disposed, "Lower wire admission failed without cleanup.");
        }
        using (var fixture = new Fixture(new ProbeStream(Bytes(TextWire())), messagesOptions: new(MaximumEvents: 2)))
            Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()), "ResourceLimit");
        using var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Unexpected send.")); using var client = new HttpClient(handler);
        foreach (var options in new AnthropicMessagesHttpSseOptions[]
        {
            new(MaximumDataEvents: 0), new(MaximumDataCharacters: 0), new(MaximumTotalDataCharacters: 0), new(MaximumJsonDepth: 65),
            new(Framing: new(RejectInvalidUtf8: false, EofBehavior: SseEofBehavior.DispatchPendingEvent)), new(Framing: new(RejectInvalidUtf8: true))
        }) Throws<ArgumentException>(() => new AnthropicMessagesHttpSseTransport(client, (_, _) => throw new InvalidOperationException(), options));
        foreach (var options in new AnthropicMessagesKeyAuthRequestOptions[]
        {
            new(MaxTokens: double.NaN), new(MaxTokens: double.PositiveInfinity), new(MaximumTokenMagnitude: double.NaN),
            new(Headers: JsonData.Parse("{\"private\\r\\nheader\":\"value\"}")), new(Headers: JsonData.Parse("{\"x-owned\":\"private\\r\\nvalue\"}")),
            new(Headers: JsonData.Parse("{\"x-owned\":\"private\\uD800\"}")),
            new(Headers: JsonData.Parse("{\"authorization\":\"Bearer private\"}")), new(Headers: JsonData.Parse("{\"content-length\":\"5\"}"))
        })
        {
            var error = Throws<AnthropicMessagesKeyAuthRequestException>(() => Factory(options)); Check(!error.Message.Contains("private", StringComparison.Ordinal), "Header configuration leaked rejected data.");
        }
        using var permissive = JsonDocument.Parse("{\"x-owned\":\"value\",}", new JsonDocumentOptions { AllowTrailingCommas = true });
        Throws<AnthropicMessagesKeyAuthRequestException>(() => Factory(new(Headers: JsonData.FromElement(permissive.RootElement))));
        var ordinary = Factory();
        foreach (var key in new[] { "", "private\r\nkey", "private key", "sk-ant-oat-private" })
        { var error = Throws<AnthropicMessagesKeyAuthRequestException>(() => ordinary.Create(Request(), key)); Check(!error.Message.Contains("private", StringComparison.Ordinal), "Explicit key diagnostic leaked data."); }
        var tooSmall = Factory(new(MaximumPayloadBytes: 32));
        var bounded = new AnthropicMessagesHttpSseTransport(client, (request, token) => tooSmall.Create(request, Key, token));
        Failed(await new ChatClient(bounded).CompleteAsync(Request()), "SourceFailed"); Equal(0, handler.SendCalls);
        using var before = new CancellationTokenSource(); before.Cancel();
        Throws<OperationCanceledException>(() => ordinary.Create(Request(), Key, before.Token)); Equal(0, handler.SendCalls);
    }

    private static async Task CancellationAndEarlyDispose()
    {
        var body = new HttpCleanupGateStream([]) { BlockRead = true };
        using (var fixture = new Fixture(body))
        using (var cancellation = new CancellationTokenSource())
        {
            var values = new List<StreamEvent>(); var running = Drain(fixture.Transport.StreamAsync(Request(), cancellation.Token), values);
            try
            {
                await body.ReadEntered.Task.WaitAsync(Deadline);
                Equal(cancellation.Token, fixture.FactoryToken); Equal(cancellation.Token, fixture.Response.AcquisitionToken);
                cancellation.Cancel(); await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!running.IsCompleted && !fixture.Response.Disposed, "Canceled operation escaped owned asynchronous cleanup.");
                _ = await fixture.OwnedRequests.Single().Content!.ReadAsStringAsync();
                body.ReleaseCleanup.TrySetResult(); await running.WaitAsync(Deadline);
                var terminal = values.OfType<StreamError>().Single(); Equal(StopReason.Aborted, terminal.Reason);
                Equal("Cancelled", Metadata(terminal.Message, "anthropicFailure"));
                Check(!fixture.Handler.Disposed && fixture.Response.Disposed, "Cancellation changed borrowed ownership.");
                await ThrowsAsync<ObjectDisposedException>(() => fixture.OwnedRequests.Single().Content!.ReadAsStringAsync());
            }
            finally { body.ReleaseCleanup.TrySetResult(); try { await running; } catch { } }
        }
        var early = new HttpCleanupGateStream(Bytes(TextWire()));
        using (var fixture = new Fixture(early))
        {
            var iterator = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
            Check(await iterator.MoveNextAsync() && iterator.Current is StreamStarted, "Missing normalized start."); Equal(0, fixture.Handler.SendCalls);
            Check(await iterator.MoveNextAsync() && iterator.Current is TextStarted, "Missing live text progress.");
            var cleanup = iterator.DisposeAsync().AsTask();
            try
            {
                await early.CleanupEntered.Task.WaitAsync(Deadline); Check(!cleanup.IsCompleted && !fixture.Response.Disposed, "Early disposal did not await body cleanup.");
                early.ReleaseCleanup.TrySetResult(); await cleanup.WaitAsync(Deadline); Equal(1, early.AsyncDisposeCalls);
                Check(!early.SyncDisposedBeforeAsync && fixture.Response.Disposed, "Early stop cleanup was reordered.");
                await ThrowsAsync<ObjectDisposedException>(() => fixture.OwnedRequests.Single().Content!.ReadAsStringAsync());
            }
            finally { early.ReleaseCleanup.TrySetResult(); await cleanup; }
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); using var unused = new Fixture(new ProbeStream([]));
        var pre = new ChatClient(unused.Transport).CompleteAsync(Request(), canceled.Token);
        Equal(ChatFailureKind.Cancelled, (await pre).Failure!.Kind); Equal(0, unused.FactoryCalls); Equal(0, unused.Handler.SendCalls);
    }

    private static async Task FaultsAndFreshRequests()
    {
        using var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("private send fault")); using var client = new HttpClient(handler);
        var failedFactory = new AnthropicMessagesHttpSseTransport(client, (_, _) => throw new InvalidOperationException("private factory fault"));
        Failed(await new ChatClient(failedFactory).CompleteAsync(Request()), "SourceFailed"); Equal(0, handler.SendCalls);
        var requests = new List<HttpRequestMessage>();
        var failedSend = new AnthropicMessagesHttpSseTransport(client, (request, token) => { var value = Factory().Create(request, Key, token); requests.Add(value); return value; });
        Failed(await new ChatClient(failedSend).CompleteAsync(Request()), "SourceFailed"); Equal(1, handler.SendCalls);
        await ThrowsAsync<ObjectDisposedException>(() => requests.Single().Content!.ReadAsStringAsync()); Check(!handler.Disposed, "Send fault disposed borrowed client.");
        using (var rejection = new Fixture(new ProbeStream(Bytes("private response body")), status: HttpStatusCode.TooManyRequests))
        {
            // The SDK reads a rejected body into its APIError message: makeMessage(status, safeJSON(text), text).
            var rejected = await new ChatClient(rejection.Transport).CompleteAsync(Request());
            Failed(rejected, "SourceFailed"); Equal("429 private response body", Metadata(rejected.Message, "errorMessage")); Equal(1, rejection.Response.AcquireCalls);
            Equal(0, rejection.Response.SerializeCalls); Equal(1, rejection.Handler.SendCalls); Check(rejection.Response.Disposed, "Rejected response retained ownership.");
        }
        foreach (var stream in new Stream[] { new ReadFailureStream(), new HttpCleanupFailureStream() })
        {
            using var fixture = new Fixture(stream); Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()), "SourceFailed");
            Check(fixture.Response.Disposed, "Read/cleanup failure skipped response disposal.");
            await ThrowsAsync<ObjectDisposedException>(() => fixture.OwnedRequests.Single().Content!.ReadAsStringAsync());
        }
        var owned = new List<HttpRequestMessage>(); var contents = new List<StreamProbeContent>();
        using var repeatedHandler = new FakeHttpHandler((_, _) =>
        { var content = new StreamProbeContent(new ProbeStream(Bytes(TextWire()))); contents.Add(content); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); });
        using var repeatedClient = new HttpClient(repeatedHandler);
        var repeated = new AnthropicMessagesHttpSseTransport(repeatedClient, (request, token) =>
        { var value = Factory().Create(request, Key, token); owned.Add(value); return value; });
        for (var index = 0; index < 2; index++) Check((await new ChatClient(repeated).CompleteAsync(Request())).Failure is null, "Repeated owned request failed.");
        Check(!ReferenceEquals(owned[0], owned[1]) && contents.All(content => content.Disposed), "Request/response was reused or retained.");
        foreach (var request in owned) await ThrowsAsync<ObjectDisposedException>(() => request.Content!.ReadAsStringAsync());
    }

    private static async Task GenuineSdkRequests()
    {
        var root = FindRoot();
        // Admit all immutable byte identities before parsing any fixture. Root's unchanged-source oracle execution is separate from this native test.
        var inputBytes = await Pinned(root, "tools/PiReferenceRunner/anthropic-sdk-inputs.json", "80ab9885440ccca2e01ed421b89a17899f3ecd25f92a558b809ca247921486e4");
        var expectedBytes = await Pinned(root, "fixtures/pi-v0.99.1/anthropic-sdk/core.expected.json", "eedd5bb2658f13fc8b722a17912e704c33a5bf5001f54689f5aadb49be511bb9");
        var lockBytes = await Pinned(root, "fixtures/pi-v0.99.1/anthropic-sdk/oracle.lock.json", "282af62b697e0b2dfcbcf45cbff0dc1ae2443c714de0104d4ab3962b51c7a5c0");
        var manifestBytes = await Pinned(root, "fixtures/pi-v0.99.1/anthropic-sdk/manifest.json", "f994319697037e2fcf6f057a26933dbd17f350cd9ce458fcab0f0d10fb474e1b");
        using var input = JsonDocument.Parse(inputBytes); using var expected = JsonDocument.Parse(expectedBytes);
        using var receipt = JsonDocument.Parse(lockBytes); using var manifest = JsonDocument.Parse(manifestBytes);
        const string source = "d86654abb8862e201933517d6f1fce9f88dd117f";
        Equal(source, input.RootElement.GetProperty("sourceSha").GetString()); Equal(source, expected.RootElement.GetProperty("sourceSha").GetString());
        Equal(source, receipt.RootElement.GetProperty("environmentPins").GetProperty("sourceSha").GetString()); Equal(source, manifest.RootElement.GetProperty("sourceSha").GetString());
        var checks = expected.RootElement.GetProperty("observations").GetProperty("checks"); Equal(3, checks.GetProperty("caseCount").GetInt32()); Equal(3, checks.GetProperty("fakeFetchCalls").GetInt32());
        Check(checks.GetProperty("noSourceTransformOrSdkShim").GetBoolean() && checks.GetProperty("actualHeadersRetained").GetBoolean() && checks.GetProperty("networkAndProcessesBlocked").GetBoolean(), "Reference provenance changed.");
        var declaration = input.RootElement.GetProperty("model");
        var model = new ModelDescriptor(declaration.GetProperty("id").GetString()!, declaration.GetProperty("api").GetString()!, declaration.GetProperty("provider").GetString()!);
        var wire = input.RootElement.GetProperty("response").GetProperty("sseText").GetString()!;
        Equal(expected.RootElement.GetProperty("observations").GetProperty("responseWire").GetProperty("utf8Sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Bytes(wire))));
        var captured = expected.RootElement.GetProperty("observations").GetProperty("cases"); var ordinal = 0;
        foreach (var item in input.RootElement.GetProperty("cases").EnumerateArray())
        {
            Equal(item.GetProperty("caseId").GetString(), captured[ordinal].GetProperty("caseId").GetString());
            var options = item.GetProperty("options"); var context = JsonNode.Parse(item.GetProperty("context").GetRawText())!;
            if (item.TryGetProperty("numberConversions", out var specification))
            {
                var arguments = context["messages"]![specification.GetProperty("messageIndex").GetInt32()]!["content"]![specification.GetProperty("contentIndex").GetInt32()]!["arguments"]!;
                foreach (var field in specification.GetProperty("fields").EnumerateArray())
                    arguments[field.GetProperty("field").GetString()!] = JsonNode.Parse(field.GetProperty("lexeme").GetString()!);
            }
            var messages = context["messages"]!.AsArray().Select(value => Entry(value!.ToJsonString())).ToImmutableArray();
            var unchanged = messages.Select(value => value.WireBody.ToString()).ToArray();
            var cache = options.TryGetProperty("cacheRetention", out var retention) ? retention.GetString() switch
                { "none" => AnthropicCacheRetention.None, "long" => AnthropicCacheRetention.Long, _ => AnthropicCacheRetention.Short } : AnthropicCacheRetention.Short;
            var projection = new AnthropicMessagesRequestOptions(declaration.GetProperty("maxTokens").GetInt32(), CacheRetention: cache,
                Temperature: options.TryGetProperty("temperature", out var temperature) ? temperature.GetDecimal() : null);
            var modelHeaders = item.TryGetProperty("modelOverrides", out var overrides) && overrides.TryGetProperty("headers", out var headerValue) ? JsonData.FromElement(headerValue) : null;
            var optionHeaders = options.TryGetProperty("headers", out headerValue) ? JsonData.FromElement(headerValue) : null;
            var profile = new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: options.TryGetProperty("maxTokens", out var cap) ? cap.GetDouble() : null,
                ModelHeaders: modelHeaders, Headers: optionHeaders, SessionId: options.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
                SendSessionAffinityHeaders: item.TryGetProperty("modelOverrides", out overrides) && overrides.GetProperty("compat").GetProperty("sendSessionAffinityHeaders").GetBoolean());
            var capturedRequest = captured[ordinal].GetProperty("fetchRequests")[0]; var sourceBody = capturedRequest.GetProperty("rawBody").GetString()!;
            Equal(capturedRequest.GetProperty("rawBodyUtf8Sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Bytes(sourceBody))));
            using var sdk = JsonDocument.Parse(sourceBody);
            var key = input.RootElement.GetProperty("commonOptions").GetProperty("apiKey").GetString()!;
            var actualHeaders = capturedRequest.GetProperty("headers").EnumerateArray().ToDictionary(pair => pair[0].GetString()!, pair => pair[1].GetString()!, StringComparer.Ordinal);
            var defaultFactory = new AnthropicMessagesKeyAuthRequestFactory(new(declaration.GetProperty("baseUrl").GetString()!), model, projection, profile);
            var defaultObserved = 0; Exception? defaultObservationFailure = null;
            using (var defaultHandler = new FakeHttpHandler(async (request, token) =>
            {
                try
                {
                defaultObserved++;
                Equal(capturedRequest.GetProperty("url").GetString(), request.RequestUri!.AbsoluteUri); Equal(capturedRequest.GetProperty("method").GetString(), request.Method.Method);
                // Match fake fetch's observation point: all headers are captured before diagnostic content buffering.
                var defaults = Headers(request);
                var differences = actualHeaders.Keys.Union(defaults.Keys, StringComparer.Ordinal).Where(name =>
                    !actualHeaders.TryGetValue(name, out var reference) || !defaults.TryGetValue(name, out var current) || reference != current).Order(StringComparer.Ordinal).ToArray();
                Check(differences.SequenceEqual(new[] { "user-agent", "x-stainless-arch", "x-stainless-lang", "x-stainless-os", "x-stainless-package-version", "x-stainless-retry-count", "x-stainless-runtime", "x-stainless-runtime-version", "x-stainless-timeout" }),
                    "Default runtime metadata difference inventory changed: " + DifferenceInventory(actualHeaders, defaults) + "; case=" + item.GetProperty("caseId").GetString());
                var text = await request.Content!.ReadAsStringAsync(token);
                using var body = JsonDocument.Parse(text); Check(Same(sdk.RootElement, body.RootElement), "Default native full body differs from SDK: " + ordinal);
                // Reading ByteArrayContent materializes Content-Length in this .NET profile; retain and verify the side effect instead of masking a field.
                var bufferedHeaders = Headers(request);
                Check(!defaults.ContainsKey("content-length") && bufferedHeaders.Keys.Except(defaults.Keys, StringComparer.Ordinal).SequenceEqual(new[] { "content-length" }) &&
                    defaults.All(pair => bufferedHeaders.TryGetValue(pair.Key, out var value) && value == pair.Value), "Diagnostic content buffering changed an unexpected header.");
                Equal(Bytes(text).Length.ToString(System.Globalization.CultureInfo.InvariantCulture), bufferedHeaders["content-length"]);
                return new(HttpStatusCode.OK) { Content = new StreamProbeContent(new ProbeStream(Bytes(wire))) };
                }
                catch (Exception error) { defaultObservationFailure = error; throw; }
            }))
            using (var defaultClient = new HttpClient(defaultHandler))
            {
                var plain = await new ChatClient(new AnthropicMessagesHttpSseTransport(defaultClient, (request, token) => defaultFactory.Create(request, key, token)))
                    .CompleteAsync(new(model, messages, input.RootElement.GetProperty("clock").GetProperty("unixMilliseconds").GetInt64()));
                if (defaultObservationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(defaultObservationFailure).Throw();
                Check(plain.Failure is null, "Default native genuine-wire replay failed."); Equal(1, defaultObserved);
                Check(Same(captured[ordinal].GetProperty("finalResult"), PiWireJson.WriteMessage(plain.Message).Value), "Default native final message differs from captured public stream.");
            }
            // Authored replay inputs represent the frozen JS SDK environment, never discovered or emitted by the default native profile.
            var authoredHeaders = JsonNode.Parse(optionHeaders?.ToString() ?? "{}")!.AsObject(); authoredHeaders["user-agent"] = "pi (win32 10.0.26200; x64)";
            var timeout = options.TryGetProperty("timeoutMs", out var milliseconds) ? milliseconds.GetInt32() / 1000 : 600;
            var runtimeHeaders = JsonData.Parse("{\"x-stainless-arch\":\"x64\",\"x-stainless-lang\":\"js\",\"x-stainless-os\":\"Windows\",\"x-stainless-package-version\":\"0.124.0\",\"x-stainless-retry-count\":\"0\",\"x-stainless-runtime\":\"node\",\"x-stainless-runtime-version\":\"v24.19.0\",\"x-stainless-timeout\":\"" + timeout + "\"}");
            var factory = new AnthropicMessagesKeyAuthRequestFactory(new(declaration.GetProperty("baseUrl").GetString()!), model, projection,
                profile with { RuntimeHeaders = runtimeHeaders, Headers = JsonData.Parse(authoredHeaders.ToJsonString()) });
            var observed = 0; Exception? observationFailure = null;
            using var handler = new FakeHttpHandler(async (request, token) =>
            {
                try
                {
                observed++; Equal(capturedRequest.GetProperty("url").GetString(), request.RequestUri!.AbsoluteUri); Equal(capturedRequest.GetProperty("method").GetString(), request.Method.Method);
                var headers = Headers(request); Check(headers.Count == actualHeaders.Count && actualHeaders.All(pair => headers.TryGetValue(pair.Key, out var value) && value == pair.Value),
                    "Complete explicitly configured HTTP header observation differs from SDK: " + DifferenceInventory(actualHeaders, headers));
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Check(Same(sdk.RootElement, body.RootElement), "Complete actual HTTP request differs from SDK.");
                if (item.TryGetProperty("numberConversions", out _))
                {
                    var arguments = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("input");
                    var conversions = captured[ordinal].GetProperty("numberConversions"); Equal(8, conversions.GetArrayLength());
                    foreach (var conversion in conversions.EnumerateArray()) Equal(conversion.GetProperty("jsonStringifyNumber").GetString(), arguments.GetProperty(conversion.GetProperty("field").GetString()!).GetRawText());
                    Equal(JsonValueKind.Null, arguments.GetProperty("retainedNull").ValueKind); Equal("001", arguments.GetProperty("opaque").GetString());
                }
                return new(HttpStatusCode.OK) { Content = new StreamProbeContent(new ProbeStream(Bytes(wire), Enumerable.Repeat(input.RootElement.GetProperty("response").GetProperty("chunkBytes").GetInt32(), Bytes(wire).Length).ToArray())) };
                }
                catch (Exception error) { observationFailure = error; throw; }
            });
            using var client = new HttpClient(handler);
            var result = await new ChatClient(new AnthropicMessagesHttpSseTransport(client, (request, token) => factory.Create(request, key, token)))
                .CompleteAsync(new(model, messages, input.RootElement.GetProperty("clock").GetProperty("unixMilliseconds").GetInt64()));
            if (observationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationFailure).Throw();
            Check(result.Failure is null, "Native genuine-wire text replay failed."); Equal(1, observed);
            Check(Same(captured[ordinal].GetProperty("finalResult"), PiWireJson.WriteMessage(result.Message).Value), "Complete native final message differs from captured public stream.");
            for (var index = 0; index < messages.Length; index++) Equal(unchanged[index], messages[index].WireBody.ToString());
            ordinal++;
        }
        Equal(3, ordinal);
        using var one = JsonDocument.Parse("{\"value\":1,\"nil\":null,\"order\":[\"a\",\"b\"]}");
        foreach (var negative in new[] { "{\"value\":1.0,\"nil\":null,\"order\":[\"a\",\"b\"]}", "{\"value\":1,\"order\":[\"a\",\"b\"]}", "{\"value\":1,\"nil\":null,\"order\":[\"b\",\"a\"]}" })
        { using var changed = JsonDocument.Parse(negative); Check(!Same(one.RootElement, changed.RootElement), "Comparator erased raw numbers, null presence or array order."); }
    }
    private static Dictionary<string, string> Headers(HttpRequestMessage request)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // HttpHeaders renders typed values with their parser's separator (e.g. spaces for User-Agent, commas for Accept).
        // Read the complete field text; enumerating GetValues and universally joining with commas changes field syntax.
        foreach (var line in (request.Headers.ToString() + request.Content!.Headers.ToString()).Split("\r\n", StringSplitOptions.None))
        {
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            Check(colon > 0 && line[..colon].All(character => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character)), "Invalid rendered HTTP field name.");
            var value = line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..]; // Remove only the renderer's delimiter space; retain the complete value lexically.
            Check(value.All(character => character == '\t' || character is >= ' ' and <= '~'), "Invalid rendered HTTP field value.");
            Check(result.TryAdd(line[..colon].ToLowerInvariant(), value), "Duplicate rendered HTTP field name.");
        }
        return result;
    }
    private static void HeaderFieldSerializationControls()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://authored.invalid") { Content = new ByteArrayContent([]) };
        request.Headers.UserAgent.ParseAdd("pi (win32 10.0.26200; x64)");
        request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/plain; q=0.5");
        request.Headers.Authorization = new("Bearer", "authored-observer-noncredential");
        Check(request.Headers.TryAddWithoutValidation("x-opaque", new[] { "first: 001", "\"second, third\"" }), "Could not author opaque lexical header control.");
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        var observed = Headers(request);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user-agent"] = "pi (win32 10.0.26200; x64)", ["accept"] = "application/json, text/plain; q=0.5",
            ["authorization"] = "Bearer authored-observer-noncredential", ["x-opaque"] = "first: 001, \"second, third\"",
            ["content-type"] = "application/json; charset=utf-8"
        };
        Check(observed.Count == expected.Count && expected.All(pair => observed.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "Lexical field serialization lost a typed separator, opaque colon/quote/comma, authentication value or content header.");
        Check(string.Join(", ", request.Headers.GetValues("User-Agent")) != observed["user-agent"], "User-Agent control failed to distinguish the old generic comma observer.");
        var reparsed = Headers(request);
        Check(reparsed.Count == observed.Count && observed.All(pair => reparsed.TryGetValue(pair.Key, out var value) && value == pair.Value), "Parsing typed values changed their rendered field text.");
    }
    private static string DifferenceInventory(Dictionary<string, string> source, Dictionary<string, string> native) =>
        string.Join("; ", source.Keys.Union(native.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(name => !source.TryGetValue(name, out var original) || !native.TryGetValue(name, out var current) || original != current)
            .Select(name => name + " [source=" + Value(source, name) + ", native=" + Value(native, name) + "]"));
    private static string Value(Dictionary<string, string> fields, string name) => !fields.TryGetValue(name, out var value) ? "<absent>" :
        name is "x-api-key" or "authorization" or "cf-aig-authorization" ? "<redacted-present>" : value;
    private static async Task<byte[]> Pinned(string root, string relative, string hash)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, relative)); Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes))); return bytes;
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools/PiReferenceRunner/anthropic-sdk-inputs.json"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate frozen Anthropic SDK input.");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly StreamProbeContent Response; public readonly FakeHttpHandler Handler; public readonly HttpClient Client;
        public readonly AnthropicMessagesHttpSseTransport Transport; public readonly List<HttpRequestMessage> OwnedRequests = [];
        public int FactoryCalls; public CancellationToken FactoryToken;
        public Fixture(Stream body, AnthropicMessagesHttpSseOptions? options = null, HttpStatusCode status = HttpStatusCode.OK,
            AnthropicMessagesOptions? messagesOptions = null)
        {
            Response = new(body); Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = Response })); Client = new(Handler);
            var factory = Factory();
            Transport = new(Client, (request, token) => { FactoryCalls++; FactoryToken = token; var value = factory.Create(request, Key, token); OwnedRequests.Add(value); return value; }, options, messagesOptions);
        }
        public void Dispose() { Client.Dispose(); Response.Dispose(); }
    }
    private sealed class Executor : IToolExecutor
    {
        public int Calls; public JsonData? Arguments;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Arguments = invocation.Call.Arguments; return ValueTask.FromResult(ToolResult.Success("owned result")); }
    }
    private sealed class Sink(Func<AgentEvent, Task> observe) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => new(observe(observation)); }
    private static AnthropicMessagesKeyAuthRequestFactory Factory(AnthropicMessagesKeyAuthRequestOptions? options = null) =>
        new(new Uri("https://authored.invalid"), Model, new(64), options);
    private static ChatRequest Request() => new(Model,
        [Entry("""{"role":"system","content":"Keep 001.","timestamp":123}"""), Entry("""{"role":"user","content":"Reply with \u03c0\nline 2","timestamp":123}""")], 123);
    private static TranscriptEntry Entry(string json) { var value = JsonData.Parse(json); return new(value.Value.GetProperty("role").GetString()!, value); }
    private static string Frame(string name, string data) => "event: " + name + "\r\ndata: " + data + "\r\n\r\n";
    private static string Sse(params string[] values) => string.Concat(values.Select(value => Frame(JsonData.Parse(value).Value.GetProperty("type").GetString()!, value)));
    private static string Finish(string reason) => "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + reason + "\"},\"usage\":{\"output_tokens\":2,\"input_tokens\":null,\"cache_read_input_tokens\":null,\"cache_creation_input_tokens\":null}}";
    private static string ToolDelta(string text) => JsonSerializer.Serialize(new { type = "content_block_delta", index = 9, delta = new { type = "input_json_delta", partial_json = text } });
    private static string TextWire() => ": comment\r\n\r\n" + Sse(Start, TextStart,
        JsonSerializer.Serialize(new { type = "content_block_delta", index = 4, delta = new { type = "text_delta", text = "Observed \u03c0 \U0001f642" } }), TextEnd, Finish("end_turn"), Stop);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Header(HttpRequestMessage request, string name) => request.Headers.GetValues(name).Single();
    private static string? Metadata(AssistantMessage message, string name) => message.ExtraProperties!.Values[name].Value.GetString();
    private static async Task Drain(IAsyncEnumerable<StreamEvent> stream, List<StreamEvent> values) { await foreach (var value in stream) values.Add(value); }
    private static void Failed(ChatResult value, string category)
    { Check(value.Failure is not null && value.Message.StopReason is StopReason.Error or StopReason.Aborted, "Failed stream succeeded."); Equal(category, Metadata(value.Message, "anthropicFailure")); }
    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var properties = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var other = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            return properties.Count == other.Count && properties.All(pair => other.TryGetValue(pair.Key, out var value) && Same(pair.Value, value));
        }
        if (left.ValueKind == JsonValueKind.Array) return left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
