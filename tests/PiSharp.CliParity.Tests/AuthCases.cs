using PiSharp.Cli.Pi;

// cli/auth-command.ts, cli/auth-check.ts and cli/credential-print.ts through the entry; migrations.ts runMigrations; --list-models.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AuthCases() =>
    [
        ("auth.help-errors-check-and-print-api-key", async () =>
        {
            using var sandbox = new Sandbox("auth");
            var (code, stdout, _) = await sandbox.Run("auth");
            Check(code == 0 && stdout.StartsWith("Usage:\n  pisharp auth print-api-key [--provider <provider>] [--model <model>]\n", StringComparison.Ordinal) &&
                stdout.EndsWith("--credentials emits the credential, or includes it in JSON output.\n", StringComparison.Ordinal), "auth help: " + stdout);
            var (badCode, _, badErr) = await sandbox.Run("auth", "login");
            Check(badCode == 1 && badErr == "Error: Unknown auth command \"login\". Use \"pisharp auth print-api-key\", \"pisharp auth print-bearer-token\", or \"pisharp auth check\".\n", "unknown: " + badErr);
            (badCode, _, badErr) = await sandbox.Run("auth", "check");
            Check(badCode == 2 && badErr == "Error: Auth checks require --provider <provider> or --model <model>\n", "check without provider: " + badErr);
            (badCode, _, badErr) = await sandbox.Run("auth", "print-api-key", "--json");
            Check(badCode == 1 && badErr == "Error: --json is only supported by auth check\n", "--json: " + badErr);
            (badCode, _, badErr) = await sandbox.Run("auth", "print-bearer-token", "--provider", "anthropic", "--min-expiry", "soon");
            Check(badCode == 1 && badErr == "Error: --min-expiry must use a duration such as 30m or 1h\n", "--min-expiry: " + badErr);
            (badCode, _, badErr) = await sandbox.Run("auth", "check", "--provider", "anthropic", "--frobnicate");
            Check(badCode == 1 && badErr == "Unknown option --frobnicate for \"auth check\".\nUse \"pisharp --help\" or \"pisharp auth check --provider <provider> [--json] [--credentials] [--no-refresh]\".\n", "unknown option: " + badErr);
            (code, stdout, _) = await sandbox.Run("auth", "check", "--provider", "anthropic", "--json");
            Check(code == 0 && stdout == "{\"status\":\"ready\",\"provider\":\"anthropic\",\"authType\":\"api_key\"}\n", "ready: " + stdout);
            (code, stdout, _) = await sandbox.Run("auth", "check", "--provider", "openai");
            Check(code == 1 && stdout == "not_ready\n", "not ready: " + stdout);
            (code, stdout, _) = await sandbox.Run("auth", "check", "--provider", "nope", "--json");
            Check(code == 1 && stdout == "{\"status\":\"not_ready\",\"provider\":\"nope\",\"reason\":\"provider_not_found\"}\n", "unknown provider: " + stdout);
            (code, stdout, _) = await sandbox.Run("auth", "print-api-key", "--provider", "anthropic");
            Check(code == 0 && stdout == "sk-test-key\n", "print-api-key: " + stdout);
            (code, stdout, _) = await sandbox.Run("auth", "check", "--provider", "anthropic", "--credentials");
            Check(code == 0 && stdout == "sk-test-key\n", "check --credentials: " + stdout);
            (badCode, _, badErr) = await sandbox.Run("auth", "print-api-key", "--provider", "nope");
            Check(badCode == 1 && badErr == "Error: Unknown provider \"nope\". Use --list-models to see available providers.\n", "unknown print provider: " + badErr);
        }),
        ("main.list-models-and-migrations", async () =>
        {
            using var sandbox = new Sandbox("list-models");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "oauth.json"), "{\"anthropic\":{\"access\":\"a\",\"refresh\":\"r\",\"expires\":1}}");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "commands", "x.md"), "old command");
            var (code, stdout, stderr) = await sandbox.Run("--list-models", "claude-sonnet-4-5");
            Equal(0, code, "exit; " + stderr);
            // Plain --list-models keeps stdout (isPlainRuntimeMetadataCommand), so migration messages print there before the table.
            Check(stdout.StartsWith("Migrated Global commands/ ", StringComparison.Ordinal) && stdout.Contains("\nprovider   model", StringComparison.Ordinal) &&
                stdout.Contains("claude-sonnet-4-5", StringComparison.Ordinal), "listing: " + stdout);
            Check(File.Exists(Path.Combine(sandbox.AgentDir, "oauth.json.migrated")) && File.ReadAllText(Path.Combine(sandbox.AgentDir, "auth.json")).Contains("\"type\": \"oauth\"", StringComparison.Ordinal),
                "oauth.json migrated to auth.json");
            Check(File.Exists(Path.Combine(sandbox.AgentDir, "prompts", "x.md")), "commands/ renamed to prompts/");
            Equal("", stderr, "nothing on stderr");
        }),
    ];
}
