// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/core/extensions/loader.ts (createExtensionRuntime
// reportError, createExtensionAPI events.on reportingHandler), packages/coding-agent/src/core/extensions/runner.ts (the runner's
// runtime.reportError), packages/coding-agent/src/core/extensions/types.ts (ExtensionRuntimeState.reportError),
// packages/coding-agent/docs/extensions.md and packages/coding-agent/test/suite/regressions/11264-pi-events-handler-errors.test.ts.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Extensions;

// Pi 1.6.0 sync, #11264: an exception thrown by an extension's pi.events handler was printed with console.error over the interactive
// TUI. It is reported like other extension errors (event "events:<channel>"): extension_error in RPC mode (the record the
// interactive mode shows in the chat) and "Extension error (...)" in print mode; a handler that fails after its runtime was replaced
// is dropped. The cases cover both extension hosts: TypeScript extensions in the Node bridge and native C# extensions. Expectations
// are authored from the pinned sources, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160EventBusCases() =>
    [
        ("sync160.pi-events-handler-error-reaches-the-extension-error-listener-not-stderr", NodeEventHandlerErrorReported),
        ("sync160.pi-events-handler-error-after-the-runtime-was-replaced-is-dropped", NodeEventHandlerErrorDroppedWhenStale),
        ("sync160.pi-events-handler-error-is-an-extension-error-line-in-print-mode", NodeEventHandlerErrorPrintMode),
        ("sync160.pi-events-handler-error-is-an-extension-error-record-in-rpc-mode", NodeEventHandlerErrorRpcMode),
        ("sync160.native-events-handler-error-reaches-the-extension-error-listener", NativeEventHandlerErrorReported),
        ("sync160.native-events-handler-error-after-the-runtime-was-replaced-is-dropped", NativeEventHandlerErrorDroppedWhenStale),
        ("sync160.native-events-handler-error-is-an-extension-error-line-in-print-mode", NativeEventHandlerErrorPrintMode),
        ("sync160.native-events-handler-error-is-an-extension-error-record-in-rpc-mode", NativeEventHandlerErrorRpcMode),
    ];

    // The upstream test's factory: a handler that throws at once and one that fails later, when released. A handler of a replaced
    // runtime has no live subscription to be released through, so the late one waits for a file.
    private const string Sync160BoomExtension = Probe + """
        import { existsSync } from "node:fs";
        export default function (pi: any) {
          pi.events.on("test:boom", () => { throw new Error("handler failed"); });
          pi.events.on("test:late", async () => {
            log("late entered");
            while (!existsSync(process.cwd() + "/release")) await new Promise((resolve) => setTimeout(resolve, 20));
            log("late throwing");
            throw new Error("late failure");
          });
          pi.registerCommand("boom", { description: "Emit test:boom", handler: async () => {
            pi.events.emit("test:boom", undefined);
            await new Promise((resolve) => setTimeout(resolve, 100));
          } });
        }
        """;

    private sealed record Sync160ExtensionError(string Path, string Event, string Error);

    private static async Task<PiExtensionHost> Sync160StartHost(Sandbox sandbox, string[] extensions, List<string> stderr, List<Sync160ExtensionError> errors, bool node)
    {
        var options = new PiExtensionHostOptions(sandbox.Cwd, sandbox.AgentDir, "tui", true)
        {
            GetEnvironment = name => sandbox.Vars.GetValueOrDefault(name), PiModules = sandbox.PiModules,
            UseCompatibilityModules = sandbox.UseCompatibilityModules, StandardError = line => { lock (stderr) stderr.Add(line); }
        };
        var host = node ? await PiExtensionHost.StartAsync(options, CancellationToken.None) : PiExtensionHost.CreateWithoutNode(options);
        // The session host's emitError listener (bindExtensions onError).
        host.ReportError = (path, eventName, error) => { lock (errors) errors.Add(new(path, eventName, error)); return ValueTask.CompletedTask; };
        await host.LoadAsync(extensions, CancellationToken.None);
        Check(host.Errors.IsEmpty, "load errors: " + string.Join("; ", host.Errors.Select(error => error.Error)));
        return host;
    }

    private static Sync160ExtensionError[] Sync160Errors(List<Sync160ExtensionError> errors) { lock (errors) return [.. errors]; }
    private static string Sync160Stderr(List<string> stderr) { lock (stderr) return string.Join("\n", stderr); }

    // "reports a throwing handler to the extension error listener instead of stderr".
    private static async Task NodeEventHandlerErrorReported()
    {
        using var sandbox = NodeSandbox("sync160-events-reported");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "boom.ts"), Sync160BoomExtension);
        var stderr = new List<string>(); var errors = new List<Sync160ExtensionError>();
        await using var host = await Sync160StartHost(sandbox, [extension], stderr, errors, node: true);
        await using var activation = await NativeExtensionActivation.LoadPiAsync(host, CancellationToken.None);
        activation.Registry.SharedEventBus.Emit("test:boom", null);
        await WaitUntil(() => Sync160Errors(errors).Length > 0);
        // A request made after the error was sent is answered after it, so the Node host's stderr line would have been written by now.
        await host.CallAsync("flags.set", new JsonObject { ["values"] = new JsonObject() }, CancellationToken.None);
        await Task.Delay(200);
        Names(["events:test:boom|handler failed"], Sync160Errors(errors).Select(error => error.Event + "|" + error.Error), "the reported extension error");
        Equal(extension, Sync160Errors(errors).Single().Path, "extensionPath is the extension's path as loaded");
        Check(!Sync160Stderr(stderr).Contains("Event handler error", StringComparison.Ordinal) && !Sync160Stderr(stderr).Contains("handler failed", StringComparison.Ordinal),
            "nothing was printed with console.error: " + Sync160Stderr(stderr));
    }

    // "drops an error from a handler that fails after its session was disposed". The extension runtime is invalidated when it is
    // replaced (agent-session.ts reload: the old runner is invalidated); the error of the old runtime's handler, which fails after
    // that, never arrives. Notifications are handled in order, so the new runtime's error arriving alone proves the drop.
    private static async Task NodeEventHandlerErrorDroppedWhenStale()
    {
        using var sandbox = NodeSandbox("sync160-events-stale");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "boom.ts"), Sync160BoomExtension);
        var stderr = new List<string>(); var errors = new List<Sync160ExtensionError>();
        await using var host = await Sync160StartHost(sandbox, [extension], stderr, errors, node: true);
        await using var activation = await NativeExtensionActivation.LoadPiAsync(host, CancellationToken.None);
        activation.Registry.SharedEventBus.Emit("test:late", null);
        await WaitUntil(() => LogLines(sandbox).Contains("""["late entered"]"""));
        var reloadErrors = await host.ReloadExtensionsAsync(CancellationToken.None);
        Check(reloadErrors.IsEmpty, "reload errors: " + string.Join("; ", reloadErrors.Select(error => error.Error)));
        File.WriteAllText(Path.Combine(sandbox.Cwd, "release"), "");
        await WaitUntil(() => LogLines(sandbox).Contains("""["late throwing"]"""));
        // The replacement runtime's handler fails next; its error follows whatever the old runtime sent.
        activation.Registry.SharedEventBus.Emit("test:boom", null);
        await WaitUntil(() => Sync160Errors(errors).Length > 0);
        await Task.Delay(200);
        Names(["events:test:boom|handler failed"], Sync160Errors(errors).Select(error => error.Event + "|" + error.Error),
            "only the current runtime's handler error is reported");
        Equal(1, LogLines(sandbox).Count(line => line == """["late throwing"]"""), "the old runtime's handler failed once, after the replacement");
        Check(!Sync160Stderr(stderr).Contains("late failure", StringComparison.Ordinal) && !Sync160Stderr(stderr).Contains("Event handler error", StringComparison.Ordinal),
            "the dropped error was not printed either: " + Sync160Stderr(stderr));
    }

    // print-mode.ts bindExtensions onError: "Extension error (<path>): <error>" on stderr.
    private static async Task NodeEventHandlerErrorPrintMode()
    {
        using var sandbox = NodeSandbox("sync160-events-print");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "boom.ts"), Sync160BoomExtension);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/boom"]);
        Equal(0, code, "exit; " + stderr);
        var lines = stderr.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Contains("handler failed", StringComparison.Ordinal)).ToArray();
        Check(lines.Length == 1 && Regex.IsMatch(lines[0], @"^Extension error \(.*boom\.ts\): handler failed$"), "one extension error line: " + stderr);
        Check(!stderr.Contains("Event handler error", StringComparison.Ordinal), "no console.error diagnostic: " + stderr);
        Equal(0, sandbox.Requests.Count, "the command was handled without a model request");
    }

    // rpc-mode.ts bindExtensions onError: { type: "extension_error", extensionPath, event, error }. The interactive mode shows this
    // record in the chat (InteractiveMode.Events: extension_error -> ShowExtensionError).
    private static async Task NodeEventHandlerErrorRpcMode()
    {
        using var sandbox = NodeSandbox("sync160-events-rpc");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "boom.ts"), Sync160BoomExtension);
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"b","type":"prompt","message":"/boom"}"""],
            (_, all) => all.Any(record => IsResponse(record, "b")) && all.Any(record => record["type"]?.GetValue<string>() == "extension_error"));
        Equal(0, code, "exit; " + stderr);
        var error = records.Single(record => record["type"]?.GetValue<string>() == "extension_error");
        Equal("events:test:boom", error["event"]?.GetValue<string>(), "event");
        Equal("handler failed", error["error"]?.GetValue<string>(), "error");
        Check(error["extensionPath"]?.GetValue<string>()?.EndsWith("boom.ts", StringComparison.Ordinal) == true, "extensionPath: " + error.ToJsonString());
        Check(!stderr.Contains("Event handler error", StringComparison.Ordinal) && !stderr.Contains("handler failed", StringComparison.Ordinal), "nothing on stderr: " + stderr);
    }

    private static Sandbox Sync160NativeSandbox(string name, out string manifest)
    {
        var sandbox = NativeSandbox(name);
        manifest = NativeExtensionFolder(Path.Combine(sandbox.Root, "native-boom"), typeof(Sync160NativeBoomExtension));
        return sandbox;
    }

    // The native extensions' bus is the registry's (RegistrationScope.Events): a failed listener is an extension error of its
    // extension, under the manifest path, not a console line.
    private static async Task NativeEventHandlerErrorReported()
    {
        using var sandbox = Sync160NativeSandbox("sync160-native-events-reported", out var manifest);
        var stderr = new List<string>(); var errors = new List<Sync160ExtensionError>();
        var previous = Console.Error; using var console = new StringWriter();
        Console.SetError(console);
        try
        {
            await using var host = await Sync160StartHost(sandbox, [manifest], stderr, errors, node: false);
            await using var activation = await NativeExtensionActivation.LoadPiAsync(host, CancellationToken.None);
            activation.Registry.SharedEventBus.Emit("sync160:boom", null);
            Names(["events:sync160:boom|native handler failed"], Sync160Errors(errors).Select(error => error.Event + "|" + error.Error), "the reported extension error");
            Check(PiSharp.Cli.Pi.PiPaths.Comparer.Equals(Path.GetFullPath(manifest), Sync160Errors(errors).Single().Path), "extensionPath is the manifest: " + Sync160Errors(errors).Single().Path);
            Check(NativeLog(sandbox).Contains("after boom"), "a later listener of the channel still ran: " + string.Join("|", NativeLog(sandbox)));
        }
        finally { Console.SetError(previous); }
        Check(!console.ToString().Contains("Event handler error", StringComparison.Ordinal), "nothing was written to the console: " + console);
    }

    // The native counterpart of the runner's invalidation: a reload replaces the runtime (the previous owners withdraw their
    // registrations); a listener of the previous generation that fails afterwards is not reported.
    private static async Task NativeEventHandlerErrorDroppedWhenStale()
    {
        using var sandbox = Sync160NativeSandbox("sync160-native-events-stale", out var manifest);
        var stderr = new List<string>(); var errors = new List<Sync160ExtensionError>();
        await using var host = await Sync160StartHost(sandbox, [manifest], stderr, errors, node: false);
        await using var activation = await NativeExtensionActivation.LoadPiAsync(host, CancellationToken.None);
        activation.Registry.SharedEventBus.Emit("sync160:late", null);
        await WaitUntil(() => NativeLog(sandbox).Contains("late entered"));
        var reloadErrors = await host.ReloadExtensionsAsync(CancellationToken.None);
        Check(reloadErrors.IsEmpty, "reload errors: " + string.Join("; ", reloadErrors.Select(error => error.Error)));
        File.WriteAllText(Path.Combine(sandbox.Root, "native.log.release"), "");
        await WaitUntil(() => NativeLog(sandbox).Contains("late throwing"));
        await activation.Registry.SharedEventBus.DrainAsync();
        Equal(0, Sync160Errors(errors).Length, "the previous generation's late failure is dropped");
        // The replacement generation reports its own failures.
        activation.Registry.SharedEventBus.Emit("sync160:boom", null);
        Names(["events:sync160:boom|native handler failed"], Sync160Errors(errors).Select(error => error.Event + "|" + error.Error), "only the current generation's error");
        Equal(1, NativeLog(sandbox).Count(line => line == "late throwing"), "the previous generation's listener failed once");
    }

    private static async Task NativeEventHandlerErrorPrintMode()
    {
        using var sandbox = Sync160NativeSandbox("sync160-native-events-print", out var manifest);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", manifest, "/sync160-boom"]);
        Equal(0, code, "exit; " + stderr);
        var lines = stderr.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Contains("native handler failed", StringComparison.Ordinal)).ToArray();
        Check(lines.Length == 1 && Regex.IsMatch(lines[0], @"^Extension error \(.*pisharp-extension\.json\): native handler failed$"), "one extension error line: " + stderr);
        Check(!stderr.Contains("Event handler error", StringComparison.Ordinal), "no console diagnostic: " + stderr);
        Check(NativeLog(sandbox).Contains("command boom") && NativeLog(sandbox).Contains("after boom"), "the command and the later listener ran: " + string.Join("|", NativeLog(sandbox)));
    }

    private static async Task NativeEventHandlerErrorRpcMode()
    {
        using var sandbox = Sync160NativeSandbox("sync160-native-events-rpc", out var manifest);
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", manifest], ["""{"id":"b","type":"prompt","message":"/sync160-boom"}"""],
            (_, all) => all.Any(record => IsResponse(record, "b")) && all.Any(record => record["type"]?.GetValue<string>() == "extension_error"));
        Equal(0, code, "exit; " + stderr);
        var error = records.Single(record => record["type"]?.GetValue<string>() == "extension_error");
        Equal("events:sync160:boom", error["event"]?.GetValue<string>(), "event");
        Equal("native handler failed", error["error"]?.GetValue<string>(), "error");
        Check(error["extensionPath"]?.GetValue<string>()?.EndsWith("pisharp-extension.json", StringComparison.Ordinal) == true, "extensionPath: " + error.ToJsonString());
        Check(!stderr.Contains("Event handler error", StringComparison.Ordinal) && !stderr.Contains("native handler failed", StringComparison.Ordinal), "nothing on stderr: " + stderr);
    }
}

/// <summary>The native C# extension of the #11264 cases (loaded from a copy of this assembly in its own load context): a listener
/// that throws at once, a later listener of the same channel, an asynchronous listener that fails once a release file exists, and a
/// command that emits the failing channel.</summary>
public sealed class Sync160NativeBoomExtension : IPiSharpExtension
{
    private static string? LogPath => Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_LOG");
    private static void Log(string line) { if (LogPath is { } path) File.AppendAllText(path, line + "\n"); }

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        var bus = ((IExtensionEventBusRegistry)registry).Events;
        bus.On("sync160:boom", _ => throw new InvalidOperationException("native handler failed"));
        bus.On("sync160:boom", _ => Log("after boom"));
        ((IAsyncExtensionEventBus)bus).OnAsync("sync160:late", async _ =>
        {
            Log("late entered");
            while (LogPath is { } path && !File.Exists(path + ".release")) await Task.Delay(20);
            Log("late throwing");
            throw new InvalidOperationException("late failure");
        });
        registry.RegisterCommand(new("sync160-boom", "sync160-boom", "Emit sync160:boom", (arguments, context, token) =>
        {
            Log("command boom");
            bus.Emit("sync160:boom", null);
            return ValueTask.CompletedTask;
        }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
