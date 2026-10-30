// Decision 0011 (run W4, R0; brief rule 6): what the routing path's capture hook (Routing/RunCaptureHandler) knows about the call it sees.
// A run (RunEngine) or a recorded chat call (RecordingDurableModels) sets RunHttpScope.Current around the provider call; the hook reads it
// on each HTTP request: the run's id, the JSON patch, strict tools and headers to apply to the model request, the run's timeout, and, only
// when the user turned raw payloads on, a RunCapture to record into. A run that needs the HTTP request (a patch, strict tools or a
// timeout) makes the account router choose Codex's SSE transport, whose request the hook sees, over its WebSocket one. An AsyncLocal set inside an async iterator is lost after its yield, so the iterators set it again before
// every step they take of the inner stream.
//
// RunCapture keeps a run's raw items in memory until the run ends (the engine stores them with its row): the request (method, URL, headers,
// body), the response headers, every raw SSE line with its arrival time, a small non-SSE body, transport errors and timing marks, at most
// MaxBytes per run (then one "truncated" mark). Nothing is kept unredacted: the Authorization header and every header whose name carries a
// credential (RunRedaction.SecretHeader) is replaced by "[redacted]" before it is kept, and so is every query parameter that carries one
// (key, api_key, token, access_token, sig, signature, x-amz-*); each value redacted is also scrubbed from every body and SSE line kept, so a
// provider that echoes a key never stores it.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.WebAgent.Runs;

/// <summary>The run a provider HTTP request belongs to, as the capture hook sees it.</summary>
public sealed class RunHttpContext
{
    private int patched;
    private int requests;

    /// <summary>The run (or chat call) id.</summary>
    public required string RunId { get; init; }

    /// <summary>The model's API.</summary>
    public string? Api { get; init; }

    /// <summary>The JSON merged into the model request's body, or null. The engine sets it for the output cap it sends
    /// (<see cref="RunModelOptions.SendPatch"/>) before the request starts.</summary>
    public JsonObject? Patch { get; set; }

    /// <summary>The end of the model request's path (<see cref="RunRequestPlan.PatchPath"/>), or null.</summary>
    public string? PatchPath { get; set; }

    /// <summary>Extra headers for every request of the call, or null.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The tools marked strict in the model request's body, or null.</summary>
    public IReadOnlyList<string>? StrictTools { get; init; }

    /// <summary>How long each request of the call waits for the provider's response headers, in milliseconds, or null for no limit.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Whether the call needs its model request to go over HTTP through the hook (a patch, strict tools or a timeout): the account
    /// router then sends a Codex request over SSE rather than a WebSocket, which the hook cannot change.</summary>
    public bool NeedsHttp => Patch is not null || StrictTools is { Count: > 0 } || TimeoutMs is not null;

    /// <summary>Where raw payloads go, or null when they are off.</summary>
    public RunCapture? Capture { get; init; }

    /// <summary>How many model requests were patched.</summary>
    public int Patched => Volatile.Read(ref patched);

    /// <summary>How many HTTP requests the call made.</summary>
    public int Requests => Volatile.Read(ref requests);

    internal void CountPatched() => Interlocked.Increment(ref patched);

    internal void CountRequest() => Interlocked.Increment(ref requests);
}

/// <summary>The run of the current provider call (an AsyncLocal).</summary>
public static class RunHttpScope
{
    private static readonly AsyncLocal<RunHttpContext?> current = new();

    /// <summary>The current call's run, or null outside a run.</summary>
    public static RunHttpContext? Current
    {
        get => current.Value;
        set => current.Value = value;
    }
}

/// <summary>Redaction of what the capture hook keeps.</summary>
public static class RunRedaction
{
    /// <summary>What a redacted value is replaced with.</summary>
    public const string Redacted = "[redacted]";

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "x-goog-api-key", "cookie", "set-cookie", "x-amz-security-token",
        "x-amz-date", "x-amz-content-sha256", "chatgpt-account-id", "openai-organization", "openai-project",
    };

    private static readonly HashSet<string> SecretQuery = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "api_key", "apikey", "api-key", "token", "access_token", "id_token", "refresh_token", "sig", "signature", "code", "client_secret",
    };

    /// <summary>Whether the header <paramref name="name"/> may carry a credential (it is kept as <see cref="Redacted"/>).</summary>
    public static bool SecretHeader(string name) => SecretHeaders.Contains(name) || RunSpec.SecretName(name);

    /// <summary>Whether the query parameter <paramref name="name"/> may carry a credential.</summary>
    public static bool SecretParameter(string name) =>
        SecretQuery.Contains(name) || name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) || RunSpec.SecretName(name);

    /// <summary><paramref name="uri"/> with every credential query parameter's value redacted (each value is given to
    /// <paramref name="secret"/>).</summary>
    public static string Url(Uri uri, Action<string> secret)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Query.Length <= 1) return uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.ToString();
        var parts = uri.Query[1..].Split('&');
        for (var index = 0; index < parts.Length; index++)
        {
            var equals = parts[index].IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(equals < 0 ? parts[index] : parts[index][..equals]);
            if (equals < 0 || !SecretParameter(name)) continue;
            var value = parts[index][(equals + 1)..];
            secret(value);
            secret(Uri.UnescapeDataString(value));
            parts[index] = parts[index][..(equals + 1)] + Redacted;
        }
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", parts);
    }

    /// <summary>The headers as a JSON object (lower-case names; several values joined with <c>, </c>), credentials redacted (each value is
    /// given to <paramref name="secret"/>, and so is a bearer token's token).</summary>
    public static JsonObject Headers(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, Action<string> secret)
    {
        var json = new JsonObject();
        foreach (var (name, values) in headers)
        {
            var lower = name.ToLowerInvariant();
            var value = string.Join(", ", values);
            if (SecretHeader(lower))
            {
                secret(value);
                var space = value.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0) secret(value[(space + 1)..].Trim());
                json[lower] = Redacted;
            }
            else json[lower] = value;
        }
        return json;
    }
}

/// <summary>One run's raw payload items while it runs. Thread-safe.</summary>
public sealed class RunCapture
{
    /// <summary>The most raw payload text kept per run: 8 MB.</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>The most characters of one request body or non-SSE response body kept.</summary>
    public const int MaxBodyChars = 1024 * 1024;

    private readonly Lock gate = new();
    private readonly Func<long> elapsed;
    private readonly List<RunRawItem> items = [];
    private readonly HashSet<string> secrets = new(StringComparer.Ordinal);
    private long bytes;
    private long sequence;
    private bool truncated;

    /// <summary>A capture whose times are <paramref name="elapsed"/> (milliseconds since the call started).</summary>
    public RunCapture(Func<long>? elapsed = null)
    {
        if (elapsed is null)
        {
            var clock = Stopwatch.StartNew();
            elapsed = () => clock.ElapsedMilliseconds;
        }
        this.elapsed = elapsed;
    }

    /// <summary>Whether items were dropped for <see cref="MaxBytes"/>.</summary>
    public bool Truncated { get { lock (gate) return truncated; } }

    /// <summary>Milliseconds since the call started.</summary>
    public long Elapsed => elapsed();

    /// <summary>Remembers <paramref name="value"/> as a credential, so it is scrubbed from everything kept (values under 8 characters are
    /// redacted where they appear but not searched for).</summary>
    public void Secret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8 || value == RunRedaction.Redacted) return;
        // Kept items are JSON text, where a character such as + or / of a key may be escaped: both forms are scrubbed.
        var encoded = System.Text.Json.JsonEncodedText.Encode(value).ToString();
        lock (gate)
        {
            secrets.Add(value);
            if (!encoded.Equals(value, StringComparison.Ordinal)) secrets.Add(encoded);
        }
    }

    /// <summary><paramref name="text"/> with every remembered credential replaced by <see cref="RunRedaction.Redacted"/>.</summary>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] known;
        lock (gate) known = [.. secrets];
        foreach (var secret in known.OrderByDescending(value => value.Length))
            text = text.Replace(secret, RunRedaction.Redacted, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Keeps one item (scrubbed), unless the run is over <see cref="MaxBytes"/>.</summary>
    public void Add(string kind, string data)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(data);
        var at = elapsed();
        var text = Scrub(data);
        lock (gate)
        {
            if (truncated) return;
            var size = Encoding.UTF8.GetByteCount(text) + kind.Length + 16;
            if (bytes + size > MaxBytes)
            {
                truncated = true;
                items.Add(new RunRawItem(++sequence, "timing", at, new JsonObject { ["mark"] = "truncated", ["maxBytes"] = MaxBytes }.ToJsonString()));
                return;
            }
            bytes += size;
            items.Add(new RunRawItem(++sequence, kind, at, text));
        }
    }

    /// <summary>Keeps a timing mark (<c>{mark, ...}</c>).</summary>
    public void Mark(string mark, JsonObject? detail = null)
    {
        var json = detail ?? new JsonObject();
        json.Insert(0, "mark", mark);
        Add("timing", json.ToJsonString());
    }

    /// <summary>Every item kept so far, scrubbed again with every credential seen since it was kept.</summary>
    public IReadOnlyList<RunRawItem> Items()
    {
        List<RunRawItem> copy;
        lock (gate) copy = [.. items];
        return [.. copy.Select(item => item with { Data = Scrub(item.Data) })];
    }

    /// <summary>The request as kept: method, redacted URL and headers (content headers too) and the body text (a compressed or binary body
    /// by its size only).</summary>
    public async Task RequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = new JsonObject
        {
            ["method"] = request.Method.Method,
            ["url"] = request.RequestUri is { } uri ? RunRedaction.Url(uri, Secret) : null,
            ["version"] = request.Version.ToString(),
        };
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = request.Headers;
        if (request.Content is { } content) headers = headers.Concat(content.Headers);
        json["headers"] = RunRedaction.Headers(headers, Secret);
        if (request.Content is { } body)
        {
            await body.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            var raw = await body.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            json["bodyBytes"] = raw.Length;
            json["body"] = Text(body.Headers, raw);
        }
        Add("request", json.ToJsonString());
    }

    /// <summary>The response headers as kept (status, reason, version, redacted headers).</summary>
    public void Response(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = response.Headers;
        if (response.Content is { } content) headers = headers.Concat(content.Headers);
        Add("response", new JsonObject
        {
            ["status"] = (int)response.StatusCode,
            ["reason"] = response.ReasonPhrase,
            ["version"] = response.Version.ToString(),
            ["headers"] = RunRedaction.Headers(headers, Secret),
        }.ToJsonString());
    }

    private static string Text(HttpContentHeaders headers, byte[] raw)
    {
        if (headers.ContentEncoding.Count == 1 && headers.ContentEncoding.First().Equals("zstd", StringComparison.OrdinalIgnoreCase))
        {
            // OpenAI Codex sends its SSE request body as one zstd frame: kept as the JSON it holds.
            try { raw = PiSharp.AI.Compression.ZstdDecoder.Decompress(raw, (int)MaxBytes); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return $"[{raw.Length:N0} bytes, zstd]";
            }
        }
        else if (headers.ContentEncoding.Count > 0) return $"[{raw.Length:N0} bytes, {string.Join(", ", headers.ContentEncoding)}]";
        var type = headers.ContentType?.MediaType ?? "";
        if (type.Length > 0 && !type.Contains("json", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return $"[{raw.Length:N0} bytes of {type}]";
        var text = Encoding.UTF8.GetString(raw);
        return text.Length <= MaxBodyChars ? text : text[..MaxBodyChars] + $"… [{text.Length - MaxBodyChars:N0} more characters]";
    }
}
