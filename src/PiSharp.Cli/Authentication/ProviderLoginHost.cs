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
            { [AnthropicOAuth.CallbackHostEnvironment] = Environment.GetEnvironmentVariable(AnthropicOAuth.CallbackHostEnvironment) })));

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

    /// <summary>The default live runtime reads the shared <c>auth.json</c> only when it exists, so a session start creates no files.</summary>
    public static async ValueTask<AuthenticationResolution?> ResolveDefaultAsync(CancellationToken cancellationToken)
    {
        var store = AuthJsonCredentialStore.CreateDefault();
        if (!File.Exists(store.AuthPath)) return null;
        using var http = new HttpClient();
        return await ResolveAsync(store, http, null, cancellationToken).ConfigureAwait(false);
    }
}
