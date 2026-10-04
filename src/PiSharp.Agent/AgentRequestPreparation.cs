using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public sealed record AgentRequestBoundary(AgentLoopSnapshot Snapshot, ImmutableArray<TranscriptEntry> PendingInputs);
/// <summary>Trusted host publication: invoke publish once after acknowledgment and before returning.
/// The Agent validates configuration/messages and builds its scheduler before calling the delegate.</summary>
public sealed record AgentRequestPreparation(AgentConfiguration Configuration,
    ImmutableArray<TranscriptEntry> AdditionalSystemMessages,
    Func<Action, CancellationToken, ValueTask> PublishAsync)
{
    /// <summary>Only system tool intent may change; other admitted values retain their original identity.</summary>
    public ImmutableArray<TranscriptEntry>? ProjectedPendingInputs { get; init; }
}
public sealed record AgentLoopRequestPreparation(TurnRunner Runner, ImmutableArray<TranscriptEntry> AdditionalSystemMessages,
    ImmutableArray<TranscriptEntry> PendingInputs);
/// <summary>Host revision changed before write admission. No acknowledgment or publication occurred.</summary>
public sealed class AgentRequestBoundaryStaleException : InvalidOperationException
{ public AgentRequestBoundaryStaleException() : base("Request boundary changed before write admission.") { } }
