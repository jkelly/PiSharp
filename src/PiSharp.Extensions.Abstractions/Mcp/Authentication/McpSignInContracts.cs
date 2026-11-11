// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/oauth/types.ts.
namespace PiSharp.Extensions.Mcp.Authentication;

/// <summary>Resolved settings; the application owns secret expansion and its identity.</summary>
public record McpOAuthClientSettings(string? ClientId = null, string? ClientSecret = null, int? CallbackPort = null,
    string? CallbackUrl = null, string? Scope = null, string? ClientName = null, string? ClientRegistration = null,
    Uri? AuthServerMetadataUrl = null);

public sealed record McpClientMetadataDocuments(Uri BaseUrl, Func<string, bool> ListsRedirectUrl);

/// <summary>An explicitly admitted authorization operation. It must save its token response before settling.</summary>
public sealed record McpSignInFlowRequest(string RedirectUrl, bool RefreshOnly = false, bool SkipRefresh = false,
    string? Scope = null, string? Code = null, string? Iss = null, string? AuthorizationRedirectUrl = null,
    McpOAuthChallenge? Challenge = null);
public sealed record McpSignInFlowResult(McpOAuthAuthorizationOutcome Outcome, Uri? AuthorizationUrl = null);
public delegate Task<McpSignInFlowResult> McpSignInFlow(McpSignInFlowRequest request, CancellationToken token);

/// <summary>All effects are borrowed. The flow adapts an admitted OAuth host; no browser or HTTP client is acquired here.</summary>
public sealed record McpOAuthClientOptions(Uri ServerUrl, IMcpAdmittedOAuthStateStore Store,
    McpOAuthClientSettings Settings, string ClientName, McpSignInFlow Flow,
    Func<CancellationToken, Task<IAsyncDisposable>> RefreshLock,
    McpClientMetadataDocuments? ClientMetadataDocuments = null);
public sealed record McpSignInOptions(McpOAuthClientOptions Client, string RedirectUrl, McpOAuthChallenge? Challenge = null);
public sealed record McpSignInRedirect(string AuthorizationUrl, string RedirectUrl, string State, string? Scope = null);
public sealed record McpSignInStart(McpSignInRedirect? Redirect = null)
{ public bool Authorized => Redirect is null; }
public sealed record McpAuthorizationResponse(string Code, string State, string? Iss = null);
public sealed class McpRefreshLockBusyException(Exception? inner = null)
    : IOException("Another process is refreshing the MCP server's tokens", inner);
public sealed class McpSignInCancellationException() : OperationCanceledException("Sign-in cancelled");
