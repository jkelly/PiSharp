using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostCallbackLifetimeCases() =>
    [
        Case("host.callback-lifetime.original-method-outlives-caller-and-bounded-drain", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HostMethod method = async (_, _) => { entered.SetResult(); await finish.Task; settled.SetResult(); return null; };
            var probe = new CallbackLifetimeProbe(method);
            var type = Contract.Local("callback.method", new() { Version = 1, Params = new(), Members = new() { ["run"] = Method() } });
            var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromMilliseconds(20) });
            var registration = host.RegisterCallbackLifetime(probe.Acquire);
            await host.LoadAsync([new("method", Facet.Define((facet, _) => facet.Serve(type, (_, _, _) =>
                new(new Dictionary<string, object?> { ["run"] = method }))))], ChordContext.Background);
            var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var call = reference.CallAsync("run", null, ChordContext.Background);
            await entered.Task;
            await host.CloseAsync(ChordContext.Background);
            registration.Dispose(); // Removing the resolver must not revoke an already-acquired generation lease.
            Check(probe.Active(method) > 0, "original method still owns code after public close/drain deadline");
            finish.SetResult();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await probe.WaitForZeroAsync();
            try { await call; throw new Exception("closed call succeeded"); } catch (OperationCanceledException) { }
        }),
        Case("host.callback-lifetime.abandoned-native-open-keeps-resource-until-late-close", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<ChordContext, Task<int>> open = async _ => { entered.SetResult(); return await finish.Task; };
            Func<int, NativeCloseReason, ChordContext, Task> close = (value, _, _) =>
            { Equal(7, value, "late opened value"); closed.SetResult(); return Task.CompletedTask; };
            var probe = new CallbackLifetimeProbe(open, close);
            var type = Contract.Local("callback.open", new() { Version = 1, Params = new(), Members = new() });
            var host = Hosts.Create(new() { OnError = _ => { }, ReadyTimeout = TimeSpan.FromMilliseconds(20), CloseTimeout = TimeSpan.FromMilliseconds(20) });
            var registration = host.RegisterCallbackLifetime(probe.Acquire);
            var loading = host.LoadAsync([new("open", Facet.Define((facet, _) => facet.Serve(type, (_, _, self) =>
            { self.Open(open, close); return new(new Dictionary<string, object?>()); })))], ChordContext.Background);
            await entered.Task;
            await host.CloseAsync(ChordContext.Background);
            registration.Dispose();
            Check(probe.Active(open) > 0 && probe.Active(close) > 0, "pending open retains both resource callbacks across host close");
            finish.SetResult(7);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await probe.WaitForZeroAsync();
            await loading;
        }),
        Case("host.callback-lifetime.native-close-original-survives-close-timeout", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<ChordContext, Task<int>> open = _ => Task.FromResult(1);
            Func<int, NativeCloseReason, ChordContext, Task> close = async (_, _, _) => { entered.SetResult(); await finish.Task; };
            var probe = new CallbackLifetimeProbe(open, close);
            var type = Contract.Local("callback.close", new() { Version = 1, Params = new(), Members = new() });
            var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromMilliseconds(20) });
            var registration = host.RegisterCallbackLifetime(probe.Acquire);
            await host.LoadAsync([new("close", Facet.Define((facet, _) => facet.Serve(type, (_, _, self) =>
            { self.Open(open, close); return new(new Dictionary<string, object?>()); })))], ChordContext.Background);
            var closing = host.CloseAsync(ChordContext.Background);
            await entered.Task; await closing.WaitAsync(TimeSpan.FromSeconds(2));
            registration.Dispose();
            Check(probe.Active(close) > 0, "timed-out close callback still owns code");
            finish.SetResult();
            await probe.WaitForZeroAsync();
        }),
        Case("host.callback-lifetime.follow-attachment-owns-code-through-late-detach", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<FollowAttachment?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new CallbackLifetimeProbe();
            FollowAttach attach = async (_, _) => { entered.SetResult(); return await finish.Task; };
            Action detach = () => { Check(probe.Total > 0, "late detach still runs under its resource code lease"); detached.SetResult(); };
            probe.Include(attach, detach);
            var type = Contract.Local("callback.follow", new() { Version = 1, Params = new(), Members = new() { ["value"] = Int() } });
            var host = Hosts.Create(new() { OnError = _ => { } });
            var registration = host.RegisterCallbackLifetime(probe.Acquire);
            var loading = host.LoadAsync([new("follow", Facet.Define((facet, _) => facet.Serve(type, (_, _, self) =>
                new(new Dictionary<string, object?> { ["value"] = self.Follow(attach) }))))], ChordContext.Background);
            await entered.Task;
            await host.CloseAsync(ChordContext.Background);
            registration.Dispose();
            Check(probe.Active(attach) > 0, "pending attachment lease survives activation cancellation");
            finish.SetResult(new(JsonValue.Create(1), detach));
            await detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await probe.WaitForZeroAsync();
            await loading;
        }),
        Case("host.callback-lifetime.registry-compose-and-adapters-own-sync-callbacks", async () =>
        {
            var probe = new CallbackLifetimeProbe();
            Func<IReadOnlyList<RegistryEntry<int>>, ComposeContext, int> compose = null!;
            Func<object?, int> contribution = null!;
            Func<int, object?> value = null!;
            compose = (table, _) => { Check(probe.Active(compose) > 0, "compose leased"); return table.Sum(entry => entry.Value); };
            contribution = raw => { Check(probe.Active(contribution) > 0, "contribution adapter leased"); return (int)raw!; };
            value = composed => { Check(probe.Active(value) > 0, "value adapter leased"); return composed; };
            probe.Include(compose, contribution, value);
            var current = Registry.Define<int, int>("callback.registry", new() { Version = 2 });
            var old = Registry.Define<int, int>("callback.registry", new() { Version = 1 });
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            using var registration = host.RegisterCallbackLifetime(probe.Acquire);
            await host.LoadAsync([new("registry", Facet.Define((facet, self) =>
            {
                facet.Serve(current, new() { Compose = compose, Accepts = new() { [1] = new() { Contribution = contribution, Value = value } } });
                self.Contribute(old, 4);
            }))], ChordContext.Background);
            Equal(4, (await host.RegistryAsync(old, ChordContext.Background)).Value, "mapped composition");
            Equal(0, probe.Total, "all synchronous leases ended");
        }),
        Case("host.callback-lifetime.declaring-facet-remains-immutable-across-reload", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HostMethod previousMethod = async (_, _) => { entered.SetResult(); await finish.Task; return null; };
            HostMethod nextMethod = (_, _) => Task.FromResult<object?>(null);
            var type = Contract.Local("callback.declared", new() { Version = 1, Params = new(), Members = new() { ["run"] = Method() } });
            var previousFacet = Facet.Define((facet, _) => facet.Serve(type, (_, _, _) => new(new Dictionary<string, object?> { ["run"] = previousMethod })));
            var nextFacet = Facet.Define((facet, _) => facet.Serve(type, (_, _, _) => new(new Dictionary<string, object?> { ["run"] = nextMethod })));
            var probe = new CallbackLifetimeProbe(previousMethod, nextMethod);
            await using var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromMilliseconds(20) });
            using var registration = host.RegisterCallbackLifetime((callback, declaringFacet) =>
            {
                if (ReferenceEquals(callback, previousMethod)) Check(ReferenceEquals(previousFacet, declaringFacet), "old callback retains old declaration identity");
                if (ReferenceEquals(callback, nextMethod)) Check(ReferenceEquals(nextFacet, declaringFacet), "new callback has new declaration identity");
                return probe.Acquire(callback);
            });
            await host.LoadAsync([new("declared", previousFacet)], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            var previousCall = reference.CallAsync("run", null, ChordContext.Background);
            await entered.Task;
            await host.ReloadAsync([new("declared", nextFacet)], ChordContext.Background);
            await host.DrainReloadsAsync(ChordContext.Background);
            Check(probe.Active(previousMethod) > 0, "bounded retirement does not drop original declaration owner");
            await reference.CallAsync("run", null, ChordContext.Background);
            finish.SetResult();
            await probe.WaitForZeroAsync();
            try { await previousCall; throw new Exception("retired call succeeded"); } catch (OperationCanceledException) { }
        }),
    ];

    private sealed class CallbackLifetimeProbe(params Delegate[] initial)
    {
        private readonly HashSet<Delegate> included = new(initial, ReferenceEqualityComparer.Instance);
        private readonly ConcurrentDictionary<Delegate, int> active = new(ReferenceEqualityComparer.Instance);
        internal int Total => active.Values.Sum();
        internal int Active(Delegate callback) => active.GetValueOrDefault(callback);
        internal void Include(params Delegate[] callbacks) { foreach (var callback in callbacks) included.Add(callback); }
        internal IDisposable? Acquire(Delegate callback)
        {
            if (!included.Contains(callback)) return null;
            active.AddOrUpdate(callback, 1, (_, count) => count + 1);
            return new Release(() => active.AddOrUpdate(callback, 0, (_, count) => count - 1));
        }
        internal async Task WaitForZeroAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (Total != 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
            Equal(0, Total, "original callbacks/resources settled their leases");
        }
        private sealed class Release(Action release) : IDisposable
        {
            private Action? action = release;
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
    }
}
