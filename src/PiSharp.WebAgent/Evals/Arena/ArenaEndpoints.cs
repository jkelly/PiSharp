// Decision 0011 (run W5, E5): the Arena's JSON endpoints (EvalRoutes.ApiArena, EvalRoutes.ApiLeaderboard). Writes are JSON POSTs (the host
// guard: same origin, JSON only); every answer is no-store; errors are {error: {code, message}} (invalid 400, not-found 404, conflict 409,
// and agent compare's own codes, such as too-large 409, passed through). Before a battle is voted on, nothing here names a contestant:
// a battle answers as ArenaBlindBattle plus counts (finished, total), and its event stream sends the same, so the page can only tell how
// many answers are in, never which (brief rule 3). A voted or void battle adds `revealed` (each column's provider, model, name and
// settings) and `vote`.
//   POST /api/evals/arena                    {mode: "request", draft: {CompareDraft}, streamAnyway?, spend?} or {mode: "agent", agent: {R5's
//                                            create request, with requestId}} -> {posted: true, battle, url}; a request battle the Spend Guard
//                                            refused -> 200 {posted: false, battleId, code, message, spend: {...}} (nothing reserved or sent;
//                                            answer with POST {battleId}/start); 400 when a column cannot run or the models are not 2 to 4
//                                            different ones.
//   POST /api/evals/arena/{id}/start         {spend: {choice, maxOutputTokens?}} -> the same as a create (a refused request battle only).
//   POST /api/evals/arena/{id}/again         {spend?} -> a new request battle asking the same, newly shuffled (New round).
//   POST /api/evals/arena/{id}/discard       {} -> {discarded}: removes a battle that never started.
//   GET  /api/evals/arena/{id}               the battle (blind until voted).
//   GET  /api/evals/arena/{id}/events        SSE: `event: state` frames {id, status, finished, total, streamAnyway, seq} on every change (and,
//                                            while held with stream anyway on, as the answers grow); ": ping" every 15 s; ends once the battle
//                                            is no longer held.
//   POST /api/evals/arena/{id}/stream-anyway {on: bool} -> the battle.
//   POST /api/evals/arena/{id}/stop          {} -> the battle, now void (its answers cancelled; revealed, never counted).
//   POST /api/evals/arena/{id}/vote          {kind: pick|tie|all-bad|rank, pick?, ranking?: [letters], tag?} -> the battle, revealed, with
//                                            `saved`; 409 when it is not ready or has another vote (the same vote again is answered).
//   POST /api/evals/arena/{id}/approve       {letter, taskId, decision: allow|deny} (agent battles): answers one waiting tool call.
//   POST /api/evals/arena/{id}/continue      {letter, scope: turn|run} (agent battles): answers a budget Ask.
//   GET  /api/evals/leaderboard              ?mode=request|agent&tag= -> {tags: [LeaderboardTag], leaderboard: Leaderboard|null}.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Arena;

/// <summary>The Arena's JSON endpoints.</summary>
public static class ArenaEndpoints
{
    /// <summary>How often an idle event stream pings.</summary>
    public static TimeSpan PingInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>How often a held battle's stream looks at its answers (stream anyway shows them as they grow).</summary>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The battle page of <paramref name="id"/>.</summary>
    public static string PageUrl(string id) => "/compare/arena/" + Uri.EscapeDataString(id);

    /// <summary>Maps the endpoints.</summary>
    public static WebApplication Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup(EvalRoutes.ApiArena);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        api.MapPost("", (JsonObject? body, ArenaService arena, IServiceProvider services, CancellationToken token) => Answer(async () =>
        {
            var json = Required(body);
            var mode = EvalJson.FromWire<ArenaMode>(Text(json, "mode") ?? "request") ?? throw new ArgumentException("The mode is request or agent.");
            if (mode == ArenaMode.Agent)
            {
                var agent = json["agent"] as JsonObject ?? throw new ArgumentException("Send the agent run as {agent: {...}}.");
                var battle = await arena.CreateAgentAsync(agent, token).ConfigureAwait(false);
                return Json(Started(battle, arena));
            }
            var draft = CompareDraft.Parse(json["draft"] as JsonObject ?? throw new ArgumentException("Send the round as {draft: {...}}."));
            var start = await arena.CreateRequestAsync(draft, Bool(json, "streamAnyway") ?? false, RunSpendAnswer.Read(json["spend"]), token).ConfigureAwait(false);
            return await StartedAsync(start, arena, services).ConfigureAwait(false);
        }));
        api.MapPost("/{id}/start", (string id, JsonObject? body, ArenaService arena, IServiceProvider services, CancellationToken token) => Answer(async () =>
            await StartedAsync(await arena.StartAsync(id, RunSpendAnswer.Read(body?["spend"]), token).ConfigureAwait(false), arena, services).ConfigureAwait(false)));
        api.MapPost("/{id}/again", (string id, JsonObject? body, ArenaService arena, IServiceProvider services, CancellationToken token) => Answer(async () =>
            await StartedAsync(await arena.AgainAsync(id, RunSpendAnswer.Read(body?["spend"]), token).ConfigureAwait(false), arena, services).ConfigureAwait(false)));
        api.MapPost("/{id}/discard", (string id, ArenaService arena) => Answer(() => Task.FromResult(Json(new { discarded = arena.Discard(id) }))));
        api.MapGet("/{id}", (string id, ArenaService arena) => Answer(() => Task.FromResult(Json(Battle(arena.Require(id), arena)))));
        api.MapGet("/{id}/events", (string id, HttpContext context, ArenaService arena) => EventsAsync(id, context, arena));
        api.MapPost("/{id}/stream-anyway", (string id, JsonObject? body, ArenaService arena) => Answer(() =>
            Task.FromResult(Json(Battle(arena.SetStreamAnyway(id, Bool(Required(body), "on") ?? throw new ArgumentException("Send {on: true|false}.")), arena)))));
        api.MapPost("/{id}/stop", (string id, ArenaService arena, CancellationToken token) => Answer(async () =>
            Json(Battle(await arena.StopAsync(id, token).ConfigureAwait(false), arena))));
        api.MapPost("/{id}/vote", (string id, JsonObject? body, ArenaService arena) => Answer(() =>
        {
            var json = Required(body);
            var kind = EvalJson.FromWire<VoteKind>(Text(json, "kind")) ?? throw new ArgumentException("The vote is pick, tie, all-bad or rank.");
            var ranking = json["ranking"] is JsonArray items ? items.Select(item => item is JsonValue value && value.TryGetValue<string>(out var letter) ? letter : "").ToList() : null;
            var result = arena.Vote(id, kind, Text(json, "pick"), ranking, Text(json, "tag"));
            var answer = Battle(result.Battle, arena);
            answer["saved"] = result.Saved;
            return Task.FromResult(Json(answer));
        }));
        api.MapPost("/{id}/approve", (string id, JsonObject? body, ArenaService arena, IServiceProvider services, CancellationToken token) => Answer(async () =>
        {
            var json = Required(body);
            var (battle, run, slot) = AgentColumn(arena, id, Text(json, "letter"));
            var decision = Text(json, "decision") ?? "allow";
            if (decision is not ("allow" or "deny")) throw new ArgumentException("Decide allow or deny.");
            var task = json["taskId"] is JsonValue value && value.TryGetValue<long>(out var number) ? number : throw new ArgumentException("Name the call (taskId).");
            var runner = services.GetRequiredService<AgentCompareRunner>();
            await runner.ApproveAsync(run, AgentApprovalScope.Call, slot, task, decision == "allow", null, null, token).ConfigureAwait(false);
            return Json(Battle(arena.Require(battle.Id), arena));
        }));
        api.MapPost("/{id}/continue", (string id, JsonObject? body, ArenaService arena, IServiceProvider services) => Answer(() =>
        {
            var json = Required(body);
            var (battle, run, slot) = AgentColumn(arena, id, Text(json, "letter"));
            var rest = (Text(json, "scope") ?? "turn") switch
            {
                "turn" => false,
                "run" => true,
                _ => throw new ArgumentException("The scope is turn or run."),
            };
            services.GetRequiredService<AgentCompareRunner>().Continue(run, slot, rest);
            return Task.FromResult(Json(Battle(arena.Require(battle.Id), arena)));
        }));

        var board = app.MapGroup(EvalRoutes.ApiLeaderboard);
        board.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        board.MapGet("", (string? mode, string? tag, ArenaService arena, ArenaLeaderboards leaderboards) => Answer(() =>
        {
            var which = EvalJson.FromWire<ArenaMode>(string.IsNullOrWhiteSpace(mode) ? "request" : mode.Trim()) ?? throw new ArgumentException("The mode is request or agent.");
            var votes = arena.Voted();
            var tags = leaderboards.Tags(votes);
            var chosen = string.IsNullOrWhiteSpace(tag) ? tags.FirstOrDefault(item => item.Mode == which)?.Tag : EvalTags.Normalize(tag);
            var built = chosen is null ? null : leaderboards.Build(which, chosen, votes);
            return Task.FromResult(Json(new JsonObject
            {
                ["mode"] = EvalJson.Wire(which),
                ["tag"] = chosen,
                ["tags"] = EvalJson.ToNode(tags),
                ["leaderboard"] = built is null ? null : EvalJson.ToNode(built),
            }));
        }));
        return app;
    }

    /// <summary>The battle as a page may see it: blind (letters, status, counts) until it is voted or void, then also who each column was
    /// and the vote.</summary>
    public static JsonObject Battle(ArenaBattle battle, ArenaService arena)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(arena);
        var progress = arena.Progress(battle);
        var json = (JsonObject)EvalJson.ToNode(battle.ToBlind())!;
        json["finished"] = progress.Finished;
        json["total"] = progress.Total;
        json["url"] = PageUrl(battle.Id);
        if (battle.Status is ArenaStatus.Voted or ArenaStatus.Void)
        {
            json["revealed"] = new JsonArray([.. battle.Contestants.Select(contestant => (JsonNode)new JsonObject
            {
                ["letter"] = contestant.Letter,
                ["provider"] = contestant.Identity.Provider,
                ["model"] = contestant.Identity.Model,
                ["key"] = contestant.Identity.Key,
                ["settings"] = contestant.Identity.SettingsLabel,
            })]);
            if (arena.Store.VoteOf(battle.Id) is { } vote) json["vote"] = EvalJson.ToNode(vote);
        }
        return json;
    }

    private static JsonObject Started(ArenaBattle battle, ArenaService arena) =>
        new() { ["posted"] = true, ["battleId"] = battle.Id, ["url"] = PageUrl(battle.Id), ["battle"] = Battle(battle, arena) };

    private static async Task<IResult> StartedAsync(ArenaStart start, ArenaService arena, IServiceProvider services)
    {
        if (start.Refusal is not { } refusal) return Json(Started(start.Battle, arena));
        // The refusal is answered on the page where the user chose the models; it is about the group, never a column letter.
        return Json(new JsonObject
        {
            ["posted"] = false,
            ["battleId"] = start.Battle.Id,
            ["code"] = refusal.Code,
            ["message"] = refusal.Message,
            ["spend"] = await RunEndpoints.SpendAsync(refusal, services).ConfigureAwait(false),
        });
    }

    private static (ArenaBattle Battle, string Run, string Slot) AgentColumn(ArenaService arena, string id, string? letter)
    {
        var battle = arena.Require(id);
        if (battle.Mode != ArenaMode.Agent || battle.GroupId is not { } run) throw new ArgumentException("Only an agent battle has agents to answer.");
        var contestant = battle.At(letter) ?? throw new ArgumentException("Name the column (letter).");
        return (battle, run, AgentCompareRunner.Slots[contestant.Position]);
    }

    private static async Task EventsAsync(string id, HttpContext context, ArenaService arena)
    {
        var token = context.RequestAborted;
        if (arena.Get(id) is not { } first)
        {
            await Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, $"There is no battle {id}.").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        var changed = new ChangeSignal();
        void OnChanged(string battle)
        {
            if (battle == first.Id) changed.Set();
        }
        arena.Changed += OnChanged;
        var seq = 0L;
        string? last = null;
        try
        {
            var idle = TimeSpan.Zero;
            while (!token.IsCancellationRequested)
            {
                var battle = arena.Get(first.Id);
                if (battle is null) break;
                var progress = arena.Progress(battle);
                // Only counts and the status go out; the signature (which also tracks streamed text) only decides whether to send.
                var signature = battle.Status + "|" + battle.StreamAnyway + "|" + progress.Signature + "|" + arena.Waiting(battle);
                if (signature != last)
                {
                    last = signature;
                    idle = TimeSpan.Zero;
                    var frame = new JsonObject
                    {
                        ["id"] = battle.Id,
                        ["status"] = EvalJson.Wire(battle.Status),
                        ["finished"] = progress.Finished,
                        ["total"] = progress.Total,
                        ["streamAnyway"] = battle.StreamAnyway,
                        ["seq"] = ++seq,
                    };
                    await context.Response.WriteAsync("event: state\ndata: " + frame.ToJsonString(SettingsStore.JsonOptions) + "\n\n", token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                }
                if (battle.Status != ArenaStatus.Held) break;
                if (!await changed.WaitAsync(PollInterval, token).ConfigureAwait(false))
                {
                    idle += PollInterval;
                    if (idle >= PingInterval)
                    {
                        idle = TimeSpan.Zero;
                        await context.Response.WriteAsync(": ping\n\n", token).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The tab went away.
        }
        finally
        {
            arena.Changed -= OnChanged;
            changed.Close();
        }
    }

    private static JsonObject Required(JsonObject? body) => body ?? throw new ArgumentException("Send a JSON body.");

    private static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && text.Trim().Length > 0 ? text.Trim() : null;

    private static bool? Bool(JsonObject json, string name) => json[name] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.True => true,
        JsonValue value when value.GetValueKind() == JsonValueKind.False => false,
        _ => throw new ArgumentException($"{name} is true or false."),
    };

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static async Task<IResult> Answer(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CompareRefusedException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
        catch (RunOptionException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
        catch (AgentCompareException error)
        {
            return Error(error.Status, error.Code, error.Message);
        }
        catch (KeyNotFoundException error)
        {
            return Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, error.Message);
        }
        catch (InvalidOperationException error)
        {
            return Error(StatusCodes.Status409Conflict, "conflict", error.Message);
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
    }
}
