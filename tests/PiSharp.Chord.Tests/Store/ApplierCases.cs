using System.Numerics;
using System.Text.Json.Nodes;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/applier.test.ts. Every case is ported; the it.each
// rows of "refuses %s" are a case each. A recorded write is its JSON (the outbox's form, which the applier reads); upstream's spread
// ({ ...table, key: [...] }) is a copy of the table write with that field replaced. Upstream's 1n (a BigInt, which JSON.stringify refuses)
// is a JsonValue holding a BigInteger, which Chord's JSON writer refuses as no JSON data.
internal static partial class Program
{
    private const string StoreApplierTable = "app.store:edits";

    /// <summary>The recorded table of the applier cases (upstream <c>table</c>).</summary>
    private static JsonObject StoreApplierTableWrite() => JsonNode.Parse($$"""
        {
          "kind": "table", "table": "{{StoreApplierTable}}", "version": 2, "state": "ready",
          "columns": {
            "id": { "type": "str" },
            "done": { "type": "bool", "default": false },
            "note": { "type": "str", "optional": true },
            "meta": { "type": "json", "optional": true, "version": 1, "fingerprint": "f" }
          },
          "key": ["id"], "fingerprint": "t"
        }
        """)!.AsObject();

    /// <summary>The table write with <paramref name="field"/> replaced by <paramref name="value"/> (upstream's <c>{ ...table, field }</c>).</summary>
    private static JsonObject StoreApplierTableWith(string field, string value)
    {
        var write = StoreApplierTableWrite();
        write[field] = JsonNode.Parse(value);
        return write;
    }

    /// <summary>Applies <paramref name="writes"/> in one transaction of a fresh database; the rows of the table (upstream
    /// <c>applied</c>).</summary>
    private static async Task<JsonArray> StoreApplied(params JsonObject[] writes)
    {
        var db = new TestSqliteDatabase(":memory:");
        try
        {
            return await db.TransactionAsync(async executor =>
            {
                var step = new Step(executor, 1, new IdAllocator(2), new Dictionary<string, TableRecord>(StringComparer.Ordinal));
                foreach (var write in writes) await StoreInternal.ApplyWriteAsync(step, write, "test");
                return StoreSqlJson(await executor.AllAsync($"SELECT * FROM \"{StoreApplierTable}\""));
            });
        }
        finally
        {
            await db.CloseAsync();
        }
    }

    private static JsonObject StoreApplierWrite(string json) => JsonNode.Parse(json)!.AsObject();

    private static IEnumerable<(string, Func<Task>)> StoreApplierCases()
    {
        // A fresh one each time: a copy (DeepClone) of a BigInteger JsonValue is serialized, and comes back as the number 1.
        JsonObject Bigint()
        {
            var write = StoreApplierWrite("""{ "kind": "put", "table": "app.store:edits", "row": { "id": "a", "done": false }, "version": 1, "columns": { "meta": 1 } }""");
            write["row"]!["meta"] = JsonValue.Create(new BigInteger(1));
            return write;
        }
        var refusals = new (string Case, Func<JsonObject[]> Writes, string Message)[]
        {
            ("a-put-for-a-table-not-recorded",
                () => [StoreApplierWrite("""{ "kind": "put", "table": "app.store:edits", "row": {}, "version": 1, "columns": {} }""")], "is not recorded"),
            ("a-key-over-an-opt-column", () => [StoreApplierTableWith("key", """["note"]""")], "\"note\" is not a non-opt str or int column"),
            ("a-json-column-without-a-fingerprint",
                () => [StoreApplierTableWith("columns", """{ "id": { "type": "str" }, "meta": { "type": "json", "version": 1 } }""")],
                "a json(...) column has a version and a fingerprint"),
            ("a-default-of-another-type",
                () => [StoreApplierTableWith("columns", """{ "id": { "type": "str" }, "n": { "type": "int", "default": "x" } }""")], "is not a int value"),
            ("an-index-over-an-unknown-column",
                () => [StoreApplierTableWrite(), StoreApplierWrite("""{ "kind": "index", "table": "app.store:edits", "name": "by_x", "columns": ["x"] }""")],
                "\"x\" is not a non-opt str or int column"),
            ("an-index-twice",
                () =>
                [
                    StoreApplierTableWrite(),
                    StoreApplierWrite("""{ "kind": "index", "table": "app.store:edits", "name": "by_id", "columns": ["id"] }"""),
                    StoreApplierWrite("""{ "kind": "index", "table": "app.store:edits", "name": "by_id", "columns": ["id"] }"""),
                ], "not a new index name"),
            ("a-version-for-a-cell-the-row-lacks",
                () =>
                [
                    StoreApplierTableWrite(),
                    StoreApplierWrite("""{ "kind": "put", "table": "app.store:edits", "row": { "id": "a", "done": false }, "version": 1, "columns": { "meta": 1 } }"""),
                ], "no json(...) cell of the row"),
            ("a-json-cell-that-is-no-json", () => [StoreApplierTableWrite(), Bigint()], "not JSON"),
        };
        return
        [
            Case("store.applier.applied-writes-a-table-and-its-rows-as-recorded-null-for-absent-opt-cells-booleans-as-0-and-1-json-as-text", async () =>
            {
                var rows = await StoreApplied(StoreApplierTableWrite(), StoreApplierWrite("""
                    { "kind": "put", "table": "app.store:edits", "row": { "id": "a", "done": true, "meta": { "x": [1] } }, "version": 1, "columns": { "meta": 1 } }
                    """));
                Json("""[{ "id": "a", "done": 1, "note": null, "meta": "{\"x\":[1]}", "meta$v": 1, "$v": 1 }]""", rows, "the rows");
            }),
            .. refusals.Select(row => Case($"store.applier.applied-writes-refuses-{row.Case}", async () =>
                Rejected(await StoreRejection(StoreApplied(row.Writes())), "invalid", null, row.Case, row.Message))),
        ];
    }
}
