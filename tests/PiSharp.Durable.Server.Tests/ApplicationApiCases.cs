using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Chord = PiSharp.Durable.Chord;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;

// Decision 0010, M2–M5: the application routes (budgets, policies, judgments, artifacts, evidence, sources, research, feedback, preferences) and the
// capability manifest.
internal static partial class Program
{
    private const string RelevanceSpec = """
        {"title":"Relevance","kind":"classifier","model":{"provider":"faux","id":"judge"},"instructions":"Is the release relevant?",
         "inputSchema":{"type":"object","additionalProperties":false,"required":["title","confidence"],"properties":{
           "title":{"type":"string","maxLength":200},"confidence":{"type":"number","minimum":0,"maximum":1}}},
         "labels":[{"id":"relevant","description":"It matters"},{"id":"ignore","description":"It does not"}]}
        """;

    /// <summary>Faux models whose classifier labels every item <c>relevant</c> with the item's own confidence.</summary>
    private static FauxDurableModels Judging() => new()
    {
        Classify = (context, _) =>
        {
            var confidence = ((Chord.JsonObjectValue)((Chord.JsonObjectValue)context["state"])["input"])["confidence"];
            return Task.FromResult(Chord.JsonValue.Object(
            [
                new("label", Chord.JsonValue.Object([new("type", "choice"), new("choice", "relevant"),
                    new("probabilities", Chord.JsonValue.Object([new("relevant", confidence), new("ignore", 1 - ((Chord.JsonNumberValue)confidence).Value)])),
                    new("confidence", confidence)])),
            ]));
        },
    };

    /// <summary>A raw request with the device token: status, body and the <c>Idempotent-Replay</c> header.</summary>
    private static async Task<(int Status, JsonNode? Body, bool Replayed)> SendRawAsync(TestServer server, HttpMethod method, string path, JsonNode? body,
        string? key = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(server.Base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? server.Token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await server.Raw.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = text.Length == 0 ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { /* Not JSON. */ }
        return ((int)response.StatusCode, json, response.Headers.TryGetValues("Idempotent-Replay", out var values) && values.Contains("true"));
    }

    private static async Task<JsonNode> WaitForJudgmentAsync(TestServer server, string session, string id, Func<JsonNode, bool> condition, string what)
    {
        JsonNode? judgment = null;
        await WaitUntilAsync(async () =>
        {
            try { judgment = await server.Client.JudgmentAsync(session, id); }
            catch (DurableApiException missing) when (missing.Code == DurableErrorCodes.NotFound) { return false; }
            return condition(judgment!);
        }, what);
        return judgment!;
    }

    private static async Task<JsonNode> WaitForExecutionAsync(TestServer server, string session, long taskId, string status)
    {
        JsonNode? execution = null;
        await WaitUntilAsync(async () =>
        {
            execution = await server.Client.ExecutionAsync(session, taskId);
            return execution?["status"]?.GetValue<string>() == status;
        }, $"execution {taskId} {status}");
        return execution!;
    }

    internal static IEnumerable<(string, Func<Task>)> ApplicationApiCases() =>
    [
        Case("application-api.manifest-matches-routes-client-and-device-features", async () =>
        {
            await using var server = await TestServer.StartAsync("manifest");
            var device = await server.Client.DeviceAsync();
            foreach (var feature in new[] { "budgets", "policies", "judgments", "artifacts", "evidence", "capabilities", "sources", "research", "feedback", "preferences", "retention" })
                Check(device.Supports(feature), "device advertises " + feature);
            Check(!device.Supports("judgments", 2), "a later version is not claimed");
            var manifest = (await server.Client.CapabilitiesAsync())!;
            var features = manifest["features"]!.AsArray().OfType<JsonObject>().ToList();
            Equal(device.Features!.Count, features.Count, "device features come from the manifest");

            var endpoints = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)server.App).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(method => method + " " + endpoint.RoutePattern.RawText))
                .ToHashSet(StringComparer.Ordinal);
            var listed = features.SelectMany(feature => feature["routes"]!.AsArray().Select(route => route!.GetValue<string>())).ToList();
            foreach (var route in listed) Check(endpoints.Contains(route), "manifest route is mapped: " + route);
            string[] applicationPaths = ["/v1/budgets", "/v1/policies", "/v1/capabilities", "/judgments", "/artifacts", "/evidence", "/usage", "/v1/sources",
                "/v1/retrieval", "/source-checks", "/research", "/feedback", "/v1/preferences", "/v1/retention"];
            foreach (var endpoint in endpoints.Where(route => applicationPaths.Any(path => route.Contains(path, StringComparison.Ordinal))))
                Check(listed.Contains(endpoint), "mapped application route is in the manifest: " + endpoint);

            var methods = typeof(DurableDeviceClient).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var feature in features)
            {
                var id = feature["id"]!.GetValue<string>();
                foreach (var method in feature["client"]!.AsArray()) Check(methods.Contains(method!.GetValue<string>()), $"{id}: client method {method}");
                var unsupported = feature["unsupported"] as JsonObject ?? [];
                foreach (var binding in new[] { "routes", "client", "cli", "web" })
                    Check(feature[binding]!.AsArray().Count > 0 || unsupported[binding] is JsonValue, $"{id}: {binding} is bound or says why not");
            }
        }),
        Case("application-api.retention-reports-a-dry-run-and-purges-in-bounded-batches", async () =>
        {
            await using var server = await TestServer.StartAsync("retention-api");
            var dry = (await server.Client.RetentionAsync())!;
            Equal(true, dry["dryRun"]!.GetValue<bool>(), "GET is a dry run");
            var categories = dry["categories"]!.AsArray().Select(category => category!["id"]!.GetValue<string>()).ToList();
            Equal("budget-reservations,judgment-cache,fetch-receipts,source-item-bodies,retrieval-cache", string.Join(",", categories), "every category");
            Check(dry["alwaysKept"]!.AsArray().Count >= 5, "says what it always keeps");
            var purged = (await server.Client.PurgeRetentionAsync(10))!;
            Equal(false, purged["dryRun"]!.GetValue<bool>(), "POST purges");
            Equal(false, purged["more"]!.GetValue<bool>(), "nothing more on a new device");
            var (status, body, _) = await SendRawAsync(server, HttpMethod.Post, "/v1/retention", new JsonObject { ["batch"] = 0 });
            Equal(400, status, "a batch of 0");
            Equal("invalid", body!["error"]!["code"]!.GetValue<string>(), "invalid");
            Equal(400, (await SendRawAsync(server, HttpMethod.Post, "/v1/retention", new JsonObject { ["batch"] = DeviceRetention.MaxBatch + 1 })).Status, "a batch over the maximum");
        }),
        Case("application-api.policies-publish-replay-activate-and-field-errors", async () =>
        {
            await using var server = await TestServer.StartAsync("policies-api", models: Judging());
            var spec = JsonNode.Parse(RelevanceSpec)!.AsObject();
            var invalid = spec.DeepClone().AsObject();
            invalid["labels"] = new JsonArray();
            invalid["surprise"] = true;
            var (status, body, _) = await SendRawAsync(server, HttpMethod.Post, "/v1/policies/relevance/versions", invalid);
            Equal(400, status, "an invalid spec");
            Equal("invalid", body!["error"]!["code"]!.GetValue<string>(), "invalid code");
            var fields = body["error"]!["fields"]!.AsArray().Select(field => field!["path"]!.GetValue<string>()).ToList();
            Check(fields.Contains("/labels") && fields.Contains("/surprise"), "field errors name the paths: " + string.Join(", ", fields));
            var failure = await FailsAsync(() => server.Client.PublishPolicyAsync("relevance", invalid), "invalid", "the client reads field errors");
            Check(failure.Fields.Count >= 2, "the client exposes field errors");

            var first = await SendRawAsync(server, HttpMethod.Post, "/v1/policies/relevance/versions", spec, "publish-1");
            Equal(201, first.Status, "published");
            Check(!first.Replayed, "a first publication is not a replay");
            var replay = await SendRawAsync(server, HttpMethod.Post, "/v1/policies/relevance/versions", spec, "publish-1");
            Check(replay.Replayed && replay.Body!["version"]!.GetValue<int>() == 1, "the same key and body replays the first result");
            var changed = spec.DeepClone().AsObject();
            changed["instructions"] = "Is the release relevant to us?";
            Equal(409, (await SendRawAsync(server, HttpMethod.Post, "/v1/policies/relevance/versions", changed, "publish-1")).Status, "the same key with another body");
            Equal(2, (await server.Client.PublishPolicyAsync("relevance", changed))!["version"]!.GetValue<int>(), "a second version");

            var detail = (await server.Client.PolicyAsync("relevance"))!;
            Equal(1, detail["activeVersion"]!.GetValue<int>(), "the first version is active");
            Equal(2, detail["versions"]!.AsArray().Count, "both versions are listed");
            Equal("Is the release relevant?", detail["active"]!["spec"]!["instructions"]!.GetValue<string>(), "the active spec");
            Equal(2, (await server.Client.PolicyVersionAsync("relevance", 2))!["version"]!.GetValue<int>(), "a version by number");
            var revision = detail["revision"]!.GetValue<long>();
            await FailsAsync(() => server.Client.ActivatePolicyAsync("relevance", 2, revision + 5), "conflict", "a stale revision");
            Equal(2, (await server.Client.ActivatePolicyAsync("relevance", 2, revision))!["activeVersion"]!.GetValue<int>(), "activated");
            Equal(1, (await server.Client.PoliciesAsync())!["items"]!.AsArray().Count, "one policy");
            await FailsAsync(() => server.Client.PolicyVersionAsync("relevance", 9), "not_found", "a missing version");
            await FailsAsync(() => server.Client.PolicyAsync("missing"), "not_found", "a missing policy");
        }),
        Case("application-api.judgment-review-decision-artifacts-evidence-and-usage", async () =>
        {
            await using var server = await TestServer.StartAsync("judgment-api", models: Judging());
            await server.Client.PublishPolicyAsync("relevance", JsonNode.Parse(RelevanceSpec)!.AsObject());
            var session = await server.CreateSessionAsync("judging");
            var evidence = (await server.Client.AddEvidenceAsync(session, new JsonObject
            {
                ["title"] = "Release notes", ["url"] = "https://example.com/releases/2.0", ["text"] = "Version 2.0 changes the API.",
            }))!;
            var evidenceId = evidence["id"]!.GetValue<string>();
            Equal(evidenceId, (await server.Client.AddEvidenceAsync(session, new JsonObject
            {
                ["title"] = "Release notes", ["url"] = "https://example.com/releases/2.0", ["text"] = "Version 2.0 changes the API.",
            }))!["id"]!.GetValue<string>(), "the same URL and text is one record");
            Equal("Version 2.0 changes the API.", await server.Client.EvidenceTextAsync(session, evidenceId), "evidence text");

            var request = new JsonObject
            {
                ["policyId"] = "relevance",
                ["title"] = "v2.0",
                ["input"] = new JsonObject { ["title"] = "v2.0", ["confidence"] = 0.6 },
                ["evidenceIds"] = new JsonArray(evidenceId),
            };
            var bad = request.DeepClone().AsObject();
            bad["input"] = new JsonObject { ["title"] = "v2.0" };
            var rejected = await FailsAsync(() => server.Client.StartJudgmentAsync(session, bad), "invalid", "input checked against the policy");
            Check(rejected.Fields.Any(field => field.Path == "/input/confidence"), "the missing field is named");
            var accepted = await SendRawAsync(server, HttpMethod.Post, $"/v1/sessions/{session}/judgments", request, "judge-1");
            Equal(202, accepted.Status, "accepted");
            var taskId = accepted.Body!["taskId"]!.GetValue<long>();
            var judgmentId = accepted.Body["judgmentId"]!.GetValue<string>();
            Equal($"/v1/sessions/{session}/executions/{taskId}", accepted.Body["executionUrl"]!.GetValue<string>(), "execution URL");
            var again = await SendRawAsync(server, HttpMethod.Post, $"/v1/sessions/{session}/judgments", request, "judge-1");
            Check(again.Replayed && again.Body!["taskId"]!.GetValue<long>() == taskId, "a retry with the key returns the first execution");

            var pending = await WaitForJudgmentAsync(server, session, judgmentId, judgment => judgment["decision"]?.GetValue<string>() == "pending", "review is pending");
            Equal("confirm", pending["route"]!.GetValue<string>(), "medium confidence asks for confirmation");
            await FailsAsync(() => server.Client.DecideJudgmentAsync(session, judgmentId, "maybe"), "invalid", "an unknown choice");
            var decided = (await server.Client.DecideJudgmentAsync(session, judgmentId, "approve", "Looks right"))!;
            Equal("approved", decided["decision"]!.GetValue<string>(), "approved");
            var last = decided["decisions"]!.AsArray().Last()!;
            Equal("device-admin", last["actor"]!.GetValue<string>(), "the actor is the credential, not the body");
            Equal("Looks right", last["reason"]!.GetValue<string>(), "the reason is kept");
            Equal("approved", (await server.Client.DecideJudgmentAsync(session, judgmentId, "approve"))!["decision"]!.GetValue<string>(), "the same choice is a no-op");
            await FailsAsync(() => server.Client.DecideJudgmentAsync(session, judgmentId, "reject"), "conflict", "the opposite choice conflicts");
            await WaitForExecutionAsync(server, session, taskId, "completed");

            var artifacts = (await server.Client.ExecutionArtifactsAsync(session, taskId))!["items"]!.AsArray();
            Equal(1, artifacts.Count, "one finding");
            var artifactId = artifacts[0]!["id"]!.GetValue<string>();
            Equal("finding", (await server.Client.ArtifactAsync(session, artifactId))!["kind"]!.GetValue<string>(), "a finding");
            Equal(artifactId, (await server.Client.ArtifactsAsync(session, 1))!["items"]![0]!["id"]!.GetValue<string>(), "the session's artifacts");
            var export = (await server.Client.ExportArtifactAsync(session, artifactId))!;
            Equal(evidenceId, export["evidence"]![0]!["id"]!.GetValue<string>(), "the export carries the evidence");
            Check((await server.Client.ArtifactTextAsync(session, artifactId)).Contains("relevant", StringComparison.Ordinal), "the text fallback");
            Equal(judgmentId, (await server.Client.JudgmentsAsync(session))!["items"]![0]!["id"]!.GetValue<string>(), "the session's judgments");
            Equal(evidenceId, (await server.Client.EvidenceListAsync(session))!["items"]![0]!["id"]!.GetValue<string>(), "the session's evidence");
            await FailsAsync(() => server.Client.InvokeArtifactActionAsync(session, artifactId, "reevaluate", "act-1"), "invalid", "a finding offers no action");

            var usage = (await server.Client.ExecutionUsageAsync(session, taskId))!;
            Equal(1, usage["reservations"]!.AsArray().Count, "one reservation");
            Equal("settled", usage["reservations"]![0]!["status"]!.GetValue<string>(), "settled");
            Check(usage["recorded"] is JsonObject, "the recorded model usage");
            var budgets = (await server.Client.BudgetsAsync())!;
            Check(budgets["used"]!["requests"]!.GetValue<long>() >= 1, "the day's use counts the call");
            await FailsAsync(() => server.Client.UpdateBudgetsAsync(new JsonObject { ["requests"] = 5 }, 99), "conflict", "a stale budget revision");
            var limits = (await server.Client.UpdateBudgetsAsync(new JsonObject { ["requests"] = 5 }, budgets["limits"]!["revision"]!.GetValue<long>()))!;
            Equal(5L, limits["limits"]!["perDay"]!["requests"]!.GetValue<long>(), "a daily limit");
            await FailsAsync(() => server.Client.UpdateBudgetsAsync(new JsonObject { ["bananas"] = 5 }, limits["limits"]!["revision"]!.GetValue<long>()), "invalid", "an unknown meter");
            await FailsAsync(() => server.Client.ResolveReservationAsync(usage["reservations"]![0]!["operationId"]!.GetValue<string>(), "released"), "conflict",
                "only an uncertain reservation is reconciled");
        }),
        Case("application-api.sources-crud-check-items-and-health", async () =>
        {
            var feedBody = """
                <?xml version="1.0"?><rss version="2.0"><channel><title>Fixture</title>
                <item><guid isPermaLink="false">r1</guid><title>Release 1</title><link>https://example.com/r1</link><description>First.</description></item>
                <item><guid isPermaLink="false">r2</guid><title>Release 2</title><link>https://example.com/r2</link><description>Second.</description></item>
                </channel></rss>
                """;
            await using var fixture = new FixtureServer(request => request.Path.StartsWith("/down", StringComparison.Ordinal)
                ? FixtureResponse.Code(503, new Dictionary<string, string> { ["Retry-After"] = "120" })
                : FixtureResponse.Text(feedBody));
            await using var server = await TestServer.StartAsync("sources-api", retrieval: new RetrievalOptions { AllowedPrivateHosts = ["127.0.0.1"] });
            var spec = new JsonObject { ["id"] = "releases", ["kind"] = "feed", ["url"] = fixture.Url("feed.xml") };

            var invalid = await SendRawAsync(server, HttpMethod.Post, "/v1/sources", new JsonObject { ["id"] = "releases", ["kind"] = "feed", ["surprise"] = 1 });
            Equal(400, invalid.Status, "an invalid source");
            Check(invalid.Body!["error"]!["fields"]!.AsArray().Any(field => field!["path"]!.GetValue<string>() == "/surprise"), "the unknown field is named");
            var created = await SendRawAsync(server, HttpMethod.Post, "/v1/sources", spec, "source-1");
            Equal(201, created.Status, "created");
            Equal(1L, created.Body!["revision"]!.GetValue<long>(), "revision 1");
            Equal("new", created.Body["state"]!["status"]!.GetValue<string>(), "never checked");
            Check((await SendRawAsync(server, HttpMethod.Post, "/v1/sources", spec, "source-1")).Replayed, "the same key replays");
            await FailsAsync(() => server.Client.CreateSourceAsync(spec), "conflict", "a second source with the id");
            Equal(1, (await server.Client.SourcesAsync())!["items"]!.AsArray().Count, "one source");

            var stale = await SendRawAsync(server, HttpMethod.Patch, "/v1/sources/releases", new JsonObject { ["title"] = "Releases", ["expectedRevision"] = 7 });
            Equal(409, stale.Status, "a stale revision");
            var updated = (await server.Client.UpdateSourceAsync("releases", new JsonObject { ["title"] = "Releases" }, 1))!;
            Equal(2L, updated["revision"]!.GetValue<long>(), "revision 2");
            Equal("Releases", updated["title"]!.GetValue<string>(), "the title changed");
            await FailsAsync(() => server.Client.UpdateSourceAsync("releases", new JsonObject { ["kind"] = "page" }, 2), "invalid", "the kind is fixed");

            var session = await server.CreateSessionAsync("sources");
            var request = new JsonObject { ["sourceIds"] = new JsonArray("releases") };
            var accepted = await SendRawAsync(server, HttpMethod.Post, $"/v1/sessions/{session}/source-checks", request, "check-1");
            Equal(202, accepted.Status, "accepted");
            var taskId = accepted.Body!["taskId"]!.GetValue<long>();
            Check((await SendRawAsync(server, HttpMethod.Post, $"/v1/sessions/{session}/source-checks", request, "check-1")).Replayed, "a retried start replays");
            await WaitForExecutionAsync(server, session, taskId, "completed");
            await FailsAsync(() => server.Client.StartSourceCheckAsync(session, new JsonObject { ["sourceIds"] = new JsonArray("missing") }), "invalid",
                "an unknown source");

            var detail = (await server.Client.SourceAsync("releases"))!;
            Equal("ok", detail["state"]!["status"]!.GetValue<string>(), "checked");
            Equal(2, detail["state"]!["items"]!.GetValue<int>(), "two items");
            Equal($"http://127.0.0.1:{fixture.Port}", detail["origin"]!.GetValue<string>(), "the origin");
            Equal("fetched", detail["receipts"]![0]!["status"]!.GetValue<string>(), "a fetch receipt");
            var items = (await server.Client.SourceItemsAsync("releases", 1))!;
            Equal(1, items["items"]!.AsArray().Count, "a page of one");
            var rest = (await server.Client.SourceItemsAsync("releases", 1, items["next"]!.GetValue<string>()))!;
            Check(rest["items"]![0]!["itemId"]!.GetValue<string>() != items["items"]![0]!["itemId"]!.GetValue<string>(), "the next page");
            Check(rest["next"] is null, "the last page");
            var artifacts = (await server.Client.ExecutionArtifactsAsync(session, taskId))!["items"]!.AsArray();
            Equal("evidence-list", artifacts[0]!["kind"]!.GetValue<string>(), "the check lists the new items");

            await server.Client.CreateSourceAsync(new JsonObject { ["id"] = "down", ["kind"] = "page", ["url"] = fixture.Url("down") });
            await server.Client.StartSourceCheckAsync(session, new JsonObject { ["sourceIds"] = new JsonArray("down") });
            await WaitUntilAsync(async () => (await server.Client.RetrievalHealthAsync())!["items"]!.AsArray().Count > 0, "the origin is cooling down");
            var health = (await server.Client.RetrievalHealthAsync())!["items"]![0]!;
            Equal("direct", health["provider"]!.GetValue<string>(), "the direct provider");
            Check(health["retryAfterUntil"] is JsonValue, "Retry-After is kept");
            Equal(1, (await server.Client.SourceAsync("down"))!["health"]!.AsArray().Count, "the detail shows its origin's health");

            await FailsAsync(() => server.Client.DeleteSourceAsync("releases", 1), "conflict", "deleting needs the current revision");
            await server.Client.DeleteSourceAsync("releases", 2);
            await FailsAsync(() => server.Client.SourceAsync("releases"), "not_found", "a deleted source");
            Equal(1, (await server.Client.SourcesAsync())!["items"]!.AsArray().Count, "only the other source is listed");
        }),
        Case("application-api.research-starts-replays-and-publishes-each-angle", async () =>
        {
            await using var server = await TestServer.StartAsync("research-api");
            var session = await server.CreateSessionAsync("research");
            var request = new JsonObject
            {
                ["question"] = "Should we upgrade?",
                ["angles"] = new JsonArray(new JsonObject { ["title"] = "Cost" }, new JsonObject { ["id"] = "risk", ["title"] = "Risk", ["prompt"] = "What breaks?" }),
                ["budget"] = new JsonObject { ["requests"] = 10 },
            };
            var invalid = await SendRawAsync(server, HttpMethod.Post, $"/v1/sessions/{session}/research",
                new JsonObject { ["question"] = "Why?", ["research"] = new JsonObject { ["model"] = "faux/echo", ["thinking"] = "high" } });
            Equal(400, invalid.Status, "thinking on a model that does not think");
            Check(invalid.Body!["error"]!["fields"]!.AsArray().Any(field => field!["path"]!.GetValue<string>() == "/research/thinking"), "the field is named");
            await FailsAsync(() => server.Client.StartResearchAsync(session, new JsonObject { ["angles"] = new JsonArray() }), "invalid", "a question is required");

            var accepted = await SendRawAsync(server, HttpMethod.Post, $"/v1/sessions/{session}/research", request, "research-1");
            Equal(202, accepted.Status, "accepted");
            var taskId = accepted.Body!["taskId"]!.GetValue<long>();
            Check(accepted.Body["executionUrl"]!.GetValue<string>().EndsWith("/executions/" + taskId, StringComparison.Ordinal), "the execution URL");
            Check((await SendRawAsync(server, HttpMethod.Post, $"/v1/sessions/{session}/research", request, "research-1")).Replayed, "a retried start replays");
            var execution = await WaitForExecutionAsync(server, session, taskId, "completed");
            var branches = execution["run"]!["branches"]!.AsArray();
            Equal("a1,risk,synthesis", string.Join(",", branches.Select(row => row!["id"]!.GetValue<string>())), "an angle each and the synthesis");
            Check(branches.All(row => row!["status"]!.GetValue<string>() == "completed"), "all answered");
            var artifacts = (await server.Client.ExecutionArtifactsAsync(session, taskId))!["items"]!.AsArray();
            Equal(3, artifacts.Count, "a finding per angle and the synthesis");
            Check(artifacts.All(artifact => artifact!["kind"]!.GetValue<string>() == "finding"), "findings");
            var usage = (await server.Client.ExecutionUsageAsync(session, taskId))!;
            Equal(3L, usage["totals"]!["requests"]!.GetValue<long>(), "a request per child within the run's budget");
        }),
        Case("application-api.feedback-replays-undoes-and-preferences-revise-export-and-delete", async () =>
        {
            await using var server = await TestServer.StartAsync("feedback-api");
            var session = await server.CreateSessionAsync("feedback");
            var started = (await server.Client.StartResearchAsync(session, new JsonObject
            {
                ["question"] = "Should we upgrade?", ["angles"] = new JsonArray(new JsonObject { ["title"] = "Cost" }),
            }))!;
            var taskId = started["taskId"]!.GetValue<long>();
            await WaitForExecutionAsync(server, session, taskId, "completed");
            var artifacts = (await server.Client.ExecutionArtifactsAsync(session, taskId))!["items"]!.AsArray();
            var first = artifacts[0]!["id"]!.GetValue<string>();
            var second = artifacts[1]!["id"]!.GetValue<string>();
            var path = $"/v1/sessions/{session}/artifacts/{first}/feedback";

            Equal(0L, (await server.Client.PreferenceAsync("default"))!["revision"]!.GetValue<long>(), "an unwritten profile is at revision 0");
            var missing = await SendRawAsync(server, HttpMethod.Post, path, new JsonObject { ["action"] = "more" });
            Equal(400, missing.Status, "feedback needs a request ID");
            var invalid = await SendRawAsync(server, HttpMethod.Post, path, new JsonObject { ["action"] = "love", ["labels"] = new JsonArray("Not A Label!") }, "bad");
            Equal(400, invalid.Status, "an unknown action");
            var paths = invalid.Body!["error"]!["fields"]!.AsArray().Select(field => field!["path"]!.GetValue<string>()).ToList();
            Check(paths.Contains("/action") && paths.Contains("/labels/0"), "field errors: " + string.Join(", ", paths));

            var more = new JsonObject { ["action"] = "more", ["labels"] = new JsonArray("upgrade"), ["note"] = "Keep these", ["example"] = true };
            var recorded = await SendRawAsync(server, HttpMethod.Post, path, more, "fb-1");
            Equal(201, recorded.Status, "recorded");
            Check(!recorded.Replayed, "a first event is not a replay");
            var eventId = recorded.Body!["event"]!["id"]!.GetValue<string>();
            Equal(0.1, recorded.Body["profile"]!["weights"]!["upgrade"]!.GetValue<double>(), "more raises the label");
            var replay = await SendRawAsync(server, HttpMethod.Post, path, more, "fb-1");
            Check(replay.Replayed && replay.Body!["event"]!["id"]!.GetValue<string>() == eventId, "the same request ID replays the event");
            var changed = more.DeepClone().AsObject();
            changed["action"] = "less";
            Equal(409, (await SendRawAsync(server, HttpMethod.Post, path, changed, "fb-1")).Status, "the same request ID with other feedback");
            await server.Client.SendFeedbackAsync(session, second, new JsonObject { ["action"] = "less", ["labels"] = new JsonArray("risk") }, "fb-2");
            var kept = (await server.Client.PreferenceAsync("default"))!["examples"]!.AsArray();
            Check(kept.Any(example => example!["eventId"]!.GetValue<string>() == eventId), "the example is kept");

            var state = (await server.Client.ArtifactFeedbackAsync(session, first))!;
            Check(state["vote"]!.GetValue<string>() == "more" && state["undo"]!.GetValue<string>() == eventId && state["example"]!.GetValue<bool>(),
                "the artifact's current feedback");
            Check((await server.Client.ArtifactFeedbackAsync(session, first, "other"))!["vote"] is null, "another profile has none");
            var undo = (await server.Client.SendFeedbackAsync(session, first, new JsonObject { ["action"] = "undo" }, "fb-3"))!;
            Check((await server.Client.ArtifactFeedbackAsync(session, first))!["vote"] is null, "undone");
            Equal(eventId, undo["event"]!["reverses"]!.GetValue<string>(), "undo reverses the newest event on the artifact");
            Check(undo["profile"]!["weights"]!["upgrade"] is null, "the undone vote no longer counts");
            await FailsAsync(() => server.Client.SendFeedbackAsync(session, first, new JsonObject { ["action"] = "undo", ["reverses"] = eventId }, "fb-4"), "conflict",
                "an event is undone once");
            await FailsAsync(() => server.Client.SendFeedbackAsync(session, "art_missing", new JsonObject { ["action"] = "dismiss" }, "fb-5"), "not_found",
                "an unknown artifact");

            var profile = (await server.Client.PreferenceAsync("default"))!;
            var revision = profile["revision"]!.GetValue<long>();
            Equal(-0.15, profile["weights"]!["risk"]!.GetValue<double>(), "less lowers the label");
            Equal(1, (await server.Client.PreferencesAsync())!["items"]!.AsArray().Count, "one profile");
            Equal(409, (await SendRawAsync(server, HttpMethod.Patch, "/v1/preferences/default",
                new JsonObject { ["settings"] = new JsonObject { ["less"] = 0.5 }, ["expectedRevision"] = revision - 1 })).Status, "a stale revision");
            var bad = await SendRawAsync(server, HttpMethod.Patch, "/v1/preferences/default",
                new JsonObject { ["settings"] = new JsonObject { ["less"] = 5 }, ["expectedRevision"] = revision });
            Check(bad.Status == 400 && bad.Body!["error"]!["fields"]![0]!["path"]!.GetValue<string>() == "/settings/less", "an out-of-range weight is named");
            var updated = (await server.Client.UpdatePreferenceAsync("default", new JsonObject { ["less"] = 0.5 }, null, revision))!;
            Equal(-0.5, updated["weights"]!["risk"]!.GetValue<double>(), "the settings reweigh the votes");

            await server.Client.SendFeedbackAsync(session, first, new JsonObject { ["action"] = "label", ["labels"] = new JsonArray("upgrade"), ["note"] = "Secret plan",
                ["example"] = true }, "fb-6");
            var labelled = (await server.Client.PreferenceAsync("default"))!;
            var example = labelled["examples"]!.AsArray().First(item => item!["note"]?.GetValue<string>() == "Secret plan")!["eventId"]!.GetValue<string>();
            var forgotten = (await server.Client.UpdatePreferenceAsync("default", null, [example], labelled["revision"]!.GetValue<long>()))!;
            Check(forgotten["examples"]!.AsArray().All(item => item!["eventId"]!.GetValue<string>() != example), "the example is forgotten");

            var reset = (await server.Client.ResetPreferenceAsync("default", forgotten["revision"]!.GetValue<long>()))!;
            Equal(0, reset["weights"]!.AsObject().Count, "a reset starts over");
            var (status, exported, _) = await SendRawAsync(server, HttpMethod.Get, "/v1/preferences/default/export", null);
            Equal(200, status, "exported");
            Check(exported!["events"]!.AsArray().Count >= 6, "the export keeps the history");
            Check(!exported.ToJsonString().Contains("Secret plan", StringComparison.Ordinal), "a forgotten example's note is scrubbed");
            await FailsAsync(() => server.Client.DeletePreferenceAsync("default", 1), "conflict", "deleting needs the current revision");
            Check((await server.Client.DeletePreferenceAsync("default", reset["revision"]!.GetValue<long>()))!["deleted"]!.GetValue<bool>(), "deleted");
            Equal(0L, (await server.Client.PreferenceAsync("default"))!["revision"]!.GetValue<long>(), "a deleted profile starts at revision 0");
        }),
        Case("application-api.trigger-credentials-cannot-use-application-routes", async () =>
        {
            await using var server = await TestServer.StartAsync("application-scope");
            var session = await server.CreateSessionAsync("scoped");
            var token = (await server.Post("/v1/trigger-credentials", new JsonObject { ["label"] = "Cron" }))!["token"]!.GetValue<string>();
            foreach (var (method, path) in new[]
            {
                (HttpMethod.Get, "/v1/capabilities"), (HttpMethod.Get, "/v1/budgets"), (HttpMethod.Patch, "/v1/budgets"),
                (HttpMethod.Get, "/v1/policies"), (HttpMethod.Post, "/v1/policies/relevance/versions"), (HttpMethod.Put, "/v1/policies/relevance/active"),
                (HttpMethod.Post, $"/v1/sessions/{session}/judgments"), (HttpMethod.Get, $"/v1/sessions/{session}/artifacts"),
                (HttpMethod.Post, $"/v1/sessions/{session}/evidence"), (HttpMethod.Post, $"/v1/sessions/{session}/artifacts/art_x/actions/reevaluate"),
                (HttpMethod.Get, "/v1/sources"), (HttpMethod.Post, "/v1/sources"), (HttpMethod.Patch, "/v1/sources/releases"),
                (HttpMethod.Get, "/v1/retrieval/health"), (HttpMethod.Post, $"/v1/sessions/{session}/source-checks"),
                (HttpMethod.Post, $"/v1/sessions/{session}/research"), (HttpMethod.Post, $"/v1/sessions/{session}/artifacts/art_x/feedback"),
                (HttpMethod.Get, "/v1/preferences"), (HttpMethod.Get, "/v1/preferences/default"), (HttpMethod.Patch, "/v1/preferences/default"),
                (HttpMethod.Post, "/v1/preferences/default/reset"), (HttpMethod.Get, "/v1/preferences/default/export"),
                (HttpMethod.Delete, "/v1/preferences/default?expectedRevision=0"), (HttpMethod.Get, "/v1/retention"), (HttpMethod.Post, "/v1/retention"),
            })
                Equal(403, (await SendRawAsync(server, method, path, new JsonObject(), "k", token)).Status, "forbidden: " + method + " " + path);
            Equal(429, DurableErrorCodes.Status(DurableErrorCodes.Limited), "limited is 429");
            Equal(413, DurableErrorCodes.Status(DurableErrorCodes.TooLarge), "too_large is 413");
        }),
    ];
}
