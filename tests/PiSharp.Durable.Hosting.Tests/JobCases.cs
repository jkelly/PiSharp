using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> JobCases() =>
    [
        Case("jobs.workspace-snapshot-completes-and-saves-its-result", async () =>
        {
            await using var device = await TestDevice.OpenAsync("jobs-snapshot");
            File.WriteAllText(Path.Join(device.Work, "a.cs"), "class A {}");
            File.WriteAllText(Path.Join(device.Work, "b.md"), "# b");
            var session = await device.CreateAsync("snapshot");
            var task = await session.RunJobAsync(1, "workspace-snapshot");
            var run = await WaitForRunAsync(session, task.Value, "completed");
            Equal("4", string.Join(",", Jobs.Items(run, "phases").OfType<JsonObjectValue>().Count(phase => Jobs.String(phase, "status") == "done")), "four phases done");
            Equal("you", Jobs.String(run, "startedBy"), "started by you");
            var saved = (JsonObjectValue)AppStateDocument.Read(device.Host.AppState.Get().Value, "/jobs/workspace-snapshot")!;
            Equal("2", saved["files"].ToJson(), "files counted");
            Check((await AllEntriesAsync(session, 1)).Any(entry => entry.Kind == "pisharp.job"), "an anchor entry for the card");
            Check(device.Host.Jobs.Count == 3, "three sample jobs");
        }),

        Case("jobs.an-approval-waits-through-a-restart", async () =>
        {
            await using var device = await TestDevice.OpenAsync("jobs-approval");
            var session = await device.CreateAsync("release");
            var task = await session.RunJobAsync(1, "release-check", JsonValue.Object([new("commits", 3.0)]));
            var waiting = await WaitForRunAsync(session, task.Value, "waiting");
            Equal("approve", Jobs.String(waiting, "phase"), "it waits in the approval phase");
            Check((await session.AgentsAsync()).Any(agent => agent.Kind == AgentKind.Subagent), "the gather phase asked a subagent");

            await device.ReopenAsync();
            var reopened = device.Host.ResolveSession(session.Id);
            await WaitForRunAsync(reopened, task.Value, "waiting");
            await reopened.DecideJobAsync(1, task.Value, approved: true);
            await reopened.DecideJobAsync(1, task.Value, approved: true);
            var done = await WaitForRunAsync(reopened, task.Value, "completed");
            Equal("approved", Jobs.String((JsonObjectValue)done["approval"], "status"), "approval");
            Check(AppStateDocument.Read(device.Host.AppState.Get().Value, "/jobs/release-check") is JsonObjectValue, "published to App State");
            await ThrowsAsync<AppStateConflictException>(() => reopened.DecideJobAsync(1, task.Value, approved: false), "deciding a finished run");
        }),

        Case("jobs.decline-and-stop", async () =>
        {
            await using var device = await TestDevice.OpenAsync("jobs-decline");
            var session = await device.CreateAsync("decline");
            var release = await session.RunJobAsync(1, "release-check");
            await WaitForRunAsync(session, release.Value, "waiting");
            await session.DecideJobAsync(1, release.Value, approved: false);
            var declined = await WaitForRunAsync(session, release.Value, "declined");
            Equal("skipped", Jobs.String(Jobs.Items(declined, "phases").OfType<JsonObjectValue>().Last(), "status"), "later phases are skipped");
            Check(AppStateDocument.Read(device.Host.AppState.Get().Value, "/jobs/release-check") is null, "nothing published");

            var watch = await session.RunJobAsync(1, "change-watch", JsonValue.Object([new("checks", 5.0), new("interval_seconds", 60.0)]));
            await WaitUntilAsync(async () => await RunAsync(session, watch.Value) is { } run && Jobs.String(run, "phase") == "watch", "the watch phase started");
            await session.AbortTaskAsync(watch.Value);
            await WaitForRunAsync(session, watch.Value, "aborted");
        }),

        Case("jobs.an-agent-runs-a-job-and-waits-for-it", async () =>
        {
            await using var device = await TestDevice.OpenAsync("jobs-tool");
            var session = await device.CreateAsync("tool");
            var listed = await session.SendAsync(1, new SendRequest("tool: list_durable_jobs {}", "jt-1") { Wait = Answer });
            Check(listed.AnswerText?.Contains("workspace-snapshot") == true, "listed: " + listed.AnswerText);
            var reply = await session.SendAsync(1, new SendRequest("""tool: run_durable_job {"job":"workspace-snapshot","wait":true}""", "jt-2") { Wait = Answer });
            Check(reply.AnswerText?.Contains("Workspace snapshot run #1") == true && reply.AnswerText.Contains("completed"), "waited: " + reply.AnswerText);
            var run = (JsonObjectValue)(await session.JobRunsAsync(1))[0];
            Equal("agent", Jobs.String(run, "startedBy"), "started by the agent");
            var checkReply = await session.SendAsync(1, new SendRequest("""tool: check_durable_job {"job":"workspace-snapshot"}""", "jt-3") { Wait = Answer });
            Check(checkReply.AnswerText?.Contains("completed") == true, "check: " + checkReply.AnswerText);
        }),
    ];

    internal static async Task<JsonObjectValue?> RunAsync(HostedSession session, long taskId) =>
        (await session.JobRunsAsync(1)).OfType<JsonObjectValue>().FirstOrDefault(run => Jobs.Long(run, "id") == taskId);

    internal static async Task<JsonObjectValue> WaitForRunAsync(HostedSession session, long taskId, string status)
    {
        JsonObjectValue? run = null;
        await WaitUntilAsync(async () =>
        {
            run = await RunAsync(session, taskId);
            if (run is not null && Jobs.String(run, "status") is "failed" && status != "failed")
                throw new InvalidOperationException("The job failed: " + run.ToJson());
            return run is not null && Jobs.String(run, "status") == status;
        }, $"job {taskId} is {status}");
        return run!;
    }
}
