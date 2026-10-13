using PiSharp.Durable.Examples;

// Usage: Durable.Examples [--storage memory|jsonl|sqlite] [--dir <directory>] [example ...]
// Examples are named by number (00-15, 17, 20, 25, 29, 30, 31) or file name (00-conversation). File storage goes under --dir, or a fresh temporary directory.
var storage = ExampleStorage.Memory;
string? root = null;
var only = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--storage" when index + 1 < args.Length:
            storage = args[++index] switch
            {
                "memory" => ExampleStorage.Memory,
                "jsonl" => ExampleStorage.Jsonl,
                "sqlite" => ExampleStorage.Sqlite,
                var other => throw new ArgumentException($"Unknown storage '{other}'; use memory, jsonl or sqlite."),
            };
            break;
        case "--dir" when index + 1 < args.Length:
            root = args[++index];
            break;
        case var name when !name.StartsWith('-'):
            only.Add(name);
            break;
        default:
            Console.Error.WriteLine("Usage: Durable.Examples [--storage memory|jsonl|sqlite] [--dir <directory>] [example ...]");
            return 2;
    }
}
if (storage != ExampleStorage.Memory)
{
    root ??= Path.Combine(Path.GetTempPath(), "pisharp-durable-examples", Guid.NewGuid().ToString("N"));
    Console.WriteLine($"storage: {storage.ToString().ToLowerInvariant()} under {Path.GetFullPath(root)}");
}
await Examples.RunAsync(storage, root, Console.Out, only);
return 0;
