using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/columns.test.ts. "row types" (RowOf and PutRowOf
// checked with the type-level Equal) has no C# counterpart: the C# rows are untyped maps (Rows.cs). Upstream's
// ({ c: value }) as unknown as Columns passes any schema; Property takes any JsonNode here.
internal static partial class Program
{
    private static OrderedDictionary<string, Property> StoreColumns(params (string Name, Property Schema)[] columns)
    {
        var map = new OrderedDictionary<string, Property>(StringComparer.Ordinal);
        foreach (var (name, schema) in columns) map[name] = schema;
        return map;
    }

    private static IEnumerable<(string, Func<Task>)> StoreColumnsCases() =>
    [
        Case("store.columns.column-descriptors-describes-the-helpers-in-declaration-order", () =>
        {
            var columns = StoreInternal.DescribeColumns(StoreColumns(
                ("id", Str()),
                ("n", Int(@default: 0)),
                ("x", Num()),
                ("b", Bool()),
                ("note", Opt(Str())),
                ("meta", Opt(Tables.Json(Obj(), new JsonColumnOptions { Version = 2 })))), "t");
            Sequence(["id str False", "n int False", "x num False", "b bool False", "note str True", "meta json True"],
                columns.Select(column => $"{column.Name} {StoreInternal.Name(column.Type)} {column.Optional}"), "name, type, optional");
            var records = columns.Select(column => StoreInternal.RecordOf(column).ToJson()).ToList();
            Json("""{"type":"str"}""", records[0], "id");
            Json("""{"type":"int","default":0}""", records[1], "n");
            Json("""{"type":"num"}""", records[2], "x");
            Json("""{"type":"bool"}""", records[3], "b");
            Json("""{"type":"str","optional":true}""", records[4], "note");
            // fingerprint: expect.any(String).
            Sequence(["type", "optional", "version", "fingerprint"], Keys(records[5]), "meta's fields");
            Json("""{"type":"json","optional":true,"version":2}""", new JsonObject { ["type"] = records[5]["type"]!.DeepClone(),
                ["optional"] = records[5]["optional"]!.DeepClone(), ["version"] = records[5]["version"]!.DeepClone() }, "meta");
            Check(records[5]["fingerprint"] is JsonValue fingerprint && fingerprint.TryGetValue<string>(out _), "meta's fingerprint is a string");
        }),
        Case("store.columns.column-descriptors-checks-names-the-pattern-no-two-equal-ignoring-case-no", () =>
        {
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("a-b", Str())), "t"), "[A-Za-z_][A-Za-z0-9_]*");
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("a$v", Str())), "t"), "column \"a$v\"");
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("path", Str()), ("Path", Str())), "t"), "only in case");
            Equal(2, StoreInternal.DescribeColumns(StoreColumns(("camelCase", Str()), ("_x1", Int())), "t").Count, "camelCase and _x1");
        }),
        Case("store.columns.column-descriptors-refuses-other-schemas-bounds-patterns-opt-defaults-and-defaults-of-another-type", () =>
        {
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("c", Arr(Str()))), "t"), "expected str()");
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("c", Obj())), "t"), "expected str()");
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("c", Str(max: 3))), "t"), "\"maxLength\" is not supported");
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("c", Str(pattern: "a"))), "t"), "\"pattern\" is not supported");
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("c", Opt(Int(@default: 1)))), "t"), "no default");
            Refuses(() => StoreInternal.DescribeColumns(StoreColumns(("c", Int(@default: 1.5))), "t"), "not a int value");
            // storedText's refusal: invalid/args, as upstream.
            StoreThrows(() => StoreInternal.DescribeColumns(StoreColumns(("c", Str(@default: "a\0"))), "t"), "NUL");
        }),
    ];
}
