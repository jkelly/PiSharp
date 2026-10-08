using System.Collections.Immutable;

using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Storage;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private McpSessionRuntimeFactory? mcpRuntime;
    private readonly object registeredMcpRefreshIdentity = new();
    private bool registeredMcpRefreshAdmitted;

    internal void ConfigureMcpRegistrationRuntime(McpRegisteredProfileAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ConfigureMcpRegistrationRuntime(admission.Bridge, admission.AcquireRuntime);
    }

    // Owner patch: call from the existing admitted composition path before session acquisition.
    // The owning host serializes initializer activation, metadata commit and runtime acquisition.
    internal void ConfigureMcpRegistrationRuntime(McpExtensionRegistrationBridge bridge,
        McpRegisteredProfileRuntimeAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(bridge); ArgumentNullException.ThrowIfNull(admission);
        if (admission.GetInvocationList().Length != 1) throw new ArgumentException("One owning profile admission required.");
        ConfigureMcpRuntime((cwd, generation, nativeRegistry, exactPolicy, token) =>
        {
            var acquire = bridge.CreateRuntimeAcquisition((currentCwd, currentGeneration, catalog, currentToken) =>
                admission(currentCwd, currentGeneration, nativeRegistry, exactPolicy, catalog, currentToken));
            return acquire(cwd, generation, token);
        });
        registeredMcpRefreshAdmitted = true;
        // ConfigureMcpRuntime retains its exact registry/policy checks, independent resource joins,
        // CaptureInitialRuntimeViewOwnership wrapper, profile binder and deferred catalog validation.
    }

    // MCP metadata replacement retains the current native profile view. Native extension reload
    // remains the separate admitted host workflow; this operation acquires no new native authority.
    internal Task<AgentSessionAttachment> RefreshRegisteredMcpRuntimeAsync(AgentSessionAttachment expected,
        CancellationToken admissionToken = default)
    {
        if (!registeredMcpRefreshAdmitted || mcpRuntime is not { } factory)
            throw new InvalidOperationException("Registered MCP refresh was not admitted.");
        var owner = Sessions ?? throw new InvalidOperationException("MCP refresh requires an attached profile.");
        return owner.RunReloadAsync(expected, async (reservation, token) =>
        {
            var acquisition = factory.AcquireAsync(Workspace, reservation.Candidate.Generation, token).AsTask();
            var runtime = await acquisition.ConfigureAwait(false);
            try { reservation.AdmitRuntime(runtime); }
            catch (Exception rejected)
            {
                Task? cleanup = null;
                try { cleanup = runtime.DisposeAsync().AsTask(); await cleanup.ConfigureAwait(false); }
                catch (Exception failure)
                { throw new AggregateException(rejected, cleanup?.Exception ?? failure); }
                throw;
            }
            var catalog = runtime.Registry.RegisteredTools;
            var selection = runtime.Registry.LifetimeToolSelection;
            var active = reservation.ActiveTools.Where(name => catalog.Any(tool => tool.Adapter.Name == name &&
                tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly) && selection?.IsAllowed(name) != false).ToList();
            if (selection?.IncludeDefaultExtensions != false)
                foreach (var tool in catalog.Where(tool => tool.IsExtension && tool.DefaultActive &&
                    tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly && selection?.IsAllowed(tool.Adapter.Name) != false))
                    if (!active.Contains(tool.Adapter.Name, StringComparer.Ordinal)) active.Add(tool.Adapter.Name);
            var drain = reservation.InvalidateAndDrainAsync(); await drain.ConfigureAwait(false);
            var publication = reservation.PublishAsync(runtime, active.ToImmutableArray(), () => { });
            await publication.ConfigureAwait(false);
            var retirement = reservation.CleanupPreviousRuntimeAsync(); await retirement.ConfigureAwait(false);
            return reservation.Candidate;
        }, admissionToken, registeredMcpRefreshIdentity);
    }
    internal void ConfigureMcpRuntime(McpProfileRuntimeAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.GetInvocationList().Length != 1) throw new ArgumentException("One owning admission is required.", nameof(admission));
        if (Sessions is not null || mcpRuntime is not null) throw new InvalidOperationException("Runtime admission must precede session acquisition.");
        mcpRuntime = new(async (cwd, generation, token) =>
        {
            var capturedRegistry = Registry;
            var capturedPolicy = _policy;
            var acquired = await admission(cwd, generation, capturedRegistry, capturedPolicy, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Profile admission returned no owned runtime.");
            try
            {
                if (!ReferenceEquals(acquired.NativeRegistry, capturedRegistry) || !ReferenceEquals(acquired.ExactPolicy, capturedPolicy))
                    throw new InvalidOperationException("Profile admission changed the actual native registry or final policy.");
                ArgumentNullException.ThrowIfNull(acquired.NativeResources);
                ArgumentNullException.ThrowIfNull(acquired.DiscoveryResources);
                if (ReferenceEquals(acquired.NativeResources, acquired.DiscoveryResources))
                    throw new ArgumentException("Native and discovery resource owners must be independent.");
                token.ThrowIfCancellationRequested();
                var admittedBinder = acquired.BindProfileView;
                if (admittedBinder?.GetInvocationList().Length > 1)
                    throw new ArgumentException("One owning profile view binder is required.");
                var ownership = CaptureInitialRuntimeViewOwnership(acquired.NativeResources, generation);
                acquired = acquired with { NativeResources = ownership.Resources, BindProfileView = (owner, attachment) =>
                {
                    admittedBinder?.Invoke(owner, attachment);
                    ownership.BindOwner(owner, attachment);
                }, ServersPromptSource = AdmitMcpServersPromptSource(acquired.ServersPromptSource) };
                if (_deferredCatalogNames is not { } requested) return acquired;
                var prepare = acquired.PrepareDiscovery;
                return acquired with { PrepareDiscovery = (plan, current) =>
                {
                    if (prepare is null || prepare.GetInvocationList().Length != 1)
                        throw new ArgumentException("One discovery preparation is required.");
                    var prepared = prepare(plan, current) ?? throw new InvalidOperationException("Discovery returned no catalog.");
                    foreach (var name in requested.Where(name => !name.Contains('*'))) // Patterns may match nothing.
                        if (!prepared.Registry.RegisteredTools.Any(tool => tool.Adapter.Name == name))
                            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
                    return prepared;
                }};
            }
            catch (Exception rejected)
            {
                var failures = new List<Exception> { rejected };
                if (acquired.DiscoveryResources is { } discovery && !ReferenceEquals(discovery, acquired.NativeResources))
                    await McpSessionRuntimeFactory.JoinDisposalAsync(discovery, "profile rejected discovery", failures).ConfigureAwait(false);
                if (acquired.NativeResources is { } native)
                    await McpSessionRuntimeFactory.JoinDisposalAsync(native, "profile rejected native", failures).ConfigureAwait(false);
                McpSessionRuntimeFactory.Rethrow(failures);
                throw;
            }
        });
    }

    internal PersistentSessionLifecycle CreateLifecycle(Func<long> clock, Func<string> nextId,
        PersistentAgentSessionOptions? options = null, SessionCatalog? catalog = null, SessionStorageBackend? backend = null)
    {
        var configured = options ?? new();
        if (mcpRuntime is not null && configured.LifetimeToolSelection is null && Registry.LifetimeToolSelection is null)
            configured = configured with { LifetimeToolSelection = AllowedToolSelection.Create(configuredDefaults:
                Registry.RegisteredTools.Where(tool => tool.DefaultActive && tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly)
                    .Select(tool => tool.Adapter.Name).ToImmutableArray()) };
        return new(Registry, clock, nextId, configured, catalog: catalog, backend: backend,
            runtimeForAttachment: mcpRuntime is null ? AcquireProfileRuntimeAsync : mcpRuntime.AcquireAsync);
    }

    internal async Task AttachOwnerAsync(PersistentAgentSession session, PersistentAgentSessionOptions? options = null,
        Func<long>? clock = null, Func<string>? nextId = null, IEnumerable<SessionCatalogStore>? catalogStores = null,
        PersistentSessionLifecycle? lifecycle = null)
    {
        if (Sessions is not null) throw new InvalidOperationException("Profile already has a session owner.");
        ConfigureRetrySession(session);
        clock ??= () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nextId ??= () => "replacement-" + Guid.NewGuid().ToString("N");
        var catalog = new SessionCatalog(catalogStores ?? [new("session-directory", Path.GetDirectoryName(session.Path)!)]);
        var owner = await (lifecycle ?? CreateLifecycle(clock, nextId, options, catalog)).AttachAsync(session).ConfigureAwait(false);
        Sessions = owner;
        _policy.ActiveSessionPath = () => owner.Current.Session.Path;
        AttachRuntimeView(owner);
    }

    internal async ValueTask CloseSessionOwnerAsync(PersistentAgentSession session)
    {
        if (Sessions is { } owner) await owner.DisposeAsync().ConfigureAwait(false);
        else await session.DisposeAsync().ConfigureAwait(false);
    }
}
