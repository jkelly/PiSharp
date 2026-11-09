using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;
using PiSharp.Tools.Processes.Unix;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/utils/tools-manager.ts (getReleaseTarget, ensureTool),
// utils/shell.ts (killProcessTree's SIGSTOP on macOS) and test/tools.test.ts (grep --no-config and a lone CR, through real ripgrep).
// Pi 1.5.0 behaviour. The grep inputs and expectations are the upstream tests', read from the source; the others are authored. Real
// processes run only where noted: rg on this machine's PATH (skipped without it), and /bin/sh on Linux and macOS.
// Authored postbaseline C06 delta (8da141c2, reviewed at 7d073328): PiGrepTool now consumes ripgrep's bounded context records,
// preserving a lone CR inside a line while stripping CRLF line terminators. This supersedes the old display's dropped-CR assertion.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.fd-rg-unsupported-arch.release-targets", Sync(() =>
        {
            Equal("aarch64-apple-darwin.tar.gz", PiToolsManager.ReleaseTarget("darwin", "arm64"), "darwin arm64");
            Equal("x86_64-unknown-linux-musl.tar.gz", PiToolsManager.ReleaseTarget("linux", "x64"), "linux x64");
            Equal("aarch64-pc-windows-msvc.zip", PiToolsManager.ReleaseTarget("win32", "arm64"), "win32 arm64");
            foreach (var (platform, architecture) in new[] { ("linux", "arm"), ("linux", "ia32"), ("win32", "ia32"), ("darwin", "riscv64"), ("freebsd", "x64") })
                Equal(null, PiToolsManager.ReleaseTarget(platform, architecture), platform + "/" + architecture);
            Equal("ripgrep-14.1.1-aarch64-unknown-linux-musl.tar.gz", PiToolsManager.Tools["rg"].AssetName("14.1.1", "aarch64-unknown-linux-musl.tar.gz"), "rg asset");
            Equal("fd-v10.2.0-x86_64-pc-windows-msvc.zip", PiToolsManager.Tools["fd"].AssetName("10.2.0", "x86_64-pc-windows-msvc.zip"), "fd asset");
        })),
        ("sync150.fd-rg-unsupported-arch.warns-and-downloads-nothing", async () =>
        {
            using var sandbox = new Sandbox("tools-unsupported-arch");
            var releases = new Releases().Latest("BurntSushi/ripgrep", "14.1.1").Latest("sharkdp/fd", "v10.2.0");
            var statuses = new List<string>();
            var arm = new PiToolsManager(Path.Combine(sandbox.AgentDir, "bin"), _ => null, () => releases, "https://gh.test") { Platform = "linux", Architecture = "arm" };
            Equal(null, await arm.EnsureToolAsync("rg", status => statuses.Add(status.Type + ":" + status.Message), CancellationToken.None), "rg");
            Equal(null, await arm.EnsureToolAsync("fd", status => statuses.Add(status.Type + ":" + status.Message), CancellationToken.None), "fd");
            Names(["warning:ripgrep not found. Automatic download is not supported on linux/arm. Install ripgrep with your system package manager.",
                "warning:fd not found. Automatic download is not supported on linux/arm. Install fd with your system package manager."], statuses, "statuses");
            Equal(0, releases.Seen.Count, "no request: " + string.Join(", ", releases.Seen));
            Check(!Directory.Exists(Path.Combine(sandbox.AgentDir, "bin")), "no bin directory created");
            // Offline mode is reported first, as upstream checks it first.
            statuses.Clear();
            var offline = new PiToolsManager(Path.Combine(sandbox.AgentDir, "bin"), name => name == "PI_OFFLINE" ? "1" : null, () => releases, "https://gh.test")
            { Platform = "win32", Architecture = "ia32" };
            Equal(null, await offline.EnsureToolAsync("rg", status => statuses.Add(status.Type + ":" + status.Message), CancellationToken.None), "offline rg");
            Names(["warning:ripgrep not found. Offline mode enabled, skipping download."], statuses, "offline status");
            // A binary already in the bin directory is used on any architecture.
            var bin = Path.Combine(sandbox.AgentDir, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "rg"), "RG");
            Equal(Path.Join(bin, "rg"), await arm.EnsureToolAsync("rg", null, CancellationToken.None), "existing binary");
        }),
        ("sync150.grep-no-config.real-ripgrep-ignores-ripgrep-config-path", async () =>
        {
            var rg = OnProcessPath("rg") ?? throw new SkipException("rg is not on this machine's PATH.");
            using var sandbox = new Sandbox("grep-no-config");
            var config = Path.Combine(sandbox.Root, "rg-config");
            File.WriteAllText(config, "--fixed-strings\n");
            var fixture = Path.Combine(sandbox.Cwd, "fixture.txt");
            File.WriteAllText(fixture, "aXb\na.b\n");
            var output = await Grep(rg, sandbox.Cwd, """{"pattern":"a.b","path":"fixture.txt","literal":false}""", ("RIPGREP_CONFIG_PATH", config));
            Check(output.Contains("fixture.txt:1: aXb", StringComparison.Ordinal) && output.Contains("fixture.txt:2: a.b", StringComparison.Ordinal), output);
        }),
        ("sync150.grep-lone-cr-context.real-ripgrep-line-numbers", async () =>
        {
            var rg = OnProcessPath("rg") ?? throw new SkipException("rg is not on this machine's PATH.");
            using var sandbox = new Sandbox("grep-lone-cr");
            File.WriteAllText(Path.Combine(sandbox.Cwd, "lone-cr.txt"), "before\rprogress\nneedle\nafter");
            var output = await Grep(rg, sandbox.Cwd, """{"pattern":"needle","path":"lone-cr.txt","context":1}""");
            Equal("lone-cr.txt-1- before\rprogress\nlone-cr.txt:2: needle\nlone-cr.txt-3- after", output,
                "reviewed context preserves the interior CR and exact ripgrep line numbers");
            // CR in a CRLF delimiter is still removed; it must not produce a fourth line or enter the displayed text.
            File.WriteAllText(Path.Combine(sandbox.Cwd, "lone-cr.txt"), "before\r\nneedle\r\nafter\r\n");
            var crlf = await Grep(rg, sandbox.Cwd, """{"pattern":"needle","path":"lone-cr.txt","context":1}""");
            Equal("lone-cr.txt-1- before\nlone-cr.txt:2: needle\nlone-cr.txt-3- after", crlf, "CRLF terminators are stripped without altering line numbers");
        }),
        // Authored: `sleep; touch` killed while sleeping must never reach the touch. macOS stops the group first, so a killed sleep
        // cannot wake its shell into the next command before the shell itself is killed; Linux signals the group at once.
        ("sync150.macos-stop-group-before-kill.no-next-command-after-stop", async () =>
        {
            UnixOnly();
            using var sandbox = new Sandbox("unix-stop-group");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var marker = Path.Combine(sandbox.Root, $"marker-{attempt}");
                var request = new ProcessRequest("/bin/sh", ["-c", "sleep 30; touch \"$MARKER\""], sandbox.Cwd, UnixEnvironment(("MARKER", marker)),
                    Path.Combine(sandbox.Root, $"spill-{attempt}.log"));
                await using var lease = await new PosixSpawnProcessAdmission().LaunchAsync(request, CancellationToken.None);
                await Task.Delay(50 + attempt * 10);
                Equal(true, await lease.StopGroupAsync(), $"attempt {attempt}: the group is stopped and gone");
                await lease.Exit.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(100);
                Check(!File.Exists(marker), $"attempt {attempt}: the shell ran its next command");
            }
        })
    ];

    private static async Task<string> Grep(string rg, string cwd, string arguments, params (string Name, string Value)[] extra)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.Ordinal);
        foreach (var (name, value) in extra) environment[name] = value;
        var tool = new PiGrepTool(cwd, cwd, _ => ValueTask.FromResult<string?>(rg), environment.ToImmutableDictionary(StringComparer.Ordinal));
        var call = new ToolCallContent("grep-call", "grep", JsonData.Parse(arguments));
        var invocation = new ToolInvocation(new("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
        var result = await new ToolInvoker([tool], new AllowEverything()).ExecuteAsync(invocation, CancellationToken.None);
        return string.Concat(result.Content.Select(content => content.Text));
    }

    private sealed class AllowEverything : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }
}
