using PiSharp.Durable.Benchmarks;

// Usage: PiSharp.Durable.Benchmarks [storage] [storage-memory] [tool-output [<scenario label filter>]] [document-append [N,...]]
//   [document-cache [N,...]] [scheduler-memory [N,...]] [scheduler-scale [fanout N,...] [chain N,...]] [scheduler-fanout [N,...]]
//   [nested-tools [streaming|nesting]] [nested-images [count]] [codemode [N,...]] [partial-commits [N,...]]
//   [conversation-view [sqlite|store|both]] [storage-throughput [latency ms,...] [conversations N,...]]
// With no argument, runs all thirteen (upstream: npm run bench:storage, bench:storage:memory, bench:tool-output, bench:document-append,
// bench:document-cache, bench:scheduler-memory, bench:scheduler-fanout and bench:codemode, and test/nested-tools-bench.ts,
// test/nested-images-bench.ts and, Pi v1.5.0 area view, test/conversation-view-bench.ts;
// scheduler-scale, partial-commits and storage-throughput (not part of the default run) are PiSharp-authored, see SchedulerScaleBench.cs and PartialCommitsBench.cs). Report only: the
// benchmarks print tables and never pass or fail on a timing. Run in Release. The --worker forms are the child processes of storage-memory
// and tool-output.
if (args is ["storage-memory", "--worker", var backend, var scale]) return await StorageMemory.WorkerMainAsync(backend, scale, Console.Out);
if (args is ["tool-output", "--worker", var scenario]) return await ToolOutputBench.WorkerMainAsync(scenario, Console.Out);
if (args is ["conversation-view", "--worker", var viewScenario]) return await ConversationViewBench.WorkerMainAsync(viewScenario, Console.Out);

string[] benchmarks = ["storage", "storage-memory", "tool-output", "document-append", "document-cache", "scheduler-memory", "scheduler-scale",
    "scheduler-fanout", "nested-tools", "nested-images", "codemode", "partial-commits", "conversation-view"];
var selected = args.Length == 0 ? benchmarks : args;
string? filter = null;
for (var index = 0; index < selected.Length; index++)
{
    switch (selected[index])
    {
        case "storage":
            Console.WriteLine("## storage.bench");
            await StorageBench.RunAsync(new StorageBenchOptions(), Console.Out);
            break;
        case "storage-throughput":
            string? latencies = null, counts = null;
            while (index + 2 < selected.Length && selected[index + 1] is "latency" or "conversations")
            {
                if (selected[++index] == "latency") latencies = selected[++index];
                else counts = selected[++index];
            }
            Console.WriteLine("## storage-throughput (PiSharp-authored)");
            await StorageBench.ThroughputAsync(StorageBench.ParseList(latencies, StorageBench.DefaultThroughputLatencies),
                StorageBench.ParseList(counts, StorageBench.DefaultThroughputConversations), Console.Out);
            break;
        case "storage-memory":
            Console.WriteLine("## storage-memory");
            await StorageMemory.RunDriverAsync(Console.Out);
            break;
        case "tool-output":
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) filter = selected[++index];
            Console.WriteLine("## tool-output-bench");
            await ToolOutputBench.RunDriverAsync(Console.Out,
                filter is null ? null : scenarioFilter => scenarioFilter.Label.Contains(filter, StringComparison.Ordinal));
            break;
        case "document-append":
            string? sizes = null;
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) sizes = selected[++index];
            Console.WriteLine("## document-append-bench");
            await DocumentAppendBench.RunAsync(DocumentAppendBench.ParseSizes(sizes), Console.Out);
            break;
        case "document-cache":
            string? conversations = null;
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) conversations = selected[++index];
            Console.WriteLine("## session-document-cache-bench");
            await SessionDocumentCacheBench.RunAsync(SessionDocumentCacheBench.ParseSizes(conversations), Console.Out);
            break;
        case "scheduler-memory":
            string? cycles = null;
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) cycles = selected[++index];
            Console.WriteLine("## scheduler-memory-bench");
            await SchedulerMemoryBench.RunAsync(SchedulerMemoryBench.ParseSizes(cycles), Console.Out);
            break;
        case "scheduler-scale":
            string? fanout = null, chain = null;
            while (index + 2 < selected.Length && selected[index + 1] is "fanout" or "chain")
            {
                if (selected[++index] == "fanout") fanout = selected[++index];
                else chain = selected[++index];
            }
            Console.WriteLine("## scheduler-scale (PiSharp-authored)");
            await SchedulerScaleBench.RunAsync(SchedulerScaleBench.ParseSizes(fanout, SchedulerScaleBench.DefaultFanout),
                SchedulerScaleBench.ParseSizes(chain, SchedulerScaleBench.DefaultChain), Console.Out);
            break;
        case "scheduler-fanout":
            string? calls = null;
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) calls = selected[++index];
            Console.WriteLine("## scheduler-fanout-bench");
            await SchedulerFanoutBench.RunAsync(SchedulerFanoutBench.ParseSizes(calls), Console.Out);
            break;
        case "nested-tools":
            string? section = null;
            if (index + 1 < selected.Length && selected[index + 1] is "streaming" or "nesting") section = selected[++index];
            Console.WriteLine("## nested-tools-bench");
            await NestedToolsBench.RunAsync(section, Console.Out);
            break;
        case "nested-images":
            string? count = null;
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) count = selected[++index];
            Console.WriteLine("## nested-images-bench");
            await NestedImagesBench.RunAsync(NestedImagesBench.ParseCounts(count), Console.Out);
            break;
        case "codemode":
            string? codemodeSizes = null;
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) codemodeSizes = selected[++index];
            Console.WriteLine("## codemode-bench");
            await CodemodeBench.RunAsync(CodemodeBench.ParseSizes(codemodeSizes), Console.Out);
            break;
        case "partial-commits":
            string? partialSizes = null;
            if (index + 1 < selected.Length && !benchmarks.Contains(selected[index + 1])) partialSizes = selected[++index];
            Console.WriteLine("## partial-commits (PiSharp-authored)");
            await PartialCommitsBench.RunAsync(PartialCommitsBench.ParseSizes(partialSizes), Console.Out);
            break;
        case "conversation-view":
            string? viewStorage = null;
            if (index + 1 < selected.Length && selected[index + 1] is "sqlite" or "store" or "both") viewStorage = selected[++index];
            Console.WriteLine("## conversation-view-bench");
            await ConversationViewBench.RunDriverAsync(ConversationViewBench.Storages(viewStorage), Console.Out);
            break;
        default:
            Console.Error.WriteLine("Usage: PiSharp.Durable.Benchmarks [storage] [storage-memory] [tool-output [<scenario label filter>]] [document-append [N,...]] [document-cache [N,...]] [scheduler-memory [N,...]] [scheduler-scale [fanout N,...] [chain N,...]] [scheduler-fanout [N,...]] [nested-tools [streaming|nesting]] [nested-images [count]] [codemode [N,...]] [partial-commits [N,...]] [conversation-view [sqlite|store|both]] [storage-throughput [latency ms,...] [conversations N,...]]");
            return 2;
    }
}
return 0;
