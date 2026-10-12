using System.Reflection;
using System.Text.RegularExpressions;
using PiSharp.Durable.Storage;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/storage-runtime-boundary.test.ts, adapted. Upstream walks the
// runtime import graph from each portable entry point and requires that it reaches no Node built-in and no Node-specific implementation
// file: the package root reaches neither env/ nor storage/jsonl/ nor storage/sqlite/; env/index.ts never reaches env/node.ts; and
// storage/jsonl/index.ts reaches neither storage/jsonl/node.ts nor env/node.ts. Only env/node.ts and storage/jsonl/node.ts touch the
// local runtime. The .NET analogue is the assembly and source boundary, with each Node-specific file mapped to a Local/ folder:
// - env/node.ts (its file-system half) is PiSharp.Durable/Env/Local/ (LocalFileSystem); its shell half joins PiSharp.Durable.Tools (P9);
// - storage/jsonl/node.ts is PiSharp.Durable/Storage/Jsonl/Local/ (JsonlStorage.OpenAsync over LocalFileSystem).
// The portable core (PiSharp.Durable outside Env/, Storage/Jsonl/ and Testing/) references only Contracts and AI, reaches neither the
// environment nor JSONL nor SQLite, and touches no local file or process API. Testing/ (upstream's separate testing subpath) may use the
// portable environment, never a Local/ folder. Portable Env/ and Storage/Jsonl/ files touch no local API and never reach a Local/ folder
// or PiSharp.Durable.Tools. The Local/ folders may use System.IO but no process API, and do not reach PiSharp.Durable.Tools either.
internal static partial class Program
{
    private static readonly Regex BoundaryLocalApi = new(
        @"\b(System\.IO\.(File|Directory|FileInfo|DirectoryInfo|FileStream|DriveInfo)\b|File\.(Open|Read|Write|Append|Create|Delete|Exists|Move|Copy)\w*\s*\(|" +
        @"Directory\.\w+\s*\(|new\s+FileStream\s*\(|System\.Diagnostics\.Process\b|Process\.Start\s*\(|Environment\.(GetEnvironmentVariable|CurrentDirectory))",
        RegexOptions.CultureInvariant);

    private static readonly Regex BoundaryProcessApi = new(@"\b(System\.Diagnostics\.Process\b|Process\.Start\s*\(|new\s+Process\s*\(|ProcessStartInfo\b)",
        RegexOptions.CultureInvariant);

    /// <summary>What reaches a Local/ folder: its namespace or its types.</summary>
    private static readonly Regex BoundaryLocalReference = new(@"\bPiSharp\.Durable\.Env\.Local\b|\bLocalFileSystem\b", RegexOptions.CultureInvariant);

    private static IEnumerable<(string, Func<Task>)> StorageRuntimeBoundaryCases() =>
    [
        Case("storage.runtime-boundary.keeps-the-package-root-limited-to-portable-core-storage", () =>
        {
            var references = typeof(MemoryStorage).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();
            foreach (var name in references)
                Check(name is "PiSharp.Contracts" or "PiSharp.AI" or "netstandard" || name.StartsWith("System", StringComparison.Ordinal),
                    "PiSharp.Durable references " + name);
            var root = DurableSourceRoot();
            foreach (var file in BoundaryCoreSources(root, excluded: ["Env", Path.Combine("Storage", "Jsonl"), "Testing"]))
            {
                var source = File.ReadAllText(file);
                Check(!source.Contains("PiSharp.Durable.Env", StringComparison.Ordinal), BoundaryRelative(root, file) + " reaches the environment");
                Check(!source.Contains("PiSharp.Durable.Storage.Jsonl", StringComparison.Ordinal), BoundaryRelative(root, file) + " reaches JSONL storage");
                Check(!source.Contains("PiSharp.Durable.Sqlite", StringComparison.Ordinal), BoundaryRelative(root, file) + " reaches SQLite storage");
                Check(!BoundaryLocalApi.IsMatch(source), BoundaryRelative(root, file) + " uses a local file or process API: " + BoundaryLocalApi.Match(source).Value);
            }
            // The testing subpath may use the portable environment's types, but no backend and no local runtime.
            foreach (var file in BoundaryFolderSources(Path.Combine(root, "Testing"), local: false))
            {
                var source = File.ReadAllText(file);
                Check(!BoundaryLocalReference.IsMatch(source), BoundaryRelative(root, file) + " reaches the local environment");
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
            BoundaryPortableFolder(Path.Combine(DurableSourceRoot(), "Env"));
        }),
        Case("storage.runtime-boundary.keeps-the-portable-jsonl-subpath-free-of-node-imports", () =>
            BoundaryPortableFolder(Path.Combine(DurableSourceRoot(), "Storage", "Jsonl"))),
        // Authored: the counterpart of env/node.ts and storage/jsonl/node.ts may use the local file system, but nothing else local.
        Case("storage.runtime-boundary.keeps-the-local-folders-to-the-file-system", () =>
        {
            var root = DurableSourceRoot();
            var local = new[] { Path.Combine(root, "Env", "Local"), Path.Combine(root, "Storage", "Jsonl", "Local") }
                .Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)).ToList();
            Check(File.Exists(Path.Combine(root, "Env", "Local", "LocalFileSystem.cs")), "LocalFileSystem is in Env/Local/");
            Check(!Directory.Exists(Path.Combine(root, "Storage", "Jsonl")) || Directory.Exists(Path.Combine(root, "Storage", "Jsonl", "Local")),
                "the local JSONL opener is in Storage/Jsonl/Local/");
            foreach (var file in local)
            {
                var source = File.ReadAllText(file);
                Check(!source.Contains("PiSharp.Durable.Tools", StringComparison.Ordinal), BoundaryRelative(root, file) + " reaches the local tools");
                Check(!BoundaryProcessApi.IsMatch(source), BoundaryRelative(root, file) + " uses a process API: " + BoundaryProcessApi.Match(source).Value);
            }
        }),
    ];

    private static string DurableSourceRoot() => Path.Combine(RepositoryRoot(), "src", "PiSharp.Durable");

    private static IEnumerable<string> BoundaryCoreSources(string root, string[] excluded) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !BoundaryRelative(root, file).Split(Path.DirectorySeparatorChar)[0].Equals("bin", StringComparison.Ordinal) &&
                !BoundaryRelative(root, file).Split(Path.DirectorySeparatorChar)[0].Equals("obj", StringComparison.Ordinal) &&
                !excluded.Any(folder => BoundaryRelative(root, file).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)));

    /// <summary>The sources of a folder (absent until its phase lands), with or without its Local/ subfolder.</summary>
    private static IEnumerable<string> BoundaryFolderSources(string folder, bool local) =>
        !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(file => local || BoundaryRelative(folder, file).Split(Path.DirectorySeparatorChar)[0] != "Local");

    /// <summary>A portable folder reaches neither the local implementation (its Local/ subfolder, LocalFileSystem or PiSharp.Durable.Tools)
    /// nor local runtime APIs.</summary>
    private static void BoundaryPortableFolder(string folder)
    {
        foreach (var file in BoundaryFolderSources(folder, local: false))
        {
            var source = File.ReadAllText(file);
            Check(!source.Contains("PiSharp.Durable.Tools", StringComparison.Ordinal), file + " reaches the local implementation");
            Check(!BoundaryLocalReference.IsMatch(source), file + " reaches the local environment");
            Check(!BoundaryLocalApi.IsMatch(source), file + " uses a local file or process API: " + BoundaryLocalApi.Match(source).Value);
        }
    }

    private static string BoundaryRelative(string root, string file) => Path.GetRelativePath(root, file);
}
