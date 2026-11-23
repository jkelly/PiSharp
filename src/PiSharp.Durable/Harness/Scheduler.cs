// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/scheduler.ts. Named DurableTaskScheduler because
// System.Threading.Tasks.TaskScheduler is used throughout the assembly. Ported as a whole against the v1.2.0 text, and brought to v1.5.0 by
// the Pi v1.5.0 sync's areas below (area scheduler-idle last, with the v1.5.0 text as a whole): the ownership indexes
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
// - Upstream's queueMicrotask wakeups (#kick) become thread-pool work items, so nothing commits synchronously from a commit or registry
//   listener; #scheduleReconcile's pass only enqueues its commit, which never starts inline (below). The commit listener's #kick for a
//   commit by which a running task created a task and keeps running is the exception: it enqueues a reservation pass inline (the line is
//   busy with the publishing job, so it only enqueues), which keeps upstream's order of the new task's reservation ahead of its creator's
//   next commit (see Kick(bool)). Invocations then run their handlers on the thread pool, possibly in parallel with each other. The sweep
//   job (#scheduleSweep) is enqueued from the thread pool too, so it never starts inline inside the lock; it still runs after the operation
//   underway.
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
//   again, so OnWake calls never overlap and none is lost. One scheduled before the Session fails is taken at the failure (Seal, outside
//   the lock) and still delivered by the job, as upstream's microtask runs before a storage failure.
// - Walks up and down the ownership tree are loops, never recursion, as upstream's are: chains can be deeper than the stack.
//
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): the scheduler observes the Session's commits as memory adopts them
// (Session.FollowCommits) and skips a held one, which changes conversation documents only; ITaskRuntime.CommitAsync takes CommitOptions,
// so a phase may defer a commit (Live.ProgressCommit).
// Pi v1.5.0 sync (area harness-options): the models are a function read at each access (7ec1f648e), and the readOnly option (be14d9732):
// open loads live tasks as committed, running ones included, and schedules no reconcile; Resume throws ReadOnlySessionException; no
// reconcile is ever scheduled. Pi v1.5.0 sync (area vocabulary, 5f312dac4): the internal observeCommits is followCommits
// (Session.FollowCommits).
//
// Pi v1.5.0 sync (area sleep): durable sleeps, host timers and wake() (f75b3240a, ec1be23eb). A sleeping task holds no invocation:
// `sleeping` holds the sleeping live tasks, WakeDueLocked makes the due ones reservation candidates at resume, when the sleep timer fires
// and at every reservation pass, and a pass skips a sleep not yet due (an abort mark ends a sleep). Without OnWake the earliest sleep keeps
// a foreground timer while scheduling runs; with it, OnWake hears each change of Wake(). A read-only scheduler never schedules a wake and
// answers inactive (be14d9732). runtime.SleepUntilAsync commits the sleep; runtime.SetTimer's timers are disarmed at the invocation's end;
// the idle context expiry is a background timer. Each pass, inspection and wake reads the Harness clock once, outside the lock. A pass and
// the wake job read it on different threads, so a sleep can come due between the two: the wake job then asks for one more pass
// (lastPassAt), as neither would run it (a hang under load: the sleep stayed asleep with no timer). Wake()
// scanned the live tasks as f75b3240a's does until area scheduler-idle (below).
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
// reported host_error telemetry. Reports and the Session's failure from a step carry the invocation's context.
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
//   publication now runs on the writer's thread rather than inside a line job, and a job must not run inside it. It starts after the
//   publication's host observers (below).
// Pi v1.5.0 sync (area publication-caches: 8b0caefc2's publication order, e5c6182ba's kept ranges):
// - Kept context ranges advance at publication (AdvanceContextsLocked, upstream #advanceContexts): under the lock, a stored commit's
//   entries extend each kept range of their conversation that is current (ContextRange.Current), so a later read scans nothing
//   (ConversationContext.RangeEntry serves a single entry from it, as upstream's rangeEntry). A read installs its range under the same lock
//   only if its tail is still the conversation's newest known entry (ConversationContext.IsKnownTail, the Session's ConversationTails,
//   upstream knownLatestEntryId); otherwise it keeps it not current, and the next read extends it from Storage. A range's retention starts
//   when its conversation goes idle (KeptContext.ExpiresAt) and a read that reinstalls it keeps that expiry. Lock order: this lock, then the
//   tails' lock (IsKnownTail), never the reverse; ConversationTails calls nothing under its lock.
// - Nothing a publication sets off runs before the host observers saw the commit: Observe and SettleIdle take the task and idle waits
//   they resolve (Waiters.Take) and the runs they signal, and hand them to Session.AfterPublication, which runs them on the publishing
//   thread after its ObserveCommits observers (upstream: promise continuations and the abort microtask queued behind the host dispatch).
//   Reservation passes (Kick) and the reconcile pass (ScheduleReconcileLocked) start there too, so a commit a host observer makes is
//   admitted before theirs, and still before the committer's continuation. Outside a publication AfterPublication runs its work at once.
//   The reconcile pass only enqueues its commit (CommitInternalAsync without inline, as a line pass of Kick), so it starts where it is
//   asked for: after a publication's host observers, or at once under the lock, and its commit is on the line before the caller goes on,
//   as upstream's microtask enqueues it before anything a later turn does. Queued on the thread pool, a busy pool brought it to the line
//   while a later commit was stored, with the requests made meanwhile (a `completing` publication's too), and it stored the parent's
//   terminal record ahead of the commit that later commit's host observer made. The scheduler's other thread-pool work goes through Post,
//   which tests can hand to Dispatch to stand in for a busy pool.
//
// Pi v1.5.0 sync (area scheduler-idle, 35dfa7573; durable 1.5.0 Changed#8): the per-commit work no longer grows with the conversations.
// - A publication collects the conversations above each task that became live or ended (AddScopesAboveLocked, before the sweep; null when
//   a node on the way up is not known) and settles only those (SettleIdleLocked): their idle waiters, the Harness-wide ones, and the idle
//   periods of their kept contexts. It settles under the same lock as its task changes, as upstream's observe does in the same synchronous
//   call, so the kept contexts' idle state never disagrees with the published tasks; the clock and the retention are read before the lock.
// - A chain load that resolved what IdleLocked counts as unknown for a live task whose chain is not loaded (a live task passed, or a node
//   installed while one is unloaded) sets unknownsChanged and schedules a reconcile pass: the next settle covers every scope.
// - Idle contexts expire oldest first from idleOrder (upstream #idleOrder, an insertion-ordered map with O(1) removal), sorted again when
//   the clock goes back (MarkIdleLocked); zero retention drops every idle one. The expiry timer only expires, and a reconcile pass resolves
//   idle waiters and expires (a full settle when unknownsChanged), for hosts that decline the timer. A pass counts itself done
//   (QuiesceAsync's reconciling) only after that check.
// - Wake() takes the earliest sleep from `sleeping` and scans the live tasks only while nothing runs (RunsNowLocked, then the registry check
//   outside the lock). IdleState (upstream idleState, SCHEDULER_IDLE_STATE) checks the incremental state against a full recompute.
// - One v1.5.0 detail outside 35dfa7573 found by the whole-file comparison: SubscribeBlocked holds a listener once, as upstream's Set.
//
// Deliberate differences from the v1.5.0 text, beyond the concurrency model above (each also commented where it is):
// - A reservation pass starts its abort invocations before its run invocations, each with the record the reservation committed
//   (StartPass), as a run invocation here can run its step inline; JoinAsync also joins the invocations Seal saw; FailSession also ignores
//   a throw once the Session is closing, and a refused sweep job clears its flag; WaitForTaskAsync checks for close again before it adds a
//   waiter; Signal reports a throwing abort listener as listener_error.
// - The context read takes the retention and the clock before the lock on every read, where upstream reads them on the idle branch only, so
//   a throwing settings getter is reported on every read. A missing phase or abort handler faults with PiSharp's own text, not V8's
//   TypeError. Seal counts the closing -1 active deltas once; the active counter is used only with host telemetry; ownedTasks returns the
//   immutable records without copies; LimitsRestarts may be null (none).
// - AddScopesAboveLocked also stops at a task an earlier walk of the same publication passed, not only at a conversation already
//   collected, with the same scopes: upstream's walks are quadratic in the depth of a chain that one publication creates or ends. With
//   upstream's walk, harness.ownership.tracks-ownership-chains-deeper-than-the-call-stack (20,000 tasks) took 17 s here instead of 2 s,
//   and scheduler-scale's 20,000-deep chain 7.9 to 8.4 s instead of 2.1 s (docs/durable-benchmarks.md). Superseded by Pi v1.6.0, whose
//   walk starts at the owner's conversation (below); the `walked` set is gone.
//
// Pi v1.6.0 sync (faac585884ae2c013a6b1400f3e9e77f10b34bac): signals, waits on any task, wakes, gates and parks.
// - A sleep may wait for signals, with or without a time (Signals.SleepDue; Asleep, upstream asleep()): a delivery's commit makes the task
//   a candidate, as any change of its record does, and the reservation that wakes parked work records why in the running state's `wake`
//   (WokeLocked, Reserved) and marks the signal memos consumed or expired. A task written back as pending keeps the wake (PendingAgain),
//   and a commit of the same checkpoint keeps it (CommitStateAsync). A wait with policy `any` runs once one of its tasks ended.
// - A sleeping tool call parked at its gates (options.Gated) is woken by every registry publication, by open, and when its sleep was
//   committed under a snapshot the registry has since replaced (gateWakes, WakeGateLocked, the publication count a run keeps).
// - Open keeps the abandonOnRestart children of an owner that was not cut off while running (options.KeepsAbandoned).
// - Kept contexts count a conversation whose live work is all parked as inactive (InactiveLocked over QuietLocked), and a task that parks
//   or wakes settles the idle periods of the conversations above it.
// - AddScopesAboveLocked starts at the task's own conversation and goes from conversation to owning conversation: a child task lives in
//   its owner's conversation, so the owner chain between costs nothing (upstream's fix of the O(n²) walk).
// - runtime.ReadSignalAsync; the close signal a task's cleanup runs under (CloseSignalOf, upstream closeSignal), aborted at the seal; the
//   provider session IDs the Harness cleans up at close (ProviderSessions, upstream providerSessionIds).
using System.Collections;
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

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
internal sealed partial class DurableTaskScheduler
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
    private readonly Func<TaskRecord, bool> keepsAbandoned;
    private readonly Func<TaskRecord, bool> parkedAtGate;
    /// <summary>Provider session IDs the tasks of this Harness used (upstream <c>providerSessionIds</c>); guarded by itself.</summary>
    private readonly HashSet<string> providerSessions = new(StringComparer.Ordinal);
    /// <summary>Aborted at the seal: cancels the cleanups an abort must not cancel (<see cref="CloseSignalOf"/>).</summary>
    private readonly CancellationSignal closed = new();

    private readonly Lock gate = new();
    // Insertion-ordered like a JavaScript Map: replacing a record keeps its position.
    private readonly OrderedDictionary<TaskId, TaskRecord> live = [];
    /// <summary>
    /// Context range last read through a task runtime, per conversation: a later read, by any of its tasks, scans only newer entries.
    /// <c>IdleSince</c> is the Harness time it was first seen idle. Derived and never persisted; dropped at the first idle check after
    /// <c>settings.ContextRetentionMs</c> of idleness, and at close.
    /// </summary>
    private readonly Dictionary<ConversationId, KeptContext> contexts = [];
    /// <summary>
    /// The idle kept contexts by <c>IdleSince</c>, oldest first: the expiry order, so neither an expiry nor finding the next one scans
    /// <see cref="contexts"/> (upstream <c>#idleOrder</c>). Kept sorted also when the Harness clock goes back (see
    /// <see cref="MarkIdleLocked"/>).
    /// </summary>
    private readonly OrderedMap<ConversationId, double> idleOrder = new();
    /// <summary>At least the newest <c>IdleSince</c> in <see cref="idleOrder"/> (drops do not lower it): an older one added means the clock
    /// went back.</summary>
    private double idleNewest = double.NegativeInfinity;
    /// <summary>A chain load resolved what <see cref="IdleLocked"/> treats as unknown for a live task whose chain is not loaded, which can make
    /// a scope idle without a task change below it; the next idle check then covers every scope.</summary>
    private bool unknownsChanged;
    /// <summary>Idle checks that covered every scope (<see cref="IdleState"/>, a test diagnostic).</summary>
    private int fullIdleChecks;
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
    /// <summary>The wake a job scheduled before the Session failed still owes OnWake (<see cref="Seal"/>).</summary>
    private Wake? failedWake;
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
    /// <summary>Registry publications so far: a phase that took its snapshot at an older count ran under a replaced registry. Guarded by
    /// the lock.</summary>
    private long publications;
    /// <summary>Sleeping calls parked at a gate that the registry or open wakes before their time (<see cref="WakeGateLocked"/>).</summary>
    private readonly OrderedSet<TaskId> gateWakes;
    /// <summary>The latest clock a reservation pass made due sleeps candidates at (<see cref="WakeDueLocked"/>): a sleep due after it
    /// needs another pass.</summary>
    private double lastPassAt = double.NegativeInfinity;
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
        keepsAbandoned = options.KeepsAbandoned ?? (static _ => false);
        parkedAtGate = options.Gated ?? (static _ => false);
        dropQueue = new(iterations);
        roots = new(iterations);
        unloaded = new(iterations);
        intent = new(iterations);
        runnable = new(iterations);
        finalizeChecks = new(iterations);
        failFastChecks = new(iterations);
        abandoned = new(iterations);
        sleeping = new(iterations);
        gateWakes = new(iterations);
    }

    /// <summary>The provider session IDs the tasks of this Harness used, taken: the Harness cleans them up once its invocations joined.</summary>
    internal List<string> TakeProviderSessions()
    {
        lock (providerSessions)
        {
            List<string> taken = [.. providerSessions];
            providerSessions.Clear();
            return taken;
        }
    }

    /// <summary>The signal of the close of the Harness running <paramref name="runtime"/>'s task (upstream <c>closeSignal</c>); a runtime
    /// of another kind, such as a test's, has one that never aborts.</summary>
    internal static CancellationSignal CloseSignalOf(ITaskRuntime runtime) => runtime is ISchedulerRuntime own ? own.CloseSignal : NeverClosed;

    private static readonly CancellationSignal NeverClosed = new();

    /// <summary>Test probe: members iterated over the scheduler's sets so far (see <see cref="iterations"/>).</summary>
    internal long SetEntries => Interlocked.Read(ref iterations.Entries);

    /// <summary>Internal, for tests: starts the work the scheduler hands to the thread pool (<see cref="Post"/>: reservation drains, the wake
    /// job, the sweep job, blocked notifications) in place of the pool; null, the default, is the pool. A test holds what it gets to stand in
    /// for a busy pool, whose workers start such work at any later time. Some sites call it under the lock, so it only takes the work.
    /// </summary>
    internal Action<Action>? Dispatch
    {
        get => Volatile.Read(ref dispatch);
        set => Volatile.Write(ref dispatch, value);
    }

    private Action<Action>? dispatch;

    /// <summary>Starts <paramref name="work"/> on the thread pool (upstream's queueMicrotask wakeups, see the file header), or hands it to
    /// <see cref="Dispatch"/>.</summary>
    private void Post(Action<DurableTaskScheduler> work)
    {
        if (Dispatch is { } hold) hold(() => work(this));
        else ThreadPool.UnsafeQueueUserWorkItem(static state => state.Work(state.Scheduler), (Work: work, Scheduler: this), preferLocal: false);
    }

    /// <summary>Loads live tasks and changes surviving <c>running</c> tasks back to <c>pending</c>, counting the restart. Dispatches nothing;
    /// a task cut off <c>settings.RestartLimit</c> times ends when it is next reserved. Read-only, it only loads them as committed: a
    /// <c>running</c> task stays as committed, running in another process.</summary>
    public async Task OpenAsync(DurableContext openContext)
    {
        session.FollowCommits((publication, _) => Observe(publication));
        session.SubscribeClose(Seal);
        registrySubscription = registry.Subscribe(() =>
        {
            lock (gate)
            {
                publications++;
                // Calls parked at a gate are asked again: the gates installed now decide (spec suspendable §5.2).
                foreach (var id in sleeping)
                    if (live.TryGetValue(id, out var parked)) WakeGateLocked(parked);
            }
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
                        // The registry may differ from the one a call parked at its gates under.
                        WakeGateLocked(record);
                        if (record.AbandonOnRestart) abandoned.Add(record.Id);
                        if (record.State is RunningTaskState running)
                            tx.SetTask(PendingAgain(record, running) with { Restarts = (record.Restarts ?? 0) + 1 });
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

    /// <summary>
    /// The kept contexts' idle state, for tests (a diagnostic, computed on demand; upstream <c>idleState</c>): how many are kept and how many
    /// are idle, how many disagree with a full recompute (an idle period without <see cref="IdleLocked"/>, or <see cref="IdleLocked"/> without
    /// one; an expiry order that differs from the idle periods, or is not sorted), and how many idle checks covered every scope.
    /// </summary>
    internal SchedulerIdleState IdleState
    {
        get
        {
            lock (gate)
            {
                var mismatches = 0;
                var idle = 0;
                foreach (var (conversationId, kept) in contexts)
                {
                    if (kept.IdleSince is not null) idle++;
                    if ((kept.IdleSince is not null) != InactiveLocked(conversationId)) mismatches++;
                    if ((idleOrder.TryGetValue(conversationId, out var since) ? since : (double?)null) != kept.IdleSince) mismatches++;
                }
                var previous = double.NegativeInfinity;
                foreach (var (conversationId, idleSince) in idleOrder)
                {
                    if (idleSince < previous || contexts.GetValueOrDefault(conversationId)?.IdleSince != idleSince) mismatches++;
                    previous = idleSince;
                }
                return new SchedulerIdleState(contexts.Count, idle, mismatches, fullIdleChecks);
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
    /// migration (upstream <c>subscribeBlocked</c>). Returns its unsubscribe. A set, as upstream's: a listener subscribed twice is held and
    /// called once, and one unsubscribe removes it.</summary>
    public Action SubscribeBlocked(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate) if (!blockedListeners.Contains(listener)) blockedListeners = blockedListeners.Add(listener);
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
    /// read-only scheduler runs nothing, so it never needs a process. The earliest sleep comes from <see cref="sleeping"/>, and the live tasks
    /// are scanned only while nothing runs, so no call visits every live task while work is active (Pi 35dfa7573).
    /// </summary>
    public Wake Wake() => readOnly ? new Wake(false) : WakeAt(now(), out _);

    /// <summary>
    /// <see cref="Wake"/> at <paramref name="at"/>, and whether a sleep came due after the latest reservation pass read the clock
    /// (<paramref name="dueSincePass"/>): its time is not in At, which holds sleeps not yet due, and no pass has made it a candidate.
    /// </summary>
    private Wake WakeAt(double at, out bool dueSincePass)
    {
        double? earliest = null;
        bool active;
        dueSincePass = false;
        var check = new List<TaskRecord>();
        lock (gate)
        {
            active = invocations.Count > 0 || reconcilingWork > 0 || ReconcileWorkLocked() || RunsNowLocked(at, check);
            // The earliest sleep not yet due: only `sleeping` holds sleeping tasks. A sleep without `until` waits only for signals, which
            // a delivery's commit wakes.
            foreach (var id in sleeping)
            {
                if (live.GetValueOrDefault(id) is not { AbortRequested: false, State: SleepingTaskState asleep } record) continue;
                if (AsleepLocked(record, at))
                {
                    if (asleep.Until is { } until) earliest = earliest is { } other ? Math.Min(other, until) : until;
                }
                else if (asleep.Until is { } due && due <= at && due > lastPassAt) dueSincePass = true;
            }
        }
        if (!active && check.Count > 0) active = RunsNow(check);
        return new Wake(active, earliest);
    }

    /// <summary>
    /// Whether a live task can run now without a registry check (upstream <c>#runsNow</c>, the scan <see cref="Wake"/> needs only while no
    /// invocation runs): an abort-marked task, which runs its handler or is orphaned. Any other task that could run waits for a definition
    /// that can take it; those are added to <paramref name="check"/> for <see cref="RunsNow(List{TaskRecord})"/>, as the registry snapshot is
    /// host code and runs outside the lock.
    /// </summary>
    private bool RunsNowLocked(double at, List<TaskRecord> check)
    {
        foreach (var record in live.Values)
        {
            var state = record.State;
            if (state.Status == DurableTaskStatus.Completing) continue;
            if (state is WaitingTaskState && !record.AbortRequested && WaitingOnLocked(record, first: true).Count > 0) continue;
            if (AsleepLocked(record, at)) continue;
            // An abort invocation waits for the live work below it, which counts itself.
            if (record.AbortRequested && WaitingOnLocked(record, first: true).Count > 0) continue;
            if (record.AbortRequested && record.AbortReason is null) return true;
            check.Add(record);
        }
        return false;
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
                        writes.Add(new TaskWrite(PendingAgain(record, running)));
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

}
