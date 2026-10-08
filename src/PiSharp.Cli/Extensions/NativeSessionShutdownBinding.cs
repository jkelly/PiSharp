using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Observes the reserved outgoing attachment without reopening session or UI authority.</summary>
internal sealed class NativeSessionShutdownBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
    Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report = null)
{
    internal async ValueTask PublishAsync(AgentSessionAttachment previous, PersistentAgentSession target, string reason)
    {
        var mapped = reason switch
        {
            "switch" => "resume",
            "resume" or "new" or "fork" => reason,
            _ => throw new InvalidOperationException("Outgoing session observation reason is invalid.")
        };
        var retained = CaptureSnapshot(registry, previous);
        var fields = new Dictionary<string, object>
        {
            ["type"] = "session_shutdown", ["reason"] = mapped
        };
        // Volatile sessions have no file. Absence is the optional-field contract;
        // an explicit JSON null is rejected by the strict native dispatcher.
        if (target.SessionFile is { } targetSessionFile)
            fields.Add("targetSessionFile", targetSessionFile);
        var observation = JsonData.Parse(JsonSerializer.Serialize(fields));
        // The registry owns the dedicated notification lifetime and handler failure continuation.
        // A trusted reporter failure propagates before source retirement, as in pinned Pi.
        await registry.DispatchSessionShutdownAsync(captured, observation, report, retained).ConfigureAwait(false);
    }

    internal static ExtensionSessionSnapshot? CaptureSnapshot(ExtensionRegistry registry, AgentSessionAttachment? attached)
    {
        if (attached is null) return null;
        var state = attached.Session.Snapshot;
        if (state.IsDisposed || state.IsRetired)
            throw new InvalidOperationException("Shutdown cannot retain a released session attachment.");
        var snapshot = new ExtensionSessionSnapshot(state.Log.Header.Id, attached.Generation, state.Context.LeafId,
            state.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray())
        {
            Persistence = state.Log.StorageDurability switch
            {
                PiSharp.Sessions.Storage.SessionLogStorageDurability.LocalFileFlush => ExtensionSessionPersistence.DurableLocalFile,
                PiSharp.Sessions.Storage.SessionLogStorageDurability.VolatileMemory => ExtensionSessionPersistence.VolatileMemory,
                PiSharp.Sessions.Storage.SessionLogStorageDurability.DeferredLocalFile => ExtensionSessionPersistence.DeferredLocalFile,
                _ => throw new InvalidOperationException("Shutdown checkpoint storage is unsupported.")
            }
        };
        registry.ValidateSessionSnapshot(snapshot);
        return snapshot;
    }
}
