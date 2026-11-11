// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/rows.ts
//
// Rows (slice 6a §5 "Values at write", §9): one compiled codec per table version between a plugin's rows and keys (a trust boundary,
// checked closed) and what the store binds and reads; json(...) cells serialized at staging (rule 8), never walked (R9), and migrated on
// access when read.
//
// The value model: a row is a map from column name to JSON value. A plugin hands a row or key as a JsonObject (or any
// IReadOnlyDictionary<string, JsonNode?>); an absent column is upstream's undefined, a JSON null a present null. The rows the codec returns
// (a put's record, a decoded row) are OrderedDictionary<string, JsonNode?> maps: a JsonNode has one parent, so a map can hold the
// plugin's json(...) value by reference, as upstream keeps it (the value is neither copied nor walked; same-host values are immutable by
// convention). A scalar cell is a JsonValue holding a string, a double (a JavaScript number) or a bool. A SQLite value (upstream
// SqliteValue) is null, a string, a double, a long or a byte[]: what the store binds (a number bound as a double, which a STRICT INTEGER
// column takes losslessly) and what Microsoft.Data.Sqlite reads (an INTEGER as a long).
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>A scalar cell as the store keeps it in rows, writes and operands: a well-formed string, a number (a double, as JavaScript
/// holds numbers) or a boolean (upstream <c>Scalar</c>).</summary>
public readonly struct Scalar : IEquatable<Scalar>
{
    private readonly object? value;

    private Scalar(object value) => this.value = value;

    /// <summary>A text scalar.</summary>
    public static implicit operator Scalar(string value) => new(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>A number scalar.</summary>
    public static implicit operator Scalar(double value) => new(value);

    /// <summary>A boolean scalar.</summary>
    public static implicit operator Scalar(bool value) => new(value);

    /// <summary>The value: a <see cref="string"/>, a <see cref="double"/> or a <see cref="bool"/>.</summary>
    public object Value => value ?? throw new InvalidOperationException("An uninitialized scalar.");

    /// <summary>The scalar as a JSON node: a fresh JsonValue of its string, double or bool.</summary>
    public JsonNode ToNode() => Value switch
    {
        string text => JsonValue.Create(text),
        double number => JsonValue.Create(number),
        _ => JsonValue.Create((bool)Value),
    };

    /// <summary>A scalar of a JSON primitive as <c>ChordJson.Primitive</c> reads it: a string, a double or a bool.</summary>
    internal static Scalar FromPrimitive(object primitive) => primitive switch
    {
        string text => text,
        double number => number,
        bool flag => flag,
        _ => throw new ArgumentException("Not a scalar: " + primitive),
    };

    /// <inheritdoc/>
    public bool Equals(Scalar other) => (value, other.value) switch
    {
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (double a, double b) => a.Equals(b),
        (bool a, bool b) => a == b,
        (null, null) => true,
        _ => false,
    };

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Scalar other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => value?.GetHashCode() ?? 0;

    /// <summary>Whether two scalars are the same value of the same type.</summary>
    public static bool operator ==(Scalar left, Scalar right) => left.Equals(right);

    /// <summary>Whether two scalars differ.</summary>
    public static bool operator !=(Scalar left, Scalar right) => !left.Equals(right);

    /// <summary>The scalar's JSON text, as <c>JSON.stringify</c> writes it.</summary>
    public override string ToString() => value switch
    {
        string text => JsonUtf16.Quote(text),
        double number => JsonNumber.Text(number),
        bool flag => flag ? "true" : "false",
        _ => "",
    };
}

/// <summary>A <c>put</c> ready for the step: its parameters in physical order (<c>RowCodec.Physical</c>), its record, each written
/// <c>json(...)</c> column's version, and the row's key identity (upstream <c>EncodedRow</c>, which <c>next/index.ts</c> exports for
/// adapters).</summary>
/// <param name="Params">The parameters, in physical order.</param>
/// <param name="Row">The row as the outbox records it: the stored values, <c>Opt(...)</c> columns absent when <c>NULL</c>.</param>
/// <param name="Columns">Each written <c>json(...)</c> column's version.</param>
/// <param name="Id">The row's key identity, as <c>EncodedKey.Id</c>.</param>
public sealed record EncodedRow(IReadOnlyList<object?> Params, IReadOnlyDictionary<string, JsonNode?> Row, IReadOnlyDictionary<string, long> Columns, string Id);

/// <summary>A key ready for the store: its parameters in key order, its record and its identity within one table (upstream
/// <c>EncodedKey</c>); <see cref="Id"/> is <c>JSON.stringify</c> of the parameters: equal keys, equal ids.</summary>
internal sealed record EncodedKey(IReadOnlyList<object?> Params, IReadOnlyDictionary<string, Scalar> Key, string Id);

/// <summary>A checked cell: what is bound and what is recorded (upstream <c>Cell</c>).</summary>
internal readonly record struct Cell(object? Param, JsonNode? Stored);

internal static partial class StoreInternal
{
    /// <summary>The physical name of a <c>json(...)</c> column's version column (upstream <c>versionColumn</c>).</summary>
    internal static string VersionColumn(string column) => column + "$v";

    /// <summary>The row's eager-step version column (W11): set by <c>put</c> and eager migration steps only (upstream
    /// <c>ROW_VERSION</c>).</summary>
    internal const string RowVersion = "$v";

    /// <summary>The physical column order of a stored row: the declared columns, each <c>json(...)</c> column's version, then <c>$v</c>
    /// (upstream <c>physicalColumns</c>).</summary>
    internal static List<string> PhysicalColumns(IEnumerable<(string Name, ColumnType Type)> columns)
    {
        var list = columns.ToList();
        return [.. list.Select(column => column.Name), .. list.Where(column => column.Type == ColumnType.Json).Select(column => VersionColumn(column.Name)), RowVersion];
    }

    /// <summary>A scalar as SQLite binds it: booleans as 0 and 1, numbers as doubles (upstream <c>toSqlite</c>).</summary>
    internal static object ToSqlite(Scalar scalar) => scalar.Value switch
    {
        bool flag => flag ? 1d : 0d,
        var other => other,
    };

    /// <summary>Checks and encodes a value of <paramref name="column"/>; a <c>json(...)</c> value is serialized, not walked (R9; upstream
    /// <c>encodeCell</c>).</summary>
    internal static Cell EncodeCell(Column column, JsonNode? value, string what)
    {
        if (column.Host is { } host)
        {
            var key = host.Key(value, what);
            return new Cell(key, JsonValue.Create(key));
        }
        if (column.Type != ColumnType.Json)
        {
            var scalar = CheckScalar(column.Type, value, what);
            return new Cell(ToSqlite(scalar), scalar.ToNode());
        }
        string text;
        try
        {
            text = ChordJson.Stringify(value);
        }
        catch (Exception error)
        {
            throw DataInternal.Invalid("args", $"{what}: not JSON ({error.Message})");
        }
        // Stringify accepted it; the recorded value is the one serialized. It is kept, not copied or parsed back: same-host values are
        // immutable by convention, so the outbox serializing it again in the step records this text (a plugin mutating a value after
        // tx.put breaks that convention, as for any other value it hands over).
        return new Cell(text, value);
    }

    /// <summary>A value of a scalar column type as the store keeps it (a trust boundary): <c>str</c> well-formed without NUL, <c>int</c> a
    /// safe integer, <c>num</c> finite, <c>bool</c> a boolean; <c>invalid/args</c> otherwise (upstream <c>checkScalar</c>).</summary>
    internal static Scalar CheckScalar(ColumnType type, JsonNode? value, string what)
    {
        var primitive = value is JsonValue node && ChordJson.Primitive(node, out var held) ? held : null;
        switch (type)
        {
            case ColumnType.Str when primitive is string text:
                return StoredText(text, what);
            case ColumnType.Int when primitive is double number && IsSafeInteger(number):
                return number;
            case ColumnType.Num when primitive is double number && double.IsFinite(number):
                return number;
            case ColumnType.Bool when primitive is bool flag:
                return flag;
        }
        throw DataInternal.Invalid("args", $"{what}: {Stringified(value)} is not a {Name(type)} value");
    }

    /// <summary><c>JSON.stringify</c> of SQLite values (a key's identity, a cursor, an <c>inArray</c> list): <c>null</c>, strings, and
    /// numbers as JavaScript writes them.</summary>
    internal static string SqliteJson(IEnumerable<object?> values)
    {
        var text = new StringBuilder("[");
        var first = true;
        foreach (var value in values)
        {
            if (!first) text.Append(',');
            first = false;
            AppendSqliteJson(text, value);
        }
        return text.Append(']').ToString();
    }

    private static void AppendSqliteJson(StringBuilder text, object? value)
    {
        switch (value)
        {
            case null: text.Append("null"); break;
            case string inner: JsonUtf16.Quote(text, inner); break;
            case double inner: text.Append(JsonNumber.Text(inner)); break;
            case long inner: text.Append(inner.ToString(CultureInfo.InvariantCulture)); break;
            case int inner: text.Append(inner.ToString(CultureInfo.InvariantCulture)); break;
            default: throw new ArgumentException("Not a SQLite value of the store: " + value.GetType().Name);
        }
    }

    /// <summary>A number as SQLite returned it (a long or a double) as a double.</summary>
    internal static double SqliteNumber(object value) => value switch
    {
        long integer => integer,
        int integer => integer,
        double number => number,
        _ => throw new InvalidCastException("Not a SQLite number: " + value.GetType().Name),
    };
}

/// <summary>A table version's codec: <c>put</c> rows and keys checked and encoded, stored rows decoded (upstream <c>RowCodec</c>).
/// <see cref="Physical"/> is the column order of an encoded row (<c>PhysicalColumns</c>).</summary>
internal sealed class RowCodec
{
    private readonly IReadOnlyList<Column> columns;
    private readonly IReadOnlyList<Column> key;
    private readonly IReadOnlyList<Column> json;
    private readonly HashSet<string> names;
    private readonly HashSet<string> keyNames;
    /// <summary>The positions of the key columns in <see cref="Physical"/>.</summary>
    private readonly int[] keyPositions;
    private readonly string table;

    internal RowCodec(IReadOnlyList<Column> columns, IReadOnlyList<string> key, string table)
    {
        this.columns = columns;
        this.key = [.. key.Select(name => columns.First(column => column.Name == name))];
        json = [.. columns.Where(column => column.Type == ColumnType.Json)];
        names = new HashSet<string>(columns.Select(column => column.Name), StringComparer.Ordinal);
        keyNames = new HashSet<string>(key, StringComparer.Ordinal);
        keyPositions = [.. key.Select(name => columns.ToList().FindIndex(column => column.Name == name))];
        this.table = table;
        Physical = StoreInternal.PhysicalColumns(columns.Select(column => (column.Name, column.Type)));
    }

    /// <summary>The column order of an encoded row.</summary>
    internal IReadOnlyList<string> Physical { get; }

    /// <summary>Checks and encodes a <c>put</c> row of table version <paramref name="version"/> (a trust boundary): a plain object with
    /// every non-<c>Opt</c> column (a defaulted one filled in) and no other field; <c>invalid/args</c> otherwise.</summary>
    internal EncodedRow Put(object? row, long version)
    {
        var what = $"{table}: put";
        var fields = Closed(row, names, what);
        var parameters = new List<object?>(Physical.Count);
        var stored = new OrderedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var versions = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var present = fields.TryGetValue(column.Name, out var value);
            if (!present && column.Type != ColumnType.Json && column.Default is { } fallback)
            {
                present = true;
                value = fallback.ToNode();
            }
            if (!present)
            {
                if (!column.Optional) throw DataInternal.Invalid("args", $"{what}: column \"{column.Name}\" is missing");
                parameters.Add(null);
                continue;
            }
            var cell = StoreInternal.EncodeCell(column, value, $"{what}: column \"{column.Name}\"");
            parameters.Add(cell.Param);
            stored[column.Name] = cell.Stored;
        }
        foreach (var column in json)
        {
            var present = stored.ContainsKey(column.Name);
            if (present) versions[column.Name] = column.Json!.Version;
            parameters.Add(present ? column.Json!.Version : null);
        }
        parameters.Add(version);
        var id = StoreInternal.SqliteJson(keyPositions.Select(position => parameters[position]));
        return new EncodedRow(parameters, stored, versions, id);
    }

    /// <summary>Checks and encodes a key (a trust boundary): an object with exactly the key columns; <c>invalid/args</c> otherwise.</summary>
    internal EncodedKey Key(object? value, string what)
    {
        var fields = Closed(value, keyNames, $"{table}: {what}: the key");
        var parameters = new List<object?>(key.Count);
        var stored = new OrderedDictionary<string, Scalar>(StringComparer.Ordinal);
        foreach (var column in key)
        {
            var where = $"{table}: {what}: key column \"{column.Name}\"";
            if (!fields.TryGetValue(column.Name, out var field)) throw DataInternal.Invalid("args", $"{where} is missing");
            if (column.Type == ColumnType.Json) throw DataInternal.Invalid("args", $"{where}: a json(...) column has no scalar value");
            var scalar = column.Host is { } host ? (Scalar)host.Key(field, where) : StoreInternal.CheckScalar(column.Type, field, where);
            parameters.Add(StoreInternal.ToSqlite(scalar));
            stored[column.Name] = scalar;
        }
        return new EncodedKey(parameters, stored, StoreInternal.SqliteJson(parameters));
    }

    /// <summary>A stored row as reads return it: booleans from 0 and 1, <c>Opt(...)</c> columns absent when <c>NULL</c>, <c>json(...)</c>
    /// cells parsed and migrated on access (a cell the code cannot read throws, see <see cref="JsonColumn.Read"/>).</summary>
    internal OrderedDictionary<string, JsonNode?> Decode(IReadOnlyDictionary<string, object?> stored, ChordContext context)
    {
        var row = new OrderedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (!stored.TryGetValue(column.Name, out var value) || value is null) continue;
            switch (column.Type)
            {
                case ColumnType.Bool:
                    row[column.Name] = value is not string && StoreInternal.SqliteNumber(value) == 1;
                    break;
                case ColumnType.Json:
                    var version = (long)StoreInternal.SqliteNumber(stored[StoreInternal.VersionColumn(column.Name)]!);
                    row[column.Name] = column.Json!.Read((string)value, version, $"{table}.{column.Name}", context);
                    break;
                default:
                    row[column.Name] = column.Host is { } host ? host.Address((string)value)
                        : value is string text ? JsonValue.Create(text) : JsonValue.Create(StoreInternal.SqliteNumber(value));
                    break;
            }
        }
        return row;
    }

    /// <summary>A decoded row with its host address converted back to the key used by expression evaluation.</summary>
    internal IReadOnlyDictionary<string, JsonNode?> Stored(IReadOnlyDictionary<string, JsonNode?> row)
    {
        var host = columns.FirstOrDefault(column => column.Host is not null);
        if (host is null || !row.TryGetValue(host.Name, out var value) || ChordJson.AsString(value) is not null) return row;
        return new OrderedDictionary<string, JsonNode?>(row, StringComparer.Ordinal) { [host.Name] = host.Host!.Key(value, table) };
    }

    /// <summary>A stored row with its host key decoded to an address, for the transaction's read view.</summary>
    internal IReadOnlyDictionary<string, JsonNode?> Read(IReadOnlyDictionary<string, JsonNode?> row)
    {
        var host = columns.FirstOrDefault(column => column.Host is not null);
        if (host is null || !row.TryGetValue(host.Name, out var value) || ChordJson.AsString(value) is not { } key) return row;
        return new OrderedDictionary<string, JsonNode?>(row, StringComparer.Ordinal) { [host.Name] = host.Host!.Address(key) };
    }

    /// <summary><paramref name="value"/>'s own fields when it is a plain object with only fields of <paramref name="allowed"/>;
    /// <c>invalid/args</c> otherwise.</summary>
    private static IReadOnlyDictionary<string, JsonNode?> Closed(object? value, HashSet<string> allowed, string what)
    {
        IEnumerable<KeyValuePair<string, JsonNode?>> entries;
        if (value is IReadOnlyDictionary<string, JsonNode?> map) entries = map;
        else if (value is JsonNode node && ChordJson.AsObject(node) is { } json) entries = ChordJson.KeysInJavaScriptOrder(json).Select(name => KeyValuePair.Create(name, json[name]));
        else throw DataInternal.Invalid("args", $"{what}: expected an object");
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (field, item) in entries)
        {
            if (!allowed.Contains(field)) throw DataInternal.Invalid("args", $"{what}: unknown column \"{field}\"");
            fields[field] = item;
        }
        return fields;
    }
}
