using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// Authored offline expectations for the Pi v1.1.0 MCP sync (names, exposure/prompt, OAuth). They are derived from the
// pinned upstream source and its tests by reading, never captured from an upstream run. Fake authorization and token
// servers answer in process; credential stores are in memory; no network or live credentials are used.
internal static partial class Program
{
    private static int requests;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new List<(string Id, Func<Task> Run)>
        {
            ("names.dash-and-underscore-tools-dispatch-distinct", DistinctDispatch),
            ("names.collisions-get-stable-hash-suffixes", StableSuffixes),
            ("names.colliding-server-names-fail-predictably", CollidingServers),
            ("exposure.codemode-deferred-alias-resolves-to-codemode", Alias),
            ("exposure.server-description-validated-and-namespaced", Description),
            ("exposure.project-overrides-and-authority-limits", ProjectOverrides),
            ("exposure.provider-auth-global-only-https-or-loopback", ProviderAuth),
            ("prompt.mcp-servers-section-lines-limits-and-append-diff", ServersSection),
            ("oauth.config-client-name-registration-and-metadata-url", OAuthConfig),
            ("oauth.mismatched-issuer-rejected-before-exchange", IssuerValidation),
            ("oauth.step-up-sign-in-unions-granted-scopes", StepUpScopes),
            ("oauth.same-url-servers-keep-separate-credentials", SeparateCredentials),
            ("oauth.legacy-url-credentials-migrate-once-deterministically", LegacyMigration),
            ("oauth.cancelled-sign-in-cleans-up", CancelledSignIn),
            ("oauth.request-timeout-is-a-failure-not-cancellation", RequestTimeout),
            ("oauth.auth-server-metadata-url-replaces-discovery", MetadataUrl),
            ("oauth.client-id-metadata-document-and-application-type", ClientMetadataDocument)
        };
        cases.AddRange(CompletionCases());
        // Linked existing contract cases (tests/PiSharp.Extensions.ContractTests/McpConfigurationTests.cs).
        cases.AddRange(McpConfigurationTests.Cases().Select(item => ("linked." + item.Name, item.Run)));
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(60)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = "abe508e1b89912adde45528136c3221eb69acdd7", status = "AUTHORED NATIVE; SOURCE QUALIFICATION OPEN",
            cases = cases.Count, failures, fakeHttpRequests = requests, genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = args.Length == 2 });
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    // ---------- names ----------

    private static McpServerEntry Entry(string json, string name = "srv", McpConfigurationScope scope = McpConfigurationScope.Global)
    {
        using var document = JsonDocument.Parse(json);
        var result = McpConfigurationReader.Validate(name, document.RootElement);
        return new(name, result.Config ?? throw new InvalidOperationException(result.Error), "synthetic.json", scope);
    }
    private static McpOfferedTool Tool(string name, string? description = null) => new(name, JsonData.Parse("{\"type\":\"object\"}"), description);
    private static string Hash(string server, string tool) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\0" + tool))).ToLowerInvariant()[..8];

    private static Task DistinctDispatch()
    {
        var entry = Entry("{\"command\":\"x\"}");
        var forward = McpCatalogPlanner.Plan([new(entry, [Tool("read-file", "dashed"), Tool("read_file", "underscored"), Tool("other")])]);
        var reverse = McpCatalogPlanner.Plan([new(entry, [Tool("read_file", "underscored"), Tool("read-file", "dashed"), Tool("other")])]);
        foreach (var plan in new[] { forward, reverse })
        {
            var dashed = plan.Tools.Single(tool => tool.OriginalName == "read-file");
            var underscored = plan.Tools.Single(tool => tool.OriginalName == "read_file");
            Equal("mcp__srv__read_file_" + Hash("srv", "read-file"), dashed.Name);
            Equal("mcp__srv__read_file_" + Hash("srv", "read_file"), underscored.Name);
            // Dispatch resolves a model-facing name to exactly one offered tool.
            Equal("srv\0read-file", plan.NameOwners[dashed.Name]); Equal("srv\0read_file", plan.NameOwners[underscored.Name]);
            Equal("mcp__srv__other", plan.Tools.Single(tool => tool.OriginalName == "other").Name);
            Equal("dashed", dashed.Description); Equal("underscored", underscored.Description);
        }
        return Task.CompletedTask;
    }

    private static Task StableSuffixes()
    {
        Equal("mcp__docs__search", McpCatalogPlanner.CreateToolName("docs", "search"));
        Equal("mcp__my_server__get_item_v2", McpCatalogPlanner.CreateToolName("my-server", "get.item/v2"));
        Equal("mcp__my_server", McpCatalogPlanner.Namespace("my-server"));
        var longName = McpCatalogPlanner.CreateToolName("server", new string('x', 100));
        Equal(64, longName.Length); Equal("mcp__server__" + new string('x', 42) + "_" + Hash("server", new string('x', 100)), longName);
        Check(McpCatalogPlanner.CreateToolName("server", new string('x', 100) + "y") != longName);
        var taken = McpCatalogPlanner.CreateToolName("s", "a_b");
        Equal("mcp__s__a_b_" + Hash("s", "a-b"), McpCatalogPlanner.CreateToolName("s", "a-b", name => name == taken));
        // The same inputs always yield the same names, across plans and processes (SHA-256 of the raw names).
        var entry = Entry("{\"command\":\"x\",\"exposure\":\"direct\"}", "my-server");
        var first = McpCatalogPlanner.Plan([new(entry, [Tool("get.item"), Tool("get_item"), Tool("get-item")])]);
        var second = McpCatalogPlanner.Plan([new(entry, [Tool("get-item"), Tool("get_item"), Tool("get.item")])]);
        foreach (var raw in new[] { "get.item", "get_item", "get-item" })
        {
            Equal("mcp__my_server__get_item_" + Hash("my-server", raw), first.Tools.Single(tool => tool.OriginalName == raw).Name);
            Equal(first.Tools.Single(tool => tool.OriginalName == raw).Name, second.Tools.Single(tool => tool.OriginalName == raw).Name);
        }
        Equal("mcp__my_server", first.Tools[0].Namespace.Name); Equal(ToolExposure.Direct, first.Tools[0].Exposure);
        return Task.CompletedTask;
    }

    private static Task CollidingServers()
    {
        var global = new McpConfigurationDocument("global.json", """{"mcpServers":{"work-files":{"command":"a"},"work_files":{"command":"b"}}}""");
        var loaded = McpConfigurationReader.Load(global, null, false);
        Equal("work-files", string.Join(',', loaded.Servers.Select(server => server.Name)));
        Equal("global.json: server \"work_files\" conflicts with \"work-files\"", loaded.Errors.Single());
        // A project server cannot share a namespace with a global one either; the same name replaces it instead.
        var project = new McpConfigurationDocument("project.json", """{"mcpServers":{"work_files":{"command":"c"},"work-files":{"command":"d"}}}""");
        var layered = McpConfigurationReader.Load(global, project, true);
        Equal("work-files", string.Join(',', layered.Servers.Select(server => server.Name)));
        Equal("d", layered.Servers[0].Config.Raw.Value.GetProperty("command").GetString()); Equal(McpConfigurationScope.Project, layered.Servers[0].Scope);
        Equal("global.json: server \"work_files\" conflicts with \"work-files\"|project.json: server \"work_files\" conflicts with \"work-files\"", string.Join('|', layered.Errors));
        // Registered servers: a clash among registrations fails; mcp.json wins over a registration of the same namespace.
        var configured = McpConfigurationReader.Load(new("global.json", """{"mcpServers":{"my-server":{"command":"a"}}}"""), null, false);
        var catalog = McpCatalogPlanner.ComposeServers(configured, [new("my_server", Entry("{\"command\":\"b\"}", "my_server").Config, "ext-a")]);
        Equal("my-server", string.Join(',', catalog.Servers.Select(server => server.Name)));
        Equal("\"my_server\" registered by ext-a is overridden by \"my-server\" in global.json", catalog.Overridden.Single());
        var error = Throws<ArgumentException>(() => McpCatalogPlanner.ComposeServers(McpConfigurationReader.Load(null, null, false),
            [new("a-b", Entry("{\"command\":\"b\"}", "a-b").Config, "ext-a"), new("a_b", Entry("{\"command\":\"b\"}", "a_b").Config, "ext-b")]));
        Check(error.Message.StartsWith("MCP server \"a_b\" conflicts with registered server \"a-b\"", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    // ---------- exposure ----------

    private static Task Alias()
    {
        var config = Entry("""{"command":"x","exposure":"codemode-deferred","toolExposure":{"a":"codemode-deferred","b":"direct"}}""").Config;
        Equal(McpExposure.Codemode, config.Exposure);
        Equal("a=Codemode,b=Direct", string.Join(',', config.ToolExposure.Select(pair => pair.Key + "=" + pair.Value)));
        Equal("codemode", config.Raw.Value.GetProperty("exposure").GetString());
        Equal("codemode", config.Raw.Value.GetProperty("toolExposure").GetProperty("a").GetString());
        Equal(ToolExposure.Deferred, McpCatalogPlanner.ToToolExposure(McpExposure.Codemode));
        Equal(ToolExposure.Deferred, McpCatalogPlanner.ToToolExposure(McpExposure.Deferred));
        using var wrong = JsonDocument.Parse("{\"command\":\"x\",\"exposure\":\"model-only\"}");
        Equal("server \"srv\": exposure must be one of \"codemode\", \"deferred\", \"direct\", \"hidden\"", McpConfigurationReader.Validate("srv", wrong.RootElement).Error);
        var plan = McpCatalogPlanner.Plan([new(new("srv", config, "synthetic.json", McpConfigurationScope.Global), [Tool("a"), Tool("b")])]);
        Check(plan.NeedsCodemode); Check(!plan.NeedsToolSearch);
        Check(McpConfigurationReader.HasIndirectTools(config) && McpConfigurationReader.HasDirectTools(config));
        return Task.CompletedTask;
    }

    private static Task Description()
    {
        var loaded = McpConfigurationReader.Load(new("global.json", """{"mcpServers":{"described":{"command":"x","description":"  Docs search \n"},"bad":{"command":"x","description":1}}}"""), null, false);
        Equal("described", loaded.Servers.Single().Name); Equal("  Docs search \n", loaded.Servers.Single().Config.Description);
        Equal("global.json: server \"bad\": description must be a string", loaded.Errors.Single());
        var plan = McpCatalogPlanner.Plan([new(loaded.Servers.Single(), [Tool("search")], "Server instructions.")]);
        // Upstream registerTools puts the connection instructions on the namespace itself.
        Equal(new ToolNamespace("mcp__described", "Docs search") { Instructions = "Server instructions." }, plan.Tools.Single().Namespace);
        Equal("Server instructions.", plan.Tools.Single().NamespaceInstructions);
        return Task.CompletedTask;
    }

    private static Task ProjectOverrides()
    {
        var global = new McpConfigurationDocument("agent/mcp.json", """{"mcpServers":{"tools":{"command":"x","env":{"TOKEN":"secret"}},"gh":{"command":"g","exposure":"deferred"}}}""");
        // An override cannot change the command, which would run with the global env.
        var rejected = McpConfigurationReader.Load(global, new(".pi/mcp.json", """{"mcpServers":{"tools":{"enabled":false,"args":["y"]},"missing":{"enabled":false}}}"""), true);
        Equal("tools,gh", string.Join(',', rejected.Servers.Select(server => server.Name)));
        Equal(null, rejected.Servers[0].Override); Check(rejected.Servers[0].Config.Enabled);
        Equal(".pi/mcp.json: server \"tools\": an override can only set enabled, exposure, toolExposure|" +
            ".pi/mcp.json: server \"missing\" needs \"command\" or \"url\", or a global server to override", string.Join('|', rejected.Errors));
        var disabled = McpConfigurationReader.Load(global, new(".pi/mcp.json", """{"mcpServers":{"tools":{"enabled":false}}}"""), true).Servers[0];
        Equal(".pi/mcp.json", disabled.Override); Equal("agent/mcp.json", disabled.Source); Equal(McpConfigurationScope.Global, disabled.Scope);
        Check(!disabled.Config.Enabled);
        Equal("""{"command":"x","env":{"TOKEN":"secret"},"enabled":false}""", disabled.Config.Raw.ToString());
        // Exposure and toolExposure overrides, exact names and `*` patterns, with the alias resolved.
        var exposed = McpConfigurationReader.Load(global, new(".pi/mcp.json",
            """{"mcpServers":{"gh":{"exposure":"codemode-deferred","toolExposure":{"get_*":"direct","get_me":"hidden","*delete*":"hidden"}}}}"""), true).Servers[1];
        Equal(".pi/mcp.json", exposed.Override); Equal(McpExposure.Codemode, exposed.Config.Exposure);
        Equal(McpExposure.Hidden, McpConfigurationReader.GetToolExposure(exposed.Config, "get_me"));
        Equal(McpExposure.Direct, McpConfigurationReader.GetToolExposure(exposed.Config, "get_issue"));
        Equal(McpExposure.Hidden, McpConfigurationReader.GetToolExposure(exposed.Config, "repo_delete"));
        Equal(McpExposure.Codemode, McpConfigurationReader.GetToolExposure(exposed.Config, "list"));
        Equal("g", exposed.Config.Raw.Value.GetProperty("command").GetString());
        // Invalid override values are reported and leave the global server unchanged.
        var invalid = McpConfigurationReader.Load(global, new(".pi/mcp.json", """{"mcpServers":{"gh":{"exposure":"visible"}}}"""), true);
        Equal(McpExposure.Deferred, invalid.Servers[1].Config.Exposure); Equal(null, invalid.Servers[1].Override);
        Check(invalid.Errors.Single().StartsWith(".pi/mcp.json: server \"gh\": exposure must be one of", StringComparison.Ordinal));
        // Untrusted projects cannot add or override servers.
        var untrusted = McpConfigurationReader.Load(global, new(".pi/mcp.json", """{"mcpServers":{"tools":{"enabled":false}}}"""), false);
        Check(untrusted.Servers[0].Config.Enabled && untrusted.Servers[0].Override is null);
        return Task.CompletedTask;
    }

    private static async Task ProviderAuth()
    {
        var global = new McpConfigurationDocument("agent/mcp.json", """
            {"mcpServers":{"radius":{"url":"https://radius.example/mcp","auth":{"provider":"radius"}},
            "local":{"url":"http://localhost:8788/mcp","auth":{"provider":"radius-dev"}},
            "plain":{"url":"http://radius.example/mcp","auth":{"provider":"radius"}},
            "empty":{"url":"https://radius.example/mcp","auth":{"provider":""}}}}
            """);
        var project = new McpConfigurationDocument(".pi/mcp.json", """
            {"mcpServers":{"radius":{"url":"https://evil.example/mcp","auth":{"provider":"radius"}},"own":{"url":"https://own.example/mcp","auth":{"provider":"radius"}},
            "local":{"enabled":false,"auth":{"provider":"other"}}}}
            """);
        var loaded = McpConfigurationReader.Load(global, project, true);
        // The project entry cannot replace the global one: it would send the credential to its own URL.
        Equal("radius:Global:https://radius.example/mcp,local:Global:http://localhost:8788/mcp", string.Join(',', loaded.Servers.Select(server =>
            $"{server.Name}:{server.Scope}:{server.Config.Raw.Value.GetProperty("url").GetString()}")));
        Equal(string.Join('|', [
            "agent/mcp.json: server \"plain\": auth requires an https URL, or http on localhost, 127.0.0.1, or [::1]",
            "agent/mcp.json: server \"empty\": auth.provider must be a provider name",
            ".pi/mcp.json: server \"radius\": auth is only allowed in the global mcp.json",
            ".pi/mcp.json: server \"own\": auth is only allowed in the global mcp.json",
            ".pi/mcp.json: server \"local\": an override can only set enabled, exposure, toolExposure"]), string.Join('|', loaded.Errors));
        var radius = loaded.Servers[0];
        Equal("radius", radius.Config.AuthProvider); Check(!McpConfigurationReader.UsesOAuth(radius.Config));
        Check(McpConfigurationReader.UsesOAuth(Entry("{\"url\":\"https://a.example/mcp\"}").Config));
        Check(!McpConfigurationReader.UsesOAuth(Entry("{\"url\":\"https://a.example/mcp\",\"headers\":{\"authorization\":\"Bearer x\"}}").Config));
        // The token is read on every request, so the provider's refreshes apply.
        var issued = 0; var asked = new List<string>();
        var authentication = McpProviderTokenAuthentication.Create(radius, (provider, _) => { asked.Add(provider); return ValueTask.FromResult<string?>("token-" + ++issued); });
        Equal("token-1", await authentication.Token(CancellationToken.None)); Equal("token-2", await authentication.Token(CancellationToken.None));
        Equal("radius,radius", string.Join(',', asked)); Equal(null, authentication.OnUnauthorized);
        Equal("MCP server \"radius\" requires sign-in. Run /login radius to sign in.", McpProviderTokenAuthentication.SignInRequiredMessage(radius));
        // A project-scoped entry never gains provider-token authority, even when constructed directly.
        Throws<InvalidOperationException>(() => McpProviderTokenAuthentication.Create(radius with { Scope = McpConfigurationScope.Project },
            (_, _) => ValueTask.FromResult<string?>("leak")));
        // A project override of a global provider server keeps the global authority and only toggles it.
        var overridden = McpConfigurationReader.Load(global, new(".pi/mcp.json", """{"mcpServers":{"radius":{"exposure":"direct"}}}"""), true).Servers[0];
        Equal(McpConfigurationScope.Global, overridden.Scope); Equal(".pi/mcp.json", overridden.Override); Equal("radius", overridden.Config.AuthProvider);
        Equal("https://radius.example/mcp", overridden.Config.Raw.Value.GetProperty("url").GetString());
    }

    // ---------- prompt section ----------

    private static McpServerListing Listing(string name, string? description = null, string? exposure = null, string? instructions = null) =>
        new(Entry("{\"command\":\"x\"" + (description is null ? "" : ",\"description\":" + JsonSerializer.Serialize(description)) +
            (exposure is null ? "" : ",\"exposure\":\"" + exposure + "\"") + "}", name), instructions);

    private static Task ServersSection()
    {
        var section = McpServersSection.Render([Listing("docs", "Docs search.\nMore."), Listing("later", exposure: "deferred"),
            Listing("direct", "Declared.", "direct"), Listing("plain", instructions: "From instructions.")]);
        Equal(string.Join('\n', [
            "MCP servers whose tools are not declared to you. Call the tools of `codemode` servers from codemode scripts. Load the tools of `tool_search` servers with `tool_search`.",
            "- mcp__docs (codemode): Docs search.", "- mcp__later (tool_search)", "- mcp__plain (codemode): From instructions."]), section);
        Equal(null, McpServersSection.Render([Listing("direct", "Declared.", "direct")]));
        Equal("MCP servers whose tools are not declared to you. Load the tools of `tool_search` servers with `tool_search`.\n- mcp__my_docs (tool_search)",
            McpServersSection.Render([Listing("my-docs", exposure: "deferred")]));
        // Disabled servers are not listed.
        Equal(null, McpServersSection.Render([new(Entry("{\"command\":\"x\",\"enabled\":false}", "off"))]));
        var shortened = McpServersSection.Render(Enumerable.Range(0, 40).Select(index => Listing($"server{index}", new string('x', 400))))!;
        Check(shortened.Length <= McpServersSection.MaxSectionChars); Equal(41, shortened.Split('\n').Length);
        Check(shortened.Contains("- mcp__server39 (codemode): x", StringComparison.Ordinal));
        Check(shortened.Split('\n').Skip(1).All(line => line.EndsWith((char)0x2026)));
        var omitted = McpServersSection.Render(Enumerable.Range(0, 200).Select(index => Listing($"server-with-a-long-name-{index}", "desc")))!;
        Check(omitted.Length <= McpServersSection.MaxSectionChars);
        var lines = omitted.Split('\n'); var last = lines[^1];
        Check(last.StartsWith("- " + (char)0x2026 + " ", StringComparison.Ordinal) && last.EndsWith(" more servers; find their tools with searchTools()", StringComparison.Ordinal));
        var count = int.Parse(last.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
        Equal(200, lines.Length - 2 + count);
        // Prompt start: unchanged sections add nothing; a change is appended as a patch; removal is a null value.
        var tagged = McpServersSection.Tag(section!);
        Equal("<mcp_servers>\n" + section + "\n</mcp_servers>", tagged);
        Equal(null, McpServersSection.Diff(tagged, section));
        Equal(new McpServersSectionPatch("mcp_servers", tagged), McpServersSection.Diff(null, section));
        var changed = McpServersSection.Render([Listing("docs", "Docs search.")]);
        Equal(new McpServersSectionPatch("mcp_servers", McpServersSection.Tag(changed!)), McpServersSection.Diff(tagged, changed));
        Equal(new McpServersSectionPatch("mcp_servers", null), McpServersSection.Diff(tagged, null));
        Equal(null, McpServersSection.Diff(null, null));
        // The first prompt waits only for servers with `direct` tools.
        var waits = McpServersSection.FirstPromptWaitsFor([Listing("docs").Entry, Listing("direct", exposure: "direct").Entry,
            Entry("{\"command\":\"x\",\"toolExposure\":{\"one\":\"direct\"}}", "mixed")]);
        Equal("direct,mixed", string.Join(',', waits.Select(entry => entry.Name)));
        return Task.CompletedTask;
    }

    // ---------- OAuth ----------

    private static Task OAuthConfig()
    {
        var loaded = McpConfigurationReader.Load(new("global.json", """
            {"mcpServers":{
            "named":{"url":"https://a.example/mcp","oauth":{"clientName":"Claude Code"}},
            "unnamed":{"url":"https://a.example/mcp","oauth":{"clientName":" "}},
            "metadata":{"url":"https://a.example/mcp","oauth":{"authServerMetadataUrl":"https://idp.example/m"}},
            "loopbackMetadata":{"url":"https://a.example/mcp","oauth":{"authServerMetadataUrl":"http://127.0.0.1:9/m"}},
            "plainMetadata":{"url":"https://a.example/mcp","oauth":{"authServerMetadataUrl":"http://idp.example/m"}},
            "cimd":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"cimd","callbackUrl":"http://localhost/callback"}},
            "dcr":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"dcr","clientId":"x"}},
            "badRegistration":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"auto"}},
            "cimdClient":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"cimd","clientId":"x"}},
            "cimdPath":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"cimd","callbackUrl":"http://127.0.0.1/cb"}},
            "cimdIpv6":{"url":"https://a.example/mcp","oauth":{"clientRegistration":"cimd","callbackUrl":"http://[::1]/callback"}}}}
            """), null, false);
        Equal("named,metadata,loopbackMetadata,cimd,dcr", string.Join(',', loaded.Servers.Select(server => server.Name)));
        Equal(string.Join('|', [
            "global.json: server \"unnamed\": oauth.clientName must be a non-empty string",
            "global.json: server \"plainMetadata\": oauth.authServerMetadataUrl must be an https URL, or http on localhost, 127.0.0.1, or [::1]",
            "global.json: server \"badRegistration\": oauth.clientRegistration must be \"dcr\" or \"cimd\"",
            "global.json: server \"cimdClient\": oauth.clientRegistration \"cimd\" cannot be combined with oauth.clientId or oauth.clientName",
            "global.json: server \"cimdPath\": oauth.clientRegistration \"cimd\" requires oauth.callbackUrl on localhost or 127.0.0.1 with path /callback",
            "global.json: server \"cimdIpv6\": oauth.clientRegistration \"cimd\" requires oauth.callbackUrl on localhost or 127.0.0.1 with path /callback"]),
            string.Join('|', loaded.Errors));
        Equal("a b c", McpOAuthScope.StepUp("a b", "b  c"));
        Equal(null, McpOAuthScope.StepUp("a", null)); Equal(null, McpOAuthScope.StepUp("a", ""));
        Equal("x", McpOAuthScope.StepUp(null, "x"));
        return Task.CompletedTask;
    }

    private static readonly Uri Server = new("https://mcp.example.test/mcp");
    private static readonly Uri LoopbackCallback = new("http://127.0.0.1:43123/callback");
    private const string Issuer = "https://issuer.example.test/";
    private static string Metadata(bool issuerParameter = false, bool documents = false, string issuer = Issuer) =>
        "{\"issuer\":\"" + issuer + "\",\"authorization_endpoint\":\"" + issuer + "authorize\",\"token_endpoint\":\"" + issuer + "token\"," +
        "\"registration_endpoint\":\"" + issuer + "register\",\"response_types_supported\":[\"code\"],\"code_challenge_methods_supported\":[\"S256\"]," +
        "\"token_endpoint_auth_methods_supported\":[\"none\"]" + (issuerParameter ? ",\"authorization_response_iss_parameter_supported\":true" : "") +
        (documents ? ",\"client_id_metadata_document_supported\":true" : "") + "}";
    private static McpOAuthState Cached(string metadata, bool client = true, McpOAuthTokens? tokens = null) => new(Server.AbsoluteUri,
        client ? JsonData.Parse("{\"client_id\":\"admitted-client\"}") : null, tokens, CodeVerifier: "stored-verifier", OAuthState: "admitted-state",
        Discovery: JsonData.Parse("{\"authorizationServerUrl\":\"" + Issuer + "\",\"authorizationServerMetadata\":" + metadata + "}"));

    private sealed class MemoryBackend : IMcpOAuthCredentialBackend
    {
        private readonly object gate = new();
        internal string? Text; internal int Writes;
        public T WithLock<T>(Func<string?, (T Result, string? Next)> update)
        { lock (gate) { var (result, next) = update(Text); if (next is not null) { Text = next; Writes++; } return result; } }
    }
    private sealed class Physical(Func<CancellationToken, ValueTask<HttpResponseMessage>> send, Action stopped) : IMcpAdmittedHttpRequestOperation
    {
        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken cancellationToken) => send(cancellationToken);
        public Task StopAsync() { stopped(); return Task.CompletedTask; }
    }
    private sealed class FakeServer
    {
        internal readonly List<(string Method, Uri Uri, string Body)> Log = [];
        internal int Stops;
        internal Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> Respond = (_, _, _) => Task.FromResult(Response(404, "{}"));
        internal McpAdmittedHttpRequestFactory Factory => request => new Physical(async token =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            lock (Log) Log.Add((request.Method.Method, request.RequestUri!, body)); Interlocked.Increment(ref requests);
            return await Respond(request, body, token);
        }, () => Interlocked.Increment(ref Stops));
    }
    private static HttpResponseMessage Response(int status, string text) => new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static McpDefaultOAuthHostResources Resources(IMcpAdmittedOAuthStateStore store, FakeServer server, Func<Uri, CancellationToken, ValueTask> redirect,
        Uri? callback = null) => new(Server, callback ?? LoopbackCallback,
            JsonData.Parse("{\"redirect_uris\":[\"" + (callback ?? LoopbackCallback).AbsoluteUri + "\"],\"client_name\":\"PiSharp fixture\",\"scope\":\"read\"}"),
            store, () => 1000, _ => ValueTask.FromResult(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()), redirect,
            (state, _) => state == "admitted-state" ? ValueTask.CompletedTask : ValueTask.FromException(new InvalidOperationException("response-state mismatch")),
            server.Factory, (_, _) => true, new(), AuthorizationState: _ => ValueTask.FromResult<string?>("admitted-state"));

    private static async Task IssuerValidation()
    {
        var backend = new MemoryBackend(); var credentials = new McpOAuthCredentialStore(backend); var store = credentials.ForServer("docs", Server);
        await store.SaveAsync(Cached(Metadata(issuerParameter: true)));
        var server = new FakeServer { Respond = (_, _, _) => Task.FromResult(Response(200, "{\"access_token\":\"issued\",\"token_type\":\"Bearer\"}")) };
        var host = McpDefaultOAuthHost.Install(Resources(store, server, (_, _) => ValueTask.CompletedTask));
        try
        {
            var wrong = await Failure(host.CompleteAuthorizationAsync("code", "admitted-state", iss: "https://evil.example.test/"));
            var mismatch = Find<McpOAuthIssuerMismatchException>(wrong);
            Equal(Issuer, mismatch.Expected); Equal("https://evil.example.test/", mismatch.Received);
            Equal("OAuth issuer mismatch: expected \"https://issuer.example.test/\", received \"https://evil.example.test/\"", mismatch.Message);
            // The server promised `iss`, so a response without it is rejected as well.
            var missing = Find<McpOAuthIssuerMismatchException>(await Failure(host.CompleteAuthorizationAsync("code", "admitted-state")));
            Equal(null, missing.Received); Check(missing.Message.EndsWith("received none", StringComparison.Ordinal));
            Equal(0, server.Log.Count); Equal(null, (await store.LoadAsync())!.Tokens);
            var result = await host.CompleteAuthorizationAsync("code", "admitted-state", iss: Issuer);
            Equal(McpOAuthAuthorizationOutcome.Authorized, result.Outcome); Equal(1, server.Log.Count);
            Check(server.Log[0].Uri.AbsoluteUri == Issuer + "token" && server.Log[0].Body.Contains("code=code", StringComparison.Ordinal));
            var saved = (await store.LoadAsync())!.Tokens!;
            // A response without `scope` grants the requested scope, recorded for later step-ups.
            Equal("issued", saved.AccessToken); Equal("read", saved.Scope);
        }
        finally { await host.DisposeAsync(); }
        // Without the promise, a present but different `iss` is still rejected; an absent one is accepted.
        var plainStore = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        await plainStore.SaveAsync(Cached(Metadata()));
        var plainServer = new FakeServer { Respond = (_, _, _) => Task.FromResult(Response(200, "{\"access_token\":\"issued\",\"token_type\":\"Bearer\",\"scope\":\"\"}")) };
        var plain = McpDefaultOAuthHost.Install(Resources(plainStore, plainServer, (_, _) => ValueTask.CompletedTask));
        try
        {
            Find<McpOAuthIssuerMismatchException>(await Failure(plain.CompleteAuthorizationAsync("code", "admitted-state", iss: "https://evil.example.test/")));
            Equal(0, plainServer.Log.Count);
            Equal(McpOAuthAuthorizationOutcome.Authorized, (await plain.CompleteAuthorizationAsync("code", "admitted-state")).Outcome);
            // `scope: ""` is absent, so the requested scope is recorded.
            Equal("read", (await plainStore.LoadAsync())!.Tokens!.Scope);
        }
        finally { await plain.DisposeAsync(); }
    }

    private static async Task StepUpScopes()
    {
        var store = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        await store.SaveAsync(Cached(Metadata(), tokens: new("old", "Bearer", Scope: "read", RefreshToken: "refresh")));
        var server = new FakeServer(); Uri? redirected = null;
        var host = McpDefaultOAuthHost.Install(Resources(store, server, (uri, _) => { redirected = uri; return ValueTask.CompletedTask; }));
        try
        {
            using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer error=\"insufficient_scope\", scope=\"write\"");
            var failure = await Failure(host.Authentication.OnUnauthorized!(new(response, Server, "old"), CancellationToken.None).AsTask());
            Find<McpOAuthAuthorizationRequiredException>(failure);
            // The challenge lists only the missing scope; the step-up asks for it on top of the granted one and never refreshes.
            Check(redirected is not null && Query(redirected, "scope") == "read write", "Step-up scope union missing: " + redirected);
            Equal(0, server.Log.Count);
        }
        finally { await host.DisposeAsync(); }
    }

    private static Task SeparateCredentials()
    {
        var credentials = new McpOAuthCredentialStore(new MemoryBackend());
        var url = new Uri("https://mcp.example.com/mcp");
        credentials.ForServer("work", url).SaveAsync(new(url.AbsoluteUri, Tokens: new("work-token", "Bearer"))).AsTask().Wait();
        credentials.ForServer("personal", url).SaveAsync(new(url.AbsoluteUri, Tokens: new("personal-token", "Bearer"))).AsTask().Wait();
        Equal("work-token", credentials.ForServer("work", url).LoadAsync().AsTask().Result!.Tokens!.AccessToken);
        Equal("personal-token", credentials.ForServer("personal", url).LoadAsync().AsTask().Result!.Tokens!.AccessToken);
        Check(credentials.Remove("work", url));
        Equal(null, credentials.ForServer("work", url).LoadAsync().AsTask().Result);
        Equal("personal-token", credentials.Tokens("personal", url)!.AccessToken);
        Equal(("mcp__my_work|https://mcp.example.com/mcp", "https://mcp.example.com/mcp"), McpOAuthCredentialStore.StoreKeys("my-work", url));
        return Task.CompletedTask;
    }

    private static async Task LegacyMigration()
    {
        const string Url = "https://mcp.example.com/mcp";
        var legacyState = "{\"serverUrl\":\"" + Url + "\",\"tokens\":{\"access_token\":\"legacy-token\",\"token_type\":\"Bearer\",\"scope\":\"read\"},\"futureField\":7}";
        var expected = "{\n  \"mcp__my_work|" + Url + "\": {\n    \"serverUrl\": \"" + Url + "\",\n    \"tokens\": {\n      \"access_token\": \"legacy-token\",\n" +
            "      \"token_type\": \"Bearer\",\n      \"scope\": \"read\"\n    },\n    \"futureField\": 7\n  }\n}\n";
        for (var run = 0; run < 2; run++)
        {
            var backend = new MemoryBackend { Text = "{\"" + Url + "\":" + legacyState + "}" }; var store = new McpOAuthCredentialStore(backend);
            var url = new Uri(Url);
            // Reading tokens does not take the legacy state over.
            Equal("legacy-token", store.Tokens("work", url)!.AccessToken); Equal(0, backend.Writes);
            Equal("legacy-token", (await store.ForServer("my_work", url).LoadAsync())!.Tokens!.AccessToken); Equal(1, backend.Writes);
            // Names differing only in `-` and `_` are the same server; the move happened once and is not repeated.
            Equal("legacy-token", (await store.ForServer("my-work", url).LoadAsync())!.Tokens!.AccessToken);
            Equal("read", store.Tokens("my-work", url)!.Scope);
            Equal(null, await store.ForServer("personal", url).LoadAsync()); Equal(1, backend.Writes);
            // Deterministic: the same document, byte for byte, on every run; unknown stored fields survive the move.
            Equal(expected, backend.Text);
        }
        var signOut = new MemoryBackend { Text = "{\"" + Url + "\":" + legacyState + "}" }; var legacyStore = new McpOAuthCredentialStore(signOut);
        Check(legacyStore.Remove("work", new(Url))); Equal("{}\n", signOut.Text); Check(!legacyStore.Remove("work", new(Url)));
        // An unreadable document is treated as empty, like the original.
        Equal(null, await new McpOAuthCredentialStore(new MemoryBackend { Text = "[1" }).ForServer("x", new(Url)).LoadAsync());
    }

    private static async Task CancelledSignIn()
    {
        var store = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        var server = new FakeServer
        {
            Respond = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/oauth-protected-resource/mcp" => Response(200, "{\"resource\":\"" + Server.AbsoluteUri + "\",\"authorization_servers\":[\"" + Issuer + "\"]}"),
                "/.well-known/oauth-authorization-server" => Response(200, Metadata()),
                "/register" => Response(201, "{\"client_id\":\"registered\"}"),
                _ => Response(404, "{}")
            })
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var observed = false;
        var host = McpDefaultOAuthHost.Install(Resources(store, server, async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { observed = token.IsCancellationRequested; }
        }));
        using var cancel = new CancellationTokenSource();
        var signIn = host.AuthorizeAsync(host.Options(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var registration = server.Log.Single(row => row.Uri.AbsolutePath == "/register").Body;
        // Dynamic registration declares `application_type` for the loopback redirect URI (SEP-837).
        Check(registration.Contains("\"application_type\":\"native\"", StringComparison.Ordinal), registration);
        var sent = server.Log.Count;
        await cancel.CancelAsync();
        var failure = await Failure(signIn);
        Check(signIn.IsCanceled || Find<OperationCanceledException>(failure) is not null);
        Check(observed, "The pending browser step did not observe the cancellation.");
        // Nothing is exchanged or stored after the cancellation, every request was stopped, and the host closes cleanly.
        Equal(sent, server.Log.Count); Equal(server.Log.Count, server.Stops);
        Equal(null, (await store.LoadAsync())!.Tokens);
        // Close joins the cancelled original and reports only that cancellation, with no new cleanup fault.
        await CloseRetaining<McpOAuthOrchestrationCanceledException>(host);
        Equal(sent, server.Log.Count);
    }

    private static async Task RequestTimeout()
    {
        var store = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        var server = new FakeServer { Respond = async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); } };
        var host = McpDefaultOAuthHost.Install(Resources(store, server, (_, _) => ValueTask.CompletedTask) with { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var signIn = host.AuthorizeAsync(host.Options());
            var failure = await Failure(signIn);
            Check(signIn.IsFaulted && !signIn.IsCanceled, "A request timeout must not look like the caller's cancellation.");
            Check(Find<TimeoutException>(failure).Message.Contains("timed out after 0.2 s", StringComparison.Ordinal));
            Check(started.Elapsed < TimeSpan.FromSeconds(10)); Check(server.Log.Count >= 1 && server.Stops == server.Log.Count);
        }
        finally { await CloseRetaining<TimeoutException>(host); }
        Equal(TimeSpan.FromSeconds(15), McpDefaultOAuthHostResources.DefaultRequestTimeout);
    }

    private static async Task MetadataUrl()
    {
        var store = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        const string Idp = "https://idp.example.test/";
        var server = new FakeServer
        {
            Respond = (request, _, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                "https://idp.example.test/custom/metadata" => Response(200, Metadata(issuer: Idp)),
                "https://idp.example.test/register" => Response(201, "{\"client_id\":\"registered\"}"),
                _ => Response(404, "{}")
            })
        };
        Uri? redirected = null; var callback = new Uri("https://app.example.test/oauth/callback");
        var host = McpDefaultOAuthHost.Install(Resources(store, server, (uri, _) => { redirected = uri; return ValueTask.CompletedTask; }, callback)
            with { AuthorizationServerMetadataUrl = new("https://idp.example.test/custom/metadata") });
        try
        {
            var result = await host.AuthorizeAsync(host.Options());
            Equal(McpOAuthAuthorizationOutcome.Redirect, result.Outcome);
            Check(redirected!.AbsoluteUri.StartsWith(Idp + "authorize?", StringComparison.Ordinal));
            Check(server.Log.All(row => !row.Uri.AbsolutePath.StartsWith("/.well-known/oauth-authorization-server", StringComparison.Ordinal) &&
                !row.Uri.AbsolutePath.Contains("openid-configuration", StringComparison.Ordinal)));
            // The configured document is trusted as configured and never cached.
            Equal(null, (await store.LoadAsync())!.Discovery);
            // An https redirect URI registers as a `web` client.
            Check(server.Log.Single(row => row.Uri.AbsolutePath == "/register").Body.Contains("\"application_type\":\"web\"", StringComparison.Ordinal));
        }
        finally { await host.DisposeAsync(); }
        var insecureServer = new FakeServer();
        var insecure = McpDefaultOAuthHost.Install(Resources(new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server), insecureServer,
            (_, _) => ValueTask.CompletedTask) with { AuthorizationServerMetadataUrl = new("http://idp.example.test/m") });
        try
        {
            var failure = await Failure(insecure.AuthorizeAsync(insecure.Options()));
            Equal("insecure_endpoint", Find<McpOAuthProtocolException>(failure).Code); Equal(0, insecureServer.Log.Count);
        }
        finally { await CloseRetaining<McpOAuthProtocolException>(insecure); }
    }

    private static async Task ClientMetadataDocument()
    {
        var id = McpOAuthClientMetadataDocuments.CallbackId(Server);
        Equal(12, id.Length); Equal(id, McpOAuthClientMetadataDocuments.CallbackId(new("https://mcp.example.test/mcp#ignored")));
        var expectedId = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Server.AbsoluteUri))[..9]).Replace('+', '-').Replace('/', '_');
        Equal(expectedId, id);
        var documentBase = new Uri("https://docs.example.test/oauth");
        Equal(new McpOAuthClientMetadataDocument("https://docs.example.test/oauth/client.json", "http://127.0.0.1:43123/callback"),
            McpOAuthClientMetadataDocuments.Create(documentBase, Server, "http://127.0.0.1:43123/callback", JsonData.Parse(Metadata(true, true))));
        Equal(new McpOAuthClientMetadataDocument($"https://docs.example.test/oauth/{id}/client.json", $"http://127.0.0.1:43123/callback/{id}"),
            McpOAuthClientMetadataDocuments.Create(documentBase, Server, "http://127.0.0.1:43123/callback", JsonData.Parse(Metadata(false, true))));
        Throws<McpOAuthProtocolException>(() => McpOAuthClientMetadataDocuments.Create(documentBase, Server, "http://127.0.0.1/callback", JsonData.Parse(Metadata())));
        Throws<McpOAuthProtocolException>(() => McpOAuthClientMetadataDocuments.Create(documentBase, Server, "http://127.0.0.1/callback", null));
        // Through the host: no registration, nothing stored for the client, and the server-specific redirect URI.
        var store = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        await store.SaveAsync(Cached(Metadata(documents: true), client: false));
        var server = new FakeServer { Respond = (_, _, _) => Task.FromResult(Response(200, "{\"access_token\":\"issued\",\"token_type\":\"Bearer\"}")) };
        Uri? redirected = null;
        var host = McpDefaultOAuthHost.Install(Resources(store, server, (uri, _) => { redirected = uri; return ValueTask.CompletedTask; })
            with { ClientMetadataDocumentBase = documentBase });
        try
        {
            Equal(McpOAuthAuthorizationOutcome.Redirect, (await host.AuthorizeAsync(host.Options())).Outcome);
            Equal($"https://docs.example.test/oauth/{id}/client.json", Query(redirected!, "client_id"));
            Equal($"http://127.0.0.1:43123/callback/{id}", Query(redirected!, "redirect_uri"));
            Equal(0, server.Log.Count); Equal(null, (await store.LoadAsync())!.ClientInformation);
            Equal(McpOAuthAuthorizationOutcome.Authorized, (await host.CompleteAuthorizationAsync("code", "admitted-state")).Outcome);
            var exchange = server.Log.Single().Body;
            Check(exchange.Contains("redirect_uri=" + Uri.EscapeDataString($"http://127.0.0.1:43123/callback/{id}"), StringComparison.Ordinal), exchange);
            Check(exchange.Contains("client_id=" + Uri.EscapeDataString($"https://docs.example.test/oauth/{id}/client.json"), StringComparison.Ordinal), exchange);
        }
        finally { await host.DisposeAsync(); }
    }

    // ---------- helpers ----------

    private static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0].Replace('+', ' ')) == name) return parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return null;
    }
    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected a failure.");
    }
    private static T Find<T>(Exception root) where T : Exception
    {
        var pending = new Stack<Exception>(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); pending.Push(root);
        while (pending.Count != 0)
        {
            var value = pending.Pop(); if (!seen.Add(value) || seen.Count > 4096) continue;
            if (value is T match) return match;
            if (value is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (value.InnerException is { } next) pending.Push(next);
        }
        throw new InvalidOperationException($"No {typeof(T).Name} in the failure: {root}");
    }
    /// <summary>The host's close joins its last operation and re-reports a failed one; it must carry that failure.</summary>
    private static async Task CloseRetaining<T>(McpDefaultOAuthHost host) where T : Exception
    {
        try { await host.DisposeAsync(); }
        catch (Exception error) { _ = Find<T>(error); }
    }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Check(bool condition, string? message = null) { if (!condition) throw new InvalidOperationException(message ?? "MCP sync assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
}
