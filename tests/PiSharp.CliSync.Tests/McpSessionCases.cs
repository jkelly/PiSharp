using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// MCP in production sessions: RpcSessionCommand with an McpSessionHost over a temp agent directory, the live Anthropic route
// against a fake endpoint, and fake MCP servers. Upstream: packages/coding-agent/src/extensions/mcp/index.ts (session start,
// background connection, reportProblems/describeState), runtime.ts (createDefaultTransport, OAuth) and config.ts (loadMcpConfig).
// Authored expectations; no network, no real agent directory or environment.
internal static partial class Program
{
    /// <summary>A fake MCP server behind a channel: one tool, optional instructions, or an initialize failure.</summary>
    private sealed class FakeMcpChannel(string tool, string? instructions = null, Exception? failure = null, TaskCompletionSource? listed = null) : IMcpAdmittedRequestChannel
    {
        public int Lists, Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize")
                return failure is not null ? ValueTask.FromException<JsonData>(failure) : ValueTask.FromResult(JsonData.Parse(
                    "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}" +
                    (instructions is null ? "" : ",\"instructions\":" + JsonSerializer.Serialize(instructions)) + "}"));
            if (method == "tools/list")
            {
                Interlocked.Increment(ref Lists); listed?.TrySetResult();
                return ValueTask.FromResult(JsonData.Parse("{\"tools\":[{\"name\":\"" + tool + "\",\"description\":\"Fixture tool.\",\"inputSchema\":{\"type\":\"object\"}}]}"));
            }
            return ValueTask.FromException<JsonData>(new IOException("No tool execution admitted."));
        }
        public Task CloseAsync() { Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    }

    private static string[] McpDiagnostics(string error) => [.. error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()).Where(line => line.StartsWith('{'))
        .Select(line => JsonDocument.Parse(line).RootElement).Where(json => json.TryGetProperty("type", out var type) && type.GetString() == "mcp_diagnostic")
        .Select(json => json.GetProperty("message").GetString()!)];

    private static string[] ToolNames(Seen request) =>
        JsonDocument.Parse(request.Body!).RootElement.TryGetProperty("tools", out var tools)
            ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)] : [];

    // mcp.exposure-and-prompt + mcp.oauth: the global mcp.json is read at session start. The direct server's tool reaches the first
    // request; a failing direct server is reported and left out (the session still starts); a codemode server connects in the
    // background when discovery tools exist and is reported and skipped without them; disabled servers and the project file are
    // not used; --no-mcp connects nothing.
    private static Task McpProductionSession() => WithLiveRoot("mcp-session", async root =>
    {
        var agent = Path.Combine(root, "agent"); Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "mcp.json"), """
            {"mcpServers":{
              "docs":{"command":"docs-server","exposure":"direct"},
              "broken":{"command":"broken-server","exposure":"direct"},
              "later":{"command":"later-server"},
              "off":{"command":"off-server","exposure":"direct","enabled":false}}}
            """);
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "mcp.json"), """{"mcpServers":{"project":{"command":"project-server","exposure":"direct"}}}""");
        var channels = new ConcurrentDictionary<string, ConcurrentBag<FakeMcpChannel>>();
        var laterConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        McpSessionHost Host(bool discovery) => new(agent, Path.Combine(root, "home"), () => [KeyValuePair.Create("PATH", root)])
        {
            CreateChannel = entry => (actual, token) =>
            {
                var channel = actual.Name switch
                {
                    "docs" => new FakeMcpChannel("search"),
                    "broken" => new FakeMcpChannel("never", failure: new IOException("initialize refused\nsecond line")),
                    "later" => new FakeMcpChannel("lookup", "Looks things up."),
                    _ => throw new InvalidOperationException("Unexpected MCP server " + actual.Name)
                };
                channels.GetOrAdd(actual.Name, _ => []).Add(channel);
                return ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel);
            },
            ObserveBackgroundConnection = report => { if (report.Entry.Name == "later" && report.Failure is null) laterConnected.TrySetResult(); },
            Discovery = discovery ? _ => [McpDiscoveryExecutableDefinition.CreateCodemode("sync-codemode", "Fixture codemode",
                (_, _, _) => ValueTask.FromResult(JsonData.Parse("{\"content\":[]}")))] : null
        };
        async Task<(Seen[] Requests, string Error)> Session(McpSessionHost host, params string[] extra)
        {
            var provider = new LiveEndpoint(seen => seen.Url == MessagesUrl ? AnthropicStream() : throw new InvalidOperationException("Unexpected URL " + seen.Url));
            await using var rpc = new LiveRpc([.. LiveArgs(root, "anthropic", "claude-sonnet-4-5"), .. extra],
                new(Env(("ANTHROPIC_API_KEY", "env-key")), () => provider), host);
            await rpc.Prompt("first", "first");
            // The background server connected and its tools were published at an idle boundary before the next prompt.
            if (host.Discovery is not null && !extra.Contains("--no-mcp")) await laterConnected.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await rpc.Prompt("second", "second");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            return (provider.Snapshot(), rpc.Error.ToString());
        }

        var projectNotice = Path.Combine(root, ".pi", "mcp.json") + " is ignored because PiSharp does not read project trust.";
        var (requests, error) = await Session(Host(discovery: true));
        foreach (var request in requests) Check(ToolNames(request).Contains("mcp__docs__search"), "direct MCP tool declared: " + string.Join(",", ToolNames(request)));
        Check(!requests.SelectMany(ToolNames).Any(name => name.Contains("broken", StringComparison.Ordinal) || name.Contains("off", StringComparison.Ordinal)),
            "failed and disabled servers declare nothing");
        Names([projectNotice, "MCP servers need attention:\n  broken: failed: initialize refused"], McpDiagnostics(error), "startup report with discovery");
        Equal(1, channels["later"].Single().Lists, "background server listed its tools");
        Check(channels.Values.SelectMany(bag => bag).All(channel => channel.Closes == 1), "every MCP channel closed with the session");
        Check(!channels.ContainsKey("off"), "disabled server not connected");

        // Without discovery tools (PiSharp's production default) the codemode server is reported and never connected.
        channels.Clear();
        (requests, error) = await Session(Host(discovery: false));
        Check(requests.All(request => ToolNames(request).Contains("mcp__docs__search")), "direct tool without discovery");
        Names([projectNotice, "MCP servers need attention:\n  later: not connected: its codemode or tool_search tools need discovery tools PiSharp does not implement yet; set \"exposure\": \"direct\" to use it\n  broken: failed: initialize refused"],
            McpDiagnostics(error), "startup report without discovery");
        Check(!channels.ContainsKey("later"), "codemode server not connected without discovery");

        // --no-mcp: nothing is read or connected.
        channels.Clear();
        (requests, error) = await Session(Host(discovery: true), "--no-mcp");
        Check(channels.IsEmpty && McpDiagnostics(error).Length == 0, "--no-mcp connects nothing");
        Check(!requests.SelectMany(ToolNames).Any(name => name.StartsWith("mcp__", StringComparison.Ordinal)), "--no-mcp declares no MCP tool");
    });

    // mcp.oauth: an HTTP server without an Authorization header uses OAuth; the session reads the stored token from the durable
    // mcp-auth.json store in the agent directory (where `mcp login` saved it) and sends it as Bearer on every MCP request.
    private static Task McpOAuthServerUsesStoredTokens() => WithLiveRoot("mcp-oauth", async root =>
    {
        var agent = Path.Combine(root, "agent"); Directory.CreateDirectory(agent);
        var url = new Uri("https://mcp.example/mcp");
        await File.WriteAllTextAsync(Path.Combine(agent, "mcp.json"), """{"mcpServers":{"remote":{"url":"https://mcp.example/mcp","exposure":"direct"}}}""");
        var store = new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(agent)).ForServer("remote", url);
        await store.SaveAsync(new McpOAuthState(url.AbsoluteUri, Tokens: new McpOAuthTokens("stored-access", "Bearer")));
        var methods = new ConcurrentQueue<(string Method, string? Authorization)>();
        var mcp = new LiveEndpoint(seen =>
        {
            if (seen.Method == "GET") return new(HttpStatusCode.MethodNotAllowed);
            if (seen.Method == "DELETE") return new(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(seen.Body!);
            var method = body.RootElement.GetProperty("method").GetString()!;
            methods.Enqueue((method, seen.Headers.TryGetValue("Authorization", out var authorization) ? authorization : null));
            if (!body.RootElement.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var result = method switch
            {
                "initialize" => """{"protocolVersion":"2025-11-25","serverInfo":{"name":"remote","version":"1"},"capabilities":{"tools":{}}}""",
                "tools/list" => """{"tools":[{"name":"fetch","description":"Fetches.","inputSchema":{"type":"object"}}]}""",
                _ => throw new InvalidOperationException("Unexpected MCP method " + method)
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}", Encoding.UTF8, "application/json") };
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "session-1");
            return response;
        });
        var provider = new LiveEndpoint(seen => seen.Url == MessagesUrl ? AnthropicStream() : throw new InvalidOperationException("Unexpected URL " + seen.Url));
        var host = new McpSessionHost(agent, Path.Combine(root, "home"), () => []) { CreateHttpHandler = () => mcp };
        await using (var rpc = new LiveRpc(LiveArgs(root, "anthropic", "claude-sonnet-4-5"), new(Env(("ANTHROPIC_API_KEY", "env-key")), () => provider), host))
            await RunPrompts(rpc, "hello");
        Check(ToolNames(provider.Snapshot().Single()).Contains("mcp__remote__fetch"), "OAuth server tool declared");
        Names(["initialize", "notifications/initialized", "tools/list"], methods.Select(row => row.Method).Distinct(), "MCP methods");
        Check(methods.All(row => row.Authorization == "Bearer stored-access"), "stored token sent on every MCP request");
        Check(mcp.Snapshot().Any(seen => seen.Method == "DELETE" && seen.Headers.TryGetValue("Mcp-Session-Id", out var session) && session == "session-1"),
            "session deleted on close");
    });

    // cross-spawn on Windows: .exe files run directly; other commands (npm .cmd shims) run through cmd.exe with escaping.
    private static Task McpStdioWindowsCommand() => WithLiveRoot("mcp-stdio", async root =>
    {
        var bin = Path.Combine(root, "bin"); var shimDirectory = Path.Combine(root, "node_modules", ".bin");
        Directory.CreateDirectory(bin); Directory.CreateDirectory(shimDirectory);
        await File.WriteAllTextAsync(Path.Combine(bin, "server.exe"), ""); await File.WriteAllTextAsync(Path.Combine(bin, "npx.cmd"), "");
        await File.WriteAllTextAsync(Path.Combine(shimDirectory, "tool.cmd"), "");
        var environment = new Dictionary<string, string> { ["Path"] = bin + ";" + shimDirectory, ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD", ["ComSpec"] = @"C:\Windows\System32\cmd.exe" };
        var direct = WindowsCommand.Resolve("server", ["--flag", "a b"], root, environment);
        Equal(Path.Combine(bin, "server.EXE"), direct.Executable, "PATH resolution with the PATHEXT spelling (node-which)");
        Names(["--flag", "a b"], direct.Arguments, "direct arguments");
        Equal(null, direct.VerbatimArguments, "no shell for .exe");
        var npx = WindowsCommand.Resolve("npx", ["-y", "@scope/server", "a&b", "say \"hi\""], root, environment);
        Equal(@"C:\Windows\System32\cmd.exe", npx.Executable, "batch files run through ComSpec");
        Equal("/d /s /c \"npx ^\"-y^\" ^\"@scope/server^\" ^\"a^&b^\" ^\"say^ \\^\"hi\\^\"^\"\"", npx.VerbatimArguments, "cmd escaping");
        var shim = WindowsCommand.Resolve("tool", ["x y"], root, environment);
        Equal("/d /s /c \"tool ^^^\"x^^^ y^^^\"\"", shim.VerbatimArguments, "npm shims are escaped twice");
        Equal(null, WindowsCommand.Which("missing", root, environment), "unresolved command");
        Equal(Path.Combine(bin, "npx.CMD"), WindowsCommand.Which(@".\bin\npx", root, environment), "relative path resolves against the cwd");
    });
}
