using PiSharp.Durable.Benchmarks;
using PiSharp.Durable.Testing;

// P13 (decision 0005): benchmarks/PiSharp.Durable.Benchmarks ports Pi v1.1.0 packages/durable/test/storage.bench.ts, test/storage-memory.ts
// and test/tool-output-bench.ts over src/PiSharp.Durable/Testing/StorageBenchmark.cs (src/testing/storage-benchmark.ts), and Pi v1.2.0
// test/document-append-bench.ts, test/session-document-cache-bench.ts and test/scheduler-memory-bench.ts, plus the PiSharp-authored
// scheduler-scale (SchedulerScaleBench.cs). The benchmarks are report only (docs/durable-benchmarks.md).
// This smoke case runs each of them at a tiny size so they cannot rot: one sample per storage scenario on every backend (each scenario's
// result is checked before it is timed), the footprint worker on every backend in-process, a short rate round closed mid-round and
// replayed, a firehose, a repetitive firehose, a bash cat of 1 MiB, 20 document appends of each kind, three conversations at each
// document retention, three subagent cycles under a live parent, a fan-out of four child tasks and a chain of five. It asserts completion,
// the number of results and the scheduler's bounded indexes, never a timing.
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

            var cacheOutput = new StringWriter();
            var cacheRows = await SessionDocumentCacheBench.RunAsync([3], cacheOutput, warmup: 2, tempRoot: root);
            Equal(SessionDocumentCacheBench.Retentions.Count, cacheRows.Count, "document-cache rows");
            Check(cacheRows.All(row => row.Conversations == 3), "document-cache sizes");
            Check(cacheOutput.ToString().StartsWith("conversations | retention", StringComparison.Ordinal), "document-cache table: " + cacheOutput);
            Equal(SessionDocumentCacheBench.DefaultSizes, SessionDocumentCacheBench.ParseSizes(null), "document-cache default sizes");

            var memoryOutput = new StringWriter();
            var memoryRows = await SchedulerMemoryBench.RunAsync([3], memoryOutput, tempRoot: root);
            Check(memoryRows is [{ Cycles: 3, Settled: 0, Edges: <= 1, LateReads: > 0 }], "one scheduler-memory row with bounded indexes");
            Check(memoryOutput.ToString().StartsWith("cycles | ms", StringComparison.Ordinal), "scheduler-memory table: " + memoryOutput);
            Equal(SchedulerMemoryBench.DefaultSizes, SchedulerMemoryBench.ParseSizes(null), "scheduler-memory default sizes");
            var scaleOutput = new StringWriter();
            var scaleRows = await SchedulerScaleBench.RunAsync([4], [5], scaleOutput);
            Check(scaleRows is [{ Scenario: "fanout", Size: 4, Commits: > 0 }, { Scenario: "chain", Size: 5, Commits: > 0 }], "scheduler-scale rows");
            Check(scaleOutput.ToString().StartsWith("fanout | ms", StringComparison.Ordinal), "scheduler-scale table: " + scaleOutput);
            Directory.Delete(root, recursive: true);
        });
    }
}
