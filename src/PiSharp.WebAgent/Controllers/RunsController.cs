// Decision 0011 (run W4, R3): History › Runs (GET /history, after the mockups' history.html) and the Run inspector (GET /runs/{id}, after
// inspector.html). /runs/{id} also takes a run's op (a ledger row's run-<id>, a chat call's op) and redirects to the run's own address; an
// unknown run is a 404 page that says so. Both pages are no-store: they show runs.db as it is now.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Controllers;

/// <summary>History › Runs and the Run inspector.</summary>
public sealed class RunsController(RunHistoryPages history, RunInspectorPages inspector) : Controller
{
    /// <summary>History › Runs.</summary>
    [HttpGet("/history")]
    public IActionResult Index()
    {
        Response.Headers.CacheControl = "no-store";
        return View(history.Page(RunHistoryFilter.Parse(Request.Query)));
    }

    /// <summary>The Run inspector.</summary>
    [HttpGet("/runs/{id}")]
    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (inspector.Find(id) is not { } run)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("NotFound", id);
        }
        // W5 integration fix 1: a run of a blind round with no vote yet is a notice that links to the battle (RunBlinds).
        if (RunBlinds.Of(HttpContext.RequestServices).Hidden(run) is { } battle) return View("Blind", new RunBlindView(run.Id, battle));
        if (!string.Equals(run.Id, id, StringComparison.Ordinal)) return Redirect("/runs/" + Uri.EscapeDataString(run.Id));
        return View(await inspector.ViewAsync(run, $"{Request.Scheme}://{Request.Host}", cancellationToken).ConfigureAwait(false));
    }
}
