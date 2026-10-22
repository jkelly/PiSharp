// Decision 0011 (run W2, WU9): every provider the settings pages show, with what it takes to connect it and how it is connected now. The list
// is the model catalog's providers (IModelCatalog: faux in --faux mode, WU6's runtime catalog otherwise), then PiSharp's built-in providers
// (BuiltinProviders: metadata only, nothing calls a provider), then any provider that only has accounts; so a key can be stored for a
// provider even offline. Accounts come from IAccountStore and sign-in methods from ISignInFlows (WU5): an entry holds metadata only, a masked
// hint at most. Nothing here picks or changes an account: the active account is the one the user chose (IAccountStore.Active).
using PiSharp.AI.Catalogs;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using BuiltinAuth = PiSharp.AI.Catalogs.ProviderAuthKind;

namespace PiSharp.WebAgent.Settings;

/// <summary>How a provider is connected, as the Providers page groups and filters it (<c>data-state</c>: connected, env, expired, none).</summary>
public enum ProviderState
{
    /// <summary>A stored key, a sign-in, or a provider that needs no credentials (faux).</summary>
    Connected,

    /// <summary>Only an environment variable or ambient SDK credentials (read-only accounts) connect it.</summary>
    Environment,

    /// <summary>Its active account (or its only account) is expired: sign in again or replace the key.</summary>
    Expired,

    /// <summary>No usable account.</summary>
    NotConfigured,
}

/// <summary>Which variant of the API key modal (<c>#modal-api-key</c>) a provider uses.</summary>
public enum ApiKeyForm
{
    /// <summary>The provider takes no API key (it signs in only, or needs nothing).</summary>
    None,

    /// <summary>One key, and an optional base URL (<c>baseUrl</c>).</summary>
    Single,

    /// <summary>Azure OpenAI: key, resource name or base URL, API version and deployment map.</summary>
    Azure,

    /// <summary>Amazon Bedrock: bearer token, or an AWS profile, or the default credential chain; and a region.</summary>
    Bedrock,

    /// <summary>Google Vertex AI: API key, or application default credentials, or a service account file; project and location.</summary>
    Vertex,

    /// <summary>Cloudflare: API token, account id and (for the AI Gateway) gateway id.</summary>
    Cloudflare,
}

/// <summary>One provider as the settings pages show it. Metadata only.</summary>
public sealed record ProviderEntry
{
    /// <summary>The provider id, such as <c>openai</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Its display name.</summary>
    public required string Name { get; init; }

    /// <summary>Its logo (Web.Common's provider marks), or null for a generic icon.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>One sentence on what the provider is and how it connects.</summary>
    public required string Description { get; init; }

    /// <summary>Whether it is one of PiSharp's built-in providers.</summary>
    public bool Builtin { get; init; }

    /// <summary>The provider's default base URL, when known.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The name of its subscription sign-in, such as <c>ChatGPT Plus/Pro</c> (Settings › Accounts), when it signs in.</summary>
    public string? SubscriptionName { get; init; }

    /// <summary>The APIs it speaks, such as <c>openai-responses</c>.</summary>
    public IReadOnlyList<string> Apis { get; init; } = [];

    /// <summary>The environment variables its key is read from, such as <c>OPENAI_API_KEY</c>.</summary>
    public IReadOnlyList<string> Variables { get; init; } = [];

    /// <summary>How its models are billed (the catalog's word; <see cref="BillingKind.Unknown"/> when it is not in the catalog).</summary>
    public BillingKind Billing { get; init; } = BillingKind.Unknown;

    /// <summary>Its chat models in the catalog (none for a provider the catalog does not list, such as every real provider in --faux mode).</summary>
    public IReadOnlyList<CatalogModel> Models { get; init; } = [];

    /// <summary>Its accounts, in credential order (IAccountStore.List).</summary>
    public IReadOnlyList<AccountInfo> Accounts { get; init; } = [];

    /// <summary>Its active account, the one new chats bind to, or null.</summary>
    public AccountInfo? Active { get; init; }

    /// <summary>How it signs in (ISignInFlows.Methods), or none.</summary>
    public IReadOnlyList<SignInMethodInfo> SignIns { get; init; } = [];

    /// <summary>Whether it needs no credentials at all (faux).</summary>
    public bool NoCredentials { get; init; }

    /// <summary>Whether it connects only by signing in (openai-codex), so it takes no API key.</summary>
    public bool SignInOnly { get; init; }

    /// <summary>The API key modal variant, or <see cref="ApiKeyForm.None"/>.</summary>
    public ApiKeyForm KeyForm { get; init; }

    /// <summary>How it is connected now.</summary>
    public ProviderState State { get; init; }

    /// <summary>Whether a key (or key settings) can be stored for it.</summary>
    public bool TakesApiKey => KeyForm != ApiKeyForm.None;

    /// <summary>Whether it has a sign-in (whether or not one is available now).</summary>
    public bool SignsIn => SignIns.Count > 0;

    /// <summary>Whether any sign-in method of it is available now (none offline).</summary>
    public bool SignInAvailable => SignIns.Any(method => method.Available);

    /// <summary>Whether it is connected in any way (a usable account, or no credentials needed).</summary>
    public bool IsConnected => State is ProviderState.Connected or ProviderState.Environment;

    /// <summary>The Providers page's <c>data-state</c> word.</summary>
    public string StateName => State switch
    {
        ProviderState.Connected => "connected",
        ProviderState.Environment => "env",
        ProviderState.Expired => "expired",
        _ => "none",
    };

    /// <summary>The auth-kind filter words (<c>apiKey</c>, <c>oauth</c>, <c>cloud</c>), space-separated.</summary>
    public string AuthTags => string.Join(' ', new[]
    {
        KeyForm is ApiKeyForm.Single or ApiKeyForm.Azure ? "apiKey" : null,
        SignsIn ? "oauth" : null,
        KeyForm is ApiKeyForm.Bedrock or ApiKeyForm.Vertex or ApiKeyForm.Cloudflare ? "cloud" : null,
    }.Where(tag => tag is not null));

    /// <summary>The accounts that can be changed or removed here (not environment or ambient ones).</summary>
    public IEnumerable<AccountInfo> StoredAccounts => Accounts.Where(account => !account.ReadOnly);
}

/// <summary>Builds <see cref="ProviderEntry"/> values from the catalog, the built-in providers, the accounts and the sign-in methods.</summary>
/// <param name="catalog">The model catalog.</param>
/// <param name="accounts">The accounts.</param>
/// <param name="signIn">The sign-in flows (only <see cref="ISignInFlows.Methods"/> is read).</param>
/// <param name="options">The credentials options (offline in --faux mode), when registered.</param>
public sealed class ProviderDirectory(IModelCatalog catalog, IAccountStore accounts, ISignInFlows signIn, CredentialsOptions? options = null)
{
    // Logos Web.Common's ProviderLogos does not map yet (a request to WU3 asks it to).
    private static readonly Dictionary<string, string> LogoFallbacks = new(StringComparer.Ordinal)
    {
        ["azure"] = "azure",
        ["cloudflare-ai-gateway"] = "cloudflare",
        ["meta"] = "meta",
        ["moonshotai"] = "kimi",
        ["moonshotai-cn"] = "kimi",
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["faux"] = "Offline test models that answer by echoing. They need no key and make no network calls.",
        ["openai"] = "GPT and o-series models through the OpenAI Responses API. Connect with an API key.",
        ["openai-codex"] = "Codex models through your ChatGPT Plus or Pro subscription. There's no API key for this provider: you sign in with " +
            "ChatGPT, and usage counts against your plan instead of per-token billing.",
        ["anthropic"] = "Claude models through the Anthropic Messages API. Anthropic doesn't allow Claude Pro or Max sign-in in other apps, so " +
            "Claude connects with an API key or through Amazon Bedrock.",
        ["github-copilot"] = "Models included in your GitHub Copilot plan. Sign in with GitHub; the Copilot token is re-issued through the " +
            "GitHub token.",
        ["google"] = "Gemini models through the Gemini API. Connect with an API key from Google AI Studio.",
        ["google-vertex"] = "Gemini models on Google Cloud. Use an API key, application default credentials or a service account.",
        ["amazon-bedrock"] = "Models on Amazon Bedrock, including Claude. Use a bearer token, an AWS profile or the default credential chain.",
        ["azure"] = "OpenAI models deployed on Azure. Needs a key and the resource name or base URL of your deployment.",
    };

    /// <summary>Whether the app runs offline (--faux): no connection tests and no sign-ins.</summary>
    public bool Offline => options?.Offline == true;

    /// <summary>The message the pages show while offline.</summary>
    public static string OfflineMessage => CredentialsOptions.OfflineMessage;

    /// <summary>Every provider: the catalog's, then the built-in ones, then providers that only have accounts.</summary>
    public IReadOnlyList<ProviderEntry> List()
    {
        var methods = signIn.Methods;
        var all = accounts.List();
        var ids = new List<string>();
        foreach (var provider in catalog.Providers) Add(provider.Id);
        foreach (var provider in BuiltinProviders.All) Add(provider.Id);
        foreach (var account in all.OrderBy(account => account.Provider, StringComparer.Ordinal)) Add(account.Provider);
        return [.. ids.Select(id => Build(id, [.. all.Where(account => account.Provider == id)], methods))];

        void Add(string id)
        {
            if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        }
    }

    /// <summary>The provider <paramref name="id"/>, or null when no catalog, built-in provider or account knows it.</summary>
    public ProviderEntry? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var known = catalog.FindProvider(id) is not null || BuiltinProviders.TryGet(id, out _);
        var own = accounts.List(id);
        return known || own.Count > 0 ? Build(id, own, signIn.Methods) : null;
    }

    /// <summary>The ids of the providers that are connected (a usable account, or no credentials needed).</summary>
    public IReadOnlySet<string> ConnectedIds() => List().Where(entry => entry.IsConnected).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

    private ProviderEntry Build(string id, IReadOnlyList<AccountInfo> own, IReadOnlyList<SignInMethodInfo> methods)
    {
        var listed = catalog.FindProvider(id);
        BuiltinProviders.TryGet(id, out var builtin);
        var noCredentials = CredentialVault.NoCredentialProviders.Contains(id) || (listed is not null && listed.AuthKinds.All(kind => kind == Contracts.ProviderAuthKind.None));
        var signInOnly = !noCredentials && CredentialVault.SignInOnly(id);
        // IAccountStore.Active is the IsActive account of its list, so the list already read answers it.
        var active = noCredentials ? null : own.FirstOrDefault(account => account.IsActive);
        var name = listed?.DisplayName ?? builtin?.Name ?? id;
        return new ProviderEntry
        {
            Id = id,
            Name = name,
            LogoUrl = Logo(id, listed?.LogoKey),
            Description = Descriptions.GetValueOrDefault(id) ?? Describe(name, builtin, signInOnly, methods.Any(method => method.Provider == id)),
            Builtin = builtin is not null,
            BaseUrl = builtin?.BaseUrl,
            SubscriptionName = id == SignInFlows.CodexProvider ? "ChatGPT Plus/Pro" : builtin?.OAuthName ?? name,
            Apis = builtin is null ? [] : [.. builtin.Apis],
            Variables = builtin is null ? [] : [.. builtin.ApiKeyVariables],
            Billing = listed?.Billing ?? (noCredentials ? BillingKind.Free : BillingKind.Unknown),
            Models = listed is null ? [] : catalog.Models(id),
            Accounts = own,
            Active = active,
            SignIns = [.. methods.Where(method => method.Provider == id)],
            NoCredentials = noCredentials,
            SignInOnly = signInOnly,
            KeyForm = noCredentials || signInOnly ? ApiKeyForm.None : FormOf(id, builtin),
            State = StateOf(noCredentials, active, own),
        };
    }

    private static ApiKeyForm FormOf(string id, BuiltinProviderDefinition? builtin) => builtin?.Auth switch
    {
        BuiltinAuth.AmazonBedrock => ApiKeyForm.Bedrock,
        BuiltinAuth.GoogleVertex => ApiKeyForm.Vertex,
        BuiltinAuth.CloudflareAIGateway or BuiltinAuth.CloudflareWorkersAI => ApiKeyForm.Cloudflare,
        _ => id == "azure" ? ApiKeyForm.Azure : ApiKeyForm.Single,
    };

    private static ProviderState StateOf(bool noCredentials, AccountInfo? active, IReadOnlyList<AccountInfo> own)
    {
        if (noCredentials) return ProviderState.Connected;
        if (active is not null)
        {
            if (active.Status == AccountStatus.Expired) return ProviderState.Expired;
            if (active.Status == AccountStatus.Connected) return active.ReadOnly ? ProviderState.Environment : ProviderState.Connected;
        }
        return own.Any(account => account.Status == AccountStatus.Expired) ? ProviderState.Expired : ProviderState.NotConfigured;
    }

    private static string? Logo(string id, string? catalogKey)
    {
        if (ProviderLogos.Url(id) is { } url) return url;
        if (LogoFallbacks.TryGetValue(id, out var key)) return ProviderLogos.KeyUrl(key);
        // A catalog's logo key names a mark only when Web.Common has one under that name (WU6 may set it to a provider id).
        return catalogKey is not null && (ProviderLogos.Url(catalogKey) ?? (LogoFallbacks.TryGetValue(catalogKey, out var other) ? ProviderLogos.KeyUrl(other) : null)) is { } mark
            ? mark : null;
    }

    private static string Describe(string name, BuiltinProviderDefinition? builtin, bool signInOnly, bool signsIn)
    {
        if (signInOnly) return $"{name} connects by signing in; it takes no API key.";
        var apis = builtin is { Apis.Length: > 0 } ? " through " + string.Join(", ", builtin.Apis) : "";
        return $"{name} models{apis}. Connect with an API key{(signsIn ? " or sign in" : "")}.";
    }
}
