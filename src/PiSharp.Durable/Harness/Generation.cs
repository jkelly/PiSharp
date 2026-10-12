// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/generation.ts (the GenerationTask identity and
// convertPartial with appendAssistant, which the scheduler's fault cleanup needs). TODO(P8): the generation state machine (phases prepare,
// request, retry, poll and tools, its abort handler, streaming, classification, runs and hand-overs) is a placeholder whose handlers fault
// with "not ported yet (P8)".
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the generation task (upstream <c>GenerationTask</c>, kind <c>pi.generation</c>, version 1).</summary>
public static partial class HarnessTasks
{
    /// <summary>The task. TODO(P8): every phase and the abort handler fault until the generation task is ported.</summary>
    public static DurableTask GenerationTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.generation",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"prepare","attempt":1}"""),
        Phases = BuiltinPlaceholders.Phases("pi.generation", "P8", "prepare", "request", "retry", "poll", "tools"),
        Abort = BuiltinPlaceholders.Handler("pi.generation", "P8"),
    });

    /// <summary>
    /// Appends a committed partial left by an interrupted, aborted, faulted or orphaned attempt as an aborted assistant entry; the caller
    /// replaces or removes <c>generation</c> (upstream <c>convertPartial</c>).
    /// </summary>
    internal static async Task ConvertPartialAsync(ITx tx, DraftObject live, ConversationId conversationId)
    {
        if (!live.ContainsKey("generation") || live.Get("generation") is not JsonObjectValue generation ||
            !generation.TryGetValue("message", out var partial) || partial is not JsonObjectValue message) return;
        await AppendAssistantAsync(tx, conversationId, message.SetItem("stopReason", "aborted")).ConfigureAwait(false);
    }

    /// <summary>Records the message's usage under <c>provider/model</c> and appends it as a <c>pi.assistant</c> entry (upstream
    /// <c>appendAssistant</c>).</summary>
    internal static async Task<EntryRecord> AppendAssistantAsync(ITx tx, ConversationId conversationId, JsonObjectValue message)
    {
        var key = $"{Text(message, "provider")}/{Text(message, "model")}";
        await UsageLedger.RecordUsageAsync(tx, conversationId, "models", key, (JsonObjectValue)message["usage"]).ConfigureAwait(false);
        return await tx.AppendEntryAsync(Entries.AssistantEntry, conversationId, new TypedEntryDraft { Model = JsonValue.Array(message) })
            .ConfigureAwait(false);
    }

    private static string Text(JsonObjectValue value, string key) =>
        value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : field?.ToJson() ?? "undefined";
}

/// <summary>Handlers of built-in tasks whose state machines later phases port.</summary>
internal static class BuiltinPlaceholders
{
    /// <summary>A phase map whose handlers all fault.</summary>
    public static IReadOnlyDictionary<string, TaskPhaseHandler> Phases(string kind, string phase, params string[] names) =>
        names.ToDictionary(name => name, _ => Handler(kind, phase), StringComparer.Ordinal);

    /// <summary>A handler that faults with "not ported yet".</summary>
    public static TaskPhaseHandler Handler(string kind, string phase) =>
        (_, _, _) => System.Threading.Tasks.Task.FromException(new NotImplementedException($"Task {kind} is not ported yet ({phase})"));
}
