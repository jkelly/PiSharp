// Decision 0011 (run W4, R5): agent compare's JSON API under /api/compare/agent, for R6's pages. Writes are POSTs with JSON bodies (HostGuard's
// same-origin and JSON-only rules cover them) and are idempotent (a repeated create answers the run its request id made; a repeated stop,
// continue, keep, discard or open-chat answers what the first did):
//   POST /api/compare/agent                    {requestId, task, projectPath, models: [{provider, model, thinking?, account?}] (1-4),
//                                              isolation?, baseRef?, extraExcludes?, confirmLargeCopy?, checkCommand?, checkTimeoutSeconds?,
//                                              maxTurns?, maxMinutes?, capMicros? | capUsd?, capAction? (ask|block), approval? (ask|allowList|auto)}
//                                              -> the run's status (below); 400/409 {error: {code, message}} (invalid, project-missing,
//                                              not-git, too-large).
//   POST /api/compare/agent/inspect            {projectPath, isolation?, extraExcludes?, models? (a count or the list)} -> R4's ProjectInspection (the setup's
//                                              isolation chip, uncommitted changes, copy size).
//   GET  /api/compare/agent                    ?limit= -> {items: [summary]} newest first.
//   GET  /api/compare/agent/{run}              -> the run's status: {id, state, task, projectPath, created, started, finished, error,
//                                              workspace, limits, approval, check, order, grants, spend, waiting, models: [model status]}.
//   GET  /api/compare/agent/{run}/events       SSE: one "status" frame (the status JSON) now and after every change (coalesced), ": ping"
//                                              every 15 s; it ends when the run is finished.
//   POST /api/compare/agent/{run}/approve      {slot?, taskId?, decision: allow|deny, reason?, scope: call|command|all} -> {answers, status}.
//   POST /api/compare/agent/{run}/stop         {slot?} (none: every model) -> status.
//   POST /api/compare/agent/{run}/continue     {slot, scope?: turn|run} -> status (a budget Ask: one more turn, or the rest of the run).
//   GET  /api/compare/agent/{run}/diff         ?slot= -> R4's WorkspaceDiff (files, hunks, patch).
//   GET  /api/compare/agent/{run}/keep-preview ?slot= -> R4's KeepPreview (the Keep modal).
//   POST /api/compare/agent/{run}/apply        {slot, discardOthers?} -> {result: KeepResult, discarded, keptBack, status}; a refusal is 200
//                                              with result.outcome "refused" (nothing changed).
//   POST /api/compare/agent/{run}/keep-branch  {slot, discardOthers?} -> as apply.
//   POST /api/compare/agent/{run}/discard      {slot?} (none: every workspace of the run) -> {discarded, status}.
//   POST /api/compare/agent/{run}/open-chat    {slot} -> {chatId, url: "/chat/{id}", status}.
// Every answer is Cache-Control: no-store and written with SettingsStore.JsonOptions. No answer carries a credential: accounts are ids.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>Maps <c>/api/compare/agent</c>.</summary>
public static class AgentCompareEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/compare/agent";

    /// <summary>How often an idle event stream sends a comment.</summary>
    public static TimeSpan PingInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Maps the endpoints.</summary>
    public static IEndpointRouteBuilder MapAgentCompareEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapPost("", (JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) => RunAsync(async () =>
        {
            var run = await runner.CreateAsync(CreateRequest(Required(body)), token).ConfigureAwait(false);
            return Json(await AgentCompareStatus.ToJsonAsync(run, runner, services, token).ConfigureAwait(false));
        }));
        api.MapPost("/inspect", (JsonObject? body, WorkspaceManager workspaces, ISettingsStore settings, CancellationToken token) => RunAsync(async () =>
        {
            var json = Required(body);
            var defaults = settings.Get<AgentCompareSettings>();
            var path = Text(json, "projectPath") ?? throw AgentCompareException.BadRequest("Choose the project folder.");
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                throw new AgentCompareException(400, AgentCompareException.ProjectMissing, $"There is no folder {path}.");
            var inspection = await workspaces.InspectAsync(path, new WorkspaceInspectOptions
            {
                Isolation = Enum<WorkspaceIsolation>(json, "isolation") ?? defaults.Isolation,
                ExtraExcludes = AgentCompareSettings.NormalizeExcludes([.. defaults.ExtraExcludes ?? [], .. Strings(json, "extraExcludes")]),
                Slots = Math.Clamp(json["models"] is JsonArray chosen ? chosen.Count : Int(json, "models") ?? 3, 1, AgentCompareRunner.MaxModels),
                WarnAboveBytes = defaults.CopyWarnBytes,
            }, token).ConfigureAwait(false);
            return Json(inspection);
        }));
        api.MapGet("", (int? limit, AgentCompareRunner runner) => RunAsync(() =>
            Task.FromResult(Json(new JsonObject { ["items"] = new JsonArray([.. runner.Store.List(limit ?? 50).Select(run => (JsonNode)AgentCompareStatus.Summary(run))]) }))));
        api.MapGet("/{run}", (string run, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) => RunAsync(async () =>
            Json(await AgentCompareStatus.ToJsonAsync(runner.Require(run), runner, services, token).ConfigureAwait(false))));
        api.MapGet("/{run}/events", (string run, HttpContext context, AgentCompareRunner runner, IServiceProvider services) =>
            EventsAsync(run, context, runner, services));
        api.MapPost("/{run}/approve", (string run, JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) => RunAsync(async () =>
        {
            var json = Required(body);
            var decision = Text(json, "decision") ?? "allow";
            if (decision is not ("allow" or "deny")) throw AgentCompareException.BadRequest("Decide allow or deny.");
            var scope = (Text(json, "scope") ?? "call") switch
            {
                "call" => AgentApprovalScope.Call,
                "command" => AgentApprovalScope.Command,
                "all" => AgentApprovalScope.All,
                _ => throw AgentCompareException.BadRequest("The scope is call, command or all."),
            };
            var reason = Text(json, "reason");
            if (reason is { Length: > 2000 }) throw AgentCompareException.BadRequest("A reason is at most 2,000 characters.");
            var answers = await runner.ApproveAsync(run, scope, Text(json, "slot"), Long(json, "taskId"), decision == "allow", reason, token).ConfigureAwait(false);
            return Json(new JsonObject
            {
                ["answers"] = JsonSerializer.SerializeToNode(answers, SettingsStore.JsonOptions),
                ["status"] = await AgentCompareStatus.ToJsonAsync(runner.Require(run), runner, services, token).ConfigureAwait(false),
            });
        }));
        api.MapPost("/{run}/stop", (string run, JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) => RunAsync(async () =>
            Json(await AgentCompareStatus.ToJsonAsync(await runner.StopAsync(run, Text(body, "slot"), token).ConfigureAwait(false), runner, services, token).ConfigureAwait(false))));
        api.MapPost("/{run}/continue", (string run, JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) => RunAsync(async () =>
        {
            var json = Required(body);
            var slot = Text(json, "slot") ?? throw AgentCompareException.BadRequest("Name the model (slot).");
            var rest = (Text(json, "scope") ?? "turn") switch
            {
                "turn" => false,
                "run" => true,
                _ => throw AgentCompareException.BadRequest("The scope is turn or run."),
            };
            return Json(await AgentCompareStatus.ToJsonAsync(runner.Continue(run, slot, rest), runner, services, token).ConfigureAwait(false));
        }));
        api.MapGet("/{run}/diff", (string run, string? slot, AgentCompareRunner runner, CancellationToken token) => RunAsync(async () =>
            Json(await runner.DiffAsync(run, slot ?? throw AgentCompareException.BadRequest("Name the model (?slot=)."), token).ConfigureAwait(false))));
        api.MapGet("/{run}/keep-preview", (string run, string? slot, AgentCompareRunner runner, CancellationToken token) => RunAsync(async () =>
            Json(await runner.PreviewKeepAsync(run, slot ?? throw AgentCompareException.BadRequest("Name the model (?slot=)."), token).ConfigureAwait(false))));
        api.MapPost("/{run}/apply", (string run, JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) =>
            KeepAsync(run, body, branch: false, runner, services, token));
        api.MapPost("/{run}/keep-branch", (string run, JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) =>
            KeepAsync(run, body, branch: true, runner, services, token));
        api.MapPost("/{run}/discard", (string run, JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) => RunAsync(async () =>
        {
            var discarded = await runner.DiscardAsync(run, Text(body, "slot"), token).ConfigureAwait(false);
            return Json(new JsonObject
            {
                ["discarded"] = new JsonArray([.. discarded.Select(slot => (JsonNode)JsonValue.Create(slot)!)]),
                ["status"] = await AgentCompareStatus.ToJsonAsync(runner.Require(run), runner, services, token).ConfigureAwait(false),
            });
        }));
        api.MapPost("/{run}/open-chat", (string run, JsonObject? body, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) => RunAsync(async () =>
        {
            var slot = Text(Required(body), "slot") ?? throw AgentCompareException.BadRequest("Name the model (slot).");
            var chat = runner.OpenChat(run, slot);
            return Json(new JsonObject
            {
                ["chatId"] = chat,
                ["url"] = "/chat/" + Uri.EscapeDataString(chat),
                ["status"] = await AgentCompareStatus.ToJsonAsync(runner.Require(run), runner, services, token).ConfigureAwait(false),
            });
        }));
        return routes;
    }

    /// <summary>A create request from its JSON (see the file comment).</summary>
    /// <exception cref="AgentCompareException">A value has the wrong type.</exception>
    public static AgentCompareCreateRequest CreateRequest(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var models = new List<AgentCompareModelRequest>();
        if (json["models"] is JsonArray items)
            foreach (var item in items)
            {
                if (item is not JsonObject model) throw AgentCompareException.BadRequest("Each model is an object with a provider and a model.");
                models.Add(new AgentCompareModelRequest(Text(model, "provider") ?? "", Text(model, "model") ?? "", Text(model, "thinking"), Text(model, "account")));
            }
        long? cap = Long(json, "capMicros");
        if (cap is null && json["capUsd"] is { } usd)
        {
            var text = usd.GetValueKind() == JsonValueKind.Number ? Number<decimal>(usd)?.ToString(CultureInfo.InvariantCulture) : usd.GetValueKind() == JsonValueKind.String ? usd.GetValue<string>() : null;
            if (text is not null && text.Trim().Length > 0)
                cap = SpendMoney.TryParseUsd(text, out var micros) ? micros : throw AgentCompareException.BadRequest($"\"{text}\" is not an amount in dollars.");
        }
        return new AgentCompareCreateRequest
        {
            RequestId = Text(json, "requestId") ?? "",
            Task = Text(json, "task") ?? "",
            ProjectPath = Text(json, "projectPath") ?? "",
            Models = models,
            Isolation = Enum<WorkspaceIsolation>(json, "isolation"),
            BaseRef = Text(json, "baseRef"),
            ExtraExcludes = Strings(json, "extraExcludes"),
            ConfirmLargeCopy = Bool(json, "confirmLargeCopy") ?? false,
            CheckCommand = Text(json, "checkCommand"),
            CheckTimeoutSeconds = Int(json, "checkTimeoutSeconds"),
            MaxTurns = Int(json, "maxTurns"),
            MaxMinutes = Int(json, "maxMinutes"),
            CapMicros = cap,
            CapAction = Enum<CapAction>(json, "capAction"),
            Approval = Enum<ToolApprovalMode>(json, "approval"),
        };
    }

    private static async Task<IResult> KeepAsync(string run, JsonObject? body, bool branch, AgentCompareRunner runner, IServiceProvider services, CancellationToken token) =>
        await RunAsync(async () =>
        {
            var json = Required(body);
            var slot = Text(json, "slot") ?? throw AgentCompareException.BadRequest("Name the model (slot).");
            var answer = await runner.KeepAsync(run, slot, branch, Bool(json, "discardOthers") ?? false, token).ConfigureAwait(false);
            return Json(new JsonObject
            {
                ["result"] = JsonSerializer.SerializeToNode(answer.Result, SettingsStore.JsonOptions),
                ["discarded"] = new JsonArray([.. answer.Discarded.Select(item => (JsonNode)JsonValue.Create(item)!)]),
                ["keptBack"] = new JsonArray([.. answer.KeptBack.Select(item => (JsonNode)JsonValue.Create(item)!)]),
                ["status"] = await AgentCompareStatus.ToJsonAsync(runner.Require(run), runner, services, token).ConfigureAwait(false),
            });
        }).ConfigureAwait(false);

    private static async Task EventsAsync(string id, HttpContext context, AgentCompareRunner runner, IServiceProvider services)
    {
        var token = context.RequestAborted;
        if (runner.Store.Get(id) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = new { code = AgentCompareException.NotFound, message = $"There is no agent compare run {id}." } }, token)
                .ConfigureAwait(false);
            return;
        }
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        var changed = new SemaphoreSlim(0, 1);
        void OnChanged(string run)
        {
            if (run != id) return;
            try { if (changed.CurrentCount == 0) changed.Release(); }
            catch (SemaphoreFullException) { /* Already signalled. */ }
        }
        runner.Store.Changed += OnChanged;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var run = runner.Store.Get(id);
                if (run is null) break;
                var status = await AgentCompareStatus.ToJsonAsync(run, runner, services, token).ConfigureAwait(false);
                await context.Response.WriteAsync("event: status\ndata: " + status.ToJsonString(SettingsStore.JsonOptions) + "\n\n", token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                if (!run.IsActive) break;
                // Coalesce bursts (a turn, then its tool call) into one frame; ping while nothing changes.
                while (!await changed.WaitAsync(PingInterval, token).ConfigureAwait(false))
                {
                    await context.Response.WriteAsync(": ping\n\n", token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The tab went away.
        }
        finally
        {
            runner.Store.Changed -= OnChanged;
            changed.Dispose();
        }
    }

    private static JsonObject Required(JsonObject? body) => body ?? throw AgentCompareException.BadRequest("Send a JSON body.");

    private static string? Text(JsonObject? json, string name) =>
        json?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && text.Trim().Length > 0 ? text.Trim() : null;

    private static long? Long(JsonObject? json, string name) => json?[name] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.Number && Number<long>(value) is { } number => number,
        JsonValue value when value.GetValueKind() == JsonValueKind.String && long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw AgentCompareException.BadRequest($"{name} is a whole number."),
    };

    // A number of a parsed body or of a node built in code (whose CLR type may be int or double), or null when it is not a T.
    private static T? Number<T>(JsonNode node) where T : struct
    {
        try { return node.Deserialize<T>(); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException) { return null; }
    }

    private static int? Int(JsonObject? json, string name) => Long(json, name) is { } value
        ? value is >= int.MinValue and <= int.MaxValue ? (int)value : throw AgentCompareException.BadRequest($"{name} is out of range.")
        : null;

    private static bool? Bool(JsonObject? json, string name) => json?[name] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
        _ => throw AgentCompareException.BadRequest($"{name} is true or false."),
    };

    private static List<string> Strings(JsonObject? json, string name) => json?[name] switch
    {
        null => [],
        JsonArray items => [.. items.Select(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>()
            : throw AgentCompareException.BadRequest($"{name} is a list of text."))],
        JsonValue value when value.GetValueKind() == JsonValueKind.String => [.. value.GetValue<string>().Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        _ => throw AgentCompareException.BadRequest($"{name} is a list of text."),
    };

    private static T? Enum<T>(JsonObject? json, string name) where T : struct, System.Enum
    {
        if (json?[name] is null) return null;
        try { return json[name].Deserialize<T>(SettingsStore.JsonOptions); }
        catch (JsonException) { throw AgentCompareException.BadRequest($"\"{json[name]}\" is not a {name} value."); }
    }

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (AgentCompareException error)
        {
            return Error(error.Status, error.Code, error.Message);
        }
        catch (WorkspaceException error)
        {
            return Error(error.Code is WorkspaceException.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, error.Code, error.Message);
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, AgentCompareException.Invalid, error.Message);
        }
    }
}
