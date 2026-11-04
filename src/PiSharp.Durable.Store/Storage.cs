// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/store/storage.ts
//
// Durable's Storage on Chord's store (slice 6b D1, D2, §2): opening under an owner name (claims, tables, the allocator), commits as one
// composite write of the store's commit step, and the reads, each one guarded read of the store (several queries at one state in a read
// transaction), its request errors in a tagged result told after it. Upstream's Read<T> union is ReadResult<T>; its StorageRequestError is
// StorageRequestException and its Error an InvalidOperationException. Upstream runs on one JavaScript thread; here a call may arrive on any
// thread, so the closed flag and the count of calls underway are kept under a short lock. A Durable context becomes the store's context
// through StoreContexts (Context.cs). The argument checks of a commit (null batches, writes, receive) are the other C# storages' (an
// argument error, not a refusal).
using PiSharp.Chord;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.Durable.Store;

/// <summary>
/// Durable's <see cref="IStorage"/> on Chord v2's store (upstream <c>StoreStorage</c>, slice 6b D1, D2, §2): its records as declared tables
/// under an owner name, its documents in the store's documents engine, its ids from the store's one allocator, each commit call as one
/// series of the store's commit queue (one backend transaction, one seq per batch), published in the host's batch. Several may share a
/// store under distinct owner names (claims); the app owns the store, and <see cref="CloseAsync"/> leaves it open.
/// </summary>
public sealed class StoreStorage : IStorage
{
    private readonly ChordStore store;
    private readonly string owner;
    private readonly IClaimant claimant;
    private readonly StoreCommitTables tables;
    private readonly StoreQueries queries;
    private readonly Lock gate = new();
    private bool closed;
    private Task? closing;
    /// <summary>Calls underway, which close waits for before it releases the claims (<see cref="drained"/> once they reach 0).</summary>
    private int underway;
    private TaskCompletionSource? drained;

    private StoreStorage(ChordStore store, string owner, IClaimant claimant, StoreCommitTables tables)
    {
        this.store = store;
        this.owner = owner;
        this.claimant = claimant;
        this.tables = tables;
        queries = new StoreQueries(tables);
    }

    /// <summary>
    /// Opens Durable's storage in <paramref name="store"/> under <paramref name="owner"/> (a lowercase <c>[a-z0-9@/._~-]</c> name): claims
    /// its tables and its document namespaces (a second claim is refused <c>invalid/args</c>), creates or migrates its tables, and raises the
    /// store's allocator above every id it holds (one index read). A failure releases what it claimed (upstream <c>open</c>).
    /// </summary>
    public static async Task<StoreStorage> OpenAsync(IStore store, string owner, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Chord's own: Tables.OpenStoreAsync or an import made it.
        var chord = store as ChordStore ?? throw new ArgumentException("A store opened by Chord v2 (Tables.OpenStoreAsync) is required", nameof(store));
        var claimant = new Claimant($"StoreStorage({owner})");
        var at = StoreContexts.ToChord(context);
        try
        {
            var declared = new StoreOwnerTables(chord, owner, claimant);
            var tables = new StoreCommitTables(declared.Declare(StoreTables.RecordIds), declared.Declare(StoreTables.Conversations),
                declared.Declare(StoreTables.Entries), declared.Declare(StoreTables.EntryHeads), declared.Declare(StoreTables.Tasks),
                declared.Declare(StoreTables.Submissions));
            foreach (var @namespace in StoreDocuments.ScopeNamespaces) chord.Claims.Documents(owner, @namespace, claimant);
            await declared.PrepareAsync(at).ConfigureAwait(false);
            var highest = await chord.ReadAsync(executor => executor.GetAsync($"SELECT max(\"id\") AS id FROM \"{tables.RecordIds.Table}\""), at)
                .ConfigureAwait(false);
            if (highest?["id"] is { } id) chord.Ids.Raise(StoreValues.Integer(id));
            return new StoreStorage(chord, owner, claimant, tables);
        }
        catch (Exception)
        {
            chord.Claims.Release(claimant);
            throw;
        }
    }

    /// <summary>Commits <paramref name="batches"/> as one series of the store (<see cref="ChordStore.CommitSeriesAsync"/>): one backend
    /// transaction, one seq per batch, each batch's checks (at staging, then against the state the earlier ones left) before its
    /// writes.</summary>
    public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        CallAsync(async () =>
        {
            StorageRules.CheckCommitArguments(batches, receive);
            var at = StoreContexts.ToChord(context);
            var ids = store.Ids;
            var commits = new List<ISeriesCommit>(batches.Count);
            foreach (var writes in batches)
            {
                try
                {
                    commits.Add(new DurableCommit(writes, owner, tables, ids.Raise, at));
                }
                catch (Exception error)
                {
                    // Staging runs Durable's checks of the batch itself: a refusal in its turn, the batches before it stored.
                    commits.Add(new RefusedCommit(error));
                    break;
                }
            }
            var result = await store.CommitSeriesAsync(commits, (index, seq, _) => receive(index, new Seq(seq)), at).ConfigureAwait(false);
            return new CommitResult(result.Stored, result.Refused);
        });

    /// <summary>Runs a commit's publication in the attached host's batch (<see cref="PiSharp.Chord.State.Batch.Within"/>: it joins a running batch, opens one,
    /// or runs next while one sends): Chord sources over Durable's views are marked in one batch per commit. Plainly without a host.</summary>
    public void Publishing(Action publish, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(publish);
        var batch = store.Batch;
        if (batch is null) publish();
        else batch.Within(publish, StoreContexts.ToChord(context));
    }

    /// <inheritdoc/>
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => CallAsync(() => Task.FromResult(TId.FromNumber(store.MintId())));

    /// <inheritdoc/>
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context)
    {
        var at = StoreContexts.ToChord(context);
        return Read(executor => ConversationAsync(executor, id, at), at);
    }

    /// <inheritdoc/>
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Reading(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
            var conditions = new List<Condition> { ScanFrom(start) };
            if (query.OwnerConversationId is { } ownerConversation) conditions.Add(new("owner_conversation_id", Comparison.Eq, ownerConversation.Value));
            if (query.OwnerTaskId is { } ownerTask) conditions.Add(new("owner_task_id", Comparison.Eq, ownerTask.Value));
            return ScanAsync(queries.Conversations, conditions, start, limit, ConversationRecord.FromJson, record => record.Id.Value, StoreContexts.ToChord(context));
        });

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context)
    {
        var at = StoreContexts.ToChord(context);
        return Read(executor => EntryAsync(executor, id, at), at);
    }

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        Reading(() =>
        {
            var at = StoreContexts.ToChord(context);
            return Told(store.ReadAsync(executor => VisibleEntryAsync(executor, conversationId, id, at), at));
        });

    /// <inheritdoc/>
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        Reading(() =>
        {
            var at = StoreContexts.ToChord(context);
            return Told(store.ReadAsync(executor => LatestHeadMarkerAsync(executor, conversationId, atOrBeforeEntryId, at), at));
        });

    /// <inheritdoc/>
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Reading(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var (order, after) = StorageScan.Start(query.Order, cursor, ScanOrder.Descending);
            var at = StoreContexts.ToChord(context);
            return Told(order == ScanOrder.Ascending
                ? store.ReadAsync(executor => EntriesAscendingAsync(executor, query, limit, after, at), at)
                : store.ReadAsync(executor => EntriesDescendingAsync(executor, query, limit, after, at), at));
        });

    /// <inheritdoc/>
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
    {
        var at = StoreContexts.ToChord(context);
        var tasks = queries.Tasks;
        return Read(async executor =>
        {
            var row = await tasks.ById(executor, id.Value).ConfigureAwait(false);
            return row is null ? null : TaskRecord.FromJson(tasks.Record(row, at));
        }, at);
    }

    /// <inheritdoc/>
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Reading(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
            var conditions = new List<Condition> { ScanFrom(start) };
            if (query.ConversationId is { } conversationId) conditions.Add(new("conversation_id", Comparison.Eq, conversationId.Value));
            if (query.Kind is { } kind) conditions.Add(new("kind", Comparison.Eq, StoreTables.EncodeIndexed(kind)));
            if (query.Status is { } status) conditions.Add(new("status", Comparison.Eq, Internal.WireName.Of(status)));
            if (query.AbortRequested is { } abortRequested) conditions.Add(new("abort_requested", Comparison.Eq, abortRequested ? 1L : 0L));
            if (query.Background is { } background) conditions.Add(new("background", Comparison.Eq, background ? 1L : 0L));
            return ScanAsync(queries.Tasks, conditions, start, limit, TaskRecord.FromJson, record => record.Id.Value, StoreContexts.ToChord(context));
        });

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context)
    {
        var at = StoreContexts.ToChord(context);
        var submissions = queries.Submissions;
        return Read(async executor =>
        {
            var row = await submissions.ById(executor, id.Value).ConfigureAwait(false);
            return row is null ? null : SubmissionRecord.FromJson(submissions.Record(row, at));
        }, at);
    }

    /// <inheritdoc/>
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Reading(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
            var conditions = new List<Condition> { ScanFrom(start) };
            if (query.ConversationId is { } conversationId) conditions.Add(new("conversation_id", Comparison.Eq, conversationId.Value));
            if (query.Status is { } status) conditions.Add(new("status", Comparison.Eq, Internal.WireName.Of(status)));
            return ScanAsync(queries.Submissions, conditions, start, limit, SubmissionRecord.FromJson, record => record.Id.Value, StoreContexts.ToChord(context));
        });

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        var at = StoreContexts.ToChord(context);
        var submissions = queries.Submissions;
        Condition[] conditions = [new("conversation_id", Comparison.Eq, conversationId.Value), new("request_id", Comparison.Eq, StoreTables.EncodeIndexed(requestId))];
        return Read(async executor =>
        {
            var row = await submissions.First(executor, conditions, ScanOrder.Ascending).ConfigureAwait(false);
            return row is null ? null : SubmissionRecord.FromJson(submissions.Record(row, at));
        }, at);
    }

    /// <inheritdoc/>
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(address);
        var chord = StoreContexts.ToChord(context);
        return Read(async executor =>
        {
            var entry = await DocumentEngine.FindDocumentEntryAsync(executor, StoreDocuments.AddressOf(address.Kind, address.Scope, address.Key, owner),
                StoreDocuments.PointOf(at)).ConfigureAwait(false);
            return entry is null ? null : StoreDocuments.RecordOfEntry(entry);
        }, chord);
    }

    /// <inheritdoc/>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        // The record and its revisions must come from one committed state (S4).
        Reading(async () => DocumentOf(await store.ReadTransactionAsync(executor => StoreDocuments.ReadAtAsync(executor, id, at, owner),
            StoreContexts.ToChord(context)).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Reading(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var after = StorageRules.CursorId(cursor) ?? -1;
            var scope = new PiSharp.Chord.Store.DocumentScope(owner, StoreDocuments.NamespaceOf(query.Scope),
                query.Kind is null ? null : StoreTables.EncodeIndexed(query.Kind));
            var found = await store.ReadAsync(executor => DocumentEngine.ListDocumentEntriesAsync(executor, scope, StoreDocuments.PointOf(query.At), after,
                (long)limit + 1), StoreContexts.ToChord(context)).ConfigureAwait(false);
            return StorageRules.MakePage([.. found.Select(StoreDocuments.RecordOfEntry)], limit, ScanOrder.Ascending, record => record.Id.Value);
        });

    /// <summary>Refuses new calls, waits for its commits underway, then releases its claims; the store stays open (the app's), and finishes
    /// the reads underway, which change nothing.</summary>
    public Task CloseAsync(DurableContext context)
    {
        lock (gate)
        {
            if (closing is null)
            {
                closed = true;
                var wait = underway > 0 ? (drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task : null;
                closing = ReleaseAsync(wait);
            }
            return closing;
        }
    }

    private async Task ReleaseAsync(Task? wait)
    {
        if (wait is not null) await wait.ConfigureAwait(false);
        store.Claims.Release(claimant);
    }

    /// <summary>Runs one commit or mint of this storage: refused once it closed, counted until it settles (close waits).</summary>
    private async Task<T> CallAsync<T>(Func<Task<T>> run)
    {
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("StoreStorage is closed");
            underway++;
        }
        try
        {
            return await run().ConfigureAwait(false);
        }
        finally
        {
            TaskCompletionSource? done = null;
            lock (gate)
            {
                if (--underway == 0) done = drained;
            }
            done?.TrySetResult();
        }
    }

    /// <summary>A call that is one guarded read of the store: refused once closed; not waited for by close (a read changes nothing, and the
    /// store, which stays open, finishes it).</summary>
    private Task<T> Read<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context) => Reading(() => store.ReadAsync(read, context));

    /// <summary>A read call: <paramref name="read"/> prepares its guarded read (a request error thrown then, before it) and returns it.
    /// Refused once closed.</summary>
    private Task<T> Reading<T>(Func<Task<T>> read)
    {
        try
        {
            lock (gate)
            {
                if (closed) throw new InvalidOperationException("StoreStorage is closed");
            }
            return read();
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    private async Task<ConversationRecord?> ConversationAsync(ISqliteExecutor executor, ConversationId id, ChordContext context)
    {
        var conversations = queries.Conversations;
        var row = await conversations.ById(executor, id.Value).ConfigureAwait(false);
        return row is null ? null : ConversationRecord.FromJson(conversations.Record(row, context));
    }

    private async Task<CommittedEntry?> EntryAsync(ISqliteExecutor executor, EntryId id, ChordContext context)
    {
        var entries = queries.Entries;
        var row = await entries.ById(executor, id.Value).ConfigureAwait(false);
        return row is null ? null : new CommittedEntry(EntryRecord.FromJson(entries.Record(row, context)), new Seq(StoreValues.Integer(row["commit_seq"])));
    }

    /// <summary>Entry <paramref name="id"/> when visible through <paramref name="conversationId"/>'s fork chain (SqliteStorage's
    /// ReadEntry).</summary>
    private async Task<ReadResult<CommittedEntry?>> VisibleEntryAsync(ISqliteExecutor executor, ConversationId conversationId, EntryId id,
        ChordContext context)
    {
        var conversation = await ConversationAsync(executor, conversationId, context).ConfigureAwait(false);
        if (conversation is null) return UnknownConversation<CommittedEntry?>(conversationId);
        var stored = await EntryAsync(executor, id, context).ConfigureAwait(false);
        if (stored is null) return ReadResult<CommittedEntry?>.Nothing;
        var upper = long.MaxValue;
        while (conversation.Id != stored.Entry.ConversationId)
        {
            if (conversation.Parent is not { } parent) return ReadResult<CommittedEntry?>.Nothing;
            upper = Math.Min(upper, parent.At.Value);
            conversation = (await ConversationAsync(executor, parent.ConversationId, context).ConfigureAwait(false))!;
        }
        return stored.Entry.Id.Value > upper ? ReadResult<CommittedEntry?>.Nothing : ReadResult<CommittedEntry?>.Found(stored);
    }

    private async Task<ReadResult<EntryRecord?>> LatestHeadMarkerAsync(ISqliteExecutor executor, ConversationId conversationId, EntryId? atOrBeforeEntryId,
        ChordContext context)
    {
        var conversation = await ConversationAsync(executor, conversationId, context).ConfigureAwait(false);
        if (conversation is null) return UnknownConversation<EntryRecord?>(conversationId);
        var upper = atOrBeforeEntryId?.Value;
        while (true)
        {
            var head = upper is { } bound
                ? await executor.GetAsync(queries.LatestHeadAtOrBefore, conversation.Id.Value, bound).ConfigureAwait(false)
                : await executor.GetAsync(queries.LatestHeadAny, conversation.Id.Value).ConfigureAwait(false);
            if (head is not null)
            {
                // Written with its entry, in the same commit.
                var row = (await queries.Entries.ById(executor, StoreValues.Integer(head["id"])).ConfigureAwait(false))!;
                return ReadResult<EntryRecord?>.Found(EntryRecord.FromJson(queries.Entries.Record(row, context)));
            }
            if (conversation.Parent is not { } parent) return ReadResult<EntryRecord?>.Nothing;
            upper = upper is { } current ? Math.Min(current, parent.At.Value) : parent.At.Value;
            conversation = (await ConversationAsync(executor, parent.ConversationId, context).ConfigureAwait(false))!;
        }
    }

    /// <summary>Newest first: each conversation of the fork chain up to its fork point (SqliteStorage's ReadEntries).</summary>
    private async Task<ReadResult<Page<EntryRecord>>> EntriesDescendingAsync(ISqliteExecutor executor, EntryQuery query, int limit, long? after,
        ChordContext context)
    {
        var conversation = await ConversationAsync(executor, query.ConversationId, context).ConfigureAwait(false);
        if (conversation is null) return UnknownConversation<Page<EntryRecord>>(query.ConversationId);
        var minimum = query.MinEntryId?.Value;
        var upper = query.MaxEntryId?.Value;
        if (after is { } last) upper = Math.Min(upper ?? Ids.MaxSafeInteger, last - 1);
        var values = new List<EntryRecord>();
        while (true)
        {
            var range = Range(conversation.Id, minimum, upper);
            var rows = await queries.Entries.Select(executor, range, ScanOrder.Descending, (long)limit + 1 - values.Count).ConfigureAwait(false);
            queries.Entries.Records(rows, values, EntryRecord.FromJson, context);
            if (values.Count > limit || conversation.Parent is not { } parent) break;
            upper = upper is { } current ? Math.Min(current, parent.At.Value) : parent.At.Value;
            if (minimum is { } lower && upper < lower) break;
            conversation = (await ConversationAsync(executor, parent.ConversationId, context).ConfigureAwait(false))!;
        }
        return ReadResult<Page<EntryRecord>>.Found(StorageRules.MakePage(values, limit, ScanOrder.Descending, entry => entry.Id.Value));
    }

    /// <summary>Oldest first: the fork chain's segments from the root forward, each up to its fork point.</summary>
    private async Task<ReadResult<Page<EntryRecord>>> EntriesAscendingAsync(ISqliteExecutor executor, EntryQuery query, int limit, long? after,
        ChordContext context)
    {
        var segments = new List<(ConversationId ConversationId, long? Upper)>();
        var conversation = await ConversationAsync(executor, query.ConversationId, context).ConfigureAwait(false);
        if (conversation is null) return UnknownConversation<Page<EntryRecord>>(query.ConversationId);
        var minimum = query.MinEntryId?.Value;
        var upper = query.MaxEntryId?.Value;
        while (true)
        {
            segments.Add((conversation.Id, upper));
            if (conversation.Parent is not { } parent) break;
            upper = upper is { } current ? Math.Min(current, parent.At.Value) : parent.At.Value;
            if (minimum is { } floor && upper < floor) break;
            conversation = (await ConversationAsync(executor, parent.ConversationId, context).ConfigureAwait(false))!;
        }
        var lower = minimum;
        if (after is { } last) lower = Math.Max(lower ?? last + 1, last + 1);
        var values = new List<EntryRecord>();
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var segment = segments[index];
            var range = Range(segment.ConversationId, lower, segment.Upper);
            var rows = await queries.Entries.Select(executor, range, ScanOrder.Ascending, (long)limit + 1 - values.Count).ConfigureAwait(false);
            queries.Entries.Records(rows, values, EntryRecord.FromJson, context);
            if (values.Count > limit) break;
        }
        return ReadResult<Page<EntryRecord>>.Found(StorageRules.MakePage(values, limit, ScanOrder.Ascending, entry => entry.Id.Value));
    }

    /// <summary>One conversation's entries with ids from <paramref name="lower"/> to <paramref name="upper"/> (inclusive, each
    /// optional).</summary>
    private static List<Condition> Range(ConversationId conversationId, long? lower, long? upper)
    {
        var conditions = new List<Condition> { new("conversation_id", Comparison.Eq, conversationId.Value) };
        if (lower is { } from) conditions.Add(new("id", Comparison.Gte, from));
        if (upper is { } to) conditions.Add(new("id", Comparison.Lte, to));
        return conditions;
    }

    /// <summary>A page of a table scan: up to <paramref name="limit"/> records of <paramref name="table"/>'s table whose
    /// <paramref name="conditions"/> hold, from <paramref name="start"/>.</summary>
    private async Task<Page<T>> ScanAsync<T>(TableQueries table, IReadOnlyList<Condition> conditions, ScanStart start, int limit,
        Func<DurableJson, T> decode, Func<T, long> id, ChordContext context)
    {
        var rows = await store.ReadAsync(executor => table.Select(executor, conditions, start.Order, (long)limit + 1), context).ConfigureAwait(false);
        var values = new List<T>(rows.Count);
        table.Records(rows, values, decode, context);
        return StorageRules.MakePage(values, limit, start.Order, id);
    }

    /// <summary>The id condition of a table scan from <paramref name="start"/>.</summary>
    private static Condition ScanFrom(ScanStart start) => start.Order == ScanOrder.Ascending
        ? new("id", Comparison.Gt, start.After ?? -1L)
        : new("id", Comparison.Lt, start.After ?? Ids.MaxSafeInteger);

    private static ReadResult<T> UnknownConversation<T>(ConversationId id) => ReadResult<T>.Refused($"Unknown conversation: {id}");

    /// <summary>A read's data, its request error thrown now that the guarded read returned (slice 6b §1.4).</summary>
    private static async Task<T> Told<T>(Task<ReadResult<T>> read)
    {
        var result = await read.ConfigureAwait(false);
        return result.Message is { } message ? throw new StorageRequestException(message) : result.Value;
    }

    /// <summary>A document read's result, a record that keeps no history at the point thrown as Durable's request error.</summary>
    private static StoredDocument? DocumentOf(DocumentRead read) => read.CurrentOnly is { } id
        ? throw new StorageRequestException($"Document {id} does not retain historical content")
        : read.Document;

    /// <summary>A guarded read's result: its data, or its request error, which is thrown after the guarded read (slice 6b §1.4), never
    /// inside it (upstream <c>Read</c>).</summary>
    private sealed record ReadResult<T>(T Value, string? Message)
    {
        /// <summary>A read that found nothing.</summary>
        public static ReadResult<T> Nothing { get; } = new(default!, null);

        public static ReadResult<T> Found(T value) => new(value, null);

        public static ReadResult<T> Refused(string message) => new(default!, message);
    }

    /// <summary>The claimant of a storage's claims, named in a second claim's refusal (upstream's <c>{ label }</c>).</summary>
    private sealed class Claimant(string label) : IClaimant
    {
        public string Label { get; } = label;
    }
}
