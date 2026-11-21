using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/store/read-plan.test.ts. Every case is ported. Read plans (slice
// 14 §3.3, §5, §6): exists, existsNode, the one-to-one join and conditions as data, compiled against the store's bindings and run on
// SQLite; what a plan refuses before any SQL; the uses it lists for the skip test; and the query plans: a joined read is driven from its
// own table, a probe is a keyed lookup. Differences from upstream:
// - A condition as data is the JSON of a compiled WhereNode; the cases hand the JSON text where upstream writes an object literal, and
//   the node where upstream passes compile's result.
// - The three cases that read EXPLAIN QUERY PLAN assert the text SQLite prints. The text depends on the SQLite that Microsoft.Data.Sqlite
//   bundles; it is the same as node:sqlite's for these statements (they ran here), so the patterns are upstream's.
// - Each case opens and closes its own store (upstream's afterEach closes the last one).
internal static partial class Program
{
    private static readonly ColumnSet Sync160PlanSessionColumns = Sync160Columns("sessions.columns",
        [("id", Str()), ("app", Str()), ("private", Bool()), ("lastTouch", Int()), ("archivedAt", Opt(Int()))], ["id"]);

    private static readonly TableDefinition Sync160PlanSessions = Sync160Table("sessions", Sync160Spread(Sync160PlanSessionColumns, ("title", Str())), ["id"],
        Sync160PlanSessionColumns, ("by_app", ["app", "lastTouch"]));

    private static readonly ColumnSet Sync160PlanTeamSessions = Sync160Columns("club.team-sessions", [("team", Str()), ("session", Str())], ["team", "session"],
        ("ofSession", ["session"]));

    private static readonly TableDefinition Sync160PlanTeams = Sync160Table("teams", Sync160Spread(Sync160PlanTeamSessions, ("note", Opt(Str()))), ["team", "session"],
        Sync160PlanTeamSessions, ("of_session", ["session"]));

    private static readonly TableDefinition Sync160PlanNotes = Sync160Table("notes", Sync160Schemas(("id", Int()), ("session", Opt(Str())), ("n", Int())), ["id"]);

    /// <summary>A store with the three tables, their sets bound, and some rows (upstream <c>seeded</c>).</summary>
    private static async Task<(ChordStore Store, Dictionary<TableDefinition, string> Tables)> Sync160PlanSeeded()
    {
        var store = await MemoryStore();
        var tables = new Dictionary<TableDefinition, string>(ReferenceEqualityComparer.Instance);
        var staged = new List<IStagedWrite>();
        foreach (var definition in new[] { Sync160PlanSessions, Sync160PlanTeams, Sync160PlanNotes })
        {
            var handle = await TableIn(store, definition);
            tables[definition] = handle.Sql.Table;
            if (definition.Exposes is { } exposes) store.ReadIndex.Bind(exposes.Name, new Bound(handle.Sql.Table, definition), tables);
            var changes = new TableChanges(handle, staged);
            if (ReferenceEquals(definition, Sync160PlanSessions))
            {
                var apps = new[] { "club", "club", "family", "club" };
                for (var index = 0; index < apps.Length; index++)
                    changes.Put(new JsonObject { ["id"] = $"s{index}", ["app"] = apps[index], ["private"] = index == 2, ["lastTouch"] = 10 * index, ["title"] = $"t{index}" });
            }
            else if (ReferenceEquals(definition, Sync160PlanTeams))
            {
                foreach (var (team, session) in new[] { ("u12", "s0"), ("u12", "s1"), ("u14", "s1"), ("u12", "gone") })
                    changes.Put(new JsonObject { ["team"] = team, ["session"] = session });
            }
            else
            {
                changes.Put(new JsonObject { ["id"] = 1, ["session"] = "s0", ["n"] = 1 });
                changes.Put(new JsonObject { ["id"] = 2, ["n"] = 2 });
                changes.Put(new JsonObject { ["id"] = 3, ["session"] = "s2", ["n"] = 3 });
            }
        }
        await store.CommitAsync(staged, CommitFence.Open, ChordContext.Background);
        return (store, tables);
    }

    /// <summary>The rows of <paramref name="query"/>'s plan over the seeded store for <paramref name="range"/> (upstream <c>planned</c>).</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Sync160Planned(Query query, ReadRange? range = null)
    {
        var (store, tables) = await Sync160PlanSeeded();
        await using (store)
        {
            var plan = new ReadPlan(query, tables[query.Table], Projection.All, ReadShape.Rows, store.ReadIndex);
            var statement = plan.Select(range ?? new ReadRange(10));
            return await store.ReadAsync(executor => executor.AllAsync(statement.Sql, [.. statement.Params]), ChordContext.Background);
        }
    }

    /// <summary>One column of each row, as JSON text.</summary>
    private static IEnumerable<string> Sync160Column(IEnumerable<IReadOnlyDictionary<string, object?>> rows, params string[] columns) =>
        rows.Select(row => Sync160Stored(row, columns));

    /// <summary>upstream's <c>refused(node)</c>: what checkNode says of a node over <paramref name="side"/>.</summary>
    private static string Sync160Refused(string node, ColumnSet side)
    {
        try
        {
            StoreInternal.CheckNode(JsonNode.Parse(node), side, "whereNode");
            return "accepted";
        }
        catch (ChordError error) when (error.Reason == "args")
        {
            return error.Message;
        }
    }

    private static void Sync160Contains(string expected, string actual, string what) =>
        Check(actual.Contains(expected, StringComparison.Ordinal), $"{what}: expected a message containing <{expected}>, actual <{actual}>.");

    private static IEnumerable<(string, Func<Task>)> StoreSync160ReadPlanCases()
    {
        var sessions = Sync160PlanSessions;
        var teams = Sync160PlanTeams;
        var notes = Sync160PlanNotes;
        var sessionColumns = Sync160PlanSessionColumns;
        var teamSessions = Sync160PlanTeamSessions;
        var clubOnly = Tables.Compile(sessionColumns, Eq(sessionColumns.Refs["app"], "club"));
        var page = Tables.Query(teams)
            .Where(t => Eq(t["team"], "u12"))
            .Join(sessionColumns, t => Sync160Match(("id", t["session"])))
            .Where((_, session) => Eq(session["app"], "club"))
            .OrderBy((_, session) => session["lastTouch"], OrderDirection.Desc);
        var own = Sync160Table("own", Sync160Schemas(("id", Int()), ("partner", Str())), ["id"]);
        return
        [
            Case("store.read-plan.exists-is-true-for-a-row-with-a-matching-row-in-the-sets-table-with-and-without-an-inner-condition", async () =>
            {
                var all = await Sync160Planned(Tables.Query(sessions).Where(s => Exists(teamSessions, Sync160Match(("session", s["id"])))));
                Sequence(["[\"s0\"]", "[\"s1\"]"], Sync160Column(all, "id"), "every session with a team");
                var u14 = await Sync160Planned(Tables.Query(sessions).Where(s => Exists(teamSessions, Sync160Match(("session", s["id"])), t => Eq(t["team"], "u14"))));
                Sequence(["[\"s1\"]"], Sync160Column(u14, "id"), "the sessions of u14");
            }),
            Case("store.read-plan.exists-negates-and-nests", async () =>
            {
                var none = await Sync160Planned(Tables.Query(sessions).Where(s => And(Eq(s["app"], "club"), Not(Exists(teamSessions, Sync160Match(("session", s["id"])))))));
                Sequence(["[\"s3\"]"], Sync160Column(none, "id"), "the club sessions without a team");
                // Teams whose session is a club session that is itself in u14.
                var nested = await Sync160Planned(Tables.Query(teams).Where(t => Exists(sessionColumns, Sync160Match(("id", t["session"])), s =>
                    And(Eq(s["app"], "club"), Exists(teamSessions, Sync160Match(("session", s["id"])), x => Eq(x["team"], "u14"))))));
                Sequence(["[\"u12\",\"s1\"]", "[\"u14\",\"s1\"]"], Sync160Column(nested, "team", "session"), "the nested exists");
            }),
            Case("store.read-plan.exists-matches-by-the-key-a-prefix-of-it-or-a-declared-index-anything-else-is-refused-naming-the-columns", () =>
            {
                Accepts(() => Tables.Query(sessions).Where(s => Exists(teamSessions, Sync160Match(("team", s["app"]), ("session", s["id"])))), "the whole key");
                Accepts(() => Tables.Query(sessions).Where(s => Exists(teamSessions, Sync160Match(("team", s["app"])))), "a prefix of the key");
                var loose = Sync160Columns("loose", [("a", Str()), ("b", Str())], ["a"]);
                StoreArgs(() => Tables.Query(sessions).Where(s => Exists(loose, Sync160Match(("b", s["id"])))), "loose is matched by (b), no leading prefix of its key (a)");
                var counted = Sync160Columns("counted", [("n", Int())], ["n"]);
                StoreArgs(() => Tables.Query(sessions).Where(s => Exists(counted, Sync160Match(("n", s["id"])))), "differ in type");
            }),
            Case("store.read-plan.exists-is-refused-in-writes", async () =>
            {
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    var handle = await TableIn(store, sessions);
                    var changes = new TableChanges(handle, []);
                    Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where = s => Exists(teamSessions, Sync160Match(("session", s["id"])));
                    StoreArgs(() => changes.UpdateWhere(where, new JsonObject { ["title"] = "x" }), "exists is for queries, not for writes");
                    StoreArgs(() => changes.DeleteWhere(where), "exists is for queries, not for writes");
                    Equal(3, tables.Count, "the seeded tables");
                }
            }),
            Case("store.read-plan.exists-matches-nothing-for-a-null-in-a-matched-column-the-exists-is-false", async () =>
            {
                var noted = await Sync160Planned(Tables.Query(notes).Where(n => Exists(sessionColumns, Sync160Match(("id", n["session"])))));
                Sequence(["[1]", "[3]"], Sync160Column(noted, "id"), "the notes with a session");
            }),
            Case("store.read-plan.exists-node-and-where-node-take-a-condition-as-data-an-optional-pointer-keeps-its-row-a-pointer-to-a-hidden-row-drops-it", async () =>
            {
                var feed = await Sync160Planned(Tables.Query(notes).Where(n => Or(IsNull(n["session"]), ExistsNode(sessionColumns, Sync160Match(("id", n["session"])), clubOnly))));
                Sequence(["[1]", "[2]"], Sync160Column(feed, "id"), "the feed");
                var ownRows = await Sync160Planned(Tables.Query(sessions).WhereNode(sessionColumns, clubOnly));
                Sequence(["[\"s0\"]", "[\"s1\"]", "[\"s3\"]"], Sync160Column(ownRows, "id"), "the club's sessions");
                // The same condition as it travels: its JSON.
                var travelled = await Sync160Planned(Tables.Query(sessions).WhereNode(sessionColumns, JsonNode.Parse(clubOnly.ToString())));
                Sequence(["[\"s0\"]", "[\"s1\"]", "[\"s3\"]"], Sync160Column(travelled, "id"), "the club's sessions, from JSON");
            }),
            Case("store.read-plan.exists-node-and-where-node-apply-to-a-joined-set-with-an-exists-matched-on-the-partner", async () =>
            {
                var inU14 = Tables.Compile(sessionColumns, Exists(teamSessions, Sync160Match(("session", sessionColumns.Refs["id"])), t => Eq(t["team"], "u14")));
                var joined = await Sync160Planned(Tables.Query(teams)
                    .Where(t => Eq(t["team"], "u12"))
                    .Join(sessionColumns, t => Sync160Match(("id", t["session"])))
                    .WhereNode(sessionColumns, inU14));
                Sequence(["[\"s1\"]"], Sync160Column(joined, "session"), "the u12 rows whose session is in u14");
            }),
            Case("store.read-plan.exists-node-and-where-node-refuse-a-malformed-node-before-anything-is-rendered", () =>
            {
                string Refused(string node) => Sync160Refused(node, sessionColumns);
                Sync160Contains("unknown kind \"like\"", Refused("""{"like":["app","x"]}"""), "an unknown kind");
                Sync160Contains("one kind", Refused("""{"eq":["app","x"],"ne":["app","y"]}"""), "two kinds");
                Sync160Contains("5 is not a str value", Refused("""{"eq":["app",5]}"""), "an operand of another type");
                Sync160Contains("no column \"nope\"", Refused("""{"eq":["nope","x"]}"""), "an unknown column");
                Sync160Contains("matched pairs", Refused("""{"exists":["club.team-sessions",[],null]}"""), "an exists without pairs");
                Sync160Contains("no column \"nope\"", Refused("""{"exists":["club.team-sessions",[["session","nope"]],null]}"""), "an exists matched to an unknown column");
                var deep = """{"eq":["app","x"]}""";
                for (var depth = 0; depth < 20; depth++) deep = "{\"not\":" + deep + "}";
                Sync160Contains("nested deeper", Refused(deep), "too deep");
                Throws<ChordError>(() => Tables.Query(sessions).WhereNode(teamSessions, clubOnly), "a node over a set the query has no side of");
            }),
            Case("store.read-plan.exists-node-and-where-node-check-a-nested-exists-against-the-table-bound-to-its-set-when-the-read-opens", async () =>
            {
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    ReadPlan Plan(string node) =>
                        new(Tables.Query(sessions).WhereNode(sessionColumns, JsonNode.Parse(node)), tables[sessions], Projection.All, ReadShape.Rows, store.ReadIndex);
                    StoreThrows(() => Plan("""{"exists":["club.team-sessions",[["team","id"]],{"eq":["nope","x"]}]}"""), "no column \"nope\"");
                    StoreThrows(() => Plan("""{"exists":["club.team-sessions",[["note","id"]],null]}"""), "no leading prefix");
                    StoreThrows(() => Plan("""{"exists":["nobody.serves",[["x","id"]],null]}"""), "column set \"nobody.serves\" is not served");
                }
            }),
            Case("store.read-plan.join-pairs-each-row-with-the-one-row-of-that-key-drops-a-row-without-a-partner-orders-by-a-partner-column", async () =>
            {
                var rows = await Sync160Planned(page);
                Sequence(["[\"s1\",10]", "[\"s0\",0]"], Sync160Column(rows, "session", "sessions.columns:lastTouch"), "the joined rows");
            }),
            Case("store.read-plan.join-continues-after-a-position-of-a-joined-order-and-ends-through-one", async () =>
            {
                var after = await Sync160Planned(page, new ReadRange(10, After: [10d, "u12", "s1"]));
                Sequence(["[\"s0\"]"], Sync160Column(after, "session"), "after the position");
                var through = await Sync160Planned(page, new ReadRange(10, Through: [10d, "u12", "s1"]));
                Sequence(["[\"s1\"]"], Sync160Column(through, "session"), "through the position");
                // A position of the leading order columns alone bounds every row equal in them.
                var prefix = await Sync160Planned(page, new ReadRange(10, Through: [10d]));
                Sequence(["[\"s1\"]"], Sync160Column(prefix, "session"), "through a leading part");
            }),
            Case("store.read-plan.join-refuses-a-match-short-of-the-whole-key-a-second-join-the-tables-own-set-and-a-column-of-another-type", () =>
            {
                StoreArgs(() => Tables.Query(sessions).Join(teamSessions, s => Sync160Match(("session", s["id"]))), "the match covers the whole key");
                StoreArgs(() => page.Join(teamSessions, t => Sync160Match(("team", t["team"]), ("session", t["session"]))), "joins one set");
                StoreArgs(() => Tables.Query(teams).Join(teamSessions, t => Sync160Match(("team", t["team"]), ("session", t["session"]))), "the set is the table's own");
                StoreArgs(() => Tables.Query(notes).Join(sessionColumns, n => Sync160Match(("id", n["n"]))), "differ in type");
            }),
            Case("store.read-plan.join-is-driven-from-the-querys-own-table-whatever-the-planner-would-prefer", async () =>
            {
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    var plan = new ReadPlan(page, tables[teams], Projection.All, ReadShape.Rows, store.ReadIndex);
                    var explained = await Sync160Explained(store, plan.Select(new ReadRange(51)));
                    Check(Regex.IsMatch(explained, "^SEARCH t .*; SEARCH p USING PRIMARY KEY"), "the own table drives the join: " + explained);
                }
            }),
            Case("store.read-plan.join-fails-a-read-whose-partner-column-is-not-what-it-was-written-for", async () =>
            {
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    var other = Sync160Columns("sessions.columns", [("id", Str()), ("app", Int())], ["id"]);
                    var q = Tables.Query(teams).Join(other, t => Sync160Match(("id", t["session"]))).Where((_, session) => Eq(session["app"], 1));
                    StoreArgs(() => new ReadPlan(q, tables[teams], Projection.All, ReadShape.Rows, store.ReadIndex),
                        "column \"app\" of column set \"sessions.columns\" is not what this read was written for");
                }
            }),
            Case("store.read-plan.uses-lists-each-table-a-read-uses-with-the-columns-it-depends-on", async () =>
            {
                var q = Tables.Query(teams)
                    .Where(t => InArray(t["team"], [(JsonNode?)"u12"]))
                    .Join(sessionColumns, t => Sync160Match(("id", t["session"])))
                    .Where((_, session) => And(Eq(session["app"], "club"), IsNull(session["archivedAt"])))
                    .OrderBy((_, session) => session["lastTouch"], OrderDirection.Desc);
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    var plan = new ReadPlan(q, tables[teams], new Projection([], ["id"]), ReadShape.Rows, store.ReadIndex);
                    var (ownUse, joined) = (plan.Uses[0], plan.Uses[1]);
                    Equal(UseRole.Own, ownUse.Role, "the first use");
                    Sequence(["session", "team"], ownUse.Depends.Order(StringComparer.Ordinal), "what the own use depends on");
                    Equal(UseRole.Joined, joined.Role, "the second use");
                    Sequence(["app", "archivedAt", "id", "lastTouch"], joined.Depends.Order(StringComparer.Ordinal), "what the joined use depends on");
                    Sequence(["app", "archivedAt", "lastTouch"], joined.Enters.Order(StringComparer.Ordinal), "what brings a partner in");
                    // The match column after the column the query fixes: the key (team, session).
                    Sequence(["session"], joined.Pairs!, "the own columns the partner pairs by");
                    Check(joined.Keyed, "the pairing is keyed");
                    var counted = new ReadPlan(q, tables[teams], Projection.All, ReadShape.Count, store.ReadIndex);
                    Sequence(["app", "archivedAt", "id"], counted.Uses[1].Depends.Order(StringComparer.Ordinal), "what a count depends on");
                }
            }),
            Case("store.read-plan.the-probe-is-one-keyed-lookup-by-the-own-key-by-a-joins-match-by-an-exists-match", async () =>
            {
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    var team = Tables.Query(teams)
                        .Where(t => Eq(t["team"], "u12"))
                        .Join(sessionColumns, t => Sync160Match(("id", t["session"])))
                        .OrderBy((_, session) => session["lastTouch"], OrderDirection.Desc);
                    var teamPlan = new ReadPlan(team, tables[teams], Projection.All, ReadShape.Rows, store.ReadIndex);
                    var byMatch = await Sync160Explained(store, teamPlan.Probe(new ReadRange(1), ["session"], ["s1"]));
                    Check(Regex.IsMatch(byMatch, @"^SEARCH t USING PRIMARY KEY \(team=\? AND session=\?\)"), "a join's probe is keyed: " + byMatch);
                    var visible = Tables.Query(sessions)
                        .Where(s => And(Eq(s["app"], "club"), Exists(teamSessions, Sync160Match(("session", s["id"])), t => Eq(t["team"], "u12"))))
                        .OrderBy(s => s["lastTouch"], OrderDirection.Desc);
                    var visiblePlan = new ReadPlan(visible, tables[sessions], Projection.All, ReadShape.Rows, store.ReadIndex);
                    var byKey = await Sync160Explained(store, visiblePlan.Probe(new ReadRange(1), ["id"], ["s1"]));
                    Check(Regex.IsMatch(byKey, @"^SEARCH t USING PRIMARY KEY \(id=\?\)"), "an exists' probe is keyed: " + byKey);
                    var use = visiblePlan.Uses.First(each => each.Role == UseRole.Exists);
                    Sequence(["session"], use.PairedBy, "the exists' matched columns");
                    Sequence(["id"], use.Pairs!, "the own columns it pairs by");
                    Check(use.Keyed && use.Stable, "keyed and stable");
                }
            }),
            Case("store.read-plan.the-probe-has-no-keyed-probe-where-the-own-table-has-no-index-for-the-match", async () =>
            {
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    var q = Tables.Query(notes).Join(sessionColumns, n => Sync160Match(("id", n["session"])));
                    var plan = new ReadPlan(q, tables[notes], Projection.All, ReadShape.Rows, store.ReadIndex);
                    Check(!plan.Uses[1].Keyed, "no index of notes starts with session");
                }
            }),
            Case("store.read-plan.checked-when-it-opens-refuses-a-joined-set-whose-key-the-bound-table-has-differently-the-join-would-pair-a-row-with-several", () =>
            {
                // Sol 1, Astra S2.
                var old = Sync160Columns("partners", [("id", Str()), ("tenant", Str())], ["id"]);
                var @new = Sync160Columns("partners", [("id", Str()), ("tenant", Str())], ["id", "tenant"]);
                var partner = Sync160Table("partner", Sync160Spread(@new), ["id", "tenant"], @new);
                var q = Tables.Query(own).Join(old, row => Sync160Match(("id", row["partner"])));
                StoreArgs(() => new ReadPlan(q, "own", Projection.All, ReadShape.Rows, new Sync160Bindings((old.Name, "partner", partner))),
                    "column set \"partners\" is joined by (id), its key is (id, tenant): not what this read was written for");
            }),
            Case("store.read-plan.checked-when-it-opens-refuses-a-column-an-exists-compares-that-the-bound-table-has-with-another-type-an-unused-one-may-change", () =>
            {
                // Sol 1: the reader compares `score` as an int; the publisher made it a num.
                var old = Sync160Columns("scores", [("id", Int()), ("score", Int()), ("note", Str())], ["id"]);
                var retyped = Sync160Columns("scores", [("id", Int()), ("score", Num()), ("note", Str())], ["id"]);
                var unused = Sync160Columns("scores", [("id", Int()), ("score", Int()), ("note", Int())], ["id"]);
                Sync160Bindings BoundTo(ColumnSet set) => new((set.Name, "other", Sync160Table("other", Sync160Spread(set), ["id"], set)));
                var q = Tables.Query(own).Where(row => Exists(old, Sync160Match(("id", row["id"])), t => Eq(t["score"], 1)));
                StoreArgs(() => new ReadPlan(q, "own", Projection.All, ReadShape.Rows, BoundTo(retyped)),
                    "column \"score\" of column set \"scores\" is not what this read was written for");
                Accepts(() => new ReadPlan(q, "own", Projection.All, ReadShape.Rows, BoundTo(unused)), "an unused column may change");
            }),
            Case("store.read-plan.checked-when-it-opens-takes-a-condition-as-data-only-with-canonical-host-keys-and-no-ordering-of-a-host-column", () =>
            {
                var project = new HostKind("project", new() { ["id"] = Str() });
                var owned = Sync160Columns("owned", [("id", Int()), ("project", Tables.Host(project))], ["id"]);
                string Refused(string node)
                {
                    try
                    {
                        StoreInternal.CheckNode(JsonNode.Parse(node), owned, "whereNode");
                        return "accepted";
                    }
                    catch (Exception error)
                    {
                        return error.Message;
                    }
                }
                var key = Tables.Compile(owned, Eq(owned.Refs["project"], project.Address(("id", "p1"))));
                Equal("accepted", Refused(key.ToString()), "a compiled condition");
                Sync160Contains("\"p1\" is no address key of project", Refused("""{"eq":["project","p1"]}"""), "no address key");
                Sync160Contains("is no address key of project", Refused("""{"inArray":["project",["session|p1"]]}"""), "a key of another kind");
                Sync160Contains("lt on host(...) column \"project\"", Refused("""{"lt":["project","project|p1"]}"""), "an order comparison");
                Sync160Contains("prefix on host(...) column", Refused("""{"prefix":["project","project|"]}"""), "a prefix");
                Sync160Contains("isNull on host(...) column", Refused("""{"isNull":"project"}"""), "isNull");
            }),
            Case("store.read-plan.a-table-handles-own-reads-page-and-count-take-an-exists-and-refuse-a-joined-query-instead-of-ignoring-the-join", async () =>
            {
                // Astra S3.
                var (store, _) = await Sync160PlanSeeded();
                await using (store)
                {
                    var context = ChordContext.Background;
                    var sessionRows = await TableIn(store, sessions);
                    var inTeam = Tables.Query(sessions).Where(s => Exists(teamSessions, Sync160Match(("session", s["id"]))));
                    Sequence(["s0", "s1"], (await sessionRows.PageAsync(inTeam, 10, context)).Items.Select(row => (string)row["id"]!), "a page takes an exists");
                    Equal(2L, await sessionRows.CountAsync(inTeam, context), "a count takes an exists");
                    var teamRows = await TableIn(store, teams);
                    var joined = Tables.Query(teams).Join(sessionColumns, t => Sync160Match(("id", t["session"])));
                    Rejected(await StoreRejection(teamRows.PageAsync(joined, 10, context)), null, "args", "a page of a joined query", "a joined query is read as a named query");
                    Rejected(await StoreRejection(teamRows.CountAsync(joined, context)), null, "args", "a count of a joined query", "a joined query is read as a named query");
                }
            }),
        ];
    }
}
