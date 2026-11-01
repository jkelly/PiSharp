// Decision 0012: the compiled shape of a workflow step. An action step is one durable phase named after it; a router step is resolved
// inside the transition that reaches it and is never a phase; composite steps (Run, Parallel, ForEach, WaitForSignal) add their own
// phases (Engine/Composition.cs).
using System.Collections.Immutable;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Workflows.Engine;

/// <summary>One step of a built workflow.</summary>
internal abstract class Step<TData>(string name) where TData : class
{
    public string Name { get; } = name;
    /// <summary>What kind of step this is, for listings and the fingerprint (code, prompt, agent, router, run, parallel, …).</summary>
    public abstract string Kind { get; }
    /// <summary>Extra shape for the fingerprint: targets, branch names.</summary>
    public virtual string Shape => string.Join(",", StaticTargets);
    /// <summary>Targets known at build time, validated by <see cref="WorkflowBuilder{TData}.Build"/>.</summary>
    public virtual IEnumerable<string> StaticTargets => [];
    /// <summary>The step's next action when it ends with <see cref="ActionResult.Next"/>; null continues with the following step.</summary>
    public virtual string? NextOverride => null;
    /// <summary>Most visits per run; null is unlimited.</summary>
    public virtual int? MaxVisits => null;
    /// <summary>Whether reaching the step routes on (a router) instead of entering a phase.</summary>
    public virtual bool IsRouter => false;
    /// <summary>Further build checks.</summary>
    public virtual void Validate(string workflow) { }
    /// <summary>Adds the step's phase handlers.</summary>
    public abstract void AddPhases(IDictionary<string, TaskPhaseHandler> phases, WorkflowEngine<TData> engine);
    /// <summary>Extensions the step needs installed (a typed prompt's result tool).</summary>
    public virtual IEnumerable<Extension> Extensions => [];
    /// <summary>Workflows the step runs as children (Run), added to the catalog with this one.</summary>
    public virtual IEnumerable<WorkflowDefinition> SubWorkflows => [];
    /// <summary>Task definitions the step runs as children (Parallel branches).</summary>
    public virtual IEnumerable<DurableTask> ChildTasks(WorkflowEngine<TData> engine) => [];
}

/// <summary>An action: one phase that runs an <see cref="IAction{TData}"/>.</summary>
internal sealed class ActionStep<TData>(string name, string kind, IAction<TData> action, ActionOptions options) : Step<TData>(name) where TData : class
{
    public IAction<TData> Action { get; } = action;
    public ActionOptions Options { get; } = options;
    public override string Kind { get; } = kind;
    public override IEnumerable<string> StaticTargets => Options.Next is { } next ? [next] : [];
    public override string? NextOverride => Options.Next;
    public override int? MaxVisits => Options.MaxVisits;
    /// <summary>Extensions a prompt-like action installs.</summary>
    public ImmutableArray<Extension> Installs { get; init; } = [];
    public override IEnumerable<Extension> Extensions => Installs;

    public override void Validate(string workflow)
    {
        if (Options.MaxVisits is < 1) throw new WorkflowDefinitionException($"Action '{Name}' of workflow '{workflow}' has MaxVisits below 1.");
        if (Options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new WorkflowDefinitionException($"Action '{Name}' of workflow '{workflow}' has a timeout that is not positive.");
        if (Options.Retry is { MaxAttempts: < 1 }) throw new WorkflowDefinitionException($"Action '{Name}' of workflow '{workflow}' allows no attempt.");
    }

    public override void AddPhases(IDictionary<string, TaskPhaseHandler> phases, WorkflowEngine<TData> engine) =>
        phases[Name] = (task, runtime, context) => engine.RunActionAsync(this, task, runtime, context);
}

/// <summary>A router: picks the next step from the Data; resolved inside the transition that reaches it.</summary>
internal sealed class RouterStep<TData>(string name, Func<TData, string> select, ImmutableArray<string> targets) : Step<TData>(name) where TData : class
{
    public Func<TData, string> Select { get; } = select;
    public override string Kind => "router";
    public override IEnumerable<string> StaticTargets => targets;
    public override bool IsRouter => true;
    public override void AddPhases(IDictionary<string, TaskPhaseHandler> phases, WorkflowEngine<TData> engine) { }
}
