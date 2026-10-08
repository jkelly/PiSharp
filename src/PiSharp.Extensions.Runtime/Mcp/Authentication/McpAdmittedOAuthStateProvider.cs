using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Bounded provider-state leaf of Pi v0.99.1 oauth/provider.ts. No transport,
/// browser, random state generation, token exchange, secret lookup or default storage.</summary>
public sealed class McpAdmittedOAuthStateProvider
{
    private readonly string serverUrl;
    private readonly IMcpAdmittedOAuthStateStore store;
    private readonly Func<double> unixMilliseconds;
    private readonly object gate = new();
    private Task writes = Task.CompletedTask;

    public McpAdmittedOAuthStateProvider(Uri serverUrl, IMcpAdmittedOAuthStateStore store,
        Func<double> unixMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (!serverUrl.IsAbsoluteUri) throw new ArgumentException("Absolute server URL required.", nameof(serverUrl));
        this.serverUrl = serverUrl.AbsoluteUri;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.unixMilliseconds = unixMilliseconds ?? throw new ArgumentNullException(nameof(unixMilliseconds));
    }

    public async Task<McpOAuthState> ReadAsync()
    {
        Task pending;
        lock (gate) pending = writes;
        await pending.ConfigureAwait(false);
        return Own(await LoadAsync().ConfigureAwait(false));
    }

    public Task SaveTokensAsync(McpOAuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        // Like the original, expiry is captured at invocation, before the serialized update.
        var expiry = tokens.ExpiresIn is { } seconds ? unixMilliseconds() + seconds * 1000 : (double?)null;
        return UpdateAsync(value => value with { Tokens = tokens, TokensExpireAt = expiry });
    }

    public Task SaveCodeVerifierAsync(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return UpdateAsync(value => value with { CodeVerifier = verifier });
    }

    public async Task<string> CodeVerifierAsync()
    {
        var verifier = (await ReadAsync().ConfigureAwait(false)).CodeVerifier;
        return !string.IsNullOrEmpty(verifier) ? verifier : throw new InvalidOperationException("No OAuth PKCE code verifier is stored");
    }

    public Task SaveClientInformationAsync(JsonData information) =>
        UpdateAsync(value => value with { ClientInformation = information });

    public Task SaveDiscoveryAsync(JsonData discovery) => UpdateAsync(value => value with { Discovery = discovery });

    public Task InvalidateAsync(McpOAuthInvalidation kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return UpdateAsync(value => kind switch
        {
            McpOAuthInvalidation.All => new(serverUrl),
            McpOAuthInvalidation.Client => value with { ClientInformation = null },
            McpOAuthInvalidation.Tokens => value with { Tokens = null, TokensExpireAt = null },
            McpOAuthInvalidation.Verifier => value with { CodeVerifier = null },
            McpOAuthInvalidation.Discovery => value with { Discovery = null },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
    }

    private McpOAuthState Own(McpOAuthState? value) => value?.ServerUrl == serverUrl ? value : new(serverUrl);

    private Task UpdateAsync(Func<McpOAuthState, McpOAuthState> update)
    {
        lock (gate)
        {
            writes = ExecuteUpdateAsync(writes, update);
            return writes;
        }
    }

    private async Task ExecuteUpdateAsync(Task previous, Func<McpOAuthState, McpOAuthState> update)
    {
        // Publish the tracked write before invoking caller code, even for synchronous stores.
        await Task.Yield();
        await previous.ConfigureAwait(false);
        var next = update(Own(await LoadAsync().ConfigureAwait(false)));
        Task? original = null;
        try { original = store.SaveAsync(next).AsTask(); await original.ConfigureAwait(false); }
        catch (Exception error) { throw new McpOAuthStoreException(original, original is { IsFaulted: true } ? original.Exception! : error); }
    }

    private async Task<McpOAuthState?> LoadAsync()
    {
        Task<McpOAuthState?>? original = null;
        try { original = store.LoadAsync().AsTask(); return await original.ConfigureAwait(false); }
        catch (Exception error) { throw new McpOAuthStoreException(original, original is { IsFaulted: true } ? original.Exception! : error); }
    }
}
