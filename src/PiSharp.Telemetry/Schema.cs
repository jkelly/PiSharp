// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/index.ts, packages/telemetry/src/metrics.ts.
using System.Text.Json.Serialization;

namespace PiSharp.Telemetry;

[JsonConverter(typeof(CardinalityConverter))]
public enum TelemetryCardinality { Low, High }

/// <summary>index.ts <c>TelemetryAttributeDefinition</c> and its start, event and metric forms (<c>required</c>). <see cref="Values"/> is
/// the closed set of a scalar attribute, <see cref="ElementValues"/> that of an array attribute's elements. Metric attributes are
/// never high-cardinality. Definitions are serializable data.</summary>
public sealed record TelemetryAttributeDefinition(
    [property: JsonPropertyName("type")] TelemetryAttributeType Type,
    [property: JsonPropertyName("description")] string Description)
{
    /// <summary>Start, event and metric attributes: whether it must be present. End attributes are all optional.</summary>
    [JsonPropertyName("required"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Required { get; init; }
    [JsonPropertyName("sensitive"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Sensitive { get; init; }
    [JsonPropertyName("cardinality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TelemetryCardinality? Cardinality { get; init; }
    [JsonPropertyName("values"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<AttributeValue>? Values { get; init; }
    [JsonPropertyName("elementValues"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<AttributeValue>? ElementValues { get; init; }
    [JsonPropertyName("examples"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<AttributeValue>? Examples { get; init; }
}

/// <summary>index.ts <c>TelemetryEventDefinition</c>.</summary>
public sealed record TelemetryEventDefinition(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, TelemetryAttributeDefinition> Attributes);

[JsonConverter(typeof(ParentKindConverter))]
public enum TelemetryParentKind { Any, RootOrExternal, Spans }

/// <summary>index.ts <c>TelemetryParentDefinition</c>: documentation of a span's admitted parents.</summary>
public sealed record TelemetryParentDefinition([property: JsonPropertyName("kind")] TelemetryParentKind Kind)
{
    [JsonPropertyName("spans"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Spans { get; init; }

    public static TelemetryParentDefinition Any { get; } = new(TelemetryParentKind.Any);
    public static TelemetryParentDefinition RootOrExternal { get; } = new(TelemetryParentKind.RootOrExternal);
    public static TelemetryParentDefinition Of(params string[] spans) => new(TelemetryParentKind.Spans) { Spans = spans };
}

/// <summary>index.ts <c>TelemetrySpanDefinition.status</c>: the default is always ok.</summary>
public sealed record TelemetrySpanStatusDefinition([property: JsonPropertyName("errorWhen"), JsonPropertyOrder(1)] string ErrorWhen)
{
    [JsonPropertyName("default")] public string Default => "ok";
}

/// <summary>index.ts <c>TelemetrySpanDefinition</c>.</summary>
public sealed record TelemetrySpanDefinition(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parents")] TelemetryParentDefinition Parents,
    [property: JsonPropertyName("startAttributes")] IReadOnlyDictionary<string, TelemetryAttributeDefinition> StartAttributes,
    [property: JsonPropertyName("endAttributes")] IReadOnlyDictionary<string, TelemetryAttributeDefinition> EndAttributes,
    [property: JsonPropertyName("status"), JsonPropertyOrder(1)] TelemetrySpanStatusDefinition Status)
{
    [JsonPropertyName("events"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, TelemetryEventDefinition>? Events { get; init; }
}

/// <summary>metrics.ts <c>TelemetryMetricDefinition</c>: only histograms take boundaries.</summary>
public sealed record TelemetryMetricDefinition(
    [property: JsonPropertyName("kind")] TelemetryMetricKind Kind,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("attributes"), JsonPropertyOrder(1)] IReadOnlyDictionary<string, TelemetryAttributeDefinition> Attributes)
{
    [JsonPropertyName("unit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Unit { get; init; }
    [JsonPropertyName("boundaries"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<double>? Boundaries { get; init; }
}

/// <summary>index.ts <c>TelemetrySchemaDefinition</c>: serializable span and metric vocabulary. Build its maps in the order they
/// should serialize.</summary>
public sealed record TelemetrySchemaDefinition(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("spans")] IReadOnlyDictionary<string, TelemetrySpanDefinition> Spans)
{
    [JsonPropertyName("metrics"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, TelemetryMetricDefinition>? Metrics { get; init; }
}

/// <summary>index.ts <c>defineTelemetrySchema</c>, <c>createTypedSpanStarter</c> and metrics.ts <c>createTypedMetrics</c>.
/// TypeScript checks the schemas' vocabulary at compile time; here the same rules are checked when a schema is defined or bound and
/// on every typed call, throwing <see cref="ArgumentException"/> for what TypeScript would not compile. The adapters themselves
/// still trust their inputs.</summary>
public static class TelemetrySchema
{
    /// <summary>Checks the definition's shape and returns the same instance.</summary>
    public static TelemetrySchemaDefinition Define(TelemetrySchemaDefinition schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        foreach (var (name, span) in schema.Spans)
        {
            CheckDefinitions(span.StartAttributes, $"span '{name}' start attributes", metric: false);
            CheckDefinitions(span.EndAttributes, $"span '{name}' end attributes", metric: false);
            foreach (var (eventName, definition) in span.Events ?? new Dictionary<string, TelemetryEventDefinition>())
                CheckDefinitions(definition.Attributes, $"span '{name}' event '{eventName}'", metric: false);
        }
        foreach (var (name, metric) in schema.Metrics ?? new Dictionary<string, TelemetryMetricDefinition>())
        {
            if (metric.Kind != TelemetryMetricKind.Histogram && metric.Boundaries is not null)
                throw new ArgumentException($"Telemetry metric '{name}' is not a histogram and takes no boundaries.", nameof(schema));
            CheckDefinitions(metric.Attributes, $"metric '{name}'", metric: true);
        }
        return schema;
    }

    /// <summary>Binds an explicit parent context to the combined span vocabulary of one or more schemas; span names must be unique
    /// across them.</summary>
    public static TypedSpanStarter CreateTypedSpanStarter(ITelemetryContext context, params TelemetrySchemaDefinition[] schemas)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spans = new Dictionary<string, TelemetrySpanDefinition>(StringComparer.Ordinal);
        foreach (var schema in Schemas(schemas))
            foreach (var (name, definition) in schema.Spans)
                if (!spans.TryAdd(name, definition))
                    throw new ArgumentException($"Duplicate telemetry span name '{name}'.", nameof(schemas));
        return new(context, spans);
    }

    /// <summary>Binds instrument names, kinds, metadata and exact measurement attributes to one or more schemas; metric names must be
    /// unique across them.</summary>
    public static TypedTelemetryMetrics CreateTypedMetrics(ITelemetryMetrics metrics, params TelemetrySchemaDefinition[] schemas)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var definitions = new Dictionary<string, TelemetryMetricDefinition>(StringComparer.Ordinal);
        foreach (var schema in Schemas(schemas))
            foreach (var (name, definition) in schema.Metrics ?? new Dictionary<string, TelemetryMetricDefinition>())
                if (!definitions.TryAdd(name, definition))
                    throw new ArgumentException($"Duplicate telemetry metric name '{name}'.", nameof(schemas));
        return new(metrics, definitions);
    }

    private static TelemetrySchemaDefinition[] Schemas(TelemetrySchemaDefinition[] schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        if (schemas.Length == 0) throw new ArgumentException("At least one telemetry schema is required.", nameof(schemas));
        foreach (var schema in schemas) Define(schema);
        return schemas;
    }

    private static void CheckDefinitions(IReadOnlyDictionary<string, TelemetryAttributeDefinition> definitions, string what, bool metric)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var (name, definition) in definitions)
        {
            if (metric && definition.Cardinality == TelemetryCardinality.High)
                throw new ArgumentException($"Telemetry {what} attribute '{name}' is high-cardinality; metrics take low-cardinality attributes.");
            var array = definition.Type is TelemetryAttributeType.StringArray or TelemetryAttributeType.NumberArray or TelemetryAttributeType.BooleanArray;
            if (array ? definition.Values is not null : definition.ElementValues is not null)
                throw new ArgumentException($"Telemetry {what} attribute '{name}' declares {(array ? "values" : "elementValues")} for a {definition.Type} attribute.");
            var element = TelemetryAttributeChecks.ElementType(definition.Type);
            foreach (var value in (array ? definition.ElementValues : definition.Values) ?? [])
                if (value is null || value.Type != element)
                    throw new ArgumentException($"Telemetry {what} attribute '{name}' declares a closed-set value of another type.");
        }
    }
}

/// <summary>index.ts <c>TypedSpanStarter</c>: a span starter bound to one explicit parent context and one or more schemas. Span names
/// must be the schemas', and start attributes exact: every required one present, no undeclared one, each of its declared type and
/// closed set. A violation throws <see cref="ArgumentException"/> before the span starts. The callback receives the typed span and a
/// starter bound to it for children.</summary>
public sealed class TypedSpanStarter
{
    private readonly IReadOnlyDictionary<string, TelemetrySpanDefinition> _spans;

    internal TypedSpanStarter(ITelemetryContext context, IReadOnlyDictionary<string, TelemetrySpanDefinition> spans)
    {
        Context = context;
        _spans = spans;
    }

    /// <summary>The parent context new spans start under.</summary>
    public ITelemetryContext Context { get; }

    public Task<T> StartSpanAsync<T>(string name, SpanAttributes? attributes, Func<SchemaTelemetrySpan, TypedSpanStarter, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var definition = Definition(name);
        TelemetryAttributeChecks.Check(definition.StartAttributes, attributes, requireRequired: true, $"span '{name}' start");
        return Context.StartSpanAsync(new SpanOptions(name) { Attributes = attributes },
            span => callback(new SchemaTelemetrySpan(span, name, definition), new TypedSpanStarter(span, _spans)));
    }

    public Task<T> StartSpanAsync<T>(string name, SpanAttributes? attributes, Func<SchemaTelemetrySpan, TypedSpanStarter, T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return StartSpanAsync(name, attributes, (span, starter) => Task.FromResult(callback(span, starter)));
    }

    public Task StartSpanAsync(string name, SpanAttributes? attributes, Func<SchemaTelemetrySpan, TypedSpanStarter, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return StartSpanAsync(name, attributes, async (span, starter) => { await callback(span, starter).ConfigureAwait(false); return true; });
    }

    public Task StartSpanAsync(string name, SpanAttributes? attributes, Action<SchemaTelemetrySpan, TypedSpanStarter> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return StartSpanAsync(name, attributes, (span, starter) => { callback(span, starter); return Task.FromResult(true); });
    }

    private TelemetrySpanDefinition Definition(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _spans.TryGetValue(name, out var definition) ? definition
            : throw new ArgumentException($"Unknown telemetry span '{name}'.", nameof(name));
    }
}

/// <summary>index.ts <c>SchemaTelemetrySpan</c>: a span whose events and end attributes are its schema's. <see cref="Span"/> is the
/// adapter's span itself: pass it wherever a context is expected (a parent, a link target, a histogram exemplar).</summary>
public sealed class SchemaTelemetrySpan
{
    private readonly TelemetrySpanDefinition _definition;

    internal SchemaTelemetrySpan(ITelemetrySpan span, string name, TelemetrySpanDefinition definition)
    {
        Span = span;
        Name = name;
        _definition = definition;
    }

    public ITelemetrySpan Span { get; }
    public string Name { get; }
    public ITelemetryMetrics? Metrics => Span.Metrics;
    public ITelemetryPropagation? Propagation => Span.Propagation;

    /// <summary>Only declared events, with exact attributes (required ones present).</summary>
    public void AddEvent(string name, SpanAttributes? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_definition.Events is null || !_definition.Events.TryGetValue(name, out var definition))
            throw new ArgumentException($"Telemetry span '{Name}' declares no event '{name}'.", nameof(name));
        TelemetryAttributeChecks.Check(definition.Attributes, attributes, requireRequired: true, $"span '{Name}' event '{name}'");
        Span.AddEvent(name, attributes);
    }

    /// <summary>Only declared end attributes, all optional.</summary>
    public void SetAttributes(SpanAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        TelemetryAttributeChecks.Check(_definition.EndAttributes, attributes, requireRequired: false, $"span '{Name}' end");
        Span.SetAttributes(attributes);
    }

    public void AddLink(SpanLink link) => Span.AddLink(link);
    public void SetStatus(SpanStatus status) => Span.SetStatus(status);
}

/// <summary>The exact-attribute rules TypeScript applies to typed spans, events and measurements.</summary>
internal static class TelemetryAttributeChecks
{
    public static TelemetryAttributeType ElementType(TelemetryAttributeType type) => type switch
    {
        TelemetryAttributeType.StringArray => TelemetryAttributeType.String,
        TelemetryAttributeType.NumberArray => TelemetryAttributeType.Number,
        TelemetryAttributeType.BooleanArray => TelemetryAttributeType.Boolean,
        _ => type
    };

    public static void Check(IReadOnlyDictionary<string, TelemetryAttributeDefinition> definitions, SpanAttributes? attributes,
        bool requireRequired, string what)
    {
        if (attributes is not null)
        {
            foreach (var (name, value) in attributes)
            {
                if (!definitions.TryGetValue(name, out var definition))
                    throw new ArgumentException($"Telemetry {what} attribute '{name}' is not declared.", nameof(attributes));
                if (value is null) continue;
                if (value.Type != definition.Type)
                    throw new ArgumentException($"Telemetry {what} attribute '{name}' must be a {definition.Type} value.", nameof(attributes));
                if (value.IsArray ? definition.ElementValues is { } elements && value.Elements().Any(item => !elements.Contains(item))
                    : definition.Values is { } values && !values.Contains(value))
                    throw new ArgumentException($"Telemetry {what} attribute '{name}' is not one of its declared values.", nameof(attributes));
            }
        }
        if (!requireRequired) return;
        foreach (var (name, definition) in definitions)
            if (definition.Required == true && attributes?[name] is null)
                throw new ArgumentException($"Telemetry {what} attribute '{name}' is required.", nameof(attributes));
    }
}
