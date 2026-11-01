// Decision 0011 (run W2, WU5): the provider accounts the Web Agent keeps, as pages and services see them: metadata only. An account is one
// credential of one provider: an API key stored in the app (apiKey), a subscription sign-in (oauth: ChatGPT for openai-codex, GitHub
// Copilot), an environment variable the host was started with (environment, read-only), or credentials the provider's SDK finds by itself
// (ambient: the AWS credential chain, Google application-default credentials; read-only). Each provider has one active account, the one new
// chats bind to; a chat keeps the account it was created with, so changing the active account, adding or removing another never moves a
// chat, and nothing here falls back from one account to another. No member carries a secret: a page gets a masked hint at most.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Credentials;

/// <summary>Where an account's credential comes from. Stored and sent as <c>apiKey</c>, <c>oauth</c>, <c>environment</c>, <c>ambient</c>.</summary>
public enum AccountKind
{
    /// <summary>An API key stored in the app (encrypted in credentials.json), possibly with non-secret settings such as a base URL.</summary>
    [JsonStringEnumMemberName("apiKey")] ApiKey,

    /// <summary>A subscription sign-in: access and refresh tokens stored in the app, refreshed under a per-account lock.</summary>
    [JsonStringEnumMemberName("oauth")] OAuth,

    /// <summary>An environment variable of the host (<c>ProviderEnvironmentKeys</c>), read when a request needs it; read-only.</summary>
    [JsonStringEnumMemberName("environment")] Environment,

    /// <summary>Credentials the provider's SDK finds itself (AWS credential chain, Google ADC); read-only.</summary>
    [JsonStringEnumMemberName("ambient")] Ambient,
}

/// <summary>Whether an account can be used. Stored and sent as <c>connected</c>, <c>expired</c>, <c>unavailable</c>.</summary>
public enum AccountStatus
{
    /// <summary>Usable as far as the app knows.</summary>
    [JsonStringEnumMemberName("connected")] Connected,

    /// <summary>Rejected or no longer refreshable (a revoked sign-in, a 401): sign in again or replace the key. Chats bound to it show the
    /// <c>expired</c> state; none of them moves to another account.</summary>
    [JsonStringEnumMemberName("expired")] Expired,

    /// <summary>An environment or ambient account whose variable is no longer set. It stays listed so chats bound to it fail clearly.</summary>
    [JsonStringEnumMemberName("unavailable")] Unavailable,
}

/// <summary>One provider account, without its secret.</summary>
public sealed record AccountInfo
{
    /// <summary>The account's id, stable for its life: <c>acc-</c> plus 16 hex digits for a stored account, <c>env-&lt;provider&gt;-&lt;variable&gt;</c>
    /// (lower case) for an environment account, <c>ambient-&lt;provider&gt;</c> for ambient credentials. Chats bind to it.</summary>
    public required string Id { get; init; }

    /// <summary>The PiSharp provider id, such as <c>openai</c> or <c>openai-codex</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The name the user gave it (or a default such as the variable's name).</summary>
    public required string Label { get; init; }

    /// <summary>Where the credential comes from.</summary>
    public required AccountKind Kind { get; init; }

    /// <summary>Whether it can be used.</summary>
    public AccountStatus Status { get; init; }

    /// <summary>Why it is <see cref="AccountStatus.Expired"/> or <see cref="AccountStatus.Unavailable"/> (scrubbed), or null.</summary>
    public string? StatusReason { get; init; }

    /// <summary>Whether new chats of its provider bind to it.</summary>
    public bool IsActive { get; init; }

    /// <summary>Environment and ambient accounts cannot be renamed, replaced or removed in the app.</summary>
    public bool ReadOnly => Kind is AccountKind.Environment or AccountKind.Ambient;

    /// <summary>The masked hint of the key (<see cref="SecretText.Mask"/>), empty for sign-ins and ambient credentials.</summary>
    public string Hint { get; init; } = "";

    /// <summary>The environment variable of an environment account, or what ambient credentials were found (<c>AWS credential chain</c>).</summary>
    public string? Source { get; init; }

    /// <summary>The provider's identity of a sign-in (the ChatGPT account id), which is not a secret; signing in again with the same identity
    /// updates this account instead of adding another. Null when the flow reports none (GitHub Copilot).</summary>
    public string? Identity { get; init; }

    /// <summary>Non-secret facts for the pages: <c>plan</c> and <c>email</c> of a ChatGPT sign-in, <c>host</c> and <c>models</c> of a Copilot
    /// sign-in.</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = EmptyMap;

    /// <summary>Non-secret settings stored with an API key account (for example <c>baseUrl</c>, <c>AWS_REGION</c>, <c>CLOUDFLARE_ACCOUNT_ID</c>).
    /// Never a secret: the key itself is the only secret of an API key account.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = EmptyMap;

    /// <summary>When a sign-in's access token expires (it is refreshed before), or null.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the account was added.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When the account last changed.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>When a request last read its credential (to the minute), or null.</summary>
    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>The last connection test the user ran, or null.</summary>
    public ConnectionTestResult? LastTest { get; init; }

    /// <summary>The account its provider's stored choice names (read with the row; <see cref="IsActive"/> is the answer).</summary>
    internal string? ActivePointer { get; init; }

    internal static IReadOnlyDictionary<string, string> EmptyMap { get; } = new Dictionary<string, string>();
}

/// <summary>An API key to store as a new account. <see cref="object.ToString"/> never shows the key.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="ApiKey">The key; may be null only when <see cref="Settings"/> holds the whole configuration (an AWS profile, a Vertex project).</param>
public sealed record ApiKeyAccountRequest(string Provider, string? ApiKey)
{
    /// <summary>The account's name; when null or blank, the provider's key name (<c>OpenAI key</c>). Made unique among the provider's accounts.</summary>
    public string? Label { get; init; }

    /// <summary>Non-secret settings (see <see cref="AccountInfo.Settings"/>).</summary>
    public IReadOnlyDictionary<string, string>? Settings { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"ApiKeyAccountRequest ({Provider}) [redacted]";
}

/// <summary>A new account.</summary>
/// <param name="Account">The account as stored.</param>
/// <param name="BecameActive">Whether it became its provider's active account (it does when the provider had no stored account: a stored
/// key or sign-in comes before an environment variable).</param>
public sealed record AccountAdded(AccountInfo Account, bool BecameActive);

/// <summary>A removed account.</summary>
/// <param name="Removed">The account as it was.</param>
/// <param name="Active">Its provider's active account now, or null when none is left.</param>
public sealed record AccountRemoved(AccountInfo Removed, AccountInfo? Active);

/// <summary>What changed in <see cref="AccountChangedEventArgs"/>.</summary>
public enum AccountChange
{
    /// <summary>An account was added (a key, a first sign-in).</summary>
    Added,

    /// <summary>Its label changed.</summary>
    Renamed,

    /// <summary>Its credential was replaced (a new key, signing in again, a refresh).</summary>
    CredentialReplaced,

    /// <summary>It became its provider's active account.</summary>
    Activated,

    /// <summary>Its status changed (expired, unavailable, connected again).</summary>
    StatusChanged,

    /// <summary>It was removed.</summary>
    Removed,
}

/// <summary>An account change, raised after it is stored.</summary>
/// <param name="accountId">The account.</param>
/// <param name="provider">Its provider.</param>
/// <param name="change">What changed.</param>
public sealed class AccountChangedEventArgs(string accountId, string provider, AccountChange change) : EventArgs
{
    /// <summary>The account.</summary>
    public string AccountId { get; } = accountId;

    /// <summary>Its provider.</summary>
    public string Provider { get; } = provider;

    /// <summary>What changed.</summary>
    public AccountChange Change { get; } = change;
}

/// <summary>The provider accounts and each provider's active account: metadata only, never a secret (secrets are <see cref="ICredentialVault"/>'s).
/// A singleton; safe to call from many threads. Reads are synchronous (one small SQLite query); writes are serialized.</summary>
public interface IAccountStore
{
    /// <summary>Raised after an account is added, renamed, replaced, activated, removed or changes status.</summary>
    event EventHandler<AccountChangedEventArgs>? Changed;

    /// <summary>Every account, or those of <paramref name="provider"/>: by provider, then sign-ins, stored keys, environment and ambient
    /// accounts, oldest first.</summary>
    IReadOnlyList<AccountInfo> List(string? provider = null);

    /// <summary>The account <paramref name="accountId"/>, or null.</summary>
    AccountInfo? Get(string accountId);

    /// <summary>The active account of <paramref name="provider"/>, the one a new chat binds to, or null when it has none. It is the one the
    /// user chose; otherwise (or when that one is unavailable) the first usable one in credential order: sign-in, stored key, environment
    /// variable, ambient credentials, oldest first.</summary>
    AccountInfo? Active(string provider);

    /// <summary>Stores a new API key account. The key is encrypted in credentials.json; the account's metadata is in webagent.db.</summary>
    /// <exception cref="ArgumentException">An invalid provider id, label, key or setting (the message never repeats the key).</exception>
    /// <exception cref="InvalidOperationException">The provider does not take API keys (openai-codex signs in only).</exception>
    Task<AccountAdded> AddApiKeyAsync(ApiKeyAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the key (and, when given, the settings) of API key account <paramref name="accountId"/>, keeping its id, so chats
    /// bound to it use the new key. A null <paramref name="apiKey"/> keeps the key. The account is connected again.</summary>
    /// <exception cref="KeyNotFoundException">No such account.</exception>
    /// <exception cref="InvalidOperationException">It is not an API key account.</exception>
    Task<AccountInfo> ReplaceApiKeyAsync(string accountId, string? apiKey, IReadOnlyDictionary<string, string>? settings = null,
        CancellationToken cancellationToken = default);

    /// <summary>Renames an account.</summary>
    /// <exception cref="KeyNotFoundException">No such account.</exception>
    /// <exception cref="InvalidOperationException">An environment or ambient account (read-only).</exception>
    Task<AccountInfo> RenameAsync(string accountId, string label, CancellationToken cancellationToken = default);

    /// <summary>Makes the account its provider's active account. Only new chats follow; chats keep their own account.</summary>
    /// <exception cref="KeyNotFoundException">No such account.</exception>
    /// <exception cref="InvalidOperationException">It is unavailable.</exception>
    Task<AccountInfo> SetActiveAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Removes an account and its secret. When it was active, the provider's next stored account becomes active (or none is chosen,
    /// and an environment variable may serve). Chats bound to it fail with the <c>expired</c> state; none moves to another account.</summary>
    /// <exception cref="KeyNotFoundException">No such account.</exception>
    /// <exception cref="InvalidOperationException">An environment or ambient account that is still available (unset the variable instead).</exception>
    Task<AccountRemoved> RemoveAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Marks an account expired (routing calls this when a provider rejects it or its refresh fails); signing in again or replacing
    /// the key connects it again.</summary>
    /// <exception cref="KeyNotFoundException">No such account.</exception>
    Task<AccountInfo> MarkExpiredAsync(string accountId, string reason, CancellationToken cancellationToken = default);
}
