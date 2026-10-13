// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/live.ts (LiveDoc, endRun, compaction statuses, tool
// slot lookup and finishing, and settleSchedulerOutcome, the cleanup the scheduler runs for faulted and orphaned outcomes). The state is
// upstream's JSON: {run?: {taskId, inputs}, generation?: {attempt, message?, retry?, deferred?}, tools?: ToolSlot[], compactions?: [...]}.
// Upstream's run writers startRun and handOver live in generation.ts, as HarnessTasks.StartRunAsync and HandOver do in Generation.cs.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>The built-in live conversation state (upstream <c>live.ts</c>).</summary>
public static class Live
{
    /// <summary>Built-in task kinds that can own <c>pi.live.run</c>.</summary>
    private static readonly HashSet<string> RunTaskKinds = new(StringComparer.Ordinal) { "pi.generation" };
    private const string ToolTaskKind = "pi.tool";
    private const string CompactionTaskKind = "pi.compaction";

    /// <summary>
    /// Built-in live conversation state: run control and presentation of the current generation and tool round (upstream <c>LiveDoc</c>).
    /// A complete base is stored whenever nothing runs (spec §8.2): no generation and no running tool slot.
    /// </summary>
    public static ConversationDocToken LiveDoc { get; } = Documents.DefineConversationDoc("pi.live", 1, DocumentFork.Initial,
        () => JsonObjectValue.Empty,
        checkpointWhen: (value, _, _) => !value.ContainsKey("generation") &&
            !(value.TryGetValue("tools", out var tools) && tools is JsonArrayValue slots &&
                slots.Any(slot => slot is JsonObjectValue item && item.TryGetValue("status", out var status) && status is JsonStringValue { Value: "running" })));

    /// <summary>
    /// Ends the run owned by <paramref name="taskId"/>: settles each of its inputs and removes <c>run</c> (upstream <c>endRun</c>). Always
    /// removes <c>generation</c> and <c>tools</c>, whose presentation belongs to the ending run.
    /// </summary>
    public static void EndRun(ITx tx, DraftObject live, TaskId taskId, SubmissionSettlement settlement)
    {
        if (RunTaskId(live) == taskId.Value)
        {
            var inputs = live.GetObject("run").GetArray("inputs");
            for (var index = 0; index < inputs.Count; index++) tx.SettleSubmission(new SubmissionId(Id(inputs.Get(index))), settlement);
            live.Delete("run");
        }
        live.Delete("generation");
        live.Delete("tools");
    }

    /// <summary>Adds the status <c>{taskId, reason, blocking, attempt, retry?}</c> of a compaction task created in this commit; statuses stay
    /// in task ID order (upstream <c>addCompactionStatus</c>).</summary>
    public static void AddCompactionStatus(DraftObject live, JsonObjectValue status)
    {
        if (!live.ContainsKey("compactions")) live.Set("compactions", JsonArrayValue.Empty);
        live.GetArray("compactions").Add(status);
    }

    /// <summary>The status of compaction task <paramref name="taskId"/>, if listed (upstream <c>compactionStatus</c>).</summary>
    public static DraftObject? CompactionStatus(DraftObject live, TaskId taskId)
    {
        if (!live.ContainsKey("compactions")) return null;
        var statuses = live.GetArray("compactions");
        var index = IndexOf(statuses, "taskId", taskId.Value);
        return index < 0 ? null : statuses.GetObject(index);
    }

    /// <summary>Removes the status of compaction task <paramref name="taskId"/>, and the list once empty (upstream
    /// <c>removeCompactionStatus</c>).</summary>
    public static void RemoveCompactionStatus(DraftObject live, TaskId taskId)
    {
        if (!live.ContainsKey("compactions")) return;
        var statuses = live.GetArray("compactions");
        var index = IndexOf(statuses, "taskId", taskId.Value);
        if (index >= 0) statuses.RemoveAt(index);
        if (statuses.Count == 0) live.Delete("compactions");
    }

    /// <summary>The slot of tool task <paramref name="taskId"/> in the current round, if the round still lists it (upstream
    /// <c>toolSlot</c>).</summary>
    public static DraftObject? ToolSlot(DraftObject live, TaskId taskId)
    {
        if (!live.ContainsKey("tools") || live.Get("tools") is not JsonArrayValue) return null;
        var slots = live.GetArray("tools");
        var index = IndexOf(slots, "taskId", taskId.Value);
        return index < 0 ? null : slots.GetObject(index);
    }

    /// <summary>Marks a slot done: the result entry, if any, now carries its running output, details and diagnostics (upstream
    /// <c>finishSlot</c>).</summary>
    public static void FinishSlot(DraftObject slot, EntryId? entry)
    {
        slot.Set("status", "done");
        if (entry is { } id) slot.Set("entry", id.ToJson());
        ClearProgress(slot);
    }

    /// <summary>Removes what a tool published while running; its result entry or a rerun replaces it (upstream <c>clearProgress</c>).</summary>
    public static void ClearProgress(DraftObject slot)
    {
        foreach (var key in new[] { "output", "droppedBytes", "droppedLines", "details", "diagnostics" }) slot.Delete(key);
    }

    /// <summary>
    /// Harness cleanup for a terminal outcome the scheduler writes itself, <c>faulted</c> or <c>orphaned</c> (upstream
    /// <c>settleSchedulerOutcome</c>). A run task ends its run; a tool task's slot is marked done without an entry, and context derivation
    /// synthesizes the missing result; a compaction task's status is removed. Other kinds are ignored, so it never creates <c>pi.live</c>
    /// elsewhere. A committed generation partial becomes an aborted assistant entry, as in the generation abort handler; the scheduler's
    /// commit has no task scope, so that entry has no <c>byTaskId</c>.
    /// </summary>
    internal static async Task SettleSchedulerOutcomeAsync(ITx tx, TaskRecord record, TaskOutcome outcome)
    {
        if (record.Kind == ToolTaskKind)
        {
            if (ToolSlot(await tx.DocAsync(LiveDoc, record.ConversationId).ConfigureAwait(false), record.Id) is { } slot) FinishSlot(slot, null);
            return;
        }
        if (record.Kind == CompactionTaskKind)
        {
            RemoveCompactionStatus(await tx.DocAsync(LiveDoc, record.ConversationId).ConfigureAwait(false), record.Id);
            return;
        }
        if (!RunTaskKinds.Contains(record.Kind)) return;
        var live = await tx.DocAsync(LiveDoc, record.ConversationId).ConfigureAwait(false);
        if (RunTaskId(live) != record.Id.Value) return;
        await HarnessTasks.ConvertPartialAsync(tx, live, record.ConversationId).ConfigureAwait(false);
        EndRun(tx, live, record.Id, outcome switch
        {
            FaultedTaskOutcome faulted => SubmissionSettlement.Unanswered("faulted", faulted.Error.Message),
            OrphanedTaskOutcome orphaned => SubmissionSettlement.Unanswered(orphaned.Reason),
            _ => throw new ArgumentException($"The scheduler settles faulted and orphaned outcomes, not {outcome.Status}"),
        });
    }

    private static long? RunTaskId(DraftObject live) =>
        live.ContainsKey("run") && live.Get("run") is JsonObjectValue run && run.TryGetValue("taskId", out var id) && id is JsonNumberValue number
            ? (long)number.Value : null;

    private static int IndexOf(DraftArray items, string key, long id)
    {
        for (var index = 0; index < items.Count; index++)
            if (items.Get(index) is JsonObjectValue item && item.TryGetValue(key, out var value) && value is JsonNumberValue number && number.Value == id)
                return index;
        return -1;
    }

    private static long Id(JsonValue value) => (long)((JsonNumberValue)value).Value;
}
