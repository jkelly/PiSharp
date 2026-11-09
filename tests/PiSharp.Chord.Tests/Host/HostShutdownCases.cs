using System.Collections.Concurrent;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Jobs;
using PiSharp.Chord.Registries;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostShutdownCases() =>
    [
        Case("host.runtime.shutdown-reverses-live-order-not-creation-order", async () =>
        {
            var a = Contract.Local("host.shutdown-a", new() { Version = 1, Params = new(), Members = new() });
            var b = Contract.Local("host.shutdown-b", new() { Version = 1, Params = new(), Members = new() });
            var order = new List<string>();
            var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("shutdown", Facet.Define((f, _) =>
            {
                // A is created first, but B must go live before A can open its native.
                f.Serve(a, (_, _, self) =>
                {
                    self.Get(b, new());
                    self.Open(_ => Task.FromResult(1), (_, _, _) => { order.Add("a"); return Task.CompletedTask; });
                    return new(new Dictionary<string, object?>());
                });
                f.Serve(b, (_, _, self) =>
                {
                    self.Open(_ => Task.FromResult(1), (_, _, _) => { order.Add("b"); return Task.CompletedTask; });
                    return new(new Dictionary<string, object?>());
                });
            }))], ChordContext.Background);
            await host.CloseAsync(ChordContext.Background);
            Equal("a,b", string.Join(',', order), "last to go live closes first");
        }),
        Case("host.runtime.job-start-during-linger-cancels-old-idle-timer", async () =>
        {
            var type = Contract.Local("host.linger-job", new() { Version = 1, Params = new() { ["id"] = Int() }, Members = new() });
            var job = JobDefinition.Define("host.linger-run", new() { Version = 1, Args = Obj(), Result = Nul() });
            IJobHandle? handle = null;
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([new("linger-job", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                handle = self.Job(job, new() { Run = async (_, _) => { await finish.Task; return null; } });
                self.Open(_ => Task.FromResult(1), (_, _, _) => { closed.TrySetResult(); return Task.CompletedTask; });
                return new(new Dictionary<string, object?>());
            }, new(Linger: TimeSpan.FromMilliseconds(70)))))], ChordContext.Background);
            var reference = await host.GetAsync(type, new System.Text.Json.Nodes.JsonObject { ["id"] = 1 }, ChordContext.Background);
            reference.Release();
            Equal(1, host.Activity.Value.Lingering, "object entered linger");
            var run = await handle!.StartAsync(new System.Text.Json.Nodes.JsonObject(), ChordContext.Background);
            Equal(0, host.Activity.Value.Lingering, "run cancels old timer");
            await Task.Delay(120);
            Check(!closed.Task.IsCompleted, "active run still owns object after old deadline");
            finish.SetResult();
            await run.OutcomeAsync(ChordContext.Background);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }),
        Case("host.runtime.parked-long-reports-once-without-failing-object", async () =>
        {
            var registry = Registry.Define<int, int>("host.missing-registry", new() { Version = 1 });
            var type = Contract.Local("host.parked", new() { Version = 1, Params = new(), Members = new() });
            var reports = new ConcurrentQueue<HostError>();
            var notice = new TaskCompletionSource<HostError>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = Hosts.Create(new() { ReadyTimeout = TimeSpan.FromMilliseconds(20), OnError = error =>
            { reports.Enqueue(error); if (error.Kind == "parked_long") notice.TrySetResult(error); } });
            await host.LoadAsync([new("parked", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            { self.Registry(registry); return new(new Dictionary<string, object?>()); })))], ChordContext.Background);
            var reported = await notice.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal("registry host.missing-registry", reported.WaitingFor, "nearest blocker named");
            Equal("failed", reported.State, "waiting dependency state");
            await Task.Delay(50);
            Equal(1, reports.Count(error => error.Kind == "parked_long"), "once per parking episode");
            using var held = host.Hold(type, new(), ChordContext.Background);
            Equal("failed", held.State.Value.Kind, "object still waits instead of timing out");
        }),
    ];
}
