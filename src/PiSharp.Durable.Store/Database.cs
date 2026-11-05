// PiSharp-only (decision 0013, phase 4): Chord v2's store runs on Chord's database surface (PiSharp.Chord.Sqlite, upstream chord/sqlite's
// SqliteExecutor and SqliteDatabase), which PiSharp.Chord declares itself because it references no Durable project; PiSharp.Durable.Sqlite
// declares the same interfaces for Durable's SQLite storage. Upstream has one copy, so a node:sqlite database serves both; here this
// adapter wraps a PiSharp.Durable.Sqlite database in Chord's surface member for member. A Durable.Sqlite SqliteRow is an
// IReadOnlyDictionary<string, object?>, so rows pass as they are; errors pass unwrapped (a SqliteException keeps its extended code, which
// the store's refusals read), and a transaction's callback gets its transaction handle wrapped the same way.
using DurableSqlite = PiSharp.Durable.Sqlite;
using StoreSqlite = PiSharp.Chord.Sqlite;

namespace PiSharp.Durable.Store;

/// <summary>A PiSharp.Durable.Sqlite database (a <see cref="DurableSqlite.SqliteConnectionDatabase"/>, for one) as the database of Chord
/// v2's store: what <see cref="PiSharp.Chord.Store.Tables.SqliteBackend"/> takes.</summary>
public sealed class StoreSqliteDatabase : StoreSqlite.ISqliteDatabase
{
    private readonly DurableSqlite.ISqliteDatabase db;

    /// <summary>Wraps <paramref name="db"/>; the store owns it from then on and closes it with the store.</summary>
    public StoreSqliteDatabase(DurableSqlite.ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
    }

    /// <inheritdoc/>
    public Task ExecAsync(string sql) => db.ExecAsync(sql);

    /// <inheritdoc/>
    public Task RunAsync(string sql, params object?[] parameters) => db.RunAsync(sql, parameters);

    /// <inheritdoc/>
    public Task<long> RunCountedAsync(string sql, params object?[] parameters) => db.RunCountedAsync(sql, parameters);

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) =>
        await db.GetAsync(sql, parameters).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) =>
        await db.AllAsync(sql, parameters).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<T> TransactionAsync<T>(Func<StoreSqlite.ISqliteExecutor, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return db.TransactionAsync(executor => callback(new Executor(executor)));
    }

    /// <inheritdoc/>
    public Task CloseAsync() => db.CloseAsync();

    /// <summary>A transaction handle in Chord's surface.</summary>
    private sealed class Executor(DurableSqlite.ISqliteExecutor executor) : StoreSqlite.ISqliteExecutor
    {
        public Task ExecAsync(string sql) => executor.ExecAsync(sql);

        public Task RunAsync(string sql, params object?[] parameters) => executor.RunAsync(sql, parameters);

        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => executor.RunCountedAsync(sql, parameters);

        public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) =>
            await executor.GetAsync(sql, parameters).ConfigureAwait(false);

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) =>
            await executor.AllAsync(sql, parameters).ConfigureAwait(false);
    }
}
