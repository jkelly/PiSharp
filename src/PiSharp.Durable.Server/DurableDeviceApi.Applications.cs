// PiSharp-only (decision 0010). The application routes: budgets and run usage, judgment policies, judgments, artifacts and evidence, feedback
// and preference profiles, sources, source checks and research, and the capability manifest. All are administrative (a trigger credential
// gets 403 from the group filter). Starting an execution with an Idempotency-Key happens once; a replay answers the first result with
// `Idempotent-Replay: true`.
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using Chord = PiSharp.Durable.Chord;

namespace PiSharp.Durable.Server;

public static partial class DurableDeviceApi
{
    /// <summary>The actor a device token decides as.</summary>
    internal const string DeviceAdmin = "device-admin";

    private static void MapApplications(RouteGroupBuilder v1, DurableDeviceHost host, DurableDeviceServerOptions options)
    {
        // ─── Capabilities ───────────────────────────────────────────────────
        v1.MapGet("/capabilities", () => Json(CapabilityManifest.Manifest.DeepClone()));

        // ─── Budgets ────────────────────────────────────────────────────────
        v1.MapGet("/budgets", () => Json(DeviceService.ToNode(host.Budgets.Today()
            .SetItem("reservations", Chord.JsonValue.Array(host.Budgets.Active().Select(item => (Chord.JsonValue)item.ToJson()))))));
        v1.MapPatch("/budgets", H(async http =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var expected = (long)(Integer(body, "expectedRevision", 0, 9_007_199_254_740_991) ?? throw new HostInvalidException("expectedRevision is required."));
            if (body["perDay"] is not JsonObject perDay) throw new HostInvalidException("perDay must be an object of meter limits.");
            var changes = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var (meter, value) in perDay)
                changes[meter] = value switch
                {
                    null => null,
                    JsonValue number when number.TryGetValue<double>(out var limit) && limit == Math.Floor(limit) && limit is >= 0 and <= 9_007_199_254_740_991 => (long)limit,
                    _ => throw new HostValidationException($"perDay.{meter} must be a non-negative integer or null.", [new FieldError("/perDay/" + meter, "must be a non-negative integer or null")]),
                };
            host.Budgets.SetLimits(changes, expected);
            return Json(DeviceService.ToNode(host.Budgets.Today()));
        }));
        v1.MapPost("/budgets/reservations/{id}/resolve", async (HttpContext http, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var outcome = String(body, "outcome") ?? throw new HostInvalidException("outcome is required: settled or released.");
            var actual = body["actual"] switch
            {
                null => null,
                JsonObject amounts => BudgetLedger.Amounts((Chord.JsonObjectValue)DeviceService.ToChord(amounts), "/actual"),
                _ => throw new HostInvalidException("actual must be an object of meter amounts."),
            };
            var resolved = host.Budgets.Resolve(id, outcome, actual, String(body, "detail"));
            return Json(DeviceService.ToNode(resolved.ToJson()));
        });
        // ─── Retention (M6) ──────────────────────────────────────────────────
        v1.MapGet("/retention", H(async http =>
            Json(DeviceService.ToNode((await host.Retention.RunAsync(dryRun: true, cancellationToken: http.RequestAborted).ConfigureAwait(false)).ToJson()))));
        v1.MapPost("/retention", H(async http =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var batch = (int)(Integer(body, "batch", 1, DeviceRetention.MaxBatch) ?? DeviceRetention.DefaultBatch);
            var report = await host.Retention.RunAsync(Bool(body, "dryRun") == true, batch, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.ToNode(report.ToJson()));
        }));
        v1.MapGet("/sessions/{s}/executions/{taskId}/usage", async (HttpContext http, string s, string taskId) =>
            Json(DeviceService.ToNode(await host.ResolveSession(s).ExecutionUsageAsync(Id(taskId, "taskId"), http.RequestAborted).ConfigureAwait(false))));

        // ─── Policies ───────────────────────────────────────────────────────
        v1.MapGet("/policies", () => Json(new JsonObject
        {
            ["items"] = new JsonArray([.. host.Policies.List().Select(item => DeviceService.ToNode(item.ToJson()))]),
            ["next"] = null,
        }));
        v1.MapGet("/policies/{id}", (string id) =>
        {
            var summary = host.Policies.Get(id) ?? throw new HostNotFoundException($"No policy {id}.");
            var json = DeviceService.ToNode(summary.ToJson())!.AsObject();
            json["versions"] = new JsonArray([.. host.Policies.Versions(id).Select(version => (JsonNode)new JsonObject
            {
                ["version"] = version.Version, ["hash"] = version.Hash, ["createdAt"] = version.CreatedAt,
            })]);
            json["active"] = summary.ActiveVersion is { } active ? DeviceService.ToNode(host.Policies.Version(id, active)!.ToJson()) : null;
            return Json(json);
        });
        v1.MapGet("/policies/{id}/versions/{version}", (string id, string version) =>
            Json(DeviceService.ToNode((host.Policies.Version(id, VersionOf(version)) ?? throw new HostNotFoundException($"No version {version} of policy {id}.")).ToJson())));
        v1.MapPost("/policies/{id}/versions", async (HttpContext http, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (published, replayed) = host.Policies.Publish(id, (Chord.JsonObjectValue)DeviceService.ToChord(body), IdempotencyKey(http));
            return Created(http, DeviceService.ToNode(published.ToJson()), replayed);
        });
        v1.MapPut("/policies/{id}/active", async (HttpContext http, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var version = (int)(Integer(body, "version", 1, 100_000) ?? throw new HostInvalidException("version is required."));
            var expected = (long)(Integer(body, "expectedRevision", 0, 9_007_199_254_740_991) ?? throw new HostInvalidException("expectedRevision is required."));
            return Json(DeviceService.ToNode(host.Policies.Activate(id, version, expected).ToJson()));
        });

        // ─── Judgments ──────────────────────────────────────────────────────
        v1.MapPost("/sessions/{s}/judgments", async (HttpContext http, string s) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var session = host.ResolveSession(s);
            var agent = await session.ResolveAgentAsync(body["agentId"] is JsonNode given ? Transcript.Describe(given) : "main", http.RequestAborted).ConfigureAwait(false);
            var request = new JsonObject
            {
                ["policyId"] = String(body, "policyId") ?? throw new HostValidationException("policyId is required.", [new FieldError("/policyId", "is required")]),
                ["input"] = body["input"]?.DeepClone() ?? new JsonObject(),
            };
            foreach (var key in new[] { "version", "title", "evidenceIds" })
                if (body[key] is JsonNode value) request[key] = value.DeepClone();
            var start = await session.StartExecutionAsync(agent.Id, "pisharp-judge", (Chord.JsonObjectValue)DeviceService.ToChord(request), Budget(body),
                IdempotencyKey(http), http.RequestAborted).ConfigureAwait(false);
            var accepted = Accepted(session, start);
            accepted["judgmentId"] = Judgments.IdOf(session.Id, start.TaskId.Value, "evaluate", 0, 0);
            return Execution(http, accepted, start.Replayed);
        });
        v1.MapGet("/sessions/{s}/judgments", (HttpContext http, string s) => RecordsAsync(host, http, s, "judgments"));
        v1.MapGet("/sessions/{s}/judgments/{id}", async (HttpContext http, string s, string id) =>
            Json(DeviceService.ToNode(await host.ResolveSession(s).JudgmentAsync(id, http.RequestAborted).ConfigureAwait(false))));
        v1.MapPost("/sessions/{s}/judgments/{id}/decision", async (HttpContext http, string s, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var choice = String(body, "choice") ?? throw new HostInvalidException("choice is required: approve or reject.");
            var reason = String(body, "reason");
            if (reason is { Length: > 1000 }) throw new HostInvalidException("reason must have at most 1000 characters.");
            var judgment = await host.ResolveSession(s).DecideJudgmentAsync(id, choice, DeviceAdmin, reason, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.ToNode(judgment));
        });

        // ─── Sources ────────────────────────────────────────────────────────
        v1.MapGet("/sources", () => Json(new JsonObject
        {
            ["items"] = new JsonArray([.. host.Sources.List().Select(item => DeviceService.ToNode(item.ToJson()))]),
            ["next"] = null,
        }));
        v1.MapPost("/sources", async (HttpContext http) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var (created, replayed) = host.Sources.Create((Chord.JsonObjectValue)DeviceService.ToChord(body), IdempotencyKey(http));
            return Created(http, DeviceService.ToNode(created.ToJson()), replayed);
        });
        v1.MapGet("/sources/{id}", (string id) => Json(DeviceService.ToNode(host.Sources.Detail(id))));
        v1.MapPatch("/sources/{id}", async (HttpContext http, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var expected = (long)(Integer(body, "expectedRevision", 0, 9_007_199_254_740_991) ?? throw new HostInvalidException("expectedRevision is required."));
            body.Remove("expectedRevision");
            return Json(DeviceService.ToNode(host.Sources.Update(id, (Chord.JsonObjectValue)DeviceService.ToChord(body), expected).ToJson()));
        });
        v1.MapDelete("/sources/{id}", (HttpContext http, string id) =>
        {
            var expected = http.Request.Query["expectedRevision"].ToString() is { Length: > 0 } given ? Id(given, "expectedRevision")
                : throw new HostInvalidException("expectedRevision is required.");
            host.Sources.Delete(id, expected);
            return Json(new { ok = true });
        });
        v1.MapGet("/sources/{id}/items", (HttpContext http, string id) =>
        {
            var query = http.Request.Query;
            var limit = query["limit"].ToString() is { Length: > 0 } count ? Id(count, "limit") : 50;
            if (limit is < 1 or > 200) throw new HostInvalidException("limit must be 1 to 200.");
            return Json(DeviceService.ToNode(host.Sources.Items(id, (int)limit, query["cursor"].ToString() is { Length: > 0 } cursor ? cursor : null)));
        });
        v1.MapGet("/retrieval/health", () => Json(new JsonObject
        {
            ["items"] = new JsonArray([.. host.Sources.Health().Select(item => DeviceService.ToNode(item))]),
            ["next"] = null,
        }));
        v1.MapPost("/sessions/{s}/source-checks", async (HttpContext http, string s) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var session = host.ResolveSession(s);
            var agent = await session.ResolveAgentAsync(body["agentId"] is JsonNode given ? Transcript.Describe(given) : "main", http.RequestAborted).ConfigureAwait(false);
            var request = new JsonObject();
            foreach (var key in new[] { "sourceIds", "policyId", "version", "reevaluate", "force", "maxJudgments" })
                if (body[key] is JsonNode value) request[key] = value.DeepClone();
            var start = await session.StartExecutionAsync(agent.Id, "pisharp-source-check", (Chord.JsonObjectValue)DeviceService.ToChord(request), Budget(body),
                IdempotencyKey(http), http.RequestAborted).ConfigureAwait(false);
            return Execution(http, Accepted(session, start), start.Replayed);
        });

        // ─── Research ───────────────────────────────────────────────────────
        v1.MapPost("/sessions/{s}/research", async (HttpContext http, string s) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var session = host.ResolveSession(s);
            var agent = await session.ResolveAgentAsync(body["agentId"] is JsonNode given ? Transcript.Describe(given) : "main", http.RequestAborted).ConfigureAwait(false);
            var request = new JsonObject();
            foreach (var key in new[] { "question", "angles", "sourceIds", "evidenceIds", "research", "synthesis", "concurrency", "requireAll", "maxAttempts", "retrieval",
                "angleDeadlineSeconds", "policyId", "version" })
                if (body[key] is JsonNode value) request[key] = value.DeepClone();
            var start = await session.StartExecutionAsync(agent.Id, "pisharp-research", (Chord.JsonObjectValue)DeviceService.ToChord(request), Budget(body),
                IdempotencyKey(http), http.RequestAborted).ConfigureAwait(false);
            return Execution(http, Accepted(session, start), start.Replayed);
        });

        // ─── Artifacts and evidence ─────────────────────────────────────────
        v1.MapGet("/sessions/{s}/executions/{taskId}/artifacts", async (HttpContext http, string s, string taskId) => Json(new JsonObject
        {
            ["items"] = DeviceService.ToNode(await host.ResolveSession(s).ExecutionArtifactsAsync(Id(taskId, "taskId"), http.RequestAborted).ConfigureAwait(false)),
        }));
        v1.MapGet("/sessions/{s}/artifacts", (HttpContext http, string s) => RecordsAsync(host, http, s, "artifacts"));
        v1.MapGet("/sessions/{s}/artifacts/{id}", async (HttpContext http, string s, string id) =>
            Json(DeviceService.ToNode(await host.ResolveSession(s).ArtifactAsync(id, http.RequestAborted).ConfigureAwait(false))));
        v1.MapGet("/sessions/{s}/artifacts/{id}/export", async (HttpContext http, string s, string id) =>
        {
            var session = host.ResolveSession(s);
            var artifact = await session.ArtifactAsync(id, http.RequestAborted).ConfigureAwait(false);
            var format = http.Request.Query["format"].ToString() is { Length: > 0 } given ? given : "json";
            if (format == "text")
                return Results.Text(Artifacts.ToText(artifact), "text/plain; charset=utf-8");
            if (format != "json") throw new HostInvalidException("format must be json or text.");
            var evidence = new List<Chord.JsonObjectValue>();
            foreach (var evidenceId in Jobs.Items(artifact, "evidenceIds").OfType<Chord.JsonStringValue>())
                try { evidence.Add(await session.EvidenceAsync(evidenceId.Value, http.RequestAborted).ConfigureAwait(false)); }
                catch (HostNotFoundException) { /* Evidence of the artifact is write-once; a missing one is left out of the export. */ }
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{id}.json\"";
            return Json(DeviceService.ToNode(Artifacts.Export(artifact, evidence)));
        });
        v1.MapPost("/sessions/{s}/artifacts/{id}/actions/{action}", async (HttpContext http, string s, string id, string action) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var key = IdempotencyKey(http) ?? throw new HostInvalidException("An artifact action needs an Idempotency-Key.");
            var session = host.ResolveSession(s);
            var start = await session.InvokeArtifactActionAsync(id, action, Bool(body, "confirm") == true, key, http.RequestAborted).ConfigureAwait(false);
            return Execution(http, Accepted(session, start), start.Replayed);
        });
        v1.MapGet("/sessions/{s}/evidence", (HttpContext http, string s) => RecordsAsync(host, http, s, "evidence"));
        v1.MapPost("/sessions/{s}/evidence", async (HttpContext http, string s) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var draft = new EvidenceDraft(String(body, "title") ?? throw new HostValidationException("title is required.", [new FieldError("/title", "is required")]),
                String(body, "url"), String(body, "text") ?? throw new HostValidationException("text is required.", [new FieldError("/text", "is required")]),
                String(body, "contentType") ?? "text/plain");
            var evidence = await host.ResolveSession(s).AddEvidenceAsync(draft, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.ToNode(evidence), 201);
        });
        v1.MapGet("/sessions/{s}/evidence/{id}", async (HttpContext http, string s, string id) =>
            Json(DeviceService.ToNode(await host.ResolveSession(s).EvidenceAsync(id, http.RequestAborted).ConfigureAwait(false))));
        v1.MapGet("/sessions/{s}/evidence/{id}/text", async (HttpContext http, string s, string id) =>
            Results.Text(await host.ResolveSession(s).EvidenceTextAsync(id, http.RequestAborted).ConfigureAwait(false), "text/plain; charset=utf-8"));

        // ─── Feedback and preferences ───────────────────────────────────────
        v1.MapPost("/sessions/{s}/artifacts/{id}/feedback", async (HttpContext http, string s, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var requestId = String(body, "requestId") ?? IdempotencyKey(http)
                ?? throw new HostValidationException("Feedback needs a requestId or an Idempotency-Key.", [new FieldError("/requestId", "is required")]);
            var labels = body["labels"] switch
            {
                null => null,
                JsonArray items => items.Select(item => item is JsonValue value && value.TryGetValue<string>(out var label) ? label
                    : throw new HostValidationException("labels must be strings.", [new FieldError("/labels", "must be an array of strings")])).ToList(),
                _ => throw new HostValidationException("labels must be an array.", [new FieldError("/labels", "must be an array of strings")]),
            };
            var (feedback, profile, replayed) = await host.ResolveSession(s).RecordFeedbackAsync(id, String(body, "profileId"), requestId,
                String(body, "action") ?? throw new HostValidationException("action is required.", [new FieldError("/action", "is required")]),
                labels, String(body, "note"), Bool(body, "example") == true, String(body, "reverses"), DeviceAdmin, http.RequestAborted).ConfigureAwait(false);
            return Created(http, new JsonObject { ["event"] = DeviceService.ToNode(feedback), ["profile"] = DeviceService.ToNode(profile) }, replayed);
        });
        v1.MapGet("/sessions/{s}/artifacts/{id}/feedback", async (HttpContext http, string s, string id) =>
        {
            var profileId = http.Request.Query["profileId"].ToString() is { Length: > 0 } given ? given : null;
            return Json(DeviceService.ToNode(await host.ResolveSession(s).FeedbackAsync(id, profileId, http.RequestAborted).ConfigureAwait(false)));
        });
        v1.MapGet("/preferences", () => Json(new JsonObject
        {
            ["items"] = new JsonArray([.. host.Preferences.List().Select(item => DeviceService.ToNode(item))]),
            ["next"] = null,
        }));
        v1.MapGet("/preferences/{id}", (string id) => Json(DeviceService.ToNode(host.Preferences.Get(id))));
        v1.MapPatch("/preferences/{id}", async (HttpContext http, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var expected = (long)(Integer(body, "expectedRevision", 0, 9_007_199_254_740_991) ?? throw new HostInvalidException("expectedRevision is required."));
            var settings = body["settings"] switch
            {
                null => null,
                JsonObject given => (Chord.JsonObjectValue)DeviceService.ToChord(given),
                _ => throw new HostValidationException("settings must be an object.", [new FieldError("/settings", "must be an object")]),
            };
            var forget = body["forgetExamples"] switch
            {
                null => [],
                JsonArray items => items.Select(item => item is JsonValue value && value.TryGetValue<string>(out var eventId) ? eventId
                    : throw new HostValidationException("forgetExamples must be event IDs.", [new FieldError("/forgetExamples", "must be an array of event IDs")])).ToList(),
                _ => throw new HostValidationException("forgetExamples must be an array.", [new FieldError("/forgetExamples", "must be an array of event IDs")]),
            };
            return Json(DeviceService.ToNode(host.Preferences.Update(id, settings, forget, expected, DeviceAdmin)));
        });
        v1.MapPost("/preferences/{id}/reset", async (HttpContext http, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var expected = (long)(Integer(body, "expectedRevision", 0, 9_007_199_254_740_991) ?? throw new HostInvalidException("expectedRevision is required."));
            return Json(DeviceService.ToNode(host.Preferences.Reset(id, expected, DeviceAdmin)));
        });
        v1.MapGet("/preferences/{id}/export", (HttpContext http, string id) =>
        {
            var exported = host.Preferences.Export(id);
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"preferences-{id}.json\"";
            return Json(DeviceService.ToNode(exported));
        });
        v1.MapDelete("/preferences/{id}", (HttpContext http, string id) =>
        {
            var expected = http.Request.Query["expectedRevision"].ToString() is { Length: > 0 } given ? Id(given, "expectedRevision")
                : throw new HostInvalidException("expectedRevision is required.");
            return Json(new JsonObject { ["ok"] = true, ["deleted"] = host.Preferences.Delete(id, expected) });
        });
    }

    private static async Task<IResult> RecordsAsync(DurableDeviceHost host, HttpContext http, string s, string kind)
    {
        var query = http.Request.Query;
        var limit = query["limit"].ToString() is { Length: > 0 } count ? Id(count, "limit") : 20;
        if (limit is < 1 or > 100) throw new HostInvalidException("limit must be 1 to 100.");
        var cursor = query["cursor"].ToString() is { Length: > 0 } given ? given : null;
        return Json(DeviceService.ToNode(await host.ResolveSession(s).RecordsAsync(kind, (int)limit, cursor, http.RequestAborted).ConfigureAwait(false)));
    }

    private static Chord.JsonObjectValue? Budget(JsonObject body) => body["budget"] switch
    {
        null => null,
        JsonObject budget => (Chord.JsonObjectValue)DeviceService.ToChord(budget),
        _ => throw new HostInvalidException("budget must be an object of meter limits."),
    };

    private static JsonObject Accepted(HostedSession session, ExecutionStart start) => new()
    {
        ["sessionId"] = session.Id,
        ["agentId"] = start.AgentId,
        ["taskId"] = start.TaskId.Value,
        ["executionUrl"] = $"/v1/sessions/{session.Id}/executions/{start.TaskId.Value.ToString(CultureInfo.InvariantCulture)}",
    };

    private static IResult Execution(HttpContext http, JsonObject accepted, bool replayed)
    {
        if (replayed) http.Response.Headers["Idempotent-Replay"] = "true";
        return Json(accepted, 202);
    }

    private static IResult Created(HttpContext http, JsonNode? created, bool replayed)
    {
        if (replayed) http.Response.Headers["Idempotent-Replay"] = "true";
        return Json(created, 201);
    }

    private static int VersionOf(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version is >= 1 and <= 100_000
            ? version : throw new HostInvalidException("version must be an integer from 1 to 100000.");
}
