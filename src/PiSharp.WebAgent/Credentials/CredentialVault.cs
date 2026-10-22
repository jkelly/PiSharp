// Decision 0011 (run W2, WU5): the credential vault. Secrets live in credentials.json (CredentialFile: Data Protection, atomic replace);
// everything else about an account lives in webagent.db's accounts tables (migration 100): id, provider, label, kind, status, a masked
// hint, non-secret settings and details, timestamps, the last connection test, and each provider's active account. The two stores are
// written in an order that a crash cannot turn into a visible broken account: a new secret is written before its row (an orphan secret is
// pruned at the next start), and a row is deleted before its secret. Environment variables (ProviderEnvironmentKeys) and ambient
// credentials are synced into read-only rows once per process, since a process's environment cannot change; a variable that went away
// leaves its row "unavailable", so a chat bound to it fails clearly instead of moving to another account. Sign-ins are refreshed through
// OAuthSource(account), one lock per account, so two accounts of one provider never share a refresh. One class serves both interfaces:
// pages take IAccountStore (metadata), routing takes ICredentialVault (secrets, server-side only).
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Credentials;

/// <summary>Server-side secret reads for routing. Never hand what it returns to a page, a log or an SSE frame.</summary>
public interface ICredentialVault
{
    /// <summary>The credential of <paramref name="accountId"/> for a provider request: the decrypted key of an API key account (with its
    /// settings), the stored sign-in of an OAuth account (refresh it through <see cref="OAuthSource"/>), the variable's current value of an
    /// environment account, nothing for ambient credentials. Null for an unknown or removed account. Check
    /// <see cref="AccountInfo.Status"/> too: an expired account is returned, and must not be used.</summary>
    /// <exception cref="CredentialUnavailableException">The account exists but its credential cannot be had now (an unset variable, a value
    /// that no longer decrypts, a sign-in never stored).</exception>
    Task<AccountCredential?> ReadAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>The stored sign-in of <paramref name="accountId"/> as the source of a <see cref="StoredOAuthLifecycle"/>: reads and
    /// modifications are serialized per account (never per provider), a refresh is stored before it is returned, and a removed account is
    /// never written back. The <c>provider</c> argument must be the account's provider, or the source answers null.</summary>
    IAdmittedOAuthCredentialSource OAuthSource(string accountId);
}

/// <summary>An account's credential, read for one request. <see cref="ToString"/> never shows a secret.</summary>
public sealed class AccountCredential
{
    internal AccountCredential(AccountInfo account, string? apiKey, OAuthCredentialSnapshot? oauth)
    {
        Account = account;
        ApiKey = apiKey;
        OAuth = oauth;
    }

    /// <summary>The account (metadata).</summary>
    public AccountInfo Account { get; }

    /// <summary>The key of an API key or environment account; null for sign-ins, ambient credentials and settings-only accounts.</summary>
    [JsonIgnore] public string? ApiKey { get; }

    /// <summary>The non-secret settings stored with an API key account.</summary>
    public IReadOnlyDictionary<string, string> Settings => Account.Settings;

    /// <summary>The stored sign-in of an OAuth account, as last stored (it may be due for a refresh).</summary>
    [JsonIgnore] public OAuthCredentialSnapshot? OAuth { get; }

    /// <inheritdoc/>
    public override string ToString() => $"AccountCredential ({Account.Id}, {Account.Kind}) [redacted]";
}

/// <summary>The accounts (webagent.db) and their secrets (credentials.json). Registered once as <see cref="IAccountStore"/> and
/// <see cref="ICredentialVault"/>.</summary>
public sealed partial class CredentialVault : IAccountStore, ICredentialVault
{
    /// <summary>100: <c>accounts</c> (one row per account, no secret) and <c>active_accounts</c> (each provider's active account), the first
    /// migration of the accounts area.</summary>
    public static SqlMigration Migration { get; } = new(100, "accounts: accounts and active accounts", """
        CREATE TABLE IF NOT EXISTS accounts (
          id TEXT PRIMARY KEY,
          provider TEXT NOT NULL,
          label TEXT NOT NULL,
          kind TEXT NOT NULL CHECK (kind IN ('apiKey', 'oauth', 'environment', 'ambient')),
          status TEXT NOT NULL CHECK (status IN ('connected', 'expired', 'unavailable')),
          status_reason TEXT,
          identity TEXT,
          hint TEXT NOT NULL DEFAULT '',
          source TEXT,
          settings TEXT NOT NULL DEFAULT '{}',
          details TEXT NOT NULL DEFAULT '{}',
          expires_at INTEGER,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          last_used_at INTEGER,
          last_test TEXT
        );
        CREATE INDEX IF NOT EXISTS accounts_by_provider ON accounts (provider, created_at);
        CREATE UNIQUE INDEX IF NOT EXISTS accounts_by_identity ON accounts (provider, identity) WHERE identity IS NOT NULL;
        CREATE TABLE IF NOT EXISTS active_accounts (
          provider TEXT PRIMARY KEY,
          account_id TEXT NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
          updated_at INTEGER NOT NULL
        );
        """);

    /// <summary>How the area writes JSON (endpoints, stored settings and tests): camelCase, enums by their names, nulls left out.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The variable that is never offered as an account: an Anthropic subscription token (Anthropic is API key or Bedrock only).</summary>
    public const string ExcludedVariable = ProviderEnvironmentKeys.AnthropicOAuthToken;

    private const string Columns = "id, provider, label, kind, status, status_reason, identity, hint, source, settings, details, expires_at, " +
        "created_at, updated_at, last_used_at, last_test, (SELECT account_id FROM active_accounts WHERE active_accounts.provider = accounts.provider)";

    private const string Order = " ORDER BY provider, CASE kind WHEN 'oauth' THEN 0 WHEN 'apiKey' THEN 1 WHEN 'environment' THEN 2 ELSE 3 END, created_at, id";

    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly WebAgentDb db;
    private readonly CredentialFile file;
    private readonly CredentialsOptions options;
    private readonly ILogger log;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> accountLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> touched = new(StringComparer.Ordinal);
    private readonly Lock initGate = new();
    private volatile bool initialized;

    /// <summary>The vault of the app's data folder: <see cref="WebAgentOptions.CredentialsPath"/> and <paramref name="db"/>.</summary>
    public CredentialVault(WebAgentOptions webAgent, WebAgentDb db, IDataProtectionProvider protection, CredentialsOptions options,
        ILogger<CredentialVault>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(webAgent);
        ArgumentNullException.ThrowIfNull(protection);
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        log = logger ?? (ILogger)NullLogger.Instance;
        file = new CredentialFile(webAgent.CredentialsPath, protection, log, options.Time);
    }

    /// <inheritdoc/>
    public event EventHandler<AccountChangedEventArgs>? Changed;

    /// <summary>credentials.json.</summary>
    public string CredentialsPath => file.Path;

    // ---- IAccountStore: reads ----

    /// <inheritdoc/>
    public IReadOnlyList<AccountInfo> List(string? provider = null)
    {
        EnsureInitialized();
        var rows = provider is null
            ? db.Query($"SELECT {Columns} FROM accounts{Order};", ReadRow)
            : db.Query($"SELECT {Columns} FROM accounts WHERE provider = $provider{Order};", ReadRow, ("$provider", provider));
        return MarkActive(rows);
    }

    /// <inheritdoc/>
    public AccountInfo? Get(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return null;
        EnsureInitialized();
        return db.Scalar("SELECT provider FROM accounts WHERE id = $id;", ("$id", accountId)) is string provider
            ? List(provider).FirstOrDefault(account => account.Id == accountId) : null;
    }

    /// <inheritdoc/>
    public AccountInfo? Active(string provider) => string.IsNullOrEmpty(provider) ? null : List(provider).FirstOrDefault(account => account.IsActive);

    // ---- IAccountStore: writes ----

    /// <inheritdoc/>
    public async Task<AccountAdded> AddApiKeyAsync(ApiKeyAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = CheckProvider(request.Provider);
        if (SignInOnly(provider)) throw new InvalidOperationException($"{provider} connects by signing in; it does not take an API key.");
        var settings = CheckSettings(request.Settings);
        var key = CheckKey(request.ApiKey);
        if (key is null && settings.Count == 0) throw new ArgumentException("Enter an API key.");
        CheckSettingsHoldNoKey(settings, key);
        var label = string.IsNullOrWhiteSpace(request.Label) ? null : CheckLabel(request.Label);
        EnsureInitialized();
        var id = NewId();
        bool becameActive;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The secret first: a crash before the row leaves an orphan that the next start prunes, never a row without its key.
            if (key is not null) file.Write(id, SecretKind.ApiKey, key);
            try
            {
                becameActive = db.Transaction((connection, transaction) =>
                {
                    var rows = ProviderRows(connection, transaction, provider);
                    Insert(connection, transaction, id, provider, UniqueLabel(rows, label ?? DefaultKeyLabel(provider)), AccountKind.ApiKey,
                        SecretText.Mask(key), identity: null, source: null, settings, details: AccountInfo.EmptyMap, expiresAt: null);
                    return TakeActiveIfFirstStored(connection, transaction, provider, rows, id);
                });
            }
            catch
            {
                file.Remove(id);
                throw;
            }
        }
        finally { writes.Release(); }
        log.LogInformation("Stored API key account {Account} for {Provider}.", id, provider);
        Raise(id, provider, AccountChange.Added);
        return new AccountAdded(Get(id)!, becameActive);
    }

    /// <inheritdoc/>
    public async Task<AccountInfo> ReplaceApiKeyAsync(string accountId, string? apiKey, IReadOnlyDictionary<string, string>? settings = null,
        CancellationToken cancellationToken = default)
    {
        var key = CheckKey(apiKey);
        var newSettings = settings is null ? null : CheckSettings(settings);
        if (newSettings is not null) CheckSettingsHoldNoKey(newSettings, key);
        EnsureInitialized();
        AccountInfo account;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            account = Get(accountId) ?? throw new KeyNotFoundException($"No account {accountId}.");
            if (account.Kind != AccountKind.ApiKey)
                throw new InvalidOperationException($"“{account.Label}” is {KindText(account.Kind)}, not a stored API key.");
            if (key is null && (newSettings ?? account.Settings).Count == 0 && file.Read(accountId, SecretKind.ApiKey) is null)
                throw new ArgumentException("Enter an API key.");
            if (key is not null) file.Write(accountId, SecretKind.ApiKey, key);
            db.Execute("""
                UPDATE accounts SET hint = COALESCE($hint, hint), settings = COALESCE($settings, settings), status = 'connected',
                  status_reason = NULL, updated_at = $now WHERE id = $id;
                """, ("$id", accountId), ("$hint", key is null ? null : SecretText.Mask(key)),
                ("$settings", newSettings is null ? null : JsonSerializer.Serialize(newSettings, JsonOptions)), ("$now", NowMs()));
        }
        finally { writes.Release(); }
        Raise(accountId, account.Provider, AccountChange.CredentialReplaced);
        return Get(accountId)!;
    }

    /// <inheritdoc/>
    public async Task<AccountInfo> RenameAsync(string accountId, string label, CancellationToken cancellationToken = default)
    {
        var name = CheckLabel(label);
        EnsureInitialized();
        AccountInfo account;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            account = Get(accountId) ?? throw new KeyNotFoundException($"No account {accountId}.");
            if (account.ReadOnly) throw new InvalidOperationException($"“{account.Label}” comes from {SourceText(account)} and cannot be renamed here.");
            if (List(account.Provider).Any(other => other.Id != accountId && string.Equals(other.Label, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Another {account.Provider} account is called “{name}”.");
            db.Execute("UPDATE accounts SET label = $label, updated_at = $now WHERE id = $id;", ("$id", accountId), ("$label", name), ("$now", NowMs()));
        }
        finally { writes.Release(); }
        Raise(accountId, account.Provider, AccountChange.Renamed);
        return Get(accountId)!;
    }

    /// <inheritdoc/>
    public async Task<AccountInfo> SetActiveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        AccountInfo account;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            account = Get(accountId) ?? throw new KeyNotFoundException($"No account {accountId}.");
            if (account.Status == AccountStatus.Unavailable)
                throw new InvalidOperationException($"“{account.Label}” is unavailable ({account.StatusReason}), so it cannot be the active account.");
            db.Transaction((connection, transaction) => SetPointer(connection, transaction, account.Provider, accountId));
        }
        finally { writes.Release(); }
        Raise(accountId, account.Provider, AccountChange.Activated);
        return Get(accountId)!;
    }

    /// <inheritdoc/>
    public async Task<AccountRemoved> RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        AccountInfo removed;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            removed = Get(accountId) ?? throw new KeyNotFoundException($"No account {accountId}.");
            if (removed.ReadOnly && removed.Status != AccountStatus.Unavailable)
                throw new InvalidOperationException($"“{removed.Label}” comes from {SourceText(removed)}; remove it there and restart the app.");
            // Waits for a refresh of this account in flight, so it cannot write the secret back after it is removed.
            var gate = AccountLock(accountId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The row first: a crash before the secret goes leaves an orphan that the next start prunes, never a visible broken account.
                db.Transaction((connection, transaction) =>
                {
                    var rows = ProviderRows(connection, transaction, removed.Provider);
                    var wasActive = ActiveOf(rows)?.Id == accountId;
                    WebAgentDb.Execute(connection, transaction, "DELETE FROM accounts WHERE id = $id;", ("$id", accountId));
                    if (wasActive)
                    {
                        // The next stored account takes over; with none left, no choice is kept, so an environment variable may serve.
                        if (rows.FirstOrDefault(row => row.Id != accountId && !row.ReadOnly && row.Status != AccountStatus.Unavailable) is { } next)
                            SetPointer(connection, transaction, removed.Provider, next.Id);
                        else
                            WebAgentDb.Execute(connection, transaction, "DELETE FROM active_accounts WHERE provider = $provider;", ("$provider", removed.Provider));
                    }
                    return 0;
                });
                file.Remove(accountId);
            }
            finally { gate.Release(); }
        }
        finally { writes.Release(); }
        touched.TryRemove(accountId, out _);
        log.LogInformation("Removed account {Account} of {Provider}.", accountId, removed.Provider);
        Raise(accountId, removed.Provider, AccountChange.Removed);
        return new AccountRemoved(removed with { IsActive = false }, Active(removed.Provider));
    }

    /// <inheritdoc/>
    public async Task<AccountInfo> MarkExpiredAsync(string accountId, string reason, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        AccountInfo account;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            account = Get(accountId) ?? throw new KeyNotFoundException($"No account {accountId}.");
            db.Execute("UPDATE accounts SET status = 'expired', status_reason = $reason, updated_at = $now WHERE id = $id;",
                ("$id", accountId), ("$reason", SecretText.Scrub(reason) is { Length: > 0 } text ? text : "Rejected by the provider."), ("$now", NowMs()));
        }
        finally { writes.Release(); }
        log.LogInformation("Account {Account} of {Provider} is marked expired.", accountId, account.Provider);
        Raise(accountId, account.Provider, AccountChange.StatusChanged);
        return Get(accountId)!;
    }

    // ---- ICredentialVault ----

    /// <inheritdoc/>
    public Task<AccountCredential?> ReadAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = Get(accountId);
        if (account is null) return Task.FromResult<AccountCredential?>(null);
        if (account.Status == AccountStatus.Unavailable)
            throw new CredentialUnavailableException(accountId, account.StatusReason ?? $"“{account.Label}” is unavailable.");
        string? key = null;
        OAuthCredentialSnapshot? oauth = null;
        try
        {
            switch (account.Kind)
            {
                case AccountKind.ApiKey:
                    key = file.Read(accountId, SecretKind.ApiKey);
                    if (key is null && account.Settings.Count == 0) throw new CredentialUnavailableException(accountId, "No key is stored for it; replace the key.");
                    break;
                case AccountKind.OAuth:
                    oauth = ReadOAuth(accountId) ?? throw new CredentialUnavailableException(accountId, "No sign-in is stored for it; sign in again.");
                    break;
                case AccountKind.Environment:
                    key = ProviderEnvironmentKeys.Value(options.Environment, account.Source ?? "")
                        ?? throw new CredentialUnavailableException(accountId, $"{account.Source} is not set.");
                    break;
            }
        }
        catch (InvalidDataException)
        {
            throw new CredentialUnavailableException(accountId, "credentials.json could not be read, so no stored key or sign-in can be used; see the app's log.");
        }
        Touch(accountId);
        return Task.FromResult<AccountCredential?>(new AccountCredential(account, key, oauth));
    }

    /// <inheritdoc/>
    public IAdmittedOAuthCredentialSource OAuthSource(string accountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        return new AccountOAuthSource(this, accountId);
    }

    /// <summary>Re-reads environment variables and ambient credentials into their read-only accounts. It runs once per process by itself
    /// (a process's environment does not change); tests with their own <see cref="CredentialsOptions.Environment"/> call it again.</summary>
    public void SyncEnvironment()
    {
        var now = NowMs();
        foreach (var provider in options.EnvironmentProviders.Distinct(StringComparer.Ordinal))
        {
            foreach (var variable in ProviderEnvironmentKeys.GetApiKeyVariables(provider) ?? [])
            {
                if (variable == ExcludedVariable) continue;
                var value = ProviderEnvironmentKeys.Value(options.Environment, variable);
                SyncReadOnly(EnvironmentId(provider, variable), provider, AccountKind.Environment, variable, value is null ? null : SecretText.Mask(value),
                    variable, $"{variable} is not set.", now);
            }
            if (provider is "amazon-bedrock" or "google-vertex")
            {
                var found = ProviderEnvironmentKeys.GetEnvironmentApiKey(provider, options.Environment, options.FileExists, options.Home)
                    == ProviderEnvironmentKeys.AuthenticatedMarker;
                var (label, source) = provider == "amazon-bedrock"
                    ? ("AWS credentials", "AWS credential chain") : ("Google application-default credentials", "Application Default Credentials");
                SyncReadOnly("ambient-" + provider, provider, AccountKind.Ambient, label, found ? "" : null, source,
                    $"No {source} were found when the app started.", now);
            }
        }
    }

    /// <summary>The id of the environment account of <paramref name="variable"/> for <paramref name="provider"/>.</summary>
    public static string EnvironmentId(string provider, string variable) => $"env-{provider}-{variable.ToLowerInvariant()}";

    // ---- the sign-in flows' and the connection tester's entry points ----

    /// <summary>A completed sign-in, stored.</summary>
    internal sealed record SavedSignIn(AccountInfo Account, bool Reauthenticated, bool BecameActive);

    /// <summary>Stores a completed sign-in: into <paramref name="accountId"/> when the user signed that account in again (same provider and
    /// identity), else into the provider's account with the same <paramref name="identity"/>, else as a new account.</summary>
    /// <exception cref="InvalidOperationException">The sign-in is another identity than the account being signed in again; nothing is stored.</exception>
    internal async Task<SavedSignIn> SaveSignInAsync(string provider, OAuthCredentialSnapshot credential, string? identity,
        IReadOnlyDictionary<string, string> details, string label, string? accountId, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var serialized = CredentialFile.SerializeOAuth(credential);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountInfo? target = null;
            if (accountId is not null)
            {
                target = Get(accountId) ?? throw new KeyNotFoundException("The account being signed in again was removed.");
                if (target.Provider != provider || target.Kind != AccountKind.OAuth)
                    throw new InvalidOperationException($"“{target.Label}” is not a {provider} sign-in.");
                if (target.Identity is { } known && identity is not null && known != identity)
                    throw new InvalidOperationException($"That sign-in is a different account than “{target.Label}”, so nothing was changed. " +
                        "Sign in with the same account, or add this one as a new account.");
            }
            else if (identity is not null)
                target = List(provider).FirstOrDefault(account => account.Kind == AccountKind.OAuth && account.Identity == identity);

            if (target is not null)
            {
                var gate = AccountLock(target.Id);
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    file.Write(target.Id, SecretKind.OAuth, serialized);
                    db.Execute("""
                        UPDATE accounts SET status = 'connected', status_reason = NULL, identity = COALESCE($identity, identity), details = $details,
                          expires_at = $expires, updated_at = $now WHERE id = $id;
                        """, ("$id", target.Id), ("$identity", identity), ("$details", JsonSerializer.Serialize(details, JsonOptions)),
                        ("$expires", credential.ExpiresUnixMilliseconds), ("$now", NowMs()));
                }
                finally { gate.Release(); }
                log.LogInformation("Signed account {Account} of {Provider} in again.", target.Id, provider);
                Raise(target.Id, provider, AccountChange.CredentialReplaced);
                return new SavedSignIn(Get(target.Id)!, Reauthenticated: true, BecameActive: false);
            }

            var id = NewId();
            file.Write(id, SecretKind.OAuth, serialized);
            bool becameActive;
            try
            {
                becameActive = db.Transaction((connection, transaction) =>
                {
                    var rows = ProviderRows(connection, transaction, provider);
                    Insert(connection, transaction, id, provider, UniqueLabel(rows, CheckLabel(label)), AccountKind.OAuth, hint: "", identity, source: null,
                        AccountInfo.EmptyMap, details, credential.ExpiresUnixMilliseconds);
                    return TakeActiveIfFirstStored(connection, transaction, provider, rows, id);
                });
            }
            catch
            {
                file.Remove(id);
                throw;
            }
            log.LogInformation("Stored sign-in account {Account} for {Provider}.", id, provider);
            Raise(id, provider, AccountChange.Added);
            return new SavedSignIn(Get(id)!, Reauthenticated: false, BecameActive: becameActive);
        }
        finally { writes.Release(); }
    }

    /// <summary>Records the last connection test of <paramref name="accountId"/> (shown on the provider page).</summary>
    internal void RecordTest(string accountId, ConnectionTestResult result) =>
        db.Execute("UPDATE accounts SET last_test = $test WHERE id = $id;", ("$id", accountId), ("$test", JsonSerializer.Serialize(result, JsonOptions)));

    // ---- internals ----

    private sealed class AccountOAuthSource(CredentialVault vault, string accountId) : IAdmittedOAuthCredentialSource
    {
        public Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(vault.Get(accountId) is { Kind: AccountKind.OAuth } account && account.Provider == provider ? vault.ReadOAuth(accountId) : null);
        }

        public async Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
            Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            var gate = vault.AccountLock(accountId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The authoritative value is re-read inside the lock; the lock is held until the mutation settles, whatever the token does.
                var account = vault.Get(accountId);
                var current = account is { Kind: AccountKind.OAuth } && account.Provider == provider ? vault.ReadOAuth(accountId) : null;
                var next = await mutation(current, cancellationToken).ConfigureAwait(false);
                if (next is null) return current;
                cancellationToken.ThrowIfCancellationRequested();
                if (current is null || vault.Get(accountId) is null)
                    throw new CredentialUnavailableException(accountId, "The account was removed, so its refreshed sign-in was not stored.");
                vault.file.Write(accountId, SecretKind.OAuth, CredentialFile.SerializeOAuth(next));
                var wasExpired = account!.Status == AccountStatus.Expired;
                vault.db.Execute("""
                    UPDATE accounts SET expires_at = $expires, status = 'connected', status_reason = NULL, updated_at = $now WHERE id = $id;
                    """, ("$id", accountId), ("$expires", next.ExpiresUnixMilliseconds), ("$now", vault.NowMs()));
                if (wasExpired) vault.Raise(accountId, provider, AccountChange.StatusChanged);
                return next;
            }
            finally { gate.Release(); }
        }
    }

    private OAuthCredentialSnapshot? ReadOAuth(string accountId)
    {
        var text = file.Read(accountId, SecretKind.OAuth);
        if (text is null) return null;
        try { return CredentialFile.DeserializeOAuth(text); }
        catch (InvalidDataException) { throw new CredentialUnavailableException(accountId, "Its stored sign-in could not be read; sign in again."); }
    }

    private SemaphoreSlim AccountLock(string accountId) => accountLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

    private void EnsureInitialized()
    {
        if (initialized) return;
        lock (initGate)
        {
            if (initialized) return;
            var ids = db.Query("SELECT id FROM accounts;", reader => reader.GetString(0)).ToHashSet(StringComparer.Ordinal);
            try
            {
                if (file.Prune(ids.Contains) is var pruned and > 0) log.LogInformation("Removed {Count} orphaned secrets from credentials.json.", pruned);
            }
            catch (InvalidDataException)
            {
                // Logged by CredentialFile; the accounts still list, and reading any secret fails with the same reason.
            }
            SyncEnvironment();
            initialized = true;
        }
    }

    private void SyncReadOnly(string id, string provider, AccountKind kind, string label, string? hint, string source, string missing, long now)
    {
        if (hint is not null)
            db.Execute("""
                INSERT INTO accounts (id, provider, label, kind, status, hint, source, created_at, updated_at)
                VALUES ($id, $provider, $label, $kind, 'connected', $hint, $source, $now, $now)
                ON CONFLICT(id) DO UPDATE SET status = 'connected', status_reason = NULL, hint = excluded.hint, source = excluded.source,
                  updated_at = CASE WHEN accounts.status = 'connected' AND accounts.hint = excluded.hint THEN accounts.updated_at ELSE excluded.updated_at END;
                """, ("$id", id), ("$provider", provider), ("$label", label), ("$kind", KindName(kind)), ("$hint", hint), ("$source", source), ("$now", now));
        else
            db.Execute("""
                UPDATE accounts SET status = 'unavailable', status_reason = $reason, hint = '', updated_at = $now
                WHERE id = $id AND status <> 'unavailable';
                """, ("$id", id), ("$reason", missing), ("$now", now));
    }

    private void Touch(string accountId)
    {
        var now = options.Time.GetUtcNow();
        if (touched.TryGetValue(accountId, out var last) && now - last < TouchInterval) return;
        touched[accountId] = now;
        db.Execute("UPDATE accounts SET last_used_at = $now WHERE id = $id;", ("$id", accountId), ("$now", now.ToUnixTimeMilliseconds()));
    }

    private void Raise(string accountId, string provider, AccountChange change)
    {
        try { Changed?.Invoke(this, new AccountChangedEventArgs(accountId, provider, change)); }
        catch (Exception error) { log.LogWarning(error, "An account change handler failed."); }
    }

    private long NowMs() => options.Time.GetUtcNow().ToUnixTimeMilliseconds();

    private static string NewId() => "acc-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static List<AccountInfo> ProviderRows(SqliteConnection connection, SqliteTransaction transaction, string provider)
    {
        using var command = WebAgentDb.Command(connection, transaction, $"SELECT {Columns} FROM accounts WHERE provider = $provider{Order};", ("$provider", provider));
        using var reader = command.ExecuteReader();
        var rows = new List<AccountInfo>();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    /// <summary>A new stored account becomes active when its provider has no chosen account and only environment or ambient ones (a stored
    /// key or sign-in comes before a variable); otherwise the active account stays as it is.</summary>
    private bool TakeActiveIfFirstStored(SqliteConnection connection, SqliteTransaction transaction, string provider, List<AccountInfo> rows, string id)
    {
        var usable = rows.Where(row => row.Status != AccountStatus.Unavailable).ToList();
        var chosen = usable.FirstOrDefault(row => row.Id == row.ActivePointer);
        if (chosen is not null || usable.Any(row => !row.ReadOnly)) return false;
        SetPointer(connection, transaction, provider, id);
        return true;
    }

    private int SetPointer(SqliteConnection connection, SqliteTransaction transaction, string provider, string accountId) =>
        WebAgentDb.Execute(connection, transaction, """
            INSERT INTO active_accounts (provider, account_id, updated_at) VALUES ($provider, $id, $now)
            ON CONFLICT(provider) DO UPDATE SET account_id = excluded.account_id, updated_at = excluded.updated_at;
            """, ("$provider", provider), ("$id", accountId), ("$now", NowMs()));

    private void Insert(SqliteConnection connection, SqliteTransaction transaction, string id, string provider, string label, AccountKind kind, string hint,
        string? identity, string? source, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> details, long? expiresAt)
    {
        var now = NowMs();
        WebAgentDb.Execute(connection, transaction, """
            INSERT INTO accounts (id, provider, label, kind, status, identity, hint, source, settings, details, expires_at, created_at, updated_at)
            VALUES ($id, $provider, $label, $kind, 'connected', $identity, $hint, $source, $settings, $details, $expires, $now, $now);
            """, ("$id", id), ("$provider", provider), ("$label", label), ("$kind", KindName(kind)), ("$identity", identity), ("$hint", hint),
            ("$source", source), ("$settings", JsonSerializer.Serialize(settings, JsonOptions)), ("$details", JsonSerializer.Serialize(details, JsonOptions)),
            ("$expires", expiresAt), ("$now", now));
    }

    private static List<AccountInfo> MarkActive(IReadOnlyList<AccountInfo> rows)
    {
        var active = rows.GroupBy(row => row.Provider, StringComparer.Ordinal).Select(ActiveOf).OfType<AccountInfo>()
            .Select(account => account.Id).ToHashSet(StringComparer.Ordinal);
        return [.. rows.Select(row => row with { IsActive = active.Contains(row.Id) })];
    }

    /// <summary>The active account among one provider's rows (in credential order): the chosen one if usable, else the first usable one.</summary>
    private static AccountInfo? ActiveOf(IEnumerable<AccountInfo> providerRows)
    {
        var usable = providerRows.Where(row => row.Status != AccountStatus.Unavailable).ToList();
        return usable.FirstOrDefault(row => row.Id == row.ActivePointer) ?? usable.FirstOrDefault();
    }

    private static AccountInfo ReadRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Provider = reader.GetString(1),
        Label = reader.GetString(2),
        Kind = ParseKind(reader.GetString(3)),
        Status = ParseStatus(reader.GetString(4)),
        StatusReason = reader.IsDBNull(5) ? null : reader.GetString(5),
        Identity = reader.IsDBNull(6) ? null : reader.GetString(6),
        Hint = reader.IsDBNull(7) ? "" : reader.GetString(7),
        Source = reader.IsDBNull(8) ? null : reader.GetString(8),
        Settings = ReadMap(reader.GetString(9)),
        Details = ReadMap(reader.GetString(10)),
        ExpiresAt = reader.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
        Created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
        Updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
        LastUsed = reader.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
        LastTest = reader.IsDBNull(15) ? null : ReadTest(reader.GetString(15)),
        ActivePointer = reader.IsDBNull(16) ? null : reader.GetString(16),
    };

    private static IReadOnlyDictionary<string, string> ReadMap(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? AccountInfo.EmptyMap; }
        catch (JsonException) { return AccountInfo.EmptyMap; }
    }

    private static ConnectionTestResult? ReadTest(string json)
    {
        try { return JsonSerializer.Deserialize<ConnectionTestResult>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    internal static string KindName(AccountKind kind) => kind switch
    {
        AccountKind.ApiKey => "apiKey", AccountKind.OAuth => "oauth", AccountKind.Environment => "environment", _ => "ambient",
    };

    private static AccountKind ParseKind(string text) => text switch
    {
        "apiKey" => AccountKind.ApiKey, "oauth" => AccountKind.OAuth, "environment" => AccountKind.Environment, _ => AccountKind.Ambient,
    };

    private static AccountStatus ParseStatus(string text) => text switch
    {
        "connected" => AccountStatus.Connected, "expired" => AccountStatus.Expired, _ => AccountStatus.Unavailable,
    };

    private static string KindText(AccountKind kind) => kind switch
    {
        AccountKind.OAuth => "a sign-in", AccountKind.Environment => "an environment variable", AccountKind.Ambient => "ambient credentials", _ => "an API key",
    };

    private static string SourceText(AccountInfo account) => account.Kind == AccountKind.Environment
        ? $"the {account.Source} environment variable" : $"the host's {account.Source ?? "credentials"}";
}
