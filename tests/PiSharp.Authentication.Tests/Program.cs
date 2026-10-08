using System.Text.Json;
using PiSharp.AI.Authentication;

// Authored synthetic controls. No ambient environment, credential store, provider or native process use.
internal static class Program
{
    private const string Secret = "AUTHORED_ONLY_SECRET_MARKER";
    private static ProviderEnvironmentSnapshot Env(params (string Name, string? Value)[] values) =>
        new(values.Select(value => KeyValuePair.Create(value.Name, value.Value)));
    private static ValueTask<StoredApiKeyCredential?> NoCredential(string _, CancellationToken __) => ValueTask.FromResult<StoredApiKeyCredential?>(null);
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Authored assertion failed."); }
    private static ResolvedAuthentication Auth(AuthenticationResolution result) { Require(result.Diagnostic == AuthenticationDiagnostic.Resolved); return result.Authentication ?? throw new InvalidOperationException("Missing resolved authentication."); }
    private sealed class Lookup(Func<string, CancellationToken, ValueTask<string?>> read) : IInjectedEnvironmentLookup
    {
        public List<string> Names { get; } = [];
        public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken) { Names.Add(name); return read(name, cancellationToken); }
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report"))
            throw new ArgumentException("Expected optional --report and fresh report path.");
        var results = new List<object>(); var failures = 0;
        async Task Check(string id, Func<Task> action)
        {
            try { await action(); results.Add(new { id, status = "passed" }); }
            catch { failures++; results.Add(new { id, status = "failed", diagnostic = "Authored assertion or unexpected failure; details withheld" }); }
        }
        await OAuthLifecycleTests.RunAsync(Check);
        await Check("all-original-environment-mappings", () =>
        {
            (string Provider, string Name)[] mappings = [
                ("github-copilot", "COPILOT_GITHUB_TOKEN"), ("ant-ling", "ANT_LING_API_KEY"),
                ("qwen-token-plan", "QWEN_TOKEN_PLAN_API_KEY"), ("qwen-token-plan-cn", "QWEN_TOKEN_PLAN_CN_API_KEY"), ("qwen-token-plan-individual", "QWEN_TOKEN_PLAN_API_KEY"),
                ("openai", "OPENAI_API_KEY"), ("azure", "AZURE_OPENAI_API_KEY"), ("nvidia", "NVIDIA_API_KEY"), ("deepseek", "DEEPSEEK_API_KEY"),
                ("google", "GEMINI_API_KEY"), ("google-vertex", "GOOGLE_CLOUD_API_KEY"), ("groq", "GROQ_API_KEY"), ("cerebras", "CEREBRAS_API_KEY"),
                ("xai", "XAI_API_KEY"), ("typesafe", "TYPESAFE_API_KEY"), ("radius", "RADIUS_API_KEY"), ("openrouter", "OPENROUTER_API_KEY"),
                ("vercel-ai-gateway", "AI_GATEWAY_API_KEY"), ("zai", "ZAI_API_KEY"), ("zai-coding-cn", "ZAI_CODING_CN_API_KEY"), ("mistral", "MISTRAL_API_KEY"),
                ("minimax", "MINIMAX_API_KEY"), ("minimax-cn", "MINIMAX_CN_API_KEY"), ("moonshotai", "MOONSHOT_API_KEY"), ("moonshotai-cn", "MOONSHOT_API_KEY"),
                ("huggingface", "HF_TOKEN"), ("fireworks", "FIREWORKS_API_KEY"), ("together", "TOGETHER_API_KEY"), ("baseten", "BASETEN_API_KEY"),
                ("opencode", "OPENCODE_API_KEY"), ("opencode-go", "OPENCODE_API_KEY"), ("kimi-coding", "KIMI_API_KEY"), ("meta", "META_API_KEY"),
                ("cloudflare-workers-ai", "CLOUDFLARE_API_KEY"), ("cloudflare-ai-gateway", "CLOUDFLARE_API_KEY"), ("xiaomi", "XIAOMI_API_KEY"),
                ("xiaomi-token-plan-cn", "XIAOMI_TOKEN_PLAN_CN_API_KEY"), ("xiaomi-token-plan-ams", "XIAOMI_TOKEN_PLAN_AMS_API_KEY"), ("xiaomi-token-plan-sgp", "XIAOMI_TOKEN_PLAN_SGP_API_KEY"),
            ];
            foreach (var (provider, name) in mappings)
            {
                var env = Env((name, Secret), ("UNRELATED", "ignored"));
                Require(InjectedAuthenticationResolver.FindEnvKeys(provider, env).SequenceEqual([name]));
                var auth = Auth(InjectedAuthenticationResolver.GetEnvApiKey(provider, env));
                Require(auth.EnvironmentName == name && auth.Secret == Secret && auth.Kind == AuthenticationKind.ApiKey);
            }
            return Task.CompletedTask;
        });
        await Check("scoped-truthy-override-empty-fallback-and-case", () =>
        {
            foreach (var (scoped, expected) in new (string?, string)[] { (Secret, Secret), ("", "process-marker"), (null, "process-marker"), (" ", " ") })
            {
                var env = new ProviderEnvironmentSnapshot(new Dictionary<string, string?> { ["OPENAI_API_KEY"] = scoped }, new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "process-marker" });
                Require(Auth(InjectedAuthenticationResolver.GetEnvApiKey("openai", env)).Secret == expected);
                Require(env.GetValue("openai_api_key") is null);
            }
            return Task.CompletedTask;
        });
        await Check("snapshot-copies-inputs", () =>
        {
            var input = new Dictionary<string, string?> { ["OPENAI_API_KEY"] = Secret };
            var env = new ProviderEnvironmentSnapshot(input); input["OPENAI_API_KEY"] = "mutation";
            Require(env.GetValue("OPENAI_API_KEY") == Secret); return Task.CompletedTask;
        });
        await Check("unknown-missing-and-ambient-exclusion", () =>
        {
            var env = Env(("AWS_PROFILE", Secret), ("GOOGLE_APPLICATION_CREDENTIALS", Secret), ("GOOGLE_CLOUD_PROJECT", "project"), ("GOOGLE_CLOUD_LOCATION", "location"));
            foreach (var provider in new string?[] { null, "", "OPENAI", Secret }) Require(InjectedAuthenticationResolver.GetEnvApiKey(provider, env).Diagnostic == AuthenticationDiagnostic.UnknownProvider);
            Require(InjectedAuthenticationResolver.GetEnvApiKey("openai", env).Diagnostic == AuthenticationDiagnostic.Missing);
            foreach (var provider in new[] { "google-vertex", "amazon-bedrock" }) Require(InjectedAuthenticationResolver.GetEnvApiKey(provider, env).Diagnostic == AuthenticationDiagnostic.AmbientAuthenticationUnsupported);
            Require(InjectedAuthenticationResolver.FindEnvKeys("amazon-bedrock", env).IsEmpty); return Task.CompletedTask;
        });
        await Check("anthropic-discovery-order-and-api-key-path-skips-auth-token", () =>
        {
            var env = Env(("ANTHROPIC_AUTH_TOKEN", "bearer-marker"), ("ANTHROPIC_OAUTH_TOKEN", "oauth-marker"), ("ANTHROPIC_API_KEY", "key-marker"));
            Require(InjectedAuthenticationResolver.FindEnvKeys("anthropic", env).SequenceEqual(["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"]));
            var auth = Auth(InjectedAuthenticationResolver.GetEnvApiKey("anthropic", env));
            Require(auth.Kind == AuthenticationKind.ApiKey && auth.Origin == AuthenticationOrigin.AnthropicOAuthEnvironmentToken && auth.Secret == "oauth-marker" && auth.GetAuthorizationHeader() is null);
            Require(InjectedAuthenticationResolver.GetEnvApiKey("anthropic", Env(("ANTHROPIC_AUTH_TOKEN", Secret))).Diagnostic == AuthenticationDiagnostic.Missing);
            Require(Auth(InjectedAuthenticationResolver.GetEnvApiKey("anthropic", Env(("ANTHROPIC_API_KEY", Secret)))).EnvironmentName == "ANTHROPIC_API_KEY");
            return Task.CompletedTask;
        });
        await Check("anthropic-stored-key-wins-without-environment-lookup", async () =>
        {
            var env = new Lookup((_, _) => throw new InvalidOperationException("Unexpected lookup"));
            var credentialEnv = Env(("AUTHORED_CONFIGURATION", Secret)); var calls = 0;
            var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(env, (provider, _) =>
            { Require(provider == "anthropic"); calls++; return ValueTask.FromResult<StoredApiKeyCredential?>(new(Secret, credentialEnv)); });
            var auth = Auth(result); Require(calls == 1 && env.Names.Count == 0 && auth.Origin == AuthenticationOrigin.StoredApiKey && auth.Secret == Secret && ReferenceEquals(auth.CredentialEnvironment, credentialEnv));
        });
        await Check("anthropic-auth-token-is-bearer-and-wins-environment-order", async () =>
        {
            var env = Env(("ANTHROPIC_AUTH_TOKEN", Secret), ("ANTHROPIC_OAUTH_TOKEN", "oauth-marker"), ("ANTHROPIC_API_KEY", "key-marker"));
            var auth = Auth(await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(env, NoCredential));
            Require(auth.Kind == AuthenticationKind.BearerToken && auth.GetAuthorizationHeader() == "Bearer " + Secret && auth.Origin == AuthenticationOrigin.AnthropicAuthToken);
        });
        await Check("anthropic-empty-stored-key-oauth-token-and-api-key-fallback", async () =>
        {
            foreach (var (name, origin) in new[] { ("ANTHROPIC_OAUTH_TOKEN", AuthenticationOrigin.AnthropicOAuthEnvironmentToken), ("ANTHROPIC_API_KEY", AuthenticationOrigin.EnvironmentApiKey) })
            {
                var auth = Auth(await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env((name, Secret)), (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(new(""))));
                Require(auth.Kind == AuthenticationKind.ApiKey && auth.GetAuthorizationHeader() is null && auth.Origin == origin && auth.Secret == Secret);
            }
            Require((await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), NoCredential)).Diagnostic == AuthenticationDiagnostic.Missing);
        });
        await Check("awaited-environment-order-stops-at-first-truthy-value", async () =>
        {
            var snapshot = Env(("ANTHROPIC_API_KEY", Secret));
            var env = new Lookup(async (name, token) => { await Task.Yield(); return await snapshot.ReadAsync(name, token); });
            var auth = Auth(await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(env, NoCredential));
            Require(auth.Secret == Secret && env.Names.SequenceEqual(["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"]));
        });
        await Check("pre-cancel-never-enters-injected-storage", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var calls = 0;
            try { await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), (_, _) => { calls++; return ValueTask.FromResult<StoredApiKeyCredential?>(null); }, cancellation.Token); Require(false); }
            catch (OperationCanceledException) { Require(calls == 0); }
        });
        await Check("cancel-after-awaited-storage-before-env-or-stored-result", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var env = new Lookup((_, _) => throw new InvalidOperationException("Unexpected lookup"));
            try
            {
                await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(env, async (_, _) => { await Task.Yield(); cancellation.Cancel(); return new(Secret); }, cancellation.Token);
                Require(false);
            }
            catch (OperationCanceledException error) { Require(env.Names.Count == 0 && error.CancellationToken == cancellation.Token && !error.ToString().Contains(Secret, StringComparison.Ordinal)); }
        });
        await Check("cancel-between-awaited-environment-lookups", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var env = new Lookup(async (name, _) => { await Task.Yield(); Require(name == "ANTHROPIC_AUTH_TOKEN"); cancellation.Cancel(); return null; });
            try { await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(env, NoCredential, cancellation.Token); Require(false); }
            catch (OperationCanceledException) { Require(env.Names.SequenceEqual(["ANTHROPIC_AUTH_TOKEN"])); }
        });
        await Check("lookup-failures-and-cancellation-diagnostics-omit-injected-secret", async () =>
        {
            var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), (_, _) => throw new InvalidOperationException(Secret));
            Require(result.Diagnostic == AuthenticationDiagnostic.InjectedLookupFailed && !JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
            var env = new Lookup((_, _) => throw new OperationCanceledException(Secret));
            var foreignCancellation = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(env, NoCredential);
            Require(foreignCancellation.Diagnostic == AuthenticationDiagnostic.InjectedLookupFailed && !JsonSerializer.Serialize(foreignCancellation).Contains(Secret, StringComparison.Ordinal));
        });
        await Check("foreign-cancelled-and-default-lookup-tokens-with-live-caller", async () =>
        {
            using var caller = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
            foreach (var token in new[] { foreign.Token, CancellationToken.None })
            {
                var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), (_, _) => throw new OperationCanceledException(Secret, token), caller.Token);
                Require(!caller.IsCancellationRequested && result.Diagnostic == AuthenticationDiagnostic.InjectedLookupFailed && !JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
            }
        });
        await Check("same-live-caller-token-is-not-proven-cancellation", async () =>
        {
            using var caller = new CancellationTokenSource();
            var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), (_, _) => throw new OperationCanceledException(Secret, caller.Token), caller.Token);
            Require(!caller.IsCancellationRequested && result.Diagnostic == AuthenticationDiagnostic.InjectedLookupFailed);
        });
        await Check("late-caller-cancellation-preserves-foreign-and-default-rejection", async () =>
        {
            using var foreign = new CancellationTokenSource(); foreign.Cancel();
            foreach (var token in new[] { foreign.Token, CancellationToken.None })
            {
                using var caller = new CancellationTokenSource();
                var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), async (_, _) =>
                { await Task.Yield(); caller.Cancel(); throw new OperationCanceledException(Secret, token); }, caller.Token);
                Require(caller.IsCancellationRequested && result.Diagnostic == AuthenticationDiagnostic.InjectedLookupFailed && !JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
            }
        });
        await Check("late-caller-cancellation-preserves-noncancellation-rejection", async () =>
        {
            using var caller = new CancellationTokenSource();
            var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), async (_, _) =>
            { await Task.Yield(); caller.Cancel(); throw new InvalidOperationException(Secret); }, caller.Token);
            Require(caller.IsCancellationRequested && result.Diagnostic == AuthenticationDiagnostic.InjectedLookupFailed && !JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
        });
        await Check("matching-requested-caller-token-is-sanitized-cancellation", async () =>
        {
            using var caller = new CancellationTokenSource();
            try
            {
                await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(), async (_, _) =>
                { await Task.Yield(); caller.Cancel(); throw new OperationCanceledException(Secret, caller.Token); }, caller.Token);
                Require(false);
            }
            catch (OperationCanceledException error)
            {
                Require(caller.IsCancellationRequested && error.CancellationToken == caller.Token && error.InnerException is null && !error.ToString().Contains(Secret, StringComparison.Ordinal));
            }
        });
        await Check("environment-rejection-precedes-late-caller-cancellation", async () =>
        {
            foreach (var injected in new Exception[] { new OperationCanceledException(Secret), new InvalidOperationException(Secret) })
            {
                using var caller = new CancellationTokenSource();
                var env = new Lookup(async (_, _) => { await Task.Yield(); caller.Cancel(); throw injected; });
                var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(env, NoCredential, caller.Token);
                Require(caller.IsCancellationRequested && env.Names.SequenceEqual(["ANTHROPIC_AUTH_TOKEN"]) && result.Diagnostic == AuthenticationDiagnostic.InjectedLookupFailed && !JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
            }
        });
        await Check("default-auth-diagnostics-and-json-omit-credential-material", async () =>
        {
            var result = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(Env(("ANTHROPIC_AUTH_TOKEN", Secret)), NoCredential);
            foreach (var text in new[] { result.ToString(), Auth(result).ToString(), JsonSerializer.Serialize(result), JsonSerializer.Serialize(new StoredApiKeyCredential(Secret)), Env(("OPENAI_API_KEY", Secret)).ToString() })
                Require(!text.Contains(Secret, StringComparison.Ordinal));
        });
        var json = JsonSerializer.Serialize(new { scope = "Authored injected controls; no original/native execution evidence", tests = results.Count, passed = results.Count - failures, failed = failures, results });
        Console.WriteLine(json);
        if (args.Length == 2)
        {
            await using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(output);
            await writer.WriteLineAsync(json);
        }
        return failures == 0 ? 0 : 1;
    }
}
