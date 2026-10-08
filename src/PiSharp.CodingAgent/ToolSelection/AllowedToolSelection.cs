// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts and packages/coding-agent/src/core/agent-session.ts.
using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.ToolSelection;

public enum NoToolsMode { None, Builtin, All }
public sealed record ToolSelectionDescriptor(string Name, ToolExposure Exposure, bool DefaultActive, bool IsExtension);

/// <summary>Immutable pinned allow/exclude policy. Filtering never acquires a tool or grants execution authority.
/// Entries are exact names or <c>*</c> patterns. A tools list of only <c>+name</c>/<c>-name</c> entries changes the
/// default selection instead of capping the registry.</summary>
public sealed class AllowedToolSelection
{
    /// <summary>Allowlist entries (names or patterns); null when the registry is not capped.</summary>
    public ImmutableHashSet<string>? AllowedNames { get; }
    /// <summary>Denylist entries (names or patterns).</summary>
    public ImmutableHashSet<string> ExcludedNames { get; }
    public ImmutableArray<string> InitialNames { get; }
    public bool IncludeDefaultExtensions => AllowedNames is null;
    /// <summary>Whether the initial tools come from <c>defaultTools</c>, so reload activates tools newly added to it.</summary>
    public bool UsesDefaultTools { get; }
    /// <summary><c>+name</c>/<c>-name</c> entries from the tools list, reapplied to a reloaded <c>defaultTools</c>.</summary>
    public ImmutableArray<string> DefaultToolModifiers { get; }
    private readonly Func<string, bool>? allowed;
    private readonly Func<string, bool> excludedMatch;
    // An empty allowlist or one naming mcp__ entries filters MCP tools; otherwise they stay registered unnamed.
    private readonly bool allowlistFiltersMcp;
    private AllowedToolSelection(ImmutableArray<string>? tools, ImmutableArray<string> excluded, NoToolsMode mode,
        ImmutableArray<string>? configuredDefaults, ImmutableArray<string> defaults)
    {
        var defaultNames = mode != NoToolsMode.None ? [] : configuredDefaults ?? defaults;
        var modifiers = tools is { } list && list.Any(ToolNamePatterns.IsModifier) ? list : (ImmutableArray<string>?)null;
        var selected = modifiers is { } entries ? ToolNamePatterns.ApplyModifiers(defaultNames, entries) : tools;
        var allowlist = modifiers is not null ? mode == NoToolsMode.All ? selected : null : tools ?? (mode == NoToolsMode.All ? [] : null);
        AllowedNames = allowlist?.ToImmutableHashSet(StringComparer.Ordinal);
        ExcludedNames = excluded.ToImmutableHashSet(StringComparer.Ordinal);
        if (allowlist is { } names)
        {
            allowed = ToolNamePatterns.CreateMatcher(names);
            allowlistFiltersMcp = names.Length == 0 || names.Any(name => name.StartsWith("mcp__", StringComparison.Ordinal));
        }
        excludedMatch = ToolNamePatterns.CreateMatcher(excluded);
        UsesDefaultTools = (tools is null || modifiers is not null) && mode == NoToolsMode.None;
        DefaultToolModifiers = modifiers ?? [];
        InitialNames = (selected ?? defaultNames).Where(IsAllowed).Distinct(StringComparer.Ordinal).ToImmutableArray();
    }
    public static AllowedToolSelection Create(ImmutableArray<string>? tools = null, ImmutableArray<string> excluded = default,
        NoToolsMode noTools = NoToolsMode.None, ImmutableArray<string>? configuredDefaults = null, ImmutableArray<string> defaults = default)
    {
        if (!Enum.IsDefined(noTools) || tools is { IsDefault: true } || configuredDefaults is { IsDefault: true })
            throw new ArgumentException("Invalid tool selection.");
        excluded = excluded.IsDefault ? [] : excluded;
        defaults = defaults.IsDefault ? ToolNamePatterns.DefaultToolNames : defaults;
        foreach (var names in new[] { tools ?? [], excluded, configuredDefaults ?? [], defaults })
            if (names.Any(name => name is null || name.Length > 128 || name.Any(char.IsControl)))
                throw new ArgumentException("Invalid tool selection name.");
        if (tools is { } list && ToolNamePatterns.GetToolListError(list) is { } error)
            throw new ArgumentException($"Invalid tools option: {error}");
        return new(tools, excluded, noTools, configuredDefaults, defaults);
    }
    /// <summary>Whether tools and excluded entries keep the tool registered. MCP tools stay registered unless the allowlist filters them.</summary>
    public bool IsAllowed(string name)
    {
        if (excludedMatch(name)) return false;
        if (allowed is null || allowed(name)) return true;
        return !allowlistFiltersMcp && ToolNamePatterns.IsMcpToolName(name);
    }
    /// <summary>Whether the allowlist names or matches the tool, which activates it at startup.</summary>
    public bool IsNamed(string name) => allowed is not null && allowed(name) && !excludedMatch(name);
    /// <summary>Whether the tool may be declared. MCP tools kept without being named are only for codemode and tool_search:
    /// they may be declared only when tool_search can load them (non-direct exposure and tool_search registered).</summary>
    public bool IsActivatable(string name, ToolExposure exposure, bool toolSearchRegistered) =>
        allowed is null || allowed(name) || !ToolNamePatterns.IsMcpToolName(name) || exposure != ToolExposure.Direct && toolSearchRegistered;
    /// <summary>The <c>defaultTools</c> selection this policy started from, for a reload of that setting.</summary>
    public ImmutableArray<string> ResolveDefaultTools(ImmutableArray<string>? configuredDefaults) => UsesDefaultTools
        ? ToolNamePatterns.ApplyModifiers(configuredDefaults ?? ToolNamePatterns.DefaultToolNames, DefaultToolModifiers) : [];
    /// <summary>Active tools after <c>/reload</c>: the previous selection plus tools newly added to <c>defaultTools</c>
    /// (removed ones stay active), then named tools or default-active extension tools, each still registered,
    /// declarable, allowed and activatable. A null policy is the default selection without CLI flags.</summary>
    public static ImmutableArray<string> SelectReloaded(AllowedToolSelection? policy, ImmutableArray<string> previous,
        ImmutableArray<ToolSelectionDescriptor> catalog, ImmutableArray<string>? previousDefaults, ImmutableArray<string>? reloadedDefaults)
    {
        ImmutableArray<string> Defaults(ImmutableArray<string>? configured) =>
            policy?.ResolveDefaultTools(configured) ?? configured ?? ToolNamePatterns.DefaultToolNames;
        var before = Defaults(previousDefaults).ToHashSet(StringComparer.Ordinal);
        var registry = new Dictionary<string, ToolSelectionDescriptor>(StringComparer.Ordinal);
        foreach (var tool in catalog) registry.TryAdd(tool.Name, tool);
        var toolSearch = registry.ContainsKey("tool_search");
        bool Selectable(string name) => registry.TryGetValue(name, out var tool) && tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly &&
            (policy is null || policy.IsAllowed(name) && policy.IsActivatable(name, tool.Exposure, toolSearch));
        var selected = new List<string>();
        foreach (var name in previous.Concat(Defaults(reloadedDefaults).Where(name => !before.Contains(name))))
            if (Selectable(name) && !selected.Contains(name, StringComparer.Ordinal)) selected.Add(name);
        foreach (var tool in catalog)
            if ((policy?.AllowedNames is not null ? policy.IsNamed(tool.Name) : tool.IsExtension && tool.DefaultActive) &&
                Selectable(tool.Name) && !selected.Contains(tool.Name, StringComparer.Ordinal)) selected.Add(tool.Name);
        return selected.ToImmutableArray();
    }
    public ImmutableArray<T> FilterCatalog<T>(ImmutableArray<T> catalog, Func<T, string> name) =>
        catalog.Where(item => IsAllowed(name(item))).ToImmutableArray();
    public ImmutableArray<string> SelectInitial(ImmutableArray<ToolSelectionDescriptor> catalog)
    {
        static bool Declarable(ToolSelectionDescriptor tool) => tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly;
        var permitted = FilterCatalog(catalog, tool => tool.Name);
        var toolSearch = permitted.Any(tool => tool.Name == "tool_search");
        bool Selectable(ToolSelectionDescriptor tool) => Declarable(tool) && IsActivatable(tool.Name, tool.Exposure, toolSearch);
        var selected = InitialNames.Where(name => permitted.Any(tool => tool.Name == name && Selectable(tool))).ToList();
        // Naming or matching a tool activates it even when it is not active by default.
        var added = AllowedNames is null
            ? permitted.Where(tool => tool.IsExtension && tool.DefaultActive && Selectable(tool))
            : permitted.Where(tool => IsNamed(tool.Name) && Selectable(tool));
        foreach (var tool in added)
            if (!selected.Contains(tool.Name, StringComparer.Ordinal)) selected.Add(tool.Name);
        return selected.ToImmutableArray();
    }
}
