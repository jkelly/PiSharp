using System.Collections.Concurrent;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostConcurrencyCases() =>
    [
        Case("host.runtime.reference-state-offthread-stop-and-cancel-race-publication", async () =>
        {
            var reports = new ConcurrentQueue<HostError>();
            var type = Contract.Local("host.concurrent", new() { Version = 1, Params = new(), Members = new() });
            await using var host = Hosts.Create(new() { OnError = reports.Enqueue });
            await host.LoadAsync([new("concurrent", Facet.Define((f, _) => f.Serve(type, (_, _, _) => new(new Dictionary<string, object?>()))))], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var local = ((Binding)reference).Target as ChordObject ?? throw new Exception("expected local target");
            var watchLoop = Task.Run(async () =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    using var cancellation = new CancellationTokenSource();
                    var watch = await reference.State.WatchAsync(ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background));
                    watch.Start((_, _) => Task.CompletedTask);
                    // Both callbacks execute from the pool here, concurrently with the host transition queue.
                    if ((i & 1) == 0) cancellation.Cancel();
                    await watch.StopAsync();
                }
            });
            var publications = Task.Run(() =>
            {
                for (var i = 0; i < 1000; i++) host.Dispatcher.Invoke(() =>
                {
                    local.SetState(RefState.Loading);
                    local.SetState(RefState.Live);
                });
            });
            await Task.WhenAll(watchLoop, publications).WaitAsync(TimeSpan.FromSeconds(15));
            Check(reports.IsEmpty, "concurrent reference-state observers produced no host failures");
            Equal("live", reference.State.Value.Kind, "last state remains live");
        }),
        Case("host.runtime.reentrant-root-cancellation-joins-one-close", async () =>
        {
            var type = Contract.Local("host.reentrant-close", new() { Version = 1, Params = new(), Members = new() });
            var closed = 0;
            var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("reentrant-close", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                self.Open(_ => Task.FromResult(1), (_, _, _) => { closed++; return Task.CompletedTask; });
                return new(new Dictionary<string, object?>());
            })))], ChordContext.Background);
            Task? nestedClose = null;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var root = host.RootAsync(async context =>
            {
                using var registration = context.AbortSignal.Register(() => nestedClose = host.CloseAsync(ChordContext.Background));
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.AbortSignal);
                return true;
            }, ChordContext.Background);
            await entered.Task;
            var outerClose = host.CloseAsync(ChordContext.Background);
            Check(ReferenceEquals(outerClose, nestedClose), "reentrant caller joins the already-published close task");
            await outerClose;
            try { await root; throw new Exception("root did not cancel"); } catch (OperationCanceledException) { }
            Equal(1, closed, "native teardown ran once");
        }),
        Case("host.runtime.last-reference-native-close-reentry-keeps-owning-pending-cleanup", async () =>
        {
            var type = Contract.Local("host.reentrant-native", new()
            { Version = 1, Params = new() { ["id"] = PiSharp.Chord.Contracts.Schemas.Int() }, Members = new() });
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeCloses = 0;
            Task? nested = null;
            var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("reentrant-native", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                self.Open(_ => Task.FromResult(1), (_, _, _) =>
                {
                    nativeCloses++; nested = host.CloseAsync(ChordContext.Background); nativeEntered.SetResult(); return gate.Task;
                });
                return new(new Dictionary<string, object?>());
            })))], ChordContext.Background);
            var reference = await host.GetAsync(type, new System.Text.Json.Nodes.JsonObject { ["id"] = 1 }, ChordContext.Background);
            reference.Release();
            await nativeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            try { Check(nested is not null && !nested.IsCompleted, "reentrant host close still owns the first native cleanup"); }
            finally { gate.TrySetResult(); }
            await nested!;
            Equal(1, nativeCloses, "one physical native close");
        }),
    ];
}
