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
// and fake MCP servers whose tools default to `codemode` exposure. Upstream: extensions/mcp/index.ts (codemode servers connect
// in the background; ensureDiscoveryActive activates codemode unless autoEnableCodemode is false), extensions/codemode (the tool,
// nested calls through ctx.executeTool, codemode-store entries on the branch) and test/suite/agent-session-codemode.test.ts.
internal static partial class Program
{
    private sealed record Seen(string Url, string? Body);

    private sealed class Endpoint(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private readonly List<Seen> requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new Seen(request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            int index; lock (requests) { requests.Add(seen); index = requests.Count - 1; }
            if (seen.Url != MessagesUrl) throw new InvalidOperationException("Unexpected URL " + seen.Url);
            return index < script.Length ? script[index]() : TextReply("ok");
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
    private static HttpResponseMessage TextReply(string text) => Stream(new { type = "text", text = "" }, text, "end_turn");
    private static HttpResponseMessage CallReply(string id, string name, object input) =>
        Stream(new { type = "tool_use", id, name, input = new { } }, JsonSerializer.Serialize(input), "tool_use");

    /// <summary>A fake `docs` MCP server; tools/call answers with the query it received and records the invocation identity.</summary>
    private sealed class DocsServer(TimeSpan initializeDelay = default) : IMcpAdmittedRequestChannel
    {
        public readonly ConcurrentQueue<(string Call, McpInvocationIdentity? Identity)> Calls = new();
        public int Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            switch (method)
            {
                case "initialize":
                    if (initializeDelay > TimeSpan.Zero) await Task.Delay(initializeDelay, token);
                    return (JsonData.Parse("""{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{}},"instructions":"Search and fetch the docs."}"""));
                case "tools/list":
                    return (JsonData.Parse("""
                        {"tools":[
                          {"name":"search","description":"Search the documentation.","inputSchema":{"type":"object","properties":{"query":{"type":"string"}}}},
                          {"name":"fetch","description":"Fetch a page by URL.","inputSchema":{"type":"object","properties":{"url":{"type":"string"}}}}]}
                        """));
                case "tools/call":
                    var call = parameters!.Value;
                    Calls.Enqueue((call.GetProperty("name").GetString() + ":" + call.GetProperty("arguments").GetRawText(), options.InvocationIdentity));
                    return JsonData.Parse("""{"content":[{"type":"text","text":"Found: install guide."}],"structuredContent":{"hits":1}}""");
                default: throw new IOException("Unexpected MCP method " + method);
            }
        }
        public Task CloseAsync() { Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    }

    private sealed class Rpc : IAsyncDisposable
    {
        private readonly Channel<JsonData> records = System.Threading.Channels.Channel.CreateUnbounded<JsonData>();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
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

    private static string[] SessionArgs(string root, string mode, params string[] extra) =>
        ["session", "rpc", "--session", Path.Combine(root, "session.jsonl"), "--workspace", root, "--live", "--provider", "anthropic",
            "--model", "claude-sonnet-4-5", "--session-mode", mode, .. extra];

    private sealed class Fixture(string root)
    {
        public readonly string Agent = Path.Combine(root, "agent");
        public readonly ConcurrentBag<DocsServer> Servers = [];
        public TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan InitializeDelay;
        public McpSessionHost Host() => new(Agent, Path.Combine(root, "home"), () => [KeyValuePair.Create("PATH", root)])
        {
            CreateChannel = entry => (actual, token) => { var server = new DocsServer(InitializeDelay); Servers.Add(server); return ValueTask.FromResult<IMcpAdmittedRequestChannel>(server); },
            ObserveBackgroundConnection = report => { if (report.Failure is null) Connected.TrySetResult(); else Connected.TrySetException(report.Failure); },
            CodemodeModels = () => null
        };
    }

    private static async Task WaitConnected(Fixture fixture, Rpc rpc)
    {
        var settled = await Task.WhenAny(fixture.Connected.Task, rpc.Completion, Task.Delay(TimeSpan.FromSeconds(30)));
        if (settled == rpc.Completion) throw new InvalidOperationException($"RPC host ended before the MCP server connected; exit {await rpc.Completion}; {rpc.Error}");
        if (settled != fixture.Connected.Task) throw new TimeoutException("MCP server did not connect; " + rpc.Error);
        await fixture.Connected.Task;
    }

    private static async Task WithSessionRoot(string name, string? mcpJson, Func<string, Fixture, Task> run)
    {
        var root = Temp(name); Directory.CreateDirectory(root);
        var fixture = new Fixture(root); Directory.CreateDirectory(fixture.Agent);
        if (mcpJson is not null) await File.WriteAllTextAsync(Path.Combine(fixture.Agent, "mcp.json"), mcpJson);
        try { await run(root, fixture); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static JsonElement[] DeclaredTools(Seen request) =>
        JsonDocument.Parse(request.Body!).RootElement.TryGetProperty("tools", out var tools) ? [.. tools.EnumerateArray()] : [];
    private static string[] ToolNamesOf(Seen request) => [.. DeclaredTools(request).Select(tool => tool.GetProperty("name").GetString()!)];
    private static string[] ToolResultTexts(Seen request) => [.. JsonDocument.Parse(request.Body!).RootElement.GetProperty("messages").EnumerateArray().Last()
        .GetProperty("content").EnumerateArray().Where(block => block.GetProperty("type").GetString() == "tool_result")
        .Select(block => block.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
            : string.Join("\n", block.GetProperty("content").EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString())))];
    private static string[] Diagnostics(string error) => [.. error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()).Where(line => line.StartsWith('{'))
        .Select(line => JsonDocument.Parse(line).RootElement).Where(json => json.TryGetProperty("type", out var type) && type.GetString() == "mcp_diagnostic")
        .Select(json => json.GetProperty("message").GetString()!)];
    private static JsonElement[] SessionEntries(string root) => [.. File.ReadAllLines(Path.Combine(root, "session.jsonl")).Select(line => JsonDocument.Parse(line).RootElement)];

    private const string CodemodeDocs = """{"mcpServers":{"docs":{"command":"docs-server","description":"Documentation search."}}}""";
    private const string Increment = "const next = (load(\"count\") ?? 0) + 1;\nstore(\"count\", next);\nreturn next;";

    private static IEnumerable<(string, Func<Task>)> SessionCases() =>
    [
        // The release blocker of 1.1.0.1: servers whose tools default to `codemode` exposure now connect. The built-in codemode tool
        // is declared (active) for them; its scripts reach the MCP tools through the shared invoker, with the codemode call as their
        // parent, and resolve to the CallToolResult.
        Case("session.codemode-server-connects-and-scripts-call-its-tools", () => WithSessionRoot("codemode", CodemodeDocs, async (root, fixture) =>
        {
            var code = "const r = await tools.mcp__docs__search({ query: \"install\" });\ntext(r.content[0].text);\nreturn [r.structuredContent.hits, \"_meta\" in r, (await searchTools(\"documentation\"))[0].name];";
            var provider = new Endpoint(() => TextReply("ready"), () => CallReply("toolu_cm", "codemode", new { code }), () => TextReply("done"));
            await using var rpc = new Rpc(SessionArgs(root, "new-lazy"), provider, fixture.Host());
            await WaitConnected(fixture, rpc);
            await rpc.Prompt("p1", "hello");
            await rpc.Prompt("p2", "search the docs");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names([], Diagnostics(rpc.Error.ToString()), "no MCP problem reported");
            var requests = provider.Snapshot();
            Equal(3, requests.Length, "provider requests");
            var declared = DeclaredTools(requests[1]).Single(tool => tool.GetProperty("name").GetString() == "codemode");
            var description = declared.GetProperty("description").GetString()!;
            Check(description.StartsWith("Run JavaScript that calls other tools.", StringComparison.Ordinal), "codemode description: " + description);
            Check(!ToolNamesOf(requests[1]).Any(name => name.StartsWith("mcp__", StringComparison.Ordinal)), "codemode MCP tools are not declared");
            Equal("Raw JavaScript source.", declared.GetProperty("input_schema").GetProperty("properties").GetProperty("code").GetProperty("description").GetString(), "schema");
            var result = ToolResultTexts(requests[2]).Single();
            Check(result.StartsWith("Script completed\nWall time ", StringComparison.Ordinal) && result.Contains("==> text 1/2 <==\nFound: install guide.\n==> text 2/2 <==\n[1,false,\"mcp__docs__search\"]", StringComparison.Ordinal), "result: " + result);
            var (call, identity) = fixture.Servers.Single().Calls.Single();
            Equal("search:{\"query\":\"install\"}", call, "the server received the nested call");
            Equal("toolu_cm", identity?.ParentToolCallId, "nested call parent");
            Equal("toolu_cm/1", identity?.ToolCallId, "nested call id");
            var recorded = SessionEntries(root).Where(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "toolResult").Single();
            var details = recorded.GetProperty("message").GetProperty("details");
            Equal("mcp__docs__search:ok:toolu_cm/1", string.Join(",", details.GetProperty("calls").EnumerateArray().Select(row => row.GetProperty("name").GetString() + ":" +
                row.GetProperty("status").GetString() + ":" + row.GetProperty("id").GetString())), "call rows persisted with the result");
            Equal(1, fixture.Servers.Single().Closes, "server closed with the session");
        })),
        // Store writes are custom entries between the assistant message that called codemode and its result, so each branch sees the
        // values written on its own path (agent-session-codemode.test.ts "loads the values written on the current branch").
        Case("session.store-entries-follow-branches", () => WithSessionRoot("store", CodemodeDocs, async (root, fixture) =>
        {
            var first = new Endpoint(() => CallReply("toolu_1", "codemode", new { code = Increment }), () => TextReply("one"),
                () => CallReply("toolu_2", "codemode", new { code = Increment }), () => TextReply("two"));
            await using (var rpc = new Rpc(SessionArgs(root, "new-lazy"), first, fixture.Host()))
            {
                await WaitConnected(fixture, rpc);
                await rpc.Prompt("p1", "count");
                await rpc.Prompt("p2", "count again");
                Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            }
            Check(ToolResultTexts(first.Snapshot()[1]).Single().EndsWith("Output:\n\n1", StringComparison.Ordinal) || ToolResultTexts(first.Snapshot()[1]).Single().EndsWith("\n1", StringComparison.Ordinal), "first count: " + ToolResultTexts(first.Snapshot()[1]).Single());
            Check(ToolResultTexts(first.Snapshot()[3]).Single().EndsWith("\n2", StringComparison.Ordinal), "second count: " + ToolResultTexts(first.Snapshot()[3]).Single());
            var entries = SessionEntries(root);
            var stores = entries.Where(entry => entry.GetProperty("type").GetString() == "custom" && entry.GetProperty("customType").GetString() == "codemode-store").ToArray();
            Names(["{\"set\":{\"count\":1},\"delete\":[]}", "{\"set\":{\"count\":2},\"delete\":[]}"], stores.Select(entry => entry.GetProperty("data").GetRawText()), "store entries");
            JsonElement ById(string id) => entries.Single(entry => entry.TryGetProperty("id", out var value) && value.GetString() == id);
            var parent = ById(stores[0].GetProperty("parentId").GetString()!);
            Equal("assistant", parent.GetProperty("message").GetProperty("role").GetString(), "store entry follows the calling assistant message");
            var child = entries.Single(entry => entry.TryGetProperty("parentId", out var value) && value.GetString() == stores[0].GetProperty("id").GetString());
            Equal("toolResult", child.GetProperty("message").GetProperty("role").GetString(), "the tool result follows the store entry");
            // Branch from the end of the first prompt: the second prompt's write is on another path.
            var endOfFirst = entries.First(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "assistant" &&
                message.GetProperty("content").EnumerateArray().Any(block => block.TryGetProperty("text", out var text) && text.GetString() == "one"));
            fixture.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var branched = new Endpoint(() => CallReply("toolu_3", "codemode", new { code = Increment }), () => TextReply("branched"));
            await using (var rpc = new Rpc(SessionArgs(root, "open", "--leaf", endOfFirst.GetProperty("id").GetString()!), branched, fixture.Host()))
            {
                await WaitConnected(fixture, rpc);
                await rpc.Prompt("p3", "count on a branch");
                Equal(0, await rpc.Finish(), "branch exit code; " + rpc.Error);
            }
            Check(ToolResultTexts(branched.Snapshot()[1]).Single().EndsWith("\n2", StringComparison.Ordinal), "branch continues from 1: " + ToolResultTexts(branched.Snapshot()[1]).Single());
        })),
        // ensureDiscoveryActive: with autoEnableCodemode false, codemode stays off unless the tool selection names it; the session
        // warns once, with the reason. A selection without codemode warns without it.
        Case("session.auto-enable-codemode-and-tool-selection", () => WithSessionRoot("auto", """{"autoEnableCodemode":false,"mcpServers":{"docs":{"command":"docs-server"}}}""", async (root, fixture) =>
        {
            async Task<(Seen[] Requests, string Error)> Session(params string[] flags)
            {
                fixture.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var provider = new Endpoint(() => TextReply("hi"));
                await using var rpc = new Rpc(SessionArgs(root, "new-memory", flags), provider, fixture.Host());
                await WaitConnected(fixture, rpc);
                await rpc.Prompt("p1", "hello");
                Equal(0, await rpc.Finish(), string.Join(" ", flags) + " exit code; " + rpc.Error);
                return (provider.Snapshot(), rpc.Error.ToString());
            }
            var (requests, error) = await Session();
            Check(!ToolNamesOf(requests[0]).Contains("codemode"), "codemode off");
            Names(["MCP tools are only reachable from the codemode or tool_search tool, but neither is active (autoEnableCodemode is false); they cannot be called."], Diagnostics(error), "warning with reason");
            (requests, error) = await Session("--tools", "read,codemode");
            Names(["read", "codemode"], ToolNamesOf(requests[0]), "named codemode is active");
            Names([], Diagnostics(error), "no warning");
            await File.WriteAllTextAsync(Path.Combine(fixture.Agent, "mcp.json"), CodemodeDocs);
            (requests, error) = await Session("--tools", "read");
            Names(["read"], ToolNamesOf(requests[0]), "selection without codemode");
            Names(["MCP tools are only reachable from the codemode or tool_search tool, but neither is active; they cannot be called."], Diagnostics(error), "warning without reason");
            Equal(3, fixture.Servers.Count, "the server connects in every session");
        })),
        // codemode.mode only: active direct tools are left out of requests and listed in the codemode description instead.
        Case("session.codemode-mode-only-from-settings", () => WithSessionRoot("only", CodemodeDocs, async (root, fixture) =>
        {
            var settings = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(settings, """{"codemode":{"mode":"only"}}""");
            var provider = new Endpoint(() => TextReply("hi"));
            await using var rpc = new Rpc(SessionArgs(root, "new-memory", "--tools", "read,codemode", "--user-settings", settings), provider, fixture.Host());
            await WaitConnected(fixture, rpc);
            await rpc.Prompt("p1", "hello");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            var request = provider.Snapshot()[0];
            Names(["codemode"], ToolNamesOf(request), "read is hidden from requests");
            Check(DeclaredTools(request)[0].GetProperty("description").GetString()!.Contains("### `read`", StringComparison.Ordinal), "codemode lists read");
        })),
    ];
}
