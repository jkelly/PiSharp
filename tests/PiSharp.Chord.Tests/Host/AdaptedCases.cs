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
