using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Cli.Mcp;

/// <summary>Explicit per-server acquisition admission. Configuration is input to validation, never a grant.</summary>
public delegate Task<McpPreOpenServerCapture> McpAdmittedPreOpenCaptureFactory(McpServerEntry entry,
    SessionRuntimeRegistry currentRegistry, CancellationToken token);
public sealed record McpServerActivationAdmission(string Name, Action<McpServerEntry> ValidateConfiguration,
    McpAdmittedPreOpenCaptureFactory AcquireCapture);
public sealed record McpPreparedDiscoveryCatalog(SessionRuntimeRegistry Registry,
    ImmutableArray<McpPreparedDiscoveryIdentity> Identities);
public delegate McpPreparedDiscoveryCatalog McpDiscoveryCatalogPreparation(McpToolCatalogPlan plan, SessionRuntimeRegistry acquiredRegistry);

/// <summary>Fresh pre-open composition of explicitly admitted servers. No ambient process, HTTP,
/// permission, credentials, registry or semantic executor is acquired here.</summary>
public sealed class McpAdmittedActivationHost : IAsyncDisposable
{
    public const int MaximumServers = 128;
    private readonly ImmutableArray<McpPreOpenServerCapture> captures;
    private readonly ImmutableArray<McpPreparedDiscoveryIdentity> identities;
    private readonly McpAdmittedResourceRegistration? resources;
    private readonly object gate = new();
    private readonly AsyncLocal<bool> inside = new();
    private ImmutableArray<IAsyncDisposable> transferred = [];
    private bool transferAttempted, bindAttempted;
    private Task? close;
    public SessionRuntimeRegistry Registry { get; }
    public McpToolCatalogPlan CatalogPlan { get; }
    private McpAdmittedActivationHost(ImmutableArray<McpPreOpenServerCapture> captures,
        SessionRuntimeRegistry registry, McpToolCatalogPlan plan, ImmutableArray<McpPreparedDiscoveryIdentity> identities,
        McpAdmittedResourceRegistration? resources)
    { this.captures = captures; Registry = registry; CatalogPlan = plan; this.identities = identities; this.resources = resources; }

    /// <summary>All enabled entries validate before the first factory call. Each factory must transfer
    /// a fresh capture and clean up allocations before throwing. Discovery validation must use opaque
    /// prepared semantic identities against the supplied final registry before historical resolution.</summary>
    public static async Task<McpAdmittedActivationHost> AcquireAsync(McpServerCatalog catalog,
        ImmutableArray<McpServerActivationAdmission> admissions, SessionRuntimeRegistry baseRegistry,
        bool autoEnableCodemode, IToolActionPolicy exactPolicy, McpDiscoveryCatalogPreparation prepareDiscovery,
        CancellationToken token = default, McpAdmittedResourceRegistration? resourceRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(baseRegistry);
        ArgumentNullException.ThrowIfNull(exactPolicy); ArgumentNullException.ThrowIfNull(prepareDiscovery);
        if (!baseRegistry.UsesFinalActionPolicy(exactPolicy)) throw new ArgumentException("Activation requires the actual final-action policy.", nameof(exactPolicy));
        if (prepareDiscovery.GetInvocationList().Length != 1) throw new ArgumentException("One owning discovery preparation is required.", nameof(prepareDiscovery));
        if (catalog.Servers.IsDefault || catalog.Servers.Length > MaximumServers || admissions.IsDefault || admissions.Length > MaximumServers)
            throw new ArgumentException("Initialized bounded server inputs are required.");
        token.ThrowIfCancellationRequested();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var admitted = new Dictionary<string, McpServerActivationAdmission>(StringComparer.Ordinal);
        foreach (var admission in admissions)
        {
            ArgumentNullException.ThrowIfNull(admission); ArgumentException.ThrowIfNullOrWhiteSpace(admission.Name);
            ArgumentNullException.ThrowIfNull(admission.ValidateConfiguration); ArgumentNullException.ThrowIfNull(admission.AcquireCapture);
            if (admission.ValidateConfiguration.GetInvocationList().Length != 1 || admission.AcquireCapture.GetInvocationList().Length != 1 || !admitted.TryAdd(admission.Name, admission))
                throw new ArgumentException("Server admission must have one validator, one fresh factory and a unique name.", nameof(admissions));
        }
        var enabled = ImmutableArray.CreateBuilder<(McpServerEntry Entry, McpServerActivationAdmission Admission)>();
        foreach (var entry in catalog.Servers)
        {
            ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(entry.Config);
            if (!names.Add(entry.Name)) throw new ArgumentException("Duplicate server catalog name.", nameof(catalog));
            if (!entry.Config.Enabled) continue;
            if (!admitted.TryGetValue(entry.Name, out var admission)) throw new InvalidOperationException("Enabled MCP server has no explicit host admission: " + entry.Name);
            admission.ValidateConfiguration(entry); token.ThrowIfCancellationRequested(); enabled.Add((entry, admission));
        }
        var acquired = ImmutableArray.CreateBuilder<McpPreOpenServerCapture>(); var current = baseRegistry;
        try
        {
            foreach (var item in enabled)
            {
                token.ThrowIfCancellationRequested();
                var capture = await item.Admission.AcquireCapture(item.Entry, current, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Server admission returned no owned capture.");
                if (acquired.Any(previous => ReferenceEquals(previous, capture))) throw new InvalidOperationException("Server admission reused a capture.");
                acquired.Add(capture);
                if (!ReferenceEquals(capture.CatalogSnapshot.Entry, item.Entry) || !capture.Registry.UsesFinalActionPolicy(exactPolicy))
                    throw new InvalidOperationException("Server acquisition changed the validated entry or final-action policy.");
                current = capture.Registry; token.ThrowIfCancellationRequested();
            }
            var rows = acquired.ToImmutable();
            if (rows.Select(capture => capture.ReservedGeneration).Distinct().Count() > 1)
                throw new InvalidOperationException("Composed MCP captures belong to different reserved generations.");
            var plan = McpCatalogPlanner.Plan(rows.Select(capture => capture.CatalogSnapshot), autoEnableCodemode);
            // Resource adapters enter the ordinary catalog first. Genuine code/search receipts
            // therefore capture the final metadata snapshot, including those resource bindings.
            if (resourceRegistration is not null) current = resourceRegistration.Prepare(plan, current, rows, exactPolicy);
            token.ThrowIfCancellationRequested();
            var prepared = prepareDiscovery(plan, current) ?? throw new InvalidOperationException("Discovery preparation returned no actual catalog.");
            ArgumentNullException.ThrowIfNull(prepared.Registry);
            if (!prepared.Registry.UsesFinalActionPolicy(exactPolicy) || prepared.Registry.InvocationOwnerGeneration != current.InvocationOwnerGeneration ||
                current.RegisteredTools.Any(original => !prepared.Registry.UsesCapturedToolBinding(
                    original.Declaration.Value.GetProperty("name").GetString()!, original.Declaration, original.Adapter) ||
                    !prepared.Registry.RegisteredTools.Any(tool => ReferenceEquals(tool.Declaration, original.Declaration) &&
                        tool.Exposure == original.Exposure && tool.Namespace == original.Namespace && tool.DefaultActive == original.DefaultActive &&
                        tool.IsExtension == original.IsExtension && ReferenceEquals(tool.PrepareLoadout, original.PrepareLoadout))))
                throw new InvalidOperationException("Discovery preparation changed existing bindings, policy or reserved generation.");
            McpPreparedDiscoveryIdentity.ValidateRequired(plan, prepared.Registry, prepared.Identities); token.ThrowIfCancellationRequested();
            resourceRegistration?.ValidateCatalog(prepared.Registry);
            if (!rows.IsEmpty && prepared.Identities.Any(identity => identity.ReservedGeneration != rows[0].ReservedGeneration))
                throw new InvalidOperationException("Discovery and MCP captures belong to different reserved generations.");
            return new(rows, prepared.Registry, plan, prepared.Identities, resourceRegistration);
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            for (var index = acquired.Count - 1; index >= 0; index--)
                try { await acquired[index].DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(original).Throw();
            throw new AggregateException(failures);
        }
    }

    /// <summary>Transfer once before session open. The returned lease owns the same stable capture
    /// close originals, including rejected historical resolution. It creates no second physical owner.</summary>
    public SessionRuntimeLease TransferRuntimeOwnership()
    {
        RefuseReentry();
        lock (gate)
        {
            if (transferAttempted || close is not null) throw new InvalidOperationException("Activation ownership has already been transferred or closed.");
            transferAttempted = true;
            var originals = ImmutableArray.CreateBuilder<IAsyncDisposable>(captures.Length);
            try
            {
                foreach (var capture in captures) originals.Add(capture.TransferRuntimeOwnership());
                transferred = originals.ToImmutable();
                return new(Registry, this);
            }
            catch { transferred = originals.ToImmutable(); throw; }
        }
    }

    /// <summary>Bind once to the actual committed attachment before exposing calls. Partial failure
    /// remains an owning failure; dispose joins all captures and does not invent a successful binding receipt.</summary>
    public void BindOwner(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(attachment); RefuseReentry();
        lock (gate)
        {
            if (!transferAttempted || transferred.Length != captures.Length || bindAttempted || close is not null)
                throw new InvalidOperationException("Activation requires one completed transfer followed by one owner binding.");
            if (!ReferenceEquals(owner.Current, attachment) || attachment.LifetimeToken.IsCancellationRequested ||
                captures.Any(capture => capture.ReservedGeneration != attachment.Generation))
                throw new InvalidOperationException("Activation requires its exact current reserved generation.");
            bindAttempted = true;
            var prior = inside.Value; inside.Value = true;
            try
            {
                McpPreparedDiscoveryIdentity.ValidateBound(CatalogPlan, owner.CaptureToolCatalogRegistryForBinding(attachment), attachment.Generation, identities);
                foreach (var capture in captures) capture.BindOwner(owner, attachment);
                resources?.Bind(owner, attachment);
                foreach (var identity in identities) identity.BindSemanticOwner(owner, attachment);
            }
            finally { inside.Value = prior; }
        }
    }

    public ValueTask DisposeAsync()
    {
        RefuseReentry();
        lock (gate) { close ??= CloseCoreAsync(); return new(close); }
    }
    private async Task CloseCoreAsync()
    {
        await Task.Yield(); // All borrowed close callbacks execute after the lifecycle state lock is released.
        var prior = inside.Value; inside.Value = true;
        var failures = new List<Exception>();
        try
        {
            // Transferred wrappers join already-admitted retirement originals without reacquiring
            // lifecycle admission from within the coordinator's runtime-release callback.
            for (var index = captures.Length - 1; index >= 0; index--)
                try
                {
                    if (index < transferred.Length) await transferred[index].DisposeAsync().ConfigureAwait(false);
                    else await captures[index].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) { failures.Add(error); }
        }
        finally { inside.Value = prior; }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }
    private void RefuseReentry()
    { if (inside.Value) throw new InvalidOperationException("Activation callback cannot await its own lifecycle settlement."); }
}
