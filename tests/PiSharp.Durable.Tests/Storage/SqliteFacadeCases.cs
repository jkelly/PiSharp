// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/sqlite-facade.test.ts, against SqliteConnectionDatabase (the
// node:sqlite adapter's counterpart). Upstream observes microtask timing with `await Promise.resolve()`; these cases use explicit gates and
// check that the queued task has not completed. Adapted: upstream counts prepare() calls by subclassing DatabaseSync; here the adapter
// counts the statements it prepares (SqliteConnectionDatabase.PrepareCounts).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StorageSqliteFacadeCases()
    {
        yield return Case("storage.sqlite.facade.prepares-each-storage-statement-once-per-connection-and-reuses-it-across-transactions", async () =>
        {
            var connection = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var storage = await SqliteStorage.OpenAsync(connection);
            await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root))], SqliteContext);
            await storage.CommitAsync([.. Enumerable.Range(0, 100).Select(index =>
                (StorageWrite)new EntryWrite(new() { Id = new EntryId(index + 2), ConversationId = ConversationId.Root, Kind = "cached" }))], SqliteContext);
            await storage.CommitAsync([new EntryWrite(new() { Id = new EntryId(102), ConversationId = ConversationId.Root, Kind = "cached-again" })],
                SqliteContext);
            Equal("cached", (await storage.GetEntryAsync(new EntryId(2), SqliteContext))?.Entry.Kind, "first entry");
            Equal("cached-again", (await storage.GetEntryAsync(new EntryId(102), SqliteContext))?.Entry.Kind, "last entry");
            Equal("ID 1 already belongs to conversation", (await ThrowsAsync<InvalidOperationException>(
                () => storage.CommitAsync([new ConversationWrite(new(ConversationId.Root))], SqliteContext), "duplicate root")).Message, "message");
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

            var storage = await SqliteStorage.OpenAsync(database);
            var commit = storage.CommitAsync([new ConversationWrite(new(ConversationId.Root))], SqliteContext);
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
            await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root)),
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

        yield return Case("storage.sqlite.facade.does-not-preserve-a-guaranteed-rejection-when-rollback-itself-fails", async () =>
        {
            var database = await SqliteConnectionDatabase.OpenAsync(":memory:");
            await database.ExecAsync("CREATE TABLE rollback_probe (value INTEGER)");
            var error = await ThrowsAsync<AggregateException>(() => database.TransactionAsync(async transaction =>
            {
                await transaction.ExecAsync("INSERT INTO rollback_probe (value) VALUES (1)");
                await transaction.ExecAsync("COMMIT");
                throw new StorageRejectedException("rejected after an escaped commit");
            }), "transaction");
            Check(error.InnerExceptions[0] is StorageRejectedException, "the callback's rejection is the first inner exception");
            Equal("{\"value\":1}", (await database.GetAsync("SELECT value FROM rollback_probe"))?.ToString(), "escaped commit");
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
            var committing = storage.CommitAsync([new EntryWrite(new() { Id = new EntryId(100), ConversationId = ConversationId.Root, Kind = "settled" })],
                SqliteContext);
            await Task.Delay(20);
            Check(!committing.IsCompleted, "the commit waits for its settlement");
            Equal(2L, (await storage.MintIdAsync<EntryId>()).Value, "minting before settlement");
            database.Settle();
            Equal(Ids.SeqFromNumber(1), await committing, "settled sequence");
            Equal(101L, (await storage.MintIdAsync<EntryId>()).Value, "minting after settlement");

            database.ControlNextSettlement(SettlementMode.Reject);
            var rejected = storage.CommitAsync([new EntryWrite(new() { Id = new EntryId(200), ConversationId = ConversationId.Root, Kind = "rejected" })],
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
                await storage.CommitAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "replaced", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, (JsonObjectValue)J("{\"value\":1}")))], SqliteContext);
                var read = storage.GetDocumentAsync(id, DocumentPoint.Current, SqliteContext);
                for (var index = 0; index < yields; index++) await Task.Yield();
                var replace = storage.CommitAsync([new DocumentChangeWrite(id, DocumentContent.Base(1, (JsonObjectValue)J("{\"value\":2}")))], SqliteContext);
                await Task.WhenAll(read, replace);
                var value = (await read)?.Value;
                Check(value is not null && (JsonValue.DeepEquals(J("{\"value\":1}"), value) || JsonValue.DeepEquals(J("{\"value\":2}"), value)),
                    "a committed value: " + value?.ToJson());
                await storage.CloseAsync(SqliteContext);
            }
        });
    }

    private static string Rows(IEnumerable<SqliteRow> rows) => string.Join("|", rows.Select(row => row.ToString()));

    private enum SettlementMode { Immediate, Delay, Reject }

    /// <summary>Upstream's ControlledSettlementDatabase: the next transaction runs and commits (or, for Reject, fails after its callback)
    /// at once, but its caller sees the outcome only when <see cref="Settle"/> is called.</summary>
    private sealed class ControlledSettlementDatabase(ISqliteDatabase inner) : ISqliteDatabase
    {
        private SettlementMode mode = SettlementMode.Immediate;
        private Action? pendingSettlement;

        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] parameters) => inner.RunAsync(sql, parameters);
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
