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
/// <summary>A search executor that also receives the actual attachment its definition was bound to, whose catalog it searches
/// and whose selection it changes.</summary>
/// <summary>A codemode executor bound to the actual attachment of its definition.</summary>
internal delegate ValueTask<JsonData> McpBoundCodemodeExecutor(string code, AgentSessionAttachment attachment,
    IExtensionToolInvocationContext invocation, CancellationToken token);
internal delegate ValueTask<JsonData> McpBoundToolSearchExecutor(string query, double? limit, AgentSessionAttachment attachment,
    IExtensionToolInvocationContext invocation, CancellationToken token);

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
    /// <summary>A codemode executor that also receives the actual attachment its definition was bound to (the session whose
    /// branch holds the store and whose tools scripts call). <paramref name="configure"/> adds presentation metadata
    /// (renderers, prompt guidelines, constrained sampling) without changing the tool's identity.</summary>
    internal static McpDiscoveryExecutableDefinition CreateCodemode(string registrationId, string description,
        McpBoundCodemodeExecutor admittedExecutor, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout,
        Func<ExtensionToolDescriptor, ExtensionToolDescriptor>? configure)
    {
        Single(admittedExecutor);
        var fence = new ExecutionFence();
        ValueTask<JsonData> Execute(JsonData arguments, IExtensionToolContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (context is not IExtensionToolInvocationContext invocation || invocation.SessionGeneration <= 0) throw new InvalidOperationException("Codemode requires the native captured invocation pipeline.");
            var attachment = fence.Validate(invocation);
            var code = arguments.Value.GetProperty("code").GetString() ?? throw new ArgumentException("A source string is required.");
            return admittedExecutor(code, attachment, invocation, token);
        }
        var descriptor = McpDiscoveryToolIdentity.CreateCodemode(registrationId, description, Execute, prepareLoadout);
        return new(McpDiscoveryKind.Codemode, configure?.Invoke(descriptor) ?? descriptor, fence);
    }
    public static McpDiscoveryExecutableDefinition CreateToolSearch(string registrationId, string description,
        McpAdmittedToolSearchExecutor admittedExecutor, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null)
    {
        Single(admittedExecutor);
        return CreateToolSearch(registrationId, description, (query, limit, _, invocation, token) => admittedExecutor(query, limit, invocation, token), prepareLoadout);
    }
    internal static McpDiscoveryExecutableDefinition CreateToolSearch(string registrationId, string description,
        McpBoundToolSearchExecutor admittedExecutor, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null) =>
        CreateToolSearch(registrationId, description, admittedExecutor, prepareLoadout, null);
    /// <summary><paramref name="configure"/> adds presentation metadata or the default active state (tool-search/index.ts
    /// registers it inactive) without changing the tool's identity.</summary>
    internal static McpDiscoveryExecutableDefinition CreateToolSearch(string registrationId, string description,
        McpBoundToolSearchExecutor admittedExecutor, Func<ToolLoadout, ToolLoadoutChanges?>? prepareLoadout,
        Func<ExtensionToolDescriptor, ExtensionToolDescriptor>? configure)
    {
        Single(admittedExecutor);
        var fence = new ExecutionFence();
        ValueTask<JsonData> Execute(JsonData arguments, IExtensionToolContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (context is not IExtensionToolInvocationContext invocation || invocation.SessionGeneration <= 0) throw new InvalidOperationException("Search requires the native captured invocation pipeline.");
            var attachment = fence.Validate(invocation);
            var query = arguments.Value.GetProperty("query").GetString() ?? throw new ArgumentException("A query string is required.");
            double? limit = arguments.Value.TryGetProperty("limit", out var supplied) ? supplied.GetDouble() : null;
            if (limit is { } value && !double.IsFinite(value)) throw new ArgumentException("A finite search limit is required.");
            return admittedExecutor(query, limit, attachment, invocation, token);
        }
        var descriptor = McpDiscoveryToolIdentity.CreateToolSearch(registrationId, description, Execute, prepareLoadout);
        return new(McpDiscoveryKind.ToolSearch, configure?.Invoke(descriptor) ?? descriptor, fence);
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
        internal AgentSessionAttachment Validate(IExtensionToolInvocationContext invocation)
        {
            ReplaceableAgentSession? capturedOwner; AgentSessionAttachment? captured; string? capturedScope; long capturedScopeGeneration;
            lock (gate) { capturedOwner = owner; captured = attachment; capturedScope = scopeOwner; capturedScopeGeneration = scopeGeneration; }
            if (capturedOwner is null || captured is null || captured.LifetimeToken.IsCancellationRequested ||
                !ReferenceEquals(capturedOwner.Current, captured) || invocation.SessionGeneration != captured.Generation ||
                invocation.OwnerId != capturedScope || invocation.OwnerGeneration != capturedScopeGeneration)
                throw new InvalidOperationException("Discovery implementation is unbound or belongs to a retired attachment.");
            return captured;
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
