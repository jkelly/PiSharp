using System.Text.Json;

// The Node bridge end to end through PiCommand.RunAsync with a real Node (cases skip without Node 22.13+): an arbitrary TypeScript
// extension loaded with -e, its tool called by the model, its event handlers, commands and actions, with a fake Anthropic endpoint.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> BridgeCases() =>
    [
        ("bridge.typescript-extension-tool-called-by-the-model", TypeScriptTool),
    ];

    private static Sandbox NodeSandbox(string name)
    {
        RequireNode();
        var sandbox = new Sandbox(name);
        foreach (var variable in new[] { "PATH", "PATHEXT", "SystemRoot", "PISHARP_NODE" })
            if (Environment.GetEnvironmentVariable(variable) is { } value) sandbox.Vars[variable] = value;
        return sandbox;
    }

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
        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "-e", extension, "Say hello");
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text");
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "greet"), "tool declared");
        var toolResult = sandbox.Requests[1].Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0];
        Equal("tool_result", toolResult.GetProperty("type").GetString(), "tool result block");
        var content = toolResult.GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString() : content[0].GetProperty("text").GetString();
        Check(text!.StartsWith("Hello, Ada! entries=", StringComparison.Ordinal) && text.EndsWith("id=toolu_01", StringComparison.Ordinal), "tool text: " + text);
    }
}
