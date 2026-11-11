// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/durable/src/storage/sqlite/commit.ts, packages/durable/test/sqlite-commit.test.ts.
// Authored native regression; not an upstream execution capture.
// Pi v1.5.2 bounded-commit regressions, adapted from packages/durable/test/sqlite-commit.test.ts.
// Physical rows are compared as well as public values: pending writes must preserve all retained history.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SqliteDetailedBulkCases()
    {
        yield return Case("storage.sqlite.bulk.eight-statements-for-one-or-150-delta-entry-batches", async () =>
        {
            string[]? firstShape = null;
            foreach (var count in new[] { 1, 150 })
            {
                var database = new RecordingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
                var storage = await SqliteStorage.OpenAsync(database, new() { Prefix = "shape_" });
                try
                {
                    await CreateSqliteRoot(storage);
                    await storage.CommitWritesAsync(Enumerable.Range(0, count).Select(index => (StorageWrite)new DocumentCreateWrite(
                        new() { Id = new DocumentId(10_000 + index), Kind = "shape", Key = index.ToString(), Scope = DocumentScope.Session },
                        DocumentContent.Base(1, (JsonObjectValue)J("{\"n\":0}")))).ToArray(), SqliteContext);
                    database.Statements.Clear();
                    var batches = Enumerable.Range(0, count).Select(index => (IReadOnlyList<StorageWrite>)[
                        new DocumentChangeWrite(new DocumentId(10_000 + index), DocumentContent.Delta(1, Ops("[[\"s\",[\"n\"],1]]"))),
                        new EntryWrite(SqliteEntry(new EntryId(index + 2), ConversationId.Root))]).ToArray();
                    var result = await storage.CommitAsync(batches, (_, _) => { }, SqliteContext);
                    Equal(count, result.Stored, "accepted delta/entry batches");
                    Equal(8, database.Statements.Count, "exact bounded statement count");
                    var shape = database.Statements.Select(statement => statement.Sql).ToArray();
                    if (firstShape is null) firstShape = shape;
                    else Equal(string.Join("\n", firstShape), string.Join("\n", shape), "SQL shape independent of batch count");
                    Check(!shape.Any(sql => sql.Contains("AS address", StringComparison.Ordinal)), "changes do not read current addresses");
                    await CheckBulkQueryPlans(database);
                    Equal((long)count, (await database.GetAsync("SELECT count(*) AS n FROM shape_entries"))!.GetInt64("n"), "all entries stored");
                }
                finally { await storage.CloseAsync(SqliteContext); }
            }
        });

        yield return Case("storage.sqlite.bulk.small-rows-split-at-512-kib", async () =>
        {
            var database = new RecordingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
            var storage = await SqliteStorage.OpenAsync(database);
            try
            {
                await CreateSqliteRoot(storage);
                database.Statements.Clear();
                var text = new string('x', 7_000);
                await storage.CommitWritesAsync(Enumerable.Range(2, 100).Select(id => (StorageWrite)new EntryWrite(
                    SqliteEntry(new EntryId(id), ConversationId.Root, JsonValue.String(text)))).ToArray(), SqliteContext);
                Equal(6, database.Statements.Count, "metadata read, IDs, claims, two entry arrays, metadata update");
                var inserts = database.Statements.Where(statement => statement.Sql.StartsWith("INSERT INTO entries ", StringComparison.Ordinal)).ToArray();
                Equal(2, inserts.Length, "two entry JSON arrays");
                foreach (var insert in inserts)
                {
                    Equal(1, insert.Values.Length, "single JSON parameter");
                    Check(((string)insert.Values[0]!).Length <= 512 * 1024, "JSON array stays inside character cap");
                }
                for (var id = 2; id < 102; id++)
                    Equal(text, ((JsonStringValue)(await storage.GetEntryAsync(new EntryId(id), SqliteContext))!.Entry.Data!).Value, "small entry round trip");
                await CheckBulkQueryPlans(database);
            }
            finally { await storage.CloseAsync(SqliteContext); }
        });

        yield return Case("storage.sqlite.bulk.large-rows-use-16-2-1-bound-groups", async () =>
        {
            var database = new RecordingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
            var storage = await SqliteStorage.OpenAsync(database);
            try
            {
                await CreateSqliteRoot(storage);
                database.Statements.Clear();
                var text = string.Concat(Enumerable.Repeat("\"\\\n", 4_000)) + "\ud800";
                await storage.CommitWritesAsync(Enumerable.Range(2, 19).Select(id => (StorageWrite)new EntryWrite(
                    SqliteEntry(new EntryId(id), ConversationId.Root, JsonValue.String(text)))).ToArray(), SqliteContext);
                Equal(7, database.Statements.Count, "exact statement total for 19 large rows");
                var inserts = database.Statements.Where(statement => statement.Sql.StartsWith("INSERT INTO entries ", StringComparison.Ordinal)).ToArray();
                Equal("80,10,5", string.Join(",", inserts.Select(statement => statement.Values.Length)), "power-of-two bound groups");
                Check(inserts.All(statement => statement.Sql.Contains(" VALUES ", StringComparison.Ordinal)), "large records use direct bindings");
                for (var id = 2; id < 21; id++)
                    Equal(text, ((JsonStringValue)(await storage.GetEntryAsync(new EntryId(id), SqliteContext))!.Entry.Data!).Value, "quote-heavy entry round trip");
                await CheckBulkQueryPlans(database);
            }
            finally { await storage.CloseAsync(SqliteContext); }
        });

        foreach (var createFirst in new[] { true, false })
            yield return Case($"storage.sqlite.bulk.replacement-retains-current-{(createFirst ? "create-first" : "retire-first")}", async () =>
            {
                var database = new RecordingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
                var storage = await SqliteStorage.OpenAsync(database);
                try
                {
                    var a = new DocumentCreate { Id = new DocumentId(2), Kind = "replace", Scope = DocumentScope.Session };
                    await storage.CommitWritesAsync([new DocumentCreateWrite(a, DocumentContent.Base(1, (JsonObjectValue)J("{}")))], SqliteContext);
                    StorageWrite create = new DocumentCreateWrite(a with { Id = new DocumentId(3) }, DocumentContent.Base(1, (JsonObjectValue)J("{}")));
                    StorageWrite retire = new DocumentRetireWrite(a.Id);
                    IReadOnlyList<StorageWrite> replace = createFirst ? [create, retire] : [retire, create];
                    var result = await storage.CommitAsync([replace,
                        [new DocumentChangeWrite(new DocumentId(3), DocumentContent.Delta(1, Ops("[[\"s\",[\"n\"],1]]")))],
                        [new DocumentCreateWrite(a with { Id = new DocumentId(4) }, DocumentContent.Base(1, (JsonObjectValue)J("{}")))]], (_, _) => { }, SqliteContext);
                    Equal(2, result.Stored, "replacement and its next change accepted");
                    Equal("Document address already has a current incarnation", result.Refused?.Message, "third incarnation refused");
                    Equal(3L, (await database.GetAsync("SELECT id FROM documents WHERE retired_at IS NULL"))!.GetInt64("id"), "B stays current after A retirement");
                    Equal(0L, (await database.GetAsync("SELECT count(*) AS n FROM record_ids WHERE id = 4"))!.GetInt64("n"), "C claim not written");
                    await CheckBulkQueryPlans(database);
                }
                finally { await storage.CloseAsync(SqliteContext); }
            });

        yield return Case("storage.sqlite.bulk.seeded-physical-and-memory-differential", async () =>
        {
            for (var seed = 0; seed < 12; seed++) await CheckDetailedBulkDifferential(seed);
        });

        foreach (var copy in new[] { false, true })
            yield return Case($"storage.sqlite.bulk.malformed-{(copy ? "copy-source" : "check-record")}-rolls-back-physical-state", async () =>
            {
                var database = new RecordingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
                var storage = await SqliteStorage.OpenAsync(database);
                try
                {
                    await CreateSqliteRoot(storage);
                    var source = BulkDocument(200, false);
                    await storage.CommitWritesAsync([new DocumentCreateWrite(source, DocumentContent.Base(1, (JsonObjectValue)J("{}")))], SqliteContext);
                    // Simulate pre-existing disk corruption past the writer's JSON validity guard.
                    // Restore enforcement before the commit under test so new writes remain checked.
                    await database.ExecAsync("PRAGMA ignore_check_constraints = ON");
                    try { await database.RunAsync("UPDATE documents SET record = ? WHERE id = 200", "{broken"); }
                    finally { await database.ExecAsync("PRAGMA ignore_check_constraints = OFF"); }
                    var before = await BulkPhysicalState(database, "");
                    StorageWrite failing = copy
                        ? new DocumentCopyWrite(source with { Id = new DocumentId(201), Scope = DocumentScope.Conversation(new ConversationId(50)) },
                            new(source.Id, DocumentPoint.Current))
                        : new DocumentChangeWrite(source.Id, DocumentContent.Base(1, (JsonObjectValue)J("{}")));
                    var receipts = new List<int>();
                    Equal("rejected", await SqliteCommitOutcome(storage.CommitAsync([
                        [new EntryWrite(SqliteEntry(new EntryId(100), ConversationId.Root))], [failing]],
                        (index, _) => receipts.Add(index), SqliteContext)), "malformed stored JSON rejects whole call");
                    Equal(0, receipts.Count, "no receipts before transaction success");
                    Equal(before, await BulkPhysicalState(database, ""), "all physical tables rolled back");
                }
                finally { await storage.CloseAsync(SqliteContext); }
            });
    }

    private static DocumentCreate BulkDocument(long id, bool latest) => new()
    {
        Id = new DocumentId(id), Kind = latest ? "latest\ud800" : "rewind\ud800", Key = "key\ud800",
        Scope = DocumentScope.Conversation(ConversationId.Root),
        History = latest ? DocumentHistory.Latest : DocumentHistory.Rewindable,
        Fork = latest ? DocumentFork.Current : DocumentFork.AsOf,
    };

    private static async Task CheckDetailedBulkDifferential(int seed)
    {
        var prefix = seed % 2 == 0 ? "physical_" : "";
        var groupedDb = new RecordingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
        var singleDb = await SqliteConnectionDatabase.OpenAsync(":memory:");
        var grouped = await SqliteStorage.OpenAsync(groupedDb, new() { Prefix = prefix.Length == 0 ? null : prefix });
        var single = await SqliteStorage.OpenAsync(singleDb, new() { Prefix = prefix.Length == 0 ? null : prefix });
        var memory = new MemoryStorage();
        try
        {
            var rewind = BulkDocument(200, false);
            var latest = BulkDocument(201, true);
            StorageWrite[] initial = [new ConversationWrite(new(ConversationId.Root)), new ConversationWrite(new(new ConversationId(50))),
                new DocumentCreateWrite(rewind, DocumentContent.Base(1, (JsonObjectValue)J("{\"n\":0}"))),
                new DocumentCreateWrite(latest, DocumentContent.Base(1, (JsonObjectValue)J("{\"n\":0}")))];
            foreach (var storage in new IStorage[] { grouped, single, memory }) await storage.CommitWritesAsync(initial, SqliteContext);
            var random = new Random(seed);
            var batches = new List<IReadOnlyList<StorageWrite>>();
            // Exercise every table and both kinds of document history before varying the order and values.
            batches.Add([new ConversationWrite(new(new ConversationId(51)))]);
            for (var index = 0; index < 30; index++)
            {
                var n = random.Next(10_000);
                var task = PendingSqliteTask(new TaskId(100 + index % 3)) with { Background = index % 2 == 0, Kind = "task\ud800" + n };
                var submission = new SubmissionRecord
                {
                    Id = new SubmissionId(110 + index % 3), ConversationId = ConversationId.Root, Type = SubmissionType.Input,
                    Status = index % 2 == 0 ? SubmissionStatus.Queued : SubmissionStatus.Unanswered,
                    RequestId = "request\ud800" + index % 3, Reason = index % 2 == 0 ? null : "finished " + n,
                };
                var document = index % 2 == 0 ? rewind : latest;
                DocumentContent content = random.Next(3) == 0
                    ? DocumentContent.Base(1, (JsonObjectValue)J($"{{\"n\":{n}}}"))
                    : DocumentContent.Delta(1, Ops($"[[\"s\",[\"n\"],{n}]]"));
                batches.Add([new EntryWrite(SqliteEntry(new EntryId(1_000 + index), ConversationId.Root, JsonValue.Number(n))),
                    new TaskWrite(task), new SubmissionWrite(submission), new DocumentChangeWrite(document.Id, content)]);
            }
            // Copies force a pending flush and must read the latest accepted change, or a historical revision as requested.
            var copiedRewind = rewind with { Id = new DocumentId(210), Scope = DocumentScope.Conversation(new ConversationId(50)) };
            var copiedLatest = latest with { Id = new DocumentId(211), Scope = DocumentScope.Conversation(new ConversationId(50)) };
            batches.Add([new DocumentCopyWrite(copiedRewind, new(rewind.Id, DocumentPoint.Current)),
                new DocumentCopyWrite(copiedLatest, new(latest.Id, DocumentPoint.Current))]);
            batches.Add([new DocumentCopyWrite(rewind with { Id = new DocumentId(212), Scope = DocumentScope.Conversation(new ConversationId(51)) },
                new(rewind.Id, DocumentPoint.At(new Seq(1))))]);
            batches.Add([new DocumentChangeWrite(copiedRewind.Id, DocumentContent.Delta(1, Ops("[[\"s\",[\"copy\"],true]]"))),
                new DocumentChangeWrite(copiedLatest.Id, DocumentContent.Base(2, (JsonObjectValue)J("{\"copy\":true}")))]);
            StorageWrite replacement = new DocumentCreateWrite(latest with { Id = new DocumentId(220) }, DocumentContent.Base(1, (JsonObjectValue)J("{\"replacement\":true}")));
            StorageWrite retirement = new DocumentRetireWrite(latest.Id);
            batches.Add(seed % 2 == 0 ? [replacement, retirement] : [retirement, replacement]);
            batches.Add([new DocumentChangeWrite(new DocumentId(220), DocumentContent.Delta(1, Ops("[[\"s\",[\"n\"],99]]"))),
                new DocumentRetireWrite(copiedLatest.Id)]);
            IReadOnlyList<StorageWrite> refusal = (seed % 6) switch
            {
                0 => [new ConversationWrite(new(ConversationId.Root))],
                1 => [new DocumentChangeWrite(latest.Id, DocumentContent.Base(1, (JsonObjectValue)J("{}")))],
                2 => [new DocumentChangeWrite(rewind.Id, DocumentContent.Delta(2, Ops("[]")))],
                3 => [new DocumentCopyWrite(rewind with { Id = new DocumentId(230), Scope = DocumentScope.Conversation(new ConversationId(99)) },
                        new(rewind.Id, DocumentPoint.Current)), new DocumentRetireWrite(rewind.Id)],
                4 => [new DocumentRetireWrite(new DocumentId(999))],
                _ => [new DocumentCreateWrite(latest with { Id = new DocumentId(230) }, DocumentContent.Base(1, (JsonObjectValue)J("{}")))],
            };
            var acceptedCount = batches.Count;
            batches.Add(refusal);
            batches.Add([new EntryWrite(SqliteEntry(new EntryId(9_999), ConversationId.Root))]);
            var groupedReceipts = new List<string>();
            var memoryReceipts = new List<string>();
            var singleReceipts = new List<string>();
            var actual = await grouped.CommitAsync(batches, (index, seq) => groupedReceipts.Add($"{index}:{seq.Value}"), SqliteContext);
            var reference = await memory.CommitAsync(batches, (index, seq) => memoryReceipts.Add($"{index}:{seq.Value}"), SqliteContext);
            CommitResult? lastSingle = null;
            for (var index = 0; index < batches.Count; index++)
            {
                lastSingle = await single.CommitAsync([batches[index]], (_, seq) => singleReceipts.Add($"{index}:{seq.Value}"), SqliteContext);
                if (lastSingle.Refused is not null) break;
            }
            Equal(acceptedCount, actual.Stored, $"seed {seed}: intended accepted prefix");
            Equal(reference.Stored, actual.Stored, $"seed {seed}: memory prefix");
            if (seed % 6 == 3)
            {
                // Upstream Memory resolves copies first and wraps its source error; SQLite checks
                // same-batch sources before resolving copies and intentionally exposes that check.
                Equal("Document copy 230 was rejected", reference.Refused?.Message, "memory copy refusal wrapper");
                Equal("Fork source document 200 is changed in the copy batch", reference.Refused?.InnerException?.Message, "memory copy refusal cause");
                Equal("Document copy 230 source is changed in the copy batch", actual.Refused?.Message, "SQLite copy refusal check");
            }
            else Equal(reference.Refused?.Message, actual.Refused?.Message, $"seed {seed}: memory refusal");
            Equal(lastSingle!.Refused?.Message, actual.Refused?.Message, $"seed {seed}: individual refusal");
            Equal(string.Join(",", memoryReceipts), string.Join(",", groupedReceipts), "memory receipts");
            Equal(string.Join(",", singleReceipts), string.Join(",", groupedReceipts), "individual receipts");
            Equal(await BulkPhysicalState(singleDb, prefix), await BulkPhysicalState(groupedDb, prefix), $"seed {seed}: every physical table");
            foreach (var id in new long[] { 200, 201, 210, 211, 212, 220, 230 })
            {
                var expected = await memory.GetDocumentAsync(new DocumentId(id), DocumentPoint.Current, SqliteContext);
                var found = await grouped.GetDocumentAsync(new DocumentId(id), DocumentPoint.Current, SqliteContext);
                Equal(expected?.Record.ToJson().ToJson(), found?.Record.ToJson().ToJson(), "document record");
                Equal(expected?.Value.ToJson(), found?.Value.ToJson(), "document value");
            }
            for (var index = 0; index < 30; index++)
                Equal((await memory.GetEntryAsync(new EntryId(1_000 + index), SqliteContext))!.Entry.ToJson().ToJson(),
                    (await grouped.GetEntryAsync(new EntryId(1_000 + index), SqliteContext))!.Entry.ToJson().ToJson(), "entry value");
            for (var index = 0; index < 3; index++)
            {
                Equal((await memory.GetTaskAsync(new TaskId(100 + index), SqliteContext))!.ToJson().ToJson(),
                    (await grouped.GetTaskAsync(new TaskId(100 + index), SqliteContext))!.ToJson().ToJson(), "last task wins");
                Equal((await memory.GetSubmissionAsync(new SubmissionId(110 + index), SqliteContext))!.ToJson().ToJson(),
                    (await grouped.GetSubmissionAsync(new SubmissionId(110 + index), SqliteContext))!.ToJson().ToJson(), "last submission wins");
            }
            await CheckBulkQueryPlans(groupedDb);
        }
        finally { await grouped.CloseAsync(SqliteContext); await single.CloseAsync(SqliteContext); await memory.CloseAsync(SqliteContext); }
    }

    private static async Task<string> BulkPhysicalState(ISqliteDatabase database, string prefix)
    {
        var snapshots = new List<string>();
        foreach (var (table, order) in new[] { ("durable_metadata", "singleton"), ("record_ids", "id"), ("conversations", "id"),
            ("entries", "id"), ("tasks", "id"), ("submissions", "id"), ("documents", "id"), ("document_revisions", "document_id, seq") })
            snapshots.Add(table + ":" + string.Join("\n", (await database.AllAsync($"SELECT * FROM {prefix}{table} ORDER BY {order}")).Select(row => row.ToString())));
        return string.Join("\n", snapshots);
    }

    private static async Task CheckBulkQueryPlans(RecordingBulkDatabase database)
    {
        foreach (var statement in database.Statements.Where(statement => statement.Sql.Contains("json_each(?)", StringComparison.Ordinal))
            .DistinctBy(statement => statement.Sql))
        {
            var plan = await database.AllAsync("EXPLAIN QUERY PLAN " + statement.Sql, statement.Values);
            foreach (var row in plan)
            {
                var detail = row.GetString("detail");
                Check(!detail.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase) || detail.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase),
                    "bulk query must probe stored tables through indexes: " + detail + " in " + statement.Sql);
            }
        }
    }

    private sealed record BulkStatement(string Sql, object?[] Values);

    private sealed class RecordingBulkDatabase(ISqliteDatabase inner) : ISqliteDatabase
    {
        public List<BulkStatement> Statements { get; } = [];
        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] values) => inner.RunAsync(sql, values);
        public Task<long> RunCountedAsync(string sql, params object?[] values) => inner.RunCountedAsync(sql, values);
        public Task<SqliteRow?> GetAsync(string sql, params object?[] values) => inner.GetAsync(sql, values);
        public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] values) => inner.AllAsync(sql, values);
        public Task CloseAsync() => inner.CloseAsync();
        public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback) => inner.TransactionAsync(tx => callback(new Executor(tx, Statements)));
        private sealed class Executor(ISqliteExecutor inner, List<BulkStatement> statements) : ISqliteExecutor
        {
            private void Record(string sql, object?[] values) => statements.Add(new(sql, [.. values]));
            public Task ExecAsync(string sql) { Record(sql, []); return inner.ExecAsync(sql); }
            public Task RunAsync(string sql, params object?[] values) { Record(sql, values); return inner.RunAsync(sql, values); }
            public Task<long> RunCountedAsync(string sql, params object?[] values) { Record(sql, values); return inner.RunCountedAsync(sql, values); }
            public Task<SqliteRow?> GetAsync(string sql, params object?[] values) { Record(sql, values); return inner.GetAsync(sql, values); }
            public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] values) { Record(sql, values); return inner.AllAsync(sql, values); }
        }
    }
}
