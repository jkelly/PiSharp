// Decision 0011 (run W4, R1): the Playground (GET /playground, after the mockups' playground.html) and the fragments its page swaps in.
// ?run={id} opens a run of runs.db: its request in the editors and its output (live while it runs: the page reconnects to its events, so a
// run survives a closed tab); ?provider=&model= opens a model. The fragments are rendered here, through the shared partials and the
// sanitizing MarkdownRenderer, so the page never builds HTML from model text: _RunOutput when a run ends, _ErrorDetailsModal for Details.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Playground;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The Playground's page and fragments.</summary>
public sealed class PlaygroundController(PlaygroundPages pages) : Controller
{
    /// <summary>The Playground.</summary>
    [HttpGet("/playground")]
    public async Task<IActionResult> Index(string? run, string? provider, string? model, CancellationToken cancellationToken) =>
        View(await pages.PageAsync(run, provider, model, cancellationToken).ConfigureAwait(false));

    /// <summary>The empty output pane (Reset › Clear output).</summary>
    [HttpGet("/playground/output")]
    public IActionResult EmptyOutput()
    {
        Response.Headers.CacheControl = "no-store";
        return PartialView("_RunOutput", new RunOutputView());
    }

    /// <summary>The output pane of run <paramref name="id"/> (<c>_RunOutput</c>).</summary>
    [HttpGet("/playground/runs/{id}/output")]
    public IActionResult Output(string id)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.Output(id) is { } view ? PartialView("_RunOutput", view) : NotFound();
    }

    /// <summary>The error details of run <paramref name="id"/> (<c>_ErrorDetailsModal</c>).</summary>
    [HttpGet("/playground/runs/{id}/error")]
    public IActionResult Error(string id)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.ErrorDetails(id) is { } view ? PartialView("_ErrorDetailsModal", view) : NotFound();
    }
}
