using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;

internal static partial class Program
{
    private sealed class ScheduleClock : TimeProvider
    {
        private long milliseconds = 1_800_000_000_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref milliseconds));
        public void Advance(int minutes) => Interlocked.Add(ref milliseconds, minutes * 60_000L);
    }

    private static JobDefinition CheckedJob(string id = "checked", bool? check = true) => new()
    {
        Id = id, Title = id, Summary = "A checked test job", RequiredChecks = ["evidence"],
        Phases = [new JobPhase("work", "Work", "Record evidence") { Run = async context =>
        {
            if (check is { } passed) await context.CheckAsync("evidence", passed, "Test evidence");
            return JobPhaseResult.Next("Finished");
        }}],
    };

    internal static IEnumerable<(string, Func<Task>)> AutomationCases()
    {
        yield return Case("automation.slots-deduplicate-and-recover-before-catalog-ack", async () =>
        {
            var time = new ScheduleClock();
            var setup = new DeviceSetup { Time = time, Jobs = [CheckedJob()] };
            await using var device = await TestDevice.OpenAsync("schedule-receipt", setup);
            var session = await device.CreateAsync();
            var schedule = await device.Host.CreateScheduleAsync(session.Id, 1, "checked", 1);
            Equal("not_due", (await device.Host.TickScheduleAsync(schedule.Id)).Status, "first run waits an interval");
            time.Advance(1);
            var ticks = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => device.Host.TickScheduleAsync(schedule.Id)));
            Equal(1, ticks.Count(tick => tick.Status == "started"), "one admission in a slot");
            var task = ticks.First(tick => tick.Status == "started").TaskId!.Value;
            await WaitForRunAsync(session, task, "completed");
            await device.Host.DisposeAsync();
            CatalogSql(device, "UPDATE job_schedules SET value = json_set(value, '$.lastSlot', 0, '$.lastTaskId', null);");
            await device.ReopenAsync(setup);
            var replay = await device.Host.TickScheduleAsync(schedule.Id);
            Equal(task, replay.TaskId, "the committed receipt survives a lost catalog acknowledgement");
            Equal(1, ((JsonArrayValue)(await device.Host.ResolveSession(session.Id).ExecutionsAsync())["items"]).Count, "no duplicate job");
        });

        yield return Case("automation.pause-coalescing-interval-edit-delete-and-credentials", async () =>
        {
            var time = new ScheduleClock();
            var setup = new DeviceSetup { Time = time, Jobs = [CheckedJob()] };
            await using var device = await TestDevice.OpenAsync("schedule-management", setup);
            var session = await device.CreateAsync();
            var schedule = await device.Host.CreateScheduleAsync(session.Id, 1, "checked", 1);
            var token = await device.Host.IssueTriggerCredentialAsync("cron", schedule.Id, 1);
            await device.Host.UpdateScheduleAsync(schedule.Id, false);
            time.Advance(120);
            Equal("paused", (await device.Host.TickScheduleAsync(schedule.Id)).Status, "paused across missed intervals");
            Equal<TriggerCredential?>(null, device.Host.AuthenticateTriggerCredential(token.Token), "expiry uses device clock");
            await device.Host.UpdateScheduleAsync(schedule.Id, true);
            var tick = await device.Host.TickScheduleAsync(schedule.Id);
            Equal(120L, tick.Slot, "missed intervals coalesce to the current slot");
            await WaitForRunAsync(session, tick.TaskId!.Value, "completed");
            Equal(1, ((JsonArrayValue)(await session.ExecutionsAsync())["items"]).Count, "no catch-up flood");
            await device.Host.UpdateScheduleAsync(schedule.Id, true, 10);
            Equal("not_due", (await device.Host.TickScheduleAsync(schedule.Id)).Status, "changing the interval resets the clock");
            var fresh = await device.Host.IssueTriggerCredentialAsync("new", schedule.Id);
            await device.ReopenAsync(setup);
            Check(device.Host.AuthenticateTriggerCredential(fresh.Token) is not null, "credential survives restart");
            await device.Host.DeleteScheduleAsync(schedule.Id);
            Equal(0, device.Host.Schedules().Count, "deleted");
            Equal<TriggerCredential?>(null, device.Host.AuthenticateTriggerCredential(fresh.Token), "deleting a schedule revokes its credentials");
            await ThrowsAsync<HostInvalidException>(() => device.Host.CreateScheduleAsync(session.Id, 1, "checked", 0), "invalid interval");
        });

        yield return Case("automation.overlap-skips-and-failed-runs-do-not-block-future-slots", async () =>
        {
            var time = new ScheduleClock();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new JobDefinition { Id = "gate", Title = "Gate", Summary = "Wait then fail", Phases = [new JobPhase("gate", "Gate", "Wait")
            { Run = async context => { entered.TrySetResult(); await release.Task.WaitAsync(context.Cancellation); throw new InvalidOperationException("Test failure"); } }] };
            await using var device = await TestDevice.OpenAsync("schedule-overlap", new DeviceSetup { Time = time, Jobs = [job] });
            var session = await device.CreateAsync();
            var schedule = await device.Host.CreateScheduleAsync(session.Id, 1, "gate", 1);
            time.Advance(1);
            var first = await device.Host.TickScheduleAsync(schedule.Id);
            await entered.Task.WaitAsync(Answer);
            time.Advance(1);
            Equal("skipped", (await device.Host.TickScheduleAsync(schedule.Id)).Status, "active previous run skips its next slot");
            release.TrySetResult();
            await WaitForRunAsync(session, first.TaskId!.Value, "failed");
            time.Advance(1);
            Equal("started", (await device.Host.TickScheduleAsync(schedule.Id)).Status, "a failed job does not stall the schedule");
        });

        yield return Case("automation.host-ticks-and-archive-cleans-up", async () =>
        {
            var time = new ScheduleClock();
            await using var device = await TestDevice.OpenAsync("schedule-loop", new DeviceSetup { Time = time, RunSchedules = true, Jobs = [CheckedJob()] });
            var session = await device.CreateAsync();
            var schedule = await device.Host.CreateScheduleAsync(session.Id, 1, "checked", 1);
            var credential = await device.Host.IssueTriggerCredentialAsync("bound", schedule.Id);
            time.Advance(1);
            using var limit = new CancellationTokenSource(Answer);
            while (device.Host.Schedules().Single().LastSlot == 0) await Task.Delay(20, limit.Token);
            await device.Host.ArchiveSessionAsync(session.Id);
            Equal(0, device.Host.Schedules().Count, "archive removes schedules");
            Equal<TriggerCredential?>(null, device.Host.AuthenticateTriggerCredential(credential.Token), "archive revokes bound tokens");
        });

        yield return Case("automation.interval-edit-after-lost-ack-cannot-overlap", async () =>
        {
            var time = new ScheduleClock();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new JobDefinition { Id = "pending", Title = "Pending", Summary = "Wait", Phases = [new JobPhase("wait", "Wait", "Wait")
            { Run = async context => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, context.Cancellation); return JobPhaseResult.Next("unused"); } }] };
            var setup = new DeviceSetup { Time = time, Jobs = [job] };
            await using var device = await TestDevice.OpenAsync("schedule-revision-overlap", setup);
            var session = await device.CreateAsync();
            var schedule = await device.Host.CreateScheduleAsync(session.Id, 1, "pending", 1);
            time.Advance(1);
            var first = await device.Host.TickScheduleAsync(schedule.Id);
            await entered.Task.WaitAsync(Answer);
            await device.Host.DisposeAsync();
            CatalogSql(device, "UPDATE job_schedules SET value = json_set(value, '$.lastSlot', 0, '$.lastTaskId', null);");
            await device.ReopenAsync(setup);
            await device.Host.UpdateScheduleAsync(schedule.Id, true, 2);
            time.Advance(2);
            var next = await device.Host.TickScheduleAsync(schedule.Id);
            Equal("skipped", next.Status, "the old revision's live task is still found");
            Equal(first.TaskId, next.TaskId, "same active execution");
        });

        foreach (var check in new bool?[] { true, false, null })
            yield return Case("automation.audit-" + (check?.ToString() ?? "missing"), async () =>
            {
                await using var device = await TestDevice.OpenAsync("job-audit", new DeviceSetup { Jobs = [CheckedJob(check: check)] });
                var session = await device.CreateAsync();
                var task = await session.RunJobAsync(1, "checked");
                var run = await WaitForRunAsync(session, task.Value, check == true ? "completed" : "failed");
                Equal(check == true ? "passed" : "failed", Jobs.String((JsonObjectValue)run["audit"], "status"), "audit verdict");
                var detail = await session.ExecutionAsync(task.Value);
                Equal(check == true ? "completed" : "failed", Jobs.String(detail, "status"), "task and application outcomes agree");
            });

        yield return Case("automation.history-survives-restart-and-card-window-pagination", async () =>
        {
            var setup = new DeviceSetup { Jobs = [CheckedJob()] };
            await using var device = await TestDevice.OpenAsync("execution-history", setup);
            var session = await device.CreateAsync();
            long first = 0;
            for (var index = 0; index < 35; index++)
            {
                var task = await session.RunJobAsync(1, "checked");
                if (index == 0) first = task.Value;
                await WaitForRunAsync(session, task.Value, "completed");
            }
            Equal(30, (await session.JobRunsAsync(1)).Count, "cards remain bounded");
            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            var old = await session.ExecutionAsync(first);
            Equal("passed", Jobs.String((JsonObjectValue)((JsonObjectValue)old["run"])["audit"], "status"), "old full record retained");
            var ids = new HashSet<long>();
            JsonObjectValue? cursor = null;
            do
            {
                var page = await session.ExecutionsAsync(kind: "job", status: "completed", query: "checked", limit: 7, cursor: cursor);
                foreach (var item in (JsonArrayValue)page["items"]) Check(ids.Add(Jobs.Long((JsonObjectValue)item, "id")!.Value), "no duplicate page item");
                cursor = page["next"] as JsonObjectValue;
            } while (cursor is not null);
            Equal(35, ids.Count, "every execution is reachable");
        });

        yield return Case("automation.history-includes-subagent-tasks-and-model-entries", async () =>
        {
            var job = new JobDefinition { Id = "research", Title = "Research", Summary = "Ask", Phases = [new JobPhase("ask", "Ask", "Research")
            { Run = async context => JobPhaseResult.Next(await context.AskAgentAsync("evidence")) }] };
            await using var device = await TestDevice.OpenAsync("history-children", new DeviceSetup { Jobs = [job] });
            var session = await device.CreateAsync();
            var task = await session.RunJobAsync(1, "research");
            await WaitForRunAsync(session, task.Value, "completed");
            var detail = await session.ExecutionAsync(task.Value);
            Check(((JsonArrayValue)detail["tasks"]).Count > 1, "descendant tasks are present");
            Check(((JsonArrayValue)detail["entries"]).OfType<JsonObjectValue>().Any(entry => Jobs.String(entry, "kind") == "pi.assistant"), "child assistant answer is present");
        });

        yield return Case("automation.pending-approval-survives-card-window-eviction", async () =>
        {
            var approval = new JobDefinition { Id = "approval", Title = "Approval", Summary = "Wait for approval",
                Phases = [new JobPhase("approve", "Approve", "Approve", JobPhaseKind.Approval)] };
            await using var device = await TestDevice.OpenAsync("history-approval", new DeviceSetup { Jobs = [CheckedJob(), approval] });
            var session = await device.CreateAsync();
            var pending = await session.RunJobAsync(1, "approval");
            await WaitForRunAsync(session, pending.Value, "waiting");
            for (var index = 0; index < 31; index++)
            {
                var task = await session.RunJobAsync(1, "checked");
                await WaitForRunAsync(session, task.Value, "completed");
            }
            Check(!(await session.JobRunsAsync(1)).OfType<JsonObjectValue>().Any(run => Jobs.Long(run, "id") == pending.Value), "the pending card was evicted");
            await session.DecideJobAsync(1, pending.Value, true);
            using var timeout = new CancellationTokenSource(Answer);
            JsonObjectValue detail;
            do
            {
                detail = await session.ExecutionAsync(pending.Value, timeout.Token);
                if (Jobs.String(detail, "status") == "completed") break;
                await Task.Delay(10, timeout.Token);
            } while (true);
            Equal("passed", Jobs.String((JsonObjectValue)((JsonObjectValue)detail["run"])["audit"], "status"), "full record continues after approval");
        });
    }
}
