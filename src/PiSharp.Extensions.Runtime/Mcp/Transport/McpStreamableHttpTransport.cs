using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp.Transport;

public sealed record McpHttpOwnedReadAbort(Task Original, Exception Cause);
public sealed class McpHttpOriginalException(Task original, Exception cause) : IOException("MCP HTTP original failed.", cause)
{ public Task Original { get; } = original; }

/// <summary>MCP HTTP methods and bounded JSON/SSE over one explicitly admitted exchange. Never creates an HttpClient.</summary>
public sealed class McpStreamableHttpTransport : IMcpWireTransport
{
    private sealed class Cursor { internal string? Id; internal int? Retry; internal bool Answered; }
    private readonly McpHttpBinding binding; private readonly McpAdmittedHttpRequestFactory createOperation; private readonly McpTransportLimits limits;
    private readonly TimeProvider clock; private readonly McpOperationSet operations = new(); private readonly object gate = new();
    private readonly Lazy<Task> close; private McpWireCallbacks? callbacks; private string? session, protocol; private Task? get;
    private readonly List<McpHttpOwnedReadAbort> ownedReadAborts = [];
    public IReadOnlyList<McpHttpOwnedReadAbort> OwnedReadAborts { get { lock (gate) return ownedReadAborts.ToArray(); } }
    public McpStreamableHttpTransport(McpHttpBinding binding, McpAdmittedHttpRequestFactory createOperation, McpTransportLimits? limits = null, TimeProvider? clock = null)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding)); this.createOperation = createOperation ?? throw new ArgumentNullException(nameof(createOperation));
        this.limits = limits ?? new(); this.clock = clock ?? TimeProvider.System; McpWireJson.Limits(this.limits);
        if (!binding.Endpoint.IsAbsoluteUri || binding.Endpoint.Scheme is not ("http" or "https") || binding.Headers is null ||
            binding.Profile is not { MaximumReconnects: >= 0 and <= 1000, InitialReconnectMilliseconds: >= 0, MaximumReconnectMilliseconds: > 0, DeleteTimeoutMilliseconds: > 0 }) throw new ArgumentException("Explicit finite MCP HTTP binding required.");
        long headerBytes = 0;
        foreach (var header in binding.Headers)
        {
            if (string.IsNullOrEmpty(header.Key) || header.Value is null || header.Key.Contains('\r') || header.Key.Contains('\n') || header.Value.Contains('\r') || header.Value.Contains('\n')) throw new ArgumentException("Invalid supplied MCP HTTP header");
            headerBytes += McpWireJson.Utf8.GetByteCount(header.Key) + (long)McpWireJson.Utf8.GetByteCount(header.Value);
            if (headerBytes > this.limits.MaximumFrameBytes) throw new ArgumentException("Supplied MCP HTTP header byte limit exceeded");
        }
        close = new(() => operations.CloseAsync(StopOwnedAsync, CloseTail), LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public Task StartAsync(McpWireCallbacks receiver, CancellationToken token)
    {
        RejectOwnerReentry(); return operations.RunAsync(_ =>
        { ArgumentNullException.ThrowIfNull(receiver); lock (gate) { if (callbacks is not null) throw new InvalidOperationException("MCP HTTP already started"); callbacks = receiver; } return Task.CompletedTask; }, token);
    }
    public Task SetProtocolVersionAsync(string version, CancellationToken token)
    { RejectOwnerReentry(); return operations.RunAsync(_ => { lock (gate) protocol = version; return Task.CompletedTask; }, token); }
    public Task SendAsync(JsonData message, CancellationToken token)
    {
        RejectOwnerReentry();
        var bytes = McpWireJson.Encode(message, limits.MaximumFrameBytes);
        return operations.RunAsync(async owned =>
        {
            McpWireCallbacks receiver; lock (gate) receiver = callbacks ?? throw new McpRuntimeDisconnectedException("MCP HTTP not started");
            using var request = Request(HttpMethod.Post, "application/json, text/event-stream");
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
            owned.ThrowIfCancellationRequested();
            var response = await ExchangeAsync(request, owned).ConfigureAwait(false);
            await OwnResponse(response, async lease =>
            {
                owned.ThrowIfCancellationRequested(); Status(response); Capture(response);
                var value = message.Value; var isRequest = value.TryGetProperty("method", out _) && value.TryGetProperty("id", out _);
                if (!isRequest)
                {
                    // Disposal cancels an ignored body without unnecessary body acquisition.
                    if (value.TryGetProperty("method", out var method) && method.GetString() == "notifications/initialized" && binding.Profile.OpenGetStream)
                        lock (gate) get ??= operations.RunAsync(lifetime => RunGetAsync(receiver, lifetime));
                    return true;
                }
                if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent) throw new McpRuntimeProtocolException("MCP request accepted without response");
                var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (media is not ("application/json" or "text/event-stream")) throw new McpRuntimeProtocolException("Unsupported MCP HTTP response content type");
                var body = await lease.BodyAsync(owned).ConfigureAwait(false); owned.ThrowIfCancellationRequested();
                if (media == "application/json")
                {
                    var payload = await ReadBody(body, owned).ConfigureAwait(false); var json = JsonData.Parse(McpWireJson.Utf8.GetString(payload));
                    if (json.Value.ValueKind == JsonValueKind.Array)
                        foreach (var item in json.Value.EnumerateArray()) await receiver.Receive(McpWireJson.Validate(JsonData.FromElement(item))).ConfigureAwait(false);
                    else await receiver.Receive(McpWireJson.Validate(json)).ConfigureAwait(false);
                    return true;
                }
                if (media != "text/event-stream") throw new McpRuntimeProtocolException("Unsupported MCP HTTP response content type");
                var cursor = new Cursor(); var id = McpWireJson.Key(value.GetProperty("id"));
                await ReadResponseAsync(body, receiver, cursor, id, owned).ConfigureAwait(false); return true;
            }).ConfigureAwait(false);
        }, token);
    }
    private HttpRequestMessage Request(HttpMethod method, string? accept = null, string? lastId = null)
    {
        var request = new HttpRequestMessage(method, binding.Endpoint);
        foreach (var header in binding.Headers) if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) { request.Dispose(); throw new ArgumentException("Unsupported supplied MCP HTTP header"); }
        if (accept is not null) { request.Headers.Remove("accept"); request.Headers.TryAddWithoutValidation("accept", accept); }
        lock (gate)
        {
            if (!string.IsNullOrEmpty(session)) { request.Headers.Remove("Mcp-Session-Id"); request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session); }
            if (!string.IsNullOrEmpty(protocol)) { request.Headers.Remove("MCP-Protocol-Version"); request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocol); }
        }
        if (lastId is not null) { request.Headers.Remove("Last-Event-ID"); request.Headers.TryAddWithoutValidation("Last-Event-ID", lastId); }
        return request;
    }
    private void Status(HttpResponseMessage response)
    { if (!response.IsSuccessStatusCode) { bool expired; lock (gate) expired = response.StatusCode == HttpStatusCode.NotFound && !string.IsNullOrEmpty(session); throw new McpHttpStatusException((int)response.StatusCode, expired); } }
    private void Capture(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("mcp-session-id", out var values)) return;
        var id = values.FirstOrDefault(); if (string.IsNullOrEmpty(id)) return;
        if (id.Length > limits.MaximumFrameBytes || id.Any(c => c < 0x21 || c > 0x7e)) throw new McpRuntimeProtocolException("Invalid MCP session header");
        lock (gate) session = id;
    }
    private async Task<byte[]> ReadBody(Stream body, CancellationToken token)
    {
        using var buffer = new MemoryStream(); var bytes = new byte[limits.ReadBufferBytes];
        while (true) { var count = await body.ReadAsync(bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); if (count == 0) return buffer.ToArray(); if (buffer.Length + count > limits.MaximumFrameBytes) throw new McpRuntimeProtocolException("MCP HTTP JSON byte limit exceeded"); buffer.Write(bytes, 0, count); }
    }
    private async Task ReadResponseAsync(Stream body, McpWireCallbacks receiver, Cursor cursor, string id, CancellationToken token)
    {
        Exception? failure = null;
        try { await Sse(body, receiver, cursor, id, token).ConfigureAwait(false); }
        catch (Exception error) when (!token.IsCancellationRequested && Retryable(error)) { failure = error; }
        for (var attempt = 0; !cursor.Answered && cursor.Id is not null && attempt < binding.Profile.MaximumReconnects; attempt++)
        {
            await Delay(attempt, cursor, token).ConfigureAwait(false);
            var cleanupFailed = false;
            try
            {
                using var request = Request(HttpMethod.Get, "text/event-stream", cursor.Id); var response = await ExchangeAsync(request, token).ConfigureAwait(false);
                await OwnResponse(response, async lease =>
                {
                    token.ThrowIfCancellationRequested(); Status(response); Capture(response);
                    if (response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() != "text/event-stream") throw new McpRuntimeProtocolException("Unsupported MCP resume content type");
                    var resumed = await lease.BodyAsync(token).ConfigureAwait(false);
                    await Sse(resumed, receiver, cursor, id, token).ConfigureAwait(false); return true;
                }, () => cleanupFailed = true).ConfigureAwait(false);
                if (cursor.Answered) failure = null;
            }
            catch (Exception error) when (!cleanupFailed && !cursor.Answered && !token.IsCancellationRequested && Retryable(error)) { failure = error; }
        }
        token.ThrowIfCancellationRequested();
        if (!cursor.Answered)
        {
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw new McpRuntimeProtocolException("MCP response stream ended without response");
        }
    }
    private async Task RunGetAsync(McpWireCallbacks receiver, CancellationToken token)
    {
        var cursor = new Cursor();
        Exception? failure = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var cleanupFailed = false;
                try
                {
                    using var request = Request(HttpMethod.Get, "text/event-stream", cursor.Id); var response = await ExchangeAsync(request, token).ConfigureAwait(false);
                    var supported = await OwnResponse(response, async lease =>
                    {
                        token.ThrowIfCancellationRequested(); if (response.StatusCode == HttpStatusCode.MethodNotAllowed) return false;
                        Status(response); Capture(response);
                        if (response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() != "text/event-stream") throw new McpRuntimeProtocolException("Unsupported MCP GET content type");
                        var body = await lease.BodyAsync(token).ConfigureAwait(false); await Sse(body, receiver, cursor, null, token, lease).ConfigureAwait(false); return true;
                    }, () => cleanupFailed = true).ConfigureAwait(false);
                    if (!supported) return;
                }
                catch (Exception error) when (!cleanupFailed && !token.IsCancellationRequested && Retryable(error)) { failure = error; }
                if (attempt >= binding.Profile.MaximumReconnects)
                {
                    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    throw new McpRuntimeDisconnectedException("MCP GET stream ended and reconnect bound reached");
                }
                await Delay(attempt, cursor, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { receiver.Disconnected(error); throw; }
    }
    private Task Delay(int attempt, Cursor cursor, CancellationToken token)
    {
        var delay = Math.Min(cursor.Retry ?? binding.Profile.InitialReconnectMilliseconds * Math.Pow(2, Math.Min(attempt, 30)), binding.Profile.MaximumReconnectMilliseconds);
        return Task.Delay(TimeSpan.FromMilliseconds(delay), clock, token);
    }
    private static bool Retryable(Exception error) => error is HttpRequestException || error is McpHttpStatusException status && (status.StatusCode is 408 or 429 || status.StatusCode >= 500) || error is IOException and not (McpRuntimeProtocolException or McpHttpStatusException or McpRuntimeDisconnectedException);
    private async Task Sse(Stream body, McpWireCallbacks receiver, Cursor cursor, string? responseId, CancellationToken token, ResponseLease? ownedGet = null)
    {
        var bytes = new byte[limits.ReadBufferBytes]; using var line = new MemoryStream(); var data = new StringBuilder(); string? eventName = null; var hasData = false; long dataBytes = 0; var firstLine = true;
        async Task Dispatch()
        {
            if (hasData && (eventName is null or "" or "message") && !string.IsNullOrWhiteSpace(data.ToString()))
            {
                var message = McpWireJson.Validate(JsonData.Parse(data.ToString()));
                await receiver.Receive(message).ConfigureAwait(false);
                var value = message.Value;
                if (responseId is not null && !value.TryGetProperty("method", out _) && value.TryGetProperty("id", out var id) && McpWireJson.Key(id) == responseId) cursor.Answered = true;
            }
            data.Clear(); eventName = null; hasData = false; dataBytes = 0;
        }
        async Task Line()
        {
            var raw = McpWireJson.Utf8.GetString(line.GetBuffer(), 0, (int)line.Length); line.SetLength(0); if (raw.EndsWith('\r')) raw = raw[..^1];
            if (firstLine) { firstLine = false; if (raw.StartsWith('\uFEFF')) raw = raw[1..]; }
            if (raw.Length == 0) { await Dispatch().ConfigureAwait(false); return; }
            if (raw[0] == ':') return;
            var colon = raw.IndexOf(':'); var field = colon < 0 ? raw : raw[..colon]; var value = colon < 0 ? "" : raw[(colon + 1)..]; if (value.StartsWith(' ')) value = value[1..];
            if (field == "data") { dataBytes += McpWireJson.Utf8.GetByteCount(value) + (hasData ? 1 : 0); if (dataBytes > limits.MaximumFrameBytes) throw new McpRuntimeProtocolException("MCP SSE event byte limit exceeded"); if (hasData) data.Append('\n'); data.Append(value); hasData = true; }
            else if (field == "event") eventName = value;
            else if (field == "id" && !value.Contains('\0')) cursor.Id = value;
            else if (field == "retry" && value.Length > 0 && value.All(c => c is >= '0' and <= '9') && int.TryParse(value, out var retry)) cursor.Retry = retry;
        }
        while (!cursor.Answered)
        {
            var count = ownedGet is null ? await body.ReadAsync(bytes, token).ConfigureAwait(false) :
                await ownedGet.ReadGetAsync(body, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (count == 0) { if (line.Length > 0) await Line().ConfigureAwait(false); await Dispatch().ConfigureAwait(false); return; }
            for (var index = 0; index < count; index++)
            {
                if (bytes[index] == 10) await Line().ConfigureAwait(false);
                else { if (line.Length >= limits.MaximumFrameBytes) throw new McpRuntimeProtocolException("MCP SSE line byte limit exceeded"); line.WriteByte(bytes[index]); }
            }
        }
    }
    private async Task CloseTail()
    {
        var errors = new List<Exception>(); Task? original; bool delete;
        lock (gate) { original = get; delete = callbacks is not null && !string.IsNullOrEmpty(session) && binding.Profile.DeleteSessionOnClose; }
        if (original is not null) try { await original.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (delete)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(binding.Profile.DeleteTimeoutMilliseconds), clock);
            try { using var request = Request(HttpMethod.Delete); var response = await ExchangeAsync(request, timeout.Token, closingDelete: true).ConfigureAwait(false); await OwnResponse(response, _ => { timeout.Token.ThrowIfCancellationRequested(); Status(response); return Task.FromResult(true); }).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        lock (gate) foreach (var error in cleanupFailures) Add(errors, error);
        Throw(errors);
    }
    private readonly AsyncLocal<bool> inOwnerCallback = new();
    private void RejectOwnerReentry()
    { if (inOwnerCallback.Value) throw new InvalidOperationException("MCP HTTP borrowed owner callback cannot reenter its transport lifecycle"); }
    public Task CloseAsync() { RejectOwnerReentry(); lock (gate) { stopping = true; return close.Value; } }
    private Task InvokeOwner(Func<Task> body) => Task.Run(async () =>
    {
        inOwnerCallback.Value = true; Task? original = null;
        try { original = body(); await original.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (original is { IsFaulted: true } && (error is OperationCanceledException || original.Exception!.InnerExceptions.Count > 1))
                throw new McpHttpOriginalException(original, original.Exception!);
            if (error is OperationCanceledException) throw new IOException("MCP HTTP cleanup cancellation.", error);
            throw;
        }
        finally { inOwnerCallback.Value = false; }
    });
    private async ValueTask<HttpResponseMessage> SendAdmittedAsync(IMcpAdmittedHttpRequestOperation operation, CancellationToken token)
    {
        inOwnerCallback.Value = true;
        try { return await operation.SendAsync(token).ConfigureAwait(false); }
        finally { inOwnerCallback.Value = false; }
    }
    private readonly HashSet<RequestOwner> owners = [];
    private readonly List<Exception> cleanupFailures = [];
    private bool stopping;

    /// <summary>The historical exchange alone cannot stop pending headers; reject it before effects.</summary>
    public McpStreamableHttpTransport(McpHttpBinding binding, McpHttpExchange exchange,
        McpTransportLimits? limits = null, TimeProvider? clock = null)
        : this(binding, (McpAdmittedHttpRequestFactory)(_ => throw new NotSupportedException()), limits, clock)
    { throw new NotSupportedException("MCP HTTP requires an admitted request-specific physical stop operation."); }

    private sealed class RequestOwner(McpStreamableHttpTransport host, IMcpAdmittedHttpRequestOperation operation)
    {
        internal readonly object Gate = new();
        internal ResponseLease? Response;
        internal CancellationTokenRegistration Cancellation;
        private Task? stop;
        internal Task BeginStop()
        {
            Task original;
            lock (Gate)
            {
                if (stop is not null) return stop;
                // Scheduling retains the actual original and prevents a synchronous admitted callback
                // from preventing initiation of the other request/response owners.
                stop = original = host.InvokeOwner(operation.StopAsync);
            }
            return original;
        }
        internal ValueTask<HttpResponseMessage> SendAsync(CancellationToken token) => host.SendAdmittedAsync(operation, token);
    }
    private sealed class ResponseLease(McpStreamableHttpTransport host, HttpResponseMessage response)
    {
        private readonly object gate = new(); private bool stopping;
        private Stream? body; private Task? disposeResponse, disposeBody;
        private Task<int>? activeRead;
        private Task<int>? stoppedRead;
        internal HttpResponseMessage Response => response;
        internal Task[] BeginStop()
        {
            lock (gate)
            {
                stopping = true;
                if (activeRead is { IsCompleted: false }) stoppedRead = activeRead;
                disposeResponse ??= host.InvokeOwner(() => { response.Dispose(); return Task.CompletedTask; });
                if (body is not null) disposeBody ??= DisposeBody(body);
                return disposeBody is null ? [disposeResponse] : [disposeResponse, disposeBody];
            }
        }
        private Task DisposeBody(Stream owned) => host.InvokeOwner(() => owned.DisposeAsync().AsTask());
        internal async Task<int> ReadGetAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
        {
            Task<int> original;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                try { original = stream.ReadAsync(buffer, token).AsTask(); }
                catch (OperationCanceledException error) { throw new IOException("Synchronous MCP GET read fault.", error); }
                activeRead = original;
            }
            try { return await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                bool stopped; lock (gate) stopped = ReferenceEquals(stoppedRead, original);
                bool ownerStopping; lock (host.gate) ownerStopping = host.stopping;
                // Only this outstanding GET read, stopped by this response owner, can acknowledge
                // the observed Windows operation-aborted shape. No unrelated IO or sibling fault.
                if (stopped && ownerStopping && token.IsCancellationRequested && original.IsFaulted &&
                    original.Exception!.InnerExceptions.Count == 1 && error is IOException { InnerException: SocketException socket } &&
                    socket.NativeErrorCode == 995 && socket.SocketErrorCode == SocketError.OperationAborted)
                {
                    lock (host.gate) host.ownedReadAborts.Add(new(original, error));
                    return 0;
                }
                if (original.IsCanceled && token.IsCancellationRequested && error is OperationCanceledException cancellation && cancellation.CancellationToken == token) throw;
                if (error is OperationCanceledException || original.Exception?.InnerExceptions.Count > 1)
                    throw new McpHttpOriginalException(original, (Exception?)original.Exception ?? error);
                throw;
            }
            finally { lock (gate) if (ReferenceEquals(activeRead, original)) activeRead = null; }
        }
        internal async Task<Stream> BodyAsync(CancellationToken token)
        {
            // No canceled-await abandonment: ownership transfers only after the acquisition original.
            var acquired = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            lock (gate) { body = acquired; if (stopping) disposeBody ??= DisposeBody(acquired); }
            return acquired;
        }
    }
    private async Task<HttpResponseMessage> ExchangeAsync(HttpRequestMessage request, CancellationToken token, bool closingDelete = false)
    {
        RequestOwner owner;
        lock (gate)
        {
            if (stopping && !closingDelete || cleanupFailures.Count != 0) throw new McpRuntimeDisconnectedException("MCP HTTP owner stopped or cleanup failed");
            if (owners.Count >= limits.MaximumInflight) throw new InvalidOperationException("MCP HTTP request owner limit exceeded");
            // Creation is contractually effect-free, and owner admission precedes SendAsync effects.
            inOwnerCallback.Value = true;
            try { owner = new(this, createOperation(request) ?? throw new InvalidOperationException("Missing admitted HTTP operation")); }
            finally { inOwnerCallback.Value = false; }
            owners.Add(owner);
        }
        owner.Cancellation = token.Register(() =>
        {
            ResponseLease? transferred; lock (gate) transferred = owner.Response;
            transferred?.BeginStop();
            owner.BeginStop();
        });
        try
        {
            var response = await owner.SendAsync(token).ConfigureAwait(false);
            bool stopNow;
            lock (gate) { owner.Response = new(this, response); stopNow = token.IsCancellationRequested || stopping && !closingDelete; }
            if (stopNow) owner.Response.BeginStop();
            return response;
        }
        catch (Exception original)
        {
            var errors = new List<Exception> { original };
            try { await owner.BeginStop().ConfigureAwait(false); } catch (Exception error) { RememberCleanup(error); Add(errors, error); }
            owner.Cancellation.Dispose();
            lock (gate) owners.Remove(owner);
            Throw(errors); throw;
        }
    }
    private async Task StopOwnedAsync()
    {
        RequestOwner[] admitted;
        lock (gate) { stopping = true; admitted = owners.ToArray(); }
        var originals = new List<Task>();
        // Start every physical stop/disposal before joining any original. A late response starts its
        // own disposal at transfer and remains joined by its admitted operation settlement slot.
        foreach (var owner in admitted)
        {
            ResponseLease? response; lock (gate) response = owner.Response;
            if (response is not null) originals.AddRange(response.BeginStop());
            originals.Add(owner.BeginStop());
        }
        var errors = new List<Exception>();
        foreach (var original in originals) try { await original.ConfigureAwait(false); } catch (Exception error) { RememberCleanup(error); Add(errors, error); }
        // Cleanup faults are owned by CloseTail after all late operation slots have settled.
    }
    private void RememberCleanup(Exception error)
    { lock (gate) if (!cleanupFailures.Any(existing => ReferenceEquals(existing, error))) cleanupFailures.Add(error); }
    private static void Add(List<Exception> errors, Exception error)
    { if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error); }
    private static void Throw(List<Exception> errors)
    {
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }
    private async Task<T> OwnResponse<T>(HttpResponseMessage response, Func<ResponseLease, Task<T>> run, Action? cleanupFailed = null)
    {
        RequestOwner owner; ResponseLease lease;
        lock (gate) { owner = owners.Single(item => ReferenceEquals(item.Response?.Response, response)); lease = owner.Response!; }
        var errors = new List<Exception>(); T result = default!;
        try { result = await run(lease).ConfigureAwait(false); } catch (Exception error) { Add(errors, error); }
        var stop = owner.BeginStop(); var disposals = lease.BeginStop();
        foreach (var original in disposals.Append(stop))
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { cleanupFailed?.Invoke(); RememberCleanup(error); Add(errors, error); }
        owner.Cancellation.Dispose();
        lock (gate) owners.Remove(owner);
        Throw(errors); return result;
    }
}
