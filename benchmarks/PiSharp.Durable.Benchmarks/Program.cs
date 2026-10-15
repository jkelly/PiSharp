using PiSharp.Durable.Benchmarks;

// Usage: PiSharp.Durable.Benchmarks [storage] [storage-memory] [tool-output [<scenario label filter>]] [document-append [N,...]]
//   [document-cache [N,...]]
// With no argument, runs all five (upstream: npm run bench:storage, bench:storage:memory, bench:tool-output, bench:document-append and
// bench:document-cache). Report only: the
// benchmarks print tables and never pass or fail on a timing. Run in Release. The --worker forms are the child processes of storage-memory
// and tool-output.
if (args is ["storage-memory", "--worker", var backend, var scale]) return await StorageMemory.WorkerMainAsync(backend, scale, Console.Out);
if (args is ["tool-output", "--worker", var scenario]) return await ToolOutputBench.WorkerMainAsync(scenario, Console.Out);

string[] benchmarks = ["storage", "storage-memory", "tool-output", "document-append", "document-cache"];
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
        default:
            Console.Error.WriteLine("Usage: PiSharp.Durable.Benchmarks [storage] [storage-memory] [tool-output [<scenario label filter>]] [document-append [N,...]] [document-cache [N,...]]");
            return 2;
    }
}
return 0;
