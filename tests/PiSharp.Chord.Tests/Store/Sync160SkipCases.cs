using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/store/skip.test.ts. Every case is ported. The skip test's oracle
// (slice 14 §3.3, test 13a): in randomized runs of writes over own, joined and exists tables, with reads of every shape the slice's
// screens use, every read's adopted result equals a fresh run of its statement after every commit, whether the commit ran it again or
// skipped it. And the deterministic cases of the rule's table: what skips without a probe, what a probe decides, what always runs.
// Differences from upstream:
// - The generator is mulberry32, step for step (Sync160Random), and the writes draw from it in upstream's order (JavaScript evaluates an
//   object literal's values and a call's arguments left to right), so a seed writes the same rows here.
// - Upstream's oracle queues one to three commits together, which its store writes as one backend transaction (group commit, S18). The
//   native store has no group commit (it was not ported at 1.5.2), so the same commits run one transaction each. The oracle's assertion
//   is the same; its counters are bounded from below as upstream bounds them, and the bounds hold.
// - The deterministic cases assert upstream's exact { runs, probes, skips } triples; each commits alone, as upstream.
internal static partial class Program
{
    private static readonly ColumnSet Sync160SkipSessionColumns = Sync160Columns("sessions.columns",
        [("id", Str()), ("app", Str()), ("creator", Str()), ("private", Bool()), ("state", Str()), ("lastTouch", Int()), ("archivedAt", Opt(Int()))], ["id"]);

    private static readonly TableDefinition Sync160SkipSessions = Sync160Table("sessions", Sync160Spread(Sync160SkipSessionColumns, ("title", Str())), ["id"],
        Sync160SkipSessionColumns, ("by_app", ["app", "lastTouch"]), ("by_state", ["state", "lastTouch"]), ("by_creator", ["app", "creator", "lastTouch"]));

    private static readonly ColumnSet Sync160SkipTeamSessions = Sync160Columns("club.team-sessions", [("team", Str()), ("session", Str())], ["team", "session"],
        ("ofSession", ["session"]));

    private static readonly TableDefinition Sync160SkipTeams = Sync160Table("teams", Sync160Spread(Sync160SkipTeamSessions), ["team", "session"],
        Sync160SkipTeamSessions, ("of_session", ["session"]));

    private static readonly ColumnSet Sync160SkipShareColumns = Sync160Columns("people.shares", [("person", Str()), ("session", Str())], ["person", "session"],
        ("ofSession", ["session"]));

    private static readonly TableDefinition Sync160SkipShares = Sync160Table("shares", Sync160Spread(Sync160SkipShareColumns), ["person", "session"],
        Sync160SkipShareColumns, ("of_session", ["session"]));

    // A pin's session is no key column: an update or a replacing put re-points it.
    private static readonly ColumnSet Sync160SkipPinColumns = Sync160Columns("pins.columns", [("id", Int()), ("person", Str()), ("session", Str())], ["id"],
        ("ofSession", ["session"]));

    private static readonly TableDefinition Sync160SkipPins = Sync160Table("pins", Sync160Spread(Sync160SkipPinColumns, ("at", Int())), ["id"],
        Sync160SkipPinColumns, ("of_session", ["session"]));

    private static readonly TableDefinition Sync160SkipNotes = Sync160Table("notes",
        Sync160Schemas(("id", Int()), ("person", Str()), ("session", Opt(Str())), ("at", Int())), ["id"], null, ("of_person", ["person", "at"]));

    private static readonly string[] Sync160SkipSessionIds = [.. Enumerable.Range(0, 24).Select(index => "s" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture))];
    private static readonly string[] Sync160SkipPersons = ["anna", "ben", "cleo"];
    private static readonly string[] Sync160SkipTeamNames = ["u10", "u12", "u14"];
    private static readonly string[] Sync160SkipApps = ["club", "family"];
    private static readonly string[] Sync160SkipStates = ["working", "waiting", "idle"];

    private static IEnumerable<JsonNode?> Sync160Texts(IEnumerable<string> values) => values.Select(value => (JsonNode?)JsonValue.Create(value));

    /// <summary>What a person may see: the family's public sessions and their own, the club's by team, and what is shared (upstream
    /// <c>conditionOf</c>).</summary>
    private static WhereNode Sync160SkipCondition(string person, string[] teams)
    {
        var s = Sync160SkipSessionColumns.Refs;
        return Tables.Compile(Sync160SkipSessionColumns, Or(
            And(Eq(s["app"], "family"), Or(Eq(s["private"], false), Eq(s["creator"], person))),
            And(Eq(s["app"], "club"), Exists(Sync160SkipTeamSessions, Sync160Match(("session", s["id"])), t => InArray(t["team"], Sync160Texts(teams)))),
            And(InArray(s["app"], Sync160Texts(Sync160SkipApps)), Exists(Sync160SkipShareColumns, Sync160Match(("session", s["id"])), x => Eq(x["person"], person)))));
    }

    /// <summary>One of the reads of the slice's screens (§8): a query and how much of it is read.</summary>
    private sealed record Sync160SkipShape(string Name, Query Query, TableDefinition Table, ReadKind What);

    /// <summary>The reads of the slice's screens, by name (upstream <c>shapes</c>).</summary>
    private static List<Sync160SkipShape> Sync160SkipShapes()
    {
        var (sessions, teams, shares, pins, notes) = (Sync160SkipSessions, Sync160SkipTeams, Sync160SkipShares, Sync160SkipPins, Sync160SkipNotes);
        var (sessionColumns, shareColumns, pinColumns) = (Sync160SkipSessionColumns, Sync160SkipShareColumns, Sync160SkipPinColumns);
        var anna = Sync160SkipCondition("anna", ["u10", "u12"]);
        var ben = Sync160SkipCondition("ben", ["u14"]);
        static ReadKind First(long limit) => new ReadKind.Rows(new ReadRange(limit));
        Query Visible(string app, WhereNode condition) => Tables.Query(sessions)
            .Where(s => Eq(s["app"], app))
            .WhereNode(sessionColumns, condition)
            .OrderBy(s => s["lastTouch"], OrderDirection.Desc)
            .OrderBy(s => s["id"], OrderDirection.Desc);
        Query OfTeams(string[] names, WhereNode condition) => Tables.Query(teams)
            .Where(t => InArray(t["team"], Sync160Texts(names)))
            .Join(sessionColumns, t => Sync160Match(("id", t["session"])))
            .Where((_, session) => And(Eq(session["app"], "club"), IsNull(session["archivedAt"])))
            .WhereNode(sessionColumns, condition)
            .OrderBy((_, session) => session["lastTouch"], OrderDirection.Desc)
            .OrderBy((_, session) => session["id"], OrderDirection.Desc);
        var all = Tables.Query(sessions).OrderBy(s => s["lastTouch"], OrderDirection.Desc);
        return
        [
            new("visible club, anna", Visible("club", anna), sessions, First(4)),
            new("visible family, ben", Visible("family", ben), sessions, First(3)),
            new("one", Tables.Query(sessions).Where(s => Eq(s["id"], "s03")).WhereNode(sessionColumns, anna), sessions, First(1)),
            new("team page u12", OfTeams(["u12"], anna), teams, First(3)),
            new("club list, two teams", OfTeams(["u10", "u12"], anna), teams, First(5)),
            new("team count", OfTeams(["u14"], ben), teams, new ReadKind.Count()),
            new("shared with ben", Tables.Query(shares)
                .Where(x => Eq(x["person"], "ben"))
                .Join(sessionColumns, x => Sync160Match(("id", x["session"])))
                .WhereNode(sessionColumns, ben)
                .OrderBy((_, session) => session["lastTouch"], OrderDirection.Desc), shares, First(3)),
            new("created by anna", Tables.Query(sessions)
                .Where(s => And(Eq(s["app"], "club"), Eq(s["creator"], "anna")))
                .OrderBy(s => s["lastTouch"], OrderDirection.Desc), sessions, First(3)),
            new("active", Tables.Query(sessions)
                .Where(s => InArray(s["state"], Sync160Texts(["working", "waiting"])))
                .WhereNode(sessionColumns, anna)
                .OrderBy(s => s["state"], OrderDirection.Desc)
                .OrderBy(s => s["lastTouch"], OrderDirection.Desc), sessions, First(4)),
            new("pinned by anyone", Tables.Query(sessions)
                .Where(s => Exists(pinColumns, Sync160Match(("session", s["id"]))))
                .OrderBy(s => s["lastTouch"], OrderDirection.Desc), sessions, First(4)),
            new("pinned by nobody", Tables.Query(sessions)
                .Where(s => And(Eq(s["app"], "family"), Not(Exists(pinColumns, Sync160Match(("session", s["id"])))))), sessions, First(4)),
            new("shared with nobody", Tables.Query(sessions)
                .Where(s => And(Eq(s["app"], "family"), Not(Exists(shareColumns, Sync160Match(("session", s["id"]))))))
                .OrderBy(s => s["lastTouch"], OrderDirection.Desc), sessions, First(3)),
            new("anna's pins", Tables.Query(pins)
                .Where(p => Eq(p["person"], "anna"))
                .Join(sessionColumns, p => Sync160Match(("id", p["session"])))
                .WhereNode(sessionColumns, anna)
                .OrderBy(p => p["at"], OrderDirection.Desc), pins, First(3)),
            new("anna's notes", Tables.Query(notes)
                .Where(n => And(Eq(n["person"], "anna"), Or(IsNull(n["session"]), ExistsNode(sessionColumns, Sync160Match(("id", n["session"])), anna))))
                .OrderBy(n => n["at"], OrderDirection.Desc), notes, First(4)),
            new("a page", all, sessions, new ReadKind.Rows(new ReadRange(6, After: [5000d, "s20"], Through: [2000d, "s02"]))),
            new("an open page", all, sessions, new ReadKind.Rows(new ReadRange(5, After: [3000d, "s10"]))),
            new("all sessions count", Tables.Query(sessions).Where(s => Eq(s["app"], "club")), sessions, new ReadKind.Count()),
        ];
    }

    /// <summary>One open read of a world, with the run it adopted last.</summary>
    private sealed class Sync160SkipRead(string name, OpenQuery query)
    {
        internal string Name => name;

        internal OpenQuery Query => query;

        internal OpenRead Open { get; set; } = null!;

        internal volatile ReadRun? Latest;
    }

    /// <summary>The tables of one store, their sets bound, and the open reads (upstream <c>World</c>).</summary>
    private sealed class Sync160SkipWorld(ChordStore store, Dictionary<string, TableHandle> handles, Batch batch) : IAsyncDisposable
    {
        internal ChordStore Store => store;

        internal Dictionary<string, TableHandle> Handles => handles;

        internal List<Sync160SkipRead> Reads { get; } = [];

        internal Batch Batch => batch;

        public async ValueTask DisposeAsync()
        {
            foreach (var read in Reads) read.Query.Close();
            await store.DisposeAsync();
        }
    }

    private static async Task<Sync160SkipWorld> Sync160SkipOpened(ChordContext context, string[]? only = null)
    {
        var store = await MemoryStore();
        var handles = new Dictionary<string, TableHandle>(StringComparer.Ordinal);
        var owner = new object();
        foreach (var definition in new[] { Sync160SkipSessions, Sync160SkipTeams, Sync160SkipShares, Sync160SkipPins, Sync160SkipNotes })
        {
            var handle = await TableIn(store, definition);
            handles[definition.Name] = handle;
            if (definition.Exposes is { } exposes) store.ReadIndex.Bind(exposes.Name, new Bound(handle.Sql.Table, definition), owner);
        }
        var world = new Sync160SkipWorld(store, handles, Sync160Batch());
        foreach (var shape in Sync160SkipShapes())
        {
            if (only is not null && !only.Contains(shape.Name)) continue;
            var spec = new QuerySpec(shape.Query, handles[shape.Table.Name].Sql.Table, Projection.All, owner);
            var opened = new OpenQuery(new ReadSite(store, world.Batch), spec, shape.What is ReadKind.Count ? ReadShape.Count : ReadShape.Rows, () => { }, context);
            var entry = new Sync160SkipRead(shape.Name, opened);
            entry.Open = opened.Read(shape.What, new ReadSink(run => entry.Latest = run));
            world.Reads.Add(entry);
            await entry.Open.LoadAsync();
        }
        return world;
    }

    /// <summary>Stages <paramref name="write"/>'s table writes as one commit with the refresh a transaction stages, and completes once
    /// adopted (upstream <c>commit</c>).</summary>
    private static Task<long> Sync160SkipCommit(Sync160SkipWorld world, ChordContext context, Action<Func<TableDefinition, TableChanges>> write) =>
        Sync160Commit(world.Store, world.Batch, context, changes => write(table => changes(world.Handles[table.Name])));

    /// <summary>What a run shows: its rows and whether more follow, or its count (upstream <c>shown</c>).</summary>
    private static string Sync160SkipShown(ReadRun? run) => run switch
    {
        null => "no run",
        ReadRun.Rows rows => "{rows:[" + string.Join(",", rows.Result.Rows.Select(row =>
            "{" + string.Join(",", row.OrderBy(cell => cell.Key, StringComparer.Ordinal).Select(cell => cell.Key + ":" + StoreInternal.SqliteJson([cell.Value]))) + "}")) +
            "],more:" + (rows.Result.More ? "true" : "false") + "}",
        ReadRun.Count count => count.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ReadRun.Failed failed => "failed: " + failed.Error,
        _ => "?",
    };

    /// <summary>Compares every read's adopted run with a fresh run of its statement (upstream <c>expectFresh</c>).</summary>
    private static async Task Sync160SkipExpectFresh(Sync160SkipWorld world, ChordContext context, string step)
    {
        foreach (var read in world.Reads)
        {
            var fresh = await world.Store.ReadAsync(executor => read.Open.Read!.FetchAsync(executor, context), context);
            Equal(Sync160SkipShown(fresh), Sync160SkipShown(read.Latest), $"{step}: {read.Name}");
        }
    }

    /// <summary>One random write to one random table (upstream <c>randomWrite</c>; the draws in its order).</summary>
    private static void Sync160SkipRandomWrite(Sync160Random r, Func<TableDefinition, TableChanges> changes, int clock)
    {
        var (sessions, teams, shares, pins, notes) = (Sync160SkipSessions, Sync160SkipTeams, Sync160SkipShares, Sync160SkipPins, Sync160SkipNotes);
        var session = r.Pick(Sync160SkipSessionIds);
        var kind = r.Int(12);
        if (kind < 3)
        {
            // A touch: the write of the slice's mix.
            changes(sessions).Update(new JsonObject { ["id"] = session }, new JsonObject { ["lastTouch"] = clock, ["state"] = r.Pick(Sync160SkipStates) });
        }
        else if (kind == 3)
        {
            var row = new JsonObject { ["id"] = session };
            row["app"] = r.Pick(Sync160SkipApps);
            row["creator"] = r.Pick(Sync160SkipPersons);
            row["private"] = r.Chance(0.3);
            row["state"] = r.Pick(Sync160SkipStates);
            row["lastTouch"] = r.Chance(0.5) ? clock : r.Int(6000);
            row["title"] = $"t{clock}";
            if (r.Chance(0.2)) row["archivedAt"] = clock;
            changes(sessions).Put(row);
        }
        else if (kind == 4)
        {
            // The array literal's elements are evaluated in order, then one is picked.
            var app = r.Pick(Sync160SkipApps);
            var isPrivate = r.Chance(0.5);
            var archived = r.Chance(0.5);
            var creator = r.Pick(Sync160SkipPersons);
            var set = r.Pick<Dictionary<string, Operand>>([
                new() { ["title"] = $"renamed {clock}" },
                new() { ["app"] = app },
                new() { ["private"] = isPrivate },
                new() { ["archivedAt"] = archived ? (double)clock : Operand.Undefined },
                new() { ["creator"] = creator },
            ]);
            changes(sessions).Update(new JsonObject { ["id"] = session }, set);
        }
        else if (kind == 5)
        {
            if (r.Chance(0.3)) changes(sessions).Delete(new JsonObject { ["id"] = session });
            else changes(teams).Delete(new JsonObject { ["team"] = r.Pick(Sync160SkipTeamNames), ["session"] = session });
        }
        else if (kind == 6) changes(teams).Put(new JsonObject { ["team"] = r.Pick(Sync160SkipTeamNames), ["session"] = session });
        else if (kind == 7)
        {
            if (r.Chance(0.6)) changes(shares).Put(new JsonObject { ["person"] = r.Pick(Sync160SkipPersons), ["session"] = session });
            else changes(shares).Delete(new JsonObject { ["person"] = r.Pick(Sync160SkipPersons), ["session"] = session });
        }
        else if (kind == 8)
        {
            var id = r.Int(6);
            var choice = r.Int(4);
            if (choice == 0) changes(pins).Put(new JsonObject { ["id"] = id, ["person"] = r.Pick(Sync160SkipPersons), ["session"] = session, ["at"] = clock });
            else if (choice == 1) changes(pins).Update(new JsonObject { ["id"] = id }, new JsonObject { ["session"] = session });
            else if (choice == 2) changes(pins).Update(new JsonObject { ["id"] = id }, new JsonObject { ["at"] = clock });
            else changes(pins).Delete(new JsonObject { ["id"] = id });
        }
        else if (kind == 9)
        {
            var id = r.Int(8);
            if (r.Chance(0.7))
            {
                var row = new JsonObject { ["id"] = id, ["person"] = r.Pick(Sync160SkipPersons), ["at"] = clock };
                if (r.Chance(0.7)) row["session"] = session;
                changes(notes).Put(row);
            }
            else changes(notes).Delete(new JsonObject { ["id"] = id });
        }
        else if (kind == 10)
        {
            // The set is an argument, evaluated at the call; the condition's callback runs inside updateWhere, after it.
            JsonObject set = r.Chance(0.5) ? new() { ["title"] = "bulk" } : new() { ["state"] = r.Pick(Sync160SkipStates) };
            changes(sessions).UpdateWhere(s => Eq(s["app"], r.Pick(Sync160SkipApps)), set);
        }
        else changes(teams).DeleteWhere(t => Eq(t["team"], r.Pick(Sync160SkipTeamNames)));
    }

    /// <summary>What one commit cost the reads.</summary>
    private readonly record struct Sync160SkipCost(double Runs, double Probes, double Skips);

    /// <summary>Seeds a world with the listed reads; its <c>Cost</c> measures one commit's runs, probes and skips (upstream
    /// <c>measured</c>).</summary>
    private sealed class Sync160SkipMeasured(Sync160SkipWorld world, ChordContext context, Func<IReadOnlyDictionary<string, double>> counts) : IAsyncDisposable
    {
        internal Sync160SkipWorld World => world;

        internal async Task<Sync160SkipCost> Cost(Action<Func<TableDefinition, TableChanges>> write)
        {
            var before = counts();
            await Sync160SkipCommit(world, context, write);
            await Sync160SkipExpectFresh(world, ChordContext.Background, "after the write");
            var after = counts();
            double Of(string name) => after.GetValueOrDefault(name) - before.GetValueOrDefault(name);
            return new Sync160SkipCost(Of("chord.query.runs"), Of("chord.query.probes"), Of("chord.query.skips"));
        }

        public ValueTask DisposeAsync() => world.DisposeAsync();
    }

    private static async Task<Sync160SkipMeasured> Sync160SkipMeasure(string[] only, Action<Func<TableDefinition, TableChanges>> seed)
    {
        var (_, context, counts) = await ChordSupport.Metered();
        var world = await Sync160SkipOpened(context, only);
        await Sync160SkipCommit(world, context, seed);
        return new Sync160SkipMeasured(world, context, counts);
    }

    /// <summary>A session row (upstream <c>session</c>).</summary>
    private static JsonObject Sync160SkipSession(string id, string app = "club", string creator = "anna", int lastTouch = 100, string state = "idle") => new()
    {
        ["id"] = id, ["app"] = app, ["creator"] = creator, ["private"] = false, ["state"] = state, ["lastTouch"] = lastTouch, ["title"] = id,
    };

    private static JsonObject Sync160SkipKey(string id) => new() { ["id"] = id };

    private static IEnumerable<(string, Func<Task>)> StoreSync160SkipCases()
    {
        var (sessions, teams, shares, pins) = (Sync160SkipSessions, Sync160SkipTeams, Sync160SkipShares, Sync160SkipPins);
        var cases = new List<(string, Func<Task>)>();
        foreach (var seed in new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            cases.Add(Case($"store.skip.the-oracle-a-skipped-read-equals-a-fresh-run-randomized-writes-single-and-queued-commits-seed-{seed}", async () =>
            {
                var (_, context, counts) = await ChordSupport.Metered();
                await using var world = await Sync160SkipOpened(context);
                var r = new Sync160Random(seed);
                var clock = 6000;
                // Seed data, so reads have rows from the start.
                await Sync160SkipCommit(world, context, changes =>
                {
                    for (var index = 0; index < 60; index++) Sync160SkipRandomWrite(r, changes, clock++);
                });
                await Sync160SkipExpectFresh(world, context, "seeded");
                for (var step = 0; step < 150; step++)
                {
                    // One commit of one to three writes, or several commits queued together.
                    var commits = r.Chance(0.25) ? 1 + r.Int(3) : 1;
                    var pending = new List<Task<long>>();
                    for (var index = 0; index < commits; index++)
                    {
                        pending.Add(Sync160SkipCommit(world, context, changes =>
                        {
                            for (var write = 1 + r.Int(2); write > 0; write--) Sync160SkipRandomWrite(r, changes, clock++);
                        }));
                    }
                    await Task.WhenAll(pending);
                    await Sync160SkipExpectFresh(world, context, $"seed {seed}, step {step}");
                }
                var total = counts();
                // The run exercised all three outcomes.
                Check(total.GetValueOrDefault("chord.query.skips") > 200, $"skips: {total.GetValueOrDefault("chord.query.skips")}");
                Check(total.GetValueOrDefault("chord.query.probes") > 100, $"probes: {total.GetValueOrDefault("chord.query.probes")}");
                Check(total.GetValueOrDefault("chord.query.runs") > 100, $"runs: {total.GetValueOrDefault("chord.query.runs")}");
            }));
        }
        cases.AddRange(
        [
            Case("store.skip.own-table-an-update-of-a-column-the-read-does-not-depend-on-skips-without-a-probe-a-touch-of-another-row-probes", async () =>
            {
                await using var measured = await Sync160SkipMeasure(["created by anna"], changes =>
                {
                    var ids = new[] { "s01", "s02", "s03", "s04", "s05" };
                    for (var index = 0; index < ids.Length; index++) changes(sessions).Put(Sync160SkipSession(ids[index], lastTouch: 100 + index));
                    changes(sessions).Put(Sync160SkipSession("s06", creator: "ben", lastTouch: 50));
                });
                // The window holds s05, s04, s03 and, past its bound, s02.
                Equal(new Sync160SkipCost(0, 0, 1), await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s01"), new JsonObject { ["title"] = "renamed" })),
                    "a column the read does not depend on... of a row outside the window");
                // A row in the window: its mapped column changed.
                Equal(new Sync160SkipCost(1, 0, 0), await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s04"), new JsonObject { ["title"] = "renamed" })),
                    "a row in the window");
                // Ben's session touched: an order column, so it might enter; the probe says it does not.
                Equal(new Sync160SkipCost(0, 1, 1), await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s06"), new JsonObject { ["lastTouch"] = 999 })),
                    "another creator's session touched");
                // A row past the window touched to the top: the probe finds it.
                Equal(new Sync160SkipCost(1, 1, 0), await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s01"), new JsonObject { ["lastTouch"] = 1000 })),
                    "a row past the window touched to the top");
                // A put behind the row past the bound: a probe miss.
                Equal(new Sync160SkipCost(0, 1, 1), await measured.Cost(changes => changes(sessions).Put(Sync160SkipSession("s07", lastTouch: 1))), "a put behind the bound");
                // A delete of a row that was not fetched needs no probe; of the row past the bound it runs.
                Equal(new Sync160SkipCost(0, 0, 1), await measured.Cost(changes => changes(sessions).Delete(Sync160SkipKey("s07"))), "a delete of a row not fetched");
                Equal(new Sync160SkipCost(1, 0, 0), await measured.Cost(changes => changes(sessions).Delete(Sync160SkipKey("s03"))), "a delete of a fetched row");
            }),
            Case("store.skip.own-table-a-bulk-update-skips-by-the-columns-it-assigns-a-bulk-delete-runs-a-count-skips-only-by-its-condition-columns", async () =>
            {
                await using var measured = await Sync160SkipMeasure(["created by anna", "all sessions count"], changes => changes(sessions).Put(Sync160SkipSession("s01")));
                // The list maps every column, the count depends on `app` alone.
                Equal(new Sync160SkipCost(1, 0, 1), await measured.Cost(changes => changes(sessions).UpdateWhere(s => Eq(s["app"], "club"), new JsonObject { ["private"] = true })),
                    "a bulk update of a mapped column");
                // A touch: the list's order column, no condition column of the count.
                Equal(new Sync160SkipCost(1, 0, 1), await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s01"), new JsonObject { ["lastTouch"] = 7 })), "a touch");
                // A put tells nothing: the count keeps no keys and runs.
                Equal(1d, (await measured.Cost(changes => changes(sessions).Put(Sync160SkipSession("s09", app: "family")))).Runs, "a put runs the count");
                Equal(new Sync160SkipCost(2, 0, 0), await measured.Cost(changes => changes(sessions).DeleteWhere(s => Eq(s["id"], "s09"))), "a bulk delete runs both");
            }),
            Case("store.skip.a-joined-table-the-team-page-of-another-team-is-skipped-by-one-probe-the-sessions-team-runs", async () =>
            {
                await using var measured = await Sync160SkipMeasure(["team page u12", "club list, two teams"], changes =>
                {
                    var ids = new[] { "s01", "s02", "s03" };
                    for (var index = 0; index < ids.Length; index++)
                    {
                        changes(sessions).Put(Sync160SkipSession(ids[index], lastTouch: 100 + index));
                        changes(teams).Put(new JsonObject { ["team"] = index == 2 ? "u14" : "u12", ["session"] = ids[index] });
                    }
                    changes(sessions).Put(Sync160SkipSession("s04", lastTouch: 90));
                });
                // s03 is in u14 only: both reads ask their probe, and neither runs.
                Equal(new Sync160SkipCost(0, 2, 2),
                    await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s03"), new JsonObject { ["lastTouch"] = 999, ["state"] = "working" })), "a session of another team");
                // s01 is on both: its partner key is among their rows.
                Equal(new Sync160SkipCost(2, 0, 0), await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s01"), new JsonObject { ["lastTouch"] = 1000 })),
                    "a session on both pages");
                // A partner column outside condition, order and the mapped ones: nothing to ask. Here every set column is mapped.
                Equal(new Sync160SkipCost(0, 0, 2), await measured.Cost(changes => changes(sessions).Update(Sync160SkipKey("s03"), new JsonObject { ["title"] = "x" })),
                    "a column outside the set");
                // A partner that is no row's partner, deleted: it cannot enter.
                Equal(new Sync160SkipCost(0, 0, 2), await measured.Cost(changes => changes(sessions).Delete(Sync160SkipKey("s03"))), "a partner of no row deleted");
                // A team row is a write of the own table, and of the table the person's condition tests exists in. s04 joins u10: the u12
                // page asks both probes and skips, the two-team list's first probe finds the row.
                Equal(new Sync160SkipCost(1, 3, 1), await measured.Cost(changes => changes(teams).Put(new JsonObject { ["team"] = "u10", ["session"] = "s04" })), "a team row");
                // s02 is on both pages: another team row of it may change its exists, so both run.
                Equal(2d, (await measured.Cost(changes => changes(teams).Put(new JsonObject { ["team"] = "u10", ["session"] = "s02" }))).Runs, "a team row of a session on both pages");
            }),
            Case("store.skip.an-exists-table-a-pin-moved-from-one-session-to-another-runs-the-reads-of-both-also-under-not-exists", async () =>
            {
                await using var measured = await Sync160SkipMeasure(["pinned by anyone", "pinned by nobody"], changes =>
                {
                    foreach (var id in new[] { "s01", "s02", "s03" }) changes(sessions).Put(Sync160SkipSession(id, app: "family"));
                    changes(pins).Put(new JsonObject { ["id"] = 1, ["person"] = "anna", ["session"] = "s01", ["at"] = 1 });
                });
                string Pinned() => string.Join(",", ((ReadRun.Rows)measured.World.Reads[0].Latest!).Result.Rows.Select(row => (string)row["id"]!));
                Equal("s01", Pinned(), "the pinned session");
                // The matched column is no key column: what the pin paired with before is unknown, so both reads run.
                Equal(new Sync160SkipCost(2, 0, 0), await measured.Cost(changes => changes(pins).Update(new JsonObject { ["id"] = 1 }, new JsonObject { ["session"] = "s02" })),
                    "a pin re-pointed by an update");
                Equal("s02", Pinned(), "the pin moved");
                Equal(new Sync160SkipCost(2, 0, 0),
                    await measured.Cost(changes => changes(pins).Put(new JsonObject { ["id"] = 1, ["person"] = "anna", ["session"] = "s03", ["at"] = 2 })), "a pin re-pointed by a put");
                Equal("s03", Pinned(), "the pin moved again");
                // A column outside the match and the inner condition: nothing to ask.
                Equal(new Sync160SkipCost(0, 0, 2), await measured.Cost(changes => changes(pins).Update(new JsonObject { ["id"] = 1 }, new JsonObject { ["at"] = 3 })),
                    "a column outside the match");
            }),
            Case("store.skip.an-exists-table-a-share-of-a-session-of-another-app-is-skipped-by-one-probe-of-a-row-in-the-read-it-runs", async () =>
            {
                await using var measured = await Sync160SkipMeasure(["visible club, anna", "one"], changes =>
                {
                    changes(sessions).Put(Sync160SkipSession("s01", app: "family", creator: "ben"));
                    changes(sessions).Put(Sync160SkipSession("s03"));
                    changes(teams).Put(new JsonObject { ["team"] = "u10", ["session"] = "s03" });
                });
                // s01 is the family's: the club list's probe misses, the read of s03 asks its own and misses too.
                Equal(new Sync160SkipCost(0, 2, 2), await measured.Cost(changes => changes(shares).Put(new JsonObject { ["person"] = "anna", ["session"] = "s01" })),
                    "a share of another app's session");
                // s03 is in both reads: a share of it may change them.
                Equal(new Sync160SkipCost(2, 0, 0), await measured.Cost(changes => changes(shares).Put(new JsonObject { ["person"] = "ben", ["session"] = "s03" })),
                    "a share of a row in the reads");
            }),
            Case("store.skip.cost-a-transaction-writing-many-rows-asks-a-read-a-few-probes-then-runs-it", async () =>
            {
                // Fable S5: a bulk of puts must not probe one read once per row.
                await using var measured = await Sync160SkipMeasure(["created by anna"], changes => changes(sessions).Put(Sync160SkipSession("s01")));
                var bulk = await measured.Cost(changes =>
                {
                    for (var index = 0; index < 100; index++) changes(sessions).Put(Sync160SkipSession($"b{index}", creator: "ben"));
                });
                Equal(new Sync160SkipCost(1, 8, 0), bulk, "eight probes, then a run");
            }),
            Case("store.skip.cost-builds-no-change-of-a-transaction-whose-tables-nothing-reads", async () =>
            {
                // Fable S4, Opus: a store without reads pays its writes nothing for them.
                var (_, context, _) = await ChordSupport.Metered();
                await using var world = await Sync160SkipOpened(context, []);
                var built = 0;
                var staged = new List<IStagedWrite>();
                var changes = new TableChanges(world.Handles[sessions.Name], staged);
                changes.Put(Sync160SkipSession("s01"));
                var refresh = new ReadRefresh(world.Store.ReadIndex, [changes.Handle.Sql.Table], () =>
                {
                    built++;
                    return changes.Changes();
                }, context);
                staged.Add(refresh.Write);
                await world.Store.CommitAsync(staged, new CommitFence(() => true, (_, _) => refresh.Adopt()), context);
                Equal(0, built, "no change was built");
            }),
        ]);
        return cases;
    }
}
