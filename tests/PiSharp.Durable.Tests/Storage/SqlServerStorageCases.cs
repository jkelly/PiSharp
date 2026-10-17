// Authored (decision 0007): SQL Server storage has no upstream, so these cases have no port-map row. They run only when
// PISHARP_DURABLE_MSSQL_CONNECTION_STRING names a SQL Server the tests may create a database on; otherwise the groups are empty. The run
// creates one database (MssqlServer), gives every case its own store (or its own schema, for cases that change tables), and drops the
// database when the run ends.
// - storage.mssql.conformance.* and storage.mssql.reopen.conformance.* run the storage conformance kit directly and through a storage that
//   closes and reopens the store after every commit, as the SQLite groups do.
// - session.mssql.*, harness.tasks.mssql.*, harness.events.mssql.*, harness.ownership.mssql.* and harness.tools-recovery.mssql.* rerun
//   those storage-agnostic suites on a fresh store per case; the ownership and recovery suites also reopen the same store.
// - storage.mssql.* cover what is particular to the backend: persistence across reopen, rollback of a statement failure inside a commit,
//   corrupt rows, the store lock, store and schema isolation, deletion, schema versions, and records that SQL Server's JSON functions refuse.
using Microsoft.Data.SqlClient;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.MSSQL;
using PiSharp.Durable.Testing;

internal static partial class Program
{
    private static readonly DurableContext MssqlContext = DurableContext.Background;

    private static IEnumerable<(string, Func<Task>)> StorageMssqlCases()
    {
        if (!MssqlServer.Configured) yield break;

        foreach (var testCase in StorageConformance.Create(async use =>
        {
            var storage = await MssqlServer.OpenAsync();
            try
            {
                await use(storage);
            }
            finally
            {
                await storage.CloseAsync(MssqlContext);
            }
        }))
            yield return Case("storage.mssql.conformance." + testCase.Slug, testCase.Run);

        foreach (var testCase in StorageConformance.Create(async use =>
        {
            var store = MssqlServer.NewStore();
            await (await MssqlServer.OpenAsync(store)).CloseAsync(MssqlContext);
            var storage = new ReopeningMssqlStorage(await MssqlServer.OpenAsync(store), () => MssqlServer.OpenAsync(store));
            try
            {
                await use(storage);
            }
            finally
            {
                await storage.CloseAsync(MssqlContext);
            }
        }))
            yield return Case("storage.mssql.reopen.conformance." + testCase.Slug, testCase.Run);

        yield return Case("storage.mssql.persists-records-sequence-allocation-and-global-id-allocation-across-reopen", async () =>
        {
            var store = MssqlServer.NewStore();
            var storage = await MssqlServer.OpenAsync(store);
            Equal(Ids.SeqFromNumber(1), await CreateMssqlRoot(storage), "root sequence");
            var entryId = await storage.MintIdAsync<EntryId>();
            Equal(Ids.SeqFromNumber(2), await storage.CommitAsync([new EntryWrite(MssqlEntry(entryId))], MssqlContext), "entry sequence");
            await storage.CloseAsync(MssqlContext);

            var reopened = await MssqlServer.OpenAsync(store);
            try
            {
                Equal<CommittedEntry?>(new CommittedEntry(MssqlEntry(entryId), Ids.SeqFromNumber(2)), await reopened.GetEntryAsync(entryId, MssqlContext),
                    "entry after reopen");
                Equal(entryId.Value + 1, (await reopened.MintIdAsync<EntryId>()).Value, "minted ID after reopen");
                Equal(Ids.SeqFromNumber(3), await reopened.CommitAsync([new TaskWrite(PendingMssqlTask(await reopened.MintIdAsync<TaskId>()))], MssqlContext),
                    "task sequence");
            }
            finally
            {
                await reopened.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.rolls-sql-rows-and-sequence-allocation-back-as-one-transaction", async () =>
        {
            // A trigger fails the task insert after the entry insert of the same commit ran: a real statement failure mid-transaction.
            const string message = "injected statement failure";
            var schema = MssqlServer.NewSchema();
            var store = MssqlServer.NewStore();
            var storage = await MssqlServer.OpenAsync(store, new SqlServerStorageOptions { Schema = schema });
            try
            {
                await CreateMssqlRoot(storage);
                var quoted = MssqlServer.Quote(schema);
                await MssqlServer.ExecAsync($"CREATE TRIGGER {quoted}.fail_task_insert ON {quoted}.durable_tasks AFTER INSERT AS THROW 50000, N'{message}', 1;");
                var transientId = await storage.MintIdAsync<EntryId>();
                var transientTaskId = await storage.MintIdAsync<TaskId>();
                var error = await ThrowsAsync<SqlException>(() => storage.CommitAsync(
                    [new EntryWrite(MssqlEntry(transientId)), new TaskWrite(PendingMssqlTask(transientTaskId))], MssqlContext), "failing commit");
                Equal(message, error.Message, "failure");
                Equal<CommittedEntry?>(null, await storage.GetEntryAsync(transientId, MssqlContext), "rolled-back entry");
                Equal<TaskRecord?>(null, await storage.GetTaskAsync(transientTaskId, MssqlContext), "rolled-back task");
                var committedId = await storage.MintIdAsync<EntryId>();
                Equal(Ids.SeqFromNumber(2), await storage.CommitAsync([new EntryWrite(MssqlEntry(committedId))], MssqlContext), "next sequence");

                var storeId = await MssqlServer.StoreIdAsync(schema, store);
                Equal(1L, await MssqlServer.ScalarAsync($"SELECT count(*) FROM {quoted}.durable_entries WHERE store_id = @p0", storeId), "entry rows");
                Equal(0L, await MssqlServer.ScalarAsync($"SELECT count(*) FROM {quoted}.durable_record_ids WHERE store_id = @p0 AND id IN (@p1, @p2)",
                    storeId, transientId.Value, transientTaskId.Value), "claimed IDs of the failed commit");
                Equal(3L, await MssqlServer.ScalarAsync($"SELECT next_seq FROM {quoted}.durable_stores WHERE store_id = @p0", storeId), "next_seq");
            }
            finally
            {
                await storage.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.rejects-a-document-whose-required-base-is-missing", async () =>
        {
            var store = MssqlServer.NewStore();
            var storage = await MssqlServer.OpenAsync(store);
            try
            {
                await CreateMssqlRoot(storage);
                var id = await storage.MintIdAsync<DocumentId>();
                await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "corrupt", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, (JsonObjectValue)J("{\"retained\":true}")))], MssqlContext);
                await MssqlServer.ExecAsync("DELETE FROM dbo.durable_document_revisions WHERE store_id = @p0 AND document_id = @p1",
                    await MssqlServer.StoreIdAsync(SqlServerMigrations.DefaultSchema, store), id.Value);
                Equal($"Document {id} is missing a required base",
                    (await ThrowsAsync<InvalidOperationException>(() => storage.GetDocumentAsync(id, DocumentPoint.Current, MssqlContext), "read")).Message,
                    "message");
            }
            finally
            {
                await storage.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.replays-root-replacements-and-follow-up-edits-while-rejecting-corrupt-operations", async () =>
        {
            var store = MssqlServer.NewStore();
            var storage = await MssqlServer.OpenAsync(store);
            try
            {
                await CreateMssqlRoot(storage);
                var id = await storage.MintIdAsync<DocumentId>();
                await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "replay", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, (JsonObjectValue)J("{\"nested\":{\"value\":1},\"rows\":[]}")))], MssqlContext);
                await storage.CommitAsync([new DocumentChangeWrite(id,
                    DocumentContent.Delta(1, Ops("[[\"r\",{\"nested\":{\"value\":2},\"rows\":[{\"id\":1}]}]]")))], MssqlContext);
                await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1,
                    Ops("[[\"s\",[\"nested\",\"value\"],3],[\"p\",[\"rows\"],1,0,[{\"id\":2}]],[\"m\",[\"rows\"],[1,0]]]")))], MssqlContext);
                await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"nested\",\"value\"],4]]")))], MssqlContext);
                Same("{\"nested\":{\"value\":4},\"rows\":[{\"id\":2},{\"id\":1}]}",
                    (await storage.GetDocumentAsync(id, DocumentPoint.Current, MssqlContext))!.Value, "replayed value");

                await MssqlServer.ExecAsync("""
                    UPDATE dbo.durable_document_revisions SET content = @p2 WHERE store_id = @p0 AND document_id = @p1 AND seq =
                    (SELECT max(seq) FROM dbo.durable_document_revisions WHERE store_id = @p0 AND document_id = @p1)
                    """, await MssqlServer.StoreIdAsync(SqlServerMigrations.DefaultSchema, store), id.Value, "[[\"unknown\"]]");
                var error = await ThrowsAsync<Exception>(() => storage.GetDocumentAsync(id, DocumentPoint.Current, MssqlContext), "corrupt read");
                Check(error.Message.Contains("unknown op verb", StringComparison.Ordinal), "corrupt operation message: " + error.Message);
            }
            finally
            {
                await storage.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.reconstructs-recent-and-ancient-rewindable-points-after-reopen", async () =>
        {
            var store = MssqlServer.NewStore();
            var storage = await MssqlServer.OpenAsync(store);
            await CreateMssqlRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            var record = new DocumentCreate
            {
                Id = id, Kind = "history", Scope = DocumentScope.Conversation(ConversationId.Root),
                History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            var createdAt = await storage.CommitAsync([new DocumentCreateWrite(record, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))],
                MssqlContext);
            var ancientAt = createdAt;
            var recentAt = createdAt;
            for (var count = 1; count <= 40; count++)
            {
                recentAt = await storage.CommitAsync([new DocumentChangeWrite(id, count == 20
                    ? DocumentContent.Base(1, (JsonObjectValue)J($"{{\"count\":{count}}}"))
                    : DocumentContent.Delta(1, Ops($"[[\"s\",[\"count\"],{count}]]")))], MssqlContext);
                if (count == 5) ancientAt = recentAt;
            }
            await storage.CloseAsync(MssqlContext);
            var reopened = await MssqlServer.OpenAsync(store);
            try
            {
                Same("{\"count\":5}", (await reopened.GetDocumentAsync(id, ancientAt, MssqlContext))!.Value, "ancient point");
                Same("{\"count\":40}", (await reopened.GetDocumentAsync(id, recentAt, MssqlContext))!.Value, "recent point");
            }
            finally
            {
                await reopened.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.reclaims-current-only-revisions-only-after-a-base-or-retirement", async () =>
        {
            var store = MssqlServer.NewStore();
            var storage = await MssqlServer.OpenAsync(store);
            try
            {
                await CreateMssqlRoot(storage);
                var storeId = await MssqlServer.StoreIdAsync(SqlServerMigrations.DefaultSchema, store);
                var id = await storage.MintIdAsync<DocumentId>();
                Task<long> Revisions() =>
                    MssqlServer.ScalarAsync("SELECT count(*) FROM dbo.durable_document_revisions WHERE store_id = @p0 AND document_id = @p1", storeId, id.Value);
                await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "latest", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":0}")))], MssqlContext);
                for (var count = 1; count <= 10; count++)
                    await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops($"[[\"s\",[\"count\"],{count}]]")))], MssqlContext);
                Equal(11L, await Revisions(), "revisions after deltas");
                await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, (JsonObjectValue)J("{\"count\":11}")))], MssqlContext);
                Equal(1L, await Revisions(), "revisions after a base");
                await storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"r\",{\"count\":12}]]")))], MssqlContext);
                Equal(2L, await Revisions(), "revisions after one more delta");
                await storage.CommitAsync([new DocumentRetireWrite(id)], MssqlContext);
                Equal(0L, await Revisions(), "revisions after retirement");
            }
            finally
            {
                await storage.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.stores-large-and-deeply-nested-records", async () =>
        {
            // SQL Server's JSON functions refuse text nested deeper than 128 levels; the schema has no ISJSON check, so such records store.
            var storage = await MssqlServer.OpenAsync();
            try
            {
                await CreateMssqlRoot(storage);
                var large = JsonValue.Object([new("text", new string('x', 3 * 1024 * 1024) + "é\ud800")]);
                var entryId = await storage.MintIdAsync<EntryId>();
                await storage.CommitAsync([new EntryWrite(MssqlEntry(entryId, large))], MssqlContext);
                Same(large.ToJson(), (await storage.GetEntryAsync(entryId, MssqlContext))!.Entry.Data!, "large entry data");

                var nested = J(new string('[', 200) + "1" + new string(']', 200));
                var id = await storage.MintIdAsync<DocumentId>();
                await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "deep", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, JsonValue.Object([new("nested", nested)])))], MssqlContext);
                Same(JsonValue.Object([new("nested", nested)]).ToJson(), (await storage.GetDocumentAsync(id, DocumentPoint.Current, MssqlContext))!.Value,
                    "deeply nested document");
            }
            finally
            {
                await storage.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.locks-a-store-to-one-storage-until-close", async () =>
        {
            var store = MssqlServer.NewStore();
            var first = await MssqlServer.OpenAsync(store);
            await CreateMssqlRoot(first);
            var busy = await ThrowsAsync<InvalidOperationException>(() => MssqlServer.OpenAsync(store, new SqlServerStorageOptions { LockTimeoutMs = 0 }),
                "second open");
            Equal($"Durable SQL Server store \"{store}\" is open in another storage", busy.Message, "busy message");
            await ThrowsAsync<InvalidOperationException>(async () => await SqlServerStorage.DeleteStoreAsync(await MssqlServer.ConnectionStringAsync(), store,
                new SqlServerStorageOptions { LockTimeoutMs = 0 }), "delete while open");

            // A waiting open succeeds once the owner closes.
            var waiting = MssqlServer.OpenAsync(store, new SqlServerStorageOptions { LockTimeoutMs = 30_000 });
            await Task.Delay(300);
            Check(!waiting.IsCompleted, "the second open waits for the lock");
            await first.CloseAsync(MssqlContext);
            var second = await waiting;
            try
            {
                Equal<ConversationRecord?>(new ConversationRecord(ConversationId.Root), await second.GetConversationAsync(ConversationId.Root, MssqlContext),
                    "root seen by the next owner");
            }
            finally
            {
                await second.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.locks-a-store-under-every-spelling-of-its-schema", async () =>
        {
            // The run's database is case-insensitive, so "dbo" and "DBO" name one schema. Application lock names compare byte for byte, so the
            // store lock must be keyed by the schema SQL Server resolves, not by the caller's spelling.
            var schema = MssqlServer.NewSchema();
            foreach (var (first, second) in new[] { ("dbo", "DBO"), (schema, schema.ToUpperInvariant()) })
            {
                var store = MssqlServer.NewStore();
                var owner = await MssqlServer.OpenAsync(store, new SqlServerStorageOptions { Schema = first });
                try
                {
                    await CreateMssqlRoot(owner);
                    var busy = await ThrowsAsync<InvalidOperationException>(() => MssqlServer.OpenAsync(store,
                        new SqlServerStorageOptions { Schema = second, LockTimeoutMs = 0 }), $"open through {second}");
                    Equal($"Durable SQL Server store \"{store}\" is open in another storage", busy.Message, $"busy message through {second}");
                }
                finally
                {
                    await owner.CloseAsync(MssqlContext);
                }
                var other = await MssqlServer.OpenAsync(store, new SqlServerStorageOptions { Schema = second, LockTimeoutMs = 0 });
                try
                {
                    Equal<ConversationRecord?>(new ConversationRecord(ConversationId.Root), await other.GetConversationAsync(ConversationId.Root, MssqlContext),
                        $"root seen through {second}");
                }
                finally
                {
                    await other.CloseAsync(MssqlContext);
                }
            }
        });

        yield return Case("storage.mssql.keeps-stores-and-schemas-independent", async () =>
        {
            // Store names are compared exactly: a trailing space or a lone surrogate names another store. A schema name is quoted wherever
            // it appears.
            var name = MssqlServer.NewStore();
            var schema = MssqlServer.NewSchema() + " o'brien]";
            (string Store, SqlServerStorageOptions? Options)[] locations =
                [(name, null), (name + " ", null), (name + "\ud800", null), (name.ToUpperInvariant(), null), (name, new SqlServerStorageOptions { Schema = schema })];
            var storages = new List<SqlServerStorage>();
            try
            {
                foreach (var (store, options) in locations) storages.Add(await MssqlServer.OpenAsync(store, options));
                for (var index = 0; index < storages.Count; index++)
                {
                    Equal(2L, (await storages[index].MintIdAsync<EntryId>()).Value, $"first ID of store {index}");
                    await CreateMssqlRoot(storages[index]);
                    await storages[index].CommitAsync([new EntryWrite(MssqlEntry(new EntryId(2), JsonValue.Number(index)))], MssqlContext);
                }
                for (var index = 0; index < storages.Count; index++)
                {
                    var page = await storages[index].ScanEntriesAsync(new EntryQuery(ConversationId.Root), 10, null, MssqlContext);
                    Equal(1, page.Items.Length, $"entries of store {index}");
                    Same(index.ToString(System.Globalization.CultureInfo.InvariantCulture), page.Items[0].Data!, $"entry of store {index}");
                }
            }
            finally
            {
                foreach (var storage in storages) await storage.CloseAsync(MssqlContext);
            }
            Equal(1L, await MssqlServer.ScalarAsync("SELECT count(*) FROM sys.schemas WHERE name = @p0", schema), "created schema");
        });

        yield return Case("storage.mssql.deletes-a-store-and-only-that-store", async () =>
        {
            var connectionString = await MssqlServer.ConnectionStringAsync();
            var (kept, deleted) = (MssqlServer.NewStore(), MssqlServer.NewStore());
            foreach (var store in new[] { kept, deleted })
            {
                var storage = await MssqlServer.OpenAsync(store);
                await CreateMssqlRoot(storage);
                await storage.CommitAsync([new EntryWrite(MssqlEntry(await storage.MintIdAsync<EntryId>()))], MssqlContext);
                await storage.CloseAsync(MssqlContext);
            }
            var deletedId = await MssqlServer.StoreIdAsync(SqlServerMigrations.DefaultSchema, deleted);
            Check(await SqlServerStorage.DeleteStoreAsync(connectionString, deleted), "deleted an existing store");
            Check(!await SqlServerStorage.DeleteStoreAsync(connectionString, deleted), "a second delete finds nothing");
            foreach (var table in new[] { "durable_record_ids", "durable_conversations", "durable_entries", "durable_stores" })
                Equal(0L, await MssqlServer.ScalarAsync($"SELECT count(*) FROM dbo.{table} WHERE store_id = @p0", deletedId), $"{table} rows of the deleted store");

            var keptStorage = await MssqlServer.OpenAsync(kept);
            var recreated = await MssqlServer.OpenAsync(deleted);
            try
            {
                Equal(1, (await keptStorage.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 10, null, MssqlContext)).Items.Length,
                    "entries of the kept store");
                Equal<ConversationRecord?>(null, await recreated.GetConversationAsync(ConversationId.Root, MssqlContext), "root of the recreated store");
                Equal(2L, (await recreated.MintIdAsync<EntryId>()).Value, "first ID of the recreated store");
            }
            finally
            {
                await keptStorage.CloseAsync(MssqlContext);
                await recreated.CloseAsync(MssqlContext);
            }
        });

        yield return Case("storage.mssql.rejects-a-newer-schema-version-and-invalid-names", async () =>
        {
            var schema = MssqlServer.NewSchema();
            var options = new SqlServerStorageOptions { Schema = schema };
            await (await MssqlServer.OpenAsync(MssqlServer.NewStore(), options)).CloseAsync(MssqlContext);
            Equal((long)SqlServerMigrations.CurrentSchemaVersion,
                await MssqlServer.ScalarAsync($"SELECT version FROM {MssqlServer.Quote(schema)}.durable_schema"), "recorded version");
            await MssqlServer.ExecAsync($"UPDATE {MssqlServer.Quote(schema)}.durable_schema SET version = 99");
            Equal($"Durable SQL Server schema version 99 is newer than supported version {SqlServerMigrations.CurrentSchemaVersion}",
                (await ThrowsAsync<InvalidOperationException>(() => MssqlServer.OpenAsync(MssqlServer.NewStore(), options), "newer schema")).Message, "message");

            var connectionString = await MssqlServer.ConnectionStringAsync();
            await ThrowsAsync<ArgumentException>(() => SqlServerStorage.OpenAsync(connectionString, ""), "empty store name");
            await ThrowsAsync<ArgumentException>(() => SqlServerStorage.OpenAsync(connectionString, "store", new SqlServerStorageOptions { Schema = "" }),
                "empty schema name");
            await ThrowsAsync<ArgumentException>(() => SqlServerStorage.OpenAsync(connectionString, "store",
                new SqlServerStorageOptions { Schema = new string('s', 129) }), "long schema name");
            await ThrowsAsync<ArgumentOutOfRangeException>(() => SqlServerStorage.OpenAsync(connectionString, "store",
                new SqlServerStorageOptions { LockTimeoutMs = -1 }), "negative lock timeout");
        });
    }

    private static IEnumerable<(string, Func<Task>)> SessionMssqlCases() =>
        MssqlServer.Configured ? SessionTests.All("session.mssql.", () => MssqlServer.OpenAsync().GetAwaiter().GetResult()) : [];

    private static IEnumerable<(string, Func<Task>)> HarnessMssqlCases() =>
        MssqlServer.Configured ? HarnessTests.TaskCases("harness.tasks.mssql.", () => MssqlServer.OpenAsync().GetAwaiter().GetResult()) : [];

    private static IEnumerable<(string, Func<Task>)> HarnessEventsMssqlCases() =>
        MssqlServer.Configured ? EventTests.Cases("harness.events.mssql.", async () => await MssqlServer.OpenAsync()) : [];

    private static IEnumerable<(string, Func<Task>)> HarnessOwnershipMssqlCases() =>
        MssqlServer.Configured ? OwnershipTests.Cases("harness.ownership.mssql.", () => MssqlLocation()(), MssqlLocation) : [];

    private static IEnumerable<(string, Func<Task>)> HarnessToolsRecoveryMssqlCases()
    {
        if (!MssqlServer.Configured) return [];
        return ToolsRecoveryTests.Cases("harness.tools-recovery.mssql.", () =>
        {
            var open = MssqlLocation();
            return () => open().GetAwaiter().GetResult();
        });
    }

    /// <summary>A fresh store: each call of the returned function opens it.</summary>
    private static StructuredTests.OpenLocation MssqlLocation()
    {
        var store = MssqlServer.NewStore();
        return async () => await MssqlServer.OpenAsync(store);
    }

    private static EntryRecord MssqlEntry(EntryId id, JsonValue? data = null) =>
        new() { Id = id, ConversationId = ConversationId.Root, Kind = "message", Data = data };

    private static Task<Seq> CreateMssqlRoot(IStorage storage) =>
        storage.CommitAsync([new ConversationWrite(new ConversationRecord(ConversationId.Root))], MssqlContext);

    private static TaskRecord PendingMssqlTask(TaskId id) => new()
    {
        Id = id, ConversationId = ConversationId.Root, Kind = "test.task", Version = 1, Input = JsonValue.Null,
        State = new PendingTaskState(J("{\"phase\":\"ready\"}")), Background = false, AbortRequested = false,
    };

    /// <summary>Closes and reopens the store after every commit, so every read sees persisted state (the SQLite group's
    /// ReopeningSqliteStorage).</summary>
    private sealed class ReopeningMssqlStorage(IStorage initial, Func<Task<SqlServerStorage>> reopen) : IStorage
    {
        private IStorage current = initial;
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
                await current.CloseAsync(MssqlContext);
                current = await reopen();
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
}

/// <summary>The SQL Server the storage.mssql.* groups use: <c>PISHARP_DURABLE_MSSQL_CONNECTION_STRING</c> names the server and credentials
/// (its database, if any, is only where the test database is created from). The first case creates one database for the run, with SQL Server's
/// default case-insensitive collation whatever the server's is, so that schema names have aliases; <see cref="DropAsync"/> drops it when the run
/// ends.</summary>
internal static class MssqlServer
{
    public const string Variable = "PISHARP_DURABLE_MSSQL_CONNECTION_STRING";

    private static readonly string? Server = Environment.GetEnvironmentVariable(Variable);
    private static readonly Lazy<Task<string>> Database = new(CreateDatabaseAsync);
    private static string? databaseName;

    public static bool Configured => !string.IsNullOrWhiteSpace(Server);

    /// <summary>The connection string of the run's database, created on first use.</summary>
    public static Task<string> ConnectionStringAsync() => Database.Value;

    public static string NewStore() => "test-" + Guid.NewGuid().ToString("N");

    public static string NewSchema() => "durable_test_" + Guid.NewGuid().ToString("N");

    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>Opens <paramref name="store"/> (default: a fresh one) in the run's database.</summary>
    public static async Task<SqlServerStorage> OpenAsync(string? store = null, SqlServerStorageOptions? options = null) =>
        await SqlServerStorage.OpenAsync(await ConnectionStringAsync(), store ?? NewStore(), options);

    /// <summary>The store_id of a store, looked up as the storage does (by its JSON-encoded name).</summary>
    public static Task<long> StoreIdAsync(string schema, string store) =>
        ScalarAsync($"SELECT store_id FROM {Quote(schema)}.durable_stores WHERE name = @p0", JsonValue.String(store).ToJson());

    /// <summary>Runs a batch on a separate connection, with positional bindings @p0, @p1, ...</summary>
    public static async Task ExecAsync(string sql, params object?[] parameters)
    {
        await using var connection = await ConnectAsync(await ConnectionStringAsync());
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The first column of the first row, as a long.</summary>
    public static async Task<long> ScalarAsync(string sql, params object?[] parameters)
    {
        await using var connection = await ConnectAsync(await ConnectionStringAsync());
        await using var command = Command(connection, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Drops the run's database, if one was created.</summary>
    public static async Task DropAsync()
    {
        if (databaseName is null || !Database.IsValueCreated || !Database.Value.IsCompletedSuccessfully) return;
        SqlConnection.ClearAllPools();
        await using var connection = await ConnectAsync(Server!);
        await using var command = Command(connection, $"""
            ALTER DATABASE {Quote(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE {Quote(databaseName)};
            """, []);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> CreateDatabaseAsync()
    {
        var name = $"pisharp_durable_tests_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..56];
        await using (var connection = await ConnectAsync(Server!))
        await using (var command = Command(connection, $"CREATE DATABASE {Quote(name)} COLLATE SQL_Latin1_General_CP1_CI_AS", []))
            await command.ExecuteNonQueryAsync();
        databaseName = name;
        return new SqlConnectionStringBuilder(Server) { InitialCatalog = name }.ConnectionString;
    }

    private static async Task<SqlConnection> ConnectAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static SqlCommand Command(SqlConnection connection, string sql, object?[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < parameters.Length; index++) command.Parameters.AddWithValue("@p" + index, parameters[index] ?? DBNull.Value);
        return command;
    }
}
