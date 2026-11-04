// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/apply-batch.ts
//
// Batch apply (slice 6b §1.6, S5, S17): a replica takes commits from..to as an outbox gave them, in one backend transaction through the
// store's commit queue, each write applied with its commit's seq by the applier (a trust boundary), lastSeq and the allocator set from the
// last commit. Internal in upstream (the sync layer's entry point, marked @internal); public here on Tables, as the store is, under the
// assembly's experimental attribute. A commit's writes are the recorded JSON (Outbox.cs); each is checked against the write schema
// (Archive.cs) before the queue, then applied in the store's turn.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Sqlite;

namespace PiSharp.Chord.Store;

public static partial class Tables
{
    /// <summary>Applies <paramref name="commits"/> (from an outbox, in seq order) to <paramref name="store"/>: refused while an owner holds a
    /// claim on it, once it diverged, or when the first commit does not follow its <c>lastSeq</c> or one does not follow the one before; a
    /// write the store's records refuse rolls the whole batch back (<c>invalid/args</c>); a recorded count the store does not reproduce rolls
    /// it back and marks the store diverged, so every later batch is refused. Commits are written to the store's outbox too when it has one
    /// (upstream <c>applyBatch</c>).</summary>
    public static Task ApplyBatchAsync(ChordStore store, IReadOnlyList<OutboxCommit> commits, ChordContext context)
    {
        List<CheckedCommit> checkedCommits;
        try
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(commits);
            ArgumentNullException.ThrowIfNull(context);
            checkedCommits = [.. commits.Select((commit, index) => StoreInternal.CheckCommit(commit, $"batch: commit {index}", context))];
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
        return store.ReplayAsync((executor, catalog) => StoreInternal.ReplayBatchAsync(store, checkedCommits, executor, catalog),
            error => error is Diverged);
    }
}

/// <summary>A commit whose envelope and writes passed the checks.</summary>
internal sealed record CheckedCommit(long Seq, long NextId, IReadOnlyList<JsonObject> Writes, JsonArray Recorded);

internal static partial class StoreInternal
{
    /// <summary>One commit's envelope and writes, checked (a trust boundary; upstream <c>checkCommit</c>).</summary>
    internal static CheckedCommit CheckCommit(OutboxCommit? commit, string what, ChordContext context)
    {
        if (commit is null || !IsSafeInteger(commit.Seq) || !IsSafeInteger(commit.NextId) || commit.Writes is null)
            throw DataInternal.Invalid("args", $"{what}: not an outbox commit");
        List<JsonObject> writes = [.. commit.Writes.Select((write, index) => CheckWrite(write, $"{what}: write {index}", context))];
        return new CheckedCommit(commit.Seq, commit.NextId, writes, commit.Writes);
    }

    /// <summary>The batch inside the store's turn: seqs continuous from <c>lastSeq</c>, allocator values never falling (upstream
    /// <c>replay</c>).</summary>
    internal static async Task<Replayed> ReplayBatchAsync(ChordStore store, IReadOnlyList<CheckedCommit> commits, ISqliteExecutor executor,
        IReadOnlyDictionary<string, TableRecord> catalog)
    {
        var tables = new Dictionary<string, TableRecord>(catalog, StringComparer.Ordinal);
        var changed = new Dictionary<string, TableRecord>(StringComparer.Ordinal);
        var lastSeq = store.LastSeq;
        var nextId = store.NextId;
        foreach (var (index, commit) in commits.Index())
        {
            var what = $"batch: commit {index} (seq {commit.Seq})";
            if (commit.Seq != lastSeq + 1) throw DataInternal.Invalid("args", $"{what}: does not follow seq {lastSeq}");
            if (commit.NextId < nextId) throw DataInternal.Invalid("args", $"{what}: its next id {commit.NextId} is below {nextId}");
            var step = new Step(executor, commit.Seq, new IdAllocator(commit.NextId), tables);
            foreach (var (position, write) in commit.Writes.Index())
            {
                var kind = (string)write["kind"]!;
                var where = $"{what}: write {position} ({kind})";
                if (kind == "document.create") CheckCreated(Integer(write["record"]!["id"]), commit.NextId, where);
                await ApplyWriteAsync(step, write, where).ConfigureAwait(false);
            }
            await WriteCatalogAsync(step).ConfigureAwait(false);
            foreach (var (name, record) in step.Tables)
            {
                tables[name] = record;
                changed[name] = record;
            }
            if (store.RecordsOutbox)
            {
                await executor.RunAsync($"INSERT INTO {StoreTables.Outbox} (seq, format, next_id, writes) VALUES (?, ?, ?, ?)", commit.Seq,
                    OutboxFormat, commit.NextId, ChordJson.Stringify(commit.Recorded)).ConfigureAwait(false);
            }
            lastSeq = commit.Seq;
            nextId = commit.NextId;
        }
        await executor.RunAsync($"UPDATE {StoreTables.Meta} SET last_seq = ?, next_id = ? WHERE singleton = 1", lastSeq, nextId).ConfigureAwait(false);
        return new Replayed(lastSeq, nextId, changed);
    }

    /// <summary>A created document's id is below its commit's next id (the allocator never mints it again), or equal to a saturated next
    /// id (2^53 - 1 may be written, as import allows; upstream <c>checkCreated</c>).</summary>
    private static void CheckCreated(long id, long nextId, string what)
    {
        if (id < nextId || (nextId == MaxSafeIntegerLong && id == nextId)) return;
        throw DataInternal.Invalid("args", $"{what}: id {id} is not below the commit's next id {nextId}");
    }
}
