// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/agent-session.ts.
// Native session-delivery integration for queued image normalization.
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    /// <summary>Host-owned user image normalization at delivery, with the current model. Runs before public message events and persistence.</summary>
    public Func<ModelDescriptor, TranscriptEntry, CancellationToken, ValueTask<TranscriptEntry>>? InputNormalizer { get; set; }

    private static AgentOptions WithInputNormalizer(AgentOptions? options, Bridge bridge)
    {
        var configured = options ?? new();
        var previous = configured.NormalizeInput;
        return configured with { NormalizeInput = async (model, message, token) =>
        {
            if (previous is not null) message = await previous(model, message, token).ConfigureAwait(false);
            return await bridge.NormalizeInput(model, message, token).ConfigureAwait(false);
        } };
    }
}
