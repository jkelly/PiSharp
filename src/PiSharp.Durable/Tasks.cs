// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/tasks.ts, plus the identity part of the TaskDefinition and Task
// types of types.ts. TODO(P7): the executable surface (phase handlers, abort, migrate, hooks) takes the TaskRuntime that the task runtime
// phase ports; until then a definition carries only what the Session needs to create a task record.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>A durable state machine definition, registered by <see cref="Name"/> (upstream <c>TaskDefinition</c>; the phases, abort handler,
/// migration and hooks join with the task runtime in P7).</summary>
public sealed class TaskDefinition
{
    /// <summary>Registered task kind persisted in <see cref="TaskRecord.Kind"/>.</summary>
    public required string Name { get; init; }
    /// <summary>Definition version persisted with live input and checkpoints.</summary>
    public required int Version { get; init; }
    /// <summary>First durable checkpoint for a newly created task.</summary>
    public required Func<JsonValue, JsonValue> Initial { get; init; }
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
        return new(definition);
    }
}
