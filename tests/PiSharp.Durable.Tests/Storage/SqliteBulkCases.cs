// Authored native regression/performance checks for the upstream bounded group-commit design.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SqliteBulkChecks()
    {
        yield return Case("storage.sqlite.bulk.bounds-statements-for-256-batches", async () =>
        {
            var database = new CountingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
            var storage = await SqliteStorage.OpenAsync(database, new() { Prefix = "bulk_" });
            try
            {
                await CreateSqliteRoot(storage);
                database.Calls = 0;
                IReadOnlyList<IReadOnlyList<StorageWrite>> batches = [.. Enumerable.Range(2, 256).Select(id =>
                    (IReadOnlyList<StorageWrite>)[new EntryWrite(SqliteEntry(new EntryId(id), ConversationId.Root))])];
                var result = await storage.CommitAsync(batches, (_, _) => { }, SqliteContext);
                Equal(256, result.Stored, "stored batches");
                Equal(5, database.Calls, "metadata read, ID read, claim insert, entry insert, metadata write");
                Equal(256L, (await database.GetAsync("SELECT count(*) AS n FROM bulk_entries"))!.GetInt64("n"), "all rows");
            }
            finally { await storage.CloseAsync(SqliteContext); }
        });
        yield return Case("storage.sqlite.bulk.bounds-large-row-bindings-and-preserves-surrogates", async () =>
        {
            var database = new CountingBulkDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
            var storage = await SqliteStorage.OpenAsync(database);
            try
            {
                await CreateSqliteRoot(storage);
                var text = new string('x', 20_000) + "\ud800";
                var writes = Enumerable.Range(2, 40).Select(id => (StorageWrite)new EntryWrite(SqliteEntry(new EntryId(id),
                    ConversationId.Root, JsonValue.String(text)))).ToArray();
                database.Calls = 0;
                await storage.CommitWritesAsync(writes, SqliteContext);
                Check(database.MaxParameters <= 100, "bounded parameter count");
                Check(database.Calls < 15, "large writes still batched");
                Equal(text, ((JsonStringValue)(await storage.GetEntryAsync(new EntryId(2), SqliteContext))!.Entry.Data!).Value, "lossless large record");
            }
            finally { await storage.CloseAsync(SqliteContext); }
        });
        yield return Case("storage.sqlite.bulk.seeded-reference-differential", async () =>
        {
            for (var seed = 0; seed < 24; seed++)
            {
                var memory = new MemoryStorage();
                var sqlite = await SqliteStorage.OpenAsync(await SqliteConnectionDatabase.OpenAsync(":memory:"),
                    new() { Prefix = seed % 2 == 0 ? "diff_" : null });
                try
                {
                    StorageWrite[] initial = [new ConversationWrite(new(ConversationId.Root)),
                        new DocumentCreateWrite(new() { Id = new DocumentId(200), Kind = "d\ud800", Scope = DocumentScope.Session },
                            DocumentContent.Base(1, (JsonObjectValue)J("{\"n\":0}")))];
                    await memory.CommitWritesAsync(initial, SqliteContext);
                    await sqlite.CommitWritesAsync(initial, SqliteContext);
                    var random = new Random(seed);
                    var batches = new List<IReadOnlyList<StorageWrite>>();
                    for (var index = 0; index < 30; index++)
                    {
                        var n = random.Next(1000);
                        StorageWrite write = random.Next(4) switch
                        {
                            0 => new EntryWrite(SqliteEntry(new EntryId(index + 2), ConversationId.Root, JsonValue.Number(n))),
                            1 => new TaskWrite(PendingSqliteTask(new TaskId(100 + random.Next(4)))),
                            2 => new DocumentChangeWrite(new DocumentId(200), DocumentContent.Base(1, (JsonObjectValue)J($"{{\"n\":{n}}}"))),
                            _ => new DocumentChangeWrite(new DocumentId(200), DocumentContent.Delta(1, Ops($"[[\"s\",[\"n\"],{n}]]"))),
                        };
                        batches.Add([write]);
                    }
                    batches.Insert(20 + seed % 10, [new ConversationWrite(new(ConversationId.Root))]);
                    var leftReceipts = new List<string>(); var rightReceipts = new List<string>();
                    var left = await memory.CommitAsync(batches, (i, seq) => leftReceipts.Add($"{i}:{seq.Value}"), SqliteContext);
                    var right = await sqlite.CommitAsync(batches, (i, seq) => rightReceipts.Add($"{i}:{seq.Value}"), SqliteContext);
                    Equal(left.Stored, right.Stored, "accepted prefix");
                    Equal(left.Refused?.Message, right.Refused?.Message, "refusal");
                    Equal(string.Join(",", leftReceipts), string.Join(",", rightReceipts), "receipt sequence");
                    for (var id = 2; id < 32; id++)
                        Equal((await memory.GetEntryAsync(new EntryId(id), SqliteContext))?.Entry.ToJson().ToJson(),
                            (await sqlite.GetEntryAsync(new EntryId(id), SqliteContext))?.Entry.ToJson().ToJson(), "entries");
                    for (var id = 100; id < 104; id++)
                        Equal((await memory.GetTaskAsync(new TaskId(id), SqliteContext))?.ToJson().ToJson(),
                            (await sqlite.GetTaskAsync(new TaskId(id), SqliteContext))?.ToJson().ToJson(), "tasks");
                    Equal((await memory.GetDocumentAsync(new DocumentId(200), DocumentPoint.Current, SqliteContext))!.Value.ToJson(),
                        (await sqlite.GetDocumentAsync(new DocumentId(200), DocumentPoint.Current, SqliteContext))!.Value.ToJson(), "document state");
                }
                finally { await sqlite.CloseAsync(SqliteContext); await memory.CloseAsync(SqliteContext); }
            }
        });
    }

    private sealed class CountingBulkDatabase(ISqliteDatabase inner) : ISqliteDatabase
    {
        public int Calls;
        public int MaxParameters;
        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] values) => inner.RunAsync(sql, values);
        public Task<long> RunCountedAsync(string sql, params object?[] values) => inner.RunCountedAsync(sql, values);
        public Task<SqliteRow?> GetAsync(string sql, params object?[] values) => inner.GetAsync(sql, values);
        public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] values) => inner.AllAsync(sql, values);
        public Task CloseAsync() => inner.CloseAsync();
        public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback) => inner.TransactionAsync(tx => callback(new Executor(tx, this)));
        private sealed class Executor(ISqliteExecutor inner, CountingBulkDatabase count) : ISqliteExecutor
        {
            private void Count(object?[] values) { count.Calls++; count.MaxParameters = Math.Max(count.MaxParameters, values.Length); }
            public Task ExecAsync(string sql) { Count([]); return inner.ExecAsync(sql); }
            public Task RunAsync(string sql, params object?[] values) { Count(values); return inner.RunAsync(sql, values); }
            public Task<long> RunCountedAsync(string sql, params object?[] values) { Count(values); return inner.RunCountedAsync(sql, values); }
            public Task<SqliteRow?> GetAsync(string sql, params object?[] values) { Count(values); return inner.GetAsync(sql, values); }
            public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] values) { Count(values); return inner.AllAsync(sql, values); }
        }
    }
}
