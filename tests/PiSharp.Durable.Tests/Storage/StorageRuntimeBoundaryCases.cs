using System.Reflection;
using System.Text.RegularExpressions;
using PiSharp.Durable.Storage;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/storage-runtime-boundary.test.ts, adapted. Upstream walks the
// runtime import graph from each portable entry point and requires that it reaches no Node built-in and no Node-specific implementation
// file. The .NET analogue is the assembly and source boundary: the portable core (PiSharp.Durable) references only Contracts and AI and
// its sources outside the environment-specific folders touch no local file or process API; the portable environment and JSONL code do
// not reach the local implementation in PiSharp.Durable.Tools (P9); SQLite (PiSharp.Durable.Sqlite) does not reach it either.
internal static partial class Program
{
    private static readonly Regex BoundaryLocalApi = new(
        @"\b(System\.IO\.(File|Directory|FileInfo|DirectoryInfo|FileStream|DriveInfo)\b|File\.(Open|Read|Write|Append|Create|Delete|Exists|Move|Copy)\w*\s*\(|" +
        @"Directory\.\w+\s*\(|new\s+FileStream\s*\(|System\.Diagnostics\.Process\b|Process\.Start\s*\(|Environment\.(GetEnvironmentVariable|CurrentDirectory))",
        RegexOptions.CultureInvariant);

    private static IEnumerable<(string, Func<Task>)> StorageRuntimeBoundaryCases() =>
    [
        Case("storage.runtime-boundary.keeps-the-package-root-limited-to-portable-core-storage", () =>
        {
            var references = typeof(MemoryStorage).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();
            foreach (var name in references)
                Check(name is "PiSharp.Contracts" or "PiSharp.AI" or "netstandard" || name.StartsWith("System", StringComparison.Ordinal),
                    "PiSharp.Durable references " + name);
            var root = Path.Combine(RepositoryRoot(), "src", "PiSharp.Durable");
            foreach (var file in BoundaryCoreSources(root, excluded: ["Env", Path.Combine("Storage", "Jsonl")]))
            {
                var source = File.ReadAllText(file);
                Check(!source.Contains("PiSharp.Durable.Env", StringComparison.Ordinal), BoundaryRelative(root, file) + " reaches the environment");
                Check(!source.Contains("PiSharp.Durable.Storage.Jsonl", StringComparison.Ordinal), BoundaryRelative(root, file) + " reaches JSONL storage");
                Check(!source.Contains("PiSharp.Durable.Sqlite", StringComparison.Ordinal), BoundaryRelative(root, file) + " reaches SQLite storage");
                Check(!BoundaryLocalApi.IsMatch(source), BoundaryRelative(root, file) + " uses a local file or process API: " + BoundaryLocalApi.Match(source).Value);
            }
        }),
        Case("storage.runtime-boundary.keeps-the-portable-sqlite-subpath-free-of-node-imports", () =>
        {
            var sqlite = Assembly.Load("PiSharp.Durable.Sqlite");
            Check(sqlite.GetReferencedAssemblies().All(name => name.Name != "PiSharp.Durable.Tools"), "PiSharp.Durable.Sqlite references the local tools");
        }),
        Case("storage.runtime-boundary.keeps-the-portable-environment-subpath-free-of-node-imports", () =>
        {
            Check(typeof(MemoryStorage).Assembly.GetReferencedAssemblies().All(name => name.Name != "PiSharp.Durable.Tools"),
                "PiSharp.Durable references the local environment");
            BoundaryPortableFolder(Path.Combine(RepositoryRoot(), "src", "PiSharp.Durable", "Env"));
        }),
        Case("storage.runtime-boundary.keeps-the-portable-jsonl-subpath-free-of-node-imports", () =>
            BoundaryPortableFolder(Path.Combine(RepositoryRoot(), "src", "PiSharp.Durable", "Storage", "Jsonl"))),
    ];

    private static IEnumerable<string> BoundaryCoreSources(string root, string[] excluded) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !BoundaryRelative(root, file).Split(Path.DirectorySeparatorChar)[0].Equals("bin", StringComparison.Ordinal) &&
                !BoundaryRelative(root, file).Split(Path.DirectorySeparatorChar)[0].Equals("obj", StringComparison.Ordinal) &&
                !excluded.Any(folder => BoundaryRelative(root, file).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)));

    /// <summary>A portable folder (absent until its phase lands) reaches neither the local implementation nor local runtime APIs.</summary>
    private static void BoundaryPortableFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Check(!source.Contains("PiSharp.Durable.Tools", StringComparison.Ordinal), file + " reaches the local implementation");
            Check(!BoundaryLocalApi.IsMatch(source), file + " uses a local file or process API: " + BoundaryLocalApi.Match(source).Value);
        }
    }

    private static string BoundaryRelative(string root, string file) => Path.GetRelativePath(root, file);
}
