using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Settings;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;

// Upstream: packages/coding-agent/src/cli/args.ts, core/settings-manager.ts, core/sdk.ts, core/agent-session.ts,
// core/mcp-servers.ts and main.ts at abe508e1b89912adde45528136c3221eb69acdd7.
internal static partial class Program
{
    private static ToolSelectionCliOptions Parse(params string[] args)
    {
        var options = new ToolSelectionCliOptions();
        for (var index = 0; index < args.Length; index++)
            if (!ToolSelectionCliConfiguration.TryConsume(args, ref index, ref options)) throw new InvalidOperationException("Unconsumed " + args[index]);
        return options;
    }

    private static void ParseToolFlags()
    {
        var parsed = Parse("--tools", " read , mcp__radius__* ,", "--exclude-tools", "-x,ask_*", "--no-mcp");
        Names(["read", "mcp__radius__*"], parsed.Tools!.Value, "--tools entries");
        Names(["-x", "ask_*"], parsed.Excluded, "--exclude-tools takes names and patterns without modifier validation");
        Check(parsed.NoMcp && parsed.IsSpecified, "--no-mcp was not parsed.");
        Check(!Parse("--tools", "read").NoMcp, "--no-mcp defaulted on.");
        Names(["+codemode", "-write"], Parse("-t", "+codemode,-write").Tools!.Value, "modifier list");
        var mixed = Throws<SessionCommandException>(() => Parse("--tools", "read,+codemode"), "mixed list");
        Equal(SessionCommandFailure.InvalidArguments, mixed.Failure, "mixed failure");
        Equal("--tools: tool names cannot be mixed with +name or -name entries", mixed.Message, "mixed message");
        var pattern = Throws<SessionCommandException>(() => Parse("-t", "+mcp__radius__*"), "modifier pattern");
        Equal("-t: +name and -name entries take exact tool names, not patterns: +mcp__radius__*", pattern.Message, "pattern message");
        ImmutableArray<string>? scalar = null; var index = 0; string[] legacy = ["-t", "-read,bash"];
        var legacyError = Throws<SessionCommandException>(() => ToolSelectionCliConfiguration.TryConsume(legacy, ref index, ref scalar), "legacy overload");
        Equal("-t: tool names cannot be mixed with +name or -name entries", legacyError.Message, "legacy overload message");
        Equal("tool names cannot be mixed with +name or -name entries", ToolNamePatterns.GetToolListError(["-a", "b"]), "getToolListError mixed");
        Equal("+name and -name entries take exact tool names, not patterns: -mcp__*", ToolNamePatterns.GetToolListError(["+a", "-mcp__*", "+b*"]), "first pattern reported");
        Equal(null, ToolNamePatterns.GetToolListError(["read", "mcp__*"]), "plain list");
        Equal(null, ToolNamePatterns.GetToolListError([]), "empty list");
        Check(ToolSelectionCliConfiguration.Flags.Contains("[--no-mcp]", StringComparison.Ordinal), "Usage omits --no-mcp.");
    }

    private static async Task CommandErrors()
    {
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await SessionCommands.RunAsync(["session", "create", "--session", Temp("s.jsonl"), "--workspace", Temp(""), "--tools", "read,+codemode"], output, error);
        Equal(2, exit, "session create exit code");
        var failure = JsonDocument.Parse(error.ToString()).RootElement;
        Equal("InvalidArguments", failure.GetProperty("code").GetString(), "session create code");
        Equal("--tools: tool names cannot be mixed with +name or -name entries", failure.GetProperty("message").GetString(), "session create message");
        error = new StringWriter();
        var (rpc, rpcExit) = await TerminalSessionCommand.ValidateStartupAsync(["session", "terminal", "--live", "--provider", "openai", "--model", "gpt-4o-mini",
            "--session", Temp("t.jsonl"), "--workspace", Temp(""), "-t", "+mcp__radius__*"], error);
        Check(rpc is null, "Invalid terminal startup produced arguments."); Equal(2, rpcExit, "terminal exit code");
        Equal("-t: +name and -name entries take exact tool names, not patterns: +mcp__radius__*",
            JsonDocument.Parse(error.ToString()).RootElement.GetProperty("message").GetString(), "terminal message");
    }

    private static async Task ProviderRequiresModel()
    {
        var error = new StringWriter();
        var (arguments, exit) = await TerminalSessionCommand.ValidateStartupAsync(["session", "terminal", "--live", "--provider", "openai",
            "--session", Temp("p.jsonl"), "--workspace", Temp("")], error);
        Check(arguments is null, "Provider without model was admitted."); Equal(2, exit, "exit code");
        var failure = JsonDocument.Parse(error.ToString()).RootElement;
        Equal("InvalidArguments", failure.GetProperty("code").GetString(), "code");
        Equal("--provider requires --model (for example: --provider openai --model <pattern>)", failure.GetProperty("message").GetString(), "message");
        var accepted =Throws<SessionCommandException>(() => RpcSessionCommand.ResolveStartupWorkspace(["session", "rpc", "--live", "--provider", "anthropic",
            "--session", "relative.jsonl", "--workspace", Temp("")]), "provider without model in rpc");
        Equal("--provider requires --model (for example: --provider anthropic --model <pattern>)", accepted.Message, "rpc message");
        Equal(Path.GetFullPath(Temp("")), RpcSessionCommand.ResolveStartupWorkspace(["session", "rpc", "--live", "--provider", "openai", "--model", "o3",
            "--session", Temp("ok.jsonl"), "--workspace", Temp("")]), "provider with model still parses");
    }

    private static void ResolveModifiers()
    {
        var selection = ToolSelectionCliConfiguration.ResolveOptions(new(ImmutableArray.Create("+grep", "-edit")), null)!;
        Names(["read", "bash", "write", "grep"], selection.Names, "modifier initial names");
        Check(selection.UseAvailableDefaults && selection.IncludeDefaultExtensions && selection.LifetimePolicy!.AllowedNames is null,
            "Modifiers became a lifetime cap or strict selection.");
        var plain = ToolSelectionCliConfiguration.ResolveOptions(new(ImmutableArray.Create("read", "mcp__srv__*")), null)!;
        Check(!plain.UseAvailableDefaults && !plain.IncludeDefaultExtensions && plain.LifetimePolicy!.AllowedNames!.Count == 2, "Plain list cap changed.");
        var none = ToolSelectionCliConfiguration.ResolveOptions(new(NoTools: true) { NoMcp = true }, null)!;
        Check(none.Names.IsEmpty && !none.LifetimePolicy!.IsAllowed("mcp__srv__x"), "--no-tools kept MCP tools.");
    }

    private static void Patterns()
    {
        var match = ToolNamePatterns.CreateMatcher(["read", "mcp__radius__*", "*_search", "a*b*c", "x.y", "q+"]);
        Check(match("read") && !match("rea") && !match("read2"), "Exact names are not exact.");
        Check(match("mcp__radius__") && match("mcp__radius__list") && !match("mcp__radiusx__list") && !match("xmcp__radius__a"), "Prefix pattern is not anchored.");
        Check(match("tool_search") && match("_search") && !match("tool_search2"), "Suffix pattern is not anchored.");
        Check(match("abc") && match("a1b2c") && match("abbc") && !match("acb") && !match("ab"), "Middle segments are wrong.");
        Check(match("x.y") && !match("xzy") && match("q+") && !match("qq"), "Regex metacharacters were not literal.");
        var star = ToolNamePatterns.CreateMatcher(["*"]); Check(star("") && star("anything"), "Star must match any name.");
        var overlap = ToolNamePatterns.CreateMatcher(["a*a"]); Check(!overlap("a") && overlap("aa") && overlap("aba"), "Overlapping prefix/suffix.");
        Check(ToolNamePatterns.IsMcpToolName("mcp__srv__tool") && ToolNamePatterns.IsMcpToolName("list_mcp_resources") &&
            ToolNamePatterns.IsMcpToolName("list_mcp_resource_templates") && ToolNamePatterns.IsMcpToolName("read_mcp_resource") &&
            !ToolNamePatterns.IsMcpToolName("mcp_x") && !ToolNamePatterns.IsMcpToolName("read"), "MCP tool classification.");
    }

    private static void McpKept()
    {
        var named = AllowedToolSelection.Create(["read", "codemode"]);
        Check(named.IsAllowed("read") && !named.IsAllowed("write"), "Allowlist lost its cap.");
        Check(named.IsAllowed("mcp__srv__x") && named.IsAllowed("read_mcp_resource") && !named.IsNamed("mcp__srv__x"), "--tools removed MCP tools.");
        var mcp = AllowedToolSelection.Create(["read", "mcp__radius__*"]);
        Check(mcp.IsAllowed("mcp__radius__t") && mcp.IsNamed("mcp__radius__t") && !mcp.IsAllowed("mcp__other__t") && !mcp.IsAllowed("list_mcp_resources"),
            "An mcp__ entry must filter MCP tools.");
        var empty = AllowedToolSelection.Create([]); Check(!empty.IsAllowed("mcp__srv__x"), "An empty list kept MCP tools.");
        var none = AllowedToolSelection.Create(noTools: NoToolsMode.All); Check(!none.IsAllowed("mcp__srv__x"), "--no-tools kept MCP tools.");
        var excluded = AllowedToolSelection.Create(["read"], ["mcp__srv__*", "rea*"]);
        Check(!excluded.IsAllowed("mcp__srv__x") && excluded.IsAllowed("mcp__other__x") && !excluded.IsAllowed("read") && !excluded.IsNamed("read"),
            "--exclude-tools patterns must apply to MCP and named tools.");
        Names([], excluded.InitialNames, "excluded initial names");
    }

    private static void Modifiers()
    {
        var policy = AllowedToolSelection.Create(["+codemode", "-write", "+codemode", "-missing", "+"]);
        Names(["read", "bash", "edit", "codemode"], policy.InitialNames, "built-in defaults with modifiers");
        Check(policy.AllowedNames is null && policy.IncludeDefaultExtensions && policy.UsesDefaultTools, "Modifiers became an allowlist.");
        Names(["+codemode", "-write", "+codemode", "-missing", "+"], policy.DefaultToolModifiers, "retained modifiers");
        Names(["read", "grep", "codemode"], AllowedToolSelection.Create(["+codemode", "-ls"], configuredDefaults: ["read", "ls", "grep"]).InitialNames, "settings defaults");
        var all = AllowedToolSelection.Create(["+codemode"], noTools: NoToolsMode.All);
        Names(["codemode"], all.InitialNames, "--no-tools with modifiers"); Names(["codemode"], all.AllowedNames!.Order(), "--no-tools modifier cap");
        Check(!all.UsesDefaultTools, "--no-tools uses defaults.");
        var builtin = AllowedToolSelection.Create(["+codemode"], noTools: NoToolsMode.Builtin);
        Check(builtin.AllowedNames is null && !builtin.UsesDefaultTools, "--no-builtin-tools modifiers capped the registry.");
        Names(["codemode"], builtin.InitialNames, "--no-builtin-tools with modifiers");
        var plain = AllowedToolSelection.Create(["read"]); Check(!plain.UsesDefaultTools && plain.DefaultToolModifiers.IsEmpty, "Plain list uses defaults.");
        var invalid = Throws<ArgumentException>(() => AllowedToolSelection.Create(["read", "-bash"]), "mixed SDK list");
        Equal("Invalid tools option: tool names cannot be mixed with +name or -name entries", invalid.Message, "SDK error");
        Names(["read", "bash", "edit", "write", "grep"], CodingAgentDefaults("[\"+grep\"]"), "defaultTools modifiers");
        Names(["ls"], CodingAgentDefaults("[\"ls\",\"-read\"]"), "defaultTools plain list with modifier");
        Names([], CodingAgentDefaults("[]"), "defaultTools empty list");
    }
    private static ImmutableArray<string> CodingAgentDefaults(string json) =>
        PiSharp.CodingAgent.Configuration.StartupToolSelection.Resolve(JsonData.Parse("{\"defaultTools\":" + json + "}"))!.Value;

    private static ImmutableArray<ToolSelectionDescriptor> Catalog(params (string Name, ToolExposure Exposure, bool Extension)[] tools) =>
        tools.Select(tool => new ToolSelectionDescriptor(tool.Name, tool.Exposure, true, tool.Extension)).ToImmutableArray();

    private static void InitialSelection()
    {
        var catalog = Catalog(("read", ToolExposure.Direct, false), ("bash", ToolExposure.Direct, false), ("codemode", ToolExposure.Direct, true),
            ("mcp__radius__a", ToolExposure.Direct, true), ("mcp__radius__b", ToolExposure.Deferred, true), ("mcp__other__c", ToolExposure.Direct, true),
            ("tool_search", ToolExposure.Direct, true));
        Names(["read", "codemode", "mcp__radius__a"], AllowedToolSelection.Create(["read", "codemode", "mcp__radius__*"]).SelectInitial(catalog), "pattern activation");
        Names(["read", "codemode"], AllowedToolSelection.Create(["read", "codemode"]).SelectInitial(catalog), "unnamed MCP tools stay inactive");
        Names(["read", "bash", "codemode", "tool_search"], AllowedToolSelection.Create(["+codemode", "-edit", "-write"]).SelectInitial(catalog).Where(name => !name.StartsWith("mcp__")), "modifier activation keeps default extensions");
        var named = AllowedToolSelection.Create(["read", "tool_search"]);
        Check(!named.IsActivatable("mcp__other__c", ToolExposure.Direct, true), "Unnamed direct MCP tools must not be declared.");
        Check(named.IsActivatable("mcp__radius__b", ToolExposure.Deferred, true) && !named.IsActivatable("mcp__radius__b", ToolExposure.Deferred, false),
            "Unnamed deferred MCP tools need tool_search.");
        Check(AllowedToolSelection.Create().IsActivatable("mcp__other__c", ToolExposure.Direct, false) &&
            named.IsActivatable("bash", ToolExposure.Direct, false), "Activation gate leaked beyond unnamed MCP tools.");
    }

    private sealed class NoTransport : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class NoAdapter(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Deny : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
    private static SessionRegisteredTool Tool(string name, ToolExposure exposure = ToolExposure.Direct, Func<ToolLoadout, ToolLoadoutChanges?>? prepare = null) =>
        new(JsonData.Parse(JsonSerializer.Serialize(new { name, description = name + " tool", parameters = new { type = "object" } })), new NoAdapter(name))
        { Exposure = exposure, IsExtension = name != "read", PrepareLoadout = prepare };
    private static SessionRuntimeRegistry Registry(ImmutableArray<SessionRegisteredTool> tools, SessionRuntimeRegistryOptions options) =>
        new([new(new ModelDescriptor("model", "openai-responses", "provider"), new NoTransport())], tools, new Deny(), options);

    private static void RegistryActivation()
    {
        var tools = ImmutableArray.Create(Tool("read"), Tool("mcp__srv__x"), Tool("mcp__srv__y", ToolExposure.Deferred), Tool("tool_search"));
        var policy = AllowedToolSelection.Create(["read", "tool_search"]);
        Names(["read", "mcp__srv__y", "tool_search"], Registry(tools, new() { LifetimeToolSelection = policy })
            .NormalizeActiveTools(["read", "mcp__srv__x", "mcp__srv__y", "tool_search"], default), "tool_search gate");
        Names(["read"], Registry(tools.RemoveAt(3), new() { LifetimeToolSelection = AllowedToolSelection.Create(["read"]) })
            .NormalizeActiveTools(["read", "mcp__srv__x", "mcp__srv__y"], default), "no tool_search");
        Names(["read", "mcp__srv__x"], Registry(tools, new() { LifetimeToolSelection = AllowedToolSelection.Create(["read", "mcp__srv__x"]) })
            .NormalizeActiveTools(["read", "mcp__srv__x"], default), "named MCP tool");
        var capped = Registry(tools, new() { LifetimeToolSelection = AllowedToolSelection.Create(["read", "mcp__srv__*"]) });
        Names(["read", "mcp__srv__x", "mcp__srv__y"], capped.RegisteredTools.Select(tool => tool.Adapter.Name), "mcp__ pattern cap filters the registry");
    }

    private static void ReloadAdditions()
    {
        var catalog = Catalog(("read", ToolExposure.Direct, false), ("bash", ToolExposure.Direct, false), ("edit", ToolExposure.Direct, false),
            ("grep", ToolExposure.Direct, false), ("ls", ToolExposure.Direct, false), ("hidden", ToolExposure.Hidden, false));
        Names(["read", "bash", "grep"], AllowedToolSelection.SelectReloaded(null, ["read", "bash"], catalog, ["read", "bash"], ["read", "bash", "grep", "missing", "hidden"]),
            "newly added defaultTools activate");
        Names(["read", "bash", "grep"], AllowedToolSelection.SelectReloaded(null, ["read", "bash", "grep"], catalog, ["read", "bash", "grep"], ["read"]),
            "removed defaultTools stay active");
        Names(["read"], AllowedToolSelection.SelectReloaded(null, ["read"], catalog, ["read", "bash"], ["read", "bash"]),
            "deliberately disabled tools stay disabled");
        Names(["read", "ls"], AllowedToolSelection.SelectReloaded(null, ["read"], catalog, null, ["read", "bash", "edit", "write", "ls"]),
            "absent setting compares with the built-in defaults");
        Names(["read", "edit"], AllowedToolSelection.SelectReloaded(null, ["read", "edit"], catalog, null, null), "unchanged defaults");
    }

    private static void ReloadPolicies()
    {
        var catalog = Catalog(("read", ToolExposure.Direct, false), ("bash", ToolExposure.Direct, false), ("write", ToolExposure.Direct, false),
            ("grep", ToolExposure.Direct, false), ("ext", ToolExposure.Direct, true), ("mcp__srv__a", ToolExposure.Direct, true));
        var removed = AllowedToolSelection.Create(["-write"]);
        Names(["read", "grep", "ext", "mcp__srv__a"], AllowedToolSelection.SelectReloaded(removed, ["read"], catalog, null, ["read", "bash", "edit", "write", "grep"]),
            "a -name modifier keeps the tool removed after reload");
        var allowlist = AllowedToolSelection.Create(["read", "bash", "grep"]);
        Names(["read", "bash", "grep"], AllowedToolSelection.SelectReloaded(allowlist, ["read"], catalog, ["read"], ["read", "write", "grep"]),
            "explicit allowlist: named tools, no defaultTools additions");
        var pattern = AllowedToolSelection.Create(["read", "mcp__*"]);
        Names(["read", "mcp__srv__a"], AllowedToolSelection.SelectReloaded(pattern, ["read"], catalog, null, null), "pattern matches reloaded tools");
        Names([], AllowedToolSelection.SelectReloaded(AllowedToolSelection.Create(noTools: NoToolsMode.All), ["read"], catalog, null, ["grep"]), "--no-tools");
    }
}
