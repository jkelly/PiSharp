using System.Collections.Immutable;

namespace PiSharp.Contracts;

/// <summary>Author-supplied immutable grouping metadata; never execution or permission authority.</summary>
public sealed record ToolNamespace(string Name, string? Description = null);

/// <summary>Immutable metadata only: these values carry no tool execution authority.</summary>
public sealed record ToolLoadoutTool(JsonData Declaration, ToolExposure Exposure)
{
    public ToolNamespace? Namespace { get; init; }
    public string Name => Declaration.Value.GetProperty("name").GetString()!;
    public string Description => Declaration.Value.GetProperty("description").GetString()!;
}

/// <summary>One original loadout shared by every active preparation callback, in active order.</summary>
public sealed record ToolLoadout(ImmutableArray<ToolLoadoutTool> Declared,
    ImmutableArray<ToolLoadoutTool> Callable, ImmutableArray<ToolLoadoutTool> Registered)
{
    public ToolExposure GetExposure(string name) => Registered.FirstOrDefault(tool => tool.Name == name)?.Exposure ?? ToolExposure.Direct;
    public ToolNamespace? GetNamespace(string name) => Registered.FirstOrDefault(tool => tool.Name == name)?.Namespace;
}

public sealed record ToolLoadoutChanges
{
    public ImmutableDictionary<string, string> Descriptions { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<string> HiddenDeclarations { get; init; } = [];
}

public sealed record ToolLoadoutDiagnostic(string ToolName, string Code);
