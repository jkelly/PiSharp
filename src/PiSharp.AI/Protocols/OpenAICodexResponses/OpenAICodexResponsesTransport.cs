// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openai-codex-responses.ts (stream over SSE with the zstd
// request body, streamSimple, buildRequestBody, resolveCodexUrl, mapCodexEvents, normalizeCodexStatus, parseErrorResponse,
// extractAccountId, buildSSEHeaders, the retry helpers and service tier pricing; the WebSocket transport is in the .WebSocket part) and
// providers/openai-codex.ts. Event processing reuses the native Responses state
// machine (openai-responses-shared.ts processResponsesStream).
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICodexResponses;

/// <summary>OpenAICodexResponsesOptions plus the StreamOptions the port uses.</summary>
public sealed record OpenAICodexResponsesOptions
{
    /// <summary>"auto" (default), "none" or "required".</summary>
    public string? ToolChoice { get; init; }
    public string? ReasoningSummary { get; init; }
    public string? ServiceTier { get; init; }
    /// <summary>"low" (default), "medium" or "high".</summary>
    public string? TextVerbosity { get; init; }
    public double? Temperature { get; init; }
    /// <summary>The session id: prompt_cache_key and the session-id/x-client-request-id headers, unless caching is off.</summary>
    public string? SessionId { get; init; }
    public string? CacheRetention { get; init; }
    public ImmutableDictionary<string, string?>? Headers { get; init; }
    /// <summary>Model catalog headers (applied before caller headers).</summary>
    public ImmutableDictionary<string, string>? ModelHeaders { get; init; }
    public int MaxRetries { get; init; }
    public double? MaxRetryDelayMilliseconds { get; init; }
    /// <summary>Response-header timeout (timeoutMs); null waits indefinitely.</summary>
    public TimeSpan? Timeout { get; init; }
    public string UserAgent { get; init; } = "pi/1.1.0";
    public string Originator { get; init; } = "pi";
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public TimeProvider? Time { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask<JsonData?>>? OnPayload { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnResponse { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnProviderStreamEvent { get; init; }
    public int MaximumMessages { get; init; } = PiRequestBudget.RequestMessages;
    /// <summary>Pi has no request-size cap; the default admits image payloads of several megabytes.</summary>
    public int MaximumEntryCharacters { get; init; } = 64 * 1_048_576;
    /// <summary>StreamOptions.transport, read for every request (the setting can change mid-session): "sse", "websocket",
    /// "websocket-cached" or "auto" (null or empty, the default). Every value but "sse" tries the WebSocket first.</summary>
    public Func<string?>? Transport { get; init; }
    /// <summary>StreamOptions.websocketConnectTimeoutMs: the WebSocket open handshake's limit (default 15 s; zero waits indefinitely).</summary>
    public TimeSpan? WebSocketConnectTimeout { get; init; }
    /// <summary>Sends the WebSocket handshake (the provider's HTTP handler, so proxies and test seams apply); null uses the transport's client.</summary>
    public HttpMessageInvoker? WebSocketInvoker { get; init; }
}

public sealed class CodexApiException(string message, string? code = null) : Exception(message) { public string? Code { get; } = code; }

/// <summary>Source CodexProtocolError: an unparseable Codex event (never retried over SSE).</summary>
public sealed class CodexProtocolException(string message) : InvalidOperationException(message);

/// <summary>Source ProviderStreamEventCallbackError: the onProviderStreamEvent hook failed (kept out of the WebSocket retry and SSE
/// fallback path).</summary>
public sealed class ProviderStreamEventCallbackException(Exception inner) : Exception(inner.Message, inner);

/// <summary>
/// OpenAI Codex Responses (ChatGPT backend) over SSE. The access token comes from <paramref name="accessToken"/> for every request
/// (a refreshed OAuth credential), and its chatgpt_account_id claim travels as the chatgpt-account-id header. Borrows the client.
/// </summary>
public sealed partial class OpenAICodexResponsesTransport : IChatTransport, IThinkingLevelTransport
{
    private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";
    private const string JwtClaimPath = "https://api.openai.com/auth";
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "completed", "incomplete", "failed", "cancelled", "queued", "in_progress" };
    private readonly HttpClient _client;
    private readonly ModelDescriptor _model;
    private readonly JsonElement _metadata;
    private readonly OpenAICodexResponsesOptions _options;
    private readonly Func<CancellationToken, ValueTask<string>> _accessToken;
    private readonly ImmutableArray<string> _levels;
    private readonly string? _defaultLevel;
    private readonly ResponsesTokenRates _rates;
    private readonly bool _grammar, _strictMode, _midConversation, _developerRole;

    public OpenAICodexResponsesTransport(HttpClient client, ModelDescriptor model, JsonData modelMetadata, OpenAICodexResponsesOptions options,
        Func<CancellationToken, ValueTask<string>> accessToken, string? defaultThinkingLevel = null)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(modelMetadata);
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(accessToken);
        if (model.Api != "openai-codex-responses" || modelMetadata.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Unsupported Codex model selection.");
        _client = client; _model = model; _metadata = modelMetadata.Value.Clone(); _options = options; _accessToken = accessToken;
        _levels = ProviderTranscript.SupportedThinkingLevels(_metadata);
        if (defaultThinkingLevel is not null && !_levels.Contains(defaultThinkingLevel)) throw new ArgumentException("Unsupported Codex thinking level.");
        _defaultLevel = defaultThinkingLevel;
        var (input, output, cacheRead, cacheWrite, tiers) = _metadata.TryGetProperty("cost", out var cost) ? PromptLengthPricing.ReadCost(cost) : (0, 0, 0, 0, []);
        _rates = new(input, output, cacheRead, cacheWrite) { Tiers = [.. tiers.Select(tier => new ResponsesTokenRateTier(tier.InputTokensAbove, tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite))] };
        bool Compat(string name, bool fallback) => _metadata.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object &&
            compat.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : fallback;
        _grammar = Compat("supportsOpenAIGrammarTools", false); _strictMode = Compat("supportsStrictMode", true);
        _midConversation = Compat("supportsMidConvoSystemMessages", false); _developerRole = Compat("supportsDeveloperRole", true);
    }

    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) => model == _model ? _levels : throw new ArgumentException("Unknown Codex model.");

    /// <summary>resolveCodexUrl: baseUrl (default chatgpt.com/backend-api) ending in /codex/responses.</summary>
    public static string ResolveUrl(string? baseUrl)
    {
        var raw = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl;
        var normalized = raw.TrimEnd('/');
        if (normalized.EndsWith("/codex/responses", StringComparison.Ordinal)) return normalized;
        if (normalized.EndsWith("/codex", StringComparison.Ordinal)) return normalized + "/responses";
        return normalized + "/codex/responses";
    }

    /// <summary>extractAccountId: the JWT's https://api.openai.com/auth chatgpt_account_id claim.</summary>
    public static string ExtractAccountId(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new FormatException();
            var payload = JsonNode.Parse(Base64UrlText(parts[1])) as JsonObject;
            var account = payload?[JwtClaimPath]?["chatgpt_account_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(account)) throw new FormatException();
            return account;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
        { throw new InvalidOperationException("Failed to extract accountId from token"); }
    }

    /// <summary>atob tolerates base64url segments in practice only after padding; JWT segments are base64url.</summary>
    internal static string Base64UrlText(string segment)
    {
        var text = segment.Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        return Encoding.Latin1.GetString(Convert.FromBase64String(text));
    }

    /// <summary>The transcript and projection budget (several images of ~4.5 MB each).</summary>
    private int Budget => (int)Math.Min(int.MaxValue / 2, Math.Max(_options.MaximumEntryCharacters, 1_048_576) * 2L);

    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 2 * JsonData.MaximumDepth };

    /// <summary>convertResponsesMessages options for Codex: no system prompt, Codex tool-call providers.</summary>
    private ResponsesTranscriptProjectionOptions ProjectionOptions() => new(
            Reasoning: _metadata.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.True,
            SupportsDeveloperRole: _developerRole, SupportsMidConversationSystemMessages: _midConversation, IncludeInitialSystemPrompt: false,
            MaximumMessages: _options.MaximumMessages, MaximumEntryCharacters: _options.MaximumEntryCharacters,
            MaximumInputCharacters: Budget, MaximumOutputCharacters: Budget,
            ToolDeclarations: new(SupportsStrictMode: _strictMode, Strict: null, MaximumMessages: _options.MaximumMessages,
                MaximumEntryCharacters: _options.MaximumEntryCharacters, MaximumInputCharacters: Budget,
                MaximumOutputCharacters: Budget, MaximumOutputBytes: Budget) { SupportsOpenAIGrammarTools = _grammar })
        {
            ModelSupportsImages = _metadata.TryGetProperty("input", out var inputs) && inputs.ValueKind == JsonValueKind.Array &&
                inputs.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "image")
        };

    /// <summary>buildRequestBody for the request's thinking level (streamSimple clamps it; "off" omits reasoningEffort).</summary>
    public JsonObject BuildBody(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var level = request.ThinkingLevel ?? _defaultLevel;
        string? effort = level is null ? null : ProviderTranscript.ClampThinkingLevel(_metadata, level);
        if (effort == "off") effort = null;
        var messages = ProviderTranscript.Parse(request.Messages);
        var resolved = _midConversation ? messages : ProviderTranscript.CollapseSystemMessages(messages);
        var initial = resolved.Count > 0 && ProviderTranscript.Role(resolved[0]) == "system" ? resolved[0] : null;
        var instructions = initial is null ? "" : ProviderTranscript.SystemText(initial);
        var projection = ProjectionOptions();
        var projector = new ResponsesTranscriptProjector(projection);
        var input = JsonNode.Parse(projector.ProjectInput(request, CancellationToken.None).ToString(), documentOptions: JsonData.DocumentOptions);
        var tools = JsonNode.Parse(projector.ProjectTools(request, CancellationToken.None).ToString(), documentOptions: JsonData.DocumentOptions) as JsonArray;
        var cacheKey = _options.CacheRetention == "none" ? null : ClampCacheKey(_options.SessionId ?? request.SessionId);
        var body = new JsonObject
        {
            ["model"] = _model.Id, ["store"] = false, ["stream"] = true,
            ["instructions"] = instructions.Length != 0 ? instructions : "You are a helpful assistant.",
            ["input"] = input,
            ["text"] = new JsonObject { ["verbosity"] = _options.TextVerbosity ?? "low" },
            ["include"] = new JsonArray("reasoning.encrypted_content")
        };
        if (cacheKey is not null) body["prompt_cache_key"] = cacheKey;
        body["tool_choice"] = _options.ToolChoice ?? "auto";
        body["parallel_tool_calls"] = true;
        if (_options.Temperature is { } temperature) body["temperature"] = temperature;
        if (_options.ServiceTier is { } tier) body["service_tier"] = tier;
        if (tools is { Count: > 0 }) body["tools"] = tools;
        var (offPresent, off) = ProviderTranscript.MappedLevel(_metadata, "off");
        if (effort is not null)
        {
            var (present, mapped) = ProviderTranscript.MappedLevel(_metadata, effort);
            body["reasoning"] = new JsonObject { ["effort"] = present && mapped is not null ? mapped : effort, ["summary"] = _options.ReasoningSummary ?? "auto" };
        }
        else if (projection.Reasoning && !(offPresent && off is null))
            body["reasoning"] = new JsonObject { ["effort"] = offPresent ? off : "none" };
        return body;
    }

    private static string? ClampCacheKey(string? key)
    {
        if (key is null) return null;
        var runes = key.EnumerateRunes().ToArray();
        return runes.Length <= 64 ? key : string.Concat(runes.Take(64).Select(rune => rune.ToString()));
    }

    /// <summary>buildSSEHeaders: originator and user agent defaults, model then caller headers (null deletes), then the bearer token,
    /// the account id, OpenAI-Beta, accept, content-type and the session headers.</summary>
    public List<KeyValuePair<string, string>> BuildHeaders(string token, string? sessionId = null)
    {
        var headers = BaseHeaders(token);
        Set(headers, "openai-beta", "responses=experimental"); Set(headers, "accept", "text/event-stream"); Set(headers, "content-type", "application/json");
        var session = _options.CacheRetention == "none" ? null : ClampCacheKey(_options.SessionId ?? sessionId);
        if (session is not null) { Set(headers, "session-id", session); Set(headers, "x-client-request-id", session); }
        return headers;
    }

    /// <summary>buildWebSocketHeaders: the base headers without accept, content-type and the caller's OpenAI-Beta, then the WebSocket
    /// beta and the request id as x-client-request-id and session-id. connectWebSocket's delete of "OpenAI-Beta" misses the lowercased
    /// key headersToRecord produces, so the beta header is sent.</summary>
    public List<KeyValuePair<string, string>> BuildWebSocketHeaders(string token, string requestId)
    {
        var headers = BaseHeaders(token);
        Set(headers, "accept", null); Set(headers, "content-type", null); Set(headers, "openai-beta", null);
        Set(headers, "openai-beta", OpenAIBetaResponsesWebSockets);
        Set(headers, "x-client-request-id", requestId); Set(headers, "session-id", requestId);
        return headers;
    }

    /// <summary>buildBaseCodexHeaders: originator and user agent defaults, model then caller headers (null deletes), then the bearer
    /// token and the account id.</summary>
    private List<KeyValuePair<string, string>> BaseHeaders(string token)
    {
        var accountId = ExtractAccountId(token);
        var headers = new List<KeyValuePair<string, string>>();
        Set(headers, "originator", _options.Originator); Set(headers, "user-agent", _options.UserAgent);
        foreach (var (name, value) in _options.ModelHeaders ?? ImmutableDictionary<string, string>.Empty) Set(headers, name, value);
        foreach (var (name, value) in _options.Headers ?? ImmutableDictionary<string, string?>.Empty) Set(headers, name, value);
        Set(headers, "authorization", "Bearer " + token); Set(headers, "chatgpt-account-id", accountId);
        return headers;
    }

    private static void Set(List<KeyValuePair<string, string>> headers, string name, string? value)
    {
        headers.RemoveAll(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (value is not null) headers.Add(new(name.ToLowerInvariant(), value));
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model != _model) throw new ArgumentException("Unknown Codex model.");
        if (request.ThinkingLevel is { } level && !_levels.Contains(level)) throw new ArgumentException("Unsupported Codex thinking level.");
        return StreamCore(request, cancellationToken);
    }

    /// <summary>One stream call: the end_turn flag, the transport diagnostics appended to the message, and the WebSocket lease and
    /// continuation state the call settles.</summary>
    private sealed class Invocation
    {
        internal bool? EndTurn;
        internal readonly List<JsonObject> Diagnostics = [];
        internal WebSocketLease? Lease;
        internal bool WebSocketStarted, UseCachedContext, RetriedMissingContinuation;
        internal JsonObject? FullBody;
        internal string? CacheSessionId, ConfiguredTransport;
        internal int RequestBytes;
    }

    private async IAsyncEnumerable<StreamEvent> StreamCore(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        var invocation = new Invocation();
        var inner = new ResponsesTextToolTransport((chat, cancellation, context) => PrepareAsync(chat, invocation, cancellation),
            new ResponsesTextToolOptions(MaximumEvents: int.MaxValue, MaximumEventCharacters: 16 * 1_048_576, MaximumInputCharacters: 64 * 1_048_576,
                MaximumContentSlots: int.MaxValue, MaximumContentCharacters: 16 * 1_048_576, MaximumJsonDepth: JsonData.MaximumDepth, Rates: _rates)
            { SupportsOpenAIGrammarTools = _grammar });
        var keep = false;
        try
        {
            await foreach (var frame in inner.StreamAsync(request, token).ConfigureAwait(false))
            {
                if (frame is StreamDone done)
                {
                    var message = done.Message;
                    if (invocation.EndTurn is { } endTurn)
                        message = message with { ExtraProperties = (message.ExtraProperties ?? JsonFields.Empty).Set("endTurn", JsonData.Parse(endTurn ? "true" : "false")) };
                    // processWebSocketStream: a completed response keeps the connection; the cached context remembers it for the next delta.
                    if (invocation.Lease is { } lease && !token.IsCancellationRequested)
                    {
                        keep = true;
                        if (invocation.UseCachedContext && lease.Entry is { } entry && ResponseId(message) is { } responseId)
                            entry.Continuation = new(invocation.FullBody!, responseId, ResponseItems(message));
                    }
                    yield return done with { Message = WithDiagnostics(message, invocation) };
                    continue;
                }
                if (frame is StreamError error)
                {
                    // An error after the WebSocket stream started is a failure of this transport (no SSE retry once events were emitted).
                    if (invocation.Lease is not null && invocation.WebSocketStarted && !token.IsCancellationRequested &&
                        error.NativeSourceException is { } failure && !IsNonTransport(failure))
                    {
                        AppendDiagnostic(invocation, failure, started: true);
                        OpenAICodexWebSockets.RecordFailure(invocation.CacheSessionId, failure);
                    }
                    yield return error with { Message = WithDiagnostics(error.Message, invocation) };
                    continue;
                }
                yield return frame;
            }
        }
        finally
        {
            if (invocation.Lease is { } lease)
            {
                if (!keep && lease.Entry is { } entry) entry.Continuation = null;
                lease.Release(keep);
            }
        }
    }

    private static string? ResponseId(AssistantMessage message) =>
        message.ExtraProperties is { } properties && properties.Values.TryGetValue("responseId", out var id) && id.Value.ValueKind == JsonValueKind.String ? id.Value.GetString() : null;

    private static AssistantMessage WithDiagnostics(AssistantMessage message, Invocation invocation)
    {
        if (invocation.Diagnostics.Count == 0) return message;
        var existing = message.ExtraProperties is { } properties && properties.Values.TryGetValue("diagnostics", out var value) && JsonNode.Parse(value.ToString()) is JsonArray array
            ? array : new JsonArray();
        foreach (var diagnostic in invocation.Diagnostics) existing.Add(diagnostic.DeepClone());
        return message with { ExtraProperties = (message.ExtraProperties ?? JsonFields.Empty).Set("diagnostics", JsonData.Parse(existing.ToJsonString())) };
    }

    private async ValueTask<IAsyncEnumerator<JsonData>> PrepareAsync(ChatRequest request, Invocation invocation, CancellationToken token)
    {
        var token0 = await _accessToken(token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token0)) throw new InvalidOperationException($"No API key for provider: {_model.Provider}");
        var accountId = ExtractAccountId(token0);
        var headers = BuildHeaders(token0, request.SessionId);
        var body = BuildBody(request);
        if (_options.OnPayload is { } hook && await hook(JsonData.Parse(body.ToJsonString(BodyJson)), _model, token).ConfigureAwait(false) is { } replaced)
            body = JsonNode.Parse(replaced.ToString(), documentOptions: JsonData.DocumentOptions) as JsonObject ?? throw new InvalidOperationException("Codex payload must be an object.");
        var bodyJson = body.ToJsonString(BodyJson);
        var transport = _options.Transport?.Invoke() is { Length: > 0 } configured ? configured : "auto";
        var cacheSessionId = _options.CacheRetention == "none" ? null : _options.SessionId ?? request.SessionId;
        invocation.CacheSessionId = cacheSessionId; invocation.ConfiguredTransport = transport;
        invocation.RequestBytes = Encoding.UTF8.GetByteCount(bodyJson);
        if (transport != "sse")
        {
            if (OpenAICodexWebSockets.IsFallbackActive(cacheSessionId)) OpenAICodexWebSockets.RecordSseFallback(cacheSessionId);
            else if (await TryWebSocketAsync(body, token0, accountId, cacheSessionId, transport, invocation, token).ConfigureAwait(false) is { } events)
                return RetryMissingContinuationAsync(events, invocation, async () =>
                    await TryWebSocketAsync(body, token0, accountId, cacheSessionId, transport, invocation, token).ConfigureAwait(false)
                    ?? await SendSseAsync(headers, bodyJson, invocation, token).ConfigureAwait(false), token).GetAsyncEnumerator(token);
        }
        return await SendSseAsync(headers, bodyJson, invocation, token).ConfigureAwait(false);
    }

    /// <summary>The SSE request with retries; the response's events.</summary>
    private async ValueTask<IAsyncEnumerator<JsonData>> SendSseAsync(List<KeyValuePair<string, string>> headers, string bodyJson, Invocation invocation,
        CancellationToken token)
    {
        // compressRequestBodyZstd: the SSE body is a zstd frame (Content-Encoding: zstd); the WebSocket frame above stays plain JSON.
        var bytes = Encoding.UTF8.GetBytes(bodyJson);
        if (CompressBody(bytes) is { } compressed) { bytes = compressed; headers.Add(new("content-encoding", "zstd")); }
        var response = await SendAsync(headers, bytes, token).ConfigureAwait(false);
        try
        {
            if (_options.OnResponse is { } responseHook) await responseHook(Observation(response), _model, token).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return new OwnedEnumerator(ReadEventsAsync(stream, invocation, token), response, stream);
        }
        catch { response.Dispose(); throw; }
    }

    /// <summary>The zstd frame of the body (upstream: zstdCompressSync at level 3), checked by decoding it again; null sends the plain
    /// JSON, as upstream does when compression is unavailable or throws.</summary>
    internal static byte[]? CompressBody(byte[] body)
    {
        try
        {
            var frame = PiSharp.AI.Compression.Zstd.Compress(body);
            return PiSharp.AI.Compression.ZstdDecoder.Decompress(frame, body.Length).AsSpan().SequenceEqual(body) ? frame : null;
        }
        catch (Exception error) when (error is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException) { return null; }
    }

    private sealed class OwnedEnumerator(IAsyncEnumerable<JsonData> source, HttpResponseMessage response, Stream stream) : IAsyncEnumerator<JsonData>
    {
        private readonly IAsyncEnumerator<JsonData> _inner = source.GetAsyncEnumerator();
        public JsonData Current => _inner.Current;
        public ValueTask<bool> MoveNextAsync() => _inner.MoveNextAsync();
        public async ValueTask DisposeAsync()
        {
            try { await _inner.DisposeAsync().ConfigureAwait(false); }
            finally { await stream.DisposeAsync().ConfigureAwait(false); response.Dispose(); }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(List<KeyValuePair<string, string>> headers, byte[] body, CancellationToken token)
    {
        var url = ResolveUrl(_metadata.TryGetProperty("baseUrl", out var baseUrl) && baseUrl.ValueKind == JsonValueKind.String ? baseUrl.GetString() : null);
        var delay = _options.Delay ?? ((wait, cancellation) => Task.Delay(wait, _options.Time ?? TimeProvider.System, cancellation));
        Exception? last = null;
        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body), Version = HttpVersion.Version11 };
                foreach (var (name, value) in headers)
                    if (name is "content-type" or "content-encoding") request.Content.Headers.TryAddWithoutValidation(name, value);
                    else request.Headers.TryAddWithoutValidation(name, value);
                using var timeout = _options.Timeout is { } limit && limit > TimeSpan.Zero ? new CancellationTokenSource(limit, _options.Time ?? TimeProvider.System) : null;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout?.Token ?? CancellationToken.None);
                try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true && !token.IsCancellationRequested)
                { throw new InvalidOperationException($"Codex SSE response headers timed out after {(long)_options.Timeout!.Value.TotalMilliseconds}ms"); }
                if (response.IsSuccessStatusCode) return response;
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (attempt < _options.MaxRetries && IsRetryable((int)response.StatusCode, text))
                {
                    var retryAfter = RetryAfter(response);
                    var wait = retryAfter is null ? 1000 * Math.Pow(2, attempt) : ValidateDelay(retryAfter.Value);
                    response.Dispose(); response = null;
                    await delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
                    continue;
                }
                var (message, friendly) = ParseError(text, response.ReasonPhrase, (int)response.StatusCode, _options.Time ?? TimeProvider.System);
                response.Dispose(); response = null;
                throw new CodexHttpException(friendly ?? message);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { response?.Dispose(); throw new OperationCanceledException("Request was aborted", token); }
            catch (Exception error)
            {
                response?.Dispose();
                last = error;
                if (attempt < _options.MaxRetries && error is not RetryDelayExceeded && !error.Message.Contains("usage limit", StringComparison.Ordinal))
                {
                    await delay(TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt)), token).ConfigureAwait(false);
                    continue;
                }
                throw;
            }
        }
        throw last ?? new InvalidOperationException("Failed after retries");
    }

    private sealed class CodexHttpException(string message) : Exception(message);
    private sealed class RetryDelayExceeded(string message) : Exception(message);

    private double ValidateDelay(double delay)
    {
        var maximum = _options.MaxRetryDelayMilliseconds ?? 60_000;
        if (maximum > 0 && delay > maximum)
            throw new RetryDelayExceeded($"Server requested {Math.Ceiling(delay / 1000)}s retry delay (max: {Math.Ceiling(maximum / 1000)}s)");
        return delay;
    }

    private double? RetryAfter(HttpResponseMessage response)
    {
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        if (Header("retry-after-ms") is { } ms && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var millis)) return Math.Max(0, millis);
        if (Header("retry-after") is not { } after) return null;
        if (double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return Math.Max(0, seconds * 1000);
        if (DateTimeOffset.TryParse(after, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return Math.Max(0, (date - (_options.Time ?? TimeProvider.System).GetUtcNow()).TotalMilliseconds);
        return null;
    }

    [GeneratedRegex("GoUsageLimitError|FreeUsageLimitError|Monthly usage limit reached|available balance|insufficient_quota|out of budget|quota exceeded|billing", RegexOptions.IgnoreCase)]
    private static partial Regex TerminalRateLimit();
    [GeneratedRegex("rate.?limit|overloaded|service.?unavailable|upstream.?connect|connection.?refused", RegexOptions.IgnoreCase)]
    private static partial Regex RetryableText();
    [GeneratedRegex("usage_limit_reached|usage_not_included|rate_limit_exceeded", RegexOptions.IgnoreCase)]
    private static partial Regex UsageLimitCode();

    internal static bool IsRetryable(int status, string text)
    {
        if (status == 429 && TerminalRateLimit().IsMatch(text)) return false;
        if (status is 429 or 500 or 502 or 503 or 504) return true;
        return RetryableText().IsMatch(text);
    }

    /// <summary>parseErrorResponse: the error message, and a friendly ChatGPT usage-limit message for 429 or usage-limit codes.</summary>
    public static (string Message, string? Friendly) ParseError(string raw, string? statusText, int status, TimeProvider time)
    {
        var message = raw.Length != 0 ? raw : !string.IsNullOrEmpty(statusText) ? statusText : "Request failed";
        string? friendly = null;
        try
        {
            if (JsonNode.Parse(raw, documentOptions: JsonData.DocumentOptions) is JsonObject parsed && parsed["error"] is JsonObject error)
            {
                string? Text(string name) => error[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                var code = Text("code") is { Length: > 0 } c ? c : Text("type") ?? "";
                if (UsageLimitCode().IsMatch(code) || status == 429)
                {
                    var plan = Text("plan_type") is { Length: > 0 } planType ? $" ({planType.ToLowerInvariant()} plan)" : "";
                    var when = error["resets_at"] is JsonValue resets && resets.TryGetValue<double>(out var at) && at != 0
                        ? $" Try again in ~{Math.Max(0, Math.Round((at * 1000 - time.GetUtcNow().ToUnixTimeMilliseconds()) / 60000, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)} min." : "";
                    friendly = $"You have hit your ChatGPT usage limit{plan}.{when}".Trim();
                }
                message = Text("message") is { Length: > 0 } text ? text : friendly ?? message;
            }
        }
        catch (JsonException) { }
        return (message, friendly);
    }

    private async IAsyncEnumerable<JsonData> ReadEventsAsync(Stream body, Invocation invocation, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var frame in new SseDecoder(new(RejectInvalidUtf8: true)).DecodeAsync(body, leaveOpen: true, token).ConfigureAwait(false))
        {
            var data = frame.Data.Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            JsonObject value;
            try { value = JsonNode.Parse(data, documentOptions: JsonData.DocumentOptions) as JsonObject ?? throw new JsonException("not an object"); }
            catch (JsonException error) { throw new CodexProtocolException("Invalid Codex SSE JSON: " + error.Message); }
            var (mapped, completed) = await MapEventAsync(value, invocation, token).ConfigureAwait(false);
            if (mapped is { } next) yield return next;
            if (completed) yield break;
        }
    }

    /// <summary>mapCodexEvents for one event: the onProviderStreamEvent hook, then "error" and "response.failed" as Codex API errors, the
    /// terminal events normalized to response.completed or response.incomplete (ending the stream), anything else passed on. Events
    /// without a type are dropped.</summary>
    private async ValueTask<(JsonData? Event, bool Completed)> MapEventAsync(JsonObject value, Invocation invocation, CancellationToken token)
    {
        if (_options.OnProviderStreamEvent is { } hook)
        {
            try { await hook(JsonData.Parse(value.ToJsonString()), _model, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { throw new ProviderStreamEventCallbackException(error); }
        }
        if (value["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type)) return (null, false);
        if (type == "error")
        {
            var nested = value["error"] as JsonObject;
            string? Field(string name) => value[name] is JsonValue own && own.TryGetValue<string>(out var text) ? text
                : nested?[name] is JsonValue inner && inner.TryGetValue<string>(out var innerText) ? innerText : null;
            var code = Field("code"); var message = Field("message");
            throw new CodexApiException("Codex error: " + (!string.IsNullOrEmpty(message) ? message : !string.IsNullOrEmpty(code) ? code : value.ToJsonString()), code);
        }
        if (type == "response.failed")
        {
            var error = value["response"]?["error"] as JsonObject;
            var message = error?["message"] is JsonValue text && text.TryGetValue<string>(out var failure) && failure.Length != 0 ? failure : "Codex response failed";
            throw new CodexApiException(message, error?["code"] is JsonValue codeValue && codeValue.TryGetValue<string>(out var errorCode) ? errorCode : null);
        }
        if (type is "response.done" or "response.completed" or "response.incomplete")
        {
            var response = value["response"] as JsonObject ?? new JsonObject();
            if (response["end_turn"] is JsonValue end && end.TryGetValue<bool>(out var endTurn)) invocation.EndTurn = endTurn;
            var status = response["status"] is JsonValue statusValue && statusValue.TryGetValue<string>(out var raw) && Statuses.Contains(raw) ? raw : null;
            // mapStopReason: failed/cancelled carry no message ("An unknown error occurred"); queued/in_progress/absent settle as stop.
            if (status is "failed" or "cancelled") throw new InvalidOperationException("An unknown error occurred");
            response["status"] = status == "incomplete" ? "incomplete" : "completed";
            // resolveCodexServiceTier and Codex pricing: "default" defers to a requested flex/priority tier; "fast" is unpriced.
            var responseTier = response["service_tier"] is JsonValue tierValue && tierValue.TryGetValue<string>(out var tier) ? tier : null;
            var resolvedTier = responseTier == "default" && _options.ServiceTier is "flex" or "priority" ? _options.ServiceTier : responseTier ?? _options.ServiceTier;
            response["service_tier"] = resolvedTier is "flex" or "priority" ? resolvedTier : "default";
            return (JsonData.Parse(new JsonObject { ["type"] = status == "incomplete" ? "response.incomplete" : "response.completed", ["response"] = response.DeepClone() }.ToJsonString()), true);
        }
        return (JsonData.Parse(value.ToJsonString()), false);
    }

    private static JsonData Observation(HttpResponseMessage response)
    {
        var headers = new JsonObject();
        foreach (var field in response.Headers.Concat(response.Content.Headers)) headers[field.Key.ToLowerInvariant()] = string.Join(", ", field.Value);
        return JsonData.Parse(new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = headers }.ToJsonString());
    }
}
