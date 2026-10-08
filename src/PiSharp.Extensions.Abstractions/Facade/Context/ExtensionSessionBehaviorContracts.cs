using PiSharp.Contracts;

namespace PiSharp.Extensions.Facade.Context;

/// <summary>Native callback adaptation: the exact fresh broker context, never an old snapshot.</summary>
public delegate ValueTask ExtensionWithSessionCallback(IExtensionCommandContext context, CancellationToken token);
public sealed record ExtensionBehaviorCompactionResult(JsonData AcknowledgedEntry);
public sealed record ExtensionBehaviorCompactionCallbacks(
    Func<ExtensionBehaviorCompactionResult, CancellationToken, ValueTask>? OnComplete = null,
    Func<Exception, CancellationToken, ValueTask>? OnError = null);

/// <summary>The actual engine task and a result reader used only after that original succeeds.</summary>
public sealed record ExtensionBehaviorCompactionWork(Task Original,
    Func<ExtensionBehaviorCompactionResult?> ReadCompletedResult);

/// <summary>Cleanup of an already admitted callback scope. Original is joined before reading failures.</summary>
public sealed record ExtensionBehaviorCallbackSettlement(Task Original,Func<IReadOnlyList<Exception>> ReadCompletedFailures);

/// <summary>Observation only. Aggregate/await exceptions are cached once after directly joining Original.</summary>
public sealed record ExtensionBehaviorOriginalEvidence(string Phase, Task Original,
    AggregateException? Aggregate, Exception? Observed);

/// <summary>Optional concrete command-host capability. It grants no process or session-creation authority.</summary>
public interface IExtensionDirectSessionBehaviorHost
{
    Task SetThinkingLevel(IExtensionCommandContext context, string level, CancellationToken token);
    ExtensionBehaviorCompactionWork Compact(IExtensionCommandContext context, string? instructions, CancellationToken token);
    void Abort(IExtensionContext context);
}
