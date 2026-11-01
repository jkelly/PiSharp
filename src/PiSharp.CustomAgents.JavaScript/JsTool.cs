// Decision 0012: JavaScript tools. A pure tool runs its script with the call's arguments bound as `args` and nothing else: no tools, no
// documents. It is replay-safe (a rerun only computes again); the returned value is the tool's text (strings as is, anything else as JSON)
// and, when the definition declares an output schema, its structured output. A durable tool may call other tools as `tools.<name>(args)`:
// it runs as durable code mode (PiSharp.Durable.Tools), which journals every delivered result, so after a restart the script replays to
// where it was without repeating a call; it sees only its AllowedTools, through an api that narrows the agent's callable tools and refuses
// any other nested call. Durable code mode renders its own output, so a durable tool returns text only.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;

namespace PiSharp.CustomAgents.JavaScript;

/// <summary>What a JavaScript tool may do.</summary>
public enum JsToolMode
{
    /// <summary>Computes from its arguments only (the default).</summary>
    Pure,
    /// <summary>May call the tools named in <see cref="JsToolDefinition.AllowedTools"/>; replays after a restart.</summary>
    Durable,
}

/// <summary>A tool written in JavaScript. <paramref name="Code"/> is an async function body that reads its arguments from <c>args</c> and
/// returns the result.</summary>
public sealed record JsToolDefinition(string Name, string Description, string Code)
{
    /// <summary>The arguments' JSON schema (an object schema); default no arguments.</summary>
    public JsonObjectValue Parameters { get; init; } = ToolRegistration.EmptyParameters;
    /// <summary>What the script may do; default <see cref="JsToolMode.Pure"/>.</summary>
    public JsToolMode Mode { get; init; } = JsToolMode.Pure;
    /// <summary>The tools a durable script may call; ignored for pure tools.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    /// <summary>The returned value's JSON schema; when set, a pure tool's value is also its structured output (validated by the Harness).</summary>
    public JsonObjectValue? OutputSchema { get; init; }
    /// <summary>Whether the tool is deferred (offered once loaded).</summary>
    public bool Deferred { get; init; }
}

/// <summary>Builds durable tools from JavaScript.</summary>
public static class JsTool
{
    /// <summary>The tool <paramref name="definition"/> describes, running on <paramref name="runtime"/> (default <see cref="JsRuntime.Shared"/>).</summary>
    public static ToolRegistration Create(JsToolDefinition definition, JsRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Code);
        if (JsRuntime.CheckSyntax(definition.Code) is { } syntax)
            throw new ArgumentException($"The code of JavaScript tool '{definition.Name}' does not parse: {syntax}", nameof(definition));
        runtime ??= JsRuntime.Shared;
        Tools.ToolNames.Validate(definition.Name);
        var durable = definition.Mode == JsToolMode.Durable;
        return new ToolRegistration
        {
            Name = definition.Name,
            Description = definition.Description,
            Parameters = definition.Parameters,
            ParametersOrigin = PiSharp.Contracts.ToolSchemaOrigin.JsonSchema,
            StructuredOutputSchema = durable ? null : definition.OutputSchema,
            Replay = ToolReplay.Safe,
            Deferred = definition.Deferred,
            Execute = durable ? Durable(definition, runtime) : Pure(definition, runtime),
        };
    }

    private static ToolExecute Pure(JsToolDefinition definition, JsRuntime runtime) => async (arguments, _, context) =>
    {
        var result = await runtime.RunAsync(definition.Code, [KeyValuePair.Create("args", arguments)], context.CancellationToken).ConfigureAwait(false);
        if (!result.Ok)
            return Text(Join(result.Output, $"Tool '{definition.Name}' failed: {result.Error}"), error: true);
        return Text(Join(result.Output, result.Value is null && result.Output.Length == 0 ? "(no result)" : result.ValueText)) with
        {
            StructuredOutput = definition.OutputSchema is not null ? result.Value : null,
        };
    };

    private static ToolExecute Durable(JsToolDefinition definition, JsRuntime runtime)
    {
        var allowed = definition.AllowedTools.ToImmutableHashSet(StringComparer.Ordinal);
        var codemode = DurableCodemode.CreateTool(new CodemodeToolOptions { Sandbox = runtime.Sandbox, Models = false });
        return (arguments, api, context) => codemode.Execute(
            JsonValue.Object([KeyValuePair.Create("code", (JsonValue)(JsRuntime.Bind([KeyValuePair.Create("args", arguments)]) + definition.Code))]),
            new AllowlistApi(api, allowed), context);
    }

    internal static ToolExecutionResult Text(string text, bool error = false) => new()
    {
        Output = JsonValue.Array(JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)text)])),
        IsError = error ? true : null,
    };

    private static string Join(string output, string text) => output.Length == 0 ? text : text.Length == 0 ? output : output + "\n" + text;

    /// <summary>A tool api whose agent can call only <paramref name="allowed"/>.</summary>
    private sealed class AllowlistApi(IToolExecutionApi inner, ImmutableHashSet<string> allowed) : DelegatingToolExecutionApi(inner)
    {
        public override async Task<PiSharp.Durable.Agent> AgentAsync(DurableContext context)
        {
            var agent = await Inner.AgentAsync(context).ConfigureAwait(false);
            return agent with { Callable = [.. agent.Callable.Where(candidate => allowed.Contains(candidate.Name))] };
        }

        public override Task<NestedToolExecutionResult> ExecuteToolAsync(string name, JsonObjectValue arguments, DurableContext context,
            NestedCallOptions? options = null) => allowed.Contains(name)
            ? Inner.ExecuteToolAsync(name, arguments, context, options)
            : throw new InvalidOperationException($"This script may not call '{name}'.");
    }
}

/// <summary>A tool source of JavaScript tools: add, replace and remove them while agents run.</summary>
public sealed class JsToolSource(string id, JsRuntime? runtime = null) : Tools.ToolSource(id)
{
    private readonly JsRuntime runtime = runtime ?? JsRuntime.Shared;

    /// <summary>Adds or replaces the tool <paramref name="definition"/> describes.</summary>
    public JsToolSource Set(JsToolDefinition definition)
    {
        var tool = JsTool.Create(definition, runtime);
        Publish(tools => [.. tools.Where(existing => existing.Name != tool.Name), tool]);
        return this;
    }

    /// <summary>Removes the tool named <paramref name="name"/>; false when there is none.</summary>
    public bool Remove(string name)
    {
        var removed = false;
        Publish(tools =>
        {
            removed = tools.Any(tool => tool.Name == name);
            return removed ? [.. tools.Where(tool => tool.Name != name)] : tools;
        });
        return removed;
    }
}
