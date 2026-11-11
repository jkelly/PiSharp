// Pi v1.5.2 (MIT): packages/chord/src/telemetry-sqlite/index.ts.
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.TelemetryIngest;
using PiSharp.Telemetry;

namespace PiSharp.Chord.TelemetrySqlite;

public sealed record SqliteTelemetryOptions
{
    public double RetentionMs { get; init; } = 7 * 24 * 60 * 60 * 1000;
    /// <summary>Independent limits for settled spans and pending metric interval/series pairs, including writes in flight.</summary>
    public int MaxQueuedSpans { get; init; } = 10_000;
}

/// <summary>Buffered SQLite telemetry context and ingest target. Recording and ingesting execute no SQL; a one-second
/// timer flushes settled spans and completed ten-second metric intervals. Close drains partial intervals, leaves the
/// caller-owned database open, and does not wait for business callbacks. A write failure permanently stops recording
/// and is returned by every subsequent flush or close.</summary>
public sealed class SqliteTelemetry : ITelemetryContext, ITelemetryStore, ITelemetryPropagation, ITelemetryMetrics,
    ITelemetryIngestTarget, IAsyncDisposable
{
    private readonly ISqliteDatabase _db;
    private readonly SqliteTelemetryOptions _options;
    private readonly TimeProvider _time;
    private readonly TelemetryRecorder _recorder;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ITimer _timer;
    private bool _writing;
    private ExceptionDispatchInfo? _failure;
    private Task? _closing;

    private SqliteTelemetry(ISqliteDatabase db, SqliteTelemetryOptions options, TimeProvider time)
    {
        _db = db; _options = options; _time = time;
        _recorder = new(() => Guid.NewGuid().ToString(), options.MaxQueuedSpans, time, this, this);
        _timer = time.CreateTimer(_ => BackgroundFlush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Applies the dedicated telemetry_schema migrations and starts recording. The database remains caller-owned.</summary>
    public static async Task<SqliteTelemetry> CreateAsync(ISqliteDatabase database, SqliteTelemetryOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxQueuedSpans);
        await SqliteTelemetrySchema.ApplyAsync(database).ConfigureAwait(false);
        return new(database, options, timeProvider ?? TimeProvider.System);
    }

    /// <summary>Opens a committed-data reader over an already-open read-only database. Ownership transfers to the reader.</summary>
    public static Task<SqliteTelemetryReader> OpenReaderAsync(ISqliteDatabase ownedReadOnlyDatabase) =>
        SqliteTelemetryStore.OpenReaderAsync(ownedReadOnlyDatabase);

    public ITelemetryMetrics Metrics => this;
    public ITelemetryPropagation Propagation => this;
    public long DroppedSpans => _recorder.DroppedSpans;
    public long DroppedMeasurements => _recorder.DroppedMeasurements;
    public int Ingest(TelemetryBatch batch) => _recorder.Ingest(batch);
    public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => _recorder.StartSpanAsync(options, callback);
    public IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context) => _recorder.Inject(context);
    public ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier) => _recorder.Extract(carrier);
    public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) => _recorder.Counter(name, options);
    public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null) => _recorder.UpDownCounter(name, options);
    public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null) => _recorder.Histogram(name, options);

    public Task FlushAsync() { lock (_gate) return _closing ?? Drain(false); }
    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closing is not null) return _closing;
            _recorder.Stop("keep");
            _timer.Dispose();
            return _closing = Drain(true);
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
    private async void BackgroundFlush()
    {
        lock (_gate) if (_writing || _recorder.Stopped) return;
        try { await FlushAsync().ConfigureAwait(false); } catch { /* Retained and surfaced by flush/close. */ }
    }

    private async Task Drain(bool partial)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        var taken = false;
        try
        {
            lock (_gate) { _failure?.Throw(); _writing = true; }
            var batch = _recorder.Take(partial);
            taken = true;
            try
            {
                await _db.TransactionAsync(async tx =>
                {
                    foreach (var span in batch.Spans)
                        await tx.RunAsync("INSERT OR IGNORE INTO telemetry_spans VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                            span.Id, span.TraceId, span.ParentId, span.Name,
                            SqliteTelemetrySchema.Microseconds(span.StartedMs), SqliteTelemetrySchema.Microseconds(span.EndedMs),
                            span.Attributes.ToString(),
                            JsonSerializer.Serialize(span.Events.Select(e => new { name = e.Name, time_ms = e.TimeMs, attributes = e.Attributes })),
                            JsonSerializer.Serialize(span.Links.Select(l => new { trace_id = l.TraceId, span_id = l.SpanId, attributes = l.Attributes })),
                            JsonSerializer.Serialize(span.Status)).ConfigureAwait(false);
                    foreach (var metric in batch.Metrics)
                        await tx.RunAsync(
                            "INSERT INTO telemetry_metrics VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT(interval_ms, series_key) DO UPDATE SET count = count + excluded.count, sum = sum + excluded.sum, min = min(min, excluded.min), max = max(max, excluded.max)",
                            metric.IntervalMs, TelemetryRecorder.SeriesKey(metric), metric.Name, MetricKind(metric.Kind), metric.Unit,
                            TelemetryRecorder.SortedAttributes(metric.Attributes).ToString(), metric.Count, metric.Sum, metric.Min, metric.Max).ConfigureAwait(false);
                    var cutoff = TelemetryClock.Now(_time) - _options.RetentionMs;
                    await tx.RunAsync("DELETE FROM telemetry_spans WHERE ended_us < ?", cutoff * 1000).ConfigureAwait(false);
                    await tx.RunAsync("DELETE FROM telemetry_metrics WHERE interval_ms < ?", cutoff).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _failure = ExceptionDispatchInfo.Capture(error);
                    _recorder.Stop("discard");
                    _timer.Dispose();
                }
                throw;
            }
        }
        finally
        {
            if (taken) _recorder.Release();
            lock (_gate) _writing = false;
            _writer.Release();
        }
    }

    private static string MetricKind(TelemetryMetricKind kind) => kind switch
    {
        TelemetryMetricKind.Counter => "counter", TelemetryMetricKind.UpDownCounter => "upDownCounter",
        TelemetryMetricKind.Histogram => "histogram", _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public Task<StoredTrace?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default) => SqliteTelemetryStore.GetTraceAsync(_db, traceId, cancellationToken);
    public Task<TracePage> FindTracesAsync(TraceQuery? query = null, CancellationToken cancellationToken = default) => SqliteTelemetryStore.FindTracesAsync(_db, query, cancellationToken);
    public Task<IReadOnlyList<StoredMetric>> FindMetricsAsync(MetricQuery query, CancellationToken cancellationToken = default) => SqliteTelemetryStore.FindMetricsAsync(_db, query, cancellationToken);
}
