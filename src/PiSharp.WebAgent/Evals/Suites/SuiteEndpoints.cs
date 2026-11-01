// Decision 0011 (run W5, E4): the suites' JSON API. Writes are idempotent JSON POSTs (HostGuard's same-origin and JSON-only rules cover them):
// a create carries its id (or gets one) and a retry with the same id returns the same row; an update, delete, move, cancel or baseline
// sent twice ends the same way; "Run now" takes an optional run id, so a retry finds the run it started.
//
//   GET  /api/evals/suites                       {items: [overview]}: each suite with its case count, worst and typical cost, next slot,
//                                                last and active run, current baselines and drift level (evals-suites _SuiteCard).
//   POST /api/evals/suites                       {id?, name, description?, targets?, k?, capMicros?} -> {created, suite}
//   GET  /api/evals/suites/{id}                  {overview, cases, runs (newest 20), baselines, slots (newest 20), signals (open)}
//   POST /api/evals/suites/{id}                  {name?, description?, targets?, k?, capMicros?} -> {suite} (a schedule that stops fitting
//                                                is paused, one that fits again resumes)
//   POST /api/evals/suites/{id}/delete           -> {deleted}; 409 while a run is going
//   POST /api/evals/suites/{id}/schedule         {enabled, repeat?, everyHours?, at?, day?, timeZone?, capMicros?} -> {saved: true, suite,
//                                                estimate, next} or {saved: false, refused: {code, message, worstMicros, capMicros}, estimate}
//   POST /api/evals/suites/{id}/estimate         {case?} -> {estimate, after?, capMicros}: the worst and typical cost, and with a case (a pin's
//                                                draft) the worst case once it is added (modal-test-case's over-cap pane)
//   POST /api/evals/suites/{id}/run              {runId?, caseIds?, spend?} -> {posted: true, run, url} | {posted: false, runId, spend: {...}}
//                                                (the Spend Guard's confirm modal, as /api/runs answers; send runId back with the answer) |
//                                                {posted: false, refused: {code, message}}
//   POST /api/evals/suites/{id}/baseline         {runId, targets?, replaceReferences?} -> {accepted, cleared, referencesReplaced}
//   GET  /api/evals/suites/{id}/runs             ?limit&offset -> {items, total}
//   GET  /api/evals/suites/{id}/export           the suite and its cases as a JSON download
//   POST /api/evals/cases                        a TestCase ({id?, suiteId, name, spec, reference?, checks?, tags?, origin?}) -> {created, case}
//   GET  /api/evals/cases/{id}                   {case}
//   POST /api/evals/cases/{id}                   {name?, tags?, spec?, reference?, checks?} -> {case}
//   POST /api/evals/cases/{id}/delete            -> {deleted}
//   POST /api/evals/cases/{id}/move              {position} -> {moved}
//   GET  /api/evals/runs/{id}                    {run, suite, running, cases, results, targets: [{target, summary, baseline}], signals, url}
//   POST /api/evals/runs/{id}/cancel             -> {id, cancelled}
//   GET  /api/evals/runs/{id}/results            the run and its results as a JSON download
//   GET  /api/evals/drift                        {suites: [{suite, level, targets: [{target, level, baseline, signals}], paused}],
//                                                priceChanges: [{change, effects}]}
// Errors are {error: {code, message}}: 400 invalid, 404 not_found, 409 conflict. Every answer is Cache-Control: no-store, written with
// EvalJson.Options.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Store;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Suites;

/// <summary>Maps <c>/api/evals/suites</c>, <c>/cases</c>, <c>/runs</c> and <c>/drift</c>.</summary>
public static class SuiteEndpoints
{
    /// <summary>How many runs, slots and signals a suite's answer lists.</summary>
    public const int RecentCount = 20;

    /// <summary>Maps the endpoints.</summary>
    public static IEndpointRouteBuilder MapSuiteEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        MapSuites(Group(routes, EvalRoutes.ApiSuites));
        MapCases(Group(routes, EvalRoutes.ApiCases));
        MapRuns(Group(routes, EvalRoutes.ApiRuns));
        Group(routes, EvalRoutes.ApiDrift).MapGet("", (SuiteService suites, IServiceProvider services) => Handle(() => Json(DriftJson(suites, services))));
        return routes;
    }

    private static RouteGroupBuilder Group(IEndpointRouteBuilder routes, string prefix)
    {
        var group = routes.MapGroup(prefix);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        return group;
    }

    private static void MapSuites(RouteGroupBuilder api)
    {
        api.MapGet("", (SuiteService suites) => Handle(() =>
            Json(new JsonObject { ["items"] = new JsonArray([.. suites.Store.Suites().Select(suite => (JsonNode)OverviewJson(suites.Overview(suite)))]) })));
        api.MapPost("", (JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var json = Required(body);
            var suite = new Suite
            {
                Id = Text(json, "id") ?? EvalIds.New(EvalIds.Suite),
                Name = Text(json, "name") ?? "",
                Description = Text(json, "description"),
                Targets = json["targets"] is JsonArray targets ? Read<List<SuiteTarget>>(targets, "targets") : [],
                K = Int(json, "k") ?? Suite.DefaultK,
                CapMicros = Long(json, "capMicros"),
            };
            var (saved, created) = suites.CreateSuite(suite);
            return Json(new JsonObject { ["created"] = created, ["suite"] = EvalJson.ToNode(saved) });
        }));
        api.MapGet("/{id}", (string id, SuiteService suites) => Handle(() =>
        {
            var suite = suites.RequireSuite(id);
            var store = suites.Store;
            return Json(new JsonObject
            {
                ["overview"] = OverviewJson(suites.Overview(suite)),
                ["cases"] = EvalJson.ToNode(store.Cases(suite.Id)),
                ["runs"] = EvalJson.ToNode(store.Runs(suite.Id, RecentCount)),
                ["baselines"] = EvalJson.ToNode(store.Baselines(suite.Id)),
                ["slots"] = EvalJson.ToNode(store.Slots(suite.Id, RecentCount)),
                ["signals"] = EvalJson.ToNode(store.Signals(suite.Id)),
            });
        }));
        api.MapPost("/{id}", (string id, JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var json = Required(body);
            var saved = suites.UpdateSuite(id, suite => suite with
            {
                Name = json.ContainsKey("name") ? Text(json, "name") ?? "" : suite.Name,
                Description = json.ContainsKey("description") ? Text(json, "description") : suite.Description,
                Targets = json.ContainsKey("targets") ? json["targets"] is JsonArray targets ? Read<List<SuiteTarget>>(targets, "targets") : [] : suite.Targets,
                K = json.ContainsKey("k") ? Int(json, "k") ?? throw new ArgumentException("k is a whole number.") : suite.K,
                CapMicros = json.ContainsKey("capMicros") ? Long(json, "capMicros") : suite.CapMicros,
            });
            return Json(new JsonObject { ["suite"] = EvalJson.ToNode(saved) });
        }));
        api.MapPost("/{id}/delete", (string id, SuiteService suites) => Handle(() => Json(new JsonObject { ["deleted"] = suites.DeleteSuite(id) })));
        api.MapPost("/{id}/schedule", (string id, JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var json = Required(body);
            var enabled = Bool(json, "enabled") ?? true;
            SuiteSchedule? schedule = null;
            if (enabled)
            {
                var copy = (JsonObject)json.DeepClone();
                copy.Remove("capMicros");
                copy.Remove("enabledAt");
                copy.Remove("paused");
                schedule = Read<SuiteSchedule>(copy, "schedule") with { Enabled = true };
            }
            var save = suites.SetSchedule(id, schedule, Long(json, "capMicros"));
            var answer = new JsonObject
            {
                ["saved"] = save.Saved,
                ["suite"] = EvalJson.ToNode(save.Suite),
                ["estimate"] = EstimateJson(save.Estimate, save.Suite.CapMicros),
                ["next"] = save.Next is { } next ? EvalJson.ToNode(next) : null,
            };
            if (save.Suite.Schedule is { } saved)
                answer["schedule"] = new JsonObject { ["label"] = saved.Label, ["runsPerDay"] = saved.MaxRunsPerDay, ["dayWorstMicros"] = save.Estimate.WorstMicros * saved.MaxRunsPerDay };
            if (save.Refused is { } refused) answer["refused"] = EvalJson.ToNode(refused);
            return Json(answer);
        }));
        api.MapPost("/{id}/estimate", (string id, JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var suite = suites.RequireSuite(id);
            TestCase? extra = null;
            if (body?["case"] is JsonObject draft) extra = CaseOf(draft, suite.Id);
            var (before, after) = suites.EstimateWith(suite, extra);
            return Json(new JsonObject
            {
                ["estimate"] = EstimateJson(before, suite.CapMicros),
                ["after"] = after is null ? null : EstimateJson(after, suite.CapMicros),
                ["capMicros"] = suite.CapMicros,
            });
        }));
        api.MapPost("/{id}/run", (string id, JsonObject? body, SuiteRunner runner, IServiceProvider services, CancellationToken token) => HandleAsync(async () =>
        {
            var json = body ?? new JsonObject();
            var request = new SuiteRunRequest
            {
                RunId = Text(json, "runId"),
                CaseIds = json["caseIds"] is JsonArray ids ? [.. ids.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim()
                    : throw new ArgumentException("caseIds is a list of test case ids."))] : null,
                Spend = RunSpendAnswer.Read(json["spend"]),
            };
            var start = await runner.StartAsync(id, request, token).ConfigureAwait(false);
            if (start.Posted && start.Run is { } run)
                return Json(new JsonObject { ["posted"] = true, ["run"] = EvalJson.ToNode(run), ["url"] = EvalRoutes.Run(run.Id) });
            if (start.Spend is { } refusal)
                return Json(new JsonObject
                {
                    ["posted"] = false,
                    // The run id the approval is stored under: the confirm modal sends it back as runId with its spend answer.
                    ["runId"] = refusal.Call.Run,
                    ["spend"] = await RunEndpoints.SpendAsync(refusal, services).ConfigureAwait(false),
                });
            return Json(new JsonObject { ["posted"] = false, ["refused"] = EvalJson.ToNode(start.Refused) });
        }));
        api.MapPost("/{id}/baseline", (string id, JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var json = Required(body);
            var runId = Text(json, "runId") ?? throw new ArgumentException("Choose the run to set as the baseline.");
            IReadOnlyList<string>? targets = json["targets"] is JsonArray chosen
                ? [.. chosen.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : throw new ArgumentException("targets is a list of target ids."))]
                : null;
            var accepted = suites.AcceptBaseline(id, runId, targets, Bool(json, "replaceReferences") ?? false);
            return Json(new JsonObject
            {
                ["accepted"] = EvalJson.ToNode(accepted.Accepted),
                ["cleared"] = EvalJson.ToNode(accepted.Cleared),
                ["referencesReplaced"] = accepted.ReferencesReplaced,
            });
        }));
        api.MapGet("/{id}/runs", (string id, HttpRequest request, SuiteService suites) => Handle(() =>
        {
            var suite = suites.RequireSuite(id);
            var limit = int.TryParse(request.Query["limit"], out var given) ? Math.Clamp(given, 1, 200) : 50;
            var offset = int.TryParse(request.Query["offset"], out var skip) ? Math.Max(0, skip) : 0;
            return Json(new JsonObject
            {
                ["items"] = EvalJson.ToNode(suites.Store.Runs(suite.Id, limit, offset)),
                ["total"] = suites.Store.RunCount(suite.Id),
                ["limit"] = limit,
                ["offset"] = offset,
            });
        }));
        api.MapGet("/{id}/export", (string id, HttpContext http, SuiteService suites) => Handle(() =>
        {
            var suite = suites.RequireSuite(id);
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"suite-{suite.Id}.json\"";
            return Json(new JsonObject { ["suite"] = EvalJson.ToNode(suite), ["cases"] = EvalJson.ToNode(suites.Store.Cases(suite.Id)) });
        }));
    }

    private static void MapCases(RouteGroupBuilder api)
    {
        api.MapPost("", (JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var json = Required(body);
            var suiteId = Text(json, "suiteId") ?? throw new ArgumentException("Choose the suite to add the case to.");
            var (saved, created) = suites.CreateCase(CaseOf(json, suiteId));
            return Json(new JsonObject { ["created"] = created, ["case"] = EvalJson.ToNode(saved) });
        }));
        api.MapGet("/{id}", (string id, SuiteService suites) => Handle(() =>
            Json(new JsonObject { ["case"] = EvalJson.ToNode(suites.Store.Case(id) ?? throw new KeyNotFoundException($"There is no test case {id}.")) })));
        api.MapPost("/{id}", (string id, JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var json = Required(body);
            var saved = suites.UpdateCase(id, current =>
            {
                var merged = (JsonObject)EvalJson.ToNode(current)!;
                foreach (var key in new[] { "name", "tags", "spec", "reference", "checks" })
                    if (json.ContainsKey(key)) merged[key] = json[key]?.DeepClone();
                return Read<TestCase>(merged, "test case");
            });
            return Json(new JsonObject { ["case"] = EvalJson.ToNode(saved) });
        }));
        api.MapPost("/{id}/delete", (string id, SuiteService suites) => Handle(() => Json(new JsonObject { ["deleted"] = suites.DeleteCase(id) })));
        api.MapPost("/{id}/move", (string id, JsonObject? body, SuiteService suites) => Handle(() =>
        {
            var position = Int(Required(body), "position") ?? throw new ArgumentException("Send {position} (0 first).");
            return suites.MoveCase(id, position) ? Json(new JsonObject { ["moved"] = true }) : throw new KeyNotFoundException($"There is no test case {id}.");
        }));
    }

    private static void MapRuns(RouteGroupBuilder api)
    {
        api.MapGet("/{id}", (string id, EvalStore store, SuiteRunner runner) => Handle(() => Json(RunJson(id, store, runner))));
        api.MapPost("/{id}/cancel", (string id, EvalStore store, SuiteRunner runner) => Handle(() =>
        {
            if (store.Run(id) is null) throw new KeyNotFoundException($"There is no suite run {id}.");
            return Json(new JsonObject { ["id"] = id, ["cancelled"] = runner.Cancel(id) });
        }));
        api.MapGet("/{id}/results", (string id, HttpContext http, EvalStore store) => Handle(() =>
        {
            var run = store.Run(id) ?? throw new KeyNotFoundException($"There is no suite run {id}.");
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"suite-run-{run.Number}-{run.Id}.json\"";
            return Json(new JsonObject { ["run"] = EvalJson.ToNode(run), ["results"] = EvalJson.ToNode(store.Results(run.Id)), ["signals"] = EvalJson.ToNode(store.RunSignals(run.Id)) });
        }));
    }

    // ----- JSON -----

    /// <summary>A suite run as the run page reads it: the run, its suite's name, its cases' names, its results, each target's totals beside
    /// its current baseline's, and the signals it raised.</summary>
    public static JsonObject RunJson(string id, EvalStore store, SuiteRunner runner)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runner);
        var run = store.Run(id) ?? throw new KeyNotFoundException($"There is no suite run {id}.");
        var suite = store.Suite(run.SuiteId);
        var results = store.Results(run.Id);
        var cases = store.Cases(run.SuiteId).ToDictionary(testCase => testCase.Id, StringComparer.Ordinal);
        var targets = new JsonArray();
        foreach (var target in run.Targets)
        {
            var baselineJson = (JsonNode?)null;
            if (store.CurrentBaseline(run.SuiteId, target.Id) is { } baseline && store.Run(baseline.RunId) is { } baselineRun)
                baselineJson = new JsonObject
                {
                    ["runId"] = baselineRun.Id,
                    ["number"] = baselineRun.Number,
                    ["accepted"] = EvalJson.ToNode(baseline.Accepted),
                    ["responseModels"] = EvalJson.ToNode(baseline.ResponseModels),
                    ["summary"] = SummaryJson(TargetSummary.Of(target.Id, baselineRun.CaseIds.Count * baselineRun.K, store.Results(baselineRun.Id))),
                };
            targets.Add(new JsonObject
            {
                ["target"] = TargetJson(target),
                ["summary"] = SummaryJson(TargetSummary.Of(target.Id, run.CaseIds.Count * run.K, results)),
                ["baseline"] = baselineJson,
            });
        }
        return new JsonObject
        {
            ["run"] = EvalJson.ToNode(run),
            ["suite"] = suite is null ? null : new JsonObject { ["id"] = suite.Id, ["name"] = suite.Name, ["schedule"] = suite.Schedule?.Label },
            ["running"] = runner.IsRunning(run.Id),
            ["answers"] = run.Answers,
            ["cases"] = new JsonArray([.. run.CaseIds.Select(caseId => (JsonNode)new JsonObject
            {
                ["id"] = caseId,
                ["name"] = cases.TryGetValue(caseId, out var testCase) ? testCase.Name : null,
                ["checks"] = cases.TryGetValue(caseId, out var withChecks) ? withChecks.Checks.Count : null,
            })]),
            ["results"] = EvalJson.ToNode(results),
            ["targets"] = targets,
            ["signals"] = EvalJson.ToNode(store.RunSignals(run.Id)),
            ["url"] = EvalRoutes.Run(run.Id),
        };
    }

    /// <summary>The drift page's data: every suite with a baseline or a schedule, with each target's level, baseline and open signals, and
    /// the price changes of the last 30 days with their effect on the suites that use them.</summary>
    public static JsonObject DriftJson(SuiteService suites, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(suites);
        ArgumentNullException.ThrowIfNull(services);
        var store = suites.Store;
        var all = store.Suites();
        var suiteCases = all.Select(suite => new SuiteCases(suite, store.Cases(suite.Id))).ToList();
        var items = new JsonArray();
        foreach (var suite in all)
        {
            var baselines = store.Baselines(suite.Id);
            var signals = store.Signals(suite.Id);
            if (baselines.Count == 0 && signals.Count == 0 && suite.Schedule is not { Enabled: true }) continue;
            var latest = store.Runs(suite.Id, 1).FirstOrDefault();
            items.Add(new JsonObject
            {
                ["suite"] = new JsonObject { ["id"] = suite.Id, ["name"] = suite.Name, ["url"] = EvalRoutes.Suite(suite.Id) },
                ["level"] = EvalJson.ToNode(DriftRules.Worst(signals)),
                ["paused"] = suite.Schedule?.Paused is { } paused ? EvalJson.ToNode(paused) : null,
                ["latestRun"] = latest is null ? null : new JsonObject { ["id"] = latest.Id, ["number"] = latest.Number, ["status"] = EvalJson.ToNode(latest.Status) },
                ["targets"] = new JsonArray([.. suite.Targets.Select(target =>
                {
                    var mine = signals.Where(signal => signal.TargetId == target.Id).ToList();
                    var baseline = baselines.FirstOrDefault(item => item.TargetId == target.Id);
                    return (JsonNode)new JsonObject
                    {
                        ["target"] = TargetJson(target),
                        ["level"] = EvalJson.ToNode(DriftRules.Worst(mine)),
                        ["baseline"] = baseline is null ? null : new JsonObject
                        {
                            ["runId"] = baseline.RunId,
                            ["number"] = store.Run(baseline.RunId)?.Number,
                            ["accepted"] = EvalJson.ToNode(baseline.Accepted),
                            ["responseModels"] = EvalJson.ToNode(baseline.ResponseModels),
                        },
                        ["signals"] = EvalJson.ToNode(mine),
                    };
                })]),
            });
        }
        var changes = new JsonArray();
        if (services.GetService<PriceWatch>() is { } watch)
        {
            foreach (var row in watch.Changes())
                changes.Add(new JsonObject
                {
                    ["id"] = row.Id,
                    ["change"] = EvalJson.ToNode(row.Change),
                    ["label"] = row.Change.Label,
                    ["rise"] = row.Change.Rise,
                    ["effects"] = EvalJson.ToNode(watch.Effects(row, suiteCases)),
                });
        }
        return new JsonObject { ["suites"] = items, ["priceChanges"] = changes };
    }

    /// <summary>A suite's overview as JSON.</summary>
    public static JsonObject OverviewJson(SuiteOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return new JsonObject
        {
            ["suite"] = EvalJson.ToNode(overview.Suite),
            ["cases"] = overview.Cases,
            ["answersPerRun"] = overview.AnswersPerRun,
            ["estimate"] = EstimateJson(overview.Estimate, overview.Suite.CapMicros),
            ["schedule"] = overview.Suite.Schedule is { } schedule ? new JsonObject
            {
                ["label"] = schedule.Label,
                ["enabled"] = schedule.Enabled,
                ["active"] = schedule.Active,
                ["paused"] = schedule.Paused is { } paused ? EvalJson.ToNode(paused) : null,
                ["next"] = overview.Next is { } next ? EvalJson.ToNode(next) : null,
                ["runsPerDay"] = schedule.MaxRunsPerDay,
            } : null,
            ["lastRun"] = EvalJson.ToNode(overview.LastRun),
            ["activeRun"] = EvalJson.ToNode(overview.ActiveRun),
            ["baselines"] = EvalJson.ToNode(overview.Baselines),
            ["level"] = EvalJson.ToNode(overview.Level),
            ["targetLevels"] = new JsonObject(overview.Suite.Targets.Select(target =>
                new KeyValuePair<string, JsonNode?>(target.Id, EvalJson.ToNode(DriftRules.Worst(overview.Signals.Where(signal => signal.TargetId == target.Id)))))),
            ["url"] = EvalRoutes.Suite(overview.Suite.Id),
        };
    }

    /// <summary>An estimate as JSON: its amounts and lines, whether a price is unknown, whether it fits <paramref name="capMicros"/>, and
    /// the amounts as the pages write them.</summary>
    public static JsonObject EstimateJson(SuiteEstimate estimate, long? capMicros)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return new JsonObject
        {
            ["worstMicros"] = estimate.WorstMicros,
            ["typicalMicros"] = estimate.TypicalMicros,
            ["listMicros"] = estimate.ListMicros,
            ["unknownPrice"] = estimate.HasUnknownPrice,
            ["fits"] = capMicros is { } cap ? estimate.Fits(cap) && !estimate.HasUnknownPrice : null,
            ["worst"] = DriftText.Money(estimate.WorstMicros),
            ["typical"] = estimate.TypicalMicros is { } typical ? DriftText.Money(typical) : null,
            ["list"] = estimate.ListMicros > 0 ? DriftText.Money(estimate.ListMicros, list: true) : null,
            ["lines"] = EvalJson.ToNode(estimate.Lines),
        };
    }

    private static JsonObject TargetJson(SuiteTarget target)
    {
        var json = (JsonObject)EvalJson.ToNode(target)!;
        json["id"] = target.Id;
        json["identity"] = target.Identity.Key;
        return json;
    }

    private static JsonObject SummaryJson(TargetSummary summary)
    {
        var json = (JsonObject)EvalJson.ToNode(summary)!;
        json["passRate"] = summary.PassRate;
        return json;
    }

    // ----- reading -----

    private static TestCase CaseOf(JsonObject json, string suiteId)
    {
        var copy = (JsonObject)json.DeepClone();
        copy["suiteId"] = suiteId;
        if (Text(copy, "id") is null) copy["id"] = EvalIds.New(EvalIds.Case);
        if (copy["origin"] is null) copy["origin"] = new JsonObject { ["source"] = EvalJson.Wire(PinSource.Suite) };
        if (copy["name"] is null) copy["name"] = "";
        if (copy["spec"] is null) throw new ArgumentException("A test case needs its request (spec).");
        return Read<TestCase>(copy, "test case");
    }

    private static T Read<T>(JsonNode json, string what)
    {
        try
        {
            return json.Deserialize<T>(EvalJson.Options) ?? throw new ArgumentException($"The {what} is empty.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException($"The {what} is not valid: {error.Message}");
        }
    }

    private static string? Text(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static int? Int(JsonObject json, string key) =>
        json[key] is null ? null : json[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : throw new ArgumentException($"{key} is a whole number.");

    private static long? Long(JsonObject json, string key) =>
        json[key] is null ? null : json[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : throw new ArgumentException($"{key} is a whole number of micro-USD.");

    private static bool? Bool(JsonObject json, string key) =>
        json[key] is null ? null : json[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : throw new ArgumentException($"{key} is true or false.");

    private static JsonObject Required(JsonObject? body) => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(JsonNode value, int status = StatusCodes.Status200OK) => Results.Json(value, EvalJson.Options, statusCode: status);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception error) when (Mapped(error) is { } result) { return result; }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (Mapped(error) is { } result) { return result; }
    }

    private static IResult? Mapped(Exception error) => error switch
    {
        KeyNotFoundException => Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, error.Message),
        ArgumentException => Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message),
        InvalidOperationException => Error(StatusCodes.Status409Conflict, SpendErrors.Conflict, error.Message),
        _ => null,
    };
}
