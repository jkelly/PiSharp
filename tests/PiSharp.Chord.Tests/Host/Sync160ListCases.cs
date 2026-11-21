using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/list.test.ts.
//
// Lists (slice 14 §4, test list host/list and 19): pages between fixed bookmarks. After every step of randomized runs (touches, inserts,
// deletes, bulk writes, commits queued together) the list's value is a gapless, duplicate-free prefix of the true order, and the whole
// order when moreAfter is false. More adds only inserts; a row moving between pages is one move; the last page is open while no row is
// behind it; pages split and merge without changing a row; overflow shows a prefix; arguments that follow a value switch every page in
// one frame; a reload keeps the bookmarks. Differences from upstream:
// - "the loaded rows are the same objects" (toBe): no node is shared between two values here (State/Framed.cs). What the identity stands
//   for, that a load sends only the new rows, is asserted on the frames the list itself publishes: one insert behind the loaded rows.
// - Frames are read at the list (the member's source in its attempt) where a case compares them one by one: a watch may fold frames, and
//   a reference's member relays values and diffs them again (Host/Reference.cs).
// - "commit groups": the native store writes each commit in a transaction of its own (group commit is not ported, see the chord-store
//   report); the commits a step queues together run one after another.
// - A reload's report returns before a restarted object's successor is live (the native activation parks on its dependencies before it
//   prepares its tables); the cases wait for the reload's work.
internal static partial class Program
{
    private static readonly TableDefinition ListRows = Tables.DefineTable("rows", new TableOptions
    {
        Version = 1,
        Columns = new() { ["id"] = Int(), ["group"] = Str(), ["touched"] = Int(), ["title"] = Str(@default: "") },
        Key = ["id"],
        Indexes = StoreIndexes(("by_group", ["group", "touched"])),
    });

    private static readonly JsonObject ListItem = Obj(("id", Int()), ("touched", Int()));

    private static readonly JsonObject ListRow = Obj(("id", Int()), ("group", Str()), ("touched", Int()));

    private static readonly Contract ListStore = Contract.Local("app.rows", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["rows"] = Queries(ListItem, ("ofGroup", Obj(("group", Str())))),
            ["write"] = Method(Arr(Obj(("put", ListRow)))),
            ["touch"] = Method(Obj(("id", Int()), ("at", Int()))),
            ["drop"] = Method(Int()),
            ["clear"] = Method(Str()),
        },
    });

    private static readonly Contract ListScreen = Contract.Local("app.screen", new()
    {
        Version = 1,
        Params = new() { ["view"] = Str() },
        Members = new() { ["list"] = Obj(("rows", Arr(ListItem)), ("moreAfter", Bool())), ["more"] = Method(), ["show"] = Method(Str()) },
    });

    /// <summary>The screen as a client holds it over a link: the list of group "a" and <c>more</c>.</summary>
    private static readonly Contract ListBoard = Contract.Remote("app.board", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new() { ["main"] = new() { State = { ["list"] = Obj(("rows", Arr(ListItem)), ("moreAfter", Bool())) }, Methods = { ["more"] = Method() } } },
    });

    /// <summary>What a test adds to the store's factory, and takes of it: more declarations (a native), its named queries.</summary>
    private sealed class ListStoreProbe
    {
        internal Action<IStoreSelf>? Declare;
        internal List<WeakReference>? Queries;
    }

    private static void ListServeStore(IFacetHost h, OrderDirection direction = OrderDirection.Desc, ListStoreProbe? probe = null) => h.Serve(ListStore, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        var table = self.Table(ListRows);
        probe?.Declare?.Invoke(self);
        var rows = self.Queries(table, new QueriesOptions(
            Map: (row, _) => new JsonObject { ["id"] = row["id"]!.DeepClone(), ["touched"] = row["touched"]!.DeepClone() }, Reads: ["id", "touched"]),
            // by_group (group, touched): the newest touched first.
            Q7Builders(("ofGroup", Q7Builder(args =>
            {
                var group = (string)args!["group"]!;
                return Tables.Query(ListRows).Where(r => Eq(r["group"], group)).OrderBy(r => r["touched"], direction);
            }))));
        if (probe?.Queries is { } queries) lock (queries) queries.Add(new WeakReference(rows["ofGroup"]));
        return new(new Dictionary<string, object?>
        {
            ["rows"] = rows,
            ["write"] = Q7Tx(self, (tx, puts) => { foreach (var each in puts!.AsArray()) tx.Put(table, each!["put"]!.DeepClone()); }),
            ["touch"] = Q7Tx(self, (tx, args) => tx.Update(table, new JsonObject { ["id"] = args!["id"]!.DeepClone() }, new JsonObject { ["touched"] = args["at"]!.DeepClone() })),
            ["drop"] = Q7Tx(self, (tx, id) => tx.Delete(table, new JsonObject { ["id"] = id!.DeepClone() })),
            ["clear"] = Q7Tx(self, (tx, group) => tx.DeleteWhere(table, r => Eq(r["group"], group!.GetValue<string>()))),
        });
    });

    /// <summary>The screen: a list of the group a method shows; <paramref name="factories"/> counts how often its factory ran.</summary>
    private static void ListServeScreen(IFacetHost h, long first, int[]? factories = null) => h.Serve(ListScreen, (_, _, self) =>
    {
        if (factories is not null) Interlocked.Increment(ref factories[0]);
        var shown = self.Writable("a");
        var args = self.Derive([shown], values => new JsonObject { ["group"] = (string)values[0]! });
        var list = self.List("rows", [(Q7Named(self.Get(ListStore, new()), "rows", "ofGroup"), args)], new ListOptions(first));
        return new(new Dictionary<string, object?>
        {
            ["list"] = list.Value,
            ["more"] = (HostMethod)(async (_, ctx) => { await list.MoreAsync(ctx.Context); return null; }),
            ["show"] = (HostMethod)((group, _) =>
            {
                shown.Set(((JsonNode)group!).GetValue<string>());
                return Task.FromResult<object?>(null);
            }),
        });
    });

    private static PluginFacet ListStorePlugin(OrderDirection direction = OrderDirection.Desc, ListStoreProbe? probe = null) =>
        Q7Plugin("@test/store", h => ListServeStore(h, direction, probe));

    private static PluginFacet ListScreenPlugin(long first, int[]? factories = null) => Q7Plugin("@test/screen", h => ListServeScreen(h, first, factories));

    /// <summary>A host with the store and a screen of pages of <c>first</c> rows; <c>seed</c> rows of group "a" touched at their id
    /// (upstream <c>hosted</c>).</summary>
    private sealed class ListHosted : IAsyncDisposable
    {
        internal ChordStore Store = null!;
        internal ChordHost Host = null!;
        internal IReference Rows = null!;
        internal IReference Screen = null!;
        internal PluginFacet StorePlugin = null!;
        internal PluginFacet ScreenPlugin = null!;

        internal static async Task<ListHosted> Create(long first, int seed, int[]? factories = null, ListStoreProbe? probe = null)
        {
            var hosted = new ListHosted { Store = await MemoryStore(), StorePlugin = ListStorePlugin(OrderDirection.Desc, probe), ScreenPlugin = ListScreenPlugin(first, factories) };
            hosted.Host = Q7Host(hosted.Store);
            await hosted.Host.LoadAsync([hosted.StorePlugin, hosted.ScreenPlugin], ChordContext.Background);
            hosted.Rows = await hosted.Host.GetAsync(ListStore, new(), ChordContext.Background);
            if (seed > 0) await hosted.Write(Enumerable.Range(1, seed).Select(id => (id, "a", id)));
            hosted.Screen = await hosted.Host.GetAsync(ListScreen, new() { ["view"] = "v" }, ChordContext.Background);
            return hosted;
        }

        internal Task Write(IEnumerable<(int Id, string Group, int Touched)> rows) => Rows.CallAsync("write",
            new JsonArray([.. rows.Select(row => (JsonNode)new JsonObject { ["put"] = new JsonObject { ["id"] = row.Id, ["group"] = row.Group, ["touched"] = row.Touched } })]),
            ChordContext.Background);

        internal Task Touch(int id, int at) => Rows.CallAsync("touch", new JsonObject { ["id"] = id, ["at"] = at }, ChordContext.Background);

        internal Task Drop(int id) => Rows.CallAsync("drop", JsonValue.Create(id), ChordContext.Background);

        internal Task More() => Screen.CallAsync("more", null, ChordContext.Background);

        /// <summary>The ids the list shows, and whether rows may follow.</summary>
        internal (string Ids, bool MoreAfter) Shown()
        {
            var value = Screen.Member("list").Value!;
            return (Q7Field(value, "id"), value["moreAfter"]!.GetValue<bool>());
        }

        internal List<int> Ids() => [.. ((JsonArray)Screen.Member("list").Value!["rows"]!).Select(row => (int)row!["id"]!.GetValue<double>())];

        /// <summary>The list itself: the member's source in the screen's current attempt.</summary>
        internal FramedSource Source => Q7Attempt(Host, ListScreen).Self.Readers.Single().Source;

        internal int Registered => Q7Reads(Store, "app.rows:rows");

        /// <summary>Reloads and waits for the reload's work.</summary>
        internal async Task Reload(params PluginFacet[] plugins)
        {
            await Host.ReloadAsync(plugins, ChordContext.Background);
            await Host.DrainReloadsAsync(ChordContext.Background);
            await Q7Settled(Host);
        }

        /// <summary>Lets what is queued on the host's dispatcher run.</summary>
        internal async Task Flushed()
        {
            for (var round = 0; round < 2; round++) await Host.Dispatcher.RunAsync(() => Task.CompletedTask);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    private static string ListJoined(IEnumerable<int> ids) => string.Join(" ", ids);

    /// <summary>The ids of the rows of a list's value.</summary>
    private static List<int> ListIdsOf(JsonNode? value) => [.. ((JsonArray)value!["rows"]!).Select(row => (int)row!["id"]!.GetValue<double>())];

    /// <summary>A weak reference to the screen's list, made in a frame of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ListWeakly(ListHosted hosted) => new(hosted.Source);

    private static IEnumerable<(string, Func<Task>)> HostSync160ListCases()
    {
        var context = ChordContext.Background;
        List<(string, Func<Task>)> cases = [];
        foreach (var seed in new uint[] { 1, 2, 3, 4 })
        {
            cases.Add(Case($"host.list.the-invariant-after-every-frame-the-value-is-a-gapless-prefix-of-the-true-order-seed-{seed}", async () =>
            {
                const int first = 5;
                await using var hosted = await ListHosted.Create(first, 12);
                var r = new Sync160Random(seed);
                // In insertion order, as upstream's Map: a touched row keeps its place, a new one comes last.
                var truth = new OrderedDictionary<int, int>();
                for (var id = 1; id <= 12; id++) truth[id] = id;
                var clock = 100;
                var nextId = 13;
                // The true order: newest touched first, ties by id (the key appended to the order).
                List<int> Order() => [.. truth.OrderByDescending(entry => entry.Value).ThenByDescending(entry => entry.Key).Select(entry => entry.Key)];
                void Verify(string step)
                {
                    var value = hosted.Screen.Member("list").Value!;
                    var shown = ListIdsOf(value);
                    var expected = Order();
                    Equal(ListJoined(expected.Take(shown.Count)), ListJoined(shown), step + ": a prefix");
                    if (!value["moreAfter"]!.GetValue<bool>()) Equal(expected.Count, shown.Count, step + ": the whole order");
                }
                for (var step = 0; step < 300; step++)
                {
                    var pending = new List<Task>();
                    // One commit, or several queued together.
                    for (var commit = r.Chance(0.2) ? 3 : 1; commit > 0; commit--)
                    {
                        var kind = r.Int(10);
                        List<int> live = [.. truth.Keys];
                        int? pick = live.Count == 0 ? null : live[Math.Min(r.Int(live.Count), live.Count - 1)];
                        if (live.Count == 0) r.Int(0);
                        if (kind < 4 && pick is { } touched)
                        {
                            truth[touched] = ++clock;
                            pending.Add(hosted.Touch(touched, clock));
                        }
                        else if (kind < 6)
                        {
                            var id = nextId++;
                            var at = r.Chance(0.5) ? ++clock : r.Int(50);
                            truth[id] = at;
                            pending.Add(hosted.Write([(id, "a", at)]));
                        }
                        else if (kind < 8 && pick is { } dropped)
                        {
                            truth.Remove(dropped);
                            pending.Add(hosted.Drop(dropped));
                        }
                        else if (kind == 8)
                        {
                            // A bulk write of many pages' rows at once.
                            var bulk = new List<(int, string, int)>();
                            for (var each = 0; each < 4 * first; each++)
                            {
                                var id = nextId++;
                                var at = r.Chance(0.5) ? ++clock : r.Int(50);
                                truth[id] = at;
                                bulk.Add((id, "a", at));
                            }
                            pending.Add(hosted.Write(bulk));
                        }
                        else pending.Add(hosted.More());
                    }
                    await Task.WhenAll(pending);
                    await hosted.Flushed();
                    Verify($"seed {seed}, step {step}");
                }
                // Loading to the end shows the whole order.
                for (var page = 0; page < 200 && hosted.Shown().MoreAfter; page++) await hosted.More();
                Equal(ListJoined(Order()), ListJoined(hosted.Ids()), "the whole order");
            }));
        }
        cases.AddRange(
        [
            Case("host.list.pages-loads-a-first-page-and-with-more-only-the-next-rows-the-loaded-ones-stay-as-they-are", async () =>
            {
                await using var hosted = await ListHosted.Create(3, 8);
                Equal(("8 7 6", true), hosted.Shown(), "the first page");
                var watch = await hosted.Screen.Member("list").WatchAsync(context);
                var calls = ChangesOf(watch);
                // As a link's provider side follows the list: every frame in its batch (a watch may fold two into one).
                var source = hosted.Source;
                var recorder = new Sync160Recorder();
                hosted.Host.Dispatcher.Invoke(() => source.Subscribe(recorder));
                await Task.WhenAll(hosted.More(), hosted.More());
                await Q7Settled(hosted.Host);
                Equal("8 7 6 5 4 3 2 1", ListJoined(hosted.Ids()), "every row");
                // Two calls in turn, each one frame that adds its page's rows.
                Sequence(["8 7 6 5 4 3", "8 7 6 5 4 3 2 1"], recorder.Calls.Select(call => ListJoined(ListIdsOf(((WatchValueCall<JsonNode?>)call).Value))), "one frame per call");
                Check(calls.Count is 1 or 2, "a watch gets them, folded or not");
                Equal("8 7 6 5 4 3 2 1", ListJoined(ListIdsOf(((WatchValueCall<JsonNode?>)calls[^1]).Value)), "the watch's last value");
                // The loaded rows are not sent again: each frame is one insert behind them (and moreAfter when it changed).
                Json("""[[["p",["rows"],3,0,[{"id":5,"touched":5},{"id":4,"touched":4},{"id":3,"touched":3}]]],[["p",["rows"],6,0,[{"id":2,"touched":2},{"id":1,"touched":1}]],["s",["moreAfter"],false]]]""",
                    new JsonArray([.. recorder.Calls.Select(call => (JsonNode?)OpsJson(((WatchValueCall<JsonNode?>)call).Ops))]), "only the new rows are sent");
                hosted.Host.Dispatcher.Invoke(() => source.Unsubscribe(recorder));
                await watch.StopAsync();
            }),
            Case("host.list.pages-moves-a-row-touched-in-a-later-page-to-the-top-one-frame-no-gap-no-duplicate", async () =>
            {
                await using var hosted = await ListHosted.Create(3, 9);
                await hosted.More();
                await hosted.More();
                var watch = await hosted.Screen.Member("list").WatchAsync(context);
                var calls = ChangesOf(watch);
                await hosted.Touch(2, 100);
                await Q7Settled(hosted.Host);
                Equal("2 9 8 7 6 5 4 3 1", ListJoined(hosted.Ids()), "the touched row moved to the top");
                Equal(1, calls.Count, "one frame");
                await watch.StopAsync();
            }),
            Case("host.list.pages-a-row-moving-between-two-pages-sets-the-list-once-per-page-and-publishes-one-frame-diffed-from-the-value-before-the-commit", async () =>
            {
                await using var hosted = await ListHosted.Create(3, 9);
                await hosted.More();
                await hosted.More();
                var before = Q7Value(hosted.Screen, "list");
                // As a link's provider side follows it: every frame in its batch, none folded (a watch folds them).
                var source = hosted.Source;
                var recorder = new Sync160Recorder();
                hosted.Host.Dispatcher.Invoke(() => source.Subscribe(recorder));
                // Row 2 leaves the third page and enters the first: both pages adopt in one commit.
                await hosted.Touch(2, 100);
                await hosted.Flushed();
                Equal(1, recorder.Calls.Count, "one frame for the commit");
                var call = recorder.Calls[0] as WatchValueCall<JsonNode?> ?? throw new InvalidOperationException("no value call");
                Equal("2 9 8 7 6 5 4 3 1", ListJoined(ListIdsOf(call.Value)), "the frame's value");
                Check(JsonNode.DeepEquals(DeltaApply.Apply(before!.DeepClone(), OpsJson(call.Ops)), call.Value), "the ops turn the value before the commit into the frame's");
                // The move is narrow: the row leaves its place and enters at the top; no other row is sent.
                Json("""[["p",["rows"],0,0,[{"id":2,"touched":100}]],["p",["rows"],8,1,[]]]""", OpsJson(call.Ops), "one insert and one removal");
                hosted.Host.Dispatcher.Invoke(() => source.Unsubscribe(recorder));
            }),
            Case("host.list.pages-over-a-link-the-last-row-of-a-second-page-moving-to-the-top-is-one-state-message-no-protocol-error-the-link-stays-open", async () =>
            {
                await using var store = await MemoryStore();
                var errors = new List<HostError>();
                await using var server = Q7Host(store, errors, address: Server.Address(), principal: new JsonObject { ["kind"] = "server" });
                await server.LoadAsync([ListStorePlugin(), Q7Plugin("@test/board", h => h.Serve(ListBoard, (_, _, self) =>
                {
                    var args = self.Derive([self.Writable("a")], values => new JsonObject { ["group"] = (string)values[0]! });
                    var list = self.List("rows", [(Q7Named(self.Get(ListStore, new()), "rows", "ofGroup"), args)], new ListOptions(3));
                    return new(new Dictionary<string, object?>
                    {
                        ["list"] = list.Value,
                        ["more"] = (HostMethod)(async (_, ctx) => { await list.MoreAsync(ctx.Context); return null; }),
                    }, "main");
                }))], context);
                var rows = await server.GetAsync(ListStore, new(), context);
                await rows.CallAsync("write", new JsonArray([.. new[] { 1, 2, 3, 4 }.Select(id => (JsonNode)new JsonObject { ["put"] = new JsonObject { ["id"] = id, ["group"] = "a", ["touched"] = id } })]), context);
                await using var client = Q7Host(null, errors, principal: new JsonObject { ["kind"] = "owner" });
                await client.LoadAsync([], context);
                var pair = MemoryLinkPair.Create();
                server.Accept(pair.First, new() { Principal = new JsonObject { ["kind"] = "owner" } }, context);
                client.Connect(pair.Second, new() { Principal = new JsonObject { ["kind"] = "server" } }, context);
                var held = await client.GetAsync(ListBoard, new(), context);
                await held.CallAsync("more", null, context);
                await Q7Until(() => ListJoined(ListIdsOf(held.Member("list").Value)) == "4 3 2 1", "both pages arrive");
                // The one row of the second page becomes the first row of the first.
                await rows.CallAsync("touch", new JsonObject { ["id"] = 1, ["at"] = 100 }, context);
                await Q7Until(() => ListJoined(ListIdsOf(held.Member("list").Value)) == "1 4 3 2", "the move arrives");
                await Q7Settled(server, client);
                Equal(0, errors.Count, "no protocol error: " + string.Join("; ", errors.Select(error => $"{error.Kind} {error.Error?.Message}")));
                Equal("live", held.State.Value.Kind, "the link stays open");
                held.Release();
            }),
            Case("host.list.pages-keeps-the-last-page-open-while-no-row-is-behind-it-empty-short-and-exactly-full-lists-take-a-new-row", async () =>
            {
                foreach (var seed in new[] { 0, 2, 3 })
                {
                    await using var hosted = await ListHosted.Create(3, seed);
                    Equal((seed, false), (hosted.Ids().Count, hosted.Shown().MoreAfter), $"seed {seed}: the whole list");
                    // A row behind everything shown (the order is descending: the oldest touch is last).
                    await hosted.Write([(50, "a", 0)]);
                    if (seed < 3) Equal((50, false), (hosted.Ids()[^1], hosted.Shown().MoreAfter), $"seed {seed}: the new row appears");
                    else Equal(("3 2 1", true), hosted.Shown(), "a full page ends there, and says more");
                }
            }),
            Case("host.list.pages-a-stale-more-after-is-corrected-by-a-more-that-loads-nothing-the-last-page-is-open-again", async () =>
            {
                await using var hosted = await ListHosted.Create(2, 3);
                Equal(("3 2", true), hosted.Shown(), "the first page");
                // The row behind the bounded page goes: the list cannot know, and still says more.
                await hosted.Drop(1);
                Check(hosted.Shown().MoreAfter, "a stale moreAfter");
                await hosted.More();
                Equal(("3 2", false), hosted.Shown(), "corrected by a more that loads nothing");
                // Open at its end again: a row behind a full page is known at once, behind a short one it appears.
                await hosted.Write([(9, "a", 0)]);
                Equal(("3 2", true), hosted.Shown(), "a row behind a full page is known at once");
                await hosted.Drop(9);
                await hosted.Drop(2);
                await hosted.More();
                await hosted.Write([(8, "a", 0)]);
                Equal(("3 8", false), hosted.Shown(), "behind a short page it appears");
            }),
            Case("host.list.pages-shows-a-prefix-never-a-hole-while-a-bulk-write-overflows-a-page-and-loads-on-by-itself", async () =>
            {
                const int first = 2;
                await using var hosted = await ListHosted.Create(first, 4);
                await hosted.More();
                var source = hosted.Source;
                var recorder = new Sync160Recorder();
                hosted.Host.Dispatcher.Invoke(() => source.Subscribe(recorder));
                // Twenty rows into the first page's range: more than a page reads (4 pages' rows).
                var bulk = Enumerable.Range(0, 20).Select(index => (100 + index, "a", 1000 + index)).ToList();
                await hosted.Write(bulk);
                List<int> expected = [.. bulk.Select(row => row.Item1).Reverse(), 4, 3, 2, 1];
                await Q7Until(() => hosted.Shown() == (ListJoined(expected), false), "the list loads on by itself");
                await Q7Settled(hosted.Host);
                List<JsonNode?> frames = [.. recorder.Calls.Select(call => ((WatchValueCall<JsonNode?>)call).Value)];
                // The first frame ends at the cut and says more; every frame is a prefix; the last has everything again.
                Equal((4 * first, true), (ListIdsOf(frames[0]).Count, frames[0]!["moreAfter"]!.GetValue<bool>()), "the first frame ends at the cut");
                foreach (var frame in frames) Equal(ListJoined(expected.Take(ListIdsOf(frame).Count)), ListJoined(ListIdsOf(frame)), "every frame is a prefix");
                hosted.Host.Dispatcher.Invoke(() => source.Unsubscribe(recorder));
            }),
            Case("host.list.pages-keeps-the-number-of-pages-bounded-over-many-touches-the-rows-a-touch-moves-merge-back", async () =>
            {
                const int first = 4;
                await using var hosted = await ListHosted.Create(first, 40);
                for (var page = 0; page < 12; page++) await hosted.More();
                Equal(40, hosted.Ids().Count, "every row is loaded");
                for (var touch = 0; touch < 400; touch++) await hosted.Touch(1 + touch * 7 % 40, 1000 + touch);
                Equal(40, hosted.Ids().Count, "every row is still loaded");
                // Every page is one read of the store over the table. Neighbours that hold more than one page's rows together stay apart, so a
                // page holds more than half a page on average: at most 2 x 40 / 4 pages, at least 40 / 8.
                var reads = hosted.Registered;
                Check(reads is >= 5 and <= 20, $"the number of pages stays bounded: {reads}");
            }),
            Case("host.list.arguments-that-follow-a-value-switch-every-page-in-one-frame-and-start-again-at-a-first-page-of-the-other-rows", async () =>
            {
                await using var hosted = await ListHosted.Create(2, 5);
                await hosted.Write(new[] { 6, 7, 8 }.Select(id => (id, "b", id)));
                await hosted.More();
                var watch = await hosted.Screen.Member("list").WatchAsync(context);
                var calls = ChangesOf(watch);
                await hosted.Screen.CallAsync("show", JsonValue.Create("b"), context);
                await Q7Until(() => ListJoined(hosted.Ids()) == "8 7 6", "the other group's rows");
                await Q7Settled(hosted.Host);
                // The bookmarks are positions of the same order: the pages reopen under them, for the other group's rows.
                Equal(1, calls.Count, "one frame");
                await watch.StopAsync();
            }),
            Case("host.list.reload-of-the-lists-plugin-keeps-the-bookmarks-the-pages-it-had-one-value-no-first-page-collapse", async () =>
            {
                var factories = new int[1];
                await using var hosted = await ListHosted.Create(3, 10, factories);
                await hosted.More();
                await hosted.More();
                Equal("10 9 8 7 6 5 4 3 2", ListJoined(hosted.Ids()), "three pages");
                await hosted.Reload(hosted.StorePlugin, ListScreenPlugin(3, factories));
                // The first activation, staging's recording run, and the successor.
                Equal(3, factories[0], "the factory ran three times");
                await Q7Until(() => hosted.Shown() == ("10 9 8 7 6 5 4 3 2", true), "the successor shows the pages the list had");
                // A write into a reopened range is one update.
                await hosted.Touch(5, 500);
                Equal("5 10 9 8 7 6 4 3 2", ListJoined(hosted.Ids()), "a write into a reopened range");
            }),
            Case("host.list.reload-of-the-stores-plugin-does-not-restart-the-list-the-same-rows-send-nothing-a-new-order-starts-again", async () =>
            {
                var factories = new int[1];
                await using var hosted = await ListHosted.Create(3, 10, factories);
                await hosted.More();
                var watch = await hosted.Screen.Member("list").WatchAsync(context);
                var calls = ChangesOf(watch);
                await hosted.Reload(ListStorePlugin(), hosted.ScreenPlugin);
                await Q7Until(() => hosted.Registered == 2, "the list reads the successor's query");
                await Q7Settled(hosted.Host);
                Equal(1, factories[0], "the list's factory did not run again");
                Equal(0, calls.Count, "the same rows send nothing");
                Equal("10 9 8 7 6 5", ListJoined(hosted.Ids()), "the rows it had");
                // The new builder orders the other way: the bookmarks are of another order, the list starts again.
                await hosted.Reload(ListStorePlugin(OrderDirection.Asc), hosted.ScreenPlugin);
                await Q7Until(() => hosted.Shown() == ("1 2 3", true), "a new order starts again with a first page");
                await watch.StopAsync();
            }),
            Case("host.list.reload-a-more-called-while-the-stores-successor-opens-waits-for-it-nothing-is-read-through-the-retired-query-and-nothing-of-it-stays", async () =>
            {
                // Code review round 2 (Astra B2, Sol B1), through the public reload: the successor waits in its native's open, after the old
                // attempt's reads retired. `more` used the old tiling: its closed query registered a read of the old code's statement, which
                // stayed in the store's index after the screen was released.
                var old = new ListStoreProbe { Queries = [] };
                await using var hosted = await ListHosted.Create(2, 4, null, old);
                Equal("4 3", ListJoined(hosted.Ids()), "the first page");
                var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var next = ListStorePlugin(OrderDirection.Desc, new ListStoreProbe
                {
                    Declare = self => self.Open(async _ => { opening.TrySetResult(); await opened.Task; return true; }, (_, _, _) => Task.CompletedTask),
                });
                var reloaded = hosted.Host.ReloadAsync([next, hosted.ScreenPlugin], context);
                await opening.Task;
                Equal(0, hosted.Registered, "the old provider's reads retired");
                var more = hosted.More();
                await Q7Settled(hosted.Host);
                Equal((false, 0), (more.IsCompleted, hosted.Registered), "more waits, and registers nothing");
                opened.TrySetResult();
                await reloaded;
                await more;
                // Loaded behind the successor's pages, with its statement.
                await Q7Until(() => hosted.Shown() == ("4 3 2 1", false), "loaded behind the successor's pages");
                Check(hosted.Registered > 0, "the successor's pages are registered");
                // Nothing keeps the old code's query while the screen still shows the list.
                await hosted.Host.DrainReloadsAsync(context);
                await Q7Settled(hosted.Host);
                await ExpectCollected(old.Queries![0]);
                hosted.Screen.Release();
                await Q7Until(() => hosted.Registered == 0, "the released screen's pages leave the index");
            }),
            Case("host.list.declaration-and-memory-refuses-a-list-without-a-name-a-second-list-of-a-name-no-page-size-and-several-sources", async () =>
            {
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                var thrown = new List<string>();
                var bad = Contract.Local("app.bad", new() { Version = 1, Params = new(), Members = new() { ["n"] = Int() } });
                await host.LoadAsync([Q7Plugin("@test/bad", h =>
                {
                    ListServeStore(h);
                    h.Serve(bad, (_, _, self) =>
                    {
                        (INamedQuery, object?) source = (Q7Named(self.Get(ListStore, new()), "rows", "ofGroup"), new JsonObject { ["group"] = "a" });
                        Q7Attempting(thrown, () => self.List("", [source], new ListOptions(3)));
                        Q7Attempting(thrown, () => self.List("a", [source], new ListOptions(0)));
                        Q7Attempting(thrown, () => self.List("a", [source, source], new ListOptions(3)));
                        self.List("a", [source], new ListOptions(3));
                        Q7Attempting(thrown, () => self.List("a", [source], new ListOptions(3)));
                        return new(new Dictionary<string, object?> { ["n"] = self.Derive([], _ => JsonValue.Create(1)) });
                    });
                })], context);
                await host.GetAsync(bad, new(), context);
                Sequence(
                [
                    "app.bad: self.list: a list has a name",
                    "app.bad: self.list: { first: n } is required, a positive integer",
                    "app.bad: self.list: one source, [[namedQuery, args]]",
                    "app.bad: list \"a\" is declared twice",
                ], thrown, "each refusal");
            }),
            Case("host.list.declaration-and-memory-keeps-nothing-of-a-list-once-its-object-ended-its-pages-leave-the-stores-index", async () =>
            {
                ListHosted? hosted = await ListHosted.Create(3, 10);
                await hosted.More();
                var list = ListWeakly(hosted);
                Check(hosted.Registered > 0, "its pages are registered");
                var store = hosted.Store;
                hosted.Screen.Release();
                await Q7Until(() => Q7Reads(store, "app.rows:rows") == 0, "its pages leave the index");
                await hosted.DisposeAsync();
                hosted = null;
                await ExpectCollected(list);
            }),
        ]);
        return cases;
    }
}
