using System.Text.Json.Nodes;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/table-evolve.test.ts. Every case is ported; the
// it.each rows of "checks" are a case each. The expectTypeOf checks (the version 3 literal type, RowOf along the chain) and the
// @ts-expect-error lines are type-level only: the C# editor is untyped, and what those types refuse is the runtime check each row asserts.
// A migration's edits are compared in their recorded JSON form (TableEdit.ToJson), a step's where and set in theirs.
internal static partial class Program
{
    private static readonly TableDefinition StoreEvolveEditsV1 = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("file", Str()), ("at", Int()), ("meta", Opt(Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 1 })))),
        Key = ["id"],
        Indexes = StoreIndexes(("by_file", ["file", "at"])),
    });

    /// <summary>A migration's edits as their writes record them.</summary>
    private static JsonArray StoreEditsJson(IEnumerable<TableEdit> edits) => new([.. edits.Select(edit => (JsonNode?)edit.ToJson())]);

    /// <summary>Index definitions as text, in order.</summary>
    private static IEnumerable<string> StoreIndexesText(IReadOnlyDictionary<string, IReadOnlyList<string>> indexes) =>
        indexes.Select(index => $"{index.Key}: {string.Join(", ", index.Value)}");

    private static IEnumerable<(string, Func<Task>)> StoreTableEvolveCases()
    {
        var v1 = StoreEvolveEditsV1;
        var refusals = new (string Case, Func<object> Declare, string Message)[]
        {
            ("rename-to-an-existing-column", () => Tables.EvolveTable(v1, t => t.Rename("file", "at")), "column \"at\" exists"),
            ("rename-an-unknown-column", () => Tables.EvolveTable(v1, t => t.Rename("nope", "x")), "no column \"nope\""),
            ("add-a-column-without-opt-or-a-default", () => Tables.EvolveTable(v1, t => t.Add("dir", Str())), "needs opt(...) or a default"),
            ("drop-a-key-column", () => Tables.EvolveTable(v1, t => t.Drop("id")), "\"id\" is a key column"),
            ("rename-after-a-drop", () => Tables.EvolveTable(v1, t => t.Drop("at").Rename("file", "path")), "rename comes before the data step"),
            ("a-second-data-step", () => Tables.EvolveTable(v1, t => t.Rows(_ => StorePatch()).Rows(_ => StorePatch())), "rows comes before the data step"),
            ("an-existing-index", () => Tables.EvolveTable(v1, t => t.Index("by_file", ["at"])), "index \"by_file\" exists"),
            ("dropindex-of-an-unknown-index", () => Tables.EvolveTable(v1, t => t.DropIndex("nope")), "no index \"nope\""),
            ("an-index-over-an-opt-column", () => Tables.EvolveTable(v1, t => t.Index("by_meta", ["meta"])), "is not a non-opt str() or int() column"),
            ("a-json-edit-of-a-scalar-column", () => Tables.EvolveTable(v1, t => t.Json("file", Tables.Json(Obj(), new JsonColumnOptions { Version = 2 }))),
                "is not a json(...) column"),
            ("a-json-edit-changing-the-schema-at-the-cell-s-version-d9",
                () => Tables.EvolveTable(v1, t => t.Json("meta", Tables.Json(Obj(("b", Str())), new JsonColumnOptions { Version = 1 }))), "changes its schema at version 1"),
            ("a-json-edit-going-back-a-version", () => Tables.EvolveTable(
                Tables.EvolveTable(v1, t => t.Json("meta", Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 2 }))),
                t => t.Json("meta", Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 1 }))), "goes back from version 2"),
            ("an-add-colliding-by-case-with-a-column-a-later-drop-removes", () => Tables.EvolveTable(v1, t => t.Add("File", Str(@default: "")).Drop("file")),
                "column \"file\" exists"),
            ("a-rename-colliding-by-case-with-another-column", () => Tables.EvolveTable(v1, t => t.Rename("at", "FILE")), "column \"file\" exists"),
            ("a-name-folding-to-a-column-s-only-beyond-ascii-sqlite-folds-ascii-the-name-rule-not-a-collision", () =>
            {
                var kinds = Tables.DefineTable("kinds", new TableOptions { Version = 1, Columns = StoreColumns(("id", Str()), ("kind", Str())), Key = ["id"] });
                return Tables.EvolveTable(kinds, t => t.Add("Kind", Opt(Str())));
            }, "expected [A-Za-z_][A-Za-z0-9_]*"),
            ("an-index-on-a-column-missing-at-that-edit-though-a-later-dropindex-removes-the-index",
                () => Tables.EvolveTable(v1, t => t.Index("bad", ["missing"]).DropIndex("bad")), "index \"bad\": no column \"missing\""),
            ("an-index-on-a-column-dropped-before-it", () => Tables.EvolveTable(v1, t => t.Drop("at").Index("by_at", ["at"])), "index \"by_at\": no column \"at\""),
            ("a-rename-to-a-name-breaking-the-column-name-rule-though-a-later-rename-fixes-it",
                () => Tables.EvolveTable(v1, t => t.Rename("at", "bad\u0000name").Rename("bad\u0000name", "when")),
                "rename: column \"bad\u0000name\": expected [A-Za-z_][A-Za-z0-9_]*"),
            ("a-rename-to-a-name-breaking-the-column-name-rule-though-a-later-drop-removes-it",
                () => Tables.EvolveTable(v1, t => t.Rename("at", "1st").Drop("1st")), "rename: column \"1st\": expected"),
            ("an-index-name-outside-a-z0-9", () => Tables.EvolveTable(v1, t => t.Index("By", ["file"])), "expected [a-z0-9_]+"),
        };
        return
        [
            Case("store.table-evolve.versions-each-evolvetable-appends-version-1-materialized-and-fingerprinted-linked-to-the-one-before", () =>
            {
                var v2 = Tables.EvolveTable(v1, t => t.Add("dir", Str(@default: "")));
                var v3 = Tables.EvolveTable(v2, t => t.Index("by_dir", ["dir"]));
                Sequence([2L, 3L], [v2.Version, v3.Version], "the versions");
                Check(ReferenceEquals(v3.Evolution?.Previous, v2), "version 3 follows version 2");
                Check(ReferenceEquals(v2.Evolution?.Previous, v1), "version 2 follows version 1");
                Equal(3, new HashSet<string>([v1.Fingerprint, v2.Fingerprint, v3.Fingerprint]).Count, "three fingerprints");
                Sequence(["by_file: file, at", "by_dir: dir"], StoreIndexesText(v3.Indexes), "the indexes");
            }),
            Case("store.table-evolve.versions-the-edit-must-return-the-editor-it-was-given-contracts-rule", () =>
            {
                Refuses(() => Tables.EvolveTable(v1, _ => null!), "edits: evolveTable must return the editor it was given");
            }),
            Case("store.table-evolve.edits-are-recorded-before-and-after-the-data-step-the-step-reads-the-columns-as-they-stand-there", () =>
            {
                var v2 = Tables.EvolveTable(v1, t => t
                    .Rename("file", "path")
                    .Add("dir", Str(@default: ""))
                    .Rows(row => new JsonObject { ["dir"] = row["path"]?.DeepClone() })
                    .Drop("at")
                    .Index("by_dir", ["dir"]));
                var migration = v2.Evolution!.Migration;
                Json("""[{"kind":"rename","from":"file","to":"path"},{"kind":"add","name":"dir","column":{"type":"str","default":""}}]""",
                    StoreEditsJson(migration.Before), "the edits before the step");
                Json("""[{"kind":"dropIndex","name":"by_file"},{"kind":"drop","name":"at"},{"kind":"index","name":"by_dir","columns":["dir"]}]""",
                    StoreEditsJson(migration.After), "the edits after the step");
                var step = migration.Step!;
                Check(step is DataStep.Rows, "a rows step");
                Sequence(["id", "path", "at", "meta", "dir"], step.Table.Columns.Select(column => column.Name), "the step's columns");
                Sequence(["id", "path", "meta", "dir"], v2.Columns.Select(column => column.Name), "version 2's columns");
                Sequence(["by_dir: dir"], StoreIndexesText(v2.Indexes), "version 2's indexes");
            }),
            Case("store.table-evolve.edits-a-rename-carries-the-key-and-the-indexes", () =>
            {
                var v2 = Tables.EvolveTable(v1, t => t.Rename("id", "uid").Rename("file", "path"));
                Sequence(["uid"], v2.Key, "the key");
                Sequence(["by_file: path, at"], StoreIndexesText(v2.Indexes), "the indexes");
            }),
            Case("store.table-evolve.edits-an-update-step-is-compiled-at-declaration-against-the-columns-there", () =>
            {
                var v2 = Tables.EvolveTable(v1, t => t.Update(e => StorePatch(("at", Add(e["at"], 1))), e => Eq(e["file"], "x")));
                var step = (DataStep.Update)v2.Evolution!.Migration.Step!;
                Json("""{"eq":["file","x"]}""", step.Where!.ToJson(), "the step's where");
                Json("""{"at":{"add":[{"col":"at"},1]}}""", step.Set.ToJson(), "the step's set");
                StoreThrows(() => Tables.EvolveTable(v1, t => t.Update(StoreValue("""{"id":"x"}"""))), "key column \"id\" cannot change");
            }),
            Case("store.table-evolve.edits-a-json-edit-changes-the-column-s-version-and-keeps-its-nullability", () =>
            {
                var v2 = Tables.EvolveTable(v1, t => t.Json("meta", Tables.Json(Obj(("a", Int()), ("b", Int())), new JsonColumnOptions
                {
                    Version = 2,
                    Migrate = (old, _) =>
                    {
                        var migrated = old!.DeepClone().AsObject();
                        migrated["b"] = 0;
                        return migrated;
                    },
                })));
                var meta = v2.ByName["meta"];
                Check(meta.Type == ColumnType.Json && meta.Optional, "meta stays an opt json(...) column");
                var before = StoreEditsJson(v2.Evolution!.Migration.Before);
                Equal(1, before.Count, "one edit");
                Json("""{"kind":"json","name":"meta"}""", new JsonObject { ["kind"] = before[0]!["kind"]!.DeepClone(), ["name"] = before[0]!["name"]!.DeepClone() }, "a json edit");
                var column = before[0]!["column"]!.AsObject();
                Json("""{"type":"json","optional":true,"version":2}""",
                    new JsonObject { ["type"] = column["type"]!.DeepClone(), ["optional"] = column["optional"]!.DeepClone(), ["version"] = column["version"]!.DeepClone() },
                    "the edit's column");
            }),
            Case("store.table-evolve.checks-a-rename-may-change-only-the-case-of-its-own-column", () =>
            {
                Check(Tables.EvolveTable(v1, t => t.Rename("at", "AT")).ByName.ContainsKey("AT"), "renamed to AT");
            }),
            .. refusals.Select(row => Case($"store.table-evolve.checks-refuses-{row.Case}-at-declaration", () =>
            {
                StoreThrows(() => row.Declare(), row.Message, row.Case);
            })),
        ];
    }
}
