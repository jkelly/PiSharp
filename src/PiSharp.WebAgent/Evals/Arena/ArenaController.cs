// Decision 0011 (run W5, E5): the Arena's battle page (GET /compare/arena/{id}, Compare's blind states: blind-held, blind, blind-voted,
// agent-blind, agent-blind-voted) and the fragments it swaps in: the body (status line, columns, vote bar and, once revealed, the Metrics
// tab) as the battle changes, and an agent column's diff (modal-agent-diff, names hidden until the vote). Every fragment is rendered on the
// server from ArenaPages' views (model text through the sanitizing MarkdownRenderer), so what a page receives before the vote is only what
// those views carry: letters, the battle's state, counts and the answers themselves.
using Microsoft.AspNetCore.Mvc;

namespace PiSharp.WebAgent.Evals.Arena;

/// <summary>The Arena battle page and its fragments.</summary>
public sealed class ArenaController(ArenaPages pages) : Controller
{
    /// <summary>A battle.</summary>
    [HttpGet("/compare/arena/{id}")]
    public async Task<IActionResult> Battle(string id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await pages.PageAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? View(ArenaPages.PageView, view) : NotFound();
    }

    /// <summary>A battle's body: <c>_ArenaBattle</c>.</summary>
    [HttpGet("/compare/arena/{id}/body")]
    public async Task<IActionResult> Body(string id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await pages.PageAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? PartialView(ArenaPages.BodyPartial, view) : NotFound();
    }

    /// <summary>Column <paramref name="letter"/>'s changes in an agent battle: <c>_ArenaAgentDiff</c> (R6's _AgentDiff, names hidden before
    /// the vote).</summary>
    [HttpGet("/compare/arena/{id}/diff")]
    public async Task<IActionResult> Diff(string id, string? letter, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await pages.DiffAsync(id, letter, cancellationToken).ConfigureAwait(false) is { } view ? PartialView(ArenaPages.DiffPartial, view) : NotFound();
    }
}
