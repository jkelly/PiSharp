using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// The extension API gaps closed after the first IMPL-E round (owner follow-up): duplicate command names, getCommands, ctx.compact,
// ctx.shutdown, promptSnippet, executionMode, outputSchema, built-in tool factories with operations and pi.events. Expectations follow
// core/extensions/{runner,loader,types}.ts, core/agent-session.ts and agent/src/agent-loop.ts at the pinned commit.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> GapCases() =>
    [
        ("gap.duplicate-commands-are-name-colon-n", DuplicateCommands),
        ("gap.get-commands-lists-templates-and-skills", GetCommandsCatalog),
        ("gap.ctx-compact-runs-the-session-compaction", ContextCompact),
        ("gap.ctx-shutdown-stops-rpc-after-the-command", ContextShutdown),
        ("gap.prompt-snippet-and-guidelines-in-the-system-prompt", PromptSnippet),
        ("gap.extension-tools-run-in-parallel-unless-sequential", ExecutionMode),
        ("gap.output-schema-reaches-codemode-declarations", OutputSchema),
        ("gap.builtin-tool-factory-with-custom-operations", BuiltinFactoryOperations),
        ("gap.pi-events-shared-with-native-extensions", EventsBridge),
        ("gap.send-message-from-an-idle-command-appends-and-displays", SendMessageWhileIdle),
        ("gap.shortcuts-run-with-a-fresh-context", ShortcutContext),
        ("gap.registrations-after-the-factory-take-effect", LateRegistrations),
        ("gap.reload-reloads-extensions-and-resources", Reload),
        ("gap.chat-provider-registration-and-unregistration", ChatProvider),
    ];

    // agent-session.ts sendCustomMessage: idle and without triggerTurn, the message is appended and emitted (message_start/_end) at once.
    private static async Task SendMessageWhileIdle()
    {
        using var sandbox = NodeSandbox("send-idle");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "note.ts"), """
            export default function (pi: any) {
              pi.registerCommand("note", { description: "Note", handler: async () => {
                pi.sendMessage({ customType: "note", content: "remember this", display: true, details: { n: 1 } });
              } });
            }
            """);
        // The session holds the message at once (get_messages); the file itself is written with the first assistant message, as upstream.
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"n","type":"prompt","message":"/note"}"""],
            (record, _) => IsResponse(record, "m"),
            react: (record, push) => { if (record["type"]?.GetValue<string>() == "message_end" && record["message"]?["role"]?.GetValue<string>() == "custom") push("""{"id":"m","type":"get_messages"}"""); });
        Equal(0, code, "exit; " + stderr + "; records " + string.Join("\n", records.Select(item => item.ToJsonString())));
        Check(records.Any(record => record["type"]?.GetValue<string>() == "message_start" && record["message"]?["customType"]?.GetValue<string>() == "note"), "message_start of the custom message");
        var messages = records.Single(record => IsResponse(record, "m"))["data"]!["messages"]!.AsArray();
        Check(messages.Any(message => message!["role"]?.GetValue<string>() == "custom" && message["customType"]?.GetValue<string>() == "note" &&
            message["content"]?.GetValue<string>() == "remember this"), "custom message in the session: " + messages.ToJsonString());
    }

    // runner.ts getShortcuts/createContext: shortcut handlers run with a fresh context; reserved built-in keys are skipped.
    private static async Task ShortcutContext()
    {
        using var sandbox = NodeSandbox("shortcut-context");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "keys.ts"), Probe + """
            export default function (pi: any) {
              pi.registerShortcut("ctrl+shift+k", { description: "Probe", handler: async (ctx: any) => log("shortcut", ctx.cwd === process.cwd(), typeof ctx.ui.notify, ctx.hasUI) });
              pi.registerShortcut("ctrl+c", { description: "Steal interrupt", handler: async () => log("stolen") });
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        var (shortcuts, diagnostics) = host.ResolveShortcuts(new Dictionary<string, IReadOnlyList<string>> { ["app.interrupt"] = ["escape", "ctrl+c"] });
        Names(["ctrl+shift+k"], shortcuts.Select(shortcut => shortcut.Key), "reserved key skipped");
        Check(diagnostics.Single().Message.Contains("conflicts with built-in shortcut. Skipping.", StringComparison.Ordinal), "diagnostic");
        await host.RunShortcutAsync(shortcuts[0].Extension, shortcuts[0].Shortcut, CancellationToken.None);
        Equal("""["shortcut",true,"function",true]""", LogLines(sandbox).Single(), "handler ran with a context");
    }

    /// <summary>An Anthropic Messages stream calling several tools in one message.</summary>
    internal static HttpResponseMessage AnthropicToolCalls(params (string Name, object Input, string Id)[] calls)
    {
        var body = Frame("message_start", new { type = "message_start", message = new { id = "msg_p", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } });
        for (var index = 0; index < calls.Length; index++)
            body += Frame("content_block_start", new { type = "content_block_start", index, content_block = new { type = "tool_use", id = calls[index].Id, name = calls[index].Name, input = new { } } })
                + Frame("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(calls[index].Input) } })
                + Frame("content_block_stop", new { type = "content_block_stop", index });
        body += Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 2 } })
            + Frame("message_stop", new { type = "message_stop" });
        return Sse(body);
    }

    private const string Probe = """
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        """;

    /// <summary>Runs PiCommand in RPC mode with the commands; <paramref name="done"/> sees every output record and ends the input.</summary>
    private static async Task<(int Code, List<JsonNode> Records, string Stderr)> RunRpc(Sandbox sandbox, string[] args, string[] commands,
        Func<JsonNode, List<JsonNode>, bool>? done, TimeSpan? timeout = null, Action<JsonNode, Action<string>>? react = null)
    {
        var input = new LineInput();
        foreach (var command in commands) input.Push(command);
        var records = new List<JsonNode>();
        using var output = new LineOutput(line =>
        {
            var record = JsonNode.Parse(line)!;
            bool finished;
            lock (records) { records.Add(record); react?.Invoke(record, input.Push); finished = done?.Invoke(record, records) == true; }
            if (finished) input.Complete();
        });
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(120));
        var code = await PiCommand.RunAsync(["--mode", "rpc", .. args], host, deadline.Token);
        lock (records) return (code, [.. records], stderr.ToString());
    }

    private static bool IsResponse(JsonNode record, string id) => record["type"]?.GetValue<string>() == "response" && record["id"]?.GetValue<string>() == id;

    // runner.ts resolveRegisteredCommands: a name two extensions register is invoked as name:1 and name:2 (load order).
    private static async Task DuplicateCommands()
    {
        using var sandbox = NodeSandbox("dup-commands");
        string Extension(string tag) => sandbox.Write(Path.Combine(sandbox.Cwd, tag + ".ts"), Probe + $$"""
            export default function (pi: any) {
              pi.registerCommand("dup", { description: "{{tag}}", handler: async (args: string) => log("dup", "{{tag}}", args) });
              pi.registerCommand("list-{{tag}}", { description: "list", handler: async () => log("names", "{{tag}}", pi.getCommands().filter((c: any) => c.source === "extension").map((c: any) => c.name)) });
            }
            """);
        var first = Extension("one"); var second = Extension("two");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", first, "-e", second, "/dup:2 hello"]);
        Equal(0, code, "exit; " + stderr);
        Equal("""["dup","two","hello"]""", LogLines(sandbox).Single(), "/dup:2 runs the second extension's command");
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", first, "-e", second, "/list-one"]);
        Equal(0, code, "exit; " + stderr);
        Equal("""["names","one",["dup:1","list-one","dup:2","list-two"]]""", LogLines(sandbox).Single(), "getCommands invocation names");
    }

    // agent-session.ts getCommands: extension commands, then prompt templates, then skills (skill:<name>).
    private static async Task GetCommandsCatalog()
    {
        using var sandbox = NodeSandbox("get-commands");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "prompts", "review.md"), "---\ndescription: Review the change\n---\nReview $@\n");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "skills", "deploy", "SKILL.md"), "---\nname: deploy\ndescription: Deploy the app\n---\nSteps.\n");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "list.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("list", { description: "List commands", handler: async () =>
                log(pi.getCommands().map((c: any) => [c.name, c.source, c.description ?? null, c.sourceInfo?.path?.endsWith(".ts") || c.sourceInfo?.path?.endsWith(".md") || false])) });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/list"]);
        Equal(0, code, "exit; " + stderr);
        Equal("""[[["list","extension","List commands",true],["review","prompt","Review the change",true],["skill:deploy","skill","Deploy the app",true]]]""",
            LogLines(sandbox).Single(), "getCommands rows");
    }

    // agent-session.ts compact(): ctx.compact() aborts, compacts the session (compaction events) and calls onComplete with the result.
    private static async Task ContextCompact()
    {
        using var sandbox = NodeSandbox("compact");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":2000}}""");
        sandbox.Respond = (seen, _) => AnthropicText(seen.Body!.Contains("summar", StringComparison.OrdinalIgnoreCase) && seen.Body.Contains("compact", StringComparison.OrdinalIgnoreCase)
            ? "SUMMARY OF THE WORK" : "noted " + new string('x', 24000));
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "squash.ts"), Probe + """
            export default function (pi: any) {
              pi.registerCommand("squash", { description: "Compact", handler: async (_args: string, ctx: any) => {
                ctx.compact({ customInstructions: "Keep the plan",
                  onComplete: (result: any) => { log("compacted", typeof result.summary, typeof result.firstKeptEntryId, result.tokensBefore > 0); ctx.ui.notify("compaction finished"); },
                  onError: (error: any) => { log("error", error.message); ctx.ui.notify("compaction finished"); } });
                log("returned");
              } });
            }
            """);
        // One prompt at a time (each after the previous run settles), enough text that compaction keeps only recent turns.
        var pending = new Queue<string>(Enumerable.Range(1, 4).Select(index => JsonSerializer.Serialize(new { id = "p" + index, type = "prompt", message = "step " + index + " " + new string('y', 24000) })));
        pending.Enqueue("""{"id":"squash","type":"prompt","message":"/squash"}""");
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension], [pending.Dequeue()],
            (record, all) => record["type"]?.GetValue<string>() == "extension_ui_request" && record["message"]?.GetValue<string>() == "compaction finished",
            react: (record, push) => { if (record["type"]?.GetValue<string>() == "agent_settled" && pending.Count > 0) push(pending.Dequeue()); });
        Equal(0, code, "exit; " + stderr + "; log " + string.Join("|", LogLines(sandbox)) + "; records " + string.Join(",", records.Select(item => item["type"]?.GetValue<string>() + ":" + (item["command"] ?? item["reason"])?.ToJsonString())));
        var log = LogLines(sandbox);
        Equal("""["returned"]""", log.First(), "compact() returns at once");
        Equal("""["compacted","string","string",true]""", log.Last(), "onComplete with the CompactionResult; records: " + string.Join("\n", records.Select(item => item.ToJsonString()).Where(text => text.Contains("compaction", StringComparison.Ordinal))));
        Check(records.Any(item => item["type"]?.GetValue<string>() == "compaction_start" && item["reason"]?.GetValue<string>() == "manual"), "compaction_start (manual)");
        Check(sandbox.Requests.Any(request => request.Body!.Contains("Keep the plan", StringComparison.Ordinal)), "custom instructions sent to the summarizer");
        Check(!records.Any(item => item["type"]?.GetValue<string>() == "response" && item["command"]?.GetValue<string>() == "compact"), "no response record for an extension compaction");
    }

    // rpc-mode.ts shutdownHandler: ctx.shutdown() records the request; the mode shuts down after the command, running session_shutdown.
    private static async Task ContextShutdown()
    {
        using var sandbox = NodeSandbox("shutdown");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "bye.ts"), Probe + """
            export default function (pi: any) {
              pi.on("session_shutdown", async (event: any) => log("shutdown", event.reason));
              pi.registerCommand("bye", { description: "Quit", handler: async (_args: string, ctx: any) => { ctx.shutdown(); log("requested"); } });
            }
            """);
        // The input never ends: only the shutdown request stops the run.
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"bye","type":"prompt","message":"/bye"}"""], null, TimeSpan.FromSeconds(60));
        Equal(0, code, "exit; " + stderr);
        Check(records.Any(record => IsResponse(record, "bye")), "the command was answered first");
        Names(["[\"requested\"]", "[\"shutdown\",\"quit\"]"], LogLines(sandbox), "shutdown after the command");
    }

    // agent-session.ts _rebuildSystemPrompt: an extension tool's promptSnippet lists it under the tools, and its promptGuidelines join the rules.
    private static async Task PromptSnippet()
    {
        using var sandbox = NodeSandbox("snippet");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "snippet.ts"), """
            export default function (pi: any) {
              pi.registerTool({ name: "probe", label: "Probe", description: "Probe things", promptSnippet: "Probe the\n  workspace",
                promptGuidelines: ["  Use probe before guessing  ", "Use probe before guessing"],
                parameters: { type: "object", properties: {} }, async execute() { return { content: [{ type: "text", text: "ok" }] }; } });
              pi.registerTool({ name: "quiet", label: "Quiet", description: "No snippet", parameters: { type: "object", properties: {} },
                async execute() { return { content: [{ type: "text", text: "ok" }] }; } });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "hi"]);
        Equal(0, code, "exit; " + stderr);
        var system = sandbox.Requests[0].Json.GetProperty("system").ToString();
        Check(system.Contains("- probe: Probe the workspace", StringComparison.Ordinal), "snippet line: " + system);
        Check(!system.Contains("- quiet:", StringComparison.Ordinal), "a tool without a snippet is not listed");
        Equal(1, System.Text.RegularExpressions.Regex.Matches(system, "- Use probe before guessing").Count, "guideline once");
    }

    // agent-loop.ts executeToolCalls: a batch runs in parallel unless a called tool declares executionMode "sequential".
    private static async Task ExecutionMode()
    {
        foreach (var sequential in new[] { false, true })
        {
            using var sandbox = NodeSandbox("execution-" + sequential);
            var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "slow.ts"), Probe + $$"""
                const slow = (name: string) => ({ name, label: name, description: "Slow", parameters: { type: "object", properties: {} },
                  {{(sequential ? "executionMode: \"sequential\"," : "")}}
                  async execute() { log("start", name); await new Promise(resolve => setTimeout(resolve, 400)); log("end", name); return { content: [{ type: "text", text: name }] }; } });
                export default function (pi: any) { pi.registerTool(slow("slow_a")); pi.registerTool(slow("slow_b")); }
                """);
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCalls(("slow_a", new { }, "toolu_a"), ("slow_b", new { }, "toolu_b")) : AnthropicText("done");
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "go"]);
            Equal(0, code, "exit; " + stderr);
            var order = LogLines(sandbox).Select(line => JsonNode.Parse(line)![0]!.GetValue<string>()).ToArray();
            Names(sequential ? ["start", "end", "start", "end"] : ["start", "start", "end", "end"], order, sequential ? "sequential batch" : "parallel batch");
        }
    }

    // tool.ts toCodemodeDeclaration: a tool's outputSchema types its codemode declaration (and resolves to structuredContent).
    private static async Task OutputSchema()
    {
        using var sandbox = NodeSandbox("output-schema");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "schema.ts"), """
            export default function (pi: any) {
              pi.registerTool({ name: "counter", label: "Counter", description: "Counts things", exposure: "codemode",
                parameters: { type: "object", properties: {} },
                outputSchema: { type: "object", properties: { count: { type: "number" } }, required: ["count"] },
                async execute() { return { content: [{ type: "text", text: "3" }], structuredContent: { count: 3 } }; } });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--tools", "read,codemode,counter", "-e", extension, "hi"]);
        Equal(0, code, "exit; " + stderr);
        var request = sandbox.Requests[0].Body!;
        Check(request.Contains("counter(args: { [key: string]: unknown; }): Promise<{ count: number; }>", StringComparison.Ordinal), "typed codemode declaration: " + request);
    }

    // tools/bash.ts BashOperations: a built-in tool definition made by the factory with custom operations runs them.
    private static async Task BuiltinFactoryOperations()
    {
        using var sandbox = NodeSandbox("factory-operations");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "remote.ts"), """
            import { createBashToolDefinition } from "@earendil-works/pi-coding-agent";
            export default function (pi: any) {
              pi.registerTool(createBashToolDefinition(process.cwd(), { operations: {
                exec: async (command: string, cwd: string, options: any) => { options.onData(Buffer.from("remote ran: " + command + "\n")); return { exitCode: 0 }; },
              } }));
            }
            """);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("bash", new { command = "uname -a" }) : AnthropicText("done");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "go"]);
        Equal(0, code, "exit; " + stderr);
        Check(ToolResultText(sandbox.Requests[1]).Contains("remote ran: uname -a", StringComparison.Ordinal), "custom operations ran: " + sandbox.Requests[1].Body);
    }

    // event-bus.ts: pi.events is one bus; Node extensions and the native extensions of the session see each other's emissions.
    private static async Task EventsBridge()
    {
        using var sandbox = NodeSandbox("events-bridge");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "events.ts"), Probe + """
            export default function (pi: any) {
              pi.events.on("to-node", (data: any) => { log("node got", data); pi.events.emit("to-native", { echo: data.n + 1 }); });
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        await using var activation = await PiSharp.Cli.Extensions.NativeExtensionActivation.LoadPiAsync(host, CancellationToken.None);
        var received = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = activation.Registry.SharedEventBus.On("to-native", data => received.TrySetResult(data));
        activation.Registry.SharedEventBus.Emit("to-node", PiSharp.Contracts.JsonData.Parse("""{"n":41}"""));
        var data = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Equal("""{"echo":42}""", ((PiSharp.Contracts.JsonData)data!).ToString(), "native listener got the Node emission");
        Equal("""["node got",{"n":41}]""", LogLines(sandbox).Single(), "Node listener got the native emission once");
    }

    // loader.ts runtime actions after loading / runner.ts: registerTool, registerCommand and pi.on after the factory returned take
    // effect in the running session (refreshTools).
    private static async Task LateRegistrations()
    {
        using var sandbox = NodeSandbox("late-registrations");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "late.ts"), Probe + """
            export default function (pi: any) {
              pi.on("session_start", async () => {
                pi.registerCommand("late", { description: "Late", handler: async (args: string) => log("late command", args) });
                pi.registerTool({ name: "late_tool", label: "Late", description: "Late tool", promptSnippet: "A tool registered late",
                  parameters: { type: "object", properties: {} },
                  async execute() { return { content: [{ type: "text", text: "late tool ran" }] }; } });
                pi.on("agent_end", async () => log("late handler"));
              });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/late now"]);
        Equal(0, code, "exit; " + stderr);
        Equal("""["late command","now"]""", LogLines(sandbox).Single(), "late command");
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("late_tool", new { }) : AnthropicText("done");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "go"]);
        Equal(0, code, "exit; " + stderr);
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "late_tool"), "late tool declared");
        Equal("late tool ran", ToolResultText(sandbox.Requests[1]), "late tool executed");
        Check(LogLines(sandbox).Contains("""["late handler"]"""), "late handler ran: " + string.Join("|", LogLines(sandbox)));
    }

    private static string ReloadExtension(string version, string extraCommand) => Probe + $$"""
        export default function (pi: any) {
          pi.on("session_start", async (event: any) => log("{{version}} start", event.reason));
          pi.on("session_shutdown", async (event: any) => log("{{version}} shutdown", event.reason));
          pi.registerCommand("hello", { description: "Hello", handler: async () => log("hello from {{version}}") });
          {{extraCommand}}
          pi.registerCommand("again", { description: "Reload from a command", handler: async (_args: string, ctx: any) => {
            await ctx.reload();
            let stale = "usable";
            try { ctx.cwd; } catch (error: any) { stale = String(error.message).startsWith("This extension ctx is stale") ? "stale" : String(error.message); }
            log("{{version}} ctx after reload", stale);
          } });
        }
        """;

    // agent-session.ts reload(): /reload shuts the extensions down (reason "reload"), loads them again with fresh modules, rereads the
    // prompt templates, skills and context files, and starts them (reason "reload"); ctx.reload() does the same from a command.
    private static async Task Reload()
    {
        using var sandbox = NodeSandbox("reload");
        var path = sandbox.Write(Path.Combine(sandbox.Cwd, "reloadable.ts"), ReloadExtension("v1", ""));
        sandbox.Write(Path.Combine(sandbox.Cwd, "AGENTS.md"), "First context.\n");
        var steps = new Queue<string>([
            """{"id":"hello1","type":"prompt","message":"/hello"}""",
            """{"id":"reload","type":"prompt","message":"/reload"}""",
            """{"id":"hello2","type":"prompt","message":"/hello"}""",
            """{"id":"commands","type":"get_commands"}""",
            """{"id":"ask","type":"prompt","message":"what changed?"}""",
            """{"id":"again","type":"prompt","message":"/again"}"""]);
        var sent = steps.Dequeue();
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", path], [sent],
            (record, all) => IsResponse(record, "again") || record["type"]?.GetValue<string>() == "extension_error",
            react: (record, push) =>
            {
                if (steps.Count == 0) return;
                var current = JsonNode.Parse(sent)!["id"]!.GetValue<string>();
                var next = current == "ask" ? record["type"]?.GetValue<string>() == "agent_settled" : IsResponse(record, current);
                if (!next) return;
                if (current == "hello1")
                {
                    // The extension, a prompt template, a skill and the context file change on disk before /reload.
                    File.WriteAllText(path, ReloadExtension("v2", """pi.registerCommand("bye", { description: "Bye", handler: async () => log("bye") });"""));
                    sandbox.Write(Path.Combine(sandbox.AgentDir, "prompts", "fresh.md"), "---\ndescription: Fresh template\n---\nFresh $@\n");
                    sandbox.Write(Path.Combine(sandbox.AgentDir, "skills", "fresh", "SKILL.md"), "---\nname: fresh\ndescription: Fresh skill\n---\nSteps.\n");
                    File.WriteAllText(Path.Combine(sandbox.Cwd, "AGENTS.md"), "Second context.\n");
                }
                sent = steps.Dequeue(); push(sent);
            });
        Equal(0, code, "exit; " + stderr + "; records " + string.Join("\n", records.Select(item => item.ToJsonString()).Where(text => !text.Contains("message_update", StringComparison.Ordinal))));
        Names(["[\"v1 start\",\"new\"]", "[\"hello from v1\"]", "[\"v1 shutdown\",\"reload\"]", "[\"v2 start\",\"reload\"]", "[\"hello from v2\"]",
            "[\"v2 shutdown\",\"reload\"]", "[\"v2 start\",\"reload\"]", "[\"v2 ctx after reload\",\"stale\"]", "[\"v2 shutdown\",\"quit\"]"], LogLines(sandbox), "reload lifecycle; records " + string.Join("\n",
            records.Select(item => item.ToJsonString()).Where(text => !text.Contains("message_update", StringComparison.Ordinal))));
        var commands = records.Single(record => IsResponse(record, "commands"))["data"]!["commands"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToList();
        Check(commands.Contains("bye") && commands.Contains("fresh") && commands.Contains("skill:fresh"), "reloaded commands: " + string.Join(",", commands));
        var ask = sandbox.Requests.Last(request => request.Body!.Contains("what changed?", StringComparison.Ordinal)).Body!;
        Check(ask.Contains("Second context.", StringComparison.Ordinal), "reloaded context file in the request: " + ask);
    }

    private const string ChatProviderExtension = """
        import { createAssistantMessageEventStream } from "@earendil-works/pi-ai";
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerProvider("acme-chat", {
            baseUrl: "https://acme.invalid/v1", apiKey: "$ACME_KEY", api: "acme-api",
            models: [{ id: "m1", name: "Acme One", reasoning: false, input: ["text"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 8000, maxTokens: 1000 }],
            streamSimple(model: any, context: any, options: any) {
              const stream = createAssistantMessageEventStream();
              const last = context.messages.filter((m: any) => m.role === "user").at(-1);
              log("stream", model.provider, model.id, options?.apiKey ?? null, typeof last.content === "string" ? last.content : last.content[0].text);
              const output: any = { role: "assistant", content: [], api: model.api, provider: model.provider, model: model.id,
                usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0, totalTokens: 2, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
                stopReason: "stop", timestamp: Date.now() };
              queueMicrotask(() => {
                stream.push({ type: "start", partial: output });
                output.content.push({ type: "text", text: "" });
                stream.push({ type: "text_start", contentIndex: 0, partial: output });
                output.content[0].text = "acme says hi";
                stream.push({ type: "text_delta", contentIndex: 0, delta: "acme says hi", partial: output });
                stream.push({ type: "text_end", contentIndex: 0, content: "acme says hi", partial: output });
                stream.push({ type: "done", reason: "stop", message: output });
                stream.end();
              });
              return stream;
            },
          });
          pi.registerCommand("drop", { description: "Unregister", handler: async () => pi.unregisterProvider("acme-chat") });
        }
        """;

    // model-runtime.ts registerProvider + api-registry streamSimple: the provider's models are selectable, its apiKey resolves at
    // request time, and its stream serves the request; unregisterProvider removes the models from the available set.
    private static async Task ChatProvider()
    {
        RequirePiRuntime();
        using var sandbox = NodeSandbox("chat-provider");
        sandbox.Vars["ACME_KEY"] = "acme-secret";
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "acme.ts"), ChatProviderExtension);
        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "acme-chat", "--model", "m1", "-e", extension, "hello acme");
        Equal(0, code, "exit; " + stderr);
        Equal("acme says hi", stdout.Trim(), "the extension stream answered");
        Equal("""["stream","acme-chat","m1","acme-secret","hello acme"]""", LogLines(sandbox).Single(), "stream inputs");
        Check(sandbox.Requests.Count == 0, "no other provider was asked");

        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        bool HasAcme(JsonNode record) => record["data"]!["models"]!.AsArray().Any(model => model!["provider"]?.GetValue<string>() == "acme-chat");
        var probes = 0;
        var (rpcCode, records, rpcError) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"before","type":"get_available_models"}"""],
            (record, _) => IsResponse(record, "after") && !HasAcme(record) || probes > 40,
            react: (record, push) =>
            {
                if (IsResponse(record, "before")) push("""{"id":"drop","type":"prompt","message":"/drop"}""");
                else if (IsResponse(record, "drop") || IsResponse(record, "after") && HasAcme(record))
                { probes++; Thread.Sleep(100); push("""{"id":"after","type":"get_available_models"}"""); }
            });
        Equal(0, rpcCode, "rpc exit; " + rpcError);
        Check(HasAcme(records.First(record => IsResponse(record, "before"))), "acme model available while registered");
        Check(!HasAcme(records.Last(record => IsResponse(record, "after"))), "unregisterProvider removed the models");
    }
}
