// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/tool-search/tool.ts (searchAndLoad,
// createToolSearchToolDefinition) and packages/coding-agent/src/extensions/tool-search/index.ts.
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Discovery;

namespace PiSharp.Cli.Mcp;

/// <summary>The built-in `tool_search` of production sessions. It searches the session's registered tools that are not declared
/// to the model (`codemode` and `deferred` exposure, see <see cref="ToolSearch.IsSearchable"/>) and are not active yet, ranks them
/// with <see cref="Bm25Ranker"/>, and activates the matches through the session's tool selection, so the next model call
/// declares them. Activation is recorded in the transcript like any other tool change. Registered with every session the tool
/// selection keeps it in, as tool-search/index.ts registers it, inactive (<paramref name="defaultActive"/> false) so that
/// `--tools tool_search` and `defaultTools` select it; active by default when an admitted MCP server has `deferred` tools, as
/// the original's MCP extension activates it for those servers (see <see cref="McpSessionHost"/>).</summary>
internal static class McpToolSearch
{
    internal const string Name = ToolSearch.Name, RegistrationId = "tool-search";
    private static readonly ConditionalWeakTable<PersistentAgentSession, object> Gates = [];

    /// <summary>A fresh definition for one generation; each binds to exactly one attachment. With
    /// <paramref name="waitForServers"/> (index.ts tool_call: tool_search reaches every server) a search first waits for the
    /// servers still connecting (their tools are registered once connected, also during the run).</summary>
    internal static McpDiscoveryExecutableDefinition Create(bool defaultActive = true, Func<CancellationToken, Task>? waitForServers = null) =>
        McpDiscoveryExecutableDefinition.CreateToolSearch(RegistrationId, ToolSearch.Description,
            async (query, limit, attachment, invocation, token) =>
            {
                if (waitForServers is not null) await waitForServers(token).ConfigureAwait(false);
                return await ExecuteAsync(query, limit, attachment, invocation, token).ConfigureAwait(false);
            }, null,
            descriptor => descriptor with { DefaultActive = defaultActive });

    /// <summary>The tool's arguments as its schema admits them: a string query and an optional number limit.</summary>
    internal static bool ValidArguments(JsonData arguments) =>
        arguments.Value.ValueKind == JsonValueKind.Object && arguments.Value.TryGetProperty("query", out var query) &&
        query.ValueKind == JsonValueKind.String &&
        (!arguments.Value.TryGetProperty("limit", out var limit) || limit.ValueKind == JsonValueKind.Number && double.IsFinite(limit.GetDouble()));

    private static ValueTask<JsonData> ExecuteAsync(string query, double? limit, AgentSessionAttachment attachment,
        IExtensionToolInvocationContext invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var max = ToolSearch.ValidateInput(query, limit);
        var loaded = SearchAndLoad(attachment.Session, query, max, token);
        var result = new
        {
            content = new[] { new { type = "text", text = ToolSearch.FormatResult(loaded) } },
            details = new { loaded = loaded.Select(tool => tool.Name).ToArray() }
        };
        return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(result)));
    }

    /// <summary>Rank the searchable tools that are not active yet and activate the matches, so the next model call declares them.</summary>
    internal static IReadOnlyList<ToolSearchResultTool> SearchAndLoad(PersistentAgentSession session, string query, int limit, CancellationToken token)
    {
        // One search at a time per session, so parallel searches each add to the selection the other produced.
        lock (Gates.GetValue(session, _ => new object()))
        {
            var active = session.GetToolActivationSelection().Names;
            var candidates = session.CaptureToolCatalogRegistry().RegisteredTools
                .Where(tool => ToolSearch.IsSearchable(tool.Exposure) && !active.Contains(tool.Adapter.Name, StringComparer.Ordinal)).ToArray();
            var documents = candidates.Select(tool => ToolSearch.CreateDocument(tool.Adapter.Name, Description(tool),
                tool.Declaration.Value.TryGetProperty("parameters", out var parameters) ? parameters : default, tool.Namespace)).ToArray();
            var matches = new Bm25Ranker().Rank(query, documents, limit);
            token.ThrowIfCancellationRequested();
            if (matches.Count > 0) session.ScheduleToolActivation([.. active, .. matches.Select(match => match.Name)], token);
            return [.. matches.Select(match => new ToolSearchResultTool(match.Name,
                Description(candidates.First(tool => tool.Adapter.Name == match.Name))))];
        }
    }

    private static string Description(SessionRegisteredTool tool) =>
        tool.Declaration.Value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
            ? description.GetString()! : "";
}
