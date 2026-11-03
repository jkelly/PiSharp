// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/memory.ts.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Telemetry;

/// <summary>memory.ts <c>RecordedTelemetryEvent</c>.</summary>
public sealed record RecordedTelemetryEvent(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("attributes")] SpanAttributes Attributes);

/// <summary>memory.ts <c>RecordedTelemetryLink</c>: the target's carrier.</summary>
public sealed record RecordedTelemetryLink(
    [property: JsonPropertyName("context")] IReadOnlyDictionary<string, string> Context,
    [property: JsonPropertyName("attributes")] SpanAttributes Attributes);

/// <summary>memory.ts <c>RecordedTelemetrySpan</c>. <see cref="ParentId"/> is the local parent ID, or null for roots and parents owned
/// by another recorder.</summary>
public sealed record RecordedTelemetrySpan(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("parentId")] int? ParentId,
    [property: JsonPropertyName("context")] IReadOnlyDictionary<string, string> Context,
    [property: JsonPropertyName("parentContext")] IReadOnlyDictionary<string, string>? ParentContext,
    [property: JsonPropertyName("links")] IReadOnlyList<RecordedTelemetryLink> Links,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("attributes")] SpanAttributes Attributes,
    [property: JsonPropertyName("events")] IReadOnlyList<RecordedTelemetryEvent> Events,
    [property: JsonPropertyName("status")] SpanStatus Status,
    [property: JsonPropertyName("settled")] bool Settled,
    [property: JsonPropertyName("endSequence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndSequence);

/// <summary>memory.ts <c>RecordedTelemetryMeasurement</c>: <see cref="Context"/> is the exemplar span's carrier, or null.</summary>
public sealed record RecordedTelemetryMeasurement(
    [property: JsonPropertyName("kind")] TelemetryMetricKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("unit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit,
    [property: JsonPropertyName("boundaries"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<double>? Boundaries,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("attributes")] SpanAttributes Attributes,
    [property: JsonPropertyName("context")] IReadOnlyDictionary<string, string>? Context);

/// <summary>memory.ts <c>InMemoryTelemetryContext</c>: the backend-neutral reference adapter, recording spans and measurements in
/// process memory. Create a fresh instance to isolate tests or independent recording scopes. Carriers are
/// <c>pi-memory-scope</c>, <c>pi-memory-span</c> and <c>pi-memory-trace</c> identities; nothing leaves the process. Recording is
/// thread-safe. <paramref name="timeProvider"/> supplies the wall clock (<c>Date.now</c>) and the timestamp clock
/// (<c>performance.now</c>).</summary>
public sealed class InMemoryTelemetryContext(TimeProvider? timeProvider = null)
    : ITelemetryContext, ITelemetryPropagation, ITelemetryMetrics, ITelemetryStore
{
    private const string ScopeKey = "pi-memory-scope";
    private const string SpanKey = "pi-memory-span";
    private const string TraceKey = "pi-memory-trace";

    // Identity survives settlement without exposing backend IDs on the generic context contract; shared by every recorder, as
    // upstream's module-level WeakMap is.
    private static readonly ConditionalWeakTable<ITelemetryContext, Carrier> ContextCarriers = [];

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly string _scope = Guid.NewGuid().ToString();
    private readonly object _gate = new();
    private readonly List<MutableSpan> _spans = [];
    private readonly List<(double IntervalMs, Measurement Measurement)> _measurements = [];
    private int _nextSpanId = 1;
    private int _nextEndSequence = 1;

    public ITelemetryMetrics? Metrics => this;
    public ITelemetryPropagation? Propagation => this;

    public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => Start(null, options, callback);

    /// <summary>A detached carrier for a known span or extracted parent, including settled spans.</summary>
    public IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ContextCarriers.TryGetValue(context, out var carrier) ? carrier.ToDictionary() : null;
    }

    /// <summary>Missing or non-string (null) carrier identities fall back to this recorder's root context.</summary>
    public ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier)
    {
        if (carrier is null || !carrier.TryGetValue(ScopeKey, out var scope) || !carrier.TryGetValue(SpanKey, out var span)
            || !carrier.TryGetValue(TraceKey, out var traceId) || scope is null || span is null || traceId is null)
            return this;
        var context = new Carrier(scope, span, traceId);
        // Number(span): a span ID this recorder did not issue still has no local parent.
        int? localId = scope == _scope && int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        var parent = new ExtractedParent(this, new Parent(context, localId, null));
        ContextCarriers.AddOrUpdate(parent, context);
        return parent;
    }

    public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) =>
        new Instrument(this, TelemetryMetricKind.Counter, name, options);

    public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null) =>
        new Instrument(this, TelemetryMetricKind.UpDownCounter, name, options);

    public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null) =>
        new Instrument(this, TelemetryMetricKind.Histogram, name, options);

    public Task<StoredTrace?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceId);
        cancellationToken.ThrowIfCancellationRequested();
        var spans = StoredSpans().Where(span => span.TraceId == traceId).ToArray();
        return Task.FromResult(spans.Length > 0 ? new StoredTrace(traceId, spans) : null);
    }

    public Task<TracePage> FindTracesAsync(TraceQuery? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new TraceQuery();
        var limit = query.Limit ?? 20;
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(query), "A trace page size is positive.");
        var after = query.Cursor is null ? ((double, string)?)null : ParseCursor(query.Cursor);
        var grouped = new Dictionary<string, List<StoredSpan>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var span in StoredSpans())
        {
            if (!grouped.TryGetValue(span.TraceId, out var list))
            {
                grouped[span.TraceId] = list = [];
                order.Add(span.TraceId);
            }
            list.Add(span);
        }
        var matches = order.Select(traceId => new StoredTrace(traceId, grouped[traceId])).Where(trace =>
        {
            var first = trace.Spans[0];
            return (query.Name is null || first.Name == query.Name)
                && (query.StartMin is not { } min || first.StartedMs >= min)
                && (query.StartMax is not { } max || first.StartedMs <= max)
                && (after is not { } cursor || first.StartedMs < cursor.Item1
                    || first.StartedMs == cursor.Item1 && string.CompareOrdinal(trace.TraceId, cursor.Item2) > 0)
                && (!query.Errors || trace.Spans.Any(span => span.Status.IsError))
                && (query.Attributes is null || trace.Spans.Any(span => AttributesMatch(span.Attributes, query.Attributes)));
        }).ToList();
        matches.Sort((a, b) =>
        {
            var start = b.Spans[0].StartedMs.CompareTo(a.Spans[0].StartedMs);
            return start != 0 ? start : string.CompareOrdinal(a.TraceId, b.TraceId);
        });
        var traces = matches.Take(limit).ToArray();
        string? next = null;
        if (matches.Count > limit)
        {
            var last = traces[^1];
            next = "[" + JsText.JsonNumber(last.Spans[0].StartedMs) + "," + JsText.Quote(last.TraceId) + "]";
        }
        return Task.FromResult(new TracePage(traces, next));
    }

    /// <summary>Raw measurements as count-one rows, without changing <see cref="GetMetrics"/> snapshots.</summary>
    public Task<IReadOnlyList<StoredMetric>> FindMetricsAsync(MetricQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StoredMetric> rows = _measurements
                .Where(row => row.Measurement.Name == query.Name
                    && (query.StartMin is not { } min || row.IntervalMs >= min)
                    && (query.StartMax is not { } max || row.IntervalMs <= max)
                    && (query.Attributes is null || AttributesMatch(row.Measurement.Attributes, query.Attributes)))
                .Select(row => new StoredMetric(row.IntervalMs, row.Measurement.Name, row.Measurement.Kind, row.Measurement.Unit,
                    SpanAttributes.Copy(row.Measurement.Attributes), 1, row.Measurement.Value, row.Measurement.Value, row.Measurement.Value))
                .ToArray();
            return Task.FromResult(rows);
        }
    }

    /// <summary>Detached measurements in recording order, without aggregation.</summary>
    public IReadOnlyList<RecordedTelemetryMeasurement> GetMetrics()
    {
        lock (_gate)
        {
            return _measurements.Select(row => new RecordedTelemetryMeasurement(row.Measurement.Kind, row.Measurement.Name,
                row.Measurement.Description, row.Measurement.Unit, row.Measurement.Boundaries?.ToArray(), row.Measurement.Value,
                SpanAttributes.Copy(row.Measurement.Attributes), row.Measurement.Context?.ToDictionary())).ToArray();
        }
    }

    /// <summary>Detached snapshots in span-start order.</summary>
    public IReadOnlyList<RecordedTelemetrySpan> GetSpans()
    {
        lock (_gate)
        {
            return _spans.Select(span => new RecordedTelemetrySpan(span.Id, span.ParentId, span.Context.ToDictionary(),
                span.ParentContext?.ToDictionary(),
                span.Links.Select(link => new RecordedTelemetryLink(link.Context.ToDictionary(), SpanAttributes.Copy(link.Attributes))).ToArray(),
                span.Name, SpanAttributes.Copy(span.Attributes),
                span.Events.Select(item => new RecordedTelemetryEvent(item.Name, SpanAttributes.Copy(item.Attributes))).ToArray(),
                span.Status, span.Settled, span.EndSequence)).ToArray();
        }
    }

    private StoredSpan[] StoredSpans()
    {
        lock (_gate)
        {
            var spans = _spans.Where(span => span.Settled).Select(span => new StoredSpan(
                span.Context.Id, span.Context.Trace, span.ParentContext?.Id, span.Name, span.StartedMs, span.EndedMs,
                SpanAttributes.Copy(span.Attributes),
                span.Events.Select(item => new StoredSpanEvent(item.Name, item.TimeMs, SpanAttributes.Copy(item.Attributes))).ToArray(),
                span.Links.Select(link => new StoredSpanLink(link.Context.Trace, link.Context.Id, SpanAttributes.Copy(link.Attributes))).ToArray(),
                span.Status)).ToList();
            spans.Sort((a, b) =>
            {
                var start = a.StartedMs.CompareTo(b.StartedMs);
                return start != 0 ? start : string.CompareOrdinal(a.Id, b.Id);
            });
            return [.. spans];
        }
    }

    /// <summary>Query equality is JavaScript's <c>===</c>: scalars by value, a missing name matching only an undefined query value.</summary>
    private static bool AttributesMatch(SpanAttributes attributes, SpanAttributes query)
    {
        foreach (var (name, value) in query)
            if (value is null ? attributes[name] is not null : !value.StrictlyEquals(attributes[name])) return false;
        return true;
    }

    private static (double, string) ParseCursor(string cursor)
    {
        using var document = JsonDocument.Parse(cursor);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 || root[0].ValueKind != JsonValueKind.Number
            || root[1].ValueKind != JsonValueKind.String)
            throw new ArgumentException("The trace cursor is not one this store issued.", nameof(cursor));
        return (root[0].GetDouble(), root[1].GetString()!);
    }

    private double NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private Task<T> Start<T>(Parent? parent, SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        bool settled;
        lock (_gate) settled = parent?.Span?.Settled == true;
        return settled ? StartInert(options, callback) : Record(parent, options, callback);
    }

    private Task<T> StartInert<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
        NoopTelemetry.Context.StartSpanAsync(options, _ => callback(new InertSpan(this)));

    private async Task<T> Record<T>(Parent? parent, SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
    {
        var recorded = CreateSpan(parent, options);
        var span = new RecordingSpan(this, recorded);
        ContextCarriers.AddOrUpdate(span, recorded.Context);
        try
        {
            return await callback(span).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (_gate)
                if (!recorded.ExplicitStatus) recorded.Status = SpanStatus.Failed(error.GetType().Name, error.Message);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                recorded.Settled = true;
                recorded.EndedMs = recorded.StartedMs + _time.GetElapsedTime(recorded.Clock).TotalMilliseconds;
                recorded.EndSequence = _nextEndSequence++;
            }
        }
    }

    private MutableSpan CreateSpan(Parent? parent, SpanOptions options)
    {
        var attributes = SpanAttributes.Copy(options.Attributes);
        var links = new List<(Carrier, SpanAttributes)>();
        foreach (var link in options.Links ?? [])
            if (CopyLink(link) is { } copy) links.Add(copy);
        lock (_gate)
        {
            var id = _nextSpanId++;
            var span = new MutableSpan
            {
                StartedMs = NowMs(),
                Clock = _time.GetTimestamp(),
                Id = id,
                ParentId = parent?.LocalId,
                Context = new Carrier(_scope, id.ToString(CultureInfo.InvariantCulture), parent?.Context.Trace ?? $"{_scope}:{id}"),
                ParentContext = parent?.Context,
                Links = links,
                Name = options.Name,
                Attributes = attributes
            };
            _spans.Add(span);
            return span;
        }
    }

    private static (Carrier Context, SpanAttributes Attributes)? CopyLink(SpanLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return ContextCarriers.TryGetValue(link.Context, out var context) ? (context, SpanAttributes.Copy(link.Attributes)) : null;
    }

    private void Measure(TelemetryMetricKind kind, string name, string? description, string? unit, double[]? boundaries,
        double value, SpanAttributes? attributes, ITelemetryContext? context)
    {
        Carrier? carrier = null;
        if (context is not null) ContextCarriers.TryGetValue(context, out carrier);
        var measurement = new Measurement(kind, name, description, unit, boundaries, value, SpanAttributes.Copy(attributes), carrier);
        lock (_gate) _measurements.Add((Math.Floor(NowMs() / 10_000) * 10_000, measurement));
    }

    /// <summary>An immutable carrier identity; <see cref="ToDictionary"/> returns a detached copy.</summary>
    private sealed record Carrier(string Scope, string Span, string Trace)
    {
        public string Id => $"{Scope}:{Span}";

        public Dictionary<string, string> ToDictionary() => new(StringComparer.Ordinal)
        {
            [ScopeKey] = Scope, [SpanKey] = Span, [TraceKey] = Trace
        };
    }

    private sealed record Parent(Carrier Context, int? LocalId, MutableSpan? Span);

    private sealed record Measurement(TelemetryMetricKind Kind, string Name, string? Description, string? Unit, double[]? Boundaries,
        double Value, SpanAttributes Attributes, Carrier? Context);

    private sealed class MutableSpan
    {
        public double StartedMs;
        public long Clock;
        public double EndedMs;
        public int Id;
        public int? ParentId;
        public Carrier Context = null!;
        public Carrier? ParentContext;
        public List<(Carrier Context, SpanAttributes Attributes)> Links = [];
        public string Name = "";
        public SpanAttributes Attributes = new();
        public List<(string Name, double TimeMs, SpanAttributes Attributes)> Events = [];
        public SpanStatus Status = SpanStatus.Ok;
        public bool ExplicitStatus;
        public bool Settled;
        public int? EndSequence;
    }

    private sealed class RecordingSpan(InMemoryTelemetryContext owner, MutableSpan recorded) : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => owner;
        public ITelemetryPropagation? Propagation => owner;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
            owner.Start(new Parent(recorded.Context, recorded.Id, recorded), options, callback);

        public void AddLink(SpanLink link)
        {
            var copy = CopyLink(link);
            lock (owner._gate)
                if (!recorded.Settled && copy is { } value) recorded.Links.Add(value);
        }

        public void AddEvent(string name, SpanAttributes? attributes = null)
        {
            ArgumentNullException.ThrowIfNull(name);
            var copy = SpanAttributes.Copy(attributes);
            lock (owner._gate)
                if (!recorded.Settled) recorded.Events.Add((name, owner._time.GetElapsedTime(recorded.Clock).TotalMilliseconds, copy));
        }

        public void SetAttributes(SpanAttributes attributes)
        {
            lock (owner._gate)
                if (!recorded.Settled) recorded.Attributes.Merge(attributes);
        }

        public void SetStatus(SpanStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            lock (owner._gate)
            {
                if (recorded.Settled) return;
                recorded.Status = status;
                recorded.ExplicitStatus = true;
            }
        }
    }

    /// <summary>A child of a settled span: nothing is recorded, but the recorder's capabilities stay available.</summary>
    private sealed class InertSpan(InMemoryTelemetryContext owner) : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => owner;
        public ITelemetryPropagation? Propagation => owner;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(callback);
            return owner.StartInert(options, callback);
        }

        public void AddLink(SpanLink link) { }
        public void AddEvent(string name, SpanAttributes? attributes = null) { }
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }

    private sealed class ExtractedParent(InMemoryTelemetryContext owner, Parent parent) : ITelemetryContext
    {
        public ITelemetryMetrics? Metrics => owner;
        public ITelemetryPropagation? Propagation => owner;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => owner.Start(parent, options, callback);
    }

    private sealed class Instrument : ITelemetryCounter, ITelemetryUpDownCounter, ITelemetryHistogram
    {
        private readonly InMemoryTelemetryContext _owner;
        private readonly TelemetryMetricKind _kind;
        private readonly string _name;
        private readonly string? _description;
        private readonly string? _unit;
        private readonly double[]? _boundaries;

        public Instrument(InMemoryTelemetryContext owner, TelemetryMetricKind kind, string name, InstrumentOptions? options)
        {
            ArgumentNullException.ThrowIfNull(name);
            _owner = owner;
            _kind = kind;
            _name = name;
            _description = options?.Description;
            _unit = options?.Unit;
            _boundaries = kind == TelemetryMetricKind.Histogram && options is HistogramOptions { Boundaries: { } boundaries } ? [.. boundaries] : null;
        }

        public void Add(double value, SpanAttributes? attributes = null) =>
            _owner.Measure(_kind, _name, _description, _unit, _boundaries, value, attributes, null);

        public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null) =>
            _owner.Measure(_kind, _name, _description, _unit, _boundaries, value, attributes, context);
    }
}
