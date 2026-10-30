// Decision 0011 (run W3, unit AP): Settings › About (the mockups' settings-about, route GET /settings/about), mapped 1:1: the brand header,
// Versions (software and host), Model catalog, Diagnostics (the report, copy and download, and the provider connectivity check, which runs
// only when the user clicks it) and Links (documentation, source, license, third-party notices). Every number on it is read from the running
// app (AboutService). No-store, since it lists the accounts' providers.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.About;

namespace PiSharp.WebAgent.Controllers;

/// <summary>Settings › About.</summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AboutController(AboutService about) : Controller
{
    /// <summary>The page's route.</summary>
    public const string Route = "/settings/about";

    /// <summary>Settings › About.</summary>
    [HttpGet(Route)]
    public IActionResult Index() => View("~/Views/Settings/About.cshtml", about.Page());
}
