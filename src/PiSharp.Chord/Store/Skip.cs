// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/skip.ts
//
// The skip test (slice 14 §3.3): whether a commit's writes can have changed a read's result. A skip proves the result unchanged by the row
// before the write (the keys and matched values the read kept of its last run) and by the row after it (the probe: the read's own
// statement, limited to the rows the written row pairs with). There is no second evaluator: the database decides with the real condition.
// Upstream's Change is a union of a single-key write and a bulk one; here it is one record with a kind, the key null for a bulk write.
namespace PiSharp.Chord.Store;

/// <summary>The kind of a <see cref="TableChange"/>.</summary>
internal enum TableChangeKind { Put, Update, Delete, Bulk }

/// <summary>One write of a transaction as the skip test reads it. A single-key write names its key and, for an <c>update</c>, the columns
/// it assigns; a bulk one the columns it assigns (null: it deletes rows) (upstream <c>Change</c>).</summary>
/// <param name="Table">The table's SQL name.</param>
/// <param name="Kind">What the write does.</param>
/// <param name="Key">A single-key write's key, by column, as SQLite values; null for a bulk write.</param>
/// <param name="Assigned">The columns an <c>update</c> or an <c>updateWhere</c> assigns; null otherwise.</param>
internal sealed record TableChange(string Table, TableChangeKind Kind, IReadOnlyDictionary<string, object?>? Key, IReadOnlyList<string>? Assigned);

internal static partial class StoreInternal
{
    /// <summary>How many probes one read may ask in one step before it runs instead. A probe costs a few microseconds and a run of a page a
    /// few hundred (upstream's <c>benchmarks.md</c>), so a transaction writing many rows (an import, a bulk <c>put</c>) must not probe a
    /// read once per row: past this many, running it is cheaper than asking on (upstream <c>PROBES_PER_READ</c>).</summary>
    internal const int ProbesPerRead = 8;

    /// <summary>Whether <paramref name="changes"/> (the writes of one step, already applied) leave <paramref name="read"/>'s result as it
    /// is: every change is skipped for every use of its table by the read. A read without a result (loading, failed) is never skipped
    /// (upstream <c>skips</c>).</summary>
    internal static async Task<bool> SkipsAsync(Read read, IReadOnlyList<TableChange> changes, Step step, ChordContext context)
    {
        var latest = read.Latest(step.Transaction);
        if (latest is null or ReadRun.Failed) return false;
        var result = (latest as ReadRun.Rows)?.Result;
        var budget = new ProbeBudget { Probes = ProbesPerRead };
        foreach (var change in changes)
        {
            foreach (var use in read.Plan.Uses)
            {
                if (use.Table != change.Table) continue;
                if (!await SkippedAsync(read, use, change, result, step, context, budget).ConfigureAwait(false)) return false;
            }
        }
        return true;
    }

    /// <summary>The probes a read may still ask in one step.</summary>
    private sealed class ProbeBudget
    {
        internal int Probes;
    }

    /// <summary>One change against one use of its table (the table of slice 14 §3.3, row by row; upstream <c>skipped</c>).</summary>
    private static async Task<bool> SkippedAsync(Read read, Use use, TableChange change, ReadResult? result, Step step, ChordContext context, ProbeBudget budget)
    {
        bool Assigns(IReadOnlySet<string> columns) => change.Assigned is null || change.Assigned.Any(columns.Contains);
        // "Any": an update (a bulk one too) that assigns none of the columns the read depends on in that table.
        if (change.Kind is not (TableChangeKind.Put or TableChangeKind.Delete) && !Assigns(use.Depends)) return true;
        // A bulk write names no rows, and a count keeps none: nothing more can be proved.
        if (change.Kind == TableChangeKind.Bulk || result is null) return false;
        // Where the value before is unknown and matters, the read runs: a matched column outside the key may have been re-pointed, and the
        // rows it paired with before are unknown.
        if (!use.Stable) return false;
        List<object?> values = [.. use.PairedBy.Select(name => change.Key!.GetValueOrDefault(name))];
        IReadOnlySet<string>? fetched = use.Role == UseRole.Own ? result.Keys : result.Paired.GetValueOrDefault(use);
        // Before: a fetched row the written row pairs with decided the result (unknown pairs count as one).
        if (fetched is null || fetched.Contains(IdentityOf(values))) return false;
        // After. A row that was not in the result cannot enter it by leaving (its own delete, its partner's), nor by an update of columns
        // that decide neither membership nor position. An exists row's delete or update can bring its own rows in (not(exists(...))): only
        // the probe tells.
        if (use.Role != UseRole.Exists && (change.Kind == TableChangeKind.Delete || (change.Kind == TableChangeKind.Update && !Assigns(use.Enters)))) return true;
        if (!use.Keyed || budget.Probes-- == 0) return false;
        return !await ProbeAsync(read, use.Pairs!, values, result, step, context).ConfigureAwait(false);
    }

    /// <summary>The probe: whether, after the step's writes, a row with <paramref name="values"/> in <paramref name="columns"/> is in the
    /// read's range (upstream <c>probe</c>).</summary>
    private static async Task<bool> ProbeAsync(Read read, IReadOnlyList<string> columns, IReadOnlyList<object?> values, ReadResult result, Step step,
        ChordContext context)
    {
        context.Count("chord.query.probes");
        var statement = read.Plan.Probe(read.Probed(result), columns, values);
        return await step.GetAsync(statement.Sql, [.. statement.Params]).ConfigureAwait(false) is not null;
    }
}
