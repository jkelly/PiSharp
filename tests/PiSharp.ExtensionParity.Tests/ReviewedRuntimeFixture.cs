using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions.Pi;

internal static partial class Program
{
    // Only the test harness consumes this variable. No cache admission marker is written or accepted.
    private static readonly Lazy<string?> ReviewedPiRuntime = new(FindReviewedPiRuntime);

    private static string? FindReviewedPiRuntime()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "tools", "NodeBridge", "runtime-fixture-150", "package.json")))
            repository = repository.Parent;
        var configured = Environment.GetEnvironmentVariable("PISHARP_TEST_PI150_NODE_MODULES");
        if (repository is null)
        {
            if (configured is not null) throw new InvalidDataException("Reviewed runtime fixture manifest is unavailable.");
            return null;
        }
        var modules = Path.GetFullPath(configured ?? Path.Combine(repository.FullName, "artifacts", "node-runtime-150", "node_modules"));
        if (!Directory.Exists(modules))
        {
            if (configured is not null) throw new InvalidDataException("Explicit test runtime fixture does not exist: " + modules);
            return null;
        }
        var reviewed = Path.Combine(repository.FullName, "tools", "NodeBridge", "runtime-fixture-150");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(reviewed, "package.json")))!;
        if (!JsonNode.DeepEquals(PiNodeRuntime.PackageJson(), manifest))
            throw new InvalidDataException("Reviewed runtime fixture manifest differs from the production package pins.");
        var actualRoot = Path.GetDirectoryName(modules)!;
        var actualManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(actualRoot, "package.json")))!;
        if (!JsonNode.DeepEquals(manifest["dependencies"], actualManifest["dependencies"]))
            throw new InvalidDataException("Installed test runtime dependencies differ from the reviewed fixture.");
        var expectedLock = JsonNode.Parse(File.ReadAllText(Path.Combine(reviewed, "package-lock.json")))!["packages"]!.AsObject();
        var actualLock = JsonNode.Parse(File.ReadAllText(Path.Combine(actualRoot, "package-lock.json")))!["packages"]!.AsObject();
        if (expectedLock.Count != actualLock.Count) throw new InvalidDataException("Installed fixture lock package set differs from review.");
        foreach (var (key, expected) in expectedLock)
        {
            if (key.Length == 0) continue;
            var expectedPackage = expected ?? throw new InvalidDataException("Reviewed fixture has an empty package: " + key);
            if (!actualLock.TryGetPropertyValue(key, out var actual) || actual is null ||
                expectedPackage["version"]?.ToJsonString() != actual["version"]?.ToJsonString() ||
                expectedPackage["integrity"]?.ToJsonString() != actual["integrity"]?.ToJsonString() ||
                expectedPackage["resolved"]?.ToJsonString() != actual["resolved"]?.ToJsonString())
                throw new InvalidDataException("Installed fixture lock differs from review: " + key);
            var packageFile = Path.Combine(actualRoot, key.Replace('/', Path.DirectorySeparatorChar), "package.json");
            if (!File.Exists(packageFile) && expectedPackage["optional"]?.GetValue<bool>() == true) continue;
            var installed = JsonNode.Parse(File.ReadAllText(packageFile))!;
            if (installed["version"]?.ToJsonString() != expectedPackage["version"]?.ToJsonString())
                throw new InvalidDataException("Installed package version differs from reviewed lock: " + key);
        }
        return modules;
    }
}
