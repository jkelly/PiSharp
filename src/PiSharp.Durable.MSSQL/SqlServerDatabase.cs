// Authored for PiSharp (decision 0007); upstream has no SQL Server adapter. The shape follows PiSharp.Durable.Sqlite's
// SqliteConnectionDatabase (upstream storage/sqlite/node.ts): one connection, one serial operation queue in call order, and transaction
// handles that are valid only while their callback runs. Differences forced by SQL Server:
// - Statements bind positional values as @p0, @p1, ... (SqlClient binds by name).
// - The connection is opened with Pooling=False: the store lock is owned by the session, and a pooled session would keep holding it after
//   close until the pool reused or pruned the connection.
// - Every session runs with SET NOCOUNT ON and SET XACT_ABORT ON, so a failed statement dooms its whole transaction.
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace PiSharp.Durable.MSSQL;

/// <summary>Runs operations in call order. An operation starts immediately when nothing is running or waiting; otherwise it waits for
/// everything before it. An asynchronous operation holds the queue until it settles.</summary>
internal sealed class SerialOperationQueue
{
    private readonly Lock gate = new();
    private Task tail = Task.CompletedTask;

    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        // Publish the barrier before the operation starts, so calls it makes synchronously wait behind it.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (gate)
        {
            previous = tail;
            tail = released.Task;
        }
        try
        {
            await previous.ConfigureAwait(false); // never faults
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            released.SetResult();
        }
    }
}

/// <summary>One result row: its columns in statement order, each null, <see cref="long"/>, <see cref="int"/>, <see cref="bool"/> or
/// <see cref="string"/>.</summary>
internal sealed class SqlServerRow(KeyValuePair<string, object?>[] columns)
{
    public object? this[string key]
    {
        get
        {
            foreach (var column in columns)
            {
                if (string.Equals(column.Key, key, StringComparison.Ordinal)) return column.Value;
            }
            throw new KeyNotFoundException($"No column \"{key}\"");
        }
    }

    /// <summary>An integer column of any width.</summary>
    public long GetInt64(string key) => this[key] switch
    {
        long value => value,
        int value => value,
        short value => value,
        byte value => value,
        _ => throw new InvalidCastException($"Column \"{key}\" is not an integer"),
    };

    /// <summary>A text column.</summary>
    public string GetString(string key) => this[key] is string value ? value : throw new InvalidCastException($"Column \"{key}\" is not text");
}

/// <summary>Executes SQL on the database's connection: directly for the database's own queued operations, or inside a transaction for a
/// handle. A handle is valid only while its callback runs.</summary>
internal sealed class SqlServerExecutor
{
    private readonly SqlConnection connection;
    private readonly SqlTransaction? transaction;

    internal SqlServerExecutor(SqlConnection connection, SqlTransaction? transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
    }

    internal bool Active { get; set; } = true;

    /// <summary>Runs one batch, which may hold several statements, with positional bindings @p0, @p1, ...: null, <see cref="long"/>,
    /// <see cref="int"/>, <see cref="bool"/> or <see cref="string"/>.</summary>
    public async Task RunAsync(string sql, params object?[] parameters)
    {
        await using var command = Command(sql, parameters);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Runs one batch and returns the first row of its first result set, or null when it returns none.</summary>
    public async Task<SqlServerRow?> GetAsync(string sql, params object?[] parameters)
    {
        await using var command = Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult).ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    /// <summary><see cref="GetAsync"/> with a command timeout of <paramref name="timeoutSeconds"/> instead of the connection's, for a batch
    /// that waits on the server for longer (a lock wait).</summary>
    public async Task<SqlServerRow?> GetWaitingAsync(int timeoutSeconds, string sql, params object?[] parameters)
    {
        await using var command = Command(sql, parameters);
        command.CommandTimeout = timeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult).ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    /// <summary>Runs one batch and returns every row of its first result set.</summary>
    public async Task<IReadOnlyList<SqlServerRow>> AllAsync(string sql, params object?[] parameters)
    {
        await using var command = Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult).ConfigureAwait(false);
        var rows = new List<SqlServerRow>();
        while (await reader.ReadAsync().ConfigureAwait(false)) rows.Add(ReadRow(reader));
        return rows;
    }

    private SqlCommand Command(string sql, object?[] parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Active) throw new InvalidOperationException("SQL Server transaction handle is no longer active");
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        for (var index = 0; index < parameters.Length; index++) command.Parameters.Add(Parameter(index, parameters[index]));
        return command;
    }

    private static SqlParameter Parameter(int index, object? value)
    {
        var name = "@p" + index.ToString(CultureInfo.InvariantCulture);
        return value switch
        {
            null => new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = DBNull.Value },
            long integer => new SqlParameter(name, SqlDbType.BigInt) { Value = integer },
            // INT, so that comparing an INT column (store_id) converts the binding rather than the column.
            int integer => new SqlParameter(name, SqlDbType.Int) { Value = integer },
            bool flag => new SqlParameter(name, SqlDbType.Bit) { Value = flag },
            // Two sizes only, so that a statement has at most two cached plans.
            string text => new SqlParameter(name, SqlDbType.NVarChar, text.Length <= 4000 ? 4000 : -1) { Value = text },
            _ => throw new ArgumentException($"SQL Server binding @p{index} has unsupported type {value.GetType().Name}"),
        };
    }

    private static SqlServerRow ReadRow(SqlDataReader reader)
    {
        var columns = new KeyValuePair<string, object?>[reader.FieldCount];
        for (var index = 0; index < columns.Length; index++)
        {
            var value = reader.GetValue(index);
            columns[index] = new(reader.GetName(index), value is DBNull ? null : value);
        }
        return new SqlServerRow(columns);
    }
}

/// <summary>
/// One SQL Server connection. Every operation and transaction runs through one serial queue, in call order; a transaction holds the queue
/// until it commits or rolls back. Calling the database from inside a callback therefore waits for that callback and, when awaited there,
/// never settles.
/// </summary>
internal sealed class SqlServerDatabase
{
    private readonly SqlConnection connection;
    private readonly SerialOperationQueue access = new();
    private bool closed;

    private SqlServerDatabase(SqlConnection connection)
    {
        this.connection = connection;
    }

    /// <summary>Opens a dedicated, unpooled connection and configures its session.</summary>
    public static async Task<SqlServerDatabase> OpenAsync(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await new SqlServerExecutor(connection, null).RunAsync("SET NOCOUNT ON; SET XACT_ABORT ON;").ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new SqlServerDatabase(connection);
    }

    /// <inheritdoc cref="SqlServerExecutor.RunAsync"/>
    public Task RunAsync(string sql, params object?[] parameters) => HoldAsync(async executor =>
    {
        await executor.RunAsync(sql, parameters).ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc cref="SqlServerExecutor.GetAsync"/>
    public Task<SqlServerRow?> GetAsync(string sql, params object?[] parameters) => HoldAsync(executor => executor.GetAsync(sql, parameters));

    /// <inheritdoc cref="SqlServerExecutor.AllAsync"/>
    public Task<IReadOnlyList<SqlServerRow>> AllAsync(string sql, params object?[] parameters) => HoldAsync(executor => executor.AllAsync(sql, parameters));

    /// <summary>Runs <paramref name="callback"/> alone on the connection, outside any transaction: no other operation of this database
    /// interleaves with its statements.</summary>
    public Task<T> HoldAsync<T>(Func<SqlServerExecutor, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return access.RunAsync(async () =>
        {
            AssertOpen();
            var executor = new SqlServerExecutor(connection, null);
            try
            {
                return await callback(executor).ConfigureAwait(false);
            }
            finally
            {
                executor.Active = false;
            }
        });
    }

    /// <summary>Runs <paramref name="callback"/> in one READ COMMITTED transaction and returns its result after commit. When the callback
    /// fails, the transaction is rolled back before the same exception is rethrown; if rollback fails, an <see cref="AggregateException"/>
    /// holds both.</summary>
    public Task<T> TransactionAsync<T>(Func<SqlServerExecutor, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return access.RunAsync(async () =>
        {
            AssertOpen();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
            var executor = new SqlServerExecutor(connection, transaction);
            try
            {
                var result = await callback(executor).ConfigureAwait(false);
                executor.Active = false;
                await transaction.CommitAsync().ConfigureAwait(false);
                return result;
            }
            catch (Exception error)
            {
                executor.Active = false;
                try
                {
                    // XACT_ABORT already rolled back a transaction that a statement failed; SqlClient then detaches it from the connection.
                    if (transaction.Connection is not null) await transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("SQL Server transaction failed and rollback failed", error, rollbackError);
                }
                throw;
            }
        });
    }

    /// <summary>Runs <paramref name="beforeClose"/> (when given, and the connection is still open) after every queued operation, then closes
    /// the connection; repeated calls succeed. A broken connection skips <paramref name="beforeClose"/>: its session, and every session-owned
    /// lock, has already ended.</summary>
    public Task CloseAsync(Func<SqlServerExecutor, Task>? beforeClose = null) => access.RunAsync(async () =>
    {
        if (closed) return true;
        closed = true;
        try
        {
            if (beforeClose is not null && connection.State == ConnectionState.Open)
                await beforeClose(new SqlServerExecutor(connection, null)).ConfigureAwait(false);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        return true;
    });

    private void AssertOpen()
    {
        if (closed) throw new InvalidOperationException("SQL Server database is closed");
    }
}
