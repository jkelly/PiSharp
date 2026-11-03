using System.Text.Json.Nodes;
using PiSharp.Cli.Packages;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/package-manager.ts (getNpmInstallArgs,
// getGitDependencyInstallArgs and uninstallNpm add --no-audit --no-fund wherever npm gets --legacy-peer-deps).
// Expectations are written from the sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150CliCases() =>
    [
        ("sync150.package-npm-skips-audit-and-fund-advice", PackageNpmNoAuditFund),
    ];

    // npm audit fix would run without --legacy-peer-deps and install a second copy of pi's peers into the package directory, so every npm
    // call that disables peer resolution also skips audit and fund advice; bun and pnpm keep their 1.2.0 arguments.
    private static async Task PackageNpmNoAuditFund()
    {
        using var sandbox = new Sandbox("pkg-no-audit-fund");
        var root = Path.Combine(sandbox.Root, "absent-root");
        Names(["install", "a", "b", "--prefix", root, "--legacy-peer-deps", "--no-audit", "--no-fund"], Manager(sandbox).GetNpmInstallArgs(["a", "b"], root), "npm install");
        Names(["install", "--omit=dev", "--legacy-peer-deps", "--no-audit", "--no-fund"], Manager(sandbox).GetGitDependencyInstallArgs(), "npm git dependencies");
        var bun = Manager(sandbox, J("""{"npmCommand":["bun"]}"""));
        Names(["install", "a", "--cwd", root, "--omit=peer"], bun.GetNpmInstallArgs(["a"], root), "bun install");
        Names(["install", "--omit=dev", "--omit=peer"], bun.GetGitDependencyInstallArgs(), "bun git dependencies");
        var pnpm = Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""));
        Names(["install", "a", "--prefix", root, "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"],
            pnpm.GetNpmInstallArgs(["a"], root), "pnpm install");

        var processes = new FakeProcesses();
        await Manager(sandbox, processes: processes).InstallAsync("npm:audit-pkg");
        var npmRoot = Path.Combine(sandbox.AgentDir, "npm");
        processes = new FakeProcesses();
        await Manager(sandbox, processes: processes).RemoveAsync("npm:audit-pkg");
        Check(processes.Ran("npm", ["uninstall", "audit-pkg", "--prefix", npmRoot, "--legacy-peer-deps", "--no-audit", "--no-fund"]),
            "npm uninstall: " + string.Join(" | ", processes.Runs.Select(run => string.Join(' ', run.Args))));
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""), processes: processes).InstallAsync("npm:audit-pkg");
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""), processes: processes).RemoveAsync("npm:audit-pkg");
        Check(processes.Ran("pnpm", ["uninstall", "audit-pkg", "--prefix", npmRoot]), "pnpm uninstall: " + string.Join(" | ", processes.Runs.Select(run => string.Join(' ', run.Args))));
    }
}
