using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Tree;

namespace PiSharp.Sessions.Context;

public enum SessionAccountingStatus { Available, UnsupportedUsage }
public sealed record SessionUsageContribution(SessionEntry SourceEntry, JsonData Usage);
public sealed record SessionUsageTotals(double Input, double Output, double CacheRead, double CacheWrite,
    double Total, double Cost);
public sealed record SessionHistoryStatistics(int UserMessages, int AssistantMessages, int ToolCalls,
    int ToolResults, int TotalMessages, SessionAccountingStatus Status, SessionUsageTotals? Totals,
    ImmutableArray<SessionUsageContribution> Contributions, ImmutableArray<int> UnsupportedRecordIndexes);

/// <summary>Independent immutable views over the same admitted forest. History/accounting never use
/// the compacted or edited model input as their source. DisplayMessages applies the native inspector
/// policy; it is not a renderer or a promise of upstream TUI layout.</summary>
public sealed record SessionHistoryProjection(SessionTreeSnapshot Tree, SessionContextProjection Context,
    ImmutableArray<SessionEntry> FullHistory, ImmutableArray<SessionEntry> BranchHistory,
    ImmutableArray<SessionContextContribution> HistoryContributions, ImmutableArray<TranscriptEntry> HistoryMessages,
    ImmutableArray<TranscriptEntry> DisplayMessages, ImmutableArray<TranscriptEntry> SystemHistory,
    TranscriptEntry? EffectiveSystemMessage, SessionSystemReplayResult SystemState, SessionHistoryStatistics SessionStatistics,
    SessionHistoryStatistics BranchStatistics);

/// <summary>Provider-free history, system replay and accounting. Admits the complete graph through
/// existing native validators, retains original records and observes cancellation before publication.</summary>
public sealed class SessionHistoryProjector
{
    private readonly SessionContextProjectionOptions options;
    private readonly SessionContextProjector contexts;
    private readonly SessionTreeQueries trees;

    public SessionHistoryProjector(SessionContextProjectionOptions? options = null, SessionTreeQueryOptions? treeOptions = null)
    {
        this.options = options ?? new(); contexts = new(this.options);
        trees = new(treeOptions ?? new(GraphOptions: this.options, MaximumQueryEntries: this.options.MaximumEntries));
    }

    public SessionHistoryProjection Project(ImmutableArray<SessionEntry> entries, string? selectedLeafId,
        CancellationToken cancellationToken = default)
    {
        var tree = trees.Build(entries, cancellationToken);
        var context = contexts.Project(entries, selectedLeafId, cancellationToken);
        var contributions = ImmutableArray.CreateBuilder<SessionContextContribution>(context.Ancestry.Length);
        var history = ImmutableArray.CreateBuilder<TranscriptEntry>();
        var display = ImmutableArray.CreateBuilder<TranscriptEntry>();
        var systems = ImmutableArray.CreateBuilder<TranscriptEntry>(); long characters = 0;
        foreach (var entry in context.Ancestry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Original history deliberately has no context edits or compaction selection. Each stored
            // checkpoint contributes its own summary/system messages, preserving their ordered history.
            var messages = SessionContextInfluenceProjector.ProjectEntry(entry, replacement: null, cancellationToken);
            contributions.Add(new(entry, messages));
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = message.WireBody.Value.GetRawText().Length;
                if (history.Count >= options.MaximumOutputMessages || length > options.MaximumOutputCharacters - characters)
                    throw new SessionContextProjectionException(SessionContextProjectionFailure.ResourceLimit);
                characters += length; history.Add(message);
                if (message.Role == "system") systems.Add(message);
                else if (Visible(message)) display.Add(message);
            }
        }
        var systemState = new SessionSystemReplay(options).Replay(context.Messages, cancellationToken);
        var sessionStatistics = Statistics(entries, cancellationToken);
        var branchStatistics = Statistics(context.Ancestry, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(tree, context, entries, context.Ancestry, contributions.ToImmutable(), history.ToImmutable(),
            display.ToImmutable(), systems.ToImmutable(), systemState.CurrentMessage, systemState, sessionStatistics, branchStatistics);
    }

    public SessionHistoryProjection ProjectLatest(ImmutableArray<SessionEntry> entries,
        CancellationToken cancellationToken = default) => Project(entries,
        entries.IsDefaultOrEmpty || entries[^1] is null ? null : entries[^1].Id, cancellationToken);

    private static bool Visible(TranscriptEntry message)
    {
        if (message.Role is "user" or "assistant" or "toolResult" or "bashExecution" or "branchSummary" or "compactionSummary") return true;
        if (message.Role != "custom") return false;
        var body = message.WireBody.Value;
        // Known custom_message entries have validated display booleans. A stored custom message with
        // no display flag remains visible; opaque malformed flags cannot acquire display authority.
        return !body.TryGetProperty("display", out var value) || value.ValueKind == JsonValueKind.True;
    }

    private static SessionHistoryStatistics Statistics(ImmutableArray<SessionEntry> entries, CancellationToken token)
    {
        int users = 0, assistants = 0, calls = 0, results = 0, messages = 0;
        double input = 0, output = 0, cacheRead = 0, cacheWrite = 0, cost = 0;
        var contributions = ImmutableArray.CreateBuilder<SessionUsageContribution>();
        var unsupported = ImmutableArray.CreateBuilder<int>();
        for (var index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var entry = entries[index]; var body = entry.WireBody.Value;
            JsonElement usage = default; bool hasUsage = false;
            if (entry.Kind == SessionEntryKind.Usage) hasUsage = body.TryGetProperty("usage", out usage);
            else if (entry.Kind is SessionEntryKind.Compaction or SessionEntryKind.BranchSummary)
                hasUsage = body.TryGetProperty("usage", out usage) && usage.ValueKind != JsonValueKind.Null;
            else if (entry.Kind == SessionEntryKind.Message)
            {
                messages++; var message = body.GetProperty("message"); var role = message.GetProperty("role").GetString();
                if (role == "user") users++;
                else if (role == "toolResult")
                { results++; hasUsage = message.TryGetProperty("usage", out usage) && usage.ValueKind != JsonValueKind.Null; }
                else if (role == "assistant")
                {
                    assistants++;
                    foreach (var part in message.GetProperty("content").EnumerateArray())
                    { token.ThrowIfCancellationRequested(); if (part.GetProperty("type").GetString() == "toolCall") calls++; }
                    hasUsage = message.TryGetProperty("usage", out usage);
                }
            }
            if (!hasUsage) continue;
            contributions.Add(new(entry, JsonData.FromElement(usage)));
            if (!Usage(usage, out var values) || !Finite(input + values.Input, output + values.Output,
                cacheRead + values.CacheRead, cacheWrite + values.CacheWrite, cost + values.Cost))
            { unsupported.Add(index); continue; }
            input += values.Input; output += values.Output; cacheRead += values.CacheRead;
            cacheWrite += values.CacheWrite; cost += values.Cost;
        }
        var total = input + output + cacheRead + cacheWrite;
        if (!double.IsFinite(total) && unsupported.Count == 0) unsupported.Add(entries.Length - 1);
        token.ThrowIfCancellationRequested();
        return new(users, assistants, calls, results, messages,
            unsupported.Count == 0 ? SessionAccountingStatus.Available : SessionAccountingStatus.UnsupportedUsage,
            unsupported.Count == 0 ? new(input, output, cacheRead, cacheWrite, total, cost) : null,
            contributions.ToImmutable(), unsupported.ToImmutable());
    }

    private static bool Usage(JsonElement body, out SessionUsageTotals values)
    {
        values = new(0, 0, 0, 0, 0, 0);
        if (body.ValueKind != JsonValueKind.Object || !Number(body, "input", out var input) ||
            !Number(body, "output", out var output) || !Number(body, "cacheRead", out var cacheRead) ||
            !Number(body, "cacheWrite", out var cacheWrite) || !body.TryGetProperty("cost", out var cost) ||
            cost.ValueKind != JsonValueKind.Object || !Number(cost, "total", out var totalCost)) return false;
        values = new(input, output, cacheRead, cacheWrite, input + output + cacheRead + cacheWrite, totalCost);
        return double.IsFinite(values.Total);
    }
    private static bool Number(JsonElement body, string name, out double value)
    {
        value = 0;
        return body.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetDouble(out value) && double.IsFinite(value);
    }
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
}
