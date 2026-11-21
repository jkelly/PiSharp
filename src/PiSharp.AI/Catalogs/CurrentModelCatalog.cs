// PiSharp-only (no upstream Pi source): the current release is the exact catalog data shipped in
// @earendil-works/pi-ai@1.6.0, source faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT), byte-verified by ReleasedModelCatalog.
// The Pi 1.5.2 release and the historical package shards remain in their original catalogs.
using System.Collections.Immutable;

namespace PiSharp.AI.Catalogs;

/// <summary>The current released provider snapshot, verified against its byte-pinned package manifest.
/// Host, maintained-catalog and explicit user overrides are applied by callers after this snapshot.</summary>
public static class CurrentModelCatalog
{
    public const string PackageVersion = "1.6.0";
    public const string SourceCommit = "faac585884ae2c013a6b1400f3e9e77f10b34bac";
    public const string PackageSha256 = "d68312ec574bc959cb7fee26da4f4876d067aed9bbbe76e876359e60d2294747";
    public const string PackageIntegrity = "sha512-9hL+eygFDEiFp//cydeUfmFsKmEqpmvsNPb10Y1W8+m5K9O+sx1hMStfu6nukrBD5sl6mP4RykCYBBNEgsVLfw==";
    public const string ManifestSha256 = "cc46553f3f008788e667600a873d3d83a4df8fe46c67b06c65b16fd828450e8b";
    public const long GeneratedAtUnixMilliseconds = 1_795_171_729_496;

    internal static ReleasedModelCatalog Release { get; } = new(PackageVersion, SourceCommit, PackageSha256, PackageIntegrity,
        ManifestSha256, GeneratedAtUnixMilliseconds);

    /// <summary>Every released provider, including providers absent from historical snapshots.</summary>
    public static ImmutableArray<string> Providers => Release.Providers;

    /// <summary>The exact released provider snapshot, or null for an unknown provider.</summary>
    public static FrozenModelCatalog? Get(string provider) => Release.Get(provider);
}
