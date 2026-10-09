using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.Cli.Mcp;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// Upstream: extensions/mcp/index.ts (formatStatus, describeState, attentionRank, serversMenu, serverMenu, showTools, chooseExposure,
// setEnabled, setExposure, saveConfig, reconnect, signIn, signOut, pickServer, loginCommand, the `/mcp` command and its completions),
// extensions/mcp/config.ts (updateMcpServerConfig) and docs/mcp.md ("Inspect or change a server"). The manager is reached through the
// host's ObserveManager seam; its terminal view is IMPL-I's.
internal static partial class Program
{
    /// <summary>A command context outside the terminal (RPC/print): notifications are recorded, selections answer the first option.</summary>
    private sealed class CommandUi(bool hasUi = true) : IMcpCommandUi
    {
        public readonly ConcurrentQueue<(string Message, string Level)> Notices = new();
        public bool HasUi => hasUi;
        public bool IsTui => false;
        public void Notify(string message, string level) => Notices.Enqueue((message, level));
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken token) => Task.FromResult<string?>(options[0]);
        public Task<string?> InputAsync(string title, string placeholder, CancellationToken token) => WaitForCancellation(token);
        public Task ShowManagerAsync(Func<IMcpManagerUi, Task> manage) => throw new InvalidOperationException("No terminal view.");
        public (string Message, string Level)[] Take() { var all = Notices.ToArray(); Notices.Clear(); return all; }
    }

    private const string ManagedServers = """
        {"mcpServers":{
          "docs":{"command":"docs-server","exposure":"direct"},
          "broken":{"command":"broken-server"},
          "off":{"command":"off-server","enabled":false}}}
        """;

    // formatStatus, the `/mcp` command outside the terminal, pickServer, completions and the manager menus.
    private static Task ManagerStatusAndMenus() => WithRoot("manager-status", ManagedServers, async fixture =>
    {
        fixture.Behaviors["broken"] = (["x"], null, false, new IOException("initialize refused\nsecond line"));
        await using var rpc = new Rpc(Args(fixture.Root, "new-memory", "--tools", "read"), new Endpoint(), fixture.Host());
        await rpc.Prompt("p1", "hello");
        await Settled(fixture, rpc, 2);
        var manager = await fixture.Manager.Task;
        var status = "docs: connected, 2 tools (direct)\nbroken: failed (codemode)\n    initialize refused\n    second line\noff: disabled (codemode)";
        Equal(status, manager.FormatStatus(), "status");
        var ui = new CommandUi();
        await manager.ExecuteCommandAsync("", ui);
        Equal((status, "info"), ui.Take().Single(), "/mcp outside the terminal prints the status");
        await manager.ExecuteCommandAsync("bogus", ui);
        Equal((McpServerManager.Usage, "warning"), ui.Take().Single(), "unknown subcommand");
        await manager.ExecuteCommandAsync("login docs extra", ui);
        Equal((McpServerManager.Usage, "warning"), ui.Take().Single(), "extra arguments");
        await manager.ExecuteCommandAsync("login", ui);
        Equal(("No enabled MCP server uses OAuth. Only HTTP servers without an Authorization header do.", "info"), ui.Take().Single(), "no OAuth server");
        await manager.ExecuteCommandAsync("login docs", ui);
        Equal(("No enabled MCP server uses OAuth. Only HTTP servers without an Authorization header do.", "error"), ui.Take().Single(), "named server without OAuth");
        await manager.ExecuteCommandAsync("reconnect nope", ui);
        Equal(("No MCP server named \"nope\".", "error"), ui.Take().Single(), "unknown server");
        // Without a name, the only failed server is preferred; it fails again.
        await manager.ExecuteCommandAsync("reconnect", ui);
        Equal(("MCP server \"broken\" failed to connect: initialize refused\nsecond line", "error"), ui.Take().Single(), "reconnect reports the failure");
        await manager.ExecuteCommandAsync("reconnect docs", ui);
        Equal(("Reconnected to MCP server \"docs\" (connected · 2 tools).", "info"), ui.Take().Single(), "reconnect");
        // Completions.
        Names(["login ", "logout ", "reconnect "], manager.GetArgumentCompletions("")!.Select(item => item.Value), "subcommands");
        Names(["reconnect docs"], manager.GetArgumentCompletions("reconnect d")!.Select(item => item.Value), "servers to reconnect");
        Equal(null, manager.GetArgumentCompletions("login d"), "no OAuth servers to sign in to");
        // Menus: servers needing attention first, then connected, then disabled.
        var servers = manager.ServersMenu();
        Names(["broken", "docs", "off"], servers.Items.Select(item => item.Value), "attention order");
        Equal("connected · 2 tools · direct · global", servers.Items[1].Description, "server line");
        Equal("failed: initialize refused · codemode · global", servers.Items[0].Description, "failed line");
        Equal(("manage", "close"), (servers.ConfirmLabel, servers.CancelLabel), "servers menu keys");
        Names(["tools", "reconnect", "exposure", "disable"], manager.ServerMenu("docs").Items.Select(item => item.Value), "connected server actions");
        Names(["reconnect", "exposure", "disable"], manager.ServerMenu("broken").Items.Select(item => item.Value), "failed server actions");
        Names(["enable"], manager.ServerMenu("off").Items.Select(item => item.Value), "disabled server actions");
        Equal("docs-server\nglobal: " + Path.Combine(fixture.Agent, "mcp.json") + "\nState: connected · 2 tools", manager.ServerMenu("docs").Details, "server details");
        Equal("initialize refused\nsecond line", manager.ServerMenu("broken").Error, "failure shown");
        var tools = manager.ToolsMenu("docs");
        Names(["search", "fetch"], tools.Items.Select(item => item.Value), "tools");
        Equal("The search tool of docs.", tools.Items[0].Description, "tool line");
        Equal("Exposure direct: declared to the model like built-in tools", tools.Details, "tools details");
        var exposure = manager.ExposureMenu("docs");
        Names(["  codemode", "  deferred", "✓ direct"], exposure.Items.Select(item => item.Label), "exposure choices");
        Equal("direct", exposure.Selected, "current exposure selected");
        Equal("This server is no longer configured.", manager.ServerMenu("gone").Empty, "unknown server menu");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
    });

    // setEnabled and setExposure save to the mcp.json that defines the server and apply at once; reconnect connects again.
    private static Task ManagerActions() => WithRoot("manager-actions", """{"other":1,"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""", async fixture =>
    {
        var provider = new Endpoint();
        var global = Path.Combine(fixture.Agent, "mcp.json");
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "with docs");
            var manager = await fixture.Manager.Task;
            Equal(null, await manager.SetEnabledAsync("docs", false), "disable");
            Equal(false, JsonNode.Parse(File.ReadAllText(global))!["mcpServers"]!["docs"]!["enabled"]!.GetValue<bool>(), "saved disabled");
            Equal(1, JsonNode.Parse(File.ReadAllText(global))!["other"]!.GetValue<int>(), "other content kept");
            Equal("docs: disabled (direct)", manager.FormatStatus(), "disabled status");
            await rpc.Prompt("p2", "without docs");
            Equal(null, await manager.SetEnabledAsync("docs", true), "enable");
            Check(JsonNode.Parse(File.ReadAllText(global))!["mcpServers"]!["docs"]!["enabled"] is null, "enabled: true removes the key");
            await rpc.Prompt("p3", "with docs again");
            Equal(null, await manager.ReconnectAsync("docs"), "reconnect");
            await rpc.Prompt("p4", "after reconnect");
            Equal(null, await manager.SetExposureAsync("docs", McpExposure.Deferred), "exposure");
            Equal("deferred", JsonNode.Parse(File.ReadAllText(global))!["mcpServers"]!["docs"]!["exposure"]!.GetValue<string>(), "saved exposure");
            await rpc.Prompt("p5", "deferred now");
            Equal(null, await manager.SetExposureAsync("docs", McpExposure.Codemode), "back to codemode");
            Check(JsonNode.Parse(File.ReadAllText(global))!["mcpServers"]!["docs"]!["exposure"] is null, "codemode removes the key");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        bool Declares(int index) => ToolNames(requests[index]).Contains("mcp__docs__search");
        Check(Declares(0) && !Declares(1) && Declares(2) && Declares(3), "disable withdraws, enable and reconnect declare: " + string.Join(" | ", requests.Select(request => string.Join(",", ToolNames(request)))));
        Check(!Declares(4) && ToolNames(requests[4]).Contains("tool_search"), "deferred: not declared, tool_search activated: " + string.Join(",", ToolNames(requests[4])));
        // setExposure registers the tools again without a new connection; enable and reconnect connect.
        Equal(3, fixture.Servers.Count(server => server.Name == "docs"), "connected again after enable and reconnect, not after exposure changes");
    });

    // In a trusted project, "Disable in this project" adds a project override for a global server; later changes go to the override.
    private static Task ManagerProjectOverride() => WithRoot("manager-project", """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""", async fixture =>
    {
        var global = Path.Combine(fixture.Agent, "mcp.json"); var project = Path.Combine(fixture.Project, ".pi", "mcp.json");
        var globalText = File.ReadAllText(global);
        await using var rpc = new Rpc(Args(fixture.Root, "new-memory"), new Endpoint(), fixture.Host(trusted: _ => true));
        await rpc.Prompt("p1", "hello");
        var manager = await fixture.Manager.Task;
        Names(["tools", "reconnect", "exposure", "disable", "disable-project"], manager.ServerMenu("docs").Items.Select(item => item.Value), "project actions offered");
        Equal(null, await manager.SetEnabledAsync("docs", false, inProject: true), "disable in this project");
        Equal("{\n  \"mcpServers\": {\n    \"docs\": {\n      \"enabled\": false\n    }\n  }\n}\n", File.ReadAllText(project).ReplaceLineEndings("\n"), "project override written");
        Equal(globalText, File.ReadAllText(global), "global file unchanged");
        Equal("global, project override", manager.ServersMenu().Items.Single().Description!.Split(" · ")[^1], "override shown");
        Names(["enable"], manager.ServerMenu("docs").Items.Select(item => item.Value), "the override owns later changes");
        Equal(null, await manager.SetEnabledAsync("docs", true), "enable");
        Equal("{\n  \"mcpServers\": {\n    \"docs\": {\n      \"enabled\": true\n    }\n  }\n}\n", File.ReadAllText(project).ReplaceLineEndings("\n"), "overrides keep default values");
        Equal(globalText, File.ReadAllText(global), "global file still unchanged");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
    }, projectJson: "{}");

    // loginCommand outside the terminal: the authorization URL is shown, the browser approves, the server reconnects with the new tokens;
    // logout deletes them and the server needs a sign-in again. Without a UI, sign-in is refused.
    private static Task ManagerLoginLogout() => WithRoot("manager-login", """{"mcpServers":{"remote":{"url":"https://mcp.example.test/mcp","exposure":"direct"}}}""", async fixture =>
    {
        var server = new FakeOAuthServer(); var pages = new ConcurrentBag<Task>();
        var provider = new Endpoint();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host() with { CreateHttpHandler = () => server, OpenUrl = Browser(pages) }))
        {
            await rpc.Prompt("p1", "hello");
            await Settled(fixture, rpc, 1);
            var manager = await fixture.Manager.Task;
            Equal("remote: needs sign-in, run /mcp login remote (direct)", manager.FormatStatus(), "needs sign-in");
            Names(["signin", "reconnect", "exposure", "disable"], manager.ServerMenu("remote").Items.Select(item => item.Value), "sign-in offered");
            var headless = new CommandUi(hasUi: false);
            await manager.ExecuteCommandAsync("login remote", headless);
            Equal(("Signing in to MCP server \"remote\" requires interactive mode.", "error"), headless.Take().Single(), "no UI");
            var ui = new CommandUi();
            Names(["login remote"], manager.GetArgumentCompletions("login r")!.Select(item => item.Value), "OAuth server completion");
            await manager.ExecuteCommandAsync("login", ui);
            await Task.WhenAll(pages);
            var notices = ui.Take();
            Equal(2, notices.Length, "notices: " + string.Join(" | ", notices.Select(row => row.Message)));
            Check(notices[0].Message.StartsWith("Sign in to MCP server \"remote\" in your browser:\nhttps://issuer.example.test/authorize?", StringComparison.Ordinal), notices[0].Message);
            Equal(("Signed in to MCP server \"remote\" (1 tools).", "info"), notices[1], "signed in");
            Equal("remote: connected, 1 tools (direct)", manager.FormatStatus(), "connected");
            await rpc.Prompt("p2", "signed in");
            await manager.ExecuteCommandAsync("logout remote", ui);
            Equal(("Signed out of MCP server \"remote\".", "info"), ui.Take().Single(), "signed out");
            Equal(null, new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(fixture.Agent)).Tokens("remote", McpServerUrl), "credentials deleted");
            Equal("remote: needs sign-in, run /mcp login remote (direct)", manager.FormatStatus(), "needs sign-in again");
            await manager.ExecuteCommandAsync("logout remote", ui);
            Equal(("No stored credentials for MCP server \"remote\".", "info"), ui.Take().Single(), "nothing to sign out of");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(!ToolNames(requests[0]).Contains("mcp__remote__lookup") && ToolNames(requests[1]).Contains("mcp__remote__lookup"), "declared after the sign-in");
    });
}
