// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/live.ts (LiveDoc, endRun, compaction statuses and
// failures, tool slot lookup, finishing and nested slot removal, and settleSchedulerOutcome, the cleanup the scheduler runs for faulted and
// orphaned outcomes). The state is upstream's JSON: {run?: {taskId, inputs}, generation?: {attempt, message?, retry?, deferred?},
// tools?: ToolSlot[], nestedTools?: NestedToolSlot[], compactions?: [...], compactionFailure?: {taskId, reason, error}, runFailure?:
// {taskId, inputs, reason, detail?}}. A ToolSlot is
// {callId, name, taskId?, status, output?, droppedBytes?, droppedLines?, details?, diagnostics?, entry?}; a NestedToolSlot is {callId,
// parentCallId, parentTaskId, name, taskId, arguments, status, output?, ..., summary?: {isError, durationMs?, usage?, error?}}.
// Upstream's run writers startRun and handOver live in generation.ts, as HarnessTasks.StartRunAsync and HandOver do in Generation.cs.
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): ProgressCommit (upstream PROGRESS_COMMIT), the deferred commit of streamed progress.
// Pi v1.5.0 sync (area run-failure, f3255de76): pi.live.runFailure (RunFailure, RunFailureReason), which EndRun sets for a run that ends
// without an answer (RunEnd) and the next HarnessTasks.StartRunAsync removes unless it keeps it; the scheduler's orphaned outcome ends its
// run as orphaned with the orphan reason as detail, its inputs still settled with that reason. pi.live stays at version 1, as at
// f3255de76: runFailure is an optional field. RunFailureReason declares every v1.5.0 value: this area produces model_error, no_model,
// aborted, reset, faulted and orphaned, and areas restarts (interrupted, restart_limit), admission (nothing_to_answer) and live-turns
// (turn_limit) the others. RunFailure.first (7e15f9601) is area run-end's. The citation stays on v1.2.0 until the file's other areas land.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

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

    /// <summary>
    /// How the progress throttles commit a partial answer and running tool output into <c>pi.live</c> (upstream <c>PROGRESS_COMMIT</c>):
    /// deferred, so Storage gets them together, at the next commit that reads or writes <c>pi.live</c> or after
    /// <see cref="ProgressPolicy.PersistWithinMs"/>. Every commit that consumes or clears streamed fields reads <c>pi.live</c>, so it stores
    /// them with its own changes (spec §8.2).
    /// </summary>
    internal static CommitOptions ProgressCommit { get; } = new(CommitPersistence.Deferred());

    private static bool AnyRunning(JsonObjectValue live, string key) =>
        live.TryGetValue(key, out var listed) && listed is JsonArrayValue slots &&
        slots.Any(slot => slot is JsonObjectValue item && item.TryGetValue("status", out var status) && status is JsonStringValue { Value: "running" });

    /// <summary>
    /// Ends the run owned by <paramref name="taskId"/>: settles each of its inputs, removes <c>run</c> and, without an answer, records
    /// <c>runFailure</c> (upstream <c>endRun</c>). Always removes <c>generation</c>, <c>tools</c> and <c>nestedTools</c>, whose presentation
    /// belongs to the ending run.
    /// </summary>
    public static void EndRun(ITx tx, DraftObject live, TaskId taskId, RunEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        if (RunTaskId(live) == taskId.Value)
        {
            var inputs = ((JsonArrayValue)live.GetObject("run").GetArray("inputs").ToValue()).Select(id => new SubmissionId(Id(id))).ToImmutableArray();
            if (end.Answer is { } answer)
            {
                foreach (var id in inputs) tx.SettleSubmission(id, SubmissionSettlement.Done(answer));
            }
            else
            {
                var reason = end.Reason!.Value;
                var settlement = end.Settlement ?? SubmissionSettlement.Unanswered(RunFailure.ReasonName(reason),
                    end.Detail is { } detail ? JsonValue.String(detail) : null);
                foreach (var id in inputs) tx.SettleSubmission(id, settlement);
                live.Set("runFailure", new RunFailure(taskId, inputs, reason, end.Detail).ToJson());
            }
            live.Delete("run");
        }
        live.Delete("generation");
        live.Delete("tools");
        live.Delete("nestedTools");
    }

    /// <summary><c>runFailure</c> of a <c>pi.live</c> value: why its conversation's newest run ended without an answer; null when no run ended
    /// so, or a later one started.</summary>
    public static RunFailure? RunFailureOf(JsonObjectValue? live) =>
        live is not null && live.TryGetValue("runFailure", out var failure) ? RunFailure.FromJson(failure) : null;

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
            FaultedTaskOutcome faulted => RunEnd.Unanswered(RunFailureReason.Faulted, faulted.Error.Message),
            // The run fails as orphaned, with why as its detail; its inputs keep the orphan reason as their settlement.
            OrphanedTaskOutcome orphaned => RunEnd.Unanswered(RunFailureReason.Orphaned, orphaned.Reason, SubmissionSettlement.Unanswered(orphaned.Reason)),
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

/// <summary>
/// Why a run ended without an answer (upstream <c>RunFailureReason</c>; <see cref="RunFailure.ReasonName"/> is the persisted spelling).
/// Every Pi v1.5.0 value is declared; the Harness produces <see cref="Interrupted"/>, <see cref="RestartLimit"/>,
/// <see cref="NothingToAnswer"/> and <see cref="TurnLimit"/> once the Pi v1.5.0 sync areas that port them land.
/// </summary>
public enum RunFailureReason
{
    /// <summary><c>model_error</c>: the model's response failed, after any retries, or the compaction a request needed failed.</summary>
    ModelError,
    /// <summary><c>no_model</c>: no model is configured, or the configured one is not available.</summary>
    NoModel,
    /// <summary><c>aborted</c>: the run was aborted (<c>IConversation.AbortAsync</c>, <c>IHarness.AbortTaskAsync</c>).</summary>
    Aborted,
    /// <summary><c>reset</c>: a queued reset started a new context at the post-tools boundary, before an answer (a tool's handoff continues
    /// the run).</summary>
    Reset,
    /// <summary><c>faulted</c>: the run's task threw where it cannot recover, or storage rejected its commit.</summary>
    Faulted,
    /// <summary><c>orphaned</c>: the run's task was aborted while no registered definition could take it.</summary>
    Orphaned,
    /// <summary><c>interrupted</c>: the host stopped while the model was answering, more often than <c>settings.retry</c> retries.</summary>
    Interrupted,
    /// <summary><c>restart_limit</c>: the run's generation was cut off <c>settings.restartLimit</c> times in a row.</summary>
    RestartLimit,
    /// <summary><c>nothing_to_answer</c>: the context did not end in a user message or tool results (empty, or ending in an answer), so no
    /// request was sent.</summary>
    NothingToAnswer,
    /// <summary><c>turn_limit</c>: a tool round reached the conversation's <c>maxTurns</c>; a continue resumes on its results.</summary>
    TurnLimit,
}

/// <summary>
/// The newest run that ended without an answer (upstream <c>RunFailure</c>, spec §8.2): <c>pi.live.runFailure</c>, <c>{taskId, inputs,
/// reason, detail?}</c>. Set in the commit that ends the run; removed when a later commit starts a run. A run started in the same commit,
/// by <c>afterFailedRun: continue</c> or after a reset, keeps it.
/// </summary>
/// <param name="TaskId">The task that ended the run: the run's task at its end.</param>
/// <param name="Inputs">The run's inputs, settled <c>unanswered</c>.</param>
/// <param name="Reason">Why the run ended without an answer.</param>
/// <param name="Detail"><c>model_error</c>: the error text; <c>no_model</c>: which model is missing; <c>faulted</c>: the error message;
/// <c>orphaned</c>: why the task could not run (<c>missing_task</c>, <c>task_too_old</c>, <c>migration_failed</c>).</param>
public sealed record RunFailure(TaskId TaskId, ImmutableArray<SubmissionId> Inputs, RunFailureReason Reason, string? Detail = null)
{
    /// <summary>The persisted spelling of <paramref name="reason"/> (<c>model_error</c>, <c>no_model</c>, ...).</summary>
    public static string ReasonName(RunFailureReason reason) => reason switch
    {
        RunFailureReason.ModelError => "model_error",
        RunFailureReason.NoModel => "no_model",
        RunFailureReason.Aborted => "aborted",
        RunFailureReason.Reset => "reset",
        RunFailureReason.Faulted => "faulted",
        RunFailureReason.Orphaned => "orphaned",
        RunFailureReason.Interrupted => "interrupted",
        RunFailureReason.RestartLimit => "restart_limit",
        RunFailureReason.NothingToAnswer => "nothing_to_answer",
        RunFailureReason.TurnLimit => "turn_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Invalid RunFailureReason"),
    };

    /// <summary>The reason persisted as <paramref name="text"/>.</summary>
    public static bool TryParseReason(string text, out RunFailureReason reason)
    {
        foreach (var candidate in Enum.GetValues<RunFailureReason>())
            if (string.Equals(ReasonName(candidate), text, StringComparison.Ordinal)) { reason = candidate; return true; }
        reason = default;
        return false;
    }

    /// <summary>Upstream's JSON, in its key order: <c>{taskId, inputs, reason, detail?}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer()
        .Add("taskId", TaskId.ToJson())
        .Add("inputs", JsonValue.Array(Inputs.Select(id => (JsonValue)id.ToJson())))
        .Add("reason", ReasonName(Reason))
        .Add("detail", Detail)
        .Build();

    /// <summary>Reads <see cref="ToJson"/>; fields it does not know (a later version's) are ignored.</summary>
    public static RunFailure FromJson(JsonValue json)
    {
        const string what = "pi.live.runFailure";
        var value = RecordJson.Object(json, what);
        var text = RecordJson.String(value, "reason", what);
        if (!TryParseReason(text, out var reason)) throw RecordJson.Invalid(what, $"unknown reason \"{text}\"");
        var inputs = RecordJson.Array(RecordJson.Required(value, "inputs", what), "inputs", what);
        return new RunFailure(RecordJson.Id<TaskId>(value, "taskId", what),
            [.. inputs.Select(id => SubmissionId.FromNumber(RecordJson.Integer(id, "inputs", what)))], reason,
            RecordJson.OptionalString(value, "detail", what));
    }

    /// <inheritdoc/>
    public bool Equals(RunFailure? other) => other is not null && JsonValue.DeepEquals(ToJson(), other.ToJson());

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(TaskId, Reason, Detail);
}

/// <summary>
/// How a run ends (upstream <c>RunEnd</c>): with an answer (<see cref="Done"/>), settling its inputs <c>done</c>, or without one
/// (<see cref="Unanswered"/>), settling them <c>unanswered</c> and recording why in <c>pi.live.runFailure</c>.
/// </summary>
public sealed class RunEnd
{
    private RunEnd(EntryId? answer, RunFailureReason? reason, string? detail, SubmissionSettlement? settlement)
    {
        Answer = answer; Reason = reason; Detail = detail; Settlement = settlement;
    }

    /// <summary>The run answered with <paramref name="answer"/>.</summary>
    public static RunEnd Done(EntryId answer) => new(answer, null, null, null);

    /// <summary>The run ended without an answer, for <paramref name="reason"/>, with <paramref name="detail"/>. <paramref name="settlement"/>
    /// is its inputs' settlement; default <c>unanswered</c> with the reason and the detail.</summary>
    public static RunEnd Unanswered(RunFailureReason reason, string? detail = null, SubmissionSettlement? settlement = null)
    {
        if (settlement is not null && settlement.Status != SubmissionStatus.Unanswered)
            throw new ArgumentException("A run without an answer settles its inputs unanswered", nameof(settlement));
        return new(null, reason, detail, settlement);
    }

    /// <summary>The answer of a run that answered.</summary>
    public EntryId? Answer { get; }
    /// <summary>Why a run without an answer ended.</summary>
    public RunFailureReason? Reason { get; }
    /// <summary>The failure's detail, if any.</summary>
    public string? Detail { get; }
    /// <summary>The inputs' settlement of a run without an answer, when it is not the default.</summary>
    public SubmissionSettlement? Settlement { get; }
}
