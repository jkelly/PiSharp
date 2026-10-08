using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private readonly Dictionary<ExtensionSessionSnapshot, ProfileRuntimeView> shutdownViews = new(ReferenceEqualityComparer.Instance);
    public JsonData CommandCatalog
    {
        get { var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter(); return view.CommandCatalog; }
    }
    public async ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken token)
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter();
        if (view.Extension is null) throw new InvalidOperationException("No active extension command revision.");
        return await view.Extension.CompleteCommandAsync(name, prefix, token).ConfigureAwait(false);
    }
    internal async ValueTask StartLifecycleAsync(string reason, CancellationToken token)
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter();
        if (view.Extension is not null) await view.Extension.DispatchSessionStartAsync(reason, token).ConfigureAwait(false);
    }
    internal ExtensionSessionSnapshot? CaptureShutdownSessionSnapshot()
    {
        var attachment = Sessions?.Current;
        var view = CaptureRuntimeView(attachment);
        var extension = view.Extension;
        // Startup rejection may already have retired the acquired runtime hold.
        // An empty view has no native shutdown callback or snapshot to borrow.
        if (extension is null) return null;
        using var use = view.Lifetime.Enter();
        var snapshot = extension.CaptureShutdownSessionSnapshot(attachment);
        if (snapshot is not null) lock (viewGate) shutdownViews[snapshot] = view;
        return snapshot;
    }
    internal async ValueTask<bool> DispatchSessionShutdownAsync(ExtensionSessionSnapshot? retained)
    {
        ProfileRuntimeView view;
        if (retained is not null)
        {
            lock (viewGate)
                view = shutdownViews.TryGetValue(retained, out var captured) ? captured :
                    throw new InvalidOperationException("Shutdown snapshot has no captured profile view.");
        }
        else view = CaptureRuntimeView();
        var extension = view.Extension;
        if (extension is null) return false;
        using var use = view.Lifetime.Enter();
        return await extension.DispatchSessionShutdownAsync(retained).ConfigureAwait(false);
    }
    internal async ValueTask DrainLoadoutDiagnosticsAsync(CancellationToken token)
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter();
        if (view.Extension is not null) await view.Extension.DrainLoadoutDiagnosticsAsync(token).ConfigureAwait(false);
    }
}
