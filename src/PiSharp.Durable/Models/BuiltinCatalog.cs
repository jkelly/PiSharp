// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/providers/all.ts (getBuiltinModels over the providers/data/*.json
// shards of @earendil-works/pi-ai@1.5.0). The shards are PiSharp.Cli's byte-pinned copies (src/PiSharp.Cli/Models/*.json), embedded here
// by link; each is verified against the SHA256 its package manifest records before it is parsed, as PiSharp.Cli does.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using PiSharp.AI.Catalogs;

namespace PiSharp.Durable;

/// <summary>The embedded, hash-verified built-in provider catalog shards (PiSharp.Cli's <c>BuiltinModelCatalog</c>, which the durable
/// core cannot reference).</summary>
internal static class DurableBuiltinCatalog
{
    /// <summary>Every shard of pi-ai 1.5.0 with the SHA256 its package manifest records (the same table as PiSharp.Cli's).</summary>
    internal static readonly ImmutableDictionary<string, string> ShardHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["amazon-bedrock"] = "7688f295f84a50aa390255a68c2e8fdc7ec0768f5737ab40358844d597e89803",
        ["ant-ling"] = "628feab3ef6c7d80ce96f75999804e1d9f05fec3851e5302e30fff3b69db90f6",
        ["anthropic"] = "e462beb2e6d227b4dd1a8df22152cdebc9f3768d5dcf0939e1bf35e359129753",
        ["azure"] = "f95a8325f93825ab21d1d83fd2aff580f4d5cfa1a537c3108cd5ddb36f26e7fc",
        ["baseten"] = "db68a67a9aff8ad2cc05be9fc935dfe0c64cf72c46cca04bb2f2631f42f7fe80",
        ["cerebras"] = "2eb969b2956844f9a641d3de52c19f1791293c58f607282fb3b0ebfe85ff66f8",
        ["cloudflare-ai-gateway"] = "3931cdac8da96113a5667ee7fe166b72bb80b77696c06378a0947bcdd9276b9a",
        ["cloudflare-workers-ai"] = "0dc5eda87ff6de3ee75ee6d547a60162319bb2d51ed09b1e7d47d8ae9ef8548e",
        ["deepseek"] = "10a296fb3e898f7715c80890c8af0af4f5fd58f22dbcd6612fdc5d762157ccdc",
        ["fireworks"] = "4c8b4942ce08b55df711ac8b1eea65bbf28135b2bf2f475c95f11b2e7aa1012d",
        ["github-copilot"] = "85330f133dac9017c97ad72dd2b356f3f03992c7ca3d5b0543060cbe13dcd34d",
        ["google-vertex"] = "bc32e24bfd8ab0caedda4c11a7527a1dcc7f783ff5326ce4c72a13bd7ab47d9b",
        ["google"] = "61a36b42e1436b84c8ed8d738ecb1a2a9d9267f876f7c612407a349b7ca4c301",
        ["groq"] = "087df37206e9eed8b10f1e558e4c496289bef60074b7e8afba4f20fb102b4ec2",
        ["huggingface"] = "cf34e226723c542afc4d024a1b1270b911738410dc5cf706fef5d9d4a0e08171",
        ["kimi-coding"] = "04e7914391e8e986d04b5cc8f84c49267d148a53a2d92bf968b7570dd8ff09b9",
        ["meta"] = "563f5ec07973f52ab8c3eb26ef91a36ad8a79e950b37174935666cdcb624bf35",
        ["minimax-cn"] = "fa15d1f67a07046d38e191af9bd97ed922ba45a99a11af4ad281151cc6b762aa",
        ["minimax"] = "1e0459dda3858ea24151182106fc4179b95f43a0a8dffc1c3d2e7db4c2d9206e",
        ["mistral"] = "013b34618e8f9a3968e0442e7e1339cc2a1bbad467f04bab0b8589a9c451b780",
        ["moonshotai-cn"] = "1a15468b1edf9205956e4c5ea41fc4cacc14eabf00523b9143e23a38264f57f7",
        ["moonshotai"] = "f31cdf2e6c81a403c4cb0e6b7e759ceff2820f966b08dbec80cfafa3f80dfc63",
        ["nvidia"] = "e030898fb1d1778b85c5b86fdde95b28e4dd75f36302f50bcff8bf2941f919b1",
        ["openai-codex"] = "dd30453068f310317abf35d9adc6d841339a1351f0c6925a6aff45d975e0257c",
        ["openai"] = "df2fe1f312051bbe29801ef903ab370c701a74a680f1370e96eb465eacd1d82d",
        ["opencode-go"] = "daed1626c48d9cec6da77f53a163aec917d5d5fc8d6e4d53c23f448f99463328",
        ["opencode"] = "89049678c12b553fd44d3b760514ead39ebc6fa68d84b135db69c133ab443bc9",
        ["openrouter"] = "ea1220ec52009a91d78b8e5ffae261ece94bafa4a03e8d75f1fd98e2b9351322",
        ["qwen-token-plan-cn"] = "80824536f0c3cd83ab27424df2d664738cbe3f971b4e568a625432288fa53229",
        ["qwen-token-plan-individual"] = "7d416a2af46d5ad5d7872f8edfa7727af4392827a5b77618ce89a3786628492a",
        ["qwen-token-plan"] = "0630bf7fea9d8579c7ac990e32c40c3685f9db20b6dbfaf745b361fe5203113c",
        ["radius"] = "a9bcc74b4a1ad2e01b6360d5f9197d802c18559a58be0ccd588662a01598368a",
        ["together"] = "ce03a702cf835ae1d3b4fd14ea4a1eae71a81224bc4a0826182f3f87b824aeee",
        ["typesafe"] = "631b03f70597ad656e8f45a393ab46bd4137459b9b499708ac3d5d9d9decd3f2",
        ["vercel-ai-gateway"] = "78631c8ff8614805c4b799bcc2063861110f34bb8ecb9575ba50154cd3e6daf1",
        ["xai"] = "db408985a02980ba9d30f359b4f394a7d088cc75e26bd4bef30aecb43077771b",
        ["xiaomi-token-plan-ams"] = "34fe47b0a79fec7843a01d542cb64b736e651a3e6691cc851183e5ce3eae0118",
        ["xiaomi-token-plan-cn"] = "e69f9704b450dbb34b6db61a7b224dab5e5a91a39af3e505ecca4db8bfdc04f0",
        ["xiaomi-token-plan-sgp"] = "fd28cc293208617127ff888873772b7b52160538caa07ca3af641f3208d3ad20",
        ["xiaomi"] = "65bc04b2ab6c9e97594de34bcea4ef7e45befd87901b4208d1ea4142b93f0561",
        ["zai-coding-cn"] = "b335901611ad018680e5bb7fe7ba12a9f1be28e9d1530c783cbd6f4567a29a92",
        ["zai"] = "08b29b02416c26f0c1a219492fa7f9b23bdb90ef4e9f7693800b9e670dcc5860",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, FrozenModelCatalog> Loaded = new(StringComparer.Ordinal);

    /// <summary>The built-in providers with a frozen shard or reviewed addition, in ordinal order.</summary>
    internal static ImmutableArray<string> Providers { get; } = CurrentModelCatalog.Providers;

    /// <summary>The verified shard bytes of a built-in provider.</summary>
    internal static byte[] ReadShard(string provider)
    {
        if (!ShardHashes.TryGetValue(provider, out var expected))
            throw new InvalidOperationException($"No built-in model catalog for provider {provider}");
        using var resource = typeof(DurableBuiltinCatalog).Assembly.GetManifestResourceStream("PiSharp.Durable.Models." + provider + ".json")
            ?? throw new InvalidOperationException($"The built-in model catalog of provider {provider} is not embedded");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
            throw new InvalidOperationException($"The built-in model catalog of provider {provider} differs from its pinned hash");
        return bytes;
    }

    /// <summary>The hash-verified baseline plus explicit reviewed changes (cached per process); null for an unknown provider.</summary>
    internal static FrozenModelCatalog? Get(string provider) =>
        CurrentModelCatalog.Providers.Contains(provider)
            ? Loaded.GetOrAdd(provider, id => ReviewedModelCatalog.Apply(id, ShardHashes.ContainsKey(id) ? FrozenModelCatalog.ReadProviderJson(id, ReadShard(id)) : null)!) : null;
}
