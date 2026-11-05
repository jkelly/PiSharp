// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/index.ts.
using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;

namespace PiSharp.Telemetry;

/// <summary>index.ts <c>TelemetryAttributeType</c>: the JSON type of an attribute value.</summary>
[JsonConverter(typeof(AttributeTypeConverter))]
public enum TelemetryAttributeType { String, Number, Boolean, StringArray, NumberArray, BooleanArray }

/// <summary>index.ts <c>AttributeValue</c>: a string, a number (a JavaScript binary64), a boolean, or an array of one of them. Values
/// are immutable: arrays are copied when converted. Null stands for <c>undefined</c> wherever an attribute may be absent.</summary>
[JsonConverter(typeof(AttributeValueJsonConverter))]
public sealed class AttributeValue : IEquatable<AttributeValue>
{
    private readonly object _value;

    private AttributeValue(TelemetryAttributeType type, object value) { Type = type; _value = value; }

    public TelemetryAttributeType Type { get; }
    public bool IsArray => Type is TelemetryAttributeType.StringArray or TelemetryAttributeType.NumberArray or TelemetryAttributeType.BooleanArray;

    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator AttributeValue?(string? value) => value is null ? null : new(TelemetryAttributeType.String, value);
    public static implicit operator AttributeValue(double value) => new(TelemetryAttributeType.Number, value);
    public static implicit operator AttributeValue?(double? value) => value is { } number ? new(TelemetryAttributeType.Number, number) : null;
    public static implicit operator AttributeValue(bool value) => new(TelemetryAttributeType.Boolean, value);
    public static implicit operator AttributeValue?(bool? value) => value is { } flag ? new(TelemetryAttributeType.Boolean, flag) : null;
    [return: NotNullIfNotNull(nameof(values))]
    public static implicit operator AttributeValue?(string[]? values) => values is null ? null : new(TelemetryAttributeType.StringArray, ImmutableArray.Create(values));
    [return: NotNullIfNotNull(nameof(values))]
    public static implicit operator AttributeValue?(double[]? values) => values is null ? null : new(TelemetryAttributeType.NumberArray, ImmutableArray.Create(values));
    [return: NotNullIfNotNull(nameof(values))]
    public static implicit operator AttributeValue?(bool[]? values) => values is null ? null : new(TelemetryAttributeType.BooleanArray, ImmutableArray.Create(values));
    public static implicit operator AttributeValue(ImmutableArray<string> values) => new(TelemetryAttributeType.StringArray, values.IsDefault ? [] : values);
    public static implicit operator AttributeValue(ImmutableArray<double> values) => new(TelemetryAttributeType.NumberArray, values.IsDefault ? [] : values);
    public static implicit operator AttributeValue(ImmutableArray<bool> values) => new(TelemetryAttributeType.BooleanArray, values.IsDefault ? [] : values);

    public string GetString() => Type == TelemetryAttributeType.String ? (string)_value : throw WrongType();
    public double GetNumber() => Type == TelemetryAttributeType.Number ? (double)_value : throw WrongType();
    public bool GetBoolean() => Type == TelemetryAttributeType.Boolean ? (bool)_value : throw WrongType();
    public ImmutableArray<string> GetStrings() => Type == TelemetryAttributeType.StringArray ? (ImmutableArray<string>)_value : throw WrongType();
    public ImmutableArray<double> GetNumbers() => Type == TelemetryAttributeType.NumberArray ? (ImmutableArray<double>)_value : throw WrongType();
    public ImmutableArray<bool> GetBooleans() => Type == TelemetryAttributeType.BooleanArray ? (ImmutableArray<bool>)_value : throw WrongType();

    /// <summary>The scalar elements of an array value, each as its own attribute value; a scalar returns itself.</summary>
    public IEnumerable<AttributeValue> Elements() => Type switch
    {
        TelemetryAttributeType.StringArray => GetStrings().Select(static item => (AttributeValue)item!),
        TelemetryAttributeType.NumberArray => GetNumbers().Select(static item => (AttributeValue)item),
        TelemetryAttributeType.BooleanArray => GetBooleans().Select(static item => (AttributeValue)item),
        _ => [this]
    };

    /// <summary>JavaScript's <c>===</c> for the scalar values a query compares; arrays are never strictly equal to a copy.</summary>
    internal bool StrictlyEquals(AttributeValue? other) => !IsArray && other is not null && !other.IsArray && Equals(other);

    public bool Equals(AttributeValue? other) => other is not null && other.Type == Type && Type switch
    {
        TelemetryAttributeType.String => GetString() == other.GetString(),
        TelemetryAttributeType.Number => GetNumber().Equals(other.GetNumber()),
        TelemetryAttributeType.Boolean => GetBoolean() == other.GetBoolean(),
        TelemetryAttributeType.StringArray => GetStrings().SequenceEqual(other.GetStrings(), StringComparer.Ordinal),
        TelemetryAttributeType.NumberArray => GetNumbers().SequenceEqual(other.GetNumbers()),
        _ => GetBooleans().SequenceEqual(other.GetBooleans())
    };

    public override bool Equals(object? obj) => obj is AttributeValue other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        foreach (var element in IsArray ? Elements() : []) hash.Add(element.GetHashCode());
        if (!IsArray) hash.Add(_value);
        return hash.ToHashCode();
    }

    /// <summary>The value as JSON.stringify writes it.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        JsText.Write(sb, this);
        return sb.ToString();
    }

    private InvalidOperationException WrongType() => new($"The attribute value is a {Type} value.");
}

/// <summary>index.ts <c>SpanAttributes</c>: attribute values by name, in insertion order. A null value is <c>undefined</c>: adapters
/// leave it out when they copy attributes. The indexer returns null for a missing name.</summary>
[JsonConverter(typeof(SpanAttributesJsonConverter))]
public sealed class SpanAttributes : IReadOnlyDictionary<string, AttributeValue?>, IEquatable<SpanAttributes>
{
    private readonly OrderedDictionary<string, AttributeValue?> _items = new(StringComparer.Ordinal);

    public SpanAttributes() { }

    public SpanAttributes(IEnumerable<KeyValuePair<string, AttributeValue?>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        foreach (var (name, value) in attributes) this[name] = value;
    }

    public AttributeValue? this[string name]
    {
        get => _items.TryGetValue(name, out var value) ? value : null;
        set => _items[name] = value;
    }

    AttributeValue? IReadOnlyDictionary<string, AttributeValue?>.this[string key] => _items[key];

    public int Count => _items.Count;
    public IEnumerable<string> Keys => _items.Keys;
    public IEnumerable<AttributeValue?> Values => _items.Values;

    /// <summary>Collection-initializer support; a name already present is an error, as for a dictionary.</summary>
    public void Add(string name, AttributeValue? value) => _items.Add(name, value);
    public bool Remove(string name) => _items.Remove(name);
    public bool ContainsKey(string key) => _items.ContainsKey(key);
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out AttributeValue? value) => _items.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, AttributeValue?>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>memory.ts <c>copyAttributes</c>: a detached copy without undefined values.</summary>
    internal static SpanAttributes Copy(SpanAttributes? source)
    {
        var copy = new SpanAttributes();
        if (source is null) return copy;
        foreach (var (name, value) in source) if (value is not null) copy._items[name] = value;
        return copy;
    }

    /// <summary><c>{ ...this, ...copyAttributes(update) }</c>: present names keep their place, new names are appended.</summary>
    internal void Merge(SpanAttributes? update)
    {
        if (update is null) return;
        foreach (var (name, value) in update) if (value is not null) _items[name] = value;
    }

    /// <summary>Deep equality, ignoring order, as <c>deepStrictEqual</c> compares objects.</summary>
    public bool Equals(SpanAttributes? other)
    {
        if (other is null || other.Count != Count) return false;
        foreach (var (name, value) in _items)
            if (!other._items.TryGetValue(name, out var theirs) || !Equals(value, theirs)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is SpanAttributes other && Equals(other);
    public override int GetHashCode() => Count;

    /// <summary>The attributes as JSON.stringify writes the object.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (name, value) in _items)
        {
            if (value is null) continue;
            if (!first) sb.Append(',');
            first = false;
            JsText.Quote(sb, name);
            sb.Append(':');
            JsText.Write(sb, value);
        }
        return sb.Append('}').ToString();
    }
}

/// <summary>index.ts <c>TelemetryPropagation</c>: the optional transport capability of the application's adapter. Carriers
/// (<c>TelemetryCarrier</c>) are opaque, JSON-serializable string maps owned by the adapter; they carry identities, never secrets.</summary>
public interface ITelemetryPropagation
{
    /// <summary>A detached carrier for a context the adapter knows, or null.</summary>
    IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context);

    /// <summary>Absent or unknown carriers use the root; recognized identifiers are opaque strings.</summary>
    ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier);
}

/// <summary>index.ts <c>SpanLink</c>: a link to a context, which may be a settled span or an extracted remote parent.</summary>
public sealed record SpanLink(ITelemetryContext Context, SpanAttributes? Attributes = null);

/// <summary>index.ts <c>SpanOptions</c>. A name converts to options with no attributes or links.</summary>
public sealed record SpanOptions(string Name)
{
    public SpanAttributes? Attributes { get; init; }
    public IReadOnlyList<SpanLink>? Links { get; init; }

    public static implicit operator SpanOptions(string name) => new(name);
}

public enum SpanStatusCode { Ok, Error }

/// <summary>The bounded error of an error status.</summary>
public sealed record SpanError(string Name, string Message);

/// <summary>index.ts <c>SpanStatus</c>: <c>{ status: "ok" }</c> or <c>{ status: "error", error? }</c>.</summary>
[JsonConverter(typeof(SpanStatusJsonConverter))]
public sealed record SpanStatus
{
    private SpanStatus(SpanStatusCode status, SpanError? error) { Status = status; Error = error; }

    public SpanStatusCode Status { get; }
    public SpanError? Error { get; }
    public bool IsError => Status == SpanStatusCode.Error;

    public static SpanStatus Ok { get; } = new(SpanStatusCode.Ok, null);
    public static SpanStatus Failed(SpanError? error = null) => new(SpanStatusCode.Error, error);
    public static SpanStatus Failed(string name, string message) => new(SpanStatusCode.Error, new(name, message));
}

/// <summary>index.ts <c>TelemetryContext</c>: the adapter contract for trusted typed inputs; malformed input behaviour is
/// unspecified. Adapter capabilities remain available on child spans and extracted parents.</summary>
public interface ITelemetryContext
{
    ITelemetryMetrics? Metrics { get; }
    ITelemetryPropagation? Propagation { get; }

    /// <summary>Starts a span and admits <paramref name="callback"/> once, synchronously. The span settles when the callback's task
    /// does; its result or exception (the same instance) is the returned task's.</summary>
    Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback);
}

/// <summary>index.ts <c>TelemetrySpan</c>. Calls after the span settles are inert.</summary>
public interface ITelemetrySpan : ITelemetryContext
{
    void AddLink(SpanLink link);
    void AddEvent(string name, SpanAttributes? attributes = null);
    void SetAttributes(SpanAttributes attributes);
    void SetStatus(SpanStatus status);
}

/// <summary>Synchronous and result-less callbacks for <see cref="ITelemetryContext.StartSpanAsync{T}"/> (upstream's callbacks return
/// <c>T | Promise&lt;T&gt;</c>). A synchronous throw rejects the returned task, as it does upstream.</summary>
public static class TelemetryContextExtensions
{
    public static Task<T> StartSpanAsync<T>(this ITelemetryContext context, SpanOptions options, Func<ITelemetrySpan, T> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);
        return context.StartSpanAsync(options, span => Task.FromResult(callback(span)));
    }

    public static Task StartSpanAsync(this ITelemetryContext context, SpanOptions options, Func<ITelemetrySpan, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);
        return context.StartSpanAsync(options, async span => { await callback(span).ConfigureAwait(false); return true; });
    }

    public static Task StartSpanAsync(this ITelemetryContext context, SpanOptions options, Action<ITelemetrySpan> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);
        return context.StartSpanAsync(options, span => { callback(span); return Task.FromResult(true); });
    }
}

/// <summary>A settled span event; its time is relative to the span start.</summary>
public sealed record StoredSpanEvent(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("timeMs")] double TimeMs,
    [property: JsonPropertyName("attributes")] SpanAttributes Attributes);

/// <summary>A link of a settled span, by the target's carrier-shaped identities.</summary>
public sealed record StoredSpanLink(
    [property: JsonPropertyName("traceId")] string TraceId,
    [property: JsonPropertyName("spanId")] string SpanId,
    [property: JsonPropertyName("attributes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpanAttributes? Attributes = null);

/// <summary>index.ts <c>StoredSpan</c>: a settled span snapshot, independent of the recording adapter. Times are milliseconds since
/// the Unix epoch; event times are relative to the span start.</summary>
public sealed record StoredSpan(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("traceId")] string TraceId,
    [property: JsonPropertyName("parentId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("startedMs")] double StartedMs,
    [property: JsonPropertyName("endedMs")] double EndedMs,
    [property: JsonPropertyName("attributes")] SpanAttributes Attributes,
    [property: JsonPropertyName("events")] IReadOnlyList<StoredSpanEvent> Events,
    [property: JsonPropertyName("links")] IReadOnlyList<StoredSpanLink> Links,
    [property: JsonPropertyName("status")] SpanStatus Status);

/// <summary>index.ts <c>StoredTrace</c>.</summary>
public sealed record StoredTrace(
    [property: JsonPropertyName("traceId")] string TraceId,
    [property: JsonPropertyName("spans")] IReadOnlyList<StoredSpan> Spans);

/// <summary>index.ts <c>TraceQuery</c>.</summary>
public sealed record TraceQuery
{
    /// <summary>Name of the earliest stored span, with span ID breaking timestamp ties.</summary>
    public string? Name { get; init; }
    /// <summary>All equalities (strings, numbers, booleans) must match one span; that span may be anywhere in the trace.</summary>
    public SpanAttributes? Attributes { get; init; }
    /// <summary>Only traces with an error span.</summary>
    public bool Errors { get; init; }
    /// <summary>Inclusive bounds on the earliest stored span's start time.</summary>
    public double? StartMin { get; init; }
    public double? StartMax { get; init; }
    /// <summary>Positive page size; defaults to 20.</summary>
    public int? Limit { get; init; }
    /// <summary>Opaque continuation from the previous page; retain the same filters.</summary>
    public string? Cursor { get; init; }
}

/// <summary>A page of <see cref="ITelemetryStore.FindTracesAsync"/>: a cursor when more traces match.</summary>
public sealed record TracePage(IReadOnlyList<StoredTrace> Traces, string? Cursor = null);

/// <summary>index.ts <c>MetricQuery</c>.</summary>
public sealed record MetricQuery(string Name)
{
    /// <summary>Inclusive bounds on interval start time.</summary>
    public double? StartMin { get; init; }
    public double? StartMax { get; init; }
    public SpanAttributes? Attributes { get; init; }
}

/// <summary>index.ts <c>StoredMetric</c>.</summary>
public sealed record StoredMetric(
    [property: JsonPropertyName("intervalMs")] double IntervalMs,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] TelemetryMetricKind Kind,
    [property: JsonPropertyName("unit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit,
    [property: JsonPropertyName("attributes")] SpanAttributes Attributes,
    [property: JsonPropertyName("count")] double Count,
    [property: JsonPropertyName("sum")] double Sum,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max);

/// <summary>index.ts <c>TelemetryStore</c>: read settled traces without depending on a storage backend.</summary>
public interface ITelemetryStore
{
    Task<TracePage> FindTracesAsync(TraceQuery? query = null, CancellationToken cancellationToken = default);
    Task<StoredTrace?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMetric>> FindMetricsAsync(MetricQuery query, CancellationToken cancellationToken = default);
}
