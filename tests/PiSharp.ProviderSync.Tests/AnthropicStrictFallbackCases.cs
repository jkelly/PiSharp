using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Authored expectations derived by reading Pi v1.2.0 (06a466935536f8c9bcacb2d5efc0fc021be1bdff) source text, not captured:
// packages/ai/src/api/anthropic-messages.ts (stream: the preferred strict tool fallback; buildParams/convertTools with
// disablePreferredStrictTools), packages/ai/src/utils/diagnostics.ts and packages/ai/test/anthropic-strict-tool-fallback.test.ts
// (#10409), whose rejection texts and cases these follow. Fake HTTP only.
internal static partial class Program
{
    private const string GrammarError =
        "The compiled grammar is too large, which would cause performance issues. Simplify your tool schemas or reduce the number of strict tools.";
    // Observed from the live API (upstream test): 32 strict tools, then 20 tools with 24 optional fields each.
    private static readonly string[] StrictLimitErrors =
    [
        GrammarError,
        "Too many strict tools (32). The maximum number of strict tools supported is 20. Try reducing the number of tools marked as strict.",
        "Schemas contains too many parameters with union types (480 parameters with type arrays or anyOf). This causes exponential compilation cost. Reduce the number of nullable or union-typed parameters (limit: 16 parameters with unions).",
    ];
    private const string LookupParameters = """{"type":"object","properties":{"value":{"type":"string"},"optional":{"type":"number"}},"required":["value"]}""";
    private const string StrictLookupSchema = """{"type":"object","properties":{"value":{"type":"string"},"optional":{"anyOf":[{"type":"number"},{"type":"null"}]}},"required":["value","optional"],"additionalProperties":false}""";
    private const string PlainLookupSchema = """{"type":"object","properties":{"value":{"type":"string"},"optional":{"type":"number"}},"required":["value"]}""";

    private static string StrictTool(string name, string? strict, string parameters = LookupParameters) =>
        "{\"name\":\"" + name + "\",\"description\":\"Look up a value\",\"parameters\":" + parameters +
        (strict is null ? "" : ",\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"" + strict + "\"}") + "}";
    private static ImmutableArray<TranscriptEntry> StrictTranscript(params string[] tools) =>
    [
        Entry("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[" + string.Join(",", tools) + "],\"timestamp\":0}"),
        Entry("""{"role":"user","content":"Hello","timestamp":1}""")
    ];
    private static AnthropicMessagesRequestOptions StrictOptions(AnthropicCacheRetention cache = AnthropicCacheRetention.None) =>
        new(100, SupportsStrictTools: true, CacheRetention: cache);

    private sealed class StrictEndpoint(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonObject> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payloads.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject());
            return respond(Payloads.Count);
        }
    }
    private static HttpResponseMessage StrictRejection(string message = GrammarError, int status = 400, string type = "invalid_request_error") =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { type = "error", error = new { type, message } }), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage StrictSuccess()
    {
        static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                Frame("message_start", new { type = "message_start", message = new { id = "msg_test", model = "fixture-haiku", usage = new { input_tokens = 10, output_tokens = 0 } } }) +
                Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 1 } }) +
                Frame("message_stop", new { type = "message_stop" }), Encoding.UTF8, "text/event-stream")
        };
    }
    private static IChatTransport StrictKeyRoute(StrictEndpoint endpoint, AnthropicMessagesRequestOptions options, AnthropicMessagesHooks? hooks = null)
    {
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), Haiku, options);
        var client = new HttpClient(endpoint, disposeHandler: false);
        return hooks?.OnPayload is not null
            ? AnthropicMessagesHttpSseTransport.FromPrepared(client, (request, token) => factory.Prepare(request, Key, token), hooks: hooks)
            : new AnthropicMessagesHttpSseTransport(client, (request, token) => factory.Create(request, Key, token), hooks: hooks);
    }
    private static async Task<(List<StreamEvent> Events, AssistantMessage Message)> StrictRun(IChatTransport transport, ImmutableArray<TranscriptEntry> transcript,
        CancellationToken token = default)
    {
        var events = new List<StreamEvent>();
        await foreach (var item in transport.StreamAsync(new(Haiku, transcript, 1), token).WithCancellation(token)) events.Add(item);
        return (events, ((StreamTerminalEvent)events[^1]).Message);
    }
    private static JsonArray StrictTools(JsonObject payload) => payload["tools"]!.AsArray();
    private static string? Strictness(JsonNode? tool) => tool!["strict"]?.ToJsonString();
    private static string ErrorText(AssistantMessage message) => message.ExtraProperties!.TryGet("errorMessage", out var value) ? value!.Value.GetString()! : "";
    private static JsonArray? Diagnostics(AssistantMessage message) =>
        message.ExtraProperties?.TryGet("diagnostics", out var value) == true ? JsonNode.Parse(value!.ToString())!.AsArray() : null;
    private static void StrictFallbackDiagnostic(AssistantMessage message, string rejection)
    {
        var diagnostic = Diagnostics(message)?[0]?.AsObject() ?? throw new InvalidOperationException("No anthropic_strict_tools_fallback diagnostic.");
        Equal("anthropic_strict_tools_fallback", diagnostic["type"]!.GetValue<string>());
        Equal("""{"reason":"strict_tool_limits_exceeded"}""", diagnostic["details"]!.ToJsonString());
        // error.message is the SDK APIError message: the status and the whole parsed body.
        Equal("400 " + JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = rejection } }), diagnostic["error"]!["message"]!.GetValue<string>());
        Check(diagnostic["timestamp"]!.GetValue<long>() > 0, "Diagnostic timestamp missing.");
    }

    // Each strict-limit rejection is retried once with preferred tools non-strict and their original schema; required and ordinary tools
    // are resent unchanged; onPayload sees both payloads (stream forced on), onResponse only the accepted response.
    private static async Task AnthropicStrictFallbackRetries()
    {
        foreach (var rejection in StrictLimitErrors)
        foreach (var hooked in new[] { false, true })
        {
            var endpoint = new StrictEndpoint(attempt => attempt == 1 ? StrictRejection(rejection) : StrictSuccess());
            var payloadCalls = 0; var responseCalls = 0;
            var hooks = !hooked ? null : new AnthropicMessagesHooks(OnResponse: (_, _, _) => { responseCalls++; return Task.CompletedTask; })
            {
                OnPayload = (payload, _, _) =>
                {
                    payloadCalls++; var replaced = JsonNode.Parse(payload.ToString())!.AsObject();
                    replaced["inference_geo"] = "us"; replaced["stream"] = false;
                    return Task.FromResult<JsonData?>(JsonData.Parse(replaced.ToJsonString()));
                }
            };
            var transcript = StrictTranscript(StrictTool("preferred", "prefer"), StrictTool("required", "require"), StrictTool("ordinary", null));
            var (events, message) = await StrictRun(StrictKeyRoute(endpoint, StrictOptions(AnthropicCacheRetention.Long), hooks), transcript).WaitAsync(Deadline);
            Equal(StopReason.Stop, message.StopReason);
            Check(events[0] is StreamStarted && events[^1] is StreamDone && events.Count == 2, "Unexpected events: " + string.Join(",", events.Select(item => item.GetType().Name)));
            Equal(2, endpoint.Payloads.Count);
            if (hooked) { Equal(2, payloadCalls); Equal(1, responseCalls); }
            var first = StrictTools(endpoint.Payloads[0]); var second = StrictTools(endpoint.Payloads[1]);
            Equal("true", Strictness(first[0])); Equal(StrictLookupSchema, first[0]!["input_schema"]!.ToJsonString());
            Equal(null, Strictness(second[0])); Equal(PlainLookupSchema, second[0]!["input_schema"]!.ToJsonString());
            Equal(first[1]!.ToJsonString(), second[1]!.ToJsonString()); Equal("true", Strictness(second[1]));
            Equal(first[2]!.ToJsonString(), second[2]!.ToJsonString());
            Equal("""{"type":"ephemeral","ttl":"1h"}""", second[2]!["cache_control"]!.ToJsonString());
            Equal("true,true", string.Join(",", endpoint.Payloads.Select(payload => payload["stream"]!.ToJsonString())));
            if (hooked) Equal("\"us\"", endpoint.Payloads[1]["inference_geo"]!.ToJsonString());
            StrictFallbackDiagnostic(message, rejection);
        }
    }

    // The production routes: the native key provider and the resolved (authenticated) provider fall back the same way; inline
    // additions and same-name redefinitions downgrade only their "prefer" definition.
    private static async Task AnthropicStrictFallbackRoutes()
    {
        var options = StrictOptions() with { SupportsMidConversationSystemMessages = true, SupportsMidConversationToolChanges = true };
        var transcript = StrictTranscript(StrictTool("lookup", "require")).AddRange((TranscriptEntry[])
        [
            Entry("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[" + StrictTool("lookup", "prefer") + "],\"timestamp\":2}"),
            Entry("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[" + StrictTool("lookup", "require") + "],\"timestamp\":3}"),
        ]);
        static List<JsonObject> Additions(JsonObject payload) => payload["messages"]!.AsArray()
            .SelectMany(message => message!["content"] is JsonArray blocks ? blocks : [])
            .Where(block => block!["type"]?.GetValue<string>() == "tool_addition").Select(block => block!["tool"]!["definition"]!.AsObject()).ToList();
        foreach (var route in new[] { "native", "resolved" })
        {
            var endpoint = new StrictEndpoint(attempt => attempt == 1 ? StrictRejection() : StrictSuccess());
            AssistantMessage message;
            if (route == "native")
            {
                using var provider = NativeProviderFactory.CreateAnthropic(Haiku, new("https://anthropic.invalid"), Key, options, handler: endpoint);
                message = (await StrictRun(provider.Transport, transcript).WaitAsync(Deadline)).Message;
            }
            else
            {
                var resolution = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(
                    new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>("ANTHROPIC_API_KEY", Key)]), (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(null));
                await using var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Haiku, resolution, (selected, binding, _) =>
                {
                    var provider = AnthropicResolvedProviderFactory.Create(selected, new("https://anthropic.invalid"), binding, options, handler: endpoint);
                    return ValueTask.FromResult(new AnthropicTransportAdmission(selected, provider.Transport, new ProviderLease(provider)));
                });
                message = (await StrictRun(lease.Transport, transcript).WaitAsync(Deadline)).Message;
            }
            Equal(StopReason.Stop, message.StopReason); Equal(2, endpoint.Payloads.Count);
            Equal(StrictTools(endpoint.Payloads[0]).ToJsonString(), StrictTools(endpoint.Payloads[1]).ToJsonString());
            var before = Additions(endpoint.Payloads[0]); var after = Additions(endpoint.Payloads[1]);
            Equal("true,true", string.Join(",", before.Select(Strictness)));
            Equal(",true", string.Join(",", after.Select(Strictness)));
            Equal("""["value"]""", after[0]["input_schema"]!["required"]!.ToJsonString());
            Equal(before[1].ToJsonString(), after[1].ToJsonString());
            StrictFallbackDiagnostic(message, GrammarError);
        }
    }

    // No fallback without a preferred tool sent strict, for other rejections, for a second rejection, a hook failure, cancellation
    // before the fallback, or an error event after the response started; later requests start strict again.
    private static async Task AnthropicStrictFallbackRefusals()
    {
        foreach (var (name, tools) in new (string, string[])[]
        {
            ("required", [StrictTool("lookup", "require")]),
            ("ordinary", [StrictTool("lookup", null)]),
            ("locally unsupported prefer", [StrictTool("lookup", "prefer", """{"type":"object","properties":{"value":{"type":"number","minimum":1}},"required":["value"]}""")]),
        })
        {
            var endpoint = new StrictEndpoint(_ => StrictRejection());
            var (_, message) = await StrictRun(StrictKeyRoute(endpoint, StrictOptions()), StrictTranscript(tools)).WaitAsync(Deadline);
            Equal(StopReason.Error, message.StopReason); Check(ErrorText(message).Contains(GrammarError, StringComparison.Ordinal), name + ": " + ErrorText(message));
            Equal(1, endpoint.Payloads.Count); Equal(null, Diagnostics(message));
        }
        foreach (var (name, text, status, type) in new[]
        {
            ("unrelated 400", "Invalid tool schema", 400, "invalid_request_error"),
            ("wrong error type", GrammarError, 400, "authentication_error"),
            ("wrong HTTP status", GrammarError, 500, "invalid_request_error"),
        })
        {
            var endpoint = new StrictEndpoint(_ => StrictRejection(text, status, type));
            var (_, message) = await StrictRun(StrictKeyRoute(endpoint, StrictOptions()), StrictTranscript(StrictTool("lookup", "prefer"))).WaitAsync(Deadline);
            Equal(StopReason.Error, message.StopReason); Equal(1, endpoint.Payloads.Count);
            Check(Diagnostics(message) is null, name + " recorded a fallback.");
        }
        // A second rejection is surfaced, with the fallback recorded; the required tool stayed strict.
        foreach (var hooked in new[] { false, true })
        {
            var endpoint = new StrictEndpoint(attempt => StrictRejection(attempt == 1 ? GrammarError : "Required grammar is still too large"));
            var hooks = hooked ? new AnthropicMessagesHooks { OnPayload = (_, _, _) => Task.FromResult<JsonData?>(null) } : null;
            var (_, message) = await StrictRun(StrictKeyRoute(endpoint, StrictOptions(), hooks), StrictTranscript(StrictTool("lookup", "prefer"), StrictTool("required", "require"))).WaitAsync(Deadline);
            Equal(StopReason.Error, message.StopReason); Check(ErrorText(message).Contains("Required grammar is still too large", StringComparison.Ordinal), ErrorText(message));
            Equal(2, endpoint.Payloads.Count); Equal("true", Strictness(StrictTools(endpoint.Payloads[1])[1]));
            StrictFallbackDiagnostic(message, GrammarError);
        }
        // A hook exception is not a provider rejection, even with the same text: nothing is sent and nothing is recorded.
        {
            var endpoint = new StrictEndpoint(_ => StrictSuccess());
            var hooks = new AnthropicMessagesHooks { OnPayload = (_, _, _) => throw new InvalidOperationException("400 " + GrammarError) };
            var (_, message) = await StrictRun(StrictKeyRoute(endpoint, StrictOptions(), hooks), StrictTranscript(StrictTool("lookup", "prefer"))).WaitAsync(Deadline);
            Equal(StopReason.Error, message.StopReason); Equal(0, endpoint.Payloads.Count); Equal(null, Diagnostics(message));
        }
        // Cancellation before the fallback attempt aborts with the one request.
        foreach (var hooked in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var endpoint = new StrictEndpoint(_ => { cancellation.Cancel(); return StrictRejection(); });
            var hooks = hooked ? new AnthropicMessagesHooks { OnPayload = (_, _, _) => Task.FromResult<JsonData?>(null) } : null;
            var events = new List<StreamEvent>();
            try { await foreach (var item in StrictKeyRoute(endpoint, StrictOptions(), hooks).StreamAsync(new(Haiku, StrictTranscript(StrictTool("lookup", "prefer")), 1), cancellation.Token)) events.Add(item); }
            catch (OperationCanceledException) { }
            Equal(1, endpoint.Payloads.Count);
            Check(events.Count == 0 || events[^1] is StreamError { Reason: StopReason.Aborted }, "Cancelled fallback did not abort.");
        }
        // An error event after the response headers is never downgraded.
        {
            var endpoint = new StrictEndpoint(_ => new(HttpStatusCode.OK)
            {
                Content = new StringContent("event: error\ndata: " + JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = GrammarError } }) + "\n\n",
                    Encoding.UTF8, "text/event-stream")
            });
            var (_, message) = await StrictRun(StrictKeyRoute(endpoint, StrictOptions()), StrictTranscript(StrictTool("lookup", "prefer"))).WaitAsync(Deadline);
            Equal(StopReason.Error, message.StopReason); Equal(1, endpoint.Payloads.Count);
        }
        // The fallback applies to its own request only: the next request is strict again.
        {
            var endpoint = new StrictEndpoint(attempt => attempt == 1 ? StrictRejection() : StrictSuccess());
            var transport = StrictKeyRoute(endpoint, StrictOptions()); var transcript = StrictTranscript(StrictTool("lookup", "prefer"));
            await StrictRun(transport, transcript).WaitAsync(Deadline); await StrictRun(transport, transcript).WaitAsync(Deadline);
            Equal("true,,true", string.Join(",", endpoint.Payloads.Select(payload => Strictness(StrictTools(payload)[0]))));
        }
    }
}
