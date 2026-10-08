// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openai-codex-responses.ts (stream over SSE, streamSimple,
// buildRequestBody, resolveCodexUrl, mapCodexEvents, normalizeCodexStatus, parseErrorResponse, extractAccountId, buildSSEHeaders,
// the retry helpers and service tier pricing) and providers/openai-codex.ts. Event processing reuses the native Responses state
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
    public int MaximumMessages { get; init; } = 1024;
    public int MaximumEntryCharacters { get; init; } = 1_048_576;
}

public sealed class CodexApiException(string message, string? code = null) : Exception(message) { public string? Code { get; } = code; }

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

    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
        var projection = new ResponsesTranscriptProjectionOptions(
            Reasoning: _metadata.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.True,
            SupportsDeveloperRole: _developerRole, SupportsMidConversationSystemMessages: _midConversation, IncludeInitialSystemPrompt: false,
            MaximumMessages: _options.MaximumMessages, MaximumEntryCharacters: _options.MaximumEntryCharacters,
            MaximumInputCharacters: Math.Max(_options.MaximumEntryCharacters, 1_048_576) * 4, MaximumOutputCharacters: 16 * 1_048_576,
            ToolDeclarations: new(SupportsStrictMode: _strictMode, Strict: null, MaximumMessages: _options.MaximumMessages,
                MaximumEntryCharacters: _options.MaximumEntryCharacters, MaximumInputCharacters: Math.Max(_options.MaximumEntryCharacters, 1_048_576) * 4,
                MaximumOutputCharacters: 16 * 1_048_576, MaximumOutputBytes: 16 * 1_048_576) { SupportsOpenAIGrammarTools = _grammar });
        var projector = new ResponsesTranscriptProjector(projection);
        var input = JsonNode.Parse(projector.ProjectInput(request, CancellationToken.None).ToString());
        var tools = JsonNode.Parse(projector.ProjectTools(request, CancellationToken.None).ToString()) as JsonArray;
        var cacheKey = _options.CacheRetention == "none" ? null : ClampCacheKey(_options.SessionId);
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
    public List<KeyValuePair<string, string>> BuildHeaders(string token)
    {
        var accountId = ExtractAccountId(token);
        var headers = new List<KeyValuePair<string, string>>();
        void Set(string name, string? value)
        {
            headers.RemoveAll(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (value is not null) headers.Add(new(name.ToLowerInvariant(), value));
        }
        Set("originator", _options.Originator); Set("user-agent", _options.UserAgent);
        foreach (var (name, value) in _options.ModelHeaders ?? ImmutableDictionary<string, string>.Empty) Set(name, value);
        foreach (var (name, value) in _options.Headers ?? ImmutableDictionary<string, string?>.Empty) Set(name, value);
        Set("authorization", "Bearer " + token); Set("chatgpt-account-id", accountId);
        Set("openai-beta", "responses=experimental"); Set("accept", "text/event-stream"); Set("content-type", "application/json");
        var session = _options.CacheRetention == "none" ? null : ClampCacheKey(_options.SessionId);
        if (session is not null) { Set("session-id", session); Set("x-client-request-id", session); }
        return headers;
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model != _model) throw new ArgumentException("Unknown Codex model.");
        if (request.ThinkingLevel is { } level && !_levels.Contains(level)) throw new ArgumentException("Unsupported Codex thinking level.");
        return StreamCore(request, cancellationToken);
    }

    private sealed class Invocation { internal bool? EndTurn; }

    private async IAsyncEnumerable<StreamEvent> StreamCore(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        var invocation = new Invocation();
        var inner = new ResponsesTextToolTransport((chat, cancellation, context) => PrepareAsync(chat, invocation, cancellation),
            new ResponsesTextToolOptions(MaximumEvents: 65_536, MaximumEventCharacters: 16 * 1_048_576, MaximumInputCharacters: 64 * 1_048_576,
                MaximumContentSlots: 256, MaximumContentCharacters: 16 * 1_048_576, MaximumJsonDepth: 64, Rates: _rates)
            { SupportsOpenAIGrammarTools = _grammar });
        await foreach (var frame in inner.StreamAsync(request, token).ConfigureAwait(false))
        {
            if (frame is StreamDone done && invocation.EndTurn is { } endTurn)
            {
                yield return done with { Message = done.Message with { ExtraProperties = (done.Message.ExtraProperties ?? JsonFields.Empty)
                    .Set("endTurn", JsonData.Parse(endTurn ? "true" : "false")) } };
                continue;
            }
            yield return frame;
        }
    }

    private async ValueTask<IAsyncEnumerator<JsonData>> PrepareAsync(ChatRequest request, Invocation invocation, CancellationToken token)
    {
        var token0 = await _accessToken(token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token0)) throw new InvalidOperationException($"No API key for provider: {_model.Provider}");
        var headers = BuildHeaders(token0);
        var body = BuildBody(request);
        if (_options.OnPayload is { } hook && await hook(JsonData.Parse(body.ToJsonString(BodyJson)), _model, token).ConfigureAwait(false) is { } replaced)
            body = JsonNode.Parse(replaced.ToString()) as JsonObject ?? throw new InvalidOperationException("Codex payload must be an object.");
        // The Codex backend accepts zstd bodies; .NET 10 ships no zstd encoder, so the uncompressed JSON is sent (upstream's
        // fallback when compression is unavailable).
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(BodyJson));
        var response = await SendAsync(headers, bytes, token).ConfigureAwait(false);
        try
        {
            if (_options.OnResponse is { } responseHook) await responseHook(Observation(response), _model, token).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return new OwnedEnumerator(ReadEventsAsync(stream, invocation, token), response, stream);
        }
        catch { response.Dispose(); throw; }
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
                    if (name == "content-type") request.Content.Headers.TryAddWithoutValidation("Content-Type", value);
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
            if (JsonNode.Parse(raw) is JsonObject parsed && parsed["error"] is JsonObject error)
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
            try { value = JsonNode.Parse(data) as JsonObject ?? throw new JsonException("not an object"); }
            catch (JsonException error) { throw new InvalidOperationException("Invalid Codex SSE JSON: " + error.Message); }
            if (_options.OnProviderStreamEvent is { } hook) await hook(JsonData.Parse(value.ToJsonString()), _model, token).ConfigureAwait(false);
            if (value["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type)) continue;
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
                throw new CodexApiException(message, error?["code"]?.GetValue<string>());
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
                yield return JsonData.Parse(new JsonObject { ["type"] = status == "incomplete" ? "response.incomplete" : "response.completed", ["response"] = response.DeepClone() }.ToJsonString());
                yield break;
            }
            yield return JsonData.Parse(value.ToJsonString());
        }
    }

    private static JsonData Observation(HttpResponseMessage response)
    {
        var headers = new JsonObject();
        foreach (var field in response.Headers.Concat(response.Content.Headers)) headers[field.Key.ToLowerInvariant()] = string.Join(", ", field.Value);
        return JsonData.Parse(new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = headers }.ToJsonString());
    }
}
