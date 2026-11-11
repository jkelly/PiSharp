// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT), durable scheduler fix #11892 (e50dc7a): a pass must retain notifications
// published while it plans. The native scheduler already applies a clean decision and candidate cleanup under one lock. These
// authored native cases hold storage and dispatch independently to make the last dependency end while a planning region is gated.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync152SchedulerCases() => HarnessTests.Scheduler152Cases();
}

internal static partial class HarnessTests
{
    internal static IEnumerable<(string, Func<Task>)> Scheduler152Cases()
    {
        foreach (var completing in new[] { false, true })
            yield return Case("sync152.scheduler.keeps-" + (completing ? "completing-owner" : "waiter") + "-notification-during-planning",
                () => NotificationDuringPlanning(completing));
    }

    private static async Task NotificationDuringPlanning(bool completing)
    {
        var controlled = new ControlledStorage(new MemoryStorage());
        var finishChild = new Deferred();
        var childStarted = new Deferred();
        var created = new Deferred<TaskId>();
        var child = OneStep("sync152.child", async (_, runtime, ctx) =>
        {
            childStarted.Resolve();
            await finishChild.Task;
            await runtime.CommitAsync((_, _) => Completed(null), ctx);
        });
        var parent = Tasks.DefineTask(new TaskDefinition
        {
            Name = "sync152.parent", Version = 1, Initial = _ => O("""{"phase":"spawn"}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["spawn"] = async (_, runtime, ctx) =>
                {
                    // Publish the child and let its invocation begin before the parent transitions. This fixture tests the
                    // scheduler's existing-live-child notification, independent of same-commit child creation and termination.
                    await runtime.CommitAsync(async (tx, current) =>
                    {
                        created.Resolve(await tx.CreateTaskAsync(child, JsonValue.Null, new TaskOptions(TaskOwnership.Task(current.Id))));
                    }, ctx);
                    await childStarted.Task;
                    var id = await created.Task;
                    await runtime.CommitAsync((_, _) => completing ? Completed(null) :
                        NextTaskState.Waiting(O("""{"phase":"done"}"""), [id], JoinPolicy.AllSettled), ctx);
                },
                ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(null), ctx),
            },
            Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
        });
        var (harness, _, reports, root) = await OpenRoot(controlled, [parent, child]);
        var parentId = await Start(root, parent);
        harness.Resume();
        var childId = await created.Task;
        var expected = completing ? DurableTaskStatus.Completing : DurableTaskStatus.Waiting;
        await Eventually(async () => (await harness.GetTaskAsync(parentId, Context))?.State.Status == expected &&
            (await harness.GetTaskAsync(childId, Context))?.State.Status == DurableTaskStatus.Running);
        await InvocationEnded(harness, parentId);
        var durable = (DurableHarness)harness;
        await durable.Scheduler.QuiesceAsync();
        var pool = new HeldPool();
        durable.Scheduler.Dispatch = pool.Post;
        var record = (await harness.GetTaskAsync(parentId, Context))!;
        // Put the existing waiter/holder back in the next pass's candidates, with dispatch still held.
        await SchedulerTests.SetTasks(harness, tx => { tx.SetTask(record); return Task.CompletedTask; });
        var storing = controlled.HoldCommits();
        finishChild.Resolve();
        await storing.Entered;
        // This pending write makes the candidate's decision region wait. The child's earlier terminal publication must still
        // wake it when storage is released; neither the old waiting decision nor the old finalize check may erase that wake.
        var unpublished = durable.LineSizes.Unpublished;
        var rewriting = SchedulerTests.SetTasks(harness, tx => { tx.SetTask(record); return Task.CompletedTask; });
        await Eventually(() => durable.LineSizes.Unpublished > unpublished);
        var hits = durable.GateHits;
        // Reconciliation starts on the Session line, not through Dispatch. Request it only after the writes are pending;
        // requesting idle earlier consumes the completing check before there is a publication race to exercise.
        var idle = harness.WaitForIdleAsync(Context);
        pool.Release();
        try
        {
            await Eventually(() => durable.GateHits > hits);
        }
        finally
        {
            storing.Release();
        }
        await rewriting;
        Equal(DurableTaskStatus.Terminal, (await harness.WaitForTaskAsync(parentId, Context)).State.Status, "parent finishes");
        await idle;
        Equal(0, reports.Count, "scheduler reports");
        await harness.CloseAsync(Context);
    }
}
