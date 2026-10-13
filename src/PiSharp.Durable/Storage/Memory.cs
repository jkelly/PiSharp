// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/storage/memory.ts.
//
// Upstream clones every retained write and every read result, and deep-freezes prepared writes, to simulate the ownership boundary that
// serialization gives SQLite and JSONL (spec §4, §11.1). Records and JSON values here are immutable, so sharing them is already detached:
// no caller can change a retained value or a read result, and the copies are omitted. Upstream runs on one JavaScript thread; this port
// takes one lock per operation so that concurrent reads never observe a half-applied commit. The Session still serializes commits.
// The batch validation, addressing and paging rules are shared with SqliteStorage (StorageRules.cs).
using System.Collections.Immutable;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Storage;

/// <summary>A fully validated, detached state mutation whose application performs no fallible preparation (upstream
/// <c>PreparedMemoryCommit</c>). JSONL storage persists <see cref="Writes"/> and only then calls <see cref="Apply"/>.</summary>
public sealed class PreparedMemoryCommit
{
    private readonly Lock gate;
    private readonly Action apply;
    private bool applied;

    internal PreparedMemoryCommit(Lock gate, Seq seq, ImmutableArray<StorageWrite> writes, Action apply)
    {
        this.gate = gate; this.apply = apply; Seq = seq; Writes = writes;
    }

    /// <summary>The sequence the commit takes.</summary>
    public Seq Seq { get; }

    /// <summary>The detached writes for persistence, with every <see cref="DocumentCopyWrite"/> resolved to a
    /// <see cref="DocumentCreateWrite"/> of the copied base.</summary>
    public ImmutableArray<StorageWrite> Writes { get; }

    /// <summary>Publishes the mutation in memory; idempotent. Returns <see cref="Seq"/>.</summary>
    public Seq Apply()
    {
        lock (gate)
        {
            if (!applied)
            {
                applied = true;
                apply();
            }
            return Seq;
        }
    }
}

/// <summary>
/// The in-memory reference implementation of <see cref="IStorage"/> (spec §11.1). Its semantics are the reference every backend's
/// conformance is checked against. Commits are split into <see cref="PrepareCommit"/>, which validates and detaches a batch without
/// changing observable state, and <see cref="PreparedMemoryCommit.Apply"/>, which cannot fail (handoff package 4).
/// </summary>
public sealed class MemoryStorage : IStorage
{
    private sealed record Revision(DocumentContent Content, Seq Seq);

    private sealed class StoredDocumentState(DocumentRecord record, List<Revision> revisions)
    {
        public DocumentRecord Record { get; set; } = record;
        public List<Revision> Revisions { get; set; } = revisions;
    }

    private sealed class DocumentAddressIndex
    {
        public List<long> Ids { get; } = [];
        public long? CurrentId { get; set; }
    }

    private readonly Lock gate = new();
    private readonly Dictionary<long, StorageTable> recordTypes = [];
    private readonly Dictionary<long, ConversationRecord> conversations = [];
    private readonly List<long> conversationIds = [];
    private readonly Dictionary<long, List<long>> conversationIdsByOwnerConversation = [];
    private readonly Dictionary<long, List<long>> conversationIdsByOwnerTask = [];
    private readonly Dictionary<long, EntryRecord> entries = [];
    private readonly Dictionary<long, List<long>> entryIds = [];
    private readonly Dictionary<long, List<long>> headEntryIds = [];
    private readonly Dictionary<long, Seq> entryCommitSeqs = [];
    private readonly Dictionary<long, TaskRecord> tasks = [];
    private readonly List<long> taskIds = [];
    private readonly Dictionary<DurableTaskStatus, List<long>> taskIdsByStatus = Enum.GetValues<DurableTaskStatus>().ToDictionary(status => status, _ => new List<long>());
    private readonly Dictionary<long, SubmissionRecord> submissions = [];
    private readonly List<long> submissionIds = [];
    private readonly Dictionary<SubmissionStatus, List<long>> submissionIdsByStatus = Enum.GetValues<SubmissionStatus>().ToDictionary(status => status, _ => new List<long>());
    private readonly Dictionary<long, Dictionary<string, long>> submissionIdsByRequest = [];
    private readonly Dictionary<long, StoredDocumentState> documents = [];
    private readonly Dictionary<string, DocumentAddressIndex> documentAddresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<long>> documentIdsByScope = new(StringComparer.Ordinal);
    private long nextId = 2;
    private long nextSeq = 1;
    private bool closed;

    /// <inheritdoc/>
    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) => Run(() => PrepareCommit(writes).Apply());

    /// <summary>Validates and detaches one commit without changing observable state. <paramref name="seq"/> defaults to the next
    /// sequence; a replaying backend passes the sequence it recorded, which must not be below the next one.</summary>
    public PreparedMemoryCommit PrepareCommit(IReadOnlyList<StorageWrite> writes, Seq? seq = null)
    {
        ArgumentNullException.ThrowIfNull(writes);
        lock (gate)
        {
            AssertOpen();
            var commitSeq = seq ?? new Seq(nextSeq);
            if (!Ids.IsSafeInteger(commitSeq.Value) || commitSeq.Value < nextSeq)
                throw new InvalidOperationException($"Commit sequence {commitSeq} does not strictly increase");
            foreach (var write in writes) ArgumentNullException.ThrowIfNull(write, nameof(writes));
            var detached = ResolveDocumentCopies(writes);
            StorageRules.CheckGlobalIds(detached, id => recordTypes.TryGetValue(id, out var table) ? table : null);
            var actions = StorageRules.PrepareDocumentActions(detached);
            StorageRules.CheckDocumentActions(actions,
                id => documents.TryGetValue(id, out var stored) ? new DocumentCheckState(stored.Record, stored.Revisions.LastOrDefault()?.Content.Version) : null,
                key => documentAddresses.GetValueOrDefault(key)?.CurrentId);
            return new PreparedMemoryCommit(gate, commitSeq, detached, () => ApplyPreparedCommit(detached, actions, commitSeq));
        }
    }

    private ImmutableArray<StorageWrite> ResolveDocumentCopies(IReadOnlyList<StorageWrite> writes)
    {
        if (!writes.Any(write => write is DocumentCopyWrite)) return [.. writes];
        var changedDocumentIds = new HashSet<long>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case DocumentCreateWrite create: changedDocumentIds.Add(create.Record.Id.Value); break;
                case DocumentCopyWrite copy: changedDocumentIds.Add(copy.Record.Id.Value); break;
                case DocumentChangeWrite change: changedDocumentIds.Add(change.Id.Value); break;
                case DocumentRetireWrite retire: changedDocumentIds.Add(retire.Id.Value); break;
            }
        }
        return [.. writes.Select(write =>
        {
            if (write is not DocumentCopyWrite copy) return write;
            try
            {
                var sourceId = copy.Source.Id;
                if (changedDocumentIds.Contains(sourceId.Value))
                    throw new InvalidOperationException($"Fork source document {sourceId} is changed in the copy batch");
                var stored = MaterializeDocument(sourceId, copy.Source.At) ??
                    throw new InvalidOperationException($"Fork source document {sourceId} cannot be read");
                StorageRules.CheckCopySource(stored, copy.Record);
                // Upstream memory.ts: { kind: "base", version, value }, which a JSONL sidecar persists as written.
                return (StorageWrite)new DocumentCreateWrite(copy.Record, new DocumentBase(stored.Version, stored.Value) { KindFirst = true });
            }
            catch (StorageRejectedException) { throw; }
            catch (Exception error) { throw new StorageRejectedException($"Document copy {copy.Record.Id} was rejected", error); }
        })];
    }

    private void ApplyPreparedCommit(ImmutableArray<StorageWrite> prepared, OrderedDictionary<long, DocumentBatchAction> documentActions, Seq seq)
    {
        foreach (var write in prepared)
        {
            switch (write)
            {
                case ConversationWrite { Value: var value }:
                {
                    var id = value.Id.Value;
                    recordTypes[id] = StorageTable.Conversation;
                    conversations[id] = value;
                    InsertSorted(conversationIds, id);
                    if (value.Owner is { } owner)
                    {
                        InsertMapId(conversationIdsByOwnerConversation, owner.ConversationId.Value, id);
                        InsertMapId(conversationIdsByOwnerTask, owner.TaskId.Value, id);
                    }
                    nextId = Math.Max(nextId, id + 1);
                    break;
                }
                case EntryWrite { Value: var value }:
                {
                    var id = value.Id.Value;
                    recordTypes[id] = StorageTable.Entry;
                    entries[id] = value;
                    entryCommitSeqs[id] = seq;
                    InsertMapId(entryIds, value.ConversationId.Value, id);
                    if (value.Head is not null) InsertMapId(headEntryIds, value.ConversationId.Value, id);
                    nextId = Math.Max(nextId, id + 1);
                    break;
                }
                case TaskWrite { Value: var value }:
                {
                    var id = value.Id.Value;
                    recordTypes[id] = StorageTable.Task;
                    if (!tasks.TryGetValue(id, out var previous))
                    {
                        InsertSorted(taskIds, id);
                        InsertSorted(taskIdsByStatus[value.State.Status], id);
                    }
                    else if (previous.State.Status != value.State.Status)
                    {
                        RemoveSorted(taskIdsByStatus[previous.State.Status], id);
                        InsertSorted(taskIdsByStatus[value.State.Status], id);
                    }
                    tasks[id] = value;
                    nextId = Math.Max(nextId, id + 1);
                    break;
                }
                case SubmissionWrite { Value: var value }:
                {
                    var id = value.Id.Value;
                    recordTypes[id] = StorageTable.Submission;
                    submissions.TryGetValue(id, out var previous);
                    if (previous is null)
                    {
                        InsertSorted(submissionIds, id);
                        InsertSorted(submissionIdsByStatus[value.Status], id);
                    }
                    else if (previous.Status != value.Status)
                    {
                        RemoveSorted(submissionIdsByStatus[previous.Status], id);
                        InsertSorted(submissionIdsByStatus[value.Status], id);
                    }
                    if (previous?.RequestId is { } previousRequest &&
                        submissionIdsByRequest.TryGetValue(previous.ConversationId.Value, out var previousRequests) &&
                        previousRequests.TryGetValue(previousRequest, out var previousOwner) && previousOwner == id)
                    {
                        previousRequests.Remove(previousRequest);
                        if (previousRequests.Count == 0) submissionIdsByRequest.Remove(previous.ConversationId.Value);
                    }
                    submissions[id] = value;
                    if (value.RequestId is { } requestId)
                    {
                        if (!submissionIdsByRequest.TryGetValue(value.ConversationId.Value, out var requests))
                            submissionIdsByRequest[value.ConversationId.Value] = requests = new(StringComparer.Ordinal);
                        requests[requestId] = id;
                    }
                    nextId = Math.Max(nextId, id + 1);
                    break;
                }
                case DocumentCopyWrite:
                    throw new InvalidOperationException("Prepared document copy was not resolved");
            }
        }
        ApplyDocumentActions(documentActions, seq);
        nextSeq = seq.Value + 1;
    }

    /// <inheritdoc/>
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => Run(() =>
    {
        lock (gate)
        {
            AssertOpen();
            if (!Ids.IsSafeInteger(nextId)) throw new InvalidOperationException("ID space is exhausted");
            return TId.FromNumber(nextId++);
        }
    });

    /// <inheritdoc/>
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => Read(() =>
        conversations.GetValueOrDefault(id.Value));

    /// <inheritdoc/>
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) => Read(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        IReadOnlyList<long> ids = query.OwnerTaskId is { } ownerTask ? conversationIdsByOwnerTask.GetValueOrDefault(ownerTask.Value) ?? []
            : query.OwnerConversationId is { } ownerConversation ? conversationIdsByOwnerConversation.GetValueOrDefault(ownerConversation.Value) ?? []
            : conversationIds;
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var values = new List<ConversationRecord>();
        foreach (var index in ScanIndexes(ids, start))
        {
            if (values.Count > limit) break;
            var value = conversations[ids[index]];
            if (query.OwnerConversationId is { } owner && value.Owner?.ConversationId != owner) continue;
            values.Add(value);
        }
        return StorageRules.MakePage(values, limit, start.Order, value => value.Id.Value);
    });

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => Read(() =>
        entries.TryGetValue(id.Value, out var entry) ? new CommittedEntry(entry, entryCommitSeqs[id.Value]) : null);

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) => Read(() =>
        VisibleEntries(conversationId, id.Value, id.Value).FirstOrDefault() is { } entry ? new CommittedEntry(entry, entryCommitSeqs[id.Value]) : null);

    /// <inheritdoc/>
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) => Read(() =>
    {
        if (!conversations.ContainsKey(conversationId.Value)) throw new InvalidOperationException($"Unknown conversation: {conversationId}");
        var currentId = conversationId.Value;
        var upperEntryId = atOrBeforeEntryId?.Value ?? long.MaxValue;
        while (true)
        {
            var ids = headEntryIds.GetValueOrDefault(currentId) ?? [];
            var index = UpperBound(ids, upperEntryId) - 1;
            if (index >= 0) return entries[ids[index]];
            var conversation = conversations[currentId];
            if (conversation.Parent is not { } parent) return null;
            upperEntryId = Math.Min(upperEntryId, parent.At.Value);
            currentId = parent.ConversationId.Value;
        }
    });

    /// <inheritdoc/>
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) => Read(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        var (order, after) = StorageScan.Start(query.Order, cursor, ScanOrder.Descending);
        // The cursor narrows the bound on the side the scan moves away from.
        var minEntryId = query.MinEntryId?.Value;
        var maxEntryId = query.MaxEntryId?.Value;
        if (after is { } descendingAfter && order == ScanOrder.Descending) maxEntryId = Math.Min(maxEntryId ?? descendingAfter, descendingAfter - 1);
        if (after is { } ascendingAfter && order == ScanOrder.Ascending) minEntryId = Math.Max(minEntryId ?? ascendingAfter, ascendingAfter + 1);
        var visible = new List<EntryRecord>();
        var range = order == ScanOrder.Descending
            ? VisibleEntries(query.ConversationId, minEntryId ?? long.MinValue, maxEntryId ?? long.MaxValue)
            : VisibleEntriesAscending(query.ConversationId, minEntryId ?? long.MinValue, maxEntryId ?? long.MaxValue);
        foreach (var entry in range)
        {
            visible.Add(entry);
            if (visible.Count > limit) break;
        }
        return StorageRules.MakePage(visible, limit, order, entry => entry.Id.Value);
    });

    /// <inheritdoc/>
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Read(() => tasks.GetValueOrDefault(id.Value));

    /// <inheritdoc/>
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) => Read(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var ids = query.Status is { } status ? taskIdsByStatus[status] : taskIds;
        var values = new List<TaskRecord>();
        foreach (var index in ScanIndexes(ids, start))
        {
            if (values.Count > limit) break;
            var value = tasks[ids[index]];
            if (query.ConversationId is { } conversationId && value.ConversationId != conversationId) continue;
            if (query.Kind is { } kind && !string.Equals(value.Kind, kind, StringComparison.Ordinal)) continue;
            if (query.AbortRequested is { } abortRequested && value.AbortRequested != abortRequested) continue;
            if (query.Background is { } background && value.Background != background) continue;
            values.Add(value);
        }
        return StorageRules.MakePage(values, limit, start.Order, value => value.Id.Value);
    });

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => Read(() => submissions.GetValueOrDefault(id.Value));

    /// <inheritdoc/>
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) => Read(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        var ids = query.Status is { } status ? submissionIdsByStatus[status] : submissionIds;
        var values = new List<SubmissionRecord>();
        foreach (var index in ScanIndexes(ids, start))
        {
            if (values.Count > limit) break;
            var value = submissions[ids[index]];
            if (query.ConversationId is { } conversationId && value.ConversationId != conversationId) continue;
            values.Add(value);
        }
        return StorageRules.MakePage(values, limit, start.Order, value => value.Id.Value);
    });

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) => Read(() =>
    {
        ArgumentNullException.ThrowIfNull(requestId);
        return submissionIdsByRequest.TryGetValue(conversationId.Value, out var requests) && requests.TryGetValue(requestId, out var id)
            ? submissions[id] : null;
    });

    /// <inheritdoc/>
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) => Read(() =>
    {
        ArgumentNullException.ThrowIfNull(address);
        var index = documentAddresses.GetValueOrDefault(StorageRules.AddressKey(address.Kind, address.Scope, address.Key));
        if (at.IsCurrent) return index?.CurrentId is { } currentId ? documents[currentId].Record : null;
        foreach (var id in index?.Ids ?? [])
        {
            var record = documents[id].Record;
            if (StorageRules.IsAliveAt(record, at)) return record;
        }
        return null;
    });

    /// <inheritdoc/>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => Read(() => MaterializeDocument(id, at));

    /// <inheritdoc/>
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) => Read(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        var ids = documentIdsByScope.GetValueOrDefault(StorageRules.ScopeKey(query.Scope)) ?? [];
        var after = StorageRules.CursorId(cursor);
        var values = new List<DocumentRecord>();
        for (var index = after is { } last ? UpperBound(ids, last) : 0; index < ids.Count && values.Count <= limit; index++)
        {
            var record = documents[ids[index]].Record;
            if (query.Kind is { } kind && !string.Equals(record.Kind, kind, StringComparison.Ordinal)) continue;
            if (StorageRules.IsAliveAt(record, query.At)) values.Add(record);
        }
        return StorageRules.MakePage(values, limit, ScanOrder.Ascending, record => record.Id.Value);
    });

    /// <inheritdoc/>
    public Task CloseAsync(DurableContext context)
    {
        lock (gate) closed = true;
        return Task.CompletedTask;
    }

    private StoredDocument? MaterializeDocument(DocumentId id, DocumentPoint at)
    {
        if (!documents.TryGetValue(id.Value, out var stored)) return null;
        if (!at.IsCurrent && StorageRules.IsCurrentOnly(stored.Record))
            throw new InvalidOperationException($"Document {id} does not retain historical content");
        if (!StorageRules.IsAliveAt(stored.Record, at)) return null;
        var revisions = at.Seq is { } seq ? [.. stored.Revisions.Where(revision => revision.Seq <= seq)] : stored.Revisions;
        var baseIndex = revisions.Count - 1;
        while (baseIndex >= 0 && revisions[baseIndex].Content is not DocumentBase) baseIndex--;
        if (baseIndex < 0 || revisions[baseIndex].Content is not DocumentBase documentBase)
            throw new InvalidOperationException($"Document {id} is missing a required base");
        var value = StorageRules.Replay(id, documentBase.Version, documentBase.Value, revisions.Skip(baseIndex + 1),
            revision => (revision.Content is DocumentDelta, revision.Content.Version), revision => ((DocumentDelta)revision.Content).Ops);
        return new StoredDocument(stored.Record, documentBase.Version, value, revisions.Count - baseIndex - 1);
    }

    private IEnumerable<EntryRecord> VisibleEntries(ConversationId conversationId, long minEntryId, long maxEntryId)
    {
        if (!conversations.ContainsKey(conversationId.Value)) throw new InvalidOperationException($"Unknown conversation: {conversationId}");
        return Walk();

        IEnumerable<EntryRecord> Walk()
        {
            var currentId = conversationId.Value;
            var upperEntryId = maxEntryId;
            while (true)
            {
                var ids = entryIds.GetValueOrDefault(currentId) ?? [];
                for (var index = UpperBound(ids, upperEntryId) - 1; index >= 0; index--)
                {
                    var id = ids[index];
                    if (id < minEntryId) break;
                    yield return entries[id];
                }
                var conversation = conversations[currentId];
                if (conversation.Parent is not { } parent) break;
                upperEntryId = Math.Min(upperEntryId, parent.At.Value);
                if (upperEntryId < minEntryId) break;
                currentId = parent.ConversationId.Value;
            }
        }
    }

    /// <summary>Visible entries oldest first: the fork chain's segments from the root conversation forward.</summary>
    private IEnumerable<EntryRecord> VisibleEntriesAscending(ConversationId conversationId, long minEntryId, long maxEntryId)
    {
        if (!conversations.ContainsKey(conversationId.Value)) throw new InvalidOperationException($"Unknown conversation: {conversationId}");
        var segments = new List<(long ConversationId, long Upper)>();
        var currentId = conversationId.Value;
        var upperEntryId = maxEntryId;
        while (true)
        {
            segments.Add((currentId, upperEntryId));
            var conversation = conversations[currentId];
            if (conversation.Parent is not { } parent) break;
            upperEntryId = Math.Min(upperEntryId, parent.At.Value);
            if (upperEntryId < minEntryId) break;
            currentId = parent.ConversationId.Value;
        }
        segments.Reverse();
        return Walk();

        IEnumerable<EntryRecord> Walk()
        {
            foreach (var (segmentConversationId, upper) in segments)
            {
                var ids = entryIds.GetValueOrDefault(segmentConversationId) ?? [];
                for (var index = LowerBound(ids, minEntryId); index < ids.Count; index++)
                {
                    var id = ids[index];
                    if (id > upper) break;
                    yield return entries[id];
                }
            }
        }
    }

    private void ApplyDocumentActions(OrderedDictionary<long, DocumentBatchAction> actions, Seq seq)
    {
        foreach (var (id, action) in actions)
        {
            StoredDocumentState stored;
            if (action.Create is not null)
            {
                var record = action.Create.ToRecord(seq, action.Retire ? seq : null);
                stored = new StoredDocumentState(record, [new Revision(action.Content!, seq)]);
                recordTypes[id] = StorageTable.Document;
                documents[id] = stored;
                var key = StorageRules.AddressKey(record.Kind, record.Scope, record.Key);
                if (!documentAddresses.TryGetValue(key, out var address)) documentAddresses[key] = address = new DocumentAddressIndex();
                InsertSorted(address.Ids, id);
                var scopeKey = StorageRules.ScopeKey(record.Scope);
                if (!documentIdsByScope.TryGetValue(scopeKey, out var scopeIds)) documentIdsByScope[scopeKey] = scopeIds = [];
                InsertSorted(scopeIds, id);
                nextId = Math.Max(nextId, id + 1);
            }
            else
            {
                stored = documents[id];
                if (action.Content is not null)
                {
                    var revision = new Revision(action.Content, seq);
                    if (action.Content is DocumentBase && StorageRules.IsCurrentOnly(stored.Record)) stored.Revisions = [revision];
                    else stored.Revisions.Add(revision);
                }
            }

            if (action.Retire && action.Create is null) stored.Record = stored.Record with { RetiredAt = seq };
            if (action.Retire && StorageRules.IsCurrentOnly(stored.Record)) stored.Revisions = [];
            if (action.Create is not null || action.Retire)
            {
                var address = documentAddresses[StorageRules.AddressKey(stored.Record.Kind, stored.Record.Scope, stored.Record.Key)];
                if (action.Retire && address.CurrentId == id) address.CurrentId = null;
                if (action.Create is not null && !action.Retire) address.CurrentId = id;
            }
        }
    }

    private void AssertOpen()
    {
        if (closed) throw new InvalidOperationException("MemoryStorage is closed");
    }

    /// <summary>Runs a read under the lock after checking the storage is open; a failure rejects the returned task.</summary>
    private Task<T> Read<T>(Func<T> read) => Run(() =>
    {
        lock (gate)
        {
            AssertOpen();
            return read();
        }
    });

    private static Task<T> Run<T>(Func<T> body)
    {
        try { return Task.FromResult(body()); }
        catch (Exception error) { return Task.FromException<T>(error); }
    }

    /// <summary>Indexes of sorted <paramref name="ids"/> in scan order, after the cursor's ID when there is one.</summary>
    private static IEnumerable<int> ScanIndexes(IReadOnlyList<long> ids, ScanStart start)
    {
        if (start.Order == ScanOrder.Ascending)
        {
            for (var index = start.After is { } after ? UpperBound(ids, after) : 0; index < ids.Count; index++) yield return index;
        }
        else
        {
            for (var index = (start.After is { } after ? LowerBound(ids, after) : ids.Count) - 1; index >= 0; index--) yield return index;
        }
    }

    private static int LowerBound(IReadOnlyList<long> ids, long target)
    {
        int low = 0, high = ids.Count;
        while (low < high)
        {
            var middle = (int)((uint)(low + high) >> 1);
            if (ids[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(IReadOnlyList<long> ids, long target)
    {
        int low = 0, high = ids.Count;
        while (low < high)
        {
            var middle = (int)((uint)(low + high) >> 1);
            if (ids[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static void InsertSorted(List<long> ids, long id)
    {
        if (ids.Count == 0 || ids[^1] < id) ids.Add(id);
        else ids.Insert(LowerBound(ids, id), id);
    }

    private static void RemoveSorted(List<long> ids, long id)
    {
        var index = LowerBound(ids, id);
        if (index < ids.Count && ids[index] == id) ids.RemoveAt(index);
    }

    private static void InsertMapId(Dictionary<long, List<long>> index, long key, long id)
    {
        if (!index.TryGetValue(key, out var ids)) index[key] = ids = [];
        InsertSorted(ids, id);
    }
}
