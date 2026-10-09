// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (compact, _runAutoCompaction:
// session_before_compact with the preparation; a cancel result aborts, a compaction result replaces the default summary) and
// core/compaction/compaction.ts (CompactionPreparation, CompactionResult).
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent;

/// <summary>Source SessionBeforeCompactEvent without its signal (the handler's cancellation token stands for it).
/// <see cref="Preparation"/> is the CompactionPreparation object; its file-operation sets are arrays.</summary>
public sealed record SessionBeforeCompactProposal(JsonData Preparation, ImmutableArray<SessionEntry> BranchEntries,
    string? CustomInstructions, SessionCompactionReason Reason, bool WillRetry)
{
    /// <summary>The Pi event object <c>{type, preparation, branchEntries, customInstructions?, reason, willRetry}</c>.</summary>
    public JsonData ToJson()
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); writer.WriteString("type", "session_before_compact");
            writer.WritePropertyName("preparation"); writer.WriteRawValue(Preparation.Value.GetRawText());
            writer.WritePropertyName("branchEntries"); writer.WriteStartArray();
            foreach (var entry in BranchEntries) writer.WriteRawValue(entry.WireBody.Value.GetRawText());
            writer.WriteEndArray();
            if (CustomInstructions is not null) writer.WriteString("customInstructions", CustomInstructions);
            writer.WriteString("reason", SessionSummarizationRetryAttemptStarted.ReasonText(Reason)); writer.WriteBoolean("willRetry", WillRetry);
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
/// <summary>Source CompactionResult supplied by an extension: used as the compaction checkpoint instead of the default summary.</summary>
public sealed record SessionExtensionCompaction(string Summary, string FirstKeptEntryId, double TokensBefore,
    TokenUsage? Usage = null, JsonData? Details = null);
/// <summary>Source SessionBeforeCompactResult: <see cref="Cancel"/> aborts the compaction; otherwise <see cref="Compaction"/>
/// (when present) replaces the default summary.</summary>
public sealed record SessionBeforeCompactDecision(bool Cancel = false, SessionExtensionCompaction? Compaction = null);
public delegate ValueTask<SessionBeforeCompactDecision?> SessionBeforeCompactHandler(SessionBeforeCompactProposal proposal, CancellationToken cancellationToken);

public sealed partial class PersistentAgentSession
{
    private SessionBeforeCompactHandler? _beforeCompaction;

    /// <summary>Trusted host binding of the session_before_compact handlers. Each compaction captures the handler once,
    /// when it starts, so a later binding never changes a compaction in progress.</summary>
    public void ConfigureBeforeCompaction(SessionBeforeCompactHandler? handler)
    {
        lock (_gate) { ThrowAvailable(); _beforeCompaction = handler; }
    }

    private static JsonData PreparationJson(SessionCompactionPlan plan, string? firstKept)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); writer.WriteString("firstKeptEntryId", firstKept);
            writer.WritePropertyName("messagesToSummarize"); writer.WriteStartArray();
            foreach (var message in plan.MessagesToSummarize) writer.WriteRawValue(message.WireBody.Value.GetRawText());
            writer.WriteEndArray();
            writer.WritePropertyName("turnPrefixMessages"); writer.WriteStartArray();
            foreach (var message in plan.TurnPrefixMessages) writer.WriteRawValue(message.WireBody.Value.GetRawText());
            writer.WriteEndArray();
            writer.WriteBoolean("isSplitTurn", plan.IsSplitTurn); writer.WriteNumber("tokensBefore", plan.TokensBefore);
            if (plan.PreviousSummary is not null) writer.WriteString("previousSummary", plan.PreviousSummary);
            writer.WritePropertyName("fileOps"); writer.WriteStartObject();
            foreach (var (name, values) in new[] { ("read", plan.FileOps.Read), ("written", plan.FileOps.Written), ("edited", plan.FileOps.Edited) })
            { writer.WritePropertyName(name); writer.WriteStartArray(); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
            writer.WriteEndObject();
            writer.WritePropertyName("settings"); writer.WriteStartObject(); writer.WriteBoolean("enabled", plan.Settings.Enabled);
            writer.WriteNumber("reserveTokens", plan.Settings.ReserveTokens); writer.WriteNumber("keepRecentTokens", plan.Settings.KeepRecentTokens);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
