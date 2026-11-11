// PiSharp-only: File-opening adapter connecting the native SQLite database to the Chord telemetry reader.
using PiSharp.Chord.TelemetrySqlite;
using PiSharp.Durable.Sqlite;

namespace PiSharp.Durable.Store;

/// <summary>File-opening convenience for the Chord telemetry reader using the native SQLite adapter.</summary>
public static class NativeSqliteTelemetry
{
    /// <summary>Opens an existing telemetry file read-only and requires the current schema. No migration is applied.
    /// The returned reader owns its connection; closing it leaves independently opened recorders untouched.</summary>
    public static async Task<SqliteTelemetryReader> OpenReaderAsync(string path, SqliteStorageOptions? options = null)
    {
        var database = await SqliteConnectionDatabase.OpenAsync(path,
            (options ?? new()) with { OpenMode = SqliteDatabaseOpenMode.ReadOnly }).ConfigureAwait(false);
        return await SqliteTelemetryStore.OpenReaderAsync(new StoreSqliteDatabase(database)).ConfigureAwait(false);
    }
}
