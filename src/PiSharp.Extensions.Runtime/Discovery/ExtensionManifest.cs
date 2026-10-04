using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Runtime.Discovery;

public enum ExtensionDeclaredCapability { Tools, Commands, Observations }
public enum ExtensionOverrideKind { Tool, Command }
public enum ExtensionSourceScope { User, Project, Explicit }
public sealed record ExtensionHostApiRange(string Minimum, string MaximumExclusive);
public sealed record ExtensionArtifactHash(string RelativePath, string Sha256);
public sealed record ExtensionExplicitOverride(ExtensionOverrideKind Kind, string Target);

/// <summary>Unfrozen schema-version-zero metadata. No field grants permission or activates executable code.</summary>
public sealed record ExtensionManifest(
    int SchemaVersion, string Id, string PackageVersion, ExtensionHostApiRange HostApiRange,
    string RuntimeKind, string Assembly, string EntryType, string Tfm, ImmutableArray<string> Rids,
    ImmutableArray<string> RequiredFeatures, ImmutableArray<ExtensionDeclaredCapability> DeclaredCapabilities,
    ImmutableArray<string> ResourcePaths, ImmutableArray<ExtensionExplicitOverride> ExplicitOverrides,
    ImmutableArray<ExtensionArtifactHash> ArtifactHashes, JsonData Metadata);

public enum ExtensionManifestFailure
{
    InvalidJson, ManifestTooLarge, InvalidManifest, UnsupportedSchema, UnsupportedRuntimeKind,
    UnsupportedHostApi, UnsupportedTfm, UnsupportedRid, UnsupportedFilesystemPlatform, UnsupportedFeature, UnsupportedCapability,
    UnsupportedOverride, InvalidPackageRoot, InvalidArtifactPath, MissingArtifact, ReparsePoint,
    ArtifactReadFailed, ArtifactCountExceeded, ArtifactTooLarge, ArtifactBudgetExceeded, IntegrityMismatch,
    TrustRequired, TrustDenied, TrustBindingMismatch
}

/// <summary>Fixed diagnostics with a bounded manifest field or relative artifact path, never raw OS exception text.</summary>
public sealed record ExtensionManifestDiagnostic(ExtensionManifestFailure Failure, string Field)
{
    public string Message => $"Extension metadata preflight rejected '{Field}': {Failure}.";
}

public sealed record ExtensionManifestReadResult(
    ExtensionManifest? Manifest, string? ManifestValueSha256, ImmutableArray<ExtensionManifestDiagnostic> Diagnostics)
{
    public bool IsValid => Manifest is not null && Diagnostics.IsEmpty;
}

public sealed record VerifiedExtensionArtifact(string RelativePath, string FullPath, string Sha256, long Bytes);

public sealed record ExtensionManifestAdmission(
    ExtensionManifest? Manifest, string? ManifestValueSha256, string? CanonicalPackageRoot,
    ExtensionSourceScope SourceScope, string EffectiveScopeId, ImmutableArray<VerifiedExtensionArtifact> VerifiedArtifacts,
    ImmutableArray<ExtensionManifestDiagnostic> Diagnostics)
{
    public bool MetadataPreflightPassed => Manifest is not null && Diagnostics.IsEmpty;
    public bool IsLoadAuthorized => false;
    public string ContractProfile => "experimental-native-manifest-0";
    public ImmutableArray<string> RequiredBeforeLoading =>
        ["atomic-path-and-open-handle-identity", "qualified-platform-link-policy", "approved-native-abi",
         "dependency-closure-and-shared-contract-identity", "entrypoint-and-runtime-validation",
         "declared-capability-brokers", "trust-rechecked-at-execution", "trusted-loader"];
}

public sealed record ExtensionManifestReaderOptions
{
    public string HostApiVersion { get; init; } = "0.0.0";
    public string HostRuntimeIdentifier { get; init; } = "win-x64";
    public string TrustPolicyRevision { get; init; } = "experimental-policy-0";
    public int MaximumManifestCharacters { get; init; } = 65_536;
    public int MaximumArtifacts { get; init; } = 128;
    public long MaximumArtifactBytes { get; init; } = 67_108_864;
    public long MaximumTotalArtifactBytes { get; init; } = 134_217_728;
}

/// <summary>Optional host instrumentation of real read ownership, not an extension callback or a byte supplier.</summary>
public enum ExtensionArtifactReadStage { Opened, Closing }
public delegate ValueTask ExtensionArtifactReadObserver(
    ExtensionArtifactReadStage stage, string relativePath, CancellationToken cancellationToken);
