using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Compaction;

public sealed class SessionCompactionPlanner
{
    private readonly SessionCompactionPlanningOptions options;
    public SessionCompactionPlanner(SessionCompactionPlanningOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.MaximumEntries <= 0 || this.options.MaximumMessages <= 0 || this.options.MaximumCharacters <= 0)
            throw new SessionCompactionException(SessionCompactionFailure.InvalidSettings);
    }
    public SessionCompactionPlan? Prepare(SessionContextProjection projection, SessionCompactionSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection); settings ??= new(); settings.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (projection.Ancestry.Length > options.MaximumEntries) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        if (!projection.Ancestry.IsEmpty && projection.Ancestry[^1].Kind == SessionEntryKind.Compaction) return null;
        var entries = projection.ContextEntries; var previousIndex = -1;
        for (var i = 0; i < entries.Length; i++) if (entries[i].SourceEntry.Kind == SessionEntryKind.Compaction && !entries[i].Messages.IsEmpty) { previousIndex = i; break; }
        var start = previousIndex + 1; var end = entries.Length; var cut = Cut(entries, start, end, settings.KeepRecentTokens, cancellationToken);
        if (cut.Kept < 0 || cut.Kept >= entries.Length || string.IsNullOrEmpty(entries[cut.Kept].SourceEntry.Id)) return null;
        var historyEnd = cut.Split ? cut.Turn : cut.Kept;
        var messages = Extract(entries, start, historyEnd); var prefix = cut.Split ? Extract(entries, cut.Turn, cut.Kept) : ImmutableArray<TranscriptEntry>.Empty;
        if (messages.IsEmpty && prefix.IsEmpty) return null;
        var fileOps = new SessionFileOperationsBuilder(); if (previousIndex >= 0) fileOps.Details(entries[previousIndex].SourceEntry);
        long characters = 0;
        foreach (var message in messages.Concat(prefix))
        {
            cancellationToken.ThrowIfCancellationRequested(); characters += message.WireBody.ToString().Length;
            if (characters > options.MaximumCharacters || messages.Length + prefix.Length > options.MaximumMessages) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
            fileOps.Message(message);
        }
        return new(entries[cut.Kept].SourceEntry.Id!, messages, prefix, cut.Split,
            SessionCompactionTokenEstimator.EstimateProjectedContextTokens(projection).Tokens,
            previousIndex < 0 ? null : entries[previousIndex].SourceEntry.WireBody.Value.GetProperty("summary").GetString(),
            fileOps.Snapshot(), settings, projection.LeafId);
    }
    /// <summary>Explicit native retained boundary. Null selects retain-none; a tool-result boundary is rejected.</summary>
    public SessionCompactionPlan? PrepareWithBoundary(SessionContextProjection projection, string? firstKeptEntryId,
        SessionCompactionSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection); settings ??= new(); settings.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (projection.Ancestry.Length > options.MaximumEntries) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        var entries = projection.ContextEntries; var previous = -1;
        for (var i = 0; i < entries.Length; i++) if (entries[i].SourceEntry.Kind == SessionEntryKind.Compaction && !entries[i].Messages.IsEmpty) { previous = i; break; }
        var start = previous + 1; var kept = firstKeptEntryId is null ? entries.Length : -1;
        for (var i = start; i < entries.Length && firstKeptEntryId is not null; i++)
            if (entries[i].SourceEntry.Id == firstKeptEntryId) { kept = i; break; }
        if (kept < start || kept < entries.Length && (entries[kept].SourceEntry.Kind == SessionEntryKind.Compaction || !entries[kept].Messages.Any(CutMessage)))
            throw new SessionCompactionException(SessionCompactionFailure.InvalidBoundary);
        var turn = -1;
        if (kept < entries.Length && !entries[kept].Messages.Any(TurnMessage))
            for (var i = kept - 1; i >= start; i--) if (entries[i].Messages.Any(TurnMessage)) { turn = i; break; }
        var messages = Extract(entries, start, turn < 0 ? kept : turn);
        var prefix = turn < 0 ? ImmutableArray<TranscriptEntry>.Empty : Extract(entries, turn, kept);
        if (messages.IsEmpty && prefix.IsEmpty) return null;
        var fileOps = new SessionFileOperationsBuilder(); if (previous >= 0) fileOps.Details(entries[previous].SourceEntry);
        long characters = 0; foreach (var message in messages.Concat(prefix))
        {
            cancellationToken.ThrowIfCancellationRequested(); characters += message.WireBody.ToString().Length;
            if (characters > options.MaximumCharacters || messages.Length + prefix.Length > options.MaximumMessages)
                throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
            fileOps.Message(message);
        }
        return new(firstKeptEntryId, messages, prefix, turn >= 0, SessionCompactionTokenEstimator.EstimateProjectedContextTokens(projection).Tokens,
            previous < 0 ? null : entries[previous].SourceEntry.WireBody.Value.GetProperty("summary").GetString(), fileOps.Snapshot(), settings, projection.LeafId);
    }
    private static ImmutableArray<TranscriptEntry> Extract(ImmutableArray<SessionContextContribution> entries, int start, int end) =>
        entries.Skip(start).Take(end - start).Where(entry => entry.SourceEntry.Kind != SessionEntryKind.Compaction)
            .SelectMany(entry => entry.Messages).Where(message => message.Role != "system").ToImmutableArray();
    private static (int Kept, int Turn, bool Split) Cut(ImmutableArray<SessionContextContribution> entries, int start, int end,
        double keepRecentTokens, CancellationToken token)
    {
        var points = Enumerable.Range(start, end - start).Where(i => entries[i].SourceEntry.Kind != SessionEntryKind.Compaction && entries[i].Messages.Any(CutMessage)).ToArray();
        if (points.Length == 0) return (start, -1, false);
        double accumulated = 0; var exceeded = false; var cut = points[0];
        for (var i = end - 1; i >= start; i--)
        {
            token.ThrowIfCancellationRequested(); var tokens = entries[i].Messages.Sum(SessionCompactionTokenEstimator.EstimateTokens); if (tokens == 0) continue;
            accumulated += tokens;
            if (accumulated >= keepRecentTokens) { exceeded = true; cut = points.FirstOrDefault(point => point >= i, points[^1]); break; }
        }
        var suffix = entries.Skip(cut + 1).Take(end - cut - 1).ToArray();
        bool Intrinsic(SessionContextContribution item) => item.SourceEntry.Kind != SessionEntryKind.ContextEdit &&
            !SessionContextInfluenceProjector.ProjectEntry(item.SourceEntry, null, token).IsEmpty;
        bool Omitted(SessionContextContribution item) => Intrinsic(item) && item.Messages.IsEmpty;
        var omitted = suffix.Where(Omitted).Select(item => item.SourceEntry.Id).ToHashSet(StringComparer.Ordinal);
        var externalReplacement = suffix.Any(item => item.SourceEntry.Kind == SessionEntryKind.ContextEdit &&
            item.SourceEntry.WireBody.Value.GetProperty("replacement").ValueKind != JsonValueKind.Null &&
            !omitted.Contains(item.SourceEntry.WireBody.Value.GetProperty("targetId").GetString()!));
        if (exceeded && !externalReplacement && suffix.Any(item => item.SourceEntry.Kind == SessionEntryKind.Message &&
                item.SourceEntry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "assistant" && Omitted(item)) &&
            suffix.All(item => item.SourceEntry.Kind != SessionEntryKind.Compaction && (!Intrinsic(item) || Omitted(item)))) cut++;
        while (cut > start) { var previous = entries[cut - 1]; if (previous.SourceEntry.Kind == SessionEntryKind.Compaction || !previous.Messages.IsEmpty) break; cut--; }
        var startsTurn = entries[cut].SourceEntry.Kind != SessionEntryKind.Compaction && entries[cut].Messages.Any(TurnMessage); var turn = -1;
        if (!startsTurn) for (var i = cut; i >= start; i--) if (entries[i].SourceEntry.Kind != SessionEntryKind.Compaction && entries[i].Messages.Any(TurnMessage)) { turn = i; break; }
        return (cut, turn, !startsTurn && turn != -1);
    }
    private static bool CutMessage(TranscriptEntry message) => message.Role is "user" or "assistant" or "bashExecution" or "custom" or "branchSummary" or "compactionSummary";
    private static bool TurnMessage(TranscriptEntry message) => message.Role is "user" or "bashExecution" or "custom" or "branchSummary" or "compactionSummary";
}
