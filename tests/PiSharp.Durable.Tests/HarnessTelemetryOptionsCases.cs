// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-telemetry-options.test.ts (2ec55d808), ported in the
// Pi v1.5.0 sync (area telemetry). HarnessOptions.Telemetry takes one adapter: InMemoryTelemetryContext, the no-op context, or none.
// Upstream's expectTypeOf and @ts-expect-error checks are compile-time; here the case reads the same facts from the property's type: it
// is an ITelemetryContext, whose propagation injects from a context, and a metrics-only adapter does not convert to it.
using PiSharp.Durable;
using PiSharp.Telemetry;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessTelemetryOptionsCases() =>
    [
        Case("harness.telemetry-options.accepts-one-adapter-with-optional-propagation-and-metrics-capabilities", () =>
        {
            var adapter = new InMemoryTelemetryContext();
            var full = new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = Registry.Create(), Telemetry = adapter };
            var contextOnly = full with { Telemetry = NoopTelemetry.Context };
            var omitted = full with { Telemetry = null };
            Check(ReferenceEquals(adapter, full.Telemetry), "full telemetry");
            Check(ReferenceEquals(NoopTelemetry.Context, contextOnly.Telemetry), "context-only telemetry");
            Check(omitted.Telemetry is null, "omitted telemetry");
            var property = typeof(HarnessOptions).GetProperty(nameof(HarnessOptions.Telemetry))!;
            Equal(typeof(ITelemetryContext), property.PropertyType, "telemetry type");
            var inject = typeof(ITelemetryPropagation).GetMethod(nameof(ITelemetryPropagation.Inject))!;
            Check(inject.GetParameters() is [{ ParameterType: var target }] && target == typeof(ITelemetryContext), "propagation injects from a context");
            // A metrics-only adapter lacks the span-starting context capability.
            Check(!typeof(ITelemetryContext).IsAssignableFrom(typeof(ITelemetryMetrics)), "telemetry requires the span-starting context capability");
        }),
    ];
}
