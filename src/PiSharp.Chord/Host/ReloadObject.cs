// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/object.ts (what a reload does to an object).
// Every affected live object restarts; old work retains the open old attempt. 1.6.0 (slice 14 §2.5): an admitted type's successor
// admits while the old attempt drains (only its factory waits for the old attempt's close), a change of the old attempt's admission
// seals it at once and the restart goes on without its drain, and the readiness of the recorded requests does not overwrite what the
// successor's admission shows while it has not allowed.
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Host;

internal sealed partial class ChordObject
{
    private readonly HashSet<Task> retiring = [];
    private Attempt? swappingSource;
    private Attempt? openOld;
    private ReloadReadiness? readiness;
    private Action? startSuccessor;
    internal Attempt? FollowingAttempt => Current?.Live == true ? Current : swappingSource is { HasSources: true } ? swappingSource : null;
    internal Attempt? OpenOldAttempt => openOld is { Draining: true } old ? old : null;
    internal FailureCause? ReadinessCause => readiness?.Cause;

    internal Task<FailureCause?> ApplyReload(ServedType served, string fate, IReadOnlyList<RecordedRequest> requests)
    {
        Host.AssertOpen(); retry?.Cancel(); retry?.Dispose(); retry = null;
        Served = served;
        var old = Current;
        var next = new Attempt(this, served);
        Current = next;
        RemoteSwapInPlace = false;
        if (fate == "restart" && old is { Live: true })
        {
            openOld = old; swappingSource = old; old.BeginDrain(); old.Self.StopRoutines();
            old.PreserveReferencesAtClose = true;
            old.PreserveSourcesAtClose = !old.OpensNatives;
            var ready = new ReloadReadiness(this, requests);
            readiness = ready;
            SetState(RefState.Loading);
            ready.Changed += () =>
            {
                if (!ReferenceEquals(Current, next) || OpenOldAttempt is null) return;
                // An admitted successor that has not allowed yet shows what its admission parks on.
                if (next.Deciding) return;
                SetState(ready.Cause is { } cause ? RefState.Failed(cause) : RefState.Loading);
                Host.ReloadCycles.Recheck();
            };
            var due = Host.Options.TimeProvider.GetUtcNow() + Host.ReloadDrainTimeout;
            startSuccessor = () =>
            {
                ready.Start();
                TrackRetirement(old, RestartAsync(old, next, ready, due));
            };
            return ready.Checked.Task;
        }
        swappingSource = old is { HasSources: true } ? old : null;
        old?.Seal(Reloaded(), "reload", keepSources: true);
        SetState(RefState.Loading);
        startSuccessor = () =>
        {
            var work = RetryReloadAsync(old, next);
            if (old is null) _ = work; else TrackRetirement(old, work);
        };
        return next.Checked.Task;
    }
    internal void StartSuccessor() { var start = startSuccessor; startSuccessor = null; start?.Invoke(); }

    private async Task RetryReloadAsync(Attempt? old, Attempt next)
    {
        if (next.Served.Admission is not null)
        {
            // Admits at once (in a later turn); its factory waits for the old attempt's close, as every later attempt's does.
            var closing = Close();
            previousClose = Task.WhenAll(Settled(previousClose), closing);
            Activate(next);
            await closing;
            if (ending || Host.Closed || !ReferenceEquals(Current, next)) { next.Seal(Reloaded(), "reload"); next.Checked.TrySetResult(null); return; }
            await next.Checked.Task;
            return;
        }
        await Close();
        async Task Close()
        {
            if (old is null) return;
            try { await old.CloseAsync(NativeCloseReason.Reload); }
            catch (Exception error) { Host.Report(new("close_failed", error, Label)); }
        }
        if (!ending && !Host.Closed && ReferenceEquals(Current, next))
        {
            _ = next.ActivateAsync();
            await next.Checked.Task;
        }
        else { next.Seal(Reloaded(), "reload"); next.Checked.TrySetResult(null); }
    }

    private async Task RestartAsync(Attempt old, Attempt next, ReloadReadiness ready, DateTimeOffset due)
    {
        try
        {
            var admitted = next.Served.Admission is not null;
            var retiring = RetireAsync(old, ready, due);
            if (admitted)
            {
                // The successor admits while the old attempt drains (slice 14 §2.5); its factory, and every later attempt's, waits for
                // the old attempt's close.
                previousClose = Task.WhenAll(Settled(previousClose), Settled(retiring));
                Activate(next);
            }
            await retiring;
            if (ending || Host.Closed || !ReferenceEquals(Current, next))
            { next.Seal(Reloaded(), "reload"); next.Checked.TrySetResult(null); return; }
            if (!admitted) _ = next.ActivateAsync();
            await next.Checked.Task;
            old.Self.ReleaseReferences(); ready.Release();
            if (ReferenceEquals(readiness, ready)) readiness = null;
        }
        finally
        {
            old.DetachSources(Reloaded());
            old.Self.ReleaseReferences(); ready.Release();
            if (ReferenceEquals(readiness, ready)) readiness = null;
        }
    }
    // The old attempt's end in a restart: once its successor's requests are ready and its old work drained it is sealed, then closed. A
    // change of its admission seals it before that (SealDraining): the wait ends at once, with no drain.
    private async Task RetireAsync(Attempt old, ReloadReadiness ready, DateTimeOffset due)
    {
        if (old.Served.Admission is null) await ready.WaitAsync(due);
        else await Task.WhenAny(ready.WaitAsync(due), old.CutTask);
        await old.DrainCallsAsync(due);
        old.Seal(Reloaded(), "reload", keepSources: true, keepContributions: !old.OpensNatives);
        // Sealed: a later attempt's admission that bound its requests lets this one's go.
        ReleaseSuperseded();
        await Documents.SettledAsync();
        StopFollowing(old);
        try { await old.CloseAsync(NativeCloseReason.Reload); }
        catch (Exception error) { Host.Report(new("close_failed", error, Label)); }
    }
    /// <summary>The admission of <paramref name="attempt"/> allowed: while it is a restart's successor waiting for the old attempt, the
    /// object shows what the readiness of its recorded requests says again.</summary>
    internal void AdmissionAllowed(Attempt attempt)
    {
        if (!ReferenceEquals(Current, attempt) || OpenOldAttempt is null || readiness is not { } ready) return;
        SetState(ready.Cause is { } cause ? RefState.Failed(cause) : RefState.Loading);
    }
    private void TrackRetirement(Attempt attempt, Task work)
    {
        OldAttempts.Add(attempt); retiring.Add(work); Host.TrackReloadWork(work, Label); _ = Settle();
        async Task Settle()
        {
            try { await work; } catch { }
            finally { OldAttempts.Remove(attempt); retiring.Remove(work); }
        }
    }
    internal void StopFollowing(Attempt previous)
    {
        if (ReferenceEquals(openOld, previous)) openOld = null;
        if (!ReferenceEquals(swappingSource, previous)) return;
        swappingSource = null;
        Host.Batch.Within(() => { foreach (var holder in Holders.ToArray()) { holder.Retarget(); holder.Moved(); } }, Context);
    }
    internal Task RetiredWork => Task.WhenAll(retiring.ToArray());
    private ChordError Reloaded() => new("released", $"{Label}: reloaded", "ended");
    internal void WithdrawRetiringContributions(Attempt next)
    {
        if (!ReferenceEquals(Current, next)) return;
        foreach (var previous in OldAttempts) previous.WithdrawContributions();
    }
    internal void RemoveForReload()
    {
        var holders = Holders.ToArray(); swappingSource = null; readiness?.Release();
        ending = true;
        try
        {
            foreach (var holder in holders)
                if (holder is Binding binding) binding.UnbindForReload();
                else if (holder is IReloadAwareHolder remote) remote.Removed(new ChordError("unavailable", $"{Served.Contract.Name} is not served here"));
                else holder.End(new ChordError("unavailable", $"{Served.Contract.Name} is not served here"));
        }
        finally { ending = false; }
        UnbindSets();
        Host.TrackReloadWork(EndAsync(new ChordError("released", $"{Label}: removed", "ended"), NativeCloseReason.End), Label);
    }
}

internal sealed class ReloadReadiness(ChordObject owner, IReadOnlyList<RecordedRequest> requests)
{
    private readonly List<Binding> bindings = [];
    private TaskCompletionSource changed = NewSignal();
    private bool released;
    internal IEnumerable<ChordObject> Targets => bindings.Select(binding => binding.Target).OfType<ChordObject>();
    internal readonly TaskCompletionSource<FailureCause?> Checked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal FailureCause? Cause { get; private set; }
    internal event Action? Changed;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Start()
    {
        foreach (var request in requests)
        {
            if (request.Contract is not { } contract || request.Parameters is not { } parameters) continue;
            try
            {
                var binding = owner.Host.Acquire(contract, parameters, owner.Caller, owner.Context);
                binding.Unlist(); bindings.Add(binding); binding.Changed += Wake;
            }
            catch (Exception error) { Cause ??= new(FailureKind.Broken, DataInternal.AsChordError(error, contract.Name)); }
        }
        Recompute(); Checked.TrySetResult(Cause);
    }
    private static bool Skew(Binding binding) => binding.Target is PiSharp.Chord.Remote.RemoteObject && binding.RefState.Cause?.Error is { } error &&
        (error.Code == "contract_mismatch" || error.Code == "unavailable" && error.Reason == "version");
    private void Recompute()
    {
        Cause = bindings.FirstOrDefault(binding => binding.RefState.Kind == "failed" && !Skew(binding)) is { } failed
            ? DataInternal.WaitingFor(failed.Label, failed.RefState.Cause!.Error) : Cause?.Kind == FailureKind.Broken ? Cause : null;
    }
    private void Wake()
    {
        Recompute(); var before = changed; changed = NewSignal(); before.TrySetResult(); Changed?.Invoke();
    }
    internal async Task WaitAsync(DateTimeOffset due)
    {
        while (!released && bindings.Any(binding => binding.RefState.Kind == "loading" || binding.RefState.Kind == "failed" && Skew(binding)))
        {
            var left = due - owner.Host.Options.TimeProvider.GetUtcNow(); if (left <= TimeSpan.Zero) return;
            try { await changed.Task.WaitAsync(left, owner.Host.Options.TimeProvider); } catch (TimeoutException) { return; }
        }
    }
    internal void Release()
    {
        if (released) return; released = true;
        foreach (var binding in bindings) { binding.Changed -= Wake; binding.Release(); }
        bindings.Clear(); changed.TrySetResult();
    }
}

internal interface IReloadAwareHolder { void Removed(ChordError error); }
public sealed partial class ChordHost
{
    private readonly HashSet<Task> reloadWork = [];
    internal void TrackReloadWork(Task work, string label)
    {
        reloadWork.Add(work); _ = Complete();
        async Task Complete()
        {
            try { await work; } catch (Exception error) { Report(new("close_failed", error, label)); }
            finally { reloadWork.Remove(work); }
        }
    }
    public Task DrainReloadsAsync(ChordContext context) => Dispatcher.RunAsync(async () =>
        await Task.WhenAll(reloadWork.ToArray()).WaitAsync(context.AbortSignal ?? CancellationToken.None));
}
