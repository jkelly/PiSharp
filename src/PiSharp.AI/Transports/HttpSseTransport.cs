using System.Net;
using System.Runtime.CompilerServices;

namespace PiSharp.AI.Transports;

/// <summary>A status-only rejection that excludes response bodies, headers, URIs and reason phrases.</summary>
public sealed class HttpSseRejectedException(HttpStatusCode statusCode)
    : HttpRequestException($"HTTP SSE request rejected with status {(int)statusCode}.", null, statusCode);

/// <summary>One injected HTTP send followed by SSE framing. The caller owns the client and request.</summary>
public sealed class HttpSseTransport
{
    private readonly HttpClient _client;
    private readonly SseDecoderOptions _options;

    public HttpSseTransport(HttpClient client, SseDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _options = options ?? new();
        _ = new SseDecoder(_options); // Validate all framing limits before any external send.
        _client = client;
    }

    /// <summary>Enumeration sends the supplied request once; the response and acquired body are owned here.</summary>
    public IAsyncEnumerable<SseEvent> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<SseEvent> SendCoreAsync(HttpRequestMessage request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var owned = await PrepareAsync(request, null, cancellationToken).ConfigureAwait(false);
        await foreach (var message in owned.ReadAsync(cancellationToken).ConfigureAwait(false)) yield return message;
    }

    internal async ValueTask<OwnedResponse> PrepareAsync(HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, ValueTask>? inspect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode) throw new HttpSseRejectedException(response.StatusCode);
            if (inspect is not null) await inspect(response, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(response, _options);
        }
        catch { response.Dispose(); throw; }
    }

    // Source providers may need to acquire input before an awaited response hook.
    // Transfer the physical owner first, so a rejecting hook cannot dispose an active pull.
    // Existing canonical preparation deliberately keeps its zero-acquisition path above.
    internal async ValueTask<OwnedResponse> PrepareOwnedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode) throw new HttpSseRejectedException(response.StatusCode);
            return new(response, _options);
        }
        catch { response.Dispose(); throw; }
    }

    internal sealed class OwnedResponse(HttpResponseMessage response, SseDecoderOptions options) : IAsyncDisposable
    {
        private Stream? _body;
        private int _readClaimed, _disposed;
        private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask InspectAsync(Func<HttpResponseMessage, CancellationToken, ValueTask> inspect,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inspect);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            await inspect(response, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Inspection borrows the response; rejection leaves disposal with its owner.
        }

        internal async IAsyncEnumerable<SseEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var body = await AcquireBodyAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var message in new SseDecoder(options).DecodeAsync(body, leaveOpen: true, cancellationToken).ConfigureAwait(false))
                yield return message;
        }

        internal async ValueTask<Stream> AcquireBodyAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _readClaimed, 1) != 0 || Volatile.Read(ref _disposed) != 0)
                throw new InvalidOperationException("An owned SSE response has exactly one body reader.");
            cancellationToken.ThrowIfCancellationRequested();
            return _body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = DisposeCoreAsync();
            return new(_disposal.Task);
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                try { if (_body is not null) await _body.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    // HttpContent may synchronously dispose its cached body again; injected streams are idempotent.
                    response.Dispose();
                }
                _disposal.TrySetResult();
            }
            catch (Exception error) { _disposal.TrySetException(error); }
        }
    }
}
