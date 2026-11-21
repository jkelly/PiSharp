// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/read-plan.ts
//
// Read plans (slice 14 §3.2, §3.3, §5): a query compiled for the tables it reads right now. The plan resolves each column set to the table
// bound to it, checks every column and match the query uses against that table (a trust boundary: conditions arrive as data, and a
// publisher's reload changes its table), lists the tables the read depends on with the columns that matter, and renders its statements: the
// rows of a range, a count, and the probe. Mappings:
// - Bindings is IBindings, Bound a record; Projection a record of two optional column lists.
// - Use is a class, not a record: a read's result keys a map by the use itself (reference identity), and two uses of one table may hold
//   equal members.
// - Position is a list of SQLite values (null, string, double or long; Rows.cs); Range is ReadRange (System.Range is taken), StoredRead a
//   row as the backend returns it, a map of column name to SQLite value.
// - A plan is immutable but for the cache of its statement texts, which a commit step and a reader may fill from two threads: it is a
//   ConcurrentDictionary.
using System.Collections.Concurrent;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>The table a column set is bound to: its SQL name and the definition its owner declared (upstream <c>Bound</c>).</summary>
internal sealed record Bound(string Table, TableDefinition Definition);

/// <summary>The store's bindings of column sets, by set name (upstream <c>Bindings</c>).</summary>
internal interface IBindings
{
    /// <summary>The table bound to <paramref name="set"/>; null when nobody serves it.</summary>
    Bound? Get(string set);
}

/// <summary>What a read hands its mapping: columns of the own table, and of the joined set; null for all of them (upstream
/// <c>Projection</c>).</summary>
internal sealed record Projection(IReadOnlyList<string>? Reads = null, IReadOnlyList<string>? PartnerReads = null)
{
    /// <summary>Every column of both sides.</summary>
    internal static Projection All { get; } = new();
}

/// <summary>The role of a table in a read (upstream <c>Use["role"]</c>).</summary>
internal enum UseRole { Own, Joined, Exists }

/// <summary>One use of a table by a read (a table may be used more than once): its role, the columns of it the read depends on, and the
/// own columns a written row of it pairs with, which the probe is limited by (upstream <c>Use</c>).</summary>
internal sealed class Use
{
    /// <summary>The table's role in the read.</summary>
    internal required UseRole Role { get; init; }

    /// <summary>The table's SQL name.</summary>
    internal required string Table { get; init; }

    /// <summary>The columns of the table a change of which may change the read's result.</summary>
    internal required IReadOnlySet<string> Depends { get; init; }

    /// <summary>The columns a change of which may bring a row into the result that was not in it (not the mapped ones).</summary>
    internal required IReadOnlySet<string> Enters { get; init; }

    /// <summary>The table's columns a written row is identified by: the key, or an <c>exists</c>' matched columns.</summary>
    internal required IReadOnlyList<string> PairedBy { get; init; }

    /// <summary>The own columns equal to <see cref="PairedBy"/>; null: none, the pairing goes through another side.</summary>
    internal required IReadOnlyList<string>? Pairs { get; init; }

    /// <summary>Whether an equality on <see cref="Pairs"/> is keyed in the own table: only then the probe is used.</summary>
    internal required bool Keyed { get; init; }

    /// <summary>Whether <see cref="PairedBy"/> are key columns of the table: a row then pairs with the same own rows before and after.</summary>
    internal required bool Stable { get; init; }

    /// <summary>This use paired by other own columns.</summary>
    internal Use With(IReadOnlyList<string>? pairs, bool keyed) => new()
    {
        Role = Role, Table = Table, Depends = Depends, Enters = Enters, PairedBy = PairedBy, Pairs = pairs, Keyed = keyed, Stable = Stable,
    };
}

/// <summary>The rows a statement asks for: after <paramref name="After"/>, through <paramref name="Through"/> (inclusive), at most
/// <paramref name="Limit"/>; a position is the values of the first order columns, all of them or a leading part (upstream <c>Range</c> and
/// <c>Position</c>).</summary>
internal sealed record ReadRange(long Limit, IReadOnlyList<object?>? After = null, IReadOnlyList<object?>? Through = null);

/// <summary>What a plan is read as: rows, or a count, which depends on its condition and match columns only (slice 14 §3.3).</summary>
internal enum ReadShape { Rows, Count }

/// <summary>A query compiled against the store's bindings. Its statements select the columns the mapping gets, the own key, the matched own
/// columns and the order columns, so a run can keep the keys the skip test needs (upstream <c>ReadPlan</c>).</summary>
internal sealed class ReadPlan
{
    private const string Own = "t";
    private const string Partner = "p";
    private static readonly IReadOnlyList<string> NoColumns = [];
    private static readonly ReadRange Unbounded = new(0);

    private readonly IBindings bindings;
    private readonly Sides sides;
    private readonly string select;
    private readonly string from;
    // The condition rendered once, with its parameters; no text without a condition.
    private readonly string? whereSql;
    private readonly IReadOnlyList<object?> whereParams;
    // Its statements' texts by shape (Text).
    private readonly ConcurrentDictionary<string, string> texts = new(StringComparer.Ordinal);

    /// <param name="query">The query.</param>
    /// <param name="table">The own table's SQL name.</param>
    /// <param name="projection">The columns the mapping gets.</param>
    /// <param name="shape">What the plan is read as.</param>
    /// <param name="bindings">The store's bindings of column sets.</param>
    internal ReadPlan(Query query, string table, Projection projection, ReadShape shape, IBindings bindings)
    {
        var (joined, condition) = (query.Joined, query.Condition);
        Query = query;
        Table = table;
        Order = query.Order;
        this.bindings = bindings;
        sides = new Sides(joined);
        var partner = joined is null ? null : BoundPartner(joined, bindings);
        CheckExpected(query.Expected, bindings);
        // A count reads no rows: nothing is mapped and nothing ordered.
        var counted = shape == ReadShape.Count;
        var reads = counted ? NoColumns : projection.Reads ?? [.. query.Table.Columns.Select(column => column.Name)];
        var partnerReads = counted ? NoColumns : projection.PartnerReads ?? joined?.Set.ColumnNames ?? NoColumns;
        List<string> used = [.. condition is null ? [] : StoreInternal.ColumnsOf(condition), .. counted ? NoColumns : Order.Columns];
        var fixedColumns = condition is null ? [] : FixedBy(condition);
        var what = $"{query.Table.Name}: exists";
        var tested = condition is null ? [] : ExistsUses(condition, query.Column, bindings, what);
        var own = OwnUse(query, table, [.. used.Where(name => !sides.IsPartner(name))], reads);
        List<Use> uses = [own];
        if (partner is not null) uses.Add(JoinedUse(query, partner, used, partnerReads, fixedColumns, sides));
        uses.AddRange(tested.Select(use => PairedWithOwn(use, query, fixedColumns, sides)));
        Uses = uses;
        select = SelectList(query, uses, reads, partnerReads, counted ? NoColumns : Order.Columns, sides);
        from = FromClause(table, joined, partner);
        var parameters = new List<object?>();
        whereSql = condition is null ? null : StoreInternal.WhereSql(condition, parameters, Render());
        whereParams = parameters;
    }

    /// <summary>The query.</summary>
    internal Query Query { get; }

    /// <summary>The own table's SQL name.</summary>
    internal string Table { get; }

    /// <summary>The order of its rows.</summary>
    internal Order Order { get; }

    /// <summary>The tables it reads, the own one first.</summary>
    internal IReadOnlyList<Use> Uses { get; }

    /// <summary>The rows of <paramref name="range"/> in the plan's order.</summary>
    internal Statement Select(ReadRange range)
    {
        var sql = Text("select", range, NoColumns, filter =>
        {
            var direction = Order.Direction == OrderDirection.Asc ? "ASC" : "DESC";
            var sorting = string.Join(", ", Order.Columns.Select(name => $"{sides.Sql(name)} {direction}"));
            return $"SELECT {select} FROM {from}{filter} ORDER BY {sorting} LIMIT ?";
        });
        return new Statement(sql, [.. BoundParams(range), range.Limit]);
    }

    /// <summary><c>count(*)</c> of the rows matching the condition.</summary>
    internal Statement Count() =>
        new(Text("count", Unbounded, NoColumns, filter => $"SELECT count(*) AS count FROM {from}{filter}"), BoundParams(Unbounded));

    /// <summary>The probe (slice 14 §3.3): whether a row of <paramref name="range"/> has <paramref name="values"/> in the own columns
    /// <paramref name="columns"/>. One row or none.</summary>
    internal Statement Probe(ReadRange range, IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        var sql = Text("probe", range, columns, filter =>
        {
            var limited = string.Join(" AND ", columns.Select(name => $"{Own}.{StoreInternal.Quote(name)} = ?"));
            return $"SELECT 1 AS found FROM {from}{filter}{(filter.Length == 0 ? " WHERE " : " AND ")}{limited} LIMIT 1";
        });
        return new Statement(sql, [.. BoundParams(range), .. values]);
    }

    /// <summary>The text of one of its statements, rendered once per shape (which bounds the range has and how long, which columns a probe
    /// is limited by): a probe runs per written key and read, so only its parameters are built per call.</summary>
    private string Text(string kind, ReadRange range, IReadOnlyList<string> columns, Func<string, string> render)
    {
        var key = $"{kind}|{range.After?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}|" +
            $"{range.Through?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}|{string.Join(",", columns)}";
        return texts.GetOrAdd(key, _ => render(Filter(range)));
    }

    /// <summary>The parameters of a statement's filter, in the order <see cref="Filter"/> renders them: the condition's, then the bounds'.</summary>
    private List<object?> BoundParams(ReadRange range) => [.. whereParams, .. range.After ?? [], .. range.Through ?? []];

    /// <summary><c> WHERE ...</c> of the condition and the range's bounds (row-value comparisons over the leading order columns).</summary>
    private string Filter(ReadRange range)
    {
        List<string> conditions = whereSql is null ? [] : [whereSql];
        var asc = Order.Direction == OrderDirection.Asc;
        void Bound(IReadOnlyList<object?> position, string comparison)
        {
            var columns = string.Join(", ", Order.Columns.Take(position.Count).Select(sides.Sql));
            conditions.Add($"({columns}) {comparison} ({string.Join(", ", position.Select(_ => "?"))})");
        }
        if (range.After is not null) Bound(range.After, asc ? ">" : "<");
        if (range.Through is not null) Bound(range.Through, asc ? "<=" : ">=");
        return conditions.Count == 0 ? "" : $" WHERE {string.Join(" AND ", conditions)}";
    }

    /// <summary>How the condition's columns and <c>exists</c> render: own and partner columns by alias, an <c>exists</c> as a subquery.</summary>
    private ConditionSql Render() => new(sides.Sql, (node, parameters) => ExistsSql(node, parameters, sides.Sql, 0));

    private string ExistsSql(WhereNode.Exists node, List<object?> parameters, Func<string, string> outer, int depth)
    {
        var alias = $"e{depth.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        string Column(string name) => $"{alias}.{StoreInternal.Quote(name)}";
        List<string> matched = [.. node.Pairs.Select(pair => $"{Column(pair.SetColumn)} = {outer(pair.Column)}")];
        var render = new ConditionSql(Column, (nested, into) => ExistsSql(nested, into, Column, depth + 1));
        if (node.Inner is not null) matched.Add(StoreInternal.WhereSql(node.Inner, parameters, render));
        return $"EXISTS (SELECT 1 FROM {StoreInternal.Quote(Served(bindings, node.Set).Table)} AS {alias} WHERE {string.Join(" AND ", matched)})";
    }

    /// <summary>The two sides of a query's column names: own columns as they are, a joined set's as <c>&lt;set&gt;:&lt;column&gt;</c>
    /// (upstream <c>Sides</c>).</summary>
    private sealed class Sides(Joined? joined)
    {
        private readonly string? prefix = joined is null ? null : StoreInternal.PartnerName(joined.Set.Name, "");

        internal bool IsPartner(string name) => prefix is not null && name.StartsWith(prefix, StringComparison.Ordinal);

        /// <summary>A partner column's name within its set.</summary>
        internal string Strip(string name) => name[(prefix?.Length ?? 0)..];

        /// <summary>The column in the plan's SQL, by its side's alias.</summary>
        internal string Sql(string name) =>
            IsPartner(name) ? $"{Partner}.{StoreInternal.Quote(Strip(name))}" : $"{Own}.{StoreInternal.Quote(name)}";
    }

    /// <summary>The table bound to the joined set, checked: the read matches the set's whole key as the bound table has it now (a key that
    /// gained a column would pair one row with several), or it is not what the read was written for (upstream <c>boundPartner</c>).</summary>
    private static Bound BoundPartner(Joined joined, IBindings bindings)
    {
        var name = joined.Set.Name;
        var bound = Served(bindings, name);
        var key = Exposed(bound, name).Key;
        List<string> matched = [.. joined.Pairs.Select(pair => pair.SetColumn)];
        if (key.Count != matched.Count || !key.All(matched.Contains))
        {
            throw DataInternal.Invalid("args",
                $"column set \"{name}\" is joined by ({string.Join(", ", matched)}), its key is ({string.Join(", ", key)}): not what this read was written for");
        }
        return bound;
    }

    /// <summary>Checks the set columns a query's <c>exists</c> were written for against the tables bound to their sets (upstream
    /// <c>checkExpected</c>).</summary>
    private static void CheckExpected(IReadOnlyDictionary<string, IReadOnlyDictionary<string, Column>> expected, IBindings bindings)
    {
        foreach (var (set, columns) in expected)
        {
            var byName = Exposed(Served(bindings, set), set).ByName;
            foreach (var (name, column) in columns) CheckColumn(column, byName.GetValueOrDefault(name), set, name);
        }
    }

    /// <summary>The use of the query's own table: <paramref name="used"/> are the own columns of its condition and order (upstream
    /// <c>ownUse</c>).</summary>
    private static Use OwnUse(Query query, string table, IReadOnlyList<string> used, IReadOnlyList<string> reads)
    {
        var matched = query.Joined?.Pairs.Select(pair => pair.Column) ?? [];
        HashSet<string> enters = new(used.Concat(matched), StringComparer.Ordinal);
        var key = query.Table.Key;
        return new Use
        {
            Role = UseRole.Own, Table = table, Depends = new HashSet<string>(enters.Concat(reads), StringComparer.Ordinal), Enters = enters,
            PairedBy = key, Pairs = key, Keyed = true, Stable = true,
        };
    }

    /// <summary>The use of the joined set's table, checked: every partner column the read uses is, in the bound table, what the read's set
    /// declares (upstream <c>joinedUse</c>).</summary>
    private static Use JoinedUse(Query query, Bound partner, IReadOnlyList<string> used, IReadOnlyList<string> partnerReads,
        IReadOnlySet<string> fixedColumns, Sides sides)
    {
        var (set, pairs) = (query.Joined!.Set, query.Joined.Pairs);
        HashSet<string> enters = new(used.Where(sides.IsPartner).Select(sides.Strip), StringComparer.Ordinal);
        HashSet<string> depends = new(enters.Concat(partnerReads).Concat(set.Key), StringComparer.Ordinal);
        var byName = Exposed(partner, set.Name).ByName;
        foreach (var name in depends) CheckColumn(set.ByName.GetValueOrDefault(name), byName.GetValueOrDefault(name), set.Name, name);
        List<string> matched = [.. pairs.Select(pair => pair.Column)];
        return new Use
        {
            Role = UseRole.Joined, Table = partner.Table, Depends = depends, Enters = enters, PairedBy = set.Key, Pairs = matched,
            Keyed = KeyedBy(query.Table, fixedColumns, matched), Stable = true,
        };
    }

    /// <summary>The uses of the <c>exists</c> in <paramref name="condition"/> (a condition of one side, whose columns
    /// <paramref name="columnOf"/> knows), each checked against the table bound to its set: the match rule, the matched columns' types, its
    /// inner condition's columns. An outermost one's <c>Pairs</c> are the enclosing columns it is matched to; one nested in another pairs
    /// with no own row (upstream <c>existsUses</c>).</summary>
    private static List<Use> ExistsUses(WhereNode condition, Func<string, Column?> columnOf, IBindings bindings, string what)
    {
        var uses = new List<Use>();
        foreach (var exists in StoreInternal.ExistsIn(condition))
        {
            var (setName, pairs, inner) = (exists.Set, exists.Pairs, exists.Inner);
            var bound = Served(bindings, setName);
            var set = Exposed(bound, setName);
            List<string> pairedBy = [.. pairs.Select(pair => pair.SetColumn)];
            StoreInternal.CheckMatch(set, pairedBy, $"{what}({setName})");
            foreach (var (name, enclosing) in pairs)
            {
                var (column, outer) = (set.ByName.GetValueOrDefault(name), columnOf(enclosing));
                if (column is null || outer is null) throw DataInternal.Invalid("args", $"{what}({setName}): no column \"{(column is null ? name : enclosing)}\"");
                StoreInternal.CheckPair(setName, column, outer, $"{what}({setName})");
            }
            // The node was read without its set's columns when it arrived as data: it is read again against the table bound now.
            if (inner is not null) StoreInternal.CheckNode(inner.ToJson(), set, $"{what}({setName})");
            HashSet<string> depends = new(pairedBy.Concat(inner is null ? [] : StoreInternal.ColumnsOf(inner)), StringComparer.Ordinal);
            uses.Add(new Use
            {
                Role = UseRole.Exists, Table = bound.Table, Depends = depends, Enters = depends, PairedBy = pairedBy,
                Pairs = [.. pairs.Select(pair => pair.Column)], Keyed = false, Stable = pairedBy.All(bound.Definition.Key.Contains),
            });
            // One nested in this one is matched to this set's columns: it pairs with no own row.
            if (inner is not null)
                uses.AddRange(ExistsUses(inner, name => set.ByName.GetValueOrDefault(name), bindings, what).Select(nested => nested.With(null, nested.Keyed)));
        }
        return uses;
    }

    /// <summary>An <c>exists</c>' use with the own columns it pairs by: its enclosing columns when they are own ones; the join's own
    /// columns when it is matched on the partner's key; none otherwise, and the read runs then (upstream <c>pairedWithOwn</c>).</summary>
    private static Use PairedWithOwn(Use use, Query query, IReadOnlySet<string> fixedColumns, Sides sides)
    {
        string? ToOwn(string name)
        {
            if (!sides.IsPartner(name)) return name;
            var key = sides.Strip(name);
            foreach (var pair in query.Joined!.Pairs)
                if (pair.SetColumn == key) return pair.Column;
            return null;
        }
        var mapped = use.Pairs?.Select(ToOwn).ToList();
        IReadOnlyList<string>? pairs = mapped is not null && mapped.All(name => name is not null) ? [.. mapped.Select(name => name!)] : null;
        return use.With(pairs, pairs is not null && KeyedBy(query.Table, fixedColumns, pairs));
    }

    /// <summary>The select list: what the mapping gets (<paramref name="reads"/>, <paramref name="partnerReads"/>, each <c>json(...)</c>
    /// column with its version), and what the store keeps of a row: the own key, the own columns paired with other tables, the order
    /// columns (upstream <c>selectList</c>).</summary>
    private static string SelectList(Query query, IReadOnlyList<Use> uses, IReadOnlyList<string> reads, IReadOnlyList<string> partnerReads,
        IReadOnlyList<string> order, Sides sides)
    {
        var own = query.Table;
        // In insertion order, each name once, as upstream's Set.
        var selected = new List<string>();
        void Selected(string name)
        {
            if (!selected.Contains(name)) selected.Add(name);
        }
        foreach (var name in reads.Concat(own.Key).Concat(order.Where(name => !sides.IsPartner(name)))) Selected(name);
        foreach (var use in uses)
            foreach (var name in use.Pairs ?? NoColumns) Selected(name);
        var versions = reads.Where(name => own.ByName.GetValueOrDefault(name)?.Type == ColumnType.Json).Select(StoreInternal.VersionColumn);
        var partner = new List<string>();
        foreach (var name in partnerReads.Concat(order.Where(sides.IsPartner).Select(sides.Strip)))
            if (!partner.Contains(name)) partner.Add(name);
        var set = query.Joined?.Set.Name ?? "";
        return string.Join(", ", selected.Concat(versions).Select(name => $"{Own}.{StoreInternal.Quote(name)} AS {StoreInternal.Quote(name)}")
            .Concat(partner.Select(name => $"{Partner}.{StoreInternal.Quote(name)} AS {StoreInternal.Quote(StoreInternal.PartnerName(set, name))}")));
    }

    /// <summary>The tables a statement reads from. <c>CROSS JOIN</c> keeps SQLite from reordering: a read is driven from its own table
    /// (upstream <c>fromClause</c>).</summary>
    private static string FromClause(string table, Joined? joined, Bound? partner)
    {
        if (joined is null || partner is null) return $"{StoreInternal.Quote(table)} AS {Own}";
        var on = string.Join(" AND ", joined.Pairs.Select(pair => $"{Partner}.{StoreInternal.Quote(pair.SetColumn)} = {Own}.{StoreInternal.Quote(pair.Column)}"));
        return $"{StoreInternal.Quote(table)} AS {Own} CROSS JOIN {StoreInternal.Quote(partner.Table)} AS {Partner} ON {on}";
    }

    /// <summary>The binding of <paramref name="set"/>; a read over a set nobody serves fails <c>unavailable</c> and runs again when one
    /// appears (upstream <c>served</c>).</summary>
    private static Bound Served(IBindings bindings, string set) =>
        bindings.Get(set) ?? throw new ChordError("unavailable", $"column set \"{set}\" is not served");

    /// <summary>The column set <paramref name="bound"/>'s table exposes: the one named <paramref name="set"/> (a binding is made from
    /// <c>Exposes</c>; upstream <c>exposed</c>).</summary>
    private static ColumnSet Exposed(Bound bound, string set) =>
        bound.Definition.Exposes is { } found && found.Name == set ? found : throw new ChordError("unavailable", $"column set \"{set}\" is not served");

    /// <summary>Refuses a column of a set the read was written for that the bound table has not, or has differently (upstream
    /// <c>checkColumn</c>).</summary>
    private static void CheckColumn(Column? expected, Column? found, string set, string name)
    {
        if (expected is not null && found is not null && StoreInternal.SameColumn(expected, found)) return;
        throw DataInternal.Invalid("args", $"column \"{name}\" of column set \"{set}\" is not what this read was written for");
    }

    /// <summary>The own columns a condition fixes with <c>eq</c> or <c>inArray</c> in its top-level <c>and</c> terms (upstream
    /// <c>fixedBy</c>).</summary>
    private static HashSet<string> FixedBy(WhereNode node) => node switch
    {
        WhereNode.And all => new HashSet<string>(all.Nodes.SelectMany(FixedBy), StringComparer.Ordinal),
        WhereNode.Compare { Op: Comparison.Eq } equal => new HashSet<string>(StringComparer.Ordinal) { equal.Column },
        WhereNode.InArray inArray => new HashSet<string>(StringComparer.Ordinal) { inArray.Column },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>Whether an equality on <paramref name="columns"/> is keyed in <paramref name="table"/>: together with the
    /// <paramref name="fixedColumns"/> before them they are a leading prefix of its key or of one of its indexes (upstream
    /// <c>keyedBy</c>).</summary>
    private static bool KeyedBy(TableDefinition table, IReadOnlySet<string> fixedColumns, IReadOnlyList<string> columns)
    {
        List<IReadOnlyList<string>> paths = [table.Key, .. table.Indexes.Values.Select(index => StoreInternal.IndexPath(index, table.Key))];
        return paths.Any(path =>
        {
            var covered = 0;
            foreach (var name in path)
            {
                if (columns.Contains(name)) covered++;
                else if (!fixedColumns.Contains(name)) break;
                if (covered == columns.Count) return true;
            }
            return false;
        });
    }
}
