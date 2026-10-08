using PiSharp.Cli.Mcp;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// MCP tool calls in production sessions follow Pi: a server configured in mcp.json is trusted, and its calls run through the tool
// pipeline with no approval step of their own (extensions/mcp/index.ts, docs/mcp.md "Permissions"). The profile's final-action
// policy admits the invoke actions of the tools of servers admitted in the session's own generation, by exact tool name and target.
internal static partial class Program
{
    private const string Found = "Found: install guide.";

    // A direct server connects before the session opens; the model calls its tool and the server's result reaches the transcript.
    private static Task DirectServerCallSucceeds() => WithRoot("direct", """{"mcpServers":{"web":{"command":"web-server","exposure":"direct"}}}""",
        async (root, fixture) =>
    {
        var provider = new Endpoint(() => Call("toolu_web", "mcp__web__search", new { query = "install" }), () => Text("done"));
        await using (var rpc = new Rpc(Args(root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "search the web docs");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(ToolNames(requests[0]).Contains("mcp__web__search") && !ToolNames(requests[0]).Contains("tool_search"), "direct tool declared: " + string.Join(",", ToolNames(requests[0])));
        Names([Found], ToolResults(requests[1]), "direct MCP tool result");
        Names(["search:{\"query\":\"install\"}"], fixture.Servers.Single().Calls, "the call reached the server");
    });

    // A background server that connects only after the first prompt: tool_search finds nothing then; once its tools register
    // (at the next idle boundary), tool_search loads one and its call succeeds.
    private static Task LateBackgroundServerCallSucceeds() => WithRoot("late", DeferredDocs, async (root, fixture) =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Initialized = release.Task;
        var provider = new Endpoint(() => Call("toolu_early", "tool_search", new { query = "search documentation", limit = 1 }), () => Text("nothing yet"),
            () => Call("toolu_search", "tool_search", new { query = "search documentation", limit = 1 }),
            () => Call("toolu_docs", "mcp__docs__search", new { query = "install" }), () => Text("done"));
        await using (var rpc = new Rpc(Args(root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "find the install guide");
            release.TrySetResult();
            await Connected(fixture, rpc);
            await rpc.Prompt("p2", "try again");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Names(["No matching tools found."], ToolResults(requests[1]), "before the server registered its tools");
        Names([LoadedSearch], ToolResults(requests[3]), "after it registered");
        Names([Found], ToolResults(requests[4]), "late background server's tool result");
        Names(["search:{\"query\":\"install\"}"], fixture.Servers.Single().Calls, "the call reached the late server");
    });

    // A codemode server's tools are registered but not declared (PiSharp 1.1.0.3 connects it with the built-in codemode), so a direct
    // call fails before the policy; a deferred tool that tool_search did not load is not callable either. Input that arrives while
    // the second server publishes its tools waits for the publication instead of being refused.
    private static Task SkippedServerCallsFail() => WithRoot("skipped",
        """{"mcpServers":{"docs":{"command":"docs-server","exposure":"deferred"},"scripts":{"command":"scripts-server"}}}""", async (root, fixture) =>
    {
        var provider = new Endpoint(() => Call("toolu_scripts", "mcp__scripts__run", new { }), () => Call("toolu_docs", "mcp__docs__search", new { query = "x" }),
            () => Text("done"));
        await using (var rpc = new Rpc(Args(root, "new-memory"), provider, fixture.Host()))
        {
            await Connected(fixture, rpc);
            await rpc.Prompt("p1", "run scripts");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Names(["Tool mcp__scripts__run not found"], ToolResults(requests[1]), "codemode server's tool called directly");
        Names(["Tool mcp__docs__search not found"], ToolResults(requests[2]), "deferred tool that was not loaded");
        Check(fixture.Servers.All(server => server.Calls.IsEmpty), "no call reached any server");
    });

    private sealed class NoExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The grant rule: exact tool name and target of an admitted server scope, or an exactly admitted host tool. Tools of other
    // scopes (skipped, removed or retired servers), non-MCP names and mismatched names are refused.
    private static async Task CallGrantRule()
    {
        await using var registry = new ExtensionRegistry();
        var docs = await registry.ActivateAsync("mcp-docs", new NoExtension(), CancellationToken.None);
        var grants = new McpCallGrants();
        grants.AdmitServer(docs);
        grants.AdmitExact("tool_search", "mcp-discovery/1/tool-search");
        var prefix = $"mcp-docs/{docs.OwnerGeneration}/";
        Check(grants.Allows("mcp__docs__search", prefix + "mcp__docs__search"), "admitted server tool");
        Check(grants.Allows("tool_search", "mcp-discovery/1/tool-search"), "admitted host tool");
        Check(!grants.Allows("mcp__docs__search", prefix + "mcp__docs__fetch"), "name and target must match");
        Check(!grants.Allows("mcp__docs__search", $"mcp-docs/{docs.OwnerGeneration + 1}/mcp__docs__search"), "another owner generation");
        Check(!grants.Allows("mcp__scripts__run", "mcp-scripts/1/mcp__scripts__run"), "a server that was never admitted");
        Check(!grants.Allows("helper", prefix + "helper"), "non-MCP names are not granted by server scope");
        Check(!grants.Allows("tool_search", "mcp-discovery/2/tool-search"), "host tool of another generation");
        Check(!new McpCallGrants().Allows("mcp__docs__search", prefix + "mcp__docs__search"), "grants of another session generation");
    }
}
