// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/sqlite-storage.test.ts. The storage conformance kit runs
// against SqliteStorage directly (storage.sqlite.conformance.<case>) and through a storage that closes and reopens the file after every
// commit (storage.sqlite.reopen.conformance.<case>), as upstream's ReopeningStorage does. Upstream inspects the file with a second
// node:sqlite connection; these cases use a second Microsoft.Data.Sqlite connection with Pooling=False. Adapted:
// - rolls-sql-rows-and-sequence-allocation-back-as-one-transaction: a circular input cannot exist in an immutable record, so a database
//   wrapper fails the task insert after the entry insert instead; the rollback assertions are upstream's.
// - replays-detached-root-replacements-...: returned values are immutable, so instead of mutating the first read the case checks that a
//   second read is equal.
// storage.sqlite.native.* is authored: the bundled e_sqlite3 library is loaded, is at least 3.37.0 (STRICT tables) and has JSON functions.
// Pi v1.5.0 (f9a26365568127461c47a07a37dcb42c140a3ac5, area sqlite) adds the table prefix: namespaces-tables-and-indexes-with-a-prefix-
// across-reopen and rejects-an-invalid-prefix-before-creating-the-database are ports of its sqlite-storage.test.ts cases. Upstream tests the
// prefix over an existing database only on its Cloudflare Durable Object adapter (sqlite-cloudflare.test.ts, out of scope); its four
// cases and its prefixed conformance run are adapted as storage.sqlite.prefix.* and storage.sqlite.prefixed.conformance.<case> over
// SqliteStorage.OpenAsync(ISqliteDatabase, options) on an in-memory database. Authored there: the prefixed conformance database first
// holds a host table under every unprefixed name, with a column no Durable query reads, so a statement that missed the prefix fails, and
// the rejection case also checks that the refused database is closed.
// Pi v1.5.0 (area storage-batches): every case writes through CommitWritesAsync, and the "SqliteStorage batches" group is
// storage.sqlite.batches.*, one case per failure kind, as upstream's loops make. A check read fails through a wrapper of the transaction
// handle, as upstream's faulty SqliteDatabase does. .NET has no rejection with a non-Error value, so the failure kinds map to what a .NET
// driver can do: an Error rejection is a faulted task; a synchronous throw is kept; a primitive rejection is a faulted task whose exception
// is the type Durable's own checks throw (InvalidOperationException), so only the failed call, not the type, tells it from a refusal; an
// undefined rejection is a canceled task, which carries no exception of its own.
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Testing;

internal static partial class Program
{
    private static readonly DurableContext SqliteContext = DurableContext.Background;

    private static IEnumerable<(string, Func<Task>)> StorageSqliteCases()
    {
        foreach (var testCase in StorageConformance.Create(async use =>
        {
            await using var temp = new SqliteTemp();
            await use((await temp.Create()).Storage);
        }))
            yield return Case("storage.sqlite.conformance." + testCase.Slug, testCase.Run);

        foreach (var testCase in StorageConformance.Create(async use =>
        {
            await using var temp = new SqliteTemp();
            var path = temp.NewPath();
            var created = await SqliteStorage.OpenAsync(path);
            await created.CloseAsync(SqliteContext);
            var storage = new ReopeningSqliteStorage(await SqliteStorage.OpenAsync(path), path);
            try
            {
                await use(storage);
            }
            finally
            {
                await storage.CloseAsync(SqliteContext);
            }
        }))
            yield return Case("storage.sqlite.reopen.conformance." + testCase.Slug, testCase.Run);

        foreach (var testCase in StorageConformance.Create(async use =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            foreach (var name in UnprefixedTableNames) await database.ExecAsync($"CREATE TABLE {name} (host_text TEXT)");
            var storage = await SqliteStorage.OpenAsync(database, new SqliteStorageOptions { Prefix = "pi_" });
            try
            {
                await use(storage);
            }
            finally
            {
                await storage.CloseAsync(SqliteContext);
            }
        }))
            yield return Case("storage.sqlite.prefixed.conformance." + testCase.Slug, testCase.Run);

        yield return Case("storage.sqlite.namespaces-tables-and-indexes-with-a-prefix-across-reopen", async () =>
        {
            await using var temp = new SqliteTemp();
            var path = temp.NewPath();
            using (var host = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                host.Open();
                RawQuery(host, "CREATE TABLE tasks (text TEXT)");
            }
            var storage = await SqliteStorage.OpenAsync(path, new SqliteStorageOptions { Prefix = "pi_" });
            Equal(Ids.SeqFromNumber(1), await CreateSqliteRoot(storage), "root sequence");
            await storage.CloseAsync(SqliteContext);

            var reopened = temp.Track(await SqliteStorage.OpenAsync(path, new SqliteStorageOptions { Prefix = "pi_" }));
            Equal(new ConversationRecord(ConversationId.Root), await reopened.GetConversationAsync(ConversationId.Root, SqliteContext), "root after reopen");
            using var database = RawSqlite(path, readOnly: true);
            var names = SchemaObjectNames(database);
            Equal("tasks", string.Join(",", names.Where(name => !name.StartsWith("pi_", StringComparison.Ordinal))), "names without the prefix");
            Check(names.Contains("pi_tasks"), "pi_tasks exists");
        });

        yield return Case("storage.sqlite.rejects-an-invalid-prefix-before-creating-the-database", async () =>
        {
            await using var temp = new SqliteTemp();
            var databaseDirectory = Path.Combine(Path.GetDirectoryName(temp.NewPath())!, "nested");
            var error = await ThrowsAsync<ArgumentException>(() =>
                SqliteStorage.OpenAsync(Path.Combine(databaseDirectory, "storage.sqlite"), new SqliteStorageOptions { Prefix = "bad-prefix" }), "open");
            Check(error.Message.Contains("Invalid SQLite schema prefix", StringComparison.Ordinal), error.Message);
            Check(!Directory.Exists(databaseDirectory), "the database directory was not created");
        });

        yield return Case("storage.sqlite.prefix.keeps-schema-names-unprefixed-by-default", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var storage = await SqliteStorage.OpenAsync(database);
            Equal("{\"version\":2}", (await database.GetAsync("SELECT version FROM durable_schema"))?.ToString(), "schema version");
            Equal<SqliteRow?>(null, await database.GetAsync("SELECT name FROM sqlite_master WHERE name = 'pi_durable_schema'"), "prefixed schema table");
            await storage.CloseAsync(SqliteContext);
        });

        yield return Case("storage.sqlite.prefix.prefixes-the-tables-and-indexes-without-changing-host-tables", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE host_notes (text TEXT)");
            await database.ExecAsync("CREATE TABLE tasks (text TEXT)");
            var storage = await SqliteStorage.OpenAsync(database, new SqliteStorageOptions { Prefix = "pi_" });
            var names = (await database.AllAsync(
                    "SELECT name FROM sqlite_master WHERE type IN ('table', 'index') AND name NOT LIKE 'sqlite_%' ORDER BY name"))
                .Select(row => row.GetString("name")).ToList();
            Check(names.Contains("host_notes"), "host_notes kept");
            Check(names.Contains("tasks"), "tasks kept");
            Check(!names.Contains("pi_host_notes"), "no pi_host_notes");
            var piNames = names.Where(name => name is not "host_notes" and not "tasks").ToList();
            Check(piNames.Count > 10, $"{piNames.Count} schema objects");
            Check(piNames.All(name => name.StartsWith("pi_", StringComparison.Ordinal)), "every schema object is prefixed: " + string.Join(",", piNames));
            Check(piNames.Contains("pi_tasks"), "pi_tasks exists");
            Check(piNames.Contains("pi_durable_schema"), "pi_durable_schema exists");
            await storage.CloseAsync(SqliteContext);
        });

        yield return Case("storage.sqlite.prefix.isolates-concurrently-opened-prefixes-on-one-database", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var opened = await Task.WhenAll(
                SqliteStorage.OpenAsync(database, new SqliteStorageOptions { Prefix = "first_" }),
                SqliteStorage.OpenAsync(database, new SqliteStorageOptions { Prefix = "second_" }));
            var (first, second) = (opened[0], opened[1]);
            var entryId = Ids.FromNumber<EntryId>(2);
            IReadOnlyList<StorageWrite> Writes(string kind) =>
            [
                new ConversationWrite(new ConversationRecord(ConversationId.Root)),
                new EntryWrite(new EntryRecord { Id = entryId, ConversationId = ConversationId.Root, Kind = kind }),
            ];
            await Task.WhenAll(first.CommitWritesAsync(Writes("first"), SqliteContext), second.CommitWritesAsync(Writes("second"), SqliteContext));
            Equal("first", (await first.GetEntryAsync(entryId, SqliteContext))?.Entry.Kind, "first entry");
            Equal("second", (await second.GetEntryAsync(entryId, SqliteContext))?.Entry.Kind, "second entry");
            await Task.WhenAll(first.CloseAsync(SqliteContext), second.CloseAsync(SqliteContext));
        });

        yield return Case("storage.sqlite.prefix.rejects-invalid-and-reserved-prefixes-and-closes-the-database", async () =>
        {
            foreach (var (prefix, message) in new[] { ("", "Invalid"), ("bad-prefix", "Invalid"), ("sqlite_pi_", "reserved") })
            {
                var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
                var error = await ThrowsAsync<ArgumentException>(() => SqliteStorage.OpenAsync(database, new SqliteStorageOptions { Prefix = prefix }),
                    $"prefix {JsonValue.String(prefix).ToJson()}");
                Check(error.Message.Contains(message, StringComparison.Ordinal), error.Message);
                var closed = await ThrowsAsync<InvalidOperationException>(() => database.GetAsync("SELECT 1 AS one"), "query after the refusal");
                Equal("SQLite database is closed", closed.Message, "the refused database is closed");
            }
        });

        yield return Case("storage.sqlite.native.loads-e-sqlite3-with-strict-tables-and-json", async () =>
        {
            Equal("e_sqlite3", SQLitePCL.raw.GetNativeLibraryName(), "native library");
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                Check(database.SqliteVersion >= new Version(3, 37, 0), $"SQLite {database.SqliteVersion} supports STRICT tables");
                var row = (await database.GetAsync("SELECT sqlite_version() AS version, json_valid(?) AS valid, json_valid(?) AS invalid",
                    "{\"a\":[1]}", "{"))!;
                Equal(database.SqliteVersion.ToString(3), row.GetString("version"), "sqlite_version()");
                Equal(1L, row.GetInt64("valid"), "json_valid of JSON");
                Equal(0L, row.GetInt64("invalid"), "json_valid of non-JSON");
                await database.ExecAsync("CREATE TABLE strict_probe (value INTEGER NOT NULL) STRICT");
                var strict = await ThrowsAsync<SqliteException>(() => database.RunAsync("INSERT INTO strict_probe (value) VALUES (?)", "text"), "strict insert");
                Check(strict.Message.Contains("cannot store TEXT value in INTEGER column strict_probe.value", StringComparison.Ordinal),
                    "STRICT rejects a mistyped value: " + strict.Message);
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("storage.sqlite.native.numbers-positional-parameters-outside-literals", () =>
        {
            Equal("SELECT ?1, '?', \"a?\", [b?], `c?` -- ?\n, ?2 /* ? */, ?3, ?7",
                SqliteConnectionExecutor.StatementCache.NumberParameters("SELECT ?, '?', \"a?\", [b?], `c?` -- ?\n, ? /* ? */, ?, ?7"),
                "numbered SQL");
            Equal("SELECT 'it''s ?', ?1", SqliteConnectionExecutor.StatementCache.NumberParameters("SELECT 'it''s ?', ?"), "doubled quote");
        });

        yield return Case("storage.sqlite.persists-records-sequence-allocation-and-global-id-allocation-across-reopen", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            Equal(Ids.SeqFromNumber(1), await CreateSqliteRoot(storage), "root sequence");
            var entryId = await storage.MintIdAsync<EntryId>();
            Equal(Ids.SeqFromNumber(2), await storage.CommitWritesAsync([new EntryWrite(SqliteEntry(entryId, ConversationId.Root))], SqliteContext), "entry sequence");
            await storage.CloseAsync(SqliteContext);

            var reopened = temp.Track(await SqliteStorage.OpenAsync(path));
            Equal<CommittedEntry?>(new CommittedEntry(SqliteEntry(entryId, ConversationId.Root), Ids.SeqFromNumber(2)),
                await reopened.GetEntryAsync(entryId, SqliteContext), "entry after reopen");
            Equal(entryId.Value + 1, (await reopened.MintIdAsync<EntryId>()).Value, "minted ID after reopen");
            Equal(Ids.SeqFromNumber(3), await reopened.CommitWritesAsync([new TaskWrite(PendingSqliteTask(await reopened.MintIdAsync<TaskId>()))], SqliteContext),
                "task sequence");
        });

        yield return Case("storage.sqlite.rejects-persisted-metadata-corruption-on-reopen", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await storage.CloseAsync(SqliteContext);
            using (var database = RawSqlite(path)) RawQuery(database, "DELETE FROM durable_metadata");
            Equal("Durable SQLite metadata is missing",
                (await ThrowsAsync<InvalidOperationException>(() => SqliteStorage.OpenAsync(path), "reopen")).Message, "message");
        });

        yield return Case("storage.sqlite.rejects-a-document-whose-required-base-is-missing", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await CreateSqliteRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "corrupt", Scope = DocumentScope.Session },
                DocumentContent.Base(1, (JsonObjectValue)J("{\"retained\":true}")))], SqliteContext);
            using (var database = RawSqlite(path)) RawQuery(database, "DELETE FROM document_revisions WHERE document_id = ?", id.Value);
            Equal($"Document {id} is missing a required base",
                (await ThrowsAsync<InvalidOperationException>(() => storage.GetDocumentAsync(id, DocumentPoint.Current, SqliteContext), "read")).Message,
                "message");
        });

        yield return Case("storage.sqlite.replays-detached-root-replacements-and-follow-up-edits-while-rejecting-corrupt-operations", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await CreateSqliteRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "replay", Scope = DocumentScope.Session },
                DocumentContent.Base(1, (JsonObjectValue)J("{\"nested\":{\"value\":1},\"rows\":[]}")))], SqliteContext);
            await storage.CommitWritesAsync([new DocumentChangeWrite(id,
                DocumentContent.Delta(1, Ops("[[\"r\",{\"nested\":{\"value\":2},\"rows\":[{\"id\":1}]}]]")))], SqliteContext);
            await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1,
                Ops("[[\"s\",[\"nested\",\"value\"],3],[\"p\",[\"rows\"],1,0,[{\"id\":2}]],[\"m\",[\"rows\"],[1,0]]]")))], SqliteContext);
            await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"nested\",\"value\"],4]]")))], SqliteContext);

            const string expected = "{\"nested\":{\"value\":4},\"rows\":[{\"id\":2},{\"id\":1}]}";
            var first = (await storage.GetDocumentAsync(id, DocumentPoint.Current, SqliteContext))!;
            Same(expected, first.Value, "first read");
            Same(expected, (await storage.GetDocumentAsync(id, DocumentPoint.Current, SqliteContext))!.Value, "second read");

            using (var database = RawSqlite(path))
            {
                RawQuery(database, """
                    UPDATE document_revisions SET content = ? WHERE document_id = ? AND seq =
                    (SELECT max(seq) FROM document_revisions WHERE document_id = ?)
                    """, "[[\"unknown\"]]", id.Value, id.Value);
            }
            var error = await ThrowsAsync<Exception>(() => storage.GetDocumentAsync(id, DocumentPoint.Current, SqliteContext), "corrupt read");
            Check(error.Message.Contains("unknown op verb", StringComparison.Ordinal), "corrupt operation message: " + error.Message);
        });

        yield return Case("storage.sqlite.rolls-sql-rows-and-sequence-allocation-back-as-one-transaction", async () =>
        {
            await using var temp = new SqliteTemp();
            var path = temp.NewPath();
            var storage = temp.Track(await SqliteStorage.OpenAsync(new FailingSqliteDatabase(await SqliteConnectionDatabase.OpenAsync(path),
                sql => sql.StartsWith("INSERT INTO tasks", StringComparison.Ordinal))));
            await CreateSqliteRoot(storage);
            var transientId = await storage.MintIdAsync<EntryId>();
            var transientTaskId = await storage.MintIdAsync<TaskId>();
            var error = await ThrowsAsync<InvalidOperationException>(() => storage.CommitWritesAsync(
                [new EntryWrite(SqliteEntry(transientId, ConversationId.Root)), new TaskWrite(PendingSqliteTask(transientTaskId))], SqliteContext), "failing commit");
            Equal(FailingSqliteDatabase.Message, error.Message, "failure");
            Equal<CommittedEntry?>(null, await storage.GetEntryAsync(transientId, SqliteContext), "rolled-back entry");
            Equal<TaskRecord?>(null, await storage.GetTaskAsync(transientTaskId, SqliteContext), "rolled-back task");
            var committedId = await storage.MintIdAsync<EntryId>();
            Equal(Ids.SeqFromNumber(2), await storage.CommitWritesAsync([new EntryWrite(SqliteEntry(committedId, ConversationId.Root))], SqliteContext),
                "next sequence");

            using var database = RawSqlite(path, readOnly: true);
            Equal(1L, RawScalar(database, "SELECT count(*) AS value FROM entries"), "entry rows");
            Equal(3L, RawScalar(database, "SELECT next_seq AS value FROM durable_metadata WHERE singleton = 1"), "next_seq");
        });

        yield return Case("storage.sqlite.reconstructs-recent-and-ancient-rewindable-points-after-reopen", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await CreateSqliteRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            var record = new DocumentCreate
            {
                Id = id, Kind = "history", Scope = DocumentScope.Conversation(ConversationId.Root),
                History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            var createdAt = await storage.CommitWritesAsync([new DocumentCreateWrite(record, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))],
                SqliteContext);
            var ancientAt = createdAt;
            var recentAt = createdAt;
            for (var count = 1; count <= 40; count++)
            {
                recentAt = await storage.CommitWritesAsync([new DocumentChangeWrite(id, count == 20
                    ? DocumentContent.Base(1, (JsonObjectValue)J($"{{\"count\":{count}}}"))
                    : DocumentContent.Delta(1, Ops($"[[\"s\",[\"count\"],{count}]]")))], SqliteContext);
                if (count == 5) ancientAt = recentAt;
            }
            await storage.CloseAsync(SqliteContext);
            var reopened = temp.Track(await SqliteStorage.OpenAsync(path));
            Same("{\"count\":5}", (await reopened.GetDocumentAsync(id, ancientAt, SqliteContext))!.Value, "ancient point");
            Same("{\"count\":40}", (await reopened.GetDocumentAsync(id, recentAt, SqliteContext))!.Value, "recent point");
        });

        yield return Case("storage.sqlite.uses-indexes-for-exact-addresses-exact-scopes-entry-history-and-document-revision-tails", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await storage.CloseAsync(SqliteContext);
            using var database = RawSqlite(path, readOnly: true);
            string Plan(string sql, params object?[] parameters) =>
                string.Join("\n", RawQuery(database, "EXPLAIN QUERY PLAN " + sql, parameters).Select(row => row.GetString("detail")));
            string[] details =
            [
                Plan("""
                    SELECT record FROM documents
                    WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
                    AND retired_at IS NULL ORDER BY created_at DESC LIMIT 1
                    """, "kind", "session", 0L, 0L, ""),
                Plan("""
                    SELECT record FROM documents
                    WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
                    AND created_at <= ? AND (retired_at IS NULL OR retired_at > ?)
                    ORDER BY created_at DESC LIMIT 1
                    """, "kind", "conversation", 1L, 0L, "", 10L, 10L),
                Plan("""
                    SELECT record FROM documents
                    WHERE scope_kind = ? AND owner_id = ? AND kind = ? AND id > ? ORDER BY id LIMIT ?
                    """, "task", 1L, "kind", 0L, 10L),
                Plan("""
                    SELECT record FROM entries
                    WHERE conversation_id = ? AND id <= ? ORDER BY id DESC LIMIT ?
                    """, 1L, 10L, 10L),
                Plan("""
                    SELECT record FROM entries
                    WHERE conversation_id = ? AND head IS NOT NULL AND id <= ? ORDER BY id DESC LIMIT 1
                    """, 1L, 10L),
                Plan("SELECT record FROM tasks WHERE status = ? AND id > ? ORDER BY id LIMIT ?", "pending", 0L, 10L),
                Plan("""
                    SELECT seq, kind, version, content FROM document_revisions
                    WHERE document_id = ? AND kind = 'base' AND seq <= ? ORDER BY seq DESC LIMIT 1
                    """, 1L, 10L),
                Plan("""
                    SELECT seq, kind, version, content FROM document_revisions
                    WHERE document_id = ? AND seq > ? AND seq <= ? ORDER BY seq
                    """, 1L, 5L, 10L),
            ];
            string[] indexes = ["documents_by_address", "documents_by_address", "documents_by_scope_kind", "entries_by_conversation",
                "entry_heads_by_conversation", "tasks_by_status", "document_revisions_by_kind", "sqlite_autoindex_document_revisions_1"];
            for (var index = 0; index < indexes.Length; index++)
                Check(details[index].Contains(indexes[index], StringComparison.Ordinal), $"plan {index} uses {indexes[index]}: {details[index]}");
            foreach (var detail in details[..3]) Check(!detail.Contains("SCAN documents", StringComparison.Ordinal), "no document scan: " + detail);
            Check(!details[7].Contains("SCAN document_revisions", StringComparison.Ordinal), "no revision scan: " + details[7]);
        });

        yield return Case("storage.sqlite.reclaims-current-only-revisions-only-after-a-base-or-retirement", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await CreateSqliteRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "latest", Scope = DocumentScope.Session },
                DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))], SqliteContext);
            for (var count = 1; count <= 10; count++)
                await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops($"[[\"s\",[\"count\"],{count}]]")))], SqliteContext);
            Equal(11L, RevisionCount(path, id), "revisions after deltas");
            using (var revisions = RawSqlite(path, readOnly: true))
            {
                var row = RawQuery(revisions,
                    "SELECT content FROM document_revisions WHERE document_id = ? AND kind = 'delta' ORDER BY seq DESC LIMIT 1", id.Value)[0];
                Same("[[\"s\",[\"count\"],10]]", J(row.GetString("content")), "newest delta");
            }
            await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":11}")))], SqliteContext);
            Equal(1L, RevisionCount(path, id), "revisions after a base");
            await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"r\",{\"count\":12}]]")))], SqliteContext);
            Equal(2L, RevisionCount(path, id), "revisions after one more delta");
            await storage.CommitWritesAsync([new DocumentRetireWrite(id)], SqliteContext);
            Equal(0L, RevisionCount(path, id), "revisions after retirement");
        });

        yield return Case("storage.sqlite.auto-checkpoints-wal-frames-and-truncates-the-wal-on-close", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create(new SqliteStorageOptions { WalAutoCheckpointPages = 1 });
            await CreateSqliteRoot(storage);
            for (var index = 0; index < 20; index++)
            {
                await storage.CommitWritesAsync([new EntryWrite(SqliteEntry(await storage.MintIdAsync<EntryId>(), ConversationId.Root,
                    JsonValue.Object([new("text", new string('x', 32 * 1024)), new("index", index)])))], SqliteContext);
            }
            var walPath = path + "-wal";
            Check(new FileInfo(walPath).Length < 512 * 1024, $"WAL is checkpointed: {new FileInfo(walPath).Length} bytes");
            using var observer = RawSqlite(path, readOnly: true);
            Equal(20L, RawScalar(observer, "SELECT count(*) AS value FROM entries"), "entries seen by another connection");
            await storage.CloseAsync(SqliteContext);
            Equal(0L, new FileInfo(walPath).Length, "WAL size after close");
        });

        yield return Case("storage.sqlite.reuses-pages-released-by-current-only-checkpoints", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await CreateSqliteRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            var large = new string('x', 512 * 1024);
            await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "reuse", Scope = DocumentScope.Session },
                DocumentContent.Base(1, JsonValue.Object([new("text", large)])))], SqliteContext);
            await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, JsonValue.Object([new("text", "small")])))], SqliteContext);
            long pagesAfterDelete, freeAfterDelete;
            using (var before = RawSqlite(path, readOnly: true))
            {
                pagesAfterDelete = RawScalar(before, "SELECT page_count AS value FROM pragma_page_count() ");
                freeAfterDelete = RawScalar(before, "SELECT freelist_count AS value FROM pragma_freelist_count() ");
            }
            Check(freeAfterDelete > 0, "pages were released");
            await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, JsonValue.Object([new("text", large)])))], SqliteContext);
            using var after = RawSqlite(path, readOnly: true);
            var pagesAfterReuse = RawScalar(after, "SELECT page_count AS value FROM pragma_page_count() ");
            var freeAfterReuse = RawScalar(after, "SELECT freelist_count AS value FROM pragma_freelist_count() ");
            Check(pagesAfterReuse <= pagesAfterDelete + 2, $"pages reused: {pagesAfterReuse} after {pagesAfterDelete}");
            Check(freeAfterReuse < freeAfterDelete, $"free pages consumed: {freeAfterReuse} after {freeAfterDelete}");
        });

        yield return Case("storage.sqlite.keeps-representative-row-and-document-storage-bounded", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create();
            await CreateSqliteRoot(storage);
            for (var index = 0; index < 100; index++)
            {
                await storage.CommitWritesAsync([new EntryWrite(SqliteEntry(await storage.MintIdAsync<EntryId>(), ConversationId.Root,
                    JsonValue.Object([new("index", index), new("text", new string('x', 1_024))])))], SqliteContext);
            }
            var documentId = await storage.MintIdAsync<DocumentId>();
            await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate
            {
                Id = documentId, Kind = "size.history", Scope = DocumentScope.Conversation(ConversationId.Root),
                History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            }, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))], SqliteContext);
            for (var count = 1; count <= 100; count++)
                await storage.CommitWritesAsync([new DocumentChangeWrite(documentId, DocumentContent.Delta(1, Ops($"[[\"s\",[\"count\"],{count}]]")))], SqliteContext);
            await storage.CloseAsync(SqliteContext);
            Check(new FileInfo(path).Length < 1024 * 1024, $"file size {new FileInfo(path).Length}");
        });
    }

    private static IEnumerable<(string, Func<Task>)> StorageSqliteBatchesCases()
    {
        foreach (var test in SqliteBulkChecks()) yield return test;
        yield return Case("storage.sqlite.batches.commit-failure-rolls-back-all-batches-before-receipts", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var storage = await SqliteStorage.OpenAsync(database);
            try
            {
                await CreateSqliteRoot(storage);
                await database.ExecAsync("PRAGMA foreign_keys = ON");
                await database.ExecAsync("CREATE TABLE fault_parent (id INTEGER PRIMARY KEY)");
                await database.ExecAsync("CREATE TABLE fault_child (id INTEGER REFERENCES fault_parent(id) DEFERRABLE INITIALLY DEFERRED)");
                await database.ExecAsync("CREATE TRIGGER fail_commit AFTER INSERT ON entries BEGIN INSERT INTO fault_child VALUES (999); END");
                var before = (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata"))!.ToString();
                var receipts = new List<int>();
                IReadOnlyList<IReadOnlyList<StorageWrite>> batches =
                    [[new EntryWrite(SqliteEntry(new EntryId(100), ConversationId.Root))],
                     [new EntryWrite(SqliteEntry(new EntryId(101), ConversationId.Root))]];
                await ThrowsAsync<SqliteException>(() => storage.CommitAsync(batches, (index, _) => receipts.Add(index), SqliteContext), "COMMIT constraint");
                Equal(0, receipts.Count, "failed COMMIT has no receipts");
                Equal(0L, (await database.GetAsync("SELECT count(*) AS n FROM entries"))!.GetInt64("n"), "entries rolled back");
                Equal(0L, (await database.GetAsync("SELECT count(*) AS n FROM record_ids WHERE id >= 100"))!.GetInt64("n"), "claims rolled back");
                Equal(before, (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata"))!.ToString(), "metadata rolled back");
                await database.ExecAsync("DROP TRIGGER fail_commit");
                var accepted = await storage.CommitAsync(batches, (index, _) =>
                {
                    Equal(2L, database.GetAsync("SELECT count(*) AS n FROM entries").GetAwaiter().GetResult()!.GetInt64("n"), "receipt observes committed batches");
                    receipts.Add(index);
                }, SqliteContext);
                Equal(2, accepted.Stored, "recovery stores both batches");
                Equal("0,1", string.Join(",", receipts), "ordered receipts");
            }
            finally { await storage.CloseAsync(SqliteContext); }
        });

        // Authored C# corruption matrix: valid JSON with invalid persisted shape, not malformed JSON syntax.
        foreach (var fault in new[] { "record-shape", "copy-record-shape", "missing-base", "base-shape", "delta-shape", "version-boundary", "missing-revision" })
        foreach (var later in new[] { false, true })
            yield return SqliteCorruptStoredDataCase(fault, later);

        yield return SqliteLaterCheckFailureCase(
            "storage.sqlite.batches.rejects-later-check-faulted-io-task",
            () => Task.FromException<SqliteRow?>(new IOException("disk I/O error")));
        yield return SqliteLaterCheckFailureCase(
            "storage.sqlite.batches.rejects-later-check-faulted-semantic-exception-type",
            () => Task.FromException<SqliteRow?>(new InvalidOperationException("driver failure")));
        yield return Case("storage.sqlite.batches.later-write-failure-rolls-back-before-receipts", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var fail = false;
            var storage = await SqliteStorage.OpenAsync(new FailingSqliteDatabase(database,
                sql => fail && sql.StartsWith("UPDATE durable_metadata", StringComparison.Ordinal)));
            try
            {
                await CreateSqliteRoot(storage);
                fail = true;
                var receipts = new List<int>();
                var before = (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata"))!.ToString();
                IReadOnlyList<IReadOnlyList<StorageWrite>> batches =
                    [[new EntryWrite(SqliteEntry(new EntryId(100), ConversationId.Root))],
                     [new EntryWrite(SqliteEntry(new EntryId(101), ConversationId.Root))]];
                Equal("rejected", await SqliteCommitOutcome(storage.CommitAsync(batches, (index, _) => receipts.Add(index), SqliteContext)), "write rejects");
                Equal(0, receipts.Count, "no write-failure receipts");
                Equal(0L, (await database.GetAsync("SELECT count(*) AS n FROM entries"))!.GetInt64("n"), "entries rolled back");
                Equal(before, (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata"))!.ToString(), "metadata unchanged");
            }
            finally { await storage.CloseAsync(SqliteContext); }
        });

        yield return SqliteLaterCheckFailureCase(
            "storage.sqlite.batches.rejects-the-whole-call-the-batches-before-it-included-when-a-later-batch-s-check-read-fails-with-a-undefined-rejection",
            () => Task.FromCanceled<SqliteRow?>(new CancellationToken(canceled: true)));
        yield return SqliteLaterCheckFailureCase(
            "storage.sqlite.batches.rejects-the-whole-call-the-batches-before-it-included-when-a-later-batch-s-check-read-fails-with-a-synchronous-throw",
            () => throw new InvalidOperationException("sync driver failure"));

        yield return SqliteCheckReadFailureCase(
            "storage.sqlite.batches.rejects-a-batch-call-whose-check-read-fails-in-the-database-with-an-error-rejection-and-refuses-only-for-its-own-checks",
            () => Task.FromException<SqliteRow?>(new IOException("disk I/O error")));
        yield return SqliteCheckReadFailureCase(
            "storage.sqlite.batches.rejects-a-batch-call-whose-check-read-fails-in-the-database-with-a-synchronous-throw-and-refuses-only-for-its-own-checks",
            () => throw new IOException("disk I/O error"));
        yield return SqliteCheckReadFailureCase(
            "storage.sqlite.batches.rejects-a-batch-call-whose-check-read-fails-in-the-database-with-a-primitive-rejection-and-refuses-only-for-its-own-checks",
            () => Task.FromException<SqliteRow?>(new InvalidOperationException("disk I/O error")));
        yield return SqliteCheckReadFailureCase(
            "storage.sqlite.batches.rejects-a-batch-call-whose-check-read-fails-in-the-database-with-an-undefined-rejection-and-refuses-only-for-its-own-checks",
            () => Task.FromCanceled<SqliteRow?>(new CancellationToken(canceled: true)));
    }

    private static (string, Func<Task>) SqliteCorruptStoredDataCase(string fault, bool later) =>
        Case($"storage.sqlite.batches.corrupt-{fault}-{(later ? "later" : "first")}-rejects-whole-transaction", async () =>
    {
        var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
        var storage = await SqliteStorage.OpenAsync(database);
        try
        {
            await CreateSqliteRoot(storage);
            var source = new DocumentCreate
            {
                Id = new DocumentId(2), Kind = "corrupt", Scope = DocumentScope.Conversation(ConversationId.Root),
                History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            await storage.CommitWritesAsync([new DocumentCreateWrite(source, DocumentContent.Base(1, (JsonObjectValue)J("{\"n\":1}")))], SqliteContext);
            await storage.CommitWritesAsync([new DocumentChangeWrite(source.Id, DocumentContent.Delta(1, Ops("[[\"s\",[\"n\"],2]]")))], SqliteContext);
            switch (fault)
            {
                case "record-shape":
                case "copy-record-shape":
                    await database.RunAsync("UPDATE documents SET record = ? WHERE id = ?", "{}", source.Id.Value);
                    break;
                case "missing-base":
                    await database.RunAsync("DELETE FROM document_revisions WHERE document_id = ? AND kind = 'base'", source.Id.Value);
                    break;
                case "base-shape":
                    await database.RunAsync("UPDATE document_revisions SET content = ? WHERE document_id = ? AND kind = 'base'", "[]", source.Id.Value);
                    break;
                case "delta-shape":
                    await database.RunAsync("UPDATE document_revisions SET content = ? WHERE document_id = ? AND kind = 'delta'", "{}", source.Id.Value);
                    break;
                case "version-boundary":
                    await database.RunAsync("UPDATE document_revisions SET version = 2 WHERE document_id = ? AND kind = 'delta'", source.Id.Value);
                    break;
                case "missing-revision":
                    await database.RunAsync("DELETE FROM document_revisions WHERE document_id = ?", source.Id.Value);
                    break;
            }
            StorageWrite failing = fault == "record-shape"
                ? new DocumentChangeWrite(source.Id, DocumentContent.Base(1, (JsonObjectValue)J("{}")))
                : fault == "missing-revision"
                    ? new DocumentChangeWrite(source.Id, DocumentContent.Delta(1, Ops("[[\"s\",[\"n\"],3]]")))
                    : new DocumentCopyWrite(source with { Id = new DocumentId(3), Scope = DocumentScope.Conversation(new ConversationId(50)) }, new(source.Id, DocumentPoint.Current));
            IReadOnlyList<StorageWrite> first = [new EntryWrite(SqliteEntry(new EntryId(100), ConversationId.Root))];
            IReadOnlyList<IReadOnlyList<StorageWrite>> batches = later ? [first, [failing]] : [[failing], first];
            var before = (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata"))!.ToString();
            var receipts = new List<int>();
            Equal("rejected", await SqliteCommitOutcome(storage.CommitAsync(batches, (index, _) => receipts.Add(index), SqliteContext)), "corruption rejects");
            Equal(0, receipts.Count, "no receipts");
            Equal<CommittedEntry?>(null, await storage.GetEntryAsync(new EntryId(100), SqliteContext), "earlier entry rolled back");
            Equal<SqliteRow?>(null, await database.GetAsync("SELECT id FROM record_ids WHERE id = 100"), "ID claim rolled back");
            Equal(before, (await database.GetAsync("SELECT next_id, next_seq FROM durable_metadata"))!.ToString(), "allocation metadata unchanged");
            // A real semantic refusal still commits the accepted prefix, even in a store with unrelated corruption.
            var accepted = await storage.CommitAsync([first, [new ConversationWrite(new(ConversationId.Root))]], (index, _) => receipts.Add(index), SqliteContext);
            Equal(1, accepted.Stored, "semantic refusal keeps prefix");
            Check(accepted.Refused is not null, "semantic refusal returned");
            Equal("0", string.Join(",", receipts), "prefix receipt only");
            Check(await storage.GetEntryAsync(new EntryId(100), SqliteContext) is not null, "prefix persisted");
        }
        finally { await storage.CloseAsync(SqliteContext); }
    });

    /// <summary>The first batch passes its checks and is written; the second batch's check read fails with <paramref name="fail"/>.</summary>
    private static (string, Func<Task>) SqliteLaterCheckFailureCase(string id, Func<Task<SqliteRow?>> fail) => Case(id, async () =>
    {
        await using var temp = new SqliteTemp();
        var failCheck = false;
        var storage = temp.Track(await SqliteStorage.OpenAsync(new CheckReadFailingDatabase(await SqliteConnectionDatabase.OpenAsync(temp.NewPath()),
            sql => failCheck && sql.StartsWith("SELECT record FROM documents WHERE id = ?", StringComparison.Ordinal) ? fail() : null)));
        await CreateSqliteRoot(storage);
        var source = new DocumentCreate { Id = new DocumentId(2), Kind = "copy", Scope = DocumentScope.Conversation(ConversationId.Root),
            History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf };
        await storage.CommitWritesAsync([new DocumentCreateWrite(source, DocumentContent.Base(1, (JsonObjectValue)J("{}")))], SqliteContext);
        EntryId[] ids = [new EntryId(3)];
        IReadOnlyList<IReadOnlyList<StorageWrite>> batches =
            [[new EntryWrite(SqliteEntry(ids[0], ConversationId.Root))],
             [new DocumentCopyWrite(source with { Id = new DocumentId(4), Scope = DocumentScope.Conversation(new ConversationId(50)) }, new(source.Id, DocumentPoint.Current))]];
        failCheck = true;
        var receipts = new List<long>();
        var outcome = await SqliteCommitOutcome(storage.CommitAsync(batches, (index, _) => receipts.Add(index), SqliteContext));
        failCheck = false;
        Equal("rejected", outcome, "outcome");
        Equal(0, receipts.Count, "receipts");
        Equal<CommittedEntry?>(null, await storage.GetEntryAsync(ids[0], SqliteContext), "the first batch's entry");
    });

    /// <summary>A driver failure in a check's read, armed for the next batch call.</summary>
    private static (string, Func<Task>) SqliteCheckReadFailureCase(string id, Func<Task<SqliteRow?>> fail) => Case(id, async () =>
    {
        await using var temp = new SqliteTemp();
        var failCheck = false;
        var storage = temp.Track(await SqliteStorage.OpenAsync(new CheckReadFailingDatabase(await SqliteConnectionDatabase.OpenAsync(temp.NewPath()),
            sql => failCheck && sql.Contains("record_type", StringComparison.Ordinal) ? fail() : null)));
        await CreateSqliteRoot(storage);
        var first = await storage.MintIdAsync<EntryId>();
        var second = await storage.MintIdAsync<EntryId>();
        IReadOnlyList<IReadOnlyList<StorageWrite>> batches = [.. new[] { first, second }.Select(IReadOnlyList<StorageWrite> (entryId) =>
            [new EntryWrite(new() { Id = entryId, ConversationId = ConversationId.Root, Kind = "note" })])];
        failCheck = true;
        var receipts = new List<long>();
        var outcome = await SqliteCommitOutcome(storage.CommitAsync(batches, (index, _) => receipts.Add(index), SqliteContext));
        failCheck = false;
        // Unknown outcome, rolled back whole: nothing received, nothing stored.
        Equal("rejected", outcome, "outcome");
        Equal(0, receipts.Count, "receipts");
        Equal<CommittedEntry?>(null, await storage.GetEntryAsync(first, SqliteContext), "the first batch's entry");
        // Durable's own check still refuses, keeping the batches before it.
        var refused = await storage.CommitAsync([batches[0], [new ConversationWrite(new(ConversationId.Root))]], (index, _) => receipts.Add(index), SqliteContext);
        Equal(1, refused.Stored, "batches stored before the refusal");
        Check(refused.Refused?.Message.Contains("already belongs to conversation", StringComparison.Ordinal) == true,
            "refusal: " + refused.Refused?.Message);
        Equal("0", string.Join(",", receipts), "receipts after the refusal");
        Check(await storage.GetEntryAsync(first, SqliteContext) is not null, "the first batch's entry after the refusal");
    });

    /// <summary>"resolved" or "rejected", as upstream's then(() => "resolved", () => "rejected"): a canceled call is rejected too.</summary>
    private static async Task<string> SqliteCommitOutcome(Task<CommitResult> commit)
    {
        try
        {
            await commit;
            return "resolved";
        }
        catch (Exception)
        {
            return "rejected";
        }
    }

    private static EntryRecord SqliteEntry(EntryId id, ConversationId conversationId, JsonValue? data = null) =>
        new() { Id = id, ConversationId = conversationId, Kind = "message", Data = data };

    private static Task<Seq> CreateSqliteRoot(IStorage storage) =>
        storage.CommitWritesAsync([new ConversationWrite(new ConversationRecord(ConversationId.Root))], SqliteContext);

    private static TaskRecord PendingSqliteTask(TaskId id) => new()
    {
        Id = id, ConversationId = ConversationId.Root, Kind = "test.task", Version = 1, Input = JsonValue.Null,
        State = new PendingTaskState(J("{\"phase\":\"ready\"}")), Background = false, AbortRequested = false,
    };

    private static long RevisionCount(string path, DocumentId documentId)
    {
        using var database = RawSqlite(path, readOnly: true);
        return RawQuery(database, "SELECT count(*) AS count FROM document_revisions WHERE document_id = ?", documentId.Value)[0].GetInt64("count");
    }

    /// <summary>A second, unpooled connection to a storage file, as upstream's tests open with node:sqlite.</summary>
    private static SqliteConnection RawSqlite(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static List<SqliteRow> RawQuery(SqliteConnection connection, string sql, params object?[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqliteConnectionExecutor.StatementCache.NumberParameters(sql);
        for (var index = 0; index < parameters.Length; index++) command.Parameters.AddWithValue("?" + (index + 1), parameters[index] ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<SqliteRow>();
        while (reader.Read())
            rows.Add(new SqliteRow(Enumerable.Range(0, reader.FieldCount)
                .Select(column => new KeyValuePair<string, object?>(reader.GetName(column), reader.IsDBNull(column) ? null : reader.GetValue(column)))));
        return rows;
    }

    private static long RawScalar(SqliteConnection connection, string sql) => RawQuery(connection, sql)[0].GetInt64("value");

    /// <summary>The tables and indexes of a database, as upstream lists them.</summary>
    private static List<string> SchemaObjectNames(SqliteConnection connection) =>
        RawQuery(connection, "SELECT name FROM sqlite_master WHERE type IN ('table', 'index') AND name NOT LIKE 'sqlite_%'")
            .Select(row => row.GetString("name")).ToList();

    /// <summary>The unprefixed names of upstream's tables (TABLE_NAMES in packages/durable/src/storage/sqlite/schema.ts).</summary>
    private static readonly string[] UnprefixedTableNames =
        ["durable_schema", "durable_metadata", "record_ids", "conversations", "entries", "tasks", "submissions", "documents", "document_revisions"];

    /// <summary>Temporary storage files for one case: closes every tracked storage, then deletes the directories (upstream
    /// <c>afterEach</c>). A file still open would make the deletion fail on Windows.</summary>
    private sealed class SqliteTemp : IAsyncDisposable
    {
        private readonly List<SqliteStorage> storages = [];
        private readonly List<string> directories = [];

        public string NewPath()
        {
            var directory = Temp("sqlite");
            directories.Add(directory);
            return Path.Combine(directory, "storage.sqlite");
        }

        public async Task<(SqliteStorage Storage, string Path)> Create(SqliteStorageOptions? options = null)
        {
            var path = NewPath();
            return (Track(await SqliteStorage.OpenAsync(path, options)), path);
        }

        public SqliteStorage Track(SqliteStorage storage)
        {
            storages.Add(storage);
            return storage;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var storage in storages) await storage.CloseAsync(SqliteContext);
            foreach (var directory in directories) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Upstream's ReopeningStorage: closes and reopens the file after every commit, so every read sees persisted state.</summary>
    private sealed class ReopeningSqliteStorage(SqliteStorage initial, string path) : IStorage
    {
        private SqliteStorage current = initial;
        private bool closed;

        public async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context)
        {
            if (closed) return await current.CommitAsync(batches, receive, context);
            try
            {
                return await current.CommitAsync(batches, receive, context);
            }
            finally
            {
                await current.CloseAsync(SqliteContext);
                current = await SqliteStorage.OpenAsync(path);
            }
        }

        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => current.MintIdAsync<TId>();
        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => current.GetConversationAsync(id, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => current.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
            current.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            current.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanEntriesAsync(query, limit, cursor, context);
        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => current.GetTaskAsync(id, context);
        public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanTasksAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => current.GetSubmissionAsync(id, context);
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            current.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            current.FindDocumentAsync(address, at, context);
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => current.GetDocumentAsync(id, at, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanDocumentsAsync(query, limit, cursor, context);

        public async Task CloseAsync(DurableContext context)
        {
            if (closed) return;
            closed = true;
            await current.CloseAsync(context);
        }
    }

    /// <summary>A database whose transaction handle's reads go to <paramref name="intercept"/> first: a task it returns, or a throw, stands
    /// for the driver's answer; null passes the read on (upstream's faulty SqliteDatabase in the "SqliteStorage batches" cases).</summary>
    private sealed class CheckReadFailingDatabase(ISqliteDatabase inner, Func<string, Task<SqliteRow?>?> intercept) : ISqliteDatabase
    {
        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] parameters) => inner.RunAsync(sql, parameters);
        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => inner.RunCountedAsync(sql, parameters);
        public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters) => inner.GetAsync(sql, parameters);
        public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters) => inner.AllAsync(sql, parameters);
        public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback) =>
            inner.TransactionAsync(transaction => callback(new Executor(transaction, intercept)));
        public Task CloseAsync() => inner.CloseAsync();

        private sealed class Executor(ISqliteExecutor inner, Func<string, Task<SqliteRow?>?> intercept) : ISqliteExecutor
        {
            public Task ExecAsync(string sql) => inner.ExecAsync(sql);
            public Task RunAsync(string sql, params object?[] parameters) => inner.RunAsync(sql, parameters);
            public Task<long> RunCountedAsync(string sql, params object?[] parameters) => inner.RunCountedAsync(sql, parameters);
            public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters) => intercept(sql) ?? inner.GetAsync(sql, parameters);
            public async Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters)
            {
                var intercepted = intercept(sql);
                if (intercepted is null) return await inner.AllAsync(sql, parameters);
                var row = await intercepted;
                return row is null ? [] : [row];
            }
        }
    }

    /// <summary>Fails the first statement inside a transaction that <paramref name="fails"/> matches, after the earlier statements ran.</summary>
    private sealed class FailingSqliteDatabase(ISqliteDatabase inner, Func<string, bool> fails) : ISqliteDatabase
    {
        public const string Message = "injected statement failure";

        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] parameters) => inner.RunAsync(sql, parameters);
        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => inner.RunCountedAsync(sql, parameters);
        public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters) => inner.GetAsync(sql, parameters);
        public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters) => inner.AllAsync(sql, parameters);
        public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback) =>
            inner.TransactionAsync(transaction => callback(new Executor(transaction, fails)));
        public Task CloseAsync() => inner.CloseAsync();

        private sealed class Executor(ISqliteExecutor inner, Func<string, bool> fails) : ISqliteExecutor
        {
            public Task ExecAsync(string sql) => inner.ExecAsync(sql);
            public Task RunAsync(string sql, params object?[] parameters) =>
                fails(sql.TrimStart()) ? Task.FromException(new InvalidOperationException(Message)) : inner.RunAsync(sql, parameters);
            public Task<long> RunCountedAsync(string sql, params object?[] parameters) =>
                fails(sql.TrimStart()) ? Task.FromException<long>(new InvalidOperationException(Message)) : inner.RunCountedAsync(sql, parameters);
            public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters) => inner.GetAsync(sql, parameters);
            public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters) => inner.AllAsync(sql, parameters);
        }
    }
}
