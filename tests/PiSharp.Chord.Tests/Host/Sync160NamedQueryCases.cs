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

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/named-query.test.ts.
//
// Named queries (slice 14 §3.1, §5, test list 2, 7, 11, 18): what self.Queries returns implements a `queries` member; a joined query's
// mapping gets the row and its partner, projected; PageAsync reads without a live read; the member check and staging; and a column set's
// binding across its publisher's reloads: unchanged code, a migration, a used column dropped, the set no longer exposed and exposed again,
// two tables exposing one name. Differences from upstream:
// - A reload's report returns at its objects' first check, which natively comes before a restarted object's tables are prepared (the
//   native activation parks on its dependencies first): the cases wait for the reload's work (DrainReloadsAsync) where upstream awaits
//   flushed().
// - "between the migration's commits": upstream replaces store.commit and runs the other owner's commit after the commit that recorded the
//   table `migrating`. Here the store's read seam (ChordStore.Reads) holds the migration's read of its rows, which comes between the same
//   two commits, until the other owner's commit ran.
// - Staging's refusals of a table handed to a reader and of a named query handed to self.Query cannot be written (the declarations are
//   typed); a named query and a table that are none of Chord's stand for them.
// - toBe on a value (the same object) is equality here.
internal static partial class Program
{
    private static readonly ColumnSet NamedSessionColumns = Tables.DefineColumns("sessions.columns", new ColumnSetOptions
    {
        Columns = new() { ["id"] = Tables.Host(Session), ["app"] = Str(), ["title"] = Str(), ["lastTouch"] = Int() },
        Key = ["id"],
    });

    private static readonly TableDefinition NamedRows = Tables.DefineTable("rows", new TableOptions
    {
        Version = 1,
        Columns = new(NamedSessionColumns.Columns) { ["cwd"] = Str(@default: "/") },
        Key = ["id"],
        Exposes = NamedSessionColumns,
    });

    private static readonly Contract NamedSessions = Contract.Local("app.sessions", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["put"] = Method(Obj(("id", Str()), ("app", Str()), ("title", Str()), ("lastTouch", Int()))),
            ["touch"] = Method(Obj(("id", Str()), ("at", Int()))),
        },
    });

    private static readonly TableDefinition NamedTeamRows = Tables.DefineTable("team-rows", new TableOptions
    { Version = 1, Columns = new() { ["team"] = Str(), ["session"] = Tables.Host(Session) }, Key = ["team", "session"] });

    private static readonly JsonObject NamedTeamItem = Obj(("session", Str()), ("title", Str()), ("touched", Int()));

    private static readonly Contract NamedClub = Contract.Local("app.club", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["teams"] = Queries(NamedTeamItem, ("ofTeam", Obj(("team", Str())))),
            ["add"] = Method(Obj(("team", Str()), ("session", Str()))),
        },
    });

    private static readonly Contract NamedTeamPage = Contract.Local("app.team-page", new()
    {
        Version = 1,
        Params = new() { ["team"] = Str() },
        Members = new() { ["sessions"] = Obj(("rows", Arr(NamedTeamItem)), ("more", Bool())) },
    });

    private static JsonObject NamedSession(JsonNode? id) => Session.Address(("id", ((JsonNode)id!).GetValue<string>())).ToJson();

    /// <summary>The sessions' store: the publisher of the column set. <paramref name="table"/> is the version its code declares;
    /// <paramref name="declare"/> adds what a test needs of its factory (a native; upstream <c>serveSessions</c>).</summary>
    private static void NamedServeSessions(IFacetHost h, TableDefinition? table = null, Action<IStoreSelf>? declare = null) => h.Serve(NamedSessions, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        declare?.Invoke(self);
        var rows = self.Table(table ?? NamedRows);
        return new(new Dictionary<string, object?>
        {
            ["put"] = Q7Tx(self, (tx, args) =>
            {
                var row = (JsonObject)args!.DeepClone();
                row["id"] = NamedSession(args["id"]);
                tx.Put(rows, row);
            }),
            ["touch"] = Q7Tx(self, (tx, args) => tx.Update(rows, new JsonObject { ["id"] = NamedSession(args!["id"]) }, new JsonObject { ["lastTouch"] = args["at"]!.DeepClone() })),
        });
    });

    /// <summary>The club's query of a team's sessions: its team rows joined with the sessions' column set.</summary>
    private static Query NamedOfTeam(JsonNode? args, ColumnSet set)
    {
        var team = (string)args!["team"]!;
        return Tables.Query(NamedTeamRows).Where(t => Eq(t["team"], team))
            .Join(set, t => Match(("id", t["session"])))
            .Where((_, session) => Eq(session["app"], "club"))
            .OrderBy((_, session) => session["lastTouch"], OrderDirection.Desc);
    }

    /// <summary>The club: its team rows joined with the sessions' column set, and a page per team. <paramref name="partners"/> takes the
    /// partner columns the club's mapping was handed, by call; <paramref name="sessionsOf"/> is the page's read, the test's to declare
    /// wrongly (staging; upstream <c>serveClub</c>).</summary>
    private static void NamedServeClub(IFacetHost h, List<string>? partners = null, Func<ISelf, string, object?>? sessionsOf = null)
    {
        h.Serve(NamedClub, (_, _, owner) =>
        {
            var self = (IStoreSelf)owner;
            var table = self.Table(NamedTeamRows);
            return new(new Dictionary<string, object?>
            {
                ["teams"] = self.Queries(table, new QueriesOptions(
                    Map: (row, session) =>
                    {
                        if (partners is not null) lock (partners) partners.Add(string.Join(",", session.Keys.Order(StringComparer.Ordinal)));
                        return new JsonObject
                        {
                            ["session"] = row["session"]!["params"]!["id"]!.DeepClone(),
                            ["title"] = session.GetValueOrDefault("title")?.DeepClone(),
                            ["touched"] = session.GetValueOrDefault("lastTouch")?.DeepClone(),
                        };
                    },
                    PartnerReads: ["title", "lastTouch"]), Q7Builders(("ofTeam", Q7Builder(args => NamedOfTeam(args, NamedSessionColumns))))),
                ["add"] = Q7Tx(self, (tx, args) => tx.Put(table, new JsonObject { ["team"] = args!["team"]!.DeepClone(), ["session"] = NamedSession(args["session"]) })),
            });
        });
        h.Serve(NamedTeamPage, (parameters, _, self) =>
        {
            var team = (string)parameters["team"]!;
            return new(new Dictionary<string, object?>
            {
                ["sessions"] = sessionsOf is not null ? sessionsOf(self, team)
                    : self.LiveQuery(Q7Named(self.Get(NamedClub, new()), "teams", "ofTeam"), new JsonObject { ["team"] = team }, 10),
            });
        });
    }

    /// <summary>The sessions' plugin at a table version: only the publisher's code changes in these reloads (upstream <c>publisher</c>).</summary>
    private static PluginFacet NamedPublisher(TableDefinition? table = null) => Q7Plugin("@test/sessions", h => NamedServeSessions(h, table));

    /// <summary>A host with both plugins, three sessions (two of the club) and a team of all three (upstream <c>hosted</c>).</summary>
    private static async Task<(ChordHost Host, List<HostError> Errors, PluginFacet Club)> NamedHosted(ChordStore store, List<string>? partners = null)
    {
        var errors = new List<HostError>();
        var host = Q7Host(store, errors);
        var club = Q7Plugin("@test/club", h => NamedServeClub(h, partners));
        await host.LoadAsync([NamedPublisher(), club], ChordContext.Background);
        var sessions = await host.GetAsync(NamedSessions, new(), ChordContext.Background);
        var teams = await host.GetAsync(NamedClub, new(), ChordContext.Background);
        var apps = new[] { "club", "club", "family" };
        for (var index = 0; index < apps.Length; index++)
        {
            await sessions.CallAsync("put", new JsonObject { ["id"] = $"s{index}", ["app"] = apps[index], ["title"] = $"t{index}", ["lastTouch"] = index }, ChordContext.Background);
            await teams.CallAsync("add", new JsonObject { ["team"] = "u12", ["session"] = $"s{index}" }, ChordContext.Background);
        }
        return (host, errors, club);
    }

    private static Task<IReference> NamedPage(ChordHost host, string team = "u12") => host.GetAsync(NamedTeamPage, new() { ["team"] = team }, ChordContext.Background);

    /// <summary>Reloads and waits for the reload's work: the restarted objects are live (or parked) again.</summary>
    private static async Task<ReloadReport> NamedReload(ChordHost host, params PluginFacet[] plugins)
    {
        var report = await host.ReloadAsync(plugins, ChordContext.Background);
        await host.DrainReloadsAsync(ChordContext.Background);
        await Q7Settled(host);
        return report;
    }

    /// <summary>The kinds and objects of the reported <c>activation_failed</c> errors, with their causes' kinds (upstream <c>failures</c>).</summary>
    private static List<string> NamedActivationFailures(List<HostError> errors)
    {
        lock (errors) return [.. errors.Where(error => error.Kind == "activation_failed").Select(error => $"{error.Object}: {error.Failure!.Kind.ToString().ToLowerInvariant()}")];
    }

    /// <summary>The failure of an attempt of the club whose factory returns <paramref name="teams"/> for its <c>queries</c> member.</summary>
    private static async Task<List<string>> NamedBroken(Func<IStoreSelf, object?> teams)
    {
        await using var store = await MemoryStore();
        var errors = new List<HostError>();
        await using var host = Q7Host(store, errors);
        // A wrong member reaches the member check.
        await host.LoadAsync([Q7Plugin("@test/club", h => h.Serve(NamedClub, (_, _, owner) => new(new Dictionary<string, object?>
        {
            ["teams"] = teams((IStoreSelf)owner),
            ["add"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
        })))], ChordContext.Background);
        try { await host.GetAsync(NamedClub, new(), ChordContext.Background); } catch { }
        return Q7Failures(errors);
    }

    // A named query over a table that exposes nothing, whose condition tests another owner's set. The successor renames a column the
    // mapping reads, with a data step: a statement compiled for the table as it was fails "no such column" from the migration's first
    // commit on.
    private static readonly ColumnSet NamedGateSet = Tables.DefineColumns("gate.columns", new ColumnSetOptions { Columns = new() { ["id"] = Int() }, Key = ["id"] });

    private static readonly TableDefinition NamedGate = Tables.DefineTable("gate", new TableOptions
    { Version = 1, Columns = new(NamedGateSet.Columns), Key = ["id"], Exposes = NamedGateSet });

    private static readonly TableDefinition NamedOld = Tables.DefineTable("texts", new TableOptions
    { Version = 1, Columns = new() { ["id"] = Int(), ["text"] = Str() }, Key = ["id"] });

    private static readonly TableDefinition NamedNew = Tables.EvolveTable(NamedOld, t => t.Rename("text", "renamed").Rows(_ => new JsonObject()));

    private static readonly JsonObject NamedItem = Obj(("id", Int()), ("text", Str()));

    private static readonly Contract NamedProvider = Contract.Local("app.provider", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["rows"] = Queries(NamedItem, ("all", Obj())),
            ["put"] = Method(Obj(("id", Int()), ("text", Str()))),
            // As put, without waiting for its commit: a write that is no running call's.
            ["later"] = Method(Obj(("id", Int()), ("text", Str()))),
        },
    });

    private static readonly Contract NamedGates = Contract.Local("app.gates", new() { Version = 1, Params = new(), Members = new() { ["put"] = Method(Int()) } });

    private static readonly Contract NamedView = Contract.Local("app.view", new()
    { Version = 1, Params = new(), Members = new() { ["items"] = Obj(("rows", Arr(NamedItem)), ("more", Bool())) } });

    private static readonly Contract NamedScreen = Contract.Local("app.screen", new()
    { Version = 1, Params = new(), Members = new() { ["items"] = Obj(("rows", Arr(NamedItem)), ("moreAfter", Bool())), ["more"] = Method() } });

    /// <summary>Every row of <paramref name="table"/> whose id is in the gates' set.</summary>
    private static Query NamedAll(TableDefinition table) => Tables.Query(table).Where(r => Exists(NamedGateSet, Match(("id", r["id"]))));

    /// <summary>The provider's plugin, before or after its migration; <paramref name="declared"/> takes the old code's named query, as
    /// each factory run declared it (upstream <c>provider</c>).</summary>
    private static PluginFacet NamedProviderPlugin(bool migrated, List<WeakReference>? declared = null) => Q7Plugin("@test/provider", h => h.Serve(NamedProvider, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        if (migrated)
        {
            var renamed = self.Table(NamedNew);
            return new(new Dictionary<string, object?>
            {
                ["rows"] = self.Queries(renamed, new QueriesOptions(Map: (row, _) => new JsonObject { ["id"] = row["id"]!.DeepClone(), ["text"] = row["renamed"]!.DeepClone() }),
                    Q7Builders(("all", Q7Builder(_ => NamedAll(NamedNew))))),
                ["put"] = Q7Tx(self, (tx, args) => tx.Put(renamed, new JsonObject { ["id"] = args!["id"]!.DeepClone(), ["renamed"] = args["text"]!.DeepClone() })),
                ["later"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
            });
        }
        var table = self.Table(NamedOld);
        var rows = self.Queries(table, new QueriesOptions(Map: (row, _) => new JsonObject { ["id"] = row["id"]!.DeepClone(), ["text"] = row["text"]!.DeepClone() }),
            Q7Builders(("all", Q7Builder(_ => NamedAll(NamedOld)))));
        if (declared is not null) lock (declared) declared.Add(new WeakReference(rows["all"]));
        return new(new Dictionary<string, object?>
        {
            ["rows"] = rows,
            ["put"] = Q7Tx(self, (tx, row) => tx.Put(table, row)),
            ["later"] = (HostMethod)((row, call) =>
            {
                // The commit outlives the call: its context must not end with it.
                var copy = ((JsonNode)row!).DeepClone();
                var writing = self.TxAsync(tx =>
                {
                    tx.Put(table, copy);
                    return Task.FromResult(true);
                }, ChordContext.Background);
                return Task.FromResult<object?>(null);
            }),
        });
    }));

    private static PluginFacet NamedOthers() => Q7Plugin("@test/others", h =>
    {
        h.Serve(NamedGates, (_, _, owner) =>
        {
            var self = (IStoreSelf)owner;
            var table = self.Table(NamedGate);
            return new(new Dictionary<string, object?> { ["put"] = Q7Tx(self, (tx, id) => tx.Put(table, new JsonObject { ["id"] = id!.DeepClone() })) });
        });
        h.Serve(NamedView, (_, _, self) => new(new Dictionary<string, object?>
        {
            ["items"] = self.LiveQuery(Q7Named(self.Get(NamedProvider, new()), "rows", "all"), new JsonObject(), 10),
        }));
        h.Serve(NamedScreen, (_, _, self) =>
        {
            var list = self.List("rows", [(Q7Named(self.Get(NamedProvider, new()), "rows", "all"), new JsonObject())], new ListOptions(1));
            return new(new Dictionary<string, object?>
            {
                ["items"] = list.Value,
                ["more"] = (HostMethod)(async (_, ctx) => { await list.MoreAsync(ctx.Context); return null; }),
            });
        });
    });

    /// <summary>A host with the old provider, rows 1 and 2 behind their gates, and <c>during</c> run once between the migration's commits
    /// (upstream <c>hosted_</c>).</summary>
    private sealed class NamedRestart : IAsyncDisposable
    {
        internal ChordStore Store = null!;
        internal ChordHost Host = null!;
        internal IReference Gates = null!;
        internal PluginFacet Others = null!;
        internal readonly List<WeakReference> Declared = [];
        internal volatile bool Migrated;
        internal Func<Task>? During;

        internal static async Task<NamedRestart> Create()
        {
            var fixture = new NamedRestart { Store = await MemoryStore(), Others = NamedOthers() };
            fixture.Host = Q7Host(fixture.Store);
            var context = ChordContext.Background;
            await fixture.Host.LoadAsync([NamedProviderPlugin(false, fixture.Declared), fixture.Others], context);
            fixture.Gates = await fixture.Host.GetAsync(NamedGates, new(), context);
            var rows = await fixture.Host.GetAsync(NamedProvider, new(), context);
            foreach (var (id, text) in new[] { (1, "first"), (2, "second") })
            {
                await fixture.Gates.CallAsync("put", JsonValue.Create(id), context);
                await rows.CallAsync("put", new JsonObject { ["id"] = id, ["text"] = text }, context);
            }
            rows.Release();
            // The other owner acts once the table is recorded `migrating`: the migration's read of its rows waits for it.
            var store = fixture.Store;
            store.Reads = async read =>
            {
                try { await read; } catch { }
                if (fixture.Migrated || store.Table("app.provider:texts")?.State != TableState.Migrating) return;
                fixture.Migrated = true;
                Check(store.ReadIndex.IsHeld("app.provider:texts"), "the table is held while it migrates");
                if (fixture.During is { } during) await during();
            };
            return fixture;
        }

        /// <summary>Reloads to the migrated provider and waits until its successor is live.</summary>
        internal Task Migrate() => NamedReload(Host, NamedProviderPlugin(true), Others);

        internal Task Gate(int id, ChordContext? context = null) => Gates.CallAsync("put", JsonValue.Create(id), context ?? ChordContext.Background);

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    /// <summary>Whether every read over the provider's table is of the migrated table's query.</summary>
    private static bool NamedReadsOfNew(ChordStore store) =>
        (store.ReadIndex.On("app.provider:texts") ?? []).All(read => ReferenceEquals(read.Plan.Query.Table, NamedNew));

    private static IEnumerable<(string, Func<Task>)> HostSync160NamedQueryCases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("host.named-query.a-joined-named-query-maps-each-row-with-its-partners-projected-columns-in-the-partners-order", async () =>
            {
                await using var store = await MemoryStore();
                var partners = new List<string>();
                var (host, _, _) = await NamedHosted(store, partners);
                await using var closing = host;
                var page = await NamedPage(host);
                // The family's session is no club session: its team row is dropped.
                Json("""[{"session":"s1","title":"t1","touched":1},{"session":"s0","title":"t0","touched":0}]""", Q7Rows(page, "sessions"), "the club's sessions of the team");
                Equal("lastTouch,title", partners[^1], "the partner's projected columns");
                // The sessions' owner commits: the club's read over its table runs in that owner's step.
                await Q7Call(await host.GetAsync(NamedSessions, new(), context), "touch", """{"id":"s0","at":9}""");
                Equal("\"s0\" \"s1\"", Q7Ids(page, "sessions", "session"), "the partner's order");
            }),
            Case("host.named-query.a-joined-named-query-reads-a-page-at-a-time-without-a-live-read", async () =>
            {
                await using var store = await MemoryStore();
                var (host, _, _) = await NamedHosted(store);
                await using var closing = host;
                var club = await host.GetAsync(NamedClub, new(), context);
                var ofTeam = Q7Named(club, "teams", "ofTeam");
                var first = await ofTeam.PageAsync(new JsonObject { ["team"] = "u12" }, 1, context);
                Json("""[{"session":"s1","title":"t1","touched":1}]""", new JsonArray([.. first.Items.Select(item => item?.DeepClone())]), "the first page");
                var second = await ofTeam.PageAsync(new JsonObject { ["team"] = "u12" }, 1, context, new PageOptions { After = first.Next });
                Equal("s0", (string?)second.Items.Single()!["session"], "the second page");
                Check(second.Next is null, "no page follows");
                Rejected(await Rejection(ofTeam.PageAsync(new JsonObject { ["team"] = "u12" }, 1, context, new PageOptions { After = "nope" })), "invalid", "args", "a cursor that is none");
                Equal(0, Q7Reads(store, "app.club:team-rows"), "a page keeps no read");
            }),
            Case("host.named-query.the-member-check-refuses-anything-but-what-self-queries-returned-a-listed-query-without-a-builder-and-a-second-schema", async () =>
            {
                var empty = await NamedBroken(_ => new Dictionary<string, INamedQuery>());
                Equal(1, empty.Count, "one failure");
                Sync160Contains("teams: no builder for the listed query \"ofTeam\"", empty[0], "a listed query without a builder");
                var other = await NamedBroken(_ => JsonValue.Create(5));
                Equal(1, other.Count, "one failure");
                Sync160Contains("teams: a queries member is what self.queries returned", other[0], "anything else");
                var twice = await NamedBroken(self => self.Queries(self.Table(NamedTeamRows), new QueriesOptions(Map: (_, _) => null),
                    Q7Builders(("ofTeam", new QueryBuilder(_ => Tables.Query(NamedTeamRows), Obj(("team", Str())))))));
                Equal(1, twice.Count, "one failure");
                Sync160Contains("the contract has its argument schema; give only the builder", twice[0], "a second schema");
            }),
            Case("host.named-query.a-column-set-across-its-publishers-reloads-unchanged-code-and-an-unused-column-added-no-reader-restarts-equal-rows-send-nothing", async () =>
            {
                await using var store = await MemoryStore();
                var (host, errors, club) = await NamedHosted(store);
                await using var closing = host;
                var page = await NamedPage(host);
                var watch = await page.Member("sessions").WatchAsync(context);
                var calls = ChangesOf(watch);
                await NamedReload(host, NamedPublisher(), club);
                var wider = Tables.EvolveTable(NamedRows, t => t.Add("note", Opt(Str())));
                await NamedReload(host, NamedPublisher(wider), club);
                Sequence([], Q7Kinds(calls), "equal rows send nothing");
                Sequence([], NamedActivationFailures(errors), "nothing failed");
                // The migrated table still feeds the reader.
                await Q7Call(await host.GetAsync(NamedSessions, new(), context), "touch", """{"id":"s0","at":9}""");
                await Q7Settled(host);
                Equal("\"s0\" \"s1\"", Q7Ids(page, "sessions", "session"), "the migrated table feeds the reader");
                Equal(1, calls.Count, "one frame");
                await watch.StopAsync();
            }),
            Case("host.named-query.a-column-set-across-its-publishers-reloads-a-used-column-dropped-fails-exactly-the-reads-using-it-before-any-sql-restored-they-heal", async () =>
            {
                await using var store = await MemoryStore();
                var (host, _, club) = await NamedHosted(store);
                await using var closing = host;
                var page = await NamedPage(host);
                var watch = await page.Member("sessions").WatchAsync(context);
                var calls = ChangesOf(watch);
                var narrow = Tables.DefineColumns("sessions.columns", new ColumnSetOptions
                { Columns = new() { ["id"] = Tables.Host(Session), ["app"] = Str(), ["lastTouch"] = Int() }, Key = ["id"] });
                var dropped = Tables.EvolveTable(NamedRows, t => t.Drop("title"), new EvolveOptions { Exposes = narrow });
                await NamedReload(host, NamedPublisher(dropped), club);
                Sequence(["failed: column \"title\" of column set \"sessions.columns\" is not what this read was written for"], Q7Kinds(calls), "the read using the column fails");
                // The store is usable: another owner still commits. Restored, the read heals.
                await Q7Call(await host.GetAsync(NamedClub, new(), context), "add", """{"team":"u14","session":"s0"}""");
                var restored = Tables.EvolveTable(dropped, t => t.Add("title", Str(@default: "restored")), new EvolveOptions { Exposes = NamedSessionColumns });
                await NamedReload(host, NamedPublisher(restored), club);
                await Q7Until(() => Q7Ids(page, "sessions", "title") == "\"restored\" \"restored\"", "the read heals");
                await watch.StopAsync();
            }),
            Case("host.named-query.a-column-set-across-its-publishers-reloads-a-set-exposed-no-more-is-not-served-two-tables-exposing-one-name-are-refused-naming-both", async () =>
            {
                await using var store = await MemoryStore();
                var (host, errors, club) = await NamedHosted(store);
                await using var closing = host;
                var page = await NamedPage(host);
                var hidden = Tables.EvolveTable(NamedRows, t => t.Add("note", Opt(Str())), new EvolveOptions { ExposesNone = true });
                await NamedReload(host, NamedPublisher(hidden), club);
                StoreThrows(() => _ = page.Member("sessions").Value, "column set \"sessions.columns\" is not served", "the set is not served");
                var exposed = Tables.EvolveTable(hidden, t => t.Add("more", Opt(Str())), new EvolveOptions { Exposes = NamedSessionColumns });
                await NamedReload(host, NamedPublisher(exposed), club);
                await Q7Until(() => Q7Ids(page, "sessions", "session") == "\"s1\" \"s0\"", "exposed again, the read heals");
                // Another owner exposing the same name: its activation fails, the binding stays.
                var thief = Contract.Local("app.thief", new() { Version = 1, Params = new(), Members = new() { ["n"] = Int() } });
                var stolen = Tables.DefineTable("stolen", new TableOptions { Version = 1, Columns = new(NamedSessionColumns.Columns), Key = ["id"], Exposes = NamedSessionColumns });
                var thieving = Q7Plugin("@test/thief", h => h.Serve(thief, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    self.Table(stolen);
                    return new(new Dictionary<string, object?> { ["n"] = self.Derive([], _ => JsonValue.Create(1)) });
                }));
                await NamedReload(host, NamedPublisher(exposed), club, thieving);
                try { await host.GetAsync(thief, new(), context); } catch { }
                Sync160Contains("app.thief: broken", NamedActivationFailures(errors)[^1], "the thief's activation fails broken");
                HostError last;
                lock (errors) last = errors.Last(error => error.Kind == "activation_failed");
                Sync160Contains("exposed by tables \"app.sessions:rows\" and \"app.thief:stolen\"", last.Failure!.Error.Message, "naming both tables");
                await Q7Until(() => Q7Rows(page, "sessions").Count == 2, "the binding stays");
            }),
            Case("host.named-query.the-mappings-projection-gets-only-the-columns-reads-and-partner-reads-list-also-when-the-query-orders-or-matches-by-others", async () =>
            {
                // Review round 1 (Sol 7): the statement selects the key, the order and the match columns for the store; the mapping does not
                // see them.
                await using var store = await MemoryStore();
                var seen = new List<string>();
                var plain = Tables.DefineTable("plain", new TableOptions
                { Version = 1, Columns = new() { ["id"] = Int(), ["group"] = Str(), ["text"] = Str(), ["session"] = Tables.Host(Session) }, Key = ["id"] });
                var projected = Contract.Local("app.projected", new()
                { Version = 1, Params = new(), Members = new() { ["rows"] = Queries(Obj(("text", Str())), ("all", Obj())), ["put"] = Method(Int()) } });
                await using var host = Q7Host(store);
                await host.LoadAsync([NamedPublisher(), Q7Plugin("@test/projected", h => h.Serve(projected, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    var table = self.Table(plain);
                    return new(new Dictionary<string, object?>
                    {
                        ["rows"] = self.Queries(table, new QueriesOptions(
                            Map: (row, session) =>
                            {
                                lock (seen) seen.Add(string.Join(",", row.Keys.Order(StringComparer.Ordinal)) + "|" + string.Join(",", session.Keys.Order(StringComparer.Ordinal)));
                                return new JsonObject { ["text"] = row["text"]!.DeepClone() };
                            },
                            Reads: ["text"], PartnerReads: ["title"]),
                            Q7Builders(("all", Q7Builder(_ => Tables.Query(plain).Join(NamedSessionColumns, row => Match(("id", row["session"])))
                                .OrderBy(row => row["group"]).OrderBy((_, session) => session["lastTouch"]))))),
                        ["put"] = Q7Tx(self, (tx, id) => tx.Put(table, new JsonObject { ["id"] = id!.DeepClone(), ["group"] = "g", ["text"] = "t", ["session"] = NamedSession(JsonValue.Create("s0")) })),
                    });
                }))], context);
                var sessions = await host.GetAsync(NamedSessions, new(), context);
                await Q7Call(sessions, "put", """{"id":"s0","app":"club","title":"t0","lastTouch":1}""");
                var rows = await host.GetAsync(projected, new(), context);
                await Q7Call(rows, "put", "1");
                var page = await Q7Named(rows, "rows", "all").PageAsync(new JsonObject(), 5, context);
                Json("""[{"text":"t"}]""", new JsonArray([.. page.Items.Select(item => item?.DeepClone())]), "the mapped item");
                Sequence(["text|title"], seen, "the mapping got the listed columns only");
            }),
            Case("host.named-query.bindings-across-reloads-a-publisher-whose-new-code-declares-no-table-serves-its-set-no-more", async () =>
            {
                // Sol 2, Astra S1: the binding must not outlive the last table.
                await using var store = await MemoryStore();
                var (host, _, club) = await NamedHosted(store);
                await using var closing = host;
                var page = await NamedPage(host);
                var bare = Q7Plugin("@test/sessions", h => h.Serve(NamedSessions, (_, _, _) => new(new Dictionary<string, object?>
                {
                    ["put"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                    ["touch"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                })));
                await NamedReload(host, bare, club);
                Check(store.ReadIndex.Get(NamedSessionColumns.Name) is null, "the set is bound no more");
                StoreThrows(() => _ = page.Member("sessions").Value, "column set \"sessions.columns\" is not served", "the reader's member fails");
            }),
            Case("host.named-query.bindings-across-reloads-a-set-moved-to-another-plugins-table-in-one-reload-is-bound-to-the-new-table", async () =>
            {
                // Opus S6, round 2 S3: the old owner's binding goes at its seal, not at the end of its teardown. Its native's close ends only
                // when the new owner is live, so its teardown is still running when the new owner binds.
                await using var store = await MemoryStore();
                var (host, _, club) = await NamedHosted(store);
                await using var closing = host;
                var page = await NamedPage(host);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var slow = Q7Plugin("@test/sessions", h => NamedServeSessions(h, NamedRows, self => self.Open(_ => Task.FromResult(true), (_, _, _) => closed.Task)));
                await NamedReload(host, slow, club);
                await host.GetAsync(NamedSessions, new(), context);
                var moved = Contract.Local("app.moved", new() { Version = 1, Params = new(), Members = new() { ["put"] = Method(Str()) } });
                var movedRows = Tables.DefineTable("moved-rows", new TableOptions
                { Version = 1, Columns = new(NamedSessionColumns.Columns), Key = ["id"], Exposes = NamedSessionColumns });
                var mover = Q7Plugin("@test/moved", h => h.Serve(moved, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    var rows = self.Table(movedRows);
                    return new(new Dictionary<string, object?>
                    {
                        ["put"] = Q7Tx(self, (tx, id) => tx.Put(rows, new JsonObject { ["id"] = NamedSession(id), ["app"] = "club", ["title"] = "moved", ["lastTouch"] = 5 })),
                    });
                }));
                // The sessions' plugin is gone, another plugin exposes the set from its own table.
                var report = await host.ReloadAsync([club, mover], context);
                Equal(0, report.Failed.Count, "nothing failed staging");
                var rows = await host.GetAsync(moved, new(), context);
                closed.TrySetResult();
                await rows.CallAsync("put", JsonValue.Create("s0"), context);
                await Q7Until(() => Q7Rows(page, "sessions").Count == 1, "the reader reads the new table");
                Equal("app.moved:moved-rows", store.ReadIndex.Get(NamedSessionColumns.Name)?.Table, "the set is bound to the new table");
                Json("""[{"session":"s0","title":"moved","touched":5}]""", Q7Rows(page, "sessions"), "the new table's row");
            }),
            Case("host.named-query.bindings-across-reloads-two-tables-of-one-object-exposing-one-set-are-refused-where-they-are-declared-naming-both", async () =>
            {
                await using var store = await MemoryStore();
                var errors = new List<HostError>();
                await using var host = Q7Host(store, errors);
                var twice = Contract.Local("app.twice", new() { Version = 1, Params = new(), Members = new() { ["n"] = Int() } });
                var second = Tables.DefineTable("second", new TableOptions { Version = 1, Columns = new(NamedSessionColumns.Columns), Key = ["id"], Exposes = NamedSessionColumns });
                await host.LoadAsync([Q7Plugin("@test/twice", h => h.Serve(twice, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    self.Table(NamedRows);
                    self.Table(second);
                    return new(new Dictionary<string, object?> { ["n"] = self.Derive([], _ => JsonValue.Create(1)) });
                }))], context);
                try { await host.GetAsync(twice, new(), context); } catch { }
                Sync160Contains("is exposed by tables \"rows\" and \"second\"", Q7Failures(errors)[^1], "naming both tables");
            }),
            Case("host.named-query.a-providers-restart-under-its-readers-runs-no-statement-compiled-for-the-table-as-it-was-no-failed-frame-while-another-owner-commits", async () =>
            {
                // Round 1 (Sol 3, Opus S1; test list 11a): the other owner commits between the migration's commits, and that commit runs no
                // read at all.
                var (_, counting, counts) = await Metered();
                await using var fixture = await NamedRestart.Create();
                fixture.During = () => fixture.Gate(1, counting);
                var view = await fixture.Host.GetAsync(NamedView, new(), context);
                var watch = await view.Member("items").WatchAsync(context);
                var calls = ChangesOf(watch);
                await fixture.Migrate();
                Check(fixture.Migrated, "the other owner committed between the migration's commits");
                Check(!counts().ContainsKey("chord.query.runs"), "that commit ran no read");
                Sequence([], Q7Kinds(calls), "no failed frame, and equal rows send nothing");
                Equal("\"first\" \"second\"", Q7Ids(view, "items", "text"), "the successor's rows");
                await watch.StopAsync();
            }),
            Case("host.named-query.a-providers-restart-under-its-readers-a-lists-more-during-the-migration-waits-for-the-successor-no-read-of-the-old-statement-is-registered", async () =>
            {
                // Round 2 (Sol): `more` on the retired query registered a read of the old plan, which stayed in the index and failed "no such
                // column" on the next commit.
                await using var fixture = await NamedRestart.Create();
                var screen = await fixture.Host.GetAsync(NamedScreen, new(), context);
                Task? more = null;
                fixture.During = async () =>
                {
                    await fixture.Gate(1);
                    // Not awaited here: it ends when the successor is live, which waits for this commit.
                    more = screen.CallAsync("more", null, context);
                };
                Json("""{"rows":[{"id":1,"text":"first"}],"moreAfter":true}""", screen.Member("items").Value, "the first page");
                await fixture.Migrate();
                Check(more is not null, "more was called during the migration");
                await more!;
                await Q7Until(() => Q7Ids(screen, "items", "text") == "\"first\" \"second\"", "the page loaded behind the successor's pages");
                Check(Q7Reads(fixture.Store, "app.provider:texts") > 0, "the list's pages are registered");
                Check(NamedReadsOfNew(fixture.Store), "every read is of the migrated table's query");
                // A later commit of the other owner runs the list's pages: none fails.
                await fixture.Gate(2);
                await Q7Settled(fixture.Host);
                Equal("\"first\" \"second\"", Q7Ids(screen, "items", "text"), "no page failed");
                // Nothing of the handover keeps the old code's query.
                await Q7Settled(fixture.Host);
                await ExpectCollected(fixture.Declared[0]);
            }),
            Case("host.named-query.a-providers-restart-under-its-readers-a-write-admitted-before-the-providers-seal-and-paused-across-it-publishes-then-no-old-statement-runs", async () =>
            {
                // Test list 17: the old attempt's reads stay registered until its admitted work settled.
                await using var fixture = await NamedRestart.Create();
                var rows = await fixture.Host.GetAsync(NamedProvider, new(), context);
                var view = await fixture.Host.GetAsync(NamedView, new(), context);
                await fixture.Gate(3);
                var watch = await view.Member("items").WatchAsync(context);
                var calls = ChangesOf(watch);
                // The provider's next commit waits inside its step, after its admission and its reads' runs.
                var pause = Sync160Pause();
                fixture.Store.NextCommit = writes => [.. writes, pause.Write];
                await Q7Call(rows, "later", """{"id":3,"text":"third"}""");
                await pause.Ran;
                // No call is running: the reload seals the provider at once, with the write still in its step.
                var reloaded = fixture.Host.ReloadAsync([NamedProviderPlugin(true), fixture.Others], context);
                await Q7Settled(fixture.Host);
                Check(!fixture.Migrated, "the migration waits for the admitted write");
                pause.Release();
                await reloaded;
                await fixture.Host.DrainReloadsAsync(context);
                await Q7Settled(fixture.Host);
                Check(fixture.Migrated, "the table migrated");
                Sequence(["value"], Q7Kinds(calls), "the admitted write publishes, once");
                Equal("\"first\" \"second\" \"third\"", Q7Ids(view, "items", "text"), "the rows");
                Check(NamedReadsOfNew(fixture.Store), "every read is of the migrated table's query");
                await watch.StopAsync();
            }),
            Case("host.named-query.a-providers-restart-under-its-readers-a-removed-providers-reads-leave-the-index-its-readers-keep-their-rows-another-owners-commit-runs-nothing", async () =>
            {
                // Round 2 (Opus S2): the reads retire at the old attempt's end, with no successor and no hold to mask it.
                await using var fixture = await NamedRestart.Create();
                var view = await fixture.Host.GetAsync(NamedView, new(), context);
                var before = Q7Value(view, "items");
                await NamedReload(fixture.Host, fixture.Others);
                Check(fixture.Store.ReadIndex.On("app.others:gate") is null, "no read is left over the gates' table");
                var (_, counting, counts) = await Metered();
                await fixture.Gate(1, counting);
                await fixture.Gate(3, counting);
                Check(!counts().ContainsKey("chord.query.runs"), "the other owner's commits run nothing");
                Check(JsonNode.DeepEquals(before, view.Member("items").Value), "the reader keeps its rows");
            }),
            Case("host.named-query.a-providers-restart-under-its-readers-keeps-nothing-of-the-old-codes-named-query-once-its-readers-read-the-successors", async () =>
            {
                await using var fixture = await NamedRestart.Create();
                var view = await fixture.Host.GetAsync(NamedView, new(), context);
                var screen = await fixture.Host.GetAsync(NamedScreen, new(), context);
                await fixture.Migrate();
                await Q7Until(() => Q7Rows(view, "items").Count == 2 && Q7Reads(fixture.Store, "app.provider:texts") == 2, "the readers read the successor's query");
                Equal(1, Q7Rows(screen, "items").Count, "the list's first page");
                Equal(1, fixture.Declared.Count, "the old code's query was declared once");
                await Q7Settled(fixture.Host);
                await ExpectCollected(fixture.Declared[0]);
            }),
            Case("host.named-query.staging-keeps-a-plugin-on-its-old-code-when-its-new-factory-declares-a-read-wrongly", async () =>
            {
                // Opus S7, Fable S10: the checks an attempt makes that need no handle run against the recording self.
                await using var store = await MemoryStore();
                var (host, _, _) = await NamedHosted(store);
                await using var closing = host;
                var page = await NamedPage(host);
                INamedQuery Teams(ISelf self) => Q7Named(self.Get(NamedClub, new()), "teams", "ofTeam");
                async Task<string> Failed(Func<ISelf, string, object?> declare)
                {
                    var report = await NamedReload(host, NamedPublisher(), Q7Plugin("@test/club", h => NamedServeClub(h, null, declare)));
                    return string.Join(",", report.Failed.Select(failure => failure.Error.Message));
                }
                Sync160Contains("self.liveQuery: { first: n } is required", await Failed((self, team) => self.LiveQuery(Teams(self), new JsonObject { ["team"] = team }, 0)), "no page size");
                Sync160Contains("a list has a name", await Failed((self, team) => self.List("", [(Teams(self), new JsonObject { ["team"] = team })], new ListOptions(2)).Value), "a list without a name");
                Sync160Contains("list \"a\" is declared twice", await Failed((self, team) =>
                {
                    self.List("a", [(Teams(self), new JsonObject { ["team"] = team })], new ListOptions(2));
                    return self.List("a", [(Teams(self), new JsonObject { ["team"] = team })], new ListOptions(2)).Value;
                }), "a second list of a name");
                // The old code still serves the page.
                await Q7Until(() => Q7Ids(page, "sessions", "session") == "\"s1\" \"s0\"", "the old code still serves the page");
            }),
            Case("host.named-query.staging-refuses-a-named-query-and-a-table-that-are-none-of-its-factorys-as-an-attempt-does", async () =>
            {
                // Round 2 (Sol S1): a recorded handle says whether it stands for a table or a named query.
                await using var store = await MemoryStore();
                await using var host = Q7Host(store);
                var counted = Contract.Local("app.counted", new() { Version = 1, Params = new(), Members = new() { ["n"] = Int() } });
                var plain = Tables.DefineTable("plain", new TableOptions { Version = 1, Columns = new() { ["id"] = Int() }, Key = ["id"] });
                PluginFacet Serving(Func<IStoreSelf, object?> declare) => Q7Plugin("@test/counted", h => h.Serve(counted, (_, _, owner) =>
                    new(new Dictionary<string, object?> { ["n"] = declare((IStoreSelf)owner) })));
                await host.LoadAsync([Serving(self => self.Derive([], _ => JsonValue.Create(1)))], context);
                var reference = await host.GetAsync(counted, new(), context);
                async Task<string> Failed(Func<IStoreSelf, object?> declare)
                {
                    var report = await NamedReload(host, Serving(declare));
                    return string.Join(",", report.Failed.Select(failure => failure.Error.Message));
                }
                IReadOnlyDictionary<string, INamedQuery> Named(IStoreSelf self) => self.Queries(self.Table(plain), new QueriesOptions(),
                    Q7Builders(("all", new QueryBuilder(_ => Tables.Query(plain), Obj()))));
                Sync160Contains("self.count reads a named query; a table of this object is read with self.query", await Failed(self => self.Count(new Q7ForeignQuery(), new JsonObject())), "count");
                Sync160Contains("self.liveQuery reads a named query", await Failed(self => self.LiveQuery(new Q7ForeignQuery(), new JsonObject(), 1)), "liveQuery");
                Sync160Contains("self.list \"l\" reads a named query", await Failed(self => self.List("l", [(new Q7ForeignQuery(), new JsonObject())], new ListOptions(1)).Value), "list");
                Sync160Contains("self.query: the table is not one this factory declared", await Failed(self => self.Query(new Q7ForeignTable(), Tables.Query(plain), 5)), "query");
                Sync160Contains("self.queries: the table is not one this factory declared", await Failed(self => self.Queries(new Q7ForeignTable(), new QueriesOptions(), Q7Builders())), "queries");
                // What an attempt accepts, staging accepts: the object's own named query, read and counted.
                Equal("", await Failed(self => self.Count(Named(self)["all"], new JsonObject())), "the object's own named query, counted");
                await Q7Until(() => reference.Member("n").Value?.ToJsonString() == "0", "the new code counts its rows");
            }),
        ];
    }
}
