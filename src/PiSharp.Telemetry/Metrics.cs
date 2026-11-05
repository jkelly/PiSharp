// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/metrics.ts.
using System.Text.Json.Serialization;

namespace PiSharp.Telemetry;

/// <summary>metrics.ts <c>InstrumentOptions</c>.</summary>
public record InstrumentOptions
{
    public string? Description { get; init; }
    public string? Unit { get; init; }
}

/// <summary>metrics.ts <c>HistogramOptions</c>.</summary>
public sealed record HistogramOptions : InstrumentOptions
{
    /// <summary>Bucket hints; callers supply finite, strictly increasing values.</summary>
    public IReadOnlyList<double>? Boundaries { get; init; }
}

[JsonConverter(typeof(MetricKindConverter))]
public enum TelemetryMetricKind { Counter, UpDownCounter, Histogram }

/// <summary>metrics.ts <c>TelemetryCounter</c>.</summary>
public interface ITelemetryCounter
{
    void Add(double value, SpanAttributes? attributes = null);
}

/// <summary>metrics.ts <c>TelemetryUpDownCounter</c>.</summary>
public interface ITelemetryUpDownCounter
{
    void Add(double value, SpanAttributes? attributes = null);
}

/// <summary>metrics.ts <c>TelemetryHistogram</c>: <paramref name="context"/> is an optional exemplar span.</summary>
public interface ITelemetryHistogram
{
    void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null);
}

/// <summary>metrics.ts <c>TelemetryMetrics</c>: an optional adapter capability with synchronous factories and recording methods.
/// Typed inputs are trusted; callers supply finite measurements and nonnegative counter increments. Malformed input behaviour is
/// unspecified.</summary>
public interface ITelemetryMetrics
{
    ITelemetryCounter Counter(string name, InstrumentOptions? options = null);
    ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null);
    ITelemetryHistogram Histogram(string name, HistogramOptions? options = null);
}

/// <summary>metrics.ts <c>TypedTelemetryMetrics</c>: instruments bound to one or more schemas' metric definitions. Only the schemas'
/// instrument names of the requested kind are admitted, and every measurement's attributes are checked against the definition:
/// required ones present, no undeclared ones, values of the declared type and closed set. A violation throws
/// <see cref="ArgumentException"/>, where TypeScript rejects it at compile time. Each call creates the adapter's instrument with the
/// definition's description, unit and boundaries.</summary>
public sealed class TypedTelemetryMetrics
{
    private readonly ITelemetryMetrics _metrics;
    private readonly Dictionary<string, TelemetryMetricDefinition> _definitions;

    internal TypedTelemetryMetrics(ITelemetryMetrics metrics, Dictionary<string, TelemetryMetricDefinition> definitions)
    {
        _metrics = metrics;
        _definitions = definitions;
    }

    public SchemaTelemetryCounter Counter(string name)
    {
        var definition = Definition(name, TelemetryMetricKind.Counter);
        return new(name, definition, _metrics.Counter(name, Options(definition)).Add);
    }

    public SchemaTelemetryCounter UpDownCounter(string name)
    {
        var definition = Definition(name, TelemetryMetricKind.UpDownCounter);
        return new(name, definition, _metrics.UpDownCounter(name, Options(definition)).Add);
    }

    public SchemaTelemetryHistogram Histogram(string name)
    {
        var definition = Definition(name, TelemetryMetricKind.Histogram);
        return new(name, definition, _metrics.Histogram(name, new HistogramOptions
        { Description = definition.Description, Unit = definition.Unit, Boundaries = definition.Boundaries }));
    }

    private static InstrumentOptions Options(TelemetryMetricDefinition definition) =>
        new() { Description = definition.Description, Unit = definition.Unit };

    private TelemetryMetricDefinition Definition(string name, TelemetryMetricKind kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_definitions.TryGetValue(name, out var definition))
            throw new ArgumentException($"Unknown telemetry metric '{name}'.", nameof(name));
        if (definition.Kind != kind)
            throw new ArgumentException($"Telemetry metric '{name}' is a {NamedEnumConverter<TelemetryMetricKind>.NameOf(definition.Kind, MetricKindConverter.Names)}.", nameof(name));
        return definition;
    }
}

/// <summary>metrics.ts <c>SchemaTelemetryCounter</c>, for counters and up-down counters.</summary>
public sealed class SchemaTelemetryCounter
{
    private readonly TelemetryMetricDefinition _definition;
    private readonly Action<double, SpanAttributes?> _add;

    internal SchemaTelemetryCounter(string name, TelemetryMetricDefinition definition, Action<double, SpanAttributes?> add)
    {
        Name = name;
        _definition = definition;
        _add = add;
    }

    public string Name { get; }

    public void Add(double value, SpanAttributes? attributes = null)
    {
        TelemetryAttributeChecks.Check(_definition.Attributes, attributes, requireRequired: true, $"metric '{Name}'");
        _add(value, attributes);
    }
}

/// <summary>metrics.ts <c>SchemaTelemetryHistogram</c>: the exemplar context never replaces required attributes.</summary>
public sealed class SchemaTelemetryHistogram
{
    private readonly TelemetryMetricDefinition _definition;
    private readonly ITelemetryHistogram _histogram;

    internal SchemaTelemetryHistogram(string name, TelemetryMetricDefinition definition, ITelemetryHistogram histogram)
    {
        Name = name;
        _definition = definition;
        _histogram = histogram;
    }

    public string Name { get; }

    public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null)
    {
        TelemetryAttributeChecks.Check(_definition.Attributes, attributes, requireRequired: true, $"metric '{Name}'");
        _histogram.Record(value, attributes, context);
    }
}
