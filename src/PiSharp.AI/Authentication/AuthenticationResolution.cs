using System.Text.Json.Serialization;

namespace PiSharp.AI.Authentication;

public enum AuthenticationKind { ApiKey, BearerToken }
public enum AuthenticationOrigin { StoredApiKey, EnvironmentApiKey, AnthropicAuthToken, AnthropicOAuthEnvironmentToken }
public enum AuthenticationDiagnostic { Resolved, Missing, UnknownProvider, AmbientAuthenticationUnsupported, InjectedLookupFailed }

/// <summary>Only an injected api_key credential. Stored OAuth/refresh flows are outside this slice.</summary>
public sealed class StoredApiKeyCredential(string? key, ProviderEnvironmentSnapshot? environment = null)
{
    [JsonIgnore] public string? Key { get; } = key;
    [JsonIgnore] public ProviderEnvironmentSnapshot? Environment { get; } = environment;
    public override string ToString() => "StoredApiKeyCredential [redacted]";
}

/// <summary>Secret access is explicit; default diagnostic strings and JSON omit credential material.</summary>
public sealed class ResolvedAuthentication
{
    internal ResolvedAuthentication(AuthenticationKind kind, AuthenticationOrigin origin, string secret,
        string? environmentName = null, ProviderEnvironmentSnapshot? credentialEnvironment = null)
    { Kind = kind; Origin = origin; Secret = secret; EnvironmentName = environmentName; CredentialEnvironment = credentialEnvironment; }

    public AuthenticationKind Kind { get; }
    public AuthenticationOrigin Origin { get; }
    public string? EnvironmentName { get; }
    [JsonIgnore] public string Secret { get; }
    [JsonIgnore] public ProviderEnvironmentSnapshot? CredentialEnvironment { get; }
    public string? GetAuthorizationHeader() => Kind == AuthenticationKind.BearerToken ? "Bearer " + Secret : null;
    public override string ToString() => $"ResolvedAuthentication ({Kind}, {Origin}) [redacted]";
}

public sealed record AuthenticationResolution(AuthenticationDiagnostic Diagnostic, ResolvedAuthentication? Authentication = null);
