// PiSharp-only (no upstream Pi source): native loader byte-verifies the exact catalog data shipped in one published
// @earendil-works/pi-ai package (MIT). Each release keeps its own embedded directory and pins; none overwrites another.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiSharp.AI.Catalogs;

/// <summary>One released provider snapshot, verified against its byte-pinned package manifest before any shard is parsed.
/// <see cref="CurrentModelCatalog"/> is the release the runtime uses; earlier releases stay addressable here unchanged.</summary>
public sealed class ReleasedModelCatalog
{
    private readonly string _resourcePrefix;
    private readonly Lazy<ImmutableDictionary<string, string>> _shardHashes;
    private readonly Lazy<ImmutableArray<string>> _providers;
    private readonly ConcurrentDictionary<string, FrozenModelCatalog> _loaded = new(StringComparer.Ordinal);

    internal ReleasedModelCatalog(string packageVersion, string sourceCommit, string packageSha256, string packageIntegrity,
        string manifestSha256, long generatedAtUnixMilliseconds)
    {
        PackageVersion = packageVersion; SourceCommit = sourceCommit; PackageSha256 = packageSha256; PackageIntegrity = packageIntegrity;
        ManifestSha256 = manifestSha256; GeneratedAtUnixMilliseconds = generatedAtUnixMilliseconds;
        _resourcePrefix = "PiSharp.AI.Catalogs.Releases." + packageVersion + ".";
        _shardHashes = new(ReadManifest);
        _providers = new(() => [.. _shardHashes.Value.Keys.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The complete Pi 1.5.2 release (b4afdddf44e059c67ccf5097e5317d913521b470), preserved as shipped. It was the current
    /// catalog of PiSharp 1.5.2 and is no longer consulted when models are resolved.</summary>
    public static ReleasedModelCatalog Pi152 { get; } = new("1.5.2", "b4afdddf44e059c67ccf5097e5317d913521b470",
        "d5715efa834c23bfdf10a042b49df4ee169cc99000bf16d7998fab2d533b45f8",
        "sha512-2tWPSnV3qzfiDIY3aWluBag3vC++Vs+VsXt/UquSU5onPMohPih72FUhIhaqmDdMYubOgB3Byl9uF4Vk40JLvQ==",
        "71375843feaa116c7d928e74e0500a22cc267e12bfb6869efb92dfd39acf7fee", 1_794_312_337_628);

    /// <summary>The release <see cref="CurrentModelCatalog"/> serves.</summary>
    public static ReleasedModelCatalog Current => CurrentModelCatalog.Release;

    public string PackageVersion { get; }
    public string SourceCommit { get; }
    public string PackageSha256 { get; }
    public string PackageIntegrity { get; }
    public string ManifestSha256 { get; }
    public long GeneratedAtUnixMilliseconds { get; }

    /// <summary>Every provider of this release, in ordinal order.</summary>
    public ImmutableArray<string> Providers => _providers.Value;

    /// <summary>The exact released provider snapshot, or null for a provider this release does not have.</summary>
    public FrozenModelCatalog? Get(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _shardHashes.Value.ContainsKey(provider) ? _loaded.GetOrAdd(provider, id =>
        {
            var bytes = ReadResource(id + ".json");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != _shardHashes.Value[id])
                throw new InvalidOperationException($"Model catalog {PackageVersion} differs from its pinned package manifest: {id}");
            return FrozenModelCatalog.ReadProviderJson(id, bytes);
        }) : null;
    }

    private ImmutableDictionary<string, string> ReadManifest()
    {
        var bytes = ReadResource(".manifest.json");
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ManifestSha256)
            throw new InvalidOperationException($"Model catalog {PackageVersion} manifest differs from its pinned package.");
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 6 ||
            DateTimeOffset.Parse(manifest.GetProperty("generatedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() != GeneratedAtUnixMilliseconds)
            throw new InvalidOperationException($"Model catalog {PackageVersion} manifest metadata differs from its pinned package.");
        return manifest.GetProperty("files").EnumerateObject().ToImmutableDictionary(
            entry => entry.Name[..^5], entry => entry.Value.GetString()!, StringComparer.Ordinal);
    }

    private byte[] ReadResource(string name)
    {
        using var resource = typeof(ReleasedModelCatalog).Assembly.GetManifestResourceStream(_resourcePrefix + name)
            ?? throw new InvalidOperationException($"Model catalog {PackageVersion} resource is missing: {name}");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }
}
