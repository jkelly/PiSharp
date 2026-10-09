using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// IMPL-H follow-up. Upstream: core/agent-session.ts (_refreshToolRegistry: tools registered during a run are active at once and
// declared from the run's next request), extensions/mcp/index.ts (tool_call waits inside the call: tool_search and the resource tools
// for every server, codemode scripts for the servers they need, scriptNeedsServer; setExposure registers the tools again without a
// new connection; setEnabled; signIn passes connection.challenge), extensions/mcp/runtime.ts (onChallenge, withClient markNeedsAuth,
// refreshResources on notifications/resources/list_changed), extensions/mcp/oauth.ts (signInMcpServer: challenge resource metadata
// URL, step-up scope, skipRefresh), extensions/mcp/cli.ts (login connects first and records the challenge), core/extensions/runner.ts
// (reportUnhandledMcpServers) and extensions/mcp/tools.ts (toToolAnnotations).
internal static partial class Program
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    /// <summary><see cref="RecordedLoadouts"/> of a session file the session still holds open.</summary>
    private static string[][] OpenLoadouts(string root)
    {
        using var file = new FileStream(Path.Combine(root, "session.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)
            .Where(entry => entry.TryGetProperty("message", out var message) && message.TryGetProperty("toolsAdded", out _))
            .Select(entry => entry.GetProperty("message").GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray())];
    }

    // _refreshToolRegistry: a direct server that connects while the model answers the run's first request has its tools published
    // during the run; the run's next request declares them and the model calls one in the same run.
    private static Task LateServerReachesTheRunningTurn() => WithRoot("mid-run", DirectDocs, async fixture =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Behaviors["docs"] = (["search"], release.Task, false, null);
        var provider = new Endpoint(() =>
        {
            release.TrySetResult();
            Check(SpinWait.SpinUntil(() => !fixture.Reports.IsEmpty, Patience), "the server connected during the request");
            return Call("toolu_read", "read", new { path = "missing.txt" });
        }, () => Call("toolu_docs", "mcp__docs__search", new { query = "install" }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-lazy"), provider, fixture.Host(startupWait: TimeSpan.FromMilliseconds(200))))
        {
            await rpc.Prompt("p1", "search the docs");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Equal(3, requests.Length, "requests of the run");
        Check(!ToolNames(requests[0]).Contains("mcp__docs__search"), "the first request goes out without the late server");
        Check(ToolNames(requests[1]).Contains("mcp__docs__search"), "the run's next request declares it: " + string.Join(",", ToolNames(requests[1])));
        Names(["docs answered."], ToolResults(requests[2]), "called in the same run");
        Check(RecordedLoadouts(fixture.Root).Any(names => names.Contains("mcp__docs__search")), "the loadout is recorded");
    });

    // A catalog the run took after its last request is recorded when the run settles, so the idle session's transcript and registry
    // agree and the next prompt declares the tools from its first request.
    private static Task LateServerAfterTheLastRequestIsRecordedAtSettlement() => WithRoot("mid-run-settled", DirectDocs, async fixture =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Behaviors["docs"] = (["search"], release.Task, false, null);
        var provider = new Endpoint(() =>
        {
            release.TrySetResult();
            Check(SpinWait.SpinUntil(() => !fixture.Reports.IsEmpty, Patience), "the server connected during the request");
            return Text("first");
        }, () => Text("second"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-lazy"), provider, fixture.Host(startupWait: TimeSpan.FromMilliseconds(200))))
        {
            await rpc.Prompt("p1", "hello");
            Check(OpenLoadouts(fixture.Root).Last().Contains("mcp__docs__search"), "recorded when the run settled");
            await rpc.Prompt("p2", "again");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(!ToolNames(requests[0]).Contains("mcp__docs__search") && ToolNames(requests[1]).Contains("mcp__docs__search"), "declared from the next prompt");
    });

    // An idle catalog publication (a tool list change) while an activation is pending (setExposure dropped the server's tools and
    // ensureDiscoveryActive activated tool_search) is published with the pending selection instead of being refused.
    private static Task IdlePublicationWithAPendingActivation() => WithRoot("pending-idle", DirectDocs, async fixture =>
    {
        var servers = new ConcurrentBag<ChangingServer>();
        var provider = new Endpoint(() => Text("first"), () => Call("toolu_search", "tool_search", new { query = "extra tool", limit = 1 }), () => Text("done"));
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
            var manager = await fixture.Manager.Task;
            Equal(null, await manager.SetExposureAsync("docs", McpExposure.Deferred), "exposure");
            var server = servers.Single();
            server.Tools = ["search", "extra"];
            await server.Notify!("notifications/tools/list_changed", null, CancellationToken.None);
            Equal(null, manager.Servers.Single().Error, "the refresh was published");
            await rpc.Prompt("p2", "find the extra tool");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(ToolNames(requests[1]).Contains("tool_search") && !ToolNames(requests[1]).Any(name => name.StartsWith("mcp__docs", StringComparison.Ordinal)),
            "the pending selection: " + string.Join(",", ToolNames(requests[1])));
        Check(ToolResults(requests[2]).Single().Contains("mcp__docs__extra", StringComparison.Ordinal), "the refreshed tool is searchable: " + ToolResults(requests[2]).Single());
        Equal(1, servers.Count, "no new connection");
    });

    // index.ts tool_call and scriptNeedsServer: a codemode script that does not name a connecting server runs at once; one that names
    // it waits inside its call (the prompt is not held), and once the server connected the script calls its tool in the same call.
    private static Task CodemodeWaitsInsideTheCall() => WithRoot("codemode-wait", """{"mcpServers":{"docs":{"command":"docs-server"}}}""", async fixture =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Behaviors["docs"] = (["search"], release.Task, false, null);
        Check(McpCodemode.ScriptNeedsServer("await tools.mcp__dev_tools__search({})", "dev-tools") && McpCodemode.ScriptNeedsServer("searchTools(\"x\")", "docs") &&
            !McpCodemode.ScriptNeedsServer("return 1", "docs"), "scriptNeedsServer");
        const string Script = "const r = await tools.mcp__docs__search({ query: \"install\" });\ntext(r.content[0].text);\nreturn 1;";
        var provider = new Endpoint(() => Call("toolu_plain", "codemode", new { code = "return 1 + 1" }), () => Text("plain"),
            () => Call("toolu_docs", "codemode", new { code = Script }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "compute");
            Equal(0, fixture.Reports.Count, "the plain script did not wait for the connecting server");
            var prompt = rpc.Prompt("p2", "search the docs");
            await Until(() => provider.Snapshot().Length == 3, "the prompt is not held");
            await Task.Delay(300);
            Equal(3, provider.Snapshot().Length, "the script waits inside its call");
            release.TrySetResult();
            await prompt;
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Check(ToolResults(requests[3]).Single().Contains("docs answered.", StringComparison.Ordinal), "the script called the late server: " + ToolResults(requests[3]).Single());
        Names(["search:{\"query\":\"install\"}"], fixture.Server("docs").Calls, "the call reached the server");
    });

    // setExposure registers the tools again without a new connection (tools that became direct are activated, indirect ones leave the
    // declared set); setEnabled disconnects and connects the same registration again, so neither grows the session's resources.
    private static Task ExposureAndEnableKeepTheRegistration() => WithRoot("exposure-enable", DirectDocs, async fixture =>
    {
        var provider = new Endpoint();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host()))
        {
            await rpc.Prompt("p1", "direct");
            var manager = await fixture.Manager.Task;
            Equal(null, await manager.SetExposureAsync("docs", McpExposure.Deferred), "deferred");
            await rpc.Prompt("p2", "deferred");
            Equal(null, await manager.SetExposureAsync("docs", McpExposure.Direct), "direct");
            await rpc.Prompt("p3", "direct again");
            Equal(1, fixture.Servers.Count, "exposure changes keep the connection");
            Equal(null, await manager.SetEnabledAsync("docs", false), "disable");
            await rpc.Prompt("p4", "disabled");
            Equal(null, await manager.SetEnabledAsync("docs", true), "enable");
            await rpc.Prompt("p5", "enabled");
            // More disable/enable cycles than the session's owned-resource bound (128): the registration is reused.
            for (var cycle = 0; cycle < ReplaceableAgentSessionBound + 2; cycle++)
            {
                Equal(null, await manager.SetEnabledAsync("docs", false), "disable " + cycle);
                Equal(null, await manager.SetEnabledAsync("docs", true), "enable " + cycle);
            }
            Equal("docs: connected, 2 tools (direct)", manager.FormatStatus(), "connected after the cycles");
            // No prompt follows: the cycles recorded 260 loadout messages, past the Anthropic request projector's 256-message bound.
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        bool Declares(int index) => ToolNames(requests[index]).Contains("mcp__docs__search");
        Equal(5, requests.Length, "requests: " + string.Join(" | ", requests.Select(request => string.Join(",", ToolNames(request)))));
        Check(Declares(0) && !Declares(1) && Declares(2) && !Declares(3) && Declares(4),
            "declared sets: " + string.Join(" | ", requests.Select(request => string.Join(",", ToolNames(request)))));
        Check(ToolNames(requests[1]).Contains("tool_search"), "deferred activates tool_search");
        Equal(ReplaceableAgentSessionBound + 4, fixture.Servers.Count, "each enable connects again; the exposure changes did not");
    });

    private const int ReplaceableAgentSessionBound = PiSharp.CodingAgent.ReplaceableAgentSession.MaximumOwnedResources;

    // mcp_servers_change: a server registered again with another config is closed and connected anew. A closed server's resources
    // leave the session, so more re-registrations than the owned-resource bound still connect.
    private static Task ReRegistrationsReleaseTheirResources() => WithRoot("re-register", "{}", async fixture =>
    {
        var host = fixture.Host();
        host.Registrations.OwnerPath = owner => "/ext/" + owner;
        host.Registrations.Register("ext", "web", JsonData.Parse("""{"command":"web-server","exposure":"direct","description":"v0"}"""));
        await using var rpc = new Rpc(Args(fixture.Root, "new-memory"), new Endpoint(), host);
        await rpc.Prompt("p1", "hello");
        var manager = await fixture.Manager.Task;
        for (var version = 1; version <= ReplaceableAgentSessionBound + 2; version++)
        {
            var description = "v" + version;
            host.Registrations.Register("ext", "web", JsonData.Parse("{\"command\":\"web-server\",\"exposure\":\"direct\",\"description\":\"" + description + "\"}"));
            await Until(() => manager.Servers.SingleOrDefault() is { State: "connected" } web && web.Entry.Config.Description == description,
                "re-registration " + version + " connected: " + manager.FormatStatus());
        }
        Equal(ReplaceableAgentSessionBound + 3, fixture.Servers.Count, "each re-registration connected");
        await Until(() => fixture.Servers.Count(server => server.Closes == 1) == ReplaceableAgentSessionBound + 2, "the replaced connections closed");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
    });

    // A call the server refuses for more scope (403 insufficient_scope) marks the server as needing a sign-in (markNeedsAuth) and
    // tells the model so; the sign-in from `/mcp` answers the server's challenge: discovery starts at its resource metadata URL, the
    // granted scope and the challenged one are requested, and no refresh is tried (skipRefresh). Then the call succeeds.
    private static Task StepUpChallengeInTheSession() => WithRoot("step-up", """{"mcpServers":{"remote":{"url":"https://mcp.example.test/mcp","exposure":"direct"}}}""", async fixture =>
    {
        var server = new FakeOAuthServer(); var pages = new ConcurrentBag<Task>();
        server.Accepted["limited"] = true; server.Limited["limited"] = true;
        await new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(fixture.Agent)).ForServer("remote", McpServerUrl)
            .SaveAsync(new(McpServerUrl.AbsoluteUri, Tokens: new("limited", "Bearer", Scope: "docs.read", RefreshToken: "refresh-0")));
        var provider = new Endpoint(() => Call("toolu_1", "mcp__remote__lookup", new { }), () => Text("needs sign-in"),
            () => Call("toolu_2", "mcp__remote__lookup", new { }), () => Text("done"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host() with { CreateHttpHandler = () => server, OpenUrl = Browser(pages) }))
        {
            await rpc.Prompt("p1", "look up");
            var manager = await fixture.Manager.Task;
            Equal("needs-auth", manager.Servers.Single().State, "a refused call marks the server");
            Equal("remote: needs sign-in, run /mcp login remote (direct)", manager.FormatStatus(), "status");
            var ui = new CommandUi();
            await manager.ExecuteCommandAsync("login remote", ui);
            await Task.WhenAll(pages);
            var notices = ui.Take();
            Equal(("Signed in to MCP server \"remote\" (1 tools).", "info"), notices[^1], "signed in: " + string.Join(" | ", notices.Select(row => row.Message)));
            var authorize = new Uri(notices[0].Message.Split('\n')[1]);
            Equal("docs.read docs.write", System.Web.HttpUtility.ParseQueryString(authorize.Query)["scope"], "the granted and the challenged scope");
            Equal("connected", manager.Servers.Single().State, "connected");
            await rpc.Prompt("p2", "look up again");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        Check(server.Log.Any(row => row.Uri.AbsolutePath == "/.well-known/oauth-protected-resource-alt/mcp"), "discovery from the challenge's resource metadata URL");
        Check(!server.Log.Any(row => row.Uri.AbsolutePath == "/token" && row.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal)), "a step-up does not refresh");
        var requests = provider.Snapshot();
        Check(ToolResults(requests[1]).Single().Contains("MCP server \"remote\" requires sign-in. Run /mcp to sign in.", StringComparison.Ordinal),
            "the model learns why: " + ToolResults(requests[1]).Single());
        Names(["remote answered."], ToolResults(requests[3]), "the call succeeds after the sign-in");
    });

    // cli.ts login: connecting first records the server's challenge, and the sign-in uses its resource metadata URL and scope.
    private static Task McpLoginUsesTheChallenge() => WithRoot("login-challenge", """{"mcpServers":{"remote":{"url":"https://mcp.example.test/mcp"}}}""", async fixture =>
    {
        var server = new FakeOAuthServer
        { Challenge = "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource-alt/mcp\", scope=\"docs.read\"" };
        var pages = new ConcurrentBag<Task>();
        using var output = new StringWriter { NewLine = "\n" }; using var error = new StringWriter { NewLine = "\n" };
        var code = await McpCommand.RunAsync(["login", "remote", "--timeout", "30"], output, error, new McpCommandOptions(fixture.Project, fixture.Agent)
        { HttpHandler = server, OpenUrl = Browser(pages), ReadRedirectUrl = WaitForCancellation });
        await Task.WhenAll(pages);
        Equal((0, ""), (code, error.ToString()), "login");
        var lines = output.ToString().Split('\n');
        var authorize = new Uri(lines[Array.FindIndex(lines, line => line.StartsWith("Sign in to MCP server", StringComparison.Ordinal)) + 1]);
        Equal("docs.read", System.Web.HttpUtility.ParseQueryString(authorize.Query)["scope"], "the challenged scope");
        Check(server.Log.Any(row => row.Uri.AbsolutePath == "/.well-known/oauth-protected-resource-alt/mcp"), "discovery from the challenge's resource metadata URL");
        Check(output.ToString().EndsWith("Signed in to MCP server \"remote\" (1 tools).\n", StringComparison.Ordinal), output.ToString());
        Equal(new McpOAuthChallenge("insufficient_scope", "a b", new Uri("https://x.test/meta")),
            McpOAuthChallenge.Parse("Bearer error=\"insufficient_scope\", scope=\"a b\", resource_metadata=\"https://x.test/meta\""), "parse");
        Equal(new McpOAuthChallenge(), McpOAuthChallenge.Parse("Basic realm=\"x\""), "another scheme");
        Equal(new McpOAuthChallenge(null, null, null), McpOAuthChallenge.Parse("Bearer scope=\"\""), "empty values are absent");
    });

    /// <summary>A server whose resource list can change; it reports changes through the session's notification handler.</summary>
    private sealed class ResourceChangingServer(McpNotificationHandler? notify) : IMcpAdmittedRequestChannel
    {
        public volatile int Resources = 1;
        public McpNotificationHandler? Notify => notify;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) => method switch
        {
            "initialize" => ValueTask.FromResult(JsonData.Parse("""{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{},"resources":{"listChanged":true}}}""")),
            "tools/list" => ValueTask.FromResult(JsonData.Parse("""{"tools":[{"name":"search","inputSchema":{"type":"object"}}]}""")),
            "resources/list" => ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
                { resources = Enumerable.Range(0, Resources).Select(index => new { uri = "docs://" + index, name = "r" + index }) }))),
            "resources/templates/list" => ValueTask.FromResult(JsonData.Parse("""{"resourceTemplates":[]}""")),
            _ => ValueTask.FromException<JsonData>(new IOException("Unexpected MCP method " + method))
        };
        public Task CloseAsync() => Task.CompletedTask;
    }

    // runtime.ts refreshResources: notifications/resources/list_changed refreshes the resources `/mcp` counts.
    private static Task ResourceListChangesRefreshTheCount() => WithRoot("resources-changed", DirectDocs, async fixture =>
    {
        var servers = new ConcurrentBag<ResourceChangingServer>();
        var host = fixture.Host() with
        {
            CreateNotifyingChannel = (entry, notify) => (actual, token) =>
            {
                var server = new ResourceChangingServer(notify); servers.Add(server);
                return ValueTask.FromResult<IMcpAdmittedRequestChannel>(server);
            }
        };
        await using var rpc = new Rpc(Args(fixture.Root, "new-memory"), new Endpoint(), host);
        await rpc.Prompt("p1", "hello");
        await Settled(fixture, rpc, 1);
        var manager = await fixture.Manager.Task;
        Equal("connected · 1 tool · 1 resource · direct · global", manager.ServersMenu().Items.Single().Description, "before");
        var changes = 0;
        using var subscription = manager.Subscribe(() => Interlocked.Increment(ref changes));
        var server = servers.Single();
        server.Resources = 3;
        await server.Notify!("notifications/resources/list_changed", null, CancellationToken.None);
        Equal("connected · 1 tool · 3 resources · direct · global", manager.ServersMenu().Items.Single().Description, "after the change");
        Check(Volatile.Read(ref changes) > 0, "the manager view is told");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
    });

    // runner.ts reportUnhandledMcpServers: when nothing connects registered servers (`--no-mcp` leaves the built-in MCP support out and
    // no extension handles mcp_servers_change), each registered server is reported once as its extension's error, at session start
    // and on later registrations.
    private static Task UnhandledRegisteredServersAreReported() => WithRoot("unhandled", DirectDocs, async fixture =>
    {
        const string Suffix = "\" is registered, but no loaded extension connects MCP servers; another extension may have replaced the built-in MCP support";
        var host = fixture.Host();
        Check(host.CreateAdmission(fixture.Project, TextWriter.Null, noMcp: true) is not null, "admission");
        Check(!host.Registrations.HostConnects, "--no-mcp: nothing connects registered servers");
        host.Registrations.Register("jira-ext", "jira", JsonData.Parse("""{"url":"https://mcp.example.test/jira"}"""));
        var reports = new ConcurrentQueue<string>();
        host.Registrations.BindUnhandledReport(() => false, (owner, name, message) => reports.Enqueue(owner + "|" + name + "|" + message));
        Names(["jira-ext|jira|MCP server \"jira" + Suffix], reports, "reported at session start");
        host.Registrations.Register("web-ext", "web", JsonData.Parse("""{"command":"web-server"}"""));
        host.Registrations.Register("jira-ext", "jira", JsonData.Parse("""{"url":"https://mcp.example.test/jira2"}"""));
        Names(["jira-ext|jira|MCP server \"jira" + Suffix, "web-ext|web|MCP server \"web" + Suffix], reports, "each server once");
        // An extension that handles mcp_servers_change connects them; the built-in support (without --no-mcp) does too.
        var handled = new McpRegisteredServers { HostConnects = false };
        handled.Register("ext", "docs", JsonData.Parse("""{"command":"x"}"""));
        handled.BindUnhandledReport(() => true, (_, name, _) => reports.Enqueue(name));
        var connected = new McpRegisteredServers();
        connected.Register("ext", "docs", JsonData.Parse("""{"command":"x"}"""));
        connected.BindUnhandledReport(() => false, (_, name, _) => reports.Enqueue(name));
        Equal(2, reports.Count, "nothing more reported");
        var withMcp = fixture.Host(); _ = withMcp.CreateAdmission(fixture.Project, TextWriter.Null);
        Check(withMcp.Registrations.HostConnects, "without --no-mcp the host connects registered servers");
    });

    /// <summary>A server offering one annotated tool.</summary>
    private sealed class AnnotatedServer : IMcpAdmittedRequestChannel
    {
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) => ValueTask.FromResult(JsonData.Parse(method switch
        {
            "initialize" => """{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{}}}""",
            "tools/list" => """{"tools":[{"name":"search","inputSchema":{"type":"object"},"annotations":{"title":"Search","readOnlyHint":true,"destructiveHint":"no","openWorldHint":false}},{"name":"plain","inputSchema":{"type":"object"}}]}""",
            _ => throw new IOException("Unexpected MCP method " + method)
        }));
        public Task CloseAsync() => Task.CompletedTask;
    }

    // tools.ts toToolAnnotations: the boolean hints of an MCP tool's annotations reach the planned tool (the extension API's
    // getAllTools carries them as `annotations`; IMPL-E's tool info).
    private static async Task ToolAnnotationsReachThePlan()
    {
        var entry = new McpServerEntry("docs", McpConfigurationReader.Validate("docs", JsonData.Parse("""{"command":"x"}""").Value).Config!, "mcp.json", McpConfigurationScope.Global);
        McpCatalogPublication? published = null;
        await using var runtime = new McpServerRuntime(entry, new(1, "1.1.0"), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new AnnotatedServer()),
            (publication, _) => { published = publication; return ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)); });
        var snapshot = await runtime.ConnectAsync();
        var hints = snapshot.Catalog.Tools[0].Annotations!;
        Names(["openWorldHint=False", "readOnlyHint=True"], hints.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value), "boolean hints only");
        Equal(null, snapshot.Catalog.Tools[1].Annotations, "a tool without annotations");
        Check(ReferenceEquals(published!.Tools.Single(tool => tool.OriginalName == "search").Annotations, hints), "the planned tool carries them");
        Equal(null, McpOfferedTool.ToolAnnotations(JsonData.Parse("""{"name":"t","annotations":{"title":"T"}}""").Value), "no boolean hints");
    }
}
