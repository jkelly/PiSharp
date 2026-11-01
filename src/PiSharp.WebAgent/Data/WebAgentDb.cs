// Decision 0011 (run W2, WU4): webagent.db, the Web Agent's own SQLite database (account metadata, active accounts, chat bindings, settings;
// later the spend ledger and evals). Chats themselves are durable sessions under device\, not rows here. Migrations come from DI
// (IWebAgentMigration), each inside its area's id range; schema_migrations records every id applied, so an id added later runs even when it
// is lower than one already applied, and PRAGMA user_version holds the highest applied id for diagnostics only. The journal is WAL, so the
// pages read while a write commits. Same package pins as the hub's registry; connections are not pooled, so the file is released as soon
// as the last one closes.
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Data;

/// <summary>webagent.db: its migrations and short-lived connections. A singleton; every member is safe to call from many threads.</summary>
public sealed class WebAgentDb
{
    private readonly Lock gate = new();
    private readonly ILogger log;
    private volatile bool migrated;

    /// <summary>The database at <see cref="WebAgentOptions.DatabasePath"/>, with <paramref name="migrations"/> (registered in DI).</summary>
    /// <exception cref="ArgumentException">A migration id is outside every <see cref="MigrationArea"/>, or two migrations share an id.</exception>
    public WebAgentDb(WebAgentOptions options, IEnumerable<IWebAgentMigration> migrations, ILogger<WebAgentDb>? log = null)
        : this(options.DatabasePath, migrations, log) { }

    /// <summary>The database at <paramref name="path"/>, with <paramref name="migrations"/>.</summary>
    /// <exception cref="ArgumentException">A migration id is outside every <see cref="MigrationArea"/>, or two migrations share an id.</exception>
    public WebAgentDb(string path, IEnumerable<IWebAgentMigration> migrations, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(migrations);
        Path = System.IO.Path.GetFullPath(path);
        this.log = log ?? NullLogger.Instance;
        var list = migrations.OrderBy(migration => migration.Id).ToList();
        foreach (var migration in list)
            if (MigrationArea.Of(migration.Id) is null)
                throw new ArgumentException($"Migration {migration.Id} ({migration.Name}) is outside every area's id range.", nameof(migrations));
        if (list.GroupBy(migration => migration.Id).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
            throw new ArgumentException($"Migration id {duplicate.Key} is used by {string.Join(" and ", duplicate.Select(m => $"\"{m.Name}\""))}.", nameof(migrations));
        Migrations = list;
    }

    /// <summary>The database file.</summary>
    public string Path { get; }

    /// <summary>The registered migrations, lowest id first.</summary>
    public IReadOnlyList<IWebAgentMigration> Migrations { get; }

    /// <summary>Applies every registered migration not yet recorded in <c>schema_migrations</c>, lowest id first, each in its own
    /// <c>BEGIN IMMEDIATE</c> transaction with its record, then sets <c>PRAGMA user_version</c> to the highest applied id. Safe to call again:
    /// it applies nothing new and returns an empty list.</summary>
    /// <returns>The ids this call applied.</returns>
    public IReadOnlyList<int> Migrate()
    {
        lock (gate)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var connection = Connect();
            Execute(connection, null, "PRAGMA journal_mode=WAL;");
            Execute(connection, null, "CREATE TABLE IF NOT EXISTS schema_migrations (id INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL);");
            var applied = new List<int>();
            foreach (var migration in Migrations)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                if (Scalar(connection, transaction, "SELECT 1 FROM schema_migrations WHERE id = $id;", ("$id", migration.Id)) is not null)
                {
                    transaction.Rollback();
                    continue;
                }
                migration.Apply(connection, transaction);
                Execute(connection, transaction, "INSERT INTO schema_migrations (id, name, applied_at) VALUES ($id, $name, $at);",
                    ("$id", migration.Id), ("$name", migration.Name), ("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                transaction.Commit();
                applied.Add(migration.Id);
                log.LogInformation("webagent.db: applied migration {Id} ({Name}).", migration.Id, migration.Name);
            }
            var highest = Convert.ToInt64(Scalar(connection, null, "SELECT COALESCE(MAX(id), 0) FROM schema_migrations;") ?? 0L);
            // A PRAGMA takes no parameters; the value is an integer read from the table above.
            Execute(connection, null, $"PRAGMA user_version = {highest};");
            migrated = true;
            return applied;
        }
    }

    /// <summary>The ids recorded in <c>schema_migrations</c>, lowest first.</summary>
    public IReadOnlyList<int> AppliedMigrations() =>
        Query("SELECT id FROM schema_migrations ORDER BY id;", reader => reader.GetInt32(0));

    /// <summary><c>PRAGMA user_version</c>: the highest applied migration id (diagnostics only; <c>schema_migrations</c> decides what runs).</summary>
    public int UserVersion() => Convert.ToInt32(Scalar("PRAGMA user_version;") ?? 0L);

    /// <summary>An open connection to the database, migrated first if <see cref="Migrate"/> has not run in this process. The caller disposes
    /// it. Foreign keys are on and a busy database is waited for up to 5 seconds.</summary>
    public SqliteConnection Open()
    {
        if (!migrated) Migrate();
        return Connect();
    }

    /// <summary>Runs <paramref name="sql"/> and returns the number of rows changed.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        return Execute(connection, null, sql, parameters);
    }

    /// <summary>The first column of the first row of <paramref name="sql"/>, or null when there is no row (a SQL NULL is also null).</summary>
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        return Scalar(connection, null, sql, parameters);
    }

    /// <summary>Every row of <paramref name="sql"/>, read by <paramref name="read"/>.</summary>
    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var connection = Open();
        using var command = Command(connection, null, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    /// <summary>Runs <paramref name="work"/> in one <c>BEGIN IMMEDIATE</c> transaction (the write lock is taken first, so a read-then-write
    /// cannot interleave with another writer), and commits when it returns. An exception rolls everything back.</summary>
    public T Transaction<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = work(connection, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>A command on <paramref name="connection"/> with named parameters (null becomes SQL NULL).</summary>
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    /// <summary>Runs <paramref name="sql"/> on a connection the caller holds and returns the number of rows changed.</summary>
    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>The first column of the first row of <paramref name="sql"/> on a connection the caller holds, or null.</summary>
    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar() is { } value and not DBNull ? value : null;
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;");
        return connection;
    }
}
