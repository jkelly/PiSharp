using System.ComponentModel;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;

internal sealed record RiskVerdict([property: Description("low, medium or high")] string Level, [property: Description("Why")] string Reason, int Score);

internal sealed class ReviewData
{
    public string OrderId { get; set; } = "";
    public string? Summary { get; set; }
    public RiskVerdict? Risk { get; set; }
    public List<string> Tags { get; set; } = [];
    public int Count { get; set; }
}

internal sealed class Node
{
    public string Name { get; set; } = "";
    public List<Node> Children { get; set; } = [];
}

// Decision 0012, P3: model-call actions: Prompt and PromptAsync (text), Prompt<T> and PromptAsync<T> (structured output through the
// submit_result tool), exactly once per attempt across restarts, fresh children per visit, timeouts, and a missing model.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> PromptCases()
    {
        yield return Case("prompt.text", async () =>
        {
            var models = new FauxAgentModels().On(request => request.LastUserText!.StartsWith("Summarize", StringComparison.Ordinal),
                request => "Order " + request.LastUserText!.Split(' ')[^1] + " is fine.");
            var workflow = Workflow.For<ReviewData>("summary").WithInstructions("You review orders.")
                .Prompt("summarize", d => $"Summarize order {d.OrderId}", (d, answer) => d.Summary = answer)
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), models: models);
            var result = await host.Runner.RunAsync(workflow, new ReviewData { OrderId = "42" });
            Equal("Order 42 is fine.", result.EnsureCompleted().Summary, "answer applied");
            Equal(1, models.RequestCount, "one request");
            var request = models.Requests[0];
            Equal(0, request.Tools.Count, "a plain prompt offers no tools");
            Check(request.SystemText.Contains("You review orders."), "workflow instructions: " + request.SystemText);
            // The child conversation belongs to the run's task.
            var record = await host.Harness.GetTaskAsync(result.TaskId, TestHost.Context);
            var owned = await host.Harness.CommitAsync(tx => tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = result.TaskId }, 10),
                TestHost.Context);
            Equal(1, owned.Items.Length, "one child conversation, owned by the run");
            Check(owned.Items[0].Id != record!.ConversationId, "a separate conversation");
        });

        yield return Case("prompt.typed-submit-result", async () =>
        {
            var models = new FauxAgentModels().On(request => request.Tools.Contains("submit_result"),
                _ => FauxReply.ToolCall("submit_result", """{"level":"high","reason":"new customer","score":7}"""));
            var workflow = Workflow.For<ReviewData>("risk")
                .Prompt<RiskVerdict>("assess", d => "Assess " + d.OrderId, (d, verdict) => d.Risk = verdict)
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), models: models);
            var data = (await host.Runner.RunAsync(workflow, new ReviewData { OrderId = "9" })).EnsureCompleted();
            Equal(new RiskVerdict("high", "new customer", 7), data.Risk, "typed result");
            Equal(1, models.RequestCount, "the terminating tool ends the run without another request");
            Equal("submit_result", string.Join(",", models.Requests[0].Tools), "only submit_result is offered");
        });

        yield return Case("prompt.typed-repair", async () =>
        {
            var models = new FauxAgentModels()
                .On(request => request.LastToolName == "submit_result" && request.LastToolResult!.Contains("must have required", StringComparison.Ordinal),
                    _ => FauxReply.ToolCall("submit_result", """{"level":"low","reason":"repeat buyer","score":1}"""))
                .On(request => request.AfterTools is false && request.LastUserText!.StartsWith("Assess", StringComparison.Ordinal),
                    _ => FauxReply.ToolCall("submit_result", """{"level":"low"}"""));
            var workflow = Workflow.For<ReviewData>("repair")
                .Prompt<RiskVerdict>("assess", d => "Assess " + d.OrderId, (d, verdict) => d.Risk = verdict)
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), models: models);
            var data = (await host.Runner.RunAsync(workflow, new ReviewData { OrderId = "9" })).EnsureCompleted();
            Equal("repeat buyer", data.Risk!.Reason, "the harness's validation feedback repaired the call");
            Equal(2, models.RequestCount, "two requests");
        });

        yield return Case("prompt.typed-text-fallback", async () =>
        {
            var models = new FauxAgentModels()
                .On(request => request.LastUserText!.StartsWith("Tags", StringComparison.Ordinal), _ => """["a","b"]""")
                .On(request => request.LastUserText!.StartsWith("Your answer could not be used", StringComparison.Ordinal),
                    _ => "Sure:\n```json\n{\"level\":\"medium\",\"reason\":\"fenced\",\"score\":4}\n```")
                .On(_ => true, _ => "I think it is risky.");
            var workflow = Workflow.For<ReviewData>("fallback")
                .Prompt<RiskVerdict>("assess", d => "Assess " + d.OrderId, (d, verdict) => d.Risk = verdict)
                .Prompt<List<string>>("tags", d => "Tags for " + d.OrderId, (d, tags) => d.Tags = tags)
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), models: models);
            var data = (await host.Runner.RunAsync(workflow, new ReviewData { OrderId = "1" })).EnsureCompleted();
            Equal("fenced", data.Risk!.Reason, "repair round, fenced JSON");
            Equal("a,b", string.Join(",", data.Tags), "a wrapped array result from bare JSON");
        });

        yield return Case("prompt.typed-schema-shapes", () =>
        {
            var verdict = ResultTool.Create("w", typeof(RiskVerdict), DataCodec.DefaultOptions);
            Check(!verdict.Schema.Wrapped, "an object is not wrapped");
            var json = verdict.Schema.Schema.ToJson();
            Check(json.Contains("\"level\"") && json.Contains("low, medium or high") && json.Contains("\"integer\""), "schema: " + json);
            Check(!json.Contains("\"string\",\"integer\""), "numbers are strict");
            Check(ResultTool.Create("w", typeof(List<string>), DataCodec.DefaultOptions).Schema.Wrapped, "an array is wrapped");
            Check(ResultTool.Create("w", typeof(int), DataCodec.DefaultOptions).Schema.Wrapped, "a number is wrapped");
            Throws<WorkflowDefinitionException>(() => Workflow.For<ReviewData>("recursive").DeclareResult<Node>().Then("a", _ => { }).Build(), "recursive type");
        });

        yield return Case("prompt.code-action-keys", async () =>
        {
            var models = new FauxAgentModels().On(_ => true, request => "echo:" + request.LastUserText);
            var workflow = Workflow.For<ReviewData>("multi")
                .DeclareResult<RiskVerdict>()
                .Then("ask", async (data, context, token) =>
                {
                    data.Tags.Add(await context.PromptAsync("one", cancellationToken: token));
                    data.Tags.Add(await context.PromptAsync("two", cancellationToken: token));
                    data.Tags.Add(await context.PromptAsync("three", new PromptOptions { Key = "named" }, token));
                })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), models: models);
            var data = (await host.Runner.RunAsync(workflow, new ReviewData())).EnsureCompleted();
            Equal("echo:one,echo:two,echo:three", string.Join(",", data.Tags), "answers in order");
            var undeclared = Workflow.For<ReviewData>("undeclared").Then("ask", async (d, c, t) => await c.PromptAsync<List<int>>("x", cancellationToken: t)).Build();
            await using var other = await TestHost.OpenAsync(catalog => catalog.Add(undeclared), models: models);
            var failed = await other.Runner.RunAsync(undeclared, new ReviewData());
            Check(failed.Status == WorkflowRunStatus.Failed && failed.Error!.Contains("DeclareResult"), "undeclared result type: " + failed.Error);
        });

        yield return Case("prompt.loop-fresh-visit", async () =>
        {
            var models = new FauxAgentModels().On(_ => true, request => "n" + request.Messages.Count(message => message is PiSharp.Durable.Chord.JsonObjectValue o &&
                o.TryGetValue("role", out var role) && role is PiSharp.Durable.Chord.JsonStringValue { Value: "user" }));
            var workflow = Workflow.For<ReviewData>("loop")
                .Prompt("ask", d => "count " + d.Count, (d, answer) => { d.Tags.Add(answer); d.Count++; })
                .Then("again", (d, _, _) => Task.FromResult(d.Count < 3 ? ActionResult.GoTo("ask") : ActionResult.Next()))
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), models: models);
            var data = (await host.Runner.RunAsync(workflow, new ReviewData())).EnsureCompleted();
            Equal("n1,n1,n1", string.Join(",", data.Tags), "each visit asks in a fresh conversation");
        });

        yield return Case("prompt.exactly-once-after-answer", async () =>
        {
            var path = TestHost.SqlitePath("prompt-once");
            var models = new FauxAgentModels().On(_ => true, _ => "answer");
            var hang = true;
            var entered = new TaskCompletionSource();
            var workflow = Workflow.For<ReviewData>("once-after")
                .Then("ask", async (data, context, token) =>
                {
                    data.Summary = await context.PromptAsync("question", cancellationToken: token);
                    if (hang)
                    {
                        entered.TrySetResult();
                        await Task.Delay(Timeout.Infinite, token);
                    }
                })
                .Build();
            WorkflowRun<ReviewData> run;
            await using (var first = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path), models))
            {
                run = await first.Runner.StartAsync(workflow, new ReviewData());
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            hang = false;
            await using (var second = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path), models))
                Equal("answer", (await second.Runner.Attach(workflow, run.TaskId, run.ConversationId).WaitAsync()).EnsureCompleted().Summary, "answer");
            Equal(1, models.RequestCount, "the rerun reuses the recorded answer");
        });

        yield return Case("prompt.exactly-once-mid-request", async () =>
        {
            var path = TestHost.SqlitePath("prompt-mid");
            var release = new TaskCompletionSource();
            var started = new TaskCompletionSource();
            var models = new FauxAgentModels().On(_ => true, _ =>
            {
                started.TrySetResult();
                return FauxReply.Answer("late answer").HeldUntil(release.Task);
            });
            var workflow = Workflow.For<ReviewData>("once-mid")
                .Prompt("ask", _ => "question", (d, answer) => d.Summary = answer)
                .Build();
            WorkflowRun<ReviewData> run;
            await using (var first = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path), models))
            {
                run = await first.Runner.StartAsync(workflow, new ReviewData());
                await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            release.SetResult();
            await using (var second = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path), models))
                Equal("late answer", (await second.Runner.Attach(workflow, run.TaskId, run.ConversationId).WaitAsync()).EnsureCompleted().Summary, "answer");
            Equal(2, models.RequestCount, "the interrupted generation asks again; the action does not submit twice");
            var submissions = 0;
            await using (var third = await TestHost.OpenAsync(_ => { }, await TestHost.SqliteAsync(path), models))
            {
                var children = await third.Harness.CommitAsync(tx => tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = run.TaskId }, 10), TestHost.Context);
                Equal(1, children.Items.Length, "one child");
                var page = await third.Harness.CommitAsync(tx => tx.GetSubmissionByRequestAsync(children.Items[0].Id, "wf:" + run.TaskId.Value + ":1:prompt:1"),
                    TestHost.Context);
                if (page is not null) submissions++;
            }
            Equal(1, submissions, "one submission with the deterministic request ID");
        });

        yield return Case("prompt.timeout-attempt", async () =>
        {
            var models = new FauxAgentModels()
                .On(_ => true, request => request.Index == 0 ? FauxReply.Answer("slow").HeldUntil(new TaskCompletionSource().Task) : "fast", once: false);
            var workflow = Workflow.For<ReviewData>("timeouts")
                .Prompt("ask", _ => "question", (d, answer) => d.Summary = answer, new PromptOptions { Timeout = TimeSpan.FromMilliseconds(200) },
                    new ActionOptions { Retry = RetryPolicy.Fixed(2) })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), models: models);
            var data = (await host.Runner.RunAsync(workflow, new ReviewData())).EnsureCompleted();
            Equal("fast", data.Summary, "the second attempt answers");
            Equal(2, models.RequestCount, "two requests");
        });

        yield return Case("prompt.model-missing", async () =>
        {
            var workflow = Workflow.For<ReviewData>("nomodel").WithModel(new ModelRef("faux", "missing-model"))
                .Prompt("ask", _ => "question", (d, answer) => d.Summary = answer)
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var result = await host.Runner.RunAsync(workflow, new ReviewData());
            Equal(WorkflowRunStatus.Failed, result.Status, "failed");
            Check(result.Error!.Contains("no_model"), "reason: " + result.Error);
        });
    }
}
