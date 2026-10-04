using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public sealed record AgentPromptStart(ImmutableArray<TranscriptEntry> History,
    ImmutableArray<TranscriptEntry> Inputs, long Timestamp);
/// <summary>Additional custom history messages and a run-owned projection after context hooks.
/// The projection is retained for ContinueAsync, and replaced on the next PromptAsync.</summary>
public sealed record AgentPromptPreparation(ImmutableArray<TranscriptEntry> AdditionalMessages)
{
    public Func<ImmutableArray<TranscriptEntry>, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? AfterContext { get; init; }
}
