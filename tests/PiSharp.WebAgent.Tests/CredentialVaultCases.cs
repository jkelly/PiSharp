using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

// Decision 0011 (run W2, WU5): the credential vault in process, over a temporary data folder and a fake environment (never this machine's
// keys, ~/.pi or auth.json): migration 100; Data Protection round trips (another start reads a secret; another key ring, another account
// or another kind of secret does not); no planted secret in credentials.json, webagent.db (and its WAL), the key ring or the log; masked
// hints only; the active account of each provider; read-only environment and ambient accounts; validation; the per-account OAuth source
// (serialized per account, never resurrecting a removed account); replacing a key; orphan secrets pruned at start; an unreadable
// credentials.json left untouched.
internal static partial class Program
{
    /// <summary>The credentials area in process: the core's registrations (webagent.db, Data Protection in keys\) and the area's, with a
    /// fake environment, a fake HTTP handler and a captured log.</summary>
    internal sealed class VaultHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly bool ownsRoot;

        public VaultHost(string name, Action<VaultSetup>? setup = null, string? root = null)
        {
            ownsRoot = root is null;
            Root = root ?? Temp(name);
            var settings = new VaultSetup();
            setup?.Invoke(settings);
            Environment = settings.Environment;
            Http = settings.Http;
            Options = new WebAgentOptions { DataDirectory = Path.Join(Root, settings.DataFolder) };
            Options.EnsureDirectories();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new ListLoggerProvider(Logs)));
            services.AddSingleton(new CredentialsOptions
            {
                Environment = variable => Environment.TryGetValue(variable, out var value) ? value : null,
                FileExists = _ => false,
                Home = Root,
                CreateHttp = () => new HttpMessageInvoker(Http, disposeHandler: false),
                Offline = settings.Offline,
                OpenAIAuthBaseUrl = FakeOAuth.AuthBase,
                CodexCallbackPort = settings.CallbackPort,
                SignInTimeout = settings.SignInTimeout,
                SignInStartWait = TimeSpan.FromSeconds(10),
                TestTimeout = TimeSpan.FromSeconds(10),
                Time = settings.Time,
            });
            services.AddWebAgentCore(Options);
            services.AddCredentialsArea(Options);
            provider = services.BuildServiceProvider();
            Applied = provider.GetRequiredService<WebAgentDb>().Migrate();
        }

        public string Root { get; }

        public WebAgentOptions Options { get; }

        public IReadOnlyList<int> Applied { get; }

        public Dictionary<string, string> Environment { get; }

        public FakeOAuth Http { get; }

        public ConcurrentQueue<string> Logs { get; } = new();

        public IServiceProvider Services => provider;

        public CredentialVault Vault => provider.GetRequiredService<CredentialVault>();

        public IAccountStore Accounts => provider.GetRequiredService<IAccountStore>();

        public ICredentialVault Secrets => provider.GetRequiredService<ICredentialVault>();

        public ISignInFlows SignIn => provider.GetRequiredService<ISignInFlows>();

        public IConnectionTester Tester => provider.GetRequiredService<IConnectionTester>();

        public WebAgentDb Db => provider.GetRequiredService<WebAgentDb>();

        public string LogText => string.Join("\n", Logs);

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            if (ownsRoot) DeleteTemp(Root);
        }
    }

    /// <summary>How <see cref="VaultHost"/> is set up.</summary>
    internal sealed class VaultSetup
    {
        public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

        public FakeOAuth Http { get; set; } = new();

        public bool Offline { get; set; }

        public int CallbackPort { get; set; } = 1455;

        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(2);

        public TimeProvider Time { get; set; } = TimeProvider.System;

        public string DataFolder { get; set; } = "data";
    }

    /// <summary>A logger that keeps every line (with its exception, as a sink would write it), to scan for secrets.</summary>
    internal sealed class ListLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(lines, categoryName);

        public void Dispose() { }

        private sealed class ListLogger(ConcurrentQueue<string> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
        }
    }

    private static void SqliteClearPools() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    /// <summary>Every file under <paramref name="folder"/> that holds <paramref name="secret"/> as UTF-8 or UTF-16 text.</summary>
    internal static List<string> FilesHolding(string folder, string secret)
    {
        SqliteClearPools();
        var utf8 = Encoding.UTF8.GetBytes(secret);
        var utf16 = Encoding.Unicode.GetBytes(secret);
        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            catch (IOException) { continue; }
            if (bytes.AsSpan().IndexOf(utf8) >= 0 || bytes.AsSpan().IndexOf(utf16) >= 0) found.Add(path);
        }
        return found;
    }

    /// <summary>Fails when <paramref name="text"/> holds any of <paramref name="secrets"/>.</summary>
    internal static void NoSecret(string text, string where, params string[] secrets)
    {
        foreach (var secret in secrets)
            Check(!text.Contains(secret, StringComparison.Ordinal), $"{where} holds a planted secret ({secret[..Math.Min(6, secret.Length)]}…)");
    }

    /// <summary>A planted secret: a recognizable value no real provider issued.</summary>
    internal static string Planted(string prefix) => prefix + "PLANTED" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private static string ToJson(object value) => JsonSerializer.Serialize(value, CredentialVault.JsonOptions);

    internal static IEnumerable<(string, Func<Task>)> CredentialVaultCases() =>
    [
        Case("credentials.migration-100-creates-the-accounts-tables", async () =>
        {
            await using var host = new VaultHost("cred-migration");
            Check(host.Applied.Contains(100), "migration 100 applies at start");
            Equal(MigrationArea.Accounts, MigrationArea.Of(CredentialVault.Migration.Id), "in the accounts range");
            var tables = ReadDb(host.Options.DatabasePath, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;", r => r.GetString(0));
            Check(tables.Contains("accounts") && tables.Contains("active_accounts"), "both tables: " + string.Join(",", tables));
            var columns = ReadDb(host.Options.DatabasePath, "SELECT name FROM pragma_table_info('accounts');", r => r.GetString(0));
            foreach (var column in new[] { "id", "provider", "label", "kind", "status", "hint", "created_at", "last_used_at" })
                Check(columns.Contains(column), "accounts." + column);
            Check(!columns.Any(column => column.Contains("secret", StringComparison.Ordinal) || column.Contains("key", StringComparison.Ordinal) ||
                column.Contains("token", StringComparison.Ordinal)), "no column for a secret: " + string.Join(",", columns));
            Equal(0, host.Accounts.List().Count, "no accounts in an empty environment");
            Check(!File.Exists(host.Options.CredentialsPath), "credentials.json is written only when a secret is stored");
        }),

        Case("credentials.data-protection-round-trip", async () =>
        {
            var root = Temp("cred-protect");
            try
            {
                var key = Planted("sk-proj-");
                string id;
                await using (var host = new VaultHost("unused", root: root))
                {
                    var added = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key) { Label = "Work" });
                    id = added.Account.Id;
                    Equal(key, (await host.Secrets.ReadAsync(id))!.ApiKey, "read back by the same start");
                    var stored = JsonNode.Parse(await File.ReadAllTextAsync(host.Options.CredentialsPath))!;
                    Equal(1, stored["version"]!.GetValue<int>(), "version");
                    Equal("apiKey", stored["secrets"]![id]!["kind"]!.GetValue<string>(), "the entry's kind");
                    Check(stored["secrets"]![id]!["value"]!.GetValue<string>().StartsWith("CfDJ8", StringComparison.Ordinal), "a Data Protection payload");
                }
                // Another start (another service provider) over the same folder reads it: the key ring is persisted.
                await using (var again = new VaultHost("unused", root: root))
                    Equal(key, (await again.Secrets.ReadAsync(id))!.ApiKey, "read back by another start");

                // Another key ring cannot: the same files beside a new keys\ folder.
                var other = Path.Join(root, "other");
                Directory.CreateDirectory(other);
                File.Copy(Path.Join(root, "data", "credentials.json"), Path.Join(other, "credentials.json"));
                File.Copy(Path.Join(root, "data", "webagent.db"), Path.Join(other, "webagent.db"));
                await using (var foreign = new VaultHost("unused", setup => setup.DataFolder = "other", root: root))
                {
                    Equal("Work", foreign.Accounts.Get(id)!.Label, "the account lists");
                    var error = await ThrowsAsync<CredentialUnavailableException>(() => foreign.Secrets.ReadAsync(id), "another key ring");
                    Equal(id, error.AccountId, "the account");
                    Check(error.Message.Contains("decrypt", StringComparison.Ordinal), "says why: " + error.Message);
                }

                // A value copied to another account, or read as another kind of secret, does not decrypt either (purpose + account id).
                await using (var host = new VaultHost("unused", root: root))
                {
                    var second = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")))).Account.Id;
                    var json = JsonNode.Parse(await File.ReadAllTextAsync(host.Options.CredentialsPath))!;
                    json["secrets"]![second]!["value"] = json["secrets"]![id]!["value"]!.GetValue<string>();
                    await File.WriteAllTextAsync(host.Options.CredentialsPath, json.ToJsonString());
                }
                await using (var host = new VaultHost("unused", root: root))
                {
                    var secondId = host.Accounts.List("openai").Single(account => account.Id != id).Id;
                    await ThrowsAsync<CredentialUnavailableException>(() => host.Secrets.ReadAsync(secondId), "a value copied from another account");
                    Equal(key, (await host.Secrets.ReadAsync(id))!.ApiKey, "the original still reads");
                }
                await using (var host = new VaultHost("unused", root: root))
                {
                    var protection = host.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
                    var file = new CredentialFile(Path.Join(root, "kinds.json"), protection, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System);
                    file.Write("acc-1", SecretKind.ApiKey, key);
                    var kinds = JsonNode.Parse(await File.ReadAllTextAsync(file.Path))!;
                    kinds["secrets"]!["acc-1"]!["kind"] = "oauth";
                    await File.WriteAllTextAsync(file.Path, kinds.ToJsonString());
                    var reread = new CredentialFile(file.Path, protection, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System);
                    Throws<CredentialUnavailableException>(() => reread.Read("acc-1", SecretKind.OAuth), "an API key read under the OAuth purpose");
                    Equal(null, reread.Read("acc-1", SecretKind.ApiKey), "and the entry is not an API key any more");
                }
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("credentials.no-plaintext-secret-on-disk-or-in-the-log", async () =>
        {
            var envKey = Planted("sk-ant-api03-");
            await using var host = new VaultHost("cred-plaintext", setup => setup.Environment["ANTHROPIC_API_KEY"] = envKey);
            var key = Planted("sk-proj-");
            var replaced = Planted("sk-proj-");
            var access = Planted("access-");
            var refresh = Planted("refresh-");
            var added = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key) { Settings = new Dictionary<string, string> { ["baseUrl"] = "https://proxy.example.test/v1" } });
            await host.Accounts.ReplaceApiKeyAsync(added.Account.Id, replaced);
            var saved = await host.Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot(access, refresh, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                new Dictionary<string, string> { ["accountId"] = "acct-plaintext" }), "acct-plaintext", new Dictionary<string, string>(), "ChatGPT", null, CancellationToken.None);
            await host.Secrets.ReadAsync(saved.Account.Id);
            await host.Secrets.ReadAsync(added.Account.Id);
            await host.Secrets.ReadAsync(CredentialVault.EnvironmentId("anthropic", "ANTHROPIC_API_KEY"));
            await host.Accounts.MarkExpiredAsync(added.Account.Id, $"401 Incorrect API key provided: {replaced}");

            foreach (var secret in new[] { key, replaced, access, refresh, envKey })
            {
                var holders = FilesHolding(host.Options.DataDirectory, secret);
                Check(holders.Count == 0, "a planted secret is on disk in " + string.Join(", ", holders));
            }
            Check(Directory.GetFiles(host.Options.DataDirectory, "*.tmp", SearchOption.AllDirectories).Length == 0, "no temporary file is left");
            NoSecret(host.LogText, "the log", key, replaced, access, refresh, envKey);
            NoSecret(ToJson(host.Accounts.List()), "the accounts as JSON", key, replaced, access, refresh, envKey);
            var credential = (await host.Secrets.ReadAsync(saved.Account.Id))!;
            NoSecret(ToJson(credential) + credential, "a credential as JSON or text", access, refresh);
            NoSecret(new ApiKeyAccountRequest("openai", key).ToString(), "a request's text", key);
            Check(!host.Accounts.Get(added.Account.Id)!.StatusReason!.Contains("PLANTED", StringComparison.Ordinal), "the expiry reason is scrubbed");
        }),

        Case("credentials.masked-hints-only", async () =>
        {
            Equal("sk-proj-…wxyz", SecretText.Mask("sk-proj-0123456789abcdefwxyz"), "a project key");
            Equal("sk-ant-…1234", SecretText.Mask("sk-ant-api03-00001234"), "a prefix longer than a third falls back to a shorter one");
            Equal("…1234", SecretText.Mask("abcdefgh1234"), "no known prefix");
            Equal(SecretText.Hidden, SecretText.Mask("short-key11"), "below 12 characters, nothing shows");
            Equal("", SecretText.Mask(null), "none");
            Equal("AIza…9xyz", SecretText.Mask("AIzaSyD0123456789abcdefgh9xyz"), "a Google key");
            var key = Planted("sk-proj-");
            Check(!SecretText.Scrub($"401: {{\"error\":\"bad key {key}\"}} for {key}", key).Contains("PLANTED", StringComparison.Ordinal), "Scrub removes the key");
            Equal("token … expired", SecretText.Scrub("token eyJhbGciOiJIUzI1NiJ9abcdefghijkl expired"), "a long token run");
            Check(SecretText.Scrub(new string('a', 10) + new string(' ', 400) + "b").Length <= SecretText.MaximumMessageLength, "short");

            await using var host = new VaultHost("cred-hints", setup => setup.Environment["OPENAI_API_KEY"] = "sk-envkey0123456789abcd");
            var added = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key));
            Equal(SecretText.Mask(key), added.Account.Hint, "the stored hint");
            Equal("sk-…abcd", host.Accounts.Get(CredentialVault.EnvironmentId("openai", "OPENAI_API_KEY"))!.Hint, "an environment key's hint");
            var listed = ToJson(host.Accounts.List());
            NoSecret(listed, "the listing", key, "sk-envkey0123456789abcd");
            Equal(SecretText.Mask(key), JsonNode.Parse(listed)!.AsArray().Single(account => account!["kind"]!.GetValue<string>() == "apiKey")!["hint"]!.GetValue<string>(),
                "the hint is listed");
            Equal(SecretText.Mask(key), ReadDb(host.Options.DatabasePath, "SELECT hint FROM accounts WHERE kind = 'apiKey';", r => r.GetString(0)).Single(), "the hint column");
        }),

        Case("credentials.active-account-per-provider", async () =>
        {
            await using var host = new VaultHost("cred-active", setup => setup.Environment["OPENAI_API_KEY"] = "sk-environment-0000000000");
            var envId = CredentialVault.EnvironmentId("openai", "OPENAI_API_KEY");
            Equal(envId, host.Accounts.Active("openai")?.Id, "an environment variable serves while nothing is stored");
            var changes = new List<string>();
            host.Accounts.Changed += (_, change) => changes.Add($"{change.Change}:{change.AccountId}");

            var first = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")) { Label = "Personal" });
            Check(first.BecameActive, "a first stored key comes before the variable");
            Equal(first.Account.Id, host.Accounts.Active("openai")!.Id, "active");
            var second = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")) { Label = "Work" });
            Check(!second.BecameActive && !second.Account.IsActive, "a second key does not move the active account");
            var anthropic = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", Planted("sk-ant-")));
            Check(anthropic.BecameActive, "each provider has its own");

            await host.Accounts.SetActiveAsync(second.Account.Id);
            Equal(second.Account.Id, host.Accounts.Active("openai")!.Id, "chosen");
            Equal(anthropic.Account.Id, host.Accounts.Active("anthropic")!.Id, "another provider is untouched");
            Equal(1, host.Accounts.List("openai").Count(account => account.IsActive), "one active account per provider");
            Equal(1, ReadDb(host.Options.DatabasePath, "SELECT COUNT(*) FROM active_accounts WHERE provider = 'openai';", r => r.GetInt32(0)).Single(), "one pointer row");

            var removed = await host.Accounts.RemoveAsync(second.Account.Id);
            Equal(first.Account.Id, removed.Active?.Id, "the next stored account takes over");
            Check(!removed.Removed.IsActive, "the removed one is not active");
            await host.Accounts.RemoveAsync(first.Account.Id);
            Equal(envId, host.Accounts.Active("openai")?.Id, "with no stored key left, the variable serves again");
            Equal(0, ReadDb(host.Options.DatabasePath, "SELECT COUNT(*) FROM active_accounts WHERE provider = 'openai';", r => r.GetInt32(0)).Single(),
                "no stale choice is kept");

            // A chosen environment account is kept when a key is added later: nothing moves by itself.
            await host.Accounts.SetActiveAsync(envId);
            var later = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")));
            Check(!later.BecameActive, "the user's choice stands");
            Equal(envId, host.Accounts.Active("openai")!.Id, "still the variable");
            Equal(null, host.Accounts.Active("mistral"), "a provider without accounts");
            Equal(null, host.Accounts.Active(""), "no provider");
            Check(changes.Contains($"Added:{first.Account.Id}") && changes.Contains($"Activated:{second.Account.Id}") && changes.Contains($"Removed:{second.Account.Id}"),
                "change events: " + string.Join(", ", changes));
            await ThrowsAsync<KeyNotFoundException>(() => host.Accounts.SetActiveAsync("acc-missing"), "an unknown account");
        }),

        Case("credentials.environment-accounts-are-read-only", async () =>
        {
            const string value = "sk-env-0123456789abcdef9999";
            await using var host = new VaultHost("cred-environment", setup =>
            {
                setup.Environment["OPENAI_API_KEY"] = value;
                setup.Environment["ANTHROPIC_OAUTH_TOKEN"] = "sk-ant-oat01-should-never-be-an-account";
                setup.Environment["AWS_PROFILE"] = "default";
            });
            var id = CredentialVault.EnvironmentId("openai", "OPENAI_API_KEY");
            var account = host.Accounts.Get(id)!;
            Equal(AccountKind.Environment, account.Kind, "kind");
            Check(account.ReadOnly, "read-only");
            Equal("OPENAI_API_KEY", account.Source, "the variable");
            Equal(value, (await host.Secrets.ReadAsync(id))!.ApiKey, "read from the environment when needed");
            Check(!host.Accounts.List().Any(other => other.Source == CredentialVault.ExcludedVariable), "ANTHROPIC_OAUTH_TOKEN never becomes an account");
            Equal(null, host.Accounts.Active("anthropic"), "so Anthropic has no account");
            Equal(ProviderEnvironmentKeys.AuthenticatedMarker, ProviderEnvironmentKeys.GetEnvironmentApiKey("amazon-bedrock", name => host.Environment.GetValueOrDefault(name)),
                "AWS_PROFILE is ambient AWS credentials");
            var ambient = host.Accounts.Get("ambient-amazon-bedrock")!;
            Equal(AccountKind.Ambient, ambient.Kind, "ambient");
            Check(ambient.ReadOnly && ambient.IsActive && ambient.Hint.Length == 0, "read-only, active, no hint");
            Equal(null, (await host.Secrets.ReadAsync(ambient.Id))!.ApiKey, "no secret: the SDK finds it");

            await ThrowsAsync<InvalidOperationException>(() => host.Accounts.RenameAsync(id, "Mine"), "rename");
            await ThrowsAsync<InvalidOperationException>(() => host.Accounts.RemoveAsync(id), "remove");
            await ThrowsAsync<InvalidOperationException>(() => host.Accounts.ReplaceApiKeyAsync(id, Planted("sk-")), "replace");

            host.Environment.Remove("OPENAI_API_KEY");
            host.Environment.Remove("AWS_PROFILE");
            host.Vault.SyncEnvironment();
            account = host.Accounts.Get(id)!;
            Equal(AccountStatus.Unavailable, account.Status, "an unset variable leaves the account unavailable");
            Check(account.StatusReason!.Contains("OPENAI_API_KEY", StringComparison.Ordinal) && account.Hint.Length == 0, "with a reason and no hint");
            Check(!account.IsActive, "an unavailable account is not active");
            Equal(AccountStatus.Unavailable, host.Accounts.Get("ambient-amazon-bedrock")!.Status, "and the ambient account");
            await ThrowsAsync<CredentialUnavailableException>(() => host.Secrets.ReadAsync(id), "a chat bound to it fails clearly");
            await ThrowsAsync<InvalidOperationException>(() => host.Accounts.SetActiveAsync(id), "it cannot be chosen");
            await host.Accounts.RemoveAsync(id);
            Equal(null, host.Accounts.Get(id), "an unavailable environment account can be removed");
            host.Environment["OPENAI_API_KEY"] = value;
            host.Vault.SyncEnvironment();
            Equal(AccountStatus.Connected, host.Accounts.Get(id)!.Status, "and comes back when the variable does");
        }),

        Case("credentials.validation-never-repeats-the-key", async () =>
        {
            await using var host = new VaultHost("cred-validation");
            var key = Planted("sk-");
            async Task<string> Refused<T>(ApiKeyAccountRequest request, string what) where T : Exception
            {
                var error = await ThrowsAsync<T>(() => host.Accounts.AddApiKeyAsync(request), what);
                NoSecret(error.Message, what, key);
                return error.Message;
            }
            await Refused<ArgumentException>(new ApiKeyAccountRequest("Not A Provider", key), "a bad provider id");
            await Refused<InvalidOperationException>(new ApiKeyAccountRequest("openai-codex", key), "a sign-in-only provider");
            await Refused<InvalidOperationException>(new ApiKeyAccountRequest("faux", key), "the faux provider");
            await Refused<ArgumentException>(new ApiKeyAccountRequest("openai", "  "), "no key");
            await Refused<ArgumentException>(new ApiKeyAccountRequest("openai", key + " " + key), "a key with a space");
            await Refused<ArgumentException>(new ApiKeyAccountRequest("openai", new string('k', CredentialVault.MaximumKeyLength + 1)), "a long key");
            await Refused<ArgumentException>(new ApiKeyAccountRequest("openai", key) { Label = new string('n', CredentialVault.MaximumLabelLength + 1) }, "a long label");
            await Refused<ArgumentException>(new ApiKeyAccountRequest("openai", key) { Settings = new Dictionary<string, string> { ["OPENAI_API_KEY"] = key } }, "a secret-named setting");
            await Refused<ArgumentException>(new ApiKeyAccountRequest("openai", key) { Settings = new Dictionary<string, string> { ["note"] = "x" + key } }, "a setting repeating the key");
            Equal(0, host.Accounts.List().Count, "nothing was stored");
            Check(!File.Exists(host.Options.CredentialsPath) || !JsonNode.Parse(await File.ReadAllTextAsync(host.Options.CredentialsPath))!["secrets"]!.AsObject().Any(),
                "no secret was left behind");

            var one = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest(" openai ", "  " + key + "\t"));
            Equal("OpenAI key", one.Account.Label, "the default label");
            Equal(key, (await host.Secrets.ReadAsync(one.Account.Id))!.ApiKey, "the key is trimmed");
            var two = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")));
            Equal("OpenAI key 2", two.Account.Label, "labels are made unique");
            Equal("Mistral key", (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("mistral", Planted("m")) { Label = "  " })).Account.Label,
                "a blank name is the default name");
            await ThrowsAsync<ArgumentException>(() => host.Accounts.RenameAsync(two.Account.Id, "openai KEY"), "a duplicate name");
            Equal("Team", (await host.Accounts.RenameAsync(two.Account.Id, "  Team ")).Label, "renamed");
            var settingsOnly = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("amazon-bedrock", null)
            { Settings = new Dictionary<string, string> { ["AWS_PROFILE"] = "work", ["AWS_REGION"] = "us-east-1" } });
            Equal("", settingsOnly.Account.Hint, "a settings-only account has no hint");
            Equal("work", (await host.Secrets.ReadAsync(settingsOnly.Account.Id))!.Settings["AWS_PROFILE"], "its settings are read");
            await ThrowsAsync<KeyNotFoundException>(() => host.Accounts.RenameAsync("acc-none", "x"), "an unknown account");
        }),

        Case("credentials.replace-keeps-the-id-and-reconnects", async () =>
        {
            await using var host = new VaultHost("cred-replace");
            var added = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")) { Settings = new Dictionary<string, string> { ["baseUrl"] = "https://a.test/v1" } });
            var expired = await host.Accounts.MarkExpiredAsync(added.Account.Id, "401 from the provider");
            Equal(AccountStatus.Expired, expired.Status, "expired");
            Check(expired.IsActive, "an expired account stays active: nothing moves to another account by itself");
            Equal(AccountStatus.Expired, (await host.Secrets.ReadAsync(added.Account.Id))!.Account.Status, "routing sees the status");
            var key = Planted("sk-proj-");
            var replaced = await host.Accounts.ReplaceApiKeyAsync(added.Account.Id, key);
            Equal(added.Account.Id, replaced.Id, "the same id, so chats bound to it use the new key");
            Equal(AccountStatus.Connected, replaced.Status, "connected again");
            Equal(SecretText.Mask(key), replaced.Hint, "the new hint");
            Equal("https://a.test/v1", replaced.Settings["baseUrl"], "settings kept when none are sent");
            Equal(key, (await host.Secrets.ReadAsync(added.Account.Id))!.ApiKey, "the new key");
            var resettled = await host.Accounts.ReplaceApiKeyAsync(added.Account.Id, null, new Dictionary<string, string> { ["baseUrl"] = "https://b.test/v1" });
            Equal("https://b.test/v1", resettled.Settings["baseUrl"], "settings replaced");
            Equal(key, (await host.Secrets.ReadAsync(added.Account.Id))!.ApiKey, "the key kept when none is sent");
            var saved = await host.Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot("a", "r", 1), "acct-r", new Dictionary<string, string>(), "ChatGPT", null, default);
            await ThrowsAsync<InvalidOperationException>(() => host.Accounts.ReplaceApiKeyAsync(saved.Account.Id, Planted("sk-")), "a sign-in takes no key");
        }),

        Case("credentials.oauth-source-serializes-per-account-and-never-resurrects", async () =>
        {
            await using var host = new VaultHost("cred-oauth-source");
            var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
            async Task<string> Add(string identity) => (await host.Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot("access-" + identity, "refresh-" + identity, expires,
                new Dictionary<string, string> { ["accountId"] = identity }), identity, new Dictionary<string, string>(), "ChatGPT", null, default)).Account.Id;
            var a = await Add("one");
            var b = await Add("two");
            Equal("ChatGPT 2", host.Accounts.Get(b)!.Label, "a second sign-in gets its own name");
            var sourceA = host.Secrets.OAuthSource(a);
            Equal(null, await sourceA.ReadAsync("github-copilot", default), "another provider's name reads nothing");
            Equal("access-one", (await sourceA.ReadAsync("openai-codex", default))!.Access, "its own provider reads the sign-in");

            // Two modifications of one account run one after the other; another account's runs beside them.
            var order = new ConcurrentQueue<string>();
            var gate = new TaskCompletionSource();
            var firstStarted = new TaskCompletionSource();
            var first = sourceA.ModifyAsync("openai-codex", async (current, _) =>
            {
                order.Enqueue("a1-start");
                firstStarted.SetResult();
                await gate.Task;
                order.Enqueue("a1-end");
                return new OAuthCredentialSnapshot("access-a1", current!.Refresh, expires);
            }, default);
            await firstStarted.Task;
            var second = host.Secrets.OAuthSource(a).ModifyAsync("openai-codex", (current, _) =>
            {
                order.Enqueue("a2:" + current!.Access);
                return Task.FromResult<OAuthCredentialSnapshot?>(new OAuthCredentialSnapshot("access-a2", current.Refresh, expires));
            }, default);
            var other = await host.Secrets.OAuthSource(b).ModifyAsync("openai-codex", (current, _) =>
            {
                order.Enqueue("b");
                return Task.FromResult<OAuthCredentialSnapshot?>(null);
            }, default).WaitAsync(TimeSpan.FromSeconds(5));
            Equal("access-two", other!.Access, "a null result keeps the stored sign-in");
            await Task.Delay(100);
            Check(!second.IsCompleted, "the second modification of the same account waits");
            gate.SetResult();
            await Task.WhenAll(first, second);
            Equal("a1-start,b,a1-end,a2:access-a1", string.Join(",", order), "serialized per account, re-read inside the lock");
            Equal("access-a2", (await host.Secrets.ReadAsync(a))!.OAuth!.Access, "the last write is stored");

            // A removal waits for a modification in flight, and nothing is written back afterwards.
            var hold = new TaskCompletionSource();
            var holding = new TaskCompletionSource();
            var inFlight = host.Secrets.OAuthSource(a).ModifyAsync("openai-codex", async (current, _) =>
            {
                holding.SetResult();
                await hold.Task;
                return new OAuthCredentialSnapshot("access-late", current!.Refresh, expires);
            }, default);
            await holding.Task;
            var removal = host.Accounts.RemoveAsync(a);
            await Task.Delay(100);
            Check(!removal.IsCompleted, "the removal waits for the refresh in flight");
            hold.SetResult();
            await inFlight;
            await removal;
            Equal(null, host.Accounts.Get(a), "removed");
            Equal(null, await host.Secrets.ReadAsync(a), "no credential");
            Check(!JsonNode.Parse(await File.ReadAllTextAsync(host.Options.CredentialsPath))!["secrets"]!.AsObject().ContainsKey(a), "its secret is gone");
            await ThrowsAsync<CredentialUnavailableException>(() => host.Secrets.OAuthSource(a).ModifyAsync("openai-codex",
                (_, _) => Task.FromResult<OAuthCredentialSnapshot?>(new OAuthCredentialSnapshot("x", "y", expires)), default), "a refresh after removal");
            Check(!JsonNode.Parse(await File.ReadAllTextAsync(host.Options.CredentialsPath))!["secrets"]!.AsObject().ContainsKey(a), "nothing was resurrected");
            Equal(null, host.Accounts.Get(a), "nor the row");

            // Through the PiSharp.AI lifecycle: a sign-in due for a refresh is refreshed and stored, and an expired account connects again.
            var soon = await host.Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot("access-old", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                new Dictionary<string, string> { ["accountId"] = "three" }), "three", new Dictionary<string, string>(), "ChatGPT", null, default);
            await host.Accounts.MarkExpiredAsync(soon.Account.Id, "401");
            var refreshed = await new StoredOAuthLifecycle(host.Secrets.OAuthSource(soon.Account.Id), new FakeRefresh()).ResolveAsync("openai-codex");
            Equal("access-new", refreshed!.Access, "refreshed");
            Equal("access-new", (await host.Secrets.ReadAsync(soon.Account.Id))!.OAuth!.Access, "stored");
            Equal(AccountStatus.Connected, host.Accounts.Get(soon.Account.Id)!.Status, "connected again");
            Check(host.Accounts.Get(soon.Account.Id)!.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30), "the new expiry is listed");
        }),

        Case("credentials.orphans-are-pruned-and-an-unreadable-file-is-left-alone", async () =>
        {
            var root = Temp("cred-prune");
            try
            {
                string kept;
                await using (var host = new VaultHost("unused", root: root))
                {
                    kept = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")))).Account.Id;
                    // A crash between the secret and its row leaves an orphan secret.
                    new CredentialFile(host.Options.CredentialsPath, host.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System).Write("acc-orphan", SecretKind.ApiKey, Planted("sk-"));
                }
                await using (var host = new VaultHost("unused", root: root))
                {
                    Equal(1, host.Accounts.List().Count, "the orphan never lists");
                    var secrets = JsonNode.Parse(await File.ReadAllTextAsync(host.Options.CredentialsPath))!["secrets"]!.AsObject();
                    Check(secrets.ContainsKey(kept) && !secrets.ContainsKey("acc-orphan"), "the orphan was pruned at start");
                    Check(host.LogText.Contains("orphaned", StringComparison.Ordinal), "and it was logged");
                }
                var path = Path.Join(root, "data", "credentials.json");
                await File.WriteAllTextAsync(path, "{ this is not json");
                var before = await File.ReadAllBytesAsync(path);
                await using (var host = new VaultHost("unused", root: root))
                {
                    Equal(1, host.Accounts.List().Count, "accounts still list");
                    var error = await ThrowsAsync<CredentialUnavailableException>(() => host.Secrets.ReadAsync(kept), "reading a secret");
                    Check(error.Message.Contains("credentials.json", StringComparison.Ordinal), "says why");
                    await ThrowsAsync<InvalidDataException>(() => host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-"))), "storing one");
                    Equal(1, host.Accounts.List().Count, "no row was added");
                }
                var after = await File.ReadAllBytesAsync(path);
                Check(before.SequenceEqual(after), "the unreadable file was not overwritten");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];

    /// <summary>A refresh that returns a new sign-in without any request.</summary>
    private sealed class FakeRefresh : IAdmittedOAuthRefresh
    {
        public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthCredentialSnapshot("access-new", "refresh-new", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), current.ProviderData));
    }
}
