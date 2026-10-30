// Decision 0011 (run W4, R0): the capture hook in the routing path. RunsModule wraps RoutingOptions.ProviderHandler with it, so every
// provider request of every account route passes through it (the routes keep it for their life; it is never disposed by them). Outside a
// run (RunHttpScope.Current is null) it passes requests through untouched. Inside one it:
//   - merges the run's JSON patch into the model request's body (a POST whose path ends with the API's path, a JSON body), so the options
//     ChatRequest cannot carry (temperature, top P, stop, seed, response format, tool choice, service tier, provider options) reach the
//     provider; a model request it cannot patch (a compressed or non-JSON body) fails before it is sent, so an option the user set is never
//     silently dropped;
//   - adds the run's extra headers (never a credential: RunSpec refuses those);
//   - only when the user turned raw payloads on (a RunCapture), keeps the redacted request, the response headers, each raw SSE line with
//     its arrival time (the body is teed as the transport reads it, unchanged), a small non-SSE body, transport errors and timing marks.
// WebSocket upgrades (101) are not teed. Keyed by op: the run (or chat call) it records into is the scope's.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Routing;

/// <summary>A model request that a run's options could not be applied to; it was not sent.</summary>
public sealed class RunPatchException(string message) : HttpRequestException(message);

/// <summary>The routing path's capture hook (see the file comment).</summary>
public sealed class RunCaptureHandler : DelegatingHandler
{
    private readonly bool ownsInner;
    private int disposed;

    /// <summary>A hook over <paramref name="inner"/>; with <paramref name="ownsInner"/> it disposes it.</summary>
    public RunCaptureHandler(HttpMessageHandler inner, bool ownsInner) : base(inner)
    {
        this.ownsInner = ownsInner;
    }

    /// <summary>A handler as the routes make their own (redirects off), shared by every route, without cookies (one account's cookies never
    /// reach another's requests).</summary>
    public static HttpMessageHandler CreateDefaultInner() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = RunHttpScope.Current;
        if (context is null) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        context.CountRequest();
        if (context.Patch is { } patch && IsModelRequest(request, context.PatchPath))
            await PatchAsync(request, patch, context).ConfigureAwait(false);
        if (context.Headers is { } headers)
            foreach (var (name, value) in headers)
            {
                try { request.Headers.Remove(name); }
                catch (InvalidOperationException) { /* A content header's name: TryAddWithoutValidation refuses it too. */ }
                request.Headers.TryAddWithoutValidation(name, value);
            }
        var capture = context.Capture;
        if (capture is null) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        await capture.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        capture.Mark("request-sent");
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            capture.Add("error", new JsonObject { ["type"] = error.GetType().Name, ["message"] = error.Message }.ToJsonString());
            throw;
        }
        capture.Mark("headers", new JsonObject { ["status"] = (int)response.StatusCode });
        capture.Response(response);
        if (response.StatusCode == HttpStatusCode.SwitchingProtocols || response.Content is null) return response;
        var sse = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        response.Content = new TeeContent(response.Content, capture, sse);
        return response;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // DelegatingHandler would dispose the inner handler, which is the caller's unless this hook made it.
        if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (ownsInner) InnerHandler?.Dispose();
    }

    private static bool IsModelRequest(HttpRequestMessage request, string? path) =>
        path is not null && request.Method == HttpMethod.Post && request.RequestUri is { } uri &&
        (uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString).EndsWith(path, StringComparison.Ordinal);

    private static async Task PatchAsync(HttpRequestMessage request, JsonObject patch, RunHttpContext context)
    {
        var api = context.Api ?? "provider";
        var content = request.Content ?? throw new RunPatchException($"PiSharp could not apply this run's options to the {api} request (it has no body), so it was not sent.");
        if (content.Headers.ContentEncoding.Count > 0)
            throw new RunPatchException($"PiSharp could not apply this run's options to the {api} request (its body is compressed), so it was not sent.");
        var type = content.Headers.ContentType?.MediaType;
        if (type is not null && !type.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new RunPatchException($"PiSharp could not apply this run's options to the {api} request (its body is {type}), so it was not sent.");
        JsonObject body;
        try
        {
            body = JsonNode.Parse(await content.ReadAsStringAsync().ConfigureAwait(false)) as JsonObject
                ?? throw new RunPatchException($"PiSharp could not apply this run's options to the {api} request (its body is not a JSON object), so it was not sent.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new RunPatchException($"PiSharp could not apply this run's options to the {api} request (its body is not JSON), so it was not sent.");
        }
        RunModelOptions.Merge(body, (JsonObject)patch.DeepClone(), "", null);
        var replaced = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        foreach (var header in content.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                replaced.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Content = replaced;
        content.Dispose();
        context.CountPatched();
    }

    /// <summary>A response body read through a <see cref="TeeStream"/>: the transport reads it unchanged while the capture keeps its lines.</summary>
    private sealed class TeeContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly RunCapture capture;
        private readonly bool sse;

        public TeeContent(HttpContent inner, RunCapture capture, bool sse)
        {
            this.inner = inner;
            this.capture = capture;
            this.sse = sse;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new TeeStream(await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), capture, sse);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A read-only stream that passes the inner stream's bytes through and gives the capture each SSE line (or, for another body,
    /// its first <see cref="RunCapture.MaxBodyChars"/> characters) as they arrive.</summary>
    private sealed class TeeStream(Stream inner, RunCapture capture, bool sse) : Stream
    {
        private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder pending = new();
        private char[] chars = new char[4096];
        private bool started;
        private bool ended;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Observe(buffer[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Observe(buffer.Span[..read]);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                End("closed");
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            End("closed");
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void Observe(ReadOnlySpan<byte> bytes)
        {
            if (ended) return;
            try
            {
                if (bytes.Length == 0)
                {
                    End("end");
                    return;
                }
                if (!started)
                {
                    started = true;
                    capture.Mark("first-byte");
                }
                if (chars.Length < bytes.Length + 4) chars = new char[bytes.Length + 4];
                var count = decoder.GetChars(bytes, chars, flush: false);
                if (!sse)
                {
                    if (pending.Length < RunCapture.MaxBodyChars) pending.Append(chars, 0, Math.Min(count, RunCapture.MaxBodyChars - pending.Length));
                    return;
                }
                for (var index = 0; index < count; index++)
                {
                    var character = chars[index];
                    if (character == '\n') Line();
                    else if (character != '\r') pending.Append(character);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The capture never breaks the stream it watches.
                ended = true;
            }
        }

        private void Line()
        {
            if (pending.Length > 0) capture.Add("sse", pending.ToString());
            pending.Clear();
        }

        private void End(string mark)
        {
            if (ended) return;
            ended = true;
            try
            {
                if (sse) Line();
                else if (pending.Length > 0) capture.Add("body", pending.ToString());
                pending.Clear();
                capture.Mark(mark);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Best effort only.
            }
        }
    }
}
