using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Discovery;

/// <summary>Identity follows pinned Pi: exact name and the factory's schema object reference.
/// Identity is presentation metadata; it grants no execution, scripting or connection authority.</summary>
public static class McpDiscoveryToolIdentity
{
    public const string CodemodeName = "codemode";
    public const string ToolSearchName = "tool_search";
    /// <summary>Pi 1.1.0 extensions/codemode/tool.ts codemodeSchema, in TypeBox's key order (type, required, properties).</summary>
    public static JsonData CodemodeSchema { get; } = JsonData.Parse("""
        {"type":"object","required":["code"],"properties":{"code":{"type":"string","description":"Raw JavaScript source."}}}
        """);
    public static JsonData ToolSearchSchema { get; } = JsonData.Parse("""
        {"type":"object","required":["query"],"properties":{"query":{"type":"string","description":"Search query for deferred tools."},"limit":{"type":"number","description":"Maximum number of tools to return. Defaults to 8."}}}
        """);

    public static bool IsCodemodeTool(string name, JsonData parameters) =>
        name == CodemodeName && ReferenceEquals(parameters, CodemodeSchema);
    public static bool IsToolSearchTool(string name, JsonData parameters) =>
        name == ToolSearchName && ReferenceEquals(parameters, ToolSearchSchema);

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
        { Exposure = ToolExposure.ModelOnly, DefaultActive = true, PrepareLoadout = prepare, ParametersOrigin = ToolSchemaOrigin.TypeBox };
    }

}
