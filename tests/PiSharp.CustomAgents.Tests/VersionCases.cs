using System.Text.Json.Nodes;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;

// Decision 0012, P4: versioning. A run parked at an action survives a reopen under a changed definition only through a version bump and
// a migration; otherwise durable blocks it (migration_failed, task_too_old) or the engine fails it (missing_action).
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> VersionCases()
    {
        yield return Case("version.migrate-rename", async () =>
        {
            var path = TestHost.SqlitePath("migrate");
            var parked = await ParkAsync(path, Versioned(1, "wait"));
            var v2 = Versioned(2, "pause", WorkflowMigration.Compose(
                WorkflowMigration.RenameActions(("wait", "pause")),
                WorkflowMigration.TransformData((node, from) =>
                {
                    node!["note"] = "migrated from v" + from;
                    return node;
                })));
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(v2), await TestHost.SqliteAsync(path));
            var data = (await WithDiagnosticsAsync(host, parked.TaskId, host.Runner.Attach(v2, parked.TaskId, parked.ConversationId).WaitAsync()))
                .EnsureCompleted();
            Equal("first,pause,last", string.Join(",", data.Trail), "continues at the renamed action");
            Equal("migrated from v1", data.Note, "data migrated");
            Equal(2, (await host.Harness.GetTaskAsync(parked.TaskId, TestHost.Context))!.Version, "record version");
        });

        yield return Case("version.no-migrate-blocked", async () =>
        {
            var path = TestHost.SqlitePath("no-migrate");
            var parked = await ParkAsync(path, Versioned(1, "wait"));
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(Versioned(2, "wait")), await TestHost.SqliteAsync(path));
            await ExpectBlockedAsync(host, parked.TaskId, BlockedReason.MigrationFailed);
        });

        yield return Case("version.rollback-too-old", async () =>
        {
            var path = TestHost.SqlitePath("rollback");
            var parked = await ParkAsync(path, Versioned(2, "wait"));
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(Versioned(1, "wait")), await TestHost.SqliteAsync(path));
            await ExpectBlockedAsync(host, parked.TaskId, BlockedReason.TaskTooOld);
        });

        yield return Case("version.missing-action", async () =>
        {
            var path = TestHost.SqlitePath("missing-action");
            var parked = await ParkAsync(path, Versioned(1, "wait"));
            // Same version, but "wait" is gone: the engine fails the run instead of letting durable fault it.
            var dropped = Workflow.For<OrderData>("versioned").Then(new AppendAction("first")).Then(new AppendAction("last")).Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(dropped), await TestHost.SqliteAsync(path));
            var result = await host.Runner.Attach(dropped, parked.TaskId, parked.ConversationId).WaitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Equal(WorkflowRunStatus.Failed, result.Status, "status");
            Equal("missing_action", result.ErrorKind, "kind");
            Check(result.Error!.Contains("'wait'") && result.Error.Contains("bump Version"), "guidance: " + result.Error);
            Equal("first", string.Join(",", result.Data!.Trail), "data kept");
            lock (host.Reports) Check(host.Reports.Any(error => error.Message.Contains("changed shape")), "drift reported");
        });
    }

    private static volatile bool parking;

    /// <summary>A three-action workflow whose middle action (<paramref name="middle"/>) hangs while <see cref="parking"/> is set.</summary>
    private static Workflow<OrderData> Versioned(int version, string middle, WorkflowMigration? migration = null) =>
        Workflow.For<OrderData>("versioned").Version(version, migration)
            .Then(new AppendAction("first"))
            .Then(middle, async (data, _, token) =>
            {
                if (parking) await Task.Delay(Timeout.Infinite, token);
                data.Trail.Add(middle);
            })
            .Then(new AppendAction("last"))
            .Build();

    /// <summary>Starts a run of <paramref name="workflow"/> on a SQLite file and closes the host while it hangs in its middle action.</summary>
    private static async Task<WorkflowRun<OrderData>> ParkAsync(string path, Workflow<OrderData> workflow)
    {
        parking = true;
        try
        {
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path));
            var run = await host.Runner.StartAsync(workflow, new OrderData());
            await WaitUntilAsync(async () => (await run.StatusAsync()).History.Any(line => line.Action == "first") &&
                (await run.StatusAsync()).CurrentAction == workflow.Steps[1], TimeSpan.FromSeconds(20));
            await Task.Delay(100);
            return run;
        }
        finally { parking = false; }
    }

    /// <summary>Awaits <paramref name="wait"/>; on a timeout, fails with the task's inspection state and the Harness reports.</summary>
    private static async Task<T> WithDiagnosticsAsync<T>(TestHost host, TaskId id, Task<T> wait)
    {
        try { return await wait.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException)
        {
            var state = (await host.Harness.InspectAsync(TestHost.Context)).Tasks.FirstOrDefault(task => task.Record.Id == id);
            string reports;
            lock (host.Reports) reports = string.Join(" | ", host.Reports.Select(error => error.Message));
            throw new TimeoutException($"Task {id.Value} did not end: {state?.State.Kind} {state?.State.Reason} {state?.State.Error?.Message} " +
                $"checkpoint {state?.Record.State} reports [{reports}]");
        }
    }

    private static async Task ExpectBlockedAsync(TestHost host, TaskId id, BlockedReason reason)
    {
        TaskInspectionState? state = null;
        await WaitUntilAsync(async () =>
        {
            state = (await host.Harness.InspectAsync(TestHost.Context)).Tasks.FirstOrDefault(task => task.Record.Id == id)?.State;
            return state?.Kind == TaskInspectionKind.Blocked;
        }, TimeSpan.FromSeconds(20));
        Equal(reason, state!.Reason, "blocked reason");
        Equal(WorkflowRunStatus.Running, (await host.Runner.Attach(Versioned(1, "wait"), id, (await host.Harness.GetTaskAsync(id, TestHost.Context))!.ConversationId)
            .StatusAsync()).Status, "the run is still live, not failed");
    }
}
