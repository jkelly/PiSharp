using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Hosting.Testing;
using Chord = PiSharp.Durable.Chord;

// Decision 0010, M2–M5: the hub's policy, budget, source, research and preference pages, the proxy for the application routes, and artifact
// cards with their feedback controls.
internal static partial class Program
{
    /// <summary>Faux models whose classifier labels every item <c>relevant</c> with the item's own confidence.</summary>
    internal static FauxDurableModels Judging() => new()
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

    internal static IEnumerable<(string, Func<Task>)> ApplicationCases() =>
    [
        Case("hub.application-pages-proxy-and-encoded-artifact-cards", async () =>
        {
            await using var hub = await Hub.StartAsync("applications");
            var session = await hub.SessionAsync("judging");
            foreach (var (path, marker) in new[] { ("/policies", "data-automation=\"policies\""), ("/budgets", "data-automation=\"budgets\"") })
            {
                var page = await hub.Get(path);
                Equal(200, page.Status, path);
                Check(page.Text.Contains(marker, StringComparison.Ordinal), $"the {path} page");
            }
            var spec = JsonNode.Parse("""
                {"title":"Relevance","kind":"classifier","model":{"provider":"faux","id":"judge"},"instructions":"Is it relevant?",
                 "inputSchema":{"type":"object","additionalProperties":false,"required":["title","confidence"],"properties":{
                   "title":{"type":"string","maxLength":200},"confidence":{"type":"number","minimum":0,"maximum":1}}},
                 "labels":[{"id":"relevant","description":"It matters"},{"id":"ignore","description":"It does not"}]}
                """)!;
            var published = await hub.Post("/api/devices/local/v1/policies/relevance/versions", spec);
            Equal(201, published.Status, "publish through the proxy: " + published.Text);
            var invalid = await hub.Post("/api/devices/local/v1/policies/broken/versions", new JsonObject { ["title"] = "Broken" });
            Equal(400, invalid.Status, "an invalid spec");
            Check(invalid.Body!["error"]!["fields"]!.AsArray().Count > 0, "field errors pass through the proxy");
            Equal(200, (await hub.SendAsync(HttpMethod.Put, "/api/devices/local/v1/policies/relevance/active", new JsonObject { ["version"] = 1, ["expectedRevision"] = 1 })).Status,
                "PUT passes through the proxy");
            var evidence = await hub.Post($"/api/devices/local/v1/sessions/{session}/evidence", new JsonObject
            {
                ["title"] = "Notes <img src=x onerror=alert(1)>", ["url"] = "https://example.com/notes", ["text"] = "Release notes for 2.0.",
            });
            Equal(201, evidence.Status, "evidence: " + evidence.Text);
            var request = new JsonObject
            {
                ["policyId"] = "relevance",
                ["title"] = "<script>alert('x')</script> v2.0",
                ["input"] = new JsonObject { ["title"] = "v2.0", ["confidence"] = 0.95 },
                ["evidenceIds"] = new JsonArray(evidence.Body!["id"]!.GetValue<string>()),
            };
            async Task<(int Status, JsonNode? Body, bool Replayed)> Judge()
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(hub.Base, $"/api/devices/local/v1/sessions/{session}/judgments"));
                message.Headers.Add("Idempotency-Key", "web-judge-1");
                message.Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await hub.Http.SendAsync(message);
                return ((int)response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()),
                    response.Headers.TryGetValues("Idempotent-Replay", out var values) && values.Contains("true"));
            }
            var started = await Judge();
            Equal(202, started.Status, "accepted");
            var task = started.Body!["taskId"]!.GetValue<long>();
            var replay = await Judge();
            Check(replay.Replayed && replay.Body!["taskId"]!.GetValue<long>() == task, "Idempotent-Replay comes back through the proxy");
            await WaitUntilAsync(async () => (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}")).Body?["status"]?.GetValue<string>() == "completed",
                "the judgment completed");

            var cards = await hub.Get($"/ui/local/{session}/executions/{task}/artifacts");
            Equal(200, cards.Status, "the artifact fragment");
            Check(cards.Text.Contains("data-kind=\"finding\"", StringComparison.Ordinal), "a finding card: " + Cut(cards.Text));
            Check(!cards.Text.Contains("<script>", StringComparison.Ordinal) && cards.Text.Contains("&lt;script&gt;", StringComparison.Ordinal), "the title is encoded");
            Check(!cards.Text.Contains("<img", StringComparison.Ordinal), "the evidence title is encoded");
            Check(cards.Text.Contains("href=\"https://example.com/notes\"", StringComparison.Ordinal), "an HTTPS evidence link");
            Check(cards.Text.Contains("/export\"", StringComparison.Ordinal) && cards.Text.Contains("Policy relevance v1", StringComparison.Ordinal), "export link and provenance");
            Equal(200, (await hub.Get($"/api/devices/local/v1/sessions/{session}/artifacts/{cards.Text.Split("data-artifact=\"")[1].Split('"')[0]}/export")).Status, "export through the proxy");
            var budgets = await hub.Get("/api/devices/local/v1/budgets");
            Equal(200, budgets.Status, "budgets through the proxy");
            Check(budgets.Body!["used"]!["requests"]!.GetValue<long>() >= 1, "the call is counted");
        }),
        Case("hub.sources-page-and-proxy-edits", async () =>
        {
            await using var hub = await Hub.StartAsync("sources");
            var page = await hub.Get("/sources");
            Equal(200, page.Status, "/sources");
            Check(page.Text.Contains("data-automation=\"sources\"", StringComparison.Ordinal) && page.Text.Contains("/js/sources.js", StringComparison.Ordinal),
                "the sources page and its script");
            Check((await hub.Get("/policies")).Text.Contains("href=\"/sources\"", StringComparison.Ordinal), "the topbar links the page");
            var created = await hub.Post("/api/devices/local/v1/sources", new JsonObject { ["id"] = "releases", ["kind"] = "feed", ["url"] = "https://example.com/feed.xml" });
            Equal(201, created.Status, "create through the proxy: " + created.Text);
            var patched = await hub.SendAsync(HttpMethod.Patch, "/api/devices/local/v1/sources/releases", new JsonObject { ["enabled"] = false, ["expectedRevision"] = 1 });
            Equal(200, patched.Status, "PATCH passes through the proxy");
            Check(patched.Body!["enabled"]!.GetValue<bool>() == false && patched.Body["revision"]!.GetValue<long>() == 2, "disabled at revision 2");
            Equal(409, (await hub.SendAsync(HttpMethod.Delete, "/api/devices/local/v1/sources/releases?expectedRevision=1", null)).Status, "a stale delete");
            Equal(200, (await hub.SendAsync(HttpMethod.Delete, "/api/devices/local/v1/sources/releases?expectedRevision=2", null)).Status,
                "DELETE and its query pass through the proxy");
            Equal(0, (await hub.Get("/api/devices/local/v1/sources")).Body!["items"]!.AsArray().Count, "no sources left");
            Equal(200, (await hub.Get("/api/devices/local/v1/retrieval/health")).Status, "retrieval health through the proxy");
        }),
        Case("hub.research-page-starts-and-follows-a-run", async () =>
        {
            await using var hub = await Hub.StartAsync("research");
            var page = await hub.Get("/research");
            Equal(200, page.Status, "/research");
            Check(page.Text.Contains("data-automation=\"research\"", StringComparison.Ordinal) && page.Text.Contains("/js/research.js", StringComparison.Ordinal),
                "the research page and its script");
            Check((await hub.Get("/sources")).Text.Contains("href=\"/research\"", StringComparison.Ordinal), "the topbar links the page");
            var session = await hub.SessionAsync("research");
            var started = await hub.Post($"/api/devices/local/v1/sessions/{session}/research", new JsonObject
            {
                ["question"] = "Should we <b>upgrade</b>?",
                ["angles"] = new JsonArray(new JsonObject { ["title"] = "Cost" }, new JsonObject { ["title"] = "Risk" }),
                ["concurrency"] = 2,
                ["budget"] = new JsonObject { ["requests"] = 10 },
            });
            Equal(202, started.Status, "start through the proxy: " + started.Text);
            var task = started.Body!["taskId"]!.GetValue<long>();
            await WaitUntilAsync(async () => (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}")).Body?["run"]?["status"]?.GetValue<string>() == "completed",
                "the research completed");
            var execution = (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}")).Body!;
            Equal(3, execution["run"]!["branches"]!.AsArray().Count, "two angles and the synthesis");
            Equal(3, (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}/artifacts")).Body!["items"]!.AsArray().Count, "the findings");
            var recent = (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions?kind=job&q=pisharp-research&limit=20")).Body!["items"]!.AsArray();
            Check(recent.Any(item => item!["id"]!.GetValue<long>() == task), "the page's recent list finds the run through the proxy");
            Equal(200, (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}/usage")).Status, "usage through the proxy");
        }),
        Case("hub.feedback-controls-and-preferences-page", async () =>
        {
            await using var hub = await Hub.StartAsync("feedback");
            var page = await hub.Get("/preferences");
            Equal(200, page.Status, "/preferences");
            Check(page.Text.Contains("data-automation=\"preferences\"", StringComparison.Ordinal) && page.Text.Contains("/js/preferences.js", StringComparison.Ordinal),
                "the preferences page and its script");
            Check((await hub.Get("/budgets")).Text.Contains("href=\"/preferences\"", StringComparison.Ordinal), "the menu links the page");
            var session = await hub.SessionAsync("feedback");
            var started = await hub.Post($"/api/devices/local/v1/sessions/{session}/research", new JsonObject
            {
                ["question"] = "Should we upgrade?", ["angles"] = new JsonArray(new JsonObject { ["title"] = "Cost" }),
            });
            var task = started.Body!["taskId"]!.GetValue<long>();
            await WaitUntilAsync(async () => (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}")).Body?["status"]?.GetValue<string>() == "completed",
                "the research completed");

            var cards = await hub.Get($"/ui/local/{session}/executions/{task}/artifacts");
            Check(cards.Text.Contains("data-feedback=\"", StringComparison.Ordinal) && cards.Text.Contains("data-feedback-action=\"more\" aria-pressed=\"false\"", StringComparison.Ordinal),
                "feedback controls on the cards: " + Cut(cards.Text));
            var artifact = cards.Text.Split("data-artifact=\"")[1].Split('"')[0];
            var path = $"/api/devices/local/v1/sessions/{session}/artifacts/{artifact}/feedback";
            async Task<(int Status, bool Replayed)> Vote()
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(hub.Base, path));
                message.Headers.Add("Idempotency-Key", "web-vote-1");
                message.Content = new StringContent("""{"action":"more","example":true}""", Encoding.UTF8, "application/json");
                using var response = await hub.Http.SendAsync(message);
                return ((int)response.StatusCode, response.Headers.TryGetValues("Idempotent-Replay", out var values) && values.Contains("true"));
            }
            Equal(201, (await Vote()).Status, "feedback through the proxy");
            Check((await Vote()).Replayed, "a retried vote replays through the proxy");
            Equal("more", (await hub.Get(path)).Body!["vote"]!.GetValue<string>(), "the artifact's feedback through the proxy");
            var voted = (await hub.Get($"/ui/local/{session}/executions/{task}/artifacts")).Text;
            var card = voted.Split($"data-feedback=\"{artifact}\"")[1].Split("</div>")[0];
            Check(card.Contains("data-feedback-action=\"more\" aria-pressed=\"true\"", StringComparison.Ordinal), "the card shows the saved vote: " + Cut(card));
            Check(card.Contains("Saved: More like this", StringComparison.Ordinal) && !card.Contains("data-feedback-action=\"undo\" hidden", StringComparison.Ordinal),
                "saved, with undo offered");

            var profile = (await hub.Get("/api/devices/local/v1/preferences/default")).Body!;
            Equal(1, profile["examples"]!.AsArray().Count, "the example");
            var stale = await hub.SendAsync(HttpMethod.Patch, "/api/devices/local/v1/preferences/default",
                new JsonObject { ["settings"] = new JsonObject { ["more"] = 0.2 }, ["expectedRevision"] = 0 });
            Equal(409, stale.Status, "a stale settings change");
            var saved = await hub.SendAsync(HttpMethod.Patch, "/api/devices/local/v1/preferences/default",
                new JsonObject { ["settings"] = new JsonObject { ["more"] = 0.2 }, ["expectedRevision"] = profile["revision"]!.GetValue<long>() });
            Equal(200, saved.Status, "PATCH through the proxy: " + saved.Text);
            using var export = await hub.Http.GetAsync(new Uri(hub.Base, "/api/devices/local/v1/preferences/default/export"));
            Equal(200, (int)export.StatusCode, "export through the proxy");
            Check(export.Content.Headers.ContentDisposition?.DispositionType == "attachment", "the export downloads");
            var revision = saved.Body!["revision"]!.GetValue<long>();
            Equal(200, (await hub.SendAsync(HttpMethod.Delete, $"/api/devices/local/v1/preferences/default?expectedRevision={revision}", null)).Status, "deleted");
            Equal(0L, (await hub.Get("/api/devices/local/v1/preferences/default")).Body!["revision"]!.GetValue<long>(), "the profile starts over");
        }),
    ];
}
