// Authored from Pi b4afdddf44e059c67ccf5097e5317d913521b470 core/package-manager.ts and package-manager-cli.ts.
using System.Collections.Concurrent;
using PiSharp.Cli.Packages;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync152PackageCases() =>
    [
        ("sync152.package.results-are-changes-in-configured-order", Sync152PackageResults),
        ("sync152.package.aggregate-outcomes", Sync152PackageAggregates),
    ];

    private static async Task Sync152PackageResults()
    {
        using var sandbox = new Sandbox("sync152-package-results");
        const string first = "git:github.com/synthetic/first";
        const string last = "git:github.com/synthetic/last";
        var heads = new ConcurrentDictionary<string, string>();
        var oldHead = new string('a', 40); var newHead = new string('b', 40);
        var events = new List<PiPackageProgressEvent>();
        var secondFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new FakeProcesses
        {
            OnCapture = (command, args, cwd) => command == "npm" ? "\"2.0.0\"" : args switch
            {
                ["rev-parse", "HEAD"] => heads[cwd!],
                ["rev-parse", "--abbrev-ref", "@{upstream}"] => "origin/main",
                _ => newHead
            },
            OnRun = async (command, args, cwd) =>
            {
                if (command == "npm")
                {
                    if (args.Contains("fresh@latest"))
                        sandbox.Write(Path.Combine(sandbox.AgentDir, "npm", "node_modules", "fresh", "package.json"), """{"name":"fresh","version":"2.0.0"}""");
                    return;
                }
                if (args[0] != "reset") return;
                if (cwd!.EndsWith("first", StringComparison.Ordinal)) await secondFinished.Task;
                else secondFinished.TrySetResult();
                heads[cwd] = newHead;
            }
        };
        var manager = Manager(sandbox, J("""{"packages":["git:github.com/synthetic/first","npm:fresh","npm:pinned@1.0.0","git:github.com/synthetic/last"]}"""),
            J("""{"packages":["npm:project-fresh"]}"""), processes: processes);
        foreach (var source in new[] { first, last })
        {
            var path = manager.GetGitInstallPath((PiGitSource)PiPackageSource.Parse(source), "user");
            Directory.CreateDirectory(path); heads[path] = oldHead;
        }
        foreach (var name in new[] { "fresh", "pinned" })
            sandbox.Write(Path.Combine(sandbox.AgentDir, "npm", "node_modules", name, "package.json"), Jstr(new { name, version = "1.0.0" }));
        // A missing npm installation stays missing, so it never claims a changed package.
        manager.SetProgressCallback(item => { lock (events) events.Add(item); });
        var summary = await manager.UpdateAsync();
        var results = events.Where(item => item.Type == "result").ToArray();
        Names([first, "npm:fresh", last], results.Select(item => item.Source), "result order ignores worker completion order and unchanged packages");
        Names([$"{first} aaaaaaa -> bbbbbbb", "npm:fresh 1.0.0 -> 2.0.0", $"{last} aaaaaaa -> bbbbbbb"], results.Select(item => item.Message!), "displayed identities");
        Check(results.All(item => item.Action == "update"), "result action");
        Check(summary.CliLines().First() == "Updated 3 packages:", "changed aggregate count");
    }

    private static async Task Sync152PackageAggregates()
    {
        using var sandbox = new Sandbox("sync152-package-aggregates");
        var empty = await Manager(sandbox).UpdateAsync();
        Equal("Packages are already up to date", empty.CliLines().Single(), "empty configuration");
        var offline = await Manager(sandbox, environment: new() { ["PI_OFFLINE"] = "1" }).UpdateAsync();
        Equal("Skipped package updates (offline mode)", offline.CliLines().Single(), "empty offline differs from checked empty");
        var warned = new PiPackageUpdateSummary([new("npm:stale", "user", PiPackageUpdateStatus.Warning, "1", "1", "unchanged")]);
        Equal("No packages were updated, see the warnings above", warned.CliLines().Single(), "warnings suppress up-to-date claim");
        Equal("npm:stale was not updated, see the warnings above", warned.CliLines("npm:stale").Single(), "targeted warning");
        var changed = new PiPackageUpdateSummary([new("npm:fresh", "project", PiPackageUpdateStatus.Warning, "1", "2", "metadata unavailable")]);
        Names(["Updated 1 package:", "  npm:fresh (project) 1 -> 2"], changed.CliLines(), "actual change survives warning");
    }
}
