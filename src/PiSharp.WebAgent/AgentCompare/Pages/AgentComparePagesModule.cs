// Decision 0011 (run W4, R6): registration and fragments of agent compare's views. Program.cs calls AddAgentComparePages after
// AddCompareArea and MapAgentComparePages after MapCompareArea. The page itself is R2's GET /compare?mode=agent, which renders _AgentMode
// (?run={id} opens a run, ?from={id} starts a new run from an earlier one). The fragments are server-rendered HTML (Cache-Control: no-store),
// the same partials the page renders, so a live update and a reload show the same markup:
//   GET /compare/agent/runs/{run}               _AgentRun: the whole run view; compare-agent-run.js swaps the regions ([data-ac-region]) and
//                                               columns ([data-ac-col]) that changed after each "status" frame of R5's event stream.
//   GET /compare/agent/runs/{run}/workspace     _AgentWorkspace: modal-agent-workspace's content for the run (read-only).
//   GET /compare/agent/runs/{run}/diff?slot=    _AgentDiff: modal-agent-diff's content (R4's diff of that workspace).
//   GET /compare/agent/runs/{run}/keep?slot=    _AgentKeep: modal-keep-result's content (R4's keep preview).
//   GET /compare/agent/column?slot=&provider=&model=&thinking=&sharedThinking=&maxTurns=&maxMinutes=&count=
//                                               _AgentSetupColumn: a setup column for a model the picker chose.
//   POST /compare/agent/project {path, isolation?, baseRef?, excludes?, models?}
//                                               _AgentProject: the setup's project row (isolation chip, uncommitted changes, copy size)
//                                               and modal-agent-workspace's content, from R4's inspection.
// They only read; every write is R5's (/api/compare/agent). 404 for a run or model that is not there, 400 for a bad slot.
// Cross-site requests (W4 fix, agent-ui): inspecting a folder runs git in it and may walk it, so the project row is a POST with a JSON
// body: HostGuard refuses a write from another origin (403), and the body binding refuses anything but application/json (415), which a
// page on another site can't send without a CORS preflight this app never answers. Every fragment also refuses a request the browser
// marks as coming from another site or origin (Sec-Fetch-Site other than same-origin or none: 403), so an <img> or a no-cors fetch from a
// website can't make the app read a run's workspaces or diffs either.
// W5 integration fix 1 (blind means blind): a run of an agent battle that has no vote yet answers every /compare/agent/runs/{run}/...
// fragment with 409 and the blind notice (_ArenaBlindNotice, which links to the battle): those fragments name the models in column order.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.AgentCompare.Pages;

/// <summary>The body of <c>POST /compare/agent/project</c>: the folder and this run's choices.</summary>
/// <param name="Path">The project folder (a full path).</param>
/// <param name="Isolation"><c>automatic</c>, <c>worktree</c> or <c>copy</c>; null for the settings'.</param>
/// <param name="BaseRef">The worktrees' base branch or commit, or null (HEAD).</param>
/// <param name="Excludes">This run's extra excludes.</param>
/// <param name="Models">How many models (workspaces) the run has.</param>
public sealed record AgentProjectRequest(string? Path, string? Isolation = null, string? BaseRef = null, IReadOnlyList<string>? Excludes = null,
    int? Models = null);

/// <summary>Registers and maps agent compare's views.</summary>
public static class AgentComparePagesModule
{
    /// <summary>The fragments' route prefix.</summary>
    public const string Prefix = "/compare/agent";

    /// <summary>Registers <see cref="AgentComparePages"/>.</summary>
    public static IServiceCollection AddAgentComparePages(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<AgentComparePages>();
        return services;
    }

    /// <summary>Maps the fragments.</summary>
    public static WebApplication MapAgentComparePages(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var pages = app.MapGroup(Prefix);
        pages.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!SameOrigin(http.Request)) return Results.Text("Cross-site requests are refused.", statusCode: StatusCodes.Status403Forbidden);
            if (http.Request.RouteValues["run"] is string run && Runs.RunBlinds.Of(http.RequestServices).Group(run) is { } battle)
            {
                var html = await http.RequestServices.GetRequiredService<FragmentRenderer>().RenderAsync(BlindPartial, BattleId(battle)).ConfigureAwait(false);
                return Results.Content(html, "text/html; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
            }
            return await next(context).ConfigureAwait(false);
        });

        pages.MapGet("/runs/{run}", async (string run, AgentComparePages views, FragmentRenderer renderer, CancellationToken token) =>
            await views.RunAsync(run, token).ConfigureAwait(false) is { } view
                ? Html(await renderer.RenderAsync(AgentComparePages.RunPartial, view).ConfigureAwait(false))
                : Results.NotFound());

        pages.MapGet("/runs/{run}/workspace", async (string run, AgentComparePages views, FragmentRenderer renderer, CancellationToken token) =>
            await views.RunAsync(run, token).ConfigureAwait(false) is { } view
                ? Html(await renderer.RenderAsync(AgentComparePages.WorkspacePartial, view.Workspace).ConfigureAwait(false))
                : Results.NotFound());

        pages.MapGet("/runs/{run}/diff", async (string run, string? slot, AgentComparePages views, FragmentRenderer renderer, CancellationToken token) =>
        {
            if (!ValidSlot(slot)) return Results.BadRequest();
            return await views.DiffAsync(run, slot!, token).ConfigureAwait(false) is { } view
                ? Html(await renderer.RenderAsync(AgentComparePages.DiffPartial, view).ConfigureAwait(false))
                : Results.NotFound();
        });

        pages.MapGet("/runs/{run}/keep", async (string run, string? slot, AgentComparePages views, FragmentRenderer renderer, CancellationToken token) =>
        {
            if (!ValidSlot(slot)) return Results.BadRequest();
            return await views.KeepAsync(run, slot!, token).ConfigureAwait(false) is { } view
                ? Html(await renderer.RenderAsync(AgentComparePages.KeepPartial, view).ConfigureAwait(false))
                : Results.NotFound();
        });

        pages.MapGet("/column", async (string? slot, string? provider, string? model, string? thinking, string? sharedThinking, int? maxTurns, int? maxMinutes,
            int? count, AgentComparePages views, FragmentRenderer renderer, CancellationToken token) =>
        {
            if (!ValidSlot(slot) || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return Results.BadRequest();
            var view = await views.SetupColumnAsync(slot!.Trim().ToLowerInvariant(), provider, model, thinking, sharedThinking,
                Math.Clamp(maxTurns ?? AgentCompareSettings.DefaultMaxTurns, 1, AgentCompareSettings.MaxTurnsLimit),
                Math.Clamp(maxMinutes ?? AgentCompareSettings.DefaultMaxMinutes, 1, AgentCompareSettings.MaxMinutesLimit),
                (count ?? 2) > 1, token).ConfigureAwait(false);
            return Html(await renderer.RenderAsync(AgentComparePages.SetupColumnPartial, view).ConfigureAwait(false));
        });

        pages.MapPost("/project", async (AgentProjectRequest body, AgentComparePages views, FragmentRenderer renderer, CancellationToken token) =>
        {
            var list = (body.Excludes ?? []).SelectMany(item => (item ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
            var view = await views.ProjectAsync(body.Path, body.Isolation, body.BaseRef, list, body.Models ?? 2, token).ConfigureAwait(false);
            return Html(await renderer.RenderAsync(AgentComparePages.ProjectPartial, view).ConfigureAwait(false));
        });
        return app;
    }

    /// <summary>Whether the browser says the request comes from this app's own pages (<c>Sec-Fetch-Site: same-origin</c>) or from the user
    /// (<c>none</c>: a typed address or a bookmark). A request without the header (an older browser, a test client) passes; HostGuard's
    /// Host check still applies to it.</summary>
    public static bool SameOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var site = request.Headers["Sec-Fetch-Site"].ToString().Trim();
        return site.Length == 0 || site.Equals("same-origin", StringComparison.OrdinalIgnoreCase) || site.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The notice a blind battle's run shows instead of its views (model: the battle's id).</summary>
    public const string BlindPartial = "/Views/Compare/_ArenaBlindNotice.cshtml";

    // The battle's id from its page's address (/compare/arena/{id}).
    private static string BattleId(string page) => Uri.UnescapeDataString(page[(page.LastIndexOf('/') + 1)..]);

    private static bool ValidSlot(string? slot) => slot is not null && AgentCompareRunner.Slots.Contains(slot.Trim().ToLowerInvariant());

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");
}
