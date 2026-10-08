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

// Production sessions (RpcSessionCommand with an McpSessionHost over a temp agent directory) against a fake Anthropic endpoint and
// fake MCP servers, as the ToolSearch and CliSync suites drive them.
internal static partial class Program
{
    internal sealed record Seen(string Url, string? Body);

    /// <summary>Fake provider endpoint: records every request and answers each with the next scripted response.</summary>
    internal sealed class Endpoint(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
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
    internal static HttpResponseMessage Text(string text) => Stream(new { type = "text", text = "" }, text, "end_turn");
    internal static HttpResponseMessage Call(string id, string name, object input) =>
        Stream(new { type = "tool_use", id, name, input = new { } }, JsonSerializer.Serialize(input), "tool_use");

    /// <summary>A fake MCP server: tools, optional instructions and resources; tools/call answers with the call it received.</summary>
    internal sealed class FakeServer(string name, string[] tools, Task? initialized = null, bool resources = false, Exception? failure = null) : IMcpAdmittedRequestChannel
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
                    if (failure is not null) throw failure;
                    return JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"" + name + "\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}" +
                        (resources ? ",\"resources\":{}" : "") + "},\"instructions\":\"The " + name + " server.\"}");
                case "tools/list":
                    Interlocked.Increment(ref Lists);
                    return JsonData.Parse(JsonSerializer.Serialize(new
                    {
                        tools = tools.Select(tool => new { name = tool, description = $"The {tool} tool of {name}.", inputSchema = new { type = "object", properties = new { query = new { type = "string" } } } })
                    }));
                case "tools/call":
                    var call = parameters!.Value;
                    Calls.Enqueue(call.GetProperty("name").GetString() + ":" + call.GetProperty("arguments").GetRawText());
                    return JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"" + name + " answered.\"}]}");
                case "resources/list":
                    return JsonData.Parse("{\"resources\":[{\"uri\":\"" + name + "://guide\",\"name\":\"guide\",\"mimeType\":\"text/plain\"}]}");
                case "resources/templates/list":
                    return JsonData.Parse("{\"resourceTemplates\":[]}");
                case "resources/read":
                    return JsonData.Parse("{\"contents\":[{\"uri\":\"" + name + "://guide\",\"mimeType\":\"text/plain\",\"text\":\"Install " + name + " with npm.\"}]}");
                default: throw new IOException("Unexpected MCP method " + method);
            }
        }
        public Task CloseAsync() { Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    }

    /// <summary>One production RPC host over a bounded in-memory connection.</summary>
    internal sealed class Rpc : IAsyncDisposable
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

    internal static string[] Args(string root, string mode, params string[] extra) =>
        ["session", "rpc", "--session", Path.Combine(root, "session.jsonl"), "--workspace", Path.Combine(root, "project"), "--live", "--provider", "anthropic",
            "--model", "claude-sonnet-4-5", "--session-mode", mode, .. extra];

    /// <summary>A temp root with <c>agent/</c> (the agent directory) and <c>project/</c> (the workspace).</summary>
    internal sealed class Fixture(string root)
    {
        public readonly string Root = root;
        public readonly string Agent = Path.Combine(root, "agent");
        public readonly string Project = Path.Combine(root, "project");
        public readonly ConcurrentBag<FakeServer> Servers = [];
        /// <summary>Per server name: the tools it offers, when initialize answers, and whether it has resources.</summary>
        public readonly ConcurrentDictionary<string, (string[] Tools, Task? Initialized, bool Resources, Exception? Failure)> Behaviors = new();
        public readonly ConcurrentQueue<McpBackgroundConnectionReport> Reports = new();
        public readonly TaskCompletionSource<McpServerManager> Manager = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public McpSessionHost Host(Func<string, bool>? trusted = null, TimeSpan? startupWait = null) =>
            new(Agent, Path.Combine(Root, "home"), () => [KeyValuePair.Create("PATH", Root)])
            {
                CreateChannel = entry => entry.Config.Transport == PiSharp.Extensions.Mcp.Configuration.McpTransportKind.Http ? null : (actual, token) =>
                {
                    var behavior = Behaviors.GetValueOrDefault(actual.Name, (["search", "fetch"], null, false, null));
                    var server = new FakeServer(actual.Name, behavior.Tools, behavior.Initialized, behavior.Resources, behavior.Failure); Servers.Add(server);
                    return ValueTask.FromResult<IMcpAdmittedRequestChannel>(server);
                },
                ObserveBackgroundConnection = Reports.Enqueue,
                ObserveManager = manager => Manager.TrySetResult(manager),
                IsProjectTrusted = trusted ?? (_ => false),
                StartupWait = startupWait ?? McpBackgroundConnections.DefaultStartupWait
            };
        public FakeServer Server(string name) => Servers.Single(server => server.Name == name);
    }

    internal static async Task WithRoot(string name, string mcpJson, Func<Fixture, Task> run, string? projectJson = null)
    {
        var root = Path.GetFullPath(Temp(name + "-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        var fixture = new Fixture(root); Directory.CreateDirectory(fixture.Agent); Directory.CreateDirectory(fixture.Project);
        await File.WriteAllTextAsync(Path.Combine(fixture.Agent, "mcp.json"), mcpJson);
        if (projectJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(fixture.Project, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(fixture.Project, ".pi", "mcp.json"), projectJson);
        }
        try { await run(fixture); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Waits until every background server settled (connected or failed).</summary>
    internal static async Task Settled(Fixture fixture, Rpc rpc, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (fixture.Reports.Count < count)
        {
            if (rpc.Completion.IsCompleted) throw new InvalidOperationException($"RPC host ended before the MCP servers settled; exit {await rpc.Completion}; {rpc.Error}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("MCP servers did not settle; " + rpc.Error);
            await Task.Delay(20);
        }
    }

    internal static string[] ToolNames(Seen request) =>
        JsonDocument.Parse(request.Body!).RootElement.TryGetProperty("tools", out var tools) ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)] : [];

    /// <summary>The text of each tool_result block in the request's last message.</summary>
    internal static string[] ToolResults(Seen request) => [.. JsonDocument.Parse(request.Body!).RootElement.GetProperty("messages").EnumerateArray().Last()
        .GetProperty("content").EnumerateArray().Where(block => block.GetProperty("type").GetString() == "tool_result")
        .Select(block => block.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
            : string.Concat(block.GetProperty("content").EnumerateArray().Select(part => part.TryGetProperty("text", out var value) ? value.GetString() : "")))];

    internal static string[] McpDiagnostics(string error) => [.. error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()).Where(line => line.StartsWith('{'))
        .Select(line => JsonDocument.Parse(line).RootElement).Where(json => json.TryGetProperty("type", out var type) && type.GetString() == "mcp_diagnostic")
        .Select(json => json.GetProperty("message").GetString()!)];

    /// <summary>The tool names of each loadout recorded in the session file, in order.</summary>
    internal static string[][] RecordedLoadouts(string root) => [.. File.ReadAllLines(Path.Combine(root, "session.jsonl")).Select(line => JsonDocument.Parse(line).RootElement)
        .Where(entry => entry.TryGetProperty("message", out var message) && message.TryGetProperty("toolsAdded", out _))
        .Select(entry => entry.GetProperty("message").GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray())];
}
