// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/sql.ts
//
// SQL of declared tables (slice 6a §5, §6, §7): the DDL a table definition renders, and the statements of its reads and writes, generated
// from the compiled vocabulary with every value bound and every identifier quoted. Names were checked at declaration (and at import), so
// no `"` occurs in them and quoting is always safe. The dialect is SQLite's. This file builds SQL text and its parameters only (each a
// SQLite value: null, a string, a double or a long; Rows.cs); the store's backend runs them.
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>The direction of an <see cref="Order"/> (upstream <c>"asc" | "desc"</c>).</summary>
public enum OrderDirection { Asc, Desc }

/// <summary>An order of rows: the order columns (the key appended as the tie-break) and one direction (upstream <c>Order</c>, which
/// <c>next/index.ts</c> exports for adapters).</summary>
/// <param name="Columns">The order columns.</param>
/// <param name="Direction">The direction.</param>
public sealed record Order(IReadOnlyList<string> Columns, OrderDirection Direction)
{
    /// <summary>The order as JavaScript prints it: <c>{"columns":[...],"direction":"asc"}</c>.</summary>
    public override string ToString() =>
        $"{{\"columns\":{ChordJson.Stringify(StoreInternal.Strings(Columns))},\"direction\":{JsonUtf16.Quote(StoreInternal.Name(Direction))}}}";
}

/// <summary>A statement and its parameters (upstream <c>Statement</c>).</summary>
internal sealed record Statement(string Sql, IReadOnlyList<object?> Params);

internal static partial class StoreInternal
{
    /// <summary>A direction's name: <c>asc</c> or <c>desc</c>.</summary>
    internal static string Name(OrderDirection direction) => direction == OrderDirection.Asc ? "asc" : "desc";

    /// <summary>A quoted SQL identifier (upstream <c>quote</c>).</summary>
    internal static string Quote(string name) => $"\"{name}\"";

    /// <summary>The SQL name of an owner's table: <c>&lt;owner&gt;:&lt;table&gt;</c> (R2), injective since neither part contains <c>:</c>
    /// (upstream <c>tableSqlName</c>).</summary>
    internal static string TableSqlName(string owner, string table) => $"{owner}:{table}";

    /// <summary><c>CREATE TABLE</c> for <paramref name="columns"/> (as recorded) and <paramref name="key"/>: <c>STRICT</c>, the key as
    /// primary key (<c>WITHOUT ROWID</c>, except for a single <c>int</c> key, which becomes the rowid: the fastest point lookups, and rows of
    /// any size), an <c>int</c> column checked within ±(2^53 - 1) (rule 10), a <c>bool</c> one within 0 and 1, a <c>json(...)</c> one as
    /// <c>TEXT</c> with its version column, and the row's <c>$v</c> (upstream <c>createTableSql</c>).</summary>
    internal static string CreateTableSql(string table, IReadOnlyDictionary<string, ColumnRecord> columns, IReadOnlyList<string> key)
    {
        var definitions = columns.SelectMany(entry => ColumnSql(entry.Key, entry.Value)).ToList();
        definitions.Add($"{Quote(RowVersion)} INTEGER NOT NULL");
        definitions.Add($"PRIMARY KEY ({string.Join(", ", key.Select(Quote))})");
        var rowid = key.Count == 1 && columns[key[0]].Type == ColumnType.Int;
        return $"CREATE TABLE {Quote(table)} ({string.Join(", ", definitions)}) STRICT{(rowid ? "" : ", WITHOUT ROWID")}";
    }

    /// <summary>The definitions of one column: itself, and a <c>json(...)</c> column's version column (upstream <c>columnSql</c>).</summary>
    internal static List<string> ColumnSql(string name, ColumnRecord column)
    {
        var nullability = column.Optional ? "" : " NOT NULL";
        var defaulted = column.Default is { } value ? $" DEFAULT {Literal(value)}" : "";
        var id = Quote(name);
        return column.Type switch
        {
            ColumnType.Str => [$"{id} TEXT{nullability}{defaulted}"],
            ColumnType.Int => [$"{id} INTEGER{nullability}{defaulted} CHECK ({id} BETWEEN -9007199254740991 AND 9007199254740991)"],
            ColumnType.Num => [$"{id} REAL{nullability}{defaulted}"],
            ColumnType.Bool => [$"{id} INTEGER{nullability}{defaulted} CHECK ({id} IN (0, 1))"],
            _ => [$"{id} TEXT{nullability}", $"{Quote(VersionColumn(name))} INTEGER{nullability}"],
        };
    }

    /// <summary>A default as a SQL literal: declaration data (checked), which DDL cannot bind.</summary>
    private static string Literal(Scalar value) => value.Value switch
    {
        string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        bool flag => flag ? "1" : "0",
        _ => JsonNumber.Text((double)value.Value),
    };

    /// <summary>The physical columns of a recorded table in the order <c>RowCodec.Physical</c> uses (<c>PhysicalColumns</c>), and its
    /// whole-row <c>INSERT OR REPLACE</c> over them (upstream <c>recordedPut</c>).</summary>
    internal static (IReadOnlyList<string> Physical, string Sql) RecordedPut(string table, IReadOnlyDictionary<string, ColumnRecord> columns)
    {
        var physical = PhysicalColumns(columns.Select(entry => (entry.Key, entry.Value.Type)));
        return (physical, PutSql(table, physical));
    }

    /// <summary><c>INSERT OR REPLACE</c> of a whole row over <paramref name="physical"/> (<c>put</c> is an upsert).</summary>
    internal static string PutSql(string table, IReadOnlyList<string> physical) =>
        $"INSERT OR REPLACE INTO {Quote(table)} ({string.Join(", ", physical.Select(Quote))}) VALUES ({string.Join(", ", physical.Select(_ => "?"))})";

    /// <summary><c>CREATE INDEX</c> named <c>&lt;table&gt;:&lt;index&gt;</c> over <paramref name="columns"/>, the key appended (every
    /// index ends with it; upstream <c>createIndexSql</c>).</summary>
    internal static string CreateIndexSql(string table, string index, IReadOnlyList<string> columns, IReadOnlyList<string> key)
    {
        var all = columns.Concat(key.Where(column => !columns.Contains(column)));
        return $"CREATE INDEX {Quote($"{table}:{index}")} ON {Quote(table)} ({string.Join(", ", all.Select(Quote))})";
    }

    /// <summary><c>DROP INDEX</c> of <c>&lt;table&gt;:&lt;index&gt;</c> (upstream <c>dropIndexSql</c>).</summary>
    internal static string DropIndexSql(string table, string index) => $"DROP INDEX {Quote($"{table}:{index}")}";

    /// <summary>The assignments of <paramref name="set"/>; a written <c>json(...)</c> column also stamps its version (a clear stamps
    /// <c>NULL</c>).</summary>
    internal static string SetSql(SetNode set, IReadOnlyDictionary<string, long> versions, List<object?> parameters)
    {
        var assignments = new List<string>();
        foreach (var (column, node) in set)
        {
            assignments.Add($"{Quote(column)} = {ExprSql(node, parameters)}");
            if (versions.TryGetValue(column, out var version))
            {
                assignments.Add($"{Quote(VersionColumn(column))} = ?");
                parameters.Add(node is null ? null : version);
            }
        }
        return string.Join(", ", assignments);
    }

    private static string ExprSql(ExprNode? node, List<object?> parameters)
    {
        switch (node)
        {
            case null:
                return "NULL";
            case ExprNode.Literal literal:
                parameters.Add(ToSqlite(literal.Value));
                return "?";
            case ExprNode.Col col:
                return Quote(col.Column);
            case ExprNode.Json json:
                // Serialized once at staging already (rule 8): this cannot throw.
                parameters.Add(ChordJson.Stringify(json.Value));
                return "?";
            case ExprNode.When conditional:
                return $"(CASE WHEN {WhereSql(conditional.Condition, parameters)} THEN {ExprSql(conditional.A, parameters)} ELSE {ExprSql(conditional.B, parameters)} END)";
            case ExprNode.ReplacePrefix replace:
            {
                // SQLite's substr counts characters of UTF-8 text: code points, as the prefix's length does. Parameters in the order their ?
                // occur in the text.
                long length = CodePoints(replace.From).Count;
                var value = ExprSql(replace.Subject, parameters);
                parameters.Add(length);
                parameters.Add(replace.From);
                parameters.Add(replace.To);
                var rest = ExprSql(replace.Subject, parameters);
                parameters.Add(length + 1);
                var unchanged = ExprSql(replace.Subject, parameters);
                return $"(CASE WHEN substr({value}, 1, ?) = ? THEN ? || substr({rest}, ?) ELSE {unchanged} END)";
            }
            case ExprNode.Binary binary:
            {
                var a = ExprSql(binary.A, parameters);
                var b = ExprSql(binary.B, parameters);
                return binary.Op switch
                {
                    Operation.Coalesce => $"coalesce({a}, {b})",
                    Operation.Add => $"({a} + {b})",
                    Operation.Sub => $"({a} - {b})",
                    Operation.Max => $"max({a}, {b})",
                    Operation.Min => $"min({a}, {b})",
                    _ => $"({a} || {b})",
                };
            }
            default:
                throw new InvalidOperationException("An unknown expression node.");
        }
    }

    /// <summary>The SQL operator of a comparison.</summary>
    private static string Sql(Comparison op) => op switch
    {
        Comparison.Eq => "=",
        Comparison.Ne => "<>",
        Comparison.Lt => "<",
        Comparison.Lte => "<=",
        Comparison.Gt => ">",
        _ => ">=",
    };

    /// <summary>A condition with its operands bound; SQL's <c>NULL</c> rules are the vocabulary's (upstream <c>whereSql</c>).</summary>
    internal static string WhereSql(WhereNode node, List<object?> parameters)
    {
        switch (node)
        {
            case WhereNode.And all:
                return all.Nodes.Count == 0 ? "1" : $"({string.Join(" AND ", all.Nodes.Select(each => WhereSql(each, parameters)))})";
            case WhereNode.Or any:
                return any.Nodes.Count == 0 ? "0" : $"({string.Join(" OR ", any.Nodes.Select(each => WhereSql(each, parameters)))})";
            case WhereNode.Not negated:
                return $"(NOT {WhereSql(negated.Node, parameters)})";
            case WhereNode.IsNull isNull:
                return $"{Quote(isNull.Column)} IS NULL";
            case WhereNode.InArray inArray:
                parameters.Add(SqliteJson(inArray.Values.Select(ToSqlite)));
                return $"{Quote(inArray.Column)} IN (SELECT value FROM json_each(?))";
            case WhereNode.Prefix prefix:
            {
                var upper = PrefixUpperBound(prefix.Value);
                parameters.Add(prefix.Value);
                if (upper is null) return $"{Quote(prefix.Column)} >= ?";
                parameters.Add(upper);
                return $"({Quote(prefix.Column)} >= ? AND {Quote(prefix.Column)} < ?)";
            }
            case WhereNode.Compare compare:
                parameters.Add(ToSqlite(compare.Value));
                return $"{Quote(compare.Column)} {Sql(compare.Op)} ?";
            default:
                throw new InvalidOperationException("An unknown condition node.");
        }
    }
}

/// <summary>The statements of one table at one version, rendered once per table handle: the fixed ones as texts, the ones over compiled
/// conditions and sets on demand (upstream <c>TableSql</c>).</summary>
internal sealed class TableSql
{
    /// <summary>The columns a read selects: the declared ones and each <c>json(...)</c> column's version.</summary>
    private readonly string select;
    private readonly string byKey;

    internal TableSql(string table, IEnumerable<Column> columns, IReadOnlyList<string> physical, IReadOnlyList<string> key)
        : this(table, columns.Select(column => (column.Name, column.Type)), physical, key)
    {
    }

    internal TableSql(string table, IEnumerable<(string Name, ColumnType Type)> columns, IReadOnlyList<string> physical, IReadOnlyList<string> key)
    {
        Table = table;
        var list = columns.ToList();
        var id = StoreInternal.Quote(table);
        select = string.Join(", ", list.Select(column => StoreInternal.Quote(column.Name))
            .Concat(list.Where(column => column.Type == ColumnType.Json).Select(column => StoreInternal.Quote(StoreInternal.VersionColumn(column.Name)))));
        byKey = string.Join(" AND ", key.Select(column => $"{StoreInternal.Quote(column)} = ?"));
        Put = StoreInternal.PutSql(table, physical);
        Get = $"SELECT {select} FROM {id} WHERE {byKey}";
        Delete = $"DELETE FROM {id} WHERE {byKey}";
    }

    /// <summary>The table's SQL name.</summary>
    internal string Table { get; }

    /// <summary><c>INSERT OR REPLACE</c> of a whole row, in <c>RowCodec.Physical</c>'s order (<c>put</c> is an upsert).</summary>
    internal string Put { get; }

    /// <summary><c>SELECT</c> of one row by key: the declared columns and each <c>json(...)</c> column's version.</summary>
    internal string Get { get; }

    /// <summary><c>DELETE</c> of one row by key.</summary>
    internal string Delete { get; }

    /// <summary><c>SELECT</c> of <paramref name="columns"/> of one row by key: a point lookup, without the <c>ORDER BY</c> and <c>LIMIT</c>
    /// of a page.</summary>
    internal string GetColumns(IEnumerable<string> columns) =>
        $"SELECT {string.Join(", ", columns.Select(StoreInternal.Quote))} FROM {StoreInternal.Quote(Table)} WHERE {byKey}";

    /// <summary><c>UPDATE</c> of one row by key; <paramref name="versions"/> gives each <c>json(...)</c> column's version to stamp.</summary>
    internal Statement Update(SetNode set, IReadOnlyList<object?> key, IReadOnlyDictionary<string, long> versions)
    {
        var parameters = new List<object?>();
        var assignments = StoreInternal.SetSql(set, versions, parameters);
        parameters.AddRange(key);
        return new Statement($"UPDATE {StoreInternal.Quote(Table)} SET {assignments} WHERE {byKey}", parameters);
    }

    /// <summary><c>UPDATE</c> of every row matching <paramref name="where"/>.</summary>
    internal Statement UpdateWhere(WhereNode where, SetNode set, IReadOnlyDictionary<string, long> versions)
    {
        var parameters = new List<object?>();
        var assignments = StoreInternal.SetSql(set, versions, parameters);
        var condition = StoreInternal.WhereSql(where, parameters);
        return new Statement($"UPDATE {StoreInternal.Quote(Table)} SET {assignments} WHERE {condition}", parameters);
    }

    /// <summary><c>DELETE</c> of every row matching <paramref name="where"/>.</summary>
    internal Statement DeleteWhere(WhereNode where)
    {
        var parameters = new List<object?>();
        return new Statement($"DELETE FROM {StoreInternal.Quote(Table)} WHERE {StoreInternal.WhereSql(where, parameters)}", parameters);
    }

    /// <summary>A page: rows matching <paramref name="where"/> in <paramref name="order"/>, after the order values <paramref name="after"/>
    /// (row-value comparison; the key in the order makes it total), at most <paramref name="limit"/> (bound); every column, or only
    /// <paramref name="columns"/>.</summary>
    internal Statement Select(WhereNode? where, Order order, IReadOnlyList<object?>? after, long limit, IReadOnlyList<string>? columns = null)
    {
        var parameters = new List<object?>();
        var conditions = new List<string>();
        if (where is not null) conditions.Add(StoreInternal.WhereSql(where, parameters));
        if (after is not null)
        {
            var orderColumns = string.Join(", ", order.Columns.Select(StoreInternal.Quote));
            conditions.Add($"({orderColumns}) {(order.Direction == OrderDirection.Asc ? ">" : "<")} ({string.Join(", ", after.Select(_ => "?"))})");
            parameters.AddRange(after);
        }
        var filter = conditions.Count == 0 ? "" : $" WHERE {string.Join(" AND ", conditions)}";
        var direction = order.Direction == OrderDirection.Asc ? "ASC" : "DESC";
        var sorting = string.Join(", ", order.Columns.Select(column => $"{StoreInternal.Quote(column)} {direction}"));
        parameters.Add(limit);
        var selected = columns is null ? select : string.Join(", ", columns.Select(StoreInternal.Quote));
        return new Statement($"SELECT {selected} FROM {StoreInternal.Quote(Table)}{filter} ORDER BY {sorting} LIMIT ?", parameters);
    }

    /// <summary><c>count(*)</c> of the rows matching <paramref name="where"/>.</summary>
    internal Statement Count(WhereNode? where)
    {
        var parameters = new List<object?>();
        var filter = where is null ? "" : $" WHERE {StoreInternal.WhereSql(where, parameters)}";
        return new Statement($"SELECT count(*) AS count FROM {StoreInternal.Quote(Table)}{filter}", parameters);
    }
}
