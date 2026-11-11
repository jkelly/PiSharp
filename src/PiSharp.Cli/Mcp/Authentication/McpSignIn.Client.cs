// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/oauth/sign-in.ts.
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.Cli.Mcp.Authentication;

public sealed record McpLoopbackCallback(McpOAuthCallbackServer Callback, string RedirectUrl);

/// <summary>Composes reusable MCP OAuth with the application's explicitly supplied HTTP and storage.</summary>
public static class McpSignInClient
{
    public static async Task<McpLoopbackCallback> ListenForLoopbackSignInAsync(Uri serverUrl,
        IMcpAdmittedOAuthStateStore store, McpOAuthClientSettings settings, McpClientMetadataDocuments? documents = null)
    {
        var url = new Uri(settings.CallbackUrl ?? "http://127.0.0.1/callback");
        var host = url.Host.Trim('[', ']');
        var configuredPort = url.IsDefaultPort ? settings.CallbackPort : url.Port;
        var state = await store.LoadAsync().ConfigureAwait(false);
        var registered = state is null ? null : McpOAuthSignIn.RegisteredRedirectUrls(state).FirstOrDefault();
        var port = configuredPort ?? (registered is null ? null : new Uri(registered) is { IsDefaultPort: false } stored ? stored.Port : null);
        var extra = McpOAuthSignIn.OffersDocument(settings, documents, url.AbsoluteUri)
            ? new[] { McpOAuthClientMetadataDocuments.ServerSpecificPath(url.AbsolutePath, serverUrl) } : [];
        McpOAuthCallbackServer callback;
        try { callback = McpOAuthCallbackServer.Listen(host == "localhost" ? "127.0.0.1" : host, port ?? 0, url.AbsolutePath, host, extra); }
        catch (System.Net.Sockets.SocketException) when (configuredPort is null && port is not null)
        { callback = McpOAuthCallbackServer.Listen(host == "localhost" ? "127.0.0.1" : host, 0, url.AbsolutePath, host, extra); }
        return new(callback, McpOAuthSignIn.FixedRedirect(settings) ?? callback.RedirectUrl);
    }

    public static async Task<McpAuthorizationResponse> WaitForLoopbackSignInAsync(McpLoopbackCallback loopback,
        McpSignInRedirect authorization, Func<CancellationToken, Task<string?>>? prompt = null, CancellationToken token = default)
    {
        try
        {
            var response = prompt is null
                ? await loopback.Callback.WaitForCallbackAsync(authorization.State, new Uri(authorization.RedirectUrl).AbsolutePath).WaitAsync(token).ConfigureAwait(false)
                : await McpSignIn.WaitForAuthorizationResponseAsync(loopback.Callback, authorization.State, new Uri(authorization.RedirectUrl), new(_ => { }, prompt), token).ConfigureAwait(false);
            return new(response.Code, response.State, response.Iss);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new McpSignInCancellationException(); }
    }

    public static McpOAuthClientOptions Create(McpSignInOptions options)
    {
        var documents = options.ClientMetadataDocuments ?? (options.Settings.ClientRegistration == "cimd"
            ? new(McpSignIn.ClientMetadataBaseUrl, url => McpConfigurationCallback(url)) : null);
        return new(options.ServerUrl, options.Store, options.Settings, options.ClientName, Flow,
            options.RefreshLock ?? (_ => Task.FromResult<IAsyncDisposable>(new NoLock())), documents);

        async Task<McpSignInFlowResult> Flow(McpSignInFlowRequest request, CancellationToken token)
        {
            var settings = options.Settings;
            if (settings.ClientId is { } id)
            {
                var current = await options.Store.LoadAsync().ConfigureAwait(false) ?? new(options.ServerUrl.AbsoluteUri);
                var information = JsonData.Parse(JsonSerializer.Serialize(new { client_id = id, client_secret = settings.ClientSecret }));
                await options.Store.SaveAsync(current with { ClientInformation = information }).ConfigureAwait(false);
            }
            Uri? authorizationUrl = null;
            var offers = McpOAuthSignIn.OffersDocument(settings, documents, request.RedirectUrl);
            McpOAuthClientMetadataDocument? Document(JsonData? metadata)
            {
                if (documents?.ListsRedirectUrl(request.RedirectUrl) != true)
                    throw new InvalidOperationException($"oauth.clientRegistration \"cimd\" needs Client ID Metadata Documents that list the redirect URI {request.RedirectUrl}");
                var document = McpOAuthClientMetadataDocuments.TryCreate(documents.BaseUrl, options.ServerUrl, request.RedirectUrl, metadata);
                if (document is null && settings.ClientRegistration == "cimd")
                    throw new McpOAuthProtocolException("client_metadata_unsupported", "The authorization server does not support Client ID Metadata Documents for public clients; remove oauth.clientRegistration \"cimd\"");
                return document;
            }
            var host = McpDefaultOAuthHost.Install(new(options.ServerUrl, new Uri(request.RedirectUrl),
                McpDefaultOAuthClientMetadata.Create(settings.ClientName ?? options.ClientName, request.RedirectUrl, settings.ClientSecret is { Length: > 0 }),
                options.Store, options.UnixMilliseconds, _ => ValueTask.FromResult(options.RandomBytes(32)),
                (url, _) => { authorizationUrl = url; return ValueTask.CompletedTask; },
                (_, _) => ValueTask.CompletedTask, options.Http, (endpoint, _) => endpoint.Scheme is "http" or "https", new(),
                AuthorizationState: async _ =>
                {
                    var current = await options.Store.LoadAsync().ConfigureAwait(false) ?? new(options.ServerUrl.AbsoluteUri);
                    if (current.OAuthState is { } existing) return existing;
                    var state = Convert.ToHexString(options.RandomBytes(32)).ToLowerInvariant();
                    await options.Store.SaveAsync(current with { OAuthState = state }).ConfigureAwait(false);
                    return state;
                }, AuthorizationServerMetadataUrl: settings.AuthServerMetadataUrl, RequestTimeout: options.RequestTimeout,
                ClientMetadataDocument: offers ? Document : null));
            Exception? failure = null;
            try
            {
                host.UseChallenge(request.Challenge?.ResourceMetadataUrl);
                var result = request.Code is { } code
                    ? await host.CompleteAuthorizationAsync(code, null, request.Scope, token, request.Iss, request.AuthorizationRedirectUrl).ConfigureAwait(false)
                    : await host.AuthorizeAsync(host.Options(scope: request.Scope, skipRefresh: request.SkipRefresh, refreshOnly: request.RefreshOnly), token).ConfigureAwait(false);
                return new(result.Outcome, authorizationUrl);
            }
            catch (Exception error) { failure = error; throw; }
            finally { try { await host.DisposeAsync().ConfigureAwait(false); } catch when (failure is not null) { } }
        }
    }

    private static bool McpConfigurationCallback(string value) => Uri.TryCreate(value, UriKind.Absolute, out var url) &&
        url.Scheme == "http" && (url.Host is "localhost" or "127.0.0.1") && url.AbsolutePath == "/callback" &&
        url.Query.Length == 0 && url.Fragment.Length == 0;
    private sealed class NoLock : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
