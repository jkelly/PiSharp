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
//   commit or registry listener. Invocations then run their handlers on the thread pool, possibly in parallel with each other. The sweep
//   job (#scheduleSweep) is enqueued from the thread pool too, so it never starts inline inside the lock; it still runs after the operation
//   underway.
// - AbortController becomes CancellationSignal (a token plus its reason); an invocation's handler context is the scheduler context bound
//   to that signal, so waits bound to it fail with the reason (the ended error, or OperationCanceledException for an abort mark or close,
//   upstream's AbortError).
// - setTimeout becomes HarnessOptions.TimeProvider timers: sleeps wait in Task.Delay steps and recheck the Harness clock; the idle context
//   expiry is one ITimer. .NET timers never keep a process alive, so the expiry timer is always kept (upstream keeps it only where it can
//   be unreferenced).
// - Walks up and down the ownership tree are loops, never recursion, as upstream's are: chains can be deeper than the stack.
using System.Collections;
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>Terminal outcomes the scheduler writes without running task code: faulted or orphaned (upstream <c>SchedulerOutcome</c>).</summary>
internal delegate Task SettleOutcome(Transaction tx, TaskRecord record, TaskOutcome outcome);

/// <summary>An invocation a conversation handle is bound to: its signal, and a check that throws once it ended (upstream
/// <c>InvocationBinding</c>).</summary>
internal sealed record InvocationBinding(CancellationSignal Signal, Action Check);

/// <summary>Sizes of the scheduler's ownership indexes, for tests that bound its memory (upstream <c>TaskScheduler.indexSizes</c>, read
/// through the Harness's <c>SCHEDULER_INDEX_SIZES</c>). <see cref="Below"/> counts the listed child nodes.</summary>
internal readonly record struct SchedulerIndexSizes(int Live, int Settled, int Edges, int Below, int Roots, int Unloaded, int DropQueue);

/// <summary>Options of the task scheduler (upstream <c>TaskSchedulerOptions</c>).</summary>
internal sealed record DurableTaskSchedulerOptions
{
    public required Session Session { get; init; }
    public required IStorage Storage { get; init; }
    public required IRegistryReader Registry { get; init; }
    public required IDurableModels Models { get; init; }
    /// <summary>Resolves a conversation's agent against a snapshot; the runtime calls it at most once per phase.</summary>
    public required Func<ConversationId, IRegistrySnapshot, DurableContext, Task<Agent>> Agent { get; init; }
    /// <summary>Resolves the settings; read at each access.</summary>
    public required Func<Settings> Settings { get; init; }
    /// <summary>Builds a conversation's environment with <c>HarnessOptions.Env</c>.</summary>
    public required Func<ConversationId, DurableContext, Task<IExecutionEnv?>> Env { get; init; }
    public required Func<double> Now { get; init; }
    public required TimeProvider TimeProvider { get; init; }
    public required Action<Exception> Report { get; init; }
    /// <summary>Harness cleanup staged in the commit that makes an outcome the scheduler wrote itself terminal.</summary>
    public required SettleOutcome SettleOutcome { get; init; }
    /// <summary>Withdraws a conversation's queued inputs, for conversation abort and abort cascades.</summary>
    public required Func<Transaction, ConversationId, Task> WithdrawInputs { get; init; }
    /// <summary>Invocation-bound handle of an existing conversation, for task runtimes and tools.</summary>
    public required Func<ConversationId, InvocationBinding, DurableContext, Task<IConversationHandle?>> Conversation { get; init; }
    /// <summary>Context for scheduler commits and invocations; carries no caller cancellation.</summary>
    public required DurableContext Context { get; init; }
}

/// <summary>
/// The durable task scheduler of one Harness (upstream <c>TaskScheduler</c>).
/// </summary>
/// <remarks>
/// <para><c>live</c> mirrors every committed non-terminal task record: pending, running, waiting and completing. The synchronous commit
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
/// marks, runtime commits, finalization, and the synchronous step before each phase, which applies the precedence rules and writes a fault
/// or handover. Handlers and joins run off the line. An invocation ends inside the step that decides its end, so a runtime commit it queued
/// either lands before that decision or is rejected.</para>
/// </remarks>
internal sealed class DurableTaskScheduler
{
    private const int ScanPageSize = 256;
    /// <summary>Longest delay one timer step takes; longer sleeps wait in several steps.</summary>
    private const double MaxTimerDelay = 2_147_483_647;
    private static readonly DurableTaskStatus[] LiveStatuses =
        [DurableTaskStatus.Pending, DurableTaskStatus.Running, DurableTaskStatus.Waiting, DurableTaskStatus.Completing];

    private readonly Session session;
    private readonly IStorage storage;
    private readonly IRegistryReader registry;
    private readonly IDurableModels models;
    private readonly Func<ConversationId, IRegistrySnapshot, DurableContext, Task<Agent>> agent;
    private readonly Func<Settings> settings;
    private readonly Func<ConversationId, DurableContext, Task<IExecutionEnv?>> env;
    private readonly Func<double> now;
    private readonly TimeProvider timeProvider;
    private readonly Action<Exception> report;
    private readonly SettleOutcome settleOutcome;
    private readonly Func<Transaction, ConversationId, Task> withdrawInputs;
    private readonly Func<ConversationId, InvocationBinding, DurableContext, Task<IConversationHandle?>> conversation;
    private readonly DurableContext context;

    private readonly Lock gate = new();
    // Insertion-ordered like a JavaScript Map: replacing a record keeps its position.
    private readonly OrderedDictionary<TaskId, TaskRecord> live = [];
    /// <summary>
    /// Context range last read through a task runtime, per conversation: a later read, by any of its tasks, scans only newer entries.
    /// <c>IdleSince</c> is the Harness time it was first seen idle. Derived and never persisted; dropped at the first idle check after
    /// <c>settings.ContextRetentionMs</c> of idleness, and at close.
    /// </summary>
    private readonly Dictionary<ConversationId, KeptContext> contexts = [];
    /// <summary>Timer for the earliest expiry of an idle context.</summary>
    private (double At, ITimer Timer)? expiry;
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
    /// <summary>Members iterated over the scheduler's sets, for tests that bound a pass's work (upstream's tests count
    /// <c>Set.prototype[Symbol.iterator]</c> entries).</summary>
    private readonly IterationCounter iterations = new();
    private bool reconcileScheduled;
    private bool sweepScheduled;
    private bool cascadePending;
    /// <summary>Reconciliation passes running now; with the flags above, what <see cref="QuiesceAsync"/> waits out.</summary>
    private int reconciling;
    private IDisposable? registrySubscription;
    private bool enabled;
    private bool closing;
    private bool dirty;
    private bool draining;

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
        timeProvider = options.TimeProvider;
        report = options.Report;
        settleOutcome = options.SettleOutcome;
        withdrawInputs = options.WithdrawInputs;
        conversation = options.Conversation;
        context = options.Context;
        dropQueue = new(iterations);
        roots = new(iterations);
        unloaded = new(iterations);
        intent = new(iterations);
        runnable = new(iterations);
        finalizeChecks = new(iterations);
        failFastChecks = new(iterations);
        abandoned = new(iterations);
    }

    /// <summary>Test probe: members iterated over the scheduler's sets so far (see <see cref="iterations"/>).</summary>
    internal long SetEntries => Interlocked.Read(ref iterations.Entries);

    /// <summary>Loads live tasks and changes surviving <c>running</c> tasks back to <c>pending</c>. Dispatches nothing.</summary>
    public async Task OpenAsync(DurableContext openContext)
    {
        session.ObserveCommits((publication, _) => Observe(publication));
        session.SubscribeClose(Seal);
        registrySubscription = registry.Subscribe(Kick);
        await session.CommitWithAsync(async tx =>
        {
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
                        if (record.AbandonOnRestart) abandoned.Add(record.Id);
                        if (record.State is RunningTaskState running) tx.SetTask(WithState(record, new PendingTaskState(running.Checkpoint)));
                        if (record.State is WaitingTaskState { Policy: JoinPolicy.FailFast }) failFastChecks.Add(record.Id);
                    }
                }
            }
        }, openContext).ConfigureAwait(false);
        // Derive abort marks a crash left unapplied below cancelled owners, and finalize held outcomes.
        lock (gate)
        {
            cascadePending = true;
            ScheduleReconcileLocked();
        }
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
            lock (gate) if (!reconcileScheduled && reconciling == 0 && !sweepScheduled && !draining) return;
            await Task.Delay(1).ConfigureAwait(false);
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
                return new SchedulerIndexSizes(live.Count, settled.Count, edges.Count, listed, roots.Count, unloaded.Count, dropQueue.Count);
            }
        }
    }

    /// <summary>Enables scheduling. Idempotent; the kick does nothing once closing.</summary>
    public void Resume()
    {
        lock (gate) enabled = true;
        Kick();
    }

    /// <summary>
    /// Signals every invocation and waits for them; writes nothing. Close calls this after every close listener has run, so waits, watches and
    /// streams a task holds end with the Session's reason, not as cancelled by its signal.
    /// </summary>
    public Task JoinAsync()
    {
        List<Invocation> signal;
        lock (gate) signal = [.. invocations.Values];
        foreach (var invocation in signal) Signal(invocation, null);
        return Task.WhenAll(signal.Select(invocation => invocation.Done.Task));
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
                bool owned;
                lock (gate) owned = HasOwnedLiveLocked(id, null, null);
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
        var found = await session.ReadOnLineAsync(async () =>
        {
            lock (gate)
            {
                if (closing) throw HarnessUtil.ClosedError(session);
                if (live.ContainsKey(id)) return taskWaiters.Add(id, callContext);
            }
            var record = await storage.GetTaskAsync(id, callContext).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task {id} does not exist");
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

    // ─── Scheduling ────────────────────────────────────────────────────────

    private void Observe(CommitPublication publication)
    {
        var signal = new List<Invocation>();
        var changed = false;
        lock (gate)
        {
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
                    taskWaiters.Resolve(record.Id, record);
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
        foreach (var invocation in signal) Signal(invocation, null);
        if (!changed) return;
        SettleIdle();
        Kick();
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
            report(error);
            return 0;
        }
    }

    /// <summary>Resolves idle waiters, and drops each kept context whose conversation has been idle for the retention period.</summary>
    private void SettleIdle()
    {
        var at = now();
        var retention = ContextRetentionMs();
        lock (gate)
        {
            foreach (var conversationId in idleWaiters.Keys())
                if (IdleLocked(conversationId)) idleWaiters.Resolve(conversationId, true);
            foreach (var (conversationId, kept) in contexts.ToList())
            {
                if (kept.IdleSince is { } since && at - since >= retention) contexts.Remove(conversationId);
                else if (!IdleLocked(conversationId)) kept.IdleSince = null;
                else if (retention > 0) kept.IdleSince ??= at;
                else contexts.Remove(conversationId);
            }
            ScheduleExpiryLocked(retention, at);
        }
    }

    /// <summary>Runs <see cref="SettleIdle"/> when the earliest idle context expires.</summary>
    private void ScheduleExpiryLocked(double retention, double at)
    {
        double? due = null;
        foreach (var kept in contexts.Values)
            if (kept.IdleSince is { } since && (due is null || since + retention < due)) due = since + retention;
        if (expiry is { } current && current.At == due) return;
        expiry?.Timer.Dispose();
        expiry = null;
        if (due is not { } when || closing) return;
        var holder = new StrongBoxTimer();
        var timer = timeProvider.CreateTimer(_ =>
        {
            lock (gate) if (expiry is { } scheduled && ReferenceEquals(scheduled.Timer, holder.Timer)) expiry = null;
            SettleIdle();
        }, null, TimeSpan.FromMilliseconds(Math.Min(Math.Max(0, when - at), MaxTimerDelay)), Timeout.InfiniteTimeSpan);
        holder.Timer = timer;
        expiry = (when, timer);
    }

    // ─── Ownership ───────────────────────────────────────────────────────────

    private void ScheduleReconcileLocked()
    {
        if (reconcileScheduled || closing) return;
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
        bool cascade;
        List<TaskId> checks;
        lock (gate)
        {
            reconcileScheduled = false;
            reconciling++;
            cascade = cascadePending;
            cascadePending = false;
            checks = [.. failFastChecks];
            failFastChecks.Clear();
        }
        try
        {
            await session.CommitWithAsync(async tx =>
            {
                lock (gate) if (closing) return;
                var queued = await LoadScopesAsync(cascade).ConfigureAwait(false);
                // A cascade from an abandoned owner passes its `restart` reason on; any other intent marks, or upgrades a `restart` mark, as
                // a request. Collected first, so a request wins over a `restart` mark of the same pass.
                var marks = new OrderedDictionary<TaskId, (TaskRecord Record, bool Restart)>();
                void Mark(TaskRecord record, bool restart)
                {
                    if (marks.TryGetValue(record.Id, out var staged) && !staged.Restart) return;
                    if (record.AbortRequested && (record.AbortReason is null || restart)) return;
                    marks[record.Id] = (record, restart);
                }
                lock (gate)
                {
                    // Every live foreground task below a live owner with cancellation intent, which is its nearest one: a walk down from
                    // each owner with intent does not enter another, whose own walk covers what is below it. Derived again on every pass,
                    // as loading edges can reveal a cancelled owner.
                    foreach (var id in intent)
                    {
                        var owner = live[id];
                        // Only an owner abandoned after a restart, and nothing else, passes its reason on.
                        var restart = owner.AbortReason == TaskAbortReason.Restart && !FailedOutcome(owner);
                        foreach (var (_, node, record) in WalkDownLocked(Node.Of(id), null, false, next => !intent.Contains(next)))
                            if (record is not null && !node.Background) Mark(record, restart);
                    }
                }
                foreach (var id in checks)
                {
                    TaskRecord? waiter;
                    lock (gate) waiter = live.GetValueOrDefault(id);
                    if (waiter?.State is not WaitingTaskState waiting || !await AnyFailedAsync(waiting.On).ConfigureAwait(false)) continue;
                    // Every other live task: the failed one keeps its own outcome.
                    lock (gate)
                        foreach (var member in waiting.On)
                            if (live.TryGetValue(member, out var record) && !FailedOutcome(record)) Mark(record, false);
                }
                foreach (var (record, restart) in marks.Values) tx.SetTask(WithAbortMark(record, restart));
                foreach (var id in queued)
                {
                    bool cancelled;
                    lock (gate) cancelled = BelowCancelledLocked(Up.Of(id));
                    if (cancelled) await withdrawInputs(tx, id).ConfigureAwait(false);
                }
                await FinalizeAsync(tx).ConfigureAwait(false);
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
            lock (gate) reconciling--;
        }
        SettleIdle();
    }

    /// <summary>Whether any of <paramref name="ids"/> holds or ended with an outcome other than <c>completed</c>.</summary>
    private async Task<bool> AnyFailedAsync(ImmutableArray<TaskId> ids)
    {
        foreach (var id in ids)
        {
            TaskRecord? record;
            lock (gate) record = live.GetValueOrDefault(id);
            record ??= await storage.GetTaskAsync(id, context).ConfigureAwait(false);
            if (record is not null && FailedOutcome(record)) return true;
        }
        return false;
    }

    /// <summary>
    /// Writes the terminal record of every <c>completing</c> task without live ordinary owned work. Finalizing one can free its owner, so
    /// this repeats over the commit's candidates until nothing changes. A held scheduler outcome gets its Harness cleanup here.
    /// </summary>
    private async Task FinalizeAsync(Transaction tx)
    {
        // A worklist: the tasks that started holding, and the holders of work that ended. Finalizing one can free the nearest live task above
        // it, which goes on the list. Tasks finalized here have no live work below them, so walks down skip them.
        // In rounds: the holders the tasks of one round free are checked once, in the next, so a holder of many tasks that end together is
        // walked once, not once per task.
        List<TaskId> round;
        lock (gate)
        {
            round = [.. finalizeChecks];
            finalizeChecks.Clear();
        }
        var done = new HashSet<TaskId>();
        var overlay = OverlayOf(tx);
        while (round.Count > 0)
        {
            var next = new OrderedSet<TaskId>(iterations);
            foreach (var id in round)
            {
                TaskRecord record;
                TaskOutcome outcome;
                TaskRecord terminal;
                lock (gate)
                {
                    var found = overlay.Tasks.GetValueOrDefault(id) ?? live.GetValueOrDefault(id);
                    if (found?.State is not CompletingTaskState completing || done.Contains(id)) continue;
                    if (HasOwnedLiveLocked(id, overlay, done)) continue;
                    record = found;
                    outcome = completing.Outcome;
                    terminal = WithState(record, new TerminalTaskState(outcome));
                }
                tx.SetTask(terminal);
                lock (gate) overlay.Tasks[id] = terminal;
                done.Add(id);
                // REMINDER: only the scheduler writes faulted and orphaned (spec §5.4); their cleanup waits for this commit.
                if (outcome is FaultedTaskOutcome or OrphanedTaskOutcome) await settleOutcome(tx, record, outcome).ConfigureAwait(false);
                lock (gate)
                {
                    var above = NearestLiveAboveLocked(NodeOf(record), overlay);
                    if (above?.State.Status == DurableTaskStatus.Completing) next.Add(above.Id);
                }
            }
            round = [.. next];
        }
    }

    /// <summary>
    /// Loads the owner chains of the tasks in <see cref="unloaded"/> and, with <paramref name="queued"/>, of every conversation with queued
    /// inputs, on the Session line; returns the latter. Reads committed Storage directly, so it may run inside a commit callback.
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
        var submissions = await HarnessUtil.ScanAll(cursor =>
            storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Queued }, ScanPageSize, cursor, context)).ConfigureAwait(false);
        var conversations = submissions.Where(submission => submission.Type == SubmissionType.Input).Select(submission => submission.ConversationId)
            .Distinct().ToList();
        foreach (var id in conversations) await LoadChainAsync(Up.Of(id), null).ConfigureAwait(false);
        return conversations;
    }

    /// <summary>
    /// Loads the owner edges and task nodes from <paramref name="start"/> up to its ownerless root. A known task outside
    /// <see cref="unloaded"/> ends the walk, as its chain is loaded; the <see cref="unloaded"/> tasks passed have a loaded chain afterwards.
    /// Loading a node can reveal a cancelled owner, so it makes the next reconcile derive cascade marks.
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
                    var record = await storage.GetTaskAsync(id, context).ConfigureAwait(false);
                    if (record is null) return;
                    node = NodeOf(record);
                    lock (gate)
                    {
                        if (record.State.Status == DurableTaskStatus.Terminal) SettleLocked(id, node.Value);
                        // A loaded node can reveal a cancelling owner: the next reconcile derives marks again.
                        ScheduleReconcileLocked();
                    }
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
                    var record = await storage.GetConversationAsync(current.Conversation, context).ConfigureAwait(false);
                    owner = record?.Owner?.TaskId;
                    lock (gate)
                    {
                        SetEdgeLocked(current.Conversation, owner);
                        // A loaded node can reveal a cancelling owner: the next reconcile derives marks again.
                        ScheduleReconcileLocked();
                    }
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
            if (below.ContainsKey(node)) continue;
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
    /// Brings the scheduler's sets in line with a task's committed record. A task that ended may free the nearest live task above it: one
    /// holding its outcome finalizes, an abort-marked one runs its abort handler; its waiters may run.
    /// </summary>
    private void TrackLocked(TaskRecord? previous, TaskRecord record)
    {
        var id = record.Id;
        if (previous?.State is WaitingTaskState previousWait) foreach (var on in previousWait.On) DeleteFrom(waiters, on, id);
        if (record.State.Status == DurableTaskStatus.Terminal)
        {
            var node = NodeOf(record);
            live.Remove(id);
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
        for (var current = node; !current.Background;)
        {
            var owner = current.Owner;
            if (owner is null)
            {
                if (!EdgeLocked(current.ConversationId, overlay, out var edge) || edge is null) return null;
                owner = edge;
            }
            var id = owner.Value;
            var record = overlay?.Tasks.GetValueOrDefault(id) ?? live.GetValueOrDefault(id);
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
    /// committed records; the walk does not enter tasks in <paramref name="done"/>, which have none. Owner chains must be loaded.
    /// </summary>
    private List<TaskId> OwnedLiveLocked(TaskId owner, Overlay? overlay, bool first, IReadOnlySet<TaskId>? done)
    {
        var found = new List<TaskId>();
        var walk = WalkDownLocked(Node.Of(owner), overlay, false, done is null ? null : id => !done.Contains(id));
        foreach (var (id, node, record) in walk)
        {
            if (record is null || node.Background) continue;
            found.Add(id);
            if (first) break;
        }
        return found;
    }

    private bool HasOwnedLiveLocked(TaskId owner, Overlay? overlay, IReadOnlySet<TaskId>? done) =>
        OwnedLiveLocked(owner, overlay, first: true, done).Count > 0;

    /// <summary>
    /// Whether ordinary traversal from <paramref name="scope"/> reaches <paramref name="start"/>: walking up reaches the scope's conversation,
    /// or an ownerless one for every root, without crossing a background owner, unless <paramref name="crossBackground"/>. Null while an edge
    /// is not loaded.
    /// </summary>
    private bool? InScopeLocked(Up start, Scope scope, bool crossBackground = false)
    {
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

    /// <summary>Fails the Session, which reports it, for a throw in one of the scheduler's own commits; ignored once closing.</summary>
    private void FailSession(Exception error)
    {
        bool closingNow;
        lock (gate) closingNow = closing;
        if (!closingNow) session.Fail(error);
    }

    /// <summary>Close listener: runs synchronously once admission is sealed, before <see cref="JoinAsync"/>, or when the Session fails.</summary>
    private void Seal()
    {
        lock (gate)
        {
            closing = true;
            contexts.Clear();
            expiry?.Timer.Dispose();
            expiry = null;
            var error = HarnessUtil.ClosedError(session);
            taskWaiters.RejectAll(error);
            idleWaiters.RejectAll(error);
        }
        try
        {
            registrySubscription?.Dispose();
        }
        catch (Exception error)
        {
            report(error);
        }
    }

    private void Kick()
    {
        lock (gate)
        {
            dirty = true;
            if (draining || !enabled || closing) return;
            draining = true;
        }
        // Never commit synchronously from a commit or registry listener.
        ThreadPool.UnsafeQueueUserWorkItem(static scheduler => _ = scheduler.DrainAsync(), this, preferLocal: false);
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
                var reservations = await ReserveAsync().ConfigureAwait(false);
                // Upstream starts every reserved invocation before any of them gets past its first await, so an abort invocation reads its
                // record (and its abort reason) as the reservation committed it. A run invocation here may run its step and phase inline
                // when the line is idle, and its commits could change that record first: read the abort invocations' records before any
                // invocation starts.
                lock (gate)
                    foreach (var reservation in reservations)
                        if (reservation.Invocation.Mode == InvocationMode.Abort) reservation.Record = live.GetValueOrDefault(reservation.Invocation.TaskId);
                foreach (var reservation in reservations) Start(reservation);
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
    private async Task<List<Reservation>> ReserveAsync()
    {
        var reservations = new List<Reservation>();
        List<TaskId> marked = [];
        try
        {
            await session.CommitWithAsync(async tx =>
            {
                lock (gate)
                {
                    if (!enabled || closing) return;
                    foreach (var id in abandoned)
                        if (live.TryGetValue(id, out var record) && !record.AbortRequested) tx.SetTask(WithAbortMark(record, restart: true));
                    if (abandoned.Count > 0)
                    {
                        marked = [.. abandoned];
                        return;
                    }
                }
                await LoadScopesAsync(false).ConfigureAwait(false);
                // Taken once per pass, and only when some task is a candidate.
                IRegistrySnapshot? snapshot = null;
                List<TaskId> candidates;
                lock (gate) candidates = [.. runnable];
                foreach (var id in candidates)
                {
                    TaskRecord record;
                    InvocationMode mode;
                    lock (gate)
                    {
                        if (!live.TryGetValue(id, out var found) || found.State.Status == DurableTaskStatus.Completing || invocations.ContainsKey(id))
                        {
                            runnable.Remove(id);
                            continue;
                        }
                        // It returns once what it waits for ends, its record changes, or the work below it ends.
                        if (WaitingOnLocked(found, first: true).Count > 0)
                        {
                            runnable.Remove(id);
                            continue;
                        }
                        record = found;
                        mode = record.AbortRequested ? InvocationMode.Abort : InvocationMode.Run;
                        // Work below an owner with cancellation intent waits for its cascade mark instead of running a phase; schedule the
                        // cascade, which may not have run yet.
                        if (mode == InvocationMode.Run && !record.Background && BelowCancelledLocked(ParentOf(record)))
                        {
                            cascadePending = true;
                            ScheduleReconcileLocked();
                            continue;
                        }
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
                    var invocation = CreateInvocation(record, mode);
                    reservations.Add(new Reservation(invocation, resolution.Task!, snapshot));
                }
            }, context).ConfigureAwait(false);
        }
        catch
        {
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
        // Marked, or already marked or gone: done with them. Reserve again, now seeing the marks.
        lock (gate)
        {
            foreach (var id in marked) abandoned.Remove(id);
            if (marked.Count > 0) dirty = true;
        }
        return reservations;
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
                RunningTaskState => new RunningTaskState(migrated.Checkpoint),
                _ => new PendingTaskState(migrated.Checkpoint),
            };
            return new Resolution(fit.Task, record with { Version = definition.Version, Input = migrated.Input, State = next }, null);
        }
        catch (Exception error)
        {
            lock (gate) failedMigrations[record.Id] = (fit.Task, error);
            report(error);
            return new Resolution(null, null, BlockedReason.MigrationFailed);
        }
    }

    private FitResult Fit(TaskRecord record, DurableTask? task)
    {
        if (task is null) return new FitResult(null, false, BlockedReason.MissingTask, null);
        var version = task.Definition.Version;
        if (version == record.Version) return new FitResult(task, false, null, null);
        if (version < record.Version) return new FitResult(null, false, BlockedReason.TaskTooOld, null);
        lock (gate)
            if (failedMigrations.TryGetValue(record.Id, out var failed) && ReferenceEquals(failed.Task, task))
                return new FitResult(null, false, BlockedReason.MigrationFailed, failed.Error);
        return new FitResult(task, true, null, null);
    }

    /// <summary>
    /// Scheduling state and every live task with its derived state, read on the Session line. Runs no task code: a pending migration shows as
    /// ready with <c>Migrates</c>, and only a migration the scheduler already tried, or one that cannot exist, shows as failed.
    /// </summary>
    public async Task<(HarnessScheduling Scheduling, ImmutableArray<TaskInspection> Tasks)> InspectAsync(IRegistrySnapshot snapshot)
    {
        await LoadScopesAsync(false).ConfigureAwait(false);
        lock (gate)
        {
            var tasks = live.Values.Select(record => new TaskInspection(record, InspectTaskLocked(record, snapshot))).ToImmutableArray();
            var scheduling = closing ? HarnessScheduling.Closing : enabled ? HarnessScheduling.Running : HarnessScheduling.Paused;
            return (scheduling, tasks);
        }
    }

    private TaskInspectionState InspectTaskLocked(TaskRecord record, IRegistrySnapshot snapshot)
    {
        if (invocations.ContainsKey(record.Id)) return new TaskInspectionState(TaskInspectionKind.Running);
        if (record.State.Status == DurableTaskStatus.Completing) return new TaskInspectionState(TaskInspectionKind.Completing);
        var on = WaitingOnLocked(record);
        if (on.Count > 0) return new TaskInspectionState(TaskInspectionKind.Waiting) { On = [.. on] };
        var fit = Fit(record, snapshot.GetTask(record.Kind));
        if (fit.Reason is { } reason) return new TaskInspectionState(TaskInspectionKind.Blocked) { Reason = reason, Error = fit.Error };
        if (fit.Migrates && fit.Task!.Definition.Migrate is null)
            return new TaskInspectionState(TaskInspectionKind.Blocked)
            {
                Reason = BlockedReason.MigrationFailed,
                Error = MissingMigration(record, fit.Task.Definition),
            };
        return new TaskInspectionState(TaskInspectionKind.Ready) { Migrates = fit.Migrates };
    }

    private Invocation CreateInvocation(TaskRecord record, InvocationMode mode)
    {
        var controller = new CancellationSignal();
        var invocation = new Invocation(record.Id, record.ConversationId, mode, controller, context.WithSignal(controller));
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
        try
        {
            if (invocation.Mode == InvocationMode.Run) await RunAsync(reservation).ConfigureAwait(false);
            else await RunAbortAsync(reservation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            report(error);
        }
        finally
        {
            End(invocation);
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
        PhaseResult? previous = null;
        while (true)
        {
            var current = await StepAsync(invocation, (tx, task) => Decide(tx, task, previous, state)).ConfigureAwait(false);
            // Close may seal between the decision and dispatch.
            bool closingNow;
            lock (gate) closingNow = closing;
            if (current is null || closingNow) return;
            var checkpoint = current.Checkpoint;
            // Each phase handler resolves its agent afresh, at first use.
            phase.ResetAgent();
            try
            {
                var name = current.Phase;
                var handler = name is not null && state.Task.Definition.Phases.TryGetValue(name, out var found) ? found
                    : throw new InvalidOperationException($"Task {current.Kind} has no phase {name ?? "undefined"}");
                await handler(current, runtime, invocation.Context).ConfigureAwait(false);
                previous = new PhaseResult(checkpoint, null);
            }
            catch (Exception error)
            {
                previous = new PhaseResult(checkpoint, error);
            }
        }
    }

    /// <summary>
    /// Precedence rules for a run invocation, on the line. Rules 1 (terminal, <c>completing</c>, or <c>waiting</c>) and 2 (closing) are
    /// applied by <see cref="StepAsync"/>. Returns whether the invocation continues with the next phase.
    /// </summary>
    private Decision Decide(Transaction tx, RunningTask current, PhaseResult? previous, RunState state)
    {
        // 3. abort mark: end; a fresh abort invocation starts once the task's ordinary owned work is gone.
        if (current.AbortRequested) return Decision.End;
        // An owner's cancellation intent ends it too, before its cascade marks it; reservation then holds it back.
        lock (gate)
        {
            if (!current.Background && BelowCancelledLocked(ParentOf(current.Record)))
            {
                cascadePending = true;
                ScheduleReconcileLocked();
                return Decision.End;
            }
        }
        if (previous is null) return Decision.Continue;
        // 4. uncaught error.
        if (previous.Failure is { } failure) return Decision.Fault(failure);
        // 6. no durable progress.
        if (JsonValue.DeepEquals(current.Checkpoint, previous.Checkpoint))
        {
            var phase = PhaseOf(previous.Checkpoint) ?? "undefined";
            return Decision.Fault(new InvalidOperationException($"Task {current.Kind} phase {phase} returned without durable progress"));
        }
        // 5. progress: refresh the snapshot; hand over to a replacement definition that can take the task.
        state.Snapshot = registry.Snapshot();
        var next = state.Snapshot.GetTask(current.Kind);
        if (!ReferenceEquals(next, state.Task))
        {
            if (next is not null && CanReserve(next, current.Record))
            {
                tx.SetTask(WithState(current.Record, new PendingTaskState(current.Checkpoint)));
                return Decision.End;
            }
            if (state.Reported is null || !ReferenceEquals(state.Reported.Task, next))
            {
                state.Reported = new ReportedTask(next);
                var error = new InvalidOperationException($"Task {current.Id} keeps running under its old {current.Kind} definition");
                error.Data["cause"] = next is null ? "missing_task" : "incompatible_task";
                report(error);
            }
        }
        return Decision.Continue;
    }

    /// <summary>Runs the abort handler once; rules 1, 2 and 4 apply, and returning without an outcome faults.</summary>
    private async Task RunAbortAsync(Reservation reservation)
    {
        var invocation = reservation.Invocation;
        // The record as the reservation committed it, read before any invocation of the pass started (see DrainAsync).
        var found = reservation.Record;
        bool closingNow;
        lock (gate) closingNow = closing;
        if (found?.State is not RunningTaskState || closingNow) return;
        Exception? failure = null;
        try
        {
            var phase = new Phase(() => reservation.Snapshot, () => reservation.Task);
            var runtime = new Runtime(this, invocation, phase);
            var abort = reservation.Task.Definition.Abort ?? throw new InvalidOperationException($"Task {found.Kind} has no abort handler");
            await abort(new RunningTask(found), runtime, invocation.Context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        var message = $"Abort handler of task {invocation.TaskId} returned without a terminal outcome";
        await StepAsync(invocation, (_, _) => Decision.Fault(failure ?? new InvalidOperationException(message))).ConfigureAwait(false);
    }

    /// <summary>
    /// One synchronous decision on the Session line. A task that is no longer running (rule 1: terminal, <c>completing</c>, or
    /// <c>waiting</c>) or a closing Harness (rule 2) ends the invocation without a write; otherwise <paramref name="decide"/> may stage a write
    /// and returns whether the invocation continues. Ending happens inside the callback, before a fault's Harness cleanup. A rejected step,
    /// such as admission after close, also ends the invocation.
    /// </summary>
    private async Task<RunningTask?> StepAsync(Invocation invocation, Func<Transaction, RunningTask, Decision> decide)
    {
        try
        {
            return await session.CommitWithAsync(async tx =>
            {
                RunningTask? current;
                bool closingNow;
                lock (gate)
                {
                    var found = live.GetValueOrDefault(invocation.TaskId);
                    current = found?.State is RunningTaskState ? new RunningTask(found) : null;
                    closingNow = closing;
                }
                var decision = current is not null && !closingNow ? decide(tx, current) : Decision.End;
                if (decision.Kind == DecisionKind.Continue) return current;
                End(invocation);
                if (decision.Kind == DecisionKind.Fault)
                    await TerminateAsync(tx, current!.Record, new FaultedTaskOutcome(new TaskOutcomeError(DurableErrors.MessageOf(decision.Error!)))).ConfigureAwait(false);
                return null;
            }, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The step writes the scheduler's own decision, a fault or a terminal record; a throw there would otherwise leave the task
            // running, to be reserved and run again.
            End(invocation);
            FailSession(error);
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
        var overlay = OverlayOf(tx);
        bool owned;
        lock (gate) owned = HasOwnedLiveLocked(record.Id, overlay, null);
        if (owned)
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
        if (next is TerminalTaskState terminal)
        {
            var overlay = OverlayOf(tx);
            await LoadScopesAsync(false).ConfigureAwait(false);
            foreach (var record in overlay.Tasks.Values) await LoadChainAsync(ParentOf(record), overlay).ConfigureAwait(false);
            bool owned;
            lock (gate) owned = HasOwnedLiveLocked(current.Id, overlay, null);
            if (owned)
            {
                tx.SetTask(WithState(current.Record, new CompletingTaskState(terminal.Outcome)));
                return;
            }
        }
        tx.SetTask(WithState(current.Record, next));
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
        var overlay = OverlayOf(tx);
        var targets = new List<TaskId>();
        foreach (var id in on)
        {
            TaskRecord? member;
            lock (gate) member = overlay.Tasks.GetValueOrDefault(id) ?? live.GetValueOrDefault(id);
            member ??= await storage.GetTaskAsync(id, context).ConfigureAwait(false);
            if (member is null) throw new InvalidOperationException($"Task {id} does not exist");
            if (policy == JoinPolicy.FailFast && member.Owner != current.Id)
                throw new InvalidOperationException($"Task {current.Id} can wait failFast only on tasks it owns; {id} is not one");
            // A live child already holds the waiter (task-owned tasks are never background): waiting on it adds no edge. A terminal task has
            // no edges.
            if (member.State.Status != DurableTaskStatus.Terminal && member.Owner != current.Id) targets.Add(id);
        }
        if (targets.Count == 0) return;
        var cycle = await WaitCycleAsync(current.Id, targets, overlay).ConfigureAwait(false);
        if (cycle is not null)
            throw new InvalidOperationException($"Task {current.Id} cannot wait on {cycle[0]}: {string.Join(" -> ", cycle.Prepend(current.Id))} is a wait cycle");
    }

    /// <summary>
    /// The path from a member of <paramref name="on"/> back to <paramref name="task"/> in the wait-for graph, if the wait would close a cycle
    /// (spec §5.1). An edge X -> Y means X cannot settle before Y: Y is in the <c>on</c> of waiting X that is not abort-marked, or Y is live
    /// ordinary owned work of X, which holds X as <c>completing</c>. Each task is visited once and each subtree walked once, so a check is
    /// bounded by the live work it reaches.
    /// </summary>
    private async Task<List<TaskId>?> WaitCycleAsync(TaskId task, IReadOnlyList<TaskId> on, Overlay overlay)
    {
        // Owned work is found by walks down, which need the chains of every live task loaded.
        await LoadScopesAsync(false).ConfigureAwait(false);
        lock (gate)
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
                var record = overlay.Tasks.GetValueOrDefault(id) ?? live.GetValueOrDefault(id);
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
    }

    /// <summary>Ends an invocation: its runtime operations reject from now on, its signal aborts, its watches stop, and its task is free.</summary>
    private void End(Invocation invocation)
    {
        List<IWatchHandle<JsonObjectValue?>> watches;
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
        }
        foreach (var watch in watches) _ = watch.StopAsync();
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
            report(error);
        }
    }

    /// <summary>No live non-background task in the scope; a task whose owner edges are not loaded yet counts as inside.</summary>
    private bool IdleLocked(ConversationId? conversationId)
    {
        var scope = conversationId is { } id ? Scope.Of(id) : Scope.Roots;
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
    /// As upstream's <c>{ ...rest, abortRequested: true, abortReason }</c>, a set reason is the record's last key.</summary>
    private static TaskRecord WithAbortMark(TaskRecord record, bool restart)
    {
        if (!restart) return record with { AbortRequested = true, AbortReason = null };
        var keys = record.ToJson().Keys.Where(key => key != "abortReason").Append("abortReason").ToImmutableArray();
        return record with { AbortRequested = true, AbortReason = TaskAbortReason.Restart, KeyOrder = keys };
    }

    /// <summary>A live owner's durable cancellation intent: its abort mark, or a held outcome other than completed.</summary>
    private static bool CancellationIntent(TaskRecord record) =>
        record.State.Status != DurableTaskStatus.Terminal && (record.AbortRequested || FailedOutcome(record));

    /// <summary>Whether the record holds or ends with an outcome other than completed.</summary>
    private static bool FailedOutcome(TaskRecord record) =>
        record.State is SettlingTaskState settling && settling.Outcome.Status != TaskOutcomeStatus.Completed;

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

    /// <summary>Replaces a live record's state; memos disappear once an outcome is decided.</summary>
    private static TaskRecord WithState(TaskRecord record, TaskState state) =>
        state is SettlingTaskState ? record with { State = state, Memos = null } : record with { State = state };

    /// <summary>Whether a definition can take the task at reservation: same version, or newer with a migration.</summary>
    private static bool CanReserve(DurableTask task, TaskRecord record)
    {
        var definition = task.Definition;
        return definition.Version == record.Version || (definition.Version > record.Version && definition.Migrate is not null);
    }

    private static string? PhaseOf(JsonValue checkpoint) =>
        checkpoint is JsonObjectValue value && value.TryGetValue("phase", out var phase) && phase is JsonStringValue name ? name.Value : null;

    // ─── Types ───────────────────────────────────────────────────────────────

    private enum InvocationMode { Run, Abort }

    /// <summary>One in-memory execution of a task in run or abort mode.</summary>
    private sealed class Invocation(TaskId taskId, ConversationId conversationId, InvocationMode mode, CancellationSignal controller,
        DurableContext context)
    {
        public TaskId TaskId { get; } = taskId;
        public ConversationId ConversationId { get; } = conversationId;
        public InvocationMode Mode { get; } = mode;
        public CancellationSignal Controller { get; } = controller;
        /// <summary>Context passed to handlers; cancelled by <see cref="Controller"/>.</summary>
        public DurableContext Context { get; } = context;
        /// <summary>Watches acquired through the runtime; stopped at invocation end. Guarded by the scheduler lock.</summary>
        public HashSet<IWatchHandle<JsonObjectValue?>> Watches { get; } = [];
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

    private enum DecisionKind { Continue, End, Fault }

    /// <summary>Step decision: continue with the next phase, end the invocation, or end it by writing faulted.</summary>
    private readonly record struct Decision(DecisionKind Kind, Exception? Error)
    {
        public static Decision Continue => new(DecisionKind.Continue, null);
        public static Decision End => new(DecisionKind.End, null);
        public static Decision Fault(Exception error) => new(DecisionKind.Fault, error);
    }

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
        public ContextRange Range { get; } = range;
        public double? IdleSince { get; set; } = idleSince;
    }

    private sealed class StrongBoxTimer
    {
        public ITimer? Timer { get; set; }
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
    private sealed class Runtime(DurableTaskScheduler scheduler, Invocation invocation, Phase phase) : ITaskRuntime, IHookRunner, ITaskTimeSource
    {
        public TimeProvider TimeProvider => scheduler.timeProvider;
        public TaskId TaskId => invocation.TaskId;
        public ConversationId ConversationId => invocation.ConversationId;
        public CancellationToken Signal => invocation.Controller.Token;
        public IRegistrySnapshot Registry => phase.Snapshot();
        public Settings Settings => scheduler.settings();
        public IDurableModels Models => scheduler.models;
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
            foreach (var handlers in Agents.AgentHooks(await PhaseAgent().ConfigureAwait(false), phase.Task().Definition.Name))
            {
                if (handlers is not THooks typed || select(typed) is not { } handler) continue;
                try
                {
                    await invoke(handler).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (invocation.Controller.Token.IsCancellationRequested) throw;
                    scheduler.report(error);
                }
            }
        }

        public Task<IExecutionEnv?> EnvAsync(DurableContext context) => Read(() => scheduler.env(invocation.ConversationId, context));

        public Task CommitAsync(Func<ITx, RunningTask, Task<TaskState?>> change, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(change);
            return Gated(async (tx, current) =>
            {
                var next = await change(tx, current).ConfigureAwait(false);
                if (next is not null) await scheduler.CommitStateAsync(tx, invocation, current, next).ConfigureAwait(false);
                return true;
            }, context);
        }

        public Task CommitAsync(Func<ITx, RunningTask, Task> change, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(change);
            return CommitAsync(async (tx, current) =>
            {
                await change(tx, current).ConfigureAwait(false);
                return (TaskState?)null;
            }, context);
        }

        public Task CommitAsync(Func<ITx, RunningTask, TaskState?> change, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(change);
            return CommitAsync((tx, current) => Task.FromResult(change(tx, current)), context);
        }

        public Task CommitAsync(Action<ITx, RunningTask> change, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(change);
            return CommitAsync((tx, current) =>
            {
                change(tx, current);
                return Task.FromResult<TaskState?>(null);
            }, context);
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

        public Task SleepAsync(double until, DurableContext context) => scheduler.SleepAsync(invocation, until, context);

        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
            Read(() => scheduler.session.ReadOnLineAsync(() => scheduler.storage.GetTaskAsync(id, context)));

        public Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context) =>
            Read(() => scheduler.WaitForTaskAsync(id, context.WithSignal(invocation.Controller)));

        public Task<IReadOnlyList<TaskRecord>> OwnedTasksAsync(DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Read(() => scheduler.session.ReadOnLineAsync<IReadOnlyList<TaskRecord>>(() =>
            {
                // The step that ends the invocation may have run on the line before this.
                if (invocation.Ended) throw EndedError(invocation);
                context.ThrowIfCancellationRequested();
                // Every live task is listed under its parent node, its owner task when it has one.
                var owned = new List<TaskRecord>();
                lock (scheduler.gate)
                {
                    if (scheduler.below.TryGetValue(Node.Of(invocation.TaskId), out var children))
                        foreach (var child in children)
                            if (!child.IsConversation && scheduler.live.TryGetValue(child.Task, out var record) && record.Owner == invocation.TaskId)
                                owned.Add(record);
                }
                // The records are immutable: upstream's copies are not needed.
                owned.Sort(static (left, right) => left.Id.CompareTo(right.Id));
                return Task.FromResult<IReadOnlyList<TaskRecord>>(owned);
            }));
        }

        public Task AbortOwnedAsync(TaskId id, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Read(async () =>
            {
                var bound = context.WithSignal(invocation.Controller);
                var record = await scheduler.session.ReadOnLineAsync(() => scheduler.storage.GetTaskAsync(id, bound)).ConfigureAwait(false);
                if (record?.Owner != invocation.TaskId) throw new InvalidOperationException($"Task {id} is not owned by task {invocation.TaskId}");
                if (record.State.Status == DurableTaskStatus.Terminal) return true;
                await scheduler.AbortAsync(id, bound, keepRestart: true).ConfigureAwait(false);
                await scheduler.WaitForTaskAsync(id, bound).ConfigureAwait(false);
                return true;
            });
        }

        public Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(IReadOnlyList<TaskId> ids, DurableContext context) => Read(() =>
            scheduler.session.ReadOnLineAsync<IReadOnlyList<TaskOutcome>>(async () =>
            {
                var outcomes = new List<TaskOutcome>();
                foreach (var id in ids)
                {
                    var state = (await scheduler.storage.GetTaskAsync(id, context).ConfigureAwait(false))?.State;
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

        private Task<EntryRecord?> ReadEntry(Entry? token, EntryId id, DurableContext context) => Read(async () =>
        {
            var found = await scheduler.session.ReadOnLineAsync(() => scheduler.storage.GetEntryAsync(invocation.ConversationId, id, context))
                .ConfigureAwait(false);
            var entry = found?.Entry;
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
            lock (scheduler.gate)
            {
                // Keep it unless the invocation ended or a concurrent read already kept a newer range.
                var kept = scheduler.contexts.GetValueOrDefault(conversationId);
                if (range is not null && !scheduler.closing && !invocation.Ended && (kept is null || kept.Range.Bounds.Tail.Value <= range.Bounds.Tail.Value))
                {
                    // A read of another, idle conversation starts or continues its retention period.
                    if (!scheduler.IdleLocked(conversationId)) scheduler.contexts[conversationId] = new KeptContext(range, null);
                    else if (retention > 0)
                    {
                        scheduler.contexts[conversationId] = new KeptContext(range, kept?.IdleSince ?? stamp);
                        scheduler.ScheduleExpiryLocked(retention, stamp);
                    }
                }
            }
            return view;
        });

        public double Now()
        {
            if (invocation.Ended) throw EndedError(invocation);
            return scheduler.now();
        }

        public void Report(Exception error)
        {
            if (invocation.Ended) throw EndedError(invocation);
            scheduler.report(error);
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

        public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocToken token, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, context));
        public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, conversationId, context));
        public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, taskId, context));
        public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, key, context));
        public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
            DurableContext context) => Watch(() => scheduler.session.WatchDocAsync(token, conversationId, key, context));
        public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
            Watch(() => scheduler.session.WatchDocAsync(token, taskId, key, context));

        private async Task<IWatchHandle<JsonObjectValue?>?> Watch(Func<Task<IWatchHandle<JsonObjectValue?>?>> acquire)
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

        /// <summary>Commits after rereading the task on the line and gating the invocation.</summary>
        private Task<T> Gated<T>(Func<Transaction, RunningTask, Task<T>> change, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (invocation.Ended) return Task.FromException<T>(EndedError(invocation));
            return scheduler.session.CommitWithAsync(async tx =>
            {
                TaskRecord? found;
                lock (scheduler.gate)
                {
                    if (invocation.Ended) throw EndedError(invocation);
                    if (scheduler.closing) throw HarnessUtil.ClosedError(scheduler.session);
                    found = scheduler.live.GetValueOrDefault(invocation.TaskId);
                }
                if (found is null) throw new InvalidOperationException($"Task {invocation.TaskId} is terminal");
                if (found.State.Status != DurableTaskStatus.Running)
                    throw new InvalidOperationException($"Task {invocation.TaskId} is {WireName.Of(found.State.Status)}");
                if (invocation.Mode == InvocationMode.Run && found.AbortRequested)
                    throw new InvalidOperationException($"Task {invocation.TaskId} has a durable abort mark");
                return await change(tx, new RunningTask(found)).ConfigureAwait(false);
            }, context, new TransactionScope(invocation.ConversationId, invocation.TaskId));
        }
    }

    /// <summary>Waits until the Harness clock reaches <paramref name="until"/>, rechecking it after every timer.</summary>
    private async Task SleepAsync(Invocation invocation, double until, DurableContext callContext)
    {
        if (invocation.Ended) throw EndedError(invocation);
        var signal = callContext.Signal is { } own ? CancellationSignal.Any(invocation.Controller, own) : invocation.Controller;
        while (true)
        {
            if (signal.Token.IsCancellationRequested) throw signal.Error();
            var remaining = until - now();
            if (remaining <= 0) return;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining, MaxTimerDelay)), timeProvider, signal.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (signal.Token.IsCancellationRequested)
            {
                throw signal.Error();
            }
        }
    }
}
