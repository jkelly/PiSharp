using System.Collections.Immutable;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Registration;

namespace PiSharp.Cli.Mcp;

/// <summary>Explicit host acquisition receives the exact committed metadata catalog; metadata never grants acquisition.</summary>
public delegate ValueTask<McpSessionRuntimeAdmission> McpExtensionRuntimeAcquisition(
    string cwd, long generation, McpServerCatalog catalog, CancellationToken token);
public sealed record McpExtensionRegistrationSnapshot(long Revision, McpServerCatalog Catalog,
    ImmutableArray<McpRegisteredServer> RegisteredServers);

/// <summary>Concrete owner-bound registration metadata publisher feeding the existing pre-open/runtime factory.
/// Host scope activation/retirement and runtime replacement must be serialized by the owning host.</summary>
public sealed partial class McpExtensionRegistrationBridge
{
    private readonly object gate = new();
    private readonly ExtensionRegistry registry;
    private readonly McpRegistrationCatalog registrations;
    private readonly HashSet<RegistrationScope> owners = new(ReferenceEqualityComparer.Instance);
    private McpExtensionRegistrationSnapshot committed;
    private RegistrationScope? retiring;

    public McpExtensionRegistrationBridge(ExtensionRegistry registry, McpLoadedConfiguration configured)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(configured);
        this.registry = registry;
        var initial = McpCatalogPlanner.ComposeServers(configured, []);
        if (initial.Servers.Length > McpAdmittedActivationHost.MaximumServers)
            throw new ArgumentException("Registration catalog exceeds admitted host server bound.");
        committed = new(0, initial, []);
        registrations = new(configured, AssertActive, Publish);
    }

    /// <summary>Bind the actual already activated scope. No initializer-stage or foreign scope is admitted.</summary>
    public IMcpServerRegistrationFacade BindOwner(RegistrationScope scope, string admittedExtensionPath)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(admittedExtensionPath) || !Path.IsPathFullyQualified(admittedExtensionPath))
            throw new ArgumentException("An admitted absolute extension path is required.", nameof(admittedExtensionPath));
        // The genuine registry transaction preparation checks registry/scope reference and active state.
        // The empty preview is never committed and causes no registration mutation.
        _ = registry.PrepareToolCatalogReplacement(scope, [], [], registry.CaptureSnapshot());
        lock (gate)
        {
            if (!owners.Contains(scope) && owners.Count >= McpAdmittedActivationHost.MaximumServers)
                throw new InvalidOperationException("Registration owner bound reached.");
            owners.Add(scope);
        }
        return registrations.BindOwner(scope, admittedExtensionPath);
    }

    private void AssertActive(IExtensionRegistry owner)
    {
        if (owner is not RegistrationScope scope) throw new InvalidOperationException("Actual native registration scope required.");
        lock (gate) if (!owners.Contains(scope) || ReferenceEquals(scope, retiring))
            throw new InvalidOperationException("Scope is not an admitted active bridge owner.");
        _ = registry.PrepareToolCatalogReplacement(scope, [], [], registry.CaptureSnapshot());
    }

    private McpRegistrationPublicationReceipt Publish(McpRegistrationPublication publication)
    {
        lock (gate)
        {
            if (publication.Owner is not RegistrationScope scope || !owners.Contains(scope) ||
                publication.Revision != checked(committed.Revision + 1) ||
                publication.Catalog.Servers.Length > McpAdmittedActivationHost.MaximumServers)
                throw new InvalidOperationException("Registration publication changed owner, revision or host bound.");
            if (!ReferenceEquals(scope, retiring))
                _ = registry.PrepareToolCatalogReplacement(scope, [], [], registry.CaptureSnapshot());
            var next = new McpExtensionRegistrationSnapshot(publication.Revision, publication.Catalog, publication.Current);
            committed = next; // Fully allocated metadata snapshot publishes before the exact receipt.
            return new(scope, next.Revision, true);
        }
    }

    public McpExtensionRegistrationSnapshot CaptureSnapshot() { lock (gate) return committed; }

    /// <summary>Host passes the stable actual scope-disposal original; metadata retires only after successful join.</summary>
    public async Task RetireClosedOwnerAsync(RegistrationScope scope, Task actualScopeDisposal)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(actualScopeDisposal);
        lock (gate) if (!owners.Contains(scope)) return;
        if (!scope.ExtensionLifetimeCancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("Host must first request its actual scope disposal.");
        var capturedOriginal = scope.DisposeAsync().AsTask();
        if (!ReferenceEquals(capturedOriginal, actualScopeDisposal))
            throw new InvalidOperationException("Retirement requires the actual stable scope disposal Task.");
        try { await capturedOriginal.ConfigureAwait(false); }
        catch (Exception error)
        {
            throw new McpFactoryDisposalException("bridge scope", capturedOriginal,
                capturedOriginal.IsFaulted ? capturedOriginal.Exception! : error);
        }
        lock (gate)
        {
            if (!owners.Contains(scope)) return;
            if (retiring is not null) throw new InvalidOperationException("One host-owned retirement at a time.");
            retiring = scope;
        }
        try { registrations.RetireOwner(scope); lock (gate) owners.Remove(scope); }
        finally { lock (gate) retiring = null; }
    }

    /// <summary>Creates the actual existing factory, not a parallel session/transport implementation.
    /// Every acquire receives one coherent registered/configured snapshot before activation/discovery.</summary>
    public McpSessionRuntimeFactory CreateRuntimeFactory(McpExtensionRuntimeAcquisition acquire)
        => new(CreateRuntimeAcquisition(acquire));

    /// <summary>Use inside existing profile admission to preserve its native registry/policy and runtime-view binder.</summary>
    public Func<string, long, CancellationToken, ValueTask<McpSessionRuntimeAdmission>> CreateRuntimeAcquisition(McpExtensionRuntimeAcquisition acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        if (acquire.GetInvocationList().Length != 1) throw new ArgumentException("One explicit owning acquisition required.");
        return async (cwd, generation, token) =>
        {
            var snapshot = CaptureSnapshot();
            var admission = await acquire(cwd, generation, snapshot.Catalog, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Host returned no owned runtime admission.");
            if (ReferenceEquals(admission.Catalog, snapshot.Catalog)) return admission;
            var failures = new List<Exception> { new InvalidOperationException("Host substituted the committed MCP metadata catalog.") };
            if (admission.DiscoveryResources is { } discovery && !ReferenceEquals(discovery, admission.NativeResources))
                await McpSessionRuntimeFactory.JoinDisposalAsync(discovery, "bridge mismatched discovery", failures).ConfigureAwait(false);
            if (admission.NativeResources is { } native)
                await McpSessionRuntimeFactory.JoinDisposalAsync(native, "bridge mismatched native", failures).ConfigureAwait(false);
            McpSessionRuntimeFactory.Rethrow(failures); throw failures[0];
        };
    }
}
