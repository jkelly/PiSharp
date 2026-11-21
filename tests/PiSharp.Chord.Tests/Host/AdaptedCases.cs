using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostAdaptedCases() =>
    [
        Case("host.adapted.swap-retains-document-slots-and-maps-old-state-and-results", async () =>
        {
            var v1 = Contract.Local("reload.counter", new() { Version = 1, Params = new(), Members = new() { ["count"] = Int(), ["read"] = Method(null, Int()), ["add"] = Method() } });
            var v2 = Contract.Evolve(v1, edit => edit.State("count", Str()).Result("read", Str()));
            var oldDefinition = StateDefinition.Define("counter", new() { Version = 1, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 3 } });
            var newDefinition = StateDefinition.Define("counter", new()
            {
                Version = 2, Schema = Obj(("count", Str())), Initial = () => new() { ["count"] = "0" },
                Migrate = (old, _) => new() { ["count"] = old["count"]!.GetValue<int>().ToString() },
            });
            ISingletonHandle? originalHandle = null;
            await using var host = H2Host();
            var old = new PluginFacet("counter", Facet.Define((h, _) => h.Serve(v1, (_, _, self) =>
            {
                var state = self.State(oldDefinition); originalHandle ??= state;
                return new(new Dictionary<string, object?>
                {
                    ["count"] = self.Derive([state], values => ((JsonNode)values[0]!)["count"]!.DeepClone()),
                    ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(state.Value!["count"]!.DeepClone())),
                    ["add"] = (HostMethod)(async (_, ctx) => { await state.ChangeAsync(draft => draft["count"] = draft["count"]!.GetValue<int>() + 1, ctx.Context); return null; }),
                });
            })));
            await host.LoadAsync([old], ChordContext.Background);
            using var oldReference = await host.GetAsync(v1, new(), ChordContext.Background);
            await oldReference.CallAsync("add", null, ChordContext.Background);
            Equal(4, oldReference.Member("count").Value!.GetValue<int>(), "old count updated");
            var slots = host.Dispatcher.Invoke(() => host.Objects.Values.Single().Documents);
            var fresh = new PluginFacet("counter", Facet.Define((h, _) => h.Serve(v2, (_, _, self) =>
            {
                var state = self.State(newDefinition);
                return new(new Dictionary<string, object?>
                {
                    ["count"] = self.Derive([state], values => ((JsonNode)values[0]!)["count"]!.DeepClone()),
                    ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(state.Value!["count"]!.DeepClone())),
                    ["add"] = (HostMethod)(async (_, ctx) => { await state.ChangeAsync(draft => draft["count"] = (int.Parse(draft["count"]!.GetValue<string>()) + 1).ToString(), ctx.Context); return null; }),
                });
            }, new(new AcceptsTable { [1] = new()
            {
                State = new() { ["count"] = new StateMapping(value => JsonValue.Create(int.Parse(((JsonNode)value!).GetValue<string>()))) },
                Methods = new() { ["read"] = new() { Result = value => JsonValue.Create(int.Parse(((JsonNode)value!).GetValue<string>())) } },
            } }))));
            var report = await host.ReloadAsync([fresh], ChordContext.Background);
            await host.DrainReloadsAsync(ChordContext.Background);
            Equal("restarted", report.Objects.Single().Fate, "state migration restarts while preserving slots");
            await host.DrainReloadsAsync(ChordContext.Background);
            Check(ReferenceEquals(slots, host.Dispatcher.Invoke(() => host.Objects.Values.Single().Documents)), "document slot container retained");
            Equal(4, oldReference.Member("count").Value!.GetValue<int>(), "state mapping preserves v1 view");
            Equal(4, ((JsonNode)(await oldReference.CallAsync("read", null, ChordContext.Background))!).GetValue<int>(), "result mapping preserves v1 view");
            using var current = await host.GetAsync(v2, new(), ChordContext.Background);
            Equal("4", current.Member("count").Value!.GetValue<string>(), "new view uses migrated persisted slot");
            await current.CallAsync("add", null, ChordContext.Background);
            Equal(5, oldReference.Member("count").Value!.GetValue<int>(), "old mapped state advances with same slot");
            var fence = Throws<ChordError>(() => _ = originalHandle!.Value, "old declaration cannot read migrated version");
            Equal("version", fence.Reason, "document version fence");
        }),
        Case("host.adapted.removed-member-maps-whole-current-state-and-shares-derive", async () =>
        {
            var v1 = Contract.Local("reload.profile", new() { Version = 1, Params = new(), Members = new() { ["name"] = Str() } });
            var v2 = Contract.Evolve(v1, edit => edit.RemoveState("name").AddState("first", Str()).AddState("last", Str()));
            var mappings = 0;
            await using var host = H2Host();
            var old = new PluginFacet("profile", Facet.Define((h, _) => h.Serve(v1, (_, _, _) => new(new Dictionary<string, object?> { ["name"] = Watchables.Constant(JsonValue.Create("Ada Lovelace")) }))));
            await host.LoadAsync([old], ChordContext.Background);
            using var one = await host.GetAsync(v1, new(), ChordContext.Background); using var two = await host.GetAsync(v1, new(), ChordContext.Background);
            var fresh = new PluginFacet("profile", Facet.Define((h, _) => h.Serve(v2, (_, _, _) => new(new Dictionary<string, object?>
            { ["first"] = Watchables.Constant(JsonValue.Create("Ada")), ["last"] = Watchables.Constant(JsonValue.Create("Lovelace")) }), new(new AcceptsTable
            { [1] = new() { State = new() { ["name"] = new StateMapping(value => { mappings++; var state = (JsonObject)value!; return JsonValue.Create(state["first"]!.GetValue<string>() + " " + state["last"]!.GetValue<string>()); }) } } }))));
            await host.ReloadAsync([fresh], ChordContext.Background);
            await host.DrainReloadsAsync(ChordContext.Background);
            Equal("Ada Lovelace", one.Member("name").Value!.GetValue<string>(), "removed member maps whole state");
            Equal("Ada Lovelace", two.Member("name").Value!.GetValue<string>(), "second reader sees same view");
            Equal(1, mappings, "one mapping derive shared by both references");
        }),
        Case("host.adapted.refused-state-unbinds-older-consumer", async () =>
        {
            var v1 = Contract.Local("reload.removed", new() { Version = 1, Params = new(), Members = new() { ["name"] = Str() } });
            var v2 = Contract.Evolve(v1, edit => edit.RemoveState("name"));
            await using var host = H2Host();
            var old = new PluginFacet("profile", Facet.Define((h, _) => h.Serve(v1, (_, _, _) => new(new Dictionary<string, object?> { ["name"] = Watchables.Constant(JsonValue.Create("Ada")) }))));
            await host.LoadAsync([old], ChordContext.Background);
            using var reference = await host.GetAsync(v1, new(), ChordContext.Background);
            var fresh = new PluginFacet("profile", Facet.Define((h, _) => h.Serve(v2, (_, _, _) => new(new Dictionary<string, object?>()),
                new(new AcceptsTable { [1] = new() { State = new() { ["name"] = StateMapping.Refuse } } }))));
            await host.ReloadAsync([fresh], ChordContext.Background);
            await host.DrainReloadsAsync(ChordContext.Background);
            var error = Throws<ChordError>(() => _ = reference.Member("name").Value, "refused old member");
            Equal("contract_mismatch", error.Code, "explicit refusal surfaces contract mismatch");
        }),
        Case("host.adapted.recording-rejects-version-rollback-without-mutating-slot", async () =>
        {
            var type = H2Type(); var v2 = StateDefinition.Define("versioned", new() { Version = 2, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 17 } });
            var v1 = StateDefinition.Define("versioned", new() { Version = 1, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 1 } });
            await using var host = H2Host();
            PluginFacet With(StateDefinition definition) => new("item", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            { var state = self.State(definition); return new(new Dictionary<string, object?> { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(state.Value!["count"]!.ToString()))) }); })));
            await host.LoadAsync([With(v2)], ChordContext.Background);
            var result = await host.ReloadAsync([With(v1)], ChordContext.Background);
            Equal(1, result.Failed.Count, "rollback refused at staging");
            using var reference = await host.GetAsync(type, new(), ChordContext.Background); Equal("17", await H2Read(reference), "old value untouched");
        }),
        Case("host.adapted.keyed-watchers-share-mapping-and-release-last-key", async () =>
        {
            var v1 = Contract.Local("reload.keyed", new() { Version = 1, Params = new(), Members = new() { ["rows"] = Keyed(Obj(("count", Int()))), ["seed"] = Method() } });
            var v2 = Contract.Evolve(v1, edit => edit.Keyed("rows", Obj(("count", Str()))));
            var d1 = StateDefinition.Define("rows", new() { Version = 1, Keyed = true, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 0 } });
            var d2 = StateDefinition.Define("rows", new()
            {
                Version = 2, Keyed = true, Schema = Obj(("count", Str())), Initial = () => new() { ["count"] = "0" },
                Migrate = (old, _) => new() { ["count"] = old["count"]!.GetValue<int>().ToString() },
            });
            var maps = 0; await using var host = H2Host();
            PluginFacet Plugin(Contract contract, StateDefinition definition, ServeOptions? accepts = null) => new("keyed", Facet.Define((h, _) => h.Serve(contract, (_, _, self) =>
            {
                var rows = self.KeyedState(definition);
                return new(new Dictionary<string, object?> { ["rows"] = rows, ["seed"] = (HostMethod)(async (_, ctx) =>
                {
                    await rows.ChangeAsync("one", row => row["count"] = definition.Version == 1 ? JsonValue.Create(5) : JsonValue.Create("6"), ctx.Context);
                    return null;
                }) });
            }, accepts)));
            await host.LoadAsync([Plugin(v1, d1)], ChordContext.Background);
            using var old = await host.GetAsync(v1, new(), ChordContext.Background);
            await old.CallAsync("seed", null, ChordContext.Background);
            await host.ReloadAsync([Plugin(v2, d2, new(new AcceptsTable { [1] = new() { Keyed = new() { ["rows"] = new StateMapping(value =>
            { maps++; return new JsonObject { ["count"] = int.Parse(((JsonObject)value!)["count"]!.GetValue<string>()) }; }) } } }))], ChordContext.Background);
            using var other = await host.GetAsync(v1, new(), ChordContext.Background);
            var first = (await old.KeyedMember("rows").WatchAsync("one", ChordContext.Background))!;
            var second = (await other.KeyedMember("rows").WatchAsync("one", ChordContext.Background))!;
            Equal(1, maps, "mapping computed once for two readers"); Equal(5, first.Value!["count"]!.GetValue<int>(), "mapped document value");
            await first.StopAsync(); await second.StopAsync();
            var again = (await old.KeyedMember("rows").WatchAsync("one", ChordContext.Background))!;
            Equal(2, maps, "last watcher releases shared mapping"); await again.StopAsync();
        }),
        Case("host.adapted.reload-rechecks-same-version-fingerprint", async () =>
        {
            var before = Contract.Local("reload.fingerprint", new() { Version = 1, Params = new(), Members = new() { ["value"] = Str() } });
            var after = Contract.Local("reload.fingerprint", new() { Version = 1, Params = new(), Members = new() { ["value"] = Int() } });
            await using var host = H2Host();
            PluginFacet Plugin(PiSharp.Chord.Contracts.Contract contract, JsonNode value) => new("item", Facet.Define((h, _) => h.Serve(contract, (_, _, _) =>
                new(new Dictionary<string, object?> { ["value"] = Watchables.Constant(value) }))));
            await host.LoadAsync([Plugin(before, JsonValue.Create("old"))], ChordContext.Background);
            using var old = await host.GetAsync(before, new(), ChordContext.Background);
            await host.ReloadAsync([Plugin(after, JsonValue.Create(4))], ChordContext.Background);
            await R1Until(() => old.State.Value.Kind == "failed");
            Equal("failed", old.State.Value.Kind, "old fingerprint no longer binds");
            Equal("contract_mismatch", old.State.Value.Cause!.Error.Code, "fingerprint mismatch classified");
            using var current = await host.GetAsync(after, new(), ChordContext.Background);
            Equal(4, current.Member("value").Value!.GetValue<int>(), "new fingerprint binds");
        }),
    ];
}

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/adapted.test.ts, "events of an older consumer" (decision
// "Events: fire-and-forget members"). Ported by reading; the upstream turn and flush are S6Turn and S6Flushed (Sync160RemoteSupport.cs).
internal static partial class Program
{
    private static readonly JsonObject A6Sample = Obj(("y", Int()));
    /// <summary>v1: one event member; v2 adds a sample time to its values and a second member.</summary>
    private static readonly Contract A6TickerV1 = Contract.Local("app.ticker", new() { Version = 1, Params = new(), Members = new() { ["ticks"] = Event(A6Sample) } });
    private static readonly Contract A6TickerV2 = Contract.Evolve(A6TickerV1, e => e.Event("ticks", Obj(("y", Int()), ("t", Int()))).AddEvent("extra", A6Sample));

    /// <summary>Serves v2 accepting v1 with <c>ticks</c> mapped by <paramref name="mapping"/>, or refused (null); its sources in the list.</summary>
    private static async Task<(ChordHost Host, List<HostError> Errors, Action<int> Emit, List<IEventSource> Sources)> A6Ticker(Func<JsonNode, JsonNode?>? mapping)
    {
        var sources = new List<IEventSource>(); var errors = new List<HostError>();
        var host = Hosts.Create(new() { OnError = error => { lock (errors) errors.Add(error); }, ReadyTimeout = TimeSpan.FromSeconds(3), CloseTimeout = TimeSpan.FromMilliseconds(200) });
        StateEntry entry = mapping is null ? StateMapping.Refuse : new StateMapping(value => mapping((JsonNode)value!));
        await host.LoadAsync([new("@test/app", Facet.Define((h, _) => h.Serve(A6TickerV2, (_, _, self) =>
        {
            var ticks = self.Events(); lock (sources) sources.Add(ticks);
            return new(new Dictionary<string, object?> { ["ticks"] = ticks, ["extra"] = self.Events() });
        }, new(new AcceptsTable { [1] = new() { Events = { ["ticks"] = entry } } }))))], ChordContext.Background);
        return (host, errors, y => { IEventSource last; lock (sources) last = sources[^1]; last.Emit(new JsonObject { ["y"] = y, ["t"] = y * 10 }); }, sources);
    }

    private static IEnumerable<(string, Func<Task>)> HostAdapted160EventsCases() =>
    [
        Case("host.adapted.events-of-an-older-consumer-get-each-value-mapped-by-their-versions-mapping-once-per-emit-for-all-of-them", async () =>
        {
            var calls = 0;
            var (host, _, emit, _) = await A6Ticker(value => { calls++; return new JsonObject { ["y"] = value["y"]!.DeepClone() }; });
            await using var closing = host; var seen = new List<string>();
            for (var reference = 0; reference < 3; reference++)
            {
                var old = await host.GetAsync(A6TickerV1, new(), ChordContext.Background);
                await old.Event("ticks").OnAsync(e => { lock (seen) seen.Add(e.Value!.ToJsonString()); }, ChordContext.Background);
            }
            var current = await host.GetAsync(A6TickerV2, new(), ChordContext.Background);
            await current.Event("ticks").OnAsync(e => { lock (seen) seen.Add(e.Value!.ToJsonString()); }, ChordContext.Background);
            emit(1);
            await S6Flushed(host);
            Sequence(["{\"y\":1}", "{\"y\":1}", "{\"y\":1}", "{\"y\":1,\"t\":10}"], S6Seen(seen), "three mapped, one current");
            Equal(1, calls, "mapped once for all of the version's bindings");
        }),
        Case("host.adapted.events-of-an-older-consumer-drop-an-emit-only-for-their-version-when-the-mapping-throws-reported-once-per-episode", async () =>
        {
            var (host, errors, emit, _) = await A6Ticker(value =>
            {
                var y = value["y"]!.GetValue<int>();
                if (y < 0) throw new InvalidOperationException("no " + y);
                return new JsonObject { ["y"] = y };
            });
            await using var closing = host; var old = new List<string>(); var current = new List<string>();
            await (await host.GetAsync(A6TickerV1, new(), ChordContext.Background)).Event("ticks").OnAsync(S6Recorder(old), ChordContext.Background);
            await (await host.GetAsync(A6TickerV2, new(), ChordContext.Background)).Event("ticks").OnAsync(S6Recorder(current), ChordContext.Background);
            foreach (var y in new[] { -1, -2, 3, -4 }) { emit(y); await S6Flushed(host); }
            Sequence(["3"], S6Seen(old), "the older version gets only what its mapping took");
            Sequence(["-1", "-2", "3", "-4"], S6Seen(current), "the current version gets every emit");
            HostError[] reported; lock (errors) reported = [.. errors];
            Sequence(["event_mapping_failed", "event_mapping_failed"], reported.Select(error => error.Kind), "once per failure episode");
            Equal("app.ticker", reported[0].Object, "the object"); Equal("ticks", reported[0].Member, "the member"); Equal(1L, reported[0].Version, "the version");
            Equal("no -1", reported[0].Error!.Message, "the first episode's error"); Equal("no -4", reported[1].Error!.Message, "the next episode's error");
        }),
        Case("host.adapted.events-of-an-older-consumer-are-refused-their-binding-when-the-provider-refuses-a-changed-event-member", async () =>
        {
            var (host, _, _, _) = await A6Ticker(null); await using var closing = host;
            var error = await S6Rejection(host.GetAsync(A6TickerV1, new(), ChordContext.Background, new(TimeSpan.FromMilliseconds(20))));
            Equal("contract_mismatch", ((ChordError)error).Code, "the older version's binding is refused");
            using var current = await host.GetAsync(A6TickerV2, new(), ChordContext.Background);
            Check(current.Event("ticks") is not null, "the current version binds");
        }),
        Case("host.adapted.events-of-an-older-consumer-bind-with-a-removed-event-member-and-receive-none-of-it-the-unchanged-ones-as-they-are", async () =>
        {
            var pulseV1 = Contract.Local("app.pulse", new() { Version = 1, Params = new(), Members = new() { ["beat"] = Event(A6Sample), ["old"] = Event(A6Sample) } });
            var pulseV2 = Contract.Evolve(pulseV1, e => e.RemoveEvent("old"));
            var beats = new List<IEventSource>();
            await using var host = Hosts.Create(new() { OnError = _ => { }, ReadyTimeout = TimeSpan.FromSeconds(3), CloseTimeout = TimeSpan.FromMilliseconds(200) });
            await host.LoadAsync([new("@test/app", Facet.Define((h, _) => h.Serve(pulseV2, (_, _, self) =>
            {
                var beat = self.Events(); lock (beats) beats.Add(beat);
                return new(new Dictionary<string, object?> { ["beat"] = beat });
            }, new(new AcceptsTable { [1] = new() }))))], ChordContext.Background);
            using var older = await host.GetAsync(pulseV1, new(), ChordContext.Background);
            var seen = new List<string>();
            await older.Event("beat").OnAsync(S6Recorder(seen, "beat "), ChordContext.Background);
            await older.Event("old").OnAsync(S6Recorder(seen, "old "), ChordContext.Background);
            IEventSource last; lock (beats) last = beats[^1];
            last.Emit(new JsonObject { ["y"] = 1 });
            await S6Flushed(host);
            Sequence(["beat 1"], S6Seen(seen), "the unchanged member as it is, none of the removed one");
            Equal("live", older.State.Value.Kind, "bound all the same");
        }),
        Case("host.adapted.events-of-an-older-consumer-keep-no-mapping-once-their-versions-last-reference-left", async () =>
        {
            // Upstream asserts the dropped mapping nodes are collected. Here the same thing is read directly: once the version's last
            // reference left, nothing taps the instance's source any more and the mapping is not run for the next emit.
            var calls = 0;
            var (host, _, emit, sources) = await A6Ticker(value => { calls++; return new JsonObject { ["y"] = value["y"]!.DeepClone() }; });
            await using var closing = host;
            var old = await host.GetAsync(A6TickerV1, new(), ChordContext.Background);
            await old.Event("ticks").OnAsync(_ => { }, ChordContext.Background);
            emit(1);
            await S6Flushed(host);
            Equal(1, calls, "mapped while the older reference listened");
            var source = (OwnEventSource)sources[^1];
            Check(host.Dispatcher.Invoke(() => source.Tapped), "the mapping taps the instance's source");
            old.Release();
            await S6Flushed(host);
            Check(!host.Dispatcher.Invoke(() => source.Tapped), "the mapping is gone with the version's last reference");
            emit(2);
            await S6Flushed(host);
            Equal(1, calls, "nothing is mapped for nobody");
        }),
    ];
}
