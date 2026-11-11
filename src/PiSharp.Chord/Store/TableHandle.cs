// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/table-handle.ts
//
// Table handles (slice 6a §6; decision "Query runner"): what self.table(Def) returns, one per attempt and table: reads of committed rows
// (page, rows, get, count) through the store's guarded reads, and what a transaction's writes to the table are staged with; a handle of an
// older version than the stored table is refused (R15, slice 7b). Upstream's Table<C, K> and TableOf<D> type the rows by the columns; the
// C# ITable is untyped, a row a map of column to JSON value (Rows.cs). A read's refusal is a faulted task, as upstream's async methods
// reject.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>One page of rows (<c>Items</c>, as Durable's pages) and, when more follow, the cursor to continue after it (upstream
/// <c>Page</c>).</summary>
/// <param name="Items">The rows.</param>
/// <param name="Next">The cursor of the next page; null when no more follow.</param>
public sealed record Page(IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>> Items, string? Next = null);

/// <summary>What <see cref="ITable.PageAsync"/> takes besides the query, the limit and the context (upstream <c>PageOptions</c>).</summary>
public sealed class PageOptions
{
    /// <summary>A cursor from an earlier page of the same order.</summary>
    public string? After { get; init; }
}

/// <summary>A table of an object: reads of committed state, the context last. Writes go through the object's transaction (upstream
/// <c>Table</c>).</summary>
public interface ITable
{
    /// <summary>Up to <paramref name="limit"/> rows of <paramref name="query"/> (after <c>options.After</c>), and the cursor of the next
    /// page when there is one.</summary>
    Task<Page> PageAsync(Query query, long limit, ChordContext context, PageOptions? options = null);

    /// <summary>Every row of <paramref name="query"/>, read a page of 100 at a time.</summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, JsonNode?>> Rows(Query query, ChordContext context);

    /// <summary>The row with <paramref name="key"/> (an object of the key columns), null when absent.</summary>
    Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(object? key, ChordContext context);

    /// <summary>The number of rows of <paramref name="query"/>.</summary>
    Task<long> CountAsync(Query query, ChordContext context);
}

/// <summary>Who owns a table handle: its object (whose transactions alone write it), liveness check and live failure (upstream
/// <c>TableOwner</c>).</summary>
internal interface ITableOwner
{
    /// <summary>The owner's name in refusals.</summary>
    string Label { get; }

    /// <summary>The object's documents: a transaction of another object refuses the handle.</summary>
    object Scope { get; }

    /// <summary>The live queries registered by the owner's active attempts.</summary>
    LiveQueries Queries => TableOwnerQueries.Get(this);

    /// <summary>Throws <c>released</c> once the object ended.</summary>
    void AssertLive();

    /// <summary>A row the code cannot read (a <c>json(...)</c> cell, slice 6a §9) fails the owner <c>broken</c> (§1.4).</summary>
    void Fail(ChordError error);
}

internal static class TableOwnerQueries
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ITableOwner, LiveQueries> Owners = new();
    internal static LiveQueries Get(ITableOwner owner) => Owners.GetValue(owner, static _ => new LiveQueries());
}

internal static partial class StoreInternal
{
    /// <summary>The rows <c>Rows()</c> reads per page (rule 17; upstream <c>ROWS_PAGE</c>).</summary>
    internal const int RowsPage = 100;
}

/// <summary>A table handle: the definition the attempt declared, the table's SQL name and statements, the store (upstream
/// <c>TableHandle</c>).</summary>
internal sealed class TableHandle : ITable
{
    internal TableHandle(TableDefinition definition, string sqlName, IChordStore store, ITableOwner owner)
    {
        Definition = definition;
        Sql = new TableSql(sqlName, definition.Columns, definition.Rows.Physical, definition.Key);
        Store = store;
        Owner = owner;
    }

    internal TableDefinition Definition { get; }

    internal TableSql Sql { get; }

    internal IChordStore Store { get; }

    internal ITableOwner Owner { get; }

    public async Task<Page> PageAsync(Query query, long limit, ChordContext context, PageOptions? options = null)
    {
        Owner.AssertLive();
        AssertVersion();
        AssertQuery(query);
        if (limit < 1 || limit > StoreInternal.MaxSafeInteger) throw DataInternal.Invalid("args", $"{Definition.Name}: limit is a positive integer");
        var order = query.Order;
        var after = options?.After is null ? null : StoreInternal.DecodeCursor(Definition, Sql.Table, order, options.After);
        var statement = Sql.Select(query.Condition, order, after, limit + 1);
        var stored = (await Store.ReadAsync(executor => executor.AllAsync(statement.Sql, [.. statement.Params]), context).ConfigureAwait(false)).ToList();
        var more = stored.Count > limit;
        if (more) stored.RemoveRange((int)limit, stored.Count - (int)limit);
        var items = stored.Select(row => (IReadOnlyDictionary<string, JsonNode?>)Decode(row, context)).ToList();
        if (!more) return new Page(items);
        var last = stored[(int)limit - 1];
        return new Page(items, StoreInternal.EncodeCursor(Sql.Table, order, [.. order.Columns.Select(column => last[column])]));
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, JsonNode?>> Rows(Query query, ChordContext context)
    {
        string? after = null;
        do
        {
            var page = await PageAsync(query, StoreInternal.RowsPage, context, after is null ? null : new PageOptions { After = after }).ConfigureAwait(false);
            foreach (var row in page.Items) yield return row;
            after = page.Next;
        } while (after is not null);
    }

    public async Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(object? key, ChordContext context)
    {
        Owner.AssertLive();
        AssertVersion();
        var encoded = Definition.Rows.Key(key, "get");
        var stored = await Store.ReadAsync(executor => executor.GetAsync(Sql.Get, [.. encoded.Params]), context).ConfigureAwait(false);
        return stored is null ? null : Decode(stored, context);
    }

    public async Task<long> CountAsync(Query query, ChordContext context)
    {
        Owner.AssertLive();
        AssertVersion();
        AssertQuery(query);
        var statement = Sql.Count(query.Condition);
        var row = await Store.ReadAsync(executor => executor.GetAsync(statement.Sql, [.. statement.Params]), context).ConfigureAwait(false);
        return row?["count"] is { } count ? (long)StoreInternal.SqliteNumber(count) : 0;
    }

    /// <summary>The version fence of old table handles (R15, slice 7b §5): once the store's table is at a newer version than this handle's
    /// definition (a reload's new code migrated it), old code reads and writes it no more (<c>released/version</c>), so it never issues
    /// SQL for a dropped column.</summary>
    internal void AssertVersion()
    {
        var stored = Store.TableVersion(Sql.Table);
        if (stored is null || stored <= Definition.Version) return;
        throw new ChordError("released", $"{Owner.Label}: table \"{Definition.Name}\" of version {Definition.Version}; the store has version {stored}", "version");
    }

    /// <summary>A stored row as reads return it; one the code cannot read fails the owner and throws.</summary>
    internal OrderedDictionary<string, JsonNode?> Decode(IReadOnlyDictionary<string, object?> stored, ChordContext context)
    {
        try
        {
            // The codec decodes by the definition's columns: a row of them.
            return Definition.Rows.Decode(stored, context);
        }
        catch (Exception error)
        {
            // Upstream's String(error) of a non-Chord error: its message.
            var failure = error as ChordError ?? new ChordError("unavailable", error.Message, cause: error);
            Owner.Fail(failure);
            throw failure;
        }
    }

    /// <summary>A query of another table is refused (<c>invalid/args</c>).</summary>
    private void AssertQuery(Query query)
    {
        if (!ReferenceEquals(query?.Table, Definition))
            throw DataInternal.Invalid("args", $"{Owner.Label}: a query of table \"{query?.Table.Name}\" run on \"{Definition.Name}\"");
    }
}
