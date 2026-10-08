using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>One configured name/transport and an explicit channel capability. Validation must
/// compare the endpoint/command/headers against the caller's admitted authority before any acquisition.</summary>
public sealed record McpApplicationServerAdmission(string Name, McpTransportKind Transport,
    Action<McpServerEntry> ValidateConfiguration,
    Func<McpServerEntry, long, McpAdmittedChannelFactory> CreateChannelFactory);

public sealed record McpApplicationGenerationRequest(string Cwd, long Generation,
    SessionRuntimeRegistry NativeRegistry, IToolActionPolicy ExactPolicy, McpServerCatalog Catalog);

/// <summary>The actual independently owned empty server registry/scope for pre-open discovery.
/// The returned generation NativeResources owns every server scope and registry.</summary>
public sealed record McpApplicationServerOwner(McpServerEntry Entry, ExtensionRegistry Registry,
    RegistrationScope Scope, ExtensionToolArgumentValidator ValidateArguments,
    McpRuntimeOptions Options, McpPreparedHookComposer ComposeHooks);

/// <summary>The supplier joins allocations on failure. On return, ownership transfers even if
/// validation fails. DiscoveryResources owns the genuine semantic registration scope/executor;
/// NativeResources owns the server scopes. Neither may reuse a previous generation's owner.</summary>
public sealed record McpApplicationGenerationResources(McpApplicationGenerationRequest Request,
    IAsyncDisposable NativeResources, IAsyncDisposable DiscoveryResources,
    ImmutableArray<McpApplicationServerOwner> Servers, McpDiscoveryCatalogPreparation PrepareDiscovery)
{
    public McpAdmittedResourceRegistration? ResourceRegistration { get; init; }
    public Action<ReplaceableAgentSession, AgentSessionAttachment>? BindProfileView { get; init; }
}

public delegate ValueTask<McpApplicationGenerationResources> McpApplicationGenerationAcquisition(
    McpApplicationGenerationRequest request, CancellationToken token);

/// <summary>Reusable concrete application mapping over loaded original server configuration.
/// Registered metadata is captured by the genuine bridge on every initial acquisition/reload.
/// Channels, scope owners, semantics and credentials remain explicit caller capabilities; this
/// installation never creates a client/process or reads configuration/credentials from ambient state.</summary>
public sealed class McpApplicationHostInstallation
{
    private static readonly ConditionalWeakTable<IAsyncDisposable, object> transferredOwners = new();
    private static readonly object ownershipGate = new();
    private readonly ImmutableDictionary<string, McpApplicationServerAdmission> servers;
    private readonly McpApplicationGenerationAcquisition acquireGeneration;
    private readonly bool autoEnableCodemode;
    public McpExtensionRegistrationBridge Bridge { get; }

    public McpApplicationHostInstallation(ExtensionRegistry actualNativeRegistry,
        McpLoadedConfiguration loadedConfiguration, ImmutableArray<McpApplicationServerAdmission> serverAdmissions,
        McpApplicationGenerationAcquisition acquireGeneration)
    {
        ArgumentNullException.ThrowIfNull(actualNativeRegistry);
        ArgumentNullException.ThrowIfNull(loadedConfiguration);
        ArgumentNullException.ThrowIfNull(acquireGeneration);
        if (acquireGeneration.GetInvocationList().Length != 1 || serverAdmissions.IsDefault ||
            serverAdmissions.Length > McpAdmittedActivationHost.MaximumServers)
            throw new ArgumentException("Bounded single-owner application admissions required.");
        var mapped = ImmutableDictionary.CreateBuilder<string, McpApplicationServerAdmission>(StringComparer.Ordinal);
        foreach (var server in serverAdmissions)
        {
            ArgumentNullException.ThrowIfNull(server); ArgumentException.ThrowIfNullOrWhiteSpace(server.Name);
            ArgumentNullException.ThrowIfNull(server.ValidateConfiguration); ArgumentNullException.ThrowIfNull(server.CreateChannelFactory);
            if (!Enum.IsDefined(server.Transport) || server.ValidateConfiguration.GetInvocationList().Length != 1 ||
                server.CreateChannelFactory.GetInvocationList().Length != 1 || !mapped.TryAdd(server.Name, server))
                throw new ArgumentException("Unique named single-owner HTTP/process admissions required.");
        }
        servers = mapped.ToImmutable(); this.acquireGeneration = acquireGeneration;
        autoEnableCodemode = loadedConfiguration.EffectiveAutoEnableCodemode;
        Bridge = new(actualNativeRegistry, loadedConfiguration);
    }

    public McpRegisteredProfileAdmission CreateRegisteredAdmission() => new(Bridge, AcquireAsync);

    private async ValueTask<McpSessionRuntimeAdmission> AcquireAsync(string cwd, long generation,
        SessionRuntimeRegistry nativeRegistry, IToolActionPolicy policy, McpServerCatalog catalog, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd) || generation < 1)
            throw new ArgumentException("Actual workspace and reserved generation required.");
        ArgumentNullException.ThrowIfNull(nativeRegistry); ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!nativeRegistry.UsesFinalActionPolicy(policy) || nativeRegistry.InvocationOwnerGeneration is not null)
            throw new InvalidOperationException("Installation requires the actual unbound profile registry/policy.");
        token.ThrowIfCancellationRequested();
        var enabled = catalog.Servers.Where(entry => entry.Config.Enabled).ToImmutableArray();
        var channels = new Dictionary<string, McpAdmittedChannelFactory>(StringComparer.Ordinal);
        foreach (var entry in enabled)
        {
            if (!servers.TryGetValue(entry.Name, out var capability) || capability.Transport != entry.Config.Transport)
                throw new InvalidOperationException("No admitted application transport for " + entry.Name);
            try { capability.ValidateConfiguration(entry); }
            catch (OperationCanceledException error)
            { throw new McpProfileResourceAcquisitionException("application configuration", null, error); }
            token.ThrowIfCancellationRequested();
        }
        // All configuration validators run before even a channel-factory construction callback.
        foreach (var entry in enabled)
        {
            McpAdmittedChannelFactory factory;
            try { factory = servers[entry.Name].CreateChannelFactory(entry, generation)
                ?? throw new InvalidOperationException("Application channel factory was absent."); }
            catch (OperationCanceledException error)
            { throw new McpProfileResourceAcquisitionException("application channel construction", null, error); }
            if (factory.GetInvocationList().Length != 1) throw new ArgumentException("One owning channel factory required.");
            channels.Add(entry.Name, factory); token.ThrowIfCancellationRequested();
        }
        var request = new McpApplicationGenerationRequest(cwd, generation, nativeRegistry, policy, catalog);
        var resources = await ReadGenerationAsync(request, token).ConfigureAwait(false);
        var ownsNative = false; var ownsDiscovery = false;
        try
        {
            ArgumentNullException.ThrowIfNull(resources);
            // Reject reuse without disposing a previous generation's still-owned resources.
            lock (ownershipGate)
            {
                if (resources.NativeResources is { } native && !transferredOwners.TryGetValue(native, out _))
                { transferredOwners.Add(native, new()); ownsNative = true; }
                if (resources.DiscoveryResources is { } discovery && !transferredOwners.TryGetValue(discovery, out _))
                { transferredOwners.Add(discovery, new()); ownsDiscovery = true; }
            }
            ArgumentNullException.ThrowIfNull(resources.NativeResources); ArgumentNullException.ThrowIfNull(resources.DiscoveryResources);
            if (!ReferenceEquals(resources.Request, request) || ReferenceEquals(resources.NativeResources, resources.DiscoveryResources) ||
                !ownsNative || !ownsDiscovery ||
                resources.Servers.IsDefault || resources.Servers.Length != enabled.Length ||
                resources.PrepareDiscovery is null || resources.PrepareDiscovery.GetInvocationList().Length != 1 ||
                resources.BindProfileView?.GetInvocationList().Length > 1)
                throw new InvalidOperationException("Generation admission changed its captured request or independent ownership.");
            var admissions = ImmutableArray.CreateBuilder<McpServerActivationAdmission>();
            var owners = new HashSet<ExtensionRegistry>(ReferenceEqualityComparer.Instance);
            foreach (var entry in enabled)
            {
                var matches = resources.Servers.Where(owner => ReferenceEquals(owner.Entry, entry)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("One exact server owner required.");
                var owner = matches[0];
                ArgumentNullException.ThrowIfNull(owner.Registry); ArgumentNullException.ThrowIfNull(owner.Scope);
                ArgumentNullException.ThrowIfNull(owner.ValidateArguments); ArgumentNullException.ThrowIfNull(owner.ComposeHooks);
                ArgumentNullException.ThrowIfNull(owner.Options);
                if (!owners.Add(owner.Registry) || owner.Options.Generation != generation ||
                    owner.ValidateArguments.GetInvocationList().Length != 1 || owner.ComposeHooks.GetInvocationList().Length != 1)
                    throw new InvalidOperationException("Fresh exact server scopes and reserved options required.");
                var channel = channels[entry.Name];
                void ValidateInstalled(McpServerEntry actual)
                {
                    if (!ReferenceEquals(actual, entry)) throw new InvalidOperationException("Installed admission changed its exact entry.");
                    try { servers[entry.Name].ValidateConfiguration(actual); }
                    catch (OperationCanceledException error)
                    { throw new McpProfileResourceAcquisitionException("application installed configuration", null, error); }
                }
                admissions.Add(new(entry.Name, ValidateInstalled,
                    (actual, current, cancellation) => McpPreOpenServerCapture.AcquireAsync(actual, owner.Registry,
                        owner.Scope, current, policy, owner.ValidateArguments, channel, owner.Options, owner.ComposeHooks, cancellation)));
            }
            token.ThrowIfCancellationRequested();
            return new(nativeRegistry, resources.NativeResources, resources.DiscoveryResources, policy, catalog,
                admissions.ToImmutable(), autoEnableCodemode, resources.PrepareDiscovery)
            { ResourceRegistration = resources.ResourceRegistration, BindProfileView = resources.BindProfileView };
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (ownsDiscovery && resources?.DiscoveryResources is { } discovery)
                await McpSessionRuntimeFactory.JoinDisposalAsync(discovery, "application rejected discovery", failures).ConfigureAwait(false);
            if (ownsNative && resources?.NativeResources is { } native)
                await McpSessionRuntimeFactory.JoinDisposalAsync(native, "application rejected native", failures).ConfigureAwait(false);
            McpSessionRuntimeFactory.Rethrow(failures); throw;
        }
    }

    private async Task<McpApplicationGenerationResources> ReadGenerationAsync(McpApplicationGenerationRequest request, CancellationToken token)
    {
        Task<McpApplicationGenerationResources>? original = null;
        try { original = acquireGeneration(request, token).AsTask(); return await original.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (error is OperationCanceledException canceled && original?.IsCanceled == true &&
                token.IsCancellationRequested && canceled.CancellationToken == token) throw;
            throw new McpProfileResourceAcquisitionException("application generation", original,
                original is { IsFaulted: true } ? original.Exception! : error);
        }
    }
}
