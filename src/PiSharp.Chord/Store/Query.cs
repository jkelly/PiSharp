// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/query.ts
//
// Queries (decision "Query runner"; slice 6a §6, rule 17): Tables.Query(table).Where(...).OrderBy(...) as immutable data (the condition
// compiled when given, the order with the key appended as the tie-break), and the opaque cursors that continue a page, bound to the table,
// the order columns and the direction. Upstream's ColumnRefs<C> and Query<C, K> carry TypeScript types of the columns; a callback here
// gets the table's Refs, one ColumnRef per column by name.
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

public static partial class Tables
{
    /// <summary>A query of <paramref name="table"/>: its rows, in key order (upstream <c>query</c>).</summary>
    public static Query Query(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new(table, null, [], null);
    }
}

/// <summary>An immutable query of one table: an optional condition (several <see cref="Where"/>s are and-ed) and an order
/// (<see cref="OrderBy"/> columns, one direction, the key appended unless already there) (upstream <c>Query</c>).</summary>
public sealed class Query
{
    private readonly IReadOnlyList<string> orderBy;
    private readonly OrderDirection? direction;

    internal Query(TableDefinition table, WhereNode? condition, IReadOnlyList<string> orderBy, OrderDirection? direction)
    {
        Table = table;
        Condition = condition;
        this.orderBy = orderBy;
        this.direction = direction;
    }

    /// <summary>The table queried.</summary>
    public TableDefinition Table { get; }

    /// <summary>The compiled condition; null for every row.</summary>
    public WhereNode? Condition { get; }

    /// <summary>This query with <paramref name="fn"/>'s condition too.</summary>
    public Query Where(Func<IReadOnlyDictionary<string, ColumnRef>, Condition> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        var compiled = StoreInternal.CompileWhere(Table, fn(Table.Refs), "where");
        var condition = Condition is null ? compiled : new WhereNode.And([Condition, compiled]);
        return new Query(Table, condition, orderBy, direction);
    }

    /// <summary>This query ordered by <paramref name="fn"/>'s column next (a non-<c>Opt</c> <c>str</c> or <c>int</c> one), in
    /// <paramref name="direction"/>: one direction per query, no column twice (<c>invalid/args</c>).</summary>
    public Query OrderBy(Func<IReadOnlyDictionary<string, ColumnRef>, ColumnRef?> fn, OrderDirection direction = OrderDirection.Asc)
    {
        ArgumentNullException.ThrowIfNull(fn);
        // What the plugin's callback returns is checked here.
        var reference = fn(Table.Refs);
        if (!Table.ByName.TryGetValue(reference?.Column ?? "", out var column) || column.Optional || column.Type is not (ColumnType.Str or ColumnType.Int))
            throw DataInternal.Invalid("args", $"{Table.Name}: orderBy: expected a non-opt str() or int() column");
        if (orderBy.Contains(column.Name)) throw DataInternal.Invalid("args", $"{Table.Name}: orderBy: \"{column.Name}\" twice");
        if (this.direction is { } current && current != direction) throw DataInternal.Invalid("args", $"{Table.Name}: orderBy: one direction per query");
        return new Query(Table, Condition, [.. orderBy, column.Name], direction);
    }

    /// <summary>The order of its rows: the <see cref="OrderBy"/> columns, then the key columns not among them.</summary>
    public Order Order => new([.. orderBy, .. Table.Key.Where(column => !orderBy.Contains(column))], direction ?? OrderDirection.Asc);
}

internal static partial class StoreInternal
{
    /// <summary>The cursor of the position after a row with the order values <paramref name="values"/> in the table named
    /// <paramref name="table"/> (its SQL name): base64url of <c>JSON.stringify({ t, o, d, v })</c> (upstream <c>encodeCursor</c>).</summary>
    internal static string EncodeCursor(string table, Order order, IReadOnlyList<object?> values)
    {
        var json = new StringBuilder("{\"t\":");
        JsonUtf16.Quote(json, table);
        json.Append(",\"o\":").Append(ChordJson.Stringify(Strings(order.Columns))).Append(",\"d\":");
        JsonUtf16.Quote(json, Name(order.Direction));
        json.Append(",\"v\":").Append(SqliteJson(values)).Append('}');
        // TextEncoder: a lone surrogate becomes U+FFFD, as .NET's UTF-8 encoder writes it.
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToString())).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The order values of <paramref name="cursor"/> (a trust boundary: an app hands it back): one made for
    /// <paramref name="table"/>'s <paramref name="order"/> (any condition) in the table named <paramref name="sqlName"/> (another object's
    /// table of the same definition is another table), each value of its column's type; <c>invalid/args</c> otherwise (upstream
    /// <c>decodeCursor</c>).</summary>
    internal static List<object?> DecodeCursor(TableDefinition table, string sqlName, Order order, object? cursor)
    {
        ChordError Refuse() => DataInternal.Invalid("args", $"{table.Name}: the cursor is not one of this table's order");
        if (cursor is not string text) throw Refuse();
        JsonNode? decoded;
        try
        {
            var json = StrictUtf8.GetString(Atob(text.Replace('-', '+').Replace('_', '/')));
            // TextDecoder drops a leading byte order mark.
            decoded = JsonUtf16.MutableNode(json.Length > 0 && json[0] == (char)0xFEFF ? json[1..] : json);
        }
        catch (Exception)
        {
            // Not base64, not UTF-8 or not JSON.
            throw Refuse();
        }
        // Wire input: every field is checked below.
        var fields = ChordJson.AsObject(decoded) ?? new JsonObject();
        var hasColumns = fields.TryGetPropertyValue("o", out var columns);
        if (ChordJson.AsString(fields["t"]) != sqlName || ChordJson.AsString(fields["d"]) != Name(order.Direction) || !hasColumns ||
            !ChordJson.IsJsonValue(columns) || ChordJson.Stringify(columns) != ChordJson.Stringify(Strings(order.Columns)))
            throw Refuse();
        if (ChordJson.AsArray(fields["v"]) is not { } values || values.Count != order.Columns.Count) throw Refuse();
        // Order columns are non-opt str or int (checked when the query was built).
        return [.. order.Columns.Select((name, index) => CheckScalar(table.ByName[name].Type, values[index], $"{table.Name}: cursor").Value)];
    }

    /// <summary><c>atob</c>'s forgiving base64: ASCII whitespace ignored, the padding optional.</summary>
    private static byte[] Atob(string text)
    {
        var data = new string([.. text.Where(c => c is not (' ' or '\t' or '\n' or '\f' or '\r'))]);
        if (data.Length % 4 == 0 && data.EndsWith('=')) data = data.EndsWith("==", StringComparison.Ordinal) ? data[..^2] : data[..^1];
        if (data.Length % 4 == 1) throw new FormatException("Not base64.");
        return Convert.FromBase64String(data + new string('=', (4 - data.Length % 4) % 4));
    }
}
