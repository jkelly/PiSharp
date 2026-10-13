using PiSharp.Durable.Benchmarks;

// Usage: PiSharp.Durable.Benchmarks [storage] [storage-memory] [tool-output [<scenario label filter>]]
// With no argument, runs all three (upstream: npm run bench:storage, bench:storage:memory and bench:tool-output). Report only: the
// benchmarks print tables and never pass or fail on a timing. Run in Release. The --worker forms are the child processes of storage-memory
// and tool-output.
if (args is ["storage-memory", "--worker", var backend, var scale]) return await StorageMemory.WorkerMainAsync(backend, scale, Console.Out);
if (args is ["tool-output", "--worker", var scenario]) return await ToolOutputBench.WorkerMainAsync(scenario, Console.Out);

var selected = args.Length == 0 ? ["storage", "storage-memory", "tool-output"] : args;
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
            if (index + 1 < selected.Length && selected[index + 1] is not ("storage" or "storage-memory")) filter = selected[++index];
            Console.WriteLine("## tool-output-bench");
            await ToolOutputBench.RunDriverAsync(Console.Out,
                filter is null ? null : scenarioFilter => scenarioFilter.Label.Contains(filter, StringComparison.Ordinal));
            break;
        default:
            Console.Error.WriteLine("Usage: PiSharp.Durable.Benchmarks [storage] [storage-memory] [tool-output [<scenario label filter>]]");
            return 2;
    }
}
return 0;
