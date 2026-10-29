using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;

internal sealed class ChildData
{
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
}

// Decision 0012, P4: composition: Run (sub-workflow), Parallel (branches over copies of the Data), ForEach (a child run per item) and
// WaitForSignal (a value sent to the run from outside).
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> CompositionCases()
    {
        yield return Case("sub.run", async () =>
        {
            var upper = Workflow.For<ChildData>("upper").Then("shout", data => data.Output = data.Input.ToUpperInvariant()).Build();
            var broken = Workflow.For<ChildData>("broken")
                .Then("explode", (_, _, _) => Task.FromResult(ActionResult.Fail("child said no")))
                .Build();
            var parent = Workflow.For<OrderData>("parent")
                .Then(new AppendAction("before"))
                .Run("child", upper, data => new ChildData { Input = data.OrderId }, (data, result) => data.Note = result.EnsureCompleted().Output)
                .Then(new AppendAction("after"))
                .Build();
            var strict = Workflow.For<OrderData>("strict")
                .Run("child", broken, data => new ChildData(), (data, _) => data.Note = "unreachable")
                .Build();
            var lenient = Workflow.For<OrderData>("lenient")
                .Run("child", broken, data => new ChildData(), (data, result) => data.Note = result.Status + ":" + result.Error, failOnError: false)
                .Build();
            // Adding a parent adds the sub-workflows it runs.
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(parent).Add(strict).Add(lenient));
            Check(host.Catalog.Find("upper") is not null && host.Catalog.Find("broken") is not null, "sub-workflows added");
            var data = (await host.Runner.RunAsync(parent, new OrderData { OrderId = "abc" })).EnsureCompleted();
            Equal("ABC", data.Note, "child output applied");
            Equal("before,after", string.Join(",", data.Trail), "parent continues after the child");
            var failed = await host.Runner.RunAsync(strict, new OrderData());
            Equal(WorkflowRunStatus.Failed, failed.Status, "a failed child fails the parent");
            Equal("child_failed", failed.ErrorKind, "kind");
            Check(failed.Error!.Contains("child said no"), "the child's reason is carried: " + failed.Error);
            Equal("Failed:child said no", (await host.Runner.RunAsync(lenient, new OrderData())).EnsureCompleted().Note, "failOnError: false");
        });

        yield return Case("parallel.failfast", async () =>
        {
            var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var merged = 0;
            var workflow = Workflow.For<OrderData>("fanout")
                .Parallel("fan", [
                    ParallelBranch<OrderData>.Of("quick", (data, _, _) => { data.Note = "quick"; return Task.CompletedTask; }),
                    ParallelBranch<OrderData>.Of("bad", new DelegateAction<OrderData>("bad", async (_, _, token) =>
                    {
                        await slowStarted.Task.WaitAsync(token);
                        return ActionResult.Fail("bad branch");
                    })),
                    ParallelBranch<OrderData>.Of("slow", async (_, _, token) =>
                    {
                        slowStarted.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException) { slowCancelled.TrySetResult(); throw; }
                    }),
                ], (_, _) => Interlocked.Increment(ref merged))
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            Check(host.Catalog.Find("fanout.fan.slow") is not null, "single-action branches compile to their own workflows");
            var result = await host.Runner.RunAsync(workflow, new OrderData()).WaitAsync(TimeSpan.FromSeconds(30));
            Equal(WorkflowRunStatus.Failed, result.Status, "status");
            Equal("child_failed", result.ErrorKind, "kind");
            Check(result.Error!.Contains("'bad'") && result.Error.Contains("bad branch"), "names the failed branch: " + result.Error);
            await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Equal(0, merged, "no merge on fail-fast");
            Equal(null, result.Data!.Note, "branches change copies, not the run's Data");
        });

        yield return Case("parallel.allsettled", async () =>
        {
            var workflow = Workflow.For<OrderData>("settled")
                .Parallel("fan", [
                    ParallelBranch<OrderData>.Of("a", (data, _, _) => { data.Note = "from a"; return Task.CompletedTask; }),
                    ParallelBranch<OrderData>.Of("b", new DelegateAction<OrderData>("b", (_, _, _) => Task.FromResult(ActionResult.Fail("b failed")))),
                    ParallelBranch<OrderData>.Of("c", Workflow.For<OrderData>("c-flow").Then(new AppendAction("c1")).Then(new AppendAction("c2")).Build()),
                ], (data, results) =>
                {
                    data.Trail.AddRange(results.Select(result => result.Branch + "=" + result.Result.Status));
                    data.Note = results[0].Result.Data!.Note;
                    data.Count = results[2].Result.Data!.Trail.Count;
                }, JoinPolicy.AllSettled)
                .Then(new AppendAction("after"))
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var data = (await host.Runner.RunAsync(workflow, new OrderData())).EnsureCompleted();
            Equal("a=Completed,b=Failed,c=Completed,after", string.Join(",", data.Trail), "every branch merged, in declaration order");
            Equal("from a", data.Note, "branch data");
            Equal(2, data.Count, "workflow branch ran its steps");
        });

        yield return Case("parallel.resume", async () =>
        {
            var path = TestHost.SqlitePath("parallel-resume");
            var hang = true;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var merges = 0;
            var workflow = Workflow.For<OrderData>("resumable-fan")
                .Parallel("fan", [
                    ParallelBranch<OrderData>.Of("fast", (data, _, _) => { data.Note = "fast"; return Task.CompletedTask; }),
                    ParallelBranch<OrderData>.Of("slow", async (data, _, token) =>
                    {
                        if (hang)
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.Infinite, token);
                        }
                        data.Note = "slow";
                    }),
                ], (data, results) =>
                {
                    Interlocked.Increment(ref merges);
                    data.Trail.AddRange(results.Select(result => result.Result.Data!.Note!));
                })
                .Build();
            WorkflowRun<OrderData> run;
            await using (var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path)))
            {
                run = await host.Runner.StartAsync(workflow, new OrderData());
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            hang = false;
            await using (var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path)))
            {
                var data = (await host.Runner.Attach(workflow, run.TaskId, run.ConversationId).WaitAsync().WaitAsync(TimeSpan.FromSeconds(20)))
                    .EnsureCompleted();
                Equal("fast,slow", string.Join(",", data.Trail), "both branches merged after the reopen");
            }
            Equal(1, merges, "the join ran once");
        });

        yield return Case("parallel.abort-cascades", async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workflow = Workflow.For<OrderData>("abortable-fan")
                .Parallel("fan", [
                    ParallelBranch<OrderData>.Of("hang", async (_, _, token) =>
                    {
                        started.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                    }),
                ], (_, _) => { })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var run = await host.Runner.StartAsync(workflow, new OrderData());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await run.AbortAsync();
            var result = await run.WaitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Equal(WorkflowRunStatus.Aborted, result.Status, "parent aborted");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });

        yield return Case("foreach.concurrency", async () =>
        {
            var live = 0;
            var peakGate = new Lock();
            var peak = 0;
            var body = Workflow.For<ChildData>("square")
                .Then("work", async (data, _, token) =>
                {
                    var now = Interlocked.Increment(ref live);
                    lock (peakGate) peak = Math.Max(peak, now);
                    await Task.Delay(60, token);
                    Interlocked.Decrement(ref live);
                    var n = int.Parse(data.Input, System.Globalization.CultureInfo.InvariantCulture);
                    data.Output = (n * n).ToString(System.Globalization.CultureInfo.InvariantCulture);
                })
                .Build();
            var workflow = Workflow.For<OrderData>("squares")
                .ForEach("each", data => data.Trail, body, (_, item, index) => new ChildData { Input = item },
                    (data, results) =>
                    {
                        data.Note = string.Join(",", results.Select(result => result.Result.Data!.Output));
                        data.Count = results.Select((result, position) => result.Index == position).Count(match => match);
                    }, new ForEachOptions { Concurrency = 3 })
                .Build();
            var failing = Workflow.For<OrderData>("squares-fail")
                .ForEach("each", data => data.Trail, Workflow.For<ChildData>("picky")
                    .Then("check", (data, _, _) => Task.FromResult(data.Input == "3" ? ActionResult.Fail("no threes") : ActionResult.Next())).Build(),
                    (_, item, _) => new ChildData { Input = item }, (data, _) => data.Note = "merged")
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow).Add(failing));
            var items = Enumerable.Range(1, 10).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var data = (await host.Runner.RunAsync(workflow, new OrderData { Trail = items }).WaitAsync(TimeSpan.FromSeconds(60))).EnsureCompleted();
            Equal("1,4,9,16,25,36,49,64,81,100", data.Note, "results merge in item order");
            Equal(10, data.Count, "indexes");
            Check(peak <= 3, $"never more than Concurrency live (peak {peak})");
            Check(peak >= 2, $"items run at once (peak {peak})");
            Equal("", (await host.Runner.RunAsync(workflow, new OrderData())).EnsureCompleted().Note, "no items");
            var failed = await host.Runner.RunAsync(failing, new OrderData { Trail = items });
            Equal("child_failed", failed.ErrorKind, "fail-fast item");
            Check(failed.Error!.Contains("no threes"), "reason: " + failed.Error);
        });

        yield return Case("foreach.failfast-stops-early", async () =>
        {
            var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var merged = 0;
            var body = Workflow.For<ChildData>("maybe-slow")
                .Then("work", async (data, _, token) =>
                {
                    if (data.Input == "bad")
                    {
                        await slowStarted.Task.WaitAsync(token);
                        return ActionResult.Fail("bad item");
                    }
                    slowStarted.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { slowCancelled.TrySetResult(); throw; }
                    return ActionResult.Next();
                })
                .Build();
            var workflow = Workflow.For<OrderData>("each-failfast")
                .ForEach("each", data => data.Trail, body, (_, item, _) => new ChildData { Input = item }, (_, _) => Interlocked.Increment(ref merged),
                    new ForEachOptions { Concurrency = 3 })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            // The slow items never end on their own: the step fails at the first failed item instead of waiting for the batch.
            var result = await host.Runner.RunAsync(workflow, new OrderData { Trail = ["bad", "slow", "slow"] }).WaitAsync(TimeSpan.FromSeconds(30));
            Equal(WorkflowRunStatus.Failed, result.Status, "status");
            Equal("child_failed", result.ErrorKind, "kind");
            Check(result.Error!.Contains("bad item"), "reason: " + result.Error);
            await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Equal(0, merged, "no merge on fail-fast");
        });

        yield return Case("signal.early-late-timeout-reopen", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approval = Workflow.For<OrderData>("approval")
                .Then("prepare", async (_, _, token) => await gate.Task.WaitAsync(token))
                .WaitForSignal<string>("approve", "approved", (data, by) => data.Note = "approved by " + by)
                .Then(new AppendAction("ship"))
                .Build();
            var timed = Workflow.For<OrderData>("timed")
                .WaitForSignal<int>("wait", "go", (data, n) => data.Count = n, TimeSpan.FromMilliseconds(300), onTimeout: "late")
                .Then(new AppendAction("ontime"), new ActionOptions { Next = Workflow.End })
                .Then(new AppendAction("late"))
                .Build();
            var strict = Workflow.For<OrderData>("strict-signal")
                .WaitForSignal<int>("wait", "go", (data, n) => data.Count = n, TimeSpan.FromMilliseconds(200))
                .Build();
            var path = TestHost.SqlitePath("signals");
            WorkflowRun<OrderData> parked;
            await using (var host = await TestHost.OpenAsync(catalog => catalog.Add(approval).Add(timed).Add(strict), await TestHost.SqliteAsync(path)))
            {
                // Early: sent before the run reaches the step, kept until it does; a repeated id is ignored.
                var early = await host.Runner.StartAsync(approval, new OrderData());
                Check(await early.SignalAsync("approved", "ana", id: "s1"), "first send");
                Check(!await early.SignalAsync("approved", "bob", id: "s1"), "duplicate id ignored");
                gate.TrySetResult();
                var data = (await early.WaitAsync().WaitAsync(TimeSpan.FromSeconds(20))).EnsureCompleted();
                Equal("approved by ana", data.Note, "early signal");
                Equal("ship", string.Join(",", data.Trail), "continued");

                // Late: the run waits at the step until the signal comes.
                var late = await host.Runner.StartAsync(approval, new OrderData());
                await WaitUntilAsync(async () => (await late.StatusAsync()).CurrentAction == "approve", TimeSpan.FromSeconds(20));
                await Task.Delay(150);
                Equal(WorkflowRunStatus.Running, (await late.StatusAsync()).Status, "waiting");
                await late.SignalAsync("other", "x");
                await late.SignalAsync("approved", "cy");
                Equal("approved by cy", (await late.WaitAsync().WaitAsync(TimeSpan.FromSeconds(20))).EnsureCompleted().Note, "late signal");

                // Timeouts.
                var timedOut = (await host.Runner.RunAsync(timed, new OrderData()).WaitAsync(TimeSpan.FromSeconds(20))).EnsureCompleted();
                Equal("late", string.Join(",", timedOut.Trail), "onTimeout target");
                var onTime = await host.Runner.StartAsync(timed, new OrderData());
                await onTime.SignalAsync("go", 5);
                var onTimeData = (await onTime.WaitAsync()).EnsureCompleted();
                Equal("ontime", string.Join(",", onTimeData.Trail), "signal before the deadline");
                Equal(5, onTimeData.Count, "value");
                var failed = await host.Runner.RunAsync(strict, new OrderData()).WaitAsync(TimeSpan.FromSeconds(20));
                Equal(WorkflowRunStatus.Failed, failed.Status, "no onTimeout fails");
                Equal("timeout", failed.ErrorKind, "kind");

                // Reopen: park a run at the step, close the host.
                parked = await host.Runner.StartAsync(approval, new OrderData { OrderId = "parked" });
                await WaitUntilAsync(async () => (await parked.StatusAsync()).CurrentAction == "approve", TimeSpan.FromSeconds(20));
            }
            await using (var host = await TestHost.OpenAsync(catalog => catalog.Add(approval).Add(timed).Add(strict), await TestHost.SqliteAsync(path)))
            {
                var run = host.Runner.Attach(approval, parked.TaskId, parked.ConversationId);
                await run.SignalAsync("approved", "dee");
                var data = (await run.WaitAsync().WaitAsync(TimeSpan.FromSeconds(20))).EnsureCompleted();
                Equal("approved by dee", data.Note, "signal after reopen");
                Equal("parked", data.OrderId, "same run");
            }
        });
    }
}
