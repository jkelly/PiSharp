using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> AutomationApiCases() =>
    [
        Case("automation-api.scoped-trigger-cannot-read-chat-manage-or-trigger-another-schedule", async () =>
        {
            await using var server = await TestServer.StartAsync("trigger-scope");
            var session = await server.CreateSessionAsync("scheduled");
            async Task<string> Create() => (await server.Post("/v1/schedules", new JsonObject
            { ["sessionId"] = session, ["jobId"] = "workspace-snapshot", ["everyMinutes"] = 60 }))!["id"]!.GetValue<string>();
            var schedule = await Create();
            var other = await Create();
            var issued = await server.Post("/v1/trigger-credentials", new JsonObject
            { ["label"] = "Cron", ["scheduleId"] = schedule, ["expiresInHours"] = 1 });
            var token = issued!["token"]!.GetValue<string>();
            var id = issued["credential"]!["id"]!.GetValue<string>();
            using var client = new DurableDeviceClient(server.Base, token);
            var tick = await client.TickScheduleAsync(schedule);
            Equal("not_due", tick!["status"]!.GetValue<string>(), "scoped token cannot force an early run");
            foreach (var (method, path) in new[]
            {
                (HttpMethod.Get, "/v1/device"), (HttpMethod.Get, "/v1/schedules"), (HttpMethod.Get, "/v1/trigger-credentials"),
                (HttpMethod.Get, $"/v1/sessions/{session}/executions"), (HttpMethod.Post, "/v1/sessions"),
                (HttpMethod.Post, "/v1/trigger-credentials"), (HttpMethod.Patch, $"/v1/schedules/{schedule}"),
                (HttpMethod.Delete, $"/v1/schedules/{schedule}"), (HttpMethod.Post, $"/v1/schedules/{other}/tick"),
                (HttpMethod.Post, $"/v1/sessions/{session}/agents/main/messages"),
            })
            {
                var error = await ThrowsAsync<DurableApiException>(() => client.SendAsync(method, path, new JsonObject()), path);
                Equal(403, error.Status, "forbidden: " + path);
            }
            await server.Patch($"/v1/schedules/{schedule}", new JsonObject { ["enabled"] = false });
            Equal("paused", (await client.SendAsync(HttpMethod.Post, $"/v1/schedules/{schedule}/tick", new JsonObject()))!["status"]!.GetValue<string>(), "token respects pause");
            var listed = (await server.Get("/v1/trigger-credentials"))!.ToJsonString();
            Check(!listed.Contains(token, StringComparison.Ordinal) && !listed.Contains("hash", StringComparison.OrdinalIgnoreCase), "GET exposes no secret or hash");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.Base, "/v1/trigger-credentials"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await server.Raw.SendAsync(request);
            Check(response.Headers.CacheControl?.NoStore == true, "issued secrets cannot be cached");
            var global = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
            using var globalClient = new DurableDeviceClient(server.Base, global);
            Equal("not_due", (await globalClient.TickScheduleAsync(other))!["status"]!.GetValue<string>(), "an unrestricted trigger can tick another existing schedule");
            await server.Delete($"/v1/trigger-credentials/{id}");
            Equal(401, (await ThrowsAsync<DurableApiException>(() => client.SendAsync(HttpMethod.Post, $"/v1/schedules/{schedule}/tick"), "revoked")).Status, "revocation is immediate");
        }),
        Case("automation-api.schedule-validation-and-execution-audit", async () =>
        {
            await using var server = await TestServer.StartAsync("schedule-api");
            var sessionId = await server.CreateSessionAsync("history");
            foreach (var interval in new[] { 0, -1, 525601 })
                await FailsAsync(() => server.Post("/v1/schedules", new JsonObject { ["sessionId"] = sessionId, ["jobId"] = "workspace-snapshot", ["everyMinutes"] = interval }), "invalid", "interval");
            await FailsAsync(() => server.Post("/v1/schedules", new JsonObject { ["sessionId"] = sessionId, ["jobId"] = "workspace-snapshot", ["everyMinutes"] = 1, ["input"] = new JsonObject { ["bad"] = "text" } }), "invalid", "numeric input");
            var session = server.Host.ResolveSession(sessionId);
            var task = await session.RunJobAsync(1, "workspace-snapshot");
            JsonNode? detail = null;
            await WaitUntilAsync(async () =>
            {
                detail = await server.Get($"/v1/sessions/{sessionId}/executions/{task.Value}");
                return detail?["status"]?.GetValue<string>() == "completed";
            }, "execution completed");
            Equal("passed", detail!["run"]!["audit"]!["status"]!.GetValue<string>(), "audit in detail");
            Check(detail["tasks"]!.AsArray().Count > 0, "task records");
            Check(detail["usage"] is JsonObject, "recorded usage");
            var history = await server.Client.ExecutionsAsync(sessionId, "job", "completed", "Workspace", 1);
            Equal(task.Value, history!["items"]![0]!["id"]!.GetValue<long>(), "filter by job and status");
            await FailsAsync(() => server.Get($"/v1/sessions/{sessionId}/executions?limit=0"), "invalid", "limit");
            await FailsAsync(() => server.Get($"/v1/sessions/{sessionId}/executions?kind=unknown"), "invalid", "kind");
            await FailsAsync(() => server.Get($"/v1/sessions/{sessionId}/executions?cursor=invalid"), "invalid", "malformed cursor");
            await FailsAsync(() => server.Get($"/v1/sessions/{sessionId}/executions/999999"), "not_found", "unknown execution");
            await server.Post($"/v1/sessions/{sessionId}/agents/main/messages?wait=5000", new JsonObject { ["content"] = "Hello history", ["requestId"] = "history-chat" });
            JsonNode? chats = null;
            await WaitUntilAsync(async () =>
            {
                chats = await server.Get($"/v1/sessions/{sessionId}/executions?kind=chat&status=completed");
                return chats!["items"]!.AsArray().Count > 0;
            }, "chat in execution history");
            var chat = await server.Get($"/v1/sessions/{sessionId}/executions/{chats!["items"]![0]!["id"]!.GetValue<long>()}");
            Check(chat!["entries"]!.AsArray().Count > 0, "chat generations are present");
        }),
    ];
}
