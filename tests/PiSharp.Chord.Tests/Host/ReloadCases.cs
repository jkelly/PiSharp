using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using static PiSharp.Chord.Contracts.Schemas;

// Expectations authored from next/host/{staging,adapted}.test.ts and examples 33-34, pinned v1.5.0.
internal static partial class Program
{
    private static Contract H2Type(string name = "reload.item") => Contract.Local(name, new()
    { Version = 1, Params = new(), Members = new() { ["read"] = Method(null, Str()) } });
    private static HostImplementation H2Read(string value) => new(new Dictionary<string, object?> { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(value))) });
    private static PluginFacet H2Plugin(Contract type, string value, string name = "item") => new(name, Facet.Define((host, _) => host.Serve(type, (_, _, _) => H2Read(value))));
    private static ChordHost H2Host(List<HostError>? errors = null) => Hosts.Create(new()
    { OnError = error => errors?.Add(error), ReadyTimeout = TimeSpan.FromSeconds(3), CloseTimeout = TimeSpan.FromMilliseconds(200) });
    private static async Task<string> H2Read(IReference reference) => (await reference.CallAsync("read", null, ChordContext.Background) as JsonNode)!.GetValue<string>();
    private static TaskCompletionSource H2Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IEnumerable<(string, Func<Task>)> HostReloadCases() =>
    [
        Case("host.reload.prepublication-get-and-atomic-failed-setup", async () =>
        {
            var type = H2Type(); await using var host = H2Host();
            var get = host.GetAsync(type, new(), ChordContext.Background);
            var good = H2Plugin(type, "old");
            var failed = new PluginFacet("broken", Facet.Define((facet, _) => { facet.Serve(H2Type("reload.partial"), (_, _, _) => H2Read("partial")); throw new Exception("setup stopped"); }));
            var loaded = await host.LoadAsync([good, failed], ChordContext.Background);
            using var reference = await get;
            Equal("old", await H2Read(reference), "preload request resolves");
            Equal(1, loaded.Failed.Count, "setup reported");
            Check(!host.Types.ContainsKey("reload.partial"), "failed setup publishes no declarations");
        }),
        Case("host.reload.same-facet-is-unchanged", async () =>
        {
            var type = H2Type(); var factories = 0; await using var host = H2Host();
            var facet = new PluginFacet("item", Facet.Define((h, _) => h.Serve(type, (_, _, _) => { factories++; return H2Read("same"); })));
            await host.LoadAsync([facet], ChordContext.Background);
            var report = await host.ReloadAsync([facet], ChordContext.Background);
            Equal(1, factories, "unchanged factory not recorded or replaced"); Equal(0, report.Plugins.Changed.Count, "unchanged set");
        }),
        Case("host.reload.setup-failure-retains-previous-code", async () =>
        {
            var type = H2Type(); await using var host = H2Host();
            await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var report = await host.ReloadAsync([new("item", Facet.Define((_, _) => throw new Exception("build failure")))], ChordContext.Background);
            Equal("old", await H2Read(reference), "previous code remains callable");
            Equal(1, report.Failed.Count, "failed plugin reported"); Equal(0, report.Plugins.Changed.Count, "failure not committed");
        }),
        Case("host.reload.factory-failure-is-transactional", async () =>
        {
            var type = H2Type(); var opens = 0; await using var host = H2Host();
            await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var broken = new PluginFacet("item", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                self.Open(_ => { opens++; return Task.FromResult(7); }, (_, _, _) => Task.CompletedTask);
                throw new Exception("factory failed after declaration");
            })));
            var result = await host.ReloadAsync([broken], ChordContext.Background);
            Equal(0, opens, "recording never opens native resources"); Equal(1, result.Failed.Count, "factory failure reported");
            Equal("old", await H2Read(reference), "factory rollback retains old code");
        }),
        Case("host.reload.swap-preserves-old-call-and-incarnation", async () =>
        {
            var type = H2Type(); var started = H2Signal(); var finish = H2Signal(); await using var host = H2Host();
            var old = new PluginFacet("item", Facet.Define((h, _) => h.Serve(type, (_, _, _) => new(new Dictionary<string, object?>
            { ["read"] = (HostMethod)(async (_, _) => { started.TrySetResult(); await finish.Task; return JsonValue.Create("old"); }) }))));
            await host.LoadAsync([old], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var incarnation = host.Dispatcher.Invoke(() => host.Objects.Values.Single().Current!.InstanceId);
            var call = H2Read(reference); await started.Task;
            var report = await host.ReloadAsync([H2Plugin(type, "new")], ChordContext.Background);
            Equal("swapped", report.Objects.Single().Fate, "pure factory swaps");
            Equal("new", await H2Read(reference), "new calls use new code");
            Equal(incarnation, host.Dispatcher.Invoke(() => host.Objects.Values.Single().Current!.InstanceId), "swap retains incarnation");
            Check(!call.IsCompleted, "old call not cancelled by publication"); finish.TrySetResult(); Equal("old", await call, "old result preserved");
            await host.DrainReloadsAsync(ChordContext.Background);
            Equal(0, host.Dispatcher.Invoke(() => host.Objects.Values.Single().OldAttempts.Count), "old attempt released after drain");
        }),
        Case("host.reload.native-restart-drains-before-close-and-open", async () =>
        {
            var type = H2Type(); var log = new List<string>(); var started = H2Signal(); var finish = H2Signal(); await using var host = H2Host();
            PluginFacet Plugin(string version) => new("item", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                self.Open(_ => { log.Add("open " + version); return Task.FromResult(version); }, (_, reason, _) => { log.Add("close " + version + " " + reason); return Task.CompletedTask; });
                return new(new Dictionary<string, object?> { ["read"] = (HostMethod)(async (_, _) => { if (version == "old") { started.TrySetResult(); await finish.Task; } return JsonValue.Create(version); }) });
            })));
            await host.LoadAsync([Plugin("old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var incarnation = host.Dispatcher.Invoke(() => host.Objects.Values.Single().Current!.InstanceId);
            var call = H2Read(reference); await started.Task;
            var report = await host.ReloadAsync([Plugin("new")], ChordContext.Background);
            Equal("restarted", report.Objects.Single().Fate, "native restarts"); Sequence(["open old"], log, "no staging native effects");
            finish.TrySetResult(); Equal("old", await call, "old call drains");
            Equal("new", await H2Read(reference), "new callable after native reopened");
            Sequence(["open old", "close old Reload", "open new"], log, "native close/open order");
            Check(incarnation != host.Dispatcher.Invoke(() => host.Objects.Values.Single().Current!.InstanceId), "native restart changes incarnation");
        }),
        Case("host.reload.precommit-cancellation-writes-nothing", async () =>
        {
            var type = H2Type(); await using var host = H2Host(); using var cancellation = new CancellationTokenSource();
            await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var fresh = new PluginFacet("item", Facet.Define((h, _) => { h.Serve(type, (_, _, _) => H2Read("new")); cancellation.Cancel(); }));
            try { await host.ReloadAsync([fresh], ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background)); throw new Exception("expected cancellation"); }
            catch (ChordError error) { Equal("aborted", error.Code, "aborted code"); Check(error.Details is null, "no committed details"); }
            Equal("old", await H2Read(reference), "old code after cancelled staging");
        }),
        Case("host.reload.postcommit-cancellation-reports-applied-set", async () =>
        {
            var type = H2Type(); await using var host = H2Host(); using var cancellation = new CancellationTokenSource(); var passes = 0;
            await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            var fresh = new PluginFacet("item", Facet.Define((h, _) => h.Serve(type, (_, _, _) => { if (++passes == 2) cancellation.Cancel(); return H2Read("new"); })));
            try { await host.ReloadAsync([fresh], ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background)); throw new Exception("expected cancellation"); }
            catch (ChordError error) { Equal("aborted", error.Code, "aborted code"); Json("[\"item\"]", error.Details!["committed"]!["changed"], "applied plugin set"); }
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            Equal("new", await H2Read(reference), "commit remains applied");
        }),
        Case("host.reload.external-failed-plugin-is-retained", async () =>
        {
            var type = H2Type(); await using var host = H2Host(); await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            var report = await host.ReloadAsync([], ChordContext.Background, new([new("item", new Exception("compile error"))]));
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            Equal("old", await H2Read(reference), "failed build retains loaded generation even if absent from prepared list");
            Equal(0, report.Plugins.Removed.Count, "failed plugin not removed");
        }),
        Case("host.reload.removed-provider-rebinds-same-reference", async () =>
        {
            var type = H2Type(); await using var host = H2Host(); await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var removed = await host.ReloadAsync([], ChordContext.Background);
            Equal("ended", removed.Objects.Single().Fate, "object ended"); Check(reference.State.Value.Kind != "ended", "request remains waiting");
            await host.ReloadAsync([H2Plugin(type, "returned")], ChordContext.Background);
            Equal("returned", await H2Read(reference), "same request rebinds after provider returns");
        }),
        Case("host.reload.retained-consumer-protects-compatible-provider", async () =>
        {
            var providerV1 = H2Type("reload.provider"); var providerV2 = Contract.Evolve(providerV1, edit => edit.Result("read", Int()));
            var consumer = H2Type("reload.consumer"); await using var host = H2Host();
            var usesV1 = new PluginFacet("consumer", Facet.Define((h, _) => h.Serve(consumer, (_, _, self) => { self.Get(providerV1, new()); return H2Read("consumer"); })));
            await host.LoadAsync([H2Plugin(providerV1, "old", "provider"), usesV1], ChordContext.Background);
            var incompatible = new PluginFacet("provider", Facet.Define((h, _) => h.Serve(providerV2, (_, _, _) => new(new Dictionary<string, object?> { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(2))) }))));
            var report = await host.ReloadAsync([incompatible, usesV1], ChordContext.Background);
            Check(report.Failed.Any(failure => failure.Plugin == "provider"), "retained consumer compatibility checked");
            using var reference = await host.GetAsync(providerV1, new(), ChordContext.Background); Equal("old", await H2Read(reference), "compatible old provider retained");
        }),
        Case("host.reload.failed-provider-closes-over-new-consumer", async () =>
        {
            var providerV1 = H2Type("reload.provider"); var providerV2 = Contract.Evolve(providerV1, edit => edit.Result("read", Int()));
            var consumer = H2Type("reload.consumer"); await using var host = H2Host();
            PluginFacet Uses(Contract provider, string value) => new("consumer", Facet.Define((h, _) => h.Serve(consumer, (_, _, self) => { self.Get(provider, new()); return H2Read(value); })));
            await host.LoadAsync([H2Plugin(providerV1, "old", "provider"), Uses(providerV1, "old consumer")], ChordContext.Background);
            var report = await host.ReloadAsync([new("provider", Facet.Define((_, _) => throw new Exception("bad setup"))), Uses(providerV2, "new consumer")], ChordContext.Background);
            Sequence(["provider", "consumer"], report.Failed.Select(failure => failure.Plugin), "compatibility closure failures");
            using var reference = await host.GetAsync(consumer, new(), ChordContext.Background); Equal("old consumer", await H2Read(reference), "consumer rollback");
        }),
        Case("host.reload.registry-withdrawal-drops-retained-composition", async () =>
        {
            var registry = Registry.Define<string, string>("reload.registry", new() { Version = 1 }); await using var host = H2Host(); var fail = false;
            var provider = new PluginFacet("provider", Facet.Define((h, _) => h.Serve(registry, new() { Compose = (entries, _) => fail ? throw new Exception("compose") : string.Join(",", entries.Select(entry => entry.Value)) })));
            var contributor = new PluginFacet("contributor", Facet.Define((_, self) => self.Contribute(registry, "entry")));
            await host.LoadAsync([provider, contributor], ChordContext.Background);
            var value = await host.RegistryAsync(registry, ChordContext.Background); Equal("entry", value.Value, "initial composition");
            fail = true; await host.ReloadAsync([provider], ChordContext.Background);
            Throws<Exception>(() => _ = value.Value, "composition cannot retain withdrawn native contributor");
        }),
        Case("host.reload.unchanged-registry-keeps-contributor-identities", async () =>
        {
            var registry = Registry.Define<string, string>("reload.registry", new() { Version = 1 }); var composed = 0; await using var host = H2Host();
            var provider = new PluginFacet("provider", Facet.Define((h, _) => h.Serve(registry, new() { Compose = (entries, _) => { composed++; return string.Join(",", entries.Select(entry => entry.Value)); } })));
            var contributor = new PluginFacet("contributor", Facet.Define((_, self) => self.Contribute(registry, "entry")));
            await host.LoadAsync([provider, contributor], ChordContext.Background);
            var value = await host.RegistryAsync(registry, ChordContext.Background); Equal("entry", value.Value, "initial value");
            await host.ReloadAsync([provider, contributor], ChordContext.Background);
            Equal("entry", value.Value, "unchanged value"); Equal(1, composed, "unchanged set does not recompose");
        }),
        Case("host.reload.concurrent-reloads-preserve-request-order", async () =>
        {
            var type = H2Type(); var firstStarted = H2Signal(); var letFirstActivate = H2Signal(); await using var host = H2Host(); var passes = 0;
            await host.LoadAsync([H2Plugin(type, "initial")], ChordContext.Background);
            var first = new PluginFacet("item", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                if (++passes == 2)
                {
                    firstStarted.TrySetResult();
                    self.Follow(async (_, _) => { await letFirstActivate.Task; return new FollowAttachment(null, () => { }); });
                }
                return H2Read("first");
            })));
            var before = host.ReloadAsync([first], ChordContext.Background); await firstStarted.Task;
            var after = host.ReloadAsync([H2Plugin(type, "second")], ChordContext.Background);
            Check(!after.IsCompleted, "second reload waits for first report"); letFirstActivate.TrySetResult();
            await before; await after;
            using var reference = await host.GetAsync(type, new(), ChordContext.Background); Equal("second", await H2Read(reference), "request order defines final provider");
        }),
        Case("host.reload.removal-keeps-all-held-requests-alive", async () =>
        {
            var type = Contract.Local("reload.parameterized", new() { Version = 1, Params = new() { ["id"] = Str() }, Members = new() { ["read"] = Method(null, Str()) } });
            await using var host = H2Host(); await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            using var one = await host.GetAsync(type, new() { ["id"] = "one" }, ChordContext.Background);
            using var two = await host.GetAsync(type, new() { ["id"] = "one" }, ChordContext.Background);
            await host.ReloadAsync([], ChordContext.Background);
            Check(one.State.Value.Kind != "ended" && two.State.Value.Kind != "ended", "both requests wait for removed type");
            await host.ReloadAsync([H2Plugin(type, "new")], ChordContext.Background);
            Equal("new", await H2Read(one), "first rebound"); Equal("new", await H2Read(two), "second rebound");
        }),
        Case("host.reload.swap-follows-old-admitted-commits-until-replacement-attaches", async () =>
        {
            var type = Contract.Local("reload.following", new() { Version = 1, Params = new(), Members = new()
            { ["count"] = Int(), ["rows"] = Keyed(Obj(("count", Int()))), ["seed"] = Method(), ["update"] = Method() } });
            var definition = StateDefinition.Define("count", new() { Version = 1, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 0 } });
            var rowsDefinition = StateDefinition.Define("rows", new() { Version = 1, Keyed = true, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 0 } });
            var started = H2Signal(); var commit = H2Signal(); var committed = H2Signal(); var finish = H2Signal();
            var attaching = H2Signal(); var attached = H2Signal(); await using var host = H2Host();
            PluginFacet Plugin(bool replacement) => new("item", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                var state = self.State(definition);
                var rows = self.KeyedState(rowsDefinition);
                if (replacement) self.Follow(async (_, _) => { attaching.TrySetResult(); await attached.Task; return new FollowAttachment(null, () => { }); });
                return new(new Dictionary<string, object?>
                {
                    ["count"] = self.Derive([state], values => ((JsonNode)values[0]!)["count"]!.DeepClone()),
                    ["rows"] = rows,
                    ["seed"] = (HostMethod)(async (_, context) => { await rows.ChangeAsync("one", row => row["count"] = 2, context.Context); return null; }),
                    ["update"] = (HostMethod)(async (_, context) =>
                    {
                        if (!replacement)
                        {
                            started.TrySetResult(); await commit.Task;
                            await state.ChangeAsync(draft => draft["count"] = 1, context.Context);
                            await rows.ChangeAsync("one", row => row["count"] = 1, context.Context);
                            committed.TrySetResult(); await finish.Task;
                        }
                        return null;
                    }),
                });
            })));
            await host.LoadAsync([Plugin(false)], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            Equal(0, reference.Member("count").Value!.GetValue<int>(), "initial value");
            await reference.CallAsync("seed", null, ChordContext.Background);
            var keyWatch = await reference.KeyedMember("rows").WatchAsync("one", ChordContext.Background)
                ?? throw new InvalidOperationException("seed must create a present keyed document");
            Equal(2, keyWatch.Value!["count"]!.GetValue<int>(), "seed differs from the absent document's initial value");
            var keyAdvanced = H2Signal();
            keyWatch.Start((call, _) =>
            {
                if (call is WatchValueCall<JsonNode?> { Value: { } value } && value["count"]!.GetValue<int>() == 1) keyAdvanced.TrySetResult();
                return Task.CompletedTask;
            });
            var oldCall = reference.CallAsync("update", null, ChordContext.Background); await started.Task;
            var reload = host.ReloadAsync([Plugin(true)], ChordContext.Background); await attaching.Task;
            commit.TrySetResult(); await committed.Task;
            Equal(1, reference.Member("count").Value!.GetValue<int>(), "old admitted commit remains visible during swap");
            await keyAdvanced.Task.WaitAsync(TimeSpan.FromSeconds(1));
            finish.TrySetResult(); attached.TrySetResult(); await oldCall; await reload;
            Equal(1, reference.Member("count").Value!.GetValue<int>(), "replacement takes same document value");
            await keyWatch.StopAsync();
        }),
        Case("host.reload.native-restart-waits-for-admitted-store-publication", async () =>
        {
            var database = new TestSqliteDatabase(":memory:"); await using var store = await StoreOver(database);
            var type = Contract.Local("reload.stored", new() { Version = 1, Params = new(), Members = new() { ["count"] = Int(), ["update"] = Method() } });
            var definition = StateDefinition.Define("count", new()
            { Version = 1, Persist = true, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 0 }, CheckpointWhen = (_, _, _) => true });
            var reached = H2Signal(); var publish = H2Signal(); var observed = H2Signal(); var closed = false;
            await using var host = Hosts.Create(new() { Store = store, OnError = _ => { }, CloseTimeout = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(3) });
            PluginFacet Plugin(string version) => new("stored", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                var state = self.State(definition);
                self.Open(_ => Task.FromResult(version), (_, _, _) => { if (version == "old") closed = true; return Task.CompletedTask; });
                return new(new Dictionary<string, object?>
                {
                    ["count"] = self.Derive([state], values => ((JsonNode)values[0]!)["count"]!.DeepClone()),
                    ["update"] = (HostMethod)(async (_, context) => { await state.ChangeAsync(value => value["count"] = 1, context.Context); return null; }),
                });
            })));
            await host.LoadAsync([Plugin("old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var watch = await reference.Member("count").WatchAsync(ChordContext.Background);
            watch.Start((call, _) => { if (call is WatchValueCall<JsonNode?> { Value: { } value } && value.GetValue<int>() == 1) observed.TrySetResult(); return Task.CompletedTask; });
            database.AfterTransaction = async () => { database.AfterTransaction = null; reached.TrySetResult(); await publish.Task; };
            var update = reference.CallAsync("update", null, ChordContext.Background); await reached.Task;
            try
            {
                var reload = await host.ReloadAsync([Plugin("new")], ChordContext.Background);
                Equal("restarted", reload.Objects.Single().Fate, "native restart planned");
                await Task.Delay(180);
                Check(!closed, "native remains open while an admitted durable commit has not published");
            }
            finally { publish.TrySetResult(); }
            // The bounded drain may cancel the caller while its already-admitted durable step still publishes.
            try { await update; } catch (OperationCanceledException) { }
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await host.DrainReloadsAsync(ChordContext.Background);
            using var current = await host.GetAsync(type, new(), ChordContext.Background);
            Equal(1, current.Member("count").Value!.GetValue<int>(), "replacement hydrates published commit"); Check(closed, "old native closes after publication");
            await watch.StopAsync();
        }),
    ];
}
