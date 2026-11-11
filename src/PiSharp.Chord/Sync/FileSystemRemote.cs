// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/filesystem.ts.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Chord.Sync;

public static class SyncPaths
{
    private static readonly Regex SessionId = new(@"\A[A-Za-z0-9_-][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);
    public static string CheckSessionId(string sessionId)
    {
        if (sessionId is null || !SessionId.IsMatch(sessionId))
            throw new ArgumentException("Invalid session ID for a sync directory: " + sessionId, nameof(sessionId));
        return sessionId;
    }
}

/// <summary>A directory-backed remote. Concurrent writers in this process are serialized. Publication is check-then-write;
/// one process must own writing this directory. Cross-process compare-and-swap is not supported.</summary>
public sealed class FileSystemSyncRemote(ISyncFileSystem fs, string root) : ISessionSyncMaintenance
{
    private static readonly SemaphoreSlim Writers = new(1, 1);
    private static readonly Regex SlotName = new(@"\A([0-9]{16})\.json\z", RegexOptions.CultureInvariant);
    private static readonly Regex SnapshotName = new(@"\A([0-9]{16})\.batch\z", RegexOptions.CultureInvariant);

    public async Task<PutBatchResult> PutBatchAsync(string sessionId, long after, long to, string device,
        IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context, PutBatchOptions? options = null)
    {
        SyncPaths.CheckSessionId(sessionId);
        Pad(after); Pad(to);
        await Writers.WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        try
        {
            var existing = await SlotAsync(sessionId, after, context).ConfigureAwait(false);
            if (existing is not null) return new PutBatchResult.Exists(existing.To, existing.Device);
            var head = SyncRemote.Head(await ListAsync(sessionId, context).ConfigureAwait(false));
            if (after != head) return new PutBatchResult.NotHead(head);
            await WriteAsync(sessionId, "batches", Pad(after) + ".batch", body, context).ConfigureAwait(false);
            // The body is complete before this slot makes it visible to remote readers.
            await WriteSlotAsync(sessionId, new(after, to, device, Archive: options?.Archive == true), context).ConfigureAwait(false);
            return new PutBatchResult.Created();
        }
        finally { Writers.Release(); }
    }

    public async Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> GetBatchAsync(string sessionId, long after, ChordContext context)
    {
        var slot = await SlotAsync(sessionId, after, context).ConfigureAwait(false);
        return slot is null || slot.Folded ? null : await StreamAsync(sessionId, "batches", Pad(after) + ".batch", context).ConfigureAwait(false);
    }
    public Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> GetSnapshotAsync(string sessionId, long to, ChordContext context)
        => StreamAsync(sessionId, "snapshots", Pad(to) + ".batch", context);
    public Task PutSnapshotAsync(string sessionId, long to, IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context)
        => WriteAsync(sessionId, "snapshots", Pad(to) + ".batch", body, context);

    public async Task FoldBatchesAsync(string sessionId, IReadOnlyList<long> afters, ChordContext context)
    {
        SyncPaths.CheckSessionId(sessionId);
        await Writers.WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var after in afters)
            {
                var slot = await SlotAsync(sessionId, after, context).ConfigureAwait(false);
                if (slot is null || slot.Folded) continue;
                await WriteSlotAsync(sessionId, slot with { Folded = true }, context).ConfigureAwait(false);
                var path = await PathAsync(sessionId, context, "batches", Pad(after) + ".batch").ConfigureAwait(false);
                (await fs.RemoveAsync(path, new(Force: true), context).ConfigureAwait(false)).Unwrap();
            }
        }
        finally { Writers.Release(); }
    }

    public async Task DeleteSnapshotsAsync(string sessionId, IReadOnlyList<long> snapshots, ChordContext context)
    {
        SyncPaths.CheckSessionId(sessionId);
        foreach (var to in snapshots)
        {
            var path = await PathAsync(sessionId, context, "snapshots", Pad(to) + ".batch").ConfigureAwait(false);
            (await fs.RemoveAsync(path, new(Force: true), context).ConfigureAwait(false)).Unwrap();
        }
    }

    public async Task<RemoteListing> ListAsync(string sessionId, ChordContext context)
    {
        var batches = new List<BatchInfo>();
        foreach (var name in await NamesAsync(sessionId, "slots", context).ConfigureAwait(false))
        {
            if (!TryNumber(SlotName, name, out var after)) continue;
            var slot = await SlotAsync(sessionId, after, context).ConfigureAwait(false);
            if (slot is not null) batches.Add(slot);
        }
        var snapshots = new List<long>();
        foreach (var name in await NamesAsync(sessionId, "snapshots", context).ConfigureAwait(false))
            if (TryNumber(SnapshotName, name, out var to)) snapshots.Add(to);
        return new(batches, snapshots);
    }

    /// <summary>A directory has no change cursor, so every call returns every session in ordinal order.</summary>
    public async Task<SessionChanges> SessionsAsync(string? cursor, ChordContext context)
    {
        var result = await fs.ListDirAsync(root, context).ConfigureAwait(false);
        if (result.IsNotFound) return new([], "", false);
        var sessions = new List<SessionSummary>();
        foreach (var id in result.Unwrap().Where(info => info.Kind == "directory" && IsSessionId(info.Name))
                     .Select(info => info.Name).Order(StringComparer.Ordinal))
            sessions.Add(new(id, SyncRemote.Head(await ListAsync(id, context).ConfigureAwait(false)),
                await GetMetaAsync(id, context).ConfigureAwait(false)));
        return new(sessions, "", false);
    }

    public Task PutMetaAsync(string sessionId, JsonObject meta, ChordContext context)
        => WriteAsync(sessionId, null, "meta.json", JsonBody(meta), context);
    public async Task<JsonObject?> GetMetaAsync(string sessionId, ChordContext context)
    {
        var bytes = await ReadAsync(sessionId, context, "meta.json").ConfigureAwait(false);
        return bytes is null ? null : JsonNode.Parse(bytes.Value.Span)?.AsObject();
    }

    private async Task<BatchInfo?> SlotAsync(string sessionId, long after, ChordContext context)
    {
        var bytes = await ReadAsync(sessionId, context, "slots", Pad(after) + ".json").ConfigureAwait(false);
        if (bytes is null) return null;
        var slot = JsonNode.Parse(bytes.Value.Span)!.AsObject();
        return new(slot["after"]!.GetValue<long>(), slot["to"]!.GetValue<long>(), slot["device"]!.GetValue<string>(),
            slot["folded"]?.GetValue<bool>() == true, slot["archive"]?.GetValue<bool>() == true);
    }
    private Task WriteSlotAsync(string sessionId, BatchInfo slot, ChordContext context)
    {
        var json = new JsonObject { ["after"] = slot.After, ["to"] = slot.To, ["device"] = slot.Device };
        if (slot.Archive) json["archive"] = true;
        if (slot.Folded) json["folded"] = true;
        return WriteAsync(sessionId, "slots", Pad(slot.After) + ".json", JsonBody(json), context);
    }
    private static IAsyncEnumerable<ReadOnlyMemory<byte>> JsonBody(JsonObject value)
        => SyncFiles.Once(Encoding.UTF8.GetBytes(value.ToJsonString()));
    private async Task<IReadOnlyList<string>> NamesAsync(string sessionId, string directory, ChordContext context)
    {
        var path = await PathAsync(sessionId, context, directory).ConfigureAwait(false);
        var result = await fs.ListDirAsync(path, context).ConfigureAwait(false);
        return result.IsNotFound ? [] : result.Unwrap().Select(info => info.Name).Order(StringComparer.Ordinal).ToArray();
    }
    private async Task<ReadOnlyMemory<byte>?> ReadAsync(string sessionId, ChordContext context, params string[] parts)
    {
        var result = await fs.ReadBinaryFileAsync(await PathAsync(sessionId, context, parts).ConfigureAwait(false), context).ConfigureAwait(false);
        // A conditional with a bare null and ReadOnlyMemory<byte> can bind null through the byte[] conversion,
        // yielding an empty (present) memory instead of a missing nullable value. Keep the missing branch explicit.
        if (result.IsNotFound) return null;
        return result.Unwrap();
    }
    private async Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> StreamAsync(string sessionId, string directory, string name, ChordContext context)
    {
        var path = await PathAsync(sessionId, context, directory, name).ConfigureAwait(false);
        var probe = await fs.OpenBinaryReaderAsync(path, null, context).ConfigureAwait(false);
        if (probe.IsNotFound) return null;
        // Do not retain a handle when the caller never starts consuming this body. Unlike upstream's eager handle,
        // the lazy reader can fail if maintenance deletes the file between this probe and enumeration.
        await probe.Unwrap().CloseAsync(ChordContext.WithoutAbortSignal(context)).ConfigureAwait(false);
        return StreamFileAsync(path, context);
    }
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamFileAsync(string path, ChordContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None, cancellationToken);
        var readContext = ChordContext.WithAbortSignal(cancellation.Token, ChordContext.WithoutAbortSignal(context));
        var reader = (await fs.OpenBinaryReaderAsync(path, null, readContext).ConfigureAwait(false)).Unwrap();
        try
        {
            long offset = 0;
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var chunk = (await reader.ReadAsync(offset, SyncFiles.ReadChunk, readContext).ConfigureAwait(false)).Unwrap();
                if (chunk.Length == 0) yield break;
                if (chunk.Length > SyncFiles.ReadChunk) throw new IOException("Filesystem reader exceeded the requested chunk length");
                offset += chunk.Length;
                yield return chunk;
            }
        }
        finally { await reader.CloseAsync(ChordContext.WithoutAbortSignal(context)).ConfigureAwait(false); }
    }
    private async Task WriteAsync(string sessionId, string? directory, string name,
        IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context)
        => await SyncFiles.WriteAtomicAsync(fs,
            await PathAsync(sessionId, context, directory is null ? [] : [directory]).ConfigureAwait(false), name, body, context).ConfigureAwait(false);
    private async Task<string> PathAsync(string sessionId, ChordContext context, params string[] parts)
        => (await fs.JoinPathAsync([root, SyncPaths.CheckSessionId(sessionId), .. parts], context).ConfigureAwait(false)).Unwrap();
    private static string Pad(long value)
    {
        if (value < 0 || value > 9_007_199_254_740_991L) throw new ArgumentOutOfRangeException(nameof(value), "Sequence must be a non-negative safe integer");
        return value.ToString("D16", CultureInfo.InvariantCulture);
    }
    private static bool TryNumber(Regex expression, string name, out long value)
    {
        var match = expression.Match(name);
        value = 0;
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value <= 9_007_199_254_740_991L;
    }
    private static bool IsSessionId(string value)
    {
        try { SyncPaths.CheckSessionId(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
