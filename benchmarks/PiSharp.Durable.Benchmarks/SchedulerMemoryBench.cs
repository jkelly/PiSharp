// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/scheduler-memory-bench.ts. Scheduler memory benchmark: under one
// live parent task, N subagent cycles run and end: a tool task owned by the parent, a conversation owned by the tool task, and a task in that
// conversation. Reports the scheduler's ownership index sizes and the bytes live objects retain (HeapRetained.cs) once all cycles ended, then
// the storage reads a later message into an old subagent conversation costs. On SQLite, so durable history stays out of the heap.
// Upstream's performance.now() is a Stopwatch; createModels() is a models catalog without providers (no task makes a request); its patched
// storage.task and storage.conversation are a counting decorator. Upstream reads the index sizes through the Harness's SCHEDULER_INDEX_SIZES
// getter right after the last cycle; this assembly reaches the internal DurableHarness.SchedulerIndexSizes by reflection, after the
// scheduler's pending passes have run (DurableTaskScheduler.QuiesceAsync: here they run on the thread pool, not in drained microtasks).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>scheduler-memory-bench.ts</c> (<c>npm run bench:scheduler-memory</c>).</summary>
public static class SchedulerMemoryBench
{
    /// <summary>Upstream's default cycle counts.</summary>
    public static IReadOnlyList<int> DefaultSizes { get; } = [1_000, 10_000];

    /// <summary>One row: the timing, the index sizes, the heap growth and the reads of a later message.</summary>
    public sealed record Row(int Cycles, double Ms, int Settled, int Edges, int Below, long Heap, int LateReads);

    /// <summary>Runs each size, writes upstream's table and returns the rows. Throws, as upstream does, when the indexes grew with history.</summary>
    public static async Task<IReadOnlyList<Row>> RunAsync(IReadOnlyList<int> sizes, TextWriter output, string? tempRoot = null)
    {
        var rows = new List<Row>();
        output.WriteLine("cycles | ms     | settled | edges  | below | heap growth             | reads for a later message");
        foreach (var n in sizes)
        {
            var row = await RunAsync(n, tempRoot);
            var heap = string.Create(CultureInfo.InvariantCulture, $"{row.Heap / 1024.0 / 1024.0:F1} MiB, {Math.Round((double)row.Heap / n)} B/cycle");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{n,-6} | {Math.Round(row.Ms),-6} | {row.Settled,-7} | {row.Edges,-6} | {row.Below,-5} | {heap,-23} | {row.LateReads}"));
            // The scheduler's ownership indexes must not grow with ended history; the heap still includes the Session's caches.
            if (row.Settled > 0 || row.Edges > 1)
                throw new InvalidOperationException($"ownership indexes grew with history: settled {row.Settled}, edges {row.Edges}, below {row.Below}");
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Upstream <c>run(cycles)</c>.</summary>
    public static async Task<Row> RunAsync(int cycles, string? tempRoot = null)
    {
        var context = DurableContext.Background;
        var directory = Path.Combine(tempRoot ?? Path.GetTempPath(), "pi-durable-memory-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storage = new CountingStorage(await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite")));
            var waiting = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
            // Holds until released by name, then completes.
            var hold = Tasks.DefineTask(new TaskDefinition
            {
                Name = "bench.hold",
                Version = 1,
                Initial = _ => JsonValue.Object(KeyValuePair.Create("phase", (JsonValue)"hold")),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["hold"] = async (task, runtime, ctx) =>
                    {
                        var name = ((JsonStringValue)((JsonObjectValue)task.Input)["name"]).Value;
                        await waiting.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
            void Release(string name) =>
                waiting.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            static JsonObjectValue Named(string name) => JsonValue.Object(KeyValuePair.Create("name", (JsonValue)name));

            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "bench", Tasks = [hold] }));
            var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, context);
            var root = await harness.RootAsync(context);
            harness.Resume();
            var parent = await root.CommitAsync(tx => tx.CreateTaskAsync(hold, Named("parent"), new TaskOptions(TaskOwnership.Conversation)), context);
            var heapBefore = await HeapRetained.RetainedBytesAsync();
            ConversationId? first = null;
            var started = Stopwatch.GetTimestamp();
            for (var n = 0; n < cycles; n++)
            {
                var index = n;
                var (tool, inner, child) = await root.CommitAsync(async tx =>
                {
                    var tool = await tx.CreateTaskAsync(hold, Named($"tool{index}"), new TaskOptions(TaskOwnership.Task(parent)));
                    var child = await tx.CreateConversationAsync(ConversationOwnership.Task(tool));
                    var inner = await tx.CreateTaskAsync(hold, Named($"inner{index}"),
                        new TaskOptions(TaskOwnership.Conversation) { ConversationId = child.Id });
                    return (tool, inner, child.Id);
                }, context);
                Release($"inner{n}");
                Release($"tool{n}");
                await harness.WaitForTaskAsync(inner, context);
                await harness.WaitForTaskAsync(tool, context);
                first ??= child;
            }
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var internals = new SchedulerInternals(harness);
            await internals.QuiesceAsync();
            var heapAfter = await HeapRetained.RetainedBytesAsync();
            var (settled, edges, below) = internals.IndexSizes;
            // A later message into an old subagent conversation: a task created there, run, and ended.
            var readsBefore = storage.Reads;
            var old = (await harness.ConversationAsync(first!.Value, context))!;
            var late = await old.CommitAsync(tx => tx.CreateTaskAsync(hold, Named("late"), new TaskOptions(TaskOwnership.Conversation)), context);
            Release("late");
            await harness.WaitForTaskAsync(late, context);
            var lateReads = storage.Reads - readsBefore;
            Release("parent");
            await harness.WaitForTaskAsync(parent, context);
            await harness.CloseAsync(context);
            return new Row(cycles, ms, settled, edges, below, heapAfter - heapBefore, lateReads);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Upstream's arguments: comma-separated cycle counts, or the defaults.</summary>
    public static IReadOnlyList<int> ParseSizes(string? argument) =>
        argument is null ? DefaultSizes : [.. argument.Split(',').Select(size => int.Parse(size, CultureInfo.InvariantCulture))];

    /// <summary>The Harness's internal scheduler barrier and index sizes (upstream's <c>harness[SCHEDULER_INDEX_SIZES]</c>).</summary>
    internal sealed class SchedulerInternals(IHarness harness)
    {
        private readonly object scheduler = harness.GetType().GetProperty("Scheduler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness)!;

        public Task QuiesceAsync() =>
            (Task)scheduler.GetType().GetMethod("QuiesceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scheduler, null)!;

        public (int Settled, int Edges, int Below) IndexSizes
        {
            get
            {
                var sizes = harness.GetType().GetProperty("SchedulerIndexSizes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness)!;
                int Read(string name) => (int)sizes.GetType().GetProperty(name)!.GetValue(sizes)!;
                return (Read("Settled"), Read("Edges"), Read("Below"));
            }
        }
    }

    /// <summary>Counts the task and conversation reads a storage serves (upstream's patched <c>storage.task</c> and
    /// <c>storage.conversation</c>).</summary>
    private sealed class CountingStorage(IStorage inner) : IStorage
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
        {
            Interlocked.Increment(ref reads);
            return inner.GetTaskAsync(id, context);
        }

        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context)
        {
            Interlocked.Increment(ref reads);
            return inner.GetConversationAsync(id, context);
        }

        public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) => inner.CommitAsync(writes, context);
        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            inner.FindDocumentAsync(address, at, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
            inner.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanEntriesAsync(query, limit, cursor, context);
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
