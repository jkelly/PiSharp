using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/table-changes.test.ts. Every case is ported, over
// TestStore (TableSupport.cs) for upstream's memoryStore; each case closes its store (upstream's afterEach). Upstream's callback casts
// ((e: { hits: never }) => ...) are type-level only. Rows are compared as JSON (StoreJson); an absent row is null (upstream's undefined).
internal static partial class Program
{
    private static readonly TableDefinition StoreChangesEdits = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("path", Str()), ("hits", Int(@default: 0)), ("note", Opt(Str()))),
        Key = ["id"],
    });

    /// <summary>Changes to a table holding e1 (committed); the staged list they append to.</summary>
    private static async Task<(TestStore Store, TableChanges Changes, List<IStagedWrite> Staged)> StoreChangesSeeded()
    {
        var store = await MemoryStore();
        var edits = await TableIn(store, StoreChangesEdits);
        await StoreCommitWith(store, edits, changes => changes.Put(StoreValue("""{"id":"e1","path":"a/1","hits":1}""")));
        var staged = new List<IStagedWrite>();
        return (store, new TableChanges(edits, staged), staged);
    }

    private static IEnumerable<(string, Func<Task>)> StoreTableChangesCases()
    {
        var context = ChordContext.Background;
        var e1 = StoreValue("""{"id":"e1"}""");
        return
        [
            Case("store.table-changes.staging-checks-at-the-call-appends-in-order-and-touches-no-backend-before-the-commit", async () =>
            {
                var (store, changes, staged) = await StoreChangesSeeded();
                await using var _ = store;
                changes.Put(StoreValue("""{"id":"e2","path":"a/2"}"""));
                StoreThrows(() => changes.Put(StoreValue("""{"id":"e3"}""")), "column \"path\" is missing");
                StoreThrows(() => changes.Update(e1, StoreValue("""{"id":"x"}""")), "cannot change");
                changes.Update(e1, StoreValue("""{"note":"n"}"""));
                changes.Delete(StoreValue("""{"id":"e9"}"""));
                Equal(3, staged.Count, "three staged writes");
                var count = await store.ReadAsync(executor => executor.GetAsync("SELECT count(*) AS n FROM \"app.store:edits\""), context);
                Equal("[1]", StoreParams([count!["n"]]), "one committed row");
            }),
            Case("store.table-changes.the-tx-get-overlay-replays-the-transaction-s-own-put-update-and-delete-over-the-committed-row", async () =>
            {
                var (store, changes, _) = await StoreChangesSeeded();
                await using var __ = store;
                changes.Update(e1, e => StorePatch(("hits", Add(e["hits"], 2))));
                Json("""{"id":"e1","path":"a/1","hits":3}""", StoreJson((await changes.GetAsync(e1, context))!), "after an update");
                changes.Update(e1, StoreValue("""{"note":"n"}"""));
                Json("""{"id":"e1","path":"a/1","hits":3,"note":"n"}""", StoreJson((await changes.GetAsync(e1, context))!), "after two updates");
                changes.Delete(e1);
                Check(await changes.GetAsync(e1, context) is null, "deleted");
                changes.Put(StoreValue("""{"id":"e1","path":"b"}"""));
                changes.Update(e1, e => StorePatch(("hits", Add(e["hits"], 1))));
                Json("""{"id":"e1","path":"b","hits":1}""", StoreJson((await changes.GetAsync(e1, context))!), "put again and updated");
            }),
            Case("store.table-changes.the-tx-get-overlay-an-update-of-an-absent-key-changes-nothing", async () =>
            {
                var (store, changes, _) = await StoreChangesSeeded();
                await using var __ = store;
                var e9 = StoreValue("""{"id":"e9"}""");
                changes.Update(e9, StoreValue("""{"note":"n"}"""));
                Check(await changes.GetAsync(e9, context) is null, "still absent");
            }),
            Case("store.table-changes.the-tx-get-overlay-is-refused-on-a-table-after-an-updatewhere-or-deletewhere-w10-other-tables-are-unaffected", async () =>
            {
                var (store, changes, _) = await StoreChangesSeeded();
                await using var __ = store;
                Check(await changes.GetAsync(e1, context) is not null, "e1 before the bulk write");
                changes.UpdateWhere(e => Prefix(e["path"], "a/"), StoreValue("""{"note":"bulk"}"""));
                Rejected(await StoreRejection(changes.GetAsync(e1, context)), "invalid", "read_after_bulk", "get after an updateWhere");
                var other = new TableChanges(changes.Handle, []);
                other.DeleteWhere(e => Eq(e["id"], "x"));
                Rejected(await StoreRejection(other.GetAsync(e1, context)), null, "read_after_bulk", "get after a deleteWhere");
                Check(await new TableChanges(changes.Handle, []).GetAsync(e1, context) is not null, "other changes are unaffected");
            }),
        ];
    }
}
