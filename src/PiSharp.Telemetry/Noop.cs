// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/noop.ts.
namespace PiSharp.Telemetry;

/// <summary>noop.ts: the shared context used when an application does not provide one, and its shared capabilities. Nothing here
/// inspects or retains metadata, measurements, attributes, carriers or contexts.</summary>
public static class NoopTelemetry
{
    /// <summary><c>NOOP_TELEMETRY_CONTEXT</c>: one inert span, reused for every callback and child.</summary>
    public static ITelemetrySpan Context { get; } = new NoopSpan();

    /// <summary><c>NOOP_TELEMETRY_METRICS</c>: shared instruments.</summary>
    public static ITelemetryMetrics Metrics { get; } = new NoopMetrics();

    /// <summary><c>NOOP_TELEMETRY_PROPAGATION</c>: injects nothing and extracts <see cref="Context"/>.</summary>
    public static ITelemetryPropagation Propagation { get; } = new NoopPropagation();

    private sealed class NoopSpan : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => NoopTelemetry.Metrics;
        public ITelemetryPropagation? Propagation => NoopTelemetry.Propagation;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            try
            {
                return callback(this);
            }
            catch (Exception error)
            {
                return Task.FromException<T>(error);
            }
        }

        public void AddLink(SpanLink link) { }
        public void AddEvent(string name, SpanAttributes? attributes = null) { }
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }

    private sealed class NoopMetrics : ITelemetryMetrics
    {
        private static readonly NoopInstrument Instrument = new();

        public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) => Instrument;
        public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null) => Instrument;
        public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null) => Instrument;
    }

    private sealed class NoopInstrument : ITelemetryCounter, ITelemetryUpDownCounter, ITelemetryHistogram
    {
        public void Add(double value, SpanAttributes? attributes = null) { }
        public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null) { }
    }

    private sealed class NoopPropagation : ITelemetryPropagation
    {
        public IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context) => null;
        public ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier) => Context;
    }
}
