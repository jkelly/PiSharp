using System.Collections;
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

internal sealed partial class DurableTaskScheduler
{
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
        Post(static scheduler => scheduler.RunWake());
    }

    private void RunWake()
    {
        while (true)
        {
            bool timer;
            Wake? owed = null;
            lock (gate)
            {
                if (!wakeDirty || closing)
                {
                    // Closed: only the wake owed at a failure (Seal) is delivered.
                    if (closing && wakeDirty) owed = failedWake;
                    failedWake = null;
                    wakeDirty = false;
                    if (owed is null)
                    {
                        wakeRunning = false;
                        return;
                    }
                }
                wakeDirty = false;
                timer = enabled;
            }
            if (owed is { } final)
            {
                Deliver(final);
                lock (gate) wakeRunning = false;
                return;
            }
            try
            {
                var wake = WakeAt(now(), out var dueSincePass);
                // A sleep's commit starts a pass and schedules this job, and each reads the clock on its own thread (upstream's pass and
                // wake run in one turn): a pass that read it before the deadline left the sleep asleep, and this wake keeps no timer for a
                // due sleep, so nothing would run it. One more pass does.
                if (dueSincePass && timer) RequestPass(onLine: false);
                if (onWake is null)
                {
                    ArmTimer(timer ? wake.At : null);
                    continue;
                }
                Deliver(wake);
            }
            catch (Exception error)
            {
                report(error, ReportInfo.ListenerError);
            }
        }
    }

    /// <summary>Tells OnWake of <paramref name="wake"/> unless it was the last delivered, on the wake job.</summary>
    private void Deliver(Wake wake)
    {
        if (lastWake == wake) return;
        try
        {
            onWake!(wake);
            // Only a delivered wake counts: after a throw, the next change or kick offers it again.
            lastWake = wake;
        }
        catch (Exception error)
        {
            report(error, OnWakeError);
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

    /// <summary>Makes every sleeping task whose time has come by <paramref name="at"/>, or that a signal or a gate wake can run, a
    /// reservation candidate.</summary>
    private void WakeDueLocked(double at)
    {
        foreach (var id in sleeping)
            if (live.TryGetValue(id, out var record) && !AsleepLocked(record, at)) runnable.Add(id);
    }

    /// <summary>A sleeping record that cannot wake yet (<see cref="Asleep"/>), unless a gate wake runs it before its time (upstream
    /// <c>#asleep</c>).</summary>
    private bool AsleepLocked(TaskRecord record, double at) => Asleep(record, at) && !gateWakes.Contains(record.Id);

    /// <summary>
    /// Wakes a call parked at a gate, so its gates are asked again; a sleep whose deadline passed takes the deadline path when reserved
    /// (<see cref="WokeLocked"/>). Other records are left alone (upstream <c>#wakeGate</c>).
    /// </summary>
    private void WakeGateLocked(TaskRecord record)
    {
        if (!parkedAtGate(record)) return;
        gateWakes.Add(record.Id);
        runnable.Add(record.Id);
    }

    /// <summary>A sleeping record that cannot wake yet: no abort mark, no listed key with an unused value, <c>until</c> not reached
    /// (upstream <c>asleep</c>).</summary>
    private static bool Asleep(TaskRecord record, double at) =>
        record.State is SleepingTaskState asleep && !record.AbortRequested && !Signals.SleepDue(asleep, record.Memos, at);

    /// <summary>A live task that holds no invocation while it waits: sleeping, waiting, or holding its outcome (upstream <c>parked</c>).</summary>
    private static bool Parked(TaskRecord record) =>
        record.State.Status is DurableTaskStatus.Sleeping or DurableTaskStatus.Waiting or DurableTaskStatus.Completing;

    /// <summary>
    /// The record a reservation writes (upstream <c>reserved</c>): <c>running</c> at the checkpoint, with the wake that ends its wait or
    /// sleep and the signal memos it marks; a task written back as <c>pending</c> keeps the wake it woke with.
    /// </summary>
    private static TaskRecord Reserved(TaskRecord record, Woke? woke)
    {
        var state = (LiveTaskState)record.State;
        if (woke is { } ended)
        {
            // As upstream's `{ ...record.memos, ...woke.memos }`: a woken record has memos, empty when it had none and none is marked.
            var memos = record.Memos ?? JsonObjectValue.Empty;
            foreach (var (name, memo) in ended.Memos) memos = memos.SetItem(name, memo);
            return WithState(record with { Memos = memos }, new RunningTaskState(state.Checkpoint, ended.Wake));
        }
        var kept = state switch { PendingTaskState pending => pending.Wake, RunningTaskState running => running.Wake, _ => null };
        return WithState(record, new RunningTaskState(state.Checkpoint, kept));
    }

    /// <summary>The record written back as <c>pending</c> (open, close, handover): its checkpoint, and the wake it woke with, if any
    /// (upstream <c>pendingAgain</c>).</summary>
    private static TaskRecord PendingAgain(TaskRecord record, RunningTaskState running) =>
        WithState(record, new PendingTaskState(running.Checkpoint, running.Wake));

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
        RequestPass(onLine);
    }

    /// <summary>The reservation pass of <see cref="Kick(bool)"/>, without the wake it schedules (for the wake job itself).</summary>
    private void RequestPass(bool onLine)
    {
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
        // Never commit synchronously from a commit or registry listener: on the line, the pass only enqueues its commit. At a publication
        // either starts after its host observers (upstream: the pass's microtask runs behind their dispatch), so a commit a host observer
        // makes there lands before the pass's, and still before the committer's continuation.
        if (onLine) session.AfterPublication(() => _ = ReservePassAsync());
        else session.AfterPublication(() =>
            Post(static scheduler => _ = scheduler.DrainAsync()));
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
                    var unmarked = await RegionAsync(() => pending.Select(LiveRecordLocked).Where(record =>
                    {
                        if (record is not { AbortRequested: false }) return false;
                        // An owner that was not cut off while running keeps them (spec suspendable §2.5).
                        var owner = record.Owner is { } ownerId ? LiveRecordLocked(ownerId) : null;
                        return owner is null || !keepsAbandoned(owner);
                    }).ToList()).ConfigureAwait(false);
                    foreach (var record in unmarked) tx.SetTask(WithAbortMark(record!, restart: true));
                    marked = pending;
                    return true;
                }
                await LoadScopesAsync(false).ConfigureAwait(false);
                // Every pass picks up sleeps whose time has come: a pass started by any change may be the first to run after a deadline,
                // and the wake it schedules no longer keeps a timer for a due sleep.
                var at = now();
                lock (gate)
                {
                    WakeDueLocked(at);
                    lastPassAt = Math.Max(lastPassAt, at);
                }
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
                long publication = 0;
                foreach (var (record, woke) in plan.Ready)
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
                    if (snapshot is null)
                    {
                        lock (gate) publication = publications;
                        snapshot = registry.Snapshot();
                    }
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
                        tx.SetTask(Reserved(resolved, woke));
                    // Registered on the line, so marks and later reservations see it and close joins it; End makes it a candidate again.
                    lock (gate) runnable.Remove(id);
                    var invocation = CreateInvocation(record, mode, InstallerOf(snapshot, resolution.Task));
                    reservations.Add(new Reservation(invocation, resolution.Task!, snapshot, publication));
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
        var ready = new List<(TaskRecord Record, Woke? Woke)>();
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
                AsleepLocked(record, at))
            {
                dropped.Add(id);
                continue;
            }
            if (!record.AbortRequested && !record.Background && BelowCancelledLocked(ParentOf(record)))
            {
                cascade = true;
                continue;
            }
            ready.Add((record, WokeLocked(record, at)));
        }
        return new ReservationPlan(dropped, cascade, ready);
    }

    /// <summary>
    /// How a run-mode reservation ends a record's wait or sleep (spec suspendable §2.4; upstream <c>#woke</c>): a wait's wake names the
    /// terminal tasks of its <c>on</c> (all of them unless <c>any</c>), read as task records; a sleep's, the key that woke it, consumed, or
    /// its deadline, with the keys that hold nothing expired. None for an abort, or for a record that neither waits nor sleeps.
    /// </summary>
    private Woke? WokeLocked(TaskRecord record, double at)
    {
        if (record.AbortRequested) return null;
        if (record.State is WaitingTaskState waiting)
        {
            var ended = waiting.Policy == JoinPolicy.Any ? [.. waiting.On.Where(id => LiveRecordLocked(id) is null)] : waiting.On;
            return new Woke(WakeReason.Tasks(ended), []);
        }
        // Not asleep: due, or woken at its gates, which needs no wake unless its deadline passed.
        return record.State is SleepingTaskState asleep && Signals.SleepWake(asleep, record.Memos, at) is { } woke
            ? new Woke(woke.Wake, woke.Memos)
            : null;
    }

    /// <summary>
    /// Live tasks a task waits for before its next invocation: its live ordinary owned work when abort-marked, since abort runs bottom-up,
    /// otherwise the live part of the <c>on</c> of a wait; none for an <c>any</c> wait one of whose tasks ended.
    /// </summary>
    private List<TaskId> WaitingOnLocked(TaskRecord record, bool first = false)
    {
        if (record.AbortRequested) return OwnedLiveLocked(record.Id, null, first, null);
        if (record.State is not WaitingTaskState waiting) return [];
        if (waiting.Policy == JoinPolicy.Any && !waiting.On.All(live.ContainsKey)) return [];
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
                SleepingTaskState asleep => new SleepingTaskState(migrated.Checkpoint, asleep.Until, asleep.Signals),
                RunningTaskState running => new RunningTaskState(migrated.Checkpoint, running.Wake),
                _ => new PendingTaskState(migrated.Checkpoint, (state as PendingTaskState)?.Wake),
            };
            return new Resolution(fit.Task, record with { Version = definition.Version, Input = migrated.Input, State = next }, null);
        }
        catch (Exception error)
        {
            lock (gate) failedMigrations[record.Id] = (fit.Task, error);
            // Blocked now without a commit: the graph and the host's wake must hear it. Queued, as upstream's microtask: this runs on the line.
            Post(static scheduler => scheduler.NotifyBlocked());
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
        if (AsleepLocked(record, at)) return new TaskInspectionState(TaskInspectionKind.Sleeping) { Until = ((SleepingTaskState)record.State).Until };
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
        var state = new RunState(reservation.Task, reservation.Snapshot) { Publication = reservation.Publication };
        var phase = new Phase(() => state.Snapshot, () => state.Task);
        var runtime = new Runtime(this, invocation, phase);
        bool Progress(Transaction tx, RunningTask task) => ProgressOf(tx, invocation, task, state);
        var current = await StepAsync(invocation, task => RulesLocked(task, null), Progress).ConfigureAwait(false);
        while (true)
        {
            // Close may seal between the decision and dispatch.
            bool closingNow;
            lock (gate) closingNow = closing;
            if (current is null || closingNow) break;
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
        // A gate's sleep committed under a snapshot the registry has since replaced: the phase could not take a fresh one, so it is woken
        // as a publication would have woken it.
        bool stale;
        lock (gate)
        {
            stale = live.TryGetValue(invocation.TaskId, out var record) && publications != state.Publication;
            if (stale) WakeGateLocked(record!);
        }
        if (stale) Kick();
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
        lock (gate) state.Publication = publications;
        state.Snapshot = registry.Snapshot();
        var next = state.Snapshot.GetTask(current.Kind);
        if (ReferenceEquals(next, state.Task)) return true;
        if (next is not null && CanReserve(next, current.Record))
        {
            tx.SetTask(PendingAgain(current.Record, current.State));
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
        if (next is SleepingTaskState asleep) ValidateSleep(invocation, current, asleep);
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
        // committed again, for example as intent before an effect, is not, and keeps the wake.
        var was = current.State.Wake is null ? current.State : new RunningTaskState(current.State.Checkpoint);
        var same = JsonValue.DeepEquals(was.ToJson(), next.ToJson());
        tx.SetTask(WithState(same ? current.Record : Progressed(current.Record), same ? current.State : next));
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
    private bool IdleLocked(ConversationId? conversationId) => QuietLocked(conversationId, null);

    /// <summary>Whether a kept context's conversation is inactive (spec suspendable §8; upstream <c>#inactive</c>): idle, or with all its
    /// live work parked (<see cref="Parked"/>), which holds no invocation however long it waits.</summary>
    private bool InactiveLocked(ConversationId conversationId) => QuietLocked(conversationId, static record => !Parked(record));

    /// <summary>No live non-background task in the scope that <paramref name="counts"/> (null: every one); one whose owner edges are not
    /// loaded yet counts as inside (upstream <c>#quiet</c>).</summary>
    private bool QuietLocked(ConversationId? conversationId, Func<TaskRecord, bool>? counts)
    {
        var scope = conversationId is { } id ? Scope.Of(id) : Scope.Roots;
        // In a decision region, global idle reads the roots it starts from.
        if (conversationId is null) Record(LineKeys.Roots);
        // Tasks whose chain is not loaded are not listed below their scope yet; one counts as inside unless its walk up decides otherwise.
        foreach (var taskId in unloaded)
            if (live.TryGetValue(taskId, out var record) && !record.Background && (counts is null || counts(record)) &&
                InScopeLocked(ParentOf(record), scope) != false) return false;
        IEnumerable<ConversationId> starts = conversationId is { } only ? [only] : roots;
        foreach (var start in starts)
            foreach (var (_, node, record) in WalkDownLocked(Node.Of(start), null, false, null))
                if (record is not null && !node.Background && (counts is null || counts(record))) return false;
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

}
