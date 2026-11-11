// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/table.ts
//
// Table definitions (slice 6a §5, §9; decisions "Flat columns", "Restricted names", "Index definitions are recorded"):
// Tables.DefineTable(name, { Version, Columns, Key, Indexes }), checked and materialized once at declaration: its columns, its key, its
// indexes, its fingerprint and the compiled row codec its writes and reads use. A version Tables.EvolveTable appends links to the one
// before it and to the migration between them (Evolution; TableEvolve.cs, TableHistory.cs). Area cv2-store-sql added this file without the
// evolution, because the query layer compiles over a table definition; area cv2-store-tables, which owns table.ts, added the evolution and
// ports table.test.ts (TableCases.cs). Upstream's TableDefinition<C, K, V> and KeyOf<C, K> carry TypeScript types of the columns, key and
// version; the C# class is untyped, and a key is an object of the key columns (Rows.cs).
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>What <see cref="Tables.DefineTable"/> takes (upstream <c>TableOptions</c>).</summary>
public sealed class TableOptions
{
    /// <summary>The table's version: a positive integer, raised by every change.</summary>
    public required long Version { get; init; }

    /// <summary>The columns in declaration order: <c>Str()</c>, <c>Int()</c>, <c>Num()</c>, <c>Bool()</c>, <c>Opt(...)</c> of them, and
    /// <see cref="Tables.Json"/> columns, with defaults.</summary>
    public OrderedDictionary<string, Property> Columns { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The primary key's columns, non-<c>Opt</c> <c>str</c> or <c>int</c> ones.</summary>
    public required IReadOnlyList<string> Key { get; init; }

    /// <summary>Secondary indexes by name (<c>[a-z0-9_]+</c>); each ends with the primary key.</summary>
    public OrderedDictionary<string, IReadOnlyList<string>> Indexes { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>How a version came about: the version before it and the migration from there, for one <c>EvolveTable</c> (upstream
/// <c>Evolution</c>).</summary>
/// <param name="Previous">The version before.</param>
/// <param name="Migration">The migration from <paramref name="Previous"/>.</param>
internal sealed record Evolution(TableDefinition Previous, Migration Migration);

public static partial class Tables
{
    /// <summary>Declares a table: a base version written in full, the first of its history; later versions come from
    /// <see cref="EvolveTable"/>, and a new base starts a new history (upstream <c>defineTable</c>).</summary>
    public static TableDefinition DefineTable(string name, TableOptions options) => new(name, options);
}

/// <summary>A declared table at one version: its columns in declaration order, key, indexes (declared columns; the key is appended when
/// they are rendered), the fingerprint of all of it and the row codec (upstream <c>TableDefinition</c>).</summary>
public sealed class TableDefinition
{
    internal TableDefinition(string name, TableOptions options, Evolution? evolution = null)
    {
        ContractsInternal.CheckName(name, "table");
        ArgumentNullException.ThrowIfNull(options);
        var version = options.Version;
        // Declaration policy: callers reach these checks.
        if (version < 1 || version > StoreInternal.MaxSafeInteger) throw new ArgumentException($"{name}: version {version} is not a positive integer");
        Name = name;
        Version = version;
        ColumnSchemas = new OrderedDictionary<string, Property>(options.Columns ?? new OrderedDictionary<string, Property>(), StringComparer.Ordinal);
        Columns = StoreInternal.DescribeColumns(ColumnSchemas, name);
        ByName = Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        Key = StoreInternal.OrderColumns(ByName, options.Key, $"{name}: the key");
        if (Key.Count == 0) throw new ArgumentException($"{name}: the key has no column");
        var indexes = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var declared = options.Indexes ?? new OrderedDictionary<string, IReadOnlyList<string>>();
        foreach (var index in StoreInternal.JavaScriptOrder(declared.Keys))
        {
            if (!StoreInternal.IndexName.IsMatch(index)) throw new ArgumentException($"{name}: index \"{index}\": expected [a-z0-9_]+");
            indexes[index] = StoreInternal.OrderColumns(ByName, declared[index], $"{name}: index \"{index}\"");
        }
        HostColumn = StoreInternal.HostColumnOf(Columns, Key, indexes, name);
        Indexes = indexes;
        var record = new OrderedDictionary<string, ColumnRecord>(StringComparer.Ordinal);
        foreach (var column in Columns) record[column.Name] = StoreInternal.RecordOf(column);
        Record = record;
        Fingerprint = StoreInternal.TableFingerprint(Record, Key, Indexes);
        Rows = new RowCodec(Columns, Key, name);
        Refs = StoreInternal.ColumnRefs(Columns);
        Evolution = evolution;
    }

    /// <summary>The table's name.</summary>
    public string Name { get; }

    /// <summary>The table's version.</summary>
    public long Version { get; }

    /// <summary>The column schemas as declared, which <c>EvolveTable</c> edits into the next version's.</summary>
    internal IReadOnlyDictionary<string, Property> ColumnSchemas { get; }

    /// <summary>The columns in declaration order.</summary>
    internal IReadOnlyList<Column> Columns { get; }

    /// <summary>The one host address column, when this table holds host-scoped rows.</summary>
    internal Column? HostColumn { get; }

    /// <summary>The columns by name.</summary>
    internal IReadOnlyDictionary<string, Column> ByName { get; }

    /// <summary>The primary key's columns.</summary>
    public IReadOnlyList<string> Key { get; }

    /// <summary>The secondary indexes by name: their declared columns.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Indexes { get; }

    /// <summary><c>Hash64</c> of the columns, key and indexes in <c>TableFormVersion</c>'s form.</summary>
    public string Fingerprint { get; }

    /// <summary>The columns as the <c>table</c> write records them.</summary>
    internal IReadOnlyDictionary<string, ColumnRecord> Record { get; }

    /// <summary>The row codec of this version.</summary>
    internal RowCodec Rows { get; }

    /// <summary>One reference per column, for the <c>t =&gt; ...</c> callbacks of queries and writes.</summary>
    public IReadOnlyDictionary<string, ColumnRef> Refs { get; }

    /// <summary>The version before this one and the migration from it; null for a base.</summary>
    internal Evolution? Evolution { get; }

    /// <summary>The column <paramref name="name"/>; an unknown one throws (<paramref name="what"/> names the use).</summary>
    internal Column Column(string name, string what) =>
        ByName.TryGetValue(name, out var column) ? column : throw new ArgumentException($"{Name}: {what}: no column \"{name}\"");
}

internal static partial class StoreInternal
{
    /// <summary>The table form's version: the fingerprints of table definitions are persisted, so their input is versioned (upstream
    /// <c>TABLE_FORM_VERSION</c>).</summary>
    internal const int TableFormVersion = 1;

    /// <summary>The index name rule (upstream <c>INDEX_NAME</c>).</summary>
    internal static readonly Regex IndexName = new(@"^[a-z0-9_]+\z", RegexOptions.CultureInvariant);

    /// <summary>Checks <paramref name="names"/> as key, index or order columns of a table whose columns are <paramref name="byName"/>:
    /// existing, non-<c>Opt</c> <c>str</c> or <c>int</c>, no column twice (declaration policy: callers reach it); <paramref name="what"/>
    /// names the use in errors (upstream <c>orderColumns</c>).</summary>
    internal static IReadOnlyList<string> OrderColumns(IReadOnlyDictionary<string, Column> byName, IReadOnlyList<string>? names, string what)
    {
        if (names is null) throw new ArgumentException($"{what}: expected an array of column names");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw new ArgumentException($"{what}: a column occurs twice");
        foreach (var name in names)
        {
            if (name is null || !byName.TryGetValue(name, out var column)) throw new ArgumentException($"{what}: no column \"{name}\"");
            if (column.Optional || column.Type is not (ColumnType.Str or ColumnType.Int))
                throw new ArgumentException($"{what}: column \"{name}\" is not a non-opt str() or int() column");
        }
        return [.. names];
    }

    /// <summary>The fingerprint of a table's columns, key and indexes; column and index names sorted (their order is no change; upstream
    /// <c>tableFingerprint</c>). The text is JavaScript's <c>JSON.stringify</c>, so the fingerprint is the one upstream computes.</summary>
    internal static string TableFingerprint(IReadOnlyDictionary<string, ColumnRecord> columns, IReadOnlyList<string> key,
        IReadOnlyDictionary<string, IReadOnlyList<string>> indexes)
    {
        var sortedColumns = new JsonObject();
        foreach (var (name, column) in columns.OrderBy(entry => entry.Key, StringComparer.Ordinal)) sortedColumns[name] = column.ToJson();
        var sortedIndexes = new JsonObject();
        foreach (var (name, names) in indexes.OrderBy(entry => entry.Key, StringComparer.Ordinal)) sortedIndexes[name] = Strings(names);
        var value = new JsonObject { ["columns"] = sortedColumns, ["key"] = Strings(key), ["indexes"] = sortedIndexes };
        return ContractsInternal.Hash64(ChordJson.Stringify(new JsonObject { ["tableFormVersion"] = TableFormVersion, ["value"] = value }));
    }

    /// <summary>A JSON array of strings.</summary>
    internal static JsonArray Strings(IEnumerable<string> names) => new([.. names.Select(name => (JsonNode?)JsonValue.Create(name))]);

    /// <summary>Property names in JavaScript's own-key order, as <c>Object.entries</c> gives them: array indexes ascending, then the rest in
    /// insertion order.</summary>
    internal static IEnumerable<string> JavaScriptOrder(IEnumerable<string> names)
    {
        var list = names.ToList();
        static bool IsIndex(string key, out uint index) =>
            uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index != uint.MaxValue &&
            index.ToString(CultureInfo.InvariantCulture) == key;
        var indexes = list.Select(name => (Name: name, IsIndex: IsIndex(name, out var index), Index: index)).Where(entry => entry.IsIndex)
            .OrderBy(entry => entry.Index).Select(entry => entry.Name);
        return [.. indexes, .. list.Where(name => !IsIndex(name, out _))];
    }
}
