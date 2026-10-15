// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/extensions/types.ts (ToolDefinition.reachesTools).
using System.Collections.Immutable;

namespace PiSharp.Contracts;

/// <summary>What <c>ToolDefinition.reachesTools(input)</c> returns for tools that reach other tools (<c>codemode</c> calls them,
/// <c>tool_search</c> loads them): <see cref="All"/>, or the words of the input that may name them. A word refers to a tool or
/// namespace whose name it contains, so words may over-match. Extensions that register tools late, such as MCP servers that are
/// still connecting, use it to finish registering those tools before the call runs.</summary>
public sealed class ToolReach
{
    private ToolReach(bool all, ImmutableArray<string> words) { IsAll = all; Words = words; }

    /// <summary><c>"all"</c>: the call may reach any tool.</summary>
    public static ToolReach All { get; } = new(true, []);

    /// <summary>The words of the input that may name the tools the call reaches.</summary>
    public static ToolReach Of(ImmutableArray<string> words) => new(false, words.IsDefault ? [] : words);

    public bool IsAll { get; }

    /// <summary>Empty for <see cref="All"/>.</summary>
    public ImmutableArray<string> Words { get; }

    /// <summary>Whether the call may reach a tool or namespace named <paramref name="name"/>: it reaches every tool, or one of
    /// its words contains the name.</summary>
    public bool Reaches(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return IsAll || Words.Any(word => word.Contains(name, StringComparison.Ordinal));
    }
}
