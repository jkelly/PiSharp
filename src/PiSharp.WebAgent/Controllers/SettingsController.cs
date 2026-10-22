// Decision 0011 (run W2, WU9): the M0 settings pages, mapped 1:1 from the mockups: Providers (settings-providers), one Provider detail view
// for every provider (settings-provider-openai, settings-provider-codex), Accounts & sign-ins (settings-accounts), Models (settings-models),
// Defaults (settings-defaults) and Tools & workspace (settings-tools). The pages read ProviderDirectory and ISettingsStore and change things
// only through the JSON APIs (/api/credentials of WU5, /api/settings of this area), so a key is POSTed once from the modal and no page ever
// holds one: an account shows its masked hint at most. Settings pages that later milestones build stay ShellController's. Every page is
// no-store, since it lists accounts.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Settings;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The settings pages of M0.</summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SettingsController(ProviderDirectory directory, ISettingsStore settings, WebAgentOptions options, CredentialsOptions? credentials = null)
    : Controller
{
    /// <summary>The environment variable that moves the browser sign-in callback host.</summary>
    public const string CallbackHostVariable = "PI_OAUTH_CALLBACK_HOST";

    /// <summary>The callback host when <see cref="CallbackHostVariable"/> is not set.</summary>
    public const string DefaultCallbackHost = "127.0.0.1";

    /// <summary><c>/settings</c> opens Providers, the first settings page.</summary>
    [HttpGet("/settings")]
    public IActionResult Index() => Redirect("/settings/providers");

    /// <summary>Settings › Providers.</summary>
    [HttpGet("/settings/providers")]
    public IActionResult Providers() => View("Providers", new ProvidersPage(directory.List(), directory.Offline) { Modals = Modals() });

    /// <summary>A provider's detail page; 404 for a provider nothing knows.</summary>
    [HttpGet("/settings/providers/{id}")]
    public IActionResult Provider(string id) => directory.Find(id) is { } entry
        ? View("Provider", new ProviderPage(entry, settings.Get<DefaultsSettings>(), settings.Get<ModelsSettings>(), directory.Offline) { Modals = Modals() })
        : NotFound();

    /// <summary>Settings › Accounts &amp; sign-ins.</summary>
    [HttpGet("/settings/accounts")]
    public IActionResult Accounts()
    {
        var providers = directory.List().Where(entry => entry.SignsIn).ToList();
        var modals = Modals();
        return View("Accounts", new AccountsPage(providers, options.DataDirectory, CallbackHost(), modals.CallbackPort, directory.Offline) { Modals = modals });
    }

    /// <summary>Settings › Models, filtered and paged by its query string (<see cref="ModelsQuery"/>).</summary>
    [HttpGet("/settings/models")]
    public IActionResult Models() => View("Models", ModelsPage.Build(directory.List(), settings.Get<DefaultsSettings>(), settings.Get<ModelsSettings>(),
        ModelsQuery.From(Request.Query), directory.Offline));

    /// <summary>Settings › Defaults.</summary>
    [HttpGet("/settings/defaults")]
    public IActionResult Defaults()
    {
        var defaults = settings.Get<DefaultsSettings>();
        var offered = directory.ConnectedIds().ToHashSet(StringComparer.Ordinal);
        if (defaults.Provider is { } provider) offered.Add(provider);
        return View("Defaults", new DefaultsPage(defaults, offered, directory.Offline));
    }

    /// <summary>Settings › Tools &amp; workspace.</summary>
    [HttpGet("/settings/tools")]
    public IActionResult Tools()
    {
        var tools = settings.Get<ToolsSettings>();
        var folder = tools.EffectiveWorkingDirectory;
        return View("Tools", new ToolsPage(tools, FolderBrowser.Exists(folder), FolderBrowser.ContextFiles(folder)));
    }

    // The callback host set by PI_OAUTH_CALLBACK_HOST, or null for the default.
    private string? CallbackHost()
    {
        var host = credentials?.Environment(CallbackHostVariable);
        return string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }

    private CredentialModals Modals() =>
        new(directory.Offline, credentials?.CodexCallbackPort ?? 1455, CallbackHost() ?? DefaultCallbackHost);
}
