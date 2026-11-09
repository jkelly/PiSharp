using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using static PiSharp.Chord.Contracts.Schemas;

// Authored against v1.5.0 host/host, attempt, root, native, dependencies, follow and registry cases.
// These use Hosts.Create, not the historical RegistryHost/StateHost test surrogates.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostRuntimeCases() =>
    [
        Case("host.runtime.prepublication-reference-and-registry-wait-for-real-load", async () =>
        {
            var type = Contract.Local("host.counter", new() { Version = 1, Params = new(), Members = new() { ["value"] = Obj(("count", Int())), ["inc"] = Method() } });
            var definition = StateDefinition.Define("counter", new() { Version = 1, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 0 } });
            var registry = Registry.Define<string, int>("host.names", new() { Version = 1 });
            var errors = new List<HostError>();
            await using var host = Hosts.Create(new() { OnError = errors.Add });
            var pending = host.GetAsync(type, new(), ChordContext.Background);
            var names = host.RegistryAsync(registry, ChordContext.Background);
            Check(!pending.IsCompleted && !names.IsCompleted, "requests wait before publication");
            var report = await host.LoadAsync([new("counter", Facet.Define((facet, _) =>
            {
                facet.Serve(registry, new() { Compose = (table, _) => table.Count });
                facet.Serve(type, (_, _, self) =>
                {
                    var state = self.State(definition);
                    ((IStoreSelf)self).Contribute(registry, "counter");
                    return new(new Dictionary<string, object?>
                    {
                        ["value"] = state,
                        ["inc"] = (HostMethod)(async (_, context) => { await state.ChangeAsync(d => d["count"] = d["count"]!.GetValue<int>() + 1, context.Context); return null; }),
                    });
                });
            }))], ChordContext.Background);
            Equal(0, report.Failed.Count, "loaded");
            using var reference = await pending;
            Equal(1, (await names).Value, "host object contribution composed");
            Equal(0, reference.Member("value").Value!["count"]!.GetValue<int>(), "initial state");
            await reference.CallAsync("inc", null, ChordContext.Background);
            Equal(1, reference.Member("value").Value!["count"]!.GetValue<int>(), "real transaction adopted");
            Equal(0, errors.Count, "no background failure");
        }),
        Case("host.runtime.failed-plugin-isolated-and-setup-api-sealed", async () =>
        {
            var type = Contract.Local("host.good", new() { Version = 1, Params = new(), Members = new() { ["value"] = Int() } });
            PiSharp.Chord.Host.IFacetHost? retained = null;
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            var report = await host.LoadAsync([
                new("bad", Facet.Define((_, _) => throw new InvalidOperationException("setup failed"))),
                new("good", Facet.Define((f, _) => { retained = f; f.Serve(type, (_, _, _) => new(new Dictionary<string, object?> { ["value"] = PiSharp.Chord.State.Watchables.Constant(JsonValue.Create(7)) })); }))
            ], ChordContext.Background);
            Equal(1, report.Failed.Count, "one failure");
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            Equal(7, reference.Member("value").Value!.GetValue<int>(), "other plugin works");
            var refused = false;
            try { retained!.Serve(type, (_, _, _) => new(new Dictionary<string, object?>())); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "retained setup API refused");
        }),
        Case("host.runtime.native-dependency-does-not-block-dispatcher", async () =>
        {
            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var type = Contract.Local("host.slow", new() { Version = 1, Params = new(), Members = new() { ["read"] = Method(null, Int()) } });
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            var loading = host.LoadAsync([new("slow", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                var native = self.Open(async _ => { opened.TrySetResult(); return await ready.Task; }, (_, _, _) => Task.CompletedTask);
                return new(new Dictionary<string, object?> { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(native.Value)) });
            })))], ChordContext.Background);
            await opened.Task;
            var held = host.Hold(type, new(), ChordContext.Background);
            Equal("loading", held.State.Value.Kind, "synchronous host operation while open pending");
            ready.SetResult(42); await loading;
            Equal(42, (int)(await held.CallAsync("read", null, ChordContext.Background))!, "native ready"); held.Release();
        }),
        Case("host.runtime.root-scope-releases-held-references-and-refuses-late-get", async () =>
        {
            var type = Contract.Local("host.scoped", new() { Version = 1, Params = new(), Members = new() });
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("scoped", Facet.Define((f, _) => f.Serve(type, (_, _, _) => new(new Dictionary<string, object?>()))))], ChordContext.Background);
            CallContext? kept = null; IReference? reference = null;
            await host.RootAsync(async ctx => { kept = ctx; reference = await ctx.GetAsync(type, new()); return true; }, ChordContext.Background);
            Equal("ended", reference!.State.Value.Kind, "scope ended its reference");
            try { await kept!.GetAsync(type, new()); throw new Exception("late get was accepted"); }
            catch (ChordError error) { Equal("released", error.Code, "late scope get"); }
        }),
        Case("host.runtime.close-seals-before-reverse-native-cleanup", async () =>
        {
            var closes = new List<int>();
            var reasons = new List<NativeCloseReason>();
            var type = Contract.Local("host.natives", new() { Version = 1, Params = new(), Members = new() });
            var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("natives", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                for (var i = 1; i <= 3; i++) { var id = i; self.Open(_ => Task.FromResult(id), (n, reason, _) => { closes.Add(n); reasons.Add(reason); return Task.CompletedTask; }); }
                return new(new Dictionary<string, object?>());
            })))], ChordContext.Background);
            var reference = await host.GetAsync(type, new(), ChordContext.Background);
            await host.CloseAsync(ChordContext.Background);
            Equal("3,2,1", string.Join(',', closes), "reverse cleanup");
            Check(reasons.All(r => r == NativeCloseReason.Close), "host close reason retained");
            Equal("ended", reference.State.Value.Kind, "references sealed");
            await host.CloseAsync(ChordContext.Background);
            Equal(3, closes.Count, "close idempotent");
        }),
        Case("host.runtime.late-native-open-after-close-is-cleaned-once", async () =>
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var type = Contract.Local("host.late", new() { Version = 1, Params = new(), Members = new() });
            var host = Hosts.Create(new() { OnError = _ => { } });
            var load = host.LoadAsync([new("late", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                self.Open(async _ => { opening.SetResult(); return await completion.Task; }, (value, _, _) => { closed.SetResult(value); return Task.CompletedTask; });
                return new(new Dictionary<string, object?>());
            })))], ChordContext.Background);
            await opening.Task;
            await host.CloseAsync(ChordContext.Background);
            completion.SetResult(9);
            Equal(9, await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)), "late resource closed");
            await load;
        }),
        Case("host.runtime.dependency-cycle-finishes-load-with-failure", async () =>
        {
            var a = Contract.Local("host.a", new() { Version = 1, Params = new(), Members = new() });
            var b = Contract.Local("host.b", new() { Version = 1, Params = new(), Members = new() });
            var errors = new List<HostError>();
            await using var host = Hosts.Create(new() { OnError = errors.Add });
            await host.LoadAsync([new("cycle", Facet.Define((f, _) =>
            {
                f.Serve(a, (_, _, self) => { self.Get(b, new()); return new(new Dictionary<string, object?>()); });
                f.Serve(b, (_, _, self) => { self.Get(a, new()); return new(new Dictionary<string, object?>()); });
            }))], ChordContext.Background).WaitAsync(TimeSpan.FromSeconds(2));
            Check(errors.Any(e => e.Failure?.Error.Reason == "cycle"), "cycle diagnosed");
        }),
        Case("host.runtime.unknown-reference-timeout-does-not-leak", async () =>
        {
            var unknown = Contract.Local("host.unknown", new() { Version = 1, Params = new(), Members = new() });
            await using var host = Hosts.Create(new() { OnError = _ => { }, ReadyTimeout = TimeSpan.FromMilliseconds(30) });
            await host.LoadAsync([], ChordContext.Background);
            try { await host.GetAsync(unknown, new(), ChordContext.Background); throw new Exception("unknown acquired"); }
            catch (ChordError error) { Equal("unavailable", error.Code, "last failure retained"); }
        }),
    ];
}
