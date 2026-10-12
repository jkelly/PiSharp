// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/storage/sqlite/storage.ts
//
// The queries, orderings and JSON columns are upstream's. Records are stored as their JSON text (Chord JsonValue.ToJson, which matches
// JSON.stringify), and indexed strings (task kinds, request IDs, document kinds and keys) are stored JSON-encoded, as upstream does to
// keep lone UTF-16 surrogates lossless. Batch validation is shared with MemoryStorage (StorageRules): each check reads the rows it needs
// first and then applies the same rule, in upstream's order. Upstream also exports this class from storage/sqlite/index.ts.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Sqlite;

/// <summary>Portable SQLite implementation of the durable storage contract (upstream <c>SqliteStorage</c>, spec §11).</summary>
public sealed class SqliteStorage : IStorage
{
    private const string MetadataSql = "SELECT next_id, next_seq FROM durable_metadata WHERE singleton = 1";

    private readonly ISqliteDatabase db;
    private readonly Lock gate = new();
    private long nextId;
    private bool closed;
    private Task? closing;
    private int admittedReads;
    private TaskCompletionSource? readsDrained;

    private SqliteStorage(ISqliteDatabase db, long nextId)
    {
        this.db = db;
        this.nextId = nextId;
    }

    /// <summary>Initializes storage over an owned database facade (upstream <c>SqliteStorage.open</c>): applies pending migrations and
    /// loads the ID allocator. On failure the database is closed.</summary>
    public static async Task<SqliteStorage> OpenAsync(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await SqliteMigrations.ApplyAsync(db).ConfigureAwait(false);
            var metadata = await db.GetAsync(MetadataSql).ConfigureAwait(false) ??
                throw new InvalidOperationException("Durable SQLite metadata is missing");
            return new SqliteStorage(db, ParseNextId(metadata));
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

    /// <summary>Opens or creates file-backed durable storage (upstream <c>openNodeSqliteStorage</c>).</summary>
    public static async Task<SqliteStorage> OpenAsync(string path, SqliteStorageOptions? options = null) =>
        await OpenAsync(await SqliteConnectionDatabase.OpenAsync(path, options).ConfigureAwait(false)).ConfigureAwait(false);

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
            var metadata = await transaction.GetAsync(MetadataSql).ConfigureAwait(false) ??
                throw new InvalidOperationException("Durable SQLite metadata is missing");
            var committedSeq = Ids.SeqFromNumber(metadata.GetInt64("next_seq"));
            await CheckGlobalIds(transaction, writes).ConfigureAwait(false);
            await CheckDocumentActions(transaction, documentActions).ConfigureAwait(false);
            foreach (var write in writes) await ApplyTableWrite(transaction, write, committedSeq).ConfigureAwait(false);
            await ApplyDocumentActions(transaction, documentActions, committedSeq).ConfigureAwait(false);
            await transaction.RunAsync("UPDATE durable_metadata SET next_id = ?, next_seq = ? WHERE singleton = 1",
                Math.Max(ParseNextId(metadata), candidateNextId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                committedSeq.Value + 1).ConfigureAwait(false);
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
        var row = await db.GetAsync("SELECT record FROM conversations WHERE id = ?", id.Value).ConfigureAwait(false);
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
        var rows = await db.AllAsync($"SELECT record FROM conversations WHERE {string.Join(" AND ", clauses)} ORDER BY id {direction} LIMIT ?",
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
                throw new InvalidOperationException($"Unknown conversation: {visibleIn}");
        }
        var row = await db.GetAsync("SELECT record, commit_seq FROM entries WHERE id = ?", id.Value).ConfigureAwait(false);
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
            throw new InvalidOperationException($"Unknown conversation: {conversationId}");
        var upper = atOrBeforeEntryId?.Value;
        while (true)
        {
            var row = upper is not { } bound
                ? await db.GetAsync("SELECT record FROM entries WHERE conversation_id = ? AND head IS NOT NULL ORDER BY id DESC LIMIT 1",
                    conversation.Id.Value).ConfigureAwait(false)
                : await db.GetAsync("SELECT record FROM entries WHERE conversation_id = ? AND head IS NOT NULL AND id <= ? ORDER BY id DESC LIMIT 1",
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
            throw new InvalidOperationException($"Unknown conversation: {query.ConversationId}");
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
            var rows = await db.AllAsync($"SELECT record FROM entries WHERE {string.Join(" AND ", clauses)} ORDER BY id DESC LIMIT ?",
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
            throw new InvalidOperationException($"Unknown conversation: {query.ConversationId}");
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
            var rows = await db.AllAsync($"SELECT record FROM entries WHERE {string.Join(" AND ", clauses)} ORDER BY id ASC LIMIT ?",
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
        var row = await db.GetAsync("SELECT record FROM tasks WHERE id = ?", id.Value).ConfigureAwait(false);
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
        var rows = await db.AllAsync($"SELECT record FROM tasks WHERE {string.Join(" AND ", clauses)} ORDER BY id {direction} LIMIT ?",
            [.. parameters]).ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => TaskRecord.FromJson(Record(row)))], limit, start.Order, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context)
    {
        AssertOpenLocked();
        var row = await db.GetAsync("SELECT record FROM submissions WHERE id = ?", id.Value).ConfigureAwait(false);
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
        var rows = await db.AllAsync($"SELECT record FROM submissions WHERE {string.Join(" AND ", clauses)} ORDER BY id {direction} LIMIT ?",
            [.. parameters]).ConfigureAwait(false);
        return StorageRules.MakePage([.. rows.Select(row => SubmissionRecord.FromJson(Record(row)))], limit, start.Order, value => value.Id.Value);
    }

    /// <inheritdoc/>
    public async Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        AssertOpenLocked();
        var row = await db.GetAsync("SELECT record FROM submissions WHERE conversation_id = ? AND request_id = ?",
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
            ? """
              SELECT record FROM documents
              WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
              AND retired_at IS NULL ORDER BY created_at DESC LIMIT 1
              """
            : """
              SELECT record FROM documents
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
        var rows = await db.AllAsync($"SELECT record FROM documents WHERE {string.Join(" AND ", clauses)} ORDER BY id LIMIT ?",
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
        var row = await db.GetAsync("SELECT record FROM conversations WHERE id = ?", id.Value).ConfigureAwait(false);
        return row is null ? null : ConversationRecord.FromJson(Record(row));
    }

    private static async Task<StoredDocument?> MaterializeDocument(ISqliteExecutor executor, DocumentId id, DocumentPoint at)
    {
        var row = await executor.GetAsync("SELECT record FROM documents WHERE id = ?", id.Value).ConfigureAwait(false);
        if (row is null) return null;
        var record = DocumentRecord.FromJson(Record(row));
        if (!at.IsCurrent && StorageRules.IsCurrentOnly(record))
            throw new InvalidOperationException($"Document {id} does not retain historical content");
        if (!StorageRules.IsAliveAt(record, at)) return null;
        var upper = at.Seq?.Value ?? Ids.MaxSafeInteger;
        var documentBase = await executor.GetAsync("""
            SELECT seq, kind, version, content FROM document_revisions
            WHERE document_id = ? AND kind = 'base' AND seq <= ? ORDER BY seq DESC LIMIT 1
            """, id.Value, upper).ConfigureAwait(false) ?? throw new InvalidOperationException($"Document {id} is missing a required base");
        var baseVersion = checked((int)documentBase.GetInt64("version"));
        var baseValue = JsonValue.Parse(documentBase.GetString("content")) as JsonObjectValue ??
            throw new InvalidOperationException($"Document {id} base is not a JSON object");
        var tail = await executor.AllAsync("""
            SELECT seq, kind, version, content FROM document_revisions
            WHERE document_id = ? AND seq > ? AND seq <= ? ORDER BY seq
            """, id.Value, documentBase.GetInt64("seq"), upper).ConfigureAwait(false);
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

    private static async Task CheckGlobalIds(ISqliteExecutor executor, IReadOnlyList<StorageWrite> writes)
    {
        var owners = new Dictionary<long, StorageTable?>();
        foreach (var write in writes)
        {
            if (StorageRules.Claim(write) is not { } claim || owners.ContainsKey(claim.Id)) continue;
            var row = await executor.GetAsync("SELECT record_type FROM record_ids WHERE id = ?", claim.Id).ConfigureAwait(false);
            owners[claim.Id] = row is null ? null : ParseTable(row.GetString("record_type"));
        }
        StorageRules.CheckGlobalIds(writes, id => owners[id]);
    }

    private static async Task CheckDocumentActions(ISqliteExecutor executor, OrderedDictionary<long, DocumentBatchAction> actions)
    {
        var stored = new Dictionary<long, DocumentCheckState?>();
        var current = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var (id, action) in actions)
        {
            var row = await executor.GetAsync("SELECT record FROM documents WHERE id = ?", id).ConfigureAwait(false);
            var existing = row is null ? null : DocumentRecord.FromJson(Record(row));
            int? lastVersion = null;
            if (existing is not null && action.Content is DocumentDelta)
            {
                var previous = await executor.GetAsync("SELECT version FROM document_revisions WHERE document_id = ? ORDER BY seq DESC LIMIT 1", id)
                    .ConfigureAwait(false);
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

    private static async Task<long?> CurrentDocumentId(ISqliteExecutor executor, IDocumentDescriptor address)
    {
        var parts = AddressParts.Of(address.Kind, address.Scope, address.Key);
        var row = await executor.GetAsync("""
            SELECT id FROM documents
            WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ? AND retired_at IS NULL
            LIMIT 1
            """, parts.Kind, parts.ScopeKind, parts.OwnerId, parts.Family, parts.KeyValue).ConfigureAwait(false);
        return row?.GetInt64("id");
    }

    private static async Task ApplyTableWrite(ISqliteExecutor executor, StorageWrite write, Seq seq)
    {
        switch (write)
        {
            case ConversationWrite { Value: var value }:
                await ClaimId(executor, value.Id.Value, StorageTable.Conversation).ConfigureAwait(false);
                await executor.RunAsync("INSERT INTO conversations (id, owner_conversation_id, owner_task_id, record) VALUES (?, ?, ?, ?)",
                    value.Id.Value, value.Owner?.ConversationId.Value, value.Owner?.TaskId.Value, value.ToJson().ToJson()).ConfigureAwait(false);
                break;
            case EntryWrite { Value: var value }:
                await ClaimId(executor, value.Id.Value, StorageTable.Entry).ConfigureAwait(false);
                await executor.RunAsync("INSERT INTO entries (id, conversation_id, head, commit_seq, record) VALUES (?, ?, ?, ?, ?)",
                    value.Id.Value, value.ConversationId.Value, value.Head?.Value, seq.Value, value.ToJson().ToJson()).ConfigureAwait(false);
                break;
            case TaskWrite { Value: var value }:
                await ClaimId(executor, value.Id.Value, StorageTable.Task).ConfigureAwait(false);
                await executor.RunAsync("""
                    INSERT INTO tasks (id, conversation_id, kind, status, abort_requested, background, record)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    ON CONFLICT(id) DO UPDATE SET conversation_id = excluded.conversation_id, kind = excluded.kind,
                    status = excluded.status, abort_requested = excluded.abort_requested,
                    background = excluded.background, record = excluded.record
                    """, value.Id.Value, value.ConversationId.Value, EncodeIndexedString(value.Kind), WireName(value.State.Status),
                    value.AbortRequested ? 1L : 0L, value.Background ? 1L : 0L, value.ToJson().ToJson()).ConfigureAwait(false);
                break;
            case SubmissionWrite { Value: var value }:
                await ClaimId(executor, value.Id.Value, StorageTable.Submission).ConfigureAwait(false);
                await executor.RunAsync("""
                    INSERT INTO submissions (id, conversation_id, request_id, status, record) VALUES (?, ?, ?, ?, ?)
                    ON CONFLICT(id) DO UPDATE SET conversation_id = excluded.conversation_id,
                    request_id = excluded.request_id, status = excluded.status, record = excluded.record
                    """, value.Id.Value, value.ConversationId.Value, value.RequestId is null ? null : EncodeIndexedString(value.RequestId),
                    WireName(value.Status), value.ToJson().ToJson()).ConfigureAwait(false);
                break;
        }
    }

    private static Task ClaimId(ISqliteExecutor executor, long id, StorageTable table) =>
        executor.RunAsync("INSERT OR IGNORE INTO record_ids (id, record_type) VALUES (?, ?)", id, StorageRules.Name(table));

    private static async Task ApplyDocumentActions(ISqliteExecutor executor, OrderedDictionary<long, DocumentBatchAction> actions, Seq seq)
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
                catch (StorageRejectedException) { throw; }
                catch (Exception error) { throw new StorageRejectedException($"Document copy {id} was rejected", error); }
            }
            DocumentRecord record;
            if (action.Create is not null)
            {
                record = action.Create.ToRecord(seq, action.Retire ? seq : null);
                var parts = AddressParts.Of(record.Kind, record.Scope, record.Key);
                await ClaimId(executor, id, StorageTable.Document).ConfigureAwait(false);
                await executor.RunAsync("""
                    INSERT INTO documents
                    (id, kind, family, key_value, scope_kind, owner_id, created_at, retired_at, record)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """, id, parts.Kind, parts.Family, parts.KeyValue, parts.ScopeKind, parts.OwnerId, seq.Value,
                    action.Retire ? seq.Value : null, record.ToJson().ToJson()).ConfigureAwait(false);
            }
            else
            {
                var row = (await executor.GetAsync("SELECT record FROM documents WHERE id = ?", id).ConfigureAwait(false))!;
                record = DocumentRecord.FromJson(Record(row));
            }

            if (content is not null)
            {
                if (content is DocumentBase && StorageRules.IsCurrentOnly(record))
                    await executor.RunAsync("DELETE FROM document_revisions WHERE document_id = ?", id).ConfigureAwait(false);
                var encodedContent = content switch
                {
                    DocumentBase documentBase => documentBase.Value.ToJson(),
                    DocumentDelta delta => JsonValue.Array(delta.Ops.Select(op => (JsonValue)op.ToJson())).ToJson(),
                    _ => throw new InvalidOperationException($"Document {id} has unknown content"),
                };
                await executor.RunAsync("INSERT INTO document_revisions (document_id, seq, kind, version, content) VALUES (?, ?, ?, ?, ?)",
                    id, seq.Value, content is DocumentBase ? "base" : "delta", (long)content.Version, encodedContent).ConfigureAwait(false);
            }

            if (action.Retire)
            {
                if (action.Create is null)
                {
                    record = record with { RetiredAt = seq };
                    await executor.RunAsync("UPDATE documents SET retired_at = ?, record = ? WHERE id = ?", seq.Value, record.ToJson().ToJson(), id)
                        .ConfigureAwait(false);
                }
                if (StorageRules.IsCurrentOnly(record))
                    await executor.RunAsync("DELETE FROM document_revisions WHERE document_id = ?", id).ConfigureAwait(false);
            }
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
