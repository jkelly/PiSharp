// PiSharp-authored, for Pi v1.2.0's scheduler (durable work list area c; packages/durable/CHANGELOG.md 1.2.0, "The scheduler no longer
// visits every live task on each pass"). Not an upstream file: upstream's test/scheduler-fanout-bench.ts makes its fan-out with nested tool
// calls (api.executeTool), which arrive with work list area a, so it is ported there (SchedulerFanoutBench.cs). These two measure the same
// scheduler work without nested calls, and node/scheduler-scale-bench.ts runs the same two scenarios on the pinned upstream for comparison:
// - fanout: one model-issued tool creates N child tasks it owns with api.createTask(), each in its own commit and all at once, and waits for
//   all of them; each child completes in its first phase. In-memory storage, so the time is the Harness's and the scheduler's own.
//   Quadratic work shows as four times the time per doubling of N.
// - chain: one commit creates N tasks, each owning the next; every one completes in its first phase, holding as completing while the task
//   below it lives, so the chain settles bottom-up once the last ends (upstream's changelog: 500 tasks took 12 s before the rewrite, 50 ms
//   after; 20,000 take 1.5 s). The time runs from the creating commit to the top task's terminal record.
using System.Diagnostics;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Scheduler fan-out and chain timings (PiSharp-authored; see the file header).</summary>
public static class SchedulerScaleBench
{
    /// <summary>The default fan-out sizes, upstream's scheduler-fanout-bench.ts's.</summary>
    public static IReadOnlyList<int> DefaultFanout { get; } = [250, 500, 1000, 2000, 4000];

    /// <summary>The default chain depths.</summary>
    public static IReadOnlyList<int> DefaultChain { get; } = [500, 2000, 20_000];

    /// <summary>One row: the scenario, its size, the time and the commits.</summary>
    public sealed record Row(string Scenario, int Size, double Ms, int Commits);

    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Completes in its first phase.</summary>
    private static readonly DurableTask Leaf = Tasks.DefineTask(new TaskDefinition
    {
        Name = "bench.leaf",
        Version = 1,
        Initial = _ => JsonValue.Object(KeyValuePair.Create("phase", (JsonValue)"run")),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx),
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
    });

    /// <summary>Runs the fan-out sizes, then the chain depths; writes a table each and returns the rows.</summary>
    public static async Task<IReadOnlyList<Row>> RunAsync(IReadOnlyList<int> fanout, IReadOnlyList<int> chain, TextWriter output)
    {
        var rows = new List<Row>();
        output.WriteLine("fanout | ms     | commits | ms per task");
        foreach (var n in fanout)
        {
            var row = await FanoutAsync(n);
            rows.Add(row);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n,-6} | {Math.Round(row.Ms),-6} | {row.Commits,-7} | {row.Ms / n:F2}"));
        }
        output.WriteLine("chain  | ms     | commits | ms per task");
        foreach (var n in chain)
        {
            var row = await ChainAsync(n);
            rows.Add(row);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n,-6} | {Math.Round(row.Ms),-6} | {row.Commits,-7} | {row.Ms / n:F3}"));
        }
        return rows;
    }

    /// <summary>One model-issued tool creating <paramref name="count"/> child tasks at once and waiting for all of them.</summary>
    public static async Task<Row> FanoutAsync(int count)
    {
        var registry = Registry.Create();
        var fan = Define.Tool(new ToolRegistration
        {
            Name = "fan",
            Description = "Create child tasks in parallel",
            Parameters = ToolRegistration.EmptyParameters,
            Execute = async (_, api, callContext) =>
            {
                var ids = await Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
                    api.CreateTaskAsync(Leaf, JsonValue.Null, new TaskOptions(TaskOwnership.Task(api.TaskId)), callContext)));
                var settled = await Task.WhenAll(ids.Select(id => api.WaitForTaskAsync(id, callContext)));
                var failed = settled.Count(record => ((SettlingTaskState)record.State).Outcome.Status != TaskOutcomeStatus.Completed);
                return new ToolExecutionResult { Output = JsonValue.Array(JsonValue.Object(
                    KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)$"{failed} failed"))), IsError = failed > 0 };
            },
        });
        registry.Install(Define.Extension(new Extension { Name = "bench", Tools = [fan], Tasks = [Leaf] }));
        var storage = new CommitCounter(new MemoryStorage());
        var models = new FauxModels(FauxModels.Calls("fan", JsonObjectValue.Empty, "fan"), FauxModels.Text("done"));
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = FauxModels.Ref });
        var started = Stopwatch.GetTimestamp();
        var settledRun = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        if (settledRun.Status != SubmissionStatus.Done) throw new InvalidOperationException($"run: {WireStatus(settledRun.Status)}");
        await harness.WaitForIdleAsync(Context);
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // A run whose children failed measures nothing.
        var page = await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
        var result = page.Items.FirstOrDefault(entry => entry.Kind == "pi.tool-result")?.Model;
        if (result is not { Count: > 0 } || result[0] is not JsonObjectValue message || message["isError"] is JsonBooleanValue { Value: true })
            throw new InvalidOperationException("run: child tasks failed");
        await harness.CloseAsync(Context);
        return new Row("fanout", count, ms, storage.Commits);
    }

    /// <summary>A chain of <paramref name="depth"/> tasks created in one commit, each owning the next, settling bottom-up.</summary>
    public static async Task<Row> ChainAsync(int depth)
    {
        var registry = Registry.Create();
        registry.Install(Define.Extension(new Extension { Name = "bench", Tasks = [Leaf] }));
        var storage = new CommitCounter(new MemoryStorage());
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, Context);
        var root = await harness.RootAsync(Context);
        harness.Resume();
        var started = Stopwatch.GetTimestamp();
        var top = await root.CommitAsync(async tx =>
        {
            var top = await tx.CreateTaskAsync(Leaf, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation));
            var owner = top;
            for (var n = 1; n < depth; n++) owner = await tx.CreateTaskAsync(Leaf, JsonValue.Null, new TaskOptions(TaskOwnership.Task(owner)));
            return top;
        }, Context);
        var settled = await harness.WaitForTaskAsync(top, Context);
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (((SettlingTaskState)settled.State).Outcome.Status != TaskOutcomeStatus.Completed) throw new InvalidOperationException("chain: top failed");
        await harness.CloseAsync(Context);
        return new Row("chain", depth, ms, storage.Commits);
    }

    /// <summary>Comma-separated sizes, or the defaults.</summary>
    public static IReadOnlyList<int> ParseSizes(string? argument, IReadOnlyList<int> defaults) =>
        argument is null ? defaults : [.. argument.Split(',').Select(size => int.Parse(size, CultureInfo.InvariantCulture))];

    private static string WireStatus(SubmissionStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Counts the commits a storage takes (upstream's patched <c>storage.commit</c>).</summary>
    private sealed class CommitCounter(IStorage inner) : IStorage
    {
        private int commits;

        public int Commits => Volatile.Read(ref commits);

        public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
        {
            Interlocked.Increment(ref commits);
            return inner.CommitAsync(writes, context);
        }

        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            inner.FindDocumentAsync(address, at, context);
        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
            inner.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanEntriesAsync(query, limit, cursor, context);
        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
        public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanTasksAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanDocumentsAsync(query, limit, cursor, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
    }
}
