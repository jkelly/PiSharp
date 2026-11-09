// Pi v1.5.0 (MIT): packages/chord/src/telemetry-sqlite/index.ts.
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Sqlite;
using PiSharp.Telemetry;

namespace PiSharp.Chord.TelemetrySqlite;

public sealed record SqliteTelemetryOptions
{
    public double RetentionMs { get; init; } = 7 * 24 * 60 * 60 * 1000;
    /// <summary>Independent limits for settled spans and pending metric interval/series pairs, including writes in flight.</summary>
    public int MaxQueuedSpans { get; init; } = 10_000;
}

/// <summary>Opt-in, buffered SQLite telemetry context and committed-data reader. Pass this context to a Chord host's
/// telemetry option. Recording executes no SQL; a one-second timer flushes settled spans and completed ten-second metric
/// intervals. Close drains partial intervals, leaves the database open, and does not wait for business callbacks.
/// A write failure permanently stops recording and is returned by every subsequent flush/close.</summary>
public sealed class SqliteTelemetry : ITelemetryContext, ITelemetryStore, ITelemetryPropagation, ITelemetryMetrics, IAsyncDisposable
{
    private sealed record Identity(string Id, string TraceId);
    private sealed record Metric(double Interval, string Key, string Name, string Kind, string? Unit, string Attributes,
        double Count, double Sum, double Min, double Max);
    private static readonly ConditionalWeakTable<ITelemetryContext, Identity> Identities = new();
    private readonly ISqliteDatabase _db;
    private readonly SqliteTelemetryOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _scope = Guid.NewGuid().ToString();
    private readonly List<object?[]> _spans = [];
    private readonly Dictionary<string, Metric> _measurements = new(StringComparer.Ordinal);
    private readonly ITimer _timer;
    private long _sequence, _droppedSpans, _droppedMeasurements;
    private int _inFlightMeasurements;
    private bool _stopped, _writing;
    private ExceptionDispatchInfo? _failure;
    private Task? _closing;

    private SqliteTelemetry(ISqliteDatabase db, SqliteTelemetryOptions options, TimeProvider time)
    {
        _db = db; _options = options; _time = time;
        _timer = time.CreateTimer(_ => BackgroundFlush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Applies the dedicated telemetry_schema migration and starts recording. The database remains caller-owned.
    /// The optional clock controls both wall/elapsed time and the flush timer.</summary>
    public static async Task<SqliteTelemetry> CreateAsync(ISqliteDatabase database, SqliteTelemetryOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        options ??= new();
        await SqliteMigrations.ApplyAsync(database, [new(1, [
            "CREATE TABLE telemetry_spans (id TEXT PRIMARY KEY, trace_id TEXT NOT NULL, parent_id TEXT, name TEXT, started_ms REAL, ended_ms REAL NOT NULL, attributes TEXT, events TEXT, links TEXT, status TEXT)",
            "CREATE INDEX telemetry_spans_ended_ms ON telemetry_spans(ended_ms)",
            "CREATE INDEX telemetry_spans_trace_started ON telemetry_spans(trace_id, started_ms)",
            "CREATE INDEX telemetry_spans_started_ms ON telemetry_spans(started_ms)",
            "CREATE TABLE telemetry_metrics (interval_ms INTEGER, series_key TEXT, name TEXT, kind TEXT, unit TEXT, attributes TEXT, count INTEGER, sum REAL, min REAL, max REAL, PRIMARY KEY(interval_ms, series_key))"
        ])], "telemetry_schema").ConfigureAwait(false);
        return new(database, options, timeProvider ?? TimeProvider.System);
    }

    public ITelemetryMetrics Metrics => this;
    public ITelemetryPropagation Propagation => this;
    public long DroppedSpans { get { lock (_gate) return _droppedSpans; } }
    public long DroppedMeasurements { get { lock (_gate) return _droppedMeasurements; } }
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

    private double Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private static SpanAttributes Copy(SpanAttributes? attributes) => new((attributes ?? new())
        .Where(pair => pair.Value is not null)); // AttributeValue arrays are already immutable copies.
    private static StoredSpanLink? CopyLink(SpanLink link) => Identities.TryGetValue(link.Context, out var identity)
        ? new(identity.TraceId, identity.Id, Copy(link.Attributes)) : null;

    private async Task<T> Start<T>(Identity? parent, SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(callback);
        RecordingSpan span;
        lock (_gate)
        {
            if (_stopped) span = new(this, null, options);
            else
            {
                var id = $"{_scope}:{++_sequence}";
                span = new(this, new(id, parent?.TraceId ?? id), options);
                Identities.Add(span, span.Identity!);
            }
        }
        try { return await callback(span).ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate) if (!span.Explicit) span.Status = SpanStatus.Failed(error.GetType().Name, error.Message);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                span.Settled = true;
                if (!_stopped && span.Identity is { } identity)
                {
                    if (_spans.Count >= _options.MaxQueuedSpans) _droppedSpans++;
                    else _spans.Add([identity.Id, identity.TraceId, parent?.Id, options.Name, span.Started,
                        span.Started + _time.GetElapsedTime(span.Clock).TotalMilliseconds, span.Attributes.ToString(),
                        JsonSerializer.Serialize(span.Events.Select(e => new { name = e.Name, time_ms = e.TimeMs, attributes = e.Attributes })),
                        JsonSerializer.Serialize(span.Links.Select(l => new { trace_id = l.TraceId, span_id = l.SpanId, attributes = l.Attributes })),
                        JsonSerializer.Serialize(span.Status)]);
                }
            }
        }
    }

    private sealed class Parent(SqliteTelemetry owner, Identity identity) : ITelemetryContext
    {
        public ITelemetryMetrics Metrics => owner;
        public ITelemetryPropagation Propagation => owner;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => owner.Start(identity, options, callback);
    }

    private sealed class RecordingSpan : ITelemetrySpan
    {
        private readonly SqliteTelemetry _owner;
        internal readonly Identity? Identity;
        internal readonly double Started;
        internal readonly long Clock;
        internal readonly SpanAttributes Attributes;
        internal readonly List<StoredSpanEvent> Events = [];
        internal readonly List<StoredSpanLink> Links = [];
        internal SpanStatus Status = SpanStatus.Ok;
        internal bool Explicit, Settled;
        internal RecordingSpan(SqliteTelemetry owner, Identity? identity, SpanOptions options)
        {
            _owner = owner; Identity = identity; Started = owner.Now; Clock = owner._time.GetTimestamp();
            Attributes = Copy(options.Attributes);
            foreach (var link in options.Links ?? []) if (CopyLink(link) is { } copy) Links.Add(copy);
        }
        public ITelemetryMetrics Metrics => _owner;
        public ITelemetryPropagation Propagation => _owner;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        {
            lock (_owner._gate)
                if (Settled || Identity is null) return NoopTelemetry.Context.StartSpanAsync(options, _ => callback(new RecordingSpan(_owner, null, options)));
            return _owner.Start(Identity, options, callback);
        }
        private void Record(Action action) { lock (_owner._gate) if (!Settled && !_owner._stopped && Identity is not null) action(); }
        public void AddLink(SpanLink link) => Record(() => { if (CopyLink(link) is { } copy) Links.Add(copy); });
        public void AddEvent(string name, SpanAttributes? attributes = null) => Record(() =>
            Events.Add(new(name, _owner._time.GetElapsedTime(Clock).TotalMilliseconds, Copy(attributes))));
        public void SetAttributes(SpanAttributes attributes) => Record(() => { foreach (var (key, value) in Copy(attributes)) Attributes[key] = value; });
        public void SetStatus(SpanStatus status) => Record(() => { Status = status; Explicit = true; });
    }

    public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) => new Instrument(this, "counter", name, options?.Unit);
    public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null) => new Instrument(this, "upDownCounter", name, options?.Unit);
    public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null) => new Instrument(this, "histogram", name, options?.Unit);
    private sealed class Instrument(SqliteTelemetry owner, string kind, string name, string? unit)
        : ITelemetryCounter, ITelemetryUpDownCounter, ITelemetryHistogram
    {
        public void Add(double value, SpanAttributes? attributes = null) => owner.Measure(kind, name, unit, value, attributes);
        public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null) => Add(value, attributes);
    }
    private void Measure(string kind, string name, string? unit, double value, SpanAttributes? attributes)
    {
        lock (_gate)
        {
            if (_stopped) return;
            var encoded = ChordJson.Stringify(JsonNode.Parse(new SpanAttributes(Copy(attributes)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)).ToString()));
            // This key must agree with JavaScript JSON.stringify when another implementation shares the file.
            var key = ChordJson.Stringify(new JsonArray(JsonValue.Create(name), JsonValue.Create(kind), JsonValue.Create(unit), JsonValue.Create(encoded)));
            var interval = Math.Floor(Now / 10_000) * 10_000;
            var pending = interval.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + key;
            _measurements.TryGetValue(pending, out var previous);
            if (previous is null && _measurements.Count + _inFlightMeasurements >= _options.MaxQueuedSpans) { _droppedMeasurements++; return; }
            _measurements[pending] = new(interval, key, name, kind, unit, encoded, (previous?.Count ?? 0) + 1,
                (previous?.Sum ?? 0) + value, Math.Min(previous?.Min ?? value, value), Math.Max(previous?.Max ?? value, value));
        }
    }

    public Task FlushAsync() { lock (_gate) return _closing ?? Drain(false); }
    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closing is not null) return _closing;
            _stopped = true; _timer.Dispose();
            return _closing = Drain(true);
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
    private async void BackgroundFlush()
    {
        lock (_gate) if (_writing || _stopped) return;
        try { await FlushAsync().ConfigureAwait(false); } catch { /* Retained and surfaced by flush/close. */ }
    }
    private async Task Drain(bool partial)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            object?[][] spans;
            Metric[] metrics;
            lock (_gate)
            {
                _failure?.Throw();
                _writing = true;
                spans = _spans.ToArray();
                var completed = Math.Floor(Now / 10_000) * 10_000;
                var selected = _measurements.Where(pair => partial || pair.Value.Interval < completed).ToArray();
                metrics = selected.Select(pair => pair.Value).ToArray();
                foreach (var pair in selected) _measurements.Remove(pair.Key);
                _inFlightMeasurements = metrics.Length;
            }
            try
            {
                await _db.TransactionAsync(async tx =>
                {
                    foreach (var span in spans) await tx.RunAsync("INSERT INTO telemetry_spans VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", span).ConfigureAwait(false);
                    foreach (var metric in metrics) await tx.RunAsync(
                        "INSERT INTO telemetry_metrics VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT(interval_ms, series_key) DO UPDATE SET count = count + excluded.count, sum = sum + excluded.sum, min = min(min, excluded.min), max = max(max, excluded.max)",
                        metric.Interval, metric.Key, metric.Name, metric.Kind, metric.Unit, metric.Attributes, metric.Count, metric.Sum, metric.Min, metric.Max).ConfigureAwait(false);
                    var cutoff = Now - _options.RetentionMs;
                    await tx.RunAsync("DELETE FROM telemetry_spans WHERE ended_ms < ?", cutoff).ConfigureAwait(false);
                    await tx.RunAsync("DELETE FROM telemetry_metrics WHERE interval_ms < ?", cutoff).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                lock (_gate) _spans.RemoveRange(0, spans.Length);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _failure = ExceptionDispatchInfo.Capture(error); _stopped = true;
                    _spans.Clear(); _measurements.Clear(); _timer.Dispose();
                }
                throw;
            }
        }
        finally { lock (_gate) { _writing = false; _inFlightMeasurements = 0; } _writer.Release(); }
    }

    public Task<StoredTrace?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default) => SqliteTelemetryStore.GetTraceAsync(_db, traceId, cancellationToken);
    public Task<TracePage> FindTracesAsync(TraceQuery? query = null, CancellationToken cancellationToken = default) => SqliteTelemetryStore.FindTracesAsync(_db, query, cancellationToken);
    public Task<IReadOnlyList<StoredMetric>> FindMetricsAsync(MetricQuery query, CancellationToken cancellationToken = default) => SqliteTelemetryStore.FindMetricsAsync(_db, query, cancellationToken);
}
