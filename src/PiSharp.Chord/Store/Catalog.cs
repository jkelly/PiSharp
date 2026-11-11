// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/catalog.ts
//
// The catalog (slice 6a §2, §5): the table definitions the store records (version, state, columns, key, fingerprint, indexes), read once at
// open, changed only by table, index, dropIndex and alter writes inside a step, and in memory again only after COMMIT returned. A record is
// stored as its JSON text (JSON.stringify, ChordJson.Stringify here) in chord_tables, and parsed back as the store wrote it (upstream casts
// JSON.parse's result; the record is the store's own).
using System.Text.Json.Nodes;
using PiSharp.Chord.Sqlite;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>A recorded table's state: <c>ready</c>, or <c>migrating</c> while an eager migration of its version runs (slice 6a §9).</summary>
internal enum TableState { Ready, Migrating }

/// <summary>A table as the store records it, by its SQL name (upstream <c>TableRecord</c>). Its maps keep upstream's order: the columns in
/// declaration order, the indexes as their writes left them.</summary>
/// <param name="Table">The table's SQL name.</param>
/// <param name="Version">The recorded version.</param>
/// <param name="State">Ready, or migrating while an eager migration of <paramref name="Version"/> runs.</param>
/// <param name="Columns">The columns as the <c>table</c> write records them.</param>
/// <param name="Key">The primary key's columns.</param>
/// <param name="Fingerprint">The fingerprint of <paramref name="Version"/>'s definition.</param>
/// <param name="Indexes">Index definitions by name: their declared columns (the key is appended when rendered).</param>
internal sealed record TableRecord(
    string Table,
    long Version,
    TableState State,
    IReadOnlyDictionary<string, ColumnRecord> Columns,
    IReadOnlyList<string> Key,
    string Fingerprint,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Indexes)
{
    /// <summary>The record in upstream's data form: <c>{ table, version, state, columns, key, fingerprint, indexes }</c>.</summary>
    internal JsonObject ToJson() => new()
    {
        ["table"] = Table,
        ["version"] = Version,
        ["state"] = StoreInternal.Name(State),
        ["columns"] = StoreInternal.ColumnsJson(Columns),
        ["key"] = StoreInternal.Strings(Key),
        ["fingerprint"] = Fingerprint,
        ["indexes"] = StoreInternal.IndexesJson(Indexes),
    };
}

internal static partial class StoreInternal
{
    /// <summary>A table state's name: <c>ready</c> or <c>migrating</c>.</summary>
    internal static string Name(TableState state) => state == TableState.Ready ? "ready" : "migrating";

    /// <summary>Columns as records in JSON, in their order.</summary>
    internal static JsonObject ColumnsJson(IReadOnlyDictionary<string, ColumnRecord> columns)
    {
        var json = new JsonObject();
        foreach (var (name, column) in columns) json[name] = column.ToJson();
        return json;
    }

    /// <summary>Index definitions in JSON, in their order (JSON.stringify puts array-index names first).</summary>
    internal static JsonObject IndexesJson(IReadOnlyDictionary<string, IReadOnlyList<string>> indexes)
    {
        var json = new JsonObject();
        foreach (var (name, columns) in indexes) json[name] = Strings(columns);
        return json;
    }

    /// <summary>Every recorded table, by SQL name (upstream <c>readCatalog</c>).</summary>
    internal static async Task<OrderedDictionary<string, TableRecord>> ReadCatalogAsync(ISqliteExecutor executor)
    {
        var rows = await executor.AllAsync($"SELECT name, definition FROM {StoreTables.Tables}").ConfigureAwait(false);
        var catalog = new OrderedDictionary<string, TableRecord>(StringComparer.Ordinal);
        foreach (var row in rows) catalog[(string)row["name"]!] = ParseTableRecord(JsonUtf16.MutableNode((string)row["definition"]!)!.AsObject());
        return catalog;
    }

    /// <summary>Writes the table records <paramref name="step"/> changed, once each, inside its backend transaction (upstream
    /// <c>writeCatalog</c>).</summary>
    internal static async Task WriteCatalogAsync(Step step)
    {
        foreach (var (name, record) in step.Tables)
        {
            var definition = ChordJson.Stringify(record.ToJson());
            await step.RunAsync($"INSERT OR REPLACE INTO {StoreTables.Tables} (name, definition) VALUES (?, ?)", name, definition).ConfigureAwait(false);
        }
    }

    /// <summary>A record as <see cref="TableRecord.ToJson"/> wrote it (the store's own data, as upstream's cast of <c>JSON.parse</c>
    /// reads it): its maps in JavaScript's own-key order.</summary>
    internal static TableRecord ParseTableRecord(JsonObject json)
    {
        var columns = new OrderedDictionary<string, ColumnRecord>(StringComparer.Ordinal);
        var columnsJson = json["columns"]!.AsObject();
        foreach (var name in ChordJson.KeysInJavaScriptOrder(columnsJson)) columns[name] = ParseColumnRecord(columnsJson[name]!.AsObject());
        var indexes = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var indexesJson = json["indexes"]!.AsObject();
        foreach (var name in ChordJson.KeysInJavaScriptOrder(indexesJson)) indexes[name] = Names(indexesJson[name]);
        return new TableRecord(
            (string)json["table"]!,
            (long)ChordJson.AsNumber(json["version"])!.Value,
            (string)json["state"]! == "ready" ? TableState.Ready : TableState.Migrating,
            columns,
            Names(json["key"]),
            (string)json["fingerprint"]!,
            indexes);
    }

    /// <summary>A column record as <see cref="ColumnRecord.ToJson"/> wrote it.</summary>
    internal static ColumnRecord ParseColumnRecord(JsonObject json)
    {
        var type = (string)json["type"]! switch
        {
            "str" => ColumnType.Str,
            "int" => ColumnType.Int,
            "num" => ColumnType.Num,
            "bool" => ColumnType.Bool,
            _ => ColumnType.Json,
        };
        Scalar? @default = json["default"] is JsonValue value && ChordJson.Primitive(value, out var primitive) && primitive is not null
            ? (Scalar?)Scalar.FromPrimitive(primitive)
            : null;
        return new ColumnRecord(type, ChordJson.IsBoolean(json["optional"], true), @default,
            ChordJson.AsNumber(json["version"]) is { } version ? (long)version : null, ChordJson.AsString(json["fingerprint"]), ChordJson.AsString(json["host"]));
    }

    /// <summary>A JSON array of names as a list.</summary>
    private static List<string> Names(JsonNode? node) => [.. node!.AsArray().Select(name => (string)name!)];
}
