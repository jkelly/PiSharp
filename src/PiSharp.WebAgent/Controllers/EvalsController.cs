// Decision 0011 (run W5, E6): the Evals pages (mockups evals-leaderboard, evals-suites, evals-suite, evals-run, evals-drift) and the
// fragments their scripts swap in. Every page and fragment is no-store: it shows the stores as they are now. Writes never come here: the
// pages post JSON to the Suites unit's /api/evals/suites, cases and runs, the Checks unit's /api/evals/checks, and read the Arena unit's
// votes. Routes:
//   GET /evals                                  -> 302 /evals/leaderboard
//   GET /evals/leaderboard?mode=&tag=           the leaderboard of a mode (request, agent) and tag
//   GET /evals/leaderboard/votes.csv?mode=&tag= that leaderboard's votes as CSV (voted battles only)
//   GET /evals/suites                           the suites
//   GET /evals/suites/{id}                      a suite
//   GET /evals/suites/{id}/schedule?cap=        _SuiteScheduleModal for it (cap in dollars, to preview a cap)
//   GET /evals/suites/{id}/baseline?run=&target= _BaselineModal for it
//   GET /evals/cases/{id}/check?check=&kind=    _CheckEditorModal for one check of a case, or a new one of a kind
//   GET /evals/runs/{id}                        a suite run
//   GET /evals/runs/{id}/live                   its header, notices and grid (_RunLive), polled while it runs
//   GET /evals/drift                            Drift Watch
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Pages;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The Evals pages and their fragments.</summary>
public sealed class EvalsController(EvalPages pages) : Controller
{
    /// <summary>The Evals area's home: its leaderboard.</summary>
    [HttpGet("/evals")]
    public IActionResult Home() => Redirect(EvalRoutes.Leaderboard);

    /// <summary>The leaderboard.</summary>
    [HttpGet("/evals/leaderboard")]
    public IActionResult Leaderboard(string? mode, string? tag)
    {
        Response.Headers.CacheControl = "no-store";
        return View(pages.Leaderboard(mode, tag));
    }

    /// <summary>A leaderboard's votes as CSV.</summary>
    [HttpGet("/evals/leaderboard/votes.csv")]
    public IActionResult Votes(string? mode, string? tag)
    {
        Response.Headers.CacheControl = "no-store";
        if (pages.VotesCsv(mode, tag) is not { } csv) return NotFound();
        var name = "pisharp-votes-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", name);
    }

    /// <summary>The suites.</summary>
    [HttpGet("/evals/suites")]
    public IActionResult Suites()
    {
        Response.Headers.CacheControl = "no-store";
        return View(pages.SuitesList());
    }

    /// <summary>A suite.</summary>
    [HttpGet("/evals/suites/{id}")]
    public IActionResult Suite(string id)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.SuiteOf(id) is { } view ? View(view) : NotFoundPage("suite", id);
    }

    /// <summary>A suite's schedule modal (<c>_SuiteScheduleModal</c>), with the cap field at <paramref name="cap"/> dollars when given.</summary>
    [HttpGet("/evals/suites/{id}/schedule")]
    public IActionResult Schedule(string id, string? cap)
    {
        Response.Headers.CacheControl = "no-store";
        long? micros = null;
        if (!string.IsNullOrWhiteSpace(cap))
        {
            if (!SpendMoney.TryParseUsd(cap, out var parsed)) return BadRequest();
            micros = parsed;
        }
        return pages.ScheduleOf(id, micros) is { } view ? PartialView("_SuiteScheduleModal", view) : NotFound();
    }

    /// <summary>A suite's baseline modal (<c>_BaselineModal</c>).</summary>
    [HttpGet("/evals/suites/{id}/baseline")]
    public IActionResult Baseline(string id, string? run, string? target)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.BaselineOf(id, run, target) is { } view ? PartialView("_BaselineModal", view) : NotFound();
    }

    /// <summary>The check editor (<c>_CheckEditorModal</c>) for one check of a case, or a new check of <paramref name="kind"/>.</summary>
    [HttpGet("/evals/cases/{id}/check")]
    public IActionResult Check(string id, string? check, string? kind)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.CheckEditorOf(id, check, kind) is { } view ? PartialView("_CheckEditorModal", view) : NotFound();
    }

    /// <summary>A suite run.</summary>
    [HttpGet("/evals/runs/{id}")]
    public IActionResult Run(string id)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.RunOf(id) is { } view ? View(view) : NotFoundPage("run", id);
    }

    /// <summary>A suite run's header, notices and grid (<c>_RunLive</c>), for its page to poll while it runs.</summary>
    [HttpGet("/evals/runs/{id}/live")]
    public IActionResult RunLive(string id)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.RunOf(id) is { } view ? PartialView("_RunLive", view) : NotFound();
    }

    /// <summary>Drift Watch.</summary>
    [HttpGet("/evals/drift")]
    public IActionResult Drift()
    {
        Response.Headers.CacheControl = "no-store";
        return View(pages.DriftOf());
    }

    private ViewResult NotFoundPage(string what, string id)
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View("NotFound", new EvalNotFound(what, id));
    }
}
