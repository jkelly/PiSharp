// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/chord/src/store/query.ts (v1.7.0 updates).
// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/query.ts
//
// Queries (decision "Query runner"; slice 6a §6, rule 17; slice 14 §5, §6): Tables.Query(table).Where(...).OrderBy(...) as immutable data
// (the condition compiled when given, the order with the key appended as the tie-break), optionally joined with one row of another plugin's
// column set, and the opaque cursors that continue a page, bound to the table, the order columns and the direction. Upstream's
// ColumnRefs<C> and Query<C, K, J> carry TypeScript types of the columns; a callback here gets the table's Refs, one ColumnRef per column
// by name, and (the two-argument forms) the joined set's, which are empty without a join.
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
        return new(table, null, [], null, null, StoreInternal.NothingExpected);
    }

    /// <summary>A condition over <paramref name="set"/>'s columns as plain data (slice 14 §6), for <see cref="Store.Query.WhereNode(ColumnSet, WhereNode)"/>
    /// and <see cref="Expressions.ExistsNode(ColumnSet, IReadOnlyDictionary{string, ColumnRef}, WhereNode)"/>; its <c>ToJson()</c> is the
    /// form that travels as an argument (upstream <c>compile</c>).</summary>
    public static WhereNode Compile(ColumnSet set, Condition condition)
    {
        ArgumentNullException.ThrowIfNull(set);
        return StoreInternal.CompileCondition(StoreInternal.SetScope(set), condition, "compile");
    }
}

/// <summary>The set a query joins: one row of it per own row, by the set's whole key (upstream <c>Joined</c>).</summary>
/// <param name="Set">The joined set.</param>
/// <param name="Pairs">The set's key columns and the own columns each is matched to, in key order.</param>
internal sealed record Joined(ColumnSet Set, IReadOnlyList<ExistsPair> Pairs);

/// <summary>An immutable query of one table: an optional condition (several <see cref="Where(Func{IReadOnlyDictionary{string, ColumnRef}, Condition})"/>s
/// are and-ed), an order (<c>OrderBy</c> columns, one direction, the key appended unless already there) and at most one joined set, whose
/// columns the condition and the order may use too (upstream <c>Query</c>).</summary>
public sealed class Query
{
    private static readonly IReadOnlyDictionary<string, ColumnRef> NoRefs = new Dictionary<string, ColumnRef>(StringComparer.Ordinal);

    private readonly IReadOnlyList<string> orderBy;
    private readonly OrderDirection? direction;

    internal Query(TableDefinition table, WhereNode? condition, IReadOnlyList<string> orderBy, OrderDirection? direction, Joined? joined,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, Column>> expected)
    {
        Table = table;
        Condition = condition;
        Joined = joined;
        Expected = expected;
        this.orderBy = orderBy;
        this.direction = direction;
    }

    /// <summary>The table queried.</summary>
    public TableDefinition Table { get; }

    /// <summary>The compiled condition; null for every row.</summary>
    public WhereNode? Condition { get; }

    /// <summary>The set the query joins; null without a join.</summary>
    internal Joined? Joined { get; }

    /// <summary>The column set the query joins; null without a join.</summary>
    public ColumnSet? JoinedSet => Joined?.Set;

    /// <summary>The set columns its <c>exists</c> were written for, by set name: checked when a read of it opens (upstream <c>expected</c>).</summary>
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, Column>> Expected { get; }

    /// <summary>This query with <paramref name="fn"/>'s condition too.</summary>
    public Query Where(Func<IReadOnlyDictionary<string, ColumnRef>, Condition> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return Where((row, _) => fn(row));
    }

    /// <summary>This query with <paramref name="fn"/>'s condition too; its second argument are the joined set's columns.</summary>
    public Query Where(Func<IReadOnlyDictionary<string, ColumnRef>, IReadOnlyDictionary<string, ColumnRef>, Condition> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        // A copy of what is expected so far: the compilation adds what this condition's exists use.
        var expected = Expected.ToDictionary(entry => entry.Key, entry => new Dictionary<string, Column>(entry.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var compiled = StoreInternal.CompileCondition(Scope(), fn(Table.Refs, Joined?.Set.Refs ?? NoRefs), "where", expected);
        return And(compiled, expected.ToDictionary(entry => entry.Key, entry => (IReadOnlyDictionary<string, Column>)entry.Value, StringComparer.Ordinal));
    }

    /// <summary>This query with a condition handed over as data (the JSON of <see cref="Tables.Compile"/>'s node, slice 14 §6): over the
    /// own table when it exposes <paramref name="set"/>, over the partner when <paramref name="set"/> is joined. A trust boundary:
    /// <paramref name="node"/> is whatever arrived as an argument, and the whole of it is checked first (upstream <c>whereNode</c>).</summary>
    public Query WhereNode(ColumnSet set, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(set);
        var what = $"{Table.Name}: whereNode({set.Name})";
        var read = StoreInternal.CheckNode(node, set, what);
        if (Joined?.Set.Name == set.Name) return And(StoreInternal.Renamed(read, column => StoreInternal.PartnerName(set.Name, column)));
        if (Table.Exposes?.Name != set.Name) throw DataInternal.Invalid("args", $"{what}: the query has no side of that set");
        return And(read);
    }

    /// <summary><see cref="WhereNode(ColumnSet, JsonNode?)"/> for a compiled node: checked as its JSON form is.</summary>
    public Query WhereNode(ColumnSet set, WhereNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return WhereNode(set, node.ToJson());
    }

    /// <summary>This query joined with the one row of <paramref name="set"/> whose key equals <paramref name="match"/>'s own columns: an
    /// inner join driven from the own table. The match covers the set's whole key; one set per query, and not the one the table exposes
    /// (upstream <c>join</c>).</summary>
    public Query Join(ColumnSet set, Func<IReadOnlyDictionary<string, ColumnRef>, IReadOnlyDictionary<string, ColumnRef>> match)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(match);
        var table = Table;
        var what = $"{table.Name}: join({set.Name})";
        if (Joined is not null) throw DataInternal.Invalid("args", $"{what}: a query joins one set");
        if (table.Exposes?.Name == set.Name) throw DataInternal.Invalid("args", $"{what}: the set is the table's own");
        // The callback is a plugin's: what it returns is read as unknown data and checked.
        var given = match(table.Refs);
        var own = StoreInternal.TableScope(table, false);
        var pairs = new List<ExistsPair>();
        foreach (var (name, reference) in given ?? NoRefs)
        {
            var column = set.ByName.GetValueOrDefault(name);
            // A reference the scope does not know (another table's, no reference at all) resolves to nothing.
            var matched = reference is null ? null : own.Resolve(reference);
            if (column is null || matched is null) throw DataInternal.Invalid("args", $"{what}: \"{name}\" is matched to no column");
            StoreInternal.CheckPair(set.Name, column, matched.Column, what);
            pairs.Add(new ExistsPair(name, matched.Name));
        }
        List<string> names = [.. pairs.Select(pair => pair.SetColumn)];
        if (names.Count != set.Key.Count) throw DataInternal.Invalid("args", $"{what}: the match covers the whole key ({string.Join(", ", set.Key)})");
        StoreInternal.CheckMatch(new MatchedSet(set.Name, set.ByName, set.Key, StoreInternal.NoIndexes), names, what);
        List<ExistsPair> ordered = [.. set.Key.Select(column => pairs.First(pair => pair.SetColumn == column))];
        return new Query(table, Condition, orderBy, direction, new Joined(set, ordered), Expected);
    }

    /// <summary>This query ordered by <paramref name="fn"/>'s column next (a non-<c>Opt</c> <c>str</c> or <c>int</c> one), in
    /// <paramref name="direction"/>: one direction per query, no column twice (<c>invalid/args</c>).</summary>
    public Query OrderBy(Func<IReadOnlyDictionary<string, ColumnRef>, ColumnRef?> fn, OrderDirection direction = OrderDirection.Asc)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return OrderBy((row, _) => fn(row), direction);
    }

    /// <summary>This query ordered by <paramref name="fn"/>'s column next (a non-<c>Opt</c> <c>str</c> or <c>int</c> one, of the table or
    /// of the joined set, its second argument), in <paramref name="direction"/>: one direction per query, no column twice
    /// (<c>invalid/args</c>).</summary>
    public Query OrderBy(Func<IReadOnlyDictionary<string, ColumnRef>, IReadOnlyDictionary<string, ColumnRef>, ColumnRef?> fn,
        OrderDirection direction = OrderDirection.Asc)
    {
        ArgumentNullException.ThrowIfNull(fn);
        // What the plugin's callback returns is checked here.
        var reference = fn(Table.Refs, Joined?.Set.Refs ?? NoRefs);
        var resolved = reference is null ? null : Scope().Resolve(reference);
        if (resolved is null || resolved.Column.Optional || resolved.Column.Type is not (ColumnType.Str or ColumnType.Int))
            throw DataInternal.Invalid("args", $"{Table.Name}: orderBy: expected a non-opt str() or int() column");
        var name = resolved.Name;
        if (orderBy.Contains(name)) throw DataInternal.Invalid("args", $"{Table.Name}: orderBy: \"{name}\" twice");
        if (this.direction is { } current && current != direction) throw DataInternal.Invalid("args", $"{Table.Name}: orderBy: one direction per query");
        return new Query(Table, Condition, [.. orderBy, name], direction, Joined, Expected);
    }

    /// <summary>The order of its rows: the <c>OrderBy</c> columns, then the key columns not among them.</summary>
    public Order Order => new([.. orderBy, .. Table.Key.Where(column => !orderBy.Contains(column))], direction ?? OrderDirection.Asc);

    /// <summary>The column <paramref name="name"/> of its order or condition: the table's, or the joined set's
    /// (<c>&lt;set&gt;:&lt;column&gt;</c>); null for none (upstream <c>column</c>).</summary>
    internal Column? Column(string name)
    {
        if (Joined is { } joined && name.StartsWith(StoreInternal.PartnerName(joined.Set.Name, ""), StringComparison.Ordinal))
            return joined.Set.ByName.GetValueOrDefault(name[StoreInternal.PartnerName(joined.Set.Name, "").Length..]);
        return Table.ByName.GetValueOrDefault(name);
    }

    private Query And(WhereNode compiled, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Column>>? expected = null)
    {
        var condition = Condition is null ? compiled : new WhereNode.And([Condition, compiled]);
        return new Query(Table, condition, orderBy, direction, Joined, expected ?? Expected);
    }

    private Scope Scope()
    {
        var own = StoreInternal.TableScope(Table, true);
        return Joined is null ? own : StoreInternal.JoinedScope(own, Joined.Set);
    }
}

internal static partial class StoreInternal
{
    /// <summary>No set column expected: a query without an <c>exists</c> (upstream <c>NOTHING_EXPECTED</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Column>> NothingExpected =
        new Dictionary<string, IReadOnlyDictionary<string, Column>>(StringComparer.Ordinal);

    /// <summary><paramref name="node"/> with every column of its own side renamed (an <c>exists</c>' inner condition is another side's;
    /// upstream <c>renamed</c>).</summary>
    internal static WhereNode Renamed(WhereNode node, Func<string, string> rename) => node switch
    {
        WhereNode.And all => new WhereNode.And([.. all.Nodes.Select(each => Renamed(each, rename))]),
        WhereNode.Or any => new WhereNode.Or([.. any.Nodes.Select(each => Renamed(each, rename))]),
        WhereNode.Not negated => new WhereNode.Not(Renamed(negated.Node, rename)),
        WhereNode.IsNull isNull => new WhereNode.IsNull(rename(isNull.Column)),
        WhereNode.Exists exists => exists with { Pairs = [.. exists.Pairs.Select(pair => pair with { Column = rename(pair.Column) })] },
        WhereNode.InArray inArray => inArray with { Column = rename(inArray.Column) },
        WhereNode.Prefix prefix => prefix with { Column = rename(prefix.Column) },
        WhereNode.Compare compare => compare with { Column = rename(compare.Column) },
        _ => throw new InvalidOperationException("An unknown condition node."),
    };

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
    /// table of the same definition is another table), each value of its column's type (<paramref name="column"/> finds a joined set's
    /// order columns); <c>invalid/args</c> otherwise (upstream <c>decodeCursor</c>).</summary>
    internal static List<object?> DecodeCursor(TableDefinition table, string sqlName, Order order, object? cursor, Func<string, Column?>? column = null)
    {
        column ??= name => table.ByName.GetValueOrDefault(name);
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
        if (ChordJson.AsString(fields["t"]) != sqlName || ChordJson.AsString(fields["d"]) != Name(order.Direction) ||
            ChordJson.AsArray(fields["o"]) is not { } columns || columns.Count != order.Columns.Count)
            throw Refuse();
        for (var i = 0; i < columns.Count; i++) if (ChordJson.AsString(columns[i]) != order.Columns[i]) throw Refuse();
        if (ChordJson.AsArray(fields["v"]) is not { } values || values.Count != order.Columns.Count) throw Refuse();
        // Order columns are non-opt str or int (checked when the query was built).
        return [.. order.Columns.Select((name, index) => CheckScalar(column(name)!.Type, values[index], $"{table.Name}: cursor").Value)];
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
