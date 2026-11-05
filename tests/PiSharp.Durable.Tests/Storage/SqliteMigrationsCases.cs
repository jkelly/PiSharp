// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/sqlite-migrations.test.ts. Pi v1.5.0
// (f9a26365568127461c47a07a37dcb42c140a3ac5): its version 1 to 2 migration case, and packages/chord/test/sqlite/migrations.test.ts (Chord's
// generic runner, as chord.sqlite.migrations.*, with rolls-initial-bootstrap-and-every-pending-migration-back-together, which v1.5.0 moved
// there). The retry case commits through v1.5.0's commitWrites (CommitWritesAsync, area storage-batches).
// The storage.sqlite.migrations.*-schema-* and contiguous-version cases are authored: the schema text must stay upstream's verbatim (the
// hashes were computed from upstream v1.5.0's createSqliteMigrations and Chord's applySqliteMigrations source text, with the table and
// index names of schema.ts substituted; no upstream code ran), a newly created file must hold exactly upstream's tables and indexes, and a
// prefix must rename every one of them. A migrated database cannot be shown byte-equal to one upstream migrated without a recapture
// (decision 0013): the expectations are upstream's own test assertions.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Testing;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StorageSqliteMigrationsCases()
    {
        yield return Case("storage.sqlite.migrations.initial-schema-is-upstream-text-verbatim", () =>
        {
            Equal(2, SqliteMigrations.Migrations.Length, "migrations");
            Equal(2, SqliteMigrations.CurrentSchemaVersion, "current version");
            Equal("47381c65d2fd1c16a62d2fe903e2c27685ab3254324c656c5630dae48dc8d757",
                SchemaHash([SqliteMigrations.SchemaTable("durable_schema"), .. SqliteMigrations.Migrations[0].Statements]), "SHA-256 of the version 1 statements");
            Equal("88dfda2ea28e89c53290a21ec93aee7125f876e3cc059dc352986fb29e27f797",
                SchemaHash(SqliteMigrations.Migrations[1].Statements), "SHA-256 of the version 2 statements");
        });

        yield return Case("storage.sqlite.migrations.prefixed-schema-is-upstream-text-verbatim", () =>
        {
            var migrations = SqliteMigrations.Create(SqliteSchema.Create("pi_"));
            Equal("238355afc9dd854798fcc4c58608a14a685f7ab6fecc7d3b03d3fc3d66a99709",
                SchemaHash([SqliteMigrations.SchemaTable("pi_durable_schema"), .. migrations[0].Statements]), "SHA-256 of the version 1 statements");
            Equal("d37218ab59ba1bf26357a59ce4c8aea65562327fce476a0b66bfeba3f6cb1c45", SchemaHash(migrations[1].Statements),
                "SHA-256 of the version 2 statements");
            Check(SqliteMigrations.Create(SqliteSchema.Default).SequenceEqual(SqliteMigrations.Migrations, MigrationComparer.Instance),
                "the default schema renders SqliteMigrations.Migrations");
        });

        yield return Case("storage.sqlite.migrations.initial-schema-creates-upstream-tables-and-indexes", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await storage.CloseAsync(SqliteContext);
            using var database = RawSqlite(path, readOnly: true);
            Equal(SchemaObjects(""), string.Join(",", SchemaNames(database)), "schema objects");
            Equal(9L, RawQuery(database, "SELECT count(*) AS value FROM pragma_table_list WHERE strict = 1 AND schema = 'main'")[0].GetInt64("value"),
                "STRICT tables");
            Equal("wal", RawQuery(database, "PRAGMA journal_mode")[0].GetString("journal_mode"), "journal mode");
        });

        yield return Case("storage.sqlite.migrations.rejects-migrations-without-contiguous-versions", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                Equal("SQLite migrations must have contiguous versions starting at 1", (await ThrowsAsync<InvalidOperationException>(
                    () => SqliteMigrations.ApplyAsync(database, [new SqliteMigration(2, ["SELECT 1"])]), "gap")).Message, "message");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("storage.sqlite.migrations.creates-the-current-schema-and-can-be-applied-repeatedly", async () =>
        {
            await using var temp = new SqliteTemp();
            var database = await SqliteConnectionDatabase.OpenAsync(temp.NewPath());
            try
            {
                await SqliteMigrations.ApplyAsync(database);
                await SqliteMigrations.ApplyAsync(database);
                Equal($"{{\"version\":{SqliteMigrations.CurrentSchemaVersion}}}",
                    (await database.GetAsync("SELECT version FROM durable_schema WHERE singleton = 1"))?.ToString(), "schema version");
                Equal("{\"next_id\":\"2\",\"next_seq\":1}",
                    (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata WHERE singleton = 1"))?.ToString(), "metadata");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("storage.sqlite.migrations.migrates-a-version-1-database-to-store-sleeping-tasks-keeping-its-tasks-and-indexes", async () =>
        {
            await using var temp = new SqliteTemp();
            var path = temp.NewPath();
            var database = await SqliteConnectionDatabase.OpenAsync(path);
            await SqliteMigrations.ApplyAsync(database, [SqliteMigrations.Migrations[0]]);
            const string waiting = "{\"id\":5,\"conversationId\":1,\"kind\":\"test.task\",\"version\":1,\"input\":null,\"background\":false," +
                "\"abortRequested\":false,\"state\":{\"status\":\"waiting\",\"checkpoint\":{\"phase\":\"run\"},\"on\":[],\"policy\":\"allSettled\"}}";
            await database.RunAsync(
                "INSERT INTO tasks (id, conversation_id, kind, status, abort_requested, background, record) VALUES (5, 1, 'test.task', 'waiting', 0, 0, ?)",
                waiting);
            await ThrowsAsync<SqliteException>(() => database.RunAsync(
                "INSERT INTO tasks (id, conversation_id, kind, status, abort_requested, background, record) VALUES (6, 1, 'test.task', 'sleeping', 0, 0, '{}')"),
                "a sleeping task in a version 1 database");
            await database.CloseAsync();

            var storage = await SqliteStorage.OpenAsync(path);
            await storage.CloseAsync(SqliteContext);
            var migrated = await SqliteConnectionDatabase.OpenAsync(path);
            try
            {
                Equal("{\"version\":2}", (await migrated.GetAsync("SELECT version FROM durable_schema WHERE singleton = 1"))?.ToString(), "version");
                var task = (await migrated.GetAsync("SELECT status, record FROM tasks WHERE id = 5"))!;
                Equal("waiting", task.GetString("status"), "kept status");
                Equal(waiting, task.GetString("record"), "kept record");
                await migrated.RunAsync(
                    "INSERT INTO tasks (id, conversation_id, kind, status, abort_requested, background, record) VALUES (6, 1, 'test.task', 'sleeping', 0, 0, '{}')");
                Equal("tasks_by_abort_requested,tasks_by_background,tasks_by_conversation,tasks_by_kind,tasks_by_status", string.Join(",",
                    (await migrated.AllAsync("SELECT name FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'tasks' ORDER BY name"))
                    .Select(row => row.GetString("name"))), "task indexes");
                Equal("{\"count\":0}", (await migrated.GetAsync("SELECT count(*) AS count FROM sqlite_schema WHERE name = 'tasks_v2'"))?.ToString(),
                    "no rebuilt table left");
            }
            finally
            {
                await migrated.CloseAsync();
            }
        });

        yield return Case("storage.sqlite.migrations.rejects-a-database-newer-than-the-portable-core", async () =>
        {
            await using var temp = new SqliteTemp();
            var path = temp.NewPath();
            var database = await SqliteConnectionDatabase.OpenAsync(path);
            await SqliteMigrations.ApplyAsync(database);
            await database.RunAsync("UPDATE durable_schema SET version = ? WHERE singleton = 1", SqliteMigrations.CurrentSchemaVersion + 1);
            await database.CloseAsync();

            var error = await ThrowsAsync<InvalidOperationException>(() => SqliteStorage.OpenAsync(path), "open");
            Check(error.Message.Contains("is newer than supported version", StringComparison.Ordinal), "message: " + error.Message);
        });

        yield return Case("storage.sqlite.migrations.rolls-initial-bootstrap-and-every-pending-migration-back-together", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                SqliteMigration[] failed =
                [
                    new(1, ["CREATE TABLE migration_first (value TEXT) STRICT", "INSERT INTO migration_first (value) VALUES ('retained')"]),
                    new(2, ["CREATE TABLE migration_second (value TEXT) STRICT", "THIS IS NOT SQL"]),
                ];
                await ThrowsAsync<SqliteException>(() => SqliteMigrations.ApplyAsync(database, failed, "app_schema"), "failed migrations");
                Equal("{\"count\":0}", (await database.GetAsync(
                    "SELECT count(*) AS count FROM sqlite_schema WHERE name IN ('app_schema', 'migration_first', 'migration_second')"))?.ToString(),
                    "no schema objects");

                await SqliteMigrations.ApplyAsync(database, [failed[0], new(2, ["CREATE TABLE migration_second (value TEXT) STRICT"])], "app_schema");
                Equal("{\"version\":2}", (await database.GetAsync("SELECT version FROM app_schema WHERE singleton = 1"))?.ToString(), "version");
                Equal("{\"value\":\"retained\"}", (await database.GetAsync("SELECT value FROM migration_first"))?.ToString(), "first migration row");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("storage.sqlite.migrations.rolls-a-failed-migration-back-and-preserves-stored-data-for-a-successful-retry", async () =>
        {
            await using var temp = new SqliteTemp();
            var path = temp.NewPath();
            var storage = await SqliteStorage.OpenAsync(path);
            await storage.CommitWritesAsync([new ConversationWrite(new(ConversationId.Root)), new EntryWrite(new()
            {
                Id = Ids.FromNumber<EntryId>(2), ConversationId = ConversationId.Root, Kind = "retained", Data = J("{\"retained\":true}"),
            })], SqliteContext);
            await storage.CloseAsync(SqliteContext);

            var database = await SqliteConnectionDatabase.OpenAsync(path);
            try
            {
                var nextVersion = SqliteMigrations.CurrentSchemaVersion + 1;
                SqliteMigration[] failedMigrations =
                    [.. SqliteMigrations.Migrations, new(nextVersion, ["CREATE TABLE migration_probe (value TEXT) STRICT", "THIS IS NOT SQL"])];
                await ThrowsAsync<SqliteException>(() => SqliteMigrations.ApplyAsync(database, failedMigrations), "failed migration");
                Equal($"{{\"version\":{SqliteMigrations.CurrentSchemaVersion}}}",
                    (await database.GetAsync("SELECT version FROM durable_schema WHERE singleton = 1"))?.ToString(), "version after the failure");
                Equal("{\"count\":0}", (await database.GetAsync(
                    "SELECT count(*) AS count FROM sqlite_schema WHERE type = 'table' AND name = 'migration_probe'"))?.ToString(), "no probe table");

                SqliteMigration[] successfulMigrations =
                    [.. SqliteMigrations.Migrations, new(nextVersion, ["CREATE TABLE migration_probe (value TEXT) STRICT"])];
                await SqliteMigrations.ApplyAsync(database, successfulMigrations);
                Equal($"{{\"version\":{nextVersion}}}", (await database.GetAsync("SELECT version FROM durable_schema WHERE singleton = 1"))?.ToString(),
                    "version after the retry");
                var entry = (await database.GetAsync("SELECT record, commit_seq FROM entries WHERE id = 2"))!;
                Equal("{\"kind\":\"retained\",\"data\":{\"retained\":true},\"id\":2,\"conversationId\":1}", entry.GetString("record"), "entry record JSON");
                Equal(1L, entry.GetInt64("commit_seq"), "entry commit sequence");
                Equal("{\"next_id\":\"3\",\"next_seq\":2}",
                    (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata WHERE singleton = 1"))?.ToString(), "metadata");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("chord.sqlite.migrations.tracks-independent-histories-and-skips-migrations-already-applied", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                SqliteMigration[] first = [new(1, ["CREATE TABLE notes (body TEXT)", "INSERT INTO notes VALUES ('kept')"])];
                await SqliteMigrations.ApplyAsync(database, first, "notes_schema");
                await SqliteMigrations.ApplyAsync(database, first, "notes_schema");
                await SqliteMigrations.ApplyAsync(database, [], "other_schema");
                await SqliteMigrations.ApplyAsync(database, [.. first, new(2, ["ALTER TABLE notes ADD COLUMN title TEXT"])], "notes_schema");
                Equal("{\"body\":\"kept\",\"title\":null}", Rows(await database.AllAsync("SELECT body, title FROM notes")), "notes");
                Equal("{\"version\":2}", (await database.GetAsync("SELECT version FROM notes_schema"))?.ToString(), "notes version");
                Equal("{\"version\":0}", (await database.GetAsync("SELECT version FROM other_schema"))?.ToString(), "other version");
                Equal("SQLite schema version 2 is newer than supported version 1", (await ThrowsAsync<InvalidOperationException>(
                    () => SqliteMigrations.ApplyAsync(database, first, "notes_schema"), "older history")).Message, "message");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("chord.sqlite.migrations.rejects-invalid-migration-versions-before-touching-the-database", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                foreach (var versions in new[] { new[] { 0 }, [2], [1, 3], [1, 1] })
                {
                    var error = await ThrowsAsync<InvalidOperationException>(() => SqliteMigrations.ApplyAsync(database,
                        [.. versions.Select(version => new SqliteMigration(version, []))], "app_schema"), $"versions {string.Join(",", versions)}");
                    Check(error.Message.Contains("contiguous versions starting at 1", StringComparison.Ordinal), "message: " + error.Message);
                }
                Equal("", Rows(await database.AllAsync("SELECT name FROM sqlite_schema")), "schema objects");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("chord.sqlite.migrations.validates-prefixes-and-migration-table-identifiers-before-executing-sql", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                // Authored addition: "pi_\n", which a .NET regular expression's $ would accept before the final newline.
                foreach (var prefix in new[] { "", "1bad", "bad-prefix", "bad; DROP TABLE notes", "sqlite_app", "SQLITE_app", "pi_\n" })
                {
                    Throws<ArgumentException>(() => SqliteSchemaPrefix.Validate(prefix), $"prefix {JsonQuote(prefix)}");
                    await ThrowsAsync<ArgumentException>(() => SqliteMigrations.ApplyAsync(database, [], prefix), $"table {JsonQuote(prefix)}");
                }
                foreach (var prefix in new[] { "pi_", "_private", "app2" }) SqliteSchemaPrefix.Validate(prefix);
                Equal("", Rows(await database.AllAsync("SELECT name FROM sqlite_schema")), "schema objects");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("chord.sqlite.migrations.names-the-refused-prefix-and-a-reserved-one", () =>
        {
            // Authored: upstream's messages, JSON-quoting the prefix.
            Equal("Invalid SQLite schema prefix \"bad-prefix\"", Throws<ArgumentException>(() => SqliteSchemaPrefix.Validate("bad-prefix"), "invalid").Message,
                "invalid message");
            Equal("SQLite schema prefix \"SQLITE_app\" is reserved", Throws<ArgumentException>(() => SqliteSchemaPrefix.Validate("SQLITE_app"), "reserved").Message,
                "reserved message");
        });
    }

    private static string SchemaHash(IEnumerable<string> statements) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n;\n", statements))));

    /// <summary>Upstream's schema objects (sqlite_schema rows other than automatic indexes) under <paramref name="prefix"/>, as type and name
    /// in that order.</summary>
    private static string SchemaObjects(string prefix) => string.Join(",", new[]
    {
        "conversations_by_owner_conversation", "conversations_by_owner_task", "document_revisions_by_kind", "documents_by_address", "documents_by_scope",
        "documents_by_scope_kind", "entries_by_conversation", "entry_heads_by_conversation", "submissions_by_conversation", "submissions_by_request",
        "submissions_by_status", "tasks_by_abort_requested", "tasks_by_background", "tasks_by_conversation", "tasks_by_kind", "tasks_by_status",
    }.Select(name => "index " + prefix + name).Concat(new[]
    {
        "conversations", "document_revisions", "documents", "durable_metadata", "durable_schema", "entries", "record_ids", "submissions", "tasks",
    }.Select(name => "table " + prefix + name)));

    private static IEnumerable<string> SchemaNames(SqliteConnection database) =>
        RawQuery(database, "SELECT type, name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_autoindex%' ORDER BY type, name")
            .Select(row => row.GetString("type") + " " + row.GetString("name"));

    private static string JsonQuote(string text) => PiSharp.Durable.Chord.JsonValue.String(text).ToJson();

    private sealed class MigrationComparer : IEqualityComparer<SqliteMigration>
    {
        public static readonly MigrationComparer Instance = new();
        public bool Equals(SqliteMigration? x, SqliteMigration? y) => x is not null && y is not null && x.Version == y.Version &&
            x.Statements.SequenceEqual(y.Statements, StringComparer.Ordinal);
        public int GetHashCode(SqliteMigration migration) => migration.Version;
    }
}
