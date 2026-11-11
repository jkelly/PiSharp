// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/transports/pipe.ts, packages/mcp/src/transports/lines.ts.
// Authored native regression; not an upstream execution capture.
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Transport;

internal static partial class McpTransportTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Sync152Cases() =>
    [
        (Prefix + "152-pipe-fragmented-reused-input-and-stderr-tail", PipeFragments),
        (Prefix + "152-pipe-malformed-and-oversize-frame-recovery", PipeRecovery),
        (Prefix + "152-pipe-rejected-exit-incomplete-order-and-send-fence", PipeExit),
        (Prefix + "152-pipe-grace-force-kill-and-stable-close", PipeClose),
        (Prefix + "152-pipe-close-during-open-owns-late-pipe", PipeOpening),
        (Prefix + "152-pipe-close-joins-receiver", PipeReceiver),
        (Prefix + "152-pipe-start-send-and-close-admission", PipeAdmission),
        (Prefix + "152-http-error-prefix-bound-and-safe-utf8", HttpErrorPrefix),
        (Prefix + "152-http-error-read-failure-retains-status", HttpErrorReadFailure),
        (Prefix + "152-http-json-exact-byte-limit-and-multibyte-splits", HttpExactJsonBound),
        (Prefix + "152-http-oversize-event-disposes-body", HttpOversizeDisposal)
    ];

    private sealed class FakeMcpPipe : IMcpPipe
    {
        internal readonly TaskCompletionSource End = Gate();
        internal readonly ConcurrentQueue<byte[]> Writes = new();
        internal McpPipeHandlers Handlers = null!;
        internal int StdinCloses, Kills;
        internal bool ExitOnStdin;
        public Task Exited => End.Task;
        public void Write(ReadOnlyMemory<byte> bytes) => Writes.Enqueue(bytes.ToArray());
        public void CloseStdin() { StdinCloses++; if (ExitOnStdin) End.TrySetResult(); }
        public void Close() { Kills++; End.TrySetException(new IOException("expected killed pipe")); }
        internal Task<IMcpPipe> Open(McpPipeHandlers handlers) { Handlers = handlers; return Task.FromResult<IMcpPipe>(this); }
        internal void Stdout(string text) => Handlers.OnStdout(Encoding.UTF8.GetBytes(text));
    }

    private static async Task PipeFragments()
    {
        var pipe = new FakeMcpPipe(); var messages = new ConcurrentQueue<string>();
        var received = Gate(); var errors = new ConcurrentQueue<Exception>(); var stderrChunks = new ConcurrentQueue<string>();
        var wire = new McpPipeTransport(new(pipe.Open, MaximumStderrBytes: 5, OnStderr: stderrChunks.Enqueue));
        try
        {
            await wire.StartAsync(new(message => { messages.Enqueue(message.Value.GetProperty("result").GetString()!); received.TrySetResult(); return ValueTask.CompletedTask; }, _ => { }) { Error = errors.Enqueue }, default);
            var source = Encoding.UTF8.GetBytes(" \r\n{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"雪\"}\r\n");
            var reused = new byte[1];
            foreach (var item in source) { reused[0] = item; pipe.Handlers.OnStdout(reused); reused[0] = 0xff; }
            pipe.Handlers.OnStderr(Encoding.UTF8.GetBytes("0123"));
            pipe.Handlers.OnStderr(Encoding.UTF8.GetBytes("456"));
            Equal("23456", wire.Stderr);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}"), default);
            Equal("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}\n", Encoding.UTF8.GetString(pipe.Writes.Single()));
            Equal("雪", messages.Single()); Check(errors.IsEmpty);
            pipe.End.TrySetResult(); await wire.CloseAsync();
            Equal("0123,456", string.Join(',', stderrChunks));
        }
        finally { pipe.End.TrySetResult(); await wire.CloseAsync(); }
    }

    private static async Task PipeRecovery()
    {
        var pipe = new FakeMcpPipe(); var events = new ConcurrentQueue<string>(); var done = Gate();
        var frame = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":true}";
        var wire = new McpPipeTransport(new(pipe.Open, MaximumMessageBytes: Encoding.UTF8.GetByteCount(frame)));
        try
        {
            await wire.StartAsync(new(_ => { events.Enqueue("message"); done.TrySetResult(); return ValueTask.CompletedTask; }, _ => { }) { Error = _ => events.Enqueue("error") }, default);
            pipe.Stdout("{bad json}\n");
            pipe.Stdout(new string('x', frame.Length)); pipe.Stdout("overflow"); pipe.Stdout("more\n\r\n" + frame + "\n");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(3)); Equal("error,error,message", string.Join(',', events));
        }
        finally { pipe.End.TrySetResult(); await wire.CloseAsync(); }
    }

    private static async Task PipeExit()
    {
        var pipe = new FakeMcpPipe(); var events = new ConcurrentQueue<string>(); var disconnected = Gate();
        var original = new IOException("actual pipe exit failure");
        var wire = new McpPipeTransport(new(pipe.Open));
        await wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { events.Enqueue("disconnected"); disconnected.TrySetResult(); })
        { Error = error => events.Enqueue(ReferenceEquals(error, original) ? "original" : error is McpRuntimeProtocolException ? "incomplete" : "other") }, default);
        pipe.Stdout("{\"jsonrpc\":"); pipe.End.TrySetException(original);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Equal("original,incomplete,disconnected", string.Join(',', events));
        await Throws<McpRuntimeDisconnectedException>(Task.Run(() => wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}"), default)));
        await wire.CloseAsync(); Equal(0, pipe.Kills);
    }

    private static async Task PipeClose()
    {
        foreach (var graceful in new[] { true, false })
        {
            var pipe = new FakeMcpPipe { ExitOnStdin = graceful }; var errors = new ConcurrentQueue<Exception>();
            var wire = new McpPipeTransport(new(pipe.Open, CloseTimeoutMilliseconds: 0));
            await wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }) { Error = errors.Enqueue }, default);
            var close = wire.CloseAsync(); Check(ReferenceEquals(close, wire.CloseAsync()));
            await close.WaitAsync(TimeSpan.FromSeconds(3));
            Equal(1, pipe.StdinCloses); Equal(graceful ? 0 : 1, pipe.Kills); Check(errors.IsEmpty);
            await Throws<McpRuntimeDisconnectedException>(Task.Run(() => wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }), default)));
        }
    }

    private static async Task PipeOpening()
    {
        var entered = Gate(); var release = Gate(); var pipe = new FakeMcpPipe(); var early = Gate();
        var wire = new McpPipeTransport(new(async handlers =>
        {
            pipe.Handlers = handlers; pipe.Stdout("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":true}\n");
            entered.TrySetResult(); await release.Task; return pipe;
        }));
        var start = wire.StartAsync(new(_ => { early.TrySetResult(); return ValueTask.CompletedTask; }, _ => { }), default);
        await entered.Task; await early.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var close = wire.CloseAsync(); Check(!close.IsCompleted); release.TrySetResult();
        await Throws<McpRuntimeDisconnectedException>(start); await close.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(1, pipe.Kills); Equal(0, pipe.StdinCloses);
    }

    private static async Task PipeReceiver()
    {
        var pipe = new FakeMcpPipe(); var entered = Gate(); var release = Gate();
        var wire = new McpPipeTransport(new(pipe.Open));
        await wire.StartAsync(new(async _ => { entered.TrySetResult(); await release.Task; }, _ => { }), default);
        pipe.Stdout("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":true}\n"); await entered.Task;
        pipe.End.TrySetResult(); var close = wire.CloseAsync(); Check(!close.IsCompleted);
        release.TrySetResult(); await close.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task PipeAdmission()
    {
        var pipe = new FakeMcpPipe { ExitOnStdin = true }; var opens = 0;
        var wire = new McpPipeTransport(new(handlers => { opens++; return pipe.Open(handlers); }));
        var callbacks = new McpWireCallbacks(_ => ValueTask.CompletedTask, _ => { });
        var ping = Json("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}");
        await Throws<McpRuntimeDisconnectedException>(Task.Run(() => wire.SendAsync(ping, default)));
        await wire.StartAsync(callbacks, default);
        await Throws<InvalidOperationException>(Task.Run(() => wire.StartAsync(callbacks, default)));
        await wire.CloseAsync();
        await Throws<McpRuntimeDisconnectedException>(Task.Run(() => wire.SendAsync(ping, default)));
        Equal(1, opens);
        var unopened = new McpPipeTransport(new(_ => { opens++; return Task.FromResult<IMcpPipe>(pipe); }));
        await unopened.CloseAsync();
        await Throws<McpRuntimeDisconnectedException>(Task.Run(() => unopened.StartAsync(callbacks, default)));
        Equal(1, opens);
    }

    private sealed class CountedBody(byte[] content, int chunk = int.MaxValue) : MemoryStream(content)
    {
        internal int ReadBytes;
        internal bool Disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], token); ReadBytes += count; return count; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static async Task HttpErrorPrefix()
    {
        foreach (var length in new[] { 8192, 20000 })
        {
            // Snow begins one byte before the diagnostic limit. A truncated scalar must not become U+FFFD.
            var payload = length == 8192 ? Encoding.UTF8.GetBytes(new string('x', length)) : Encoding.UTF8.GetBytes(new string('x', 8191) + "雪" + new string('y', length));
            var body = new CountedBody(payload, 97);
            var wire = Http(Binding(), (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StreamContent(body) }));
            await wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }), default);
            var error = await Throws<McpHttpStatusException>(wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}"), default));
            Equal(502, error.StatusCode); Equal(length != 8192, error.ResponseTruncated);
            Equal(length == 8192 ? 8192 : 8191, error.ResponseText.Length);
            Check(!error.ResponseText.Contains('\uFFFD') && body.ReadBytes <= 8193 && body.Disposed);
            Check(error.Message.EndsWith("...") && error.Message.Length < 250);
            await wire.CloseAsync();
        }
    }

    private static async Task HttpErrorReadFailure()
    {
        var body = new ReadFailureBody(new IOException("diagnostic stream failed"));
        var wire = Http(Binding(), (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(body) }));
        await wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }), default);
        var error = await Throws<McpHttpStatusException>(wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}"), default));
        Equal(400, error.StatusCode); Equal("", error.ResponseText); Check(body.CleanupAttempted);
        await wire.CloseAsync();
    }

    private static async Task HttpExactJsonBound()
    {
        foreach (var bom in new[] { "", "\uFEFF" })
        foreach (var extra in new[] { false, true })
        {
            var payload = Encoding.UTF8.GetBytes(bom + "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"雪\"}");
            var body = new CountedBody(extra ? [.. payload, (byte)' '] : payload, 1); JsonData? received = null;
            var wire = Http(Binding(), (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(body) { Headers = { ContentType = new("application/json") } } }), new(MaximumFrameBytes: payload.Length));
            await wire.StartAsync(new(message => { received = message; return ValueTask.CompletedTask; }, _ => { }), default);
            var send = wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\"}"), default);
            if (extra) await Throws<McpRuntimeProtocolException>(send); else { await send; Equal("雪", received!.Value.GetProperty("result").GetString()); }
            Check(body.Disposed); await wire.CloseAsync();
        }
    }

    private static async Task HttpOversizeDisposal()
    {
        var body = new CountedBody(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("data: " + new string('x', 50) + "\r", 4))));
        var wire = Http(Binding(), (_, _) => ValueTask.FromResult(SseBody(body)), new(MaximumFrameBytes: 128));
        await wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }), default);
        await Throws<McpRuntimeProtocolException>(wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\"}"), default));
        Check(body.Disposed); await wire.CloseAsync();
    }
}
