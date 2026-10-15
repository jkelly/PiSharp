using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Contracts;

// Pi 1.2.0 through the Node bridge (authored from the v1.2.0 sources, 06a466935536f8c9bcacb2d5efc0fc021be1bdff):
// core/extensions/types.ts (BeforeAgentStartUserEvent, BeforeAgentStartCustomEvent, ToolDefinition.reachesTools, ToolNamespace.aliases,
// ToolInfo.reachesTools), core/extensions/runner.ts (emitBeforeAgentStart over a BeforeAgentStartTrigger) and core/agent-session.ts
// (getAllTools, a custom message with triggerTurn starting a run).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync120Cases() =>
    [
        ("bridge.before-agent-start-user-and-custom-message-runs", BeforeAgentStartTriggers),
        ("bridge.reaches-tools-and-namespace-aliases-in-getAllTools-and-loadouts", ReachesToolsInfo),
        ("bridge.reaches-tools-of-a-typescript-tool-reaches-all", ReachesToolsDescriptor),
        ("bridge.print-mode-exits-while-a-command-triggered-run-starts", TriggeredRunAtPrintModeExit),
    ];

    private const string TriggerExtension = """
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerCommand("kick", { description: "Kick", handler: async (_args: string, ctx: any) => {
            pi.sendMessage({ customType: "kick", content: "kick off", display: true, details: { n: 1 } }, { triggerTurn: true });
            await ctx.waitForIdle();
          } });
          pi.on("before_agent_start", (event: any) => {
            log("start", event.type, event.messageType, "prompt" in event ? event.prompt : "(none)",
              "message" in event ? [event.message.role, event.message.customType, event.message.content, event.message.details] : "(none)",
              typeof event.systemPrompt);
            return { systemPrompt: event.systemPrompt + "\nSTART-" + event.messageType };
          });
        }
        """;

    // types.ts BeforeAgentStartEvent: a run started by a prompt has messageType "user" with the prompt; a run started by
    // pi.sendMessage(..., { triggerTurn: true }) has messageType "custom" with the custom message and no prompt. The handler's
    // system prompt reaches the run's request either way.
    private static async Task BeforeAgentStartTriggers()
    {
        using var sandbox = NodeSandbox("before-agent-start-triggers");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "trigger.ts"), TriggerExtension);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "hello there"]);
        Equal(0, code, "user run exit; " + stderr);
        Equal("""["start","before_agent_start","user","hello there","(none)","string"]""", LogRecords(sandbox, "start").Single().ToJsonString(), "user trigger");
        Check(sandbox.Requests.Single().Json.GetProperty("system").ToString().Contains("START-user", StringComparison.Ordinal), "user run system prompt");

        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        sandbox.Requests.Clear();
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/kick"]);
        Equal(0, code, "custom run exit; " + stderr);
        Equal("""["start","before_agent_start","custom","(none)",["custom","kick","kick off",{"n":1}],"string"]""",
            LogRecords(sandbox, "start").Single().ToJsonString(), "custom trigger; log " + string.Join(" | ", LogLines(sandbox)));
        var request = sandbox.Requests.Single().Json;
        Check(request.GetProperty("system").ToString().Contains("START-custom", StringComparison.Ordinal), "custom run system prompt");
        Check(request.GetProperty("messages").ToString().Contains("kick off", StringComparison.Ordinal), "the custom message starts the run: " + request.GetProperty("messages"));
    }

    private const string ReachExtension = """
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerTool({
            name: "reacher", label: "Reacher", description: "Reaches other tools",
            parameters: { type: "object", properties: { q: { type: "string" } } },
            namespace: { name: "ns__docs", aliases: ["docs"], description: "Docs tools" },
            reachesTools: (input: any) => input.q === "everything" ? "all" : String(input.q ?? "").split(/\s+/).filter(Boolean),
            async execute() { return { content: [{ type: "text", text: "reached" }], details: {} }; },
          });
          pi.registerTool({
            name: "thrower", label: "Thrower", description: "Its reach fails",
            parameters: { type: "object", properties: {} },
            reachesTools: () => { throw new Error("no reach"); },
            async execute() { return { content: [{ type: "text", text: "thrown" }], details: {} }; },
          });
          pi.registerTool({
            name: "probe", label: "Probe", description: "Reads getAllTools",
            parameters: { type: "object", properties: {} },
            prepareLoadout(loadout: any) { log("loadout", loadout.getNamespace("reacher") ?? null, loadout.getNamespace("probe") ?? null); return undefined; },
            async execute() {
              const all = pi.getAllTools();
              const reacher = all.find((t: any) => t.name === "reacher");
              const probe = all.find((t: any) => t.name === "probe");
              log("all", typeof reacher.reachesTools, reacher.reachesTools({ q: "alpha beta" }), reacher.reachesTools({ q: "everything" }),
                reacher.namespace, "reachesTools" in probe, typeof all.find((t: any) => t.name === "thrower").reachesTools,
                all.filter((t: any) => typeof t.reachesTools === "function" && t.name !== "reacher" && t.name !== "thrower")
                  .map((t: any) => [t.name, t.reachesTools({ code: "callFoo(bar)", query: "x" })]));
              return { content: [{ type: "text", text: "probed" }], details: {} };
            },
          });
        }
        """;

    // agent-session.ts getAllTools: ToolInfo.reachesTools is the tool's own function (only for tools that declare it, the built-in
    // codemode and tool_search tools included) and namespace is the registered ToolNamespace with its aliases;
    // ToolLoadout.getNamespace returns the same namespace.
    private static async Task ReachesToolsInfo()
    {
        using var sandbox = NodeSandbox("reaches-tools-info");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "reach.ts"), ReachExtension);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("probe", new { }) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "probe it"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text; log " + string.Join(" | ", LogLines(sandbox)));
        // The built-in codemode and tool_search tools reach as upstream's: getScriptReach (the words of the script) and "all".
        Equal("""["all","function",["alpha","beta"],"all",{"name":"ns__docs","aliases":["docs"],"description":"Docs tools"},false,"function",""" +
            """[["codemode",["callFoo","bar"]],["tool_search","all"]]]""", LogRecords(sandbox, "all").Single().ToJsonString(), "getAllTools");
        Check(LogRecords(sandbox, "loadout").Any(), "prepareLoadout ran; log " + string.Join(" | ", LogLines(sandbox)));
        foreach (var record in LogRecords(sandbox, "loadout"))
            Equal("""["loadout",{"name":"ns__docs","aliases":["docs"],"description":"Docs tools"},null]""", record.ToJsonString(), "ToolLoadout.getNamespace");
    }

    // The registration PiSharp makes for a TypeScript tool: the namespace keeps its aliases, and a tool that declares reachesTools
    // reaches every tool. ExtensionToolDescriptor.ReachesTools is pure and synchronous and runs on the tool-call path, so the tool's
    // own function in Node is not asked there (getAllTools still hands extensions the function itself).
    private static async Task ReachesToolsDescriptor()
    {
        using var sandbox = NodeSandbox("reaches-tools-descriptor");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "reach.ts"), ReachExtension);
        await using var host = await StartHost(sandbox, extension);
        var loaded = host.Extensions.Single();
        var tools = (loaded.Descriptor["tools"] as JsonArray)!.OfType<JsonObject>().ToDictionary(tool => tool["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Equal(true, tools["reacher"]["hasReachesTools"]!.GetValue<bool>(), "reacher reports reachesTools");
        Check(tools["probe"]["hasReachesTools"] is null, "probe declares no reachesTools");
        var owner = new PiNodeOwner(host, loaded);
        var reacher = owner.Tool(tools["reacher"]);
        Equal("ns__docs", reacher.Namespace!.Name, "namespace");
        Names(["docs"], reacher.Namespace.Aliases, "namespace aliases");
        Equal("Docs tools", reacher.Namespace.Description, "namespace description");
        Check(reacher.ReachesTools!(JsonData.Parse("""{"q":"alpha beta"}""")).IsAll, "a TypeScript tool's reach is all");
        Check(owner.Tool(tools["thrower"]).ReachesTools!(JsonData.Parse("{}")).IsAll, "also when its own function would fail");
        Check(owner.Tool(tools["probe"]).ReachesTools is null, "no reachesTools");
        Check(owner.Tool(tools["probe"]).Namespace is null, "no namespace");
    }

    // print-mode.ts: session.prompt resolves once a command's handler returns, so a run the handler started with
    // pi.sendMessage(..., { triggerTurn: true }) without awaiting ctx.waitForIdle() is still starting when print mode reads the last
    // message and disposes the runtime. AgentSessionRuntime.dispose emits session_shutdown and AgentSession.dispose aborts the agent
    // and drops the session's listeners, so Pi 1.2.0 exits 0 and prints nothing of that run: no text in text mode, the session
    // header alone in JSON mode. (Observed with the 1.2.0 npm packages: exit 0, empty stdout and stderr, no model request.)
    private static async Task TriggeredRunAtPrintModeExit()
    {
        using var sandbox = NodeSandbox("triggered-run-at-print-exit");
        // A late answer: a run that reached the model before print mode read the last message still has no assistant text there.
        sandbox.Respond = (_, _) => { Thread.Sleep(2000); return AnthropicText("late answer"); };
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "kick.ts"), """
            import { appendFileSync } from "node:fs";
            const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
            export default function (pi: any) {
              pi.on("session_shutdown", () => log("session_shutdown"));
              pi.registerCommand("kick", { description: "Kick", handler: async () => {
                pi.sendMessage({ customType: "kick", content: "go now", display: true }, { triggerTurn: true });
                log("handler_end");
              } });
            }
            """);
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/kick"]);
        Equal(0, code, "text mode exit; " + stderr);
        Equal("", stdout, "text mode prints nothing of the run");
        Equal("", stderr, "text mode stderr");
        var lines = LogLines(sandbox);
        Check(lines.Contains("""["handler_end"]""") && lines.Contains("""["session_shutdown"]"""), "handler and shutdown ran: " + string.Join(" | ", lines));

        File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
        (code, stdout, stderr) = await sandbox.Run([.. new[] { "--mode", "json" }, .. Model, "-e", extension, "/kick"]);
        Equal(0, code, "json mode exit; " + stderr);
        Equal("", stderr, "json mode stderr");
        var records = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Equal(1, records.Length, "json mode prints the session header alone: " + stdout);
        Equal("session", JsonNode.Parse(records[0])!["type"]!.GetValue<string>(), "the header");
    }
}
