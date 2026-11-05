// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/registry-reader.ts
//
// A Harness's registry over a Chord registry (Chord v2 slice 8 §5): the composed value of an app's extensions registry (p3's pattern),
// read as Durable's IRegistryReader, so a Harness opened in a Chord object takes its extensions from a Chord registry and any plugin extends
// it by contributing an extension. It needs Chord v2's registry value (PiSharp.Chord.Registries.IRegistryValue<V>), so it lives in this
// adapter and PiSharp.Durable keeps decision 0005 §4 (decision 0013, phase 4). Mappings:
// - Upstream's registryReader<Tool>(value, context) is RegistryReader.Create(value, context). PiSharp's IRegistryReader and IRegistrySnapshot
//   are not generic over the tool type, so the value is an IRegistryValue<IRegistrySnapshot>; its Value is snapshot()'s result, which
//   throws the value's failure while it has no composition, as upstream's `value.value` does.
// - subscribe's listener is an INativeObserver whose Advance calls it for a value call only (call.kind === "value"): a failed composition
//   keeps the last snapshot, so there is nothing to reconsider. Its End does nothing. The returned disposer detaches the observation, once.
// - context is a ChordContext, the observation's: cancelling its AbortSignal detaches every listener (the value ends each one aborted).
// - Chord v2's batch belongs to its host and is not thread-safe (src/PiSharp.Chord/State/Batch.cs): Subscribe attaches an observer and its
//   disposer detaches it, so the Harness that subscribes (its scheduler's open) and is closed (which disposes) runs where the host runs,
//   outside its batch, as everything that calls a Chord value does; phase 6's host decides how other threads reach it. The listener runs
//   inside the batch that composed, so it may only record or wake: a Chord call there throws invalid/reentrant. The Harness's listener
//   notifies the task graph's blocked reasons and kicks the scheduler, both of which only queue work.
using PiSharp.Chord;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;

namespace PiSharp.Durable.Store;

/// <summary>A Harness's registry read from a Chord v2 registry (upstream <c>registryReader</c>).</summary>
public static class RegistryReader
{
    /// <summary>
    /// An <see cref="IRegistryReader"/> over a Chord registry value whose composition is a registry snapshot (upstream
    /// <c>registryReader(value, context)</c>): <see cref="IRegistryReader.Snapshot"/> reads the current composition (the last good one while
    /// <c>compose</c> fails), and <see cref="IRegistryReader.Subscribe"/> observes its compositions, its listener called synchronously inside
    /// the Chord batch that composed (so it only wakes the scheduler; a Chord call there throws <c>invalid/reentrant</c>).
    /// <paramref name="context"/> is the observation's: cancelling its abort signal detaches every listener.
    /// </summary>
    /// <param name="value">The registry's value as a reader of its version sees it (<c>self.registry</c>, <c>host.registry</c>).</param>
    /// <param name="context">The context of every listener's observation.</param>
    public static IRegistryReader Create(IRegistryValue<IRegistrySnapshot> value, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(context);
        return new Reader(value, context);
    }

    private sealed class Reader(IRegistryValue<IRegistrySnapshot> value, ChordContext context) : IRegistryReader
    {
        public IRegistrySnapshot Snapshot() => value.Value;

        public IDisposable Subscribe(Action listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            var observation = value.Observe(new Listener(listener), context);
            return new Detacher(observation);
        }
    }

    /// <summary>Calls its listener for each new composition.</summary>
    private sealed class Listener(Action listener) : INativeObserver<IRegistrySnapshot>
    {
        public void Advance(NativeWatchCall<IRegistrySnapshot> call, ChordContext context)
        {
            // A failed composition keeps the last snapshot: nothing to reconsider.
            if (call is NativeWatchValueCall<IRegistrySnapshot>) listener();
        }

        public void End(WatchEnd end) { }
    }

    /// <summary>Detaches the observation on its first dispose.</summary>
    private sealed class Detacher(NativeObservation<IRegistrySnapshot> observation) : IDisposable
    {
        private NativeObservation<IRegistrySnapshot>? observation = observation;

        public void Dispose() => Interlocked.Exchange(ref observation, null)?.Detach();
    }
}
