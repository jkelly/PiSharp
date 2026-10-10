using PiSharp.Cli.Extensions;
using PiSharp.Contracts;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    // ConfigureEffectiveSettings/CaptureEffectiveSettings are defined only by the typed reload
    // commit bridge. This leaf consumes its actual attachment-keyed acknowledged snapshot.
    internal void BindSettingsThinkingReads()
    {
        RequireStartupViewMutable();
        _extension?.ConfigureSettingsThinkingReads(CaptureEffectiveSettings);
        _extension?.ConfigureModelSwitchThinking(ModelSwitchThinkingLevel);
    }

    /// <summary>The settings an extension's setModel reads for the new model's thinking level (the Pi entry re-reads the settings
    /// files); unset, the current attachment's effective settings.</summary>
    internal Func<ModelDescriptor, string?>? ModelSwitchThinking { get; set; }
    private string? ModelSwitchThinkingLevel(ModelDescriptor model)
    {
        if (ModelSwitchThinking is { } resolve) return resolve(model);
        if (Sessions is not { } owner) return null;
        try { return RpcSessionCommand.ModelSwitchThinkingLevel(CaptureEffectiveSettings(owner.Current), model); }
        catch (InvalidOperationException) { return null; }
    }

    // The reload/view owner invokes this once within its actual view.Lifetime.BindOnce callback,
    // before AttachOwner. The startup activation was already bound at the startup callsite.
    internal void BindSettingsThinkingReads(NativeExtensionActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (!ReferenceEquals(activation, _extension))
        {
            activation.ConfigureSettingsThinkingReads(CaptureEffectiveSettings);
            activation.ConfigureModelSwitchThinking(ModelSwitchThinkingLevel);
        }
    }
}
