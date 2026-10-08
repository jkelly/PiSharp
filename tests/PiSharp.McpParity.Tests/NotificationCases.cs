using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

// Upstream: extensions/mcp/runtime.ts (connectOnce: notifications/message to the server log, notifications/tools/list_changed refreshes
// the tools; fetchResources counts), extensions/mcp/log.ts (formatMcpLogMessage, rotation), extensions/mcp/cli.ts (list: resources and
// URI templates), extensions/mcp/index.ts (describeState with resources, renderServersSection from the current servers).
internal static partial class Program
{
    /// <summary>A fake server whose tool list can change; it reports changes through the session's notification handler.</summary>
    private sealed class ChangingServer(McpNotificationHandler? notify) : IMcpAdmittedRequestChannel
    {
        public volatile string[] Tools = ["search"];
        public McpNotificationHandler? Notify => notify;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) => method switch
        {
            "initialize" => ValueTask.FromResult(JsonData.Parse("""{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{"listChanged":true}}}""")),
            "tools/list" => ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
                { tools = Tools.Select(tool => new { name = tool, description = "The " + tool + " tool.", inputSchema = new { type = "object" } }) }))),
            _ => ValueTask.FromException<JsonData>(new IOException("Unexpected MCP method " + method))
        };
        public Task CloseAsync() => Task.CompletedTask;
    }

    // notifications/message is appended to mcp.log; notifications/tools/list_changed refreshes the tools: new tools are declared,
    // withdrawn ones become unreachable.
    private static Task NotificationsLogAndToolListChanges() => WithRoot("notifications", DirectDocs, async fixture =>
    {
        var servers = new ConcurrentBag<ChangingServer>();
        var provider = new Endpoint();
        var host = fixture.Host() with
        {
            CreateNotifyingChannel = (entry, notify) => (actual, token) =>
            {
                var server = new ChangingServer(notify); servers.Add(server);
                return ValueTask.FromResult<IMcpAdmittedRequestChannel>(server);
            }
        };
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, host))
        {
            await rpc.Prompt("p1", "hello");
            var server = servers.Single();
            Check(server.Notify is not null, "the session handles the server's notifications");
            await server.Notify!("notifications/message", JsonData.Parse("""{"level":"warning","logger":"db","data":"slow query\r\nretrying"}"""), CancellationToken.None);
            await server.Notify!("notifications/message", JsonData.Parse("""{"data":{"rows":2}}"""), CancellationToken.None);
            server.Tools = ["search", "extra"];
            await server.Notify!("notifications/tools/list_changed", null, CancellationToken.None);
            await rpc.Prompt("p2", "after the change");
            server.Tools = ["extra"];
            await server.Notify!("notifications/tools/list_changed", null, CancellationToken.None);
            await rpc.Prompt("p3", "after the withdrawal");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(ToolNames(requests[0]).Contains("mcp__docs__search") && !ToolNames(requests[0]).Contains("mcp__docs__extra"), "before");
        Check(ToolNames(requests[1]).Contains("mcp__docs__search") && ToolNames(requests[1]).Contains("mcp__docs__extra"), "a new tool is declared");
        Check(!ToolNames(requests[2]).Contains("mcp__docs__search") && ToolNames(requests[2]).Contains("mcp__docs__extra"), "a withdrawn tool is not");
        var log = File.ReadAllLines(Path.Combine(fixture.Agent, "mcp.log"));
        Equal(3, log.Length, "log lines: " + string.Join(" | ", log));
        Check(System.Text.RegularExpressions.Regex.IsMatch(log[0], @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \[docs\] warning db: slow query$"), log[0]);
        Equal("    retrying", log[1], "continuation line");
        Check(log[2].EndsWith(" [docs] info {\"rows\":2}", StringComparison.Ordinal), log[2]);
    });

    // log.ts: formatting without a logger, non-record params, and rotation past 5 MB.
    private static async Task ServerLogFormatAndRotation()
    {
        var at = new DateTimeOffset(2026, 10, 8, 12, 0, 0, 5, TimeSpan.Zero);
        Equal("2026-10-08T12:00:00.005Z [docs] error boom\n", McpServerLog.Format("docs", JsonData.Parse("""{"level":"error","data":"boom"}"""), at), "level without a logger");
        Equal("2026-10-08T12:00:00.005Z [docs] info plain\n", McpServerLog.Format("docs", JsonData.Parse("\"plain\""), at), "non-record params are the data");
        Equal("2026-10-08T12:00:00.005Z [docs] info undefined\n", McpServerLog.Format("docs", JsonData.Parse("{}"), at), "no data");
        var root = Path.GetFullPath(Temp("log-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "agent", "mcp.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, new string('x', (int)McpServerLog.MaximumBytes + 1));
            new McpServerLog(path).Write("docs", JsonData.Parse("""{"data":"after rotation"}"""));
            Check(new FileInfo(path + ".1").Length == McpServerLog.MaximumBytes + 1, "rotated");
            Check(File.ReadAllText(path).EndsWith(" [docs] info after rotation\n", StringComparison.Ordinal), "new file");
        }
        finally { Directory.Delete(root, true); }
    }

    // cli.ts list: servers with resources report their resource and URI template counts, without MCP App resources.
    private static Task ListResourceCounts() => WithRoot("list-resources", """{"mcpServers":{"docs":{"command":"docs-server"}}}""", async fixture =>
    {
        McpAdmittedChannelFactory Channel(McpServerEntry entry) => (actual, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new ResourceServer());
        using var output = new StringWriter { NewLine = "\n" }; using var error = new StringWriter { NewLine = "\n" };
        var options = new McpCommandOptions(fixture.Project, fixture.Agent) { CreateChannel = Channel };
        Equal(0, await McpCommand.RunAsync(["list"], output, error, options), "exit code; " + error);
        Equal($"docs: connected, 1 tool (codemode, global)\n  docs-server\n  tools: search\n  resources: 2, URI templates: 1\n", output.ToString(), "list");
        using var json = new StringWriter { NewLine = "\n" };
        Equal(0, await McpCommand.RunAsync(["list", "--json"], json, error, options), "json exit code");
        var report = JsonDocument.Parse(json.ToString()).RootElement.GetProperty("servers")[0];
        Equal(2, report.GetProperty("resources").GetInt32(), "json resources"); Equal(1, report.GetProperty("resourceTemplates").GetInt32(), "json templates");
    });

    private sealed class ResourceServer : IMcpAdmittedRequestChannel
    {
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) => ValueTask.FromResult(JsonData.Parse(method switch
        {
            "initialize" => """{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{},"resources":{}}}""",
            "tools/list" => """{"tools":[{"name":"search","inputSchema":{"type":"object"}}]}""",
            // The second page carries an MCP App resource, which is not counted.
            "resources/list" when parameters is null => """{"resources":[{"uri":"docs://a","name":"a"}],"nextCursor":"2"}""",
            "resources/list" => """{"resources":[{"uri":"docs://b","name":"b"},{"uri":"ui://app","name":"app"},{"uri":"docs://c","name":"c","mimeType":"text/html;profile=mcp-app"}]}""",
            "resources/templates/list" => """{"resourceTemplates":[{"uriTemplate":"docs://{id}","name":"doc"}]}""",
            _ => throw new IOException("Unexpected MCP method " + method)
        }));
        public Task CloseAsync() => Task.CompletedTask;
    }

    // renderServersSection renders the current servers: an exposure change from `/mcp` updates the section's source.
    private static Task ServersSectionFollowsManagerChanges()
    {
        static McpServerEntry Entry(string name, string json) =>
            new(name, McpConfigurationReader.Validate(name, JsonData.Parse(json).Value).Config!, "mcp.json", McpConfigurationScope.Global);
        var source = new McpServersPromptSource();
        var docs = Entry("docs", """{"command":"x","exposure":"deferred"}""");
        source.Publish(1, new([docs], []), [new(docs, [], "Docs instructions.")]);
        Equal("MCP servers whose tools are not declared to you. Load the tools of `tool_search` servers with `tool_search`.\n- mcp__docs (tool_search): Docs instructions.",
            source.Render(1), "before");
        Check(source.ReplaceServers(1, [Entry("docs", """{"command":"x","exposure":"direct"}""")]), "replaced");
        Equal(null, source.Render(1), "a direct server leaves the section");
        Check(source.ReplaceServers(1, [Entry("docs", """{"command":"x"}""")]), "replaced again");
        Equal("MCP servers whose tools are not declared to you. Call the tools of `codemode` servers from codemode scripts.\n- mcp__docs (codemode): Docs instructions.",
            source.Render(1), "codemode keeps the instructions");
        Check(!source.ReplaceServers(2, []), "unknown generation");
        return Task.CompletedTask;
    }
}
