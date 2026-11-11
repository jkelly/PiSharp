// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/oauth/auth-provider.ts.
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Stored-token authentication, with one refresh per process and a caller-admitted cross-process lock.</summary>
public sealed class McpOAuthAuthProvider(IMcpAdmittedOAuthStateStore store, Func<McpOAuthClientOptions> client,
    Action<McpOAuthChallenge>? onChallenge = null, Func<double>? unixMilliseconds = null, string? redirectUrl = null)
{
    private readonly object gate = new();
    private Task? refreshing;
    public McpAdmittedHttpAuthentication Authentication => new(token => new(TokenAsync(token)),
        (context, token) => new(UnauthorizedAsync(context, token)));

    public async Task SettledAsync()
    { Task? pending; lock (gate) pending = refreshing; if (pending is not null) try { await pending.ConfigureAwait(false); } catch { } }

    private Task Refresh(string? staleToken, McpOAuthChallenge? challenge = null)
    { lock (gate) return refreshing ??= Run();
        async Task Run()
        {
            await Task.Yield();
            try
            {
                var options = client();
                await using var held = await options.RefreshLock(CancellationToken.None).ConfigureAwait(false);
                var state = await store.LoadAsync().ConfigureAwait(false);
                if (state?.Tokens?.AccessToken != staleToken) return;
                if (state?.Tokens?.RefreshToken is null) throw new McpOAuthAuthorizationRequiredException();
                var result = await McpOAuthSignIn.RefreshStoredAsync(options, state, redirectUrl, challenge).ConfigureAwait(false);
                if (result.Outcome != McpOAuthAuthorizationOutcome.Authorized) throw new McpOAuthAuthorizationRequiredException();
            }
            finally { lock (gate) refreshing = null; }
        }
    }

    private async Task<string?> TokenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await SettledAsync().ConfigureAwait(false);
        var state = await store.LoadAsync().ConfigureAwait(false);
        var now = unixMilliseconds?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (state?.TokensExpireAt is { } expires && expires - 30_000 <= now && state.Tokens?.RefreshToken is not null)
            try { await Refresh(state.Tokens.AccessToken).ConfigureAwait(false); } catch { }
        token.ThrowIfCancellationRequested();
        return (await store.LoadAsync().ConfigureAwait(false))?.Tokens?.AccessToken;
    }

    private async Task UnauthorizedAsync(McpHttpUnauthorizedContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var challenge = McpOAuthChallenge.Parse(context.Response.Headers.TryGetValues("WWW-Authenticate", out var values) ? string.Join(',', values) : null);
        onChallenge?.Invoke(challenge);
        if (challenge.IsStepUp) throw new McpOAuthAuthorizationRequiredException();
        await Refresh(context.RejectedToken, challenge).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}
