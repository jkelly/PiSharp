// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/evaluate.ts
//
// The C# evaluator of the compiled table vocabulary (slice 6a §6; rule 10): the tx.get overlay matches WhereNode conditions and applies
// SetNode assignments with SQL's three-valued NULL rules and code point text order, as the SQL that Sql.cs renders from the same nodes does
// in SQLite. A row is a map of stored cells (upstream EvaluatedRow): an absent column is NULL, a scalar cell a JsonValue of its string,
// double or bool, a json(...) cell its JSON value (held by reference, R9).
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

internal static partial class StoreInternal
{
    /// <summary>Whether <paramref name="row"/> matches <paramref name="node"/> under SQL's rules: unknown (<c>NULL</c>) does not match
    /// (upstream <c>matches</c>).</summary>
    internal static bool Matches(WhereNode node, IReadOnlyDictionary<string, JsonNode?> row) => Truth(node, row) == true;

    /// <summary>SQL's three-valued truth: <c>null</c> is unknown (a comparison with <c>NULL</c>).</summary>
    private static bool? Truth(WhereNode node, IReadOnlyDictionary<string, JsonNode?> row)
    {
        switch (node)
        {
            case WhereNode.And all:
                return all.Nodes.Aggregate<WhereNode, bool?>(true, (truth, each) => truth == false ? false : Both(truth, Truth(each, row)));
            case WhereNode.Or any:
                return any.Nodes.Aggregate<WhereNode, bool?>(false, (truth, each) => truth == true ? true : Either(truth, Truth(each, row)));
            case WhereNode.Not negated:
                return Truth(negated.Node, row) is { } inner ? !inner : null;
            case WhereNode.IsNull isNull:
                return !row.ContainsKey(isNull.Column);
            // The row is checked against the table the condition was compiled for: its columns hold scalars.
            case WhereNode.InArray inArray:
            {
                // SQL: x IN () is false, for a NULL x too.
                if (inArray.Values.Count == 0) return false;
                return CellOf(row, inArray.Column) is { } value ? inArray.Values.Any(each => CompareScalar(value, each) == 0) : null;
            }
            case WhereNode.Prefix prefix:
                return CellOf(row, prefix.Column) is { } text ? ((string)text.Value).StartsWith(prefix.Value, StringComparison.Ordinal) : null;
            case WhereNode.Compare compare:
            {
                if (CellOf(row, compare.Column) is not { } value) return null;
                var order = CompareScalar(value, compare.Value);
                return compare.Op switch
                {
                    Comparison.Eq => order == 0,
                    Comparison.Ne => order != 0,
                    Comparison.Lt => order < 0,
                    Comparison.Lte => order <= 0,
                    Comparison.Gt => order > 0,
                    _ => order >= 0,
                };
            }
            default:
                throw new InvalidOperationException("An unknown condition node.");
        }
    }

    private static bool? Both(bool? a, bool? b)
    {
        if (a == false || b == false) return false;
        return a is null || b is null ? null : true;
    }

    private static bool? Either(bool? a, bool? b)
    {
        if (a == true || b == true) return true;
        return a is null || b is null ? null : false;
    }

    /// <summary>The scalar cell of <paramref name="column"/>; null when absent (<c>NULL</c>).</summary>
    private static Scalar? CellOf(IReadOnlyDictionary<string, JsonNode?> row, string column) =>
        row.TryGetValue(column, out var node) ? ScalarOf(node) : null;

    /// <summary>A stored scalar cell as a <see cref="Scalar"/>; null for anything else.</summary>
    private static Scalar? ScalarOf(JsonNode? node) =>
        node is JsonValue value && ChordJson.Primitive(value, out var primitive) && primitive is not null ? (Scalar?)Scalar.FromPrimitive(primitive) : null;

    /// <summary>Orders two cells of one column as SQLite does: text by code point, booleans as 0 and 1, numbers numerically (upstream
    /// <c>compareScalar</c>: the sign of the result is the order; NaN for none).</summary>
    internal static double CompareScalar(Scalar a, Scalar b)
    {
        if (a.Value is string text) return Tables.CompareText(text, (string)b.Value);
        return Number(a) - Number(b);
    }

    /// <summary><c>Number(scalar)</c>: a boolean is 0 or 1.</summary>
    private static double Number(Scalar scalar) => scalar.Value switch
    {
        double number => number,
        bool flag => flag ? 1 : 0,
        _ => double.NaN,
    };

    /// <summary><paramref name="row"/> after <paramref name="set"/>, every right side evaluated against <paramref name="row"/>
    /// (simultaneous: <c>{ a: b, b: a }</c> swaps); an integer result outside ±(2^53 - 1) throws <c>invalid/args</c> (rule 10), as the
    /// column's <c>CHECK</c> fails the step (upstream <c>applySet</c>).</summary>
    internal static OrderedDictionary<string, JsonNode?> ApplySet(SetNode set, IReadOnlyDictionary<string, JsonNode?> row, string table)
    {
        var next = new OrderedDictionary<string, JsonNode?>(row, StringComparer.Ordinal);
        foreach (var (column, node) in set)
        {
            var value = Evaluate(node, row, table);
            if (!value.Present) next.Remove(column);
            else next[column] = value.Node;
        }
        return next;
    }

    /// <summary>An evaluated cell: absent (<c>NULL</c>, upstream <c>undefined</c>) or a value.</summary>
    private readonly record struct Evaluated(bool Present, JsonNode? Node)
    {
        internal static Evaluated Absent => default;

        internal static Evaluated Of(JsonNode? node) => new(true, node);

        internal static Evaluated Of(Scalar scalar) => new(true, scalar.ToNode());
    }

    private static Evaluated Evaluate(ExprNode? node, IReadOnlyDictionary<string, JsonNode?> row, string table)
    {
        switch (node)
        {
            case null:
                return Evaluated.Absent;
            case ExprNode.Literal literal:
                return Evaluated.Of(literal.Value);
            case ExprNode.Col col:
                return row.TryGetValue(col.Column, out var cell) ? Evaluated.Of(cell) : Evaluated.Absent;
            case ExprNode.Json json:
                return Evaluated.Of(json.Value);
            case ExprNode.When conditional:
                return Evaluate(Matches(conditional.Condition, row) ? conditional.A : conditional.B, row, table);
            case ExprNode.Binary { Op: Operation.Coalesce } coalesce:
            {
                // ??: an absent cell and a JSON null fall through.
                var first = Evaluate(coalesce.A, row, table);
                return first.Present && first.Node is not null ? first : Evaluate(coalesce.B, row, table);
            }
            case ExprNode.ReplacePrefix replace:
            {
                var value = Evaluate(replace.Subject, row, table);
                if (!value.Present || ScalarOf(value.Node) is not { Value: string text } || !text.StartsWith(replace.From, StringComparison.Ordinal))
                    return value;
                return Evaluated.Of((Scalar)(replace.To + text[replace.From.Length..]));
            }
            case ExprNode.Binary binary:
            {
                // The arithmetic and text operations, over scalar operands (compiled so).
                var x = Evaluate(binary.A, row, table);
                var y = Evaluate(binary.B, row, table);
                if (!x.Present || !y.Present || ScalarOf(x.Node) is not { } a || ScalarOf(y.Node) is not { } b) return Evaluated.Absent;
                switch (binary.Op)
                {
                    case Operation.Concat:
                        return Evaluated.Of((Scalar)((string)a.Value + (string)b.Value));
                    case Operation.Max:
                        return Evaluated.Of(CompareScalar(a, b) >= 0 ? a : b);
                    case Operation.Min:
                        return Evaluated.Of(CompareScalar(a, b) <= 0 ? a : b);
                    default:
                    {
                        var result = binary.Op == Operation.Add ? (double)a.Value + (double)b.Value : (double)a.Value - (double)b.Value;
                        if (result > MaxSafeInteger || result < -MaxSafeInteger)
                            throw DataInternal.Invalid("args",
                                $"{table}: {Name(binary.Op)} gives {ChordJson.ToJavaScriptString(JsonValue.Create(result))}, outside ±(2^53 - 1)");
                        return Evaluated.Of((Scalar)result);
                    }
                }
            }
            default:
                throw new InvalidOperationException("An unknown expression node.");
        }
    }
}
