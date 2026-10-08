using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Authentication;

public enum McpOAuthAuthorizationOutcome { Authorized, Redirect }
/// <summary>Explicit proposed values only. Neither endpoint, registration, redirect nor storage authority is acquired.</summary>
public sealed record McpOAuthOrchestrationOptions(Uri ServerUrl, JsonData ClientMetadata,
    string? ClientMetadataScope = null, Uri? ClientMetadataUrl = null, string? Scope = null,
    string? AuthorizationCode = null, bool SkipRefresh = false)
{ public override string ToString() => nameof(McpOAuthOrchestrationOptions); }
public sealed record McpOAuthOrchestrationContext(McpOAuthOrchestrationOptions Options,
    McpOAuthDiscoveredServer Discovery, McpOAuthAdmittedClient Client, string? Resource, string? Scope, string? State = null)
{ public override string ToString() => nameof(McpOAuthOrchestrationContext); }
public sealed record McpOAuthAuthorizationProposal(Uri AuthorizationUrl, string CodeVerifier)
{ public override string ToString() => nameof(McpOAuthAuthorizationProposal); }
public sealed record McpOAuthOrchestrationOriginal(string Phase, Task? Original,
    AggregateException? Aggregate, Exception? Direct);
public sealed record McpOAuthOrchestrationResult(McpOAuthAuthorizationOutcome Outcome,
    ImmutableArray<McpOAuthOrchestrationOriginal> Originals);

/// <summary>All callbacks are explicitly admitted and borrowed. Discover/code/refresh/start callbacks must
/// adapt the actual reviewed operations, not acquire ambient HTTP, entropy, clients or browsers. Every returned
/// ValueTask is captured once and joined. After-await cancellation that can enter callbacks MUST use the
/// owner's required cancellation admission and join its returned original. Unjoined callback work is outside admission.
/// Optional persistence/invalidation callbacks follow the original provider optional surface.</summary>
public sealed record McpOAuthOrchestrationDependencies(
    Func<McpOAuthOrchestrationOptions, CancellationToken, ValueTask<McpOAuthDiscoveredServer>> Discover,
    Func<McpOAuthDiscoveredServer, CancellationToken, ValueTask>? SaveDiscovery,
    Func<CancellationToken, ValueTask<McpOAuthAdmittedClient?>> ReadClient,
    Func<McpOAuthAdmittedClient, CancellationToken, ValueTask>? SaveClient,
    Func<McpOAuthOrchestrationOptions, McpOAuthDiscoveredServer, string?, CancellationToken, ValueTask<McpOAuthAdmittedClient>> RegisterClient,
    Func<CancellationToken, ValueTask<McpOAuthTokens?>> ReadTokens,
    Func<McpOAuthOrchestrationContext, string, CancellationToken, ValueTask<McpOAuthTokens>> Refresh,
    Func<CancellationToken, ValueTask<string>> ReadVerifier,
    Func<McpOAuthOrchestrationContext, string, string, CancellationToken, ValueTask<McpOAuthTokens>> ExchangeCode,
    Func<McpOAuthOrchestrationContext, CancellationToken, ValueTask<McpOAuthAuthorizationProposal>> BeginAuthorization,
    Func<McpOAuthTokens, CancellationToken, ValueTask> SaveTokens,
    Func<string, CancellationToken, ValueTask> SaveVerifier,
    Func<Uri, CancellationToken, ValueTask> Redirect,
    Func<McpOAuthInvalidation, CancellationToken, ValueTask>? Invalidate,
    Func<CancellationToken, ValueTask<string?>>? ReadAuthorizationState = null);

/// <summary>Retains the exact actual originals, first captured Task.Exception aggregates and selected direct faults.</summary>
public sealed class McpOAuthOrchestrationException(string phase,
    ImmutableArray<McpOAuthOrchestrationOriginal> originals, Exception evidence)
    : IOException("Admitted OAuth orchestration " + phase + " failed.", evidence)
{ public ImmutableArray<McpOAuthOrchestrationOriginal> Originals { get; } = originals; }
public sealed class McpOAuthOrchestrationCanceledException(
    ImmutableArray<McpOAuthOrchestrationOriginal> originals, OperationCanceledException direct)
    : OperationCanceledException("Admitted OAuth orchestration canceled.", direct, direct.CancellationToken)
{ public ImmutableArray<McpOAuthOrchestrationOriginal> Originals { get; } = originals; }
public enum McpOAuthTokenFailureCategory { WireOAuthError, InsecureEndpoint }
/// <summary>Explicit operation provenance, not a guessed code allowlist. A token-operation adapter may use
/// WireOAuthError only for the actual parsed OAuth error response, and InsecureEndpoint only for the actual
/// token-endpoint safety refusal. Local parse/default-auth/store failures remain ordinary exceptions.
/// The adapter must preserve its actual original error as Evidence; no default adapter is installed.</summary>
public sealed class McpOAuthTokenOperationFailure : IOException
{
    public McpOAuthTokenFailureCategory Category { get; }
    public string? Code { get; }
    public Exception Evidence { get; }
    public McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory category, string? code, Exception evidence)
        : base("Admitted token operation failed.", evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(category) || category == McpOAuthTokenFailureCategory.WireOAuthError && code is null)
            throw new ArgumentException("Actual wire OAuth error code or insecure-endpoint provenance required.");
        Category = category; Code = code; Evidence = evidence;
    }
}