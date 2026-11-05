using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/evaluate.test.ts. Every case is ported. Upstream's
// SQLite side is node:sqlite (openNodeSqliteDatabase(":memory:")); here it is Microsoft.Data.Sqlite's in-memory database (StoreSqlite,
// the test project's reference), running the SQL PiSharp.Chord renders. A row is a map of column to JsonNode (an absent column is NULL).
internal static partial class Program
{
    /// <summary>The rows of "one semantics": text by code point, NULLs in every opt column.</summary>
    private static List<OrderedDictionary<string, JsonNode?>> StoreAgreementRows() =>
    [
        StoreRow("""{"id":"1","path":"a/x","n":1,"x":0.5,"b":true,"note":"z","count":1}"""),
        StoreRow("""{"id":"2","path":"a/\uD83D\uDE00","n":2,"x":2,"b":false}"""),
        StoreRow("""{"id":"3","path":"a\uFFFF","n":-3,"x":-1,"b":true,"note":""}"""),
        StoreRow("""{"id":"4","path":"b","n":0,"x":0,"b":false,"count":0}"""),
        StoreRow("""{"id":"5","path":"","n":9,"x":9.5,"b":true,"note":"a/"}"""),
        StoreRow("""{"id":"6","path":"\uDBFF\uDFFF","n":5,"x":1,"b":false,"count":-2}"""),
    ];

    /// <summary>A database with the table and its rows (upstream's table()).</summary>
    private static (StoreSqlite Db, TableSql Sql) StoreAgreementTable(List<OrderedDictionary<string, JsonNode?>> rows)
    {
        var T = StoreVocabularyTable;
        var db = new StoreSqlite();
        db.Run(StoreInternal.CreateTableSql("t", T.Record, T.Key));
        var sql = new TableSql("t", T.Columns, T.Rows.Physical, T.Key);
        foreach (var row in rows) db.Run(sql.Put, T.Rows.Put(row, 1).Params);
        return (db, sql);
    }

    /// <summary>Checks that SQLite selects the rows the evaluator matches, for each condition.</summary>
    private static void StoreSelectsAsMatched(IEnumerable<Condition> conditions)
    {
        var T = StoreVocabularyTable;
        var rows = StoreAgreementRows();
        var (db, _) = StoreAgreementTable(rows);
        using (db)
        {
            foreach (var condition in conditions)
            {
                var node = StoreInternal.CompileWhere(T, condition, "where");
                var parameters = new List<object?>();
                var text = StoreInternal.WhereSql(node, parameters);
                var selected = db.All($"SELECT id FROM t WHERE {text} ORDER BY id", parameters).Select(row => (string)row["id"]!);
                var expected = rows.Where(row => StoreInternal.Matches(node, row)).Select(row => (string)row["id"]!);
                Sequence(expected, selected, node.ToString());
            }
        }
    }

    /// <summary>Checks that SQLite updates every row as the evaluator does, for each set (upstream's updateWhere over and([])).</summary>
    private static void StoreUpdatesAsApplied(IEnumerable<OrderedDictionary<string, Operand>> sets)
    {
        var T = StoreVocabularyTable;
        foreach (var set in sets)
        {
            var rows = StoreAgreementRows();
            var (db, sql) = StoreAgreementTable(rows);
            using (db)
            {
                var node = StoreInternal.CompileSet(T, set, "updateWhere");
                var statement = sql.UpdateWhere(new WhereNode.And([]), node, new Dictionary<string, long>());
                db.Run(statement.Sql, statement.Params);
                var read = db.All("SELECT * FROM t ORDER BY id").Select(row => T.Rows.Decode(row, ChordContext.Background));
                Json(StoreJson(rows.Select(row => (IReadOnlyDictionary<string, JsonNode?>)StoreInternal.ApplySet(node, row, "t"))).ToJsonString(),
                    StoreJson(read), node.ToJson().ToJsonString());
            }
        }
    }

    private static IEnumerable<(string, Func<Task>)> StoreEvaluateCases()
    {
        var T = StoreVocabularyTable;
        var refs = T.Refs;
        var row = StoreRow("""{"id":"r","path":"a/x","n":2,"x":1,"b":true,"count":3}""");
        WhereNode Where(Condition condition) => StoreInternal.CompileWhere(T, condition, "where");
        Condition[] predicates =
        [
            Eq(refs["note"], "z"),
            Ne(refs["note"], "z"),
            Lt(refs["count"], 1),
            Lte(refs["count"], 0),
            Gt(refs["note"], "a"),
            Gte(refs["count"], 0),
            InArray(refs["note"], ["z", ""]),
            InArray(refs["note"], []),
            Prefix(refs["note"], "a"),
            Prefix(refs["note"], ""),
            IsNull(refs["count"]),
        ];
        var wrapped = predicates.SelectMany(p => new[]
        {
            p,
            Not(p),
            And(p, Eq(refs["b"], true)),
            Or(p, Eq(refs["b"], true)),
            Not(And(p, Eq(refs["b"], true))),
            Not(Or(p, Eq(refs["b"], false))),
        }).ToList();
        return
        [
            Case("store.evaluate.the-evaluator-follows-sql-s-null-rules-a-comparison-with-null-never-matches-not-and-ne-included", () =>
            {
                Equal(false, StoreInternal.Matches(Where(Eq(refs["note"], "a")), row), "eq");
                Equal(false, StoreInternal.Matches(Where(Ne(refs["note"], "a")), row), "ne");
                Equal(false, StoreInternal.Matches(Where(Not(Eq(refs["note"], "a"))), row), "not eq");
                Equal(true, StoreInternal.Matches(Where(Or(IsNull(refs["note"]), Ne(refs["note"], "a"))), row), "isNull or ne");
                Equal(true, StoreInternal.Matches(Where(And()), row), "and()");
                Equal(false, StoreInternal.Matches(Where(Or()), row), "or()");
                Equal(false, StoreInternal.Matches(Where(InArray(refs["n"], [])), row), "inArray []");
            }),
            Case("store.evaluate.the-evaluator-evaluates-every-right-side-against-the-row-before-a-swap", () =>
            {
                var swap = Tables.DefineTable("s", new TableOptions { Version = 1, Columns = StoreColumns(("id", Str()), ("a", Str()), ("b", Str())), Key = ["id"] });
                var set = StoreInternal.CompileSet(swap, StoreSet(("a", swap.Refs["b"]), ("b", swap.Refs["a"])), "update");
                Json("""{"id":"1","a":"y","b":"x"}""", StoreJson(StoreInternal.ApplySet(set, StoreRow("""{"id":"1","a":"x","b":"y"}"""), "s")), "the swap");
            }),
            Case("store.evaluate.the-evaluator-leaves-a-value-without-the-prefix-unchanged-and-refuses-integers-outside-the-safe-range-rule-10", () =>
            {
                Json("\"a/x\"", StoreInternal.ApplySet(StoreInternal.CompileSet(T, StoreSet(("path", ReplacePrefix(refs["path"], "b/", "c/"))), "u"), row, "t")["path"],
                    "without the prefix");
                Json("\"x\"", StoreInternal.ApplySet(StoreInternal.CompileSet(T, StoreSet(("path", ReplacePrefix(refs["path"], "a/", ""))), "u"), row, "t")["path"],
                    "with the prefix");
                var big = new OrderedDictionary<string, JsonNode?>(row, StringComparer.Ordinal) { ["n"] = StoreInternal.MaxSafeInteger };
                StoreArgs(() => StoreInternal.ApplySet(StoreInternal.CompileSet(T, StoreSet(("n", Add(refs["n"], 1))), "u"), big, "t"));
            }),
            Case("store.evaluate.one-semantics-the-evaluator-agrees-with-sqlite-selects-the-rows-the-evaluator-matches", () =>
            {
                StoreSelectsAsMatched(
                [
                    Eq(refs["note"], "z"),
                    Ne(refs["note"], "z"),
                    Not(Ne(refs["count"], 0)),
                    Lt(refs["path"], "a\uFFFF"),
                    Gte(refs["path"], "a/" + StoreU(0x1F600)),
                    Prefix(refs["path"], "a"),
                    Prefix(refs["path"], ""),
                    Prefix(refs["path"], StoreU(0x10FFFF)),
                    InArray(refs["path"], ["b", "", "x"]),
                    InArray(refs["b"], [true]),
                    Or(IsNull(refs["count"]), Gt(refs["x"], 1)),
                    And(Not(IsNull(refs["note"])), Lte(refs["n"], 1)),
                    Not(Or(Eq(refs["b"], true), Lt(refs["count"], 0))),
                ]);
            }),
            Case("store.evaluate.one-semantics-the-evaluator-agrees-with-sqlite-agrees-on-every-operator-s-null-truth-under-not-and-or-and-when", () =>
            {
                StoreSelectsAsMatched(wrapped);
                StoreUpdatesAsApplied(wrapped.Select(condition => StoreSet(("n", When(condition, 1, 2)))));
            }),
            Case("store.evaluate.one-semantics-the-evaluator-agrees-with-sqlite-binds-a-long-in-array-list-as-one-parameter-beyond-sqlite-s-32-766-variables", () =>
            {
                var rows = StoreAgreementRows();
                var (db, _) = StoreAgreementTable(rows);
                using (db)
                {
                    var values = Enumerable.Range(0, 40_000).Select(i => (JsonNode?)$"v{i}").Append("b").ToList();
                    var node = StoreInternal.CompileWhere(T, InArray(refs["path"], values), "where");
                    var parameters = new List<object?>();
                    var text = StoreInternal.WhereSql(node, parameters);
                    Equal(1, parameters.Count, "one parameter");
                    Sequence(["4"], db.All($"SELECT id FROM t WHERE {text}", parameters).Select(selected => (string)selected["id"]!), "the rows");
                }
            }),
            Case("store.evaluate.one-semantics-the-evaluator-agrees-with-sqlite-updates-rows-as-the-evaluator-does", () =>
            {
                StoreUpdatesAsApplied(
                [
                    StoreSet(("n", Add(refs["n"], 10)), ("count", Sub(Coalesce(refs["count"], 5), 1))),
                    StoreSet(("path", ReplacePrefix(refs["path"], "a/", StoreU(0x1F600) + "/")), ("note", Concat(refs["path"], "!"))),
                    StoreSet(("x", Min(refs["x"], 1)), ("n", Max(refs["n"], 1)), ("note", When(Prefix(refs["path"], "a"), refs["path"], refs["note"]))),
                    StoreSet(("note", refs["path"]), ("path", Coalesce(refs["note"], "none")), ("count", Operand.Undefined)),
                ]);
            }),
        ];
    }
}
