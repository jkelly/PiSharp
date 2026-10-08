using PiSharp.Cli.Extensions;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    // ConfigureEffectiveSettings/CaptureEffectiveSettings are defined only by the typed reload
    // commit bridge. This leaf consumes its actual attachment-keyed acknowledged snapshot.
    internal void BindSettingsThinkingReads()
    {
        RequireStartupViewMutable();
        _extension?.ConfigureSettingsThinkingReads(CaptureEffectiveSettings);
    }

    // The reload/view owner invokes this once within its actual view.Lifetime.BindOnce callback,
    // before AttachOwner. The startup activation was already bound at the startup callsite.
    internal void BindSettingsThinkingReads(NativeExtensionActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (!ReferenceEquals(activation, _extension))
            activation.ConfigureSettingsThinkingReads(CaptureEffectiveSettings);
    }
}
