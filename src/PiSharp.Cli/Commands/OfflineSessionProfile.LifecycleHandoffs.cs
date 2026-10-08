using PiSharp.Extensions;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private readonly object lifecycleGate = new();
    private readonly List<NativeLifecycleOrigin> lifecycleRequests = [];
    private readonly Dictionary<Task, (string Phase, AggregateException? Aggregate, Exception? Direct)> lifecycleOriginals =
        new(ReferenceEqualityComparer.Instance);
    private Func<Task?>? lifecycleModeStop;
    private bool lifecycleClosing, lifecycleDraining;
    internal bool LifecycleShutdownRequested { get; private set; }
    internal void ConfigureLifecycleModeStop(Func<Task?> stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        if (stop.GetInvocationList().Length != 1) throw new ArgumentException("One actual mode stop request is required.", nameof(stop));
        lock (lifecycleGate)
        {
            if (lifecycleModeStop is not null || lifecycleClosing) throw new InvalidOperationException("Lifecycle mode already installed or closing.");
            lifecycleModeStop = stop;
        }
    }
    internal void EnqueueLifecycleHandoff(NativeLifecycleOrigin origin, IExtensionCommandContext context,
        ExtensionLifecycleHandoffKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested(); context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        var owner = Sessions ?? throw new NotSupportedException("Lifecycle handoffs require the actual profile session owner.");
        owner.ValidateAttachment(origin.Attachment);
        if (!ReferenceEquals(owner.Current, origin.Attachment) || !ReferenceEquals(CaptureRuntimeView(origin.Attachment).Extension, origin.Activation))
            throw new InvalidOperationException("Lifecycle request differs from the actual current runtime view.");
        lock (lifecycleGate)
        {
            if (lifecycleDraining) throw new InvalidOperationException("Lifecycle callbacks may not enqueue during active retirement.");
            if (lifecycleClosing || _disposal is not null) throw new ObjectDisposedException(nameof(OfflineSessionProfile));
            if (kind == ExtensionLifecycleHandoffKind.Reload && reloadAdmission is null)
                throw new NotSupportedException("No genuine native reload admission is installed.");
            if (kind == ExtensionLifecycleHandoffKind.Shutdown && lifecycleModeStop is null)
                throw new NotSupportedException("No actual mode stop intent is installed.");
            if (origin.Request is { } prior)
            {
                if (prior != kind || origin.OwnerId != context.OwnerId || origin.OwnerGeneration != context.OwnerGeneration)
                    throw new InvalidOperationException("Conflicting lifecycle handoff from one origin.");
                return;
            }
            if (lifecycleRequests.Count >= 256) throw new InvalidOperationException("Lifecycle handoff limit reached.");
            origin.OwnerId = context.OwnerId; origin.OwnerGeneration = context.OwnerGeneration; origin.Request = kind;
            lifecycleRequests.Add(origin);
        }
    }
    /// <summary>Invoked only after the host's actual input/run and writer originals settle, outside their gates.</summary>
    internal async Task DrainLifecycleHandoffsAsync(PersistentAgentSession originating)
    {
        NativeLifecycleOrigin[] pending;
        lock (lifecycleGate)
        {
            if (lifecycleClosing) return;
            pending = lifecycleRequests.Where(origin => !origin.Started && origin.Finished && origin.Original?.IsCompleted == true
                && ReferenceEquals(origin.Attachment.Session, originating)).ToArray();
            if (pending.Length == 0) return;
            if (lifecycleDraining) throw new InvalidOperationException("Lifecycle drains may not reenter or overlap.");
            lifecycleDraining = true;
        }
        var failures = new List<Exception>();
        try
        {
            foreach (var origin in pending)
            {
                Task? original = null;
                try
                {
                    var owner = Sessions ?? throw new ObjectDisposedException(nameof(OfflineSessionProfile));
                    owner.ValidateAttachment(origin.Attachment);
                    origin.Attachment.LifetimeToken.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(owner.Current, origin.Attachment) || !ReferenceEquals(CaptureRuntimeView(origin.Attachment).Extension, origin.Activation))
                        throw new InvalidOperationException("A lifecycle handoff lost its admitted generation before effects.");
                    lock (lifecycleGate) origin.Started = true;
                    if (origin.Request == ExtensionLifecycleHandoffKind.Reload)
                    {
                        var reload = ReloadAsync(origin.Attachment, CancellationToken.None);
                        original = reload;
                        await ObserveLifecycleOriginal("reload", reload).ConfigureAwait(false);
                        if (!reload.Result.Workflow.Failures.IsEmpty)
                            throw new AggregateException("Queued reload workflow failed.", reload.Result.Workflow.Failures.Select(failure => failure.Cause));
                    }
                    else
                    {
                        LifecycleShutdownRequested = true;
                        original = (lifecycleModeStop ?? throw new NotSupportedException("No actual mode stop."))()
                            ; // Synchronous one-shot intent has no Task; never invent an original.
                        if (original is not null) await ObserveLifecycleOriginal("mode-stop-intent", original).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    lock (lifecycleGate) origin.Started = true;
                    lock (lifecycleGate) failures.Add(original is not null && lifecycleOriginals.TryGetValue(original, out var evidence)
                        ? (Exception?)evidence.Aggregate ?? evidence.Direct ?? error : error);
                }
            }
        }
        finally { lock (lifecycleGate) lifecycleDraining = false; }
        if (failures.Count != 0) throw new AggregateException("Owned lifecycle handoff failed.", failures);
    }
    private async Task ObserveLifecycleOriginal(string phase, Task original)
    {
        Exception? direct = null;
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; throw; }
        finally
        {
            lock (lifecycleGate)
                if (!lifecycleOriginals.ContainsKey(original))
                    lifecycleOriginals.Add(original, (phase, original.IsFaulted ? original.Exception : null, direct));
        }
    }
    internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedLifecycleOriginals
    {
        get
        {
            lock (lifecycleGate)
                return lifecycleOriginals.Select(pair => (pair.Value.Phase, pair.Key, pair.Value.Aggregate, pair.Value.Direct))
                    .Concat(lifecycleRequests.Where(origin => origin.Finished && origin.Original is not null)
                        .Select(origin => ("native-command-origin", origin.Original!, origin.Aggregate, origin.Direct))).ToArray();
        }
    }
    // Conservative public close fence includes unrelated captured ExecutionContexts. External close must retry
    // after the active drain; stable profile close identity is preserved outside this explicitly refused interval.
    private void RefuseLifecycleDrainClose()
    {
        lock (lifecycleGate)
            if (lifecycleDraining) throw new InvalidOperationException("Profile close cannot join an active lifecycle drain.");
    }
    private async Task CloseLifecycleHandoffsAsync()
    {
        Task[] originals;
        lock (lifecycleGate)
        {
            lifecycleClosing = true;
            originals = lifecycleOriginals.Keys.Concat(lifecycleRequests.Where(origin => origin.Original is not null)
                .Select(origin => origin.Original!)).Distinct<Task>(ReferenceEqualityComparer.Instance).ToArray();
        }
        var failures = new List<Exception>();
        foreach (var original in originals)
        {
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (lifecycleGate)
                {
                    if (lifecycleOriginals.TryGetValue(original, out var captured)) failures.Add((Exception?)captured.Aggregate ?? captured.Direct ?? error);
                    else
                    {
                        var origin = lifecycleRequests.First(item => ReferenceEquals(item.Original, original));
                        failures.Add((Exception?)origin.Aggregate ?? origin.Direct ?? error);
                    }
                }
            }
        }
        if (failures.Count != 0) throw new AggregateException("Lifecycle source originals failed during profile close.", failures);
    }
}
