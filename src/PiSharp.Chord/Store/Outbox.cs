// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/outbox.ts
//
// The outbox (slice 6a §2; decisions "The outbox is optional, internal and versioned", small items 3-4): the store's commits as the sync
// layer reads them, one { seq, nextId, writes } per commit from its floor on, in the current format. Upstream's OutboxCommit carries its
// writes as JSON.parse returns them (typed StoreWrite only by a cast); here they are the recorded JSON (a JsonArray), which is what batch
// apply checks and applies (ApplyBatch.cs). The outbox's queue of reads and trims runs each operation after the one before settled, never
// inline, as a promise chain does.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Sqlite;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>One commit as the outbox gives it: its seq, the allocator's value it persisted (D3) and its writes in applied order, as recorded
/// (upstream <c>OutboxCommit</c>).</summary>
public sealed record OutboxCommit(long Seq, long NextId, JsonArray Writes)
{
    /// <summary><c>{ seq, nextId, writes }</c> (a copy of the writes).</summary>
    public JsonObject ToJson() => new() { ["seq"] = Seq, ["nextId"] = NextId, ["writes"] = Writes.DeepClone() };
}

/// <summary>A store's outbox: its commits after the floor (the seq it was turned on at, advanced by <see cref="TrimAsync"/>; upstream
/// <c>Outbox</c>).</summary>
public sealed class Outbox
{
    private readonly ChordStore store;
    private readonly Lock gate = new();
    private long floor;
    /// <summary>The outbox's reads and trims, one at a time: a read checks the floor and reads with no trim running (a trim's commit may
    /// have deleted rows before its floor is set), a trim waits for the reads in flight.</summary>
    private Task queue = Task.CompletedTask;

    internal Outbox(ChordStore store, long floor)
    {
        this.store = store;
        this.floor = floor;
    }

    /// <summary>The seq the outbox starts after: <see cref="ReadAsync"/> takes any <c>afterSeq</c> from it to <c>lastSeq</c>.</summary>
    public long Floor => Interlocked.Read(ref floor);

    /// <summary>Up to <paramref name="limit"/> commits after <paramref name="afterSeq"/>, in seq order; <paramref name="afterSeq"/>
    /// outside floor..lastSeq is <c>invalid/args</c>; a commit stored in a newer format is <c>unavailable</c>, its data intact.</summary>
    public async Task<IReadOnlyList<OutboxCommit>> ReadAsync(long afterSeq, long limit, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!StoreInternal.IsSafeInteger(afterSeq)) throw DataInternal.Invalid("args", $"outbox: read after {afterSeq}, not an integer");
        if (!StoreInternal.IsSafeInteger(limit) || limit < 1) throw DataInternal.Invalid("args", "outbox: limit is a positive integer");
        return await Queued(() => ReadNowAsync(afterSeq, limit, context)).ConfigureAwait(false);
    }

    /// <summary>Drops the commits through <paramref name="throughSeq"/> and advances the floor; idempotent; beyond <c>lastSeq</c> is
    /// <c>invalid/args</c>. Trims run one at a time, so the stored floor only rises and equals the one in memory.</summary>
    public Task TrimAsync(long throughSeq, ChordContext context)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(context);
            context.AbortSignal?.ThrowIfCancellationRequested();
            if (!StoreInternal.IsSafeInteger(throughSeq) || throughSeq > store.LastSeq)
                throw DataInternal.Invalid("args", $"outbox: trim through {throughSeq}, beyond {store.LastSeq}");
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
        return Queued(async () =>
        {
            await TrimNowAsync(throughSeq).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Runs <paramref name="operation"/> once the outbox operations before it settled.</summary>
    private Task<T> Queued<T>(Func<Task<T>> operation)
    {
        lock (gate)
        {
            var run = StoreInternal.After(queue, operation);
            queue = StoreInternal.Settled(run);
            return run;
        }
    }

    /// <summary><see cref="ReadAsync"/>, its turn come: the floor as no trim moves it.</summary>
    private async Task<IReadOnlyList<OutboxCommit>> ReadNowAsync(long afterSeq, long limit, ChordContext context)
    {
        if (afterSeq < Floor || afterSeq > store.LastSeq)
            throw DataInternal.Invalid("args", $"outbox: read after {afterSeq}, outside {Floor}..{store.LastSeq}");
        var rows = await store.ReadAsync(executor => executor.AllAsync(
            $"SELECT seq, format, next_id, writes FROM {StoreInternal.StoreTables.Outbox} WHERE seq > ? ORDER BY seq LIMIT ?", afterSeq, limit), context)
            .ConfigureAwait(false);
        return [.. rows.Select(row =>
        {
            var seq = SqliteValues.Integer(row["seq"]);
            var format = SqliteValues.Integer(row["format"]);
            if (format != StoreInternal.OutboxFormat)
                throw new ChordError("unavailable", $"outbox: commit {seq} is in format {format}, newer than {StoreInternal.OutboxFormat}");
            return new OutboxCommit(seq, SqliteValues.Integer(row["next_id"]), JsonUtf16.MutableNode((string)row["writes"]!)!.AsArray());
        })];
    }

    private async Task TrimNowAsync(long throughSeq)
    {
        if (throughSeq <= Floor) return;
        await store.MaintainAsync(async executor =>
        {
            await executor.RunAsync($"DELETE FROM {StoreInternal.StoreTables.Outbox} WHERE seq <= ?", throughSeq).ConfigureAwait(false);
            await executor.RunAsync($"UPDATE {StoreInternal.StoreTables.Meta} SET outbox_floor = ? WHERE singleton = 1", throughSeq).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Interlocked.Exchange(ref floor, throughSeq);
    }
}

internal static partial class StoreInternal
{
    /// <summary>The write vocabulary's version an outbox commit is stored in; a newer one is refused, data intact (upstream
    /// <c>OUTBOX_FORMAT</c>).</summary>
    internal const long OutboxFormat = 1;
}
