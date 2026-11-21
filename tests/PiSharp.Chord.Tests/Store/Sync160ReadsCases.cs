using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/store/reads.test.ts. Every case is ported. The store's reads
// (slice 14 §3.2, §5): the index from a table to the reads over it, the bindings of column sets (one table per name, moved or dropped by
// their owner alone), tables held while they are not ready, and the refresh a commit stages: it takes its reads from the index inside the
// step and runs none over a held table. Differences from upstream:
// - A run that follows a binding or a table's readiness is awaited by nobody; upstream waits a macrotask (setTimeout 0), the cases here
//   wait until the run arrived (Sync160Until).
// - `String(run.error)` is "ChordError: <message>" upstream; the case compares the message.
// - The two memory cases make their query in a frame of their own and collect (ChordSupport.ExpectCollected).
internal static partial class Program
{
    private static readonly HostKind Sync160ReadsSession = new("session", new() { ["id"] = Str() });

    private static readonly ColumnSet Sync160ReadsTagSet = Sync160Columns("tags.columns", [("note", Int()), ("tag", Str())], ["note", "tag"]);

    private static readonly TableDefinition Sync160ReadsTags = Sync160Table("tags", Sync160Spread(Sync160ReadsTagSet), ["note", "tag"], Sync160ReadsTagSet);

    private static readonly TableDefinition Sync160ReadsNotes = Sync160Table("notes", Sync160Schemas(("id", Int()), ("text", Str())), ["id"]);

    private static readonly TableDefinition Sync160ReadsOwned =
        Sync160Table("owned", Sync160Schemas(("id", Int()), ("session", Tables.Host(Sync160ReadsSession))), ["id"]);

    private static readonly Query Sync160ReadsTagged = Tables.Query(Sync160ReadsNotes)
        .Where(n => Exists(Sync160ReadsTagSet, Sync160Match(("note", n["id"])), t => Eq(t["tag"], "red")));

    /// <summary>An open query of <paramref name="query"/> over <paramref name="handle"/>'s table with one read of it, and every run its
    /// sink adopted (upstream <c>opened</c>).</summary>
    private sealed class Sync160Opened
    {
        private readonly List<ReadRun> runs = [];

        internal OpenQuery Query { get; set; } = null!;

        internal OpenRead Open { get; set; } = null!;

        internal Batch Batch { get; } = Sync160Batch();

        /// <summary>The sink: a run may arrive on the store's thread.</summary>
        internal void Adopt(ReadRun run)
        {
            lock (runs) runs.Add(run);
        }

        /// <summary>Every run adopted so far.</summary>
        internal IReadOnlyList<ReadRun> All
        {
            get
            {
                lock (runs) return [.. runs];
            }
        }
    }

    private static async Task<Sync160Opened> Sync160Open(ChordStore store, TableHandle handle, Query? query = null)
    {
        var opened = new Sync160Opened();
        var spec = new QuerySpec(query ?? Sync160ReadsTagged, handle.Sql.Table, Projection.All, handle);
        // A binding changed: its one read runs again, as a live query's.
        opened.Query = new OpenQuery(new ReadSite(store, opened.Batch), spec, ReadShape.Rows, () => _ = opened.Open.LoadAsync(), ChordContext.Background);
        opened.Open = opened.Query.Read(new ReadKind.Rows(new ReadRange(5)), new ReadSink(opened.Adopt));
        await opened.Open.LoadAsync();
        return opened;
    }

    /// <summary>Commits one put into <paramref name="handle"/>'s table with the refresh a transaction stages (upstream <c>put</c>).</summary>
    private static Task Sync160Put(ChordStore store, TableHandle handle, JsonObject row, Batch batch) =>
        Sync160Commit(store, batch, ChordContext.Background, changes => changes(handle).Put(row));

    /// <summary>What a run shows: the ids of its rows, or its kind (upstream <c>shown</c>).</summary>
    private static string Sync160Shown(ReadRun? run) => run switch
    {
        null => "none",
        ReadRun.Rows rows => "[" + string.Join(",", rows.Result.Rows.Select(row => Sync160Stored(row, "id"))) + "]",
        ReadRun.Count => "count",
        _ => "failed",
    };

    private static IEnumerable<(string, Func<Task>)> StoreSync160ReadsCases()
    {
        var (tagSet, tags, notes, owned) = (Sync160ReadsTagSet, Sync160ReadsTags, Sync160ReadsNotes, Sync160ReadsOwned);
        var context = ChordContext.Background;
        return
        [
            Case("store.reads.bindings-bind-one-table-per-set-name-another-owners-other-table-is-refused-naming-both-only-the-owner-unbinds", () =>
            {
                var reads = new Reads();
                var (owner, other) = (new object(), new object());
                reads.Bind("tags.columns", new Bound("a:tags", tags), owner);
                Equal("a:tags", reads.Get("tags.columns")?.Table, "the binding");
                Refuses(() => reads.Bind("tags.columns", new Bound("b:tags", tags), other), "column set \"tags.columns\" is exposed by tables \"a:tags\" and \"b:tags\"");
                reads.Unbind("tags.columns", other);
                Sequence(["tags.columns"], reads.BoundBy(owner), "only the owner unbinds");
                // The owner moves the set to another of its tables, then drops it.
                reads.Bind("tags.columns", new Bound("a:tags2", tags), owner);
                Equal("a:tags2", reads.Get("tags.columns")?.Table, "the moved binding");
                reads.Unbind("tags.columns", owner);
                Check(reads.Get("tags.columns") is null, "the dropped binding");
            }),
            Case("store.reads.bindings-tell-a-sets-watchers-of-a-binding-a-move-a-drop-and-its-tables-readiness-until-they-release", () =>
            {
                var reads = new Reads();
                var owner = new object();
                var told = 0;
                var release = reads.Watch("tags.columns", () => told++);
                reads.Bind("tags.columns", new Bound("a:tags", tags), owner);
                // The same binding again changes nothing.
                reads.Bind("tags.columns", new Bound("a:tags", tags), owner);
                reads.Hold("a:tags");
                Check(reads.IsHeld("a:tags"), "the table is held");
                reads.Ready("a:tags");
                reads.Ready("a:tags");
                reads.Unbind("tags.columns", owner);
                Equal(3, told, "a binding, a readiness, a drop");
                release();
                reads.Bind("tags.columns", new Bound("a:tags", tags), owner);
                Equal(3, told, "released");
            }),
            Case("store.reads.a-commits-run-of-a-plan-the-query-no-longer-has-is-dropped-it-cannot-heal-the-failure-that-followed-it", async () =>
            {
                // Astra: another owner's commit ran the read; the set was withdrawn (a failed run, unavailable); the commit then adopted
                // the run its step had made with the old plan, and the read showed rows of a set nobody serves.
                await using var store = await MemoryStore();
                var (noteRows, tagRows) = (await TableIn(store, notes), await TableIn(store, tags));
                store.ReadIndex.Bind(tagSet.Name, new Bound(tagRows.Sql.Table, tags), tagRows);
                var opened = await Sync160Open(store, noteRows);
                await Sync160Put(store, tagRows, new JsonObject { ["note"] = 1, ["tag"] = "red" }, opened.Batch);
                // The step runs the read: the new note passes its condition.
                var before = opened.All.Count;
                var pause = Sync160Pause();
                var committed = Sync160Commit(store, opened.Batch, context, changes => changes(noteRows).Put(new JsonObject { ["id"] = 1, ["text"] = "t" }), pause.Write);
                await pause.Ran;
                store.ReadIndex.Unbind(tagSet.Name, tagRows);
                await Sync160Until(() => opened.All.Count > before, "the withdrawn set is a failed run");
                Check(opened.All.Skip(before).All(run => run is ReadRun.Failed) && opened.All.Count == before + 1, "one failed run");
                var failed = opened.All.Count;
                pause.Release();
                await committed;
                Equal(failed, opened.All.Count, "the commit's run of the old plan is dropped");
                // Bound again, the new registration reads for itself.
                store.ReadIndex.Bind(tagSet.Name, new Bound(tagRows.Sql.Table, tags), tagRows);
                await Sync160Until(() => opened.All.Count > failed, "the bound set runs once");
                Equal("[[1]]", Sync160Shown(opened.All[^1]), "the rows of the new registration");
                opened.Query.Close();
            }),
            Case("store.reads.a-run-a-commit-made-before-the-read-closed-still-reaches-its-sink-a-lists-retired-page", async () =>
            {
                await using var store = await MemoryStore();
                var noteRows = await TableIn(store, notes);
                var opened = await Sync160Open(store, noteRows, Tables.Query(notes));
                var pause = Sync160Pause();
                var committed = Sync160Commit(store, opened.Batch, context, changes => changes(noteRows).Put(new JsonObject { ["id"] = 1, ["text"] = "t" }), pause.Write);
                await pause.Ran;
                opened.Open.Close();
                pause.Release();
                await committed;
                Equal("[[1]]", Sync160Shown(opened.All[^1]), "the run reached the sink");
                opened.Query.Close();
            }),
            Case("store.reads.the-index-enters-a-read-under-every-table-it-uses-the-other-owners-commit-runs-it-closed-nothing-does", async () =>
            {
                await using var store = await MemoryStore();
                var (noteRows, tagRows) = (await TableIn(store, notes), await TableIn(store, tags));
                store.ReadIndex.Bind(tagSet.Name, new Bound(tagRows.Sql.Table, tags), tagRows);
                var opened = await Sync160Open(store, noteRows);
                Equal(1, store.ReadIndex.On(noteRows.Sql.Table)?.Count, "under its own table");
                Equal(1, store.ReadIndex.On(tagRows.Sql.Table)?.Count, "under the exists' table");
                await Sync160Put(store, noteRows, new JsonObject { ["id"] = 1, ["text"] = "t" }, opened.Batch);
                await Sync160Put(store, tagRows, new JsonObject { ["note"] = 1, ["tag"] = "red" }, opened.Batch);
                Equal("[[1]]", Sync160Shown(opened.All[^1]), "the other owner's commit ran it");
                var before = opened.All.Count;
                opened.Query.Close();
                Check(store.ReadIndex.On(tagRows.Sql.Table) is null, "closed: out of the index");
                await Sync160Put(store, tagRows, new JsonObject { ["note"] = 1, ["tag"] = "blue" }, opened.Batch);
                Equal(before, opened.All.Count, "closed: nothing runs it");
            }),
            Case("store.reads.a-query-that-does-not-compile-is-a-failed-run-the-set-bound-later-it-compiles-and-runs-once", async () =>
            {
                await using var store = await MemoryStore();
                var (noteRows, tagRows) = (await TableIn(store, notes), await TableIn(store, tags));
                var opened = await Sync160Open(store, noteRows);
                Sequence(["unavailable: column set \"tags.columns\" is not served"],
                    opened.All.Select(run => run is ReadRun.Failed { Error: ChordError error } ? $"{error.Code}: {error.Message}" : Sync160Shown(run)), "the failed run");
                Check(opened.Open.Read is null, "no read is registered");
                store.ReadIndex.Bind(tagSet.Name, new Bound(tagRows.Sql.Table, tags), tagRows);
                await Sync160Until(() => opened.All.Count > 1, "bound: it compiles and runs");
                Equal("[]", Sync160Shown(opened.All[^1]), "its rows");
                opened.Query.Close();
            }),
            Case("store.reads.run-no-read-over-a-held-table-and-one-run-when-it-is-ready", async () =>
            {
                await using var store = await MemoryStore();
                var (noteRows, tagRows) = (await TableIn(store, notes), await TableIn(store, tags));
                store.ReadIndex.Bind(tagSet.Name, new Bound(tagRows.Sql.Table, tags), tagRows);
                var opened = await Sync160Open(store, noteRows);
                await Sync160Put(store, noteRows, new JsonObject { ["id"] = 1, ["text"] = "t" }, opened.Batch);
                var before = opened.All.Count;
                store.ReadIndex.Hold(tagRows.Sql.Table);
                // Another owner commits between two chunks of the migration: the read keeps its rows.
                await Sync160Put(store, noteRows, new JsonObject { ["id"] = 2, ["text"] = "t" }, opened.Batch);
                await Sync160Put(store, tagRows, new JsonObject { ["note"] = 2, ["tag"] = "red" }, opened.Batch);
                Equal(before, opened.All.Count, "held: no run");
                store.ReadIndex.Ready(tagRows.Sql.Table);
                await Sync160Until(() => opened.All.Count > before, "ready: one run");
                await Task.Delay(30);
                Equal(before + 1, opened.All.Count, "one run when it is ready");
                Equal("[[2]]", Sync160Shown(opened.All[^1]), "its rows");
                opened.Query.Close();
            }),
            Case("store.reads.find-the-reads-over-tables-with-a-host-column-of-a-kind-joined-and-exists-tables-included", async () =>
            {
                await using var store = await MemoryStore();
                var (noteRows, ownedRows) = (await TableIn(store, notes), await TableIn(store, owned));
                var ownedSet = Sync160Columns("owned.columns", [("id", Int()), ("session", Tables.Host(Sync160ReadsSession))], ["id"]);
                var exposing = Sync160Table("owned", Sync160Spread(ownedSet), ["id"], ownedSet);
                store.ReadIndex.Bind(ownedSet.Name, new Bound(ownedRows.Sql.Table, exposing), ownedRows);
                var direct = await Sync160Open(store, ownedRows, Tables.Query(owned));
                var through = await Sync160Open(store, noteRows, Tables.Query(notes).Where(n => Exists(ownedSet, Sync160Match(("id", n["id"])))));
                var plain = await Sync160Open(store, noteRows, Tables.Query(notes));
                Equal(2, store.ReadIndex.OnHostKind("session").Count, "the direct read and the one through an exists");
                Equal(0, store.ReadIndex.OnHostKind("server").Count, "another kind");
                foreach (var each in new[] { direct, through, plain }) each.Query.Close();
            }),
            Case("store.reads.close-the-queries-of-an-owner-that-retires-their-reads-leave-the-index-others-stay", async () =>
            {
                // Code review round 1 (Opus S1, Sol 3): a provider's attempt ended; no commit runs its queries again.
                await using var store = await MemoryStore();
                var noteRows = await TableIn(store, notes);
                var retiring = await Sync160Open(store, noteRows, Tables.Query(notes));
                var other = new object();
                var batch = Sync160Batch();
                var staying = new OpenQuery(new ReadSite(store, batch), new QuerySpec(Tables.Query(notes), noteRows.Sql.Table, Projection.All, other), ReadShape.Rows, () => { }, context);
                staying.Read(new ReadKind.Rows(new ReadRange(5)), new ReadSink(_ => { }));
                store.ReadIndex.Retire(noteRows);
                Check(retiring.Query.Closed, "the retiring owner's query is closed");
                Check(!staying.Closed, "another owner's stays");
                Equal(1, store.ReadIndex.On(noteRows.Sql.Table)?.Count, "one read is left in the index");
                var before = retiring.All.Count;
                await Sync160Put(store, noteRows, new JsonObject { ["id"] = 1, ["text"] = "t" }, retiring.Batch);
                Equal(before, retiring.All.Count, "no commit runs the retired query");
                staying.Close();
            }),
            Case("store.reads.a-read-of-a-closed-query-is-closed-never-registered-never-run", async () =>
            {
                // Code review round 2 (Sol): a list whose provider retired asked its closed query for another page; the read of the old
                // plan stayed in the index for good.
                await using var store = await MemoryStore();
                var noteRows = await TableIn(store, notes);
                var opened = await Sync160Open(store, noteRows, Tables.Query(notes));
                store.ReadIndex.Retire(noteRows);
                var runs = new List<ReadRun>();
                var late = opened.Query.Read(new ReadKind.Rows(new ReadRange(5)), new ReadSink(runs.Add));
                var known = opened.Query.ReadKnown(new ReadRange(5), [], new ReadSink(runs.Add));
                Check(late.Read is null && known.Read is null && store.ReadIndex.On(noteRows.Sql.Table) is null, "neither is registered");
                Check(await late.LoadAsync() is null, "a closed read does not load");
                await Sync160Put(store, noteRows, new JsonObject { ["id"] = 1, ["text"] = "t" }, opened.Batch);
                Equal(0, runs.Count, "no run");
            }),
            Case("store.reads.keep-nothing-of-a-closed-read", async () =>
            {
                await using var store = await MemoryStore();
                var noteRows = await TableIn(store, notes);
                var read = await Sync160ClosedRead(store, noteRows);
                await ChordSupport.ExpectCollected(read);
            }),
            Case("store.reads.keep-nothing-of-a-closed-query-that-watched-a-sets-binding", async () =>
            {
                await using var store = await MemoryStore();
                var (noteRows, tagRows) = (await TableIn(store, notes), await TableIn(store, tags));
                store.ReadIndex.Bind(tagSet.Name, new Bound(tagRows.Sql.Table, tags), tagRows);
                // Opened and closed in a function of its own: nothing of this test keeps the query.
                await ChordSupport.ExpectCollected(await Sync160ClosedQuery(store, noteRows));
                // The binding changes without a watcher left to tell.
                store.ReadIndex.Unbind(tagSet.Name, tagRows);
            }),
        ];
    }

    /// <summary>A weak reference to the registered read of a query opened and closed here.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> Sync160ClosedRead(ChordStore store, TableHandle handle)
    {
        var opened = await Sync160Open(store, handle, Tables.Query(handle.Definition));
        var read = new WeakReference(opened.Open.Read!);
        opened.Query.Close();
        return read;
    }

    /// <summary>A weak reference to a query (over the tagged notes) opened and closed here.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> Sync160ClosedQuery(ChordStore store, TableHandle handle)
    {
        var opened = await Sync160Open(store, handle);
        opened.Query.Close();
        return new WeakReference(opened.Query);
    }
}
