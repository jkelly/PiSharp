// Decision 0011 (run W4, R7): the Tool Bench (GET /bench, after the mockups' tool-bench.html). ?session={id} opens a bench session;
// without it the page opens the latest session (or a new one). The page's parts are rendered by the same partials the JSON API returns
// (BenchPages.FragmentsAsync), so an update swaps in server markup and the page never builds HTML from model text.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Bench;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The Tool Bench page.</summary>
public sealed class BenchController(BenchPages pages) : Controller
{
    /// <summary>The Tool Bench.</summary>
    [HttpGet("/bench")]
    public IActionResult Index(string? session) => View(pages.Page(session));
}
