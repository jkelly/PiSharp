// Decision 0011 (run W3, CP): Settings › Custom providers, mapped 1:1 from the mockups' settings-custom-providers page (states default and
// empty) with its _CustomProviderModal editor. The page reads the custom providers area (CustomProvidersPage) and changes things only
// through /api/custom-providers, so a key is POSTed once from the editor and no page ever holds one. It is no-store, since it names the
// stored keys' masked hints.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Controllers;

/// <summary>Settings › Custom providers.</summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CustomProvidersController(CustomProviderService service, IModelCatalog catalog, WebAgentOptions options) : Controller
{
    /// <summary>The page.</summary>
    [HttpGet("/settings/custom-providers")]
    public IActionResult Index() => View("~/Views/Settings/CustomProviders.cshtml", CustomProvidersPage.Build(service, catalog, options.Faux));
}
