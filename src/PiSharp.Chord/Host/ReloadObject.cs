// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/object.ts (reload)
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Host;

internal sealed partial class ChordObject
{
    private readonly HashSet<Task> retiring = [];
    private Attempt? swappingSource;
    internal Attempt? FollowingAttempt => Current?.Live == true ? Current : swappingSource is { HasSources: true } ? swappingSource : null;

    internal Task<FailureCause?> ApplyReload(ServedType served, string fate)
    {
        Host.AssertOpen();
        retry?.Cancel(); retry?.Dispose(); retry = null;
        Served = served;
        var old = Current;
        var attempt = new Attempt(this, served);
        if (fate == "swap" && old is not null)
        {
            // Same object incarnation and document slots; old calls retain their own provider and token.
            attempt.InstanceId = old.InstanceId;
            old.HandOver();
            RemoteSwapInPlace = true;
            swappingSource = old;
            Current = attempt;
            SetState(RefState.Loading);
            TrackRetirement(old, RetireSwapAsync(old, attempt));
            _ = Host.Dispatcher.RunAsync(attempt.ActivateAsync);
        }
        else
        {
            RemoteSwapInPlace = false;
            swappingSource = old is { Live: true } ? old : null;
            Current = attempt;
            SetState(RefState.Loading);
            if (old is null) _ = Host.Dispatcher.RunAsync(attempt.ActivateAsync);
            else
            {
                old.Self.StopRoutines();
                TrackRetirement(old, RestartAsync(old, attempt));
            }
        }
        return attempt.Checked.Task;
    }

    private async Task RetireSwapAsync(Attempt old, Attempt next)
    {
        // Share dependencies until the replacement's first check. A parked replacement still ends the sharing
        // period; the old invocation is then sealed after its admitted calls drained or reached their bound.
        await Task.WhenAll(old.DrainCallsAsync(), next.Checked.Task);
        old.Seal(Reloaded(), "reload", keepSources: true);
        await Documents.SettledAsync();
        old.DetachSources(Reloaded());
        StopFollowing(old);
        // Handed-over job invocations retain their original context and implementation until they settle.
        // A normal reload does not turn a long-running job into a close-timeout failure. A real object/host
        // end still calls CloseAsync and signals bounded abandonment through RetirementEnded.
        if (old.Runs.Count != 0)
            await Task.WhenAny(Task.WhenAll(old.Runs.Keys.ToArray()), old.RetirementEnded.Task);
        await old.CloseAsync(NativeCloseReason.Reload);
    }

    private async Task RestartAsync(Attempt old, Attempt next)
    {
        // Earlier pure swaps may still own admitted calls. Their native-free job invocations can continue,
        // but every admitted call and document step must settle before this restart closes its resources.
        var calls = OldAttempts.Select(previous => previous.DrainCallsAsync()).Append(old.DrainCallsAsync()).ToArray();
        await Task.WhenAll(calls);
        old.Seal(Reloaded(), "reload", keepSources: true);
        await Documents.SettledAsync();
        old.DetachSources(Reloaded());
        StopFollowing(old);
        await old.CloseAsync(NativeCloseReason.Reload);
        // A later removal/close may have superseded this deferred restart while it drained.
        if (ending || Host.Closed || !ReferenceEquals(Current, next))
        {
            next.Seal(Reloaded(), "reload");
            next.Checked.TrySetResult(null);
            return;
        }
        await next.ActivateAsync();
    }

    private void TrackRetirement(Attempt attempt, Task work)
    {
        OldAttempts.Add(attempt);
        retiring.Add(work);
        Host.TrackReloadWork(work, Label);
        _ = Settle();
        async Task Settle()
        {
            try { await work; }
            catch { /* The host's reload-work owner reports cleanup failures. */ }
            finally { OldAttempts.Remove(attempt); retiring.Remove(work); }
        }
    }

    internal void StopFollowing(Attempt previous)
    {
        if (!ReferenceEquals(swappingSource, previous)) return;
        swappingSource = null;
        Host.Batch.Within(() => { foreach (var holder in Holders.ToArray()) holder.Retarget(); }, Context);
    }

    internal Task RetiredWork => Task.WhenAll(retiring.ToArray());
    private ChordError Reloaded() => new("released", $"{Label}: reloaded", "ended");

    internal void WithdrawRetiringContributions(Attempt next)
    {
        if (!ReferenceEquals(Current, next)) return;
        foreach (var previous in OldAttempts)
        {
            foreach (var contribution in previous.Self.PublishedContributions) contribution.Record.Remove(contribution);
            previous.Self.PublishedContributions.Clear();
        }
    }

    internal void RemoveForReload()
    {
        // Withdrawal ends the object, not the request: its holders return to the host's unbound index.
        var holders = Holders.ToArray();
        swappingSource = null;
        // Unholding the first request must not trigger normal last-holder teardown while the remaining
        // requests are still being moved. That teardown would release them permanently.
        ending = true;
        try
        {
            foreach (var holder in holders)
            {
                if (holder is Binding binding) binding.UnbindForReload();
                else if (holder is IReloadAwareHolder remote) remote.Removed(new ChordError("unavailable", $"{Served.Contract.Name} is not served here"));
                else holder.End(new ChordError("unavailable", $"{Served.Contract.Name} is not served here"));
            }
        }
        finally { ending = false; }
        Host.TrackReloadWork(EndAsync(new ChordError("released", $"{Label}: removed", "ended"), NativeCloseReason.End), Label);
    }
}

internal interface IReloadAwareHolder
{
    void Removed(ChordError error);
}

public sealed partial class ChordHost
{
    private readonly HashSet<Task> reloadWork = [];
    internal void TrackReloadWork(Task work, string label)
    {
        reloadWork.Add(work);
        _ = Complete();
        async Task Complete()
        {
            try { await work; }
            catch (Exception error) { Report(new("close_failed", error, label)); }
            finally { reloadWork.Remove(work); }
        }
    }
    /// <summary>Waits for old calls and resources retired by committed reloads. Cancellation ends only this wait.</summary>
    public Task DrainReloadsAsync(ChordContext context) => Dispatcher.RunAsync(async () =>
    {
        var work = reloadWork.ToArray();
        await Task.WhenAll(work).WaitAsync(context.AbortSignal ?? CancellationToken.None);
    });
}
