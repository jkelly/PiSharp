using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/state/writable.test.ts. Upstream's counter is a local type
// served by createHost (phase 6) whose factory makes the writable (self.writable) and its double (self.derive); here they are the state
// layer's on the hand-built host (StateHost.cs), the writable's attempt a StateOwner. An attempt's end (restart, host.close) is what the
// host does to its writables: the attempt is sealed and each writable detached with the attempt's error. What only the host has waits for
// phase 6: the restartable type and its attempts, the owner's activation_failed and registry_failed reports, a registry's contributions
// (here the hand-built StateRegistry, NativeSourceCases.cs) and the `on` routine (here an observer: its start value, then each call). The
// abort-listener case counts no listeners (a CancellationToken has no getEventListeners): an observer kept by its registration would stay
// alive while the token does, so the observer's collection with the token alive shows the registration went.
internal static partial class Program
{
    /// <summary>Upstream's <c>counter</c>: a writable number and its double, a derive over it.</summary>
    private static (StateHost Host, StateOwner Owner, WritableSource<double> Writable, Derive Doubled) StateCounter(StateHost host)
    {
        var owner = new StateOwner("app.counter");
        var writable = host.Writable(1.0, owner);
        var doubled = host.Derive([writable], values => (double)values[0]! * 2);
        return (host, owner, writable, doubled);
    }

    /// <summary>The attempt ends (a restart, host.close): sealed, its writable detached with its error.</summary>
    private static void StateEnd<T>(StateOwner owner, WritableSource<T> writable)
    {
        owner.Ended = true;
        writable.Detach(owner.EndError());
    }

    /// <summary>Starts <paramref name="watch"/> and collects its calls.</summary>
    private static List<NativeWatchCall<T>> StateNativeCalls<T>(INativeWatchHandle<T> watch)
    {
        var calls = new List<NativeWatchCall<T>>();
        watch.Start((call, _) =>
        {
            calls.Add(call);
            return Task.CompletedTask;
        });
        return calls;
    }

    /// <summary>Starts <paramref name="watch"/> and collects the numbers of its value calls.</summary>
    private static List<double> StateNumbers(IWatchHandle<JsonNode?> watch)
    {
        var numbers = new List<double>();
        watch.Start((call, _) =>
        {
            if (call is WatchValueCall<JsonNode?> value) numbers.Add(ChordJson.AsNumber(value.Value)!.Value);
            return Task.CompletedTask;
        });
        return numbers;
    }

    private static IEnumerable<(string, Func<Task>)> StateWritableCases() =>
    [
        Case("state.writable.writable-a-new-value-by-identity-is-a-new-frame-the-same-value-notifies-nobody-and-costs-one-comparison", async () =>
        {
            var metered = await Metered();
            var (host, _, writable, doubledSource) = StateCounter(new StateHost(metered.Context.Telemetry.Metrics));
            var calls = StateNativeCalls(await writable.WatchAsync(StateBg));
            var doubled = StateNumbers(await doubledSource.WatchAsync(StateBg));
            double Computes() => metered.Counts().GetValueOrDefault("chord.derive.computes");
            var computes = Computes();
            writable.Set(1);
            host.Delivered();
            Equal(0, calls.Count, "no call for the same value");
            Equal(computes, Computes(), "no compute for the same value");
            writable.Set(2);
            writable.Set(3);
            host.Delivered();
            Sequence([new NativeWatchValueCall<double>(2), new NativeWatchValueCall<double>(3)], calls, "the calls");
            // A JSON watch folds what came while it waited: the derive's last value.
            Sequence([6.0], doubled, "the doubles");
            Equal(3.0, writable.Value, "the value");
        }),
        Case("state.writable.writable-inside-a-batch-sets-mark-it-there-one-frame-with-the-last-value-the-derives-over-it-computed-once", async () =>
        {
            var metered = await Metered();
            var (host, _, writable, doubledSource) = StateCounter(new StateHost(metered.Context.Telemetry.Metrics));
            var calls = StateNativeCalls(await writable.WatchAsync(StateBg));
            var doubled = StateNumbers(await doubledSource.WatchAsync(StateBg));
            double Computes() => metered.Counts().GetValueOrDefault("chord.derive.computes");
            var computes = Computes();
            host.Batch.Commit(() =>
            {
                writable.Set(5);
                writable.Set(6);
            }, StateBg);
            host.Delivered();
            Sequence([new NativeWatchValueCall<double>(6)], calls, "the calls");
            Sequence([12.0], doubled, "the doubles");
            Equal(computes + 1, Computes(), "one compute");
        }),
        Case("state.writable.writable-refuses-a-set-inside-a-derive-compute-or-a-compose-invalid-reentrant", () =>
        {
            var host = new StateHost();
            var owner = new StateOwner("app.good");
            var inner = host.Writable(0.0, owner);
            var echo = new StateRegistry<double>(host.Batch, table =>
            {
                inner.Set(table.Count + 100);
                return table.Count;
            }, host.Writable(1.0, owner));
            var composed = Throws<ChordError>(() => _ = echo.Value, "the compose");
            Check(composed is { Code: "invalid", Reason: "reentrant" }, $"the compose's error: {composed.Code}/{composed.Reason}");
            Equal(0.0, inner.Value, "the inner writable");
            var errors = new List<Exception>();
            var own = host.Writable(0.0, new StateOwner("app.bad"));
            var bad = host.Derive([own], values =>
            {
                var n = (double)values[0]!;
                own.Set(n + 1);
                return n;
            }, errors.Add);
            var computed = Throws<ChordError>(() => _ = bad.Value, "the derive");
            Check(computed is { Code: "invalid", Reason: "reentrant" }, $"the derive's error: {computed.Code}/{computed.Reason}");
            Check(errors.Count == 1 && ReferenceEquals(computed, errors[0]), "the derive reports it");
            Equal(0.0, own.Value, "its own writable");
        }),
        Case("state.writable.writable-is-the-attempts-set-after-its-end-throws-released-its-watches-end-ended-its-value-stays-readable", async () =>
        {
            var host = new StateHost();
            var owner = new StateOwner("app.restartable(1)");
            var writable = host.Writable(1.0, owner);
            var watch = await writable.WatchAsync(StateBg);
            StateEnd(owner, writable);
            Equal(WatchEnd.Ended, await watch.Closed, "the end");
            var error = Throws<ChordError>(() => writable.Set(2), "the set");
            Equal("released", error.Code, "the code");
            Equal("app.restartable(1): ended", error.Message, "the message");
            Equal(1.0, writable.Value, "the value");
        }),
        Case("state.writable.writable-is-a-contribution-a-derive-source-and-an-on-source", () =>
        {
            var host = new StateHost();
            var value = host.Writable(1.0, new StateOwner("app.source"));
            var values = new StateRegistry<double[]>(host.Batch, table => [.. table], value);
            var seen = new List<double>();
            var observation = value.Observe(new StateNativeObserver<double>(call =>
            {
                if (call is NativeWatchValueCall<double> next) seen.Add(next.Value);
            }), StateBg);
            seen.Add(observation.Value);
            var derived = host.Derive([value], inputs => (double)inputs[0]!);
            value.Set(2);
            host.Delivered();
            Sequence([2.0], values.Value, "the registry's value");
            Json("2", derived.Value, "the derive");
            Sequence([1.0, 2], seen, "the observer");
        }),
        Case("state.writable.writable-is-collected-with-its-attempt", async () =>
        {
            var host = new StateHost();
            await ExpectCollected(StateEndedAttempt(host));
            GC.KeepAlive(host);
        }),
        Case("state.writable.writable-a-set-of-the-same-value-after-its-attempt-ended-throws-released-too-sol-astra", () =>
        {
            var (_, owner, writable, _) = StateCounter(new StateHost());
            StateEnd(owner, writable);
            var error = Throws<ChordError>(() => writable.Set(1), "the set");
            Equal("released", error.Code, "the code");
            Equal("app.counter: ended", error.Message, "the message");
        }),
        Case("state.writable.writable-a-writable-an-app-keeps-after-host-close-keeps-no-closed-host-its-observers-abort-listeners-go-sol", async () =>
        {
            using var signal = new CancellationTokenSource();
            var (references, writable) = StateObservedThenClosed(signal.Token);
            // The token stays alive here: a registration it still held would keep the observer.
            await ExpectCollected(references);
            Equal(1.0, writable.Value, "the value");
            GC.KeepAlive(signal);
        }),
        Case("state.writable.writable-a-late-callbacks-set-after-the-host-closed-throws-released", async () =>
        {
            var (_, owner, writable, _) = StateCounter(new StateHost());
            StateEnd(owner, writable);
            async Task Late()
            {
                await Task.Yield();
                writable.Set(9);
            }
            var rejected = await Rejection(Late());
            Check(rejected is JsonObject json && ChordJson.AsString(json["code"]) == "released", $"the rejection: {rejected}");
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] StateEndedAttempt(StateHost host)
    {
        var owner = new StateOwner("app.restartable(1)");
        var writable = host.Writable(new int[1000], owner);
        var observer = new StateNativeObserver<int[]>(_ => { });
        writable.Observe(observer, StateBg);
        var derive = host.Derive([writable], values => ((int[])values[0]!).Length);
        Json("1000", derive.Value, "the derive");
        derive.Detach(new InvalidOperationException("app.restartable(1): ended"));
        StateEnd(owner, writable);
        Sequence([WatchEnd.Ended], observer.Ends, "the observer's end");
        return [new WeakReference(writable), new WeakReference(owner), new WeakReference(observer), new WeakReference(derive)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] References, WritableSource<double> Writable) StateObservedThenClosed(CancellationToken signal)
    {
        var host = new StateHost();
        var (_, owner, writable, _) = StateCounter(host);
        var observer = new StateNativeObserver<double>(_ => { });
        writable.Observe(observer, ChordContext.WithAbortSignal(signal, StateBg));
        // host.close: the attempt ends.
        StateEnd(owner, writable);
        Sequence([WatchEnd.Ended], observer.Ends, "the observer's end");
        return ([new WeakReference(host), new WeakReference(observer)], writable);
    }
}
