// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/session/session.ts (createSession, SessionImpl,
// observedOperations, cancellationError).
//
// Concurrency model. Upstream relies on one JavaScript thread: `#enqueue` chains every mutation and cold load onto one promise, and
// listeners run in later microtasks. Here .NET callers may be on any thread, so:
// - The line (SessionLine) is a FIFO queue of jobs run one at a time. Enqueue order, taken under a short lock, is the order jobs run in,
//   so ordering is deterministic. An enqueue on an idle line runs the job inline on the caller's thread until its first incomplete await
//   (an async method's usual start); later jobs run on the thread that completed the previous one. No lock is held while a job runs.
//   Job results complete with RunContinuationsAsynchronously, so a caller's continuation never runs on, or blocks, the line.
// - Every commit callback, preparation, Storage settlement, adoption and publication runs inside one job. Commit listeners run
//   synchronously in it, after adoption. Document-state and watch deliveries that a publication schedules (upstream's microtasks) run in
//   the same job right after publication, before the commit's task completes; a listener that awaits continues later on its own.
// - A job must not wait for a later job (a commit callback that awaits another commit deadlocks, as upstream's does). Session APIs called
//   from a listener only enqueue, so they never block the line.
// - The loaded-document cache and the listener sets are guarded by short locks so snapshot's fast path can read them off the line.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>
/// Session kernel over one storage backend (upstream <c>SessionImpl</c>): one mutation line, the loaded document tracker cache and
/// committed publication. Only committed state is observable.
/// </summary>
public class Session : ISession
{
    private readonly IStorage storage;
    private readonly Func<double> now;
    private readonly Lock documentsGate = new();
    private readonly Dictionary<string, LoadedDocument> documents = new(StringComparer.Ordinal);
    private readonly Lock listenersGate = new();
    private readonly List<Action<CommitPublication, DurableContext>> commitListeners = [];
    private readonly List<Action> closeListeners = [];
    private readonly SessionLine line = new();
    private readonly Host host;
    private readonly Lock closingGate = new();
    private Task? closing;
    private volatile bool closingStarted;
    private volatile Exception? poison;
    // Deliveries scheduled while a job publishes; run by that job right after publication. Guarded by deferGate.
    private readonly Lock deferGate = new();
    private List<Action>? deferred;

    /// <summary>Opens a Session kernel over <paramref name="storage"/>; <paramref name="now"/> is the wall clock for task times in milliseconds
    /// (default: the system clock).</summary>
    internal Session(IStorage storage, Func<double>? now = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        host = new Host(this);
    }

    /// <summary>Opens a Session kernel over one storage backend (upstream <c>createSession</c>). <paramref name="now"/> is the wall clock for
    /// task times in milliseconds; default the system clock.</summary>
    public static ISession Create(IStorage storage, Func<double>? now = null) => new Session(storage, now);

    // ─── Commits ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(tx => change(tx), context);
    }

    /// <inheritdoc/>
    public Task CommitAsync(Func<ITx, Task> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(async tx =>
        {
            await change(tx).ConfigureAwait(false);
            return true;
        }, context);
    }

    /// <inheritdoc/>
    public Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(tx => Task.FromResult(change(tx)), context);
    }

    /// <inheritdoc/>
    public Task CommitAsync(Action<ITx> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(tx =>
        {
            change(tx);
            return Task.FromResult(true);
        }, context);
    }

    /// <summary>
    /// Internal commit exposing the concrete transaction and its internal operations, such as the reserved-ID root bootstrap and task
    /// replacement (upstream <c>commitWith</c>). <paramref name="scope"/> sets the default <c>CreateTaskAsync</c> conversation and the task
    /// attributed to appended entries.
    /// </summary>
    internal Task<T> CommitWithAsync<T>(Func<Transaction, Task<T>> change, DurableContext context, TransactionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);
        try { AssertUsable(); }
        catch (Exception error) { return Task.FromException<T>(error); }
        return line.Enqueue(() => RunCommitAsync(change, context, scope));
    }

    /// <summary>Internal: <see cref="CommitWithAsync{T}"/> for an asynchronous callback without a result.</summary>
    internal Task CommitWithAsync(Func<Transaction, Task> change, DurableContext context, TransactionScope? scope = null) =>
        CommitWithAsync(async tx =>
        {
            await change(tx).ConfigureAwait(false);
            return true;
        }, context, scope);

    /// <summary>Internal: <see cref="CommitWithAsync{T}"/> for a synchronous callback without a result.</summary>
    internal Task CommitWithAsync(Action<Transaction> change, DurableContext context, TransactionScope? scope = null) =>
        CommitWithAsync(tx =>
        {
            change(tx);
            return Task.FromResult(true);
        }, context, scope);

    /// <summary>Internal: runs a read-only job on the mutation line so multi-read derivations observe one committed state.</summary>
    internal Task<T> ReadOnLineAsync<T>(Func<Task<T>> job)
    {
        try { AssertUsable(); }
        catch (Exception error) { return Task.FromException<T>(error); }
        return line.Enqueue(() =>
        {
            AssertHealthy();
            return job();
        });
    }

    /// <summary>
    /// Internal: a conversation document's current incarnation and value, for a job already running on the line (see
    /// <see cref="ReadOnLineAsync{T}"/>). Absent documents are null.
    /// </summary>
    internal async Task<(DocumentRecord Record, int Version, JsonObjectValue Value)?> ConversationDocumentOnLineAsync(ConversationDocToken token,
        ConversationId conversationId, DurableContext context)
    {
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, conversationId.Value, null);
        var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false);
        if (loaded is null) return null;
        Documents.CheckRecordScope(definition, loaded.Record);
        Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        return (loaded.Record, loaded.ValueVersion, loaded.Value);
    }

    /// <summary>Internal test and Harness barrier: completes once every job enqueued before it, and the deliveries those jobs scheduled,
    /// have run. It works on a closed or poisoned Session.</summary>
    internal Task FlushLineAsync() => line.Enqueue(() => Task.FromResult(true));

    /// <summary>Internal test barrier: how many jobs wait on the line behind the running one.</summary>
    internal int LineBacklog => line.Waiting;

    // ─── Reads ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => Snapshot(token, null, null, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        Snapshot(token, conversationId.Value, null, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => Snapshot(token, taskId.Value, null, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => Snapshot(token, null, key, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
        Snapshot(token, conversationId.Value, key, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        Snapshot(token, taskId.Value, key, context);

    private async Task<JsonObjectValue?> Snapshot(DocumentToken token, long? owner, string? key, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, owner, key);
        var cached = Cached(resolved.Id);
        var loaded = cached?.ValueVersion == definition.Version
            ? cached
            : await line.Enqueue(async () =>
            {
                AssertHealthy();
                return await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false);
            }).ConfigureAwait(false);
        if (loaded is null) return null;
        Documents.CheckRecordScope(definition, loaded.Record);
        Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        return loaded.Value;
    }

    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context) =>
        SnapshotAsOf(token, conversationId, null, at, context);

    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
        DurableContext context) => SnapshotAsOf(token, conversationId, key, at, context);

    private async Task<JsonObjectValue?> SnapshotAsOf(DocumentToken token, ConversationId conversationId, string? key, EntryId at, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, conversationId.Value, key);
        if (resolved.Address.Scope.Kind != DocumentScopeKind.Conversation)
            throw new ArgumentException("Session.snapshotAsOf() requires a conversation document");
        return await line.Enqueue(async () =>
        {
            AssertHealthy();
            var storedEntry = await storage.GetEntryAsync(conversationId, at, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Entry {at} is not visible from conversation {conversationId}");
            var address = resolved.Address with { Scope = DocumentScope.Conversation(storedEntry.Entry.ConversationId) };
            var record = await storage.FindDocumentAsync(address, storedEntry.CommitSeq, context).ConfigureAwait(false);
            if (record is null) return null;
            var stored = await storage.GetDocumentAsync(record.Id, storedEntry.CommitSeq, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Historical document {record.Id} ({record.Kind}) cannot be read");
            return Documents.MaterializeDocument(definition, stored);
        }).ConfigureAwait(false);
    }

    // ─── Document states ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(SessionDocToken token, DurableContext context) =>
        DocumentState(token, null, null, context);
    /// <inheritdoc/>
    public Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(ConversationDocToken token, ConversationId conversationId,
        DurableContext context) => DocumentState(token, conversationId.Value, null, context);
    /// <inheritdoc/>
    public Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        DocumentState(token, taskId.Value, null, context);
    /// <inheritdoc/>
    public Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        DocumentState(token, null, key, context);
    /// <inheritdoc/>
    public Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(ConversationDocFamilyToken token, ConversationId conversationId,
        string key, DurableContext context) => DocumentState(token, conversationId.Value, key, context);
    /// <inheritdoc/>
    public Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(TaskDocFamilyToken token, TaskId taskId, string key,
        DurableContext context) => DocumentState(token, taskId.Value, key, context);

    private Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentState(DocumentToken token, long? owner, string? key, DurableContext context)
    {
        try
        {
            AssertUsable();
            ArgumentNullException.ThrowIfNull(token);
            ArgumentNullException.ThrowIfNull(context);
            var definition = token.Definition;
            var resolved = Documents.ResolveAddress(definition, owner, key);
            return line.Enqueue<AttachedReplicatedState<JsonObjectValue?>?>(async () =>
            {
                AssertHealthy();
                var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false);
                if (loaded is null) return null;
                var (source, detach) = AttachDocument(definition, loaded,
                    (value, release) => new CommittedStateSource<JsonObjectValue?>(value, release, Defer));
                try
                {
                    return ReplicatedState.Attach<JsonObjectValue?>(source);
                }
                catch
                {
                    detach();
                    throw;
                }
            });
        }
        catch (Exception error)
        {
            return Task.FromException<AttachedReplicatedState<JsonObjectValue?>?>(error);
        }
    }

    // ─── Watches ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocToken token, DurableContext context) => WatchDoc(token, null, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        WatchDoc(token, conversationId.Value, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        WatchDoc(token, taskId.Value, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        WatchDoc(token, null, key, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context) => WatchDoc(token, conversationId.Value, key, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        WatchDoc(token, taskId.Value, key, context);

    private async Task<IWatchHandle<JsonObjectValue?>?> WatchDoc(DocumentToken token, long? owner, string? key, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, owner, key);
        var flag = new StrongBox<int>(context.IsCancellationRequested ? 1 : 0);
        var registration = context.CancellationToken.UnsafeRegister(static state => Volatile.Write(ref ((StrongBox<int>)state!).Value, 1), flag);
        try
        {
            var watch = await line.Enqueue(async () =>
            {
                AssertHealthy();
                if (Volatile.Read(ref flag.Value) != 0) context.ThrowIfCancellationRequested();
                var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false);
                if (Volatile.Read(ref flag.Value) != 0) context.ThrowIfCancellationRequested();
                if (loaded is null) return null;
                return AttachDocument(definition, loaded, (value, release) => new CommittedWatch<JsonObjectValue?>(value, release, Defer)).Observer;
            }).ConfigureAwait(false);
            if (watch is null) return null;
            if (Volatile.Read(ref flag.Value) != 0)
            {
                watch.Cancel();
                context.ThrowIfCancellationRequested();
            }
            if (context.HasCancellation) watch.ObserveCancellation(context);
            return watch;
        }
        finally
        {
            registration.Unregister();
        }
    }

    // ─── Lifecycle ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public virtual Task CloseAsync(DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Task closed;
        Action[] listeners = [];
        lock (closingGate)
        {
            if (closing is null)
            {
                // Seal admission before anything else runs, then stop observers; admitted work settles before Storage closes.
                closingStarted = true;
                lock (listenersGate)
                {
                    listeners = [.. closeListeners];
                    closeListeners.Clear();
                }
                var cleanup = context.WithoutCancellation();
                closing = Task.Run(async () =>
                {
                    await BeforeCloseAsync().ConfigureAwait(false);
                    await line.Enqueue(async () =>
                    {
                        lock (listenersGate) commitListeners.Clear();
                        lock (documentsGate) documents.Clear();
                        await storage.CloseAsync(cleanup).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                });
            }
            closed = closing;
        }
        foreach (var listener in listeners) listener();
        return context.WaitAsync(closed);
    }

    /// <summary>
    /// Runs inside every transaction that creates or forks a conversation, after the conversation record is staged. A plain Session stages
    /// nothing; a Harness stages its built-in documents.
    /// </summary>
    internal virtual Task ConversationCreatedAsync(Transaction tx, ConversationRecord record) => Task.CompletedTask;

    /// <summary>Runs after close seals admission and before the line closes Storage; must not fail.</summary>
    internal virtual Task BeforeCloseAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public IDisposable SubscribeCommits(Action<CommitPublication, DurableContext> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        AssertUsable();
        // A distinct wrapper per subscription, so two subscriptions of one delegate are independent and removal is exact.
        Action<CommitPublication, DurableContext> entry = (publication, context) => listener(publication, context);
        lock (listenersGate) commitListeners.Add(entry);
        return new Unsubscriber(() =>
        {
            lock (listenersGate) commitListeners.Remove(entry);
        });
    }

    /// <inheritdoc/>
    public IDisposable SubscribeClose(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        AssertUsable();
        Action entry = () => listener();
        lock (listenersGate) closeListeners.Add(entry);
        return new Unsubscriber(() =>
        {
            lock (listenersGate) closeListeners.Remove(entry);
        });
    }

    /// <summary>Internal: drops every loaded tracker on the mutation line; later access cold-loads from Storage.</summary>
    internal Task UnloadDocumentsAsync() => line.Enqueue(() =>
    {
        lock (documentsGate) documents.Clear();
        return Task.FromResult(true);
    });

    // ─── The commit ─────────────────────────────────────────────────────────

    private async Task<T> RunCommitAsync<T>(Func<Transaction, Task<T>> change, DurableContext context, TransactionScope? scope)
    {
        AssertHealthy();
        context.ThrowIfCancellationRequested();
        var tx = new Transaction(host, context, scope);
        T result;
        try
        {
            result = await change(tx).ConfigureAwait(false);
        }
        catch
        {
            await tx.SettleFailureAsync().ConfigureAwait(false);
            throw;
        }
        var writes = await tx.SettleSuccessAsync().ConfigureAwait(false);
        if (writes.Count == 0)
        {
            tx.Discard();
            return result;
        }
        Seq seq;
        try
        {
            // Once admitted, caller cancellation does not interrupt Storage settlement.
            seq = await storage.CommitAsync(writes, context.WithoutCancellation()).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            tx.Discard();
            // Callback errors never reach this branch; StorageRejectedException alone guarantees that no batch effect committed.
            if (error is not StorageRejectedException) poison = error;
            throw;
        }
        List<ICommitChange> documentChanges;
        try
        {
            documentChanges = tx.Adopt(seq);
        }
        catch (Exception error)
        {
            // Storage already committed; a failed adoption leaves memory behind durable state.
            poison = error;
            throw;
        }
        Publish(seq, writes, documentChanges, context);
        return result;
    }

    private void Publish(Seq seq, IReadOnlyList<StorageWrite> writes, List<ICommitChange> documentChanges, DurableContext context)
    {
        Action<CommitPublication, DurableContext>[] listeners;
        lock (listenersGate) listeners = [.. commitListeners];
        if (listeners.Length == 0) return;
        var changes = new List<ICommitChange>();
        foreach (var write in writes)
            if (write is ICommitChange change) changes.Add(change);
        changes.AddRange(documentChanges);
        var publication = new CommitPublication(seq, changes);
        lock (deferGate) deferred = [];
        try
        {
            foreach (var listener in listeners) listener(publication, context);
        }
        finally
        {
            RunDeferred();
        }
    }

    /// <summary>
    /// Schedules observer delivery work (upstream <c>queueMicrotask</c>). During a publication it runs in that commit's job right after the
    /// publication; otherwise it becomes its own line job, so it never runs inline in the caller.
    /// </summary>
    internal void Defer(Action work)
    {
        lock (deferGate)
        {
            if (deferred is not null)
            {
                deferred.Add(work);
                return;
            }
        }
        line.Enqueue(() =>
        {
            Run(work);
            return Task.FromResult(true);
        }, inline: false);
    }

    private void RunDeferred()
    {
        var index = 0;
        while (true)
        {
            Action work;
            lock (deferGate)
            {
                if (deferred is null || index == deferred.Count)
                {
                    deferred = null;
                    return;
                }
                work = deferred[index++];
            }
            Run(work);
        }
    }

    private static void Run(Action work)
    {
        try { work(); }
        catch (Exception error) { ReplicatedState.ReportUnhandled(error); }
    }

    /// <summary>
    /// Attaches an observer to one committed incarnation: checks the definition, then forwards this incarnation's committed changes and
    /// close. The returned detach removes both subscriptions.
    /// </summary>
    private (TObserver Observer, Action Detach) AttachDocument<TObserver>(DocDefinition definition, LoadedDocument loaded,
        Func<JsonObjectValue, Action, TObserver> create) where TObserver : ICommittedObserver<JsonObjectValue?>
    {
        Documents.CheckRecordScope(definition, loaded.Record);
        Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        IDisposable? commitSubscription = null;
        IDisposable? closeSubscription = null;
        var detach = () =>
        {
            commitSubscription?.Dispose();
            closeSubscription?.Dispose();
        };
        var observer = create(loaded.Value, detach);
        var observedVersion = loaded.ValueVersion;
        var recordId = loaded.Record.Id;
        // A document state's frames carry no caller cancellation; a watch observes its own cancellation.
        var stripCancellation = observer is CommittedStateSource<JsonObjectValue?>;
        commitSubscription = SubscribeCommits((publication, context) =>
        {
            foreach (var change in publication.Changes)
            {
                if (change is not DocumentCommitChange document || document.Record.Id != recordId) continue;
                var frameContext = stripCancellation ? context.WithoutCancellation() : context;
                var ops = ObservedOperations(ref observedVersion, document);
                // A migration-only base changes nothing for an observer of the new version.
                if (ops.IsEmpty) continue;
                observer.Advance(document.Value, ops, frameContext);
            }
        });
        closeSubscription = SubscribeClose(observer.CloseSession);
        return (observer, detach);
    }

    private async Task<LoadedDocument?> LoadDocumentAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context)
    {
        lock (documentsGate)
        {
            if (documents.TryGetValue(addressId, out var cached))
            {
                // A tracker serves only tokens of the version its value was materialized for; others reload from Storage.
                if (cached.ValueVersion == definition.Version) return cached;
                documents.Remove(addressId);
            }
        }
        var record = await storage.FindDocumentAsync(address, DocumentPoint.Current, context).ConfigureAwait(false);
        if (record is null) return null;
        var stored = await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, context).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Current document {record.Id} ({record.Kind}) cannot be read");
        var value = Documents.MaterializeDocument(definition, stored);
        var loaded = new LoadedDocument(addressId, stored.Record, stored.Version, definition.Version, stored.DeltasSinceBase, Tracker.Track(value));
        lock (documentsGate) documents[addressId] = loaded;
        return loaded;
    }

    private LoadedDocument? Cached(string addressId)
    {
        lock (documentsGate) return documents.GetValueOrDefault(addressId);
    }

    private void AssertUsable()
    {
        if (closingStarted) throw new InvalidOperationException("Session is closed");
        AssertHealthy();
    }

    private void AssertHealthy()
    {
        if (poison is { } error)
            throw new InvalidOperationException("Session is poisoned by a failed commit after storage admission; reopen it", error);
    }

    /// <summary>
    /// Operations an observer applies for one committed change. An observer hydrated under another definition version holds a differently
    /// shaped value, so it receives the new value as a root replacement instead of operations for that shape.
    /// </summary>
    private static ImmutableArray<Op> ObservedOperations(ref int observedVersion, DocumentCommitChange change)
    {
        if (change.Value is null) return Observation.RetirementOperations;
        if (change.Version == observedVersion) return change.Ops;
        observedVersion = change.Version!.Value;
        return [new ReplaceOp(change.Value)];
    }

    private sealed class Host(Session session) : ITransactionHost
    {
        public IStorage Storage => session.storage;
        public double Now() => session.now();
        public LoadedDocument? Cached(string addressId) => session.Cached(addressId);
        public Task<LoadedDocument?> LoadAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context) =>
            session.LoadDocumentAsync(definition, addressId, address, context);
        public void Install(LoadedDocument document)
        {
            lock (session.documentsGate) session.documents[document.AddressId] = document;
        }
        public void Evict(string addressId, DocumentId recordId)
        {
            lock (session.documentsGate)
                if (session.documents.TryGetValue(addressId, out var cached) && cached.Record.Id == recordId) session.documents.Remove(addressId);
        }
        public Task ConversationCreatedAsync(Transaction tx, ConversationRecord record) => session.ConversationCreatedAsync(tx, record);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}

/// <summary>
/// The Session mutation line (upstream <c>#tail</c> / <c>#enqueue</c>): a FIFO queue of jobs run one at a time, in enqueue order. See the
/// concurrency model in Session.cs.
/// </summary>
internal sealed class SessionLine
{
    private readonly Lock gate = new();
    private readonly Queue<Func<Task>> jobs = new();
    private bool running;

    /// <summary>How many jobs wait behind the running one.</summary>
    public int Waiting
    {
        get { lock (gate) return jobs.Count; }
    }

    /// <summary>
    /// Enqueues <paramref name="job"/>. On an idle line it starts inline when <paramref name="inline"/> is true, else on the thread pool.
    /// The returned task completes asynchronously, after the job has fully settled.
    /// </summary>
    public Task<T> Enqueue<T>(Func<Task<T>> job, bool inline = true)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Run()
        {
            try { completion.TrySetResult(await job().ConfigureAwait(false)); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        bool start;
        lock (gate)
        {
            jobs.Enqueue(Run);
            start = !running;
            running = true;
        }
        if (start)
        {
            if (inline) Pump();
            else ThreadPool.UnsafeQueueUserWorkItem(static line => line.Pump(), this, preferLocal: false);
        }
        return completion.Task;
    }

    private void Pump()
    {
        while (true)
        {
            Func<Task> next;
            lock (gate)
            {
                if (!jobs.TryDequeue(out next!))
                {
                    running = false;
                    return;
                }
            }
            var task = next();
            if (task.IsCompleted) continue;
            task.ContinueWith(static (_, state) => ((SessionLine)state!).Pump(), this, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }
    }
}
