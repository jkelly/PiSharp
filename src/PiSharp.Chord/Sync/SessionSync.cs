// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/client.ts.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Sync;

public sealed record SyncState(long? SyncedHead, long? Pulling = null);

public interface ISyncStateStore
{
    Task<SyncState?> GetAsync(string sessionId, ChordContext context);
    Task SetAsync(string sessionId, SyncState state, ChordContext context);
}

public sealed class OutboxUnavailableError(string message) : Exception(message);
public sealed class UnsyncedCommitsError(string sessionId, long localHead, long syncedHead)
    : Exception($"This copy of session {sessionId} is at {localHead}, past what it shares with the remote ({syncedHead})");

public abstract record UploadResult
{
    private UploadResult() { }
    public sealed record Uploaded(long Head) : UploadResult;
    public sealed record Idle : UploadResult;
    public sealed record Diverged(long At) : UploadResult;
    public sealed record Behind(long Head, long SyncedHead) : UploadResult;
    public sealed record StaleEpoch(long At, long Epoch) : UploadResult;
}

public abstract record LocalCopy
{
    private LocalCopy() { }
    public sealed record Store(IStore Value) : LocalCopy;
    public sealed record Empty(Backend Backend) : LocalCopy;
}

public sealed record UploadOptions
{
    public required string SessionId { get; init; }
    public required string Device { get; init; }
    public required IStore Source { get; init; }
    public required ISessionSyncStore Store { get; init; }
    public required ISyncStateStore State { get; init; }
    public required ChordContext Context { get; init; }
    public long? Epoch { get; init; }
}

public sealed record PullOptions
{
    public required string SessionId { get; init; }
    public required ISessionSyncStore Store { get; init; }
    public required LocalCopy Local { get; init; }
    public required ISyncStateStore State { get; init; }
    public required ChordContext Context { get; init; }
}

public sealed record FoldOptions
{
    public required string SessionId { get; init; }
    public required ISessionSyncMaintenance Store { get; init; }
    public required Backend Scratch { get; init; }
    public required ChordContext Context { get; init; }
}

public sealed record PullResult(IStore Store, long Head);

/// <summary>Portable outbox upload, replay, restore and fold. Each operation on a copy is serialized by its caller.</summary>
public static class SessionSync
{
    public const int BatchBytes = 8 * 1024 * 1024;
    public const int ApplyBytes = 4 * 1024 * 1024;
    private const int OutboxPage = 100;
    private const int PageBytes = 1024 * 1024;

    public static long StoreHead(IStore store) => store.LastSeq;

    private static long SharedHead(SyncState? state, long local) =>
        state?.Pulling is { } pulling && local <= pulling ? local : state?.SyncedHead ?? SyncRemote.LogStart;

    private static Outbox OutboxOf(IStore store) => store.Outbox ?? throw new OutboxUnavailableError(
        "The session's store has no outbox: open it with { outbox: true } to sync it");

    public static Task<IReadOnlyList<OutboxCommit>> ReadOutboxAsync(IStore store, long after, int limit, ChordContext context)
    {
        var outbox = OutboxOf(store);
        if (after < outbox.Floor)
            throw new OutboxUnavailableError($"The outbox starts after {outbox.Floor}, past the synced head {after}");
        return outbox.ReadAsync(after, limit, context);
    }

    public static async Task<UploadResult> UploadSessionAsync(UploadOptions options)
    {
        var (sessionId, device, source, remote, state, context) =
            (options.SessionId, options.Device, options.Source, options.Store, options.State, options.Context);
        var outbox = OutboxOf(source);
        var synced = SharedHead(await state.GetAsync(sessionId, context).ConfigureAwait(false), StoreHead(source));
        long? uploaded = null;
        while (true)
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
            var head = StoreHead(source);
            if (head < synced) return new UploadResult.Behind(head, synced);
            long to;
            IAsyncEnumerable<ReadOnlyMemory<byte>> body;
            ExportedSnapshot? snapshot = null;
            var archive = false;
            if (synced == SyncRemote.LogStart && outbox.Floor > 0)
            {
                snapshot = await ExportSnapshotAsync(source, context).ConfigureAwait(false);
                to = snapshot.To;
                body = snapshot.Body;
                archive = true;
            }
            else
            {
                var range = await OutboxRangeAsync(source, synced, context).ConfigureAwait(false);
                if (range is null) return uploaded is { } last ? new UploadResult.Uploaded(last) : new UploadResult.Idle();
                to = range.Value;
                body = SyncCodec.EncodeBatch(OutboxCommits(source, synced, to, context), context.AbortSignal ?? default);
            }
            var after = synced;
            PutBatchResult result;
            try
            {
                result = await remote.PutBatchAsync(sessionId, after, to, device, body, context,
                    new PutBatchOptions(archive, options.Epoch)).ConfigureAwait(false);
            }
            finally
            {
                // A rejected slot may leave the archive entirely unread; it still holds the store's commit queue.
                if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false);
            }
            switch (result)
            {
                case PutBatchResult.StaleEpoch stale: return new UploadResult.StaleEpoch(after, stale.Epoch);
                case PutBatchResult.NotHead: return new UploadResult.Diverged(after);
                case PutBatchResult.Exists existing when existing.Device != device: return new UploadResult.Diverged(after);
            }
            synced = result is PutBatchResult.Exists sameDevice ? sameDevice.To : to;
            await state.SetAsync(sessionId, new SyncState(synced), context).ConfigureAwait(false);
            var trimmable = Math.Min(synced, source.LastSeq);
            if (trimmable > outbox.Floor) await outbox.TrimAsync(trimmable, context).ConfigureAwait(false);
            if (result is PutBatchResult.Created) uploaded = to;
        }
    }

    private static async Task<long?> OutboxRangeAsync(IStore store, long after, ChordContext context)
    {
        long? to = null;
        long bytes = 0;
        var pages = new OutboxPages(store, after, context);
        for (var page = await pages.NextAsync().ConfigureAwait(false); page.Count > 0;
             page = await pages.NextAsync().ConfigureAwait(false))
        {
            foreach (var commit in page)
            {
                var size = EncodedSize(commit);
                if (to is not null && bytes + size > BatchBytes) return to;
                bytes += size;
                to = commit.Seq;
            }
            if (bytes >= BatchBytes) break;
        }
        return to;
    }

    private static async IAsyncEnumerable<OutboxCommit> OutboxCommits(IStore store, long after, long to, ChordContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pages = new OutboxPages(store, after, context);
        var at = after;
        while (at < to)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await pages.NextAsync().ConfigureAwait(false);
            if (page.Count == 0) throw new OutboxUnavailableError($"The outbox ends at {at}, before {to}");
            foreach (var commit in page)
            {
                if (commit.Seq > to) yield break;
                yield return commit;
                at = commit.Seq;
            }
        }
    }

    private sealed class OutboxPages(IStore store, long after, ChordContext context)
    {
        private long after = after;
        private int limit = OutboxPage;
        private bool done;

        internal async Task<IReadOnlyList<OutboxCommit>> NextAsync()
        {
            if (done) return [];
            var page = await ReadOutboxAsync(store, after, limit, context).ConfigureAwait(false);
            if (page.Count < limit) done = true;
            if (page.Count == 0) return page;
            after = page[^1].Seq;
            var average = page.Average(EncodedSize);
            limit = Math.Max(1, Math.Min(OutboxPage, (int)Math.Floor(PageBytes / average)));
            return page;
        }
    }

    // Upstream measures JS UTF-16 string length and allows 64 characters of envelope overhead.
    private static int EncodedSize(OutboxCommit commit) => checked(ChordJson.Stringify(commit.Writes).Length + 64);

    private sealed class ExportedSnapshot(long to, IAsyncEnumerable<ReadOnlyMemory<byte>> body, StoreExport parts) : IAsyncDisposable
    {
        internal long To { get; } = to;
        internal IAsyncEnumerable<ReadOnlyMemory<byte>> Body { get; } = body;
        public ValueTask DisposeAsync() => parts.DisposeAsync();
    }

    private static async Task<ExportedSnapshot> ExportSnapshotAsync(IStore store, ChordContext context)
    {
        var parts = store.Export(context);
        try
        {
            if (!await parts.MoveNextAsync().ConfigureAwait(false) || (string?)parts.Current["kind"] != "manifest")
                throw new InvalidOperationException("A store export does not start with its manifest");
            var manifest = parts.Current;
            var to = manifest["lastSeq"]!.GetValue<long>();
            return new ExportedSnapshot(to,
                SyncCodec.EncodeSnapshot(SnapshotParts(parts, manifest), context.AbortSignal ?? default), parts);
        }
        catch
        {
            await parts.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async IAsyncEnumerable<JsonObject> SnapshotParts(StoreExport parts, JsonObject manifest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using (((IAsyncDisposable)parts).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return manifest;
            while (await parts.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return parts.Current;
            }
        }
    }

    private static async Task ApplyBodyAsync(ChordStore store, IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context)
    {
        var chunk = new List<OutboxCommit>();
        long bytes = 0;
        await foreach (var line in SyncCodec.DecodeBatchLines(body, context.AbortSignal ?? default).ConfigureAwait(false))
        {
            if (line.Commit.Seq <= store.LastSeq) continue;
            chunk.Add(line.Commit);
            bytes += line.Bytes;
            if (bytes < ApplyBytes) continue;
            await Tables.ApplyBatchAsync(store, chunk, context).ConfigureAwait(false);
            chunk = [];
            bytes = 0;
        }
        if (chunk.Count > 0) await Tables.ApplyBatchAsync(store, chunk, context).ConfigureAwait(false);
    }

    public static async Task<PullResult> PullSessionAsync(PullOptions options)
    {
        var (sessionId, remote, local, state, context) =
            (options.SessionId, options.Store, options.Local, options.State, options.Context);
        var listing = await remote.ListAsync(sessionId, context).ConfigureAwait(false);
        IStore? target = (local as LocalCopy.Store)?.Value;
        long At() => target?.LastSeq ?? SyncRemote.LogStart;
        var synced = SharedHead(await state.GetAsync(sessionId, context).ConfigureAwait(false), At());
        if (At() > synced) throw new UnsyncedCommitsError(sessionId, At(), synced);
        var history = SyncRemote.History(listing, At());
        try
        {
            var bodies = new List<(long To, bool Archive, Func<Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?>> Read)>();
            if (history.Snapshot is { } snapshot)
                bodies.Add((snapshot, true, () => remote.GetSnapshotAsync(sessionId, snapshot, context)));
            foreach (var batch in history.Batches)
                bodies.Add((batch.To, batch.Archive, () => remote.GetBatchAsync(sessionId, batch.After, context)));
            foreach (var body in bodies)
            {
                context.AbortSignal?.ThrowIfCancellationRequested();
                if (body.To <= At()) continue;
                var bytes = await body.Read().ConfigureAwait(false) ?? throw new HistoryUnavailableError(At());
                await state.SetAsync(sessionId, new SyncState(At(), body.To), context).ConfigureAwait(false);
                if (body.Archive)
                {
                    if (target is not null || local is not LocalCopy.Empty empty) throw new HistoryUnavailableError(At());
                    target = await Tables.ImportStoreAsync(empty.Backend,
                        SyncCodec.DecodeSnapshot(bytes, context.AbortSignal ?? default), context,
                        new StoreOptions { Outbox = true }).ConfigureAwait(false);
                }
                else
                {
                    if (target is null)
                    {
                        if (local is not LocalCopy.Empty empty) throw new InvalidOperationException("A store copy has no target");
                        target = await Tables.OpenStoreAsync(empty.Backend, context, new StoreOptions { Outbox = true }).ConfigureAwait(false);
                    }
                    if (target is not ChordStore chord) throw new ArgumentException("Batch replay requires a ChordStore", nameof(options));
                    await ApplyBodyAsync(chord, bytes, context).ConfigureAwait(false);
                }
                await state.SetAsync(sessionId, new SyncState(At()), context).ConfigureAwait(false);
            }
            if (target is null)
            {
                if (local is not LocalCopy.Empty empty) throw new InvalidOperationException("A store copy has no target");
                target = await Tables.OpenStoreAsync(empty.Backend, context, new StoreOptions { Outbox = true }).ConfigureAwait(false);
            }
            var head = At();
            if (head < SyncRemote.Head(listing)) throw new HistoryUnavailableError(head);
            await state.SetAsync(sessionId, new SyncState(head), context).ConfigureAwait(false);
            if (target.Outbox is { } outbox && head > outbox.Floor) await outbox.TrimAsync(head, context).ConfigureAwait(false);
            return new PullResult(target, head);
        }
        catch
        {
            if (local is LocalCopy.Empty && target is not null)
            {
                try { await target.CloseAsync(context).ConfigureAwait(false); }
                catch { /* Preserve the original pull failure. */ }
            }
            throw;
        }
    }

    public static async Task<long?> FoldSessionAsync(FoldOptions options)
    {
        var (sessionId, remote, scratch, context) = (options.SessionId, options.Store, options.Scratch, options.Context);
        var listing = await remote.ListAsync(sessionId, context).ConfigureAwait(false);
        var history = SyncRemote.History(listing);
        if (history.Batches.Count + (history.Snapshot is null ? 0 : 1) < 2) return null;
        var restored = await PullSessionAsync(new PullOptions
        {
            SessionId = sessionId, Store = remote, Local = new LocalCopy.Empty(scratch), State = new ScratchState(), Context = context,
        }).ConfigureAwait(false);
        try
        {
            await using var snapshot = await ExportSnapshotAsync(restored.Store, context).ConfigureAwait(false);
            await remote.PutSnapshotAsync(sessionId, snapshot.To, snapshot.Body, context).ConfigureAwait(false);
            await snapshot.DisposeAsync().ConfigureAwait(false);
            await remote.FoldBatchesAsync(sessionId,
                [.. history.Batches.Where(batch => batch.To <= snapshot.To).Select(batch => batch.After)], context).ConfigureAwait(false);
            await remote.DeleteSnapshotsAsync(sessionId, [.. listing.Snapshots.Where(at => at < snapshot.To)], context).ConfigureAwait(false);
            return snapshot.To;
        }
        finally
        {
            await restored.Store.CloseAsync(context).ConfigureAwait(false);
        }
    }

    private sealed class ScratchState : ISyncStateStore
    {
        private SyncState? state;
        public Task<SyncState?> GetAsync(string sessionId, ChordContext context) => Task.FromResult(state);
        public Task SetAsync(string sessionId, SyncState value, ChordContext context)
        {
            state = value;
            return Task.CompletedTask;
        }
    }
}
