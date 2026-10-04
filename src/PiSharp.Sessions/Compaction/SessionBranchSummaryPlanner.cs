using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Compaction;

/// <summary>Raw abandoned-branch selection. Source branch summaries deliberately do not apply context edits.</summary>
public sealed class SessionBranchSummaryPlanner
{
    private readonly SessionCompactionPlanningOptions options;
    public SessionBranchSummaryPlanner(SessionCompactionPlanningOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.MaximumEntries <= 0 || this.options.MaximumMessages <= 0 || this.options.MaximumCharacters <= 0)
            throw new SessionCompactionException(SessionCompactionFailure.InvalidSettings);
    }
    public SessionBranchSummaryCollection Collect(ImmutableArray<SessionEntry> entries, string? oldLeafId,
        string targetId, CancellationToken cancellationToken = default)
    {
        CheckEntries(entries); ArgumentException.ThrowIfNullOrEmpty(targetId);
        if (oldLeafId is null) return new([], null);
        var byId = entries.ToDictionary(entry => entry.Id!, StringComparer.Ordinal);
        var old = Branch(oldLeafId); var target = Branch(targetId);
        var oldIds = old.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var common = target.Reverse().FirstOrDefault(entry => oldIds.Contains(entry.Id))?.Id;
        return new(old.SkipWhile(entry => common is not null && entry.Id != common).Skip(common is null ? 0 : 1).ToImmutableArray(), common);

        ImmutableArray<SessionEntry> Branch(string leaf)
        {
            var branch = new List<SessionEntry>(); var visited = new HashSet<string>(StringComparer.Ordinal); string? current = leaf;
            while (current is not null && byId.TryGetValue(current, out var entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(current)) throw new SessionCompactionException(SessionCompactionFailure.InvalidBoundary);
                branch.Add(entry); current = entry.ParentId;
            }
            branch.Reverse(); return branch.ToImmutableArray();
        }
    }
    public SessionBranchSummaryPlan Prepare(ImmutableArray<SessionEntry> entries, double tokenBudget = 0,
        string? commonAncestorId = null, CancellationToken cancellationToken = default)
    {
        CheckEntries(entries);
        if (!double.IsFinite(tokenBudget)) throw new SessionCompactionException(SessionCompactionFailure.InvalidSettings);
        var fileOps = new SessionFileOperationsBuilder();
        foreach (var entry in entries) { cancellationToken.ThrowIfCancellationRequested(); if (entry.Kind == SessionEntryKind.BranchSummary) fileOps.Details(entry); }
        var messages = new List<TranscriptEntry>(); double tokens = 0; long characters = 0;
        for (var i = entries.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested(); var entry = entries[i];
            // Raw system message entries count in source branch preparation. A stored compaction's
            // system checkpoint is distinct from that entry's summary conversation message.
            var message = entry.Kind == SessionEntryKind.BranchSummary
                ? new TranscriptEntry("branchSummary", JsonData.Parse(JsonSerializer.Serialize(new
                    { role = "branchSummary", summary = entry.WireBody.Value.GetProperty("summary").GetString(),
                        fromId = entry.WireBody.Value.GetProperty("fromId").GetString(),
                        timestamp = DateTimeOffset.Parse(entry.Timestamp, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() })))
                : SessionContextInfluenceProjector.ProjectEntry(entry, null, cancellationToken)
                    .FirstOrDefault(message => message.Role != "toolResult" &&
                        (entry.Kind != SessionEntryKind.Compaction || message.Role == "compactionSummary"));
            if (message is null) continue;
            fileOps.Message(message); var estimate = SessionCompactionTokenEstimator.EstimateTokens(message);
            if (tokenBudget > 0 && tokens + estimate > tokenBudget)
            {
                if (entry.Kind is SessionEntryKind.Compaction or SessionEntryKind.BranchSummary && tokens < tokenBudget * 0.9) Add(message, estimate);
                break;
            }
            Add(message, estimate);
        }
        return new(messages.ToImmutableArray(), fileOps.Snapshot(), tokens, commonAncestorId);

        void Add(TranscriptEntry message, double estimate)
        {
            characters += message.WireBody.ToString().Length;
            if (characters > options.MaximumCharacters || messages.Count >= options.MaximumMessages)
                throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
            messages.Insert(0, message); tokens += estimate;
        }
    }
    private void CheckEntries(ImmutableArray<SessionEntry> entries)
    {
        if (entries.IsDefault || entries.Any(entry => entry.Id is null || entry.IsHeader))
            throw new SessionCompactionException(SessionCompactionFailure.InvalidMessage);
        if (entries.Length > options.MaximumEntries) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
    }
}
