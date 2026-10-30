// Decision 0011 (run W3, SU): the first-run wizard, mapped 1:1 from the mockups' setup.html: GET /setup (Views/Setup/Index.cshtml), a full
// page without the sidebar, with the steps Welcome → Connect → Default model → Done. ?step= names the step to show (setup.js keeps it in the
// address, so the reload after a key is saved or a sign-in ends comes back to the same step). The page is always reachable, also after the
// wizard ended and with --faux; whether / and /chat/new send the user here is SetupRedirect's. It reads ProviderDirectory and
// ISettingsStore and changes things only through the JSON APIs (/api/credentials for keys and sign-ins, /api/setup for the wizard's own
// steps), so no key is ever part of the page: a connected account shows its masked hint at most. Every answer is no-store.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Settings;
using PiSharp.WebAgent.Setup;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The setup wizard.</summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SetupController(ProviderDirectory directory, ISettingsStore settings, IModelCatalog catalog, WebAgentOptions options,
    CredentialsOptions? credentials = null) : Controller
{
    /// <summary>The wizard, on step <paramref name="step"/> (welcome, connect, model or done; default welcome).</summary>
    [HttpGet(SetupModule.SetupPath)]
    public IActionResult Index(string? step)
    {
        var host = credentials?.Environment(SettingsController.CallbackHostVariable);
        var modals = new CredentialModals(directory.Offline, credentials?.CodexCallbackPort ?? 1455,
            string.IsNullOrWhiteSpace(host) ? SettingsController.DefaultCallbackHost : host.Trim());
        var origin = $"{Request.Scheme}://{Request.Host.Value}";
        return View("Index", SetupPage.Build(directory, settings, catalog, modals, origin, options.DataDirectory, step));
    }
}
