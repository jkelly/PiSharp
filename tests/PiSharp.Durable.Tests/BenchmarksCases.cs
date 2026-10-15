using PiSharp.Durable.Benchmarks;
using PiSharp.Durable.Testing;

// P13 (decision 0005): benchmarks/PiSharp.Durable.Benchmarks ports Pi v1.1.0 packages/durable/test/storage.bench.ts, test/storage-memory.ts
// and test/tool-output-bench.ts over src/PiSharp.Durable/Testing/StorageBenchmark.cs (src/testing/storage-benchmark.ts), and Pi v1.2.0
// test/document-append-bench.ts. The benchmarks are report only (docs/durable-benchmarks.md). This smoke case runs each of them at a tiny
// size so they cannot rot: one sample per storage scenario on every backend (each scenario's result is checked before it is timed), the
// footprint worker on every backend in-process, a short rate round closed mid-round and replayed, a firehose, a repetitive firehose, a
// bash cat of 1 MiB, and 20 document appends of each kind. It asserts completion and the number of results, never a timing.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> BenchmarksCases()
    {
        yield return Case("benchmarks.smoke", async () =>
        {
            var root = Temp("benchmarks");
            var tiny = new StorageBenchmarkScale("smoke", 100, 60, 10);
            Equal(1 + 100 + 60 + 10 + 4 + 1 + 8 * 33, StorageBenchmark.PrimaryRecordCount(tiny), "primary records");
            var one = new BenchOptions(0, 1, 0, 0);
            var output = new StringWriter();
            var results = await StorageBench.RunAsync(new StorageBenchOptions { Scale = tiny, Read = one, Write = one, Reopen = one, TempRoot = root }, output);
            // 12 read and 3 write scenarios on three backends, and reopen on the two persistent ones.
            Equal(12 * 3 + 3 * 3 + 2, results.Count, "storage benchmark results");
            Check(results.All(result => result.Samples == 1), "one sample each");

            foreach (var backend in new[] { BenchBackend.Memory, BenchBackend.Sqlite, BenchBackend.Jsonl })
            {
                var footprint = await StorageMemory.RunWorkerAsync(backend, tiny, root);
                Equal(StorageBench.Name(backend), footprint.Backend, "footprint backend");
                Equal(backend != BenchBackend.Memory, footprint.Disk is not null, "footprint disk");
            }
            Check(StorageMemory.Table([]).StartsWith("| backend |", StringComparison.Ordinal), "footprint table");

            var options = new ToolOutputOptions { RateSeconds = 0.2, SettleMs = 50, Bytes = 1 << 20, BigFile = Path.Combine(root, "big.txt") };
            await ToolOutputBench.EnsureBigFileAsync(options.BigFile, options.Bytes);
            Equal(options.Bytes, new FileInfo(options.BigFile).Length, "big file size");
            ToolOutputScenario[] scenarios =
            [
                new() { Kind = "rate", Backend = "memory", Rate = "normal", Retain = "head", Tools = 4 },
                new() { Kind = "rate", Backend = "sqlite", Rate = "high", Retain = "tail", Tools = 1 },
                new() { Kind = "rate", Backend = "jsonl", Rate = "low", Retain = "tail", Tools = 1 },
                new() { Kind = "firehose", Backend = "jsonl", Retain = "head" },
                new() { Kind = "firehose", Backend = "memory", Retain = "tail", Repetitive = true },
                new() { Kind = "cat", Backend = "sqlite" },
            ];
            foreach (var scenario in scenarios)
            {
                var metrics = await ToolOutputBench.RunScenarioAsync(scenario, options);
                Equal(scenario.Kind == "rate" && scenario.Backend != "memory", metrics.ReplayMs is not null, scenario.Label + " replay measured");
                Equal(scenario.Backend != "memory", metrics.FinalKiB is not null, scenario.Label + " final size measured");
                Check(ToolOutputBench.Row(scenario, metrics).StartsWith("| " + scenario.Label + " | " + scenario.Backend + " |", StringComparison.Ordinal),
                    scenario.Label + " row");
            }
            Equal(50, ToolOutputBench.Scenarios().Count, "upstream scenario count");

            var appendOutput = new StringWriter();
            var appends = await DocumentAppendBench.RunAsync([20], appendOutput);
            Check(appends is [{ N: 20 }], "one document-append row");
            Check(appendOutput.ToString().StartsWith("n=20: array ", StringComparison.Ordinal), "document-append line: " + appendOutput);
            Equal(DocumentAppendBench.DefaultSizes, DocumentAppendBench.ParseSizes(null), "default sizes");
            Directory.Delete(root, recursive: true);
        });
    }
}
