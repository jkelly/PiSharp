using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Cli.Extensions;
using PiSharp.Sessions.Compaction;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private void BindProfileSessionBehaviors(NativeExtensionActivation activation,ProfileRuntimeView bindingView)
    {
        activation.ConfigureSessionBehaviorCompaction((attachment,instructions,token)=>
        {
            var owner=Sessions ?? throw new InvalidOperationException("Typed compaction requires an admitted session owner.");
            owner.ValidateAttachment(attachment);
            if(!ReferenceEquals(owner.Current,attachment)||!ReferenceEquals(CaptureRuntimeView(attachment),bindingView))
                throw new InvalidOperationException("Typed compaction belongs to its actual captured profile/view.");
            var raw=SelectedModelDefinition.Raw.Value;
            if(!raw.TryGetProperty("contextWindow",out var window)||!window.TryGetDouble(out var contextWindow)||
                !double.IsFinite(contextWindow)||contextWindow<=0)throw new InvalidOperationException("Actual model has no finite compaction window.");
            var settings=new SessionCompactionSettings();
            var snapshot=CaptureEffectiveSettings(attachment);
            if(snapshot is not null&&snapshot.Values.Value.TryGetProperty("compaction",out var compaction))
            {
                if(compaction.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Actual compaction settings are not an object.");
                double Number(string name,double current)
                {
                    if(!compaction.TryGetProperty(name,out var value))return current;
                    if(!value.TryGetDouble(out var parsed)||!double.IsFinite(parsed)||parsed<0)
                        throw new InvalidOperationException("Actual compaction token setting is invalid.");
                    return parsed;
                }
                settings=settings with {ReserveTokens=Number("reserveTokens",settings.ReserveTokens),KeepRecentTokens=Number("keepRecentTokens",settings.KeepRecentTokens)};
            }
            token.ThrowIfCancellationRequested();
            // Return the exact already-admitted owner original; registry command settlement keeps
            // the activation alive through kernel join and registered admission release.
            return owner.CompactAsync(attachment,new(settings,ContextWindow:contextWindow,
                SummaryOptions:new(CustomInstructions:instructions)),SummaryGenerator,token);
        });
    }

    /// <summary>agent-session.ts compact(): the manual compaction request of the current session with the effective compaction
    /// settings (reserveTokens, keepRecentTokens) and the model's context window, as a Pi extension's ctx.compact() runs it.</summary>
    internal SessionCompactionRequest ManualCompactionRequest(string? instructions)
    {
        var attachment = Sessions?.Current ?? throw new InvalidOperationException("Compaction requires an admitted session owner.");
        var settings = new SessionCompactionSettings();
        if (CaptureEffectiveSettings(attachment) is { } snapshot && snapshot.Values.Value.TryGetProperty("compaction", out var compaction) &&
            compaction.ValueKind == JsonValueKind.Object)
        {
            double Number(string name, double current) => compaction.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) &&
                double.IsFinite(parsed) && parsed >= 0 ? parsed : current;
            settings = settings with { ReserveTokens = Number("reserveTokens", settings.ReserveTokens), KeepRecentTokens = Number("keepRecentTokens", settings.KeepRecentTokens) };
        }
        var contextWindow = SelectedModelDefinition.Raw.Value.TryGetProperty("contextWindow", out var window) && window.TryGetDouble(out var value) &&
            double.IsFinite(value) && value > 0 ? value : 128_000;
        return new(settings, ContextWindow: contextWindow, SummaryOptions: new(CustomInstructions: instructions));
    }
}
