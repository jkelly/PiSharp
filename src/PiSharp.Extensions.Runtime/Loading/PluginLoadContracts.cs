using System.Collections.Immutable;
using PiSharp.Extensions.Runtime.Discovery;

namespace PiSharp.Extensions.Runtime.Loading;

public enum PluginExecutionDisposition { Deny, ApprovePublishedFixtureExecution }

/// <summary>Explicit host execution trust, independent of non-authorizing metadata inspection.</summary>
public sealed record PluginExecutionDecision(
    PluginExecutionDisposition Disposition, string CanonicalPackageRoot, string ManifestValueSha256,
    ImmutableArray<ExtensionArtifactHash> ArtifactHashes, ExtensionSourceScope SourceScope,
    string EffectiveScopeId, string PolicyRevision, long HostGeneration)
{
    /// <summary>Separate explicit per-artifact platform-import approval. Empty grants no unmanaged resolution.</summary>
    public ImmutableArray<PluginSystemImportApproval> SystemImportApprovals { get; init; } = [];
}

/// <summary>A closed host platform profile bound to an exact approved managed artifact, never an arbitrary DLL path.</summary>
public sealed record PluginSystemImportApproval(string Profile, string Artifact, string Sha256);

public sealed record PluginAssemblyLoaderOptions
{
    public string SnapshotParentDirectory { get; init; } = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
    public ExtensionManifestReaderOptions ManifestOptions { get; init; } = new()
    { MaximumArtifactBytes = 8_388_608, MaximumTotalArtifactBytes = 16_777_216 };
    public long HostGeneration { get; init; } = 1;
    public int MaximumOwnedPackages { get; init; } = 8;
    public long MaximumRetainedSnapshotBytes { get; init; } = 67_108_864;
}

public enum PluginLoadFailure
{
    MetadataRejected, ExecutionTrustRequired, ExecutionTrustDenied, ExecutionTrustBindingMismatch,
    DuplicateOwner, LimitExceeded, ArtifactChanged, InvalidPublishedPackage, MissingDependency,
    UnsupportedAbi, UnsupportedFramework, UnsupportedNativeDependency, InvalidEntryType,
    LoadFailed, InitializationFailed, CleanupFailed, ReentrantDisposal, InactiveLoader
}

/// <summary>Bounded host diagnostics. Inner failures do not become interpolated product text.</summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadFailure Failure { get; }
    public string OwnerId { get; }
    public string Operation { get; }
    public bool RestartMayBeRequired => Failure is PluginLoadFailure.CleanupFailed or PluginLoadFailure.LoadFailed;
    internal PluginLoadException(PluginLoadFailure failure, string ownerId, string operation, Exception? inner = null)
        : base($"Published extension '{ownerId}' failed '{operation}': {failure}.", inner)
    { Failure = failure; OwnerId = ownerId; Operation = operation; }
}
