// Decision 0011 (run W4, R2): Compare (GET /compare, after the mockups' compare.html) and the fragments its page swaps in. Request mode is
// this unit's; ?mode=agent renders R6's _AgentMode instead. ?group={id} reopens a Compare run: its prompt, its columns (live while they run)
// and the mark. The fragments are rendered here, through the shared partials and the sanitizing MarkdownRenderer, so the page never builds
// HTML from model text: a column when its run ends (or when a model is chosen for it), and the Metrics tab.
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Compare;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The Compare page and fragments.</summary>
public sealed partial class CompareController(ComparePages pages) : Controller
{
    /// <summary>Compare.</summary>
    [HttpGet("/compare")]
    public async Task<IActionResult> Index(string? group, string? mode, CancellationToken cancellationToken) =>
        View(await pages.PageAsync(group, mode, cancellationToken).ConfigureAwait(false));

    /// <summary>A column that has not run (a model chosen for it): <c>_CompareColumn</c>. In a group that ran (<paramref name="group"/>), the
    /// column is shown under the group's shared request, which its Re-run sends; otherwise under the page's shared options.</summary>
    [HttpGet("/compare/column")]
    public IActionResult Column(string? provider, string? model, string? key, string? thinking, double? temperature, int? count,
        string? sharedThinking, double? sharedTemperature, int? sharedMaxTokens, string? group)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return BadRequest("Name the provider and the model.");
        var stored = string.IsNullOrWhiteSpace(group) ? null : pages.Group(group.Trim());
        var shared = stored?.Shared ?? new CompareDraft
        {
            Thinking = string.IsNullOrWhiteSpace(sharedThinking) ? null : sharedThinking.Trim(),
            MaxTokens = sharedMaxTokens,
            Temperature = sharedTemperature,
        };
        var column = new CompareColumnDraft { Provider = provider, Model = model, Thinking = thinking, Temperature = temperature };
        return PartialView("_CompareColumn", pages.DraftColumn(shared, column, Key(key), count ?? CompareDraft.MaxColumns, stored?.Id));
    }

    /// <summary>Column <paramref name="run"/> of group <paramref name="id"/>: <c>_CompareColumn</c>.</summary>
    [HttpGet("/compare/groups/{id}/runs/{run}")]
    public IActionResult GroupColumn(string id, string run, string? key)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.GroupColumn(id, run, Key(key)) is { } view ? PartialView("_CompareColumn", view) : NotFound();
    }

    /// <summary>The Metrics tab of group <paramref name="id"/>: <c>_CompareMetrics</c>.</summary>
    [HttpGet("/compare/groups/{id}/metrics")]
    public IActionResult Metrics(string id)
    {
        Response.Headers.CacheControl = "no-store";
        return pages.Metrics(id) is { } view ? PartialView("_CompareMetrics", view) : NotFound();
    }

    // An element id part: c and a short id (letters, digits, '-'), else c1.
    private static string Key(string? key) => key is { Length: > 0 and <= 24 } && KeyPattern().IsMatch(key) ? key : "c1";

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex KeyPattern();
}
