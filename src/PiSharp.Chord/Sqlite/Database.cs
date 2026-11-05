// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/sqlite/database.ts
//
// The portable SQLite facade Chord v2's store runs on (upstream SqliteExecutor and SqliteDatabase, chord/sqlite's interfaces). PiSharp
// ports chord/sqlite in PiSharp.Durable.Sqlite (area sqlite: Database.cs, with the migration runner and the prefix check), and
// PiSharp.Chord references no PiSharp.Durable project (decision 0013), so Chord v2 declares the same interfaces under the same names here.
// The two copies have the same members, and a Durable.Sqlite SqliteRow is an IReadOnlyDictionary<string, object?>, so an adapter wraps one
// in the other member for member. Area cv2-store-tables added this file because the table writes run in the commit step's executor and
// the table reads through the store's guarded reads; area cv2-store-engine runs the store over ISqliteDatabase. Upstream's SqliteValue is
// null | number | bigint | string | Uint8Array; here a binding is null, long (or int), double, string or byte[], and a result column is
// null, long, double, string or byte[]. Upstream returns rows as plain objects; here a row is a read-only map from column name to value in
// the statement's column order.
namespace PiSharp.Chord.Sqlite;

/// <summary>
/// Asynchronous SQL operations shared by a database and its transaction handles (upstream <c>SqliteExecutor</c>).
/// <para><see cref="ExecAsync"/> runs SQL text without bindings and may contain several statements. <see cref="RunAsync"/>,
/// <see cref="GetAsync"/> and <see cref="AllAsync"/> execute one statement with positional <c>?</c> bindings. <see cref="RunCountedAsync"/>
/// runs one like <see cref="RunAsync"/> and returns the rows it inserted, updated or deleted, as SQLite's <c>changes()</c> counts them (index
/// writes and rows changed by triggers do not count); it may cost a statement more than <see cref="RunAsync"/>, so callers that need no
/// count use <see cref="RunAsync"/>. Adapters may cache prepared statements by SQL text, so callers pass values as bindings instead of
/// interpolating them.</para>
/// </summary>
public interface ISqliteExecutor
{
    /// <summary>Runs SQL text, which may hold several statements, without bindings.</summary>
    Task ExecAsync(string sql);

    /// <summary>Runs one statement with positional bindings: null, <see cref="long"/>, <see cref="int"/>, <see cref="double"/>,
    /// <see cref="string"/> or <see cref="byte"/>[].</summary>
    Task RunAsync(string sql, params object?[] parameters);

    /// <summary>Runs one statement with positional bindings, as <see cref="RunAsync"/> does, and returns the rows it inserted, updated or
    /// deleted (upstream <c>runCounted</c>).</summary>
    Task<long> RunCountedAsync(string sql, params object?[] parameters);

    /// <summary>Runs one statement and returns its first row, or null when it returns none.</summary>
    Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters);

    /// <summary>Runs one statement and returns every row.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters);
}

/// <summary>
/// Minimal portable SQLite database facade (upstream <c>SqliteDatabase</c>). All operations are asynchronous so adapters may execute
/// outside the caller's runtime.
/// <para><see cref="TransactionAsync{T}"/> passes the callback a transaction handle. All work in the transaction must use that handle; the
/// handle is invalid after the callback settles. Adapters must queue unrelated operations and other transactions until the transaction
/// finishes. The returned task settles after commit or rollback. Calling the database itself (including <see cref="TransactionAsync{T}"/>
/// or <see cref="CloseAsync"/>) from inside a callback therefore waits for that transaction and, when awaited there, never settles.</para>
/// <para>When the callback fails, the adapter must roll the transaction back before failing with that same exception. If rollback fails,
/// it must fail with a different exception (for example an <see cref="AggregateException"/>) so callers cannot mistake the callback's
/// exception for a guaranteed rollback.</para>
/// </summary>
public interface ISqliteDatabase : ISqliteExecutor
{
    /// <summary>Runs <paramref name="callback"/> in one transaction and returns its result after commit.</summary>
    Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback);

    /// <summary>Closes the database after every queued operation; repeated calls succeed.</summary>
    Task CloseAsync();
}
