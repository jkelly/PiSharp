// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/src/connection.ts.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Env.Protocol;

namespace PiSharp.Env;

internal sealed class ConnectionSession(long id, Process process, string token, ConnectionOptions options,
    Action<ConnectionSession> onLost) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<uint, Pending> pending = new();
    private readonly Channel<byte[]> writes = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    private readonly object diagnosticsGate = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object disposalGate = new();
    private Task? disposal;
    private string diagnostics = "";
    private int live = 1;
    private bool ready;
    private long lastSeen = Environment.TickCount64;
    private Task reader = Task.CompletedTask, writer = Task.CompletedTask, stderr = Task.CompletedTask;
    private Task timer = Task.CompletedTask, exited = Task.CompletedTask, cleanup = Task.CompletedTask;

    public long Id { get; } = id;
    public bool Live => Volatile.Read(ref live) == 1;

    public void Start()
    {
        try
        {
            stderr = ReadStderrAsync();
            writer = WriteAsync();
            reader = ReadAsync();
            timer = KeepAliveAsync();
            exited = ObserveExitAsync();
        }
        finally { started.TrySetResult(); }
    }

    public void MarkReady() { lock (diagnosticsGate) { ready = true; diagnostics = ""; } }

    public Task<RemoteReply> RequestAsync(uint requestId, string op, JsonObject json,
        RemoteRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        if (!Live) return Task.FromException<RemoteReply>(RemoteError.Lost("pi-env connection lost"));
        // Snapshot all bytes before returning; callers may reuse their JSON/payload buffers immediately.
        var body = (JsonObject)json.DeepClone();
        body["op"] = op;
        var bytes = FrameCodec.Encode(new(FrameType.Request, requestId, body, requestOptions?.Payload ?? default));
        var request = new Pending(requestOptions?.OnEvent);
        if (!pending.TryAdd(requestId, request)) throw new InvalidOperationException("Duplicate request ID.");
        try
        {
            requestOptions?.OnStart?.Invoke(requestId, Id);
            if (!Live || !writes.Writer.TryWrite(bytes)) throw RemoteError.Lost("pi-env connection lost");
            // Register only after the request is queued: an immediate cancel cannot overtake it.
            request.Register(cancellationToken, () => Abort(requestId, request));
        }
        catch (Exception error)
        {
            if (pending.TryRemove(requestId, out var removed)) removed.Fail(error);
        }
        return request.Completion.Task;
    }

    public void Send(ProtocolFrame frame)
    {
        if (!Live) return;
        var bytes = FrameCodec.Encode(frame);
        if (!writes.Writer.TryWrite(bytes)) Fail(RemoteError.Lost("pi-env connection lost"));
    }

    private void Abort(uint requestId, Pending request)
    {
        if (!pending.TryGetValue(requestId, out var active) || !ReferenceEquals(request, active)) return;
        Send(new(FrameType.Cancel, requestId, new()));
        _ = AbortAfterGraceAsync(requestId, request);
    }

    private async Task AbortAfterGraceAsync(uint requestId, Pending request)
    {
        try
        {
            await Task.Delay(options.AbortGrace, request.Settled.Token).ConfigureAwait(false);
            if (pending.TryRemove(new KeyValuePair<uint, Pending>(requestId, request))) request.Fail(RemoteError.Aborted());
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var bytes in writes.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes, stop.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        { if (Live) Fail(RemoteError.Lost("pi-env input failed: " + error.Message)); }
    }

    private async Task ReadAsync()
    {
        try
        {
            using var progress = new ProgressStream(process.StandardOutput.BaseStream, () => Interlocked.Exchange(ref lastSeen, Environment.TickCount64));
            using var input = new BufferedStream(progress, 64 * 1024);
            await SynchronizeAsync(input).ConfigureAwait(false);
            while (Live)
            {
                var frame = await FrameCodec.ReadAsync(input, stop.Token).ConfigureAwait(false);
                if (frame is null) { Fail(RemoteError.Lost("pi-env connection lost")); break; }
                if (frame.Type == FrameType.Event)
                {
                    if (pending.TryGetValue(frame.Id, out var request))
                    {
                        try { request.OnEvent?.Invoke(frame.Json, frame.Payload); }
                        catch (Exception error)
                        {
                            if (pending.TryRemove(frame.Id, out var removed)) removed.Fail(error);
                            Send(new(FrameType.Cancel, frame.Id, new()));
                        }
                    }
                }
                else if (frame.Type is FrameType.Result or FrameType.Error && pending.TryRemove(frame.Id, out var request))
                {
                    if (frame.Type == FrameType.Result) request.Succeed(new(frame.Json, frame.Payload.ToArray(), Id));
                    else request.Fail(new RemoteError(frame.Json));
                }
            }
        }
        // InvalidDataException derives from SystemException, not IOException: a malformed frame is an expected
        // transport loss, and must settle requests before a later disposal joins the reader task.
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        { if (Live) Fail(RemoteError.Lost("Corrupt or closed pi-env connection: " + error.Message)); }
    }

    private async Task SynchronizeAsync(Stream input)
    {
        var marker = Encoding.ASCII.GetBytes("PI-ENV " + token + "\n");
        var matched = 0;
        var single = new byte[1];
        var noise = new List<byte>();
        while (true)
        {
            if (await input.ReadAsync(single, stop.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            var value = single[0];
            noise.Add(value);
            matched = value == marker[matched] ? matched + 1 : value == marker[0] ? 1 : 0;
            if (matched == marker.Length)
            {
                noise.RemoveRange(noise.Count - marker.Length, marker.Length);
                var text = Encoding.UTF8.GetString(noise.ToArray()).Trim();
                if (text.Length > 0) Log(text);
                return;
            }
            if (noise.Count > 1024 * 1024) noise.RemoveRange(0, noise.Count - marker.Length);
        }
    }

    private async Task ReadStderrAsync()
    {
        var chars = new char[4096];
        try
        {
            while (true)
            {
                var count = await process.StandardError.ReadAsync(chars.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (count == 0) return;
                var text = new string(chars, 0, count);
                lock (diagnosticsGate)
                {
                    if (!ready)
                    {
                        diagnostics += text;
                        if (diagnostics.Length > 2000) diagnostics = diagnostics[^2000..];
                    }
                }
                Log(text);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    private async Task KeepAliveAsync()
    {
        try
        {
            using var interval = new PeriodicTimer(options.PingInterval);
            while (await interval.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastSeen) > options.SilenceLimit.TotalMilliseconds)
                { Fail(RemoteError.Lost("pi-env connection timed out")); return; }
                Send(new(FrameType.Ping, 0, new()));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            Fail(RemoteError.Lost($"pi-env exited with code {process.ExitCode}"));
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
    }

    public void Fail(RemoteError error)
    {
        if (Interlocked.Exchange(ref live, 0) == 0) return;
        writes.Writer.TryComplete();
        // Let stderr drain on a pre-ready exit; stdout and writer stop immediately.
        stop.Cancel();
        cleanup = StopProcessAsync();
        // Clear the owner's ready session before publishing a failed request. Its caller may reconnect immediately.
        onLost(this);
        foreach (var entry in pending)
            if (pending.TryRemove(entry.Key, out var request)) request.Fail(error);
    }

    private async Task StopProcessAsync()
    {
        try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        try
        {
            // EOF is the daemon's orderly shutdown signal. Escalate for a broken transport/daemon.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
        }
        catch (InvalidOperationException) { }
    }

    public async Task DrainStartupDiagnosticsAsync()
    {
        try { await stderr.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    public RemoteError WithDiagnostics(RemoteError error)
    {
        string tail;
        lock (diagnosticsGate) tail = diagnostics;
        var clean = Sanitize(tail);
        if (clean.Length == 0) return error;
        var fields = (JsonObject)error.Fields.DeepClone();
        fields["message"] = error.Message + "\nremote stderr:\n" + clean;
        return new(fields);
    }

    private void Log(string text) { try { options.OnLog?.Invoke(text); } catch { /* Diagnostics observers do not own the connection. */ } }

    internal static string Sanitize(string text)
    {
        var clean = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var value = text[i];
            if (value == '\x1b')
            {
                if (i + 1 >= text.Length) break;
                var kind = text[++i];
                if (kind == '[') { while (i + 1 < text.Length && text[++i] is not (>= '@' and <= '~')) { } }
                else if (kind is ']' or 'P' or '^' or '_')
                {
                    while (++i < text.Length)
                        if (text[i] == '\a' || (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')) { if (text[i] == '\x1b') i++; break; }
                }
                continue;
            }
            if (value == '\r') { clean.Append('\n'); if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (value is '\n' or '\t' || value >= ' ' && (value < '\x7f' || value > '\x9f')) clean.Append(value);
        }
        return string.Join('\n', clean.ToString().Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length != 0).TakeLast(20));
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        // Publish the disposal task before Fail can ask the owner to reap this same session.
        await Task.Yield();
        Fail(RemoteError.Closed());
        await started.Task.ConfigureAwait(false);
        try { await Task.WhenAll(reader, writer, stderr, timer, exited, cleanup).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        finally { process.Dispose(); }
    }

    private sealed class Pending(Action<JsonObject, ReadOnlyMemory<byte>>? onEvent)
    {
        private readonly object gate = new();
        private CancellationTokenRegistration registration;
        private bool completed;
        public TaskCompletionSource<RemoteReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Settled { get; } = new();
        public Action<JsonObject, ReadOnlyMemory<byte>>? OnEvent { get; } = onEvent;
        public void Register(CancellationToken cancellationToken, Action callback)
        {
            lock (gate)
            {
                if (completed) return;
                registration = cancellationToken.UnsafeRegister(_ => callback(), null);
                if (completed) registration.Unregister();
            }
        }
        public void Succeed(RemoteReply reply) { Finish(); Completion.TrySetResult(reply); }
        public void Fail(Exception error) { Finish(); Completion.TrySetException(error); }
        private void Finish()
        {
            lock (gate) { completed = true; registration.Unregister(); Settled.Cancel(); }
        }
    }

    private sealed class ProgressStream(Stream inner, Action progress) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var result = inner.Read(buffer, offset, count); if (result > 0) progress(); return result; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var result = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (result > 0) progress(); return result; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
