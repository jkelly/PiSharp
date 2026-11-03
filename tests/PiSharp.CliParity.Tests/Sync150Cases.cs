// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/validation.ts (getValidator: compile(schema, { formats,
// coerce: true, unresolvedRefs: "reject" })) through the Pi entry. Authored from the source, not captured.
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

internal static partial class Program
{
    /// <summary>A fake MCP server with one tool whose schema refers to a definition it does not have; tools/call echoes its arguments.</summary>
    private sealed class DanglingRefMcpChannel : IMcpAdmittedRequestChannel
    {
        public List<string> Calls { get; } = [];
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize")
                return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"));
            if (method == "tools/list")
                return ValueTask.FromResult(JsonData.Parse("""
                    {"tools":[{"name":"lookup","description":"Looks up.","inputSchema":{"type":"object","properties":{"q":{"type":"string"},"t":{"$ref":"#/definitions/Missing"}},"required":["q"]}}]}
                    """));
            if (method == "tools/call")
            {
                var arguments = parameters!.Value.GetProperty("arguments").GetRawText();
                lock (Calls) Calls.Add(arguments);
                return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "got " + arguments } } })));
            }
            return ValueTask.FromException<JsonData>(new IOException("Unexpected MCP request " + method));
        }
        public Task CloseAsync() => Task.CompletedTask;
    }

    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        // A dangling $ref in an MCP server's schema rejects only the values at its place: other calls of the tool still validate and coerce.
        ("sync150.validation-mcp-dangling-ref-rejects-only-values-at-its-place", async () =>
        {
            using var sandbox = new Sandbox("validation-dangling-ref");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "mcp.json"), """{"mcpServers":{"proj":{"command":"proj-server","exposure":"direct"}}}""");
            var channel = new DanglingRefMcpChannel();
            sandbox.Respond = (_, index) => index switch
            {
                0 => AnthropicToolCall("mcp__proj__lookup", new { q = 5 }, "toolu_d1"),
                1 => AnthropicToolCall("mcp__proj__lookup", new { q = "a", t = 1 }, "toolu_d2"),
                _ => AnthropicText("done")
            };
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with
            {
                CreateMcpHost = agentDir => new McpSessionHost(agentDir, sandbox.Home, () => [])
                { CreateChannel = entry => (actual, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel) }
            };
            var code = await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "look up"], host, CancellationToken.None);
            Equal(0, code, "exit; " + stderr);
            Equal("{\"q\":\"5\"}", channel.Calls.Single(), "the call that does not reach the reference is coerced and runs");
            var (text, isError) = ToolResultBlock(sandbox.Requests[1]);
            Equal(false, isError, "the first call succeeded: " + text);
            (text, isError) = ToolResultBlock(sandbox.Requests[2]);
            Equal(true, isError, "a value at the dangling reference is an error result");
            Equal("Validation failed for tool \"mcp__proj__lookup\":\n  - t: unresolved reference #/definitions/Missing\n\nReceived arguments:\n{\n  \"q\": \"a\",\n  \"t\": 1\n}",
                text, "the unresolved reference error");
        }),
    ];
}
