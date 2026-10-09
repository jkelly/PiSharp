using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>A single-generation semantic registration owned by an explicitly admitted discovery
/// registry lifetime. The caller supplies executable semantics, argument admission and hook composition.
/// This helper acquires no interpreter, ranker, transport, registry lifetime or credentials.</summary>
public sealed class McpRegisteredProfileDiscoveryAdmission
{
    private readonly ImmutableArray<McpAdmittedDiscoveryRegistration> registrations;
    private readonly IToolActionPolicy policy;
    private readonly long generation;
    private readonly Func<SessionRuntimeRegistry, ExtensionAgentBinding, IPreparedToolHooks?> composeHooks;
    private int prepared;
    public ExtensionAgentBinding Binding { get; }

    public McpRegisteredProfileDiscoveryAdmission(ExtensionRegistry registry, RegistrationScope scope,
        ImmutableArray<McpDiscoveryExecutableDefinition> definitions, IToolActionPolicy exactPolicy,
        long reservedGeneration, ExtensionToolArgumentValidator validateArguments,
        Func<SessionRuntimeRegistry, ExtensionAgentBinding, IPreparedToolHooks?> admittedHookComposition)
    {
        ArgumentNullException.ThrowIfNull(exactPolicy);
        ArgumentNullException.ThrowIfNull(validateArguments);
        ArgumentNullException.ThrowIfNull(admittedHookComposition);
        if (reservedGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(reservedGeneration));
        if (validateArguments.GetInvocationList().Length != 1 || admittedHookComposition.GetInvocationList().Length != 1)
            throw new ArgumentException("One argument admission and one owning hook composer required.");
        policy = exactPolicy; generation = reservedGeneration; composeHooks = admittedHookComposition;
        // The caller owns this exact scope and must retire it if subsequent binding/preparation fails.
        registrations = McpAdmittedDiscoveryRegistration.Register(registry, scope, definitions);
        Binding = new(registry, policy, validateArguments);
    }

    /// <summary>Pass directly as the registered profile runtime admission's discovery preparation.
    /// Preserves every captured existing adapter/declaration and mints identities against the final catalog.</summary>
    public McpPreparedDiscoveryCatalog Prepare(McpToolCatalogPlan plan, SessionRuntimeRegistry acquiredRegistry)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(acquiredRegistry);
        if (!acquiredRegistry.UsesFinalActionPolicy(policy) ||
            acquiredRegistry.InvocationOwnerGeneration is { } actual && actual != generation)
            throw new InvalidOperationException("Discovery admission belongs to another policy or generation.");
        if (Interlocked.CompareExchange(ref prepared, 1, 0) != 0)
            throw new InvalidOperationException("Discovery preparation is affine to one acquired generation.");
        var names = acquiredRegistry.RegisteredTools.Select(tool => tool.Adapter.Name).ToHashSet(StringComparer.Ordinal);
        if (Binding.Registrations.Any(tool => names.Contains(tool.Name)))
            throw new InvalidOperationException("Discovery collides with an existing captured executable binding.");
        var added = Binding.Registrations.Select((tool, index) =>
            new SessionRegisteredTool(Binding.RegisteredToolDeclarations[index], Binding.Adapters[index])
            { Namespace = tool.Namespace, Exposure = tool.Exposure, DefaultActive = tool.DefaultActive, Annotations = tool.Annotations,
                IsExtension = true, PrepareLoadout = Binding.GetLoadoutPreparation(tool.Name) }).ToImmutableArray();
        var final = acquiredRegistry.WithToolCatalog(acquiredRegistry.RegisteredTools.AddRange(added),
            composeHooks(acquiredRegistry, Binding));
        var identities = registrations.Select(registration =>
            McpPreparedDiscoveryIdentity.Capture(registration, Binding, final, policy, generation)).ToImmutableArray();
        McpPreparedDiscoveryIdentity.ValidateRequired(plan, final, identities);
        return new(final, identities);
    }
}
