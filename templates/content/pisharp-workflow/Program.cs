using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable.Sqlite;

namespace MyPiSharpWorkflow;

public sealed class ReleaseRequest
{
    public string ReleaseId { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? ApprovedBy { get; set; }
    public bool Published { get; set; }
}

internal static class Program
{
    private static async Task<int> Main()
    {
        // FauxAgentModels runs offline out of the box; swap in NativeDurableModels to call a live provider.
        var models = new FauxAgentModels("scripted");
        models.On(_ => true, _ => "Release checklist passed: all automated gates green.");

        var workflow = Workflow.For<ReleaseRequest>("release-approval")
            .Prompt("summarize",
                data => $"Summarize readiness for release {data.ReleaseId}.",
                (data, summary) => data.Summary = summary)
            .WaitForSignal<string>("approve", "approved",
                (data, approver) => data.ApprovedBy = approver)
            .Then("publish", (data, _, _) =>
            {
                data.Published = true;
                return Task.CompletedTask;
            })
            .Build();

        var team = new AgentTeam(new AgentTeamOptions
        {
            Models = models,
            DefaultModel = models.Model(),
        });
        team.Workflows.Add(workflow);

        var dataDir = Path.Combine(AppContext.BaseDirectory, "workflow-data");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "workflow.sqlite");

        var storage = await SqliteStorage.OpenAsync(dbPath);
        await using var session = await team.OpenAsync(storage);

        var runKey = $"release-{Guid.NewGuid():N}";
        var run = await session.Workflows.StartAsync(
            workflow,
            new ReleaseRequest { ReleaseId = "v1.0.0" },
            new WorkflowStartOptions { RunKey = runKey });

        // Wait until the workflow durably parks at the approval signal gate, then deliver the signal.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await run.StatusAsync();
            if (status.CurrentAction == "approve") break;
            await Task.Delay(25);
        }

        await run.SignalAsync("approved", "ops-lead", id: "approval-1");
        var completed = (await run.WaitAsync().WaitAsync(TimeSpan.FromSeconds(20))).EnsureCompleted();

        Console.WriteLine($"Workflow completed: release={completed.ReleaseId}, approvedBy={completed.ApprovedBy}, published={completed.Published}");
        Console.WriteLine($"Summary: {completed.Summary}");
        return completed.Published ? 0 : 1;
    }
}
