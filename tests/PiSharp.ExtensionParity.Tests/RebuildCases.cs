using System.Text.Json;
using System.Text.Json.Nodes;

// Owner follow-up 2: the extension runtime is rebuilt on reload without fixed owner or handler slots, late registrations of any event
// name, the session_start reasons, ctx.compact in print mode and prompts waiting for late tool registrations. Expectations follow
// core/agent-session.ts (reload, _buildRuntime, _refreshToolRegistry, compact), core/agent-session-runtime.ts (session_start reasons),
// core/extensions/runner.ts (emit over the live handler maps) and main.ts (the initial runtime starts with reason "startup").
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RebuildCases() =>
    [
        ("rebuild.reload-loads-any-number-of-extensions", ReloadManyExtensions),
        ("rebuild.late-before-agent-start-handlers-are-unbounded", ManyBeforeAgentStartHandlers),
        ("rebuild.late-handler-for-a-custom-event-name", LateCustomEvent),
        ("rebuild.mcp-servers-registered-again-on-reload", McpServersOnReload),
        ("rebuild.prompt-waits-for-a-tool-registered-by-a-command", PromptWaitsForLateTool),
        ("rebuild.session-start-reasons", SessionStartReasons),
        ("rebuild.ctx-compact-in-print-mode", PrintModeCompact),
    ];

    private static string NumberedExtension(int n) => Probe + $$"""
        export default function (pi: any) {
          pi.registerCommand("cmd{{n}}", { description: "Command {{n}}", handler: async () => log("cmd{{n}}") });
          pi.registerTool({ name: "tool{{n}}", label: "Tool {{n}}", description: "Tool number {{n}}", parameters: { type: "object", properties: {} },
            async execute() { return { content: [{ type: "text", text: "tool{{n}} ran" }] }; } });
          pi.on("session_start", async (event: any) => log("start{{n}}", event.reason));
        }
        """;

    // agent-session.ts reload → _buildRuntime: a new runner over every extension the reload loaded (no limit), their commands and tools
    // replacing the previous runtime's.
    private static async Task ReloadManyExtensions()
    {
        using var sandbox = NodeSandbox("reload-many");
        var folder = Path.Combine(sandbox.Cwd, ".pi", "extensions");
        sandbox.Write(Path.Combine(folder, "ext0.ts"), NumberedExtension(0));
        var steps = new Queue<string>([
            """{"id":"reload","type":"prompt","message":"/reload"}""",
            """{"id":"commands","type":"get_commands"}""",
            """{"id":"cmd7","type":"prompt","message":"/cmd7"}""",
            """{"id":"ask","type":"prompt","message":"use tool7"}"""]);
        sandbox.Respond = (seen, index) => seen.Body!.Contains("use tool7", StringComparison.Ordinal) && !seen.Body.Contains("tool7 ran", StringComparison.Ordinal)
            ? AnthropicToolCall("tool7", new { }) : AnthropicText("done");
        var sent = """{"id":"first","type":"get_commands"}""";
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model], [sent],
            (record, _) => record["type"]?.GetValue<string>() == "agent_settled" && JsonNode.Parse(sent)!["id"]!.GetValue<string>() == "ask",
            react: (record, push) =>
            {
                if (steps.Count == 0) return;
                var current = JsonNode.Parse(sent)!["id"]!.GetValue<string>();
                if (!IsResponse(record, current)) return;
                if (current == "first")
                    for (var n = 1; n <= 9; n++) sandbox.Write(Path.Combine(folder, $"ext{n}.ts"), NumberedExtension(n));
                sent = steps.Dequeue(); push(sent);
            });
        Equal(0, code, "exit; " + stderr);
        var commands = records.Single(record => IsResponse(record, "commands"))["data"]!["commands"]!.AsArray()
            .Select(item => item!["name"]!.GetValue<string>()).Where(name => name.StartsWith("cmd", StringComparison.Ordinal)).ToList();
        Names(Enumerable.Range(0, 10).Select(n => "cmd" + n), commands, "every reloaded extension's command");
        var log = LogLines(sandbox);
        Check(log.Contains("""["cmd7"]"""), "a command of the eighth extension ran: " + string.Join("|", log));
        Equal(10, log.Count(line => line.EndsWith(",\"reload\"]", StringComparison.Ordinal)), "session_start(reload) of every extension: " + string.Join("|", log));
        var ask = sandbox.Requests.First(request => request.Body!.Contains("use tool7", StringComparison.Ordinal));
        Names(Enumerable.Range(0, 10).Select(n => "tool" + n).Order(StringComparer.Ordinal),
            ask.Json.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).Where(name => name.StartsWith("tool", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
            "every reloaded extension's tool is declared");
        Equal("tool7 ran", ToolResultText(sandbox.Requests.Last()), "the eighth extension's tool executed");
    }

    // runner.ts emitBeforeAgentStart: every before_agent_start handler runs in order, however many were added after the factory.
    private static async Task ManyBeforeAgentStartHandlers()
    {
        using var sandbox = NodeSandbox("many-before-start");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "chain.ts"), """
            export default function (pi: any) {
              pi.on("session_start", async () => {
                for (let i = 0; i < 12; i++) pi.on("before_agent_start", async (event: any) => ({ systemPrompt: event.systemPrompt + " H" + i }));
              });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "hi"]);
        Equal(0, code, "exit; " + stderr);
        var system = sandbox.Requests[0].Json.GetProperty("system").ToString();
        Check(system.Contains(string.Concat(Enumerable.Range(0, 12).Select(i => " H" + i)), StringComparison.Ordinal), "twelve chained handlers: " + system);
    }

    // runner.ts emit/hasHandlers: a handler registered after loading for any event name is in the runner's live handler map; a
    // dispatch of that name (a native extension of the same session raising it) reaches it.
    private static async Task LateCustomEvent()
    {
        using var sandbox = NodeSandbox("late-custom-event");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "custom.ts"), Probe + """
            export default function (pi: any) {
              pi.events.on("arm", () => pi.on("my_custom_event", async (event: any) => log("custom", event.n)));
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        await using var activation = await PiSharp.Cli.Extensions.NativeExtensionActivation.LoadPiAsync(host, CancellationToken.None);
        activation.Registry.SharedEventBus.Emit("arm", null);
        await WaitUntil(() => activation.Registry.CaptureSnapshot().Registrations.Any(entry => entry.Kind == "Observation" && entry.Name == "my_custom_event"));
        await activation.Registry.DispatchObservationsAsync(activation.Registry.CaptureSnapshot(), "my_custom_event",
            PiSharp.Contracts.JsonData.Parse("""{"type":"my_custom_event","n":7}"""));
        Equal("""["custom",7]""", LogLines(sandbox).Single(), "late custom handler ran");
    }

    private static string McpExtension(string server) => $$"""
        export default function (pi: any) {
          pi.registerMcpServer("{{server}}", { command: "node", args: ["-e", "0"] });
          pi.events.on("late", () => pi.registerMcpServer("late-{{server}}", { command: "node", args: ["-e", "0"] }));
        }
        """;

    // agent-session.ts reload: the new runtime's extensions register their MCP servers again (the previous runtime's leave); a server
    // registered after loading is registered at once.
    private static async Task McpServersOnReload()
    {
        using var sandbox = NodeSandbox("mcp-reload");
        var path = sandbox.Write(Path.Combine(sandbox.Cwd, "servers.ts"), McpExtension("docs"));
        await using var host = await StartHost(sandbox, path);
        var servers = new PiSharp.Cli.Mcp.McpRegisteredServers();
        await using var activation = await PiSharp.Cli.Extensions.NativeExtensionActivation.LoadPiAsync(host, CancellationToken.None, mcpServers: servers);
        Names(["docs"], servers.List().Select(server => server.Name), "registered while loading");
        activation.Registry.SharedEventBus.Emit("late", null);
        await WaitUntil(() => servers.List().Any(server => server.Name == "late-docs"));
        File.WriteAllText(path, McpExtension("wiki"));
        await host.ReloadExtensionsAsync(CancellationToken.None);
        Names(["wiki"], servers.List().Select(server => server.Name), "the reloaded runtime's servers replace the previous runtime's");
        Check(servers.List().All(server => PiSharp.Cli.Pi.PiPaths.Comparer.Equals(server.ExtensionPath, path)), "owned by the extension path");
    }

    // agent-session.ts _refreshToolRegistry is synchronous: a prompt right after the command that registered a tool declares it.
    private static async Task PromptWaitsForLateTool()
    {
        using var sandbox = NodeSandbox("prompt-waits");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "add.ts"), """
            export default function (pi: any) {
              pi.registerCommand("add", { description: "Add a tool", handler: async () => {
                pi.registerTool({ name: "added", label: "Added", description: "Added by a command", parameters: { type: "object", properties: {} },
                  async execute() { return { content: [{ type: "text", text: "added ran" }] }; } });
              } });
            }
            """);
        var (code, _, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension],
            ["""{"id":"add","type":"prompt","message":"/add"}""", """{"id":"go","type":"prompt","message":"go"}"""],
            (record, _) => record["type"]?.GetValue<string>() == "agent_settled");
        Equal(0, code, "exit; " + stderr);
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "added"),
            "the prompt after /add declares the tool: " + sandbox.Requests[0].Body);
    }

    // main.ts / agent-session-runtime.ts: the initial runtime starts with "startup" (also for a continued session); /new "new" with
    // the previous session file; a resumed session "resume"; a fork "fork"; a reload "reload".
    private static async Task SessionStartReasons()
    {
        using var sandbox = NodeSandbox("start-reasons");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "reasons.ts"), Probe + """
            export default function (pi: any) {
              pi.on("session_start", async (event: any) => log(event.reason, typeof event.previousSessionFile));
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "one"]);
        Equal(0, code, "exit; " + stderr);
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p", "-c" }, .. Model, "-e", extension, "two"]);
        Equal(0, code, "exit; " + stderr);
        Names(["[\"startup\",\"undefined\"]", "[\"startup\",\"undefined\"]"], LogLines(sandbox), "startup for a new and a continued session");
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        string? firstFile = null;
        var sent = """{"id":"first","type":"prompt","message":"first"}""";
        var (rpc, records, rpcError) = await RunRpc(sandbox, [.. Model, "-e", extension], [sent],
            (record, _) => IsResponse(record, "reload"),
            react: (record, push) =>
            {
                var current = JsonNode.Parse(sent)!["id"]!.GetValue<string>();
                string? next = null;
                if (current == "first" && record["type"]?.GetValue<string>() == "agent_settled")
                {
                    firstFile = sandbox.SessionFiles().OrderBy(File.GetLastWriteTimeUtc).Last();
                    next = """{"id":"new","type":"new_session"}""";
                }
                else if (current == "new" && IsResponse(record, "new")) next = JsonSerializer.Serialize(new { id = "resume", type = "switch_session", sessionPath = firstFile });
                else if (current == "resume" && IsResponse(record, "resume")) next = """{"id":"reload","type":"prompt","message":"/reload"}""";
                if (next is null) return;
                sent = next; push(sent);
            });
        Equal(0, rpc, "rpc exit; " + rpcError);
        Names(["[\"startup\",\"undefined\"]", "[\"new\",\"string\"]", "[\"resume\",\"string\"]", "[\"reload\",\"undefined\"]"], LogLines(sandbox),
            "rpc session_start reasons; records " + string.Join("\n", records.Select(item => item.ToJsonString()).Where(text => text.Contains("response", StringComparison.Ordinal))));
    }

    // agent-session.ts compact(): ctx.compact works in every mode; in print mode a command that waits for it gets the CompactionResult.
    private static async Task PrintModeCompact()
    {
        using var sandbox = NodeSandbox("print-compact");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":2000}}""");
        sandbox.Respond = (seen, _) => AnthropicText(seen.Body!.Contains("summar", StringComparison.OrdinalIgnoreCase) && seen.Body.Contains("compact", StringComparison.OrdinalIgnoreCase)
            ? "SUMMARY OF THE WORK" : "noted " + new string('x', 24000));
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "squash.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("squash", { description: "Compact", handler: async (_args: string, ctx: any) => {
                const result: any = await new Promise((resolve) => ctx.compact({ customInstructions: "Keep the plan",
                  onComplete: (value: any) => resolve(value), onError: (error: any) => resolve({ error: error.message }) }));
                log("compacted", result.error ?? typeof result.summary);
              } });
            }
            """);
        for (var index = 1; index <= 3; index++)
        {
            var (step, _, stepError) = await sandbox.Run([.. new[] { "-p" }, .. (index == 1 ? Array.Empty<string>() : ["-c"]), .. Model, "step " + index + " " + new string('y', 24000)]);
            Equal(0, step, "exit; " + stepError);
        }
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p", "-c" }, .. Model, "-e", extension, "/squash"]);
        Equal(0, code, "exit; " + stderr);
        Equal("""["compacted","string"]""", LogLines(sandbox).Single(), "print-mode compaction result");
        Check(sandbox.Requests.Any(request => request.Body!.Contains("Keep the plan", StringComparison.Ordinal)), "custom instructions sent to the summarizer");
    }
}
