using PiSharp.CodingAgent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>One captured native registration revision, installed on each actual attachment generation.</summary>
internal sealed class NativeSessionCompactionObservationBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
    Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report = null)
{
    internal void Attach(ReplaceableAgentSession owner, AgentSessionAttachment attached)
    {
        owner.ValidateAttachment(attached);
        owner.ConfigureCompactionObservationForBinding(attached, async observation =>
        {
            try
            {
                // Never retarget an old publisher to the replacement attachment.
                if (!ReferenceEquals(owner.Current, attached)) throw new InvalidOperationException("Compaction attachment generation is stale.");
                await registry.DispatchSessionCompactAsync(captured, observation.ToJson(), report).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Host admission/immutable JSON bounds can refuse delivery after commit, never poison the writer.
                if (report is not null)
                    try { await report(new("session_compact", "native-host", attached.Generation, "publish",
                        ExtensionEventFailure.HandlerFailed), CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
            }
        });
    }
}
