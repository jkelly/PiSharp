using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Env;

public sealed partial class RemoteExecutionEnv
{
    public Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, DurableContext context) => FileOp<IDirReader>(path, context, async resolved =>
    {
        var reply = await Connection.RequestAsync("opendir", new() { ["path"] = resolved }).ConfigureAwait(false);
        var handle = Handle(reply, resolved);
        try { CheckCancellation(context, resolved); return new RemoteDirReader(handle, await Connection.InfoAsync().ConfigureAwait(false)); }
        catch { await handle.CloseAsync().ConfigureAwait(false); throw; }
    });
    public Task<Result<ImmutableArray<FileMetadata>, FileError>> ListDirAsync(string path, DurableContext context) => FileOp(path, context, async resolved =>
    {
        var info = await Connection.InfoAsync().ConfigureAwait(false);
        var handle = Handle(await Connection.RequestAsync("opendir", new() { ["path"] = resolved }).ConfigureAwait(false), resolved);
        var entries = new List<(byte[] Sort, JsonObject Entry)>();
        try
        {
            bool done;
            do
            {
                CheckCancellation(context, resolved);
                var reply = await handle.RequestAsync("readdir", new() { ["max"] = 1000 }).ConfigureAwait(false);
                foreach (var node in reply.Json["entries"]!.AsArray())
                {
                    var entry = node!.AsObject();
                    var key = entry["raw"] is JsonArray raw ? raw.Select(n => n!.GetValue<byte>()).ToArray() : Encoding.UTF8.GetBytes(entry["name"]!.GetValue<string>());
                    entries.Add((key, entry));
                }
                done = reply.Json["done"]!.GetValue<bool>();
            } while (!done);
        }
        finally { await handle.CloseAsync().ConfigureAwait(false); }
        if (!RemotePaths.IsWindows(info)) entries.Sort((a, b) => a.Sort.AsSpan().SequenceCompareTo(b.Sort));
        var result = ImmutableArray.CreateBuilder<FileMetadata>();
        foreach (var (_, entry) in entries)
        {
            CheckCancellation(context, resolved);
            var entryPath = RemotePaths.Resolve(info, resolved, entry["name"]!.GetValue<string>());
            if (entry["error"] is JsonObject error) throw FileFailure(new RemoteError(error), entryPath);
            var metadata = entry["info"]!.AsObject();
            if (metadata["kind"]?.GetValue<string>() != "other") result.Add(Metadata(entryPath, metadata));
        }
        return result.ToImmutable();
    });
    public Task<Result<FileListing, FileError>> ListFilesAsync(string path, ListFilesOptions? options, DurableContext context) => FileOp(path, context, async resolved =>
    {
        var info = await Connection.InfoAsync(context.CancellationToken).ConfigureAwait(false);
        if (!info.Features.Contains("listFiles")) throw new FileError(FileErrorCode.NotSupported, "The remote environment does not list files", resolved);
        foreach (var bound in new[] { options?.MaxBytes, options?.MaxEntries, options?.TimeoutMs })
            if (bound is { } value && (!double.IsFinite(value) || value < 0 || value > 9007199254740991d || value != Math.Truncate(value)))
                throw new FileError(FileErrorCode.Invalid, "Listing bounds must be non-negative safe integers", resolved);
        if (options?.MaxBytes > 16 * 1024 * 1024 - 64 * 1024) throw new FileError(FileErrorCode.Invalid, "Listing byte limit exceeds frame limit", resolved);
        var json = options is null ? new JsonObject() : Json(options)!.AsObject(); json["path"] = resolved;
        var reply = await Connection.RequestAsync("listFiles", json, cancellationToken: context.CancellationToken).ConfigureAwait(false);
        CheckCancellation(context, resolved);
        var entries = ImmutableArray.CreateBuilder<FileListEntry>();
        for (var offset = 0; offset < reply.Payload.Length;)
        {
            var kind = reply.Payload[offset++] switch { (byte)'f' => FileKind.File, (byte)'d' => FileKind.Directory, (byte)'l' => FileKind.Symlink, _ => throw new InvalidDataException("Invalid listing kind") };
            var end = Array.IndexOf(reply.Payload, (byte)0, offset);
            if (end < 0) throw new InvalidDataException("Unterminated listing record");
            entries.Add(new(Encoding.UTF8.GetString(reply.Payload, offset, end - offset), kind)); offset = end + 1;
        }
        if (reply.Json["count"]!.GetValue<int>() != entries.Count) throw new InvalidDataException("Listing count mismatch");
        return new FileListing(entries.ToImmutable(), reply.Json["truncated"]!.GetValue<bool>());
    });
    public async Task<Result<IFileWatcher, FileError>> WatchAsync(IReadOnlyList<WatchTarget> targets, Action<FileChange> onChange, DurableContext context)
    {
        try
        {
            CheckCancellation(context);
            var resolved = new List<WatchTarget>();
            foreach (var target in targets) resolved.Add(target with { Path = await ResolveAsync(target.Path, context.CancellationToken).ConfigureAwait(false) });
            var watcher = await RemoteWatcher.OpenAsync(Connection, resolved, onChange, options.Watch, context.CancellationToken).ConfigureAwait(false);
            if (context.IsCancellationRequested)
            {
                await watcher.StopAsync(context).ConfigureAwait(false);
                CheckCancellation(context);
            }
            watchers.TryAdd(watcher, 0);
            return new OwnedWatcher(watcher, () => watchers.TryRemove(watcher, out _));
        }
        catch (Exception error) { return FileFailure(error); }
    }
    private sealed class OwnedWatcher(RemoteWatcher watcher, Action released) : IFileWatcher
    {
        public WatchMode Mode => watcher.Mode;
        public async Task StopAsync(DurableContext context)
        {
            try { await watcher.StopAsync(context).ConfigureAwait(false); }
            finally { released(); }
        }
    }
}
