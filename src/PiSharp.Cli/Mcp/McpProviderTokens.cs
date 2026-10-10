// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts (providerToken:
// modelRegistry.getApiKeyForProvider) and packages/coding-agent/src/extensions/mcp/runtime.ts (auth.provider: read on every request).
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;

namespace PiSharp.Cli.Mcp;

/// <summary>The current token of a provider for MCP servers with `auth.provider`: a stored auth.json credential (an OAuth access
/// token, refreshed when it expires, or an api_key entry) or the provider's environment key, as the session's own requests
/// resolve it. Null when the provider is not configured, so the request goes out without credentials and the server's 401 asks
/// for `/login &lt;provider&gt;`.</summary>
internal static class McpProviderTokens
{
    internal static async ValueTask<string?> ResolveAsync(LiveSessionRuntime runtime, string provider, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentException.ThrowIfNullOrEmpty(provider);
        var environment = runtime.CreateEnvironment();
        try
        {
            if (provider == AnthropicLiveAuthentication.Provider)
                return (await runtime.CreateAnthropicAuthentication(environment).ResolveAsync(token).ConfigureAwait(false)).Authentication?.Secret;
            if (ProviderAuthCatalog.Find(provider) is not { } entry) return null;
            var store = runtime.AuthPath is null ? null : new AuthJsonCredentialStore(runtime.AuthPath, runtime.Time);
            var authentication = new ProviderLiveAuthentication(entry, store, environment, runtime.CreateAuthHttp ?? (() => new HttpClient()), runtime.Time);
            return (await authentication.ResolveAsync(token).ConfigureAwait(false))?.ApiKey;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or
            PiSharp.AI.Authentication.OAuth.OAuthLifecycleException)
        { return null; }
    }
}
