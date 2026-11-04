// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/step.ts
//
// The commit step's working state (slice 6a §3, slice 6b §1.3) and the shape every write takes into it: a write staged before the queue
// (serialized, rule 8), applied inside the step, returning the writes it recorded. Area cv2-store-tables added this file because the table
// writes are staged writes applied in a step; it ports step.ts except the step's id allocator (upstream's ids: IdAllocator, D3), which
// comes with allocator.ts in area cv2-store-engine, which owns step.ts. Upstream's StagedWrite is an object type that an adapter's class
// implements (Durable's DurableCommit), so here it is the interface IStagedWrite.
using PiSharp.Chord.Sqlite;

namespace PiSharp.Chord.Store;

/// <summary>Where a staged write goes in its commit: retirements, creations, then everything else, each phase in staging order (upstream
/// <c>WriteOrder</c>). A retirement frees a live address before a creation takes it (the partial unique index); an adapter's composite
/// write orders its own parts inside <see cref="Write"/>.</summary>
public enum WriteOrder { Retire, Create, Write }

/// <summary>A write staged for the commit step: its order and what it does there (upstream <c>StagedWrite</c>).</summary>
public interface IStagedWrite
{
    /// <summary>The write's phase in its commit.</summary>
    WriteOrder Order { get; }

    /// <summary>Applies the write inside the step's backend transaction; returns the writes as the outbox records them, in the order
    /// applied, with what only the step knows resolved (its seq, a record's revision).</summary>
    Task<IReadOnlyList<StoreWrite>> ApplyAsync(Step step);
}

/// <summary>A staged write of a phase and a function (upstream's object literal <c>{ order, apply }</c>).</summary>
internal sealed class StagedWrite(WriteOrder order, Func<Step, Task<IReadOnlyList<StoreWrite>>> apply) : IStagedWrite
{
    public WriteOrder Order { get; } = order;

    public Task<IReadOnlyList<StoreWrite>> ApplyAsync(Step step) => apply(step);
}

/// <summary>One commit step: its backend transaction, its seq, the ids of the documents it created, the table records it changed (the
/// store's catalog takes them after <c>COMMIT</c>) and the statements it ran (<c>chord.store.statements</c>). Its reads are the step reader
/// of adapters: they see the step's own writes (upstream <c>Step</c>).</summary>
public sealed class Step
{
    private readonly IReadOnlyDictionary<string, TableRecord> catalog;

    internal Step(ISqliteExecutor executor, long seq, IReadOnlyDictionary<string, TableRecord> catalog)
    {
        Executor = executor;
        Seq = seq;
        this.catalog = catalog;
    }

    /// <summary>The step's backend transaction.</summary>
    public ISqliteExecutor Executor { get; }

    /// <summary>The seq this step commits.</summary>
    public long Seq { get; }

    /// <summary>The ids of the commit's created documents, in the order of their writes.</summary>
    public List<long> Created { get; } = [];

    /// <summary>The table records this step changed, by SQL name.</summary>
    internal OrderedDictionary<string, TableRecord> Tables { get; } = new(StringComparer.Ordinal);

    /// <summary>The statements the step ran.</summary>
    public long Statements { get; internal set; }

    /// <summary>The record of table <paramref name="name"/> as this step leaves it so far.</summary>
    internal TableRecord? Table(string name) =>
        Tables.TryGetValue(name, out var changed) ? changed : catalog.TryGetValue(name, out var recorded) ? recorded : null;

    /// <summary>Runs one statement.</summary>
    public Task RunAsync(string sql, params object?[] parameters)
    {
        Statements++;
        return Executor.RunAsync(sql, parameters);
    }

    /// <summary>Runs one statement and returns the rows it changed.</summary>
    public Task<long> RunCountedAsync(string sql, params object?[] parameters)
    {
        Statements++;
        return Executor.RunCountedAsync(sql, parameters);
    }

    /// <summary>Runs one query for its first row.</summary>
    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters)
    {
        Statements++;
        return Executor.GetAsync(sql, parameters);
    }

    /// <summary>Runs one query.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters)
    {
        Statements++;
        return Executor.AllAsync(sql, parameters);
    }
}
