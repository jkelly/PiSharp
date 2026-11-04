// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/store/queries.ts
//
// StoreStorage's reads as the store's queries (slice 6b D2, §2.4), compiled once from the store's query vocabulary into SQL over the
// declared tables: no SQL is rendered per call, and a read selects only the record cell (by id, an entry's commit seq too; each column
// returned costs) and parses one JSON text per record, as SqliteStorage's do. Upstream's RecordRow is a row as a query selects it; here it
// is the backend's row map. A record parses into Durable's JsonValue, from the text (at version 1) or from the column's migrated value.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;
using PiSharp.Contracts;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.Durable.Store;

/// <summary>One condition of a read: a column, how it compares, its value (a long or a string); a call site keeps one order of them
/// (upstream <c>Condition</c>).</summary>
internal readonly record struct Condition(string Column, Comparison Op, object Value);

/// <summary>One table's compiled reads: by id (with the <c>byIdExtra</c> columns), and by shape (conditions in binding order, an order; the
/// record only), each shape compiled at its first use (upstream <c>TableQueries</c>).</summary>
internal sealed class TableQueries
{
    private static readonly Order Ascending = new(["id"], OrderDirection.Asc);
    private static readonly Order Descending = new(["id"], OrderDirection.Desc);

    private readonly OwnerTable table;
    /// <summary>The record column's read of a cell at a version past 1; null while the column is at version 1.</summary>
    private readonly Func<string, long, ChordContext, JsonNode?>? versioned;
    private readonly string byId;
    private readonly ConcurrentDictionary<string, string> shapes = new(StringComparer.Ordinal);
    private readonly string[] columns;

    internal TableQueries(OwnerTable table, params string[] byIdExtra)
    {
        this.table = table;
        var hasVersions = table.JsonVersion("record") > 1;
        versioned = hasVersions ? table.Reader("record") : null;
        columns = hasVersions ? ["record", "record$v"] : ["record"];
        byId = table.PrepareGet([.. columns, .. byIdExtra]);
    }

    /// <summary>The row with <paramref name="id"/>, null when absent.</summary>
    internal Task<IReadOnlyDictionary<string, object?>?> ById(ISqliteExecutor executor, long id) => executor.GetAsync(byId, id);

    /// <summary>Up to <paramref name="limit"/> rows whose <paramref name="conditions"/> hold, by id in <paramref name="direction"/>.</summary>
    internal Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Select(ISqliteExecutor executor, IReadOnlyList<Condition> conditions,
        ScanOrder direction, long limit)
    {
        var sql = Shape(conditions, direction, many: true);
        var parameters = new object?[conditions.Count + 1];
        for (var index = 0; index < conditions.Count; index++) parameters[index] = conditions[index].Value;
        parameters[^1] = limit;
        return executor.AllAsync(sql, parameters);
    }

    /// <summary>The first row whose <paramref name="conditions"/> hold, by id in <paramref name="direction"/>; null when none does.</summary>
    internal Task<IReadOnlyDictionary<string, object?>?> First(ISqliteExecutor executor, IReadOnlyList<Condition> conditions, ScanOrder direction)
    {
        var sql = Shape(conditions, direction, many: false);
        return executor.GetAsync(sql, [.. conditions.Select(condition => condition.Value)]);
    }

    /// <summary>A row's record, read as its column's (version checked, migrated on access).</summary>
    internal DurableJson Record(IReadOnlyDictionary<string, object?> row, ChordContext context)
    {
        // The cell holds a record of this table the storage wrote; at version 1 it is that JSON (OwnerTable.Reader).
        var text = (string)row["record"]!;
        return versioned is null ? DurableJson.Parse(text) : DurableJson.Parse(JsonUtf16.ToJsonString(versioned(text, StoreValues.Integer(row["record$v"]), context)));
    }

    /// <summary>Appends the records of <paramref name="rows"/> to <paramref name="into"/>, in order, each read as <see cref="Record"/>
    /// reads it and decoded by <paramref name="decode"/>.</summary>
    internal void Records<T>(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, List<T> into, Func<DurableJson, T> decode, ChordContext context)
    {
        foreach (var row in rows) into.Add(decode(Record(row, context)));
    }

    /// <summary>The compiled read of a shape: its conditions' columns and comparisons, the direction, one row or many.</summary>
    private string Shape(IReadOnlyList<Condition> conditions, ScanOrder direction, bool many)
    {
        var key = $"{(many ? "many" : "first")} {direction}";
        foreach (var condition in conditions) key += $" {condition.Column} {condition.Op}";
        return shapes.GetOrAdd(key, _ =>
        {
            // One comparison per condition: the vocabulary's node of that name, its value a placeholder.
            WhereNode? where = conditions.Count == 0
                ? null
                : new WhereNode.And([.. conditions.Select(condition => (WhereNode)new WhereNode.Compare(condition.Op, condition.Column, StoreQueries.Any))]);
            var order = direction == ScanOrder.Ascending ? Ascending : Descending;
            return many ? table.Prepare(columns, where, order) : table.PrepareFirst(columns, where, order);
        });
    }
}

/// <summary>The compiled reads of the storage's tables (upstream <c>StorageQueries</c>).</summary>
internal sealed class StoreQueries
{
    /// <summary>A placeholder value: a query's shape binds its values at each call.</summary>
    internal static readonly Scalar Any = 0d;

    private static readonly string[] Id = ["id"];

    internal StoreQueries(StoreCommitTables tables)
    {
        var heads = tables.EntryHeads;
        var descending = new Order(["id"], OrderDirection.Desc);
        Conversations = new TableQueries(tables.Conversations);
        Entries = new TableQueries(tables.Entries, "commit_seq");
        LatestHeadAny = heads.PrepareFirst(Id, new WhereNode.Compare(Comparison.Eq, "conversation_id", Any), descending);
        LatestHeadAtOrBefore = heads.PrepareFirst(Id,
            new WhereNode.And([new WhereNode.Compare(Comparison.Eq, "conversation_id", Any), new WhereNode.Compare(Comparison.Lte, "id", Any)]), descending);
        Tasks = new TableQueries(tables.Tasks);
        Submissions = new TableQueries(tables.Submissions);
    }

    internal TableQueries Conversations { get; }

    internal TableQueries Entries { get; }

    /// <summary>The id of a conversation's newest entry with a head marker.</summary>
    internal string LatestHeadAny { get; }

    /// <summary>The id of a conversation's newest entry with a head marker at or before an id (bound second).</summary>
    internal string LatestHeadAtOrBefore { get; }

    internal TableQueries Tasks { get; }

    internal TableQueries Submissions { get; }
}

/// <summary>SQLite values as the store's rows hold them.</summary>
internal static class StoreValues
{
    /// <summary>An integer column's value (a long, or a whole double the binding wrote).</summary>
    internal static long Integer(object? value) => value switch
    {
        long integer => integer,
        int small => small,
        double number when Math.Floor(number) == number => (long)number,
        _ => throw new InvalidOperationException($"Not an integer column value: {value ?? "null"}"),
    };
}
