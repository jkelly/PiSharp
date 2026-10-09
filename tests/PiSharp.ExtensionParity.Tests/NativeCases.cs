using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Extensions;

// Owner decision 10: native C# extensions in the Pi entry are discovered like Pi extensions (global and project extension folders,
// settings, packages, -e), gated only by project trust, without approval or preflight documents, on every platform, and share one
// registry and session with the Node extensions. The extension below is this test assembly's own type: each case copies the assembly
// into an extension folder with a pisharp-extension.json manifest, and the run loads it into its own load context.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> NativeCases() =>
    [
        ("native.project-folder-extension-loads-when-trusted", NativeProjectTrusted),
        ("native.project-folder-extension-skipped-when-untrusted", NativeProjectUntrusted),
        ("native.cli-extension-runs-without-node", NativeWithoutNode),
        ("native.shares-the-session-with-node-extensions", NativeWithNode),
        ("native.reload-loads-native-extensions-again", NativeReload),
        ("native.command-named-like-another-extensions-becomes-name-n", NativeDuplicateCommand),
    ];

    // runner.ts resolveRegisteredCommands over every extension (Node and native, load order): a name two extensions register is
    // invoked as name:1 and name:2; neither extension fails to load.
    private static async Task NativeDuplicateCommand()
    {
        using var sandbox = NodeSandbox("native-duplicate-command");
        Environment.SetEnvironmentVariable(NativeLogVariable, Path.Combine(sandbox.Root, "native.log"));
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var node = sandbox.Write(Path.Combine(sandbox.Cwd, "node.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("native-hello", { description: "Node hello", handler: async (args: string) => log("node", args) });
              pi.registerCommand("list", { description: "List", handler: async () =>
                log("commands", pi.getCommands().filter((c: any) => c.source === "extension").map((c: any) => c.name)) });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", node, "/list"]);
        Equal(0, code, "exit; " + stderr);
        var commands = LogLines(sandbox).Single(line => line.StartsWith("[\"commands\"", StringComparison.Ordinal));
        Check(commands.Contains("\"native-hello:1\"", StringComparison.Ordinal) && commands.Contains("\"native-hello:2\"", StringComparison.Ordinal) &&
            !commands.Contains("\"native-hello\"", StringComparison.Ordinal), "invocation names: " + commands + " stderr " + stderr);
        Check(!stderr.Contains("native-hello", StringComparison.Ordinal), "no load failure: " + stderr);
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        foreach (var (suffix, text) in new[] { ("1", "first"), ("2", "second") })
        {
            (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", node, $"/native-hello:{suffix} {text}"]);
            Equal(0, code, "exit; " + stderr);
        }
        var native = NativeLog(sandbox).Where(line => line.StartsWith("command ", StringComparison.Ordinal)).ToArray();
        var nodeRuns = LogLines(sandbox).Where(line => line.StartsWith("[\"node\"", StringComparison.Ordinal)).ToArray();
        Check(native.Length == 1 && nodeRuns.Length == 1 && (native[0] == "command first" ? nodeRuns[0] == """["node","second"]""" :
            native[0] == "command second" && nodeRuns[0] == """["node","first"]"""), "each name:N runs one extension: native " +
            string.Join("|", native) + " node " + string.Join("|", nodeRuns));
        Equal(0, sandbox.Requests.Count, "commands handled without a model request");
    }

    private const string NativeLogVariable = "PISHARP_EXTENSION_PARITY_NATIVE_LOG";

    /// <summary>Writes an extension folder: a copy of this assembly and its manifest. Returns the manifest path.</summary>
    private static string NativeExtensionFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly = typeof(Program).Assembly.Location;
        File.Copy(assembly, Path.Combine(folder, "ParityNative.dll"), overwrite: true);
        var manifest = Path.Combine(folder, "pisharp-extension.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { assembly = "ParityNative.dll", entryType = typeof(ParityNativeExtension).FullName }));
        return manifest;
    }

    private static string[] NativeLog(Sandbox sandbox)
    {
        var path = Path.Combine(sandbox.Root, "native.log");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private static Sandbox NativeSandbox(string name, bool trusted = true)
    {
        var sandbox = new Sandbox(name, trusted);
        Environment.SetEnvironmentVariable(NativeLogVariable, Path.Combine(sandbox.Root, "native.log"));
        return sandbox;
    }

    // package-manager.ts collectAutoExtensionEntries over <cwd>/.pi/extensions (a trusted project): the extension's folder holds its
    // manifest; its command and tool join the session.
    private static async Task NativeProjectTrusted()
    {
        using var sandbox = NativeSandbox("native-trusted");
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-hello world"]);
        Equal(0, code, "exit; " + stderr);
        Check(NativeLog(sandbox).Contains("command world"), "the native command ran: " + string.Join("|", NativeLog(sandbox)));
        Check(sandbox.Requests.Count == 0, "the command was handled without a model request");
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("native_echo", new { text = "ping" }) : AnthropicText("done");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "use it"]);
        Equal(0, code, "exit; " + stderr);
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "native_echo"), "native tool declared");
        Equal("native echo: ping", ToolResultText(sandbox.Requests[1]), "native tool executed");
        Check(NativeLog(sandbox).Contains("start startup"), "session_start observed with reason startup: " + string.Join("|", NativeLog(sandbox)));
    }

    // trust-manager.ts: project extensions (native ones too) load only for a trusted project.
    private static async Task NativeProjectUntrusted()
    {
        using var sandbox = NativeSandbox("native-untrusted", trusted: false);
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--no-approve", "/native-hello world"]);
        Equal(0, code, "exit; " + stderr);
        Equal(0, NativeLog(sandbox).Length, "nothing of the untrusted project's extension ran");
        Check(sandbox.Requests.Single().Body!.Contains("/native-hello world", StringComparison.Ordinal), "the text went to the model as a prompt");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--approve", "/native-hello again"]);
        Equal(0, code, "approved exit; " + stderr);
        Check(NativeLog(sandbox).Contains("command again"), "loaded once the project is trusted");
    }

    // cli/args.ts -e: a native extension needs no Node.js (a run whose PATH has no node).
    private static async Task NativeWithoutNode()
    {
        using var sandbox = NativeSandbox("native-no-node");
        var manifest = NativeExtensionFolder(Path.Combine(sandbox.Root, "elsewhere", "native"));
        sandbox.Vars["PATH"] = Path.Combine(sandbox.Root, "empty-bin");
        sandbox.Vars["PISHARP_NODE"] = Path.Combine(sandbox.Root, "no-node");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", manifest, "/native-hello bare"]);
        Equal(0, code, "exit; " + stderr);
        Check(NativeLog(sandbox).Contains("command bare"), "the native command ran without Node: " + stderr);
    }

    // One runner: the Node and native extensions are owners of one registry; getCommands lists both, pi.events reaches both.
    private static async Task NativeWithNode()
    {
        using var sandbox = NodeSandbox("native-with-node");
        Environment.SetEnvironmentVariable(NativeLogVariable, Path.Combine(sandbox.Root, "native.log"));
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var node = sandbox.Write(Path.Combine(sandbox.Cwd, "node.ts"), Probe + """
            export default function (pi: any) {
              pi.events.on("native-pong", (data: any) => log("pong", data.text));
              pi.registerCommand("list", { description: "List", handler: async () => {
                log("commands", pi.getCommands().filter((c: any) => c.source === "extension").map((c: any) => c.name));
                pi.events.emit("native-ping", { text: "hi" });
                await new Promise((resolve) => setTimeout(resolve, 300));
              } });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", node, "/list"]);
        Equal(0, code, "exit; " + stderr);
        var log = LogLines(sandbox);
        Check(log.Any(line => line.StartsWith("[\"commands\"", StringComparison.Ordinal) && line.Contains("\"list\"", StringComparison.Ordinal) &&
            line.Contains("\"native-hello\"", StringComparison.Ordinal)), "both extensions' commands: " + string.Join("|", log));
        Check(log.Contains("""["pong","hi"]"""), "the native extension answered on pi.events: " + string.Join("|", log) + " native " + string.Join("|", NativeLog(sandbox)));
    }

    // agent-session.ts reload: native extensions load again (session_shutdown reload, then session_start reload with the new instance).
    private static async Task NativeReload()
    {
        using var sandbox = NativeSandbox("native-reload");
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        var steps = new Queue<string>(["""{"id":"reload","type":"prompt","message":"/reload"}""", """{"id":"after","type":"prompt","message":"/native-hello after"}""",
            """{"id":"use","type":"prompt","message":"use it"}"""]);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("native_echo", new { text = "reloaded" }) : AnthropicText("done");
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model], ["""{"id":"before","type":"prompt","message":"/native-hello before"}"""],
            (record, _) => record["type"]?.GetValue<string>() == "agent_settled",
            react: (record, push) => { if (steps.Count > 0 && (IsResponse(record, "before") || IsResponse(record, "reload") || IsResponse(record, "after"))) push(steps.Dequeue()); });
        Equal(0, code, "exit; " + stderr);
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Count(tool => tool.GetProperty("name").GetString() == "native_echo") == 1,
            "the reloaded extension's tool is declared once: " + sandbox.Requests[0].Body);
        Equal("native echo: reloaded", ToolResultText(sandbox.Requests[1]), "the reloaded extension's tool executed");
        var log = NativeLog(sandbox);
        Names(["start startup", "command before", "shutdown reload", "start reload", "command after"],
            log.Where(line => !line.StartsWith("shutdown quit", StringComparison.Ordinal)), "native lifecycle across the reload; records " +
            string.Join("\n", records.Select(item => item.ToJsonString()).Where(text => text.Contains("response", StringComparison.Ordinal) || text.Contains("error", StringComparison.Ordinal))));
    }
}

/// <summary>The native C# extension the cases load (from a copy of this assembly in its own load context).</summary>
public sealed class ParityNativeExtension : IPiSharpExtension
{
    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_LOG") is { } path) File.AppendAllText(path, line + "\n");
    }

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterCommand(new("native-hello", "native-hello", "Native hello", (arguments, context, token) =>
        {
            Log("command " + (arguments.Value.ValueKind == JsonValueKind.String ? arguments.Value.GetString() : ""));
            return ValueTask.CompletedTask;
        }));
        registry.RegisterTool(new("native-echo", "native_echo", "Echoes text", JsonData.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}"""),
            (arguments, context, token) => ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = "native echo: " + arguments.Value.GetProperty("text").GetString() } }, details = new { }
            })))));
        registry.Observe(new("on-session-start", "session_start", (observation, context, token) =>
        {
            Log("start " + observation.Value.GetProperty("reason").GetString());
            return ValueTask.CompletedTask;
        }));
        registry.Observe(new("on-session-shutdown", "session_shutdown", (observation, context, token) =>
        { Log("shutdown " + observation.Value.GetProperty("reason").GetString()); return ValueTask.CompletedTask; }));
        if (registry is IExtensionEventBusRegistry bus)
            bus.Events.On("native-ping", data => bus.Events.Emit("native-pong", data));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
