// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts (McpOAuthSettings,
// callbackSettings, mergeScopes, signInMcpServer, waitForAuthorizationResponse, responseFromRedirectUrl, listenForCallback),
// packages/coding-agent/src/extensions/mcp/runtime.ts oauthSettings and packages/mcp/src/oauth/provider.ts state().
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.Cli.Mcp.Authentication;

/// <summary>A server's `oauth` settings; <see cref="ClientSecret"/> is already resolved.</summary>
public sealed record McpOAuthSettings(string? ClientId = null, string? ClientSecret = null, int? CallbackPort = null,
    string? CallbackUrl = null, string? Scope = null, string? ClientName = null, string? ClientRegistration = null,
    Uri? AuthServerMetadataUrl = null)
{
    /// <summary>The validated `oauth` object of an HTTP server. <paramref name="resolveSecret"/> resolves
    /// `oauth.clientSecret` (value, description) and is only called when one is configured.</summary>
    public static McpOAuthSettings From(McpServerEntry entry, Func<string, string, string> resolveSecret)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(resolveSecret);
        if (!entry.Config.Raw.Value.TryGetProperty("oauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return new();
        string? Text(string name) => oauth.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var secret = Text("clientSecret");
        return new(Text("clientId"), secret is null ? null : resolveSecret(secret, $"MCP server \"{entry.Name}\" oauth.clientSecret"),
            oauth.TryGetProperty("callbackPort", out var port) && port.ValueKind == JsonValueKind.Number ? (int)port.GetDouble() : null,
            Text("callbackUrl"), Text("scope"), Text("clientName"), Text("clientRegistration"),
            Text("authServerMetadataUrl") is { } metadata ? new Uri(metadata) : null);
    }
}

/// <summary>How a sign-in reaches the user.</summary>
/// <param name="ShowAuthorizationUrl">Show the authorization URL and open it in a browser.</param>
/// <param name="PromptForRedirectUrl">Ask for the redirect URL from the browser address bar, for when the browser cannot
/// reach the loopback callback. Cancelled once the callback arrives or the sign-in is cancelled; null or blank cancels.</param>
public sealed record McpSignInPrompt(Action<Uri> ShowAuthorizationUrl, Func<CancellationToken, Task<string?>> PromptForRedirectUrl);

public sealed class McpSignInCancelledException() : OperationCanceledException("Sign-in cancelled");

/// <summary>Everything a sign-in uses, explicitly supplied: the server's credential store, the physical HTTP factory
/// for authorization server requests, entropy and the clock.</summary>
public sealed record McpSignInOptions(Uri ServerUrl, IMcpAdmittedOAuthStateStore Store, McpOAuthSettings Settings,
    McpSignInPrompt Prompt, McpAdmittedHttpRequestFactory Http)
{
    public Func<double> UnixMilliseconds { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Func<int, byte[]> RandomBytes { get; init; } = RandomNumberGenerator.GetBytes;
    /// <summary>Bounds each authorization server request (the original's 15 s).</summary>
    public TimeSpan RequestTimeout { get; init; } = McpDefaultOAuthHostResources.DefaultRequestTimeout;
    /// <summary>The challenge that asked for this sign-in (the connection's last one): its resource metadata URL starts discovery,
    /// its scope is requested, and `insufficient_scope` asks for more scope through the browser.</summary>
    public McpOAuthChallenge? Challenge { get; init; }
}

/// <summary>Sign-in to an MCP server: the stored refresh token when possible, otherwise the browser authorization code
/// flow against a loopback callback (or a pasted redirect URL). Tokens are saved to the store. Cancelling stops the
/// sign-in at any step with <see cref="McpSignInCancelledException"/>.</summary>
public static class McpSignIn
{
    private const string CallbackHost = "127.0.0.1";
    private const string CallbackPath = "/callback";
    /// <summary>Where pi.dev serves pi's Client ID Metadata Documents: `client.json` and `&lt;callback ID&gt;/client.json`.</summary>
    public static readonly Uri ClientMetadataBaseUrl = new("https://pi.dev/oauth");

    /// <summary>Where the loopback callback listens and the redirect URI it serves.</summary>
    internal sealed record CallbackSettings(string Host, string RedirectHost, int? Port, string Path, string? FixedRedirectUrl);

    internal static CallbackSettings Callback(McpOAuthSettings settings)
    {
        var url = new Uri(settings.CallbackUrl ?? $"http://{CallbackHost}{CallbackPath}");
        var address = url.Host.Trim('[', ']');
        int? port = url.IsDefaultPort ? settings.CallbackPort : url.Port;
        string? fixedRedirectUrl = null;
        // A configured URI with a port is sent exactly as written, since servers compare it as a string.
        if (!url.IsDefaultPort) fixedRedirectUrl = settings.CallbackUrl;
        else if (port is { } chosen) fixedRedirectUrl = new UriBuilder(url) { Port = chosen }.Uri.AbsoluteUri;
        // `localhost` is served on 127.0.0.1; browsers fall back to it when ::1 refuses.
        return new(address == "localhost" ? CallbackHost : address, address, port, url.AbsolutePath, fixedRedirectUrl);
    }

    /// <summary>Scopes of both lists, each once.</summary>
    internal static string? MergeScopes(params string?[] scopes)
    {
        var merged = scopes.SelectMany(scope => scope?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? []).Distinct(StringComparer.Ordinal).ToArray();
        return merged.Length > 0 ? string.Join(' ', merged) : null;
    }

    internal static ImmutableArray<string> RegisteredRedirectUrls(JsonData? client) =>
        client is { } value && value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("redirect_uris", out var uris) &&
            uris.ValueKind == JsonValueKind.Array ? uris.EnumerateArray().Where(uri => uri.ValueKind == JsonValueKind.String).Select(uri => uri.GetString()!).ToImmutableArray() : [];

    public static async Task SignInAsync(McpSignInOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (token.IsCancellationRequested) throw new McpSignInCancelledException();
        var settings = options.Settings; var store = options.Store;
        var stored = await store.LoadAsync().ConfigureAwait(false);
        var callbackOptions = Callback(settings);
        // Reuse the port of the registered redirect URI so the registered client stays valid.
        var registered = RegisteredRedirectUrls(stored?.ClientInformation).FirstOrDefault();
        var preferredPort = callbackOptions.Port ?? (registered is not null && Uri.TryCreate(registered, UriKind.Absolute, out var registeredUri) &&
            !registeredUri.IsDefaultPort ? registeredUri.Port : null);
        var cimd = settings.ClientRegistration == "cimd";
        var callback = Listen(callbackOptions,
            // The redirect URI of a server-specific Client ID Metadata Document.
            cimd ? [$"{CallbackPath}/{McpOAuthClientMetadataDocuments.CallbackId(options.ServerUrl)}"] : [],
            preferredPort, callbackOptions.Port is not null);
        var redirectUrl = callbackOptions.FixedRedirectUrl ?? callback.RedirectUrl;
        McpDefaultOAuthHost? host = null; Exception? failure = null;
        try
        {
            if (stored is not null)
            {
                // Every sign-in gets a fresh `state` parameter.
                var next = stored with { OAuthState = null };
                // A registered client cannot use another redirect URI, and its tokens belong to it. A Client ID Metadata
                // Document is not stored, so with one, a stored client was registered before and is replaced.
                var keepClient = !string.IsNullOrEmpty(settings.ClientId) ||
                    (cimd ? stored.ClientInformation is null : RegisteredRedirectUrls(stored.ClientInformation).Contains(redirectUrl));
                if (!keepClient) next = next with { ClientInformation = null, Tokens = null, TokensExpireAt = null };
                await store.SaveAsync(next).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(settings.ClientId))
            {
                // A configured client is used as is; the native orchestrator reads it from the store.
                var current = await store.LoadAsync().ConfigureAwait(false) ?? new McpOAuthState(options.ServerUrl.AbsoluteUri);
                var client = JsonSerializer.Serialize(settings.ClientSecret is { Length: > 0 } secret
                    ? new Dictionary<string, string> { ["client_id"] = settings.ClientId, ["client_secret"] = secret }
                    : new Dictionary<string, string> { ["client_id"] = settings.ClientId });
                await store.SaveAsync(current with { ClientInformation = JsonData.Parse(client) }).ConfigureAwait(false);
            }

            Uri? authorizationUrl = null; string? state = null;
            host = McpDefaultOAuthHost.Install(new(options.ServerUrl, new Uri(redirectUrl),
                McpDefaultOAuthClientMetadata.Create(settings.ClientName, redirectUrl, !string.IsNullOrEmpty(settings.ClientSecret)), store,
                options.UnixMilliseconds, _ => ValueTask.FromResult(options.RandomBytes(32)),
                (url, _) => { authorizationUrl = url; return ValueTask.CompletedTask; },
                (returned, _) => returned is not null && returned == state ? ValueTask.CompletedTask
                    : ValueTask.FromException(new InvalidOperationException("The redirect URL belongs to a different sign-in")),
                options.Http, (endpoint, _) => endpoint.Scheme is "http" or "https", new(),
                AuthorizationState: async _ =>
                {
                    // provider.state(): a stored state, or 32 random bytes as hex, stored for the response.
                    var current = await store.LoadAsync().ConfigureAwait(false);
                    if (current?.OAuthState is { Length: > 0 } existing) return state = existing;
                    state = Convert.ToHexString(options.RandomBytes(32)).ToLowerInvariant();
                    await store.SaveAsync((current ?? new McpOAuthState(options.ServerUrl.AbsoluteUri)) with { OAuthState = state }).ConfigureAwait(false);
                    return state;
                },
                ClientMetadataDocumentBase: cimd ? ClientMetadataBaseUrl : null,
                AuthorizationServerMetadataUrl: settings.AuthServerMetadataUrl, RequestTimeout: options.RequestTimeout));
            // A server asking for more scope gets it on top of the configured scope and, since the challenge may list only the
            // missing scopes, on top of the scope granted so far. A refresh keeps the granted scope, so a step-up skips it.
            var challenge = options.Challenge; var stepUp = challenge?.IsStepUp == true;
            var scope = MergeScopes(settings.Scope, stepUp ? McpOAuthScope.StepUp(stored?.Tokens?.Scope, challenge!.Scope) : challenge?.Scope);
            host.UseChallenge(challenge?.ResourceMetadataUrl);
            if ((await host.AuthorizeAsync(host.Options(scope: scope, skipRefresh: stepUp), token).ConfigureAwait(false)).Outcome == McpOAuthAuthorizationOutcome.Authorized) return;
            if (authorizationUrl is null || state is null) throw new InvalidOperationException("OAuth flow did not produce an authorization URL");
            // The flow picks the redirect URI, which may be specific to the MCP server.
            var authorizationRedirectUrl = new Uri(McpOAuthCallbackServer.Query(authorizationUrl.Query.TrimStart('?'), "redirect_uri") ?? redirectUrl);
            options.Prompt.ShowAuthorizationUrl(authorizationUrl);
            var response = await WaitForAuthorizationResponseAsync(callback, state, authorizationRedirectUrl, options.Prompt, token).ConfigureAwait(false);
            await host.CompleteAuthorizationAsync(response.Code, response.State, scope, token, response.Iss).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Aborted requests fail with the cancellation; report them as the cancellation they are.
            failure = token.IsCancellationRequested ? new McpSignInCancelledException() : error;
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
        finally
        {
            await callback.DisposeAsync().ConfigureAwait(false);
            // The host's close re-reports its last failed operation, which the caller already has.
            if (host is not null)
                try { await host.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) when (failure is not null) { }
        }
    }

    /// <summary>Listen on the port, or on a free port when it is taken and not required.</summary>
    private static McpOAuthCallbackServer Listen(CallbackSettings settings, string[] extraPaths, int? port, bool required)
    {
        try { return McpOAuthCallbackServer.Listen(settings.Host, port ?? 0, settings.Path, settings.RedirectHost, extraPaths); }
        catch (System.Net.Sockets.SocketException) when (!required && port is not null)
        { return McpOAuthCallbackServer.Listen(settings.Host, 0, settings.Path, settings.RedirectHost, extraPaths); }
    }

    /// <summary>The browser callback or a pasted redirect URL, whichever comes first.</summary>
    private static async Task<McpOAuthCallback> WaitForAuthorizationResponseAsync(McpOAuthCallbackServer callback, string state,
        Uri redirectUrl, McpSignInPrompt prompt, CancellationToken token)
    {
        using var controller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var fromBrowser = callback.WaitForCallbackAsync(state, redirectUrl.AbsolutePath);
        var fromUser = FromUserAsync();
        try { return await await Task.WhenAny(fromBrowser, fromUser).ConfigureAwait(false); }
        finally
        {
            // The losing side fails once the prompt is cancelled or the callback server closes.
            await controller.CancelAsync().ConfigureAwait(false);
            _ = fromBrowser.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            _ = fromUser.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        }
        async Task<McpOAuthCallback> FromUserAsync()
        {
            await Task.Yield();
            var input = await prompt.PromptForRedirectUrl(controller.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input)) throw new McpSignInCancelledException();
            return ResponseFromRedirectUrl(input, state, redirectUrl);
        }
    }

    internal static McpOAuthCallback ResponseFromRedirectUrl(string input, string state, Uri redirectUrl)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var url)) throw new InvalidOperationException("Expected the full redirect URL from the browser address bar");
        // A server-specific redirect URI tells authorization servers apart, so it must match exactly.
        if (url.GetLeftPart(UriPartial.Authority) != redirectUrl.GetLeftPart(UriPartial.Authority) || url.AbsolutePath != redirectUrl.AbsolutePath)
            throw new InvalidOperationException("The redirect URL does not match this sign-in's redirect URI");
        var query = url.Query.TrimStart('?');
        if (McpOAuthCallbackServer.Query(query, "error") is { Length: > 0 } error)
            throw new InvalidOperationException(McpOAuthCallbackServer.Query(query, "error_description") ?? error);
        if (McpOAuthCallbackServer.Query(query, "state") != state) throw new InvalidOperationException("The redirect URL belongs to a different sign-in");
        if (McpOAuthCallbackServer.Query(query, "code") is not { Length: > 0 } code) throw new InvalidOperationException("The redirect URL does not contain an authorization code");
        var iss = McpOAuthCallbackServer.Query(query, "iss");
        return new(code, state, iss);
    }
}
