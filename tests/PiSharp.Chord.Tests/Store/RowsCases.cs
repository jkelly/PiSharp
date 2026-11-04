using System.Numerics;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/rows.test.ts. Ported but for the cycle of "serializes
// json cells at staging": a JsonNode tree cannot hold a cycle (a node has one parent), so only its BigInt half is ported, as a JsonValue
// holding a BigInteger. Upstream's { ...row, id: undefined } is the row without "id" (an absent field is undefined). Parameters are
// compared as JSON text (StoreParams): a double 7 and a long 7 print alike, as JavaScript numbers do. A stored row is a map of column to
// SQLite value, as the backend reads it.
internal static partial class Program
{
    private static readonly TableDefinition StoreRowsTable = Tables.DefineTable("t", new TableOptions
    {
        Version = 3,
        Columns = StoreColumns(
            ("id", Str()),
            ("n", Int(@default: 7)),
            ("x", Num()),
            ("b", Bool()),
            ("note", Opt(Str())),
            ("meta", Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 2 }))),
        Key = ["id"],
    });

    /// <summary>The row with <paramref name="column"/> set to <paramref name="value"/> (upstream's { ...row, column: value }).</summary>
    private static OrderedDictionary<string, JsonNode?> StoreWith(OrderedDictionary<string, JsonNode?> row, string column, JsonNode? value) =>
        new(row, StringComparer.Ordinal) { [column] = value };

    private static IEnumerable<(string, Func<Task>)> StoreRowsCases()
    {
        var T = StoreRowsTable;
        return
        [
            Case("store.rows.put-rows-encodes-a-row-in-physical-order-defaults-filled-booleans-as-0-and-1-json-serialized-with-its-version", () =>
            {
                var encoded = T.Rows.Put(StoreRow("""{"id":"a","x":1.5,"b":true,"meta":{"a":1}}"""), 3);
                Equal("""["a",7,1.5,1,null,"{\"a\":1}",2,3]""", StoreParams(encoded.Params), "the parameters");
                Json("""{"id":"a","n":7,"x":1.5,"b":true,"meta":{"a":1}}""", StoreJson(encoded.Row), "the row");
                Sequence(["meta: 2"], encoded.Columns.Select(entry => $"{entry.Key}: {entry.Value}"), "the json columns' versions");
                Equal(T.Rows.Key(StoreRow("""{"id":"a"}"""), "get").Id, encoded.Id, "the key's identity");
            }),
            Case("store.rows.put-rows-is-a-closed-check-missing-and-unknown-columns-wrong-types-unsafe-integers-non-finite-numbers-nul", () =>
            {
                var row = StoreRow("""{"id":"a","x":1,"b":false,"meta":{}}""");
                var withoutId = new OrderedDictionary<string, JsonNode?>(row, StringComparer.Ordinal);
                withoutId.Remove("id");
                StoreArgs(() => T.Rows.Put(withoutId, 3), null, "id: undefined");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "extra", 1), 3), "unknown column \"extra\"");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "n", Math.Pow(2, 53)), 3), null, "n: 2 ** 53");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "n", 1.5), 3), null, "n: 1.5");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "x", double.PositiveInfinity), 3), null, "x: Infinity");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "b", 1), 3), null, "b: 1");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "id", "a\0"), 3), "NUL");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "note", null), 3), null, "note: null");
                StoreArgs(() => T.Rows.Put(new JsonArray(), 3), "expected an object");
            }),
            Case("store.rows.put-rows-serializes-json-cells-at-staging-a-cycle-or-a-big-int-fails-there-rule-8", () =>
            {
                var row = StoreRow("""{"id":"a","x":1,"b":false}""");
                StoreArgs(() => T.Rows.Put(StoreWith(row, "meta", new JsonObject { ["a"] = JsonValue.Create(new BigInteger(1)) }), 3), null, "a BigInt");
            }),
            Case("store.rows.put-rows-replaces-lone-surrogates-in-text", () =>
            {
                // JSON text cannot carry a lone surrogate through JsonNode.Parse; the field is set in code.
                var row = StoreRow("""{"x":1,"b":false,"meta":{}}""");
                row["id"] = "a\uD800";
                Json("\"a\uFFFD\"", T.Rows.Put(row, 3).Row["id"], "the id");
            }),
            Case("store.rows.keys-takes-exactly-the-key-columns", () =>
            {
                Equal("""["a"]""", StoreParams(T.Rows.Key(StoreRow("""{"id":"a"}"""), "get").Params), "the parameters");
                StoreArgs(() => T.Rows.Key(StoreRow("{}"), "get"), "is missing");
                StoreArgs(() => T.Rows.Key(StoreRow("""{"id":"a","n":1}"""), "get"), "unknown column \"n\"");
                StoreArgs(() => T.Rows.Key(StoreRow("""{"id":1}"""), "get"), null, "id: 1");
            }),
            Case("store.rows.decoding-reads-booleans-leaves-null-opt-columns-absent-and-parses-json-cells", () =>
            {
                var row = T.Rows.Decode(new Dictionary<string, object?>
                {
                    ["id"] = "a", ["n"] = 1L, ["x"] = 2d, ["b"] = 0L, ["note"] = null, ["meta"] = """{"a":1}""", ["meta$v"] = 2L,
                }, ChordContext.Background);
                Json("""{"id":"a","n":1,"x":2,"b":false,"meta":{"a":1}}""", StoreJson(row), "the row");
                Check(!row.ContainsKey("note"), "a NULL opt column is absent");
            }),
            Case("store.rows.decoding-refuses-a-json-cell-of-a-newer-version", () =>
            {
                StoreRefused(() => T.Rows.Decode(new Dictionary<string, object?>
                {
                    ["id"] = "a", ["n"] = 1L, ["x"] = 2d, ["b"] = 0L, ["note"] = null, ["meta"] = "{}", ["meta$v"] = 3L,
                }, ChordContext.Background), "unavailable", null, "newer than 2");
            }),
        ];
    }
}
