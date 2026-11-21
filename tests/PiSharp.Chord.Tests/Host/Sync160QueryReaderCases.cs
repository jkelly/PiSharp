using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/query-reader.test.ts.
//
// Readers of named queries (slice 14 §3.1, §3.2, test list 1 to 4, 10, 13b): self.LiveQuery and self.Count read another object's named
// query through its reference, or the owner's own; the first run comes before the reader is live; a commit of the store's owner publishes
// the diff in its batch; arguments may follow a value; a reader whose read cannot run parks and heals; refused arguments break it; the
// mapping gets only the declared columns. Differences from upstream:
// - "the loaded row is the same object" (toBe) is asserted as: the mapping did not run again, and the row is equal (State/Framed.cs: no
//   node is shared between two values).
// - "the wrong form of each call": a table handed to a reader and a named query handed to self.Query cannot be written (ITable and
//   INamedQuery are different types). What the types still allow is asserted: a named query and a table that are none of Chord's, and a
//   page size below one.
// - flushed() and delivered() are Q7Settled or Q7Until (Sync160QueriesSupport.cs).
internal static partial class Program
{
    private static readonly TableDefinition ReaderNotes = Tables.DefineTable("notes", new TableOptions
    {
        Version = 1,
        Columns = new() { ["id"] = Int(), ["board"] = Str(), ["text"] = Str(), ["stars"] = Int(@default: 0), ["hidden"] = Bool(@default: false) },
        Key = ["id"],
        Indexes = StoreIndexes(("by_board", ["board", "id"])),
    });

    private static readonly JsonObject ReaderNote = Obj(("id", Int()), ("text", Str()));

    private static readonly Contract ReaderNoteStore = Contract.Local("app.notes", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["notes"] = Queries(ReaderNote, ("ofBoard", Obj(("board", Str()))), ("one", Obj(("id", Int())))),
            ["put"] = Method(Obj(("id", Int()), ("board", Str()), ("text", Str()))),
            ["star"] = Method(Int()),
            ["rename"] = Method(Obj(("id", Int()), ("text", Str()))),
            ["drop"] = Method(Int()),
        },
    });

    private static readonly Contract ReaderBoard = Contract.Local("app.board", new()
    {
        Version = 1,
        Params = new() { ["board"] = Str() },
        Members = new() { ["notes"] = Obj(("rows", Arr(ReaderNote)), ("more", Bool())), ["count"] = Int() },
    });

    /// <summary>Serves the note store; <paramref name="mapped"/> takes the columns of every row its mapping got, <paramref name="declare"/>
    /// runs first in its factory (upstream <c>serveStore</c>).</summary>
    private static void ReaderServeStore(IFacetHost h, List<string>? mapped = null, Action<IStoreSelf>? declare = null) => h.Serve(ReaderNoteStore, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        declare?.Invoke(self);
        var table = self.Table(ReaderNotes);
        var notes = self.Queries(table, new QueriesOptions(
            Map: (row, _) =>
            {
                if (mapped is not null) lock (mapped) mapped.Add(string.Join(",", row.Keys.Order(StringComparer.Ordinal)));
                return new JsonObject { ["id"] = row["id"]!.DeepClone(), ["text"] = row["text"]!.DeepClone() };
            },
            Reads: ["id", "text"]), Q7Builders(
            ("ofBoard", Q7Builder(args =>
            {
                var board = (string)args!["board"]!;
                if (board == "broken") throw new InvalidOperationException("no such board");
                return Tables.Query(ReaderNotes).Where(n => Eq(n["board"], board)).OrderBy(n => n["id"], OrderDirection.Desc);
            })),
            ("one", Q7Builder(args => Tables.Query(ReaderNotes).Where(n => Eq(n["id"], args!["id"]!.DeepClone()))))));
        return new(new Dictionary<string, object?>
        {
            ["notes"] = notes,
            ["put"] = Q7Tx(self, (tx, row) => tx.Put(table, row)),
            ["star"] = Q7Tx(self, (tx, id) => tx.Update(table, new JsonObject { ["id"] = id!.DeepClone() }, new JsonObject { ["stars"] = 5 })),
            ["rename"] = Q7Tx(self, (tx, args) => tx.Update(table, new JsonObject { ["id"] = args!["id"]!.DeepClone() }, new JsonObject { ["text"] = args["text"]!.DeepClone() })),
            ["drop"] = Q7Tx(self, (tx, id) => tx.Delete(table, new JsonObject { ["id"] = id!.DeepClone() })),
        });
    });

    private static void ReaderServeBoard(IFacetHost h, long first = 2) => h.Serve(ReaderBoard, (parameters, _, self) =>
    {
        var ofBoard = Q7Named(self.Get(ReaderNoteStore, new()), "notes", "ofBoard");
        var args = new JsonObject { ["board"] = (string)parameters["board"]! };
        return new(new Dictionary<string, object?>
        {
            ["notes"] = self.LiveQuery(ofBoard, args, first),
            ["count"] = self.Count(ofBoard, args.DeepClone()),
        });
    });

    /// <summary>A host on a store with the note store and boards served by two plugins, metered (upstream <c>hosted</c>).</summary>
    private sealed class ReaderHosted(ChordHost host, ChordStore store, IReference notes, List<HostError> errors, ChordContext counting,
        Func<IReadOnlyDictionary<string, double>> counts) : IAsyncDisposable
    {
        internal ChordHost Host => host;

        internal ChordStore Store => store;

        internal IReference Notes => notes;

        internal List<HostError> Errors => errors;

        /// <summary>A context counting on the host's telemetry.</summary>
        internal ChordContext Counting => counting;

        internal double Runs => counts().GetValueOrDefault("chord.query.runs");

        internal Task Put(int id, string board, string text, ChordContext? context = null) =>
            notes.CallAsync("put", new JsonObject { ["id"] = id, ["board"] = board, ["text"] = text }, context ?? ChordContext.Background);

        internal Task<IReference> Board(string board) => host.GetAsync(ReaderBoard, new() { ["board"] = board }, ChordContext.Background);

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    private static async Task<ReaderHosted> ReaderHost(List<string>? mapped = null)
    {
        var store = await MemoryStore();
        var (telemetry, context, counts) = await ChordSupport.Metered();
        var errors = new List<HostError>();
        var host = Q7Host(store, errors, telemetry);
        await host.LoadAsync([Q7Plugin("@test/store", h => ReaderServeStore(h, mapped)), Q7Plugin("@test/boards", h => ReaderServeBoard(h))], context);
        return new ReaderHosted(host, store, await host.GetAsync(ReaderNoteStore, new(), context), errors, context, counts);
    }

    private static readonly Contract ReaderFilter = Contract.Local("app.filter", new()
    { Version = 1, Params = new(), Members = new() { ["board"] = Str(), ["show"] = Method(Str()) } });

    private static readonly Contract ReaderFiltered = Contract.Local("app.filtered", new()
    { Version = 1, Params = new(), Members = new() { ["notes"] = Obj(("rows", Arr(ReaderNote)), ("more", Bool())) } });

    /// <summary>A filter whose board a method sets, and a view whose arguments follow it (upstream <c>serveFiltered</c>).</summary>
    private static void ReaderServeFiltered(IFacetHost h, List<string>? factories = null)
    {
        h.Serve(ReaderFilter, (_, _, self) =>
        {
            var board = self.Writable("a");
            return new(new Dictionary<string, object?>
            {
                ["board"] = self.Derive([board], values => JsonValue.Create((string)values[0]!)),
                ["show"] = (HostMethod)((value, _) =>
                {
                    board.Set(((JsonNode)value!).GetValue<string>());
                    return Task.FromResult<object?>(null);
                }),
            });
        });
        h.Serve(ReaderFiltered, (_, _, self) =>
        {
            if (factories is not null) lock (factories) factories.Add("filtered");
            var filter = self.Get(ReaderFilter, new());
            var args = self.Derive([filter.Member("board")], values => new JsonObject { ["board"] = ((JsonNode)values[0]!).DeepClone() });
            return new(new Dictionary<string, object?>
            {
                ["notes"] = self.LiveQuery(Q7Named(self.Get(ReaderNoteStore, new()), "notes", "ofBoard"), args, 5),
            });
        });
    }

    private static readonly ColumnSet ReaderTagSet = Tables.DefineColumns("tags.columns", new ColumnSetOptions
    { Columns = new() { ["note"] = Int(), ["tag"] = Str() }, Key = ["note", "tag"] });

    private static readonly TableDefinition ReaderTags = Tables.DefineTable("tags", new TableOptions
    { Version = 1, Columns = new(ReaderTagSet.Columns), Key = ["note", "tag"], Exposes = ReaderTagSet });

    private static readonly Contract ReaderTagStore = Contract.Local("app.tags", new()
    { Version = 1, Params = new(), Members = new() { ["tag"] = Method(Obj(("note", Int()), ("tag", Str()))) } });

    private static readonly Contract ReaderTagged = Contract.Local("app.tagged", new()
    { Version = 1, Params = new(), Members = new() { ["notes"] = Obj(("rows", Arr(ReaderNote)), ("more", Bool())) } });

    private static readonly Contract ReaderTaggedNotes = Contract.Local("app.tagged-notes", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["notes"] = Queries(ReaderNote, ("tagged", Obj(("tag", Str())))),
            ["put"] = Method(Obj(("id", Int()), ("board", Str()), ("text", Str()))),
        },
    });

    private static readonly TableDefinition ReaderMine = Tables.DefineTable("mine", new TableOptions
    { Version = 1, Columns = new() { ["id"] = Int(), ["board"] = Str(), ["text"] = Str() }, Key = ["id"] });

    private static void ReaderServeTags(IFacetHost h) => h.Serve(ReaderTagStore, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        var table = self.Table(ReaderTags);
        return new(new Dictionary<string, object?> { ["tag"] = Q7Tx(self, (tx, row) => tx.Put(table, row)) });
    });

    /// <summary>The named query of the tagged notes, as a reader of another object reads it.</summary>
    private static INamedQuery ReaderTaggedQuery(ISelf self) => Q7Named(self.Get(ReaderTaggedNotes, new()), "notes", "tagged");

    /// <summary>A store whose query tests <c>exists</c> in the tags' column set (another plugin's), and a view of it (upstream
    /// <c>serveTagged</c>).</summary>
    private static void ReaderServeTagged(IFacetHost h)
    {
        h.Serve(ReaderTaggedNotes, (_, _, owner) =>
        {
            var self = (IStoreSelf)owner;
            var table = self.Table(ReaderMine);
            return new(new Dictionary<string, object?>
            {
                ["notes"] = self.Queries(table, new QueriesOptions(Map: (row, _) => new JsonObject { ["id"] = row["id"]!.DeepClone(), ["text"] = row["text"]!.DeepClone() }),
                    Q7Builders(("tagged", Q7Builder(args =>
                    {
                        var tag = (string)args!["tag"]!;
                        return Tables.Query(ReaderMine).Where(n => Exists(ReaderTagSet, Match(("note", n["id"])), t => Eq(t["tag"], tag)));
                    })))),
                ["put"] = Q7Tx(self, (tx, row) => tx.Put(table, row)),
            });
        });
        h.Serve(ReaderTagged, (_, _, self) => new(new Dictionary<string, object?>
        {
            ["notes"] = self.LiveQuery(ReaderTaggedQuery(self), new JsonObject { ["tag"] = "red" }, 5),
        }));
    }

    private static IEnumerable<(string, Func<Task>)> HostSync160QueryReaderCases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("host.query-reader.open-reads-another-objects-named-query-before-the-reader-is-live-and-counts-it", async () =>
            {
                await using var first = await ReaderHost();
                foreach (var id in new[] { 1, 2, 3 }) await first.Put(id, "a", $"t{id}");
                await first.Put(9, "b", "other");
                var board = await first.Board("a");
                Json("""{"rows":[{"id":3,"text":"t3"},{"id":2,"text":"t2"}],"more":true}""", board.Member("notes").Value, "the first two rows, and more follow");
                Json("3", board.Member("count").Value, "the count");
            }),
            Case("host.query-reader.open-publishes-a-commits-change-in-its-batch-as-a-diff-a-commit-that-cannot-change-it-runs-nothing", async () =>
            {
                await using var hosted = await ReaderHost();
                await hosted.Put(1, "a", "t1");
                var board = await hosted.Board("a");
                var watch = await board.Member("notes").WatchAsync(context);
                var calls = ChordSupport.ChangesOf(watch);
                var before = hosted.Runs;
                // Another board's note: the list's probe misses; the count keeps no keys and runs.
                await hosted.Put(7, "b", "x", hosted.Counting);
                await Q7Settled(hosted.Host);
                Equal(0, calls.Count, "no frame for another board's note");
                Equal(1d, hosted.Runs - before, "the count alone ran");
                before = hosted.Runs;
                // A column neither read depends on: nothing runs.
                await hosted.Notes.CallAsync("star", JsonValue.Create(1), hosted.Counting);
                Equal(0d, hosted.Runs - before, "nothing runs for a column neither read depends on");
                await hosted.Put(2, "a", "t2", hosted.Counting);
                // Published in the commit's batch: the value is current once the call returned.
                Json("""{"rows":[{"id":2,"text":"t2"},{"id":1,"text":"t1"}],"more":false}""", board.Member("notes").Value, "the rows after the commit");
                Json("2", board.Member("count").Value, "the count after the commit");
                await Q7Settled(hosted.Host);
                Equal(1, calls.Count, "one frame");
                await watch.StopAsync();
            }),
            Case("host.query-reader.the-commit-step-chooses-its-reads-a-read-opened-while-a-commit-waits-in-the-queue-is-refreshed-by-it", async () =>
            {
                await using var hosted = await ReaderHost();
                // One commit held in its step, the note's commit queued behind it: staged before the reader exists.
                var held = HoldNextCommit(hosted.Store);
                var blocking = hosted.Put(50, "z", "blocker");
                await held.Entered;
                var queued = hosted.Put(1, "a", "late");
                // The reader registers its reads now; their first runs wait behind the held transaction.
                var getting = hosted.Board("a");
                await Q7Settled(hosted.Host);
                held.Release();
                try { await blocking; } catch { }
                await queued;
                var board = await getting;
                Json("""[{"id":1,"text":"late"}]""", board.Member("notes").Value!["rows"], "the queued commit's row");
                Json("1", board.Member("count").Value, "counted");
            }),
            Case("host.query-reader.projection-hands-the-mapping-only-the-declared-columns-an-unlisted-columns-update-changes-no-item", async () =>
            {
                var mapped = new List<string>();
                await using var hosted = await ReaderHost(mapped);
                await hosted.Put(1, "a", "t1");
                var board = await hosted.Board("a");
                var item = Q7Rows(board, "notes")[0]!.DeepClone();
                Equal("id,text", mapped[^1], "the mapping got the declared columns only");
                var runs = mapped.Count;
                await hosted.Notes.CallAsync("star", JsonValue.Create(1), context);
                Equal(runs, mapped.Count, "an unlisted column's update maps no row");
                Check(JsonNode.DeepEquals(item, Q7Rows(board, "notes")[0]), "the item is as it was");
                await hosted.Notes.CallAsync("rename", new JsonObject { ["id"] = 1, ["text"] = "renamed" }, context);
                Json("""[{"id":1,"text":"renamed"}]""", Q7Rows(board, "notes"), "a listed column's update changes the item");
            }),
            Case("host.query-reader.arguments-that-follow-a-value-shows-the-old-rows-until-the-read-for-the-new-arguments-ran-then-switches-in-one-frame", async () =>
            {
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                await host.LoadAsync([Q7Plugin("@test/store", h => ReaderServeStore(h)), Q7Plugin("@test/views", h => ReaderServeFiltered(h))], context);
                var notes = await host.GetAsync(ReaderNoteStore, new(), context);
                await Q7Call(notes, "put", """{"id":1,"board":"a","text":"a1"}""");
                await Q7Call(notes, "put", """{"id":2,"board":"b","text":"b2"}""");
                var view = await host.GetAsync(ReaderFiltered, new(), context);
                var filter = await host.GetAsync(ReaderFilter, new(), context);
                Json("""[{"id":1,"text":"a1"}]""", Q7Rows(view, "notes"), "the rows of board a");
                var watch = await view.Member("notes").WatchAsync(context);
                var calls = ChordSupport.ChangesOf(watch);
                await filter.CallAsync("show", JsonValue.Create("b"), context);
                await Q7Until(() => Q7Ids(view, "notes") == "2", "the rows of board b");
                await Q7Settled(host);
                // One frame, from the rows of "a" straight to the rows of "b": never an empty list in between.
                Json("""[[{"id":2,"text":"b2"}]]""", new JsonArray([.. calls.Select(call => ((WatchValueCall<JsonNode?>)call).Value!["rows"]!.DeepClone())]), "one frame");
                // The read of board "a" is gone: a note there changes nothing here.
                await Q7Call(notes, "put", """{"id":3,"board":"a","text":"a3"}""");
                await Q7Call(notes, "put", """{"id":4,"board":"b","text":"b4"}""");
                Equal("4 2", Q7Ids(view, "notes"), "only board b's notes");
                await watch.StopAsync();
            }),
            Case("host.query-reader.arguments-the-builder-refuses-fail-the-member-a-write-to-the-previous-arguments-rows-neither-heals-it-nor-publishes-them", async () =>
            {
                // Review round 1 (Sol 4).
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                await host.LoadAsync([Q7Plugin("@test/store", h => ReaderServeStore(h)), Q7Plugin("@test/views", h => ReaderServeFiltered(h))], context);
                var notes = await host.GetAsync(ReaderNoteStore, new(), context);
                await Q7Call(notes, "put", """{"id":1,"board":"a","text":"first"}""");
                var view = await host.GetAsync(ReaderFiltered, new(), context);
                var filter = await host.GetAsync(ReaderFilter, new(), context);
                var watch = await view.Member("notes").WatchAsync(context);
                var calls = ChordSupport.ChangesOf(watch);
                await filter.CallAsync("show", JsonValue.Create("broken"), context);
                await Q7Settled(host);
                StoreThrows(() => _ = view.Member("notes").Value, "no such board", "the member is failed");
                await Q7Call(notes, "put", """{"id":1,"board":"a","text":"edited"}""");
                await Q7Settled(host);
                StoreThrows(() => _ = view.Member("notes").Value, "no such board", "a write to the previous arguments' rows does not heal it");
                Sequence(["failed: no such board"], Q7Kinds(calls), "one failure call");
                // Arguments that build again read again: the member heals with their rows.
                await filter.CallAsync("show", JsonValue.Create("a"), context);
                await Q7Until(() => Q7Rows(view, "notes").Count == 1, "the member heals");
                Json("""[{"id":1,"text":"edited"}]""", Q7Rows(view, "notes"), "the rows of the arguments that build again");
                await watch.StopAsync();
            }),
            Case("host.query-reader.failures-arguments-the-contracts-schema-refuses-break-the-reader", async () =>
            {
                await using var store = await MemoryStore();
                var errors = new List<HostError>();
                await using var host = Q7Host(store, errors);
                var broken = Contract.Local("app.broken", new()
                { Version = 1, Params = new(), Members = new() { ["notes"] = Obj(("rows", Arr(ReaderNote)), ("more", Bool())) } });
                await host.LoadAsync(
                [
                    Q7Plugin("@test/store", h => ReaderServeStore(h)),
                    // A wrong argument reaches the schema.
                    Q7Plugin("@test/views", h => h.Serve(broken, (_, _, self) => new(new Dictionary<string, object?>
                    {
                        ["notes"] = self.LiveQuery(Q7Named(self.Get(ReaderNoteStore, new()), "notes", "one"), new JsonObject { ["id"] = "x" }, 1),
                    }))),
                ], context);
                Rejected(await ChordSupport.Rejection(host.GetAsync(broken, new(), context)), "invalid", "args", "the reader's arguments are refused");
                Sequence(["activation_failed/Broken"], errors.Select(error => $"{error.Kind}/{error.Failure?.Kind}"), "a broken activation");
            }),
            Case("host.query-reader.failures-the-wrong-form-of-each-call-throws-naming-the-other-a-private-query-needs-its-schema", async () =>
            {
                await using var store = await MemoryStore();
                var thrown = new List<string>();
                await using var host = Q7Host(store);
                var own = Contract.Local("app.own", new() { Version = 1, Params = new(), Members = new() { ["n"] = Int() } });
                await host.LoadAsync([Q7Plugin("@test/own", h => h.Serve(own, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    var table = self.Table(ReaderNotes);
                    var named = self.Queries(table, new QueriesOptions(Map: (row, _) => row["id"]!.DeepClone()),
                        Q7Builders(("all", new QueryBuilder(_ => Tables.Query(ReaderNotes), Obj()))));
                    // What the types still allow: a named query and a table that are none of Chord's, no page size, a private query
                    // without its schema.
                    Q7Attempting(thrown, () => self.Queries(new Q7ForeignTable(), new QueriesOptions(), Q7Builders()));
                    Q7Attempting(thrown, () => self.LiveQuery(new Q7ForeignQuery(), new JsonObject(), 1));
                    Q7Attempting(thrown, () => self.LiveQuery(named["all"], new JsonObject(), 0));
                    return new(new Dictionary<string, object?> { ["n"] = self.Count(named["all"], new JsonObject()) });
                }))], context);
                Sequence(
                [
                    "app.own: self.queries: the table is not one this factory declared",
                    "app.own: self.liveQuery reads a named query; a table of this object is read with self.query",
                    "app.own: self.liveQuery: { first: n } is required, a positive integer",
                ], thrown, "each refusal");
                // The owner's own private query, counted: read in its own activation, no dependency on itself.
                var reference = await host.GetAsync(own, new(), context);
                Json("0", reference.Member("n").Value, "the owner's own private query, counted");
                // A private query that brings no schema is refused by the member check and at its first read.
                var bare = Contract.Local("app.bare", new() { Version = 1, Params = new(), Members = new() { ["n"] = Int() } });
                await using var bareStore = await MemoryStore();
                await using var second = Q7Host(bareStore);
                await second.LoadAsync([Q7Plugin("@test/bare", h => h.Serve(bare, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    var named = self.Queries(self.Table(ReaderMine), new QueriesOptions(), Q7Builders(("all", Q7Builder(_ => Tables.Query(ReaderMine)))));
                    return new(new Dictionary<string, object?> { ["n"] = self.Count(named["all"], new JsonObject()) });
                }))], context);
                Rejected(await ChordSupport.Rejection(second.GetAsync(bare, new(), context)), null, null, "a private query without a schema",
                    "a private query brings its argument schema");
            }),
            Case("host.query-reader.a-column-set-nobody-serves-parks-the-reader-failed-waiting-and-heals-when-the-set-is-served-the-other-owners-commit-then-reaches-it", async () =>
            {
                await using var store = await MemoryStore();
                var errors = new List<HostError>();
                var host = Q7Host(store, errors);
                var tagged = Q7Plugin("@test/tagged", ReaderServeTagged);
                await host.LoadAsync([tagged], context);
                var notes = await host.GetAsync(ReaderTaggedNotes, new(), context);
                await Q7Call(notes, "put", """{"id":1,"board":"a","text":"t1"}""");
                var view = host.Hold(ReaderTagged, new(), context);
                await Q7Until(() => view.State.Value.Kind == "failed", "the reader parks");
                var parked = view.State.Value;
                Equal(FailureKind.Waiting, parked.Cause!.Kind, "parked waiting");
                Equal("unavailable", parked.Cause.Error.Code, "the cause's code");
                Sync160Contains("\"tags.columns\" is not served", parked.Cause.Error.Message, "the cause names the set");
                await host.ReloadAsync([tagged, Q7Plugin("@test/tags", ReaderServeTags)], context);
                // The tags' store goes live when someone holds it: its table then exposes the set, and the reader runs.
                var tags = await host.GetAsync(ReaderTagStore, new(), context);
                await Q7Until(() => view.State.Value.Kind == "live", "the reader heals");
                Json("[]", Q7Rows(view, "notes"), "no note is tagged yet");
                // The tags' owner commits: the read over its table runs in that owner's step.
                await Q7Call(tags, "tag", """{"note":1,"tag":"red"}""");
                Json("""[{"id":1,"text":"t1"}]""", Q7Rows(view, "notes"), "the tagged note");
                await Q7Call(tags, "tag", """{"note":1,"tag":"blue"}""");
                Json("""[{"id":1,"text":"t1"}]""", Q7Rows(view, "notes"), "another tag changes nothing");
                // A closing host reports no read failed: its objects keep their sets bound until every object is torn down (the readers'
                // reads are closed by then), and leave none in the store.
                lock (errors) errors.Clear();
                await host.CloseAsync(context);
                Equal(0, errors.Count, "a closing host reports nothing: " + string.Join("; ", errors.Select(error => $"{error.Kind} {error.Error?.Message}")));
                Check(store.ReadIndex.Get(ReaderTagSet.Name) is null, "the set is bound no more");
                view.Release();
            }),
            Case("host.query-reader.readers-before-natives-an-attempt-that-waits-on-its-reader-holds-no-native-it-opens-when-the-read-ran", async () =>
            {
                // Opus S3, Fable S9: a waiting attempt keeps nothing open that a native holds (a socket, a process).
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                var natives = new List<string>();
                var watching = Contract.Local("app.watching", new()
                { Version = 1, Params = new(), Members = new() { ["notes"] = Obj(("rows", Arr(ReaderNote)), ("more", Bool())) } });
                var tagged = Q7Plugin("@test/tagged", ReaderServeTagged);
                var watcher = Q7Plugin("@test/watching", h => h.Serve(watching, (_, _, self) =>
                {
                    self.Open(_ => { lock (natives) natives.Add("open"); return Task.FromResult(true); },
                        (_, _, _) => { lock (natives) natives.Add("close"); return Task.CompletedTask; });
                    return new(new Dictionary<string, object?> { ["notes"] = self.LiveQuery(ReaderTaggedQuery(self), new JsonObject { ["tag"] = "red" }, 5) });
                }));
                await host.LoadAsync([tagged, watcher], context);
                var view = host.Hold(watching, new(), context);
                await Q7Until(() => view.State.Value.Kind == "failed", "the reader parks");
                Equal(FailureKind.Waiting, view.State.Value.Cause!.Kind, "parked waiting");
                await Q7Settled(host);
                Equal(0, natives.Count, "a parked attempt opened no native");
                await host.ReloadAsync([tagged, watcher, Q7Plugin("@test/tags", ReaderServeTags)], context);
                await host.GetAsync(ReaderTagStore, new(), context);
                await Q7Until(() => view.State.Value.Kind == "live", "the reader goes live");
                Sequence(["open"], natives, "the native opened once the read ran");
                view.Release();
            }),
            Case("host.query-reader.arguments-that-have-no-value-when-the-reader-opens-break-the-attempt-naming-the-rule", async () =>
            {
                // Opus S1: the argument's derive threw not_ready, the reader waited, and nothing could wake the attempt. A followed source
                // attaches after the readers, so waiting would never end.
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                var following = Contract.Local("app.following", new()
                { Version = 1, Params = new(), Members = new() { ["notes"] = Obj(("rows", Arr(ReaderNote)), ("more", Bool())) } });
                await host.LoadAsync([Q7Plugin("@test/following", h =>
                {
                    ReaderServeStore(h);
                    h.Serve(following, (_, _, self) =>
                    {
                        var board = self.Follow((_, _) => Task.FromResult<FollowAttachment?>(new(new JsonObject { ["board"] = "a" }, () => { })));
                        var args = self.Derive([board], values => new JsonObject { ["board"] = ((JsonNode)values[0]!)["board"]!.DeepClone() });
                        return new(new Dictionary<string, object?>
                        {
                            ["notes"] = self.LiveQuery(Q7Named(self.Get(ReaderNoteStore, new()), "notes", "ofBoard"), args, 5),
                        });
                    });
                })], context);
                var view = host.Hold(following, new(), context);
                await Q7Until(() => view.State.Value.Kind == "failed", "the attempt fails");
                var cause = view.State.Value.Cause!;
                Equal(FailureKind.Broken, cause.Kind, "broken");
                Equal("invalid", cause.Error.Code, "the reader's own code");
                Sync160Contains("a reader's arguments read references, registries, writables and earlier reads, not a followed source or a native", cause.Error.Message, "the rule");
                view.Release();
            }),
            Case("host.query-reader.a-providers-restart-does-not-restart-its-readers-they-read-the-successors-query-and-give-one-frame-none-when-equal", async () =>
            {
                await using var store = await MemoryStore();
                var factories = new List<string>();
                await using var host = Q7Host(store);
                var views = Q7Plugin("@test/views", h => ReaderServeFiltered(h, factories));
                await host.LoadAsync([Q7Plugin("@test/store", h => ReaderServeStore(h)), views], context);
                var notes = await host.GetAsync(ReaderNoteStore, new(), context);
                foreach (var id in new[] { 1, 2 }) await Q7Call(notes, "put", $$"""{"id":{{id}},"board":"a","text":"t{{id}}"}""");
                var view = await host.GetAsync(ReaderFiltered, new(), context);
                var watch = await view.Member("notes").WatchAsync(context);
                var calls = ChordSupport.ChangesOf(watch);
                Sequence(["filtered"], factories, "one factory run");
                var before = Q7ReaderSources(host, ReaderFiltered);
                // The same code again: the readers bind to the successor, and equal rows send nothing.
                await host.ReloadAsync([Q7Plugin("@test/store", h => ReaderServeStore(h)), views], context);
                await Q7Until(() => Q7Reads(store, "app.notes:notes") == 1, "the reader reads the successor's query");
                await Q7Settled(host);
                Sequence(["filtered"], factories, "the reader's factory did not run again");
                Equal(0, calls.Count, "equal rows send nothing");
                // The successor's commit reaches the reader, once.
                await Q7Call(notes, "put", """{"id":3,"board":"a","text":"t3"}""");
                await Q7Settled(host);
                Equal("3 2 1", Q7Ids(view, "notes"), "the successor's commit reaches the reader");
                Equal(1, calls.Count, "once");
                Check(before.All(source => source.IsAlive), "the reader's source is the one it had");
                await watch.StopAsync();
            }),
            Case("host.query-reader.count-is-exact-and-uncapped-skipped-only-by-an-update-of-no-condition-column", async () =>
            {
                await using var hosted = await ReaderHost();
                for (var id = 1; id <= 300; id++) await hosted.Put(id, "a", "t");
                var board = await hosted.Board("a");
                Json("300", board.Member("count").Value, "exact and uncapped");
                var before = hosted.Runs;
                // A rename: the list's mapped column (one run), no condition column of the count.
                await hosted.Notes.CallAsync("rename", new JsonObject { ["id"] = 300, ["text"] = "renamed" }, hosted.Counting);
                Equal(1d, hosted.Runs - before, "the list ran, the count was skipped");
                await hosted.Notes.CallAsync("drop", JsonValue.Create(300), hosted.Counting);
                Json("299", board.Member("count").Value, "the count after a delete");
            }),
            Case("host.query-reader.memory-collects-a-readers-live-query-once-its-object-ended", async () =>
            {
                ReaderHosted? hosted = await ReaderHost();
                IReference? board = await hosted.Board("a");
                Json("0", board.Member("count").Value, "counted");
                var mine = Q7ReaderSources(hosted.Host, ReaderBoard);
                Equal(2, mine.Length, "a live query and a count");
                board.Release();
                board = null;
                await hosted.DisposeAsync();
                hosted = null;
                await ChordSupport.ExpectCollected(mine);
            }),
        ];
    }
}
