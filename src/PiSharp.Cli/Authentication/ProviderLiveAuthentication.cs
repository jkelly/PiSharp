// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/resolve.ts (resolveProviderAuth: a stored credential owns
// the provider; OAuth refreshed under the store lock then toAuth; api_key through the provider's resolve; ambient only without a
// stored credential), auth/helpers.ts (envApiKeyAuth.resolve), providers/cloudflare-auth.ts (per-field merge),
// providers/amazon-bedrock.ts (bedrockAuth.resolve), packages/coding-agent/src/core/settings-manager.ts (getOrCreateDeviceId) and
// core/model-runtime.ts (prepareRequest: auth is resolved for every request).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;

namespace PiSharp.Cli.Authentication;

/// <summary>One resolution (AuthResult): request auth plus provider-scoped env and the status source.</summary>
internal sealed record ProviderResolvedAuth(string? ApiKey, ImmutableDictionary<string, string?>? Headers, string? BaseUrl,
    ImmutableDictionary<string, string>? Environment, string Source)
{
    public override string ToString() => $"ProviderResolvedAuth ({Source}) [redacted]";
}

/// <summary>
/// resolveProviderAuth for a non-Anthropic provider, run at session start and before every request. A stored auth.json credential
/// owns the provider: OAuth is refreshed (within five minutes of expiry, persisted) and converted by the flow's toAuth; an api_key
/// entry resolves through the provider's api-key auth (its key template and env). Only without a stored credential is the
/// environment read. Null means the provider is not configured.
/// </summary>
internal sealed class ProviderLiveAuthentication
{
    private readonly ProviderAuthEntry provider;
    private readonly AuthJsonCredentialStore? store;
    private readonly LiveProcessEnvironment environment;
    private readonly Func<HttpMessageInvoker> createHttp;
    private readonly TimeProvider? time;
    private readonly Func<OAuthFlowContext, IProviderOAuth>? oauthOverride;
    private readonly StoredOAuthLifecycle? lifecycle;

    public ProviderLiveAuthentication(ProviderAuthEntry provider, AuthJsonCredentialStore? store, LiveProcessEnvironment environment,
        Func<HttpMessageInvoker> createHttp, TimeProvider? time = null, Func<OAuthFlowContext, IProviderOAuth>? oauthOverride = null)
    {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(createHttp);
        this.provider = provider; this.store = store; this.environment = environment; this.createHttp = createHttp; this.time = time;
        this.oauthOverride = oauthOverride;
        if (store is not null && provider.OAuth is not null) lifecycle = new(store, new Refresh(this), time);
    }

    /// <summary>The last OAuth credential the resolution used (Copilot's availableModelIds filter reads it).</summary>
    public OAuthCredentialSnapshot? LastCredential { get; private set; }

    private IProviderOAuth Flow(HttpMessageInvoker http)
    {
        var context = new OAuthFlowContext(http, environment.Get, time, null, AnthropicOAuth.CallbackPort, null);
        return oauthOverride?.Invoke(context) ?? provider.OAuth!.Create(context);
    }

    /// <summary>getAuth(provider, { minOAuthValidityMs }): <paramref name="minimumOAuthValidityMilliseconds"/> raises the OAuth refresh window.</summary>
    public async ValueTask<ProviderResolvedAuth?> ResolveAsync(CancellationToken cancellationToken, long? minimumOAuthValidityMilliseconds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = store is null ? null : await store.ReadEntryAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (stored is { Type: "oauth" } && provider.OAuth is not null)
        {
            var credential = await lifecycle!.ResolveAsync(provider.Id, minimumOAuthValidityMilliseconds, cancellationToken).ConfigureAwait(false);
            if (credential is null) return null; // logged out meanwhile
            LastCredential = credential;
            using var http = createHttp();
            var auth = Flow(http).ToAuth(credential);
            return new(auth.ApiKey, auth.Headers, auth.BaseUrl, null, "OAuth");
        }
        if (stored is not null && stored.Type != "api_key") return null;
        if (stored is null && provider.ApiKeyName.Length == 0) return null;
        string? key = null;
        if (stored?.Key is { Length: > 0 } configured)
        {
            key = ConfigValueTemplate.Resolve(configured, stored.Environment, environment.Get);
        }
        var scoped = stored?.Environment?.ToImmutableDictionary(StringComparer.Ordinal);
        return ResolveApiKey(key, scoped, stored is not null);
    }

    /// <summary>The provider's ApiKeyAuth.resolve with the stored key and env (per field) and the ambient environment.</summary>
    private ProviderResolvedAuth? ResolveApiKey(string? storedKey, ImmutableDictionary<string, string>? scoped, bool hasStored)
    {
        string? Scoped(string name) => scoped is not null && scoped.TryGetValue(name, out var value) ? value : null;
        switch (provider.ApiKeyLogin)
        {
            case ApiKeyLoginKind.CloudflareWorkersAI:
            case ApiKeyLoginKind.CloudflareAIGateway:
            {
                // Per-field merge: a stored value wins; a credential carrying only the key still reads the ids from the environment.
                string? Value(string name) => hasStored ? (name == "CLOUDFLARE_API_KEY" ? storedKey : Scoped(name)) ?? environment.Get(name) : environment.Get(name);
                var apiKey = Value("CLOUDFLARE_API_KEY"); var account = Value("CLOUDFLARE_ACCOUNT_ID");
                var gateway = provider.ApiKeyLogin == ApiKeyLoginKind.CloudflareAIGateway ? Value("CLOUDFLARE_GATEWAY_ID") : null;
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(account) || provider.ApiKeyLogin == ApiKeyLoginKind.CloudflareAIGateway && string.IsNullOrEmpty(gateway)) return null;
                var env = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                env["CLOUDFLARE_ACCOUNT_ID"] = account; if (gateway is not null) env["CLOUDFLARE_GATEWAY_ID"] = gateway;
                var source = hasStored ? "stored credential" : "CLOUDFLARE_API_KEY";
                return provider.ApiKeyLogin == ApiKeyLoginKind.CloudflareWorkersAI
                    ? new(apiKey, null, null, env.ToImmutable(), source)
                    : new(null, PiSharp.AI.Providers.ProviderHeaderPolicies.CloudflareGatewayHeaders(apiKey), null, env.ToImmutable(), source);
            }
            case ApiKeyLoginKind.AmazonBedrock:
            {
                if (storedKey is { Length: > 0 }) return new(storedKey, null, null, scoped, "stored credential");
                if (environment.Get("AWS_BEARER_TOKEN_BEDROCK") is not null) return new(null, null, null, null, "AWS_BEARER_TOKEN_BEDROCK");
                if ((Scoped("AWS_PROFILE") ?? environment.Get("AWS_PROFILE")) is not null)
                    return new(null, null, null, scoped, Scoped("AWS_PROFILE") is not null ? "stored credential" : "AWS_PROFILE");
                if (environment.Get("AWS_ACCESS_KEY_ID") is not null && environment.Get("AWS_SECRET_ACCESS_KEY") is not null) return new(null, null, null, null, "AWS access keys");
                if (environment.Get("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is not null || environment.Get("AWS_CONTAINER_CREDENTIALS_FULL_URI") is not null)
                    return new(null, null, null, null, "ECS task role");
                if (environment.Get("AWS_WEB_IDENTITY_TOKEN_FILE") is not null) return new(null, null, null, null, "web identity token");
                return null;
            }
            case ApiKeyLoginKind.GoogleVertex:
            {
                // vertexAuth.resolve: an API key, else ADC credentials with a project and a location.
                var key = storedKey is { Length: > 0 } ? storedKey : environment.Get("GOOGLE_CLOUD_API_KEY");
                if (key is not null) return new(key, null, null, null, storedKey is { Length: > 0 } ? "stored credential" : "GOOGLE_CLOUD_API_KEY");
                var adcPath = Scoped("GOOGLE_APPLICATION_CREDENTIALS") ?? environment.Get("GOOGLE_APPLICATION_CREDENTIALS");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string Expand(string path) => path.StartsWith('~') ? home + path[1..] : path;
                var hasCredentials = adcPath is not null ? File.Exists(Expand(adcPath))
                    : File.Exists(Path.Combine(home, ".config", "gcloud", "application_default_credentials.json")) ||
                      environment.Get("APPDATA") is { } appData && File.Exists(Path.Combine(appData, "gcloud", "application_default_credentials.json"));
                var project = Scoped("GOOGLE_CLOUD_PROJECT") ?? environment.Get("GOOGLE_CLOUD_PROJECT") ?? environment.Get("GCLOUD_PROJECT");
                var location = Scoped("GOOGLE_CLOUD_LOCATION") ?? environment.Get("GOOGLE_CLOUD_LOCATION");
                return hasCredentials && project is not null && location is not null
                    ? new(null, null, null, scoped, hasStored ? "stored credential" : "gcloud application default credentials") : null;
            }
            default:
            {
                // envApiKeyAuth: a stored key wins (with its env), else the first set variable.
                if (storedKey is { Length: > 0 }) return new(storedKey, null, null, scoped, "stored credential");
                foreach (var name in provider.EnvironmentVariables)
                    if (environment.Get(name) is { } value) return new(value, null, null, null, name);
                return null;
            }
        }
    }

    private sealed class Refresh(ProviderLiveAuthentication owner) : IAdmittedOAuthRefresh
    {
        public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
        {
            using var http = owner.createHttp();
            return await owner.Flow(http).RefreshAsync(provider, current, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>settings-manager.ts getOrCreateDeviceId: the global settings.json deviceId, created on first use (project settings ignored).</summary>
internal static class DeviceIdentity
{
    private static readonly object Gate = new();
    public static string GetOrCreate(string agentDirectory)
    {
        lock (Gate)
        {
            var path = Path.Combine(agentDirectory, "settings.json");
            JsonObject settings;
            try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path).TrimStart('﻿')) as JsonObject ?? [] : []; }
            catch (JsonException) { throw new InvalidOperationException("Cannot create a device ID: settings.json is not valid JSON."); }
            if (settings["deviceId"] is JsonValue existing && existing.TryGetValue<string>(out var id) && id.Length != 0) return id;
            var created = Guid.NewGuid().ToString();
            settings["deviceId"] = created;
            Directory.CreateDirectory(agentDirectory);
            File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\n",
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return created;
        }
    }
}

/// <summary>The shipped GitHub Copilot chat catalog ids (GITHUB_COPILOT_MODELS), from the pinned catalog resource when present.</summary>
internal static class CopilotCatalog
{
    private static readonly Lazy<ImmutableHashSet<string>> Ids = new(() =>
    {
        using var resource = typeof(CopilotCatalog).Assembly.GetManifestResourceStream("PiSharp.Cli.Models.github-copilot.json");
        if (resource is null) return [];
        using var document = JsonDocument.Parse(resource);
        var ids = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var api in document.RootElement.EnumerateObject())
            if (api.Value.ValueKind == JsonValueKind.Object)
                foreach (var model in api.Value.EnumerateObject())
                    if (model.Name.StartsWith("chat:", StringComparison.Ordinal)) ids.Add(model.Name[5..]);
        return ids.ToImmutable();
    });
    public static bool Contains(string id) => Ids.Value.Contains(id);
}
