// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/remote.ts.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Sync;

/// <summary>A remote history slot. Folding removes its body, but the slot remains taken.</summary>
public sealed record BatchInfo(long After, long To, string Device, bool Folded = false, bool Archive = false);
public sealed record RemoteListing(IReadOnlyList<BatchInfo> Batches, IReadOnlyList<long> Snapshots);
public sealed record RemoteHistory(long? Snapshot, IReadOnlyList<BatchInfo> Batches, long Head);
public sealed record SessionSummary(string Id, long Head, JsonObject? Meta);
public sealed record SessionChanges(IReadOnlyList<SessionSummary> Sessions, string Cursor, bool More);
public sealed record PutBatchOptions(bool Archive = false, long? Epoch = null);

public abstract record PutBatchResult
{
    private PutBatchResult() { }
    public sealed record Created : PutBatchResult;
    public sealed record Exists(long To, string Device) : PutBatchResult;
    public sealed record NotHead(long Head) : PutBatchResult;
    public sealed record StaleEpoch(long Epoch) : PutBatchResult;
}

/// <summary>The client's view of a session remote. Bodies are single-pass streams; a refused upload may leave its body unread.</summary>
public interface ISessionSyncStore
{
    Task<PutBatchResult> PutBatchAsync(string sessionId, long after, long to, string device,
        IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context, PutBatchOptions? options = null);
    Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> GetBatchAsync(string sessionId, long after, ChordContext context);
    Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> GetSnapshotAsync(string sessionId, long to, ChordContext context);
    Task<RemoteListing> ListAsync(string sessionId, ChordContext context);
    Task<SessionChanges> SessionsAsync(string? cursor, ChordContext context);
    Task PutMetaAsync(string sessionId, JsonObject meta, ChordContext context);
    Task<JsonObject?> GetMetaAsync(string sessionId, ChordContext context);
}

/// <summary>Remote maintenance operations; a client never deletes or folds history.</summary>
public interface ISessionSyncMaintenance : ISessionSyncStore
{
    Task PutSnapshotAsync(string sessionId, long to, IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context);
    Task FoldBatchesAsync(string sessionId, IReadOnlyList<long> afters, ChordContext context);
    Task DeleteSnapshotsAsync(string sessionId, IReadOnlyList<long> snapshots, ChordContext context);
}

public sealed class HistoryUnavailableError(long head) : Exception($"The remote history does not continue from {head}");

/// <summary>Pure remote history operations. Slots form one increasing chain from zero.</summary>
public static class SyncRemote
{
    public const long LogStart = 0;

    public static RemoteHistory History(RemoteListing listing, long? from = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var chain = Chain(listing);
        var batches = new List<BatchInfo>();
        long? snapshot = null;
        long at;
        if (from is null or LogStart)
        {
            snapshot = listing.Snapshots.Count == 0 ? null : listing.Snapshots.Max();
            at = snapshot ?? LogStart;
        }
        else
        {
            at = from.Value;
            var inside = listing.Batches.FirstOrDefault(batch => batch.After < at && at < batch.To);
            if (inside is not null)
            {
                batches.Add(inside);
                at = inside.To;
            }
        }
        while (chain.TryGetValue(at, out var next))
        {
            // A malformed remote listing must not trap a native caller in an infinite loop.
            if (next.To <= at) throw new ArgumentException("Remote history does not increase the sequence", nameof(listing));
            batches.Add(next);
            at = next.To;
        }
        return new RemoteHistory(snapshot, batches, at);
    }

    public static long Head(RemoteListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var chain = Chain(listing);
        var at = LogStart;
        while (chain.TryGetValue(at, out var next))
        {
            if (next.To <= at) throw new ArgumentException("Remote history does not increase the sequence", nameof(listing));
            at = next.To;
        }
        return at;
    }

    private static Dictionary<long, BatchInfo> Chain(RemoteListing listing)
    {
        var chain = new Dictionary<long, BatchInfo>();
        foreach (var batch in listing.Batches) chain[batch.After] = batch;
        return chain;
    }
}
