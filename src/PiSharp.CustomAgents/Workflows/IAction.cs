// Decision 0012: an action is one step of a workflow over the workflow's shared Data class.
namespace PiSharp.CustomAgents.Workflows;

/// <summary>
/// One step of a workflow. <see cref="ExecuteAsync"/> receives a fresh copy of the run's Data, may change it, and returns what happens
/// next; the changes and that decision are committed together, exactly once. The action itself runs at least once: after a crash it runs
/// again from the last committed Data, so side effects belong behind <see cref="ActionContext.OnceAsync{T}"/> or an idempotency key.
/// </summary>
/// <typeparam name="TData">The workflow's Data class: everything its actions need, serialized with System.Text.Json between steps.</typeparam>
public interface IAction<TData> where TData : class
{
    /// <summary>The action's name, unique in its workflow: <c>^[A-Za-z][A-Za-z0-9_.-]{0,63}$</c>.</summary>
    string Name { get; }

    /// <summary>Runs the action against <paramref name="data"/>.</summary>
    Task<ActionResult> ExecuteAsync(TData data, ActionContext context, CancellationToken cancellationToken);
}

/// <summary>An action from a delegate.</summary>
public sealed class DelegateAction<TData>(string name, Func<TData, ActionContext, CancellationToken, Task<ActionResult>> run) : IAction<TData>
    where TData : class
{
    private readonly Func<TData, ActionContext, CancellationToken, Task<ActionResult>> run = run ?? throw new ArgumentNullException(nameof(run));

    /// <inheritdoc/>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <inheritdoc/>
    public Task<ActionResult> ExecuteAsync(TData data, ActionContext context, CancellationToken cancellationToken) => run(data, context, cancellationToken);
}

/// <summary>A workflow definition error found by <see cref="WorkflowBuilder{TData}.Build"/>.</summary>
public sealed class WorkflowDefinitionException(string message) : Exception(message);

/// <summary>Data that cannot be stored: a number a double cannot hold exactly, a size over the limit, or a serializer failure.</summary>
public sealed class WorkflowDataException(string message, string? path = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The JSON path of the offending value, when known.</summary>
    public string? Path { get; } = path;
}

/// <summary>A workflow run that did not complete, thrown by <see cref="Hosting.WorkflowResult{TData}.EnsureCompleted"/>.</summary>
public sealed class WorkflowFailedException(string message, Hosting.WorkflowRunStatus status) : Exception(message)
{
    /// <summary>How the run ended.</summary>
    public Hosting.WorkflowRunStatus Status { get; } = status;
}
