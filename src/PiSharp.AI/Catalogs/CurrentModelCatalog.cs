// PiSharp-only (no upstream Pi source): native loader byte-verifies the exact catalog data
// shipped in @earendil-works/pi-ai@1.5.2, source b4afdddf44e059c67ccf5097e5317d913521b470 (MIT).
// Historical package shards remain in their original catalogs.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiSharp.AI.Catalogs;

/// <summary>The current released provider snapshot, verified against its byte-pinned package manifest.
/// Host, maintained-catalog and explicit user overrides are applied by callers after this snapshot.</summary>
public static class CurrentModelCatalog
{
    public const string PackageVersion = "1.5.2";
    public const string SourceCommit = "b4afdddf44e059c67ccf5097e5317d913521b470";
    public const string PackageSha256 = "d5715efa834c23bfdf10a042b49df4ee169cc99000bf16d7998fab2d533b45f8";
    public const string PackageIntegrity = "sha512-2tWPSnV3qzfiDIY3aWluBag3vC++Vs+VsXt/UquSU5onPMohPih72FUhIhaqmDdMYubOgB3Byl9uF4Vk40JLvQ==";
    public const string ManifestSha256 = "71375843feaa116c7d928e74e0500a22cc267e12bfb6869efb92dfd39acf7fee";
    public const long GeneratedAtUnixMilliseconds = 1_794_312_337_628;
    private const string ResourcePrefix = "PiSharp.AI.Catalogs.Releases.1.5.2.";
    private static readonly ImmutableDictionary<string, string> ShardHashes = ReadManifest();
    private static readonly ConcurrentDictionary<string, FrozenModelCatalog> Loaded = new(StringComparer.Ordinal);

    /// <summary>Every released provider, including providers absent from historical snapshots.</summary>
    public static ImmutableArray<string> Providers { get; } = [.. ShardHashes.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The exact released provider snapshot, or null for an unknown provider.</summary>
    public static FrozenModelCatalog? Get(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return ShardHashes.ContainsKey(provider) ? Loaded.GetOrAdd(provider, id =>
        {
            var bytes = ReadResource(id + ".json");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ShardHashes[id])
                throw new InvalidOperationException("Current model catalog differs from its pinned package manifest: " + id);
            return FrozenModelCatalog.ReadProviderJson(id, bytes);
        }) : null;
    }

    private static ImmutableDictionary<string, string> ReadManifest()
    {
        var bytes = ReadResource(".manifest.json");
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ManifestSha256)
            throw new InvalidOperationException("Current model catalog manifest differs from its pinned package.");
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 6 ||
            DateTimeOffset.Parse(manifest.GetProperty("generatedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() != GeneratedAtUnixMilliseconds)
            throw new InvalidOperationException("Current model catalog manifest metadata differs from its pinned package.");
        return manifest.GetProperty("files").EnumerateObject().ToImmutableDictionary(
            entry => entry.Name[..^5], entry => entry.Value.GetString()!, StringComparer.Ordinal);
    }

    private static byte[] ReadResource(string name)
    {
        using var resource = typeof(CurrentModelCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException("Current model catalog resource is missing: " + name);
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }
}
