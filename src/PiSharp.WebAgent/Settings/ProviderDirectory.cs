// Decision 0011 (run W2, WU9): every provider the settings pages show, with what it takes to connect it and how it is connected now. The list
// is the model catalog's providers (IModelCatalog: faux in --faux mode, WU6's runtime catalog otherwise), then PiSharp's built-in providers
// (BuiltinProviders: metadata only, nothing calls a provider), then any provider that only has accounts; so a key can be stored for a
// provider even offline. Accounts come from IAccountStore and sign-in methods from ISignInFlows (WU5): an entry holds metadata only, a masked
// hint at most. Nothing here picks or changes an account: the active account is the one the user chose (IAccountStore.Active).
// W3 fix: a custom provider of the data folder's models.json (whenever CustomProviderService knows the id) is listed after the built-in
// providers, routed or not, with CustomProviderFacts: where its key comes from (none, a vault account named "<name> key", or a $VAR
// reference that is set or not; never the key), and the page that edits it (Settings › Custom providers), which its rows link to instead
// of a detail page. It takes no key through the generic API key modal (KeyForm None), it is not a no-credentials (faux) provider, and it
// counts as connected only when it is routed (never with --faux) and its key source is usable: no key, a stored key that works, or a set
// variable. So the first-run redirect (SetupState.HasConnection) no longer counts a custom provider whose variable is unset, or one that
// is not routed. W4 L3: "no key" is usable only for a server on this machine (a loopback base URL: LM Studio, Ollama, vLLM); a keyless
// custom provider elsewhere is listed with its key source but not counted as connected, so it never ends the first-run redirect on its
// own (it is still routed and in the model picker).
using PiSharp.AI.Catalogs;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;
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

/// <summary>A custom provider of the data folder's models.json as the settings pages show it (W3 fix): where its key comes from (never the
/// key), whether it is routed, and the page that edits it.</summary>
/// <param name="KeySource">Where its key comes from now (<see cref="CustomProviderService.KeySource"/>).</param>
/// <param name="Variable">The environment variable of an environment key, or null.</param>
/// <param name="VariableSet">Whether that variable is set on the web host (<see cref="CustomProviderService.IsSet"/>; never its value).</param>
/// <param name="BaseUrl">Its base URL (empty when the file has none).</param>
/// <param name="Api">Its API type (empty when the file has none).</param>
/// <param name="Loopback">Whether the base URL is on this machine.</param>
/// <param name="Routed">Whether it is routed: a key source the web app uses, models, and an API with a live route.</param>
/// <param name="Problem">Why its key or a header is not used, or null.</param>
public sealed record CustomProviderFacts(CustomKeySource KeySource, string? Variable, bool VariableSet, string BaseUrl, string Api, bool Loopback,
    bool Routed, string? Problem)
{
    /// <summary>Settings › Custom providers, where a custom provider is edited (its key too).</summary>
    public const string PageUrl = "/settings/custom-providers";

    /// <summary>The chip of its key source: <c>no key</c>, <c>stored key</c>, <c>env · VARIABLE</c> (with <c>not set</c>), <c>key not used</c>.</summary>
    public string Chip => KeySource switch
    {
        CustomKeySource.Stored => "stored key",
        CustomKeySource.Environment => "env · " + Variable + (VariableSet ? "" : " · not set"),
        CustomKeySource.Unsupported => "key not used",
        _ => "no key",
    };

    /// <summary>The chip's tone: neutral for "no key" on a server elsewhere, which does not count as connected (W4 L3).</summary>
    public string Tone => KeySource switch
    {
        CustomKeySource.Stored => "ok",
        CustomKeySource.Environment => VariableSet ? "info" : "warn",
        CustomKeySource.Unsupported => "warn",
        _ => Loopback ? "ok" : "outline",
    };

    /// <summary>Whether "no key" lets it count as connected: only for a server on this machine (W4 L3). A keyless provider elsewhere is
    /// still routed with the placeholder key, but connecting it is not assumed.</summary>
    public bool KeylessCounts => KeySource == CustomKeySource.None && Loopback;

    /// <summary>The chip's Bootstrap icon.</summary>
    public string Icon => KeySource switch
    {
        CustomKeySource.Stored => "bi-key",
        CustomKeySource.Environment => "bi-terminal",
        CustomKeySource.Unsupported => "bi-exclamation-triangle",
        _ => "bi-hdd-network",
    };

    /// <summary>How it authenticates, for the provider header.</summary>
    public string Auth => KeySource switch
    {
        CustomKeySource.Stored => "API key (vault)",
        CustomKeySource.Environment => "$" + Variable + (VariableSet ? "" : " (not set)"),
        CustomKeySource.Unsupported => "a key the web app doesn't use",
        _ => "none (a placeholder key is sent)",
    };
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

    /// <summary>The custom provider of models.json this is (W3 fix), or null for any other provider.</summary>
    public CustomProviderFacts? Custom { get; init; }

    /// <summary>Where it is managed: its detail page, or Settings › Custom providers for a custom provider.</summary>
    public string Href => Custom is null ? "/settings/providers/" + Uri.EscapeDataString(Id) : CustomProviderFacts.PageUrl;

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
        KeyForm is ApiKeyForm.Single or ApiKeyForm.Azure || Custom is { KeySource: CustomKeySource.Stored or CustomKeySource.Environment } ? "apiKey" : null,
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
/// <param name="customs">The custom providers area (models.json), when registered: its providers are listed with
/// <see cref="ProviderEntry.Custom"/>.</param>
public sealed class ProviderDirectory(IModelCatalog catalog, IAccountStore accounts, ISignInFlows signIn, CredentialsOptions? options = null,
    CustomProviderService? customs = null)
{
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

    /// <summary>Every provider: the catalog's, then the built-in ones, then the custom ones of models.json (routed or not), then providers that
    /// only have accounts.</summary>
    public IReadOnlyList<ProviderEntry> List()
    {
        var methods = signIn.Methods;
        var all = accounts.List();
        var ids = new List<string>();
        foreach (var provider in catalog.Providers) Add(provider.Id);
        foreach (var provider in BuiltinProviders.All) Add(provider.Id);
        foreach (var provider in CustomProviders()) Add(provider.Id);
        foreach (var account in all.OrderBy(account => account.Provider, StringComparer.Ordinal)) Add(account.Provider);
        return [.. ids.Select(id => Build(id, [.. all.Where(account => account.Provider == id)], methods))];

        void Add(string id)
        {
            if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        }
    }

    /// <summary>The provider <paramref name="id"/>, or null when no catalog, built-in provider, custom provider or account knows it.</summary>
    public ProviderEntry? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var known = catalog.FindProvider(id) is not null || BuiltinProviders.TryGet(id, out _) || CustomOf(id) is not null;
        var own = accounts.List(id);
        return known || own.Count > 0 ? Build(id, own, signIn.Methods) : null;
    }

    /// <summary>The ids of the providers that are connected (a usable account, or no credentials needed).</summary>
    public IReadOnlySet<string> ConnectedIds() => List().Where(entry => entry.IsConnected).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The connected provider ids from <paramref name="services"/>' directory, or null when the app has no directory (every catalog
    /// provider is offered then). Pages resolve it lazily, so no area depends on the Settings area's registration order.</summary>
    public static IReadOnlySet<string>? ConnectedIdsOf(IServiceProvider services) => services.GetService<ProviderDirectory>()?.ConnectedIds();

    private IReadOnlyList<CustomProviderInfo> CustomProviders() => customs?.Current.Providers ?? [];

    private CustomProviderInfo? CustomOf(string id) => customs is null || CustomProviderJson.IsBuiltin(id) ? null : customs.Current.Find(id);

    private ProviderEntry Build(string id, IReadOnlyList<AccountInfo> own, IReadOnlyList<SignInMethodInfo> methods)
    {
        if (customs is not null && CustomOf(id) is { } custom) return BuildCustom(custom, customs, own);
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

    /// <summary>A custom provider (W3 fix): its key source instead of an API key form, and connected only when it is routed and its key
    /// source is usable (W4 L3: no key only for a server on this machine).</summary>
    private ProviderEntry BuildCustom(CustomProviderInfo custom, CustomProviderService service, IReadOnlyList<AccountInfo> own)
    {
        var listed = catalog.FindProvider(custom.Id);
        var source = service.KeySource(custom);
        var variable = source == CustomKeySource.Environment ? custom.KeyVariable : null;
        var facts = new CustomProviderFacts(source, variable, service.IsSet(variable), custom.BaseUrl, custom.Api, custom.Loopback, custom.Routed,
            custom.Problem);
        var active = source == CustomKeySource.Stored ? own.FirstOrDefault(account => account.IsActive) : null;
        var name = listed?.DisplayName ?? custom.Name;
        return new ProviderEntry
        {
            Id = custom.Id,
            Name = name,
            LogoUrl = Logo(custom.Id, listed?.LogoKey),
            Description = DescribeCustom(custom, facts, Offline),
            BaseUrl = custom.BaseUrl.Length > 0 ? custom.BaseUrl : null,
            SubscriptionName = name,
            Apis = custom.Api.Length > 0 ? [custom.Api] : [],
            Variables = variable is null ? [] : [variable],
            Billing = listed?.Billing ?? (custom.Loopback ? BillingKind.Free : BillingKind.Unknown),
            Models = listed is null ? [] : catalog.Models(custom.Id),
            Accounts = own,
            Active = active,
            KeyForm = ApiKeyForm.None,
            // With --faux no custom provider is routed (the faux catalog stands in), so none counts as connected. Without a key it counts
            // only on this machine (W4 L3): a server elsewhere that takes the placeholder key is not assumed to be connected.
            State = !custom.Routed || Offline ? ProviderState.NotConfigured : source switch
            {
                CustomKeySource.None => facts.KeylessCounts ? ProviderState.Connected : ProviderState.NotConfigured,
                CustomKeySource.Environment => facts.VariableSet ? ProviderState.Environment : ProviderState.NotConfigured,
                CustomKeySource.Stored => StateOf(false, active, own),
                _ => ProviderState.NotConfigured,
            },
            Custom = facts,
        };
    }

    private static string DescribeCustom(CustomProviderInfo custom, CustomProviderFacts facts, bool offline)
    {
        var at = custom.BaseUrl.Length > 0 ? " at " + custom.BaseUrl : "";
        var api = custom.Api.Length > 0 ? " (" + custom.Api + ")" : "";
        var key = facts.KeySource switch
        {
            CustomKeySource.Stored => "Its key is stored encrypted in the credential vault.",
            CustomKeySource.Environment => $"Its key is read from {facts.Variable}" + (facts.VariableSet ? "." : ", which isn't set for the web host."),
            CustomKeySource.Unsupported => "The key in models.json isn't one the web app uses, so it isn't routed until it is saved with another key source.",
            _ when facts.Loopback => "It needs no key; requests carry a placeholder key.",
            _ => "It has no key, so requests carry a placeholder key; a server on another machine usually needs one, so it doesn't count as " +
                "connected until it has a key.",
        };
        var routed = offline ? " With --faux it isn't routed."
            : custom.Routed || facts.KeySource == CustomKeySource.Unsupported ? ""
            : custom.Models.Count == 0 ? " It has no models yet, so it isn't in the model picker."
            : " Its API has no live route here, so it isn't in the model picker.";
        return $"A custom provider from models.json{at}{api}. {key}{routed} Edit it in Settings › Custom providers.";
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
        // A catalog's logo key names a mark only when Web.Common has one under that name (WU6 may set it to a provider id).
        return catalogKey is null ? null : ProviderLogos.Url(catalogKey);
    }

    private static string Describe(string name, BuiltinProviderDefinition? builtin, bool signInOnly, bool signsIn)
    {
        if (signInOnly) return $"{name} connects by signing in; it takes no API key.";
        var apis = builtin is { Apis.Length: > 0 } ? " through " + string.Join(", ", builtin.Apis) : "";
        return $"{name} models{apis}. Connect with an API key{(signsIn ? " or sign in" : "")}.";
    }
}
