using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.CodingAgent;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>Explicitly admitted semantic implementation. The factory supplies no JavaScript engine.</summary>
public delegate ValueTask<JsonData> McpAdmittedCodemodeExecutor(string code, IExtensionToolInvocationContext invocation, CancellationToken token);
/// <summary>Explicitly admitted search plus durable activation implementation. The factory supplies no ranker.</summary>
public delegate ValueTask<JsonData> McpAdmittedToolSearchExecutor(string query, double? limit, IExtensionToolInvocationContext invocation, CancellationToken token);

public sealed class McpDiscoveryExecutableDefinition
{
    internal readonly ExecutionFence Fence;
    public McpDiscoveryKind Kind { get; }
    internal ExtensionToolDescriptor Descriptor { get; }
    private McpDiscoveryExecutableDefinition(McpDiscoveryKind kind, ExtensionToolDescriptor descriptor, ExecutionFence fence)
    { Kind = kind; Descriptor = descriptor; Fence = fence; }

    public static McpDiscoveryExecutableDefinition CreateCodemode(string registrationId, string description,
        McpAdmittedCodemodeExecutor admittedExecutor, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null)
    {
        Single(admittedExecutor);
        var fence = new ExecutionFence();
        ValueTask<JsonData> Execute(JsonData arguments, IExtensionToolContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (context is not IExtensionToolInvocationContext invocation || invocation.SessionGeneration <= 0) throw new InvalidOperationException("Codemode requires the native captured invocation pipeline.");
            fence.Validate(invocation);
            var code = arguments.Value.GetProperty("code").GetString() ?? throw new ArgumentException("A source string is required.");
            return admittedExecutor(code, invocation, token);
        }
        return new(McpDiscoveryKind.Codemode, McpDiscoveryToolIdentity.CreateCodemode(registrationId, description, Execute, prepareLoadout), fence);
    }
    public static McpDiscoveryExecutableDefinition CreateToolSearch(string registrationId, string description,
        McpAdmittedToolSearchExecutor admittedExecutor, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null)
    {
        Single(admittedExecutor);
        var fence = new ExecutionFence();
        ValueTask<JsonData> Execute(JsonData arguments, IExtensionToolContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (context is not IExtensionToolInvocationContext invocation || invocation.SessionGeneration <= 0) throw new InvalidOperationException("Search requires the native captured invocation pipeline.");
            fence.Validate(invocation);
            var query = arguments.Value.GetProperty("query").GetString() ?? throw new ArgumentException("A query string is required.");
            double? limit = arguments.Value.TryGetProperty("limit", out var supplied) ? supplied.GetDouble() : null;
            if (limit is { } value && !double.IsFinite(value)) throw new ArgumentException("A finite search limit is required.");
            return admittedExecutor(query, limit, invocation, token);
        }
        return new(McpDiscoveryKind.ToolSearch, McpDiscoveryToolIdentity.CreateToolSearch(registrationId, description, Execute, prepareLoadout), fence);
    }
    private static void Single(Delegate admitted)
    { ArgumentNullException.ThrowIfNull(admitted); if (admitted.GetInvocationList().Length != 1) throw new ArgumentException("One joined semantic implementation is required."); }
    internal sealed class ExecutionFence
    {
        private readonly object gate = new();
        private ReplaceableAgentSession? owner;
        private AgentSessionAttachment? attachment;
        private bool registered;
        private string? scopeOwner;
        private long scopeGeneration;
        internal void CheckFresh() { lock (gate) if (registered) throw new InvalidOperationException("Discovery definition has already been registered."); }
        internal void Claim(RegistrationScope scope)
        {
            lock (gate)
            {
                if (registered) throw new InvalidOperationException("Discovery definition registration raced another owner.");
                registered = true; scopeOwner = scope.OwnerId; scopeGeneration = scope.OwnerGeneration;
            }
        }
        internal void Bind(ReplaceableAgentSession actualOwner, AgentSessionAttachment actualAttachment)
        {
            lock (gate)
            {
                if (owner is not null) throw new InvalidOperationException("Discovery implementation has already been bound.");
                owner = actualOwner; attachment = actualAttachment;
            }
        }
        internal void Validate(IExtensionToolInvocationContext invocation)
        {
            ReplaceableAgentSession? capturedOwner; AgentSessionAttachment? captured; string? capturedScope; long capturedScopeGeneration;
            lock (gate) { capturedOwner = owner; captured = attachment; capturedScope = scopeOwner; capturedScopeGeneration = scopeGeneration; }
            if (capturedOwner is null || captured is null || captured.LifetimeToken.IsCancellationRequested ||
                !ReferenceEquals(capturedOwner.Current, captured) || invocation.SessionGeneration != captured.Generation ||
                invocation.OwnerId != capturedScope || invocation.OwnerGeneration != capturedScopeGeneration)
                throw new InvalidOperationException("Discovery implementation is unbound or belongs to a retired attachment.");
        }
    }
}

/// <summary>Opaque registration receipt minted only by the dedicated semantic factory batch.</summary>
public sealed class McpAdmittedDiscoveryRegistration
{
    internal RegistrationScope Scope { get; }
    internal ExtensionRegistry Registry { get; }
    internal ExtensionRegistrySnapshot Snapshot { get; }
    internal string RegistrationId { get; }
    public McpDiscoveryKind Kind { get; }
    private readonly McpDiscoveryExecutableDefinition definition;
    private McpAdmittedDiscoveryRegistration(RegistrationScope scope, ExtensionRegistry registry,
        ExtensionRegistrySnapshot snapshot, McpDiscoveryExecutableDefinition definition)
    { Scope = scope; Registry = registry; Snapshot = snapshot; Kind = definition.Kind; RegistrationId = definition.Descriptor.RegistrationId; this.definition = definition; }

    /// <summary>Register one or both implementations in one dedicated discovery catalog batch.
    /// Any later metadata drift invalidates this receipt; it does not silently brand reused registrations.</summary>
    public static ImmutableArray<McpAdmittedDiscoveryRegistration> Register(ExtensionRegistry registry,
        RegistrationScope scope, ImmutableArray<McpDiscoveryExecutableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(scope);
        if (definitions.IsDefaultOrEmpty || definitions.Length > 2) throw new ArgumentException("One or two discovery definitions are required.", nameof(definitions));
        var kinds = new HashSet<McpDiscoveryKind>();
        foreach (var definition in definitions)
        { ArgumentNullException.ThrowIfNull(definition); if (!kinds.Add(definition.Kind)) throw new ArgumentException("Duplicate discovery kind.", nameof(definitions)); }
        // Definition ownership is affine: an admitted executor cannot be reused across generations.
        foreach (var definition in definitions) definition.Fence.CheckFresh();
        if (registry.CaptureSnapshot().Registrations.Any(row => row.OwnerId == scope.OwnerId && row.OwnerGeneration == scope.OwnerGeneration))
            throw new InvalidOperationException("Discovery requires a dedicated scope with no existing registrations.");
        var plan = registry.PrepareToolCatalogReplacement(scope, [], definitions.Select(item => item.Descriptor).ToImmutableArray(), registry.CaptureSnapshot());
        var admitted = definitions.Select(definition => new McpAdmittedDiscoveryRegistration(scope, registry, plan.PreviewSnapshot, definition)).ToImmutableArray();
        foreach (var definition in definitions) definition.Fence.Claim(scope);
        plan.Commit();
        return admitted;
    }
    internal void Validate()
    {
        if (Scope.ExtensionLifetimeCancellationToken.IsCancellationRequested || !ReferenceEquals(Snapshot, Registry.CaptureSnapshot()))
            throw new InvalidOperationException("Discovery semantic registration is stale or retired.");
    }
    internal void Bind(ReplaceableAgentSession owner, AgentSessionAttachment attachment) => definition.Fence.Bind(owner, attachment);
}
