// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/mcp/src/oauth/flow.ts, packages/mcp/src/oauth/errors.ts.
using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Authentication;

public enum McpOAuthAuthorizationOutcome { Authorized, Redirect }
/// <summary>A Client ID Metadata Document: an https URL used as `client_id`, and a redirect URI it lists.</summary>
public sealed record McpOAuthClientMetadataDocument(string Url, string RedirectUrl)
{ public override string ToString() => nameof(McpOAuthClientMetadataDocument); }
/// <summary>Explicit proposed values only. Neither endpoint, registration, redirect nor storage authority is acquired.
/// <paramref name="ClientMetadataDocument"/> is called, with the authorization server metadata (null when it has none),
/// only when no client is stored; the document is not stored. <paramref name="Iss"/> is the `iss` parameter of the
/// authorization response that delivered <paramref name="AuthorizationCode"/> (RFC 9207).
/// <paramref name="AuthorizationServerMetadataUrl"/> replaces discovery and is trusted as configured; discovery is then
/// not cached.</summary>
public sealed record McpOAuthOrchestrationOptions(Uri ServerUrl, JsonData ClientMetadata,
    string? ClientMetadataScope = null, Func<JsonData?, McpOAuthClientMetadataDocument?>? ClientMetadataDocument = null, string? Scope = null,
    string? AuthorizationCode = null, bool SkipRefresh = false, string? Iss = null, Uri? AuthorizationServerMetadataUrl = null)
{ public override string ToString() => nameof(McpOAuthOrchestrationOptions); }
/// <summary><paramref name="RedirectUrl"/> is the redirect URI of a Client ID Metadata Document, which may differ from
/// the installed one by a server-specific path; null uses the installed redirect URI.</summary>
public sealed record McpOAuthOrchestrationContext(McpOAuthOrchestrationOptions Options,
    McpOAuthDiscoveredServer Discovery, McpOAuthAdmittedClient Client, string? Resource, string? Scope, string? State = null,
    string? RedirectUrl = null)
{ public override string ToString() => nameof(McpOAuthOrchestrationContext); }
/// <summary>An authorization response came from another authorization server than the metadata names, or lacks the
/// `iss` parameter its server promised (RFC 9207). Raised before the code is sent anywhere.</summary>
public sealed class McpOAuthIssuerMismatchException(string expected, string? received)
    : IOException($"OAuth issuer mismatch: expected {Quote(expected)}, received {(received is null ? "none" : Quote(received))}")
{
    public string Expected { get; } = expected;
    /// <summary>Null when the response lacks the `iss` parameter its server promised.</summary>
    public string? Received { get; } = received;
    private static readonly System.Text.Json.JsonSerializerOptions JsonStringify =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value, JsonStringify);
}
/// <summary>Scope arithmetic of the original flow.</summary>
public static class McpOAuthScope
{
    /// <summary>Scopes for a step-up authorization: the challenged scopes plus the ones granted so far, since a challenge
    /// may list only the missing scopes and a token with just those would lose access the old one had (SEP-2350).
    /// Without challenged scopes, null lets the flow pick its default.</summary>
    public static string? StepUp(string? granted, string? challenged)
    {
        if (string.IsNullOrEmpty(challenged)) return null;
        var scopes = new[] { granted, challenged }.SelectMany(scope => (scope ?? "").Split(WhiteSpace, StringSplitOptions.RemoveEmptyEntries));
        return string.Join(' ', scopes.Distinct(StringComparer.Ordinal));
    }
    /// <summary>A response without `scope` grants the requested scope (RFC 6749 §5.1) and a refresh without one keeps the
    /// grant's (§6); recorded so a step-up can keep it.</summary>
    public static McpOAuthTokens WithScope(McpOAuthTokens tokens, string? scope)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return tokens.Scope is null && !string.IsNullOrEmpty(scope) ? tokens with { Scope = scope } : tokens;
    }
    // ECMAScript `\s`.
    private static readonly char[] WhiteSpace = [.. new[] { 0x20, 0x09, 0x0a, 0x0d, 0x0b, 0x0c, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002,
        0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000, 0xfeff }.Select(code => (char)code)];
}
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