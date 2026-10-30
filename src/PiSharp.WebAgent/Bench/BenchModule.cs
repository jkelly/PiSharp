// Decision 0011 (run W4, R7): registration and endpoints of the Tool Bench. Program.cs calls AddBenchArea after AddRunsArea (and the
// Playground) and MapBenchArea after MapRunsArea. The page is BenchController's (GET /bench); its JSON API is under /api/bench:
//   POST /api/bench/sessions                          {setup?, messages?} -> {id, url, session}: a new session (Presets open one this way).
//   GET  /api/bench/sessions/{id}                     {session}.
//   GET  /api/bench/sessions/{id}/view                the page's parts: {id, version, state, pageState, runId, mode, setup, messages, model,
//                                                     html: {controls, timelineHead, timeline, tools, log}}.
//   GET  /api/bench/sessions/{id}/events              SSE: a "session" frame {id, version, state, runId} now and on every change; ": ping"
//                                                     every 15 s.
//   POST /api/bench/sessions/{id}/setup               {setup} -> the view.
//   POST /api/bench/sessions/{id}/run                 {setup?, spend?} -> {posted, runId, view} or, when the Spend Guard asks or blocks,
//                                                     200 {posted: false, code, message, spend: {...the confirm modal...}, view}.
//   POST /api/bench/sessions/{id}/messages            {text, run?, setup?, spend?} -> as run.
//   POST /api/bench/sessions/{id}/messages/{n}/remove the view (entry n and everything after it removed).
//   POST /api/bench/sessions/{id}/calls/{call}/result {text, isError?, mock?, continue?, spend?} -> as run.
//   POST /api/bench/sessions/{id}/stop | /reset       the view.
//   GET  /api/bench/sessions/{id}/log.json            the call log (attachment).
//   POST /api/bench/tools/check                       {name, description, parameters} -> {valid, errors, summary, properties, required}.
// Writes are JSON POSTs (HostGuard: same origin, JSON only). Every answer is no-store; errors are {error: {code, message, refused?}} with 400
// (invalid; an option the model does not take), 404 (no such session or call) or 409 (the state does not allow it: code "conflict").
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Bench;

/// <summary>Registration and endpoints of the Tool Bench.</summary>
public static class BenchModule
{
    /// <summary>The route prefix of the JSON endpoints.</summary>
    public const string Prefix = "/api/bench";

    /// <summary>How often an idle event stream sends a comment.</summary>
    public static TimeSpan PingInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Registers the area's services (the bench_sessions migration, the store, the runner, the pages). Call it after
    /// <c>AddRunsArea</c>.</summary>
    public static IServiceCollection AddBenchArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddWebAgentMigration(BenchStore.Migration);
        services.TryAddSingleton<BenchStore>();
        services.TryAddSingleton<BenchRunner>();
        services.TryAddSingleton<BenchPages>();
        return services;
    }

    /// <summary>Maps <c>/api/bench</c>.</summary>
    public static WebApplication MapBenchArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapBenchEndpoints();
        return app;
    }

    /// <summary>Maps the endpoints.</summary>
    public static IEndpointRouteBuilder MapBenchEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapPost("/sessions", (JsonObject? body, BenchRunner runner) => Answer(() =>
        {
            var setup = body?["setup"] is { } node ? Setup(node) : null;
            var messages = body?["messages"] switch
            {
                null => null,
                JsonArray list => list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException("messages is a list of strings.")).ToList(),
                _ => throw new ArgumentException("messages is a list of strings."),
            };
            var session = runner.Create(setup, messages);
            return Task.FromResult(Json(new { id = session.Id, url = "/bench?session=" + Uri.EscapeDataString(session.Id), session }));
        }));
        api.MapGet("/sessions/{id}", (string id, BenchRunner runner) => Answer(() => Task.FromResult(Json(new { session = runner.Get(id) }))));
        api.MapGet("/sessions/{id}/view", (string id, BenchRunner runner, BenchPages pages) => Answer(async () =>
            Json(await pages.FragmentsAsync(runner.Get(id)).ConfigureAwait(false))));
        api.MapGet("/sessions/{id}/events", (string id, HttpContext http, BenchRunner runner) => EventsAsync(id, http, runner));
        api.MapPost("/sessions/{id}/setup", (string id, JsonObject? body, BenchRunner runner, BenchPages pages, CancellationToken token) => Answer(async () =>
        {
            var setup = Setup(Required(body)["setup"] ?? throw new ArgumentException("Send {setup}."));
            var session = await runner.SaveSetupAsync(id, setup, token).ConfigureAwait(false);
            return Json(await pages.FragmentsAsync(session).ConfigureAwait(false));
        }));
        api.MapPost("/sessions/{id}/run", (string id, JsonObject? body, BenchRunner runner, BenchPages pages, IServiceProvider services, CancellationToken token) =>
            Answer(async () =>
            {
                var setup = body?["setup"] is { } node ? Setup(node) : null;
                var action = await runner.RunAsync(id, setup, RunSpendAnswer.Read(body?["spend"]), token).ConfigureAwait(false);
                return await ActionAsync(action, pages, services).ConfigureAwait(false);
            }));
        api.MapPost("/sessions/{id}/messages", (string id, JsonObject? body, BenchRunner runner, BenchPages pages, IServiceProvider services, CancellationToken token) =>
            Answer(async () =>
            {
                var request = Required(body);
                var setup = request["setup"] is { } node ? Setup(node) : null;
                var action = await runner.AddMessageAsync(id, Text(request, "text"), Flag(request, "run"), setup, RunSpendAnswer.Read(request["spend"]), token)
                    .ConfigureAwait(false);
                return await ActionAsync(action, pages, services).ConfigureAwait(false);
            }));
        api.MapPost("/sessions/{id}/messages/{index:int}/remove", (string id, int index, BenchRunner runner, BenchPages pages, CancellationToken token) => Answer(async () =>
            Json(await pages.FragmentsAsync(await runner.RemoveMessageAsync(id, index, token).ConfigureAwait(false)).ConfigureAwait(false))));
        api.MapPost("/sessions/{id}/calls/{callId}/result", (string id, string callId, JsonObject? body, BenchRunner runner, BenchPages pages, IServiceProvider services,
            CancellationToken token) => Answer(async () =>
            {
                var request = Required(body);
                var action = await runner.SubmitResultAsync(id, callId, Text(request, "text") ?? "", Flag(request, "isError"), Text(request, "mock"), Flag(request, "continue"),
                    RunSpendAnswer.Read(request["spend"]), token).ConfigureAwait(false);
                return await ActionAsync(action, pages, services).ConfigureAwait(false);
            }));
        api.MapPost("/sessions/{id}/stop", (string id, BenchRunner runner, BenchPages pages, CancellationToken token) => Answer(async () =>
            Json(await pages.FragmentsAsync(await runner.StopAsync(id, token).ConfigureAwait(false)).ConfigureAwait(false))));
        api.MapPost("/sessions/{id}/reset", (string id, BenchRunner runner, BenchPages pages, CancellationToken token) => Answer(async () =>
            Json(await pages.FragmentsAsync(await runner.ResetAsync(id, token).ConfigureAwait(false)).ConfigureAwait(false))));
        api.MapGet("/sessions/{id}/log.json", (string id, HttpContext http, BenchRunner runner) => Answer(() =>
        {
            var session = runner.Get(id);
            var name = $"bench-calls-{DateTime.Now.ToString("yyyy-MM-ddTHH-mm", CultureInfo.InvariantCulture)}.json";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            return Task.FromResult(Json(BenchPages.LogJson(session)));
        }));
        api.MapPost("/tools/check", (JsonObject? body) => Answer(() =>
        {
            var tool = Required(body)["tool"] as JsonObject ?? body!;
            var check = BenchSchema.CheckTool(Text(tool, "name"), Text(tool, "description"), tool["parameters"]);
            return Task.FromResult(Json(new { valid = check.Valid, errors = check.Errors, summary = check.Summary, properties = check.Properties, required = check.Required }));
        }));
        return routes;
    }

    /// <summary>The confirm modal's answer for a refused turn (the same shape as <c>/api/runs</c>' and <c>/api/spend/confirm</c>'s).</summary>
    public static async Task<JsonObject> SpendAsync(RunRefusal refusal, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(services);
        var decision = refusal.Decision;
        var estimate = decision.Estimate;
        var answer = new JsonObject
        {
            ["verdict"] = JsonSerializer.SerializeToNode(decision.Verdict, SettingsStore.JsonOptions),
            ["code"] = refusal.Code,
            ["message"] = refusal.Message,
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
            var view = pages.ConfirmView(refusal.Call, decision);
            answer["pane"] = view.Pane;
            answer["canProceed"] = view.CanProceed;
            answer["capOutputTokens"] = view.CapOutputTokens;
            if (services.GetService<FragmentRenderer>() is { } renderer)
                answer["html"] = await renderer.RenderAsync(SpendEndpoints.ConfirmPartial, view).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(BenchModule)).LogWarning(error, "The spend confirm modal of a refused bench turn could not be made.");
        }
        return answer;
    }

    private static async Task<IResult> ActionAsync(BenchAction action, BenchPages pages, IServiceProvider services)
    {
        var view = await pages.FragmentsAsync(action.Session).ConfigureAwait(false);
        if (action.Refusal is { } refusal)
            return Json(new JsonObject
            {
                ["posted"] = false,
                ["code"] = refusal.Code,
                ["message"] = refusal.Message,
                ["spend"] = await SpendAsync(refusal, services).ConfigureAwait(false),
                ["view"] = view,
            });
        var running = action.Session.State == BenchStates.Running && action.Session.ActiveRun is not null;
        return Json(new JsonObject { ["posted"] = running, ["runId"] = running ? action.Session.ActiveRun : null, ["view"] = view });
    }

    private static async Task EventsAsync(string id, HttpContext http, BenchRunner runner)
    {
        var token = http.RequestAborted;
        var session = runner.Store.Get(id);
        if (session is null)
        {
            await Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, $"There is no bench session {id}.").ExecuteAsync(http).ConfigureAwait(false);
            return;
        }
        var changes = Channel.CreateBounded<long>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        void OnChanged(string changed, long version)
        {
            if (changed == id) changes.Writer.TryWrite(version);
        }
        runner.Store.Changed += OnChanged;
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            await WriteAsync(http, Frame(session), token).ConfigureAwait(false);
            var reader = changes.Reader;
            Task<bool>? wait = null;
            while (!token.IsCancellationRequested)
            {
                // One wait for a change at a time (it outlives the pings); each ping's timer is cancelled once the round ends.
                wait ??= reader.WaitToReadAsync(token).AsTask();
                using var ping = CancellationTokenSource.CreateLinkedTokenSource(token);
                var first = await Task.WhenAny(wait, Task.Delay(PingInterval, ping.Token)).ConfigureAwait(false);
                await ping.CancelAsync().ConfigureAwait(false);
                if (first != wait)
                {
                    await WriteAsync(http, ": ping\n\n", token).ConfigureAwait(false);
                    continue;
                }
                var more = await wait.ConfigureAwait(false);
                wait = null;
                if (!more) break;
                while (reader.TryRead(out _)) { }
                if (runner.Store.Get(id) is not { } current) break;
                await WriteAsync(http, Frame(current), token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // The tab closed; the bench goes on.
        }
        finally
        {
            runner.Store.Changed -= OnChanged;
        }
    }

    private static string Frame(BenchSession session) =>
        $"id: {session.Version.ToString(CultureInfo.InvariantCulture)}\nevent: session\ndata: " +
        new JsonObject { ["id"] = session.Id, ["version"] = session.Version, ["state"] = session.State, ["runId"] = session.ActiveRun }.ToJsonString() + "\n\n";

    private static async Task WriteAsync(HttpContext http, string text, CancellationToken token)
    {
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    private static BenchSetup Setup(JsonNode node)
    {
        if (node is not JsonObject) throw new ArgumentException("setup is an object.");
        try
        {
            return node.Deserialize<BenchSetup>(SettingsStore.JsonOptions) ?? throw new ArgumentException("setup is an object.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The setup is not valid: " + (error.Path is { Length: > 0 } path ? path + ": " : "") + error.Message.Split(". ")[0] + ".");
        }
        catch (InvalidOperationException error)
        {
            throw new ArgumentException("The setup is not valid: " + error.Message);
        }
    }

    private static string? Text(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new ArgumentException($"{name} is a string."),
    };

    private static bool Flag(JsonObject body, string name) => body[name] switch
    {
        null => false,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        _ => throw new ArgumentException($"{name} is true or false."),
    };

    private static JsonObject Required(JsonObject? body) => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string code, string message, JsonNode? refused = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (refused is not null) error["refused"] = refused;
        return Json(new JsonObject { ["error"] = error }, status);
    }

    private static async Task<IResult> Answer(Func<Task<IResult>> action)
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
        catch (BenchConflictException error)
        {
            return Error(StatusCodes.Status409Conflict, SpendErrors.Conflict, error.Message);
        }
    }
}
