using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>A process-lifetime kernel lock. Crash recovery does not depend on deleting a marker file.</summary>
public static class LegacyProcessLock
{
    public static Task<IDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name); cancellationToken.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..32].ToLowerInvariant();
        try
        {
            if (OperatingSystem.IsWindows())
                return Task.FromResult<IDisposable>(new NamedPipeServerStream("chord2-" + key, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance));
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { socket.Bind(new UnixDomainSocketEndPoint("\0chord2-" + key)); socket.Listen(1); return Task.FromResult<IDisposable>(socket); }
                catch { socket.Dispose(); throw; }
            }
            // An exclusive open is kernel-owned. Keep the inode: unlinking it would let two processes lock different files.
            var directory = Path.Combine(Path.GetTempPath(), "pisharp-chord-locks");
            Directory.CreateDirectory(directory);
            var file = new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult<IDisposable>(file);
        }
        catch (Exception error) when (error is IOException || OperatingSystem.IsWindows() && error is UnauthorizedAccessException || error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
        { throw new ChordException("unavailable", $"lock {name} is held by another process", "locked", cause: error); }
    }
}

/// <summary>Native child control messages share the same envelope and link as protocol 4 frames.</summary>
public sealed class LegacyChildControl : IAsyncDisposable
{
    private readonly ILegacyIpcChannel channel;
    private readonly IDisposable? ownedChannel;
    private readonly TaskCompletionSource<JsonNode?> init = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Func<Task>> shutdown = [];
    private readonly object gate = new();
    private IDisposable? processLock;
    private bool shuttingDown, disposed;
    public LegacyChildControl(ILegacyIpcChannel channel)
    {
        this.channel = channel;
        channel.Message += Receive; channel.Disconnected += Disconnected;
        Link = new LegacyIpcLink(channel);
    }
    private LegacyChildControl(LegacyFramedPipeChannel channel) : this((ILegacyIpcChannel)channel) { ownedChannel = channel; }
    public ILegacyLink Link { get; }
    public Task<JsonNode?> Init => init.Task;
    /// <summary>Connects the native worker to the private pipe passed by LegacyProcessDriver.</summary>
    public static async Task<LegacyChildControl> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var name = Environment.GetEnvironmentVariable(LegacyProcessDriver.PipeEnvironmentVariable)
            ?? throw new InvalidOperationException("No legacy manager control pipe was supplied.");
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false); }
        catch { pipe.Dispose(); throw; }
        var channel = new LegacyFramedPipeChannel(pipe, _ => Task.CompletedTask);
        var result = new LegacyChildControl(channel); channel.Start(); return result;
    }
    public async Task LockAsync(CancellationToken cancellationToken = default)
    {
        var name = Environment.GetEnvironmentVariable(LegacyProcessDriver.LockEnvironmentVariable);
        if (name is null) return;
        try
        {
            var acquired = await LegacyProcessLock.AcquireAsync(name, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed || processLock is not null) { acquired.Dispose(); if (disposed) throw new ObjectDisposedException(nameof(LegacyChildControl)); }
                else processLock = acquired;
            }
        }
        catch (Exception error) { Fatal(error); throw; }
    }
    public void Ready() => Send(new("ready"));
    public void Fatal(Exception error) => Send(new("fatal") { Message = error.Message, Reason = (error as ChordException)?.Reason });
    public void Report(int? activeCalls = null, int? viewers = null, int? durableTasks = null) =>
        Send(new("status") { ActiveCalls = activeCalls, Viewers = viewers, DurableTasks = durableTasks });
    public void OnShutdown(Func<Task> handler) { lock (gate) shutdown.Add(handler); }
    private void Send(LegacyControlMessage message)
    {
        if (channel.Connected) channel.Send(new JsonObject { ["chord2control"] = message.ToJson() }, _ => { });
    }
    private void Receive(JsonNode? message)
    {
        if (message is not JsonObject wrapper || LegacyControlMessage.FromJson(wrapper["chord2control"]) is not { } control) return;
        if (control.Type == "init") init.TrySetResult(control.Value?.DeepClone());
        else if (control.Type == "shutdown") _ = Shutdown();
    }
    private void Disconnected() { init.TrySetException(new IOException("manager disconnected")); _ = Shutdown(); }
    private async Task Shutdown()
    {
        Func<Task>[] handlers;
        lock (gate) { if (shuttingDown) return; shuttingDown = true; handlers = shutdown.ToArray(); }
        foreach (var handler in handlers)
        {
            try { await handler().ConfigureAwait(false); }
            catch (Exception error) { Fatal(error); }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true; shutdown.Clear(); processLock?.Dispose(); processLock = null;
        }
        channel.Message -= Receive; channel.Disconnected -= Disconnected;
        Link.Close("child closed"); ownedChannel?.Dispose(); return ValueTask.CompletedTask;
    }
}
