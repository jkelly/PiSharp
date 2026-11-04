using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/catalog.test.ts. Every case is ported, over ChordStore
// and support.ts's memoryStore (ChordSupport.cs): the first case opens a store twice on a file in a fresh temporary directory
// (upstream's mkdtemp and openNodeSqliteDatabase(path)). The failing write of the second is a staged write of its own whose statement
// fails; the commit rejects with SQLite's error, which names the missing table.
internal static partial class Program
{
    private static readonly TableDefinition StoreCatalogEdits = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("at", Int())),
        Key = ["id"],
        Indexes = StoreIndexes(("by_at", ["at"])),
    });

    private static IEnumerable<(string, Func<Task>)> StoreCatalogCases()
    {
        const string table = "app.store:edits";
        var edits = StoreCatalogEdits;
        return
        [
            Case("store.catalog.the-catalog-is-read-once-at-open-a-reopened-store-knows-its-tables", async () =>
            {
                var directory = Directory.CreateTempSubdirectory("chord-catalog-");
                try
                {
                    var path = Path.Combine(directory.FullName, "store.sqlite");
                    var first = await StoreOver(new TestSqliteDatabase(path));
                    await TableIn(first, edits);
                    await first.CloseAsync(ChordContext.Background);
                    var second = await StoreOver(new TestSqliteDatabase(path));
                    var expected = new JsonObject
                    {
                        ["table"] = table,
                        ["version"] = 1,
                        ["state"] = "ready",
                        ["columns"] = StoreInternal.ColumnsJson(edits.Record),
                        ["key"] = new JsonArray("id"),
                        ["fingerprint"] = edits.Fingerprint,
                        ["indexes"] = new JsonObject { ["by_at"] = new JsonArray("at") },
                    };
                    Json(expected.ToJsonString(), second.Table(table)?.ToJson(), "the reopened store's record");
                    await second.CloseAsync(ChordContext.Background);
                }
                finally
                {
                    directory.Delete(recursive: true);
                }
            }),
            Case("store.catalog.the-catalog-changes-in-memory-only-after-commit-returned", async () =>
            {
                await using var store = await MemoryStore();
                IStagedWrite failing = new StagedWrite(WriteOrder.Write, async step =>
                {
                    await step.RunAsync("INSERT INTO no_such_table VALUES (1)");
                    return [new TableWrite.DropIndex(table, "x")];
                });
                var write = StoreInternal.StageTable(new TableWrite.Definition(table, edits.Version, TableState.Ready, edits.Record, edits.Key, edits.Fingerprint));
                Rejected(await StoreRejection(store.CommitAsync([write, failing], CommitFence.Open, ChordContext.Background)), null, null, "the failing commit",
                    "no such table");
                Check(store.Table(table) is null, "the catalog is unchanged");
            }),
        ];
    }
}
