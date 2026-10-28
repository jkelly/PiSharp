// Decision 0012: agent steps. Agent runs a team agent (by definition name, resolved through WorkflowCatalog.Agents) on an input built from
// the Data, in a child conversation of the run configured with that agent's whole AgentChange: its model, instructions and tools, nothing
// inherited from the run. Like a prompt, a rerun within one attempt returns the recorded answer instead of asking again.
using PiSharp.CustomAgents.Workflows.Engine;

namespace PiSharp.CustomAgents.Workflows;

/// <summary>Options for one agent run from a workflow.</summary>
public sealed record AgentRunOptions
{
    /// <summary>How long the agent may take; on expiry its run is aborted and the attempt fails with a <see cref="TimeoutException"/>.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Distinguishes runs within one action visit; default the run's order in the attempt (<c>agent</c>, <c>agent2</c>, …).</summary>
    public string? Key { get; init; }
}

public sealed partial class ActionContext
{
    private int agents;

    /// <summary>Runs the team agent <paramref name="agent"/> on <paramref name="input"/> and returns its answer. Needs an agent provider
    /// (an agent team sets <see cref="Hosting.WorkflowCatalog.Agents"/>).</summary>
    public Task<ModelAnswer> RunAgentAsync(string agent, string input, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentNullException.ThrowIfNull(input);
        options ??= new AgentRunOptions();
        var provider = Scope.Catalog.Agents ?? throw new InvalidOperationException(
            $"Workflow '{Workflow}' runs agent '{agent}', but its catalog has no agent provider; add the workflow to an agent team.");
        var change = provider.Resolve(agent) ?? throw new ModelCallException($"No agent '{agent}' is defined.", "unknown_agent");
        if (options.Key is null) agents++;
        var key = options.Key ?? (agents == 1 ? "agent" : "agent" + agents);
        return ModelCalls.CallAsync(this, key, change, input, options.Timeout, null, cancellationToken);
    }
}

public sealed partial class WorkflowBuilder<TData>
{
    /// <summary>Adds an action that runs the team agent <paramref name="agent"/> on <paramref name="input"/>(Data) and applies its answer.</summary>
    public WorkflowBuilder<TData> Agent(string name, string agent, Func<TData, string> input, Action<TData, ModelAnswer> apply,
        AgentRunOptions? options = null, ActionOptions? action = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(apply);
        var run = (options ?? new AgentRunOptions()) with { Key = options?.Key ?? "agent" };
        return Add(new ActionStep<TData>(name, "agent:" + agent, new DelegateAction<TData>(name, async (data, context, token) =>
        {
            apply(data, await context.RunAgentAsync(agent, input(data), run, token).ConfigureAwait(false));
            return ActionResult.Next();
        }), action ?? new ActionOptions()));
    }
}

public sealed partial class ActionContext
{
    private Teams.IAgentMessenger? messages;

    /// <summary>Messaging as this run (address <c>workflow.&lt;task id&gt;</c>): message team agents, ask them questions, read replies. Message
    /// IDs are stable within one action visit, so a rerun of the action does not send twice. Needs an agent team.</summary>
    public Teams.IAgentMessenger Messages => messages ??= Teams.AgentMessaging.ForTask(Runtime, Scope.Catalog.Team ?? throw new InvalidOperationException(
            $"Workflow '{Workflow}' sends messages, but its catalog belongs to no agent team; add the workflow to an agent team."),
        Teams.AgentMessaging.WorkflowAddress(TaskId), $"w{TaskId.Value}.{Step}");
}
