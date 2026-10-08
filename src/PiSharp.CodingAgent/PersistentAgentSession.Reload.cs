using System.Collections.Immutable;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    internal SessionRuntimeLease? CaptureReloadRuntime(ReplacementReservation reservation)
    {
        lock (_gate) { reservation.ValidateCatalogAuthority(this); return _runtimeLease; }
    }

    internal async Task<SessionToolCatalogReceipt> PublishReloadAsync(ReplacementReservation reservation,
        SessionRuntimeRegistry admitted, SessionRuntimeLease candidate, ImmutableArray<string> activeNames,
        long generation, CancellationToken lifetime, Action commitPrepared)
    {
        // The actual owner claimed this candidate during staging. Publication owns the lease from the
        // synchronous commit onward, even if a later agent configuration step faults.
        var invocation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _closing.Token);
        var transferred = false;
        CancellationTokenSource? previousInvocation = null;
        try
        {
            var predecessor = reservation.CaptureToolCatalogRegistry();
            var replacement = candidate.Registry.ForReload(predecessor, admitted, new(generation, invocation.Token));
            var original = reservation.PublishToolCatalogAsync(predecessor, replacement, activeNames, () =>
            {
                commitPrepared();
                previousInvocation = _invocationLifetime;
                _invocationLifetime = invocation;
                _runtimeLease = candidate;
                transferred = true;
            });
            return await AgentSessionReloadTask.Join(original).ConfigureAwait(false);
        }
        finally
        {
            if (!transferred) invocation.Dispose();
            // Old authority was cancelled and its original work joined before publication.
            previousInvocation?.Dispose();
        }
    }
}

/// <summary>Retains the exact original task state, including every fault and faulted OCEs.</summary>
public sealed class AgentSessionReloadCallbackException(Task original, Exception awaitedCause)
    : Exception("An original reload operation failed.", original.Exception ?? awaitedCause)
{
    public bool OriginalTaskIsCanceled { get; } = original.IsCanceled;
    public AggregateException? OriginalTaskException { get; } = original.Exception;
    public Exception AwaitedCause { get; } = awaitedCause;
}

internal static class AgentSessionReloadTask
{
    // Keep legacy single-fault identity while preventing await from discarding sibling
    // faults or changing a faulted OperationCanceledException into task cancellation.
    internal static async Task JoinOwned(Task original)
    {
        try { await original.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (original.Exception is { } all && (all.InnerExceptions.Count > 1 || error is OperationCanceledException)) throw all;
            throw;
        }
    }
    internal static async Task<T> JoinOwned<T>(Task<T> original)
    {
        try { return await original.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (original.Exception is { } all && (all.InnerExceptions.Count > 1 || error is OperationCanceledException)) throw all;
            throw;
        }
    }
    internal static async Task Join(Task original)
    {
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { throw new AgentSessionReloadCallbackException(original, error); }
    }
    internal static async Task<T> Join<T>(Task<T> original)
    {
        try { return await original.ConfigureAwait(false); }
        catch (Exception error) { throw new AgentSessionReloadCallbackException(original, error); }
    }
}
