using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Existing-session preflight with the pinned source observation payload and unchanged native veto/cancellation profile.</summary>
internal sealed class NativeSessionBeforeSwitchBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
    CancellationToken sessionToken)
{
    internal async ValueTask<bool> BeforeSwitchAsync(AgentSessionAttachment previous, PersistentAgentSession target, CancellationToken token)
    {
        if (!await registry.BeforeSessionSwitchAsync(captured,
            new(previous.Session.Snapshot.Log.Header.Id, target.Snapshot.Log.Header.Id, target.Path, target.Snapshot.Context.LeafId),
            token, sessionToken).ConfigureAwait(false)) return false;
        await registry.DispatchObservationsAsync(captured, "session_before_switch",
            JsonData.Parse(JsonSerializer.Serialize(new { type = "session_before_switch", reason = "resume", targetSessionFile = target.Path })),
            token, sessionToken).ConfigureAwait(false);
        return true;
    }
}
