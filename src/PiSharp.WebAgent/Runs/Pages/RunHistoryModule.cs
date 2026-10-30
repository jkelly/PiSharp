// Decision 0011 (run W4, R3): registration and JSON/file endpoints of History › Runs and the Run inspector. Program.cs calls
// AddRunHistoryArea after AddPlaygroundArea and MapRunHistoryArea after MapPlaygroundArea. The pages are RunsController's (GET /history,
// GET /runs/{id}); deleting, pinning and cancelling a run use R0's /api/runs. Here, under /api/history:
//   GET  /api/history/export            ?format=csv|json|json-raw plus the page's filters (q, from, to, source, provider, model, status), or
//                                       &ids=a,b,c for chosen runs -> the file (Content-Disposition: attachment).
//   POST /api/history/clear             {keepPinned} -> {deleted}: every run (or every unpinned one) with its events and raw payloads.
//   GET  /api/history/runs/{id}/spec    -> {spec}: the run's stored request for Re-run (404 unknown; 400 when it stored none: a chat's call).
//   GET  /api/history/runs/{id}/har     -> the raw payloads as HAR 1.2 (404 when none were stored).
//   GET  /api/history/runs/{id}/json    -> the run with its children, events and raw payload items.
//   POST /api/history/filters           {name, query} -> {filters}: saves (or replaces) a saved filter.
//   POST /api/history/filters/manage    {filters: [{name, query}]} -> {filters}: replaces the list (Manage saved filters).
// Writes are JSON POSTs (HostGuard: same origin, JSON only); every answer is no-store; errors are {error: {code, message}}.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Runs;

/// <summary>Registration and endpoints of History › Runs and the Run inspector.</summary>
public static class RunHistoryModule
{
    /// <summary>The route prefix of the JSON endpoints.</summary>
    public const string Prefix = "/api/history";

    /// <summary>Registers the area's services. Call it after <c>AddPlaygroundArea</c>.</summary>
    public static IServiceCollection AddRunHistoryArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<RunHistoryPages>();
        services.TryAddSingleton<RunInspectorPages>();
        return services;
    }

    /// <summary>Maps <c>/api/history</c>.</summary>
    public static WebApplication MapRunHistoryArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        api.MapGet("/export", (HttpRequest request, RunHistoryPages pages) => Answer(() =>
        {
            var format = request.Query["format"].ToString() is { Length: > 0 } given ? given.Trim() : "csv";
            var ids = request.Query["ids"].ToString() is { Length: > 0 } list ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            var file = pages.Export(format, ids is null ? RunHistoryFilter.Parse(request.Query) : null, ids);
            return Results.File(file.Content, file.ContentType, file.Name);
        }));
        api.MapPost("/clear", (JsonObject? body, RunStore store) => Answer(() =>
        {
            var keep = body?["keepPinned"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
            return Json(new { deleted = store.Clear(keep), keepPinned = keep });
        }));
        api.MapGet("/runs/{id}/spec", (string id, RunHistoryPages pages) => Answer(() =>
        {
            if (pages.Store.Get(id) is null) return NotFound(id);
            return pages.RerunSpec(id) is { } spec ? Json(new JsonObject { ["spec"] = spec })
                : Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, "This run did not store its request (a chat's call or an agent compare turn), so it cannot be sent again from here.");
        }));
        api.MapGet("/runs/{id}/har", (string id, RunInspectorPages pages) => Answer(() =>
        {
            if (pages.Find(id) is not { } run) return NotFound(id);
            return pages.Har(run) is { } har ? Results.File(System.Text.Encoding.UTF8.GetBytes(har.ToJsonString(Indented)), "application/json; charset=utf-8", run.Id + ".har")
                : Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, "Raw payloads weren't stored for this run, so there is no HAR.");
        }));
        api.MapGet("/runs/{id}/json", (string id, RunInspectorPages pages) => Answer(() => pages.Find(id) is { } run
            ? Results.File(System.Text.Encoding.UTF8.GetBytes(pages.RunJson(run).ToJsonString(Indented)), "application/json; charset=utf-8", run.Id + ".json")
            : NotFound(id)));
        api.MapPost("/filters", async (JsonObject? body, ISettingsStore settings, CancellationToken token) => await AnswerAsync(async () =>
        {
            var name = RunSavedFilters.NormalizeName(Text(body, "name"));
            var query = RunSavedFilters.NormalizeQuery(Text(body, "query"));
            var saved = await settings.UpdateAsync<RunSavedFilters>(current =>
            {
                var filters = (current.Filters ?? []).ToList();
                var at = filters.FindIndex(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) filters[at] = new RunSavedFilter(name, query);
                else
                {
                    if (filters.Count >= RunSavedFilters.MaxFilters)
                        throw new ArgumentException($"You already have {RunSavedFilters.MaxFilters} saved filters; remove one under Manage saved filters first.");
                    filters.Add(new RunSavedFilter(name, query));
                }
                return new RunSavedFilters(filters);
            }, token).ConfigureAwait(false);
            return Json(new { name, query, filters = saved.Filters });
        }).ConfigureAwait(false));
        api.MapPost("/filters/manage", async (JsonObject? body, ISettingsStore settings, CancellationToken token) => await AnswerAsync(async () =>
        {
            if (body?["filters"] is not JsonArray list) throw new ArgumentException("Send {filters: [{name, query}]}.");
            var filters = list.Select(item => new RunSavedFilter(Text(item as JsonObject, "name") ?? "", Text(item as JsonObject, "query") ?? "")).ToList();
            var saved = await settings.SaveAsync(new RunSavedFilters(filters).Normalize(), token).ConfigureAwait(false);
            return Json(new { filters = saved.Filters });
        }).ConfigureAwait(false));
        return app;
    }

    private static readonly JsonSerializerOptions Indented = new(SettingsStore.JsonOptions) { WriteIndented = true };

    private static string? Text(JsonObject? body, string name) => body?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult NotFound(string id) => Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, $"There is no run {id}.");

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static IResult Answer(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
    }

    private static async Task<IResult> AnswerAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
    }
}
