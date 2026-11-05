// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/session/session.ts (createSession, setDefaultDocumentRetention,
// documentRetention, SessionImpl, contained, GuardedStorage, observedOperations, cancellationError). Pi v1.5.0 sync (area
// harness-options): a read-only Session refuses commits with writes (upstream be14d9732); the citation stays on v1.2.0 until the file's
// other areas land. Pi v1.5.0 sync (area storage-batches): a commit sends its writes as one batch of the batched Storage.commit and judges
// the result, as session/writer.ts does for a call of one part: a refusal fails the Session and is the commit's error, as a rejection is.
// Group commit (several parts per call) and the publication scope (Storage.publishing) came with area group-commit (below); GuardedStorage
// forwards both. Pi v1.5.0 sync (area sleep): createSession's setTimer arms the document sweep as a background timer (upstream
// f75b3240a, Timers.cs); a host that declines one leaves eviction to the end of each commit. Pi v1.5.0 sync (area reports): a report
// carries its ReportInfo (a30c2a81d): the Session's failure and a failing close are storage_failed, a host timer host_error setTimer, and a
// listener's throw listener_error, the default. Pi v1.5.0 sync (area telemetry, 2ec55d808 and v1.5.0's fix): a stored commit's publication
// records its committed entries (pi.durable.entry) and settled submissions (pi.durable.submission) on the commit context's span first,
// listeners or not; a throwing telemetry adapter is reported host_error telemetry with that context and the commit stands. A listener's
// report, and the Session's failure from a commit, carry the commit context, so the Harness records them on its span. Pi v1.5.0 sync
// (area watches, 92ab53602 and 42cb822f1): a document watch is Chord's Watch (Chord/Watch.cs) on the Session line's scheduler; the Session
// ends it ended at close, failed with its failure, and delivers a retirement as its last call (a null value, [["r", null]] and retired),
// after which it ends ended (Chord's endAfterDelivery). documentState() is removed, and Durable no longer uses Chord's replicatedState().
// Pi v1.5.0 sync (area watch-folding, 8b0caefc2 and 5f312dac4): instead, a document watch follows its address (DocumentFollower, upstream
// #observeDocument with createWatch and endWatch): a retirement is a retired call with the last value and no ops, a later incarnation
// arrives as [["r", value]], the current incarnation is pinned (nothing while the address is absent), and a task document's watch ends
// ended after the retirement call of the commit that makes its task terminal. Its value is never null. Publication at adoption is not
// here yet (area publication-caches). Pi v1.5.0 sync (area observers, 8b0caefc2 with v1.5.0's shapes): ObserveDocAsync attaches a host's
// Observer<T> behind GuardedObserver<T> (Observers.cs) through the same source (#attachDocument and #observeDocument: AttachDocumentAsync
// and ObserveDocument), whose watch is a WatchObserver<T>; the follower calls its observer outside its lock, and the Session's close
// detaches it, then ends a watch ended and an observer failed with "session closed" (CloseEnds, decision "Slice 8 S1"). Pi v1.5.0 sync
// (area deferred-commits, 6c2fd73b0): a commit takes CommitOptions. A deferred one (CommitPersistence) that only changes existing latest,
// initial-fork conversation documents is held:
// adopted in memory and shown to the Session's own observers (ObservedCommit "memory", which watches and views follow), its operations
// queued per incarnation (Hold, Chord's PendingOps) and the document pinned in the cache. The first commit that reads or writes the document
// stores them in front of its own (Transaction.TakeHeld); otherwise they are stored, every held document in one storage transaction, at the
// earliest deadline (one foreground timer), at close after the writes at close, by UnloadDocumentsAsync, and before a token of another
// definition version reloads the document. ObserveCommits sees each storage transaction with exactly what it stored, so its seq has
// gaps; a failed Session stores nothing more.
// Pi v1.5.0 sync (area restarts, b3a29f111): WriteAtCloseAsync (upstream writeAtClose), for the scheduler's join, writes on the line,
// after the commits admitted before the seal, straight to Storage; a failed Session writes nothing. Since area group-commit it is a part of
// the writer, as upstream's. Pi v1.5.0 sync (area vocabulary, 5f312dac4): SubscribeCommits(listener) is
// ObserveCommits(CommitObserver), each observer behind the generic guard (ObserverGuard<CommitPublication>, Observers.cs): a throw is
// reported, detaches it and ends it listener_error, and close ends the rest after its last publication, failed with "session closed"
// (CloseEnds, as v1.5.0 ends every synchronous observer) or with the error that failed the Session. The internal observeCommits is
// FollowCommits (upstream followCommits); Closed settles a SessionEnd tagged by Kind, and a document change is a DocumentValueChange or a
// DocumentRetireChange.
// Pi v1.5.0 sync (area admission, cc9c52310): OwnsTransaction (upstream ownsTransaction) tells whether a transaction is one of this
// Session's commits, for submit(tx, draft).
// Pi v1.5.0 sync (area group-commit, e5c6182ba's Session half; docs/group-commit.md): a Session no longer holds its line while a commit
// is stored. A job's line part runs its callback and settlement and admits its commit to the writer (Session/Writer.cs) as a part; the
// writer sends every part admitted while a call is in flight together in the next storage call, each with its own seq and outcome, and
// publishes them in line order. A job runs while earlier commits are being stored until it touches a key one of them wrote (Session/Keys.cs:
// every Storage read of a job goes through GatedStorage, a document load touches its address, a retirement its address and a terminal task's
// scope); then it waits for that commit's publication. Outcomes are delivered in line order (fences); an exempt internal commit
// (CommitInternalAsync) that writes nothing and waited at no gate is delivered at once; every job after the one a failure is the outcome
// of gets SessionFailed, except the other parts of a call that rejected. At most 256 jobs wait for their outcome. Held commits, the
// held-store path (StoreHoldsAsync), the writes at close and close itself go through the writer; a stored commit is published inside the
// Storage's publication scope (IStorage.Publishing). Upkeep (sweeps, hold timers, close, the writes at close) runs as maintain jobs, with
// no outcome fence. The read caches, conversation tails and the task-document index (upstream CachedStorage, ConversationTails,
// #taskDocuments) are area publication-caches'. Pi v1.5.0 sync (area group-commit-scheduler, e5c6182ba's scheduler half): a Harness's
// line jobs overlap storage too, as its scheduler's decisions record the keys they read and its commits write their membership keys
// (MembershipKeysAsync); the serialized line a Harness kept until then (LineWaitsForPublication) is gone. A stored commit's keys leave
// after the Session's own observers (the scheduler's indexes among them) adopted it and before host observers run (Publish), so a
// decision region that finds a key without an unpublished writer reads memory that already holds its every write. The citation stays on
// v1.2.0: areas publication-caches and registry-reader change this file too.
//
// Concurrency model. Upstream relies on one JavaScript thread: `#enqueue` chains every mutation and cold load onto one promise, the writer
// publishes between line jobs, and listeners run in later microtasks. Here .NET callers may be on any thread, so:
// - The line (SessionLine) is a FIFO queue of jobs run one at a time. Enqueue order, taken under a short lock, is the order jobs run in,
//   so ordering is deterministic. An enqueue on an idle line runs the job inline on the caller's thread until its first incomplete await
//   (an async method's usual start); later jobs run on the thread that completed the previous one. No lock is held while a job runs.
//   Jobs one thread enqueues in SessionLine.Together queue back to back, as upstream's enqueues in one synchronous stretch do. A job's
//   line index, its outcome fence and the job on the line (current) are taken when it starts, so on the line, in line order.
//   Outcomes complete with RunContinuationsAsynchronously, so a caller's continuation never runs on, or blocks, the line or the writer. A
//   flow with an OrderedLineDelivery (the scheduler's abort invocations) gets its outcomes through that delivery instead, off the line too:
//   in line order, each continuation running until it next suspends before the next, as upstream's microtasks resume them.
// - A job's callback, preparation and settlement run in its line part; storing and publication run in the writer, which may run on
//   another thread while later jobs run. What publication changes that a later job reads is keyed (the gates above), and memory adopts a
//   part before its keys leave (Writer.cs), so a job that passed a gate sees the commit adopted. Every other structure publication and jobs
//   share is guarded: the document cache by documentsGate, the held documents and their timer by holdsGate (TakeHeld's lookups included),
//   the listener sets by listenersGate. Commit listeners run synchronously in the publication, after adoption. Watch deliveries that a
//   publication schedules (upstream's microtasks) run in that publication right after its listeners (a thread-static scope, so a job on
//   another thread never joins it), before the commit's gate opens and its outcome completes; a listener that awaits continues later.
// - A job must not wait for a later job (a commit callback that awaits another commit deadlocks, as upstream's does). Session APIs called
//   from a listener only enqueue, so they never block the line.
// - The loaded-document cache, its retention bookkeeping and the listener sets are guarded by short locks so snapshot's fast path can read
//   them off the line; the clock and the retention setting (host code) are read outside them. Eviction itself runs only on the line.
// - Retention timers are background timers of the host's SetTimer, by default .NET timers, which never keep a process alive and are never
//   declined. A host that declines one gets upstream's fallback: no timer is kept, and the end of each commit sweeps, as it always does.
//   The host's SetTimer and disarm are host code, so they run outside documentsGate: a sweep is installed first and armed after, and a
//   timer armed for a sweep that was replaced, ran, or closed meanwhile is disarmed again.
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
using System.Runtime.ExceptionServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>
/// Session kernel over one storage backend (upstream <c>SessionImpl</c>): one mutation line, the loaded document tracker cache and
/// committed publication. Only committed state is observable.
/// </summary>
/// <remarks>
/// <para>The cache is bounded by use: a document with no watch attached leaves it once it was not read or written for the retention
/// period, and the next access loads it from Storage. Eviction runs as a job on the mutation line, so no transaction ever holds a
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
    private readonly IStorage storage;
    private readonly StorageGuard guard;
    private readonly Func<double> now;
    private readonly Func<double> clock;
    private readonly Func<double> documentRetentionMs;
    private readonly Func<double> persistWithinMs;
    // Documents with held changes, by incarnation (record ID), in the order they were first held; each is pinned in the cache until they are
    // stored. Changed on the line only; guarded by holdsGate, as DocumentCacheSizes reads it off the line.
    private readonly Lock holdsGate = new();
    private readonly OrderedDictionary<DocumentId, Hold> holds = [];
    // The armed store of held documents. Guarded by holdsGate.
    private HoldTimer? holdTimer;
    private readonly HostTimers timers;
    /// <summary>Refuse every commit with writes (<see cref="HarnessOptions.ReadOnly"/>).</summary>
    private readonly bool readOnly;
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
    /// <summary>Set once the host declined a background timer: then none is kept, and the end of each commit evicts what expired. Guarded by
    /// documentsGate.</summary>
    private bool documentTimersUnavailable;
    private readonly Lock listenersGate = new();
    // Host commit observers (ObserveCommits), each behind its guard.
    private readonly List<ObserverGuard<CommitPublication>> commitListeners = [];
    // The Session's own listeners, such as its scheduler's: they keep memory in step with storage, so a throw fails it.
    private readonly List<Action<ObservedCommit, DurableContext>> internalListeners = [];
    private readonly List<Action> closeListeners = [];
    private readonly SessionLine line = new();
    private readonly Host host;
    private readonly Lock closingGate = new();
    private Task? closing;
    private volatile bool closingStarted;
    private volatile FailureBox? failure;
    private readonly TaskCompletionSource failedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SessionEnd> closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Deliveries scheduled while this thread publishes one of this Session's commits; run by that publication right after its listeners.
    [ThreadStatic] private static (Session Owner, List<Action> Work)? publishing;
    // The writer and the Storage jobs on the line read through (Keys.cs, Writer.cs).
    private readonly Writer writer;
    private readonly GatedStorage gated;
    // The record cache under the guard, which the writer applies each batch to at its publication, and the newest entry and head marker per
    // conversation; none for a read-only Session, whose Storage another process writes (CachedStorage.cs, ConversationTails.cs).
    private readonly CachedStorage? cached;
    private readonly ConversationTails? tails;
    // The current documents of each live task this Session created, so the commit that ends a task retires them without a scan (upstream
    // #taskDocuments). An entry starts with its task's creation and goes when the task is terminal: bounded by live tasks. Changed at
    // publication and read on the line, so guarded by taskDocumentsGate.
    private readonly Lock taskDocumentsGate = new();
    private readonly Dictionary<TaskId, Dictionary<DocumentId, DocumentRecord>> taskDocuments = [];
    // Line state, changed on the line only, except where noted: the next job's index, the job on the line, and the fence the next job's
    // outcome waits behind (the delivery of every earlier outcome).
    private long nextIndex;
    private volatile Job? current;
    private Task fence = Task.CompletedTask;
    // Jobs that started on the line and have no outcome delivered yet (MaxOutstanding), and the wait for room. Guarded by outcomesGate,
    // as deliveries run off the line.
    private readonly Lock outcomesGate = new();
    private int outstanding;
    private TaskCompletionSource? room;
    private long lastDelivered = -1;
    // The line index whose outcome the failure is: every later job's outcome is SessionFailed, except ownOutcomes. Guarded by closingGate.
    private long? failAt;
    private readonly HashSet<long> ownOutcomes = [];

    /// <summary>Jobs admitted to the line whose outcome is not delivered yet, beyond which the line waits (upstream <c>MAX_OUTSTANDING</c>):
    /// a count, not bytes.</summary>
    internal const int MaxOutstanding = 256;

    /// <summary>Opens a Session kernel over <paramref name="storage"/>; <paramref name="now"/> is the wall clock for task times in milliseconds
    /// (default: the system clock); <paramref name="documentRetentionMs"/> is read at each use (null: the default). A
    /// <paramref name="readOnly"/> Session refuses every commit with writes before it reaches Storage. <paramref name="setTimer"/> arms the
    /// timers that evict idle documents and store held commits; default the timers of <paramref name="timeProvider"/>, or of the system.
    /// <paramref name="persistWithinMs"/> is the default window of a deferred commit, read at each use (null: 1,000 ms).</summary>
    internal Session(IStorage storage, Func<double>? now = null, Func<double?>? documentRetentionMs = null, bool readOnly = false,
        SetTimer? setTimer = null, TimeProvider? timeProvider = null, Func<double?>? persistWithinMs = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        this.readOnly = readOnly;
        // Waits race it; none has to.
        _ = failedSource.Task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // A read-only Session shows a Storage another process writes, so nothing it reads stays current.
        cached = readOnly ? null : new CachedStorage(storage);
        tails = readOnly ? null : new ConversationTails();
        guard = new StorageGuard(cached ?? storage, this);
        this.storage = new GuardedStorage(guard, null);
        gated = new GatedStorage(this.storage, Touch);
        writer = new Writer(new WriterHost(this));
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
        var within = persistWithinMs ?? (() => null);
        this.persistWithinMs = () =>
        {
            try { return PersistWithin(within()); }
            catch (Exception) { return PersistWithin(null); }
        };
        timers = new HostTimers(setTimer, timeProvider is null ? NativeTimer.System : NativeTimer.Of(timeProvider),
            error => Report(error, ReportInfo.HostError(HostCallback.SetTimer)));
        host = new Host(this);
    }

    /// <summary>Opens a Session kernel over one storage backend (upstream <c>createSession</c>). <paramref name="now"/> is the wall clock for
    /// task times in milliseconds; default the system clock. <paramref name="documentRetentionMs"/> is how long an unobserved document stays
    /// cached after its last use (see <see cref="HarnessSettings.DocumentRetentionMs"/>); default ten minutes. <paramref name="setTimer"/> arms
    /// the timers that evict idle documents; default .NET timers (see <see cref="HarnessOptions.SetTimer"/>). The first exception a Storage
    /// method throws fails the Session (<see cref="SessionFailedException"/>). <paramref name="persistWithinMs"/> is the default
    /// <see cref="CommitPersistence.WithinMs"/> of a deferred commit; default 1,000 ms.</summary>
    public static ISession Create(IStorage storage, Func<double>? now = null, double? documentRetentionMs = null, SetTimer? setTimer = null,
        double? persistWithinMs = null) =>
        new Session(storage, now, documentRetentionMs is { } retention ? () => retention : null, setTimer: setTimer,
            persistWithinMs: () => persistWithinMs);

    /// <summary>Internal, for tests: the retention of every Session that keeps the default, as a suite run with eviction at once (upstream
    /// <c>setDefaultDocumentRetention</c>).</summary>
    internal static void SetDefaultDocumentRetention(double ms) => Volatile.Write(ref defaultRetention, ms);

    /// <summary>A retention as given, when it is a number of milliseconds, 0 or more (<see cref="double.PositiveInfinity"/> keeps documents);
    /// otherwise the default (upstream <c>documentRetention</c>).</summary>
    internal static double DocumentRetention(double? ms) => ms is { } value && value >= 0 ? value : Volatile.Read(ref defaultRetention);

    /// <summary>Default <see cref="CommitPersistence.WithinMs"/> of a deferred commit, <see cref="ProgressSettings.PersistWithinMs"/>.</summary>
    private const double DefaultPersistWithinMs = 1_000;

    /// <summary>A deferred commit's default window as given, when it is a number of milliseconds, 0 or more
    /// (<see cref="double.PositiveInfinity"/> waits for a reading or writing commit or close); otherwise the default (upstream
    /// <c>persistWithin</c>).</summary>
    internal static double PersistWithin(double? ms) => ms is { } value && value >= 0 ? value : DefaultPersistWithinMs;

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
    internal void Fail(Exception error) => Fail(error, null);

    /// <summary><see cref="Fail(Exception)"/>, reported with the context of the operation that failed, when there is one.</summary>
    internal void Fail(Exception error, DurableContext? context) => Fail(error, context, null, null);

    /// <summary>
    /// <see cref="Fail(Exception, DurableContext?)"/> with the line index <paramref name="at"/> of the job whose outcome the failure is, by
    /// default the job on the line (else the last delivered one): later jobs' outcomes become SessionFailed, except <paramref name="own"/>,
    /// whose outcome is still their own error (upstream <c>fail(error, context, at, own)</c>).
    /// </summary>
    internal void Fail(Exception error, DurableContext? context, long? at, IReadOnlyCollection<long>? own)
    {
        if (!TrySetFailure(error, at, own)) return;
        failedSource.TrySetException(new SessionFailedException(error));
        // Seal first, so a report handler that calls back finds the Session failed. Closing runs the close listeners synchronously; a
        // failing backend close is the caller's to see, and here nobody waits.
        var finishClose = BeginClose(DurableContext.Background);
        _ = closing!.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Report(error, ReportInfo.StorageFailed, context);
        // Upstream's next microtask: the join and the closing line job follow the listeners and the report. They start on the thread pool,
        // not inside the call that failed, which may be a line job or a Storage call.
        if (finishClose is not null) ThreadPool.UnsafeQueueUserWorkItem(static finish => finish(), finishClose, preferLocal: false);
    }

    private bool TrySetFailure(Exception error, long? at = null, IReadOnlyCollection<long>? own = null)
    {
        lock (closingGate)
        {
            if (failure is not null) return false;
            // The job that caused it keeps its own outcome; without one, every outcome not delivered yet is SessionFailed.
            long lastDeliveredIndex;
            lock (outcomesGate) lastDeliveredIndex = lastDelivered;
            failAt = at ?? current?.Index ?? lastDeliveredIndex;
            if (own is not null) ownOutcomes.UnionWith(own);
            failure = new FailureBox(error);
            return true;
        }
    }

    /// <summary>Whether job <paramref name="index"/> comes after the failure's boundary without an outcome of its own: it gets SessionFailed
    /// (upstream <c>#poisoned</c>).</summary>
    private bool Poisoned(long index)
    {
        lock (closingGate) return failAt is { } at && index > at && !ownOutcomes.Contains(index);
    }

    /// <summary>Internal: errors of listeners, which never fail what ran them, reported as <c>listener_error</c>.</summary>
    internal void Report(Exception error) => Report(error, ReportInfo.ListenerError);

    /// <summary>Internal: the Session's failure, and errors that never fail what ran them, with what they are about and the context of the
    /// operation they came from, if any (upstream <c>report(error, info, context)</c>). A plain Session drops them.</summary>
    internal virtual void Report(Exception error, ReportInfo info, DurableContext? context = null) { }

    // ─── Commits ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(tx => change(tx), context, null, options);
    }

    /// <inheritdoc/>
    public Task CommitAsync(Func<ITx, Task> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(async tx =>
        {
            await change(tx).ConfigureAwait(false);
            return true;
        }, context, null, options);
    }

    /// <inheritdoc/>
    public Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(tx => Task.FromResult(change(tx)), context, null, options);
    }

    /// <inheritdoc/>
    public Task CommitAsync(Action<ITx> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CommitWithAsync(tx =>
        {
            change(tx);
            return Task.FromResult(true);
        }, context, null, options);
    }

    /// <summary>
    /// Internal commit exposing the concrete transaction and its internal operations, such as the reserved-ID root bootstrap and task
    /// replacement (upstream <c>commitWith</c>). <paramref name="scope"/> sets the default <c>CreateTaskAsync</c> conversation and the task
    /// attributed to appended entries. <paramref name="options"/> may defer storing it (<see cref="CommitPersistence"/>).
    /// </summary>
    internal Task<T> CommitWithAsync<T>(Func<Transaction, Task<T>> change, DurableContext context, TransactionScope? scope = null,
        CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);
        double? withinMs;
        try
        {
            AssertUsable();
            withinMs = Deferral(options?.Persistence);
        }
        catch (Exception error) { return Task.FromException<T>(error); }
        return Enqueue(index => RunCommitAsync(change, context, scope, withinMs, index));
    }

    /// <summary>
    /// Internal: <see cref="CommitWithAsync{T}"/> for a commit of the scheduler's own whose outcome goes only to scheduler code (upstream
    /// <c>commitInternal</c>). When it writes nothing and waited at no gate, it is equivalent to having run before every unpublished commit, so
    /// its outcome is delivered at once, ahead of theirs. One that writes is never exempt: its outcome waits for its own publication and every
    /// earlier outcome, so no invocation starts on a reservation that is not durable. Without <paramref name="inline"/>, a job enqueued on an
    /// idle line starts on the thread pool instead of in the caller: the scheduler enqueues one from inside a publication, which a job must
    /// not run in.
    /// </summary>
    internal Task<T> CommitInternalAsync<T>(Func<Transaction, Task<T>> change, DurableContext context, bool inline = true)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);
        try { AssertUsable(); }
        catch (Exception error) { return Task.FromException<T>(error); }
        return Enqueue(index => RunCommitAsync(change, context, null, null, index), exempt: true, inline: inline);
    }

    /// <summary>
    /// Internal, on the line: calls <paramref name="end"/> once the outcome of the job on the line settles, its commit published or never to
    /// be; at once off the line (upstream <c>onJobEnd</c>).
    /// </summary>
    internal void OnJobEnd(Action end)
    {
        ArgumentNullException.ThrowIfNull(end);
        if (current is not { } job)
        {
            end();
            return;
        }
        lock (job) (job.Ends ??= []).Add(end);
    }

    /// <summary>Internal: the Storage jobs on the line read through (upstream <c>lineStorage</c>); off the line, use
    /// <see cref="GuardedStorage"/>.</summary>
    internal IStorage LineStorage => gated;

    /// <summary>
    /// Internal, on the line: waits, while <paramref name="key"/> has an unpublished writer (for <see cref="LineKeys.All"/>, while anything is
    /// unpublished), until it is published, then checks that the Session is healthy; null when there is nothing to wait for (upstream
    /// <c>touch</c>). A job's every read touches its key first, so it never observes state an earlier job admitted and the writer has not
    /// published.
    /// </summary>
    internal Task? Touch(string key)
    {
        var gate = key == LineKeys.All ? writer.Drained() : writer.Gate(key);
        if (gate is null) return null;
        if (current is { } job) job.Hit = true;
        Interlocked.Increment(ref gateHits);
        return AfterGateAsync(gate);
    }

    private int gateHits;

    /// <summary>Internal, for tests: how many touches so far found an unpublished writer and waited for it. A test that holds a commit in
    /// flight waits for this count to grow to know that a job is at a gate, where upstream's tests let a macrotask pass.</summary>
    internal int GateHits => Volatile.Read(ref gateHits);

    private async Task AfterGateAsync(Task gate)
    {
        await gate.ConfigureAwait(false);
        AssertHealthy();
    }

    /// <summary>On the line: waits for what an unpublished commit wrote of a conversation's entries, its appends and its creation, as reading
    /// them from Storage fails for a conversation not stored yet (upstream <c>#touchEntries</c>).</summary>
    private Task? TouchEntries(ConversationId conversationId)
    {
        var created = Touch(LineKeys.Conversation(conversationId));
        var appended = Touch(LineKeys.Append(conversationId));
        if (created is null) return appended;
        return appended is null ? created : Task.WhenAll(created, appended);
    }

    /// <summary>Internal, on the Session line: the newest entry ID visible from <paramref name="conversationId"/> (upstream
    /// <c>latestEntryId</c>). Known from the Session's own commits when it owns its Storage (ConversationTails), so it costs no Storage read
    /// after the first.</summary>
    internal async Task<EntryId?> LatestEntryIdAsync(ConversationId conversationId, DurableContext context)
    {
        if (TouchEntries(conversationId) is { } gate) await gate.ConfigureAwait(false);
        async Task<EntryId?> Read()
        {
            var page = await storage.ScanEntriesAsync(new EntryQuery(conversationId), 1, null, context).ConfigureAwait(false);
            return page.Items is [var newest, ..] ? newest.Id : null;
        }
        return tails is null ? await Read().ConfigureAwait(false) : await tails.LatestAsync(conversationId, Read).ConfigureAwait(false);
    }

    /// <summary>Internal: the newest entry ID of <paramref name="conversationId"/> as known from the Session's commits right now, without
    /// reading (upstream <c>knownLatestEntryId</c>): false when not known, and always for a read-only Session; else
    /// <paramref name="newest"/> is it, null for none.</summary>
    internal bool KnownLatestEntryId(ConversationId conversationId, out EntryId? newest)
    {
        if (tails is not null) return tails.TryPeek(conversationId, out newest);
        newest = null;
        return false;
    }

    /// <summary>Internal, on the Session line: the newest visible head marker of <paramref name="conversationId"/> at or before
    /// <paramref name="atOrBefore"/>, any when null (upstream <c>latestHeadMarker</c>); known from the Session's own commits as
    /// <see cref="LatestEntryIdAsync"/> is.</summary>
    internal async Task<EntryRecord?> LatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBefore, DurableContext context)
    {
        if (TouchEntries(conversationId) is { } gate) await gate.ConfigureAwait(false);
        Task<EntryRecord?> Read() => storage.FindLatestHeadMarkerAsync(conversationId, atOrBefore, context);
        return tails is null ? await Read().ConfigureAwait(false) : await tails.MarkerAsync(conversationId, atOrBefore, Read).ConfigureAwait(false);
    }

    /// <summary>
    /// Keys a commit writes besides its records' (<see cref="Transaction.WriteKeys"/>), at its admission, on the line (upstream
    /// <c>membershipKeys</c>): a Harness adds the ownership memberships its task changes write (area group-commit-scheduler). A plain Session
    /// adds none.
    /// </summary>
    internal virtual ValueTask<IReadOnlyList<string>> MembershipKeysAsync(Transaction tx) => new([]);

    /// <summary>Internal, for tests: the writer's wait between two parts of one call (see Writer.cs).</summary>
    internal Func<Writer.Part, Task>? PublicationHop
    {
        get => writer.Hop;
        set => writer.Hop = value;
    }

    /// <summary>Internal, for tests: a fault to throw from the next adoption of a stored commit, or of a held one (upstream's spies on
    /// <c>Transaction.adopt</c> and <c>adoptHeld</c>); each returns the fault to throw, or null.</summary>
    internal Func<Exception?>? AdoptFault { get; set; }

    /// <inheritdoc cref="AdoptFault"/>
    internal Func<Exception?>? AdoptHeldFault { get; set; }

    /// <summary>Internal, for tests: runs when the writer discards the staged changes of a stored commit that will never be published
    /// (upstream's spy on <c>Transaction.discard</c>).</summary>
    internal Action? OnDiscard { get; set; }

    /// <summary>Internal: <see cref="CommitWithAsync{T}"/> for an asynchronous callback without a result.</summary>
    internal Task CommitWithAsync(Func<Transaction, Task> change, DurableContext context, TransactionScope? scope = null,
        CommitOptions? options = null) =>
        CommitWithAsync(async tx =>
        {
            await change(tx).ConfigureAwait(false);
            return true;
        }, context, scope, options);

    /// <summary>Internal: <see cref="CommitWithAsync{T}"/> for a synchronous callback without a result.</summary>
    internal Task CommitWithAsync(Action<Transaction> change, DurableContext context, TransactionScope? scope = null,
        CommitOptions? options = null) =>
        CommitWithAsync(tx =>
        {
            change(tx);
            return Task.FromResult(true);
        }, context, scope, options);

    /// <summary>How long a commit may stay in memory before it is stored; null when it is immediate (upstream <c>#deferral</c>).</summary>
    private double? Deferral(CommitPersistence? persistence)
    {
        if (persistence is not { Kind: CommitPersistenceKind.Deferred }) return null;
        if (persistence.WithinMs is not { } ms) return persistWithinMs();
        // Public options: checked here, as the types cannot (upstream's TypeError).
        if (!(ms >= 0))
            throw new ArgumentOutOfRangeException(nameof(persistence), ms, "CommitPersistence.WithinMs must be a number of milliseconds, 0 or more");
        return ms;
    }

    /// <summary>Internal: whether <paramref name="tx"/> is the transaction of one of this Session's commits.</summary>
    internal bool OwnsTransaction(ITx tx) => Transaction.OwnedBy(tx, host);

    /// <summary>Internal: runs a read-only job on the mutation line so multi-read derivations observe one committed state.</summary>
    internal Task<T> ReadOnLineAsync<T>(Func<Task<T>> job) => ReadOnLineAsync(_ => job());

    /// <summary>Internal: <see cref="ReadOnLineAsync{T}(Func{Task{T}})"/> reading through the line's Storage, whose reads wait for what an
    /// unpublished commit wrote (upstream <c>readOnLine</c>).</summary>
    internal Task<T> ReadOnLineAsync<T>(Func<IStorage, Task<T>> job)
    {
        try { AssertUsable(); }
        catch (Exception error) { return Task.FromException<T>(error); }
        return Enqueue(async _ =>
        {
            AssertHealthy();
            return new Ran<T>(await job(gated).ConfigureAwait(false));
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

    /// <summary>Internal test and Harness barrier: completes once every job enqueued before it has run, every commit they admitted is
    /// published or never will be, and the deliveries those publications scheduled have run. It works on a closed or failed Session.</summary>
    internal Task FlushLineAsync() => Maintain(async _ =>
    {
        await DrainedAsync().ConfigureAwait(false);
        return true;
    });

    /// <summary>Internal test barrier: completes once every job enqueued before it has run its line part (its callback, up to admission),
    /// without waiting for their commits to be stored (upstream's <c>flush()</c>, a macrotask).</summary>
    internal Task LineTickAsync() => Maintain(_ => Task.FromResult(true));

    /// <summary>Internal test barrier: how many jobs wait on the line behind the running one.</summary>
    internal int LineBacklog => line.Waiting;

    /// <summary>Internal, for tests that bound memory: jobs whose outcome is not delivered yet, keys with an unpublished writer, and record
    /// reads in flight in the record cache (CachedStorage). All three are 0 once the line is idle (upstream <c>lineSizes</c>).</summary>
    internal (int Outstanding, int Unpublished, int Reads) LineSizes
    {
        get
        {
            var reads = cached?.ReadsInFlight ?? 0;
            lock (outcomesGate) return (outstanding, writer.Unpublished, reads);
        }
    }

    /// <summary>Internal, for tests: the record cache under the guard (upstream's <c>CachedStorage</c> of this Session); null for a read-only
    /// Session.</summary>
    internal CachedStorage? RecordCache => cached;

    // ─── The line ───────────────────────────────────────────────────────────

    /// <summary>A line job's result: its value, delivered once <see cref="After"/>, the publication of its commit, settles, if it has one
    /// (upstream <c>Ran</c>).</summary>
    private readonly record struct Ran<T>(T Value, Task? After = null);

    /// <summary>A job on the line (upstream <c>Job</c>): its index in line order, whether a touch of it waited for an unpublished writer, and
    /// what runs once its outcome settles (<see cref="OnJobEnd"/>; guarded by the job itself).</summary>
    private sealed class Job(long index)
    {
        public long Index { get; } = index;
        public volatile bool Hit;
        public List<Action>? Ends;
    }

    /// <summary>
    /// How a line result completes its caller's task (see the file header): at once, with RunContinuationsAsynchronously; or, for a flow with
    /// an <see cref="OrderedLineDelivery"/>, through that delivery, in line order, unless it runs inline in the caller outside the delivery's
    /// segments, where nothing orders it.
    /// </summary>
    private sealed class LineResult<T>
    {
        private readonly OrderedLineDelivery? ordered = OrderedLineDelivery.Current;
        private readonly int caller = Environment.CurrentManagedThreadId;

        public LineResult()
        {
            // An ordered result is set by the delivery, which runs its continuation until it next suspends.
            Completion = new TaskCompletionSource<T>(ordered is null ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None);
        }

        public TaskCompletionSource<T> Completion { get; }

        /// <summary>Set while the line runs the job inline in the caller.</summary>
        public StrongBox<bool> InCaller { get; } = new(false);

        public void Complete(T value) => Set(() => Completion.TrySetResult(value));

        public void Fail(Exception error) => Set(() => Completion.TrySetException(error));

        private void Set(Action set)
        {
            if (ordered is null) set();
            else if (Volatile.Read(ref InCaller.Value) && Environment.CurrentManagedThreadId == caller && !ordered.Executing) set();
            else ordered.Post(set);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the line, after the jobs before it, and delivers its outcome in line order: once the jobs before it
    /// have theirs, and once its commit, if it admitted one, is published (upstream <c>#enqueue</c>). With <paramref name="exempt"/>, a job
    /// that admitted no commit and waited at no gate is delivered as soon as it has its outcome. A job whose outcome comes after the one a
    /// failure is gets SessionFailed instead. At most <see cref="MaxOutstanding"/> jobs wait for their delivery; beyond it the line waits.
    /// </summary>
    private Task<T> Enqueue<T>(Func<long, Task<Ran<T>>> work, bool exempt = false, bool inline = true)
    {
        var result = new LineResult<T>();
        async Task Run()
        {
            // A job runs on whatever thread starts or continues the line, possibly inside an ordered flow: what it enqueues is not part of it.
            if (OrderedLineDelivery.Current is not null) OrderedLineDelivery.Current = null;
            await WaitForRoomAsync().ConfigureAwait(false);
            // On the line, so in line order: the index, and the fence the outcome is delivered behind.
            var job = new Job(Interlocked.Increment(ref nextIndex) - 1);
            var previous = fence;
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fence = delivered.Task;
            Ran<T> ran = default;
            Exception? error = null;
            try
            {
                current = job;
                ran = await work(job.Index).ConfigureAwait(false);
            }
            catch (Exception thrown)
            {
                error = thrown;
            }
            finally
            {
                current = null;
            }
            _ = DeliverAsync(job, ran, error, previous, delivered, exempt, result);
        }
        line.Enqueue(Run, inline, result.InCaller);
        return result.Completion.Task;
    }

    /// <summary>Delivers job <paramref name="job"/>'s outcome: after its publication, and, fenced, after every earlier outcome; then opens the
    /// next job's fence. Never faults.</summary>
    private async Task DeliverAsync<T>(Job job, Ran<T> ran, Exception? error, Task previous, TaskCompletionSource delivered, bool exempt,
        LineResult<T> result)
    {
        // Only a job without writes is exempt: one with writes waits for its own publication and the outcomes before it.
        var fenced = !exempt || job.Hit || (error is null && ran.After is not null);
        try
        {
            if (error is null && ran.After is { } after)
            {
                try { await after.ConfigureAwait(false); }
                catch (Exception failed) { error = failed; }
            }
            Ended(job);
            if (fenced) await previous.ConfigureAwait(false);
            Delivered(job.Index, fenced);
            if (Poisoned(job.Index) && failure is { } failed2) error = new SessionFailedException(failed2.Error);
            if (error is null) result.Complete(ran.Value);
            else result.Fail(error);
        }
        catch (Exception unexpected)
        {
            result.Fail(unexpected);
        }
        finally
        {
            if (!fenced) await previous.ConfigureAwait(false);
            delivered.TrySetResult();
        }
    }

    /// <summary>The job's outcome settled: runs what waited for it (<see cref="OnJobEnd"/>).</summary>
    private static void Ended(Job job)
    {
        List<Action>? ends;
        lock (job)
        {
            ends = job.Ends;
            job.Ends = null;
        }
        if (ends is null) return;
        foreach (var end in ends) Run(end);
    }

    /// <summary>Waits, on the line, until fewer than <see cref="MaxOutstanding"/> outcomes are undelivered, and counts this job's.</summary>
    private Task WaitForRoomAsync()
    {
        Task wait;
        lock (outcomesGate)
        {
            if (outstanding < MaxOutstanding)
            {
                outstanding++;
                return Task.CompletedTask;
            }
            room ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = room.Task;
        }
        return WaitThenRetryAsync(wait);
    }

    private async Task WaitThenRetryAsync(Task wait)
    {
        await wait.ConfigureAwait(false);
        await WaitForRoomAsync().ConfigureAwait(false);
    }

    /// <summary>Job <paramref name="index"/> has its outcome: lets the line go on past <see cref="MaxOutstanding"/> (upstream
    /// <c>#delivered</c>).</summary>
    private void Delivered(long index, bool fenced)
    {
        TaskCompletionSource? open = null;
        lock (outcomesGate)
        {
            if (fenced) lastDelivered = index;
            outstanding--;
            if (room is not null && outstanding < MaxOutstanding)
            {
                open = room;
                room = null;
            }
        }
        open?.TrySetResult();
    }

    /// <summary>Runs a job of the Session's own upkeep on the line: no outcome to deliver, so no fence and no SessionFailed (upstream
    /// <c>#maintain</c>).</summary>
    private Task<T> Maintain<T>(Func<long, Task<T>> work, bool inline = true)
    {
        var result = new LineResult<T>();
        async Task Run()
        {
            if (OrderedLineDelivery.Current is not null) OrderedLineDelivery.Current = null;
            var job = new Job(Interlocked.Increment(ref nextIndex) - 1);
            T value;
            try
            {
                current = job;
                value = await work(job.Index).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                current = null;
                result.Fail(error);
                return;
            }
            current = null;
            result.Complete(value);
        }
        line.Enqueue(Run, inline, result.InCaller);
        return result.Completion.Task;
    }

    /// <summary>A <see cref="LineKeys.All"/> barrier: once every part admitted so far is published, or never will be.</summary>
    private Task DrainedAsync() => writer.Drained() ?? Task.CompletedTask;

    /// <summary>
    /// Runs one commit's publication inside the Storage's own scope (<see cref="IStorage.Publishing"/>), such as a store host's batch; null
    /// when it ran at once, else completes once it ran (upstream <c>#publishing</c>). A Storage that throws instead of running it is
    /// reported, and the publication runs directly: the commit is stored, so it must be published.
    /// </summary>
    private Task? PublishingScope(Action publish, DurableContext context)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = 0;
        void Once()
        {
            if (Interlocked.Exchange(ref ran, 1) != 0) return;
            try { publish(); }
            finally { done.TrySetResult(); }
        }
        try
        {
            storage.Publishing(Once, context);
        }
        catch (Exception error)
        {
            Report(error, ReportInfo.ListenerError, context);
            Once();
        }
        return done.Task.IsCompleted ? null : done.Task;
    }

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
            : await Enqueue(async _ =>
            {
                AssertHealthy();
                return new Ran<LoadedDocument?>(await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false));
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
        return await Enqueue(async _ =>
        {
            AssertHealthy();
            var storedEntry = await gated.GetEntryAsync(conversationId, at, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Entry {at} is not visible from conversation {conversationId}");
            var address = resolved.Address with { Scope = DocumentScope.Conversation(storedEntry.Entry.ConversationId) };
            var record = await gated.FindDocumentAsync(address, storedEntry.CommitSeq, context).ConfigureAwait(false);
            if (record is null) return new Ran<JsonObjectValue?>(null);
            var stored = await gated.GetDocumentAsync(record.Id, storedEntry.CommitSeq, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Historical document {record.Id} ({record.Kind}) cannot be read");
            return new Ran<JsonObjectValue?>(Documents.MaterializeDocument(definition, stored));
        }).ConfigureAwait(false);
    }

    // ─── Watches ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocToken token, DurableContext context) => WatchDoc(token, null, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        WatchDoc(token, conversationId.Value, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        WatchDoc(token, taskId.Value, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        WatchDoc(token, null, key, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context) => WatchDoc(token, conversationId.Value, key, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        WatchDoc(token, taskId.Value, key, context);

    private async Task<IWatchHandle<JsonObjectValue>?> WatchDoc(DocumentToken token, long? owner, string? key, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, owner, key);
        var watch = await AttachDocumentAsync(definition, resolved, context,
            loaded => ObserveDocument(definition, loaded, (value, detach, report) =>
                new WatchObserver<JsonObjectValue>(new Watch<JsonObjectValue>(value, detach, report, schedule: Defer))).Watch,
            static watch => watch.Abort()).ConfigureAwait(false);
        if (watch is not null && context.HasCancellation) watch.ObserveAbort(context.CancellationToken);
        return watch;
    }

    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(SessionDocToken token, Observer<JsonObjectValue> observer, DurableContext context) =>
        ObserveDoc(token, null, null, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(ConversationDocToken token, ConversationId conversationId,
        Observer<JsonObjectValue> observer, DurableContext context) => ObserveDoc(token, conversationId.Value, null, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(TaskDocToken token, TaskId taskId, Observer<JsonObjectValue> observer,
        DurableContext context) => ObserveDoc(token, taskId.Value, null, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(SessionDocFamilyToken token, string key, Observer<JsonObjectValue> observer,
        DurableContext context) => ObserveDoc(token, null, key, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        Observer<JsonObjectValue> observer, DurableContext context) => ObserveDoc(token, conversationId.Value, key, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(TaskDocFamilyToken token, TaskId taskId, string key,
        Observer<JsonObjectValue> observer, DurableContext context) => ObserveDoc(token, taskId.Value, key, observer, context);

    /// <summary>Upstream <c>observeDoc</c>: asynchronous, so a throw here faults the task, as upstream's rejects.</summary>
    private async Task<Observation<JsonObjectValue>?> ObserveDoc(DocumentToken token, long? owner, string? key, Observer<JsonObjectValue> observer,
        DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, owner, key);
        return await AttachDocumentAsync(definition, resolved, context, loaded =>
        {
            var guarded = ObserveDocument(definition, loaded, (_, detach, report) =>
                new GuardedObserver<JsonObjectValue>(observer, detach, (error, _) => report(error)));
            return new Observation<JsonObjectValue>(loaded.Value, guarded);
        }, static attached => attached.Detach()).ConfigureAwait(false);
    }

    /// <summary>
    /// On the line, as one job: loads the document at <paramref name="resolved"/> and attaches to it with <paramref name="attach"/>, so the
    /// attachment sees every commit after the value it starts from; null when absent (upstream <c>#attachDocument</c>). Cancelling
    /// <paramref name="context"/> meanwhile faults the task with the cancellation error, after <paramref name="cancel"/> undid an attachment
    /// made.
    /// </summary>
    private async Task<TAttached?> AttachDocumentAsync<TAttached>(DocDefinition definition, ResolvedAddress resolved, DurableContext context,
        Func<LoadedDocument, TAttached> attach, Action<TAttached> cancel) where TAttached : class
    {
        var flag = new StrongBox<int>(context.IsCancellationRequested ? 1 : 0);
        var registration = context.CancellationToken.UnsafeRegister(static state => Volatile.Write(ref ((StrongBox<int>)state!).Value, 1), flag);
        try
        {
            var attached = await Enqueue(async _ =>
            {
                AssertHealthy();
                if (Volatile.Read(ref flag.Value) != 0) context.ThrowIfCancellationRequested();
                var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false);
                if (Volatile.Read(ref flag.Value) != 0) context.ThrowIfCancellationRequested();
                return new Ran<TAttached?>(loaded is null ? null : attach(loaded));
            }).ConfigureAwait(false);
            if (attached is null) return null;
            if (Volatile.Read(ref flag.Value) != 0)
            {
                cancel(attached);
                context.ThrowIfCancellationRequested();
            }
            return attached;
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
            await Maintain(async _ =>
            {
                // After every commit admitted before the seal is published or never will be.
                await DrainedAsync().ConfigureAwait(false);
                // Last, after the writes at close (BeforeCloseAsync), while the trackers are still here. A failed Session writes nothing; a
                // failing store fails it, and the backend still closes.
                ExceptionDispatchInfo? unstored = null;
                // Only with something to store: otherwise the backend's close begins in this very job.
                if (failure is null && HoldCount > 0)
                {
                    try { await StoreHoldsAsync(HeldDocuments, cleanup).ConfigureAwait(false); }
                    catch (Exception error) { unstored = ExceptionDispatchInfo.Capture(error); }
                }
                ObserverGuard<CommitPublication>[] observers;
                lock (listenersGate)
                {
                    observers = [.. commitListeners];
                    commitListeners.Clear();
                    internalListeners.Clear();
                }
                // After the last publication: the commits admitted before the seal have settled, held changes are stored. Commit observers
                // are synchronous observers, all of them (CloseEnds).
                var end = CloseEnds.Of(failure?.Error).Observer;
                foreach (var observer in observers) observer.End(end);
                lock (documentsGate)
                {
                    documents.Clear();
                    idleDocuments.Clear();
                    idleIndex.Clear();
                    attachedDocuments.Clear();
                }
                lock (holdsGate) holds.Clear();
                lock (taskDocumentsGate) taskDocuments.Clear();
                try
                {
                    await storage.CloseAsync(cleanup).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // A Storage that cannot close is a failed one; an earlier failure stays the cause.
                    if (TrySetFailure(error)) Report(error, ReportInfo.StorageFailed);
                    throw;
                }
                unstored?.Throw();
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
        Action? disarm;
        lock (documentsGate)
        {
            disarm = documentSweep?.Disarm;
            documentSweep = null;
        }
        if (disarm is not null) Contained(disarm, SetTimerError);
        Action? disarmHolds;
        lock (holdsGate)
        {
            disarmHolds = holdTimer?.Disarm;
            holdTimer = null;
        }
        if (disarmHolds is not null) Contained(disarmHolds, SetTimerError);
        foreach (var listener in listeners) Contained(listener, Report);
    }

    /// <summary>
    /// Runs inside every transaction that creates or forks a conversation, after the conversation record is staged. A plain Session stages
    /// nothing; a Harness stages its built-in documents.
    /// </summary>
    internal virtual Task ConversationCreatedAsync(Transaction tx, ConversationRecord record) => Task.CompletedTask;

    /// <summary>Runs after the close listeners (and, for a failure, its report) and before the line closes Storage; must not fail.</summary>
    internal virtual Task BeforeCloseAsync() => Task.CompletedTask;

    /// <summary>
    /// Internal, for <see cref="BeforeCloseAsync"/>: on the line, after every commit admitted before the seal is published or never will be,
    /// writes what <paramref name="writes"/> returns then to Storage, in one batch, through the writer (upstream <c>writeAtClose</c>). Nothing
    /// is published, since the Session is closing; a failed Session writes nothing. A Storage that throws or refuses the batch fails the
    /// Session, and the returned task faults with that error.
    /// </summary>
    internal Task WriteAtCloseAsync(Func<IReadOnlyList<StorageWrite>> writes) => Maintain(async index =>
    {
        await DrainedAsync().ConfigureAwait(false);
        if (failure is not null) return true;
        var batch = writes();
        if (batch.Count == 0) return true;
        // Through the writer, as its own part: a refusal or a rejection fails the Session there, and is this task's error.
        await writer.Admit(new PartSpec(index, batch, [], DurableContext.Background, static (_, _) => null, static () => { })).Outcome
            .ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc/>
    public CommitObservation ObserveCommits(CommitObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        AssertUsable();
        ObserverGuard<CommitPublication>? guarded = null;
        guarded = new ObserverGuard<CommitPublication>(observer.Advance, observer.End, () =>
        {
            lock (listenersGate) commitListeners.Remove(guarded!);
        }, (error, context) => Report(error, ReportInfo.ListenerError, context));
        lock (listenersGate) commitListeners.Add(guarded);
        return new CommitObservation(guarded);
    }

    /// <summary>
    /// Internal: follows every commit as memory adopts it (<see cref="ObservedCommit"/>), for the Session's own components, whose state
    /// follows memory (upstream <c>followCommits</c>). A throw leaves that state behind, so it fails the Session instead of being reported.
    /// They run before host observers.
    /// </summary>
    internal IDisposable FollowCommits(Action<ObservedCommit, DurableContext> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        AssertUsable();
        // A distinct wrapper per subscription, so two subscriptions of one delegate are independent and removal is exact.
        Action<ObservedCommit, DurableContext> entry = (publication, context) => listener(publication, context);
        lock (listenersGate) internalListeners.Add(entry);
        return new Unsubscriber(() =>
        {
            lock (listenersGate) internalListeners.Remove(entry);
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

    /// <summary>Internal: drops every loaded tracker on the mutation line, held changes stored first; later access cold-loads from
    /// Storage.</summary>
    internal Task UnloadDocumentsAsync() => Maintain(async _ =>
    {
        if (failure is null) await StoreHoldsAsync(HeldDocuments, DurableContext.Background).ConfigureAwait(false);
        lock (documentsGate)
        {
            documents.Clear();
            idleDocuments.Clear();
            idleIndex.Clear();
        }
        return true;
    });

    /// <summary>Internal, for tests: how many documents the cache holds, how many are idle, how many incarnations are pinned, how many
    /// sweep jobs were queued on the line so far, and how many documents have held changes (upstream <c>documentCacheSizes</c>).</summary>
    internal (int Cached, int Idle, int Attached, int Sweeps, int Held) DocumentCacheSizes
    {
        get
        {
            int held;
            lock (holdsGate) held = holds.Count;
            lock (documentsGate) return (documents.Count, idleDocuments.Count, attachedDocuments.Count, sweepJobs, held);
        }
    }

    /// <summary>Internal, for tests: how many live tasks have their documents indexed, and how many conversations have their newest entry
    /// kept (upstream <c>documentCacheSizes</c>' <c>taskDocuments</c> and <c>tails</c>; apart from <see cref="DocumentCacheSizes"/>, whose
    /// shape a benchmark reads by reflection).</summary>
    internal (int TaskDocuments, int Tails) IndexSizes
    {
        get
        {
            lock (taskDocumentsGate) return (taskDocuments.Count, tails?.Count ?? 0);
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
    /// Schedules the eviction of the oldest idle document: on the line at once when it is due, otherwise when it expires, by a background
    /// timer (<see cref="HarnessOptions.SetTimer"/>), which never keeps the process alive. Where the host declines it, none is kept: the end
    /// of each commit evicts what expired.
    /// </summary>
    private void ScheduleDocumentSweep()
    {
        var current = clock();
        var retention = documentRetentionMs();
        DocumentSweep sweep;
        Action? replaced;
        bool arm;
        double delay;
        lock (documentsGate)
        {
            if (closingStarted) return;
            // Documents with unpublished changes are not swept: their publication touches them again.
            var oldest = idleDocuments.First;
            while (oldest is not null && writer.Gate(LineKeys.Document(oldest.Value.AddressId)) is not null) oldest = oldest.Next;
            if (oldest is null) return;
            var at = oldest.Value.Since + retention;
            if (documentSweep is { } pending && pending.At <= at) return;
            replaced = documentSweep?.Disarm;
            // The pending sweep stays set until its job runs, so touches while the line is busy queue no more.
            sweep = new DocumentSweep(at);
            documentSweep = sweep;
            delay = at - current;
            arm = false;
            if (delay <= 0) QueueDocumentSweepLocked(sweep);
            else if (documentTimersUnavailable) documentSweep = null;
            else arm = true;
        }
        if (replaced is not null) Contained(replaced, SetTimerError);
        if (!arm) return;
        // A timer that fires early sweeps nothing and arms the next one.
        var disarm = timers.Background(() =>
        {
            lock (documentsGate) QueueDocumentSweepLocked(sweep);
        }, delay);
        lock (documentsGate)
        {
            if (disarm is null)
            {
                documentTimersUnavailable = true;
                if (ReferenceEquals(documentSweep, sweep)) documentSweep = null;
                return;
            }
            if (ReferenceEquals(documentSweep, sweep) && !closingStarted)
            {
                sweep.Disarm = disarm;
                return;
            }
        }
        // Replaced, swept or closed while it was armed.
        Contained(disarm, SetTimerError);
    }

    /// <summary>Reports a host timer's throw, such as its disarm's.</summary>
    private void SetTimerError(Exception error) => Report(error, ReportInfo.HostError(HostCallback.SetTimer));

    /// <summary>Queues the sweep job on the line; called under <see cref="documentsGate"/>, and never runs the job inline.</summary>
    private void QueueDocumentSweepLocked(DocumentSweep sweep)
    {
        sweepJobs++;
        _ = Maintain(_ =>
        {
            bool sweepNow;
            lock (documentsGate)
            {
                if (ReferenceEquals(documentSweep, sweep)) documentSweep = null;
                sweepNow = !closingStarted;
            }
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
            for (var node = idleDocuments.First; node is not null && node.Value.Since <= expired;)
            {
                var next = node.Next;
                // A document with an unpublished change stays until its publication touches it.
                if (writer.Gate(LineKeys.Document(node.Value.AddressId)) is null) DropDocumentLocked(node.Value.AddressId);
                node = next;
            }
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

    private async Task<Ran<T>> RunCommitAsync<T>(Func<Transaction, Task<T>> change, DurableContext context, TransactionScope? scope,
        double? withinMs, long index)
    {
        AssertHealthy();
        // Cancellation is checked when the job starts (changelog 1.5.0, Changed#7): a job may start while earlier commits are stored.
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
            return new Ran<T>(result);
        }
        if (readOnly)
        {
            // Refused before Storage: the Session stays healthy, and the caller sees why.
            tx.Discard();
            SweepDocuments();
            throw new ReadOnlySessionException("a commit with writes");
        }
        if (withinMs is { } within && tx.Holdable() is { } held)
        {
            // Adopted in memory when the commits before it are published, as a part of no storage call.
            var heldPart = writer.Admit(new PartSpec(index, null, tx.WriteKeys(), context,
                (_, unregister) => HoldCommit(tx, held, within, context, unregister), tx.Discard));
            SweepDocuments();
            return new Ran<T>(result, heldPart.Outcome);
        }
        IReadOnlyList<string> membership;
        try
        {
            membership = await MembershipKeysAsync(tx).ConfigureAwait(false);
        }
        catch
        {
            tx.Discard();
            SweepDocuments();
            throw;
        }
        // Logical admission. Held changes leave their queue only here, once nothing but Storage can fail; a commit never stores a hold adopted
        // after it in line order. Taken before the keys, so the documents it stores held changes of are its writes too: a later job that reads
        // one waits for it instead of storing them again.
        List<Hold> taken;
        lock (holdsGate) taken = holds.Count == 0 ? NoHolds : tx.TakeHeld<Hold>(id => holds.GetValueOrDefault(id));
        var keys = tx.WriteKeys();
        keys.AddRange(membership);
        var part = writer.Admit(new PartSpec(index, writes, keys, context,
            (seq, unregister) => AdoptCommit(tx, writes, taken, seq!.Value, context, unregister), () =>
            {
                OnDiscard?.Invoke();
                tx.Discard();
            }));
        SweepDocuments();
        return new Ran<T>(result, part.Outcome);
    }

    /// <summary>
    /// Publishes a stored commit (Writer.cs; upstream <c>#adopt</c>): adopts it in memory, drops its keys, and runs its publication. A throw
    /// of the adoption leaves memory behind durable state; the writer fails the Session with it.
    /// </summary>
    private PublishFailure? AdoptCommit(Transaction tx, IReadOnlyList<StorageWrite> writes, List<Hold> taken, Seq seq, DurableContext context,
        Action unregister)
    {
        AdoptedDocuments documentChanges;
        try
        {
            if (AdoptFault?.Invoke() is { } fault) throw fault;
            documentChanges = tx.Adopt(seq);
        }
        catch (Exception error)
        {
            return PublishFailure.OfAdoption(error);
        }
        // Before the keys leave, as a task's documents are read on the line once its scope's key has no unpublished writer.
        IndexTaskDocuments(tx, writes, documentChanges, seq);
        // Before the keys leave: a job that then touches a document whose held changes this commit stored must not find them held.
        if (taken.Count > 0) Release(taken);
        var failure = Publish(seq, writes, documentChanges, context, unregister);
        foreach (var adopted in documentChanges.Stored)
        {
            var record = adopted switch
            {
                DocumentCommitChange document => document.Record,
                DocumentCopyCommitChange copy => copy.Record,
                _ => null,
            };
            if (record is not null) TouchDocument(Documents.AddressId(DocumentAddress.Of(record)));
        }
        return failure is null ? null : PublishFailure.OfListener(failure);
    }

    /// <summary>
    /// At publication: keeps the task-document index in step with a stored commit (upstream <c>#indexTaskDocuments</c>): the tasks it creates
    /// start with no documents, task documents it creates join their task, ones it retires leave, and tasks it ends leave the index.
    /// </summary>
    private void IndexTaskDocuments(Transaction tx, IReadOnlyList<StorageWrite> writes, AdoptedDocuments documentChanges, Seq seq)
    {
        var created = tx.CreatedTaskIds();
        lock (taskDocumentsGate)
        {
            foreach (var id in created) taskDocuments[id] = [];
            if (taskDocuments.Count == 0) return;
            foreach (var change in documentChanges.Stored)
            {
                if (change is not DocumentCommitChange { Record: { Scope.TaskId: { } taskId } record } document) continue;
                if (!taskDocuments.TryGetValue(taskId, out var known)) continue;
                if (document.Kind == DocumentCommitChangeKind.Retire) known.Remove(record.Id);
                else if (record.CreatedAt == seq) known[record.Id] = record;
            }
            foreach (var write in writes)
                if (write is TaskWrite { Value: { State.Status: DurableTaskStatus.Terminal } ended }) taskDocuments.Remove(ended.Id);
        }
    }

    /// <summary>
    /// At publication: publishes a stored commit (upstream <c>#publish</c>). Its telemetry events first, listeners or not, then the Session's
    /// own observers, then the dispatch to host observers, then the deliveries, waits and abort signals they scheduled (<see cref="Defer"/>,
    /// <see cref="AfterPublication"/>). Upstream queues the dispatch a microtask ahead of the waits and aborts its own observers wake, and
    /// only when a host observer is attached at the publication; the dispatch calls the observers attached when it runs. Here it runs right
    /// after the own observers, under the same rule: none when no host observer was attached at the publication, and the observers attached
    /// by then otherwise (one an own observer attached or detached is in or out). Never throws: returns the first error of an own observer,
    /// which fails the Session once its storage call settled, after the hosts got the commit. <paramref name="unregister"/> drops the
    /// commit's keys once the Session's own observers adopted it, before host observers run: upstream drops them before the publication, as
    /// nothing else runs until it returns, but here a job on another thread that touched one in between would read scheduler memory that has
    /// not adopted the commit yet.
    /// </summary>
    private Exception? Publish(Seq seq, IReadOnlyList<StorageWrite> writes, AdoptedDocuments documentChanges, DurableContext context,
        Action unregister)
    {
        // Its telemetry events first, listeners or not.
        foreach (var write in writes)
        {
            if (write is EntryWrite entry)
                Event("pi.durable.entry", new SpanAttributes { ["entry.id"] = entry.Value.Id.Value, ["entry.kind"] = entry.Value.Kind }, context);
            else if (write is SubmissionWrite { Value: { Status: SubmissionStatus.Done or SubmissionStatus.Unanswered } submission })
                Event("pi.durable.submission", new SpanAttributes
                {
                    ["submission.id"] = submission.Id.Value,
                    ["status"] = WireName.Of(submission.Status),
                    ["answer.entry_id"] = submission.Answer?.Value,
                }, context);
        }
        Action<ObservedCommit, DurableContext>[] internals;
        bool hosts;
        lock (listenersGate)
        {
            internals = [.. internalListeners];
            hosts = commitListeners.Count > 0;
        }
        if (internals.Length == 0 && !hosts)
        {
            unregister();
            return null;
        }
        var changes = new List<ICommitChange>();
        foreach (var write in writes)
            if (write is ICommitChange change) changes.Add(change);
        var tables = changes.Count;
        changes.AddRange(documentChanges.Observed);
        var observed = ObservedCommit.Stored(seq, changes);
        // One list for both, unless the commit also stored held changes, which only durable listeners get.
        var stored = ReferenceEquals(documentChanges.Stored, documentChanges.Observed)
            ? new CommitPublication(seq, changes)
            : new CommitPublication(seq, [.. changes.Take(tables), .. documentChanges.Stored]);
        Exception? failure = null;
        RunPublishing(() =>
        {
            // The commit is durable: a failing listener neither fails the commit nor skips the others. A host observer's guard reports its
            // throw, detaches it and ends it listener_error.
            foreach (var listener in internals) Contained(() => listener(observed, context), error => failure ??= error);
            unregister();
            if (hosts) NotifyHosts(stored, context);
        });
        return failure;
    }

    /// <summary>At publication: calls the host commit observers attached now with <paramref name="publication"/> (upstream
    /// <c>#notifyHosts</c>'s dispatch); their guards report a throw.</summary>
    private void NotifyHosts(CommitPublication publication, DurableContext context)
    {
        ObserverGuard<CommitPublication>[] observers;
        lock (listenersGate) observers = [.. commitListeners];
        foreach (var observer in observers) observer.Advance(publication, context);
    }

    /// <summary>Runs <paramref name="publish"/> as this thread's publication: the deliveries it schedules (<see cref="Defer"/>) run right
    /// after it, before it returns.</summary>
    private void RunPublishing(Action publish)
    {
        var outer = publishing;
        publishing = (this, []);
        try
        {
            publish();
        }
        finally
        {
            RunDeferred();
            publishing = outer;
        }
    }

    // ─── Held commits ───────────────────────────────────────────────────────

    /// <summary>
    /// Holds a deferred commit at its publication (<see cref="Transaction.Holdable"/>; upstream <c>#hold</c>): adopts it in memory, queues its
    /// changes per document, pinned, by the earliest deadline of the document's held commits, and shows it to the Session's own observers.
    /// A throw of the adoption is the writer's to fail the Session with, as a stored commit's.
    /// </summary>
    private PublishFailure? HoldCommit(Transaction tx, List<HeldChange> held, double withinMs, DurableContext context, Action unregister)
    {
        List<ICommitChange> documentChanges;
        try
        {
            if (AdoptHeldFault?.Invoke() is { } fault) throw fault;
            documentChanges = tx.AdoptHeld();
        }
        catch (Exception error)
        {
            return PublishFailure.OfAdoption(error);
        }
        var deadline = clock() + withinMs;
        foreach (var (loaded, ops, isBase) in held)
        {
            var id = loaded.Record.Id;
            Hold? hold;
            lock (holdsGate) hold = holds.GetValueOrDefault(id);
            if (hold is null)
            {
                // Host code (the pin may arm a timer), outside holdsGate.
                var unpin = PinDocument(loaded.AddressId, id);
                hold = new Hold(loaded, deadline, unpin);
                lock (holdsGate) holds.Add(id, hold);
            }
            lock (holdsGate)
            {
                hold.Pending.Push(ops);
                if (isBase) hold.Base = true;
                if (deadline < hold.Deadline) hold.Deadline = deadline;
            }
        }
        unregister();
        ArmHoldTimer();
        Action<ObservedCommit, DurableContext>[] internals;
        lock (listenersGate) internals = [.. internalListeners];
        if (internals.Length == 0) return null;
        var publication = ObservedCommit.Memory(documentChanges);
        Exception? failure = null;
        RunPublishing(() =>
        {
            foreach (var listener in internals) Contained(() => listener(publication, context), error => failure ??= error);
        });
        return failure is null ? null : PublishFailure.OfListener(failure);
    }

    /// <summary>The held documents, in the order they were first held.</summary>
    private List<Hold> HeldDocuments()
    {
        lock (holdsGate) return [.. holds.Values];
    }

    private int HoldCount
    {
        get { lock (holdsGate) return holds.Count; }
    }

    /// <summary>
    /// Stores the held changes <paramref name="select"/> returns in one storage transaction, through the writer, from a job on the line
    /// (upstream <c>#storeHolds</c>). A barrier: they are selected once every commit admitted before is published. Memory already has them, so
    /// only <c>ObserveCommits</c> observers see it, published as the writer publishes it. A failure fails the Session, as every Storage failure
    /// does, and is this task's error.
    /// </summary>
    private async Task StoreHoldsAsync(Func<List<Hold>> select, DurableContext context)
    {
        await DrainedAsync().ConfigureAwait(false);
        var held = select();
        if (held.Count == 0) return;
        var writes = new List<StorageWrite>(held.Count);
        var changes = new List<ICommitChange>(held.Count);
        var keys = new List<string>(held.Count * 2);
        foreach (var hold in held)
        {
            var loaded = hold.Loaded;
            var value = loaded.Value;
            var version = loaded.ValueVersion;
            (DocumentContent content, ImmutableArray<Op> ops) stored;
            lock (holdsGate) stored = Transaction.StoreHeld(hold, NoOperations, value, version);
            writes.Add(new DocumentChangeWrite(loaded.Record.Id, stored.content));
            keys.Add(LineKeys.Document(loaded.AddressId));
            keys.Add(LineKeys.Incarnation(loaded.Record.Id));
            // Held documents are conversation documents.
            changes.Add(new DocumentValueChange(loaded.Record, loaded.Record.Scope.ConversationId, version, value, stored.ops));
        }
        PublishFailure? Publish(Seq? seq, Action unregister)
        {
            for (var index = 0; index < held.Count; index++)
            {
                var loaded = held[index].Loaded;
                if (((DocumentChangeWrite)writes[index]).Content is DocumentBase) loaded.DeltasSinceBase = 0;
                else loaded.DeltasSinceBase++;
            }
            Release(held);
            unregister();
            bool hosts;
            lock (listenersGate) hosts = commitListeners.Count > 0;
            if (!hosts) return null;
            var publication = new CommitPublication(seq!.Value, changes);
            RunPublishing(() => NotifyHosts(publication, context));
            return null;
        }
        // Run from a job on the line, which a failure it causes is the outcome of.
        var index = current?.Index ?? Interlocked.Read(ref nextIndex) - 1;
        await writer.Admit(new PartSpec(index, writes, keys, context, Publish, static () => { })).Outcome.ConfigureAwait(false);
    }

    /// <summary>After their store: forgets <paramref name="held"/>, unpins their documents, and moves the timer to the next deadline (upstream
    /// <c>#release</c>).</summary>
    private void Release(IReadOnlyList<Hold> held)
    {
        lock (holdsGate)
            foreach (var hold in held) holds.Remove(hold.Loaded.Record.Id);
        foreach (var hold in held) hold.Unpin();
        ArmHoldTimer();
    }

    /// <summary>
    /// Keeps a foreground timer (<see cref="HarnessOptions.SetTimer"/>) for the earliest deadline of the held documents (upstream
    /// <c>#armHoldTimer</c>). When it fires, every held document is stored, in one storage transaction: a deadline is the latest time, and
    /// the others cost little more in the same transaction. Once fired, it stays until its job ran. Called on the line and at publication.
    /// </summary>
    private void ArmHoldTimer()
    {
        HoldTimer? armedTimer = null;
        Action? replaced;
        var delay = 0.0;
        // Host code, read outside the lock.
        var current = clock();
        lock (holdsGate)
        {
            if (closingStarted) return;
            var at = double.PositiveInfinity;
            foreach (var hold in holds.Values)
                if (hold.Deadline < at) at = hold.Deadline;
            var armed = holdTimer;
            if (armed is not null && (armed.Fired || armed.At == at)) return;
            replaced = armed?.Disarm;
            holdTimer = null;
            if (!double.IsPositiveInfinity(at))
            {
                armedTimer = new HoldTimer(at);
                holdTimer = armedTimer;
                delay = Math.Max(0, at - current);
            }
        }
        if (replaced is not null) Contained(replaced, SetTimerError);
        if (armedTimer is not { } timer) return;
        void Run()
        {
            lock (holdsGate) timer.Fired = true;
            var stored = Maintain(async _ =>
            {
                lock (holdsGate)
                    if (ReferenceEquals(holdTimer, timer)) holdTimer = null;
                // Close stores what is held itself.
                if (closingStarted || failure is not null) return true;
                await StoreHoldsAsync(HeldDocuments, DurableContext.Background).ConfigureAwait(false);
                return true;
            }, inline: false);
            _ = stored.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        var disarm = timers.Foreground(Run, delay);
        lock (holdsGate)
        {
            // Still the armed timer: keep its disarm.
            if (ReferenceEquals(holdTimer, timer) && !closingStarted)
            {
                timer.Disarm = disarm;
                return;
            }
            // Fired already (its job owns it now), or replaced or closed meanwhile.
            if (timer.Fired) return;
        }
        Contained(disarm, SetTimerError);
    }

    /// <summary>At publication: records one of a commit's telemetry events (upstream <c>#event</c>). A throwing adapter is reported, as
    /// publication must not throw.</summary>
    private void Event(string name, SpanAttributes attributes, DurableContext context)
    {
        try
        {
            context.Telemetry.AddEvent(name, attributes);
        }
        catch (Exception error)
        {
            Report(error, ReportInfo.HostError(HostCallback.Telemetry), context);
        }
    }

    /// <summary>
    /// Schedules observer delivery work (upstream <c>queueMicrotask</c>). During a publication on this thread it runs in that publication right
    /// after its listeners; otherwise it becomes its own line job, so it never runs inline in the caller.
    /// </summary>
    internal void Defer(Action work)
    {
        if (publishing is { } scope && ReferenceEquals(scope.Owner, this))
        {
            scope.Work.Add(work);
            return;
        }
        _ = Maintain(_ =>
        {
            Run(work);
            return Task.FromResult(true);
        }, inline: false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> after the host observers of the publication this thread runs, if it runs one of this Session's (upstream:
    /// a promise continuation or a microtask queued behind the dispatch to host observers), else at once: the waits a commit resolves and
    /// the aborts it signals, which must not resume before <c>ObserveCommits</c> observers saw it.
    /// </summary>
    internal void AfterPublication(Action work)
    {
        if (publishing is { } scope && ReferenceEquals(scope.Owner, this)) scope.Work.Add(work);
        else work();
    }

    private void RunDeferred()
    {
        if (publishing is not { } scope || !ReferenceEquals(scope.Owner, this)) return;
        // Deliveries may schedule more deliveries; they join the list.
        for (var index = 0; index < scope.Work.Count; index++) Run(scope.Work[index]);
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
    /// Follows the address of the incarnation <paramref name="loaded"/>, after checking the definition, with the observer
    /// <paramref name="create"/> makes from its value, the source's idempotent detach, and the report of a listener error; returns that
    /// observer (upstream <c>#observeDocument</c>). See <see cref="DocumentFollower"/>.
    /// </summary>
    private TObserver ObserveDocument<TObserver>(DocDefinition definition, LoadedDocument loaded,
        Func<JsonObjectValue, Action, Action<Exception>, TObserver> create) where TObserver : SyncObserver<JsonObjectValue>
    {
        Documents.CheckRecordScope(definition, loaded.Record);
        Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        var follower = new DocumentFollower(this, loaded);
        var observer = create(loaded.Value, follower.Detach, Report);
        follower.Attach(observer);
        return observer;
    }

    private async Task<LoadedDocument?> LoadDocumentAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context)
    {
        // What an unpublished commit wrote at the address is in neither the cache nor what Storage shows yet.
        if (Touch(LineKeys.Document(addressId)) is { } gate) await gate.ConfigureAwait(false);
        LoadedDocument? cached;
        lock (documentsGate) cached = documents.GetValueOrDefault(addressId);
        // A tracker serves only tokens of the version its value was materialized for; others reload from Storage.
        if (cached is not null && cached.ValueVersion == definition.Version)
        {
            TouchDocument(addressId);
            return cached;
        }
        if (cached is not null)
        {
            // The reload must find what memory holds: held changes are stored before their tracker goes, after a barrier. On the line, as
            // every load is.
            var id = cached.Record.Id;
            await StoreHoldsAsync(() =>
            {
                lock (holdsGate) return holds.GetValueOrDefault(id) is { } hold ? [hold] : NoHolds;
            }, context).ConfigureAwait(false);
            lock (documentsGate)
                if (documents.TryGetValue(addressId, out var current) && ReferenceEquals(current, cached)) DropDocumentLocked(addressId);
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

    private sealed record FailureBox(Exception Error);

    /// <summary>
    /// What a document observer follows (upstream <c>#observeDocument</c>): the address (kind, scope and key) of the incarnation it was
    /// attached at, from that incarnation on. Its observer, a watch (<see cref="WatchObserver{T}"/>) or a guarded host observer
    /// (<see cref="GuardedObserver{T}"/>), gets each committed change there; a retirement as a <c>retired</c> call with the last value it was
    /// given and no ops (a change in the retiring commit is not published: that commit publishes the retirement only); a later incarnation,
    /// or a value of another definition version, whose shape the observer's value does not have, as a root replacement. When a commit makes
    /// the owner task of a task document terminal, no later commit can change the address: after its changes the commit subscription and
    /// the pin go, and <see cref="SyncObserver{T}.OwnerEnded"/> tells the observer (a watch ends <c>ended</c> once it delivered them, Chord's
    /// <c>endAfterDelivery</c>; a host observer ends <c>ended</c> at once), which stays subscribed to the Session's close until it ends. At
    /// close the source detaches, then ends the observer as <see cref="CloseEnds"/> says. The current incarnation is pinned in the cache,
    /// nothing while the address is absent. Once the detach ran, nothing more of the commit underway reaches the observer or pins anything
    /// for it.
    /// </summary>
    /// <remarks>
    /// Publications run on the line; the detach (a stop, an abort, a listener failure, an observation's detach) may run on any thread, so one
    /// short lock guards what is followed and is never held while the observer runs, a pin is released (which may arm a host timer) or a
    /// subscription is removed. Only the address is kept (the cache's ID and the first record), never <c>LoadedDocument</c>, whose tracker a
    /// retirement frees.
    /// </remarks>
    private sealed class DocumentFollower
    {
        private static readonly Action Unpinned = static () => { };

        private readonly Session session;
        private readonly string addressId;
        private readonly DocumentRecord address;
        private readonly Lock gate = new();
        private SyncObserver<JsonObjectValue>? observer;
        private IDisposable? commitSubscription;
        private IDisposable? closeSubscription;
        // Written under gate; read without it between calls, where a detach on another thread may clear it at any time.
        private volatile bool attached = true;
        // The incarnation followed and its value: none while the address is absent, so the Session keeps no retired value.
        private DocumentId? incarnation;
        private JsonObjectValue? value;
        // The definition version the observer's value has: a change of another version replaces it whole.
        private int version;
        // The incarnation followed stays cached; once it retires or the observer detaches, it is idle again.
        private Action unpin;

        public DocumentFollower(Session session, LoadedDocument loaded)
        {
            this.session = session;
            addressId = loaded.AddressId;
            address = loaded.Record;
            incarnation = loaded.Record.Id;
            value = loaded.Value;
            version = loaded.ValueVersion;
            unpin = session.PinDocument(addressId, loaded.Record.Id);
        }

        /// <summary>Subscribes <paramref name="followed"/> to the Session's commits and close; a Session closing meanwhile attaches nothing.</summary>
        public void Attach(SyncObserver<JsonObjectValue> followed)
        {
            observer = followed;
            try
            {
                commitSubscription = session.FollowCommits(Follow);
                closeSubscription = session.SubscribeClose(Close);
            }
            catch
            {
                // Close or failure began while the document loaded: attach nothing.
                Detach();
                throw;
            }
        }

        /// <summary>The source's detach: idempotent; once it ran, nothing more of a commit underway reaches the observer or pins anything for
        /// it.</summary>
        public void Detach()
        {
            Action release;
            IDisposable? commits;
            IDisposable? closes;
            lock (gate)
            {
                if (!attached) return;
                attached = false;
                release = unpin;
                unpin = Unpinned;
                commits = commitSubscription;
                closes = closeSubscription;
                commitSubscription = null;
                closeSubscription = null;
                incarnation = null;
                value = null;
            }
            commits?.Dispose();
            closes?.Dispose();
            release();
        }

        /// <summary>The Session's close: detach, then end the observer, a watch <c>ended</c>, an observer <c>failed</c> with "session closed"
        /// (or both with the error that failed the Session).</summary>
        private void Close()
        {
            Detach();
            if (observer is { } followed) followed.End(CloseEnds.Of(session.Failure).For(followed.IsWatch));
        }

        private void Follow(ObservedCommit publication, DurableContext context)
        {
            // Listeners run from a copy of the list: another observer's call may have detached this one.
            if (!attached || observer is not { } followed) return;
            var ownerEnds = false;
            foreach (var change in publication.Changes)
            {
                if (change is TaskWrite task)
                {
                    if (address.Scope.Kind == DocumentScopeKind.Task && task.Value.Id.Value == address.Scope.OwnerId &&
                        task.Value.State.Status == DurableTaskStatus.Terminal)
                        ownerEnds = true;
                    continue;
                }
                if (change is not DocumentCommitChange document) continue;
                WatchValueCall<JsonObjectValue> call;
                Action? released = null;
                lock (gate)
                {
                    // A detach on another thread since the last call: the rest of the commit is not the observer's.
                    if (!attached) return;
                    // The incarnation followed is at the address by definition; only another record needs the address compared.
                    var record = document.Record;
                    if (record.Id != incarnation && !AtAddress(record)) continue;
                    if (document is not DocumentValueChange valued)
                    {
                        if (record.Id != incarnation) continue;
                        // Set with every incarnation.
                        var last = value!;
                        incarnation = null;
                        value = null;
                        Release(ref released);
                        call = new WatchValueCall<JsonObjectValue>(last, NoOperations, Retired: true);
                    }
                    else if (record.Id != incarnation || valued.Version != version)
                    {
                        // A new incarnation at the address, or a value of another definition version, whose shape the observer's value
                        // does not have: shown whole.
                        if (record.Id != incarnation)
                        {
                            incarnation = record.Id;
                            Release(ref released);
                            unpin = session.PinDocument(addressId, record.Id);
                        }
                        version = valued.Version;
                        value = valued.Value;
                        call = new WatchValueCall<JsonObjectValue>(valued.Value, [new ReplaceOp(valued.Value)]);
                    }
                    else
                    {
                        // A migration-only base changes nothing for an observer of the new version.
                        if (valued.Ops.IsEmpty) continue;
                        value = valued.Value;
                        call = new WatchValueCall<JsonObjectValue>(valued.Value, valued.Ops);
                    }
                }
                released?.Invoke();
                followed.Advance(call, context);
                // The observer may detach in its call, or throw and be detached: the rest of the commit is not its.
                if (!attached) return;
            }
            if (!ownerEnds) return;
            IDisposable? commits;
            Action? ownerReleased = null;
            lock (gate)
            {
                if (!attached) return;
                commits = commitSubscription;
                commitSubscription = null;
                Release(ref ownerReleased);
            }
            commits?.Dispose();
            ownerReleased?.Invoke();
            followed.OwnerEnded();
        }

        /// <summary>Takes the pin of the incarnation followed, to release outside the lock.</summary>
        private void Release(ref Action? released)
        {
            var pin = unpin;
            unpin = Unpinned;
            if (!ReferenceEquals(pin, Unpinned)) released += pin;
        }

        /// <summary>Whether <paramref name="record"/> is an incarnation at the address followed (upstream <c>atAddress</c>).</summary>
        private bool AtAddress(DocumentRecord record) =>
            record.Kind == address.Kind && record.Key == address.Key && record.Scope.Kind == address.Scope.Kind &&
            record.Scope.OwnerId == address.Scope.OwnerId;
    }

    /// <summary>The ops of a retirement call (upstream <c>NO_OPERATIONS</c>).</summary>
    private static readonly ImmutableArray<Op> NoOperations = [];

    /// <summary>No holds taken (upstream <c>NO_HOLDS</c>).</summary>
    private static readonly List<Hold> NoHolds = [];

    /// <summary>A held document (<see cref="HeldDocument"/>) as the Session keeps it: pinned in the cache until stored (upstream
    /// <c>Hold</c>). Used on the line only.</summary>
    private sealed class Hold(LoadedDocument loaded, double deadline, Action unpin) : HeldDocument(loaded)
    {
        /// <summary>When the earliest of its held commits must be stored, on the Session clock.</summary>
        public double Deadline { get; set; } = deadline;
        public Action Unpin { get; } = unpin;
    }

    /// <summary>The armed store of held documents (upstream <c>HoldTimer</c>): a foreground timer for the earliest deadline, <c>Fired</c> once
    /// its job is queued. Guarded by holdsGate.</summary>
    private sealed class HoldTimer(double at)
    {
        public double At { get; } = at;
        public Action? Disarm { get; set; }
        public bool Fired { get; set; }
    }

    private sealed class DocumentSweep(double at)
    {
        public double At { get; } = at;
        /// <summary>Disarms its timer, once armed. Guarded by documentsGate.</summary>
        public Action? Disarm { get; set; }
    }

    private sealed class Host(Session session) : ITransactionHost
    {
        /// <summary>Reads wait while their key has an unpublished writer (<see cref="GatedStorage"/>).</summary>
        public IStorage Storage => session.gated;
        public Task? Touch(string key) => session.Touch(key);
        public Task<EntryId?> LatestEntryIdAsync(ConversationId conversationId, DurableContext context) =>
            session.LatestEntryIdAsync(conversationId, context);
        public Task<EntryRecord?> LatestHeadMarkerAsync(ConversationId conversationId, DurableContext context) =>
            session.LatestHeadMarkerAsync(conversationId, null, context);
        /// <summary>The Session's index of live tasks' documents is area publication-caches'; until then every terminal task's documents are
        /// scanned.</summary>
        public IEnumerable<DocumentRecord>? TaskDocuments(TaskId taskId)
        {
            lock (session.taskDocumentsGate)
                return session.taskDocuments.TryGetValue(taskId, out var known) ? [.. known.Values] : null;
        }
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

    /// <summary>What the writer needs of its Session (upstream's <c>new Writer({...})</c> in the SessionImpl constructor).</summary>
    private sealed class WriterHost(Session session) : IWriterHost
    {
        public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive,
            DurableContext context) => session.storage.CommitAsync(batches, receive, context);

        /// <summary>The read caches are area publication-caches'.</summary>
        public void Stored(IReadOnlyList<StorageWrite> writes)
        {
            session.cached?.Published(writes);
            session.tails?.Apply(writes);
        }

        public Task? Publishing(Action publish, DurableContext context) => session.PublishingScope(publish, context);

        public Exception? Failure => session.failure?.Error;

        public void Fail(Exception error, long? at, IReadOnlyCollection<long> own, DurableContext? context) =>
            session.Fail(error, context, at, own);

        public void Report(Exception error, DurableContext context) => session.Report(error, ReportInfo.ListenerError, context);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}

/// <summary>
/// What the Session's own components observe of one commit (<c>Session.ObserveCommits</c>; upstream <c>ObservedCommit</c>): a stored commit
/// with its own changes, or a deferred one adopted in memory only (<see cref="CommitPersistence"/>), which changes documents only. Memory is
/// never behind: a later store of held changes is not observed again.
/// </summary>
internal sealed class ObservedCommit
{
    private ObservedCommit(bool memory, Seq? seq, IReadOnlyList<ICommitChange> changes)
    {
        IsMemory = memory;
        Seq = seq;
        Changes = changes;
    }

    /// <summary>Whether it is <c>{kind: "memory"}</c>: adopted in memory and held, not stored yet.</summary>
    public bool IsMemory { get; }
    /// <summary>The commit sequence of a stored commit; null for one held in memory.</summary>
    public Seq? Seq { get; }
    /// <summary>The changes: of a held commit, its document changes only.</summary>
    public IReadOnlyList<ICommitChange> Changes { get; }

    /// <summary><c>{kind: "stored", seq, changes}</c>.</summary>
    public static ObservedCommit Stored(Seq seq, IReadOnlyList<ICommitChange> changes) => new(false, seq, changes);
    /// <summary><c>{kind: "memory", changes}</c>.</summary>
    public static ObservedCommit Memory(IReadOnlyList<ICommitChange> changes) => new(true, null, changes);
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
    /// <summary>Jobs this thread enqueues inside <see cref="Together"/>, queued when it returns.</summary>
    [ThreadStatic] private static List<(SessionLine Line, Func<Task> Run, bool Inline)>? held;

    /// <summary>How many jobs wait behind the running one.</summary>
    public int Waiting
    {
        get { lock (gate) return jobs.Count; }
    }

    /// <summary>
    /// Enqueues <paramref name="job"/>, a job's line part. On an idle line it starts inline when <paramref name="inline"/> is true, else on
    /// the thread pool; <paramref name="inCaller"/>, when given, is set while it runs inline in the caller. The Session delivers outcomes
    /// (Session.Enqueue and Session.Maintain): the line only runs the line parts, one at a time.
    /// </summary>
    public void Enqueue(Func<Task> job, bool inline = true, StrongBox<bool>? inCaller = null)
    {
        if (held is { } together)
        {
            together.Add((this, job, inline));
            return;
        }
        bool start;
        lock (gate)
        {
            jobs.Enqueue(job);
            start = !running;
            running = true;
        }
        if (!start) return;
        if (inline)
        {
            if (inCaller is not null) Volatile.Write(ref inCaller.Value, true);
            Pump();
            if (inCaller is not null) Volatile.Write(ref inCaller.Value, false);
        }
        else ThreadPool.UnsafeQueueUserWorkItem(static line => line.Pump(), this, preferLocal: false);
    }

    /// <summary>
    /// Runs <paramref name="enqueue"/>, holding the jobs it enqueues on this thread, and queues them when it returns, back to back under one
    /// lock: as upstream's enqueues in one synchronous stretch, no job another thread enqueues meanwhile gets between them, and on an idle
    /// line the first starts only once all are queued. A call inside another joins it.
    /// </summary>
    public static void Together(Action enqueue)
    {
        if (held is not null)
        {
            enqueue();
            return;
        }
        var together = held = [];
        try
        {
            enqueue();
        }
        finally
        {
            held = null;
            foreach (var line in together.Select(job => job.Line).Distinct())
                line.EnqueueHeld([.. together.Where(job => job.Line == line)]);
        }
    }

    private void EnqueueHeld(List<(SessionLine Line, Func<Task> Run, bool Inline)> together)
    {
        bool start;
        lock (gate)
        {
            foreach (var job in together) jobs.Enqueue(job.Run);
            start = !running;
            running = true;
        }
        if (!start) return;
        if (together[0].Inline) Pump();
        else ThreadPool.UnsafeQueueUserWorkItem(static line => line.Pump(), this, preferLocal: false);
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
/// Upstream's microtask order for the line results of chosen flows (the scheduler's abort invocations). Upstream resumes every awaiting
/// handler in the order its line job ran, and each runs until it next waits before the next resumes. Here a result's continuation is
/// otherwise scheduled on the thread pool on its own, and a flow whose next job runs inline on an idle line goes on at once: of two flows that
/// each read and then commit, the one whose read ran second can commit first.
/// <para>A delivery runs segments one at a time. <see cref="RunAsync"/> runs a segment that starts flows; every line result of a flow that
/// set <see cref="Current"/> (in an async method, so the setting stays with that flow) is queued as the job that produced it ends, so in line
/// order, and set by the delivery, which runs the awaiting continuation inline until it next suspends. A flow inside a segment has waited
/// before the segment ends, so its result is never set before its continuation is registered (one registered late is scheduled on the thread
/// pool instead). Line jobs reset <see cref="Current"/>, so nothing they enqueue joins the flow. A flow resumed by anything else (a task
/// signal, a timer) runs outside the segments until it next waits on the line.</para>
/// </summary>
internal sealed class OrderedLineDelivery
{
    private static readonly AsyncLocal<OrderedLineDelivery?> current = new();
    [ThreadStatic] private static OrderedLineDelivery? executing;

    private readonly Lock gate = new();
    private readonly Queue<Action> pending = new();
    private bool running;

    /// <summary>The delivery of the current flow, if any.</summary>
    public static OrderedLineDelivery? Current
    {
        get => current.Value;
        set => current.Value = value;
    }

    /// <summary>Whether this thread runs one of the delivery's segments now.</summary>
    public bool Executing => executing == this;

    /// <summary>
    /// Runs <paramref name="segment"/> as a segment of its own: inline when the delivery is idle (or from inside one of its segments), else
    /// after the segments queued before it. Results its flows get meanwhile are delivered after it, on the thread pool.
    /// </summary>
    public Task RunAsync(Action segment)
    {
        if (Executing)
        {
            segment();
            return Task.CompletedTask;
        }
        lock (gate)
        {
            if (running)
            {
                var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Enqueue(() =>
                {
                    try
                    {
                        segment();
                        queued.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        queued.TrySetException(error);
                    }
                });
                return queued.Task;
            }
            running = true;
        }
        var previous = executing;
        executing = this;
        try
        {
            segment();
        }
        finally
        {
            executing = previous;
            bool more;
            lock (gate)
            {
                more = pending.Count > 0;
                if (!more) running = false;
            }
            if (more) ThreadPool.UnsafeQueueUserWorkItem(static delivery => delivery.Deliver(), this, preferLocal: false);
        }
        return Task.CompletedTask;
    }

    /// <summary>Queues one result as the line job that produced it ends, so results queue in line order.</summary>
    public void Post(Action deliver)
    {
        lock (gate)
        {
            pending.Enqueue(deliver);
            if (running) return;
            running = true;
        }
        // Never inline: this runs on the line, and the continuation would run there.
        ThreadPool.UnsafeQueueUserWorkItem(static delivery => delivery.Deliver(), this, preferLocal: false);
    }

    private void Deliver()
    {
        var previous = executing;
        executing = this;
        try
        {
            while (true)
            {
                Action next;
                lock (gate)
                {
                    if (!pending.TryDequeue(out next!))
                    {
                        running = false;
                        return;
                    }
                }
                next();
            }
        }
        finally
        {
            executing = previous;
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
    public async Task<T> CallAsync<T>(DurableContext? read, StorageLease? lease, Func<Task<T>> run, bool failsSession = true)
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
                if (!exempt && failsSession) session.Fail(error);
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

    /// <summary>Guarded like every call, but its outcome is the Session writer's to judge: it publishes the batches received first, then fails
    /// the Session for a refusal (<see cref="CommitResult.Refused"/>) or a rejection, and for a Session that failed meanwhile.</summary>
    public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        guard.CallAsync(null, lease, () => Inner.CommitAsync(batches, receive, context), failsSession: false);

    /// <summary>The backend's own publication scope (upstream <c>scopeOf</c>); not a call to guard.</summary>
    public void Publishing(Action publish, DurableContext context) => Inner.Publishing(publish, context);

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
