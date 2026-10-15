// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/session/session.ts (createSession, setDefaultDocumentRetention,
// documentRetention, SessionImpl, contained, GuardedStorage, observedOperations, cancellationError).
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
// - The loaded-document cache, its retention bookkeeping and the listener sets are guarded by short locks so snapshot's fast path can read
//   them off the line; the clock and the retention setting (host code) are read outside them. Eviction itself runs only on the line.
// - Retention timers are .NET timers, which never keep a process alive, so upstream's fallback for timers that cannot be unreferenced
//   (sweeping only at the end of commits) is never needed; each commit still sweeps at its end, as upstream's does.
// - A failure is latched once (first wins) and closes the Session in the background; close listeners run synchronously in the call that
//   failed it, as upstream's do. Listener callbacks are contained: a throw, or a faulted task an asynchronous overload's listener returns
//   (upstream's rejected promise), is reported (or fails the Session, for the Harness's own listeners) and the others still run.
// - GuardedStorage keeps upstream's guard. One addition stands in for upstream's single thread: a read whose bounds a line job captured
//   continues off the line (Conversation.ContextAsync), and upstream starts its first Storage call before the closing line job runs. Such a
//   read takes a lease on the line (HoldStorageOpen); close waits for the lease's first call, which it admits, and refuses its later pages
//   from the moment close begins, as upstream's close reaches Storage before such a read's next page.
// - Close keeps upstream's order: one call seals admission and runs the close listeners, then starts the scheduler's join and the closing
//   line job inline (upstream's next microtask), so an explicit close returns with the Storage guard sealed when the line is idle. A
//   failure starts them on the thread pool after its report, never inside the call that failed.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>
/// Session kernel over one storage backend (upstream <c>SessionImpl</c>): one mutation line, the loaded document tracker cache and
/// committed publication. Only committed state is observable.
/// </summary>
/// <remarks>
/// <para>The cache is bounded by use: a document with no watch or document state attached leaves it once it was not read or written for the
/// retention period, and the next access loads it from Storage. Eviction runs as a job on the mutation line, so no transaction ever holds a
/// tracker that leaves.</para>
/// <para>Storage failures are final. Every Storage call goes through one guard: the first exception a Storage method throws, other than for
/// a cancelled caller, fails the Session, as does a commit it cannot adopt. That call gets the exception; every later call and every Storage
/// call still underway gets <see cref="SessionFailedException"/>; close listeners run at once, and the Session then closes itself, Storage
/// included, and <see cref="Closed"/> settles. Nothing retries: transient errors are the Storage's to retry. Reopening recovers from what was
/// committed.</para>
/// </remarks>
public class Session : ISession
{
    /// <summary>Default document retention: ten minutes, as for kept contexts.</summary>
    private static double defaultRetention = 600_000;
    /// <summary>Longest delay one retention timer waits; a longer retention waits in several steps.</summary>
    private const double MaxTimerDelay = 2_147_483_647;

    private readonly IStorage storage;
    private readonly StorageGuard guard;
    private readonly Func<double> now;
    private readonly Func<double> clock;
    private readonly Func<double> documentRetentionMs;
    private readonly Lock documentsGate = new();
    private readonly Dictionary<string, LoadedDocument> documents = new(StringComparer.Ordinal);
    // Cached documents with no observer attached, by address, oldest use first, with the time of that use. Guarded by documentsGate.
    private readonly LinkedList<(string AddressId, double Since)> idleDocuments = new();
    private readonly Dictionary<string, LinkedListNode<(string AddressId, double Since)>> idleIndex = new(StringComparer.Ordinal);
    // Observers attached per document incarnation (record ID); a cached incarnation with any is never evicted. Guarded by documentsGate.
    private readonly Dictionary<DocumentId, int> attachedDocuments = [];
    // The pending eviction: a timer for the oldest idle document, or a line job when it is already due. Guarded by documentsGate.
    private DocumentSweep? documentSweep;
    private int sweepJobs;
    private readonly Lock listenersGate = new();
    private readonly List<Action<CommitPublication, DurableContext>> commitListeners = [];
    // The Session's own listeners, such as its scheduler's: they keep memory in step with storage, so a throw fails it.
    private readonly List<Action<CommitPublication, DurableContext>> internalListeners = [];
    private readonly List<Action> closeListeners = [];
    private readonly SessionLine line = new();
    private readonly Host host;
    private readonly Lock closingGate = new();
    private Task? closing;
    private volatile bool closingStarted;
    private volatile FailureBox? failure;
    private readonly TaskCompletionSource failedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SessionEnd> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Deliveries scheduled while a job publishes; run by that job right after publication. Guarded by deferGate.
    private readonly Lock deferGate = new();
    private List<Action>? deferred;

    /// <summary>Opens a Session kernel over <paramref name="storage"/>; <paramref name="now"/> is the wall clock for task times in milliseconds
    /// (default: the system clock); <paramref name="documentRetentionMs"/> is read at each use (null: the default).</summary>
    internal Session(IStorage storage, Func<double>? now = null, Func<double?>? documentRetentionMs = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        // Waits race it; none has to.
        _ = failedSource.Task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        guard = new StorageGuard(storage, this);
        this.storage = new GuardedStorage(guard, null);
        var hostNow = now ?? SystemClock;
        this.now = hostNow;
        // Cache bookkeeping must never change the outcome of the operation it follows: a clock or a retention setting that throws counts as
        // now, and as the default.
        clock = () =>
        {
            try { return hostNow(); }
            catch (Exception) { return SystemClock(); }
        };
        var retention = documentRetentionMs ?? (() => null);
        this.documentRetentionMs = () =>
        {
            try { return DocumentRetention(retention()); }
            catch (Exception) { return DocumentRetention(null); }
        };
        host = new Host(this);
    }

    /// <summary>Opens a Session kernel over one storage backend (upstream <c>createSession</c>). <paramref name="now"/> is the wall clock for
    /// task times in milliseconds; default the system clock. <paramref name="documentRetentionMs"/> is how long an unobserved document stays
    /// cached after its last use (see <see cref="HarnessSettings.DocumentRetentionMs"/>); default ten minutes. The first exception a Storage
    /// method throws fails the Session (<see cref="SessionFailedException"/>).</summary>
    public static ISession Create(IStorage storage, Func<double>? now = null, double? documentRetentionMs = null) =>
        new Session(storage, now, documentRetentionMs is { } retention ? () => retention : null);

    /// <summary>Internal, for tests: the retention of every Session that keeps the default, as a suite run with eviction at once (upstream
    /// <c>setDefaultDocumentRetention</c>).</summary>
    internal static void SetDefaultDocumentRetention(double ms) => Volatile.Write(ref defaultRetention, ms);

    /// <summary>A retention as given, when it is a number of milliseconds, 0 or more (<see cref="double.PositiveInfinity"/> keeps documents);
    /// otherwise the default (upstream <c>documentRetention</c>).</summary>
    internal static double DocumentRetention(double? ms) => ms is { } value && value >= 0 ? value : Volatile.Read(ref defaultRetention);

    private static double SystemClock() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <inheritdoc/>
    public Task<SessionEnd> Closed => closedSource.Task;

    /// <summary>Internal: the exception that failed the Session, if one did (upstream <c>failure</c>).</summary>
    internal Exception? Failure => failure?.Error;

    /// <summary>
    /// Internal: whether close has begun (upstream <c>#closing !== undefined</c>), so admission is sealed. Upstream runs the close listeners in
    /// the same synchronous call that seals admission, so no other code ever sees one without the other; here another thread can see admission
    /// sealed before the listeners have run, and a component that ignores errors once closing must also check this.
    /// </summary>
    internal bool Closing => closingStarted;

    /// <summary>Internal: faults with <see cref="SessionFailedException"/> the moment the Session fails, before work underway has ended; never
    /// completes otherwise (upstream <c>failed</c>).</summary>
    internal Task Failed => failedSource.Task;

    /// <summary>The Storage behind the failure guard; every component reads and writes through it.</summary>
    internal IStorage GuardedStorage => storage;

    // ─── Failure ────────────────────────────────────────────────────────────

    /// <summary>
    /// Internal: fails the Session with <paramref name="error"/>, the first wins, and reports it once. Admission ends, and close listeners run
    /// now; they read the error from <see cref="Failure"/>. The Storage guard calls this; the Harness also calls it for a throw in its
    /// scheduler's own commits.
    /// </summary>
    internal void Fail(Exception error)
    {
        if (!TrySetFailure(error)) return;
        failedSource.TrySetException(new SessionFailedException(error));
        // Seal first, so a report handler that calls back finds the Session failed. Closing runs the close listeners synchronously; a
        // failing backend close is the caller's to see, and here nobody waits.
        var finishClose = BeginClose(DurableContext.Background);
        _ = closing!.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Report(error);
        // Upstream's next microtask: the join and the closing line job follow the listeners and the report. They start on the thread pool,
        // not inside the call that failed, which may be a line job or a Storage call.
        if (finishClose is not null) ThreadPool.UnsafeQueueUserWorkItem(static finish => finish(), finishClose, preferLocal: false);
    }

    private bool TrySetFailure(Exception error)
    {
        lock (closingGate)
        {
            if (failure is not null) return false;
            failure = new FailureBox(error);
            return true;
        }
    }

    /// <summary>Internal: the Session's failure, and errors of listeners, which never fail what ran them. A plain Session drops them.</summary>
    internal virtual void Report(Exception error) { }

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
    /// have run. It works on a closed or failed Session.</summary>
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
        if (cached?.ValueVersion == definition.Version) TouchDocument(resolved.Id);
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
                    return ReplicatedState.Attach<JsonObjectValue?>(source, new ReplicatedStateSourceOptions { OnError = Report });
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
                return AttachDocument(definition, loaded, (value, release) => new CommittedWatch<JsonObjectValue?>(value, release, Defer, Report)).Observer;
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
        BeginClose(context)?.Invoke();
        return context.WaitAsync(closing!);
    }

    /// <summary>
    /// The synchronous part of upstream's <c>close()</c>: seals admission and runs the close listeners. Returns the rest of close, which the
    /// caller starts at once, as upstream's next microtask: the scheduler's join (<see cref="BeforeCloseAsync"/>) and the line job that
    /// closes Storage, run inline up to their first wait, so the Storage guard refuses new calls before an explicit close returns. Null when
    /// close had begun.
    /// </summary>
    private Action? BeginClose(DurableContext context)
    {
        Action[] listeners;
        TaskCompletionSource closingSource;
        lock (closingGate)
        {
            if (closing is not null) return null;
            // Seal admission before anything else runs, then stop observers; admitted work settles before Storage closes.
            closingStarted = true;
            lock (listenersGate)
            {
                listeners = [.. closeListeners];
                closeListeners.Clear();
            }
            closingSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closing = closingSource.Task;
        }
        NotifyClose(listeners);
        var cleanup = context.WithoutCancellation();
        return () => _ = SettleClosingAsync(FinishCloseAsync(cleanup), closingSource);
    }

    private static async Task SettleClosingAsync(Task finishing, TaskCompletionSource closingSource)
    {
        try
        {
            await finishing.ConfigureAwait(false);
            closingSource.TrySetResult();
        }
        catch (Exception error)
        {
            closingSource.TrySetException(error);
        }
    }

    /// <summary>Joins what the Session runs, then closes Storage on the line once admitted work has settled; settles <see cref="Closed"/>.</summary>
    private async Task FinishCloseAsync(DurableContext cleanup)
    {
        try
        {
            await BeforeCloseAsync().ConfigureAwait(false);
            await line.Enqueue(async () =>
            {
                lock (listenersGate)
                {
                    commitListeners.Clear();
                    internalListeners.Clear();
                }
                lock (documentsGate)
                {
                    documents.Clear();
                    idleDocuments.Clear();
                    idleIndex.Clear();
                    attachedDocuments.Clear();
                }
                try
                {
                    await storage.CloseAsync(cleanup).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // A Storage that cannot close is a failed one; an earlier failure stays the cause.
                    if (TrySetFailure(error)) Report(error);
                    throw;
                }
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            closedSource.TrySetResult(failure is { } failed ? SessionEnd.Failed(failed.Error) : SessionEnd.Closed);
        }
    }

    /// <summary>Runs the close listeners once, each on its own: one that fails is reported, and the rest still run.</summary>
    private void NotifyClose(Action[] listeners)
    {
        DocumentSweep? sweep;
        lock (documentsGate)
        {
            sweep = documentSweep;
            documentSweep = null;
        }
        sweep?.Timer?.Dispose();
        foreach (var listener in listeners) Contained(listener, Report);
    }

    /// <summary>
    /// Runs inside every transaction that creates or forks a conversation, after the conversation record is staged. A plain Session stages
    /// nothing; a Harness stages its built-in documents.
    /// </summary>
    internal virtual Task ConversationCreatedAsync(Transaction tx, ConversationRecord record) => Task.CompletedTask;

    /// <summary>Runs after the close listeners (and, for a failure, its report) and before the line closes Storage; must not fail.</summary>
    internal virtual Task BeforeCloseAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public IDisposable SubscribeCommits(Action<CommitPublication, DurableContext> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Subscribe(commitListeners, listener);
    }

    /// <inheritdoc/>
    public IDisposable SubscribeCommits(Func<CommitPublication, DurableContext, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Subscribe(commitListeners, (publication, context) => Observe(listener(publication, context), Report));
    }

    /// <summary>
    /// Internal: <see cref="SubscribeCommits(Action{CommitPublication, DurableContext})"/> for the Session's own components, whose state follows
    /// each commit (upstream <c>observeCommits</c>). A throw leaves that state behind storage, so it fails the Session instead of being
    /// reported. They run before host listeners.
    /// </summary>
    internal IDisposable ObserveCommits(Action<CommitPublication, DurableContext> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Subscribe(internalListeners, listener);
    }

    private Unsubscriber Subscribe(List<Action<CommitPublication, DurableContext>> listeners, Action<CommitPublication, DurableContext> listener)
    {
        AssertUsable();
        // A distinct wrapper per subscription, so two subscriptions of one delegate are independent and removal is exact.
        Action<CommitPublication, DurableContext> entry = (publication, context) => listener(publication, context);
        lock (listenersGate) listeners.Add(entry);
        return new Unsubscriber(() =>
        {
            lock (listenersGate) listeners.Remove(entry);
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

    /// <inheritdoc/>
    public IDisposable SubscribeClose(Func<Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return SubscribeClose(() => Observe(listener(), Report));
    }

    /// <summary>
    /// Internal: called from a line job by a read that continues off the line, such as <c>Conversation.ContextAsync</c>; its first call through
    /// the lease's <see cref="StorageLease.Storage"/> is admitted even once close has begun, and close waits for it; its later calls are
    /// refused from then on. See the file header.
    /// </summary>
    internal StorageLease HoldStorageOpen() => guard.Lease();

    /// <summary>Internal: drops every loaded tracker on the mutation line; later access cold-loads from Storage.</summary>
    internal Task UnloadDocumentsAsync() => line.Enqueue(() =>
    {
        lock (documentsGate)
        {
            documents.Clear();
            idleDocuments.Clear();
            idleIndex.Clear();
        }
        return Task.FromResult(true);
    });

    /// <summary>Internal, for tests: how many documents the cache holds, how many are idle, how many incarnations are pinned, and how many
    /// sweep jobs were queued on the line so far (upstream <c>documentCacheSizes</c>).</summary>
    internal (int Cached, int Idle, int Attached, int Sweeps) DocumentCacheSizes
    {
        get
        {
            lock (documentsGate) return (documents.Count, idleDocuments.Count, attachedDocuments.Count, sweepJobs);
        }
    }

    // ─── Document cache retention ───────────────────────────────────────────

    /// <summary>Notes a use of a cached document: an unattached one becomes the newest idle one, and an eviction is scheduled.</summary>
    private void TouchDocument(string addressId)
    {
        var at = clock();
        var retention = documentRetentionMs();
        bool schedule;
        lock (documentsGate)
        {
            if (!documents.TryGetValue(addressId, out var cached) || attachedDocuments.ContainsKey(cached.Record.Id)) return;
            // A hot document moves to the back at most once per granule, a tenth of the retention up to 1 s: it may then leave up to a
            // granule early, which costs a reload, while a commit-heavy workload skips the reordering.
            if (idleIndex.TryGetValue(addressId, out var node))
            {
                if (at - node.Value.Since < Math.Min(1_000, retention / 10)) return;
                idleDocuments.Remove(node);
            }
            idleIndex[addressId] = idleDocuments.AddLast((addressId, at));
            // A pending sweep for the oldest document comes first unless the retention was shortened since; then it moves up.
            schedule = documentSweep is null || documentSweep.At > at + retention;
        }
        if (schedule) ScheduleDocumentSweep();
    }

    private void DropDocumentLocked(string addressId)
    {
        documents.Remove(addressId);
        if (idleIndex.Remove(addressId, out var node)) idleDocuments.Remove(node);
    }

    /// <summary>
    /// Schedules the eviction of the oldest idle document: on the line at once when it is due, otherwise when it expires, by a timer that
    /// never keeps the process alive.
    /// </summary>
    private void ScheduleDocumentSweep()
    {
        var current = clock();
        var retention = documentRetentionMs();
        lock (documentsGate)
        {
            if (closingStarted) return;
            if (idleDocuments.First is not { } oldest) return;
            var at = oldest.Value.Since + retention;
            if (documentSweep is { } pending && pending.At <= at) return;
            documentSweep?.Timer?.Dispose();
            // The pending sweep stays set until its job runs, so touches while the line is busy queue no more.
            var sweep = new DocumentSweep(at);
            documentSweep = sweep;
            var delay = at - current;
            if (delay <= 0)
            {
                QueueDocumentSweepLocked(sweep);
                return;
            }
            sweep.Timer = TimeProvider.System.CreateTimer(static state =>
            {
                var (session, sweep) = ((Session, DocumentSweep))state!;
                lock (session.documentsGate) session.QueueDocumentSweepLocked(sweep);
            }, (this, sweep), TimeSpan.FromMilliseconds(Math.Min(delay, MaxTimerDelay)), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Queues the sweep job on the line; called under <see cref="documentsGate"/>, and never runs the job inline.</summary>
    private void QueueDocumentSweepLocked(DocumentSweep sweep)
    {
        sweepJobs++;
        _ = line.Enqueue(() =>
        {
            bool sweepNow;
            lock (documentsGate)
            {
                if (ReferenceEquals(documentSweep, sweep)) documentSweep = null;
                sweepNow = !closingStarted;
            }
            sweep.Timer?.Dispose();
            if (sweepNow) SweepDocuments();
            return Task.FromResult(true);
        }, inline: false);
    }

    /// <summary>On the line: evicts the idle documents whose retention passed, oldest first, then schedules the next eviction.</summary>
    private void SweepDocuments()
    {
        var expired = clock() - documentRetentionMs();
        lock (documentsGate)
        {
            while (idleDocuments.First is { } oldest && oldest.Value.Since <= expired) DropDocumentLocked(oldest.Value.AddressId);
        }
        ScheduleDocumentSweep();
    }

    /// <summary>Pins a cached incarnation for one observer; the returned release unpins it once, idle again after the last.</summary>
    private Action PinDocument(string addressId, DocumentId recordId)
    {
        lock (documentsGate)
        {
            attachedDocuments[recordId] = attachedDocuments.GetValueOrDefault(recordId) + 1;
            if (idleIndex.Remove(addressId, out var node)) idleDocuments.Remove(node);
        }
        var pinned = 1;
        return () =>
        {
            if (Interlocked.Exchange(ref pinned, 0) == 0) return;
            bool touch;
            lock (documentsGate)
            {
                var count = attachedDocuments.GetValueOrDefault(recordId, 1) - 1;
                if (count > 0)
                {
                    attachedDocuments[recordId] = count;
                    return;
                }
                attachedDocuments.Remove(recordId);
                touch = documents.TryGetValue(addressId, out var cached) && cached.Record.Id == recordId;
            }
            if (touch) TouchDocument(addressId);
        };
    }

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
            // A callback that caught a failed read must not commit, nor succeed as if it had.
            AssertHealthy();
        }
        catch
        {
            await tx.SettleFailureAsync().ConfigureAwait(false);
            SweepDocuments();
            throw;
        }
        IReadOnlyList<StorageWrite> writes;
        try
        {
            writes = await tx.SettleSuccessAsync().ConfigureAwait(false);
        }
        catch
        {
            // Settlement aborted every change already.
            SweepDocuments();
            throw;
        }
        if (writes.Count == 0)
        {
            tx.Discard();
            SweepDocuments();
            return result;
        }
        Seq seq;
        try
        {
            // Once admitted, caller cancellation does not interrupt Storage settlement.
            seq = await storage.CommitAsync(writes, context.WithoutCancellation()).ConfigureAwait(false);
        }
        catch
        {
            // The guard has failed the Session.
            tx.Discard();
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
            Fail(error);
            throw;
        }
        foreach (var adopted in documentChanges)
        {
            var record = adopted switch
            {
                DocumentCommitChange document => document.Record,
                DocumentCopyCommitChange copy => copy.Record,
                _ => null,
            };
            if (record is not null) TouchDocument(Documents.AddressId(DocumentAddress.Of(record)));
        }
        Publish(seq, writes, documentChanges, context);
        SweepDocuments();
        return result;
    }

    private void Publish(Seq seq, IReadOnlyList<StorageWrite> writes, List<ICommitChange> documentChanges, DurableContext context)
    {
        Action<CommitPublication, DurableContext>[] internals;
        Action<CommitPublication, DurableContext>[] listeners;
        lock (listenersGate)
        {
            internals = [.. internalListeners];
            listeners = [.. commitListeners];
        }
        if (internals.Length == 0 && listeners.Length == 0) return;
        var changes = new List<ICommitChange>();
        foreach (var write in writes)
            if (write is ICommitChange change) changes.Add(change);
        changes.AddRange(documentChanges);
        var publication = new CommitPublication(seq, changes);
        lock (deferGate) deferred = [];
        try
        {
            // The commit is durable: a failing listener neither fails the commit nor skips the others.
            foreach (var listener in internals) Contained(() => listener(publication, context), Fail);
            foreach (var listener in listeners) Contained(() => listener(publication, context), Report);
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
    /// Runs a host callback that must not fail its caller (upstream <c>contained</c>): a throw goes to <paramref name="onError"/>, which is
    /// not awaited.
    /// </summary>
    internal static void Contained(Action callback, Action<Exception> onError)
    {
        try { callback(); }
        catch (Exception error) { onError(error); }
    }

    /// <summary>Reports a faulted or cancelled task a host callback returned (upstream's rejected promise in <c>contained</c>), without
    /// awaiting it.</summary>
    internal static void Observe(Task? task, Action<Exception> onError)
    {
        if (task is null || task.IsCompletedSuccessfully) return;
        task.ContinueWith(static (completed, state) =>
        {
            var report = (Action<Exception>)state!;
            Exception error = completed.IsFaulted
                ? completed.Exception!.InnerExceptions.Count == 1 ? completed.Exception.InnerException! : completed.Exception
                : new TaskCanceledException(completed);
            try { report(error); }
            catch (Exception reportError) { ReplicatedState.ReportUnhandled(reportError); }
        }, onError, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Attaches an observer to one committed incarnation: checks the definition, pins the incarnation in the cache, then forwards this
    /// incarnation's committed changes and close. The returned detach removes both subscriptions and the pin.
    /// </summary>
    private (TObserver Observer, Action Detach) AttachDocument<TObserver>(DocDefinition definition, LoadedDocument loaded,
        Func<JsonObjectValue, Action, TObserver> create) where TObserver : ICommittedObserver<JsonObjectValue?>
    {
        Documents.CheckRecordScope(definition, loaded.Record);
        Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        IDisposable? commitSubscription = null;
        IDisposable? closeSubscription = null;
        // Attached, the incarnation stays cached; once its last observer detaches, or it retires, it is idle again.
        var unpin = PinDocument(loaded.AddressId, loaded.Record.Id);
        var detach = () =>
        {
            commitSubscription?.Dispose();
            closeSubscription?.Dispose();
            unpin();
        };
        TObserver observer;
        try
        {
            observer = create(loaded.Value, detach);
        }
        catch
        {
            unpin();
            throw;
        }
        var observedVersion = loaded.ValueVersion;
        var recordId = loaded.Record.Id;
        // A document state's frames carry no caller cancellation; a watch observes its own cancellation.
        var stripCancellation = observer is CommittedStateSource<JsonObjectValue?>;
        try
        {
            commitSubscription = ObserveCommits((publication, context) =>
            {
                foreach (var change in publication.Changes)
                {
                    if (change is not DocumentCommitChange document || document.Record.Id != recordId) continue;
                    var frameContext = stripCancellation ? context.WithoutCancellation() : context;
                    var ops = ObservedOperations(ref observedVersion, document);
                    // A migration-only base changes nothing for an observer of the new version.
                    if (ops.IsEmpty) continue;
                    // A retired incarnation never changes again: its observers keep their last value but pin nothing.
                    if (document.Value is null) unpin();
                    observer.Advance(document.Value, ops, frameContext);
                }
            });
            closeSubscription = SubscribeClose(() => observer.CloseSession(Failure));
        }
        catch
        {
            // Close or failure began while the document loaded: attach nothing.
            detach();
            throw;
        }
        return (observer, detach);
    }

    private async Task<LoadedDocument?> LoadDocumentAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context)
    {
        LoadedDocument? hit = null;
        lock (documentsGate)
        {
            if (documents.TryGetValue(addressId, out var cached))
            {
                // A tracker serves only tokens of the version its value was materialized for; others reload from Storage.
                if (cached.ValueVersion == definition.Version) hit = cached;
                else DropDocumentLocked(addressId);
            }
        }
        if (hit is not null)
        {
            TouchDocument(addressId);
            return hit;
        }
        var record = await storage.FindDocumentAsync(address, DocumentPoint.Current, context).ConfigureAwait(false);
        if (record is null) return null;
        var stored = await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, context).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Current document {record.Id} ({record.Kind}) cannot be read");
        var value = Documents.MaterializeDocument(definition, stored);
        var loaded = new LoadedDocument(addressId, stored.Record, stored.Version, definition.Version, stored.DeltasSinceBase, Tracker.Track(value));
        lock (documentsGate) documents[addressId] = loaded;
        TouchDocument(addressId);
        return loaded;
    }

    private LoadedDocument? Cached(string addressId)
    {
        lock (documentsGate) return documents.GetValueOrDefault(addressId);
    }

    /// <summary>Internal: throws <see cref="SessionFailedException"/> when failed, else an exception when closing.</summary>
    internal void AssertUsable()
    {
        AssertHealthy();
        if (closingStarted) throw new InvalidOperationException("Session is closed");
    }

    private void AssertHealthy()
    {
        if (failure is { } failed) throw new SessionFailedException(failed.Error);
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

    private sealed record FailureBox(Exception Error);

    private sealed class DocumentSweep(double at)
    {
        public double At { get; } = at;
        public ITimer? Timer { get; set; }
    }

    private sealed class Host(Session session) : ITransactionHost
    {
        public IStorage Storage => session.storage;
        public double Now() => session.now();
        public LoadedDocument? Cached(string addressId)
        {
            var cached = session.Cached(addressId);
            if (cached is not null) session.TouchDocument(addressId);
            return cached;
        }
        public Task<LoadedDocument?> LoadAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context) =>
            session.LoadDocumentAsync(definition, addressId, address, context);
        public void Install(LoadedDocument document)
        {
            lock (session.documentsGate) session.documents[document.AddressId] = document;
            session.TouchDocument(document.AddressId);
        }
        public void Evict(string addressId, DocumentId recordId)
        {
            lock (session.documentsGate)
                if (session.documents.TryGetValue(addressId, out var cached) && cached.Record.Id == recordId) session.DropDocumentLocked(addressId);
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

/// <summary>
/// The state behind the Session's Storage guard (upstream <c>GuardedStorage</c>'s fields): the backend, the Session to fail, the calls
/// underway, and whether close has reached the backend. <see cref="GuardedStorage"/> views share it.
/// </summary>
internal sealed class StorageGuard(IStorage storage, Session session)
{
    private readonly Lock gate = new();
    // Backend calls underway, also those off the Session line, which close waits for before it closes the backend.
    private readonly HashSet<Task> underway = [];
    private int unstartedLeases;
    private TaskCompletionSource? leasesStarted;
    private bool closing;

    public IStorage Storage { get; } = storage;

    /// <summary>A lease for one read that continues off the line; see <see cref="Session.HoldStorageOpen"/>.</summary>
    public StorageLease Lease()
    {
        lock (gate) unstartedLeases++;
        return new StorageLease(this);
    }

    /// <summary>Marks a lease started (its first call admitted, or the lease released unused); close waits for every lease to start.</summary>
    internal void StartLease()
    {
        TaskCompletionSource? started = null;
        lock (gate)
        {
            if (--unstartedLeases == 0)
            {
                started = leasesStarted;
                leasesStarted = null;
            }
        }
        started?.TrySetResult();
    }

    /// <summary>
    /// Closes the backend once every lease has started and every call underway has settled, so none outlives it or fails after
    /// <c>Closed</c> settles. New calls are refused from here on: a paged read must not start its next page while the backend closes.
    /// </summary>
    public async Task CloseAsync(DurableContext context)
    {
        Task leases;
        lock (gate)
        {
            closing = true;
            if (unstartedLeases == 0) leases = Task.CompletedTask;
            else
            {
                leasesStarted ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                leases = leasesStarted.Task;
            }
        }
        await leases.ConfigureAwait(false);
        Task[] calls;
        lock (gate) calls = [.. underway];
        await Task.WhenAll(calls).ConfigureAwait(false);
        await Storage.CloseAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one backend call under the guard. <paramref name="read"/> is a read's context; <c>CommitAsync</c> and <c>MintIdAsync</c> pass
    /// none, so nothing exempts their exceptions: once a batch is admitted, whether it committed is unknown. A call still underway when another
    /// fails the Session ends with <see cref="SessionFailedException"/> too, whatever it returns: nothing it read or wrote is used.
    /// <paramref name="lease"/> admits its first call even while closing; its later calls are refused once the Session is closing.
    /// </summary>
    public async Task<T> CallAsync<T>(DurableContext? read, StorageLease? lease, Func<Task<T>> run)
    {
        AssertHealthy();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        lock (gate)
        {
            var admitted = lease is not null && lease.TryStart();
            started = admitted;
            // A leased read's later pages are refused once close has begun, not only once it reached the backend: upstream's close reaches
            // Storage before such a read continues, and here a line job still underway may hold the closing job back.
            if (!admitted && (closing || (lease is not null && session.Closing))) throw new InvalidOperationException("Session is closed");
            underway.Add(done.Task);
        }
        if (started) StartLease();
        try
        {
            T result;
            try
            {
                result = await run().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                AssertHealthy();
                // An invalid read, or one its caller cancelled, says nothing about the Storage; it fails that call only.
                var exempt = read is not null && (error is StorageRequestException || read.IsCancellationRequested);
                if (!exempt) session.Fail(error);
                throw;
            }
            AssertHealthy();
            return result;
        }
        finally
        {
            lock (gate) underway.Remove(done.Task);
            done.TrySetResult();
        }
    }

    private void AssertHealthy()
    {
        if (session.Failure is { } failure) throw new SessionFailedException(failure);
    }
}

/// <summary>
/// A read's hold on Storage across the Session line (see <see cref="Session.HoldStorageOpen"/>): its first call through <see cref="Storage"/>
/// is admitted even once close has begun, and close waits until it has started, or the lease is disposed unused. Its later calls are refused
/// once close has begun.
/// </summary>
internal sealed class StorageLease : IDisposable
{
    private readonly StorageGuard guard;
    private int state;

    internal StorageLease(StorageGuard guard)
    {
        this.guard = guard;
        Storage = new GuardedStorage(guard, this);
    }

    /// <summary>The guarded Storage the leased read calls.</summary>
    public IStorage Storage { get; }

    /// <summary>Claims the lease's first call; true once.</summary>
    internal bool TryStart() => Interlocked.CompareExchange(ref state, 1, 0) == 0;

    /// <summary>Releases an unused lease.</summary>
    public void Dispose()
    {
        if (TryStart()) guard.StartLease();
    }
}

/// <summary>
/// The Session's Storage behind its failure guard (upstream <c>GuardedStorage</c>); every component of the Session reads and writes through
/// it. Before a call, a failed Session gets <see cref="SessionFailedException"/> without reaching the backend. A call that throws fails the
/// Session and rethrows the backend's exception, unless it is a <see cref="StorageRequestException"/> or a read whose caller's context was
/// cancelled: neither says the Storage is broken. <see cref="CloseAsync"/> always reaches the backend, so a failed Session still closes it.
/// </summary>
internal sealed class GuardedStorage(StorageGuard guard, StorageLease? lease) : IStorage
{
    private IStorage Inner => guard.Storage;

    /// <summary>Never exempt, whatever it throws: once admitted, whether a failed batch committed is unknown.</summary>
    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) =>
        guard.CallAsync(null, lease, () => Inner.CommitAsync(writes, context));

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => guard.CallAsync(null, lease, Inner.MintIdAsync<TId>);

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetConversationAsync(id, context));

    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanConversationsAsync(query, limit, cursor, context));

    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetEntryAsync(id, context));

    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetEntryAsync(conversationId, id, context));

    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context));

    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanEntriesAsync(query, limit, cursor, context));

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetTaskAsync(id, context));

    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanTasksAsync(query, limit, cursor, context));

    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetSubmissionAsync(id, context));

    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanSubmissionsAsync(query, limit, cursor, context));

    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetSubmissionByRequestAsync(conversationId, requestId, context));

    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.FindDocumentAsync(address, at, context));

    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetDocumentAsync(id, at, context));

    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanDocumentsAsync(query, limit, cursor, context));

    /// <summary>Closes the backend after the calls underway; see <see cref="StorageGuard.CloseAsync"/>.</summary>
    public Task CloseAsync(DurableContext context) => guard.CloseAsync(context);
}
