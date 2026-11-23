using System.Collections;
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

internal sealed partial class DurableTaskScheduler
{
    // ─── Invocation runtime ──────────────────────────────────────────────────

    /// <summary>The task runtime of one invocation (upstream <c>#runtime</c>).</summary>
    private sealed class Runtime(DurableTaskScheduler scheduler, Invocation invocation, Phase phase) : ITaskRuntime, IHookRunner, ISchedulerRuntime
    {
        public CancellationSignal CloseSignal => scheduler.closed;

        public void UseProviderSession(string sessionId)
        {
            lock (scheduler.providerSessions) scheduler.providerSessions.Add(sessionId);
        }

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

        public Task<SignalState?> ReadSignalAsync(SignalToken token, string key, DurableContext context)
        {
            ArgumentNullException.ThrowIfNull(token);
            ArgumentNullException.ThrowIfNull(key);
            return Read(() =>
            {
                JsonObjectValue? memos;
                lock (scheduler.gate) memos = scheduler.live.GetValueOrDefault(invocation.TaskId)?.Memos;
                // A migration is host code: outside the lock.
                return Task.FromResult(Signals.Read(memos, token, key));
            });
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
                    if (!scheduler.InactiveLocked(conversationId))
                    {
                        scheduler.contexts[conversationId] = new KeptContext(range, null);
                        scheduler.idleOrder.Remove(conversationId);
                    }
                    else if (retention > 0)
                    {
                        // An idle period that already started keeps its expiry and its place; a new one starts now.
                        var idleSince = kept?.IdleSince ?? stamp;
                        var expiresAt = kept?.IdleSince is not null && kept.ExpiresAt is { } fixedAt ? fixedAt : idleSince + retention;
                        scheduler.contexts[conversationId] = new KeptContext(range, idleSince) { ExpiresAt = expiresAt };
                        if (kept?.IdleSince is null) scheduler.MarkIdleLocked(conversationId, idleSince);
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

    /// <summary>A sleep's <c>until</c>, when set, is a time on the Harness clock; it waits for <c>until</c> or a non-empty <c>signals</c>,
    /// none of whose keys served a wait (<see cref="Signals.CheckSleep"/>); an abort handler cannot sleep (upstream
    /// <c>validateSleep</c>).</summary>
    private static void ValidateSleep(Invocation invocation, RunningTask current, SleepingTaskState next)
    {
        var task = current.Id;
        if (invocation.Mode == InvocationMode.Abort) throw new InvalidOperationException($"Abort handler of task {task} cannot sleep");
        if (next.Until is { } until && (!double.IsFinite(until) || until < 0))
            throw new ArgumentException($"Task {task} cannot sleep until {Internal.PiAi.Validation.Js.NumberToString(until)}: not a time in milliseconds");
        Signals.CheckSleep(next, current.Memos, task);
    }
}
