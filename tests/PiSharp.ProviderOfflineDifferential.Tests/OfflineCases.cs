using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal sealed record GoldenCase(string Name, string Source, string Scope, JsonData Expected, Func<Task<JsonData>> Run);
internal static class OfflineCases
{
    internal static readonly List<OriginalTaskRecord> Originals = [];
    private static readonly ModelDescriptor ResponsesModel = new("fixture", "openai-responses", "openai");
    private static readonly ModelDescriptor MistralModel = new("fixture", "mistral-conversations", "mistral");
    private const string ResponsesDone = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
    private const string MistralDone = "data: {\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{}}]}\n\n";
    private const string ResponsesSource = "api/openai-responses.ts buildParams/createClient/getCompat";
    private const string MistralSource = "api/mistral-conversations.ts buildMistralHeaders/toMistralWirePayload/consumeChatStream";
    private static JsonData Json(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static string? Field(JsonData? value, string field) => value is not null && value.Value.TryGetProperty(field, out var result) ? result.GetRawText() : null;
    private static string? Header(Observation value, string name) => value.Headers.TryGetValue(name, out var result) ? result : null;
    private static string? Error(StreamTerminalEvent? terminal) => terminal?.Message.ExtraProperties is { } fields && fields.TryGet("errorMessage", out var value) ? value?.Value.GetString() : null;
    private static ChatRequest MistralRequest => new(MistralModel, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"hello\",\"timestamp\":1}"))]);
    private static JsonData Metadata(bool mistral = false, JsonObject? extras = null)
    {
        var model = mistral ? MistralModel : ResponsesModel;
        var row = new JsonObject { ["id"] = model.Id, ["provider"] = model.Provider, ["api"] = model.Api,
            ["reasoning"] = false, ["maxTokens"] = 100, ["contextWindow"] = 8192, ["input"] = new JsonArray("text"),
            ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 } };
        if (extras is not null) foreach (var pair in extras) row[pair.Key] = pair.Value?.DeepClone();
        return JsonData.Parse(row.ToJsonString());
    }
    private static ChatRequest ToolRequest(bool grammar)
    {
        var tool = new JsonObject { ["name"] = "echo", ["description"] = "echo", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } };
        if (grammar) tool["constrainedSampling"] = new JsonObject { ["type"] = "grammar", ["variants"] = new JsonObject { ["openai_regex"] = "[a-z]+" } };
        var body = new JsonObject { ["role"] = "system", ["content"] = "", ["toolsAdded"] = new JsonArray(tool) };
        return new(ResponsesModel, [new("system", JsonData.Parse(body.ToJsonString()))]);
    }
    internal static readonly GoldenCase[] All = [
        new("responses-default-max-output-support", ResponsesSource, "default/disabled capability and bounded token controls", Json(new { sent = true, max = "17", clamped = "16", disabledSent = true, disabledMax = (string?)null, resourceBlocked = true, invalidCompatBlocked = true }), async () => {
            var value = await Responses(new(MaxOutputTokens: 17));
            var clamped = await Responses(new(MaxOutputTokens: 1));
            var disabled = await Responses(new(MaxOutputTokens: 17), Metadata(extras: new() { ["compat"] = new JsonObject { ["supportsMaxOutputTokens"] = false } }));
            var resource = await Responses(new(MaxOutputTokens: 1_000_001));
            var invalid = await Responses(new(), Metadata(extras: new() { ["compat"] = new JsonObject { ["supportsMaxOutputTokens"] = "invalid" } }));
            return Json(new { sent = value.Payload is not null, max = Field(value.Payload, "max_output_tokens"), clamped = Field(clamped.Payload, "max_output_tokens"),
                disabledSent = disabled.Payload is not null, disabledMax = Field(disabled.Payload, "max_output_tokens"), resourceBlocked = resource.Payload is null, invalidCompatBlocked = invalid.Payload is null }); }),
        new("responses-default-strict-field-omitted", ResponsesSource, "default omission and explicit supported strict capability", Json(new { sent = true, strictField = false, supportedSent = true, supportedStrict = "false" }), async () => {
            var value = await Responses(new(), request: ToolRequest(false));
            var supported = await Responses(new(), Metadata(extras: new() { ["compat"] = new JsonObject { ["supportsStrictMode"] = true } }), ToolRequest(false));
            return Json(new { sent = value.Payload is not null, strictField = value.Payload?.Value.GetProperty("tools")[0].TryGetProperty("strict", out _) == true,
                supportedSent = supported.Payload is not null, supportedStrict = supported.Payload?.Value.GetProperty("tools")[0].GetProperty("strict").GetRawText() }); }),
        new("responses-unsupported-grammar-falls-back-function", "api/constrained-sampling.ts resolveGrammarConstrainedSampling; api/openai-responses-shared.ts convertResponsesTools", "required default grammar fallback", Json(new { sent = true, kind = "function", stop = "Stop" }), async () => {
            var value = await Responses(new(), request: ToolRequest(true)); return Json(new { sent = value.Payload is not null,
                kind = value.Payload?.Value.GetProperty("tools")[0].GetProperty("type").GetString(), stop = value.Stop }); }),
        new("responses-model-header-forwarding", ResponsesSource, "model/request precedence, null deletion and bounded header control", Json(new { probe = "model", agent = "fixture-original", deleted = (string?)null, requestAgent = "request", invalidBlocked = true }), async () => {
            var value = await Responses(new(), Metadata(extras: new() { ["headers"] = new JsonObject { ["x-probe"] = "model", ["User-Agent"] = "fixture-original" } }));
            var overridden = await Responses(new() { Headers = JsonData.Parse("{\"x-probe\":null,\"User-Agent\":\"request\"}") },
                Metadata(extras: new() { ["headers"] = new JsonObject { ["x-probe"] = "model", ["User-Agent"] = "fixture-original" } }));
            var invalid = await Responses(new() { Headers = JsonData.Parse("{\"x-probe\":\"bad\\nvalue\"}") });
            return Json(new { probe = Header(value, "x-probe"), agent = Header(value, "user-agent"), deleted = Header(overridden, "x-probe"),
                requestAgent = Header(overridden, "user-agent"), invalidBlocked = invalid.Payload is null }); }),
        new("responses-cache-session-affinity-headers", ResponsesSource, "session affinity independent of cache and request null override", Json(new { cache = "\"fixture-session\"", session = "fixture-session", requestId = "fixture-session", noCache = (string?)null, noCacheSession = "fixture-session", deletedSession = (string?)null, deletedRequest = (string?)null }), async () => {
            var value = await Responses(new(SessionId: "fixture-session"));
            var noCache = await Responses(new(SessionId: "fixture-session", CacheRetention: "none"));
            var deleted = await Responses(new(SessionId: "fixture-session") { Headers = JsonData.Parse("{\"session_id\":null,\"x-client-request-id\":null}") });
            return Json(new { cache = Field(value.Payload, "prompt_cache_key"), session = Header(value, "session_id"), requestId = Header(value, "x-client-request-id"),
                noCache = Field(noCache.Payload, "prompt_cache_key"), noCacheSession = Header(noCache, "session_id"), deletedSession = Header(deleted, "session_id"), deletedRequest = Header(deleted, "x-client-request-id") }); }),
        new("responses-model-sampling-overrides-named-field", ResponsesSource, "named/model/request merge, hook ordering and bounded JSON control", Json(new { temperature = "0.5", extension = "true", requestTemperature = "0.75", requestExtension = "null", hookTemperature = "0.75", resourceBlocked = true }), async () => {
            var value = await Responses(new(Temperature: 0.25), Metadata(extras: new() { ["samplingParams"] = new JsonObject { ["temperature"] = 0.5, ["fixture_sampling"] = true } }));
            string? hookTemperature = null;
            var request = await Responses(new(Temperature: 0.25) { SamplingParams = JsonData.Parse("{\"temperature\":0.75,\"fixture_sampling\":null}"),
                OnPayload = (payload, _, _) => { hookTemperature = Field(payload, "temperature"); return ValueTask.FromResult<JsonData?>(null); } },
                Metadata(extras: new() { ["samplingParams"] = new JsonObject { ["temperature"] = 0.5, ["fixture_sampling"] = true } }));
            var resource = await Responses(new() { SamplingParams = JsonData.Parse("{\"max_output_tokens\":1000001}") });
            return Json(new { temperature = Field(value.Payload, "temperature"), extension = Field(value.Payload, "fixture_sampling"), requestTemperature = Field(request.Payload, "temperature"),
                requestExtension = Field(request.Payload, "fixture_sampling"), hookTemperature, resourceBlocked = resource.Payload is null }); }),
        new("responses-http-error-ascii-and-hook-order", "utils/error-body.ts status/body normalization; api/openai-responses.ts response hook after successful acquisition", "normalization contract, not every SDK field shape", Json(new { stop = "Error", starts = 0, responseHooks = 0, error = "OpenAI API error (503): denied" }), async () => ErrorView(await Responses(new(), status: HttpStatusCode.ServiceUnavailable, body: " denied "))),
        new("responses-http-error-bom-trim", "utils/error-body.ts extractBody .trim()", "status/body normalization contract; SDK emission shape not claimed", Json(new { stop = "Error", starts = 0, responseHooks = 0, error = "OpenAI API error (503): denied" }), async () => ErrorView(await Responses(new(), status: HttpStatusCode.ServiceUnavailable, body: "\uFEFFdenied\uFEFF"))),
        new("responses-http-error-nel-retained", "utils/error-body.ts extractBody .trim()", "status/body normalization contract; SDK emission shape not claimed", Json(new { stop = "Error", starts = 0, responseHooks = 0, error = "OpenAI API error (503): \u0085denied\u0085" }), async () => ErrorView(await Responses(new(), status: HttpStatusCode.ServiceUnavailable, body: "\u0085denied\u0085"))),
        new("responses-http-error-all-ecma-whitespace", "utils/error-body.ts extractBody .trim() ECMAScript whitespace/line terminators", "status/body normalization contract", Json(new { stop = "Error", starts = 0, responseHooks = 0, error = "OpenAI API error (503): denied" }), async () => ErrorView(await Responses(new(), status: HttpStatusCode.ServiceUnavailable, body: EcmaSpace + "denied" + EcmaSpace))),
        new("responses-http-error-180e-retained", "utils/error-body.ts extractBody .trim() excludes180E", "status/body normalization contract", Json(new { stop = "Error", starts = 0, responseHooks = 0, error = "OpenAI API error (503): \u180Edenied\u180E" }), async () => ErrorView(await Responses(new(), status: HttpStatusCode.ServiceUnavailable, body: "\u180Edenied\u180E"))),
        new("responses-http-error-200b-retained", "utils/error-body.ts extractBody .trim() excludes200B", "status/body normalization contract", Json(new { stop = "Error", starts = 0, responseHooks = 0, error = "OpenAI API error (503): \u200Bdenied\u200B" }), async () => ErrorView(await Responses(new(), status: HttpStatusCode.ServiceUnavailable, body: "\u200Bdenied\u200B"))),
        new("responses-response-callback-original-error", "api/openai-responses.ts await onResponse and catch normalizeProviderError", "error hook control", Json(new { stop = "Error", starts = 0, responseHooks = 1, error = "offline callback failure" }), async () => ErrorView(await Responses(new(), callbackFailure: true))),
        new("mistral-direct-header-null-precedence", MistralSource, "implemented header control", Json(new { probe = "request", removed = (string?)null, affinity = (string?)null }), async () => HeaderView(await Mistral(false))),
        new("mistral-simple-header-null-precedence", MistralSource, "public Simple header control", Json(new { probe = "request", removed = (string?)null, affinity = (string?)null }), async () => HeaderView(await Mistral(true))),
        new("mistral-camel-null-wins-wire-value", MistralSource, "implemented hook remapping control", Json(new { topP = "null", camel = (string?)null, stop = "Stop" }), async () => {
            var value = await Mistral(false, replace: payload => { var root = JsonNode.Parse(payload.ToString())!.AsObject(); root["top_p"] = 0.9; root["topP"] = null; return JsonData.Parse(root.ToJsonString()); });
            return Json(new { topP = Field(value.Payload, "top_p"), camel = Field(value.Payload, "topP"), stop = value.Stop }); }),
        new("mistral-http-error-bom-and-response-hook", "api/mistral-conversations.ts requestMistralStream/formatMistralError .trim()", "implemented error control", Json(new { stop = "Error", starts = 0, responseHooks = 1, error = "Mistral API error (503): denied" }), async () => ErrorView(await Mistral(false, status: HttpStatusCode.ServiceUnavailable, body: "\uFEFFdenied\uFEFF"))),
        new("mistral-http-error-nel-retained", "api/mistral-conversations.ts formatMistralError .trim()", "implemented error control", Json(new { stop = "Error", starts = 0, responseHooks = 1, error = "Mistral API error (503): \u0085denied\u0085" }), async () => ErrorView(await Mistral(false, status: HttpStatusCode.ServiceUnavailable, body: "\u0085denied\u0085"))),
        new("mistral-response-callback-original-error", "api/mistral-conversations.ts requestMistralStream/formatMistralError", "documented bounded diagnostic difference", Json(new { stop = "Error", starts = 0, responseHooks = 1, error = "offline callback failure" }), async () => ErrorView(await Mistral(false, callbackFailure: true))),
        new("mistral-missing-text-item-is-empty", "api/mistral-conversations.ts consumeChatStream item.text ?? empty string", "valid optional stream text field", Json(new { stop = "Stop", rawHooks = 1, content = 0 }), async () => ContentView(await Mistral(false, body: EmptyItem("{\"type\":\"text\"}")))),
        new("mistral-null-text-item-is-empty", "api/mistral-conversations.ts consumeChatStream item.text ?? empty string", "nullish stream text field", Json(new { stop = "Stop", rawHooks = 1, content = 0 }), async () => ContentView(await Mistral(false, body: EmptyItem("{\"type\":\"text\",\"text\":null}")))),
        new("mistral-missing-thinking-item-is-empty", "api/mistral-conversations.ts consumeChatStream item.thinking ?? empty array", "valid optional stream thinking field", Json(new { stop = "Stop", rawHooks = 1, content = 0 }), async () => ContentView(await Mistral(false, body: EmptyItem("{\"type\":\"thinking\"}")))),
        new("mistral-null-thinking-item-is-empty", "api/mistral-conversations.ts consumeChatStream item.thinking ?? empty array", "nullish stream thinking field", Json(new { stop = "Stop", rawHooks = 1, content = 0 }), async () => ContentView(await Mistral(false, body: EmptyItem("{\"type\":\"thinking\",\"thinking\":null}")))),
        new("mistral-missing-thinking-text-is-empty", "api/mistral-conversations.ts thinking part.text ?? empty string", "valid optional thinking part text", Json(new { stop = "Stop", rawHooks = 1, content = 0 }), async () => ContentView(await Mistral(false, body: EmptyItem("{\"type\":\"thinking\",\"thinking\":[{}]}")))),
        new("mistral-null-thinking-text-is-empty", "api/mistral-conversations.ts thinking part.text ?? empty string", "nullish thinking part text", Json(new { stop = "Stop", rawHooks = 1, content = 0 }), async () => ContentView(await Mistral(false, body: EmptyItem("{\"type\":\"thinking\",\"thinking\":[{\"text\":null}]}")))),
        new("mistral-nonstring-text-still-errors-retained-partial", "api/mistral-conversations.ts sanitizeSurrogates requires .replace", "genuine malformed known text control", Json(new { stop = "Error", rawHooks = 2, content = 1, text = "partial" }), async () => MalformedView(await Mistral(false, body: MalformedItem("{\"type\":\"text\",\"text\":7}")))),
        new("mistral-nonarray-thinking-still-errors-retained-partial", "api/mistral-conversations.ts thinking uses .map", "genuine malformed known thinking control", Json(new { stop = "Error", rawHooks = 2, content = 1, text = "partial" }), async () => MalformedView(await Mistral(false, body: MalformedItem("{\"type\":\"thinking\",\"thinking\":\"bad\"}")))),
        new("mistral-null-thinking-part-still-errors-retained-partial", "api/mistral-conversations.ts dereferences part.text", "genuine malformed thinking part control", Json(new { stop = "Error", rawHooks = 2, content = 1, text = "partial" }), async () => MalformedView(await Mistral(false, body: MalformedItem("{\"type\":\"thinking\",\"thinking\":[null]}")))),
        new("mistral-null-content-item-still-errors-retained-partial", "api/mistral-conversations.ts dereferences item.type", "genuine malformed null item control", Json(new { stop = "Error", rawHooks = 2, content = 1, text = "partial" }), async () => MalformedView(await Mistral(false, body: MalformedItem("null")))),
        new("mistral-unknown-content-item-ignored", "api/mistral-conversations.ts consumeChatStream string/text/thinking branches", "concrete original ignored event", Json(new { stop = "Stop", rawHooks = 1, content = 0 }), async () => {
            var value = await Mistral(false, body: "data: {\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{\"content\":[{\"type\":\"fixture_extension\",\"text\":\"auxiliary\"}]}}]}\n\n");
            return Json(new { stop = value.Stop, rawHooks = value.RawHooks, content = value.Terminal?.Message.Content.Length ?? -1 }); })
    ];
    private const string EcmaSpace = "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";
    private static string EmptyItem(string item) => "data: {\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{\"content\":[" + item + "]}}]}\n\n";
    private static string MalformedItem(string item) => "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" + EmptyItem(item);
    private static JsonData ContentView(Observation value) => Json(new { stop = value.Stop, rawHooks = value.RawHooks, content = value.Terminal?.Message.Content.Length ?? -1 });
    private static JsonData MalformedView(Observation value) => Json(new { stop = value.Stop, rawHooks = value.RawHooks, content = value.Terminal?.Message.Content.Length ?? -1,
        text = value.Terminal is null ? "" : string.Concat(value.Terminal.Message.Content.OfType<TextContent>().Select(part => part.Text)) });
    private static JsonData ErrorView(Observation value) => Json(new { stop = value.Stop, starts = value.Starts, responseHooks = value.ResponseHooks, error = value.Error });
    private static JsonData HeaderView(Observation value) => Json(new { probe = Header(value, "x-probe"), removed = Header(value, "x-removed"), affinity = Header(value, "x-affinity") });

    private static async Task<Observation> Responses(ResponsesKeyAuthRequestOptions options, JsonData? metadata = null, ChatRequest? request = null,
        HttpStatusCode status = HttpStatusCode.OK, string body = ResponsesDone, bool callbackFailure = false)
    {
        var result = new Observation(); using var handler = new OfflineHandler(result, status, body);
        var callback = callbackFailure ? Task.FromException(new InvalidOperationException("offline callback failure")) : Task.CompletedTask;
        var callbackRecord = new OriginalTaskRecord("responses-response-callback-original" + (callbackFailure ? "-expected-fault" : ""), callback); Originals.Add(callbackRecord);
        NativeHttpModelProvider? provider = null;
        try
        {
            provider = NativeProviderFactory.CreateResponses(ResponsesModel, new("https://api.openai.com/v1/responses"), "offline-placeholder", new(false), options with {
                OnResponse = (_, _, _) => { result.ResponseHooks++; return new ValueTask(callback); },
                OnProviderStreamEvent = (_, _, _) => { result.RawHooks++; return ValueTask.CompletedTask; } }, handler, metadata);
            await Drain(provider, request ?? new(ResponsesModel, []), result);
        }
        catch (Exception error) { result.Error = error.Message; result.Stop = provider is null ? "ConstructionFailure" : "InvocationFailure"; }
        finally { await Join(callbackRecord); await handler.JoinOriginals(); provider?.Dispose(); }
        return result;
    }
    private static async Task<Observation> Mistral(bool simple, HttpStatusCode status = HttpStatusCode.OK, string body = MistralDone,
        bool callbackFailure = false, Func<JsonData, JsonData>? replace = null)
    {
        var result = new Observation(); using var handler = new OfflineHandler(result, status, body);
        var callback = callbackFailure ? Task.FromException(new InvalidOperationException("offline callback failure")) : Task.CompletedTask;
        var callbackRecord = new OriginalTaskRecord("mistral-response-callback-original" + (callbackFailure ? "-expected-fault" : ""), callback); Originals.Add(callbackRecord);
        var modelHeaders = new Dictionary<string, string?> { ["x-probe"] = "model", ["x-removed"] = "model", ["x-affinity"] = null }.ToImmutableDictionary();
        var options = new MistralTextOptions(new("https://api.mistral.ai/"), true, new(0, 0, 0, 0), "fixture-original") {
            SessionId = "fixture-session", ModelHeaders = modelHeaders,
            Headers = new Dictionary<string, string?> { ["x-probe"] = "request", ["x-removed"] = null }.ToImmutableDictionary(),
            OnPayload = (payload, _, _) => ValueTask.FromResult<JsonData?>(replace?.Invoke(payload)),
            OnResponse = (_, _, _) => { result.ResponseHooks++; return new ValueTask(callback); },
            OnProviderStreamEvent = (_, _, _) => { result.RawHooks++; return ValueTask.CompletedTask; } };
        NativeHttpModelProvider? provider = null;
        try
        {
            provider = simple ? NativeProviderFactory.CreateMistralSimple(MistralModel, options.BaseUrl, "offline-placeholder",
                Metadata(true, new() { ["headers"] = JsonSerializer.SerializeToNode(modelHeaders) }), options, handler)
                : NativeProviderFactory.CreateMistral(MistralModel, options.BaseUrl, "offline-placeholder", options, handler);
            await Drain(provider, MistralRequest, result);
        }
        catch (Exception error) { result.Error = error.Message; result.Stop = provider is null ? "ConstructionFailure" : "InvocationFailure"; }
        finally { await Join(callbackRecord); await handler.JoinOriginals(); provider?.Dispose(); }
        return result;
    }
    private static async Task Drain(IChatTransport transport, ChatRequest request, Observation result)
    {
        var record = new OriginalTaskRecord("offline-drain-original"); Originals.Add(record);
        async Task Read() { await foreach (var frame in transport.StreamAsync(request)) {
            if (frame is StreamStarted) result.Starts++;
            if (frame is StreamTerminalEvent terminal) { result.Terminal = terminal; result.Stop = terminal.Reason.ToString(); result.Error = Error(terminal); }
        } }
        try { var original = Read(); record.Original = original; await original; }
        catch (Exception error) { record.Direct = error; throw; } finally { record.Capture(); }
    }
    private static async Task Join(OriginalTaskRecord record)
    { if (record.Original is { } original) { try { await original; } catch (Exception error) { record.Direct = error; } finally { record.Capture(); } } }
    private sealed class Observation
    {
        internal JsonData? Payload; internal readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        internal StreamTerminalEvent? Terminal; internal string Stop = "MissingTerminal"; internal string? Error;
        internal int Starts, ResponseHooks, RawHooks;
    }
    private sealed class OfflineHandler(Observation observed, HttpStatusCode status, string body) : HttpMessageHandler
    {
        private readonly List<OriginalTaskRecord> sends = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var record = new OriginalTaskRecord("offline-handler-send-original"); sends.Add(record); Originals.Add(record);
            var original = ReadAndRespond(request, token); record.Original = original; return original;
        }
        private async Task<HttpResponseMessage> ReadAndRespond(HttpRequestMessage request, CancellationToken token)
        {
            observed.Payload = JsonData.Parse(await request.Content!.ReadAsStringAsync(token));
            // Explicit diagnostic allowlist: never capture Authorization or any credential field.
            foreach (var name in new[] { "x-probe", "x-removed", "x-affinity", "user-agent", "session_id", "x-client-request-id" })
                if (request.Headers.TryGetValues(name, out var values)) observed.Headers[name] = string.Join(", ", values);
            return new(status) { Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "text/event-stream" : "text/plain") };
        }
        internal async Task JoinOriginals() { foreach (var record in sends) await Join(record); }
    }
}
