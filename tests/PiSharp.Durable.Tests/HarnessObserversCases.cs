// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-observers.test.ts, in part: the watch cases of its
// "task documents" block (harness.observers.task-documents.*), which pin how a task document's watch ends with its task (8b0caefc2, work
// list area watch-folding). The synchronous observers (observeConversation, observeTaskGraph, observeDoc) and the block's observer case
// wait on area observers, which ports the rest of the file here. Verbatim apart from the adaptations in SessionSupport.cs and
// HarnessSupport.cs, and these:
// - The garbage-collection cases use WeakReference and GC.Collect for WeakRef and --expose-gc, with what they hold built in a method the
//   JIT does not inline (SessionWatchesCases.cs); the Harness is the Session kernel, so its document cache sizes are the Session's.
// - flush() before a negative check is Settled's flush; where upstream collects right after a task wait, a Session line barrier lets the
//   watch's retirement call, which runs on the line, finish first.
using System.Runtime.CompilerServices;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessObserversCases() => HarnessObserverTests.Cases("harness.observers.");
}

internal static class HarnessObserverTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly TaskDocToken ProgressDoc =
        Documents.DefineTaskDoc("test.observers.progress", 1, () => O("""{"lines":[]}"""), checkpointWhen: (_, _, _) => false);

    /// <summary>A task that writes its progress document, waits for <paramref name="gate"/>, then completes, which retires the document
    /// (upstream <c>writer</c>).</summary>
    private static DurableTask Writer(string name, Task gate, Action written) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (task, runtime, ctx) =>
            {
                await runtime.CommitAsync(async (tx, _) => { (await tx.DocAsync(ProgressDoc, task.Id)).GetArray("lines").Add("started"); }, ctx);
                written();
                await gate;
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            },
        },
        Abort = (_, _, _) => Task.CompletedTask,
    });

    private sealed record OpenedWriter(IHarness Harness, ControlledStorage Storage, IConversation Root, TaskId Id, Action Finish);

    /// <summary>A Harness with a writer task started in the root conversation and its progress document written (upstream
    /// <c>openWriter</c>).</summary>
    private static async Task<OpenedWriter> OpenWriter()
    {
        var storage = new ControlledStorage(new MemoryStorage());
        var gate = new Deferred();
        var written = new Deferred();
        var writer = Writer("test.observers.writer", gate.Task, written.Resolve);
        var opened = await OpenTasks(storage, [writer]);
        var root = await opened.Harness.RootAsync(Context);
        var id = await root.CommitAsync(tx => tx.CreateTaskAsync(writer, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
        opened.Harness.Resume();
        await written.Task;
        return new OpenedWriter(opened.Harness, storage, root, id, gate.Resolve);
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        prefix += "task-documents.";

        yield return Case(prefix + "a-watch-delivers-the-retirement-of-the-commit-that-ends-its-task-then-ends-ended", async () =>
        {
            var (harness, _, _, id, finish) = await OpenWriter();
            var watch = (await harness.WatchDocAsync(ProgressDoc, id, Context))!;
            SameJson("""{"lines":["started"]}""", watch.Value, "value");
            var changes = new SyncList<WatchValueCall<JsonObjectValue>>();
            watch.Start((call, _) =>
            {
                changes.Add(Change(call));
                return Task.CompletedTask;
            });
            finish();
            await harness.WaitForTaskAsync(id, Context);
            Equal(WatchEnd.Ended, await watch.Closed, "closed");
            var all = changes.Snapshot();
            Equal(1, all.Count, "one call");
            SameJson("""{"lines":["started"]}""", all[0].Value, "the last value");
            Equal("[]", OpsJson(all[0].Ops), "no ops");
            Check(all[0].Retired, "retired");
            Same(watch.Value, all[0].Value, "watch.value");
            // The task is terminal: its address is absent for good.
            Check(await harness.WatchDocAsync(ProgressDoc, id, Context) is null, "absent");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-no-task-document-watch-alive-once-its-task-ended", async () =>
        {
            var (harness, _, _, id, finish) = await OpenWriter();
            var watch = await StartedWatch(harness, id);
            // Control: the watch stays while its task runs, so the collection below is real.
            await CollectGarbage();
            Check(watch.IsAlive, "a running task's watch stays");
            finish();
            await harness.WaitForTaskAsync(id, Context);
            // The retirement call runs on the Session line, after the commit the wait saw.
            await ((Session)harness).FlushLineAsync();
            await CollectGarbage();
            Check(!watch.IsAlive, "the ended watch is gone");
            // The Harness is the Session kernel; nothing stays pinned for the ended watch.
            Equal(0, ((Session)harness).DocumentCacheSizes.Attached, "attached");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "a-watch-never-started-when-its-task-ends-still-ends-with-the-harnesss-close-keeping-nothing-of-it", async () =>
        {
            var (watch, harness) = await NeverStartedWatch();
            // Closed at the Harness's close, the pending retirement dropped, as for every watch.
            Equal(WatchEnd.Ended, await watch.Closed, "closed");
            Throws(() => watch.Start((_, _) => Task.CompletedTask), "stopped", "late start");
            await CollectGarbage();
            Check(!harness.IsAlive, "the Harness is gone");
        });

        yield return Case(prefix + "a-watch-never-started-when-its-task-ends-fails-with-the-harness", async () =>
        {
            var (harness, storage, root, id, finish) = await OpenWriter();
            var watch = (await harness.WatchDocAsync(ProgressDoc, id, Context))!;
            finish();
            await harness.WaitForTaskAsync(id, Context);
            var failure = new InvalidOperationException("disk gone");
            storage.FailNextCommit(failure);
            await RejectsAsync(() => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = O("{}") }), Context),
                "disk gone", "the failing commit");
            Equal(WatchEnd.Failed(failure), await watch.Closed, "failed");
        });

        yield return Case(prefix + "a-watch-busy-in-an-earlier-call-when-its-task-ends-fails-with-the-harness-before-its-retirement-call", async () =>
        {
            var (harness, storage, root, id, finish) = await OpenWriter();
            var watch = (await harness.WatchDocAsync(ProgressDoc, id, Context))!;
            var entered = new Deferred();
            var release = new Deferred();
            var values = new SyncList<string>();
            watch.Start(async (call, _) =>
            {
                values.Add(Change(call).Value.ToJson());
                entered.Resolve();
                await release.Task;
            });
            await harness.CommitAsync(async tx => { (await tx.DocAsync(ProgressDoc, id)).GetArray("lines").Add("more"); }, Context);
            await entered.Task;
            // The task ends while the listener is busy: its retirement waits behind the running call.
            finish();
            await harness.WaitForTaskAsync(id, Context);
            var failure = new InvalidOperationException("disk gone");
            storage.FailNextCommit(failure);
            await RejectsAsync(() => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = O("{}") }), Context),
                "disk gone", "the failing commit");
            Equal(WatchEnd.Failed(failure), await watch.Closed, "failed");
            release.Resolve();
            await Flush();
            Seq(["""{"lines":["started","more"]}"""], values.Snapshot(), "values");
        });

        yield return Case(prefix + "a-watch-whose-retirement-call-runs-when-its-task-ends-ends-only-after-that-call-returns", async () =>
        {
            var (harness, _, _, id, finish) = await OpenWriter();
            var watch = (await harness.WatchDocAsync(ProgressDoc, id, Context))!;
            var entered = new Deferred();
            var release = new Deferred();
            var returned = false;
            watch.Start(async (_, _) =>
            {
                entered.Resolve();
                await release.Task;
                Volatile.Write(ref returned, true);
            });
            // An explicit retirement first: its call runs while the task ends, and nothing more is pending.
            await harness.CommitAsync(tx => tx.RetireDocAsync(ProgressDoc, id), Context);
            await entered.Task;
            finish();
            await harness.WaitForTaskAsync(id, Context);
            Check(!await Settled(watch.Closed), "open while its retirement call runs");
            release.Resolve();
            Equal(WatchEnd.Ended, await watch.Closed, "ended");
            Check(Volatile.Read(ref returned), "after the call returned");
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>A started watch of the task's document, which only the Harness references (upstream <c>acquire</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StartedWatch(IHarness harness, TaskId id)
    {
        var watch = (await harness.WatchDocAsync(ProgressDoc, id, Context))!;
        watch.Start((_, _) => Task.CompletedTask);
        return new WeakReference(watch);
    }

    /// <summary>Acquires a task document's watch, never starts it, ends its task, and closes the Harness (upstream <c>acquire</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IWatchHandle<JsonObjectValue> Watch, WeakReference Harness)> NeverStartedWatch()
    {
        var (harness, _, _, id, finish) = await OpenWriter();
        var watch = (await harness.WatchDocAsync(ProgressDoc, id, Context))!;
        finish();
        await harness.WaitForTaskAsync(id, Context);
        // Its retirement is pending until a start that never comes: the watch is open.
        Check(!await Settled(watch.Closed), "open");
        await harness.CloseAsync(Context);
        return (watch, new WeakReference(harness));
    }

    /// <summary>Upstream's <c>collect()</c> (SessionWatchesCases.cs).</summary>
    private static async Task CollectGarbage()
    {
        for (var round = 0; round < 5; round++)
        {
            await Task.Delay(1);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }
}
