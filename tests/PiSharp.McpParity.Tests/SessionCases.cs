using PiSharp.Cli.Mcp;

// Upstream: extensions/mcp/config.ts (loadMcpConfig: project entries with trust, overrides, auth only in the global file),
// extensions/mcp/index.ts (session_start connects every server in the background, waitForDirectServers with startupWaitMs,
// reportProblems once the connections settled, syncResourceTools, session_shutdown records nothing), extensions/tool-search/index.ts
// (registered inactive), docs/mcp.md and test/suite/agent-session-mcp.test.ts ("holds the first prompt for servers with direct tools
// only up to startupWaitMs", "lists and reads resources with Codex's resource tools").
internal static partial class Program
{
    private const string DirectDocs = """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""";

    // Without project trust the project's .pi/mcp.json is not read, and nothing is reported (the trust flow decided).
    private static Task UntrustedProject() => WithRoot("untrusted", DirectDocs, async fixture =>
    {
        var provider = new Endpoint();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "hello");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names([], McpDiagnostics(rpc.Error.ToString()), "nothing reported");
        }
        var declared = ToolNames(provider.Snapshot().Single());
        Check(declared.Contains("mcp__docs__search") && !declared.Any(name => name.StartsWith("mcp__team", StringComparison.Ordinal)), "global server only: " + string.Join(",", declared));
        Names(["docs"], fixture.Servers.Select(server => server.Name), "only the global server connected");
    }, projectJson: """{"mcpServers":{"team":{"command":"team-server","exposure":"direct"},"docs":{"enabled":false}}}""");

    // A trusted project adds its servers, turns off a global one with an override, and cannot use auth.provider.
    private static Task TrustedProject() => WithRoot("trusted",
        """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"},"web":{"command":"web-server","exposure":"direct"}}}""", async fixture =>
    {
        var provider = new Endpoint();
        var project = Path.Combine(fixture.Project, ".pi", "mcp.json");
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host(trusted: cwd => cwd == fixture.Project)))
        {
            await rpc.Prompt("p1", "hello");
            var manager = await fixture.Manager.Task;
            var docs = manager.Servers.Single(server => server.Name == "docs");
            Equal(project, docs.Entry.Override, "the override is recorded"); Equal("disabled", docs.State, "overridden off");
            Equal(project, manager.ProjectConfig, "project config of a trusted project");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names([$"MCP servers need attention:\n  config: {project}: server \"remote\": auth is only allowed in the global mcp.json"],
                McpDiagnostics(rpc.Error.ToString()), "the project's auth.provider is refused");
        }
        var declared = ToolNames(provider.Snapshot().Single());
        Check(declared.Contains("mcp__team__search") && declared.Contains("mcp__web__search") && !declared.Contains("mcp__docs__search"),
            "project server added, global one turned off: " + string.Join(",", declared));
        Names(["team", "web"], fixture.Servers.Select(server => server.Name).Order(StringComparer.Ordinal), "connected servers");
    }, projectJson: """{"mcpServers":{"team":{"command":"team-server","exposure":"direct"},"docs":{"enabled":false},"remote":{"url":"https://remote.example.test/mcp","auth":{"provider":"anthropic"}}}}""");

    // Servers connect in the background; the first prompt waits for servers with direct tools, so its request declares them.
    private static Task FirstPromptWaitsForDirectServers() => WithRoot("direct-wait", DirectDocs, async fixture =>
    {
        fixture.Behaviors["docs"] = (["search"], Task.Delay(700), false, null);
        var provider = new Endpoint();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "hello");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names([], McpDiagnostics(rpc.Error.ToString()), "nothing reported");
        }
        Check(ToolNames(provider.Snapshot().Single()).Contains("mcp__docs__search"), "the first request declares the direct tool");
    });

    // ...but only up to the startup wait (10 s; 1 s here): the prompt then proceeds, a notice says the servers are still connecting,
    // and the server's tools are declared once it connected.
    private static Task FirstPromptWaitCap() => WithRoot("direct-cap", DirectDocs, async fixture =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Behaviors["docs"] = (["search"], release.Task, false, null);
        var provider = new Endpoint();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host(startupWait: TimeSpan.FromSeconds(1))))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            await rpc.Prompt("p1", "hello");
            Check(started.Elapsed >= TimeSpan.FromSeconds(0.9) && started.Elapsed < TimeSpan.FromSeconds(20), "waited about the cap: " + started.Elapsed);
            Names([McpBackgroundConnections.StillConnecting], McpDiagnostics(rpc.Error.ToString()), "still connecting notice");
            release.TrySetResult();
            await Settled(fixture, rpc, 1);
            await rpc.Prompt("p2", "again");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(!ToolNames(requests[0]).Contains("mcp__docs__search"), "the first request goes out without the slow server");
        Check(ToolNames(requests[1]).Contains("mcp__docs__search"), "declared once connected");
    });

    // reportProblems: config errors and failed servers in one message, once every server settled.
    private static Task ProblemsReportedOnce() => WithRoot("problems",
        """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"},"broken":{"command":"broken-server","exposure":"direct"},"late":{"command":"late-server","exposure":"deferred"},"bad":{"command":5}}}""",
        async fixture =>
    {
        fixture.Behaviors["broken"] = (["x"], null, false, new IOException("initialize refused\nsecond line"));
        fixture.Behaviors["late"] = (["lookup"], Task.Delay(800), false, null);
        var global = Path.Combine(fixture.Agent, "mcp.json");
        await using var rpc = new Rpc(Args(fixture.Root, "new-memory"), new Endpoint(), fixture.Host());
        await rpc.Prompt("p1", "hello");
        await Settled(fixture, rpc, 3);
        await Task.Delay(200);
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        Names([$"MCP servers need attention:\n  config: {global}: server \"bad\" needs either \"command\" (stdio) or \"url\" (streamable HTTP)\n  broken: failed: initialize refused"],
            McpDiagnostics(rpc.Error.ToString()), "one report after all settled");
    });

    // syncResourceTools: a direct server with resources registers the resource tools as direct, so the first request declares them,
    // and read_mcp_resource reads from the server.
    private static Task DirectResources() => WithRoot("resources-direct", DirectDocs, async fixture =>
    {
        fixture.Behaviors["docs"] = (["search"], null, true, null);
        var provider = new Endpoint(() => Call("toolu_read", "read_mcp_resource", new { server = "docs", uri = "docs://guide" }),
            () => Call("toolu_list", "list_mcp_resources", new { server = "docs" }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "read the guide");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        foreach (var name in McpResourceToolsPublisher.ToolNames) Check(ToolNames(requests[0]).Contains(name), name + " declared: " + string.Join(",", ToolNames(requests[0])));
        Check(ToolResults(requests[1]).Single().Contains("Install docs with npm.", StringComparison.Ordinal), "resource read: " + ToolResults(requests[1]).Single());
        Check(ToolResults(requests[2]).Single().Contains("docs://guide", StringComparison.Ordinal), "resource listed: " + ToolResults(requests[2]).Single());
    });

    // A deferred server with resources registers the resource tools with deferred exposure: not declared until tool_search loads them.
    private static Task DeferredResources() => WithRoot("resources-deferred", """{"mcpServers":{"docs":{"command":"docs-server","exposure":"deferred"}}}""", async fixture =>
    {
        fixture.Behaviors["docs"] = (["search"], null, true, null);
        var provider = new Endpoint(() => Call("toolu_search", "tool_search", new { query = "read specific resource", limit = 1 }),
            () => Call("toolu_read", "read_mcp_resource", new { server = "docs", uri = "docs://guide" }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "read the guide");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(!ToolNames(requests[0]).Any(name => McpResourceToolsPublisher.ToolNames.Contains(name)), "not declared before loading");
        Names(["Loaded 1 tool. They are available from your next call:\n- read_mcp_resource: Read a specific resource from an MCP server given the server name and resource URI."],
            ToolResults(requests[1]), "tool_search loads the resource tool");
        Check(ToolResults(requests[2]).Single().Contains("Install docs with npm.", StringComparison.Ordinal), "resource read after loading");
    });

    // tool-search/index.ts: tool_search is registered with every session, inactive, so --tools selects it without deferred servers.
    private static Task ToolSearchInactive() => WithRoot("tool-search-inactive", DirectDocs, async fixture =>
    {
        var plain = new Endpoint();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), plain, fixture.Host()))
        {
            await rpc.Prompt("p1", "hello");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        Check(!ToolNames(plain.Snapshot().Single()).Contains("tool_search"), "inactive by default");
        var named = new Endpoint(() => Call("toolu_search", "tool_search", new { query = "fetch", limit = 1 }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory", "--tools", "read,tool_search"), named, fixture.Host()))
        {
            await rpc.Prompt("p1", "hello");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names([], McpDiagnostics(rpc.Error.ToString()), "nothing reported");
        }
        Names(["read", "tool_search"], ToolNames(named.Snapshot()[0]), "--tools names it");
        Names(["No matching tools found."], ToolResults(named.Snapshot()[1]), "it runs (the direct server's tools are not searchable)");
    });

    // session_shutdown records nothing: the session file's last loadout still names the direct server's tool.
    private static Task CloseRecordsNoWithdrawal() => WithRoot("close", DirectDocs, async fixture =>
    {
        await using (var rpc = new Rpc(Args(fixture.Root, "new-lazy"), new Endpoint(), fixture.Host()))
        {
            await rpc.Prompt("p1", "hello");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var loadouts = RecordedLoadouts(fixture.Root);
        Check(loadouts.Length > 0 && loadouts[^1].Contains("mcp__docs__search"), "last loadout keeps the server's tool: " + string.Join(" | ", loadouts.Select(names => string.Join(",", names))));
        Equal(1, fixture.Server("docs").Closes, "the server closed with the session");
    });

    // A server that connects while the session shuts down: its catalog publication either completes or is dropped, and the session
    // never faults (exit code 0, durable file readable).
    private static async Task CloseDuringPublication()
    {
        for (var iteration = 0; iteration < 12; iteration++)
        {
            await WithRoot("close-race-" + iteration, """{"mcpServers":{"docs":{"command":"docs-server","exposure":"deferred"}}}""", async fixture =>
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Behaviors["docs"] = (["search"], release.Task, false, null);
                // --tools read keeps tool_search out, so no prompt waits for the server.
                await using var rpc = new Rpc(Args(fixture.Root, "new-lazy", "--tools", "read"), new Endpoint(), fixture.Host());
                await rpc.Prompt("p1", "hello");
                // Release the server and close at once, staggered so the close lands at different points of the publication.
                release.TrySetResult();
                if (iteration % 3 == 1) await Task.Yield(); else if (iteration % 3 == 2) await Task.Delay(iteration);
                var exit = await rpc.Finish();
                Equal(0, exit, $"iteration {iteration} exit code; {rpc.Error}");
                Check(!rpc.Error.ToString().Contains("InvalidCommit", StringComparison.Ordinal) && !rpc.Error.ToString().Contains("AppendFailed", StringComparison.Ordinal),
                    "no session fault: " + rpc.Error);
                _ = RecordedLoadouts(fixture.Root);
            });
        }
    }
}
