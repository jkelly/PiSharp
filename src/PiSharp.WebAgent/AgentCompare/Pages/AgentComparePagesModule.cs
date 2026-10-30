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
//   GET /compare/agent/project?path=&isolation=&baseRef=&excludes=&models=
//                                               _AgentProject: the setup's project row (isolation chip, uncommitted changes, copy size)
//                                               and modal-agent-workspace's content, from R4's inspection.
// They only read; every write is R5's (/api/compare/agent). 404 for a run or model that is not there, 400 for a bad slot.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.AgentCompare.Pages;

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
            context.HttpContext.Response.Headers.CacheControl = "no-store";
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

        pages.MapGet("/project", async (string? path, string? isolation, string? baseRef, string? excludes, int? models, AgentComparePages views,
            FragmentRenderer renderer, CancellationToken token) =>
        {
            var list = (excludes ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var view = await views.ProjectAsync(path, isolation, baseRef, list, models ?? 2, token).ConfigureAwait(false);
            return Html(await renderer.RenderAsync(AgentComparePages.ProjectPartial, view).ConfigureAwait(false));
        });
        return app;
    }

    private static bool ValidSlot(string? slot) => slot is not null && AgentCompareRunner.Slots.Contains(slot.Trim().ToLowerInvariant());

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");
}
