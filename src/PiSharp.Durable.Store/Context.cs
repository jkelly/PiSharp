// PiSharp-only (decision 0013, phase 4): upstream's Durable and Chord share one Context (packages/chord/src/context/index.ts); PiSharp has
// two, DurableContext (PiSharp.Durable) and ChordContext (PiSharp.Chord, which references no Durable project), so the adapter converts a
// Durable context where StoreStorage hands one to the store. Phase 4 carries the telemetry span, the only value of a ChordContext the store
// reads besides cancellation. Pi v1.5.0 sync (area telemetry): a DurableContext carries a span (DurableContext.Telemetry), which the
// store's context takes through ChordContext.WithTelemetry; one with the no-op span stays the background context. Cancellation comes later
// (decision 0013): a store read whose context is aborted fails aborted rather than failing the store, and Durable's storages do not cancel
// reads today.
using PiSharp.Chord;
using PiSharp.Durable.Chord;
using PiSharp.Telemetry;

namespace PiSharp.Durable.Store;

/// <summary>Durable's contexts as the store takes them.</summary>
internal static class StoreContexts
{
    /// <summary>The store's context for <paramref name="context"/>: the background context, with <paramref name="context"/>'s telemetry
    /// span when it has one.</summary>
    internal static ChordContext ToChord(DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var span = context.Telemetry;
        return ReferenceEquals(span, NoopTelemetry.Context) ? ChordContext.Background : ChordContext.WithTelemetry(span, ChordContext.Background);
    }
}
