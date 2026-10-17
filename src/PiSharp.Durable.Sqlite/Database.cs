// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/storage/sqlite/database.ts
//
// Upstream's SqliteValue is null | number | bigint | string | Uint8Array; here a binding is null, long (or int), double, string or byte[],
// and a result column is null, long, double, string or byte[]. Upstream returns rows as plain objects; here a row is an ordered,
// read-only map from column name to value.
using System.Collections;
using System.Globalization;
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Sqlite;

/// <summary>
/// Asynchronous SQL operations shared by a database and its transaction handles (upstream <c>SqliteExecutor</c>).
/// <para><see cref="ExecAsync"/> runs SQL text without bindings and may contain several statements. <see cref="RunAsync"/>,
/// <see cref="GetAsync"/> and <see cref="AllAsync"/> execute one statement with positional <c>?</c> bindings. Adapters may cache prepared
/// statements by SQL text, so callers pass values as bindings instead of interpolating them.</para>
/// </summary>
public interface ISqliteExecutor
{
    /// <summary>Runs SQL text, which may hold several statements, without bindings.</summary>
    Task ExecAsync(string sql);

    /// <summary>Runs one statement with positional bindings: null, <see cref="long"/>, <see cref="int"/>, <see cref="double"/>,
    /// <see cref="string"/> or <see cref="byte"/>[].</summary>
    Task RunAsync(string sql, params object?[] parameters);

    /// <summary>Runs one statement and returns its first row, or null when it returns none.</summary>
    Task<SqliteRow?> GetAsync(string sql, params object?[] parameters);

    /// <summary>Runs one statement and returns every row.</summary>
    Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters);
}

/// <summary>
/// Minimal database facade required by <see cref="SqliteStorage"/> (upstream <c>SqliteDatabase</c>). All operations are asynchronous so
/// adapters may execute outside the harness runtime.
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

/// <summary>Conveniences over <see cref="ISqliteDatabase"/>.</summary>
public static class SqliteDatabaseExtensions
{
    /// <summary>Runs <paramref name="callback"/> in one transaction.</summary>
    public static Task TransactionAsync(this ISqliteDatabase database, Func<ISqliteExecutor, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(callback);
        return database.TransactionAsync(async transaction => { await callback(transaction).ConfigureAwait(false); return true; });
    }
}

/// <summary>One result row: its columns in statement order, each null, <see cref="long"/>, <see cref="double"/>, <see cref="string"/> or
/// <see cref="byte"/>[].</summary>
public sealed class SqliteRow : IReadOnlyDictionary<string, object?>
{
    private readonly KeyValuePair<string, object?>[] columns;

    /// <summary>A row of <paramref name="columns"/> in order.</summary>
    public SqliteRow(IEnumerable<KeyValuePair<string, object?>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        this.columns = [.. columns];
    }

    /// <inheritdoc/>
    public object? this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"No column \"{key}\"");

    /// <inheritdoc/>
    public IEnumerable<string> Keys => columns.Select(column => column.Key);

    /// <inheritdoc/>
    public IEnumerable<object?> Values => columns.Select(column => column.Value);

    /// <inheritdoc/>
    public int Count => columns.Length;

    /// <inheritdoc/>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out object? value)
    {
        foreach (var column in columns)
        {
            if (string.Equals(column.Key, key, StringComparison.Ordinal)) { value = column.Value; return true; }
        }
        value = null;
        return false;
    }

    /// <summary>An INTEGER column.</summary>
    public long GetInt64(string key) => this[key] is long value ? value : throw new InvalidCastException($"Column \"{key}\" is not an integer");

    /// <summary>A nullable INTEGER column.</summary>
    public long? GetNullableInt64(string key) => this[key] switch
    {
        null => null,
        long value => value,
        _ => throw new InvalidCastException($"Column \"{key}\" is not an integer"),
    };

    /// <summary>A TEXT column.</summary>
    public string GetString(string key) => this[key] is string value ? value : throw new InvalidCastException($"Column \"{key}\" is not text");

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)columns).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The row as a JSON object, for diagnostics and tests: integers and reals as numbers, text as strings, blobs as base64.</summary>
    public override string ToString()
    {
        var text = new StringBuilder("{");
        for (var index = 0; index < columns.Length; index++)
        {
            if (index > 0) text.Append(',');
            text.Append(JsonValue.String(columns[index].Key).ToJson()).Append(':').Append(columns[index].Value switch
            {
                null => "null",
                long integer => integer.ToString(CultureInfo.InvariantCulture),
                double real => JsonValue.Number(real).ToJson(),
                string value => JsonValue.String(value).ToJson(),
                byte[] blob => JsonValue.String(Convert.ToBase64String(blob)).ToJson(),
                var other => JsonValue.String(Convert.ToString(other, CultureInfo.InvariantCulture) ?? "").ToJson(),
            });
        }
        return text.Append('}').ToString();
    }
}
