using System.Collections.Immutable;
using System.Runtime.InteropServices;
using PiSharp.Cli.Pi;
using PiSharp.Tools.Processes;
using PiSharp.Tools.Processes.Unix;

// Linux and macOS only (skipped on Windows): the posix_spawn process admission behind IUnixProcessAdmission, as core/bash-executor.ts
// and utils/shell.ts run commands (a detached process group, the whole tree killed on abort and timeout), through the bash tool and
// the RPC bash command (user_bash) of the Pi entry.
internal static partial class Program
{
    private static class Libc
    {
        [DllImport("libc", SetLastError = true)] internal static extern int getpgid(int pid);
        [DllImport("libc", SetLastError = true)] internal static extern int kill(int pid, int signal);
    }

    private static bool Alive(int pid) => Libc.kill(pid, 0) == 0;

    private static async Task<bool> GoneWithin(int pid, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (Alive(pid)) { if (DateTime.UtcNow > deadline) return false; await Task.Delay(25); }
        return true;
    }

    private static ImmutableDictionary<string, string> UnixEnvironment(params (string, string)[] extra) =>
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, [new("PATH", Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            .. extra.Select(pair => new KeyValuePair<string, string>(pair.Item1, pair.Item2))]);

    private static IEnumerable<(string, Func<Task>)> UnixProcessCases() =>
    [
        ("unix.posix-spawn-own-process-group-cwd-environment-and-stdio-pipes", async () =>
        {
            UnixOnly();
            using var sandbox = new Sandbox("unix-spawn");
            var request = new ProcessRequest("/bin/sh", ["-c", "sleep 0.3; echo \"out:$MARKER:$(pwd)\"; echo err >&2; read line; echo \"in:$line\"; exit 7"],
                sandbox.Cwd, UnixEnvironment(("MARKER", "m1")), Path.Combine(sandbox.Root, "spill.log")) { StandardInput = "piped-in\n"u8.ToArray() };
            await using var lease = await new PosixSpawnProcessAdmission().LaunchAsync(request, CancellationToken.None);
            var group = Libc.getpgid(lease.ProcessId);
            Check(group > 0 && group != Libc.getpgid(0) && group != lease.ProcessId, $"own process group led by the anchor (group {group}, pid {lease.ProcessId})");
            var stdout = Task.Run(() => new StreamReader(lease.StandardOutput).ReadToEndAsync());
            var stderr = Task.Run(() => new StreamReader(lease.StandardError).ReadToEndAsync());
            Equal(7, await lease.Exit.WaitAsync(TimeSpan.FromSeconds(30)), "exit code");
            var cwd = Path.GetFullPath(sandbox.Cwd);
            var output = await stdout;
            Check(output.StartsWith("out:m1:", StringComparison.Ordinal) && output.Contains("in:piped-in\n", StringComparison.Ordinal) &&
                (output.Contains(cwd, StringComparison.Ordinal) || output.Contains(Path.GetFileName(cwd), StringComparison.Ordinal)), "stdout: " + output);
            Equal("err\n", await stderr, "stderr");
            Equal(true, await lease.StopGroupAsync(), "the group is stopped and confirmed gone after a natural exit");
        }),
        ("unix.runner-timeout-and-cancel-kill-the-whole-process-group", async () =>
        {
            UnixOnly();
            using var sandbox = new Sandbox("unix-kill");
            var runner = new UnixProcessRunner(new PosixSpawnProcessAdmission());
            // Timeout: the background sleep is a grandchild in the same group, so it dies with the shell.
            var timed = await runner.RunAsync(new ProcessRequest("/bin/sh", ["-c", "sleep 30 & echo $!; wait"], sandbox.Cwd, UnixEnvironment(),
                Path.Combine(sandbox.Root, "timeout.log"), TimeoutSeconds: 1));
            Equal(ProcessRunStatus.TimedOut, timed.Status, "timeout status");
            Check(timed.CleanupConfirmed, "group cleanup confirmed");
            var grandchild = int.Parse(timed.Output.Content.Trim().Split('\n')[0]);
            Check(await GoneWithin(grandchild, TimeSpan.FromSeconds(5)), "the background child is killed with the group on timeout");
            // Abort: cancellation after the first output.
            using var abort = new CancellationTokenSource();
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = runner.RunAsync(new ProcessRequest("/bin/sh", ["-c", "sleep 30 & echo $!; wait"], sandbox.Cwd, UnixEnvironment(),
                Path.Combine(sandbox.Root, "cancel.log")), snapshot =>
                {
                    if (snapshot.Content.Contains('\n')) started.TrySetResult(snapshot.Content);
                    return ValueTask.CompletedTask;
                }, abort.Token);
            var pid = int.Parse((await started.Task.WaitAsync(TimeSpan.FromSeconds(30))).Trim().Split('\n')[0]);
            Check(Alive(pid), "running before the abort");
            await abort.CancelAsync();
            var result = await canceled;
            Equal(ProcessRunStatus.Canceled, result.Status, "cancel status");
            Check(await GoneWithin(pid, TimeSpan.FromSeconds(5)), "the background child is killed with the group on abort");
        }),
        ("unix.pi-entry-bash-tool-runs-and-timeout-kills-the-tree", async () =>
        {
            UnixOnly();
            using var sandbox = new Sandbox("unix-bash");
            var pidFile = Path.Combine(sandbox.Root, "child.pid");
            sandbox.Respond = (_, index) => index switch
            {
                0 => AnthropicToolCall("bash", new { command = "echo hello-$((1+2)); pwd" }, "toolu_b1"),
                1 => AnthropicToolCall("bash", new { command = $"sleep 30 & echo $! > '{pidFile}'; wait", timeout = 1 }, "toolu_b2"),
                _ => AnthropicText("done")
            };
            var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "run");
            Equal(0, code, "exit; " + stderr);
            Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "bash"), "bash offered");
            var first = ToolResultText(sandbox.Requests[1]);
            Check(first.Contains("hello-3", StringComparison.Ordinal) && first.Contains(Path.GetFileName(sandbox.Cwd), StringComparison.Ordinal), "bash result in the cwd: " + first);
            var second = ToolResultText(sandbox.Requests[2]);
            Check(second.Contains("Command timed out after 1 seconds", StringComparison.Ordinal), "timeout result: " + second);
            Check(await GoneWithin(int.Parse(File.ReadAllText(pidFile).Trim()), TimeSpan.FromSeconds(5)), "the timed-out command's background child is killed");
        }),
        // Runs on every platform (Windows through the native process layer), so its response shape is checked here as well.
        ("tools.pi-entry-rpc-bash-command-runs-user-bash", async () =>
        {
            using var sandbox = new Sandbox("unix-user-bash");
            sandbox.Vars["PI_OFFLINE"] = "1";
            var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"1\",\"type\":\"bash\",\"command\":\"echo user-$((2+3)) && echo err-line >&2\"}\n"));
            var gate = new GatedInput(input);
            using var output = new SignalingStream("\"command\":\"bash\"", gate.Release);
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null, rpcInput: gate, rpcOutput: output) with { StdoutIsTty = false };
            Equal(0, await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None), "exit; " + stderr);
            var response = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n').First(line => line.Contains("\"command\":\"bash\"", StringComparison.Ordinal));
            Check(response.Contains("\"success\":true", StringComparison.Ordinal) && response.Contains("user-5", StringComparison.Ordinal) &&
                response.Contains("err-line", StringComparison.Ordinal) && response.Contains("\"exitCode\":0", StringComparison.Ordinal), "bash response: " + response);
        }),
    ];
}
