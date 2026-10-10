// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/all.ts (getBuiltinModels,
// getBuiltinModelDataGeneratedAt) over the providers/data/*.json shards of @earendil-works/pi-ai@1.1.0.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

internal sealed class BuiltinCatalogException(string code, string message) : Exception(message)
{ internal string Code { get; } = code; }

/// <summary>The embedded, byte-pinned provider catalog shards. Each shard is verified against its recorded SHA256 before parsing.</summary>
internal static class BuiltinModelCatalog
{
    /// <summary>providers/data/.manifest.json <c>generatedAt</c> (2026-10-07T22:01:28.515Z) as Unix milliseconds.</summary>
    internal const long GeneratedAtUnixMilliseconds = 1_791_410_488_515;
    /// <summary>SHA256 of the package's <c>providers/data/.manifest.json</c>; its per-file hashes equal <see cref="ShardHashes"/>.</summary>
    internal const string ManifestSha256 = "fb3b3b6b4fd9dcfac1f77058520bffb5fab4f15e15f97307b8afe2cd3f9dfae1";
    internal const string PackageSha256 = "6caab33cec57480ed02c57fe37428a030a77cc2a0662814b435a5cf8932ad829";

    /// <summary>Every shard of pi-ai 1.1.0 with the SHA256 its package manifest records.</summary>
    internal static readonly ImmutableDictionary<string, string> ShardHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["amazon-bedrock"] = "d9381ff9bf5c9d8c03b2909665526d243dfc33600e73beec4d58df18dfd0addd",
        ["ant-ling"] = "628feab3ef6c7d80ce96f75999804e1d9f05fec3851e5302e30fff3b69db90f6",
        ["anthropic"] = "aa4342dfb96feb1619794113619d6630088d6ac544c547a4f9899a0a7f26419b",
        ["azure"] = "46c4e25b84463c6475e706c581196d2735c35feb9612d2efca0ab932c917d727",
        ["baseten"] = "b04d1423d6958bec857ffccc36726f091ec1a8f3a09cf0836b64cc94b0ee697d",
        ["cerebras"] = "3eccb58c5de3d0dc7ba49dc6e477ba29f00cb3150ce0a35cc90d59e6b5ab368e",
        ["cloudflare-ai-gateway"] = "e76699154fe8a933c504c5f64f6cb5e2bca572efd21edd0d429e51c6c234ebbc",
        ["cloudflare-workers-ai"] = "d04336a61cacfd0cadbc2f3d30cd3b552e3d153e5a320658a31ba1bab8f01c83",
        ["deepseek"] = "10a296fb3e898f7715c80890c8af0af4f5fd58f22dbcd6612fdc5d762157ccdc",
        ["fireworks"] = "8db3c11f65a5d76260d5040a5dc5a9ef0f35b92de8505a30907c2355c2837b36",
        ["github-copilot"] = "8119aa175666d70136471aa7cae08043910a97d9cbede8b367d0b2c20559049e",
        ["google-vertex"] = "40090c680538bc867f2624d986a449f361be4dfa6ba96d7a27402050f036e009",
        ["google"] = "64bdd2563cee0e3598aec4d93a6735c189b6d7d14b9c03f061cc2824f3730528",
        ["groq"] = "910952ad9dad07856983459d63fce0148dba1203edd7b10d801a77c5824f3405",
        ["huggingface"] = "1b8604bd31a5e05fe43a40ec1ec6dfc1a51a28b0b020151ef057fab43f57c923",
        ["kimi-coding"] = "582250b920454d98940da3e7e99d30b807edbea28f2eb8ac15f658d2ef41e8fc",
        ["meta"] = "0c7e9a370a7005c5a89dc94f0f93bc67b8935c3f802de324f11af6ff56fff429",
        ["minimax-cn"] = "1bbfbd11c3edbd141841d3cef027e12157218f1aec92477e15772116c1fabe7a",
        ["minimax"] = "a4b6864bd97eddf5749b2e117f643e751ec237408e8e24bec9e2f947782fa244",
        ["mistral"] = "fcd37c7b178416f86954efdacbb45726d10f102fd1211062792691e62fcf327c",
        ["moonshotai-cn"] = "79c9585dc84ee525ebaebbc78b3c55f3d369ec884d4f943488f1e0b14e3587f5",
        ["moonshotai"] = "1da2c4e34f22aef17a07c974d85bdee8e1b68b6dc712e3769c935b3f1e7dadca",
        ["nvidia"] = "a75e963dc81779e3eec01c051b12277c15ec37c5b94ab51ece9e9ccb3b16407c",
        ["openai-codex"] = "3cc35a7e122e5c77ee8ec30a9a3bd8bd59629997e85ba2cb3f9e0ed62e9a09c0",
        ["openai"] = "f4c1ac9f8f84cb9f2a952b0ceec51c90a38b31b4cdf33200e018ece9d408e95f",
        ["opencode-go"] = "4a10d85457c0d25d4fd8b4089cb15c0f54f086b9a6b304d7bd8e92397a3d7d0b",
        ["opencode"] = "a6a76926c51c2c410422dec612fdbe903b63191ff7334242e4f6ca3758bc0d2f",
        ["openrouter"] = "c86aa3b95d412465dac54cb902402cbdb40f47a1fe33f12b002913834724d8f0",
        ["qwen-token-plan-cn"] = "2469a4091c474ec3db0a49a9962d8d2726c074ad9065b96bc4e0c87861c4c6c5",
        ["qwen-token-plan-individual"] = "0fb7cf64faf39d1a3b0a884210a86c87084b805eda15644f5f39293287b959dd",
        ["qwen-token-plan"] = "eff10620cb577142de4ec02698d5961a71fda2fe4f6ae7ea9432bad9d6844176",
        ["radius"] = "88a3f060a851882df89015bcd346e03238b4b0725bd72c8ad501fcffff782768",
        ["together"] = "52293df62191c8e799ae3632f8ab1b3f52ee626aee0280677086627b32864bf7",
        ["typesafe"] = "a4429a2f696d5cc5273969af61a7310bda07d6bdcd27bf9ac9c76c534cc43716",
        ["vercel-ai-gateway"] = "0ec37357cc2c3eec0b71cbff98910bfd0988e16b89178d9c930579f1ce747c30",
        ["xai"] = "879c49b78b663a52a220ac4ee013e726b10c4f50aaa03bc58ee53a4b54c76928",
        ["xiaomi-token-plan-ams"] = "ca65178f4525d9705bc8d085ed136cecbe149c4080e857ea6a64ae643d2ac00a",
        ["xiaomi-token-plan-cn"] = "b065326926681c2262084e59c1c19ebdaf146c07107b5242c76b456becf74db0",
        ["xiaomi-token-plan-sgp"] = "92c3d6d707f81fd9cd2b6d1fb57dc431d90ba51e10fe49d97bc8806035c23492",
        ["xiaomi"] = "b81d01a374f7b558728c78d81f4168efa9c8996e6e2b5618409a8094b083c759",
        ["zai-coding-cn"] = "ee5ee705fb6e413a6088db5e3616c27fce802a8ece6c355f1db35490d787f610",
        ["zai"] = "58e6642fdfb736f0b9a4a277c760c536a3cea66a030abbfb2615f7b2851d5c6f",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, FrozenModelCatalog> Loaded = new(StringComparer.Ordinal);

    /// <summary>The verified shard bytes of a built-in provider.</summary>
    internal static byte[] ReadShard(string provider)
    {
        if (!ShardHashes.TryGetValue(provider, out var expected))
            throw new BuiltinCatalogException("LiveCatalogUnavailable", "The pinned live model catalog is unavailable.");
        using var resource = typeof(BuiltinModelCatalog).Assembly.GetManifestResourceStream("PiSharp.Cli.Models." + provider + ".json")
            ?? throw new BuiltinCatalogException("LiveCatalogUnavailable", "The pinned live model catalog is unavailable.");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
            throw new BuiltinCatalogException("LiveCatalogMismatch", "The pinned live model catalog hash differs.");
        return bytes;
    }

    /// <summary>The parsed, hash-verified catalog of a built-in provider (cached per process).</summary>
    internal static FrozenModelCatalog Get(string provider) => Loaded.GetOrAdd(provider, id =>
        FrozenModelCatalog.ReadProviderJson(id, ReadShard(id)));

    internal static bool Has(string provider) => ShardHashes.ContainsKey(provider);
}
