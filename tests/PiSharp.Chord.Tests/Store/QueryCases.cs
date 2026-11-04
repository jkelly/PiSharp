using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/query.test.ts. Every case is ported. Upstream's
// e.note as never (an opt column where the type wants a non-opt one) needs no cast here. A query's order is compared in its JSON form
// (Order.ToString), cursor values as JSON text (StoreParams).
internal static partial class Program
{
    private static readonly TableDefinition StoreEditsTable = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("entry", Int()), ("at", Int()), ("note", Opt(Str()))),
        Key = ["id", "entry"],
    });

    private static IEnumerable<(string, Func<Task>)> StoreQueryCases()
    {
        var T = StoreEditsTable;
        var order = Tables.Query(T).OrderBy(e => e["at"], OrderDirection.Desc).Order;
        return
        [
            Case("store.query.queries-are-immutable-several-wheres-are-and-ed", () =>
            {
                var root = Tables.Query(T);
                var one = root.Where(e => Eq(e["id"], "a"));
                var two = one.Where(e => Gt(e["at"], 1));
                Check(root.Condition is null, "the base query has no condition");
                Json("""{"eq":["id","a"]}""", one.Condition!.ToJson(), "one where");
                Json("""{"and":[{"eq":["id","a"]},{"gt":["at",1]}]}""", two.Condition!.ToJson(), "two wheres");
            }),
            Case("store.query.queries-order-by-the-key-ascending-by-default-and-append-the-key-as-the-tie-break-not-repeated", () =>
            {
                Json("""{"columns":["id","entry"],"direction":"asc"}""", JsonNode.Parse(Tables.Query(T).Order.ToString()), "the default order");
                Json("""{"columns":["at","id","entry"],"direction":"desc"}""",
                    JsonNode.Parse(Tables.Query(T).OrderBy(e => e["at"], OrderDirection.Desc).Order.ToString()), "by at, descending");
                Json("""{"columns":["entry","id"],"direction":"asc"}""", JsonNode.Parse(Tables.Query(T).OrderBy(e => e["entry"]).Order.ToString()), "by entry");
            }),
            Case("store.query.queries-refuse-duplicate-order-by-columns-two-directions-and-opt-columns-rule-17", () =>
            {
                StoreArgs(() => Tables.Query(T).OrderBy(e => e["at"]).OrderBy(e => e["at"]), "twice");
                StoreArgs(() => Tables.Query(T).OrderBy(e => e["at"]).OrderBy(e => e["id"], OrderDirection.Desc), "one direction");
                StoreArgs(() => Tables.Query(T).OrderBy(e => e["note"]), null, "an opt column");
            }),
            Case("store.query.cursors-round-trip-the-order-values-also-across-conditions-with-the-same-order", () =>
            {
                var values = new object?[] { 5d, "a" + StoreU(0x1F600), 2d };
                var cursor = StoreInternal.EncodeCursor("app:edits", order, values);
                Check(Regex.IsMatch(cursor, "^[A-Za-z0-9_-]+$"), "base64url: " + cursor);
                Equal(StoreParams(values), StoreParams(StoreInternal.DecodeCursor(T, "app:edits", order, cursor)), "the order values");
                var other = Tables.Query(T).Where(e => Eq(e["id"], "x")).OrderBy(e => e["at"], OrderDirection.Desc).Order;
                Equal(StoreParams(values), StoreParams(StoreInternal.DecodeCursor(T, "app:edits", other, cursor)), "under another condition");
            }),
            Case("store.query.cursors-are-a-trust-boundary-another-table-order-or-direction-garbage-and-bad-values-are-invalid-args", () =>
            {
                var cursor = StoreInternal.EncodeCursor("app:edits", order, [5d, "a", 2d]);
                StoreArgs(() => StoreInternal.DecodeCursor(T, "app:edits", Tables.Query(T).Order, cursor), null, "another order");
                StoreArgs(() => StoreInternal.DecodeCursor(T, "app:edits", Tables.Query(T).OrderBy(e => e["at"]).Order, cursor), null, "another direction");
                var otherTable = Tables.DefineTable("other", new TableOptions
                {
                    Version = 1,
                    Columns = StoreColumns(("id", Str()), ("entry", Int()), ("at", Int())),
                    Key = ["id", "entry"],
                });
                StoreArgs(() => StoreInternal.DecodeCursor(otherTable, "app:other", order, cursor), null, "another table");
                // The same definition in another object's table.
                StoreArgs(() => StoreInternal.DecodeCursor(T, "other:edits", order, cursor), null, "another object's table");
                StoreArgs(() => StoreInternal.DecodeCursor(T, "app:edits", order, "%%%"), null, "garbage");
                StoreArgs(() => StoreInternal.DecodeCursor(T, "app:edits", order, 42), null, "a number");
                StoreArgs(() => StoreInternal.DecodeCursor(T, "app:edits", order, StoreInternal.EncodeCursor("app:edits", order, ["x", "a", 2d])), null, "a bad value");
                StoreArgs(() => StoreInternal.DecodeCursor(T, "app:edits", order, StoreInternal.EncodeCursor("app:edits", order, [5d, "a"])), null, "too few values");
            }),
        ];
    }
}
