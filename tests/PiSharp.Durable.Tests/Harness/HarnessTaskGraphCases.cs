// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-task-graph.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and SessionSupport.cs, and these:
// - Backends. Upstream runs four cases on MemoryStorage (one through ControlledStorage) and the reopen case on a SQLite file. Nothing in
//   them depends on the backend, so HarnessTaskGraphCases() runs them on MemoryStorage with the reopen case on a SQLite file as upstream
//   (harness.task-graph.*), HarnessTaskGraphJsonlCases() on fresh JSONL directories, the reopen case reopening one
//   (harness.task-graph.jsonl.*), and HarnessTaskGraphSqliteCases() the four others on fresh SQLite files (harness.task-graph.sqlite.*).
// - A TaskGraph is a typed class; toEqual compares ToJson(), toBe is ReferenceEquals, and statuses() is a sorted list of "kind#id status".
// - flush() is Drained(): a Session line barrier, after which every state and watch delivery of the commits before it has run (see
//   HarnessViewCases.cs). eventually() is Eventually.
// - The watch listener's replay check, an expect inside the listener upstream, is collected and asserted by the test body.
// - An aborted controller is a DurableCancellation cancelled with Exception("cancelled"), which is what the acquisition rejects with.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessTaskGraphCases() =>
        TaskGraphTests.Cases("harness.task-graph.", () => Task.FromResult<IStorage>(new MemoryStorage()), TaskGraphTests.SqliteReopen);

    private static IEnumerable<(string, Func<Task>)> HarnessTaskGraphJsonlCases() =>
        TaskGraphTests.Cases("harness.task-graph.jsonl.",
            async () => await JsonlStorage.OpenAsync(Temp("task-graph-jsonl"), DurableContext.Background), TaskGraphTests.JsonlReopen);

    private static IEnumerable<(string, Func<Task>)> HarnessTaskGraphSqliteCases() =>
        TaskGraphTests.Cases("harness.task-graph.sqlite.",
            async () => await SqliteStorage.OpenAsync(Path.Combine(Temp("task-graph-sqlite"), "graph.sqlite")), null);
}

/// <summary>The harness.task-graph.* cases and their support.</summary>
internal static class TaskGraphTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>A backend the reopen case opens twice over one location.</summary>
    internal sealed record Reopenable(string Name, Func<Func<Task<IStorage>>> Location);

    internal static readonly Reopenable SqliteReopen = new("sqlite", () =>
    {
        var path = Path.Combine(Temp("pi-durable-graph-"), "session.sqlite");
        return async () => await SqliteStorage.OpenAsync(path);
    });

    internal static readonly Reopenable JsonlReopen = new("jsonl", () =>
    {
        var directory = Temp("pi-durable-graph-");
        return async () => await JsonlStorage.OpenAsync(directory, DurableContext.Background);
    });

    private static readonly JsonValue WorkStep = O("""{"phase":"work"}""");

    /// <summary>
    /// A parent that creates a child task and a conversation it owns, waits for the child, creates a second child, and completes while
    /// that child is still live, so its outcome is held as <c>completing</c> (upstream <c>family</c>).
    /// </summary>
    /// <remarks>For the differential scenario, <paramref name="lateStarted"/> runs when the late child's phase starts and
    /// <paramref name="finishing"/> runs before the parent completes.</remarks>
    internal static (DurableTask Parent, DurableTask Child) Family(Task child, Task late, Func<Task>? finishing = null, Action? lateStarted = null)
    {
        var childTask = Tasks.DefineTask(new TaskDefinition
        {
            Name = "test.graph-child",
            Version = 1,
            Initial = _ => WorkStep,
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["work"] = async (task, runtime, ctx) =>
                {
                    var isLate = ((JsonObjectValue)task.Input)["late"] is JsonBooleanValue { Value: true };
                    if (isLate) lateStarted?.Invoke();
                    await (isLate ? late : child);
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            },
            Abort = (_, _, _) => Task.CompletedTask,
        });
        var parentTask = Tasks.DefineTask(new TaskDefinition
        {
            Name = "test.graph-parent",
            Version = 1,
            Initial = _ => O("""{"phase":"spawn"}"""),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["spawn"] = async (task, runtime, ctx) =>
                {
                    var ownership = ConversationOwnership.Task(task.Id);
                    // Two conversations in a commit that leaves the parent's record unchanged.
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        await tx.CreateConversationAsync(ownership);
                        await tx.CreateConversationAsync(ownership);
                    }, ctx);
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        var created = await tx.CreateTaskAsync(childTask, O("""{"late":false}"""), new TaskOptions(TaskOwnership.Task(task.Id)));
                        return (TaskState?)NextTaskState.Waiting(Obj(("phase", "join"), ("child", created.ToJson())), [created], JoinPolicy.AllSettled);
                    }, ctx);
                },
                ["join"] = async (task, runtime, ctx) =>
                {
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        await tx.CreateTaskAsync(childTask, O("""{"late":true}"""), new TaskOptions(TaskOwnership.Task(task.Id)));
                        return (TaskState?)NextTaskState.Running(O("""{"phase":"finish"}"""));
                    }, ctx);
                },
                ["finish"] = async (_, runtime, ctx) =>
                {
                    if (finishing is not null) await finishing();
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            },
            Abort = (_, _, _) => Task.CompletedTask,
        });
        return (parentTask, childTask);
    }

    private static (DurableTask Parent, DurableTask Child) GatesNever()
    {
        var never = new TaskCompletionSource().Task;
        return Family(never, never);
    }

    /// <summary>Node states by task kind, for compact assertions (upstream <c>statuses</c>), as sorted "kind#id status" lines.</summary>
    internal static List<string> Statuses(TaskGraph graph) =>
        [.. graph.Tasks.Values.Select(node => $"{node.Kind}#{node.Id.Value} {StatusName(node.State.Status)}").Order(StringComparer.Ordinal)];

    internal static string StatusName(DurableTaskStatus status) => status.ToString().ToLowerInvariant();

    internal static void SameGraph(JsonValue expected, TaskGraph actual, string what) =>
        Check(JsonValue.DeepEquals(expected, actual.ToJson()), $"{what}: expected {expected.ToJson()}, actual {actual}");

    /// <summary>Upstream <c>flush()</c>; see the file header.</summary>
    internal static Task Drained(IHarness harness) => ((Session)harness).FlushLineAsync();

    private static Task<TaskId> StartOwned(IConversation root, DurableTask task, JsonValue? input = null, bool background = false) =>
        root.CommitAsync(tx => tx.CreateTaskAsync(task, input ?? JsonValue.Null,
            new TaskOptions(TaskOwnership.Conversation) { Background = background ? true : null }), Context);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh, Reopenable? reopen)
    {
        yield return Case(prefix + "follows-every-live-task-through-its-statuses-owner-edges-and-owned-conversations", async () =>
        {
            var child = new Deferred();
            var late = new Deferred();
            var (parentTask, childTask) = Family(child.Task, late.Task);
            var (harness, _, _) = await OpenTasks(await fresh(), [parentTask, childTask]);
            var root = await harness.RootAsync(Context);
            var seen = new SyncList<TaskGraph>();
            async Task<AttachedReplicatedState<TaskGraph>> Observe()
            {
                var opened = await harness.TaskGraphAsync(Context);
                opened.Subscribe((value, _, delivery) =>
                {
                    if (delivery.Kind == ReplicatedStateDeliveryKind.Update) seen.Add(value);
                });
                return opened;
            }
            var graph = await Observe();
            SameGraph(J("""{"tasks":{}}"""), graph.Value, "empty graph");
            // The advanced value equals a fresh build from Storage once the last observer left.
            async Task Rebuild()
            {
                var advanced = graph.Value;
                graph.Dispose();
                graph = await Observe();
                Check(!ReferenceEquals(graph.Value, advanced), "a rebuilt revision is a new value");
                SameGraph(advanced.ToJson(), graph.Value, "rebuilt");
            }

            var parent = await StartOwned(root, parentTask);
            await Drained(harness);
            SameJson($$"""
                {"id":{{parent.Value}},"kind":"test.graph-parent","conversationId":{{root.Id.Value}},"background":false,"abortRequested":false,
                 "state":{"status":"pending","phase":"spawn"},"conversations":[]}
                """, graph.Value.Node(parent)?.ToJson(), "created parent");

            harness.Resume();
            TaskId ChildId() => graph.Value.Tasks.Values.FirstOrDefault(node => node.Kind == "test.graph-child")?.Id ?? default;
            await Eventually(() =>
            {
                var value = graph.Value;
                return value.Tasks.Count == 2 && value.Tasks.Values.Any(node => node.Kind == "test.graph-child" && node.State.Status == DurableTaskStatus.Running);
            });
            var first = ChildId();
            var parentNode = graph.Value.Node(parent)!;
            SameJson($$"""{"status":"waiting","phase":"join","on":[{{first.Value}}],"policy":"allSettled"}""", parentNode.State.ToJson(), "waiting parent");
            Equal(2, parentNode.Conversations.Length, "owned conversations");
            Seq(parentNode.Conversations.OrderBy(id => id.Value), parentNode.Conversations, "conversations in ID order");
            var owned = parentNode.Conversations[0];
            var firstNode = graph.Value.Node(first)!;
            Equal<TaskId?>(parent, firstNode.Owner, "child owner");
            Equal(root.Id, firstNode.ConversationId, "child conversation");
            Check(await harness.ConversationAsync(owned, Context) is not null, "owned conversation exists");
            await Rebuild();

            child.Resolve();
            // The parent completes while the late child lives: its outcome is held.
            await Eventually(() => graph.Value.Node(parent)?.State.Status == DurableTaskStatus.Completing);
            SameJson("""{"status":"completing","outcome":"completed"}""", graph.Value.Node(parent)!.State.ToJson(), "completing parent");
            Check(graph.Value.Node(first) is null, "the first child left the graph");
            // Owned conversations stay listed while the owner lives.
            Seq(parentNode.Conversations, graph.Value.Node(parent)!.Conversations, "conversations kept");
            await Rebuild();

            late.Resolve();
            await harness.WaitForTaskAsync(parent, Context);
            await Drained(harness);
            SameGraph(J("""{"tasks":{}}"""), graph.Value, "drained graph");
            // Every revision is one commit that changed a node; none repeats its predecessor.
            var all = seen.Snapshot();
            for (var index = 1; index < all.Count; index++)
                Check(!JsonValue.DeepEquals(all[index].ToJson(), all[index - 1].ToJson()), $"revision {index} repeats its predecessor: {all[index]}");
            // The commit that created the two conversations published one revision setting them.
            Check(all.Any(value => value.Node(parent)?.Conversations.Length == 2), "a revision with both conversations");
            graph.Dispose();
            await harness.CloseAsync(Context);
        });

        if (reopen is not null)
            yield return Case(prefix + "builds-from-committed-tasks-shows-surviving-tasks-as-pending-after-reopen-and-marks-aborts", async () =>
            {
                var open = reopen.Location();
                var gate = new Deferred();
                var work = Tasks.DefineTask(new TaskDefinition
                {
                    Name = "test.graph-work",
                    Version = 1,
                    Initial = _ => WorkStep,
                    Phases = new Dictionary<string, TaskPhaseHandler>
                    {
                        ["work"] = async (_, runtime, ctx) =>
                        {
                            await await Task.WhenAny(gate.Task, Aborted(runtime.Signal));
                            await runtime.CommitAsync((_, _) => Completed(null), ctx);
                        },
                    },
                    Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
                });
                var firstRun = await OpenTasks(await open(), [work]);
                var firstRoot = await firstRun.Harness.RootAsync(Context);
                var (foreground, background, owned) = await firstRoot.CommitAsync(async tx =>
                {
                    var foreground = await tx.CreateTaskAsync(work, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation));
                    var background = await tx.CreateTaskAsync(work, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation) { Background = true });
                    var owned = await tx.CreateConversationAsync(ConversationOwnership.Task(foreground));
                    return (foreground, background, owned.Id);
                }, Context);
                firstRun.Harness.Resume();
                await Eventually(async () => (await firstRun.Harness.InspectAsync(Context)).Tasks.All(task => task.State.Kind == TaskInspectionKind.Running));
                var running = await firstRun.Harness.TaskGraphAsync(Context);
                Seq([$"test.graph-work#{foreground.Value} running", $"test.graph-work#{background.Value} running"], Statuses(running.Value), "running");
                await firstRun.Harness.CloseAsync(Context);

                // Acquired after reopen: built from the committed records and owner edges; open reconciled running to pending.
                var (harness, _, _) = await OpenTasks(await open(), [work]);
                var watch = await harness.WatchTaskGraphAsync(Context);
                Seq([$"test.graph-work#{foreground.Value} pending", $"test.graph-work#{background.Value} pending"], Statuses(watch.Value), "pending");
                Seq([owned], watch.Value.Node(foreground)!.Conversations, "owned conversation");
                Check(watch.Value.Node(background)!.Background, "background node");

                // Exact frames: replaying their operations from the acquisition revision gives each delivered value.
                JsonValue replica = watch.Value.ToJson();
                var frames = new SyncList<ImmutableArray<Op>>();
                var mismatches = new SyncList<string>();
                watch.Start((value, ops, _) =>
                {
                    replica = DeltaApply.Apply(replica, ops);
                    if (!JsonValue.DeepEquals(replica, value.ToJson())) mismatches.Add($"replayed {replica.ToJson()}, delivered {value}");
                    frames.Add(ops);
                    return Task.CompletedTask;
                });
                harness.Resume();
                await Eventually(() => watch.Value.Node(background)?.State.Status == DurableTaskStatus.Running);
                Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(background, Context), "abort");
                await harness.WaitForTaskAsync(background, Context);
                await Drained(harness);
                Seq(Array.Empty<string>(), mismatches.Snapshot(), "replay mismatches");
                var all = frames.Snapshot();
                var key = background.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Check(all.Any(ops => ops is [SetOp { Value: JsonObjectValue node } set] && set.Path.SequenceEqual([PathSegment.Of("tasks"), PathSegment.Of(key)]) &&
                    node["abortRequested"] is JsonBooleanValue { Value: true }), "a frame marks the abort: " + string.Join(" ", all.Select(ops => OpsJson(ops))));
                Equal($$"""[["d",["tasks","{{key}}"]]]""", OpsJson(all[^1]), "last frame");
                Seq([foreground.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    ((JsonObjectValue)((JsonObjectValue)replica)["tasks"]).Keys, "replica tasks");
                await watch.StopAsync();
                gate.Resolve();
                await harness.WaitForTaskAsync(foreground, Context);
                await harness.CloseAsync(Context);
            });

        yield return Case(prefix + "lists-owned-conversations-in-id-order-whatever-order-one-commit-creates-them-in", async () =>
        {
            var gate = new Deferred();
            var created = new SyncList<long>();
            var spawner = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.graph-spawner",
                Version = 1,
                Initial = _ => O("""{"phase":"spawn"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["spawn"] = async (task, runtime, ctx) =>
                    {
                        await runtime.CommitAsync(async (tx, _) =>
                        {
                            var ownership = ConversationOwnership.Task(task.Id);
                            var at = EntryId.FromNumber((long)N(((JsonObjectValue)task.Input)["at"]));
                            var forkedTask = tx.ForkConversationAsync(runtime.ConversationId, at, ownership);
                            var freshTask = tx.CreateConversationAsync(ownership);
                            await Task.WhenAll(forkedTask, freshTask);
                            created.Add((await forkedTask).Id.Value);
                            created.Add((await freshTask).Id.Value);
                        }, ctx);
                        await gate.Task;
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var (harness, _, _) = await OpenTasks(await fresh(), [spawner]);
            var root = await harness.RootAsync(Context);
            var graph = await harness.TaskGraphAsync(Context);
            var id = await root.CommitAsync(async tx =>
            {
                var entry = await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" });
                return await tx.CreateTaskAsync(spawner, Obj(("at", entry.Id.ToJson())), new TaskOptions(TaskOwnership.Conversation));
            }, Context);
            harness.Resume();
            await Eventually(() => (graph.Value.Node(id)?.Conversations.Length ?? 0) == 2);
            var advanced = graph.Value;
            Seq(created.Snapshot().Order(), advanced.Node(id)!.Conversations.Select(conversation => conversation.Value), "conversations");
            graph.Dispose();
            var rebuilt = await harness.TaskGraphAsync(Context);
            SameGraph(advanced.ToJson(), rebuilt.Value, "rebuilt");
            rebuilt.Dispose();
            gate.Resolve();
            await harness.WaitForTaskAsync(id, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "registers-nothing-for-an-acquisition-cancelled-while-it-waits-for-the-line", async () =>
        {
            var storage = new ControlledStorage(await fresh());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            await StartOwned(root, GatesNever().Child, O("""{"late":false}"""));
            var held = storage.HoldCommits();
            var blocking = harness.CommitAsync(async tx => { await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "blocker" }); }, Context);
            await held.Entered;
            var controller = Context.WithCancel();
            var cancelled = harness.WatchTaskGraphAsync(controller.Context);
            controller.Cancel(new Exception("cancelled"));
            held.Release();
            await blocking;
            await RejectsAsync(() => cancelled, "cancelled", "cancelled acquisition");
            // No observer kept the mount: each new observer builds a new revision.
            var first = await harness.TaskGraphAsync(Context);
            var value = first.Value;
            first.Dispose();
            var second = await harness.TaskGraphAsync(Context);
            Check(!ReferenceEquals(second.Value, value), "a new revision");
            SameGraph(value.ToJson(), second.Value, "same graph");
            second.Dispose();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "publishes-no-revision-for-a-commit-that-changes-no-node-and-shares-one-mount-between-observers", async () =>
        {
            var gate = new Deferred();
            var reached = new Deferred();
            var memo = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.graph-memo",
                Version = 1,
                Initial = _ => WorkStep,
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["work"] = async (_, runtime, ctx) =>
                    {
                        await runtime.MemoAsync("seen", JsonValue.Boolean(true), ctx);
                        reached.Resolve();
                        await gate.Task;
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var (harness, _, _) = await OpenTasks(await fresh(), [memo]);
            var root = await harness.RootAsync(Context);
            var id = await StartOwned(root, memo);
            var first = await harness.TaskGraphAsync(Context);
            var second = await harness.TaskGraphAsync(Context);
            Same(first.Value, second.Value, "one mount");
            var updates = new SyncList<string>();
            first.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) updates.Add(value.Node(id) is { } node ? StatusName(node.State.Status) : "gone");
            });
            harness.Resume();
            await reached.Task;
            await Drained(harness);
            // Reservation changed the node; the memo commit did not.
            Seq(["running"], updates.Snapshot(), "updates");
            gate.Resolve();
            await harness.WaitForTaskAsync(id, Context);
            await Drained(harness);
            Seq(["running", "gone"], updates.Snapshot(), "updates");
            var last = first.Value;
            first.Dispose();
            second.Dispose();
            // No observer is left, so the mount was dropped: a new observer builds a new revision.
            var rebuilt = await harness.TaskGraphAsync(Context);
            SameGraph(last.ToJson(), rebuilt.Value, "rebuilt");
            Check(!ReferenceEquals(rebuilt.Value, last), "a new revision");
            rebuilt.Dispose();
            await harness.CloseAsync(Context);
        });
    }
}
