using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

public enum McpDiscoveryKind { Codemode, ToolSearch }

/// <summary>Opaque receipt for an explicitly host-admitted executable discovery binding.
/// It captures the actual owner, metadata snapshot, adapter and native catalog; metadata alone cannot mint a receipt.</summary>
public sealed class McpPreparedDiscoveryIdentity
{
    private readonly RegistrationScope scope;
    private readonly ExtensionRegistry registry;
    private readonly ExtensionRegistrySnapshot snapshot;
    private readonly SessionRuntimeRegistry catalog;
    private readonly IPreparedToolAdapter adapter;
    private readonly IToolActionPolicy policy;
    private readonly long sessionGeneration;
    private readonly McpAdmittedDiscoveryRegistration semantic;
    private readonly JsonData declaration;
    public McpDiscoveryKind Kind { get; }
    /// <summary>Explicit reserved target metadata. It grants no bound invocation authority.</summary>
    public long ReservedGeneration => sessionGeneration;
    private McpPreparedDiscoveryIdentity(McpDiscoveryKind kind, RegistrationScope scope, ExtensionRegistry registry,
        ExtensionRegistrySnapshot snapshot, SessionRuntimeRegistry catalog, IPreparedToolAdapter adapter,
        IToolActionPolicy policy, long sessionGeneration, McpAdmittedDiscoveryRegistration semantic, JsonData declaration)
    {
        Kind = kind; this.scope = scope; this.registry = registry; this.snapshot = snapshot;
        this.catalog = catalog; this.adapter = adapter; this.policy = policy; this.sessionGeneration = sessionGeneration;
        this.semantic = semantic;
        this.declaration = declaration;
    }

    /// <summary>The host must explicitly supply a real code/search implementation through its admitted
    /// extension callback and prepared binding. This method verifies ownership, not JavaScript/BM25 semantics.</summary>
    public static McpPreparedDiscoveryIdentity Capture(McpAdmittedDiscoveryRegistration semantic,
        ExtensionAgentBinding admittedBinding, SessionRuntimeRegistry catalog,
        IToolActionPolicy exactPolicy, long expectedSessionGeneration)
    {
        ArgumentNullException.ThrowIfNull(semantic); semantic.Validate();
        var scope = semantic.Scope; var registry = semantic.Registry; var kind = semantic.Kind;
        ArgumentNullException.ThrowIfNull(admittedBinding); ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(exactPolicy);
        if (!Enum.IsDefined(kind) || expectedSessionGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(kind));
        var snapshot = registry.CaptureSnapshot();
        if (!ReferenceEquals(snapshot, admittedBinding.Snapshot) || !ReferenceEquals(snapshot, semantic.Snapshot)) throw new InvalidOperationException("Discovery requires the exact current semantic/prepared snapshot.");
        var name = kind == McpDiscoveryKind.Codemode ? McpDiscoveryToolIdentity.CodemodeName : McpDiscoveryToolIdentity.ToolSearchName;
        var tool = snapshot.Tools.SingleOrDefault(tool => tool.Name == name && tool.OwnerId == scope.OwnerId && tool.OwnerGeneration == scope.OwnerGeneration && tool.RegistrationId == semantic.RegistrationId);
        if (tool is null || tool.Exposure != PiSharp.Contracts.ToolExposure.ModelOnly ||
            !(kind == McpDiscoveryKind.Codemode ? McpDiscoveryToolIdentity.IsCodemodeTool(tool.Name, tool.Parameters) :
                McpDiscoveryToolIdentity.IsToolSearchTool(tool.Name, tool.Parameters)))
            throw new InvalidOperationException("Discovery metadata is foreign, ordinary or an impostor.");
        var adapter = admittedBinding.Adapters.SingleOrDefault(adapter => adapter.Name == name)
            ?? throw new InvalidOperationException("Discovery requires an actual admitted prepared adapter.");
        var declaration = admittedBinding.RegisteredToolDeclarations[snapshot.Tools.IndexOf(tool)];
        var identity = new McpPreparedDiscoveryIdentity(kind, scope, registry, snapshot, catalog, adapter, exactPolicy, expectedSessionGeneration, semantic, declaration);
        identity.Validate(catalog);
        return identity;
    }

    private void Validate(SessionRuntimeRegistry current, bool bound = false)
    {
        semantic.Validate();
        if (scope.ExtensionLifetimeCancellationToken.IsCancellationRequested || (!bound && !ReferenceEquals(catalog, current)) ||
            !ReferenceEquals(snapshot, registry.CaptureSnapshot()) ||
            (bound ? current.InvocationOwnerGeneration != sessionGeneration :
                current.InvocationOwnerGeneration is { } actual && actual != sessionGeneration) ||
            !current.UsesFinalActionPolicy(policy) || !current.UsesCapturedToolBinding(adapter.Name, declaration, adapter) ||
            !current.RegisteredTools.Any(tool => ReferenceEquals(tool.Declaration, declaration) &&
                tool.Exposure == ToolExposure.ModelOnly))
            throw new InvalidOperationException("Discovery identity belongs to a stale or foreign owner/catalog/adapter.");
    }

    public static void ValidateRequired(McpToolCatalogPlan plan, SessionRuntimeRegistry current,
        ImmutableArray<McpPreparedDiscoveryIdentity> admitted)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(current);
        if (admitted.IsDefault || admitted.Length > 2) throw new ArgumentException("At most two captured discovery identities are admitted.", nameof(admitted));
        var kinds = new HashSet<McpDiscoveryKind>();
        foreach (var identity in admitted)
        {
            ArgumentNullException.ThrowIfNull(identity); identity.Validate(current);
            if (!kinds.Add(identity.Kind)) throw new ArgumentException("Duplicate discovery identity.", nameof(admitted));
        }
        if (NeedsCodemode(plan, current) && !kinds.Contains(McpDiscoveryKind.Codemode))
            throw new InvalidOperationException("MCP exposure requires an admitted executable codemode implementation.");
        if (NeedsToolSearch(plan, current) && !kinds.Contains(McpDiscoveryKind.ToolSearch))
            throw new InvalidOperationException("MCP exposure requires an admitted executable tool_search implementation.");
    }

    /// <summary>`deferred` tools need tool_search unless the tool selection (--tools/--exclude-tools) leaves it out, which leaves
    /// them unreachable as in the original, where tool_search is then not registered.</summary>
    private static bool NeedsToolSearch(McpToolCatalogPlan plan, SessionRuntimeRegistry current) =>
        plan.NeedsToolSearch && current.LifetimeToolSelection?.IsAllowed(McpDiscoveryToolIdentity.ToolSearchName) != false;

    /// <summary>`codemode` tools need codemode unless the tool selection leaves it out, or autoEnableCodemode is false and the
    /// selection does not name it; their tools are then unreachable, as in the original, where codemode stays inactive.</summary>
    private static bool NeedsCodemode(McpToolCatalogPlan plan, SessionRuntimeRegistry current) =>
        plan.NeedsCodemode && current.LifetimeToolSelection?.IsAllowed(McpDiscoveryToolIdentity.CodemodeName) != false &&
        (plan.AutoEnableCodemode || current.LifetimeToolSelection?.IsNamed(McpDiscoveryToolIdentity.CodemodeName) == true ||
            current.LifetimeToolSelection?.InitialNames.Contains(McpDiscoveryToolIdentity.CodemodeName) == true);

    internal static void ValidateBound(McpToolCatalogPlan plan, SessionRuntimeRegistry current,
        long actualAttachmentGeneration, ImmutableArray<McpPreparedDiscoveryIdentity> admitted)
    {
        foreach (var identity in admitted)
        {
            if (identity.sessionGeneration != actualAttachmentGeneration)
                throw new InvalidOperationException("Discovery reserved generation does not match the actual attachment.");
            identity.Validate(current, bound: true);
        }
        if (NeedsCodemode(plan, current) && !admitted.Any(identity => identity.Kind == McpDiscoveryKind.Codemode) ||
            NeedsToolSearch(plan, current) && !admitted.Any(identity => identity.Kind == McpDiscoveryKind.ToolSearch))
            throw new InvalidOperationException("Bound MCP exposure requires its actual discovery implementations.");
    }
    internal void BindSemanticOwner(ReplaceableAgentSession owner, AgentSessionAttachment attachment) => semantic.Bind(owner, attachment);
}
