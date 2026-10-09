using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Cli.Pi;

// The Node bridge end to end through PiCommand.RunAsync with a real Node (every case here needs Node.js 22.13+ and skips without it):
// arbitrary TypeScript extensions loaded with -e, their tools called by the model, their event handlers, commands, flags, actions and
// UI requests, with a fake Anthropic endpoint. Expectations follow core/extensions/{loader,runner,types}.ts.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> BridgeCases() =>
    [
        ("bridge.typescript-extension-tool-called-by-the-model", TypeScriptTool),
        ("bridge.tool-arguments-validated-as-upstream-for-typebox-and-raw-schemas", ValidatedToolArguments),
        ("bridge.extension-api-members-in-a-print-run", ApiMembers),
        ("bridge.events-reach-node-handlers", EventsReachNode),
        ("bridge.command-from-the-prompt-with-actions", CommandFromPrompt),
        ("bridge.flags-in-help-values-and-unknown-options", Flags),
        ("bridge.load-failure-stops-the-run-with-hint", LoadFailure),
        ("bridge.project-extensions-need-project-trust", ProjectTrustGating),
        ("bridge.discovery-global-folder-and-settings-entries", DiscoveryFolders),
        ("bridge.rpc-ui-dialog-round-trip", RpcDialog),
        ("bridge.virtual-model-routes-each-request", VirtualModel),
        ("bridge.renderers-markdown-and-shortcuts-through-the-host", Renderers),
        ("bridge.virtual-modules-for-upstream-imports", VirtualModules),
    ];

    private static async Task<PiSharp.Cli.Extensions.Pi.PiExtensionHost> StartHost(Sandbox sandbox, string extension)
    {
        var host = await PiSharp.Cli.Extensions.Pi.PiExtensionHost.StartAsync(new(sandbox.Cwd, sandbox.AgentDir, "tui", true)
        { GetEnvironment = name => sandbox.Vars.GetValueOrDefault(name) }, CancellationToken.None);
        await host.LoadAsync([extension], CancellationToken.None);
        Check(host.Errors.IsEmpty, "load errors: " + string.Join("; ", host.Errors.Select(error => error.Error)));
        return host;
    }

    // types.ts registerMessageRenderer/registerEntryRenderer/registerMarkdownTransformer/registerShortcut: pi-tui components built in
    // Node render to rows the host draws; the shortcut handler runs with a context.
    private static async Task Renderers()
    {
        using var sandbox = NodeSandbox("renderers");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "render.ts"), """
            import { Text, Container, Spacer } from "@earendil-works/pi-tui";
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              pi.registerMessageRenderer("note", (message: any, options: any, theme: any) => {
                const box = new Container(); box.addChild(new Text("NOTE: " + message.content, 0, 0)); box.addChild(new Spacer(1)); return box;
              });
              pi.registerEntryRenderer("marker", (entry: any) => new Text("ENTRY " + entry.data.n, 1, 0));
              pi.registerMarkdownTransformer((markdown: string) => markdown.replace("TODO", "DONE"));
              pi.registerShortcut("ctrl+shift+k", { description: "Probe shortcut", handler: (ctx: any) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["shortcut", ctx.mode]) + "\n") });
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        var rows = await host.RenderMessageAsync("note", new JsonObject { ["role"] = "custom", ["customType"] = "note", ["content"] = "hello" }, 20, false, CancellationToken.None);
        Names(["NOTE: hello", ""], rows!.Value.Select(row => row.TrimEnd()), "message renderer rows");
        Check(await host.RenderMessageAsync("other", new JsonObject(), 20, false, CancellationToken.None) is null, "unrendered custom type");
        var entry = await host.RenderEntryAsync("marker", new JsonObject { ["data"] = new JsonObject { ["n"] = 7 } }, 20, false, CancellationToken.None);
        Equal("ENTRY 7", entry!.Value.Single().Trim(), "entry renderer");
        Equal("all DONE", await host.TransformMarkdownAsync("all TODO", null, CancellationToken.None), "markdown transformer");
        var shortcut = host.Shortcuts.Single();
        Equal("ctrl+shift+k", shortcut.Shortcut, "shortcut key"); Equal("Probe shortcut", shortcut.Description, "shortcut description");
    }

    // virtual-modules.ts: the runtime values extensions import from Pi's packages resolve in the bridge (typebox, pi-ai StringEnum,
    // pi-tui helpers, pi-coding-agent helpers), so upstream-style extensions load unchanged.
    private static async Task VirtualModules()
    {
        using var sandbox = NodeSandbox("virtual-modules");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "imports.ts"), """
            import { Type } from "typebox";
            import { StringEnum } from "@earendil-works/pi-ai";
            import { truncateToWidth, visibleWidth, matchesKey, Key } from "@earendil-works/pi-tui";
            import { defineTool, isToolCallEventType, CONFIG_DIR_NAME, getAgentDir, truncateHead, formatSize } from "@mariozechner/pi-coding-agent";
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              const tool = defineTool({ name: "shaped", label: "Shaped", description: "TypeBox schema",
                parameters: Type.Object({ mode: StringEnum(["a", "b"] as const), count: Type.Optional(Type.Number()) }),
                async execute() { return { content: [{ type: "text", text: "ok" }], details: {} }; } });
              pi.registerTool(tool);
              appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["imports", visibleWidth(truncateToWidth("abcdefgh", 4)), typeof matchesKey, typeof Key,
                isToolCallEventType("bash", { type: "tool_call", toolName: "bash", input: {} }), CONFIG_DIR_NAME, getAgentDir() === process.env.PI_CODING_AGENT_DIR,
                formatSize(2048), truncateHead("a\nb\nc", { maxLines: 2 }).truncated]) + "\n");
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        var parameters = host.Extensions.Single().Descriptor["tools"]![0]!["parameters"]!;
        Equal("""{"type":"object","required":["mode"],"properties":{"mode":{"type":"string","enum":["a","b"]},"count":{"type":"number"}}}""",
            JsonNode.Parse(parameters.ToJsonString())!.ToJsonString(), "TypeBox schema as JSON");
        var record = LogRecords(sandbox, "imports").Single();
        Equal("""["imports",4,"function","object",true,".pi",true,"2.0KB",true]""", record.ToJsonString(), "virtual module values");
    }

    private static Sandbox NodeSandbox(string name, bool trusted = true)
    {
        RequireNode();
        var sandbox = new Sandbox(name, trusted);
        foreach (var variable in new[] { "PATH", "PATHEXT", "SystemRoot", "PISHARP_NODE" })
            if (Environment.GetEnvironmentVariable(variable) is { } value) sandbox.Vars[variable] = value;
        return sandbox;
    }

    private static readonly string[] Model = ["--provider", "anthropic", "--model", "claude-sonnet-4-5"];

    private static string[] LogLines(Sandbox sandbox, string name = "probe.log")
    {
        var path = Path.Combine(sandbox.Cwd, name);
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }
    private static IEnumerable<JsonArray> LogRecords(Sandbox sandbox, string kind) =>
        LogLines(sandbox).Select(line => JsonNode.Parse(line)!.AsArray()).Where(record => record[0]!.GetValue<string>() == kind);

    private const string HelloExtension = """
        import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
        enum Greeting { Plain = "Hello" }
        class Greeter { constructor(private readonly prefix: string) {} greet(name: string): string { return `${this.prefix}, ${name}!`; } }
        export default function (pi: ExtensionAPI) {
          pi.registerTool({
            name: "greet", label: "Greet", description: "Greets someone",
            parameters: { type: "object", properties: { name: { type: "string" } }, required: ["name"] },
            async execute(toolCallId: string, params: { name: string }, _signal: AbortSignal | undefined, onUpdate: any, ctx: any) {
              const entries = ctx.sessionManager.getEntries().length;
              return { content: [{ type: "text", text: new Greeter(Greeting.Plain).greet(params.name) + " entries=" + entries + " id=" + toolCallId }], details: { cwd: ctx.cwd } };
            },
          });
        }
        """;

    // loader.ts with jiti: a TypeScript file (enum, parameter properties, type imports) loads; agent-loop: the tool result reaches the
    // next request as the tool_result content.
    private static async Task TypeScriptTool()
    {
        using var sandbox = NodeSandbox("ts-tool");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "hello.ts"), HelloExtension);
        sandbox.Respond = (seen, index) => index == 0 ? AnthropicToolCall("greet", new { name = "Ada" }) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "Say hello"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text");
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "greet"), "tool declared");
        var text = ToolResultText(sandbox.Requests[1]);
        Check(text.StartsWith("Hello, Ada! entries=", StringComparison.Ordinal) && text.EndsWith("id=toolu_01", StringComparison.Ordinal), "tool text: " + text);
    }

    // agent-loop prepareToolCall + validation.ts validateToolArguments for extension tools: a Type.* schema (TypeBox's hidden "~kind"
    // markers reach PiSharp through the bridge) is also converted by Value.Convert ("a" -> ["a"]); a raw-object schema gets the
    // JSON-schema coercion only, so the same value fails. Expected texts were captured from the real pi-ai 1.1.0 / typebox 1.3.27.
    private static async Task ValidatedToolArguments()
    {
        using var sandbox = NodeSandbox("validated-args");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "validated.ts"), """
            import { Type } from "typebox";
            import { appendFileSync } from "node:fs";
            const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
            export default function (pi: any) {
              pi.registerTool({ name: "typed", label: "Typed", description: "TypeBox schema",
                parameters: Type.Object({ n: Type.Number(), when: Type.Optional(Type.Boolean()), tags: Type.Optional(Type.Array(Type.String())) }),
                async execute(_id: string, params: any) { log("typed", params); return { content: [{ type: "text", text: "typed ok" }], details: {} }; } });
              pi.registerTool({ name: "raw", label: "Raw", description: "Raw JSON schema",
                parameters: { type: "object", properties: { n: { type: "integer" }, tags: { type: "array", items: { type: "string" } } }, required: ["n"] },
                async execute(_id: string, params: any) { log("raw", params); return { content: [{ type: "text", text: "raw ok" }], details: {} }; } });
              pi.registerTool({ name: "prepared", label: "Prepared", description: "prepareArguments shim",
                parameters: Type.Object({ n: Type.Number() }),
                prepareArguments(args: any) { if (args.fail) throw new Error("prepare refused " + args.fail); return { n: args.count }; },
                async execute(_id: string, params: any) { log("prepared", params); return { content: [{ type: "text", text: "prepared ok" }], details: {} }; } });
            }
            """);
        sandbox.Respond = (_, index) => index switch
        {
            0 => AnthropicToolCall("typed", new { n = "5", when = "true", tags = "a" }, "toolu_1"),
            1 => AnthropicToolCall("typed", new { n = "x" }, "toolu_2"),
            2 => AnthropicToolCall("raw", new { n = "7", tags = "a" }, "toolu_3"),
            3 => AnthropicToolCall("raw", new { n = "7" }, "toolu_4"),
            4 => AnthropicToolCall("prepared", new { count = "3" }, "toolu_5"),
            5 => AnthropicToolCall("prepared", new { fail = "x" }, "toolu_6"),
            _ => AnthropicText("done")
        };
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "Validate"]);
        Equal(0, code, "exit; " + stderr);
        Equal("""[["typed",{"n":5,"when":true,"tags":["a"]}],["raw",{"n":7}],["prepared",{"n":3}]]""",
            new JsonArray([.. LogLines(sandbox).Select(line => JsonNode.Parse(line))]).ToJsonString(), "the tools received the coerced arguments");
        Equal("typed ok", ToolResultText(sandbox.Requests[1]), "typed result");
        Equal("Validation failed for tool \"typed\":\n  - n: must be number\n\nReceived arguments:\n{\n  \"n\": \"x\"\n}", ToolResultText(sandbox.Requests[2]), "typed failure");
        Equal("Validation failed for tool \"raw\":\n  - tags: must be array\n\nReceived arguments:\n{\n  \"n\": \"7\",\n  \"tags\": \"a\"\n}", ToolResultText(sandbox.Requests[3]), "raw failure");
        Equal("raw ok", ToolResultText(sandbox.Requests[4]), "raw result");
        // prepareToolCall runs prepareArguments before the schema check, and an error it throws is the result's text.
        Equal("prepared ok", ToolResultText(sandbox.Requests[5]), "prepared result");
        Equal("prepare refused x", ToolResultText(sandbox.Requests[6]), "prepareArguments error");
    }

    private static string ToolResultText(Seen request)
    {
        var block = request.Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").EnumerateArray().First(item => item.GetProperty("type").GetString() == "tool_result");
        var content = block.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(item => item.TryGetProperty("text", out var t) ? t.GetString() : ""));
    }

    private const string ProbeExtension = """
        import { appendFileSync } from "node:fs";
        import { join } from "node:path";
        const log = (...items: unknown[]) => appendFileSync(join(process.cwd(), "probe.log"), JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerFlag("probe-mode", { type: "string", default: "quiet", description: "Probe mode" });
          pi.registerFlag("probe-loud", { type: "boolean", description: "Loud probe" });
          pi.registerTool({
            name: "probe", label: "Probe", description: "Reads the extension API",
            parameters: { type: "object", properties: { count: { type: "number" } }, required: ["count"] },
            annotations: { readOnlyHint: true, openWorldHint: false },
            async execute(id: string, params: { count: number }, signal: AbortSignal, onUpdate: any, ctx: any) {
              onUpdate?.({ content: [{ type: "text", text: "partial" }] });
              const exec = await pi.exec(process.execPath, ["-e", "process.stdout.write('hi')"]);
              log("tool", {
                count: params.count, countType: typeof params.count, active: pi.getActiveTools(),
                all: pi.getAllTools().filter((t: any) => t.name === "probe" || t.name === "read").map((t: any) => [t.name, t.annotations ?? null, t.exposure]),
                mode: pi.getFlag("probe-mode"), loud: pi.getFlag("probe-loud"), unregistered: pi.getFlag("nope") ?? null,
                thinking: pi.getThinkingLevel(), model: ctx.model?.id, provider: ctx.model?.provider, idle: ctx.isIdle(), ctxMode: ctx.mode, hasUI: ctx.hasUI,
                cwd: ctx.cwd === process.cwd(), header: ctx.sessionManager.getHeader()?.type, leaf: typeof ctx.sessionManager.getLeafId(),
                branch: ctx.sessionManager.getBranch().length > 0, file: typeof ctx.sessionManager.getSessionFile(), exec: exec.stdout, code: exec.code,
                systemPrompt: ctx.getSystemPrompt().includes("PROBE-SYSTEM"), trusted: ctx.isProjectTrusted(), pending: ctx.hasPendingMessages(),
              });
              pi.appendEntry("probe-entry", { n: 1 });
              pi.setSessionName("Probe\nsession");
              return { content: [{ type: "text", text: "probe raw" }], details: { ok: true } };
            },
          });
          pi.on("tool_call", (event: any) => { log("tool_call", event.toolName); if (event.toolName === "bash") return { block: true, reason: "Blocked by probe" }; });
          pi.on("tool_call", (event: any) => { if (event.toolName === "probe") event.input.count = Number(event.input.count) + 1; });
          pi.on("input", (event: any) => event.text.startsWith("rewrite:") ? { action: "transform", text: event.text.slice(8) } : undefined);
          pi.on("before_agent_start", (event: any) => ({ systemPrompt: event.systemPrompt + "\nPROBE-SYSTEM", message: { customType: "probe", content: "probe context", display: false } }));
          pi.on("tool_result", (event: any) => event.toolName === "probe" ? { content: [{ type: "text", text: "rewritten " + event.content[0].text }] } : undefined);
          pi.on("session_start", (event: any, ctx: any) => log("session_start", event.reason, ctx.hasUI));
          pi.events.on("probe:bus", (data: any) => log("bus", data));
          pi.events.emit("probe:bus", { ok: true });
        }
        """;

    // types.ts ExtensionAPI: registerTool (annotations, exposure), getFlag/registerFlag, getActiveTools/getAllTools, getThinkingLevel,
    // exec, appendEntry, setSessionName; ctx getters; tool_call (block, in-place input edit), input transform, before_agent_start
    // (system prompt and message), tool_result rewrite, session_start and the event bus.
    private static async Task ApiMembers()
    {
        using var sandbox = NodeSandbox("api");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "probe.ts"), ProbeExtension);
        sandbox.Respond = (seen, index) => index switch
        {
            0 => AnthropicToolCall("probe", new { count = "41" }),
            1 => AnthropicToolCall("bash", new { command = "echo hi" }, "toolu_02"),
            _ => AnthropicText("done")
        };
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "--probe-mode", "loud-mode", "rewrite:Hello there", "--probe-loud"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text; requests " + sandbox.Requests.Count + "; stderr " + stderr + "; log " + string.Join(" | ", LogLines(sandbox)));
        var first = sandbox.Requests[0].Json;
        var userText = first.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user").GetProperty("content");
        Check(userText.ToString().Contains("Hello there", StringComparison.Ordinal) && !userText.ToString().Contains("rewrite:", StringComparison.Ordinal), "input transform: " + userText);
        Check(first.GetProperty("system").ToString().Contains("PROBE-SYSTEM", StringComparison.Ordinal), "before_agent_start system prompt");
        Check(first.GetProperty("messages").ToString().Contains("probe context", StringComparison.Ordinal), "before_agent_start message in context");
        Equal("rewritten probe raw", ToolResultText(sandbox.Requests[1]), "tool_result rewrite");
        Check(ToolResultText(sandbox.Requests[2]).Contains("Blocked by probe", StringComparison.Ordinal), "tool_call block reason: " + ToolResultText(sandbox.Requests[2]) + "; log " + string.Join(" | ", LogLines(sandbox)) + "; request " + sandbox.Requests[2].Body);
        var tool = LogRecords(sandbox, "tool").Single()[1]!.AsObject();
        Equal(42.0, tool["count"]!.GetValue<double>(), "validateToolArguments coerced the string and tool_call edited the input");
        Equal("number", tool["countType"]!.GetValue<string>(), "coerced type");
        Check(tool["active"]!.AsArray().Select(n => n!.GetValue<string>()).Contains("probe"), "active tools");
        Equal("""[["read",null,"direct"],["probe",{"readOnlyHint":true,"openWorldHint":false},"direct"]]""", tool["all"]!.ToJsonString(), "getAllTools with annotations");
        Equal("loud-mode", tool["mode"]!.GetValue<string>(), "string flag value");
        Equal(true, tool["loud"]!.GetValue<bool>(), "boolean flag value");
        Check(tool["unregistered"] is null, "unregistered flag");
        Equal("claude-sonnet-4-5", tool["model"]!.GetValue<string>(), "ctx.model");
        Equal("print", tool["ctxMode"]!.GetValue<string>(), "ctx.mode");
        Equal(false, tool["hasUI"]!.GetValue<bool>(), "print mode has no UI");
        Equal("hi", tool["exec"]!.GetValue<string>(), "pi.exec stdout");
        Equal("session", tool["header"]!.GetValue<string>(), "sessionManager.getHeader");
        Equal(true, tool["systemPrompt"]!.GetValue<bool>(), "ctx.getSystemPrompt after before_agent_start");
        Equal(false, tool["idle"]!.GetValue<bool>(), "not idle while a tool runs");
        Check(LogRecords(sandbox, "bus").Any(), "event bus delivery");
        Check(LogRecords(sandbox, "session_start").Any(record => record[1]!.GetValue<string>() == "startup" && !record[2]!.GetValue<bool>()), "session_start startup (main.ts initial runtime), no UI");
        var session = File.ReadAllLines(sandbox.SessionFiles().Single()).Select(line => JsonNode.Parse(line)!).ToList();
        Check(session.Any(entry => entry["type"]?.GetValue<string>() == "custom" && entry["customType"]?.GetValue<string>() == "probe-entry" && entry["data"]?["n"]?.GetValue<int>() == 1), "appendEntry custom entry");
        Check(session.Any(entry => entry["type"]?.GetValue<string>() == "session_info" && entry["name"]?.GetValue<string>() == "Probe session"), "setSessionName sanitized");
    }

    private static readonly string[] AllEvents =
    [
        "project_trust", "resources_discover", "session_start", "session_info_changed", "session_before_switch", "session_before_fork",
        "session_before_compact", "session_compact", "session_compact_failed", "session_shutdown", "mcp_servers_change", "session_before_tree",
        "session_tree", "context", "context_with_system", "cache_warming_decision", "before_provider_request", "before_provider_headers",
        "after_provider_response", "provider_stream_event", "before_agent_start", "agent_start", "agent_end", "agent_before_settle", "agent_settled",
        "ui_prompt_start", "ui_prompt_end", "turn_start", "turn_end", "message_start", "message_update", "message_end", "tool_execution_start",
        "tool_execution_update", "tool_execution_end", "model_select", "thinking_level_select", "tool_call", "tool_result", "user_bash", "input"
    ];

    // types.ts: every catalog event can be subscribed with pi.on; the events a print run raises reach the Node handlers.
    private static async Task EventsReachNode()
    {
        using var sandbox = NodeSandbox("events");
        var events = string.Join(", ", AllEvents.Select(name => "\"" + name + "\""));
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "events.ts"), $$"""
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              for (const name of [{{events}}]) pi.on(name, (event: any) => { appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["event", name, event.type ?? null]) + "\n"); });
              pi.registerTool({ name: "noop", label: "Noop", description: "No-op", parameters: { type: "object", properties: {} },
                async execute(id: string, p: any, s: any, onUpdate: any) { onUpdate?.({ content: [{ type: "text", text: "..." }] }); return { content: [{ type: "text", text: "ok" }], details: {} }; } });
            }
            """);
        sandbox.Respond = (seen, index) => index == 0 ? AnthropicToolCall("noop", new { }) : AnthropicText("done");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "go"]);
        Equal(0, code, "exit; " + stderr);
        var seen = LogRecords(sandbox, "event").Select(record => record[1]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        string[] expected = ["session_start", "input", "before_agent_start", "agent_start", "turn_start", "context", "before_provider_request",
            "before_provider_headers", "after_provider_response", "provider_stream_event", "message_start", "message_update", "message_end",
            "tool_call", "tool_execution_start", "tool_execution_end", "tool_result", "turn_end", "agent_end", "agent_before_settle", "agent_settled", "session_shutdown"];
        var missing = expected.Where(name => !seen.Contains(name)).ToArray();
        Check(missing.Length == 0, "events not delivered to Node: " + string.Join(", ", missing) + "; seen: " + string.Join(", ", seen));
        // Each Node handler receives the upstream event object (its type matches the subscribed name).
        Check(LogRecords(sandbox, "event").All(record => record[2] is null || record[2]!.GetValue<string>() == record[1]!.GetValue<string>()), "event.type");
    }

    // types.ts registerCommand: "/name args" runs the handler (no model request); pi.sendMessage without triggerTurn records a custom message.
    private static async Task CommandFromPrompt()
    {
        using var sandbox = NodeSandbox("command");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "command.ts"), """
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              pi.registerCommand("probe-cmd", { description: "Probe command", handler: async (args: string, ctx: any) => {
                appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["command", args, ctx.hasUI, typeof ctx.waitForIdle, typeof ctx.newSession]) + "\n");
                pi.sendMessage({ customType: "probe-msg", content: "from command", display: true });
              } });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/probe-cmd one two"]);
        Equal(0, code, "exit; " + stderr);
        Equal(0, sandbox.Requests.Count, "a command does not reach the model");
        var record = LogRecords(sandbox, "command").Single();
        Equal("one two", record[1]!.GetValue<string>(), "command arguments");
        Equal("function", record[3]!.GetValue<string>(), "command context waitForIdle");
        Equal("function", record[4]!.GetValue<string>(), "command context newSession");
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(50);
        Check(condition(), what);
    }

    // main.ts: --help lists the extensions' flags; applyExtensionFlagValues: an unknown option is an error, a string flag needs a value.
    private static async Task Flags()
    {
        using var sandbox = NodeSandbox("flags");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "probe.ts"), ProbeExtension);
        var (code, stdout, stderr) = await sandbox.Run("-e", extension, "--help");
        Equal(0, code, "help exit; " + stderr);
        Check(stdout.Contains("Extension CLI Flags:", StringComparison.Ordinal) && stdout.Contains("--probe-mode", StringComparison.Ordinal), "help lists flags: " + stdout[^Math.Min(600, stdout.Length)..]);
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "--no-such-flag", "x", "hi"]);
        Equal(1, code, "unknown option exit");
        Check(stderr.Contains("Unknown option: --no-such-flag", StringComparison.Ordinal), "unknown option: " + stderr);
        Equal(0, sandbox.Requests.Count, "no request after the error");
    }

    // main.ts: a failing extension is a runtime error: "Failed to load extension" and the -ne hint, exit 1.
    private static async Task LoadFailure()
    {
        using var sandbox = NodeSandbox("load-failure");
        var broken = sandbox.Write(Path.Combine(sandbox.Cwd, "broken.ts"), "export default function (pi: any) { throw new Error(\"factory exploded\"); }\n");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", broken, "hi"]);
        Equal(1, code, "exit");
        Check(stderr.Contains($"Failed to load extension \"{broken}\": Failed to load extension: factory exploded", StringComparison.Ordinal), "error text: " + stderr);
        Check(stderr.Contains("Hint: Start without extensions using \"pisharp -ne\".", StringComparison.Ordinal), "hint: " + stderr);
        var missing = Path.Combine(sandbox.Cwd, "missing.ts");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", missing, "hi"]);
        Equal(1, code, "missing exit");
        Check(stderr.Contains("Extension path does not exist", StringComparison.Ordinal), "missing path: " + stderr);
    }

    private const string MarkerExtension = """
        import { appendFileSync } from "node:fs";
        export default function (pi: any) { appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["loaded", "MARKER"]) + "\n"); }
        """;

    // decision 0004 / trust-manager.ts: .pi/extensions loads only for a trusted project; user extensions load either way.
    private static async Task ProjectTrustGating()
    {
        using var sandbox = NodeSandbox("trust", trusted: false);
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "extensions", "project.ts"), MarkerExtension.Replace("MARKER", "project", StringComparison.Ordinal));
        sandbox.Write(Path.Combine(sandbox.AgentDir, "extensions", "user.ts"), MarkerExtension.Replace("MARKER", "user", StringComparison.Ordinal));
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--no-approve", "hi"]);
        Equal(0, code, "untrusted exit; " + stderr);
        Names(["user"], LogRecords(sandbox, "loaded").Select(record => record[1]!.GetValue<string>()), "untrusted: user extension only");
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--approve", "hi"]);
        Equal(0, code, "trusted exit; " + stderr);
        Names(["user", "project"], LogRecords(sandbox, "loaded").Select(record => record[1]!.GetValue<string>()), "trusted: user then project (load order); both loaded");
        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "--approve", "-ne", "hi"]);
        Equal(0, code, "-ne exit; " + stderr);
        Equal(0, LogLines(sandbox).Length, "--no-extensions loads none");
    }

    // loader.ts discoverExtensionsInDir and settings "extensions": direct files, a folder with index.ts, a package.json pi manifest.
    private static async Task DiscoveryFolders()
    {
        using var sandbox = NodeSandbox("discovery");
        var extensions = Path.Combine(sandbox.AgentDir, "extensions");
        sandbox.Write(Path.Combine(extensions, "a.ts"), MarkerExtension.Replace("MARKER", "file", StringComparison.Ordinal));
        sandbox.Write(Path.Combine(extensions, "folder", "index.ts"), MarkerExtension.Replace("MARKER", "index", StringComparison.Ordinal));
        sandbox.Write(Path.Combine(extensions, "pkg", "package.json"), """{ "name": "pkg", "pi": { "extensions": ["./src/main.ts"] } }""");
        sandbox.Write(Path.Combine(extensions, "pkg", "src", "main.ts"), MarkerExtension.Replace("MARKER", "manifest", StringComparison.Ordinal));
        sandbox.Write(Path.Combine(extensions, "notes.md"), "not an extension");
        var configured = sandbox.Write(Path.Combine(sandbox.Root, "elsewhere", "configured.js"), "module.exports = function (pi) { require('node:fs').appendFileSync(process.cwd() + '/probe.log', JSON.stringify(['loaded', 'settings']) + '\\n'); };\n");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), JsonSerializer.Serialize(new { extensions = new[] { configured } }));
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "hi"]);
        Equal(0, code, "exit; " + stderr);
        Names(["settings", "file", "index", "manifest"], LogRecords(sandbox, "loaded").Select(record => record[1]!.GetValue<string>()), "user settings entries before agentDir/extensions");
    }

    private const string DialogExtension = """
        export default function (pi: any) {
          pi.registerCommand("ask", { description: "Ask", handler: async (args: string, ctx: any) => {
            const choice = await ctx.ui.select("Pick one", ["red", "blue"]);
            ctx.ui.notify("picked " + choice, "info");
          } });
        }
        """;

    // rpc-mode.ts: ctx.ui.select becomes an extension_ui_request; the client's extension_ui_response resolves it; notify is published.
    private static async Task RpcDialog()
    {
        using var sandbox = NodeSandbox("rpc-dialog");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "ask.ts"), DialogExtension);
        var input = new LineInput();
        input.Push("{\"id\":\"1\",\"type\":\"prompt\",\"message\":\"/ask\"}");
        var lines = new List<string>(); var notified = false; var responded = false;
        using var output = new LineOutput(line =>
        {
            lock (lines) lines.Add(line);
            var record = JsonNode.Parse(line)!;
            if (record["type"]?.GetValue<string>() == "extension_ui_request" && record["method"]?.GetValue<string>() == "select")
                input.Push(JsonSerializer.Serialize(new { type = "extension_ui_response", id = record["id"]!.GetValue<string>(), value = "blue" }));
            if (record["type"]?.GetValue<string>() == "extension_ui_request" && record["method"]?.GetValue<string>() == "notify") notified = true;
            if (record["type"]?.GetValue<string>() == "response" && record["command"]?.GetValue<string>() == "prompt") responded = true;
            if (notified && responded) input.Complete();
        });
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var code = await PiCommand.RunAsync(["--mode", "rpc", .. Model, "-e", extension], host, deadline.Token);
        Equal(0, code, "exit; " + stderr + "; output: " + string.Join("\n", lines));
        string[] all; lock (lines) all = [.. lines];
        Check(all.Any(line => line.Contains("\"method\":\"select\"", StringComparison.Ordinal) && line.Contains("Pick one", StringComparison.Ordinal)), "select request: " + string.Join("\n", all));
        Check(all.Any(line => line.Contains("\"method\":\"notify\"", StringComparison.Ordinal) && line.Contains("picked blue", StringComparison.Ordinal)), "notify with the answer: " + string.Join("\n", all));
    }

    // virtual-models.ts / agent-session.ts: pi.registerVirtualModel; --model provider/id selects it and each request asks the router.
    private static async Task VirtualModel()
    {
        using var sandbox = NodeSandbox("virtual");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "router.ts"), """
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              pi.registerVirtualModel({
                provider: "router", id: "auto", name: "Auto router",
                route(request: any, ctx: any) {
                  appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["route", request.reason, request.model.id]) + "\n");
                  return { model: { provider: "anthropic", id: "claude-sonnet-4-5" }, thinkingLevel: "off", state: { calls: 1 } };
                },
              });
            }
            """);
        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "router", "--model", "auto", "-e", extension, "hi");
        Equal(0, code, "exit; " + stderr);
        Equal("ok", stdout.Trim(), "answer through the routed model");
        Equal("claude-sonnet-4-5", sandbox.Requests.Single().Json.GetProperty("model").GetString(), "physical model requested");
        Check(LogRecords(sandbox, "route").Any(record => record[2]!.GetValue<string>() == "auto"), "router asked: " + string.Join("\n", LogLines(sandbox)));
    }

    /// <summary>RPC standard input fed line by line; it ends when completed.</summary>
    private sealed class LineInput : Stream
    {
        private readonly Channel<byte[]> lines = Channel.CreateUnbounded<byte[]>();
        private byte[] current = []; private int offset;
        public void Push(string line) => lines.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));
        public void Complete() => lines.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (offset >= current.Length)
            {
                if (!await lines.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (lines.Reader.TryRead(out var next)) { current = next; offset = 0; }
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>RPC standard output that reports each complete line.</summary>
    private sealed class LineOutput(Action<string> onLine) : MemoryStream
    {
        private readonly StringBuilder pending = new();
        private int depth;
        public override void Write(byte[] buffer, int offset, int count) { depth++; try { base.Write(buffer, offset, count); } finally { depth--; } if (depth == 0) Feed(buffer.AsSpan(offset, count)); }
        public override void Write(ReadOnlySpan<byte> buffer) { depth++; try { base.Write(buffer); } finally { depth--; } if (depth == 0) Feed(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Write(buffer.AsSpan(offset, count)); return Task.CompletedTask; }
        private void Feed(ReadOnlySpan<byte> bytes)
        {
            List<string> complete = [];
            lock (pending)
            {
                pending.Append(Encoding.UTF8.GetString(bytes));
                string text;
                while ((text = pending.ToString()).IndexOf('\n') is var end && end >= 0)
                { complete.Add(text[..end].TrimEnd('\r')); pending.Remove(0, end + 1); }
            }
            foreach (var line in complete) if (line.Length > 0) onLine(line);
        }
    }
}
