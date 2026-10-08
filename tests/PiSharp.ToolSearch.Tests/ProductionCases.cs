using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

// Production sessions (RpcSessionCommand with an McpSessionHost over a temp agent directory) against a fake Anthropic endpoint
// and fake MCP servers. Upstream: extensions/mcp/index.ts (`deferred` servers connect in the background, ensureDiscoveryActive
// activates tool_search or warns, renderServersSection), extensions/tool-search (the tool), core/agent-session.ts
// (_isAllowedTool/_isActivatable: --tools/--exclude-tools keep tool_search out, which leaves deferred tools unreachable) and
// core/sdk.ts (a CLI session applies its initial selection instead of restoring the transcript's).
internal static partial class Program
{
    private sealed record Seen(string Url, string? Body);

    /// <summary>Fake provider endpoint: records every request and answers each with the next scripted response.</summary>
    private sealed class Endpoint(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private readonly List<Seen> requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new Seen(request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            int index; lock (requests) { requests.Add(seen); index = requests.Count - 1; }
            if (seen.Url != MessagesUrl) throw new InvalidOperationException("Unexpected URL " + seen.Url);
            return index < script.Length ? script[index]() : Text("ok");
        }
        public Seen[] Snapshot() { lock (requests) return [.. requests]; }
    }

    private const string MessagesUrl = "https://api.anthropic.com/v1/messages?beta=true";
    private static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage Stream(object block, string? delta, string stop)
    {
        var start = Frame("message_start", new { type = "message_start", message = new { id = "authored", role = "assistant", model = "claude-sonnet-4-5",
            content = Array.Empty<object>(), usage = new { input_tokens = 2, output_tokens = 0 } } });
        var body = start + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = block })
            + (delta is null ? "" : Frame("content_block_delta", new { type = "content_block_delta", index = 0,
                delta = stop == "tool_use" ? (object)new { type = "input_json_delta", partial_json = delta } : new { type = "text_delta", text = delta } }))
            + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
            + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = stop }, usage = new { output_tokens = 1 } })
            + Frame("message_stop", new { type = "message_stop" });
        return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }
    private static HttpResponseMessage Text(string text) => Stream(new { type = "text", text = "" }, text, "end_turn");
    private static HttpResponseMessage Call(string id, string name, object input) =>
        Stream(new { type = "tool_use", id, name, input = new { } }, JsonSerializer.Serialize(input), "tool_use");

    /// <summary>A fake `docs` MCP server with two tools; tools/call answers with the query it received.</summary>
    private sealed class DocsServer(string name, Task? initialized = null) : IMcpAdmittedRequestChannel
    {
        public string Name => name;
        public readonly ConcurrentQueue<string> Calls = new();
        public int Lists, Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            switch (method)
            {
                case "initialize":
                    if (initialized is not null) await initialized.WaitAsync(token);
                    return (JsonData.Parse("""{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{}},"instructions":"Search and fetch the docs."}"""));
                case "tools/list":
                    Interlocked.Increment(ref Lists);
                    return (JsonData.Parse("""
                        {"tools":[
                          {"name":"search","description":"Search the documentation.\nReturns links.","inputSchema":{"type":"object","properties":{"query":{"type":"string","description":"Words to look for."}}}},
                          {"name":"fetch","description":"Fetch a page by URL.","inputSchema":{"type":"object","properties":{"url":{"type":"string"}}}}]}
                        """));
                case "tools/call":
                    var call = parameters!.Value;
                    Calls.Enqueue(call.GetProperty("name").GetString() + ":" + call.GetProperty("arguments").GetRawText());
                    return JsonData.Parse("""{"content":[{"type":"text","text":"Found: install guide."}]}""");
                default: throw new IOException("Unexpected MCP method " + method);
            }
        }
        public Task CloseAsync() { Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    }

    /// <summary>One production RPC host over a bounded in-memory connection.</summary>
    private sealed class Rpc : IAsyncDisposable
    {
        private readonly Channel<JsonData> records = System.Threading.Channels.Channel.CreateUnbounded<JsonData>();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        private readonly BoundedRpcConnection connection;
        private bool finished;
        public StringWriter Error { get; } = new();
        public Task<int> Completion { get; }
        public List<JsonData> Events { get; } = [];
        public Rpc(string[] args, Endpoint provider, McpSessionHost host)
        {
            connection = new((record, _) => { records.Writer.TryWrite(record); return ValueTask.CompletedTask; });
            var runtime = new LiveSessionRuntime(name => name == "ANTHROPIC_API_KEY" ? "env-key" : null, () => provider);
            Completion = Run();
            async Task<int> Run()
            {
                try { return await RpcSessionCommand.RunWithPresentationAsync(args, connection.Input, connection.Output, Error, null!, deadline.Token, liveRuntime: runtime, mcpHost: host); }
                finally { records.Writer.TryComplete(); }
            }
        }
        public async Task Prompt(string id, string message)
        {
            await connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(new { id, type = "prompt", message })), deadline.Token);
            var responded = false;
            while (true)
            {
                JsonData record;
                try { record = await records.Reader.ReadAsync(deadline.Token); }
                catch (ChannelClosedException) { throw new InvalidOperationException($"RPC host ended before {id}; exit {await Completion}; {Error}"); }
                Events.Add(record);
                var type = record.Value.GetProperty("type").GetString();
                if (type == "response" && record.Value.GetProperty("id").GetString() == id)
                { Check(record.Value.GetProperty("success").GetBoolean(), "prompt refused: " + record); responded = true; }
                else if (type == "agent_settled" && responded) return;
            }
        }
        public async Task<int> Finish()
        {
            finished = true; connection.CompleteInput();
            while (await records.Reader.WaitToReadAsync()) while (records.Reader.TryRead(out var record)) Events.Add(record);
            return await Completion;
        }
        public async ValueTask DisposeAsync()
        {
            if (!finished) { connection.CompleteInput(); try { await Completion; } catch (Exception) { } }
            await connection.DisposeAsync(); deadline.Dispose();
        }
    }

    private static string[] Args(string root, string mode, params string[] extra) =>
        ["session", "rpc", "--session", Path.Combine(root, "session.jsonl"), "--workspace", root, "--live", "--provider", "anthropic",
            "--model", "claude-sonnet-4-5", "--session-mode", mode, .. extra];

    private sealed class Fixture(string root)
    {
        public readonly string Agent = Path.Combine(root, "agent");
        public readonly ConcurrentBag<DocsServer> Servers = [];
        public TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>When set, every server answers initialize only once it completes, so background servers connect late.</summary>
        public Task? Initialized;
        public McpSessionHost Host(Func<long, System.Collections.Immutable.ImmutableArray<McpDiscoveryExecutableDefinition>>? discovery = null) =>
            new(Agent, Path.Combine(root, "home"), () => [KeyValuePair.Create("PATH", root)])
            {
                CreateChannel = entry => (actual, token) =>
                {
                    if (actual.Name is not ("docs" or "web")) throw new InvalidOperationException("Unexpected MCP server " + actual.Name);
                    var server = new DocsServer(actual.Name, Initialized); Servers.Add(server);
                    return ValueTask.FromResult<IMcpAdmittedRequestChannel>(server);
                },
                ObserveBackgroundConnection = report => { if (report.Failure is null) Connected.TrySetResult(); else Connected.TrySetException(report.Failure); },
                Discovery = discovery
            };
    }

    /// <summary>Waits until the background server connected and published its tools; a host that ended first is reported.</summary>
    private static async Task Connected(Fixture fixture, Rpc rpc)
    {
        var settled = await Task.WhenAny(fixture.Connected.Task, rpc.Completion, Task.Delay(TimeSpan.FromSeconds(30)));
        if (settled == rpc.Completion) throw new InvalidOperationException($"RPC host ended before the MCP server connected; exit {await rpc.Completion}; {rpc.Error}");
        if (settled != fixture.Connected.Task) throw new TimeoutException("MCP server did not connect; " + rpc.Error);
        await fixture.Connected.Task;
    }

    private static async Task WithRoot(string name, string mcpJson, Func<string, Fixture, Task> run)
    {
        var root = Path.GetFullPath(Temp(name + "-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        var fixture = new Fixture(root); Directory.CreateDirectory(fixture.Agent);
        await File.WriteAllTextAsync(Path.Combine(fixture.Agent, "mcp.json"), mcpJson);
        try { await run(root, fixture); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private const string DeferredDocs = """{"mcpServers":{"docs":{"command":"docs-server","exposure":"deferred","description":"Documentation search."}}}""";

    private static string[] ToolNames(Seen request) =>
        JsonDocument.Parse(request.Body!).RootElement.TryGetProperty("tools", out var tools) ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)] : [];

    /// <summary>Every string value of the request, so prompt text is found wherever the provider projection puts it.</summary>
    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Object => value.EnumerateObject().SelectMany(property => Strings(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        _ => []
    };
    private static bool Mentions(Seen request, string text) => Strings(JsonDocument.Parse(request.Body!).RootElement).Any(value => value.Contains(text, StringComparison.Ordinal));

    /// <summary>The text of each tool_result block in the request's last message.</summary>
    private static string[] ToolResults(Seen request) => [.. JsonDocument.Parse(request.Body!).RootElement.GetProperty("messages").EnumerateArray().Last()
        .GetProperty("content").EnumerateArray().Where(block => block.GetProperty("type").GetString() == "tool_result")
        .Select(block => block.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
            : string.Concat(block.GetProperty("content").EnumerateArray().Select(part => part.GetProperty("text").GetString())))];

    private static string[] McpDiagnostics(string error) => [.. error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()).Where(line => line.StartsWith('{'))
        .Select(line => JsonDocument.Parse(line).RootElement).Where(json => json.TryGetProperty("type", out var type) && type.GetString() == "mcp_diagnostic")
        .Select(json => json.GetProperty("message").GetString()!)];

    private const string Unreachable = "MCP tools are only reachable from the codemode or tool_search tool, but neither is active; they cannot be called.";
    private const string LoadedSearch = "Loaded 1 tool. They are available from your next call:\n- mcp__docs__search: Search the documentation.";

    // The release blocker of 1.1.0: a `deferred` server now connects in a production session. Its tools are registered but not
    // declared; tool_search (active for it, and admitted by the profile's final-action policy) loads the best match, which the
    // next model call declares and calls on the server (Pi trusts the servers of mcp.json), and which stays declared for later
    // prompts. The change is recorded in the session file.
    private static Task DeferredServerProductionSession() => WithRoot("deferred", DeferredDocs, async (root, fixture) =>
    {
        var provider = new Endpoint(() => Text("ready"),
            () => Call("toolu_search", "tool_search", new { query = "search documentation", limit = 1 }),
            () => Call("toolu_docs", "mcp__docs__search", new { query = "install" }),
            () => Text("done"));
        await using var rpc = new Rpc(Args(root, "new-lazy"), provider, fixture.Host());
        await Connected(fixture, rpc);
        await rpc.Prompt("p1", "hello");
        await rpc.Prompt("p2", "find the install guide");
        await rpc.Prompt("p3", "thanks");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        Names([], McpDiagnostics(rpc.Error.ToString()), "no MCP problem reported");
        var requests = provider.Snapshot();
        Equal(5, requests.Length, "provider requests");
        foreach (var request in requests[..2])
        {
            Check(ToolNames(request).Contains("tool_search"), "tool_search declared: " + string.Join(",", ToolNames(request)));
            Check(!ToolNames(request).Any(name => name.StartsWith("mcp__", StringComparison.Ordinal)), "deferred tools not declared before loading");
        }
        Names([LoadedSearch], ToolResults(requests[2]), "tool_search result");
        Check(ToolNames(requests[2]).Contains("mcp__docs__search") && !ToolNames(requests[2]).Contains("mcp__docs__fetch"), "the next call declares the loaded tool only");
        Names(["Found: install guide."], ToolResults(requests[3]), "the loaded tool's result");
        Names(["search:{\"query\":\"install\"}"], fixture.Servers.Single().Calls, "the loaded tool is called on its server");
        Check(ToolNames(requests[4]).Contains("mcp__docs__search") && ToolNames(requests[4]).Contains("tool_search"), "still declared at the next prompt");
        var recorded = File.ReadAllLines(Path.Combine(root, "session.jsonl")).Select(line => JsonDocument.Parse(line).RootElement)
            .Where(entry => entry.TryGetProperty("message", out var message) && message.TryGetProperty("toolsAdded", out _))
            .Select(entry => entry.GetProperty("message").GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray()).ToArray();
        var history = string.Join(" | ", recorded.Select(names => string.Join(",", names)));
        Names(["read", "write", "tool_search", "mcp__docs__search"], recorded[^2], "loaded tool recorded in the session file: " + history);
        // PiSharp deviation (pre-existing): closing the session withdraws the background server's tools durably, so the file's
        // last loadout no longer names them; upstream records nothing at shutdown.
        Names(["read", "write", "tool_search"], recorded[^1], "shutdown withdrawal of the background server's tools: " + history);
        Equal(1, fixture.Servers.Single().Closes, "server closed with the session");
    });


    // A CLI session reopened from its file applies the initial selection (core/sdk.ts initialActiveToolNames), as upstream:
    // tool_search is active again, the tool it loaded is not declared until it is loaded again.
    private static Task CliReopenAppliesInitialSelection() => WithRoot("reopen", DeferredDocs, async (root, fixture) =>
    {
        var first = new Endpoint(() => Call("toolu_search", "tool_search", new { query = "documentation", limit = 1 }), () => Text("loaded"));
        await using (var rpc = new Rpc(Args(root, "new-lazy"), first, fixture.Host()))
        {
            await Connected(fixture, rpc);
            await rpc.Prompt("p1", "load docs");
            Equal(0, await rpc.Finish(), "first exit code; " + rpc.Error);
        }
        Names([LoadedSearch], ToolResults(first.Snapshot()[1]), "loaded before reopening");
        fixture.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new Endpoint(() => Call("toolu_again", "tool_search", new { query = "documentation", limit = 1 }), () => Text("again"));
        await using (var rpc = new Rpc(Args(root, "open"), second, fixture.Host()))
        {
            await Connected(fixture, rpc);
            await rpc.Prompt("p2", "load docs again");
            Equal(0, await rpc.Finish(), "reopen exit code; " + rpc.Error);
        }
        var requests = second.Snapshot();
        Check(ToolNames(requests[0]).Contains("tool_search") && !ToolNames(requests[0]).Contains("mcp__docs__search"), "initial selection after reopening: " + string.Join(",", ToolNames(requests[0])));
        Names([LoadedSearch], ToolResults(requests[1]), "loaded again");
        Check(ToolNames(requests[1]).Contains("mcp__docs__search"), "declared after loading again");
    });

    // _isAllowedTool/_isActivatable: --tools without tool_search, or --exclude-tools tool_search, keeps tool_search out; the deferred
    // server still connects, its tools stay undeclared and the session warns once (ensureDiscoveryActive). Naming tool_search in
    // --tools keeps it, and it loads the unnamed MCP tools the allowlist kept registered.
    private static Task ToolSelectionGatesToolSearch() => WithRoot("gate", DeferredDocs, async (root, fixture) =>
    {
        foreach (var flags in new[] { new[] { "--tools", "read" }, ["--exclude-tools", "tool_search"], ["--tools", "read,mcp__docs__*"] })
        {
            fixture.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Servers.Clear();
            var provider = new Endpoint(() => Call("toolu_docs", "tool_search", new { query = "documentation" }), () => Text("refused"));
            await using var rpc = new Rpc(Args(root, "new-memory", flags), provider, fixture.Host());
            await Connected(fixture, rpc);
            await rpc.Prompt("p1", "load docs");
            Equal(0, await rpc.Finish(), string.Join(" ", flags) + " exit code; " + rpc.Error);
            Names([Unreachable], McpDiagnostics(rpc.Error.ToString()), string.Join(" ", flags) + " warning");
            Equal(1, fixture.Servers.Single().Lists, string.Join(" ", flags) + ": the deferred server still connects");
            foreach (var request in provider.Snapshot())
                Check(!ToolNames(request).Any(name => name == "tool_search" || name.StartsWith("mcp__", StringComparison.Ordinal)),
                    string.Join(" ", flags) + ": nothing reaches the deferred tools: " + string.Join(",", ToolNames(request)));
            Check(ToolResults(provider.Snapshot()[1]).Single().Length > 0 && !ToolResults(provider.Snapshot()[1]).Single().StartsWith("Loaded", StringComparison.Ordinal),
                string.Join(" ", flags) + ": undeclared tool_search is not run");
        }

        fixture.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Servers.Clear();
        var named = new Endpoint(() => Call("toolu_search", "tool_search", new { query = "search documentation", limit = 1 }), () => Text("loaded"));
        await using (var rpc = new Rpc(Args(root, "new-memory", "--tools", "read,tool_search"), named, fixture.Host()))
        {
            await Connected(fixture, rpc);
            await rpc.Prompt("p1", "load docs");
            Equal(0, await rpc.Finish(), "named exit code; " + rpc.Error);
            Names([], McpDiagnostics(rpc.Error.ToString()), "no warning with tool_search named");
        }
        var requests = named.Snapshot();
        Names(["read", "tool_search"], ToolNames(requests[0]), "named selection");
        Names([LoadedSearch], ToolResults(requests[1]), "named tool_search loads an unnamed MCP tool");
        Names(["read", "tool_search", "mcp__docs__search"], ToolNames(requests[1]), "loaded unnamed MCP tool declared");
    });

    // Codemode is a later release: a server whose tools need codemode is still reported and not connected without a codemode
    // implementation, while its deferred neighbour connects.
    private static Task CodemodeServersStillSkipped() => WithRoot("codemode",
        """{"mcpServers":{"docs":{"command":"docs-server","exposure":"deferred"},"scripts":{"command":"scripts-server"},"mixed":{"command":"mixed-server","exposure":"deferred","toolExposure":{"run":"codemode"}}}}""",
        async (root, fixture) =>
    {
        var provider = new Endpoint();
        await using var rpc = new Rpc(Args(root, "new-memory"), provider, fixture.Host());
        await Connected(fixture, rpc);
        await rpc.Prompt("p1", "hello");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        Names(["MCP servers need attention:\n" +
            "  scripts: not connected: its codemode tools need the codemode tool, which PiSharp does not implement yet; set \"exposure\": \"deferred\" or \"direct\" to use it\n" +
            "  mixed: not connected: its codemode tools need the codemode tool, which PiSharp does not implement yet; set \"exposure\": \"deferred\" or \"direct\" to use it"],
            McpDiagnostics(rpc.Error.ToString()), "codemode servers reported");
        Equal(1, fixture.Servers.Count, "only the deferred server connected");
        Check(ToolNames(provider.Snapshot().Single()).Contains("tool_search"), "tool_search active for the deferred server");
    });
}
