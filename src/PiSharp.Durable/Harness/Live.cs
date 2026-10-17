// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/live.ts (LiveDoc, endRun, compaction statuses and
// failures, tool slot lookup, finishing and nested slot removal, and settleSchedulerOutcome, the cleanup the scheduler runs for faulted and
// orphaned outcomes). The state is upstream's JSON: {run?: {taskId, inputs}, generation?: {attempt, message?, retry?, deferred?},
// tools?: ToolSlot[], nestedTools?: NestedToolSlot[], compactions?: [...], compactionFailure?: {taskId, reason, error}}. A ToolSlot is
// {callId, name, taskId?, status, output?, droppedBytes?, droppedLines?, details?, diagnostics?, entry?}; a NestedToolSlot is {callId,
// parentCallId, parentTaskId, name, taskId, arguments, status, output?, ..., summary?: {isError, durationMs?, usage?, error?}}.
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
        checkpointWhen: (value, _, _) => !value.ContainsKey("generation") && !AnyRunning(value, "tools") && !AnyRunning(value, "nestedTools"));

    private static bool AnyRunning(JsonObjectValue live, string key) =>
        live.TryGetValue(key, out var listed) && listed is JsonArrayValue slots &&
        slots.Any(slot => slot is JsonObjectValue item && item.TryGetValue("status", out var status) && status is JsonStringValue { Value: "running" });

    /// <summary>
    /// Ends the run owned by <paramref name="taskId"/>: settles each of its inputs and removes <c>run</c> (upstream <c>endRun</c>). Always
    /// removes <c>generation</c>, <c>tools</c> and <c>nestedTools</c>, whose presentation belongs to the ending run.
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
        live.Delete("nestedTools");
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

    /// <summary>
    /// Removes the status of compaction task <paramref name="taskId"/> and records that it failed with <paramref name="error"/> as
    /// <c>pi.live.compactionFailure</c> <c>{taskId, reason, error}</c>, the newest failure since a compaction last produced a summary (upstream
    /// <c>failCompactionStatus</c>).
    /// </summary>
    public static void FailCompactionStatus(DraftObject live, TaskId taskId, string error)
    {
        if (CompactionStatus(live, taskId) is { } status)
            live.Set("compactionFailure", JsonValue.Object([
                KeyValuePair.Create("taskId", status.Get("taskId")!),
                KeyValuePair.Create("reason", status.Get("reason")!),
                KeyValuePair.Create("error", (JsonValue)error),
            ]));
        RemoveCompactionStatus(live, taskId);
    }

    /// <summary>
    /// The slot of tool task <paramref name="taskId"/>, model-issued or nested, if <c>pi.live</c> still lists it (upstream <c>toolSlot</c>).
    /// Nested slots are found by binary search: a tool may make thousands of nested calls, each of which looks up its slot in several commits.
    /// Their task IDs ascend: each slot is appended in the commit that creates its task, task IDs ascend in commit order, and removal keeps
    /// the order. A nested slot is told apart by its <c>parentTaskId</c> (<see cref="IsNested"/>).
    /// </summary>
    public static DraftObject? ToolSlot(DraftObject live, TaskId taskId)
    {
        if (live.ContainsKey("tools") && live.Get("tools") is JsonArrayValue)
        {
            var slots = live.GetArray("tools");
            var index = IndexOf(slots, "taskId", taskId.Value);
            if (index >= 0) return slots.GetObject(index);
        }
        if (!live.ContainsKey("nestedTools") || live.Get("nestedTools") is not JsonArrayValue) return null;
        var nested = live.GetArray("nestedTools");
        var low = 0;
        var high = nested.Count - 1;
        while (low <= high)
        {
            var middle = (int)((uint)(low + high) >> 1);
            var candidate = Id(((JsonObjectValue)nested.Get(middle))["taskId"]);
            if (candidate == taskId.Value) return nested.GetObject(middle);
            if (candidate < taskId.Value) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    /// <summary>Whether <paramref name="slot"/> is a nested call's (upstream <c>"parentTaskId" in slot</c>).</summary>
    public static bool IsNested(DraftObject slot) => slot.ContainsKey("parentTaskId");

    /// <summary>Marks a slot done and clears its running output (upstream <c>finishSlot</c>); the caller sets a model-issued call's result
    /// <c>entry</c>, or a nested call's <c>summary</c>. Neither is set when the task faulted or was orphaned.</summary>
    public static void FinishSlot(DraftObject slot)
    {
        slot.Set("status", "done");
        ClearProgress(slot);
    }

    /// <summary>Removes the nested slots below tool task <paramref name="taskId"/>, transitively, and the list once empty (upstream
    /// <c>removeNestedSlots</c>); the list holds parents before their children.</summary>
    public static void RemoveNestedSlots(DraftObject live, TaskId taskId)
    {
        if (!live.ContainsKey("nestedTools") || live.Get("nestedTools") is not JsonArrayValue) return;
        var nested = live.GetArray("nestedTools");
        var removed = new HashSet<long> { taskId.Value };
        var values = (JsonArrayValue)nested.ToValue();
        foreach (var slot in values.Cast<JsonObjectValue>())
            if (removed.Contains(Id(slot["parentTaskId"]))) removed.Add(Id(slot["taskId"]));
        for (var index = values.Count - 1; index >= 0; index--)
            if (removed.Contains(Id(((JsonObjectValue)values[index])["parentTaskId"]))) nested.RemoveAt(index);
        if (nested.Count == 0) live.Delete("nestedTools");
    }

    /// <summary>Removes what a tool published while running; its result entry or a rerun replaces it (upstream <c>clearProgress</c>).</summary>
    public static void ClearProgress(DraftObject slot)
    {
        foreach (var key in new[] { "output", "droppedBytes", "droppedLines", "details", "diagnostics" }) slot.Delete(key);
    }

    /// <summary>
    /// Harness cleanup for a terminal outcome the scheduler writes itself, <c>faulted</c> or <c>orphaned</c> (upstream
    /// <c>settleSchedulerOutcome</c>). A run task ends its run; a tool task's slot is marked done without an entry, and context derivation
    /// synthesizes the missing result, and the nested slots below it are removed; a compaction task's status is removed and its failure
    /// recorded. Other kinds are ignored, so it never
    /// creates <c>pi.live</c>
    /// elsewhere. A committed generation partial becomes an aborted assistant entry, as in the generation abort handler; the scheduler's
    /// commit has no task scope, so that entry has no <c>byTaskId</c>.
    /// </summary>
    internal static async Task SettleSchedulerOutcomeAsync(ITx tx, TaskRecord record, TaskOutcome outcome)
    {
        if (record.Kind == ToolTaskKind)
        {
            var slots = await tx.DocAsync(LiveDoc, record.ConversationId).ConfigureAwait(false);
            if (ToolSlot(slots, record.Id) is { } slot) FinishSlot(slot);
            RemoveNestedSlots(slots, record.Id);
            return;
        }
        if (record.Kind == CompactionTaskKind)
        {
            var error = outcome switch
            {
                FaultedTaskOutcome faulted => faulted.Error.Message,
                OrphanedTaskOutcome orphaned => orphaned.Reason,
                _ => throw new ArgumentException($"The scheduler settles faulted and orphaned outcomes, not {outcome.Status}"),
            };
            FailCompactionStatus(await tx.DocAsync(LiveDoc, record.ConversationId).ConfigureAwait(false), record.Id, error);
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
