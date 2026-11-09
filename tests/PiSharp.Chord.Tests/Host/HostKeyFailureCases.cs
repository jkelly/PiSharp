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
    private static IEnumerable<(string, Func<Task>)> HostKeyFailureCases() =>
    [
        Case("host.runtime.keyed-failure-keeps-last-value-and-retry-replaces-root", async () =>
        {
            var type = Contract.Local("host.key-failure", new()
            { Version = 1, Params = new(), Members = new() { ["rows"] = Keyed(Obj(("count", Int()))), ["seed"] = Method() } });
            var definition = StateDefinition.Define("rows", new()
            { Version = 1, Keyed = true, Schema = Obj(("count", Int())), Initial = () => new() { ["count"] = 0 } });
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("key-failure", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                var rows = self.KeyedState(definition);
                return new(new Dictionary<string, object?>
                {
                    ["rows"] = rows,
                    ["seed"] = (HostMethod)(async (_, context) => { await rows.ChangeAsync("one", row => row["count"] = 1, context.Context); return null; }),
                });
            })))], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            await reference.CallAsync("seed", null, ChordContext.Background);
            var key = reference.KeyedMember("rows").At("one");
            var watch = await key.WatchAsync(ChordContext.Background);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource<WatchValueCall<JsonNode?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            watch.Start((call, _) =>
            {
                if (call is WatchFailedCall<JsonNode?>) failed.TrySetResult();
                else if (failed.Task.IsCompleted && call is WatchValueCall<JsonNode?> value) recovered.TrySetResult(value);
                return Task.CompletedTask;
            });
            host.Dispatcher.Invoke(() => ((ChordObject)((Binding)reference).Target!).Fail(new("unavailable", "injected live failure")));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(1, key.Value!["count"]!.GetValue<int>(), "target failure retains last value");
            reference.Retry();
            var next = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(1, next.Value!["count"]!.GetValue<int>(), "same durable value recovered");
            Check(next.Ops.Length == 1 && next.Ops[0] is PiSharp.Chord.Delta.ReplaceOp, "first value after failure replaces root");
            await watch.StopAsync();
        }),
        Case("host.runtime.derived-member-failure-survives-target-failure-and-loading", async () =>
        {
            var type = Contract.Local("host.derive-failure", new() { Version = 1, Params = new(), Members = new() { ["value"] = Int() } });
            var memberError = new InvalidOperationException("derive failed");
            IWritable<bool>? trigger = null;
            var opens = 0;
            var reopening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("derive-failure", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                trigger = self.Writable(false);
                self.Open(_ => ++opens == 1 ? Task.FromResult(1) : Reopening(), (_, _, _) => Task.CompletedTask);
                var computed = self.Derive([trigger], values => (bool)values[0]! ? throw memberError : JsonValue.Create(5));
                return new(new Dictionary<string, object?> { ["value"] = computed });
            })))], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var value = reference.Member("value");
            var watch = await value.WatchAsync(ChordContext.Background);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource<WatchValueCall<JsonNode?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            watch.Start((call, _) =>
            {
                if (call is WatchFailedCall<JsonNode?>) failed.TrySetResult();
                else if (failed.Task.IsCompleted && call is WatchValueCall<JsonNode?> change) recovered.TrySetResult(change);
                return Task.CompletedTask;
            });
            host.Dispatcher.Invoke(() => trigger!.Set(true));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(ReferenceEquals(memberError, Throws<InvalidOperationException>(() => _ = value.Value, "member compute error")), "original error preserved");
            var local = (ChordObject)((Binding)reference).Target!;
            var previous = local.Current!;
            host.Dispatcher.Invoke(() => local.Fail(new("unavailable", "target failed after derive")));
            await previous.RetirementEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(ReferenceEquals(memberError, Throws<InvalidOperationException>(() => _ = value.Value, "target error must not hide member error")), "member error survives target failure");
            reference.Retry();
            await reopening.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(ReferenceEquals(memberError, Throws<InvalidOperationException>(() => _ = value.Value, "loading must not hide member error")), "member error survives loading");
            ready.SetResult(1);
            var recovery = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(5, recovery.Value!.GetValue<int>(), "recovered value");
            Check(recovery.Ops.Length == 1 && recovery.Ops[0] is PiSharp.Chord.Delta.ReplaceOp, "recovery replaces root");
            await watch.StopAsync();
            async Task<int> Reopening() { reopening.TrySetResult(); return await ready.Task; }
        }),
    ];
}
