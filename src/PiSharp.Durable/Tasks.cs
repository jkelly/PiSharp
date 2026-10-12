// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/tasks.ts, plus the TaskDefinition, Task, RunningTask,
// NextTaskState and PhaseHandler types of packages/durable/src/types.ts (the TaskRuntime and HookRunner interfaces are in TypesTasks.cs).
//
// Upstream types a definition by its input, checkpoint, result and hook map (TaskDefinition<I, S, R, H>); here a definition is erased:
// input, checkpoints and results are JsonValue, and every checkpoint is a JSON object whose "phase" names its handler. The phase map is
// keyed by that name. Phases and Abort are optional so a definition that only creates records (the Session tests and samples) still
// compiles; a task whose phase or abort handler is missing faults when the scheduler reaches it.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>Runs one checkpoint phase. It must commit a changed checkpoint or a terminal outcome through <see cref="ITaskRuntime.CommitAsync(Func{ITx, RunningTask, Task{TaskState?}}, DurableContext)"/>;
/// returning without durable progress faults the task (upstream <c>PhaseHandler</c>).</summary>
public delegate Task TaskPhaseHandler(RunningTask task, ITaskRuntime runtime, DurableContext context);

/// <summary>Converts a record stored by an older supported version; runs at reservation (upstream <c>migrate</c>).</summary>
public delegate TaskMigrationResult TaskMigration(JsonValue input, JsonValue checkpoint, int fromVersion);

/// <summary>The migrated input and checkpoint of a task.</summary>
/// <param name="Input">The input for the current version.</param>
/// <param name="Checkpoint">The checkpoint for the current version.</param>
public sealed record TaskMigrationResult(JsonValue Input, JsonValue Checkpoint);

/// <summary>An executable durable state machine definition, registered in the registry by <see cref="Name"/> (upstream
/// <c>TaskDefinition</c>).</summary>
public sealed class TaskDefinition
{
    /// <summary>Registered task kind persisted in <see cref="TaskRecord.Kind"/>.</summary>
    public required string Name { get; init; }
    /// <summary>Definition version persisted with live input and checkpoints.</summary>
    public required int Version { get; init; }
    /// <summary>First durable checkpoint for a newly created task: a JSON object whose <c>phase</c> names a handler in
    /// <see cref="Phases"/>.</summary>
    public required Func<JsonValue, JsonValue> Initial { get; init; }
    /// <summary>Exhaustive phase map; each handler receives the task at its phase.</summary>
    public IReadOnlyDictionary<string, TaskPhaseHandler> Phases { get; init; } = ImmutableDictionary<string, TaskPhaseHandler>.Empty;
    /// <summary>Runs in a fresh invocation after an abort mark and must commit a terminal outcome.</summary>
    public TaskPhaseHandler? Abort { get; init; }
    /// <summary>Converts a record stored by any older supported version; runs at reservation.</summary>
    public TaskMigration? Migrate { get; init; }
    /// <summary>The hook map type this task dispatches through <see cref="ITaskRuntime.Hooks"/> (upstream <c>hooks</c>, a type
    /// carrier); extensions register handler objects of this type with <c>Define.Hook</c>.</summary>
    public object? Hooks { get; init; }
}

/// <summary>A typed executable task definition (upstream <c>Task</c>, the value <c>defineTask()</c> returns).</summary>
public sealed class DurableTask
{
    internal DurableTask(TaskDefinition definition) { Definition = definition; }
    /// <summary>The definition.</summary>
    public TaskDefinition Definition { get; }
    /// <inheritdoc/>
    public override string ToString() => Definition.Name;
}

/// <summary>Task definitions.</summary>
public static class Tasks
{
    /// <summary>Defines an executable task. Register it in the registry so a Harness can run tasks of its kind.</summary>
    public static DurableTask DefineTask(TaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Name);
        ArgumentNullException.ThrowIfNull(definition.Initial);
        ArgumentNullException.ThrowIfNull(definition.Phases);
        return new(definition);
    }
}

/// <summary>A live task record reserved by one invocation: its state is <see cref="RunningTaskState"/> (upstream
/// <c>RunningTask</c>).</summary>
public sealed class RunningTask
{
    internal RunningTask(TaskRecord record)
    {
        Record = record;
        State = record.State as RunningTaskState ?? throw new ArgumentException($"Task {record.Id} is {WireName.Of(record.State.Status)}");
    }

    /// <summary>The complete record.</summary>
    public TaskRecord Record { get; }
    /// <summary>The running state.</summary>
    public RunningTaskState State { get; }
    /// <summary>The task.</summary>
    public TaskId Id => Record.Id;
    /// <summary>The task's conversation.</summary>
    public ConversationId ConversationId => Record.ConversationId;
    /// <summary>The registered kind.</summary>
    public string Kind => Record.Kind;
    /// <summary>The definition version of the input and checkpoint.</summary>
    public int Version => Record.Version;
    /// <summary>The original input.</summary>
    public JsonValue Input => Record.Input;
    /// <summary>The owning task of a child task.</summary>
    public TaskId? Owner => Record.Owner;
    /// <summary>Whether this conversation-owned task is background work.</summary>
    public bool Background => Record.Background;
    /// <summary>Whether the task carries a durable abort mark.</summary>
    public bool AbortRequested => Record.AbortRequested;
    /// <summary>First-writer-wins values retained while the task can run.</summary>
    public JsonObjectValue? Memos => Record.Memos;
    /// <summary>The complete durable state execution resumes from.</summary>
    public JsonValue Checkpoint => State.Checkpoint;
    /// <summary>The checkpoint's <c>phase</c>; null when the checkpoint names none.</summary>
    public string? Phase => Checkpoint is JsonObjectValue checkpoint && checkpoint.TryGetValue("phase", out var phase) &&
        phase is JsonStringValue name ? name.Value : null;
    /// <inheritdoc/>
    public override string ToString() => Record.ToString();
}

/// <summary>
/// Next states a task commits for itself (upstream <c>NextTaskState</c>): a replacement checkpoint, a wait, or its outcome. A terminal
/// state is stored as <c>completing</c> while ordinary owned work below the task is live (spec §5.5).
/// </summary>
public static class NextTaskState
{
    /// <summary><c>{status: "running", checkpoint}</c>.</summary>
    public static RunningTaskState Running(JsonValue checkpoint) => new(checkpoint);
    /// <summary><c>{status: "waiting", checkpoint, on, policy}</c>: park until every task in <paramref name="on"/> is terminal.</summary>
    public static WaitingTaskState Waiting(JsonValue checkpoint, IEnumerable<TaskId> on, JoinPolicy policy) => new(checkpoint, [.. on], policy);
    /// <summary><c>{status: "terminal", outcome}</c>.</summary>
    public static TerminalTaskState Terminal(TaskOutcome outcome) => new(outcome);
    /// <summary><c>{status: "terminal", outcome: {status: "completed", result}}</c>.</summary>
    public static TerminalTaskState Completed(JsonValue? result) => new(new CompletedTaskOutcome(result ?? JsonValue.Null));
    /// <summary><c>{status: "terminal", outcome: {status: "aborted", reason?}}</c>.</summary>
    public static TerminalTaskState Aborted(string? reason = null) => new(new AbortedTaskOutcome(reason));
    /// <summary><c>{status: "terminal", outcome: {status: "failed", error}}</c>.</summary>
    public static TerminalTaskState Failed(string message, JsonValue? detail = null) => new(new FailedTaskOutcome(new TaskOutcomeError(message, detail)));

    /// <summary>Whether <paramref name="state"/> is a state a task may commit for itself: running, waiting or terminal.</summary>
    internal static void Check(TaskState state)
    {
        if (state.Status is not (DurableTaskStatus.Running or DurableTaskStatus.Waiting or DurableTaskStatus.Terminal))
            throw new ArgumentException($"A task commits a running, waiting or terminal state, not {WireName.Of(state.Status)}");
    }
}
