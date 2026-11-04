using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/table-handle.test.ts. Every case is ported, over
// TestStore (TableSupport.cs) for upstream's memoryStore and holdNextCommit; each case closes its store (upstream's afterEach). One
// assertion is not: page(q, 1.5, ...), since a C# limit is a long (a fraction does not compile); limit 0 is refused as upstream refuses
// both. Upstream's query(Other) as never needs no cast here. Rows are compared as JSON (StoreJson); an absent row is null.
internal static partial class Program
{
    private static readonly TableDefinition StoreHandleEdits = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(
            ("id", Str()),
            ("path", Str()),
            ("at", Int()),
            ("done", Bool(@default: false)),
            ("note", Opt(Str())),
            ("meta", Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 1 }))),
        Key = ["id"],
        Indexes = StoreIndexes(("by_path", ["path", "at"])),
    });

    /// <summary>A store with a table of <paramref name="count"/> rows e000..: path p/&lt;i % 3&gt;, at i.</summary>
    private static async Task<(TestStore Store, TableHandle Edits)> StoreHandleFilled(int count, TestTableOwner? owner = null)
    {
        var store = await MemoryStore();
        var edits = await TableIn(store, StoreHandleEdits, owner);
        await StoreCommitWith(store, edits, changes =>
        {
            for (var i = 0; i < count; i++)
                changes.Put(new JsonObject
                {
                    ["id"] = "e" + i.ToString("000", CultureInfo.InvariantCulture),
                    ["path"] = $"p/{i % 3}",
                    ["at"] = i,
                    ["meta"] = new JsonObject { ["a"] = i },
                });
        });
        return (store, edits);
    }

    private static IEnumerable<(string, Func<Task>)> StoreTableHandleCases()
    {
        var context = ChordContext.Background;
        var definition = StoreHandleEdits;
        return
        [
            Case("store.table-handle.reads-outside-a-transaction-get-takes-a-key-object-and-reads-committed-rows-as-stored", async () =>
            {
                var (store, edits) = await StoreHandleFilled(3);
                await using var _ = store;
                Json("""{"id":"e001","path":"p/1","at":1,"done":false,"meta":{"a":1}}""", StoreJson((await edits.GetAsync(StoreValue("""{"id":"e001"}"""), context))!), "e001");
                Check(await edits.GetAsync(StoreValue("""{"id":"nope"}"""), context) is null, "an absent key");
                Rejected(await StoreRejection(edits.GetAsync(StoreValue("""{"id":"e001","path":"x"}"""), context)), "invalid", "args", "a key with another column");
            }),
            Case("store.table-handle.reads-outside-a-transaction-page-fetches-limit-1-for-next-and-the-cursor-continues-in-order-with-the-key-tie-break", async () =>
            {
                var (store, edits) = await StoreHandleFilled(10);
                await using var _ = store;
                var q = Tables.Query(definition).Where(e => Prefix(e["path"], "p/")).OrderBy(e => e["path"], OrderDirection.Desc);
                static string Label(IReadOnlyDictionary<string, JsonNode?> row) => $"{(string)row["path"]!}:{(string)row["id"]!}";
                var seen = new List<string>();
                string? after = null;
                do
                {
                    var page = await edits.PageAsync(q, 3, context, after is null ? null : new PageOptions { After = after });
                    Check(page.Items.Count <= 3, "at most 3 rows a page");
                    seen.AddRange(page.Items.Select(Label));
                    after = page.Next;
                } while (after is not null);
                var all = (await edits.PageAsync(q, 100, context)).Items.Select(Label).ToList();
                Sequence(all, seen, "the pages in order");
                Sequence(["p/2:e008", "p/2:e005", "p/2:e002", "p/1:e007"], all.Take(4), "the first rows");
                Check((await edits.PageAsync(q, 4, context)).Next is not null, "a next cursor when more follow");
                Check((await edits.PageAsync(Tables.Query(definition), 10, context)).Next is null, "no next cursor at the end");
            }),
            Case("store.table-handle.reads-outside-a-transaction-rows-reads-every-page-count-counts", async () =>
            {
                var (store, edits) = await StoreHandleFilled(250);
                await using var _ = store;
                var ids = new List<string>();
                await foreach (var row in edits.Rows(Tables.Query(definition).Where(e => Gte(e["at"], 10)), context)) ids.Add((string)row["id"]!);
                Equal(240, ids.Count, "the rows from at 10");
                Equal("e010", ids[0], "the first row");
                Equal(84L, await edits.CountAsync(Tables.Query(definition).Where(e => Eq(e["path"], "p/0")), context), "the rows of p/0");
                Equal(250L, await edits.CountAsync(Tables.Query(definition).Where(e => IsNull(e["note"])), context), "the rows without a note");
            }),
            Case("store.table-handle.reads-outside-a-transaction-refuses-a-query-of-another-table-a-bad-limit-and-a-foreign-cursor-before-the-backend", async () =>
            {
                var (store, edits) = await StoreHandleFilled(1);
                await using var _ = store;
                var other = Tables.DefineTable("other", new TableOptions { Version = 1, Columns = StoreColumns(("id", Str())), Key = ["id"] });
                Rejected(await StoreRejection(edits.PageAsync(Tables.Query(other), 1, context)), null, "args", "a query of another table");
                Rejected(await StoreRejection(edits.PageAsync(Tables.Query(definition), 0, context)), null, "args", "limit 0");
                Rejected(await StoreRejection(edits.PageAsync(Tables.Query(definition), 1, context, new PageOptions { After = "x" })), null, "args", "a foreign cursor");
            }),
            Case("store.table-handle.reads-outside-a-transaction-reads-go-through-the-store-s-guard-refused-once-the-store-closed", async () =>
            {
                var (store, edits) = await StoreHandleFilled(1);
                await store.CloseAsync();
                Rejected(await StoreRejection(edits.GetAsync(StoreValue("""{"id":"e000"}"""), context)), "released", null, "a read after close");
            }),
            Case("store.table-handle.unreadable-cells-a-json-cell-of-a-newer-version-fails-the-owner-and-the-read-the-data-intact", async () =>
            {
                var owner = TableOwner();
                var (store, edits) = await StoreHandleFilled(1, owner);
                await using var _ = store;
                await store.ReadAsync(async executor =>
                {
                    await executor.RunAsync("UPDATE \"app.store:edits\" SET \"meta$v\" = 2");
                    return true;
                }, context);
                Rejected(await StoreRejection(edits.GetAsync(StoreValue("""{"id":"e000"}"""), context)), "unavailable", null, "a newer cell");
                Equal(1, owner.Failures.Count, "the owner failed once");
                var stored = await store.ReadAsync(executor => executor.GetAsync("SELECT meta FROM \"app.store:edits\""), context);
                Equal("{\"a\":0}", (string?)stored!["meta"], "the cell intact");
            }),
            Case("store.table-handle.the-version-fence-of-old-handles-r15-slice-7b-5-refuses-reads-through-a-handle-older-than-the-stored-table-released-version", async () =>
            {
                await using var store = await MemoryStore();
                var old = await TableIn(store, definition);
                var v2 = Tables.EvolveTable(definition, t => t.Rename("note", "comment"));
                await TableIn(store, v2);
                Rejected(await StoreRejection(old.GetAsync(StoreValue("""{"id":"e0"}"""), context)), "released", "version", "a get");
                Rejected(await StoreRejection(old.CountAsync(Tables.Query(definition), context)), "released", "version", "a count");
                StoreThrows(() => old.AssertVersion(), "the store has version 2");
            }),
            Case("store.table-handle.the-version-fence-against-a-migration-in-flight-fix-round-1-sol-8-refuses-an-old-handle-s-read-made-while-the-migration-s-commit-runs-rather-than-issuing-obsolete-sql", async () =>
            {
                var v1 = Tables.DefineTable("race", new TableOptions { Version = 1, Columns = StoreColumns(("id", Str()), ("old", Str())), Key = ["id"] });
                var v2 = Tables.EvolveTable(v1, t => t.Rename("old", "new"));
                await using var store = await MemoryStore();
                var old = await TableIn(store, v1);
                var (entered, release) = store.HoldNextCommit();
                var changing = TableIn(store, v2);
                await entered;
                var reading = StoreRejection(old.GetAsync(StoreValue("""{"id":"x"}"""), context));
                release();
                await changing;
                Rejected(await reading, "released", "version", "the old handle's read");
                Accepts(store.AssertUsable, "the store goes on");
            }),
        ];
    }
}
