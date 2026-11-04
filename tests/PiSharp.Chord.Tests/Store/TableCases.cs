using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/table.test.ts. Every case is ported. Upstream's casts
// (key: ["note" as "id"]) are type-level only. A declaration refusal is an ArgumentException with upstream's message (StoreThrows takes
// any exception whose message contains the text, as toThrow(message) does).
internal static partial class Program
{
    /// <summary>table.test.ts's columns.</summary>
    private static OrderedDictionary<string, Property> StoreTableColumns() => StoreColumns(("id", Str()), ("path", Str()), ("at", Int()),
        ("note", Opt(Str())), ("meta", Tables.Json(Obj(), new JsonColumnOptions { Version = 1 })));

    private static TableDefinition StoreTableEdits(long version, IReadOnlyList<string> key, OrderedDictionary<string, IReadOnlyList<string>>? indexes = null,
        OrderedDictionary<string, Property>? columns = null) =>
        Tables.DefineTable("edits", new TableOptions { Version = version, Columns = columns ?? StoreTableColumns(), Key = key, Indexes = indexes ?? StoreIndexes() });

    private static IEnumerable<(string, Func<Task>)> StoreTableCases() =>
    [
        Case("store.table.definetable-materializes-columns-key-indexes-and-the-record", () =>
        {
            var edits = StoreTableEdits(2, ["id"], StoreIndexes(("by_path", ["path", "at"])));
            Equal("edits", edits.Name, "the name");
            Equal(2L, edits.Version, "the version");
            Sequence(["id"], edits.Key, "the key");
            Sequence(["by_path: path, at"], edits.Indexes.Select(index => $"{index.Key}: {string.Join(", ", index.Value)}"), "the indexes");
            Sequence(["id", "path", "at", "note", "meta"], edits.Record.Keys, "the record's columns");
            Sequence(["id", "path", "at", "note", "meta", "meta$v", "$v"], edits.Rows.Physical, "the physical columns");
        }),
        Case("store.table.definetable-checks-the-table-name-the-version-key-index-names-and-index-columns", () =>
        {
            StoreThrows(() => Tables.DefineTable("Edits", new TableOptions { Version = 1, Columns = StoreTableColumns(), Key = ["id"] }), "expected lowercase");
            StoreThrows(() => Tables.DefineTable("a:b", new TableOptions { Version = 1, Columns = StoreTableColumns(), Key = ["id"] }), "expected lowercase");
            Equal("@pi/file-history.edits",
                Tables.DefineTable("@pi/file-history.edits", new TableOptions { Version = 1, Columns = StoreTableColumns(), Key = ["id"] }).Name, "a scoped name");
            StoreThrows(() => StoreTableEdits(0, ["id"]), "positive integer");
            StoreThrows(() => StoreTableEdits(1, []), "no column");
            StoreThrows(() => StoreTableEdits(1, ["note"]), "non-opt str() or int()", "an opt key column");
            StoreThrows(() => StoreTableEdits(1, ["meta"]), "non-opt str() or int()", "a json key column");
            StoreThrows(() => StoreTableEdits(1, ["id", "id"]), "occurs twice");
            StoreThrows(() => StoreTableEdits(1, ["nope"]), "no column \"nope\"");
            StoreThrows(() => StoreTableEdits(1, ["id"], StoreIndexes(("ByPath", ["path"]))), "[a-z0-9_]+");
            StoreThrows(() => StoreTableEdits(1, ["id"], StoreIndexes(("by", ["note"]))), "non-opt");
        }),
        Case("store.table.definetable-fingerprints-columns-key-and-indexes-not-their-declaration-order", () =>
        {
            var a = StoreTableEdits(1, ["id"], StoreIndexes(("x", ["path"]), ("y", ["at"])));
            var columns = StoreTableColumns();
            var reordered = StoreColumns(("meta", columns["meta"]), ("note", columns["note"]), ("at", columns["at"]), ("path", columns["path"]), ("id", columns["id"]));
            var b = StoreTableEdits(1, ["id"], StoreIndexes(("y", ["at"]), ("x", ["path"])), reordered);
            Equal(a.Fingerprint, b.Fingerprint, "reordered columns and indexes");
            Check(StoreTableEdits(1, ["id"]).Fingerprint != a.Fingerprint, "no indexes: another fingerprint");
            Check(StoreTableEdits(1, ["id", "at"], StoreIndexes(("x", ["path"]), ("y", ["at"]))).Fingerprint != a.Fingerprint, "another key: another fingerprint");
            var otherMeta = StoreTableColumns();
            otherMeta["meta"] = Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 1 });
            Check(StoreTableEdits(1, ["id"], StoreIndexes(("x", ["path"]), ("y", ["at"])), otherMeta).Fingerprint != a.Fingerprint,
                "another json schema: another fingerprint");
        }),
    ];
}
