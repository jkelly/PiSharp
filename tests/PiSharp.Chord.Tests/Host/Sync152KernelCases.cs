// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/host/old-work.ts, packages/chord/src/host/readiness.ts, packages/chord/src/host/reload-cycle.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync152KernelCases() =>
    [
        Case("host.sync152.old-context-descendant-and-new-call-gate", async () =>
        {
            var type = Contract.Local("sync152.calls", new() { Version = 1, Params = new(), Members = new()
            { ["outer"] = Method(null, Str()), ["inner"] = Method(null, Str()) } });
            var entered = H2Signal(); var continueOld = H2Signal();
            await using var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(2) });
            PluginFacet Plugin(string version) => new("calls", Facet.Define((facet, _) => facet.Serve(type, (_, _, _) =>
                new(new Dictionary<string, object?>
                {
                    ["inner"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(version))),
                    ["outer"] = (HostMethod)(async (_, call) =>
                    {
                        entered.TrySetResult(); await continueOld.Task;
                        using var same = await call.GetAsync(type, new());
                        return await same.CallAsync("inner", null, ChordContext.WithoutAbortSignal(call.Context));
                    }),
                }))));
            await host.LoadAsync([Plugin("old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var oldCall = reference.CallAsync("outer", null, ChordContext.Background); await entered.Task;
            var report = await host.ReloadAsync([Plugin("new")], ChordContext.Background);
            Equal("restarted", report.Objects.Single().Fate, "pure object restarts");
            var newCall = reference.CallAsync("inner", null, ChordContext.Background);
            Check(!newCall.IsCompleted, "new work waits for successor");
            continueOld.TrySetResult();
            Equal("old", ((JsonNode)(await oldCall)!).GetValue<string>(), "descendant retains old context across WithoutAbortSignal");
            Equal("new", ((JsonNode)(await newCall)!).GetValue<string>(), "ordinary work gets successor");
            await host.DrainReloadsAsync(ChordContext.Background);
        }),
        Case("host.sync152.reload-deadline-cuts-old-call", async () =>
        {
            var type = H2Type("sync152.deadline"); var entered = H2Signal(); var finish = H2Signal(); var errors = new List<HostError>();
            await using var host = Hosts.Create(new() { OnError = errors.Add, CloseTimeout = TimeSpan.FromSeconds(2), ReloadDrainTimeout = TimeSpan.FromMilliseconds(40) });
            var old = new PluginFacet("item", Facet.Define((f, _) => f.Serve(type, (_, _, _) => new(new Dictionary<string, object?>
            { ["read"] = (HostMethod)(async (_, _) => { entered.TrySetResult(); await finish.Task; return JsonValue.Create("old"); }) }))));
            await host.LoadAsync([old], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var running = reference.CallAsync("read", null, ChordContext.Background); await entered.Task;
            await host.ReloadAsync([H2Plugin(type, "new")], ChordContext.Background);
            Equal("new", await H2Read(reference).WaitAsync(TimeSpan.FromSeconds(1)), "new code starts at reload bound not close bound");
            try { await running; throw new Exception("old call should be cut"); } catch (OperationCanceledException) { }
            Check(errors.Any(error => error.Kind == "drain_timeout"), "deadline reported");
            finish.TrySetResult(); await host.DrainReloadsAsync(ChordContext.Background);
        }),
        Case("host.sync152.detached-root-keeps-old-provenance-after-parent-settles", async () =>
        {
            var type = Contract.Local("sync152.detached", new() { Version = 1, Params = new(), Members = new()
            { ["spawn"] = Method(), ["hold"] = Method(), ["inner"] = Method(null, Str()) } });
            var held = H2Signal(); var release = H2Signal(); var continueRoot = H2Signal(); Task<string>? child = null;
            await using var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(3) });
            PluginFacet Plugin(string version) => new("detached", Facet.Define((facet, _) => facet.Serve(type, (_, _, _) => new(new Dictionary<string, object?>
            {
                ["spawn"] = (HostMethod)((_, call) =>
                {
                    child = host.RootAsync(async root =>
                    {
                        await continueRoot.Task;
                        using var reference = await root.GetAsync(type, new());
                        return ((JsonNode)(await reference.CallAsync("inner", null, root.Context))!).GetValue<string>();
                    }, ChordContext.WithoutAbortSignal(call.Context));
                    return Task.FromResult<object?>(null);
                }),
                ["hold"] = (HostMethod)(async (_, _) => { held.TrySetResult(); await release.Task; return null; }),
                ["inner"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(version))),
            }))));
            await host.LoadAsync([Plugin("old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            await reference.CallAsync("spawn", null, ChordContext.Background);
            var holding = reference.CallAsync("hold", null, ChordContext.Background); await held.Task;
            await host.ReloadAsync([Plugin("new")], ChordContext.Background);
            continueRoot.TrySetResult();
            Equal("old", await child!.WaitAsync(TimeSpan.FromSeconds(1)), "independent root retains old call chain after parent settles");
            release.TrySetResult(); await holding; await host.DrainReloadsAsync(ChordContext.Background);
        }),
        Case("host.sync152.invalid-name-does-not-start-load-or-change-reload", async () =>
        {
            await using var host = H2Host(); var type = H2Type("sync152.names");
            try { await host.LoadAsync([H2Plugin(type, "bad", "UPPER")], ChordContext.Background); throw new Exception("invalid name accepted"); }
            catch (ArgumentException) { }
            Check(!host.IsLoaded, "invalid name before any load mutation");
            await host.LoadAsync([H2Plugin(type, "old")], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            try { await host.ReloadAsync([H2Plugin(type, "bad", "UPPER")], ChordContext.Background); throw new Exception("invalid name accepted"); }
            catch (ArgumentException) { }
            Equal("old", await H2Read(reference), "invalid reload keeps loaded code");
        }),
        Case("host.sync152.registry-undeclared-once-per-contributors", async () =>
        {
            var errors = new List<HostError>(); var registry = Registry.Define<string, int>("sync152.registry", new() { Version = 1 });
            await using var host = H2Host(errors);
            PluginFacet Contribution(string name) => new(name, Facet.Define((_, self) => self.Contribute(registry, name)));
            var first = Contribution("one"); await host.LoadAsync([first], ChordContext.Background);
            Equal(1, errors.Count(error => error.Kind == "registry_undeclared"), "initial contributor warning");
            await host.ReloadAsync([first], ChordContext.Background);
            Equal(1, errors.Count(error => error.Kind == "registry_undeclared"), "unchanged contributor not repeated");
            await host.ReloadAsync([first, Contribution("two")], ChordContext.Background);
            Equal(2, errors.Count(error => error.Kind == "registry_undeclared"), "changed contributors reported");
        }),
        Case("host.sync152.context-keys-survive-cancellation-removal", () =>
        {
            var key = new ContextKey<string>("value"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var context = ChordContext.WithValue(key, "one", ChordContext.WithAbortSignal(cancelled.Token, ChordContext.Background));
            var independent = ChordContext.WithoutAbortSignal(context);
            Equal("one", independent.Value(key), "value propagated"); Check(independent.AbortSignal is null, "signal removed only");
            Equal("two", ChordContext.WithValue(key, "two", independent).Value(key), "nearest value wins");
            Equal("one", context.Value(key), "parent unchanged");
        }),
        Case("host.sync152.old-getter-cycle-fails-before-drain-bound", async () =>
        {
            var a1 = Contract.Local("sync152.cycle.a", new() { Version = 1, Params = new(), Members = new() { ["run"] = Method(null, Str()) } });
            var a2 = Contract.Evolve(a1, edit => edit.AddState("revision", Int()));
            var b1 = H2Type("sync152.cycle.b");
            var b2 = Contract.Evolve(b1, edit => edit.AddState("revision", Int()));
            var entered = H2Signal(); var proceed = H2Signal();
            await using var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(3), ReloadDrainTimeout = TimeSpan.FromSeconds(2) });
            var old = new PluginFacet("cycle", Facet.Define((facet, _) =>
            {
                facet.Serve(a1, (_, _, _) => new(new Dictionary<string, object?> { ["run"] = (HostMethod)(async (_, call) =>
                {
                    entered.TrySetResult(); await proceed.Task;
                    using var dependency = await call.GetAsync(b2, new());
                    return JsonValue.Create("should not reach here");
                }) }));
                facet.Serve(b1, (_, _, _) => H2Read("old"));
            }));
            await host.LoadAsync([old], ChordContext.Background);
            using var reference = await host.GetAsync(a1, new(), ChordContext.Background);
            var running = reference.CallAsync("run", null, ChordContext.Background); await entered.Task;
            var fresh = new PluginFacet("cycle", Facet.Define((facet, _) =>
            {
                facet.Serve(a2, (_, _, _) => new(new Dictionary<string, object?>
                { ["run"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create("new"))), ["revision"] = PiSharp.Chord.State.Watchables.Constant(JsonValue.Create(2)) }));
                facet.Serve(b2, (_, _, self) =>
                {
                    self.Get(a2, new());
                    return new(new Dictionary<string, object?>
                    { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create("new"))), ["revision"] = PiSharp.Chord.State.Watchables.Constant(JsonValue.Create(2)) });
                });
            }));
            await host.ReloadAsync([fresh], ChordContext.Background); proceed.TrySetResult();
            try { await running.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("reload cycle should fail"); }
            catch (ChordError error) { Equal("reload_cycle", error.Reason, "cycle cut immediately"); }
            await host.DrainReloadsAsync(ChordContext.Background);
        }),
        Case("host.sync152.waiting-cause-keeps-version-reason", () =>
        {
            var failure = DataInternal.WaitingFor("first", new ChordError("contract_mismatch", "version refused"));
            failure = DataInternal.WaitingFor("second", failure.Error);
            Equal("version", failure.Error.Reason, "nested version reason");
            Equal("forbidden", Errors.Forbidden("principal denied").Code, "public refusal helper");
        }),
        Case("host.sync152.original-join-retains-unattributed-native-closer", async () =>
        {
            var type = H2Type("sync152.join"); var entered = H2Signal(); var finish = H2Signal();
            var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromMilliseconds(20) });
            await host.LoadAsync([new("join", Facet.Define((facet, _) => facet.Serve(type, (_, _, self) =>
            {
                self.Open(_ => Task.FromResult(1), async (_, _, _) => { entered.TrySetResult(); await finish.Task; });
                return H2Read("value");
            })))], ChordContext.Background);
            await host.CloseAsync(ChordContext.Background); await entered.Task;
            var joined = host.JoinOriginalsAsync(ChordContext.Background);
            Check(!joined.IsCompleted, "native closer retains core ownership after public timeout without plugin registration");
            finish.TrySetResult(); await joined.WaitAsync(TimeSpan.FromSeconds(1));
        }),
    ];
}
