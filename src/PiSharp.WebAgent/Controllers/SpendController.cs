// Decision 0011 (run W3, S4): the Spend pages: Settings › Spend (GET /settings/spend, the mockups' settings-spend) and History › Spend
// (GET /history/spend, the mockups' history-spend, with its filters in the query string), and the Resolve modal's content for one uncertain
// row (GET /history/spend/resolve?op=, a fragment the page swaps in). Every change goes through /api/spend (SpendEndpoints). No-store: the
// pages name accounts by their masked hints.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SpendController(SpendPages pages) : Controller
{
    [HttpGet("/settings/spend")]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken) =>
        View("~/Views/Settings/Spend.cshtml", await pages.SettingsPageAsync(cancellationToken));

    [HttpGet("/history/spend")]
    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        var filter = SpendHistoryFilter.Parse(key => Request.Query.TryGetValue(key, out var value) ? value.ToString() : null);
        return View("~/Views/Runs/Spend.cshtml", await pages.HistoryPageAsync(filter, cancellationToken));
    }

    [HttpGet("/history/spend/resolve")]
    public async Task<IActionResult> Resolve(string? op, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op)) return NotFound();
        try
        {
            var view = await pages.ResolveViewAsync(op, cancellationToken);
            return view is null ? Text(StatusCodes.Status404NotFound, "That request is not in the ledger.")
                : PartialView("~/Views/Runs/_ResolveReservationModal.cshtml", view);
        }
        catch (SpendException error)
        {
            return Text(error.Status, error.Message);
        }
    }

    private static ContentResult Text(int status, string message) => new() { StatusCode = status, Content = message, ContentType = "text/plain; charset=utf-8" };
}
