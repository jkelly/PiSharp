// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/read.ts
//
// Reads (slice 14 §3.2, §3.3): one statement the store keeps current. A read is registered with the store under every table it uses; a
// commit that writes one of them runs the skip test and, unless it skips, runs the read again in its step. What a read does with its rows
// is its sink's business (a live query's rows, a list's page, a count). Mappings:
// - ReadRun and ReadKind are closed record hierarchies (upstream's tagged unions); a run is compared by reference, as upstream's `===`.
// - ReadExecutor is ISqliteExecutor: a commit step (Step implements it, counting its statements) or the backend under the store's guarded
//   read.
// - Upstream's identity of a row is the value itself for one column and JSON text for several, compared with JavaScript's `===`. A written
//   key arrives here as doubles and a fetched row holds longs, which are different boxed values, so the identity is always the JSON text
//   of the values (StoreInternal.SqliteJson writes a long 7 and a double 7 alike).
// - Upstream keeps a step's run with the backend transaction it ran in (the executor); here it is the step's Transaction token, since an
//   adapter may hand every transaction the same executor object.
// - Threads: a read's state is read by commit steps (the skip test, on the store's queue) and set by adoptions (in the host's batch); it is
//   kept under a short lock.
using PiSharp.Chord.Sqlite;

namespace PiSharp.Chord.Store;

/// <summary>The rows of one run, at most the range's limit; <paramref name="More"/> says a further row followed inside the range. The keys
/// are of every row fetched, the one past the limit included (slice 14 §3.3; upstream <c>ReadResult</c>).</summary>
/// <param name="Rows">The rows, as the backend returned them.</param>
/// <param name="More">Whether a further row followed inside the range.</param>
/// <param name="Keys">The own keys of the fetched rows, each as <see cref="StoreInternal.IdentityOf"/> its key values.</param>
/// <param name="Paired">Per use of another table, the fetched rows' values of the own columns it pairs by, each as an identity.</param>
/// <param name="Past">The position of the row past the limit, when there is one: the read's range ends there.</param>
internal sealed record ReadResult(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool More, IReadOnlySet<string> Keys,
    IReadOnlyDictionary<Use, HashSet<string>> Paired, IReadOnlyList<object?>? Past);

/// <summary>One run of a read: its result, the number of a count, or why it could not run (upstream <c>ReadRun</c>).</summary>
internal abstract record ReadRun
{
    private ReadRun()
    {
    }

    /// <summary>The rows of a range.</summary>
    internal sealed record Rows(ReadResult Result) : ReadRun;

    /// <summary>The number of rows of a condition.</summary>
    internal sealed record Count(long Number) : ReadRun;

    /// <summary>The run failed.</summary>
    internal sealed record Failed(Exception Error) : ReadRun;
}

/// <summary>Who takes a read's runs: called inside the batch of the commit that ran it (upstream <c>ReadSink</c>).</summary>
internal interface IReadSink
{
    /// <summary>Takes <paramref name="run"/>, inside a batch.</summary>
    void Adopt(ReadRun run);
}

/// <summary>A sink of a function (upstream's object literal <c>{ adopt }</c>).</summary>
internal sealed class ReadSink(Action<ReadRun> adopt) : IReadSink
{
    public void Adopt(ReadRun run) => adopt(run);
}

/// <summary>What a read is: the rows of a range, or the number of rows of its condition (upstream <c>ReadKind</c>).</summary>
internal abstract record ReadKind
{
    private ReadKind()
    {
    }

    /// <summary>The rows of <paramref name="Range"/>.</summary>
    internal sealed record Rows(ReadRange Range) : ReadKind;

    /// <summary>The number of rows of the condition.</summary>
    internal sealed record Count : ReadKind;
}

/// <summary>A read registered with the store. It keeps what the skip test needs of its last adopted run, and of a run a commit has not
/// adopted yet (tentative: a later step of the same backend transaction tests against it) (upstream <c>Read</c>).</summary>
internal sealed class Read
{
    private readonly Lock gate = new();
    // The last adopted run; null while loading or failed: such a read is never skipped.
    private ReadRun? adopted;
    // A step's run not adopted yet, with the backend transaction it ran in (a rolled-back one's is stale).
    private (object Transaction, ReadRun Run)? tentative;

    internal Read(ReadPlan plan, ReadKind what, IReadSink sink)
    {
        Plan = plan;
        What = what;
        Sink = sink;
    }

    /// <summary>The plan its statements come from.</summary>
    internal ReadPlan Plan { get; }

    /// <summary>What it reads.</summary>
    internal ReadKind What { get; }

    /// <summary>Who takes its runs.</summary>
    internal IReadSink Sink { get; }

    /// <summary>Takes <paramref name="rows"/> as its adopted result without running: every row of its range, known to its owner already (a
    /// list's page cut from another one's rows). The sink is not told.</summary>
    internal void Know(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        // A count has no rows to know.
        var range = ((ReadKind.Rows)What).Range;
        var known = new ReadRun.Rows(Result(rows, range with { Limit = Math.Max(range.Limit, rows.Count) }));
        lock (gate) adopted = known;
    }

    /// <summary>Drops its adopted result: without one it is never skipped, so the next commit over its tables runs it.</summary>
    internal void Forget()
    {
        lock (gate) adopted = null;
    }

    /// <summary>The run the skip test of a step in <paramref name="transaction"/> compares against: that transaction's latest, else the
    /// adopted one.</summary>
    internal ReadRun? Latest(object transaction)
    {
        lock (gate) return tentative is { } step && ReferenceEquals(step.Transaction, transaction) ? step.Run : adopted;
    }

    /// <summary>Runs the read inside a commit step, after its writes: the run, or the failure; it never throws.</summary>
    internal async Task<ReadRun> RunAsync(ISqliteExecutor step, ChordContext context)
    {
        try
        {
            return await FetchAsync(step, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new ReadRun.Failed(error);
        }
    }

    /// <summary>One run on <paramref name="reader"/>, counted where it happens; a backend error throws (the store's guarded read takes
    /// it).</summary>
    internal async Task<ReadRun> FetchAsync(ISqliteExecutor reader, ChordContext context)
    {
        context.Count("chord.query.runs");
        if (What is not ReadKind.Rows { Range: var range })
        {
            var counting = Plan.Count();
            var row = await reader.GetAsync(counting.Sql, [.. counting.Params]).ConfigureAwait(false);
            return new ReadRun.Count(row?["count"] is { } count ? (long)StoreInternal.SqliteNumber(count) : 0);
        }
        // One row more than asked for: whether a further row exists, and where the range ends.
        var statement = Plan.Select(range with { Limit = range.Limit + 1 });
        var stored = await reader.AllAsync(statement.Sql, [.. statement.Params]).ConfigureAwait(false);
        context.Count("chord.query.rows", stored.Count);
        return new ReadRun.Rows(Result(stored, range));
    }

    /// <summary>A step's run, kept for the later steps of its backend transaction until it is adopted.</summary>
    internal void Ran(object transaction, ReadRun run)
    {
        lock (gate) tentative = (transaction, run);
    }

    /// <summary>Takes <paramref name="run"/> as its state and hands it to the sink.</summary>
    internal void Adopt(ReadRun run)
    {
        lock (gate)
        {
            if (tentative is { } step && ReferenceEquals(step.Run, run)) tentative = null;
            adopted = run is ReadRun.Failed ? null : run;
        }
        Sink.Adopt(run);
    }

    /// <summary>The range its probe asks about: the read's own, ending at the row past its limit when there is one.</summary>
    internal ReadRange Probed(ReadResult result)
    {
        // A count is never probed (slice 14 §3.3).
        var range = ((ReadKind.Rows)What).Range;
        return result.Past is null ? range with { Limit = 1 } : range with { Through = result.Past, Limit = 1 };
    }

    private ReadResult Result(IReadOnlyList<IReadOnlyDictionary<string, object?>> stored, ReadRange range)
    {
        var keyColumns = Plan.Query.Table.Key;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var paired = new Dictionary<Use, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var use in Plan.Uses)
            if (use.Role != UseRole.Own && use.Pairs is not null) paired[use] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in stored)
        {
            keys.Add(StoreInternal.Identity(keyColumns, row));
            foreach (var (use, values) in paired) values.Add(StoreInternal.Identity(use.Pairs!, row));
        }
        var more = stored.Count > range.Limit;
        IReadOnlyList<object?>? past = more ? [.. Plan.Order.Columns.Select(name => stored[(int)range.Limit][name])] : null;
        return new ReadResult(more ? [.. stored.Take((int)range.Limit)] : stored, more, keys, paired, past);
    }
}

internal static partial class StoreInternal
{
    /// <summary>The identity of <paramref name="row"/> in <paramref name="columns"/>, for a set or a map of rows of one read: the JSON text
    /// of its values there (upstream <c>identity</c>; a column the row has not is <c>null</c>).</summary>
    internal static string Identity(IReadOnlyList<string> columns, IReadOnlyDictionary<string, object?> row) =>
        SqliteJson(columns.Select(name => row.GetValueOrDefault(name)));

    /// <summary>The identity of <paramref name="values"/>, as <see cref="Identity"/> gives it for a row holding them (upstream
    /// <c>identityOf</c>).</summary>
    internal static string IdentityOf(IEnumerable<object?> values) => SqliteJson(values);
}
