// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts renderServersSection,
// packages/coding-agent/src/core/system-prompt.ts buildSystemPromptSections/diffSystemPromptSections.
using System.Collections.Immutable;

namespace PiSharp.Extensions.Mcp.Configuration;

/// <summary>What the `mcp_servers` section needs of a server: its entry and, once connected, its instructions.</summary>
public sealed record McpServerListing(McpServerEntry Entry, string? Instructions = null);

/// <summary>A change to the section the model has: <see cref="Value"/> is the new tagged section text, or null to remove it.</summary>
public sealed record McpServersSectionPatch(string Name, string? Value);

/// <summary>The `mcp_servers` system prompt section: every enabled server with codemode or deferred tools, one line
/// each, with how its tools are reached and a one-line summary. The model learns of these servers from it, since
/// neither codemode nor tool_search lists them. It is rendered at every prompt start from the configuration as it is
/// then; a changed section is appended to the conversation as a sections patch, never edited in place.</summary>
public static class McpServersSection
{
    public const string Name = "mcp_servers";
    /// <summary>Characters of a server description in the section, as Codex allows for deferred namespaces.</summary>
    public const int MaxServerDescriptionChars = 250;
    /// <summary>Characters of the whole section. Descriptions shrink to fit; when the server lines alone do not fit,
    /// the last servers are left out and counted in a closing line.</summary>
    public const int MaxSectionChars = 4096;

    /// <summary>The untagged section content, or null when no enabled server has codemode or deferred tools.</summary>
    public static string? Render(IEnumerable<McpServerListing> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var listed = servers.Where(server => server.Entry.Config.Enabled && McpConfigurationReader.HasIndirectTools(server.Entry.Config))
            .OrderBy(server => server.Entry.Name, StringComparer.InvariantCulture).ToArray();
        if (listed.Length == 0) return null;
        var reaches = listed.Select(server =>
            McpConfigurationReader.ConfiguredExposures(server.Entry.Config).Contains(McpExposure.Codemode) ? "codemode" : "tool_search").ToArray();
        var intro = "MCP servers whose tools are not declared to you.";
        if (reaches.Contains("codemode")) intro += " Call the tools of `codemode` servers from codemode scripts.";
        if (reaches.Contains("tool_search")) intro += " Load the tools of `tool_search` servers with `tool_search`.";
        var heads = listed.Select((server, index) => $"- {McpCatalogPlanner.Namespace(server.Entry.Name)} ({reaches[index]})").ToArray();
        static string[] Omitted(int count) =>
            count > 0 ? [$"- \u2026 {count} more server{(count == 1 ? "" : "s")}; find their tools with searchTools()"] : [];
        // Characters of the intro, the first `kept` server lines without descriptions, and the omission line.
        int Size(int kept) => string.Join("\n", heads.Take(kept).Prepend(intro).Concat(Omitted(listed.Length - kept))).Length;
        var kept = listed.Length;
        while (kept > 0 && Size(kept) > MaxSectionChars) kept--;
        // Each description also takes a ": " separator.
        var perServer = kept == 0 ? 0 : Math.Min(MaxServerDescriptionChars, (int)Math.Floor((MaxSectionChars - Size(kept)) / (double)kept) - 2);
        var lines = listed.Take(kept).Select((server, index) =>
        {
            var summary = perServer > 0 ? Truncate(Summary(server), perServer) : "";
            return summary.Length > 0 ? $"{heads[index]}: {summary}" : heads[index];
        });
        return string.Join("\n", lines.Prepend(intro).Concat(Omitted(listed.Length - kept)));
    }

    /// <summary>The section as the system prompt carries it: XML-tagged with its name.</summary>
    public static string Tag(string content) { ArgumentNullException.ThrowIfNull(content); return $"<{Name}>\n{content}\n</{Name}>"; }

    /// <summary>The sections patch to append when the rendered content differs from the section the model currently has
    /// (replayed from the transcript); null when nothing changed. Removal is a null value.</summary>
    public static McpServersSectionPatch? Diff(string? current, string? rendered)
    {
        var desired = rendered is null ? null : Tag(rendered);
        return string.Equals(current, desired, StringComparison.Ordinal) ? null : new(Name, desired);
    }

    /// <summary>First line of the configured description, or of the server instructions once connected.</summary>
    private static string Summary(McpServerListing server)
    {
        var configured = server.Entry.Config.Description is { } text ? McpJson.JsTrim(text) : "";
        var source = configured.Length > 0 ? configured : server.Instructions ?? "";
        return McpJson.JsTrim(source.Split('\n', 2)[0]);
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        return max <= 1 ? "" : text[..(max - 1)].TrimEnd(McpJson.JsWhiteSpace) + "\u2026";
    }

    /// <summary>Servers whose tools the first prompt waits for: enabled servers with `direct` tools. Others connect in
    /// the background and are waited for only when a script, search or resource tool needs them.</summary>
    public static ImmutableArray<McpServerEntry> FirstPromptWaitsFor(IEnumerable<McpServerEntry> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return servers.Where(server => server.Config.Enabled && McpConfigurationReader.HasDirectTools(server.Config)).ToImmutableArray();
    }
}
