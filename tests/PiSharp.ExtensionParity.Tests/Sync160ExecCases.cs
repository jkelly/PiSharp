// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/exec.test.ts ("signal-terminated commands", #12088)
// through the Node bridge's pi.exec and native pi.exec (NativeExtensionExecHost over the platform's separated process runner).
// Upstream skips these cases on Windows; here each platform's own result is asserted. Expectations are written from the v1.6.0
// sources by reading, never captured from an upstream run.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions.Execution;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Extensions.Runtime;
using PiSharp.Tools.Processes;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160ExecCases() =>
    [
        ("sync160.bridge-exec-signal-terminated-exit-codes", BridgeExecSignalCodes),
        ("sync160.native-exec-signal-terminated-exit-codes", NativeExecSignalCodes),
        ("sync160.native-exec-exit-code-mapping", NativeExecCodeMapping),
    ];

    private const string SignalExecExtension = """
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerTool({
            name: "signals", label: "Signals", description: "Runs pi.exec", parameters: { type: "object", properties: {} },
            async execute() {
              const idle = ["-e", "setInterval(() => {}, 1000)"];
              const controller = new AbortController();
              setTimeout(() => controller.abort(), 100);
              const records: any = {
                timeout: await pi.exec(process.execPath, idle, { timeout: 100 }),
                abort: await pi.exec(process.execPath, idle, { signal: controller.signal }),
                // Not killed by pi, so `killed` is false and only the code tells this apart from success.
                killedByOther: await pi.exec(process.execPath, ["-e", 'process.kill(process.pid, "SIGKILL")']),
              };
              if (process.platform !== "win32")
                records.terminatedByOther = await pi.exec(process.execPath, ["-e", 'process.kill(process.pid, "SIGTERM")']);
              log("signals", records);
              return { content: [{ type: "text", text: "signals ok" }] };
            },
          });
        }
        """;

    // exec.ts execCommand through the bridge's pi.exec: a command ended by a signal has no exit code and reports 128 + the signal
    // number instead of 0. A timeout and an abort send SIGTERM (143); on Windows Node ends the child with TerminateProcess and reports
    // the SIGTERM it was asked to send, so the code is 143 there too. A command killed by someone else reads 128 + that signal on
    // POSIX; on Windows it has the exit code TerminateProcess gave it (1) and no signal.
    private static async Task BridgeExecSignalCodes()
    {
        using var sandbox = NodeSandbox("exec-signals");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "signals.ts"), SignalExecExtension);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("signals", new { }) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "run the signals"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text; stderr " + stderr + "; log " + string.Join(" | ", LogLines(sandbox)));
        var records = LogRecords(sandbox, "signals").Single()[1]!.AsObject();
        const string stopped = """{"stdout":"","stderr":"","code":143,"killed":true,"truncated":false}""";
        Equal(stopped, records["timeout"]!.ToJsonString(), "a timeout reports 128 + SIGTERM");
        Equal(stopped, records["abort"]!.ToJsonString(), "an abort reports 128 + SIGTERM");
        if (OperatingSystem.IsWindows())
            Equal("""{"stdout":"","stderr":"","code":1,"killed":false,"truncated":false}""", records["killedByOther"]!.ToJsonString(),
                "Windows: a command terminated by someone else has an exit code and no signal");
        else
        {
            Equal("""{"stdout":"","stderr":"","code":137,"killed":false,"truncated":false}""", records["killedByOther"]!.ToJsonString(),
                "a command killed by someone else reports 128 + SIGKILL");
            Equal("""{"stdout":"","stderr":"","code":143,"killed":false,"truncated":false}""", records["terminatedByOther"]!.ToJsonString(),
                "a command terminated by someone else reports 128 + SIGTERM");
        }
    }

    // The same cases through native pi.exec. This host stops a command by ending its whole process tree at once: on Linux and macOS
    // the process group gets SIGKILL, so a stopped command reads 137 (Pi sends SIGTERM first: 143); on Windows the job is terminated,
    // which Pi reports as the SIGTERM it asked for, so a stopped command reads 143 as it does under Pi. A command ended by someone
    // else keeps what the platform recorded: 128 + the signal on POSIX, the exit code on Windows.
    private static async Task NativeExecSignalCodes()
    {
        RequireNode();
        var node = Tool("node", "-p process.execPath") ?? throw new SkipException("NO_NODE", "Node.js is not on PATH.");
        using var sandbox = new Sandbox("native-exec-signals");
        var spill = Path.Combine(sandbox.Root, "spill"); Directory.CreateDirectory(spill);
        var environment = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { } root
            ? ImmutableDictionary<string, string>.Empty.Add("SystemRoot", root) : ImmutableDictionary<string, string>.Empty;
        var registry = new ExtensionRegistry(); IExtensionExecFacade? facade = null;
        var install = new NativeExtensionExecInstallation(registry, OfflineSessionProfile.PiProcessRunner(),
            (invocation, _) => ValueTask.FromResult(new ProcessRequest(node, invocation.Arguments, invocation.WorkingDirectory, environment,
                Path.Combine(spill, Guid.NewGuid().ToString("N") + ".spill"), invocation.TimeoutSeconds)),
            sandbox.Cwd, (_, value) => facade = value);
        var stoppedCode = OperatingSystem.IsWindows() ? 143 : 137;
        try
        {
            await registry.ActivateAsync("sync160-native-exec", install.Decorate(registry, new ExecOwner()));
            ImmutableArray<string> idle = ["-e", "setInterval(() => {}, 1000)"];

            var timeout = await facade!.ExecAsync("node", idle, new() { TimeoutMilliseconds = 500 });
            Equal(("", "", stoppedCode, true, false), (timeout.Stdout, timeout.Stderr, timeout.Code, timeout.Killed, timeout.Truncated), "a timeout");

            using var abort = new CancellationTokenSource();
            var running = facade.ExecAsync("node", idle, new(Signal: abort.Token) { TimeoutMilliseconds = 60_000 });
            abort.CancelAfter(500);
            var aborted = await running;
            Equal(("", "", stoppedCode, true, false), (aborted.Stdout, aborted.Stderr, aborted.Code, aborted.Killed, aborted.Truncated), "an abort");

            var flood = await facade.ExecAsync("node", ["-e", "setInterval(() => process.stdout.write('x'.repeat(1000)), 10)"],
                new() { MaxBuffer = 1000, TimeoutMilliseconds = 60_000 });
            Equal((1000, stoppedCode, true, true), (flood.Stdout.Length, flood.Code, flood.Killed, flood.Truncated), "output over maxBuffer stops the command");

            // Not stopped by the host, so Killed is false and only the code tells this apart from success.
            var killed = await facade.ExecAsync("node", ["-e", "process.kill(process.pid, 'SIGKILL')"], new() { TimeoutMilliseconds = 60_000 });
            Equal(("", "", OperatingSystem.IsWindows() ? 1 : 137, false, false), (killed.Stdout, killed.Stderr, killed.Code, killed.Killed, killed.Truncated),
                "a command killed by someone else");
            if (!OperatingSystem.IsWindows())
            {
                var terminated = await facade.ExecAsync("node", ["-e", "process.kill(process.pid, 'SIGTERM')"], new() { TimeoutMilliseconds = 60_000 });
                Equal((143, false, false), (terminated.Code, terminated.Killed, terminated.Truncated), "a command terminated by someone else");
            }

            // An ordinary exit keeps its code on every platform, also the one a Windows stop would give.
            var one = await facade.ExecAsync("node", ["-e", "process.exit(1)"], new() { TimeoutMilliseconds = 60_000 });
            Equal((1, false, false), (one.Code, one.Killed, one.Truncated), "exit 1 is exit 1");
            var seven = await facade.ExecAsync("node", ["-e", "process.exit(7)"], new() { TimeoutMilliseconds = 60_000 });
            Equal((7, false, false), (seven.Code, seven.Killed, seven.Truncated), "exit 7");
        }
        finally { await registry.DisposeAsync(); }
    }

    // The receipt-to-code mapping of both platforms, without a process.
    private static Task NativeExecCodeMapping()
    {
        // Windows: a stop terminates the job with exit code 1, read as 128 + SIGTERM; any other code is the command's own.
        Equal(143, NativeExtensionExecHost.ExitCode(1, killed: true, windows: true), "Windows stop");
        Equal(143, NativeExtensionExecHost.ExitCode(null, killed: true, windows: true), "Windows stop without a recorded code");
        Equal(0, NativeExtensionExecHost.ExitCode(0, killed: true, windows: true), "Windows: the command ended before the stop took effect");
        Equal(7, NativeExtensionExecHost.ExitCode(7, killed: true, windows: true), "Windows: the command's own code before the stop");
        Equal(1, NativeExtensionExecHost.ExitCode(1, killed: true, windows: true, endedBeforeStop: true), "Windows: exit 1 seen before the timeout");
        Equal(1, NativeExtensionExecHost.ExitCode(1, killed: false, windows: true), "Windows: an ordinary exit 1");
        Equal(unchecked((int)0xC0000005), NativeExtensionExecHost.ExitCode(unchecked((int)0xC0000005), killed: false, windows: true), "Windows crash status");
        // Linux and macOS: the receipt already holds 128 + the signal waitpid reported.
        Equal(137, NativeExtensionExecHost.ExitCode(137, killed: true, windows: false), "POSIX stop (SIGKILL to the process group)");
        Equal(137, NativeExtensionExecHost.ExitCode(null, killed: true, windows: false), "POSIX stop without a recorded code");
        Equal(143, NativeExtensionExecHost.ExitCode(143, killed: false, windows: false), "POSIX SIGTERM from someone else");
        Equal(139, NativeExtensionExecHost.ExitCode(139, killed: false, windows: false), "POSIX crash (SIGSEGV)");
        Equal(0, NativeExtensionExecHost.ExitCode(0, killed: true, windows: false), "POSIX: the command ended before the stop took effect");
        Equal(1, NativeExtensionExecHost.ExitCode(1, killed: true, windows: false), "POSIX: exit 1 is never a stop code");
        // No stopped or crashed command reads as success.
        foreach (var windows in new[] { true, false })
            Check(NativeExtensionExecHost.ExitCode(null, killed: true, windows) != 0 && NativeExtensionExecHost.ExitCode(null, killed: false, windows) != 0,
                "a missing exit code is never 0");
        return Task.CompletedTask;
    }
}
