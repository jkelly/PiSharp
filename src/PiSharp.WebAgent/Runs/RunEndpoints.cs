// Decision 0011 (run W4, R0): the run engine's and run history's JSON API under /api/runs, for the Playground, Compare, Bench, History, the
// Run inspector and Settings › Data. Writes are POSTs with JSON bodies (HostGuard's cross-origin and JSON-only rules cover them):
//   POST /api/runs                   a RunSpec (+ spend: {choice, maxOutputTokens?}) -> {posted: true, id, ids, groupId, events}, or 200
//                                    {posted: false, code, message, spend: {verdict, code, pane, message, canProceed, capOutputTokens,
//                                    estimate, html}} when the Spend Guard asks or blocks (nothing sent; W3 rule: not 409), or 400
//                                    {error: {code: "invalid", message, refused?}} for an invalid run or options the model does not take.
//   POST /api/runs/group             {groupId?, runs: [RunSpec], spend?}: a Compare run's columns, reserved all or none.
//   POST /api/runs/plan              a RunSpec -> what it would send (View code): applied, refused, notes, patch, headers, messages.
//   GET  /api/runs/options           ?provider=&model= -> the model's RunCapabilities.
//   GET  /api/runs                   ?source&provider&model&status&kind&q&from&to&group&parent&pinned&limit&offset&totals -> {items, total, totals?}.
//   GET  /api/runs/storage           {count, sizeBytes, path, settings}.
//   GET  /api/runs/{id}              {run, running}; /api/runs/{id}/raw -> {rawState, items}.
// W5 (fix 1, arena): a run of an Arena battle not voted yet is listed masked (no provider, model, account, usage, cost or timings; with
// blind: its battle's page) and never matches a provider, model or text filter; its row, raw payloads and events answer 409 {error: {code:
// "blind", message, battle}}.
//   GET  /api/runs/{id}/events       SSE: "event" frames (id: seq; data {seq, atMs, event}) from after Last-Event-ID or ?after=, live while
//                                    it runs, then one "status" frame (the run's outcome) and the end of the stream; ": ping" every 15 s.
//   POST /api/runs/{id}/cancel       -> {cancelled}; POST /api/runs/{id}/pin {pinned} -> {id, pinned}; POST /api/runs/delete {ids} -> {deleted}.
// A run started by POST belongs to the app, so it keeps going when the tab that started it closes; a tab reconnects with Last-Event-ID.
// Every answer is Cache-Control: no-store (the stream: no-cache, no-transform) and written with SettingsStore.JsonOptions. No answer carries
// a credential: accounts are ids, headers a run sends are never credentials, and raw payloads were redacted before they were stored.
// W5 integration fix 1: a refusal of a blind round (SpendAsync with blind: true) names no provider, model or account.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Runs;

/// <summary>Maps <c>/api/runs</c>.</summary>
public static class RunEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/runs";

    /// <summary>How often an idle event stream sends a comment.</summary>
    public static TimeSpan PingInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Maps the endpoints.</summary>
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapPost("", (JsonObject? body, RunEngine engine, IServiceProvider services, CancellationToken token) =>
            RunAsync(() => StartAsync(Required(body), engine, services, token)));
        api.MapPost("/group", (JsonObject? body, RunEngine engine, IServiceProvider services, CancellationToken token) =>
            RunAsync(() => StartGroupAsync(Required(body), engine, services, token)));
        api.MapPost("/plan", (JsonObject? body, RunEngine engine) => RunAsync(() => Task.FromResult(Json(PlanJson(engine.Plan(RunSpec.Parse(Required(body))))))));
        api.MapGet("/options", (string? provider, string? model, RunEngine engine) => RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Name the provider and the model (?provider=&model=).");
            return Task.FromResult(engine.Capabilities(provider, model) is { } capabilities ? Json(capabilities)
                : Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, $"There is no model {provider}/{model}."));
        }));
        api.MapGet("", (HttpRequest request, RunStore store, IServiceProvider services) => RunAsync(() =>
        {
            var query = Query(request);
            // W5 (fix 1, arena): a blind battle's runs never match a filter that could single out one model, and are listed masked.
            var blind = ArenaBlindSet.Of(services);
            if (!blind.IsEmpty && (query.Provider is not null || query.Model is not null || query.Search is not null)) query = query with { ExceptKeys = blind.Keys };
            var page = store.List(query);
            var items = new JsonArray();
            foreach (var item in page.Items)
            {
                if (blind.BattleOf(item) is not { } battle)
                {
                    items.Add(JsonSerializer.SerializeToNode(item, SettingsStore.JsonOptions));
                    continue;
                }
                var masked = JsonSerializer.SerializeToNode(ArenaBlindText.Mask(item), SettingsStore.JsonOptions)!.AsObject();
                masked["blind"] = ArenaBlindText.Href(battle);
                items.Add(masked);
            }
            var answer = new JsonObject
            {
                ["items"] = items,
                ["total"] = page.Total,
                ["limit"] = query.Limit,
                ["offset"] = query.Offset,
            };
            if (Flag(request, "totals") == true) answer["totals"] = JsonSerializer.SerializeToNode(store.Totals(query), SettingsStore.JsonOptions);
            return Task.FromResult(Json(answer));
        }));
        api.MapGet("/storage", (RunStore store, IServiceProvider services) => RunAsync(() => Task.FromResult(Json(new
        {
            count = store.Count(),
            sizeBytes = store.SizeBytes(),
            path = store.Db.Path,
            settings = services.GetService<ISettingsStore>()?.Get<RunHistorySettings>() ?? RunHistorySettings.CreateDefault(),
            rawRetentionDays = RunHistorySettings.RawRetentionDays,
        }))));
        api.MapGet("/{id}", (string id, RunStore store, RunLive live, IServiceProvider services) => RunAsync(() => Task.FromResult(store.Get(id) is { } run
            ? Blind(services, run) ?? Json(new { run, running = live.Find(id) is { Ended: false } })
            : NotFound(id))));
        api.MapGet("/{id}/raw", (string id, RunStore store, IServiceProvider services) => RunAsync(() => Task.FromResult(store.Get(id) is { } run
            ? Blind(services, run) ?? Json(new { id, rawState = run.RawState, items = store.Raw(id) })
            : NotFound(id))));
        api.MapGet("/{id}/events", (string id, HttpContext http, RunStore store, RunLive live) => EventsAsync(id, http, store, live));
        api.MapPost("/{id}/cancel", (string id, RunEngine engine, RunStore store) => RunAsync(() =>
        {
            var cancelled = engine.Cancel(id);
            return Task.FromResult(cancelled || store.Get(id) is not null ? Json(new { id, cancelled }) : NotFound(id));
        }));
        api.MapPost("/{id}/pin", (string id, JsonObject? body, RunStore store) => RunAsync(() =>
        {
            var pinned = body?["pinned"] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : true;
            return Task.FromResult(store.Pin(id, pinned) ? Json(new { id, pinned }) : NotFound(id));
        }));
        api.MapPost("/delete", (JsonObject? body, RunStore store) => RunAsync(() =>
        {
            if (Required(body)["ids"] is not JsonArray ids) throw new ArgumentException("Send {ids: [...]}.");
            var list = ids.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToList();
            return Task.FromResult(Json(new { deleted = store.Delete(list) }));
        }));
        return routes;
    }

    /// <summary>The final status frame of a stored run (as RunEngine publishes it when a run ends).</summary>
    public static JsonObject StatusOf(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return RunEngine.Status(run.Id, new RunOutcome
        {
            Status = run.Status,
            StopReason = run.StopReason,
            Error = run.Error,
            Notes = run.Notes,
            Usage = run.Usage,
            CostMicros = run.CostMicros,
            CostKind = run.CostKind,
            Billing = run.Billing,
            SpendOp = run.SpendOp,
            TtftMs = run.TtftMs,
            DurationMs = run.DurationMs,
            TokensPerSecond = run.TokensPerSecond,
            Finished = run.Finished ?? run.Updated,
        });
    }

    /// <summary>The View code answer of a plan.</summary>
    public static JsonObject PlanJson(RunRequestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new JsonObject
        {
            ["provider"] = plan.Model.Provider,
            ["model"] = plan.Model.Id,
            ["api"] = plan.Model.Api,
            ["allowed"] = plan.Allowed,
            ["message"] = plan.RefusalMessage,
            ["thinking"] = plan.Thinking,
            ["maxTokens"] = plan.Spec.MaxTokens,
            ["applied"] = JsonSerializer.SerializeToNode(plan.Applied, SettingsStore.JsonOptions),
            ["refused"] = JsonSerializer.SerializeToNode(plan.Refused, SettingsStore.JsonOptions),
            ["notes"] = JsonSerializer.SerializeToNode(plan.Notes, SettingsStore.JsonOptions),
            ["patch"] = plan.Patch?.DeepClone(),
            ["patchPath"] = plan.PatchPath,
            ["headers"] = plan.Headers is null ? null : JsonSerializer.SerializeToNode(plan.Headers, SettingsStore.JsonOptions),
            ["cache"] = plan.Cache is null ? null : JsonSerializer.SerializeToNode(plan.Cache, SettingsStore.JsonOptions),
            ["cappedCache"] = plan.CappedCache is null ? null : JsonSerializer.SerializeToNode(plan.CappedCache, SettingsStore.JsonOptions),
            ["thinkingBudget"] = plan.ThinkingBudget,
            ["maxRetries"] = plan.Allowed ? plan.Spec.MaxRetries : null,
            ["apiVersion"] = plan.Allowed ? plan.Spec.AzureApiVersion : null,
            ["messages"] =JsonNode.Parse("[" + string.Join(",", plan.Messages.Select(message => message.ToJson())) + "]"),
        };
    }

    private static async Task<IResult> StartAsync(JsonObject body, RunEngine engine, IServiceProvider services, CancellationToken token)
    {
        var spend = RunSpendAnswer.Read(body["spend"]);
        var spec = (JsonObject)body.DeepClone();
        spec.Remove("spend");
        var start = await engine.StartAsync(RunSpec.Parse(spec), spend, token).ConfigureAwait(false);
        return await AnswerAsync(start, services).ConfigureAwait(false);
    }

    private static async Task<IResult> StartGroupAsync(JsonObject body, RunEngine engine, IServiceProvider services, CancellationToken token)
    {
        var spend = RunSpendAnswer.Read(body["spend"]);
        if (body["runs"] is not JsonArray runs) throw new ArgumentException("Send {runs: [...]} with one run per column.");
        var specs = runs.Select((run, index) =>
        {
            try { return RunSpec.Parse(run); }
            catch (ArgumentException error) { throw new ArgumentException($"Column {index + 1}: {error.Message}", error); }
        }).ToList();
        var group = body["groupId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var start = await engine.StartGroupAsync(specs, group, spend, token).ConfigureAwait(false);
        return await AnswerAsync(start, services).ConfigureAwait(false);
    }

    private static async Task<IResult> AnswerAsync(RunStart start, IServiceProvider services)
    {
        if (start.Posted)
            return Json(new { posted = true, id = start.Id, ids = start.Ids, groupId = start.GroupId, events = $"{Prefix}/{start.Id}/events" });
        var refusal = start.Refusal!;
        return Json(new { posted = false, code = refusal.Code, message = refusal.Message, groupId = start.GroupId, spend = await SpendAsync(refusal, services).ConfigureAwait(false) });
    }

    /// <summary>What a blind round's refusal says instead of the Spend Guard's own message (which names the model).</summary>
    public const string BlindRefusal = "The Spend Guard stopped this blind round before anything was reserved or sent: one of its models is over a cap or has no price.";

    /// <summary>The confirm modal's answer for a refused run (the same shape as <c>/api/spend/confirm</c>): the <c>spend</c> of a
    /// <c>{posted:false}</c> answer. Compare's and the Tool Bench's run endpoints answer with it too. With <paramref name="blind"/> (a blind
    /// round's refusal) the message and the modal name no provider, model or account.</summary>
    public static async Task<JsonObject> SpendAsync(RunRefusal refusal, IServiceProvider services, bool blind = false)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(services);
        var decision = refusal.Decision;
        var estimate = decision.Estimate;
        var answer = new JsonObject
        {
            ["verdict"] = JsonSerializer.SerializeToNode(decision.Verdict, SettingsStore.JsonOptions),
            ["code"] = refusal.Code,
            ["message"] = blind ? BlindRefusal : refusal.Message,
            ["pane"] = null,
            ["canProceed"] = decision.Verdict == SpendVerdict.Ask,
            ["capOutputTokens"] = null,
            ["estimate"] = new JsonObject
            {
                ["worstMicros"] = estimate.WorstMicros,
                ["typicalMicros"] = estimate.TypicalMicros,
                ["billing"] = JsonSerializer.SerializeToNode(estimate.Billing, SettingsStore.JsonOptions),
                ["chip"] = SpendEstimateText.Chip(estimate),
            },
            ["html"] = null,
        };
        if (services.GetService<SpendPages>() is not { } pages || decision.Causes.Count == 0) return answer;
        try
        {
            var view = pages.ConfirmView(refusal.Call, decision, blind);
            // "Cap the output" is offered only when the run would hold that cap (RunModelOptions.OutputCapRefusal).
            if (view.CapOutputTokens is { } tokens && refusal.CapOutputRefusal?.Invoke(tokens) is { } why)
                view = view with
                {
                    CapOutputTokens = null,
                    Fit = blind ? "Capping the output is not offered for this round: one of its models can't hold that cap." : $"Capping the output is not offered for this request: {why}.",
                    Others = view.Others?.Replace(" Capping the output also answers that Ask, for this request only.", "", StringComparison.Ordinal),
                };
            answer["pane"] = view.Pane;
            answer["canProceed"] = view.CanProceed;
            answer["capOutputTokens"] = view.CapOutputTokens;
            if (services.GetService<FragmentRenderer>() is { } renderer)
                answer["html"] = await renderer.RenderAsync(SpendEndpoints.ConfirmPartial, view).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(RunEndpoints)).LogWarning(error, "The spend confirm modal of a refused run could not be made.");
        }
        return answer;
    }

    private static async Task EventsAsync(string id, HttpContext http, RunStore store, RunLive live)
    {
        var token = http.RequestAborted;
        var after = After(http.Request);
        var running = live.Find(id);
        var stored = running is null ? store.Get(id) : null;
        if (running is null && stored is null)
        {
            await NotFound(id).ExecuteAsync(http).ConfigureAwait(false);
            return;
        }
        if (Blind(http.RequestServices, stored ?? store.Get(id) ?? new RunRecord { Id = id, Op = "", Source = "", Created = default }) is { } blind)
        {
            await blind.ExecuteAsync(http).ConfigureAwait(false);
            return;
        }
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            await http.Response.Body.FlushAsync(token).ConfigureAwait(false);
            if (running is not null)
            {
                var subscription = running.Subscribe(after);
                try
                {
                    foreach (var item in subscription.Backlog) await WriteAsync(http, Frame(item), token).ConfigureAwait(false);
                    if (subscription.Live is { } reader)
                    {
                        while (true)
                        {
                            var wait = reader.WaitToReadAsync(token).AsTask();
                            if (await Task.WhenAny(wait, Task.Delay(PingInterval, token)).ConfigureAwait(false) != wait)
                            {
                                await WriteAsync(http, ": ping\n\n", token).ConfigureAwait(false);
                                continue;
                            }
                            if (!await wait.ConfigureAwait(false)) break;
                            while (reader.TryRead(out var item)) await WriteAsync(http, Frame(item), token).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    subscription.Unsubscribe();
                }
                var final = running.Final ?? (store.Get(id) is { } row ? StatusOf(row) : new JsonObject { ["id"] = id, ["status"] = RunStatus.Error });
                await WriteAsync(http, "event: status\ndata: " + final.ToJsonString() + "\n\n", token).ConfigureAwait(false);
                return;
            }
            foreach (var item in store.Events(id, after))
                await WriteAsync(http, Frame(new RunLiveItem(item.Seq, item.AtMs, item.Event)), token).ConfigureAwait(false);
            await WriteAsync(http, "event: status\ndata: " + StatusOf(stored!).ToJsonString() + "\n\n", token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // The tab closed; the run goes on.
        }
    }

    private static string Frame(RunLiveItem item) =>
        $"id: {item.Seq.ToString(CultureInfo.InvariantCulture)}\nevent: event\ndata: " +
        new JsonObject { ["seq"] = item.Seq, ["atMs"] = item.AtMs, ["event"] = item.Event.DeepClone() }.ToJsonString() + "\n\n";

    private static async Task WriteAsync(HttpContext http, string text, CancellationToken token)
    {
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    private static long After(HttpRequest request)
    {
        var text = request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrWhiteSpace(text)) text = request.Query["after"].ToString();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var after) && after > 0 ? after : 0;
    }

    private static RunQuery Query(HttpRequest request)
    {
        string? Text(string name) => request.Query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString().Trim() : null;
        int Int(string name, int fallback) =>
            Text(name) is { } text ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number
                : throw new ArgumentException($"{name} is a whole number.") : fallback;
        DateTimeOffset? Time(string name)
        {
            if (Text(name) is not { } text) return null;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time
                : throw new ArgumentException($"{name} is a date (ISO 8601) or Unix milliseconds.");
        }
        return new RunQuery
        {
            Source = Text("source"),
            Provider = Text("provider"),
            Model = Text("model"),
            Status = Text("status"),
            Kind = Text("kind"),
            Search = Text("q"),
            From = Time("from"),
            To = Time("to"),
            GroupId = Text("group"),
            ParentId = Text("parent"),
            Pinned = Flag(request, "pinned"),
            Limit = Int("limit", 50),
            Offset = Int("offset", 0),
        };
    }

    private static bool? Flag(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var value) && value.ToString() is { Length: > 0 } text
            ? text is "1" or "true" ? true : text is "0" or "false" ? false : throw new ArgumentException($"{name} is true or false.")
            : null;

    private static JsonObject Required(JsonObject? body) => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult NotFound(string id) => Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, $"There is no run {id}.");

    // W5 (fix 1, arena): a run of an Arena battle not voted yet (its row, events and raw payloads name the model) is refused: 409
    // {error: {code: "blind", message, battle}}.
    private static IResult? Blind(IServiceProvider services, RunRecord run) => ArenaBlindSet.Of(services).BattleOf(run) is { } battle
        ? Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "blind", ["message"] = ArenaBlindText.Refusal, ["battle"] = ArenaBlindText.Href(battle) } },
            StatusCodes.Status409Conflict)
        : null;

    private static IResult Error(int status, string code, string message, JsonNode? refused = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (refused is not null) error["refused"] = refused;
        return Json(new JsonObject { ["error"] = error }, status);
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (RunOptionException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message, JsonSerializer.SerializeToNode(error.Plan.Refused, SettingsStore.JsonOptions));
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

    private static Task<IResult> RunAsync(Func<IResult> action) => RunAsync(() => Task.FromResult(action()));
}
