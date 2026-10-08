using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed record SessionTreeNavigationOptions(bool Summarize = false, string? CustomInstructions = null,
    bool ReplaceInstructions = false, string? Label = null);
public sealed record SessionTreeOptionOverride<T>(T Value);
public sealed record SessionTreePreparationResult(bool Cancel = false, SessionProvidedSummary? Summary = null,
    SessionTreeOptionOverride<string?>? CustomInstructions = null,
    SessionTreeOptionOverride<bool>? ReplaceInstructions = null, SessionTreeOptionOverride<string?>? Label = null)
{ public ImmutableArray<SessionBoundaryOriginalEvidence> Originals {get;init;}=[]; }

/// <summary>Actual host supplies an already admitted generator. Inert options never acquire one.</summary>
public sealed record SessionTreeSummaryBudget(double ContextWindow,double ReserveTokens);
public sealed record SessionTreeNavigationExecution(ISessionSummaryGenerator? Generator,
    double ContextWindow = 128_000, double ReserveTokens = 16_384,
    Func<SessionTreeNavigationPreview, SessionTreeNavigationOptions, CancellationToken, ValueTask<SessionTreePreparationResult?>>? BeforeTree = null,
    Func<SessionTreeNavigationReceipt, CancellationToken, ValueTask>? AfterTree = null,
    Func<PiSharp.Sessions.Context.SessionContextProjection,CancellationToken,ValueTask>? ValidateProspective = null)
{
    /// <summary>Read-only host capture used only when default summarization consumes its budget.</summary>
    public Func<SessionTreeSummaryBudget>? CaptureSummaryBudget {get;init;}
}
public sealed record SessionTreeCheckpoint(SessionLogAppendResult Append, JsonData? SummaryEntry, JsonData? LabelEntry);
public sealed class SessionTreeCommittedObservationException(SessionTreeNavigationReceipt receipt, Exception inner)
    : Exception("Tree navigation committed before its observation failed.", inner)
{ public SessionTreeNavigationReceipt Receipt { get; } = receipt; }
public sealed class SessionTreeCheckpointPublicationException(SessionTreeCheckpoint checkpoint, Exception inner)
    : Exception("Tree checkpoint acknowledged before publication failed.", inner)
{ public SessionTreeCheckpoint Checkpoint { get; } = checkpoint; }
