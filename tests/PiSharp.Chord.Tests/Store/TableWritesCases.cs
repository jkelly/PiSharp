using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/table-writes.test.ts. Every case is ported, over
// TestStore (TableSupport.cs) with its outbox for upstream's memoryStore({ outbox: true }); each case closes its store (upstream's
// afterEach). The outbox's writes are compared as JSON, ignoring key order as toEqual does. Upstream's set callback returns note:
// undefined to clear the column, which is Operand.Undefined here (StorePatch).
internal static partial class Program
{
    private static readonly TableDefinition StoreWritesEdits = Tables.DefineTable("edits", new TableOptions
    {
        Version = 2,
        Columns = StoreColumns(
            ("id", Str()),
            ("path", Str()),
            ("hits", Int(@default: 0)),
            ("note", Opt(Str())),
            ("meta", Opt(Tables.Json(Obj(), new JsonColumnOptions { Version = 3 })))),
        Key = ["id"],
        Indexes = StoreIndexes(("by_path", ["path"])),
    });

    /// <summary>A store with an outbox and the edits table.</summary>
    private static async Task<(TestStore Store, TableHandle Handle)> StoreWritesOpened()
    {
        var store = await MemoryStore(outbox: true);
        return (store, await TableIn(store, StoreWritesEdits));
    }

    /// <summary>Applies <paramref name="edits"/> to the edits table as the migration to version 3 does, one commit.</summary>
    private static Task StoreWritesAltered(TestStore store, params TableEdit[] edits) =>
        store.CommitAsync([.. edits.Select(edit => StoreInternal.StageEdit("app.store:edits", 3, edit))], CommitFence.Open, ChordContext.Background);

    /// <summary>The physical columns of the edits table.</summary>
    private static async Task<List<string>> StoreWritesPhysical(TestStore store)
    {
        var rows = await store.ReadAsync(executor => executor.AllAsync("SELECT name FROM pragma_table_info('app.store:edits')"), ChordContext.Background);
        return [.. rows.Select(row => (string)row["name"]!)];
    }

    private static IEnumerable<(string, Func<Task>)> StoreTableWritesCases()
    {
        const string table = "app.store:edits";
        var context = ChordContext.Background;
        var edits = StoreWritesEdits;
        var a = StoreValue("""{"id":"a"}""");
        return
        [
            Case("store.table-writes.schema-writes-record-the-table-and-its-indexes-the-table-write-carries-version-state-columns-key-fingerprint", async () =>
            {
                var (store, _) = await StoreWritesOpened();
                await using var __ = store;
                var commit = (await store.OutboxAsync(0, 1, context))[0];
                var expected = new JsonArray(
                    new JsonObject
                    {
                        ["kind"] = "table",
                        ["table"] = table,
                        ["version"] = 2,
                        ["state"] = "ready",
                        ["columns"] = StoreInternal.ColumnsJson(edits.Record),
                        ["key"] = new JsonArray("id"),
                        ["fingerprint"] = edits.Fingerprint,
                    },
                    new JsonObject { ["kind"] = "index", ["table"] = table, ["name"] = "by_path", ["columns"] = new JsonArray("path") });
                Json(expected.ToJsonString(), commit["writes"], "the schema writes");
                var record = store.Table(table)!;
                Check(record.Version == 2 && record.State == TableState.Ready, "version 2, ready");
                Json("""{"by_path":["path"]}""", StoreInternal.IndexesJson(record.Indexes), "the recorded indexes");
                var indexes = await store.ReadAsync(executor => executor.AllAsync("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = ?", table), context);
                Check(indexes.Any(row => (string?)row["name"] == $"{table}:by_path"), "the index exists");
            }),
            Case("store.table-writes.schema-writes-an-index-write-for-a-table-the-store-does-not-have-refuses-the-step-d4-the-store-goes-on", async () =>
            {
                var (store, _) = await StoreWritesOpened();
                await using var __ = store;
                var write = StoreInternal.StageIndex(new TableWrite.Index("app.store:nope", "x", ["id"]));
                Rejected(await StoreRejection(store.CommitAsync([write], CommitFence.Open, context)), null, null, "an index of no table", "not in the store");
                Equal(1, await store.ReadAsync(_ => Task.FromResult(1), context), "the store goes on");
            }),
            Case("store.table-writes.row-writes-in-the-outbox-carry-what-a-replica-needs-put-with-v-and-json-versions-update-and-delete-with-counts-the-canonical-set", async () =>
            {
                var (store, handle) = await StoreWritesOpened();
                await using var __ = store;
                await StoreCommitWith(store, handle, changes =>
                {
                    changes.Put(StoreValue("""{"id":"a","path":"x/1","meta":{}}"""));
                    changes.Put(StoreValue("""{"id":"b","path":"x/2"}"""));
                    changes.Update(a, e => StorePatch(("hits", Add(e["hits"], 1)), ("note", Operand.Undefined), ("meta", new JsonObject())));
                    changes.Update(StoreValue("""{"id":"zz"}"""), StoreValue("""{"note":"n"}"""));
                    changes.Delete(StoreValue("""{"id":"b"}"""));
                    changes.Delete(StoreValue("""{"id":"b"}"""));
                });
                var commit = (await store.OutboxAsync(0, 10, context))[1];
                Json("""
                    [
                        {"kind":"put","table":"app.store:edits","row":{"id":"a","path":"x/1","hits":0,"meta":{}},"version":2,"columns":{"meta":3}},
                        {"kind":"put","table":"app.store:edits","row":{"id":"b","path":"x/2","hits":0},"version":2,"columns":{}},
                        {"kind":"update","table":"app.store:edits","key":{"id":"a"},"set":{"hits":{"add":[{"col":"hits"},1]},"note":null,"meta":{"json":{}}},"columns":{"meta":3},"count":1},
                        {"kind":"update","table":"app.store:edits","key":{"id":"zz"},"set":{"note":"n"},"columns":{},"count":0},
                        {"kind":"delete","table":"app.store:edits","key":{"id":"b"},"count":1},
                        {"kind":"delete","table":"app.store:edits","key":{"id":"b"},"count":0}
                    ]
                    """, commit["writes"], "the row writes");
            }),
            Case("store.table-writes.row-writes-in-the-outbox-a-put-is-an-upsert-the-whole-row-replaced", async () =>
            {
                var (store, handle) = await StoreWritesOpened();
                await using var __ = store;
                await StoreCommitWith(store, handle, changes => changes.Put(StoreValue("""{"id":"a","path":"x","hits":5,"note":"n"}""")));
                await StoreCommitWith(store, handle, changes => changes.Put(StoreValue("""{"id":"a","path":"y"}""")));
                Json("""{"id":"a","path":"y","hits":0}""", StoreJson((await handle.GetAsync(a, context))!), "the replaced row");
            }),
            Case("store.table-writes.row-writes-in-the-outbox-updatewhere-and-deletewhere-are-one-statement-each-recorded-with-where-set-and-count", async () =>
            {
                var (store, handle) = await StoreWritesOpened();
                await using var __ = store;
                await StoreCommitWith(store, handle, changes =>
                {
                    for (var i = 0; i < 5; i++) changes.Put(new JsonObject { ["id"] = $"e{i}", ["path"] = i < 3 ? $"a/{i}" : $"b/{i}" });
                });
                var (_, counting, counts) = await ChordSupport.Metered();
                await StoreCommitWith(store, handle, changes =>
                {
                    changes.UpdateWhere(e => Prefix(e["path"], "a/"), e => StorePatch(("path", ReplacePrefix(e["path"], "a/", "c/"))));
                    changes.DeleteWhere(e => Eq(e["path"], "b/4"));
                }, counting);
                // The two bulk writes, the outbox commit, lastSeq.
                Equal(4d, counts()["chord.store.statements"], "the step's statements");
                var commit = (await store.OutboxAsync(0, 10, context))[2];
                Json("""
                    [
                        {"kind":"updateWhere","table":"app.store:edits","where":{"prefix":["path","a/"]},"set":{"path":{"replacePrefix":[{"col":"path"},"a/","c/"]}},"columns":{},"count":3},
                        {"kind":"deleteWhere","table":"app.store:edits","where":{"eq":["path","b/4"]},"count":1}
                    ]
                    """, commit["writes"], "the bulk writes");
                var page = await handle.PageAsync(Tables.Query(edits), 10, context);
                Sequence(["c/0", "c/1", "c/2", "b/3"], page.Items.Select(row => (string)row["path"]!), "the paths after the bulk writes");
            }),
            Case("store.table-writes.alter-writes-rename-a-column-a-json-one-s-version-column-the-key-and-the-indexes-follow", async () =>
            {
                var (store, handle) = await StoreWritesOpened();
                await using var __ = store;
                await StoreCommitWith(store, handle, changes => changes.Put(StoreValue("""{"id":"a","path":"p"}""")));
                await StoreWritesAltered(store, new AlterEdit.Rename("meta", "info"), new AlterEdit.Rename("path", "file"));
                Sequence(["id", "file", "hits", "note", "info", "info$v", "$v"], await StoreWritesPhysical(store), "the physical columns");
                Json("""{"by_path":["file"]}""", StoreInternal.IndexesJson(store.Table(table)!.Indexes), "the index follows");
                Sequence(["id", "file", "hits", "note", "info"], store.Table(table)!.Columns.Keys, "the recorded columns");
            }),
            Case("store.table-writes.alter-writes-add-a-column-existing-rows-get-its-default-an-opt-json-one-adds-its-version-column", async () =>
            {
                var (store, handle) = await StoreWritesOpened();
                await using var __ = store;
                await StoreCommitWith(store, handle, changes => changes.Put(StoreValue("""{"id":"a","path":"p"}""")));
                await StoreWritesAltered(store,
                    new AlterEdit.Add("level", new ColumnRecord(ColumnType.Int, Default: 3d)),
                    new AlterEdit.Add("extra", new ColumnRecord(ColumnType.Json, Optional: true, Version: 1, Fingerprint: "f")));
                Sequence(["id", "path", "hits", "note", "meta", "meta$v", "$v", "level", "extra", "extra$v"], await StoreWritesPhysical(store), "the physical columns");
                var rows = await store.ReadAsync(executor => executor.AllAsync($"SELECT level FROM \"{table}\""), context);
                Equal("[3]", StoreParams(rows.Select(row => row["level"])), "the existing row's default");
                Json("""{"type":"int","default":3}""", store.Table(table)!.Columns["level"].ToJson(), "the recorded column");
            }),
            Case("store.table-writes.alter-writes-drop-a-column-with-its-version-column-a-json-edit-changes-the-record-only", async () =>
            {
                var (store, handle) = await StoreWritesOpened();
                await using var __ = store;
                await StoreCommitWith(store, handle, changes => changes.Put(StoreValue("""{"id":"a","path":"p"}""")));
                var column = new ColumnRecord(ColumnType.Json, Optional: true, Version: 4, Fingerprint: "g");
                await StoreWritesAltered(store, new AlterEdit.Drop("note"), new AlterEdit.Json("meta", column));
                Sequence(["id", "path", "hits", "meta", "meta$v", "$v"], await StoreWritesPhysical(store), "the physical columns");
                Json(column.ToJson().ToJsonString(), store.Table(table)!.Columns["meta"].ToJson(), "the json edit's record");
                await StoreWritesAltered(store, new AlterEdit.Drop("meta"));
                Sequence(["id", "path", "hits", "$v"], await StoreWritesPhysical(store), "meta and its version column dropped");
            }),
            Case("store.table-writes.alter-writes-are-recorded-as-alter-writes-with-the-migration-s-version-index-edits-as-index-and-dropindex", async () =>
            {
                var (store, _) = await StoreWritesOpened();
                await using var __ = store;
                var seq = store.LastSeq;
                await StoreWritesAltered(store, new TableEdit.DropIndex("by_path"), new AlterEdit.Drop("path"));
                var commit = (await store.OutboxAsync(seq, 1, context))[0];
                Json("""
                    [
                        {"kind":"dropIndex","table":"app.store:edits","name":"by_path"},
                        {"kind":"alter","table":"app.store:edits","version":3,"edit":{"kind":"drop","name":"path"}}
                    ]
                    """, commit["writes"], "the alter writes");
            }),
        ];
    }
}
