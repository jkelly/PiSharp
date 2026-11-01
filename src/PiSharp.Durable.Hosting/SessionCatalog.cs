// PiSharp-only (decision 0008): the device catalog, after durable-pi's catalog.sqlite (apps/dpi-backend/src/store.ts) with its known gaps
// closed: archiving removes every row of the session, and a spawn record is written in the same transaction as its session.
using Microsoft.Data.Sqlite;

namespace PiSharp.Durable.Hosting;

/// <summary>One session row of the catalog.</summary>
/// <param name="Id">The session's UUID, also its directory name.</param>
/// <param name="Name">Its display name; not unique.</param>
/// <param name="Cwd">The working directory its agents start in.</param>
/// <param name="CreatedAt">Unix milliseconds.</param>
/// <param name="Provider">Main's model provider, kept in step with Main.</param>
/// <param name="Model">Main's model ID.</param>
/// <param name="Compaction">The session's compaction settings as JSON; null follows the defaults of Main's model window.</param>
internal sealed record CatalogSession(string Id, string Name, string Cwd, long CreatedAt, string? Provider, string? Model, string? Compaction,
    string? InitialThinking = null, string? ArchivePath = null);

/// <summary>
/// The device catalog in SQLite: sessions, custom agent names, spawn records, the device identity, and the device's shared App State with
/// its receipts, recurring schedules, scoped credentials and pending session lifecycle operations; from version 5 the application records
/// of decision 0010 (operation receipts, blobs, budgets, policies, the judgment cache), from version 6 its source monitoring records and
/// from version 7 its feedback events and preference profiles. Schema version in <c>PRAGMA user_version</c>; a newer
/// catalog is refused rather than misread, and an older one is backed up before it is migrated. One connection, serialized by a lock.
/// </summary>
internal sealed partial class SessionCatalog : IDisposable
{
    internal const int SchemaVersion = 7;

    private const string Schema = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE sessions (
          id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          cwd TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          provider TEXT,
          model TEXT,
          compaction TEXT
        );
        CREATE TABLE agent_names (
          session_id TEXT NOT NULL,
          conversation_id INTEGER NOT NULL,
          name TEXT NOT NULL,
          PRIMARY KEY (session_id, conversation_id)
        );
        CREATE TABLE session_spawns (key TEXT PRIMARY KEY, session_id TEXT NOT NULL);
        """;

    private const string SchemaV2 = """
        CREATE TABLE app_state (id INTEGER PRIMARY KEY CHECK (id = 1), value TEXT NOT NULL, revision INTEGER NOT NULL);
        CREATE TABLE app_state_receipts (key TEXT PRIMARY KEY, value TEXT NOT NULL, revision INTEGER NOT NULL, created_at INTEGER NOT NULL);
        """;

    private const string SchemaV3 = """
        ALTER TABLE sessions ADD COLUMN initial_thinking TEXT;
        ALTER TABLE sessions ADD COLUMN archive_path TEXT;
        ALTER TABLE session_spawns ADD COLUMN request TEXT;
        """;

    private readonly Lock gate = new();
    private readonly SqliteConnection connection;

    private SessionCatalog(SqliteConnection connection) => this.connection = connection;

    /// <summary>Opens or creates the catalog at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The catalog was written by a newer host.</exception>
    public static SessionCatalog Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA journal_mode = WAL;");
            Execute(connection, null, "PRAGMA busy_timeout = 3000;");
            var version = (long)(Scalar(connection, null, "PRAGMA user_version;") ?? 0L);
            if (version > SchemaVersion)
                throw new InvalidDataException($"The device catalog {path} has schema version {version}; this host reads version {SchemaVersion}.");
            if (version < SchemaVersion)
            {
                // Decision 0010: a consistent copy (SQLite's backup API, which includes the WAL) before the first changed byte. There is no
                // in-place downgrade; restoring the copy is the way back.
                if (version > 0) Backup(connection, path, version);
                using var transaction = connection.BeginTransaction();
                if (version < 1) Execute(connection, transaction, Schema);
                if (version < 2) Execute(connection, transaction, SchemaV2);
                if (version < 3) Execute(connection, transaction, SchemaV3);
                if (version < 4) Execute(connection, transaction, SchemaV4);
                if (version < 5) Execute(connection, transaction, SchemaV5);
                if (version < 6) Execute(connection, transaction, SchemaV6);
                if (version < 7) Execute(connection, transaction, SchemaV7);
                Execute(connection, transaction, $"PRAGMA user_version = {SchemaVersion};");
                transaction.Commit();
            }
            return new SessionCatalog(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Copies the catalog to <c>catalog.v&lt;version&gt;.bak.sqlite</c> beside it (a numbered name when that exists).</summary>
    private static void Backup(SqliteConnection connection, string path, long version)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var target = Path.Join(directory, $"catalog.v{version}.bak.sqlite");
        for (var attempt = 2; File.Exists(target); attempt++) target = Path.Join(directory, $"catalog.v{version}.bak-{attempt}.sqlite");
        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        copy.Open();
        connection.BackupDatabase(copy);
    }

    /// <summary>The device identity: a UUID made on the first open and kept for the catalog's life.</summary>
    public string DeviceUid()
    {
        lock (gate)
        {
            if (Scalar(connection, null, "SELECT value FROM meta WHERE key = 'device_uid';") is string existing) return existing;
            var uid = Guid.NewGuid().ToString("D");
            Execute(connection, null, "INSERT INTO meta (key, value) VALUES ('device_uid', $value);", ("$value", uid));
            return uid;
        }
    }

    /// <summary>Every session, oldest first.</summary>
    public IReadOnlyList<CatalogSession> Sessions()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, cwd, created_at, provider, model, compaction, initial_thinking, archive_path FROM sessions ORDER BY created_at, id;";
            using var reader = command.ExecuteReader();
            var sessions = new List<CatalogSession>();
            while (reader.Read())
                sessions.Add(new CatalogSession(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            return sessions;
        }
    }

    /// <summary>The session a spawn key created, or null.</summary>
    public string? SpawnedSession(string key, string? request = null)
    {
        lock (gate)
        {
            using var command = Command(connection, null, "SELECT session_id, request FROM session_spawns WHERE key = $key;", [("$key", key)]);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (request is not null && (reader.IsDBNull(1) || reader.GetString(1) != request))
                throw new HostInvalidException("This creation key was already used for a different request, or predates request verification.");
            return reader.GetString(0);
        }
    }

    /// <summary>Adds a session and, with <paramref name="spawnKey"/>, its spawn record, in one transaction.</summary>
    public void AddSession(CatalogSession session, string? spawnKey, string? request = null)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction,
                "INSERT INTO sessions (id, name, cwd, created_at, provider, model, compaction, initial_thinking) VALUES ($id, $name, $cwd, $created, $provider, $model, $compaction, $thinking);",
                ("$id", session.Id), ("$name", session.Name), ("$cwd", session.Cwd), ("$created", session.CreatedAt), ("$provider", session.Provider),
                ("$model", session.Model), ("$compaction", session.Compaction), ("$thinking", session.InitialThinking));
            if (spawnKey is not null)
                Execute(connection, transaction, "INSERT INTO session_spawns (key, session_id, request) VALUES ($key, $id, $request);",
                    ("$key", spawnKey), ("$id", session.Id), ("$request", request));
            transaction.Commit();
        }
    }

    /// <summary>The initial root and its configuration are now committed in the session database.</summary>
    public void CompleteInitialization(string id) => Update("UPDATE sessions SET initial_thinking = $value WHERE id = $id;", id, null);

    /// <summary>Records an archive destination before moving any files. Recovery completes this same move.</summary>
    public void BeginArchive(string id, string path) => Update("UPDATE sessions SET archive_path = $value WHERE id = $id;", id, path);

    public string? ArchivePath(string id)
    {
        lock (gate) return Scalar(connection, null, "SELECT archive_path FROM sessions WHERE id = $id;", ("$id", id)) as string;
    }

    /// <summary>Renames a session.</summary>
    public void RenameSession(string id, string name) => Update("UPDATE sessions SET name = $value WHERE id = $id;", id, name);

    /// <summary>Records Main's model.</summary>
    public void SetSessionModel(string id, string provider, string model)
    {
        lock (gate)
            Execute(connection, null, "UPDATE sessions SET provider = $provider, model = $model WHERE id = $id;", ("$provider", provider), ("$model", model), ("$id", id));
    }

    /// <summary>Stores the session's compaction settings as JSON; null returns to the model window's defaults.</summary>
    public void SetCompaction(string id, string? json) => Update("UPDATE sessions SET compaction = $value WHERE id = $id;", id, json);

    /// <summary>The session's custom agent names by conversation ID.</summary>
    public IReadOnlyDictionary<long, string> AgentNames(string sessionId)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT conversation_id, name FROM agent_names WHERE session_id = $id;";
            command.Parameters.AddWithValue("$id", sessionId);
            using var reader = command.ExecuteReader();
            var names = new Dictionary<long, string>();
            while (reader.Read()) names[reader.GetInt64(0)] = reader.GetString(1);
            return names;
        }
    }

    /// <summary>Sets an agent's custom name; null removes it, so the derived name applies again.</summary>
    public void SetAgentName(string sessionId, long conversationId, string? name)
    {
        lock (gate)
        {
            if (name is null)
                Execute(connection, null, "DELETE FROM agent_names WHERE session_id = $id AND conversation_id = $conversation;",
                    ("$id", sessionId), ("$conversation", conversationId));
            else
                Execute(connection, null,
                    "INSERT INTO agent_names (session_id, conversation_id, name) VALUES ($id, $conversation, $name) " +
                    "ON CONFLICT (session_id, conversation_id) DO UPDATE SET name = excluded.name;",
                    ("$id", sessionId), ("$conversation", conversationId), ("$name", name));
        }
    }

    /// <summary>Removes the session's row, its agent names and its spawn records in one transaction.</summary>
    public void RemoveSession(string id)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM agent_names WHERE session_id = $id;", ("$id", id));
            Execute(connection, transaction, "DELETE FROM trigger_credentials WHERE schedule_id IN (SELECT id FROM job_schedules WHERE session_id = $id);", ("$id", id));
            Execute(connection, transaction, "DELETE FROM job_schedules WHERE session_id = $id;", ("$id", id));
            Execute(connection, transaction, "DELETE FROM session_spawns WHERE session_id = $id;", ("$id", id));
            Execute(connection, transaction, "DELETE FROM sessions WHERE id = $id;", ("$id", id));
            transaction.Commit();
        }
    }

    /// <summary>The App State and its revision; null before the first write.</summary>
    public (string Value, long Revision)? AppState()
    {
        lock (gate) return ReadAppState(null);
    }

    /// <summary>
    /// Changes the App State in one transaction: <paramref name="change"/> maps the current value and revision (null before the first
    /// write) to the new value, which is stored with the next revision. With <paramref name="receiptKey"/>, a key already used returns its
    /// stored result and changes nothing, so a replayed tool call applies once.
    /// </summary>
    public (string Value, long Revision) ChangeAppState(Func<(string Value, long Revision)?, string> change, string? receiptKey, long createdAt)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            if (receiptKey is not null)
            {
                using var receipt = Command(connection, transaction, "SELECT value, revision FROM app_state_receipts WHERE key = $key;", [("$key", receiptKey)]);
                using var reader = receipt.ExecuteReader();
                if (reader.Read()) return (reader.GetString(0), reader.GetInt64(1));
            }
            var current = ReadAppState(transaction);
            var value = change(current);
            var revision = (current?.Revision ?? 0) + 1;
            Execute(connection, transaction,
                "INSERT INTO app_state (id, value, revision) VALUES (1, $value, $revision) ON CONFLICT (id) DO UPDATE SET value = excluded.value, revision = excluded.revision;",
                ("$value", value), ("$revision", revision));
            if (receiptKey is not null)
                Execute(connection, transaction, "INSERT INTO app_state_receipts (key, value, revision, created_at) VALUES ($key, $value, $revision, $created);",
                    ("$key", receiptKey), ("$value", value), ("$revision", revision), ("$created", createdAt));
            transaction.Commit();
            return (value, revision);
        }
    }

    private (string Value, long Revision)? ReadAppState(SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT value, revision FROM app_state WHERE id = 1;", []);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    /// <summary>The number of catalog rows that mention the session, across its three tables (tests).</summary>
    internal long RowsFor(string id)
    {
        lock (gate)
            return (long)(Scalar(connection, null,
                "SELECT (SELECT count(*) FROM sessions WHERE id = $id) + (SELECT count(*) FROM agent_names WHERE session_id = $id) + " +
                "(SELECT count(*) FROM session_spawns WHERE session_id = $id);", ("$id", id)) ?? 0L);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (gate) connection.Dispose();
    }

    private void Update(string sql, string id, string? value)
    {
        lock (gate) Execute(connection, null, sql, ("$value", value), ("$id", id));
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar() is var value && value is DBNull ? null : value;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
