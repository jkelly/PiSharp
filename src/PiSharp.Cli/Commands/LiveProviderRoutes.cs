// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-runtime.ts (prepareRequest: auth resolved
// before every request, a stored credential owns the provider), packages/ai/src/auth/resolve.ts, providers/amazon-bedrock.ts,
// providers/openai-codex.ts, providers/github-copilot.ts (filterModels), providers/cloudflare-workers-ai.ts and
// providers/cloudflare-ai-gateway.ts (resolveCloudflareModel).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.Bedrock;
using PiSharp.AI.Protocols.OpenAICodexResponses;
using PiSharp.AI.Providers;
using PiSharp.Cli.Authentication;

namespace PiSharp.Cli.Commands;

/// <summary>
/// The live route for providers whose auth resolves for every request (amazon-bedrock, openai-codex, github-copilot,
/// cloudflare-workers-ai, cloudflare-ai-gateway), and the stored auth.json credential of the other static-key live providers.
/// </summary>
internal sealed class LiveProviderRoute
{
    internal static bool Handles(string provider) =>
        provider is "amazon-bedrock" or "openai-codex" or "github-copilot" or "cloudflare-workers-ai" or "cloudflare-ai-gateway";

    /// <summary>The APIs each routed provider serves (providers/*.ts api maps).</summary>
    internal static bool SupportsApi(string provider, string api) => provider switch
    {
        "amazon-bedrock" => api == "bedrock-converse-stream",
        "openai-codex" => api == "openai-codex-responses",
        "github-copilot" or "cloudflare-ai-gateway" => api is "anthropic-messages" or "openai-completions" or "openai-responses",
        "cloudflare-workers-ai" => api == "openai-completions",
        _ => false
    };

    private readonly LiveSessionSelection selection;
    private readonly ProviderLiveAuthentication authentication;
    private readonly LiveSessionRuntime runtime;
    private readonly PiSharp.Cli.Authentication.LiveProcessEnvironment environment;

    /// <summary>The AWS credential chain's client (STS, SSO, container and instance metadata), shared for the session.</summary>
    private readonly Lazy<HttpMessageInvoker> credentialHttp;

    private LiveProviderRoute(LiveSessionSelection selection, ProviderLiveAuthentication authentication, LiveSessionRuntime runtime,
        PiSharp.Cli.Authentication.LiveProcessEnvironment environment)
    {
        this.selection = selection; this.authentication = authentication; this.runtime = runtime; this.environment = environment;
        credentialHttp = new(() => (runtime.CreateAuthHttp ?? (() => new HttpClient()))());
    }

    /// <summary>
    /// Connects a routed provider (its auth must resolve now, as upstream refuses an unconfigured provider), or returns a static-key
    /// connection for another catalog provider with a stored auth.json credential. Null leaves the environment-key route unchanged.
    /// </summary>
    internal static LiveSessionConnection? TryConnect(LiveSessionSelection selection, LiveSessionRuntime runtime)
    {
        var provider = selection.Model.Provider;
        if (provider == "anthropic" || ProviderAuthCatalog.Find(provider) is not { } entry) return null;
        var store = runtime.AuthPath is null ? null : new AuthJsonCredentialStore(runtime.AuthPath, runtime.Time);
        if (!Handles(provider) && (provider == "azure" || store is null || !File.Exists(store.AuthPath))) return null;
        var environment = runtime.CreateEnvironment();
        var authentication = new ProviderLiveAuthentication(entry, store, environment, runtime.CreateAuthHttp ?? (() => new HttpClient()), runtime.Time,
            runtime.OAuthFlows is { } flows ? context => flows(provider, context) : null);
        ProviderResolvedAuth? resolved;
        try { resolved = authentication.ResolveAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
        catch (OAuthLifecycleException error) when (error.Failure == OAuthLifecycleFailure.Refresh)
        { throw new LiveSessionException("LiveAuthenticationFailed", "OAuth refresh failed for " + provider); }
        catch (Exception error) when (error is OAuthLifecycleException or InvalidDataException or InvalidOperationException or IOException)
        { throw new LiveSessionException("LiveAuthenticationFailed", error is OAuthLifecycleException ? "Credential store read failed for " + provider : error.Message); }
        if (!Handles(provider))
        {
            // A stored credential owns the provider; without one the environment-key route applies.
            if (store!.ReadEntryAsync(provider, CancellationToken.None).GetAwaiter().GetResult() is null) return null;
            if (resolved?.ApiKey is not { Length: > 0 } key || key.Length > 4096 || key.Any(char.IsControl))
                throw new LiveSessionException("MissingLiveApiKey", "Provider is not configured: " + provider);
            return new(selection, runtime.CreateHttpHandler(), key);
        }
        if (resolved is null)
            throw new LiveSessionException("MissingLiveApiKey", provider == "openai-codex"
                ? "Run /login to sign in to OpenAI Codex before launching the live session."
                : $"Run /login, or set {string.Join(" or ", entry.EnvironmentVariables)}, before launching the live session.");
        // providers/github-copilot.ts filterModels: an OAuth credential's availableModelIds hide every other model.
        if (provider == "github-copilot" && GitHubCopilotOAuth.AvailableModels(authentication.LastCredential) is { } available && !available.Contains(selection.Model.Id))
            throw new LiveSessionException("UnknownLiveModel", "The selected GitHub Copilot model is not available to this account.");
        return new(selection, runtime.CreateHttpHandler(), string.Empty) { ProviderRoute = new LiveProviderRoute(selection, authentication, runtime, environment) };
    }

    private async ValueTask<ProviderResolvedAuth> CurrentAsync(CancellationToken token) =>
        await authentication.ResolveAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Provider is not configured: " + selection.Model.Provider);

    /// <summary>Binds the provider's protocol for the main route or a summary (no caching, thinking off).</summary>
    internal NativeHttpModelProvider Create(HttpMessageHandler? handler, int maximum, bool summary)
    {
        var model = selection.Model; var definition = selection.Definition;
        switch (model.Provider)
        {
            case "amazon-bedrock":
                return NativeProviderFactory.CreateBedrock(model, definition.Raw, new BedrockConverseOptions
                {
                    MaxTokens = maximum, CacheRetention = summary ? "none" : null,
                    Auth = async token => { var auth = await CurrentAsync(token).ConfigureAwait(false); return (auth.ApiKey, auth.Environment); }
                }, new AwsEnvironment(environment.Get, runtime.HomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    credentialHttp.Value, runtime.Time), handler, summary && Levels(definition).Contains("off") ? "off" : null);
            case "openai-codex":
                return NativeProviderFactory.CreateCodexResponses(model, definition.Raw, new OpenAICodexResponsesOptions
                {
                    CacheRetention = summary ? "none" : null,
                    ModelHeaders = Headers(definition)
                }, async token => (await CurrentAsync(token).ConfigureAwait(false)).ApiKey ?? throw new InvalidOperationException("No API key for provider: openai-codex"),
                    handler, summary && Levels(definition).Contains("off") ? "off" : null);
            default:
                return NativeProviderFactory.CreateProviderRoute(model, definition.Raw, new ProviderRouteOptions(async token =>
                {
                    var auth = await CurrentAsync(token).ConfigureAwait(false);
                    var baseUrl = ProviderHeaderPolicies.ResolveCloudflareBaseUrl(auth.BaseUrl ?? definition.BaseUrl, auth.Environment);
                    return new ProviderRequestAuth(auth.ApiKey, baseUrl, auth.Headers);
                })
                { MaxTokens = maximum, Summary = summary, MaximumMessages = 1024, MaximumEntryCharacters = 1_048_576, MaximumPayloadBytes = 1_048_576 }, handler);
        }
    }

    private static ImmutableArray<string> Levels(FrozenCatalogModel definition) => AI.Protocols.ProviderShared.ProviderTranscriptAccess.SupportedThinkingLevels(definition.Raw);

    private static ImmutableDictionary<string, string>? Headers(FrozenCatalogModel definition) =>
        definition.Raw.Value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
            ? headers.EnumerateObject().Where(header => header.Value.ValueKind == JsonValueKind.String)
                .ToImmutableDictionary(header => header.Name, header => header.Value.GetString()!, StringComparer.Ordinal)
            : null;
}
