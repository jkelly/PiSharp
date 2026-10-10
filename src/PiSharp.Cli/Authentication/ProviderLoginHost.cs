// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/resolve.ts (resolveProviderAuth: a stored credential
// owns the provider) and packages/coding-agent/src/modes/interactive/components/login-dialog.ts (openBrowser on auth_url).
using System.Diagnostics;
using PiSharp.AI.Authentication;
using PiSharp.AI.Authentication.OAuth;

namespace PiSharp.Cli.Authentication;

/// <summary>Runs a provider login and stores the credential in <c>auth.json</c>. Only Anthropic (Claude Pro/Max) OAuth is ported.</summary>
internal sealed class ProviderLoginHost(AuthJsonCredentialStore store, Func<HttpMessageInvoker> createHttp, Action<string>? openBrowser = null,
    TimeProvider? timeProvider = null, string? callbackHost = null, int callbackPort = AnthropicOAuth.CallbackPort)
{
    public const string AnthropicProvider = "anthropic";
    public const string AnthropicName = "Anthropic";
    public AuthJsonCredentialStore Store => store;

    /// <summary>Source login-dialog showAuth: the sign-in URL is also opened in the browser. A failure to open is not an error.</summary>
    public void OpenBrowser(string url)
    {
        try { openBrowser?.Invoke(url); } catch (Exception) { }
    }

    /// <summary>Source ModelRuntime.login for an OAuth provider: run the flow, then store <c>{"type":"oauth",...}</c>.</summary>
    public async Task<OAuthCredentialSnapshot?> LoginAnthropicAsync(IAnthropicOAuthLoginInteraction interaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        using var http = createHttp();
        var oauth = new AnthropicOAuth(http, timeProvider, callbackHost, callbackPort);
        var credential = await oauth.LoginAsync(interaction, cancellationToken).ConfigureAwait(false);
        return await new StoredOAuthLifecycle(store, oauth, timeProvider).ReauthenticateAsync(AnthropicProvider, credential, cancellationToken).ConfigureAwait(false);
    }

    public static ProviderLoginHost CreateDefault() => new(AuthJsonCredentialStore.CreateDefault(), () => new HttpClient(), OpenSystemBrowser,
        callbackHost: AnthropicOAuth.CallbackHostFrom(new ProviderEnvironmentSnapshot(process: new Dictionary<string, string?>
            { [AnthropicOAuth.CallbackHostEnvironment] = Environment.GetEnvironmentVariable(AnthropicOAuth.CallbackHostEnvironment) })))
    {
        AllProviders = true, GetDeviceId = () => DeviceIdentity.GetOrCreate(Path.GetDirectoryName(AuthJsonCredentialStore.CreateDefault().AuthPath)!),
        IsCopilotCatalogModel = CopilotCatalog.Contains
    };

    /// <summary>Every upstream login provider (the default CLI); false keeps the Anthropic-only login surface.</summary>
    public bool AllProviders { get; init; }
    /// <summary>getOrCreateDeviceId: the installation's stable UUID (Sign in with ChatGPT's agent host id).</summary>
    public Func<string>? GetDeviceId { get; init; }
    public Func<string, string?> ReadEnvironment { get; init; } = Environment.GetEnvironmentVariable;
    /// <summary>Whether a Copilot account model id is in the shipped catalog (policy enabling is limited to those).</summary>
    public Func<string, bool>? IsCopilotCatalogModel { get; init; }
    /// <summary>Overrides the OAuth flow for a provider (tests); null uses the catalog flow.</summary>
    public Func<string, OAuthFlowContext, IProviderOAuth?>? OAuthOverride { get; init; }
    public string AuthPath => store.AuthPath;

    internal OAuthFlowContext FlowContext(HttpMessageInvoker http) =>
        new(http, ReadEnvironment, timeProvider, callbackHost, callbackPort, IsCopilotCatalogModel);

    internal IProviderOAuth OAuthFor(ProviderAuthEntry provider, OAuthFlowContext context) =>
        OAuthOverride?.Invoke(provider.Id, context) ?? (provider.OAuth ?? throw new InvalidOperationException($"Provider {provider.Id} has no OAuth login")).Create(context);

    /// <summary>Source ModelRuntime.login: run the provider's OAuth or api-key login and store the credential in auth.json.</summary>
    public async Task LoginAsync(ProviderAuthCatalog.LoginOption option, IProviderAuthInteraction interaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(option); ArgumentNullException.ThrowIfNull(interaction);
        if (option.AuthType == "oauth")
        {
            using var http = createHttp();
            var flow = OAuthFor(option.Provider, FlowContext(http));
            var credential = await flow.LoginAsync(interaction, new(GetDeviceId), cancellationToken).ConfigureAwait(false);
            await new StoredOAuthLifecycle(store, flow, timeProvider).ReauthenticateAsync(option.Provider.Id, credential, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var loginHttp = createHttp();
        var (key, environment) = await ApiKeyLoginAsync(option.Provider, interaction, cancellationToken, ReadEnvironment, loginHttp).ConfigureAwait(false);
        await store.WriteApiKeyAsync(option.Provider.Id, key, environment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The api-key login prompts: envApiKeyAuth, cloudflare-auth.ts, amazon-bedrock.ts, google-vertex.ts and the llama.cpp
    /// extension's provider.ts (which reads the router's catalog through <paramref name="http"/>).</summary>
    internal static async Task<(string? Key, IReadOnlyDictionary<string, string>? Environment)> ApiKeyLoginAsync(ProviderAuthEntry provider,
        IProviderAuthInteraction interaction, CancellationToken token, Func<string, string?>? readEnvironment = null, HttpMessageInvoker? http = null)
    {
        Task<string> Prompt(AuthPromptKind kind, string message, IReadOnlyList<AuthPromptOption>? options = null)
        {
            token.ThrowIfCancellationRequested();
            return interaction.PromptAsync(new(kind, message, Options: options), token);
        }
        switch (provider.ApiKeyLogin)
        {
            case ApiKeyLoginKind.LlamaCpp:
                return await PiSharp.Cli.Llama.LlamaCatalog.LoginAsync(interaction, readEnvironment ?? Environment.GetEnvironmentVariable, token, http).ConfigureAwait(false);
            case ApiKeyLoginKind.CloudflareWorkersAI:
            case ApiKeyLoginKind.CloudflareAIGateway:
            {
                var key = await Prompt(AuthPromptKind.Secret, "Enter Cloudflare API key").ConfigureAwait(false);
                var account = await Prompt(AuthPromptKind.Text, "Enter Cloudflare account ID").ConfigureAwait(false);
                var environment = new Dictionary<string, string> { ["CLOUDFLARE_ACCOUNT_ID"] = account };
                if (provider.ApiKeyLogin == ApiKeyLoginKind.CloudflareAIGateway)
                    environment["CLOUDFLARE_GATEWAY_ID"] = await Prompt(AuthPromptKind.Text, "Enter Cloudflare AI Gateway ID").ConfigureAwait(false);
                return (key, environment);
            }
            case ApiKeyLoginKind.AmazonBedrock:
            {
                var method = await Prompt(AuthPromptKind.Select, "Select Amazon Bedrock authentication method:",
                    [new("bearer-token", "Bearer token"), new("aws-profile", "AWS profile"), new("credential-chain", "Existing AWS credential chain")]).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (method == "bearer-token") return (await Prompt(AuthPromptKind.Secret, "Enter Amazon Bedrock bearer token").ConfigureAwait(false), null);
                interaction.Notify(new(AuthEventKind.Info)
                {
                    Message = "Amazon Bedrock supports AWS profiles, IAM credentials, and role-based credentials.",
                    Links = [new("https://docs.aws.amazon.com/sdkref/latest/guide/standardized-credentials.html", "AWS credential provider chain")]
                });
                if (method == "aws-profile")
                    return (null, new Dictionary<string, string> { ["AWS_PROFILE"] = await Prompt(AuthPromptKind.Text, "Enter AWS profile name").ConfigureAwait(false) });
                if (method != "credential-chain") throw new InvalidOperationException($"Unknown Amazon Bedrock auth method: {method}");
                await Prompt(AuthPromptKind.Text, "Configure AWS credentials, then press Enter to continue").ConfigureAwait(false);
                return (null, null);
            }
            case ApiKeyLoginKind.GoogleVertex:
            {
                var method = await Prompt(AuthPromptKind.Select, "Select Google Vertex AI authentication method:",
                    [new("api-key", "Google Cloud API key"), new("adc", "Application Default Credentials"), new("service-account", "Service account credentials file")]).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (method == "api-key") return (await Prompt(AuthPromptKind.Secret, "Enter Google Cloud API key").ConfigureAwait(false), null);
                if (method is not ("adc" or "service-account")) throw new InvalidOperationException($"Unknown Google Vertex AI auth method: {method}");
                interaction.Notify(new(AuthEventKind.Info)
                {
                    Message = method == "adc" ? "Run `gcloud auth application-default login`, then provide the project and location."
                        : "Provide a service account credentials file, project, and location.",
                    Links = [new("https://cloud.google.com/docs/authentication/provide-credentials-adc", "Application Default Credentials")]
                });
                var environment = new Dictionary<string, string>
                {
                    ["GOOGLE_CLOUD_PROJECT"] = await Prompt(AuthPromptKind.Text, "Enter Google Cloud project ID").ConfigureAwait(false),
                    ["GOOGLE_CLOUD_LOCATION"] = await Prompt(AuthPromptKind.Text, "Enter Google Cloud location").ConfigureAwait(false)
                };
                if (method == "service-account" && await Prompt(AuthPromptKind.Text, "Enter service account credentials file path").ConfigureAwait(false) is { Length: > 0 } path)
                    environment["GOOGLE_APPLICATION_CREDENTIALS"] = path;
                return (null, environment);
            }
            default:
            {
                var key = await Prompt(AuthPromptKind.Secret, $"Enter {provider.ApiKeyName}").ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return (key, null);
            }
        }
    }

    private static void OpenSystemBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>Source resolveProviderAuth for Anthropic: a stored OAuth credential owns the provider and is refreshed (and the
/// rotation persisted) when it expires within five minutes; its access token travels in the apiKey channel, where the
/// Anthropic transport applies the OAuth projection. Without a stored credential the caller falls back to the environment.</summary>
internal static class StoredAnthropicAuthentication
{
    public static async ValueTask<AuthenticationResolution?> ResolveAsync(IAdmittedOAuthCredentialSource store, HttpMessageInvoker http,
        TimeProvider? timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(http);
        var credential = await new StoredOAuthLifecycle(store, new AnthropicOAuth(http, timeProvider), timeProvider)
            .ResolveAsync(ProviderLoginHost.AnthropicProvider, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (credential is null) return null;
        return await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(new ProviderEnvironmentSnapshot(),
            (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(new(credential.Access)), cancellationToken).ConfigureAwait(false);
    }
}
