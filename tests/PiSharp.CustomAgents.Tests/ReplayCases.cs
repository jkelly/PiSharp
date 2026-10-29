using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;

// Decision 0012, P2: replay rules. Data and the next step commit exactly once; action bodies run at least once; OnceAsync records a
// result once per visit; IsReplay and idempotency keys let an action recognise a rerun; runs resume from SQLite after a close.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> ReplayCases()
    {
        yield return Case("once.memo", async () =>
        {
            var charges = 0;
            var keys = new List<string>();
            var workflow = Workflow.For<OrderData>("charging")
                .Then("charge", async (data, context, token) =>
                {
                    var receipt = await context.OnceAsync("charge", _ =>
                    {
                        Interlocked.Increment(ref charges);
                        return Task.FromResult("receipt-" + context.IdempotencyKey("charge"));
                    }, token);
                    keys.Add(context.IdempotencyKey("charge"));
                    data.Note = receipt;
                    if (context.Attempt == 1) throw new IOException("network");
                    await context.OnceAsync("noop", _ => Task.CompletedTask, token);
                }, new ActionOptions { Retry = RetryPolicy.Fixed(2) })
                .Then("again", async (data, context, token) =>
                {
                    // A new visit: its own memo scope.
                    data.Trail.Add(await context.OnceAsync("charge", _ => Task.FromResult("second-visit"), token));
                })
                .Build();
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow));
            var data = (await host.Runner.RunAsync(workflow, new OrderData())).EnsureCompleted();
            Equal(1, charges, "charged once across the retry");
            Equal(2, keys.Count, "two attempts");
            Equal(keys[0], keys[1], "the idempotency key is stable across attempts");
            Check(data.Note!.StartsWith("receipt-wf:", StringComparison.Ordinal), "recorded receipt: " + data.Note);
            Equal("second-visit", string.Join(",", data.Trail), "new visit, new scope");
        });

        yield return Case("resume.sqlite-reopen", async () =>
        {
            var path = TestHost.SqlitePath("reopen");
            var hang = true;
            var runs = new Dictionary<string, int>();
            var replays = new List<bool>();
            var keys = new List<string>();
            var entered = new TaskCompletionSource();
            void Count(string name) { lock (runs) runs[name] = runs.GetValueOrDefault(name) + 1; }
            var workflow = Workflow.For<OrderData>("resumable")
                .Then("first", data => { Count("first"); data.Trail.Add("first"); })
                .Then("second", async (data, context, token) =>
                {
                    Count("second");
                    replays.Add(context.IsReplay);
                    keys.Add(context.IdempotencyKey("ship"));
                    data.Trail.Add("second");
                    if (hang)
                    {
                        entered.TrySetResult();
                        await Task.Delay(Timeout.Infinite, token);
                    }
                })
                .Then("sleep", (data, _, _) => Task.FromResult(ActionResult.Sleep(TimeSpan.FromMilliseconds(400))))
                .Then("third", data => { Count("third"); data.Trail.Add("third"); })
                .Build();
            WorkflowRun<OrderData> run;
            await using (var first = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path)))
            {
                run = await first.Runner.StartAsync(workflow, new OrderData { OrderId = "7" }, new WorkflowStartOptions { RunKey = "seven" });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            hang = false;
            await using (var second = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path)))
            {
                var found = await second.Runner.FindAsync(workflow, "seven");
                Equal(run.TaskId, found!.TaskId, "run key survives the reopen");
                // Close while the durable sleep is pending; the next host finishes it.
                await WaitUntilAsync(async () => (await found.StatusAsync()).History.Any(line => line.Action == "sleep"), TimeSpan.FromSeconds(20));
                Equal("third", (await found.StatusAsync()).CurrentAction, "sleeping before third");
            }
            await using (var third = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path)))
            {
                var data = (await third.Runner.Attach(workflow, run.TaskId, run.ConversationId).WaitAsync()).EnsureCompleted();
                Equal("first,second,third", string.Join(",", data.Trail), "the interrupted attempt's changes never committed");
            }
            Equal(1, runs["first"], "a committed action never reruns");
            Equal(2, runs["second"], "the interrupted action reruns (at least once)");
            Equal(1, runs["third"], "third once");
            Equal("False,True", string.Join(",", replays), "the rerun is a replay");
            Equal(keys[0], keys[1], "the idempotency key survives the restart");
        });
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(20);
        }
    }
}
