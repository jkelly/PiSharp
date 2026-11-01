// Decision 0012: a workflow action in C# script. The script changes the typed Data in place (`Data.Total += 1;`) and may return an
// ActionResult; ending without one continues with the next action. It is compiled when the workflow is built, so errors surface there, and
// runs like any code action: at least once, its Data changes and decision committed together. Data types must be public.
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Engine;

namespace PiSharp.CustomAgents.Scripting;

/// <summary>What an action script sees.</summary>
public sealed class ScriptActionGlobals<TData> where TData : class
{
    /// <summary>The workflow's Data; change it in place.</summary>
    public required TData Data { get; init; }
    /// <summary>The action context (step, attempt, once-only work, model calls, services).</summary>
    public required ActionContext Context { get; init; }
    /// <summary>Cancelled when the attempt is.</summary>
    public required CancellationToken CancellationToken { get; init; }
    /// <summary>Messages to and from the team's agents (needs a team).</summary>
    public IAgentMessenger Messages => Context.Messages;
}

/// <summary>C# script steps for workflows.</summary>
public static class ScriptWorkflowExtensions
{
    /// <summary>Adds an action named <paramref name="name"/> running <paramref name="code"/>, compiled now by <paramref name="host"/> (default
    /// <see cref="CSharpScriptHost.Shared"/>).</summary>
    /// <exception cref="ScriptCompilationException">The script has errors.</exception>
    public static WorkflowBuilder<TData> CSharp<TData>(this WorkflowBuilder<TData> builder, string name, string code, CSharpScriptHost? host = null,
        ActionOptions? options = null) where TData : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var script = (host ?? CSharpScriptHost.Shared).Compile<ScriptActionGlobals<TData>, ActionResult>(code, name + ".csx",
            "return global::PiSharp.CustomAgents.Workflows.ActionResult.Next();");
        return builder.Add(new ActionStep<TData>(name, "csharp", new ScriptAction<TData>(name, script), options ?? new ActionOptions()));
    }
}

internal sealed class ScriptAction<TData>(string name, CompiledScript<ScriptActionGlobals<TData>, ActionResult> script) : IAction<TData> where TData : class
{
    public string Name => name;

    public async Task<ActionResult> ExecuteAsync(TData data, ActionContext context, CancellationToken cancellationToken) =>
        await script.RunAsync(new() { Data = data, Context = context, CancellationToken = cancellationToken }, cancellationToken).ConfigureAwait(false)
        ?? ActionResult.Next();
}
