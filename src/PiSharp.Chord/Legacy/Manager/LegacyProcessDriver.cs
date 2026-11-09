// Native .NET mapping of Pi v1.5.0 manager.ts. The pipe framing is explicitly not Node IPC framing.
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Tools.Processes;

namespace PiSharp.Chord.Legacy;

public sealed record LegacyProcessDriverOptions
{
    /// <summary>Absolute executable path; the driver does not use a shell or ambient executable lookup.</summary>
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public string? WorkingDirectory { get; init; }
    public JsonNode? Init { get; init; }
    public string? Lock { get; init; }
    public Action<string, string>? OnOutput { get; init; }
    public Action<JsonNode?>? OnMessage { get; init; }
    public TimeSpan PipeConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Starts a native child with a private, length-prefixed JSON control/Chord pipe.</summary>
public sealed class LegacyProcessDriver(LegacyProcessDriverOptions options) : ILegacyHostDriver
{
    public const string PipeEnvironmentVariable = "PISHARP_CHORD_CONTROL_PIPE";
    public const string LockEnvironmentVariable = "CHORD2_LOCK";
    public ILegacyHostInstance Spawn()
    {
        var addresses = PrivatePipeDirectory.Create();
        NamedPipeServerStream? pipe = null;
        OwnedChildProcess? process = null;
        try
        {
            var name = addresses.Name("control");
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(options.FileName)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.WorkingDirectory = Path.GetFullPath(options.WorkingDirectory ?? System.Environment.CurrentDirectory);
            foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
            // A child never inherits the parent's storage lock name.
            start.Environment.Remove(LockEnvironmentVariable);
            start.Environment.Remove(PipeEnvironmentVariable);
            foreach (var pair in options.Environment) start.Environment[pair.Key] = pair.Value;
            if (options.Lock is not null) start.Environment[LockEnvironmentVariable] = options.Lock;
            start.Environment[PipeEnvironmentVariable] = name;
            // Spawn is a synchronous legacy Driver seam. Native admission completes before
            // returning, outside the Chord host dispatcher, and does not run a shell.
            process = OwnedChildProcess.StartAsync(start).GetAwaiter().GetResult();
            var channel = new LegacyFramedPipeChannel(pipe, async cancellationToken =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(options.PipeConnectTimeout);
                await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            });
            process.OwnReader(Mirror(process.StandardOutput, "stdout", options.OnOutput));
            process.OwnReader(Mirror(process.StandardError, "stderr", options.OnOutput));
            var instance = new Instance(process, channel, options.OnMessage, addresses);
            if (options.Init is not null) instance.Send(new("init") { Value = options.Init.DeepClone() });
            channel.Start();
            return instance;
        }
        catch
        {
            try
            {
                pipe?.Dispose();
                if (process is not null) process.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally { addresses.Dispose(); }
            throw;
        }
    }
    private static async Task Mirror(StreamReader reader, string stream, Action<string, string>? output)
    {
        try
        {
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var text = new string(buffer, 0, count);
                try { output?.Invoke(stream, text); } catch (Exception) { /* Output taps cannot kill the reader or child. */ }
                if (stream == "stderr") Console.Error.Write(text); else Console.Out.Write(text);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private sealed class Instance : ILegacyHostInstance
    {
        private readonly object gate = new();
        private readonly OwnedChildProcess process;
        private readonly LegacyFramedPipeChannel channel;
        private readonly PrivatePipeDirectory addresses;
        private readonly List<Action<LegacyControlMessage>> listeners = [];
        private readonly Queue<LegacyControlMessage> backlog = new();
        private bool killed;
        private Task? stopping;
        internal Instance(OwnedChildProcess process, LegacyFramedPipeChannel channel, Action<JsonNode?>? onMessage, PrivatePipeDirectory addresses)
        {
            this.process = process; this.channel = channel; this.addresses = addresses; Pid = process.Id;
            channel.Message += message =>
            {
                if (message is JsonObject wrapper && LegacyControlMessage.FromJson(wrapper["chord2control"]) is { } control)
                {
                    Action<LegacyControlMessage>[] handlers;
                    lock (gate) { handlers = listeners.ToArray(); if (handlers.Length == 0) backlog.Enqueue(control); }
                    foreach (var handler in handlers) try { handler(control); } catch (Exception) { /* Isolate control listeners. */ }
                }
                else if (message is not JsonObject objectMessage || !objectMessage.ContainsKey("chord2"))
                    try { onMessage?.Invoke(message); } catch (Exception) { }
            };
            Link = new LegacyIpcLink(channel); Exited = ObserveExit();
        }
        public int Pid { get; }
        public ILegacyLink Link { get; }
        public Task<LegacyProcessExit> Exited { get; }
        public void Send(LegacyControlMessage message)
        {
            if (Exited.IsCompleted) return;
            channel.Send(new JsonObject { ["chord2control"] = message.ToJson() }, _ => { });
        }
        public void OnControl(Action<LegacyControlMessage> listener)
        {
            LegacyControlMessage[] queued;
            lock (gate) { listeners.Add(listener); queued = backlog.ToArray(); backlog.Clear(); }
            foreach (var message in queued) listener(message);
        }
        public void KillTree()
        {
            lock (gate) { killed = true; stopping ??= process.StopAsync(); }
        }
        private async Task<LegacyProcessExit> ObserveExit()
        {
            var code = await process.Exit.ConfigureAwait(false);
            Task? stop; bool wasKilled; lock (gate) { stop = stopping; wasKilled = killed; }
            if (stop is not null) await stop.ConfigureAwait(false);
            channel.Dispose();
            var exit = new LegacyProcessExit(code, wasKilled ? "SIGKILL" : null);
            try { await process.DisposeAsync().ConfigureAwait(false); }
            finally { addresses.Dispose(); }
            lock (gate) { listeners.Clear(); backlog.Clear(); }
            return exit;
        }
    }
}

/// <summary>Private native pipe addresses whose Unix paths do not depend on TMPDIR length.</summary>
internal sealed class PrivatePipeDirectory : IDisposable
{
    private readonly string stem;
    private readonly string? directory;
    private int disposed;
    private PrivatePipeDirectory(string stem, string? directory) { this.stem = stem; this.directory = directory; }
    internal static PrivatePipeDirectory Create()
    {
        var stem = "psc-" + Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows()) return new(stem, null);
        // .NET PipeStream.GetPipePath explicitly accepts absolute Unix socket paths to
        // bypass the TMPDIR/CoreFxPipe_ prefix and its macOS sockaddr_un length limit.
        var directory = Path.Combine("/tmp", stem);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new(stem, directory);
    }
    internal string Name(string endpoint) => directory is null ? stem + "-" + endpoint : Path.Combine(directory, endpoint);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || directory is null) return;
        // Streams unlink their own sockets first. Never recursively remove unowned files.
        try { Directory.Delete(directory); } catch (DirectoryNotFoundException) { }
    }
}

/// <summary>4-byte little-endian byte count + UTF-8 JSON. This is the native adapter, not node:child_process IPC.</summary>
internal sealed class LegacyFramedPipeChannel(Stream stream, Func<CancellationToken, Task> connect) : ILegacyIpcChannel, IDisposable
{
    private const int MaximumMessage = 64 * 1024 * 1024;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task writes = Task.CompletedTask;
    private bool disposed;
    public bool Connected { get { lock (gate) return !disposed; } }
    public event Action<JsonNode?>? Message;
    public event Action? Disconnected;
    public void Start() => _ = Read();
    public void Send(JsonObject message, Action<Exception?> completion)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        lock (gate)
        {
            if (disposed) { completion(new IOException("pipe disconnected")); return; }
            writes = writes.ContinueWith(async _ =>
            {
                try
                {
                    if (bytes.Length > MaximumMessage) throw new IOException("legacy pipe message is too large");
                    await connected.Task.ConfigureAwait(false);
                    var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                    await stream.WriteAsync(header, lifetime.Token).ConfigureAwait(false);
                    await stream.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
                    await stream.FlushAsync(lifetime.Token).ConfigureAwait(false); completion(null);
                }
                catch (Exception error) { completion(error); Dispose(); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }
    private async Task Read()
    {
        try
        {
            await connect(lifetime.Token).ConfigureAwait(false); connected.TrySetResult();
            var header = new byte[4];
            while (!lifetime.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, lifetime.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaximumMessage) throw new IOException("invalid legacy pipe frame length");
                var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, lifetime.Token).ConfigureAwait(false);
                Message?.Invoke(JsonNode.Parse(bytes));
            }
        }
        catch (Exception error) { connected.TrySetException(error); }
        finally { Dispose(); }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        lifetime.Cancel(); lifetime.Dispose(); connected.TrySetException(new IOException("pipe disconnected")); stream.Dispose();
        var callbacks = Disconnected?.GetInvocationList() ?? [];
        Message = null; Disconnected = null;
        foreach (var callback in callbacks) try { ((Action)callback)(); } catch (Exception) { /* One consumer cannot prevent the other close notifications. */ }
    }
}
