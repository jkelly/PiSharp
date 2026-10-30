// Decision 0011 (run W2, WU9): the models of the settings pages (Views/Settings) and of their partials. SettingsController builds them from
// ProviderDirectory, IModelCatalog and ISettingsStore; the views only format. The Models page filters and pages on the server (a GET form:
// the catalog can hold well over a thousand models), so it works without script and its links can be bookmarked.
using System.Globalization;
using Microsoft.AspNetCore.Http;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.Settings;

/// <summary>What the credential modals (<c>_CredentialModals</c>) need: whether the app is offline, and where the ChatGPT callback listens.</summary>
/// <param name="Offline">Whether the app runs offline (--faux): no connection tests and no sign-ins.</param>
/// <param name="CallbackPort">The ChatGPT browser sign-in's callback port.</param>
/// <param name="CallbackHost">The host the callback listener binds to (<c>PI_OAUTH_CALLBACK_HOST</c>, default <c>127.0.0.1</c>).</param>
public sealed record CredentialModals(bool Offline, int CallbackPort, string CallbackHost)
{
    /// <summary>The openai-codex mark.</summary>
    public string? CodexLogo => ProviderLogos.Url(SignInFlows.CodexProvider);

    /// <summary>The github-copilot mark.</summary>
    public string? CopilotLogo => ProviderLogos.Url(SignInFlows.CopilotProvider);
}

/// <summary>Settings › Providers.</summary>
/// <param name="Providers">Every provider, in directory order.</param>
/// <param name="Offline">Whether the app runs offline (--faux).</param>
public sealed record ProvidersPage(IReadOnlyList<ProviderEntry> Providers, bool Offline)
{
    /// <summary>The credential modals.</summary>
    public required CredentialModals Modals { get; init; }

    /// <summary>How many providers are listed before "Show N more" in Available providers.</summary>
    public const int AvailableShown = 8;

    /// <summary>Providers with a credential (usable, environment or expired), and faux.</summary>
    public IReadOnlyList<ProviderEntry> Connected => [.. Providers.Where(entry => entry.State != ProviderState.NotConfigured)];

    /// <summary>Providers with no usable account.</summary>
    public IReadOnlyList<ProviderEntry> Available => [.. Providers.Where(entry => entry.State == ProviderState.NotConfigured)];

    /// <summary>The count of the status filter <paramref name="state"/> (null for All).</summary>
    public int Count(ProviderState? state) => state is null ? Providers.Count : Providers.Count(entry => entry.State == state);

    /// <summary>Whether no provider is connected beyond the ones that need no credentials (the empty state shows).</summary>
    public bool NothingConnected => Providers.All(entry => entry.NoCredentials || entry.State == ProviderState.NotConfigured);
}

/// <summary>A provider's detail page (Settings › Providers › name).</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Defaults">The Defaults section (its model gets the "default" chip).</param>
/// <param name="Visibility">The Models section (hidden models).</param>
/// <param name="Offline">Whether the app runs offline.</param>
public sealed record ProviderPage(ProviderEntry Provider, DefaultsSettings Defaults, ModelsSettings Visibility, bool Offline)
{
    /// <summary>The credential modals.</summary>
    public required CredentialModals Modals { get; init; }

    /// <summary>The account to warn about: the active account when expired, else the only account when expired.</summary>
    public AccountInfo? ExpiredAccount => Provider.Active is { Status: AccountStatus.Expired } active ? active
        : Provider.Active is null && Provider.Accounts.Count(account => account.Status == AccountStatus.Expired) == 1
            ? Provider.Accounts.First(account => account.Status == AccountStatus.Expired) : null;

    /// <summary>Whether "Forget all settings" has anything to forget.</summary>
    public bool HasSomethingToForget => Provider.StoredAccounts.Any() ||
        (Visibility.Hidden ?? []).Any(key => key.StartsWith(Provider.Id + "/", StringComparison.Ordinal));
}

/// <summary>A row of the API key and sign-in buttons: what the button opens.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Label">The button text.</param>
/// <param name="Classes">The button classes.</param>
public sealed record CredentialButton(ProviderEntry Provider, string Label, string Classes)
{
    /// <summary>Whether it opens the provider's sign-in modal (otherwise the API key modal).</summary>
    public bool SignIn { get; init; }

    /// <summary>For the API key modal: replace this account's key instead of adding one. For a sign-in: sign this account in again.</summary>
    public AccountInfo? Account { get; init; }

    /// <summary>For a sign-in: the method to start straight away (otherwise the modal's first step).</summary>
    public SignInMethod? Method { get; init; }

    /// <summary>A Bootstrap icon before the label.</summary>
    public string? Icon { get; init; }

    /// <summary>Rendered as a dropdown item instead of a button.</summary>
    public bool DropdownItem { get; init; }
}

/// <summary>A provider logo (<c>_ProviderLogo</c>): its mark, or its initial when Web.Common has no mark.</summary>
/// <param name="Name">The provider's name (the label).</param>
/// <param name="Url">The mark, or null.</param>
/// <param name="Size">An extra size class: <c>lg</c>, <c>sm</c> or empty.</param>
public sealed record LogoModel(string Name, string? Url, string Size = "")
{
    /// <summary>The logo of <paramref name="provider"/>.</summary>
    public static LogoModel Of(ProviderEntry provider, string size = "")
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new LogoModel(provider.Name, provider.LogoUrl, size);
    }
}

/// <summary>An account's card on a provider page (<c>_AccountCard</c>).</summary>
/// <param name="Provider">Its provider.</param>
/// <param name="Account">The account.</param>
/// <param name="Offline">Whether the app runs offline (no connection tests, no sign-ins).</param>
public sealed record AccountCard(ProviderEntry Provider, AccountInfo Account, bool Offline);

/// <summary>Settings › Accounts &amp; sign-ins.</summary>
/// <param name="Providers">The providers that sign in (ISignInFlows.Methods), in directory order.</param>
/// <param name="DataDirectory">The app's data folder (where credentials.json and keys\ are).</param>
/// <param name="CallbackHost">The browser sign-in callback host (<c>PI_OAUTH_CALLBACK_HOST</c>), or null for the default.</param>
/// <param name="CallbackPort">The ChatGPT callback port.</param>
/// <param name="Offline">Whether the app runs offline.</param>
public sealed record AccountsPage(IReadOnlyList<ProviderEntry> Providers, string DataDirectory, string? CallbackHost, int CallbackPort, bool Offline)
{
    /// <summary>The credential modals.</summary>
    public required CredentialModals Modals { get; init; }

    /// <summary>The sign-in accounts of these providers.</summary>
    public IReadOnlyList<AccountInfo> SignIns => [.. Providers.SelectMany(entry => entry.Accounts.Where(account => account.Kind == AccountKind.OAuth))];

    /// <summary>The connected sign-in accounts.</summary>
    public int ConnectedCount => SignIns.Count(account => account.Status == AccountStatus.Connected);

    /// <summary>How many providers have a connected sign-in.</summary>
    public int ConnectedProviders => Providers.Count(entry => entry.Accounts.Any(account => account.Kind == AccountKind.OAuth && account.Status == AccountStatus.Connected));

    /// <summary>The expired sign-in accounts.</summary>
    public int ExpiredCount => SignIns.Count(account => account.Status == AccountStatus.Expired);

    /// <summary>Providers without any sign-in account.</summary>
    public int NotConnected => Providers.Count(entry => !entry.Accounts.Any(account => account.Kind == AccountKind.OAuth));

    /// <summary>The custom providers of models.json whose key is a vault account (named "&lt;name&gt; key"), which Settings › Custom providers
    /// manages (W3 fix: the page notes them, since they are API keys, not sign-ins).</summary>
    public IReadOnlyList<ProviderEntry> CustomKeys { get; init; } = [];
}

/// <summary>The Models page's filters, from its query string: <c>q</c>, <c>provider</c> (repeated), <c>connected</c>, <c>enabled</c>,
/// <c>reasoning</c>, <c>vision</c>, <c>free</c>, <c>long</c> and <c>page</c>. <c>f=1</c> marks a submitted form, so an unticked "Connected
/// providers only" (on by default) stays off.</summary>
public sealed record ModelsQuery
{
    /// <summary>The search text.</summary>
    public string Search { get; init; } = "";

    /// <summary>The providers ticked in the provider menu (none: every provider).</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    /// <summary>Only providers that are connected (default on).</summary>
    public bool ConnectedOnly { get; init; } = true;

    /// <summary>Only models shown in the pickers (not hidden).</summary>
    public bool EnabledOnly { get; init; }

    /// <summary>Only reasoning models.</summary>
    public bool Reasoning { get; init; }

    /// <summary>Only models with image input.</summary>
    public bool Vision { get; init; }

    /// <summary>Only free models.</summary>
    public bool Free { get; init; }

    /// <summary>Only models with a context window of 1M tokens or more.</summary>
    public bool LongContext { get; init; }

    /// <summary>The page, from 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Whether any filter beyond the defaults is set.</summary>
    public bool Filtered => Search.Length > 0 || Providers.Count > 0 || EnabledOnly || Reasoning || Vision || Free || LongContext;

    /// <summary>Reads the filters from <paramref name="query"/>.</summary>
    public static ModelsQuery From(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        static bool On(IQueryCollection query, string name) => query.TryGetValue(name, out var values) && values.Any(value => value is "1" or "true" or "on");
        var submitted = On(query, "f");
        var search = query["q"].ToString().Trim();
        return new ModelsQuery
        {
            Search = search.Length > 200 ? search[..200] : search,
            Providers = [.. query["provider"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.Ordinal)],
            ConnectedOnly = submitted ? On(query, "connected") : !query.ContainsKey("connected") || On(query, "connected"),
            EnabledOnly = On(query, "enabled"),
            Reasoning = On(query, "reasoning"),
            Vision = On(query, "vision"),
            Free = On(query, "free"),
            LongContext = On(query, "long"),
            Page = int.TryParse(query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1,
        };
    }

    /// <summary>The query string of these filters on page <paramref name="page"/> (for paging links), starting with <c>?</c>.</summary>
    public string Href(int page)
    {
        var parts = new List<string> { "f=1" };
        if (Search.Length > 0) parts.Add("q=" + Uri.EscapeDataString(Search));
        parts.AddRange(Providers.Select(provider => "provider=" + Uri.EscapeDataString(provider)));
        if (ConnectedOnly) parts.Add("connected=1");
        if (EnabledOnly) parts.Add("enabled=1");
        if (Reasoning) parts.Add("reasoning=1");
        if (Vision) parts.Add("vision=1");
        if (Free) parts.Add("free=1");
        if (LongContext) parts.Add("long=1");
        if (page > 1) parts.Add("page=" + page.ToString(CultureInfo.InvariantCulture));
        return "?" + string.Join("&", parts);
    }
}

/// <summary>A model row of the Models page.</summary>
/// <param name="Provider">Its provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Hidden">Whether it is hidden from the pickers.</param>
/// <param name="IsDefault">Whether it is the default model (Settings › Defaults).</param>
public sealed record ModelRow(ProviderEntry Provider, CatalogModel Model, bool Hidden, bool IsDefault)
{
    /// <summary>Its <c>provider/model</c> key.</summary>
    public string Key => ModelsSettings.Key(Provider.Id, Model.Id);
}

/// <summary>A provider's rows on one page of the Models table.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Rows">Its rows on this page.</param>
/// <param name="Matches">Its models that match the filters (on every page).</param>
/// <param name="Enabled">Its models that are not hidden.</param>
public sealed record ModelGroup(ProviderEntry Provider, IReadOnlyList<ModelRow> Rows, int Matches, int Enabled);

/// <summary>Settings › Models.</summary>
public sealed record ModelsPage
{
    /// <summary>Models per page.</summary>
    public const int PageSize = 50;

    /// <summary>The filters.</summary>
    public required ModelsQuery Query { get; init; }

    /// <summary>The groups of this page.</summary>
    public required IReadOnlyList<ModelGroup> Groups { get; init; }

    /// <summary>Every provider with catalog models, for the provider menu.</summary>
    public required IReadOnlyList<ProviderEntry> CatalogProviders { get; init; }

    /// <summary>Models of connected providers.</summary>
    public int Usable { get; init; }

    /// <summary>Usable models not hidden.</summary>
    public int Enabled { get; init; }

    /// <summary>Connected providers with catalog models.</summary>
    public int ConnectedProviders { get; init; }

    /// <summary>Every model of the catalog.</summary>
    public int CatalogTotal { get; init; }

    /// <summary>Models that match the filters.</summary>
    public int Matches { get; init; }

    /// <summary>The page shown (1-based, clamped).</summary>
    public int Page { get; init; } = 1;

    /// <summary>The number of pages (at least 1).</summary>
    public int Pages { get; init; } = 1;

    /// <summary>The global default thinking level (Settings › Defaults), or null for the model's own.</summary>
    public string? DefaultThinking { get; init; }

    /// <summary>The Models section as saved.</summary>
    public required ModelsSettings Visibility { get; init; }

    /// <summary>Whether the app runs offline.</summary>
    public bool Offline { get; init; }

    /// <summary>The rows of this page.</summary>
    public int Shown => Groups.Sum(group => group.Rows.Count);

    /// <summary>Builds the page from the providers, the saved sections and the filters.</summary>
    public static ModelsPage Build(IReadOnlyList<ProviderEntry> providers, DefaultsSettings defaults, ModelsSettings visibility, ModelsQuery query, bool offline)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(query);
        var catalogProviders = providers.Where(entry => entry.Models.Count > 0).ToList();
        var matches = new List<ModelRow>();
        foreach (var entry in catalogProviders)
        {
            if (query.ConnectedOnly && !entry.IsConnected) continue;
            if (query.Providers.Count > 0 && !query.Providers.Contains(entry.Id, StringComparer.Ordinal)) continue;
            foreach (var model in entry.Models)
            {
                var hidden = visibility.IsHidden(entry.Id, model.Id);
                if (query.EnabledOnly && hidden) continue;
                if (query.Reasoning && !model.Reasoning) continue;
                if (query.Vision && !model.Input.Contains("image", StringComparer.Ordinal)) continue;
                if (query.Free && !SettingsText.IsFree(entry.Billing, model)) continue;
                if (query.LongContext && model.ContextWindow < 1_000_000) continue;
                if (query.Search.Length > 0 && !IsMatch(query.Search, entry, model)) continue;
                matches.Add(new ModelRow(entry, model, hidden, defaults.Provider == entry.Id && defaults.Model == model.Id));
            }
        }
        var pages = Math.Max(1, (matches.Count + PageSize - 1) / PageSize);
        var page = Math.Clamp(query.Page, 1, pages);
        var groups = matches.Skip((page - 1) * PageSize).Take(PageSize).GroupBy(row => row.Provider.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var entry = group.First().Provider;
                return new ModelGroup(entry, [.. group], matches.Count(row => row.Provider.Id == entry.Id),
                    entry.Models.Count(model => !visibility.IsHidden(entry.Id, model.Id)));
            }).ToList();
        var connected = catalogProviders.Where(entry => entry.IsConnected).ToList();
        return new ModelsPage
        {
            Query = query,
            Groups = groups,
            CatalogProviders = catalogProviders,
            Usable = connected.Sum(entry => entry.Models.Count),
            Enabled = connected.Sum(entry => entry.Models.Count(model => !visibility.IsHidden(entry.Id, model.Id))),
            ConnectedProviders = connected.Count,
            CatalogTotal = catalogProviders.Sum(entry => entry.Models.Count),
            Matches = matches.Count,
            Page = page,
            Pages = pages,
            DefaultThinking = defaults.Thinking,
            Visibility = visibility,
            Offline = offline,
        };
    }

    private static bool IsMatch(string search, ProviderEntry entry, CatalogModel model) =>
        model.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || model.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entry.Id.Contains(search, StringComparison.OrdinalIgnoreCase) || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        ModelsSettings.Key(entry.Id, model.Id).Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Settings › Defaults.</summary>
/// <param name="Defaults">The Defaults section as saved.</param>
/// <param name="Providers">The providers the Provider dropdown offers (the connected ones, and the saved default's).</param>
/// <param name="Offline">Whether the app runs offline.</param>
public sealed record DefaultsPage(DefaultsSettings Defaults, IReadOnlyCollection<string> Providers, bool Offline);

/// <summary>Settings › Tools &amp; workspace.</summary>
/// <param name="Tools">The Tools section as saved.</param>
/// <param name="DirectoryExists">Whether the working directory exists.</param>
/// <param name="ContextFiles">The context files in it.</param>
public sealed record ToolsPage(ToolsSettings Tools, bool DirectoryExists, IReadOnlyList<string> ContextFiles)
{
    /// <summary>The working directory new chats use.</summary>
    public string Directory => Tools.EffectiveWorkingDirectory;

    /// <summary>The approval mode as the radio values say it: ask, allowList, auto.</summary>
    public string Mode => Tools.ApprovalMode switch
    {
        ToolApprovalMode.AllowList => "allowList",
        ToolApprovalMode.Auto => "auto",
        _ => "ask",
    };
}

/// <summary>A section a later milestone builds (<c>_LaterMilestone</c>): its title and one sentence on what it will hold.</summary>
/// <param name="Title">The section's title.</param>
/// <param name="Text">What it will hold.</param>
/// <param name="Id">The section's anchor id, or null.</param>
public sealed record LaterMilestone(string Title, string Text, string? Id = null);

/// <summary>One model's details for <c>_ModelDetailsModal</c>, serialized into the details button's <c>data-model</c> attribute (metadata
/// only: the catalog's facts, whether it is hidden, and the provider's credential as its chip says it).</summary>
/// <param name="Key">The <c>provider/model</c> key.</param>
/// <param name="Provider">The provider id.</param>
/// <param name="ProviderName">The provider's name.</param>
/// <param name="Logo">The provider's mark, or null.</param>
/// <param name="Id">The model id.</param>
/// <param name="Name">The model's name.</param>
/// <param name="Credential">The provider's active credential as a chip says it (<c>stored key · sk-…Q7x2</c>), or null.</param>
/// <param name="Billing">The billing word: metered, subscription, free or unknown.</param>
/// <param name="Model">The catalog model.</param>
/// <param name="Hidden">Whether it is hidden from the pickers.</param>
/// <param name="IsDefault">Whether it is the default model.</param>
public sealed record ModelDetails(string Key, string Provider, string ProviderName, string? Logo, string Id, string Name, string? Credential, string Billing,
    CatalogModel Model, bool Hidden, bool IsDefault)
{
    /// <summary>The details of <paramref name="row"/>.</summary>
    public static ModelDetails Of(ModelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var provider = row.Provider;
        var active = provider.Active;
        var credential = provider.Custom is { } custom ? custom.Chip : provider.NoCredentials ? "none needed" : active is null ? null
            : active.Hint.Length > 0 ? SettingsText.KindChip(active) + " · " + active.Hint : SettingsText.KindChip(active);
        return new ModelDetails(row.Key, provider.Id, provider.Name, provider.LogoUrl, row.Model.Id, row.Model.Name, credential,
            provider.Billing.ToString().ToLowerInvariant(), row.Model, row.Hidden, row.IsDefault);
    }
}
