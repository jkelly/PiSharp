using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Authentication;

public enum McpOAuthExchangePurpose { ProtectedResourceMetadata, AuthorizationServerMetadata, RefreshToken }
/// <summary>Proposed request, not authority: the explicitly admitted exchange must independently
/// accept its endpoint. Body may contain caller-admitted synthetic or real credentials; never logged.</summary>
public sealed record McpOAuthExchangeRequest(Uri Endpoint, HttpMethod Method,
    ImmutableDictionary<string, string> Headers, string? Body, McpOAuthExchangePurpose Purpose)
{ public override string ToString() => nameof(McpOAuthExchangeRequest); }
public sealed record McpOAuthExchangeResponse(int Status, string Body)
{ public override string ToString() => nameof(McpOAuthExchangeResponse); }
public delegate ValueTask<McpOAuthExchangeResponse> McpAdmittedOAuthExchange(McpOAuthExchangeRequest request, CancellationToken token);
public sealed record McpOAuthAdmittedClient(string ClientId, string? ClientSecret = null, string? AuthenticationMethod = null)
{ public override string ToString() => nameof(McpOAuthAdmittedClient); }
public sealed record McpOAuthDiscoveredServer(string AuthorizationServerUrl, JsonData? AuthorizationServerMetadata,
    JsonData? ResourceMetadata)
{ public override string ToString() => nameof(McpOAuthDiscoveredServer); }
public sealed class McpOAuthProtocolException(string code, string message, int? status = null, string? errorUri = null, Exception? original = null) : IOException(message, original)
{ public string Code { get; } = code; public int? Status { get; } = status; public string? ErrorUri { get; } = errorUri; }
public sealed class McpOAuthAuthorizationRequiredException() : IOException("An explicitly admitted authorization/code flow is required; no browser or client registration is acquired.") { }
public sealed class McpOAuthFlowOriginalException(string phase, Task? original, Exception evidence, Exception direct)
    : IOException("Admitted MCP OAuth " + phase + " original failed.", evidence)
{
    public string Phase { get; } = phase;
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
    public Exception Direct { get; } = direct;
}
public sealed class McpOAuthFlowCanceledException(Task original, OperationCanceledException direct)
    : OperationCanceledException("Admitted MCP OAuth original canceled.", direct, direct.CancellationToken)
{ public Task Original { get; } = original; public OperationCanceledException Direct { get; } = direct; }