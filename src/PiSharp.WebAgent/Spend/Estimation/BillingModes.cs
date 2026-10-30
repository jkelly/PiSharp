// Decision 0011 (run W3, S1): how each provider account is billed (mockup settings-spend, _SpendAccounts), worked out from how it connects.
// In order: the faux provider is Free (plain --faux) or Metered at its test list price (--faux-metered); a provider whose every model is
// served from a loopback address (a custom provider such as LM Studio on localhost:1234) is Free, because it runs on this machine; the
// subscription sign-ins (openai-codex with ChatGPT, github-copilot) and any OAuth account are Subscription (shown at list price, never
// counted toward money caps); a provider the catalog marks Free is Free; a provider none of whose models has a price (in the catalog or
// entered in Settings › Spend) is Unknown; everything else, which is API keys, environment keys and ambient credentials, is Metered. A
// billing override in Settings › Spend wins (an account's over its provider's), and the detected mode stays visible beside it. The basis
// says why in a few words, without a secret: an account's masked hint is never part of it. IDurableModels (the model base URLs) is resolved
// on use, not injected, for the same reason as ModelPrices'. Offline (--faux, Offline) the active catalog is the faux one and nothing else is
// ever called, so Accounts lists only the providers it holds: a machine's environment keys for real providers are not shown as unpriced
// accounts with Unknown-price rules that cannot apply (W3 fix, spend-ui).
using System.Collections.Frozen;
using System.Net;
using PiSharp.Durable;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Settings;

namespace PiSharp.WebAgent.Spend;

/// <summary>One row of Settings › Spend › Accounts: an account (or a provider that needs none) and how it is billed.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="ProviderName">The provider's display name (its id when the catalog does not list it).</param>
/// <param name="LogoKey">The provider's bundled logo, or null for a neutral badge.</param>
/// <param name="Account">The account, or null for a provider that needs none (the faux provider, a loopback server).</param>
/// <param name="Billing">How it is billed, and why.</param>
public sealed record BillingAccount(string Provider, string ProviderName, string? LogoKey, AccountInfo? Account, BillingResolution Billing)
{
    /// <summary>The loopback address its models are served from (<c>localhost:1234</c>) when it is Free for that reason, else null.</summary>
    public string? Endpoint { get; init; }
}

/// <summary>Resolves the billing mode of a provider account (<see cref="IBillingModes"/>). A singleton; safe to call from many threads.</summary>
public sealed class BillingModes : IBillingModes
{
    /// <summary>The providers whose accounts are subscriptions: OpenAI Codex (ChatGPT sign-in) and GitHub Copilot.</summary>
    public static IReadOnlySet<string> SubscriptionProviders { get; } = new[] { "openai-codex", "github-copilot" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly IModelCatalog catalog;
    private readonly ISettingsStore settings;
    private readonly ModelPrices prices;
    private readonly IAccountStore? accounts;
    private readonly Func<IDurableModels?> models;

    /// <summary>Billing modes from <paramref name="catalog"/>'s providers, <paramref name="accounts"/>' account kinds (null: no accounts
    /// known), <paramref name="prices"/> (whether a provider has any price), the model base URLs of <paramref name="models"/> (resolved on
    /// each use; null: no loopback detection) and the overrides in <paramref name="settings"/>' <see cref="SpendSettings"/>.</summary>
    public BillingModes(IModelCatalog catalog, ISettingsStore settings, ModelPrices prices, IAccountStore? accounts = null, Func<IDurableModels?>? models = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prices);
        this.catalog = catalog;
        this.settings = settings;
        this.prices = prices;
        this.accounts = accounts;
        this.models = models ?? (() => null);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    public BillingResolution Resolve(string provider, string? account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        provider = provider.Trim();
        account = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        var (detected, basis, _) = Detect(provider, account is null ? null : accounts?.Get(account));
        return Apply(provider, account, detected, basis);
    }

    /// <summary>Whether the app runs offline (<c>--faux</c>, <see cref="Hosting.WebAgentOptions.Faux"/>): the catalog is the faux one and no
    /// other provider is ever called, so <see cref="Accounts"/> leaves out accounts of providers the catalog does not list.</summary>
    public bool Offline { get; init; }

    /// <summary>Every account and how it is billed, for Settings › Spend: the catalog's providers in display order, each provider's accounts
    /// (<see cref="IAccountStore.List"/> order), and one row without an account for a provider that needs none; then accounts of providers
    /// the catalog does not list, unless <see cref="Offline"/> (they are not used offline).</summary>
    public IReadOnlyList<BillingAccount> Accounts()
    {
        var all = accounts?.List() ?? [];
        var rows = new List<BillingAccount>();
        foreach (var provider in catalog.Providers)
        {
            var own = all.Where(account => account.Provider == provider.Id).ToList();
            if (own.Count == 0 && (provider.AuthKinds.Count == 0 || provider.AuthKinds.All(kind => kind == ProviderAuthKind.None)))
                rows.Add(Row(provider.Id, provider, null));
            rows.AddRange(own.Select(account => Row(provider.Id, provider, account)));
        }
        if (!Offline)
            rows.AddRange(all.Where(account => catalog.FindProvider(account.Provider) is null).Select(account => Row(account.Provider, null, account)));
        return rows;
    }

    /// <summary>Whether <paramref name="baseUrl"/> is an http(s) address on this machine (<c>localhost</c>, <c>*.localhost</c>, 127.0.0.0/8,
    /// <c>[::1]</c>); <paramref name="endpoint"/> is then its host and port, such as <c>localhost:1234</c>.</summary>
    public static bool IsLoopback(string? baseUrl, out string endpoint)
    {
        endpoint = "";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        var host = uri.IdnHost;
        var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
        if (local) endpoint = uri.Authority;
        return local;
    }

    private BillingAccount Row(string providerId, CatalogProvider? provider, AccountInfo? account)
    {
        var (detected, basis, endpoint) = Detect(providerId, account);
        return new BillingAccount(providerId, provider?.DisplayName ?? providerId, provider?.LogoKey, account, Apply(providerId, account?.Id, detected, basis))
        {
            Endpoint = endpoint,
        };
    }

    private BillingResolution Apply(string provider, string? account, BillingKind detected, string basis) =>
        settings.Get<SpendSettings>().OverrideFor(provider, account) is { } chosen
            ? new BillingResolution(chosen.Mode, detected, true, basis)
            : new BillingResolution(detected, detected, false, basis);

    /// <summary>The mode worked out from how <paramref name="account"/> (of <paramref name="provider"/>, or null) connects, why, and the
    /// loopback endpoint when that is why.</summary>
    private (BillingKind Mode, string Basis, string? Endpoint) Detect(string provider, AccountInfo? account)
    {
        if (account is not null && account.Provider != provider) account = null;
        var listed = catalog.FindProvider(provider);
        if (listed is null) return (BillingKind.Unknown, "Not in the model catalog", null);
        if (provider == FauxDurableModels.ProviderId)
            return listed.Billing == BillingKind.Free ? (BillingKind.Free, "Offline faux models", null) : (listed.Billing, "Offline faux models · test list price", null);
        if (Loopback(provider) is { } endpoint) return (BillingKind.Free, endpoint + " · runs on this machine", endpoint);
        if (SubscriptionProviders.Contains(provider) || listed.Billing == BillingKind.Subscription || account?.Kind == AccountKind.OAuth)
            return (BillingKind.Subscription, SubscriptionBasis(listed, account), null);
        if (listed.Billing == BillingKind.Free) return (BillingKind.Free, "Free provider", null);
        if (!prices.AnyPriced(provider)) return (BillingKind.Unknown, "No price in the catalog", null);
        return (BillingKind.Metered, account?.Kind switch
        {
            AccountKind.ApiKey => "Stored API key",
            AccountKind.Environment => "Environment · " + (account.Source ?? "variable"),
            AccountKind.Ambient => "Ambient · " + (account.Source ?? "SDK credentials"),
            _ => "Paid per token",
        }, null);
    }

    /// <summary>The loopback endpoint of <paramref name="provider"/> when every model it lists is served from one, else null.</summary>
    private string? Loopback(string provider)
    {
        if (models() is not { } durable) return null;
        string? first = null;
        foreach (var model in catalog.Models(provider))
        {
            if (!IsLoopback(durable.GetModel(provider, model.Id)?.BaseUrl, out var endpoint)) return null;
            first ??= endpoint;
        }
        return first;
    }

    private static string SubscriptionBasis(CatalogProvider provider, AccountInfo? account) => account?.Kind switch
    {
        AccountKind.OAuth when account.Details.TryGetValue("plan", out var plan) => SettingsText.Plan(plan),
        AccountKind.OAuth when account.Details.TryGetValue("host", out var host) => provider.DisplayName + " · " + host,
        AccountKind.OAuth => provider.Id == SignInFlows.CodexProvider ? "ChatGPT sign-in" : provider.DisplayName + " sign-in",
        AccountKind.Environment => "Environment · " + (account.Source ?? "variable"),
        _ => provider.DisplayName + " subscription",
    };
}
