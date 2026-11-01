// Decision 0011 (run W5, E7): _TestCaseModal's content (GET /api/evals/pins/modal), which pin.js swaps into the shell a host page renders
// when a Pin as test button opens the modal. Rendered on the server from PinService's view (the reference output and the request are
// encoded, never Markdown), so the page builds nothing from model text. A pin that can't be made is the refused fragment (200); an unknown
// run, chat, suite or case is the refused fragment with 404, and a bad query with 400.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Chat;

namespace PiSharp.WebAgent.Evals.Pins;

/// <summary>The test case modal's content.</summary>
public sealed class PinsController(PinService pins) : Controller
{
    /// <summary>The partial the modal is.</summary>
    public const string ModalPartial = "/Views/Shared/_TestCaseModal.cshtml";

    /// <summary>The modal's content for a run (<c>run</c>), a chat answer (<c>chat</c>, <c>entry</c>, <c>agent</c>), a suite's Add case
    /// (<c>suite</c>) or a case's Edit case (<c>case</c>).</summary>
    [HttpGet(EvalRoutes.ApiPins + "/modal")]
    public async Task<IActionResult> Modal(string? source, string? run, string? chat, string? agent, string? entry, string? suite,
        [FromQuery(Name = "case")] string? testCase, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var target = PinTarget.From(source, run, chat, agent, entry, suite, testCase);
            return PartialView(ModalPartial, await pins.ModalAsync(target, cancellationToken).ConfigureAwait(false));
        }
        catch (KeyNotFoundException error)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return PartialView(ModalPartial, PinService.Refused(error.Message));
        }
        catch (ArgumentException error)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return PartialView(ModalPartial, PinService.Refused(error.Message));
        }
        catch (ChatException error)
        {
            Response.StatusCode = error.Status;
            return PartialView(ModalPartial, PinService.Refused(error.Message));
        }
    }
}
