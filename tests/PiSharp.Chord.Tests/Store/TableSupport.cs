using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;

// Authored test support of the table cases (area cv2-store-tables); no upstream file. Upstream's table tests open a ChordStore with
// test/next/support.ts's memoryStore() (openStore over node:sqlite's ":memory:") and use its tableOwner, tableIn and holdNextCommit; the
// store itself (store.ts, outbox.ts, backend.ts) is area cv2-store-engine's. Until it lands, TestStore stands in for ChordStore with what the
// table cases see of it: the store's schema applied at open (its statements as Schema.cs records them; the schema version kept in a table
// of this support's own form), the outbox switch, the catalog read once, one commit queue running each step as store.ts's #step does
// (the fence's admission, the writes in their order, the catalog, the outbox commit, chord_meta, COMMIT, then memory, the counters
// chord.store.commits and chord.store.statements, and the fence's adoption; a refusal rolls back and the store goes on, D4; any other
// failure stops it, storage_failed), guarded reads, the outbox's read, the claims, and close. The store's id allocator, series, replays
// and exports are not here. TestSqliteDatabase is an ISqliteDatabase over Microsoft.Data.Sqlite (the test project's reference) on a file
// or ":memory:", one operation or transaction at a time (BEGIN IMMEDIATE, COMMIT, ROLLBACK). When the engine lands, its ChordStore and
// memoryStore replace these, and the helpers move to ChordSupport.cs (test/next/support.ts).
internal static partial class Program
{
    /// <summary>A store on a fresh SQLite <c>:memory:</c> database (upstream <c>memoryStore</c>).</summary>
    internal static Task<TestStore> MemoryStore(bool? outbox = null) => TestStore.OpenAsync(":memory:", outbox);

    /// <summary>The owner of a test's table handles: its failures collected (upstream <c>tableOwner</c>).</summary>
    internal static TestTableOwner TableOwner() => new();

    /// <summary>Creates <paramref name="definition"/> (and its indexes) in <paramref name="store"/> as <c>app.store:&lt;name&gt;</c> and
    /// returns a handle to it (upstream <c>tableIn</c>).</summary>
    internal static async Task<TableHandle> TableIn(TestStore store, TableDefinition definition, TestTableOwner? owner = null)
    {
        var table = $"app.store:{definition.Name}";
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(definition, table)], store, CommitFence.Open, "app.store", ChordContext.Background);
        return new TableHandle(definition, table, store, owner ?? TableOwner());
    }

    /// <summary>expect(await rejection(...)).toMatchObject({ code, reason }): <paramref name="rejection"/> is ChordError JSON with
    /// <paramref name="code"/> (when given), <paramref name="reason"/> (when given) and a message containing <paramref name="message"/>
    /// (when given); an exception's message is checked alone.</summary>
    internal static void Rejected(object rejection, string? code, string? reason, string what, string? message = null)
    {
        if (rejection is Exception error)
        {
            Check(code is null && reason is null, $"{what}: expected a ChordError, actual {error.GetType().Name}: {error.Message}");
            Check(message is not null && error.Message.Contains(message, StringComparison.Ordinal), $"{what}: expected a message containing <{message}>, actual <{error.Message}>.");
            return;
        }
        var json = (JsonObject)rejection;
        if (code is not null) Equal(code, (string?)json["code"], what + ": the code");
        if (reason is not null) Equal(reason, (string?)json["reason"], what + ": the reason");
        if (message is not null)
        {
            var actual = (string?)json["message"] ?? "";
            Check(actual.Contains(message, StringComparison.Ordinal), $"{what}: expected a message containing <{message}>, actual <{actual}>.");
        }
    }

    /// <summary>The error <paramref name="task"/> fails with, as ChordError JSON when it is one (upstream <c>rejection</c>).</summary>
    internal static Task<object> StoreRejection(Task task) => ChordSupport.Rejection(task);

    /// <summary>Index definitions by name, in order (a TableOptions.Indexes).</summary>
    internal static OrderedDictionary<string, IReadOnlyList<string>> StoreIndexes(params (string Name, string[] Columns)[] indexes)
    {
        var map = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, columns) in indexes) map[name] = columns;
        return map;
    }

    /// <summary>A JSON value from its text: a row, a key or a set as upstream writes them as object literals.</summary>
    internal static JsonNode StoreValue(string json) => JsonNode.Parse(json)!;

    /// <summary>Stages <paramref name="fill"/>'s writes to <paramref name="handle"/>'s table and commits them through the open fence.</summary>
    internal static async Task StoreCommitWith(TestStore store, TableHandle handle, Action<TableChanges> fill, ChordContext? context = null)
    {
        var staged = new List<IStagedWrite>();
        fill(new TableChanges(handle, staged));
        await store.CommitAsync(staged, CommitFence.Open, context ?? ChordContext.Background);
    }

    /// <summary>A rows step's result: column values as operands (<see cref="Operand.Undefined"/> clears).</summary>
    internal static Dictionary<string, Operand> StorePatch(params (string Column, Operand Value)[] values) =>
        values.ToDictionary(value => value.Column, value => value.Value, StringComparer.Ordinal);
}

/// <summary>A table owner whose failures are collected (upstream <c>tableOwner()</c>'s result).</summary>
internal sealed class TestTableOwner : ITableOwner
{
    public string Label => "app.store";

    public object Scope { get; } = new();

    /// <summary>The failures the handles reported.</summary>
    internal List<ChordError> Failures { get; } = [];

    public void AssertLive()
    {
    }

    public void Fail(ChordError error) => Failures.Add(error);
}

/// <summary>A stand-in for the engine's ChordStore (see the file's header).</summary>
internal sealed class TestStore : IChordStore, IAsyncDisposable
{
    private readonly TestSqliteDatabase db;
    private readonly Dictionary<string, TableRecord> catalog;
    private readonly Dictionary<string, long> announced = new(StringComparer.Ordinal);
    private readonly long nextId;
    private Task tail = Task.CompletedTask;
    private Exception? failure;
    private bool closed;
    private int underway;
    private TaskCompletionSource? drained;
    private Task? closing;
    private Func<IReadOnlyList<IStagedWrite>, IReadOnlyList<IStagedWrite>>? nextCommit;

    private TestStore(TestSqliteDatabase db, MetaRow meta, IEnumerable<KeyValuePair<string, TableRecord>> catalog)
    {
        this.db = db;
        this.catalog = new Dictionary<string, TableRecord>(catalog, StringComparer.Ordinal);
        LastSeq = meta.LastSeq;
        nextId = meta.NextId;
        HasOutbox = meta.Outbox == 1;
    }

    /// <summary>The seq of the store's last commit (0 for a new store).</summary>
    internal long LastSeq { get; private set; }

    /// <summary>Whether the store records an outbox.</summary>
    internal bool HasOutbox { get; }

    /// <summary>The claims of its tables and documents (slice 6b).</summary>
    internal Claims Claims { get; } = new();

    /// <summary>Opens a store on <paramref name="path"/>: the schema applied when the database is new, the outbox switched when asked, the
    /// metadata and the catalog read once (upstream <c>openStore</c>).</summary>
    internal static async Task<TestStore> OpenAsync(string path, bool? outbox = null)
    {
        var db = new TestSqliteDatabase(path);
        var fresh = await db.GetAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name = ?", StoreInternal.StoreSchemaTable) is null;
        if (fresh)
        {
            await db.TransactionAsync(async executor =>
            {
                foreach (var migration in StoreInternal.StoreMigrations)
                    foreach (var statement in migration.Statements) await executor.ExecAsync(statement);
                await executor.ExecAsync($"CREATE TABLE {StoreInternal.StoreSchemaTable} (singleton INTEGER PRIMARY KEY CHECK (singleton = 1), version INTEGER NOT NULL) STRICT");
                await executor.RunAsync($"INSERT INTO {StoreInternal.StoreSchemaTable} (singleton, version) VALUES (1, ?)", StoreInternal.StoreSchema);
                return true;
            });
        }
        var row = (await db.GetAsync(StoreInternal.MetaSql))!;
        var meta = new MetaRow((long)row["last_seq"]!, (long)row["next_id"]!, (long)row["outbox"]!, (long)row["outbox_floor"]!);
        if (outbox is { } on && on != (meta.Outbox == 1))
        {
            var floor = on ? meta.LastSeq : 0;
            await db.TransactionAsync(async executor =>
            {
                await executor.RunAsync($"UPDATE {StoreInternal.StoreTables.Meta} SET outbox = ?, outbox_floor = ? WHERE singleton = 1", on ? 1L : 0L, floor);
                if (!on) await executor.RunAsync($"DELETE FROM {StoreInternal.StoreTables.Outbox}");
                return true;
            });
            meta = meta with { Outbox = on ? 1 : 0, OutboxFloor = floor };
        }
        return new TestStore(db, meta, await StoreInternal.ReadCatalogAsync(db));
    }

    public TableRecord? Table(string name) => catalog.GetValueOrDefault(name);

    public long? TableVersion(string name)
    {
        long? recorded = catalog.TryGetValue(name, out var record) ? record.Version : null;
        if (!announced.TryGetValue(name, out var version) || (recorded is { } at && at >= version)) return recorded;
        return version;
    }

    public Action AnnounceTable(string name, long version)
    {
        announced[name] = version;
        return () =>
        {
            if (announced.TryGetValue(name, out var current) && current == version) announced.Remove(name);
        };
    }

    public Task<long> CommitAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context)
    {
        try
        {
            AssertUsable();
        }
        catch (Exception error)
        {
            return Task.FromException<long>(error);
        }
        if (nextCommit is { } hold)
        {
            nextCommit = null;
            writes = hold(writes);
        }
        async Task<long> Queued(Task before)
        {
            await before.ConfigureAwait(false);
            return await StepAsync(writes, fence, context).ConfigureAwait(false);
        }
        var step = Queued(tail);
        tail = Settled(step);
        return step;
    }

    /// <summary><paramref name="task"/> settled either way; complete at once when it already is (no continuation is scheduled).</summary>
    private static async Task Settled(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The commit's own caller sees its failure.
        }
    }

    public async Task<T> ReadAsync<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context)
    {
        AssertUsable();
        underway++;
        T result;
        try
        {
            result = await read(db).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            AssertUsable();
            if (context.AbortSignal is { IsCancellationRequested: true })
                throw new ChordError("aborted", "store: read aborted", cause: error);
            throw Fail(error);
        }
        finally
        {
            if (--underway == 0) drained?.TrySetResult();
        }
        AssertUsable();
        return result;
    }

    /// <summary>Up to <paramref name="limit"/> outbox commits after <paramref name="afterSeq"/>, each <c>{ seq, nextId, writes }</c> with
    /// its writes parsed (upstream <c>outbox.read</c>, without the floor's checks).</summary>
    internal async Task<List<JsonObject>> OutboxAsync(long afterSeq, long limit, ChordContext context)
    {
        Program.Check(HasOutbox, "the store has no outbox");
        var rows = await ReadAsync(executor => executor.AllAsync(
            $"SELECT seq, format, next_id, writes FROM {StoreInternal.StoreTables.Outbox} WHERE seq > ? ORDER BY seq LIMIT ?", afterSeq, limit), context);
        return [.. rows.Select(row => new JsonObject
        {
            ["seq"] = (long)row["seq"]!,
            ["nextId"] = (long)row["next_id"]!,
            ["writes"] = JsonNode.Parse((string)row["writes"]!),
        })];
    }

    /// <summary>Holds the next commit step inside its backend transaction, after its admission: <c>Entered</c> completes there,
    /// <c>Release</c> lets it finish; later commits run as usual (upstream <c>holdNextCommit</c>).</summary>
    internal (Task Entered, Action Release) HoldNextCommit()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nextCommit = writes => [.. writes, new HeldWrite(entered, gate)];
        return (entered.Task, () => gate.TrySetResult());
    }

    /// <summary>Throws <c>unavailable/storage_failed</c> once the store stopped, <c>released</c> once it closed.</summary>
    internal void AssertUsable()
    {
        if (closed) throw new ChordError("released", "store: closed", "closed");
        if (failure is not null) throw StorageFailed(failure);
    }

    /// <summary>Closes the store: waits for the commit queue and the reads underway, closes the database; idempotent.</summary>
    internal Task CloseAsync() => closing ??= CloseCoreAsync();

    public async ValueTask DisposeAsync() => await CloseAsync();

    private async Task CloseCoreAsync()
    {
        closed = true;
        await tail;
        if (underway > 0)
        {
            drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await drained.Task;
        }
        await db.CloseAsync();
    }

    private async Task<long> StepAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context)
    {
        AssertUsable();
        if (!fence.Admit()) throw new ChordError("released", "store: the commit's attempt ended", "discarded");
        if (writes.Count == 0)
        {
            try
            {
                fence.Adopt(LastSeq, []);
            }
            catch (Exception error)
            {
                throw Fail(error);
            }
            return LastSeq;
        }
        var seq = LastSeq + 1;
        Exception? thrown = null;
        Step done;
        try
        {
            done = await db.TransactionAsync(async executor =>
            {
                try
                {
                    var step = new Step(executor, seq, catalog);
                    await WriteAsync(step, writes).ConfigureAwait(false);
                    await step.RunAsync($"UPDATE {StoreInternal.StoreTables.Meta} SET last_seq = ?, next_id = ? WHERE singleton = 1", step.Seq, nextId).ConfigureAwait(false);
                    return step;
                }
                catch (Exception error)
                {
                    thrown = error;
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The database rolled back and fails with the step's own error: a refusal, the store goes on (D4).
            if (ReferenceEquals(error, thrown)) throw;
            throw Fail(error);
        }
        LastSeq = seq;
        foreach (var (name, record) in done.Tables) catalog[name] = record;
        context.Count("chord.store.commits");
        context.Count("chord.store.statements", done.Statements);
        try
        {
            fence.Adopt(seq, done.Created);
        }
        catch (Exception error)
        {
            throw Fail(error);
        }
        return seq;
    }

    /// <summary>The step's writes: each staged write in its phase, the catalog, the outbox commit (upstream <c>#write</c>).</summary>
    private async Task WriteAsync(Step step, IReadOnlyList<IStagedWrite> writes)
    {
        var recorded = HasOutbox ? new List<StoreWrite>() : null;
        foreach (var order in (WriteOrder[])[WriteOrder.Retire, WriteOrder.Create, WriteOrder.Write])
        {
            foreach (var write in writes)
            {
                if (write.Order != order) continue;
                var applied = await write.ApplyAsync(step).ConfigureAwait(false);
                recorded?.AddRange(applied);
            }
        }
        await StoreInternal.WriteCatalogAsync(step).ConfigureAwait(false);
        if (recorded is not null)
        {
            // JSON.stringify of the array: each write's own text, comma-separated.
            var json = "[" + string.Join(",", recorded.Select(write => write.ToString())) + "]";
            await step.RunAsync($"INSERT INTO {StoreInternal.StoreTables.Outbox} (seq, format, next_id, writes) VALUES (?, ?, ?, ?)", step.Seq, 1L, nextId, json)
                .ConfigureAwait(false);
        }
    }

    private ChordError Fail(Exception error)
    {
        if (!closed && failure is null) failure = error;
        return StorageFailed(error);
    }

    private static ChordError StorageFailed(Exception error) => new("unavailable", "store: storage failed", "storage_failed", cause: error);

    /// <summary>The write holdNextCommit appends: it signals its entry, waits for the release, and records a <c>dropIndex</c>.</summary>
    private sealed class HeldWrite(TaskCompletionSource entered, TaskCompletionSource gate) : IStagedWrite
    {
        public WriteOrder Order => WriteOrder.Write;

        public async Task<IReadOnlyList<StoreWrite>> ApplyAsync(Step step)
        {
            entered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            return [new TableWrite.DropIndex("x", "y")];
        }
    }
}

/// <summary>An <see cref="ISqliteDatabase"/> over Microsoft.Data.Sqlite: one operation or transaction at a time, positional <c>?</c>
/// parameters numbered in order, rows as maps of column to SQLite value in the statement's column order.</summary>
internal sealed class TestSqliteDatabase : ISqliteDatabase
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Executor executor;
    private bool closed;

    internal TestSqliteDatabase(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        executor = new Executor(this);
    }

    public Task ExecAsync(string sql) => Queued(() => executor.ExecAsync(sql));

    public Task RunAsync(string sql, params object?[] parameters) => Queued(() => executor.RunAsync(sql, parameters));

    public Task<long> RunCountedAsync(string sql, params object?[] parameters) => Queued(() => executor.RunCountedAsync(sql, parameters));

    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => Queued(() => executor.GetAsync(sql, parameters));

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) => Queued(() => executor.AllAsync(sql, parameters));

    public async Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Execute("BEGIN IMMEDIATE", []);
            T result;
            try
            {
                result = await callback(executor).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                try
                {
                    Execute("ROLLBACK", []);
                }
                catch (Exception rollback)
                {
                    throw new AggregateException(error, rollback);
                }
                throw;
            }
            Execute("COMMIT", []);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            closed = true;
            connection.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Queued(Func<Task> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> Queued<T>(Func<Task<T>> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private int Execute(string sql, object?[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private List<IReadOnlyDictionary<string, object?>> Query(string sql, object?[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.GetValue(index);
                row[reader.GetName(index)] = value is DBNull ? null : value;
            }
            rows.Add(row);
        }
        return rows;
    }

    private SqliteCommand Command(string sql, object?[] parameters)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        var command = connection.CreateCommand();
        command.CommandText = parameters.Length == 0 ? sql : Numbered(sql);
        for (var index = 0; index < parameters.Length; index++)
        {
            command.Parameters.Add(new SqliteParameter("?" + (index + 1).ToString(CultureInfo.InvariantCulture), parameters[index] switch
            {
                null => DBNull.Value,
                int integer => (long)integer,
                var value => value,
            }));
        }
        return command;
    }

    /// <summary>Numbers each <c>?</c> outside quoted text and identifiers (the store's SQL has no comments).</summary>
    private static string Numbered(string sql)
    {
        var text = new StringBuilder(sql.Length + 8);
        var number = 0;
        char? quote = null;
        foreach (var character in sql)
        {
            if (quote is { } open)
            {
                if (character == open) quote = null;
                text.Append(character);
            }
            else if (character is '\'' or '"')
            {
                quote = character;
                text.Append(character);
            }
            else if (character == '?') text.Append('?').Append((++number).ToString(CultureInfo.InvariantCulture));
            else text.Append(character);
        }
        return text.ToString();
    }

    /// <summary>The executor the database and its transactions share (a transaction's calls run under the transaction's turn).</summary>
    private sealed class Executor(TestSqliteDatabase database) : ISqliteExecutor
    {
        public Task ExecAsync(string sql)
        {
            database.Execute(sql, []);
            return Task.CompletedTask;
        }

        public Task RunAsync(string sql, params object?[] parameters)
        {
            database.Execute(sql, parameters);
            return Task.CompletedTask;
        }

        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => Task.FromResult((long)database.Execute(sql, parameters));

        public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) =>
            Task.FromResult(database.Query(sql, parameters).FirstOrDefault());

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(database.Query(sql, parameters));
    }
}
