using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;

internal static partial class Program
{
    /// <summary>providers/data/.manifest.json of @earendil-works/pi-ai@1.1.0, verbatim (its SHA256 is checked below).</summary>
    private const string Manifest = """{"schemaVersion":6,"generatedAt":"2026-10-07T22:01:28.515Z","structureHash":"080cfcf6bdd13064ce2362f26e4902c31503b675fd06a8c4685c04a0333bc465","files":{"amazon-bedrock.json":"d9381ff9bf5c9d8c03b2909665526d243dfc33600e73beec4d58df18dfd0addd","ant-ling.json":"628feab3ef6c7d80ce96f75999804e1d9f05fec3851e5302e30fff3b69db90f6","anthropic.json":"aa4342dfb96feb1619794113619d6630088d6ac544c547a4f9899a0a7f26419b","azure.json":"46c4e25b84463c6475e706c581196d2735c35feb9612d2efca0ab932c917d727","baseten.json":"b04d1423d6958bec857ffccc36726f091ec1a8f3a09cf0836b64cc94b0ee697d","cerebras.json":"3eccb58c5de3d0dc7ba49dc6e477ba29f00cb3150ce0a35cc90d59e6b5ab368e","cloudflare-ai-gateway.json":"e76699154fe8a933c504c5f64f6cb5e2bca572efd21edd0d429e51c6c234ebbc","cloudflare-workers-ai.json":"d04336a61cacfd0cadbc2f3d30cd3b552e3d153e5a320658a31ba1bab8f01c83","deepseek.json":"10a296fb3e898f7715c80890c8af0af4f5fd58f22dbcd6612fdc5d762157ccdc","fireworks.json":"8db3c11f65a5d76260d5040a5dc5a9ef0f35b92de8505a30907c2355c2837b36","github-copilot.json":"8119aa175666d70136471aa7cae08043910a97d9cbede8b367d0b2c20559049e","google-vertex.json":"40090c680538bc867f2624d986a449f361be4dfa6ba96d7a27402050f036e009","google.json":"64bdd2563cee0e3598aec4d93a6735c189b6d7d14b9c03f061cc2824f3730528","groq.json":"910952ad9dad07856983459d63fce0148dba1203edd7b10d801a77c5824f3405","huggingface.json":"1b8604bd31a5e05fe43a40ec1ec6dfc1a51a28b0b020151ef057fab43f57c923","kimi-coding.json":"582250b920454d98940da3e7e99d30b807edbea28f2eb8ac15f658d2ef41e8fc","meta.json":"0c7e9a370a7005c5a89dc94f0f93bc67b8935c3f802de324f11af6ff56fff429","minimax-cn.json":"1bbfbd11c3edbd141841d3cef027e12157218f1aec92477e15772116c1fabe7a","minimax.json":"a4b6864bd97eddf5749b2e117f643e751ec237408e8e24bec9e2f947782fa244","mistral.json":"fcd37c7b178416f86954efdacbb45726d10f102fd1211062792691e62fcf327c","moonshotai-cn.json":"79c9585dc84ee525ebaebbc78b3c55f3d369ec884d4f943488f1e0b14e3587f5","moonshotai.json":"1da2c4e34f22aef17a07c974d85bdee8e1b68b6dc712e3769c935b3f1e7dadca","nvidia.json":"a75e963dc81779e3eec01c051b12277c15ec37c5b94ab51ece9e9ccb3b16407c","openai-codex.json":"3cc35a7e122e5c77ee8ec30a9a3bd8bd59629997e85ba2cb3f9e0ed62e9a09c0","openai.json":"f4c1ac9f8f84cb9f2a952b0ceec51c90a38b31b4cdf33200e018ece9d408e95f","opencode-go.json":"4a10d85457c0d25d4fd8b4089cb15c0f54f086b9a6b304d7bd8e92397a3d7d0b","opencode.json":"a6a76926c51c2c410422dec612fdbe903b63191ff7334242e4f6ca3758bc0d2f","openrouter.json":"c86aa3b95d412465dac54cb902402cbdb40f47a1fe33f12b002913834724d8f0","qwen-token-plan-cn.json":"2469a4091c474ec3db0a49a9962d8d2726c074ad9065b96bc4e0c87861c4c6c5","qwen-token-plan-individual.json":"0fb7cf64faf39d1a3b0a884210a86c87084b805eda15644f5f39293287b959dd","qwen-token-plan.json":"eff10620cb577142de4ec02698d5961a71fda2fe4f6ae7ea9432bad9d6844176","radius.json":"88a3f060a851882df89015bcd346e03238b4b0725bd72c8ad501fcffff782768","together.json":"52293df62191c8e799ae3632f8ab1b3f52ee626aee0280677086627b32864bf7","typesafe.json":"a4429a2f696d5cc5273969af61a7310bda07d6bdcd27bf9ac9c76c534cc43716","vercel-ai-gateway.json":"0ec37357cc2c3eec0b71cbff98910bfd0988e16b89178d9c930579f1ce747c30","xai.json":"879c49b78b663a52a220ac4ee013e726b10c4f50aaa03bc58ee53a4b54c76928","xiaomi-token-plan-ams.json":"ca65178f4525d9705bc8d085ed136cecbe149c4080e857ea6a64ae643d2ac00a","xiaomi-token-plan-cn.json":"b065326926681c2262084e59c1c19ebdaf146c07107b5242c76b456becf74db0","xiaomi-token-plan-sgp.json":"92c3d6d707f81fd9cd2b6d1fb57dc431d90ba51e10fe49d97bc8806035c23492","xiaomi.json":"b81d01a374f7b558728c78d81f4168efa9c8996e6e2b5618409a8094b083c759","zai-coding-cn.json":"ee5ee705fb6e413a6088db5e3616c27fce802a8ece6c355f1db35490d787f610","zai.json":"58e6642fdfb736f0b9a4a277c760c536a3cea66a030abbfb2615f7b2851d5c6f"}}""";
    private const string ManifestSha256 = "fb3b3b6b4fd9dcfac1f77058520bffb5fab4f15e15f97307b8afe2cd3f9dfae1";
    private const string TarballSha256 = "6caab33cec57480ed02c57fe37428a030a77cc2a0662814b435a5cf8932ad829";

    private static IEnumerable<(string, Func<Task>)> CatalogCases() =>
    [
        ("catalog.manifest-copy-is-the-package-manifest", Sync(ManifestCopy)),
        ("catalog.every-shard-embedded-with-its-manifest-hash-and-parses", Sync(EveryShard)),
        ("catalog.shards-equal-the-package-tarball-bytes", ShardsEqualTarball),
        ("catalog.builtin-provider-table-matches-all-ts-and-shards", Sync(ProviderTable)),
        ("catalog.generated-at-matches-the-manifest", Sync(GeneratedAt)),
        ("env.api-key-map-and-ambient-credentials", Sync(EnvironmentKeys)),
        ("env.builtin-auth-checks", Sync(AuthChecks)),
    ];

    private static void ManifestCopy() =>
        Equal(ManifestSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Manifest + "\n"))), "manifest SHA256");

    private static Dictionary<string, string> ManifestFiles()
    {
        using var document = JsonDocument.Parse(Manifest);
        return document.RootElement.GetProperty("files").EnumerateObject().ToDictionary(file => file.Name[..^".json".Length], file => file.Value.GetString()!);
    }

    private static void EveryShard()
    {
        var files = ManifestFiles();
        Equal(42, files.Count, "manifest shards");
        Names(files.Keys.Order(StringComparer.Ordinal), BuiltinModelCatalog.ShardHashes.Keys.Order(StringComparer.Ordinal), "embedded shard set");
        var models = 0;
        foreach (var (provider, hash) in files)
        {
            Equal(hash, BuiltinModelCatalog.ShardHashes[provider], provider + " recorded hash");
            Equal(hash, Convert.ToHexStringLower(SHA256.HashData(BuiltinModelCatalog.ReadShard(provider))), provider + " embedded bytes");
            var catalog = BuiltinModelCatalog.Get(provider);
            Equal(hash, catalog.ProviderJsonSha256, provider + " parsed hash");
            models += catalog.Models.Length;
        }
        Equal(1650, models, "catalog entries across all shards");
    }

    private static async Task ShardsEqualTarball()
    {
        if (Tarball is null) return; // Verified when --tarball is given (the published report records tarballVerified).
        var bytes = await File.ReadAllBytesAsync(Tarball);
        Equal(TarballSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), "tarball SHA256");
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            const string prefix = "package/dist/providers/data/";
            if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal) || !entry.Name.EndsWith(".json", StringComparison.Ordinal) ||
                entry.Name.EndsWith("/.manifest.json", StringComparison.Ordinal) || entry.DataStream is null) continue;
            using var buffer = new MemoryStream(); await entry.DataStream.CopyToAsync(buffer);
            var provider = entry.Name[prefix.Length..^".json".Length];
            Check(BuiltinModelCatalog.ReadShard(provider).AsSpan().SequenceEqual(buffer.ToArray()), provider + " differs from the tarball");
            seen.Add(provider);
        }
        Equal(42, seen.Count, "tarball shards compared");
    }

    private static void ProviderTable()
    {
        Names(["amazon-bedrock", "ant-ling", "anthropic", "azure", "baseten", "cerebras", "cloudflare-ai-gateway", "cloudflare-workers-ai", "deepseek",
            "fireworks", "github-copilot", "google", "google-vertex", "groq", "huggingface", "kimi-coding", "meta", "minimax", "minimax-cn", "mistral",
            "moonshotai", "moonshotai-cn", "nvidia", "openai", "openai-codex", "opencode", "opencode-go", "openrouter", "qwen-token-plan",
            "qwen-token-plan-cn", "qwen-token-plan-individual", "radius", "together", "typesafe", "vercel-ai-gateway", "xai", "xiaomi",
            "xiaomi-token-plan-ams", "xiaomi-token-plan-cn", "xiaomi-token-plan-sgp", "zai", "zai-coding-cn"],
            BuiltinProviders.All.Select(provider => provider.Id), "builtinProviders() order");
        foreach (var provider in BuiltinProviders.All)
        {
            var catalog = BuiltinModelCatalog.Get(provider.Id);
            foreach (var api in catalog.DeclaredApis) Check(provider.Apis.Contains(api), $"{provider.Id} lacks API {api}");
            if (provider.BaseUrl is { } baseUrl && provider.Id is not ("fireworks" or "openrouter" or "vercel-ai-gateway"))
                Check(catalog.Models.All(model => model.BaseUrl == baseUrl), provider.Id + " base URL");
        }
        Registry(out var registry);
        Equal("Z.AI Coding CN", registry.GetProviderDisplayName("zai-coding-cn"), "display name");
        Equal("not-a-provider", registry.GetProviderDisplayName("not-a-provider"), "fallback display name");
    }

    private static void GeneratedAt() =>
        Equal(DateTimeOffset.Parse("2026-10-07T22:01:28.515Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            BuiltinModelCatalog.GeneratedAtUnixMilliseconds, "generatedAt");

    private static void EnvironmentKeys()
    {
        Names(["COPILOT_GITHUB_TOKEN"], ProviderEnvironmentKeys.GetApiKeyVariables("github-copilot")!, "copilot");
        Names(["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"], ProviderEnvironmentKeys.GetApiKeyVariables("anthropic")!, "anthropic");
        Names(["QWEN_TOKEN_PLAN_API_KEY"], ProviderEnvironmentKeys.GetApiKeyVariables("qwen-token-plan-individual")!, "qwen individual");
        Names(["HF_TOKEN"], ProviderEnvironmentKeys.GetApiKeyVariables("huggingface")!, "huggingface");
        Names(["MOONSHOT_API_KEY"], ProviderEnvironmentKeys.GetApiKeyVariables("moonshotai-cn")!, "moonshot cn");
        Names(["CLOUDFLARE_API_KEY"], ProviderEnvironmentKeys.GetApiKeyVariables("cloudflare-ai-gateway")!, "cloudflare gateway");
        Check(ProviderEnvironmentKeys.GetApiKeyVariables("amazon-bedrock") is null, "bedrock has no API key variable");
        Check(ProviderEnvironmentKeys.GetApiKeyVariables("openai-codex") is null, "codex has no API key variable");
        // Every built-in provider with an envApiKeyAuth uses exactly the env-api-keys.ts variable.
        foreach (var provider in BuiltinProviders.All.Where(provider => provider.Auth == ProviderAuthKind.EnvironmentKey))
            Names(provider.ApiKeyVariables, ProviderEnvironmentKeys.GetApiKeyVariables(provider.Id)!, provider.Id + " variables");
        Check(ProviderEnvironmentKeys.FindEnvironmentKeys("openai", Env(("OPENAI_API_KEY", ""))) is null, "empty is absent");
        Names(["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY"], ProviderEnvironmentKeys.FindEnvironmentKeys("anthropic",
            Env(("ANTHROPIC_AUTH_TOKEN", "t"), ("ANTHROPIC_API_KEY", "k")))!, "found anthropic keys");
        Equal("k", ProviderEnvironmentKeys.GetEnvironmentApiKey("anthropic", Env(("ANTHROPIC_AUTH_TOKEN", "t"), ("ANTHROPIC_API_KEY", "k"))), "auth token skipped");
        Equal<string?>(null, ProviderEnvironmentKeys.GetEnvironmentApiKey("anthropic", Env(("ANTHROPIC_AUTH_TOKEN", "t"))), "auth token alone");
        Equal("<authenticated>", ProviderEnvironmentKeys.GetEnvironmentApiKey("amazon-bedrock", Env(("AWS_PROFILE", "dev"))), "bedrock profile");
        Equal<string?>(null, ProviderEnvironmentKeys.GetEnvironmentApiKey("amazon-bedrock", Env(("AWS_ACCESS_KEY_ID", "a"))), "bedrock half keys");
        var adc = Env(("GOOGLE_CLOUD_PROJECT", "p"), ("GOOGLE_CLOUD_LOCATION", "us-central1"));
        Equal("<authenticated>", ProviderEnvironmentKeys.GetEnvironmentApiKey("google-vertex", adc, path =>
            path.Replace('\\', '/').EndsWith("/home/.config/gcloud/application_default_credentials.json", StringComparison.Ordinal), "/home"), "vertex ADC");
        Equal<string?>(null, ProviderEnvironmentKeys.GetEnvironmentApiKey("google-vertex", adc, _ => false, "/home"), "vertex without ADC file");
        Equal("vk", ProviderEnvironmentKeys.GetEnvironmentApiKey("google-vertex", Env(("GOOGLE_CLOUD_API_KEY", "vk")), _ => false, "/home"), "vertex key");
    }

    private static void AuthChecks()
    {
        BuiltinProviders.TryGet("deepseek", out var deepseek); BuiltinProviders.TryGet("anthropic", out var anthropic);
        BuiltinProviders.TryGet("openai-codex", out var codex); BuiltinProviders.TryGet("cloudflare-ai-gateway", out var gateway);
        BuiltinProviders.TryGet("amazon-bedrock", out var bedrock);
        Equal("DEEPSEEK_API_KEY", BuiltinProviders.CheckAuth(deepseek, null, Env(("DEEPSEEK_API_KEY", "k"))), "env key");
        Equal("stored credential", BuiltinProviders.CheckAuth(deepseek, new("api_key", "s"), Env()), "stored key");
        Equal("DEEPSEEK_API_KEY", BuiltinProviders.CheckAuth(deepseek, new("api_key"), Env(("DEEPSEEK_API_KEY", "k"))), "keyless stored falls back");
        Equal<string?>(null, BuiltinProviders.CheckAuth(deepseek, new("oauth"), Env(("DEEPSEEK_API_KEY", "k"))), "OAuth without an OAuth method");
        Equal("OAuth", BuiltinProviders.CheckAuth(anthropic, new("oauth"), Env()), "anthropic OAuth");
        Equal("ANTHROPIC_AUTH_TOKEN", BuiltinProviders.CheckAuth(anthropic, null, Env(("ANTHROPIC_AUTH_TOKEN", "t"), ("ANTHROPIC_API_KEY", "k"))), "auth token first");
        Equal("workload identity federation", BuiltinProviders.CheckAuth(anthropic, null, Env(("ANTHROPIC_FEDERATION_RULE_ID", "r"),
            ("ANTHROPIC_ORGANIZATION_ID", "o"), ("ANTHROPIC_IDENTITY_TOKEN_FILE", "/t"))), "federation");
        Equal<string?>(null, BuiltinProviders.CheckAuth(codex, null, Env(("OPENAI_API_KEY", "k"))), "codex needs OAuth");
        Equal("OAuth", BuiltinProviders.CheckAuth(codex, new("oauth"), Env()), "codex OAuth");
        Equal<string?>(null, BuiltinProviders.CheckAuth(gateway, null, Env(("CLOUDFLARE_API_KEY", "k"), ("CLOUDFLARE_ACCOUNT_ID", "a"))), "gateway id required");
        Equal("CLOUDFLARE_API_KEY", BuiltinProviders.CheckAuth(gateway, null, Env(("CLOUDFLARE_API_KEY", "k"), ("CLOUDFLARE_ACCOUNT_ID", "a"),
            ("CLOUDFLARE_GATEWAY_ID", "g"))), "gateway configured");
        Equal("stored credential", BuiltinProviders.CheckAuth(gateway, new("api_key", "k", new Dictionary<string, string>
            { ["CLOUDFLARE_ACCOUNT_ID"] = "a", ["CLOUDFLARE_GATEWAY_ID"] = "g" }), Env()), "gateway stored");
        Equal("AWS access keys", BuiltinProviders.CheckAuth(bedrock, null, Env(("AWS_ACCESS_KEY_ID", "a"), ("AWS_SECRET_ACCESS_KEY", "s"))), "bedrock keys");
    }

    internal static void Registry(out ModelRegistry registry, Func<string, string?>? environment = null, string? modelsPath = null,
        IReadOnlyDictionary<string, ProviderStoredCredential>? stored = null) =>
        registry = ModelRegistry.Create(new()
        {
            Environment = environment ?? Env(), ModelsPath = modelsPath,
            StoredCredentials = stored ?? new Dictionary<string, ProviderStoredCredential>()
        });
}
