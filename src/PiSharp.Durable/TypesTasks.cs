// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/types.ts (TaskRuntime and HookRunner; split from Types.cs).
// Upstream's AbortSignal becomes a CancellationToken plus the handler context, which the same signal cancels; its memo and entry overloads
// become C# overloads; its synchronous-or-promise commit callback becomes four overloads, as ISession.CommitAsync has.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable;

/// <summary>Dispatches one hook of a task to every matching registered handler, in registry order of the phase snapshot (upstream
/// <c>HookRunner</c>).</summary>
public interface IHookRunner
{
    /// <summary>
    /// Calls <paramref name="invoke"/> with each handler that <paramref name="select"/> picks from the registered handler objects of type
    /// <typeparamref name="THooks"/> (upstream <c>each(name, invoke)</c>: the selector names the hook). An ordinary throw from
    /// <paramref name="invoke"/> is reported and the next handler runs; once the invocation is signalled, the error propagates. Composition
    /// happens inside <paramref name="invoke"/>.
    /// </summary>
    Task EachAsync<THooks, THandler>(Func<THooks, THandler?> select, Func<THandler, Task> invoke) where THooks : class where THandler : class;
}

/// <summary>
/// Operations of one task invocation (upstream <c>TaskRuntime</c>). Every operation rejects after the invocation ends; watches acquired
/// through it stop at invocation end. A runtime is the <see cref="IHookApi"/> its task passes to hooks, as upstream's structurally is; the task
/// and conversation IDs, <c>Models</c> and the memo reads and writes are the inherited <see cref="IHookApi"/> members.
/// </summary>
public interface ITaskRuntime : IDocumentObserver, IDocumentReader, IHookApi
{
    /// <summary>
    /// Cancelled when the run is signalled by <c>AbortTaskAsync</c>, the Harness closes, or the invocation ends (upstream <c>signal</c>).
    /// The handler's context carries the same signal. Work still using it after the invocation ended is cancelled; it could not write
    /// anything anyway.
    /// </summary>
    CancellationToken Signal { get; }
    /// <summary>Registry snapshot of the current phase; refreshed at every phase boundary.</summary>
    IRegistrySnapshot Registry { get; }
    /// <summary>The task's conversation's agent, resolved at most once per phase, at first use, and fixed for the phase.</summary>
    Task<Agent> AgentAsync(DurableContext context);
    /// <summary><c>HarnessOptions.Settings</c>, resolved at each access.</summary>
    Settings Settings { get; }
    /// <summary>Calls <c>HarnessOptions.Env</c> for the task's conversation; rejects with its error.</summary>
    Task<IExecutionEnv?> EnvAsync(DurableContext context);
    /// <summary>Handlers of this task's name from the extensions its conversation selects, in extension order.</summary>
    IHookRunner Hooks { get; }

    /// <summary>
    /// Commits on the Session line after rereading the task. Rejects when the task is terminal, the invocation ended, the Harness is
    /// closing, or, in a run invocation, the task carries an abort mark. A returned state (running, waiting or terminal; see
    /// <see cref="NextTaskState"/>) replaces the task's state in the same commit; returning null leaves it unchanged.
    /// <c>tx.CreateTaskAsync()</c> defaults to the task's conversation, and appended entries carry the task as <c>byTaskId</c>. Inside the
    /// callback read only through the transaction: <c>SnapshotAsync</c>, <c>DocumentStateAsync</c>, <c>WatchDocAsync</c>,
    /// <c>GetTaskAsync</c> and the other Session and runtime reads may queue on the line the callback holds, and then never complete.
    /// </summary>
    Task CommitAsync(Func<ITx, RunningTask, Task<TaskState?>> change, DurableContext context);
    /// <summary><see cref="CommitAsync(Func{ITx, RunningTask, Task{TaskState?}}, DurableContext)"/> leaving the state unchanged.</summary>
    Task CommitAsync(Func<ITx, RunningTask, Task> change, DurableContext context);
    /// <summary><see cref="CommitAsync(Func{ITx, RunningTask, Task{TaskState?}}, DurableContext)"/> with a synchronous callback.</summary>
    Task CommitAsync(Func<ITx, RunningTask, TaskState?> change, DurableContext context);
    /// <summary><see cref="CommitAsync(Func{ITx, RunningTask, Task{TaskState?}}, DurableContext)"/> with a synchronous callback leaving the
    /// state unchanged.</summary>
    Task CommitAsync(Action<ITx, RunningTask> change, DurableContext context);

    /// <summary>The committed task record.</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context);
    /// <summary>Resolves with the task's terminal receipt (upstream <c>SettledTask</c>); rejects when the invocation ends.</summary>
    Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context);
    /// <summary>
    /// Aborts a task this task owns, as <c>IHarness.AbortTaskAsync</c> does, and completes once it is terminal (upstream
    /// <c>abortOwned</c>). Rejects for a task another owns; completes at once for a terminal one.
    /// </summary>
    Task AbortOwnedAsync(TaskId id, DurableContext context);
    /// <summary>
    /// Live tasks this task owns directly, in ID order: also those an earlier invocation created, before a restart (upstream
    /// <c>ownedTasks</c>). Tasks in conversations it owns are not included. The committed records, which are immutable.
    /// </summary>
    Task<IReadOnlyList<TaskRecord>> OwnedTasksAsync(DurableContext context);
    /// <summary>Outcomes of terminal tasks, in order; rejects when one is missing or not terminal. Used after a wait.</summary>
    Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(IReadOnlyList<TaskId> ids, DurableContext context);
    /// <summary>
    /// Invocation-bound handle of an existing conversation, for example one this task owns; null when absent. Its operations and the
    /// submissions it returns reject after the invocation ends; admitted work stays durable.
    /// </summary>
    Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context);
    /// <summary>A committed entry visible from the task's conversation.</summary>
    Task<EntryRecord?> EntryAsync(EntryId id, DurableContext context);
    /// <summary>A committed entry visible from the task's conversation; null when absent, not visible, or of another kind.</summary>
    Task<EntryRecord?> EntryAsync(Entry token, EntryId id, DurableContext context);
    /// <summary>A committed typed entry visible from the task's conversation; null when absent, not visible, or of another kind.</summary>
    Task<TypedEntry<TData>?> EntryAsync<TData>(Entry<TData> token, EntryId id, DurableContext context) where TData : JsonValue;
    /// <summary>Committed raw active transcript and model context, optionally cut off at the visible entry <paramref name="at"/>.</summary>
    Task<ContextView> ContextAsync(ConversationId conversationId, DurableContext context, EntryId? at = null);
    /// <summary>The Harness clock, in milliseconds.</summary>
    double Now();
    /// <summary>Forwards a non-fatal failure to <c>HarnessOptions.OnReport</c>.</summary>
    void Report(Exception error);
    /// <summary>Resolves once the Harness clock reaches <paramref name="until"/>; rejects when the invocation or
    /// <paramref name="context"/> is cancelled.</summary>
    Task SleepAsync(double until, DurableContext context);
}
