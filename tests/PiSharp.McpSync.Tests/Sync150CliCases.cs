using PiSharp.Cli.Commands;

// Pi 1.5.0 sync, cli.mcp-login-local-flag (f9a26365568127461c47a07a37dcb42c140a3ac5): packages/coding-agent/src/extensions/mcp/cli.ts
// (be15fe3c1: login and logout accept -l, unknown options are reported as typed, editDistance suggestions). Expectations are authored
// from reading the v1.5.0 sources; nothing is captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync150CliCases() =>
    [
        ("sync150.mcp-login-logout-local-flag-and-suggestions", McpLoginLocalFlag),
    ];

    private static async Task McpLoginLocalFlag()
    {
        var (root, options) = CliFixture();
        try
        {
            const string Hint = "Use \"pisharp mcp --help\" for usage.\n";
            Check(McpCommand.Help.Contains("\nOptions for add, remove, login, and logout:\n  -l, --local ", StringComparison.Ordinal), "help names login and logout");
            Check(McpCommand.Usage.Contains("mcp login <server> [-l] [--timeout <seconds>]; mcp logout <server> [-l];", StringComparison.Ordinal), "usage");
            // -l requires a project server: a global one is refused with its source.
            var global = Path.Combine(options.AgentDirectory, "mcp.json");
            Equal((1, "", $"MCP server \"docs\" is defined in {global}, not in the project; omit --local.\n"), await Mcp(options, "login", "docs", "-l"));
            Equal((1, "", $"MCP server \"docs\" is defined in {global}, not in the project; omit --local.\n"), await Mcp(options, "logout", "docs", "--local"));
            // Unknown options are reported as typed (an alias is not shown expanded).
            Equal((1, "", "Unknown option -l.\n" + Hint), await Mcp(options, "list", "-l"));
            Equal((1, "", "Unknown option --bogus.\n" + Hint), await Mcp(options, "logout", "docs", "--bogus"));
            // A mistyped name within two edits gets the first such configured name as a suggestion.
            Equal((1, "", "No MCP server named \"dosc\". Did you mean \"docs\"? Configured: docs, keyed, local.\n"), await Mcp(options, "login", "dosc"));
            Equal((1, "", "No MCP server named \"lokal\". Did you mean \"local\"? Configured: docs, keyed, local.\n"), await Mcp(options, "logout", "lokal", "-l"));
            Equal((1, "", "No MCP server named \"nope\". Configured: docs, keyed, local.\n"), await Mcp(options, "login", "nope"));
            Equal(2, McpCommand.EditDistance("gurke", "gurker") + McpCommand.EditDistance("kitten", "sitten"));
            Equal(3, McpCommand.EditDistance("kitten", "sitting"));
            Equal(0, McpCommand.EditDistance("", ""));
            // A trusted project's server passes the -l check and reaches the OAuth check.
            Directory.CreateDirectory(Path.Combine(options.Cwd, ".pi"));
            File.WriteAllText(Path.Combine(options.Cwd, ".pi", "mcp.json"), "{\"mcpServers\":{\"team\":{\"command\":\"team-server\"}}}");
            var trusted = options with { IsProjectTrusted = cwd => cwd == options.Cwd };
            Equal((1, "", "MCP server \"team\" does not use OAuth. Only HTTP servers without an Authorization header do.\n"), await Mcp(trusted, "logout", "team", "-l"));
        }
        finally { Delete(root); }
    }
}
