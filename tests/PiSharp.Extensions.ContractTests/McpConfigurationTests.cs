using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

// Authored synthetic controls. Coordinator owns registration in Program.cs and formal execution.
internal static class McpConfigurationTests
{
    internal const string Prefix = "mcp-configuration.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "defaults-and-absent-files", Defaults),
        (Prefix + "trusted-layering-preserves-order-disabled-and-errors", Layering),
        (Prefix + "untrusted-project-is-not-parsed", UntrustedProject),
        (Prefix + "invalid-root-preference-and-json-diagnostics", DocumentErrors),
        (Prefix + "server-validation-and-transport-precedence", ServerValidation),
        (Prefix + "oauth-loopback-port-and-null-validation", OAuthValidation),
        (Prefix + "literal-command-env-and-unknown-metadata-stay-inert", InertValues),
        (Prefix + "exact-before-first-wildcard-and-literal-regex-text", Exposure),
        (Prefix + "json-last-duplicate-and-array-index-enumeration", JsonOrder),
        (Prefix + "configured-entries-win-over-extension-registration", RegisteredServers),
        (Prefix + "provider-tool-name-length-collision-and-utf16", Names),
        (Prefix + "catalog-metadata-exposure-schema-and-description", Catalog),
        (Prefix + "disabled-disconnected-and-resource-discovery", Discovery),
        (Prefix + "borrowed-catalog-refresh-retains-name-ownership", Refresh),
        (Prefix + "schema-ownership-and-document-lifetime", Ownership)
    ];

    private static McpConfigurationDocument Document(string source, string text) => new(source, text);
    private static McpServerConfiguration Config(string json, string name = "docs")
    {
        using var document = JsonDocument.Parse(json);
        var result = McpConfigurationReader.Validate(name, document.RootElement);
        return result.Config ?? throw new InvalidOperationException(result.Error);
    }
    private static string? Error(string json, string name = "docs")
    { using var document = JsonDocument.Parse(json); return McpConfigurationReader.Validate(name, document.RootElement).Error; }
    private static McpServerEntry Entry(string json, string name = "docs") => new(name, Config(json, name), "synthetic.json", McpConfigurationScope.Global);
    private static McpOfferedTool Tool(string name, string? description = null, string? title = null, string? annotationTitle = null) =>
        new(name, JsonData.Parse("{}"), description, title, annotationTitle);
    private static Task Defaults()
    {
        var loaded = McpConfigurationReader.Load(null, null, true);
        Equal(0, loaded.Servers.Length); Equal(0, loaded.Errors.Length); Equal<bool?>(null, loaded.AutoEnableCodemode);
        Check(loaded.EffectiveAutoEnableCodemode);
        var config = Config("{\"command\":\"\"}");
        Equal(McpTransportKind.Stdio, config.Transport); Equal(McpExposure.Codemode, config.Exposure);
        Equal(60d, config.TimeoutSeconds); Check(config.Enabled); Equal(0, config.ToolExposure.Length);
        return Task.CompletedTask;
    }
    private static Task Layering()
    {
        var global = Document("global.json", """{"autoEnableCodemode":false,"mcpServers":{"first":{"command":"one"},"second":{"url":"https://invalid.example/mcp"}}} """);
        var project = Document("project.json", """{"autoEnableCodemode":true,"mcpServers":{"first":{"command":"two","enabled":false},"second":{"type":"sse","url":"https://invalid.example/mcp"},"third":{"command":"three"}}} """);
        var loaded = McpConfigurationReader.Load(global, project, true);
        Equal("first,second,third", string.Join(',', loaded.Servers.Select(server => server.Name)));
        Equal(McpConfigurationScope.Project, loaded.Servers[0].Scope); Check(!loaded.Servers[0].Config.Enabled);
        Equal("two", loaded.Servers[0].Config.Raw.Value.GetProperty("command").GetString());
        Equal(McpConfigurationScope.Global, loaded.Servers[1].Scope); Check(loaded.EffectiveAutoEnableCodemode);
        Equal(1, loaded.Errors.Length); Check(loaded.Errors[0].StartsWith("project.json: server \"second\": legacy SSE", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
    private static Task UntrustedProject()
    {
        var loaded = McpConfigurationReader.Load(Document("global", """{"mcpServers":{"safe":{"command":"literal"}},"autoEnableCodemode":false} """),
            Document("untrusted", "invalid-json-must-not-be-parsed"), false);
        Equal(1, loaded.Servers.Length); Equal(0, loaded.Errors.Length); Check(!loaded.EffectiveAutoEnableCodemode);
        return Task.CompletedTask;
    }
    private static Task DocumentErrors()
    {
        foreach (var text in new[] { "[]", "null", "{\"mcpServers\":null}" })
        { var loaded = McpConfigurationReader.Load(Document("bad", text), null, false); Equal("bad: expected an object with an \"mcpServers\" object", loaded.Errors.Single()); }
        var preference = McpConfigurationReader.Load(Document("global", "{\"autoEnableCodemode\":false}"),
            Document("project", """{"autoEnableCodemode":null,"mcpServers":{"ok":{"command":"literal"}}} """), true);
        Check(!preference.EffectiveAutoEnableCodemode); Equal(1, preference.Servers.Length);
        Equal("project: autoEnableCodemode must be a boolean", preference.Errors.Single());
        Check(McpConfigurationReader.Load(Document("malformed", "{"), null, true).Errors.Single().StartsWith("malformed: ", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
    private static Task ServerValidation()
    {
        Equal("invalid server name \"bad.name\" (use letters, digits, \"_\" and \"-\")", Error("{}", "bad.name"));
        Equal("server \"docs\" must be an object", Error("null"));
        foreach (var (json, fragment) in new[]
        {
            ("{\"command\":\"x\",\"exposure\":null}", "exposure must be one of"),
            ("{\"command\":\"x\",\"toolExposure\":[]}", "toolExposure must map"),
            ("{\"command\":\"x\",\"toolExposure\":{\"one\":\"wrong\"}}", "toolExposure \"one\" must be"),
            ("{\"command\":\"x\",\"enabled\":null}", "enabled must be a boolean"),
            ("{\"command\":\"x\",\"timeout\":0}", "timeout must be a positive number"),
            ("{\"command\":\"x\",\"args\":[1]}", "args must be an array of strings"),
            ("{\"command\":\"x\",\"env\":{\"x\":false}}", "env must map names to strings"),
            ("{\"command\":\"x\",\"cwd\":false}", "cwd must be a string"),
            ("{\"url\":\"file:///tmp/x\"}", "url must be an http or https URL"),
            ("{\"url\":\"https://invalid.example\",\"headers\":{\"x\":null}}", "headers must map names to strings")
        }) Check(Error(json)?.Contains(fragment, StringComparison.Ordinal) == true);
        Equal(McpTransportKind.Http, Config("{\"command\":\"x\",\"url\":\"https://invalid.example\"}").Transport);
        Equal(McpTransportKind.Http, Config("{\"type\":\"streamable-http\",\"url\":\"http://127.0.0.1/mcp\"}").Transport);
        Equal(McpTransportKind.Stdio, Config("{\"type\":\"stdio\",\"command\":\"x\",\"url\":\"https://invalid.example\"}").Transport);
        Check(Error("{\"type\":\"http\",\"command\":\"x\"}")?.Contains("needs either", StringComparison.Ordinal) == true);
        return Task.CompletedTask;
    }
    private static Task OAuthValidation()
    {
        foreach (var url in new[] { "http://localhost/callback", "http://127.0.0.1:8123/callback", "http://[::1]/callback" }) Check(McpConfigurationReader.IsLoopbackRedirectUri(url));
        foreach (var url in new[] { "https://localhost/callback", "http://invalid.example/callback", "http://localhost/callback?q=1", "http://localhost/callback#x" }) Check(!McpConfigurationReader.IsLoopbackRedirectUri(url));
        Equal("server \"docs\": oauth must be an object", Error("{\"url\":\"https://invalid.example\",\"oauth\":null}"));
        Equal("server \"docs\": oauth.callbackPort must be a port number", Error("{\"url\":\"https://invalid.example\",\"oauth\":{\"callbackPort\":1.5}}"));
        Equal("server \"docs\": oauth.callbackUrl and oauth.callbackPort name different ports", Error("{\"url\":\"https://invalid.example\",\"oauth\":{\"callbackUrl\":\"http://localhost:8123/callback\",\"callbackPort\":8124}}"));
        Check(Config("{\"url\":\"https://invalid.example\",\"oauth\":{\"callbackUrl\":\"http://localhost:80/callback\",\"callbackPort\":8124}}").Transport == McpTransportKind.Http);
        Check(Config("{\"command\":\"x\",\"oauth\":null}").Transport == McpTransportKind.Stdio);
        return Task.CompletedTask;
    }
    private static Task InertValues()
    {
        var config = Config("""{"command":"!must-not-run","args":["~/inert"],"cwd":"../inert","env":{"VALUE":"${UNUSED_SYNTHETIC}"},"unknown":{"value":7}} """);
        Equal("!must-not-run", config.Raw.Value.GetProperty("command").GetString());
        Equal("${UNUSED_SYNTHETIC}", config.Raw.Value.GetProperty("env").GetProperty("VALUE").GetString());
        Equal(7, config.Raw.Value.GetProperty("unknown").GetProperty("value").GetInt32());
        return Task.CompletedTask;
    }
    private static Task Exposure()
    {
        var config = Config("""{"command":"x","exposure":"hidden","toolExposure":{"read*":"deferred","*":"codemode","readOne":"direct"}} """);
        Equal(McpExposure.Direct, McpConfigurationReader.GetToolExposure(config, "readOne"));
        Equal(McpExposure.Deferred, McpConfigurationReader.GetToolExposure(config, "readOther"));
        Equal(McpExposure.Codemode, McpConfigurationReader.GetToolExposure(config, "other"));
        var literal = Config("""{"command":"x","exposure":"hidden","toolExposure":{"a.b[*":"direct"}} """);
        Equal(McpExposure.Direct, McpConfigurationReader.GetToolExposure(literal, "a.b[tail"));
        Equal(McpExposure.Hidden, McpConfigurationReader.GetToolExposure(literal, "axb[tail"));
        Equal(McpExposure.Hidden, McpConfigurationReader.GetToolExposure(literal, "a.b[line\nnext"));
        return Task.CompletedTask;
    }
    private static Task JsonOrder()
    {
        var loaded = McpConfigurationReader.Load(Document("synthetic", """{"mcpServers":{"z":{"command":"first"},"10":{"command":"ten"},"2":{"command":"two"},"z":{"command":"last","unknown":{"a":1,"a":2}}}} """), null, false);
        Equal("2,10,z", string.Join(',', loaded.Servers.Select(row => row.Name)));
        Equal("last", loaded.Servers[2].Config.Raw.Value.GetProperty("command").GetString());
        Equal(2, loaded.Servers[2].Config.Raw.Value.GetProperty("unknown").GetProperty("a").GetInt32());
        return Task.CompletedTask;
    }
    private static Task RegisteredServers()
    {
        var loaded = McpConfigurationReader.Load(Document("global", """{"mcpServers":{"docs":{"command":"configured","enabled":false}}} """), null, false);
        var registered = new[] { new McpRegisteredServer("docs", Config("{\"command\":\"extension\"}"), "extension-a"),
            new McpRegisteredServer("new", Config("{\"command\":\"old\"}", "new"), "extension-b"),
            new McpRegisteredServer("new", Config("{\"command\":\"latest\"}", "new"), "extension-c") };
        var catalog = McpCatalogPlanner.ComposeServers(loaded, registered);
        Equal("docs,new", string.Join(',', catalog.Servers.Select(row => row.Name))); Check(!catalog.Servers[0].Config.Enabled);
        // Pi v1.1.0 names the configured server, which may differ from the registered name by `-`/`_`.
        Equal("\"docs\" registered by extension-a is overridden by \"docs\" in global", catalog.Overridden.Single());
        Equal("extension-c", catalog.Servers[1].Source); Equal(McpConfigurationScope.Extension, catalog.Servers[1].Scope);
        return Task.CompletedTask;
    }
    private static Task Names()
    {
        Equal("mcp__docs__a_b", McpCatalogPlanner.CreateToolName("docs", "a.b"));
        Equal("mcp__docs__a_b_63617bb9", McpCatalogPlanner.CreateToolName("docs", "a_b", _ => true));
        Equal("mcp__docs__" + new string('x', 44) + "_bb31f9f5", McpCatalogPlanner.CreateToolName("docs", new string('x', 100)));
        Equal("mcp__docs__x__y", McpCatalogPlanner.CreateToolName("docs", "x\ud83d\ude00y"));
        return Task.CompletedTask;
    }
    private static Task Catalog()
    {
        var entry = Entry("""{"command":"literal","timeout":2.5,"toolExposure":{"direct":"direct","later":"codemode-deferred","hidden":"hidden"}} """);
        var plan = McpCatalogPlanner.Plan([new(entry, [Tool("direct", " \ufeffdescription\ufeff "), Tool("later", " ", "title"), Tool("hidden")], "Instructions")]);
        Equal(3, plan.Tools.Length); Equal("description", plan.Tools[0].Description); Equal("title", plan.Tools[1].Description);
        Equal("MCP tool hidden from server docs", plan.Tools[2].Description);
        Equal(ToolExposure.Direct, plan.Tools[0].Exposure); Equal(ToolExposure.Deferred, plan.Tools[1].Exposure); Equal(ToolExposure.Hidden, plan.Tools[2].Exposure);
        // Pi v1.1.0: `codemode-deferred` is an alias of `codemode`; server instructions are kept apart from the
        // namespace description, which only a configured `description` sets.
        Equal(McpExposure.Codemode, plan.Tools[1].McpExposure); Check(plan.NeedsCodemode); Check(!plan.NeedsToolSearch);
        Equal("mcp__docs", plan.Tools[0].Namespace.Name); Equal<string?>(null, plan.Tools[0].Namespace.Description);
        Equal("Instructions", plan.Tools[0].NamespaceInstructions);
        Equal("docs/direct", plan.Tools[0].Label); Equal(2500d, plan.Tools[0].TimeoutMilliseconds);
        Equal("object", plan.Tools[0].Parameters.Value.GetProperty("type").GetString());
        Equal(JsonValueKind.Object, plan.Tools[0].Parameters.Value.GetProperty("properties").ValueKind);
        return Task.CompletedTask;
    }
    private static Task Discovery()
    {
        var plan = McpCatalogPlanner.Plan([
            new(Entry("{\"command\":\"x\",\"enabled\":false}", "off"), [Tool("x")], HasResources: true),
            new(Entry("{\"command\":\"x\"}", "gone"), [Tool("x")], Connected: false),
            new(Entry("{\"command\":\"x\",\"exposure\":\"deferred\"}"), [Tool("x")]),
            new(Entry("{\"command\":\"x\",\"exposure\":\"direct\"}", "resources"), [], HasResources: true)
        ], autoEnableCodemode: false);
        Equal(1, plan.Tools.Length); Equal("docs", plan.Tools[0].Server);
        Check(plan.NeedsToolSearch); Check(!plan.NeedsCodemode); Check(!plan.AutoEnableCodemode); Equal<McpExposure?>(McpExposure.Direct, plan.ResourceToolsExposure);
        var resourceOnly = McpCatalogPlanner.Plan([new(Entry("{\"command\":\"x\",\"exposure\":\"codemode-deferred\"}"), [], HasResources: true)]);
        Check(resourceOnly.NeedsCodemode); Equal<McpExposure?>(McpExposure.Codemode, resourceOnly.ResourceToolsExposure);
        return Task.CompletedTask;
    }
    private static Task Refresh()
    {
        var entry = Entry("{\"command\":\"x\"}");
        // Pi v1.1.0: every tool whose name sanitizes to a shared name gets the hash suffix, independent of list order.
        var first = McpCatalogPlanner.Plan([new(entry, [Tool("a.b"), Tool("a_b")])]);
        Equal("mcp__docs__a_b_" + McpCatalogPlanner.CreateToolName("docs", "a.b", _ => true)[^8..], first.Tools[0].Name);
        Equal("mcp__docs__a_b_63617bb9", first.Tools[1].Name);
        // Without the sibling the plain name is free again; owned names stay with their owners across refreshes.
        var refreshed = McpCatalogPlanner.Plan([new(entry, [Tool("a_b")])], previousNameOwners: first.NameOwners);
        Equal("mcp__docs__a_b", refreshed.Tools.Single().Name); Equal(3, refreshed.NameOwners.Count);
        var later = McpCatalogPlanner.Plan([new(entry, [Tool("a-b")])], previousNameOwners: refreshed.NameOwners);
        Check(later.Tools.Single().Name.StartsWith("mcp__docs__a_b_", StringComparison.Ordinal));
        Equal(2, first.Tools.Length); Equal<string?>(null, refreshed.Tools.Single().Namespace.Description);
        return Task.CompletedTask;
    }
    private static Task Ownership()
    {
        var config = Config("{\"command\":\"literal\",\"args\":[\"retained\"]}");
        Equal("retained", config.Raw.Value.GetProperty("args")[0].GetString());
        var original = JsonData.Parse("{\"type\":null,\"properties\":null,\"additionalProperties\":false}");
        var plan = McpCatalogPlanner.Plan([new(new("docs", config, "synthetic", McpConfigurationScope.Global), [new("schema", original)])]);
        Equal(JsonValueKind.Null, original.Value.GetProperty("type").ValueKind);
        Equal("object", plan.Tools.Single().Parameters.Value.GetProperty("type").GetString());
        Equal(JsonValueKind.Null, plan.Tools.Single().Parameters.Value.GetProperty("properties").ValueKind);
        Equal(JsonValueKind.False, plan.Tools.Single().Parameters.Value.GetProperty("additionalProperties").ValueKind);
        return Task.CompletedTask;
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("MCP synthetic fixture assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
}
