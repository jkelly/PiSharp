// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/session/cached-storage.ts (CachedStorage, scopeOf, Lru). The
// write-through cache of hot Storage record reads for the Session that owns the Storage (area publication-caches, e5c6182ba).
//
// Concurrency. Upstream's cache runs on the Session's one JavaScript thread: a publication, which applies a batch (Published), never
// overlaps a read's bookkeeping. Here a publication runs on the writer's thread while line jobs and off-line reads run on others, so one
// short lock (gate) guards every table and the reads in flight. It is never held while the backend runs: a read takes its version under
// the lock, reads the backend outside it, and keeps the result under it again only if no publication since wrote its record, which is
// upstream's rule. A read on the line has touched its record's key first (GatedStorage), so no commit that writes it is unpublished when
// it starts, and a publication applies a batch here before the commit's keys leave the writer (Writer.cs), so a job that passed a gate
// finds the record the commit wrote.
//
// Records are immutable here (sealed records with init-only members, immutable task states and JSON values), so upstream's freeze has no
// counterpart: a kept record is the written object itself, shared with the Session's components that follow the publication (the
// scheduler's live tasks), and no reader can change it. That also keeps the TaskState instance a task returned, which run-end's
// EndedByCrashesStates is keyed by (Harness/Scheduler.cs), exactly as written.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>
/// Storage that serves hot record reads from memory (upstream <c>CachedStorage</c>): conversations, tasks and written submissions, each
/// table bounded by a count, the least recently used record leaving first, except live tasks, which are kept until they end, and the
/// ended tasks a task waits on, kept until it moved on from its wait: a task's record is read again at each of its state changes, a waiter
/// reads the outcomes of what it waited on once it resumes (<c>runtime.OutcomesAsync</c>), and both are bounded by live work, which the
/// scheduler keeps in memory too. Every commit goes through it, and one process owns a Storage, so it applies each batch as it is published
/// and never goes stale: <see cref="CommitAsync"/>'s batches when the Session publishes them (<see cref="Published"/>), not at their
/// durability receipts. A read result is kept only when no commit published since the read started wrote its record, as the read may not
/// observe that commit; versions are kept only for records with a read in flight. Entries, scans and documents pass through: the Session
/// keeps documents and the newest entry per conversation itself, and context ranges hold the entries a run reads.
/// </summary>
/// <remarks>Not for a read-only Session: another process writes its Storage.</remarks>
internal sealed class CachedStorage(IStorage storage) : IStorage
{
    /// <summary>Conversations kept: records are small, so many.</summary>
    internal const int ConversationCapacity = 4096;

    /// <summary>Ended tasks kept besides the awaited ones: ended task records carry inputs and outcomes, such as a tool call's arguments, so
    /// fewer are kept.</summary>
    internal const int TaskCapacity = 256;

    /// <summary>Written submissions kept.</summary>
    internal const int SubmissionCapacity = 256;

    private readonly Lock gate = new();
    private readonly Lru<ConversationId, ConversationRecord> conversations = new(ConversationCapacity);
    // Live (non-terminal) task records, until their task ends.
    private readonly Dictionary<TaskId, TaskRecord> liveTasks = [];
    // Tasks some task waits on, or waited on until its wait ended and it has not moved on since: how many such waits name each, and its ended
    // record once it ended.
    private readonly Dictionary<TaskId, Awaited> awaited = [];
    // Per task that waits, or left its wait and has not changed since: what it waited on, and whether it left.
    private readonly Dictionary<TaskId, Wait> waits = [];
    private readonly Lru<TaskId, TaskRecord> tasks = new(TaskCapacity);
    private readonly Lru<SubmissionId, SubmissionRecord> submissions = new(SubmissionCapacity);
    // Per record with a read in flight (IDs are one Session-global namespace): the reads, and how many published commits wrote it since the
    // first began.
    private readonly Dictionary<long, Reads> reads = [];

    /// <summary>The backend under the cache.</summary>
    internal IStorage Inner => storage;

    /// <summary>Internal, for tests: a fault to throw from the next application of a published batch (upstream's spy on
    /// <c>CachedStorage.prototype.published</c>); returns the fault to throw, or null.</summary>
    internal Func<IReadOnlyList<StorageWrite>, Exception?>? PublishedFault { get; set; }

    /// <summary>Passes the batches through; the Session applies each one with <see cref="Published"/> when it publishes it.</summary>
    public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        storage.CommitAsync(batches, receive, context);

    /// <summary>The backend's own scope for a publication (upstream <c>publishing</c>, through <c>scopeOf</c>).</summary>
    public void Publishing(Action publish, DurableContext context) => storage.Publishing(publish, context);

    /// <summary>
    /// Applies a batch <see cref="CommitAsync"/> stored, when the Session publishes it (upstream <c>published</c>). Its records are kept as
    /// they are, no copies: the Session's components that follow the publication, such as the scheduler's live tasks, hold the same objects,
    /// so a live task's record is in memory once.
    /// </summary>
    public void Published(IReadOnlyList<StorageWrite> writes)
    {
        if (PublishedFault?.Invoke(writes) is { } fault) throw fault;
        lock (gate)
        {
            foreach (var write in writes)
            {
                switch (write)
                {
                    case ConversationWrite conversation:
                        WroteLocked(conversation.Value.Id.Value);
                        conversations.Set(conversation.Value.Id, conversation.Value);
                        break;
                    case TaskWrite task:
                        WroteLocked(task.Value.Id.Value);
                        FollowWaitLocked(task.Value);
                        KeepTaskLocked(task.Value);
                        break;
                    case SubmissionWrite submission:
                        WroteLocked(submission.Value.Id.Value);
                        submissions.Set(submission.Value.Id, submission.Value);
                        break;
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => storage.MintIdAsync<TId>();

    /// <inheritdoc/>
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context)
    {
        lock (gate)
            if (conversations.Get(id) is { } cached) return Task.FromResult<ConversationRecord?>(cached);
        return KeepAsync(id.Value, () => storage.GetConversationAsync(id, context), record => conversations.Set(id, record));
    }

    /// <inheritdoc/>
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanConversationsAsync(query, limit, cursor, context);

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => storage.GetEntryAsync(id, context);

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        storage.GetEntryAsync(conversationId, id, context);

    /// <inheritdoc/>
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        storage.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);

    /// <inheritdoc/>
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanEntriesAsync(query, limit, cursor, context);

    /// <inheritdoc/>
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
    {
        lock (gate)
        {
            var cached = liveTasks.GetValueOrDefault(id) ?? awaited.GetValueOrDefault(id)?.Record ?? tasks.Get(id);
            if (cached is not null) return Task.FromResult<TaskRecord?>(cached);
        }
        return KeepAsync(id.Value, () => storage.GetTaskAsync(id, context), KeepTaskLocked);
    }

    /// <inheritdoc/>
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanTasksAsync(query, limit, cursor, context);

    /// <summary>Cached as written only: this process writes every submission it settles, and a read one is usually settled.</summary>
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context)
    {
        lock (gate)
            if (submissions.Get(id) is { } cached) return Task.FromResult<SubmissionRecord?>(cached);
        return storage.GetSubmissionAsync(id, context);
    }

    /// <inheritdoc/>
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanSubmissionsAsync(query, limit, cursor, context);

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        storage.GetSubmissionByRequestAsync(conversationId, requestId, context);

    /// <inheritdoc/>
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        storage.FindDocumentAsync(address, at, context);

    /// <inheritdoc/>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => storage.GetDocumentAsync(id, at, context);

    /// <inheritdoc/>
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanDocumentsAsync(query, limit, cursor, context);

    /// <inheritdoc/>
    public Task CloseAsync(DurableContext context) => storage.CloseAsync(context);

    /// <summary>Records kept per table, for tests that bound memory (upstream <c>sizes</c>).</summary>
    internal (int Conversations, int Tasks, int LiveTasks, int Awaited, int Waits, int Submissions) Sizes
    {
        get
        {
            lock (gate) return (conversations.Count, tasks.Count, liveTasks.Count, awaited.Count, waits.Count, submissions.Count);
        }
    }

    /// <summary>Records with a read in flight, for tests that bound memory (upstream <c>readsInFlight</c>).</summary>
    internal int ReadsInFlight
    {
        get { lock (gate) return reads.Count; }
    }

    /// <summary>Keeps a task record: a live one until its task ends, an ended one in the bounded table (upstream <c>#keepTask</c>).</summary>
    private void KeepTaskLocked(TaskRecord record)
    {
        if (record.State.Status == DurableTaskStatus.Terminal)
        {
            liveTasks.Remove(record.Id);
            if (awaited.TryGetValue(record.Id, out var waitedOn)) waitedOn.Record = record;
            else tasks.Set(record.Id, record);
            return;
        }
        tasks.Remove(record.Id);
        liveTasks[record.Id] = record;
    }

    /// <summary>
    /// Follows a task's waits from its written record (upstream <c>#followWait</c>): a wait holds the ended records of its <c>on</c> until the
    /// task, after leaving the wait, changes again (or ends), as the phase that resumes reads their outcomes before its next commit.
    /// </summary>
    private void FollowWaitLocked(TaskRecord record)
    {
        waits.TryGetValue(record.Id, out var wait);
        if (record.State is WaitingTaskState waiting)
        {
            if (wait is not null) ReleaseLocked(wait.On);
            var on = waiting.On;
            waits[record.Id] = new Wait(on);
            foreach (var id in on)
            {
                if (!awaited.TryGetValue(id, out var waitedOn))
                {
                    // One that ended already moves over from the bounded table.
                    var ended = tasks.Get(id);
                    tasks.Remove(id);
                    awaited[id] = waitedOn = new Awaited { Record = ended };
                }
                waitedOn.Count++;
            }
            return;
        }
        if (wait is null) return;
        if (!wait.Left && record.State.Status != DurableTaskStatus.Terminal)
        {
            wait.Left = true;
            return;
        }
        waits.Remove(record.Id);
        ReleaseLocked(wait.On);
    }

    /// <summary>A wait on <paramref name="on"/> is over: what no other wait names goes back to the bounded table (upstream
    /// <c>#release</c>).</summary>
    private void ReleaseLocked(IEnumerable<TaskId> on)
    {
        foreach (var id in on)
        {
            var waitedOn = awaited[id];
            if (--waitedOn.Count > 0) continue;
            awaited.Remove(id);
            if (waitedOn.Record is { } record) tasks.Set(id, record);
        }
    }

    /// <summary>A published commit wrote record <paramref name="id"/>: a read of it in flight may have missed that (upstream
    /// <c>#wrote</c>).</summary>
    private void WroteLocked(long id)
    {
        if (reads.TryGetValue(id, out var inFlight)) inFlight.Version++;
    }

    /// <summary>Completes with a read's result; keeps a found record unless a commit published since the read started wrote it (upstream
    /// <c>#keep</c>). <paramref name="keep"/> runs under the lock.</summary>
    private async Task<T?> KeepAsync<T>(long id, Func<Task<T?>> read, Action<T> keep) where T : class
    {
        Reads inFlight;
        int version;
        lock (gate)
        {
            if (!reads.TryGetValue(id, out inFlight!)) reads[id] = inFlight = new Reads();
            inFlight.Count++;
            version = inFlight.Version;
        }
        T? value;
        try
        {
            value = await read().ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
                if (--inFlight.Count == 0) reads.Remove(id);
            throw;
        }
        // One section, as upstream's runs without a turn in between: a publication between the end of the read and its keep would find no
        // read in flight to bump, and the stale value would replace the one it wrote.
        lock (gate)
        {
            if (--inFlight.Count == 0) reads.Remove(id);
            if (value is not null && inFlight.Version == version) keep(value);
        }
        return value;
    }

    private sealed class Awaited
    {
        public int Count;
        public TaskRecord? Record;
    }

    private sealed class Wait(IEnumerable<TaskId> on)
    {
        public IReadOnlyList<TaskId> On { get; } = [.. on];
        public bool Left;
    }

    private sealed class Reads
    {
        public int Count;
        public int Version;
    }
}

/// <summary>A dictionary bounded to <c>capacity</c> entries that drops the least recently used one (upstream <c>Lru</c>). Not thread-safe:
/// its owner guards it.</summary>
internal sealed class Lru<TKey, TValue>(int capacity) where TKey : notnull where TValue : class
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map = [];
    // Least recently used first.
    private readonly LinkedList<(TKey Key, TValue Value)> order = new();

    public int Count => map.Count;

    /// <summary>The value for <paramref name="key"/>, made the most recently used; null when absent.</summary>
    public TValue? Get(TKey key)
    {
        if (!map.TryGetValue(key, out var node)) return null;
        order.Remove(node);
        order.AddLast(node);
        return node.Value.Value;
    }

    /// <summary>The value for <paramref name="key"/>, its place unchanged; null when absent.</summary>
    public TValue? Peek(TKey key) => map.TryGetValue(key, out var node) ? node.Value.Value : null;

    public void Remove(TKey key)
    {
        if (map.Remove(key, out var node)) order.Remove(node);
    }

    /// <summary>Sets <paramref name="key"/> as the most recently used, dropping the least recently used one beyond the capacity.</summary>
    public void Set(TKey key, TValue value)
    {
        Remove(key);
        map[key] = order.AddLast((key, value));
        if (map.Count <= capacity) return;
        var oldest = order.First!;
        order.RemoveFirst();
        map.Remove(oldest.Value.Key);
    }
}
