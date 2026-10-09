using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Compaction;

public sealed record SessionCompactionSettings(bool Enabled = true, double ReserveTokens = 16_384,
    double KeepRecentTokens = 20_000)
{
    internal void Validate()
    {
        if (!double.IsFinite(ReserveTokens) || ReserveTokens < 0 || !double.IsFinite(KeepRecentTokens) || KeepRecentTokens < 0)
            throw new SessionCompactionException(SessionCompactionFailure.InvalidSettings);
    }
}
public enum SessionCompactionFailure { InvalidSettings, InvalidMessage, UnsupportedNumber, ResourceLimit, InvalidBoundary,
    NothingToSummarize, SummaryFailed, StaleSelection, Cancelled }
public sealed class SessionCompactionException(SessionCompactionFailure failure) : Exception(failure switch
{
    SessionCompactionFailure.InvalidSettings => "Summary planning settings are invalid.",
    SessionCompactionFailure.ResourceLimit => "Summary planning exceeds the configured bounds.",
    SessionCompactionFailure.NothingToSummarize => "The selected context has nothing to compact.",
    SessionCompactionFailure.StaleSelection => "Summary result no longer owns the selected session context.",
    SessionCompactionFailure.SummaryFailed => "Summary generation failed; no summary checkpoint was appended.",
    SessionCompactionFailure.Cancelled => "Compaction cancelled",
    _ => "Summary planning requires supported canonical context and a valid selected boundary."
})
{
    public SessionCompactionFailure Failure { get; } = failure;
    /// <summary>The summarization response's provider error text, when generation failed with a provider error response.</summary>
    public string? ProviderErrorMessage { get; init; }
    /// <summary>True when the summarization response was aborted rather than failed.</summary>
    public bool ProviderAborted { get; init; }
}
public sealed record SessionContextUsageEstimate(double Tokens, double UsageTokens, double TrailingTokens, int? LastUsageIndex);
public sealed record SessionFileOperations(ImmutableArray<string> Read, ImmutableArray<string> Written, ImmutableArray<string> Edited)
{
    public (ImmutableArray<string> ReadFiles, ImmutableArray<string> ModifiedFiles) ComputeFileLists()
    {
        var modified = Edited.Concat(Written).ToImmutableHashSet(StringComparer.Ordinal);
        return (Read.Where(path => !modified.Contains(path)).Order(StringComparer.Ordinal).ToImmutableArray(),
            modified.Order(StringComparer.Ordinal).ToImmutableArray());
    }
    public static string FormatFileOperations(IEnumerable<string> readFiles, IEnumerable<string> modifiedFiles)
    {
        var read = readFiles.ToArray(); var modified = modifiedFiles.ToArray(); var sections = new List<string>();
        if (read.Length != 0) sections.Add("<read-files>\n" + string.Join('\n', read) + "\n</read-files>");
        if (modified.Length != 0) sections.Add("<modified-files>\n" + string.Join('\n', modified) + "\n</modified-files>");
        return sections.Count == 0 ? "" : "\n\n" + string.Join("\n\n", sections);
    }
}
public sealed record SessionCompactionPlan(string? FirstKeptEntryId, ImmutableArray<TranscriptEntry> MessagesToSummarize,
    ImmutableArray<TranscriptEntry> TurnPrefixMessages, bool IsSplitTurn, double TokensBefore, string? PreviousSummary,
    SessionFileOperations FileOps, SessionCompactionSettings Settings, string? SelectedLeafId);
public sealed record SessionBranchSummaryCollection(ImmutableArray<SessionEntry> Entries, string? CommonAncestorId);
public sealed record SessionBranchSummaryPlan(ImmutableArray<TranscriptEntry> Messages, SessionFileOperations FileOps,
    double TotalTokens, string? CommonAncestorId = null);
/// <summary>Pi plans compaction over any branch; the entry and message bounds match the session log record bound (100,000).</summary>
public sealed record SessionCompactionPlanningOptions(int MaximumEntries = 100_000, int MaximumMessages = 100_000,
    int MaximumCharacters = 8_388_608);

internal sealed class SessionFileOperationsBuilder
{
    private readonly HashSet<string> read = new(StringComparer.Ordinal), written = new(StringComparer.Ordinal), edited = new(StringComparer.Ordinal);
    internal void Details(SessionEntry entry)
    {
        var body = entry.WireBody.Value;
        if (body.TryGetProperty("fromHook", out var hook) && hook.ValueKind == System.Text.Json.JsonValueKind.True ||
            !body.TryGetProperty("details", out var details) || details.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        AddArray("readFiles", read); AddArray("modifiedFiles", edited);
        void AddArray(string key, HashSet<string> target)
        {
            if (!details.TryGetProperty(key, out var values) || values.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            foreach (var value in values.EnumerateArray())
                if (value.ValueKind == System.Text.Json.JsonValueKind.String) target.Add(value.GetString()!);
                else throw new SessionCompactionException(SessionCompactionFailure.InvalidMessage);
        }
    }
    internal void Message(TranscriptEntry message)
    {
        var body = message.WireBody.Value;
        if (message.Role == "toolResult")
        {
            if (body.TryGetProperty("nestedCalls", out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.Object &&
                nested.TryGetProperty("calls", out var calls) && calls.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var call in calls.EnumerateArray()) Add(call);
        }
        else if (message.Role == "assistant" && body.TryGetProperty("content", out var content) && content.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == System.Text.Json.JsonValueKind.Object && block.TryGetProperty("type", out var type) && type.GetString() == "toolCall") Add(block);
    }
    private void Add(System.Text.Json.JsonElement call)
    {
        if (call.ValueKind != System.Text.Json.JsonValueKind.Object || !call.TryGetProperty("name", out var name) || name.ValueKind != System.Text.Json.JsonValueKind.String ||
            !call.TryGetProperty("arguments", out var args) || args.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !args.TryGetProperty("path", out var path) || path.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrEmpty(path.GetString())) return;
        var target = name.GetString() switch { "read" => read, "write" => written, "edit" => edited, _ => null };
        target?.Add(path.GetString()!);
    }
    internal SessionFileOperations Snapshot() => new(read.ToImmutableArray(), written.ToImmutableArray(), edited.ToImmutableArray());
}
