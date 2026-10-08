using PiSharp.CodingAgent;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    internal ValueTask DispatchSessionTreeAsync(ReplaceableAgentSession owner,
        SessionTreeNavigationReceipt receipt, string? oldLeafId) =>
        new NativeSessionTreeObservationBinding(_registry, Binding.Snapshot).PublishAsync(owner, receipt, oldLeafId);
}
