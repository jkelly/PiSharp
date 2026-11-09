using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Env;

/// <summary>A typed daemon resource: its session is carried on every operation, including close.</summary>
internal sealed class RemoteHandle(RemoteExecutionEnv env, long id, long session, string path)
{
    private int closed;
    internal string Path => path;
    internal JsonObject? Stat { get; init; }
    internal void CheckOpen()
    {
        if (Volatile.Read(ref closed) != 0) throw new FileError(FileErrorCode.Invalid, "Reader is closed", path);
    }
    internal Task<RemoteReply> RequestAsync(string op, JsonObject json, ReadOnlyMemory<byte> payload = default, CancellationToken cancellationToken = default)
    {
        CheckOpen();
        json["handle"] = id;
        return env.Connection.RequestAsync(op, json, new() { Session = session, Payload = payload }, cancellationToken);
    }
    internal async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        env.Released(this);
        try { await env.Connection.RequestAsync("close", new() { ["handle"] = id }, new() { Session = session }).ConfigureAwait(false); }
        catch (Exception) { } // A lost session already released all resources; never reopen it to close this handle.
    }
    internal async Task<byte[]> ReadRangeAsync(long offset, long length, int chunkSize, DurableContext context)
    {
        using var output = new MemoryStream();
        var pending = new Queue<(int Length, Task<RemoteReply> Reply)>();
        var next = offset;
        try
        {
            while (output.Length < length)
            {
                while (pending.Count < 8 && next < offset + length)
                {
                    var size = (int)Math.Min(chunkSize, offset + length - next);
                    pending.Enqueue((size, RequestAsync("pread", new() { ["offset"] = next, ["length"] = size })));
                    next += size;
                }
                var first = pending.Dequeue();
                var reply = await first.Reply.ConfigureAwait(false);
                RemoteExecutionEnv.CheckCancellation(context, path);
                if (reply.Payload.Length > first.Length) throw new InvalidDataException("Oversized pread response");
                if (reply.Payload.Length == 0) break;
                output.Write(reply.Payload);
                if (reply.Payload.Length < first.Length)
                {
                    // These positional reads assumed a full preceding read. Drain them before retrying from the actual end.
                    await DrainAsync().ConfigureAwait(false);
                    next = offset + output.Length;
                }
            }
            return output.ToArray();
        }
        finally { await DrainAsync().ConfigureAwait(false); }

        async Task DrainAsync()
        {
            while (pending.TryDequeue(out var request))
                try { await request.Reply.ConfigureAwait(false); } catch (Exception) { }
        }
    }
}

internal sealed class RemoteBinaryReader(RemoteHandle handle) : IBinaryReader
{
    public async Task<Result<FileMetadata, FileError>> InfoAsync(DurableContext context)
    {
        try { RemoteExecutionEnv.CheckCancellation(context, handle.Path); return RemoteExecutionEnv.Metadata(handle.Path, (await handle.RequestAsync("fstat", new()).ConfigureAwait(false)).Json); }
        catch (Exception error) { return RemoteExecutionEnv.FileFailure(error, handle.Path); }
    }
    public async Task<Result<byte[], FileError>> ReadAsync(long offset, long length, DurableContext context)
    {
        try
        {
            RemoteExecutionEnv.CheckCancellation(context, handle.Path);
            handle.CheckOpen();
            if (offset < 0 || length < 0 || offset > 9007199254740991L || length > int.MaxValue || offset > 9007199254740991L - length)
                throw new FileError(FileErrorCode.Invalid, "Invalid read range", handle.Path);
            return await handle.ReadRangeAsync(offset, length, 256 * 1024, context).ConfigureAwait(false);
        }
        catch (Exception error) { return RemoteExecutionEnv.FileFailure(error, handle.Path); }
    }
    public async Task<Result<LineScan, FileError>> ScanLinesAsync(long startLine, long? endLine, DurableContext context)
    {
        try
        {
            RemoteExecutionEnv.CheckCancellation(context, handle.Path);
            handle.CheckOpen();
            if (startLine < 0 || startLine > 9007199254740991L || endLine is { } end && (end <= startLine || end > 9007199254740991L))
                throw new FileError(FileErrorCode.Invalid, "Invalid line range", handle.Path);
            var json = new JsonObject { ["startLine"] = startLine };
            if (endLine.HasValue) json["endLine"] = endLine.Value;
            var reply = await handle.RequestAsync("scanLines", json, cancellationToken: context.CancellationToken).ConfigureAwait(false);
            long Get(string key) => reply.Json[key]!.GetValue<long>();
            return new LineScan(Get("newlines"), Get("start"), Get("end"), Get("firstLineEnd"), Get("lastLineStart"), Get("selectedBytes"), Get("firstLineBytes"));
        }
        catch (Exception error) { return RemoteExecutionEnv.FileFailure(error, handle.Path); }
    }
    public Task CloseAsync(DurableContext context) => handle.CloseAsync();
}

internal sealed class RemoteTextLineReader(RemoteHandle handle) : ITextLineReader
{
    private readonly StreamDecoder decoder = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private string buffered = "";
    private long offset;
    private bool ended;
    private bool closed;
    private readonly bool positional = handle.Stat?["kind"]?.GetValue<string>() == "file";
    public async Task<Result<TextLine?, FileError>> ReadLineAsync(DurableContext context)
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            RemoteExecutionEnv.CheckCancellation(context, handle.Path);
            if (closed) throw new FileError(FileErrorCode.Invalid, "Text line reader is closed", handle.Path);
            while (true)
            {
                var newline = buffered.IndexOf('\n');
                if (newline >= 0)
                {
                    var text = buffered[..newline]; buffered = buffered[(newline + 1)..];
                    return new TextLine(text, true);
                }
                if (ended)
                {
                    if (buffered.Length == 0) return Result<TextLine?, FileError>.Ok(null);
                    var text = buffered; buffered = ""; return new TextLine(text, false);
                }
                var request = new JsonObject { ["length"] = 64 * 1024 };
                if (positional) request["offset"] = offset;
                var bytes = (await handle.RequestAsync("pread", request).ConfigureAwait(false)).Payload;
                // A positional read can be retried unchanged. A pipe's bytes have already been consumed, so retain them privately
                // before reporting cancellation; the next call still receives the same complete next line.
                if (positional) RemoteExecutionEnv.CheckCancellation(context, handle.Path);
                offset += bytes.Length;
                if (bytes.Length == 0) { buffered += decoder.Finish(); ended = true; }
                else buffered += decoder.Decode(bytes);
                RemoteExecutionEnv.CheckCancellation(context, handle.Path);
            }
        }
        catch (Exception error) { return RemoteExecutionEnv.FileFailure(error, handle.Path); }
        finally { serial.Release(); }
    }
    public async Task CloseAsync(DurableContext context)
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try { closed = true; buffered = ""; await handle.CloseAsync().ConfigureAwait(false); }
        finally { serial.Release(); }
    }
}

internal sealed class RemoteDirReader(RemoteHandle handle, RemoteInfo info) : IDirReader
{
    private bool done;
    public async Task<Result<DirPage, FileError>> NextAsync(int maxEntries, DurableContext context)
    {
        try
        {
            RemoteExecutionEnv.CheckCancellation(context, handle.Path);
            handle.CheckOpen();
            if (maxEntries <= 0) throw new FileError(FileErrorCode.Invalid, "maxEntries must be positive", handle.Path);
            var entries = ImmutableArray.CreateBuilder<FileMetadata>();
            while (!done && entries.Count < maxEntries)
            {
                var reply = await handle.RequestAsync("readdir", new() { ["max"] = Math.Min(1000, maxEntries - entries.Count) }).ConfigureAwait(false);
                RemoteExecutionEnv.CheckCancellation(context, handle.Path);
                foreach (var node in reply.Json["entries"]!.AsArray())
                {
                    var entry = node!.AsObject();
                    var path = RemotePaths.Resolve(info, handle.Path, entry["name"]!.GetValue<string>());
                    if (entry["error"] is JsonObject error)
                    {
                        if (error["code"]?.GetValue<string>() == "ENOENT") continue;
                        throw RemoteExecutionEnv.FileFailure(new RemoteError(error), path);
                    }
                    var metadata = entry["info"]!.AsObject();
                    if (metadata["kind"]?.GetValue<string>() != "other") entries.Add(RemoteExecutionEnv.Metadata(path, metadata));
                }
                done = reply.Json["done"]!.GetValue<bool>();
            }
            return new DirPage(entries.ToImmutable(), done);
        }
        catch (Exception error) { return RemoteExecutionEnv.FileFailure(error, handle.Path); }
    }
    public Task CloseAsync(DurableContext context) => handle.CloseAsync();
}
