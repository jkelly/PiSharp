// Decision 0011 (run W2, WU6): one account's credential as PiSharp.ModelRuntime's credential seam (IProviderCredentialSource), so the live
// routes, the model registry and StoredOAuthLifecycle read that account and nothing else. Over WU5's vault, one source per account:
//  - an API key account is an api_key entry (its key escaped, so a key starting with "!" or holding "$" is sent as typed and never runs a
//    command or reads a variable), with its non-secret settings as the entry's env; a settings-only account (an AWS profile) has no key;
//  - a sign-in (OpenAI Codex, GitHub Copilot) is an oauth entry, read and refreshed through the vault's per-account lock, so a refresh never
//    crosses accounts and never writes to a removed one; a refresh the auth server rejects marks the account expired (after the lock is
//    released: the vault's write lock and an account lock are never held together), a network failure does not;
//  - an environment or ambient account has no stored entry: the route reads the credential from the account's own environment
//    (AccountEnvironment), which shows only that account's variable (or, for ambient credentials, the provider SDK's own chain).
// Anthropic never has an oauth entry here (API key or Amazon Bedrock only). Nothing in this file returns or logs a secret.
using System.Collections.Frozen;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Authentication;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.Routing;

/// <summary>One account's stored credential, for the routes of its provider only.</summary>
internal sealed class WebCredentialSource : IProviderCredentialSource
{
    /// <summary>The providers whose sign-ins are routed (the credentials area signs in to these only).</summary>
    internal static readonly FrozenSet<string> SignInProviders = new[] { "openai-codex", "github-copilot" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ICredentialVault vault;
    private readonly IAccountStore accounts;
    private readonly ILogger? log;
    private int failures;
    private volatile bool lastTransient;

    internal WebCredentialSource(AccountInfo account, ICredentialVault vault, IAccountStore accounts, ILogger? log)
    {
        ArgumentNullException.ThrowIfNull(account); ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(accounts);
        Account = account; this.vault = vault; this.accounts = accounts; this.log = log;
    }

    /// <summary>The account (as it was when this source was made).</summary>
    internal AccountInfo Account { get; }

    /// <summary>How many refreshes of this account failed so far (a request compares it before and after).</summary>
    internal int Failures => Volatile.Read(ref failures);

    /// <summary>Whether the last failed refresh was a network failure (the account was not marked expired).</summary>
    internal bool LastFailureTransient => lastTransient;

    private bool Owns(string provider) => provider == Account.Provider;

    private bool IsSignIn => Account.Kind == AccountKind.OAuth && SignInProviders.Contains(Account.Provider);

    /// <inheritdoc/>
    public async Task<StoredCredentialEntry?> ReadEntryAsync(string provider, CancellationToken cancellationToken)
    {
        if (!Owns(provider)) return null;
        switch (Account.Kind)
        {
            case AccountKind.ApiKey:
            {
                // Null: removed meanwhile. CredentialUnavailableException (nothing stored, an unreadable file) propagates to the route.
                var credential = await vault.ReadAsync(Account.Id, cancellationToken).ConfigureAwait(false);
                if (credential is null) return null;
                var settings = credential.Settings.Count == 0 ? null : credential.Settings.ToDictionary(StringComparer.Ordinal);
                return new StoredCredentialEntry("api_key", credential.ApiKey is { Length: > 0 } key ? Literal(key) : null, settings);
            }
            case AccountKind.OAuth when IsSignIn:
                return new StoredCredentialEntry("oauth", null, null);
            default:
                return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(string Provider, string Type)>> ListAsync(CancellationToken cancellationToken) =>
        await ReadEntryAsync(Account.Provider, cancellationToken).ConfigureAwait(false) is { } entry ? [(Account.Provider, entry.Type)] : [];

    /// <inheritdoc/>
    public Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken cancellationToken) =>
        Owns(provider) && IsSignIn ? vault.OAuthSource(Account.Id).ReadAsync(provider, cancellationToken) : Task.FromResult<OAuthCredentialSnapshot?>(null);

    /// <inheritdoc/>
    public async Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
        Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!Owns(provider) || !IsSignIn) return null;
        try
        {
            return await vault.OAuthSource(Account.Id).ModifyAsync(provider, mutation, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthLifecycleException error) when (error.Failure == OAuthLifecycleFailure.Refresh)
        {
            // The vault's account lock is released here, so marking the account (the vault's write lock) cannot deadlock with it.
            var transient = IsTransient(error.OriginalException);
            lastTransient = transient;
            Interlocked.Increment(ref failures);
            if (!transient)
            {
                try
                {
                    await accounts.MarkExpiredAsync(Account.Id, "The sign-in could not be refreshed (it was revoked or has expired); sign in again.",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (KeyNotFoundException) { /* Removed meanwhile: its chats fail as removed. */ }
            }
            log?.LogWarning("Refreshing the sign-in of account {Account} failed ({Kind}).", Account.Id, transient ? "network" : "rejected");
            throw;
        }
    }

    /// <summary><paramref name="key"/> as a stored value that resolves to itself: a configured value runs <c>!command</c> and reads
    /// <c>$NAME</c>, so <c>$</c> is doubled and a leading <c>!</c> is escaped as <c>$!</c>.</summary>
    internal static string Literal(string key)
    {
        var escaped = key.Replace("$", "$$", StringComparison.Ordinal);
        return escaped.StartsWith('!') ? "$" + escaped : escaped;
    }

    /// <summary>Whether a refresh failure is the network's (unreachable, timed out, a 5xx) rather than the auth server's rejection.</summary>
    internal static bool IsTransient(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException { StatusCode: null }:
                case HttpRequestException { StatusCode: { } status } when (int)status >= 500:
                case IOException or TimeoutException or OperationCanceledException:
                    return true;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(current.Message, @"\(5\d\d\)")) return true;
        }
        return false;
    }
}

/// <summary>
/// The environment one account's routes read: an API key account sees its own settings; an environment account sees its own variable; an
/// ambient account sees the host's environment except the provider's API key variables (those are environment accounts of their own). Every
/// account also sees a short list of non-secret configuration (regions, endpoints, home folders). Nothing else: an account never falls back to
/// another credential the host happens to have.
/// </summary>
internal static class AccountEnvironment
{
    /// <summary>Configuration every account may read from the host's environment: never a credential.</summary>
    internal static readonly FrozenSet<string> Configuration = new[]
    {
        "AWS_REGION", "AWS_DEFAULT_REGION", "AWS_CONFIG_FILE", "AWS_SHARED_CREDENTIALS_FILE", "AWS_USE_FIPS_ENDPOINT", "AWS_ENDPOINT_URL",
        "AWS_ENDPOINT_URL_BEDROCK_RUNTIME", "AWS_BEDROCK_FORCE_CACHE", "PI_CACHE_RETENTION", "AZURE_OPENAI_BASE_URL", "AZURE_OPENAI_RESOURCE_NAME",
        "AZURE_OPENAI_API_VERSION", "AZURE_OPENAI_DEPLOYMENT_NAME_MAP", "CLOUDFLARE_ACCOUNT_ID", "CLOUDFLARE_GATEWAY_ID", "GOOGLE_CLOUD_PROJECT",
        "GCLOUD_PROJECT", "GOOGLE_CLOUD_LOCATION", "CLOUDSDK_CONFIG", "APPDATA", "HOME", "USERPROFILE",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The environment reader of <paramref name="account"/> over the host's <paramref name="host"/>.</summary>
    internal static Func<string, string?> For(AccountInfo account, Func<string, string?> host)
    {
        ArgumentNullException.ThrowIfNull(account); ArgumentNullException.ThrowIfNull(host);
        switch (account.Kind)
        {
            case AccountKind.Ambient:
            {
                var keys = (ProviderEnvironmentKeys.GetApiKeyVariables(account.Provider) ?? []).ToFrozenSet(StringComparer.Ordinal);
                return name => keys.Contains(name) || name == CredentialVault.ExcludedVariable ? null : host(name);
            }
            case AccountKind.Environment:
            {
                var own = account.Source;
                return name => name == own && name != CredentialVault.ExcludedVariable || Configuration.Contains(name) ? host(name) : null;
            }
            default:
            {
                var settings = account.Settings;
                return name => settings.TryGetValue(name, out var value) ? value : Configuration.Contains(name) ? host(name) : null;
            }
        }
    }
}
