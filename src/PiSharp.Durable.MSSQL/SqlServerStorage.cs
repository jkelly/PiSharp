// Authored for PiSharp (decision 0007); upstream has no SQL Server storage. The queries, orderings, JSON columns and batch validation are
// PiSharp.Durable.Sqlite's SqliteStorage (upstream storage/sqlite/storage.ts) in T-SQL, so the backend passes the same storage conformance
// kit. Records are stored as their JSON text (Chord JsonValue.ToJson, which matches JSON.stringify), and indexed strings are stored
// JSON-encoded, as upstream does, so that lone UTF-16 surrogates survive the driver. Differences:
// - A store is one named partition of a schema (SqlServerMigrations); every statement filters on its store_id.
// - Opening a store takes a session-owned application lock that close releases, so one storage owns a store at a time (a SQLite file has
//   no lock of its own; the CLI's session lock covers it).
// - Commits lock the store's allocator row (UPDLOCK) first, and global ID ownership is read in one query per commit.
// - Inserts that SQLite writes with INSERT OR IGNORE or ON CONFLICT DO UPDATE are IF NOT EXISTS ... INSERT and UPDATE ... IF @@ROWCOUNT = 0
//   INSERT, which the allocator lock keeps race-free.
using System.Collections.Immutable;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.MSSQL;

/// <summary>Connection settings for durable SQL Server storage.</summary>
public sealed record SqlServerStorageOptions
{
    /// <summary>The schema that holds the durable tables; default <c>dbo</c>. It is created, with the tables, when missing.</summary>
    public string? Schema { get; init; }

    /// <summary>How long opening waits for another storage to close the same store, or for another connection to finish migrating the
    /// schema. Defaults to 5,000 ms; 0 fails at once.</summary>
    public int? LockTimeoutMs { get; init; }
}

/// <summary>SQL Server implementation of the durable storage contract (spec §11). A database schema holds any number of named stores, each
/// the equivalent of one SQLite storage file; one storage at a time owns a store.</summary>
public sealed class SqlServerStorage : IStorage
{
    private const int DefaultLockTimeoutMs = 5_000;

    // SQL Server accepts at most 2,100 parameters per request.
    private const int IdsPerQuery = 1_000;

    private readonly SqlServerDatabase db;
    private readonly string schema;
    private readonly int storeId;
    private readonly string storeLock;
    private readonly Lock gate = new();
    private long nextId;
    private bool closed;
    private Task? closing;
    private int admittedReads;
    private TaskCompletionSource? readsDrained;

    private SqlServerStorage(SqlServerDatabase db, string schema, int storeId, string storeLock, long nextId)
    {
        this.db = db;
        this.schema = schema;
        this.storeId = storeId;
        this.storeLock = storeLock;
        this.nextId = nextId;
    }

    /// <summary>Opens or creates the store named <paramref name="store"/>: applies pending schema migrations, takes the store's lock and loads
    /// its ID allocator. Fails when another storage holds the store past <see cref="SqlServerStorageOptions.LockTimeoutMs"/>. The
    /// connection string must name the database; the storage keeps one unpooled connection until it closes.</summary>
    public static async Task<SqlServerStorage> OpenAsync(string connectionString, string store, SqlServerStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var (schemaName, timeout) = Settings(store, options);
        var db = await SqlServerDatabase.OpenAsync(connectionString).ConfigureAwait(false);
        try
        {
            var resource = SqlServerLocks.StoreResource(await SqlServerMigrations.ApplyAsync(db, schemaName, timeout).ConfigureAwait(false), store);
            await db.HoldAsync(async executor =>
            {
                await SqlServerLocks.AcquireAsync(executor, resource, "Session", timeout, $"Durable SQL Server store \"{store}\" is open in another storage")
                    .ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            var quoted = SqlServerMigrations.Quote(schemaName);
            // The store lock already keeps every other connection away from this name, so the lookup takes no range locks: those would
            // deadlock two connections creating different stores in the same gap of the name index.
            var row = await db.TransactionAsync(async transaction =>
            {
                var existing = await transaction.GetAsync(SqlServerMigrations.Render(
                    "SELECT store_id, next_id FROM {schema}.durable_stores WHERE " + StoreName, quoted), EncodeIndexedString(store))
                    .ConfigureAwait(false);
                return existing ?? await transaction.GetAsync(SqlServerMigrations.Render("""
                    INSERT INTO {schema}.durable_stores (name, next_id, next_seq) OUTPUT inserted.store_id, inserted.next_id VALUES (@p0, 2, 1)
                    """, quoted), EncodeIndexedString(store)).ConfigureAwait(false);
            }).ConfigureAwait(false) ?? throw new InvalidOperationException("Durable SQL Server store metadata is missing");
            return new SqlServerStorage(db, quoted, checked((int)row.GetInt64("store_id")), resource, row.GetInt64("next_id"));
        }
        catch
        {
            try
            {
                await db.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the initialization failure.
            }
            throw;
        }
    }

    /// <summary>Deletes the store named <paramref name="store"/> and every record in it. Like <see cref="OpenAsync"/>, it first applies
    /// pending schema migrations and takes the store's lock, so it fails while a storage has the store open. Returns false when the store does
    /// not exist.</summary>
    public static async Task<bool> DeleteStoreAsync(string connectionString, string store, SqlServerStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var (schemaName, timeout) = Settings(store, options);
        var db = await SqlServerDatabase.OpenAsync(connectionString).ConfigureAwait(false);
        try
        {
            var schemaId = await SqlServerMigrations.ApplyAsync(db, schemaName, timeout).ConfigureAwait(false);
            var quoted = SqlServerMigrations.Quote(schemaName);
            return await db.TransactionAsync(async transaction =>
            {
                await SqlServerLocks.AcquireAsync(transaction, SqlServerLocks.StoreResource(schemaId, store), "Transaction", timeout,
                    $"Durable SQL Server store \"{store}\" is open in another storage").ConfigureAwait(false);
                var row = await transaction.GetAsync(SqlServerMigrations.Render(
                    "SELECT store_id FROM {schema}.durable_stores WHERE " + StoreName, quoted), EncodeIndexedString(store))
                    .ConfigureAwait(false);
                if (row is null) return false;
                await transaction.RunAsync(SqlServerMigrations.Render("""
                    DELETE FROM {schema}.durable_document_revisions WHERE store_id = @p0;
                    DELETE FROM {schema}.durable_documents WHERE store_id = @p0;
                    DELETE FROM {schema}.durable_submissions WHERE store_id = @p0;
                    DELETE FROM {schema}.durable_tasks WHERE store_id = @p0;
                    DELETE FROM {schema}.durable_entries WHERE store_id = @p0;
                    DELETE FROM {schema}.durable_conversations WHERE store_id = @p0;
                    DELETE FROM {schema}.durable_record_ids WHERE store_id = @p0;
                    DELETE FROM {schema}.durable_stores WHERE store_id = @p0;
                    """, quoted), checked((int)row.GetInt64("store_id"))).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            await db.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(writes);
        foreach (var write in writes) ArgumentNullException.ThrowIfNull(write, nameof(writes));
        long candidateNextId;
        lock (gate)
        {
            AssertOpen();
            candidateNextId = CandidateNextId(writes);
        }
        var documentActions = StorageRules.PrepareDocumentActions(writes);
        var seq = await db.TransactionAsync(async transaction =>
        {
            var metadata = await transaction.GetAsync(Sql("SELECT next_id, next_seq FROM {schema}.durable_stores WITH (UPDLOCK, HOLDLOCK) WHERE store_id = @p0"),
                storeId).ConfigureAwait(false) ?? throw new InvalidOperationException("Durable SQL Server store metadata is missing");
            var committedSeq = Ids.SeqFromNumber(metadata.GetInt64("next_seq"));
            await CheckGlobalIds(transaction, writes).ConfigureAwait(false);
            await CheckDocumentActions(transaction, documentActions).ConfigureAwait(false);
            foreach (var write in writes) await ApplyTableWrite(transaction, write, committedSeq).ConfigureAwait(false);
            await ApplyDocumentActions(transaction, documentActions, committedSeq).ConfigureAwait(false);
            await transaction.RunAsync(Sql("UPDATE {schema}.durable_stores SET next_id = @p1, next_seq = @p2 WHERE store_id = @p0"),
                storeId, Math.Max(metadata.GetInt64("next_id"), candidateNextId), committedSeq.Value + 1).ConfigureAwait(false);
            return committedSeq;
        }).ConfigureAwait(false);
        lock (gate) nextId = Math.Max(nextId, candidateNextId);
        return seq;
    }

    /// <inheritdoc/>
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId>
    {
        try
        {
            lock (gate)
            {
                AssertOpen();
                if (!Ids.IsSafeInteger(nextId)) throw new InvalidOperationException("ID space is exhausted");
                return Task.FromResult(TId.FromNumber(nextId++));
            }
        }
        catch (Exception error)
        {
            return Task.FromException<TId>(error);
        }
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context)
    {
        AssertOpenLocked();
        return await ReadConversation(id).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var filter = new Filter(storeId);
        var direction = filter.Scan(start);
        if (query.OwnerConversationId is { } ownerConversation) filter.Add("owner_conversation_id = ", ownerConversation.Value);
        if (query.OwnerTaskId is { } ownerTask) filter.Add("owner_task_id = ", ownerTask.Value);
        var top = filter.Bind((long)limit + 1);
        var rows = await db.AllAsync(Sql($"SELECT TOP ({top}) record FROM {{schema}}.durable_conversations WHERE {filter} ORDER BY id {direction}"),
            filter.Parameters).ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => ConversationRecord.FromJson(Record(row)))], limit, start.Order, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => AdmitRead(() => ReadEntry(id, null));

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        AdmitRead(() => ReadEntry(id, conversationId));

    /// <inheritdoc/>
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        AdmitRead(() => ReadLatestHeadMarker(conversationId, atOrBeforeEntryId));

    /// <inheritdoc/>
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        return AdmitRead(() => ReadEntries(query, limit, cursor));
    }

    private async Task<CommittedEntry?> ReadEntry(EntryId id, ConversationId? conversationId)
    {
        ConversationRecord? conversation = null;
        if (conversationId is { } visibleIn)
        {
            conversation = await ReadConversation(visibleIn).ConfigureAwait(false) ??
                throw new StorageRequestException($"Unknown conversation: {visibleIn}");
        }
        var row = await db.GetAsync(Sql("SELECT record, commit_seq FROM {schema}.durable_entries WHERE store_id = @p0 AND id = @p1"), storeId, id.Value)
            .ConfigureAwait(false);
        if (row is null) return null;
        var entry = EntryRecord.FromJson(Record(row));
        if (conversation is not null)
        {
            var upperEntryId = long.MaxValue;
            while (conversation.Id != entry.ConversationId)
            {
                if (conversation.Parent is not { } parent) return null;
                upperEntryId = Math.Min(upperEntryId, parent.At.Value);
                conversation = (await ReadConversation(parent.ConversationId).ConfigureAwait(false))!;
            }
            if (entry.Id.Value > upperEntryId) return null;
        }
        return new CommittedEntry(entry, Ids.SeqFromNumber(row.GetInt64("commit_seq")));
    }

    private async Task<EntryRecord?> ReadLatestHeadMarker(ConversationId conversationId, EntryId? atOrBeforeEntryId)
    {
        var conversation = await ReadConversation(conversationId).ConfigureAwait(false) ??
            throw new StorageRequestException($"Unknown conversation: {conversationId}");
        var upper = atOrBeforeEntryId?.Value;
        while (true)
        {
            var row = upper is not { } bound
                ? await db.GetAsync(Sql("""
                    SELECT TOP (1) record FROM {schema}.durable_entries
                    WHERE store_id = @p0 AND conversation_id = @p1 AND head IS NOT NULL ORDER BY id DESC
                    """), storeId, conversation.Id.Value).ConfigureAwait(false)
                : await db.GetAsync(Sql("""
                    SELECT TOP (1) record FROM {schema}.durable_entries
                    WHERE store_id = @p0 AND conversation_id = @p1 AND head IS NOT NULL AND id <= @p2 ORDER BY id DESC
                    """), storeId, conversation.Id.Value, bound).ConfigureAwait(false);
            if (row is not null) return EntryRecord.FromJson(Record(row));
            if (conversation.Parent is not { } parent) return null;
            upper = upper is { } previous ? Math.Min(previous, parent.At.Value) : parent.At.Value;
            conversation = (await ReadConversation(parent.ConversationId).ConfigureAwait(false))!;
        }
    }

    private async Task<Page<EntryRecord>> ReadEntries(EntryQuery query, int limit, JsonObjectValue? cursor)
    {
        var (order, after) = StorageScan.Start(query.Order, cursor, ScanOrder.Descending);
        if (order == ScanOrder.Ascending) return await ReadEntriesAscending(query, limit, after).ConfigureAwait(false);
        var conversation = await ReadConversation(query.ConversationId).ConfigureAwait(false) ??
            throw new StorageRequestException($"Unknown conversation: {query.ConversationId}");
        var upper = query.MaxEntryId?.Value;
        if (after is { } last) upper = Math.Min(upper ?? Ids.MaxSafeInteger, last - 1);
        var values = new List<EntryRecord>();
        while (true)
        {
            var filter = new Filter(storeId);
            filter.Add("conversation_id = ", conversation.Id.Value);
            if (query.MinEntryId is { } min) filter.Add("id >= ", min.Value);
            if (upper is { } max) filter.Add("id <= ", max);
            var top = filter.Bind((long)limit + 1 - values.Count);
            var rows = await db.AllAsync(Sql($"SELECT TOP ({top}) record FROM {{schema}}.durable_entries WHERE {filter} ORDER BY id DESC"),
                filter.Parameters).ConfigureAwait(false);
            values.AddRange(rows.Select(row => EntryRecord.FromJson(Record(row))));
            if (values.Count > limit || conversation.Parent is not { } parent) break;
            upper = upper is { } previous ? Math.Min(previous, parent.At.Value) : parent.At.Value;
            if (query.MinEntryId is { } lower && upper < lower.Value) break;
            conversation = (await ReadConversation(parent.ConversationId).ConfigureAwait(false))!;
        }
        return StorageRules.MakePage(values, limit, ScanOrder.Descending, entry => entry.Id.Value);
    }

    /// <summary>Oldest first: the fork chain's segments from the root conversation forward, each up to its fork point.</summary>
    private async Task<Page<EntryRecord>> ReadEntriesAscending(EntryQuery query, int limit, long? after)
    {
        var segments = new List<(ConversationId ConversationId, long? Upper)>();
        var conversation = await ReadConversation(query.ConversationId).ConfigureAwait(false) ??
            throw new StorageRequestException($"Unknown conversation: {query.ConversationId}");
        var upper = query.MaxEntryId?.Value;
        while (true)
        {
            segments.Add((conversation.Id, upper));
            if (conversation.Parent is not { } parent) break;
            upper = upper is { } previous ? Math.Min(previous, parent.At.Value) : parent.At.Value;
            if (query.MinEntryId is { } min && upper < min.Value) break;
            conversation = (await ReadConversation(parent.ConversationId).ConfigureAwait(false))!;
        }
        var lower = query.MinEntryId?.Value;
        if (after is { } last) lower = Math.Max(lower ?? last + 1, last + 1);
        var values = new List<EntryRecord>();
        segments.Reverse();
        foreach (var segment in segments)
        {
            var filter = new Filter(storeId);
            filter.Add("conversation_id = ", segment.ConversationId.Value);
            if (lower is { } bottom) filter.Add("id >= ", bottom);
            if (segment.Upper is { } top) filter.Add("id <= ", top);
            var count = filter.Bind((long)limit + 1 - values.Count);
            var rows = await db.AllAsync(Sql($"SELECT TOP ({count}) record FROM {{schema}}.durable_entries WHERE {filter} ORDER BY id ASC"),
                filter.Parameters).ConfigureAwait(false);
            values.AddRange(rows.Select(row => EntryRecord.FromJson(Record(row))));
            if (values.Count > limit) break;
        }
        return StorageRules.MakePage(values, limit, ScanOrder.Ascending, entry => entry.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
    {
        AssertOpenLocked();
        var row = await db.GetAsync(Sql("SELECT record FROM {schema}.durable_tasks WHERE store_id = @p0 AND id = @p1"), storeId, id.Value).ConfigureAwait(false);
        return row is null ? null : TaskRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public async Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var filter = new Filter(storeId);
        var direction = filter.Scan(start);
        if (query.ConversationId is { } conversationId) filter.Add("conversation_id = ", conversationId.Value);
        if (query.Kind is { } kind) filter.AddIndexedString("kind", EncodeIndexedString(kind));
        if (query.Status is { } status) filter.Add("status = ", WireName(status));
        if (query.AbortRequested is { } abortRequested) filter.Add("abort_requested = ", abortRequested);
        if (query.Background is { } background) filter.Add("background = ", background);
        var top = filter.Bind((long)limit + 1);
        var rows = await db.AllAsync(Sql($"SELECT TOP ({top}) record FROM {{schema}}.durable_tasks WHERE {filter} ORDER BY id {direction}"),
            filter.Parameters).ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => TaskRecord.FromJson(Record(row)))], limit, start.Order, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context)
    {
        AssertOpenLocked();
        var row = await db.GetAsync(Sql("SELECT record FROM {schema}.durable_submissions WHERE store_id = @p0 AND id = @p1"), storeId, id.Value)
            .ConfigureAwait(false);
        return row is null ? null : SubmissionRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public async Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var filter = new Filter(storeId);
        var direction = filter.Scan(start);
        if (query.ConversationId is { } conversationId) filter.Add("conversation_id = ", conversationId.Value);
        if (query.Status is { } status) filter.Add("status = ", WireName(status));
        var top = filter.Bind((long)limit + 1);
        var rows = await db.AllAsync(Sql($"SELECT TOP ({top}) record FROM {{schema}}.durable_submissions WHERE {filter} ORDER BY id {direction}"),
            filter.Parameters).ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => SubmissionRecord.FromJson(Record(row)))], limit, start.Order, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        AssertOpenLocked();
        var row = await db.GetAsync(Sql("""
            SELECT TOP (1) record FROM {schema}.durable_submissions
            WHERE store_id = @p0 AND conversation_id = @p1 AND request_hash = CAST(HASHBYTES('SHA2_256', @p2) AS BINARY(32)) AND request_id = @p2
            """), storeId, conversationId.Value, EncodeIndexedString(requestId)).ConfigureAwait(false);
        return row is null ? null : SubmissionRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public async Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(address);
        AssertOpenLocked();
        var parts = AddressParts.Of(address.Kind, address.Scope, address.Key);
        var sql = at.IsCurrent
            ? $"SELECT TOP (1) record FROM {{schema}}.durable_documents WHERE {AddressSql} AND retired_at IS NULL ORDER BY created_at DESC"
            : $$"""
                SELECT TOP (1) record FROM {schema}.durable_documents
                WHERE {{AddressSql}} AND created_at <= @p6 AND (retired_at IS NULL OR retired_at > @p6)
                ORDER BY created_at DESC
                """;
        List<object?> parameters = [storeId, parts.Kind, parts.ScopeKind, parts.OwnerId, parts.Family, parts.KeyValue];
        if (at.Seq is { } seq) parameters.Add(seq.Value);
        var row = await db.GetAsync(Sql(sql), [.. parameters]).ConfigureAwait(false);
        return row is null ? null : DocumentRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context)
    {
        try
        {
            AssertOpenLocked();
        }
        catch (Exception error)
        {
            return Task.FromException<StoredDocument?>(error);
        }
        // The record and revision queries must observe one committed state; a commit between them can replace the base. The store lock keeps
        // other storages out, and holding the connection keeps this storage's commits out.
        return db.HoldAsync(executor => MaterializeDocument(executor, id, at));
    }

    /// <inheritdoc/>
    public async Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var scope = ScopeColumns.Of(query.Scope);
        var filter = new Filter(storeId);
        filter.Add("scope_kind = ", scope.ScopeKind);
        filter.Add("owner_id = ", scope.OwnerId);
        filter.Add("id > ", StorageRules.CursorId(cursor) ?? -1L);
        if (query.Kind is { } kind) filter.AddIndexedString("kind", EncodeIndexedString(kind));
        if (query.At.Seq is not { } seq)
        {
            filter.Add("retired_at IS NULL");
        }
        else
        {
            var point = filter.Bind(seq.Value);
            filter.Add($"created_at <= {point}");
            filter.Add($"(retired_at IS NULL OR retired_at > {point})");
        }
        var top = filter.Bind((long)limit + 1);
        var rows = await db.AllAsync(Sql($"SELECT TOP ({top}) record FROM {{schema}}.durable_documents WHERE {filter} ORDER BY id"), filter.Parameters)
            .ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => DocumentRecord.FromJson(Record(row)))], limit, ScanOrder.Ascending, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public Task CloseAsync(DurableContext context)
    {
        lock (gate)
        {
            if (closing is null)
            {
                closed = true;
                var drained = admittedReads > 0 ? (readsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task : null;
                closing = CloseDatabase(drained);
            }
            return closing;
        }
    }

    private async Task CloseDatabase(Task? readsDrained)
    {
        if (readsDrained is not null) await readsDrained.ConfigureAwait(false);
        await db.CloseAsync(executor => SqlServerLocks.ReleaseAsync(executor, storeLock)).ConfigureAwait(false);
    }

    /// <summary>Runs a read that issues several queries. Close waits for admitted reads, so their later queries never reach a closed
    /// database. Single-query reads and transactions are already ordered before close by the database.</summary>
    private async Task<T> AdmitRead<T>(Func<Task<T>> read)
    {
        lock (gate)
        {
            AssertOpen();
            admittedReads++;
        }
        try
        {
            return await read().ConfigureAwait(false);
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (gate)
            {
                if (--admittedReads == 0) drained = readsDrained;
            }
            drained?.TrySetResult();
        }
    }

    private async Task<ConversationRecord?> ReadConversation(ConversationId id)
    {
        var row = await db.GetAsync(Sql("SELECT record FROM {schema}.durable_conversations WHERE store_id = @p0 AND id = @p1"), storeId, id.Value)
            .ConfigureAwait(false);
        return row is null ? null : ConversationRecord.FromJson(Record(row));
    }

    private async Task<StoredDocument?> MaterializeDocument(SqlServerExecutor executor, DocumentId id, DocumentPoint at)
    {
        var row = await executor.GetAsync(Sql("SELECT record FROM {schema}.durable_documents WHERE store_id = @p0 AND id = @p1"), storeId, id.Value)
            .ConfigureAwait(false);
        if (row is null) return null;
        var record = DocumentRecord.FromJson(Record(row));
        if (!at.IsCurrent && StorageRules.IsCurrentOnly(record))
            throw new StorageRequestException($"Document {id} does not retain historical content");
        if (!StorageRules.IsAliveAt(record, at)) return null;
        var upper = at.Seq?.Value ?? Ids.MaxSafeInteger;
        var documentBase = await executor.GetAsync(Sql("""
            SELECT TOP (1) seq, kind, version, content FROM {schema}.durable_document_revisions
            WHERE store_id = @p0 AND document_id = @p1 AND kind = N'base' AND seq <= @p2 ORDER BY seq DESC
            """), storeId, id.Value, upper).ConfigureAwait(false) ?? throw new InvalidOperationException($"Document {id} is missing a required base");
        var baseVersion = checked((int)documentBase.GetInt64("version"));
        var baseValue = JsonValue.Parse(documentBase.GetString("content")) as JsonObjectValue ??
            throw new InvalidOperationException($"Document {id} base is not a JSON object");
        var tail = await executor.AllAsync(Sql("""
            SELECT seq, kind, version, content FROM {schema}.durable_document_revisions
            WHERE store_id = @p0 AND document_id = @p1 AND seq > @p2 AND seq <= @p3 ORDER BY seq
            """), storeId, id.Value, documentBase.GetInt64("seq"), upper).ConfigureAwait(false);
        var value = StorageRules.Replay(id, baseVersion, baseValue, tail,
            revision => (revision.GetString("kind") == "delta", (int)Math.Clamp(revision.GetInt64("version"), int.MinValue, int.MaxValue)),
            revision => DecodeOps(revision.GetString("content")));
        return new StoredDocument(record, baseVersion, value, tail.Count);
    }

    private long CandidateNextId(IReadOnlyList<StorageWrite> writes)
    {
        var candidate = nextId;
        foreach (var write in writes)
        {
            if (StorageRules.Claim(write) is { } claim) candidate = Math.Max(candidate, claim.Id + 1);
        }
        return candidate;
    }

    private async Task CheckGlobalIds(SqlServerExecutor executor, IReadOnlyList<StorageWrite> writes)
    {
        var owners = new Dictionary<long, StorageTable?>();
        foreach (var write in writes)
        {
            if (StorageRules.Claim(write) is { } claim) owners.TryAdd(claim.Id, null);
        }
        foreach (var chunk in owners.Keys.ToList().Chunk(IdsPerQuery))
        {
            var filter = new Filter(storeId);
            var ids = string.Join(", ", chunk.Select(id => filter.Bind(id)));
            var rows = await executor.AllAsync(Sql($"SELECT id, record_type FROM {{schema}}.durable_record_ids WHERE {filter} AND id IN ({ids})"),
                filter.Parameters).ConfigureAwait(false);
            foreach (var row in rows) owners[row.GetInt64("id")] = ParseTable(row.GetString("record_type"));
        }
        StorageRules.CheckGlobalIds(writes, id => owners[id]);
    }

    private async Task CheckDocumentActions(SqlServerExecutor executor, OrderedDictionary<long, DocumentBatchAction> actions)
    {
        var stored = new Dictionary<long, DocumentCheckState?>();
        var current = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var (id, action) in actions)
        {
            var row = await executor.GetAsync(Sql("SELECT record FROM {schema}.durable_documents WHERE store_id = @p0 AND id = @p1"), storeId, id)
                .ConfigureAwait(false);
            var existing = row is null ? null : DocumentRecord.FromJson(Record(row));
            int? lastVersion = null;
            if (existing is not null && action.Content is DocumentDelta)
            {
                var previous = await executor.GetAsync(Sql("""
                    SELECT TOP (1) version FROM {schema}.durable_document_revisions WHERE store_id = @p0 AND document_id = @p1 ORDER BY seq DESC
                    """), storeId, id).ConfigureAwait(false);
                lastVersion = previous is null ? null : checked((int)previous.GetInt64("version"));
            }
            stored[id] = existing is null ? null : new DocumentCheckState(existing, lastVersion);
            IDocumentDescriptor? descriptor = action.Create is not null ? action.Create : existing;
            if (descriptor is null) continue;
            var key = StorageRules.AddressKey(descriptor);
            if (!current.ContainsKey(key)) current[key] = await CurrentDocumentId(executor, descriptor).ConfigureAwait(false);
        }
        StorageRules.CheckDocumentActions(actions, id => stored[id], key => current[key]);
    }

    private async Task<long?> CurrentDocumentId(SqlServerExecutor executor, IDocumentDescriptor address)
    {
        var parts = AddressParts.Of(address.Kind, address.Scope, address.Key);
        var row = await executor.GetAsync(Sql($"SELECT TOP (1) id FROM {{schema}}.durable_documents WHERE {AddressSql} AND retired_at IS NULL"),
            storeId, parts.Kind, parts.ScopeKind, parts.OwnerId, parts.Family, parts.KeyValue).ConfigureAwait(false);
        return row?.GetInt64("id");
    }

    private async Task ApplyTableWrite(SqlServerExecutor executor, StorageWrite write, Seq seq)
    {
        switch (write)
        {
            case ConversationWrite { Value: var value }:
                await executor.RunAsync(Sql(ClaimId + """
                    INSERT INTO {schema}.durable_conversations (store_id, id, owner_conversation_id, owner_task_id, record) VALUES (@p0, @p1, @p3, @p4, @p5);
                    """), storeId, value.Id.Value, StorageRules.Name(StorageTable.Conversation), value.Owner?.ConversationId.Value,
                    value.Owner?.TaskId.Value, value.ToJson().ToJson()).ConfigureAwait(false);
                break;
            case EntryWrite { Value: var value }:
                await executor.RunAsync(Sql(ClaimId + """
                    INSERT INTO {schema}.durable_entries (store_id, id, conversation_id, head, commit_seq, record) VALUES (@p0, @p1, @p3, @p4, @p5, @p6);
                    """), storeId, value.Id.Value, StorageRules.Name(StorageTable.Entry), value.ConversationId.Value, value.Head?.Value, seq.Value,
                    value.ToJson().ToJson()).ConfigureAwait(false);
                break;
            case TaskWrite { Value: var value }:
                await executor.RunAsync(Sql(ClaimId + """
                    UPDATE {schema}.durable_tasks SET conversation_id = @p3, kind = @p4, status = @p5, abort_requested = @p6, background = @p7, record = @p8
                    WHERE store_id = @p0 AND id = @p1;
                    IF @@ROWCOUNT = 0
                    INSERT INTO {schema}.durable_tasks (store_id, id, conversation_id, kind, status, abort_requested, background, record)
                    VALUES (@p0, @p1, @p3, @p4, @p5, @p6, @p7, @p8);
                    """), storeId, value.Id.Value, StorageRules.Name(StorageTable.Task), value.ConversationId.Value, EncodeIndexedString(value.Kind),
                    WireName(value.State.Status), value.AbortRequested, value.Background, value.ToJson().ToJson()).ConfigureAwait(false);
                break;
            case SubmissionWrite { Value: var value }:
                await executor.RunAsync(Sql(ClaimId + """
                    UPDATE {schema}.durable_submissions SET conversation_id = @p3, request_id = @p4, status = @p5, record = @p6
                    WHERE store_id = @p0 AND id = @p1;
                    IF @@ROWCOUNT = 0
                    INSERT INTO {schema}.durable_submissions (store_id, id, conversation_id, request_id, status, record)
                    VALUES (@p0, @p1, @p3, @p4, @p5, @p6);
                    """), storeId, value.Id.Value, StorageRules.Name(StorageTable.Submission), value.ConversationId.Value,
                    value.RequestId is null ? null : EncodeIndexedString(value.RequestId), WireName(value.Status), value.ToJson().ToJson())
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyDocumentActions(SqlServerExecutor executor, OrderedDictionary<long, DocumentBatchAction> actions, Seq seq)
    {
        foreach (var (id, action) in actions)
        {
            var content = action.Content;
            if (action.Copy is { } copy)
            {
                try
                {
                    var stored = await MaterializeDocument(executor, copy.Id, copy.At).ConfigureAwait(false) ??
                        throw new InvalidOperationException($"Fork source document {copy.Id} cannot be read");
                    StorageRules.CheckCopySource(stored, action.Create!);
                    content = DocumentContent.Base(stored.Version, stored.Value);
                }
                catch (Exception error) { throw new InvalidOperationException($"Document copy {id} was rejected", error); }
            }
            DocumentRecord record;
            if (action.Create is not null)
            {
                record = action.Create.ToRecord(seq, action.Retire ? seq : null);
                var parts = AddressParts.Of(record.Kind, record.Scope, record.Key);
                await executor.RunAsync(Sql(ClaimId + """
                    INSERT INTO {schema}.durable_documents
                    (store_id, id, kind, family, key_value, scope_kind, owner_id, created_at, retired_at, record)
                    VALUES (@p0, @p1, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10);
                    """), storeId, id, StorageRules.Name(StorageTable.Document), parts.Kind, parts.Family, parts.KeyValue, parts.ScopeKind,
                    parts.OwnerId, seq.Value, action.Retire ? seq.Value : null, record.ToJson().ToJson()).ConfigureAwait(false);
            }
            else
            {
                var row = (await executor.GetAsync(Sql("SELECT record FROM {schema}.durable_documents WHERE store_id = @p0 AND id = @p1"), storeId, id)
                    .ConfigureAwait(false))!;
                record = DocumentRecord.FromJson(Record(row));
            }

            if (content is not null)
            {
                if (content is DocumentBase && StorageRules.IsCurrentOnly(record)) await DeleteRevisions(executor, id).ConfigureAwait(false);
                var encodedContent = content switch
                {
                    DocumentBase documentBase => documentBase.Value.ToJson(),
                    DocumentDelta delta => JsonValue.Array(delta.Ops.Select(op => (JsonValue)op.ToJson())).ToJson(),
                    _ => throw new InvalidOperationException($"Document {id} has unknown content"),
                };
                await executor.RunAsync(Sql("""
                    INSERT INTO {schema}.durable_document_revisions (store_id, document_id, seq, kind, version, content) VALUES (@p0, @p1, @p2, @p3, @p4, @p5)
                    """), storeId, id, seq.Value, content is DocumentBase ? "base" : "delta", (long)content.Version, encodedContent).ConfigureAwait(false);
            }

            if (action.Retire)
            {
                if (action.Create is null)
                {
                    record = record with { RetiredAt = seq };
                    await executor.RunAsync(Sql("UPDATE {schema}.durable_documents SET retired_at = @p2, record = @p3 WHERE store_id = @p0 AND id = @p1"),
                        storeId, id, seq.Value, record.ToJson().ToJson()).ConfigureAwait(false);
                }
                if (StorageRules.IsCurrentOnly(record)) await DeleteRevisions(executor, id).ConfigureAwait(false);
            }
        }
    }

    private Task DeleteRevisions(SqlServerExecutor executor, long id) =>
        executor.RunAsync(Sql("DELETE FROM {schema}.durable_document_revisions WHERE store_id = @p0 AND document_id = @p1"), storeId, id);

    /// <summary>Claims record ID @p1 for table @p2 in store @p0 (SQLite's INSERT OR IGNORE INTO record_ids).</summary>
    private const string ClaimId = """
        IF NOT EXISTS (SELECT 1 FROM {schema}.durable_record_ids WHERE store_id = @p0 AND id = @p1)
        INSERT INTO {schema}.durable_record_ids (store_id, id, record_type) VALUES (@p0, @p1, @p2);

        """;

    /// <summary>The store named by @p0 (JSON-encoded).</summary>
    private const string StoreName = "name_hash = CAST(HASHBYTES('SHA2_256', @p0) AS BINARY(32)) AND name = @p0";

    /// <summary>A logical document address: store @p0, kind @p1, scope kind @p2, owner @p3, family @p4 and key @p5.</summary>
    private const string AddressSql = """
        store_id = @p0 AND kind_hash = CAST(HASHBYTES('SHA2_256', @p1) AS BINARY(32)) AND kind = @p1 AND scope_kind = @p2 AND owner_id = @p3
        AND family = @p4 AND key_hash = CAST(HASHBYTES('SHA2_256', @p5) AS BINARY(32)) AND key_value = @p5
        """;

    private string Sql(string statement) => SqlServerMigrations.Render(statement, schema);

    private void AssertOpen()
    {
        if (closed) throw new InvalidOperationException("SqlServerStorage is closed");
    }

    private void AssertOpenLocked()
    {
        lock (gate) AssertOpen();
    }

    private static (string Schema, int LockTimeoutMs) Settings(string store, SqlServerStorageOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        var schemaName = options?.Schema ?? SqlServerMigrations.DefaultSchema;
        if (schemaName.Length is 0 or > 128 || schemaName.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("The schema name must have 1 to 128 characters and no NUL", nameof(options));
        var timeout = options?.LockTimeoutMs ?? DefaultLockTimeoutMs;
        ArgumentOutOfRangeException.ThrowIfNegative(timeout, nameof(options));
        return (schemaName, timeout);
    }

    private static JsonValue Record(SqlServerRow row) => JsonValue.Parse(row.GetString("record"));

    private static IEnumerable<Op> DecodeOps(string content) =>
        JsonValue.Parse(content) is JsonArrayValue ops ? ops.Select(Op.FromJson).ToImmutableArray() : throw new FormatException("Document delta is not an operation array");

    private static StorageTable ParseTable(string text) =>
        Internal.WireName.TryParse<StorageTable>(text, out var table) ? table : throw new InvalidOperationException($"Unknown record type: {text}");

    private static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum => Internal.WireName.Of(value);

    // A driver may replace lone UTF-16 surrogates. JSON encoding keeps indexed identities lossless, as upstream does for SQLite; it also ends
    // every value with a quote, so SQL Server's padded comparison of trailing spaces cannot equate two values.
    private static string EncodeIndexedString(string value) => JsonValue.String(value).ToJson();

    /// <summary>A WHERE clause over one store: conditions joined by AND, with their positional bindings after the store's @p0.</summary>
    private sealed class Filter
    {
        private readonly List<string> clauses = ["store_id = @p0"];
        private readonly List<object?> parameters;

        public Filter(int storeId) => parameters = [storeId];

        public object?[] Parameters => [.. parameters];

        /// <summary>Adds a binding and returns its name.</summary>
        public string Bind(object? value)
        {
            parameters.Add(value);
            return "@p" + (parameters.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Adds <paramref name="condition"/> followed by a binding of <paramref name="value"/> (for example <c>"id &gt; "</c>).</summary>
        public void Add(string condition, object? value) => clauses.Add(condition + Bind(value));

        /// <summary>Adds a condition that needs no binding.</summary>
        public void Add(string condition) => clauses.Add(condition);

        /// <summary>Adds an exact match on an indexed string column and its hash.</summary>
        public void AddIndexedString(string column, string value)
        {
            var name = Bind(value);
            clauses.Add($"{column}_hash = CAST(HASHBYTES('SHA2_256', {name}) AS BINARY(32)) AND {column} = {name}");
        }

        /// <summary>The ID condition of a table scan; returns the ORDER BY direction.</summary>
        public string Scan(ScanStart start)
        {
            if (start.Order == ScanOrder.Ascending)
            {
                Add("id > ", start.After ?? -1);
                return "ASC";
            }
            Add("id < ", start.After ?? Ids.MaxSafeInteger);
            return "DESC";
        }

        public override string ToString() => string.Join(" AND ", clauses);
    }

    private readonly record struct ScopeColumns(string ScopeKind, long OwnerId)
    {
        public static ScopeColumns Of(DocumentScope scope) => scope.Kind switch
        {
            DocumentScopeKind.Session => new("session", 0),
            DocumentScopeKind.Conversation => new("conversation", scope.OwnerId),
            _ => new("task", scope.OwnerId),
        };
    }

    private readonly record struct AddressParts(string Kind, string ScopeKind, long OwnerId, bool Family, string KeyValue)
    {
        public static AddressParts Of(string kind, DocumentScope scope, string? key)
        {
            var columns = ScopeColumns.Of(scope);
            return new(EncodeIndexedString(kind), columns.ScopeKind, columns.OwnerId, key is not null, EncodeIndexedString(key ?? ""));
        }
    }
}
