// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/storage/sqlite/migrations.ts
//
// The schema statements are upstream's text verbatim, whitespace included, so that sqlite_schema matches a database Pi created; a test
// pins their SHA-256 as computed from upstream's module. next_id is TEXT because node:sqlite rejects INTEGER results outside JavaScript's
// safe integer range; it stays TEXT here so that either tool opens the other's files.
using System.Collections.Immutable;

namespace PiSharp.Durable.Sqlite;

/// <summary>One immutable step of the schema history (upstream <c>SqliteMigration</c>).</summary>
/// <param name="Version">The schema version after this step; versions are contiguous from 1.</param>
/// <param name="Statements">The SQL statements, run in order.</param>
public sealed record SqliteMigration(int Version, ImmutableArray<string> Statements);

/// <summary>The durable SQLite schema and its migration runner.</summary>
public static class SqliteMigrations
{
    // Upstream INITIAL_SCHEMA.
    private static readonly ImmutableArray<string> InitialSchema =
    [
        "CREATE TABLE durable_metadata (\n\t\tsingleton INTEGER PRIMARY KEY CHECK (singleton = 1),\n\t\tnext_id TEXT NOT NULL,\n\t\tnext_seq INTEGER NOT NULL\n\t) STRICT",
        "INSERT INTO durable_metadata (singleton, next_id, next_seq) VALUES (1, '2', 1)",
        "CREATE TABLE record_ids (\n\t\tid INTEGER PRIMARY KEY,\n\t\trecord_type TEXT NOT NULL CHECK (record_type IN ('conversation', 'entry', 'task', 'submission', 'document'))\n\t) STRICT",
        "CREATE TABLE conversations (\n\t\tid INTEGER PRIMARY KEY,\n\t\towner_conversation_id INTEGER,\n\t\towner_task_id INTEGER,\n\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t) STRICT",
        "CREATE INDEX conversations_by_owner_conversation ON conversations (owner_conversation_id, id)",
        "CREATE INDEX conversations_by_owner_task ON conversations (owner_task_id, id)",
        "CREATE TABLE entries (\n\t\tid INTEGER PRIMARY KEY,\n\t\tconversation_id INTEGER NOT NULL,\n\t\thead INTEGER,\n\t\tcommit_seq INTEGER NOT NULL,\n\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t) STRICT",
        "CREATE INDEX entries_by_conversation ON entries (conversation_id, id DESC)",
        "CREATE INDEX entry_heads_by_conversation ON entries (conversation_id, id DESC) WHERE head IS NOT NULL",
        "CREATE TABLE tasks (\n\t\tid INTEGER PRIMARY KEY,\n\t\tconversation_id INTEGER NOT NULL,\n\t\tkind TEXT NOT NULL,\n\t\tstatus TEXT NOT NULL CHECK (status IN ('pending', 'running', 'waiting', 'completing', 'terminal')),\n\t\tabort_requested INTEGER NOT NULL CHECK (abort_requested IN (0, 1)),\n\t\tbackground INTEGER NOT NULL CHECK (background IN (0, 1)),\n\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t) STRICT",
        "CREATE INDEX tasks_by_status ON tasks (status, id)",
        "CREATE INDEX tasks_by_conversation ON tasks (conversation_id, id)",
        "CREATE INDEX tasks_by_kind ON tasks (kind, id)",
        "CREATE INDEX tasks_by_abort_requested ON tasks (abort_requested, id)",
        "CREATE INDEX tasks_by_background ON tasks (background, id)",
        "CREATE TABLE submissions (\n\t\tid INTEGER PRIMARY KEY,\n\t\tconversation_id INTEGER NOT NULL,\n\t\trequest_id TEXT,\n\t\tstatus TEXT NOT NULL CHECK (status IN ('queued', 'placed', 'done', 'unanswered')),\n\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t) STRICT",
        "CREATE INDEX submissions_by_request ON submissions (conversation_id, request_id)",
        "CREATE INDEX submissions_by_conversation ON submissions (conversation_id, id)",
        "CREATE INDEX submissions_by_status ON submissions (status, id)",
        "CREATE TABLE documents (\n\t\tid INTEGER PRIMARY KEY,\n\t\tkind TEXT NOT NULL,\n\t\tfamily INTEGER NOT NULL CHECK (family IN (0, 1)),\n\t\tkey_value TEXT NOT NULL,\n\t\tscope_kind TEXT NOT NULL CHECK (scope_kind IN ('session', 'conversation', 'task')),\n\t\towner_id INTEGER NOT NULL,\n\t\tcreated_at INTEGER NOT NULL,\n\t\tretired_at INTEGER,\n\t\trecord TEXT NOT NULL CHECK (json_valid(record))\n\t) STRICT",
        "CREATE INDEX documents_by_address\n\t\tON documents (kind, scope_kind, owner_id, family, key_value, created_at DESC, retired_at)",
        "CREATE INDEX documents_by_scope ON documents (scope_kind, owner_id, id)",
        "CREATE INDEX documents_by_scope_kind ON documents (scope_kind, owner_id, kind, id)",
        "CREATE TABLE document_revisions (\n\t\tdocument_id INTEGER NOT NULL,\n\t\tseq INTEGER NOT NULL,\n\t\tkind TEXT NOT NULL CHECK (kind IN ('base', 'delta')),\n\t\tversion INTEGER NOT NULL,\n\t\tcontent TEXT NOT NULL CHECK (json_valid(content)),\n\t\tPRIMARY KEY (document_id, seq)\n\t) STRICT",
        "CREATE INDEX document_revisions_by_kind ON document_revisions (document_id, kind, seq DESC)",
    ];

    /// <summary>The <c>durable_schema</c> table that records the applied version (upstream's text inside <c>applySqliteMigrations</c>).</summary>
    internal const string SchemaTable =
        "CREATE TABLE IF NOT EXISTS durable_schema (\n\t\t\tsingleton INTEGER PRIMARY KEY CHECK (singleton = 1),\n\t\t\tversion INTEGER NOT NULL CHECK (version >= 0)\n\t\t) STRICT";

    /// <summary>Immutable, ordered schema history (upstream <c>SQLITE_MIGRATIONS</c>). Append new migrations after the initial schema
    /// ships.</summary>
    public static ImmutableArray<SqliteMigration> Migrations { get; } = [new(1, InitialSchema)];

    /// <summary>The newest schema version (upstream <c>CURRENT_SQLITE_SCHEMA_VERSION</c>).</summary>
    public static int CurrentSchemaVersion { get; } = Migrations.IsEmpty ? 0 : Migrations[^1].Version;

    /// <summary>Applies all pending schema migrations atomically (upstream <c>applySqliteMigrations</c>).</summary>
    public static async Task ApplyAsync(ISqliteDatabase database, IReadOnlyList<SqliteMigration>? migrations = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        migrations ??= Migrations;
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index]?.Version != index + 1)
                throw new InvalidOperationException("Durable SQLite migrations must have contiguous versions starting at 1");
        }

        await database.TransactionAsync(async transaction =>
        {
            await transaction.ExecAsync(SchemaTable).ConfigureAwait(false);
            await transaction.RunAsync("INSERT OR IGNORE INTO durable_schema (singleton, version) VALUES (1, 0)").ConfigureAwait(false);
            var row = await transaction.GetAsync("SELECT version FROM durable_schema WHERE singleton = 1").ConfigureAwait(false) ??
                throw new InvalidOperationException("Durable SQLite schema metadata is missing");
            var version = row.GetInt64("version");
            var currentVersion = migrations.Count == 0 ? 0 : migrations[^1].Version;
            if (version > currentVersion)
                throw new InvalidOperationException($"Durable SQLite schema version {version} is newer than supported version {currentVersion}");
            foreach (var migration in migrations)
            {
                if (migration.Version <= version) continue;
                foreach (var statement in migration.Statements) await transaction.ExecAsync(statement).ConfigureAwait(false);
                await transaction.RunAsync("UPDATE durable_schema SET version = ? WHERE singleton = 1", migration.Version).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}
