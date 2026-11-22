// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/expression.ts
//
// The table expression language (slice 6a §6, §7; rules 10, 13, 14, 16; decision "Nullable columns and the `set` form"): the builders
// plugins write `where` and `set` with, and their compilation at staging into one data-only vocabulary (what the outbox and archives
// carry; Evaluate.cs runs it in C#, Sql.cs renders it). The vocabulary is typed here: WhereNode, ExprNode (a null ExprNode is upstream's
// null, SQL NULL) and SetNode, each with ToJson giving upstream's data form ({ eq: [column, value] }, { add: [a, b] }, ...). A plugin's
// value is a JsonNode (Rows.cs); an expression operand is an Operand: an Expression, a JsonNode (C# null is JSON null) or
// Operand.Undefined (upstream's undefined, which clears an Opt(...) column). A set is a map from column name to Operand (or a JsonObject of
// plain values). Upstream's Expression<T, K>, ColumnRef<T, K> and SetOf<C, K> carry TypeScript types of the value and column type; here
// the classes are untyped and what those types refuse is refused when the expression is compiled.
//
// 1.6.0 (slice 14 §5, §6): a query's condition may test Exists over another plugin's column set, a condition is compiled in a Scope (one
// table, a column set alone, or a table joined with a set), and a condition can travel as data: Tables.Compile gives the WhereNode,
// CheckNode reads one back from JSON (upstream's checkNode asserts that an unknown value is a WhereNode; here it is the parser from the
// JSON form to the typed node, with the same checks and messages). A Match is a map from a set's column to a reference of the enclosing row.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>The comparison operators of a <see cref="WhereNode.Compare"/> (upstream <c>Comparison</c>).</summary>
public enum Comparison { Eq, Ne, Lt, Lte, Gt, Gte }

/// <summary>A compiled condition: column comparisons with checked operands, combined by <c>and</c>, <c>or</c>, <c>not</c> (upstream
/// <c>WhereNode</c>, which <c>next/index.ts</c> exports for adapters). Immutable.</summary>
public abstract record WhereNode
{
    private protected WhereNode()
    {
    }

    /// <summary><c>{ [op]: [column, value] }</c>: the column compared with a value (<c>NULL</c> never matches).</summary>
    public sealed record Compare(Comparison Op, string Column, Scalar Value) : WhereNode;

    /// <summary><c>{ inArray: [column, values] }</c>: the column equals one of the values (<c>[]</c> matches nothing).</summary>
    public sealed record InArray(string Column, IReadOnlyList<Scalar> Values) : WhereNode;

    /// <summary><c>{ isNull: column }</c>: the column is <c>NULL</c>.</summary>
    public sealed record IsNull(string Column) : WhereNode;

    /// <summary><c>{ prefix: [column, prefix] }</c>: the text column starts with the prefix, by code point.</summary>
    public sealed record Prefix(string Column, string Value) : WhereNode;

    /// <summary><c>{ and: nodes }</c>: every node holds (<c>and()</c> always does).</summary>
    public sealed record And(IReadOnlyList<WhereNode> Nodes) : WhereNode;

    /// <summary><c>{ or: nodes }</c>: some node holds (<c>or()</c> never does).</summary>
    public sealed record Or(IReadOnlyList<WhereNode> Nodes) : WhereNode;

    /// <summary><c>{ not: node }</c>: the node does not hold (<c>NULL</c> stays <c>NULL</c>).</summary>
    public sealed record Not(WhereNode Node) : WhereNode;

    /// <summary><c>{ exists: [set, [[setColumn, column], ...], inner] }</c> (slice 14 §5): true for a row when the set's table has a row
    /// equal in the matched columns that satisfies <paramref name="Inner"/> (null: any). A query's condition only; writes take none.</summary>
    public sealed record Exists(string Set, IReadOnlyList<ExistsPair> Pairs, WhereNode? Inner) : WhereNode;

    /// <summary>The node in upstream's data form, as the outbox and archives carry it.</summary>
    public JsonObject ToJson() => this switch
    {
        Exists node => new JsonObject
        {
            ["exists"] = new JsonArray(node.Set,
                new JsonArray([.. node.Pairs.Select(pair => (JsonNode)new JsonArray(pair.SetColumn, pair.Column))]), node.Inner?.ToJson()),
        },
        Compare node => new JsonObject { [StoreInternal.Name(node.Op)] = new JsonArray(node.Column, node.Value.ToNode()) },
        InArray node => new JsonObject { ["inArray"] = new JsonArray(node.Column, new JsonArray([.. node.Values.Select(value => value.ToNode())])) },
        IsNull node => new JsonObject { ["isNull"] = node.Column },
        Prefix node => new JsonObject { ["prefix"] = new JsonArray(node.Column, node.Value) },
        And node => new JsonObject { ["and"] = new JsonArray([.. node.Nodes.Select(each => (JsonNode)each.ToJson())]) },
        Or node => new JsonObject { ["or"] = new JsonArray([.. node.Nodes.Select(each => (JsonNode)each.ToJson())]) },
        Not node => new JsonObject { ["not"] = node.Node.ToJson() },
        _ => throw new InvalidOperationException("An unknown condition node."),
    };

    /// <summary>The node's JSON text (<c>JSON.stringify</c>).</summary>
    public sealed override string ToString() => ChordJson.Stringify(ToJson());
}

/// <summary>One matched pair of an <see cref="WhereNode.Exists"/>: a column of the set and the column of the enclosing row it equals.</summary>
/// <param name="SetColumn">The set's column.</param>
/// <param name="Column">The enclosing row's column, as its side names it.</param>
public readonly record struct ExistsPair(string SetColumn, string Column);

/// <summary>The operations of a <see cref="ExprNode.Binary"/> (upstream <c>Operation</c>, without <c>replacePrefix</c> and <c>when</c>,
/// which have nodes of their own).</summary>
internal enum Operation { Add, Sub, Max, Min, Concat, Coalesce }

/// <summary>A compiled value expression: a literal, a column, a <c>json(...)</c> cell, or an operation (upstream <c>ExprNode</c>); a null
/// <c>ExprNode?</c> is upstream's <c>null</c>, which clears. Immutable.</summary>
internal abstract record ExprNode
{
    private protected ExprNode()
    {
    }

    /// <summary>A scalar literal.</summary>
    internal sealed record Literal(Scalar Value) : ExprNode;

    /// <summary><c>{ col: column }</c>: the row's value of the column.</summary>
    internal sealed record Col(string Column) : ExprNode;

    /// <summary><c>{ json: value }</c>: a <c>json(...)</c> cell's value, held as given.</summary>
    internal sealed record Json(JsonNode? Value) : ExprNode;

    /// <summary><c>{ [op]: [a, b] }</c>.</summary>
    internal sealed record Binary(Operation Op, ExprNode? A, ExprNode? B) : ExprNode;

    /// <summary><c>{ replacePrefix: [subject, from, to] }</c>.</summary>
    internal sealed record ReplacePrefix(ExprNode? Subject, string From, string To) : ExprNode;

    /// <summary><c>{ when: [condition, a, b] }</c>.</summary>
    internal sealed record When(WhereNode Condition, ExprNode? A, ExprNode? B) : ExprNode;

    /// <summary><paramref name="node"/> in upstream's data form (<c>null</c> for <c>NULL</c>).</summary>
    internal static JsonNode? ToJson(ExprNode? node) => node switch
    {
        null => null,
        Literal literal => literal.Value.ToNode(),
        Col col => new JsonObject { ["col"] = col.Column },
        Json json => new JsonObject { ["json"] = json.Value?.DeepClone() },
        Binary binary => new JsonObject { [StoreInternal.Name(binary.Op)] = new JsonArray(ToJson(binary.A), ToJson(binary.B)) },
        ReplacePrefix replace => new JsonObject { ["replacePrefix"] = new JsonArray(ToJson(replace.Subject), replace.From, replace.To) },
        When conditional => new JsonObject { ["when"] = new JsonArray(conditional.Condition.ToJson(), ToJson(conditional.A), ToJson(conditional.B)) },
        _ => throw new InvalidOperationException("An unknown expression node."),
    };

    /// <summary>The node's JSON text.</summary>
    public sealed override string ToString() => ChordJson.Stringify(ToJson(this));
}

/// <summary>A compiled <c>set</c>: the new value of each changed column in the set's order, every right side evaluated against the row
/// before (upstream <c>SetNode</c>); a null value clears.</summary>
internal sealed class SetNode : OrderedDictionary<string, ExprNode?>
{
    internal SetNode()
        : base(StringComparer.Ordinal)
    {
    }

    /// <summary>The set in upstream's data form.</summary>
    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var (column, node) in this) json[column] = ExprNode.ToJson(node);
        return json;
    }
}

/// <summary>A value expression as a plugin builds it with <see cref="Expressions"/>; compiled when staged (upstream
/// <c>Expression</c>).</summary>
public class Expression
{
    private protected Expression(string op, IReadOnlyList<object?> args)
    {
        Op = op;
        Args = args;
    }

    internal static Expression Of(string op, IReadOnlyList<object?> args) => new(op, args);

    /// <summary>The operation (upstream <c>Operation | "col"</c>).</summary>
    internal string Op { get; }

    /// <summary>The operands as built: boxed <see cref="Operand"/>s, strings and conditions.</summary>
    internal IReadOnlyList<object?> Args { get; }
}

/// <summary>A reference to a column of the row, as a table's <c>Refs</c> give them to <c>t =&gt; ...</c> callbacks (upstream
/// <c>ColumnRef</c>). One of a column set (<see cref="ColumnSet.Refs"/>) carries the set's name: a condition names the side it means by it
/// (a query's own table, a joined set, an <c>Exists</c>).</summary>
public sealed class ColumnRef : Expression
{
    internal ColumnRef(string column, string? set = null)
        : base("col", [column])
    {
        Column = column;
        Set = set;
    }

    /// <summary>The column's name.</summary>
    public string Column { get; }

    /// <summary>The name of the column set the reference is of; null for a table's own reference.</summary>
    public string? Set { get; }
}

/// <summary>A condition as a plugin builds it with <see cref="Expressions"/>; compiled when staged or run (upstream <c>Condition</c>).</summary>
public sealed class Condition
{
    internal Condition(string op, IReadOnlyList<object?> args)
    {
        Op = op;
        Args = args;
    }

    /// <summary>The operator (upstream <c>Comparison | "inArray" | "isNull" | "prefix" | "and" | "or" | "not" | "exists" |
    /// "existsNode"</c>).</summary>
    internal string Op { get; }

    /// <summary>The operands as built: a column reference and a JsonNode value (or a list of them), or conditions.</summary>
    internal IReadOnlyList<object?> Args { get; }
}

/// <summary>An operand of an expression or a value of a <c>set</c>: an <see cref="Expression"/>, a plain value (a JsonNode; C# null is
/// JSON null), or <see cref="Undefined"/>, which clears an <c>Opt(...)</c> column (upstream's <c>Expression | T</c> and <c>undefined</c>).
/// Strings, numbers and booleans convert to plain values.</summary>
public readonly struct Operand
{
    private readonly object? value;
    private readonly bool defined;

    private Operand(object? value)
    {
        this.value = value;
        defined = true;
    }

    /// <summary>No value (upstream's <c>undefined</c>): in a <c>set</c>, it clears an <c>Opt(...)</c> column; in an expression it is
    /// <c>NULL</c>. The default Operand.</summary>
    public static Operand Undefined => default;

    /// <summary>An expression (a null one is JSON null).</summary>
    public static implicit operator Operand(Expression? expression) => new(expression);

    /// <summary>A plain value; null is JSON null.</summary>
    public static implicit operator Operand(JsonNode? value) => new(value);

    /// <summary>A text value; null is JSON null.</summary>
    public static implicit operator Operand(string? value) => new(value is null ? null : JsonValue.Create(value));

    /// <summary>A number value.</summary>
    public static implicit operator Operand(double value) => new(JsonValue.Create(value));

    /// <summary>A boolean value.</summary>
    public static implicit operator Operand(bool value) => new(JsonValue.Create(value));

    /// <summary>Whether this is <see cref="Undefined"/>.</summary>
    internal bool IsUndefined => !defined;

    /// <summary>Whether this is JSON null (a null value or expression).</summary>
    internal bool IsNull => defined && value is null;

    /// <summary>The expression, when this is one.</summary>
    internal Expression? Expression => value as Expression;

    /// <summary>The plain value, when this is one (null for JSON null).</summary>
    internal JsonNode? Value => value as JsonNode;

    /// <summary>An argument as an operand: an Operand itself, otherwise a plain value of any other CLR value (a hand-built argument).</summary>
    internal static Operand From(object? argument) => argument switch
    {
        Operand operand => operand,
        null => new Operand(null),
        Expression expression => expression,
        JsonNode node => node,
        _ => new Operand(JsonValue.Create(argument)),
    };
}

/// <summary>The builders plugins write <c>where</c> and <c>set</c> with (upstream <c>expression.ts</c>'s functions): conditions over a
/// table's column references, and value expressions.</summary>
public static class Expressions
{
    /// <summary>The column equals <paramref name="value"/> (<c>NULL</c> never matches).</summary>
    public static Condition Eq(ColumnRef column, object? value) => new("eq", [column, value]);

    /// <summary>The column differs from <paramref name="value"/> (<c>NULL</c> never matches).</summary>
    public static Condition Ne(ColumnRef column, object? value) => new("ne", [column, value]);

    /// <summary>The column is below <paramref name="value"/>.</summary>
    public static Condition Lt(ColumnRef column, JsonNode? value) => new("lt", [column, value]);

    /// <summary>The column is at most <paramref name="value"/>.</summary>
    public static Condition Lte(ColumnRef column, JsonNode? value) => new("lte", [column, value]);

    /// <summary>The column is above <paramref name="value"/>.</summary>
    public static Condition Gt(ColumnRef column, JsonNode? value) => new("gt", [column, value]);

    /// <summary>The column is at least <paramref name="value"/>.</summary>
    public static Condition Gte(ColumnRef column, JsonNode? value) => new("gte", [column, value]);

    /// <summary>The column equals one of <paramref name="values"/> (<c>[]</c> matches nothing).</summary>
    public static Condition InArray(ColumnRef column, IEnumerable<JsonNode?> values) => new("inArray", [column, values]);

    /// <summary>The host column equals one of the addresses.</summary>
    public static Condition InArray(ColumnRef column, IEnumerable<Address> values) => new("inArray", [column, values.Select(value => (JsonNode?)value.ToJson())]);

    /// <summary>The column is <c>NULL</c> (an <c>Opt(...)</c> column without a value).</summary>
    public static Condition IsNull(ColumnRef column) => new("isNull", [column]);

    /// <summary>The text column starts with <paramref name="prefix"/>, compared by code point (<c>""</c> matches every value).</summary>
    public static Condition Prefix(ColumnRef column, string prefix) => new("prefix", [column, prefix is null ? null : JsonValue.Create(prefix)]);

    /// <summary>Every condition holds (<c>And()</c> always does).</summary>
    public static Condition And(params Condition[] conditions) => new("and", [.. conditions]);

    /// <summary>Some condition holds (<c>Or()</c> never does).</summary>
    public static Condition Or(params Condition[] conditions) => new("or", [.. conditions]);

    /// <summary>The condition does not hold (<c>NULL</c> stays <c>NULL</c>: it never matches).</summary>
    public static Condition Not(Condition condition) => new("not", [condition]);

    /// <summary>A match of an <see cref="Exists(ColumnSet, IReadOnlyDictionary{string, ColumnRef}, Func{IReadOnlyDictionary{string, ColumnRef}, Condition}?)"/>
    /// or a join, pair by pair in the order given: <c>(setColumn, column)</c> (upstream's object literal <c>{ setColumn: column }</c>).</summary>
    public static IReadOnlyDictionary<string, ColumnRef> Match(params (string SetColumn, ColumnRef Column)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var match = new OrderedDictionary<string, ColumnRef>(StringComparer.Ordinal);
        foreach (var (setColumn, column) in pairs) match[setColumn] = column;
        return match;
    }

    /// <summary><paramref name="set"/>'s table has a row equal to the enclosing row in the matched columns (a leading prefix of the set's
    /// key or of one of its declared indexes) that satisfies <paramref name="inner"/>. In queries only (upstream <c>exists</c>).</summary>
    public static Condition Exists(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match,
        Func<IReadOnlyDictionary<string, ColumnRef>, Condition>? inner = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new("exists", [set, match, inner?.Invoke(set.Refs)]);
    }

    /// <summary><see cref="Exists"/> with its inner condition as data (<see cref="Tables.Compile"/>'s result in its JSON form): for a
    /// condition handed over as an argument. A trust boundary: <paramref name="inner"/> is whatever arrived, and is checked whole before
    /// anything is rendered (upstream <c>existsNode</c>).</summary>
    public static Condition ExistsNode(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match, JsonNode? inner)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new("existsNode", [set, match, inner]);
    }

    /// <summary><see cref="ExistsNode(ColumnSet, IReadOnlyDictionary{string, ColumnRef}, JsonNode?)"/> for a compiled node: checked as its
    /// JSON form is.</summary>
    public static Condition ExistsNode(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match, WhereNode inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return ExistsNode(set, match, inner.ToJson());
    }

    /// <summary><c>a + b</c> over integers; a result outside ±(2^53 - 1) fails (rule 10).</summary>
    public static Expression Add(Operand a, Operand b) => Expression.Of("add", [a, b]);

    /// <summary><c>a - b</c> over integers; a result outside ±(2^53 - 1) fails (rule 10).</summary>
    public static Expression Sub(Operand a, Operand b) => Expression.Of("sub", [a, b]);

    /// <summary>The larger of <paramref name="a"/> and <paramref name="b"/> (<c>int</c> or <c>num</c>).</summary>
    public static Expression Max(Operand a, Operand b) => Expression.Of("max", [a, b]);

    /// <summary>The smaller of <paramref name="a"/> and <paramref name="b"/> (<c>int</c> or <c>num</c>).</summary>
    public static Expression Min(Operand a, Operand b) => Expression.Of("min", [a, b]);

    /// <summary><paramref name="a"/> followed by <paramref name="b"/>.</summary>
    public static Expression Concat(Operand a, Operand b) => Expression.Of("concat", [a, b]);

    /// <summary><paramref name="value"/> with its prefix <paramref name="from"/> replaced by <paramref name="to"/>; a value without the
    /// prefix stays as it is.</summary>
    public static Expression ReplacePrefix(Operand value, string from, string to) => Expression.Of("replacePrefix", [value, from, to]);

    /// <summary><paramref name="a"/> unless it is <c>NULL</c>, else <paramref name="b"/>.</summary>
    public static Expression Coalesce(Expression a, Operand b) => Expression.Of("coalesce", [(Operand)a, b]);

    /// <summary><paramref name="a"/> where <paramref name="condition"/> holds for the row, else <paramref name="b"/>.</summary>
    public static Expression When(Condition condition, Operand a, Operand b) => Expression.Of("when", [condition, a, b]);
}

internal static partial class StoreInternal
{
    /// <summary>A comparison's name: <c>eq</c>, <c>ne</c>, <c>lt</c>, <c>lte</c>, <c>gt</c> or <c>gte</c>.</summary>
    internal static string Name(Comparison op) => op switch
    {
        Comparison.Eq => "eq",
        Comparison.Ne => "ne",
        Comparison.Lt => "lt",
        Comparison.Lte => "lte",
        Comparison.Gt => "gt",
        _ => "gte",
    };

    /// <summary>An operation's name: <c>add</c>, <c>sub</c>, <c>max</c>, <c>min</c>, <c>concat</c> or <c>coalesce</c>.</summary>
    internal static string Name(Operation op) => op switch
    {
        Operation.Add => "add",
        Operation.Sub => "sub",
        Operation.Max => "max",
        Operation.Min => "min",
        Operation.Concat => "concat",
        _ => "coalesce",
    };

    private static readonly Dictionary<string, Comparison> Comparisons = new(StringComparer.Ordinal)
    {
        ["eq"] = Comparison.Eq,
        ["ne"] = Comparison.Ne,
        ["lt"] = Comparison.Lt,
        ["lte"] = Comparison.Lte,
        ["gt"] = Comparison.Gt,
        ["gte"] = Comparison.Gte,
    };

    /// <summary>One <see cref="ColumnRef"/> per column, for a table's <c>t =&gt; ...</c> callbacks (upstream <c>columnRefs</c>).</summary>
    internal static IReadOnlyDictionary<string, ColumnRef> ColumnRefs(IEnumerable<Column> columns) =>
        columns.ToDictionary(column => column.Name, column => new ColumnRef(column.Name), StringComparer.Ordinal);

    /// <summary>The scope of one table: its own references, and those of the set it exposes (upstream <c>tableScope</c>).
    /// <paramref name="exists"/>: whether a condition here may test <c>exists</c> (a query's may, a write's may not).</summary>
    internal static Scope TableScope(TableDefinition table, bool exists) => new(table.Name, exists, reference =>
    {
        if (reference.Set is not null && reference.Set != table.Exposes?.Name) return null;
        return table.ByName.TryGetValue(reference.Column, out var column) ? new Resolved(column, column.Name) : null;
    });

    /// <summary>The scope of a column set alone: its own references (<c>Compile</c>, an <c>exists</c>' inner condition; upstream
    /// <c>setScope</c>).</summary>
    internal static Scope SetScope(ISide set) => new(set.Name, true, reference =>
        reference.Set == set.Name && set.ByName.TryGetValue(reference.Column, out var column) ? new Resolved(column, column.Name) : null);

    /// <summary>The name of a joined set's column in compiled nodes, orders and cursors: <c>&lt;set&gt;:&lt;column&gt;</c> (upstream
    /// <c>partnerName</c>).</summary>
    internal static string PartnerName(string set, string column) => $"{set}:{column}";

    /// <summary>The scope of a query of <paramref name="own"/> joined with <paramref name="set"/>: the partner's columns are named
    /// <c>&lt;set&gt;:&lt;column&gt;</c> (upstream <c>joinedScope</c>).</summary>
    internal static Scope JoinedScope(Scope own, ISide set) => new(own.Name, true, reference =>
    {
        if (reference.Set != set.Name) return own.Resolve(reference);
        return set.ByName.TryGetValue(reference.Column, out var column) ? new Resolved(column, PartnerName(set.Name, column.Name)) : null;
    });

    /// <summary>Compiles a plugin's condition over <paramref name="table"/>'s columns for a write (a trust boundary: plugin values): known
    /// scalar columns, operands of their type made storable (<c>invalid/args</c> otherwise, null included: ask with <c>IsNull</c>, rule 16).
    /// No <c>exists</c> (upstream <c>compileWhere</c>).</summary>
    internal static WhereNode CompileWhere(TableDefinition table, object? condition, string what) =>
        CompileCondition(TableScope(table, false), condition, what);

    /// <summary>Compiles a plugin's condition in <paramref name="scope"/> (a trust boundary), as <see cref="CompileWhere"/> does for one
    /// table. The set columns its <c>exists</c> use are added to <paramref name="expected"/> (upstream <c>compileCondition</c>).</summary>
    internal static WhereNode CompileCondition(Scope scope, object? condition, string what, Dictionary<string, Dictionary<string, Column>>? expected = null)
    {
        if (condition is not Condition built) throw DataInternal.Invalid("args", $"{scope.Name}: {what}: expected a condition");
        expected ??= new Dictionary<string, Dictionary<string, Column>>(StringComparer.Ordinal);
        var args = built.Args;
        object? Arg(int index) => index < args.Count ? args[index] : null;
        switch (built.Op)
        {
            case "and":
            case "or":
            {
                var nodes = args.Select(each => CompileCondition(scope, each, what, expected)).ToList();
                return built.Op == "and" ? new WhereNode.And(nodes) : new WhereNode.Or(nodes);
            }
            case "not":
                return new WhereNode.Not(CompileCondition(scope, Arg(0), what, expected));
            case "exists":
            case "existsNode":
                return CompileExists(scope, built, what, expected);
            case "isNull":
                return new WhereNode.IsNull(Ordered(scope, ScalarColumn(scope, Arg(0), what), "isNull", what).Name);
            case "prefix":
            {
                var resolved = Ordered(scope, ScalarColumn(scope, Arg(0), what), "prefix", what);
                if (resolved.Column.Type != ColumnType.Str) throw DataInternal.Invalid("args", $"{scope.Name}: {what}: prefix needs a str() column");
                return new WhereNode.Prefix(resolved.Name, (string)OperandOf(resolved.Column, Arg(1), what).Value);
            }
            case "inArray":
            {
                var resolved = ScalarColumn(scope, Arg(0), what);
                if (Arg(1) is not IEnumerable<JsonNode?> values) throw DataInternal.Invalid("args", $"{scope.Name}: {what}: inArray needs an array");
                return new WhereNode.InArray(resolved.Name, [.. values.Select(value => OperandOf(resolved.Column, value, what))]);
            }
            default:
            {
                if (!Comparisons.TryGetValue(built.Op, out var op)) throw new ArgumentException($"An unknown condition operator \"{built.Op}\".");
                var resolved = ScalarColumn(scope, Arg(0), what);
                if (op is not (Comparison.Eq or Comparison.Ne)) Ordered(scope, resolved, built.Op, what);
                // One of the six comparisons: the node's shape is { [op]: [column, value] }.
                return new WhereNode.Compare(op, resolved.Name, OperandOf(resolved.Column, Arg(1), what));
            }
        }
    }

    /// <summary>How deep a condition handed over as data may nest (<c>and</c>, <c>or</c>, <c>not</c>, <c>exists</c>; upstream
    /// <c>NODE_DEPTH</c>).</summary>
    internal const int NodeDepth = 16;

    /// <summary>Compiles an <c>exists</c> of <paramref name="scope"/>: the set's matched columns are a leading prefix of its key or of one of
    /// its declared indexes, each of the type (and host kind) of the enclosing column it is matched to; the inner condition is the
    /// callback's, compiled over the set, or a node, checked as <c>WhereNode</c> checks one (upstream <c>compileExists</c>).</summary>
    private static WhereNode.Exists CompileExists(Scope scope, Condition condition, string what, Dictionary<string, Dictionary<string, Column>> expected)
    {
        // The builders' arguments (Exists, ExistsNode): plugin values, read as unknown data.
        var args = condition.Args;
        if (!scope.Exists) throw DataInternal.Invalid("args", $"{scope.Name}: {what}: exists is for queries, not for writes");
        if (args.Count < 2 || args[0] is not ColumnSet set || args[1] is not IEnumerable<KeyValuePair<string, ColumnRef>> match)
            throw DataInternal.Invalid("args", $"{scope.Name}: {what}: exists needs a match");
        var inner = args.Count > 2 ? args[2] : null;
        var pairs = new List<ExistsPair>();
        foreach (var (name, reference) in match)
        {
            var column = set.ByName.GetValueOrDefault(name);
            var outer = reference is null ? null : scope.Resolve(reference);
            if (column is null || outer is null)
                throw DataInternal.Invalid("args", $"{scope.Name}: {what}: exists({set.Name}): \"{name}\" is matched to no column");
            CheckPair(set.Name, column, outer.Column, $"{scope.Name}: {what}");
            pairs.Add(new ExistsPair(name, outer.Name));
        }
        CheckMatch(set, [.. pairs.Select(pair => pair.SetColumn)], $"{scope.Name}: {what}");
        WhereNode? node = null;
        if (condition.Op == "existsNode") node = CheckNode(inner as JsonNode, set, $"{scope.Name}: {what}: existsNode({set.Name})");
        else if (inner is not null) node = CompileCondition(SetScope(set), inner, what, expected);
        // What this exists uses of the set, as its author's set declares it.
        if (!expected.TryGetValue(set.Name, out var columns)) expected[set.Name] = columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (var name in pairs.Select(pair => pair.SetColumn).Concat(node is null ? [] : ColumnsOf(node))) columns[name] = set.ByName[name];
        return new WhereNode.Exists(set.Name, pairs, node);
    }

    /// <summary>The columns a condition uses of its own side: the ones it compares, and the ones its <c>exists</c> are matched to. An
    /// <c>exists</c>' inner condition is another side's (upstream <c>columnsOf</c>).</summary>
    internal static IEnumerable<string> ColumnsOf(WhereNode node) => node switch
    {
        WhereNode.And all => all.Nodes.SelectMany(ColumnsOf),
        WhereNode.Or any => any.Nodes.SelectMany(ColumnsOf),
        WhereNode.Not negated => ColumnsOf(negated.Node),
        WhereNode.IsNull isNull => [isNull.Column],
        WhereNode.Exists exists => exists.Pairs.Select(pair => pair.Column),
        WhereNode.InArray inArray => [inArray.Column],
        WhereNode.Prefix prefix => [prefix.Column],
        WhereNode.Compare compare => [compare.Column],
        _ => throw new InvalidOperationException("An unknown condition node."),
    };

    /// <summary>The <c>exists</c> a condition tests on its own side, not the ones nested inside them (upstream <c>existsIn</c>).</summary>
    internal static IEnumerable<WhereNode.Exists> ExistsIn(WhereNode node) => node switch
    {
        WhereNode.And all => all.Nodes.SelectMany(ExistsIn),
        WhereNode.Or any => any.Nodes.SelectMany(ExistsIn),
        WhereNode.Not negated => ExistsIn(negated.Node),
        WhereNode.Exists exists => [exists],
        _ => [],
    };

    /// <summary>Whether <paramref name="key"/> is the canonical stored key of an address of <paramref name="host"/>'s kind.</summary>
    private static bool IsHostKey(HostColumn host, string key)
    {
        try
        {
            return host.Key(host.Address(key), "") == key;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Refuses a match that is no leading prefix of <paramref name="set"/>'s key or of one of its declared indexes
    /// (<c>invalid/args</c>, listing them): two tables are matched only by such an equality (the bounding rule, slice 14 §5; upstream
    /// <c>checkMatch</c>).</summary>
    internal static void CheckMatch(IMatchedSet set, IReadOnlyList<string> matched, string what)
    {
        bool Fits(IReadOnlyList<string> path) =>
            matched.Count > 0 && matched.Count <= path.Count && path.Take(matched.Count).All(column => matched.Contains(column));
        if (Fits(set.Key) || set.Indexes.Values.Any(Fits)) return;
        var listed = new[] { $"key ({string.Join(", ", set.Key)})" }.Concat(set.Indexes.Select(index => $"{index.Key} ({string.Join(", ", index.Value)})"));
        throw DataInternal.Invalid("args",
            $"{what}: {set.Name} is matched by ({string.Join(", ", matched)}), no leading prefix of its {string.Join(" or ", listed)}");
    }

    /// <summary>Refuses a pair of matched columns of different types or host kinds (<c>invalid/args</c>, naming the set's column; upstream
    /// <c>checkPair</c>).</summary>
    internal static void CheckPair(string set, Column column, Column outer, string what)
    {
        var (a, b) = (RecordOf(column), RecordOf(outer));
        if (a.Type == ColumnType.Json || a.Type != b.Type || a.Host != b.Host)
            throw DataInternal.Invalid("args", $"{what}: {set} column \"{column.Name}\" and the column it is matched to differ in type");
    }

    /// <summary>Reads a condition handed over as data against <paramref name="side"/>'s columns (a trust boundary, slice 14 §6): one known
    /// kind per node, operand shapes and types (a <c>host(...)</c> column's operands are stored keys), every column in the side, a depth
    /// bound. The inner condition of a nested <c>exists</c> is over another set: its structure is checked here, its columns when the read
    /// opens and the set's table is known. <c>invalid/args</c> otherwise (upstream <c>checkNode</c>, which asserts the shape; here the
    /// checked value is returned as the typed node).</summary>
    internal static WhereNode CheckNode(JsonNode? node, ISide? side, string what, int depth = 0)
    {
        ChordError Refuse(string message) => DataInternal.Invalid("args", $"{what}: {message}");
        if (depth > NodeDepth) throw Refuse($"nested deeper than {NodeDepth}");
        if (ChordJson.AsObject(node) is not { } fields) throw Refuse("a condition node is an object");
        if (fields.Count != 1) throw Refuse("a condition node has one kind");
        var (kind, value) = fields.First();
        Column? ColumnOf(JsonNode? name, out string text)
        {
            text = ChordJson.AsString(name) ?? throw Refuse("a column is named by a string");
            Column? found = null;
            if (side is not null && !side.ByName.TryGetValue(text, out found)) throw Refuse($"no column \"{text}\" in {side.Name}");
            if (found?.Type == ColumnType.Json) throw Refuse($"json(...) column \"{text}\" is not queryable");
            return found;
        }
        Scalar Operand(Column? of, JsonNode? each)
        {
            var primitive = each is JsonValue held && ChordJson.Primitive(held, out var read) ? read : null;
            if (primitive is not (string or bool) && !(primitive is double number && double.IsFinite(number)))
                throw Refuse("an operand is a string, a number or a boolean");
            if (of is null) return Scalar.FromPrimitive(primitive!);
            var scalar = CheckScalar(of.Type, each, what);
            // A host(...) column holds canonical address keys: anything else would silently match nothing.
            if (of.Type == ColumnType.Str && of.Host is { } host && !IsHostKey(host, (string)scalar.Value))
                throw Refuse($"\"{scalar.Value}\" is no address key of {host.Kind.Kind} (column \"{of.Name}\")");
            return scalar;
        }
        // A host(...) column compares only with eq, ne and inArray, as Compile has it.
        void OrderedOnly(Column? of)
        {
            if (of is { Type: ColumnType.Str, Host: not null }) throw Refuse($"{kind} on host(...) column \"{of.Name}\"");
        }
        JsonArray Pair() => ChordJson.AsArray(value) is { Count: 2 } pair ? pair : throw Refuse($"{kind} takes [column, operand]");
        switch (kind)
        {
            case "and":
            case "or":
            {
                if (ChordJson.AsArray(value) is not { } each) throw Refuse($"{kind} takes an array");
                List<WhereNode> nodes = [.. each.Select(item => CheckNode(item, side, what, depth + 1))];
                return kind == "and" ? new WhereNode.And(nodes) : new WhereNode.Or(nodes);
            }
            case "not":
                return new WhereNode.Not(CheckNode(value, side, what, depth + 1));
            case "isNull":
            {
                OrderedOnly(ColumnOf(value, out var name));
                return new WhereNode.IsNull(name);
            }
            case "inArray":
            {
                var pair = Pair();
                var of = ColumnOf(pair[0], out var name);
                if (ChordJson.AsArray(pair[1]) is not { } values) throw Refuse("inArray takes an array of operands");
                return new WhereNode.InArray(name, [.. values.Select(each => Operand(of, each))]);
            }
            case "prefix":
            {
                var pair = Pair();
                var of = ColumnOf(pair[0], out var name);
                OrderedOnly(of);
                if (ChordJson.AsString(pair[1]) is not { } text || (of is not null && of.Type != ColumnType.Str))
                    throw Refuse("prefix takes a str() column and a string");
                return new WhereNode.Prefix(name, text);
            }
            case "eq":
            case "ne":
            {
                var pair = Pair();
                var of = ColumnOf(pair[0], out var name);
                return new WhereNode.Compare(Comparisons[kind], name, Operand(of, pair[1]));
            }
            case "lt":
            case "lte":
            case "gt":
            case "gte":
            {
                var pair = Pair();
                var of = ColumnOf(pair[0], out var name);
                OrderedOnly(of);
                return new WhereNode.Compare(Comparisons[kind], name, Operand(of, pair[1]));
            }
            case "exists":
            {
                if (ChordJson.AsArray(value) is not { Count: 3 } parts) throw Refuse("exists takes [set, pairs, inner]");
                if (ChordJson.AsString(parts[0]) is not { } set || ChordJson.AsArray(parts[1]) is not { Count: > 0 } pairs)
                    throw Refuse("exists takes a set name and its matched pairs");
                var matched = new List<ExistsPair>();
                foreach (var each in pairs)
                {
                    if (ChordJson.AsArray(each) is not { Count: 2 } pair || ChordJson.AsString(pair[0]) is not { } setColumn)
                        throw Refuse("an exists pair is [setColumn, column]");
                    ColumnOf(pair[1], out var column);
                    matched.Add(new ExistsPair(setColumn, column));
                }
                return new WhereNode.Exists(set, matched, parts[2] is null ? null : CheckNode(parts[2], null, what, depth + 1));
            }
            default:
                throw Refuse($"unknown kind \"{kind}\"");
        }
    }

    /// <summary>Compiles a plugin's <c>set</c> (plain values or a callback's result) for <paramref name="table"/> (a trust boundary): known
    /// non-key columns (rule 13), at least one (rule 14), <see cref="Operand.Undefined"/> clearing an <c>Opt(...)</c> column as the explicit
    /// null (a JSON null value is a <c>json(...)</c> cell's JSON null, refused for other columns), an expression whose result may be
    /// <c>NULL</c> only for an <c>Opt(...)</c> column; integer arithmetic only on <c>int</c> columns (upstream <c>compileSet</c>).</summary>
    internal static SetNode CompileSet(TableDefinition table, object? set, string what)
    {
        List<KeyValuePair<string, Operand>> entries = set switch
        {
            IReadOnlyDictionary<string, Operand> map => [.. map],
            JsonNode plain when ChordJson.AsObject(plain) is { } values =>
                [.. ChordJson.KeysInJavaScriptOrder(values).Select(name => KeyValuePair.Create(name, (Operand)values[name]))],
            _ => throw DataInternal.Invalid("args", $"{table.Name}: {what}: expected an object of column values"),
        };
        if (entries.Count == 0) throw DataInternal.Invalid("args", $"{table.Name}: {what}: the set is empty");
        var node = new SetNode();
        foreach (var (name, value) in entries)
        {
            if (!table.ByName.TryGetValue(name, out var column)) throw DataInternal.Invalid("args", $"{table.Name}: {what}: no column \"{name}\"");
            if (table.Key.Contains(name))
                throw DataInternal.Invalid("args", $"{table.Name}: {what}: key column \"{name}\" cannot change (delete and put)");
            var where = $"{what}: column \"{name}\"";
            if (value.IsUndefined)
            {
                if (!column.Optional) throw DataInternal.Invalid("args", $"{table.Name}: {where} is not opt(...) and cannot be cleared");
                node[name] = null;
            }
            else if (value.IsNull && column.Type != ColumnType.Json)
            {
                // A row never holds an explicit null (decision "Nullable columns and the set form"); a JSON null is a value.
                throw DataInternal.Invalid("args", $"{table.Name}: {where}: null is no {Name(column.Type)} value (undefined clears)");
            }
            else if (value.Expression is not null)
            {
                if (column.Type == ColumnType.Json || column.Host is not null) throw DataInternal.Invalid("args", $"{table.Name}: {where}: {(column.Host is null ? "json" : "host")}(...) columns take plain values");
                var compiled = CompileExpr(table, value, column.Type, where);
                if (compiled.Nullable && !column.Optional)
                    throw DataInternal.Invalid("args", $"{table.Name}: {where}: the expression may be NULL; the column is not opt(...)");
                node[name] = compiled.Node;
            }
            else if (column.Type == ColumnType.Json) node[name] = new ExprNode.Json(EncodeCell(column, value.Value, $"{table.Name}: {where}").Stored);
            else node[name] = new ExprNode.Literal(column.Host is { } host ? (Scalar)host.Key(value.Value, $"{table.Name}: {where}") : CheckScalar(column.Type, value.Value, $"{table.Name}: {where}"));
        }
        return node;
    }

    /// <summary>A compiled expression and whether its result may be <c>NULL</c>.</summary>
    private readonly record struct Compiled(ExprNode? Node, bool Nullable);

    private static Compiled CompileExpr(TableDefinition table, Operand value, ColumnType type, string what)
    {
        if (value.Expression is not { } expression)
        {
            if (value.IsUndefined || value.IsNull) return new Compiled(null, true);
            return new Compiled(new ExprNode.Literal(CheckScalar(type, value.Value, $"{table.Name}: {what}")), false);
        }
        var args = expression.Args;
        Operand Arg(int index) => Operand.From(index < args.Count ? args[index] : null);
        ChordError Refuse(string message) => DataInternal.Invalid("args", $"{table.Name}: {what}: {message}");
        switch (expression.Op)
        {
            case "col":
            {
                var name = args.Count > 0 ? args[0] as string : null;
                if (name is null || !table.ByName.TryGetValue(name, out var column)) throw Refuse($"no column \"{name ?? "undefined"}\"");
                if (column.Host is not null) throw Refuse($"host(...) column \"{column.Name}\" holds addresses");
                if (column.Type != type) throw Refuse($"column \"{column.Name}\" is {Name(column.Type)}, not {Name(type)}");
                return new Compiled(new ExprNode.Col(column.Name), column.Optional);
            }
            case "add":
            case "sub":
            case "max":
            case "min":
            case "concat":
            {
                ColumnType[] types = expression.Op == "concat" ? [ColumnType.Str] : expression.Op is "add" or "sub" ? [ColumnType.Int] : [ColumnType.Int, ColumnType.Num];
                if (!types.Contains(type)) throw Refuse($"{expression.Op} gives {string.Join(" or ", types.Select(Name))}, the column is {Name(type)}");
                var (a, b) = (CompileExpr(table, Arg(0), type, what), CompileExpr(table, Arg(1), type, what));
                var op = expression.Op switch { "add" => Operation.Add, "sub" => Operation.Sub, "max" => Operation.Max, "min" => Operation.Min, _ => Operation.Concat };
                // The operation's shape is { [op]: [a, b] }.
                return new Compiled(new ExprNode.Binary(op, a.Node, b.Node), a.Nullable || b.Nullable);
            }
            case "coalesce":
            {
                var (a, b) = (CompileExpr(table, Arg(0), type, what), CompileExpr(table, Arg(1), type, what));
                return new Compiled(new ExprNode.Binary(Operation.Coalesce, a.Node, b.Node), a.Nullable && b.Nullable);
            }
            case "replacePrefix":
            {
                if (type != ColumnType.Str) throw Refuse($"replacePrefix gives str, the column is {Name(type)}");
                var subject = CompileExpr(table, Arg(0), type, what);
                var bounds = new[] { 1, 2 }.Select(index => (string)CheckScalar(ColumnType.Str, Text(index < args.Count ? args[index] : null), $"{table.Name}: {what}").Value).ToList();
                return new Compiled(new ExprNode.ReplacePrefix(subject.Node, bounds[0], bounds[1]), subject.Nullable);
            }
            case "when":
            {
                var condition = CompileWhere(table, args.Count > 0 ? args[0] : null, what);
                var (a, b) = (CompileExpr(table, Arg(1), type, what), CompileExpr(table, Arg(2), type, what));
                return new Compiled(new ExprNode.When(condition, a.Node, b.Node), a.Nullable || b.Nullable);
            }
            default:
                throw new ArgumentException($"An unknown expression operation \"{expression.Op}\".");
        }
    }

    /// <summary>A replacePrefix bound as a value: a string, or the hand-built value itself.</summary>
    private static JsonNode? Text(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        JsonNode node => node,
        _ => JsonValue.Create(value),
    };

    /// <summary>The column a condition compares: a reference to a known scalar column of <paramref name="scope"/> (upstream
    /// <c>scalarColumn</c>).</summary>
    private static Resolved ScalarColumn(Scope scope, object? reference, string what)
    {
        if (reference is not ColumnRef column) throw DataInternal.Invalid("args", $"{scope.Name}: {what}: expected a column reference");
        var resolved = scope.Resolve(column) ?? throw DataInternal.Invalid("args", $"{scope.Name}: {what}: no column \"{column.Column}\"");
        if (resolved.Column.Type == ColumnType.Json)
            throw DataInternal.Invalid("args", $"{scope.Name}: {what}: json(...) column \"{column.Column}\" is not queryable");
        return resolved;
    }

    /// <summary><paramref name="resolved"/> for a comparison by order, <c>isNull</c> or <c>prefix</c>: a <c>host(...)</c> column compares
    /// only with eq, ne, inArray (upstream <c>ordered</c>).</summary>
    private static Resolved Ordered(Scope scope, Resolved resolved, string operation, string what)
    {
        if (resolved.Column.Host is not null)
            throw DataInternal.Invalid("args", $"{scope.Name}: {what}: {operation} on host(...) column \"{resolved.Column.Name}\" (eq, ne, inArray only)");
        return resolved;
    }

    /// <summary>An operand compared with <paramref name="column"/>, made storable (an address as its key for a <c>host(...)</c> column);
    /// null is refused (rule 16; upstream <c>operand</c>).</summary>
    private static Scalar OperandOf(Column column, object? value, string what)
    {
        if (value is null) throw DataInternal.Invalid("args", $"{what}: column \"{column.Name}\" compared with null; ask with isNull");
        if (column.Host is { } host) return host.Key(value, $"{what}: column \"{column.Name}\"");
        // ScalarColumn refused json columns.
        return CheckScalar(column.Type, value as JsonNode ?? JsonValue.Create(value), $"{what}: column \"{column.Name}\"");
    }
}

/// <summary>A column as a scope resolved it (upstream <c>Resolved</c>): the column and its name in compiled nodes.</summary>
internal sealed record Resolved(Column Column, string Name);

/// <summary>Where a condition's columns are looked up: one side of a query, or two for a joined one (upstream <c>Scope</c>).</summary>
/// <param name="name">Named in errors.</param>
/// <param name="exists">Whether a condition here may test <c>exists</c> (a query's may, a write's may not).</param>
/// <param name="resolve">The column a reference names here and its name in compiled nodes; null when it is of no side of this scope.</param>
internal sealed class Scope(string name, bool exists, Func<ColumnRef, Resolved?> resolve)
{
    internal string Name => name;

    internal bool Exists => exists;

    internal Resolved? Resolve(ColumnRef reference) => resolve(reference);
}
