using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Authentication;

/// <summary>Caller-supplied OAuth values. This leaf never acquires or transmits them.</summary>
public sealed record McpOAuthTokens(string AccessToken, string TokenType, double? ExpiresIn = null,
    string? Scope = null, string? RefreshToken = null, string? IdToken = null)
{
    public override string ToString() => nameof(McpOAuthTokens);
}

/// <summary>Immutable state for one normalized, exact server URL. JSON metadata retains unknown fields.</summary>
public sealed record McpOAuthState(string ServerUrl, JsonData? ClientInformation = null,
    McpOAuthTokens? Tokens = null, double? TokensExpireAt = null, string? CodeVerifier = null,
    string? OAuthState = null, JsonData? Discovery = null)
{
    public override string ToString() => nameof(McpOAuthState);
}

public enum McpOAuthInvalidation { All, Client, Tokens, Verifier, Discovery }

/// <summary>Explicit caller admission to state storage. No default durable store, secret lookup,
/// credential acquisition or filesystem access is installed. Each returned original must settle.</summary>
public interface IMcpAdmittedOAuthStateStore
{
    ValueTask<McpOAuthState?> LoadAsync();
    ValueTask SaveAsync(McpOAuthState state);
}

/// <summary>Retains the exact admitted callback original and its unflattened failure evidence.
/// Synchronous callback failure has no Task original.</summary>
public sealed class McpOAuthStoreException(Task? original, Exception evidence)
    : IOException("Admitted MCP OAuth state operation failed.", evidence)
{
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
}
