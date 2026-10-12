// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/scheduler.ts. Named DurableTaskScheduler because
// System.Threading.Tasks.TaskScheduler is used throughout the assembly.
//
// Concurrency model. Upstream runs on one JavaScript thread: commit listeners, line callbacks and handler code never overlap, and wakeups
// are queued microtasks. Here:
// - Every transition is still decided by one callback serialized on the Session line (reservation, marks, runtime commits, steps,
//   reconciliation), so the durable order is upstream's.
// - The in-memory mirror (live records, invocations, owner edges, kept contexts, flags) is also read off the line (idle checks, runtime
//   memo reads, close), so it is guarded by one short lock. The lock is held only for synchronous bookkeeping: never across an await,
//   never while signalling an invocation, never around host code (handlers, hooks, settings getters, the clock, onReport, registry
//   readers); the one exception is the registry snapshot's own lookup methods during inspection, which are pure reads.
// - Upstream's queueMicrotask wakeups (#kick, #scheduleReconcile) become thread-pool work items, so nothing commits synchronously from a
//   commit or registry listener. Invocations then run their handlers on the thread pool, possibly in parallel with each other.
// - AbortController becomes CancellationSignal (a token plus its reason); an invocation's handler context is the scheduler context bound
//   to that signal, so waits bound to it fail with the reason (the ended error, or OperationCanceledException for an abort mark or close,
//   upstream's AbortError).
// - setTimeout becomes HarnessOptions.TimeProvider timers: sleeps wait in Task.Delay steps and recheck the Harness clock; the idle context
//   expiry is one ITimer. .NET timers never keep a process alive, so the expiry timer is always kept (upstream keeps it only where it can
//   be unreferenced).
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
/// parent is its owner task, if any. Walks up that tree decide cascades, idle scopes, and whether a task's ordinary owned work is live, which
/// holds its outcome as <c>completing</c> and delays its abort handler.</para>
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
    /// <summary>Owner task of each loaded conversation, null when ownerless.</summary>
    private readonly Dictionary<ConversationId, TaskId?> edges = [];
    /// <summary>Tasks that own a loaded conversation; their nodes stay in <see cref="settled"/> once terminal.</summary>
    private readonly HashSet<TaskId> conversationOwners = [];
    /// <summary>Ownership fields of terminal tasks that walks pass through.</summary>
    private readonly Dictionary<TaskId, TaskNode> settled = [];
    /// <summary><c>failFast</c> waiters the next reconcile checks for a failed task in <c>on</c>: at open, when they start waiting, and when
    /// one of their tasks fails. Ordered as upstream's Set.</summary>
    private readonly OrderedDictionary<TaskId, bool> failFastChecks = [];
    private bool reconcileScheduled;
    private bool cascadePending;
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
    }

    /// <summary>Loads live tasks and changes surviving <c>running</c> tasks back to <c>pending</c>. Dispatches nothing.</summary>
    public async Task OpenAsync(DurableContext openContext)
    {
        session.SubscribeCommits((publication, _) => Observe(publication));
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
                        live[record.Id] = record;
                        if (record.State is RunningTaskState running) tx.SetTask(WithState(record, new PendingTaskState(running.Checkpoint)));
                        if (record.State is WaitingTaskState { Policy: JoinPolicy.FailFast }) failFastChecks[record.Id] = true;
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

    /// <summary>Enables scheduling. Idempotent; the kick does nothing once closing.</summary>
    public void Resume()
    {
        lock (gate) enabled = true;
        Kick();
    }

    /// <summary>Waits for every invocation signalled by <see cref="Seal"/>. Writes nothing.</summary>
    public Task JoinAsync()
    {
        Task[] done;
        lock (gate) done = [.. invocations.Values.Select(invocation => invocation.Done.Task)];
        return Task.WhenAll(done);
    }

    /// <summary>
    /// Commits the abort mark, or settles a task that no registered definition can take as <c>orphaned</c> when nothing it owns is live, then
    /// joins the run invocation seen on the line; the commit listener signalled it. The abort invocation starts once the task's ordinary owned
    /// work is gone. A <c>completing</c> task is only marked.
    /// </summary>
    public async Task<TaskAbortResult> AbortAsync(TaskId id, DurableContext callContext)
    {
        var (result, run) = await session.CommitWithAsync<(TaskAbortResult, Invocation?)>(async tx =>
        {
            var current = await tx.GetTaskAsync(id).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task {id} does not exist");
            if (current.State.Status == DurableTaskStatus.Terminal) return (TaskAbortResult.Terminal, null);
            Invocation? invocation;
            lock (gate) invocation = invocations.GetValueOrDefault(id);
            if (invocation is null && current.State.Status != DurableTaskStatus.Completing)
            {
                await LoadScopesAsync(false).ConfigureAwait(false);
                bool owned;
                lock (gate) owned = OwnedLive(null).ContainsKey(id);
                if (!owned && Resolve(current, registry.Snapshot()) is { Blocked: { } reason })
                {
                    await TerminateAsync(tx, current, new OrphanedTaskOutcome(TaskInspectionState.ReasonName(reason))).ConfigureAwait(false);
                    return (TaskAbortResult.Marked, null);
                }
            }
            if (!current.AbortRequested) tx.SetTask(current with { AbortRequested = true });
            return (TaskAbortResult.Marked, invocation?.Mode == InvocationMode.Run ? invocation : null);
        }, callContext).ConfigureAwait(false);
        // The commit listener signalled the run; join it.
        if (run is not null) await callContext.WaitAsync(run.Done.Task).ConfigureAwait(false);
        return result;
    }

    /// <summary>Resolves with the terminal receipt; checks and registers on the line so no terminal publication falls between them.</summary>
    public async Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext callContext)
    {
        var found = await session.ReadOnLineAsync(async () =>
        {
            lock (gate)
            {
                if (closing) throw HarnessUtil.ClosedError();
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
            if (closing) return Task.FromException(HarnessUtil.ClosedError());
            if (Idle(conversationId)) return Task.CompletedTask;
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
                foreach (var record in live.Values)
                {
                    if (record.Background && !background) continue;
                    if (InScope(ParentOf(record), scope, background) != true) continue;
                    reached.Add(record.Id);
                    if (!record.AbortRequested) tx.SetTask(record with { AbortRequested = true });
                }
            }
            foreach (var id in queued)
            {
                bool inScope;
                lock (gate) inScope = InScope(Up.Of(id), scope, background) == true;
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
        bool changed;
        lock (gate)
        {
            var updated = new List<TaskRecord>();
            var failed = new List<TaskId>();
            changed = false;
            foreach (var change in publication.Changes)
            {
                if (change is not TaskWrite write) continue;
                changed = true;
                var record = write.Value;
                var previous = live.GetValueOrDefault(record.Id);
                if (FailedOutcome(record) && (previous is null || !FailedOutcome(previous))) failed.Add(record.Id);
                if (record.State.Status == DurableTaskStatus.Terminal)
                {
                    live.Remove(record.Id);
                    failedMigrations.Remove(record.Id);
                    failFastChecks.Remove(record.Id);
                    if (conversationOwners.Contains(record.Id)) settled[record.Id] = NodeOf(record);
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
                var status = record.State.Status;
                if (status == DurableTaskStatus.Completing && previous?.State.Status != DurableTaskStatus.Completing)
                {
                    if (CancellationIntent(record)) cascadePending = true;
                    ScheduleReconcileLocked();
                }
                if (record.State is WaitingTaskState { Policy: JoinPolicy.FailFast } && previous?.State.Status != DurableTaskStatus.Waiting)
                {
                    failFastChecks[record.Id] = true;
                    ScheduleReconcileLocked();
                }
                live[record.Id] = record;
                updated.Add(record);
            }
            foreach (var id in failed)
            {
                foreach (var record in live.Values)
                {
                    if (record.State is WaitingTaskState { Policy: JoinPolicy.FailFast } waiting && waiting.On.Contains(id))
                    {
                        failFastChecks[record.Id] = true;
                        ScheduleReconcileLocked();
                    }
                }
            }
            foreach (var change in publication.Changes)
                if (change is ConversationWrite created && !edges.ContainsKey(created.Value.Id)) SetEdge(created.Value.Id, created.Value.Owner?.TaskId);
            foreach (var change in publication.Changes)
            {
                // A queued input below a cancelled owner is withdrawn, even after its cascade.
                if (change is not SubmissionWrite { Value: { Status: SubmissionStatus.Queued, Type: SubmissionType.Input } submission }) continue;
                var up = Up.Of(submission.ConversationId);
                if (!ChainKnown(up, null) || BelowCancelled(up)) cascadePending = true;
            }
            foreach (var record in updated)
            {
                // Work created below a cancelled owner, even after its cascade, is aborted too.
                if (!ChainKnown(ParentOf(record), null)) ScheduleReconcileLocked();
                else if (!record.Background && !record.AbortRequested && BelowCancelled(ParentOf(record))) cascadePending = true;
            }
            // Also retries, with the next commit of any kind, a cascade whose commit failed.
            if (cascadePending) ScheduleReconcileLocked();
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
                if (Idle(conversationId)) idleWaiters.Resolve(conversationId, true);
            foreach (var (conversationId, kept) in contexts.ToList())
            {
                if (kept.IdleSince is { } since && at - since >= retention) contexts.Remove(conversationId);
                else if (!Idle(conversationId)) kept.IdleSince = null;
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
            cascade = cascadePending;
            cascadePending = false;
            checks = [.. failFastChecks.Keys];
            failFastChecks.Clear();
        }
        try
        {
            await session.CommitWithAsync(async tx =>
            {
                lock (gate) if (closing) return;
                var queued = await LoadScopesAsync(cascade).ConfigureAwait(false);
                var marked = new HashSet<TaskId>();
                void Mark(TaskRecord record)
                {
                    if (record.AbortRequested || !marked.Add(record.Id)) return;
                    tx.SetTask(record with { AbortRequested = true });
                }
                // Loading edges can reveal a cancelled owner, so marks are derived on every pass.
                lock (gate)
                    foreach (var record in live.Values.ToList())
                        if (!record.Background && BelowCancelled(ParentOf(record))) Mark(record);
                foreach (var id in checks)
                {
                    TaskRecord? waiter;
                    lock (gate) waiter = live.GetValueOrDefault(id);
                    if (waiter?.State is not WaitingTaskState waiting || !await AnyFailedAsync(waiting.On).ConfigureAwait(false)) continue;
                    // Every other live task: the failed one keeps its own outcome.
                    lock (gate)
                        foreach (var member in waiting.On)
                            if (live.TryGetValue(member, out var record) && !FailedOutcome(record)) Mark(record);
                }
                foreach (var id in queued)
                {
                    bool below;
                    lock (gate) below = BelowCancelled(Up.Of(id));
                    if (below) await withdrawInputs(tx, id).ConfigureAwait(false);
                }
                await FinalizeAsync(tx).ConfigureAwait(false);
            }, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            bool reportIt;
            lock (gate)
            {
                // Any pass may have staged marks, so a failed one is retried with the next commit.
                cascadePending = true;
                foreach (var id in checks) failFastChecks[id] = true;
                reportIt = !closing;
            }
            if (reportIt) report(error);
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
        while (true)
        {
            List<TaskRecord> done;
            lock (gate)
            {
                var overlay = OverlayOf(tx);
                var owned = OwnedLive(overlay);
                done = [.. LiveRecords(overlay).Where(record => record.State.Status == DurableTaskStatus.Completing && !owned.ContainsKey(record.Id))];
            }
            if (done.Count == 0) return;
            foreach (var record in done)
            {
                var outcome = ((CompletingTaskState)record.State).Outcome;
                tx.SetTask(WithState(record, new TerminalTaskState(outcome)));
                // Only the scheduler writes faulted and orphaned (spec §5.4); their cleanup waits for this commit.
                if (outcome is FaultedTaskOutcome or OrphanedTaskOutcome) await settleOutcome(tx, record, outcome).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Loads the owner chains of every live task and, with <paramref name="queued"/>, of every conversation with queued submissions, on the
    /// Session line; returns the latter. Reads committed Storage directly, so it may run inside a commit callback.
    /// </summary>
    private async Task<List<ConversationId>> LoadScopesAsync(bool queued)
    {
        List<TaskRecord> records;
        lock (gate) records = [.. live.Values];
        foreach (var record in records)
        {
            bool known;
            lock (gate) known = ChainKnown(ParentOf(record), null);
            if (!known) await LoadChainAsync(ParentOf(record), null).ConfigureAwait(false);
        }
        if (!queued) return [];
        var submissions = await HarnessUtil.ScanAll(cursor =>
            storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Queued }, ScanPageSize, cursor, context)).ConfigureAwait(false);
        var conversations = submissions.Select(submission => submission.ConversationId).Distinct().ToList();
        foreach (var id in conversations) await LoadChainAsync(Up.Of(id), null).ConfigureAwait(false);
        return conversations;
    }

    /// <summary>Loads the owner edges and task nodes from <paramref name="start"/> up to its ownerless root.</summary>
    private async Task LoadChainAsync(Up start, Overlay? overlay)
    {
        Up? at = start;
        while (at is { } current)
        {
            if (current.IsTask)
            {
                TaskNode? node;
                lock (gate) node = Node(current.Task, overlay);
                if (node is null)
                {
                    var record = await storage.GetTaskAsync(current.Task, context).ConfigureAwait(false);
                    if (record is null) return;
                    node = NodeOf(record);
                    if (record.State.Status == DurableTaskStatus.Terminal) lock (gate) settled[record.Id] = node.Value;
                }
                at = ParentOf(node.Value);
            }
            else
            {
                bool known;
                TaskId? owner;
                lock (gate) known = TryEdge(current.Conversation, overlay, out owner);
                if (!known)
                {
                    var record = await storage.GetConversationAsync(current.Conversation, context).ConfigureAwait(false);
                    owner = record?.Owner?.TaskId;
                    lock (gate) SetEdge(current.Conversation, owner);
                }
                at = owner is { } task ? Up.Of(task) : null;
            }
        }
    }

    private void SetEdge(ConversationId conversationId, TaskId? owner)
    {
        edges[conversationId] = owner;
        if (owner is { } task) conversationOwners.Add(task);
    }

    /// <summary>Owner task of a conversation (null when ownerless); false while not loaded.</summary>
    private bool TryEdge(ConversationId id, Overlay? overlay, out TaskId? owner)
    {
        if (overlay is not null && overlay.Edges.TryGetValue(id, out owner)) return true;
        return edges.TryGetValue(id, out owner);
    }

    private TaskNode? Node(TaskId id, Overlay? overlay)
    {
        if (overlay is not null && overlay.Tasks.TryGetValue(id, out var candidate)) return NodeOf(candidate);
        if (live.TryGetValue(id, out var record)) return NodeOf(record);
        return settled.TryGetValue(id, out var node) ? node : null;
    }

    /// <summary>Walks up from <paramref name="start"/>: owner tasks and conversations, ending at an ownerless root or an edge not loaded
    /// yet.</summary>
    private List<Step> Above(Up start, Overlay? overlay)
    {
        var steps = new List<Step>();
        Up? at = start;
        while (at is { } current)
        {
            if (current.IsTask)
            {
                if (Node(current.Task, overlay) is not { } node)
                {
                    steps.Add(Step.Unknown);
                    return steps;
                }
                steps.Add(Step.OfTask(current.Task, node));
                at = ParentOf(node);
            }
            else
            {
                steps.Add(Step.OfConversation(current.Conversation));
                if (!TryEdge(current.Conversation, overlay, out var owner))
                {
                    steps.Add(Step.Unknown);
                    return steps;
                }
                at = owner is { } task ? Up.Of(task) : null;
            }
        }
        return steps;
    }

    /// <summary>Whether every owner above <paramref name="start"/> is loaded.</summary>
    private bool ChainKnown(Up start, Overlay? overlay) => !Above(start, overlay).Any(step => step.Kind == StepKind.Unknown);

    /// <summary>Live records, with the overlay's candidates replacing committed ones; terminal candidates are gone.</summary>
    private IEnumerable<TaskRecord> LiveRecords(Overlay? overlay)
    {
        foreach (var record in live.Values)
        {
            var candidate = overlay?.Tasks.GetValueOrDefault(record.Id) ?? record;
            if (candidate.State.Status != DurableTaskStatus.Terminal) yield return candidate;
        }
        if (overlay is null) yield break;
        foreach (var record in overlay.Tasks.Values)
            if (!live.ContainsKey(record.Id) && record.State.Status != DurableTaskStatus.Terminal) yield return record;
    }

    /// <summary>
    /// Every task with live ordinary owned work (spec §5.5), mapped to that work: each live non-background task counts for every owner task
    /// above it up to and including the first background one. Owner chains must be loaded.
    /// </summary>
    private Dictionary<TaskId, List<TaskId>> OwnedLive(Overlay? overlay)
    {
        var owned = new Dictionary<TaskId, List<TaskId>>();
        foreach (var record in LiveRecords(overlay).ToList())
        {
            if (record.Background) continue;
            foreach (var step in Above(ParentOf(record), overlay))
            {
                if (step.Kind == StepKind.Unknown) break;
                if (step.Kind != StepKind.Task) continue;
                if (owned.TryGetValue(step.Task, out var below)) below.Add(record.Id);
                else owned[step.Task] = [record.Id];
                if (step.Node.Background) break;
            }
        }
        return owned;
    }

    /// <summary>
    /// Whether ordinary traversal from <paramref name="scope"/> reaches <paramref name="start"/>: walking up reaches the scope's conversation,
    /// or an ownerless one for every root, without crossing a background owner, unless <paramref name="crossBackground"/>. Null while an edge
    /// is not loaded.
    /// </summary>
    private bool? InScope(Up start, Scope scope, bool crossBackground = false)
    {
        foreach (var step in Above(start, null))
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
    private bool BelowCancelled(Up start)
    {
        foreach (var step in Above(start, null))
        {
            if (step.Kind == StepKind.Unknown) return false;
            if (step.Kind != StepKind.Task) continue;
            if (live.TryGetValue(step.Task, out var owner) && CancellationIntent(owner)) return true;
            if (step.Node.Background) return false;
        }
        return false;
    }

    /// <summary>Close listener: runs synchronously once admission is sealed, before <see cref="JoinAsync"/>.</summary>
    private void Seal()
    {
        List<Invocation> signal;
        lock (gate)
        {
            closing = true;
            contexts.Clear();
            expiry?.Timer.Dispose();
            expiry = null;
            signal = [.. invocations.Values];
            var error = HarnessUtil.ClosedError();
            taskWaiters.RejectAll(error);
            idleWaiters.RejectAll(error);
        }
        registrySubscription?.Dispose();
        foreach (var invocation in signal) Signal(invocation, null);
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
                foreach (var reservation in await ReserveAsync().ConfigureAwait(false)) Start(reservation);
            }
        }
        catch (Exception error)
        {
            bool reportIt;
            lock (gate) reportIt = !closing;
            if (reportIt) report(error);
        }
        finally
        {
            bool again;
            lock (gate)
            {
                draining = false;
                again = dirty;
            }
            // A wakeup that arrived during a failed pass still needs its pass.
            if (again) Kick();
        }
    }

    /// <summary>Reserves every eligible task in one commit; orphans abort-marked tasks no definition can take.</summary>
    private async Task<List<Reservation>> ReserveAsync()
    {
        var reservations = new List<Reservation>();
        try
        {
            await session.CommitWithAsync(async tx =>
            {
                lock (gate) if (!enabled || closing) return;
                await LoadScopesAsync(false).ConfigureAwait(false);
                Dictionary<TaskId, List<TaskId>> owned;
                List<TaskRecord> records;
                lock (gate)
                {
                    owned = OwnedLive(null);
                    records = [.. live.Values];
                }
                // Taken once per pass, and only when some task is a candidate.
                IRegistrySnapshot? snapshot = null;
                foreach (var record in records)
                {
                    lock (gate) if (invocations.ContainsKey(record.Id) || WaitingOn(record, owned).Count > 0) continue;
                    if (record.State.Status == DurableTaskStatus.Completing) continue;
                    var mode = record.AbortRequested ? InvocationMode.Abort : InvocationMode.Run;
                    snapshot ??= registry.Snapshot();
                    var resolution = Resolve(record, snapshot);
                    if (resolution.Blocked is { } reason)
                    {
                        if (mode == InvocationMode.Abort)
                            await TerminateAsync(tx, record, new OrphanedTaskOutcome(TaskInspectionState.ReasonName(reason))).ConfigureAwait(false);
                        continue;
                    }
                    var resolved = resolution.Record!;
                    if (!ReferenceEquals(resolved, record) || record.State.Status != DurableTaskStatus.Running)
                        tx.SetTask(WithState(resolved, new RunningTaskState(((LiveTaskState)resolved.State).Checkpoint)));
                    // Registered on the line, so marks and later reservations see it and close joins it.
                    var invocation = CreateInvocation(record, mode);
                    reservations.Add(new Reservation(invocation, resolution.Task!, snapshot));
                }
            }, context).ConfigureAwait(false);
        }
        catch
        {
            foreach (var reservation in reservations)
            {
                lock (gate) invocations.Remove(reservation.Invocation.TaskId);
                reservation.Invocation.Done.TrySetResult();
            }
            throw;
        }
        return reservations;
    }

    /// <summary>
    /// Live tasks a task waits for before its next invocation: its live ordinary owned work when abort-marked, since abort runs bottom-up,
    /// otherwise the live part of the <c>on</c> of a wait.
    /// </summary>
    private List<TaskId> WaitingOn(TaskRecord record, Dictionary<TaskId, List<TaskId>> owned)
    {
        if (record.AbortRequested) return owned.GetValueOrDefault(record.Id) ?? [];
        if (record.State is not WaitingTaskState waiting) return [];
        return [.. waiting.On.Where(live.ContainsKey)];
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
            var owned = OwnedLive(null);
            var tasks = live.Values.Select(record => new TaskInspection(record, InspectTask(record, snapshot, owned))).ToImmutableArray();
            var scheduling = closing ? HarnessScheduling.Closing : enabled ? HarnessScheduling.Running : HarnessScheduling.Paused;
            return (scheduling, tasks);
        }
    }

    private TaskInspectionState InspectTask(TaskRecord record, IRegistrySnapshot snapshot, Dictionary<TaskId, List<TaskId>> owned)
    {
        if (invocations.ContainsKey(record.Id)) return new TaskInspectionState(TaskInspectionKind.Running);
        if (record.State.Status == DurableTaskStatus.Completing) return new TaskInspectionState(TaskInspectionKind.Completing);
        var on = WaitingOn(record, owned);
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
        TaskRecord? found;
        bool closingNow;
        lock (gate)
        {
            found = live.GetValueOrDefault(invocation.TaskId);
            closingNow = closing;
        }
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
                    await TerminateAsync(tx, current!.Record, new FaultedTaskOutcome(new TaskOutcomeError(decision.Error!.Message))).ConfigureAwait(false);
                return null;
            }, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            End(invocation);
            bool reportIt;
            lock (gate) reportIt = !closing;
            if (reportIt) report(error);
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
        bool owned;
        lock (gate) owned = OwnedLive(OverlayOf(tx)).ContainsKey(record.Id);
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
            lock (gate) owned = OwnedLive(overlay).ContainsKey(current.Id);
            if (owned)
            {
                tx.SetTask(WithState(current.Record, new CompletingTaskState(terminal.Outcome)));
                return;
            }
        }
        tx.SetTask(WithState(current.Record, next));
    }

    /// <summary>
    /// A wait names existing tasks other than the waiter and its owners, which could never finish first; <c>failFast</c> only tasks the waiter
    /// owns. An abort handler cannot wait.
    /// </summary>
    private async Task ValidateWaitAsync(Transaction tx, Invocation invocation, RunningTask current, ImmutableArray<TaskId> on, JoinPolicy policy)
    {
        if (invocation.Mode == InvocationMode.Abort) throw new InvalidOperationException($"Abort handler of task {current.Id} cannot wait");
        var overlay = OverlayOf(tx);
        await LoadChainAsync(ParentOf(current.Record), null).ConfigureAwait(false);
        HashSet<TaskId> owners;
        lock (gate) owners = [.. Above(ParentOf(current.Record), null).Where(step => step.Kind == StepKind.Task).Select(step => step.Task)];
        foreach (var id in on)
        {
            if (id == current.Id || owners.Contains(id)) throw new InvalidOperationException($"Task {current.Id} cannot wait on itself or its owner {id}");
            TaskRecord? member;
            lock (gate) member = overlay.Tasks.GetValueOrDefault(id) ?? live.GetValueOrDefault(id);
            member ??= await storage.GetTaskAsync(id, context).ConfigureAwait(false);
            if (member is null) throw new InvalidOperationException($"Task {id} does not exist");
            if (policy == JoinPolicy.FailFast && member.Owner != current.Id)
                throw new InvalidOperationException($"Task {current.Id} can wait failFast only on tasks it owns; {id} is not one");
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
            if (invocations.TryGetValue(invocation.TaskId, out var registered) && ReferenceEquals(registered, invocation)) invocations.Remove(invocation.TaskId);
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

    /// <summary>No live non-background task in the scope; a task whose owner edges are not loaded yet counts as inside. Under the lock.</summary>
    private bool Idle(ConversationId? conversationId)
    {
        var scope = conversationId is { } id ? Scope.Of(id) : Scope.Roots;
        foreach (var record in live.Values)
            if (!record.Background && InScope(ParentOf(record), scope) != false) return false;
        return true;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A live owner's durable cancellation intent: its abort mark, or a held outcome other than completed.</summary>
    private static bool CancellationIntent(TaskRecord record) =>
        record.State.Status != DurableTaskStatus.Terminal && (record.AbortRequested || FailedOutcome(record));

    /// <summary>Whether the record holds or ends with an outcome other than completed.</summary>
    private static bool FailedOutcome(TaskRecord record) =>
        record.State is SettlingTaskState settling && settling.Outcome.Status != TaskOutcomeStatus.Completed;

    private static Up ParentOf(TaskRecord record) => record.Owner is { } owner ? Up.Of(owner) : Up.Of(record.ConversationId);

    private static Up ParentOf(TaskNode node) => node.Owner is { } owner ? Up.Of(owner) : Up.Of(node.ConversationId);

    private static TaskNode NodeOf(TaskRecord record) => new(record.ConversationId, record.Owner, record.Background);

    private static Overlay OverlayOf(Transaction tx)
    {
        var tasks = new Dictionary<TaskId, TaskRecord>();
        foreach (var record in tx.StagedTasks()) tasks[record.Id] = record;
        var conversations = new Dictionary<ConversationId, TaskId?>();
        foreach (var record in tx.StagedConversations()) conversations[record.Id] = record.Owner?.TaskId;
        return new Overlay(tasks, conversations);
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

    private sealed record Reservation(Invocation Invocation, DurableTask Task, IRegistrySnapshot Snapshot);

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
    private sealed record Overlay(Dictionary<TaskId, TaskRecord> Tasks, Dictionary<ConversationId, TaskId?> Edges);

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

    // ─── Invocation runtime ──────────────────────────────────────────────────

    /// <summary>The task runtime of one invocation (upstream <c>#runtime</c>).</summary>
    private sealed class Runtime(DurableTaskScheduler scheduler, Invocation invocation, Phase phase) : ITaskRuntime, IHookRunner
    {
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
                    if (!scheduler.Idle(conversationId)) scheduler.contexts[conversationId] = new KeptContext(range, null);
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
                    if (scheduler.closing) throw HarnessUtil.ClosedError();
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
