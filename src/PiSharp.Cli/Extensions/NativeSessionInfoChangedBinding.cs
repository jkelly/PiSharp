using PiSharp.CodingAgent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

internal sealed class NativeSessionInfoChangedBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
    Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report = null)
{
    internal void Attach(ReplaceableAgentSession owner, AgentSessionAttachment attached)
    {
        owner.ValidateAttachment(attached);
        owner.ConfigureSessionInfoObservationForBinding(attached, async observation =>
        {
            try
            {
                if (!ReferenceEquals(owner.Current, attached)) throw new InvalidOperationException("Metadata attachment generation is stale.");
                await registry.DispatchSessionInfoChangedAsync(captured, observation.ToJson(), report).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Pi detaches the extension promise. Native ownership joins it while preserving the
                // committed name and command behavior; admission/reporter failures cannot undo it.
                if (report is not null)
                    try { await report(new("session_info_changed", "native-host", attached.Generation, "publish",
                        ExtensionEventFailure.HandlerFailed), CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
            }
        });
    }
}
