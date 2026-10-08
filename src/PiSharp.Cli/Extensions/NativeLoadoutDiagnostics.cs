using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Activation-owned bounded bootstrap; delivery borrows the exact binding and generation lifetime.</summary>
internal sealed class NativeLoadoutDiagnostics(
    Func<ExtensionEventDiagnostic, CancellationToken, ValueTask> reporter, CancellationToken lifetime)
{
    private const int MaximumPending = 128;
    private readonly object _gate = new();
    private readonly List<(string Name, Exception Failure)> _bootstrap = [];
    private LoadoutDiagnosticDelivery? _delivery;
    private ExtensionRegistrySnapshot? _snapshot;
    internal LoadoutDiagnosticDeliveryReceipt? LastReceipt { get; private set; }

    internal void Capture(string name, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (_delivery is null)
            {
                if (lifetime.IsCancellationRequested)
                    throw new OperationCanceledException("Prepare-loadout capture belongs to a retired activation.", failure, lifetime);
                if (_bootstrap.Count == MaximumPending)
                    throw new InvalidOperationException("Prepare-loadout bootstrap capacity reached.", failure);
                _bootstrap.Add((name, failure)); return;
            }
            RequireCapture(_delivery.Capture(name, failure));
        }
    }
    internal void Bind(ExtensionRegistrySnapshot snapshot)
    {
        lock (_gate)
        {
            if (_delivery is not null) throw new InvalidOperationException("Diagnostic activation already bound.");
            var delivery = new LoadoutDiagnosticDelivery(snapshot, reporter, lifetime, MaximumPending);
            foreach (var item in _bootstrap) RequireCapture(delivery.Capture(item.Name, item.Failure));
            _snapshot = snapshot; _delivery = delivery; _bootstrap.Clear();
        }
    }
    internal async ValueTask DrainAsync(CancellationToken admissionToken)
    {
        LoadoutDiagnosticDelivery? delivery; ExtensionRegistrySnapshot? snapshot; Exception[] bootstrapFailures;
        lock (_gate) { delivery = _delivery; snapshot = _snapshot; bootstrapFailures = _bootstrap.Select(item => item.Failure).ToArray(); }
        if (delivery is null && lifetime.IsCancellationRequested && bootstrapFailures.Length != 0)
            throw new AggregateException("Activation retired before diagnostic bootstrap could bind.", bootstrapFailures);
        if (delivery is null) return;
        var receipt = await delivery.DeliverAsync(snapshot!, admissionToken).ConfigureAwait(false);
        if (!receipt.Results.IsEmpty) { lock (_gate) LastReceipt = receipt; }
        receipt.ThrowIfReporterFailed();
    }
    internal void RefuseReporterInitiatedDisposal()
    {
        LoadoutDiagnosticDelivery? delivery;
        lock (_gate) delivery = _delivery;
        delivery?.RefuseReporterInitiatedDisposal();
    }
    private static void RequireCapture(LoadoutDiagnosticCapture capture)
    {
        if (capture.Status != LoadoutDiagnosticCaptureStatus.Captured)
            throw new InvalidOperationException("Prepare-loadout diagnostic capture refused: " + capture.Status, capture.PrepareFailure);
    }
}
