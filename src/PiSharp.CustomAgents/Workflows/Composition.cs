// Decision 0012: composing workflows: Run (a sub-workflow), Parallel (branches over copies of the Data), ForEach (a sub-workflow per item)
// and WaitForSignal (a value sent to the run from outside).
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Workflows;

/// <summary>One branch of a Parallel step, run on a copy of the Data: a workflow over the same Data type, or one action (compiled as the
/// workflow <c>&lt;parent&gt;.&lt;step&gt;.&lt;branch&gt;</c>).</summary>
public sealed class ParallelBranch<TData> where TData : class
{
    private ParallelBranch(string name, Workflow<TData>? workflow, IAction<TData>? action, ActionOptions? options)
    {
        if (!Workflows.Workflow.IsName(name)) throw new ArgumentException($"'{name}' is not a branch name.", nameof(name));
        Name = name;
        Workflow = workflow;
        Action = action;
        Options = options;
    }

    /// <summary>The branch name.</summary>
    public string Name { get; }
    internal Workflow<TData>? Workflow { get; }
    internal IAction<TData>? Action { get; }
    internal ActionOptions? Options { get; }

    /// <summary>A branch running one action.</summary>
    public static ParallelBranch<TData> Of(string name, IAction<TData> action, ActionOptions? options = null) =>
        new(name, null, action ?? throw new ArgumentNullException(nameof(action)), options);

    /// <summary>A branch running a delegate.</summary>
    public static ParallelBranch<TData> Of(string name, Func<TData, ActionContext, CancellationToken, Task> run, ActionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Of(name, new DelegateAction<TData>(name, async (data, context, token) =>
        {
            await run(data, context, token).ConfigureAwait(false);
            return ActionResult.Next();
        }), options);
    }

    /// <summary>A branch running a workflow.</summary>
    public static ParallelBranch<TData> Of(string name, Workflow<TData> workflow) => new(name, workflow ?? throw new ArgumentNullException(nameof(workflow)), null, null);
}

/// <summary>A compiled Parallel branch.</summary>
internal sealed record CompiledBranch<TData>(string Name, Workflow<TData> Workflow) where TData : class;

/// <summary>A finished Parallel branch.</summary>
public sealed record BranchResult<TData>(string Branch, WorkflowResult<TData> Result) where TData : class;

/// <summary>A finished ForEach item.</summary>
public sealed record ItemResult<TItem, TChild>(TItem Item, int Index, WorkflowResult<TChild> Result) where TChild : class;

/// <summary>ForEach options.</summary>
public sealed record ForEachOptions
{
    /// <summary>The most items running at once (they start in batches of this size); default 4.</summary>
    public int Concurrency { get; init; } = 4;
    /// <summary><see cref="JoinPolicy.FailFast"/> (default) fails the step at the first failed item; <see cref="JoinPolicy.AllSettled"/>
    /// merges every result, failures included.</summary>
    public JoinPolicy JoinPolicy { get; init; } = JoinPolicy.FailFast;
}

public sealed partial class WorkflowBuilder<TData>
{
    /// <summary>Runs <paramref name="child"/> as a sub-workflow with <paramref name="input"/>(Data) and applies its result. A child that does
    /// not complete fails this run unless <paramref name="failOnError"/> is false (then <paramref name="output"/> sees the failure).</summary>
    public WorkflowBuilder<TData> Run<TChild>(string name, Workflow<TChild> child, Func<TData, TChild> input, Action<TData, WorkflowResult<TChild>> output,
        bool failOnError = true) where TChild : class
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return Add(new RunStep<TData, TChild>(name, child, input, output, failOnError));
    }

    /// <summary>Runs <paramref name="branches"/> at once, each on a copy of the Data, then <paramref name="merge"/>s their results into
    /// the Data. With <see cref="JoinPolicy.FailFast"/> (default) the first failed branch aborts the others and fails the run.</summary>
    public WorkflowBuilder<TData> Parallel(string name, IEnumerable<ParallelBranch<TData>> branches, Action<TData, IReadOnlyList<BranchResult<TData>>> merge,
        JoinPolicy policy = JoinPolicy.FailFast)
    {
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(merge);
        // Single-action branches become workflows of their own, named after this workflow and step, so their task kinds are distinct.
        var compiled = branches.Select(branch => new CompiledBranch<TData>(branch.Name, branch.Workflow ??
            Workflows.Workflow.For<TData>(Name + "." + name + "." + branch.Name).WithJsonOptions(Json ?? DataCodec.DefaultOptions)
                .Then(branch.Action!, branch.Options).Build())).ToList();
        return Add(new ParallelStep<TData>(name, compiled, merge, policy));
    }

    /// <summary>Runs <paramref name="body"/> once per item of <paramref name="items"/>(Data), with <paramref name="input"/>(Data, item, index),
    /// at most <see cref="ForEachOptions.Concurrency"/> at a time, then <paramref name="merge"/>s the results in item order.
    /// <paramref name="items"/> must be a pure function of the Data.</summary>
    public WorkflowBuilder<TData> ForEach<TItem, TChild>(string name, Func<TData, IEnumerable<TItem>> items, Workflow<TChild> body,
        Func<TData, TItem, int, TChild> input, Action<TData, IReadOnlyList<ItemResult<TItem, TChild>>> merge, ForEachOptions? options = null)
        where TChild : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(merge);
        return Add(new ForEachStep<TData, TItem, TChild>(name, items, body, input, merge, options ?? new ForEachOptions()));
    }

    /// <summary>Waits for the signal <paramref name="signal"/> (sent with <see cref="WorkflowRun{TData}.SignalAsync{T}"/>; one sent earlier
    /// is kept) and applies its value. After <paramref name="timeout"/> the run continues at <paramref name="onTimeout"/>, or fails.</summary>
    public WorkflowBuilder<TData> WaitForSignal<TSignal>(string name, string signal, Action<TData, TSignal> apply, TimeSpan? timeout = null,
        string? onTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        ArgumentNullException.ThrowIfNull(apply);
        if (timeout is { } limit && limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return Add(new SignalStep<TData, TSignal>(name, signal, apply, timeout, onTimeout));
    }
}
