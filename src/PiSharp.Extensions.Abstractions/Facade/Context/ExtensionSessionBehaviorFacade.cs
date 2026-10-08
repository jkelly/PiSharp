using System.Collections.Immutable;

namespace PiSharp.Extensions.Facade.Context;

public delegate ValueTask ExtensionFacadeWithSessionCallback(IExtensionCommandFacade freshContext,CancellationToken token);
/// <summary>Optional adapters through actual command admission; no independent lifecycle owner.</summary>
public interface IExtensionSessionBehaviorFacade
{
    ValueTask SetThinkingLevelAsync(string level);
    void Abort();
    ValueTask CompactWithCallbacksAsync(string? customInstructions=null,ExtensionBehaviorCompactionCallbacks? callbacks=null);
    ValueTask<IExtensionCommandFacade?> NewSessionWithSessionAsync(ExtensionFacadeWithSessionCallback callback,
        string? parentSession=null,CancellationToken cancellationToken=default);
    ValueTask<IExtensionCommandFacade?> ForkWithSessionAsync(string entryId,ExtensionFacadeWithSessionCallback callback,
        bool before=true,CancellationToken cancellationToken=default);
    ValueTask<IExtensionCommandFacade?> SwitchSessionWithSessionAsync(string absolutePath,ExtensionFacadeWithSessionCallback callback,
        CancellationToken cancellationToken=default);
}
/// <summary>Actual close original and once-cached raw graphs. InnerException preserves the close graph.</summary>
public sealed class ExtensionFacadeBehaviorSettlementException(Task original,AggregateException? fault,Exception direct,
    ImmutableArray<ExtensionBehaviorOriginalEvidence> originals)
    : Exception("Session behavior settlement failed; actual engine/callback originals retained.",fault??direct)
{
    public Task Original {get;}=original;
    public AggregateException? Fault {get;}=fault;
    public Exception Direct {get;}=direct;
    public ImmutableArray<ExtensionBehaviorOriginalEvidence> Originals {get;}=originals;
}
