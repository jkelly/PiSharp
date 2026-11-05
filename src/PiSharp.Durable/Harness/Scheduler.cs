// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/scheduler.ts. Named DurableTaskScheduler because
// System.Threading.Tasks.TaskScheduler is used throughout the assembly. Ported as a whole against the v1.2.0 text: the ownership indexes
// (below, roots, edges, settled, unloaded, dropQueue) and the candidate sets of each pass (intent, runnable, finalizeChecks, failFastChecks,
// waiters, abandoned), abandonOnRestart with abortReason "restart", abortOwned and ownedTasks, and wait-cycle rejection.
//
// Concurrency model. Upstream runs on one JavaScript thread: commit listeners, line callbacks and handler code never overlap, and wakeups
// are queued microtasks. Here:
// - Every transition is still decided by one callback serialized on the Session line (reservation, marks, runtime commits, steps,
//   reconciliation), so the durable order is upstream's. The ownership indexes change only on the line too: in the commit listener, in
//   chain loads (which run inside line jobs) and in the sweep (a line job of its own).
// - The in-memory state (live records, the indexes and candidate sets, invocations, kept contexts, flags) is also read off the line (idle
//   checks, runtime memo reads, close, the index sizes for tests), so it is guarded by one short lock. The lock is held only for synchronous
//   bookkeeping: never across an await, never while signalling an invocation, never around host code (handlers, hooks, migrations, settings
//   getters, the clock, onReport); the one exception is the registry snapshot's own lookup methods during inspection, which are pure reads.
// - Upstream's Sets iterate in insertion order, and the order of a pass's candidates is the order of its writes; the candidate sets are
//   insertion-ordered here (OrderedSet), so commits stage their writes in upstream's order.
// - Upstream's queueMicrotask wakeups (#kick, #scheduleReconcile) become thread-pool work items, so nothing commits synchronously from a
//   commit or registry listener. The commit listener's #kick for a commit by which a running task created a task and keeps running is the
//   exception: it enqueues a reservation pass inline (the line is busy with the publishing job, so it only enqueues), which keeps
//   upstream's order of the new task's reservation ahead of its creator's next commit (see Kick(bool)). Invocations then run their
//   handlers on the thread pool, possibly in parallel with each other. The sweep job (#scheduleSweep) is enqueued from the thread pool
//   too, so it never starts inline inside the lock; it still runs after the operation underway.
// - Two outcomes upstream gets from microtask order alone are kept explicitly: a reconciliation pass takes the pending cascade on the line
//   (ReconcileAsync), and a context read that finishes once its task is no longer running is not kept (Runtime.ContextAsync).
// - Abort invocations keep upstream's microtask order on the line (OrderedLineDelivery, one per scheduler): a pass starts them in one
//   segment of the delivery, and their line results resume them one at a time in the order the jobs ran, so abort handlers started
//   together reach the line in reservation order at every step (a caller's nested calls abandoned at a restart settle in call order).
//   Run invocations keep the thread pool: their handlers are long-running host code.
// - AbortController becomes CancellationSignal (a token plus its reason); an invocation's handler context is the scheduler context bound
//   to that signal, so waits bound to it fail with the reason (the ended error, or OperationCanceledException for an abort mark or close,
//   upstream's AbortError).
// - setTimeout becomes the host timers of HarnessOptions.SetTimer (Timers.cs; by default TimeProvider timers, which never keep a process
//   alive and never decline). The host's SetTimer, its disarm actions and OnWake are host code, so none runs under the lock: the sleep timer
//   and the idle context expiry timer are armed and disarmed under a second lock of their own (timerGate), which may take the main lock
//   briefly but is never taken under it. A timer's holder is installed before it is armed, so one that fires synchronously finds itself.
//   Upstream's #scheduleWake microtask becomes one thread-pool job at a time (wakeRunning, wakeDirty): a change while it runs runs it
//   again, so OnWake calls never overlap and none is lost.
// - Walks up and down the ownership tree are loops, never recursion, as upstream's are: chains can be deeper than the stack.
//
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): the scheduler observes the Session's commits as memory adopts them
// (Session.FollowCommits) and skips a held one, which changes conversation documents only; ITaskRuntime.CommitAsync takes CommitOptions,
// so a phase may defer a commit (Live.ProgressCommit).
// Pi v1.5.0 sync (area harness-options): the models are a function read at each access (7ec1f648e), and the readOnly option (be14d9732):
// open loads live tasks as committed, running ones included, and schedules no reconcile; Resume throws ReadOnlySessionException; no
// reconcile is ever scheduled. Pi v1.5.0 sync (area vocabulary, 5f312dac4): the internal observeCommits is followCommits
// (Session.FollowCommits). The citation stays on v1.2.0 until the file's other areas land.
//
// Pi v1.5.0 sync (area sleep): durable sleeps, host timers and wake() (f75b3240a, ec1be23eb). A sleeping task holds no invocation:
// `sleeping` holds the sleeping live tasks, WakeDueLocked makes the due ones reservation candidates at resume, when the sleep timer fires
// and at every reservation pass, and a pass skips a sleep not yet due (an abort mark ends a sleep). Without OnWake the earliest sleep keeps
// a foreground timer while scheduling runs; with it, OnWake hears each change of Wake(). A read-only scheduler never schedules a wake and
// answers inactive (be14d9732). runtime.SleepUntilAsync commits the sleep; runtime.SetTimer's timers are disarmed at the invocation's end;
// the idle context expiry is a background timer. Each pass, inspection and wake reads the Harness clock once, outside the lock. Upstream's
// idle index is another area's: Wake() scans the live tasks as f75b3240a's does.
//
// Pi v1.5.0 sync (area reports): every report carries its ReportInfo (a30c2a81d, 91b33cab3, 5f312dac4). An invocation keeps the extension
// that installed its definition at reservation (installerOf), which a task's own failure names: task_error, task_faulted with reason threw
// once the fault is durable, task_outdated with outdatedReason's reason in place of v1.2.0's cause, migration_failed; a hook names its own
// extension, runtime.Report(error, from) the code it names. BlockedReasonOf and SubscribeBlocked serve the task graph (440a5fcdb): a registry
// change and a failed migration notify the graph, and a failed migration schedules a wake. Wake() counts a reconcile pass with work, one
// underway or one due (reconcileWork), so open's recovery pass keeps the host's wake active until it lands (5ba1cbaf3). The telemetry
// context of a report is area telemetry's. Host timer callbacks the scheduler runs
// are contained, as a throw on a timer thread would end the process: a task's own SetTimer callback reports task_error, a host disarm
// host_error setTimer, and the scheduler's own callbacks listener_error.
//
// Pi v1.5.0 sync (area telemetry, 2ec55d808): each phase invocation is a pi.durable.phase span (PhaseAsync, upstream #phase) under the
// task's stored trace, or the Harness telemetry, that covers the handler and the decision commit after it (the first decision commit of a
// run, before any handler, is outside every span, as upstream's). Its start attributes name the task, the phase, the invocation kind and
// whether this is the first invocation of a task loaded at open (resumed); the first invocation of a never-started task links to its
// creation links. Handlers see the span on their context, steps commit under it, and its outcome (next=<phase>, waiting, sleeping, done,
// faulted or aborted) sets the error status of a faulted, failed or orphaned outcome, its diagnostic capped at 500 characters, and is
// recorded in pi.durable.phase.duration. Invocation.Record follows the committed record, as upstream's invocation.record, and a fault
// the scheduler decides is kept (Invocation.Fault), as its terminal record is published after the invocation ended. A running commit that
// changes the checkpoint's phase adds pi.durable.checkpoint. pi.durable.tasks.active counts live tasks: +1 when one becomes live (open's
// load included), -1 when it ends, every live one at close; the deltas are taken under the lock and counted outside it, a throwing adapter
// reported host_error telemetry. Reports and the Session's failure from a step carry the invocation's context. The citation stays on v1.2.0
// until the file's other areas land.
//
// Pi v1.5.0 sync (area restarts, b3a29f111 over a30c2a81d's crash-loop guard, without 1.4.0's parking): open counts a restart of each task
// it finds running (TaskRecord.Restarts); a state the task commits that differs from its current one is progress and drops the count, the
// same state committed again (intent before an effect) keeps it, and an outcome or an abort mark drops it (the count then counts cut-offs
// of the abort handler). A close is not a crash: the join writes the tasks it stopped back as pending, uncounted (Session.WriteAtCloseAsync),
// read on the line after the commits admitted before the seal, so an abort mark queued before the close survives. At
// settings.restartLimit a reservation pass faults an abort handler's task, or a task of a kind that does not end itself at the limit
// (LimitsRestarts: the built-in generation, tool and compaction tasks end themselves), with detail {reason: "restart_limit"}, and
// reports task_faulted with reason restart_limit; the settings are read only for a task that was cut off at all. A runtime commit that
// ends its task failed with detail reason restart_limit or interrupted reports task_interrupted with the task's count.
//
// Pi v1.5.0 sync (area run-end): TaskRuntime.entries() reads the task's conversation's raw history on the line (65fba65b2), and
// endedByCrashes (7e15f9601) marks a terminal state a built-in task commits because it was cut off settings.restartLimit times when its
// outcome does not say so (a generation whose run answered ends completed without its after-run tasks): the runtime commit reports it
// task_interrupted with reason restart_limit. Upstream keys a WeakMap by the state object; EndedByCrashesStates is a
// ConditionalWeakTable keyed by the TaskState instance, so nothing is added to the stored state.
//
// Pi v1.5.0 sync (area group-commit-scheduler, e5c6182ba's scheduler half; docs/group-commit.md §4.2 and §6): decisions stay serialized on
// the line while their storage overlaps (group commit), and this memory changes only as each commit is published. So every decision reads
// it in a decision region (RegionAsync, upstream #region): it records the keys it reads (a task's record, LiveRecordLocked; the live work
// below a node, WalkDownLocked; an owner edge, EdgeLocked; the cancellation intent set; the roots, IdleLocked), stages nothing, and when one
// of them has an unpublished writer it waits for that publication, reloads the chains it walks and runs again; only a region that read
// nothing unpublished is applied. Every commit writes the memberships its task changes alter (MembershipKeysAsync, upstream
// membershipKeys: the chain from each created or ended task's parent up to the nearest live task or root conversation, the roots for a
// walk that reached one, and the intent set when a mark or failed outcome changes), loading the chains it walks; the nodes a chain load
// installs are pinned until its job's commit is published (pins, Pin), so the sweep keeps them. Decisions that cannot be keyed (open,
// inspection, a conversation abort) wait for every unpublished commit (PublishedAsync). Reservation, reconciliation and steps are the
// scheduler's own commits (Session.CommitInternalAsync): one that writes nothing and waited at no gate is delivered at once. Line reads go
// through the line's Storage (Session.LineStorage, ReadOnLineAsync's argument). What stands in for upstream's single thread here:
// - A region's decide runs under the lock, and so do the touches of the keys it recorded (Session.Touch takes only the writer's own lock,
//   which never calls out, so this lock comes first). A commit's publication adopts it in this memory (Observe, under this lock) before
//   it drops its keys (Session.Publish), so a key a region finds without an unpublished writer was adopted before the region read it,
//   and no publication can slip between the read and the touch. Upstream needs neither: nothing runs between its synchronous decide and
//   its touches.
// - What a clean region decided stands while its job applies it, as upstream's does: no commit is admitted while the job holds the line,
//   and none of the keys it read has an unpublished writer. Publications of other commits still run on other threads meanwhile, so a
//   region's changes to the candidate sets (a reservation's dropped candidates, the finalize and failFast checks it took) are applied
//   under the same lock as its decide (RegionAsync's apply), never by clearing a set another publication may have added to since.
// - A reservation pass a publication kicks (Kick(bool) on the line) is enqueued in line order there but starts on the thread pool, as a
//   publication now runs on the writer's thread rather than inside a line job, and a job must not run inside it.
// Upstream's kept context ranges, advanced at publication (#advanceContexts, rangeEntry, knownLatestEntryId), are area
// publication-caches'. The citation stays on v1.2.0: area scheduler-idle changes this file last and re-pins it.
using System.Collections;
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>Terminal outcomes the scheduler writes without running task code: faulted or orphaned (upstream <c>SchedulerOutcome</c>).</summary>
internal delegate Task SettleOutcome(Transaction tx, TaskRecord record, TaskOutcome outcome);

/// <summary>An invocation a conversation handle is bound to: its signal, and a check that throws once it ended (upstream
/// <c>InvocationBinding</c>).</summary>
internal sealed record InvocationBinding(CancellationSignal Signal, Action Check);

/// <summary>Sizes of the scheduler's ownership indexes, for tests that bound its memory (upstream <c>TaskScheduler.indexSizes</c>, read
/// through the Harness's <c>SCHEDULER_INDEX_SIZES</c>). <see cref="Below"/> counts the listed child nodes; <see cref="Pins"/> the nodes chain
/// loads keep for commits not published yet.</summary>
internal readonly record struct SchedulerIndexSizes(int Live, int Settled, int Edges, int Below, int Roots, int Unloaded, int DropQueue,
    int Pins);

/// <summary>Options of the task scheduler (upstream <c>TaskSchedulerOptions</c>).</summary>
/// <summary>What <see cref="DurableTaskScheduler.BlockedReasonOf"/> reads of a task: the task graph keeps only this of each record
/// (upstream <c>BlockedSubject</c>).</summary>
internal readonly record struct BlockedSubject(TaskId Id, string Kind, int Version, bool AbortRequested, TaskAbortReason? AbortReason,
    DurableTaskStatus Status)
{
    /// <summary>The part of <paramref name="record"/> <see cref="DurableTaskScheduler.BlockedReasonOf"/> reads (upstream
    /// <c>blockedSubject</c>).</summary>
    public static BlockedSubject Of(TaskRecord record) =>
        new(record.Id, record.Kind, record.Version, record.AbortRequested, record.AbortReason, record.State.Status);
}

internal sealed record DurableTaskSchedulerOptions
{
    public required Session Session { get; init; }
    public required IStorage Storage { get; init; }
    public required IRegistryReader Registry { get; init; }
    /// <summary>The Harness's models, read at each access (upstream <c>currentModels</c>).</summary>
    public required Func<IDurableModels> Models { get; init; }
    /// <summary>Resolves a conversation's agent against a snapshot; the runtime calls it at most once per phase.</summary>
    public required Func<ConversationId, IRegistrySnapshot, DurableContext, Task<Agent>> Agent { get; init; }
    /// <summary>Resolves the settings; read at each access.</summary>
    public required Func<Settings> Settings { get; init; }
    /// <summary>Builds a conversation's environment with <c>HarnessOptions.Env</c>.</summary>
    public required Func<ConversationId, DurableContext, Task<IExecutionEnv?>> Env { get; init; }
    public required Func<double> Now { get; init; }
    /// <summary><see cref="HarnessOptions.OnWake"/>: replaces the timer for sleeping tasks.</summary>
    public Action<Wake>? OnWake { get; init; }
    /// <summary>The host's timers (<see cref="HarnessOptions.SetTimer"/>).</summary>
    public required HostTimers Timers { get; init; }
    /// <summary>The Harness's report: never throws.</summary>
    public required ReportSink Report { get; init; }
    /// <summary><see cref="HarnessOptions.Telemetry"/>: phase spans, phase durations and active tasks. None records nothing.</summary>
    public ITelemetryContext? Telemetry { get; init; }
    /// <summary>Harness cleanup staged in the commit that makes an outcome the scheduler wrote itself terminal.</summary>
    public required SettleOutcome SettleOutcome { get; init; }
    /// <summary>Withdraws a conversation's queued inputs, for conversation abort and abort cascades.</summary>
    public required Func<Transaction, ConversationId, Task> WithdrawInputs { get; init; }
    /// <summary>Invocation-bound handle of an existing conversation, for task runtimes and tools.</summary>
    public required Func<ConversationId, InvocationBinding, DurableContext, Task<IConversationHandle?>> Conversation { get; init; }
    /// <summary>Context for scheduler commits and invocations; carries no caller cancellation.</summary>
    public required DurableContext Context { get; init; }
    /// <summary><see cref="HarnessOptions.ReadOnly"/>: open loads live tasks as committed, <c>running</c> ones included, and the scheduler
    /// never commits or runs anything.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Task kinds whose run phases end the task themselves once it was cut off <c>settings.RestartLimit</c> times (the built-in
    /// tasks, from <see cref="TaskRecord.Restarts"/>); the scheduler ends a task of any other kind <c>faulted</c> instead. Null: none.</summary>
    public Func<string, bool>? LimitsRestarts { get; init; }
}

/// <summary>
/// The durable task scheduler of one Harness (upstream <c>TaskScheduler</c>).
/// </summary>
/// <remarks>
/// <para><c>live</c> mirrors every committed non-terminal task record: pending, running, waiting, sleeping and completing. The synchronous commit
/// listener updates it on the Session line, so code running on the line reads exactly the committed state from it.</para>
/// <para>Tasks and conversations form one ownership tree (spec §5.5): a task's parent is its owner task, or its conversation; a conversation's
/// parent is its owner task, if any. Walks up that tree decide what reaches one task: a cancelling owner, a scope. <c>below</c> lists the tree
/// downward around live work, so what lies below one node is a walk down from it: a task's live ordinary owned work, which holds its outcome
/// as <c>completing</c> and delays its abort handler, a scope's tasks, a cascade's reach. No pass visits every live task; the candidate sets
/// hold each pass's candidates.</para>
/// <para>The ownership indexes are bounded by live work: an owner edge and an ended task's ownership fields are kept only while something is
/// listed below them in <c>below</c>, so only the chains above live tasks are known, whole. Work that appears later below a dropped node finds
/// it unknown and loads its chain from storage again.</para>
/// <para>Invariant: every task transition is decided and written by one callback serialized on the Session line. That covers reservation,
/// marks, runtime commits, finalization, and the step before each phase, which applies the precedence rules and writes a fault or handover.
/// Handlers and joins run off the line. An invocation ends inside the step that decides its end, so a runtime commit it queued either lands
/// before that decision or is rejected.</para>
/// <para>Decisions stay serialized; their storage overlaps (group commit). The Session stores the commits of several jobs in one call while
/// the line runs on, and this memory changes only as each commit is published. So every decision reads it in a decision region
/// (<see cref="RegionAsync{T}"/>): recording the keys it reads, staging nothing; when one of them has an unpublished writer, the region waits
/// for its publication and runs again, and only a region that read nothing unpublished is applied. Every commit writes the memberships its
/// task changes alter (<see cref="MembershipKeysAsync(Transaction)"/>), so a walk that a pending commit would change waits.</para>
/// </remarks>
internal sealed class DurableTaskScheduler
{
    private const int ScanPageSize = 256;
    private static readonly DurableTaskStatus[] LiveStatuses =
        [DurableTaskStatus.Pending, DurableTaskStatus.Running, DurableTaskStatus.Waiting, DurableTaskStatus.Sleeping, DurableTaskStatus.Completing];

    private readonly Session session;
    private readonly IStorage storage;
    private readonly IRegistryReader registry;
    private readonly Func<IDurableModels> models;
    private readonly Func<ConversationId, IRegistrySnapshot, DurableContext, Task<Agent>> agent;
    private readonly Func<Settings> settings;
    private readonly Func<ConversationId, DurableContext, Task<IExecutionEnv?>> env;
    private readonly Func<double> now;
    private readonly Action<Wake>? onWake;
    private readonly HostTimers timers;
    private readonly ReportSink report;
    private readonly ITelemetryContext telemetry;
    private readonly SchemaTelemetryCounter activeMetric;
    private readonly SchemaTelemetryHistogram durationMetric;
    /// <summary>Whether active-task deltas are counted: only with host telemetry, as the no-op counter records nothing.</summary>
    private readonly bool countsActive;
    /// <summary>Tasks loaded at open whose first invocation has not started yet (upstream <c>#resumed</c>). Guarded by the lock.</summary>
    private readonly HashSet<TaskId> resumed = [];
    /// <summary><c>pi.durable.tasks.active</c> deltas taken under the lock, counted outside it by <see cref="CountActive"/>.</summary>
    private readonly List<(int Delta, string Kind)> activeDeltas = [];
    private readonly SettleOutcome settleOutcome;
    private readonly Func<Transaction, ConversationId, Task> withdrawInputs;
    private readonly Func<ConversationId, InvocationBinding, DurableContext, Task<IConversationHandle?>> conversation;
    private readonly DurableContext context;
    private readonly bool readOnly;
    private readonly Func<string, bool> limitsRestarts;

    private readonly Lock gate = new();
    // Insertion-ordered like a JavaScript Map: replacing a record keeps its position.
    private readonly OrderedDictionary<TaskId, TaskRecord> live = [];
    /// <summary>
    /// Context range last read through a task runtime, per conversation: a later read, by any of its tasks, scans only newer entries.
    /// <c>IdleSince</c> is the Harness time it was first seen idle. Derived and never persisted; dropped at the first idle check after
    /// <c>settings.ContextRetentionMs</c> of idleness, and at close.
    /// </summary>
    private readonly Dictionary<ConversationId, KeptContext> contexts = [];
    /// <summary>Guards the two timers below while they are armed or disarmed; taken before <see cref="gate"/>, never under it.</summary>
    private readonly Lock timerGate = new();
    /// <summary>Background timer for the earliest expiry of an idle context, unless the host declined it. Guarded by
    /// <see cref="timerGate"/>.</summary>
    private ArmedTimer? expiry;
    /// <summary>When the earliest idle context expires, which <see cref="ApplyExpiry"/> arms <see cref="expiry"/> for.</summary>
    private double? expiryDue;
    /// <summary>Timer for the earliest sleep, without <c>OnWake</c> while scheduling runs. Guarded by <see cref="timerGate"/>.</summary>
    private ArmedTimer? sleepTimer;
    /// <summary>Last value given to <c>OnWake</c>, so an unchanged one is not repeated. Only the wake job reads and writes it.</summary>
    private Wake? lastWake;
    /// <summary>The wake job is queued or running; <see cref="wakeDirty"/> asks it for another round.</summary>
    private bool wakeRunning;
    private bool wakeDirty;
    private readonly Dictionary<TaskId, Invocation> invocations = [];
    private readonly Waiters<TaskId, TaskRecord> taskWaiters = new();
    /// <summary>Idle waiters by conversation; null waits for the whole Harness.</summary>
    private readonly Waiters<ConversationId?, bool> idleWaiters = new();
    /// <summary>Definition whose migration failed per task; retried only once the registry resolves another definition.</summary>
    private readonly Dictionary<TaskId, (DurableTask Task, Exception Error)> failedMigrations = [];
    /// <summary>Owner task of each known conversation, null when ownerless.</summary>
    private readonly Dictionary<ConversationId, TaskId?> edges = [];
    /// <summary>Ownership fields of the known terminal tasks, which walks pass through.</summary>
    private readonly Dictionary<TaskId, TaskNode> settled = [];
    /// <summary>
    /// Ended tasks and conversations found unneeded, dropped by <see cref="SweepLocked"/> at the end of the commit's publication unless
    /// needed again by then: a chain load installs nodes before the work below them is listed.
    /// </summary>
    private readonly OrderedSet<Node> dropQueue;
    /// <summary>
    /// The tree downward around live work: each node's child nodes that are live tasks or have nodes listed below them. A conversation's
    /// parent is its owner task; ownerless ones with work are in <see cref="roots"/>.
    /// </summary>
    private readonly Dictionary<Node, OrderedSet<Node>> below = [];
    private readonly OrderedSet<ConversationId> roots;
    /// <summary>Known tasks whose chain may not be loaded; every other known task's chain is, and stays, loaded.</summary>
    private readonly OrderedSet<TaskId> unloaded;
    /// <summary>Live tasks with cancellation intent, the sources of abort cascades (spec §5.4).</summary>
    private readonly OrderedSet<TaskId> intent;
    /// <summary>Live tasks that are not <c>completing</c>, the candidates of a reservation pass; ones found waiting leave it.</summary>
    private readonly OrderedSet<TaskId> runnable;
    /// <summary><c>completing</c> tasks the next finalize pass checks: those that started holding, and holders of ended work.</summary>
    private readonly OrderedSet<TaskId> finalizeChecks;
    /// <summary>Live waiting tasks by each task in their <c>on</c>.</summary>
    private readonly Dictionary<TaskId, OrderedSet<TaskId>> waiters = [];
    /// <summary><c>failFast</c> waiters the next reconcile checks for a failed task in <c>on</c>: at open, when they start waiting, and when
    /// one of their tasks fails.</summary>
    private readonly OrderedSet<TaskId> failFastChecks;
    /// <summary>
    /// Live tasks with <c>abandonOnRestart</c> found at open, from an earlier Harness. The first reservation pass abort-marks them with reason
    /// <c>restart</c>, so nothing of them or below them runs a phase again.
    /// </summary>
    private readonly OrderedSet<TaskId> abandoned;
    /// <summary>Sleeping live tasks, due or not: the candidates of <see cref="WakeDueLocked"/>.</summary>
    private readonly OrderedSet<TaskId> sleeping;
    /// <summary>Members iterated over the scheduler's sets, for tests that bound a pass's work (upstream's tests count
    /// <c>Set.prototype[Symbol.iterator]</c> entries).</summary>
    private readonly IterationCounter iterations = new();
    private bool reconcileScheduled;
    private bool sweepScheduled;
    private bool cascadePending;
    /// <summary>Reconciliation passes running now; with the flags above, what <see cref="QuiesceAsync"/> waits out.</summary>
    private int reconciling;
    /// <summary>Reconcile passes underway with work (they may overlap): their commits finalize held outcomes and mark cascades, so
    /// <see cref="Wake"/> counts them.</summary>
    private int reconcilingWork;
    /// <summary>Listeners of <see cref="SubscribeBlocked"/>. Guarded by <see cref="gate"/>; called outside it.</summary>
    private ImmutableArray<Action> blockedListeners = [];
    /// <summary>A registry snapshot that threw for the task graph was reported; it is read per record, so only once.</summary>
    private int snapshotReported;
    private IDisposable? registrySubscription;
    private bool enabled;
    private bool closing;
    private bool dirty;
    private bool draining;
    /// <summary>Invocations live when <see cref="Seal"/> ran, which <see cref="JoinAsync"/> joins even once they have left the map.</summary>
    private readonly List<Invocation> sealedInvocations = [];
    /// <summary>Line reservation passes (<see cref="Kick(bool)"/> on the line) enqueued whose commit has not started yet.</summary>
    private int linePassesQueued;
    /// <summary>Line reservation passes not finished yet (their invocations not started); <see cref="QuiesceAsync"/> waits them out.</summary>
    private int linePassesRunning;
    /// <summary>Nodes chain loads installed for jobs whose commits are not published yet, with how many such jobs: the sweep keeps them, as
    /// work a pending commit creates below them needs them (upstream <c>#pins</c>). Guarded by the lock.</summary>
    private readonly Dictionary<Node, int> pins = [];
    /// <summary>Keys the decision region underway reads (<see cref="RegionAsync{T}"/>); set only while one runs, under the lock (upstream
    /// <c>#reading</c>).</summary>
    private HashSet<string>? reading;
    /// <summary>Starts a pass's invocations and resumes abort invocations on their line results, in line order (see
    /// <see cref="StartPassAsync"/>).</summary>
    private readonly OrderedLineDelivery abortDelivery = new();

    public DurableTaskScheduler(DurableTaskSchedulerOptions options)
    {
        session = options.Session;
        storage = options.Storage;
        registry = options.Registry;
        models = options.Models;
        agent = options.Agent;
        settings = options.Settings;
        env = options.Env;
        now = options.Now;
        onWake = options.OnWake;
        timers = options.Timers;
        report = options.Report;
        telemetry = options.Telemetry ?? NoopTelemetry.Context;
        countsActive = options.Telemetry is not null;
        var metrics = TelemetrySchema.CreateTypedMetrics(telemetry.Metrics ?? NoopTelemetry.Metrics, DurableTelemetry.Schema);
        activeMetric = metrics.UpDownCounter("pi.durable.tasks.active");
        durationMetric = metrics.Histogram("pi.durable.phase.duration");
        settleOutcome = options.SettleOutcome;
        withdrawInputs = options.WithdrawInputs;
        conversation = options.Conversation;
        context = options.Context;
        readOnly = options.ReadOnly;
        limitsRestarts = options.LimitsRestarts ?? (static _ => false);
        dropQueue = new(iterations);
        roots = new(iterations);
        unloaded = new(iterations);
        intent = new(iterations);
        runnable = new(iterations);
        finalizeChecks = new(iterations);
        failFastChecks = new(iterations);
        abandoned = new(iterations);
        sleeping = new(iterations);
    }

    /// <summary>Test probe: members iterated over the scheduler's sets so far (see <see cref="iterations"/>).</summary>
    internal long SetEntries => Interlocked.Read(ref iterations.Entries);

    /// <summary>Loads live tasks and changes surviving <c>running</c> tasks back to <c>pending</c>, counting the restart. Dispatches nothing;
    /// a task cut off <c>settings.RestartLimit</c> times ends when it is next reserved. Read-only, it only loads them as committed: a
    /// <c>running</c> task stays as committed, running in another process.</summary>
    public async Task OpenAsync(DurableContext openContext)
    {
        session.FollowCommits((publication, _) => Observe(publication));
        session.SubscribeClose(Seal);
        registrySubscription = registry.Subscribe(() =>
        {
            NotifyBlocked();
            Kick();
        });
        await session.CommitWithAsync(async tx =>
        {
            await PublishedAsync().ConfigureAwait(false);
            // Every table read before the first write.
            var scans = new List<List<TaskRecord>>();
            foreach (var status in LiveStatuses)
                scans.Add(await HarnessUtil.ScanAll(cursor => tx.ScanTasksAsync(new TaskQuery { Status = status }, ScanPageSize, cursor)).ConfigureAwait(false));
            lock (gate)
            {
                foreach (var records in scans)
                {
                    foreach (var record in records)
                    {
                        TrackLocked(null, record);
                        resumed.Add(record.Id);
                        if (readOnly) continue;
                        if (record.AbandonOnRestart) abandoned.Add(record.Id);
                        if (record.State is RunningTaskState running)
                            tx.SetTask(WithState(record, new PendingTaskState(running.Checkpoint)) with { Restarts = (record.Restarts ?? 0) + 1 });
                        if (record.State is WaitingTaskState { Policy: JoinPolicy.FailFast }) failFastChecks.Add(record.Id);
                    }
                }
            }
            CountActive();
        }, openContext).ConfigureAwait(false);
        if (readOnly) return;
        // Derive abort marks a crash left unapplied below cancelled owners, and finalize held outcomes.
        lock (gate)
        {
            cascadePending = true;
            ScheduleReconcileLocked();
        }
        ScheduleWake();
    }

    /// <summary>Test barrier: completes once the task has no live invocation, or its current invocation has fully ended.</summary>
    internal Task InvocationDone(TaskId id)
    {
        lock (gate) return invocations.TryGetValue(id, out var invocation) ? invocation.Done.Task : Task.CompletedTask;
    }

    /// <summary>
    /// Test barrier: completes once no reconciliation, reservation or sweep is scheduled or running and the line has drained, so the indexes
    /// hold what the committed records imply. Upstream's tests get this from microtask order; here those passes run on the thread pool.
    /// </summary>
    internal async Task QuiesceAsync()
    {
        while (true)
        {
            await session.FlushLineAsync().ConfigureAwait(false);
            lock (gate) if (!reconcileScheduled && reconciling == 0 && !sweepScheduled && !draining && linePassesRunning == 0) return;
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    /// <summary>Internal test barrier: a sweep job is scheduled and has not run its line part yet (see <see cref="ScheduleSweepLocked"/>).
    /// Unlike <see cref="QuiesceAsync"/>, reading it waits for no publication, so a test reads it while a commit is held.</summary>
    internal bool SweepScheduled
    {
        get
        {
            lock (gate) return sweepScheduled;
        }
    }

    /// <summary>Sizes of the ownership indexes, for tests that bound the scheduler's memory (upstream <c>indexSizes</c>).</summary>
    internal SchedulerIndexSizes IndexSizes
    {
        get
        {
            lock (gate)
            {
                var listed = 0;
                foreach (var children in below.Values) listed += children.Count;
                return new SchedulerIndexSizes(live.Count, settled.Count, edges.Count, listed, roots.Count, unloaded.Count, dropQueue.Count,
                    pins.Count);
            }
        }
    }

    /// <summary>Enables scheduling, and makes sleeping tasks that are due runnable: a host wakeup, such as an alarm, calls this. Idempotent;
    /// the kick does nothing once closing. A read-only scheduler throws <see cref="ReadOnlySessionException"/>.</summary>
    public void Resume()
    {
        if (readOnly) throw new ReadOnlySessionException("running tasks");
        var at = now();
        lock (gate)
        {
            enabled = true;
            WakeDueLocked(at);
        }
        Kick();
    }

    /// <summary>
    /// Why a live task cannot run here, if it cannot (upstream <c>blockedReason</c>): its definition is missing, too old, or failed to
    /// migrate. A running task, an abort-marked one, and one holding its outcome are never blocked. A registry snapshot that throws is
    /// reported once, and blocks nothing.
    /// </summary>
    public BlockedReason? BlockedReasonOf(BlockedSubject record)
    {
        if (record.Status is DurableTaskStatus.Running or DurableTaskStatus.Completing or DurableTaskStatus.Terminal) return null;
        // An abort request runs its handler or orphans the task; a `restart` mark waits for the definition, blocked.
        if (record.AbortRequested && record.AbortReason is null) return null;
        IRegistrySnapshot snapshot;
        try
        {
            snapshot = registry.Snapshot();
        }
        catch (Exception error)
        {
            if (Interlocked.Exchange(ref snapshotReported, 1) == 0) report(error, RegistryError);
            return null;
        }
        var fit = Fit(record.Id, record.Version, snapshot.GetTask(record.Kind));
        if (fit.Reason is { } reason) return reason;
        return fit.Migrates && fit.Task!.Definition.Migrate is null ? Durable.BlockedReason.MigrationFailed : null;
    }

    /// <summary>Calls <paramref name="listener"/> when blocked reasons may have changed without a commit: a registry change or a failed
    /// migration (upstream <c>subscribeBlocked</c>). Returns its unsubscribe.</summary>
    public Action SubscribeBlocked(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate) blockedListeners = blockedListeners.Add(listener);
        return () =>
        {
            lock (gate) blockedListeners = blockedListeners.Remove(listener);
        };
    }

    private void NotifyBlocked()
    {
        ImmutableArray<Action> listeners;
        lock (gate) listeners = blockedListeners;
        foreach (var listener in listeners)
        {
            try
            {
                listener();
            }
            catch (Exception error)
            {
                report(error, ReportInfo.ListenerError);
            }
        }
    }

    /// <summary>
    /// When the Session next needs a process (upstream <c>wake()</c>): <c>Active</c> while some task can run now or is running, or a reconcile
    /// pass has work (it finalizes held outcomes and marks cascades, at open as recovery: a process that dies before it lands must be woken
    /// again), <c>At</c> for the earliest sleep not yet due. From memory; a task whose chain is not loaded yet counts as it would alone. A
    /// read-only scheduler runs nothing, so it never needs a process.
    /// </summary>
    public Wake Wake()
    {
        if (readOnly) return new Wake(false);
        var at = now();
        double? earliest = null;
        bool active;
        var check = new List<TaskRecord>();
        lock (gate)
        {
            active = invocations.Count > 0 || reconcilingWork > 0 || ReconcileWorkLocked();
            foreach (var record in live.Values)
            {
                var state = record.State;
                if (state.Status == DurableTaskStatus.Completing) continue;
                if (state is WaitingTaskState waiting && !record.AbortRequested && waiting.On.Any(live.ContainsKey)) continue;
                if (state is SleepingTaskState asleep && !record.AbortRequested && asleep.Until > at)
                {
                    earliest = earliest is { } other ? Math.Min(other, asleep.Until) : asleep.Until;
                    continue;
                }
                if (active) continue;
                // An abort invocation waits for the live work below it, which counts itself.
                if (record.AbortRequested && WaitingOnLocked(record, first: true).Count > 0) continue;
                // An abort-marked task runs its handler or is orphaned; any other waits for a definition that can take it.
                if (record.AbortRequested && record.AbortReason is null) active = true;
                else check.Add(record);
            }
        }
        if (!active && check.Count > 0) active = RunsNow(check);
        return new Wake(active, earliest);
    }

    /// <summary>Whether a definition can take one of <paramref name="records"/>: the registry check <see cref="Wake"/> needs only while
    /// nothing else is active. The snapshot is host code, so this runs outside the lock; a snapshot that throws is reported, and counts as
    /// able.</summary>
    private bool RunsNow(List<TaskRecord> records)
    {
        IRegistrySnapshot snapshot;
        try
        {
            snapshot = registry.Snapshot();
        }
        catch (Exception error)
        {
            report(error, RegistryError);
            return true;
        }
        foreach (var record in records)
            if (Fit(record, snapshot.GetTask(record.Kind)).Reason is null) return true;
        return false;
    }

    /// <summary>
    /// Signals every invocation and waits for them, then writes the tasks they ran back as <c>pending</c> (<see cref="Session.WriteAtCloseAsync"/>),
    /// as a close is not a crash. Close calls this after every close listener has run, so waits, watches and streams a task holds end with the
    /// Session's reason, not as cancelled by its signal. Never fails.
    /// </summary>
    public async Task JoinAsync()
    {
        List<Invocation> signal;
        lock (gate)
        {
            // Also those Seal saw: a close listener can end an invocation (a watch it awaited closes), and it leaves the map before End
            // signals it, while upstream's join runs before any such continuation.
            signal = [.. sealedInvocations.Union(invocations.Values).OrderBy(invocation => invocation.Started)];
            sealedInvocations.Clear();
        }
        foreach (var invocation in signal) Signal(invocation, null);
        await Task.WhenAll(signal.Select(invocation => invocation.Done.Task)).ConfigureAwait(false);
        if (signal.Count == 0) return;
        // A close is not a crash: a task it stopped is pending again, so the next open counts no restart for it. Read on the line after
        // commits admitted before the seal, such as an abort mark, so the write keeps them. A failure leaves them running, counted as if cut
        // off; the Storage guard fails the Session and reports it.
        try
        {
            await session.WriteAtCloseAsync(() =>
            {
                var writes = new List<StorageWrite>();
                lock (gate)
                {
                    foreach (var invocation in signal)
                    {
                        if (live.GetValueOrDefault(invocation.TaskId) is not { State: RunningTaskState running } record) continue;
                        writes.Add(new TaskWrite(WithState(record, new PendingTaskState(running.Checkpoint))));
                    }
                }
                return writes;
            }).ConfigureAwait(false);
        }
        catch
        {
            // Reported by the Session's failure.
        }
    }

    /// <summary>
    /// Commits the abort mark, or settles a task that no registered definition can take as <c>orphaned</c> when nothing it owns is live, then
    /// joins the run invocation seen on the line; the commit listener signalled it. The abort invocation starts once the task's ordinary owned
    /// work is gone. A <c>completing</c> task is only marked. A request replaces a <c>restart</c> mark, so a task abandoned after a restart
    /// that waits for its definition is orphaned; with <paramref name="keepRestart"/>, as for an owner's own cleanup, such a task keeps its
    /// mark and waits on.
    /// </summary>
    public async Task<TaskAbortResult> AbortAsync(TaskId id, DurableContext callContext, bool keepRestart = false)
    {
        var (result, run) = await session.CommitWithAsync<(TaskAbortResult, Invocation?)>(async tx =>
        {
            var current = await tx.GetTaskAsync(id).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task {id} does not exist");
            if (current.State.Status == DurableTaskStatus.Terminal) return (TaskAbortResult.Terminal, null);
            Invocation? invocation;
            lock (gate) invocation = invocations.GetValueOrDefault(id);
            // A restart-marked task has no run invocation; it only waits for its abort.
            if (keepRestart && current.AbortReason == TaskAbortReason.Restart) return (TaskAbortResult.Marked, null);
            if (invocation is null && current.State.Status != DurableTaskStatus.Completing)
            {
                await LoadScopesAsync(false).ConfigureAwait(false);
                var owned = await RegionAsync(() => HasOwnedLiveLocked(id, null, null), ReloadScopes).ConfigureAwait(false);
                // Resolved once that decision stands, as it may run Migrate.
                if (!owned && Resolve(current, registry.Snapshot()) is { Blocked: { } reason })
                {
                    await TerminateAsync(tx, current, new OrphanedTaskOutcome(TaskInspectionState.ReasonName(reason))).ConfigureAwait(false);
                    return (TaskAbortResult.Marked, null);
                }
            }
            if (!current.AbortRequested || current.AbortReason is not null) tx.SetTask(WithAbortMark(current, restart: false));
            return (TaskAbortResult.Marked, invocation?.Mode == InvocationMode.Run ? invocation : null);
        }, callContext).ConfigureAwait(false);
        // The commit listener signalled the run; join it. A run that ignores its signal can outlive a failed Session, which ends the wait
        // instead.
        if (run is not null) await callContext.WaitAsync(Task.WhenAny(run.Done.Task, session.Failed).Unwrap()).ConfigureAwait(false);
        return result;
    }

    /// <summary>Resolves with the terminal receipt; checks and registers on the line so no terminal publication falls between them.</summary>
    public async Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext callContext)
    {
        var found = await session.ReadOnLineAsync(async line =>
        {
            lock (gate) if (closing) throw HarnessUtil.ClosedError(session);
            // A clean region found no unpublished writer of the task, and none is admitted while this job holds the line: no terminal
            // publication of it falls between the check and the registration.
            if (await RegionAsync(() => LiveRecordLocked(id) is not null).ConfigureAwait(false))
            {
                lock (gate)
                {
                    if (closing) throw HarnessUtil.ClosedError(session);
                    return taskWaiters.Add(id, callContext);
                }
            }
            var record = await line.GetTaskAsync(id, callContext).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task {id} does not exist");
            return Task.FromResult(record);
        }).ConfigureAwait(false);
        return await found.ConfigureAwait(false);
    }

    /// <summary>Resolves when ordinary traversal from the conversation, or from every ownerless conversation, reaches no live non-background
    /// task.</summary>
    public Task WaitForIdleAsync(ConversationId? conversationId, DurableContext callContext)
    {
        Task waiting;
        lock (gate)
        {
            if (closing) return Task.FromException(HarnessUtil.ClosedError(session));
            if (IdleLocked(conversationId)) return Task.CompletedTask;
            ScheduleReconcileLocked();
            waiting = idleWaiters.Add(conversationId, callContext);
        }
        return waiting;
    }

    /// <summary>
    /// <c>Conversation.AbortAsync()</c>: in one commit, withdraws the queued inputs and marks every live non-background task that ordinary
    /// traversal from the conversation reaches; resolves once the scope is idle. With <paramref name="background"/>, traversal crosses
    /// background boundaries, and the wait also covers every task it reached.
    /// </summary>
    public async Task AbortConversationAsync(ConversationId conversationId, bool background, DurableContext callContext)
    {
        var reached = await session.CommitWithAsync(async tx =>
        {
            await PublishedAsync().ConfigureAwait(false);
            var queued = await LoadScopesAsync(true).ConfigureAwait(false);
            var scope = Scope.Of(conversationId);
            var reached = new List<TaskId>();
            lock (gate)
            {
                foreach (var (_, node, record) in WalkDownLocked(Node.Of(conversationId), null, background, null))
                {
                    if (record is null || (node.Background && !background)) continue;
                    reached.Add(record.Id);
                    if (!record.AbortRequested || record.AbortReason is not null) tx.SetTask(WithAbortMark(record, restart: false));
                }
            }
            foreach (var id in queued)
            {
                bool inScope;
                lock (gate) inScope = InScopeLocked(Up.Of(id), scope, background) == true;
                if (inScope) await withdrawInputs(tx, id).ConfigureAwait(false);
            }
            return reached;
        }, callContext).ConfigureAwait(false);
        if (background) foreach (var id in reached) await WaitForTaskAsync(id, callContext).ConfigureAwait(false);
        await WaitForIdleAsync(conversationId, callContext).ConfigureAwait(false);
    }

    /// <summary>
    /// The ownership memberships a commit's task and conversation changes write (group commit §4.2; upstream <c>membershipKeys</c>), at its
    /// admission on the line: for each task created or made terminal, every node from its parent up to and including the nearest live task,
    /// or the root conversation, and then the roots too, conservatively, as a pending end may unlist the root a creation finds listed; for
    /// each conversation created under a task, that task; and the intent set when a change sets, upgrades, or clears a mark or a held or
    /// ended outcome other than <c>completed</c>. Loads the chains it walks; the nodes it installs stay until the commit is published.
    /// </summary>
    public ValueTask<IReadOnlyList<string>> MembershipKeysAsync(Transaction tx)
    {
        var tasks = tx.StagedTasks();
        var conversations = tx.StagedConversations();
        if (tasks.Count == 0 && conversations.TrueForAll(record => record.Owner is null)) return new([]);
        return new(MembershipKeysAsync(tx, tasks, conversations));
    }

    private async Task<IReadOnlyList<string>> MembershipKeysAsync(Transaction tx, List<TaskRecord> tasks, List<ConversationRecord> conversations)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var overlay = OverlayOf(tx);
        var created = new HashSet<TaskId>(tx.CreatedTaskIds());
        var reached = new Dictionary<string, ConversationId?>(StringComparer.Ordinal);
        foreach (var record in tasks)
        {
            TaskRecord? previous;
            lock (gate) previous = live.GetValueOrDefault(record.Id);
            if (IntentChanges(previous, record)) keys.Add(LineKeys.Intent);
            if (!created.Contains(record.Id) && record.State.Status != DurableTaskStatus.Terminal) continue;
            await LoadChainAsync(ParentOf(record), overlay).ConfigureAwait(false);
            if (await ChainKeysAsync(ParentNode(NodeOf(record)), overlay, keys, reached).ConfigureAwait(false) is not null) keys.Add(LineKeys.Roots);
        }
        foreach (var conversation in conversations)
        {
            if (conversation.Owner?.TaskId is not { } owner) continue;
            await LoadChainAsync(Up.Of(owner), overlay).ConfigureAwait(false);
            await ChainKeysAsync(Node.Of(owner), overlay, keys, reached).ConfigureAwait(false);
        }
        return [.. keys];
    }

    /// <summary>
    /// Adds the memberships from <paramref name="start"/> up to and including the nearest live task, or the root conversation, which it
    /// returns (upstream <c>#chainKeys</c>). <paramref name="reached"/> keeps, per node walked, where its walk ended, so a chain several tasks
    /// of a commit share is walked once. Whether an ancestor is live can change under a pending commit, so it is read after a touch of the
    /// ancestor's record: a pending end must not stop the chain early. Ownership fields are immutable, so not keyed.
    /// </summary>
    private async Task<ConversationId?> ChainKeysAsync(Node start, Overlay overlay, HashSet<string> keys, Dictionary<string, ConversationId?> reached)
    {
        var walked = new List<string>();
        ConversationId? root = null;
        Node? at = start;
        while (at is { } node)
        {
            var key = OwnedKey(node);
            // Walked up from here already, for another task of the commit.
            if (reached.TryGetValue(key, out var ended))
            {
                root = ended;
                break;
            }
            keys.Add(key);
            walked.Add(key);
            if (node.IsConversation)
            {
                bool known;
                TaskId? owner;
                lock (gate) known = EdgeLocked(node.Conversation, overlay, out owner);
                if (known && owner is null) root = node.Conversation;
                at = known && owner is { } task ? Node.Of(task) : null;
                continue;
            }
            TaskRecord? record;
            lock (gate) record = overlay.Tasks.GetValueOrDefault(node.Task);
            if (record is null)
            {
                if (session.Touch(LineKeys.Task(node.Task)) is { } wait)
                {
                    await wait.ConfigureAwait(false);
                    // It may have ended meanwhile, and its node been swept: load the chain above it again.
                    await LoadChainAsync(Up.Of(node.Task), overlay).ConfigureAwait(false);
                }
                lock (gate) record = live.GetValueOrDefault(node.Task);
            }
            if (record is not null && record.State.Status != DurableTaskStatus.Terminal) break;
            TaskNode? above;
            lock (gate) above = record is not null ? NodeOf(record) : settled.TryGetValue(node.Task, out var known) ? known : null;
            at = above is { } fields ? ParentNode(fields) : null;
        }
        foreach (var key in walked) reached[key] = root;
        return root;
    }

    // ─── Scheduling ────────────────────────────────────────────────────────

    private void Observe(ObservedCommit publication)
    {
        // A commit held in memory changes documents only.
        if (publication.IsMemory) return;
        var signal = new List<Invocation>();
        var changed = false;
        // Whether the commit created a task, and whether a running task that keeps running committed it; see Kick(bool).
        var createsTask = false;
        var keepsRunning = false;
        // The clock is host code, read before the lock: only a commit that appends entries can advance a kept range.
        var at = publication.Changes.Any(change => change is EntryWrite) ? now() : 0;
        // The waits a terminal commit resolves, resumed after host observers saw it, as the abort signals are.
        var resumes = new List<Action>();
        lock (gate)
        {
            AdvanceContextsLocked(publication, at);
            var updated = new List<TaskRecord>();
            var failed = new List<TaskId>();
            // Edges first, so the tasks a commit creates in a new conversation hang from it.
            foreach (var change in publication.Changes)
                if (change is ConversationWrite created && !edges.ContainsKey(created.Value.Id)) SetEdgeLocked(created.Value.Id, created.Value.Owner?.TaskId);
            foreach (var change in publication.Changes)
            {
                if (change is not TaskWrite write) continue;
                changed = true;
                var record = write.Value;
                var previous = live.GetValueOrDefault(record.Id);
                if (FailedOutcome(record) && (previous is null || !FailedOutcome(previous))) failed.Add(record.Id);
                TrackLocked(previous, record);
                if (record.State.Status == DurableTaskStatus.Terminal)
                {
                    failedMigrations.Remove(record.Id);
                    failFastChecks.Remove(record.Id);
                    if (taskWaiters.Take(record.Id, record) is { } resume) resumes.Add(resume);
                    // Its owner may finalize now.
                    ScheduleReconcileLocked();
                    continue;
                }
                if (record.AbortRequested && previous?.AbortRequested != true)
                {
                    cascadePending = true;
                    // Signal a run invocation of the newly marked task; its next step ends it.
                    if (invocations.TryGetValue(record.Id, out var invocation) && invocation.Mode == InvocationMode.Run) signal.Add(invocation);
                }
                // A request replacing a `restart` mark upgrades the marks of the work below.
                if (previous?.AbortReason is not null && record.AbortReason is null) cascadePending = true;
                var status = record.State.Status;
                if (status == DurableTaskStatus.Completing && previous?.State.Status != DurableTaskStatus.Completing)
                {
                    if (CancellationIntent(record)) cascadePending = true;
                    ScheduleReconcileLocked();
                }
                if (record.State is WaitingTaskState { Policy: JoinPolicy.FailFast } && previous?.State.Status != DurableTaskStatus.Waiting)
                {
                    failFastChecks.Add(record.Id);
                    ScheduleReconcileLocked();
                }
                // Its owners and place never change: what is above it is checked once, when it becomes live.
                if (previous is null) updated.Add(record);
                if (previous is null && status == DurableTaskStatus.Pending) createsTask = true;
                if (status == DurableTaskStatus.Running && previous?.State.Status == DurableTaskStatus.Running &&
                    invocations.TryGetValue(record.Id, out var running) && running.Mode == InvocationMode.Run) keepsRunning = true;
            }
            foreach (var id in failed)
            {
                if (!waiters.TryGetValue(id, out var waiting)) continue;
                foreach (var waiter in waiting)
                {
                    if (live.GetValueOrDefault(waiter)?.State is WaitingTaskState { Policy: JoinPolicy.FailFast })
                    {
                        failFastChecks.Add(waiter);
                        ScheduleReconcileLocked();
                    }
                }
            }
            foreach (var change in publication.Changes)
            {
                // A queued input below a cancelled owner is withdrawn, even after its cascade.
                if (change is not SubmissionWrite { Value: { Status: SubmissionStatus.Queued, Type: SubmissionType.Input } submission }) continue;
                // Only an owner with cancellation intent withdraws it; a later intent's mark runs the cascade then.
                if (intent.Count == 0) continue;
                var conversationId = submission.ConversationId;
                if (!KnownLocked(Node.Of(conversationId)) || BelowCancelledLocked(Up.Of(conversationId))) cascadePending = true;
            }
            foreach (var record in updated)
            {
                // Work created below a cancelled owner, even after its cascade, is aborted too.
                if (unloaded.Contains(record.Id)) ScheduleReconcileLocked();
                else if (!record.Background && !record.AbortRequested && BelowCancelledLocked(ParentOf(record))) cascadePending = true;
            }
            // A cascade that a reservation or step found pending runs with the next commit of any kind.
            if (cascadePending) ScheduleReconcileLocked();
            SweepLocked();
        }
        CountActive();
        // Task code, and what the waits resume, never runs before host observers saw the commit (upstream: the aborts follow in a
        // microtask and the waits in promise continuations, after the dispatch to host observers).
        if (resumes.Count > 0 || signal.Count > 0)
            session.AfterPublication(() =>
            {
                foreach (var resume in resumes) resume();
                foreach (var invocation in signal) Signal(invocation, null);
            });
        if (!changed) return;
        SettleIdle();
        Kick(onLine: createsTask && keepsRunning);
    }

    /// <summary>
    /// Extends each kept context range with the entries a stored commit appended to its conversation, so a later read needs no Storage scan
    /// (upstream <c>#advanceContexts</c>). Entries are published in ID order. Runs under the lock at the publication, which the context
    /// read installs its range under, so a range is either installed before a publication advances it or checked against the entries that
    /// publication's commit wrote (<see cref="ConversationContext.IsKnownTail"/>: the Session applied them to its tails before publishing).
    /// </summary>
    private void AdvanceContextsLocked(ObservedCommit publication, double at)
    {
        if (contexts.Count == 0) return;
        Dictionary<ConversationId, List<EntryRecord>>? appended = null;
        foreach (var change in publication.Changes)
        {
            if (change is not EntryWrite { Value: var entry } || !contexts.ContainsKey(entry.ConversationId)) continue;
            appended ??= [];
            if (appended.TryGetValue(entry.ConversationId, out var entries)) entries.Add(entry);
            else appended[entry.ConversationId] = [entry];
        }
        if (appended is null) return;
        foreach (var (conversationId, entries) in appended)
        {
            var kept = contexts[conversationId];
            // An idle range past its retention is dropped here too: entry-only commits do not settle idleness, and a host may decline the
            // expiry timer. ExpiresAt was fixed when it went idle, so no settings are read here.
            if (kept.IdleSince is not null && kept.ExpiresAt is { } expiresAt && expiresAt <= at)
            {
                contexts.Remove(conversationId);
                continue;
            }
            if (ConversationContext.AdvanceRange(kept.Range, entries) is { } range) kept.Range = range;
            else contexts.Remove(conversationId);
        }
    }

    /// <summary><c>settings.ContextRetentionMs</c>, or 0 when the host's settings throw. Kept contexts are only a cache, so dropping them is
    /// safe, while a throw here would escape commit listeners, reconciliation and the expiry timer.</summary>
    private double ContextRetentionMs()
    {
        try
        {
            return settings().ContextRetentionMs;
        }
        catch (Exception error)
        {
            report(error, SettingsError);
            return 0;
        }
    }

    /// <summary><c>settings.RestartLimit</c>, or the default when the host's settings throw (upstream <c>#restartLimit</c>).</summary>
    private double RestartLimit()
    {
        try
        {
            return settings().RestartLimit;
        }
        catch (Exception error)
        {
            report(error, SettingsError);
            return Agents.DefaultRestartLimit;
        }
    }

    /// <summary>Resolves idle waiters, and drops each kept context whose conversation has been idle for the retention period.</summary>
    private void SettleIdle()
    {
        var at = now();
        var retention = ContextRetentionMs();
        var resumes = new List<Action>();
        lock (gate)
        {
            foreach (var conversationId in idleWaiters.Keys())
                if (IdleLocked(conversationId) && idleWaiters.Take(conversationId, true) is { } resume) resumes.Add(resume);
            foreach (var (conversationId, kept) in contexts.ToList())
            {
                if (kept.IdleSince is { } since && at - since >= retention) contexts.Remove(conversationId);
                else if (!IdleLocked(conversationId)) kept.IdleSince = null;
                else if (retention > 0)
                {
                    if (kept.IdleSince is null)
                    {
                        kept.IdleSince = at;
                        kept.ExpiresAt = at + retention;
                    }
                }
                else contexts.Remove(conversationId);
            }
            ComputeExpiryLocked(retention);
        }
        // At a publication, after its host observers (upstream: promise continuations behind their dispatch); else at once.
        if (resumes.Count > 0)
            session.AfterPublication(() =>
            {
                foreach (var resume in resumes) resume();
            });
        ApplyExpiry();
    }

    /// <summary>Records when the earliest idle context expires, for <see cref="ApplyExpiry"/>, which the caller runs once out of the
    /// lock.</summary>
    private void ComputeExpiryLocked(double retention)
    {
        double? due = null;
        foreach (var kept in contexts.Values)
            if (kept.IdleSince is { } since && (due is null || since + retention < due)) due = since + retention;
        expiryDue = due;
    }

    /// <summary>
    /// Runs <see cref="SettleIdle"/> when the earliest idle context expires, on a background timer, which never keeps the process alive.
    /// Where the host declines it, none is kept: task changes alone check expiry. Never called under the lock.
    /// </summary>
    private void ApplyExpiry()
    {
        lock (timerGate)
        {
            double? due;
            bool closingNow;
            lock (gate)
            {
                due = expiryDue;
                closingNow = closing;
            }
            if (expiry is { } current && current.At == due) return;
            var replaced = expiry;
            expiry = null;
            Disarm(replaced);
            if (due is not { } when || closingNow) return;
            var armed = new ArmedTimer(when);
            expiry = armed;
            var disarm = timers.Background(() => Contained(() =>
            {
                lock (timerGate) if (ReferenceEquals(expiry, armed)) expiry = null;
                SettleIdle();
            }, ReportInfo.ListenerError), when - now());
            if (disarm is null)
            {
                if (ReferenceEquals(expiry, armed)) expiry = null;
                return;
            }
            armed.Disarm = disarm;
        }
    }

    /// <summary>Disarms a scheduler timer; the host's disarm is host code, and a throw is reported.</summary>
    private void Disarm(ArmedTimer? armed)
    {
        if (armed?.Disarm is { } disarm) Contained(disarm, SetTimerError);
    }

    /// <summary>Runs a timer callback or a host action, reporting a throw as <paramref name="info"/>: on a timer thread it would end the
    /// process.</summary>
    private void Contained(Action action, ReportInfo info)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            report(error, info);
        }
    }

    // ─── Ownership ───────────────────────────────────────────────────────────

    private void ScheduleReconcileLocked()
    {
        if (reconcileScheduled || closing || readOnly) return;
        reconcileScheduled = true;
        ThreadPool.UnsafeQueueUserWorkItem(static scheduler => _ = scheduler.ReconcileAsync(), this, preferLocal: false);
    }

    /// <summary>
    /// One commit that applies what committed records imply: abort marks below live owners with cancellation intent (spec §5.4),
    /// <c>failFast</c> marks (spec §5.5), withdrawn queued inputs below cancelled owners, and the final terminal record of every
    /// <c>completing</c> task whose ordinary owned work is gone. The durable records are the intent, so this also repairs whatever a crash
    /// left unapplied. Resolves idle waiters that the loaded edges decide.
    /// </summary>
    private async Task ReconcileAsync()
    {
        bool work;
        lock (gate)
        {
            reconcileScheduled = false;
            reconciling++;
            work = ReconcileWorkLocked();
            if (work) reconcilingWork++;
        }
        try
        {
            await session.CommitInternalAsync(async tx =>
            {
                bool cascade;
                lock (gate)
                {
                    if (closing) return true;
                    // Taken on the line, not when the pass starts: upstream's pass enqueues its commit in the microtask that takes the
                    // flag, so a commit that sets it later always has its own pass queued right behind this one. Here the pass it schedules
                    // can reach the line much later, while this one's walk already marks below an intent that commit published, and the
                    // owner can end before that pass withdraws the queued inputs below it.
                    cascade = cascadePending;
                }
                // A cascade scans the queued inputs, which waits for every unpublished commit: a request made before the scan is covered by
                // it, and none is published while the job holds the line after it. So the flag is cleared once the scan returned.
                var queued = await LoadScopesAsync(cascade).ConfigureAwait(false);
                if (cascade) lock (gate) cascadePending = false;
                var marks = await MarksAsync().ConfigureAwait(false);
                foreach (var (record, restart) in marks.Values) tx.SetTask(WithAbortMark(record, restart));
                var withdrawn = await RegionAsync(() => queued.FindAll(id => BelowCancelledLocked(Up.Of(id))), ReloadScopes).ConfigureAwait(false);
                foreach (var id in withdrawn) await withdrawInputs(tx, id).ConfigureAwait(false);
                await FinalizeAsync(tx).ConfigureAwait(false);
                return true;
            }, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // No extension code runs in this commit: a throw is a storage failure, a host callback, or a bug. None is fixed by running the
            // pass again, so it fails the Session, which reports it.
            FailSession(error);
        }
        finally
        {
            lock (gate)
            {
                reconciling--;
                if (work) reconcilingWork--;
            }
        }
        ScheduleWake();
        SettleIdle();
    }

    /// <summary>Whether a reconcile pass would have work: a cascade to derive, held outcomes to check, or <c>failFast</c> waits (upstream
    /// <c>#reconcileWork</c>).</summary>
    private bool ReconcileWorkLocked() => cascadePending || finalizeChecks.Count > 0 || failFastChecks.Count > 0;

    /// <summary>
    /// The abort marks committed records imply (spec §5.4, §5.5; upstream <c>#marks</c>): every live foreground task below a live owner with
    /// cancellation intent, which is its nearest one (a walk down from each owner with intent does not enter another, whose own walk covers
    /// what is below it), and every other live task in the <c>on</c> of a <c>failFast</c> waiter one of whose tasks failed. A cascade from an
    /// abandoned owner passes its <c>restart</c> reason on; any other intent marks, or upgrades a <c>restart</c> mark, as a request. Collected
    /// first, so a request wins over a <c>restart</c> mark of the same pass. Derived again on every pass, as loading edges can reveal a
    /// cancelled owner. Takes the <c>failFast</c> checks it read once its decision stands.
    /// </summary>
    private async Task<OrderedDictionary<TaskId, (TaskRecord Record, bool Restart)>> MarksAsync()
    {
        // Terminal tasks in a checked `on`, read from Storage (null: missing): each one's outcome is final.
        var ended = new Dictionary<TaskId, TaskRecord?>();
        while (true)
        {
            var plan = await RegionAsync(() => PlanMarksLocked(ended), ReloadScopes, plan =>
            {
                // Only the checks it read: a publication may have added others since.
                if (plan.Missing is null) foreach (var id in plan.Checked!) failFastChecks.Remove(id);
            }).ConfigureAwait(false);
            if (plan.Missing is not { } missing) return plan.Marks!;
            foreach (var id in missing) ended[id] = await session.LineStorage.GetTaskAsync(id, context).ConfigureAwait(false);
        }
    }

    /// <summary><see cref="MarksAsync"/>' decision region; the <c>failFast</c> members it needs from Storage first, when
    /// <paramref name="ended"/> lacks them (upstream <c>#planMarks</c>).</summary>
    private MarksPlan PlanMarksLocked(Dictionary<TaskId, TaskRecord?> ended)
    {
        var marks = new OrderedDictionary<TaskId, (TaskRecord Record, bool Restart)>();
        // A mark rewrites the whole record, so the record it copies is read as a task record, keyed: a commit of the task that is not
        // published yet makes the region wait and run again, rather than stage a mark over a stale checkpoint.
        void Mark(TaskRecord found, bool restart)
        {
            var record = LiveRecordLocked(found.Id)!;
            if (marks.TryGetValue(record.Id, out var staged) && !staged.Restart) return;
            if (record.AbortRequested && (record.AbortReason is null || restart)) return;
            marks[record.Id] = (record, restart);
        }
        Record(LineKeys.Intent);
        foreach (var id in intent)
        {
            var owner = LiveRecordLocked(id)!;
            // Only an owner abandoned after a restart, and nothing else, passes its reason on.
            var restart = owner.AbortReason == TaskAbortReason.Restart && !FailedOutcome(owner);
            foreach (var (_, node, record) in WalkDownLocked(Node.Of(id), null, false, next => !intent.Contains(next)))
                if (record is not null && !node.Background) Mark(record, restart);
        }
        List<TaskId> checks = [.. failFastChecks];
        List<TaskId>? missing = null;
        foreach (var id in checks)
        {
            if (LiveRecordLocked(id)?.State is not WaitingTaskState waiting) continue;
            var failed = false;
            foreach (var member in waiting.On)
            {
                var record = LiveRecordLocked(member);
                if (record is null && !ended.TryGetValue(member, out record)) (missing ??= []).Add(member);
                else if (record is not null && FailedOutcome(record)) failed = true;
            }
            if (!failed) continue;
            // Every other live task: the failed one keeps its own outcome.
            foreach (var member in waiting.On)
                if (LiveRecordLocked(member) is { } record && !FailedOutcome(record)) Mark(record, false);
        }
        return missing is not null ? new MarksPlan(null, null, missing) : new MarksPlan(marks, checks, null);
    }

    /// <summary>
    /// Writes the terminal record of every <c>completing</c> task without live ordinary owned work. Finalizing one can free its owner, so
    /// this repeats over the commit's candidates until nothing changes. A held scheduler outcome gets its Harness cleanup here. In rounds, one
    /// decision region each: the holders the tasks of one round free are checked once, in the next, so a holder of many tasks that end
    /// together is walked once, not once per task. The first round takes the <c>completing</c> tasks it checked once its decision stands.
    /// </summary>
    private async Task FinalizeAsync(Transaction tx)
    {
        var done = new HashSet<TaskId>();
        // The commit's candidates, kept in step with the terminal records each round stages.
        var overlay = OverlayOf(tx);
        List<TaskId>? round = null;
        while (true)
        {
            var ids = round;
            var plan = await RegionAsync(() => PlanRoundLocked(ids ?? [.. finalizeChecks], overlay, done), ReloadScopes, plan =>
            {
                // Only the checks it read: a publication may have added others since.
                if (ids is null) foreach (var id in plan.Checked) finalizeChecks.Remove(id);
            }).ConfigureAwait(false);
            foreach (var (record, terminal) in plan.Finalized)
            {
                tx.SetTask(terminal);
                lock (gate) overlay.Tasks[record.Id] = terminal;
                done.Add(record.Id);
                var outcome = ((CompletingTaskState)record.State).Outcome;
                // REMINDER: only the scheduler writes faulted and orphaned (spec §5.4); their cleanup waits for this commit.
                if (outcome is FaultedTaskOutcome or OrphanedTaskOutcome) await settleOutcome(tx, record, outcome).ConfigureAwait(false);
            }
            if (plan.Next.Count == 0) return;
            round = plan.Next;
        }
    }

    /// <summary>
    /// One finalize round's decision (upstream <c>#planRound</c>): which of <paramref name="ids"/> are <c>completing</c> without live
    /// ordinary owned work below them, in the commit's candidates with the round's own finalizations applied in order, and the holders above
    /// them to check next. Tasks finalized have no live work below them, so walks down skip them. It applies the round's terminal records to
    /// <paramref name="overlay"/> while it decides and takes them back before it returns, so a run again starts from the same candidates.
    /// </summary>
    private RoundPlan PlanRoundLocked(List<TaskId> ids, Overlay overlay, HashSet<TaskId> finalized)
    {
        var tasks = overlay.Tasks;
        // The tasks finalized so far, earlier rounds' and this one's, without copying the former.
        var round = new HashSet<TaskId>();
        bool Done(TaskId id) => round.Contains(id) || finalized.Contains(id);
        var ended = new List<(TaskRecord Record, TaskRecord Terminal, bool Staged)>();
        var next = new OrderedSet<TaskId>(iterations);
        // Links derived before a wait may miss what was published meanwhile: derived again for each run of the region.
        overlay.Links = null;
        try
        {
            foreach (var id in ids)
            {
                var staged = tasks.GetValueOrDefault(id);
                var record = staged ?? LiveRecordLocked(id);
                if (record?.State is not CompletingTaskState completing || Done(id)) continue;
                if (HasOwnedLiveLocked(id, overlay, Done)) continue;
                var terminal = WithState(record, new TerminalTaskState(completing.Outcome));
                tasks[id] = terminal;
                round.Add(id);
                ended.Add((record, terminal, staged is not null));
                var above = NearestLiveAboveLocked(NodeOf(record), overlay);
                if (above?.State.Status == DurableTaskStatus.Completing) next.Add(above.Id);
            }
        }
        finally
        {
            foreach (var (record, _, staged) in ended)
            {
                if (staged) tasks[record.Id] = record;
                else tasks.Remove(record.Id);
            }
        }
        return new RoundPlan([.. ended.Select(entry => (entry.Record, entry.Terminal))], [.. next], ids);
    }

    /// <summary>
    /// Loads the owner chains of the tasks in <see cref="unloaded"/> and, with <paramref name="queued"/>, of every conversation with queued
    /// inputs, on the Session line; returns the latter. Reads through the line's Storage, so it may run inside a commit callback.
    /// </summary>
    private async Task<List<ConversationId>> LoadScopesAsync(bool queued)
    {
        List<TaskId> pending;
        lock (gate) pending = [.. unloaded];
        foreach (var id in pending)
        {
            // An earlier load in this loop may have passed it.
            bool still;
            lock (gate) still = unloaded.Contains(id);
            if (still) await LoadChainAsync(Up.Of(id), null).ConfigureAwait(false);
        }
        if (!queued) return [];
        // A scan, which cannot be keyed: it waits for every unpublished commit.
        var submissions = await HarnessUtil.ScanAll(cursor => session.LineStorage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Queued },
            ScanPageSize, cursor, context)).ConfigureAwait(false);
        var conversations = submissions.Where(submission => submission.Type == SubmissionType.Input).Select(submission => submission.ConversationId)
            .Distinct().ToList();
        foreach (var id in conversations) await LoadChainAsync(Up.Of(id), null).ConfigureAwait(false);
        return conversations;
    }

    /// <summary>
    /// Loads the owner edges and task nodes from <paramref name="start"/> up to its ownerless root. A known task outside
    /// <see cref="unloaded"/> ends the walk, as its chain is loaded; the <see cref="unloaded"/> tasks passed have a loaded chain afterwards.
    /// Loading a node can reveal a cancelled owner, so it makes the next reconcile derive cascade marks. The nodes it installs stay until the
    /// job's commit is published (<see cref="Pin"/>).
    /// </summary>
    private async Task LoadChainAsync(Up start, Overlay? overlay)
    {
        var passed = new List<TaskId>();
        Up? at = start;
        while (at is { } current)
        {
            if (current.IsTask)
            {
                var id = current.Task;
                TaskNode? node;
                bool loaded;
                lock (gate)
                {
                    node = live.TryGetValue(id, out var record) ? NodeOf(record) : settled.TryGetValue(id, out var known) ? known : null;
                    // Created in this commit: the load from its own record covers what is above it.
                    if (node is null && overlay?.Tasks.ContainsKey(id) == true) break;
                    loaded = node is not null && !unloaded.Contains(id);
                }
                if (loaded) break;
                if (node is null)
                {
                    // Through the line's Storage: a task whose creation or end is not published yet waits for it.
                    var record = await session.LineStorage.GetTaskAsync(id, context).ConfigureAwait(false);
                    if (record is null) return;
                    node = NodeOf(record);
                    var terminal = record.State.Status == DurableTaskStatus.Terminal;
                    lock (gate)
                    {
                        if (terminal) SettleLocked(id, node.Value);
                        // A loaded node can reveal a cancelling owner: the next reconcile derives marks again.
                        ScheduleReconcileLocked();
                    }
                    if (terminal) Pin(Node.Of(id));
                }
                passed.Add(id);
                at = ParentOf(node.Value);
            }
            else
            {
                bool known;
                TaskId? owner;
                lock (gate) known = EdgeLocked(current.Conversation, overlay, out owner);
                if (!known)
                {
                    // Through the line's Storage: a conversation whose creation is not published yet waits for it, so a missing record is never
                    // mistaken for an ownerless one.
                    var record = await session.LineStorage.GetConversationAsync(current.Conversation, context).ConfigureAwait(false);
                    owner = record?.Owner?.TaskId;
                    lock (gate)
                    {
                        SetEdgeLocked(current.Conversation, owner);
                        // A loaded node can reveal a cancelling owner: the next reconcile derives marks again.
                        ScheduleReconcileLocked();
                    }
                    Pin(Node.Of(current.Conversation));
                }
                at = owner is { } task ? Up.Of(task) : null;
            }
        }
        lock (gate) foreach (var id in passed) unloaded.Remove(id);
    }

    private void SetEdgeLocked(ConversationId conversationId, TaskId? owner)
    {
        edges[conversationId] = owner;
        // The conversation now hangs from its owner, or is a root; with nothing below it, it waits for the sweep.
        RelistLocked(Node.Of(conversationId));
        DropIfUnneededLocked(Node.Of(conversationId));
    }

    /// <summary>Knows a terminal task a chain load read, so walks pass through it; its own chain is not loaded yet.</summary>
    private void SettleLocked(TaskId id, TaskNode node)
    {
        settled[id] = node;
        unloaded.Add(id);
        RelistLocked(Node.Of(id));
        DropIfUnneededLocked(Node.Of(id));
    }

    /// <summary>Queues an ended task or a conversation for the sweep when nothing is listed below it.</summary>
    private void DropIfUnneededLocked(Node node)
    {
        if (!node.IsConversation && live.ContainsKey(node.Task)) return;
        if (below.ContainsKey(node)) return;
        dropQueue.Add(node);
        ScheduleSweepLocked();
    }

    /// <summary>
    /// Sweeps in a job of its own on the Session line, after the operation underway: a rejected commit, one with nothing to write, and a read
    /// publish nothing, so the sweep at the end of <see cref="Observe"/> would not run for what they loaded.
    /// </summary>
    private void ScheduleSweepLocked()
    {
        if (sweepScheduled || closing) return;
        sweepScheduled = true;
        // Enqueued from the thread pool: on an idle line the job would start inline, inside this lock.
        ThreadPool.UnsafeQueueUserWorkItem(static scheduler =>
        {
            Task<bool> job;
            try
            {
                job = scheduler.session.ReadOnLineAsync(() =>
                {
                    lock (scheduler.gate)
                    {
                        scheduler.sweepScheduled = false;
                        scheduler.SweepLocked();
                    }
                    return Task.FromResult(true);
                });
            }
            catch
            {
                job = Task.FromResult(false);
            }
            // Rejected only once the Session is closing or failed, when nothing is kept anyway.
            _ = job.ContinueWith(static (failed, state) =>
            {
                _ = failed.Exception;
                var owner = (DurableTaskScheduler)state!;
                lock (owner.gate) owner.sweepScheduled = false;
            }, scheduler, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);
        }, this, preferLocal: false);
    }

    /// <summary>
    /// Drops the queued nodes that are still unneeded: not live, nothing listed below. Runs once a publication is fully tracked, and as a job of
    /// its own on the line, never inside an operation: work a commit creates below a chain its own callback loaded keeps that chain. A flat
    /// loop: unlisting already queued every node on the way up that lost its last listed node.
    /// </summary>
    private void SweepLocked()
    {
        foreach (var node in dropQueue)
        {
            // A pinned node is queued again once its pins go.
            if (below.ContainsKey(node) || pins.ContainsKey(node)) continue;
            if (node.IsConversation)
            {
                edges.Remove(node.Conversation);
                roots.Remove(node.Conversation);
            }
            else if (!live.ContainsKey(node.Task))
            {
                settled.Remove(node.Task);
                unloaded.Remove(node.Task);
            }
        }
        dropQueue.Clear();
    }

    /// <summary>
    /// Runs <paramref name="decide"/>, a decision region (group commit §6; upstream <c>#region</c>): reading scheduler memory under the lock
    /// and recording the keys it reads, staging nothing and changing no scheduler state. When a key it read has an unpublished writer, its
    /// result, or its error, is dropped: once that writer is published, <paramref name="refresh"/> reloads what the region needs and it runs
    /// again. A waiting job holds the line, so unpublished writes only shrink, and the runs converge. What a region read that had no
    /// unpublished writer gets none for the rest of the job, so its result stands while the job applies it. The keys are touched under the
    /// lock, which a publication takes to adopt its commit before it drops its keys (see the file header); <paramref name="apply"/> runs there
    /// too, once the result stands, for what the decision changes in the candidate sets that publications also change.
    /// </summary>
    private async Task<T> RegionAsync<T>(Func<T> decide, Func<Task>? refresh = null, Action<T>? apply = null)
    {
        while (true)
        {
            List<Task>? gates = null;
            T value = default!;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
            lock (gate)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                reading = keys;
                try
                {
                    value = decide();
                }
                catch (Exception thrown)
                {
                    error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown);
                }
                finally
                {
                    reading = null;
                }
                foreach (var key in keys)
                    if (session.Touch(key) is { } wait) (gates ??= []).Add(wait);
                if (gates is null && error is null) apply?.Invoke(value);
            }
            if (gates is null)
            {
                error?.Throw();
                return value;
            }
            await Task.WhenAll(gates).ConfigureAwait(false);
            if (refresh is not null) await refresh().ConfigureAwait(false);
        }
    }

    /// <summary>Loads the chains a region walks, again after it waited: what was published meanwhile may list new work (upstream
    /// <c>#reloadScopes</c>).</summary>
    private Task ReloadScopes() => LoadScopesAsync(false);

    /// <summary>In a decision region, notes that it reads <paramref name="key"/>; anywhere else, nothing. Under the lock.</summary>
    private void Record(string key) => reading?.Add(key);

    /// <summary>A live task's committed record, read by a decision region (upstream <c>#liveRecord</c>). Under the lock.</summary>
    private TaskRecord? LiveRecordLocked(TaskId id)
    {
        Record(LineKeys.Task(id));
        return live.GetValueOrDefault(id);
    }

    /// <summary>The key of the live work listed below a node (upstream <c>ownedKey</c>): a task ID, or <c>c</c> and a conversation ID.</summary>
    private static string OwnedKey(Node node) => node.IsConversation ? LineKeys.Owned("c" + node.Conversation) : LineKeys.Owned(node.Task);

    /// <summary>
    /// Keeps <paramref name="node"/>, which a chain load of the job on the line installed, until that job's commit is published or fails
    /// (upstream <c>#pin</c>): work it creates below the node needs it, and the sweep would drop it as unneeded before then. Not under the
    /// lock: off the line the release runs at once.
    /// </summary>
    private void Pin(Node node)
    {
        lock (gate) pins[node] = pins.GetValueOrDefault(node) + 1;
        session.OnJobEnd(() =>
        {
            lock (gate)
            {
                var count = pins[node] - 1;
                if (count > 0)
                {
                    pins[node] = count;
                    return;
                }
                pins.Remove(node);
                DropIfUnneededLocked(node);
            }
        });
    }

    /// <summary>
    /// On the line: waits until every commit admitted before is published, for a decision that reads what cannot be keyed: every live task
    /// (open, inspection) or every task a conversation abort reaches (upstream <c>#published</c>).
    /// </summary>
    private async Task PublishedAsync()
    {
        if (session.Touch(LineKeys.All) is { } wait) await wait.ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the scheduler's sets in line with a task's committed record. A task that ended may free the nearest live task above it: one
    /// holding its outcome finalizes, an abort-marked one runs its abort handler; its waiters may run.
    /// </summary>
    private void TrackLocked(TaskRecord? previous, TaskRecord record)
    {
        var id = record.Id;
        if (invocations.TryGetValue(id, out var invocation)) invocation.Record = record;
        if (record.State.Status == DurableTaskStatus.Terminal)
        {
            if (previous is not null && !closing && countsActive) activeDeltas.Add((-1, record.Kind));
            resumed.Remove(id);
        }
        else if (previous is null && !closing && countsActive)
        {
            activeDeltas.Add((1, record.Kind));
        }
        if (previous?.State is WaitingTaskState previousWait) foreach (var on in previousWait.On) DeleteFrom(waiters, on, id);
        if (record.State.Status == DurableTaskStatus.Terminal)
        {
            var node = NodeOf(record);
            live.Remove(id);
            sleeping.Remove(id);
            intent.Remove(id);
            runnable.Remove(id);
            finalizeChecks.Remove(id);
            settled[id] = node;
            // Before anything is forgotten: the walk up to the task it held passes the ended tasks between them. Its chain is loaded, as every
            // commit that ends a task loads the chains of `unloaded` first.
            ReleaseLocked(node, null);
            if (waiters.TryGetValue(id, out var waiting)) foreach (var waiter in waiting) runnable.Add(waiter);
            RelistLocked(Node.Of(id));
            DropIfUnneededLocked(Node.Of(id));
            return;
        }
        live[id] = record;
        if (previous is null)
        {
            if (!KnownLocked(ParentNode(NodeOf(record)))) unloaded.Add(id);
            RelistLocked(Node.Of(id));
        }
        if (CancellationIntent(record)) intent.Add(id);
        else intent.Remove(id);
        if (record.State.Status == DurableTaskStatus.Completing)
        {
            runnable.Remove(id);
            finalizeChecks.Add(id);
        }
        else
        {
            runnable.Add(id);
        }
        if (record.State is WaitingTaskState waitingState) foreach (var on in waitingState.On) AddTo(waiters, on, id);
        if (record.State.Status == DurableTaskStatus.Sleeping) sleeping.Add(id);
        else sleeping.Remove(id);
    }

    /// <summary>The nearest live task above ended ordinary work may be free now: one holding its outcome, or an aborting one.</summary>
    private TaskRecord? ReleaseLocked(TaskNode node, Overlay? overlay)
    {
        var above = NearestLiveAboveLocked(node, overlay);
        if (above?.State.Status == DurableTaskStatus.Completing) finalizeChecks.Add(above.Id);
        else if (above?.AbortRequested == true) runnable.Add(above.Id);
        return above;
    }

    /// <summary>
    /// Lists <paramref name="start"/> under its parent node in <see cref="below"/> while it is a live task or has nodes listed below it, and
    /// unlists it otherwise; then its parent, as far up as that changes. So <see cref="below"/> holds exactly the tree around live work; a
    /// parent that loses its last listed node is queued for the sweep. A loop, as ownership chains can be deeper than the call stack.
    /// </summary>
    private void RelistLocked(Node start)
    {
        var node = start;
        while (true)
        {
            var needed = (!node.IsConversation && live.ContainsKey(node.Task)) || below.ContainsKey(node);
            Node parent;
            if (node.IsConversation)
            {
                if (!edges.TryGetValue(node.Conversation, out var owner)) return;
                if (owner is null)
                {
                    if (needed) roots.Add(node.Conversation);
                    else roots.Remove(node.Conversation);
                    return;
                }
                parent = Node.Of(owner.Value);
            }
            else
            {
                if (live.TryGetValue(node.Task, out var record)) parent = ParentNode(NodeOf(record));
                else if (settled.TryGetValue(node.Task, out var known)) parent = ParentNode(known);
                else return;
            }
            var listed = below.TryGetValue(parent, out var children) && children.Contains(node);
            if (listed == needed) return;
            if (needed)
            {
                var first = children is null;
                AddTo(below, parent, node);
                if (!first) return;
            }
            else
            {
                DeleteFrom(below, parent, node);
                if (below.ContainsKey(parent)) return;
                DropIfUnneededLocked(parent);
            }
            node = parent;
        }
    }

    /// <summary>
    /// The nearest live task above ended ordinary work <paramref name="node"/>, in the overlay's view: the one that work held, if any. Work at
    /// or below a background task holds nothing above it.
    /// </summary>
    private TaskRecord? NearestLiveAboveLocked(TaskNode node, Overlay? overlay)
    {
        Record(LineKeys.Intent);
        for (var current = node; !current.Background;)
        {
            var owner = current.Owner;
            if (owner is null)
            {
                if (!EdgeLocked(current.ConversationId, overlay, out var edge) || edge is null) return null;
                owner = edge;
            }
            var id = owner.Value;
            var record = overlay?.Tasks.GetValueOrDefault(id) ?? LiveRecordLocked(id);
            if (record is not null && record.State.Status != DurableTaskStatus.Terminal) return record;
            if (record is not null) current = NodeOf(record);
            else if (settled.TryGetValue(id, out var above)) current = above;
            else return null;
        }
        return null;
    }

    /// <summary>Whether the chain above <paramref name="node"/> is loaded, without walking: known tasks outside <see cref="unloaded"/> have a
    /// loaded chain.</summary>
    private bool KnownLocked(Node node)
    {
        if (!node.IsConversation) return (live.ContainsKey(node.Task) || settled.ContainsKey(node.Task)) && !unloaded.Contains(node.Task);
        if (!edges.TryGetValue(node.Conversation, out var owner)) return false;
        return owner is null || KnownLocked(Node.Of(owner.Value));
    }

    /// <summary>Owner task of a conversation (null when ownerless); false while not loaded.</summary>
    private bool EdgeLocked(ConversationId id, Overlay? overlay, out TaskId? owner)
    {
        if (overlay is not null && overlay.Edges.TryGetValue(id, out owner)) return true;
        Record(LineKeys.Conversation(id));
        return edges.TryGetValue(id, out owner);
    }

    private TaskNode? NodeLocked(TaskId id, Overlay? overlay)
    {
        if (overlay is not null && overlay.Tasks.TryGetValue(id, out var candidate)) return NodeOf(candidate);
        if (live.TryGetValue(id, out var record)) return NodeOf(record);
        return settled.TryGetValue(id, out var node) ? node : null;
    }

    /// <summary>Walks up from <paramref name="start"/>: owner tasks and conversations, ending at an ownerless root or an edge not loaded
    /// yet.</summary>
    private IEnumerable<Step> AboveLocked(Up start, Overlay? overlay)
    {
        Up? at = start;
        while (at is { } current)
        {
            if (current.IsTask)
            {
                if (NodeLocked(current.Task, overlay) is not { } node)
                {
                    yield return Step.Unknown;
                    yield break;
                }
                yield return Step.OfTask(current.Task, node);
                at = ParentOf(node);
            }
            else
            {
                yield return Step.OfConversation(current.Conversation);
                if (!EdgeLocked(current.Conversation, overlay, out var owner))
                {
                    yield return Step.Unknown;
                    yield break;
                }
                at = owner is { } task ? Up.Of(task) : null;
            }
        }
    }

    /// <summary>
    /// The tasks below <paramref name="start"/> that a walk down reaches: those <see cref="below"/> lists and, under an overlay, the commit's
    /// new tasks, with its candidates in place of committed records. Background tasks are reached but not entered, unless
    /// <paramref name="crossBackground"/>, and neither are the tasks <paramref name="enter"/> rejects. A loop, as chains can be deeper than the
    /// call stack. Consume it under the lock.
    /// </summary>
    private IEnumerable<(TaskId Id, TaskNode Node, TaskRecord? Live)> WalkDownLocked(Node start, Overlay? overlay, bool crossBackground,
        Func<TaskId, bool>? enter)
    {
        var links = overlay is null ? null : LinksLocked(overlay);
        var pending = new Stack<Node>();
        pending.Push(start);
        while (pending.TryPop(out var at))
        {
            Record(OwnedKey(at));
            for (var source = 0; source < 2; source++)
            {
                OrderedSet<Node>? children = null;
                if (source == 0) below.TryGetValue(at, out children);
                else links?.TryGetValue(at, out children);
                if (children is null) continue;
                foreach (var child in children)
                {
                    if (child.IsConversation)
                    {
                        pending.Push(child);
                        continue;
                    }
                    var id = child.Task;
                    var record = overlay?.Tasks.GetValueOrDefault(id) ?? live.GetValueOrDefault(id);
                    TaskNode node;
                    if (record is not null) node = NodeOf(record);
                    else if (settled.TryGetValue(id, out var known)) node = known;
                    else continue;
                    var liveRecord = record is not null && record.State.Status != DurableTaskStatus.Terminal ? record : null;
                    yield return (id, node, liveRecord);
                    if ((crossBackground || !node.Background) && (enter is null || enter(id))) pending.Push(child);
                }
            }
        }
    }

    /// <summary>
    /// The links walks down need for the commit's new tasks: each one under its parent node, then each node above it that
    /// <see cref="below"/> does not list yet, until one it lists, a live task (always listed), or the chain's end.
    /// </summary>
    private Dictionary<Node, OrderedSet<Node>> LinksLocked(Overlay overlay)
    {
        if (overlay.Links is { } cached) return cached;
        var links = new Dictionary<Node, OrderedSet<Node>>();
        foreach (var record in overlay.Tasks.Values)
        {
            if (live.ContainsKey(record.Id) || settled.ContainsKey(record.Id)) continue;
            var child = Node.Of(record.Id);
            Node? parent = ParentNode(NodeOf(record));
            while (parent is { } current && !(links.TryGetValue(current, out var listed) && listed.Contains(child)))
            {
                AddTo(links, current, child);
                if (!current.IsConversation && (live.ContainsKey(current.Task) || overlay.Tasks.ContainsKey(current.Task))) break;
                Node? above = null;
                if (current.IsConversation)
                {
                    if (EdgeLocked(current.Conversation, overlay, out var owner) && owner is { } ownerTask) above = Node.Of(ownerTask);
                }
                else if (settled.TryGetValue(current.Task, out var fields))
                {
                    above = ParentNode(fields);
                }
                if (above is { } next && below.TryGetValue(next, out var listedAbove) && listedAbove.Contains(current)) break;
                child = current;
                parent = above;
            }
        }
        overlay.Links = links;
        return links;
    }

    /// <summary>
    /// The live ordinary owned work of <paramref name="owner"/> (spec §5.5): the live non-background tasks below it that a walk down reaches
    /// without entering a background task; with <paramref name="first"/>, only the first. Candidates in <paramref name="overlay"/> replace
    /// committed records; the walk does not enter tasks <paramref name="done"/> holds, which have none. Owner chains must be loaded.
    /// </summary>
    private List<TaskId> OwnedLiveLocked(TaskId owner, Overlay? overlay, bool first, Func<TaskId, bool>? done)
    {
        var found = new List<TaskId>();
        var walk = WalkDownLocked(Node.Of(owner), overlay, false, done is null ? null : id => !done(id));
        foreach (var (id, node, record) in walk)
        {
            if (record is null || node.Background) continue;
            found.Add(id);
            if (first) break;
        }
        return found;
    }

    private bool HasOwnedLiveLocked(TaskId owner, Overlay? overlay, Func<TaskId, bool>? done) =>
        OwnedLiveLocked(owner, overlay, first: true, done).Count > 0;

    /// <summary>
    /// Whether ordinary traversal from <paramref name="scope"/> reaches <paramref name="start"/>: walking up reaches the scope's conversation,
    /// or an ownerless one for every root, without crossing a background owner, unless <paramref name="crossBackground"/>. Null while an edge
    /// is not loaded.
    /// </summary>
    private bool? InScopeLocked(Up start, Scope scope, bool crossBackground = false)
    {
        Record(LineKeys.Intent);
        foreach (var step in AboveLocked(start, null))
        {
            if (step.Kind == StepKind.Unknown) return null;
            if (step.Kind == StepKind.Conversation)
            {
                if (scope.Conversation is { } target && step.Conversation == target) return true;
            }
            else if (step.Node.Background && !crossBackground)
            {
                return false;
            }
        }
        return scope.Conversation is null;
    }

    /// <summary>
    /// Whether a live owner's cancellation intent reaches <paramref name="start"/>: walking up finds an owner with intent before a background
    /// owner without it. Terminal owners never cascade (spec §5.4).
    /// </summary>
    private bool BelowCancelledLocked(Up start) => CancellingOwnerLocked(start) is not null;

    /// <summary>The nearest live owner above <paramref name="start"/> whose cancellation intent reaches it, if any; see
    /// <see cref="BelowCancelledLocked"/>.</summary>
    private TaskRecord? CancellingOwnerLocked(Up start)
    {
        Record(LineKeys.Intent);
        // Nothing cancels, the common case: no walk.
        if (intent.Count == 0) return null;
        foreach (var step in AboveLocked(start, null))
        {
            if (step.Kind == StepKind.Unknown) return null;
            if (step.Kind != StepKind.Task) continue;
            if (live.TryGetValue(step.Task, out var owner) && CancellationIntent(owner)) return owner;
            if (step.Node.Background) return null;
        }
        return null;
    }

    /// <summary>
    /// Fails the Session, which reports it, for a throw in one of the scheduler's own commits; ignored once closing. Closing is the Session's:
    /// upstream's close seals admission and runs <see cref="Seal"/> in one synchronous call, but here a pass on another thread can have its
    /// commit refused ("Session is closed") before <see cref="Seal"/> has run, and that refusal must not fail the Session.
    /// </summary>
    private void FailSession(Exception error, DurableContext? failed = null)
    {
        bool closingNow;
        lock (gate) closingNow = closing;
        if (!closingNow && !session.Closing) session.Fail(error, failed);
    }

    /// <summary>Counts the active-task deltas taken so far in or out of <c>pi.durable.tasks.active</c> (upstream <c>#countActive</c>),
    /// outside the lock. Commit observers count at a commit's publication, which must not throw, so a throwing adapter is reported.</summary>
    private void CountActive()
    {
        (int Delta, string Kind)[] deltas;
        lock (gate)
        {
            if (activeDeltas.Count == 0) return;
            deltas = [.. activeDeltas];
            activeDeltas.Clear();
        }
        foreach (var (delta, kind) in deltas)
        {
            try
            {
                activeMetric.Add(delta, new SpanAttributes { ["task"] = kind });
            }
            catch (Exception error)
            {
                report(error, TelemetryError);
            }
        }
    }

    /// <summary>Close listener: runs synchronously once admission is sealed, before <see cref="JoinAsync"/>, or when the Session fails.</summary>
    private void Seal()
    {
        lock (gate)
        {
            if (!closing && countsActive) foreach (var record in live.Values) activeDeltas.Add((-1, record.Kind));
            resumed.Clear();
            closing = true;
            sealedInvocations.AddRange(invocations.Values);
            contexts.Clear();
            expiryDue = null;
            var error = HarnessUtil.ClosedError(session);
            taskWaiters.RejectAll(error);
            idleWaiters.RejectAll(error);
        }
        CountActive();
        try
        {
            registrySubscription?.Dispose();
        }
        catch (Exception error)
        {
            report(error, RegistryError);
        }
        lock (timerGate)
        {
            Disarm(expiry);
            expiry = null;
            Disarm(sleepTimer);
            sleepTimer = null;
        }
    }

    /// <summary>
    /// Recomputes the wake on the thread pool, never on the Session line (upstream <c>#scheduleWake</c>'s microtask): arms the built-in
    /// timer, or tells <c>OnWake</c> of a change. One job runs at a time; a change while it runs makes it run again.
    /// </summary>
    private void ScheduleWake()
    {
        lock (gate)
        {
            if (closing || readOnly) return;
            wakeDirty = true;
            if (wakeRunning) return;
            wakeRunning = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static scheduler => scheduler.RunWake(), this, preferLocal: false);
    }

    private void RunWake()
    {
        while (true)
        {
            bool timer;
            lock (gate)
            {
                if (!wakeDirty || closing)
                {
                    wakeRunning = false;
                    return;
                }
                wakeDirty = false;
                timer = enabled;
            }
            try
            {
                var wake = Wake();
                if (onWake is null)
                {
                    ArmTimer(timer ? wake.At : null);
                    continue;
                }
                if (lastWake == wake) continue;
                try
                {
                    onWake(wake);
                    // Only a delivered wake counts: after a throw, the next change or kick offers it again.
                    lastWake = wake;
                }
                catch (Exception error)
                {
                    report(error, OnWakeError);
                }
            }
            catch (Exception error)
            {
                report(error, ReportInfo.ListenerError);
            }
        }
    }

    /// <summary>
    /// Keeps the timer for the earliest sleep. It paces work, unlike the context expiry timer: a host that declines it gets the default
    /// timer. Rechecks the clock when it fires, so a wake before its time only arms it again.
    /// </summary>
    private void ArmTimer(double? at)
    {
        lock (timerGate)
        {
            if (sleepTimer is { } current && current.At == at) return;
            var replaced = sleepTimer;
            sleepTimer = null;
            Disarm(replaced);
            bool closingNow;
            lock (gate) closingNow = closing;
            if (at is not { } when || closingNow) return;
            var armed = new ArmedTimer(when);
            sleepTimer = armed;
            armed.Disarm = timers.Foreground(() => Contained(() =>
            {
                lock (timerGate) if (ReferenceEquals(sleepTimer, armed)) sleepTimer = null;
                var due = now();
                lock (gate) WakeDueLocked(due);
                Kick();
            }, ReportInfo.ListenerError), when - now());
        }
    }

    /// <summary>Makes every sleeping task whose time has come by <paramref name="at"/> a reservation candidate.</summary>
    private void WakeDueLocked(double at)
    {
        foreach (var id in sleeping)
            if (live.TryGetValue(id, out var record) && !NotDue(record, at)) runnable.Add(id);
    }

    /// <summary>A sleep whose time the clock has not reached at <paramref name="at"/>; an abort mark ends a sleep.</summary>
    private static bool NotDue(TaskRecord record, double at) =>
        record.State is SleepingTaskState asleep && !record.AbortRequested && asleep.Until > at;

    /// <summary>Schedules a reservation pass on the thread pool (upstream <c>#kick</c>).</summary>
    private void Kick() => Kick(onLine: false);

    /// <summary>
    /// Schedules a reservation pass (upstream <c>#kick</c>). From the listener of a commit by which a running task created a task and keeps
    /// running (<paramref name="onLine"/>), a pass of its own is enqueued inline unless a line pass is already queued behind the publishing
    /// job (it will see this commit), whether or not a drain is underway: the listener runs inside the publishing commit's line job, so the
    /// reservation commit is only enqueued, never run, and it lands ahead of anything the committer enqueues once its commit completes.
    /// That is upstream's order, where the listener's microtask (and a running drain's next pass, as the drain finishes on microtasks
    /// before the line's next job) comes before the committer's continuation: a task a commit creates is reserved before its creator's next
    /// commit. compaction-failure shows it: generation 22's prepare commit creates background compaction 23, whose reservation precedes
    /// 22's request commit. A thread-pool drain raced that continuation, as its loop's next pass did when a drain was still underway. A
    /// creator that waits or ends commits nothing more, so the thread-pool drain keeps its order there: its pass then usually finds the
    /// line idle and runs each invocation's step and phase inline in reservation order, as upstream starts them (a tools round's handlers
    /// start in call order).
    /// </summary>
    private void Kick(bool onLine)
    {
        ScheduleWake();
        lock (gate)
        {
            if (!enabled || closing)
            {
                dirty = true;
                return;
            }
            if (onLine)
            {
                if (linePassesQueued > 0) return;
                linePassesQueued++;
                linePassesRunning++;
            }
            else
            {
                dirty = true;
                if (draining) return;
                draining = true;
            }
        }
        // Never commit synchronously from a commit or registry listener: on the line, the pass only enqueues its commit.
        if (onLine) _ = ReservePassAsync();
        else ThreadPool.UnsafeQueueUserWorkItem(static scheduler => _ = scheduler.DrainAsync(), this, preferLocal: false);
    }

    /// <summary>One reservation pass outside the drain loop, for <see cref="Kick(bool)"/> on the line.</summary>
    private async Task ReservePassAsync()
    {
        try
        {
            List<Reservation> reservations;
            try
            {
                reservations = await ReserveAsync(linePass: true).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // As in DrainAsync, but never from inside the publishing job.
                await Task.Yield();
                FailSession(error);
                return;
            }
            await StartPassAsync(reservations).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) linePassesRunning--;
        }
        // A pass that only marked abandoned tasks asks for another (ReserveAsync sets dirty); the drain loop runs it.
        bool again;
        lock (gate) again = dirty;
        if (again) Kick();
    }

    /// <summary>
    /// Starts a reservation pass's invocations; one with abort invocations, as one segment of <see cref="abortDelivery"/>. Upstream starts
    /// each up to its first await, and from there every line result resumes its handler in the order the jobs ran, each running until it
    /// next waits: abort handlers started together reach the line in reservation order at every step. On the thread pool each result's
    /// continuation was scheduled on its own, and a later invocation whose job ran inline on an idle line went on at once: of a caller's two
    /// nested calls abandoned at a restart, each reading its owned calls and then settling, c1/2 could settle before c1/1. Abort handlers
    /// commit at once, so resuming them one at a time holds nothing up; run invocations, whose handlers are host code of any length, keep
    /// the thread pool.
    /// </summary>
    private Task StartPassAsync(List<Reservation> reservations)
    {
        if (!reservations.Exists(reservation => reservation.Invocation.Mode == InvocationMode.Abort))
        {
            StartPass(reservations);
            return Task.CompletedTask;
        }
        return abortDelivery.RunAsync(() => StartPass(reservations));
    }

    private void StartPass(List<Reservation> reservations)
    {
        // Upstream starts every reserved invocation before any of them gets past its first await, so an abort invocation reads its record
        // (and its abort reason) as the reservation committed it. A run invocation here may run its step and phase inline when the line is
        // idle, and its commits could change that record first: read the abort invocations' records before any invocation starts.
        lock (gate)
            foreach (var reservation in reservations)
                if (reservation.Invocation.Mode == InvocationMode.Abort) reservation.Record = live.GetValueOrDefault(reservation.Invocation.TaskId);
        // Abort invocations first. Upstream starts each reserved invocation up to its first await, so an abort handler that commits at once
        // (as most do) enqueues that commit before any run invocation of the pass gets past its first step. A run invocation here may run its
        // step and phase inline when the line is idle (a tool's whole recovery, say, settling it and cascading a request mark over a `restart`
        // mark of a task it owns), so starting it first could reorder those commits.
        foreach (var reservation in reservations)
            if (reservation.Invocation.Mode == InvocationMode.Abort) Start(reservation);
        foreach (var reservation in reservations)
            if (reservation.Invocation.Mode != InvocationMode.Abort) Start(reservation);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                lock (gate)
                {
                    if (!(dirty && enabled && !closing)) break;
                    dirty = false;
                }
                await StartPassAsync(await ReserveAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            // As in ReconcileAsync: the reservation commit runs no extension code.
            FailSession(error);
        }
        finally
        {
            bool again;
            lock (gate)
            {
                draining = false;
                again = dirty;
            }
            // A wakeup that arrived after the loop's last check still needs its pass.
            if (again) Kick();
        }
    }

    /// <summary>
    /// Reserves every eligible task in one commit; orphans abort-marked tasks no definition can take, unless abandoned after a restart. The
    /// first pass after open only abort-marks the abandoned tasks, so later passes see the marks.
    /// </summary>
    private async Task<List<Reservation>> ReserveAsync(bool linePass = false)
    {
        var reservations = new List<Reservation>();
        List<TaskId> marked = [];
        List<(TaskRecord Record, string Message)> faulted = [];
        var begun = false;
        try
        {
            await session.CommitInternalAsync(async tx =>
            {
                List<TaskId> pending;
                lock (gate)
                {
                    // From here on, a commit's listener needs a line pass of its own again.
                    if (linePass) linePassesQueued--;
                    begun = true;
                    if (!enabled || closing) return true;
                    pending = [.. abandoned];
                }
                if (pending.Count > 0)
                {
                    var unmarked = await RegionAsync(() => pending.Select(LiveRecordLocked).Where(record => record is { AbortRequested: false }).ToList())
                        .ConfigureAwait(false);
                    foreach (var record in unmarked) tx.SetTask(WithAbortMark(record!, restart: true));
                    marked = pending;
                    return true;
                }
                await LoadScopesAsync(false).ConfigureAwait(false);
                // Every pass picks up sleeps whose time has come: a pass started by any change may be the first to run after a deadline,
                // and the wake it schedules no longer keeps a timer for a due sleep.
                var at = now();
                lock (gate) WakeDueLocked(at);
                var plan = await RegionAsync(() => PlanReservationLocked(at), ReloadScopes, plan =>
                {
                    foreach (var id in plan.Dropped) runnable.Remove(id);
                    // Work below an owner with cancellation intent waits for its cascade mark instead of running a phase; schedule the
                    // cascade, which may not have run yet.
                    if (plan.Cascade)
                    {
                        cascadePending = true;
                        ScheduleReconcileLocked();
                    }
                }).ConfigureAwait(false);
                // Taken once per pass, and only when some task is a candidate.
                IRegistrySnapshot? snapshot = null;
                foreach (var record in plan.Ready)
                {
                    var id = record.Id;
                    var mode = record.AbortRequested ? InvocationMode.Abort : InvocationMode.Run;
                    // Work cut off restartLimit times in a row would run forever: an abort handler, or a phase of a kind that does not end
                    // itself at the limit, ends the task faulted instead. The settings are read only for a task that was cut off at all.
                    if (record.Restarts is { } restarts && restarts >= RestartLimit() && (mode == InvocationMode.Abort || !limitsRestarts(record.Kind)))
                    {
                        var message = mode == InvocationMode.Abort
                            ? $"Abort handler of task {id} ({record.Kind}) was cut off {restarts} times"
                            : $"Task {id} ({record.Kind}) was cut off {restarts} times without progress";
                        await TerminateAsync(tx, record, new FaultedTaskOutcome(new TaskOutcomeError(message, RestartLimitDetail))).ConfigureAwait(false);
                        faulted.Add((record, message));
                        continue;
                    }
                    snapshot ??= registry.Snapshot();
                    var resolution = Resolve(record, snapshot);
                    if (resolution.Blocked is { } reason)
                    {
                        // An abandoned task waits for its definition, so its abort handler can clean up.
                        if (mode == InvocationMode.Abort && record.AbortReason is null)
                            await TerminateAsync(tx, record, new OrphanedTaskOutcome(TaskInspectionState.ReasonName(reason))).ConfigureAwait(false);
                        continue;
                    }
                    var resolved = resolution.Record!;
                    if (!ReferenceEquals(resolved, record) || record.State.Status != DurableTaskStatus.Running)
                        tx.SetTask(WithState(resolved, new RunningTaskState(((LiveTaskState)resolved.State).Checkpoint)));
                    // Registered on the line, so marks and later reservations see it and close joins it; End makes it a candidate again.
                    lock (gate) runnable.Remove(id);
                    var invocation = CreateInvocation(record, mode, InstallerOf(snapshot, resolution.Task));
                    reservations.Add(new Reservation(invocation, resolution.Task!, snapshot));
                }
                return true;
            }, context, inline: !linePass).ConfigureAwait(false);
        }
        catch
        {
            if (linePass && !begun) lock (gate) linePassesQueued--;
            foreach (var reservation in reservations)
            {
                lock (gate)
                {
                    invocations.Remove(reservation.Invocation.TaskId);
                    runnable.Add(reservation.Invocation.TaskId);
                }
                reservation.Invocation.Done.TrySetResult();
            }
            throw;
        }
        foreach (var (record, message) in faulted)
            report(new InvalidOperationException(message), AboutRecord(ReportKind.TaskFaulted, record) with { Reason = ReportReason.RestartLimit });
        // Marked, or already marked or gone: done with them. Reserve again, now seeing the marks.
        lock (gate)
        {
            foreach (var id in marked) abandoned.Remove(id);
            if (marked.Count > 0) dirty = true;
        }
        return reservations;
    }

    /// <summary>
    /// A reservation pass's decision region (upstream <c>#planReservation</c>): the candidates it drops (gone, holding, waiting, asleep, or
    /// with an invocation), whether one waits for a cascade, and the runnable records to resolve. Iterating the candidates is not keyed: a
    /// pass that misses a task a pending commit makes runnable only delays it, as does skipping one with an invocation without reading its
    /// record, or a waiter's check whether what it waits on is still live. The drops are applied under the lock with the decision
    /// (<see cref="RegionAsync{T}"/>'s apply): a publication that makes a dropped waiter runnable again between the two would be lost.
    /// </summary>
    private ReservationPlan PlanReservationLocked(double at)
    {
        var dropped = new List<TaskId>();
        var ready = new List<TaskRecord>();
        var cascade = false;
        foreach (var id in runnable)
        {
            if (invocations.ContainsKey(id))
            {
                dropped.Add(id);
                continue;
            }
            var record = LiveRecordLocked(id);
            // It returns once what it waits for ends, its record changes, or the work below it ends; a sleep at the first pass after its time
            // comes, which the timer, Resume(), or any change starts.
            if (record is null || record.State.Status == DurableTaskStatus.Completing || WaitingOnLocked(record, first: true).Count > 0 ||
                NotDue(record, at))
            {
                dropped.Add(id);
                continue;
            }
            if (!record.AbortRequested && !record.Background && BelowCancelledLocked(ParentOf(record)))
            {
                cascade = true;
                continue;
            }
            ready.Add(record);
        }
        return new ReservationPlan(dropped, cascade, ready);
    }

    /// <summary>
    /// Live tasks a task waits for before its next invocation: its live ordinary owned work when abort-marked, since abort runs bottom-up,
    /// otherwise the live part of the <c>on</c> of a wait.
    /// </summary>
    private List<TaskId> WaitingOnLocked(TaskRecord record, bool first = false)
    {
        if (record.AbortRequested) return OwnedLiveLocked(record.Id, null, first, null);
        if (record.State is not WaitingTaskState waiting) return [];
        if (!first) return [.. waiting.On.Where(live.ContainsKey)];
        foreach (var id in waiting.On) if (live.ContainsKey(id)) return [id];
        return [];
    }

    /// <summary>Resolves the record's definition by kind, migrating an older stored version. A migration runs host code, so this never runs
    /// under the lock.</summary>
    private Resolution Resolve(TaskRecord record, IRegistrySnapshot snapshot)
    {
        var fit = Fit(record, snapshot.GetTask(record.Kind));
        if (fit.Reason is { } reason) return new Resolution(null, null, reason);
        if (!fit.Migrates) return new Resolution(fit.Task, record, null);
        var definition = fit.Task!.Definition;
        try
        {
            if (definition.Migrate is null) throw MissingMigration(record, definition);
            var state = (LiveTaskState)record.State;
            var migrated = definition.Migrate(record.Input, state.Checkpoint, record.Version);
            TaskState next = state switch
            {
                WaitingTaskState waiting => new WaitingTaskState(migrated.Checkpoint, waiting.On, waiting.Policy),
                SleepingTaskState asleep => new SleepingTaskState(migrated.Checkpoint, asleep.Until),
                RunningTaskState => new RunningTaskState(migrated.Checkpoint),
                _ => new PendingTaskState(migrated.Checkpoint),
            };
            return new Resolution(fit.Task, record with { Version = definition.Version, Input = migrated.Input, State = next }, null);
        }
        catch (Exception error)
        {
            lock (gate) failedMigrations[record.Id] = (fit.Task, error);
            // Blocked now without a commit: the graph and the host's wake must hear it. Queued, as upstream's microtask: this runs on the line.
            ThreadPool.UnsafeQueueUserWorkItem(static scheduler => scheduler.NotifyBlocked(), this, preferLocal: false);
            ScheduleWake();
            report(error, new ReportInfo(ReportKind.MigrationFailed) { Task = ReportTaskOf(record), Extension = InstallerOf(snapshot, fit.Task) });
            return new Resolution(null, null, BlockedReason.MigrationFailed);
        }
    }

    private FitResult Fit(TaskRecord record, DurableTask? task) => Fit(record.Id, record.Version, task);

    private FitResult Fit(TaskId id, int recordVersion, DurableTask? task)
    {
        if (task is null) return new FitResult(null, false, BlockedReason.MissingTask, null);
        var version = task.Definition.Version;
        if (version == recordVersion) return new FitResult(task, false, null, null);
        if (version < recordVersion) return new FitResult(null, false, BlockedReason.TaskTooOld, null);
        lock (gate)
            if (failedMigrations.TryGetValue(id, out var failed) && ReferenceEquals(failed.Task, task))
                return new FitResult(null, false, BlockedReason.MigrationFailed, failed.Error);
        return new FitResult(task, true, null, null);
    }

    /// <summary>
    /// Scheduling state and every live task with its derived state, read on the Session line. Runs no task code: a pending migration shows as
    /// pending with <c>Migrates</c>, and only a migration the scheduler already tried, or one that cannot exist, shows as failed.
    /// </summary>
    public async Task<(HarnessScheduling Scheduling, ImmutableArray<TaskInspection> Tasks)> InspectAsync(IRegistrySnapshot snapshot)
    {
        await PublishedAsync().ConfigureAwait(false);
        await LoadScopesAsync(false).ConfigureAwait(false);
        var at = now();
        lock (gate)
        {
            var tasks = live.Values.Select(record => new TaskInspection(record, InspectTaskLocked(record, snapshot, at))).ToImmutableArray();
            var scheduling = closing ? HarnessScheduling.Closing : enabled ? HarnessScheduling.Running : HarnessScheduling.Paused;
            return (scheduling, tasks);
        }
    }

    private TaskInspectionState InspectTaskLocked(TaskRecord record, IRegistrySnapshot snapshot, double at)
    {
        if (invocations.ContainsKey(record.Id)) return new TaskInspectionState(TaskInspectionKind.Running);
        if (record.State.Status == DurableTaskStatus.Completing) return new TaskInspectionState(TaskInspectionKind.Completing);
        var on = WaitingOnLocked(record);
        if (on.Count > 0) return new TaskInspectionState(TaskInspectionKind.Waiting) { On = [.. on] };
        if (NotDue(record, at)) return new TaskInspectionState(TaskInspectionKind.Sleeping) { Until = ((SleepingTaskState)record.State).Until };
        var fit = Fit(record, snapshot.GetTask(record.Kind));
        if (fit.Reason is { } reason) return new TaskInspectionState(TaskInspectionKind.Blocked) { Reason = reason, Error = fit.Error };
        if (fit.Migrates && fit.Task!.Definition.Migrate is null)
            return new TaskInspectionState(TaskInspectionKind.Blocked)
            {
                Reason = BlockedReason.MigrationFailed,
                Error = MissingMigration(record, fit.Task.Definition),
            };
        return new TaskInspectionState(TaskInspectionKind.Pending) { Migrates = fit.Migrates };
    }

    /// <summary>An invocation of <paramref name="record"/>, whose definition <paramref name="extension"/> installed (none for a built-in
    /// task).</summary>
    private Invocation CreateInvocation(TaskRecord record, InvocationMode mode, string? extension)
    {
        var controller = new CancellationSignal();
        var invocation = new Invocation(record.Id, record.Kind, record.ConversationId, extension, mode, controller, context.WithSignal(controller))
        {
            Record = record,
            Links = record.StartedAt is null ? record.Links : null,
        };
        lock (gate) invocations[record.Id] = invocation;
        return invocation;
    }

    private void Start(Reservation reservation)
    {
        _ = RunInvocationAsync(reservation);
    }

    private async Task RunInvocationAsync(Reservation reservation)
    {
        var invocation = reservation.Invocation;
        // Set in this async method, so it stays with the invocation's flow (see StartPassAsync).
        if (invocation.Mode == InvocationMode.Abort) OrderedLineDelivery.Current = abortDelivery;
        try
        {
            if (invocation.Mode == InvocationMode.Run) await RunAsync(reservation).ConfigureAwait(false);
            else await RunAbortAsync(reservation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            report(error, AboutTask(ReportKind.TaskError, invocation), invocation.Context);
        }
        finally
        {
            End(invocation);
            lock (gate)
            {
                invocation.Record = null;
                invocation.Links = null;
                invocation.Fault = null;
            }
            invocation.Done.TrySetResult();
            Kick();
        }
    }

    /// <summary>Runs phase handlers, each preceded by a step that decides on the line whether the invocation continues.</summary>
    private async Task RunAsync(Reservation reservation)
    {
        var invocation = reservation.Invocation;
        var state = new RunState(reservation.Task, reservation.Snapshot);
        var phase = new Phase(() => state.Snapshot, () => state.Task);
        var runtime = new Runtime(this, invocation, phase);
        bool Progress(Transaction tx, RunningTask task) => ProgressOf(tx, invocation, task, state);
        var current = await StepAsync(invocation, task => RulesLocked(task, null), Progress).ConfigureAwait(false);
        while (true)
        {
            // Close may seal between the decision and dispatch.
            bool closingNow;
            lock (gate) closingNow = closing;
            if (current is null || closingNow) return;
            var task = current;
            current = await PhaseAsync(invocation, task, async () =>
            {
                var checkpoint = task.Checkpoint;
                // Each phase handler resolves its agent afresh, at first use.
                phase.ResetAgent();
                PhaseResult previous;
                try
                {
                    var name = task.Phase;
                    var handler = name is not null && state.Task.Definition.Phases.TryGetValue(name, out var found) ? found
                        : throw new InvalidOperationException($"Task {task.Kind} has no phase {name ?? "undefined"}");
                    await handler(task, runtime, invocation.Context).ConfigureAwait(false);
                    previous = new PhaseResult(checkpoint, null);
                }
                catch (Exception error)
                {
                    previous = new PhaseResult(checkpoint, error);
                }
                return await StepAsync(invocation, next => RulesLocked(next, previous), Progress).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One phase invocation as a <c>pi.durable.phase</c> span (upstream <c>#phase</c>): it owns its decision commit, not the next handler, a
    /// durable wait, or recovery. Its parent is the task's stored trace, or the Harness telemetry; while it runs the invocation's context
    /// carries it. Its outcome is read from the record the invocation last saw and the fault it decided.
    /// </summary>
    private Task<T> PhaseAsync<T>(Invocation invocation, RunningTask task, Func<Task<T>> run)
    {
        var trace = task.Record.Trace;
        var parent = trace is null ? telemetry : telemetry.Propagation?.Extract(trace) ?? telemetry;
        var phaseName = task.Phase ?? "undefined";
        bool first;
        lock (gate) first = resumed.Remove(task.Id);
        var attributes = new SpanAttributes
        {
            ["task.id"] = task.Id.Value,
            ["task.name"] = task.Kind,
            ["phase"] = phaseName,
            ["kind"] = invocation.Mode == InvocationMode.Run ? "run" : "abort",
            ["resumed"] = first,
        };
        if (task.Checkpoint is JsonObjectValue checkpoint && checkpoint.TryGetValue("attempt", out var attempt) && attempt is JsonNumberValue number)
            attributes["attempt"] = number.Value;
        return TelemetrySchema.CreateTypedSpanStarter(parent, DurableTelemetry.Schema).StartSpanAsync(DurableTelemetry.PhaseSpanName, attributes,
            async (span, _) =>
            {
                if (span.Propagation is { } propagation && invocation.Links is { } links)
                    foreach (var carrier in links) span.AddLink(new SpanLink(propagation.Extract(carrier)));
                var previous = invocation.Context;
                invocation.Context = previous.WithTelemetry(span.Span);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    return await run().ConfigureAwait(false);
                }
                finally
                {
                    invocation.Context = previous;
                    TaskRecord record;
                    bool closingNow;
                    lock (gate)
                    {
                        record = invocation.Record!;
                        closingNow = closing;
                    }
                    var state = record.State;
                    var terminal = invocation.Fault ?? (state as SettlingTaskState)?.Outcome;
                    var outcome = terminal is not null
                        ? terminal.Status switch
                        {
                            TaskOutcomeStatus.Completed => "done",
                            TaskOutcomeStatus.Aborted => "aborted",
                            _ => "faulted",
                        }
                        : state.Status switch
                        {
                            DurableTaskStatus.Waiting => "waiting",
                            DurableTaskStatus.Sleeping => "sleeping",
                            _ when invocation.Ended || closingNow => "aborted",
                            _ => $"next={PhaseOf(((LiveTaskState)state).Checkpoint) ?? "undefined"}",
                        };
                    span.SetAttributes(new SpanAttributes { ["outcome"] = outcome });
                    var diagnostic = terminal switch
                    {
                        FaultedTaskOutcome faulted => faulted.Error.Message,
                        FailedTaskOutcome failed => failed.Error.Message,
                        OrphanedTaskOutcome orphaned => orphaned.Reason,
                        _ => null,
                    };
                    if (diagnostic is not null)
                        span.SetStatus(SpanStatus.Failed($"task_{WireName.Of(terminal!.Status)}", diagnostic.Length > 500 ? diagnostic[..500] : diagnostic));
                    else if (outcome == "aborted")
                        span.SetStatus(SpanStatus.Ok);
                    durationMetric.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        new SpanAttributes { ["task"] = task.Kind, ["phase"] = phaseName, ["outcome"] = outcome }, span.Span);
                }
            });
    }

    /// <summary>
    /// Precedence rules for a run invocation, a decision region of its step (upstream <c>#rules</c>). Rules 1 (terminal, <c>completing</c>,
    /// <c>waiting</c>, or <c>sleeping</c>) and 2 (closing) are applied by <see cref="StepAsync"/>. Progress (rule 5) is decided by
    /// <see cref="ProgressOf"/> once the decision stands, as it takes a registry snapshot.
    /// </summary>
    private StepPlan RulesLocked(RunningTask current, PhaseResult? previous)
    {
        // 3. abort mark: end; a fresh abort invocation starts once the task's ordinary owned work is gone.
        if (current.AbortRequested) return StepPlan.End;
        // An owner's cancellation intent ends it too, before its cascade marks it; reservation then holds it back.
        if (!current.Background && BelowCancelledLocked(ParentOf(current.Record))) return StepPlan.Cascade;
        if (previous is null) return StepPlan.Continue;
        // 4. uncaught error.
        if (previous.Failure is { } failure) return StepPlan.Fault(failure);
        // 6. no durable progress.
        if (JsonValue.DeepEquals(current.Checkpoint, previous.Checkpoint))
        {
            var phase = PhaseOf(previous.Checkpoint) ?? "undefined";
            return StepPlan.Fault(new InvalidOperationException($"Task {current.Kind} phase {phase} returned without durable progress"));
        }
        return StepPlan.Progress;
    }

    /// <summary>
    /// Rule 5, progress (upstream <c>#progress</c>): refreshes the snapshot; hands over to a replacement definition that can take the task,
    /// writing it back as <c>pending</c>, or keeps running under the old one, reported once per resolved definition. Returns whether the
    /// invocation continues.
    /// </summary>
    private bool ProgressOf(Transaction tx, Invocation invocation, RunningTask current, RunState state)
    {
        state.Snapshot = registry.Snapshot();
        var next = state.Snapshot.GetTask(current.Kind);
        if (ReferenceEquals(next, state.Task)) return true;
        if (next is not null && CanReserve(next, current.Record))
        {
            tx.SetTask(WithState(current.Record, new PendingTaskState(current.Checkpoint)));
            return false;
        }
        if (state.Reported is null || !ReferenceEquals(state.Reported.Task, next))
        {
            state.Reported = new ReportedTask(next);
            report(new InvalidOperationException($"Task {current.Id} keeps running under its old {current.Kind} definition"),
                AboutTask(ReportKind.TaskOutdated, invocation) with { Reason = OutdatedReason(next, current.Record) }, invocation.Context);
        }
        return true;
    }

    /// <summary>Runs the abort handler once; rules 1, 2 and 4 apply, and returning without an outcome faults.</summary>
    private async Task RunAbortAsync(Reservation reservation)
    {
        var invocation = reservation.Invocation;
        // The record as the reservation committed it, read before any invocation of the pass started (see StartPass).
        var found = reservation.Record;
        bool closingNow;
        lock (gate) closingNow = closing;
        if (found?.State is not RunningTaskState || closingNow) return;
        var current = new RunningTask(found);
        await PhaseAsync(invocation, current, async () =>
        {
            Exception? failure = null;
            try
            {
                var phase = new Phase(() => reservation.Snapshot, () => reservation.Task);
                var runtime = new Runtime(this, invocation, phase);
                var abort = reservation.Task.Definition.Abort ?? throw new InvalidOperationException($"Task {found.Kind} has no abort handler");
                await abort(current, runtime, invocation.Context).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error;
            }
            var message = $"Abort handler of task {invocation.TaskId} returned without a terminal outcome";
            return await StepAsync(invocation, _ => StepPlan.Fault(failure ?? new InvalidOperationException(message))).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// One decision on the Session line, before a phase: a decision region (<see cref="RegionAsync{T}"/>) that reads the task's record. A task
    /// that is no longer running (rule 1: terminal, <c>completing</c>, <c>waiting</c>, or <c>sleeping</c>) or a closing Harness (rule 2) ends
    /// the invocation without a write; otherwise <paramref name="plan"/> decides, under the lock, and a decision that stands is applied: the
    /// invocation continues, with <paramref name="progress"/> for rule 5, or ends, with a fault written. Ending happens inside the callback,
    /// before a fault's Harness cleanup. A rejected step, such as admission after close, also ends the invocation. Its outcome goes to the
    /// invocation only, so it is a scheduler commit (<see cref="Session.CommitInternalAsync{T}"/>).
    /// </summary>
    private async Task<RunningTask?> StepAsync(Invocation invocation, Func<RunningTask, StepPlan> plan,
        Func<Transaction, RunningTask, bool>? progress = null)
    {
        Exception? fault = null;
        try
        {
            var continued = await session.CommitInternalAsync(async tx =>
            {
                var (current, step) = await RegionAsync(() =>
                {
                    var found = LiveRecordLocked(invocation.TaskId);
                    if (found?.State is not RunningTaskState || closing) return ((RunningTask?)null, StepPlan.End);
                    var running = new RunningTask(found);
                    return (running, plan(running));
                }).ConfigureAwait(false);
                if (step.Kind == StepPlanKind.Continue) return current;
                if (step.Kind == StepPlanKind.Progress && progress!(tx, current!)) return current;
                End(invocation);
                if (step.Kind == StepPlanKind.Cascade)
                {
                    lock (gate)
                    {
                        cascadePending = true;
                        ScheduleReconcileLocked();
                    }
                }
                if (step.Kind == StepPlanKind.Fault)
                {
                    fault = step.Error!;
                    var faulted = new FaultedTaskOutcome(new TaskOutcomeError(DurableErrors.MessageOf(step.Error!)));
                    invocation.Fault = faulted;
                    await TerminateAsync(tx, current!.Record, faulted).ConfigureAwait(false);
                }
                return null;
            }, invocation.Context.WithoutCancellation()).ConfigureAwait(false);
            // Reported once the fault is durable: a host may count it, for example toward a breaker.
            if (fault is not null) report(fault, AboutTask(ReportKind.TaskFaulted, invocation) with { Reason = ReportReason.Threw }, invocation.Context);
            return continued;
        }
        catch (Exception error)
        {
            // The step writes the scheduler's own decision, a fault or a terminal record; a throw there would otherwise leave the task
            // running, to be reserved and run again.
            End(invocation);
            FailSession(error, invocation.Context);
            return null;
        }
    }

    /// <summary>
    /// Writes an outcome the scheduler decided. While the task's ordinary owned work is live it holds as <c>completing</c> and its Harness
    /// cleanup waits for the final commit (spec §5.5, rule 4); otherwise it is terminal with its cleanup.
    /// </summary>
    private async Task TerminateAsync(Transaction tx, TaskRecord record, TaskOutcome outcome)
    {
        await LoadScopesAsync(false).ConfigureAwait(false);
        if (await RegionAsync(() => HasOwnedLiveLocked(record.Id, OverlayOf(tx), null), ReloadScopes).ConfigureAwait(false))
        {
            tx.SetTask(WithState(record, new CompletingTaskState(outcome)));
            return;
        }
        tx.SetTask(WithState(record, new TerminalTaskState(outcome)));
        await settleOutcome(tx, record, outcome).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a running task's state with what it committed. A terminal state holds as <c>completing</c> while ordinary owned work is live,
    /// judged on the commit's candidates, so work the same commit creates below the task counts. A wait is validated first.
    /// </summary>
    private async Task CommitStateAsync(Transaction tx, Invocation invocation, RunningTask current, TaskState next)
    {
        NextTaskState.Check(next);
        if (next is WaitingTaskState waiting) await ValidateWaitAsync(tx, invocation, current, waiting.On, waiting.Policy).ConfigureAwait(false);
        if (next is SleepingTaskState asleep) ValidateSleep(invocation, current.Id, asleep.Until);
        if (next is TerminalTaskState terminal)
        {
            var overlay = OverlayOf(tx);
            await LoadScopesAsync(false).ConfigureAwait(false);
            foreach (var record in overlay.Tasks.Values) await LoadChainAsync(ParentOf(record), overlay).ConfigureAwait(false);
            if (await RegionAsync(() => HasOwnedLiveLocked(current.Id, OverlayOf(tx), null), ReloadScopes).ConfigureAwait(false))
            {
                tx.SetTask(WithState(Progressed(current.Record), new CompletingTaskState(terminal.Outcome)));
                return;
            }
        }
        // A state the task committed that differs from its current one is progress: its restart count starts over. The same checkpoint
        // committed again, for example as intent before an effect, is not.
        var same = JsonValue.DeepEquals(current.State.ToJson(), next.ToJson());
        tx.SetTask(WithState(same ? current.Record : Progressed(current.Record), next));
    }

    /// <summary>
    /// A wait names existing tasks, and <c>failFast</c> only tasks the waiter owns. It may not close a cycle in the wait-for graph, whose edges
    /// run from each task to what it waits on and to its live ordinary owned work (spec §5.5); waiting on itself or on an owner holding it are
    /// the shortest such cycles. An abort handler cannot wait.
    /// </summary>
    /// <remarks>Invariant: the wait-for graph is acyclic. Only a wait adds an edge between existing tasks, as a new task has only incoming
    /// edges, so checking each wait maintains it.</remarks>
    private async Task ValidateWaitAsync(Transaction tx, Invocation invocation, RunningTask current, ImmutableArray<TaskId> on, JoinPolicy policy)
    {
        if (invocation.Mode == InvocationMode.Abort) throw new InvalidOperationException($"Abort handler of task {current.Id} cannot wait");
        // Owned work is found by walks down, which need the chains of every live task loaded.
        await LoadScopesAsync(false).ConfigureAwait(false);
        // Tasks in `on` that are not live, read from Storage (null: missing).
        var ended = new Dictionary<TaskId, TaskRecord?>();
        while (true)
        {
            var checkedWait = await RegionAsync(() => CheckWaitLocked(current, on, policy, OverlayOf(tx), ended), ReloadScopes).ConfigureAwait(false);
            if (checkedWait.Missing is { } missing)
            {
                foreach (var id in missing) ended[id] = await session.LineStorage.GetTaskAsync(id, context).ConfigureAwait(false);
                continue;
            }
            if (checkedWait.Error is { } error) throw error;
            return;
        }
    }

    /// <summary><see cref="ValidateWaitAsync"/>'s decision region (upstream <c>#checkWait</c>): why the wait is refused, nothing when it may be
    /// stored, or the tasks in <paramref name="on"/> it needs from Storage first, when <paramref name="ended"/> lacks them.</summary>
    private (List<TaskId>? Missing, Exception? Error) CheckWaitLocked(RunningTask current, ImmutableArray<TaskId> on, JoinPolicy policy,
        Overlay overlay, Dictionary<TaskId, TaskRecord?> ended)
    {
        var targets = new List<TaskId>();
        List<TaskId>? missing = null;
        foreach (var id in on)
        {
            var member = overlay.Tasks.GetValueOrDefault(id) ?? LiveRecordLocked(id);
            if (member is null && !ended.TryGetValue(id, out member))
            {
                (missing ??= []).Add(id);
                continue;
            }
            if (member is null) return (null, new InvalidOperationException($"Task {id} does not exist"));
            if (policy == JoinPolicy.FailFast && member.Owner != current.Id)
                return (null, new InvalidOperationException($"Task {current.Id} can wait failFast only on tasks it owns; {id} is not one"));
            // A live child already holds the waiter (task-owned tasks are never background): waiting on it adds no edge. A terminal task has
            // no edges.
            if (member.State.Status != DurableTaskStatus.Terminal && member.Owner != current.Id) targets.Add(id);
        }
        if (missing is not null) return (missing, null);
        if (targets.Count == 0) return (null, null);
        var cycle = WaitCycleLocked(current.Id, targets, overlay);
        if (cycle is null) return (null, null);
        return (null, new InvalidOperationException(
            $"Task {current.Id} cannot wait on {cycle[0]}: {string.Join(" -> ", cycle.Prepend(current.Id))} is a wait cycle"));
    }

    /// <summary>
    /// The path from a member of <paramref name="on"/> back to <paramref name="task"/> in the wait-for graph, if the wait would close a cycle
    /// (spec §5.1). An edge X -> Y means X cannot settle before Y: Y is in the <c>on</c> of waiting X that is not abort-marked, or Y is live
    /// ordinary owned work of X, which holds X as <c>completing</c>. Each task is visited once and each subtree walked once, so a check is
    /// bounded by the live work it reaches. Part of a decision region: it records every record it reads. Owned work is found by walks down,
    /// which need the chains of every live task loaded.
    /// </summary>
    private List<TaskId>? WaitCycleLocked(TaskId task, IReadOnlyList<TaskId> on, Overlay overlay)
    {
        var from = new Dictionary<TaskId, TaskId?>();
        var walked = new HashSet<TaskId>();
        var pending = new Stack<TaskId>();
        void Reach(TaskId id, TaskId? via)
        {
            if (!from.TryAdd(id, via)) return;
            pending.Push(id);
        }
        List<TaskId> PathTo(TaskId end)
        {
            var steps = new List<TaskId>();
            for (TaskId? at = end; at is { } step; at = from[step]) steps.Insert(0, step);
            return steps;
        }
        foreach (var id in on) Reach(id, null);
        while (pending.TryPop(out var id))
        {
            if (id == task) return PathTo(id);
            var record = overlay.Tasks.GetValueOrDefault(id) ?? LiveRecordLocked(id);
            if (record is null || record.State.Status == DurableTaskStatus.Terminal) continue;
            // An abort-marked task waits only for its owned work, as abort runs bottom-up (WaitingOnLocked).
            if (record.State is WaitingTaskState waiting && !record.AbortRequested)
                foreach (var member in waiting.On) Reach(member, id);
            if (!walked.Add(id)) continue;
            // Below a walked task the walk does not go again: its own walk covered what lies below.
            var found = new List<TaskId>();
            foreach (var (belowId, node, liveRecord) in WalkDownLocked(Node.Of(id), overlay, false, next => !walked.Contains(next)))
            {
                if (liveRecord is null || node.Background) continue;
                found.Add(belowId);
                Reach(belowId, id);
            }
            foreach (var belowId in found) walked.Add(belowId);
        }
        return null;
    }

    /// <summary>Ends an invocation: its runtime operations reject from now on, its signal aborts, its watches stop, its timers are disarmed,
    /// and its task is free.</summary>
    private void End(Invocation invocation)
    {
        List<IWatchHandle<JsonObjectValue>> watches;
        List<Action> disarms = [];
        lock (gate)
        {
            if (invocation.Ended) return;
            invocation.Ended = true;
            if (invocations.TryGetValue(invocation.TaskId, out var registered) && ReferenceEquals(registered, invocation))
            {
                invocations.Remove(invocation.TaskId);
                if (live.ContainsKey(invocation.TaskId)) runnable.Add(invocation.TaskId);
            }
            watches = [.. invocation.Watches];
            foreach (var slot in invocation.Timers)
            {
                slot.Done = true;
                if (slot.Disarm is { } disarm) disarms.Add(disarm);
            }
            invocation.Timers.Clear();
        }
        foreach (var watch in watches) _ = watch.StopAsync();
        foreach (var disarm in disarms) Contained(disarm, SetTimerError);
        // Pending waits bound to the invocation, such as a tool's WaitForTaskAsync, reject with it.
        Signal(invocation, EndedError(invocation));
    }

    /// <summary>Aborts an invocation's controller. Its listeners run synchronously here, as an AbortSignal's do; a throwing listener is
    /// reported instead of escaping into the caller (a commit listener or close).</summary>
    private void Signal(Invocation invocation, Exception? reason)
    {
        try
        {
            invocation.Controller.Cancel(reason);
        }
        catch (Exception error)
        {
            report(error, ReportInfo.ListenerError);
        }
    }

    /// <summary>No live non-background task in the scope; a task whose owner edges are not loaded yet counts as inside.</summary>
    private bool IdleLocked(ConversationId? conversationId)
    {
        var scope = conversationId is { } id ? Scope.Of(id) : Scope.Roots;
        // In a decision region, global idle reads the roots it starts from.
        if (conversationId is null) Record(LineKeys.Roots);
        // Tasks whose chain is not loaded are not listed below their scope yet; one counts as inside unless its walk up decides otherwise.
        foreach (var taskId in unloaded)
            if (live.TryGetValue(taskId, out var record) && !record.Background && InScopeLocked(ParentOf(record), scope) != false) return false;
        IEnumerable<ConversationId> starts = conversationId is { } only ? [only] : roots;
        foreach (var start in starts)
            foreach (var (_, node, record) in WalkDownLocked(Node.Of(start), null, false, null))
                if (record is not null && !node.Background) return false;
        return true;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The record with an abort mark: <c>restart</c> for an abandonment; without a reason, a request, which replaces <c>restart</c>.
    /// As upstream's <c>{ ...rest, abortRequested: true, abortReason }</c>, a set reason is the record's last key. The restart count starts
    /// over: it now counts cut-offs of the abort handler.</summary>
    private static TaskRecord WithAbortMark(TaskRecord record, bool restart)
    {
        if (!restart) return record with { AbortRequested = true, AbortReason = null, Restarts = null };
        var keys = record.ToJson().Keys.Where(key => key is not "abortReason" and not "restarts").Append("abortReason").ToImmutableArray();
        return record with { AbortRequested = true, AbortReason = TaskAbortReason.Restart, Restarts = null, KeyOrder = keys };
    }

    /// <summary>A live owner's durable cancellation intent: its abort mark, or a held outcome other than completed.</summary>
    private static bool CancellationIntent(TaskRecord record) =>
        record.State.Status != DurableTaskStatus.Terminal && (record.AbortRequested || FailedOutcome(record));

    /// <summary>Whether the record holds or ends with an outcome other than completed.</summary>
    private static bool FailedOutcome(TaskRecord record) =>
        record.State is SettlingTaskState settling && settling.Outcome.Status != TaskOutcomeStatus.Completed;

    /// <summary>Whether replacing <paramref name="previous"/> (null: a new task) with <paramref name="next"/> changes the cancellation intent
    /// set or what it means: a mark set, upgraded, or cleared, or a held or ended outcome other than completed (upstream
    /// <c>intentChanges</c>).</summary>
    private static bool IntentChanges(TaskRecord? previous, TaskRecord next) =>
        (previous?.AbortRequested ?? false) != next.AbortRequested ||
        previous?.AbortReason != next.AbortReason ||
        (previous is not null && FailedOutcome(previous)) != FailedOutcome(next) ||
        (previous is not null && CancellationIntent(previous)) != CancellationIntent(next);

    private static Up ParentOf(TaskRecord record) => record.Owner is { } owner ? Up.Of(owner) : Up.Of(record.ConversationId);

    private static Up ParentOf(TaskNode node) => node.Owner is { } owner ? Up.Of(owner) : Up.Of(node.ConversationId);

    /// <summary>The node a task hangs from: its owner task, or its conversation.</summary>
    private static Node ParentNode(TaskNode node) => node.Owner is { } owner ? Node.Of(owner) : Node.Of(node.ConversationId);

    private static TaskNode NodeOf(TaskRecord record) => new(record.ConversationId, record.Owner, record.Background);

    private static Overlay OverlayOf(Transaction tx)
    {
        var tasks = new Dictionary<TaskId, TaskRecord>();
        foreach (var record in tx.StagedTasks()) tasks[record.Id] = record;
        var conversations = new Dictionary<ConversationId, TaskId?>();
        foreach (var record in tx.StagedConversations()) conversations[record.Id] = record.Owner?.TaskId;
        return new Overlay(tasks, conversations);
    }

    private void AddTo<TKey, TValue>(Dictionary<TKey, OrderedSet<TValue>> map, TKey key, TValue value) where TKey : notnull where TValue : notnull
    {
        if (!map.TryGetValue(key, out var values)) map[key] = values = new OrderedSet<TValue>(iterations);
        values.Add(value);
    }

    private static void DeleteFrom<TKey, TValue>(Dictionary<TKey, OrderedSet<TValue>> map, TKey key, TValue value) where TKey : notnull where TValue : notnull
    {
        if (!map.TryGetValue(key, out var values)) return;
        values.Remove(value);
        if (values.Count == 0) map.Remove(key);
    }

    /// <summary>Own memo entry only.</summary>
    private static JsonValue? MemoOf(TaskRecord? record, string name) =>
        record?.Memos is { } memos && memos.TryGetValue(name, out var value) ? value : null;

    private static InvalidOperationException MissingMigration(TaskRecord record, TaskDefinition definition) =>
        new($"Task {record.Kind} version {definition.Version} has no migration from {record.Version}");

    private static InvalidOperationException EndedError(Invocation invocation) => new($"Task {invocation.TaskId} invocation has ended");

    /// <summary>Replaces a live record's state; memos and the restart count disappear once an outcome is decided.</summary>
    private static TaskRecord WithState(TaskRecord record, TaskState state) =>
        state is SettlingTaskState ? record with { State = state, Memos = null, Restarts = null } : record with { State = state };

    /// <summary>The record without its restart count, as after a state the task committed (upstream <c>progressed</c>).</summary>
    private static TaskRecord Progressed(TaskRecord record) => record.Restarts is null ? record : record with { Restarts = null };

    /// <summary><c>{reason: "restart_limit"}</c>, the detail of an outcome that ends a task cut off <c>settings.RestartLimit</c> times.</summary>
    internal static JsonObjectValue RestartLimitDetail { get; } = new RecordJson.Writer().Add("reason", "restart_limit").Build();

    /// <summary>
    /// Terminal states a built-in task commits because it was cut off <c>settings.RestartLimit</c> times, when their outcome does not say so
    /// (a failure with detail <c>{reason: "restart_limit"}</c> does), with the message the scheduler reports them <c>task_interrupted</c> with
    /// (upstream <c>endedByCrashesStates</c>). Keyed by the state instance the task returns, so nothing is added to the stored state.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TaskState, string> EndedByCrashesStates = [];

    /// <summary><paramref name="state"/>, marked as ending its task for crashes (<c>task_interrupted</c> with <paramref name="message"/>),
    /// whatever its outcome (upstream <c>endedByCrashes</c>).</summary>
    internal static T EndedByCrashes<T>(T state, string message) where T : TaskState
    {
        EndedByCrashesStates.AddOrUpdate(state, message);
        return state;
    }

    /// <summary>Why a failed outcome's error says crashes ended the task (its detail's <c>reason</c>), if it does (upstream
    /// <c>interruptionOf</c>).</summary>
    private static ReportReason? InterruptionOf(TaskOutcomeError error) =>
        error.Detail is JsonObjectValue detail && detail.TryGetValue("reason", out var reason) && reason is JsonStringValue name
            ? name.Value switch { "restart_limit" => ReportReason.RestartLimit, "interrupted" => ReportReason.Interrupted, _ => null }
            : null;

    /// <summary>Whether a definition can take the task at reservation: same version, or newer with a migration.</summary>
    private static bool CanReserve(DurableTask task, TaskRecord record)
    {
        var definition = task.Definition;
        return definition.Version == record.Version || (definition.Version > record.Version && definition.Migrate is not null);
    }

    private static string? PhaseOf(JsonValue checkpoint) =>
        checkpoint is JsonObjectValue value && value.TryGetValue("phase", out var phase) && phase is JsonStringValue name ? name.Value : null;

    // ─── Reports ─────────────────────────────────────────────────────────────

    private static readonly ReportInfo RegistryError = ReportInfo.HostError(HostCallback.Registry);
    private static readonly ReportInfo TelemetryError = ReportInfo.HostError(HostCallback.Telemetry);
    private static readonly ReportInfo SettingsError = ReportInfo.HostError(HostCallback.Settings);
    private static readonly ReportInfo OnWakeError = ReportInfo.HostError(HostCallback.OnWake);
    private static readonly ReportInfo SetTimerError = ReportInfo.HostError(HostCallback.SetTimer);

    /// <summary>What a report about an invocation's own code says of it (upstream <c>#aboutTask</c> of an invocation): the task, and the
    /// extension that installed the definition it runs.</summary>
    private static ReportInfo AboutTask(ReportKind kind, Invocation invocation) =>
        new(kind) { Task = ReportTaskOf(invocation), Extension = invocation.Extension };

    /// <summary>What a report about a stored task says of it (upstream <c>#aboutTask(recordTask(record))</c>): the task, and the extension
    /// that installed its current definition; none when the registry throws, which is reported where it is read for scheduling.</summary>
    private ReportInfo AboutRecord(ReportKind kind, TaskRecord record)
    {
        string? extension = null;
        try
        {
            var snapshot = registry.Snapshot();
            extension = InstallerOf(snapshot, snapshot.GetTask(record.Kind));
        }
        catch
        {
            // A report goes out without the name.
        }
        return new ReportInfo(kind) { Task = ReportTaskOf(record), Extension = extension };
    }

    /// <summary>The task an invocation's report is about (upstream <c>reportTask</c>).</summary>
    private static ReportTask ReportTaskOf(Invocation invocation) => new(invocation.TaskId, invocation.Kind, invocation.ConversationId);

    /// <summary>A stored task, as a report names it (upstream <c>reportTask(recordTask(record))</c>).</summary>
    private static ReportTask ReportTaskOf(TaskRecord record) => new(record.Id, record.Kind, record.ConversationId);

    /// <summary>The extension that installed <paramref name="task"/> in <paramref name="snapshot"/>, the last one that lists it; none for a
    /// built-in task (upstream <c>installerOf</c>).</summary>
    private static string? InstallerOf(IRegistrySnapshot snapshot, DurableTask? task)
    {
        if (task is null) return null;
        var installed = snapshot.Installed();
        for (var index = installed.Count - 1; index >= 0; index--)
        {
            if (installed[index].Tasks is { } tasks && tasks.Any(listed => ReferenceEquals(listed, task))) return installed[index].Name;
        }
        return null;
    }

    /// <summary>Why a replacement definition cannot take a running task, in <see cref="Fit(TaskRecord, DurableTask?)"/>'s words: none is
    /// registered, it is older than the task, or it is newer without a migration (upstream <c>outdatedReason</c>).</summary>
    private static ReportReason OutdatedReason(DurableTask? task, TaskRecord record)
    {
        if (task is null) return ReportReason.MissingTask;
        return task.Definition.Version < record.Version ? ReportReason.TaskTooOld : ReportReason.MigrationFailed;
    }

    // ─── Types ───────────────────────────────────────────────────────────────

    private enum InvocationMode { Run, Abort }

    /// <summary>One in-memory execution of a task in run or abort mode.</summary>
    private sealed class Invocation(TaskId taskId, string kind, ConversationId conversationId, string? extension, InvocationMode mode,
        CancellationSignal controller, DurableContext context)
    {
        private static long started;
        /// <summary>When it started, among all invocations: the join writes back in this order, as upstream's insertion-ordered
        /// <c>#invocations</c> Map lists them (a Dictionary reuses the slots of removed entries).</summary>
        public long Started { get; } = Interlocked.Increment(ref started);
        public TaskId TaskId { get; } = taskId;
        public string Kind { get; } = kind;
        public ConversationId ConversationId { get; } = conversationId;
        /// <summary>The extension that installed the definition this invocation runs, at reservation; none for a built-in task.</summary>
        public string? Extension { get; } = extension;
        public InvocationMode Mode { get; } = mode;
        public CancellationSignal Controller { get; } = controller;
        private volatile DurableContext context = context;
        /// <summary>Context passed to handlers; cancelled by <see cref="Controller"/>. While a phase runs, it carries the phase span.</summary>
        public DurableContext Context { get => context; set => context = value; }
        /// <summary>Creation links, for the spans of a never-started task's first invocation; cleared when the invocation finishes.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, string>>? Links { get; set; }
        /// <summary>The task's record as last committed while the invocation was registered, so the terminal state stays available after it
        /// leaves the live index (upstream <c>invocation.record</c>). Guarded by the scheduler lock; released when the invocation finishes.</summary>
        public TaskRecord? Record { get; set; }
        /// <summary>The fault the invocation's step decided, whose terminal record is published after the invocation ended.</summary>
        public FaultedTaskOutcome? Fault { get; set; }
        /// <summary>Watches acquired through the runtime; stopped at invocation end. Guarded by the scheduler lock.</summary>
        public HashSet<IWatchHandle<JsonObjectValue>> Watches { get; } = [];
        /// <summary>Timers armed through the runtime; disarmed at invocation end. Guarded by the scheduler lock.</summary>
        public HashSet<TimerSlot> Timers { get; } = [];
        private volatile bool ended;
        public bool Ended { get => ended; set => ended = value; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>What a runtime reads for the phase handler it serves: the phase's snapshot and task, and its lazily resolved agent.</summary>
    private sealed class Phase(Func<IRegistrySnapshot> snapshot, Func<DurableTask> task)
    {
        private readonly Lock gate = new();
        private Task<Agent>? agent;
        public Func<IRegistrySnapshot> Snapshot { get; } = snapshot;
        public Func<DurableTask> Task { get; } = task;

        public void ResetAgent()
        {
            lock (gate) agent = null;
        }

        public Task<Agent> Agent(Func<Task<Agent>> resolve)
        {
            lock (gate)
            {
                if (agent is not null) return agent;
                agent = resolve();
                // A caller that stops waiting must not leave the shared resolution's failure unobserved.
                agent.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
                return agent;
            }
        }
    }

    private sealed class RunState(DurableTask task, IRegistrySnapshot snapshot)
    {
        public DurableTask Task { get; set; } = task;
        public IRegistrySnapshot Snapshot { get; set; } = snapshot;
        public ReportedTask? Reported { get; set; }
    }

    /// <summary>Replacement definition already reported as unable to take over, per invocation.</summary>
    private sealed record ReportedTask(DurableTask? Task);

    private sealed record Reservation(Invocation Invocation, DurableTask Task, IRegistrySnapshot Snapshot)
    {
        /// <summary>An abort invocation's committed record, read when its pass's invocations start (upstream's <c>#runAbort</c> reads it
        /// first thing).</summary>
        public TaskRecord? Record { get; set; }
    }

    /// <summary>Outcome of the phase that just returned, judged by the next step.</summary>
    private sealed record PhaseResult(JsonValue Checkpoint, Exception? Failure);

    private enum StepPlanKind { Continue, Progress, End, Cascade, Fault }

    /// <summary>Step decision, made in a decision region (upstream <c>StepPlan</c>): continue with the next phase, decide progress (rule 5)
    /// once the decision stands, end the invocation, end it and schedule the cascade an owner's intent implies, or end it by writing
    /// faulted.</summary>
    private readonly record struct StepPlan(StepPlanKind Kind, Exception? Error)
    {
        /// <summary>Continue with the next phase, the first one.</summary>
        public static StepPlan Continue => new(StepPlanKind.Continue, null);
        /// <summary>Rule 5: the phase made durable progress; <see cref="ProgressOf"/> decides once the decision stands.</summary>
        public static StepPlan Progress => new(StepPlanKind.Progress, null);
        /// <summary>End the invocation without a write.</summary>
        public static StepPlan End => new(StepPlanKind.End, null);
        /// <summary>End it: an owner's cancellation intent reaches it before its cascade did; schedule the cascade.</summary>
        public static StepPlan Cascade => new(StepPlanKind.Cascade, null);
        public static StepPlan Fault(Exception error) => new(StepPlanKind.Fault, error);
    }

    /// <summary><see cref="PlanMarksLocked"/>'s result: the marks and the <c>failFast</c> checks it read, or the members it needs from
    /// Storage first.</summary>
    private sealed record MarksPlan(OrderedDictionary<TaskId, (TaskRecord Record, bool Restart)>? Marks, List<TaskId>? Checked, List<TaskId>? Missing);

    /// <summary><see cref="PlanRoundLocked"/>'s result: the tasks it finalizes with their terminal records, the holders to check next, and the
    /// tasks it checked.</summary>
    private sealed record RoundPlan(List<(TaskRecord Record, TaskRecord Terminal)> Finalized, List<TaskId> Next, List<TaskId> Checked);

    /// <summary><see cref="PlanReservationLocked"/>'s result.</summary>
    private sealed record ReservationPlan(List<TaskId> Dropped, bool Cascade, List<TaskRecord> Ready);

    /// <summary>A definition that can take a record, or why none can; deciding it runs no task code.</summary>
    private readonly record struct FitResult(DurableTask? Task, bool Migrates, BlockedReason? Reason, Exception? Error);

    private readonly record struct Resolution(DurableTask? Task, TaskRecord? Record, BlockedReason? Blocked);

    /// <summary>The immutable ownership fields of a task.</summary>
    private readonly record struct TaskNode(ConversationId ConversationId, TaskId? Owner, bool Background);

    /// <summary>A node of the downward index: a task, or a conversation (upstream <c>TaskId | `c${number}`</c>).</summary>
    private readonly record struct Node(long Value, bool IsConversation)
    {
        public static Node Of(TaskId task) => new(task.Value, false);
        public static Node Of(ConversationId conversation) => new(conversation.Value, true);
        public TaskId Task => new(Value);
        public ConversationId Conversation => new(Value);
    }

    /// <summary>Where a walk up the ownership tree continues: an owner task, or a conversation.</summary>
    private readonly record struct Up(bool IsTask, TaskId Task, ConversationId Conversation)
    {
        public static Up Of(TaskId task) => new(true, task, default);
        public static Up Of(ConversationId conversation) => new(false, default, conversation);
    }

    private enum StepKind { Task, Conversation, Unknown }

    /// <summary>One step of a walk up: an owner task with its node, a conversation, or an owner edge that is not loaded yet.</summary>
    private readonly record struct Step(StepKind Kind, TaskId Task, TaskNode Node, ConversationId Conversation)
    {
        public static Step Unknown => new(StepKind.Unknown, default, default, default);
        public static Step OfTask(TaskId task, TaskNode node) => new(StepKind.Task, task, node, default);
        public static Step OfConversation(ConversationId conversation) => new(StepKind.Conversation, default, default, conversation);
    }

    /// <summary>Candidate records a commit staged; they override committed records in ownership walks.</summary>
    private sealed class Overlay(Dictionary<TaskId, TaskRecord> tasks, Dictionary<ConversationId, TaskId?> edges)
    {
        public Dictionary<TaskId, TaskRecord> Tasks { get; } = tasks;
        public Dictionary<ConversationId, TaskId?> Edges { get; } = edges;
        /// <summary>Links to the staged tasks <c>below</c> does not list, by parent node, for walks down; built at first use.</summary>
        public Dictionary<Node, OrderedSet<Node>>? Links { get; set; }
    }

    /// <summary>Where ordinary ownership traversal starts: one conversation, or every ownerless conversation.</summary>
    private readonly record struct Scope(ConversationId? Conversation)
    {
        public static Scope Roots => new(null);
        public static Scope Of(ConversationId conversation) => new(conversation);
    }

    private sealed class KeptContext(ContextRange range, double? idleSince)
    {
        /// <summary>Replaced when a publication advances it (AdvanceContextsLocked). Guarded by the scheduler's lock.</summary>
        public ContextRange Range { get; set; } = range;
        public double? IdleSince { get; set; } = idleSince;
        /// <summary>When an idle range expires, fixed when it went idle (upstream <c>expiresAt</c>), so a publication that advances it reads
        /// no settings.</summary>
        public double? ExpiresAt { get; set; }
    }

    /// <summary>A scheduler timer: when it is due, and its disarm once armed.</summary>
    private sealed class ArmedTimer(double at)
    {
        public double At { get; } = at;
        public Action? Disarm { get; set; }
    }

    /// <summary>A timer armed through a task runtime. Guarded by the scheduler lock: <see cref="Done"/> once disarmed, fired, or ended with
    /// its invocation.</summary>
    private sealed class TimerSlot
    {
        public Action? Disarm { get; set; }
        public bool Done { get; set; }
    }

    /// <summary>Counts the members the scheduler's sets yield; they are iterated under the scheduler lock.</summary>
    private sealed class IterationCounter
    {
        public long Entries;
    }

    /// <summary>An insertion-ordered set, as a JavaScript Set iterates: re-adding a member keeps its place; removal is O(1). Every member an
    /// iteration yields is counted.</summary>
    private sealed class OrderedSet<T>(IterationCounter counter) : IEnumerable<T> where T : notnull
    {
        private readonly Dictionary<T, LinkedListNode<T>> index = [];
        private readonly LinkedList<T> order = new();

        public int Count => index.Count;

        public bool Contains(T value) => index.ContainsKey(value);

        public bool Add(T value)
        {
            if (index.ContainsKey(value)) return false;
            index[value] = order.AddLast(value);
            return true;
        }

        public bool Remove(T value)
        {
            if (!index.Remove(value, out var node)) return false;
            order.Remove(node);
            return true;
        }

        public void Clear()
        {
            index.Clear();
            order.Clear();
        }

        public Enumerator GetEnumerator() => new(order.GetEnumerator(), counter);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            private LinkedList<T>.Enumerator inner;
            private readonly IterationCounter counter;

            internal Enumerator(LinkedList<T>.Enumerator inner, IterationCounter counter)
            {
                this.inner = inner;
                this.counter = counter;
            }

            public readonly T Current => inner.Current;

            readonly object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (!inner.MoveNext()) return false;
                Interlocked.Increment(ref counter.Entries);
                return true;
            }

            void IEnumerator.Reset() => ((IEnumerator)inner).Reset();

            public void Dispose() => inner.Dispose();
        }
    }

    // ─── Invocation runtime ──────────────────────────────────────────────────

    /// <summary>The task runtime of one invocation (upstream <c>#runtime</c>).</summary>
    private sealed class Runtime(DurableTaskScheduler scheduler, Invocation invocation, Phase phase) : ITaskRuntime, IHookRunner
    {
        public TaskId TaskId => invocation.TaskId;
        public ConversationId ConversationId => invocation.ConversationId;
        public CancellationToken Signal => invocation.Controller.Token;
        public IRegistrySnapshot Registry => phase.Snapshot();
        public Settings Settings => scheduler.settings();
        public IDurableModels Models => scheduler.models();
        public IHookRunner Hooks => this;

        /// <summary>Resolved at most once per phase handler, at first use, with the invocation's context; fixed for the phase.</summary>
        private Task<Agent> PhaseAgent()
        {
            if (invocation.Ended) return Task.FromException<Agent>(EndedError(invocation));
            return phase.Agent(() => scheduler.agent(invocation.ConversationId, phase.Snapshot(), invocation.Context));
        }

        public Task<Agent> AgentAsync(DurableContext context) =>
            invocation.Ended ? Task.FromException<Agent>(EndedError(invocation)) : context.WaitAsync(PhaseAgent());

        public async Task EachAsync<THooks, THandler>(Func<THooks, THandler?> select, Func<THandler, Task> invoke) where THooks : class where THandler : class
        {
            ArgumentNullException.ThrowIfNull(select);
            ArgumentNullException.ThrowIfNull(invoke);
            foreach (var (extension, handlers) in Agents.AgentHooks(await PhaseAgent().ConfigureAwait(false), phase.Task().Definition.Name))
            {
                if (handlers is not THooks typed || select(typed) is not { } handler) continue;
                try
                {
                    await invoke(handler).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (invocation.Controller.Token.IsCancellationRequested) throw;
                    scheduler.report(error, new ReportInfo(ReportKind.ExtensionError) { Extension = extension, Task = ReportTaskOf(invocation) },
                        invocation.Context);
                }
            }
        }

        public Task<IExecutionEnv?> EnvAsync(DurableContext context) => Read(() => scheduler.env(invocation.ConversationId, context));

        public async Task CommitAsync(Func<ITx, RunningTask, Task<TaskState?>> change, DurableContext context, CommitOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(change);
            string? checkpoint = null;
            (string Message, ReportReason Reason, int Restarts)? interrupted = null;
            await Gated(async (tx, current) =>
            {
                var next = await change(tx, current).ConfigureAwait(false);
                if (next is not null) await scheduler.CommitStateAsync(tx, invocation, current, next).ConfigureAwait(false);
                if (next is RunningTaskState running && PhaseOf(running.Checkpoint) is { } phase && phase != current.Phase) checkpoint = phase;
                // A task that ends for crashes says so (task_interrupted), so a host keeps a crash-loop signal.
                interrupted = null;
                if (next is TerminalTaskState terminal)
                {
                    var restarts = current.Record.Restarts ?? 0;
                    if (terminal.Outcome is FailedTaskOutcome failed && InterruptionOf(failed.Error) is { } reason)
                        interrupted = (failed.Error.Message, reason, restarts);
                    // A task that ended for crashes with another outcome says so itself, as a generation whose run answered ends
                    // completed without its after-run tasks.
                    else if (EndedByCrashesStates.TryGetValue(terminal, out var message))
                        interrupted = (message, ReportReason.RestartLimit, restarts);
                }
                return true;
            }, context, options).ConfigureAwait(false);
            if (interrupted is { } ended)
            {
                scheduler.report(new InvalidOperationException(ended.Message),
                    AboutTask(ReportKind.TaskInterrupted, invocation) with { Reason = ended.Reason, Restarts = ended.Restarts }, invocation.Context);
            }
            if (checkpoint is not null) invocation.Context.Telemetry.AddEvent("pi.durable.checkpoint", new SpanAttributes { ["phase"] = checkpoint });
        }

        public Task CommitAsync(Func<ITx, RunningTask, Task> change, DurableContext context, CommitOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(change);
            return CommitAsync(async (tx, current) =>
            {
                await change(tx, current).ConfigureAwait(false);
                return (TaskState?)null;
            }, context, options);
        }

        public Task CommitAsync(Func<ITx, RunningTask, TaskState?> change, DurableContext context, CommitOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(change);
            return CommitAsync((tx, current) => Task.FromResult(change(tx, current)), context, options);
        }

        public Task CommitAsync(Action<ITx, RunningTask> change, DurableContext context, CommitOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(change);
            return CommitAsync((tx, current) =>
            {
                change(tx, current);
                return Task.FromResult<TaskState?>(null);
            }, context, options);
        }

        public Task<JsonValue?> MemoAsync(string name, DurableContext context) => Read(() =>
        {
            lock (scheduler.gate) return Task.FromResult(MemoOf(scheduler.live.GetValueOrDefault(invocation.TaskId), name));
        });

        public Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            return Gated((tx, current) =>
            {
                if (MemoOf(current.Record, name) is { } winner) return Task.FromResult(winner);
                tx.SetTask(current.Record with { Memos = (current.Memos ?? JsonObjectValue.Empty).SetItem(name, candidate) });
                return Task.FromResult(candidate);
            }, context);
        }

        public Task SleepUntilAsync(double until, JsonValue checkpoint, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            return Gated(async (tx, current) =>
            {
                await scheduler.CommitStateAsync(tx, invocation, current, NextTaskState.Sleeping(checkpoint, until)).ConfigureAwait(false);
                return true;
            }, context);
        }

        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
            Read(() => scheduler.session.ReadOnLineAsync(line => line.GetTaskAsync(id, context)));

        public Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context) =>
            Read(() => scheduler.WaitForTaskAsync(id, context.WithSignal(invocation.Controller)));

        public Task<IReadOnlyList<TaskRecord>> OwnedTasksAsync(DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Read(() => scheduler.session.ReadOnLineAsync<IReadOnlyList<TaskRecord>>(async () =>
            {
                // The step that ends the invocation may have run on the line before this.
                if (invocation.Ended) throw EndedError(invocation);
                context.ThrowIfCancellationRequested();
                // Every live task is listed under its parent node, its owner task when it has one.
                var owned = await scheduler.RegionAsync(() =>
                {
                    scheduler.Record(OwnedKey(Node.Of(invocation.TaskId)));
                    var records = new List<TaskRecord>();
                    if (scheduler.below.TryGetValue(Node.Of(invocation.TaskId), out var children))
                        foreach (var child in children)
                            if (!child.IsConversation && scheduler.LiveRecordLocked(child.Task) is { } record && record.Owner == invocation.TaskId)
                                records.Add(record);
                    return records;
                }).ConfigureAwait(false);
                // The records are immutable: upstream's copies are not needed.
                owned.Sort(static (left, right) => left.Id.CompareTo(right.Id));
                return owned;
            }));
        }

        public Task AbortOwnedAsync(TaskId id, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Read(async () =>
            {
                var bound = context.WithSignal(invocation.Controller);
                var record = await scheduler.session.ReadOnLineAsync(line => line.GetTaskAsync(id, bound)).ConfigureAwait(false);
                if (record?.Owner != invocation.TaskId) throw new InvalidOperationException($"Task {id} is not owned by task {invocation.TaskId}");
                if (record.State.Status == DurableTaskStatus.Terminal) return true;
                await scheduler.AbortAsync(id, bound, keepRestart: true).ConfigureAwait(false);
                await scheduler.WaitForTaskAsync(id, bound).ConfigureAwait(false);
                return true;
            });
        }

        public Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(IReadOnlyList<TaskId> ids, DurableContext context) => Read(() =>
            scheduler.session.ReadOnLineAsync<IReadOnlyList<TaskOutcome>>(async line =>
            {
                var outcomes = new List<TaskOutcome>();
                foreach (var id in ids)
                {
                    var state = (await line.GetTaskAsync(id, context).ConfigureAwait(false))?.State;
                    if (state is not TerminalTaskState terminal) throw new InvalidOperationException($"Task {id} is not terminal");
                    outcomes.Add(terminal.Outcome);
                }
                return outcomes;
            }));

        public Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context) => Read(() =>
            scheduler.conversation(id, new InvocationBinding(invocation.Controller, () =>
            {
                if (invocation.Ended) throw EndedError(invocation);
            }), context));

        public Task<EntryRecord?> EntryAsync(EntryId id, DurableContext context) => ReadEntry(null, id, context);

        public Task<EntryRecord?> EntryAsync(Entry token, EntryId id, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(token);
            return ReadEntry(token, id, context);
        }

        public async Task<TypedEntry<TData>?> EntryAsync<TData>(Entry<TData> token, EntryId id, DurableContext context) where TData : JsonValue
        {
            ArgumentNullException.ThrowIfNull(token);
            return token.As(await ReadEntry(token, id, context).ConfigureAwait(false));
        }

        public Task<Page<EntryRecord>> EntriesAsync(EntryQueryBounds query, int limit, JsonObjectValue? cursor, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(query);
            return Read(() => scheduler.session.ReadOnLineAsync(line =>
                line.ScanEntriesAsync(HarnessUtil.ConversationEntries(invocation.ConversationId, query), limit, cursor, context)));
        }

        private Task<EntryRecord?> ReadEntry(Entry? token, EntryId id, DurableContext context) => Read(async () =>
        {
            // An entry of the conversation's kept context range is committed and visible: no Storage read.
            EntryRecord? entry;
            lock (scheduler.gate)
                entry = scheduler.contexts.GetValueOrDefault(invocation.ConversationId) is { } kept ? ConversationContext.RangeEntry(kept.Range, id) : null;
            entry ??= (await scheduler.session.ReadOnLineAsync(line => line.GetEntryAsync(invocation.ConversationId, id, context))
                .ConfigureAwait(false))?.Entry;
            return token is null || entry?.Kind == token.Kind ? entry : null;
        });

        public Task<ContextView> ContextAsync(ConversationId conversationId, DurableContext context, EntryId? at = null) => Read(async () =>
        {
            ContextRange? previous;
            lock (scheduler.gate) previous = scheduler.contexts.GetValueOrDefault(conversationId)?.Range;
            var (view, range) = await ConversationContext.ReadContextFromAsync(scheduler.session, scheduler.storage, conversationId, context, at, previous)
                .ConfigureAwait(false);
            var retention = scheduler.ContextRetentionMs();
            var stamp = scheduler.now();
            var expires = false;
            lock (scheduler.gate)
            {
                // Checked again here, under the lock up to the install below, which publications advance ranges under: a commit published
                // since the read returned was not seen by AdvanceContextsLocked, so a range that missed it must not be advanced.
                if (range is { Current: true } && !ConversationContext.IsKnownTail(scheduler.session, conversationId, range.Bounds.Tail))
                    range = range with { Current = false };
                // Keep it unless the invocation ended or a concurrent read already kept a newer range. A task no longer running ends its
                // invocation at the next step (rule 1), which upstream's microtask order commits before any commit that follows the task's;
                // here that step can queue behind them, so such an invocation counts as ended.
                var kept = scheduler.contexts.GetValueOrDefault(conversationId);
                var ended = invocation.Ended || scheduler.live.GetValueOrDefault(invocation.TaskId)?.State is not RunningTaskState;
                if (range is not null && !scheduler.closing && !ended && (kept is null || kept.Range.Bounds.Tail.Value <= range.Bounds.Tail.Value))
                {
                    // A read of another, idle conversation starts or continues its retention period.
                    if (!scheduler.IdleLocked(conversationId)) scheduler.contexts[conversationId] = new KeptContext(range, null);
                    else if (retention > 0)
                    {
                        // An idle period that already started keeps its expiry; a new one starts now.
                        var idleSince = kept?.IdleSince ?? stamp;
                        var expiresAt = kept?.IdleSince is not null && kept.ExpiresAt is { } fixedAt ? fixedAt : idleSince + retention;
                        scheduler.contexts[conversationId] = new KeptContext(range, idleSince) { ExpiresAt = expiresAt };
                        scheduler.ComputeExpiryLocked(retention);
                        expires = true;
                    }
                }
            }
            if (expires) scheduler.ApplyExpiry();
            return view;
        });

        public double Now()
        {
            if (invocation.Ended) throw EndedError(invocation);
            return scheduler.now();
        }

        public Action SetTimer(Action callback, double delayMs)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var slot = new TimerSlot();
            lock (scheduler.gate)
            {
                // An ended invocation's timers never fire.
                if (invocation.Ended) return static () => { };
                invocation.Timers.Add(slot);
            }
            var disarm = scheduler.timers.Foreground(() => scheduler.Contained(() =>
            {
                lock (scheduler.gate)
                {
                    slot.Done = true;
                    invocation.Timers.Remove(slot);
                }
                if (!invocation.Ended) callback();
            }, AboutTask(ReportKind.TaskError, invocation)), delayMs);
            bool done;
            lock (scheduler.gate)
            {
                done = slot.Done;
                if (!done) slot.Disarm = disarm;
            }
            // Ended with its invocation, or fired, while it was armed: End found nothing to disarm.
            if (done) scheduler.Contained(disarm, SetTimerError);
            return () =>
            {
                Action? armed;
                lock (scheduler.gate)
                {
                    armed = slot.Done ? null : slot.Disarm;
                    slot.Done = true;
                    invocation.Timers.Remove(slot);
                }
                armed?.Invoke();
            };
        }

        public void Report(Exception error, ReportFrom? from = null)
        {
            ArgumentNullException.ThrowIfNull(error);
            if (invocation.Ended) throw EndedError(invocation);
            // The task's own failure names the task's extension; any other names whose code failed, if anyone's.
            var task = ReportTaskOf(invocation);
            scheduler.report(error, from switch
            {
                null => AboutTask(ReportKind.TaskError, invocation),
                { Kind: ReportFromKind.Extension } => new ReportInfo(ReportKind.ExtensionError) { Task = task, Extension = from.Extension },
                _ => ReportInfo.HostError(from.Callback!.Value, task),
            }, invocation.Context);
        }

        // ─── Committed document reads and watches ───

        public Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => Read(() => scheduler.session.SnapshotAsync(token, context));
        public Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
            Read(() => scheduler.session.SnapshotAsync(token, conversationId, context));
        public Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
            Read(() => scheduler.session.SnapshotAsync(token, taskId, context));
        public Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
            Read(() => scheduler.session.SnapshotAsync(token, key, context));
        public Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
            Read(() => scheduler.session.SnapshotAsync(token, conversationId, key, context));
        public Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
            Read(() => scheduler.session.SnapshotAsync(token, taskId, key, context));
        public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context) =>
            Read(() => scheduler.session.SnapshotAsOfAsync(token, conversationId, at, context));
        public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
            DurableContext context) => Read(() => scheduler.session.SnapshotAsOfAsync(token, conversationId, key, at, context));

        public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocToken token, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, context));
        public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, conversationId, context));
        public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, taskId, context));
        public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, key, context));
        public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
            DurableContext context) => Watch(() => scheduler.session.WatchDocAsync(token, conversationId, key, context));
        public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, taskId, key, context));

        private async Task<IWatchHandle<JsonObjectValue>?> Watch(Func<Task<IWatchHandle<JsonObjectValue>?>> acquire)
        {
            if (invocation.Ended) throw EndedError(invocation);
            var watch = await acquire().ConfigureAwait(false);
            if (watch is null) return null;
            bool added;
            lock (scheduler.gate)
            {
                added = !invocation.Ended;
                if (added) invocation.Watches.Add(watch);
            }
            if (!added)
            {
                _ = watch.StopAsync();
                throw EndedError(invocation);
            }
            _ = watch.Closed.ContinueWith(_ =>
            {
                lock (scheduler.gate) invocation.Watches.Remove(watch);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
            return watch;
        }

        /// <summary>Runs a committed-state read unless the invocation has ended.</summary>
        private async Task<T> Read<T>(Func<Task<T>> read)
        {
            if (invocation.Ended) throw EndedError(invocation);
            return await read().ConfigureAwait(false);
        }

        /// <summary>Commits after rereading the task on the line and gating the invocation; <paramref name="options"/> may defer storing it.</summary>
        private Task<T> Gated<T>(Func<Transaction, RunningTask, Task<T>> change, DurableContext context, CommitOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (invocation.Ended) return Task.FromException<T>(EndedError(invocation));
            return scheduler.session.CommitWithAsync(async tx =>
            {
                lock (scheduler.gate)
                {
                    if (invocation.Ended) throw EndedError(invocation);
                    if (scheduler.closing) throw HarnessUtil.ClosedError(scheduler.session);
                }
                var current = await scheduler.RegionAsync(() =>
                {
                    var found = scheduler.LiveRecordLocked(invocation.TaskId) ??
                        throw new InvalidOperationException($"Task {invocation.TaskId} is terminal");
                    if (found.State.Status != DurableTaskStatus.Running)
                        throw new InvalidOperationException($"Task {invocation.TaskId} is {WireName.Of(found.State.Status)}");
                    if (invocation.Mode == InvocationMode.Run && found.AbortRequested)
                        throw new InvalidOperationException($"Task {invocation.TaskId} has a durable abort mark");
                    return new RunningTask(found);
                }).ConfigureAwait(false);
                // The step that ends it may have run while the region waited.
                if (invocation.Ended) throw EndedError(invocation);
                return await change(tx, current).ConfigureAwait(false);
            }, context, new TransactionScope(invocation.ConversationId, invocation.TaskId), options);
        }
    }

    /// <summary>A sleep's <c>until</c> is a time on the Harness clock; an abort handler cannot sleep (upstream <c>validateSleep</c>).</summary>
    private static void ValidateSleep(Invocation invocation, TaskId task, double until)
    {
        if (invocation.Mode == InvocationMode.Abort) throw new InvalidOperationException($"Abort handler of task {task} cannot sleep");
        if (!double.IsFinite(until) || until < 0)
            throw new ArgumentException($"Task {task} cannot sleep until {Internal.PiAi.Validation.Js.NumberToString(until)}: not a time in milliseconds");
    }
}
