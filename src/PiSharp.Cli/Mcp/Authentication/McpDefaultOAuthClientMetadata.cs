// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts createProvider,
// packages/mcp/src/oauth/provider.ts McpOAuthProvider (client metadata defaults), packages/coding-agent/src/config.ts APP_NAME.
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Cli.Mcp.Authentication;

/// <summary>The client metadata the original registers with: `client_name` from `oauth.clientName`, else the
/// application name, and the provider's defaults for the rest. Pass it as <see cref="McpDefaultOAuthHostResources.ClientMetadata"/>.</summary>
public static class McpDefaultOAuthClientMetadata
{
    /// <summary>The original's `APP_NAME`: `piConfig.name` from its package.json, which is unset, so `"pi"`.</summary>
    public const string DefaultClientName = "pi";

    /// <summary>`oauth.clientName` of the server, or <see cref="DefaultClientName"/>.</summary>
    public static string ClientName(McpServerConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Raw.Value.TryGetProperty("oauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object &&
            oauth.TryGetProperty("clientName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : DefaultClientName;
    }

    /// <summary>`{client_name, redirect_uris: [redirect], grant_types, response_types, token_endpoint_auth_method}` in the
    /// original's key order. A client secret selects `client_secret_post`, otherwise the client is public (`none`).</summary>
    public static JsonData Create(McpServerConfiguration config, string redirectUri, bool hasClientSecret)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Create(ClientName(config), redirectUri, hasClientSecret);
    }

    /// <summary>As <see cref="Create(McpServerConfiguration, string, bool)"/> with an already read `oauth.clientName`.</summary>
    public static JsonData Create(string? clientName, string redirectUri, bool hasClientSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(redirectUri);
        return JsonData.Parse(JsonSerializer.Serialize(new
        {
            client_name = clientName ?? DefaultClientName,
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = hasClientSecret ? "client_secret_post" : "none"
        }));
    }
}
