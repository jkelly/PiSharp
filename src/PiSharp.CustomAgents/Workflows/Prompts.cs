// Decision 0012: model-call steps and helpers. Prompt sends text to a model and applies the answer to the Data; Prompt<TResult> asks for
// a typed result through the submit_result tool, with repair rounds when the answer does not fit. Each call runs in a child conversation
// of the run (Engine/ModelCalls.cs), so a crash or restart never asks the same question twice within an attempt.
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Workflows;

/// <summary>Options for one model call.</summary>
public sealed record PromptOptions
{
    /// <summary>The model; default the workflow's, else the catalog's default.</summary>
    public ModelRef? Model { get; init; }
    /// <summary>The thinking level; default the workflow's, else the catalog's, else off.</summary>
    public string? Thinking { get; init; }
    /// <summary>Instructions for this call, after the workflow's.</summary>
    public string? Instructions { get; init; }
    /// <summary>How long the model may take; on expiry the call is aborted and the attempt fails with a <see cref="TimeoutException"/>.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Typed calls: how many repair rounds follow an answer that does not fit the result type (default 2).</summary>
    public int MaxRepairs { get; init; } = 2;
    /// <summary>Distinguishes calls within one action visit; default the call's order in the attempt (<c>prompt</c>, <c>prompt2</c>, …).</summary>
    public string? Key { get; init; }
}

public sealed partial class ActionContext
{
    private int prompts;

    /// <summary>Asks the model and returns its answer text. Within one attempt a rerun returns the recorded answer instead of asking again.</summary>
    public async Task<string> PromptAsync(string prompt, PromptOptions? options = null, CancellationToken cancellationToken = default) =>
        (await PromptAnswerAsync(prompt, options, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>Asks the model and returns its whole answer: text, message and the child conversation that answered.</summary>
    public Task<ModelAnswer> PromptAnswerAsync(string prompt, PromptOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        options ??= new PromptOptions();
        var agent = ModelCalls.PromptAgent(ModelFor(options), ThinkingFor(options), ModelCalls.Join(Defaults?.Instructions, options.Instructions), null);
        return ModelCalls.CallAsync(this, options.Key ?? NextKey(), agent, prompt, options.Timeout, null, cancellationToken);
    }

    /// <summary>Asks the model for a <typeparamref name="T"/> through the submit_result tool. <typeparamref name="T"/> must be declared on
    /// the workflow (<see cref="WorkflowBuilder{TData}.DeclareResult{TResult}"/>, or used by a <c>Prompt&lt;T&gt;</c> step), so its tool is
    /// installed before a restart resumes the call.</summary>
    public Task<T> PromptAsync<T>(string prompt, PromptOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        options ??= new PromptOptions();
        var result = Scope.ResultFor(typeof(T)) ?? throw new InvalidOperationException(
            $"Workflow '{Workflow}' has no result type {typeof(T).Name}; declare it with DeclareResult<{typeof(T).Name}>().");
        var instructions = ModelCalls.Join(Defaults?.Instructions, options.Instructions,
            $"When you have the answer, call the {ResultTool.ToolName} tool with it. Do not answer in plain text.");
        var agent = ModelCalls.PromptAgent(ModelFor(options), ThinkingFor(options), instructions, result);
        return ModelCalls.CallTypedAsync<T>(this, options.Key ?? NextKey(), agent, result, prompt, options.Timeout, Math.Max(0, options.MaxRepairs),
            cancellationToken);
    }

    private Hosting.IHasPromptDefaults? Defaults => Scope.Definition as Hosting.IHasPromptDefaults;

    internal ModelRef? ModelFor(PromptOptions options) => options.Model ?? Defaults?.Model ?? Scope.Catalog.Options.DefaultModel;

    internal string? ThinkingFor(PromptOptions options) => options.Thinking ?? Defaults?.Thinking ?? Scope.Catalog.Options.DefaultThinking;

    private string NextKey()
    {
        prompts++;
        return prompts == 1 ? "prompt" : "prompt" + prompts;
    }
}

public sealed partial class WorkflowBuilder<TData>
{
    private readonly List<Type> results = [];

    internal IReadOnlyList<Type> ResultTypes => results;

    /// <summary>Adds an action that asks the model <paramref name="prompt"/>(Data) and applies the answer text.</summary>
    public WorkflowBuilder<TData> Prompt(string name, Func<TData, string> prompt, Action<TData, string> apply, PromptOptions? options = null,
        ActionOptions? action = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(apply);
        var call = (options ?? new PromptOptions()) with { Key = options?.Key ?? "prompt" };
        return Add(new ActionStep<TData>(name, "prompt", new DelegateAction<TData>(name, async (data, context, token) =>
        {
            apply(data, await context.PromptAsync(prompt(data), call, token).ConfigureAwait(false));
            return ActionResult.Next();
        }), action ?? new ActionOptions()));
    }

    /// <summary>Adds an action that asks the model for a <typeparamref name="TResult"/> (structured output through the submit_result tool,
    /// validated against <typeparamref name="TResult"/>'s JSON schema) and applies it.</summary>
    public WorkflowBuilder<TData> Prompt<TResult>(string name, Func<TData, string> prompt, Action<TData, TResult> apply, PromptOptions? options = null,
        ActionOptions? action = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(apply);
        DeclareResult<TResult>();
        var call = (options ?? new PromptOptions()) with { Key = options?.Key ?? "prompt" };
        return Add(new ActionStep<TData>(name, "prompt:" + typeof(TResult).Name, new DelegateAction<TData>(name, async (data, context, token) =>
        {
            apply(data, await context.PromptAsync<TResult>(prompt(data), call, token).ConfigureAwait(false));
            return ActionResult.Next();
        }), action ?? new ActionOptions()));
    }

    /// <summary>Declares a result type for <see cref="ActionContext.PromptAsync{T}"/> calls in code actions.</summary>
    public WorkflowBuilder<TData> DeclareResult<TResult>()
    {
        if (!results.Contains(typeof(TResult))) results.Add(typeof(TResult));
        return this;
    }
}
