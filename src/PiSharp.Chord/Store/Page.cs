// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/page.ts
//
// Pages (decision "Query runner"; slice 14 §3.1): one page of a query read once, not live, with the opaque cursor that continues after it.
// A table handle's PageAsync and a named query's page are this.
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>The stored rows of one page and, when more follow, the cursor to continue after it (upstream <c>StoredPage</c>).</summary>
internal sealed record StoredPage(IReadOnlyList<IReadOnlyDictionary<string, object?>> Stored, string? Next = null);

internal static partial class StoreInternal
{
    /// <summary>Up to <paramref name="limit"/> rows of <paramref name="plan"/>'s query after the cursor <paramref name="after"/> (a trust
    /// boundary: an app hands it back), read under the store's guard. <paramref name="limit"/> is a positive integer
    /// (<c>invalid/args</c>; upstream <c>readPage</c>).</summary>
    internal static async Task<StoredPage> ReadPageAsync(IChordStore store, ReadPlan plan, long limit, string? after, ChordContext context)
    {
        var (query, table, order) = (plan.Query, plan.Table, plan.Order);
        if (limit < 1 || limit > MaxSafeInteger) throw DataInternal.Invalid("args", $"{query.Table.Name}: limit is a positive integer");
        var from = after is null ? null : DecodeCursor(query.Table, table, order, after, query.Column);
        // One row more than asked for: whether a further page exists.
        var statement = plan.Select(new ReadRange(limit + 1, from));
        var stored = await store.ReadAsync(executor => executor.AllAsync(statement.Sql, [.. statement.Params]), context).ConfigureAwait(false);
        if (stored.Count <= limit) return new StoredPage(stored);
        List<IReadOnlyDictionary<string, object?>> page = [.. stored.Take((int)limit)];
        var last = page[^1];
        return new StoredPage(page, EncodeCursor(table, order, [.. order.Columns.Select(name => last[name])]));
    }
}
