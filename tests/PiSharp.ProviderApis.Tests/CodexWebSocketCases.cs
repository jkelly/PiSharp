using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICodexResponses;
using PiSharp.Contracts;

/// <summary>openai-codex-responses.ts WebSocket transport against an in-process loopback server (raw TCP: HTTP for the SSE fallback and
/// the RFC 6455 upgrade, then System.Net.WebSockets on the server side). Authored expectations, read from the pinned source.</summary>
internal static partial class Program
{
    private static readonly (string Id, Func<Task> Run)[] CodexWebSocketCases =
    [
        ("codex-ws.handshake-headers-and-response-create-frame", CodexWebSocketHandshake),
        ("codex-ws.session-connection-reuse-and-previous-response-id-delta", CodexWebSocketContinuation),
        ("codex-ws.sse-fallback-before-the-first-event-and-for-the-session", CodexWebSocketFallback),
        ("codex-ws.failures-after-start-retries-and-codex-errors", CodexWebSocketFailures),
        ("codex-ws.connect-and-idle-timeouts-and-connection-age-limit", CodexWebSocketTimeouts),
        ("codex-ws.previous-response-not-found-after-events-retries-the-full-context", CodexWebSocketMissingContinuationAfterStart),
        ("codex-ws.session-dispose-closes-the-session-connections", CodexWebSocketSessionDispose),
    ];

    private static readonly string[] CodexCompleted =
    [
        """{"type":"response.created","response":{"id":"resp-1","status":"in_progress"}}""",
        """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg-1","role":"assistant","content":[]}}""",
        """{"type":"response.output_text.delta","output_index":0,"item_id":"msg-1","delta":"Hello"}""",
        """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg-1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello","annotations":[]}]}}""",
        """{"type":"response.completed","response":{"id":"resp-1","status":"completed","output":[],"usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12}}}"""
    ];

    private static (OpenAICodexResponsesTransport Transport, ModelDescriptor Model, FixedTime Time) CodexOver(CodexServer server, string transport,
        string? sessionId = null, OpenAICodexResponsesOptions? options = null, HttpMessageInvoker? invoker = null)
    {
        var row = CatalogRow("openai-codex", "gpt-5.5");
        var raw = (JsonObject)JsonNode.Parse(row.Raw.ToString())!; raw["baseUrl"] = server.BaseUrl;
        var time = new FixedTime(1_800_000_000_000);
        options = (options ?? new()) with { SessionId = sessionId, Transport = () => transport, Time = time, WebSocketInvoker = invoker };
        var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
        return (new OpenAICodexResponsesTransport(new HttpClient(new SocketsHttpHandler { UseProxy = false }), model, JsonData.Parse(raw.ToJsonString()), options,
            _ => ValueTask.FromResult(CodexToken)), model, time);
    }

    private static ChatRequest Hi(ModelDescriptor model) => new(model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 3);

    private static JsonArray? Diagnostics(StreamEvent terminal) =>
        ((StreamTerminalEvent)terminal).Message.ExtraProperties is { } properties && properties.TryGet("diagnostics", out var value)
            ? (JsonArray)JsonNode.Parse(value!.ToString())! : null;

    private static async Task CodexWebSocketHandshake()
    {
        await using var server = new CodexServer();
        server.OnFrame = (connection, _) => connection.SendAllAsync(CodexCompleted);
        var (transport, model, _) = CodexOver(server, "auto", "sess-ws-handshake");
        try
        {
            var events = await Collect(transport, Hi(model));
            var done = events[^1] as StreamDone ?? throw new CheckException("done: " + ErrorMessage(events[^1]));
            Check(done.Message.Content.Single() is TextContent { Text: "Hello" }, "text over the WebSocket");
            Check(Diagnostics(done) is null, "no diagnostics");
            Check(server.Posts.IsEmpty, "no SSE request");
            var handshake = server.Upgrades.Single();
            Equal("GET /backend-api/codex/responses", handshake.RequestLine[..handshake.RequestLine.LastIndexOf(' ')], "upgrade target");
            Check(handshake.Header("authorization") == "Bearer " + CodexToken && handshake.Header("chatgpt-account-id") == "acct-123" &&
                handshake.Header("openai-beta") == "responses_websockets=2026-02-06" && handshake.Header("session-id") == "sess-ws-handshake" &&
                handshake.Header("x-client-request-id") == "sess-ws-handshake" && handshake.Header("originator") == "pi" &&
                handshake.Header("user-agent") == "pi/1.1.0" && handshake.Header("accept") is null && handshake.Header("content-type") is null &&
                handshake.Header("upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true &&
                handshake.Header("sec-websocket-version") == "13" &&
                handshake.Header("sec-websocket-extensions")?.StartsWith("permessage-deflate", StringComparison.Ordinal) == true,
                "handshake headers: " + string.Join("; ", handshake.Headers.Select(pair => pair.Key + "=" + pair.Value)));
            // JSON.stringify({ type: "response.create", ...requestBody }): the type first, then the SSE body unchanged.
            var expected = new JsonObject { ["type"] = "response.create" };
            foreach (var (name, value) in transport.BuildBody(Hi(model) with { SessionId = null })) expected[name] = value?.DeepClone();
            expected["prompt_cache_key"] = "sess-ws-handshake";
            JsonSame(expected.ToJsonString(), server.Frames.Single().Text, "response.create frame");
            Equal("type", JsonNode.Parse(server.Frames.Single().Text)!.AsObject().First().Key, "type leads the frame");
            // Without a session the request id is a fresh UUIDv7 and the connection closes after the request.
            var (plain, plainModel, _) = CodexOver(server, "websocket");
            Check((await Collect(plain, Hi(plainModel)))[^1] is StreamDone, "uncached request");
            var id = server.Upgrades.Last().Header("x-client-request-id")!;
            Check(Guid.TryParse(id, out var parsed) && parsed.Version == 7 && server.Upgrades.Last().Header("session-id") == id, "uuidv7 request id: " + id);
            Check(await server.WaitAsync(() => server.Closes.Contains("1000 done")), "an uncached connection closes with 1000 done");
        }
        finally { OpenAICodexWebSockets.CloseSessions("sess-ws-handshake"); OpenAICodexWebSockets.ResetDebugStats("sess-ws-handshake"); }
    }

    private static async Task CodexWebSocketContinuation()
    {
        await using var server = new CodexServer();
        server.OnFrame = (connection, _) => connection.SendAllAsync(CodexCompleted);
        const string session = "sess-ws-continuation";
        var (transport, model, _) = CodexOver(server, "auto", session);
        try
        {
            var first = (StreamDone)(await Collect(transport, Hi(model)))[^1];
            var second = new ChatRequest(model, [Entry("""{"role":"user","content":"Hi","timestamp":2}"""), new TranscriptEntry("assistant", PiWireJson.WriteMessage(first.Message)),
                Entry("""{"role":"user","content":"More","timestamp":4}""")], 5);
            Check((await Collect(transport, second))[^1] is StreamDone, "second request");
            Equal(1, server.Upgrades.Count, "one connection for the session");
            var delta = JsonNode.Parse(server.Frames[1].Text)!;
            Equal("resp-1", delta["previous_response_id"]?.GetValue<string>(), "previous_response_id");
            JsonEqual("""[{"role":"user","content":[{"type":"input_text","text":"More"}]}]""", delta["input"]!.ToJsonString(), "only the new input");
            var stats = OpenAICodexWebSockets.GetDebugStats(session)!;
            Check(stats is { Requests: 2, ConnectionsCreated: 1, ConnectionsReused: 1, CachedContextRequests: 2, FullContextRequests: 1, DeltaRequests: 1,
                LastDeltaInputItems: 1, LastPreviousResponseId: "resp-1", WebSocketFailures: 0 }, "stats: " + stats);
            // A changed request (another thinking level) drops the continuation: the full context goes again.
            Check((await Collect(transport, second with { ThinkingLevel = "high" }))[^1] is StreamDone, "changed body");
            Check(JsonNode.Parse(server.Frames[2].Text)!["previous_response_id"] is null, "no delta for a changed body");
            // "websocket" reuses the connection but never sends a delta.
            var (plain, plainModel, _) = CodexOver(server, "websocket", "sess-ws-plain");
            var plainFirst = (StreamDone)(await Collect(plain, Hi(plainModel)))[^1];
            Check((await Collect(plain, second with { Model = plainModel }))[^1] is StreamDone, "plain second");
            Check(JsonNode.Parse(server.Frames[^1].Text)!["previous_response_id"] is null && OpenAICodexWebSockets.GetDebugStats("sess-ws-plain") is
                { ConnectionsReused: 1, CachedContextRequests: 0, DeltaRequests: 0 }, "websocket transport: reuse without delta");
            _ = plainFirst;
            // A busy session connection: a concurrent request opens an uncached connection.
            var gate = new TaskCompletionSource();
            server.OnFrame = async (connection, frame) => { if (!frame.Contains("\"Wait\"", StringComparison.Ordinal)) await gate.Task; await connection.SendAllAsync(CodexCompleted); };
            var upgrades = server.Upgrades.Count; var framesBefore = server.Frames.Count;
            var slow = Collect(transport, Hi(model));
            Check(await server.WaitAsync(() => server.Frames.Count == framesBefore + 1), "first request sent");
            var fast = await Collect(transport, new(model, [Entry("""{"role":"user","content":"Wait","timestamp":2}""")], 3));
            gate.SetResult();
            Check((await slow)[^1] is StreamDone && fast[^1] is StreamDone, "both complete");
            Equal(upgrades + 1, server.Upgrades.Count, "one extra connection while the cached one is busy");
        }
        finally
        {
            OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session);
            OpenAICodexWebSockets.CloseSessions("sess-ws-plain"); OpenAICodexWebSockets.ResetDebugStats("sess-ws-plain");
        }
    }

    private static async Task CodexWebSocketFallback()
    {
        await using var server = new CodexServer { RejectUpgrade = true };
        server.OnPost = _ => CodexCompleted;
        const string session = "sess-ws-fallback";
        var (transport, model, _) = CodexOver(server, "auto", session);
        try
        {
            var done = (StreamDone)(await Collect(transport, Hi(model)))[^1];
            Equal(1, server.Upgrades.Count, "one upgrade attempt"); Equal(1, server.Posts.Count, "then SSE");
            var diagnostic = Diagnostics(done)!.Single()!;
            var bytes = Encoding.UTF8.GetByteCount(server.Posts.Single().Body);
            Equal("provider_transport_failure", diagnostic["type"]!.GetValue<string>(), "diagnostic type");
            Equal(1_800_000_000_000L, diagnostic["timestamp"]!.GetValue<long>(), "diagnostic timestamp");
            JsonEqual($$"""{"configuredTransport":"auto","fallbackTransport":"sse","eventsEmitted":false,"phase":"before_message_stream_start","requestBytes":{{bytes}}}""",
                diagnostic["details"]!.ToJsonString(), "diagnostic details");
            Equal("Error", diagnostic["error"]!["name"]!.GetValue<string>(), "error name");
            Check(server.Posts.Single().Header("accept") == "text/event-stream" && server.Posts.Single().Header("openai-beta") == "responses=experimental", "SSE headers");
            // The session stays on SSE (websocketSseFallbackSessions): no second upgrade.
            var again = (StreamDone)(await Collect(transport, Hi(model)))[^1];
            Equal(1, server.Upgrades.Count, "no further upgrade for the session"); Equal(2, server.Posts.Count, "SSE again");
            Check(Diagnostics(again) is null, "no diagnostic for a session already on SSE");
            Check(OpenAICodexWebSockets.GetDebugStats(session) is { WebSocketFailures: 1, SseFallbacks: 2, WebSocketFallbackActive: true }, "fallback stats");
            // "sse" never upgrades.
            var (sse, sseModel, _) = CodexOver(server, "sse", "sess-ws-sse");
            Check((await Collect(sse, Hi(sseModel)))[^1] is StreamDone && server.Upgrades.Count == 1, "sse transport");
            // A close before any event (1009 without a reason) also falls back: "WebSocket closed 1009 message too big".
            await using var closing = new CodexServer();
            closing.OnFrame = (connection, _) => connection.CloseAsync((WebSocketCloseStatus)1009, "");
            closing.OnPost = _ => CodexCompleted;
            var (big, bigModel, _) = CodexOver(closing, "auto");
            var bigDone = (StreamDone)(await Collect(big, Hi(bigModel)))[^1];
            var bigError = Diagnostics(bigDone)!.Single()!["error"]!;
            JsonEqual("""{"name":"WebSocketCloseError","message":"WebSocket closed 1009 message too big","code":1009}""", bigError.ToJsonString(), "1009 close");
        }
        finally
        {
            OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session); OpenAICodexWebSockets.ResetDebugStats("sess-ws-sse");
        }
    }

    private static async Task CodexWebSocketFailures()
    {
        // After the first event a close is the stream's error (no SSE), recorded as an after-start transport failure.
        await using var server = new CodexServer();
        server.OnFrame = async (connection, _) => { await connection.SendAllAsync(CodexCompleted[..3]); await connection.CloseAsync(WebSocketCloseStatus.InternalServerError, "boom"); };
        server.OnPost = _ => CodexCompleted;
        const string session = "sess-ws-failures";
        var (transport, model, _) = CodexOver(server, "auto", session);
        try
        {
            var error = (await Collect(transport, Hi(model)))[^1];
            Check(error is StreamError, "error terminal");
            Equal("WebSocket closed 1011 boom", ErrorMessage(error), "close after start");
            Check(server.Posts.IsEmpty, "no SSE after events were emitted");
            JsonEqual("""{"configuredTransport":"auto","eventsEmitted":true,"phase":"after_message_stream_start","requestBytes":0}""",
                ((JsonObject)Diagnostics(error)!.Single()!["details"]!.DeepClone()).Also(details => details["requestBytes"] = 0).ToJsonString(), "after-start details");
            Check(OpenAICodexWebSockets.GetDebugStats(session) is { WebSocketFailures: 1, WebSocketFallbackActive: true }, "failure recorded");
            // A Codex error event before any event is not a transport failure: no SSE.
            await using var refusing = new CodexServer();
            refusing.OnFrame = (connection, _) => connection.SendAllAsync(["""{"type":"error","code":"server_error","message":"boom"}"""]);
            refusing.OnPost = _ => CodexCompleted;
            var (refused, refusedModel, _) = CodexOver(refusing, "auto");
            var refusal = (await Collect(refused, Hi(refusedModel)))[^1];
            Check(refusal is StreamError && ErrorMessage(refusal) == "Codex error: boom" && refusing.Posts.IsEmpty && Diagnostics(refusal) is null, "codex error event");
            // websocket_connection_limit_reached before the first event: one retry on a new connection.
            await using var limited = new CodexServer();
            var attempts = 0;
            limited.OnFrame = (connection, _) => Interlocked.Increment(ref attempts) == 1
                ? connection.SendAllAsync(["""{"type":"error","error":{"code":"websocket_connection_limit_reached","message":"too many"}}"""])
                : connection.SendAllAsync(CodexCompleted);
            var (retry, retryModel, _) = CodexOver(limited, "auto");
            Check((await Collect(retry, Hi(retryModel)))[^1] is StreamDone && limited.Upgrades.Count == 2 && limited.Posts.IsEmpty, "connection limit retried");
            // A second connection limit falls back to SSE with the diagnostic.
            await using var exhausted = new CodexServer();
            exhausted.OnFrame = (connection, _) => connection.SendAllAsync(["""{"type":"error","error":{"code":"websocket_connection_limit_reached","message":"too many"}}"""]);
            exhausted.OnPost = _ => CodexCompleted;
            var (fallback, fallbackModel, _) = CodexOver(exhausted, "auto");
            var fallbackDone = (await Collect(fallback, Hi(fallbackModel)))[^1];
            Check(fallbackDone is StreamDone && exhausted.Upgrades.Count == 2 && exhausted.Posts.Count == 1 &&
                Diagnostics(fallbackDone)!.Single()!["error"]!["message"]!.GetValue<string>() == "Codex error: too many", "connection limit twice: SSE");
            // previous_response_not_found: one retry, which sends the full context on a fresh connection.
            await using var forgetful = new CodexServer();
            forgetful.OnFrame = (connection, frame) => frame.Contains("previous_response_id", StringComparison.Ordinal)
                ? connection.SendAllAsync(["""{"type":"error","error":{"code":"previous_response_not_found","message":"gone"}}"""])
                : connection.SendAllAsync(CodexCompleted);
            var (cached, cachedModel, _) = CodexOver(forgetful, "websocket-cached", "sess-ws-forgetful");
            var firstDone = (StreamDone)(await Collect(cached, Hi(cachedModel)))[^1];
            var next = new ChatRequest(cachedModel, [Entry("""{"role":"user","content":"Hi","timestamp":2}"""),
                new TranscriptEntry("assistant", PiWireJson.WriteMessage(firstDone.Message)), Entry("""{"role":"user","content":"More","timestamp":4}""")], 5);
            Check((await Collect(cached, next))[^1] is StreamDone, "retried after previous_response_not_found");
            Check(JsonNode.Parse(forgetful.Frames[1].Text)!["previous_response_id"] is not null && JsonNode.Parse(forgetful.Frames[2].Text)!["previous_response_id"] is null &&
                forgetful.Upgrades.Count == 2, "delta, then the full context on a new connection");
        }
        finally
        {
            OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session);
            OpenAICodexWebSockets.CloseSessions("sess-ws-forgetful"); OpenAICodexWebSockets.ResetDebugStats("sess-ws-forgetful");
        }
    }

    private static async Task CodexWebSocketTimeouts()
    {
        // The open handshake is bounded by websocketConnectTimeoutMs; a stalled stream by timeoutMs (the idle timeout).
        await using var stalled = new CodexServer { StallUpgrade = true };
        stalled.OnPost = _ => CodexCompleted;
        var (connect, connectModel, _) = CodexOver(stalled, "auto", options: new() { WebSocketConnectTimeout = TimeSpan.FromMilliseconds(300) });
        var connectDone = (await Collect(connect, Hi(connectModel)))[^1];
        Equal("WebSocket connect timeout after 300ms", Diagnostics(connectDone)!.Single()!["error"]!["message"]!.GetValue<string>(), "connect timeout");
        await using var silent = new CodexServer();
        silent.OnFrame = (_, _) => Task.CompletedTask;
        silent.OnPost = _ => CodexCompleted;
        var (idle, idleModel, _) = CodexOver(silent, "auto", options: new() { Timeout = TimeSpan.FromMilliseconds(300) });
        var idleDone = (await Collect(idle, Hi(idleModel)))[^1];
        Check(idleDone is StreamDone && silent.Posts.Count == 1, "idle timeout falls back to SSE");
        Equal("WebSocket idle timeout after 300ms", Diagnostics(idleDone)!.Single()!["error"]!["message"]!.GetValue<string>(), "idle timeout");
        Check(await silent.WaitAsync(() => silent.Closes.Contains("1000 idle_timeout")), "closed with 1000 idle_timeout");
        // A cached connection older than 55 minutes is closed ("connection_age_limit") and replaced.
        await using var server = new CodexServer();
        server.OnFrame = (connection, _) => connection.SendAllAsync(CodexCompleted);
        const string session = "sess-ws-age";
        var (aged, agedModel, time) = CodexOver(server, "websocket", session);
        try
        {
            Check((await Collect(aged, Hi(agedModel)))[^1] is StreamDone, "first");
            Interlocked.Add(ref time.Now, 54 * 60_000);
            Check((await Collect(aged, Hi(agedModel)))[^1] is StreamDone && server.Upgrades.Count == 1, "reused before 55 minutes");
            Interlocked.Add(ref time.Now, 60_000);
            Check((await Collect(aged, Hi(agedModel)))[^1] is StreamDone && server.Upgrades.Count == 2, "replaced at 55 minutes");
            Check(await server.WaitAsync(() => server.Closes.Contains("1000 connection_age_limit")), "closed with connection_age_limit");
            OpenAICodexWebSockets.CloseSessions(session);
            Check(await server.WaitAsync(() => server.Closes.Contains("1000 debug_close")), "session cleanup closes with debug_close");
        }
        finally { OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session); }
    }

    private static async Task CodexWebSocketMissingContinuationAfterStart()
    {
        // stream: previous_response_not_found retries once even after events (response.created) arrived; the failed attempt dropped the
        // continuation and closed its connection, so the retry sends the full context (no previous_response_id) on a new connection.
        await using var server = new CodexServer();
        const string gone = """{"type":"error","error":{"code":"previous_response_not_found","message":"gone"}}""";
        var forget = 1;
        server.OnFrame = (connection, frame) => frame.Contains("previous_response_id", StringComparison.Ordinal) || Volatile.Read(ref forget) > 1
            ? connection.SendAllAsync([CodexCompleted[0], gone])
            : connection.SendAllAsync(CodexCompleted);
        server.OnPost = _ => CodexCompleted;
        const string session = "sess-ws-forgotten-after-start";
        var (transport, model, _) = CodexOver(server, "websocket-cached", session);
        try
        {
            var first = (StreamDone)(await Collect(transport, Hi(model)))[^1];
            var next = new ChatRequest(model, [Entry("""{"role":"user","content":"Hi","timestamp":2}"""),
                new TranscriptEntry("assistant", PiWireJson.WriteMessage(first.Message)), Entry("""{"role":"user","content":"More","timestamp":4}""")], 5);
            var events = await Collect(transport, next);
            var done = events[^1] as StreamDone ?? throw new CheckException("done: " + ErrorMessage(events[^1]));
            Check(done.Message.Content.Single() is TextContent { Text: "Hello" } && Diagnostics(done) is null, "the retried response is the message");
            Equal(1, events.Count(item => item is StreamStarted), "one start");
            Equal(3, server.Frames.Count, "first, delta, full retry");
            var delta = JsonNode.Parse(server.Frames[1].Text)!; var retried = JsonNode.Parse(server.Frames[2].Text)!;
            Equal("resp-1", delta["previous_response_id"]?.GetValue<string>(), "the delta named the previous response");
            Check(retried["previous_response_id"] is null, "the retry names no previous response");
            var expected = new JsonObject { ["type"] = "response.create" };
            foreach (var (name, value) in transport.BuildBody(next with { SessionId = null })) expected[name] = value?.DeepClone();
            expected["prompt_cache_key"] = session;
            JsonSame(expected.ToJsonString(), server.Frames[2].Text, "full response.create frame of the retry");
            Check(server.Upgrades.Count == 2 && server.Frames[2].Connection != server.Frames[1].Connection && server.Posts.IsEmpty, "a new connection, no SSE");
            Check(await server.WaitAsync(() => server.Closes.Contains("1000 done")), "the failed connection closed");
            Check(OpenAICodexWebSockets.GetDebugStats(session) is { Requests: 3, DeltaRequests: 1, FullContextRequests: 2, WebSocketFailures: 0 }, "stats");
            // Only once per stream: a second previous_response_not_found after events is the stream's error.
            Volatile.Write(ref forget, 2);
            var frames = server.Frames.Count;
            var failed = (await Collect(transport, Hi(model)))[^1];
            Check(failed is StreamError && ErrorMessage(failed) == "Codex error: gone" && Diagnostics(failed) is null && server.Posts.IsEmpty, "retried once: " + ErrorMessage(failed));
            Equal(frames + 2, server.Frames.Count, "two attempts");
        }
        finally { OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session); }
    }

    private static async Task CodexWebSocketSessionDispose()
    {
        // agent-session.ts dispose: cleanupSessionResources(sessionId) closes the session's cached Codex connections (1000 debug_close).
        await using var server = new CodexServer();
        server.OnFrame = (connection, _) => connection.SendAllAsync(CodexCompleted);
        var directory = Temp("codex-ws-dispose");
        const string session = "019a0000-0000-7000-8000-00000000c0de";
        var (transport, model, _) = CodexOver(server, "auto");
        var cleaned = new ConcurrentQueue<string?>();
        var unregister = SessionResources.RegisterCleanup(cleaned.Enqueue);
        try
        {
            var header = new PiSharp.Sessions.Serialization.SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = session,
                timestamp = "2026-10-09T00:00:00.000Z", cwd = directory }));
            var ids = 0;
            var owner = await PiSharp.CodingAgent.PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header,
                new PiSharp.Agent.AgentConfiguration(model, transport, []), () => 7, () => "entry-" + Interlocked.Increment(ref ids));
            // The session's requests carry its id: the connection is cached under it.
            Check((await Collect(transport, Hi(model) with { SessionId = session }))[^1] is StreamDone, "request");
            Check(OpenAICodexWebSockets.GetDebugStats(session) is { ConnectionsCreated: 1 } && server.Closes.IsEmpty, "a cached connection stays open");
            await owner.DisposeAsync();
            Check(await server.WaitAsync(() => server.Closes.Contains("1000 debug_close")), "dispose closed the connection with debug_close");
            Check(cleaned.SequenceEqual([session]), "cleanup ran for the session id: " + string.Join(",", cleaned));
            // The next request of that session opens a new connection.
            Check((await Collect(transport, Hi(model) with { SessionId = session }))[^1] is StreamDone && server.Upgrades.Count == 2, "a new connection afterwards");
            // cleanupSessionResources runs every cleanup and throws their failures together.
            var unregisterFailing = SessionResources.RegisterCleanup(_ => throw new InvalidOperationException("broken"));
            try
            {
                var failure = await Throws<AggregateException>(() => { SessionResources.Cleanup("no-such-session"); return Task.CompletedTask; });
                Check(failure.InnerExceptions.Single().Message == "broken" && failure.Message.StartsWith("Failed to cleanup session resources", StringComparison.Ordinal) &&
                    cleaned.Last() == "no-such-session", "aggregate failure after every cleanup");
            }
            finally { unregisterFailing(); }
        }
        finally { unregister(); OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session); }
    }

    private static T Also<T>(this T value, Action<T> change) { change(value); return value; }

    /// <summary>A loopback Codex peer: POSTs answer with SSE events, upgrades become server WebSockets that call <see cref="OnFrame"/>
    /// for every client text frame. Records handshakes, POSTs, frames and the close frames the client sends.</summary>
    internal sealed class CodexServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        public ConcurrentQueue<HttpHead> Posts { get; } = new();
        public List<HttpHead> Upgrades { get; } = [];
        public List<(int Connection, string Text)> Frames { get; } = [];
        public ConcurrentBag<string> Closes { get; } = [];
        public Func<ServerConnection, string, Task> OnFrame { get; set; } = (_, _) => Task.CompletedTask;
        public Func<HttpHead, string[]> OnPost { get; set; } = _ => [];
        public bool RejectUpgrade { get; init; }
        public bool StallUpgrade { get; init; }
        public string BaseUrl { get; }

        public CodexServer()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/backend-api";
            _accept = Task.Run(AcceptAsync);
        }

        public async Task<bool> WaitAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(25);
            return condition();
        }

        private async Task AcceptAsync()
        {
            var number = 0;
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch (Exception) { return; }
                var id = ++number;
                _ = Task.Run(() => ServeAsync(client, id));
            }
        }

        private async Task ServeAsync(TcpClient client, int id)
        {
            using var owned = client;
            var stream = owned.GetStream();
            try
            {
                var head = await HttpHead.ReadAsync(stream, _stop.Token);
                if (head is null) return;
                if (head.Header("upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true)
                {
                    lock (Upgrades) Upgrades.Add(head);
                    if (StallUpgrade) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                    if (RejectUpgrade)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                        return;
                    }
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(head.Header("sec-websocket-key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
                    using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
                    var connection = new ServerConnection(socket);
                    var buffer = new byte[1 << 20];
                    while (socket.State == WebSocketState.Open)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(buffer, _stop.Token);
                            if (received.MessageType == WebSocketMessageType.Close)
                            {
                                Closes.Add($"{(int?)received.CloseStatus} {received.CloseStatusDescription}");
                                if (socket.State == WebSocketState.CloseReceived)
                                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", _stop.Token);
                                return;
                            }
                            message.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        var text = Encoding.UTF8.GetString(message.ToArray());
                        lock (Frames) Frames.Add((id, text));
                        await OnFrame(connection, text);
                    }
                    return;
                }
                var body = head.Body;
                Posts.Enqueue(head);
                var events = string.Concat(OnPost(head).Select(data => "data: " + data + "\n\n"));
                var payload = Encoding.UTF8.GetBytes(events);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(payload, _stop.Token);
                _ = body;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _accept; } catch (Exception) { }
        }
    }

    internal sealed class ServerConnection(WebSocket socket)
    {
        public async Task SendAllAsync(IEnumerable<string> events)
        {
            foreach (var data in events) await socket.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        public Task CloseAsync(WebSocketCloseStatus status, string reason) => socket.CloseOutputAsync(status, reason, CancellationToken.None);
    }

    /// <summary>An HTTP/1.1 request head (lower-cased header names) and its Content-Length body.</summary>
    internal sealed record HttpHead(string RequestLine, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        public static async Task<HttpHead?> ReadAsync(Stream stream, CancellationToken token)
        {
            var bytes = new List<byte>(); var one = new byte[1];
            while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
            {
                if (await stream.ReadAsync(one, token) == 0) return null;
                bytes.Add(one[0]);
            }
            var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            var body = "";
            if (headers.TryGetValue("content-length", out var length) && int.Parse(length) is var count and > 0)
            {
                var buffer = new byte[count]; await stream.ReadExactlyAsync(buffer, token);
                body = Encoding.UTF8.GetString(headers.GetValueOrDefault("content-encoding") == "zstd" ? PiSharp.AI.Compression.ZstdDecoder.Decompress(buffer) : buffer);
            }
            return new(lines[0], headers, body);
        }
    }
}
