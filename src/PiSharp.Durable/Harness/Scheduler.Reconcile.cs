using System.Collections;
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

internal sealed partial class DurableTaskScheduler
{
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
        // The clock and the settings are host code, read before the lock: only a commit that appends entries can advance a kept range, and
        // only one that changes a task settles idleness.
        var tasks = publication.Changes.Any(change => change is TaskWrite);
        var at = tasks || publication.Changes.Any(change => change is EntryWrite) ? now() : 0;
        var retention = tasks ? ContextRetentionMs() : 0;
        // The waits a terminal commit resolves, resumed after host observers saw it, as the abort signals and the idle waits are.
        var resumes = new List<Action>();
        var idle = new List<Action>();
        lock (gate)
        {
            AdvanceContextsLocked(publication, at);
            var updated = new List<TaskRecord>();
            var failed = new List<TaskId>();
            // The conversations whose idleness the commit's task changes can change; null when a chain is not known.
            HashSet<ConversationId>? scopes = [];
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
                // Idleness counts live tasks, so only a task that became live or ended can change it, and the inactivity of kept contexts
                // also a task that parked or woke. Collected before SweepLocked, which may forget the chain of a task that ended.
                var liveness = previous is null || record.State.Status == DurableTaskStatus.Terminal || Parked(previous) != Parked(record);
                if (liveness && scopes is not null && !AddScopesAboveLocked(record, scopes)) scopes = null;
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
            // Under the same lock as the task changes, as upstream's observe settles in the same synchronous call: the kept contexts' idle
            // state never disagrees with the published tasks.
            if (changed) SettleIdleLocked(scopes, at, retention, idle);
        }
        CountActive();
        // Task code, and what the waits resume, never runs before host observers saw the commit (upstream: the aborts follow in a
        // microtask and the waits in promise continuations, after the dispatch to host observers).
        if (resumes.Count > 0 || signal.Count > 0 || idle.Count > 0)
            session.AfterPublication(() =>
            {
                foreach (var resume in resumes) resume();
                foreach (var invocation in signal) Signal(invocation, null);
                foreach (var resume in idle) resume();
            });
        if (!changed) return;
        ApplyExpiry();
        Kick(onLine: createsTask && keepsRunning);
    }

    /// <summary>
    /// Adds the conversations above <paramref name="record"/> to <paramref name="scopes"/>: those whose idleness a change of it can change
    /// (upstream <c>#addScopesAbove</c>). False when an owner edge or owner task on the way up is not known. A conversation already in
    /// <paramref name="scopes"/> has everything above it there too. A child task lives in its owner's conversation, so the first
    /// conversation above a task is its own: the walk skips the owner chain between, which made a change deep in a long chain cost its
    /// depth (Pi v1.6.0).
    /// </summary>
    private bool AddScopesAboveLocked(TaskRecord record, HashSet<ConversationId> scopes)
    {
        var conversation = record.ConversationId;
        while (scopes.Add(conversation))
        {
            if (!EdgeLocked(conversation, null, out var owner)) return false;
            if (owner is not { } task) return true;
            if (NodeLocked(task, null) is not { } node) return false;
            conversation = node.ConversationId;
        }
        return true;
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
                DropContextLocked(conversationId);
                continue;
            }
            if (ConversationContext.AdvanceRange(kept.Range, entries) is { } range) kept.Range = range;
            else DropContextLocked(conversationId);
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

    /// <summary>
    /// After task changes below the conversations in <paramref name="scopes"/> (every conversation when null): resolves their idle waiters
    /// and the Harness-wide ones, drops the kept contexts whose idle period passed the retention, then starts or ends the idle periods of the
    /// kept contexts in <paramref name="scopes"/> (upstream <c>#settleIdle</c>). A task change can change the idleness only of the
    /// conversations above it, so no check visits every conversation. The clock (<paramref name="at"/>) and the retention are host code,
    /// read by the caller before the lock; the waits it takes are added to <paramref name="resumes"/>, and the caller runs
    /// <see cref="ApplyExpiry"/> out of the lock.
    /// </summary>
    private void SettleIdleLocked(IReadOnlySet<ConversationId>? scopes, double at, double retention, List<Action> resumes)
    {
        var all = scopes is null || unknownsChanged;
        unknownsChanged = false;
        if (all) fullIdleChecks++;
        ResolveIdleWaitersLocked(all ? null : scopes, resumes);
        // Expired first: a context past its retention is dropped even when the same commit makes it busy again.
        ExpireContextsLocked(at, retention);
        IEnumerable<ConversationId> checks = all ? [.. contexts.Keys] : scopes!;
        foreach (var conversationId in checks)
        {
            if (!contexts.TryGetValue(conversationId, out var kept)) continue;
            if (!InactiveLocked(conversationId))
            {
                if (kept.IdleSince is null) continue;
                kept.IdleSince = null;
                idleOrder.Remove(conversationId);
            }
            else if (retention <= 0) DropContextLocked(conversationId);
            else if (kept.IdleSince is null)
            {
                kept.IdleSince = at;
                kept.ExpiresAt = at + retention;
                MarkIdleLocked(conversationId, at);
            }
        }
        ComputeExpiryLocked(retention);
    }

    /// <summary>Takes the waits of the idle waiters of the conversations in <paramref name="scopes"/> (all when null) and of the Harness-wide
    /// ones, those that are idle now (upstream <c>#resolveIdleWaiters</c>).</summary>
    private void ResolveIdleWaitersLocked(IReadOnlySet<ConversationId>? scopes, List<Action> resumes)
    {
        foreach (var conversationId in idleWaiters.Keys())
        {
            if (conversationId is { } id && scopes is not null && !scopes.Contains(id)) continue;
            if (IdleLocked(conversationId) && idleWaiters.Take(conversationId, true) is { } resume) resumes.Add(resume);
        }
    }

    /// <summary>Resumes idle waits at a publication, after its host observers (upstream: promise continuations behind their dispatch); else at
    /// once.</summary>
    private void ResumeAfterPublication(List<Action> resumes)
    {
        if (resumes.Count > 0)
            session.AfterPublication(() =>
            {
                foreach (var resume in resumes) resume();
            });
    }

    /// <summary>
    /// Drops the kept contexts idle for at least <paramref name="retention"/>, oldest first (upstream <c>#expireContexts</c>); with no
    /// retention, every idle one, also those whose idle period a clock that went back makes start after now.
    /// </summary>
    private void ExpireContextsLocked(double at, double retention)
    {
        while (idleOrder.First is { } oldest)
        {
            if (retention > 0 && at - oldest.Value < retention) return;
            DropContextLocked(oldest.Key);
        }
    }

    /// <summary>
    /// Starts the idle period of a kept context in the expiry order (upstream <c>#markIdle</c>). An idle period older than the newest one
    /// kept, which a clock that went back starts, sorts the order again (stably, as upstream's sort), and the newest is taken from what is
    /// kept then.
    /// </summary>
    private void MarkIdleLocked(ConversationId conversationId, double idleSince)
    {
        idleOrder.Remove(conversationId);
        idleOrder.Set(conversationId, idleSince);
        if (idleOrder.Count == 1 || idleSince >= idleNewest)
        {
            idleNewest = idleSince;
            return;
        }
        var sorted = idleOrder.OrderBy(entry => entry.Value).ToList();
        idleOrder.Clear();
        foreach (var (id, since) in sorted) idleOrder.Set(id, since);
        idleNewest = sorted[^1].Value;
    }

    /// <summary>Forgets a kept context, idle or not, and its place in the expiry order (upstream <c>#dropContext</c>).</summary>
    private void DropContextLocked(ConversationId conversationId)
    {
        contexts.Remove(conversationId);
        idleOrder.Remove(conversationId);
    }

    /// <summary>Records when the earliest idle context expires, the oldest in <see cref="idleOrder"/>, for <see cref="ApplyExpiry"/>, which
    /// the caller runs once out of the lock.</summary>
    private void ComputeExpiryLocked(double retention) => expiryDue = idleOrder.First is { } oldest ? oldest.Value + retention : null;

    /// <summary>The expiry timer's work (upstream's timer callback): drops the expired contexts, then arms the timer for the next
    /// expiry.</summary>
    private void ExpireDue()
    {
        var at = now();
        var retention = ContextRetentionMs();
        lock (gate)
        {
            ExpireContextsLocked(at, retention);
            ComputeExpiryLocked(retention);
        }
        ApplyExpiry();
    }

    /// <summary>
    /// Drops the expired contexts when the earliest idle context expires (<see cref="ExpireDue"/>), on a background timer, which never keeps
    /// the process alive. Where the host declines it, none is kept: task changes and reconcile passes alone check expiry. Never called under
    /// the lock.
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
                ExpireDue();
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
        // The pass only enqueues its commit, so it starts where it is asked for (upstream: its microtask takes the flag and enqueues the
        // commit at once). A publication that asks for it starts it right after its host observers, on the publishing thread: a commit a host
        // observer makes there lands first, and the pass is on the line before the publication returns, so before the committer's
        // continuation, the waits it resolves and any later commit. Elsewhere it starts at once, under the caller's lock. Started on the
        // thread pool instead, a busy pool could bring it to the line while a later commit was being stored, and a request made meanwhile
        // (the flag still set) went with it: a `completing` publication's included. It then waited at that commit's gate ahead of the commits
        // its host observers make, and stored the parent's terminal record first (the busy-pool case of harness.tasks'
        // publication-at-adoption block).
        session.AfterPublication(() => _ = ReconcileAsync());
    }

    /// <summary>
    /// One commit that applies what committed records imply: abort marks below live owners with cancellation intent (spec §5.4),
    /// <c>failFast</c> marks (spec §5.5), withdrawn queued inputs below cancelled owners, and the final terminal record of every
    /// <c>completing</c> task whose ordinary owned work is gone. The durable records are the intent, so this also repairs whatever a crash
    /// left unapplied. Resolves idle waiters that the loaded edges decide. Started in a publication or under the scheduler's lock (see
    /// <see cref="ScheduleReconcileLocked"/>), it only enqueues its commit there: its job never starts inline, and the rest of the pass runs
    /// where the commit's outcome is delivered, a commit refused at once after a yield.
    /// </summary>
    private async Task ReconcileAsync()
    {
        // Started where it is asked for, possibly in an ordered flow (an abort invocation's): its commit is not part of it. Set in this async
        // method, so the caller's flow keeps its own.
        if (OrderedLineDelivery.Current is not null) OrderedLineDelivery.Current = null;
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
            var commit = session.CommitInternalAsync(async tx =>
            {
                bool cascade;
                lock (gate)
                {
                    if (closing) return true;
                    // Taken on the line, not when the pass starts: upstream's pass enqueues its commit in the microtask that takes the
                    // flag, so a commit that sets it later always has its own pass queued right behind this one. Here other jobs can get
                    // between the two, while this one's walk already marks below an intent that commit published, and the owner can end
                    // before that pass withdraws the queued inputs below it.
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
            }, context, inline: false);
            // Never goes on inside the publishing job or under the caller's lock (as ReservePassAsync): a commit refused at once (the Session
            // closed or failed) has completed here, and one delivered later resumes the pass on the thread pool, as no ordered flow takes it.
            if (commit.IsCompleted) await Task.Yield();
            await commit.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // No extension code runs in this commit: a throw is a storage failure, a host callback, or a bug. None is fixed by running the
            // pass again, so it fails the Session, which reports it.
            FailSession(error);
        }
        finally
        {
            lock (gate) if (work) reconcilingWork--;
        }
        try
        {
            ScheduleWake();
            // Its commit's task changes were settled at their publication; a waiter that asked for this pass checks again, and what its
            // chain loads resolved can make any scope idle.
            var at = now();
            var retention = ContextRetentionMs();
            var resumes = new List<Action>();
            lock (gate)
            {
                if (unknownsChanged) SettleIdleLocked(null, at, retention, resumes);
                else
                {
                    ResolveIdleWaitersLocked(null, resumes);
                    // A pass checks expiry too, for hosts that decline the expiry timer.
                    ExpireContextsLocked(at, retention);
                    ComputeExpiryLocked(retention);
                }
            }
            ResumeAfterPublication(resumes);
            ApplyExpiry();
        }
        finally
        {
            // Counted down once the pass's idle check ran, so QuiesceAsync waits for it too.
            lock (gate) reconciling--;
        }
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
        var installed = false;
        Up? at = start;
        try
        {
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
                        if (terminal)
                        {
                            Pin(Node.Of(id));
                            installed = true;
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
                        // Through the line's Storage: a conversation whose creation is not published yet waits for it, so a missing record is
                        // never mistaken for an ownerless one.
                        var record = await session.LineStorage.GetConversationAsync(current.Conversation, context).ConfigureAwait(false);
                        owner = record?.Owner?.TaskId;
                        lock (gate)
                        {
                            SetEdgeLocked(current.Conversation, owner);
                            // A loaded node can reveal a cancelling owner: the next reconcile derives marks again.
                            ScheduleReconcileLocked();
                        }
                        Pin(Node.Of(current.Conversation));
                        installed = true;
                    }
                    at = owner is { } task ? Up.Of(task) : null;
                }
            }
        }
        finally
        {
            // IdleLocked counts a live task whose chain is not loaded as inside every scope it may be in: what this load resolved, also when
            // it stopped early, can make such a scope idle without a task change below it.
            lock (gate)
            {
                if (passed.Exists(live.ContainsKey) || (installed && LiveUnloadedLocked()))
                {
                    unknownsChanged = true;
                    // A waiter such a task held is checked again at the pass's end, also when this load was not a reconcile's.
                    ScheduleReconcileLocked();
                }
            }
        }
        lock (gate) foreach (var id in passed) unloaded.Remove(id);
    }

    /// <summary>Whether a live task's chain is not loaded yet: one <see cref="IdleLocked"/> counts as inside every scope it may be in
    /// (upstream <c>#liveUnloaded</c>).</summary>
    private bool LiveUnloadedLocked()
    {
        foreach (var id in unloaded) if (live.ContainsKey(id)) return true;
        return false;
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
        Post(static scheduler =>
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
        });
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
            gateWakes.Remove(id);
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
        else
        {
            sleeping.Remove(id);
            gateWakes.Remove(id);
        }
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
        // A wake scheduled before the Session failed is still the host's: upstream's microtask runs before the failure, which comes from
        // storage, so the host hears the work underway then (open's recovery pass keeps its alarm). The job runs on the thread pool and can
        // find the Session closed first, so the wake is taken here, before closing and outside the lock (the clock and the registry
        // snapshot are host code), and the job delivers it.
        Wake? owed = null;
        if (onWake is not null && session.Failure is not null)
        {
            bool pending;
            lock (gate) pending = wakeDirty && !closing;
            if (pending) owed = WakeAt(now(), out _);
        }
        lock (gate)
        {
            if (!closing && countsActive) foreach (var record in live.Values) activeDeltas.Add((-1, record.Kind));
            resumed.Clear();
            if (!closing) failedWake = owed;
            closing = true;
            sealedInvocations.AddRange(invocations.Values);
            contexts.Clear();
            idleOrder.Clear();
            expiryDue = null;
            var error = HarnessUtil.ClosedError(session);
            taskWaiters.RejectAll(error);
            idleWaiters.RejectAll(error);
        }
        CountActive();
        // Outside the lock, as its listeners run synchronously: the cleanups an abort mark must not cancel end with the Harness.
        try
        {
            closed.Cancel(HarnessUtil.ClosedError(session));
        }
        catch (Exception error)
        {
            report(error, ReportInfo.ListenerError);
        }
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

}
