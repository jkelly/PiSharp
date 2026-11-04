// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/store/index.ts
//
// Upstream's entry point (@earendil-works/pi-durable/storage/store) exports StoreStorage; this assembly is that entry point. Upstream's app
// opens its store as openStore(sqliteBackend(await openNodeSqliteDatabase(path)), context); StoreBackends.OpenSqliteAsync is that backend
// here, over PiSharp.Durable.Sqlite's connection database (Database.cs). PiSharp-only beyond the export: the backend helper.
using PiSharp.Chord.Store;
using PiSharp.Durable.Sqlite;

namespace PiSharp.Durable.Store;

/// <summary>Backends for Chord v2's store over Durable's SQLite layer.</summary>
public static class StoreBackends
{
    /// <summary>A store backend over the SQLite file at <paramref name="path"/> (<c>":memory:"</c> opens a private in-memory database),
    /// opened as <see cref="SqliteConnectionDatabase.OpenAsync"/> opens it (upstream
    /// <c>sqliteBackend(await openNodeSqliteDatabase(path))</c>); give it to <see cref="Tables.OpenStoreAsync"/>, which owns it.</summary>
    public static async Task<Backend> OpenSqliteAsync(string path, SqliteStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Tables.SqliteBackend(new StoreSqliteDatabase(await SqliteConnectionDatabase.OpenAsync(path, options).ConfigureAwait(false)));
    }
}
