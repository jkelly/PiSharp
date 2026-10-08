using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Threading.Channels;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp;
using PiSharp.Extensions.Runtime.Mcp.Transport;

// Only supplied in-memory streams/exchanges. No HTTP client, process, credential or package acquisition.
internal static class McpTransportTests
{
    internal const string Prefix = "mcp-transport.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual-jsonl-channel-runtime-initialize-list-call", ReachableRuntime),
        (Prefix + "split-crlf-blank-frames-and-out-of-order-ids", Correlation),
        (Prefix + "remote-error-code-data-and-result-size", Errors),
        (Prefix + "progress-meta-and-joined-native-delivery", Progress),
        (Prefix + "caller-cancel-writes-and-initialize-exclusion", Cancellation),
        (Prefix + "deadline-reset-and-original-cancel-write", Deadline),
        (Prefix + "incoming-ping-roots-and-method-not-found", Incoming),
        (Prefix + "notification-refresh-does-not-block-reader", Notification),
        (Prefix + "notification-disconnect-retains-callback-close-and-lazy-reconnect", NotificationDisconnect),
        (Prefix + "notification-retirement-cleanup-fault-remains-owned", NotificationDisconnectFault),
        (Prefix + "external-close-joins-deferred-retirement-without-cycle", NotificationExternalClose),
        (Prefix + "held-send-close-joins-and-stable-cleanup-fault", HeldClose),
        (Prefix + "held-start-close-joins-no-reader-effects", HeldStart),
        (Prefix + "close-initiates-owner-stop-before-cancellation-callback-join", StopBeforeCancelJoin),
        (Prefix + "stdio-frame-limit-and-partial-eof", Framing),
        (Prefix + "http-json-batch-session-protocol-and-delete", HttpJson),
        (Prefix + "http-sse-eof-filter-and-get-resumption-no-post-replay", HttpSse),
        (Prefix + "http-get-after-initialized-and-405", HttpGet),
        (Prefix + "http-rejections-and-configured-body-bounds", HttpBounds),
        (Prefix + "http-cancel-joins-held-send-and-held-delete", HttpHeld),
        (Prefix + "post-get-disposal-unblocks-held-read-originals", HttpBody),
        (Prefix + "http-read-and-cleanup-faults-preserve-originals", HttpCleanupFault),
        (Prefix + "http-physical-stop-joins-pending-headers-and-late-response", HttpPhysicalStop),
        (Prefix + "http-legacy-delegate-has-no-stop-authority", HttpLegacyAdmission),
        (Prefix + "http-admitted-send-stop-disposal-reentry-rejects-before-effects", HttpOwnerReentry),
        (Prefix + "sse-terminal-read-cleanup-and-exhaustion-retain-originals", SseTerminalFaults),
        (Prefix + "resumed-sse-cleanup-never-becomes-success-or-retry", SseResumeCleanup),
        (Prefix + "held-notification-close-and-progress-fault-original", CallbackOwnership),
        (Prefix + "callback-limit-refuses-before-second-host-effect", CallbackBound),
        (Prefix + "finite-inflight-id-and-callback-admission", Admission)
    ];
    private static JsonData Json(string value) => JsonData.Parse(value);
    private static JsonData Reply(JsonData request, string result = "{}") => Json("{\"jsonrpc\":\"2.0\",\"id\":" + request.Value.GetProperty("id").GetRawText() + ",\"result\":" + result + "}");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    // An explicit per-request synthetic lease. Cancellation begins before joining the actual header
    // original; handlers that deliberately ignore cancellation remain held until their fixture releases.
    private sealed class HttpOperation(HttpRequestMessage request, McpHttpExchange exchange) : IMcpAdmittedHttpRequestOperation
    {
        private readonly object gate = new(); private readonly CancellationTokenSource lifetime = new();
        private Task<HttpResponseMessage>? send; private Task? stop;
        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken token)
        {
            lock (gate)
            {
                if (stop is not null || send is not null) throw new InvalidOperationException("Synthetic HTTP lease already consumed");
                send = Send(token); return new(send);
            }
        }
        private async Task<HttpResponseMessage> Send(CancellationToken token)
        { using var joined = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); return await exchange(request, joined.Token); }
        public Task StopAsync() { lock (gate) return stop ??= Stop(send); }
        private async Task Stop(Task<HttpResponseMessage>? original)
        {
            var cancellation = lifetime.CancelAsync();
            try { if (original is not null) try { await original; } catch { /* Send owns its original failure. */ } }
            finally { await cancellation; lifetime.Dispose(); }
        }
    }
    private static McpStreamableHttpTransport Http(McpHttpBinding binding, McpHttpExchange exchange,
        McpTransportLimits? limits = null, TimeProvider? clock = null) =>
        new(binding, (McpAdmittedHttpRequestFactory)(request => new HttpOperation(request, exchange)), limits, clock);
    private sealed class ControlledHttpOperation(HttpResponseMessage response) : IMcpAdmittedHttpRequestOperation
    {
        internal readonly TaskCompletionSource HeadersEntered = Gate(), HeadersRelease = Gate(),
            StopEntered = Gate(), StopRelease = Gate(), CallbackEntered = Gate();
        private Task<HttpResponseMessage>? send; private Task? stop;
        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken token) => new(send = Send(token));
        private async Task<HttpResponseMessage> Send(CancellationToken token)
        {
            using var registration = token.Register(() => { CallbackEntered.TrySetResult(); StopEntered.Task.GetAwaiter().GetResult(); });
            HeadersEntered.TrySetResult(); await HeadersRelease.Task; return response;
        }
        public Task StopAsync() => stop ??= Stop();
        private async Task Stop() { StopEntered.TrySetResult(); if (send is not null) await send; await StopRelease.Task; }
    }
    private static async Task HttpPhysicalStop()
    {
        var body = new HeldBody(""); var operation = new ControlledHttpOperation(SseBody(body));
        var wire = new McpStreamableHttpTransport(Binding(), (McpAdmittedHttpRequestFactory)(request => operation));
        Task? send = null, close = null;
        try
        {
            await wire.StartAsync(new(message => ValueTask.CompletedTask, error => { }), default);
            send = wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/test\"}"), default);
            await operation.HeadersEntered.Task; close = wire.CloseAsync();
            await operation.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await operation.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted && ReferenceEquals(close, wire.CloseAsync()));
            operation.HeadersRelease.TrySetResult();
            await body.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted); operation.StopRelease.TrySetResult();
            await Throws<OperationCanceledException>(send); await close;
        }
        finally
        {
            operation.HeadersRelease.TrySetResult(); operation.StopRelease.TrySetResult(); body.Release.TrySetResult();
            if (send is not null) await Observe(send); if (close is not null) await Observe(close); else await wire.CloseAsync();
        }
    }
    private static Task HttpLegacyAdmission()
    {
        var effects = 0;
        ThrowsAction<NotSupportedException>(() => new McpStreamableHttpTransport(Binding(),
            (McpHttpExchange)((request, token) => { effects++; return ValueTask.FromResult(Response("")); })));
        Equal(0, effects); return Task.CompletedTask;
    }
    private sealed class GuardedBody(Action check) : MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"))
    { protected override void Dispose(bool disposing) { check(); base.Dispose(disposing); } }
    private sealed class ReentrantHttpOperation(Func<McpStreamableHttpTransport> owner, Func<HttpResponseMessage> response,
        Action sendChecked, Action stopChecked) : IMcpAdmittedHttpRequestOperation
    {
        private Task? stop;
        public async ValueTask<HttpResponseMessage> SendAsync(CancellationToken token)
        {
            await Task.Yield(); ThrowsAction<InvalidOperationException>(() => owner().CloseAsync());
            ThrowsAction<InvalidOperationException>(() => owner().SendAsync(Json("{\"jsonrpc\":\"2.0\",\"method\":\"forbidden\"}"), default));
            sendChecked(); return response();
        }
        public Task StopAsync() => stop ??= Stop();
        private async Task Stop()
        { await Task.Yield(); ThrowsAction<InvalidOperationException>(() => owner().CloseAsync()); stopChecked(); }
    }
    private static async Task HttpOwnerReentry()
    {
        var sends = 0; var stops = 0; var disposals = 0; var creations = 0;
        McpStreamableHttpTransport? wire = null;
        wire = new(Binding(), (McpAdmittedHttpRequestFactory)(request =>
        {
            creations++; ThrowsAction<InvalidOperationException>(() => wire!.CloseAsync());
            return new ReentrantHttpOperation(() => wire!, () => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GuardedBody(() =>
                { ThrowsAction<InvalidOperationException>(() => wire!.CloseAsync()); Interlocked.Increment(ref disposals); }))
                { Headers = { ContentType = new("application/json") } }
            }, () => sends++, () => stops++);
        }));
        try
        {
            await wire.StartAsync(new(message => ValueTask.CompletedTask, error => { }), default);
            await wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test\"}"), default);
            Equal(1, creations); Equal(1, sends); Equal(1, stops); Check(disposals >= 1);
            var original = wire.CloseAsync(); await original; Check(ReferenceEquals(original, wire.CloseAsync()));
        }
        finally { await wire.CloseAsync(); }
    }
    private sealed class Input(Func<ReadOnlyMemory<byte>, CancellationToken, Task> write) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => new(write(buffer, token));
        public override Task FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Output : Stream
    {
        private readonly Channel<byte[]> bytes = Channel.CreateUnbounded<byte[]>(); private byte[] current = []; private int offset;
        internal Action<CancellationToken>? BeforeRead;
        internal void Push(string text) => Push(Encoding.UTF8.GetBytes(text));
        internal void Push(byte[] data) { if (!bytes.Writer.TryWrite(data)) throw new InvalidOperationException("Synthetic output closed"); }
        internal void Finish() => bytes.Writer.TryComplete();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var before = BeforeRead; BeforeRead = null; before?.Invoke(token);
            while (offset == current.Length) { if (!await bytes.Reader.WaitToReadAsync(token)) return 0; if (!bytes.Reader.TryRead(out var next)) continue; current = next; offset = 0; }
            var count = Math.Min(buffer.Length, current.Length - offset); current.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Lease : IMcpAdmittedDuplexLease
    {
        internal readonly Output Pipe = new(); internal readonly ConcurrentQueue<JsonData> Sent = new();
        internal Func<JsonData, CancellationToken, Task>? Writer; internal Func<CancellationToken, Task>? Starter;
        internal Exception? CleanupFailure; internal int Closes, Starts; internal readonly Channel<JsonData> Messages = Channel.CreateUnbounded<JsonData>();
        internal Action? StopObserver; internal Func<Task>? StopTail;
        private readonly Lazy<Task> close; private Task? start;
        internal Lease()
        {
            Input = new Input(async (buffer, token) =>
            {
                var message = Json(Encoding.UTF8.GetString(buffer.Span).TrimEnd('\n')); Sent.Enqueue(message); Messages.Writer.TryWrite(message);
                if (Writer is { } writer) await writer(message, token);
            });
            close = new(async () => { Closes++; Pipe.Finish(); StopObserver?.Invoke(); if (StopTail is { } tail) await tail(); if (start is not null) await start; if (CleanupFailure is { } failure) throw failure; });
        }
        public Stream Input { get; } public Stream Output => Pipe;
        public Task StartAsync(CancellationToken token) => start = StartCore(token);
        private async Task StartCore(CancellationToken token) { Starts++; if (Starter is { } starter) await starter(token); token.ThrowIfCancellationRequested(); }
        public Task CloseAsync() => close.Value;
        internal async Task<JsonData> Next() => await Messages.Reader.ReadAsync();
        internal void Respond(JsonData request, string result = "{}") => Pipe.Push(Reply(request, result).ToString() + "\n");
    }
    private sealed class Context : IExtensionToolInvocationContext
    {
        public string OwnerId => "synthetic"; public long OwnerGeneration => 1; public string ToolCallId => "actual-call";
        public CancellationToken OperationCancellationToken => default; public CancellationToken SessionCancellationToken => default; public CancellationToken ExtensionLifetimeCancellationToken => default;
        public ValueTask ReportUpdateAsync(JsonData partialResult, CancellationToken token = default) => ValueTask.CompletedTask;
    }
    private static async Task ReachableRuntime()
    {
        var lease = new Lease(); lease.Writer = (message, token) =>
        {
            if (!message.Value.TryGetProperty("id", out _)) return Task.CompletedTask;
            lease.Respond(message, message.Value.GetProperty("method").GetString() switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"synthetic\",\"version\":\"1\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"actual_remote\",\"inputSchema\":{}}]}",
                "tools/call" => "{\"content\":[{\"type\":\"text\",\"text\":\"reached\"}]}", _ => "{}"
            }); return Task.CompletedTask;
        };
        var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease)); var config = McpConfigurationReader.Validate("test", Json("{\"command\":\"never-acquired\"}").Value).Config!;
        await using var runtime = new McpServerRuntime(new("test", config, "supplied", McpConfigurationScope.Global), new(1, "0.99.1"), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel), (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
        var catalog = await runtime.ConnectAsync(); Equal("actual_remote", catalog.OriginalTools.Single().Value.GetProperty("name").GetString());
        var result = await runtime.CallToolAsync("actual_remote", JsonData.EmptyObject, new Context()); Equal("reached", result.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Equal("initialize,notifications/initialized,tools/list,tools/call", string.Join(',', lease.Sent.Select(message => message.Value.GetProperty("method").GetString())));
    }
    private static async Task Correlation()
    {
        var lease = new Lease(); var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease, new(ReadBufferBytes: 2)));
        try
        {
            await channel.StartAsync(default); var first = channel.RequestAsync("first", null, new(0), default).AsTask(); var a = await lease.Next();
            var second = channel.RequestAsync("second", null, new(0), default).AsTask(); var b = await lease.Next();
            lease.Pipe.Push(" \r\n" + Reply(b, "{\"order\":2}").ToString() + "\r\n" + Reply(a, "{\"order\":1}").ToString() + "\n");
            Equal(1, (await first).Value.GetProperty("order").GetInt32()); Equal(2, (await second).Value.GetProperty("order").GetInt32());
        }
        finally { await channel.CloseAsync(); }
    }
    private static async Task Errors()
    {
        var lease = new Lease(); var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease));
        try
        {
            await channel.StartAsync(default); var call = channel.RequestAsync("test", null, new(0), default).AsTask(); var request = await lease.Next();
            lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"id\":" + request.Value.GetProperty("id") + ",\"error\":{\"code\":-32001,\"message\":\"remote\",\"data\":{\"owned\":true}}}\n");
            var error = await Throws<McpRemoteErrorException>(call); Equal(-32001d, error.Code); Check(error.Data!.Value.GetProperty("owned").GetBoolean());
            var bounded = channel.RequestAsync("test", null, new(0) { MaximumResponseBytes = 1 }, default).AsTask(); lease.Respond(await lease.Next()); await Throws<McpRuntimeProtocolException>(bounded);
        }
        finally { await channel.CloseAsync(); }
    }
    private static async Task Progress()
    {
        var lease = new Lease(); var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease)); var entered = Gate(); var release = Gate(); Task<JsonData>? call = null;
        try
        {
            await channel.StartAsync(default);
            call = channel.RequestAsync("tools/call", Json("{\"_meta\":{\"keep\":7}}"), new(0, async (progress, _) => { Equal(2d, progress.Progress); entered.TrySetResult(); await release.Task; }), default).AsTask();
            var request = await lease.Next(); var meta = request.Value.GetProperty("params").GetProperty("_meta"); Equal(7, meta.GetProperty("keep").GetInt32()); Equal(request.Value.GetProperty("id").GetInt64(), meta.GetProperty("progressToken").GetInt64());
            lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":" + request.Value.GetProperty("id") + ",\"progress\":2}}\n");
            await entered.Task; lease.Respond(request); Check(!call.IsCompleted); release.TrySetResult(); await call;
        }
        finally { release.TrySetResult(); if (call is not null) await Observe(call); await channel.CloseAsync(); }
    }
    private static async Task Cancellation()
    {
        foreach (var method in new[] { "tools/call", "initialize" })
        {
            var lease = new Lease(); var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease)); using var caller = new CancellationTokenSource();
            try
            {
                await channel.StartAsync(default); var call = channel.RequestAsync(method, null, new(0), caller.Token).AsTask(); var request = await lease.Next(); caller.Cancel(); await Throws<OperationCanceledException>(call);
                Equal(method == "initialize" ? 1 : 2, lease.Sent.Count);
                if (method != "initialize") { var cancel = lease.Sent.Last(); Equal("notifications/cancelled", cancel.Value.GetProperty("method").GetString()); Equal(request.Value.GetProperty("id").GetInt64(), cancel.Value.GetProperty("params").GetProperty("requestId").GetInt64()); }
            }
            finally { await channel.CloseAsync(); }
        }
    }
    private sealed class Clock : TimeProvider
    {
        internal readonly List<Timer> Timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state, dueTime); Timers.Add(timer); return timer; }
        internal sealed class Timer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal int Changes; internal TimeSpan Due = due; internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; Changes++; return true; }
            internal void Fire() { if (!Disposed) callback(state); }
            public void Dispose() => Disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static async Task Deadline()
    {
        var lease = new Lease(); var clock = new Clock(); var progress = Gate(); var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease), clock: clock);
        try
        {
            await channel.StartAsync(default); var call = channel.RequestAsync("test", null, new(10, (_, _) => { progress.TrySetResult(); return ValueTask.CompletedTask; }), default).AsTask(); var request = await lease.Next();
            lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":" + request.Value.GetProperty("id") + ",\"progress\":1}}\n");
            await progress.Task; Equal(1, clock.Timers.Single().Changes); clock.Timers.Single().Fire(); await Throws<McpRequestTimeoutException>(call); Check(clock.Timers.Single().Disposed); Equal(2, lease.Sent.Count);
        }
        finally { await channel.CloseAsync(); }
    }
    private static async Task Incoming()
    {
        var lease = new Lease(); var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease));
        try
        {
            await channel.ConfigureRootsAsync(Json("[{\"uri\":\"file:///synthetic\"}]"), default); await channel.StartAsync(default);
            foreach (var method in new[] { "ping", "roots/list", "missing" }) lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + method + "\",\"method\":\"" + method + "\"}\n");
            var replies = new[] { await lease.Next(), await lease.Next(), await lease.Next() }.ToDictionary(message => message.Value.GetProperty("id").GetString()!);
            Equal(1, replies["roots/list"].Value.GetProperty("result").GetProperty("roots").GetArrayLength()); Equal(-32601, replies["missing"].Value.GetProperty("error").GetProperty("code").GetInt32()); Check(replies["ping"].Value.TryGetProperty("result", out _));
        }
        finally { await channel.CloseAsync(); }
    }
    private static async Task Notification()
    {
        var lease = new Lease(); var handled = Gate(); McpJsonRpcRequestChannel? channel = null;
        channel = new(new McpStdioTransport(lease), notification: async (method, _, _) =>
        {
            Equal("notifications/tools/list_changed", method); ThrowsAction<InvalidOperationException>(() => channel!.CloseAsync());
            await channel!.RequestAsync("tools/list", null, new(0), default); handled.TrySetResult();
        });
        try { await channel.StartAsync(default); lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n"); lease.Respond(await lease.Next()); await handled.Task; }
        finally { await channel.CloseAsync(); }
    }
    private static Task NotificationDisconnect() => NotificationDisconnectCore(false);
    private static Task NotificationDisconnectFault() => NotificationDisconnectCore(true);
    private static async Task NotificationDisconnectCore(bool failCleanup)
    {
        var callbackEntered = Gate(); var callbackRelease = Gate(); var stopEntered = Gate(); var cleanupRelease = Gate();
        var cleanup = new IOException("retained deferred channel cleanup original");
        var first = new Lease { StopObserver = () => stopEntered.TrySetResult(), StopTail = () => cleanupRelease.Task,
            CleanupFailure = failCleanup ? cleanup : null };
        var second = new Lease(); var listed = 0; var acquired = 0;
        static string Initial(JsonData message) => message.Value.GetProperty("method").GetString() == "initialize"
            ? "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"synthetic\",\"version\":\"1\"}}"
            : "{\"tools\":[{\"name\":\"original\",\"inputSchema\":{}}]}";
        first.Writer = (message, token) =>
        {
            if (!message.Value.TryGetProperty("id", out var requestId)) return Task.CompletedTask;
            if (message.Value.GetProperty("method").GetString() == "tools/list" && ++listed > 1)
                throw new McpRuntimeDisconnectedException("actual notification list send disconnected");
            first.Respond(message, Initial(message)); return Task.CompletedTask;
        };
        second.Writer = (message, token) =>
        { if (message.Value.TryGetProperty("id", out var requestId)) second.Respond(message, Initial(message)); return Task.CompletedTask; };
        McpServerRuntime? runtime = null; McpJsonRpcRequestChannel? channel = null;
        channel = new(new McpStdioTransport(first), notification: async (method, parameters, token) =>
        {
            await Throws<McpRuntimeDisconnectedException>(runtime!.HandleNotificationAsync(method, token));
            var receipt = channel!.RetireAfterNotification();
            ThrowsAction<InvalidOperationException>(() => receipt.JoinAsync());
            ThrowsAction<InvalidOperationException>(() => channel.CloseAsync());
            ThrowsAction<InvalidOperationException>(() => runtime.CloseAsync());
            await Throws<InvalidOperationException>(runtime.ConnectAsync());
            Equal(1, acquired); callbackEntered.TrySetResult(); await callbackRelease.Task;
        });
        var next = new McpJsonRpcRequestChannel(new McpStdioTransport(second));
        var config = McpConfigurationReader.Validate("notification", Json("{\"command\":\"never-acquired\"}").Value).Config!;
        runtime = new(new("notification", config, "supplied", McpConfigurationScope.Global), new(1, "0.99.1"),
            (entry, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(++acquired == 1 ? channel! : next),
            (publication, token) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
        Task? reconnect = null, close = null;
        try
        {
            var before = await runtime.ConnectAsync();
            first.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n");
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, first.Closes); Equal(before.Revision, runtime.Snapshot.Revision); Check(!runtime.Snapshot.Catalog.Connected);
            reconnect = runtime.ConnectAsync(); Check(!reconnect.IsCompleted); Equal(1, acquired);
            callbackRelease.TrySetResult(); await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, first.Closes); Check(!reconnect.IsCompleted); cleanupRelease.TrySetResult();
            if (failCleanup)
            {
                Check(ReferenceEquals(cleanup, await Throws<IOException>(reconnect))); Equal(1, acquired);
                close = runtime.CloseAsync(); Check(Flatten(await Throws<Exception>(close)).Any(item => ReferenceEquals(item, cleanup)));
                Check(ReferenceEquals(close, runtime.CloseAsync())); Equal(1, first.Closes);
            }
            else { await reconnect; Equal(2, acquired); Check(runtime.Snapshot.Catalog.Connected); }
        }
        finally
        {
            callbackRelease.TrySetResult(); cleanupRelease.TrySetResult();
            if (reconnect is not null) await Observe(reconnect);
            await Observe(close ?? runtime.CloseAsync()); await Observe(next.CloseAsync());
        }
    }
    private static async Task NotificationExternalClose()
    {
        var entered = Gate(); var release = Gate(); var stop = Gate();
        var lease = new Lease { StopObserver = () => stop.TrySetResult() };
        McpJsonRpcRequestChannel? channel = null; IMcpAdmittedChannelRetirement? receipt = null;
        channel = new(new McpStdioTransport(lease), notification: async (method, parameters, token) =>
        { receipt = channel!.RetireAfterNotification(); entered.TrySetResult(); await release.Task; });
        Task? close = null;
        try
        {
            await channel.StartAsync(default); lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/test\"}\n");
            await entered.Task; close = channel.CloseAsync(); await stop.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted && !receipt!.JoinAsync().IsCompleted); Check(ReferenceEquals(close, channel.CloseAsync()));
            release.TrySetResult(); await close; await receipt!.JoinAsync(); Equal(1, lease.Closes);
        }
        finally { release.TrySetResult(); await Observe(close ?? channel.CloseAsync()); }
    }
    private static async Task HeldClose()
    {
        var lease = new Lease(); var entered = Gate(); var release = Gate(); var failure = new IOException("lease original cleanup failure"); lease.CleanupFailure = failure;
        lease.Writer = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease)); Task<JsonData>? call = null; Task? close = null;
        try
        {
            await channel.StartAsync(default); call = channel.RequestAsync("test", null, new(0), default).AsTask(); await entered.Task;
            close = channel.CloseAsync(); Check(ReferenceEquals(close, channel.CloseAsync())); Check(!close.IsCompleted && !call.IsCompleted);
            release.TrySetResult(); await Throws<McpRuntimeDisconnectedException>(call); Check(ReferenceEquals(failure, await Throws<IOException>(close))); Check(ReferenceEquals(failure, await Throws<IOException>(channel.CloseAsync()))); Equal(1, lease.Closes);
        }
        finally { release.TrySetResult(); if (call is not null) await Observe(call); if (close is not null) await Observe(close); }
    }
    private static async Task HeldStart()
    {
        var lease = new Lease(); var entered = Gate(); var release = Gate(); lease.Starter = async _ => { entered.TrySetResult(); await release.Task; };
        var transport = new McpStdioTransport(lease); Task? start = null, close = null;
        try
        {
            start = transport.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }), default); await entered.Task; close = transport.CloseAsync(); Check(!close.IsCompleted); release.TrySetResult();
            await Throws<OperationCanceledException>(start); await Throws<OperationCanceledException>(close); Equal(1, lease.Closes); Equal(0, lease.Sent.Count);
        }
        finally { release.TrySetResult(); if (start is not null) await Observe(start); if (close is not null) await Observe(close); }
    }
    private static async Task Framing()
    {
        foreach (var bytes in new[] { "{partial", new string('x', 20) })
        {
            var lease = new Lease(); var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously); var transport = new McpStdioTransport(lease, new(MaximumFrameBytes: 10));
            await transport.StartAsync(new(_ => ValueTask.CompletedTask, error => failed.TrySetResult(error)), default); lease.Pipe.Push(bytes); lease.Pipe.Finish();
            Check(await failed.Task is McpRuntimeProtocolException); await Throws<McpRuntimeProtocolException>(transport.CloseAsync()); Equal(1, lease.Closes);
        }
    }
    private static async Task StopBeforeCancelJoin()
    {
        var lease = new Lease(); var read = Gate(); var stopped = Gate(); CancellationTokenRegistration registration = default;
        lease.StopObserver = () => stopped.TrySetResult();
        lease.Pipe.BeforeRead = token => { registration = token.Register(() => stopped.Task.GetAwaiter().GetResult()); read.TrySetResult(); };
        var wire = new McpStdioTransport(lease); Task? close = null;
        try { await wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }), default); await read.Task; close = wire.CloseAsync(); Check(stopped.Task.IsCompleted); await close; Equal(1, lease.Closes); }
        finally { stopped.TrySetResult(); if (close is not null) await Observe(close); else await wire.CloseAsync(); registration.Dispose(); }
    }
    private sealed record HttpRecord(string Method, ImmutableDictionary<string, string> Headers, string? Body);
    private static McpHttpBinding Binding(McpHttpProfile? profile = null) => new(new Uri("https://synthetic.invalid/admitted"), ImmutableDictionary<string, string>.Empty.Add("X-Supplied", "yes"), profile ?? new(false, false));
    private static HttpResponseMessage Response(string body, string media = "application/json", HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, media) };
    private static async Task<HttpRecord> Record(HttpRequestMessage request)
    { return new(request.Method.Method, request.Headers.ToImmutableDictionary(header => header.Key, header => string.Join(',', header.Value), StringComparer.OrdinalIgnoreCase), request.Content is null ? null : await request.Content.ReadAsStringAsync()); }
    private static async Task HttpJson()
    {
        var seen = new List<HttpRecord>();
        var wire = Http(Binding(new(false, true)), async (request, _) =>
        {
            var row = await Record(request); seen.Add(row); if (row.Method == "DELETE") return Response("", status: HttpStatusCode.NoContent);
            var message = Json(row.Body!); var response = Response("[" + Reply(message, "{\"ok\":true}") + "]"); response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "synthetic-session"); return response;
        });
        var channel = new McpJsonRpcRequestChannel(wire);
        try { await channel.StartAsync(default); var result = await channel.RequestAsync("initialize", null, new(0), default); Check(result.Value.GetProperty("ok").GetBoolean()); await channel.SetProtocolVersionAsync("2025-11-25", default); await channel.RequestAsync("test", null, new(0), default); }
        finally { await channel.CloseAsync(); }
        Equal("synthetic-session", seen[1].Headers["Mcp-Session-Id"]); Equal("2025-11-25", seen[1].Headers["MCP-Protocol-Version"]); Equal("DELETE", seen.Last().Method); Equal(3, seen.Count);
    }
    private static async Task HttpSse()
    {
        var seen = new List<HttpRecord>(); JsonData? original = null;
        var wire = Http(Binding(new(false, false, MaximumReconnects: 1, InitialReconnectMilliseconds: 0)), async (request, _) =>
        {
            var row = await Record(request); seen.Add(row);
            if (row.Method == "POST") { original = Json(row.Body!); return Response("id: resume-marker\nretry: 0\nevent: ignored\ndata: {}\n\n", "text/event-stream"); }
            return Response("event: message\ndata: " + Reply(original!, "{\"resumed\":true}"), "text/event-stream");
        });
        var channel = new McpJsonRpcRequestChannel(wire);
        try { await channel.StartAsync(default); Check((await channel.RequestAsync("test", null, new(0), default)).Value.GetProperty("resumed").GetBoolean()); Equal("POST,GET", string.Join(',', seen.Select(row => row.Method))); Equal("resume-marker", seen[1].Headers["Last-Event-ID"]); }
        finally { await channel.CloseAsync(); }
    }
    private static async Task HttpGet()
    {
        var get = Gate(); var seen = new ConcurrentQueue<HttpRecord>(); var wire = Http(Binding(new(true, false)), async (request, _) =>
        { var row = await Record(request); seen.Enqueue(row); if (row.Method == "GET") { get.TrySetResult(); return Response("", status: HttpStatusCode.MethodNotAllowed); } return Response("", status: HttpStatusCode.Accepted); });
        var channel = new McpJsonRpcRequestChannel(wire);
        try { await channel.StartAsync(default); await channel.NotifyAsync("notifications/initialized", null, default); await get.Task; Equal("POST,GET", string.Join(',', seen.Select(row => row.Method))); }
        finally { await channel.CloseAsync(); }
    }
    private static async Task HttpBounds()
    {
        foreach (var kind in new[] { "accepted", "status", "type", "bound", "ssebound" })
        {
            var wire = Http(Binding(), (_, _) => ValueTask.FromResult(kind switch
            {
                "accepted" => Response("", status: HttpStatusCode.Accepted), "status" => Response("ignored", status: HttpStatusCode.Unauthorized),
                "type" => Response("ignored", "text/plain"), "ssebound" => Response("data: " + new string('x', 300), "text/event-stream"), _ => Response(new string('x', 300))
            }), new(MaximumFrameBytes: 200));
            var channel = new McpJsonRpcRequestChannel(wire);
            try { await channel.StartAsync(default); if (kind == "status") Equal(401, (await Throws<McpHttpStatusException>(channel.RequestAsync("test", null, new(0), default).AsTask())).StatusCode); else await Throws<McpRuntimeProtocolException>(channel.RequestAsync("test", null, new(0), default).AsTask()); }
            finally { await channel.CloseAsync(); }
        }
    }
    private static async Task HttpHeld()
    {
        var entered = Gate(); var release = Gate(); var deleteEntered = Gate(); var deleteRelease = Gate(); using var caller = new CancellationTokenSource();
        var wire = Http(Binding(new(false, true)), async (request, token) =>
        {
            if (request.Method == HttpMethod.Delete) { deleteEntered.TrySetResult(); await deleteRelease.Task; return Response("", status: HttpStatusCode.NoContent); }
            var message = Json((await Record(request)).Body!);
            if (message.Value.GetProperty("method").GetString() == "initialize") { var response = Response(Reply(message).ToString()); response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "synthetic"); return response; }
            if (message.Value.GetProperty("method").GetString() == "notifications/cancelled") return Response("", status: HttpStatusCode.Accepted);
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return Response(Reply(message).ToString());
        });
        var channel = new McpJsonRpcRequestChannel(wire); Task<JsonData>? call = null; Task? close = null;
        try
        {
            await channel.StartAsync(default); await channel.RequestAsync("initialize", null, new(0), default);
            call = channel.RequestAsync("tools/call", null, new(0), caller.Token).AsTask(); await entered.Task; caller.Cancel(); Check(!call.IsCompleted); release.TrySetResult(); await Throws<OperationCanceledException>(call);
            close = channel.CloseAsync(); await deleteEntered.Task; Check(!close.IsCompleted && ReferenceEquals(close, channel.CloseAsync())); deleteRelease.TrySetResult(); await close;
        }
        finally { release.TrySetResult(); deleteRelease.TrySetResult(); if (call is not null) await Observe(call); if (close is not null) await Observe(close); else await channel.CloseAsync(); }
    }
    private static async Task Admission()
    {
        var lease = new Lease(); var limits = new McpTransportLimits(MaximumInflight: 1, MaximumRequestId: 1); var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease), limits);
        Task<JsonData>? first = null;
        try { await channel.StartAsync(default); first = channel.RequestAsync("first", null, new(0), default).AsTask(); var request = await lease.Next(); await Throws<InvalidOperationException>(channel.RequestAsync("overflow", null, new(0), default).AsTask()); lease.Respond(request); await first; await Throws<InvalidOperationException>(channel.RequestAsync("exhausted", null, new(0), default).AsTask()); Equal(1, lease.Sent.Count); }
        finally { await channel.CloseAsync(); if (first is not null) await Observe(first); }
    }
    private sealed class HeldBody(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate(), DisposeEntered = Gate(); internal bool Disposed, Released;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task; Released = true; token.ThrowIfCancellationRequested(); return await base.ReadAsync(buffer, token); }
        protected override void Dispose(bool disposing) { Disposed = true; DisposeEntered.TrySetResult(); if (Released) base.Dispose(disposing); }
    }
    private static async Task HttpBody()
    {
        foreach (var isGet in new[] { false, true })
        {
            var body = new HeldBody(""); var wire = Http(Binding(new(isGet, false)), (request, _) => ValueTask.FromResult(
                request.Method == HttpMethod.Get || !isGet ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("text/event-stream") } } } : Response("", status: HttpStatusCode.Accepted)));
            var channel = new McpJsonRpcRequestChannel(wire); Task? call = null, close = null;
            try
            {
                await channel.StartAsync(default); call = isGet ? channel.NotifyAsync("notifications/initialized", null, default).AsTask() : channel.RequestAsync("test", null, new(0), default).AsTask();
                await body.Entered.Task; close = channel.CloseAsync(); await body.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!close.IsCompleted && body.Disposed); body.Release.TrySetResult();
                if (!isGet) await Throws<McpRuntimeDisconnectedException>(call); else await call;
                await close; Check(body.Disposed); Check(ReferenceEquals(close, channel.CloseAsync()));
            }
            finally { body.Release.TrySetResult(); if (call is not null) await Observe(call); if (close is not null) await Observe(close); else await channel.CloseAsync(); }
        }
    }
    private static async Task CallbackOwnership()
    {
        var lease = new Lease(); var entered = Gate(); var release = Gate(); var failure = new IOException("actual notification callback failure");
        var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease), notification: (_, _, _) => { entered.TrySetResult(); return new(release.Task); }); Task? close = null;
        try
        {
            await channel.StartAsync(default); lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/test\"}\n"); await entered.Task;
            close = channel.CloseAsync(); Check(!close.IsCompleted); release.TrySetException(failure);
            var wrapper = await Throws<McpChannelCallbackException>(close);
            Check(ReferenceEquals(wrapper.Original, release.Task) && wrapper.Evidence is AggregateException aggregate &&
                aggregate.InnerExceptions.Count == 1 && ReferenceEquals(aggregate.InnerExceptions[0], failure));
            Check(ReferenceEquals(close, channel.CloseAsync()) && ReferenceEquals(wrapper, await Throws<McpChannelCallbackException>(channel.CloseAsync())));
        }
        finally { release.TrySetResult(); if (close is not null) await Observe(close); }
        var progressLease = new Lease(); var progressChannel = new McpJsonRpcRequestChannel(new McpStdioTransport(progressLease)); var callback = Gate();
        try
        {
            await progressChannel.StartAsync(default); var call = progressChannel.RequestAsync("test", null, new(0, (_, _) => { callback.TrySetResult(); return ValueTask.FromException(failure); }), default).AsTask(); var request = await progressLease.Next();
            progressLease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":" + request.Value.GetProperty("id") + ",\"progress\":1}}\n");
            await callback.Task; progressLease.Respond(request); Check(ReferenceEquals(failure, await Throws<IOException>(call)));
        }
        finally { await progressChannel.CloseAsync(); }
    }
    private sealed class FailingBody(Exception readFailure, Exception cleanupFailure) : MemoryStream
    {
        internal bool CleanupAttempted;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromException<int>(readFailure);
        protected override void Dispose(bool disposing) { CleanupAttempted = true; throw cleanupFailure; }
        public override ValueTask DisposeAsync() { CleanupAttempted = true; return ValueTask.FromException(cleanupFailure); }
    }
    private static async Task HttpCleanupFault()
    {
        var read = new IOException("actual response read failed"); var cleanup = new IOException("actual response cleanup failed"); var body = new FailingBody(read, cleanup);
        var wire = Http(Binding(), (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("application/json") } } }));
        var channel = new McpJsonRpcRequestChannel(wire);
        try { await channel.StartAsync(default); var error = await Throws<AggregateException>(channel.RequestAsync("test", null, new(0), default).AsTask()); Check(ReferenceEquals(read, error.InnerExceptions[0])); Check(ReferenceEquals(cleanup, error.InnerExceptions[1])); Equal(2, error.InnerExceptions.Count); Check(body.CleanupAttempted); }
        finally { var original = channel.CloseAsync(); var error = await Throws<Exception>(original); Check(Flatten(error).Any(item => ReferenceEquals(item, cleanup))); Check(ReferenceEquals(original, channel.CloseAsync())); }
    }
    private static async Task CallbackBound()
    {
        var lease = new Lease(); var entered = Gate(); var release = Gate(); var callbacks = 0;
        var channel = new McpJsonRpcRequestChannel(new McpStdioTransport(lease), new(MaximumCallbacks: 1), notification: async (_, _, _) => { callbacks++; entered.TrySetResult(); await release.Task; }); Task? close = null;
        try
        {
            await channel.StartAsync(default); lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/one\"}\n"); await entered.Task;
            // A tracked request is the deterministic receipt that the second frame failed the reader.
            var request = channel.RequestAsync("waiting", null, new(0), default).AsTask(); await lease.Next();
            lease.Pipe.Push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/two\"}\n"); await Throws<McpRuntimeDisconnectedException>(request); Equal(1, callbacks);
            close = channel.CloseAsync(); Check(!close.IsCompleted); release.TrySetResult(); await Throws<Exception>(close);
        }
        finally { release.TrySetResult(); if (close is not null) await Observe(close); else await Observe(channel.CloseAsync()); }
    }
    private sealed class ReadFailureBody(Exception failure, string prefix = "") : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        internal bool CleanupAttempted;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Position < Length ? base.ReadAsync(buffer, token) : ValueTask.FromException<int>(failure);
        protected override void Dispose(bool disposing) { CleanupAttempted = true; base.Dispose(disposing); }
    }
    private sealed class CleanupFailureBody(string text, Exception failure) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        internal bool CleanupAttempted;
        protected override void Dispose(bool disposing) { CleanupAttempted = true; throw failure; }
        public override ValueTask DisposeAsync() { CleanupAttempted = true; return ValueTask.FromException(failure); }
    }
    private static HttpResponseMessage SseBody(Stream body) => new(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("text/event-stream") } } };
    private static async Task SseTerminalFaults()
    {
        var read = new IOException("actual initial SSE read failed"); var cleanup = new IOException("actual initial SSE cleanup failed"); var body = new FailingBody(read, cleanup); var sends = new List<string>();
        var wire = Http(Binding(new(false, false, MaximumReconnects: 2, InitialReconnectMilliseconds: 0)), (request, _) => { sends.Add(request.Method.Method); return ValueTask.FromResult(SseBody(body)); });
        var channel = new McpJsonRpcRequestChannel(wire);
        try
        {
            await channel.StartAsync(default); var error = await Throws<AggregateException>(channel.RequestAsync("test", null, new(0), default).AsTask());
            Equal(2, error.InnerExceptions.Count); Check(ReferenceEquals(read, error.InnerExceptions[0])); Check(ReferenceEquals(cleanup, error.InnerExceptions[1])); Check(body.CleanupAttempted); Equal("POST", string.Join(',', sends));
        }
        finally { Check(Flatten(await Throws<Exception>(channel.CloseAsync())).Any(item => ReferenceEquals(item, cleanup))); }
        var resumedRead = new IOException("actual terminal resumed SSE read failed"); var resumedBody = new ReadFailureBody(resumedRead); var resumedSends = new List<string>();
        var resumedWire = Http(Binding(new(false, false, MaximumReconnects: 1, InitialReconnectMilliseconds: 0)), (request, _) =>
        { resumedSends.Add(request.Method.Method); return ValueTask.FromResult(request.Method == HttpMethod.Post ? Response("id: primed\n\n", "text/event-stream") : SseBody(resumedBody)); });
        var resumedChannel = new McpJsonRpcRequestChannel(resumedWire);
        try { await resumedChannel.StartAsync(default); Check(ReferenceEquals(resumedRead, await Throws<IOException>(resumedChannel.RequestAsync("test", null, new(0), default).AsTask()))); Check(resumedBody.CleanupAttempted); Equal("POST,GET", string.Join(',', resumedSends)); }
        finally { await resumedChannel.CloseAsync(); }
        var initialRead = new IOException("initial primed SSE read failed before unanswered resume"); var initialBody = new ReadFailureBody(initialRead, "id: primed\n\n"); var eofSends = new List<string>();
        var eofWire = Http(Binding(new(false, false, MaximumReconnects: 1, InitialReconnectMilliseconds: 0)), (request, _) =>
        { eofSends.Add(request.Method.Method); return ValueTask.FromResult(request.Method == HttpMethod.Post ? SseBody(initialBody) : Response("id: next\n\n", "text/event-stream")); });
        var eofChannel = new McpJsonRpcRequestChannel(eofWire);
        try { await eofChannel.StartAsync(default); Check(ReferenceEquals(initialRead, await Throws<IOException>(eofChannel.RequestAsync("test", null, new(0), default).AsTask()))); Check(initialBody.CleanupAttempted); Equal("POST,GET", string.Join(',', eofSends)); }
        finally { await eofChannel.CloseAsync(); }
    }
    private static async Task SseResumeCleanup()
    {
        foreach (var answered in new[] { true, false })
        {
            var cleanup = new IOException("actual resumed SSE cleanup failed"); var sends = new List<HttpRecord>(); CleanupFailureBody? body = null; JsonData? original = null;
            var wire = Http(Binding(new(false, false, MaximumReconnects: 2, InitialReconnectMilliseconds: 0)), async (request, _) =>
            {
                var row = await Record(request); sends.Add(row);
                if (row.Method == "POST") { original = Json(row.Body!); return Response("id: primed\n\n", "text/event-stream"); }
                body = new(answered ? "data: " + Reply(original!, "{\"observed\":true}") + "\n\n" : "id: second-prime\n\n", cleanup);
                return SseBody(body);
            });
            var channel = new McpJsonRpcRequestChannel(wire);
            try
            {
                await channel.StartAsync(default); Check(ReferenceEquals(cleanup, await Throws<IOException>(channel.RequestAsync("test", null, new(0), default).AsTask())));
                Check(body is { CleanupAttempted: true }); Equal("POST,GET", string.Join(',', sends.Select(row => row.Method))); Equal("primed", sends[1].Headers["Last-Event-ID"]);
            }
            finally { Check(Flatten(await Throws<Exception>(channel.CloseAsync())).Any(item => ReferenceEquals(item, cleanup))); }
        }
    }
    private static IEnumerable<Exception> Flatten(Exception error) => error is AggregateException aggregate ? aggregate.InnerExceptions.SelectMany(Flatten) : [error];
    private static async Task<T> Throws<T>(Task task) where T : Exception { try { await task; } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void ThrowsAction<T>(Func<object> run) where T : Exception { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Observe(Task task) { try { await task; } catch { } }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("MCP wire synthetic assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}"); }
}
