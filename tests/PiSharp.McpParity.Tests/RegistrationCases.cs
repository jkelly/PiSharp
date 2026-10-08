using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;

// Upstream: core/extensions/loader.ts (registerMcpServer, unregisterMcpServer, getMcpServers), core/mcp-servers.ts (McpServerRegistry),
// core/extensions/runner.ts (bind: mcp_servers_change with every registered server), extensions/mcp/index.ts (registeredServers,
// mcp_servers_change: registered servers connect and disconnect during the session; mcp.json takes precedence), docs/extensions.md
// ("MCP servers") and test/suite/agent-session-mcp.test.ts ("connects servers registered while extensions load", "connects and
// disconnects servers registered during the session", "rejects names another extension registered").
internal static partial class Program
{
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // pi.registerMcpServer / unregisterMcpServer / getMcpServers on a native extension's registry, and mcp_servers_change once bound.
    private static async Task NativeRegistrationApiAndEvent()
    {
        var servers = new McpRegisteredServers { OwnerPath = owner => "/ext/" + owner };
        await using var registry = new ExtensionRegistry { McpServerHost = servers };
        var observed = new ConcurrentQueue<string>();
        IExtensionMcpServerRegistry? jira = null, other = null;
        await registry.ActivateAsync("jira-ext", new Plugin(api =>
        {
            jira = (IExtensionMcpServerRegistry)api;
            // Registered while the extension loads: read at session start, no event.
            jira.RegisterMcpServer("jira", JsonData.Parse("""{"url":"https://mcp.example.test/jira","exposure":"codemode-deferred"}"""));
            api.Observe(new("watch", "mcp_servers_change", (value, _, _) => { observed.Enqueue(value.ToString()); return ValueTask.CompletedTask; }));
        }));
        await registry.ActivateAsync("other-ext", new Plugin(api => other = (IExtensionMcpServerRegistry)api));
        Names(["jira"], jira!.GetMcpServers().Select(server => server.Name), "registered while loading");
        Equal("/ext/jira-ext", jira.GetMcpServers().Single().ExtensionPath, "owner path");
        Equal("codemode", jira.GetMcpServers().Single().Config.Raw.Value.GetProperty("exposure").GetString(), "aliases resolved");
        // Errors: invalid name, invalid config, a name another extension registered, a namespace clash.
        Equal("Invalid MCP server registered by extension \"/ext/other-ext\": invalid server name \"bad name\" (use letters, digits, \"_\" and \"-\")",
            Throws<InvalidOperationException>(() => other!.RegisterMcpServer("bad name", JsonData.Parse("""{"command":"x"}"""))).Message, "invalid name");
        Equal("Invalid MCP server registered by extension \"/ext/other-ext\": server \"x\" needs either \"command\" (stdio) or \"url\" (streamable HTTP)",
            Throws<InvalidOperationException>(() => other!.RegisterMcpServer("x", JsonData.Parse("{}"))).Message, "invalid config");
        Equal("MCP server \"jira\" is already registered by extension \"/ext/jira-ext\"",
            Throws<InvalidOperationException>(() => other!.RegisterMcpServer("jira", JsonData.Parse("""{"command":"x"}"""))).Message, "owned name");
        jira.RegisterMcpServer("dev-tools", JsonData.Parse("""{"command":"dev"}"""));
        Equal("MCP server \"dev_tools\" conflicts with registered server \"dev-tools\"",
            Throws<InvalidOperationException>(() => other!.RegisterMcpServer("dev_tools", JsonData.Parse("""{"command":"x"}"""))).Message, "namespace clash");
        other!.UnregisterMcpServer("jira");
        Names(["jira", "dev-tools"], servers.List().Select(server => server.Name), "another extension's server is left alone");
        Check(observed.IsEmpty, "no event before the session binds");
        // Bound: every change reaches the observers with every registered server.
        var snapshot = registry.CaptureSnapshot();
        servers.BindDispatch(value => registry.DispatchObservationsReportingAsync(snapshot, "mcp_servers_change", value, null).AsTask());
        jira.UnregisterMcpServer("dev-tools");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (observed.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
        Equal("""{"type":"mcp_servers_change","servers":[{"name":"jira","config":{"url":"https://mcp.example.test/jira","exposure":"codemode"},"extensionPath":"/ext/jira-ext"}]}""",
            observed.Single(), "mcp_servers_change payload");
        // A host that connects no MCP servers.
        await using var plain = new ExtensionRegistry();
        await plain.ActivateAsync("plain", new Plugin(api =>
            Check(Throws<NotSupportedException>(() => ((IExtensionMcpServerRegistry)api).GetMcpServers()).Message.Contains("unavailable", StringComparison.Ordinal), "unavailable")));
    }

    // Registered servers connect with the configured ones (mcp.json wins a shared name, and /mcp shows the override); servers
    // registered during the session connect right away, unregistered ones close and their tools become unreachable.
    private static Task RegisteredServersConnect() => WithRoot("registered", DirectDocs, async fixture =>
    {
        var provider = new Endpoint();
        var host = fixture.Host();
        host.Registrations.OwnerPath = owner => "/ext/" + owner;
        // While the extensions load (before the session starts).
        host.Registrations.Register("ext", "web", JsonData.Parse("""{"command":"web-server","exposure":"direct"}"""));
        host.Registrations.Register("ext", "docs", JsonData.Parse("""{"command":"other-docs","exposure":"direct"}"""));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, host))
        {
            await rpc.Prompt("p1", "hello");
            var manager = await fixture.Manager.Task;
            Check(manager.FormatStatus().EndsWith($"\noverridden: \"docs\" registered by /ext/ext is overridden by \"docs\" in {Path.Combine(fixture.Agent, "mcp.json")}", StringComparison.Ordinal),
                "override shown: " + manager.FormatStatus());
            var web = manager.Servers.Single(server => server.Name == "web");
            Equal("/ext/ext", web.Entry.Source, "registered server source"); Equal("connected", web.State, "registered server connected");
            Equal("web: connected · 2 tools · direct · extension", manager.ServersMenu().Items.Single(item => item.Value == "web").Label + ": " + manager.ServersMenu().Items.Single(item => item.Value == "web").Description, "manager line");
            // During the session.
            host.Registrations.Register("ext", "late", JsonData.Parse("""{"command":"late-server","exposure":"direct"}"""));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (manager.Servers.All(server => server.Name != "late" || server.State != "connected") && DateTime.UtcNow < deadline) await Task.Delay(20);
            await rpc.Prompt("p2", "late server");
            host.Registrations.Unregister("ext", "web");
            while (manager.Servers.Any(server => server.Name == "web") && DateTime.UtcNow < deadline) await Task.Delay(20);
            await Task.Delay(300);
            await rpc.Prompt("p3", "web unregistered");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(ToolNames(requests[0]).Contains("mcp__web__search") && ToolNames(requests[0]).Contains("mcp__docs__search") && !ToolNames(requests[0]).Contains("mcp__late__search"), "first: " + string.Join(",", ToolNames(requests[0])));
        Check(ToolNames(requests[1]).Contains("mcp__late__search"), "registered during the session: " + string.Join(",", ToolNames(requests[1])));
        Check(!ToolNames(requests[2]).Contains("mcp__web__search") && ToolNames(requests[2]).Contains("mcp__late__search"), "unregistered: " + string.Join(",", ToolNames(requests[2])));
        Check(fixture.Servers.Count(server => server.Name == "docs") == 1, "the configured docs server wins");
        Equal(1, fixture.Servers.Single(server => server.Name == "web").Closes, "unregistered server closed");
    });

    private static T Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
}
