using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionContextFacadeHost : IExtensionSettingsThinkingReadHost
{
    private Func<AgentSessionAttachment, StartupSettingsSnapshot?>? settingsThinkingCapture;

    // Only application composition can bind this capture. Extension facets expose no publisher,
    // delegate or snapshot slot. The profile bridge validates the exact attachment and bound view.
    internal void ConfigureSettingsThinkingReads(Func<AgentSessionAttachment, StartupSettingsSnapshot?> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.GetInvocationList().Length != 1)
            throw new ArgumentException("One admitted effective settings capture is required.", nameof(capture));
        if (Volatile.Read(ref owner) is not null)
            throw new InvalidOperationException("Effective settings reads must be bound before attachment.");
        if (Interlocked.CompareExchange(ref settingsThinkingCapture, capture, null) is not null)
            throw new InvalidOperationException("Effective settings reads are already bound.");
    }

    public JsonData GetSettings(IExtensionContext context) => Read(context, attached =>
    {
        var capture = Volatile.Read(ref settingsThinkingCapture) ??
            throw new NotSupportedException("No typed effective settings capture has been admitted.");
        var snapshot = capture(attached) ??
            throw new NotSupportedException("The current attachment has no acknowledged effective settings snapshot.");
        return JsonData.FromElement(snapshot.Values.Value);
    });

    private Func<ModelDescriptor, string?>? modelSwitchThinking;
    /// <summary>agent-session.ts _getThinkingLevelForModelSwitch for an extension's setModel: the settings' per-model level, else
    /// defaultThinkingLevel; null keeps the current level (setThinkingLevel clamps either to the new model).</summary>
    internal void ConfigureModelSwitchThinking(Func<ModelDescriptor, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        Volatile.Write(ref modelSwitchThinking, resolve);
    }
    internal string? ModelSwitchThinkingLevel(ModelDescriptor model) => Volatile.Read(ref modelSwitchThinking)?.Invoke(model);

    public string GetThinkingLevel(IExtensionContext context)
        => Read(context, attached => attached.Session.Snapshot.Context.ThinkingLevel);
}
