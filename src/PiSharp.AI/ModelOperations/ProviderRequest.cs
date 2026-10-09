// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/provider-retry.ts, utils/error-body.ts,
// utils/headers.ts and utils/sanitize-unicode.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.ModelOperations;

/// <summary>A provider HTTP failure in the shape <c>retryProviderRequest</c> and <c>normalizeProviderError</c> understand:
/// a status (absent for timeouts and connection failures), response headers, and the raw body or the SDK's parsed
/// <c>error</c> object.</summary>
public sealed class ProviderRequestException : Exception
{
    public int? Status { get; }
    public ImmutableArray<KeyValuePair<string, string>> Headers { get; }
    /// <summary>The raw response body (classifier APIs).</summary>
    public string? Body { get; }
    /// <summary>The parsed <c>error</c> member of a JSON error body (OpenAI SDK <c>APIError.error</c>).</summary>
    public JsonElement? SdkError { get; }
    public bool IsTimeout { get; }

    internal ProviderRequestException(string message, int? status, ImmutableArray<KeyValuePair<string, string>> headers,
        string? body = null, JsonElement? sdkError = null, bool timeout = false) : base(message)
    { Status = status; Headers = headers.IsDefault ? [] : headers; Body = body; SdkError = sdkError; IsTimeout = timeout; }

    internal string? Header(string name) => Headers.FirstOrDefault(pair => pair.Key == name).Value;
}

/// <summary>The caller's cancellation as upstream reports it: <c>AbortError</c> with "Request aborted".</summary>
public sealed class ModelRequestAbortedException(CancellationToken token) : OperationCanceledException("Request aborted", token);

internal static partial class ProviderRequest
{
    internal const int MaxErrorBodyCharacters = 4000;
    private const int DefaultMaxRetryDelayMs = 60_000;
    private static readonly JavaScriptEncoder Relaxed = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    private static readonly JsonSerializerOptions NodeOutput = new() { Encoder = Relaxed };
    private static readonly EcmaScriptJsonProjectionOptions Unbounded = new(MaximumInputCharacters: int.MaxValue,
        MaximumInputBytes: int.MaxValue, MaximumOutputCharacters: int.MaxValue, MaximumOutputBytes: int.MaxValue, MaximumDepth: 64,
        MaximumNodes: int.MaxValue, MaximumPropertiesPerObject: int.MaxValue, MaximumNumbers: int.MaxValue,
        MaximumNumberCharacters: 16_384, MaximumTotalNumberCharacters: int.MaxValue, MaximumStringCharacters: int.MaxValue);
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan });

    internal static HttpMessageInvoker Http(ModelRequestOptions? options) => options?.Http ?? SharedClient.Value;

    // ----- JSON -----

    /// <summary><c>JSON.stringify(value)</c>.</summary>
    internal static string Stringify(JsonNode? value) => EcmaScriptJsonProjection.Project(
        value is null ? "null" : value.ToJsonString(NodeOutput), Unbounded);

    /// <summary><c>JSON.stringify(value)</c> of an owned JSON value.</summary>
    internal static string Stringify(JsonData value) => EcmaScriptJsonProjection.Project(value.ToString(), Unbounded);

    /// <summary><c>JSON.stringify(value, null, indent)</c>: the compact form with line breaks and <paramref name="indent"/>
    /// spaces per level; empty objects and arrays stay <c>{}</c> and <c>[]</c>.</summary>
    internal static string Stringify(JsonData value, int indent)
    {
        var compact = Stringify(value); var output = new StringBuilder(compact.Length * 2); var depth = 0;
        void Break() { output.Append('\n'); output.Append(' ', depth * indent); }
        for (var index = 0; index < compact.Length; index++)
        {
            var character = compact[index];
            if (character == '"')
            {
                var end = index + 1;
                while (compact[end] != '"') end += compact[end] == '\\' ? 2 : 1;
                output.Append(compact, index, end - index + 1); index = end;
            }
            else if (character is '{' or '[')
            {
                if (compact[index + 1] is '}' or ']') { output.Append(character).Append(compact[index + 1]); index++; continue; }
                output.Append(character); depth++; Break();
            }
            else if (character is '}' or ']') { depth--; Break(); output.Append(character); }
            else if (character == ',') { output.Append(','); Break(); }
            else if (character == ':') output.Append(": ");
            else output.Append(character);
        }
        return output.ToString();
    }

    /// <summary><c>String(number)</c>.</summary>
    internal static string JsNumber(double value) => double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity"
        : double.IsNegativeInfinity(value) ? "-Infinity" : Stringify(JsonValue.Create(value));

    internal static JsonNode Node(JsonData value) => JsonNode.Parse(value.ToString())!;

    internal static bool IsRecord(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    internal static bool TryGet(JsonElement value, string name, out JsonElement field)
    {
        field = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out field);
    }

    internal static bool IsString(JsonElement value, string name, out string text)
    {
        text = "";
        if (!TryGet(value, name, out var field) || field.ValueKind != JsonValueKind.String) return false;
        text = field.GetString()!; return true;
    }

    /// <summary><c>typeof value === "number" &amp;&amp; Number.isFinite(value)</c>.</summary>
    internal static bool FiniteNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }

    /// <summary>classifier-shared.ts <c>requiredNumber</c>.</summary>
    internal static double RequiredNumber(string label, JsonElement? value, string field) =>
        value is { } element && FiniteNumber(element, out var number) ? number : throw new InvalidDataException($"{label} returned an invalid {field}");

    /// <summary>sanitize-unicode.ts <c>sanitizeSurrogates</c>: drops unpaired surrogates.</summary>
    internal static string SanitizeSurrogates(string text)
    {
        StringBuilder? output = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var paired = char.IsHighSurrogate(character) ? index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                : !char.IsLowSurrogate(character) || index > 0 && char.IsHighSurrogate(text[index - 1]);
            if (paired) { output?.Append(character); continue; }
            output ??= new StringBuilder(text, 0, index, text.Length);
        }
        return output?.ToString() ?? text;
    }

    // ----- Headers -----

    /// <summary>headers.ts <c>providerHeadersToRecord</c>: case-insensitive merge in source order; a later name replaces an
    /// earlier one (keeping the later spelling) and a null value deletes it.</summary>
    internal static ImmutableArray<KeyValuePair<string, string>> MergeHeaders(params IEnumerable<KeyValuePair<string, string?>>?[] sources)
    {
        var merged = new List<KeyValuePair<string, string>>();
        foreach (var source in sources)
            foreach (var (name, value) in source ?? [])
            {
                merged.RemoveAll(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
                if (value is not null) merged.Add(new(name, value));
            }
        return [.. merged];
    }

    internal static IEnumerable<KeyValuePair<string, string?>> Nullable(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value));

    // ----- Errors -----

    /// <summary>error-body.ts <c>normalizeProviderError</c> followed by <c>formatProviderError</c>.</summary>
    internal static string FormatError(Exception error, string? prefix = null)
    {
        int? status = null; string? body = null; var message = error.Message;
        if (error is ProviderRequestException provider)
        {
            status = provider.Status;
            var text = provider.Body ?? (provider.SdkError is { ValueKind: JsonValueKind.Object } sdk && sdk.EnumerateObject().Any()
                ? EcmaScriptJsonProjection.Project(sdk.GetRawText(), Unbounded) : null);
            if (text is not null && text.Trim() is { Length: > 0 } trimmed) body = TruncateErrorText(trimmed, MaxErrorBodyCharacters);
        }
        var carriesBody = body is null || message.Contains(body, StringComparison.Ordinal);
        if (carriesBody || status is null || body is null)
            return prefix is not null && status is not null ? $"{prefix} ({status}): {message}" : message;
        return prefix is not null ? $"{prefix} ({status}): {body}" : $"{status}: {body}";
    }

    internal static string TruncateErrorText(string text, int maxChars) =>
        text.Length <= maxChars ? text : $"{text[..maxChars]}... [truncated {text.Length - maxChars} chars]";

    // ----- Retries -----

    /// <summary>provider-retry.ts <c>retryProviderRequest</c>: the OpenAI/Anthropic SDK retry policy with an interruptible
    /// backoff. Only <see cref="ProviderRequestException"/> failures are retried.</summary>
    internal static async Task<T> RetryAsync<T>(Func<Task<T>> request, int maxRetries, ModelRequestOptions? options,
        IReadOnlyCollection<int>? noRetryStatuses, CancellationToken signal)
    {
        var remaining = maxRetries; var time = options?.TimeProvider ?? TimeProvider.System;
        for (;;)
        {
            try { return await request().ConfigureAwait(false); }
            catch (Exception error)
            {
                if (signal.IsCancellationRequested) throw new ModelRequestAbortedException(signal);
                if (remaining <= 0 || error is not ProviderRequestException provider || !Retryable(provider)) throw;
                if (provider.Status is { } status && noRetryStatuses?.Contains(status) == true) throw;
                var retryIndex = maxRetries - remaining; remaining--;
                var delay = RetryDelayMs(provider, retryIndex, options?.MaxRetryDelayMs, time);
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(delay, int.MaxValue - 1))), time, signal).ConfigureAwait(false); }
                catch (OperationCanceledException) when (signal.IsCancellationRequested) { throw new ModelRequestAbortedException(signal); }
            }
        }
    }

    private static bool Retryable(ProviderRequestException error)
    {
        var directive = error.Header("x-should-retry");
        if (directive == "true") return true;
        if (directive == "false") return false;
        return error.Status is null or 408 or 409 or 429 || error.Status >= 500;
    }

    private static double RetryDelayMs(ProviderRequestException error, int retryIndex, int? maxRetryDelayMs, TimeProvider time)
    {
        if (error.Header("retry-after-ms") is { Length: > 0 } milliseconds && ParseFloat(milliseconds) is var value && double.IsFinite(value))
            return ValidateServerDelay(value, maxRetryDelayMs, error.Message);
        if (error.Header("retry-after") is { Length: > 0 } retryAfter)
        {
            var seconds = ParseFloat(retryAfter);
            var delay = double.IsNaN(seconds)
                ? DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? (date - time.GetUtcNow()).TotalMilliseconds : double.NaN
                : seconds * 1000;
            if (double.IsFinite(delay)) return ValidateServerDelay(delay, maxRetryDelayMs, error.Message);
        }
        var exponential = Math.Min(0.5 * Math.Pow(2, retryIndex), 8) * 1000;
        return exponential * (1 - Random.Shared.NextDouble() * 0.25);
    }

    private static double ValidateServerDelay(double delayMs, int? maxRetryDelayMs, string providerErrorMessage)
    {
        var maxDelayMs = maxRetryDelayMs ?? DefaultMaxRetryDelayMs;
        if (maxDelayMs > 0 && delayMs > maxDelayMs)
            throw new InvalidOperationException(
                $"Server requested {Math.Ceiling(delayMs / 1000)}s retry delay (max: {Math.Ceiling(maxDelayMs / 1000d)}s). {providerErrorMessage}");
        return delayMs;
    }

    /// <summary>JavaScript <c>parseFloat</c>: the longest numeric prefix after leading white space, else NaN.</summary>
    internal static double ParseFloat(string value)
    {
        var match = FloatPrefix().Match(value);
        if (!match.Success) return double.NaN;
        var text = match.Value.Trim();
        if (text.EndsWith("Infinity", StringComparison.Ordinal)) return text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }

    [GeneratedRegex(@"^\s*[+-]?(?:Infinity|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPrefix();

    // ----- HTTP -----

    /// <summary>One POST attempt: a fresh timeout per attempt, the body as text, and the response headers as fetch reports
    /// them (lower-cased names in sorted order, repeated values joined with ", ").</summary>
    internal static async Task<(ProviderResponseInfo Response, string Body)> PostAsync(HttpMessageInvoker http, Uri url,
        ImmutableArray<KeyValuePair<string, string>> headers, string body, ModelRequestOptions? options, CancellationToken signal,
        Func<Exception, Exception>? mapTransportFailure = null, string? timeoutMessage = null)
    {
        using var timeout = options?.TimeoutMs is { } milliseconds
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)), options.TimeProvider) : null;
        using var linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(signal, timeout.Token);
        var token = linked?.Token ?? signal;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            foreach (var (name, value) in headers)
            {
                if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                { request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value); continue; }
                if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content.Headers.TryAddWithoutValidation(name, value);
            }
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var info = new ProviderResponseInfo((int)response.StatusCode, ResponseHeaders(response));
            return (info, text);
        }
        catch (Exception error) when (timeout?.IsCancellationRequested == true && !signal.IsCancellationRequested && error is OperationCanceledException)
        {
            // AbortSignal.timeout fired without the caller's signal: a timeout, not a cancellation.
            throw new ProviderRequestException(timeoutMessage ?? $"Request timed out after {options!.TimeoutMs}ms", null, [], body: "", timeout: true);
        }
        catch (Exception error) when (mapTransportFailure is not null && !signal.IsCancellationRequested && error is HttpRequestException)
        {
            throw mapTransportFailure(error);
        }
    }

    internal static ImmutableArray<KeyValuePair<string, string>> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            var key = name.ToLowerInvariant();
            if (!headers.TryGetValue(key, out var list)) headers[key] = list = [];
            list.AddRange(values);
        }
        return [.. headers.Select(pair => KeyValuePair.Create(pair.Key, string.Join(", ", pair.Value)))];
    }

    internal static bool IsSuccess(int status) => status is >= 200 and <= 299;

    /// <summary>undici fetch's network failure: <c>TypeError: fetch failed</c> (the cause carries the socket error).</summary>
    internal static Exception FetchFailed(Exception error) => new HttpRequestException("fetch failed", error);

    /// <summary><c>response.json()</c>.</summary>
    internal static JsonElement ParseJson(string label, string text)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        // undici's response.json() throws JSON.parse's SyntaxError (classifier-shared.ts, llama-cpp-classify.ts).
        catch (JsonException) { throw new InvalidDataException(PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(text, $"{label} returned invalid JSON")); }
    }

    /// <summary>A usage record priced with <see cref="ModelCost.Calculate"/>; the exact binary64 costs travel as the
    /// source cost snapshot.</summary>
    internal static TokenUsage Usage(long input, long output, long cacheRead, long cacheWrite, long totalTokens,
        (double Input, double Output, double CacheRead, double CacheWrite, double Total) cost)
    {
        if (!double.IsFinite(cost.Total)) throw new InvalidDataException("Usage cost is not a finite number.");
        var exact = JsonData.Parse(new JsonObject
        {
            ["input"] = cost.Input, ["output"] = cost.Output, ["cacheRead"] = cost.CacheRead, ["cacheWrite"] = cost.CacheWrite, ["total"] = cost.Total
        }.ToJsonString());
        decimal Decimal(string name) => exact.Value.GetProperty(name).GetDecimal();
        return new(input, output, cacheRead, cacheWrite, totalTokens,
            new(Decimal("input"), Decimal("output"), Decimal("cacheRead"), Decimal("cacheWrite"), Decimal("total"), SourceBinary64Cost: exact));
    }

    internal static long Now(ModelRequestOptions? options) => (options?.TimeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
}
