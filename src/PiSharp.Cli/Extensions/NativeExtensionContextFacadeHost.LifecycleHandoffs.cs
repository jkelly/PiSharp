using PiSharp.Extensions;
using PiSharp.Cli.Commands;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionContextFacadeHost : IExtensionLifecycleHandoffHost
{
    private NativeExtensionActivation? lifecycleActivation;
    internal void BindLifecycleHandoffs(NativeExtensionActivation activation)
    {
        var previous = Interlocked.CompareExchange(ref lifecycleActivation, activation, null);
        if (previous is not null && !ReferenceEquals(previous, activation))
            throw new InvalidOperationException("Lifecycle handoffs already belong to another activation.");
    }
    public void RequestLifecycleHandoff(IExtensionCommandContext originating,
        ExtensionLifecycleHandoffKind kind, CancellationToken admissionCancellation)
    {
        ArgumentNullException.ThrowIfNull(originating);
        admissionCancellation.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var attachment = Capture(originating);
        var activation = Volatile.Read(ref lifecycleActivation) ??
            throw new NotSupportedException("No actual profile lifecycle installation is bound.");
        activation.RequestLifecycleHandoff(originating, attachment, kind, admissionCancellation);
        if (!ReferenceEquals(attachment, Capture(originating)))
            throw new InvalidOperationException("Lifecycle admission crossed attachment replacement.");
    }
}
