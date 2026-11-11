using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Env;

public sealed partial class RemoteExecutionEnv
{
    private async Task<Result<T, FileError>> FileOp<T>(string path, DurableContext context, Func<string, Task<T>> action, bool after = false)
    {
        var resolved = path;
        try
        {
            CheckCancellation(context, path);
            resolved = await ResolveAsync(path, context.CancellationToken).ConfigureAwait(false);
            CheckCancellation(context, resolved);
            var value = await action(resolved).ConfigureAwait(false);
            if (after) CheckCancellation(context, resolved);
            return value;
        }
        catch (Exception error) { return FileFailure(error, resolved); }
    }
    private Task<Result<Unit, FileError>> Mutation(string op, string path, DurableContext context, JsonObject? extra = null, bool after = false) =>
        FileOp(path, context, async resolved =>
        {
            var json = extra ?? new(); json["path"] = resolved;
            await Connection.RequestAsync(op, json).ConfigureAwait(false);
            return Unit.Value;
        }, after);
    public Task<Result<string, FileError>> AbsolutePathAsync(string path, DurableContext context) => FileOp(path, context, resolved => Task.FromResult(resolved));
    public async Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context)
    {
        try { CheckCancellation(context); return RemotePaths.Join(await Connection.InfoAsync(context.CancellationToken).ConfigureAwait(false), parts); }
        catch (Exception error) { return FileFailure(error); }
    }
    internal static FileMetadata Metadata(string path, JsonObject json)
    {
        var kind = json["kind"]?.GetValue<string>() switch
        {
            "file" => FileKind.File, "directory" => FileKind.Directory, "symlink" => FileKind.Symlink,
            _ => throw new FileError(FileErrorCode.Invalid, "Unsupported file type", path),
        };
        return new(json["name"]?.GetValue<string>() ?? RemotePaths.Name(path), path, kind, json["size"]!.GetValue<long>(),
            json["mtimeSec"]!.GetValue<double>() * 1000 + json["mtimeNsec"]!.GetValue<double>() / 1e6);
    }
    public Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context) =>
        FileOp(path, context, async resolved => Metadata(resolved, (await Connection.RequestAsync("lstat", new() { ["path"] = resolved }).ConfigureAwait(false)).Json));
    public async Task<Result<bool, FileError>> ExistsAsync(string path, DurableContext context)
    {
        var result = await FileInfoAsync(path, context).ConfigureAwait(false);
        return result.IsOk ? true : result.Error.Code == FileErrorCode.NotFound ? false : result.Error;
    }
    public Task<Result<string, FileError>> CanonicalPathAsync(string path, DurableContext context) => FileOp(path, context,
        async resolved => (await Connection.RequestAsync("realpath", new() { ["path"] = resolved }).ConfigureAwait(false)).Json["path"]!.GetValue<string>());
    private async Task<RemoteHandle> Open(string path, bool read, bool noFollow = false)
    {
        var json = new JsonObject { ["path"] = path, ["noFollow"] = noFollow };
        if (read) json["mode"] = "read";
        var reply = await Connection.RequestAsync("open", json).ConfigureAwait(false);
        var handle = Handle(reply, path);
        if (reply.Json["statError"] is JsonObject error) { await handle.CloseAsync().ConfigureAwait(false); throw new RemoteError(error); }
        return handle;
    }
    public Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context) =>
        FileOp<IBinaryReader>(path, context, async resolved =>
        {
            var handle = await Open(resolved, false, options?.NoFollow == true).ConfigureAwait(false);
            if (context.IsCancellationRequested) { await handle.CloseAsync().ConfigureAwait(false); CheckCancellation(context, resolved); }
            return new RemoteBinaryReader(handle);
        });
    public Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, DurableContext context) =>
        FileOp<ITextLineReader>(path, context, async resolved =>
        {
            var handle = await Open(resolved, true).ConfigureAwait(false);
            if (context.IsCancellationRequested) { await handle.CloseAsync().ConfigureAwait(false); CheckCancellation(context, resolved); }
            return new RemoteTextLineReader(handle);
        });
    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context) => FileOp(path, context, async resolved =>
    {
        var handle = await Open(resolved, true).ConfigureAwait(false);
        try
        {
            using var bytes = new MemoryStream();
            var knownSize = handle.Stat?["kind"]?.GetValue<string>() == "file" ? handle.Stat["size"]!.GetValue<long>() : 0;
            if (knownSize > int.MaxValue) throw new FileError(FileErrorCode.Unknown, "File size is greater than 2 GiB", resolved);
            if (knownSize > 0) return await handle.ReadRangeAsync(0, knownSize, 512 * 1024, context).ConfigureAwait(false);
            while (knownSize == 0 || bytes.Length < knownSize)
            {
                CheckCancellation(context, resolved);
                var length = knownSize == 0 ? 64 * 1024 : Math.Min(512 * 1024, knownSize - bytes.Length);
                var request = new JsonObject { ["length"] = length };
                if (knownSize > 0) request["offset"] = bytes.Length;
                var chunk = (await handle.RequestAsync("pread", request).ConfigureAwait(false)).Payload;
                CheckCancellation(context, resolved);
                if (chunk.Length > length) throw new InvalidDataException("Oversized pread response");
                if (chunk.Length == 0) break;
                if (bytes.Length + chunk.Length > int.MaxValue) throw new FileError(FileErrorCode.Unknown, "File size is greater than 2 GiB", resolved);
                bytes.Write(chunk);
            }
            return bytes.ToArray();
        }
        finally { await handle.CloseAsync().ConfigureAwait(false); }
    });
    public async Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context)
    {
        var bytes = await ReadBinaryFileAsync(path, context).ConfigureAwait(false);
        return bytes.IsOk ? Encoding.UTF8.GetString(bytes.Value) : bytes.Error;
    }
    public async Task<Result<ImmutableArray<string>, FileError>> ReadTextLinesAsync(string path, ReadTextLinesOptions? options, DurableContext context)
    {
        if (options?.MaxLines <= 0) return ImmutableArray<string>.Empty;
        var opened = await OpenTextLineReaderAsync(path, context).ConfigureAwait(false);
        if (!opened.IsOk) return opened.Error;
        var lines = ImmutableArray.CreateBuilder<string>();
        try
        {
            while (options?.MaxLines is not { } max || lines.Count < max)
            {
                var next = await opened.Value.ReadLineAsync(context).ConfigureAwait(false);
                if (!next.IsOk) return next.Error;
                if (next.Value is not { } line) break;
                lines.Add(line.Text);
            }
            return lines.ToImmutable();
        }
        finally { await opened.Value.CloseAsync(context).ConfigureAwait(false); }
    }
    private Task<Result<Unit, FileError>> Write(string path, ReadOnlyMemory<byte> content, bool append, DurableContext context) => FileOp(path, context, async resolved =>
    {
        const int chunkSize = 512 * 1024;
        var keep = content.Length > chunkSize;
        var reply = await Connection.RequestAsync("write", new() { ["path"] = resolved, ["append"] = append, ["keep"] = keep },
            new() { Payload = content[..Math.Min(content.Length, chunkSize)] }).ConfigureAwait(false);
        if (keep)
        {
            var handle = Handle(reply, resolved);
            var pending = new Queue<Task<RemoteReply>>();
            try
            {
                for (var offset = chunkSize; offset < content.Length;)
                {
                    if (!append && context.IsCancellationRequested)
                    {
                        while (pending.TryDequeue(out var prior)) await prior.ConfigureAwait(false);
                        CheckCancellation(context, resolved);
                    }
                    var length = Math.Min(chunkSize, content.Length - offset);
                    pending.Enqueue(handle.RequestAsync("writeChunk", new(), content.Slice(offset, length)));
                    offset += length;
                    if (pending.Count >= 8) await pending.Dequeue().ConfigureAwait(false);
                }
                while (pending.TryDequeue(out var write)) await write.ConfigureAwait(false);
            }
            finally
            {
                while (pending.TryDequeue(out var write)) try { await write.ConfigureAwait(false); } catch (Exception) { }
                await handle.CloseAsync().ConfigureAwait(false);
            }
        }
        return Unit.Value;
    }, after: append);
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) => Write(path, content, false, context);
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) => Write(path, content, true, context);
    public Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context) => size < 0 || size > 9007199254740991L
        ? Task.FromResult<Result<Unit, FileError>>(new FileError(FileErrorCode.Invalid, "Invalid file size", path))
        : Mutation("truncate", path, context, new() { ["size"] = size }, true);
    public Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context) => Mutation("fsync", path, context, after: true);
    public Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context) => Mutation("mkdir", path, context, new() { ["recursive"] = options?.Recursive ?? true });
    public Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context) => Mutation("rm", path, context, new() { ["recursive"] = options?.Recursive ?? false, ["force"] = options?.Force ?? false });
    public Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context) => FileOp(sourcePath, context, async source =>
    {
        var destination = await ResolveAsync(destinationPath, context.CancellationToken).ConfigureAwait(false);
        CheckCancellation(context, destination);
        await Connection.RequestAsync("rename", new() { ["path"] = source, ["to"] = destination }).ConfigureAwait(false);
        return Unit.Value;
    });
    public async Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context)
    {
        try
        {
            CheckCancellation(context);
            var info = await Connection.InfoAsync(context.CancellationToken).ConfigureAwait(false);
            return (await Connection.RequestAsync("mkdtemp", new() { ["path"] = RemotePaths.Join(info, [info.Tmpdir, prefix ?? "tmp-"]) }).ConfigureAwait(false)).Json["path"]!.GetValue<string>();
        }
        catch (Exception error) { return FileFailure(error); }
    }
    public async Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context)
    {
        var directory = await CreateTempDirAsync("tmp-", context).ConfigureAwait(false);
        if (!directory.IsOk) return directory.Error;
        return await FileOp(directory.Value, context, async resolved =>
        {
            var info = await Connection.InfoAsync(context.CancellationToken).ConfigureAwait(false);
            var path = RemotePaths.Join(info, [resolved, (options?.Prefix ?? "") + Guid.NewGuid().ToString() + (options?.Suffix ?? "")]);
            await Connection.RequestAsync("write", new() { ["path"] = path, ["append"] = false, ["parents"] = false }).ConfigureAwait(false);
            return path;
        }).ConfigureAwait(false);
    }
}
