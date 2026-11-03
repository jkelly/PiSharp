using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions.Execution;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Extensions.Runtime;
using PiSharp.Tools.Processes;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/exec.ts (execCommand: maxBuffer and
// truncated, SIGKILL after SIGTERM, UTF-8 decoding across chunks, spawn errors in stderr) through the Node bridge's pi.exec and native
// pi.exec (NativeExtensionExecHost over the platform's separated process runner).
// Expectations are written from the sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.bridge-exec-max-buffer-utf8-and-spawn-errors", BridgeExecLimits),
        ("sync150.native-exec-max-buffer-truncates-stops-and-spawn-errors", NativeExecLimits),
    ];

    private const string ExecExtension = """
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerTool({
            name: "execs", label: "Execs", description: "Runs pi.exec", parameters: { type: "object", properties: {} },
            async execute() {
              const run = (script: string, options?: any) => pi.exec(process.execPath, ["-e", script], options);
              const rejected: string[] = [];
              for (const maxBuffer of [-1, 0.5, Number.NaN, Number.POSITIVE_INFINITY]) {
                try { await pi.exec("pi-sync150-missing-command", [], { maxBuffer }); rejected.push("resolved"); }
                catch (error: any) { rejected.push(error?.name ?? String(error)); }
              }
              const records: any = {
                rejected,
                missing: await pi.exec("pi-sync150-missing-command", []),
                silent: await run("process.exit(1)"),
                zero: await run("process.stdout.write('x')", { maxBuffer: 0 }),
                exact: await run("process.stdout.write('a'.repeat(1000))", { maxBuffer: 1000 }),
                cut: await run("process.stdout.write('a€🙂')", { maxBuffer: 5 }),
                cutStderr: await run("process.stderr.write('€')", { maxBuffer: 1 }),
                split: await run("process.stdout.write(Buffer.from([0xe2, 0x82])); setTimeout(() => process.stdout.write(Buffer.from([0xac])), 200)"),
                partialAtEof: await run("process.stdout.write(Buffer.from([0xe2]))"),
              };
              const flood = await run("process.stderr.write('e'.repeat(600)); process.stdout.write('o'.repeat(600)); setInterval(() => process.stdout.write('x'.repeat(1000)), 10)", { maxBuffer: 1000 });
              records.flood = { bytes: Buffer.byteLength(flood.stdout) + Buffer.byteLength(flood.stderr), code: flood.code, killed: flood.killed, truncated: flood.truncated };
              if (process.platform !== "win32") {
                const began = Date.now();
                const stubborn = await run("process.on('SIGTERM', () => {}); process.stdout.write('xx'); setInterval(() => {}, 1000)", { maxBuffer: 1 });
                records.stubborn = { ...stubborn, elapsed: Date.now() - began };
              }
              log("exec", records);
              return { content: [{ type: "text", text: "execs ok" }] };
            },
          });
        }
        """;

    // exec.ts execCommand through the bridge's pi.exec: an invalid maxBuffer rejects with a RangeError before anything runs; a command
    // that cannot start resolves with its error in stderr and code 1; the combined stdout/stderr cap stops the command and reports
    // truncated, killed and a nonzero code; a cut drops an incomplete UTF-8 suffix, while a character split across chunks decodes whole
    // and a partial character at a normal EOF flushes as U+FFFD; a child that ignores SIGTERM is killed with SIGKILL after five seconds.
    private static async Task BridgeExecLimits()
    {
        using var sandbox = NodeSandbox("exec-limits");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "execs.ts"), ExecExtension);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("execs", new { }) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "run the execs"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text; stderr " + stderr + "; log " + string.Join(" | ", LogLines(sandbox)));
        var records = LogRecords(sandbox, "exec").Single()[1]!.AsObject();
        Equal("""["RangeError","RangeError","RangeError","RangeError"]""", records["rejected"]!.ToJsonString(), "invalid maxBuffer rejects");
        var missing = records["missing"]!;
        Check(missing["stderr"]!.GetValue<string>().Contains("ENOENT", StringComparison.Ordinal), "spawn error in stderr: " + missing.ToJsonString());
        Equal("""{"stdout":"","code":1,"killed":false,"truncated":false}""", Without(missing, "stderr"), "spawn error result");
        Equal("""{"stdout":"","stderr":"","code":1,"killed":false,"truncated":false}""", records["silent"]!.ToJsonString(), "a silent failure keeps stderr empty");
        Equal("""{"stdout":"","stderr":"","code":1,"killed":true,"truncated":true}""", records["zero"]!.ToJsonString(), "maxBuffer 0 truncates everything");
        Equal("{\"stdout\":\"" + new string('a', 1000) + "\",\"stderr\":\"\",\"code\":0,\"killed\":false,\"truncated\":false}",
            records["exact"]!.ToJsonString(), "output up to maxBuffer is unchanged");
        Equal("a€", records["cut"]!["stdout"]!.GetValue<string>(), "a cut keeps whole characters only");
        Equal("""{"code":1,"killed":true,"truncated":true}""", Without(records["cut"]!, "stdout", "stderr"), "cut result");
        Equal("""{"stdout":"","stderr":"","code":1,"killed":true,"truncated":true}""", records["cutStderr"]!.ToJsonString(), "a stderr cut inside a character");
        Equal("€", records["split"]!["stdout"]!.GetValue<string>(), "a character split across chunks");
        Equal(false, records["split"]!["truncated"]!.GetValue<bool>(), "split is not truncated");
        Equal("�", records["partialAtEof"]!["stdout"]!.GetValue<string>(), "a partial character at a normal EOF");
        Equal("""{"bytes":1000,"code":1,"killed":true,"truncated":true}""", records["flood"]!.ToJsonString(), "the combined cap stops a writing command");
        if (!OperatingSystem.IsWindows())
        {
            var stubborn = records["stubborn"]!;
            Equal("""{"stdout":"x","stderr":"","killed":true,"truncated":true}""", Without(stubborn, "code", "elapsed"), "SIGTERM-ignoring child");
            Check(stubborn["code"]!.GetValue<int>() != 0 && stubborn["elapsed"]!.GetValue<double>() >= 4500, "SIGKILL after the delay: " + stubborn.ToJsonString());
        }
    }

    private sealed class ExecOwner : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // exec.ts execCommand through native pi.exec: maxBuffer is validated before admission, and the default is 256 MiB; output over the
    // limit stops the real child and reads truncated, killed and nonzero, keeping whole UTF-8 characters; an executable that cannot start
    // resolves with "spawn <command> ENOENT" in stderr and code 1 instead of throwing.
    private static async Task NativeExecLimits()
    {
        RequireNode();
        var node = Tool("node", "-p process.execPath") ?? throw new SkipException("NO_NODE", "Node.js is not on PATH.");
        using var sandbox = new Sandbox("native-exec");
        var spill = Path.Combine(sandbox.Root, "spill"); Directory.CreateDirectory(spill);
        var environment = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { } root
            ? ImmutableDictionary<string, string>.Empty.Add("SystemRoot", root) : ImmutableDictionary<string, string>.Empty;
        var requests = new List<ProcessRequest>();
        var registry = new ExtensionRegistry(); IExtensionExecFacade? facade = null;
        var install = new NativeExtensionExecInstallation(registry, new LimitObservingRunner(OfflineSessionProfile.PiProcessRunner(), requests),
            (invocation, _) => ValueTask.FromResult(new ProcessRequest(invocation.Command == "node" ? node : Path.Combine(sandbox.Root, "missing-" + invocation.Command),
                invocation.Arguments, invocation.WorkingDirectory, environment, Path.Combine(spill, Guid.NewGuid().ToString("N") + ".spill"), invocation.TimeoutSeconds)),
            sandbox.Cwd, (_, value) => facade = value);
        try
        {
            await registry.ActivateAsync("sync150-native-exec", install.Decorate(registry, new ExecOwner()));
            Task<ExtensionExecResult> Run(string script, long? maxBuffer = null) =>
                facade!.ExecAsync("node", ["-e", script], new() { MaxBuffer = maxBuffer, TimeoutMilliseconds = 60_000 });
            foreach (var invalid in new long[] { -1, 0x3FFFFFDF + 1L })
            {
                var refused = await Rejects<ArgumentOutOfRangeException>(() => Run("", invalid), "maxBuffer " + invalid);
                Check(refused.Message.StartsWith("maxBuffer must be an integer between 0 and 1073741791 bytes", StringComparison.Ordinal), refused.Message);
            }
            Equal(0, requests.Count, "an invalid maxBuffer never reaches the runner");
            var plain = await Run("process.stdout.write('a'.repeat(1000))");
            Equal(256L * 1024 * 1024, requests[^1].CaptureLimitBytes, "default maxBuffer");
            Equal((new string('a', 1000), "", 0, false, false), (plain.Stdout, plain.Stderr, plain.Code, plain.Killed, plain.Truncated), "output within the default");
            var exact = await Run("process.stdout.write('a'.repeat(1000))", 1000);
            Equal((1000, false, false, 0), (exact.Stdout.Length, exact.Truncated, exact.Killed, exact.Code), "output up to maxBuffer is unchanged");
            var flood = await Run("process.stderr.write('e'.repeat(600)); process.stdout.write('o'.repeat(600)); setInterval(() => process.stdout.write('x'.repeat(1000)), 10)", 1000);
            Equal((1000, true, true), (flood.Stdout.Length + flood.Stderr.Length, flood.Truncated, flood.Killed), "the combined cap stops a writing command");
            Check(flood.Code != 0, "a truncated result is never a success: " + flood.Code);
            var cut = await Run("process.stdout.write('a€🙂')", 5);
            Equal(("a€", true, true), (cut.Stdout, cut.Truncated, cut.Killed), "a cut keeps whole characters only");
            Check(cut.Code != 0, "cut code " + cut.Code);
            var zero = await Run("process.stdout.write('x')", 0);
            Equal(("", "", true, true), (zero.Stdout, zero.Stderr, zero.Truncated, zero.Killed), "maxBuffer 0 truncates everything");
            Check(zero.Code != 0, "zero code " + zero.Code);
            var missing = await facade!.ExecAsync("pi-sync150-missing", []);
            Equal(("", "spawn pi-sync150-missing ENOENT", 1, false, false), (missing.Stdout, missing.Stderr, missing.Code, missing.Killed, missing.Truncated),
                "an executable that cannot start");
        }
        finally { await registry.DisposeAsync(); }
    }

    private static async Task<T> Rejects<T>(Func<Task> run, string what) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    private sealed class LimitObservingRunner(ISeparatedProcessRunner inner, List<ProcessRequest> requests) : ISeparatedProcessRunner
    {
        public ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { lock (requests) requests.Add(request); return inner.RunSeparatedAsync(request, cancellationToken); }
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default)
            => inner.RunAsync(request, onUpdate, cancellationToken);
    }

    private static string Without(JsonNode node, params string[] names)
    {
        var copy = node.DeepClone().AsObject();
        foreach (var name in names) copy.Remove(name);
        return copy.ToJsonString();
    }
}
