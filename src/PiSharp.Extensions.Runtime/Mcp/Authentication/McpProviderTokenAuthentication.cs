// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/runtime.ts (auth.provider).
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>`"auth": {"provider": "&lt;p&gt;"}`: the server receives the current login token of a provider instead of
/// running OAuth. Only global and extension configuration can carry it (the reader rejects it in project files) and
/// it requires https except on loopback hosts, since the credential is sent to the server URL.</summary>
public static class McpProviderTokenAuthentication
{
    /// <summary>Reads the token on every request, so the provider's refreshes apply; MCP stores no copy. A 401 is not
    /// retried here: the user signs in to the provider again.</summary>
    public static McpAdmittedHttpAuthentication Create(McpServerEntry entry, Func<string, CancellationToken, ValueTask<string?>> providerToken)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(providerToken);
        if (entry.Scope == McpConfigurationScope.Project) throw new InvalidOperationException("Project MCP configuration cannot use provider authentication.");
        var provider = entry.Config.AuthProvider ?? throw new ArgumentException("The MCP server does not configure auth.provider.", nameof(entry));
        if (entry.Config.Transport != McpTransportKind.Http) throw new ArgumentException("Provider authentication requires an HTTP server.", nameof(entry));
        if (providerToken.GetInvocationList().Length != 1) throw new ArgumentException("One admitted token callback required.", nameof(providerToken));
        return new(token => providerToken(provider, token));
    }

    /// <summary>The message shown when a server requires sign-in: `/login &lt;provider&gt;` for provider auth, else `/mcp`.</summary>
    public static string SignInRequiredMessage(McpServerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var provider = entry.Config.Transport == McpTransportKind.Http ? entry.Config.AuthProvider : null;
        return $"MCP server \"{entry.Name}\" requires sign-in. Run {(provider is not null ? "/login " + provider : "/mcp")} to sign in.";
    }
}
