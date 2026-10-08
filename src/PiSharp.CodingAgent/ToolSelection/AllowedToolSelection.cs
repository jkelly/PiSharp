using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.ToolSelection;

public enum NoToolsMode { None, Builtin, All }
public sealed record ToolSelectionDescriptor(string Name, ToolExposure Exposure, bool DefaultActive, bool IsExtension);

/// <summary>Immutable pinned allow/exclude policy. Filtering never acquires a tool or grants execution authority.</summary>
public sealed class AllowedToolSelection
{
    public ImmutableHashSet<string>? AllowedNames { get; }
    public ImmutableHashSet<string> ExcludedNames { get; }
    public ImmutableArray<string> InitialNames { get; }
    public bool IncludeDefaultExtensions => AllowedNames is null;
    private AllowedToolSelection(ImmutableArray<string>? tools, ImmutableArray<string> excluded, NoToolsMode mode,
        ImmutableArray<string>? configuredDefaults, ImmutableArray<string> defaults)
    {
        AllowedNames = tools?.ToImmutableHashSet(StringComparer.Ordinal) ??
            (mode == NoToolsMode.All ? ImmutableHashSet.Create<string>(StringComparer.Ordinal) : null);
        ExcludedNames = excluded.ToImmutableHashSet(StringComparer.Ordinal);
        InitialNames = (tools ?? (mode != NoToolsMode.None ? [] : configuredDefaults ?? defaults))
            .Where(IsAllowed).Distinct(StringComparer.Ordinal).ToImmutableArray();
    }
    public static AllowedToolSelection Create(ImmutableArray<string>? tools = null, ImmutableArray<string> excluded = default,
        NoToolsMode noTools = NoToolsMode.None, ImmutableArray<string>? configuredDefaults = null, ImmutableArray<string> defaults = default)
    {
        if (!Enum.IsDefined(noTools) || tools is { IsDefault: true } || configuredDefaults is { IsDefault: true })
            throw new ArgumentException("Invalid tool selection.");
        excluded = excluded.IsDefault ? [] : excluded;
        defaults = defaults.IsDefault ? ["read", "bash", "edit", "write"] : defaults;
        foreach (var names in new[] { tools ?? [], excluded, configuredDefaults ?? [], defaults })
            if (names.Any(name => name is null || name.Length > 128 || name.Any(char.IsControl)))
                throw new ArgumentException("Invalid tool selection name.");
        return new(tools, excluded, noTools, configuredDefaults, defaults);
    }
    public bool IsAllowed(string name) => (AllowedNames is null || AllowedNames.Contains(name)) && !ExcludedNames.Contains(name);
    public ImmutableArray<T> FilterCatalog<T>(ImmutableArray<T> catalog, Func<T, string> name) =>
        catalog.Where(item => IsAllowed(name(item))).ToImmutableArray();
    public ImmutableArray<string> SelectInitial(ImmutableArray<ToolSelectionDescriptor> catalog)
    {
        static bool Declarable(ToolSelectionDescriptor tool) => tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly;
        var permitted = FilterCatalog(catalog, tool => tool.Name);
        var selected = InitialNames.Where(name => permitted.Any(tool => tool.Name == name && Declarable(tool))).ToList();
        if (IncludeDefaultExtensions)
            foreach (var tool in permitted.Where(tool => tool.IsExtension && Declarable(tool) && tool.DefaultActive))
                if (!selected.Contains(tool.Name, StringComparer.Ordinal)) selected.Add(tool.Name);
        return selected.ToImmutableArray();
    }
}
