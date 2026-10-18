// PiSharp-only (no upstream Pi source): the HTTP plumbing of the media APIs, over the model-operation request helpers
// (ProviderRequest: retry policy, response headers, timeouts and error formatting).
using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.ModelOperations;

namespace PiSharp.AI.Media;

[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
internal static partial class MediaHttp
{
    private const int MaxRedirects = 5;

    /// <summary>A JSON request body.</summary>
    internal static Func<HttpContent> Json(JsonNode body)
    {
        var text = body.ToJsonString();
        return () => new StringContent(text, Encoding.UTF8, "application/json");
    }

    /// <summary>Bearer auth (OpenAI-compatible APIs) under the endpoint's merged headers.</summary>
    internal static ImmutableArray<KeyValuePair<string, string>> BearerHeaders(MediaEndpoint endpoint, string accept = "application/json") =>
        WithAuth(endpoint, accept, "authorization", "Bearer " + endpoint.ApiKey);

    /// <summary>Google API-key auth (<c>x-goog-api-key</c>) under the endpoint's merged headers.</summary>
    internal static ImmutableArray<KeyValuePair<string, string>> GoogleHeaders(MediaEndpoint endpoint, string accept = "application/json") =>
        WithAuth(endpoint, accept, "x-goog-api-key", endpoint.ApiKey ?? "");

    private static ImmutableArray<KeyValuePair<string, string>> WithAuth(MediaEndpoint endpoint, string accept, string name, string value)
    {
        var defaults = new List<KeyValuePair<string, string?>> { new("accept", accept), new("user-agent", "PiSharp") };
        if (!string.IsNullOrEmpty(endpoint.ApiKey)) defaults.Add(new(name, value));
        return ProviderRequest.MergeHeaders(defaults, endpoint.Headers);
    }

    /// <summary><paramref name="baseUrl"/> joined with <paramref name="path"/> by one slash.</summary>
    internal static Uri Url(string baseUrl, string path) => new(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));

    /// <summary>An OpenAI-compatible base URL with a version segment: <c>https://api.mistral.ai</c> becomes <c>…/v1</c>, while
    /// <c>…/v1</c>, <c>…/api/v1</c> and <c>…/v1beta</c> stay as they are.</summary>
    internal static string Versioned(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return VersionSegment().IsMatch(trimmed) ? trimmed : trimmed + "/v1";
    }

    [GeneratedRegex(@"/v\d+[a-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSegment();

    /// <summary>
    /// Sends one request with the model-operation retry policy (408, 409, 429 and 5xx, <c>retry-after</c> honored) and returns the
    /// accepted response's status, headers and body bytes. Redirects are followed for GET requests only; a redirect to another host
    /// drops the credential headers. A response that is not 2xx throws <see cref="ProviderRequestException"/> with the provider's
    /// error message.
    /// </summary>
    internal static Task<(ProviderResponseInfo Response, byte[] Body)> SendAsync(HttpMethod method, Uri url,
        ImmutableArray<KeyValuePair<string, string>> headers, Func<HttpContent>? content, ModelRequestOptions options, int maxRetries,
        CancellationToken cancellationToken)
    {
        var http = ProviderRequest.Http(options);
        return ProviderRequest.RetryAsync(() => AttemptAsync(http, method, url, headers, content, options, cancellationToken),
            maxRetries, options, null, cancellationToken);
    }

    /// <summary><see cref="SendAsync"/> for a JSON response.</summary>
    internal static async Task<JsonElement> SendJsonAsync(string label, HttpMethod method, Uri url, ImmutableArray<KeyValuePair<string, string>> headers,
        Func<HttpContent>? content, ModelRequestOptions options, int maxRetries, CancellationToken cancellationToken)
    {
        var (response, body) = await SendAsync(method, url, headers, content, options, maxRetries, cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(body);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{label} returned a response that is not JSON (status {response.Status}): " +
                ProviderRequest.TruncateErrorText(text, 300));
        }
    }

    private static async Task<(ProviderResponseInfo, byte[])> AttemptAsync(HttpMessageInvoker http, HttpMethod method, Uri url,
        ImmutableArray<KeyValuePair<string, string>> headers, Func<HttpContent>? content, ModelRequestOptions options, CancellationToken signal)
    {
        using var timeout = options.TimeoutMs is { } milliseconds
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)), options.TimeProvider) : null;
        using var linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(signal, timeout.Token);
        var token = linked?.Token ?? signal;
        var current = url;
        var sendHeaders = headers;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(method, current);
                if (content is not null) request.Content = content();
                foreach (var (name, value) in sendHeaders)
                {
                    if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (request.Content is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                        continue;
                    }
                    if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
                }
                using var response = await http.SendAsync(request, token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 300 and <= 399 && method == HttpMethod.Get && response.Headers.Location is { } location && redirects < MaxRedirects)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!string.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase))
                        sendHeaders = [.. sendHeaders.Where(pair => !IsCredential(pair.Key))];
                    current = next;
                    continue;
                }
                var body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                var info = new ProviderResponseInfo(status, ProviderRequest.ResponseHeaders(response));
                if (!ProviderRequest.IsSuccess(status)) throw Error(info, body);
                return (info, body);
            }
        }
        catch (Exception error) when (timeout?.IsCancellationRequested == true && !signal.IsCancellationRequested && error is OperationCanceledException)
        {
            throw new ProviderRequestException($"Request timed out after {options.TimeoutMs}ms", null, [], body: "", timeout: true);
        }
        catch (HttpRequestException error) when (!signal.IsCancellationRequested)
        {
            // A connection failure has no status, so the retry policy retries it.
            throw new ProviderRequestException("Connection error: " + error.Message, null, []);
        }
    }

    private static bool IsCredential(string header) =>
        header.Equals("authorization", StringComparison.OrdinalIgnoreCase) || header.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("api-key", StringComparison.OrdinalIgnoreCase);

    /// <summary>A failed response: <c>{status} {error.message}</c> for the <c>{ error: { message } }</c> bodies OpenAI and Google
    /// send, else the status and the start of the body.</summary>
    private static ProviderRequestException Error(ProviderResponseInfo response, byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                message = error.ValueKind == JsonValueKind.String ? error.GetString()
                    : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var inner) && inner.ValueKind == JsonValueKind.String
                        ? inner.GetString() : null;
        }
        catch (JsonException) { }
        message ??= text.Length == 0 ? "(no body)" : ProviderRequest.TruncateErrorText(text, ProviderRequest.MaxErrorBodyCharacters);
        return new ProviderRequestException($"{response.Status} {message}", response.Status, response.Headers, body: text);
    }

    /// <summary>Adds the members of a JSON object to <paramref name="body"/>, replacing fields of the same name.</summary>
    internal static void MergeOptions(JsonObject body, PiSharp.Contracts.JsonData? options)
    {
        if (options is not { Value.ValueKind: JsonValueKind.Object }) return;
        foreach (var (name, value) in JsonNode.Parse(options.ToString())!.AsObject().ToList())
            body[name] = value?.DeepClone();
    }

    // ----- JSON reading -----

    internal static JsonElement? Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : null;

    internal static string? String(JsonElement value, string name) =>
        Field(value, name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;

    internal static double? Number(JsonElement value, string name) =>
        Field(value, name) is { ValueKind: JsonValueKind.Number } field && field.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    /// <summary>A downloaded video's MIME type from its <c>content-type</c>, else <c>video/mp4</c>.</summary>
    internal static string VideoMime(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var type = headers.FirstOrDefault(pair => pair.Key == "content-type").Value?.Split(';')[0].Trim();
        return type is { Length: > 0 } && type.StartsWith("video/", StringComparison.Ordinal) ? type : "video/mp4";
    }

    /// <summary>The MIME type of an image file name or format name (<c>png</c>, <c>jpeg</c>, <c>webp</c>).</summary>
    internal static string ImageMime(string? format) => format?.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        _ => "image/png",
    };
}
