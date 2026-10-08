using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Mcp.OAuth;

/// <summary>Already admitted client/discovery values. This does not discover/register a client or validate a callback's state.</summary>
public sealed record McpAuthorizationCodeProfile(Uri AuthorizationServer, Uri RedirectUrl,
    McpOAuthAdmittedClient Client, JsonData? Metadata = null, string? Scope = null, string? State = null, string? Resource = null)
{ public override string ToString() => nameof(McpAuthorizationCodeProfile); }

/// <summary>Proposal to an explicitly admitted exchange, never endpoint authority. Body may contain secrets.</summary>
public sealed record McpAuthorizationCodeRequest(Uri Endpoint, ImmutableDictionary<string, string> Headers, string Body)
{ public override string ToString() => nameof(McpAuthorizationCodeRequest); }
public sealed record McpAuthorizationCodeResponse(int Status, string Body)
{ public override string ToString() => nameof(McpAuthorizationCodeResponse); }

/// <summary>Mutable, callback-lifetime client-authentication proposal. The custom hook replaces default
/// Basic/Post/none authentication. It sees base headers and code/resource form only, plus admitted values.
/// All mutation ends when the hook's returned original settles; saved proposal references then refuse access.
/// No endpoint/client/credential authority is acquired. Form Set preserves first position and removes duplicates.
/// Header names/values use HTTP Headers validation; Append joins values with comma-space.</summary>
public interface IMcpAuthorizationCodeClientAuthentication
{
    Uri Endpoint { get; }
    McpOAuthAdmittedClient Client { get; }
    JsonData? Metadata { get; }
    string? HeaderGet(string name);
    void HeaderSet(string name, string value);
    void HeaderAppend(string name, string value);
    void HeaderDelete(string name);
    string? FormGet(string name);
    ImmutableArray<string> FormGetAll(string name);
    void FormSet(string name, string value);
    void FormAppend(string name, string value);
    void FormDelete(string name);
}

/// <summary>Borrowed callbacks only. Owners MUST use the flow's required cancellation admission for cancellation
/// after awaits that can enter callbacks, and directly join its returned actual Task before returning.
/// No ambient random source, browser, HTTP client, token/verifier store or credentials are supplied.</summary>
public sealed record McpAuthorizationCodeDependencies(
    Func<CancellationToken, ValueTask<byte[]>> Entropy32,
    Func<string, CancellationToken, ValueTask> SaveVerifier,
    Func<CancellationToken, ValueTask<string>> ReadVerifier,
    Func<Uri, CancellationToken, ValueTask> Redirect,
    Func<McpAuthorizationCodeRequest, CancellationToken, ValueTask<McpAuthorizationCodeResponse>> Exchange,
    Func<McpOAuthTokens, CancellationToken, ValueTask> SaveTokens,
    Func<IMcpAuthorizationCodeClientAuthentication, CancellationToken, ValueTask>? AddClientAuthentication = null);
