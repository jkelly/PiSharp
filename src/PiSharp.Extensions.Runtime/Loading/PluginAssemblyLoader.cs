using PiSharp.Contracts;
using PiSharp.Extensions.Runtime.Discovery;

namespace PiSharp.Extensions.Runtime.Loading;

/// <summary>Experimental execution of explicitly approved, published task-local fixtures. Not an untrusted-code boundary.</summary>
public sealed class PluginAssemblyLoader : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly PluginAssemblyLoaderOptions options;
    private readonly ExtensionManifestReader reader;
    private readonly string snapshotParent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, PluginReservation> reservations = new(StringComparer.Ordinal);
    private long chargedBytes;
    private bool closing;
    private TaskCompletionSource? disposal;

    public PluginAssemblyLoader(PluginAssemblyLoaderOptions? options = null)
    {
        this.options = options ?? new();
        ArgumentNullException.ThrowIfNull(this.options.ManifestOptions);
        reader = new(this.options.ManifestOptions);
        snapshotParent = ManifestPathPolicy.Root(this.options.SnapshotParentDirectory) ?? throw new ArgumentException("A canonical local snapshot parent is required.", nameof(options));
        if (this.options.HostGeneration < 1 || this.options.MaximumOwnedPackages is < 1 or > 8 ||
            this.options.MaximumRetainedSnapshotBytes is < 1 or > 67_108_864 || this.options.ManifestOptions.MaximumArtifactBytes > 8_388_608 ||
            this.options.ManifestOptions.MaximumTotalArtifactBytes > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task<LoadedExtension> LoadAsync(JsonData metadata, string packageRoot, ExtensionSourceScope sourceScope,
        string effectiveScopeId, ExtensionTrustDecision? inspectionDecision, PluginExecutionDecision? executionDecision,
        ExtensionRegistry registry, CancellationToken cancellationToken = default,
        Func<string, IPiSharpExtension, CancellationToken, Task<RegistrationScope>>? activateOwner = null,
        Func<RegistrationScope, Task>? retireOwner = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (activateOwner is not null && activateOwner.GetInvocationList().Length != 1)
            throw new ArgumentException("One explicitly admitted owner initializer required.", nameof(activateOwner));
        if (retireOwner is not null && (activateOwner is null || retireOwner.GetInvocationList().Length != 1))
            throw new ArgumentException("One admitted retirement paired with the initializer required.", nameof(retireOwner));
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = reader.Read(metadata);
        if (!parsed.IsValid) throw Failure(PluginLoadFailure.MetadataRejected, "invalid-owner", "manifest");
        var manifest = parsed.Manifest!;
        var root = ManifestPathPolicy.Root(packageRoot);
        if (root is null || ExtensionTrustPolicy.Evaluate(inspectionDecision, root, parsed.ManifestValueSha256!, sourceScope,
            effectiveScopeId, options.ManifestOptions.TrustPolicyRevision) is not null)
            throw Failure(PluginLoadFailure.MetadataRejected, manifest.Id, "inspection-trust");
        CheckExecution(executionDecision, manifest, parsed.ManifestValueSha256!, root, sourceScope, effectiveScopeId);
        if (manifest.RequiredFeatures.Any(feature => !registry.AvailableFeatures.Contains(feature)))
            throw Failure(PluginLoadFailure.MetadataRejected, manifest.Id, "host-features");
        var reservation = Reserve(manifest.Id, registry);
        CancellationTokenSource? operation = null;
        PluginPackageSnapshot? snapshot = null;
        PluginLoadContext? context = null;
        OwnedPluginInstance? instance = null;
        RegistrationScope? scope = null;
        Task<RegistrationScope>? activationOriginal = null;
        try
        {
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var admission = await reader.InspectAsync(metadata, root, sourceScope, effectiveScopeId, inspectionDecision, operation.Token).ConfigureAwait(false);
            if (!admission.MetadataPreflightPassed) throw Failure(PluginLoadFailure.MetadataRejected, manifest.Id, "artifact-preflight");
            if (options.ManifestOptions.HostRuntimeIdentifier != "win-x64" || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                throw Failure(PluginLoadFailure.UnsupportedFramework, manifest.Id, "host-platform");
            ManifestPathPolicy.Inspect(snapshotParent, requireDirectory: true);
            if (new DriveInfo(Path.GetPathRoot(snapshotParent)!).DriveType != DriveType.Fixed)
                throw Failure(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "snapshot-parent");
            snapshot = await PluginPackageSnapshot.CreateAsync(admission, snapshotParent, options.ManifestOptions, operation.Token).ConfigureAwait(false);
            ValidatedPublishedPackage published;
            try { published = PublishedPackageValidator.Validate(snapshot, manifest, options.ManifestOptions.HostRuntimeIdentifier); }
            catch (Exception error) when (error is BadImageFormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            { throw Failure(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "published-metadata", error); }
            operation.Token.ThrowIfCancellationRequested();
            var systemImports = PluginSystemImportProfile.Validate(manifest.Id, published, executionDecision!.SystemImportApprovals);
            context = new(manifest.Id, published, systemImports);
            var assembly = context.LoadEntry(snapshot.Artifacts[manifest.Assembly]);
            var type = assembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (type is null || !type.IsPublic || type.IsAbstract || type.ContainsGenericParameters || !typeof(IPiSharpExtension).IsAssignableFrom(type))
                throw Failure(PluginLoadFailure.InvalidEntryType, manifest.Id, "entry-type");
            operation.Token.ThrowIfCancellationRequested();
            instance = new((IPiSharpExtension)Activator.CreateInstance(type)!);
            operation.Token.ThrowIfCancellationRequested();
            activationOriginal = activateOwner is null ? registry.ActivateAsync(manifest.Id, instance, operation.Token)
                : activateOwner(manifest.Id, instance, operation.Token) ?? throw new InvalidOperationException("Initializer returned no original activation.");
            var activated = await activationOriginal.ConfigureAwait(false);
            if (activated is null || !ReferenceEquals(activated.Registry, registry) || activated.OwnerId != manifest.Id)
                throw new InvalidOperationException("Initializer returned a foreign owner; loader has no retirement authority over it.");
            scope = activated;
            if (scope.State != RegistrationScopeState.Active || scope.ExtensionLifetimeCancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("Initializer must return the exact active owner in the admitted native registry.");
            operation.Token.ThrowIfCancellationRequested();
            var loaded = new LoadedExtension(this, reservation, scope, instance, snapshot, context, retireOwner: retireOwner);
            reservation.Ready.TrySetResult(loaded);
            return loaded;
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            var activationAggregate = activationOriginal?.Exception;
            if (activationAggregate is not null) original = new AggregateException("Original loader activation fault.", activationAggregate, original);
            if (original is SnapshotCreationFailure snapshotFailure)
            { snapshot = snapshotFailure.Snapshot; failures.Add(snapshotFailure); }
            if (scope is not null)
                await CollectRollbackAsync(() => RetireScopeAsync(scope, retireOwner), failures).ConfigureAwait(false);
            if (instance is not null)
                await CollectRollbackAsync(() => instance.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            if (failures.Count == 0)
            {
                if (context is not null) try { context.ReleaseRootsAndRequestUnload(); } catch (Exception error) { failures.Add(error); }
                if (failures.Count == 0 && snapshot is not null)
                    await CollectRollbackAsync(() => snapshot.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            }
            if (failures.Count != 0)
            {
                var cleanup = Failure(PluginLoadFailure.CleanupFailed, manifest.Id, "load-rollback", new AggregateException(failures.Prepend(original)));
                reservation.Ready.TrySetResult(new LoadedExtension(this, reservation, null, instance, snapshot, context, cleanup, retireOwner));
                throw cleanup;
            }
            Release(reservation);
            reservation.Ready.TrySetResult(null);
            if (original is PluginLoadException or OperationCanceledException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
            throw Failure(original is ExtensionRegistrationException ? PluginLoadFailure.InitializationFailed : PluginLoadFailure.LoadFailed,
                manifest.Id, "activate", original);
        }
        finally { operation?.Dispose(); }
    }

    private static async Task CollectRollbackAsync(Func<Task> invoke, List<Exception> failures)
    {
        Task? original = null;
        try { original = invoke() ?? throw new InvalidOperationException("Rollback returned no original."); await original.ConfigureAwait(false); }
        catch (Exception error) { if (original?.Exception is { } aggregate) failures.Add(aggregate); failures.Add(error); }
    }
    internal static async Task RetireScopeAsync(RegistrationScope scope, Func<RegistrationScope, Task>? retireOwner)
    {
        var failures = new List<Exception>();
        if (retireOwner is not null) await CollectRollbackAsync(() => retireOwner(scope), failures).ConfigureAwait(false);
        // The hook may update metadata, but cannot substitute completion for actual owner drainage.
        // Scope disposal is idempotent and its original is always joined, including hook failure.
        await CollectRollbackAsync(() => scope.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
        if (failures.Count != 0) throw new AggregateException("Admitted retirement hook and exact native scope cleanup originals.", failures);
    }

    private void CheckExecution(PluginExecutionDecision? decision, ExtensionManifest manifest, string hash, string root,
        ExtensionSourceScope scope, string effectiveScopeId)
    {
        if (decision is null) throw Failure(PluginLoadFailure.ExecutionTrustRequired, manifest.Id, "execution-trust");
        if (decision.Disposition == PluginExecutionDisposition.Deny) throw Failure(PluginLoadFailure.ExecutionTrustDenied, manifest.Id, "execution-trust");
        if (decision.Disposition != PluginExecutionDisposition.ApprovePublishedFixtureExecution || decision.CanonicalPackageRoot != root ||
            decision.ManifestValueSha256 != hash || decision.SourceScope != scope || decision.EffectiveScopeId != effectiveScopeId ||
            decision.PolicyRevision != options.ManifestOptions.TrustPolicyRevision || decision.HostGeneration != options.HostGeneration ||
            decision.ArtifactHashes.IsDefault || decision.ArtifactHashes.Length != manifest.ArtifactHashes.Length ||
            decision.ArtifactHashes.Where((item, index) => item is null || item.RelativePath != manifest.ArtifactHashes[index].RelativePath ||
                !ManifestPathPolicy.Hash(item.Sha256) || !string.Equals(item.Sha256, manifest.ArtifactHashes[index].Sha256, StringComparison.OrdinalIgnoreCase)).Any())
            throw Failure(PluginLoadFailure.ExecutionTrustBindingMismatch, manifest.Id, "execution-trust");
    }

    private PluginReservation Reserve(string owner, ExtensionRegistry registry)
    {
        lock (gate)
        {
            if (closing) throw Failure(PluginLoadFailure.InactiveLoader, owner, "reserve");
            if (reservations.ContainsKey(owner)) throw Failure(PluginLoadFailure.DuplicateOwner, owner, "reserve");
            if (reservations.Count >= options.MaximumOwnedPackages || options.ManifestOptions.MaximumTotalArtifactBytes > options.MaximumRetainedSnapshotBytes - chargedBytes)
                throw Failure(PluginLoadFailure.LimitExceeded, owner, "reserve");
            var reservation = new PluginReservation(owner, registry);
            reservations.Add(owner, reservation);
            chargedBytes += options.ManifestOptions.MaximumTotalArtifactBytes;
            return reservation;
        }
    }

    internal void Release(PluginReservation reservation)
    {
        lock (gate)
            if (reservations.TryGetValue(reservation.OwnerId, out var current) && ReferenceEquals(current, reservation))
            { reservations.Remove(reservation.OwnerId); chargedBytes -= options.ManifestOptions.MaximumTotalArtifactBytes; }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource settlement;
        PluginReservation[] admitted;
        lock (gate)
        {
            if (reservations.Values.Any(item => CallbackFrame.IsExecuting(item.Registry)))
                throw Failure(PluginLoadFailure.ReentrantDisposal, "loader", "dispose");
            if (disposal is not null) return new(disposal.Task);
            closing = true;
            settlement = disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            admitted = reservations.Values.ToArray();
        }
        _ = CloseAsync(settlement, admitted);
        return new(settlement.Task);
    }

    private async Task CloseAsync(TaskCompletionSource settlement, PluginReservation[] admitted)
    {
        var failures = new List<Exception>();
        try
        {
            try { await lifetime.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            foreach (var reservation in admitted)
            {
                var loaded = await reservation.Ready.Task.ConfigureAwait(false);
                if (loaded is not null) try { await loaded.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally { lifetime.Dispose(); }
        if (failures.Count == 0) settlement.TrySetResult();
        else settlement.TrySetException(Failure(PluginLoadFailure.CleanupFailed, "loader", "dispose", new AggregateException(failures)));
    }

    internal static PluginLoadException Failure(PluginLoadFailure failure, string owner, string operation, Exception? inner = null) => new(failure, owner, operation, inner);
}

internal sealed class PluginReservation(string ownerId, ExtensionRegistry registry)
{
    internal string OwnerId { get; } = ownerId;
    internal ExtensionRegistry Registry { get; } = registry;
    internal TaskCompletionSource<LoadedExtension?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
