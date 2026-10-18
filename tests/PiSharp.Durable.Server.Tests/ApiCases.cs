using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

internal static partial class Program
{
    private static JsonObject Message(string content, string requestId, string whenBusy = "followUp", int? waitMs = null)
    {
        var body = new JsonObject { ["content"] = content, ["requestId"] = requestId, ["whenBusy"] = whenBusy };
        if (waitMs is { } wait) body["waitMs"] = wait;
        return body;
    }

    private static string AnswerText(JsonNode? result) => result?["answer"]?["text"]?.GetValue<string>() ?? "";

    internal static IEnumerable<(string, Func<Task>)> ApiCases() =>
    [
        Case("api.auth-envelope-and-unknown-routes", async () =>
        {
            await using var server = await TestServer.StartAsync("api-auth", "api-box");
            var health = await server.RawAsync(HttpMethod.Get, "/healthz", token: false);
            Equal(200, health.Status, "healthz is open");
            Check(health.Body?["ok"]?.GetValue<bool>() == true, "healthz body");
            var anonymous = await server.RawAsync(HttpMethod.Get, "/v1/device", token: false);
            Equal(401, anonymous.Status, "no token");
            Equal("unauthorized", anonymous.Body?["error"]?["code"]?.GetValue<string>(), "the envelope");
            using (var wrong = new DurableDeviceClient(server.Base, new string('x', 40)))
                await FailsAsync(() => wrong.DeviceAsync(), "unauthorized", "a wrong token");
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Base, "/v1/device")))
            {
                request.Headers.Add("X-DPI-Token", server.Token);
                Equal(200, (int)(await server.Raw.SendAsync(request)).StatusCode, "X-DPI-Token");
            }
            var info = await server.Client.DeviceAsync();
            Equal("api-box", info.Name, "name");
            Equal(1, info.ApiVersion, "apiVersion");
            Equal(server.Work, info.StartDirs.Single(), "start dirs");
            Equal("echo", info.Models[0].Id, "models");
            Equal("echo", info.DefaultModel, "default model");
            Check(info.Capabilities.Contains("interactions"), "PiSharp capabilities");
            Equal("workspace-snapshot,release-check,change-watch", string.Join(",", info.Jobs!.Select(job => job!["id"]!.GetValue<string>())), "jobs");
            var missing = await server.RawAsync(HttpMethod.Get, "/v1/nope");
            Equal(404, missing.Status, "unknown route");
            Equal("not_found", missing.Body?["error"]?["code"]?.GetValue<string>(), "unknown route code");
            var bad = await server.RawAsync(HttpMethod.Post, "/v1/sessions", "{nope");
            Equal(400, bad.Status, "bad JSON");
            Equal("invalid", bad.Body?["error"]?["code"]?.GetValue<string>(), "bad JSON code");
            Equal(400, (await server.RawAsync(HttpMethod.Post, "/v1/sessions", "[]")).Status, "not an object");
            Equal(400, (await server.RawAsync(HttpMethod.Post, "/v1/sessions", "name=x", "application/x-www-form-urlencoded")).Status, "not JSON");
            Equal(400, (await server.RawAsync(HttpMethod.Post, "/v1/sessions", "{\"name\":\"" + new string('x', 300_000) + "\"}")).Status, "too large");
        }),

        Case("api.directories-sessions-and-idempotency", async () =>
        {
            await using var server = await TestServer.StartAsync("api-sessions");
            var roots = (await server.Get("/v1/fs/dirs"))!;
            Equal(server.Work, roots["dirs"]![0]!["path"]!.GetValue<string>(), "start dirs");
            var inside = (await server.Get("/v1/fs/dirs?path=" + Uri.EscapeDataString(server.Work)))!;
            Equal("project", inside["dirs"]![0]!["name"]!.GetValue<string>(), "subdirectories");
            await FailsAsync(() => server.Post("/v1/sessions", new JsonObject { ["name"] = "x", ["cwd"] = Path.Join(server.Work, "missing") }), "invalid", "a missing cwd");
            var first = await server.CreateSessionAsync("Alpha", "create-1");
            var again = await server.CreateSessionAsync("Alpha", "create-1");
            Equal(first, again, "an Idempotency-Key creates once");
            Equal(1, (await server.Get("/v1/sessions"))!.AsArray().Count, "one session");
            await server.CreateSessionAsync("Twin");
            await server.CreateSessionAsync("twin");
            await FailsAsync(() => server.Get("/v1/sessions/twin"), "ambiguous", "two sessions share a name");
            Equal(first, (await server.Get("/v1/sessions/alpha"))!["id"]!.GetValue<string>(), "by name, ignoring case");
            Equal("Alpha Prime", (await server.Patch($"/v1/sessions/{first}", new JsonObject { ["name"] = "Alpha Prime" }))!["name"]!.GetValue<string>(), "rename");
            var compaction = (await server.Patch($"/v1/sessions/{first}/compaction", new JsonObject { ["reserveTokens"] = 20000 }))!;
            Equal(20000.0, compaction["reserveTokens"]!.GetValue<double>(), "compaction");
            Equal(20000.0, (await server.Get($"/v1/sessions/{first}"))!["compaction"]!["reserveTokens"]!.GetValue<double>(), "on the summary");
            await FailsAsync(() => server.Patch($"/v1/sessions/{first}/compaction", new JsonObject { ["backgroundTokens"] = -1 }), "invalid", "out of range");
            await FailsAsync(() => server.Delete("/v1/sessions/Twin"), "ambiguous", "archive needs one session");
            var tree = await server.Client.TreeAsync();
            await server.Delete($"/v1/sessions/{tree.Sessions.First(session => session.Name == "Twin").Id}");
            Equal(2, (await server.Get("/v1/sessions"))!.AsArray().Count, "archived");
        }),

        Case("api.messages-waits-busy-and-transcript", async () =>
        {
            await using var server = await TestServer.StartAsync("api-messages");
            var id = await server.CreateSessionAsync("Messages");
            var main = $"/v1/sessions/{id}/agents/main";
            var queued = (await server.Post(main + "/messages", Message("hello", "req-1")))!;
            Equal("queued", queued["status"]!.GetValue<string>(), "queued");
            var submission = queued["submissionId"]!.GetValue<long>();
            var settled = await server.Get($"/v1/sessions/{id}/submissions/{submission}?waitMs=15000");
            Equal("done", settled!["status"]!.GetValue<string>(), "settled");
            Equal("Echo: hello", AnswerText(settled), "answer");
            Equal(submission, (await server.Post(main + "/messages", Message("hello", "req-1")))!["submissionId"]!.GetValue<long>(), "the same request id");
            Equal("Echo: ping", AnswerText(await server.Post(main + "/messages", Message("ping", "req-2", waitMs: 15000))), "one request");

            var slow = (await server.Post(main + "/messages", Message("slow: " + string.Join(' ', Enumerable.Repeat("word", 40)), "req-3", waitMs: 200)))!;
            Equal("pending", slow["status"]!.GetValue<string>(), "a wait that times out");
            var busy = await FailsAsync(() => server.Post(main + "/messages", Message("not now", "req-4", "reject")), "busy", "reject while busy");
            Equal(409, busy.Status, "busy status");
            Equal("queued", (await server.Post(main + "/messages", Message("steer this", "req-5", "steer")))!["status"]!.GetValue<string>(), "a steer queues");
            var finished = await server.Get($"/v1/sessions/{id}/submissions/{slow["submissionId"]!.GetValue<long>()}?waitMs=20000");
            Equal("done", finished!["status"]!.GetValue<string>(), "the slow reply finished");
            await WaitUntilAsync(async () => (await server.Get(main + "/transcript?format=markdown"))!["text"]!.GetValue<string>().Contains("Echo: steer this"),
                "the steer was answered");
            var markdown = (await server.Get(main + "/transcript?format=markdown"))!["text"]!.GetValue<string>();
            Check(markdown.Contains("### You (#") && markdown.Contains("Echo: ping"), "markdown: " + markdown);
            var json = (await server.Get(main + "/transcript"))!;
            Equal("json", json["format"]!.GetValue<string>(), "format");
            Check(json["items"]!.AsArray().Any(item => item!["kind"]!.GetValue<string>() == "assistant"), "json items");
            await FailsAsync(() => server.Post(main + "/messages", Message("", "req-6")), "invalid", "empty content");
            await FailsAsync(() => server.Post(main + "/messages", new JsonObject { ["content"] = "x" }), "invalid", "no request id");
            await FailsAsync(() => server.Get($"/v1/sessions/{id}/submissions/999"), "not_found", "an unknown submission");
        }),

        Case("api.agents-forks-config-and-tools", async () =>
        {
            await using var server = await TestServer.StartAsync("api-agents");
            var id = await server.CreateSessionAsync("Agents");
            var main = $"/v1/sessions/{id}/agents/main";
            await server.Post(main + "/messages", Message("first", "a-1", waitMs: 15000));
            Equal("Lead", (await server.Patch(main, new JsonObject { ["name"] = "Lead" }))!["name"]!.GetValue<string>(), "rename");
            Equal(1L, (await server.Get($"/v1/sessions/{id}/agents/lead"))!["id"]!.GetValue<long>(), "by its new name");
            var active = (await server.Get(main + "/entries?scope=active"))!["items"]!.AsArray();
            var answer = active.First(entry => entry!["kind"]!.GetValue<string>() == "pi.assistant")!["id"]!.GetValue<long>();
            var fork = (await server.Post(main + "/fork", new JsonObject { ["entryId"] = answer, ["name"] = "branch" }, "fork-1"))!;
            Equal("fork", fork["kind"]!.GetValue<string>(), "kind");
            Equal(1L, fork["parentId"]!.GetValue<long>(), "parent");
            Equal(fork["id"]!.GetValue<long>(), (await server.Post(main + "/fork", new JsonObject { ["entryId"] = answer, ["name"] = "branch" }, "fork-1"))!["id"]!.GetValue<long>(),
                "an Idempotency-Key forks once");
            await FailsAsync(() => server.Post(main + "/fork", new JsonObject { ["entryId"] = answer + 1 }, "fork-1"), "invalid", "a key reused for another fork");
            var reviewer = (await server.Post($"/v1/sessions/{id}/agents", new JsonObject { ["name"] = "reviewer" }, "agent-1"))!;
            Equal("root", reviewer["kind"]!.GetValue<string>(), "a new root agent");
            Equal(reviewer["id"]!.GetValue<long>(), (await server.Post($"/v1/sessions/{id}/agents", new JsonObject { ["name"] = "reviewer" }, "agent-1"))!["id"]!.GetValue<long>(),
                "an Idempotency-Key creates once");
            var detail = DurableWire.Read<AgentDetail>(await server.Get($"/v1/sessions/{id}/agents/reviewer"))!;
            Check(detail.Tools.Any(tool => tool.Name == "bash" && tool.Writes && tool.Effect == "exec"), "tools with effects");
            Check(detail.Tools.Any(tool => tool.Name == "read" && !tool.Writes) && detail.Tools.Any(tool => tool.Replay == "safe"), "effects and replay policies");
            Equal("echo", detail.Config.Model?.ModelId, "config model");
            var config = (await server.Patch($"/v1/sessions/{id}/agents/reviewer/config", new JsonObject { ["thinkingLevel"] = "high", ["instructions"] = "Be brief." }))!;
            Equal("high", config["thinkingLevel"]!.GetValue<string>(), "thinking");
            await FailsAsync(() => server.Patch($"/v1/sessions/{id}/agents/reviewer/config", new JsonObject { ["thinkingLevel"] = "max" }), "invalid", "a bad level");
            await FailsAsync(() => server.Get($"/v1/sessions/{id}/agents/nobody"), "not_found", "an unknown agent");
            Equal("Lead,branch,reviewer", string.Join(",", (await server.Get($"/v1/sessions/{id}/agents"))!.AsArray().Select(agent => agent!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)),
                "agents");
            Equal(true, (await server.Patch(main + "/config", new JsonObject { ["readOnly"] = true }))!["readOnly"]!.GetValue<bool>(), "read only");
            var readOnly = DurableWire.Read<AgentDetail>(await server.Get(main))!;
            Check(!readOnly.Tools.Single(tool => tool.Name == "bash").Offered && readOnly.Tools.Single(tool => tool.Name == "read").Offered, "writers leave the request");
            Equal(false, (await server.Patch(main + "/config", new JsonObject { ["readOnly"] = false }))!["readOnly"]!.GetValue<bool>(), "read only off");
            var entries = (await server.Get(main + "/entries?limit=2"))!;
            Equal(2, entries["items"]!.AsArray().Count, "a page");
            var next = (await server.Get(main + "/entries?limit=500&cursor=" + entries["next"]!.GetValue<string>()))!;
            Check(next["items"]!.AsArray().Count > 0 && next["items"]![0]!["id"]!.GetValue<long>() > entries["items"]![1]!["id"]!.GetValue<long>(), "the next page");
            await FailsAsync(() => server.Get(main + "/entries?cursor=%%%"), "invalid", "a bad cursor");
            await server.Post(main + "/reset", new JsonObject { ["note"] = "handoff note" });
            Check((await server.Get(main + "/entries?scope=active"))!["items"]!.AsArray().Any(entry => entry!["kind"]!.GetValue<string>() == "pi.reset"), "reset");
            Check((await server.Post(main + "/abort"))!["ok"]!.GetValue<bool>(), "abort");
            Check((await server.Post(main + "/compact"))!["taskId"]!.GetValue<long>() > 0, "compact");
        }),

        Case("api.state-jobs-extensions-and-interactions", async () =>
        {
            await using var server = await TestServer.StartAsync("api-apps");
            var id = await server.CreateSessionAsync("Apps");
            var main = $"/v1/sessions/{id}/agents/main";
            var state = (await server.Post(main + "/demo/state", new JsonObject { ["increment"] = true }))!;
            Equal(1L, state["revision"]!.GetValue<long>(), "revision");
            Equal(1.0, (await server.Get("/v1/docs/app.shared-state"))!["value"]!["counter"]!.GetValue<double>(), "the device document");
            await FailsAsync(() => server.Post(main + "/state", new JsonObject { ["replace"] = new JsonObject { ["goal"] = "x" }, ["expectedRevision"] = 0 }),
                "conflict", "a stale save");
            Equal(2L, (await server.Post(main + "/state", new JsonObject { ["replace"] = new JsonObject { ["goal"] = "x" }, ["expectedRevision"] = 1 }))!["revision"]!.GetValue<long>(),
                "a current save");
            await FailsAsync(() => server.Post(main + "/state", new JsonObject { ["operation"] = "increment", ["path"] = "/goal" }), "invalid", "increment a string");

            var job = (await server.Post(main + "/demo/jobs", new JsonObject { ["job"] = "workspace-snapshot" }))!["taskId"]!.GetValue<long>();
            await WaitUntilAsync(async () => (await server.Get(main + "/docs/pisharp.job-runs"))!["value"]!["items"]!.AsArray()
                .Any(run => run!["id"]!.GetValue<long>() == job && run["status"]!.GetValue<string>() == "completed"), "the job completed");
            await FailsAsync(() => server.Post(main + "/jobs", new JsonObject { ["job"] = "nope" }), "invalid", "an unknown job");
            var release = (await server.Post(main + "/jobs", new JsonObject { ["job"] = "release-check" }))!["taskId"]!.GetValue<long>();
            await WaitUntilAsync(async () => (await server.Get(main + "/jobs"))!["runs"]!.AsArray()
                .Any(run => run!["id"]!.GetValue<long>() == release && run["status"]!.GetValue<string>() == "waiting"), "the release waits");
            await server.Post(main + $"/jobs/{release}/approval", new JsonObject { ["approved"] = true });
            await WaitUntilAsync(async () => (await server.Get(main + "/jobs"))!["runs"]!.AsArray()
                .Any(run => run!["id"]!.GetValue<long>() == release && run["status"]!.GetValue<string>() == "completed"), "approved and completed");
            var live = (await server.Get($"/v1/sessions/{id}/tasks?status=live"))!.AsArray();
            Check(live.All(task => task!["kind"] is not null), "live tasks");
            Check((await server.Get($"/v1/sessions/{id}/tasks?status=terminal"))!.AsArray().Count > 0, "terminal tasks");
            await FailsAsync(() => server.Get($"/v1/sessions/{id}/tasks?status=sometimes"), "invalid", "a bad status");

            var extensions = DurableWire.Read<AgentExtensions>(await server.Get(main + "/extensions"))!;
            Equal("system-prompts,command-guard", string.Join(",", extensions.Extensions.Select(extension => extension.Name)), "two optional extensions");
            var guarded = DurableWire.Read<AgentExtensions>(await server.Patch(main + "/extensions", new JsonObject { ["name"] = "command-guard", ["enabled"] = true }))!;
            Check(guarded.Extensions.Single(extension => extension.Name == "command-guard").Enabled, "the guard is on");
            await FailsAsync(() => server.Patch(main + "/extensions", new JsonObject { ["name"] = "coding-tools", ["enabled"] = false }), "invalid", "a built-in extension");
            var blocked = await server.Post(main + "/messages", Message("!rm -rf ./nothing-here", "g-1", waitMs: 15000));
            Check(AnswerText(blocked).Contains("Blocked by the command guard"), "blocked: " + AnswerText(blocked));

            await server.Post(main + "/messages", Message(
                """tool: ask_user_question {"questions":[{"question":"Which version?","header":"Version","options":[{"label":"1.2.0.2","description":"Fixes"},{"label":"1.2.1","description":"Next"}],"multiSelect":false}]}""",
                "q-1"));
            JsonArray pending = [];
            await WaitUntilAsync(async () => (pending = (await server.Get(main + "/interactions"))!.AsArray()).Count == 1, "a question waits");
            Equal("questions", pending[0]!["kind"]!.GetValue<string>(), "kind");
            var task = pending[0]!["taskId"]!.GetValue<long>();
            await server.Post(main + $"/interactions/{task}", new JsonObject { ["answers"] = new JsonArray(new JsonObject { ["selected"] = new JsonArray("1.2.0.2") }) });
            await WaitUntilAsync(async () => (await server.Get(main + "/transcript?format=markdown"))!["text"]!.GetValue<string>().Contains("1.2.0.2"), "the answer reached the agent");
            await FailsAsync(() => server.Post(main + $"/interactions/{task}", new JsonObject { ["decline"] = true }), "conflict", "answered already");

            Equal(true, (await server.Patch(main + "/plan-mode", new JsonObject { ["enabled"] = true }))!["enabled"]!.GetValue<bool>(), "plan mode on");
            var planning = DurableWire.Read<AgentDetail>(await server.Get(main))!;
            Check(planning.Extensions.Contains("plan-mode"), "plan-mode selected");
            Equal(false, (await server.Patch(main + "/plan-mode", new JsonObject { ["enabled"] = false }))!["enabled"]!.GetValue<bool>(), "plan mode off");
        }),
    ];
}
