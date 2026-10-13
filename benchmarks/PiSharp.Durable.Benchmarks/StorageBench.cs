// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/storage.bench.ts
// The timed storage benchmarks on memory, SQLite and JSONL: every read scenario against one seeded storage per backend, every write
// scenario against a pool of freshly seeded storages (one per sample, so no sample sees an earlier sample's writes), and reopen plus the
// first exact read against copies of one seeded file or directory. Each scenario's result is checked once before it is timed. Upstream's
// Vitest bench options are kept: reads sample for at least 300 ms and 10 runs after a 75 ms / 3 run warmup; writes take exactly 20 samples
// after 5 warmups; reopen 20 after 3.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Testing;

namespace PiSharp.Durable.Benchmarks;

/// <summary>A storage benchmark backend.</summary>
public enum BenchBackend
{
    /// <summary>MemoryStorage.</summary>
    Memory,
    /// <summary>SqliteStorage over one file.</summary>
    Sqlite,
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
    private static readonly BenchBackend[] Backends = [BenchBackend.Memory, BenchBackend.Sqlite, BenchBackend.Jsonl];

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

    private static string Leaf(BenchBackend backend) => backend == BenchBackend.Sqlite ? "storage.sqlite" : "storage";

    private static async Task<IStorage> OpenPersistentAsync(BenchBackend backend, string path) =>
        backend == BenchBackend.Sqlite ? await SqliteStorage.OpenAsync(path) : await JsonlStorage.OpenAsync(path, Context);

    private static void CopyPersistent(BenchBackend backend, string source, string destination)
    {
        if (backend == BenchBackend.Sqlite) { File.Copy(source, destination); return; }
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
}
