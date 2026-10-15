// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/extensions/codemode/tool.ts (codemodeSchema,
// getScriptReach) and packages/coding-agent/src/extensions/tool-search/tool.ts (toolSearchSchema, reachesTools).
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Discovery;

/// <summary>The built-in discovery tools: <c>codemode</c> and <c>tool_search</c>, which declare <c>reachesTools</c>. A tool of these
/// names reaches MCP tools only when it declares <c>reachesTools</c> (Pi 1.2.0 removed the schema-identity checks). Identity is
/// presentation metadata; it grants no execution, scripting or connection authority.</summary>
public static class McpDiscoveryToolIdentity
{
    public const string CodemodeName = "codemode";
    public const string ToolSearchName = "tool_search";
    /// <summary>Pi extensions/codemode/tool.ts codemodeSchema, in TypeBox's key order (type, required, properties).</summary>
    public static JsonData CodemodeSchema { get; } = JsonData.Parse("""
        {"type":"object","required":["code"],"properties":{"code":{"type":"string","description":"Raw JavaScript source."}}}
        """);
    public static JsonData ToolSearchSchema { get; } = JsonData.Parse("""
        {"type":"object","required":["query"],"properties":{"query":{"type":"string","description":"Search query for deferred tools."},"limit":{"type":"number","description":"Maximum number of tools to return. Defaults to 8."}}}
        """);

    /// <summary>Script globals that list or look up tools and namespaces, which the script may then call by any name.</summary>
    private static readonly Regex DiscoveryGlobals =
        new("(?<![A-Za-z0-9_])(searchTools|describeNamespace|describeTool|ALL_TOOLS)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    /// <summary>JavaScript's <c>/\w+/g</c>: runs of ASCII letters, digits and <c>_</c>.</summary>
    private static readonly Regex Words = new("[A-Za-z0-9_]+", RegexOptions.CultureInvariant);

    /// <summary>codemode/tool.ts getScriptReach, codemode's <c>reachesTools</c>: every tool when the script uses a discovery global
    /// (<c>searchTools</c>, <c>describeNamespace</c>, <c>describeTool</c>, <c>ALL_TOOLS</c>), else the words of its source, so
    /// <c>tools.mcp__docs__search</c> and <c>tools["mcp__docs__" + name]</c> both name the <c>mcp__docs</c> namespace. Input without
    /// a string <c>code</c> reaches nothing.</summary>
    public static ToolReach CodemodeReach(JsonData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Value.ValueKind != JsonValueKind.Object || !input.Value.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
            return ToolReach.Of([]);
        return ScriptReach(code.GetString()!);
    }

    /// <summary>The reach of one script's source (see <see cref="CodemodeReach"/>).</summary>
    public static ToolReach ScriptReach(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return DiscoveryGlobals.IsMatch(source) ? ToolReach.All : ToolReach.Of([.. Words.Matches(source).Select(match => match.Value)]);
    }

    /// <summary>tool-search/tool.ts: a query can match any tool that is not declared yet.</summary>
    public static ToolReach ToolSearchReach(JsonData input) => ToolReach.All;

    /// <summary>The host supplies the actual admitted native pipeline callback. No executor is created here.</summary>
    public static ExtensionToolDescriptor CreateCodemode(string registrationId, string description,
        ExtensionToolCallback admittedExecute, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null) =>
        Create(registrationId, CodemodeName, description, CodemodeSchema, admittedExecute, prepareLoadout);
    /// <summary>The host supplies actual discovery plus durable active-selection publication.</summary>
    public static ExtensionToolDescriptor CreateToolSearch(string registrationId, string description,
        ExtensionToolCallback admittedExecute, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null) =>
        Create(registrationId, ToolSearchName, description, ToolSearchSchema, admittedExecute, prepareLoadout);

    private static ExtensionToolDescriptor Create(string registrationId, string name, string description,
        JsonData schema, ExtensionToolCallback execute, Func<ToolLoadout, ToolLoadoutChanges?>? prepare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(execute);
        if (execute.GetInvocationList().Length != 1) throw new ArgumentException("Discovery requires one joined admitted callback.", nameof(execute));
        if (prepare?.GetInvocationList().Length > 1) throw new ArgumentException("Discovery presentation requires one pure callback.", nameof(prepare));
        return new(registrationId, name, description, schema, execute)
        // codemodeSchema and toolSearchSchema are TypeBox schemas (Type.Object), so validateToolArguments also converts their arguments.
        {
            Exposure = ToolExposure.ModelOnly, DefaultActive = true, PrepareLoadout = prepare, ParametersOrigin = ToolSchemaOrigin.TypeBox,
            ReachesTools = name == CodemodeName ? CodemodeReach : ToolSearchReach
        };
    }

}
