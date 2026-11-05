// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/sqlite-facade.test.ts, against SqliteConnectionDatabase (the
// node:sqlite adapter's counterpart). Upstream observes microtask timing with `await Promise.resolve()`; these cases use explicit gates and
// check that the queued task has not completed. Adapted: upstream counts prepare() calls by subclassing DatabaseSync; here the adapter
// counts the statements it prepares (SqliteConnectionDatabase.PrepareCounts).
// Pi v1.5.0 (f9a26365568127461c47a07a37dcb42c140a3ac5) moved the driver cases to packages/chord/test/sqlite/node.test.ts; the
// storage.sqlite.facade.* cases for the driver keep their ids, and the driver cases new in v1.5.0 are chord.sqlite.node.*: connection
// settings, runCounted (with helpers.ts's runChanges), and the outcome of a transaction (slice 6b D4). Adapted there: upstream makes
// BEGIN IMMEDIATE, COMMIT or ROLLBACK throw by replacing DatabaseSync.exec; here BEGIN IMMEDIATE fails inside a transaction the
// connection already began, COMMIT on a deferred foreign key violation, and ROLLBACK after the callback committed. Not ported: upstream's
// re-export identity case (the C# types are the same types, with no re-export) and the 256-statement cache case (row
// chord.sqlite-statement-cache, phase 6). The authored chord.sqlite.node.counts-no-rows-that-triggers-change pins that trigger writes do
// not count. The storage commits go through v1.5.0's commitWrites (CommitWritesAsync, area storage-batches).
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Testing;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StorageSqliteFacadeCases()
    {
        yield return Case("storage.sqlite.facade.prepares-each-storage-statement-once-per-connection-and-reuses-it-across-transactions", async () =>
        {
            var connection = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var storage = await SqliteStorage.OpenAsync(connection);
            await storage.CommitWritesAsync([new ConversationWrite(new(ConversationId.Root))], SqliteContext);
            await storage.CommitWritesAsync([.. Enumerable.Range(0, 100).Select(index =>
                (StorageWrite)new EntryWrite(new() { Id = new EntryId(index + 2), ConversationId = ConversationId.Root, Kind = "cached" }))], SqliteContext);
            await storage.CommitWritesAsync([new EntryWrite(new() { Id = new EntryId(102), ConversationId = ConversationId.Root, Kind = "cached-again" })],
                SqliteContext);
            Equal("cached", (await storage.GetEntryAsync(new EntryId(2), SqliteContext))?.Entry.Kind, "first entry");
            Equal("cached-again", (await storage.GetEntryAsync(new EntryId(102), SqliteContext))?.Entry.Kind, "last entry");
            Equal("ID 1 already belongs to conversation", (await ThrowsAsync<InvalidOperationException>(
                () => storage.CommitWritesAsync([new ConversationWrite(new(ConversationId.Root))], SqliteContext), "duplicate root")).Message, "message");
            Equal("cached", (await storage.GetEntryAsync(new EntryId(2), SqliteContext))?.Entry.Kind, "entry after the rejection");
            var repeated = connection.PrepareCounts.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToArray();
            Check(repeated.Length == 0, "statements prepared more than once: " + string.Join(" | ", repeated));
            Check(connection.PrepareCounts.Count > 0, "statements were prepared");
            await storage.CloseAsync(SqliteContext);
        });

        yield return Case("storage.sqlite.facade.commits-work-done-through-the-transaction-handle-and-closes-idempotently", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.TransactionAsync(async transaction =>
            {
                await transaction.ExecAsync("CREATE TABLE async_probe (value INTEGER)");
                await transaction.RunAsync("INSERT INTO async_probe (value) VALUES (?)", 1L);
            });
            Equal("{\"value\":1}", (await database.GetAsync("SELECT value FROM async_probe"))?.ToString(), "row");
            await database.CloseAsync();
            await database.CloseAsync();
        });

        yield return Case("storage.sqlite.facade.serializes-concurrent-transactions", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE transaction_queue (value INTEGER)");
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = database.TransactionAsync(async transaction =>
            {
                await transaction.ExecAsync("INSERT INTO transaction_queue (value) VALUES (1)");
                firstStarted.SetResult();
                await firstGate.Task;
            });
            await firstStarted.Task;

            var secondStarted = 0;
            var second = database.TransactionAsync(async transaction =>
            {
                Volatile.Write(ref secondStarted, 1);
                await transaction.ExecAsync("INSERT INTO transaction_queue (value) VALUES (2)");
            });
            await Task.Delay(20);
            Equal(0, Volatile.Read(ref secondStarted), "second transaction waits");

            firstGate.SetResult();
            await Task.WhenAll(first, second);
            Equal("{\"value\":1}|{\"value\":2}", Rows(await database.AllAsync("SELECT value FROM transaction_queue ORDER BY value")), "rows");
            await database.CloseAsync();
        });

        yield return Case("storage.sqlite.facade.runs-operations-in-call-order-whether-they-start-immediately-or-wait", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE call_order (value INTEGER)");
            // Operations called during a transaction must neither see its uncommitted rows nor join its rollback.
            var transaction = database.TransactionAsync(async handle =>
            {
                await handle.RunAsync("INSERT INTO call_order (value) VALUES (?)", 1L);
                await Task.Yield();
                throw new InvalidOperationException("roll back");
            });
            var beforeWrite = database.AllAsync("SELECT value FROM call_order ORDER BY value");
            var write = database.RunAsync("INSERT INTO call_order (value) VALUES (?)", 2L);
            var afterWrite = database.AllAsync("SELECT value FROM call_order ORDER BY value");
            Equal("roll back", (await ThrowsAsync<InvalidOperationException>(() => transaction, "transaction")).Message, "transaction failure");
            await write;
            Equal("", Rows(await beforeWrite), "rows before the write");
            Equal("{\"value\":2}", Rows(await afterWrite), "rows after the write");
            await database.CloseAsync();
        });

        yield return Case("storage.sqlite.facade.reads-storage-commits-in-call-order", async () =>
        {
            var storage = await SqliteStorage.OpenAsync(await SqliteConnectionDatabase.OpenAsync(":memory:"));
            var commit = storage.CommitWritesAsync([new ConversationWrite(new(ConversationId.Root))], SqliteContext);
            var read = storage.GetConversationAsync(ConversationId.Root, SqliteContext);
            await commit;
            Equal(new ConversationRecord(ConversationId.Root), await read, "read queued after the commit");
            await storage.CloseAsync(SqliteContext);
        });

        yield return Case("storage.sqlite.facade.queues-ordinary-operations-behind-an-active-transaction", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE operation_queue (value INTEGER)");
            var transactionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transactionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = database.TransactionAsync(async transaction =>
            {
                await transaction.ExecAsync("INSERT INTO operation_queue (value) VALUES (1)");
                transactionStarted.SetResult();
                await transactionGate.Task;
            });
            await transactionStarted.Task;

            var write = database.ExecAsync("INSERT INTO operation_queue (value) VALUES (2)");
            var read = database.AllAsync("SELECT value FROM operation_queue ORDER BY value");
            await Task.Delay(20);
            Check(!write.IsCompleted, "the write waits for the transaction");
            Check(!read.IsCompleted, "the read waits for the transaction");

            transactionGate.SetResult();
            await pending;
            await write;
            Equal("{\"value\":1}|{\"value\":2}", Rows(await read), "rows");
            await database.CloseAsync();
        });

        yield return Case("storage.sqlite.facade.queues-database-calls-made-synchronously-by-a-transaction-that-started-immediately", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE barrier_probe (value INTEGER)");
            Task? outside = null;
            var transaction = database.TransactionAsync(async handle =>
            {
                // Misuse: this call must wait for the transaction instead of joining it.
                outside = database.RunAsync("INSERT INTO barrier_probe (value) VALUES (?)", 2L);
                await handle.RunAsync("INSERT INTO barrier_probe (value) VALUES (?)", 1L);
                await Task.Yield();
                throw new InvalidOperationException("roll back");
            });
            Equal("roll back", (await ThrowsAsync<InvalidOperationException>(() => transaction, "transaction")).Message, "transaction failure");
            await outside!;
            Equal("{\"value\":2}", Rows(await database.AllAsync("SELECT value FROM barrier_probe")), "rows");
            await database.CloseAsync();
        });

        yield return Case("storage.sqlite.facade.lets-admitted-multi-query-reads-finish-before-storage-closes", async () =>
        {
            var storage = await SqliteStorage.OpenAsync(await SqliteConnectionDatabase.OpenAsync(":memory:"));
            var entryId = new EntryId(2);
            await storage.CommitWritesAsync([new ConversationWrite(new(ConversationId.Root)),
                new EntryWrite(new() { Id = entryId, ConversationId = ConversationId.Root, Kind = "probe" })], SqliteContext);
            var scan = storage.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 10, null, SqliteContext);
            var entry = storage.GetEntryAsync(ConversationId.Root, entryId, SqliteContext);
            var head = storage.FindLatestHeadMarkerAsync(ConversationId.Root, null, SqliteContext);
            var closed = storage.CloseAsync(SqliteContext);
            // A repeated close settles only when the database is closed.
            Check(ReferenceEquals(closed, storage.CloseAsync(SqliteContext)), "a repeated close returns the same task");
            Equal("2", string.Join(",", (await scan).Items.Select(item => item.Id.Value)), "scanned entries");
            Equal("probe", (await entry)?.Entry.Kind, "entry");
            Equal<EntryRecord?>(null, await head, "head marker");
            await closed;
            Equal("SqliteStorage is closed", (await ThrowsAsync<InvalidOperationException>(
                () => storage.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 10, null, SqliteContext), "scan after close")).Message, "message");
        });

        yield return Case("storage.sqlite.facade.rejects-a-transaction-handle-used-after-its-transaction-settles", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE stale_probe (value INTEGER)");
            ISqliteExecutor? handle = null;
            await database.TransactionAsync(async transaction =>
            {
                handle = transaction;
                await transaction.RunAsync("INSERT INTO stale_probe (value) VALUES (?)", 1L);
            });
            const string stale = "SQLite transaction handle is no longer active";
            Equal(stale, (await ThrowsAsync<InvalidOperationException>(() => handle!.ExecAsync("INSERT INTO stale_probe (value) VALUES (2)"), "exec"))
                .Message, "exec message");
            Equal(stale, (await ThrowsAsync<InvalidOperationException>(() => handle!.RunAsync("INSERT INTO stale_probe (value) VALUES (?)", 3L), "run"))
                .Message, "run message");
            Equal("{\"value\":1}", Rows(await database.AllAsync("SELECT value FROM stale_probe")), "rows");
            await database.CloseAsync();
        });

        yield return Case("storage.sqlite.facade.does-not-preserve-the-callback-error-when-rollback-itself-fails", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE rollback_probe (value INTEGER)");
            var rejection = new InvalidOperationException("rejected after an escaped commit");
            var error = await ThrowsAsync<AggregateException>(() => database.TransactionAsync(async transaction =>
            {
                await transaction.ExecAsync("INSERT INTO rollback_probe (value) VALUES (1)");
                await transaction.ExecAsync("COMMIT");
                throw rejection;
            }), "transaction");
            Check(ReferenceEquals(error.InnerExceptions[0], rejection), "the callback's error is the first inner exception");
            Equal("{\"value\":1}", (await database.GetAsync("SELECT value FROM rollback_probe"))?.ToString(), "escaped commit");
            await database.CloseAsync();
        });

        yield return Case("chord.sqlite.node.configures-connection-defaults-and-accepts-explicit-settings", async () =>
        {
            foreach (var options in new[] { new SqliteStorageOptions(), new SqliteStorageOptions { BusyTimeoutMs = 123, WalAutoCheckpointPages = 0 } })
            {
                var database = await SqliteConnectionDatabase.OpenAsync(":memory:", options);
                try
                {
                    Equal($"{{\"timeout\":{options.BusyTimeoutMs ?? 5_000}}}", (await database.GetAsync("PRAGMA busy_timeout"))?.ToString(), "busy timeout");
                    Equal($"{{\"wal_autocheckpoint\":{options.WalAutoCheckpointPages ?? 1_000}}}",
                        (await database.GetAsync("PRAGMA wal_autocheckpoint"))?.ToString(), "WAL auto-checkpoint");
                    Equal("{\"synchronous\":1}", (await database.GetAsync("PRAGMA synchronous"))?.ToString(), "synchronous");
                }
                finally
                {
                    await database.CloseAsync();
                }
            }
        });

        yield return Case("chord.sqlite.node.reports-the-rows-a-statement-changed-without-index-writes", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                Equal(RunChangesExpected, await RunChanges(database), "changes");
                var inTransaction = await database.TransactionAsync(handle =>
                    handle.RunCountedAsync("INSERT INTO changes_probe VALUES (?, ?, ?)", "t", "t/1", 1L));
                Equal(1L, inTransaction, "changes in a transaction");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("chord.sqlite.node.counts-no-rows-that-triggers-change", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            try
            {
                await database.ExecAsync("CREATE TABLE trigger_probe (id INTEGER PRIMARY KEY, n INTEGER NOT NULL) STRICT");
                await database.ExecAsync("CREATE TABLE trigger_log (n INTEGER NOT NULL) STRICT");
                await database.ExecAsync("""
                    CREATE TRIGGER trigger_probe_inserted AFTER INSERT ON trigger_probe BEGIN
                        INSERT INTO trigger_log (n) VALUES (new.n), (new.n + 1);
                    END;
                    CREATE TRIGGER trigger_probe_updated AFTER UPDATE ON trigger_probe BEGIN
                        INSERT INTO trigger_log (n) SELECT n FROM trigger_log;
                    END;
                    """);
                Equal(1L, await database.RunCountedAsync("INSERT INTO trigger_probe (id, n) VALUES (?, ?)", 1L, 10L), "insert");
                Equal(1L, await database.RunCountedAsync("UPDATE trigger_probe SET n = ? WHERE id = ?", 20L, 1L), "update");
                Equal(0L, await database.RunCountedAsync("UPDATE trigger_probe SET n = ? WHERE id = ?", 30L, 2L), "update of no row");
                Equal("{\"count\":4}", (await database.GetAsync("SELECT count(*) AS count FROM trigger_log"))?.ToString(), "rows the triggers wrote");
            }
            finally
            {
                await database.CloseAsync();
            }
        });

        yield return Case("chord.sqlite.node.transaction-outcome.rejects-with-the-callbacks-own-error-after-a-rollback", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var own = new InvalidOperationException("refused");
            await database.ExecAsync("CREATE TABLE t (n INTEGER)");
            var outcome = database.TransactionAsync(async handle =>
            {
                await handle.RunAsync("INSERT INTO t VALUES (1)");
                throw own;
            });
            Check(ReferenceEquals(own, await ThrowsAsync<InvalidOperationException>(() => outcome, "outcome")), "the callback's own error");
            Equal("", Rows(await database.AllAsync("SELECT n FROM t")), "rows");
            await database.CloseAsync();
        });

        yield return Case("chord.sqlite.node.transaction-outcome.rejects-with-another-error-when-begin-immediate-fails", async () =>
        {
            var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            var database = new SqliteConnectionDatabase(connection);
            using (var begin = connection.CreateCommand())
            {
                begin.CommandText = "BEGIN";
                begin.ExecuteNonQuery();
            }
            var called = false;
            var error = await ThrowsAsync<SqliteException>(() => database.TransactionAsync(_ =>
            {
                called = true;
                return Task.FromResult(1);
            }), "outcome");
            Check(error.Message.Contains("cannot start a transaction within a transaction", StringComparison.Ordinal),
                "BEGIN IMMEDIATE failed: " + error.Message);
            Check(!called, "the callback never ran");
            await database.ExecAsync("ROLLBACK");
            await database.CloseAsync();
        });

        yield return Case("chord.sqlite.node.transaction-outcome.rejects-with-another-error-when-commit-fails", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("PRAGMA foreign_keys = ON");
            await database.ExecAsync("CREATE TABLE parent (id INTEGER PRIMARY KEY)");
            await database.ExecAsync("CREATE TABLE child (parent_id INTEGER REFERENCES parent (id) DEFERRABLE INITIALLY DEFERRED)");
            var error = await ThrowsAsync<SqliteException>(() => database.TransactionAsync(async handle =>
            {
                await handle.RunAsync("INSERT INTO child (parent_id) VALUES (?)", 5L);
                return 1;
            }), "outcome");
            Check(error.Message.Contains("FOREIGN KEY constraint failed", StringComparison.Ordinal), "COMMIT failed: " + error.Message);
            Equal("", Rows(await database.AllAsync("SELECT parent_id FROM child")), "rolled back");
            Equal(2, await database.TransactionAsync(_ => Task.FromResult(2)), "a later transaction");
            await database.CloseAsync();
        });

        yield return Case("chord.sqlite.node.transaction-outcome.rejects-with-an-aggregate-error-not-the-callbacks-error-when-its-rollback-fails", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var own = new InvalidOperationException("refused");
            var error = await ThrowsAsync<Exception>(() => database.TransactionAsync<int>(async handle =>
            {
                await handle.ExecAsync("COMMIT");
                return await Task.FromException<int>(own);
            }), "outcome");
            Check(error is AggregateException, "an AggregateException: " + error.GetType().Name);
            Check(!ReferenceEquals(error, own), "not the callback's error");
            await database.CloseAsync();
        });

        yield return Case("storage.sqlite.facade.awaits-async-transaction-settlement-and-adopts-ids-only-after-success", async () =>
        {
            var database = new ControlledSettlementDatabase(await SqliteConnectionDatabase.OpenAsync(":memory:"));
            database.ControlNextSettlement(SettlementMode.Delay);
            var opening = SqliteStorage.OpenAsync(database);
            await Task.Delay(20);
            Check(!opening.IsCompleted, "open waits for the migration settlement");
            database.Settle();
            var storage = await opening;

            database.ControlNextSettlement(SettlementMode.Delay);
            var committing = storage.CommitWritesAsync([new EntryWrite(new() { Id = new EntryId(100), ConversationId = ConversationId.Root, Kind = "settled" })],
                SqliteContext);
            await Task.Delay(20);
            Check(!committing.IsCompleted, "the commit waits for its settlement");
            Equal(2L, (await storage.MintIdAsync<EntryId>()).Value, "minting before settlement");
            database.Settle();
            Equal(Ids.SeqFromNumber(1), await committing, "settled sequence");
            Equal(101L, (await storage.MintIdAsync<EntryId>()).Value, "minting after settlement");

            database.ControlNextSettlement(SettlementMode.Reject);
            var rejected = storage.CommitWritesAsync([new EntryWrite(new() { Id = new EntryId(200), ConversationId = ConversationId.Root, Kind = "rejected" })],
                SqliteContext);
            Equal(102L, (await storage.MintIdAsync<EntryId>()).Value, "minting before the rejection");
            database.Settle();
            Equal("controlled settlement rejection", (await ThrowsAsync<InvalidOperationException>(() => rejected, "rejected commit")).Message, "message");
            Equal(103L, (await storage.MintIdAsync<EntryId>()).Value, "minting after the rejection");
            Equal<CommittedEntry?>(null, await storage.GetEntryAsync(new EntryId(200), SqliteContext), "rejected entry");
            await storage.CloseAsync(SqliteContext);
        });

        yield return Case("storage.sqlite.facade.reads-a-document-from-one-committed-state-while-a-commit-replaces-its-base", async () =>
        {
            var id = new DocumentId(5);
            // Each yield count starts the commit at a different point of the read's record and revision queries.
            for (var yields = 0; yields < 16; yields++)
            {
                var storage = await SqliteStorage.OpenAsync(await SqliteConnectionDatabase.OpenAsync(":memory:"));
                await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "replaced", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, (JsonObjectValue)J("{\"value\":1}")))], SqliteContext);
                var read = storage.GetDocumentAsync(id, DocumentPoint.Current, SqliteContext);
                for (var index = 0; index < yields; index++) await Task.Yield();
                var replace = storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, (JsonObjectValue)J("{\"value\":2}")))], SqliteContext);
                await Task.WhenAll(read, replace);
                var value = (await read)?.Value;
                Check(value is not null && (JsonValue.DeepEquals(J("{\"value\":1}"), value) || JsonValue.DeepEquals(J("{\"value\":2}"), value)),
                    "a committed value: " + value?.ToJson());
                await storage.CloseAsync(SqliteContext);
            }
        });
    }

    private static string Rows(IEnumerable<SqliteRow> rows) => string.Join("|", rows.Select(row => row.ToString()));

    /// <summary>Upstream's runChanges (packages/chord/test/sqlite/helpers.ts): the rows each kind of statement changes, as RunCountedAsync
    /// reports them, on a table with two indexes, so that a driver counting index writes would report more.</summary>
    private static async Task<string> RunChanges(ISqliteExecutor database)
    {
        await database.ExecAsync("CREATE TABLE changes_probe (id TEXT PRIMARY KEY, path TEXT NOT NULL, n INTEGER NOT NULL) STRICT, WITHOUT ROWID");
        await database.ExecAsync("CREATE INDEX changes_probe_path ON changes_probe (path, id)");
        await database.ExecAsync("CREATE INDEX changes_probe_n ON changes_probe (n, id)");
        for (var i = 0; i < 10; i++) await database.RunAsync("INSERT INTO changes_probe VALUES (?, ?, ?)", $"k{i}", $"a/{i}", (long)i);
        (string Kind, long Rows)[] changes =
        [
            ("insert", await database.RunCountedAsync("INSERT INTO changes_probe VALUES (?, ?, ?)", "x", "a/x", 99L)),
            ("update", await database.RunCountedAsync("UPDATE changes_probe SET path = ? WHERE id = ?", "b/x", "x")),
            ("none", await database.RunCountedAsync("UPDATE changes_probe SET n = 0 WHERE id = ?", "absent")),
            ("bulk", await database.RunCountedAsync("UPDATE changes_probe SET path = 'c/' || substr(path, 3) WHERE path >= 'a/' AND path < 'a0'")),
            ("upsert", await database.RunCountedAsync("INSERT INTO changes_probe VALUES (?, ?, ?) ON CONFLICT (id) DO UPDATE SET n = excluded.n",
                "x", "b/x", 5L)),
            ("delete", await database.RunCountedAsync("DELETE FROM changes_probe WHERE id = ?", "x")),
            ("bulkDelete", await database.RunCountedAsync("DELETE FROM changes_probe WHERE path >= 'c/' AND path < 'c0'")),
        ];
        return string.Join(",", changes.Select(change => $"{change.Kind}={change.Rows}"));
    }

    /// <summary>Upstream's RUN_CHANGES: what RunChanges reports on every driver.</summary>
    private const string RunChangesExpected = "insert=1,update=1,none=0,bulk=10,upsert=1,delete=1,bulkDelete=10";

    private enum SettlementMode { Immediate, Delay, Reject }

    /// <summary>Upstream's ControlledSettlementDatabase: the next transaction runs and commits (or, for Reject, fails after its callback)
    /// at once, but its caller sees the outcome only when <see cref="Settle"/> is called.</summary>
    private sealed class ControlledSettlementDatabase(ISqliteDatabase inner) : ISqliteDatabase
    {
        private SettlementMode mode = SettlementMode.Immediate;
        private Action? pendingSettlement;

        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] parameters) => inner.RunAsync(sql, parameters);
        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => inner.RunCountedAsync(sql, parameters);
        public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters) => inner.GetAsync(sql, parameters);
        public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters) => inner.AllAsync(sql, parameters);
        public Task CloseAsync() => inner.CloseAsync();

        public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback)
        {
            var current = mode;
            mode = SettlementMode.Immediate;
            if (current == SettlementMode.Immediate) return inner.TransactionAsync(callback);
            var settlement = inner.TransactionAsync(async transaction =>
            {
                var value = await callback(transaction);
                if (current == SettlementMode.Reject) throw new InvalidOperationException("controlled settlement rejection");
                return value;
            });
            var outcome = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingSettlement = () => settlement.ContinueWith(task =>
            {
                if (task.IsFaulted) outcome.SetException(task.Exception!.InnerExceptions);
                else if (task.IsCanceled) outcome.SetCanceled();
                else outcome.SetResult(task.Result);
            }, TaskScheduler.Default);
            return outcome.Task;
        }

        public void ControlNextSettlement(SettlementMode next)
        {
            if (pendingSettlement is not null) throw new InvalidOperationException("A settlement is already pending");
            mode = next;
        }

        public void Settle()
        {
            var settle = pendingSettlement ?? throw new InvalidOperationException("No settlement is pending");
            pendingSettlement = null;
            settle();
        }
    }
}
