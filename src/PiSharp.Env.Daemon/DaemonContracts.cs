using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace PiSharp.Env.Daemon;

/// <summary>A session operation. Implementations must stop and release resources when the session is cancelled.</summary>
public interface IEnvDaemonOperation
{
    string Name { get; }
    ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request);
}

public sealed record EnvDaemonReply(JsonObject Json, ReadOnlyMemory<byte> Payload = default)
{
    public static EnvDaemonReply Empty => new(new JsonObject());
}

public sealed class EnvDaemonException(string code, string message, string? path = null, string? syscall = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Path { get; } = path;
    public string? Syscall { get; } = syscall;
    public JsonObject Details { get; } = new();
}

public sealed class EnvDaemonOptions
{
    public string? Token { get; init; }
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;
    public string Version { get; init; } = "1.5.0";
    public int MaxHandles { get; init; } = 4096;
    public int FileWorkers { get; init; } = 16;
    public int MaxPendingRequests { get; init; } = 1024;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum disconnect drain; blocking native FIFO opens cannot be joined indefinitely.</summary>
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public IReadOnlyList<IEnvDaemonOperation> Operations { get; init; } = [];
    public IReadOnlyList<string> Features { get; init; } = [];
}

public sealed class EnvDaemonRequest
{
    private readonly EnvDaemonSession session;
    private int killRequested, abortRequested;
    internal EnvDaemonRequest(EnvDaemonSession session, uint id, JsonObject json, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    { this.session = session; Id = id; Json = json; Payload = payload; CancellationToken = cancellationToken; }
    public uint Id { get; }
    public JsonObject Json { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public CancellationToken CancellationToken { get; }
    public CancellationToken SessionCancellationToken => session.CancellationToken;
    public bool IsKillRequested => Volatile.Read(ref killRequested) != 0;
    public bool IsAbortRequested => Volatile.Read(ref abortRequested) != 0;
    /// <summary>Signals both abort and kill; inspect IsKillRequested to distinguish their outcome.</summary>
    internal void MarkKillRequested() => Volatile.Write(ref killRequested, 1);
    internal void MarkAbortRequested() => Volatile.Write(ref abortRequested, 1);
    public ConcurrentDictionary<string, object> State => session.State;
    public ValueTask EmitEventAsync(JsonObject json, ReadOnlyMemory<byte> payload = default, CancellationToken cancellationToken = default) =>
        session.SendEventAsync(Id, json, payload, cancellationToken);
    /// <summary>Registers cleanup for disconnect, including during startup races. Ownership lasts for this session.</summary>
    public void Own(IAsyncDisposable resource) => session.Own(resource);
    /// <summary>Removes a resource after its owner has disposed it; safe if disconnect already claimed it.</summary>
    public void Release(IAsyncDisposable resource) => session.Release(resource);
}
