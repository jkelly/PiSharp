// Authored for PiSharp (decision 0007). The tables, columns and indexes follow upstream's SQLite schema
// (packages/durable/src/storage/sqlite/migrations.ts, ported in PiSharp.Durable.Sqlite), adapted to SQL Server:
// - One schema holds many stores. durable_stores names each store and keeps its ID and sequence allocators (SQLite's durable_metadata);
//   every other table leads its key and indexes with store_id.
// - Records are NVARCHAR(MAX) with no ISJSON check: SQL Server's JSON parser refuses text nested deeper than 128 levels, which upstream's
//   json_valid accepts, and only this storage writes the column.
// - Indexed strings (store names, task kinds, request IDs, document kinds and keys) are NVARCHAR(MAX), which cannot be an index key; each is
//   indexed through a persisted SHA-256 of its text, and queries compare both the hash and the text. Text columns use a binary collation, so
//   that comparisons are exact whatever the database's default collation is.
// - Tables carry a durable_ prefix, since the default schema dbo may hold other tables.
using System.Collections.Immutable;

namespace PiSharp.Durable.MSSQL;

/// <summary>One immutable step of the schema history.</summary>
/// <param name="Version">The schema version after this step; versions are contiguous from 1.</param>
/// <param name="Statements">The T-SQL statements, run in order; <c>{schema}</c> stands for the quoted schema name.</param>
public sealed record SqlServerMigration(int Version, ImmutableArray<string> Statements);

/// <summary>The durable SQL Server schema and its migration runner.</summary>
public static class SqlServerMigrations
{
    /// <summary>The schema that holds the durable tables when the options name none.</summary>
    public const string DefaultSchema = "dbo";

    private const string Text = "NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2";
    private const string Name = "NVARCHAR(16) COLLATE Latin1_General_100_BIN2";

    private static string Hash(string column) => $"CAST(HASHBYTES('SHA2_256', {column}) AS BINARY(32)) PERSISTED";

    private static readonly ImmutableArray<string> InitialSchema =
    [
        $$"""
         CREATE TABLE {schema}.durable_stores (
             store_id INT IDENTITY(1, 1) NOT NULL CONSTRAINT durable_stores_pk PRIMARY KEY,
             name {{Text}} NOT NULL,
             name_hash AS {{Hash("name")}},
             next_id BIGINT NOT NULL,
             next_seq BIGINT NOT NULL)
         """,
        "CREATE UNIQUE INDEX durable_stores_by_name ON {schema}.durable_stores (name_hash)",
        $$"""
         CREATE TABLE {schema}.durable_record_ids (
             store_id INT NOT NULL,
             id BIGINT NOT NULL,
             record_type {{Name}} NOT NULL CHECK (record_type IN (N'conversation', N'entry', N'task', N'submission', N'document')),
             CONSTRAINT durable_record_ids_pk PRIMARY KEY (store_id, id))
         """,
        """
        CREATE TABLE {schema}.durable_conversations (
            store_id INT NOT NULL,
            id BIGINT NOT NULL,
            owner_conversation_id BIGINT,
            owner_task_id BIGINT,
            record NVARCHAR(MAX) NOT NULL,
            CONSTRAINT durable_conversations_pk PRIMARY KEY (store_id, id))
        """,
        "CREATE INDEX durable_conversations_by_owner_conversation ON {schema}.durable_conversations (store_id, owner_conversation_id, id)",
        "CREATE INDEX durable_conversations_by_owner_task ON {schema}.durable_conversations (store_id, owner_task_id, id)",
        """
        CREATE TABLE {schema}.durable_entries (
            store_id INT NOT NULL,
            id BIGINT NOT NULL,
            conversation_id BIGINT NOT NULL,
            head BIGINT,
            commit_seq BIGINT NOT NULL,
            record NVARCHAR(MAX) NOT NULL,
            CONSTRAINT durable_entries_pk PRIMARY KEY (store_id, id))
        """,
        "CREATE INDEX durable_entries_by_conversation ON {schema}.durable_entries (store_id, conversation_id, id DESC)",
        "CREATE INDEX durable_entry_heads_by_conversation ON {schema}.durable_entries (store_id, conversation_id, id DESC) WHERE head IS NOT NULL",
        $$"""
         CREATE TABLE {schema}.durable_tasks (
             store_id INT NOT NULL,
             id BIGINT NOT NULL,
             conversation_id BIGINT NOT NULL,
             kind {{Text}} NOT NULL,
             kind_hash AS {{Hash("kind")}},
             status {{Name}} NOT NULL CHECK (status IN (N'pending', N'running', N'waiting', N'completing', N'terminal')),
             abort_requested BIT NOT NULL,
             background BIT NOT NULL,
             record NVARCHAR(MAX) NOT NULL,
             CONSTRAINT durable_tasks_pk PRIMARY KEY (store_id, id))
         """,
        "CREATE INDEX durable_tasks_by_status ON {schema}.durable_tasks (store_id, status, id)",
        "CREATE INDEX durable_tasks_by_conversation ON {schema}.durable_tasks (store_id, conversation_id, id)",
        "CREATE INDEX durable_tasks_by_kind ON {schema}.durable_tasks (store_id, kind_hash, id)",
        "CREATE INDEX durable_tasks_by_abort_requested ON {schema}.durable_tasks (store_id, abort_requested, id)",
        "CREATE INDEX durable_tasks_by_background ON {schema}.durable_tasks (store_id, background, id)",
        $$"""
         CREATE TABLE {schema}.durable_submissions (
             store_id INT NOT NULL,
             id BIGINT NOT NULL,
             conversation_id BIGINT NOT NULL,
             request_id {{Text}},
             request_hash AS {{Hash("request_id")}},
             status {{Name}} NOT NULL CHECK (status IN (N'queued', N'placed', N'done', N'unanswered')),
             record NVARCHAR(MAX) NOT NULL,
             CONSTRAINT durable_submissions_pk PRIMARY KEY (store_id, id))
         """,
        "CREATE INDEX durable_submissions_by_request ON {schema}.durable_submissions (store_id, conversation_id, request_hash)",
        "CREATE INDEX durable_submissions_by_conversation ON {schema}.durable_submissions (store_id, conversation_id, id)",
        "CREATE INDEX durable_submissions_by_status ON {schema}.durable_submissions (store_id, status, id)",
        $$"""
         CREATE TABLE {schema}.durable_documents (
             store_id INT NOT NULL,
             id BIGINT NOT NULL,
             kind {{Text}} NOT NULL,
             kind_hash AS {{Hash("kind")}},
             family BIT NOT NULL,
             key_value {{Text}} NOT NULL,
             key_hash AS {{Hash("key_value")}},
             scope_kind {{Name}} NOT NULL CHECK (scope_kind IN (N'session', N'conversation', N'task')),
             owner_id BIGINT NOT NULL,
             created_at BIGINT NOT NULL,
             retired_at BIGINT,
             record NVARCHAR(MAX) NOT NULL,
             CONSTRAINT durable_documents_pk PRIMARY KEY (store_id, id))
         """,
        """
        CREATE INDEX durable_documents_by_address
            ON {schema}.durable_documents (store_id, kind_hash, scope_kind, owner_id, family, key_hash, created_at DESC, retired_at)
        """,
        "CREATE INDEX durable_documents_by_scope ON {schema}.durable_documents (store_id, scope_kind, owner_id, id)",
        "CREATE INDEX durable_documents_by_scope_kind ON {schema}.durable_documents (store_id, scope_kind, owner_id, kind_hash, id)",
        $$"""
         CREATE TABLE {schema}.durable_document_revisions (
             store_id INT NOT NULL,
             document_id BIGINT NOT NULL,
             seq BIGINT NOT NULL,
             kind {{Name}} NOT NULL CHECK (kind IN (N'base', N'delta')),
             version BIGINT NOT NULL,
             content NVARCHAR(MAX) NOT NULL,
             CONSTRAINT durable_document_revisions_pk PRIMARY KEY (store_id, document_id, seq))
         """,
        "CREATE INDEX durable_document_revisions_by_kind ON {schema}.durable_document_revisions (store_id, document_id, kind, seq DESC)",
    ];

    /// <summary>The <c>durable_schema</c> table that records the applied version; created when missing.</summary>
    private const string SchemaTable = """
        IF OBJECT_ID(QUOTENAME(@p0) + N'.durable_schema', N'U') IS NULL
        CREATE TABLE {schema}.durable_schema (
            singleton INT NOT NULL CONSTRAINT durable_schema_pk PRIMARY KEY CHECK (singleton = 1),
            version INT NOT NULL CHECK (version >= 0))
        """;

    /// <summary>Immutable, ordered schema history. Append new migrations after the initial schema ships.</summary>
    public static ImmutableArray<SqlServerMigration> Migrations { get; } = [new(1, InitialSchema)];

    /// <summary>The newest schema version.</summary>
    public static int CurrentSchemaVersion { get; } = Migrations.IsEmpty ? 0 : Migrations[^1].Version;

    /// <summary>The schema name as a bracket-delimited identifier, as T-SQL <c>QUOTENAME</c> writes it.</summary>
    internal static string Quote(string schema) => "[" + schema.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>A statement with <c>{schema}</c> replaced by the quoted schema name.</summary>
    internal static string Render(string statement, string quotedSchema) => statement.Replace("{schema}", quotedSchema, StringComparison.Ordinal);

    /// <summary>Applies all pending schema migrations to <paramref name="schema"/> atomically, creating the schema when it is missing, and
    /// returns the schema's <c>SCHEMA_ID</c>. A transaction-owned application lock serializes the database's migrations;
    /// <paramref name="lockTimeoutMs"/> bounds the wait.</summary>
    internal static async Task<int> ApplyAsync(SqlServerDatabase database, string schema, int lockTimeoutMs, IReadOnlyList<SqlServerMigration>? migrations = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        migrations ??= Migrations;
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index]?.Version != index + 1)
                throw new InvalidOperationException("Durable SQL Server migrations must have contiguous versions starting at 1");
        }
        var quoted = Quote(schema);
        return await database.TransactionAsync(async transaction =>
        {
            await SqlServerLocks.AcquireAsync(transaction, SqlServerLocks.MigrationResource, "Transaction", lockTimeoutMs,
                "Durable SQL Server schemas of this database are being migrated by another connection").ConfigureAwait(false);
            await transaction.RunAsync("""
                DECLARE @create NVARCHAR(300) = N'CREATE SCHEMA ' + QUOTENAME(@p0);
                IF SCHEMA_ID(@p0) IS NULL EXEC (@create);
                """, schema).ConfigureAwait(false);
            await transaction.RunAsync(Render(SchemaTable, quoted), schema).ConfigureAwait(false);
            await transaction.RunAsync(Render("""
                IF NOT EXISTS (SELECT 1 FROM {schema}.durable_schema WITH (UPDLOCK, HOLDLOCK) WHERE singleton = 1)
                INSERT INTO {schema}.durable_schema (singleton, version) VALUES (1, 0)
                """, quoted)).ConfigureAwait(false);
            var row = await transaction.GetAsync(Render("SELECT version FROM {schema}.durable_schema WHERE singleton = 1", quoted)).ConfigureAwait(false) ??
                throw new InvalidOperationException("Durable SQL Server schema metadata is missing");
            var version = row.GetInt64("version");
            var currentVersion = migrations.Count == 0 ? 0 : migrations[^1].Version;
            if (version > currentVersion)
                throw new InvalidOperationException($"Durable SQL Server schema version {version} is newer than supported version {currentVersion}");
            foreach (var migration in migrations)
            {
                if (migration.Version <= version) continue;
                foreach (var statement in migration.Statements) await transaction.RunAsync(Render(statement, quoted)).ConfigureAwait(false);
                await transaction.RunAsync(Render("UPDATE {schema}.durable_schema SET version = @p0 WHERE singleton = 1", quoted), migration.Version)
                    .ConfigureAwait(false);
            }
            var id = await transaction.GetAsync("SELECT SCHEMA_ID(@p0) AS id", schema).ConfigureAwait(false);
            return checked((int)id!.GetInt64("id"));
        }).ConfigureAwait(false);
    }
}
