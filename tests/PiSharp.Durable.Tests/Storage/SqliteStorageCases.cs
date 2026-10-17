// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/sqlite-storage.test.ts. The storage conformance kit runs
// against SqliteStorage directly (storage.sqlite.conformance.<case>) and through a storage that closes and reopens the file after every
// commit (storage.sqlite.reopen.conformance.<case>), as upstream's ReopeningStorage does. Upstream inspects the file with a second
// node:sqlite connection; these cases use a second Microsoft.Data.Sqlite connection with Pooling=False. Adapted:
// - rolls-sql-rows-and-sequence-allocation-back-as-one-transaction: a circular input cannot exist in an immutable record, so a database
//   wrapper fails the task insert after the entry insert instead; the rollback assertions are upstream's.
// - replays-detached-root-replacements-...: returned values are immutable, so instead of mutating the first read the case checks that a
//   second read is equal.
// storage.sqlite.native.* is authored: the bundled e_sqlite3 library is loaded, is at least 3.37.0 (STRICT tables) and has JSON functions.
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
            Equal(Ids.SeqFromNumber(2), await storage.CommitAsync([new EntryWrite(SqliteEntry(entryId, ConversationId.Root))], SqliteContext), "entry sequence");
            await storage.CloseAsync(SqliteContext);

            var reopened = temp.Track(await SqliteStorage.OpenAsync(path));
            Equal<CommittedEntry?>(new CommittedEntry(SqliteEntry(entryId, ConversationId.Root), Ids.SeqFromNumber(2)),
                await reopened.GetEntryAsync(entryId, SqliteContext), "entry after reopen");
            Equal(entryId.Value + 1, (await reopened.MintIdAsync<EntryId>()).Value, "minted ID after reopen");
            Equal(Ids.SeqFromNumber(3), await reopened.CommitAsync([new TaskWrite(PendingSqliteTask(await reopened.MintIdAsync<TaskId>()))], SqliteContext),
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
            await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "corrupt", Scope = DocumentScope.Session },
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
            await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "replay", Scope = DocumentScope.Session },
                DocumentContent.Base(1, (JsonObjectValue)J("{\"nested\":{\"value\":1},\"rows\":[]}")))], SqliteContext);
            await storage.CommitAsync([new DocumentChangeWrite(id,
                DocumentContent.Delta(1, Ops("[[\"r\",{\"nested\":{\"value\":2},\"rows\":[{\"id\":1}]}]]")))], SqliteContext);
            await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1,
                Ops("[[\"s\",[\"nested\",\"value\"],3],[\"p\",[\"rows\"],1,0,[{\"id\":2}]],[\"m\",[\"rows\"],[1,0]]]")))], SqliteContext);
            await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"nested\",\"value\"],4]]")))], SqliteContext);

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
            var error = await ThrowsAsync<InvalidOperationException>(() => storage.CommitAsync(
                [new EntryWrite(SqliteEntry(transientId, ConversationId.Root)), new TaskWrite(PendingSqliteTask(transientTaskId))], SqliteContext), "failing commit");
            Equal(FailingSqliteDatabase.Message, error.Message, "failure");
            Equal<CommittedEntry?>(null, await storage.GetEntryAsync(transientId, SqliteContext), "rolled-back entry");
            Equal<TaskRecord?>(null, await storage.GetTaskAsync(transientTaskId, SqliteContext), "rolled-back task");
            var committedId = await storage.MintIdAsync<EntryId>();
            Equal(Ids.SeqFromNumber(2), await storage.CommitAsync([new EntryWrite(SqliteEntry(committedId, ConversationId.Root))], SqliteContext),
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
            var createdAt = await storage.CommitAsync([new DocumentCreateWrite(record, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))],
                SqliteContext);
            var ancientAt = createdAt;
            var recentAt = createdAt;
            for (var count = 1; count <= 40; count++)
            {
                recentAt = await storage.CommitAsync([new DocumentChangeWrite(id, count == 20
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
            await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "latest", Scope = DocumentScope.Session },
                DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))], SqliteContext);
            for (var count = 1; count <= 10; count++)
                await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops($"[[\"s\",[\"count\"],{count}]]")))], SqliteContext);
            Equal(11L, RevisionCount(path, id), "revisions after deltas");
            using (var revisions = RawSqlite(path, readOnly: true))
            {
                var row = RawQuery(revisions,
                    "SELECT content FROM document_revisions WHERE document_id = ? AND kind = 'delta' ORDER BY seq DESC LIMIT 1", id.Value)[0];
                Same("[[\"s\",[\"count\"],10]]", J(row.GetString("content")), "newest delta");
            }
            await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":11}")))], SqliteContext);
            Equal(1L, RevisionCount(path, id), "revisions after a base");
            await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"r\",{\"count\":12}]]")))], SqliteContext);
            Equal(2L, RevisionCount(path, id), "revisions after one more delta");
            await storage.CommitAsync([new DocumentRetireWrite(id)], SqliteContext);
            Equal(0L, RevisionCount(path, id), "revisions after retirement");
        });

        yield return Case("storage.sqlite.auto-checkpoints-wal-frames-and-truncates-the-wal-on-close", async () =>
        {
            await using var temp = new SqliteTemp();
            var (storage, path) = await temp.Create(new SqliteStorageOptions { WalAutoCheckpointPages = 1 });
            await CreateSqliteRoot(storage);
            for (var index = 0; index < 20; index++)
            {
                await storage.CommitAsync([new EntryWrite(SqliteEntry(await storage.MintIdAsync<EntryId>(), ConversationId.Root,
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
            await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "reuse", Scope = DocumentScope.Session },
                DocumentContent.Base(1, JsonValue.Object([new("text", large)])))], SqliteContext);
            await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, JsonValue.Object([new("text", "small")])))], SqliteContext);
            long pagesAfterDelete, freeAfterDelete;
            using (var before = RawSqlite(path, readOnly: true))
            {
                pagesAfterDelete = RawScalar(before, "SELECT page_count AS value FROM pragma_page_count() ");
                freeAfterDelete = RawScalar(before, "SELECT freelist_count AS value FROM pragma_freelist_count() ");
            }
            Check(freeAfterDelete > 0, "pages were released");
            await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, JsonValue.Object([new("text", large)])))], SqliteContext);
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
                await storage.CommitAsync([new EntryWrite(SqliteEntry(await storage.MintIdAsync<EntryId>(), ConversationId.Root,
                    JsonValue.Object([new("index", index), new("text", new string('x', 1_024))])))], SqliteContext);
            }
            var documentId = await storage.MintIdAsync<DocumentId>();
            await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate
            {
                Id = documentId, Kind = "size.history", Scope = DocumentScope.Conversation(ConversationId.Root),
                History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            }, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))], SqliteContext);
            for (var count = 1; count <= 100; count++)
                await storage.CommitAsync([new DocumentChangeWrite(documentId, DocumentContent.Delta(1, Ops($"[[\"s\",[\"count\"],{count}]]")))], SqliteContext);
            await storage.CloseAsync(SqliteContext);
            Check(new FileInfo(path).Length < 1024 * 1024, $"file size {new FileInfo(path).Length}");
        });
    }

    private static EntryRecord SqliteEntry(EntryId id, ConversationId conversationId, JsonValue? data = null) =>
        new() { Id = id, ConversationId = conversationId, Kind = "message", Data = data };

    private static Task<Seq> CreateSqliteRoot(IStorage storage) =>
        storage.CommitAsync([new ConversationWrite(new ConversationRecord(ConversationId.Root))], SqliteContext);

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

        public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
        {
            if (closed) return await current.CommitAsync(writes, context);
            try
            {
                return await current.CommitAsync(writes, context);
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

    /// <summary>Fails the first statement inside a transaction that <paramref name="fails"/> matches, after the earlier statements ran.</summary>
    private sealed class FailingSqliteDatabase(ISqliteDatabase inner, Func<string, bool> fails) : ISqliteDatabase
    {
        public const string Message = "injected statement failure";

        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] parameters) => inner.RunAsync(sql, parameters);
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
            public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters) => inner.GetAsync(sql, parameters);
            public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters) => inner.AllAsync(sql, parameters);
        }
    }
}
