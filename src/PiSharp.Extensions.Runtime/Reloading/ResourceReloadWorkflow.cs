using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime.Reloading;

public enum ResourceReloadAuthority { Old, New, None, Unknown }
public enum ResourceReloadPhase { Stage, Shutdown, InvalidateAndDrain, Publish, RetireOld, StartAndExtend, RollbackCandidate }
public sealed record ResourceReloadFailure(ResourceReloadPhase Phase, Exception Cause);

/// <summary>Publication owner acknowledgement. Old is forbidden after acknowledged invalidation.
/// None/Unknown require an original failure; a thrown publication callback means Unknown.</summary>
public sealed record ResourceReloadPublication(ResourceReloadAuthority Authority, Exception? Failure = null);

public sealed record ResourceReloadReceipt<TGeneration>(ResourceReloadAuthority Authority, TGeneration OldGeneration,
    TGeneration? Candidate, bool ShutdownCompleted, bool OldInvalidatedAndDrained, bool OldCleanupAttempted,
    bool CandidateCleanupAttempted, ImmutableArray<ResourceReloadFailure> Failures) where TGeneration : class;

/// <summary>Borrowed host operations. Stage must clean acquisitions that it does not return. A returned candidate is
/// unexposed and owned by this attempt until publication acknowledges New. InvalidateAndDrain must reject stale
/// contexts and join the original admitted callbacks/reporters; it must not detach them using a cancelled outer wait.
/// Publish is the shared activation owner's compare-and-publish, never an independent registry publication.
/// Cleanup joins the actual loader/registry/reporter disposal. StartAndExtend implements the host's reload start,
/// unhandled-server reporting and extension resource additions, in that order. No delegate runs under a state lock.</summary>
public sealed class ResourceReloadOperations<TGeneration> where TGeneration : class
{
    public required Func<ResourceReloadPlan, CancellationToken, ValueTask<TGeneration>> StageAsync { get; init; }
    public required Func<TGeneration, CancellationToken, ValueTask> ShutdownAsync { get; init; }
    public required Func<TGeneration, ValueTask> InvalidateAndDrainAsync { get; init; }
    public required Func<TGeneration, TGeneration, ValueTask<ResourceReloadPublication>> PublishAsync { get; init; }
    public required Func<TGeneration, ValueTask> CleanupAsync { get; init; }
    public required Func<TGeneration, CancellationToken, ValueTask> StartAndExtendAsync { get; init; }
}

/// <summary>One admitted reload attempt, not a session mutation reservation or a new lifetime owner.
/// The host must hold its existing reload/session reservation through the original returned task. Concurrent callers
/// join that task; callback self-waits are refused. Caller cancellation controls admission only. The borrowed host
/// lifetime controls staging/notifications; invalidation, publication and cleanup always join their original operations.
/// Pre-publication failures roll back the unexposed candidate. Old authority is never revived after invalidation.
/// An uncertain publication retains the candidate for the publication owner to recover; it is never guessed away.</summary>
public sealed class ResourceReloadWorkflow<TGeneration> where TGeneration : class
{
    private readonly object gate = new();
    private readonly AsyncLocal<bool> inside = new();
    private readonly TGeneration old;
    private readonly ResourceReloadPlan plan;
    private readonly ResourceReloadOperations<TGeneration> operations;
    private readonly CancellationToken hostLifetime;
    private Task<ResourceReloadReceipt<TGeneration>>? attempt;

    public ResourceReloadWorkflow(TGeneration oldGeneration, ResourceReloadPlan plan,
        ResourceReloadOperations<TGeneration> operations, CancellationToken hostLifetime)
    {
        ArgumentNullException.ThrowIfNull(oldGeneration); ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(operations.StageAsync); ArgumentNullException.ThrowIfNull(operations.ShutdownAsync);
        ArgumentNullException.ThrowIfNull(operations.InvalidateAndDrainAsync); ArgumentNullException.ThrowIfNull(operations.PublishAsync);
        ArgumentNullException.ThrowIfNull(operations.CleanupAsync); ArgumentNullException.ThrowIfNull(operations.StartAndExtendAsync);
        if (!hostLifetime.CanBeCanceled) throw new ArgumentException("An owned host lifetime is required.", nameof(hostLifetime));
        old = oldGeneration; this.plan = plan; this.operations = operations; this.hostLifetime = hostLifetime;
    }

    public Task<ResourceReloadReceipt<TGeneration>> ReloadAsync(CancellationToken admissionToken = default)
    {
        if (inside.Value) throw new InvalidOperationException("A reload callback cannot await its own attempt.");
        admissionToken.ThrowIfCancellationRequested();
        TaskCompletionSource<ResourceReloadReceipt<TGeneration>>? settlement = null;
        Task<ResourceReloadReceipt<TGeneration>> pending;
        lock (gate)
        {
            if (attempt is null)
            {
                settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                attempt = settlement.Task;
            }
            pending = attempt;
        }
        if (settlement is not null) _ = RunAsync(settlement);
        return pending;
    }

    private async Task RunAsync(TaskCompletionSource<ResourceReloadReceipt<TGeneration>> settlement)
    {
        inside.Value = true;
        try { settlement.TrySetResult(await RunCoreAsync().ConfigureAwait(false)); }
        catch (Exception error) { settlement.TrySetException(error); }
        finally { inside.Value = false; }
    }

    private async Task<ResourceReloadReceipt<TGeneration>> RunCoreAsync()
    {
        TGeneration? candidate = null;
        var authority = ResourceReloadAuthority.Old;
        var phase = ResourceReloadPhase.Stage;
        var shutdown = false; var invalidated = false; var publicationEntered = false;
        var oldCleanup = false; var candidateCleanup = false;
        var failures = ImmutableArray.CreateBuilder<ResourceReloadFailure>();
        try
        {
            hostLifetime.ThrowIfCancellationRequested();
            candidate = await operations.StageAsync(plan, hostLifetime).ConfigureAwait(false);
            if (candidate is null || ReferenceEquals(candidate, old))
            {
                // Never dispose the old generation as a malformed staged candidate.
                candidate = null;
                throw new InvalidOperationException("Staging must return a distinct unexposed generation.");
            }
            hostLifetime.ThrowIfCancellationRequested();
            phase = ResourceReloadPhase.Shutdown;
            await operations.ShutdownAsync(old, hostLifetime).ConfigureAwait(false);
            shutdown = true;
            phase = ResourceReloadPhase.InvalidateAndDrain;
            authority = ResourceReloadAuthority.Unknown;
            await operations.InvalidateAndDrainAsync(old).ConfigureAwait(false);
            invalidated = true; authority = ResourceReloadAuthority.None;
            phase = ResourceReloadPhase.Publish; publicationEntered = true; authority = ResourceReloadAuthority.Unknown;
            var publication = await operations.PublishAsync(old, candidate).ConfigureAwait(false);
            if (publication is null || publication.Authority is not (ResourceReloadAuthority.New or ResourceReloadAuthority.None or ResourceReloadAuthority.Unknown) ||
                publication.Authority != ResourceReloadAuthority.New && publication.Failure is null)
                throw new InvalidOperationException("Publication must acknowledge new, none or uncertain authority.");
            authority = publication.Authority;
            if (publication.Failure is not null) failures.Add(new(phase, publication.Failure));
        }
        catch (Exception error) { failures.Add(new(phase, error)); }

        if (invalidated)
        {
            oldCleanup = true;
            try { await operations.CleanupAsync(old).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(new(ResourceReloadPhase.RetireOld, error)); }
        }
        if (candidate is not null && (!publicationEntered || authority == ResourceReloadAuthority.None))
        {
            candidateCleanup = true;
            try { await operations.CleanupAsync(candidate).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(new(ResourceReloadPhase.RollbackCandidate, error)); }
        }
        if (authority == ResourceReloadAuthority.New && failures.Count == 0)
        {
            try { await operations.StartAndExtendAsync(candidate!, hostLifetime).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(new(ResourceReloadPhase.StartAndExtend, error)); }
        }
        return new(authority, old, candidate, shutdown, invalidated, oldCleanup, candidateCleanup, failures.ToImmutable());
    }
}
