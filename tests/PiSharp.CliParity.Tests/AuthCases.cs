using PiSharp.Cli.Pi;
using PiSharp.Cli.Authentication;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

// cli/auth-command.ts, cli/auth-check.ts and cli/credential-print.ts through the entry; migrations.ts runMigrations; --list-models.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AuthCases() =>
    [
        ("auth.mcp-provider-login-logout-guidance", async () =>
        {
            using var sandbox = new Sandbox("auth-mcp-guidance");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{"provider-server":{"url":"https://synthetic.invalid/mcp","auth":{"provider":"radius"}}}}""");
            var (code, _, stderr) = await sandbox.Run("mcp", "login", "provider-server");
            Equal(1, code, "provider login offers shell guidance");
            Check(stderr.Contains("pisharp auth login radius", StringComparison.Ordinal), "login guidance: " + stderr);
            (code, _, stderr) = await sandbox.Run("mcp", "logout", "provider-server");
            Equal(1, code, "provider logout offers shell guidance");
            Check(stderr.Contains("pisharp auth logout radius", StringComparison.Ordinal), "logout guidance: " + stderr);
            using var output = new StringWriter(); using var errors = new StringWriter();
            var host = sandbox.Host(output, errors, null) with
            {
                ConfigureMcp = options => options with { CreateChannel = _ => (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new ShellNeedsAuthChannel()) }
            };
            Equal(1, await PiCommand.RunAsync(["mcp", "list"], host, default), "provider authentication rejection");
            Check(output.ToString().Contains("pisharp auth login radius", StringComparison.Ordinal) && !output.ToString().Contains("mcp login provider-server", StringComparison.Ordinal), "provider list guidance: " + output);
        }),
        ("auth.shell-login-logout-api-key-and-fake-oauth", async () =>
        {
            using var sandbox = new Sandbox("auth-shell");
            using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
            var host = sandbox.Host(stdout, stderr, null, interactive: true) with { Stdin = new StringReader("synthetic-key\n") };
            var store = new AuthJsonCredentialStore(host.LiveRuntime.AuthPath!);
            var opened = new List<string>();
            var flow = new ShellTestOAuth();
            var login = new ProviderLoginHost(store, () => new HttpClient(new RejectAuthNetwork()), opened.Add)
            { OAuthOverride = (_, _) => flow };
            Equal(0, await PiAuthCommand.RunAsync(["auth", "login", "anthropic", "--api-key"], host, default, login), "API key login");
            Equal("synthetic-key", (await store.ReadEntryAsync("anthropic", default))!.Key, "API key persisted");
            Check(!stdout.ToString().Contains("synthetic-key", StringComparison.Ordinal), "key never echoed");
            Equal(0, await PiAuthCommand.RunAsync(["auth", "login", "openai-codex", "--no-browser"], host, default, login), "headless account login");
            Equal("device_code", flow.Method!, "headless selects alternative method");
            Equal(0, opened.Count, "no browser opened");
            Equal("oauth", (await store.ReadEntryAsync("openai-codex", default))!.Type, "OAuth persisted before success");
            Equal(0, await PiAuthCommand.RunAsync(["auth", "login", "openai-codex"], host, default, login), "browser account login");
            Equal("browser", flow.Method!, "default selects browser method");
            Equal(1, opened.Count, "browser callback used");
            Equal(0, await PiAuthCommand.RunAsync(["auth", "logout", "anthropic"], host, default, login), "logout");
            Check(await store.ReadEntryAsync("anthropic", default) is null, "only selected credentials removed");
            Check(await store.ReadEntryAsync("openai-codex", default) is not null, "other provider retained");
            Equal(0, await PiAuthCommand.RunAsync(["auth", "logout", "anthropic"], host, default, login), "repeated logout");
            Equal(1, await PiAuthCommand.RunAsync(["auth", "login", "anthropic", "--api-key", "--no-browser"], host, default, login), "invalid flag combination");
            Equal(1, await PiAuthCommand.RunAsync(["auth", "login", "openai-codex", "--api-key"], host, default, login), "OAuth only rejects API key mode");
            Equal(1, await PiAuthCommand.RunAsync(["auth", "login", "anthropic"], host with { StdinIsTty = false }, default, login), "redirected login refused");
            flow.Fail = true;
            stdout.GetStringBuilder().Clear();
            Equal(1, await PiAuthCommand.RunAsync(["auth", "login", "openai-codex"], host, default, login), "failed provider flow");
            Check(!stdout.ToString().Contains("Logged in", StringComparison.Ordinal) && !stderr.ToString().Contains("synthetic-secret", StringComparison.Ordinal), "failure neither succeeds nor leaks secret");
        }),
        ("auth.help-errors-check-and-print-api-key", async () =>
        {
            using var sandbox = new Sandbox("auth");
            var (code, stdout, _) = await sandbox.Run("auth");
            Check(code == 0 && stdout.StartsWith("Usage:\n  pisharp auth print-api-key [--provider <provider>] [--model <model>]\n", StringComparison.Ordinal) && stdout.Contains("auth login <provider>", StringComparison.Ordinal) &&
                stdout.EndsWith("--credentials emits the credential, or includes it in JSON output.\n", StringComparison.Ordinal), "auth help: " + stdout);
            var (badCode, _, badErr) = await sandbox.Run("auth", "login");
            Check(badCode == 1 && badErr == "Error: Usage: pisharp auth login <provider>\n", "login usage: " + badErr);
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

    private sealed class RejectAuthNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Unexpected auth network request");
    }
    private sealed class ShellNeedsAuthChannel : IMcpAdmittedRequestChannel
    {
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) => ValueTask.FromException<JsonData>(new McpHttpStatusException(401));
        public Task CloseAsync() => Task.CompletedTask;
    }
    private sealed class ShellTestOAuth : IProviderOAuth
    {
        public string Name => "Synthetic";
        public bool IsSubscription => false;
        public string? LoginLabel => null;
        public string? Method { get; private set; }
        public bool Fail { get; set; }
        public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken token)
        {
            Method = await interaction.PromptAsync(new(AuthPromptKind.Select, "Method", Options: [new("browser", "Browser"), new("device_code", "Device code")]), token);
            if (Fail) throw new InvalidOperationException("synthetic-secret");
            interaction.Notify(new(AuthEventKind.AuthUrl) { Url = "https://synthetic.invalid/login" });
            return new("synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        }
        public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken token) => throw new InvalidOperationException("Unexpected refresh");
        public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
    }
}
