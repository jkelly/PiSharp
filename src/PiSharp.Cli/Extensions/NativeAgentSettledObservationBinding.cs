// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (agent_settled)
// and packages/coding-agent/src/core/extensions/runner.ts (emit: report a handler error and continue).
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Delivers the session-level <c>agent_settled</c> observation, with <c>aborted</c>, to native extension observers of
/// one captured registration revision on each actual attachment generation. Delivery runs inside the session's settlement
/// publication, so a handler cannot await the same session's idle state.</summary>
internal sealed class NativeAgentSettledObservationBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
    Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report = null)
{
    private readonly object _gate = new();
    private IDisposable? _subscription;

    internal void Attach(ReplaceableAgentSession owner, AgentSessionAttachment attached)
    {
        owner.ValidateAttachment(attached);
        var next = attached.Session.SubscribeOperationEvents(new Sink(this, owner, attached));
        IDisposable? previous;
        lock (_gate) { previous = _subscription; _subscription = next; }
        previous?.Dispose();
    }

    internal static JsonData Observation(bool aborted) =>
        JsonData.Parse(JsonSerializer.Serialize(new { type = "agent_settled", aborted }));

    private async ValueTask PublishAsync(ReplaceableAgentSession owner, AgentSessionAttachment attached, SessionOperationSettled settled)
    {
        try
        {
            // Never retarget an old publisher to the replacement attachment.
            if (!ReferenceEquals(owner.Current, attached)) return;
            await registry.DispatchObservationsAsync(captured, "agent_settled", Observation(settled.Aborted),
                CancellationToken.None, attached.LifetimeToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An observer failure is reported; it never fails the settled run.
            if (report is not null)
                try { await report(new("agent_settled", "native-host", attached.Generation, "publish", ExtensionEventFailure.HandlerFailed),
                    CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
        }
    }

    private sealed class Sink(NativeAgentSettledObservationBinding binding, ReplaceableAgentSession owner, AgentSessionAttachment attached)
        : ISessionOperationEventSink
    {
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken cancellationToken) =>
            observation is SessionOperationSettled settled ? binding.PublishAsync(owner, attached, settled) : ValueTask.CompletedTask;
    }
}
