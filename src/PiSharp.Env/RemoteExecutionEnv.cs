// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/src/remote-env.ts.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Durable.Env;

namespace PiSharp.Env;

public sealed record RemoteExecutionEnvOptions
{
    public required Connection Connection { get; init; }
    public required string Id { get; init; }
    public required string Cwd { get; init; }
    public string? ShellPath { get; init; }
    public IReadOnlyDictionary<string, string>? ShellEnv { get; init; }
    public RemoteWatchOptions? Watch { get; init; }
}

/// <summary>The Durable filesystem and shell capabilities of a native pi-env daemon.</summary>
public sealed partial class RemoteExecutionEnv : IExecutionEnv
{
    private readonly RemoteExecutionEnvOptions options;
    private readonly ConcurrentDictionary<RemoteHandle, byte> handles = new();
    private readonly ConcurrentDictionary<RemoteWatcher, byte> watchers = new();
    private readonly ConcurrentDictionary<uint, long> running = new();
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    public RemoteExecutionEnv(RemoteExecutionEnvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Connection);
        this.options = options;
        Id = options.Id;
        Cwd = options.Cwd;
    }
    public Connection Connection => options.Connection;
    public string Id { get; }
    public string Cwd { get; set; }
    internal static JsonNode? Json<T>(T value) => JsonSerializer.SerializeToNode(value, JsonOptions);
    internal async Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) =>
        RemotePaths.Resolve(await Connection.InfoAsync(cancellationToken).ConfigureAwait(false), Cwd, path);
    internal RemoteHandle Handle(RemoteReply reply, string path)
    {
        var handle = new RemoteHandle(this, reply.Json["handle"]!.GetValue<long>(), reply.Session, path)
        { Stat = reply.Json["stat"] as JsonObject };
        handles.TryAdd(handle, 0);
        return handle;
    }
    internal void Released(RemoteHandle handle) => handles.TryRemove(handle, out _);
    internal static FileError FileFailure(Exception error, string? path = null)
    {
        if (error is FileError file) return file;
        var remote = error as RemoteError;
        var code = remote?.Code switch
        {
            "aborted" => FileErrorCode.Aborted,
            "ENOENT" => FileErrorCode.NotFound,
            "EACCES" or "EPERM" or "permission_denied" => FileErrorCode.PermissionDenied,
            "ENOTDIR" => FileErrorCode.NotDirectory,
            "EISDIR" or "ERR_FS_EISDIR" => FileErrorCode.IsDirectory,
            "EINVAL" or "SYMLINK" or "NOT_REGULAR" or "EBADF" or "invalid" => FileErrorCode.Invalid,
            "ENOTSUP" or "not_supported" => FileErrorCode.NotSupported,
            _ => error is OperationCanceledException ? FileErrorCode.Aborted : FileErrorCode.Unknown,
        };
        return new(code, error.Message, remote?.Path ?? path, error);
    }
    internal static void CheckCancellation(PiSharp.Durable.Chord.DurableContext context, string? path = null)
    {
        if (context.IsCancellationRequested) throw new FileError(FileErrorCode.Aborted, "aborted", path);
    }
    public async Task CleanupAsync(PiSharp.Durable.Chord.DurableContext context)
    {
        foreach (var pair in running) Connection.Kill(pair.Key, pair.Value);
        running.Clear();
        try { await Task.WhenAll(watchers.Keys.Select(watcher => watcher.StopAsync(context))).ConfigureAwait(false); }
        catch (Exception) { } // Shutdown is best effort, including a failed or disconnected watch.
        watchers.Clear();
        await Task.WhenAll(handles.Keys.Select(handle => handle.CloseAsync())).ConfigureAwait(false);
    }
}
