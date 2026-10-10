// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent-loop.ts, packages/coding-agent/src/core/extensions/runner.ts,
// packages/coding-agent/src/core/session-manager.ts and packages/ai/src/api/anthropic-messages.ts.
using System.Text.Json;

// Extension values travel as JavaScript values: a tool's result text may hold a lone surrogate (agent-loop.ts keeps it, the session line
// writes it as JSON.stringify's escape, anthropic-messages.ts sanitizeSurrogates drops it from the request), and JSON.parse/JSON.stringify
// hold values far deeper than the former 64-level registry, dispatch and 256-level bridge bounds (PiSharp now holds JsonData.MaximumDepth,
// 1,000).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> JsonLeftoverCases() =>
    [
        ("json.extension-tool-result-with-lone-surrogates-is-kept", LoneSurrogateToolResult),
        ("json.extension-results-and-session-entries-hold-deep-values", DeepExtensionValues),
        ("json.extension-stream-simple-receives-lone-surrogates-unchanged", LoneSurrogateExtensionProvider),
        ("json.extension-tool-schema-deeper-than-64-levels-is-declared", DeepToolSchema),
    ];

    // A tool's parameter schema is a JavaScript value: anthropic-messages.ts sends it as input_schema however deep it nests (formerly
    // the declaration serializer stopped at 64 levels).
    private static async Task DeepToolSchema()
    {
        using var sandbox = NodeSandbox("deep-schema");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "deep.ts"), """
            export default function (pi: any) {
              let schema: any = { type: "string" };
              for (let i = 0; i < 100; i++) schema = { type: "object", properties: { n: schema } };
              pi.registerTool({ name: "deep", label: "Deep", description: "Deep schema", parameters: schema,
                async execute() { return { content: [{ type: "text", text: "ok" }], details: {} }; } });
            }
            """);
        sandbox.Respond = (_, _) => AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "hi"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text");
        using var body = JsonDocument.Parse(sandbox.Requests.Single().Body!, new JsonDocumentOptions { MaxDepth = 1000 });
        var tool = body.RootElement.GetProperty("tools").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "deep");
        var schema = tool.GetProperty("input_schema"); var levels = 0;
        while (schema.TryGetProperty("properties", out var properties)) { schema = properties.GetProperty("n"); levels++; }
        Equal(100, levels, "every level declared");
    }

    // Only Pi's own provider converters apply sanitizeSurrogates: an extension's streamSimple receives the context as the agent holds it
    // (formerly PiSharp dropped the lone surrogate before the extension saw the request).
    private static async Task LoneSurrogateExtensionProvider()
    {
        RequirePiRuntime();
        using var sandbox = NodeSandbox("lone-surrogate-provider");
        sandbox.Vars["ACME_KEY"] = "acme-secret";
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "acme.ts"), ChatProviderExtension);
        // Input closes after agent_settled, the end of the run, like the other RPC cases (closing at agent_end raced the shutdown).
        var (code, records, stderr) = await RunRpc(sandbox, ["--provider", "acme-chat", "--model", "m1", "-e", extension],
            ["{\"id\":\"p\",\"type\":\"prompt\",\"message\":\"hi \\ud800 there\"}"],
            (record, _) => record["type"]?.GetValue<string>() == "agent_settled" || IsResponse(record, "p") && record["success"]?.GetValue<bool>() == false);
        Equal(0, code, "rpc exit; " + stderr);
        Check(records.Any(record => record["type"]?.GetValue<string>() == "agent_end"), "the prompt ran: " + records.Count + " records");
        // The probe logs JSON.stringify of what streamSimple received.
        Equal("""["stream","acme-chat","m1","acme-secret","hi \ud800 there"]""", LogLines(sandbox).Single(), "stream inputs");
    }

    private static async Task LoneSurrogateToolResult()
    {
        using var sandbox = NodeSandbox("lone-surrogate-result");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "lone.ts"), """
            export default function (pi: any) {
              pi.registerTool({ name: "lone", label: "Lone", description: "Returns lone surrogates",
                parameters: { type: "object", properties: {} },
                async execute() {
                  return { content: [{ type: "text", text: "x\ud800y" }], details: { "k\udc00": "v\ud800" } };
                } });
            }
            """);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("lone", new { }) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "Use the tool"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text");
        // anthropic-messages.ts convertContentBlocks: sanitizeSurrogates drops the lone surrogate from the request text.
        Equal("xy", ToolResultText(sandbox.Requests[1]), "tool result text sent");
        var result = sandbox.Requests[1].Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").EnumerateArray()
            .First(item => item.GetProperty("type").GetString() == "tool_result");
        Check(!result.TryGetProperty("is_error", out var error) || !error.GetBoolean(), "the tool result is not an error (formerly refused)");
        var session = File.ReadAllText(sandbox.SessionFiles().Single());
        Check(session.Contains("\"toolName\":\"lone\",\"content\":[{\"type\":\"text\",\"text\":\"x\\ud800y\"}],\"details\":{\"k\\udc00\":\"v\\ud800\"},\"isError\":false,",
            StringComparison.Ordinal), "session tool result line: " + session);
    }

    private static async Task DeepExtensionValues()
    {
        using var sandbox = NodeSandbox("deep-extension-values");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "deep.ts"), """
            export default function (pi: any) {
              pi.registerTool({ name: "deep_store", label: "Deep", description: "Stores deep values",
                parameters: { type: "object", properties: {} },
                async execute() {
                  let deep: any = { leaf: true };
                  for (let level = 0; level < 300; level++) deep = { next: deep };
                  pi.appendEntry("deep-state", { deep });
                  return { content: [{ type: "text", text: "stored" }], details: { deep } };
                } });
              pi.registerTool({ name: "deep_read", label: "Read", description: "Reads the deep entry",
                parameters: { type: "object", properties: {} },
                async execute(_id: string, _params: any, _signal: any, _onUpdate: any, ctx: any) {
                  const entry = ctx.sessionManager.getEntries().find((item: any) => item.type === "custom" && item.customType === "deep-state");
                  let depth = 0; for (let node = entry?.data?.deep; node?.next; node = node.next) depth++;
                  return { content: [{ type: "text", text: "depth=" + depth }], details: {} };
                } });
            }
            """);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("deep_store", new { }) : AnthropicText("done");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "store"]);
        Equal(0, code, "exit; " + stderr);
        // The result's 300-level details cross the bridge (formerly over its 256-level frame bound) and the tool result is not an error.
        Equal("stored", ToolResultText(sandbox.Requests[1]), "deep result");
        var file = sandbox.SessionFiles().Single();
        Check(File.ReadLines(file).Any(line => line.Contains("\"customType\":\"deep-state\"", StringComparison.Ordinal)), "the deep entry was written");
        sandbox.Requests.Clear();
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("deep_read", new { }, "toolu_02") : AnthropicText("done");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "--session", file, "read"]);
        Equal(0, code, "resume exit; " + stderr);
        // The handler context carries every session entry at its depth (formerly 64 levels per entry, 256 per bridge frame).
        Equal("depth=300", ToolResultText(sandbox.Requests[1]), "the resumed session holds the deep entry");
    }
}
