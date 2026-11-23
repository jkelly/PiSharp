// PiSharp-only (no upstream Pi source): zero-dependency bridge from PiSharp.Telemetry onto .NET's built-in
// System.Diagnostics.ActivitySource and System.Diagnostics.Metrics.Meter, so hosts using OpenTelemetry,
// .NET Aspire, or custom ActivityListener/MeterListener pipelines can observe PiSharp spans and metrics
// without pulling any external package dependency into PiSharp.Telemetry. Off by default unless explicitly
// instantiated and passed by the host.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace PiSharp.Telemetry;

/// <summary>
/// Opt-in telemetry adapter backed by .NET's built-in <see cref="ActivitySource"/> and <see cref="Meter"/>.
/// Maps PiSharp spans, events, links, W3C <c>traceparent</c>/<c>tracestate</c> propagation, and counters/up-down-counters/histograms
/// directly onto <see cref="System.Diagnostics"/> with zero external dependencies.
/// </summary>
public sealed class DiagnosticsTelemetry : ITelemetryContext, ITelemetryPropagation, ITelemetryMetrics, IDisposable
{
    /// <summary>Default <see cref="ActivitySource"/> and <see cref="Meter"/> name.</summary>
    public const string DefaultSourceName = "PiSharp";

    internal const string ErrorPropertyKey = "pi.telemetry.error";
    internal const string TraceParentKey = "traceparent";
    internal const string TraceStateKey = "tracestate";

    // Context identities survive span settlement and work across DiagnosticsTelemetry instances (for multi-hop propagation
    // and links between spans from different adapters), matching InMemoryTelemetryContext's WeakMap semantics.
    private static readonly ConditionalWeakTable<ITelemetryContext, ContextIdentity> ContextIdentities = [];

    private readonly ActivitySource _source;
    private readonly Meter _meter;
    private readonly bool _ownsSource;
    private readonly bool _ownsMeter;
    private readonly ConcurrentDictionary<string, Counter<double>> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UpDownCounter<double>> _upDownCounters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Creates an adapter owning a new <see cref="ActivitySource"/> and <see cref="Meter"/> with the given name and optional version.</summary>
    public DiagnosticsTelemetry(string sourceName = DefaultSourceName, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        _source = new ActivitySource(sourceName, version);
        _meter = new Meter(sourceName, version);
        _ownsSource = true;
        _ownsMeter = true;
    }

    /// <summary>Creates an adapter wrapping caller-owned <see cref="ActivitySource"/> and <see cref="Meter"/> instances (not disposed by this adapter).</summary>
    public DiagnosticsTelemetry(ActivitySource activitySource, Meter meter)
    {
        ArgumentNullException.ThrowIfNull(activitySource);
        ArgumentNullException.ThrowIfNull(meter);
        _source = activitySource;
        _meter = meter;
        _ownsSource = false;
        _ownsMeter = false;
    }

    /// <summary>The underlying <see cref="ActivitySource"/> emitting PiSharp spans.</summary>
    public ActivitySource ActivitySource => _source;

    /// <summary>The underlying <see cref="Meter"/> emitting PiSharp measurements.</summary>
    public Meter Meter => _meter;

    /// <inheritdoc/>
    public ITelemetryMetrics? Metrics => this;

    /// <inheritdoc/>
    public ITelemetryPropagation? Propagation => this;

    /// <inheritdoc/>
    public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
        StartAsync(parent: null, options, callback);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ContextIdentities.TryGetValue(context, out var identity) ? ToCarrier(identity.Context) : null;
    }

    /// <inheritdoc/>
    public ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier)
    {
        if (carrier is null) return this;
        string? traceParent = null;
        string? traceState = null;
        foreach (var (key, value) in carrier)
        {
            if (string.Equals(key, TraceParentKey, StringComparison.OrdinalIgnoreCase)) traceParent = value;
            else if (string.Equals(key, TraceStateKey, StringComparison.OrdinalIgnoreCase)) traceState = value;
        }
        if (string.IsNullOrWhiteSpace(traceParent)) return this;
        if (!ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parsed) || parsed == default)
            return this;
        var extracted = new ExtractedParent(this, parsed);
        ContextIdentities.AddOrUpdate(extracted, new ContextIdentity(parsed, Activity: null));
        return extracted;
    }

    /// <inheritdoc/>
    public ITelemetryCounter Counter(string name, InstrumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var description = options?.Description;
        var unit = options?.Unit;
        var instrument = _counters.GetOrAdd(name, static (metricName, state) =>
            state.Meter.CreateCounter<double>(metricName, state.Unit, state.Description), (Meter: _meter, Unit: unit, Description: description));
        return new CounterAdapter(instrument);
    }

    /// <inheritdoc/>
    public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var description = options?.Description;
        var unit = options?.Unit;
        var instrument = _upDownCounters.GetOrAdd(name, static (metricName, state) =>
            state.Meter.CreateUpDownCounter<double>(metricName, state.Unit, state.Description), (Meter: _meter, Unit: unit, Description: description));
        return new UpDownCounterAdapter(instrument);
    }

    /// <inheritdoc/>
    public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var description = options?.Description;
        var unit = options?.Unit;
        double[]? boundaries = options?.Boundaries is { Count: > 0 } b ? [.. b] : null;
        var instrument = _histograms.GetOrAdd(name, static (metricName, state) =>
            state.Boundaries is { Length: > 0 }
                ? state.Meter.CreateHistogram<double>(metricName, state.Unit, state.Description,
                    tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = state.Boundaries })
                : state.Meter.CreateHistogram<double>(metricName, state.Unit, state.Description),
            (Meter: _meter, Unit: unit, Description: description, Boundaries: boundaries));
        return new HistogramAdapter(instrument);
    }

    /// <summary>Disposes owned <see cref="ActivitySource"/> and <see cref="Meter"/> instances.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsSource) _source.Dispose();
        if (_ownsMeter) _meter.Dispose();
    }

    private Task<T> StartAsync<T>(ParentRef? parent, SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        if (parent?.Span is { IsSettled: true })
            return StartInertAsync(options, callback);
        return RecordAsync(parent, options, callback);
    }

    private Task<T> StartInertAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
        NoopTelemetry.Context.StartSpanAsync(options, _ => callback(new InertSpan(this)));

    private async Task<T> RecordAsync<T>(ParentRef? parent, SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
    {
        var initialTags = ToTags(options.Attributes);
        var initialLinks = ToLinks(options.Links);

        var savedCurrent = Activity.Current;
        Activity? activity = null;
        try
        {
            // When starting a root span (parent is null), clear ambient Activity.Current so ActivitySource.StartActivity
            // does not inherit an unrelated ambient parent.
            if (parent is null) Activity.Current = null;
            activity = _source.StartActivity(
                options.Name,
                ActivityKind.Internal,
                parent?.Context ?? default,
                initialTags,
                initialLinks);
        }
        finally
        {
            if (activity is null) Activity.Current = savedCurrent;
        }

        if (activity is null)
        {
            // No listener sampled this activity; run the callback synchronously while keeping adapter capabilities.
            return await callback(new InertSpan(this)).ConfigureAwait(false);
        }

        var span = new DiagnosticsSpan(this, activity);
        ContextIdentities.AddOrUpdate(span, new ContextIdentity(activity.Context, activity));
        try
        {
            return await callback(span).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            span.ApplyFailureIfUnset(error);
            throw;
        }
        finally
        {
            span.Settle();
            activity.Dispose();
            Activity.Current = savedCurrent;
        }
    }

    private static Dictionary<string, string> ToCarrier(ActivityContext context)
    {
        var carrier = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TraceParentKey] = $"00-{context.TraceId.ToHexString()}-{context.SpanId.ToHexString()}-{(byte)context.TraceFlags:x2}",
        };
        if (!string.IsNullOrEmpty(context.TraceState))
            carrier[TraceStateKey] = context.TraceState;
        return carrier;
    }

    private static IEnumerable<KeyValuePair<string, object?>>? ToTags(SpanAttributes? attributes)
    {
        if (attributes is null || attributes.Count == 0) return null;
        var list = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var (name, value) in attributes)
        {
            if (value is not null) list.Add(new KeyValuePair<string, object?>(name, ToObject(value)));
        }
        return list.Count > 0 ? list : null;
    }

    private static ActivityTagsCollection? ToActivityTags(SpanAttributes? attributes)
    {
        if (attributes is null || attributes.Count == 0) return null;
        ActivityTagsCollection? tags = null;
        foreach (var (name, value) in attributes)
        {
            if (value is null) continue;
            tags ??= new ActivityTagsCollection();
            tags[name] = ToObject(value);
        }
        return tags;
    }

    private static KeyValuePair<string, object?>[] ToMeasurementTags(SpanAttributes? attributes)
    {
        if (attributes is null || attributes.Count == 0) return [];
        var list = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var (name, value) in attributes)
        {
            if (value is not null) list.Add(new KeyValuePair<string, object?>(name, ToObject(value)));
        }
        return [.. list];
    }

    private static IEnumerable<ActivityLink>? ToLinks(IReadOnlyList<SpanLink>? links)
    {
        if (links is null || links.Count == 0) return null;
        List<ActivityLink>? result = null;
        foreach (var link in links)
        {
            ArgumentNullException.ThrowIfNull(link);
            if (!ContextIdentities.TryGetValue(link.Context, out var identity)) continue;
            result ??= new List<ActivityLink>(links.Count);
            result.Add(new ActivityLink(identity.Context, ToActivityTags(link.Attributes)));
        }
        return result;
    }

    private static object ToObject(AttributeValue value) => value.Type switch
    {
        TelemetryAttributeType.String => value.GetString(),
        TelemetryAttributeType.Number => value.GetNumber(),
        TelemetryAttributeType.Boolean => value.GetBoolean(),
        TelemetryAttributeType.StringArray => value.GetStrings().ToArray(),
        TelemetryAttributeType.NumberArray => value.GetNumbers().ToArray(),
        TelemetryAttributeType.BooleanArray => value.GetBooleans().ToArray(),
        TelemetryAttributeType.StringNumberArray => value.GetStringNumbers().Select(ToObject).ToArray(),
        _ => value.ToString(),
    };

    private sealed record ContextIdentity(ActivityContext Context, Activity? Activity);

    private sealed record ParentRef(ActivityContext Context, DiagnosticsSpan? Span);

    private sealed class DiagnosticsSpan(DiagnosticsTelemetry owner, Activity activity) : ITelemetrySpan
    {
        private readonly Lock _gate = new();
        private bool _explicitStatus;
        private bool _settled;

        public bool IsSettled { get { lock (_gate) return _settled; } }
        public ITelemetryMetrics? Metrics => owner;
        public ITelemetryPropagation? Propagation => owner;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
            owner.StartAsync(new ParentRef(activity.Context, this), options, callback);

        public void AddLink(SpanLink link)
        {
            ArgumentNullException.ThrowIfNull(link);
            if (!ContextIdentities.TryGetValue(link.Context, out var identity)) return;
            var tags = ToActivityTags(link.Attributes);
            lock (_gate)
            {
                if (_settled) return;
                activity.AddLink(new ActivityLink(identity.Context, tags));
            }
        }

        public void AddEvent(string name, SpanAttributes? attributes = null)
        {
            ArgumentNullException.ThrowIfNull(name);
            var tags = ToActivityTags(attributes);
            lock (_gate)
            {
                if (_settled) return;
                activity.AddEvent(new ActivityEvent(name, tags: tags));
            }
        }

        public void SetAttributes(SpanAttributes attributes)
        {
            if (attributes is null) return;
            var snapshot = ToTags(attributes);
            if (snapshot is null) return;
            lock (_gate)
            {
                if (_settled) return;
                foreach (var (key, value) in snapshot) activity.SetTag(key, value);
            }
        }

        public void SetStatus(SpanStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            lock (_gate)
            {
                if (_settled) return;
                _explicitStatus = true;
                ApplyStatusLocked(status);
            }
        }

        public void ApplyFailureIfUnset(Exception error)
        {
            lock (_gate)
            {
                if (_settled || _explicitStatus) return;
                _explicitStatus = true;
                ApplyStatusLocked(SpanStatus.Failed(error.GetType().Name, error.Message));
            }
        }

        public void Settle()
        {
            lock (_gate)
            {
                if (_settled) return;
                if (!_explicitStatus) activity.SetStatus(ActivityStatusCode.Ok);
                _settled = true;
            }
        }

        private void ApplyStatusLocked(SpanStatus status)
        {
            if (status.IsError)
            {
                activity.SetStatus(ActivityStatusCode.Error, status.Error?.Message);
                activity.SetCustomProperty(ErrorPropertyKey, status.Error);
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
                activity.SetCustomProperty(ErrorPropertyKey, null);
            }
        }
    }

    private sealed class InertSpan(DiagnosticsTelemetry owner) : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => owner;
        public ITelemetryPropagation? Propagation => owner;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(callback);
            return owner.StartInertAsync(options, callback);
        }

        public void AddLink(SpanLink link) { }
        public void AddEvent(string name, SpanAttributes? attributes = null) { }
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }

    private sealed class ExtractedParent(DiagnosticsTelemetry owner, ActivityContext context) : ITelemetryContext
    {
        public ITelemetryMetrics? Metrics => owner;
        public ITelemetryPropagation? Propagation => owner;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
            owner.StartAsync(new ParentRef(context, Span: null), options, callback);
    }

    private sealed class CounterAdapter(Counter<double> counter) : ITelemetryCounter
    {
        public void Add(double value, SpanAttributes? attributes = null) =>
            counter.Add(value, ToMeasurementTags(attributes));
    }

    private sealed class UpDownCounterAdapter(UpDownCounter<double> counter) : ITelemetryUpDownCounter
    {
        public void Add(double value, SpanAttributes? attributes = null) =>
            counter.Add(value, ToMeasurementTags(attributes));
    }

    private sealed class HistogramAdapter(Histogram<double> histogram) : ITelemetryHistogram
    {
        public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null)
        {
            var tags = ToMeasurementTags(attributes);
            var saved = Activity.Current;
            try
            {
                if (context is not null && ContextIdentities.TryGetValue(context, out var identity) && identity.Activity is { } exemplar)
                    Activity.Current = exemplar;
                else
                    Activity.Current = null;
                histogram.Record(value, tags);
            }
            finally
            {
                Activity.Current = saved;
            }
        }
    }
}
