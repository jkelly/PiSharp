// PiSharp-only (decision 0008, PLAN.md Appendix A). The v1 routes of a durable device, wire-compatible with durable-pi's dpi-backend
// (apps/dpi-backend/src/routes.ts, MIT): `/healthz` is open and everything under `/v1` needs the device token. Errors are
// `{error: {code, message}}` with durable-pi's codes and statuses; a request body is JSON of at most 256 KB. Session refs are a UUID or a
// unique name, agent refs a number, `main` or a unique name. Creating a session, an agent or a fork with an `Idempotency-Key` happens once:
// the key is recorded in the commit that creates it. PiSharp additions are listed in `capabilities`: `/state` and `/jobs` (aliases of
// durable-pi's `/demo/*`), pending interactions (question forms and plan reviews), plan mode, and job runs.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Tools;

namespace PiSharp.Durable.Server;

/// <summary>Maps a durable device's v1 API.</summary>
public static class DurableDeviceApi
{
    /// <summary>Maps <c>/healthz</c> and the <c>/v1</c> routes for <paramref name="host"/>.</summary>
    public static IEndpointRouteBuilder MapDurableDeviceApi(this IEndpointRouteBuilder endpoints, DurableDeviceHost host, DurableDeviceServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        if (!DeviceTokens.IsStrong(options.Token))
            throw new ArgumentException($"A device token needs at least {DeviceTokens.MinimumLength} characters and no spaces.", nameof(options));
        var service = new DeviceService(host, options);
        var tokenHash = DeviceTokens.Hash(options.Token);

        endpoints.MapGet("/healthz", () => Json(new JsonObject { ["ok"] = true, ["apiVersion"] = DurableWire.ApiVersion }));

        var v1 = endpoints.MapGroup("/v1");
        v1.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var presented = http.Request.Headers.Authorization.ToString() is { Length: > 7 } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header[7..].Trim() : http.Request.Headers["X-DPI-Token"].ToString();
            if (!DeviceTokens.Matches(presented, tokenHash))
                return Error(DurableErrorCodes.Unauthorized, "A valid device token is required (Authorization: Bearer <token>).");
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch (Exception failure) when (!http.Response.HasStarted)
            {
                return Failure(failure, http);
            }
            catch (Exception failure) when (failure is OperationCanceledException or IOException)
            {
                return Results.Empty;
            }
        });

        // ─── Device ─────────────────────────────────────────────────────────
        v1.MapGet("/device", H(async http => Json(await service.InfoAsync(http.RequestAborted).ConfigureAwait(false))));
        v1.MapGet("/tree", H(async http => Json(await service.TreeAsync(http.RequestAborted).ConfigureAwait(false))));
        v1.MapGet("/events", H(http => DeviceStreams.DeviceAsync(service, http)));
        v1.MapGet("/fs/dirs", H(http => Json(service.Browse(http.Request.Query["path"].ToString()))));
        v1.MapGet("/docs/{kind}", (string kind) => DeviceService.DeviceDocKinds.Contains(kind)
            ? Json(service.AppState()) : throw new HostInvalidException($"Unknown device document {kind}."));

        // ─── Sessions ───────────────────────────────────────────────────────
        v1.MapGet("/sessions", H(async http => Json((await service.TreeAsync(http.RequestAborted).ConfigureAwait(false)).Sessions)));
        v1.MapPost("/sessions", H(async http =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var key = IdempotencyKey(http);
            var session = await host.CreateSessionAsync(new SessionCreateRequest
            {
                Cwd = String(body, "cwd") ?? "",
                Name = String(body, "name"),
                Model = ModelOf(body["model"]),
                ThinkingLevel = String(body, "thinkingLevel"),
                SpawnKey = key is null ? null : "http:" + key,
            }, http.RequestAborted).ConfigureAwait(false);
            return Json(await service.SessionAsync(session, http.RequestAborted).ConfigureAwait(false), 201);
        }));
        v1.MapGet("/sessions/{s}", async (HttpContext http, string s) =>
            Json(await service.SessionAsync(host.ResolveSession(s), http.RequestAborted).ConfigureAwait(false)));
        v1.MapPatch("/sessions/{s}", async (HttpContext http, string s) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var session = await host.RenameSessionAsync(s, String(body, "name") ?? throw new HostInvalidException("A name is required."),
                http.RequestAborted).ConfigureAwait(false);
            return Json(await service.SessionAsync(session, http.RequestAborted).ConfigureAwait(false));
        });
        v1.MapDelete("/sessions/{s}", async (HttpContext http, string s) =>
        {
            var archived = await host.ArchiveSessionAsync(s, http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["ok"] = true, ["archivedTo"] = archived });
        });
        v1.MapGet("/sessions/{s}/compaction", (string s) => Json(DeviceService.Compaction(host.ResolveSession(s).Compaction)));
        v1.MapPatch("/sessions/{s}/compaction", async (HttpContext http, string s) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var updated = host.ResolveSession(s).UpdateCompaction(new SessionCompactionChange
            {
                Enabled = Bool(body, "enabled"),
                ReserveTokens = Integer(body, "reserveTokens", 1024, 1_000_000),
                BackgroundTokens = Integer(body, "backgroundTokens", 0, 1_000_000),
                KeepRecentTokens = Integer(body, "keepRecentTokens", 500, 200_000),
            });
            return Json(DeviceService.Compaction(updated));
        });
        v1.MapGet("/sessions/{s}/events", (HttpContext http, string s) => DeviceStreams.SessionAsync(service, http, host.ResolveSession(s)));
        v1.MapGet("/sessions/{s}/tasks", async (HttpContext http, string s) =>
        {
            var status = http.Request.Query["status"].ToString() is { Length: > 0 } given ? given : "terminal";
            if (status is not ("live" or "terminal")) throw new HostInvalidException("status must be live or terminal.");
            var session = host.ResolveSession(s);
            if (status == "live") return Json(await service.LiveTasksAsync(session, null, http.RequestAborted).ConfigureAwait(false));
            var limit = int.TryParse(http.Request.Query["limit"], out var parsed) ? parsed : 20;
            return Json(await service.TasksAsync(session, status, limit, http.RequestAborted).ConfigureAwait(false));
        });
        v1.MapPost("/sessions/{s}/tasks/{taskId}/abort", async (HttpContext http, string s, string taskId) =>
        {
            var session = host.ResolveSession(s);
            var id = Id(taskId, "taskId");
            if (await session.GetHarness().GetTaskAsync(new TaskId(id), DeviceService.Context(http.RequestAborted)).ConfigureAwait(false) is null)
                throw new HostNotFoundException($"No task {id} in {session.Name}.");
            var result = await session.AbortTaskAsync(id, http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["status"] = result == TaskAbortResult.Terminal ? "terminal" : "marked" });
        });
        v1.MapGet("/sessions/{s}/inspect", async (HttpContext http, string s) =>
            Json(await DeviceService.InspectAsync(host.ResolveSession(s), http.RequestAborted).ConfigureAwait(false)));
        v1.MapGet("/sessions/{s}/submissions/{id}", async (HttpContext http, string s, string id) =>
        {
            var wait = WaitOf(http.Request.Query["waitMs"].ToString(), options);
            var result = await host.ResolveSession(s).SubmissionAsync(Id(id, "submission id"), wait, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.Result(result));
        });

        // ─── Agents ─────────────────────────────────────────────────────────
        const string agent = "/sessions/{s}/agents/{a}";
        v1.MapGet("/sessions/{s}/agents", async (HttpContext http, string s) =>
            Json((await host.ResolveSession(s).AgentsAsync(http.RequestAborted).ConfigureAwait(false)).Select(DeviceService.Agent)));
        v1.MapPost("/sessions/{s}/agents", async (HttpContext http, string s) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var session = host.ResolveSession(s);
            long? from = body["from"] is JsonNode source ? (await session.ResolveAgentAsync(Transcript.Describe(source), http.RequestAborted).ConfigureAwait(false)).Id : null;
            var created = await session.CreateAgentAsync(from, String(body, "name"), IdempotencyKey(http), http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.Agent(created), 201);
        });
        v1.MapGet(agent, async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return Json(await service.DetailAsync(session, resolved, http.RequestAborted).ConfigureAwait(false));
        });
        v1.MapPatch(agent, async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var renamed = await session.RenameAgentAsync(resolved.Id, String(body, "name"), http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.Agent(renamed));
        });
        v1.MapGet(agent + "/events", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return await DeviceStreams.AgentAsync(service, http, session, resolved.Id).ConfigureAwait(false);
        });
        v1.MapGet(agent + "/entries", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            if (http.Request.Query["scope"].ToString() == "active")
                return Json(new JsonObject { ["items"] = new JsonArray([.. await DeviceService.ActiveEntriesAsync(session, resolved.Id, http.RequestAborted).ConfigureAwait(false)]) });
            var limit = int.TryParse(http.Request.Query["limit"], out var parsed) ? parsed : 500;
            return Json(await DeviceService.EntriesAsync(session, resolved.Id, http.Request.Query["cursor"].ToString(), limit, http.RequestAborted).ConfigureAwait(false));
        });
        v1.MapGet(agent + "/transcript", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var items = Transcript.Build(await DeviceService.ActiveEntriesAsync(session, resolved.Id, http.RequestAborted).ConfigureAwait(false));
            return http.Request.Query["format"].ToString() == "markdown"
                ? Json(new JsonObject { ["format"] = "markdown", ["text"] = Transcript.ToMarkdown(items) })
                : Json(new JsonObject { ["format"] = "json", ["items"] = Transcript.ToJson(items) });
        });
        v1.MapPost(agent + "/messages", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var content = String(body, "content");
            if (string.IsNullOrWhiteSpace(content) || content.Length > HostedSession.MaxMessageLength)
                throw new HostInvalidException("content must be a message under 100,000 characters.");
            var whenBusy = (String(body, "whenBusy") ?? "followUp") switch
            {
                "followUp" => WhenBusy.FollowUp,
                "steer" => WhenBusy.Steer,
                "reject" => WhenBusy.Reject,
                _ => throw new HostInvalidException("whenBusy must be followUp, steer or reject."),
            };
            var requestId = String(body, "requestId");
            if (string.IsNullOrEmpty(requestId) || requestId.Length > 128)
                throw new HostInvalidException("requestId is required (at most 128 characters) so a retry never submits twice.");
            var wait = body["waitMs"] is JsonNode waitMs ? WaitOf(Transcript.Describe(waitMs), options) : null;
            var result = await session.SendAsync(resolved.Id, new SendRequest(content, requestId) { WhenBusy = whenBusy, Wait = wait },
                http.RequestAborted).ConfigureAwait(false);
            var json = DeviceService.Result(result);
            if (wait is null) json = new JsonObject { ["submissionId"] = result.SubmissionId.Value, ["status"] = "queued" };
            return Json(json, wait is null || result.Status is SendStatus.Pending or SendStatus.Queued ? 202 : 200);
        });
        v1.MapDelete(agent + "/inbox/{submissionId}", async (HttpContext http, string s, string a, string submissionId) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var result = await session.WithdrawAsync(resolved.Id, Id(submissionId, "submissionId"), http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["status"] = Snake(result.ToString()) });
        });
        v1.MapPost(agent + "/abort", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            await session.AbortAsync(resolved.Id, Bool(body, "background") == true, http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["ok"] = true });
        });
        v1.MapPost(agent + "/fork", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var entryId = body["entryId"] is JsonValue entry && entry.TryGetValue<double>(out var number) && number == Math.Floor(number)
                ? (long)number : throw new HostInvalidException("entryId is required.");
            var fork = await session.ForkAsync(resolved.Id, entryId, String(body, "name"), IdempotencyKey(http), http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.Agent(fork), 201);
        });
        v1.MapGet(agent + "/config", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return Json(DeviceService.Config(await session.ConfigurationAsync(resolved.Id, http.RequestAborted).ConfigureAwait(false)));
        });
        v1.MapPatch(agent + "/config", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var thinking = String(body, "thinkingLevel");
            if (thinking is not null && !DurableWire.ThinkingLevels.Contains(thinking)) throw new HostInvalidException("Invalid thinking level.");
            var configured = await session.ConfigureAsync(resolved.Id, new AgentConfigurationChange
            {
                Model = ModelOf(body["model"]),
                ThinkingLevel = thinking,
                Instructions = String(body, "instructions"),
                Cwd = String(body, "cwd"),
                ReadOnly = Bool(body, "readOnly"),
            }, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.Config(configured));
        });
        v1.MapGet(agent + "/extensions", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return Json(DeviceService.Extensions(await session.ExtensionsAsync(resolved.Id, http.RequestAborted).ConfigureAwait(false)));
        });
        v1.MapPatch(agent + "/extensions", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var enabled = Bool(body, "enabled") ?? throw new HostInvalidException("enabled must be a boolean.");
            var extensions = String(body, "name") is { } name
                ? await session.SetExtensionAsync(resolved.Id, name, enabled, http.RequestAborted).ConfigureAwait(false)
                : String(body, "prompt") is { } prompt
                    ? await session.SetPromptAsync(resolved.Id, prompt, enabled, http.RequestAborted).ConfigureAwait(false)
                    : throw new HostInvalidException("name or prompt is required.");
            return Json(DeviceService.Extensions(extensions));
        });
        v1.MapPost(agent + "/compact", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var task = await session.CompactAsync(resolved.Id, String(body, "instructions"), http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["taskId"] = task.Value });
        });
        v1.MapPost(agent + "/reset", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            await session.ResetAsync(resolved.Id, String(body, "note"), http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["ok"] = true });
        });
        v1.MapGet(agent + "/docs/{kind}", async (HttpContext http, string s, string a, string kind) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return Json(new JsonObject { ["kind"] = kind, ["value"] = await DeviceService.AgentDocAsync(session, resolved.Id, kind, http.RequestAborted).ConfigureAwait(false) });
        });

        // ─── App State, jobs, interactions, plan mode ───────────────────────
        async Task<IResult> ChangeState(HttpContext http, string s, string a)
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var expected = body["expectedRevision"] is JsonValue revision && revision.TryGetValue<double>(out var number) ? (long)number : (long?)null;
            AppStateChange change;
            if (String(body, "operation") is { } operation)
                change = new AppStateChange(operation switch
                {
                    "create" => AppStateOperation.Create,
                    "update" => AppStateOperation.Update,
                    "delete" => AppStateOperation.Delete,
                    "increment" => AppStateOperation.Increment,
                    _ => throw new HostInvalidException("operation must be create, update, delete or increment."),
                }, String(body, "path") ?? "", body["value"] is JsonNode value ? DeviceService.ToChord(value) : null);
            else if (body["replace"] is JsonObject document)
                change = new AppStateChange(AppStateOperation.Update, "", DeviceService.ToChord(document));
            else if (String(body, "goal") is { } goal)
                change = new AppStateChange(AppStateDocument.Read(host.AppState.Get().Value, "/goal") is null ? AppStateOperation.Create : AppStateOperation.Update, "/goal", goal);
            else if (Bool(body, "increment") == true)
                change = new AppStateChange(AppStateOperation.Increment, "/counter", 1.0);
            else if (String(body, "note") is { } note)
                change = new AppStateChange(AppStateOperation.Create, "/notes/-", note);
            else throw new HostInvalidException("A state operation is required.");
            await session.ChangeAppStateAsync(resolved.Id, change, expected, http.RequestAborted).ConfigureAwait(false);
            var state = service.AppState();
            state["ok"] = true;
            return Json(state);
        }
        v1.MapPost(agent + "/demo/state", ChangeState);
        v1.MapPost(agent + "/state", ChangeState);

        async Task<IResult> StartJob(HttpContext http, string s, string a)
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var job = String(body, "job");
            if (job is null || host.Jobs.All(definition => definition.Id != job))
                throw new HostInvalidException($"job must be one of {string.Join(", ", host.Jobs.Select(definition => definition.Id))}.");
            var input = body["input"] is JsonObject values ? (Chord.JsonObjectValue)DeviceService.ToChord(values) : null;
            var task = await session.RunJobAsync(resolved.Id, job, input, http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["taskId"] = task.Value }, 201);
        }
        v1.MapPost(agent + "/demo/jobs", StartJob);
        v1.MapPost(agent + "/jobs", StartJob);
        v1.MapGet(agent + "/jobs", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return Json(new JsonObject
            {
                ["definitions"] = new JsonArray([.. host.Jobs.Select(job => DeviceService.ToNode(job.ToJson()))]),
                ["runs"] = DeviceService.ToNode(await session.JobRunsAsync(resolved.Id, http.RequestAborted).ConfigureAwait(false)),
            });
        });

        async Task<IResult> DecideJob(HttpContext http, string s, string a, string taskId)
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var approved = Bool(body, "approved") ?? throw new HostInvalidException("approved must be a boolean.");
            await session.DecideJobAsync(resolved.Id, Id(taskId, "taskId"), approved, http.RequestAborted).ConfigureAwait(false);
            return Json(new JsonObject { ["ok"] = true });
        }
        v1.MapPost(agent + "/demo/jobs/{taskId}/approval", DecideJob);
        v1.MapPost(agent + "/jobs/{taskId}/approval", DecideJob);

        v1.MapGet(agent + "/interactions", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return Json(DeviceService.Interactions(await session.PendingInteractionsAsync(resolved.Id, http.RequestAborted).ConfigureAwait(false)));
        });
        v1.MapPost(agent + "/interactions/{taskId}", async (HttpContext http, string s, string a, string taskId) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var id = Id(taskId, "taskId");
            var pending = (await session.PendingInteractionsAsync(resolved.Id, http.RequestAborted).ConfigureAwait(false)).FirstOrDefault(item => item.TaskId.Value == id)
                ?? throw new AppStateConflictException($"Tool call {id} does not wait for an answer.");
            if (pending.Kind == PlanMode.DetailsKind)
            {
                var approved = Bool(body, "approved") ?? throw new HostInvalidException("approved must be a boolean.");
                await session.ReviewPlanAsync(resolved.Id, id, approved, String(body, "feedback"), http.RequestAborted).ConfigureAwait(false);
            }
            else if (Bool(body, "decline") == true)
                await session.AnswerQuestionsAsync(resolved.Id, id, null, http.RequestAborted).ConfigureAwait(false);
            else if (body["answers"] is JsonArray answers)
                await session.AnswerQuestionsAsync(resolved.Id, id, QuestionResponse.Answered(answers.Select(answer => answer is JsonObject value
                    ? QuestionAnswer.FromJson(DeviceService.ToChord(value)) : throw new HostInvalidException("Each answer is {selected, other?}."))),
                    http.RequestAborted).ConfigureAwait(false);
            else throw new HostInvalidException("answers or decline is required.");
            return Json(new JsonObject { ["ok"] = true });
        });
        v1.MapGet(agent + "/plan-mode", async (HttpContext http, string s, string a) =>
        {
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            return Json(new JsonObject { ["enabled"] = await session.PlanModeAsync(resolved.Id, http.RequestAborted).ConfigureAwait(false) });
        });
        v1.MapPatch(agent + "/plan-mode", async (HttpContext http, string s, string a) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (session, resolved) = await AgentAsync(host, s, a, http).ConfigureAwait(false);
            var enabled = Bool(body, "enabled") ?? throw new HostInvalidException("enabled must be a boolean.");
            return Json(new JsonObject { ["enabled"] = await session.SetPlanModeAsync(resolved.Id, enabled, http.RequestAborted).ConfigureAwait(false) });
        });

        v1.Map("{**rest}", () => Error(DurableErrorCodes.NotFound, "API route not found."));
        return endpoints;
    }

    /// <summary>A handler that takes only the context; as a Delegate, its IResult is written (a lambda would bind as a RequestDelegate).</summary>
    private static Delegate H(Func<HttpContext, Task<IResult>> handler) => handler;

    private static Delegate H(Func<HttpContext, IResult> handler) => handler;

    private static async Task<(HostedSession Session, HostedAgent Agent)> AgentAsync(DurableDeviceHost host, string s, string a, HttpContext http)
    {
        var session = host.ResolveSession(s);
        return (session, await session.ResolveAgentAsync(a, http.RequestAborted).ConfigureAwait(false));
    }

    // ─── Bodies ─────────────────────────────────────────────────────────────

    private static async Task<JsonObject> BodyAsync(HttpContext http, DurableDeviceServerOptions options)
    {
        var request = http.Request;
        if (request.ContentLength is 0 || (request.ContentLength is null && !request.Headers.TransferEncoding.Any())) return [];
        if (request.ContentType is { } type && !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new HostInvalidException("The body must be JSON (Content-Type: application/json).");
        if (request.ContentLength > options.MaxJsonBytes) throw new HostInvalidException($"The body is larger than {options.MaxJsonBytes / 1024} KB.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, http.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > options.MaxJsonBytes) throw new HostInvalidException($"The body is larger than {options.MaxJsonBytes / 1024} KB.");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return [];
        try
        {
            return JsonNode.Parse(buffer.ToArray()) as JsonObject ?? throw new HostInvalidException("The body must be a JSON object.");
        }
        catch (JsonException failure)
        {
            throw new HostInvalidException("The body is not valid JSON: " + failure.Message);
        }
    }

    private static string? IdempotencyKey(HttpContext http) =>
        http.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } key
            ? key.Length <= 200 ? key : throw new HostInvalidException("Idempotency-Key must have at most 200 characters.")
            : null;

    private static string? String(JsonObject body, string key) => body[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new HostInvalidException($"{key} must be a string."),
    };

    private static bool? Bool(JsonObject body, string key) => body[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        _ => throw new HostInvalidException($"{key} must be a boolean."),
    };

    private static double? Integer(JsonObject body, string key, double minimum, double maximum) => body[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<double>(out var number) && number == Math.Floor(number) && number >= minimum && number <= maximum => number,
        _ => throw new HostInvalidException($"{key} must be an integer from {minimum} to {maximum}."),
    };

    private static ModelRef? ModelOf(JsonNode? node) => node switch
    {
        null => null,
        JsonObject model when model["provider"] is JsonValue provider && provider.TryGetValue<string>(out var providerId) &&
            model["modelId"] is JsonValue id && id.TryGetValue<string>(out var modelId) => new ModelRef(providerId, modelId),
        _ => throw new HostInvalidException("model must be {provider, modelId}."),
    };

    private static long Id(string text, string what) =>
        long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id : throw new HostInvalidException($"{what} must be an integer.");

    private static TimeSpan? WaitOf(string text, DurableDeviceServerOptions options) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(Math.Min(ms, options.MaxWait.TotalMilliseconds)) : null;

    private static string Snake(string name) => string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

    // ─── Responses ──────────────────────────────────────────────────────────

    internal static IResult Json<T>(T value, int status = 200) =>
        Results.Content(value is JsonNode node ? node.ToJsonString(DurableWire.Options) : DurableWire.Serialize(value), "application/json; charset=utf-8",
            Encoding.UTF8, status);

    internal static IResult Error(string code, string message) =>
        Json(new ApiErrorBody(new ApiError(code, message)), DurableErrorCodes.Status(code));

    private static IResult Failure(Exception failure, HttpContext http) => failure switch
    {
        DurableHostException known => Error(known.Code, known.Message),
        OperationCanceledException when http.RequestAborted.IsCancellationRequested => Results.Empty,
        ArgumentException or FormatException or InvalidCastException or JsonException => Error(DurableErrorCodes.Invalid, failure.Message),
        _ => Error(DurableErrorCodes.Internal, failure.Message),
    };
}
