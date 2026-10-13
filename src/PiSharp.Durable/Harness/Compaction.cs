// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/compaction.ts (the CompactionTask identity, and
// createCompaction, selectCut and estimateContext, which generation's threshold and overflow checks use). TODO(P11): the compaction state
// machine (phases select, summarize and retry, its abort handler, summaries and their placement) is a placeholder whose handlers fault with
// "not ported yet (P11)"; a generation that starts a compaction therefore sees it fault and continues as upstream does for a compaction
// that ended without a summary.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the compaction task (upstream <c>CompactionTask</c>, kind <c>pi.compaction</c>, version 1).</summary>
public static partial class HarnessTasks
{
    /// <summary>The task. TODO(P11): every phase and the abort handler fault until compaction is ported.</summary>
    public static DurableTask CompactionTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.compaction",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"select"}"""),
        Phases = BuiltinPlaceholders.Phases("pi.compaction", "P11", "select", "summarize", "retry"),
        Abort = BuiltinPlaceholders.Handler("pi.compaction", "P11"),
    });
}

/// <summary>Compaction helpers shared with generation (upstream <c>compaction.ts</c>).</summary>
internal static class Compaction
{
    /// <summary>
    /// Creates a compaction task with its status in this commit (upstream <c>createCompaction</c>). <paramref name="owner"/> is the
    /// generation that waits for it (a blocking compaction); without one it is conversation-owned, and background unless it is manual. The
    /// input is <c>{reason, instructions?}</c>.
    /// </summary>
    public static async Task<TaskId> CreateCompactionAsync(ITx tx, ConversationId conversationId, string reason, string? instructions = null,
        TaskId? owner = null)
    {
        var ownership = owner is { } taskId ? TaskOwnership.Task(taskId) : TaskOwnership.Conversation;
        var background = owner is null && reason != "manual";
        var input = new RecordJson.Writer().Add("reason", reason).Add("instructions", instructions).Build();
        var options = new TaskOptions(ownership) { ConversationId = conversationId, Background = background };
        var id = await tx.CreateTaskAsync(HarnessTasks.CompactionTask, input, options).ConfigureAwait(false);
        var status = new RecordJson.Writer().Add("taskId", id.ToJson()).Add("reason", reason).Add("blocking", owner is not null)
            .Number("attempt", 1).Build();
        Live.AddCompactionStatus(await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false), status);
        return id;
    }

    /// <summary>
    /// Index in <c>view.Entries</c> of the first entry a summary keeps, or null when there is nothing to compact (upstream <c>selectCut</c>,
    /// spec §8.7). Walks back from the tail until <paramref name="keepRecentTokens"/> are kept, then cuts at the first candidate at or after
    /// that entry: an entry whose contribution starts with a user or assistant message, never a tool result, and never a user entry that a
    /// result of the preceding assistant's calls still follows.
    /// </summary>
    public static int? SelectCut(ContextView view, double keepRecentTokens)
    {
        var contributions = view.Contributions;
        var start = view.Head is null ? 0 : 1;
        var candidates = new List<int>();
        for (var index = start; index < contributions.Length; index++) if (IsCandidate(contributions, index)) candidates.Add(index);
        double kept = 0;
        int? cut = null;
        for (var index = contributions.Length - 1; index >= start; index--)
        {
            foreach (var message in contributions[index]) kept += PiAiEstimate.EstimateMessageTokens(message);
            if (kept < keepRecentTokens) continue;
            var at = index;
            cut = candidates.Where(candidate => candidate >= at).Select(candidate => (int?)candidate).FirstOrDefault() ??
                (candidates.Count > 0 ? candidates[^1] : null);
            break;
        }
        if (cut is not { } found) return null;
        for (var index = start; index < found; index++) if (contributions[index].Length > 0) return found;
        return null;
    }

    private static bool IsCandidate(System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<JsonValue>> contributions, int index)
    {
        var first = contributions[index].IsEmpty ? null : contributions[index][0];
        if (Role(first) == "assistant") return true;
        if (Role(first) != "user") return false;
        // A result of the preceding assistant's calls that follows this entry, before the next assistant, belongs before it.
        var calls = new HashSet<string>(StringComparer.Ordinal);
        for (var before = index - 1; before >= 0; before--)
        {
            var assistant = contributions[before].LastOrDefault(message => Role(message) == "assistant");
            if (assistant is null) continue;
            foreach (var content in ((JsonObjectValue)assistant).TryGetValue("content", out var blocks) && blocks is JsonArrayValue list ? list : JsonArrayValue.Empty)
                if (content is JsonObjectValue call && Text(call, "type") == "toolCall" && Text(call, "id") is { } id) calls.Add(id);
            break;
        }
        if (calls.Count == 0) return true;
        for (var after = index; after < contributions.Length; after++)
        {
            for (var position = 0; position < contributions[after].Length; position++)
            {
                var message = contributions[after][position];
                if (Role(message) == "assistant" && (after > index || position > 0)) return true;
                if (Role(message) == "toolResult" && Text((JsonObjectValue)message, "toolCallId") is { } callId && calls.Contains(callId)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Size of a request over <paramref name="view"/> followed by <paramref name="extra"/> (upstream <c>estimateContext</c>, spec §8.3): the
    /// usage of the newest assistant appended after the head marker, whose request included the marker, plus estimates of the messages after
    /// it; without one, estimates of every message.
    /// </summary>
    public static double EstimateContext(ContextView view, IEnumerable<JsonValue> extra)
    {
        JsonObjectValue? measured = null;
        var after = view.Head?.Id.Value ?? long.MinValue;
        for (var index = view.Entries.Length - 1; index >= 0 && measured is null; index--)
        {
            if (view.Entries[index].Id.Value <= after) continue;
            measured = view.Contributions[index].OfType<JsonObjectValue>().LastOrDefault(message =>
                Role(message) == "assistant" && message.TryGetValue("usage", out var usage) && usage is JsonObjectValue counts &&
                PiAiEstimate.CalculateContextTokens(counts) > 0);
        }
        var from = 0;
        if (measured is not null)
            for (var index = view.Messages.Length - 1; index >= 0; index--)
                if (ReferenceEquals(view.Messages[index], measured)) { from = index + 1; break; }
        var tokens = measured is null ? 0 : PiAiEstimate.CalculateContextTokens((JsonObjectValue)measured["usage"]);
        for (var index = from; index < view.Messages.Length; index++) tokens += PiAiEstimate.EstimateMessageTokens(view.Messages[index]);
        foreach (var message in extra) tokens += PiAiEstimate.EstimateMessageTokens(message);
        return tokens;
    }

    private static string? Role(JsonValue? message) => message is JsonObjectValue value ? Text(value, "role") : null;

    private static string? Text(JsonObjectValue value, string key) => value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : null;
}
