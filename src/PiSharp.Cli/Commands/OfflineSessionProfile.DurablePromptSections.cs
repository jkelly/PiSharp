using System.Collections.Immutable;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private McpServersPromptSource? mcpServersPromptSource;

    /// <summary>Pi 1.1.0 `mcp_servers`: every prompt start lists the servers as they are then; the section diff appends a
    /// change to the conversation instead of editing the recorded prompt. Configure once with the MCP runtime's source.</summary>
    internal void ConfigureMcpServersPromptSection(McpServersPromptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Interlocked.CompareExchange(ref mcpServersPromptSource, source, null) is not null)
            throw new InvalidOperationException("The MCP servers prompt section is already configured.");
    }

    /// <summary>The source an admitted MCP runtime publishes to: the configured one (created on first use), or the
    /// host's own when none was configured yet. A different second source is refused.</summary>
    private McpServersPromptSource AdmitMcpServersPromptSource(McpServersPromptSource? supplied)
    {
        var actual = Interlocked.CompareExchange(ref mcpServersPromptSource, supplied ?? new(), null) ?? Volatile.Read(ref mcpServersPromptSource)!;
        if (supplied is not null && !ReferenceEquals(actual, supplied))
            throw new InvalidOperationException("MCP runtime admission supplied a different servers prompt source.");
        return actual;
    }

    /// <summary>agent-session.ts _rebuildSystemPrompt: the tool list and rules use the promptSnippet and promptGuidelines of the
    /// registered definitions, so an extension tool's own (normalized) snippet and guidelines join the built-in ones, and a Pi
    /// extension tool that replaces a built-in name replaces its entries. Read at every prompt start, so tools registered later count.</summary>
    private OriginalSystemPromptSnapshot WithExtensionToolPrompts(OriginalSystemPromptSnapshot snapshot, AgentSessionAttachment? attached)
    {
        if (_extension is null) return snapshot;
        SessionRuntimeRegistry? registry;
        try { registry = attached?.Session.CaptureToolCatalogRegistry() ?? _startupRegistry; }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { registry = _startupRegistry; }
        if (registry is null) return snapshot;
        var pi = _extension?.Pi?.Extensions
            .SelectMany(extension => (extension.Descriptor["tools"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
            .Select(tool => tool["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        return WithExtensionToolPrompts(snapshot, registry.RegisteredTools, pi);
    }

    private static OriginalSystemPromptSnapshot WithExtensionToolPrompts(OriginalSystemPromptSnapshot snapshot, IEnumerable<SessionRegisteredTool> tools,
        HashSet<string> pi)
    {
        var snippets = snapshot.Input.ToolSnippets.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var guidelines = snapshot.Input.ToolGuidelines.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var changed = false;
        foreach (var tool in tools.Where(tool => tool.IsExtension))
        {
            var name = tool.Declaration.Value.GetProperty("name").GetString()!;
            var replaces = pi.Contains(name);
            var snippet = tool.PromptSnippet is { } text ? System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim() : "";
            if (snippet.Length > 0) { snippets[name] = snippet; changed = true; }
            else if (replaces && snippets.Remove(name)) changed = true;
            var rules = (tool.PromptGuidelines.IsDefault ? [] : tool.PromptGuidelines).Select(rule => rule.Trim()).Where(rule => rule.Length > 0)
                .Distinct(StringComparer.Ordinal).ToImmutableArray();
            if (rules.Length > 0) { guidelines[name] = rules; changed = true; }
            else if (replaces && guidelines.Remove(name)) changed = true;
        }
        if (!changed) return snapshot;
        return snapshot with { Input = snapshot.Input with
        {
            ToolSnippets = [.. snippets.OrderBy(row => row.Key, StringComparer.Ordinal)],
            ToolGuidelines = [.. guidelines.OrderBy(row => row.Key, StringComparer.Ordinal)]
        } };
    }

    internal SessionRuntimeRegistry DecorateDurablePromptRegistry(SessionRuntimeRegistry registry)
        => registry.UsesPromptSectionPreparation(PrepareDurablePromptSections) ? registry :
            registry.WithPromptSectionPreparation(PrepareDurablePromptSections);

    private SessionPromptSectionPreparation? PrepareDurablePromptSections(SessionPromptSectionRequest request)
    {
        var owner = Sessions;
        var expected = owner?.Current;
        OriginalPromptSlot? slot = null;
        OriginalSystemPromptSnapshot snapshot;
        lock (viewGate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(OfflineSessionProfile));
            if (expected is null) snapshot = startupOriginalPrompt;
            else
            {
                if (!runtimeViews.TryGetValue(expected, out var view) || !originalPromptViews.TryGetValue(view, out slot) ||
                    !originalPromptAttachments.TryGetValue(expected, out var attached) || !ReferenceEquals(slot, attached) ||
                    Volatile.Read(ref slot.State) != 2) throw new InvalidOperationException("Exact acknowledged prompt source required.");
                snapshot = slot.Snapshot;
            }
        }
        if (snapshot.NativeLiteralBaseline) return null;
        var selected = snapshot with { SelectedTools = request.SelectedTools,
            Input = snapshot.Input with { SelectedTools = request.SelectedTools, HiddenTools = request.HiddenTools } };
        selected = WithExtensionToolPrompts(selected, expected);
        if (Volatile.Read(ref mcpServersPromptSource) is { } servers)
            selected = OriginalSystemPromptBuilder.WithSection(selected, McpServersSection.Name, servers.Render(expected?.Generation));
        return new(slot is null ? snapshot : slot, OriginalSystemPromptBuilder.Sections(selected), () =>
        {
            if (!ReferenceEquals(Sessions, owner) || !ReferenceEquals(owner?.Current, expected))
                throw new InvalidOperationException("Prompt source crossed attachment publication.");
            lock (viewGate)
            {
                if (_disposal is not null) throw new ObjectDisposedException(nameof(OfflineSessionProfile));
                if (expected is null)
                { if (!ReferenceEquals(startupOriginalPrompt, snapshot)) throw new InvalidOperationException("Startup prompt source changed."); }
                else if (!runtimeViews.TryGetValue(expected, out var view) || !originalPromptViews.TryGetValue(view, out var actual) ||
                    !ReferenceEquals(actual, slot) || Volatile.Read(ref actual.State) != 2)
                    throw new InvalidOperationException("Prompt view changed before durable write.");
            }
        });
    }
}
