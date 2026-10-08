using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>Opt-in ordinary resource registration in the existing pre-open catalog pipeline.
/// The caller supplies and owns the registry/scope lifetime, validator, policy, hooks and saver.
/// This object neither acquires channels nor creates another activation/resource lifetime owner.</summary>
public sealed class McpAdmittedResourceRegistration
{
    private readonly ExtensionRegistry registry;
    private readonly RegistrationScope scope;
    private readonly IToolActionPolicy policy;
    private readonly ExtensionToolArgumentValidator validator;
    private readonly McpPreparedHookComposer compose;
    private readonly McpResourceOutputSaver saver;
    private readonly string prefix;
    private readonly McpResourceLimits? limits;
    private readonly object gate = new();
    private readonly AsyncLocal<bool> inside = new();
    private bool attempted, prepared, bound;
    private long generation;
    private ImmutableArray<SessionRegisteredTool> tools = [];
    private ImmutableArray<ExtensionToolRegistrationInfo> metadata = [];
    private McpOwnedResourceDispatch? dispatch;

    public McpAdmittedResourceRegistration(ExtensionRegistry registry, RegistrationScope scope,
        IToolActionPolicy exactPolicy, ExtensionToolArgumentValidator argumentValidator,
        McpPreparedHookComposer hookComposer, McpResourceOutputSaver admittedSaver,
        string registrationPrefix = "mcp.resources", McpResourceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(exactPolicy); ArgumentNullException.ThrowIfNull(argumentValidator);
        ArgumentNullException.ThrowIfNull(hookComposer); ArgumentNullException.ThrowIfNull(admittedSaver);
        if (argumentValidator.GetInvocationList().Length != 1 || hookComposer.GetInvocationList().Length != 1 || admittedSaver.GetInvocationList().Length != 1)
            throw new ArgumentException("One admitted validator, hook composer and output saver required.");
        this.registry = registry; this.scope = scope; policy = exactPolicy; validator = argumentValidator;
        compose = hookComposer; saver = admittedSaver; prefix = registrationPrefix; this.limits = limits;
        // Validate names/limits/delegates before any catalog effects.
        _ = new McpOwnedResourceDispatch(_ => [], saver, limits).CreateDescriptors(prefix);
    }

    public McpOwnedResourceDispatch Dispatch
    { get { lock (gate) return dispatch ?? throw new InvalidOperationException("No resource descriptors were prepared."); } }

    internal SessionRuntimeRegistry Prepare(McpToolCatalogPlan plan, SessionRuntimeRegistry current,
        ImmutableArray<McpPreOpenServerCapture> captures, IToolActionPolicy exactPolicy)
    {
        RefuseReentry();
        lock (gate)
        {
            if (attempted) throw new InvalidOperationException("Resource registration cannot be reused across acquisitions.");
            attempted = true;
        }
        var prior = inside.Value; inside.Value = true;
        try
        {
            scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(policy, exactPolicy) || !current.UsesFinalActionPolicy(policy) || current.InvocationOwnerGeneration is not null)
                throw new InvalidOperationException("Resources require the actual unbound native catalog and final policy.");
            if (plan.ResourceToolsExposure is not { } resourceExposure)
            { lock (gate) prepared = true; return current; }
            if (captures.IsEmpty || captures.Any(capture => capture.ReservedGeneration != captures[0].ReservedGeneration))
                throw new InvalidOperationException("Resources require actual captures for one reserved generation.");
            var resourceCaptures = captures.Where(capture => capture.CatalogSnapshot is { Connected: true, HasResources: true }).ToImmutableArray();
            var candidate = new McpOwnedResourceDispatch(invocation => resourceCaptures.Select(capture => capture.CaptureResourceServer(invocation)).ToArray(), saver, limits);
            var descriptors = candidate.CreateDescriptors(prefix, McpCatalogPlanner.ToToolExposure(resourceExposure));
            if (descriptors.Any(descriptor => current.RegisteredTools.Any(tool => tool.Adapter.Name == descriptor.Name)))
                throw new InvalidOperationException("Resource tools collide with an existing executable binding.");
            var replacement = registry.PrepareToolCatalogReplacement(scope, [], descriptors, registry.CaptureSnapshot());
            var binding = new ExtensionAgentBinding(registry, policy, validator, options: new() { ActiveToolNames = [] },
                sessionCancellationToken: scope.ExtensionLifetimeCancellationToken, capturedSnapshot: replacement.PreviewSnapshot);
            var ids = descriptors.Select(descriptor => descriptor.RegistrationId).ToImmutableHashSet(StringComparer.Ordinal);
            var added = binding.Registrations.Select((tool, index) => (Tool: tool, Index: index))
                .Where(row => row.Tool.OwnerId == scope.OwnerId && row.Tool.OwnerGeneration == scope.OwnerGeneration && ids.Contains(row.Tool.RegistrationId)).ToImmutableArray();
            if (added.Length != descriptors.Length) throw new InvalidOperationException("Resource preparation changed actual registration ownership.");
            var preparedTools = added.Select(row => new SessionRegisteredTool(binding.RegisteredToolDeclarations[row.Index], binding.Adapters[row.Index])
            { Exposure = row.Tool.Exposure, Namespace = row.Tool.Namespace, DefaultActive = row.Tool.DefaultActive, IsExtension = true,
                PrepareLoadout = binding.GetLoadoutPreparation(row.Tool.Name) }).ToImmutableArray();
            var result = current.WithToolCatalog(current.RegisteredTools.AddRange(preparedTools), compose(current, binding));
            scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
            replacement.Commit();
            lock (gate)
            {
                generation = captures[0].ReservedGeneration; tools = preparedTools;
                metadata = added.Select(row => row.Tool).ToImmutableArray(); dispatch = candidate; prepared = true;
            }
            ValidateCatalog(result); return result;
        }
        finally { inside.Value = prior; }
    }

    internal void ValidateCatalog(SessionRuntimeRegistry current)
    {
        lock (gate)
        {
            if (!prepared || scope.ExtensionLifetimeCancellationToken.IsCancellationRequested || !current.UsesFinalActionPolicy(policy))
                throw new InvalidOperationException("Resource registration is stale or belongs to another final policy.");
            var snapshot = registry.CaptureSnapshot();
            if (metadata.Any(expected => !snapshot.Tools.Contains(expected)) || tools.Any(expected =>
                !current.UsesCapturedToolBinding(expected.Adapter.Name, expected.Declaration, expected.Adapter) ||
                !current.RegisteredTools.Any(actual => ReferenceEquals(actual.Declaration, expected.Declaration) &&
                    actual.Exposure == expected.Exposure && actual.Namespace == expected.Namespace && actual.DefaultActive == expected.DefaultActive &&
                    actual.IsExtension && ReferenceEquals(actual.PrepareLoadout, expected.PrepareLoadout))))
                throw new InvalidOperationException("Resource preparation lost its actual scope/metadata/declaration/adapter binding.");
        }
    }

    internal void Bind(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        RefuseReentry();
        lock (gate)
        {
            if (bound || !prepared) throw new InvalidOperationException("Resources require one preparation and one binding.");
            ValidateCatalog(owner.CaptureToolCatalogRegistryForBinding(attachment));
            if (dispatch is not null && attachment.Generation != generation)
                throw new InvalidOperationException("Resource reserved generation differs from its actual attachment.");
            bound = true; dispatch?.Bind(owner, attachment, scope);
        }
    }
    private void RefuseReentry()
    { if (inside.Value) throw new InvalidOperationException("Resource preparation callback cannot reenter its owning registration."); }
}
