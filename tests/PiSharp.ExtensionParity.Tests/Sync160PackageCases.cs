// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/package-manager.test.ts ("captured command output").
// Authored from that test and from the v1.6.0 sources of #11264: core/package-manager.ts (PackageCommandOutput, runCommandCaptured,
// getNonInteractiveGitEnv, the install progress of resolve), core/resource-loader.ts (packageCommandOutput, onPackageProgress) and
// main.ts (packageCommandOutput, announcePackageInstalls). Expectations are written by reading, never captured from an upstream run.
// Commands are local scripts (Node.js, a fake git) or fake processes; nothing reaches a network.
using System.Diagnostics;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Pi;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160PackageCases() =>
    [
        ("sync160.package.captured-failure-reports-the-end-of-the-output (needs node)", Sync160CapturedFailure),
        ("sync160.package.captured-command-returns-while-a-background-process-holds-its-output (needs node)", Sync160CapturedBackgroundProcess),
        ("sync160.package.captured-git-cannot-prompt-and-names-the-fix", Sync160CapturedGitPrompt),
        ("sync160.package.resolve-announces-the-install", Sync160ResolveAnnouncesInstall),
        ("sync160.package.announcements-are-dim-start-messages-until-stopped", Sync(Sync160RunOutputAnnouncements)),
        ("sync160.package.interactive-captures-and-announces-until-the-tui-starts", Sync160InteractiveCapture),
        ("sync160.package.print-mode-and-package-commands-keep-live-output", Sync160LiveOutput),
        ("sync160.package.interactive-keeps-real-command-output-off-the-terminal (needs node)", Sync160InteractiveRealCommand),
    ];

    private static PiPackageProcesses Sync160Capturing(StringWriter? output = null, Dictionary<string, string?>? environment = null) => new()
    {
        CommandOutput = PiPackageCommandOutput.Capture, Output = output, ErrorOutput = output,
        Environment = environment ?? new Dictionary<string, string?>()
    };

    // "reports the end of a failing command's output in the error": stdout and stderr in arrival order after "failed with code N:";
    // nothing is written to the host's streams, standard input is closed, and only the last 8000 characters are kept.
    private static async Task Sync160CapturedFailure()
    {
        RequireNode();
        using var terminal = new StringWriter();
        var processes = Sync160Capturing(terminal);
        // The second write waits, so the two pipes are read in the order they were written.
        const string script = "process.stdout.write('npm out\\n'); setTimeout(() => { process.stderr.write('npm ERR! broken\\n'); setTimeout(() => process.exit(3), 50); }, 200)";
        var error = await ThrowsAsync<PiPackageException>(() => processes.RunAsync("node", ["-e", script], null, CancellationToken.None));
        Equal($"node -e {script} failed with code 3:\nnpm out\nnpm ERR! broken", error.Message, "failure message");
        Equal("", terminal.ToString(), "nothing reaches the host's streams");

        await processes.RunAsync("node", ["-e", "console.log('added 12 packages')"], null, CancellationToken.None);
        Equal("", terminal.ToString(), "a successful command is silent");

        var silent = await ThrowsAsync<PiPackageException>(() => processes.RunAsync("node", ["-e", "process.exit(4)"], null, CancellationToken.None));
        Equal("node -e process.exit(4) failed with code 4", silent.Message, "no output, no tail");

        const string flood = "process.stdout.write('x'.repeat(20000) + 'END', () => process.exit(1))";
        var tail = (await ThrowsAsync<PiPackageException>(() => processes.RunAsync("node", ["-e", flood], null, CancellationToken.None))).Message;
        var prefix = $"node -e {flood} failed with code 1:\n";
        Check(tail.StartsWith(prefix, StringComparison.Ordinal) && tail.EndsWith("xEND", StringComparison.Ordinal), "the end of the output is kept");
        Equal(PiPackageProcesses.CapturedOutputTailChars, tail.Length - prefix.Length, "tail length");

        // stdio "ignore": the command reads the end of its input at once instead of waiting on the terminal.
        const string stdin = "process.stdin.on('end', () => process.exit(0)); process.stdin.resume(); setTimeout(() => process.exit(9), 20000)";
        var started = Stopwatch.GetTimestamp();
        await processes.RunAsync("node", ["-e", stdin], null, CancellationToken.None);
        Check(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15), "standard input is closed");

        // Inherit (the default) still pumps the output to the host's streams and reports no tail.
        using var live = new StringWriter();
        var inherit = new PiPackageProcesses { Output = live, ErrorOutput = live };
        Equal(PiPackageCommandOutput.Inherit, inherit.CommandOutput, "default command output");
        var liveError = await ThrowsAsync<PiPackageException>(() => inherit.RunAsync("node", ["-e", "console.log('live out'); process.exit(2)"], null, CancellationToken.None));
        Equal("node -e console.log('live out'); process.exit(2) failed with code 2", liveError.Message, "inherit failure message");
        Check(live.ToString().Contains("live out", StringComparison.Ordinal), "inherit keeps live output");
    }

    // "returns when the command exits although a background process still holds its output".
    private static async Task Sync160CapturedBackgroundProcess()
    {
        RequireNode();
        const string script = "require('child_process').spawn(process.execPath, ['-e', 'setTimeout(() => {}, 5000)'], { stdio: 'inherit', detached: true }).unref()";
        var started = Stopwatch.GetTimestamp();
        await Sync160Capturing().RunAsync("node", ["-e", script], null, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);
        Check(elapsed < TimeSpan.FromMilliseconds(3000), $"returned after {elapsed.TotalMilliseconds:F0} ms");
    }

    // "stops git and ssh from prompting and names the fix when git could not ask for credentials" (upstream skips it on Windows; here
    // the fake git is a batch file there).
    private static async Task Sync160CapturedGitPrompt()
    {
        using var sandbox = new Sandbox("sync160-git-prompt");
        var bin = Path.Combine(sandbox.Root, "bin");
        foreach (var name in new[] { "git", "notgit" })
        {
            if (OperatingSystem.IsWindows())
                sandbox.Write(Path.Combine(bin, name + ".cmd"), "@echo off\r\necho prompt=%GIT_TERMINAL_PROMPT% ssh=%GIT_SSH_COMMAND%\r\n" +
                    "echo fatal: could not read Username for 'https://example.com': terminal prompts disabled 1>&2\r\nexit 128\r\n");
            else
            {
                var path = sandbox.Write(Path.Combine(bin, name), "#!/bin/sh\necho \"prompt=$GIT_TERMINAL_PROMPT ssh=$GIT_SSH_COMMAND\"\n" +
                    "echo \"fatal: could not read Username for 'https://example.com': terminal prompts disabled\" >&2\nexit 128\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        var searchPath = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        string[] args = ["clone", "https://example.com/private.git"];

        var error = await ThrowsAsync<PiPackageException>(() => Sync160Capturing(environment: new() { ["PATH"] = searchPath, ["GIT_SSH_COMMAND"] = null })
            .RunAsync("git", args, null, CancellationToken.None));
        Check(error.Message.StartsWith("git clone https://example.com/private.git failed with code 128:\n", StringComparison.Ordinal), "message: " + error.Message);
        Check(error.Message.Contains("prompt=0 ssh=ssh -o BatchMode=yes", StringComparison.Ordinal), "git and ssh cannot prompt: " + error.Message);
        Check(error.Message.Contains("Set up a git credential helper or an ssh agent", StringComparison.Ordinal), "the fix is named: " + error.Message);
        Check(error.Message.EndsWith("\n" + PiPackageProcesses.GitCredentialsHint, StringComparison.Ordinal) &&
            PiPackageProcesses.GitCredentialsHint.EndsWith("install the package once from the shell with `pisharp install <source>`.", StringComparison.Ordinal), "hint text");

        var custom = await ThrowsAsync<PiPackageException>(() => Sync160Capturing(environment: new() { ["PATH"] = searchPath, ["GIT_SSH_COMMAND"] = "ssh -i key" })
            .RunAsync("git", args, null, CancellationToken.None));
        Check(custom.Message.Contains("prompt=0 ssh=ssh -i key", StringComparison.Ordinal), "a user-defined GIT_SSH_COMMAND is kept: " + custom.Message);

        var other = await ThrowsAsync<PiPackageException>(() => Sync160Capturing(environment: new() { ["PATH"] = searchPath, ["GIT_SSH_COMMAND"] = null })
            .RunAsync("notgit", args, null, CancellationToken.None));
        Check(other.Message.Contains("terminal prompts disabled", StringComparison.Ordinal) && !other.Message.Contains("credential helper", StringComparison.Ordinal),
            "only git gets the hint: " + other.Message);
    }

    // "announces a package that resolve() installs", and the failure and temporary (-e) forms.
    private static async Task Sync160ResolveAnnouncesInstall()
    {
        using var sandbox = new Sandbox("sync160-resolve-progress");
        var events = new List<PiPackageProgressEvent>();
        var manager = Manager(sandbox, project: J("""{"packages":["npm:new-package"]}"""));
        manager.SetProgressCallback(events.Add);
        await manager.ResolveAsync();
        Check(events.Contains(new("start", "install", "npm:new-package", "Installing npm:new-package...")), "start event: " + string.Join(" | ", events));
        Check(events.Contains(new("complete", "install", "npm:new-package")), "complete event");

        events.Clear();
        var failing = Manager(sandbox, project: J("""{"packages":["npm:broken"]}"""),
            processes: new FakeProcesses { OnRun = (_, _, _) => throw new PiPackageException("npm install broken failed with code 1:\nnpm ERR! 404") });
        failing.SetProgressCallback(events.Add);
        var resolved = await failing.ResolveAsync();
        Names(["start", "error"], events.Select(item => item.Type), "failure events");
        Equal("npm install broken failed with code 1:\nnpm ERR! 404", events[1].Message, "error event message");
        Equal("npm install broken failed with code 1:\nnpm ERR! 404", resolved.InstallFailures.Single().Message, "the failure keeps the end of the output");

        events.Clear();
        var temporary = Manager(sandbox);
        temporary.SetProgressCallback(events.Add);
        await temporary.ResolveExtensionSourcesAsync(["npm:temp-package"], temporary: true);
        Check(events.Contains(new("start", "install", "npm:temp-package", "Installing npm:temp-package...")), "temporary -e package announced");

        // An installed package is not announced, and an offline run installs (and announces) nothing.
        events.Clear();
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "npm", "node_modules", "present", "package.json"), """{"name":"present","version":"1.0.0"}""");
        var present = Manager(sandbox, project: J("""{"packages":["npm:present"]}"""));
        present.SetProgressCallback(events.Add);
        await present.ResolveAsync();
        var offline = Manager(sandbox, project: J("""{"packages":["npm:new-package"]}"""), environment: new() { ["PI_OFFLINE"] = "1" });
        offline.SetProgressCallback(events.Add);
        await offline.ResolveAsync();
        Equal(0, events.Count, "no events: " + string.Join(" | ", events));
    }

    // main.ts onPackageProgress: chalk.dim(event.message) on stderr for start events while announcing.
    private static void Sync160RunOutputAnnouncements()
    {
        using var plain = new StringWriter { NewLine = "\n" };
        var run = new PiPackageRunOutput(PiPackageCommandOutput.Capture, plain);
        run.OnProgress(new("start", "install", "npm:a", "Installing npm:a..."));
        run.OnProgress(new("complete", "install", "npm:a"));
        run.OnProgress(new("error", "install", "npm:a", "failed"));
        run.OnProgress(new("progress", "update", "npm:a", "still 1.0.0"));
        run.OnProgress(new("start", "install", "npm:a", "Installing npm:a...")); // the same source resolved again before the session starts
        run.OnProgress(new("start", "pull", "git:github.com/a/b", "Refreshing git:github.com/a/b..."));
        run.OnProgress(new("start", "install", "npm:b"));
        Equal("Installing npm:a...\nRefreshing git:github.com/a/b...\n", plain.ToString(), "start messages, each once");
        run.StopAnnouncing();
        run.OnProgress(new("start", "install", "npm:late", "Installing npm:late..."));
        Equal("Installing npm:a...\nRefreshing git:github.com/a/b...\n", plain.ToString(), "nothing once the TUI started");

        using var colored = new StringWriter { NewLine = "\n" };
        new PiPackageRunOutput(PiPackageCommandOutput.Capture, colored, color: true).OnProgress(new("start", "install", "npm:a", "Installing npm:a..."));
        Equal("\u001b[2mInstalling npm:a...\u001b[22m\n", colored.ToString(), "dim when stderr takes color");

        using var inherit = new StringWriter();
        new PiPackageRunOutput(PiPackageCommandOutput.Inherit, inherit).OnProgress(new("start", "install", "npm:a", "Installing npm:a..."));
        PiPackageRunOutput.Inherit.OnProgress(new("start", "install", "npm:a", "Installing npm:a..."));
        Equal("", inherit.ToString(), "live output announces nothing");
    }

    private static FakeProcesses Sync160NpmInstalls(Sandbox sandbox, List<string> installed) => new()
    {
        OnRun = (_, args, _) =>
        {
            if (args[0] != "install") return Task.CompletedTask;
            var name = args[1];
            lock (installed) installed.Add(name);
            if (name == "broken") throw new PiPackageException("npm install broken failed with code 1:\nnpm ERR! 404");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "npm", "node_modules", name, "package.json"), Jstr(new { name, version = "1.0.0" }));
            return Task.CompletedTask;
        }
    };

    // main.ts: packageCommandOutput is "capture" for interactive mode; installs are announced on stderr until InteractiveMode starts
    // (announcePackageInstalls = false) and are silent on a later reload. resource-loader.ts hands both to the package manager.
    private static async Task Sync160InteractiveCapture()
    {
        using var sandbox = new Sandbox("sync160-interactive");
        var settingsPath = sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"packages":["npm:announced","npm:broken"]}""");
        var installed = new List<string>();
        var processes = Sync160NpmInstalls(sandbox, installed);
        var writers = new List<TextWriter>();
        using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
        string? atStart = null; PiEntryOptions? entry = null; var modes = new List<PiPackageCommandOutput>();
        var host = sandbox.Host(stdout, stderr, null, interactive: true, runInteractive: async (_, options, token) =>
        {
            entry = options; atStart = stderr.ToString(); modes.Add(processes.CommandOutput);
            // /reload after the TUI started: a package added meanwhile is installed without a line on the terminal.
            File.WriteAllText(settingsPath, """{"packages":["npm:announced","npm:broken","npm:late"]}""");
            await options.ReloadResources!(null, token);
            modes.Add(processes.CommandOutput);
            return 0;
        }) with { PackageProcesses = (output, error) => { lock (writers) { writers.Add(output); writers.Add(error); } return processes; } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(200));
        Equal(0, await PiCommand.RunAsync([.. Model], host, deadline.Token), "interactive run; " + stderr);
        Check(entry is not null && atStart is not null, "the interactive mode started");
        var lines = atStart!.Split('\n');
        Equal(1, lines.Count(line => line == "Installing npm:announced..."), "install announced once on stderr: " + atStart);
        Equal(1, lines.Count(line => line == "Installing npm:broken..."), "a failing install is announced once: " + atStart);
        Check(installed.Count(name => name == "broken") > 1, "although it was attempted by each resolve pass");
        Check(!stdout.ToString().Contains("Installing", StringComparison.Ordinal), "nothing on stdout: " + stdout);
        Names([nameof(PiPackageCommandOutput.Capture), nameof(PiPackageCommandOutput.Capture)], modes.Select(mode => mode.ToString()), "captured at startup and on reload");
        Check(writers.Count > 0 && writers.All(writer => ReferenceEquals(writer, TextWriter.Null)), "no terminal stream is handed to git or npm");
        Check(entry!.StartupDiagnostics.Any(diagnostic => diagnostic.Type == "warning" && diagnostic.Message ==
            "Extension package \"npm:broken\": failed to install (npm install broken failed with code 1:\nnpm ERR! 404). Run \"pisharp remove npm:broken --local\" to remove it from settings.json."),
            "the failed install reports the end of its output: " + string.Join(" | ", entry.StartupDiagnostics.Select(diagnostic => diagnostic.Message)));
        Check(installed.Contains("late"), "the reload installed the new package");
        Equal(atStart, stderr.ToString(), "no announcement once the TUI started");
        Equal("", stdout.ToString(), "stdout untouched");
    }

    // main.ts: the other modes use "inherit" (their stdout is taken over, so git and npm write to stderr) and never announce;
    // package-manager-cli.ts: pi install and pi update keep live output.
    private static async Task Sync160LiveOutput()
    {
        using var sandbox = new Sandbox("sync160-live");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"packages":["npm:announced"]}""");
        var installed = new List<string>();
        var processes = Sync160NpmInstalls(sandbox, installed);
        var writers = new List<TextWriter>();
        using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
        var host = sandbox.Host(stdout, stderr, null) with { PackageProcesses = (output, error) => { lock (writers) { writers.Add(output); writers.Add(error); } return processes; } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(200));
        Equal(0, await PiCommand.RunAsync(["-p", .. Model, "hi"], host, deadline.Token), "print run; " + stderr);
        Check(installed.Contains("announced"), "the package was installed");
        Equal(PiPackageCommandOutput.Inherit, processes.CommandOutput, "print mode keeps live output");
        Check(writers.Count > 0 && writers.All(writer => ReferenceEquals(writer, stderr)), "live output goes to stderr");
        Check(!stderr.ToString().Contains("Installing", StringComparison.Ordinal) && !stdout.ToString().Contains("Installing", StringComparison.Ordinal),
            "no announcement outside interactive mode: " + stderr);

        processes.CommandOutput = PiPackageCommandOutput.Capture; // a stale value must not survive
        writers.Clear(); stdout.GetStringBuilder().Clear(); stderr.GetStringBuilder().Clear();
        Equal(0, await PiCommand.RunAsync(["install", "npm:live", "-l"], host, deadline.Token), "install; " + stderr);
        Equal(PiPackageCommandOutput.Inherit, processes.CommandOutput, "pi install keeps live output");
        Check(ReferenceEquals(writers[0], stdout) && ReferenceEquals(writers[1], stderr), "the command's own streams");
        Check(stdout.ToString().Contains("Installing npm:live...", StringComparison.Ordinal), "the command prints its own progress: " + stdout);

        processes.CommandOutput = PiPackageCommandOutput.Capture;
        Equal(0, await PiCommand.RunAsync(["update", "--extensions"], host, deadline.Token), "update; " + stderr);
        Equal(PiPackageCommandOutput.Inherit, processes.CommandOutput, "pi update keeps live output");
    }

    // The same through the user's own tools: a failing npmCommand writes "added 12 packages" and an error. Interactive mode keeps both
    // off stdout and stderr and reports the end of the output in the warning; print mode shows them live on stderr.
    private static async Task Sync160InteractiveRealCommand()
    {
        RequireNode();
        using var sandbox = new Sandbox("sync160-real-command");
        var npm = sandbox.Write(Path.Combine(sandbox.Root, "fake-npm.js"),
            "process.stdout.write('added 12 packages\\n'); setTimeout(() => { process.stderr.write('npm ERR! broken\\n'); setTimeout(() => process.exit(1), 50); }, 200);");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), Jstr(new { packages = new[] { "npm:real-broken" }, npmCommand = new[] { "node", npm } }));
        using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
        PiEntryOptions? entry = null;
        var host = sandbox.Host(stdout, stderr, null, interactive: true, runInteractive: (_, options, _) => { entry = options; return Task.FromResult(0); });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(200));
        Equal(0, await PiCommand.RunAsync([.. Model], host, deadline.Token), "interactive run; " + stderr);
        Check(!stdout.ToString().Contains("added 12 packages", StringComparison.Ordinal) && !stderr.ToString().Contains("added 12 packages", StringComparison.Ordinal) &&
            !stderr.ToString().Contains("npm ERR!", StringComparison.Ordinal), "npm output stays off the terminal: " + stdout + stderr);
        Equal("Installing npm:real-broken...\n", stderr.ToString(), "only the announcement");
        var warning = entry!.StartupDiagnostics.Single(diagnostic => diagnostic.Message.StartsWith("Extension package \"npm:real-broken\"", StringComparison.Ordinal)).Message;
        Check(warning.Contains(" failed with code 1:\nadded 12 packages\nnpm ERR! broken). Run \"pisharp remove npm:real-broken --local\"", StringComparison.Ordinal),
            "the warning carries the end of the output: " + warning);

        stdout.GetStringBuilder().Clear(); stderr.GetStringBuilder().Clear();
        Equal(0, await PiCommand.RunAsync(["-p", .. Model, "hi"], sandbox.Host(stdout, stderr, null), deadline.Token), "print run; " + stderr);
        Check(stderr.ToString().Contains("added 12 packages", StringComparison.Ordinal) && stderr.ToString().Contains("npm ERR! broken", StringComparison.Ordinal),
            "print mode shows npm output live on stderr: " + stderr);
        Check(!stderr.ToString().Contains("Installing npm:real-broken", StringComparison.Ordinal), "and announces nothing");
        Check(stderr.ToString().Contains(" failed with code 1). Run \"pisharp remove npm:real-broken --local\"", StringComparison.Ordinal), "its warning has no tail: " + stderr);
        Equal("ok", stdout.ToString().Trim(), "print output stays clean");
    }
}
