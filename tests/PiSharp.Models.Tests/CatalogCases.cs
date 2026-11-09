using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;

internal static partial class Program
{
    /// <summary>providers/data/.manifest.json of @earendil-works/pi-ai@1.5.0, verbatim (its SHA256 is checked below).</summary>
    private const string Manifest = """{"schemaVersion":6,"generatedAt":"2026-11-02T10:43:28.346Z","structureHash":"4a18066209dbb48dee5fc8d9ca253ea98d5e085ba8dd5e8fef4207bce0ffaa54","files":{"amazon-bedrock.json":"7688f295f84a50aa390255a68c2e8fdc7ec0768f5737ab40358844d597e89803","ant-ling.json":"628feab3ef6c7d80ce96f75999804e1d9f05fec3851e5302e30fff3b69db90f6","anthropic.json":"e462beb2e6d227b4dd1a8df22152cdebc9f3768d5dcf0939e1bf35e359129753","azure.json":"f95a8325f93825ab21d1d83fd2aff580f4d5cfa1a537c3108cd5ddb36f26e7fc","baseten.json":"db68a67a9aff8ad2cc05be9fc935dfe0c64cf72c46cca04bb2f2631f42f7fe80","cerebras.json":"2eb969b2956844f9a641d3de52c19f1791293c58f607282fb3b0ebfe85ff66f8","cloudflare-ai-gateway.json":"3931cdac8da96113a5667ee7fe166b72bb80b77696c06378a0947bcdd9276b9a","cloudflare-workers-ai.json":"0dc5eda87ff6de3ee75ee6d547a60162319bb2d51ed09b1e7d47d8ae9ef8548e","deepseek.json":"10a296fb3e898f7715c80890c8af0af4f5fd58f22dbcd6612fdc5d762157ccdc","fireworks.json":"4c8b4942ce08b55df711ac8b1eea65bbf28135b2bf2f475c95f11b2e7aa1012d","github-copilot.json":"85330f133dac9017c97ad72dd2b356f3f03992c7ca3d5b0543060cbe13dcd34d","google-vertex.json":"bc32e24bfd8ab0caedda4c11a7527a1dcc7f783ff5326ce4c72a13bd7ab47d9b","google.json":"61a36b42e1436b84c8ed8d738ecb1a2a9d9267f876f7c612407a349b7ca4c301","groq.json":"087df37206e9eed8b10f1e558e4c496289bef60074b7e8afba4f20fb102b4ec2","huggingface.json":"cf34e226723c542afc4d024a1b1270b911738410dc5cf706fef5d9d4a0e08171","kimi-coding.json":"04e7914391e8e986d04b5cc8f84c49267d148a53a2d92bf968b7570dd8ff09b9","meta.json":"563f5ec07973f52ab8c3eb26ef91a36ad8a79e950b37174935666cdcb624bf35","minimax-cn.json":"fa15d1f67a07046d38e191af9bd97ed922ba45a99a11af4ad281151cc6b762aa","minimax.json":"1e0459dda3858ea24151182106fc4179b95f43a0a8dffc1c3d2e7db4c2d9206e","mistral.json":"013b34618e8f9a3968e0442e7e1339cc2a1bbad467f04bab0b8589a9c451b780","moonshotai-cn.json":"1a15468b1edf9205956e4c5ea41fc4cacc14eabf00523b9143e23a38264f57f7","moonshotai.json":"f31cdf2e6c81a403c4cb0e6b7e759ceff2820f966b08dbec80cfafa3f80dfc63","nvidia.json":"e030898fb1d1778b85c5b86fdde95b28e4dd75f36302f50bcff8bf2941f919b1","openai-codex.json":"dd30453068f310317abf35d9adc6d841339a1351f0c6925a6aff45d975e0257c","openai.json":"df2fe1f312051bbe29801ef903ab370c701a74a680f1370e96eb465eacd1d82d","opencode-go.json":"daed1626c48d9cec6da77f53a163aec917d5d5fc8d6e4d53c23f448f99463328","opencode.json":"89049678c12b553fd44d3b760514ead39ebc6fa68d84b135db69c133ab443bc9","openrouter.json":"ea1220ec52009a91d78b8e5ffae261ece94bafa4a03e8d75f1fd98e2b9351322","qwen-token-plan-cn.json":"80824536f0c3cd83ab27424df2d664738cbe3f971b4e568a625432288fa53229","qwen-token-plan-individual.json":"7d416a2af46d5ad5d7872f8edfa7727af4392827a5b77618ce89a3786628492a","qwen-token-plan.json":"0630bf7fea9d8579c7ac990e32c40c3685f9db20b6dbfaf745b361fe5203113c","radius.json":"a9bcc74b4a1ad2e01b6360d5f9197d802c18559a58be0ccd588662a01598368a","together.json":"ce03a702cf835ae1d3b4fd14ea4a1eae71a81224bc4a0826182f3f87b824aeee","typesafe.json":"631b03f70597ad656e8f45a393ab46bd4137459b9b499708ac3d5d9d9decd3f2","vercel-ai-gateway.json":"78631c8ff8614805c4b799bcc2063861110f34bb8ecb9575ba50154cd3e6daf1","xai.json":"db408985a02980ba9d30f359b4f394a7d088cc75e26bd4bef30aecb43077771b","xiaomi-token-plan-ams.json":"34fe47b0a79fec7843a01d542cb64b736e651a3e6691cc851183e5ce3eae0118","xiaomi-token-plan-cn.json":"e69f9704b450dbb34b6db61a7b224dab5e5a91a39af3e505ecca4db8bfdc04f0","xiaomi-token-plan-sgp.json":"fd28cc293208617127ff888873772b7b52160538caa07ca3af641f3208d3ad20","xiaomi.json":"65bc04b2ab6c9e97594de34bcea4ef7e45befd87901b4208d1ea4142b93f0561","zai-coding-cn.json":"b335901611ad018680e5bb7fe7ba12a9f1be28e9d1530c783cbd6f4567a29a92","zai.json":"08b29b02416c26f0c1a219492fa7f9b23bdb90ef4e9f7693800b9e670dcc5860"}}""";
    private const string ManifestSha256 = "50e0e74cb54dada0a6972c2619e56e99830ee01857810719b6b913cd29e04e87";
    private const string TarballSha256 = "e320fe7bd2b927782ba9a37d356fe5837f1aa96a46432610e1b3b9ccb748ea5c";

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
        Equal(1712, models, "catalog entries across all shards");
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
            "qwen-token-plan-cn", "qwen-token-plan-individual", "qwen-token-plan-individual-cn", "radius", "together", "typesafe", "vercel-ai-gateway", "xai", "xiaomi",
            "xiaomi-token-plan-ams", "xiaomi-token-plan-cn", "xiaomi-token-plan-sgp", "zai", "zai-coding-cn"],
            BuiltinProviders.All.Select(provider => provider.Id), "builtinProviders() order");
        foreach (var provider in BuiltinProviders.All)
        {
            var catalog = ReviewedModelCatalog.Apply(provider.Id, BuiltinModelCatalog.Has(provider.Id) ? BuiltinModelCatalog.Get(provider.Id) : null)!;
            foreach (var api in catalog.DeclaredApis) Check(provider.Apis.Contains(api), $"{provider.Id} lacks API {api}");
            if (provider.BaseUrl is { } baseUrl && provider.Id is not ("fireworks" or "openrouter" or "vercel-ai-gateway"))
                Check(catalog.Models.All(model => model.BaseUrl == baseUrl), provider.Id + " base URL");
        }
        Registry(out var registry);
        Equal("Z.AI Coding CN", registry.GetProviderDisplayName("zai-coding-cn"), "display name");
        Equal("not-a-provider", registry.GetProviderDisplayName("not-a-provider"), "fallback display name");
    }

    private static void GeneratedAt() =>
        Equal(DateTimeOffset.Parse("2026-11-02T10:43:28.346Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
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
