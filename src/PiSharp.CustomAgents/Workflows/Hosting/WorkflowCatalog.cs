// Decision 0012: the workflows a host can run. Adding a workflow compiles it and installs its task definition (and any result tools its
// typed prompts need) into the durable registry. Register every workflow before IHarness.Resume, so runs a restart left pending find
// their definition.
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Workflows.Hosting;

/// <summary>Host services and defaults for workflow actions.</summary>
public sealed record WorkflowCatalogOptions
{
    /// <summary>Services for <see cref="ActionContext.GetService{T}"/>.</summary>
    public IServiceProvider? Services { get; init; }
    /// <summary>The model for prompts when neither the prompt nor the workflow names one.</summary>
    public ModelRef? DefaultModel { get; init; }
    /// <summary>The thinking level for prompts when neither the prompt nor the workflow names one.</summary>
    public string? DefaultThinking { get; init; }
    /// <summary>The clock for action timeouts; pass the Harness's TimeProvider when tests drive time. Default the system clock.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>Resolves the agents that workflow <c>Agent</c> steps and <see cref="ActionContext.RunAgentAsync"/> run (an agent team).</summary>
public interface IAgentProvider
{
    /// <summary>The agent configuration for <paramref name="agent"/>; null when no such agent is defined.</summary>
    AgentChange? Resolve(string agent);
}

/// <summary>The workflows a host can run, installed into a durable registry.</summary>
public sealed class WorkflowCatalog
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>A catalog installing into <paramref name="registry"/>.</summary>
    public WorkflowCatalog(IRegistry registry, WorkflowCatalogOptions? options = null)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Options = options ?? new WorkflowCatalogOptions();
    }

    /// <summary>The registry workflows are installed into.</summary>
    public IRegistry Registry { get; }
    /// <summary>The host options.</summary>
    public WorkflowCatalogOptions Options { get; }
    /// <summary>The agents workflow agent steps run; set by an agent team.</summary>
    public IAgentProvider? Agents { get; set; }
    /// <summary>The team that owns this catalog, for workflow messaging.</summary>
    internal Teams.AgentTeam? Team { get; set; }

    /// <summary>The added workflows, by name.</summary>
    public IReadOnlyList<WorkflowDefinition> Workflows
    {
        get { lock (gate) return [.. entries.Values.Select(entry => entry.Engine.Workflow).OrderBy(workflow => workflow.Name, StringComparer.Ordinal)]; }
    }

    /// <summary>Adds (or replaces) <paramref name="workflow"/> and the sub-workflows it runs.</summary>
    public WorkflowCatalog Add(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var engine = workflow.Compile(this);
        var extension = new Extension { Name = workflow.TaskKind, Tasks = [engine.Task, .. engine.ChildTasks] };
        lock (gate)
        {
            if (entries.TryGetValue(workflow.Name, out var previous))
                foreach (var stale in previous.Extensions) Registry.Uninstall(stale);
            entries[workflow.Name] = new Entry(engine, [extension, .. engine.Extensions]);
            Registry.Install(extension);
            foreach (var installed in engine.Extensions) Registry.Install(installed);
        }
        foreach (var child in engine.SubWorkflows) if (Find(child.Name)?.Fingerprint != child.Fingerprint) Add(child);
        return this;
    }

    /// <summary>Removes the workflow named <paramref name="name"/>; its live runs block as missing until it is added again.</summary>
    public bool Remove(string name)
    {
        lock (gate)
        {
            if (!entries.Remove(name, out var entry)) return false;
            foreach (var extension in entry.Extensions) Registry.Uninstall(extension);
            return true;
        }
    }

    /// <summary>The workflow named <paramref name="name"/>; null when not added.</summary>
    public WorkflowDefinition? Find(string name)
    {
        lock (gate) return entries.TryGetValue(name, out var entry) ? entry.Engine.Workflow : null;
    }

    internal IWorkflowEngine Engine(WorkflowDefinition workflow)
    {
        lock (gate)
        {
            if (entries.TryGetValue(workflow.Name, out var entry) && (ReferenceEquals(entry.Engine.Workflow, workflow) ||
                entry.Engine.Workflow.Fingerprint == workflow.Fingerprint))
                return entry.Engine;
        }
        throw new InvalidOperationException($"Workflow '{workflow.Name}' is not in the catalog; add it before running it.");
    }

    internal IWorkflowEngine? Engine(string name)
    {
        lock (gate) return entries.TryGetValue(name, out var entry) ? entry.Engine : null;
    }

    private sealed record Entry(IWorkflowEngine Engine, IReadOnlyList<Extension> Extensions);
}
