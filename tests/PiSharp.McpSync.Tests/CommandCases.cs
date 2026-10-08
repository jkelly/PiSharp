using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Runtime;

// `mcp add`, `remove` and `list` (packages/coding-agent/src/extensions/mcp/cli.ts and config.ts addMcpServerConfig,
// removeMcpServerConfig, editMcpServers). Expected text is typed from that source with PiSharp's app name; servers are fake
// channels and every file lives in a fresh temporary directory. Nothing is captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> CommandCases() =>
    [
        ("cli.mcp-add-replace-local-and-errors", McpCliAdd),
        ("cli.mcp-remove-global-local-and-hints", McpCliRemove),
        ("cli.mcp-list-text-json-and-exit-code", McpCliList)
    ];

    private static async Task McpCliAdd()
    {
        // The file's indentation (four spaces here) and other content are kept.
        var (root, options) = CliFixture("{\n    \"autoEnableCodemode\": false,\n    \"mcpServers\": {}\n}\n");
        try
        {
            var global = Path.Combine(root, "agent", "mcp.json"); var project = Path.Combine(options.Cwd, ".pi", "mcp.json");
            Equal((0, $"Added global MCP server \"docs\" in {global}.\nCheck it with: PiSharp.Cli mcp list\n", ""),
                await Mcp(options, "add", "docs", "--env", "TOKEN=${DOCS_TOKEN}", "--exposure", "direct", "--description", "Docs.", "--", "docs-server", "--port", "1"));
            Equal((0, $"Added global MCP server \"remote\" in {global}.\nCheck it with: PiSharp.Cli mcp list\n", ""),
                await Mcp(options, "add", "remote", "--url", "https://remote.example.test/mcp", "--bearer-token-env-var", "REMOTE_TOKEN", "--header", "X-Team=a=b"));
            Equal((0, $"Added global MCP server \"signin\" in {global}.\nCheck it with: PiSharp.Cli mcp list. If it requires sign-in: PiSharp.Cli mcp login signin\n", ""),
                await Mcp(options, "add", "signin", "--url", "https://signin.example.test/mcp", "--oauth-client-id", "client", "--oauth-callback-port", "8123"));
            Equal((0, $"Replaced global MCP server \"docs\" in {global}.\nCheck it with: PiSharp.Cli mcp list\n", ""),
                await Mcp(options, "add", "docs", "docs-server", "--flag-of-the-server"));
            Equal("{\n    \"autoEnableCodemode\": false,\n    \"mcpServers\": {\n        \"docs\": {\n            \"command\": \"docs-server\",\n            \"args\": [\n                \"--flag-of-the-server\"\n            ]\n        },\n" +
                "        \"remote\": {\n            \"url\": \"https://remote.example.test/mcp\",\n            \"headers\": {\n                \"X-Team\": \"a=b\",\n                \"Authorization\": \"Bearer ${REMOTE_TOKEN}\"\n            }\n        },\n" +
                "        \"signin\": {\n            \"url\": \"https://signin.example.test/mcp\",\n            \"oauth\": {\n                \"clientId\": \"client\",\n                \"callbackPort\": 8123\n            }\n        }\n    }\n}\n",
                File.ReadAllText(global).ReplaceLineEndings("\n"));
            // -l writes the project file, which is read only once the project is trusted.
            Equal((0, $"Added project MCP server \"local\" in {project}.\nThe project is not trusted, so {project} is ignored until you start PiSharp.Cli in the project and trust it.\nCheck it with: PiSharp.Cli mcp list\n", ""),
                await Mcp(options, "add", "local", "-l", "--", "local-server"));
            Equal((0, $"Replaced project MCP server \"local\" in {project}.\nCheck it with: PiSharp.Cli mcp list\n", ""),
                await Mcp(options with { IsProjectTrusted = cwd => cwd == options.Cwd }, "add", "local", "-l", "--", "local-server"));
            Equal("{\n  \"mcpServers\": {\n    \"local\": {\n      \"command\": \"local-server\"\n    }\n  }\n}\n", File.ReadAllText(project).ReplaceLineEndings("\n"));

            const string Hint = "Use \"PiSharp.Cli mcp --help\" for usage.\n";
            var usage = "Usage: PiSharp.Cli mcp add <server> [options] (--url <url> | -- <command> [args...])\n" + Hint;
            Equal((1, "", usage), await Mcp(options, "add", "x"));
            Equal((1, "", usage), await Mcp(options, "add", "x", "--url", "https://x.example.test/mcp", "--", "cmd"));
            Equal((1, "", "--env only applies to stdio servers.\n"), await Mcp(options, "add", "x", "--url", "https://x.example.test/mcp", "--env", "A=1"));
            Equal((1, "", "--header only applies to HTTP servers (--url).\n"), await Mcp(options, "add", "x", "--header", "A=1", "--", "cmd"));
            Equal((1, "", "--env expects KEY=VALUE, got \"=1\".\n"), await Mcp(options, "add", "x", "--env", "=1", "--", "cmd"));
            Equal((1, "", "Unknown option --bogus.\n" + Hint), await Mcp(options, "add", "x", "--bogus", "--", "cmd"));
            Equal((1, "", "--cwd needs a value.\n"), await Mcp(options, "add", "x", "--cwd"));
            var invalid = await Mcp(options, "add", "x", "--exposure", "everywhere", "--", "cmd");
            Check(invalid.Code == 1 && invalid.Output == "" && invalid.Error.Length > 1, "invalid exposure refused: " + invalid.Error);
            File.WriteAllText(global, "[]");
            Equal((1, "", $"Could not update {global}: {global}: expected an object with an \"mcpServers\" object\n"), await Mcp(options, "add", "x", "--", "cmd"));
        }
        finally { Delete(root); }
    }

    private static async Task McpCliRemove()
    {
        var (root, options) = CliFixture();
        try
        {
            var global = Path.Combine(root, "agent", "mcp.json"); var project = Path.Combine(options.Cwd, ".pi", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "{\"mcpServers\":{\"projected\":{\"command\":\"p\"}}}");
            Equal((0, $"Removed global MCP server \"keyed\" from {global}.\n", ""), await Mcp(options, "remove", "keyed"));
            Check(!File.ReadAllText(global).Contains("keyed", StringComparison.Ordinal) && File.ReadAllText(global).Contains("\"docs\"", StringComparison.Ordinal), "keyed removed, docs kept");
            Equal((1, "", $"No global MCP server named \"keyed\" in {global}.\n"), await Mcp(options, "remove", "keyed"));
            Equal((1, "", $"No project MCP server named \"docs\" in {project}. It is defined in {global}; omit --local.\n"), await Mcp(options, "remove", "docs", "-l"));
            Equal((1, "", $"No global MCP server named \"projected\" in {global}. It is defined in {project}; use --local.\n"), await Mcp(options, "remove", "projected"));
            Equal((0, $"Removed project MCP server \"projected\" from {project}.\n", ""), await Mcp(options, "remove", "projected", "--local"));
            Equal((1, "", "Usage: PiSharp.Cli mcp remove <server> [-l]\nUse \"PiSharp.Cli mcp --help\" for usage.\n"), await Mcp(options, "remove"));
        }
        finally { Delete(root); }
    }

    private static async Task McpCliList()
    {
        var (root, options) = CliFixture("""
            {"mcpServers":{
              "docs":{"command":"docs-server","args":["--port","1"],"exposure":"direct","toolExposure":{"search":"hidden"}},
              "broken":{"command":"broken-server"},
              "signin":{"url":"https://signin.example.test/mcp"},
              "off":{"command":"off-server","enabled":false},
              "bad":{"exposure":"direct"}}}
            """);
        options = options with
        {
            ProcessEnvironment = () => [],
            CreateChannel = entry => (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(entry.Name switch
            {
                "docs" => new FakeChannel("Docs."),
                "broken" => new FakeChannel(null, new IOException("spawn broken-server ENOENT\nsecond line")),
                _ => new FakeChannel(null, new McpOAuthAuthorizationRequiredException())
            })
        };
        try
        {
            var (code, output, error) = await Mcp(options, "list");
            Equal(1, code); Equal("", error);
            var global = Path.Combine(root, "agent", "mcp.json");
            Equal(string.Join("\n",
                "docs: connected, 1 tool (direct, global)", "  docs-server --port 1", "  tools: search [hidden]",
                "broken: failed (codemode, global)", "  broken-server", "  spawn broken-server ENOENT", "  second line",
                "signin: needs sign-in (codemode, global)", "  https://signin.example.test/mcp", "  sign in with: PiSharp.Cli mcp login signin",
                "  MCP server \"signin\" requires sign-in. Run /mcp to sign in.",
                "off: disabled (codemode, global)", "  off-server",
                await ConfigError(global), ""), output);

            var json = await Mcp(options, "list", "--json");
            Equal(1, json.Code);
            using var document = JsonDocument.Parse(json.Output);
            var servers = document.RootElement.GetProperty("servers");
            Equal(4, servers.GetArrayLength());
            Equal("""{"name":"docs","scope":"global","source":""" + JsonSerializer.Serialize(global) +
                ""","enabled":true,"exposure":"direct","transport":"docs-server --port 1","state":"connected","tools":["search"],"toolExposure":{"search":"hidden"}}""",
                JsonSerializer.Serialize(servers[0]));
            Equal("failed", servers[1].GetProperty("state").GetString());
            Equal("needs-auth", servers[2].GetProperty("state").GetString());
            Equal("disabled", servers[3].GetProperty("state").GetString());
            Equal(1, document.RootElement.GetProperty("errors").GetArrayLength());
            Check(json.Output.StartsWith("{\n  \"servers\": [\n    {\n      \"name\": \"docs\",", StringComparison.Ordinal), "two-space JSON");

            // Only connected servers and no config errors: exit 0.
            var (_, clean) = CliFixture("""{"mcpServers":{"docs":{"command":"docs-server"}}}""");
            clean = clean with { ProcessEnvironment = () => [], CreateChannel = _ => (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new FakeChannel(null)) };
            Equal((0, "docs: connected, 1 tool (codemode, global)\n  docs-server\n  tools: search\n", ""), await Mcp(clean, "list"));
            var (_, empty) = CliFixture("{}");
            Equal((0, $"No MCP servers configured. Add them to {Path.Combine(empty.AgentDirectory, "mcp.json")} or .pi/mcp.json.\n", ""), await Mcp(empty, "list"));
            Equal((1, "", "Usage: PiSharp.Cli mcp list [--json]\nUse \"PiSharp.Cli mcp --help\" for usage.\n"), await Mcp(options, "list", "extra"));
        }
        finally { Delete(root); }
    }

    /// <summary>The configuration reader's error line for the invalid server, as `list` prints it.</summary>
    private static Task<string> ConfigError(string global)
    {
        var loaded = PiSharp.Extensions.Mcp.Configuration.McpConfigurationReader.Load(new(global, File.ReadAllText(global)), null, false);
        return Task.FromResult("config error: " + loaded.Errors.Single());
    }
}
