using System.Text.Json.Nodes;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/json-column.test.ts. Every case is ported. Upstream's
// (json(...) as unknown as { json: JsonColumn }).json reads the column out of the schema; here it is the "json" field's JsonValue.
internal static partial class Program
{
    private static readonly JsonObject StoreMetaV1 = Obj(("tag", Str()));
    private static readonly JsonObject StoreMetaV2 = Obj(("tags", Str()), ("n", Int()));

    private static JsonColumn StoreColumnOf(JsonObject schema) => schema["json"]!.GetValue<JsonColumn>();

    private static readonly JsonColumn StoreMetaColumn = new(StoreMetaV2, 2, (old, from) => new JsonObject { ["tags"] = old!["tag"]!.DeepClone(), ["n"] = from });

    private static IEnumerable<(string, Func<Task>)> StoreJsonColumnCases() =>
    [
        Case("store.json-column.json-is-a-schema-carrying-its-column-version-and-the-fingerprint-of-the-schema-s-normal-form", () =>
        {
            var schema = Tables.Json(StoreMetaV1, new JsonColumnOptions { Version = 1 });
            Check(schema["json"] is JsonValue holder && holder.TryGetValue<JsonColumn>(out _), "a JsonColumn");
            var column = StoreColumnOf(schema);
            Equal(1L, column.Version, "its version");
            var again = StoreColumnOf(Tables.Json(Obj(("tag", Str())), new JsonColumnOptions { Version = 1 }));
            var other = StoreColumnOf(Tables.Json(StoreMetaV2, new JsonColumnOptions { Version = 1 }));
            Equal(column.Fingerprint, again.Fingerprint, "the same schema");
            Check(other.Fingerprint != column.Fingerprint, "another schema has another fingerprint");
        }),
        Case("store.json-column.json-refuses-a-version-that-is-not-a-positive-integer", () =>
        {
            Refuses(() => Tables.Json(StoreMetaV1, new JsonColumnOptions { Version = 0 }), "version 0");
        }),
        Case("store.json-column.reading-a-cell-parses-a-cell-of-the-current-version-with-no-check", async () =>
        {
            var counts = await Counted(context =>
                Json("""{"tags":"a","n":1}""", StoreMetaColumn.Read("""{"tags":"a","n":1}""", 2, "t.meta", context), "the cell"));
            Equal(0d, counts.Validations, "validations");
        }),
        Case("store.json-column.reading-a-cell-migrates-an-older-cell-and-checks-the-result-once-r8", async () =>
        {
            var counts = await Counted(context =>
                Json("""{"tags":"x","n":1}""", StoreMetaColumn.Read("""{"tag":"x"}""", 1, "t.meta", context), "the migrated cell"));
            Equal(1d, counts.Validations, "validations");
        }),
        Case("store.json-column.reading-a-cell-refuses-a-newer-cell-a-missing-or-throwing-migrate-and-an-invalid-result", async () =>
        {
            await Counted(context =>
            {
                StoreRefused(() => StoreMetaColumn.Read("{}", 3, "t.meta", context), "unavailable", null, "newer than 2");
                StoreRefused(() => new JsonColumn(StoreMetaV2, 2, null).Read("{}", 1, "t.meta", context), "unavailable", null, "no migrate");
                var throwing = new JsonColumn(StoreMetaV2, 2, (_, _) => throw new InvalidOperationException("no"));
                StoreRefused(() => throwing.Read("{}", 1, "t.meta", context), "unavailable", null, "threw");
                var invalid = new JsonColumn(StoreMetaV2, 2, (_, _) => new JsonObject { ["tags"] = 1, ["n"] = 1 });
                StoreRefused(() => invalid.Read("{}", 1, "t.meta", context), "unavailable", null);
            });
        }),
    ];
}
