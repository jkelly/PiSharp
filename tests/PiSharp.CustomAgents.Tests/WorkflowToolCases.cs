using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable.Storage;

// Decision 0012, P12: workflows as agent tools (run_workflow_<name>, workflow_status).
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> WorkflowToolCases()
    {
        yield return Case("agents.wf-tools.run-waits-and-reports", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var orders = Workflow.For<OrderData>("wf-orders")
                .Describe("Counts an order's trail")
                .Then(new AppendAction("load"))
                .Then("count", data => data.Count = data.Trail.Count)
                .Build();
            var slow = Workflow.For<OrderData>("wf-slow")
                .Then("hold", async (data, _, token) =>
                {
                    await gate.Task.WaitAsync(token);
                    data.Note = "released";
                })
                .Build();
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Workflows.Add(orders);
            team.Workflows.Add(slow);
            CallingTeamOn(team, models, new AgentDefinition
            {
                Name = "lead",
                Description = "Runs workflows",
                Tools =
                [
                    WorkflowTools.RunTool(team.Workflows, orders),
                    WorkflowTools.RunTool(team.Workflows, slow, new WorkflowToolOptions { Timeout = TimeSpan.FromMilliseconds(300) }),
                    WorkflowTools.RunTool(team.Workflows, orders, new WorkflowToolOptions { Name = "start_orders", Wait = false }),
                    WorkflowTools.RunTool(team.Workflows, orders, (OrderInput input) => new OrderData { OrderId = input.Id, Trail = ["mapped"] },
                        new WorkflowToolOptions { Name = "orders_by_id" }),
                    WorkflowTools.StatusTool(),
                ],
            });
            Check(WorkflowTools.RunTool(team.Workflows, orders).Parameters.ToJson().Contains("\"orderId\"", StringComparison.Ordinal), "the Data schema");
            var mappedSchema = WorkflowTools.RunTool(team.Workflows, orders, (OrderInput input) => new OrderData { OrderId = input.Id }).Parameters.ToJson();
            Check(mappedSchema.Contains("\"id\"", StringComparison.Ordinal) && !mappedSchema.Contains("trail", StringComparison.Ordinal), "the input's schema: " + mappedSchema);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var done = (await session.AskAsync("call run_workflow_wf_orders {\"orderId\":\"7\",\"trail\":[\"x\"]}")).Text;
            Check(done.StartsWith("Workflow 'wf-orders' run ", StringComparison.Ordinal) && done.Contains("completed after 2 steps", StringComparison.Ordinal) &&
                done.Contains("\"trail\":[\"x\",\"load\"]", StringComparison.Ordinal) && done.Contains("\"count\":2", StringComparison.Ordinal),
                "the call waited for the run and returned its Data: " + done);
            Equal(1, (await session.Workflows.ListAsync()).Count, "one run");
            var mapped = (await session.AskAsync("call orders_by_id {\"id\":\"9\"}")).Text;
            Check(mapped.Contains("\"orderId\":\"9\"", StringComparison.Ordinal) && mapped.Contains("\"trail\":[\"mapped\",\"load\"]", StringComparison.Ordinal),
                "the input was mapped to the run's Data: " + mapped);
            Check((await session.AskAsync("call orders_by_id {\"trail\":[]}")).Text.StartsWith("error: ", StringComparison.Ordinal),
                "arguments outside the input's schema are refused");
            var waiting = (await session.AskAsync("call run_workflow_wf_slow {}")).Text;
            Check(waiting.Contains("is still running; call workflow_status with task_id", StringComparison.Ordinal), "past the timeout: " + waiting);
            var slowId = waiting.Split("task_id ")[1].Split(' ')[0];
            var running = (await session.AskAsync("call workflow_status {\"task_id\":" + slowId + "}")).Text;
            Check(running.StartsWith($"Run {slowId} of 'wf-slow': running at 'hold'", StringComparison.Ordinal), "status of a running run: " + running);
            gate.SetResult();
            var started = (await session.AskAsync("call start_orders {\"orderId\":\"8\"}")).Text;
            Check(started.StartsWith("Started workflow 'wf-orders' as run ", StringComparison.Ordinal), "without waiting: " + started);
            await WaitUntilAsync(async () => (await session.AskAsync("call workflow_status {\"task_id\":" + slowId + "}")).Text.Contains("completed", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20));
            var final = (await session.AskAsync("call workflow_status {\"task_id\":" + slowId + "}")).Text;
            Check(final.Contains("\"note\":\"released\"", StringComparison.Ordinal), "a finished run's status shows its Data: " + final);
            Check((await session.AskAsync("call workflow_status {\"task_id\":1}")).Text.StartsWith("error: Task 1 is not a workflow run", StringComparison.Ordinal),
                "another task is refused");
        });
    }

    /// <summary>The input of a workflow tool that maps it to the run's Data.</summary>
    private sealed record OrderInput(string Id);

    /// <summary><see cref="CallingTeam"/> over an existing team.</summary>
    private static (AgentTeam Team, FauxAgentModels Models) CallingTeamOn(AgentTeam team, FauxAgentModels models, AgentDefinition lead)
    {
        team.Catalog.Register(lead);
        models.On(request => Is(request, "lead") && !request.AfterTools && request.LastUserText!.StartsWith("call ", StringComparison.Ordinal), request =>
        {
            var parts = request.LastUserText!.Split(' ', 3);
            return FauxReply.ToolCall(parts[1], parts.Length > 2 ? parts[2] : "{}");
        });
        models.On(request => Is(request, "lead") && request.AfterTools, request => (request.ToolResults[^1].IsError ? "error: " : "") + request.LastToolResult);
        models.On(request => Is(request, "lead"), request => "tools: " + string.Join(",", Own(request.Tools)));
        return (team, models);
    }
}
