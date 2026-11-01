// Decision 0011 (run W4, R2): registration and endpoints of Compare's request mode. Program.cs calls AddCompareArea after AddPlaygroundArea and
// MapCompareArea after MapPlaygroundArea. The page and its fragments are CompareController's (GET /compare and /compare/...); runs go through
// the run engine (Spend Guard, runs.db, live events at /api/runs/{id}/events, cancel at /api/runs/{id}/cancel). The JSON endpoints:
//   POST /api/compare/plan                   a draft -> {valid, message, columns: [{index, ok, error, thinking, temperature, maxTokens, chips,
//                                            refused}], estimate: {html, verdict, chip, over, pane}}: each column as it would be sent, and the
//                                            estimate by Run all (the Spend Guard's group preflight, with group-total caps;
//                                            nothing reserved).
//   POST /api/compare/run                    {...draft, groupId?, spend?} -> {posted: true, groupId, ids, columns: [{index, runId, events}]}, or
//                                            200 {posted: false, code, message, groupId, provider, model, spend: {...}} when the Spend Guard asks or blocks the
//                                            group (nothing reserved or sent; resend with the same groupId and spend: {choice, maxOutputTokens?}),
//                                            or 400 {error: {code: "invalid", message, column?, refused?}} when a column cannot run.
//   POST /api/compare/groups/{id}/rerun      {column, replaces?, position?, spend?} -> {posted, groupId, id, events} (or the refusal above).
//   POST /api/compare/groups/{id}/best       {runId: string|null} -> {groupId, best}.
//   POST /api/compare/groups/{id}/remove     {runId} -> {groupId, columns}.
//   POST /api/compare/groups/{id}/stop       {} -> {groupId, stopped: [run ids]}.
//   GET  /api/compare/groups/{id}            {groupId, best, columns: [{runId, provider, model, status}]}.
// (R5's agent compare lives under /api/compare/agent.) Writes are JSON POSTs (HostGuard: same origin, JSON only); every answer is no-store;
// errors are {error: {code, message}} as /api/runs's. No answer carries a credential: accounts are ids and labels.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Compare;

/// <summary>Registration and endpoints of Compare's request mode.</summary>
public static class CompareModule
{
    /// <summary>The route prefix of the JSON endpoints.</summary>
    public const string Prefix = "/api/compare";

    /// <summary>Registers the area's services and its webagent.db table. Call it after <c>AddRunsArea</c> and <c>AddPlaygroundArea</c>.</summary>
    public static IServiceCollection AddCompareArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddWebAgentMigration(CompareStore.Migration);
        services.TryAddSingleton<CompareStore>(provider => new CompareStore(provider.GetRequiredService<WebAgentDb>(), provider.GetService<RunStore>(),
            provider.GetService<ILogger<CompareStore>>()));
        services.TryAddSingleton<ComparePages>();
        return services;
    }

    /// <summary>Maps <c>/api/compare</c> (request mode).</summary>
    public static WebApplication MapCompareArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        api.MapPost("/plan", (JsonObject? body, ComparePages pages, CancellationToken token) => Answer(async () =>
            Json(await pages.PlanJsonAsync(Draft(body), token).ConfigureAwait(false))));
        api.MapPost("/run", (JsonObject? body, ComparePages pages, IServiceProvider services, CancellationToken token) => Answer(async () =>
        {
            var json = Required(body);
            var started = await pages.RunAsync(Draft(json), Text(json, "groupId"), RunSpendAnswer.Read(json["spend"]), token).ConfigureAwait(false);
            if (!started.Start.Posted) return await RefusedAsync(started.Start, services).ConfigureAwait(false);
            return Json(new
            {
                posted = true,
                groupId = started.Start.GroupId,
                ids = started.Start.Ids,
                columns = started.Start.Ids.Select((id, index) => new { index, runId = id, events = $"{RunEndpoints.Prefix}/{id}/events" }),
            });
        }));
        api.MapPost("/groups/{id}/rerun", (string id, JsonObject? body, ComparePages pages, IServiceProvider services, CancellationToken token) => Answer(async () =>
        {
            var json = Required(body);
            var column = CompareDraft.ParseColumn(json["column"]);
            var position = json["position"] is JsonValue value && value.TryGetValue<int>(out var at) ? at : (int?)null;
            var started = await pages.RerunAsync(id, column, Text(json, "replaces"), position, RunSpendAnswer.Read(json["spend"]), token).ConfigureAwait(false);
            if (!started.Start.Posted) return await RefusedAsync(started.Start, services).ConfigureAwait(false);
            return Json(new { posted = true, groupId = started.Start.GroupId, id = started.Start.Id, events = $"{RunEndpoints.Prefix}/{started.Start.Id}/events" });
        }));
        api.MapPost("/groups/{id}/best", (string id, JsonObject? body, ComparePages pages) => Answer(() =>
        {
            var group = pages.MarkBest(id, Text(Required(body), "runId"));
            return Task.FromResult(Json(new { groupId = group.Id, best = group.Best }));
        }));
        api.MapPost("/groups/{id}/remove", (string id, JsonObject? body, ComparePages pages) => Answer(() =>
        {
            var run = Text(Required(body), "runId") ?? throw new ArgumentException("Send {runId}.");
            var group = pages.RemoveColumn(id, run);
            return Task.FromResult(Json(new { groupId = group.Id, columns = group.Columns.Select(column => column.RunId) }));
        }));
        api.MapPost("/groups/{id}/stop", (string id, ComparePages pages) => Answer(() =>
            Task.FromResult(Json(new { groupId = id, stopped = pages.Stop(id) }))));
        api.MapGet("/groups/{id}", (string id, ComparePages pages, RunStore runs) => Answer(() =>
        {
            var group = pages.Group(id) ?? throw new KeyNotFoundException($"There is no Compare run {id}.");
            return Task.FromResult(Json(new
            {
                groupId = group.Id,
                best = group.Best,
                columns = group.Columns.Select(column => new
                {
                    runId = column.RunId,
                    provider = column.Column.Provider,
                    model = column.Column.Model,
                    status = runs.Get(column.RunId)?.Status,
                }),
            }));
        }));
        return app;
    }

    /// <summary>The confirm modal's answer for a refused group or column (<see cref="RunEndpoints.SpendAsync"/>: the same shape as
    /// <c>/api/runs</c>' and <c>/api/spend/confirm</c>).</summary>
    public static Task<JsonObject> SpendJsonAsync(RunRefusal refusal, IServiceProvider services) => RunEndpoints.SpendAsync(refusal, services);

    private static async Task<IResult> RefusedAsync(RunStart start, IServiceProvider services)
    {
        var refusal = start.Refusal!;
        return Json(new
        {
            posted = false,
            code = refusal.Code,
            message = refusal.Message,
            groupId = start.GroupId,
            provider = refusal.Call.Provider,
            model = refusal.Call.Model,
            spend = await SpendJsonAsync(refusal, services).ConfigureAwait(false),
        });
    }

    private static CompareDraft Draft(JsonObject? body)
    {
        var json = (JsonObject)Required(body).DeepClone();
        json.Remove("spend");
        json.Remove("groupId");
        return CompareDraft.Parse(json);
    }

    private static JsonObject Required(JsonObject? body) => body ?? throw new ArgumentException("Send a JSON body.");

    private static string? Text(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string code, string message, Action<JsonObject>? more = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        more?.Invoke(error);
        return Json(new JsonObject { ["error"] = error }, status);
    }

    private static async Task<IResult> Answer(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CompareRefusedException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message, json =>
            {
                if (error.Column is { } column)
                {
                    json["column"] = column.Index + 1;
                    json["refused"] = JsonSerializer.SerializeToNode(column.Refused, SettingsStore.JsonOptions);
                }
            });
        }
        catch (RunOptionException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message,
                json => json["refused"] = JsonSerializer.SerializeToNode(error.Plan.Refused, SettingsStore.JsonOptions));
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
        catch (KeyNotFoundException error)
        {
            return Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, error.Message);
        }
    }
}
