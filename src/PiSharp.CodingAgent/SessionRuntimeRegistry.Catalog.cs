using System.Collections.Immutable;
using PiSharp.Agent;

namespace PiSharp.CodingAgent;

public sealed partial class SessionRuntimeRegistry
{
    private readonly SessionRuntimeRegistry? _catalogReplacementSource;

    /// <summary>Build a validated borrowed catalog with the same models, final-action policy,
    /// invocation owner, limits and lifetime selection. Publication belongs to the session.</summary>
    public SessionRuntimeRegistry WithToolCatalog(ImmutableArray<SessionRegisteredTool> tools,
        IPreparedToolHooks? preparedHooks) => new(this, tools, preparedHooks);

    public ImmutableArray<SessionRegisteredTool> RegisteredTools => _registeredTools;
    public IPreparedToolHooks? PreparedToolHooks => _options.PreparedToolHooks;
    public bool UsesFinalActionPolicy(IToolActionPolicy policy) => ReferenceEquals(_policy, policy);
    public long? InvocationOwnerGeneration => _options.InvocationScopes?.SessionGeneration;

    private SessionRuntimeRegistry(SessionRuntimeRegistry source, ImmutableArray<SessionRegisteredTool> tools,
        IPreparedToolHooks? preparedHooks)
        : this(source._modelCatalog.Read().Bindings, tools, source._policy,
            source._options with { PreparedToolHooks = preparedHooks }, source._modelCatalog)
    {
        _modelCatalog = source._modelCatalog;
        _catalogReplacementSource = source;
        _loadoutDrain = source._loadoutDrain;
        _inLoadoutDrain = source._inLoadoutDrain;
    }

    internal bool ReplacesCatalogOf(SessionRuntimeRegistry source) => ReferenceEquals(_catalogReplacementSource, source);
}
