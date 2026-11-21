using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/store/column-set.test.ts. Every case is ported. Column sets
// (slice 14 §5): defineColumns checks its own declaration; a table that exposes a set has its columns with the same definition, the same
// key, and an index path for each index the set declares; evolveTable carries the set over, replaces it or drops it. Upstream's `as never`
// casts (a json column, a key or an index of a column the set has not) need none here: the C# declarations are untyped and refused when
// they run. `exposes: "none"` is EvolveOptions.ExposesNone.
internal static partial class Program
{
    private static readonly HostKind Sync160SetSession = new("session", new() { ["id"] = Str() });

    private static readonly ColumnSet Sync160SetColumns = Sync160Columns("sessions.columns",
        [("id", Tables.Host(Sync160SetSession)), ("app", Str()), ("private", Bool()), ("archivedAt", Opt(Int()))], ["id"], ("ofApp", ["app"]));

    /// <summary>upstream's <c>table(columns, key, indexes = { by_app: ["app", "id"] })</c>, exposing the set.</summary>
    private static TableDefinition Sync160SetTable(OrderedDictionary<string, Property> columns, string[] key, (string, string[])[]? indexes = null) =>
        Sync160Table("rows", columns, key, Sync160SetColumns, indexes ?? [("by_app", ["app", "id"])]);

    private static IEnumerable<(string, Func<Task>)> StoreSync160ColumnSetCases()
    {
        var rows = Tables.DefineTable("rows", new TableOptions
        {
            Version = 1, Columns = Sync160Spread(Sync160SetColumns), Key = ["id"], Indexes = StoreIndexes(("by_app", ["app"])), Exposes = Sync160SetColumns,
        });
        return
        [
            Case("store.column-set.define-columns-keeps-its-columns-key-and-indexes-and-hands-out-references-tagged-with-its-name", () =>
            {
                Sequence(["id", "app", "private", "archivedAt"], Sync160SetColumns.ColumnNames, "the columns, in declaration order");
                Sequence(["id"], Sync160SetColumns.Key, "the key");
                Sequence(["ofApp:app"], Sync160SetColumns.Indexes.Select(index => $"{index.Key}:{string.Join(",", index.Value)}"), "the indexes");
                Equal("app", Sync160SetColumns.Refs["app"].Column, "a reference's column");
                Equal("sessions.columns", Sync160SetColumns.Refs["app"].Set, "a reference's set");
            }),
            Case("store.column-set.define-columns-refuses-a-json-column-a-key-or-an-index-of-a-column-it-has-not-and-a-bad-name", () =>
            {
                var meta = Tables.Json(Obj(), new JsonColumnOptions { Version = 1 });
                Refuses(() => Sync160Columns("x", [("id", Str()), ("meta", meta)], ["id"]), "column \"meta\": json(...) is not in a column set");
                Refuses(() => Sync160Columns("x", [("id", Str())], ["nope"]), "no column \"nope\"");
                Refuses(() => Sync160Columns("x", [("id", Str())], ["id"], ("a", ["b"])), "index \"a\": no column \"b\"");
                Refuses(() => Sync160Columns("Bad Name", [("id", Str())], ["id"]), "expected lowercase");
            }),
            Case("store.column-set.exposes-takes-a-table-that-spreads-the-sets-columns-with-the-key-and-an-index-path-per-declared-index", () =>
            {
                var table = Sync160SetTable(Sync160Spread(Sync160SetColumns, ("cwd", Str())), ["id"]);
                Check(ReferenceEquals(Sync160SetColumns, table.Exposes), "the table exposes the set");
            }),
            Case("store.column-set.exposes-is-no-part-of-the-tables-fingerprint", () =>
            {
                TableOptions Options(ColumnSet? exposes) => new()
                {
                    Version = 1, Columns = Sync160Spread(Sync160SetColumns), Key = ["id"], Indexes = StoreIndexes(("by_app", ["app"])), Exposes = exposes,
                };
                Equal(Tables.DefineTable("rows", Options(null)).Fingerprint, Tables.DefineTable("rows", Options(Sync160SetColumns)).Fingerprint, "the fingerprint");
            }),
            Case("store.column-set.exposes-refuses-a-missing-column-another-type-another-key-and-an-index-the-table-lacks", () =>
            {
                var withoutApp = Sync160Spread(Sync160SetColumns, ("cwd", Str()));
                withoutApp.Remove("app");
                Refuses(() => Sync160SetTable(withoutApp, ["id"], []), "the table has no column \"app\"");
                Refuses(() => Sync160SetTable(Sync160Spread(Sync160SetColumns, ("app", Int())), ["id"]), "column \"app\" is declared differently in the table");
                Refuses(() => Sync160SetTable(Sync160Spread(Sync160SetColumns, ("n", Int())), ["n"]), "the keys differ");
                Refuses(() => Sync160SetTable(Sync160Spread(Sync160SetColumns), ["id"], []), "index \"ofApp\" is no leading prefix of the key or of an index of the table");
            }),
            Case("store.column-set.evolve-table-carries-the-set-over-replaces-it-or-drops-it", () =>
            {
                Check(ReferenceEquals(Sync160SetColumns, Tables.EvolveTable(rows, t => t.Add("cwd", Opt(Str()))).Exposes), "carried over");
                Check(Tables.EvolveTable(rows, t => t.Add("cwd", Opt(Str())), new EvolveOptions { ExposesNone = true }).Exposes is null, "dropped");
                var narrow = Sync160Columns("sessions.columns", [("id", Tables.Host(Sync160SetSession)), ("app", Str())], ["id"]);
                Check(ReferenceEquals(narrow, Tables.EvolveTable(rows, t => t.Drop("private"), new EvolveOptions { Exposes = narrow }).Exposes), "replaced");
            }),
            Case("store.column-set.evolve-table-refuses-a-version-that-no-longer-has-a-column-of-the-set-it-exposes", () =>
                Refuses(() => Tables.EvolveTable(rows, t => t.Drop("private")), "the table has no column \"private\"")),
        ];
    }
}
