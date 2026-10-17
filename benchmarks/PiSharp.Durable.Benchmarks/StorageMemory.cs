// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/storage-memory.ts
// The storage footprint after the deterministic benchmark seed, per backend and scale, each measured in its own child process so one
// measurement cannot inherit another's heap. Node's process.memoryUsage() becomes the .NET counterparts: heapUsed is the GC heap after three
// forced full collections (GC.GetTotalMemory(true)), rss the working set. Node's "external" (ArrayBuffers outside the V8 heap) has no .NET
// counterpart and is left out. The SQLite disk detail reads page_count and freelist_count as upstream does, over a read-only connection.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Testing;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Process memory at one point (upstream <c>MemorySnapshot</c>).</summary>
public sealed record MemorySnapshot(long HeapUsed, long Rss);

/// <summary>Storage on disk after the seed and reads (upstream <c>DiskFootprint</c>); the SQLite fields or the JSONL fields are set.</summary>
public sealed record DiskFootprint(string Kind, long MainBytes, long AuxiliaryBytes, int FileCount, long PageCount = 0, long FreelistCount = 0,
    int DocumentFiles = 0, int TaskFiles = 0);

/// <summary>One worker's measurement (upstream <c>StorageMemoryResult</c>).</summary>
public sealed record StorageMemoryResult(string Backend, string Scale, int RecordCount, MemorySnapshot Baseline, MemorySnapshot PostSeed,
    MemorySnapshot PostRead, DiskFootprint? Disk);

/// <summary>The storage footprint benchmark (upstream <c>test/storage-memory.ts</c>).</summary>
public static class StorageMemory
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly BenchBackend[] Backends = [BenchBackend.Memory, BenchBackend.Sqlite, BenchBackend.Jsonl];

    private static void CollectGarbage()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static MemorySnapshot Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new(GC.GetTotalMemory(forceFullCollection: false), process.WorkingSet64);
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static DiskFootprint SqliteMetrics(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        long Pragma(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
        return new("sqlite", FileSize(path), FileSize(path + "-wal") + FileSize(path + "-shm"), 3,
            PageCount: Pragma("SELECT page_count AS value FROM pragma_page_count()"),
            FreelistCount: Pragma("SELECT freelist_count AS value FROM pragma_freelist_count()"));
    }

    private static DiskFootprint JsonlMetrics(string directory)
    {
        long mainBytes = 0, auxiliaryBytes = 0;
        int fileCount = 0, documentFiles = 0, taskFiles = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            fileCount++;
            if (file.Name == "main.jsonl") mainBytes += file.Length; else auxiliaryBytes += file.Length;
            if (file.Name.StartsWith("doc-", StringComparison.Ordinal) && file.Name.EndsWith(".jsonl", StringComparison.Ordinal)) documentFiles++;
            if (file.Name.StartsWith("task-", StringComparison.Ordinal) && file.Name.EndsWith(".jsonl", StringComparison.Ordinal)) taskFiles++;
        }
        return new("jsonl", mainBytes, auxiliaryBytes, fileCount, DocumentFiles: documentFiles, TaskFiles: taskFiles);
    }

    /// <summary>Measures one backend at one scale in this process (upstream <c>runWorker</c>) and returns the result.</summary>
    public static async Task<StorageMemoryResult> RunWorkerAsync(BenchBackend backend, StorageBenchmarkScale scale, string? tempRoot = null)
    {
        ArgumentNullException.ThrowIfNull(scale);
        string? directory = null, path = null;
        IStorage storage;
        if (backend == BenchBackend.Memory) storage = new MemoryStorage();
        else
        {
            directory = Directory.CreateDirectory(Path.Combine(tempRoot ?? Path.GetTempPath(),
                $"pi-durable-{StorageBench.Name(backend)}-memory-{Guid.NewGuid().ToString("N")[..8]}")).FullName;
            path = Path.Combine(directory, backend == BenchBackend.Sqlite ? "storage.sqlite" : "storage");
            storage = backend == BenchBackend.Sqlite ? await SqliteStorage.OpenAsync(path) : await JsonlStorage.OpenAsync(path, Context);
        }
        try
        {
            CollectGarbage();
            var baseline = Snapshot();
            var dataset = await StorageBenchmark.SeedAsync(storage, scale);
            CollectGarbage();
            var postSeed = Snapshot();
            var checksum = 0L;
            foreach (var scenario in StorageBenchmark.ReadBenchmarks)
            {
                var result = await scenario.Run(storage, dataset);
                if (result != scenario.Expected(dataset)) throw new InvalidOperationException($"Invalid benchmark result: {scenario.Name}");
                checksum += result;
            }
            for (var round = 1; round < 10; round++)
                foreach (var scenario in StorageBenchmark.ReadBenchmarks) checksum += await scenario.Run(storage, dataset);
            if (checksum < 0) throw new InvalidOperationException("Storage memory read checksum is invalid");
            CollectGarbage();
            var postRead = Snapshot();
            var disk = path is null ? null : backend == BenchBackend.Sqlite ? SqliteMetrics(path) : JsonlMetrics(path);
            return new(StorageBench.Name(backend), scale.Name, StorageBenchmark.PrimaryRecordCount(scale), baseline, postSeed, postRead, disk);
        }
        finally
        {
            await storage.CloseAsync(Context);
            if (directory is not null) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The worker entry point: prints one result as a JSON line.</summary>
    public static async Task<int> WorkerMainAsync(string backendName, string scaleName, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var backend = Backends.FirstOrDefault(candidate => StorageBench.Name(candidate) == backendName, (BenchBackend)(-1));
        if ((int)backend < 0) throw new ArgumentException($"Unknown storage benchmark backend: {backendName}");
        var scale = StorageBenchmark.MemoryScales.FirstOrDefault(candidate => candidate.Name == scaleName)
            ?? throw new ArgumentException($"Unknown storage benchmark scale: {scaleName}");
        await output.WriteLineAsync(JsonSerializer.Serialize(await RunWorkerAsync(backend, scale), Json));
        return 0;
    }

    /// <summary>Runs a worker process per backend and scale (upstream <c>runDriver</c>) and prints the table.</summary>
    public static async Task<IReadOnlyList<StorageMemoryResult>> RunDriverAsync(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var results = new List<StorageMemoryResult>();
        foreach (var backend in Backends)
            foreach (var scale in StorageBenchmark.MemoryScales)
            {
                var stdout = await ChildProcess.RunAsync(["storage-memory", "--worker", StorageBench.Name(backend), scale.Name]);
                results.Add(JsonSerializer.Deserialize<StorageMemoryResult>(stdout.Trim().Split('\n')[^1], Json)!);
            }
        await output.WriteLineAsync("Storage footprint after deterministic synthetic workloads; values are process deltas, not limits.");
        await output.WriteLineAsync(Table(results));
        return results;
    }

    /// <summary>Upstream's console.table columns as a Markdown table.</summary>
    public static string Table(IEnumerable<StorageMemoryResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        static string MiB(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "| backend | scale | heap after seed MiB | RSS after seed MiB | heap after reads MiB | heap bytes/primary record | disk main MiB | disk auxiliary MiB | disk total MiB | disk files | disk detail |",
            "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|",
        };
        foreach (var result in results)
        {
            var disk = result.Disk;
            lines.Add(string.Join(" | ", [
                "| " + result.Backend, result.Scale,
                MiB(result.PostSeed.HeapUsed - result.Baseline.HeapUsed), MiB(result.PostSeed.Rss - result.Baseline.Rss),
                MiB(result.PostRead.HeapUsed - result.PostSeed.HeapUsed),
                Math.Round((double)(result.PostSeed.HeapUsed - result.Baseline.HeapUsed) / result.RecordCount).ToString(CultureInfo.InvariantCulture),
                disk is null ? "-" : MiB(disk.MainBytes), disk is null ? "-" : MiB(disk.AuxiliaryBytes),
                disk is null ? "-" : MiB(disk.MainBytes + disk.AuxiliaryBytes), disk is null ? "-" : disk.FileCount.ToString(CultureInfo.InvariantCulture),
                disk is null ? "-" : disk.Kind == "sqlite" ? $"pages/free {disk.PageCount}/{disk.FreelistCount}" : $"documents/tasks {disk.DocumentFiles}/{disk.TaskFiles}",
            ]) + " |");
        }
        return string.Join('\n', lines);
    }
}

/// <summary>Relaunches this benchmark executable as a child, through the same host (apphost, or dotnet plus the DLL).</summary>
internal static class ChildProcess
{
    public static async Task<string> RunAsync(IEnumerable<string> arguments)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ChildProcess).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + host);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Benchmark worker failed ({process.ExitCode}): {await stderr}");
        return await stdout;
    }
}
