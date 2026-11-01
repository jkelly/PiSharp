// Decision 0011 (run W3, unit AP): Settings › Appearance (the mockups' settings-appearance, route GET /settings/appearance), mapped 1:1:
// the theme cards, Display and Messages with the live preview and the save bar. The preferences are this browser's (AppearancePrefs): the
// page holds no stored value of its own, so wwwroot/js/appearance.js sets every control from localStorage when the page loads, and Save
// writes them back there. The server only names the default model for the preview's sample reply.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Appearance;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Controllers;

/// <summary>Settings › Appearance.</summary>
public sealed class AppearanceController(ISettingsStore settings) : Controller
{
    /// <summary>The page's route.</summary>
    public const string Route = "/settings/appearance";

    /// <summary>Settings › Appearance.</summary>
    [HttpGet(Route)]
    public IActionResult Index()
    {
        var defaults = settings.Get<DefaultsSettings>();
        var sample = defaults is { Provider: { } provider, Model: { } model } ? provider + "/" + model : null;
        return View("~/Views/Settings/Appearance.cshtml", new AppearancePage(sample));
    }
}
