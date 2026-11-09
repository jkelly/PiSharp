// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/daemon/src/watch.rs.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using JsonValue = System.Text.Json.Nodes.JsonValue;
using System.Threading.Channels;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Env.Protocol;

namespace PiSharp.Env.Daemon;

/// <summary>Session-owned watch coverage with bounded pending notifications.</summary>
public sealed class WatchOperation(string? workingDirectory = null) : IEnvDaemonOperation
{
    public string Name => "watch";

    public async ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request)
    {
        var targets = ParseTargets(request.Json);
        var options = new LocalWatchOptions
        {
            Mode = OptionalText(request.Json["mode"], "mode") switch
            { "native" => WatchMode.Native, "polling" => WatchMode.Polling, null => null, _ => throw Invalid("Invalid watch mode") },
            PollIntervalMs = Number(request.Json, "pollIntervalMs", false),
            MaxDirectories = Number(request.Json, "maxDirectories", true),
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, request.SessionCancellationToken);
        var token = cancellation.Token;
        var owner = new WatchOwner();
        request.Own(owner);
        try
        {
            var fs = new LocalFileSystem(workingDirectory ?? Environment.CurrentDirectory, options);
            var result = await fs.WatchAsync(targets, owner.Changed, DurableContext.Background.WithCancellation(token)).ConfigureAwait(false);
            if (!result.IsOk) throw new EnvDaemonException(result.Error.Code == FileErrorCode.PermissionDenied ? "EACCES" : "EINVAL", result.Error.Message, result.Error.Path);
            await owner.AttachAsync(result.Value).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Changes observed while initial native coverage settles remain queued behind ready.
            await request.EmitEventAsync(new() { ["kind"] = "ready", ["mode"] = Mode(result.Value.Mode) }, cancellationToken: token).ConfigureAwait(false);
            try
            {
                await foreach (var change in owner.Changes.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (owner.TakeOverflow()) await EmitAsync(new FileOverflowChange()).ConfigureAwait(false);
                    await EmitAsync(change).ConfigureAwait(false);
                }
                if (owner.TakeOverflow()) await EmitAsync(new FileOverflowChange()).ConfigureAwait(false);
                if (owner.Failure is { } failure) await EmitAsync(new FileFailedChange(failure)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return EnvDaemonReply.Empty;

            async ValueTask EmitAsync(FileChange change) => await request.EmitEventAsync(ChangeEvent(change, result.Value.Mode), cancellationToken: token).ConfigureAwait(false);
        }
        finally
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            finally { request.Release(owner); }
        }
    }

    internal static JsonObject ChangeEvent(FileChange change, WatchMode mode)
    {
        if (change is FileFailedChange failure) return new() { ["kind"] = "error", ["code"] = failure.Error.Code == FileErrorCode.PermissionDenied ? "permission_denied" : "invalid", ["message"] = failure.Error.Message };
        if (change is FileOverflowChange) return new() { ["kind"] = "change", ["overflow"] = true, ["mode"] = Mode(mode) };
        var paths = ((FilePathsChange)change).Paths;
        // Reject an oversized candidate before allocating JSON nodes/UTF8 for arbitrarily large input.
        long upperBound = 64;
        foreach (var path in paths) { upperBound += 8L + path.Length * 6L; if (upperBound > FrameCodec.MaxPayloadBytes) return new() { ["kind"] = "change", ["overflow"] = true }; }
        // Each UTF-16 code unit needs at most six JSON bytes (\uXXXX); the bound above
        // includes quotes, separators and object keys without reflection-based serialization.
        return new JsonObject { ["kind"] = "change", ["paths"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()) };
    }

    private static string Mode(WatchMode mode) => mode == WatchMode.Polling ? "polling" : "native";
    private static EnvDaemonException Invalid(string message) => new("EINVAL", message);
    private static string? OptionalText(JsonNode? node, string name) => node is null ? null :
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw Invalid("Invalid " + name);
    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static int? Number(JsonObject json, string name, bool zero)
    {
        if (json[name] is null) return null;
        if (json[name] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < (zero ? 0 : 1)) throw Invalid("Invalid " + name);
        return number;
    }
    private static WatchTarget[] ParseTargets(JsonObject json)
    {
        if (json["targets"] is not JsonArray targets) throw Invalid("watch needs targets");
        return targets.Select(target =>
        {
            if (target is not JsonObject obj || obj["path"] is not JsonValue path || !path.TryGetValue<string>(out var text) || text.Contains('\0')) throw Invalid("watch needs targets");
            var exclude = obj["exclude"] as JsonObject;
            return new WatchTarget(text) { Recursive = IsTrue(obj["recursive"]),
                Exclude = new() { Hidden = IsTrue(exclude?["hidden"]),
                    Names = exclude?["names"] is JsonArray names
                        ? names.OfType<JsonValue>().Where(name => name.TryGetValue<string>(out _)).Select(name => name.GetValue<string>()).ToImmutableArray() : [] } };
        }).ToArray();
    }

    private sealed class WatchOwner : IAsyncDisposable
    {
        // At most eight retained notifications, each at most 256 KiB of encoded path content.
        // Congestion is reported as uncertain coverage instead of blocking the local watcher thread.
        private const int PendingEventBytes = 256 * 1024;
        private readonly object gate = new();
        private IFileWatcher? watcher;
        private Task? disposal;
        private bool disposed;
        private int overflow;
        public FileError? Failure { get; private set; }
        public Channel<FileChange> Changes { get; } = Channel.CreateBounded<FileChange>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public bool TakeOverflow() => Interlocked.Exchange(ref overflow, 0) != 0;
        public void Changed(FileChange change)
        {
            lock (gate)
            {
                if (disposed || Failure is not null) return;
                if (change is FileFailedChange failed) { Failure = failed.Error; Changes.Writer.TryComplete(); }
                else
                {
                    if (change is FilePathsChange paths && paths.Paths.Sum(path => (long)path.Length * 6 + 8) > PendingEventBytes)
                        change = new FileOverflowChange();
                    if (!Changes.Writer.TryWrite(change)) Interlocked.Exchange(ref overflow, 1);
                }
            }
        }
        public async Task AttachAsync(IFileWatcher value)
        {
            lock (gate) { if (!disposed) { watcher = value; return; } }
            await value.StopAsync(DurableContext.Background).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposal is not null) return new(disposal);
                disposed = true;
                Changes.Writer.TryComplete();
                disposal = watcher?.StopAsync(DurableContext.Background) ?? Task.CompletedTask;
                watcher = null;
                return new(disposal);
            }
        }
    }
}
