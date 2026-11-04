// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/store.ts
//
// The store (slice 6a §2, §3, slice 6b §1): opened over a backend, one commit queue that serializes only the commit step, reads guarded as
// Durable's GuardedStorage guards them. Area cv2-store-tables added this file with what the declared tables use of the store: the seal
// fence a commit takes (upstream CommitFence) and the members of upstream's ChordStore that table handles and the eager migrations call
// (table, tableVersion, announceTable, commit, read), as the interface IChordStore. Area cv2-store-engine, which owns store.ts, ports the
// store itself (openStore, the Store interface apps hold, ChordStore and its commit step, series, replays and close) and has ChordStore
// implement IChordStore; until then the table cases run against a test store (tests/PiSharp.Chord.Tests/Store/StoreSupport.cs).
using PiSharp.Chord.Sqlite;

namespace PiSharp.Chord.Store;

/// <summary>The seal fence's two checks of a commit (§1.3), as the commit step calls them (upstream <c>CommitFence</c>).</summary>
/// <param name="Admit">At the step's entry: <c>false</c> rejects the commit <c>released</c> and writes nothing.</param>
/// <param name="Adopt">After <c>COMMIT</c>: adopts the commit, the ids being its created documents' ids in the order of their writes.</param>
internal sealed record CommitFence(Func<bool> Admit, Action<long, IReadOnlyList<long>> Adopt)
{
    /// <summary>The fence of work no attempt owns (an activation's preparation, an adapter): every commit admitted, nothing adopted
    /// (upstream's <c>{ admit: () =&gt; true, adopt: () =&gt; {} }</c>).</summary>
    internal static CommitFence Open { get; } = new(() => true, (_, _) => { });
}

/// <summary>The store as declared tables use it (upstream <c>ChordStore</c>'s members that <c>table-handle.ts</c> and
/// <c>table-migration.ts</c> call).</summary>
internal interface IChordStore
{
    /// <summary>The recorded table <paramref name="name"/> (its SQL name), as of the last commit.</summary>
    TableRecord? Table(string name);

    /// <summary>The version table <paramref name="name"/> is at or is being migrated to: the recorded one, or a higher one a migration
    /// announced before its first commit (<see cref="AnnounceTable"/>), so an old handle is refused before its SQL could queue behind the
    /// migration's DDL (fix round 1, Sol 8).</summary>
    long? TableVersion(string name);

    /// <summary>Announces a migration of table <paramref name="name"/> to <paramref name="version"/> until the returned withdrawal (after
    /// its last commit).</summary>
    Action AnnounceTable(string name, long version);

    /// <summary>Commits <paramref name="writes"/> as one seq when the commit queue reaches it: <c>fence.Admit()</c> at the step's entry, the
    /// writes in their order (retirements, creations, the rest), the outbox commit, <c>lastSeq</c>, <c>COMMIT</c>, then
    /// <c>fence.Adopt</c>. Returns the seq. A refused admission fails <c>released</c>; a step the backend rolled back after the step's own
    /// code threw (a refusal, D4) fails with that error unchanged and the store goes on; any other failure stops the store
    /// (<c>storage_failed</c>). <paramref name="context"/> counts the step (no abort once queued).</summary>
    Task<long> CommitAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context);

    /// <summary>Runs <paramref name="read"/> on the backend under the guard (rule 11): refused once the store stopped or closed; a backend
    /// error fails the store unless <paramref name="context"/> was aborted (then <c>aborted</c>).</summary>
    Task<T> ReadAsync<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context);
}
