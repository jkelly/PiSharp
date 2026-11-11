// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry-ingest/recorder.ts.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Telemetry;

namespace PiSharp.Chord.TelemetryIngest;

/// <summary>A bounded, storage-independent telemetry context. Take reserves capacity until Release.</summary>
public sealed class TelemetryRecorder : ITelemetryContext, ITelemetryPropagation, ITelemetryMetrics, ITelemetryIngestTarget
{
    public const int IntervalMs = 10_000;
    private sealed record Identity(string Id, string TraceId);
    // Shared across recorder instances: adapters on different hosts still recognize one another's contexts.
    private static readonly ConditionalWeakTable<ITelemetryContext, Identity> Identities = new();
    private readonly object gate = new();
    private readonly Func<string> scope;
    private readonly int capacity;
    private readonly TimeProvider time;
    private readonly ITelemetryMetrics metrics;
    private readonly ITelemetryPropagation propagation;
    private readonly Queue<StoredSpan> spans = new();
    private readonly Dictionary<string, StoredMetric> measurements = new(StringComparer.Ordinal);
    private string? prefix;
    private long sequence, droppedSpans, droppedMeasurements;
    private int inFlight, inFlightMeasurements;
    private bool stopped;

    public TelemetryRecorder(Func<string> scope, int capacity, TimeProvider? timeProvider = null)
        : this(scope, capacity, timeProvider, null, null) { }

    // Composing adapters retain their public capability identity through every derived context and span.
    internal TelemetryRecorder(Func<string> scope, int capacity, TimeProvider? timeProvider,
        ITelemetryMetrics? metrics, ITelemetryPropagation? propagation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        this.scope = scope; this.capacity = capacity; time = timeProvider ?? TimeProvider.System;
        this.metrics = metrics ?? this; this.propagation = propagation ?? this;
    }
    public ITelemetryMetrics Metrics => metrics;
    public ITelemetryPropagation Propagation => propagation;
    public bool Stopped { get { lock (gate) return stopped; } }
    public int QueuedSpans { get { lock (gate) return spans.Count; } }
    public long DroppedSpans { get { lock (gate) return droppedSpans; } }
    public long DroppedMeasurements { get { lock (gate) return droppedMeasurements; } }
    public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => Start(null, options, callback);
    public IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context) =>
        Identities.TryGetValue(context, out var identity) ? new Dictionary<string, string>
        { ["pi-sqlite-span"] = identity.Id, ["pi-sqlite-trace"] = identity.TraceId } : null;
    public ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier)
    {
        if (carrier is null || !carrier.TryGetValue("pi-sqlite-span", out var id) || id is null ||
            !carrier.TryGetValue("pi-sqlite-trace", out var trace) || trace is null) return this;
        var identity = new Identity(id, trace);
        var context = new Parent(this, identity);
        Identities.Add(context, identity);
        return context;
    }
    public TelemetryBatch Take(bool partial, int limit = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        lock (gate)
        {
            var taken = new List<StoredSpan>(Math.Min(limit, spans.Count));
            while (taken.Count < limit && spans.TryDequeue(out var span)) taken.Add(span);
            inFlight += taken.Count;
            var current = CurrentInterval;
            var metrics = measurements.Where(pair => partial || pair.Value.IntervalMs < current).ToArray();
            foreach (var pair in metrics) measurements.Remove(pair.Key);
            inFlightMeasurements += metrics.Length;
            return new(taken, metrics.Select(pair => pair.Value).ToArray());
        }
    }
    public void Release() { lock (gate) { inFlight = 0; inFlightMeasurements = 0; } }
    public bool Pending(bool partial)
    {
        lock (gate) return spans.Count > 0 || measurements.Values.Any(metric => partial || metric.IntervalMs < CurrentInterval);
    }
    public int Ingest(TelemetryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (gate)
        {
            if (stopped) return 0;
            var queued = 0;
            foreach (var span in batch.Spans)
                if (spans.Count + inFlight >= capacity) droppedSpans++;
                else { spans.Enqueue(span); queued++; }
            foreach (var metric in batch.Metrics) Merge(metric);
            return queued;
        }
    }
    public void Stop(string mode)
    {
        if (mode is not ("keep" or "discard")) throw new ArgumentException("Expected keep or discard", nameof(mode));
        Stop(mode == "discard");
    }
    public void Stop(bool discard = false)
    {
        lock (gate)
        {
            stopped = true;
            if (discard) { spans.Clear(); measurements.Clear(); }
        }
    }
    private double CurrentInterval => Math.Floor(time.GetUtcNow().ToUnixTimeMilliseconds() / (double)IntervalMs) * IntervalMs;
    private static SpanAttributes Copy(SpanAttributes? attributes) => new((attributes ?? new()).Where(pair => pair.Value is not null));
    public static SpanAttributes SortedAttributes(SpanAttributes attributes) => new(Copy(attributes).OrderBy(pair => pair.Key, StringComparer.Ordinal));
    public static string SeriesKey(StoredMetric metric) => ChordJson.Stringify(new JsonArray(
        JsonValue.Create(metric.Name), JsonValue.Create(KindName(metric.Kind)), JsonValue.Create(metric.Unit),
        JsonValue.Create(ChordJson.Stringify(JsonNode.Parse(SortedAttributes(metric.Attributes).ToString())))));
    private static string KindName(TelemetryMetricKind kind) => kind switch
    { TelemetryMetricKind.Counter => "counter", TelemetryMetricKind.UpDownCounter => "upDownCounter", _ => "histogram" };
    private static StoredSpanLink? CopyLink(SpanLink link) => Identities.TryGetValue(link.Context, out var identity)
        ? new(identity.TraceId, identity.Id, Copy(link.Attributes)) : null;

    private async Task<T> Start<T>(Identity? parent, SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(callback);
        RecordingSpan span;
        lock (gate)
        {
            Identity? identity = null;
            if (!stopped) { prefix ??= scope(); var id = $"{prefix}:{++sequence}"; identity = new(id, parent?.TraceId ?? id); }
            span = new(this, identity, options);
            if (identity is not null) Identities.Add(span, identity);
        }
        try { return await callback(span).ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (gate) if (!span.Explicit) span.Status = SpanStatus.Failed(error.GetType().Name, error.Message);
            throw;
        }
        finally
        {
            lock (gate)
            {
                span.Settled = true;
                if (!stopped && span.Identity is { } identity)
                {
                    if (spans.Count + inFlight >= capacity) droppedSpans++;
                    else spans.Enqueue(new(identity.Id, identity.TraceId, parent?.Id, options.Name, span.Started,
                        TelemetryClock.RoundMillisecondsToMicrosecond(span.Started + time.GetElapsedTime(span.Clock).TotalMilliseconds),
                        span.Attributes, span.Events.ToArray(), span.Links.ToArray(), span.Status));
                }
            }
        }
    }
    private sealed class Parent(TelemetryRecorder owner, Identity identity) : ITelemetryContext
    {
        public ITelemetryMetrics Metrics => owner.Metrics;
        public ITelemetryPropagation Propagation => owner.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => owner.Start(identity, options, callback);
    }
    private sealed class RecordingSpan : ITelemetrySpan
    {
        private readonly TelemetryRecorder owner;
        internal readonly Identity? Identity;
        internal readonly double Started;
        internal readonly long Clock;
        internal readonly SpanAttributes Attributes;
        internal readonly List<StoredSpanEvent> Events = [];
        internal readonly List<StoredSpanLink> Links = [];
        internal SpanStatus Status = SpanStatus.Ok;
        internal bool Explicit, Settled;
        internal RecordingSpan(TelemetryRecorder owner, Identity? identity, SpanOptions options)
        {
            this.owner = owner; Identity = identity;
            Started = identity is null ? 0 : TelemetryClock.Now(owner.time); Clock = owner.time.GetTimestamp();
            Attributes = identity is null ? new() : Copy(options.Attributes);
            if (identity is not null) foreach (var link in options.Links ?? []) if (CopyLink(link) is { } copied) Links.Add(copied);
        }
        public ITelemetryMetrics Metrics => owner.Metrics;
        public ITelemetryPropagation Propagation => owner.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        {
            lock (owner.gate)
                if (Settled || Identity is null)
                    return NoopTelemetry.Context.StartSpanAsync(options, _ => callback(new Inert(owner)));
            return owner.Start(Identity, options, callback);
        }
        private void Record(Action action) { lock (owner.gate) if (!Settled && !owner.stopped && Identity is not null) action(); }
        public void AddLink(SpanLink link) => Record(() => { if (CopyLink(link) is { } copy) Links.Add(copy); });
        public void AddEvent(string name, SpanAttributes? attributes = null) => Record(() => Events.Add(new(name,
            TelemetryClock.RoundMillisecondsToMicrosecond(owner.time.GetElapsedTime(Clock).TotalMilliseconds), Copy(attributes))));
        public void SetAttributes(SpanAttributes attributes) => Record(() => { foreach (var pair in Copy(attributes)) Attributes[pair.Key] = pair.Value; });
        public void SetStatus(SpanStatus status) => Record(() => { Status = status; Explicit = true; });
    }
    private sealed class Inert(TelemetryRecorder owner) : ITelemetrySpan
    {
        public ITelemetryMetrics Metrics => owner.Metrics;
        public ITelemetryPropagation Propagation => owner.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
            NoopTelemetry.Context.StartSpanAsync(options, _ => callback(this));
        public void AddLink(SpanLink link) { }
        public void AddEvent(string name, SpanAttributes? attributes = null) { }
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }
    public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) => new Instrument(this, TelemetryMetricKind.Counter, name, options?.Unit);
    public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null) => new Instrument(this, TelemetryMetricKind.UpDownCounter, name, options?.Unit);
    public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null) => new Instrument(this, TelemetryMetricKind.Histogram, name, options?.Unit);
    private sealed class Instrument(TelemetryRecorder owner, TelemetryMetricKind kind, string name, string? unit)
        : ITelemetryCounter, ITelemetryUpDownCounter, ITelemetryHistogram
    {
        public void Add(double value, SpanAttributes? attributes = null)
        {
            lock (owner.gate) if (!owner.stopped)
                owner.Merge(new(owner.CurrentInterval, name, kind, unit, SortedAttributes(attributes ?? new()), 1, value, value, value));
        }
        public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null) => Add(value, attributes);
    }
    private void Merge(StoredMetric metric)
    {
        var key = metric.IntervalMs.ToString(CultureInfo.InvariantCulture) + ":" + SeriesKey(metric);
        measurements.TryGetValue(key, out var previous);
        if (previous is null && measurements.Count + inFlightMeasurements >= capacity) { droppedMeasurements++; return; }
        measurements[key] = metric with { Attributes = SortedAttributes(metric.Attributes),
            Count = (previous?.Count ?? 0) + metric.Count, Sum = (previous?.Sum ?? 0) + metric.Sum,
            Min = Math.Min(previous?.Min ?? metric.Min, metric.Min), Max = Math.Max(previous?.Max ?? metric.Max, metric.Max) };
    }
}
