// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts (ToolNamespace, ToolLoadout).
using System.Collections.Immutable;

namespace PiSharp.Contracts;

/// <summary>Author-supplied immutable grouping metadata; never execution or permission authority.
/// Description is the short summary listed with the group.</summary>
public sealed record ToolNamespace(string Name, string? Description = null)
{
    /// <summary>Longer usage guidance, such as MCP server instructions. Not part of tool listings; tools that
    /// describe the namespace on request return it.</summary>
    public string? Instructions { get; init; }
}

/// <summary>Immutable metadata only: these values carry no tool execution authority.</summary>
public sealed record ToolLoadoutTool(JsonData Declaration, ToolExposure Exposure)
{
    public ToolNamespace? Namespace { get; init; }
    /// <summary>The tool's promptGuidelines. Hidden declarations leave them out of the system prompt.</summary>
    public ImmutableArray<string> PromptGuidelines { get; init; } = [];
    /// <summary>The tool's outputSchema (its structuredContent shape), when it declares one.</summary>
    public JsonData? OutputSchema { get; init; }
    public string Name => Declaration.Value.GetProperty("name").GetString()!;
    public string Description => Declaration.Value.GetProperty("description").GetString()!;
}

/// <summary>One original loadout shared by every active preparation callback, in active order.</summary>
public sealed record ToolLoadout(ImmutableArray<ToolLoadoutTool> Declared,
    ImmutableArray<ToolLoadoutTool> Callable, ImmutableArray<ToolLoadoutTool> Registered)
{
    public ToolExposure GetExposure(string name) => Registered.FirstOrDefault(tool => tool.Name == name)?.Exposure ?? ToolExposure.Direct;
    public ToolNamespace? GetNamespace(string name) => Registered.FirstOrDefault(tool => tool.Name == name)?.Namespace;
    /// <summary>A tool's promptGuidelines; empty for an unknown tool or a tool without guidelines.</summary>
    public ImmutableArray<string> GetPromptGuidelines(string name) =>
        Registered.FirstOrDefault(tool => tool.Name == name)?.PromptGuidelines is { IsDefault: false } guidelines ? guidelines : [];
}

public sealed record ToolLoadoutChanges
{
    public ImmutableDictionary<string, string> Descriptions { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<string> HiddenDeclarations { get; init; } = [];
}

public sealed record ToolLoadoutDiagnostic(string ToolName, string Code);
