// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/session/session.ts (createSession, setDefaultDocumentRetention,
// documentRetention, persistWithin, ObservedCommit, SessionImpl, contained, GuardedStorage, cancellationError, and the document follower's
// atAddress, abortWatch and detachObservation, the last two inline at the attachment they undo); its observer helpers (SyncObserver,
// GuardedObserver, guardObserver, ENDED, closeEnds, closeEndOf, observation, and createWatch, endWatch and endGuarded as WatchObserver<T>
// and SyncObserver<T>.OwnerEnded) are Session/Observers.cs. Pi v1.5.0 sync (area
// harness-options): a read-only Session refuses commits with writes (upstream be14d9732). Pi v1.5.0 sync (area storage-batches): a commit
// sends its writes as one batch of the batched Storage.commit and judges
// the result, as session/writer.ts does for a call of one part: a refusal fails the Session and is the commit's error, as a rejection is.
// Group commit (several parts per call) and the publication scope (Storage.publishing) came with area group-commit (below); GuardedStorage
// forwards both. Pi v1.5.0 sync (area sleep): createSession's setTimer arms the document sweep as a background timer (upstream
// f75b3240a, Timers.cs); a host that declines one leaves eviction to the end of each commit. Pi v1.5.0 sync (area reports): a report
// carries its ReportInfo (a30c2a81d): the Session's failure and a failing close are storage_failed, a host timer host_error setTimer, and a
// listener's throw listener_error, the default. Pi v1.5.0 sync (area telemetry, 2ec55d808 and v1.5.0's fix): a stored commit's publication
// records its committed entries (pi.durable.entry) and settled submissions (pi.durable.submission) on the commit context's span first,
// listeners or not; a throwing telemetry adapter is reported host_error telemetry with that context and the commit stands. A listener's
// report carries the commit context, so the Harness records it on its span; the Session's failure from a commit is reported without one,
// as upstream's writer host passes none (fail(error, undefined, at, own), area registry-reader). Pi v1.5.0 sync (area watches, 92ab53602
// and 42cb822f1): a document watch is Chord's Watch (Chord/Watch.cs) on the Session line's scheduler; the Session ends it ended at close,
// or failed with its failure. documentState() is removed, and Durable no longer uses Chord's replicatedState().
// Pi v1.5.0 sync (area watch-folding, 8b0caefc2 and 5f312dac4): a document watch follows its address (DocumentFollower, upstream
// #observeDocument with createWatch and endWatch): a retirement is a retired call with the last value and no ops, a later incarnation
// arrives as [["r", value]], the current incarnation is pinned (nothing while the address is absent), and a task document's watch ends
// ended after the retirement call of the commit that makes its task terminal. Its value is never null. Publication at adoption came with
// area publication-caches (below). Pi v1.5.0 sync (area observers, 8b0caefc2 with v1.5.0's shapes): ObserveDocAsync attaches a host's
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
// no outcome fence. Pi v1.5.0 sync (area group-commit-scheduler, e5c6182ba's scheduler half): a Harness's
// line jobs overlap storage too, as its scheduler's decisions record the keys they read and its commits write their membership keys
// (MembershipKeysAsync); the serialized line a Harness kept until then (LineWaitsForPublication) is gone. A stored commit's keys leave
// after the Session's own observers (the scheduler's indexes among them) adopted it and before host observers run (Publish), so a
// decision region that finds a key without an unpublished writer reads memory that already holds its every write.
// Pi v1.5.0 sync (area publication-caches: 8b0caefc2 and 5f312dac4's publication order, e5c6182ba's caches): a writing Session reads hot
// task, submission and conversation records through CachedStorage, and each conversation's newest entry ID and head marker through
// ConversationTails; it indexes the documents of the tasks it created (upstream #taskDocuments: TaskDocuments, updated as a commit is
// adopted, a task's entry dropped when it ends), so a terminal commit of such a task scans no documents. A read-only Session keeps none of them.
// Both caches take a stored batch right before it is published (WriterHost.Stored, inside the Storage's publication scope); a throw there
// leaves memory behind what is stored, so it fails the Session as an adoption error does and the call's later parts stay unpublished.
// Publication order of a stored commit (AdoptCommit, Publish), upstream's apart from (3): (1) the read caches, the transaction's adoption
// into memory, the task-document index and the release of the held changes it stored; (2) its telemetry events, then the Session's own
// observers (FollowCommits: the scheduler, submissions, views, watches), contained, the first throw failing the Session once the storage
// call settled, after the host observers got the commit; (3) its keys leave (upstream drops them before the publication runs, as nothing
// else runs until it returns; here a job on another thread could then read memory not yet adopted, Session/Writer.cs); (4) the
// ObserveCommits host observers, only when one was attached as the publication began, and then the observers
// attached by the time the dispatch runs (upstream: one microtask later, queued before the own observers ran); (5) what the own observers
// deferred (AfterPublication, Defer), in order: resumed task, idle and submission waits, abort signals, the scheduler's reservation passes
// and reconcile, watch deliveries (upstream's microtasks and promise continuations behind the dispatch). All of it runs on the publishing
// thread before the commit's outcome completes, so a commit a host observer makes is admitted before any the deferred work makes. One
// window has no upstream counterpart: a wait called on another thread between (2) and (4) can see the new state before host observers
// saw it; a wait registered before the publication always resumes after (4). Locks: CachedStorage and ConversationTails each guard their
// maps with one short lock of their own, held for no Storage call or callback, and taskDocumentsGate guards the index; none of them is
// taken while another Session lock is held, except the tails' lock under the scheduler's gate (ConversationContext.IsKnownTail), never the
// reverse.
// Pi v1.5.0 sync (area registry-reader, the re-pin): the writer host's fail takes no context, as upstream's. The file matches session.ts at
// v1.5.0 apart from the adaptations in this header and these PiSharp hardenings: a host timer's disarm (the document sweep's and the hold
// timer's, at close and on re-arming) is contained and reported host_error setTimer, where upstream's throw would propagate from the call
// that disarms; the held changes a commit stored are released before its publication (step 1), where upstream releases them right after
// the publication's synchronous part, since here a job on another thread could otherwise find them still held; and FollowCommits and
// SubscribeClose wrap each subscription, so one delegate subscribed twice runs twice and each disposer removes its own (upstream's Sets
// hold a function once).
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
//   the listener sets by listenersGate. Commit listeners run synchronously in the publication, after adoption: the Session's own, then
//   the host observers. What a publication schedules (watch deliveries, resumed waits, abort signals, scheduler passes; upstream's
//   microtasks) runs in that publication right after its host observers (a thread-static scope, so a job on another thread never joins
//   it), before the commit's outcome completes; a listener that awaits continues later.
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
public partial class Session : ISession
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

}
