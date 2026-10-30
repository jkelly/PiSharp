// Decision 0011 (run W2, WU4): the shell's own pages: the neutral "Coming in a later milestone" page behind every sidebar link
// that a later milestone builds (LaterFeature). The chat pages are the Chat area's (ChatController) and the M0 settings pages the Settings
// area's (SettingsController).
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Shell;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The shell's placeholder pages.</summary>
public sealed class ShellController : Controller
{
    /// <summary>A sidebar page a later milestone builds.</summary>
    public IActionResult Later() =>
        LaterFeature.Pages.TryGetValue((Request.Path.Value ?? "").TrimEnd('/'), out var feature) ? View("Later", feature) : NotFound();
}
