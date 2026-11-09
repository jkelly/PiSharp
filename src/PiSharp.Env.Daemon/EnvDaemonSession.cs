// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/daemon/src/main.rs; native .NET session owner.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Env.Protocol;

namespace PiSharp.Env.Daemon;

/// <summary>One connection's frames, requests, file handles and owned process/watch resources. A session is never reused.</summary>
public sealed class EnvDaemonSession : IAsyncDisposable
{
    private readonly Stream input, output;
    private readonly EnvDaemonOptions options;
    private readonly SemaphoreSlim workers;
    private readonly DaemonOutput sender;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<uint, Pending> pending = new();
    private readonly Dictionary<string, IEnvDaemonOperation> operations;
    private readonly object resourcesLock = new();
    private readonly List<IAsyncDisposable> resources = [];
    private readonly DaemonFileSystem files;
    private long lastReceived = Environment.TickCount64;
    private int disposed, running;
    private bool greeted;
    public ConcurrentDictionary<string, object> State { get; } = new();
    public CancellationToken CancellationToken => lifetime.Token;

    public EnvDaemonSession(Stream input, Stream output, EnvDaemonOptions? options = null)
    {
        this.input = new ActivityStream(input, () => Interlocked.Exchange(ref lastReceived, Environment.TickCount64)); this.output = output; this.options = options ?? new();
        if (this.options.MaxHandles is < 1 or > 4096 || this.options.FileWorkers < 1 || this.options.MaxPendingRequests < 1 ||
            this.options.ShutdownDrainTimeout < TimeSpan.Zero || this.options.ShutdownDrainTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options));
        workers = new(this.options.FileWorkers);
        sender = new(output, lifetime.Token);
        operations = this.options.Operations.ToDictionary(x => x.Name, StringComparer.Ordinal);
        files = new(this.options.WorkingDirectory, this.options.MaxHandles);
        Own(files);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("A daemon session runs once.");
        using var cancel = cancellationToken.Register(lifetime.Cancel);
        Task timer = Task.CompletedTask;
        try
        {
            if (options.Token is { } token)
            {
                if (token.Length != 32 || token.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Token must be 32 hexadecimal characters.");
                await output.WriteAsync(Encoding.ASCII.GetBytes($"PI-ENV {token}\n"), lifetime.Token).ConfigureAwait(false);
                await output.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
            sender.Start();
            timer = RunLivenessAsync();
            while (!lifetime.IsCancellationRequested)
            {
                ProtocolFrame? frame;
                var read = FrameCodec.ReadAsync(input, lifetime.Token).AsTask();
                try { frame = await read.WaitAsync(lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    // Stdio implementations may not interrupt an already pending OS read. The session
                    // still ends promptly; it is never reused, and any late read is only observed.
                    _ = ObserveAsync(read);
                    throw;
                }
                catch (ProtocolJsonException error)
                {
                    await SendAsync(new(FrameType.Error, error.Id, ErrorJson(new EnvDaemonException("EINVAL", error.Message))), lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                if (frame is null) break;
                Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
                if (frame.Type == FrameType.Ping) continue;
                if (frame.Type == FrameType.Cancel)
                {
                    if (pending.TryGetValue(frame.Id, out var target))
                    {
                        if (frame.Json["mode"]?.ToString() == "kill") target.Request.MarkKillRequested();
                        else target.Request.MarkAbortRequested();
                        try { target.Cancel.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    continue;
                }
                if (frame.Type != FrameType.Request) throw new InvalidDataException("Client sent a non-request frame.");
                if (frame.Id == 0 || pending.ContainsKey(frame.Id)) throw new InvalidDataException("Invalid or duplicate request id.");
                if (pending.Count >= options.MaxPendingRequests)
                {
                    await SendAsync(new(FrameType.Error, frame.Id, ErrorJson(new EnvDaemonException("EBUSY", "Too many pending requests"))), lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                var cancelRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var request = new EnvDaemonRequest(this, frame.Id, frame.Json, frame.Payload, cancelRequest.Token);
                var entry = new Pending(request, cancelRequest);
                pending[frame.Id] = entry; // Cancellation can observe the request before it enters any worker queue.
                var op = frame.Json["op"] is JsonValue value && value.TryGetValue<string>(out var operationName) ? operationName : null;
                if (op is "hello" or "ptyWrite" or "ptyResize" or "ptyForeground")
                {
                    // Handshake is ordered before any subsequent operation.
                    entry.Task = ExecuteAsync(entry, op);
                    await entry.Task.ConfigureAwait(false);
                }
                else
                {
                    var previous = files.Predecessor(frame.Json, entry.Completion.Task);
                    entry.Task = Task.Run(async () => { await previous.ConfigureAwait(false); await ExecuteAsync(entry, op).ConfigureAwait(false); });
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            files.StopAcceptingHandles();
            // POSIX raw read-mode FIFO open is deliberately blocking. As upstream's process::exit does,
            // disconnect must leave those background workers behind rather than wait forever. Closed
            // handle admission and lifetime-bound writes make any late completion cleanup-only.
            var drain = Task.WhenAll(CleanupAsync(), Task.WhenAll(pending.Values.Select(p => p.Task)), sender.Completion, ObserveAsync(timer));
            try { await drain.WaitAsync(options.ShutdownDrainTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { _ = ObserveAsync(drain); }
        }
    }

    private async Task ExecuteAsync(Pending entry, string? op)
    {
        var worker = false;
        try
        {
            EnvDaemonReply result;
            if (op == "hello")
            {
                if (entry.Request.Json["protocol"]?.GetValue<int>() != 1) throw new EnvDaemonException("EINVAL", "Unsupported protocol version");
                greeted = true;
                result = Hello();
            }
            else
            {
                if (!greeted) throw new EnvDaemonException("EINVAL", "hello must be the first request");
                if (op is null) throw new EnvDaemonException("EINVAL", "Missing operation");
                if (operations.TryGetValue(op, out var extension)) result = await extension.ExecuteAsync(entry.Request).ConfigureAwait(false);
                else
                {
                    await workers.WaitAsync(lifetime.Token).ConfigureAwait(false); worker = true;
                    result = await files.ExecuteAsync(entry.Request, op).ConfigureAwait(false);
                }
            }
            await SendAsync(new(FrameType.Result, entry.Request.Id, result.Json, result.Payload), lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested)
            {
                try { await SendAsync(new(FrameType.Error, entry.Request.Id, ErrorJson(error)), lifetime.Token).ConfigureAwait(false); }
                catch (Exception sendError) when (sendError is IOException or OperationCanceledException or ObjectDisposedException) { lifetime.Cancel(); }
            }
        }
        finally
        {
            if (worker) workers.Release();
            pending.TryRemove(entry.Request.Id, out _);
            entry.Completion.TrySetResult();
            entry.Cancel.Dispose();
        }
    }

    private EnvDaemonReply Hello()
    {
        var drives = new JsonObject();
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (variable.Key is string key && key.Length == 3 && key[0] == '=' && key[2] == ':') drives[key[1..]] = variable.Value?.ToString();
        return new(new JsonObject
        {
            ["protocol"] = 1, ["version"] = options.Version,
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsAndroid() ? "android" : "linux",
            ["arch"] = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", Architecture.X86 => "x86", _ => "arm" },
            ["home"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ["tmpdir"] = TemporaryDirectory(),
            ["cwd"] = Path.GetFullPath(options.WorkingDirectory), ["driveCwds"] = drives, ["separator"] = Path.DirectorySeparatorChar.ToString(),
            ["pid"] = Environment.ProcessId, ["features"] = new JsonArray(new[] { "listFiles" }.Concat(options.Features).Distinct().Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        });
    }
    private static string TemporaryDirectory()
    {
        string? Get(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : null;
        var path = OperatingSystem.IsWindows()
            ? Get("TEMP") ?? Get("TMP") ?? Path.Combine(Get("SystemRoot") ?? Get("windir") ?? @"C:\Windows", "temp")
            : Get("TMPDIR") ?? Get("TMP") ?? Get("TEMP") ?? "/tmp";
        return Path.TrimEndingDirectorySeparator(path);
    }

    internal ValueTask SendEventAsync(uint id, JsonObject json, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        SendAsync(new(FrameType.Event, id, json, payload), cancellationToken);
    private async ValueTask SendAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try { await sender.SendAsync(frame, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && cancellationToken.IsCancellationRequested) { throw; }
        catch { lifetime.Cancel(); throw; }
    }
    private async Task RunLivenessAsync()
    {
        using var timer = new PeriodicTimer(options.PingInterval);
        Task? ping = null;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastReceived) >= options.IdleTimeout.TotalMilliseconds) { lifetime.Cancel(); break; }
                // A peer that stops reading must not stop the independent input-silence deadline.
                // Keep at most one ping outstanding instead of waiting for stdout on the monitor.
                if (ping is { IsCompleted: false }) continue;
                if (ping is not null) await ping.ConfigureAwait(false);
                ping = SendAsync(new(FrameType.Ping, 0, new()), lifetime.Token).AsTask();
            }
        }
        finally { if (ping is not null) await ObserveAsync(ping).ConfigureAwait(false); }
    }
    internal void Own(IAsyncDisposable resource)
    {
        lock (resourcesLock)
        {
            if (!lifetime.IsCancellationRequested) { resources.Add(resource); return; }
        }
        // Startup lost the disconnect race; dispose synchronously before returning ownership.
        resource.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    internal void Release(IAsyncDisposable resource)
    {
        lock (resourcesLock) resources.Remove(resource);
    }
    private async Task CleanupAsync()
    {
        IAsyncDisposable[] owned;
        lock (resourcesLock) { owned = resources.ToArray(); resources.Clear(); }
        await Task.WhenAll(owned.Reverse().Select(resource => Task.Run(async () =>
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); } catch { /* Best effort on disconnect. */ }
        }))).ConfigureAwait(false);
    }
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { /* Disconnect drains are best effort. */ } }
    internal static JsonObject ErrorJson(Exception error) => DaemonErrors.Json(error);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); files.StopAcceptingHandles();
        var cleanup = CleanupAsync();
        try { await cleanup.WaitAsync(options.ShutdownDrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { _ = ObserveAsync(cleanup); }
    }
    private sealed class Pending(EnvDaemonRequest request, CancellationTokenSource cancel)
    {
        public EnvDaemonRequest Request { get; } = request;
        public CancellationTokenSource Cancel { get; } = cancel;
        public Task Task { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ActivityStream(Stream inner, Action received) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); if (read != 0) received(); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (read != 0) received(); return read; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
