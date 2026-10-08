using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Cli.Mcp.Authentication;

public enum McpDefaultOAuthHttpPurpose { Discovery, Registration, AuthorizationCode, Refresh, AuthorizationRedirect }

/// <summary>Caller-admitted resources for one generation. No client, browser, endpoint,
/// entropy, credentials, environment or state store is acquired by installation.
/// <paramref name="ClientMetadataDocumentBase"/> selects `oauth.clientRegistration: "cimd"`: the caller-chosen https
/// base under which the application serves its Client ID Metadata Documents (`client.json` and
/// `&lt;callback ID&gt;/client.json`); null registers dynamically. <paramref name="AuthorizationServerMetadataUrl"/> is
/// `oauth.authServerMetadataUrl`. <paramref name="RequestTimeout"/> bounds each authorization server request
/// (default 15 s), so an unresponsive server cannot hold a sign-in or refresh for long.</summary>
public sealed record McpDefaultOAuthHostResources(
    Uri Server, Uri RedirectUri, JsonData ClientMetadata,
    IMcpAdmittedOAuthStateStore Store, Func<double> UnixMilliseconds,
    Func<CancellationToken, ValueTask<byte[]>> Entropy32,
    Func<Uri, CancellationToken, ValueTask> Redirect,
    Func<string?, CancellationToken, ValueTask> ValidateAuthorizationState,
    McpAdmittedHttpRequestFactory Http,
    Func<Uri, McpDefaultOAuthHttpPurpose, bool> AdmitEndpoint,
    McpDefaultOAuthHostCancellationAdmission CancellationAdmission,
    Func<CancellationToken, ValueTask<string?>>? AuthorizationState = null,
    Func<McpDefaultOAuthClientAuthentication, CancellationToken, ValueTask>? AddClientAuthentication = null,
    Uri? ClientMetadataDocumentBase = null, int MaximumBytes = 1_048_576,
    Uri? AuthorizationServerMetadataUrl = null, TimeSpan? RequestTimeout = null)
{
    /// <summary>Bounds each request to the authorization server.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    public override string ToString() => nameof(McpDefaultOAuthHostResources);
}

/// <summary>Actual retained originals. Aliases may intentionally name the same Task.</summary>
public sealed record McpDefaultOAuthHostOriginal(string Phase, Task? Original,
    AggregateException? Aggregate, Exception? Direct);

public sealed class McpDefaultOAuthHostFailure(string phase,
    ImmutableArray<McpDefaultOAuthHostOriginal> originals, Exception evidence)
    : IOException("Admitted default OAuth host " + phase + " failed.", evidence)
{
    public ImmutableArray<McpDefaultOAuthHostOriginal> Originals { get; } = originals;
}

/// <summary>Required affine callback cancellation admission, including after an await.
/// Callers directly join CancelAsync's exact original before returning.</summary>
public sealed class McpDefaultOAuthHostCancellationAdmission
{
    private readonly object gate = new();
    private McpDefaultOAuthHost? owner;
    internal void Bind(McpDefaultOAuthHost value)
    {
        lock (gate)
        {
            if (owner is not null) throw new InvalidOperationException("OAuth host cancellation admission is affine.");
            owner = value;
        }
    }
    private McpDefaultOAuthHost Capture()
    { lock (gate) return owner ?? throw new InvalidOperationException("OAuth host admission is not bound."); }
    public void Cancel(CancellationTokenSource source) => Capture().CancelOwned(source);
    public Task CancelAsync(CancellationTokenSource source) => Capture().CancelOwnedAsync(source);
}
