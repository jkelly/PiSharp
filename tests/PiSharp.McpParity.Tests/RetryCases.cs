using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

// Upstream: extensions/mcp/runtime.ts (open: CONNECT_RETRY_DELAYS_MS for HTTP servers that fail transiently; withClient: a call on an
// expired session is retried once on a new session, reads are retried once after a transient HTTP error, tool calls are not) and
// test/mcp-extension.test.ts ("retries HTTP connections that fail with a transient error", "starts a new session and retries once
// when the session expired", "retries resource reads, but not tool calls, after a transient HTTP error").
internal static partial class Program
{
    /// <summary>A streamable HTTP MCP server whose answers can fail: <see cref="Fail"/> maps a method to the statuses its next
    /// requests answer with, before the method succeeds.</summary>
    private sealed class FlakyHttpServer : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string, ConcurrentQueue<int>> Fail = new(StringComparer.Ordinal);
        public readonly ConcurrentQueue<string> Methods = new();
        public int Sessions, Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.MethodNotAllowed);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var method = json.RootElement.GetProperty("method").GetString()!;
            Methods.Enqueue(method);
            if (Fail.TryGetValue(method, out var statuses) && statuses.TryDequeue(out var status)) return new((HttpStatusCode)status);
            if (!json.RootElement.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            if (method == "tools/call") Interlocked.Increment(ref Calls);
            var result = method switch
            {
                "initialize" => """{"protocolVersion":"2025-11-25","serverInfo":{"name":"plain","version":"1"},"capabilities":{"tools":{},"resources":{}}}""",
                "tools/list" => """{"tools":[{"name":"get","inputSchema":{"type":"object"}}]}""",
                "tools/call" => """{"content":[{"type":"text","text":"called"}]}""",
                "resources/list" => """{"resources":[{"uri":"plain://doc","name":"doc"}]}""",
                "resources/templates/list" => """{"resourceTemplates":[]}""",
                "resources/read" => """{"contents":[{"uri":"plain://doc","text":"the doc"}]}""",
                _ => "{}"
            };
            var response = Json(200, "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}");
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "session-" + Interlocked.Increment(ref Sessions));
            return response;
        }
    }

    private const string PlainHttp = """{"mcpServers":{"plain":{"url":"https://plain.example.test/mcp","headers":{"Authorization":"Bearer fixed"},"exposure":"direct"}}}""";

    // Two transient failures are retried (250 ms and 1 s later); a third leaves the server failed.
    private static Task ConnectRetries() => WithRoot("connect-retry", PlainHttp, async fixture =>
    {
        var server = new FlakyHttpServer(); server.Fail["initialize"] = new([503, 429]);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), new Endpoint(), fixture.Host() with { CreateHttpHandler = () => server }))
        {
            await rpc.Prompt("p1", "hello");
            Equal("connected", (await fixture.Manager.Task).Servers.Single().State, "connected after two retries");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        Check(started.Elapsed >= TimeSpan.FromSeconds(1.2), "waited between attempts: " + started.Elapsed);
        Equal(3, server.Methods.Count(method => method == "initialize"), "three attempts");
    });

    private static Task ConnectRetriesExhausted() => WithRoot("connect-retry-failed", PlainHttp, async fixture =>
    {
        var server = new FlakyHttpServer(); server.Fail["initialize"] = new([503, 503, 500]);
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), new Endpoint(), fixture.Host() with { CreateHttpHandler = () => server }))
        {
            await rpc.Prompt("p1", "hello");
            await Settled(fixture, rpc, 1);
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names(["MCP servers need attention:\n  plain: failed: MCP HTTP status 500"], McpDiagnostics(rpc.Error.ToString()), "failed after the retries");
        }
        Equal(3, server.Methods.Count(method => method == "initialize"), "no fourth attempt");
    });

    // A call on an expired session (404) runs again on a new session; a 503 resource read is retried once.
    private static Task CallAndReadRetries() => WithRoot("call-retry", PlainHttp, async fixture =>
    {
        var server = new FlakyHttpServer();
        var provider = new Endpoint(() => Call("t1", "mcp__plain__get", new { }), () => Call("t2", "mcp__plain__get", new { }),
            () => Call("t3", "read_mcp_resource", new { server = "plain", uri = "plain://doc" }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host() with { CreateHttpHandler = () => server }))
        {
            await Settled(fixture, rpc, 1);
            // The first call finds its session expired and runs on a new one; the read answers 503 once.
            server.Fail["tools/call"] = new([404]);
            server.Fail["resources/read"] = new([503]);
            await rpc.Prompt("p1", "call");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Equal(2, server.Sessions, "a new session after the expired one");
        }
        var requests = provider.Snapshot();
        Names(["called"], ToolResults(requests[1]), "the expired call ran on the new session");
        Names(["called"], ToolResults(requests[2]), "the second call");
        Check(ToolResults(requests[3]).Single().Contains("the doc", StringComparison.Ordinal), "the read was retried: " + ToolResults(requests[3]).Single());
        Equal(2, server.Calls, "each call ran once");
    });

    private static Task CallsAreNotRetried() => WithRoot("call-no-retry", PlainHttp, async fixture =>
    {
        var server = new FlakyHttpServer();
        var provider = new Endpoint(() => Call("t1", "mcp__plain__get", new { }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host() with { CreateHttpHandler = () => server }))
        {
            await Settled(fixture, rpc, 1);
            server.Fail["tools/call"] = new([503]);
            await rpc.Prompt("p1", "call");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        Check(ToolResults(provider.Snapshot()[1]).Single().Contains("503", StringComparison.Ordinal), "the failure reaches the model: " + ToolResults(provider.Snapshot()[1]).Single());
        Equal(1, server.Methods.Count(method => method == "tools/call"), "not retried");
    });
}
