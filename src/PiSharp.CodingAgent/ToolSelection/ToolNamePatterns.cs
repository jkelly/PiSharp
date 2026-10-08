// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/settings-manager.ts and packages/coding-agent/src/core/mcp-servers.ts.
using System.Collections.Immutable;

namespace PiSharp.CodingAgent.ToolSelection;

/// <summary>Tool selection entries: exact names, <c>*</c> patterns and <c>+name</c>/<c>-name</c> modifiers.</summary>
public static class ToolNamePatterns
{
    /// <summary>Tools enabled at startup when <c>defaultTools</c> does not change them.</summary>
    public static ImmutableArray<string> DefaultToolNames { get; } = ["read", "bash", "edit", "write"];
    private static readonly ImmutableHashSet<string> McpResourceTools =
        ImmutableHashSet.Create(StringComparer.Ordinal, "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource");

    /// <summary>Whether a tool selection entry is a <c>+name</c> or <c>-name</c> modifier.</summary>
    public static bool IsModifier(string? entry) => entry is not null && (entry.StartsWith('+') || entry.StartsWith('-'));

    /// <summary>The problem with a <c>--tools</c> list, or null. It is either an allowlist of plain names and patterns
    /// or a list of only modifiers with exact names.</summary>
    public static string? GetToolListError(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var modifiers = entries.Where(IsModifier).ToArray();
        if (modifiers.Length == 0) return null;
        if (modifiers.Length < entries.Count) return "tool names cannot be mixed with +name or -name entries";
        var pattern = modifiers.FirstOrDefault(entry => entry.Contains('*'));
        return pattern is null ? null : $"+name and -name entries take exact tool names, not patterns: {pattern}";
    }

    /// <summary>Apply <c>+name</c> (add) and <c>-name</c> (remove) entries to <paramref name="baseline"/> in order; other entries are ignored.</summary>
    public static ImmutableArray<string> ApplyModifiers(IEnumerable<string> baseline, IEnumerable<string> entries)
    {
        var tools = baseline.ToList();
        foreach (var entry in entries)
        {
            if (!IsModifier(entry)) continue;
            var name = entry[1..]; var index = tools.IndexOf(name);
            if (entry[0] == '+' && index < 0 && name.Length > 0) tools.Add(name);
            else if (entry[0] == '-' && index >= 0) tools.RemoveAt(index);
        }
        return tools.ToImmutableArray();
    }

    /// <summary>Whether a tool name matches any entry, each an exact name or a pattern where <c>*</c> matches any characters.</summary>
    public static Func<string, bool> CreateMatcher(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var names = new HashSet<string>(StringComparer.Ordinal); var patterns = new List<string[]>();
        foreach (var entry in entries) if (entry.Contains('*')) patterns.Add(entry.Split('*')); else names.Add(entry);
        return name => names.Contains(name) || patterns.Any(parts => Matches(name, parts));
    }

    /// <summary>Whether a tool comes from MCP: a server tool (<c>mcp__&lt;server&gt;__&lt;tool&gt;</c>) or a resource tool.</summary>
    public static bool IsMcpToolName(string name) =>
        name.StartsWith("mcp__", StringComparison.Ordinal) || McpResourceTools.Contains(name);

    // Anchored glob with only `*`, equivalent to upstream's escaped ^...$ regular expression.
    private static bool Matches(string name, string[] parts)
    {
        if (!name.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        var position = parts[0].Length; var last = parts[^1];
        if (name.Length - position < last.Length || !name.EndsWith(last, StringComparison.Ordinal)) return false;
        var end = name.Length - last.Length;
        for (var index = 1; index < parts.Length - 1; index++)
        {
            var found = name.IndexOf(parts[index], position, end - position, StringComparison.Ordinal);
            if (found < 0) return false;
            position = found + parts[index].Length;
        }
        return position <= end;
    }
}
