// PiSharp-only (decision 0013, phase 4): upstream's Durable and Chord share one Context (packages/chord/src/context/index.ts); PiSharp has
// two, DurableContext (PiSharp.Durable) and ChordContext (PiSharp.Chord, which references no Durable project), so the adapter converts a
// Durable context where StoreStorage hands one to the store. Phase 4 carries the telemetry span, the only value of a ChordContext the store
// reads besides cancellation: a DurableContext has none until area telemetry gives it one, so the store's context is the background one,
// with the no-op span, until then. Cancellation comes later (decision 0013): a store read whose context is aborted fails aborted rather
// than failing the store, and Durable's storages do not cancel reads today.
using PiSharp.Chord;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Store;

/// <summary>Durable's contexts as the store takes them.</summary>
internal static class StoreContexts
{
    /// <summary>The store's context for <paramref name="context"/>.</summary>
    internal static ChordContext ToChord(DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ChordContext.Background;
    }
}
