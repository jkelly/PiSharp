using System.Collections.Immutable;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Extensions.Agent;

public enum LoadoutDiagnosticCaptureStatus { Captured, UnknownTool, CapacityReached, RetiredGeneration }
public enum LoadoutDiagnosticDeliveryStatus { Delivered, ReporterFailed, SkippedAfterReporterFailure, StaleGeneration, RetiredGeneration }

/// <summary>Trusted host receipt. Original exception objects are retained here, never copied into wire diagnostics.</summary>
public sealed record LoadoutDiagnosticCapture(string ToolName, Exception PrepareFailure,
    LoadoutDiagnosticCaptureStatus Status, ExtensionEventDiagnostic? Diagnostic);
public sealed record LoadoutDiagnosticDeliveryResult(LoadoutDiagnosticCapture Capture,
    LoadoutDiagnosticDeliveryStatus Status, Exception? ReporterFailure = null);
public sealed record LoadoutDiagnosticDeliveryReceipt(ImmutableArray<LoadoutDiagnosticDeliveryResult> Results)
{
    /// <summary>Optional host error policy: retain both original preparation and reporter causes.</summary>
    public void ThrowIfReporterFailed()
    {
        var failures = Results.Where(result => result.ReporterFailure is not null)
            .SelectMany(result => new[] { result.Capture.PrepareFailure, result.ReporterFailure! }).ToArray();
        if (failures.Length != 0) throw new AggregateException("Prepare-loadout diagnostic reporting failed.", failures);
    }
}

/// <summary>Bounded synchronous capture and explicit awaited delivery for one captured extension binding.
/// Does not run prepare hooks, open callback authority, acquire registry leases, or own the generation lifetime.
/// Host supplies its current Binding.Snapshot and retires the borrowed generation token before replacing it.</summary>
public sealed class LoadoutDiagnosticDelivery
{
    private readonly object gate = new();
    private readonly ExtensionRegistrySnapshot captured;
    private readonly ImmutableDictionary<string, ExtensionEventDiagnostic> identities;
    private readonly Func<ExtensionEventDiagnostic, CancellationToken, ValueTask> report;
    private readonly CancellationToken generationLifetime;
    private readonly int maximumPendingDiagnostics;
    private readonly List<LoadoutDiagnosticCapture> pending = [];
    private readonly AsyncLocal<bool> inDelivery = new();
    private Task<LoadoutDiagnosticDeliveryReceipt>? delivery;
    private int ownedCount;
    private bool stale;

    public LoadoutDiagnosticDelivery(ExtensionRegistrySnapshot captured,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask> report, CancellationToken generationLifetime,
        int maximumPendingDiagnostics = 128)
    {
        ArgumentNullException.ThrowIfNull(captured); ArgumentNullException.ThrowIfNull(report);
        if (!generationLifetime.CanBeCanceled)
            throw new ArgumentException("Diagnostic delivery requires an explicitly owned generation lifetime.", nameof(generationLifetime));
        if (maximumPendingDiagnostics < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingDiagnostics));
        this.captured = captured; this.report = report; this.generationLifetime = generationLifetime;
        this.maximumPendingDiagnostics = maximumPendingDiagnostics;
        identities = captured.Tools.Where(tool => tool.HasLoadoutPreparation).ToImmutableDictionary(tool => tool.Name, tool =>
        {
            var registration = captured.Registrations.Single(entry => entry.Kind == "Tool" && entry.Name == tool.Name &&
                entry.OwnerId == tool.OwnerId && entry.OwnerGeneration == tool.OwnerGeneration);
            return new ExtensionEventDiagnostic("prepare_loadout", tool.OwnerId, tool.OwnerGeneration,
                registration.RegistrationId, ExtensionEventFailure.HandlerFailed);
        }, StringComparer.Ordinal);
    }

    /// <summary>No async work or reporter call. Caller must retain declined captures; do not discard their original causes.
    /// Each accepted occurrence is charged through its original report join, including captures during delivery.</summary>
    public LoadoutDiagnosticCapture Capture(string toolName, Exception prepareFailure)
    {
        ArgumentNullException.ThrowIfNull(toolName); ArgumentNullException.ThrowIfNull(prepareFailure);
        lock (gate)
        {
            if (stale || generationLifetime.IsCancellationRequested)
                return new(toolName, prepareFailure, LoadoutDiagnosticCaptureStatus.RetiredGeneration, null);
            if (!identities.TryGetValue(toolName, out var diagnostic))
                return new(toolName, prepareFailure, LoadoutDiagnosticCaptureStatus.UnknownTool, null);
            if (ownedCount == maximumPendingDiagnostics)
                return new(toolName, prepareFailure, LoadoutDiagnosticCaptureStatus.CapacityReached, diagnostic);
            var item = new LoadoutDiagnosticCapture(toolName, prepareFailure, LoadoutDiagnosticCaptureStatus.Captured, diagnostic);
            pending.Add(item); ownedCount++;
            return item;
        }
    }

    /// <summary>Drain one immutable pending batch. Concurrent callers join the same original delivery task.
    /// Admission cancellation preserves pending captures; once admitted, the borrowed generation lifetime governs reporting.
    /// A mismatched current binding permanently refuses this bridge, but any already admitted reporter is still joined.</summary>
    public Task<LoadoutDiagnosticDeliveryReceipt> DeliverAsync(ExtensionRegistrySnapshot currentBinding,
        CancellationToken admissionToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentBinding);
        if (inDelivery.Value) throw new InvalidOperationException("A diagnostic reporter cannot await its own delivery.");
        admissionToken.ThrowIfCancellationRequested();
        TaskCompletionSource start;
        Task<LoadoutDiagnosticDeliveryReceipt> original;
        lock (gate)
        {
            stale |= !ReferenceEquals(captured, currentBinding);
            if (delivery is { IsCompleted: false }) return delivery;
            delivery = null;
            if (pending.Count == 0) return Task.FromResult(new LoadoutDiagnosticDeliveryReceipt([]));
            var batch = pending.ToImmutableArray(); pending.Clear();
            // The original task first awaits a private start gate, so no trusted reporter runs under this monitor.
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            original = DeliverCoreAsync(batch, start.Task); delivery = original;
        }
        start.SetResult();
        return original;
    }

    /// <summary>Host close admission must refuse before retiring or disposing resources from its own reporter context.</summary>
    public void RefuseReporterInitiatedDisposal()
    {
        if (inDelivery.Value)
            throw new InvalidOperationException("A diagnostic reporter cannot dispose its own activation.");
    }

    private async Task<LoadoutDiagnosticDeliveryReceipt> DeliverCoreAsync(ImmutableArray<LoadoutDiagnosticCapture> batch, Task start)
    {
        await start.ConfigureAwait(false);
        var prior = inDelivery.Value; inDelivery.Value = true;
        var results = ImmutableArray.CreateBuilder<LoadoutDiagnosticDeliveryResult>(batch.Length);
        var failed = false;
        try
        {
            foreach (var item in batch)
            {
                if (failed)
                {
                    results.Add(new(item, LoadoutDiagnosticDeliveryStatus.SkippedAfterReporterFailure)); continue;
                }
                bool isStale; lock (gate) isStale = stale;
                if (isStale || generationLifetime.IsCancellationRequested)
                {
                    results.Add(new(item, isStale ? LoadoutDiagnosticDeliveryStatus.StaleGeneration :
                        LoadoutDiagnosticDeliveryStatus.RetiredGeneration)); continue;
                }
                try
                {
                    await report(item.Diagnostic!, generationLifetime).ConfigureAwait(false);
                    results.Add(new(item, LoadoutDiagnosticDeliveryStatus.Delivered));
                }
                catch (Exception error)
                {
                    // Source error listeners can abort later delivery. Never retry an uncertain reporter effect.
                    results.Add(new(item, LoadoutDiagnosticDeliveryStatus.ReporterFailed, error)); failed = true;
                }
            }
            return new(results.MoveToImmutable());
        }
        finally
        {
            lock (gate) ownedCount -= batch.Length;
            inDelivery.Value = prior;
        }
    }
}
