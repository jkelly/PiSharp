// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-observers.test.ts: the Harness's synchronous observers
// (harness.observers.observe-conversation-and-observe-task-graph.*: observeConversation, observeTaskGraph) and task document watches and
// observers, which end with their task (harness.observers.task-documents.*). Area watch-folding ported the watch cases of the task
// documents block (8b0caefc2), area observers the rest (8b0caefc2, with v1.5.0's shapes). Verbatim apart from the adaptations in
// SessionSupport.cs and HarnessSupport.cs, and these:
// - The garbage-collection cases use WeakReference and GC.Collect for WeakRef and --expose-gc, with what they hold built in a method the
//   JIT does not inline (SessionWatchesCases.cs); the Harness is the Session kernel, so its document cache sizes are the Session's.
// - flush() before a negative check is Settled's flush; where upstream collects right after a task wait, a Session line barrier lets the
//   watch's retirement call, which runs on the line, finish first. Where upstream checks an observer right after a task wait, a Session line
//   barrier lets the commit's publication finish: a wait here resumes on the thread pool, maybe before the observers after the scheduler.
// - An observer is Observer<T> over two callbacks; recorder() keeps each call with ControlledStorage.Adopting, or the end.
// - An aborted controller is a DurableCancellation cancelled with an exception, which the attachment faults with.
// - "hands listeners changes that keep nothing of the Harness": a change is a WatchValueCall record (kind, value, ops, retired), which has
//   no other state to clone; the check is its exact type, and that the Harness is collected while the changes are held.
using System.Runtime.CompilerServices;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
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

    /// <summary>A recorded call: an advance with whether it ran inside the storage's receive, or an end.</summary>
    private sealed record Recorded<T>(WatchValueCall<T>? Advance, bool Adopting, WatchEnd? End);

    /// <summary>An observer recording each call and whether it ran inside the storage's receive (upstream <c>recorder</c>).</summary>
    private sealed class Recorder<T>
    {
        public Recorder(ControlledStorage storage) => Observer = new Observer<T>((call, _) =>
        {
            // Durable never sends a failure call.
            var value = call as WatchValueCall<T> ?? throw new InvalidOperationException("Unexpected failure call");
            Calls.Add(new Recorded<T>(value, storage.Adopting, null));
        }, end => Calls.Add(new Recorded<T>(null, false, end)));

        public SyncList<Recorded<T>> Calls { get; } = new();
        public Observer<T> Observer { get; }
    }

    private static Task<EntryRecord> Note(IConversation root) =>
        root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = O("{}") }), Context);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        foreach (var observer in Observers(prefix + "observe-conversation-and-observe-task-graph.")) yield return observer;
        foreach (var document in TaskDocuments(prefix + "task-documents.")) yield return document;
    }

    private static IEnumerable<(string, Func<Task>)> Observers(string prefix)
    {
        yield return Case(prefix + "start-at-the-current-value-and-advance-at-the-publication-with-the-same-change-a-watch-gets", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            var view = new Recorder<ConversationView>(storage);
            var viewObservation = await harness.ObserveConversationAsync(root.Id, view.Observer, Context);
            var graph = new Recorder<TaskGraph>(storage);
            var graphObservation = await harness.ObserveTaskGraphAsync(graph.Observer, Context);
            Equal(root.Id, viewObservation.Value.Conversation.Id, "the view's conversation");
            SameJson("""{"tasks":{}}""", graphObservation.Value.ToJson(), "the graph");
            var watch = await root.WatchAsync(Context);
            var watched = new SyncList<WatchCall<ConversationView>>();
            watch.Start((call, _) =>
            {
                watched.Add(call);
                return Task.CompletedTask;
            });
            var entry = await Note(root);
            Equal(1, view.Calls.Count, "one view call");
            var call = view.Calls[0];
            // Published once Storage stored it, outside its call.
            Check(!call.Adopting, "not inside the storage's receive");
            Check(!call.Advance!.Retired, "not retired");
            Equal(entry.Id, call.Advance.Value.Entries[^1].Id, "the new entry");
            await ((Session)harness).FlushLineAsync();
            var delivered = watched.Snapshot();
            Equal(1, delivered.Count, "one watch call");
            var watchCall = Change(delivered[0]);
            Same(call.Advance.Value, watchCall.Value, "the same value");
            Equal(OpsJson(call.Advance.Ops), OpsJson(watchCall.Ops), "the same ops");
            Equal(call.Advance.Retired, watchCall.Retired, "the same retired");
            // No task changed: the graph got nothing.
            Equal(0, graph.Calls.Count, "no graph call");
            viewObservation.Detach();
            await Note(root);
            Equal(1, view.Calls.Count, "nothing after detach");
            await watch.StopAsync();
            var graphWatch = await harness.WatchTaskGraphAsync(Context);
            await harness.CloseAsync(Context);
            // S1: a Session close fails an observer (it may follow the next Session); a watch ends ended.
            var ends = graph.Calls.Snapshot();
            Equal(1, ends.Count, "one graph call");
            Equal(WatchEndKind.Failed, ends[0].End?.Kind, "failed");
            Equal("session closed", (ends[0].End!.Error as Exception)?.Message, "session closed");
            Equal(WatchEnd.Ended, await graphWatch.Closed, "the graph watch");
        });

        yield return Case(prefix + "end-with-the-error-that-failed-the-harness", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            var graph = new Recorder<TaskGraph>(storage);
            await harness.ObserveTaskGraphAsync(graph.Observer, Context);
            var failure = new InvalidOperationException("disk gone");
            storage.FailNextCommit(failure);
            await RejectsAsync(() => Note(root), "disk gone", "the failing commit");
            await harness.Closed;
            Seq([new Recorded<TaskGraph>(null, false, WatchEnd.Failed(failure))], graph.Calls.Snapshot(), "calls");
        });

        yield return Case(prefix + "report-a-throwing-observer-detach-it-and-end-it-with-the-error-and-keep-the-view-going", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var opened = await OpenTasks(storage, []);
            var harness = opened.Harness;
            var root = await harness.RootAsync(Context);
            var bug = new InvalidOperationException("observer bug");
            var ends = new SyncList<WatchEnd>();
            await harness.ObserveConversationAsync(root.Id, new Observer<ConversationView>((_, _) => throw bug, ends.Add), Context);
            var healthy = new Recorder<ConversationView>(storage);
            await harness.ObserveConversationAsync(root.Id, healthy.Observer, Context);
            await Note(root);
            await Note(root);
            Seq<Exception>([bug], opened.Reports.Snapshot(), "reports");
            Seq([ReportInfo.ListenerError], opened.Infos.Snapshot(), "infos");
            Seq([WatchEnd.ListenerError(bug)], ends.Snapshot(), "ends");
            Equal(2, healthy.Calls.Count, "the healthy observer");
            await harness.CloseAsync(Context);
            Equal(1, ends.Count, "one end");
            Equal(SessionEnd.Closed, await harness.Closed, "closed");
        });

        yield return Case(prefix + "reject-never-throw-on-a-closed-harness", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            await harness.CloseAsync(Context);
            Task view, graph;
            try
            {
                view = harness.ObserveConversationAsync(root.Id, new Recorder<ConversationView>(storage).Observer, Context);
                graph = harness.ObserveTaskGraphAsync(new Recorder<TaskGraph>(storage).Observer, Context);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("threw instead of faulting its task", error);
            }
            await RejectsAsync(() => view, "Session is closed", "view");
            await RejectsAsync(() => graph, "Session is closed", "graph");
        });

        yield return Case(prefix + "reject-an-attachment-cancelled-while-it-waits-on-the-line-and-attach-nothing", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            var view = new Recorder<ConversationView>(storage);
            var graph = new Recorder<TaskGraph>(storage);
            // A commit holds the line, so both attachments wait behind it.
            var held = storage.HoldCommits();
            var committing = Note(root);
            await held.Entered;
            var cancelled = Context.WithCancel();
            var attachingView = harness.ObserveConversationAsync(root.Id, view.Observer, cancelled.Context);
            var attachingGraph = harness.ObserveTaskGraphAsync(graph.Observer, cancelled.Context);
            cancelled.Cancel(new InvalidOperationException("gone"));
            held.Release();
            await committing;
            await RejectsAsync(() => attachingView, "gone", "view");
            await RejectsAsync(() => attachingGraph, "gone", "graph");
            await Note(root);
            await harness.CloseAsync(Context);
            Equal(0, view.Calls.Count, "no view call");
            Equal(0, graph.Calls.Count, "no graph call");
        });

        yield return Case(prefix + "advance-a-graph-observer-with-a-blocked-refresh-outside-any-commit", async () =>
        {
            var missing = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.observers.missing",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(null), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, registry, _) = await OpenTasks(storage, []);
            var graph = new Recorder<TaskGraph>(storage);
            var observation = await harness.ObserveTaskGraphAsync(graph.Observer, Context);
            var root = await harness.RootAsync(Context);
            // Scheduling is not resumed: the task stays pending, blocked while its definition is missing.
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(missing, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            var key = TaskGraph.Key(id);
            var first = graph.Calls.Snapshot();
            Equal(1, first.Count, "one call");
            Check(!first[0].Adopting, "not inside the storage's receive");
            Check(!first[0].Advance!.Retired, "not retired");
            Equal(BlockedReason.MissingTask, first[0].Advance!.Value.Node(id)?.Blocked, "blocked");
            Equal(1, first[0].Advance!.Value.Tasks.Count, "one task");
            var set = first[0].Advance!.Ops.Single() as SetOp;
            Check(set is not null && set.Path.SequenceEqual([PathSegment.Of("tasks"), PathSegment.Of(key)]), "a set of the node");
            SameJson("\"missing_task\"", ((JsonObjectValue)set!.Value)["blocked"], "the set node's blocked");
            registry.Install(Define.Extension(new Extension { Name = "late", Tasks = [missing] }));
            await Eventually(() => graph.Calls.Count == 2);
            // The registry change re-derives the reason on the line, with no commit and outside the storage's receive.
            var second = graph.Calls[1];
            Check(!second.Adopting, "outside the storage's receive");
            Check(!second.Advance!.Retired, "not retired");
            Equal($"""[["d",["tasks","{key}","blocked"]]]""", OpsJson(second.Advance.Ops), "the delete");
            Check(second.Advance.Value.Node(id)?.Blocked is null, "unblocked");
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            observation.Detach();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keep-no-detached-observer-alive", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            // Control: attached observers stay, so the collection below is real.
            var attached = await AttachUnreferenced(harness, root.Id, detach: false);
            await CollectGarbage();
            Check(attached.View.IsAlive, "an attached view observer stays");
            Check(attached.Graph.IsAlive, "an attached graph observer stays");
            foreach (var observation in attached.Observations) observation.Detach();

            var detached = await AttachUnreferenced(harness, root.Id, detach: true);
            await CollectGarbage();
            Check(!detached.View.IsAlive, "the detached view observer is gone");
            Check(!detached.Graph.IsAlive, "the detached graph observer is gone");
            await harness.CloseAsync(Context);
        });
    }

    private static IEnumerable<(string, Func<Task>)> TaskDocuments(string prefix)
    {

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

        yield return Case(prefix + "an-observer-gets-the-retirement-then-its-end-at-the-publication-of-the-commit-that-ends-the-task", async () =>
        {
            var (harness, storage, _, id, finish) = await OpenWriter();
            // The recorder, and whether the end ran inside the storage's receive.
            var progress = new SyncList<Recorded<JsonObjectValue>>();
            bool? endedAdopting = null;
            var observer = new Observer<JsonObjectValue>((call, _) => progress.Add(new Recorded<JsonObjectValue>(Change(call), storage.Adopting, null)),
                end =>
                {
                    endedAdopting = storage.Adopting;
                    progress.Add(new Recorded<JsonObjectValue>(null, false, end));
                });
            await harness.ObserveDocAsync(ProgressDoc, id, observer, Context);
            finish();
            await harness.WaitForTaskAsync(id, Context);
            // The terminal commit's publication runs on the line; the wait may resume before it reached the observer.
            await ((Session)harness).FlushLineAsync();
            var calls = progress.Snapshot();
            Equal(2, calls.Count, "two calls");
            SameJson("""{"lines":["started"]}""", calls[0].Advance?.Value, "the last value");
            Equal("[]", OpsJson(calls[0].Advance!.Ops), "no ops");
            Check(calls[0].Advance!.Retired, "retired");
            Check(!calls[0].Adopting, "not inside the storage's receive");
            Equal(WatchEnd.Ended, calls[1].End, "ended");
            Equal<bool?>(false, endedAdopting, "ended outside the storage's receive");
            await harness.CloseAsync(Context);
            Equal(2, progress.Count, "nothing at close");
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

        yield return Case(prefix + "hands-listeners-changes-that-keep-nothing-of-the-harness", async () =>
        {
            var (changes, harness) = await WatchUntilTheTaskEnds();
            Equal(2, changes.Count, "two changes");
            foreach (var change in changes) Equal(typeof(WatchValueCall<JsonObjectValue>), change.GetType(), "a plain value call");
            await CollectGarbage();
            Check(!harness.IsAlive, "the Harness is gone");
        });
    }

    /// <summary>Attaches observers only the Harness references; <paramref name="detach"/> says whether to detach them before returning
    /// (upstream <c>attach</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IReadOnlyList<IObservationDetach> Observations, WeakReference View, WeakReference Graph)> AttachUnreferenced(
        IHarness harness, ConversationId root, bool detach)
    {
        var view = new Observer<ConversationView>((_, _) => { }, _ => { });
        var graph = new Observer<TaskGraph>((_, _) => { }, _ => { });
        IReadOnlyList<IObservationDetach> observations =
        [
            new ObservationDetach<ConversationView>(await harness.ObserveConversationAsync(root, view, Context)),
            new ObservationDetach<TaskGraph>(await harness.ObserveTaskGraphAsync(graph, Context)),
        ];
        if (detach) foreach (var observation in observations) observation.Detach();
        return (observations, new WeakReference(view), new WeakReference(graph));
    }

    private interface IObservationDetach
    {
        void Detach();
    }

    private sealed class ObservationDetach<T>(Observation<T> observation) : IObservationDetach
    {
        public void Detach() => observation.Detach();
    }

    /// <summary>Watches a task document until its task ends, keeping every change, then closes the Harness (upstream <c>acquire</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(List<WatchCall<JsonObjectValue>> Changes, WeakReference Harness)> WatchUntilTheTaskEnds()
    {
        var (harness, _, _, id, finish) = await OpenWriter();
        var watch = (await harness.WatchDocAsync(ProgressDoc, id, Context))!;
        var changes = new SyncList<WatchCall<JsonObjectValue>>();
        watch.Start((call, _) =>
        {
            changes.Add(call);
            return Task.CompletedTask;
        });
        await harness.CommitAsync(async tx => { (await tx.DocAsync(ProgressDoc, id)).GetArray("lines").Add("more"); }, Context);
        await ((Session)harness).FlushLineAsync();
        finish();
        await harness.WaitForTaskAsync(id, Context);
        Equal(WatchEnd.Ended, await watch.Closed, "closed");
        await harness.CloseAsync(Context);
        return (changes.Snapshot(), new WeakReference(harness));
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
