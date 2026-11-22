using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/host-data.test.ts, the three cases 1.6.0 added (describe "a
// delete limited to one plugin", slice 14 §6, test 16), and authored cases (host.host-data.160-*) of what the store's column sets ask of
// a host: binding a set at activation, a reader of another object's set following that object's commits, a set dropped by a reload and
// at close, and one set exposed by two tables refused.
// The upstream fixture serves a board (a host-keyed document, a table with a host column, a live query and a derive over both), a view
// and a ledger from one facet and splits them over two plugins with a filtering `serve`; here each plugin has its own facet. The view
// serves none of the three cases and is left out.
internal static partial class Program
{
    private static readonly StateDefinition Data160Meta = StateDefinition.Define("meta", new()
    {
        Version = 1, KeyHost = Session, Persist = true, CheckpointWhen = (_, _, _) => false,
        Schema = Obj(("title", Str())), Initial = () => new() { ["title"] = "" },
    });

    private static readonly TableDefinition Data160Labels = Tables.DefineTable("labels", new()
    {
        Version = 1, Columns = new() { ["session"] = Tables.Host(Session), ["label"] = Str() }, Key = ["session", "label"],
    });

    private static readonly TableDefinition Data160Entries = Tables.DefineTable("entries", new()
    {
        Version = 1, Columns = new() { ["session"] = Tables.Host(Session), ["n"] = Int() }, Key = ["session", "n"],
    });

    /// <summary>S1's title and labels as live members and a derive over both (upstream <c>Board</c>, without the members the three cases
    /// do not use).</summary>
    private static readonly Contract Data160Board = Contract.Local("test.board", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["meta"] = Obj(("title", Str())),
            ["labels"] = Arr(Unknown()),
            ["summary"] = Str(),
            ["write"] = Method(Obj(("id", Str()), ("title", Str()), ("labels", Arr(Str())))),
        },
    });

    /// <summary>Entries of sessions in a table only: no host-keyed document, no live query (upstream <c>Ledger</c>).</summary>
    private static readonly Contract Data160Ledger = Contract.Local("test.ledger", new()
    {
        Version = 1, Params = new(), Members = new() { ["put"] = Method(Str()), ["count"] = Method(Str(), Int()) },
    });

    private static readonly Address Data160S1 = Session.Address(("id", "S1"));

    private static PluginFacet Data160BoardPlugin() => new("@test/board", Facet.Define((facet, _) => facet.Serve(Data160Board, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        var meta = owner.HostKeyedState(Data160Meta);
        var labels = self.Table(Data160Labels);
        var pinned = meta.At(Data160S1);
        var rows = self.Query(labels, Tables.Query(Data160Labels).Where(t => Eq(t["session"], Data160S1)), 10);
        return new(new Dictionary<string, object?>
        {
            ["meta"] = pinned,
            ["labels"] = rows,
            ["summary"] = self.Derive([pinned, rows], values => JsonValue.Create($"{(string?)((JsonNode)values[0]!)["title"]}:{((JsonArray)values[1]!).Count}")),
            ["write"] = (HostMethod)(async (args, ctx) =>
            {
                var given = (JsonNode)args!;
                var address = Session.Address(("id", (string)given["id"]!));
                await self.TxAsync(async tx =>
                {
                    (await tx.DraftAsync(meta, address))["title"] = (string)given["title"]!;
                    foreach (var label in given["labels"]!.AsArray()) tx.Put(labels, new JsonObject { ["session"] = address.ToJson(), ["label"] = (string)label! });
                    return true;
                }, ctx.Context);
                return null;
            }),
        });
    })));

    private static PluginFacet Data160LedgerPlugin() => new("@test/ledger", Facet.Define((facet, _) => facet.Serve(Data160Ledger, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        var entries = self.Table(Data160Entries);
        return new(new Dictionary<string, object?>
        {
            ["put"] = (HostMethod)(async (id, ctx) =>
            {
                var address = Session.Address(("id", ((JsonNode)id!).GetValue<string>()));
                await self.TxAsync(tx =>
                {
                    tx.Put(entries, new JsonObject { ["session"] = address.ToJson(), ["n"] = 1 });
                    return Task.FromResult(true);
                }, ctx.Context);
                return null;
            }),
            ["count"] = (HostMethod)(async (id, ctx) =>
            {
                var address = Session.Address(("id", ((JsonNode)id!).GetValue<string>()));
                return JsonValue.Create((double)await entries.CountAsync(Tables.Query(Data160Entries).Where(t => Eq(t["session"], address)), ctx.Context));
            }),
        });
    })));

    /// <summary>A host of two plugins on <paramref name="store"/>: <c>@test/board</c> serves the board, <c>@test/ledger</c> the ledger;
    /// S1 has a title, a label and a ledger entry (upstream <c>twoPlugins</c>).</summary>
    private static async Task<(ChordHost Host, IReference Board, IReference Ledger)> Data160TwoPlugins(ChordStore store)
    {
        var context = ChordContext.Background;
        var host = Hosts.Create(new() { Store = store, OnError = _ => { } });
        await host.LoadAsync([Data160BoardPlugin(), Data160LedgerPlugin()], context);
        var board = await host.GetAsync(Data160Board, new(), context);
        var ledger = await host.GetAsync(Data160Ledger, new(), context);
        await board.CallAsync("write", new JsonObject { ["id"] = "S1", ["title"] = "one", ["labels"] = new JsonArray("a") }, context);
        await ledger.CallAsync("put", JsonValue.Create("S1"), context);
        return (host, board, ledger);
    }

    private static async Task<double> Data160Count(IReference ledger) =>
        ((JsonNode)(await ledger.CallAsync("count", JsonValue.Create("S1"), ChordContext.Background))!).GetValue<double>();

    // The column sets of the authored cases: a provider's sessions, read by another object's notes.
    private static readonly ColumnSet Data160SessionSet = Sync160Columns("test.sessions.columns", [("id", Str()), ("app", Str())], ["id"]);

    private static readonly TableDefinition Data160Sessions = Sync160Table("sessions", Sync160Spread(Data160SessionSet, ("title", Str())), ["id"], Data160SessionSet);

    private static readonly TableDefinition Data160Notes = Sync160Table("notes", Sync160Schemas(("id", Int()), ("session", Str())), ["id"]);

    private static readonly Contract Data160Provider = Contract.Local("test.sessions", new()
    {
        Version = 1, Params = new(), Members = new() { ["put"] = Method(Obj(("id", Str()), ("app", Str()), ("title", Str()))) },
    });

    private static readonly Contract Data160Reader = Contract.Local("test.notes", new()
    {
        Version = 1, Params = new(), Members = new() { ["club"] = Arr(Unknown()), ["put"] = Method(Obj(("id", Int()), ("session", Str()))) },
    });

    private static HostMethod Data160Put(IStoreSelf self, ITable table) => async (row, ctx) =>
    {
        await self.TxAsync(tx =>
        {
            tx.Put(table, ((JsonNode)row!).DeepClone());
            return Task.FromResult(true);
        }, ctx.Context);
        return null;
    };

    private static PluginFacet Data160ProviderPlugin(TableDefinition sessions) => new("@test/sessions", Facet.Define((facet, _) => facet.Serve(Data160Provider, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        return new(new Dictionary<string, object?> { ["put"] = Data160Put(self, self.Table(sessions)) });
    })));

    /// <summary>The notes whose session is one of the club's, by the provider's column set: its object is live before the reader's own
    /// steps (a dependency), so the set is served when the query first runs.</summary>
    private static PluginFacet Data160ReaderPlugin() => new("@test/notes", Facet.Define((facet, _) => facet.Serve(Data160Reader, (_, _, owner) =>
    {
        var self = (IStoreSelf)owner;
        owner.Get(Data160Provider, new());
        var notes = self.Table(Data160Notes);
        var club = Tables.Query(Data160Notes).Where(n => Exists(Data160SessionSet, Sync160Match(("id", n["session"])), s => Eq(s["app"], "club")));
        return new(new Dictionary<string, object?> { ["club"] = self.Query(notes, club, 10), ["put"] = Data160Put(self, notes) });
    })));

    private static IEnumerable<(string, Func<Task>)> HostDataSync160Cases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("host.host-data.a-delete-limited-to-one-plugin-removes-that-plugins-records-and-rows-of-the-address-in-one-commit-and-leaves-the-others", async () =>
            {
                await using var store = await MemoryStore();
                var (host, board, ledger) = await Data160TwoPlugins(store);
                await using (host)
                {
                    var before = store.LastSeq;
                    Equal(new HostDataCounts(0, 1), await host.DeleteHostDataAsync(Data160S1, context, new HostDeleteOptions("@test/ledger")), "the ledger's row");
                    Equal(before + 1, store.LastSeq, "one commit");
                    Equal(0d, await Data160Count(ledger), "the ledger's entry is gone");
                    // The board's plugin keeps its document and its rows of the address.
                    Json("""{"title":"one"}""", board.Member("meta").Value, "the board's document");
                    Sequence(["a"], ((JsonArray)board.Member("labels").Value!).Select(row => (string)row!["label"]!), "the board's rows");
                }
            }),
            Case("host.host-data.a-delete-limited-to-one-plugin-runs-the-reads-over-its-tables-again-the-owners-live-query-and-pinned-key-follow-in-that-commit", async () =>
            {
                await using var store = await MemoryStore();
                var (host, board, ledger) = await Data160TwoPlugins(store);
                await using (host)
                {
                    var frames = FramesOf(await board.Member("summary").WatchAsync(context));
                    Equal(new HostDataCounts(1, 1), await host.DeleteHostDataAsync(Data160S1, context, new HostDeleteOptions("@test/board")), "the board's document and row");
                    await Flushed();
                    Json("""{"title":""}""", board.Member("meta").Value, "the pinned key reads its initial value");
                    Equal(0, ((JsonArray)board.Member("labels").Value!).Count, "the live query follows");
                    // One frame: the document and the query adopted in the delete's batch.
                    Sequence([":0"], frames.Select(frame => (string)frame.Value!), "one frame of the derive over both");
                    Equal(1d, await Data160Count(ledger), "the ledger keeps its entry");
                }
            }),
            Case("host.host-data.a-delete-limited-to-one-plugin-refuses-a-plugin-that-is-not-loaded-here-and-changes-nothing", async () =>
            {
                await using var store = await MemoryStore();
                var (host, _, ledger) = await Data160TwoPlugins(store);
                await using (host)
                {
                    var before = store.LastSeq;
                    var refused = await Rejection(host.DeleteHostDataAsync(Data160S1, context, new HostDeleteOptions("@test/gone")));
                    Rejected(refused, "invalid", "args", "a plugin not loaded");
                    Equal("deleteHostData: plugin \"@test/gone\" is not loaded here", (string?)((JsonObject)refused)["message"], "the message");
                    Equal(before, store.LastSeq, "no commit");
                    Equal(1d, await Data160Count(ledger), "the ledger keeps its entry");
                }
            }),
            Case("host.host-data.160-a-column-set-is-bound-at-activation-another-objects-query-over-it-follows-its-owners-commits-and-it-is-dropped-at-close", async () =>
            {
                await using var store = await MemoryStore();
                var errors = new List<HostError>();
                var host = Hosts.Create(new() { Store = store, OnError = error => { lock (errors) errors.Add(error); } });
                await host.LoadAsync([Data160ProviderPlugin(Data160Sessions), Data160ReaderPlugin()], context);
                var reader = await host.GetAsync(Data160Reader, new(), context);
                var provider = await host.GetAsync(Data160Provider, new(), context);
                Equal("test.sessions:sessions", store.ReadIndex.Get(Data160SessionSet.Name)?.Table, "the set is bound to its owner's table");
                Check(!store.ReadIndex.IsHeld("test.sessions:sessions"), "a prepared table is ready");
                IEnumerable<int> Club() => ((JsonArray)reader.Member("club").Value!).Select(row => (int)row!["id"]!.GetValue<double>());
                await provider.CallAsync("put", new JsonObject { ["id"] = "s1", ["app"] = "club", ["title"] = "one" }, context);
                await reader.CallAsync("put", new JsonObject { ["id"] = 1, ["session"] = "s1" }, context);
                await reader.CallAsync("put", new JsonObject { ["id"] = 2, ["session"] = "s2" }, context);
                Sequence([1], Club(), "the note of a club session");
                // Another object's commit: the read is registered under the provider's table too, and follows in that commit's batch.
                await provider.CallAsync("put", new JsonObject { ["id"] = "s2", ["app"] = "club", ["title"] = "two" }, context);
                Sequence([1, 2], Club(), "the provider's commit brought a note in");
                await provider.CallAsync("put", new JsonObject { ["id"] = "s1", ["app"] = "family", ["title"] = "one" }, context);
                Sequence([2], Club(), "the provider's commit took a note out");
                Equal(0, errors.Count, "nothing failed: " + string.Join("; ", errors.Select(error => $"{error.Kind} {error.Error?.Message}")));
                reader.Dispose();
                provider.Dispose();
                await host.DisposeAsync();
                Check(store.ReadIndex.Get(Data160SessionSet.Name) is null, "a closed host serves no set");
                Check(store.ReadIndex.On("test.sessions:sessions") is null && store.ReadIndex.On("test.notes:notes") is null, "its reads left the index");
            }),
            Case("host.host-data.160-a-reload-whose-table-no-longer-exposes-a-set-drops-its-binding", async () =>
            {
                await using var store = await MemoryStore();
                await using var host = Hosts.Create(new() { Store = store, OnError = _ => { } });
                await host.LoadAsync([Data160ProviderPlugin(Data160Sessions)], context);
                using var provider = await host.GetAsync(Data160Provider, new(), context);
                Check(store.ReadIndex.Get(Data160SessionSet.Name) is not null, "bound by the first code");
                // The same table (exposes is no part of its fingerprint), exposing nothing.
                var hidden = Sync160Table("sessions", Sync160Spread(Data160SessionSet, ("title", Str())), ["id"]);
                var report = await host.ReloadAsync([Data160ProviderPlugin(hidden)], context);
                Equal(0, report.Failed.Count, "the reload is taken");
                await Sync160Until(() => store.ReadIndex.Get(Data160SessionSet.Name) is null, "the new attempt drops the set its object had bound");
            }),
            Case("host.host-data.160-one-object-exposing-a-set-by-two-tables-is-refused-naming-both", async () =>
            {
                await using var store = await MemoryStore();
                await using var host = Hosts.Create(new() { Store = store, OnError = _ => { } });
                var twice = Sync160Table("sessions2", Sync160Spread(Data160SessionSet), ["id"], Data160SessionSet);
                PluginFacet Twice() => new("@test/sessions", Facet.Define((facet, _) => facet.Serve(Data160Provider, (_, _, owner) =>
                {
                    var self = (IStoreSelf)owner;
                    var first = self.Table(Data160Sessions);
                    self.Table(twice);
                    return new(new Dictionary<string, object?> { ["put"] = Data160Put(self, first) });
                })));
                await host.LoadAsync([Twice()], context);
                Rejected(await Rejection(host.GetAsync(Data160Provider, new(), context)), null, null, "the activation is refused",
                    "column set \"test.sessions.columns\" is exposed by tables \"sessions\" and \"sessions2\"");
                // Staging refuses it too: a reload keeps the code it had.
                await using var other = await MemoryStore();
                await using var reloaded = Hosts.Create(new() { Store = other, OnError = _ => { } });
                await reloaded.LoadAsync([Data160ProviderPlugin(Data160Sessions)], context);
                using var provider = await reloaded.GetAsync(Data160Provider, new(), context);
                var report = await reloaded.ReloadAsync([Twice()], context);
                Equal(1, report.Failed.Count, "the reload's staging refuses the plugin");
                Sync160Contains("is exposed by tables \"sessions\" and \"sessions2\"", report.Failed[0].Error.Message, "why");
                Equal("live", provider.State.Value.Kind, "the object keeps its old code");
            }),
        ];
    }
}
