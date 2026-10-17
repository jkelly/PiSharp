// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): a differential check of the task graph view against the pinned upstream. There is no
// upstream test file: Fixtures/v1.2.0/task-graph.upstream.json holds what the pinned pi-durable delivers to a task graph watch acquired before
// the run (the acquisition graph, each frame's operations, the last value) and what a fresh taskGraph() state reads at four pauses, for the
// scenario of Fixtures/capture-task-graph.mjs on MemoryStorage: harness-task-graph.test.ts's family, with the parent completing only once
// the late child's phase started, so only one task can run at each step and the frame sequence does not depend on scheduling order. Values
// are compared as JSON; frames, and every node's keys, must also match upstream's text, key order included. The scenario runs on memory,
// JSONL and SQLite and must deliver the same frames, IDs included. Upstream's `setTimeout(0)` before reading the last value is
// TaskGraphTests.Drained (a Session line barrier).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessTaskGraphDifferentialCases() =>
    [
        TaskGraphDifferentialTests.Case("harness.task-graph-differential.family-frames", () => Task.FromResult<IStorage>(new MemoryStorage())),
        TaskGraphDifferentialTests.Case("harness.task-graph-differential.jsonl.family-frames",
            async () => await JsonlStorage.OpenAsync(Temp("task-graph-differential-jsonl"), DurableContext.Background)),
        TaskGraphDifferentialTests.Case("harness.task-graph-differential.sqlite.family-frames",
            async () => await SqliteStorage.OpenAsync(Path.Combine(Temp("task-graph-differential-sqlite"), "session.sqlite"))),
    ];
}

/// <summary>The harness.task-graph-differential.* cases.</summary>
internal static class TaskGraphDifferentialTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly Lazy<JsonObjectValue> Upstream = new(() => (JsonObjectValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.2.0", "task-graph.upstream.json"))));

    internal static (string, Func<Task>) Case(string id, Func<Task<IStorage>> fresh) => Program.Case(id, async () =>
    {
        var expected = Upstream.Value;
        var child = new Deferred();
        var late = new Deferred();
        var lateStarted = new Deferred();
        var (parentTask, childTask) = TaskGraphTests.Family(child.Task, late.Task, () => lateStarted.Task, lateStarted.Resolve);
        var (harness, _, _) = await OpenTasks(await fresh(), [parentTask, childTask]);
        var root = await harness.RootAsync(Context);
        var watch = await harness.WatchTaskGraphAsync(Context);
        var initial = watch.Value;
        var frames = new SyncList<string>();
        watch.Start((_, ops, _) =>
        {
            frames.Add(OpsJson(ops));
            return Task.CompletedTask;
        });
        async Task<TaskGraph> Fresh()
        {
            var state = await harness.TaskGraphAsync(Context);
            var value = state.Value;
            state.Dispose();
            return value;
        }
        var snapshots = new List<KeyValuePair<string, JsonValue>>();

        var parent = await root.CommitAsync(tx => tx.CreateTaskAsync(parentTask, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
        snapshots.Add(KeyValuePair.Create("created", (JsonValue)(await Fresh()).ToJson()));
        harness.Resume();
        await Eventually(() => watch.Value.Node(parent)?.State.Status == DurableTaskStatus.Waiting &&
            watch.Value.Tasks.Values.Any(node => node.State.Status == DurableTaskStatus.Running));
        snapshots.Add(KeyValuePair.Create("waiting", (JsonValue)(await Fresh()).ToJson()));
        child.Resolve();
        await Eventually(() => watch.Value.Node(parent)?.State.Status == DurableTaskStatus.Completing &&
            watch.Value.Tasks.Values.Any(node => node.State.Status == DurableTaskStatus.Running));
        snapshots.Add(KeyValuePair.Create("completing", (JsonValue)(await Fresh()).ToJson()));
        late.Resolve();
        await harness.WaitForTaskAsync(parent, Context);
        await TaskGraphTests.Drained(harness);
        var last = watch.Value;
        await watch.StopAsync();
        snapshots.Add(KeyValuePair.Create("drained", (JsonValue)(await Fresh()).ToJson()));
        await harness.CloseAsync(Context);

        Equal(N(expected["parent"]), (double)parent.Value, "parent ID");
        Equal(expected["initial"].ToJson(), initial.ToJson().ToJson(), "initial");
        var upstreamFrames = ((JsonArrayValue)expected["frames"]).Select(frame => frame.ToJson()).ToList();
        var actualFrames = frames.Snapshot();
        for (var index = 0; index < Math.Max(upstreamFrames.Count, actualFrames.Count); index++)
            Equal(index < upstreamFrames.Count ? upstreamFrames[index] : "<end>", index < actualFrames.Count ? actualFrames[index] : "<end>",
                $"frame {index}");
        Equal(expected["final"].ToJson(), last.ToJson().ToJson(), "final");
        Equal(expected["snapshots"].ToJson(), JsonValue.Object(snapshots).ToJson(), "snapshots");
    });
}
