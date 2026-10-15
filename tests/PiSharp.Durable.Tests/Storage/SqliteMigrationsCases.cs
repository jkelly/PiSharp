// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/sqlite-migrations.test.ts. The two
// storage.sqlite.migrations.initial-schema-* and contiguous-version cases are authored: the schema text must stay upstream's verbatim
// (the hash was computed from upstream's SQLITE_MIGRATIONS and applySqliteMigrations at abe508e), and a newly created file must hold
// exactly upstream's tables and indexes.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Sqlite;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StorageSqliteMigrationsCases()
    {
        yield return Case("storage.sqlite.migrations.initial-schema-is-upstream-text-verbatim", () =>
        {
            Equal(1, SqliteMigrations.Migrations.Length, "migrations");
            Equal(1, SqliteMigrations.CurrentSchemaVersion, "current version");
            var text = string.Join("\n;\n", new[] { SqliteMigrations.SchemaTable }.Concat(SqliteMigrations.Migrations[0].Statements));
            Equal("3a8f5a82d0b23ac308537567fd04b5614526caba2d2fb4a741fd7a019c173d14",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), "SHA-256 of the schema statements");
        });

        yield return Case("storage.sqlite.migrations.initial-schema-creates-upstream-tables-and-indexes", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await storage.CloseAsync(SqliteContext);
            using var database = RawSqlite(path, readOnly: true);
            var names = RawQuery(database, "SELECT type, name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_autoindex%' ORDER BY type, name")
                .Select(row => row.GetString("type") + " " + row.GetString("name"));
            Equal("index conversations_by_owner_conversation,index conversations_by_owner_task,index document_revisions_by_kind," +
                "index documents_by_address,index documents_by_scope,index documents_by_scope_kind,index entries_by_conversation," +
                "index entry_heads_by_conversation,index submissions_by_conversation,index submissions_by_request,index submissions_by_status," +
                "index tasks_by_abort_requested,index tasks_by_background,index tasks_by_conversation,index tasks_by_kind,index tasks_by_status," +
                "table conversations,table document_revisions,table documents,table durable_metadata,table durable_schema,table entries," +
                "table record_ids,table submissions,table tasks", string.Join(",", names), "schema objects");
            Equal(9L, RawQuery(database, "SELECT count(*) AS value FROM pragma_table_list WHERE strict = 1 AND schema = 'main'")[0].GetInt64("value"),
                "STRICT tables");
            Equal("wal", RawQuery(database, "PRAGMA journal_mode")[0].GetString("journal_mode"), "journal mode");
        });

        yield return Case("storage.sqlite.migrations.rejects-migrations-without-contiguous-versions", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                Equal("Durable SQLite migrations must have contiguous versions starting at 1", (await ThrowsAsync<InvalidOperationException>(
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
            await using var temp = new SqliteTemp();
            var database = await SqliteConnectionDatabase.OpenAsync(temp.NewPath());
            try
            {
                SqliteMigration[] failed =
                [
                    new(1, ["CREATE TABLE migration_first (value TEXT) STRICT", "INSERT INTO migration_first (value) VALUES ('retained')"]),
                    new(2, ["CREATE TABLE migration_second (value TEXT) STRICT", "THIS IS NOT SQL"]),
                ];
                await ThrowsAsync<SqliteException>(() => SqliteMigrations.ApplyAsync(database, failed), "failed migrations");
                Equal("{\"count\":0}", (await database.GetAsync(
                    "SELECT count(*) AS count FROM sqlite_schema WHERE name IN ('durable_schema', 'migration_first', 'migration_second')"))?.ToString(),
                    "no schema objects");

                await SqliteMigrations.ApplyAsync(database, [failed[0], new(2, ["CREATE TABLE migration_second (value TEXT) STRICT"])]);
                Equal("{\"version\":2}", (await database.GetAsync("SELECT version FROM durable_schema WHERE singleton = 1"))?.ToString(), "version");
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
            await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root)), new EntryWrite(new()
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
    }
}
