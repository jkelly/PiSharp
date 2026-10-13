// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/testing/storage-benchmark.ts
// The deterministic storage benchmark workloads: a seeded dataset written only through the public IStorage contract, the read scenarios
// timed against it, and the write scenarios timed against a small baseline. The scenarios return a number the caller checks against
// Expected before timing, so a backend that answers wrongly cannot report a fast time. benchmarks/PiSharp.Durable.Benchmarks times them
// (upstream test/storage.bench.ts and test/storage-memory.ts); the kit itself has no timing.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable.Testing;

/// <summary>The size of a seeded benchmark dataset (upstream <c>StorageBenchmarkScale</c>).</summary>
/// <param name="Name">The scale's label.</param>
/// <param name="EntryCount">Root conversation entries.</param>
/// <param name="TaskCount">Task records.</param>
/// <param name="DocumentCount">Session documents of one keyed family.</param>
public sealed record StorageBenchmarkScale(string Name, int EntryCount, int TaskCount, int DocumentCount);

/// <summary>What the read scenarios look up in a seeded dataset (upstream <c>StorageBenchmarkDataset</c>).</summary>
public sealed record StorageBenchmarkDataset
{
    /// <summary>The first root entry; it carries a head marker.</summary>
    public required EntryId FirstEntryId { get; init; }
    /// <summary>How many tasks the filtered task scan returns.</summary>
    public required int FilteredTaskCount { get; init; }
    /// <summary>The last document of the keyed family.</summary>
    public required DocumentId ExactDocumentId { get; init; }
    /// <summary>The key of <see cref="ExactDocumentId"/>.</summary>
    public required string ExactDocumentKey { get; init; }
    /// <summary>One rewindable document per replay tail length (0, 16, 128 and 1024 deltas).</summary>
    public required ImmutableDictionary<int, DocumentId> ReplayDocumentIds { get; init; }
    /// <summary>The rewindable document with a base between two delta segments.</summary>
    public required DocumentId HistoricalDocumentId { get; init; }
    /// <summary>The last commit of the first delta segment, before the newer base.</summary>
    public required Seq AncientAt { get; init; }
    /// <summary>The last commit of the second delta segment, after the newer base.</summary>
    public required Seq RecentAt { get; init; }
    /// <summary>The deepest of the nested forks.</summary>
    public required ConversationId DeepestConversationId { get; init; }
    /// <summary>The head marker every fork inherits.</summary>
    public required EntryId AncestorHeadEntryId { get; init; }
}

/// <summary>One read scenario (upstream <c>StorageReadBenchmark</c>).</summary>
/// <param name="Name">Upstream's scenario name.</param>
/// <param name="Run">Runs the read once and returns its checked number.</param>
/// <param name="Expected">What <paramref name="Run"/> must return for the dataset.</param>
public sealed record StorageReadBenchmark(string Name, Func<IStorage, StorageBenchmarkDataset, Task<long>> Run, Func<StorageBenchmarkDataset, long> Expected);

/// <summary>One write scenario (upstream <c>StorageWriteBenchmark</c>); each sample needs a storage seeded by
/// <see cref="StorageBenchmark.SeedWriteAsync"/>.</summary>
/// <param name="Name">Upstream's scenario name.</param>
/// <param name="Expected">The number of writes <paramref name="Run"/> commits.</param>
/// <param name="Run">Commits once and returns the number of writes.</param>
public sealed record StorageWriteBenchmark(string Name, long Expected, Func<IStorage, Task<long>> Run);

/// <summary>The storage benchmark kit (upstream <c>@earendil-works/pi-durable/testing</c> storage-benchmark exports).</summary>
public static class StorageBenchmark
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly int[] ReplayTails = [0, 16, 128, 1_024];
    private const int HistorySegmentLength = 128;
    private const int ForkDepth = 8;
    private const int EntriesPerFork = 32;
    private const int BatchSize = 100;

    /// <summary>The scales the footprint benchmark seeds (upstream <c>STORAGE_MEMORY_SCALES</c>).</summary>
    public static ImmutableArray<StorageBenchmarkScale> MemoryScales { get; } =
    [
        new("1k", 1_000, 200, 200),
        new("10k", 10_000, 2_000, 2_000),
    ];

    /// <summary>The scale the timed read benchmarks seed (upstream <c>TIMING_SCALE</c>).</summary>
    public static StorageBenchmarkScale TimingScale { get; } = new("timing", 1_000, 300, 300);

    /// <summary>The number of primary records <see cref="SeedAsync"/> writes at <paramref name="scale"/> (upstream
    /// <c>storageBenchmarkPrimaryRecordCount</c>).</summary>
    public static int PrimaryRecordCount(StorageBenchmarkScale scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        return 1 + scale.EntryCount + scale.TaskCount + scale.DocumentCount + ReplayTails.Length + 1 + ForkDepth * (1 + EntriesPerFork);
    }

    private static TaskRecord Task(TaskId id, long index)
    {
        var status = (index % 3) switch { 0 => "pending", 1 => "running", _ => "terminal" };
        TaskState state = status switch
        {
            "terminal" => new TerminalTaskState(new CompletedTaskOutcome(Obj(("index", index)))),
            "pending" => new PendingTaskState(Obj(("index", index), ("payload", new string('x', 64)))),
            _ => new RunningTaskState(Obj(("index", index), ("payload", new string('x', 64)))),
        };
        return new()
        {
            Id = id, ConversationId = ConversationId.Root, Kind = index % 4 == 0 ? "benchmark.filtered" : "benchmark.other", Version = 1,
            Input = Obj(("index", index)), Background = index % 5 == 0, AbortRequested = index % 7 == 0, State = state,
        };
    }

    /// <summary>Seeds deterministic representative data through only the public <see cref="IStorage"/> contract (upstream
    /// <c>seedStorageBenchmark</c>).</summary>
    public static async Task<StorageBenchmarkDataset> SeedAsync(IStorage storage, StorageBenchmarkScale? scale = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        scale ??= TimingScale;
        await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root))], Context);

        EntryId? firstEntryId = null;
        for (var start = 0; start < scale.EntryCount; start += BatchSize)
        {
            var writes = new List<StorageWrite>();
            for (var index = start; index < Math.Min(start + BatchSize, scale.EntryCount); index++)
            {
                var id = await storage.MintIdAsync<EntryId>();
                if (index == 0) firstEntryId = id;
                writes.Add(new EntryWrite(new()
                {
                    Id = id, ConversationId = ConversationId.Root, Kind = "benchmark.entry", Head = index == 0 ? id : null,
                    Data = Obj(("index", index), ("text", $"entry-{index}-{new string('x', 96)}")),
                }));
            }
            await storage.CommitAsync(writes, Context);
        }

        for (var start = 0; start < scale.TaskCount; start += BatchSize)
        {
            var writes = new List<StorageWrite>();
            for (var index = start; index < Math.Min(start + BatchSize, scale.TaskCount); index++)
                writes.Add(new TaskWrite(Task(await storage.MintIdAsync<TaskId>(), index)));
            await storage.CommitAsync(writes, Context);
        }

        DocumentId? exactDocumentId = null;
        for (var start = 0; start < scale.DocumentCount; start += BatchSize)
        {
            var writes = new List<StorageWrite>();
            for (var index = start; index < Math.Min(start + BatchSize, scale.DocumentCount); index++)
            {
                var id = await storage.MintIdAsync<DocumentId>();
                exactDocumentId = id;
                writes.Add(new DocumentCreateWrite(
                    new DocumentCreate { Id = id, Kind = "benchmark.family", Key = $"key-{index}", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, Obj(("index", index), ("text", new string('x', 128))))));
            }
            await storage.CommitAsync(writes, Context);
        }

        var replayEntries = new List<(int Tail, DocumentId Id)>();
        foreach (var tail in ReplayTails) replayEntries.Add((tail, await storage.MintIdAsync<DocumentId>()));
        await storage.CommitAsync([.. replayEntries.Select(StorageWrite (replay) => new DocumentCreateWrite(
            new DocumentCreate
            {
                Id = replay.Id, Kind = "benchmark.replay", Key = replay.Tail.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            },
            DocumentContent.Base(1, Obj(("count", 0), ("text", new string('x', 64))))))], Context);
        for (var count = 1; count <= ReplayTails[^1]; count++)
            await storage.CommitAsync([.. replayEntries.Where(replay => count <= replay.Tail)
                .Select(StorageWrite (replay) => new DocumentChangeWrite(replay.Id, SetCount(count)))], Context);

        var historicalDocumentId = await storage.MintIdAsync<DocumentId>();
        var historicalRecord = new DocumentCreate
        {
            Id = historicalDocumentId, Kind = "benchmark.history", Scope = DocumentScope.Conversation(ConversationId.Root),
            History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
        };
        await storage.CommitAsync([new DocumentCreateWrite(historicalRecord, DocumentContent.Base(1, Obj(("count", 0))))], Context);
        Seq? ancientAt = null;
        for (var count = 1; count <= HistorySegmentLength; count++)
            ancientAt = await storage.CommitAsync([new DocumentChangeWrite(historicalDocumentId, SetCount(count))], Context);
        await storage.CommitAsync([new DocumentChangeWrite(historicalDocumentId, DocumentContent.Base(1, Obj(("count", HistorySegmentLength))))], Context);
        if (ancientAt is not { } ancient) throw new InvalidOperationException("Benchmark history seed produced no commits");
        var recentAt = ancient;
        for (var count = HistorySegmentLength + 1; count <= HistorySegmentLength * 2; count++)
            recentAt = await storage.CommitAsync([new DocumentChangeWrite(historicalDocumentId, SetCount(count))], Context);

        if (firstEntryId is not { } first) throw new InvalidOperationException("Benchmark scale must create entries");
        var parentConversationId = ConversationId.Root;
        var parentAt = first;
        var deepestConversationId = ConversationId.Root;
        for (var depth = 0; depth < ForkDepth; depth++)
        {
            var conversationId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync([new ConversationWrite(new(conversationId, new ConversationParent(parentConversationId, parentAt)))], Context);
            var ids = new EntryId[EntriesPerFork];
            for (var index = 0; index < ids.Length; index++) ids[index] = await storage.MintIdAsync<EntryId>();
            await storage.CommitAsync([.. ids.Select(StorageWrite (id, index) => new EntryWrite(new()
            {
                Id = id, ConversationId = conversationId, Kind = "benchmark.fork", Data = Obj(("depth", depth), ("index", index)),
            }))], Context);
            parentConversationId = conversationId;
            parentAt = ids[^1];
            deepestConversationId = conversationId;
        }

        if (exactDocumentId is not { } exact) throw new InvalidOperationException("Benchmark scale must create documents");
        return new()
        {
            FirstEntryId = first,
            FilteredTaskCount = Math.Min(50, (int)Math.Ceiling(scale.TaskCount / 60.0)),
            ExactDocumentId = exact,
            ExactDocumentKey = $"key-{scale.DocumentCount - 1}",
            ReplayDocumentIds = replayEntries.ToImmutableDictionary(replay => replay.Tail, replay => replay.Id),
            HistoricalDocumentId = historicalDocumentId,
            AncientAt = ancient,
            RecentAt = recentAt,
            DeepestConversationId = deepestConversationId,
            AncestorHeadEntryId = first,
        };
    }

    /// <summary>The timed read scenarios (upstream <c>STORAGE_READ_BENCHMARKS</c>).</summary>
    public static ImmutableArray<StorageReadBenchmark> ReadBenchmarks { get; } =
    [
        new("exact entry lookup",
            async (storage, dataset) => (await storage.GetEntryAsync(dataset.FirstEntryId, Context))?.Entry.Id.Value ?? -1,
            dataset => dataset.FirstEntryId.Value),
        new("entry page scan (100)",
            async (storage, _) => (await storage.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 100, null, Context)).Items.Length,
            _ => 100),
        new("filtered task scan (50)",
            async (storage, _) => (await storage.ScanTasksAsync(
                new TaskQuery { Kind = "benchmark.filtered", Status = DurableTaskStatus.Pending, Background = true }, 50, null, Context)).Items.Length,
            dataset => dataset.FilteredTaskCount),
        new("exact document address among many",
            async (storage, dataset) => (await storage.FindDocumentAsync(
                new DocumentAddress("benchmark.family", DocumentScope.Session, dataset.ExactDocumentKey), DocumentPoint.Current, Context))?.Id.Value ?? -1,
            dataset => dataset.ExactDocumentId.Value),
        .. ReplayTails.Select(tail => new StorageReadBenchmark($"document replay tail ({tail})",
            async (storage, dataset) => Count(await storage.GetDocumentAsync(dataset.ReplayDocumentIds[tail], DocumentPoint.Current, Context)),
            _ => tail)),
        new("ancient historical read before newer base",
            async (storage, dataset) => Count(await storage.GetDocumentAsync(dataset.HistoricalDocumentId, dataset.AncientAt, Context)),
            _ => HistorySegmentLength),
        new("recent historical read after newer base",
            async (storage, dataset) => Count(await storage.GetDocumentAsync(dataset.HistoricalDocumentId, dataset.RecentAt, Context)),
            _ => HistorySegmentLength * 2),
        new("fork-depth history scan (100)",
            async (storage, dataset) => (await storage.ScanEntriesAsync(new EntryQuery(dataset.DeepestConversationId), 100, null, Context)).Items.Length,
            _ => 100),
        new("fork-depth head lookup",
            async (storage, dataset) => (await storage.FindLatestHeadMarkerAsync(dataset.DeepestConversationId, null, Context))?.Id.Value ?? -1,
            dataset => dataset.AncestorHeadEntryId.Value),
    ];

    /// <summary>Seeds the common state every write benchmark sample expects (upstream <c>seedStorageWriteBenchmark</c>).</summary>
    public static async Task SeedWriteAsync(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root))], Context);
        var writes = new List<StorageWrite>();
        for (var index = 0; index < 100; index++)
            writes.Add(new EntryWrite(new()
            {
                Id = await storage.MintIdAsync<EntryId>(), ConversationId = ConversationId.Root, Kind = "benchmark.baseline", Data = Obj(("index", index)),
            }));
        await storage.CommitAsync(writes, Context);
    }

    /// <summary>The timed write scenarios (upstream <c>STORAGE_WRITE_BENCHMARKS</c>).</summary>
    public static ImmutableArray<StorageWriteBenchmark> WriteBenchmarks { get; } =
    [
        new("commit one entry", 1, async storage =>
        {
            var id = await storage.MintIdAsync<EntryId>();
            await storage.CommitAsync([new EntryWrite(new()
            {
                Id = id, ConversationId = ConversationId.Root, Kind = "benchmark.write", Data = Obj(("text", new string('x', 128))),
            })], Context);
            return 1;
        }),
        new("commit 100 entries", 100, async storage =>
        {
            var writes = new List<StorageWrite>();
            for (var index = 0; index < 100; index++)
                writes.Add(new EntryWrite(new()
                {
                    Id = await storage.MintIdAsync<EntryId>(), ConversationId = ConversationId.Root, Kind = "benchmark.write",
                    Data = Obj(("index", index), ("text", new string('x', 128))),
                }));
            await storage.CommitAsync(writes, Context);
            return writes.Count;
        }),
        new("commit mixed entry/task/submission/document", 4, async storage =>
        {
            var entryId = await storage.MintIdAsync<EntryId>();
            var taskId = await storage.MintIdAsync<TaskId>();
            var submissionId = await storage.MintIdAsync<SubmissionId>();
            var documentId = await storage.MintIdAsync<DocumentId>();
            StorageWrite[] writes =
            [
                new EntryWrite(new() { Id = entryId, ConversationId = ConversationId.Root, Kind = "benchmark.mixed" }),
                new TaskWrite(Task(taskId, taskId.Value)),
                new SubmissionWrite(new SubmissionRecord
                {
                    Id = submissionId, ConversationId = ConversationId.Root, RequestId = $"benchmark-{submissionId.Value}", Type = SubmissionType.Write,
                    Status = SubmissionStatus.Done, Entry = entryId,
                }),
                new DocumentCreateWrite(
                    new DocumentCreate
                    {
                        Id = documentId, Kind = "benchmark.mixed", Key = documentId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Scope = DocumentScope.Session,
                    },
                    DocumentContent.Base(1, Obj(("entryId", entryId.Value), ("taskId", taskId.Value)))),
            ];
            await storage.CommitAsync(writes, Context);
            return writes.Length;
        }),
    ];

    private static DocumentContent SetCount(int count) =>
        DocumentContent.Delta(1, [new SetOp([PathSegment.Of("count")], JsonValue.Number(count))]);

    // Upstream Number(value.count): a missing document or count is NaN, which never equals the expected count.
    private static long Count(StoredDocument? document) =>
        document?.Value.TryGetValue("count", out var count) == true && count is JsonNumberValue number ? (long)number.Value : -1;

    private static JsonObjectValue Obj(params (string Key, JsonValue Value)[] properties) =>
        JsonObjectValue.From(properties.Select(property => KeyValuePair.Create(property.Key, property.Value)));
}
