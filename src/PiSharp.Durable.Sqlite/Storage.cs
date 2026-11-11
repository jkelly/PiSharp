// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/sqlite/storage.ts
//
// Group-commit internals now follow the frozen 2026 upstream commit.ts/statements.ts (see Commit.cs).
// The queries, orderings and JSON columns are upstream's. Records are stored as their JSON text (Chord JsonValue.ToJson, which matches
// JSON.stringify), and indexed strings (task kinds, request IDs, document kinds and keys) are stored JSON-encoded, as upstream does to
// keep lone UTF-16 surrogates lossless. Batch validation uses StorageRules for IDs and a SQLite-local document check so only created
// addresses need prefetching; each group applies upstream's rules sequentially in memory. Upstream also exports this class from storage/sqlite/index.ts.
// Every table name comes from one SqliteSchema, rendered with the optional prefix of SqliteStorageOptions (upstream's
// SqliteStorageOptions.prefix), and openNodeSqliteStorage's prefix check before the file opens (packages/durable/src/storage/sqlite/node.ts)
// is OpenAsync(path, options). Group checks decode stored state before semantic-refusal handling. Copy reads flush accepted writes
// first and use a watched executor; storage or decoding failures always reject the transaction with no receipts.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Sqlite;

/// <summary>Portable SQLite implementation of the durable storage contract (upstream <c>SqliteStorage</c>, spec §11).</summary>
public sealed partial class SqliteStorage : IStorage
{
    private readonly ISqliteDatabase db;
    private readonly SqliteTableNames tables;
    private readonly string metadataSql;
    private readonly Lock gate = new();
    private long nextId;
    private bool closed;
    private Task? closing;
    private int admittedReads;
    private TaskCompletionSource? readsDrained;

    private SqliteStorage(ISqliteDatabase db, SqliteSchema schema, long nextId)
    {
        this.db = db;
        tables = schema.Tables;
        metadataSql = MetadataSql(tables);
        this.nextId = nextId;
    }

    /// <summary>Initializes storage over an owned database facade (upstream <c>SqliteStorage.open</c>): applies pending migrations and
    /// loads the ID allocator. <see cref="SqliteStorageOptions.Prefix"/> namespaces every table and index; the connection settings of
    /// <paramref name="options"/> do not apply to a database the caller opened. On failure, an invalid prefix included, the database is
    /// closed.</summary>
    public static async Task<SqliteStorage> OpenAsync(ISqliteDatabase db, SqliteStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            var schema = SqliteSchema.Create(options?.Prefix);
            await SqliteMigrations.ApplyAsync(db, schema).ConfigureAwait(false);
            var metadata = await db.GetAsync(MetadataSql(schema.Tables)).ConfigureAwait(false) ??
                throw new InvalidOperationException("Durable SQLite metadata is missing");
            return new SqliteStorage(db, schema, ParseNextId(metadata));
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

    /// <summary>Opens or creates file-backed durable storage, optionally under a table prefix (upstream <c>openNodeSqliteStorage</c>). An
    /// invalid prefix fails before the file is opened.</summary>
    public static async Task<SqliteStorage> OpenAsync(string path, SqliteStorageOptions? options = null)
    {
        if (options?.Prefix is { } prefix) SqliteSchemaPrefix.Validate(prefix);
        return await OpenAsync(await SqliteConnectionDatabase.OpenAsync(path, options).ConfigureAwait(false), options).ConfigureAwait(false);
    }

    private static string MetadataSql(SqliteTableNames tables) => $"SELECT next_id, next_seq FROM {tables.DurableMetadata} WHERE singleton = 1";

    /// <summary>
    /// Commits <paramref name="batches"/> in one SQL transaction, each with its own seq. Each batch is checked before it writes anything,
    /// against the state the earlier ones left, and the first one refused ends the batch: the transaction commits the ones before it. No
    /// savepoints, which Durable Objects do not allow: the checks run before the writes instead.
    /// </summary>
    public async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context)
    {
        StorageRules.CheckCommitArguments(batches, receive);
        long candidateNextId;
        lock (gate)
        {
            AssertOpen();
            candidateNextId = nextId;
        }
        var seqs = new List<Seq>();
        Exception? refused = null;
        await db.TransactionAsync(async transaction =>
        {
            var metadata = await ReadMetadata(transaction).ConfigureAwait(false);
            var next = metadata.GetInt64("next_seq");
            // Decode persisted state before semantic checks, then advance it only for accepted batches.
            var state = await CommitState.Read(transaction, tables, batches).ConfigureAwait(false);
            var pending = new PendingWrites();
            foreach (var writes in batches)
            {
                OrderedDictionary<long, DocumentBatchAction> documentActions;
                try
                {
                    documentActions = StorageRules.PrepareDocumentActions(writes);
                    state.Check(writes, documentActions);
                }
                catch (Exception error)
                {
                    refused = error;
                    break;
                }
                if (documentActions.Values.Any(action => action.Copy is not null))
                {
                    // Copies observe all earlier accepted batches, including their retained revision history.
                    await pending.Flush(transaction, tables).ConfigureAwait(false);
                    var watched = new WatchedExecutor(transaction);
                    try { await ResolveCopies(watched, documentActions).ConfigureAwait(false); }
                    catch (Exception error) when (!watched.Failed)
                    {
                        refused = error;
                        break;
                    }
                }
                var seq = Ids.SeqFromNumber(next++);
                state.Apply(writes, documentActions, seq);
                pending.Add(writes, documentActions, seq, state);
                seqs.Add(seq);
                candidateNextId = StorageRules.CandidateNextId(candidateNextId, writes);
            }
            await pending.Flush(transaction, tables).ConfigureAwait(false);
            if (seqs.Count > 0) await WriteMetadata(transaction, Math.Max(ParseNextId(metadata), candidateNextId), next).ConfigureAwait(false);
        }).ConfigureAwait(false);
        lock (gate) nextId = Math.Max(nextId, candidateNextId);
        // COMMIT returned: the stored batches are durable.
        for (var index = 0; index < seqs.Count; index++) receive(index, seqs[index]);
        return new CommitResult(seqs.Count, refused);
    }

    // Decoding persisted rows is part of the read, even when the SQL call itself succeeded.
    // Mark it on the same watcher so malformed stored data never commits an earlier batch.
    private static T ReadStored<T>(ISqliteExecutor executor, Func<T> decode)
    {
        try { return decode(); }
        catch
        {
            if (executor is WatchedExecutor watched) watched.Failed = true;
            throw;
        }
    }

    private async Task<SqliteRow> ReadMetadata(ISqliteExecutor executor) =>
        await executor.GetAsync(metadataSql).ConfigureAwait(false) ?? throw new InvalidOperationException("Durable SQLite metadata is missing");

    private Task WriteMetadata(ISqliteExecutor executor, long nextId, long nextSeq) =>
        executor.RunAsync($"UPDATE {tables.DurableMetadata} SET next_id = ?, next_seq = ? WHERE singleton = 1",
            nextId.ToString(System.Globalization.CultureInfo.InvariantCulture), nextSeq);

    /// <summary>
    /// <paramref name="executor"/>, setting <see cref="Failed"/> when one of its calls throws, synchronously or by faulting or canceling its
    /// task (upstream <c>watchCalls</c>): then a failing check is the database's, not one of Durable's own. It forwards every call of
    /// <see cref="ISqliteExecutor"/>, <see cref="RunCountedAsync"/> included.
    /// </summary>
    private sealed class WatchedExecutor(ISqliteExecutor executor) : ISqliteExecutor
    {
        /// <summary>Whether a database call or persisted-data decode failed since this was last reset.</summary>
        public bool Failed { get; set; }

        public Task ExecAsync(string sql) => Watched(() => executor.ExecAsync(sql));

        public Task RunAsync(string sql, params object?[] parameters) => Watched(() => executor.RunAsync(sql, parameters));

        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => Watched(() => executor.RunCountedAsync(sql, parameters));

        public Task<SqliteRow?> GetAsync(string sql, params object?[] parameters) => Watched(() => executor.GetAsync(sql, parameters));

        public Task<IReadOnlyList<SqliteRow>> AllAsync(string sql, params object?[] parameters) => Watched(() => executor.AllAsync(sql, parameters));

        private async Task Watched(Func<Task> call)
        {
            try
            {
                await call().ConfigureAwait(false);
            }
            catch
            {
                Failed = true;
                throw;
            }
        }

        private async Task<T> Watched<T>(Func<Task<T>> call)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch
            {
                Failed = true;
                throw;
            }
        }
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
        var row = await db.GetAsync($"SELECT record FROM {tables.Conversations} WHERE id = ?", id.Value).ConfigureAwait(false);
        return row is null ? null : ConversationRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public async Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var (clause, param, direction) = ScanSql(start);
        var clauses = new List<string> { clause };
        var parameters = new List<object?> { param };
        if (query.OwnerConversationId is { } ownerConversation)
        {
            clauses.Add("owner_conversation_id = ?");
            parameters.Add(ownerConversation.Value);
        }
        if (query.OwnerTaskId is { } ownerTask)
        {
            clauses.Add("owner_task_id = ?");
            parameters.Add(ownerTask.Value);
        }
        parameters.Add((long)limit + 1);
        var rows = await db.AllAsync($"SELECT record FROM {tables.Conversations} WHERE {string.Join(" AND ", clauses)} ORDER BY id {direction} LIMIT ?",
            [.. parameters]).ConfigureAwait(false);
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
        var row = await db.GetAsync($"SELECT record, commit_seq FROM {tables.Entries} WHERE id = ?", id.Value).ConfigureAwait(false);
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
                ? await db.GetAsync($"SELECT record FROM {tables.Entries} WHERE conversation_id = ? AND head IS NOT NULL ORDER BY id DESC LIMIT 1",
                    conversation.Id.Value).ConfigureAwait(false)
                : await db.GetAsync($"SELECT record FROM {tables.Entries} WHERE conversation_id = ? AND head IS NOT NULL AND id <= ? ORDER BY id DESC LIMIT 1",
                    conversation.Id.Value, bound).ConfigureAwait(false);
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
            var clauses = new List<string> { "conversation_id = ?" };
            var parameters = new List<object?> { conversation.Id.Value };
            if (query.MinEntryId is { } min)
            {
                clauses.Add("id >= ?");
                parameters.Add(min.Value);
            }
            if (upper is { } max)
            {
                clauses.Add("id <= ?");
                parameters.Add(max);
            }
            parameters.Add((long)limit + 1 - values.Count);
            var rows = await db.AllAsync($"SELECT record FROM {tables.Entries} WHERE {string.Join(" AND ", clauses)} ORDER BY id DESC LIMIT ?",
                [.. parameters]).ConfigureAwait(false);
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
            var clauses = new List<string> { "conversation_id = ?" };
            var parameters = new List<object?> { segment.ConversationId.Value };
            if (lower is { } bottom)
            {
                clauses.Add("id >= ?");
                parameters.Add(bottom);
            }
            if (segment.Upper is { } top)
            {
                clauses.Add("id <= ?");
                parameters.Add(top);
            }
            parameters.Add((long)limit + 1 - values.Count);
            var rows = await db.AllAsync($"SELECT record FROM {tables.Entries} WHERE {string.Join(" AND ", clauses)} ORDER BY id ASC LIMIT ?",
                [.. parameters]).ConfigureAwait(false);
            values.AddRange(rows.Select(row => EntryRecord.FromJson(Record(row))));
            if (values.Count > limit) break;
        }
        return StorageRules.MakePage(values, limit, ScanOrder.Ascending, entry => entry.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
    {
        AssertOpenLocked();
        var row = await db.GetAsync($"SELECT record FROM {tables.Tasks} WHERE id = ?", id.Value).ConfigureAwait(false);
        return row is null ? null : TaskRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public async Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var (clause, param, direction) = ScanSql(start);
        var clauses = new List<string> { clause };
        var parameters = new List<object?> { param };
        if (query.ConversationId is { } conversationId)
        {
            clauses.Add("conversation_id = ?");
            parameters.Add(conversationId.Value);
        }
        if (query.Kind is { } kind)
        {
            clauses.Add("kind = ?");
            parameters.Add(EncodeIndexedString(kind));
        }
        if (query.Status is { } status)
        {
            clauses.Add("status = ?");
            parameters.Add(WireName(status));
        }
        if (query.AbortRequested is { } abortRequested)
        {
            clauses.Add("abort_requested = ?");
            parameters.Add(abortRequested ? 1L : 0L);
        }
        if (query.Background is { } background)
        {
            clauses.Add("background = ?");
            parameters.Add(background ? 1L : 0L);
        }
        parameters.Add((long)limit + 1);
        var rows = await db.AllAsync($"SELECT record FROM {tables.Tasks} WHERE {string.Join(" AND ", clauses)} ORDER BY id {direction} LIMIT ?",
            [.. parameters]).ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => TaskRecord.FromJson(Record(row)))], limit, start.Order, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context)
    {
        AssertOpenLocked();
        var row = await db.GetAsync($"SELECT record FROM {tables.Submissions} WHERE id = ?", id.Value).ConfigureAwait(false);
        return row is null ? null : SubmissionRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public async Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var (clause, param, direction) = ScanSql(start);
        var clauses = new List<string> { clause };
        var parameters = new List<object?> { param };
        if (query.ConversationId is { } conversationId)
        {
            clauses.Add("conversation_id = ?");
            parameters.Add(conversationId.Value);
        }
        if (query.Status is { } status)
        {
            clauses.Add("status = ?");
            parameters.Add(WireName(status));
        }
        parameters.Add((long)limit + 1);
        var rows = await db.AllAsync($"SELECT record FROM {tables.Submissions} WHERE {string.Join(" AND ", clauses)} ORDER BY id {direction} LIMIT ?",
            [.. parameters]).ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => SubmissionRecord.FromJson(Record(row)))], limit, start.Order, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        AssertOpenLocked();
        var row = await db.GetAsync($"SELECT record FROM {tables.Submissions} WHERE conversation_id = ? AND request_id = ?",
            conversationId.Value, EncodeIndexedString(requestId)).ConfigureAwait(false);
        return row is null ? null : SubmissionRecord.FromJson(Record(row));
    }

    /// <inheritdoc/>
    public async Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(address);
        AssertOpenLocked();
        var parts = AddressParts.Of(address.Kind, address.Scope, address.Key);
        var sql = at.IsCurrent
            ? $"""
              SELECT record FROM {tables.Documents}
              WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
              AND retired_at IS NULL ORDER BY created_at DESC LIMIT 1
              """
            : $"""
              SELECT record FROM {tables.Documents}
              WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
              AND created_at <= ? AND (retired_at IS NULL OR retired_at > ?)
              ORDER BY created_at DESC LIMIT 1
              """;
        List<object?> parameters = [parts.Kind, parts.ScopeKind, parts.OwnerId, parts.Family, parts.KeyValue];
        if (at.Seq is { } seq) parameters.AddRange([seq.Value, seq.Value]);
        var row = await db.GetAsync(sql, [.. parameters]).ConfigureAwait(false);
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
        // The record and revision queries must observe one committed state; a commit between them can replace the base.
        return db.TransactionAsync(transaction => MaterializeDocument(transaction, id, at));
    }

    /// <inheritdoc/>
    public async Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertOpenLocked();
        var scope = ScopeColumns.Of(query.Scope);
        var clauses = new List<string> { "scope_kind = ?", "owner_id = ?", "id > ?" };
        var parameters = new List<object?> { scope.ScopeKind, scope.OwnerId, StorageRules.CursorId(cursor) ?? -1L };
        if (query.Kind is { } kind)
        {
            clauses.Add("kind = ?");
            parameters.Add(EncodeIndexedString(kind));
        }
        if (query.At.Seq is not { } seq)
        {
            clauses.Add("retired_at IS NULL");
        }
        else
        {
            clauses.Add("created_at <= ?");
            clauses.Add("(retired_at IS NULL OR retired_at > ?)");
            parameters.AddRange([seq.Value, seq.Value]);
        }
        parameters.Add((long)limit + 1);
        var rows = await db.AllAsync($"SELECT record FROM {tables.Documents} WHERE {string.Join(" AND ", clauses)} ORDER BY id LIMIT ?",
            [.. parameters]).ConfigureAwait(false);
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
        await db.CloseAsync().ConfigureAwait(false);
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
        var row = await db.GetAsync($"SELECT record FROM {tables.Conversations} WHERE id = ?", id.Value).ConfigureAwait(false);
        return row is null ? null : ConversationRecord.FromJson(Record(row));
    }

    private async Task<StoredDocument?> MaterializeDocument(ISqliteExecutor executor, DocumentId id, DocumentPoint at)
    {
        var row = await executor.GetAsync($"SELECT record FROM {tables.Documents} WHERE id = ?", id.Value).ConfigureAwait(false);
        if (row is null) return null;
        var record = ReadStored(executor, () => DocumentRecord.FromJson(Record(row)));
        if (!at.IsCurrent && StorageRules.IsCurrentOnly(record))
            throw new StorageRequestException($"Document {id} does not retain historical content");
        if (!StorageRules.IsAliveAt(record, at)) return null;
        var upper = at.Seq?.Value ?? Ids.MaxSafeInteger;
        var documentBase = await executor.GetAsync($"""
            SELECT seq, kind, version, content FROM {tables.DocumentRevisions}
            WHERE document_id = ? AND kind = 'base' AND seq <= ? ORDER BY seq DESC LIMIT 1
            """, id.Value, upper).ConfigureAwait(false);
        documentBase = ReadStored(executor, () => documentBase ?? throw new InvalidOperationException($"Document {id} is missing a required base"));
        var baseVersion = ReadStored(executor, () => checked((int)documentBase.GetInt64("version")));
        var baseValue = ReadStored(executor, () => JsonValue.Parse(documentBase.GetString("content")) as JsonObjectValue ??
            throw new InvalidOperationException($"Document {id} base is not a JSON object"));
        var baseSeq = ReadStored(executor, () => documentBase.GetInt64("seq"));
        var tail = await executor.AllAsync($"""
            SELECT seq, kind, version, content FROM {tables.DocumentRevisions}
            WHERE document_id = ? AND seq > ? AND seq <= ? ORDER BY seq
            """, id.Value, baseSeq, upper).ConfigureAwait(false);
        var value = ReadStored(executor, () => StorageRules.Replay(id, baseVersion, baseValue, tail,
            revision => (revision.GetString("kind") == "delta", (int)Math.Clamp(revision.GetInt64("version"), int.MinValue, int.MaxValue)),
            revision => DecodeOps(revision.GetString("content"))));
        return new StoredDocument(record, baseVersion, value, tail.Count);
    }

    /// <summary>Resolves each copy to a base of its source's state before this batch, which may not change the source.</summary>
    private async Task ResolveCopies(ISqliteExecutor executor, OrderedDictionary<long, DocumentBatchAction> actions)
    {
        foreach (var (id, action) in actions)
        {
            if (action.Copy is not { } copy) continue;
            try
            {
                var stored = await MaterializeDocument(executor, copy.Id, copy.At).ConfigureAwait(false) ??
                    throw new InvalidOperationException($"Fork source document {copy.Id} cannot be read");
                StorageRules.CheckCopySource(stored, action.Create!);
                action.Content = DocumentContent.Base(stored.Version, stored.Value);
            }
            catch (Exception error) { throw new InvalidOperationException($"Document copy {id} was rejected", error); }
        }
    }

    private void AssertOpen()
    {
        if (closed) throw new InvalidOperationException("SqliteStorage is closed");
    }

    private void AssertOpenLocked()
    {
        lock (gate) AssertOpen();
    }

    private static JsonValue Record(SqliteRow row) => JsonValue.Parse(row.GetString("record"));

    private static IEnumerable<Op> DecodeOps(string content) =>
        JsonValue.Parse(content) is JsonArrayValue ops ? ops.Select(Op.FromJson).ToImmutableArray() : throw new FormatException("Document delta is not an operation array");

    private static long ParseNextId(SqliteRow metadata) =>
        long.Parse(metadata.GetString("next_id"), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);

    private static StorageTable ParseTable(string text) =>
        Internal.WireName.TryParse<StorageTable>(text, out var table) ? table : throw new InvalidOperationException($"Unknown record type: {text}");

    private static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum => Internal.WireName.Of(value);

    // Some SQLite bindings replace lone UTF-16 surrogates. JSON encoding keeps indexed identities lossless.
    private static string EncodeIndexedString(string value) => JsonValue.String(value).ToJson();

    /// <summary>The ID condition, its parameter, and the ORDER BY direction of a table scan.</summary>
    private static (string Clause, long Param, string Direction) ScanSql(ScanStart start) => start.Order == ScanOrder.Ascending
        ? ("id > ?", start.After ?? -1, "ASC")
        : ("id < ?", start.After ?? Ids.MaxSafeInteger, "DESC");

    private readonly record struct ScopeColumns(string ScopeKind, long OwnerId)
    {
        public static ScopeColumns Of(DocumentScope scope) => scope.Kind switch
        {
            DocumentScopeKind.Session => new("session", 0),
            DocumentScopeKind.Conversation => new("conversation", scope.OwnerId),
            _ => new("task", scope.OwnerId),
        };
    }

    private readonly record struct AddressParts(string Kind, string ScopeKind, long OwnerId, long Family, string KeyValue)
    {
        public static AddressParts Of(string kind, DocumentScope scope, string? key)
        {
            var columns = ScopeColumns.Of(scope);
            return new(EncodeIndexedString(kind), columns.ScopeKind, columns.OwnerId, key is null ? 0 : 1, EncodeIndexedString(key ?? ""));
        }
    }
}
