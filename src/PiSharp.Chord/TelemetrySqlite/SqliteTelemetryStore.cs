// Pi v1.5.0 (MIT): packages/chord/src/telemetry-sqlite/store.ts.
using System.Globalization;
using System.Text.Json;
using PiSharp.Chord.Sqlite;
using PiSharp.Telemetry;

namespace PiSharp.Chord.TelemetrySqlite;

/// <summary>Read committed telemetry without creating a recorder or timer. Queries use bound values, preserve scalar JSON
/// types, and require all attribute conditions to match the same span.</summary>
public static class SqliteTelemetryStore
{
    private static string Text(IReadOnlyDictionary<string, object?> row, string key) => (string)row[key]!;
    private static double Number(IReadOnlyDictionary<string, object?> row, string key) => Convert.ToDouble(row[key], CultureInfo.InvariantCulture);
    private static SpanAttributes Attributes(string json) => JsonSerializer.Deserialize<SpanAttributes>(json)!;
    private static StoredSpan Span(IReadOnlyDictionary<string, object?> row)
    {
        using var events = JsonDocument.Parse(Text(row, "events"));
        using var links = JsonDocument.Parse(Text(row, "links"));
        return new(Text(row, "id"), Text(row, "trace_id"), (string?)row["parent_id"], Text(row, "name"),
            Number(row, "started_ms"), Number(row, "ended_ms"), Attributes(Text(row, "attributes")),
            events.RootElement.EnumerateArray().Select(e => new StoredSpanEvent(e.GetProperty("name").GetString()!,
                e.GetProperty("time_ms").GetDouble(), Attributes(e.GetProperty("attributes").GetRawText()))).ToArray(),
            links.RootElement.EnumerateArray().Select(l => new StoredSpanLink(l.GetProperty("trace_id").GetString()!,
                l.GetProperty("span_id").GetString()!, l.TryGetProperty("attributes", out var a) ? Attributes(a.GetRawText()) : null)).ToArray(),
            JsonSerializer.Deserialize<SpanStatus>(Text(row, "status"))!);
    }

    private static string AttributesSql(SpanAttributes? attributes, string column, List<object?> parameters)
    {
        var terms = new List<string>();
        foreach (var (key, value) in attributes ?? new())
        {
            // TraceQuery admits scalar equality. Arrays and undefined cannot match a stored scalar.
            if (value is null || value.IsArray) { terms.Add("0"); continue; }
            var type = value.Type switch
            {
                TelemetryAttributeType.Number => "entry.type IN ('integer', 'real')",
                TelemetryAttributeType.Boolean => value.GetBoolean() ? "entry.type = 'true'" : "entry.type = 'false'",
                _ => "entry.type = 'text'"
            };
            parameters.Add(key);
            parameters.Add(value.Type switch
            {
                TelemetryAttributeType.Number => (object)value.GetNumber(),
                TelemetryAttributeType.Boolean => value.GetBoolean() ? 1 : 0,
                _ => value.GetString()
            });
            terms.Add($"EXISTS (SELECT 1 FROM json_each({column}) entry WHERE entry.key = ? AND {type} AND entry.value = ?)");
        }
        return terms.Count == 0 ? "1" : string.Join(" AND ", terms);
    }

    public static async Task<StoredTrace?> GetTraceAsync(ISqliteDatabase db, string traceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await db.AllAsync("SELECT * FROM telemetry_spans WHERE trace_id = ? ORDER BY started_ms, id", traceId).ConfigureAwait(false);
        return rows.Count == 0 ? null : new(traceId, rows.Select(Span).ToArray());
    }

    public static async Task<TracePage> FindTracesAsync(ISqliteDatabase db, TraceQuery? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new();
        var limit = query.Limit ?? 20;
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(query), "A trace page size is positive.");
        var terms = new List<string>();
        var parameters = new List<object?>();
        if (query.Name is { } name)
        {
            terms.Add("(SELECT name FROM telemetry_spans WHERE trace_id = heads.trace_id ORDER BY started_ms, id LIMIT 1) = ?"); parameters.Add(name);
        }
        if (query.StartMin is { } min) { terms.Add("started_ms >= ?"); parameters.Add(min); }
        if (query.StartMax is { } max) { terms.Add("started_ms <= ?"); parameters.Add(max); }
        if (query.Errors) terms.Add("EXISTS (SELECT 1 FROM telemetry_spans WHERE trace_id = heads.trace_id AND json_extract(status, '$.status') = 'error')");
        if (query.Attributes is { } attributes)
            terms.Add($"EXISTS (SELECT 1 FROM telemetry_spans candidate WHERE candidate.trace_id = heads.trace_id AND {AttributesSql(attributes, "candidate.attributes", parameters)})");
        if (query.Cursor is { } cursor)
        {
            using var json = JsonDocument.Parse(cursor);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 || root[0].ValueKind != JsonValueKind.Number || root[1].ValueKind != JsonValueKind.String)
                throw new ArgumentException("The trace cursor is not one this store issued.", nameof(query));
            terms.Add("(started_ms < ? OR (started_ms = ? AND trace_id > ?))");
            parameters.AddRange([root[0].GetDouble(), root[0].GetDouble(), root[1].GetString()]);
        }
        parameters.Add((long)limit + 1);
        return await db.TransactionAsync(async tx =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var heads = (await tx.AllAsync($"WITH heads AS (SELECT trace_id, min(started_ms) started_ms FROM telemetry_spans GROUP BY trace_id) SELECT * FROM heads WHERE {(terms.Count == 0 ? "1" : string.Join(" AND ", terms))} ORDER BY started_ms DESC, trace_id LIMIT ?", parameters.ToArray()).ConfigureAwait(false)).ToList();
            var more = heads.Count > limit;
            if (more) heads.RemoveAt(heads.Count - 1);
            if (heads.Count == 0) return new TracePage([]);
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await tx.AllAsync("SELECT * FROM telemetry_spans WHERE trace_id IN (SELECT value FROM json_each(?)) ORDER BY started_ms, id",
                JsonSerializer.Serialize(heads.Select(h => Text(h, "trace_id")))).ConfigureAwait(false);
            var grouped = rows.Select(Span).GroupBy(s => s.TraceId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            return new TracePage(heads.Select(h => new StoredTrace(Text(h, "trace_id"), grouped[Text(h, "trace_id")])).ToArray(),
                more ? JsonSerializer.Serialize(new object[] { Number(heads[^1], "started_ms"), Text(heads[^1], "trace_id") }) : null);
        }).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<StoredMetric>> FindMetricsAsync(ISqliteDatabase db, MetricQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = new List<object?> { query.Name };
        var terms = new List<string> { "name = ?" };
        if (query.StartMin is { } min) { terms.Add("interval_ms >= ?"); parameters.Add(min); }
        if (query.StartMax is { } max) { terms.Add("interval_ms <= ?"); parameters.Add(max); }
        terms.Add(AttributesSql(query.Attributes, "attributes", parameters));
        var rows = await db.AllAsync($"SELECT * FROM telemetry_metrics WHERE {string.Join(" AND ", terms)} ORDER BY interval_ms, series_key", parameters.ToArray()).ConfigureAwait(false);
        return rows.Select(row => new StoredMetric(Number(row, "interval_ms"), Text(row, "name"), Text(row, "kind") switch
        {
            "counter" => TelemetryMetricKind.Counter, "upDownCounter" => TelemetryMetricKind.UpDownCounter,
            "histogram" => TelemetryMetricKind.Histogram, _ => throw new InvalidOperationException("Unknown telemetry metric kind")
        }, (string?)row["unit"], Attributes(Text(row, "attributes")), Number(row, "count"), Number(row, "sum"), Number(row, "min"), Number(row, "max"))).ToArray();
    }
}
