// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/oauth/sign-in.ts.
using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Split sign-in. Persist Redirect and reuse the same store when completing in another request.</summary>
public sealed class McpOAuthSignIn
{
    private readonly ConcurrentDictionary<Task, byte> pending = new();

    /// <summary>Joins refreshes that outlived a cancelled caller, so rotated credentials reach storage.</summary>
    public async Task SettledAsync()
    {
        while (!pending.IsEmpty)
            foreach (var operation in pending.Keys) try { await operation.ConfigureAwait(false); } catch { }
    }

    public async Task<McpSignInStart> StartAsync(McpSignInOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Canceled(token);
        var client = options.Client;
        if (options.Challenge?.IsStepUp != true)
        {
            var refresh = RefreshBeforeSignIn();
            Track(refresh);
            try
            {
                if (await refresh.WaitAsync(token).ConfigureAwait(false) == McpOAuthAuthorizationOutcome.Authorized) return new();
            }
            catch (McpRefreshLockBusyException) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new McpSignInCancellationException(); }
        }
        Canceled(token);
        var stored = await client.Store.LoadAsync().ConfigureAwait(false);
        if (stored is not null)
        {
            var keep = client.Settings.ClientId is not null || (stored.ClientInformation is not null
                ? client.Settings.ClientRegistration != "cimd" && RegisteredRedirectUrls(stored).Contains(options.RedirectUrl, StringComparer.Ordinal)
                : OffersDocument(client.Settings, client.ClientMetadataDocuments, options.RedirectUrl));
            await client.Store.SaveAsync(keep ? stored with { OAuthState = null } : stored with
            { OAuthState = null, ClientInformation = null, Tokens = null, TokensExpireAt = null }).ConfigureAwait(false);
        }
        var scope = MergeScopes(client.Settings.Scope, options.Challenge?.IsStepUp == true
            ? McpOAuthScope.StepUp(stored?.Tokens?.Scope, options.Challenge.Scope) : options.Challenge?.Scope);
        var result = await Cancelable(() => client.Flow(new(options.RedirectUrl, SkipRefresh: true, Scope: scope,
            Challenge: options.Challenge), token), token).ConfigureAwait(false);
        var url = result.AuthorizationUrl ?? throw new InvalidOperationException("OAuth flow did not produce an authorization URL");
        var state = (await client.Store.LoadAsync().ConfigureAwait(false))?.OAuthState
            ?? throw new InvalidOperationException("OAuth flow did not persist its state");
        return new(new(url.AbsoluteUri, Query(url.Query, "redirect_uri") ?? options.RedirectUrl, state, Query(url.Query, "scope")));

        async Task<McpOAuthAuthorizationOutcome> RefreshBeforeSignIn()
        {
            await using var held = await client.RefreshLock(token).ConfigureAwait(false);
            // A queued acquisition can succeed after WaitAsync returned cancellation.
            if (token.IsCancellationRequested) return McpOAuthAuthorizationOutcome.Redirect;
            var state = await client.Store.LoadAsync().ConfigureAwait(false);
            if (state?.Tokens?.RefreshToken is null || client.Settings.ClientRegistration == "cimd" &&
                state.ClientInformation is not null && client.Settings.ClientId is null) return McpOAuthAuthorizationOutcome.Redirect;
            return (await RefreshStoredAsync(client, state, options.RedirectUrl, options.Challenge).ConfigureAwait(false)).Outcome;
        }
    }

    public async Task CompleteAsync(McpSignInOptions options, McpSignInRedirect authorization,
        McpAuthorizationResponse response, CancellationToken token = default)
    {
        Canceled(token);
        if (response.State != authorization.State) throw new InvalidOperationException("The authorization response belongs to a different sign-in");
        if ((await options.Client.Store.LoadAsync().ConfigureAwait(false))?.OAuthState != authorization.State)
            throw new InvalidOperationException("Another sign-in to this MCP server started since; sign in again");
        await Cancelable(() => options.Client.Flow(new(options.RedirectUrl, Scope: authorization.Scope, Code: response.Code,
            Iss: response.Iss, AuthorizationRedirectUrl: authorization.RedirectUrl, Challenge: options.Challenge), token), token).ConfigureAwait(false);
    }

    public static Task<McpSignInFlowResult> RefreshStoredAsync(McpOAuthClientOptions client, McpOAuthState state,
        string? redirectUrl = null, McpOAuthChallenge? challenge = null)
    {
        var fixedRedirect = FixedRedirect(client.Settings);
        var candidates = new[] { redirectUrl, fixedRedirect, RegisteredRedirectUrls(state).FirstOrDefault() };
        var selected = candidates.FirstOrDefault(url => url is not null && client.ClientMetadataDocuments?.ListsRedirectUrl(url) == true)
            ?? "http://127.0.0.1/callback";
        // A refresh must persist a rotated token even when the request that needed it is cancelled.
        return client.Flow(new(selected, RefreshOnly: true, Challenge: challenge), CancellationToken.None);
    }

    public static bool OffersDocument(McpOAuthClientSettings settings, McpClientMetadataDocuments? documents, string redirectUrl) =>
        (settings.ClientRegistration ?? "auto") switch
        {
            "cimd" => true,
            "auto" => settings.ClientId is null && settings.ClientName is null && documents?.ListsRedirectUrl(redirectUrl) == true,
            _ => false
        };

    public static string? FixedRedirect(McpOAuthClientSettings settings)
    {
        var url = new Uri(settings.CallbackUrl ?? "http://127.0.0.1/callback");
        if (!url.IsDefaultPort) return settings.CallbackUrl;
        return settings.CallbackPort is { } port ? new UriBuilder(url) { Port = port }.Uri.AbsoluteUri : null;
    }

    public static IReadOnlyList<string> RegisteredRedirectUrls(McpOAuthState state) =>
        state.ClientInformation is { } client && client.Value.ValueKind == JsonValueKind.Object &&
        client.Value.TryGetProperty("redirect_uris", out var uris) && uris.ValueKind == JsonValueKind.Array
            ? uris.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray() : [];

    public static McpAuthorizationResponse ResponseFromRedirectUrl(string input, McpSignInRedirect authorization)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var url)) throw new InvalidOperationException("Expected the full redirect URL from the browser address bar");
        var expected = new Uri(authorization.RedirectUrl);
        if (url.GetLeftPart(UriPartial.Authority) != expected.GetLeftPart(UriPartial.Authority) || url.AbsolutePath != expected.AbsolutePath)
            throw new InvalidOperationException("The redirect URL does not match this sign-in's redirect URI");
        if (Query(url.Query, "error") is { Length: > 0 } error) throw new InvalidOperationException(Query(url.Query, "error_description") ?? error);
        var state = Query(url.Query, "state");
        if (state != authorization.State) throw new InvalidOperationException("The redirect URL belongs to a different sign-in");
        var code = Query(url.Query, "code");
        if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("The redirect URL does not contain an authorization code");
        return new(code, state!, Query(url.Query, "iss"));
    }

    private void Track(Task task)
    {
        pending.TryAdd(task, 0);
        _ = task.ContinueWith(completed => { _ = completed.Exception; pending.TryRemove(completed, out _); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static void Canceled(CancellationToken token) { if (token.IsCancellationRequested) throw new McpSignInCancellationException(); }
    private static async Task<T> Cancelable<T>(Func<Task<T>> action, CancellationToken token)
    { try { Canceled(token); return await action().ConfigureAwait(false); } catch (Exception) when (token.IsCancellationRequested) { throw new McpSignInCancellationException(); } }
    private static string? MergeScopes(params string?[] scopes)
    {
        var values = scopes.SelectMany(scope => scope?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? []).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? null : string.Join(' ', values);
    }
    private static string? Query(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0].Replace('+', ' ')) == name)
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return null;
    }
}
