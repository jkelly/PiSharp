// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/adapter.ts
//
// The store for adapters (slice 6b §2.1, D1, D2): what an owner outside Chord's objects (Durable's StoreStorage, which PiSharp ports in
// PiSharp.Durable.Store, area store-storage) uses to keep its data in the store as declared tables and engine documents: its tables declared
// and prepared under its owner name (claimed, the path self.table takes), rows encoded at staging and written in a step with what only the
// step knows, and its reads. Internal for adapters in upstream (next/index.ts marks it so); apps use Tables.OpenStoreAsync and objects.
// Upstream's DecodedRow is a row as RowCodec decodes it (a map of column to JsonNode), its SqliteValue an object? binding; an OwnerTable's
// SQL (upstream's public `sql: TableSql`) is internal, as TableSql is: its SQL name is OwnerTable.Table.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Sqlite;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>The tables of one owner name in a store: each declared once (its SQL name <c>"&lt;owner&gt;:&lt;table&gt;"</c> claimed for the
/// claimant), then prepared together (created at their newest version or migrated, as <c>self.table</c>'s are; upstream
/// <c>StoreOwnerTables</c>).</summary>
public sealed class StoreOwnerTables
{
    private readonly IClaimant claimant;
    private readonly List<OwnerTable> tables = [];

    /// <summary><paramref name="owner"/> is a restricted name (lowercase <c>[a-z0-9@/._~-]</c>): it becomes the first part of SQL names.</summary>
    public StoreOwnerTables(ChordStore store, string owner, IClaimant claimant)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(claimant);
        ContractsInternal.CheckName(owner, "An owner name");
        Store = store;
        Owner = owner;
        this.claimant = claimant;
    }

    /// <summary>The store.</summary>
    public ChordStore Store { get; }

    /// <summary>The owner name.</summary>
    public string Owner { get; }

    /// <summary>Declares <paramref name="definition"/> under the owner, claiming its SQL name (held by another: <c>invalid/args</c>).</summary>
    public OwnerTable Declare(TableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var table = new OwnerTable(definition, StoreInternal.TableSqlName(Owner, definition.Name));
        Store.Claims.Table(table.Table, claimant);
        tables.Add(table);
        return table;
    }

    /// <summary>Brings every declared table to its definition: created fresh, or migrated eagerly in their commits.</summary>
    public Task PrepareAsync(ChordContext context) =>
        StoreInternal.PrepareTablesAsync([.. tables.Select(table => new DeclaredTable(table.Definition, table.Table))], Store, CommitFence.Open, Owner, context);
}

/// <summary>One declared table of an owner: its rows encoded and written in a step, its reads by key and by query (upstream
/// <c>OwnerTable</c>).</summary>
public sealed class OwnerTable
{
    /// <summary>A table of <paramref name="definition"/> under the SQL name <paramref name="table"/>.</summary>
    public OwnerTable(TableDefinition definition, string table)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(table);
        Definition = definition;
        Sql = new TableSql(table, definition.Columns, definition.Rows.Physical, definition.Key);
    }

    /// <summary>The table's definition.</summary>
    public TableDefinition Definition { get; }

    /// <summary>The table's SQL name.</summary>
    public string Table => Sql.Table;

    /// <summary>The table's SQL (upstream <c>sql</c>).</summary>
    internal TableSql Sql { get; }

    /// <summary><paramref name="row"/> checked and encoded for a <c>put</c> (a trust boundary, rule 8: at staging, before the queue).</summary>
    public EncodedRow Encode(object? row) => Definition.Rows.Put(row, Definition.Version);

    /// <summary>Writes <paramref name="encoded"/> in <paramref name="step"/> (an upsert) and returns the <c>put</c> as recorded;
    /// <paramref name="stamp"/> names an <c>int</c> column that takes the step's seq (Durable's <c>entries.commit_seq</c>), in the row and in
    /// the record.</summary>
    public async Task<StoreWrite> PutAsync(Step step, EncodedRow encoded, string? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(encoded);
        var version = Definition.Version;
        if (stamp is null)
        {
            await step.RunAsync(Sql.Put, [.. encoded.Params]).ConfigureAwait(false);
            return new TableWrite.Put(Table, encoded.Row, version, encoded.Columns);
        }
        var parameters = encoded.Params.ToArray();
        var physical = Definition.Rows.Physical;
        for (var index = 0; index < physical.Count; index++)
        {
            if (physical[index] == stamp) parameters[index] = step.Seq;
        }
        await step.RunAsync(Sql.Put, parameters).ConfigureAwait(false);
        var row = new OrderedDictionary<string, JsonNode?>(encoded.Row, StringComparer.Ordinal) { [stamp] = step.Seq };
        return new TableWrite.Put(Table, row, version, encoded.Columns);
    }

    /// <summary>The row with the key <paramref name="parameters"/> (in key order), decoded.</summary>
    public async Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(ISqliteExecutor executor, IReadOnlyList<object?> parameters, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(parameters);
        var stored = await executor.GetAsync(Sql.Get, [.. parameters]).ConfigureAwait(false);
        return stored is null ? null : Definition.Rows.Decode(stored, context);
    }

    /// <summary>A read compiled once (the store's query vocabulary rendered to SQL): <paramref name="columns"/> of the rows
    /// <paramref name="where"/> matches in <paramref name="order"/>. The values in <paramref name="where"/> only shape it: each is a <c>?</c>
    /// the caller binds in the order the conditions take them, then the limit. Adapters keep their fixed reads so: no SQL is rendered per
    /// call.</summary>
    public string Prepare(IReadOnlyList<string> columns, WhereNode? where, Order order) => Sql.Select(where, order, null, 0, columns).Sql;

    /// <summary><see cref="Prepare"/> for the first row only: the limit is a literal <c>1</c> (a bound one costs SQLite a plan step per
    /// call).</summary>
    public string PrepareFirst(IReadOnlyList<string> columns, WhereNode? where, Order order)
    {
        var sql = Sql.Select(where, order, null, 1, columns).Sql;
        return sql.EndsWith("LIMIT ?", StringComparison.Ordinal) ? sql[..^"LIMIT ?".Length] + "LIMIT 1" : sql;
    }

    /// <summary>The version of <c>json(...)</c> column <paramref name="column"/>: at 1 every stored cell is at 1 (none is older, a newer one
    /// fails the owner when its table is prepared), so a read may leave the version column out.</summary>
    public long JsonVersion(string column) => JsonColumnOf(column, "jsonVersion").Version;

    /// <summary>A point lookup compiled once: <paramref name="columns"/> of the row whose key the caller binds, in key order.</summary>
    public string PrepareGet(IReadOnlyList<string> columns) => Sql.GetColumns(columns);

    /// <summary>The read of <c>json(...)</c> column <paramref name="column"/>'s stored cells (text written at a version, migrated on access),
    /// made once: an adapter selecting the cell itself reads it so.</summary>
    public Func<string, long, ChordContext, JsonNode?> Reader(string column)
    {
        var json = JsonColumnOf(column, "reader");
        // At version 1 a cell is the JSON it was written as: no version to compare, nothing to migrate.
        if (json.Version == 1) return (text, _, _) => JsonUtf16.MutableNode(text);
        var what = $"{Table}.{column}";
        return (text, version, context) => json.Read(text, version, what, context);
    }

    /// <summary>Up to <paramref name="limit"/> rows matching <paramref name="where"/> in <paramref name="order"/>, decoded.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>>> SelectAsync(ISqliteExecutor executor, WhereNode? where, Order order, long limit,
        ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var statement = Sql.Select(where, order, null, limit);
        var stored = await executor.AllAsync(statement.Sql, [.. statement.Params]).ConfigureAwait(false);
        return [.. stored.Select(row => (IReadOnlyDictionary<string, JsonNode?>)Definition.Rows.Decode(row, context))];
    }

    private JsonColumn JsonColumnOf(string column, string what)
    {
        var found = Definition.Column(column, what);
        return found is { Type: ColumnType.Json, Json: { } json } ? json : throw new InvalidOperationException($"{Definition.Name}: column \"{column}\" is no json(...) column");
    }
}
