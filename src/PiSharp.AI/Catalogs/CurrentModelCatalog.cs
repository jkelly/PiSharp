// PiSharp-only (no upstream Pi source): the current release is the exact catalog data shipped in
// @earendil-works/pi-ai@1.7.0, source fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT), byte-verified by ReleasedModelCatalog.
// Historical release catalogs retain their original package bytes.
using System.Collections.Immutable;

namespace PiSharp.AI.Catalogs;

/// <summary>The current released provider snapshot, verified against its byte-pinned package manifest.
/// Host, maintained-catalog and explicit user overrides are applied by callers after this snapshot.</summary>
public static class CurrentModelCatalog
{
    public const string PackageVersion = "1.7.0";
    public const string SourceCommit = "fb0fe823539f6d04850f6e34748c01f01f212e1f";
    public const string PackageSha256 = "d0dd546e7c7c1dfdab6582be8a177ed1184cc857b6a876d3ef576a1cc8088a12";
    public const string PackageIntegrity = "sha512-iKryMlUYniwWbU7YU0uS7W0+cGd9c6oJ6PzRLOn8j4+1njG4c+BQq1cNawctx/npDqGeOx8ME7Gkgl6K6arPuw==";
    public const string ManifestSha256 = "d9c5aa7766128bc032ebd68e055760530bdf5b3ef56af445c01cc6ab0ba3fce3";
    public const long GeneratedAtUnixMilliseconds = 1_795_797_610_684;

    internal static ReleasedModelCatalog Release { get; } = new(PackageVersion, SourceCommit, PackageSha256, PackageIntegrity,
        ManifestSha256, GeneratedAtUnixMilliseconds);

    /// <summary>Every released provider, including providers absent from historical snapshots.</summary>
    public static ImmutableArray<string> Providers => Release.Providers;

    /// <summary>The exact released provider snapshot, or null for an unknown provider.</summary>
    public static FrozenModelCatalog? Get(string provider) => Release.Get(provider);
}
