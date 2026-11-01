using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> JobReplayCases()
    {
        foreach (var sample in new[] { SampleJobs.WorkspaceSnapshot(), SampleJobs.ReleaseCheck() })
            yield return Case("jobs.replay." + sample.Id + "-after-state-write", async () =>
            {
                var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var attempts = 0;
                var phaseId = sample.Id == "release-check" ? "publish" : "save";
                var job = sample with { Phases = sample.Phases.Select(phase => phase.Id != phaseId ? phase : phase with
                {
                    Run = async context =>
                    {
                        var result = await phase.Run!(context);
                        if (Interlocked.Increment(ref attempts) == 1)
                        {
                            saved.TrySetResult();
                            await Task.Delay(Timeout.Infinite, context.Cancellation);
                        }
                        return result;
                    },
                }).ToArray() };
                var setup = new DeviceSetup { Jobs = [job] };
                await using var device = await TestDevice.OpenAsync("job-state-replay", setup);
                var session = await device.CreateAsync("replay");
                var task = await session.RunJobAsync(1, job.Id);
                if (job.NeedsApproval)
                {
                    await WaitForRunAsync(session, task.Value, "waiting");
                    await session.DecideJobAsync(1, task.Value, true);
                }
                await saved.Task.WaitAsync(Answer);
                var firstState = AppStateDocument.Read(device.Host.AppState.Get().Value, job.ResultPath!);
                await device.ReopenAsync(setup);
                var completed = await WaitForRunAsync(device.Host.ResolveSession(session.Id), task.Value, "completed");
                Check(attempts >= 2, "the interrupted phase ran again");
                Check(JsonValue.DeepEquals(firstState, completed["result"]), "the phase result matches its originally persisted state");
            });

        yield return Case("jobs.replay.multiple-subagent-questions-have-distinct-submissions", async () =>
        {
            var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var job = new JobDefinition
            {
                Id = "two-questions", Title = "Two questions", Summary = "Ask two questions in one phase.",
                Phases = [new JobPhase("ask", "Ask", "Ask twice") { Run = async context =>
                {
                    var first = await context.AskAgentAsync("first question");
                    var second = await context.AskAgentAsync("second question");
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        answered.TrySetResult();
                        await Task.Delay(Timeout.Infinite, context.Cancellation);
                    }
                    return JobPhaseResult.Next("answered", JsonValue.Object([new("first", first), new("second", second)]));
                }}],
            };
            var setup = new DeviceSetup { Jobs = [job] };
            await using var device = await TestDevice.OpenAsync("job-questions", setup);
            var session = await device.CreateAsync("questions");
            var task = await session.RunJobAsync(1, job.Id);
            await answered.Task.WaitAsync(Answer);
            await device.ReopenAsync(setup);
            var run = await WaitForRunAsync(device.Host.ResolveSession(session.Id), task.Value, "completed");
            var result = (JsonObjectValue)run["result"];
            Equal("Echo: first question", Jobs.String(result, "first"), "first answer");
            Equal("Echo: second question", Jobs.String(result, "second"), "second answer");
        });

        foreach (var wait in new[] { false, true })
            yield return Case($"jobs.replay.agent-started-{wait}-has-one-transcript-card", async () =>
            {
                await using var device = await TestDevice.OpenAsync("job-card");
                var session = await device.CreateAsync("card");
                await session.SendAsync(1, new SendRequest("tool: run_durable_job {\"job\":\"workspace-snapshot\",\"wait\":" +
                    (wait ? "true" : "false") + "}", "start-job") { Wait = Answer });
                var run = (JsonObjectValue)(await session.JobRunsAsync(1))[0];
                var id = Jobs.Long(run, "id")!.Value;
                await WaitForRunAsync(session, id, "completed");
                var anchors = (await AllEntriesAsync(session, 1)).Where(entry => entry.Kind == "pisharp.job").ToArray();
                Equal(1, anchors.Length, "one card anchor");
                Equal(id, Jobs.Long((JsonObjectValue)anchors[0].Data!, "taskId"), "card identifies the job task");
            });
    }
}
