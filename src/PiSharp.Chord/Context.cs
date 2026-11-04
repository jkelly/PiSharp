// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/context/index.ts (Context, BACKGROUND_CONTEXT, TODO_CONTEXT,
// withTelemetry)
//
// The context every Chord v2 operation takes: immutable invocation-scoped values passed explicitly. PiSharp.Durable ports context/index.ts
// as DurableContext; Chord v2 references no Durable project, so it has a context of its own, as it reads JsonNode trees instead of
// Durable's JsonValue (Json.cs). Ported so far: what the data layer needs, the telemetry span its counters record through (chord.data.walks,
// chord.data.validations, chord.data.compilations), the two empty roots and withTelemetry. The abort signal, typed values (ContextKey,
// withContextValue), cancellation (withAbortSignal, withoutAbortSignal, withCancel, awaitWithContext) and traceIn come with the first
// Chord v2 area that needs them.
using PiSharp.Telemetry;

namespace PiSharp.Chord;

/// <summary>Immutable invocation-scoped values passed explicitly through Chord v2's operations (upstream <c>Context</c>). Every
/// derivation returns a new context and leaves its parent unchanged.</summary>
public sealed class ChordContext
{
    private readonly ChordContext? parent;
    private readonly string? name;
    private readonly string? key;

    private ChordContext(string name)
    {
        this.name = name;
        Telemetry = NoopTelemetry.Context;
    }

    private ChordContext(ChordContext parent, string key, ITelemetrySpan telemetry)
    {
        this.parent = parent;
        this.key = key;
        Telemetry = telemetry;
    }

    /// <summary>The empty root context for work with no caller (<c>BACKGROUND_CONTEXT</c>).</summary>
    public static ChordContext Background { get; } = new("[Context BACKGROUND_CONTEXT]");

    /// <summary>A distinct empty root marking code that has not yet been given a real context (<c>TODO_CONTEXT</c>).</summary>
    public static ChordContext Todo { get; } = new("[Context TODO_CONTEXT]");

    /// <summary>The explicit telemetry parent: the no-op span unless a derivation set one (<c>context.telemetry</c>).</summary>
    public ITelemetrySpan Telemetry { get; }

    /// <summary>A context whose telemetry parent is <paramref name="telemetry"/>, every other value kept (<c>withTelemetry</c>).</summary>
    public static ChordContext WithTelemetry(ITelemetrySpan telemetry, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(context);
        return new ChordContext(context, "chord.telemetry", telemetry);
    }

    /// <summary>Adds <paramref name="value"/> to the counter <paramref name="counter"/> of the telemetry's metrics, if it has metrics
    /// (<c>context.telemetry.metrics?.counter(counter).add(value)</c>).</summary>
    internal void Count(string counter, double value = 1) => Telemetry.Metrics?.Counter(counter).Add(value);

    /// <summary>The root's name, then <c>.WithValue(key)</c> for each derivation, as upstream prints a context.</summary>
    public override string ToString() => parent is null ? name! : $"{parent}.WithValue({key})";
}
