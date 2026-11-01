// Decision 0012: agents that write their own tools. Opt-in twice: the host calls JsAuthoring.Enable on the team, and a definition sets
// CanCreateTools; only such agents select the agents.authoring extension (create_tool, remove_tool) and the tools.js-authored source that
// holds what was written. Written tools are pure JavaScript (no tools, no documents), named js_<name>, bounded in size and count, offered to
// every agent that may write tools from its next request, and checked to parse before they are kept. They are stored in the session
// document pisharp.agents.created-tools, so a reopened session gets them back before scheduling resumes. The code is model-written, so it
// is untrusted: the runtime must isolate scripts in a worker process unless the host opts out. Both tools are replay-safe: the document
// write is idempotent, and installing a tool again is a no-op.
using System.Text.RegularExpressions;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.JavaScript;

/// <summary>Options of <see cref="JsAuthoring.Enable"/>.</summary>
public sealed record JsAuthoringOptions
{
    /// <summary>Runs the written tools; default a runtime with worker isolation.</summary>
    public JsRuntime? Runtime { get; init; }
    /// <summary>Whether written tools must run in a worker process (default true). Turn off only where the model's code is trusted.</summary>
    public bool RequireIsolation { get; init; } = true;
    /// <summary>The most written tools a session keeps; default 16.</summary>
    public int MaxTools { get; init; } = 16;
    /// <summary>The longest code of one tool, in characters; default 16 KB.</summary>
    public int MaxCodeChars { get; init; } = 16 * 1024;
    /// <summary>The longest parameters schema of one tool, as JSON characters; default 8 KB.</summary>
    public int MaxSchemaChars { get; init; } = 8 * 1024;
}

/// <summary>Lets agents with <see cref="AgentDefinition.CanCreateTools"/> write JavaScript tools.</summary>
public static partial class JsAuthoring
{
    /// <summary>The extension holding <c>create_tool</c> and <c>remove_tool</c>.</summary>
    public const string ExtensionName = "agents.authoring";
    /// <summary>The tool source holding the written tools (extension <c>tools.js-authored</c>).</summary>
    public const string SourceId = "js-authored";

    /// <summary><c>pisharp.agents.created-tools</c>: <c>{ tools: { name: { description, parameters, code, by } } }</c>.</summary>
    internal static SessionDocToken Doc { get; } = Documents.DefineSessionDoc("pisharp.agents.created-tools", 1,
        () => (JsonObjectValue)JsonValue.Parse("""{"tools":{}}"""));

    private static readonly JsonObjectValue CreateParameters = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["name","description","code"],"properties":{
          "name":{"type":"string","description":"js_ then 1 to 40 lowercase letters, digits or _"},
          "description":{"type":"string","description":"What the tool does, for whoever calls it"},
          "code":{"type":"string","description":"JavaScript: the body of an async function that reads its arguments from `args` and returns the result. No tools, files or network."},
          "parameters":{"type":"object","description":"JSON schema of the arguments (an object schema); default none"}}}
        """);

    private static readonly JsonObjectValue RemoveParameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["name"],"properties":{"name":{"type":"string","description":"A tool you wrote (js_...)"}}}""");

    [GeneratedRegex("^js_[a-z0-9_]{1,40}$")]
    private static partial Regex NamePattern();

    /// <summary>Enables authoring on <paramref name="team"/>; returns the source of written tools.</summary>
    public static ToolSource Enable(AgentTeam team, JsAuthoringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(team);
        options ??= new JsAuthoringOptions();
        var runtime = options.Runtime ?? new JsRuntime(new JsRuntimeOptions { Isolation = options.RequireIsolation ? JsIsolation.Worker : JsIsolation.Default });
        if (options.RequireIsolation && !runtime.IsIsolated)
            throw new InvalidOperationException(
                "Model-written JavaScript runs in a worker process: call JsWorkerHost.Configure() at startup, or set RequireIsolation = false.");
        var source = new DynamicToolSource(SourceId);
        team.Catalog.AddSource(source);
        var extension = new Extension { Name = ExtensionName, Tools = [CreateTool(source, runtime, options), RemoveTool(source)] };
        team.Registry.Install(extension);
        var previous = team.Catalog.SystemExtensions;
        team.Catalog.SystemExtensions = (definition, placement) => definition.CanCreateTools
            ? [.. previous(definition, placement), extension, new Extension { Name = source.ExtensionName, Tools = [.. source.Tools] }]
            : previous(definition, placement);
        team.Opening.Add(async (harness, context) =>
        {
            var doc = await harness.SnapshotAsync(Doc, context).ConfigureAwait(false);
            var restored = new List<ToolRegistration>();
            foreach (var (name, value) in Entries(doc))
            {
                try { restored.Add(JsTool.Create(Definition(name, value), runtime)); }
                catch (ArgumentException error) { team.Options.OnReport?.Invoke(new InvalidOperationException($"The written tool '{name}' was not restored: {error.Message}", error)); }
            }
            source.Set(restored);
        });
        return source;
    }

    private static ToolRegistration CreateTool(DynamicToolSource source, JsRuntime runtime, JsAuthoringOptions options) => new()
    {
        Name = "create_tool",
        Description = "Writes a JavaScript tool you (and agents allowed to write tools) can call from your next step. The code is the body of an " +
            "async function: read arguments from `args`, compute, and `return` the result (strings as text, anything else as JSON). It has no " +
            "tools, files or network. Creating a tool with an existing name replaces it.",
        Parameters = CreateParameters,
        Replay = ToolReplay.Safe,
        Callers = [ToolCaller.Model],
        Execute = async (arguments, api, context) =>
        {
            var fields = (JsonObjectValue)arguments;
            var name = ((JsonStringValue)fields["name"]).Value;
            var parameters = fields.TryGetValue("parameters", out var schema) && schema is JsonObjectValue given ? given : ToolRegistration.EmptyParameters;
            var entry = JsonValue.Object([
                KeyValuePair.Create("description", fields["description"]),
                KeyValuePair.Create("parameters", (JsonValue)parameters),
                KeyValuePair.Create("code", fields["code"]),
            ]);
            if (Refusal(name, entry, options) is { } refused) return AgentTool.ToExecution(AgentToolResult.Error(refused));
            var tool = JsTool.Create(Definition(name, entry), runtime);
            var agent = await api.AgentAsync(context).ConfigureAwait(false);
            if (agent.Tools.Concat(agent.Callable).Concat(agent.Deferred).Any(existing => existing.Name == name) && !source.Tools.Any(existing => existing.Name == name))
                return AgentTool.ToExecution(AgentToolResult.Error($"A tool named '{name}' already exists and was not written by an agent; choose another name."));
            var full = await api.CommitAsync(async tx =>
            {
                var tools = (await tx.DocAsync(Doc).ConfigureAwait(false)).GetObject("tools");
                if (!tools.ContainsKey(name) && tools.Count >= options.MaxTools) return true;
                if (tools.Get(name) is not JsonObjectValue stored || !JsonValue.DeepEquals(Content(stored), entry))
                    tools.Set(name, entry.SetItem("by", (JsonValue)api.ConversationId.Value));
                return false;
            }, context).ConfigureAwait(false);
            if (full) return AgentTool.ToExecution(AgentToolResult.Error($"This session already keeps {options.MaxTools} written tools; remove one first."));
            source.Replace(tool);
            return AgentTool.ToExecution(new AgentToolResult
            {
                Text = $"Created {name}. Call it from your next step.",
                Control = new ToolControl { AddTools = [name] },
            });
        },
    };

    private static ToolRegistration RemoveTool(DynamicToolSource source) => new()
    {
        Name = "remove_tool",
        Description = "Removes a JavaScript tool written with create_tool.",
        Parameters = RemoveParameters,
        Replay = ToolReplay.Safe,
        Callers = [ToolCaller.Model],
        Execute = async (arguments, api, context) =>
        {
            var name = ((JsonStringValue)((JsonObjectValue)arguments)["name"]).Value;
            var found = await api.CommitAsync(async tx => (await tx.DocAsync(Doc).ConfigureAwait(false)).GetObject("tools").Delete(name), context)
                .ConfigureAwait(false);
            var removed = source.Remove(name);
            return AgentTool.ToExecution(found || removed ? AgentToolResult.Ok($"Removed {name}.") : AgentToolResult.Error($"No written tool is named '{name}'."));
        },
    };

    /// <summary>Why a tool cannot be kept; null when it can.</summary>
    private static string? Refusal(string name, JsonObjectValue entry, JsAuthoringOptions options)
    {
        if (!NamePattern().IsMatch(name)) return $"'{name}' is not a written tool name: use js_ then 1 to 40 lowercase letters, digits or _.";
        var code = ((JsonStringValue)entry["code"]).Value;
        if (code.Length > options.MaxCodeChars) return $"The code is {code.Length} characters; the limit is {options.MaxCodeChars}.";
        var schema = (JsonObjectValue)entry["parameters"];
        if (schema.ToJson().Length > options.MaxSchemaChars) return $"The parameters schema is longer than {options.MaxSchemaChars} characters.";
        if (!schema.TryGetValue("type", out var type) || type is not JsonStringValue { Value: "object" }) return "The parameters schema must have \"type\": \"object\".";
        if (string.IsNullOrWhiteSpace(((JsonStringValue)entry["description"]).Value)) return "The description is empty.";
        return JsRuntime.CheckSyntax(code) is { } syntax ? "The code does not parse: " + syntax : null;
    }

    private static JsToolDefinition Definition(string name, JsonObjectValue entry) =>
        new(name, ((JsonStringValue)entry["description"]).Value, ((JsonStringValue)entry["code"]).Value)
        {
            Parameters = (JsonObjectValue)entry["parameters"],
        };

    private static JsonObjectValue Content(JsonObjectValue stored) => JsonValue.Object(
        stored.Where(field => field.Key is "description" or "parameters" or "code"));

    private static IEnumerable<(string Name, JsonObjectValue Entry)> Entries(JsonObjectValue? doc) =>
        doc is not null && doc.TryGetValue("tools", out var tools) && tools is JsonObjectValue map
            ? map.Where(pair => pair.Value is JsonObjectValue).Select(pair => (pair.Key, (JsonObjectValue)pair.Value))
            : [];
}

/// <summary>Runs this executable as a JavaScript worker process when asked to, and makes it the worker of every runtime.</summary>
public static class JsWorkerHost
{
    /// <summary>
    /// Call first in <c>Main</c>: <c>if (JsWorkerHost.TryRun(args, out var worker)) return await worker;</c>. True when this process was
    /// started as a worker (its only argument is <see cref="PiSharp.Codemode.CodemodeWorker.Argument"/>); <paramref name="exit"/> then runs it.
    /// </summary>
    public static bool TryRun(string[] args, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Task<int>? exit)
    {
        ArgumentNullException.ThrowIfNull(args);
        exit = args is [PiSharp.Codemode.CodemodeWorker.Argument]
            ? PiSharp.Codemode.CodemodeWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput())
            : null;
        return exit is not null;
    }

    /// <summary>Makes this executable the default worker (unless the host set another), before any runtime is created; false when the process
    /// has no launchable entry.</summary>
    public static bool Configure()
    {
        PiSharp.Codemode.CodemodeWorker.Default ??= PiSharp.Codemode.CodemodeWorkerLauncher.ForCurrentProcess();
        return PiSharp.Codemode.CodemodeWorker.Default is not null;
    }
}
