using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/expression.test.ts. Every case is ported. A plugin's
// undefined and null operand are both C# null here (Expressions take a JsonNode?), so "refuses undefined and null operands" checks the
// one refusal twice, as upstream's two lines do; { column: "nope" } as never is a hand-built Condition over a JSON object, and "x" a
// string where a Condition belongs. A set is a map of column to Operand (Operand.Undefined is upstream's undefined). The compiled nodes
// are compared in upstream's data form (WhereNode.ToJson, SetNode.ToJson).
internal static partial class Program
{
    /// <summary>The table of expression.test.ts and evaluate.test.ts (both declare it so).</summary>
    private static readonly TableDefinition StoreVocabularyTable = Tables.DefineTable("t", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(
            ("id", Str()),
            ("path", Str()),
            ("n", Int()),
            ("x", Num()),
            ("b", Bool()),
            ("note", Opt(Str())),
            ("count", Opt(Int())),
            ("meta", Opt(Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 1 })))),
        Key = ["id"],
    });

    /// <summary>A set: columns and their operands, in order.</summary>
    private static OrderedDictionary<string, Operand> StoreSet(params (string Column, Operand Value)[] values)
    {
        var set = new OrderedDictionary<string, Operand>(StringComparer.Ordinal);
        foreach (var (column, value) in values) set[column] = value;
        return set;
    }

    private static IEnumerable<(string, Func<Task>)> StoreExpressionCases()
    {
        var T = StoreVocabularyTable;
        var refs = T.Refs;
        return
        [
            Case("store.expression.compiling-conditions-compiles-every-operator-into-data", () =>
            {
                var where = And(
                    Eq(refs["path"], "a"),
                    Ne(refs["n"], 1),
                    Lt(refs["n"], 2),
                    Lte(refs["x"], 2.5),
                    Gt(refs["n"], 0),
                    Gte(refs["path"], "a"),
                    InArray(refs["n"], [1, 2]),
                    IsNull(refs["note"]),
                    Prefix(refs["path"], "a/"),
                    Or(Not(Eq(refs["b"], true))));
                Json("""
                    {"and":[{"eq":["path","a"]},{"ne":["n",1]},{"lt":["n",2]},{"lte":["x",2.5]},{"gt":["n",0]},{"gte":["path","a"]},
                    {"inArray":["n",[1,2]]},{"isNull":"note"},{"prefix":["path","a/"]},{"or":[{"not":{"eq":["b",true]}}]}]}
                    """, StoreInternal.CompileWhere(T, where, "where").ToJson(), "the compiled condition");
            }),
            Case("store.expression.compiling-conditions-refuses-undefined-and-null-operands-rule-16-wrong-types-json-and-unknown-columns", () =>
            {
                StoreArgs(() => StoreInternal.CompileWhere(T, Eq(refs["note"], null), "where"), "ask with isNull", "undefined");
                StoreArgs(() => StoreInternal.CompileWhere(T, Eq(refs["note"], null), "where"), null, "null");
                StoreArgs(() => StoreInternal.CompileWhere(T, Eq(refs["n"], "1"), "where"), null, "a string for an int");
                StoreArgs(() => StoreInternal.CompileWhere(T, Eq(refs["n"], 1.5), "where"), null, "1.5 for an int");
                StoreArgs(() => StoreInternal.CompileWhere(T, Eq(refs["meta"], 1), "where"), "not queryable");
                StoreArgs(() => StoreInternal.CompileWhere(T, new Condition("eq", [new JsonObject { ["column"] = "nope" }, JsonValue.Create(1)]), "where"),
                    "expected a column reference");
                StoreArgs(() => StoreInternal.CompileWhere(T, "x", "where"), "expected a condition");
                StoreArgs(() => StoreInternal.CompileWhere(T, Prefix(refs["n"], "a"), "where"), "str() column");
            }),
            Case("store.expression.compiling-conditions-makes-text-operands-storable", () =>
            {
                Json("""{"eq":["path","a\uFFFD"]}""", StoreInternal.CompileWhere(T, Eq(refs["path"], "a\uD800"), "where").ToJson(), "a lone surrogate");
                StoreArgs(() => StoreInternal.CompileWhere(T, Prefix(refs["path"], "a\0"), "where"), "NUL");
            }),
            Case("store.expression.compiling-sets-compiles-plain-values-and-expressions-into-one-vocabulary-a-clear-as-null-rule-14", () =>
            {
                Json("""{"path":"p","note":null,"b":true,"meta":{"json":{"a":1}}}""",
                    StoreInternal.CompileSet(T, StoreSet(("path", "p"), ("note", Operand.Undefined), ("b", true), ("meta", new JsonObject { ["a"] = 1 })), "update").ToJson(),
                    "plain values");
                var set = StoreInternal.CompileSet(T, StoreSet(
                    ("n", Add(refs["n"], 1)),
                    ("count", Sub(Coalesce(refs["count"], 0), refs["n"])),
                    ("x", Max(refs["x"], 1.5)),
                    ("path", ReplacePrefix(Concat(refs["path"], "/"), "a/", "b/")),
                    ("note", When(Gt(refs["n"], 1), refs["path"], refs["note"]))), "update");
                Json("""
                    {"n":{"add":[{"col":"n"},1]},"count":{"sub":[{"coalesce":[{"col":"count"},0]},{"col":"n"}]},"x":{"max":[{"col":"x"},1.5]},
                    "path":{"replacePrefix":[{"concat":[{"col":"path"},"/"]},"a/","b/"]},"note":{"when":[{"gt":["n",1]},{"col":"path"},{"col":"note"}]}}
                    """, JsonNode.Parse(set.ToJson().ToJsonString()), "JSON.parse(JSON.stringify(set))");
            }),
            Case("store.expression.compiling-sets-refuses-an-empty-set-key-columns-rule-13-unknown-columns-and-clears-of-non-opt-columns", () =>
            {
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(), "update"), "empty");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("id", "x")), "update"), "cannot change");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("nope", 1)), "update"), "no column \"nope\"");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("path", Operand.Undefined)), "update"), "cannot be cleared");
            }),
            Case("store.expression.compiling-sets-undefined-clears-an-opt-column-null-is-a-json-cell-s-json-null-and-refused-for-scalar-columns", () =>
            {
                Json("""{"note":null,"meta":null}""",
                    StoreInternal.CompileSet(T, StoreSet(("note", Operand.Undefined), ("meta", Operand.Undefined)), "update").ToJson(), "clears");
                var nullable = Tables.DefineTable("n", new TableOptions
                {
                    Version = 1,
                    Columns = StoreColumns(("id", Str()), ("data", Tables.Json(Nul(), new JsonColumnOptions { Version = 1 }))),
                    Key = ["id"],
                });
                Json("""{"data":{"json":null}}""", StoreInternal.CompileSet(nullable, StoreSet(("data", (JsonNode?)null)), "update").ToJson(), "a JSON null");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("note", (JsonNode?)null)), "update"), "null is no str value");
            }),
            Case("store.expression.compiling-sets-refuses-integer-arithmetic-outside-int-columns-rule-10-type-mismatches-and-nullable-results-into-non-opt-columns", () =>
            {
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("x", Add(refs["n"], 1))), "update"), "add gives int");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("path", Max(refs["n"], 1))), "update"), "gives int or num");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("path", refs["n"])), "update"), "column \"n\" is int, not str");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("n", Add(refs["count"], 1))), "update"), "may be NULL");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("path", refs["note"])), "update"), "may be NULL");
                Json("""{"path":{"coalesce":[{"col":"note"},"d"]}}""",
                    StoreInternal.CompileSet(T, StoreSet(("path", Coalesce(refs["note"], "d"))), "update").ToJson(), "coalesce");
                StoreArgs(() => StoreInternal.CompileSet(T, StoreSet(("meta", refs["meta"])), "update"), "plain values");
            }),
        ];
    }
}
