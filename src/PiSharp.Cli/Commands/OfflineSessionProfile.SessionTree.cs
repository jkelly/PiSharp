using PiSharp.CodingAgent;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    internal async ValueTask PublishSelectedTreeAsync(SessionTreeNavigationReceipt receipt, string? oldLeafId)
    {
        var view = CaptureRuntimeView(receipt.View.Attachment);
        using var use = view.Lifetime.Enter();
        if (view.Extension is not null)
            await view.Extension.DispatchSessionTreeAsync(
                Sessions ?? throw new InvalidOperationException("No actual session owner."), receipt, oldLeafId).ConfigureAwait(false);
    }
}
