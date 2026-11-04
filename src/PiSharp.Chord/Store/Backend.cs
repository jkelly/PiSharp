// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/backend.ts
//
// Store backends (slice 6a §2): what a store runs on. One kind today, any ISqliteDatabase (Chord v2's database surface, Sqlite/Database.cs):
// upstream's are node:sqlite through chord/sqlite/node and Durable Objects through chord/sqlite/cloudflare (Cloudflare is excluded); in
// PiSharp an adapter over PiSharp.Durable.Sqlite comes with PiSharp.Durable.Store (area store-storage), and the tests run one over
// Microsoft.Data.Sqlite. Its interface is internal to the store.
using PiSharp.Chord.Sqlite;

namespace PiSharp.Chord.Store;

/// <summary>A backend for <see cref="Tables.OpenStoreAsync"/>: opaque to apps, owned by the store once given to it (upstream
/// <c>Backend</c>).</summary>
public sealed class Backend
{
    /// <summary>A backend over <paramref name="db"/>.</summary>
    public Backend(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        Db = db;
    }

    /// <summary>The database the store runs its SQL on.</summary>
    public ISqliteDatabase Db { get; }
}

public static partial class Tables
{
    /// <summary>A backend over a SQLite database (upstream <c>sqliteBackend</c>).</summary>
    public static Backend SqliteBackend(ISqliteDatabase db) => new(db);
}
