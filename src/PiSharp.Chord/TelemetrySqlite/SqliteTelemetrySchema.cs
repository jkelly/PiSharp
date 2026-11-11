// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry-sqlite/schema.ts.
using PiSharp.Chord.Sqlite;

namespace PiSharp.Chord.TelemetrySqlite;

internal static class SqliteTelemetrySchema
{
    internal const long Version = 2;

    internal static Task ApplyAsync(ISqliteDatabase database) => SqliteMigrations.ApplyAsync(database,
    [
        // Keep the original migration: existing version-one files are upgraded without losing rows.
        new(1, [
            "CREATE TABLE telemetry_spans (id TEXT PRIMARY KEY, trace_id TEXT NOT NULL, parent_id TEXT, name TEXT, started_ms REAL, ended_ms REAL NOT NULL, attributes TEXT, events TEXT, links TEXT, status TEXT)",
            "CREATE INDEX telemetry_spans_ended_ms ON telemetry_spans(ended_ms)",
            "CREATE INDEX telemetry_spans_trace_started ON telemetry_spans(trace_id, started_ms)",
            "CREATE INDEX telemetry_spans_started_ms ON telemetry_spans(started_ms)",
            "CREATE TABLE telemetry_metrics (interval_ms INTEGER, series_key TEXT, name TEXT, kind TEXT, unit TEXT, attributes TEXT, count INTEGER, sum REAL, min REAL, max REAL, PRIMARY KEY(interval_ms, series_key))"
        ]),
        new(2, [
            "CREATE TABLE telemetry_spans_v2 (id TEXT PRIMARY KEY, trace_id TEXT NOT NULL, parent_id TEXT, name TEXT, started_us INTEGER, ended_us INTEGER NOT NULL, attributes TEXT, events TEXT, links TEXT, status TEXT)",
            "INSERT INTO telemetry_spans_v2 SELECT id, trace_id, parent_id, name, CAST(round(started_ms * 1000) AS INTEGER), CAST(round(ended_ms * 1000) AS INTEGER), attributes, events, links, status FROM telemetry_spans",
            "DROP TABLE telemetry_spans",
            "ALTER TABLE telemetry_spans_v2 RENAME TO telemetry_spans",
            "CREATE INDEX telemetry_spans_ended_us ON telemetry_spans(ended_us)",
            "CREATE INDEX telemetry_spans_trace_started ON telemetry_spans(trace_id, started_us)",
            "CREATE INDEX telemetry_spans_started_us ON telemetry_spans(started_us)"
        ])
    ], "telemetry_schema");

    internal static async Task CheckAsync(ISqliteDatabase database)
    {
        if (await database.GetAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name = ?", "telemetry_schema").ConfigureAwait(false) is null)
            throw new InvalidOperationException("SQLite telemetry schema is missing");
        var row = await database.GetAsync("SELECT version FROM telemetry_schema WHERE singleton = 1").ConfigureAwait(false);
        if (row is null) throw new InvalidOperationException("SQLite telemetry schema metadata is missing");
        var version = SqliteValues.Integer(row["version"]);
        if (version != Version)
            throw new InvalidOperationException($"SQLite telemetry schema version {version} does not match supported version {Version}");
    }

    internal static long Microseconds(double milliseconds) => checked((long)Math.Floor(milliseconds * 1000 + 0.5));
}
