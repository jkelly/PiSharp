// PiSharp-only (decision 0008). A client for one device's v1 API, after durable-pi's DpiClient (apps/dpi-sdk/src/client.ts, MIT): the token
// goes in the Authorization header, an error body becomes a DurableApiException with its code, and a stream that fails before its headers
// reports the device's error. Follow() reconnects a stream with backoff (1 s doubling to 15 s), treats 45 s without a byte as a dead
// connection, and stops for errors that a retry cannot fix.
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Durable.Client;

/// <summary>A client for one durable device.</summary>
public sealed class DurableDeviceClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsClient;

    /// <summary>A client for the device at <paramref name="baseUrl"/> with <paramref name="token"/>.</summary>
    /// <param name="handler">The HTTP handler; null is a new <see cref="SocketsHttpHandler"/>.</param>
    public DurableDeviceClient(Uri baseUrl, string token, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(token);
        http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) }, disposeHandler: handler is null)
        {
            BaseAddress = baseUrl,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ownsClient = true;
        BaseUrl = baseUrl;
    }

    /// <summary>The device's address.</summary>
    public Uri BaseUrl { get; }

    /// <summary>What the device is. Times out after <paramref name="timeout"/> (default 6 s) as <see cref="DurableErrorCodes.Offline"/>.</summary>
    public async Task<DeviceInfo> DeviceAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        DurableWire.Read<DeviceInfo>(await GetAsync("/v1/device", timeout ?? TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false))
            ?? throw new DurableApiException(DurableErrorCodes.Internal, "The device sent no description.");

    /// <summary>The device's sessions and agents.</summary>
    public async Task<DeviceTree> TreeAsync(CancellationToken cancellationToken = default) =>
        DurableWire.Read<DeviceTree>(await GetAsync("/v1/tree", null, cancellationToken).ConfigureAwait(false))
            ?? throw new DurableApiException(DurableErrorCodes.Internal, "The device sent no tree.");

    /// <summary>A GET of <paramref name="path"/> (with its query).</summary>
    public Task<JsonNode?> GetAsync(string path, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, path, null, null, timeout, cancellationToken);

    /// <summary>A request with an optional JSON body and <c>Idempotency-Key</c>; the response body, or null when it is empty.</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, string? idempotencyKey = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } after) limit.CancelAfter(after);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, text);
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DurableApiException(DurableErrorCodes.Offline, failure is OperationCanceledException
                ? $"{BaseUrl} did not answer in time." : $"{BaseUrl} is unreachable: {failure.Message}", 0, failure);
        }
    }

    /// <summary>The frames of one stream (<c>/v1/events</c>, a session's or an agent's), until it ends. An error before the stream opens is
    /// a <see cref="DurableApiException"/>.</summary>
    public async IAsyncEnumerable<SseFrame> StreamAsync(string path, Action? onActivity = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure)
        {
            throw new DurableApiException(DurableErrorCodes.Offline, $"{BaseUrl} is unreachable: {failure.Message}", 0, failure);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var frames = SseReader.ReadAsync(body, onActivity, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool more;
                try { more = await frames.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
                {
                    throw new DurableApiException(DurableErrorCodes.Offline, $"The stream from {BaseUrl} broke: {failure.Message}", 0, failure);
                }
                if (!more) yield break;
                yield return frames.Current;
            }
        }
    }

    /// <summary>
    /// Follows a stream across reconnects. Each connection starts with the device's <c>hello</c> and a fresh snapshot, so nothing is replayed.
    /// Between connections <paramref name="onDisconnected"/> hears why; the backoff runs from 1 s to 15 s and resets after a <c>hello</c>.
    /// 45 s without a byte (the device pings every 15 s) counts as a dead connection. <c>unauthorized</c>, <c>not_found</c>, <c>invalid</c>
    /// and <c>ambiguous</c> end the enumeration with the error.
    /// </summary>
    public async IAsyncEnumerable<SseFrame> FollowAsync(string path, Action<string>? onDisconnected = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            silence.CancelAfter(TimeSpan.FromSeconds(45));
            var reason = "The stream ended.";
            await using (var frames = StreamAsync(path, () => silence.CancelAfter(TimeSpan.FromSeconds(45)), silence.Token).GetAsyncEnumerator(silence.Token))
            {
                while (true)
                {
                    bool more;
                    try { more = await frames.MoveNextAsync().ConfigureAwait(false); }
                    catch (DurableApiException failure) when (failure.Code is not (DurableErrorCodes.Unauthorized or DurableErrorCodes.NotFound
                        or DurableErrorCodes.Invalid or DurableErrorCodes.Ambiguous))
                    {
                        reason = failure.Message;
                        break;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        reason = "No data for 45 s.";
                        break;
                    }
                    if (!more) break;
                    if (frames.Current.Type == "hello") backoff = TimeSpan.FromSeconds(1);
                    yield return frames.Current;
                }
            }
            if (cancellationToken.IsCancellationRequested) yield break;
            onDisconnected?.Invoke(reason);
            try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(15).Ticks));
        }
    }

    private DurableApiException Failure(HttpStatusCode status, string text)
    {
        try
        {
            if (DurableWire.Read<ApiErrorBody>(JsonNode.Parse(text)) is { Error: { } error })
                return new DurableApiException(error.Code, error.Message, (int)status);
        }
        catch (System.Text.Json.JsonException) { /* Not an error envelope. */ }
        var code = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DurableErrorCodes.Unauthorized,
            HttpStatusCode.NotFound => DurableErrorCodes.NotFound,
            HttpStatusCode.BadRequest => DurableErrorCodes.Invalid,
            HttpStatusCode.Conflict => DurableErrorCodes.Conflict,
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => DurableErrorCodes.Offline,
            _ => DurableErrorCodes.Internal,
        };
        return new DurableApiException(code, $"{BaseUrl} answered {(int)status} {status}.", (int)status);
    }

    /// <summary>Closes the connections.</summary>
    public void Dispose()
    {
        if (ownsClient) http.Dispose();
    }
}
