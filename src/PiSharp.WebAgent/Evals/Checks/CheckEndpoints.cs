// Decision 0011 (run W5, E2): the check editor's JSON API under /api/evals/checks (mockup modal-check-editor: the line under each pane that
// says what the check makes of the reference output, and the judge pane's family chip and cost line). Writes are POSTs with JSON bodies
// (HostGuard's cross-origin and JSON-only rules cover them); nothing here changes anything or calls a model:
//   POST /api/evals/checks/validate       {check} -> {check (normalized, as saved), label, editorLabel, summary}, or 400 {error: {code:
//                                         "invalid", message}} saying what is wrong (a judge's rubric is checked as a judgment policy).
//   POST /api/evals/checks/try            {check, caseId? | runId? | reference?} -> {subject: "reference"|"answer", result: CheckResult|null,
//                                         note?}: the check on a case's reference output, a recorded run's answer, or a reference sent as
//                                         JSON. A judge check is never tried (result null, with a note): it runs only in suite runs. 404
//                                         {error: {code: "not_found"}} for an unknown case or run.
//   POST /api/evals/checks/judge-preview  {provider, model, account?, suiteId? | caseId? | targets?: [{provider, model}], judgements?} ->
//                                         {family: {same, chip, family, targets, message}, estimate: {billing, inputTokens, outputTokens,
//                                         inputRate, outputRate, eachMicros, judgements, runMicros, text}}: the same-family warning against
//                                         the suite's targets and the judge's worst case for the case's judgements per run (targets × k
//                                         unless given).
// Every answer is Cache-Control: no-store and written with SettingsStore.JsonOptions (EvalJson.Options). Cases and suites are read from
// webagent.db's eval_cases and eval_suites (the contracts' tables), read only.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Checks;

/// <summary>Maps <c>/api/evals/checks</c>.</summary>
public static class CheckEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/evals/checks";

    /// <summary>Maps the endpoints.</summary>
    public static IEndpointRouteBuilder MapCheckEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapPost("/validate", (JsonObject? body, CheckRunner runner) => Handle(() =>
        {
            var check = runner.Validate(CheckOf(Required(body)));
            return Json(new JsonObject
            {
                ["check"] = EvalJson.ToNode(check),
                ["label"] = check.Label,
                ["editorLabel"] = Check.EditorLabel(check.Kind),
                ["summary"] = CheckText.Summary(check),
            });
        }));
        api.MapPost("/try", (JsonObject? body, CheckRunner runner, WebAgentDb db, RunStore runs) => Handle(() => Try(Required(body), runner, db, runs)));
        api.MapPost("/judge-preview", (JsonObject? body, RubricJudge judge, RunEngine engine, WebAgentDb db, CancellationToken token) =>
            HandleAsync(() => PreviewAsync(Required(body), judge, engine, db, token)));
        return routes;
    }

    private static IResult Try(JsonObject body, CheckRunner runner, WebAgentDb db, RunStore runs)
    {
        var check = runner.Validate(CheckOf(body));
        EvalAnswer? answer = null;
        var subject = CheckSubject.Reference;
        string? note = null;
        if (Text(body, "runId") is { } runId)
        {
            var run = runs.Get(runId) ?? throw new KeyNotFoundException($"There is no run {runId}.");
            if (run.Status == RunStatus.Running) note = "This run hasn't finished yet.";
            else answer = EvalAnswer.FromRun(run);
            subject = CheckSubject.Answer;
        }
        else if (Text(body, "caseId") is { } caseId)
        {
            var testCase = CheckRecords.Case(db, caseId) ?? throw new KeyNotFoundException($"There is no test case {caseId}.");
            if (testCase.Reference is { } reference) answer = reference.ToAnswer();
            else note = "This case has no reference output to try the check on.";
        }
        else if (body["reference"] is JsonObject reference)
        {
            answer = Deserialize<TestCaseReference>(reference, "reference").Normalize().ToAnswer();
        }
        else throw new ArgumentException("Say what to try the check on: caseId, runId or reference.");
        CheckResult? result = null;
        if (check.Kind == CheckKind.Judge)
            note = "An LLM judge runs only in suite runs, where each judgement is a metered call; it is not tried here.";
        else if (answer is not null)
            result = CheckRunner.Evaluate(check, answer, subject);
        var json = new JsonObject
        {
            ["subject"] = subject == CheckSubject.Reference ? "reference" : "answer",
            ["result"] = result is null ? null : EvalJson.ToNode(result),
        };
        if (note is not null) json["note"] = note;
        return Json(json);
    }

    private static async Task<IResult> PreviewAsync(JsonObject body, RubricJudge judge, RunEngine engine, WebAgentDb db, CancellationToken token)
    {
        var provider = Text(body, "provider") ?? throw new ArgumentException("Choose the judge's provider.");
        var model = Text(body, "model") ?? throw new ArgumentException("Choose the judge's model.");
        if (engine.Capabilities(provider, model) is null) throw new KeyNotFoundException($"There is no model {provider}/{model}.");
        Suite? suite = null;
        if (Text(body, "suiteId") is { } suiteId) suite = CheckRecords.Suite(db, suiteId) ?? throw new KeyNotFoundException($"There is no suite {suiteId}.");
        else if (Text(body, "caseId") is { } caseId)
        {
            var testCase = CheckRecords.Case(db, caseId) ?? throw new KeyNotFoundException($"There is no test case {caseId}.");
            suite = CheckRecords.Suite(db, testCase.SuiteId);
        }
        List<ModelIdentity> targets;
        if (body["targets"] is JsonArray given)
        {
            targets = [];
            foreach (var item in given)
            {
                if (item is not JsonObject target || Text(target, "provider") is not { } targetProvider || Text(target, "model") is not { } targetModel)
                    throw new ArgumentException("Each target is {provider, model}.");
                targets.Add(new ModelIdentity(targetProvider, targetModel).Normalize());
            }
        }
        else targets = [.. (suite?.Targets ?? []).Select(target => target.Identity)];
        int judgements;
        if (body["judgements"] is JsonValue count)
        {
            if (!count.TryGetValue<int>(out judgements) || judgements is < 0 or > 100_000) throw new ArgumentException("judgements is a whole number from 0.");
        }
        else judgements = (suite?.Targets.Count ?? targets.Count) * Math.Max(1, suite?.K ?? 1);
        var spec = new JudgeSpec(provider, model, "Preview", Account: Text(body, "account"));
        var family = RubricJudge.Family(spec, targets);
        var estimate = await judge.EstimateAsync(spec, judgements, token).ConfigureAwait(false);
        return Json(new JsonObject
        {
            ["family"] = new JsonObject
            {
                ["same"] = family.Same,
                ["chip"] = family.Chip,
                ["family"] = family.Family,
                ["targets"] = new JsonArray([.. family.Targets.Select(name => (JsonNode)JsonValue.Create(name)!)]),
                ["message"] = family.Message,
            },
            ["estimate"] = new JsonObject
            {
                ["billing"] = JsonSerializer.SerializeToNode(estimate.Billing, EvalJson.Options),
                ["inputTokens"] = estimate.InputTokens,
                ["outputTokens"] = estimate.OutputTokens,
                ["inputRate"] = estimate.InputRate,
                ["outputRate"] = estimate.OutputRate,
                ["eachMicros"] = estimate.EachMicros,
                ["judgements"] = estimate.Judgements,
                ["runMicros"] = estimate.RunMicros,
                ["text"] = estimate.Text,
            },
        });
    }

    private static Check CheckOf(JsonObject body) =>
        body["check"] is JsonObject check ? Deserialize<Check>(check, "check") : throw new ArgumentException("Send the check as {check: {kind, ...}}.");

    private static T Deserialize<T>(JsonObject json, string what)
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

    private static JsonObject Required(JsonObject? body) => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, EvalJson.Options, statusCode: status);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static IResult Handle(Func<IResult> action)
    {
        try
        {
            return action();
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

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
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

/// <summary>Test cases and suites as the check editor reads them: webagent.db's <c>eval_cases</c> and <c>eval_suites</c> rows (their
/// <c>data</c> JSON, the contract records), read only. The Suites unit owns writing them.</summary>
internal static class CheckRecords
{
    /// <summary>The test case <paramref name="id"/>, or null when there is none (or its row cannot be read).</summary>
    public static TestCase? Case(WebAgentDb db, string id) =>
        db.Query("SELECT data FROM eval_cases WHERE id = $id;", reader => reader.GetString(0), ("$id", id))
            .Select(EvalJson.TryDeserialize<TestCase>).FirstOrDefault(found => found is not null);

    /// <summary>The suite <paramref name="id"/>, or null when there is none (or its row cannot be read).</summary>
    public static Suite? Suite(WebAgentDb db, string id) =>
        db.Query("SELECT data FROM eval_suites WHERE id = $id;", reader => reader.GetString(0), ("$id", id))
            .Select(EvalJson.TryDeserialize<Suite>).FirstOrDefault(found => found is not null);
}
