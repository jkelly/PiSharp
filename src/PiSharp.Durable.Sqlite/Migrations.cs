// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/sqlite/migrations.ts and packages/chord/src/sqlite/migrations.ts
//
// The schema statements are upstream's text verbatim, whitespace included, so that sqlite_schema matches a database Pi created; a test
// pins their SHA-256 as computed from upstream's module. next_id is TEXT because node:sqlite rejects INTEGER results outside JavaScript's
// safe integer range; it stays TEXT here so that either tool opens the other's files. Upstream's generic runner (Chord's
// applySqliteMigrations over a named table) is the ApplyAsync overload that takes a table; Durable's overloads run it on durable_schema.
// Schema version 2 adds the sleeping task status by rebuilding the tasks table, as upstream does: an older PiSharp (1.2.0.x) or Pi (1.3.0
// and earlier) cannot open a database this version migrated.
using System.Collections.Immutable;

namespace PiSharp.Durable.Sqlite;

/// <summary>One immutable step of the schema history (upstream <c>SqliteMigration</c>).</summary>
/// <param name="Version">The schema version after this step; versions are contiguous from 1.</param>
/// <param name="Statements">The SQL statements, run in order.</param>
public sealed record SqliteMigration(int Version, ImmutableArray<string> Statements);

/// <summary>The durable SQLite schema and the migration runner.</summary>
public static class SqliteMigrations
{
    /// <summary>The metadata table <paramref name="table"/> that records the applied version (upstream's text inside Chord's
    /// <c>applySqliteMigrations</c>).</summary>
    internal static string SchemaTable(string table) =>
        $"CREATE TABLE IF NOT EXISTS {table} (\n\t\t\tsingleton INTEGER PRIMARY KEY CHECK (singleton = 1),\n\t\t\tversion INTEGER NOT NULL CHECK (version >= 0)\n\t\t) STRICT";

    /// <summary>The immutable, ordered schema history for a set of identifiers (upstream <c>createSqliteMigrations</c>).</summary>
    internal static ImmutableArray<SqliteMigration> Create(SqliteSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var (tables, indexes) = (schema.Tables, schema.Indexes);
        ImmutableArray<string> initialSchema =
        [
            $"CREATE TABLE {tables.DurableMetadata} (\n\t\t\tsingleton INTEGER PRIMARY KEY CHECK (singleton = 1),\n\t\t\tnext_id TEXT NOT NULL,\n\t\t\tnext_seq INTEGER NOT NULL\n\t\t) STRICT",
            $"INSERT INTO {tables.DurableMetadata} (singleton, next_id, next_seq) VALUES (1, '2', 1)",
            $"CREATE TABLE {tables.RecordIds} (\n\t\t\tid INTEGER PRIMARY KEY,\n\t\t\trecord_type TEXT NOT NULL CHECK (record_type IN ('conversation', 'entry', 'task', 'submission', 'document'))\n\t\t) STRICT",
            $"CREATE TABLE {tables.Conversations} (\n\t\t\tid INTEGER PRIMARY KEY,\n\t\t\towner_conversation_id INTEGER,\n\t\t\towner_task_id INTEGER,\n\t\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t\t) STRICT",
            $"CREATE INDEX {indexes.ConversationsByOwnerConversation} ON {tables.Conversations} (owner_conversation_id, id)",
            $"CREATE INDEX {indexes.ConversationsByOwnerTask} ON {tables.Conversations} (owner_task_id, id)",
            $"CREATE TABLE {tables.Entries} (\n\t\t\tid INTEGER PRIMARY KEY,\n\t\t\tconversation_id INTEGER NOT NULL,\n\t\t\thead INTEGER,\n\t\t\tcommit_seq INTEGER NOT NULL,\n\t\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t\t) STRICT",
            $"CREATE INDEX {indexes.EntriesByConversation} ON {tables.Entries} (conversation_id, id DESC)",
            $"CREATE INDEX {indexes.EntryHeadsByConversation} ON {tables.Entries} (conversation_id, id DESC) WHERE head IS NOT NULL",
            TasksTable(tables.Tasks, "'pending', 'running', 'waiting', 'completing', 'terminal'"),
            .. TasksIndexes(schema),
            $"CREATE TABLE {tables.Submissions} (\n\t\t\tid INTEGER PRIMARY KEY,\n\t\t\tconversation_id INTEGER NOT NULL,\n\t\t\trequest_id TEXT,\n\t\t\tstatus TEXT NOT NULL CHECK (status IN ('queued', 'placed', 'done', 'unanswered')),\n\t\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t\t) STRICT",
            $"CREATE INDEX {indexes.SubmissionsByRequest} ON {tables.Submissions} (conversation_id, request_id)",
            $"CREATE INDEX {indexes.SubmissionsByConversation} ON {tables.Submissions} (conversation_id, id)",
            $"CREATE INDEX {indexes.SubmissionsByStatus} ON {tables.Submissions} (status, id)",
            $"CREATE TABLE {tables.Documents} (\n\t\t\tid INTEGER PRIMARY KEY,\n\t\t\tkind TEXT NOT NULL,\n\t\t\tfamily INTEGER NOT NULL CHECK (family IN (0, 1)),\n\t\t\tkey_value TEXT NOT NULL,\n\t\t\tscope_kind TEXT NOT NULL CHECK (scope_kind IN ('session', 'conversation', 'task')),\n\t\t\towner_id INTEGER NOT NULL,\n\t\t\tcreated_at INTEGER NOT NULL,\n\t\t\tretired_at INTEGER,\n\t\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t\t) STRICT",
            $"CREATE INDEX {indexes.DocumentsByAddress}\n\t\t\tON {tables.Documents} (kind, scope_kind, owner_id, family, key_value, created_at DESC, retired_at)",
            $"CREATE INDEX {indexes.DocumentsByScope} ON {tables.Documents} (scope_kind, owner_id, id)",
            $"CREATE INDEX {indexes.DocumentsByScopeKind} ON {tables.Documents} (scope_kind, owner_id, kind, id)",
            $"CREATE TABLE {tables.DocumentRevisions} (\n\t\t\tdocument_id INTEGER NOT NULL,\n\t\t\tseq INTEGER NOT NULL,\n\t\t\tkind TEXT NOT NULL CHECK (kind IN ('base', 'delta')),\n\t\t\tversion INTEGER NOT NULL,\n\t\t\tcontent TEXT NOT NULL CHECK (json_valid(content)),\n\t\t\tPRIMARY KEY (document_id, seq)\n\t\t) STRICT",
            $"CREATE INDEX {indexes.DocumentRevisionsByKind} ON {tables.DocumentRevisions} (document_id, kind, seq DESC)",
        ];
        // Version 2 adds the sleeping task status. SQLite cannot change a CHECK constraint in place, so the table is rebuilt.
        var rebuiltTasks = tables.Tasks + "_v2";
        ImmutableArray<string> sleepingStatus =
        [
            TasksTable(rebuiltTasks, "'pending', 'running', 'waiting', 'sleeping', 'completing', 'terminal'"),
            $"INSERT INTO {rebuiltTasks} (id, conversation_id, kind, status, abort_requested, background, record)\n\t\t\tSELECT id, conversation_id, kind, status, abort_requested, background, record FROM {tables.Tasks}",
            $"DROP TABLE {tables.Tasks}",
            $"ALTER TABLE {rebuiltTasks} RENAME TO {tables.Tasks}",
            .. TasksIndexes(schema),
        ];
        return [new(1, initialSchema), new(2, sleepingStatus)];
    }

    private static string TasksTable(string table, string statuses) =>
        $"CREATE TABLE {table} (\n\t\t\tid INTEGER PRIMARY KEY,\n\t\t\tconversation_id INTEGER NOT NULL,\n\t\t\tkind TEXT NOT NULL,\n\t\t\tstatus TEXT NOT NULL CHECK (status IN ({statuses})),\n\t\t\tabort_requested INTEGER NOT NULL CHECK (abort_requested IN (0, 1)),\n\t\t\tbackground INTEGER NOT NULL CHECK (background IN (0, 1)),\n\t\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t\t) STRICT";

    private static string[] TasksIndexes(SqliteSchema schema)
    {
        var (tasks, indexes) = (schema.Tables.Tasks, schema.Indexes);
        return
        [
            $"CREATE INDEX {indexes.TasksByStatus} ON {tasks} (status, id)",
            $"CREATE INDEX {indexes.TasksByConversation} ON {tasks} (conversation_id, id)",
            $"CREATE INDEX {indexes.TasksByKind} ON {tasks} (kind, id)",
            $"CREATE INDEX {indexes.TasksByAbortRequested} ON {tasks} (abort_requested, id)",
            $"CREATE INDEX {indexes.TasksByBackground} ON {tasks} (background, id)",
        ];
    }

    /// <summary>The immutable, ordered schema history of the unprefixed schema (upstream <c>SQLITE_MIGRATIONS</c>).</summary>
    public static ImmutableArray<SqliteMigration> Migrations { get; } = Create(SqliteSchema.Default);

    /// <summary>The newest schema version (upstream <c>CURRENT_SQLITE_SCHEMA_VERSION</c>).</summary>
    public static int CurrentSchemaVersion { get; } = Migrations.IsEmpty ? 0 : Migrations[^1].Version;

    /// <summary>Applies all pending schema migrations of the unprefixed schema atomically (Durable's <c>applySqliteMigrations</c>).</summary>
    public static Task ApplyAsync(ISqliteDatabase database, IReadOnlyList<SqliteMigration>? migrations = null) =>
        ApplyAsync(database, SqliteSchema.Default, migrations);

    /// <summary>Applies migrations rendered for <paramref name="schema"/>, by default its whole history (upstream
    /// <c>applySqliteMigrationsForSchema</c>).</summary>
    internal static Task ApplyAsync(ISqliteDatabase database, SqliteSchema schema, IReadOnlyList<SqliteMigration>? migrations = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return ApplyAsync(database, migrations ?? (ReferenceEquals(schema, SqliteSchema.Default) ? Migrations : Create(schema)), schema.Tables.DurableSchema);
    }

    /// <summary>Applies every pending migration atomically, tracking the version in the metadata table <paramref name="table"/> (Chord's
    /// <c>applySqliteMigrations</c>). The table name is checked as <see cref="SqliteSchemaPrefix.Validate"/> checks a prefix, and the
    /// versions must be contiguous from 1, before any SQL runs. One transaction creates the table when it is missing and runs the pending
    /// migrations, so a failure leaves the database as it was.</summary>
    public static async Task ApplyAsync(ISqliteDatabase database, IReadOnlyList<SqliteMigration> migrations, string table)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(migrations);
        SqliteSchemaPrefix.Validate(table);
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index]?.Version != index + 1)
                throw new InvalidOperationException("SQLite migrations must have contiguous versions starting at 1");
        }

        await database.TransactionAsync(async transaction =>
        {
            await transaction.ExecAsync(SchemaTable(table)).ConfigureAwait(false);
            await transaction.RunAsync($"INSERT OR IGNORE INTO {table} (singleton, version) VALUES (1, 0)").ConfigureAwait(false);
            var row = await transaction.GetAsync($"SELECT version FROM {table} WHERE singleton = 1").ConfigureAwait(false) ??
                throw new InvalidOperationException("SQLite schema metadata is missing");
            var version = row.GetInt64("version");
            var currentVersion = migrations.Count == 0 ? 0 : migrations[^1].Version;
            if (version > currentVersion)
                throw new InvalidOperationException($"SQLite schema version {version} is newer than supported version {currentVersion}");
            foreach (var migration in migrations)
            {
                if (migration.Version <= version) continue;
                foreach (var statement in migration.Statements) await transaction.ExecAsync(statement).ConfigureAwait(false);
                await transaction.RunAsync($"UPDATE {table} SET version = ? WHERE singleton = 1", migration.Version).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}
