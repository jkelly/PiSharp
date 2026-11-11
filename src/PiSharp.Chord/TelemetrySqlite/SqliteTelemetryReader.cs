// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry-sqlite/store.ts, packages/chord/src/telemetry-sqlite/node.ts.
using PiSharp.Chord.Sqlite;
using PiSharp.Telemetry;

namespace PiSharp.Chord.TelemetrySqlite;

/// <summary>A view of committed telemetry on an owned read-only connection. Opening never migrates or flushes data.</summary>
public sealed class SqliteTelemetryReader : ITelemetryStore, IAsyncDisposable
{
    private readonly ISqliteDatabase database;
    private readonly object gate = new();
    private Task? closing;

    internal SqliteTelemetryReader(ISqliteDatabase database) => this.database = database;

    public Task<StoredTrace?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default) =>
        SqliteTelemetryStore.GetTraceAsync(database, traceId, cancellationToken);
    public Task<TracePage> FindTracesAsync(TraceQuery? query = null, CancellationToken cancellationToken = default) =>
        SqliteTelemetryStore.FindTracesAsync(database, query, cancellationToken);
    public Task<IReadOnlyList<StoredMetric>> FindMetricsAsync(MetricQuery query, CancellationToken cancellationToken = default) =>
        SqliteTelemetryStore.FindMetricsAsync(database, query, cancellationToken);

    public Task CloseAsync() { lock (gate) return closing ??= database.CloseAsync(); }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
