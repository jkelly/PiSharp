// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/session-document-cache-bench.ts. Session document cache
// benchmark: a Harness on SQLite creates N conversations and runs one short task in each, as a long-running agent with subagents does; then,
// at a few retention settings, reports the documents left in the cache, the bytes live objects retain per conversation (HeapRetained.cs),
// and the cost of reading a document of an old conversation again. Upstream's performance.now() is a Stopwatch; createModels() is a models
// catalog without providers (the task makes no request). Upstream casts the Harness to SessionImpl for its internal readOnLine() and
// documentCacheSizes; this assembly reaches the Session's internal FlushLineAsync and DocumentCacheSizes by reflection.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>session-document-cache-bench.ts</c> (<c>npm run bench:document-cache</c>).</summary>
public static class SessionDocumentCacheBench
{
    /// <summary>Upstream's default sizes.</summary>
    public static IReadOnlyList<int> DefaultSizes { get; } = [1_000, 5_000];

    /// <summary>Upstream's retention settings: infinite (Number.MAX_SAFE_INTEGER), ten minutes, 50 ms and 0.</summary>
    public static IReadOnlyList<(string Label, double Retention)> Retentions { get; } =
        [("infinite", 9_007_199_254_740_991), ("10 min", 600_000), ("50 ms", 50), ("0", 0)];

    /// <summary>Writes <c>pi.usage</c> of its conversation once, then completes (upstream <c>Work</c>).</summary>
    private static readonly DurableTask Work = Tasks.DefineTask(new TaskDefinition
    {
        Name = "bench.work",
        Version = 1,
        Initial = _ => JsonValue.Object(KeyValuePair.Create("phase", (JsonValue)"work")),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["work"] = (_, runtime, context) => runtime.CommitAsync(async (tx, _) =>
            {
                (await tx.DocAsync(UsageLedger.UsageDoc, runtime.ConversationId)).GetObject("tools")
                    .Set("bench", JsonValue.Object(KeyValuePair.Create("input", (JsonValue)1)));
                return NextTaskState.Completed(JsonValue.Null);
            }, context),
        },
        Abort = (_, runtime, context) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), context),
    });

    /// <summary>One row: the timing, the cache and the heap at one size and retention.</summary>
    public sealed record Row(int Conversations, string Retention, double Ms, int Cached, long Heap, double ReadUs);

    /// <summary>Runs every retention at each size, writes upstream's table and returns the rows.</summary>
    public static async Task<IReadOnlyList<Row>> RunAsync(IReadOnlyList<int> sizes, TextWriter output, int warmup = 200, string? tempRoot = null)
    {
        var rows = new List<Row>();
        output.WriteLine("conversations | retention   | ms     | cached docs | retained bytes        | old-doc read");
        foreach (var n in sizes)
        {
            foreach (var (label, retention) in Retentions)
            {
                var (ms, heap, cached, readUs) = await RunAsync(n, retention, warmup, tempRoot);
                var bytes = string.Create(CultureInfo.InvariantCulture, $"{heap / 1024.0 / 1024.0:F1} MiB, {Math.Round((double)heap / n)} B/conv");
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{n,-13} | {label,-11} | {Math.Round(ms),-6} | {cached,-11} | {bytes,-21} | {Math.Round(readUs)} µs"));
                rows.Add(new Row(n, label, ms, cached, heap, readUs));
            }
        }
        return rows;
    }

    /// <summary>Upstream <c>run(conversations, documentRetentionMs)</c>.</summary>
    public static async Task<(double Ms, long Heap, int Cached, double ReadUs)> RunAsync(int conversations, double documentRetentionMs, int warmup = 200,
        string? tempRoot = null)
    {
        var context = DurableContext.Background;
        var directory = Path.Combine(tempRoot ?? Path.GetTempPath(), "pi-durable-doc-cache-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storage = await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite"));
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "bench", Tasks = [Work] }));
            var harness = await Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = EmptyDurableModels.Instance,
                Registry = registry,
                Settings = new HarnessSettings { DocumentRetentionMs = documentRetentionMs },
            }, context);
            var session = new SessionInternals((Session)harness);
            harness.Resume();
            ConversationId? first = null;
            async Task Cycle()
            {
                var conversation = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), context);
                first ??= conversation.Id;
                var task = await conversation.CommitAsync(tx => tx.CreateTaskAsync(Work, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), context);
                var settled = await harness.WaitForTaskAsync(task, context);
                var status = ((SettlingTaskState)settled.State).Outcome.Status;
                if (status != TaskOutcomeStatus.Completed) throw new InvalidOperationException($"task {task} {status}");
            }
            // Warm up first: compiled code and storage caches would otherwise count as growth.
            for (var n = 0; n < warmup; n++) await Cycle();
            await session.FlushLineAsync();
            var heapBefore = await HeapRetained.RetainedBytesAsync();
            var started = Stopwatch.GetTimestamp();
            for (var n = 0; n < conversations; n++) await Cycle();
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // Wait past a short retention without committing, so only the timer can evict; then let its job run.
            if (documentRetentionMs is > 0 and < 1_000) await Task.Delay(TimeSpan.FromMilliseconds(documentRetentionMs * 4));
            await session.FlushLineAsync();
            var heap = await HeapRetained.RetainedBytesAsync() - heapBefore;
            var cached = session.DocumentCacheSizes.Cached;
            // One read of an old conversation's document: from the cache, or from storage after eviction.
            var readStarted = Stopwatch.GetTimestamp();
            for (var n = 0; n < 100; n++) await harness.SnapshotAsync(Live.LiveDoc, first!.Value, context);
            var readUs = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds / 100 * 1000;
            await harness.CloseAsync(context);
            return (ms, heap, cached, readUs);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The Session's internal line barrier and cache sizes (upstream's cast to SessionImpl).</summary>
    private sealed class SessionInternals(Session session)
    {
        private static readonly MethodInfo Flush = typeof(Session).GetMethod("FlushLineAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly PropertyInfo Sizes = typeof(Session).GetProperty("DocumentCacheSizes", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public Task FlushLineAsync() => (Task)Flush.Invoke(session, null)!;

        public (int Cached, int Idle, int Attached, int Sweeps) DocumentCacheSizes =>
            ((int Cached, int Idle, int Attached, int Sweeps))Sizes.GetValue(session)!;
    }

    /// <summary>Upstream's arguments: comma-separated sizes, or the defaults.</summary>
    public static IReadOnlyList<int> ParseSizes(string? argument) =>
        argument is null ? DefaultSizes : [.. argument.Split(',').Select(size => int.Parse(size, CultureInfo.InvariantCulture))];
}
