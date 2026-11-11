// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/columns.ts
//
// Table columns (decisions "Flat columns", "Nullable columns and the `set` form", "Restricted names"): the restricted subset of L1's helpers
// a table's `columns` take (Str, Int, Num, Bool, Opt(...) of them, Tables.Json(...), defaults), and the column descriptors and records the
// store keeps. A table's columns are an OrderedDictionary<string, Property> in declaration order: a schema (a JsonNode, as the contracts
// layer's helpers build it) or an Optional. Upstream's ColumnSchema, Columns, ColumnTypeOf, ValueOf, RowOf, PutRowOf and OrderColumn are
// TypeScript type-level mappings with no C# counterpart: a row is a map from column name to JSON value (Rows.cs), and what the types refuse
// is refused when the table is declared or the row is checked.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>A column's type in the store (upstream <c>ColumnType</c>: <c>str</c>, <c>int</c>, <c>num</c>, <c>bool</c>, <c>json</c>).</summary>
internal enum ColumnType { Str, Int, Num, Bool, Json }

/// <summary>A column of the store: its name, type, nullability and default; a <c>json(...)</c> one with its definition (upstream
/// <c>Column</c>, a union of the scalar and the <c>json(...)</c> shape: <see cref="Default"/> is only a scalar column's, <see cref="Json"/>
/// only a <c>json(...)</c> one's).</summary>
internal sealed record Column(string Name, ColumnType Type, bool Optional, Scalar? Default, JsonColumn? Json, HostColumn? Host = null);

/// <summary>A column as the <c>table</c> write records it (slice 6a §5): what a replica needs without the plugin's code; a <c>json(...)</c>
/// column's version and schema fingerprint (upstream <c>ColumnRecord</c>).</summary>
internal sealed record ColumnRecord(ColumnType Type, bool Optional = false, Scalar? Default = null, long? Version = null, string? Fingerprint = null, string? Host = null)
{
    /// <summary>The record as JSON, in upstream's key order: <c>{ type, optional?, default? }</c>, or <c>{ type: "json", optional?, version,
    /// fingerprint }</c>.</summary>
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = StoreInternal.Name(Type) };
        if (Optional) json["optional"] = true;
        if (Default is { } value) json["default"] = value.ToNode();
        if (Version is { } version) json["version"] = version;
        if (Fingerprint is not null) json["fingerprint"] = Fingerprint;
        if (Host is not null) json["host"] = Host;
        return json;
    }
}

internal static partial class StoreInternal
{
    /// <summary>The column name rule (decision "Restricted names"; upstream <c>COLUMN_NAME</c>). <c>\z</c>, not <c>$</c>: JavaScript's
    /// <c>$</c> matches only at the end.</summary>
    internal static readonly Regex ColumnName = new(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>A column type's name, as upstream spells it: <c>str</c>, <c>int</c>, <c>num</c>, <c>bool</c> or <c>json</c>.</summary>
    internal static string Name(ColumnType type) => type switch
    {
        ColumnType.Str => "str",
        ColumnType.Int => "int",
        ColumnType.Num => "num",
        ColumnType.Bool => "bool",
        _ => "json",
    };

    /// <summary>The columns of <paramref name="columns"/> in declaration order, checked (declaration policy: callers reach it): names
    /// <c>[A-Za-z_][A-Za-z0-9_]*</c> without two equal ignoring ASCII case, only the helpers' types, no bounds or patterns (a bulk
    /// <c>set</c> could not keep them), a default of the column's type and not on an <c>Opt(...)</c> column (upstream
    /// <c>describeColumns</c>).</summary>
    internal static List<Column> DescribeColumns(IEnumerable<KeyValuePair<string, Property>> columns, string table)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var described = new List<Column>();
        foreach (var (name, schema) in columns)
        {
            CheckColumnName(name, table);
            // The names are ASCII here, so ToLowerInvariant is JavaScript's toLowerCase.
            if (!seen.Add(name.ToLowerInvariant())) throw new ArgumentException($"{table}: column \"{name}\" differs from another only in case");
            described.Add(DescribeColumn(name, schema, $"{table}: column \"{name}\""));
        }
        return described;
    }

    /// <summary>Throws unless <paramref name="name"/> follows the column name rule (declaration policy); <paramref name="what"/> names the
    /// table or the edit (upstream <c>checkColumnName</c>).</summary>
    internal static void CheckColumnName(string name, string what)
    {
        if (name is null || !ColumnName.IsMatch(name)) throw new ArgumentException($"{what}: column \"{name}\": expected [A-Za-z_][A-Za-z0-9_]*");
    }

    private static readonly Dictionary<string, ColumnType> ScalarTypes = new(StringComparer.Ordinal)
    {
        ["string"] = ColumnType.Str,
        ["integer"] = ColumnType.Int,
        ["number"] = ColumnType.Num,
        ["boolean"] = ColumnType.Bool,
    };

    private static Column DescribeColumn(string name, Property column, string what)
    {
        var optional = ContractsInternal.IsOptional(column.Value);
        // Declaration policy: a caller may pass any schema, so its fields are read as unknown data.
        var fields = ChordJson.AsObject(optional ? ((Optional)column.Value!).Schema : column.Value as JsonNode);
        if (fields is not null && fields.TryGetPropertyValue("json", out var json) && json is JsonValue holder && holder.TryGetValue<JsonColumn>(out var definition))
            return new Column(name, ColumnType.Json, optional, null, definition);
        if (fields is not null && fields.TryGetPropertyValue("host", out var host) && host is JsonValue hostHolder && hostHolder.TryGetValue<HostColumn>(out var hostDefinition))
        {
            if (optional) throw new ArgumentException($"{what}: a host(...) column is not opt(...)");
            return new Column(name, ColumnType.Str, false, null, null, hostDefinition);
        }
        if (fields is null || ChordJson.AsString(fields["type"]) is not { } typeName || !ScalarTypes.TryGetValue(typeName, out var type))
            throw new ArgumentException($"{what}: expected str(), int(), num(), bool(), opt(...) or json(...)");
        foreach (var key in ChordJson.KeysInJavaScriptOrder(fields))
        {
            if (key is not ("type" or "default")) throw new ArgumentException($"{what}: \"{key}\" is not supported in a table column");
        }
        Scalar? @default = null;
        if (fields.TryGetPropertyValue("default", out var value))
        {
            if (optional) throw new ArgumentException($"{what}: an opt(...) column has no default");
            @default = CheckDefault(type, value, what);
        }
        return new Column(name, type, optional, @default, null);
    }

    private static Scalar CheckDefault(ColumnType type, JsonNode? value, string what)
    {
        var primitive = value is JsonValue node && ChordJson.Primitive(node, out var held) ? held : null;
        var valid = type switch
        {
            ColumnType.Str => primitive is string text && StoredText(text, what) == text,
            ColumnType.Int => primitive is double number && IsSafeInteger(number),
            ColumnType.Num => primitive is double number && double.IsFinite(number),
            _ => primitive is bool,
        };
        return valid ? Scalar.FromPrimitive(primitive!) : throw new ArgumentException($"{what}: the default {Stringified(value)} is not a {Name(type)} value");
    }

    /// <summary>The record of <paramref name="column"/> in the <c>table</c> write (upstream <c>columnRecord</c>).</summary>
    internal static ColumnRecord RecordOf(Column column) => column.Type == ColumnType.Json
        ? new ColumnRecord(ColumnType.Json, column.Optional, Version: column.Json!.Version, Fingerprint: column.Json.Fingerprint)
        : column.Host is { } host ? new ColumnRecord(ColumnType.Str, Host: host.Kind.Kind)
        : new ColumnRecord(column.Type, column.Optional, column.Default);

    /// <summary><c>Number.isSafeInteger</c>: an integer within ±(2^53 - 1).</summary>
    internal static bool IsSafeInteger(double value) => double.IsInteger(value) && Math.Abs(value) <= MaxSafeInteger;

    /// <summary><c>Number.MAX_SAFE_INTEGER</c>.</summary>
    internal const double MaxSafeInteger = 9007199254740991;

    /// <summary><c>JSON.stringify(value) ?? String(value)</c> of a value in a message; a value that is no JSON data (a JsonValue wrapping
    /// another CLR value, whose JSON.stringify would throw or give undefined) is its <c>ToString()</c>.</summary>
    internal static string Stringified(JsonNode? value)
    {
        if (ChordJson.IsJsonValue(value)) return ChordJson.Stringify(value);
        // JSON.stringify writes NaN and the infinities as null.
        if (value is JsonValue number && ChordJson.Primitive(number, out var primitive) && primitive is double) return "null";
        return value is JsonValue node && node.TryGetValue<object>(out var held) ? held.ToString() ?? "" : value?.GetType().Name ?? "null";
    }
}
