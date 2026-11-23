using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/tiling.test.ts.
//
// A list's pages under several owners (slice 14 §4, test list host/list "commit groups", code review round 1): a team's sessions, driven
// from the club's team rows and joined with the sessions' rows, so two owners' commits reach one page. The runs of one commit are all
// computed before any is adopted: a run of a page an earlier adoption cut or merged applies to the pages that cover its interval, which
// then know its keys and whether it was cut. After every adoption of randomized runs the value is a gapless prefix of the true order,
// compared row by row. Differences from upstream:
// - Commit groups. ChordStore coalesces commits queued behind a running one into one backend transaction (group commit, S18), so the
//   16 randomized cases (`Task.WhenAll(fill, a, b)`) and `a-commit-refused-in-its-step` exercise real multi-owner commit groups.
//   The deterministic `hosted.Group` cases run on separate actor lines whose threads are not deterministically ordered against each other,
//   so `hosted.Group` awaits `first` then `second` to preserve exact deterministic ordering, while `Sync160StoreCases` asserts the
//   store-level group-commit coalescing, transaction count, and refusal isolation directly.
// - What a commit wrote. Upstream takes it from a queue when the commit is queued (the commits are queued in the order of the calls).
//   Here a transaction runs on whatever thread its object's line resumed it on, so two owners' commits are queued in no fixed order: a
//   method takes what its call wrote when it is called (on the host's dispatcher, in the order of the calls) and hands it to its own
//   commit (TilingTx).
// - A page's run that fails. Upstream replaces the `run` method of one read. A Read is sealed here: the failed run is adopted by that
//   read in a batch of its own (TilingFail), and a retired page's later run is fetched and adopted the same way (the case "a failed page a
//   retired page's later run is patched into").
// - store.reads.hold and ready are called on the host's dispatcher, where the list lives.
internal static partial class Program
{
    private static readonly ColumnSet TilingSessionColumns = Tables.DefineColumns("sessions.columns", new ColumnSetOptions
    { Columns = new() { ["id"] = Str(), ["title"] = Str(), ["touched"] = Int() }, Key = ["id"] });

    private static readonly TableDefinition TilingSessionRows = Tables.DefineTable("session-rows", new TableOptions
    { Version = 1, Columns = new(TilingSessionColumns.Columns), Key = ["id"], Exposes = TilingSessionColumns });

    private static readonly JsonObject TilingSession = Obj(("id", Str()), ("title", Str()), ("touched", Int()));

    /// <summary>One transaction of the sessions' owner: rows put, touched, renamed and dropped; <c>overflow</c> adds past the integer
    /// range, which is refused in the commit step (the column's check), not at staging.</summary>
    private static readonly Contract TilingSessions = Contract.Local("app.sessions", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["write"] = Method(Obj(
                ("put", Opt(Arr(TilingSession))),
                ("touch", Opt(Arr(Obj(("id", Str()), ("touched", Int()))))),
                ("rename", Opt(Arr(Obj(("id", Str()), ("title", Str()))))),
                ("drop", Opt(Arr(Str()))),
                ("overflow", Opt(Arr(Str()))))),
        },
    });

    private static readonly TableDefinition TilingTeamRows = Tables.DefineTable("team-rows", new TableOptions
    { Version = 1, Columns = new() { ["team"] = Str(), ["session"] = Str() }, Key = ["team", "session"] });

    private static readonly JsonObject TilingMembership = Obj(("team", Str()), ("session", Str()));

    private static readonly Contract TilingClub = Contract.Local("app.club", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["teams"] = Queries(TilingSession, ("ofTeam", Obj(("team", Str())))),
            ["write"] = Method(Obj(("add", Opt(Arr(TilingMembership))), ("remove", Opt(Arr(TilingMembership))))),
        },
    });

    private static readonly TableDefinition TilingFillRows = Tables.DefineTable("fill-rows", new TableOptions { Version = 1, Columns = new() { ["id"] = Int() }, Key = ["id"] });

    /// <summary>A third owner: upstream's commit that runs first, so the two that queue behind it form one group.</summary>
    private static readonly Contract TilingFiller = Contract.Local("app.filler", new() { Version = 1, Params = new(), Members = new() { ["write"] = Method(Int()) } });

    private static readonly Contract TilingScreen = Contract.Local("app.screen", new()
    {
        Version = 1,
        Params = new() { ["view"] = Str() },
        Members = new() { ["list"] = Obj(("rows", Arr(TilingSession)), ("moreAfter", Bool())), ["more"] = Method(), ["show"] = Method(Str()) },
    });

    private static IEnumerable<JsonNode> TilingEach(JsonNode? args, string field) => args?[field] is JsonArray list ? list.Select(each => each!) : [];

    // The fixture the methods of the three owners report to (one case runs at a time), and what the transaction running on this thread
    // wrote: set by its callback, taken by its commit, which is queued on the same thread right after the callback returned.
    private static TilingHosted? tilingProbe;
    [ThreadStatic]
    private static Action? tilingWrote;

    /// <summary>A method that runs one transaction of its object, and pairs what its call wrote (the fixture's next entry, taken in the
    /// order of the calls) with that transaction's commit.</summary>
    private static HostMethod TilingTx(ISelf self, Action<PiSharp.Chord.Documents.ITx, JsonNode?> write) => async (args, ctx) =>
    {
        var wrote = tilingProbe?.Take();
        await self.TxAsync(tx =>
        {
            write(tx, (JsonNode?)args);
            tilingWrote = wrote;
            return Task.FromResult(true);
        }, ctx.Context);
        return null;
    };

    private static void TilingServeFiller(IFacetHost h) => h.Serve(TilingFiller, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        var rows = self.Table(TilingFillRows);
        return new(new Dictionary<string, object?> { ["write"] = TilingTx(self, (tx, id) => tx.Put(rows, new JsonObject { ["id"] = id!.DeepClone() })) });
    });

    /// <summary>A list of <paramref name="query"/> in pages of <paramref name="first"/>, with <c>more</c>; with <c>show</c>, its arguments
    /// follow the team a method sets.</summary>
    private static HostImplementation TilingListed(ISelf self, string name, INamedQuery query, long first, bool shows)
    {
        var team = self.Writable("t");
        object args = shows ? (object)self.Derive([team], values => new JsonObject { ["team"] = (string)values[0]! }) : new JsonObject();
        var list = self.List(name, [(query, args)], new ListOptions(first));
        var members = new Dictionary<string, object?>
        {
            ["list"] = list.Value,
            ["more"] = (HostMethod)(async (_, ctx) => { await list.MoreAsync(ctx.Context); return null; }),
        };
        if (shows)
            members["show"] = (HostMethod)((shown, _) =>
            {
                team.Set(((JsonNode)shown!).GetValue<string>());
                return Task.FromResult<object?>(null);
            });
        return new(members);
    }

    private static PluginFacet TilingFacet(long first) => Q7Plugin("@test/team", h =>
    {
        h.Serve(TilingSessions, (_, _, owner) =>
        {
            var self = (IStoreSelf)owner;
            var rows = self.Table(TilingSessionRows);
            return new(new Dictionary<string, object?>
            {
                ["write"] = TilingTx(self, (tx, args) =>
                {
                    foreach (var id in TilingEach(args, "overflow"))
                        tx.Update(rows, new JsonObject { ["id"] = id.DeepClone() }, r => StorePatch(("touched", Add(r["touched"], 9007199254740991d))));
                    foreach (var row in TilingEach(args, "put")) tx.Put(rows, row.DeepClone());
                    foreach (var each in TilingEach(args, "touch")) tx.Update(rows, new JsonObject { ["id"] = each["id"]!.DeepClone() }, new JsonObject { ["touched"] = each["touched"]!.DeepClone() });
                    foreach (var each in TilingEach(args, "rename")) tx.Update(rows, new JsonObject { ["id"] = each["id"]!.DeepClone() }, new JsonObject { ["title"] = each["title"]!.DeepClone() });
                    foreach (var id in TilingEach(args, "drop")) tx.Delete(rows, new JsonObject { ["id"] = id.DeepClone() });
                }),
            });
        });
        h.Serve(TilingClub, (_, _, owner) =>
        {
            var self = (IStoreSelf)owner;
            var rows = self.Table(TilingTeamRows);
            return new(new Dictionary<string, object?>
            {
                ["teams"] = self.Queries(rows, new QueriesOptions(
                    Map: (_, session) => new JsonObject { ["id"] = session["id"]!.DeepClone(), ["title"] = session["title"]!.DeepClone(), ["touched"] = session["touched"]!.DeepClone() },
                    Reads: []),
                    // The key (team, session): driven from the team's rows, ordered by the sessions' last touch.
                    Q7Builders(("ofTeam", Q7Builder(args =>
                    {
                        var team = (string)args!["team"]!;
                        if (team == "broken") throw new InvalidOperationException("no such team");
                        return Tables.Query(TilingTeamRows).Where(t => Eq(t["team"], team))
                            .Join(TilingSessionColumns, t => Match(("id", t["session"])))
                            .OrderBy((_, session) => session["touched"], OrderDirection.Desc);
                    })))),
                ["write"] = TilingTx(self, (tx, args) =>
                {
                    foreach (var row in TilingEach(args, "remove")) tx.Delete(rows, row.DeepClone());
                    foreach (var row in TilingEach(args, "add")) tx.Put(rows, row.DeepClone());
                }),
            });
        });
        TilingServeFiller(h);
        h.Serve(TilingScreen, (_, _, self) => TilingListed(self, "team", Q7Named(self.Get(TilingClub, new()), "teams", "ofTeam"), first, shows: true));
    });

    /// <summary>A host with the three owners and a screen of team "t" in pages of <c>first</c>; sessions s1 to s<c>seed</c>, touched at
    /// their number, all in the team. Every commit's adoption is followed by <c>Adopted</c>, after the first of <c>Pending</c> (what that
    /// commit wrote) ran (upstream <c>hosted</c>).</summary>
    private sealed class TilingHosted : IAsyncDisposable
    {
        internal ChordStore Store = null!;
        internal ChordHost Host = null!;
        internal IReference Sessions = null!;
        internal IReference Club = null!;
        internal IReference Filler = null!;
        internal IReference Screen = null!;
        internal readonly Queue<Action> Pending = new();
        internal Action Adopted = () => { };
        internal volatile Exception? Failure;

        internal static async Task<TilingHosted> Create(long first, int seed)
        {
            var hosted = new TilingHosted { Store = await MemoryStore() };
            var context = ChordContext.Background;
            hosted.Host = Q7Host(hosted.Store);
            await hosted.Host.LoadAsync([TilingFacet(first)], context);
            hosted.Sessions = await hosted.Host.GetAsync(TilingSessions, new(), context);
            hosted.Club = await hosted.Host.GetAsync(TilingClub, new(), context);
            hosted.Filler = await hosted.Host.GetAsync(TilingFiller, new(), context);
            List<string> ids = [.. Enumerable.Range(1, seed).Select(index => $"s{index}")];
            await hosted.Write(put: ids.Select((id, index) => (id, $"old {id}", index + 1)));
            await hosted.Team(add: ids);
            hosted.Screen = await hosted.Host.GetAsync(TilingScreen, new() { ["view"] = "v" }, context);
            // From here on: a commit applies what its call wrote to the truth when it is adopted, and tells the probe once adopted.
            tilingProbe = hosted;
            hosted.Store.CommitFences = fence =>
            {
                var wrote = tilingWrote;
                tilingWrote = null;
                return fence with
                {
                    Adopt = (seq, ids) =>
                    {
                        wrote?.Invoke();
                        fence.Adopt(seq, ids);
                        // A failed check is the case's: kept for it, the store goes on.
                        try { hosted.Adopted(); }
                        catch (Exception error) { hosted.Failure ??= error; }
                    },
                };
            };
            return hosted;
        }

        internal void Wrote(Action wrote) { lock (Pending) Pending.Enqueue(wrote); }

        /// <summary>What the call being handled wrote: the next entry, in the order of the calls.</summary>
        internal Action? Take() { lock (Pending) return Pending.Count > 0 ? Pending.Dequeue() : null; }

        internal Task Write(IEnumerable<(string Id, string Title, int Touched)>? put = null, IEnumerable<(string Id, int Touched)>? touch = null,
            IEnumerable<(string Id, string Title)>? rename = null, IEnumerable<string>? drop = null, IEnumerable<string>? overflow = null)
        {
            var args = new JsonObject();
            if (put is not null) args["put"] = new JsonArray([.. put.Select(row => (JsonNode)new JsonObject { ["id"] = row.Id, ["title"] = row.Title, ["touched"] = row.Touched })]);
            if (touch is not null) args["touch"] = new JsonArray([.. touch.Select(row => (JsonNode)new JsonObject { ["id"] = row.Id, ["touched"] = row.Touched })]);
            if (rename is not null) args["rename"] = new JsonArray([.. rename.Select(row => (JsonNode)new JsonObject { ["id"] = row.Id, ["title"] = row.Title })]);
            if (drop is not null) args["drop"] = new JsonArray([.. drop.Select(id => (JsonNode)JsonValue.Create(id))]);
            if (overflow is not null) args["overflow"] = new JsonArray([.. overflow.Select(id => (JsonNode)JsonValue.Create(id))]);
            return Sessions.CallAsync("write", args, ChordContext.Background);
        }

        internal Task Team(IEnumerable<string>? add = null, IEnumerable<string>? remove = null, string team = "t")
        {
            JsonArray Members(IEnumerable<string> sessions) => new([.. sessions.Select(session => (JsonNode)new JsonObject { ["team"] = team, ["session"] = session })]);
            var args = new JsonObject();
            // The transaction removes, then adds.
            if (remove is not null) args["remove"] = Members(remove);
            if (add is not null) args["add"] = Members(add);
            return Club.CallAsync("write", args, ChordContext.Background);
        }

        internal Task Fill(int id) => Filler.CallAsync("write", JsonValue.Create(id), ChordContext.Background);

        internal Task More() => Screen.CallAsync("more", null, ChordContext.Background);

        internal Task Show(string team) => Screen.CallAsync("show", JsonValue.Create(team), ChordContext.Background);

        /// <summary>The three owners' commits one after another (upstream queues them together: the filler's runs alone, the other two
        /// behind it as one group).</summary>
        internal async Task Group(Func<Task> first, Func<Task> second)
        {
            await Fill(1);
            await first();
            await second();
            await Flushed();
        }

        internal JsonNode Value => Screen.Member("list").Value!;

        internal string Ids => string.Join(" ", ((JsonArray)Value["rows"]!).Select(row => (string)row!["id"]!));

        internal bool MoreAfter => Value["moreAfter"]!.GetValue<bool>();

        /// <summary>The rows as <c>id:title</c>, joined by a space (upstream <c>shown</c>).</summary>
        internal string Shown => string.Join(" ", ((JsonArray)Value["rows"]!).Select(row => $"{(string)row!["id"]!}:{(string)row["title"]!}"));

        /// <summary>The list's pages' reads, in the order of their ranges.</summary>
        internal List<Read> Pages() => [.. (Store.ReadIndex.On("app.club:team-rows") ?? [])
            .OrderBy(read => ((ReadKind.Rows)read.What).Range.After is { } after ? -StoreInternal.SqliteNumber(after[0]!) : double.NegativeInfinity)];

        internal async Task Flushed()
        {
            for (var round = 0; round < 2; round++) await Host.Dispatcher.RunAsync(() => Task.CompletedTask);
        }

        public async ValueTask DisposeAsync()
        {
            if (ReferenceEquals(tilingProbe, this)) tilingProbe = null;
            await Host.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    /// <summary>Hands <paramref name="read"/> a failed run, in a batch of its own: its page's last run could not read it.</summary>
    private static void TilingFail(ChordHost host, Read read, string message) =>
        host.Dispatcher.Invoke(() => host.Batch.Within(() => read.Adopt(new ReadRun.Failed(new InvalidOperationException(message))), ChordContext.Background));

    private static IEnumerable<(string, Func<Task>)> HostSync160TilingCases()
    {
        var context = ChordContext.Background;
        List<(string, Func<Task>)> cases =
        [
            Case("host.tiling.commits-of-two-owners-queued-together-a-run-patched-into-a-merged-page-the-page-knows-its-rows-so-a-later-rename-and-delete-reach-it", async () =>
            {
                // Review round 1 (Fable B1, Opus S2): pages [s5 s4] [s3 s2] [s1].
                await using var hosted = await TilingHosted.Create(2, 5);
                await hosted.More();
                await hosted.More();
                Equal("s5 s4 s3 s2 s1", hosted.Ids, "three pages");
                // The club removes s3 (its adoption merges the second and third page); the sessions' owner moves s5 to the end. Upstream's
                // group computes the second commit's run for the old third page before the merge, which shows s5 at once. Here the second
                // commit runs the merged page: it is full with a row behind it, ends at its last row and says more.
                await hosted.Group(() => hosted.Team(remove: ["s3"]), () => hosted.Write(touch: [("s5", 0)]));
                Equal(("s4 s2 s1", true), (hosted.Ids, hosted.MoreAfter), "after both commits: a prefix, and more");
                await hosted.More();
                Equal("s4 s2 s1 s5", hosted.Ids, "the moved row, loaded");
                await hosted.Write(rename: [("s5", "NEW")]);
                Sync160Contains("s5:NEW", hosted.Shown, "a later rename reaches the merged page");
                await hosted.Write(drop: ["s5"]);
                Equal("s4 s2 s1", hosted.Ids, "a later delete reaches it");
            }),
            Case("host.tiling.commits-of-two-owners-queued-together-c1-merges-p-and-q-c2s-run-for-qs-old-interval-changes-its-row-and-nothing-else-then-both-orders", async () =>
            {
                foreach (var clubFirst in new[] { true, false })
                {
                    // Pages P [s6 s5 s4 s3], Q [s2 s1] (first 4): C1 leaves P with [s6] and Q with [s2 s1]; C2 renames s1.
                    await using var hosted = await TilingHosted.Create(4, 6);
                    await hosted.More();
                    Task C1() => hosted.Team(remove: ["s5", "s4", "s3"]);
                    Task C2() => hosted.Write(rename: [("s1", "renamed")]);
                    await (clubFirst ? hosted.Group(C1, C2) : hosted.Group(C2, C1));
                    Json("""{"rows":[{"id":"s6","title":"old s6","touched":6},{"id":"s2","title":"old s2","touched":2},{"id":"s1","title":"renamed","touched":1}],"moreAfter":false}""",
                        hosted.Value, $"club first {clubFirst}: the merged page");
                    // The merged page is one read that knows all three rows: a touch of each moves it, a delete removes it.
                    await hosted.Write(touch: [("s1", 9)]);
                    await hosted.Write(drop: ["s2"]);
                    Equal("s1 s6", hosted.Ids, $"club first {clubFirst}: the merged page knows its rows");
                }
            }),
            Case("host.tiling.commits-of-two-owners-queued-together-a-page-cut-by-the-first-commits-adoption-takes-the-second-commits-run-for-the-uncut-page", async () =>
            {
                // One page [s3 s2 s1] of first 2 (open, short of a row behind); C1 adds five sessions' team rows (the page is cut), C2 renames
                // s2.
                await using var hosted = await TilingHosted.Create(2, 3);
                await hosted.More();
                Equal("s3 s2 s1", hosted.Ids, "the list");
                await hosted.Write(put: new[] { 4, 5, 6, 7, 8 }.Select(n => ($"s{n}", $"old s{n}", n)));
                await hosted.Group(() => hosted.Team(add: new[] { 4, 5, 6, 7, 8 }.Select(n => $"s{n}")), () => hosted.Write(rename: [("s2", "renamed")]));
                for (var page = 0; page < 6 && hosted.MoreAfter; page++) await hosted.More();
                Equal("s8:old s8 s7:old s7 s6:old s6 s5:old s5 s4:old s4 s3:old s3 s2:renamed s1:old s1", hosted.Shown, "every row once, the rename taken");
                // Every page knows its rows: each is still reached.
                await hosted.Write(drop: ["s8", "s5", "s2"]);
                Equal("s7 s6 s4 s3 s1", hosted.Ids, "each page is still reached");
            }),
            Case("host.tiling.commits-of-two-owners-queued-together-a-commit-refused-in-its-step-leaves-the-same-pages", async () =>
            {
                await using var hosted = await TilingHosted.Create(2, 5);
                await hosted.More();
                await hosted.More();
                // The sessions' commit refuses in its step (an integer past its range); the club's stands.
                var bad = Rejection(hosted.Write(overflow: ["s1"]));
                await Task.WhenAll(hosted.Fill(1), hosted.Team(remove: ["s3"]), bad);
                await hosted.Flushed();
                Equal("s5 s4 s2 s1", hosted.Ids, "the club's commit stands");
                await hosted.Write(touch: [("s1", 50)]);
                Equal("s1 s5 s4 s2", hosted.Ids, "the pages still follow");
            }),
            Case("host.tiling.one-commit-several-pages-a-merge-by-the-first-pages-run-and-the-second-pages-run-after-it-the-merged-page-knows-the-new-row", async () =>
            {
                // Review round 1 (Astra B1): pages [s6 s5 s4 s3] [s2 s1]; one commit of the club drops four rows and adds s7 (touched between
                // s3 and s1).
                await using var hosted = await TilingHosted.Create(4, 6);
                await hosted.More();
                await hosted.Write(put: [("s7", "old s7", 2)]);
                await hosted.Team(remove: ["s6", "s5", "s4", "s2"], add: ["s7"]);
                Equal("s3 s7 s1", hosted.Ids, "the merged page");
                await hosted.Team(remove: ["s7"]);
                Equal("s3 s1", hosted.Ids, "the merged page knows the new row");
            }),
            Case("host.tiling.one-commit-several-pages-a-cut-run-patched-into-a-merged-page-keeps-its-continuation-more-rows-are-behind-and-more-loads-them", async () =>
            {
                // Review round 1 (Astra B2): the second page's run returns four of ten new rows, cut by its limit; the page merged meanwhile
                // must not claim the list is complete. With rows left in the first page, and with none.
                foreach (var emptied in new[] { new[] { "s6", "s5", "s4", "s2" }, ["s6", "s5", "s4", "s3", "s2"] })
                {
                    await using var hosted = await TilingHosted.Create(4, 6);
                    await hosted.More();
                    List<string> added = [.. Enumerable.Range(0, 10).Select(index => $"n{index}")];
                    await hosted.Write(put: added.Select(id => (id, id, 2)));
                    await hosted.Team(remove: emptied, add: added);
                    List<string> all = [.. emptied.Contains("s3") ? Array.Empty<string>() : ["s3"], .. added.AsEnumerable().Reverse(), "s1"];
                    var rows = hosted.Ids.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Equal(string.Join(" ", all.Take(rows.Length)), string.Join(" ", rows), "a prefix");
                    Check(hosted.MoreAfter || rows.Length == all.Count, "more rows are behind, or the list is complete");
                    for (var page = 0; page < 6 && hosted.MoreAfter; page++) await hosted.More();
                    Equal(string.Join(" ", all), hosted.Ids, "more loads them");
                }
            }),
            Case("host.tiling.a-row-another-owners-commit-lets-in-with-the-commit-that-merged-its-pages-is-known-to-the-merged-page-its-delete-reaches-the-list", async () =>
            {
                // Code review round 1 (Sol 7): pages of two; row 35 is hidden by an `exists` in the gates' set. The rows' owner deletes 20 and
                // 40 (the pages merge), the gates' owner lets 35 in. The merged page must know 35, or its delete is skipped.
                var gateSet = Tables.DefineColumns("gate.columns", new ColumnSetOptions { Columns = new() { ["id"] = Int() }, Key = ["id"] });
                var gate = Tables.DefineTable("gate", new TableOptions { Version = 1, Columns = new(gateSet.Columns), Key = ["id"], Exposes = gateSet });
                var data = Tables.DefineTable("data", new TableOptions { Version = 1, Columns = new() { ["id"] = Int() }, Key = ["id"] });
                var item = Obj(("id", Int()));
                Query Gated() => Tables.Query(data).Where(r => Exists(gateSet, Match(("id", r["id"]))));
                var rowsType = Contract.Local("app.rows", new()
                {
                    Version = 1, Params = new(),
                    Members = new() { ["rows"] = Queries(item, ("all", Obj())), ["write"] = Method(Obj(("put", Opt(Arr(Int()))), ("drop", Opt(Arr(Int()))))) },
                });
                var gatesType = Contract.Local("app.gates", new() { Version = 1, Params = new(), Members = new() { ["put"] = Method(Arr(Int())) } });
                var gated = Contract.Local("app.gated", new()
                { Version = 1, Params = new(), Members = new() { ["list"] = Obj(("rows", Arr(item)), ("moreAfter", Bool())), ["more"] = Method() } });
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                await host.LoadAsync([Q7Plugin("@test/gated", h =>
                {
                    h.Serve(rowsType, (_, _, owner) =>
                    {
                        var self = (IStoreSelf)owner;
                        var table = self.Table(data);
                        return new(new Dictionary<string, object?>
                        {
                            ["rows"] = self.Queries(table, new QueriesOptions(Map: (row, _) => new JsonObject { ["id"] = row["id"]!.DeepClone() }),
                                Q7Builders(("all", Q7Builder(_ => Gated())))),
                            ["write"] = Q7Tx(self, (tx, args) =>
                            {
                                foreach (var id in TilingEach(args, "put")) tx.Put(table, new JsonObject { ["id"] = id.DeepClone() });
                                foreach (var id in TilingEach(args, "drop")) tx.Delete(table, new JsonObject { ["id"] = id.DeepClone() });
                            }),
                        });
                    });
                    h.Serve(gatesType, (_, _, owner) =>
                    {
                        var self = (IStoreSelf)owner;
                        var table = self.Table(gate);
                        return new(new Dictionary<string, object?>
                        {
                            ["put"] = Q7Tx(self, (tx, ids) => { foreach (var id in ids!.AsArray()) tx.Put(table, new JsonObject { ["id"] = id!.DeepClone() }); }),
                        });
                    });
                    TilingServeFiller(h);
                    h.Serve(gated, (_, _, self) => TilingListed(self, "rows", Q7Named(self.Get(rowsType, new()), "rows", "all"), 2, shows: false));
                })], context);
                var rows = await host.GetAsync(rowsType, new(), context);
                var gates = await host.GetAsync(gatesType, new(), context);
                var filler = await host.GetAsync(TilingFiller, new(), context);
                await Q7Call(rows, "write", """{"put":[10,20,30,35,40,50,60]}""");
                await Q7Call(gates, "put", "[10,20,30,40,50,60]");
                var screen = await host.GetAsync(gated, new(), context);
                await screen.CallAsync("more", null, context);
                Equal("10 20 30 40", Q7Ids(screen, "list"), "two pages");
                await Task.WhenAll(filler.CallAsync("write", JsonValue.Create(1), context), Q7Call(rows, "write", """{"drop":[20,40]}"""), Q7Call(gates, "put", "[35]"));
                await Q7Settled(host);
                Equal("10 30 35", Q7Ids(screen, "list"), "the row the gates' owner let in");
                await Q7Call(rows, "write", """{"drop":[35]}""");
                await Q7Settled(host);
                var after = Q7Ids(screen, "list").Split(' ');
                Sequence(["10", "30"], after.Take(2), "its delete reaches the list");
                Check(!after.Contains("35"), "the deleted row is gone");
            }),
            Case("host.tiling.a-publishers-migration-that-changes-the-order-switches-the-list-in-one-frame-every-row-once-in-the-new-order-no-page-reloaded-alone", async () =>
            {
                // Code review round 2 (Sol). The list joins another plugin's set and orders by its `touched`. Only the publisher reloads: its
                // migration turns the order around. The provider and the list's object keep their code and their attempts. Each page was
                // loaded again on its own: the first adoption cut and merged the others with the rows they had and closed their loads, and
                // the list ended as [1 2 3 4 5 6 1] for good.
                var touches = Tables.DefineColumns("touch.columns", new ColumnSetOptions { Columns = new() { ["id"] = Int(), ["touched"] = Int() }, Key = ["id"] });
                var touchRows = Tables.DefineTable("touches", new TableOptions { Version = 1, Columns = new(touches.Columns), Key = ["id"], Exposes = touches });
                var turned = Tables.EvolveTable(touchRows, t => t.Rows(row => new JsonObject { ["touched"] = 10 - row["touched"]!.GetValue<double>() }));
                var data = Tables.DefineTable("data", new TableOptions { Version = 1, Columns = new() { ["id"] = Int() }, Key = ["id"] });
                var item = Obj(("id", Int()));
                var rowsType = Contract.Local("app.rows", new() { Version = 1, Params = new(), Members = new() { ["rows"] = Queries(item, ("all", Obj())), ["put"] = Method(Int()) } });
                var toucherType = Contract.Local("app.toucher", new() { Version = 1, Params = new(), Members = new() { ["put"] = Method(Int()) } });
                var listed = Contract.Local("app.listed", new()
                { Version = 1, Params = new(), Members = new() { ["list"] = Obj(("rows", Arr(item)), ("moreAfter", Bool())), ["more"] = Method() } });
                var readers = Q7Plugin("@test/readers", h =>
                {
                    h.Serve(rowsType, (_, _, owner) =>
                    {
                        var self = (IStoreSelf)owner;
                        var table = self.Table(data);
                        return new(new Dictionary<string, object?>
                        {
                            ["rows"] = self.Queries(table, new QueriesOptions(Map: (row, _) => new JsonObject { ["id"] = row["id"]!.DeepClone() }),
                                Q7Builders(("all", Q7Builder(_ => Tables.Query(data).Join(touches, r => Match(("id", r["id"])))
                                    .OrderBy((_, touch) => touch["touched"], OrderDirection.Desc))))),
                            ["put"] = Q7Tx(self, (tx, id) => tx.Put(table, new JsonObject { ["id"] = id!.DeepClone() })),
                        });
                    });
                    h.Serve(listed, (_, _, self) => TilingListed(self, "rows", Q7Named(self.Get(rowsType, new()), "rows", "all"), 2, shows: false));
                });
                PluginFacet Publisher(TableDefinition table) => Q7Plugin("@test/publisher", h => h.Serve(toucherType, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    var rows = self.Table(table);
                    return new(new Dictionary<string, object?>
                    {
                        ["put"] = Q7Tx(self, (tx, id) => tx.Put(rows, new JsonObject { ["id"] = id!.DeepClone(), ["touched"] = id.DeepClone() })),
                    });
                }));
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                await host.LoadAsync([readers, Publisher(touchRows)], context);
                var rows = await host.GetAsync(rowsType, new(), context);
                var toucher = await host.GetAsync(toucherType, new(), context);
                foreach (var id in new[] { 1, 2, 3, 4, 5, 6 })
                {
                    await rows.CallAsync("put", JsonValue.Create(id), context);
                    await toucher.CallAsync("put", JsonValue.Create(id), context);
                }
                var screen = await host.GetAsync(listed, new(), context);
                await screen.CallAsync("more", null, context);
                await screen.CallAsync("more", null, context);
                Equal("6 5 4 3 2 1", Q7Ids(screen, "list"), "three pages");
                // Every frame the list publishes, none folded.
                var source = Q7Attempt(host, listed).Self.Readers.Single().Source;
                var recorder = new Sync160Recorder();
                host.Dispatcher.Invoke(() => source.Subscribe(recorder));
                await host.ReloadAsync([readers, Publisher(turned)], context);
                await host.DrainReloadsAsync(context);
                await Q7Until(() => Q7Ids(screen, "list") == "1 2 3 4 5 6", "the new order");
                await Q7Settled(host);
                Sequence(["1 2 3 4 5 6"], recorder.Calls.Select(call => call is WatchValueCall<JsonNode?> frame ? Q7Field(frame.Value, "id") : "failed"), "one frame: every row once, in the new order");
                Check(!screen.Member("list").Value!["moreAfter"]!.GetValue<bool>(), "the whole list");
                // The pages know their rows under the new plan: a write of the publisher still reaches each.
                await toucher.CallAsync("put", JsonValue.Create(3), context);
                await Q7Settled(host);
                Equal("1 2 4 5 6 3", Q7Ids(screen, "list"), "a write of the publisher reaches the pages");
                host.Dispatcher.Invoke(() => source.Unsubscribe(recorder));
            }),
            Case("host.tiling.switches-and-failures-a-switch-waits-for-every-page-of-the-new-arguments-no-page-that-has-not-run-is-shown", async () =>
            {
                // Review round 1 (Sol 5): three pages, the joined table held (its owner migrates it), then other arguments.
                await using var hosted = await TilingHosted.Create(2, 6);
                await hosted.Team(add: ["s1", "s2"], team: "u");
                await hosted.More();
                await hosted.More();
                var before = hosted.Ids;
                Equal(6, before.Split(' ').Length, "three pages");
                hosted.Host.Dispatcher.Invoke(() => hosted.Store.ReadIndex.Hold("app.sessions:session-rows"));
                await hosted.Show("u");
                await Q7Settled(hosted.Host);
                // The pages of team "u" have not run: the list still shows what it had.
                Equal(before, hosted.Ids, "the list shows what it had");
                hosted.Host.Dispatcher.Invoke(() => hosted.Store.ReadIndex.Ready("app.sessions:session-rows"));
                await Q7Until(() => hosted.Ids == "s2 s1", "the switch, once every page ran");
            }),
            Case("host.tiling.switches-and-failures-a-more-called-while-a-switch-is-pending-waits-for-it-and-loads-behind-the-new-arguments-pages", async () =>
            {
                // Code review round 3 (Sol): the new arguments' pages were held, so their start had settled without a run; `more` then loaded
                // behind the old arguments' pages and the page asked for was lost with them.
                await using var hosted = await TilingHosted.Create(2, 6);
                await hosted.Team(add: ["s1", "s2", "s3"], team: "u");
                hosted.Host.Dispatcher.Invoke(() => hosted.Store.ReadIndex.Hold("app.sessions:session-rows"));
                await hosted.Show("u");
                await Q7Settled(hosted.Host);
                var more = hosted.More();
                await Q7Settled(hosted.Host);
                Equal((false, "s6 s5"), (more.IsCompleted, hosted.Ids), "more waits for the switch");
                hosted.Host.Dispatcher.Invoke(() => hosted.Store.ReadIndex.Ready("app.sessions:session-rows"));
                await more;
                // The new arguments' rows lie behind the bookmark the list had (s5's position): the switch alone shows none of them, and the
                // page the call asked for is the first that does.
                await Q7Until(() => (hosted.Ids, hosted.MoreAfter) == ("s3 s2", true), "the page asked for, behind the new arguments' pages");
            }),
            Case("host.tiling.switches-and-failures-a-failed-page-fails-the-list-until-that-page-runs-well-another-pages-run-does-not-heal-it", async () =>
            {
                // Review round 1 (Sol 6): three pages; the first page's run fails.
                await using var hosted = await TilingHosted.Create(2, 6);
                await hosted.More();
                await hosted.More();
                Task Rename(string title) => hosted.Write(rename: new[] { "s6", "s4", "s2" }.Select(id => (id, title)));
                await Rename("x");
                await hosted.Write(rename: [("s1", "y")]);
                var pages = hosted.Pages();
                TilingFail(hosted.Host, pages[0], "page failure");
                StoreThrows(() => _ = hosted.Value, "page failure", "the list is failed");
                // A run of another page alone changes nothing about that: the third page's run, adopted while the first is failed.
                var good = await hosted.Store.ReadAsync(executor => pages[2].FetchAsync(executor, context), context);
                Check(good is ReadRun.Rows, "the third page's run is a good one");
                hosted.Host.Dispatcher.Invoke(() => hosted.Host.Batch.Within(() => pages[2].Adopt(good), context));
                StoreThrows(() => _ = hosted.Value, "page failure", "another page's run does not heal it");
                // The failed page has no result: the next commit over its tables runs it.
                await Rename("z");
                Equal("s6:z s5:old s5 s4:z s3:old s3 s2:z s1:y", hosted.Shown, "the failed page ran well");
            }),
            Case("host.tiling.switches-and-failures-a-failed-page-a-retired-pages-later-run-is-patched-into-stays-without-a-result-the-next-commit-runs-it", async () =>
            {
                // Code review round 2 (Astra): pages [s6 s5 s4 s3] and [s2 s1]. C1 removes four sessions from the team (the pages merge), C2
                // renames s1. The merged page then fails, and the second page's run, for its old interval, is adopted after it: the merged
                // page takes the rows, stays failed, and must not skip the next write as if it knew its rows.
                await using var hosted = await TilingHosted.Create(4, 6);
                await hosted.More();
                var tail = hosted.Pages()[1];
                await hosted.Team(remove: ["s6", "s5", "s4", "s2"]);
                await hosted.Write(rename: [("s1", "renamed")]);
                Equal("s3:old s3 s1:renamed", hosted.Shown, "the merged page");
                Equal(1, hosted.Pages().Count, "one page");
                TilingFail(hosted.Host, hosted.Pages()[0], "page failure");
                // The retired page's run, of its old interval, as a commit's step computed it before the adoption that merged the pages.
                var late = await hosted.Store.ReadAsync(executor => tail.FetchAsync(executor, context), context);
                hosted.Host.Dispatcher.Invoke(() => hosted.Host.Batch.Within(() => tail.Adopt(late), context));
                StoreThrows(() => _ = hosted.Value, "page failure", "the merged page stays failed");
                // A write that changes no row of the list: a page that knew its rows would ask one probe and skip.
                await hosted.Write(touch: [("s999", 999)]);
                Equal("s3:old s3 s1:renamed", hosted.Shown, "the next commit runs the failed page");
            }),
            Case("host.tiling.switches-and-failures-arguments-the-builder-refuses-fail-the-list-a-write-to-the-previous-arguments-rows-does-not-publish-them", async () =>
            {
                // Review round 1 (Sol 4, for lists).
                await using var hosted = await TilingHosted.Create(2, 4);
                await hosted.Show("broken");
                await Q7Settled(hosted.Host);
                StoreThrows(() => _ = hosted.Value, "no such team", "the list is failed");
                await hosted.Write(rename: [("s4", "edited")]);
                StoreThrows(() => _ = hosted.Value, "no such team", "a write to the previous arguments' rows does not publish them");
                await hosted.Show("t");
                await Q7Until(() => hosted.Shown == "s4:edited s3:old s3", "arguments that build again");
            }),
        ];
        for (uint seed = 1; seed <= 16; seed++)
        {
            var each = seed;
            cases.Add(Case($"host.tiling.the-invariant-with-two-owners-after-every-adoption-the-value-is-a-gapless-prefix-of-the-true-order-row-by-row-seed-{each}", async () =>
            {
                var first = 2 + (int)(each % 2);
                await using var hosted = await TilingHosted.Create(first, 9);
                var r = new Sync160Random(each);
                var gate = new object();
                var rows = new OrderedDictionary<string, (string Title, int Touched)>(StringComparer.Ordinal);
                for (var index = 1; index <= 9; index++) rows[$"s{index}"] = ($"old s{index}", index);
                // In insertion order, as upstream's Set.
                var team = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
                foreach (var id in rows.Keys) team[id] = true;
                var clock = 100;
                var made = 9;
                // The true order: the team's sessions that exist, newest touch first, ties by id (the key's order).
                List<string> Order()
                {
                    lock (gate)
                        return [.. team.Keys.Where(rows.ContainsKey).Select(id => (Id: id, Row: rows[id]))
                            .OrderByDescending(entry => entry.Row.Touched).ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
                            .Select(entry => $"{entry.Id}:{entry.Row.Title}:{entry.Row.Touched}")];
                }
                var step = "start";
                var checks = 0;
                void Verify()
                {
                    var value = hosted.Value;
                    List<string> shown = [.. ((JsonArray)value["rows"]!).Select(row => $"{(string)row!["id"]!}:{(string)row["title"]!}:{row["touched"]!.ToJsonString()}")];
                    var expected = Order();
                    Interlocked.Increment(ref checks);
                    Equal(string.Join(" ", expected.Take(shown.Count)), string.Join(" ", shown), step + ": a prefix");
                    if (!value["moreAfter"]!.GetValue<bool>()) Equal(expected.Count, shown.Count, step + ": the whole order");
                }
                // After every commit's adoption: the frame it published is checked against the truth as of that commit.
                hosted.Adopted = Verify;
                // A session to write: mostly one the list shows now (a stale page shows there), else any that exists.
                string Pick()
                {
                    var shownIds = hosted.Ids.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string[] all;
                    lock (gate) all = [.. rows.Keys];
                    var from = shownIds.Length > 0 && r.Chance(0.8) ? shownIds : all;
                    var at = r.Int(from.Length);
                    return at < from.Length ? from[at] : "s1";
                }
                // One owner's commit; what it wrote reaches the truth when its commit is adopted.
                Task SessionsWrite()
                {
                    var kind = r.Int(8);
                    var id = Pick();
                    if (kind < 2)
                    {
                        var touched = r.Chance(0.7) ? ++clock : r.Int(50);
                        hosted.Wrote(() => { lock (gate) if (rows.TryGetValue(id, out var row)) rows[id] = (row.Title, touched); });
                        return hosted.Write(touch: [(id, touched)]);
                    }
                    if (kind < 5)
                    {
                        var title = $"t{++clock}";
                        hosted.Wrote(() => { lock (gate) if (rows.TryGetValue(id, out var row)) rows[id] = (title, row.Touched); });
                        return hosted.Write(rename: [(id, title)]);
                    }
                    if (kind < 7)
                    {
                        hosted.Wrote(() => { lock (gate) rows.Remove(id); });
                        return hosted.Write(drop: [id]);
                    }
                    var fresh = new List<(string Id, string Title, int Touched)>();
                    for (var count = 1 + r.Int(4 * first); count > 0; count--)
                    {
                        var number = ++made;
                        fresh.Add(($"s{number}", $"new {number}", r.Chance(0.5) ? ++clock : r.Int(50)));
                    }
                    hosted.Wrote(() => { lock (gate) foreach (var row in fresh) rows[row.Id] = (row.Title, row.Touched); });
                    return hosted.Write(put: fresh);
                }
                Task ClubWrite()
                {
                    // Sessions leave the team (mostly shown ones: pages empty and merge) and others join, also many at once (a run cut by
                    // its limit) and ones that do not exist yet; often both in one commit.
                    var leaving = new List<string>();
                    if (r.Chance(0.6)) for (var count = 1 + r.Int(3); count > 0; count--) leaving.Add(Pick());
                    var joining = new List<string>();
                    if (leaving.Count == 0 || r.Chance(0.5)) for (var count = 1 + r.Int(4 * first); count > 0; count--) joining.Add($"s{1 + r.Int(made + 2)}");
                    hosted.Wrote(() =>
                    {
                        lock (gate)
                        {
                            foreach (var session in leaving) team.Remove(session);
                            foreach (var session in joining) team[session] = true;
                        }
                    });
                    // The transaction removes, then adds: a session in both stays in the team.
                    return hosted.Team(remove: leaving.Distinct(), add: joining.Distinct());
                }
                for (var index = 0; index < 400; index++)
                {
                    step = $"seed {each}, step {index}";
                    var kind = r.Int(10);
                    if (kind < 3)
                    {
                        // The filler's commit, and the two owners' commits queued behind it.
                        hosted.Wrote(() => { });
                        var fill = hosted.Fill(index);
                        var (a, b) = r.Chance(0.5) ? (SessionsWrite(), ClubWrite()) : (ClubWrite(), SessionsWrite());
                        await Task.WhenAll(fill, a, b);
                    }
                    else if (kind < 6) await SessionsWrite();
                    else if (kind < 9) await ClubWrite();
                    else await hosted.More();
                    await hosted.Flushed();
                    if (hosted.Failure is { } failure) throw new InvalidOperationException(step + ": after an adoption: " + failure.Message, failure);
                    Verify();
                }
                Check(checks > 600, $"checked after every adoption: {checks}");
                for (var page = 0; page < 200 && hosted.MoreAfter; page++) await hosted.More();
                hosted.Adopted = () => { };
                Verify();
                Check(!hosted.MoreAfter, "loaded to the end");
            }));
        }
        return cases;
    }
}
