using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed record SessionGeneratedSummary(string Text, TokenUsage Usage);
public interface ISessionSummaryGenerator
{
    ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken cancellationToken = default);
}
/// <summary>Extension output is immutable data. It receives no tool or session-write authority.</summary>
public sealed record SessionProvidedSummary(string Text, TokenUsage? Usage = null, JsonData? Details = null);
public enum SessionCompactionReason { Manual, Threshold, Overflow }
public sealed record SessionCompactionRequest(SessionCompactionSettings? Settings = null, bool Automatic = false,
    double ContextWindow = 128_000, SessionSummaryRequestOptions? SummaryOptions = null,
    SessionProvidedSummary? ExtensionSummary = null, bool OverrideRetainedBoundary = false, string? FirstKeptEntryId = null)
{
    /// <summary>Explicit observation origin; Automatic controls planning only and cannot establish recovery origin.</summary>
    public SessionCompactionReason Reason { get; init; } = SessionCompactionReason.Manual;
    public bool WillRetry { get; init; }
}
public sealed record SessionBranchSummaryRequest(string? TargetId, double ContextWindow = 128_000,
    double ReserveTokens = 16_384, SessionSummaryRequestOptions? SummaryOptions = null,
    SessionProvidedSummary? ExtensionSummary = null);
public sealed record SessionSummaryCheckpointPreview(SessionEntry Entry, SessionContextProjection Context,
    SessionLogStoreSnapshot PreviousLog);
public sealed record SessionSummaryCheckpointReceipt(SessionEntry Entry, SessionLogAppendResult Append,
    SessionContextProjection Context, SessionCompactionPlan? Plan = null, SessionBranchSummaryPlan? BranchPlan = null);
public sealed record SessionAutomaticCompactionStatus(string Disposition, string? EntryId = null,
    SessionCompactionFailure? Failure = null);

/// <summary>Committed immutable observation. Entry is the source-first matching summary, not necessarily the new checkpoint.</summary>
public sealed record SessionCompactionObservation(SessionEntry CompactionEntry, bool FromExtension,
    SessionCompactionReason Reason, bool WillRetry)
{
    public JsonData ToJson() => JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new
    {
        type = "session_compact", compactionEntry = CompactionEntry.WireBody.Value, fromExtension = FromExtension,
        reason = Reason switch { SessionCompactionReason.Manual => "manual", SessionCompactionReason.Threshold => "threshold",
            SessionCompactionReason.Overflow => "overflow", _ => throw new ArgumentOutOfRangeException(nameof(Reason)) }, willRetry = WillRetry
    }));
}