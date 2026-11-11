// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry.ts, packages/chord/src/host/host.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

public sealed partial class ChordHost
{
    private static readonly ContextKey<ITelemetryContext> TraceParent = new("chord.traceParent");
    internal ITelemetryContext Telemetry => Options.Telemetry ?? NoopTelemetry.Context;
    internal static ChordContext WithTelemetryParent(ITelemetryContext parent, ChordContext context) => ChordContext.WithValue(TraceParent, parent, context);
    internal static ITelemetryContext ParentOf(ChordContext context) => context.Value(TraceParent) ?? context.Telemetry;
    internal ChordContext Counted(ChordContext context) => ReferenceEquals(context.Telemetry, NoopTelemetry.Context) && Telemetry.Metrics is { } metrics
        ? ChordContext.WithTelemetry(ChordTelemetry.CountingOnly(metrics), context) : context;
    internal async Task<T> TraceOperation<T>(string name, SpanAttributes attributes, ChordContext context, Func<ChordContext, Task<T>> work)
    {
        var parent = ParentOf(context);
        if (ChordTelemetry.IsUntraced(parent)) return await work(Counted(context));
        return await parent.StartSpanAsync(new SpanOptions(name) { Attributes = attributes }, async span =>
        {
            try
            {
                var result = await work(WithTelemetryParent(span, ChordContext.WithTelemetry(span, context)));
                if (result is LoadReport loaded) span.SetAttributes(new SpanAttributes { ["failed"] = (double)loaded.Failed.Count });
                if (result is ReloadReport reloaded)
                {
                    span.SetAttributes(new SpanAttributes { ["added"] = (double)reloaded.Plugins.Added.Count,
                        ["changed"] = (double)reloaded.Plugins.Changed.Count, ["removed"] = (double)reloaded.Plugins.Removed.Count,
                        ["failed"] = (double)reloaded.Failed.Count, ["fates"] = (double)reloaded.Objects.Count });
                    foreach (var fate in reloaded.Objects) span.AddEvent("chord.reload.fate", new SpanAttributes
                    { ["object"] = ChordTelemetry.Capped(fate.Object), ["fate"] = fate.Fate });
                }
                return result;
            }
            catch (Exception error) { ChordTelemetry.Ended(span, name[6..] + "_failed", error, context.AbortSignal); throw; }
        });
    }
    public SpanAttributes PrincipalAttributes(JsonObject? principal, ChordContext context)
    {
        if (principal is null) return new();
        return Options.PrincipalAttributes?.Invoke(principal) ?? new SpanAttributes { ["chord.principal"] = ChordTelemetry.Capped(DataInternal.PrincipalKey(principal, context, Options.PrincipalKey)) };
    }
    internal SpanAttributes Attributes(string? type = null, string? obj = null)
    {
        var attributes = new SpanAttributes { ["chord.host"] = ChordTelemetry.HostAttribute(Address) };
        if (type is not null) attributes["type"] = ChordTelemetry.Capped(type);
        if (obj is not null) attributes["object"] = ChordTelemetry.Capped(obj);
        return attributes;
    }
}
