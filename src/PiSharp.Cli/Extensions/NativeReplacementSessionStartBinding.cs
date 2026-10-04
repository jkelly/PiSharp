using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Publishes only the actual committed replacement, using the native binding's captured revision.</summary>
internal sealed class NativeReplacementSessionStartBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
    Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report = null)
{
    internal async ValueTask PublishAsync(ReplaceableAgentSession owner, AgentSessionReplacement replacement)
    {
        try
        {
            owner.ValidateAttachment(replacement.Current);
            if (replacement.Current.Generation != checked(replacement.Previous.Generation + 1))
                throw new InvalidOperationException("Replacement attachment generation is invalid.");
            var reason = replacement.Reason switch
            {
                "switch" => "resume",
                "resume" or "new" or "fork" => replacement.Reason,
                _ => throw new InvalidOperationException("Replacement observation reason is invalid.")
            };
            var observation = JsonData.Parse(JsonSerializer.Serialize(new
            {
                type = "session_start", reason, previousSessionFile = replacement.Previous.Session.SessionFile
            }));
            await registry.DispatchReplacementSessionStartAsync(captured, observation, report).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Delivery refusal after commit cannot undo replacement, retarget an old generation or poison its writer.
            if (report is not null)
                try { await report(new("session_start", "native-host", replacement.Current.Generation, "publish",
                    ExtensionEventFailure.HandlerFailed), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
        }
    }
}
