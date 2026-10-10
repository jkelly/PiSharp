// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openai-codex-responses.ts (the WebSocket transport of stream:
// the response.create frame, connectWebSocket, acquireWebSocket and the per-session connection cache with its idle and age limits,
// parseWebSocket, the cached-context continuation with previous_response_id, the retries for websocket_connection_limit_reached and
// previous_response_not_found, the SSE fallback with its provider_transport_failure diagnostic, and the debug stats) and
// utils/diagnostics.ts. Built on System.Net.WebSockets.ClientWebSocket.
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICodexResponses;

/// <summary>Source OpenAICodexWebSocketDebugStats for one session.</summary>
public sealed record OpenAICodexWebSocketDebugStats(int Requests, int ConnectionsCreated, int ConnectionsReused, int CachedContextRequests,
    int StoreTrueRequests, int FullContextRequests, int DeltaRequests, int LastInputItems, int? LastDeltaInputItems, string? LastPreviousResponseId,
    int WebSocketFailures, int SseFallbacks, bool? WebSocketFallbackActive, string? LastWebSocketError);

/// <summary>Source getOpenAICodexWebSocketDebugStats, resetOpenAICodexWebSocketDebugStats and closeOpenAICodexWebSocketSessions (the
/// session resource cleanup): the process-wide Codex WebSocket connection cache, keyed by session and account.</summary>
public static class OpenAICodexWebSockets
{
    internal static readonly object Gate = new();
    internal static readonly Dictionary<string, Dictionary<string, CachedConnection>> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Stats> DebugStats = new(StringComparer.Ordinal);
    private static readonly HashSet<string> FallbackSessions = new(StringComparer.Ordinal);

    internal sealed class Stats
    {
        public int Requests, ConnectionsCreated, ConnectionsReused, CachedContextRequests, StoreTrueRequests, FullContextRequests, DeltaRequests,
            LastInputItems, WebSocketFailures, SseFallbacks;
        public int? LastDeltaInputItems;
        public string? LastPreviousResponseId, LastWebSocketError;
        public bool? WebSocketFallbackActive;
    }

    public static OpenAICodexWebSocketDebugStats? GetDebugStats(string sessionId)
    {
        lock (Gate)
            return DebugStats.TryGetValue(sessionId, out var s) ? new(s.Requests, s.ConnectionsCreated, s.ConnectionsReused, s.CachedContextRequests,
                s.StoreTrueRequests, s.FullContextRequests, s.DeltaRequests, s.LastInputItems, s.LastDeltaInputItems, s.LastPreviousResponseId,
                s.WebSocketFailures, s.SseFallbacks, s.WebSocketFallbackActive, s.LastWebSocketError) : null;
    }

    /// <summary>Clears one session's stats and SSE fallback (every session's without an id).</summary>
    public static void ResetDebugStats(string? sessionId = null)
    {
        lock (Gate)
        {
            if (sessionId is not null) { DebugStats.Remove(sessionId); FallbackSessions.Remove(sessionId); return; }
            DebugStats.Clear(); FallbackSessions.Clear();
        }
    }

    /// <summary>Closes one session's cached connections (every session's without an id) with 1000 "debug_close".</summary>
    public static void CloseSessions(string? sessionId = null)
    {
        List<CachedConnection> closing;
        lock (Gate)
        {
            if (sessionId is not null)
            {
                closing = Cache.TryGetValue(sessionId, out var entries) ? [.. entries.Values] : [];
                Cache.Remove(sessionId);
            }
            else { closing = [.. Cache.Values.SelectMany(entries => entries.Values)]; Cache.Clear(); }
        }
        foreach (var entry in closing) { entry.IdleTimer?.Dispose(); CloseSilently(entry.Socket, "debug_close"); }
    }

    internal static Stats StatsFor(string sessionId)
    {
        if (!DebugStats.TryGetValue(sessionId, out var stats)) DebugStats[sessionId] = stats = new();
        return stats;
    }

    internal static bool IsFallbackActive(string? sessionId) { lock (Gate) return sessionId is not null && FallbackSessions.Contains(sessionId); }

    internal static void RecordSseFallback(string? sessionId)
    {
        if (sessionId is null) return;
        lock (Gate) { var stats = StatsFor(sessionId); stats.SseFallbacks++; stats.WebSocketFallbackActive = FallbackSessions.Contains(sessionId); }
    }

    internal static void RecordFailure(string? sessionId, Exception error)
    {
        if (sessionId is null) return;
        lock (Gate)
        {
            FallbackSessions.Add(sessionId);
            var stats = StatsFor(sessionId); stats.WebSocketFailures++; stats.LastWebSocketError = error.Message; stats.WebSocketFallbackActive = true;
        }
    }

    /// <summary>closeWebSocketSilently: starts the close handshake and lets it finish (or abort) in the background.</summary>
    internal static void CloseSilently(WebSocket socket, string reason)
    {
        if (!Closing.TryAdd(socket, reason)) return; // The first close's code and reason stand.
        _ = Task.Run(async () =>
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, limit.Token).ConfigureAwait(false);
                    var buffer = new byte[1024];
                    while (socket.State == WebSocketState.CloseSent)
                        if ((await socket.ReceiveAsync(buffer, limit.Token).ConfigureAwait(false)).MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException) { }
            finally { socket.Dispose(); }
        });
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, string> Closing = new();
}

/// <summary>CachedWebSocketConnection: the socket, whether a request owns it, when it opened, its idle timer and the continuation state.</summary>
internal sealed class CachedConnection(WebSocket socket, long createdAt)
{
    public WebSocket Socket { get; } = socket;
    public long CreatedAt { get; } = createdAt;
    public bool Busy;
    public ITimer? IdleTimer;
    public CachedContinuation? Continuation;
}

/// <summary>CachedWebSocketContinuationState: the last full request body, its response id and the response's own input items.</summary>
internal sealed record CachedContinuation(JsonObject LastRequestBody, string LastResponseId, JsonArray LastResponseItems);

/// <summary>An acquired socket: the cache entry (null for an uncached connection) and the release that keeps or closes it.</summary>
internal sealed class WebSocketLease(WebSocket socket, CachedConnection? entry, bool reused, Action<bool> release)
{
    private int _released;
    public WebSocket Socket { get; } = socket;
    public CachedConnection? Entry { get; } = entry;
    public bool Reused { get; } = reused;
    public void Release(bool keep) { if (Interlocked.Exchange(ref _released, 1) == 0) release(keep); }
}

/// <summary>Source WebSocketCloseError: "WebSocket closed &lt;code&gt; &lt;reason&gt;" (1009 without a reason: "message too big").</summary>
public sealed class CodexWebSocketCloseException(string message, int? code, string? reason) : Exception(message)
{
    public int? Code { get; } = code;
    public string? Reason { get; } = reason;
}

public sealed partial class OpenAICodexResponsesTransport
{
    private const string OpenAIBetaResponsesWebSockets = "responses_websockets=2026-02-06";
    private const string ConnectionLimitReached = "websocket_connection_limit_reached";
    private const string PreviousResponseNotFound = "previous_response_not_found";
    private static readonly TimeSpan SessionCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SessionMaxAge = TimeSpan.FromMinutes(55);
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(15_000);

    private TimeProvider Clock => _options.Time ?? TimeProvider.System;

    /// <summary>resolveCodexWebSocketUrl: the Codex responses URL with https as wss and http as ws.</summary>
    public static Uri ResolveWebSocketUrl(string? baseUrl)
    {
        var builder = new UriBuilder(ResolveUrl(baseUrl));
        builder.Scheme = builder.Scheme switch { "https" => "wss", "http" => "ws", var other => other };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>isCodexNonTransportError: Codex API and protocol errors and hook failures are never retried over SSE.</summary>
    private static bool IsNonTransport(Exception error) => error is CodexApiException or CodexProtocolException or ProviderStreamEventCallbackException;

    /// <summary>The stream's WebSocket attempt: the response.create frame on an acquired connection until the first Codex event, retried
    /// once for websocket_connection_limit_reached before any event and once per stream for previous_response_not_found (also after
    /// events, <see cref="RetryMissingContinuationAsync"/>). A transport failure before the first event records a
    /// provider_transport_failure diagnostic and returns null (the SSE fallback); Codex errors, hook failures and aborts propagate.</summary>
    private async Task<IAsyncEnumerator<JsonData>?> TryWebSocketAsync(JsonObject body, string token, string accountId, string? cacheSessionId,
        string transport, Invocation invocation, CancellationToken cancellation)
    {
        var requestId = ClampCacheKey(cacheSessionId) ?? Guid.CreateVersion7().ToString("D");
        var headers = BuildWebSocketHeaders(token, requestId);
        var url = ResolveWebSocketUrl(_metadata.TryGetProperty("baseUrl", out var baseUrl) && baseUrl.ValueKind == JsonValueKind.String ? baseUrl.GetString() : null);
        var retriedConnectionLimit = false;
        while (true)
        {
            WebSocketLease? lease = null;
            try
            {
                lease = await AcquireAsync(url, headers, cacheSessionId, accountId, cancellation).ConfigureAwait(false);
                invocation.UseCachedContext = transport is "websocket-cached" or "auto";
                invocation.FullBody = body;
                var requestBody = invocation.UseCachedContext && lease.Entry is { } entry ? CachedRequestBody(entry, body) : body;
                RecordRequest(cacheSessionId, lease.Reused, invocation.UseCachedContext, requestBody);
                var frame = new JsonObject { ["type"] = "response.create" };
                foreach (var (name, value) in requestBody) frame[name] = value?.DeepClone();
                await lease.Socket.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString(BodyJson)), WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false);
                var events = MapWebSocketEventsAsync(lease.Socket, invocation, cancellation).GetAsyncEnumerator(cancellation);
                bool first;
                try { first = await events.MoveNextAsync().ConfigureAwait(false); }
                catch { await events.DisposeAsync().ConfigureAwait(false); throw; }
                invocation.Lease = lease; invocation.WebSocketStarted = first;
                return new PrefetchedEnumerator(events, first);
            }
            catch (Exception error)
            {
                if (lease is not null) { if (lease.Entry is { } entry) entry.Continuation = null; lease.Release(false); }
                var aborted = cancellation.IsCancellationRequested;
                var connectionLimit = error is CodexApiException { Code: ConnectionLimitReached };
                if (!aborted && error is CodexApiException { Code: PreviousResponseNotFound } && !invocation.RetriedMissingContinuation)
                { invocation.RetriedMissingContinuation = true; continue; }
                if (!aborted && connectionLimit && !retriedConnectionLimit) { retriedConnectionLimit = true; continue; }
                if (aborted || IsNonTransport(error) && !connectionLimit)
                {
                    if (aborted && error is not OperationCanceledException) throw new OperationCanceledException("Request was aborted", error, cancellation);
                    throw;
                }
                AppendDiagnostic(invocation, error, started: false);
                OpenAICodexWebSockets.RecordFailure(cacheSessionId, error);
                OpenAICodexWebSockets.RecordSseFallback(cacheSessionId);
                return null;
            }
        }
    }

    /// <summary>stream's retry of previous_response_not_found, which also applies once events were emitted: processWebSocketStream's
    /// failure drops the cached continuation and closes the connection, and the attempt runs again with the full body (its events
    /// continue the same output); a transport failure before the retry's first event falls back to SSE.</summary>
    private static async IAsyncEnumerable<JsonData> RetryMissingContinuationAsync(IAsyncEnumerator<JsonData> events, Invocation invocation,
        Func<ValueTask<IAsyncEnumerator<JsonData>>> retry, [EnumeratorCancellation] CancellationToken cancellation)
    {
        IAsyncEnumerator<JsonData>? current = events;
        try
        {
            while (true)
            {
                bool moved;
                try { moved = await current.MoveNextAsync().ConfigureAwait(false); }
                catch (CodexApiException error) when (error.Code == PreviousResponseNotFound && !cancellation.IsCancellationRequested && !invocation.RetriedMissingContinuation)
                {
                    invocation.RetriedMissingContinuation = true;
                    var failed = current; current = null;
                    await failed.DisposeAsync().ConfigureAwait(false);
                    if (invocation.Lease is { } lease) { if (lease.Entry is { } entry) entry.Continuation = null; lease.Release(false); }
                    invocation.Lease = null; invocation.WebSocketStarted = false;
                    current = await retry().ConfigureAwait(false);
                    continue;
                }
                if (!moved) yield break;
                yield return current.Current;
            }
        }
        finally { if (current is not null) await current.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>The first event, already read, then the rest.</summary>
    private sealed class PrefetchedEnumerator(IAsyncEnumerator<JsonData> inner, bool hasFirst) : IAsyncEnumerator<JsonData>
    {
        private bool _pending = hasFirst, _ended = !hasFirst;
        public JsonData Current => inner.Current;
        public async ValueTask<bool> MoveNextAsync()
        {
            if (_pending) { _pending = false; return true; }
            if (_ended) return false;
            return await inner.MoveNextAsync().ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>createAssistantMessageDiagnostic("provider_transport_failure", error, details): the WebSocket failure, whether events had
    /// already been emitted and, before any, the SSE fallback. The error carries its name, message and code (no stack).</summary>
    private void AppendDiagnostic(Invocation invocation, Exception error, bool started)
    {
        var info = new JsonObject { ["name"] = error is CodexWebSocketCloseException ? "WebSocketCloseError" : "Error", ["message"] = error.Message };
        if (error is CodexWebSocketCloseException { Code: { } code }) info["code"] = code;
        var details = new JsonObject { ["configuredTransport"] = invocation.ConfiguredTransport };
        if (!started) details["fallbackTransport"] = "sse";
        details["eventsEmitted"] = started;
        details["phase"] = started ? "after_message_stream_start" : "before_message_stream_start";
        details["requestBytes"] = invocation.RequestBytes;
        invocation.Diagnostics.Add(new JsonObject
        {
            ["type"] = "provider_transport_failure", ["timestamp"] = Clock.GetUtcNow().ToUnixTimeMilliseconds(), ["error"] = info, ["details"] = details
        });
    }

    private static void RecordRequest(string? sessionId, bool reused, bool cachedContext, JsonObject requestBody)
    {
        if (sessionId is null) return;
        lock (OpenAICodexWebSockets.Gate)
        {
            var stats = OpenAICodexWebSockets.StatsFor(sessionId);
            stats.Requests++;
            if (reused) stats.ConnectionsReused++; else stats.ConnectionsCreated++;
            if (cachedContext) stats.CachedContextRequests++;
            if (requestBody["store"] is JsonValue store && store.TryGetValue<bool>(out var stored) && stored) stats.StoreTrueRequests++;
            var items = (requestBody["input"] as JsonArray)?.Count ?? 0;
            stats.LastInputItems = items;
            if (requestBody["previous_response_id"] is JsonValue previous && previous.TryGetValue<string>(out var id) && id.Length != 0)
            { stats.DeltaRequests++; stats.LastDeltaInputItems = items; stats.LastPreviousResponseId = id; }
            else { stats.FullContextRequests++; stats.LastDeltaInputItems = null; stats.LastPreviousResponseId = null; }
        }
    }

    /// <summary>buildCachedWebSocketRequestBody: with an unchanged body apart from its input, and the input extending the last request's
    /// input plus the last response's items, only the new items go with previous_response_id; otherwise the continuation is dropped.</summary>
    private static JsonObject CachedRequestBody(CachedConnection entry, JsonObject body)
    {
        if (entry.Continuation is not { } continuation) return body;
        var delta = InputDelta(body, continuation);
        if (delta is null || continuation.LastResponseId.Length == 0) { entry.Continuation = null; return body; }
        var result = (JsonObject)body.DeepClone();
        result["previous_response_id"] = continuation.LastResponseId;
        result["input"] = delta;
        return result;
    }

    private static JsonArray? InputDelta(JsonObject body, CachedContinuation continuation)
    {
        static string WithoutInput(JsonObject value)
        {
            var copy = (JsonObject)value.DeepClone(); copy.Remove("input"); copy.Remove("previous_response_id");
            return copy.ToJsonString(BodyJson);
        }
        if (WithoutInput(body) != WithoutInput(continuation.LastRequestBody)) return null;
        var current = body["input"] as JsonArray ?? [];
        var baseline = new JsonArray([.. (continuation.LastRequestBody["input"] as JsonArray ?? []).Select(item => item?.DeepClone()),
            .. continuation.LastResponseItems.Select(item => item?.DeepClone())]);
        if (current.Count < baseline.Count) return null;
        var prefix = new JsonArray([.. current.Take(baseline.Count).Select(item => item?.DeepClone())]);
        if (prefix.ToJsonString(BodyJson) != baseline.ToJsonString(BodyJson)) return null;
        return new JsonArray([.. current.Skip(baseline.Count).Select(item => item?.DeepClone())]);
    }

    /// <summary>The response's own items for the next delta: the assistant message projected as request input (no system prompt), less
    /// any tool outputs.</summary>
    private JsonArray ResponseItems(AssistantMessage message)
    {
        var projector = new ResponsesTranscriptProjector(ProjectionOptions());
        var request = new ChatRequest(_model, [new TranscriptEntry("assistant", PiWireJson.WriteMessage(message))]);
        var items = JsonNode.Parse(projector.ProjectInput(request, CancellationToken.None).ToString(), documentOptions: JsonData.DocumentOptions) as JsonArray ?? [];
        return new JsonArray([.. items.Where(item => item?["type"] is not JsonValue type || !type.TryGetValue<string>(out var name) ||
            name is not ("function_call_output" or "custom_tool_call_output")).Select(item => item?.DeepClone())]);
    }

    /// <summary>acquireWebSocket: without a session a fresh connection closed after the request. With one, the cached connection of the
    /// session and account when it is idle, open and younger than 55 minutes; a busy one gets a fresh uncached connection; otherwise
    /// a new connection replaces it in the cache. A kept connection closes after five idle minutes.</summary>
    private async Task<WebSocketLease> AcquireAsync(Uri url, List<KeyValuePair<string, string>> headers, string? sessionId, string accountId,
        CancellationToken cancellation)
    {
        if (sessionId is null)
        {
            var socket = await ConnectAsync(url, headers, cancellation).ConfigureAwait(false);
            return new(socket, null, false, _ => OpenAICodexWebSockets.CloseSilently(socket, "done"));
        }
        var connectUncached = false;
        lock (OpenAICodexWebSockets.Gate)
        {
            if (OpenAICodexWebSockets.Cache.TryGetValue(sessionId, out var entries) && entries.TryGetValue(accountId, out var cached))
            {
                cached.IdleTimer?.Dispose(); cached.IdleTimer = null;
                if (!cached.Busy && Clock.GetUtcNow().ToUnixTimeMilliseconds() - cached.CreatedAt >= (long)SessionMaxAge.TotalMilliseconds)
                {
                    OpenAICodexWebSockets.CloseSilently(cached.Socket, "connection_age_limit");
                    Remove(sessionId, accountId, cached);
                }
                else if (!cached.Busy && cached.Socket.State == WebSocketState.Open)
                {
                    cached.Busy = true;
                    return new(cached.Socket, cached, true, keep => Settle(sessionId, accountId, cached, keep));
                }
                else if (cached.Busy) connectUncached = true;
                else
                {
                    OpenAICodexWebSockets.CloseSilently(cached.Socket, "done");
                    Remove(sessionId, accountId, cached);
                }
            }
        }
        if (connectUncached)
        {
            var socket = await ConnectAsync(url, headers, cancellation).ConfigureAwait(false);
            return new(socket, null, false, _ => OpenAICodexWebSockets.CloseSilently(socket, "done"));
        }
        var created = await ConnectAsync(url, headers, cancellation).ConfigureAwait(false);
        var entry = new CachedConnection(created, Clock.GetUtcNow().ToUnixTimeMilliseconds()) { Busy = true };
        lock (OpenAICodexWebSockets.Gate)
        {
            if (!OpenAICodexWebSockets.Cache.TryGetValue(sessionId, out var entries)) OpenAICodexWebSockets.Cache[sessionId] = entries = new(StringComparer.Ordinal);
            entries[accountId] = entry;
        }
        return new(created, entry, false, keep => Settle(sessionId, accountId, entry, keep));
    }

    /// <summary>The release of a cached connection: closed and uncached unless kept and still open; a kept one idles for five minutes.</summary>
    private void Settle(string sessionId, string accountId, CachedConnection entry, bool keep)
    {
        lock (OpenAICodexWebSockets.Gate)
        {
            if (!keep || entry.Socket.State != WebSocketState.Open)
            {
                OpenAICodexWebSockets.CloseSilently(entry.Socket, "done");
                entry.IdleTimer?.Dispose(); entry.IdleTimer = null;
                Remove(sessionId, accountId, entry);
                return;
            }
            entry.Busy = false;
            entry.IdleTimer?.Dispose();
            entry.IdleTimer = Clock.CreateTimer(_ =>
            {
                lock (OpenAICodexWebSockets.Gate)
                {
                    if (entry.Busy) return;
                    OpenAICodexWebSockets.CloseSilently(entry.Socket, "idle_timeout");
                    Remove(sessionId, accountId, entry);
                }
            }, null, SessionCacheTtl, Timeout.InfiniteTimeSpan);
        }
    }

    private static void Remove(string sessionId, string accountId, CachedConnection entry)
    {
        if (!OpenAICodexWebSockets.Cache.TryGetValue(sessionId, out var entries)) return;
        if (entries.TryGetValue(accountId, out var current) && ReferenceEquals(current, entry)) entries.Remove(accountId);
        if (entries.Count == 0) OpenAICodexWebSockets.Cache.Remove(sessionId);
    }

    /// <summary>connectWebSocket: the handshake with the Codex headers (permessage-deflate offered and no keep-alive pings, as Node's
    /// WebSocket), within the connect timeout ("WebSocket connect timeout after Nms").</summary>
    private async Task<WebSocket> ConnectAsync(Uri url, List<KeyValuePair<string, string>> headers, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        try
        {
            foreach (var (name, value) in headers) socket.Options.SetRequestHeader(name, value);
            socket.Options.KeepAliveInterval = TimeSpan.Zero;
            socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
            var limit = _options.WebSocketConnectTimeout ?? DefaultConnectTimeout;
            using var timeout = limit > TimeSpan.Zero ? new CancellationTokenSource(limit, Clock) : null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout?.Token ?? CancellationToken.None);
            try { await socket.ConnectAsync(url, _options.WebSocketInvoker ?? _client, linked.Token).ConfigureAwait(false); }
            catch (Exception) when (timeout?.IsCancellationRequested == true && !cancellation.IsCancellationRequested)
            { throw new InvalidOperationException($"WebSocket connect timeout after {(long)limit.TotalMilliseconds}ms"); }
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>parseWebSocket through mapCodexEvents: each text (or binary) message as UTF-8 JSON, until the terminal event. A close
    /// before it is "WebSocket closed &lt;code&gt; &lt;reason&gt;"; an idle wait longer than timeoutMs is "WebSocket idle timeout after Nms"
    /// (the socket closes with 1000 "idle_timeout"); invalid JSON is a Codex protocol error.</summary>
    private async IAsyncEnumerable<JsonData> MapWebSocketEventsAsync(WebSocket socket, Invocation invocation, [EnumeratorCancellation] CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        var idle = _options.Timeout is { } limit && limit > TimeSpan.Zero ? limit : (TimeSpan?)null;
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            // The idle wait covers the whole next message; it closes the socket gracefully (a cancelled receive would abort it).
            using (var waiting = new CancellationTokenSource())
            {
                var idleElapsed = idle is { } wait ? Task.Delay(wait, Clock, waiting.Token) : null;
                do
                {
                    var receive = socket.ReceiveAsync(buffer, cancellation);
                    if (idleElapsed is not null && await Task.WhenAny(receive, idleElapsed).ConfigureAwait(false) == idleElapsed)
                    {
                        _ = receive.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
                        OpenAICodexWebSockets.CloseSilently(socket, "idle_timeout");
                        throw new InvalidOperationException($"WebSocket idle timeout after {(long)idle!.Value.TotalMilliseconds}ms");
                    }
                    received = await receive.ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (message.Length + received.Count > MaximumWebSocketMessageBytes) throw new CodexProtocolException("Invalid Codex WebSocket JSON: message too large");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                waiting.Cancel();
            }
            if (received.MessageType == WebSocketMessageType.Close)
            {
                var code = received.CloseStatus is { } status ? (int)status : 1005;
                var reason = received.CloseStatusDescription is { Length: > 0 } description ? description : null;
                throw new CodexWebSocketCloseException($"WebSocket closed {code}{(reason is not null ? " " + reason : code == 1009 ? " message too big" : "")}", code, reason);
            }
            if (message.Length == 0) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            JsonNode? parsed;
            try { parsed = JsonUtf16.MutableNode(text); }
            catch (JsonException error) { throw new CodexProtocolException("Invalid Codex WebSocket JSON: " + error.Message); }
            if (parsed is not JsonObject value) continue;
            var (mapped, completed) = await MapEventAsync(value, invocation, cancellation).ConfigureAwait(false);
            if (mapped is { } next) yield return next;
            if (completed) yield break;
        }
    }

    private const int MaximumWebSocketMessageBytes = 64 * 1_048_576;
}
