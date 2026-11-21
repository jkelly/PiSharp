using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Host;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/store/live-query.test.ts, and authored cases of
// packages/chord/src/store/live-query.ts.
//
// The upstream cases (store.live-query.*, the first group below) are tests of live queries through a host: self.query(table, q, limit)
// runs before its object goes live, again in the commit step of every commit of the object that can change it, and publishes the diff in
// that commit's batch; a commit writing another table runs none; a reload restarts the object; the limit is required; a failing run is a
// failed member; an ended attempt's query is collected; a refresh applied again adopts only its last runs. Differences from upstream:
// - "keeps an unchanged row the same object across runs": upstream compares the row objects of two values by identity. A JsonNode has one
//   parent, so two values here share no node (State/Framed.cs); what the case is about, that an unchanged row is not decoded or compared
//   again, is asserted on the frame (the changed row alone is diffed, as one op) and, in an authored case, on DecodedRows.
// - The declaration refusals "no limit" and "a fractional limit" cannot be written: the limit is a long. The limit 0 stands for both.
// - "a refresh applied again" stubs a Read upstream; here a real read over a counting executor is applied twice.
// - delivered() and flushed() are a short wait: a host delivers its frames on its dispatcher.
// - The three publication cases that compare a frame's ops watch the live query itself (the member's source in its attempt), not the
//   reference's member as upstream does: the native reference member relays values and diffs them again, and its first frame after it
//   was made is a replacement of the whole value (a 1.5.2 trait of Host/Reference.cs, outside this package). The values are read through
//   the reference, as upstream reads them.
//
// The authored cases (store.live-query.160-*) replace the seven 1.5.2 ones, which were written against the per-owner LiveQueries registry
// and QueryRefresh that 1.6.0 removed: they cover the same ground over the store's reads, a LiveQuery standing alone on a batch.
internal static partial class Program
{
    private static readonly TableDefinition LiveItems = Tables.DefineTable("items", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Int()), ("text", Str()), ("meta", Opt(Tables.Json(Obj(), new JsonColumnOptions { Version = 1 })))),
        Key = ["id"],
    });

    private static readonly TableDefinition LiveOther = Tables.DefineTable("other", new TableOptions { Version = 1, Columns = StoreColumns(("id", Int())), Key = ["id"] });

    /// <summary>The newest items first.</summary>
    private static readonly Query LiveNewest = Tables.Query(LiveItems).OrderBy(item => item["id"], OrderDirection.Desc);

    private static readonly Contract LiveBoard = Contract.Local("app.board", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["items"] = Arr(Obj(("id", Int()), ("text", Str()), ("meta", Opt(Obj())))),
            ["put"] = Method(Obj(("id", Int()), ("text", Str()), ("meta", Opt(Obj())))),
            ["rename"] = Method(Obj(("id", Int()), ("text", Str()))),
            ["drop"] = Method(Int()),
            ["other"] = Method(Int()),
        },
    });

    /// <summary>Serves the board with a live query of the newest <paramref name="limit"/> items; <paramref name="declare"/> runs first in
    /// its factory (upstream <c>serveBoard</c>).</summary>
    private static PluginFacet LiveServeBoard(long limit, Action<IStoreSelf>? declare = null) => new("@test/app", Facet.Define((facet, _) => facet.Serve(LiveBoard, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        declare?.Invoke(self);
        var items = self.Table(LiveItems);
        var other = self.Table(LiveOther);
        async Task<object?> Tx(Action<PiSharp.Chord.Documents.ITx> write, CallContext ctx)
        {
            await self.TxAsync(tx =>
            {
                write(tx);
                return Task.FromResult(true);
            }, ctx.Context);
            return null;
        }
        return new(new Dictionary<string, object?>
        {
            ["items"] = self.Query(items, LiveNewest, limit),
            ["put"] = (HostMethod)((row, ctx) => Tx(tx => tx.Put(items, row), ctx)),
            ["rename"] = (HostMethod)((args, ctx) => Tx(tx => tx.UpdateWhere(items, item => Eq(item["id"], ((JsonNode)args!)["id"]!.DeepClone()),
                new JsonObject { ["text"] = ((JsonNode)args!)["text"]!.DeepClone() }), ctx)),
            ["drop"] = (HostMethod)((id, ctx) => Tx(tx => tx.DeleteWhere(items, item => Eq(item["id"], ((JsonNode)id!).DeepClone())), ctx)),
            ["other"] = (HostMethod)((id, ctx) => Tx(tx => tx.Put(other, new JsonObject { ["id"] = ((JsonNode)id!).DeepClone() }), ctx)),
        });
    })));

    /// <summary>A host serving the board on a store, metered, and a reference to the board (upstream <c>board</c>).</summary>
    private sealed class LiveBoardHost(ChordHost host, IReference board, List<HostError> errors, ChordContext counting, Func<IReadOnlyDictionary<string, double>> counts) : IAsyncDisposable
    {
        internal ChordHost Host => host;

        internal IReference Board => board;

        internal List<HostError> Errors => errors;

        /// <summary>A context counting on the host's telemetry.</summary>
        internal ChordContext Counting => counting;

        internal double Count(string name) => counts().GetValueOrDefault(name);

        internal IWatchable<JsonNode?> Items => board.Member("items");

        /// <summary>The live query itself, the member's source in the board's current attempt. The publication cases watch it here: a
        /// reference's member (Host/Reference.cs, ReferenceMember) relays values, not ops, and the first frame it makes after it was made
        /// or retargeted replaces the value whole, whatever its source published.</summary>
        internal IWatchable<JsonNode?> Source =>
            host.Dispatcher.Invoke(() => host.Objects.Values.Single(owner => owner.Served.Contract.Name == LiveBoard.Name).Current!.Self.Queries.Single().Live);

        internal Task Put(int id, string text, bool meta = false, ChordContext? context = null)
        {
            var row = new JsonObject { ["id"] = id, ["text"] = text };
            if (meta) row["meta"] = new JsonObject();
            return board.CallAsync("put", row, context ?? ChordContext.Background);
        }

        internal Task Rename(int id, string text) => board.CallAsync("rename", new JsonObject { ["id"] = id, ["text"] = text }, ChordContext.Background);

        internal Task Drop(int id) => board.CallAsync("drop", JsonValue.Create(id), ChordContext.Background);

        internal Task Other(int id, ChordContext context) => board.CallAsync("other", JsonValue.Create(id), context);

        public async ValueTask DisposeAsync()
        {
            board.Dispose();
            await host.DisposeAsync();
        }
    }

    private static async Task<LiveBoardHost> LiveBoardOn(ChordStore store, long limit = 3)
    {
        var (telemetry, context, counts) = await ChordSupport.Metered();
        var errors = new List<HostError>();
        // The board lives as long as its host: it activates under the host's telemetry.
        var host = Hosts.Create(new() { Store = store, Telemetry = telemetry, OnError = error => { lock (errors) errors.Add(error); } });
        await host.LoadAsync([LiveServeBoard(limit)], context);
        return new LiveBoardHost(host, await host.GetAsync(LiveBoard, new(), context), errors, context, counts);
    }

    /// <summary>The ids of the rows of a live query's value.</summary>
    private static IEnumerable<int> LiveIds(JsonNode? rows) => ((JsonArray)rows!).Select(row => (int)row!["id"]!.GetValue<double>());

    /// <summary>The ops of every frame, as their wire tuples.</summary>
    private static string LiveOps<T>(IEnumerable<(T Value, System.Collections.Immutable.ImmutableArray<Op> Ops)> frames) =>
        new JsonArray([.. frames.Select(frame => (JsonNode?)ChordSupport.OpsJson(frame.Ops))]).ToJsonString();

    /// <summary>A weak reference to the live query the board's current attempt declared (upstream collects them with <c>instancesOf</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LiveBoardQuery(ChordHost host) =>
        new(host.Objects.Values.Single(owner => owner.Served.Contract.Name == LiveBoard.Name).Current!.Self.Queries.Single().Live);

    /// <summary>A table that is none of the factory's (upstream passes <c>{} as never</c>).</summary>
    private sealed class LiveForeignTable : ITable
    {
        public Task<Page> PageAsync(Query query, long limit, ChordContext context, PageOptions? options = null) => throw new NotSupportedException();

        public IAsyncEnumerable<IReadOnlyDictionary<string, JsonNode?>> Rows(Query query, ChordContext context) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(object? key, ChordContext context) => throw new NotSupportedException();

        public Task<long> CountAsync(Query query, ChordContext context) => throw new NotSupportedException();
    }

    private static IEnumerable<(string, Func<Task>)> StoreLiveQueryCases()
    {
        var context = ChordContext.Background;
        var refusals = new (string What, Action<IStoreSelf> Declare, string Message)[]
        {
            ("a-limit-of-0", self => self.Query(self.Table(LiveItems), LiveNewest, 0), "a limit is required"),
            ("a-query-of-another-table", self => self.Query(self.Table(LiveOther), LiveNewest, 3), "a query of table \"items\" over \"other\""),
            ("a-table-this-factory-did-not-declare", self => self.Query(new LiveForeignTable(), LiveNewest, 3), "the table is not one this factory declared"),
        };
        List<(string, Func<Task>)> cases =
        [
            Case("store.live-query.activation-runs-the-query-before-the-object-goes-live-over-the-rows-already-stored", async () =>
            {
                await using var store = await MemoryStore();
                await using (var first = await LiveBoardOn(store))
                {
                    foreach (var id in new[] { 1, 2 }) await first.Put(id, $"t{id}");
                }
                await using var again = await LiveBoardOn(store);
                Json("""[{"id":2,"text":"t2"},{"id":1,"text":"t1"}]""", again.Items.Value, "the rows already stored");
                Equal(1d, again.Count("chord.query.runs"), "one run, at activation");
            }),
            Case("store.live-query.activation-a-run-that-fails-at-activation-fails-the-activation-broken", async () =>
            {
                await using var store = await MemoryStore();
                await using (var first = await LiveBoardOn(store))
                {
                    await first.Put(1, "t1", meta: true);
                }
                await store.ReadAsync(async executor =>
                {
                    await executor.RunAsync("UPDATE \"app.board:items\" SET \"meta$v\" = 9");
                    return true;
                }, context);
                var errors = new List<HostError>();
                await using var host = Hosts.Create(new() { Store = store, OnError = error => { lock (errors) errors.Add(error); } });
                await host.LoadAsync([LiveServeBoard(3)], context);
                Rejected(await ChordSupport.Rejection(host.GetAsync(LiveBoard, new(), context)), null, null, "the activation fails", "stored at version 9");
                Sequence(["activation_failed/Broken"], errors.Select(error => $"{error.Kind}/{error.Failure?.Kind}"), "reported as a broken activation");
            }),
            Case("store.live-query.publication-an-insert-an-update-and-a-delete-reach-a-watch-as-ops-in-the-commits-batch", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store);
                var watch = await b.Source.WatchAsync(context);
                var frames = ChordSupport.FramesOf(watch);
                await b.Put(1, "a");
                // Published in the commit's batch: the value is current once the call returned.
                Sequence([1], LiveIds(b.Items.Value), "the value after the call");
                await b.Put(2, "b");
                await b.Rename(1, "A");
                await b.Drop(2);
                await ChordSupport.Flushed();
                // Splices of the rows at the root (["p", path, at, deleted, inserted]) and a set inside one row.
                Json("""[[["p",[],0,0,[{"id":1,"text":"a"}]]],[["p",[],0,0,[{"id":2,"text":"b"}]]],[["s",[1,"text"],"A"]],[["p",[],0,1,[]]]]""",
                    JsonNode.Parse(LiveOps(frames)), "one frame of ops per commit");
                Json("""[{"id":1,"text":"A"}]""", b.Items.Value, "the rows");
                await watch.StopAsync();
            }),
            Case("store.live-query.publication-keeps-the-window-at-its-limit-a-newer-row-pushes-the-oldest-out", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store, 2);
                foreach (var id in new[] { 1, 2 }) await b.Put(id, $"t{id}");
                var watch = await b.Source.WatchAsync(context);
                var frames = ChordSupport.FramesOf(watch);
                await b.Put(3, "t3");
                await ChordSupport.Flushed();
                Sequence([3, 2], LiveIds(b.Items.Value), "the window");
                Json("""[[["p",[],0,0,[{"id":3,"text":"t3"}]],["p",[],2,1,[]]]]""", JsonNode.Parse(LiveOps(frames)), "one insert and one removal");
                await watch.StopAsync();
            }),
            Case("store.live-query.publication-keeps-an-unchanged-row-as-it-is-across-runs-a-changed-one-new", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store);
                foreach (var id in new[] { 1, 2 }) await b.Put(id, $"t{id}", meta: true);
                var before = b.Items.Value!;
                var watch = await b.Source.WatchAsync(context);
                var frames = ChordSupport.FramesOf(watch);
                await b.Rename(2, "T2");
                await ChordSupport.Flushed();
                var after = b.Items.Value!;
                Json("""{"id":2,"text":"T2","meta":{}}""", after[0], "the changed row");
                // The unchanged row is not compared again: the frame is the changed row's one op.
                Json("""[[["s",[0,"text"],"T2"]]]""", JsonNode.Parse(LiveOps(frames)), "the changed row alone is diffed");
                Json("""[{"id":2,"text":"t2","meta":{}},{"id":1,"text":"t1","meta":{}}]""", before, "the value before is as it was");
                await watch.StopAsync();
            }),
            Case("store.live-query.publication-a-commit-writing-another-table-runs-no-query-and-publishes-nothing-cost-guard", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store);
                var watch = await b.Items.WatchAsync(context);
                var calls = ChordSupport.ChangesOf(watch);
                double Statements() => b.Count("chord.store.statements");
                double Runs() => b.Count("chord.query.runs");
                var before = (Statements(), Runs());
                await b.Other(1, b.Counting);
                var other = (Statements: Statements() - before.Item1, Runs: Runs() - before.Item2);
                before = (Statements(), Runs());
                await b.Put(1, "a", context: b.Counting);
                var touching = (Statements: Statements() - before.Item1, Runs: Runs() - before.Item2);
                await ChordSupport.Flushed();
                // The same one-row put: the touching commit costs one query and two statements more (slice 14 §3.3: a put of a row not in
                // the window asks the keyed probe first, and it finds the row).
                Equal(0d, other.Runs, "a commit writing another table runs no query");
                Equal((other.Statements + 2, 1d), touching, "the touching commit: the probe and the run");
                Equal(1, calls.Count, "one frame");
                await watch.StopAsync();
            }),
            Case("store.live-query.reload-restarts-the-object-the-new-attempts-query-runs-at-its-activation-and-takes-over", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store);
                foreach (var id in new[] { 1, 2, 3 }) await b.Put(id, $"t{id}");
                var report = await b.Host.ReloadAsync([LiveServeBoard(2)], context);
                Sequence(["app.board:restarted"], report.Objects.Select(each => $"{each.Object}:{each.Fate}"), "the board restarts");
                await ChordSupport.Flushed();
                Sequence([3, 2], LiveIds(b.Items.Value), "the new attempt's window");
                var runs = b.Count("chord.query.runs");
                await b.Put(4, "t4", context: b.Counting);
                Sequence([4, 3], LiveIds(b.Items.Value), "the new attempt's query follows");
                // Only the new attempt's query runs: the old one left with its attempt.
                Equal(1d, b.Count("chord.query.runs") - runs, "one run");
            }),
            Case("store.live-query.declaration-staging-refuses-a-query-without-a-valid-limit-the-plugin-keeps-its-old-code", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store);
                var report = await b.Host.ReloadAsync([LiveServeBoard(0)], context);
                Equal(1, report.Failed.Count, "the plugin failed staging");
                Sync160Contains("a limit is required", report.Failed[0].Error.Message, "why");
                Equal("live", b.Board.State.Value.Kind, "the board keeps its old code");
            }),
            Case("store.live-query.failure-a-failing-run-is-a-failed-member-reported-once-the-next-good-run-heals-it", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store);
                await b.Put(1, "a", meta: true);
                var watch = await b.Items.WatchAsync(context);
                var calls = ChordSupport.ChangesOf(watch);
                await store.ReadAsync(async executor =>
                {
                    await executor.RunAsync("UPDATE \"app.board:items\" SET \"meta$v\" = 9");
                    return true;
                }, context);
                await b.Put(2, "b");
                await b.Put(3, "c");
                StoreThrows(() => _ = b.Items.Value, "stored at version 9", "the member is failed");
                await b.Drop(1);
                await ChordSupport.Flushed();
                Sequence(["failed", "value"], calls.Select(call => call is WatchFailedCall<JsonNode?> ? "failed" : "value"), "one failure, then the healed value");
                Json("""[["r",[{"id":3,"text":"c"},{"id":2,"text":"b"}]]]""", ChordSupport.OpsJson(((WatchValueCall<JsonNode?>)calls[1]).Ops), "the value replaces the failure whole");
                Sequence(["query_failed/items"], b.Errors.Select(error => $"{error.Kind}/{error.Query}"), "reported once, naming the query");
                // The object stays live: only the member failed.
                Equal("live", b.Board.State.Value.Kind, "the object stays live");
                await watch.StopAsync();
            }),
            Case("store.live-query.memory-collects-a-restarted-attempts-query-no-commit-re-runs-it", async () =>
            {
                await using var store = await MemoryStore();
                await using var b = await LiveBoardOn(store);
                var old = LiveBoardQuery(b.Host);
                await b.Host.ReloadAsync([LiveServeBoard(2)], context);
                await ChordSupport.Flushed();
                await ChordSupport.ExpectCollected(old);
            }),
            Case("store.live-query.a-refresh-applied-again-adopts-its-last-applications-runs-only", async () =>
            {
                // Upstream: a grouped commit that rolled back applies again alone. Here: any step applied a second time.
                var adopted = new List<ReadRun>();
                var counting = new LiveQueryExecutor();
                var plan = new ReadPlan(Tables.Query(LiveOther), "t", Projection.All, ReadShape.Count, new Sync160Bindings());
                // A read without a result is never skipped.
                var read = new Read(plan, new ReadKind.Count(), new ReadSink(adopted.Add));
                var reads = new Reads();
                reads.Add(read);
                var refresh = new ReadRefresh(reads, ["t"], () => [new TableChange("t", TableChangeKind.Bulk, null, null)], context);
                var step = new Step(counting, 1, new IdAllocator(2), new Dictionary<string, TableRecord>());
                await refresh.Write.ApplyAsync(step);
                await refresh.Write.ApplyAsync(step);
                refresh.Adopt();
                Sequence(["2"], adopted.Select(run => run is ReadRun.Count count ? count.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?"), "the second application's run");
            }),
        ];
        foreach (var (what, declare, message) in refusals)
        {
            cases.Add(Case($"store.live-query.declaration-refuses-{what}", async () =>
            {
                await using var store = await MemoryStore();
                await using var host = Hosts.Create(new() { Store = store, OnError = _ => { } });
                await host.LoadAsync([new("@test/app", Facet.Define((facet, _) => facet.Serve(LiveBoard, (_, _, owner) =>
                {
                    declare((IStoreSelf)owner);
                    throw new InvalidOperationException("unreachable");
                })))], context);
                Rejected(await ChordSupport.Rejection(host.GetAsync(LiveBoard, new(), context)), null, null, what, message);
            }));
        }
        return cases;
    }

    private static readonly TableDefinition LiveQueryTable = Tables.DefineTable("live-query", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Int()), ("value", Tables.Json(Obj(("n", Int())), new JsonColumnOptions { Version = 1 }))),
        Key = ["id"],
    });

    private static JsonObject LiveQueryRow(int id, int? n = null) => new() { ["id"] = id, ["value"] = new JsonObject { ["n"] = n ?? id } };

    private static async Task<TableHandle> LiveQueryHandle(ChordStore store, LiveQueryTestOwner owner)
    {
        const string sqlName = "app.store:live-query";
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(LiveQueryTable, sqlName)], store, CommitFence.Open, owner.Label, ChordContext.Background);
        return new TableHandle(LiveQueryTable, sqlName, store, owner);
    }

    /// <summary>What self.Query makes of a table handle: the owner's window of <paramref name="limit"/> rows in key order, and what it reads.</summary>
    private static (LiveQuery Live, LiveTarget Target) LiveQueryOver(TableHandle handle, IChordStore store, LiveQueryTestOwner owner, StateHost state, long limit = 100) =>
        (new LiveQuery(new ReadSite(store, state.Batch), owner, new LiveShape.Rows(new ReadRange(limit))), LiveQueryTarget(handle, Tables.Query(handle.Definition)));

    /// <summary>What a live source reads of <paramref name="handle"/>'s table: <paramref name="query"/>, each row as its columns' object.</summary>
    private static LiveTarget LiveQueryTarget(TableHandle handle, Query query)
    {
        var rows = handle.Definition.Rows;
        return new LiveTarget(new QuerySpec(query, handle.Sql.Table, Projection.All, handle), (stored, context) =>
        {
            var row = new JsonObject();
            foreach (var (column, cell) in rows.Decode(stored, context)) row[column] = cell;
            return row;
        });
    }

    private static async Task LiveQueryCommit(ChordStore store, TableHandle handle, StateHost state, Action<TableChanges>? fill = null, ChordContext? context = null)
    {
        await Sync160Commit(store, state.Batch, context ?? ChordContext.Background, changes => fill?.Invoke(changes(handle)));
        state.Delivered();
    }

    private static IEnumerable<(string, Func<Task>)> StoreLiveQueryAuthoredCases() =>
    [
        Case("store.live-query.160-declaration-requires-the-same-table-and-a-bounded-positive-limit", () =>
        {
            var other = Tables.DefineTable("other-live-query", new TableOptions { Version = 1, Columns = StoreColumns(("id", Int())), Key = ["id"] });
            StoreArgs(() => StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(other), 1, "app.store"), "a query of table");
            StoreArgs(() => StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(LiveQueryTable), 0, "app.store"), "a positive integer");
            StoreArgs(() => StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(LiveQueryTable), long.MaxValue, "app.store"), "a positive integer");
            StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(LiveQueryTable), 1, "app.store");
            return Task.CompletedTask;
        }),
        Case("store.live-query.160-values-are-bounded-commit-coherent-and-earlier-values-stay-as-they-were", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => { for (var id = 1; id <= 4; id++) changes.Put(LiveQueryRow(id)); });
            var state = new StateHost();
            var (query, target) = LiveQueryOver(handle, store, owner, state, 3);
            await query.LoadAsync(target, ChordContext.Background);
            var initial = query.Value!;
            Json("""[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}}]""", initial, "initial bounded rows");
            var frames = ChordSupport.ChangesOf(await query.WatchAsync(ChordContext.Background));
            await LiveQueryCommit(store, handle, state, changes => changes.Delete(new JsonObject { ["id"] = 1 }));
            Equal(1, frames.Count, "one frame for the commit");
            var frame = (WatchValueCall<JsonNode?>)frames[0];
            Json("""[{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}},{"id":4,"value":{"n":4}}]""", frame.Value, "new window");
            Check(frame.Ops.Length == 2 && frame.Ops.All(op => op is SpliceOp), "window shift is one removal and one insertion");
            Json("""[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}}]""", initial, "the value before is still intact");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(2)));
            Equal(1, frames.Count, "an unchanged stored row publishes no frame");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(2, 20)));
            var changed = (WatchValueCall<JsonNode?>)frames[^1];
            Json("""[["s",[0,"value","n"],20]]""", ChordSupport.OpsJson(changed.Ops), "a changed row keeps a narrow delta");
            Equal(0, owner.Failures.Count, "query decoding does not fail the table owner");
            query.Detach(new InvalidOperationException("done"));
        }),
        Case("store.live-query.160-decoded-rows-reuse-an-unchanged-row-and-a-refresh-runs-a-read-once", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => { changes.Put(LiveQueryRow(1)); changes.Put(LiveQueryRow(2)); });
            var state = new StateHost();
            var (query, target) = LiveQueryOver(handle, store, owner, state);
            var (_, context, counts) = await ChordSupport.Metered();
            await query.LoadAsync(target, context);
            // The rows a run decodes: an unchanged stored row is the row decoded before (upstream: the same object), a changed one is new.
            var plan = new ReadPlan(target.Spec.Query, handle.Sql.Table, Projection.All, ReadShape.Rows, store.ReadIndex);
            var statement = plan.Select(new ReadRange(10));
            Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Stored() => store.ReadAsync(executor => executor.AllAsync(statement.Sql, [.. statement.Params]), context);
            var decoded = new DecodedRows();
            var first = decoded.Decode(await Stored(), target, context);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(2, 20)));
            var second = decoded.Decode(await Stored(), target, context);
            Check(ReferenceEquals(first[0], second[0]) && first[0].Token == second[0].Token, "the unchanged row is the row decoded before");
            Check(!ReferenceEquals(first[1], second[1]) && first[1].Token != second[1].Token, "the changed row is decoded anew");
            Json("""{"id":2,"value":{"n":20}}""", second[1].Row, "the changed row");
            var runs = counts().GetValueOrDefault("chord.query.runs");
            // A write of a table nothing reads refreshes nothing; a table listed twice runs its read once.
            var unread = new ReadRefresh(store.ReadIndex, ["app.store:another"], () => [], context);
            var twice = new ReadRefresh(store.ReadIndex, [handle.Sql.Table, handle.Sql.Table], () => [new TableChange(handle.Sql.Table, TableChangeKind.Bulk, null, null)], context);
            await store.CommitAsync([unread.Write, twice.Write], new CommitFence(() => true, (_, _) => state.Batch.Commit(() => { unread.Adopt(); twice.Adopt(); }, context)), context);
            Equal(1d, counts().GetValueOrDefault("chord.query.runs") - runs, "one run");
            query.Detach(new InvalidOperationException("done"));
        }),
        Case("store.live-query.160-detach-ends-its-watches-leaves-the-index-and-a-pending-run-cannot-publish", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            var state = new StateHost();
            var (plain, target) = LiveQueryOver(handle, store, owner, state);
            await plain.LoadAsync(target, ChordContext.Background);
            var (other, otherTarget) = LiveQueryOver(handle, store, owner, state);
            await other.LoadAsync(otherTarget, ChordContext.Background);
            Equal(2, store.ReadIndex.On(handle.Sql.Table)?.Count, "both are in the index");
            var watch = await plain.WatchAsync(ChordContext.Background);
            var calls = ChordSupport.ChangesOf(watch);
            // A commit runs the read, then waits; the query is detached before the commit adopts.
            var pause = Sync160Pause();
            var committed = Sync160Commit(store, state.Batch, ChordContext.Background, changes => changes(handle).Put(LiveQueryRow(1)), pause.Write);
            await pause.Ran;
            var ended = new InvalidOperationException("ended");
            plain.Detach(ended);
            pause.Release();
            await committed;
            state.Delivered();
            Equal(WatchEnd.Ended, await watch.Closed, "detach ends an existing watch");
            Equal(0, calls.Count, "a pending run cannot publish after detach");
            Check(ReferenceEquals(ended, StoreThrows(() => _ = plain.Value, "ended")), "reads throw the detach error");
            Equal(1, store.ReadIndex.On(handle.Sql.Table)?.Count, "the detached query's read left the index, the other's stays");
            Json("""[{"id":1,"value":{"n":1}}]""", other.Value, "the other query followed the commit");
            other.Detach(ended);
            Check(store.ReadIndex.On(handle.Sql.Table) is null, "nothing is left in the index");
        }),
        Case("store.live-query.160-failed-refresh-keeps-last-rows-reports-once-and-recovers-with-replacement", async () =>
        {
            var db = new TestSqliteDatabase(":memory:");
            await using var store = await StoreOver(db);
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var state = new StateHost();
            var (query, target) = LiveQueryOver(handle, store, owner, state);
            await query.LoadAsync(target, ChordContext.Background);
            var calls = ChordSupport.ChangesOf(await query.WatchAsync(ChordContext.Background));
            await db.RunAsync("UPDATE \"app.store:live-query\" SET \"value\" = ?", "not json");
            await LiveQueryRun(store, handle, state);
            await LiveQueryRun(store, handle, state);
            Equal(1, owner.Reports.Count, "one report for a continuous failure");
            Equal(1, calls.Count, "one failed call for a continuous failure");
            Check(calls[0] is WatchFailedCall<JsonNode?>, "the query watch failed");
            StoreThrows(() => _ = query.Value, owner.Reports[0].Message, "reads throw the query failure");
            var late = await query.WatchAsync(ChordContext.Background);
            Json("""[{"id":1,"value":{"n":1}}]""", late.Value, "a watch opened during failure has the last good rows");
            var lateCalls = ChordSupport.ChangesOf(late);
            state.Delivered();
            Check(lateCalls.Single() is WatchFailedCall<JsonNode?>, "a late watch first gets the failure");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(1, 10)));
            Check(((WatchValueCall<JsonNode?>)calls[^1]).Ops is [ReplaceOp], "recovery replaces the failed watch value");
            Json("""[{"id":1,"value":{"n":10}}]""", query.Value, "recovered rows");
            await db.RunAsync("UPDATE \"app.store:live-query\" SET \"value\" = ?", "not json again");
            await LiveQueryRun(store, handle, state);
            Equal(2, owner.Reports.Count, "a good run resets the failure episode");
            Equal(0, owner.Failures.Count, "a query failure remains isolated from its table owner");
            query.Detach(new InvalidOperationException("done"));
        }),
        Case("store.live-query.160-one-frame-per-commit-when-its-value-is-set-several-times-in-that-commit", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var state = new StateHost();
            var (query, target) = LiveQueryOver(handle, store, owner, state);
            await query.LoadAsync(target, ChordContext.Background);
            var calls = ChordSupport.ChangesOf(await query.WatchAsync(ChordContext.Background));
            var deliveries = new Sync160Recorder();
            query.Subscribe(deliveries);
            // Two refreshes in one commit (a host data change beside a transaction's own, a list touching several pages): the read runs in
            // the step twice, both runs are adopted in the commit's batch, and the value is set twice.
            List<IStagedWrite> staged = [];
            new TableChanges(handle, staged).Put(LiveQueryRow(2));
            TableChange[] bulk = [new TableChange(handle.Sql.Table, TableChangeKind.Bulk, null, null)];
            var first = new ReadRefresh(store.ReadIndex, [handle.Sql.Table], () => bulk, ChordContext.Background);
            var second = new ReadRefresh(store.ReadIndex, [handle.Sql.Table], () => bulk, ChordContext.Background);
            staged.Add(first.Write);
            new TableChanges(handle, staged).Put(LiveQueryRow(3));
            staged.Add(second.Write);
            await store.CommitAsync(staged, new CommitFence(() => true, (_, _) => state.Batch.Commit(() => { first.Adopt(); second.Adopt(); }, ChordContext.Background)),
                ChordContext.Background);
            state.Delivered();
            Equal(1, deliveries.Calls.Count, "one frame for the commit, as a link takes it");
            Equal(1, calls.Count, "one call for a watch");
            Json("""[["p",[],1,0,[{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}}]]]""", ChordSupport.OpsJson(((WatchValueCall<JsonNode?>)calls[0]).Ops),
                "the diff from the value before the commit to the last one set");
            Json("""[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}}]""", query.Value, "the rows");
            query.Unsubscribe(deliveries);
            query.Detach(new InvalidOperationException("done"));
        }),
        Case("store.live-query.160-a-later-read-switches-in-one-frame-and-a-failure-closes-its-reads-until-the-next-read", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => { for (var id = 1; id <= 3; id++) changes.Put(LiveQueryRow(id)); });
            var state = new StateHost();
            // The store's reads can be held: the new read's first run is caught between its statement and its adoption.
            var controlled = new LiveQueryControlledStore(store);
            var (query, all) = LiveQueryOver(handle, controlled, owner, state);
            await query.LoadAsync(all, ChordContext.Background);
            var calls = ChordSupport.ChangesOf(await query.WatchAsync(ChordContext.Background));
            string Ids() => string.Join(",", ((JsonArray)query.Value!).Select(row => row!["id"]!.GetValue<double>()));
            // Another read (arguments that follow a value): the previous value shows until the new read's first run is adopted.
            var above = LiveQueryTarget(handle, Tables.Query(handle.Definition).Where(row => Gte(row["id"], 2)));
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controlled.AfterRead = async () => { captured.TrySetResult(); await release.Task; };
            var reading = query.ReadAsync(above, ChordContext.Background);
            await captured.Task;
            controlled.AfterRead = null;
            Equal("1,2,3", Ids(), "the previous value while the new read runs");
            Equal(2, store.ReadIndex.On(handle.Sql.Table)?.Count, "both reads are registered meanwhile");
            release.TrySetResult();
            Check(await reading is ReadRun.Rows, "the new read's first run");
            state.Delivered();
            Equal("2,3", Ids(), "the new read's rows");
            Equal(1, calls.Count, "one frame for the switch");
            Json("""[["p",[],0,1,[]]]""", ChordSupport.OpsJson(((WatchValueCall<JsonNode?>)calls[0]).Ops), "the diff of the two values");
            Equal(1, store.ReadIndex.On(handle.Sql.Table)?.Count, "the read of the previous arguments left the index");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(4)));
            Json("""[["p",[],2,0,[{"id":4,"value":{"n":4}}]]]""", ChordSupport.OpsJson(((WatchValueCall<JsonNode?>)calls[^1]).Ops), "the new read follows commits");
            // What it should read cannot be read: a failed member, and no read may heal it.
            var refused = new InvalidOperationException("its arguments threw");
            query.Fail(refused, ChordContext.Background);
            state.Delivered();
            Check(calls[^1] is WatchFailedCall<JsonNode?>, "the watch failed");
            Equal("its arguments threw", owner.Reports.Single().Message, "reported once");
            Check(store.ReadIndex.On(handle.Sql.Table) is null, "its reads are closed");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(5)));
            Check(ReferenceEquals(refused, StoreThrows(() => _ = query.Value, "its arguments threw")), "a commit does not heal it");
            await query.ReadAsync(all, ChordContext.Background);
            state.Delivered();
            Equal("1,2,3,4,5", Ids(), "the next read heals it");
            Check(((WatchValueCall<JsonNode?>)calls[^1]).Ops is [ReplaceOp], "the value replaces the failure whole");
            query.Detach(new InvalidOperationException("done"));
        }),
        Case("store.live-query.160-rows-with-more-and-a-count-are-the-shapes-a-named-query-is-read-in", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => { for (var id = 1; id <= 3; id++) changes.Put(LiveQueryRow(id)); });
            var state = new StateHost();
            var target = LiveQueryTarget(handle, Tables.Query(handle.Definition));
            var page = new LiveQuery(new ReadSite(store, state.Batch), owner, new LiveShape.Rows(new ReadRange(2), WithMore: true));
            var count = new LiveQuery(new ReadSite(store, state.Batch), owner, new LiveShape.Count());
            await page.LoadAsync(target, ChordContext.Background);
            await count.LoadAsync(target, ChordContext.Background);
            Json("""{"rows":[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}}],"more":true}""", page.Value, "the first two rows, and more follow");
            Json("3", count.Value, "the count");
            var pageCalls = ChordSupport.ChangesOf(await page.WatchAsync(ChordContext.Background));
            var countCalls = ChordSupport.ChangesOf(await count.WatchAsync(ChordContext.Background));
            await LiveQueryCommit(store, handle, state, changes => changes.Delete(new JsonObject { ["id"] = 1 }));
            Json("""[["p",["rows"],0,1,[]],["p",["rows"],1,0,[{"id":3,"value":{"n":3}}]],["s",["more"],false]]""",
                ChordSupport.OpsJson(((WatchValueCall<JsonNode?>)pageCalls.Single()).Ops), "the rows' ops under rows, then more");
            Json("""{"rows":[{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}}],"more":false}""", page.Value, "the page after the delete");
            Json("""[["r",2]]""", ChordSupport.OpsJson(((WatchValueCall<JsonNode?>)countCalls.Single()).Ops), "a count is replaced");
            // A count keeps no keys: an update of a column its condition does not use is skipped, a put runs it.
            await LiveQueryCommit(store, handle, state, changes => changes.Update(new JsonObject { ["id"] = 2 }, new JsonObject { ["value"] = new JsonObject { ["n"] = 20 } }));
            Equal(1, countCalls.Count, "the count was skipped");
            Equal(2, pageCalls.Count, "the page's row changed");
            var ended = new InvalidOperationException("done");
            page.Detach(ended);
            count.Detach(ended);
        }),
        Case("store.live-query.160-refused-step-adopts-nothing", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var state = new StateHost();
            var (query, target) = LiveQueryOver(handle, store, owner, state);
            await query.LoadAsync(target, ChordContext.Background);
            var refuse = new StagedWrite(WriteOrder.Write, _ => throw new InvalidOperationException("refuse after query read"));
            await StoreRejection(Sync160Commit(store, state.Batch, ChordContext.Background, changes => changes(handle).Put(LiveQueryRow(2)), refuse));
            Json("""[{"id":1,"value":{"n":1}}]""", query.Value, "the refused run was never adopted");
            Equal(1L, await handle.CountAsync(Tables.Query(LiveQueryTable), ChordContext.Background), "the write rolled back");
            // The rolled-back step's run is no state of the read: the next commit tests against the adopted rows, and its put is seen.
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(3)));
            Json("""[{"id":1,"value":{"n":1}},{"id":3,"value":{"n":3}}]""", query.Value, "the next commit's rows");
            query.Detach(new InvalidOperationException("done"));
        }),
        Case("store.live-query.160-activation-failure-throws-unreported-and-a-slow-load-cannot-replace-a-newer-adoption-or-revive-a-detached-query", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var controlled = new LiveQueryControlledStore(store);
            var wrapped = new TableHandle(handle.Definition, handle.Sql.Table, controlled, owner);
            var state = new StateHost();
            var (query, target) = LiveQueryOver(wrapped, controlled, owner, state);
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controlled.AfterRead = async () => { captured.TrySetResult(); await release.Task; };
            var loading = query.LoadAsync(target, ChordContext.Background);
            await captured.Task;
            controlled.AfterRead = null;
            await LiveQueryCommit(store, wrapped, state, changes => changes.Put(LiveQueryRow(2)));
            release.TrySetResult();
            await loading;
            Json("""[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}}]""", query.Value, "the newer commit wins over the slow activation run");
            controlled.Failure = new InvalidOperationException("activation read failed");
            var (failed, failedTarget) = LiveQueryOver(wrapped, controlled, owner, state);
            Check(ReferenceEquals(controlled.Failure, await StoreRejection(failed.LoadAsync(failedTarget, ChordContext.Background))), "the first run's failure fails the load");
            Equal(0, owner.Reports.Count, "an activation failure is its activation's to report");
            controlled.Failure = null;
            var (detached, detachedTarget) = LiveQueryOver(wrapped, controlled, owner, state);
            captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controlled.AfterRead = async () => { captured.TrySetResult(); await release.Task; };
            var detachingLoad = detached.LoadAsync(detachedTarget, ChordContext.Background);
            await captured.Task;
            var ended = new InvalidOperationException("attempt ended");
            detached.Detach(ended);
            release.TrySetResult();
            await detachingLoad;
            Check(ReferenceEquals(ended, StoreThrows(() => _ = detached.Value, "attempt ended")), "a late activation run cannot revive a detached query");
            query.Detach(ended);
            failed.Detach(ended);
        }),
    ];

    /// <summary>A commit that runs every read over <paramref name="handle"/>'s table again (a bulk write names no rows) and writes nothing.</summary>
    private static async Task LiveQueryRun(ChordStore store, TableHandle handle, StateHost state)
    {
        var refresh = new ReadRefresh(store.ReadIndex, [handle.Sql.Table], () => [new TableChange(handle.Sql.Table, TableChangeKind.Bulk, null, null)], ChordContext.Background);
        await store.CommitAsync([refresh.Write], new CommitFence(() => true, (_, _) => state.Batch.Commit(refresh.Adopt, ChordContext.Background)), ChordContext.Background);
        state.Delivered();
    }
}

internal sealed class LiveQueryTestOwner : ITableOwner, IFramedOwner
{
    public string Label => "app.store";
    public object Scope { get; } = new();
    internal List<Exception> Reports { get; } = [];
    internal List<ChordError> Failures { get; } = [];
    public void AssertLive() { }
    public void Fail(ChordError error) => Failures.Add(error);
    public void Report(Exception error) => Reports.Add(error);
}

/// <summary>Holds the result of a completed read, so a newer commit can overtake activation without blocking SQLite.</summary>
internal sealed class LiveQueryControlledStore(ChordStore store) : IChordStore
{
    internal Func<Task>? AfterRead { get; set; }
    internal Exception? Failure { get; set; }
    public TableRecord? Table(string name) => store.Table(name);
    public long? TableVersion(string name) => store.TableVersion(name);
    public Action AnnounceTable(string name, long version) => store.AnnounceTable(name, version);
    public Task<long> CommitAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context) => store.CommitAsync(writes, fence, context);
    public Reads ReadIndex => store.ReadIndex;
    public async Task<T> ReadAsync<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context)
    {
        if (Failure is { } failure) throw failure;
        var result = await store.ReadAsync(read, context);
        if (AfterRead is { } after) await after();
        return result;
    }
}

/// <summary>An executor of a count: each query answers the next number (upstream's stub <c>run: async () =&gt; ({ kind: "count", count:
/// ++runs })</c>).</summary>
internal sealed class LiveQueryExecutor : ISqliteExecutor
{
    private long runs;
    public Task ExecAsync(string sql) => throw new NotSupportedException();
    public Task RunAsync(string sql, params object?[] parameters) => throw new NotSupportedException();
    public Task<long> RunCountedAsync(string sql, params object?[] parameters) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["count"] = ++runs });
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) => throw new NotSupportedException();
}
