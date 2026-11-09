using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostLifecycleCases() =>
    [
        Case("host.runtime.follow-publishes-one-atomic-batch-and-detaches-on-close", async () =>
        {
            var type = Contract.Local("host.follow", new() { Version = 1, Params = new(), Members = new() { ["sum"] = Int() } });
            IFollowObserver? left = null, right = null;
            var detached = 0;
            var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("follow", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                var a = self.Follow((observer, _) => { left = observer; return Task.FromResult<FollowAttachment?>(new(JsonValue.Create(1), () => detached++)); });
                var b = self.Follow((observer, _) => { right = observer; return Task.FromResult<FollowAttachment?>(new(JsonValue.Create(2), () => detached++)); });
                var sum = self.Derive([a, b], values => JsonValue.Create(((JsonNode)values[0]!).GetValue<int>() + ((JsonNode)values[1]!).GetValue<int>()));
                return new(new Dictionary<string, object?> { ["sum"] = sum });
            })))], ChordContext.Background);
            var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var source = reference.Member("sum");
            Equal(3, source.Value!.GetValue<int>(), "initial followed values");
            var watch = await source.WatchAsync(ChordContext.Background);
            var changed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            watch.Start((call, _) => { if (call is WatchValueCall<JsonNode?> value) changed.TrySetResult(value.Value!.GetValue<int>()); return Task.CompletedTask; });
            host.Dispatcher.Invoke(() => host.Batch.Within(() =>
            {
                left!.Advance(new WatchValueCall<JsonNode?>(JsonValue.Create(4), [new PiSharp.Chord.Delta.ReplaceOp(JsonValue.Create(4))]), ChordContext.Background);
                right!.Advance(new WatchValueCall<JsonNode?>(JsonValue.Create(6), [new PiSharp.Chord.Delta.ReplaceOp(JsonValue.Create(6))]), ChordContext.Background);
            }, ChordContext.Background));
            Equal(10, await changed.Task.WaitAsync(TimeSpan.FromSeconds(2)), "both views from one batch");
            await host.CloseAsync(ChordContext.Background);
            Equal(2, detached, "both attachments closed once");
        }),
        Case("host.runtime.follow-late-attachment-is-detached-after-close", async () =>
        {
            var type = Contract.Local("host.attach", new() { Version = 1, Params = new(), Members = new() { ["value"] = Int() } });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attachment = new TaskCompletionSource<FollowAttachment?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = Hosts.Create(new() { OnError = _ => { } });
            var load = host.LoadAsync([new("attach", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
                new(new Dictionary<string, object?> { ["value"] = self.Follow((_, _) => { entered.SetResult(); return attachment.Task; }) }))))], ChordContext.Background);
            await entered.Task;
            await host.CloseAsync(ChordContext.Background);
            attachment.SetResult(new(JsonValue.Create(1), () => detached.TrySetResult()));
            await detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await load;
        }),
        Case("host.runtime.broken-open-retries-only-when-requested", async () =>
        {
            var type = Contract.Local("host.retry", new() { Version = 1, Params = new(), Members = new() { ["read"] = Method(null, Int()) } });
            var opens = 0;
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("retry", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                var native = self.Open(_ => ++opens == 1 ? Task.FromException<int>(Failure.Broken(new Exception("bad config"))) : Task.FromResult(12), (_, _, _) => Task.CompletedTask);
                return new(new Dictionary<string, object?> { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(native.Value)) });
            })))], ChordContext.Background);
            using var held = host.Hold(type, new(), ChordContext.Background);
            Equal("failed", held.State.Value.Kind, "broken failure");
            Equal(1, opens, "no implicit retry");
            held.Retry();
            using var ready = await host.GetAsync(type, new(), ChordContext.Background);
            Equal(12, (int)(await ready.CallAsync("read", null, ChordContext.Background))!, "retried native");
        }),
        Case("host.runtime.method-arguments-validated-before-handler", async () =>
        {
            var type = Contract.Local("host.args", new() { Version = 1, Params = new(), Members = new() { ["run"] = Method(Obj(("number", Int()))) } });
            var calls = 0;
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("args", Facet.Define((f, _) => f.Serve(type, (_, _, _) =>
                new(new Dictionary<string, object?> { ["run"] = (HostMethod)((_, _) => { calls++; return Task.FromResult<object?>(null); }) }))))], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            try { await reference.CallAsync("run", new JsonObject { ["number"] = "bad" }, ChordContext.Background); throw new Exception("invalid arguments accepted"); }
            catch (ChordError error) { Equal("invalid", error.Code, "argument failure"); }
            Equal(0, calls, "handler untouched");
            await reference.CallAsync("run", new JsonObject { ["number"] = 3 }, ChordContext.Background);
            Equal(1, calls, "valid arguments invoked");
        }),
        Case("host.runtime.call-cancel-rejects-before-noncooperative-handler-settles", async () =>
        {
            var type = Contract.Local("host.cancel", new() { Version = 1, Params = new(), Members = new() { ["run"] = Method() } });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("cancel", Facet.Define((f, _) => f.Serve(type, (_, _, _) => new(new Dictionary<string, object?>
            { ["run"] = (HostMethod)(async (_, _) => { entered.SetResult(); await finish.Task; return null; }) }))))], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            using var cancellation = new CancellationTokenSource();
            var call = reference.CallAsync("run", null, ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background));
            await entered.Task; cancellation.Cancel();
            try { await call.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("cancelled call succeeded"); } catch (OperationCanceledException) { }
            finish.SetResult();
        }),
        Case("host.runtime.registry-observer-cannot-reenter-publication", async () =>
        {
            var token = Registry.Define<int, int>("host.observe", new() { Version = 1 });
            var type = Contract.Local("host.publisher", new() { Version = 1, Params = new(), Members = new() { ["change"] = Method() } });
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("observe", Facet.Define((f, _) =>
            {
                f.Serve(token, new() { Compose = (table, _) => table.Sum(item => item.Value) });
                f.Serve(type, (_, _, self) =>
                {
                    var value = self.Writable(1); ((IStoreSelf)self).Contribute(token, value);
                    return new(new Dictionary<string, object?> { ["change"] = (HostMethod)((_, _) => { value.Set(2); return Task.FromResult<object?>(null); }) });
                });
            }))], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var registry = await host.RegistryAsync(token, ChordContext.Background);
            var observed = new HostReentrantObserver(host, type);
            var observation = registry.Observe(observed, ChordContext.Background);
            await reference.CallAsync("change", null, ChordContext.Background);
            Equal("reentrant", observed.Refused?.Reason, "observer reentrancy refusal");
            observation.Detach();
        }),
    ];

    private sealed class HostReentrantObserver(ChordHost host, Contract type) : PiSharp.Chord.State.INativeObserver<int>
    {
        internal ChordError? Refused;
        public void Advance(NativeWatchCall<int> call, ChordContext context)
        { try { host.Hold(type, new(), context); } catch (ChordError error) { Refused = error; } }
        public void End(WatchEnd end) { }
    }
}
