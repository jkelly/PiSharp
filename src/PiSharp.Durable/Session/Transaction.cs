// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/session/transaction.ts (Transaction, LoadedDocument,
// TransactionHost, TransactionScope, applySubmissionChange, planDocument, publishes).
//
// A transaction belongs to one commit callback, which holds the Session line, so it never races another transaction. Its own operations
// may still run concurrently (a callback may start several Tx operations at once, and continuations after storage awaits run on whatever
// thread completed them), so its shared state is guarded by one lock that is never held across an await or a user callback. Document
// values are immutable JsonValue trees: upstream's copyJson of initializer, migration and table values is not needed, because an
// immutable value cannot be changed by its caller after it is handed over and cannot hold anything but strict JSON.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>One committed document incarnation owned by the Session tracker cache (upstream <c>LoadedDocument</c>).</summary>
internal sealed class LoadedDocument(string addressId, DocumentRecord record, int storedVersion, int valueVersion, int deltasSinceBase, Tracker tracker)
{
    public string AddressId { get; } = addressId;
    public DocumentRecord Record { get; } = record;
    /// <summary>Persisted definition version; older while the tracked value is migrated only in memory. Changed on the line only.</summary>
    public int StoredVersion { get; set; } = storedVersion;
    /// <summary>Definition version whose shape the tracked value has; access with another version reloads from Storage.</summary>
    public int ValueVersion { get; } = valueVersion;
    /// <summary>Stored deltas after the newest base; advanced by adoption so the next predicate call needs no read.</summary>
    public int DeltasSinceBase { get; set; } = deltasSinceBase;
    public Tracker Tracker { get; } = tracker;
    /// <summary>The current immutable revision.</summary>
    public JsonObjectValue Value => (JsonObjectValue)Tracker.Value;
}

/// <summary>Session services used by a transaction while it holds the mutation line (upstream <c>TransactionHost</c>).</summary>
internal interface ITransactionHost
{
    IStorage Storage { get; }
    /// <summary>Wall clock for task lifecycle times, in milliseconds.</summary>
    double Now();
    /// <summary>The cached current incarnation, without loading.</summary>
    LoadedDocument? Cached(string addressId);
    /// <summary>The cached current incarnation, cold-loading and migrating it when necessary.</summary>
    Task<LoadedDocument?> LoadAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context);
    /// <summary>Installs a newly committed incarnation.</summary>
    void Install(LoadedDocument document);
    /// <summary>Removes a retired incarnation if it is still the cached occupant of its address.</summary>
    void Evict(string addressId, DocumentId recordId);
    /// <summary>Stages writes that belong to every newly created or forked conversation, in its creating transaction.</summary>
    Task ConversationCreatedAsync(Transaction tx, ConversationRecord record);
}

/// <summary>Defaults a commit binds to: the <c>CreateTaskAsync</c> conversation and the task attributed to appended entries (upstream
/// <c>TransactionScope</c>).</summary>
/// <param name="ConversationId">The default conversation of created tasks.</param>
/// <param name="TaskId">Task whose runtime commit this is; stamped as <c>byTaskId</c> on appended entries.</param>
internal sealed record TransactionScope(ConversationId? ConversationId = null, TaskId? TaskId = null)
{
    public static TransactionScope None { get; } = new();
}

/// <summary>
/// Transaction for one Session commit callback (upstream <c>Transaction</c>). Every asynchronous operation is tracked so callback
/// settlement can reject and drain unfinished work. The Session calls one settlement method, then either discards prepared changes or
/// adopts them once after Storage succeeds.
/// </summary>
internal sealed class Transaction : ITx
{
    private const int InternalScanPageSize = 256;
    private static readonly ImmutableArray<Op> EmptyOperations = [];

    private readonly Lock gate = new();
    private readonly ITransactionHost host;
    private readonly DurableContext context;
    private readonly TransactionScope scope;
    private readonly HashSet<Task> pendingOperations = [];
    private volatile bool sealedState;
    private volatile bool hasTableWrite;

    // Atomic batch; conversation and entry writes stage eagerly, while task and document writes assemble later.
    private readonly List<StorageWrite> writes = [];
    private readonly HashSet<ConversationId> createdConversationIds = [];
    private readonly HashSet<ConversationId> forkSourceConversationIds = [];
    private readonly HashSet<DocumentId> forkSourceDocumentIds = [];
    // One entry per task touched by a public read, candidate write or document-owner lookup.
    private readonly Dictionary<TaskId, TransactionTask> tasksById = [];
    // Submissions created by this transaction, by ID, in creation order.
    private readonly Dictionary<SubmissionId, SubmissionRecord> submissions = [];
    // Submission settlements and placements in staging order; resolved against the latest candidate record during assembly.
    private readonly List<(SubmissionId Id, SubmissionChange Change)> submissionChanges = [];
    // Write and publication plans of every staged incarnation, built during assembly.
    private readonly List<DocumentPlan> plans = [];
    // Every document acquisition or retirement marker in staging order.
    private readonly List<DocumentEntry> documents = [];
    // Latest transaction-local incarnation or retirement marker at each logical address.
    private readonly Dictionary<string, DocumentEntry> latestDocumentByAddress = new(StringComparer.Ordinal);

    public Transaction(ITransactionHost host, DurableContext context, TransactionScope? scope = null)
    {
        this.host = host;
        this.context = context;
        this.scope = scope ?? TransactionScope.None;
    }

    // ─── Table reads ────────────────────────────────────────────────────────

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id) =>
        Read("conversation", () => host.Storage.GetConversationAsync(id, context));

    public Task<EntryRecord?> GetEntryAsync(EntryId id) => ReadEntry(null, id);

    public Task<EntryRecord?> GetEntryAsync(Entry token, EntryId id)
    {
        ArgumentNullException.ThrowIfNull(token);
        return ReadEntry(token.Kind, id);
    }

    public async Task<TypedEntry<TData>?> GetEntryAsync<TData>(Entry<TData> token, EntryId id) where TData : JsonValue
    {
        ArgumentNullException.ThrowIfNull(token);
        return await ReadEntry(token.Kind, id).ConfigureAwait(false) is { } entry ? new TypedEntry<TData>(entry) : null;
    }

    private Task<EntryRecord?> ReadEntry(string? kind, EntryId id) => Read("entry", async () =>
    {
        var entry = (await host.Storage.GetEntryAsync(id, context).ConfigureAwait(false))?.Entry;
        return kind is null || string.Equals(entry?.Kind, kind, StringComparison.Ordinal) ? entry : null;
    });

    public Task<TaskRecord?> GetTaskAsync(TaskId id) => Read("task", () => CommittedTask(id));

    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor = null) =>
        Read("scanConversations", () => host.Storage.ScanConversationsAsync(query, limit, cursor, context));

    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor = null) =>
        Read("scanEntries", () => host.Storage.ScanEntriesAsync(query, limit, cursor, context));

    public Task<EntryRecord?> LatestHeadMarkerAsync(ConversationId conversationId) =>
        Read("latestHeadMarker", () => host.Storage.FindLatestHeadMarkerAsync(conversationId, null, context));

    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor = null) =>
        Read("scanTasks", () => host.Storage.ScanTasksAsync(query, limit, cursor, context));

    /// <summary>Internal: the committed submission record.</summary>
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id) => Read("submission", () => host.Storage.GetSubmissionAsync(id, context));

    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId) =>
        Read("submissionByRequest", () => host.Storage.GetSubmissionByRequestAsync(conversationId, requestId, context));

    // ─── Table writes ───────────────────────────────────────────────────────

    public Task<ConversationRecord> CreateConversationAsync(ConversationOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        return Write(() => StageConversationAsync(null, ownership, null));
    }

    /// <summary>Internal final-form bootstrap path for the reserved root identity.</summary>
    public Task<ConversationRecord> CreateRootConversationAsync() =>
        Write(() => StageConversationAsync(null, ConversationOwnership.Ownerless, ConversationId.Root));

    public Task<ConversationRecord> ForkConversationAsync(ConversationId parentConversationId, EntryId at, ConversationOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        return Write(() => StageConversationAsync(new ConversationParent(parentConversationId, at), ownership, null));
    }

    private async Task<ConversationRecord> StageConversationAsync(ConversationParent? parent, ConversationOwnership ownership, ConversationId? reservedId)
    {
        var id = reservedId ?? await host.Storage.MintIdAsync<ConversationId>().ConfigureAwait(false);
        AssertOpen();
        ConversationOwner? owner = null;
        if (ownership.TaskId is { } ownerTaskId)
        {
            var task = await CurrentTask(ownerTaskId).ConfigureAwait(false);
            AssertOpen();
            if (task is null) throw new InvalidOperationException($"Conversation owner task {ownerTaskId} does not exist");
            owner = new ConversationOwner(task.ConversationId, ownerTaskId);
        }
        var record = new ConversationRecord(id, parent, owner);
        var copies = parent is null ? [] : await Forks.PrepareForkDocumentCopiesAsync(host.Storage, parent.ConversationId, parent.At, id, context)
            .ConfigureAwait(false);
        AssertOpen();
        lock (gate)
        {
            foreach (var copy in copies)
            {
                forkSourceDocumentIds.Add(copy.Source.Id);
                var address = DocumentAddress.Of(copy.Record);
                var entry = new DocumentEntry(Documents.AddressId(address), address, null) { Target = new ForkCopyTarget(copy.Record, copy.Source) };
                documents.Add(entry);
                latestDocumentByAddress[entry.AddressId] = entry;
            }
            if (parent is not null) forkSourceConversationIds.Add(parent.ConversationId);
            createdConversationIds.Add(id);
            writes.Add(new ConversationWrite(record));
        }
        await host.ConversationCreatedAsync(this, record).ConfigureAwait(false);
        AssertOpen();
        return record;
    }

    public Task<EntryRecord> AppendEntryAsync(ConversationId conversationId, EntryDraft value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AppendEntryCore(conversationId, value);
    }

    public Task<EntryRecord> AppendEntryAsync(Entry token, ConversationId conversationId, TypedEntryDraft value)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(value);
        return AppendEntryCore(conversationId, new EntryDraft
        {
            Kind = token.Kind, Model = value.Model, Head = value.Head, Edits = value.Edits, KeyOrder = EntryDraft.TypedOrder,
        });
    }

    public async Task<TypedEntry<TData>> AppendEntryAsync<TData>(Entry<TData> token, ConversationId conversationId, TypedEntryDraft<TData> value)
        where TData : JsonValue
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(value);
        var draft = new EntryDraft
        {
            Kind = token.Kind, Model = value.Model, Data = value.Data, Head = value.Head, Edits = value.Edits, KeyOrder = EntryDraft.TypedOrder,
        };
        return new TypedEntry<TData>(await AppendEntryCore(conversationId, draft).ConfigureAwait(false));
    }

    private Task<EntryRecord> AppendEntryCore(ConversationId conversationId, EntryDraft value) => Write(async () =>
    {
        ArgumentNullException.ThrowIfNull(value.Kind);
        await RequireConversation(conversationId).ConfigureAwait(false);
        AssertOpen();
        var id = await host.Storage.MintIdAsync<EntryId>().ConfigureAwait(false);
        AssertOpen();
        // Upstream's { ...draft without head, id, conversationId, head, byTaskId }: the draft's own key order leads.
        var draftOrder = value.KeyOrder.IsDefault ? EntryDraft.DefaultOrder : value.KeyOrder;
        var record = new EntryRecord
        {
            Id = id, ConversationId = conversationId, Kind = value.Kind, Model = value.Model, Data = value.Data,
            Head = value.Head?.Resolve(id), Edits = value.Edits, ByTaskId = scope.TaskId,
            KeyOrder = [.. draftOrder.Where(key => key != "head"), "id", "conversationId", "head", "byTaskId"],
        };
        lock (gate) writes.Add(new EntryWrite(record));
        return record;
    });

    public Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        return Write(async () =>
        {
            TaskRecord? owner = null;
            if (options.Ownership.TaskId is { } ownerId)
            {
                // Validated again against the owner's final candidate during assembly.
                owner = await CurrentTask(ownerId).ConfigureAwait(false);
                AssertOpen();
                if (owner is null) throw new InvalidOperationException($"Task owner {ownerId} does not exist");
                if (options.Background == true) throw new ArgumentException("A child task cannot be background");
                if (options.ConversationId is { } explicitId && explicitId != owner.ConversationId)
                    throw new InvalidOperationException($"A child task lives in its owner's conversation {owner.ConversationId}");
            }
            var conversationId = owner?.ConversationId ?? options.ConversationId ?? scope.ConversationId ??
                throw new ArgumentException("Tx.createTask() requires options.conversationId");
            await RequireConversation(conversationId).ConfigureAwait(false);
            AssertOpen();
            var definition = task.Definition;
            var checkpoint = definition.Initial(input) ?? throw new InvalidOperationException($"Task {definition.Name} initial() must return JSON");
            var id = await host.Storage.MintIdAsync<TaskId>().ConfigureAwait(false);
            AssertOpen();
            var record = new TaskRecord
            {
                Id = id, ConversationId = conversationId, Kind = definition.Name, Version = definition.Version, Input = input, Owner = owner?.Id,
                Background = options.Background ?? false, AbortRequested = false, AbandonOnRestart = options.AbandonOnRestart == true,
                State = new PendingTaskState(checkpoint),
            };
            lock (gate) tasksById[id] = new TransactionTask { Write = new TaskWriteCandidate(TaskWriteKind.Create, record) };
            return id;
        });
    }

    public Task<SubmissionRecord> CreateSubmissionAsync(SubmissionCreate create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return Write(async () =>
        {
            create.Validate();
            await RequireConversation(create.ConversationId).ConfigureAwait(false);
            AssertOpen();
            var id = await host.Storage.MintIdAsync<SubmissionId>().ConfigureAwait(false);
            AssertOpen();
            var record = create.WithId(id);
            lock (gate) submissions[id] = record;
            return record;
        });
    }

    public void SettleSubmission(SubmissionId id, SubmissionSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        AssertOpen();
        hasTableWrite = true;
        lock (gate) submissionChanges.Add((id, new SubmissionChange(settlement, null)));
    }

    public void PlaceSubmission(SubmissionId id, EntryId entry)
    {
        AssertOpen();
        hasTableWrite = true;
        lock (gate) submissionChanges.Add((id, new SubmissionChange(null, entry)));
    }

    /// <summary>Internal: replaces one task record completely. Tasks change their own state through their runtime.</summary>
    public void SetTask(TaskRecord value)
    {
        AssertOpen();
        ArgumentNullException.ThrowIfNull(value);
        hasTableWrite = true;
        lock (gate)
        {
            var task = TaskEntry(value.Id);
            var candidate = task.Write?.Record;
            if (candidate?.State.Status == DurableTaskStatus.Terminal)
                throw new InvalidOperationException($"Task {value.Id} already has a terminal candidate");
            if (candidate is not null && candidate.ConversationId != value.ConversationId)
                throw new InvalidOperationException($"Task {value.Id} cannot change conversations");
            task.Write = new TaskWriteCandidate(task.Write?.Kind == TaskWriteKind.Create ? TaskWriteKind.Create : TaskWriteKind.Replace,
                StampTimes(value.Validate(), candidate));
        }
    }

    /// <summary>Internal: candidate records of the tasks this transaction created or replaced so far.</summary>
    public List<TaskRecord> StagedTasks()
    {
        lock (gate) return [.. tasksById.Values.Where(task => task.Write is not null).Select(task => task.Write!.Record)];
    }

    /// <summary>Internal: conversations this transaction created or forked so far.</summary>
    public List<ConversationRecord> StagedConversations()
    {
        lock (gate) return [.. writes.OfType<ConversationWrite>().Select(write => write.Value)];
    }

    // ─── Documents ──────────────────────────────────────────────────────────

    public Task<DraftObject> DocAsync(SessionDocToken token) => Doc(token, null, null, null);
    public Task<DraftObject> DocAsync(ConversationDocToken token, ConversationId conversationId) => Doc(token, conversationId.Value, null, null);
    public Task<DraftObject> DocAsync(TaskDocToken token, TaskId taskId) => Doc(token, taskId.Value, null, null);
    public Task<DraftObject> DocAsync(SessionDocFamilyToken token, string key, JsonValue seed) => Doc(token, null, key, seed);
    public Task<DraftObject> DocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, JsonValue seed) =>
        Doc(token, conversationId.Value, key, seed);
    public Task<DraftObject> DocAsync(TaskDocFamilyToken token, TaskId taskId, string key, JsonValue seed) => Doc(token, taskId.Value, key, seed);

    private Task<DraftObject> Doc(DocumentToken token, long? owner, string? key, JsonValue? seed)
    {
        try
        {
            AssertOpen();
            ArgumentNullException.ThrowIfNull(token);
            var definition = token.Definition;
            var resolved = Documents.ResolveAddress(definition, owner, key);
            if (definition.Family) ArgumentNullException.ThrowIfNull(seed);
            DocumentEntry entry;
            Task<DraftObject> draft;
            Func<Task<DraftObject>> acquire;
            var source = new TaskCompletionSource<DraftObject>();
            lock (gate)
            {
                AssertTaskDocumentsOpen(resolved);
                latestDocumentByAddress.TryGetValue(resolved.Id, out var latest);
                if (latest is not null && !latest.RetireOnCommit && latest.DraftTask is not null) return latest.DraftTask;
                if (latest is { RetireOnCommit: false, Target: ForkCopyTarget copy })
                {
                    entry = latest;
                    acquire = () => AcquireForkCopyAsync(latest, definition, copy);
                }
                else
                {
                    entry = new DocumentEntry(resolved.Id, resolved.Address, definition);
                    documents.Add(entry);
                    latestDocumentByAddress[entry.AddressId] = entry;
                    // Capture retirement before awaiting so a pending old acquisition and its replacement stay distinct.
                    var skipLoad = latest?.RetireOnCommit == true;
                    acquire = () => AcquireAsync(entry, definition.Family ? seed : null, skipLoad);
                }
                // Memoized before the acquisition starts, so a concurrent access of the address shares it.
                draft = entry.DraftTask = Track(source.Task);
            }
            Forward(acquire, source);
            return draft;
        }
        catch (Exception error)
        {
            return Task.FromException<DraftObject>(error);
        }
    }

    public Task RetireDocAsync(SessionDocToken token) => RetireDoc(token, null, null);
    public Task RetireDocAsync(ConversationDocToken token, ConversationId conversationId) => RetireDoc(token, conversationId.Value, null);
    public Task RetireDocAsync(TaskDocToken token, TaskId taskId) => RetireDoc(token, taskId.Value, null);
    public Task RetireDocAsync(SessionDocFamilyToken token, string key) => RetireDoc(token, null, key);
    public Task RetireDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key) => RetireDoc(token, conversationId.Value, key);
    public Task RetireDocAsync(TaskDocFamilyToken token, TaskId taskId, string key) => RetireDoc(token, taskId.Value, key);

    private Task RetireDoc(DocumentToken token, long? owner, string? key)
    {
        try
        {
            AssertOpen();
            ArgumentNullException.ThrowIfNull(token);
            var definition = token.Definition;
            var resolved = Documents.ResolveAddress(definition, owner, key);
            DocumentEntry entry;
            lock (gate)
            {
                latestDocumentByAddress.TryGetValue(resolved.Id, out var latest);
                if (latest?.RetireOnCommit == true) return Task.CompletedTask;
                if (latest?.Target is ForkCopyTarget copy)
                {
                    Documents.CheckRecordScope(definition, copy.Record);
                    latest.RetireOnCommit = true;
                    return Task.CompletedTask;
                }
                if (latest?.DraftTask is { } draft)
                {
                    // Retirement of an acquired draft persists its final content before retirement.
                    latest.RetireOnCommit = true;
                    return Track(draft.ContinueWith(static completed => { completed.GetAwaiter().GetResult(); return true; },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
                }
                entry = new DocumentEntry(resolved.Id, resolved.Address, definition) { RetireOnCommit = true };
                documents.Add(entry);
                latestDocumentByAddress[entry.AddressId] = entry;
            }
            return Track(FindRetirementAsync(entry));
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    private async Task<DraftObject> AcquireAsync(DocumentEntry entry, JsonValue? seed, bool skipLoad)
    {
        var definition = entry.Definition!;
        var loaded = skipLoad ? null : await host.LoadAsync(definition, entry.AddressId, entry.Address, context).ConfigureAwait(false);
        AssertOpen();
        if (loaded is not null)
        {
            Documents.CheckRecordScope(definition, loaded.Record);
            Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
            var change = loaded.Tracker.BeginChange();
            lock (gate)
            {
                entry.Target = new LoadedTarget(loaded);
                entry.Change = change;
            }
            return change.State.AsObject();
        }
        var scope = entry.Address.Scope;
        if (scope.ConversationId is { } conversationId) await RequireConversation(conversationId).ConfigureAwait(false);
        if (scope.TaskId is { } taskId)
        {
            var task = await CurrentTask(taskId).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task {taskId} does not exist");
            if (task.State.Status == DurableTaskStatus.Terminal) throw new InvalidOperationException($"Task {taskId} is terminal");
        }
        AssertOpen();
        var value = definition.Initial(seed);
        var id = await host.Storage.MintIdAsync<DocumentId>().ConfigureAwait(false);
        AssertOpen();
        var tracker = Tracker.Track(value);
        var created = tracker.BeginChange();
        lock (gate)
        {
            entry.Target = new CreatedTarget(Documents.CreateRecord(definition, entry.Address, id), definition.Version, tracker);
            entry.Change = created;
        }
        return created.State.AsObject();
    }

    private async Task<DraftObject> AcquireForkCopyAsync(DocumentEntry entry, DocDefinition definition, ForkCopyTarget target)
    {
        var stored = await host.Storage.GetDocumentAsync(target.Source.Id, target.Source.At, context).ConfigureAwait(false);
        AssertOpen();
        if (stored is null) throw new InvalidOperationException($"Fork source document {target.Source.Id} cannot be read");
        if (stored.Record.Scope.Kind != DocumentScopeKind.Conversation || stored.Record.Kind != target.Record.Kind ||
            stored.Record.Key != target.Record.Key || stored.Record.History != target.Record.History || stored.Record.Fork != target.Record.Fork)
            throw new InvalidOperationException($"Fork source document {target.Source.Id} does not match the copied record");
        var value = Documents.MaterializeDocumentValue(definition, target.Record, stored.Version, stored.Value);
        var tracker = Tracker.Track(value);
        var change = tracker.BeginChange();
        lock (gate)
        {
            entry.Definition = definition;
            entry.Target = new CreatedTarget(target.Record, definition.Version, tracker);
            entry.Change = change;
        }
        return change.State.AsObject();
    }

    private async Task FindRetirementAsync(DocumentEntry entry)
    {
        var record = host.Cached(entry.AddressId)?.Record ??
            await host.Storage.FindDocumentAsync(entry.Address, DocumentPoint.Current, context).ConfigureAwait(false);
        AssertOpen();
        if (record is null) return;
        Documents.CheckRecordScope(entry.Definition!, record);
        lock (gate) entry.Target = new RetireOnlyTarget(record);
    }

    // ─── Settlement ─────────────────────────────────────────────────────────

    /// <summary>Seals after callback failure: aborts every change and observes every pending operation.</summary>
    public async Task SettleFailureAsync()
    {
        sealedState = true;
        AbortChanges();
        await DrainPendingAsync().ConfigureAwait(false);
    }

    /// <summary>Seals after callback success, prepares every open change and assembles the atomic batch. Any failure aborts every change
    /// before Storage admission.</summary>
    public async Task<IReadOnlyList<StorageWrite>> SettleSuccessAsync()
    {
        sealedState = true;
        bool pending;
        lock (gate) pending = pendingOperations.Count > 0;
        if (pending)
        {
            AbortChanges();
            await DrainPendingAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Session commit callback settled before its pending Tx operations");
        }
        try
        {
            // Synchronously prepare every open change; this revokes every draft.
            foreach (var document in documents)
                if (document.Change is not null) document.Prepared = document.Change.Prepare();
            return await AssembleAsync().ConfigureAwait(false);
        }
        catch
        {
            AbortChanges();
            throw;
        }
    }

    /// <summary>Aborts every prepared change after Storage failure or when no write is required.</summary>
    public void Discard() => AbortChanges();

    /// <summary>Adopts every prepared change by pointer swap after Storage success and describes the publication.</summary>
    public List<ICommitChange> Adopt(Seq seq)
    {
        var publications = new List<ICommitChange>();
        foreach (var plan in plans)
        {
            var committed = plan.Record is DocumentRecord;
            var record = plan.Record as DocumentRecord ?? ((DocumentCreate)plan.Record).ToRecord(seq);
            if (plan.Retire) record = record with { RetiredAt = seq };
            var change = plan.Change;
            if (change is not null)
            {
                // A new incarnation is adopted unless it retires in the same commit; a loaded one only when it changed.
                if (change.Loaded is null ? !plan.Retire : !change.Prepared.Ops.IsEmpty) change.Tracker.Adopt(change.Prepared);
                else change.Prepared.Abort();
                if (change.Loaded is { } loaded)
                {
                    if (loaded.StoredVersion < change.Version) loaded.StoredVersion = change.Version;
                    if (plan.Content is DocumentChangeWrite write)
                    {
                        if (write.Content is DocumentBase) loaded.DeltasSinceBase = 0;
                        else loaded.DeltasSinceBase++;
                    }
                }
                else if (!plan.Retire)
                {
                    host.Install(new LoadedDocument(plan.AddressId, record, change.Version, change.Version, 0, change.Tracker));
                }
            }
            if (plan.Retire)
            {
                if (committed) host.Evict(plan.AddressId, record.Id);
                publications.Add(new DocumentCommitChange(record, plan.ConversationId, null, null, EmptyOperations));
            }
            else if (plan.Content is DocumentCopyWrite copy)
            {
                publications.Add(new DocumentCopyCommitChange(record, plan.ConversationId!.Value, copy.Source));
            }
            else if (change is not null && Publishes(plan))
            {
                var ops = change.Loaded is null ? EmptyOperations : change.Prepared.Ops;
                publications.Add(new DocumentCommitChange(record, plan.ConversationId, change.Version, (JsonObjectValue)change.Prepared.Value, ops));
            }
        }
        return publications;
    }

    private async Task<IReadOnlyList<StorageWrite>> AssembleAsync()
    {
        var storage = host.Storage;
        foreach (var document in documents)
            if (PlanDocument(document) is { } plan) plans.Add(plan);
        RejectForkSourceWrites();
        await ValidateOwnersAsync().ConfigureAwait(false);
        foreach (var (id, task) in tasksById.ToList())
        {
            if (task.Write?.Kind != TaskWriteKind.Replace) continue;
            var committedTask = await CommittedTask(id).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task {id} does not exist");
            if (committedTask.State.Status == DurableTaskStatus.Terminal) throw new InvalidOperationException($"Task {id} is already terminal");
            if (committedTask.ConversationId != task.Write.Record.ConversationId) throw new InvalidOperationException($"Task {id} cannot change conversations");
        }

        // Terminal settlement retires every task document, including ones created by this transaction.
        var terminalTaskIds = new List<TaskId>();
        foreach (var task in tasksById.Values)
            if (task.Write?.Record.State.Status == DurableTaskStatus.Terminal) terminalTaskIds.Add(task.Write.Record.Id);
        if (terminalTaskIds.Count > 0)
        {
            var retiring = new HashSet<DocumentId>();
            foreach (var plan in plans)
            {
                if (plan.Record.Scope.TaskId is not { } taskId || !terminalTaskIds.Contains(taskId)) continue;
                plan.Retire = true;
                retiring.Add(plan.Record.Id);
            }
            foreach (var taskId in terminalTaskIds)
            {
                if (tasksById[taskId].Write?.Kind == TaskWriteKind.Create) continue;
                JsonObjectValue? cursor = null;
                do
                {
                    var page = await storage.ScanDocumentsAsync(new DocumentQuery(DocumentScope.Task(taskId), DocumentPoint.Current),
                        InternalScanPageSize, cursor, context).ConfigureAwait(false);
                    foreach (var record in page.Items)
                    {
                        if (!retiring.Add(record.Id)) continue;
                        plans.Add(new DocumentPlan(Documents.AddressId(DocumentAddress.Of(record)), record) { Retire = true });
                    }
                    cursor = page.Next;
                } while (cursor is not null);
            }
        }

        // Resolve publication ownership before Storage admission so adoption remains synchronous.
        foreach (var plan in plans)
        {
            if (!Publishes(plan)) continue;
            var documentScope = plan.Record.Scope;
            if (documentScope.ConversationId is { } conversationId) plan.ConversationId = conversationId;
            if (documentScope.TaskId is not { } taskId) continue;
            TransactionTask task;
            lock (gate) task = TaskEntry(taskId);
            if (task.PublicationConversationId is null && await CurrentTask(taskId).ConfigureAwait(false) is { } current)
                task.PublicationConversationId = current.ConversationId;
            plan.ConversationId = task.PublicationConversationId;
        }

        foreach (var (id, change) in submissionChanges)
        {
            var current = submissions.GetValueOrDefault(id) ?? await storage.GetSubmissionAsync(id, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Submission {id} does not exist");
            var next = ApplySubmissionChange(current, change);
            if (!ReferenceEquals(next, current)) submissions[id] = next;
        }

        foreach (var value in submissions.Values) writes.Add(new SubmissionWrite(value));
        foreach (var task in tasksById.Values)
            if (task.Write is not null) writes.Add(new TaskWrite(task.Write.Record));
        foreach (var plan in plans)
        {
            var change = plan.Change;
            // Checkpoint predicates run last, after every validation.
            if (plan.Content is DocumentChangeWrite { Content: DocumentDelta } write && change?.Loaded is not null)
            {
                var info = new CheckpointInfo(change.Loaded.DeltasSinceBase);
                var value = (JsonObjectValue)change.Prepared.Value;
                if (change.Definition?.CheckpointWhen?.Invoke(value, change.Prepared.Ops, info) == true)
                    plan.Content = write with { Content = DocumentContent.Base(change.Version, value) };
            }
            if (plan.Content is not null) writes.Add(plan.Content);
            if (plan.Retire) writes.Add(new DocumentRetireWrite(plan.Record.Id));
        }
        return writes;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// New owned work needs a live owner, judged on the owner's final candidate: not completing, terminal or abort-marked. A task therefore
    /// cannot create owned work in the commit that finishes it (spec §5.5).
    /// </summary>
    private async Task ValidateOwnersAsync()
    {
        var owners = new List<(string What, TaskId TaskId)>();
        foreach (var write in writes)
            if (write is ConversationWrite { Value.Owner: { } owner }) owners.Add(("Conversation owner task", owner.TaskId));
        foreach (var task in tasksById.Values)
            if (task.Write is { Kind: TaskWriteKind.Create, Record.Owner: { } owner }) owners.Add(("Task owner", owner));
        foreach (var (what, taskId) in owners)
        {
            var task = await CurrentTask(taskId).ConfigureAwait(false) ?? throw new InvalidOperationException($"{what} {taskId} does not exist");
            if (task.State.Status is DurableTaskStatus.Terminal or DurableTaskStatus.Completing)
                throw new InvalidOperationException($"{what} {taskId} is {WireName.Of(task.State.Status)}");
            if (task.AbortRequested) throw new InvalidOperationException($"{what} {taskId} is abort-marked");
        }
    }

    private void RejectForkSourceWrites()
    {
        foreach (var plan in plans)
        {
            if (plan.Content is null && !plan.Retire) continue;
            var record = plan.Record;
            if (forkSourceDocumentIds.Contains(record.Id))
                throw new InvalidOperationException($"Cannot change fork source document {record.Id} in the fork transaction");
            if (record.Scope.ConversationId is { } conversationId && forkSourceConversationIds.Contains(conversationId) && record.Fork == DocumentFork.Current)
                throw new InvalidOperationException($"Cannot fork conversation {conversationId} while changing its current-policy documents");
        }
    }

    private void AbortChanges()
    {
        DocumentEntry[] entries;
        lock (gate) entries = [.. documents];
        foreach (var document in entries) document.Change?.Abort();
    }

    private async Task DrainPendingAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (gate) pending = [.. pendingOperations];
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception) { /* every pending failure is observed here */ }
        }
    }

    private void AssertOpen()
    {
        if (sealedState) throw new InvalidOperationException("Transaction has settled");
    }

    private void AssertTaskDocumentsOpen(ResolvedAddress resolved)
    {
        if (resolved.Address.Scope.TaskId is { } taskId && tasksById.TryGetValue(taskId, out var task) &&
            task.Write?.Record.State.Status == DurableTaskStatus.Terminal)
            throw new InvalidOperationException($"Task {taskId} is terminal");
    }

    private static void Forward<T>(Func<Task<T>> start, TaskCompletionSource<T> target)
    {
        Task<T> task;
        try { task = start(); }
        catch (Exception error)
        {
            target.TrySetException(error);
            return;
        }
        task.ContinueWith(static (completed, state) =>
        {
            var target = (TaskCompletionSource<T>)state!;
            if (completed.IsCompletedSuccessfully) target.TrySetResult(completed.Result);
            else if (completed.IsFaulted) target.TrySetException(completed.Exception!.InnerExceptions);
            else target.TrySetCanceled();
        }, target, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Registers an operation so callback settlement can reject and drain it.</summary>
    private Task<T> Track<T>(Task<T> operation)
    {
        Track((Task)operation);
        return operation;
    }

    private Task Track(Task operation)
    {
        lock (gate) pendingOperations.Add(operation);
        operation.ContinueWith(static (completed, state) =>
        {
            var self = (Transaction)state!;
            _ = completed.Exception;
            lock (self.gate) self.pendingOperations.Remove(completed);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return operation;
    }

    private Task<T> Read<T>(string method, Func<Task<T>> read)
    {
        try
        {
            AssertOpen();
            if (hasTableWrite) throw new ReadAfterWriteException(method);
            return Track(read());
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    private Task<T> Write<T>(Func<Task<T>> write)
    {
        try
        {
            AssertOpen();
            hasTableWrite = true;
            return Track(write());
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    private async Task RequireConversation(ConversationId id)
    {
        lock (gate)
            if (createdConversationIds.Contains(id)) return;
        if (await host.Storage.GetConversationAsync(id, context).ConfigureAwait(false) is null)
            throw new InvalidOperationException($"Conversation {id} does not exist");
    }

    /// <summary>
    /// Lifecycle times: <c>startedAt</c> on the first change to running, <c>endedAt</c> on the change to terminal. Once set, they carry over
    /// from the replaced record; records written before they existed lack them.
    /// </summary>
    private TaskRecord StampTimes(TaskRecord value, TaskRecord? candidate)
    {
        var status = value.State.Status;
        var startedAt = candidate?.StartedAt ?? value.StartedAt ?? (status == DurableTaskStatus.Running ? host.Now() : null);
        var endedAt = candidate?.EndedAt ?? value.EndedAt ?? (status == DurableTaskStatus.Terminal ? host.Now() : null);
        if (startedAt == value.StartedAt && endedAt == value.EndedAt) return value;
        return value with { StartedAt = startedAt, EndedAt = endedAt };
    }

    // Call under the gate.
    private TransactionTask TaskEntry(TaskId id)
    {
        if (!tasksById.TryGetValue(id, out var task))
        {
            task = new TransactionTask();
            tasksById[id] = task;
        }
        return task;
    }

    /// <summary>Latest candidate task record, falling back to committed state; not a caller table read.</summary>
    private async Task<TaskRecord?> CurrentTask(TaskId id)
    {
        TaskRecord? candidate;
        lock (gate) candidate = tasksById.GetValueOrDefault(id)?.Write?.Record;
        return candidate ?? await CommittedTask(id).ConfigureAwait(false);
    }

    private Task<TaskRecord?> CommittedTask(TaskId id)
    {
        lock (gate)
        {
            var task = TaskEntry(id);
            return task.CommittedRead ??= host.Storage.GetTaskAsync(id, context);
        }
    }

    /// <summary>Complete record after applying one change. Placement turns a queued input placed and a queued write done; only a placed
    /// input can be answered. A settled record stays.</summary>
    private static SubmissionRecord ApplySubmissionChange(SubmissionRecord current, SubmissionChange change)
    {
        if (current.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered) return current;
        if (change.PlacedAt is { } entry)
        {
            if (current.Status != SubmissionStatus.Queued) throw new InvalidOperationException($"Submission {current.Id} is not queued");
            return current with { Status = current.Type == SubmissionType.Input ? SubmissionStatus.Placed : SubmissionStatus.Done, Entry = entry };
        }
        var settlement = change.Settlement!;
        if (settlement.Status == SubmissionStatus.Done && current.Status != SubmissionStatus.Placed)
            throw new InvalidOperationException($"Submission {current.Id} is not a placed input");
        // Queued and placed records carry no answer, reason or detail; an unanswered input keeps its entry.
        return (current with { Status = settlement.Status, Answer = settlement.Answer, Reason = settlement.Reason, Detail = settlement.Detail }).Validate();
    }

    /// <summary>Plan of one staged document: its record, content write and prepared change. Retirement is decided later.</summary>
    private static DocumentPlan? PlanDocument(DocumentEntry document)
    {
        switch (document.Target)
        {
            case CreatedTarget created:
            {
                var prepared = document.Prepared!;
                var content = DocumentContent.Base(created.Version, (JsonObjectValue)prepared.Value);
                return new DocumentPlan(document.AddressId, created.Record)
                {
                    Retire = document.RetireOnCommit,
                    Content = new DocumentCreateWrite(created.Record, content),
                    Change = new PlanChange(created.Tracker, prepared, created.Version, null, null),
                };
            }
            case ForkCopyTarget copy:
                return new DocumentPlan(document.AddressId, copy.Record)
                {
                    Retire = document.RetireOnCommit,
                    Content = new DocumentCopyWrite(copy.Record, copy.Source),
                };
            case RetireOnlyTarget retire:
                return new DocumentPlan(document.AddressId, retire.Record) { Retire = document.RetireOnCommit };
            case LoadedTarget { Document: var loaded }:
            {
                var definition = document.Definition!;
                var prepared = document.Prepared!;
                var version = definition.Version;
                var id = loaded.Record.Id;
                // A version change stores a base even without operations; otherwise only a change stores a delta, which shares the
                // prepared batch that is also published.
                StorageWrite? content = loaded.StoredVersion < version
                    ? new DocumentChangeWrite(id, DocumentContent.Base(version, (JsonObjectValue)prepared.Value))
                    : !prepared.Ops.IsEmpty ? new DocumentChangeWrite(id, new DocumentDelta(version, prepared.Ops)) : null;
                return new DocumentPlan(document.AddressId, loaded.Record)
                {
                    Retire = document.RetireOnCommit,
                    Content = content,
                    Change = new PlanChange(loaded.Tracker, prepared, version, loaded, definition),
                };
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether adoption publishes the plan: every creation, copy and retirement, and a loaded incarnation that writes content, which includes
    /// a migration-only base so observers of the older shape receive the new value.
    /// </summary>
    private static bool Publishes(DocumentPlan plan) => plan.Retire || plan.Change?.Loaded is null || plan.Content is not null;

    /// <summary>A staged submission change: a settlement, or the placement of a queued submission at its entry.</summary>
    private sealed record SubmissionChange(SubmissionSettlement? Settlement, EntryId? PlacedAt);

    private enum TaskWriteKind { Create, Replace }

    private sealed record TaskWriteCandidate(TaskWriteKind Kind, TaskRecord Record);

    /// <summary>Committed and candidate state for one task touched by this transaction.</summary>
    private sealed class TransactionTask
    {
        public Task<TaskRecord?>? CommittedRead { get; set; }
        public TaskWriteCandidate? Write { get; set; }
        public ConversationId? PublicationConversationId { get; set; }
    }

    /// <summary>Storage and cache provenance of one staged document incarnation.</summary>
    private abstract record DocumentTarget;
    private sealed record LoadedTarget(LoadedDocument Document) : DocumentTarget;
    private sealed record CreatedTarget(DocumentCreate Record, int Version, Tracker Tracker) : DocumentTarget;
    private sealed record ForkCopyTarget(DocumentCreate Record, DocumentCopySource Source) : DocumentTarget;
    private sealed record RetireOnlyTarget(DocumentRecord Record) : DocumentTarget;

    /// <summary>One document incarnation acquired, created or retired by this transaction.</summary>
    private sealed class DocumentEntry(string addressId, DocumentAddress address, DocDefinition? definition)
    {
        public string AddressId { get; } = addressId;
        public DocumentAddress Address { get; } = address;
        /// <summary>Absent for definition-free fork copies and retirement entries discovered by a terminal-task scan.</summary>
        public DocDefinition? Definition { get; set; } = definition;
        /// <summary>Memoized public acquisition; absent for metadata-only retirement.</summary>
        public Task<DraftObject>? DraftTask { get; set; }
        /// <summary>Set after acquisition or retirement lookup finds the affected incarnation.</summary>
        public DocumentTarget? Target { get; set; }
        public Change? Change { get; set; }
        public Prepared? Prepared { get; set; }
        public bool RetireOnCommit { get; set; }
    }

    /// <summary>The prepared change of a tracked incarnation.</summary>
    private sealed record PlanChange(Tracker Tracker, Prepared Prepared, int Version, LoadedDocument? Loaded, DocDefinition? Definition);

    /// <summary>
    /// What one staged incarnation writes and publishes, decided once before Storage admission so adoption only applies it. The record is a
    /// <see cref="DocumentRecord"/> for an incarnation that is already committed and a <see cref="DocumentCreate"/> for a new one.
    /// </summary>
    private sealed class DocumentPlan(string addressId, IDocumentDescriptor record)
    {
        public string AddressId { get; } = addressId;
        public IDocumentDescriptor Record { get; } = record;
        public bool Retire { get; set; }
        /// <summary>Creation, copy or change content; absent when only retirement is written.</summary>
        public StorageWrite? Content { get; set; }
        /// <summary>Prepared change of a tracked incarnation; absent for fork copies and retirement-only entries.</summary>
        public PlanChange? Change { get; init; }
        /// <summary>Resolved before Storage admission so adoption performs no reads.</summary>
        public ConversationId? ConversationId { get; set; }
    }
}
