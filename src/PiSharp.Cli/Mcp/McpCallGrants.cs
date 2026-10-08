// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts and docs/mcp.md
// (Permissions): a server configured in mcp.json is trusted; its calls run through the tool pipeline with no approval step of
// their own, so extension tool_call/tool_result handlers are the only gate.
using System.Collections.Immutable;
using System.Globalization;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>Final-action grants of one acquired session generation's MCP integration. A server is admitted when its registry
/// scope is created for this generation (pre-open servers once they connected, background servers when they bind); its tools
/// are granted by exact tool name and extension target (<c>owner/ownerGeneration/registrationId</c>, where an MCP tool's
/// registration id is its name). Host tools such as the built-in tool_search are granted by exact name and target. Servers
/// that were skipped never get a scope, so nothing of theirs is granted.</summary>
public sealed class McpCallGrants
{
    private ImmutableHashSet<string> servers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private ImmutableHashSet<(string Tool, string Target)> exact = [];

    internal void AdmitServer(RegistrationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var prefix = scope.OwnerId + "/" + scope.OwnerGeneration.ToString(CultureInfo.InvariantCulture) + "/";
        ImmutableInterlocked.Update(ref servers, current => current.Add(prefix));
    }

    internal void AdmitExact(string tool, string target) => ImmutableInterlocked.Update(ref exact, current => current.Add((tool, target)));

    /// <summary>Whether the exact invoke action of this tool and target belongs to an admitted server or host tool.</summary>
    public bool Allows(string tool, string target)
    {
        if (exact.Contains((tool, target))) return true;
        if (!tool.StartsWith("mcp__", StringComparison.Ordinal) || !target.EndsWith("/" + tool, StringComparison.Ordinal)) return false;
        return servers.Contains(target[..^tool.Length]);
    }
}
