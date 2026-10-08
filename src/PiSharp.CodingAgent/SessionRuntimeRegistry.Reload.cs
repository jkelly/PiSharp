using System.Collections.Immutable;
using PiSharp.Agent;

namespace PiSharp.CodingAgent;

public sealed partial class SessionRuntimeRegistry
{
    // The staged runtime supplies bindings, but cannot widen the actual session's policy.
    // The predecessor is captured after owned-resource withdrawal has finished.
    internal SessionRuntimeRegistry ForReload(SessionRuntimeRegistry predecessor,
        SessionRuntimeRegistry admitted, ToolInvocationScopeOptions scopes)
        => new(this, predecessor, admitted, scopes);

    private SessionRuntimeRegistry(SessionRuntimeRegistry candidate, SessionRuntimeRegistry predecessor,
        SessionRuntimeRegistry admitted, ToolInvocationScopeOptions scopes)
        : this(candidate._modelCatalog.Read().Bindings, candidate._registeredTools, admitted._policy,
            candidate._options with
            {
                LifetimeToolSelection = admitted._options.LifetimeToolSelection,
                ToolInvokerOptions = admitted._options.ToolInvokerOptions,
                InvocationScopes = admitted._options.InvocationScopes is { } previous
                    ? previous with { SessionGeneration = scopes.SessionGeneration, SessionCancellationToken = scopes.SessionCancellationToken }
                    : scopes,
                BindNestedCallsToSessionOwner = true
            }, candidate._modelCatalog)
    {
        _modelCatalog = candidate._modelCatalog;
        _catalogReplacementSource = predecessor;
    }
}
