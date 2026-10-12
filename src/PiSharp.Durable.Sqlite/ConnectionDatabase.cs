// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/storage/sqlite/node.ts
//
// Upstream adapts node:sqlite's DatabaseSync; this port adapts a Microsoft.Data.Sqlite connection (decision 0005) with the same serial
// operation queue, statement cache, transaction handles and pragmas. Differences, each forced by the binding:
// - Microsoft.Data.Sqlite binds parameters by name, so each unnamed "?" outside literals and comments is numbered ("?1", "?2", ...) when a
//   statement is prepared; callers still pass positional values.
// - Connections are opened with Pooling=False so that closing releases the file (Windows keeps pooled files open and locked).
// - Microsoft.Data.Sqlite retries a busy statement until its command timeout on top of SQLite's busy_timeout; the command timeout is set
//   to the busy timeout rounded up to whole seconds (at least one), so a competing lock fails after roughly the same time.
// - Opening checks the SQLite library is at least 3.37.0, the first release with STRICT tables, which the schema uses.
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PiSharp.Durable.Sqlite;

/// <summary>Connection settings for a durable storage file (upstream <c>NodeSqliteStorageOptions</c>).</summary>
public sealed record SqliteStorageOptions
{
    /// <summary>SQLite WAL auto-checkpoint threshold in pages. SQLite and this adapter default to 1,000; 0 disables it.</summary>
    public int? WalAutoCheckpointPages { get; init; }

    /// <summary>Time SQLite waits for a competing file lock. SQLite defaults to 0; this adapter defaults to 5,000 ms.</summary>
    public int? BusyTimeoutMs { get; init; }
}

/// <summary>Runs operations in call order. An operation starts immediately when nothing is running or waiting; otherwise it waits for
/// everything before it. An asynchronous operation holds the queue until it settles (upstream <c>SerialOperationQueue</c>).</summary>
internal sealed class SerialOperationQueue
{
    private readonly Lock gate = new();
    private Task tail = Task.CompletedTask;

    public Task<T> Run<T>(Func<T> operation) => RunAsync(() => Task.FromResult(operation()));

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

/// <summary>Executes SQL on one connection. Prepared statements are cached per connection by SQL text, so the database and its
/// transaction handles share them across transactions.</summary>
public abstract class SqliteConnectionExecutor : ISqliteExecutor
{
    private protected SqliteConnectionExecutor(SqliteConnection connection, StatementCache statements)
    {
        Connection = connection;
        Statements = statements;
    }

    private protected SqliteConnection Connection { get; }

    private protected StatementCache Statements { get; }

    /// <inheritdoc/>
    public Task ExecAsync(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return RunOperation(() =>
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
            return true;
        });
    }

    /// <inheritdoc/>
    public Task RunAsync(string sql, params object?[] parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        return RunOperation(() =>
        {
            var command = Statements.Bind(Connection, sql, parameters);
            command.ExecuteNonQuery();
            return true;
        });
    }

    /// <inheritdoc/>
    public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        return RunOperation(() =>
        {
            using var reader = Statements.Bind(Connection, sql, parameters).ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        });
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        return RunOperation<IReadOnlyList<SqliteRow>>(() =>
        {
            using var reader = Statements.Bind(Connection, sql, parameters).ExecuteReader();
            var rows = new List<SqliteRow>();
            while (reader.Read()) rows.Add(ReadRow(reader));
            return rows;
        });
    }

    private protected abstract Task<T> RunOperation<T>(Func<T> operation);

    private static SqliteRow ReadRow(SqliteDataReader reader)
    {
        var columns = new KeyValuePair<string, object?>[reader.FieldCount];
        for (var index = 0; index < columns.Length; index++)
        {
            var value = reader.GetValue(index);
            columns[index] = new(reader.GetName(index), value is DBNull ? null : value);
        }
        return new SqliteRow(columns);
    }

    /// <summary>Prepared commands by SQL text, for one connection.</summary>
    internal sealed class StatementCache
    {
        private readonly Dictionary<string, SqliteCommand> commands = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> prepares = new(StringComparer.Ordinal);

        /// <summary>How many times each SQL text was prepared; a test checks no statement is prepared twice.</summary>
        public IReadOnlyDictionary<string, int> PrepareCounts => prepares;

        public SqliteCommand Bind(SqliteConnection connection, string sql, object?[] parameters)
        {
            if (!commands.TryGetValue(sql, out var command))
            {
                command = connection.CreateCommand();
                command.CommandText = NumberParameters(sql);
                command.Prepare();
                prepares[sql] = prepares.GetValueOrDefault(sql) + 1;
                commands[sql] = command;
            }
            command.Parameters.Clear();
            for (var index = 0; index < parameters.Length; index++)
                command.Parameters.Add(new SqliteParameter("?" + (index + 1).ToString(CultureInfo.InvariantCulture), Value(parameters[index], index)));
            return command;
        }

        public void Clear()
        {
            foreach (var command in commands.Values) command.Dispose();
            commands.Clear();
        }

        private static object Value(object? value, int index) => value switch
        {
            null => DBNull.Value,
            long or double or string or byte[] => value,
            int integer => (long)integer,
            _ => throw new ArgumentException($"SQLite binding {index + 1} has unsupported type {value.GetType().Name}"),
        };

        /// <summary>Numbers each unnamed <c>?</c> parameter outside string literals, quoted identifiers and comments.</summary>
        internal static string NumberParameters(string sql)
        {
            var text = new StringBuilder(sql.Length + 8);
            var number = 0;
            for (var index = 0; index < sql.Length; index++)
            {
                var character = sql[index];
                switch (character)
                {
                    case '\'' or '"' or '`':
                    {
                        var end = sql.IndexOf(character, index + 1);
                        // A doubled quote escapes itself and continues the literal.
                        while (end >= 0 && end + 1 < sql.Length && sql[end + 1] == character) end = sql.IndexOf(character, end + 2);
                        end = end < 0 ? sql.Length - 1 : end;
                        text.Append(sql, index, end - index + 1);
                        index = end;
                        continue;
                    }
                    case '[':
                    {
                        var end = sql.IndexOf(']', index + 1);
                        end = end < 0 ? sql.Length - 1 : end;
                        text.Append(sql, index, end - index + 1);
                        index = end;
                        continue;
                    }
                    case '-' when index + 1 < sql.Length && sql[index + 1] == '-':
                    {
                        var end = sql.IndexOf('\n', index);
                        end = end < 0 ? sql.Length - 1 : end;
                        text.Append(sql, index, end - index + 1);
                        index = end;
                        continue;
                    }
                    case '/' when index + 1 < sql.Length && sql[index + 1] == '*':
                    {
                        var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                        end = end < 0 ? sql.Length - 1 : end + 1;
                        text.Append(sql, index, end - index + 1);
                        index = end;
                        continue;
                    }
                    case '?' when index + 1 >= sql.Length || !char.IsAsciiDigit(sql[index + 1]):
                        text.Append('?').Append(++number);
                        continue;
                    default:
                        text.Append(character);
                        continue;
                }
            }
            return text.ToString();
        }
    }
}

/// <summary>A transaction handle: valid only while its transaction's callback runs.</summary>
internal sealed class SqliteConnectionTransaction : SqliteConnectionExecutor
{
    private readonly TransactionScope scope;

    internal SqliteConnectionTransaction(SqliteConnection connection, StatementCache statements, TransactionScope scope)
        : base(connection, statements)
    {
        this.scope = scope;
    }

    private protected override Task<T> RunOperation<T>(Func<T> operation)
    {
        try
        {
            if (!scope.Active) throw new InvalidOperationException("SQLite transaction handle is no longer active");
            return Task.FromResult(operation());
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    internal sealed class TransactionScope
    {
        public bool Active { get; set; } = true;
    }
}

/// <summary>
/// <see cref="ISqliteDatabase"/> backed by one Microsoft.Data.Sqlite connection (upstream <c>NodeSqliteDatabase</c>). Every operation and
/// transaction runs through one serial queue, in call order; a transaction holds the queue until it commits or rolls back.
/// </summary>
public sealed class SqliteConnectionDatabase : SqliteConnectionExecutor, ISqliteDatabase
{
    /// <summary>The oldest SQLite library the schema supports: 3.37.0 added STRICT tables.</summary>
    public static Version MinimumSqliteVersion { get; } = new(3, 37, 0);

    private const int DefaultWalAutoCheckpointPages = 1_000;
    private const int DefaultBusyTimeoutMs = 5_000;

    private readonly SerialOperationQueue access = new();
    private bool closed;

    /// <summary>Adapts an open connection, which the database then owns. Open file connections with <c>Pooling=False</c>.</summary>
    public SqliteConnectionDatabase(SqliteConnection connection) : base(Checked(connection), new StatementCache()) { }

    /// <summary>How many times each SQL text was prepared on this connection.</summary>
    internal IReadOnlyDictionary<string, int> PrepareCounts => Statements.PrepareCounts;

    /// <summary>The SQLite library version of the connection.</summary>
    public Version SqliteVersion => ParseVersion(Connection.ServerVersion);

    /// <inheritdoc/>
    public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return access.RunAsync(async () =>
        {
            AssertOpen();
            Execute("BEGIN IMMEDIATE");
            var scope = new SqliteConnectionTransaction.TransactionScope();
            try
            {
                var result = await callback(new SqliteConnectionTransaction(Connection, Statements, scope)).ConfigureAwait(false);
                scope.Active = false;
                Execute("COMMIT");
                return result;
            }
            catch (Exception error)
            {
                scope.Active = false;
                try
                {
                    Execute("ROLLBACK");
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("SQLite transaction failed and rollback failed", error, rollbackError);
                }
                throw;
            }
        });
    }

    /// <inheritdoc/>
    public Task CloseAsync() => access.Run(() =>
    {
        if (closed) return true;
        closed = true;
        Statements.Clear();
        try
        {
            Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        finally
        {
            Connection.Close();
            Connection.Dispose();
        }
        return true;
    });

    private protected override Task<T> RunOperation<T>(Func<T> operation) => access.Run(() =>
    {
        AssertOpen();
        return operation();
    });

    /// <summary>Opens and configures a database file (upstream <c>openNodeSqliteDatabase</c>): WAL journal, <c>synchronous=NORMAL</c>,
    /// the busy timeout and the WAL auto-checkpoint threshold. <c>":memory:"</c> opens a private in-memory database.</summary>
    public static async Task<SqliteConnectionDatabase> OpenAsync(string path, SqliteStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var checkpointPages = options?.WalAutoCheckpointPages ?? DefaultWalAutoCheckpointPages;
        var timeout = options?.BusyTimeoutMs ?? DefaultBusyTimeoutMs;
        ArgumentOutOfRangeException.ThrowIfNegative(timeout, nameof(options));
        if (path != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (timeout + 999) / 1000),
        }.ToString());
        SqliteConnectionDatabase adapter;
        try
        {
            connection.Open();
            adapter = new SqliteConnectionDatabase(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        try
        {
            if (adapter.SqliteVersion < MinimumSqliteVersion)
                throw new NotSupportedException($"Durable SQLite storage needs SQLite {MinimumSqliteVersion} or newer for STRICT tables; the loaded " +
                    $"library is {connection.ServerVersion}");
            await adapter.ExecAsync($"PRAGMA busy_timeout = {timeout.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            await adapter.ExecAsync("PRAGMA journal_mode = WAL").ConfigureAwait(false);
            await adapter.ExecAsync("PRAGMA synchronous = NORMAL").ConfigureAwait(false);
            await adapter.ExecAsync($"PRAGMA wal_autocheckpoint = {checkpointPages.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            return adapter;
        }
        catch
        {
            try
            {
                await adapter.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the configuration failure.
            }
            throw;
        }
    }

    private void Execute(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void AssertOpen()
    {
        if (closed) throw new InvalidOperationException("SQLite database is closed");
    }

    private static SqliteConnection Checked(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection;
    }

    private static Version ParseVersion(string text)
    {
        var parts = text.Split('.');
        int Part(int index) => index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return new Version(Part(0), Part(1), Part(2));
    }
}
