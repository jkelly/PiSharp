using System.Text.Json;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

internal sealed class OrderData
{
    public string OrderId { get; set; } = "";
    public List<string> Trail { get; set; } = [];
    public int Count { get; set; }
    public bool Vip { get; set; }
    public long Big { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

internal sealed class AppendAction(string name) : IAction<OrderData>
{
    public string Name { get; } = name;
    public Task<ActionResult> ExecuteAsync(OrderData data, ActionContext context, CancellationToken cancellationToken)
    {
        data.Trail.Add(Name);
        return Task.FromResult(ActionResult.Next());
    }
}

// Decision 0012, P1: the workflow engine core: build validation, the Data codec, transitions, routers, limits, retries, abort, run keys.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> WorkflowCases()
    {
        yield return Case("build.validate", () =>
        {
            Throws<WorkflowDefinitionException>(() => Workflow.For<OrderData>("empty").Build(), "no steps");
            Throws<WorkflowDefinitionException>(() => Workflow.For<OrderData>("dup").Then(new AppendAction("a")).Then(new AppendAction("a")).Build(),
                "duplicate names");
            Throws<WorkflowDefinitionException>(() => Workflow.For<OrderData>("next").Then(new AppendAction("a"), new ActionOptions { Next = "nope" }).Build(),
                "unknown next");
            Throws<WorkflowDefinitionException>(() => Workflow.For<OrderData>("branch").Branch(d => d.Vip, "a", "b").Then(new AppendAction("a")).Build(),
                "unknown branch target");
            Throws<ArgumentException>(() => Workflow.For<OrderData>("bad name"), "workflow name");
            Throws<ArgumentException>(() => Workflow.For<OrderData>("ok").Then(new AppendAction("has space")), "action name");
            var built = Workflow.For<OrderData>("ok").Then(new AppendAction("a")).Branch(d => d.Vip, "a", Workflow.End).Build();
            Equal(2, built.Steps.Length, "steps");
            Equal("pisharp.workflow.ok", built.TaskKind, "task kind");
            Check(built.Fingerprint.Length == 16, "fingerprint");
        });

        yield return Case("codec.roundtrip", () =>
        {
            var codec = new DataCodec(DataCodec.DefaultOptions, 1024);
            var data = new OrderData { OrderId = "42", Count = 3, Amount = 12.34m, Big = 9007199254740992, Trail = ["x"] };
            var json = codec.Serialize(data);
            var back = codec.Deserialize<OrderData>(json);
            Equal("42", back.OrderId, "id");
            Equal(12.34m, back.Amount, "decimal");
            Equal(9007199254740992L, back.Big, "2^53");
            Equal("orderId", ((JsonObjectValue)json).Keys.First(), "web naming");
            var big = Throws<WorkflowDataException>(() => codec.Serialize(new OrderData { Big = 9007199254740993 }), "2^53 + 1");
            Equal("$.big", big.Path, "path of the long");
            Throws<WorkflowDataException>(() => codec.Serialize(new OrderData { Amount = 0.1234567890123456789m }), "decimal digits");
            Throws<WorkflowDataException>(() => codec.Serialize(new OrderData { Note = new string('x', 2000) }), "size cap");
            Equal(0.1, codec.Deserialize<double>(codec.Serialize(0.1)), "double");
            Equal(0.30000000000000004, codec.Deserialize<double>(codec.Serialize(0.1 + 0.2)), "shortest round-trip double");
        });

        yield return Case("run.linear", async () =>
        {
            var workflow = Workflow.For<OrderData>("linear")
                .Then(new AppendAction("load"))
                .Then("count", data => data.Count = data.Trail.Count)
                .Then("finish", async (data, context, token) =>
                {
                    await Task.Yield();
                    data.Note = context.Workflow + ":" + context.Action + ":" + context.Attempt;
                })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var result = await host.Runner.RunAsync(workflow, new OrderData { OrderId = "1" });
            Equal(WorkflowRunStatus.Completed, result.Status, "status");
            var data = result.EnsureCompleted();
            Equal("load", string.Join(",", data.Trail), "trail");
            Equal(1, data.Count, "count");
            Equal("linear:finish:1", data.Note, "context");
            Equal("finish", result.StoppedAt, "stopped at");
            Equal(3, result.Steps, "steps");
            var run = host.Runner.Attach(workflow, result.TaskId, (await host.Harness.GetTaskAsync(result.TaskId, TestHost.Context))!.ConversationId);
            var state = await run.StatusAsync();
            Equal(WorkflowRunStatus.Completed, state.Status, "state");
            Equal("load,count,finish", string.Join(",", state.History.Select(line => line.Action)), "history");
        });

        yield return Case("run.branch", async () =>
        {
            var workflow = Workflow.For<OrderData>("branching")
                .Then(new AppendAction("load"))
                .Branch(d => d.Vip, "vip", "standard")
                .Then(new AppendAction("vip"), new ActionOptions { Next = Workflow.End })
                .Then(new AppendAction("standard"))
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var vip = (await host.Runner.RunAsync(workflow, new OrderData { Vip = true })).EnsureCompleted();
            Equal("load,vip", string.Join(",", vip.Trail), "vip path");
            var standard = await host.Runner.RunAsync(workflow, new OrderData { Vip = false });
            Equal("load,standard", string.Join(",", standard.EnsureCompleted().Trail), "standard path");
            var run = host.Runner.Attach(workflow, standard.TaskId, (await host.Harness.GetTaskAsync(standard.TaskId, TestHost.Context))!.ConversationId);
            var history = (await run.StatusAsync()).History;
            Check(history.All(line => !line.Action.StartsWith('#') && (line.Next is null || !line.Next.StartsWith('#'))), "routers never become phases");
            Equal("standard", history[0].Next, "router resolved in the transition");
        });

        yield return Case("run.goto-loop", async () =>
        {
            var loop = Workflow.For<OrderData>("looping")
                .Then("tick", (data, _, _) =>
                {
                    data.Count++;
                    return Task.FromResult(data.Count < 5 ? ActionResult.GoTo("tick") : ActionResult.Next());
                })
                .Build();
            var limited = Workflow.For<OrderData>("visits")
                .Then("tick", (data, _, _) => { data.Count++; return Task.FromResult(ActionResult.GoTo("tick")); }, new ActionOptions { MaxVisits = 3 })
                .Build();
            var steps = Workflow.For<OrderData>("steps").WithLimits(maxSteps: 4)
                .Then("tick", (data, _, _) => { data.Count++; return Task.FromResult(ActionResult.GoTo("tick")); })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(loop).Add(limited).Add(steps));
            Equal(5, (await host.Runner.RunAsync(loop, new OrderData())).EnsureCompleted().Count, "loop count");
            var visits = await host.Runner.RunAsync(limited, new OrderData());
            Equal(WorkflowRunStatus.Failed, visits.Status, "visits status");
            Equal("limit", visits.ErrorKind, "visits kind");
            Equal(3, visits.Data!.Count, "visits data kept");
            var stepped = await host.Runner.RunAsync(steps, new OrderData());
            Equal("limit", stepped.ErrorKind, "steps kind");
            Check(stepped.Error!.Contains("MaxSteps"), "steps message");
        });

        yield return Case("run.stop-and-fail", async () =>
        {
            var stop = Workflow.For<OrderData>("stopping")
                .Then("first", (data, _, _) => { data.Note = "stopped"; return Task.FromResult(ActionResult.Stop()); })
                .Then(new AppendAction("never"))
                .Build();
            var fail = Workflow.For<OrderData>("failing")
                .Then("check", (data, _, _) => { data.Note = "bad"; return Task.FromResult(ActionResult.Fail("order is invalid")); })
                .Build();
            var thrower = Workflow.For<OrderData>("throwing").Then("boom", data => throw new InvalidOperationException("boom")).Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(stop).Add(fail).Add(thrower));
            var stopped = await host.Runner.RunAsync(stop, new OrderData());
            Equal("first", stopped.StoppedAt, "stopped at");
            Equal(0, stopped.EnsureCompleted().Trail.Count, "never ran");
            var failed = await host.Runner.RunAsync(fail, new OrderData());
            Equal(WorkflowRunStatus.Failed, failed.Status, "failed");
            Equal("order is invalid", failed.Error, "reason");
            Equal("bad", failed.Data!.Note, "data of a Fail is kept");
            Equal("check", failed.StoppedAt, "failed at");
            var thrown = await host.Runner.RunAsync(thrower, new OrderData());
            Equal("action_failed", thrown.ErrorKind, "exception kind");
            Check(thrown.Error!.Contains("boom"), "exception message");
            var error = await ThrowsAsync<WorkflowFailedException>(() => Task.FromResult(thrown.EnsureCompleted()), "ensure completed");
            Equal(WorkflowRunStatus.Failed, error.Status, "exception status");
        });

        yield return Case("run.retry", async () =>
        {
            var attempts = new List<(int Attempt, long Step)>();
            var workflow = Workflow.For<OrderData>("retrying")
                .Then("flaky", (data, context, _) =>
                {
                    attempts.Add((context.Attempt, context.Step));
                    data.Trail.Add("attempt" + context.Attempt);
                    if (context.Attempt < 3) throw new IOException("transient");
                    return Task.FromResult(ActionResult.Next());
                }, new ActionOptions { Retry = RetryPolicy.Fixed(3, TimeSpan.FromMilliseconds(20)) })
                .Build();
            var exhausted = Workflow.For<OrderData>("exhausted")
                .Then("flaky", data => throw new IOException("always"), new ActionOptions { Retry = RetryPolicy.Fixed(2) })
                .Build();
            var filtered = Workflow.For<OrderData>("filtered")
                .Then("flaky", data => throw new ArgumentException("bad input"),
                    new ActionOptions { Retry = RetryPolicy.Fixed(5) with { RetryOn = error => error is IOException } })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow).Add(exhausted).Add(filtered));
            var data = (await host.Runner.RunAsync(workflow, new OrderData())).EnsureCompleted();
            Equal("1,2,3", string.Join(",", attempts.Select(item => item.Attempt)), "attempts");
            Check(attempts.Select(item => item.Step).Distinct().Count() == 1, "one visit across retries");
            Equal("attempt3", string.Join(",", data.Trail), "failed attempts' changes are discarded");
            var failed = await host.Runner.RunAsync(exhausted, new OrderData());
            Check(failed.Error!.Contains("after 2 attempts"), "exhausted message: " + failed.Error);
            var once = await host.Runner.RunAsync(filtered, new OrderData());
            Check(!once.Error!.Contains("attempts"), "not retried: " + once.Error);
        });

        yield return Case("run.timeout", async () =>
        {
            var workflow = Workflow.For<OrderData>("slow")
                .Then("wait", async (data, context, token) => await Task.Delay(Timeout.Infinite, token),
                    new ActionOptions { Timeout = TimeSpan.FromMilliseconds(50), Retry = RetryPolicy.Fixed(2) })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var result = await host.Runner.RunAsync(workflow, new OrderData());
            Equal(WorkflowRunStatus.Failed, result.Status, "status");
            Check(result.Error!.Contains("timed out") && result.Error.Contains("2 attempts"), "message: " + result.Error);
        });

        yield return Case("run.repeat-and-sleep", async () =>
        {
            var workflow = Workflow.For<OrderData>("polling")
                .Then("poll", (data, context, _) =>
                {
                    data.Count++;
                    data.Trail.Add("visit" + context.Visit);
                    return Task.FromResult(data.Count < 3 ? ActionResult.Repeat(TimeSpan.FromMilliseconds(10)) : ActionResult.Sleep(TimeSpan.FromMilliseconds(30), "done"));
                })
                .Then(new AppendAction("skipped"))
                .Then(new AppendAction("done"))
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var started = DateTimeOffset.UtcNow;
            var data = (await host.Runner.RunAsync(workflow, new OrderData())).EnsureCompleted();
            Equal("visit1,visit2,visit3,done", string.Join(",", data.Trail), "trail");
            Check(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(45), "waited");
        });

        yield return Case("run.sleep-before-composite-and-end", async () =>
        {
            long napped = 0, ran = 0;
            var child = Workflow.For<ChildData>("after-nap")
                .Then("mark", (_, _, _) => { ran = System.Diagnostics.Stopwatch.GetTimestamp(); return Task.CompletedTask; })
                .Build();
            var workflow = Workflow.For<OrderData>("nap-then-run")
                .Then("nap", (_, _, _) =>
                {
                    napped = System.Diagnostics.Stopwatch.GetTimestamp();
                    return Task.FromResult(ActionResult.Sleep(TimeSpan.FromMilliseconds(300)));
                })
                .Run("sub", child, _ => new ChildData(), (data, result) => data.Note = result.Status.ToString())
                .Build();
            var ending = Workflow.For<OrderData>("nap-then-end")
                .Then("last", (data, _, _) =>
                {
                    data.Count = 1;
                    return Task.FromResult(ActionResult.Sleep(TimeSpan.FromMilliseconds(300)));
                })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow).Add(ending));
            Equal("Completed", (await host.Runner.RunAsync(workflow, new OrderData())).EnsureCompleted().Note, "the sub-workflow ran");
            var waited = System.Diagnostics.Stopwatch.GetElapsedTime(napped, ran);
            Check(waited >= TimeSpan.FromMilliseconds(250), $"the composite step waited for the sleep ({waited.TotalMilliseconds} ms)");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var ended = await host.Runner.RunAsync(ending, new OrderData());
            Equal(1, ended.EnsureCompleted().Count, "data kept");
            Check(System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(250), "the end waited for the sleep");
            Equal("last", ended.StoppedAt, "stopped at the action that slept");
        });

        yield return Case("run.data-error", async () =>
        {
            var workflow = Workflow.For<OrderData>("precision").Then("big", data => data.Big = long.MaxValue).Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var result = await host.Runner.RunAsync(workflow, new OrderData { Count = 7 });
            Equal("data", result.ErrorKind, "kind");
            Equal(7, result.Data!.Count, "last committed data kept");
            await ThrowsAsync<WorkflowDataException>(() => host.Runner.StartAsync(workflow, new OrderData { Big = long.MaxValue }), "start validates");
        });

        yield return Case("run.abort", async () =>
        {
            var entered = new TaskCompletionSource();
            var aborts = 0;
            var workflow = Workflow.For<OrderData>("aborting")
                .Then("prepare", data => data.Note = "prepared")
                .Then("hang", async (data, context, token) =>
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                })
                .OnAbort((data, context, token) =>
                {
                    Interlocked.Increment(ref aborts);
                    data.Trail.Add("cleaned");
                    return Task.CompletedTask;
                })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var run = await host.Runner.StartAsync(workflow, new OrderData());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await run.AbortAsync();
            var result = await run.WaitAsync();
            Equal(WorkflowRunStatus.Aborted, result.Status, "status");
            Equal(1, aborts, "OnAbort runs once");
            Equal("prepared", result.Data!.Note, "committed data");
            Equal("cleaned", string.Join(",", result.Data.Trail), "abort handler changes kept");
            Equal(WorkflowRunStatus.Aborted, (await run.StatusAsync()).Status, "state");
        });

        yield return Case("start.runkey", async () =>
        {
            var gate = new TaskCompletionSource();
            var workflow = Workflow.For<OrderData>("keyed").Then("wait", async (data, context, token) => await gate.Task.WaitAsync(token)).Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var first = await host.Runner.StartAsync(workflow, new OrderData { OrderId = "a" }, new WorkflowStartOptions { RunKey = "order-42" });
            var second = await host.Runner.StartAsync(workflow, new OrderData { OrderId = "b" }, new WorkflowStartOptions { RunKey = "order-42" });
            Check(!first.Existing && second.Existing, "second start finds the first");
            Equal(first.TaskId, second.TaskId, "same task");
            Equal(first.TaskId, (await host.Runner.FindAsync(workflow, "order-42"))!.TaskId, "find by key");
            Check(await host.Runner.FindAsync(workflow, "other") is null, "unknown key");
            gate.SetResult();
            Equal("a", (await second.WaitAsync()).EnsureCompleted().OrderId, "first data wins");
            var listed = await host.Runner.ListAsync();
            Equal(1, listed.Count, "one run listed");
            Equal("order-42", listed[0].RunKey, "listed key");
        });

        yield return Case("catalog.unknown-workflow", async () =>
        {
            var workflow = Workflow.For<OrderData>("unregistered").Then(new AppendAction("a")).Build();
            await using var host = await TestHost.OpenAsync(_ => { });
            await ThrowsAsync<InvalidOperationException>(() => host.Runner.StartAsync(workflow, new OrderData()), "not in catalog");
        });
    }
}
