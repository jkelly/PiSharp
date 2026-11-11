using System.Diagnostics;
using PiSharp.Cli.Extensions.Pi;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> GapRuntimeCases() =>
    [
        ("gap.runtime-installed-pi150-real-abi-and-attribution", InstalledRuntimeAbi),
        ("gap.runtime-old-cache-isolated-and-offline-rollback-preserved", RuntimeCacheIsolation),
        ("gap.runtime-committed-fixture-matches-production-manifest", CommittedRuntimeFixture),
        ("gap.overlay-node-factory-lifetime-and-shutdown-handles", OverlayNodeLifetime),
    ];

    private static Task CommittedRuntimeFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "NodeBridge", "runtime-fixture-152", "package.json"))) root = root.Parent;
        if (root is null) throw new SkipException("NO_RUNTIME_FIXTURE", "Repository runtime fixture manifest unavailable.");
        var directory = Path.Combine(root.FullName, "tools", "NodeBridge", "runtime-fixture-152");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(PiNodeRuntime.PackageJson(), manifest), "committed fixture matches production manifest exactly");
        var locked = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "package-lock.json")))!;
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(manifest!["dependencies"], locked["packages"]![""]!["dependencies"]), "lock root matches manifest dependencies");
        Equal(manifest["name"]!.GetValue<string>(), locked["name"]!.GetValue<string>(), "lock name");
        return Task.CompletedTask;
    }

    private static async Task OverlayNodeLifetime()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "NodeBridge", "overlay-lifetime.test.mjs"))) root = root.Parent;
        if (root is null) throw new SkipException("NO_RUNTIME_FIXTURE", "Repository overlay fixture script unavailable.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PISHARP_NODE") ?? "node")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tools", "NodeBridge", "overlay-lifetime.test.mjs"));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Equal(0, process.ExitCode, "Node overlay lifetime: " + await stderr + await stdout);
    }

    private static async Task InstalledRuntimeAbi()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "NodeBridge", "installed-runtime-abi.mjs"))) root = root.Parent;
        if (root is null) throw new SkipException("NO_RUNTIME_FIXTURE", "Repository ABI fixture script unavailable.");
        var modules = Environment.GetEnvironmentVariable("PISHARP_TEST_PI152_NODE_MODULES")
            ?? Path.Combine(root.FullName, "artifacts", "node-runtime-152", "node_modules");
        if (!File.Exists(Path.Combine(modules, "@earendil-works", "pi-coding-agent", "package.json")))
            throw new SkipException("NO_RUNTIME_FIXTURE", "Install the exact published Pi 1.5.2 runtime fixture to execute this ABI test.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PISHARP_NODE") ?? "node")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tools", "NodeBridge", "installed-runtime-abi.mjs"));
        start.ArgumentList.Add(modules);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Equal(0, process.ExitCode, "actual package ABI fixture: " + await stderr + await stdout);
    }

    private static async Task RuntimeCacheIsolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var old = Path.Combine(root, "package.json");
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"version\":\"1.2.0\",\"rollback\":true}");
        await File.WriteAllBytesAsync(old, bytes);
        try
        {
            string? Env(string name) => name switch { "PISHARP_PI_RUNTIME_DIR" => root, "PI_OFFLINE" => "1", _ => null };
            var target = PiNodeRuntime.Directory(root, Env);
            Equal(Path.Combine(root, PiNodeRuntime.CacheVersion), target, "version and policy isolated target");
            var (modules, fallback) = await PiNodeRuntime.EnsureAsync(root, Env, TextWriter.Null, null, null, CancellationToken.None);
            Check(modules is null && fallback?.Contains("offline", StringComparison.Ordinal) == true, "offline upgrade uses explicit fallback");
            var oldBytesAfterOffline = await File.ReadAllBytesAsync(old);
            Check(bytes.SequenceEqual(oldBytesAfterOffline), "old configured runtime stays byte-for-byte available for rollback");
            Check(!Directory.Exists(target), "offline attempt does not create a new cache");
            Directory.CreateDirectory(target);
            var marker = Path.Combine(target, "unverified-marker");
            await File.WriteAllTextAsync(marker, "keep for inspection");
            var processes = new UnexpectedRuntimeInstall();
            var failed = await PiNodeRuntime.EnsureAsync(root, name => name == "PISHARP_PI_RUNTIME_DIR" ? root : null,
                TextWriter.Null, processes, null, CancellationToken.None);
            Check(failed.NodeModules is null && failed.Fallback?.Contains("preserved", StringComparison.Ordinal) == true,
                "unverified target is an explicit fallback");
            Equal("keep for inspection", await File.ReadAllTextAsync(marker), "unverified target preserved");
            Check(!processes.Called, "no install overwrites an existing target");
            var oldBytesAfterFailure = await File.ReadAllBytesAsync(old);
            Check(bytes.SequenceEqual(oldBytesAfterFailure), "failed upgrade preserves rollback bytes");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class UnexpectedRuntimeInstall : PiSharp.Cli.Packages.PiPackageProcesses
    {
        internal bool Called;
        internal override string RunSync(string command, IReadOnlyList<string> args)
        { Called = true; throw new InvalidOperationException("Existing cache must be preserved before invoking npm."); }
    }
}
