using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Env.Protocol;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace PiSharp.Env.Daemon;

internal sealed class DaemonFileSystem(string cwd, int maxHandles) : IAsyncDisposable
{
    private readonly LocalFileSystem local = new(cwd);
    private readonly object gate = new();
    private readonly Dictionary<long, OpenHandle> handles = [];
    private readonly ConcurrentDictionary<string, Task> ordering = new();
    private long nextHandle;
    private bool closed;

    // Reserve FIFO positions on the reader thread before workers can be scheduled out of order.
    public Task Predecessor(JsonObject json, Task completion)
    {
        var isProcessControl = json["op"]?.ToString() is "processWrite" or "processCloseStdin";
        var field = isProcessControl ? "target" : "handle";
        if (json[field] is not JsonValue value || !value.TryGetValue<long>(out var handle)) return Task.CompletedTask;
        var key = (isProcessControl ? "process:" : "file:") + handle;
        var previous = ordering.GetValueOrDefault(key, Task.CompletedTask);
        ordering[key] = completion;
        _ = completion.ContinueWith(_ => ((ICollection<KeyValuePair<string, Task>>)ordering).Remove(new(key, completion)), TaskScheduler.Default);
        return previous;
    }

    public async ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request, string op)
    {
        var json = request.Json;
        var context = DurableContext.Background.WithCancellation(request.SessionCancellationToken);
        string PathArg(string key = "path")
        {
            var path = Text(json, key);
            if (!Path.IsPathFullyQualified(path)) throw new EnvDaemonException("EINVAL", "Paths must be absolute", path);
            return path;
        }
        switch (op)
        {
            case "lstat": return new(DaemonMetadata.PathInfo(PathArg()));
            case "realpath": return new(new() { ["path"] = (await local.CanonicalPathAsync(PathArg(), context).ConfigureAwait(false)).GetOrThrow() });
            case "write":
            {
                var path = PathArg();
                if (Flag(json, "parents", true) && Path.GetDirectoryName(path) is { } parent)
                    (await local.CreateDirAsync(parent, new() { Recursive = true }, context).ConfigureAwait(false)).GetOrThrow();
                if (Directory.Exists(path)) throw new EnvDaemonException("EISDIR", "Is a directory", path, "open");
                var stream = new FileStream(path, Flag(json, "append") ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                try
                {
                    await stream.WriteAsync(request.Payload, request.SessionCancellationToken).ConfigureAwait(false);
                    if (Flag(json, "keep")) return new(new() { ["handle"] = Insert(new(path, stream)) });
                }
                catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
                await stream.DisposeAsync().ConfigureAwait(false);
                return EnvDaemonReply.Empty;
            }
            case "writeChunk":
            {
                var id = Number(json, "handle"); var file = Handle(id);
                if (file.Stream is null || !file.Stream.CanWrite) throw BadHandle();
                try { await file.Stream.WriteAsync(request.Payload, request.SessionCancellationToken).ConfigureAwait(false); }
                catch { await RemoveAsync(id).ConfigureAwait(false); throw; }
                return EnvDaemonReply.Empty;
            }
            case "truncate": (await local.TruncateFileAsync(PathArg(), Number(json, "size"), context).ConfigureAwait(false)).GetOrThrow(); return EnvDaemonReply.Empty;
            case "fsync": (await local.FlushFileAsync(PathArg(), context).ConfigureAwait(false)).GetOrThrow(); return EnvDaemonReply.Empty;
            case "rename": (await local.RenameFileAsync(PathArg(), PathArg("to"), context).ConfigureAwait(false)).GetOrThrow(); return EnvDaemonReply.Empty;
            case "mkdir": (await local.CreateDirAsync(PathArg(), new() { Recursive = Flag(json, "recursive") }, context).ConfigureAwait(false)).GetOrThrow(); return EnvDaemonReply.Empty;
            case "rm":
            {
                var path = PathArg();
                if (!Flag(json, "recursive") && Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                    throw new EnvDaemonException("ERR_FS_EISDIR", "Path is a directory", path);
                (await local.RemoveAsync(path, new() { Recursive = Flag(json, "recursive"), Force = Flag(json, "force") }, context).ConfigureAwait(false)).GetOrThrow();
                return EnvDaemonReply.Empty;
            }
            case "mkdtemp":
            {
                var prefix = PathArg();
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    var path = prefix + System.Security.Cryptography.RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 6);
                    if (DaemonMetadata.CreateTempDirectory(path)) return new(new() { ["path"] = path });
                }
                throw new EnvDaemonException("EEXIST", "Could not allocate temporary directory", prefix, "mkdtemp");
            }
            case "open":
            {
                var path = PathArg(); var readMode = json["mode"]?.GetValue<string>() == "read";
                // FileShare.Delete preserves the opened object across rename/replacement, as POSIX descriptors do.
                var stream = DaemonMetadata.OpenRead(path, readMode, Flag(json, "noFollow"));
                try
                {
                    JsonObject metadata;
                    try { metadata = new() { [readMode ? "stat" : "info"] = DaemonMetadata.HandleInfo(stream.SafeFileHandle, path) }; }
                    catch (Exception error) when (readMode) { metadata = new() { ["statError"] = DaemonErrors.Json(error) }; }
                    metadata["handle"] = Insert(new(path, stream));
                    return new(metadata);
                }
                catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
            }
            case "pread":
            {
                var file = Handle(Number(json, "handle")); var stream = file.Stream;
                if (stream is null || !stream.CanRead) throw BadHandle();
                var length = (int)Math.Min(Number(json, "length"), FrameCodec.MaxPayloadBytes);
                var buffer = new byte[length]; int read;
                if (json["offset"] is not null)
                {
                    var offset = Number(json, "offset"); read = 0;
                    while (read < buffer.Length)
                    {
                        var count = await RandomAccess.ReadAsync(stream.SafeFileHandle, buffer.AsMemory(read), checked(offset + read), request.SessionCancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        read += count;
                    }
                }
                else read = await stream.ReadAsync(buffer, request.SessionCancellationToken).ConfigureAwait(false);
                return new(new(), buffer.AsMemory(0, read));
            }
            case "fstat":
            {
                var file = Handle(Number(json, "handle"));
                if (file.Stream is null) throw BadHandle();
                return new(DaemonMetadata.HandleInfo(file.Stream.SafeFileHandle, file.Path));
            }
            case "scanLines":
            {
                var file = Handle(Number(json, "handle"));
                if (file.Stream is null || !file.Stream.CanRead) throw BadHandle();
                var scanner = new LineScanner(Number(json, "startLine"), json["endLine"] is null ? null : Number(json, "endLine"));
                var buffer = new byte[64 * 1024]; long offset = 0;
                while (true)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    var count = await RandomAccess.ReadAsync(file.Stream.SafeFileHandle, buffer, offset, request.CancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    scanner.Push(buffer.AsSpan(0, count)); offset += count;
                }
                var scan = scanner.Finish();
                return new(new()
                {
                    ["newlines"] = scan.Newlines, ["start"] = scan.Start, ["end"] = scan.End,
                    ["firstLineEnd"] = scan.FirstLineEnd, ["lastLineStart"] = scan.LastLineStart,
                    ["selectedBytes"] = scan.SelectedBytes, ["firstLineBytes"] = scan.FirstLineBytes
                });
            }
            case "opendir":
            {
                var path = PathArg();
                if (!Directory.Exists(path))
                { if (File.Exists(path)) throw new EnvDaemonException("ENOTDIR", "Not a directory", path); throw new EnvDaemonException("ENOENT", "No such directory", path); }
                return new(new() { ["handle"] = Insert(new(path, new DaemonDirectory(path))) });
            }
            case "readdir":
            {
                var file = Handle(Number(json, "handle")); if (file.Directory is null) throw BadHandle();
                var max = Math.Min(Number(json, "max"), 4096);
                if (max < 1) throw new EnvDaemonException("EINVAL", "max must be positive");
                var entries = new JsonArray();
                while (!file.Done && entries.Count < max)
                {
                    if (!file.Directory.MoveNext()) { file.Done = true; break; }
                    var name = file.Directory.Current; var path = Path.Combine(file.Path, name.Name);
                    var entry = new JsonObject { ["name"] = name.Name };
                    if (name.Raw is { } raw) entry["raw"] = new JsonArray(raw.Select(x => (JsonNode?)JsonValue.Create((int)x)).ToArray());
                    try { entry["info"] = DaemonMetadata.PathInfo(path); }
                    catch (Exception error) { entry["error"] = DaemonErrors.Json(error); }
                    entries.Add((JsonNode)entry);
                }
                return new(new() { ["entries"] = entries, ["done"] = file.Done });
            }
            case "close": await RemoveAsync(Number(json, "handle")).ConfigureAwait(false); return EnvDaemonReply.Empty;
            case "listFiles":
            {
                var path = PathArg();
                var listing = (await local.ListFilesAsync(path, new()
                {
                    Hidden = Flag(json, "hidden", true), Gitignore = Flag(json, "gitignore", true),
                    Exclude = json["exclude"] is JsonArray exclude ? exclude.Select(x => x?.GetValue<string>() ?? throw new EnvDaemonException("EINVAL", "Invalid exclude name")).ToImmutableArray() : [],
                    MaxBytes = json["maxBytes"] is null ? null : Number(json, "maxBytes"), MaxEntries = json["maxEntries"] is null ? null : Number(json, "maxEntries"),
                    TimeoutMs = json["timeoutMs"] is null ? null : Number(json, "timeoutMs")
                }, DurableContext.Background.WithCancellation(request.CancellationToken)).ConfigureAwait(false)).GetOrThrow();
                using var bytes = new MemoryStream();
                foreach (var item in listing.Entries)
                {
                    bytes.WriteByte(item.Kind switch { FileKind.File => (byte)'f', FileKind.Directory => (byte)'d', _ => (byte)'l' });
                    bytes.Write(Encoding.UTF8.GetBytes(item.Path)); bytes.WriteByte(0);
                }
                return new(new() { ["count"] = listing.Entries.Length, ["truncated"] = listing.Truncated }, bytes.ToArray());
            }
            default: throw new EnvDaemonException("EINVAL", $"Unknown operation: {op}");
        }
    }

    private long Insert(OpenHandle handle)
    {
        lock (gate)
        {
            if (closed) { handle.Dispose(); throw new OperationCanceledException(); }
            if (handles.Count >= maxHandles) { handle.Dispose(); throw new EnvDaemonException("EMFILE", "Too many open files"); }
            var id = ++nextHandle; handles.Add(id, handle); return id;
        }
    }
    private OpenHandle Handle(long id) { lock (gate) return handles.TryGetValue(id, out var value) ? value : throw BadHandle(); }
    private async ValueTask RemoveAsync(long id)
    {
        OpenHandle? value;
        lock (gate) { if (!handles.Remove(id, out value)) throw BadHandle(); }
        await value.DisposeAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        OpenHandle[] values;
        lock (gate) { closed = true; values = handles.Values.ToArray(); handles.Clear(); }
        foreach (var value in values) await value.DisposeAsync().ConfigureAwait(false);
    }
    public void StopAcceptingHandles() { lock (gate) closed = true; }
    private static EnvDaemonException BadHandle() => new("EBADF", "Bad file descriptor");
    internal static string Text(JsonObject json, string name) => json[name] is JsonValue node && node.TryGetValue<string>(out var value) && !value.Contains('\0') ? value : throw new EnvDaemonException("EINVAL", $"Invalid {name}");
    internal static long Number(JsonObject json, string name) => json[name] is JsonValue node && node.TryGetValue<long>(out var value) && value is >= 0 and <= 9007199254740991 ? value : throw new EnvDaemonException("EINVAL", $"Invalid {name}");
    internal static bool Flag(JsonObject json, string name, bool fallback = false) => json[name] is null ? fallback : json[name] is JsonValue node && node.TryGetValue<bool>(out var value) ? value : throw new EnvDaemonException("EINVAL", $"Invalid {name}");
    private sealed class OpenHandle : IDisposable, IAsyncDisposable
    {
        public OpenHandle(string path, FileStream stream) { Path = path; Stream = stream; }
        public OpenHandle(string path, DaemonDirectory directory) { Path = path; Directory = directory; }
        public string Path { get; }
        public FileStream? Stream { get; }
        public DaemonDirectory? Directory { get; }
        public bool Done { get; set; }
        public void Dispose() { Stream?.Dispose(); Directory?.Dispose(); }
        public async ValueTask DisposeAsync() { if (Stream is not null) await Stream.DisposeAsync().ConfigureAwait(false); Directory?.Dispose(); }
    }
}
