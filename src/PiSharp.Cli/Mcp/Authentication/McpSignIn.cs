// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts (McpOAuthSettings,
// callbackSettings, mergeScopes, offersClientMetadataDocument, createProvider's clientMetadataDocument, signInMcpServer,
// waitForAuthorizationResponse, responseFromRedirectUrl, listenForCallback), packages/coding-agent/src/extensions/mcp/runtime.ts
// oauthSettings and packages/mcp/src/oauth/provider.ts state(). Intentional difference (owner decision 2026-11-05, "Use safe
// defaults"): PiSharp is treated as a rebranded build, so `auto` never offers pi's Client ID Metadata Document (upstream's
// rebrand guard reads APP_NAME, which is "pi" in PiSharp); see OffersClientMetadataDocument.
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
    Uri? AuthServerMetadataUrl = null) : McpOAuthClientSettings(ClientId, ClientSecret, CallbackPort, CallbackUrl,
        Scope, ClientName, ClientRegistration, AuthServerMetadataUrl)
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
    public Func<CancellationToken, Task<IAsyncDisposable>>? RefreshLock { get; init; }
    public string ClientName { get; init; } = McpDefaultOAuthClientMetadata.DefaultClientName;
    public McpClientMetadataDocuments? ClientMetadataDocuments { get; init; }
}

/// <summary>Sign-in to an MCP server: the stored refresh token when possible, otherwise the browser authorization code
/// flow against a loopback callback (or a pasted redirect URL). Tokens are saved to the store. Cancelling stops the
/// sign-in at any step with <see cref="McpSignInCancelledException"/>.</summary>
public static class McpSignIn
{
    private const string CallbackHost = "127.0.0.1";
    private const string CallbackPath = "/callback";
    /// <summary>Where pi.dev serves pi's Client ID Metadata Documents: `client.json` and `&lt;callback ID&gt;/client.json`. Only an
    /// explicit `oauth.clientRegistration: "cimd"` presents them, as upstream's does in a rebranded build.</summary>
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

    /// <summary>
    /// <c>offersClientMetadataDocument</c>: whether sign-in may identify with pi's Client ID Metadata Document instead of registering.
    /// Upstream's <c>auto</c> (the default) offers it unless <c>clientId</c> or <c>clientName</c> is set, the callback is not one the
    /// documents list, or <c>APP_NAME</c> is not "pi". PiSharp's APP_NAME is "pi", but it is not pi, so (owner decision 2026-11-05)
    /// it takes the rebranded-build branch: <c>auto</c> never offers the document and registers dynamically, or keeps a registered
    /// client. <c>dcr</c> never offers it; an explicit <c>cimd</c> always does, as upstream's does for every build.
    /// </summary>
    internal static bool OffersClientMetadataDocument(McpOAuthSettings settings) => (settings.ClientRegistration ?? "auto") == "cimd";

    internal static ImmutableArray<string> RegisteredRedirectUrls(JsonData? client) =>
        client is { } value && value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("redirect_uris", out var uris) &&
            uris.ValueKind == JsonValueKind.Array ? uris.EnumerateArray().Where(uri => uri.ValueKind == JsonValueKind.String).Select(uri => uri.GetString()!).ToImmutableArray() : [];

    public static async Task SignInAsync(McpSignInOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (token.IsCancellationRequested) throw new McpSignInCancelledException();
        var settings = options.Settings;
        var stored = await options.Store.LoadAsync().ConfigureAwait(false);
        var callbackOptions = Callback(settings);
        var registered = RegisteredRedirectUrls(stored?.ClientInformation).FirstOrDefault();
        var preferredPort = callbackOptions.Port ?? (registered is not null && Uri.TryCreate(registered, UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : null);
        var client = McpSignInClient.Create(options);
        var offers = McpOAuthSignIn.OffersDocument(settings, client.ClientMetadataDocuments, settings.CallbackUrl ?? "http://127.0.0.1/callback");
        await using var callback = Listen(callbackOptions, offers
            ? [McpOAuthClientMetadataDocuments.ServerSpecificPath(callbackOptions.Path, options.ServerUrl)] : [],
            preferredPort, callbackOptions.Port is not null);
        var redirect = callbackOptions.FixedRedirectUrl ?? callback.RedirectUrl;
        var signIn = new McpOAuthSignIn();
        var startOptions = new PiSharp.Extensions.Mcp.Authentication.McpSignInOptions(client, redirect, options.Challenge);
        try
        {
            var start = await signIn.StartAsync(startOptions, token).ConfigureAwait(false);
            if (start.Redirect is not { } authorization) return;
            options.Prompt.ShowAuthorizationUrl(new Uri(authorization.AuthorizationUrl));
            var response = await WaitForAuthorizationResponseAsync(callback, authorization.State,
                new Uri(authorization.RedirectUrl), options.Prompt, token).ConfigureAwait(false);
            await signIn.CompleteAsync(startOptions, authorization, new(response.Code, response.State, response.Iss), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new McpSignInCancelledException(); }
        finally { await signIn.SettledAsync().ConfigureAwait(false); }
    }

    /// <summary>Listen on the port, or on a free port when it is taken and not required.</summary>
    private static McpOAuthCallbackServer Listen(CallbackSettings settings, string[] extraPaths, int? port, bool required)
    {
        try { return McpOAuthCallbackServer.Listen(settings.Host, port ?? 0, settings.Path, settings.RedirectHost, extraPaths); }
        catch (System.Net.Sockets.SocketException) when (!required && port is not null)
        { return McpOAuthCallbackServer.Listen(settings.Host, 0, settings.Path, settings.RedirectHost, extraPaths); }
    }

    /// <summary>The browser callback or a pasted redirect URL, whichever comes first.</summary>
    public static async Task<McpOAuthCallback> WaitForAuthorizationResponseAsync(McpOAuthCallbackServer callback, string state,
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

    public static McpOAuthCallback ResponseFromRedirectUrl(string input, string state, Uri redirectUrl)
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
