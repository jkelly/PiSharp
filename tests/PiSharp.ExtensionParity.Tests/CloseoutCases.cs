using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Extensions;

// The extension differences 1.1.0.2 still listed (1.1.0.3 close-out): sendMessage with triggerTurn from inside a command
// (agent-session.ts sendCustomMessage and _runAgentPrompt), ctx.ui.setTheme outside interactive mode (rpc-mode.ts, runner.ts
// noOpUIContext) and the CLI's own events reaching native C# extensions (runner.ts emitProjectTrustEvent, emitResourcesDiscover).
// Expectations are written from the pinned sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CloseoutCases() =>
    [
        ("closeout.trigger-turn-from-a-command-runs-while-the-handler-runs", TriggerTurnDuringCommand),
        ("closeout.set-theme-outside-interactive-mode", SetThemeOutsideInteractive),
        ("closeout.native-project-trust-handler-decides", NativeProjectTrust),
        ("closeout.native-resources-discover-adds-paths", NativeResourcesDiscover),
        ("closeout.sample-stateful-todo-loads-from-an-extension-folder", SampleStatefulTodo),
        ("closeout.sample-session-checkpoint-loads-with-dash-e", SampleSessionCheckpoint),
        ("closeout.trigger-turn-while-idle-runs-a-turn", TriggerTurnWhileIdle),
    ];

    // agent-session.ts sendCustomMessage: an idle session with triggerTurn starts a turn (_runAgentPrompt) outside any prompt, e.g. from a
    // timer an event handler set; rpc-mode.ts streams its events and agent_settled.
    private static async Task TriggerTurnWhileIdle()
    {
        using var sandbox = NodeSandbox("trigger-turn-idle");
        sandbox.Respond = (_, _) => AnthropicText("idle answer");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "idle.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("later", { description: "Later", handler: async () => {
                setTimeout(() => pi.sendMessage({ customType: "later", content: "wake up", display: true }, { triggerTurn: true }), 300);
              } });
            }
            """);
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"l","type":"prompt","message":"/later"}"""],
            (record, _) => record["type"]?.GetValue<string>() == "agent_settled", TimeSpan.FromSeconds(60));
        var types = records.Select(record => record["type"]?.GetValue<string>() == "response" ? "response:" + record["data"]?["disposition"]?.GetValue<string>() : record["type"]?.GetValue<string>()).ToList();
        Equal(0, code, "rpc exit; " + stderr + " " + string.Join(",", types));
        Check(types.IndexOf("response:handled") >= 0 && types.IndexOf("agent_start") > types.IndexOf("response:handled") && types.Contains("agent_settled"),
            "record order: " + string.Join(",", types));
        Equal(1, sandbox.Requests.Count, "the idle turn made a request");
    }

    /// <summary>A sample's build output (samples/extensions/&lt;name&gt;/bin/&lt;configuration&gt;/net10.0, built before this suite):
    /// its assembly next to the pisharp-extension.json manifest the build copies there.</summary>
    private static string SampleOutput(string name)
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configuration = tfm.Parent!.Name;
        var repo = tfm;
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "PiSharp.slnx"))) repo = repo.Parent;
        var output = Path.Combine(repo?.FullName ?? throw new InvalidOperationException("Repository root not found."), "samples", "extensions", name, "bin", configuration, tfm.Name);
        Check(File.Exists(Path.Combine(output, "pisharp-extension.json")) && File.Exists(Path.Combine(output, name + ".dll")), "the sample was built with its manifest: " + output);
        return output;
    }

    private static void CopyFolder(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, overwrite: true);
        }
    }

    // samples/extensions/StatefulTodo (Pi's todo.ts example as a native tool): copied into the project's .pi/extensions folder it loads
    // as Pi loads extensions (owner decision 10); its state is rebuilt from the session branch, so a continued session lists the todo
    // the first run added.
    private static async Task SampleStatefulTodo()
    {
        using var sandbox = NativeSandbox("sample-todo");
        CopyFolder(SampleOutput("StatefulTodo"), Path.Combine(sandbox.Cwd, ".pi", "extensions", "stateful-todo"));
        sandbox.Respond = (_, index) => index switch
        {
            0 => AnthropicToolCall("todo", new { action = "add", text = "buy milk" }),
            2 => AnthropicToolCall("todo", new { action = "list" }, "toolu_02"),
            _ => AnthropicText("done")
        };
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "add a todo"]);
        Equal(0, code, "exit; " + stderr);
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "todo"), "todo tool declared");
        Equal("Added todo #1: buy milk", ToolResultText(sandbox.Requests[1]), "add");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p", "-c" }, .. Model, "list the todos"]);
        Equal(0, code, "continued exit; " + stderr);
        Equal("[ ] #1: buy milk", ToolResultText(sandbox.Requests[3]), "the state comes from the continued session's branch");
    }

    // samples/extensions/SessionCheckpoint: loaded with -e <manifest>, /checkpoint saves a durable checkpoint entry and switches to an
    // existing session (ctx.switchSession), saving there through the fresh context.
    private static async Task SampleSessionCheckpoint()
    {
        using var sandbox = NativeSandbox("sample-checkpoint");
        var manifest = Path.Combine(SampleOutput("SessionCheckpoint"), "pisharp-extension.json");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", manifest, "first session"]);
        Equal(0, code, "exit; " + stderr);
        var source = sandbox.SessionFiles().Single();
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", manifest, "second session"]);
        Equal(0, code, "exit; " + stderr);
        var target = sandbox.SessionFiles().Single(path => !PiSharp.Cli.Pi.PiPaths.Comparer.Equals(path, source));
        var request = JsonSerializer.Serialize(new { target, confirmSwitch = false, label = "sample" });
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p", "--session", source }, .. Model, "-e", manifest, "/checkpoint " + request]);
        Equal(0, code, "checkpoint exit; " + stderr);
        Check(File.ReadAllText(source).Contains("\"phase\":\"before-switch\"", StringComparison.Ordinal), "checkpoint saved in the command's session: " + File.ReadAllText(source));
        Check(File.ReadAllText(target).Contains("\"phase\":\"after-switch\"", StringComparison.Ordinal), "checkpoint saved in the switched-to session: " + File.ReadAllText(target));
    }

    /// <summary>Writes a native extension folder whose entry type is <see cref="ParityNativeEventsExtension"/>.</summary>
    private static string NativeEventsFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        File.Copy(typeof(Program).Assembly.Location, Path.Combine(folder, "ParityNativeEvents.dll"), overwrite: true);
        var manifest = Path.Combine(folder, "pisharp-extension.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { assembly = "ParityNativeEvents.dll", entryType = typeof(ParityNativeEventsExtension).FullName }));
        return manifest;
    }

    // runner.ts emitProjectTrustEvent over the extensions loaded before trust (resource-loader.ts loadProjectTrustExtensions: user and
    // CLI extensions): a native user extension's project_trust handler sees { type, cwd } and its "yes" trusts the project, so the
    // project's own extensions load (here the project's native /native-hello command).
    private static async Task NativeProjectTrust()
    {
        using var sandbox = NativeSandbox("native-project-trust", trusted: false);
        NativeEventsFolder(Path.Combine(sandbox.AgentDir, "extensions", "native-events"));
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-hello"));
        Environment.SetEnvironmentVariable(NativeTrustVariable, "yes");
        try
        {
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-hello trusted"]);
            Equal(0, code, "exit; " + stderr);
            var log = NativeLog(sandbox);
            Check(log.Contains("project_trust " + JsonSerializer.Serialize(new { type = "project_trust", cwd = sandbox.Cwd })), "the native handler saw the event: " + string.Join("|", log));
            Check(log.Contains("command trusted"), "the native handler's decision trusted the project: " + string.Join("|", log) + " stderr " + stderr);
            Equal(0, sandbox.Requests.Count, "the project's command ran without a model request");
            File.Delete(Path.Combine(sandbox.Root, "native.log"));
            Environment.SetEnvironmentVariable(NativeTrustVariable, "no");
            (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "/native-hello untrusted"]);
            Equal(0, code, "exit; " + stderr);
            Check(!NativeLog(sandbox).Contains("command untrusted"), "a native \"no\" keeps the project untrusted: " + string.Join("|", NativeLog(sandbox)));
        }
        finally { Environment.SetEnvironmentVariable(NativeTrustVariable, null); }
    }

    // agent-session.ts extendResourcesFromExtensions: resources_discover ({ type, cwd, reason: "startup" }) reaches native handlers;
    // the prompt paths they return become prompt templates of the run.
    private static async Task NativeResourcesDiscover()
    {
        using var sandbox = NativeSandbox("native-resources-discover");
        var manifest = NativeEventsFolder(Path.Combine(sandbox.Root, "elsewhere", "native-events"));
        var template = sandbox.Write(Path.Combine(sandbox.Root, "discovered", "discovered-hello.md"), "---\ndescription: Discovered\n---\nTemplate body from a native extension");
        Environment.SetEnvironmentVariable(NativePromptVariable, template);
        try
        {
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", manifest, "/discovered-hello"]);
            Equal(0, code, "exit; " + stderr);
            Check(NativeLog(sandbox).Contains("resources_discover " + JsonSerializer.Serialize(new { type = "resources_discover", cwd = sandbox.Cwd, reason = "startup" })),
                "the native handler saw the event: " + string.Join("|", NativeLog(sandbox)));
            Check(sandbox.Requests.Single().Body!.Contains("Template body from a native extension", StringComparison.Ordinal), "the discovered template expanded: " + sandbox.Requests.Single().Body);
            // resource-loader.ts reload: /reload asks the reloaded extensions again, with reason "reload".
            var (rpcCode, records, rpcStderr) = await RunRpc(sandbox, [.. Model, "-e", manifest], ["""{"id":"r","type":"prompt","message":"/reload"}"""],
                (record, _) => IsResponse(record, "r"));
            Equal(0, rpcCode, "rpc exit; " + rpcStderr + string.Join("\n", records.Select(record => record.ToJsonString())));
            Check(NativeLog(sandbox).Contains("resources_discover " + JsonSerializer.Serialize(new { type = "resources_discover", cwd = sandbox.Cwd, reason = "reload" })),
                "the reloaded native handler saw reason reload: " + string.Join("|", NativeLog(sandbox)));
        }
        finally { Environment.SetEnvironmentVariable(NativePromptVariable, null); }
    }

    private const string NativeTrustVariable = "PISHARP_EXTENSION_PARITY_NATIVE_TRUST";
    private const string NativePromptVariable = "PISHARP_EXTENSION_PARITY_NATIVE_PROMPT";

    // agent-session.ts sendCustomMessage: an idle session (a command's handler runs outside any agent run) with triggerTurn calls
    // _runAgentPrompt at once, so the turn runs while the handler is still running; ctx.waitForIdle() (isIdle: no agent run, no
    // compaction) waits for that turn, and the handler then sees its assistant message. print-mode.ts prints the last assistant text.
    private static async Task TriggerTurnDuringCommand()
    {
        using var sandbox = NodeSandbox("trigger-turn-command");
        sandbox.Respond = (_, _) => AnthropicText("turn answer");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "kick.ts"), Probe + """
            export default function (pi: any) {
              pi.on("agent_start", () => log("agent_start"));
              pi.on("agent_end", () => log("agent_end"));
              pi.registerCommand("kick", { description: "Kick", handler: async (_args: string, ctx: any) => {
                log("handler_start", ctx.isIdle());
                pi.sendMessage({ customType: "kick", content: "go now", display: true }, { triggerTurn: true });
                await ctx.waitForIdle();
                const entries = ctx.sessionManager.getEntries();
                const last = entries[entries.length - 1];
                log("handler_end", last.type, last.message?.role, last.message?.content?.[0]?.text);
              } });
            }
            """);
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/kick"]);
        Equal(0, code, "exit; " + stderr);
        Names(["""["handler_start",true]""", """["agent_start"]""", """["agent_end"]""", """["handler_end","message","assistant","turn answer"]"""],
            LogLines(sandbox), "the turn ran inside the handler; stderr " + stderr);
        Equal(1, sandbox.Requests.Count, "one model request");
        var messages = sandbox.Requests[0].Json.GetProperty("messages");
        var lastMessage = messages[messages.GetArrayLength() - 1];
        Equal("user", lastMessage.GetProperty("role").GetString(), "the custom message is sent as a user message");
        Check(lastMessage.GetRawText().Contains("go now", StringComparison.Ordinal), "the custom message content: " + lastMessage.GetRawText());
        Equal("turn answer\n", stdout, "print mode prints the turn's assistant text");

        // rpc-mode.ts: the turn's events stream while the handler runs; the prompt is answered "handled" once the handler returns,
        // and the run settles (agent_settled) as any other.
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        var (rpcCode, records, rpcStderr) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"k","type":"prompt","message":"/kick"}"""],
            (record, seen) => seen.Any(item => IsResponse(item, "k")) && seen.Any(item => item["type"]?.GetValue<string>() == "agent_settled"));
        var types = records.Select(record => record["type"]?.GetValue<string>() == "response" ? "response:" + record["data"]?["disposition"]?.GetValue<string>() : record["type"]?.GetValue<string>()).ToList();
        Equal(0, rpcCode, "rpc exit; " + rpcStderr + " " + string.Join(",", types));
        Check(types.IndexOf("agent_start") >= 0 && types.IndexOf("agent_end") > types.IndexOf("agent_start") &&
            types.IndexOf("response:handled") > types.IndexOf("agent_end") && types.Contains("agent_settled"), "record order: " + string.Join(",", types));
        Equal(2, sandbox.Requests.Count, "the RPC turn made its own request");
    }

    // rpc-mode.ts createExtensionUIContext: setTheme returns { success: false, error: "Theme switching not supported in RPC mode" };
    // print and json modes have no UI (runner.ts noOpUIContext: "UI not available").
    private static async Task SetThemeOutsideInteractive()
    {
        using var sandbox = NodeSandbox("set-theme-modes");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "theme.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("theme-probe", { description: "Theme", handler: async (_args: string, ctx: any) => {
                log(ctx.mode ?? null, ctx.hasUI, ctx.ui.setTheme("light"), ctx.ui.setTheme("no-such-theme"));
              } });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/theme-probe"]);
        Equal(0, code, "print exit; " + stderr);
        var (rpcCode, records, rpcStderr) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"t","type":"prompt","message":"/theme-probe"}"""],
            (record, _) => IsResponse(record, "t"));
        Equal(0, rpcCode, "rpc exit; " + rpcStderr + string.Join("\n", records.Select(record => record.ToJsonString())));
        var lines = LogLines(sandbox);
        Equal(2, lines.Length, "two runs logged: " + string.Join("|", lines));
        var print = JsonNode.Parse(lines[0])!.AsArray(); var rpc = JsonNode.Parse(lines[1])!.AsArray();
        Equal(false, print[1]!.GetValue<bool>(), "print mode has no UI");
        Equal("""{"success":false,"error":"UI not available"}""", print[2]!.ToJsonString(), "print setTheme");
        Equal(true, rpc[1]!.GetValue<bool>(), "rpc mode has a UI");
        Equal("""{"success":false,"error":"Theme switching not supported in RPC mode"}""", rpc[2]!.ToJsonString(), "rpc setTheme");
        Equal("""{"success":false,"error":"Theme switching not supported in RPC mode"}""", rpc[3]!.ToJsonString(), "rpc setTheme of an unknown theme");
    }
}

/// <summary>A native extension with handlers for the CLI's own events (loaded from a copy of this assembly).</summary>
public sealed class ParityNativeEventsExtension : IPiSharpExtension
{
    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_LOG") is { } path) File.AppendAllText(path, line + "\n");
    }

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        if (registry is not IExtensionEventHandlerRegistry events) throw new InvalidOperationException("No event handler registry.");
        events.RegisterEventHandler(new("trust", "project_trust", (value, context, token) =>
        {
            Log("project_trust " + value);
            var decision = Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_TRUST") ?? "undecided";
            return ValueTask.FromResult<JsonData?>(JsonData.Parse(JsonSerializer.Serialize(new { trusted = decision })));
        }));
        events.RegisterEventHandler(new("discover", "resources_discover", (value, context, token) =>
        {
            Log("resources_discover " + value);
            var prompt = Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_PROMPT");
            return ValueTask.FromResult<JsonData?>(prompt is null ? null : JsonData.Parse(JsonSerializer.Serialize(new { promptPaths = new[] { prompt } })));
        }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
