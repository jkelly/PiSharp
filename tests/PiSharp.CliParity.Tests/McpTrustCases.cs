using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

// extensions/mcp/config.ts loadMcpConfig with ctx.isProjectTrusted(): the project .pi/mcp.json is read only for a trusted project.
// The trust answer is the Pi entry's own (PiEntryOptions.ProjectTrusted) wired into IMPL-H's McpSessionHost.IsProjectTrusted.
internal static partial class Program
{
    /// <summary>A fake MCP server behind a channel with one tool.</summary>
    private sealed class FakeMcpChannel(string tool) : IMcpAdmittedRequestChannel
    {
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize")
                return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"));
            if (method == "tools/list")
                return ValueTask.FromResult(JsonData.Parse("{\"tools\":[{\"name\":\"" + tool + "\",\"description\":\"Fixture tool.\",\"inputSchema\":{\"type\":\"object\"}}]}"));
            return ValueTask.FromException<JsonData>(new IOException("No tool execution admitted."));
        }
        public Task CloseAsync() => Task.CompletedTask;
    }

    private static IEnumerable<(string, Func<Task>)> McpTrustCases() =>
    [
        ("mcp.project-config-connects-when-trusted-and-is-skipped-when-untrusted", async () =>
        {
            foreach (var trusted in new[] { true, false })
            {
                using var sandbox = new Sandbox("mcp-trust-" + trusted, trusted);
                sandbox.Write(System.IO.Path.Combine(sandbox.Cwd, ".pi", "mcp.json"), """{"mcpServers":{"proj":{"command":"proj-server","exposure":"direct"}}}""");
                var connected = new List<string>();
                using var stdout = new StringWriter(); using var stderr = new StringWriter();
                var host = sandbox.Host(stdout, stderr, null) with
                {
                    CreateMcpHost = agentDir => new McpSessionHost(agentDir, sandbox.Home, () => [])
                    {
                        CreateChannel = entry => (actual, token) => { lock (connected) connected.Add(entry.Name); return ValueTask.FromResult<IMcpAdmittedRequestChannel>(new FakeMcpChannel("lookup")); }
                    }
                };
                var code = await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi"], host, CancellationToken.None);
                Equal(0, code, "exit; " + stderr);
                var tools = sandbox.Requests[0].Json.TryGetProperty("tools", out var declared)
                    ? declared.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
                Equal(trusted, connected.Contains("proj"), "project server connected (trusted=" + trusted + ")");
                Equal(trusted, tools.Contains("mcp__proj__lookup"), "project server tool declared (trusted=" + trusted + "): " + string.Join(",", tools));
            }
        }),
    ];
}
