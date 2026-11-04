using System.Text.Json.Nodes;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/sql.test.ts. Every case is ported. The DDL runs in
// Microsoft.Data.Sqlite's in-memory database (StoreSqlite) where upstream opens node:sqlite's; "rejects.toThrow("CHECK")" is a
// SqliteException whose message names the CHECK constraint. Compiled nodes (SetNode, WhereNode) are built by hand, as upstream's
// literals are; parameters are compared as JSON text (StoreParams).
internal static partial class Program
{
    private static readonly TableDefinition StoreSqlTable = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(
            ("id", Str()),
            ("at", Int(@default: 0)),
            ("x", Num()),
            ("b", Bool()),
            ("note", Opt(Str())),
            ("meta", Tables.Json(Obj(), new JsonColumnOptions { Version = 2 }))),
        Key = ["id"],
    });

    private static IEnumerable<(string, Func<Task>)> StoreSqlCases()
    {
        var T = StoreSqlTable;
        var sql = new TableSql("t", T.Columns, T.Rows.Physical, T.Key);
        return
        [
            Case("store.sql.names-renders-host-kind-object-type-table-quoted", () =>
            {
                Equal("@pi/file-history.store:edits", StoreInternal.TableSqlName("@pi/file-history.store", "edits"), "the SQL name");
                Equal("\"session:a:b\"", StoreInternal.Quote("session:a:b"), "quoted");
            }),
            Case("store.sql.ddl-renders-a-strict-without-rowid-table-with-checks-defaults-hidden-version-columns-and-the-key", () =>
            {
                Equal("CREATE TABLE \"s:o:edits\" (\"id\" TEXT NOT NULL, \"at\" INTEGER NOT NULL DEFAULT 0 CHECK (\"at\" BETWEEN -9007199254740991 AND 9007199254740991), " +
                    "\"x\" REAL NOT NULL, \"b\" INTEGER NOT NULL CHECK (\"b\" IN (0, 1)), \"note\" TEXT, \"meta\" TEXT NOT NULL, \"meta$v\" INTEGER NOT NULL, " +
                    "\"$v\" INTEGER NOT NULL, PRIMARY KEY (\"id\")) STRICT, WITHOUT ROWID", StoreInternal.CreateTableSql("s:o:edits", T.Record, T.Key), "CREATE TABLE");
                Equal("CREATE INDEX \"s:o:edits:by_at\" ON \"s:o:edits\" (\"at\", \"id\")", StoreInternal.CreateIndexSql("s:o:edits", "by_at", ["at"], ["id"]), "by_at");
                Equal("CREATE INDEX \"s:o:edits:by_id\" ON \"s:o:edits\" (\"id\", \"at\")", StoreInternal.CreateIndexSql("s:o:edits", "by_id", ["id", "at"], ["id"]), "by_id");
                Equal("DROP INDEX \"s:o:edits:by_at\"", StoreInternal.DropIndexSql("s:o:edits", "by_at"), "DROP INDEX");
            }),
            Case("store.sql.ddl-keeps-int-columns-within-2-53-1-and-bool-columns-within-0-and-1-rule-10", () =>
            {
                using var db = new StoreSqlite();
                db.Run(StoreInternal.CreateTableSql("t", T.Record, T.Key));
                db.Run(sql.Put, T.Rows.Put(StoreRow("""{"id":"a","x":1,"b":true,"meta":{}}"""), 1).Params);
                StoreThrows(() => db.Run("UPDATE t SET at = ?", [Math.Pow(2, 53)]), "CHECK", "at = 2 ** 53");
                StoreThrows(() => db.Run("UPDATE t SET b = 2"), "CHECK", "b = 2");
                Throws<Exception>(() => db.Run("UPDATE t SET at = 'x'"), "at = 'x'");
                db.Run("INSERT INTO t (id, x, b, meta, meta$v, \"$v\") VALUES ('d', 1, 0, '{}', 2, 1)");
                Equal("""[0]""", StoreParams([db.Get("SELECT at FROM t WHERE id = 'd'")!["at"]]), "the default");
                Sequence(["at"], db.Get("SELECT at FROM t WHERE id = 'd'")!.Keys, "{ at: 0 }");
            }),
            Case("store.sql.statements-renders-the-fixed-statements-once", () =>
            {
                Equal("INSERT OR REPLACE INTO \"t\" (\"id\", \"at\", \"x\", \"b\", \"note\", \"meta\", \"meta$v\", \"$v\") VALUES (?, ?, ?, ?, ?, ?, ?, ?)", sql.Put, "put");
                Equal("SELECT \"id\", \"at\", \"x\", \"b\", \"note\", \"meta\", \"meta$v\" FROM \"t\" WHERE \"id\" = ?", sql.Get, "get");
                Equal("DELETE FROM \"t\" WHERE \"id\" = ?", sql.Delete, "delete");
            }),
            Case("store.sql.statements-stamps-a-json-column-s-version-with-its-value-null-with-a-clear", () =>
            {
                var versions = new Dictionary<string, long> { ["meta"] = 2 };
                var update = sql.Update(new SetNode { ["meta"] = new ExprNode.Json(new JsonObject { ["a"] = 1 }) }, ["k"], versions);
                Equal("UPDATE \"t\" SET \"meta\" = ?, \"meta$v\" = ? WHERE \"id\" = ?", update.Sql, "the update");
                Equal("""["{\"a\":1}",2,"k"]""", StoreParams(update.Params), "its parameters");
                Equal("""[null,"k"]""", StoreParams(sql.Update(new SetNode { ["meta"] = null }, ["k"], versions).Params), "a clear's parameters");
            }),
            Case("store.sql.statements-binds-every-value-one-text-per-query-shape-whatever-the-values-rule-18", () =>
            {
                var order = new Order(["at", "id"], OrderDirection.Desc);
                var a = sql.Select(new WhereNode.InArray("id", ["a", "b"]), order, [1d, "x"], 10);
                var b = sql.Select(new WhereNode.InArray("id", [.. Enumerable.Range(0, 40_000).Select(i => (Scalar)$"k{i}")]), order, [2d, "y"], 99);
                Equal(a.Sql, b.Sql, "one text");
                Equal("SELECT \"id\", \"at\", \"x\", \"b\", \"note\", \"meta\", \"meta$v\" FROM \"t\" WHERE \"id\" IN (SELECT value FROM json_each(?)) AND (\"at\", \"id\") < (?, ?) ORDER BY \"at\" DESC, \"id\" DESC LIMIT ?",
                    a.Sql, "the select");
                Equal("""["[\"a\",\"b\"]",1,"x",10]""", StoreParams(a.Params), "its parameters");
                var count = sql.Count(new WhereNode.Prefix("id", StoreU(0x10FFFF)));
                Equal("SELECT count(*) AS count FROM \"t\" WHERE \"id\" >= ?", count.Sql, "the count");
                Sequence([StoreU(0x10FFFF)], count.Params.Cast<string>(), "its parameters");
                var delete = sql.DeleteWhere(new WhereNode.Prefix("id", "a"));
                Equal("DELETE FROM \"t\" WHERE (\"id\" >= ? AND \"id\" < ?)", delete.Sql, "the delete");
                Equal("""["a","b"]""", StoreParams(delete.Params), "its parameters");
            }),
        ];
    }
}
