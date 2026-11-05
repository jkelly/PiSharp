// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/storage.bench.ts
// The timed storage benchmarks on memory, SQLite, the store (StoreStorage over one SQLite file, area store-storage) and JSONL: every read scenario against one seeded storage per backend, every write
// scenario against a pool of freshly seeded storages (one per sample, so no sample sees an earlier sample's writes), and reopen plus the
// first exact read against copies of one seeded file or directory. Each scenario's result is checked once before it is timed. Upstream's
// Vitest bench options are kept: reads sample for at least 300 ms and 10 runs after a 75 ms / 3 run warmup; writes take exactly 20 samples
// after 5 warmups; reopen 20 after 3. The store backend opens a store over the file and Durable's storage in it under one owner, whose
// close closes the store too (StoreOwningStorage, as upstream's openStoreStorage of test/store-storage-support.ts); its reopen copies the
// file, as SQLite's does.
// Throughput (PiSharp-authored, Pi v1.5.0 area group-commit; upstream measured its group commit with an uncommitted probe, docs/group-commit.md
// section 9, and quotes "about 5 -> 440 agent runs per second" for one Harness with 128 conversations over storage with a 4 ms round trip):
// one Session's commit throughput and one Harness's run throughput with N conversations working at once, over MemoryStorage behind a fixed
// round trip on every call (LatencyStorage; PreciseDelay keeps sub-millisecond waits, as Task.Delay rounds up to the 15.6 ms Windows timer
// tick) and over local SQLite. Each Session conversation appends one entry per commit (session commits), or changes its own cached
// conversation document (session changes: a commit that reads nothing from Storage, until area publication-caches gives the entry path
// its record cache); each Harness run is one faux text reply. Prints commits or runs per second, the storage calls and the mean commits
// per call (the group size), and the median run.
using System.Diagnostics;
using System.Globalization;
using PiSharp.Chord;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Store;
using PiSharp.Durable.Testing;
using ChordTables = PiSharp.Chord.Store.Tables;
using IStore = PiSharp.Chord.Store.IStore;

namespace PiSharp.Durable.Benchmarks;

/// <summary>A storage benchmark backend.</summary>
public enum BenchBackend
{
    /// <summary>MemoryStorage.</summary>
    Memory,
    /// <summary>SqliteStorage over one file.</summary>
    Sqlite,
    /// <summary>StoreStorage on a store over one SQLite file.</summary>
    Store,
    /// <summary>JsonlStorage over one directory.</summary>
    Jsonl,
}

/// <summary>Options of <see cref="StorageBench.RunAsync"/>; the defaults are upstream's.</summary>
public sealed record StorageBenchOptions
{
    /// <summary>The dataset of the read scenarios (upstream <c>TIMING_SCALE</c>).</summary>
    public StorageBenchmarkScale Scale { get; init; } = StorageBenchmark.TimingScale;
    /// <summary>Upstream <c>READ_OPTIONS</c>.</summary>
    public BenchOptions Read { get; init; } = new(300, 10, 75, 3);
    /// <summary>Upstream <c>WRITE_OPTIONS</c>; <c>TimeMs</c> and <c>WarmupTimeMs</c> must stay 0, as each sample takes a fixture from the pool.</summary>
    public BenchOptions Write { get; init; } = new(0, 20, 0, 5);
    /// <summary>Upstream <c>REOPEN_OPTIONS</c>; the times must stay 0, as each sample takes a copy from the pool.</summary>
    public BenchOptions Reopen { get; init; } = new(0, 20, 0, 3);
    /// <summary>Where the file backends' temporary directories go; default the system temporary directory.</summary>
    public string? TempRoot { get; init; }
}

/// <summary>The timed storage benchmarks (upstream <c>test/storage.bench.ts</c>).</summary>
public static class StorageBench
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly BenchBackend[] Backends = [BenchBackend.Memory, BenchBackend.Sqlite, BenchBackend.Store, BenchBackend.Jsonl];

    /// <summary>The backend's name as upstream prints it.</summary>
    public static string Name(BenchBackend backend) => backend.ToString().ToLowerInvariant();

    private sealed class Fixtures(string tempRoot) : IAsyncDisposable
    {
        public List<IStorage> Storages { get; } = [];
        public List<string> Directories { get; } = [];

        public string NewDirectory(string prefix)
        {
            var directory = Path.Combine(tempRoot, prefix + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            Directories.Add(directory);
            return directory;
        }

        public async Task<IStorage> CreateAsync(BenchBackend backend)
        {
            IStorage storage = backend switch
            {
                BenchBackend.Memory => new MemoryStorage(),
                BenchBackend.Sqlite => await SqliteStorage.OpenAsync(Path.Combine(NewDirectory("pi-durable-benchmark-"), "storage.sqlite")),
                BenchBackend.Store => await OpenStoreStorageAsync(Path.Combine(NewDirectory("pi-durable-benchmark-"), "storage.sqlite")),
                _ => await JsonlStorage.OpenAsync(Path.Combine(NewDirectory("pi-durable-benchmark-"), "storage"), Context),
            };
            Storages.Add(storage);
            return storage;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var storage in Storages) await storage.CloseAsync(Context);
            foreach (var directory in Directories)
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs every benchmark, printing each group as it finishes, and returns the results.</summary>
    public static async Task<IReadOnlyList<BenchResult>> RunAsync(StorageBenchOptions options, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        var results = new List<BenchResult>();
        await using var fixtures = new Fixtures(options.TempRoot ?? Path.GetTempPath());

        async Task Report(BenchResult result) { results.Add(result); await output.WriteLineAsync(result.Row()); }
        await output.WriteLineAsync(BenchResult.Header);

        // Reads: one seeded storage per backend, each scenario checked before it is timed.
        var readFixtures = new List<(BenchBackend Backend, IStorage Storage, StorageBenchmarkDataset Dataset)>();
        foreach (var backend in Backends)
        {
            var storage = await fixtures.CreateAsync(backend);
            readFixtures.Add((backend, storage, await StorageBenchmark.SeedAsync(storage, options.Scale)));
        }
        foreach (var (_, storage, dataset) in readFixtures)
            foreach (var scenario in StorageBenchmark.ReadBenchmarks)
                Check(await scenario.Run(storage, dataset), scenario.Expected(dataset), scenario.Name);
        foreach (var scenario in StorageBenchmark.ReadBenchmarks)
            foreach (var (backend, storage, dataset) in readFixtures)
                await Report(await BenchLoop.RunAsync(scenario.Name, Name(backend), () => scenario.Run(storage, dataset), options.Read));

        // Writes: a validated fixture, then one fresh seeded fixture per warmup and sample.
        foreach (var scenario in StorageBenchmark.WriteBenchmarks)
        {
            var pools = new Dictionary<BenchBackend, Queue<IStorage>>();
            foreach (var backend in Backends)
            {
                var validation = await fixtures.CreateAsync(backend);
                await StorageBenchmark.SeedWriteAsync(validation);
                Check(await scenario.Run(validation), scenario.Expected, scenario.Name);
                var pool = new Queue<IStorage>();
                for (var index = 0; index < options.Write.Iterations + options.Write.WarmupIterations; index++)
                {
                    var storage = await fixtures.CreateAsync(backend);
                    await StorageBenchmark.SeedWriteAsync(storage);
                    pool.Enqueue(storage);
                }
                pools[backend] = pool;
            }
            foreach (var backend in Backends)
                await Report(await BenchLoop.RunAsync(scenario.Name, Name(backend), async () =>
                {
                    if (!pools[backend].TryDequeue(out var storage)) throw new InvalidOperationException("Write benchmark fixture pool was exhausted");
                    await scenario.Run(storage);
                }, options.Write));
        }

        // Reopen: seed and close one storage per persistent backend, then copy it once per warmup and sample.
        var reopenFixtures = new List<(BenchBackend Backend, string Path, EntryId FirstEntryId, Queue<string> Samples)>();
        foreach (var backend in Backends.Where(backend => backend != BenchBackend.Memory))
        {
            var seedPath = Path.Combine(fixtures.NewDirectory($"pi-durable-{Name(backend)}-reopen-benchmark-"), Leaf(backend));
            var seed = await OpenPersistentAsync(backend, seedPath);
            var dataset = await StorageBenchmark.SeedAsync(seed, options.Scale);
            await seed.CloseAsync(Context);
            var samples = new Queue<string>();
            for (var index = 0; index < options.Reopen.Iterations + options.Reopen.WarmupIterations; index++)
            {
                var path = Path.Combine(fixtures.NewDirectory($"pi-durable-{Name(backend)}-reopen-sample-"), Leaf(backend));
                CopyPersistent(backend, seedPath, path);
                samples.Enqueue(path);
            }
            reopenFixtures.Add((backend, seedPath, dataset.FirstEntryId, samples));
        }
        foreach (var (backend, path, firstEntryId, _) in reopenFixtures)
        {
            var (id, storage) = await ReopenAndReadAsync(backend, path, firstEntryId);
            Check(id, firstEntryId.Value, "reopen and first exact read");
            await storage.CloseAsync(Context);
        }
        foreach (var (backend, _, firstEntryId, samples) in reopenFixtures)
            await Report(await BenchLoop.RunAsync("reopen and first exact read", Name(backend), async () =>
            {
                if (!samples.TryDequeue(out var path)) throw new InvalidOperationException("Reopen benchmark fixture pool was exhausted");
                fixtures.Storages.Add((await ReopenAndReadAsync(backend, path, firstEntryId)).Storage);
            }, options.Reopen));
        return results;
    }

    private static string Leaf(BenchBackend backend) => backend == BenchBackend.Jsonl ? "storage" : "storage.sqlite";

    private static async Task<IStorage> OpenPersistentAsync(BenchBackend backend, string path) => backend switch
    {
        BenchBackend.Sqlite => await SqliteStorage.OpenAsync(path),
        BenchBackend.Store => await OpenStoreStorageAsync(path),
        _ => await JsonlStorage.OpenAsync(path, Context),
    };

    /// <summary>A StoreStorage on a store over the SQLite file at <paramref name="path"/> whose close closes the store too (upstream
    /// <c>openStoreStorage</c>, owner <c>durable.test</c>).</summary>
    internal static async Task<IStorage> OpenStoreStorageAsync(string path)
    {
        var store = await ChordTables.OpenStoreAsync(await StoreBackends.OpenSqliteAsync(path), ChordContext.Background);
        try
        {
            return new StoreOwningStorage(await StoreStorage.OpenAsync(store, "durable.test", Context), store);
        }
        catch
        {
            await store.CloseAsync(ChordContext.Background);
            throw;
        }
    }

    private static void CopyPersistent(BenchBackend backend, string source, string destination)
    {
        if (backend != BenchBackend.Jsonl) { File.Copy(source, destination); return; }
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task<(long Id, IStorage Storage)> ReopenAndReadAsync(BenchBackend backend, string path, EntryId firstEntryId)
    {
        var storage = await OpenPersistentAsync(backend, path);
        return ((await storage.GetEntryAsync(firstEntryId, Context))?.Entry.Id.Value ?? -1, storage);
    }

    private static void Check(long actual, long expected, string scenario)
    {
        if (actual != expected) throw new InvalidOperationException($"Invalid benchmark result: {scenario}: expected {expected}, actual {actual}");
    }

    /// <summary>The conversation counts of <see cref="ThroughputAsync"/> by default.</summary>
    public static IReadOnlyList<int> DefaultThroughputConversations { get; } = [1, 8, 32, 128];

    /// <summary>The round trips, in milliseconds, of <see cref="ThroughputAsync"/> by default.</summary>
    public static IReadOnlyList<double> DefaultThroughputLatencies { get; } = [0, 1, 4];

    /// <summary>One throughput row: <c>Work</c> is the commits or runs timed, <c>Commits</c> the commits the storage calls carried.</summary>
    public sealed record ThroughputRow(string Scenario, string Backend, double LatencyMs, int Conversations, int Work, double Seconds,
        int StorageCalls, int Commits, double MedianMs);

    /// <summary>Comma-separated numbers, or the defaults.</summary>
    public static IReadOnlyList<T> ParseList<T>(string? argument, IReadOnlyList<T> defaults) where T : IParsable<T> =>
        argument is null ? defaults : [.. argument.Split(',').Select(item => T.Parse(item, CultureInfo.InvariantCulture))];

    /// <summary>
    /// Session commit and Harness run throughput with each count of <paramref name="conversations"/> working at once, over memory storage
    /// behind each round trip of <paramref name="latencies"/> and over local SQLite (see the file header). Writes one table per scenario.
    /// Harness rows take only the round trips of 0 and at least 4 ms.
    /// </summary>
    public static async Task<IReadOnlyList<ThroughputRow>> ThroughputAsync(IReadOnlyList<double> latencies, IReadOnlyList<int> conversations,
        TextWriter output, string? tempRoot = null)
    {
        var rows = new List<ThroughputRow>();
        await using var fixtures = new Fixtures(tempRoot ?? Path.GetTempPath());
        output.WriteLine("scenario        | backend | round trip ms | conversations | commits | s     | commits/s | storage calls | commits per call");
        foreach (var changes in new[] { false, true })
            foreach (var (backend, latency) in Throughputs(latencies))
                foreach (var count in conversations)
                {
                    var row = await SessionCommitsAsync(fixtures, backend, latency, count, changes);
                    rows.Add(row);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{row.Scenario,-15} | {row.Backend,-7} | {row.LatencyMs,-13} | {row.Conversations,-13} | {row.Work,-7} | {row.Seconds,-5:F2} | {row.Work / row.Seconds,-9:F0} | {row.StorageCalls,-13} | {(double)row.Commits / Math.Max(1, row.StorageCalls):F2}"));
                }
        output.WriteLine("scenario     | backend | round trip ms | conversations | runs | s     | runs/s | median run ms | commits per run | storage calls | commits per call");
        foreach (var (backend, latency) in Throughputs([.. latencies.Where(latency => latency == 0 || latency >= 4)]))
            foreach (var count in conversations)
            {
                var row = await HarnessRunsAsync(fixtures, backend, latency, count);
                rows.Add(row);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"harness runs | {row.Backend,-7} | {row.LatencyMs,-13} | {row.Conversations,-13} | {row.Work,-4} | {row.Seconds,-5:F2} | {row.Work / row.Seconds,-6:F1} | {row.MedianMs,-13:F1} | {(double)row.Commits / row.Work,-15:F1} | {row.StorageCalls,-13} | {(double)row.Commits / Math.Max(1, row.StorageCalls):F2}"));
            }
        return rows;
    }

    private static IEnumerable<(BenchBackend Backend, double Latency)> Throughputs(IReadOnlyList<double> latencies)
    {
        foreach (var latency in latencies) yield return (BenchBackend.Memory, latency);
        yield return (BenchBackend.Sqlite, 0);
    }

    /// <summary>The commits of one Session row: 512 in all, split evenly across the conversations, each conversation's in sequence.</summary>
    private const int SessionCommits = 512;

    /// <summary>The runs of one Harness row: 128 in all, split evenly across the conversations, each conversation's in sequence.</summary>
    private const int HarnessRuns = 128;

    /// <summary>The conversation document a session changes row changes (see the file header).</summary>
    private static readonly ConversationDocToken Counter = Documents.DefineConversationDoc("bench.throughput.counter", 1, DocumentFork.Initial,
        () => (JsonObjectValue)JsonValue.Parse("""{"n":0}"""), checkpointWhen: (_, _, _) => false);

    private static void Count(DraftObject counter) => counter.Set("n", ((JsonNumberValue)counter.Get("n")!).Value + 1);

    private static async Task<ThroughputRow> SessionCommitsAsync(Fixtures fixtures, BenchBackend backend, double latency, int count, bool changes)
    {
        var storage = new LatencyStorage(await fixtures.CreateAsync(backend), latency);
        var session = Session.Create(storage);
        try
        {
            var ids = await session.CommitAsync(async tx =>
            {
                var created = new List<ConversationId>();
                for (var index = 0; index < count; index++) created.Add((await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id);
                return created;
            }, Context);
            var each = Math.Max(1, SessionCommits / count);
            // Each conversation's document is created and cached before the clock starts.
            if (changes)
                foreach (var id in ids) await session.CommitAsync(async tx => Count(await tx.DocAsync(Counter, id)), Context);
            storage.Reset();
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(ids.Select(async id =>
            {
                for (var index = 0; index < each; index++)
                {
                    if (changes) await session.CommitAsync(async tx => Count(await tx.DocAsync(Counter, id)), Context);
                    else await session.CommitAsync(tx => tx.AppendEntryAsync(id, new EntryDraft { Kind = "note" }), Context);
                }
            }));
            watch.Stop();
            var (calls, commits) = storage.Reset();
            return new ThroughputRow(changes ? "session changes" : "session commits", Name(backend), latency, count, each * count,
                watch.Elapsed.TotalSeconds, calls, commits, 0);
        }
        finally
        {
            await session.CloseAsync(Context);
        }
    }

    private static async Task<ThroughputRow> HarnessRunsAsync(Fixtures fixtures, BenchBackend backend, double latency, int count)
    {
        var each = Math.Max(1, HarnessRuns / count);
        var storage = new LatencyStorage(await fixtures.CreateAsync(backend), latency);
        var models = new FauxModels([.. Enumerable.Range(0, each * count).Select(_ => FauxModels.Text("done"))]);
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = models, Registry = Registry.Create() }, Context);
        try
        {
            var created = new List<IConversation>();
            for (var index = 0; index < count; index++)
                created.Add(await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
                {
                    Agent = new AgentChange { Model = FauxModels.Ref },
                }, Context));
            storage.Reset();
            var runs = new List<double>();
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(created.Select(async conversation =>
            {
                for (var index = 0; index < each; index++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var settled = await (await conversation.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                    if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"run {index}: {settled}");
                    lock (runs) runs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }));
            watch.Stop();
            var (calls, commits) = storage.Reset();
            runs.Sort();
            return new ThroughputRow("harness runs", Name(backend), latency, count, each * count, watch.Elapsed.TotalSeconds, calls, commits,
                runs[runs.Count / 2]);
        }
        finally
        {
            await harness.CloseAsync(Context);
        }
    }
}

/// <summary>A StoreStorage whose close also closes its store, once (the store backend's storage).</summary>
internal sealed class StoreOwningStorage(StoreStorage storage, IStore store) : IStorage
{
    private readonly Lock gate = new();
    private Task? closing;

    public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        storage.CommitAsync(batches, receive, context);
    public void Publishing(Action publish, DurableContext context) => storage.Publishing(publish, context);
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => storage.MintIdAsync<TId>();
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => storage.GetConversationAsync(id, context);
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanConversationsAsync(query, limit, cursor, context);
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => storage.GetEntryAsync(id, context);
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        storage.GetEntryAsync(conversationId, id, context);
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        storage.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanEntriesAsync(query, limit, cursor, context);
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => storage.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => storage.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        storage.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        storage.FindDocumentAsync(address, at, context);
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => storage.GetDocumentAsync(id, at, context);
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanDocumentsAsync(query, limit, cursor, context);

    public Task CloseAsync(DurableContext context)
    {
        lock (gate) return closing ??= CloseBothAsync(context);
    }

    private async Task CloseBothAsync(DurableContext context)
    {
        await storage.CloseAsync(context);
        await store.CloseAsync(ChordContext.Background);
    }
}

/// <summary>A storage whose every call first waits a fixed round trip (the throughput benchmark's; see the file header).</summary>
internal sealed class LatencyStorage(IStorage inner, double latencyMs) : IStorage
{
    private int calls;
    private int commits;

    /// <summary>The commit calls and the commits they carried since the last reset; resets both.</summary>
    public (int Calls, int Commits) Reset() => (Interlocked.Exchange(ref calls, 0), Interlocked.Exchange(ref commits, 0));

    private async Task<T> Trip<T>(Func<Task<T>> call)
    {
        await PreciseDelay.Wait(latencyMs);
        return await call();
    }

    public async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context)
    {
        Interlocked.Increment(ref calls);
        Interlocked.Add(ref commits, batches.Count);
        await PreciseDelay.Wait(latencyMs);
        return await inner.CommitAsync(batches, receive, context);
    }

    public void Publishing(Action publish, DurableContext context) => inner.Publishing(publish, context);
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => Trip(() => inner.GetConversationAsync(id, context));
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Trip(() => inner.ScanConversationsAsync(query, limit, cursor, context));
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => Trip(() => inner.GetEntryAsync(id, context));
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        Trip(() => inner.GetEntryAsync(conversationId, id, context));
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        Trip(() => inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context));
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Trip(() => inner.ScanEntriesAsync(query, limit, cursor, context));
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Trip(() => inner.GetTaskAsync(id, context));
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Trip(() => inner.ScanTasksAsync(query, limit, cursor, context));
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => Trip(() => inner.GetSubmissionAsync(id, context));
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Trip(() => inner.ScanSubmissionsAsync(query, limit, cursor, context));
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        Trip(() => inner.GetSubmissionByRequestAsync(conversationId, requestId, context));
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        Trip(() => inner.FindDocumentAsync(address, at, context));
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        Trip(() => inner.GetDocumentAsync(id, at, context));
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Trip(() => inner.ScanDocumentsAsync(query, limit, cursor, context));
    public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
}

/// <summary>
/// Waits a given number of milliseconds to within a few microseconds: one background thread spins over a deadline queue while waits are
/// pending (it costs one core while the throughput benchmark runs) and sleeps on the queue's monitor otherwise.
/// </summary>
internal static class PreciseDelay
{
    private static readonly object Gate = new();
    private static readonly PriorityQueue<TaskCompletionSource, long> Waits = new();
    private static Thread? worker;

    /// <summary>Completes after about <paramref name="ms"/> milliseconds; at once for 0 or less.</summary>
    public static Task Wait(double ms)
    {
        if (ms <= 0) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var due = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        lock (Gate)
        {
            Waits.Enqueue(done, due);
            if (worker is null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "bench-latency" };
                worker.Start();
            }
            Monitor.Pulse(Gate);
        }
        return done.Task;
    }

    private static void Run()
    {
        while (true)
        {
            TaskCompletionSource? due = null;
            lock (Gate)
            {
                while (Waits.Count == 0) Monitor.Wait(Gate);
                if (Waits.TryPeek(out var next, out var at) && Stopwatch.GetTimestamp() >= at) due = Waits.Dequeue();
            }
            if (due is not null) due.SetResult();
            else Thread.SpinWait(20);
        }
    }
}
