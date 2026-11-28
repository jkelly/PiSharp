// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/durable/src/harness/tool.ts.
//
// Mappings:
// - Input, checkpoints and results are upstream's JSON in its key order. Input (version 3) {kind: "model", assistant, callId, unoffered?:
//   true} or {kind: "nested", parent, parentCallId, key, call, progress?}; checkpoints {phase: "call"} and {phase: "execute", arguments,
//   replay}; result {kind: "model", entryId, control?} (control {addTools?, terminate?, handoff?}) or {kind: "nested"}. Migrate turns
//   version 1 input {assistant, callId} into {kind: "model", assistant, callId} and keeps version 2 input, which has no unoffered; a version
//   1 result has no kind and is a model-issued call's.
// - NestedCallDoc (pi.tool.nested-call, a task-scoped family of the caller) holds {taskId?, result?}, the result being
//   NestedToolExecutionResult.ToJson.
// - Calls are pi-ai ToolCall wire JSON; a nested call's hook call adds `parent: {taskId, callId}`. Arguments are validated by
//   Internal/PiAi/Validation.cs, the port of pi-ai's validateToolArguments (packages/ai/src/utils/validation.ts) on pi-schema
//   (PiSharp.Contracts.Schema), with the call's argumentsError. Pi v1.5.0 sync (area tools-schema): structured output is checked with
//   pi-schema too, compiled with the standard formats and unresolved references rejected in place (Check, and the first of Errors for the
//   message, its JSON Pointer path dotted).
// - `runtime.signal.aborted` is ITaskRuntime.Signal; an abort reaches a running tool as its context's cancellation, so the tool rejects
//   with an OperationCanceledException (upstream's AbortError) or the reason.
// - The tool reports from any thread; what it reported (output buffer, diagnostics, details, the ended flag) sits behind one lock, and the
//   progress commits run on the Harness clock and runtime.SetTimer timers (output.ts, upstream progressClock). A nested call is admitted under that lock, so none starts after
//   the call ended, as upstream's single thread guarantees.
// - Upstream chains each nested call's admission on the attempt's madeNestedCalls memo, so admissions commit in call order, back to back
//   once the memo committed. NestedCalls queues them behind the memo and enqueues their commits in call order under one lock, so the
//   commits (and the task and document IDs they allocate) are upstream's on any thread.
// - Pi v1.5.0 sync (area telemetry, 2ec55d808): reading the call sets the phase span's tool.name and tool.call_id, and a nested call
//   answered from its stored result adds pi.durable.replay with the tool name; neither records arguments or output.
// - performance.now() is a Stopwatch; Math.round is round half up.
// - Pi v1.5.0 (7ec1f648e, area harness-options): api.models and api.env are getters, models read from the runtime at each access and env
//   built for each call, so a rerun after recovery gets the conversation's environment at that time. ToolExecutionApi.Models reads
//   runtime.Models at each access, and the call builds its environment before the API, which then holds it. 4a5d37291 moved OutputBuffer,
//   Progress and boundOutput to @earendil-works/chord/output; they are Output.cs here, unchanged.
// - A ToolExecutionResult field left null is upstream's undefined: Output null takes the retained output, Details null the last running
//   details; JsonValue.Null is an explicit null. A C# JsonValue cannot hold undefined, so upstream's copyJson(..., {omitUndefinedProperties})
//   of arguments, diagnostics and details has nothing to drop.
// - Upstream's copyJson of a control drops undefined keys; a C# ToolControl has none, and an unset (null) Terminate is omitted. Pi v1.5.0
//   sync (area harness-options): Terminate false is stored as given, as upstream's `terminate?: boolean` is (#10858).
// - Pi v1.5.0 sync (area reports): a model-issued call's broken structured output is reported as the extension_error of the extension
//   that provides or wraps the tool (toolExtension; a30c2a81d); a failed agent resolution leaves the name out.
// - Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): progress commits are deferred (Live.ProgressCommit), and api.CommitAsync takes
//   CommitOptions; the intent's slot status and a nested slot's arguments (assigned whole) are a deferred commit of their own beside the
//   state-only intent commit, and a safe rerun shows the slot running with its arguments again (e5c6182ba, durable@1.5.0 Changed#5).
//   PiSharp-only order: the intent commits first and the slot commit follows (upstream's slot commit comes first), so the agent event
//   projection (PiSharp.Durable.Hosting's AgentEvents.cs; upstream removed watchEvents) has the arguments a tool_execution_start carries when the slot turns
//   running. Storage gets the same either way: the intent at once, the held slot change with the next commit that reads or writes pi.live.
// - Pi v1.5.0 sync (area restarts, b3a29f111): a call cut off settings.restartLimit times in a row (TaskRecord.Restarts) ends with an
//   error result built from its slot, diagnostic code restart_limit, instead of running again (EndCutOffAsync): checked in the call phase
//   once the call is read, and before a safe rerun. The task fails with detail {reason: "restart_limit"}, which the scheduler reports as
//   task_interrupted.
// - Pi v1.5.0 sync (area tool-tasks, 87eff524f, #11043): afterTool runs on every result the task writes, also the error results the Harness
//   writes itself without executing the tool: tool_unavailable, invalid_arguments and blocked from the call phase (RejectAsync, the hooks
//   seeing the call as the model made it), and interrupted, restart_limit, aborted and abandoned rebuilt from the slot (SettleFromSlotAsync,
//   which reads the slot from a snapshot before the result commit, since hooks run outside commits). Such a result is then bounded with the
//   tool's output limits (the defaults when it did not resolve), loses any structuredOutput (a nested call a hook turns into a success gets
//   its bounded output as value unless the tool declares a schema), and its control applies only when the task completes
//   (AfterHarnessResultAsync). The hooks are skipped past the restart limit (Live.Salvageable) and in the abort handler's last attempt
//   (restarts at the limit minus one); the cut-off check now comes before an interruption result too, so a non-replay-safe tool interrupted
//   restartLimit times ends with restart_limit.
// - Pi v1.5.0 sync (area tool-tasks, 1c6928b30): a call to a tool its request did not offer is a task whose input is flagged unoffered
//   (pi.tool version 3): it resolves nothing, so it settles tool_unavailable through afterTool even when the agent has the tool, and its
//   abort handler settles tool_unavailable too.
// - Pi v1.6.0 sync (faac585884ae2c013a6b1400f3e9e77f10b34bac): pi.tool is version 4. A call may park: `Execute` returns a park
//   (ToolPark; Parks.cs) and the task sleeps or waits at the `parked` checkpoint {phase: "parked", arguments, replay,
//   checkpoint?}, its slot `sleeping` or `waiting` without output; once woken, the `parked` phase resolves the tool again, records intent
//   with the wake ({phase: "execute", arguments, replay, wake?, checkpoint?}) and runs `Execute` again (api.Wake, api.Checkpoint). A
//   beforeTool hook may park the call at its gates (ParkAtGateAsync): the task sleeps at `call`, and every wake runs the whole phase
//   again. An invalid park ends the call with an invalid_park result. `Cancel` cleans up a parked call that is aborted (CancelAsync). One
//   run of the tool's code is an Attempt (StartAttemptAsync); a woken run starts from the details and diagnostics earlier runs left in
//   the slot and needs explicit keys for nested calls and tasks. api.StartToolAsync is executeTool with wait false, and api.CreateTaskAsync
//   with a key records the task under it in NestedCallDoc, whose members carry kind "nested" or "task" (absent: a nested call's, from
//   before keyed tasks). A result's durationMs is the span from the task's first run (TaskRecord.StartedAt) on the Harness clock,
//   computed in the settling commit, so results that never executed have one too; a record without StartedAt keeps the measured time
//   of the last attempt. The intent's two commits keep PiSharp's order (state first, slot second; above).
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the tool task (upstream <c>ToolTask</c>, kind <c>pi.tool</c>, version 4) and its nested call document.</summary>
public static partial class HarnessTasks
{
    /// <summary>
    /// Built-in tool task (upstream <c>ToolTask</c>): resolves the called tool among its phase agent's tools, validates, runs
    /// <c>beforeTool</c>, records intent, executes, runs <c>afterTool</c>, and settles the result, all in one <c>call</c> handler so nothing
    /// separates resolution from settlement. <c>execute</c> is reached only by recovery and applies the replay rule: a call reruns only when
    /// both its recorded and its current registration say <see cref="ToolReplay.Safe"/>; otherwise it settles <c>failed</c> with an
    /// <c>interrupted</c> result. A model-issued call settles by appending its result entry; a nested call by writing its result to its
    /// caller's <see cref="NestedCallDoc"/>. Every result the task writes passes <c>afterTool</c>, also the error results the Harness writes
    /// itself, except past the restart limit. A call flagged <c>unoffered</c> resolves nothing and settles <c>tool_unavailable</c>. A call
    /// that parks runs again in <c>parked</c>, which resolves the tool, records intent, and executes in one handler too. Version 1 tasks,
    /// which had only model-issued calls, version 2 tasks, which had no <c>unoffered</c>, and version 3 tasks, which had no <c>parked</c>
    /// phase, migrate.
    /// </summary>
    public static DurableTask ToolTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.tool",
        Version = 4,
        Initial = _ => JsonValue.Parse("""{"phase":"call"}"""),
        // Version 1 had only model-issued calls, without kind; version 3 added unoffered, absent in version 2 inputs; version 4 added
        // parked and the optional fields of execute, absent in older checkpoints.
        Migrate = (input, checkpoint, fromVersion) => new TaskMigrationResult(
            fromVersion == 1 ? JsonValue.Object([KeyValuePair.Create("kind", (JsonValue)"model"), .. (JsonObjectValue)input]) : input, checkpoint),
        Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
        {
            ["call"] = ToolPhases.CallAsync,
            ["parked"] = ToolPhases.ParkedAsync,
            ["execute"] = ToolPhases.ExecuteAsync,
        },
        Abort = ToolPhases.AbortAsync,
    });

    /// <summary>
    /// The nested calls of a tool call (upstream <c>NestedCallDoc</c>, kind <c>pi.tool.nested-call</c>): a task-scoped family of the calling
    /// task keyed by the nested call's key, so a rerun finds the calls it already made, and the caller reads each result. Each member is
    /// <c>{taskId?, result?}</c>: created with the nested task; the nested call's terminal commit adds its result
    /// (<see cref="NestedToolExecutionResult.ToJson"/>), absent for a call the scheduler faulted or orphaned. One document per call, so no
    /// commit rewrites the others. Retires with the caller. Since Pi v1.6.0 a member is one key of a running tool: a nested call
    /// (<c>kind: "nested"</c>), or a task created under the key (<c>kind: "task"</c>, <c>CreateTaskAsync</c> with a key); a member without
    /// <c>kind</c>, written before keyed tasks, is a nested call's.
    /// </summary>
    public static TaskDocFamilyToken NestedCallDoc { get; } = Documents.DefineTaskDocFamily("pi.tool.nested-call", 1, _ => JsonObjectValue.Empty,
        // The document retires with its task.
        checkpointWhen: (_, _, _) => false);
}

/// <summary>The tool task's phases and helpers (upstream <c>tool.ts</c>).</summary>
internal static partial class ToolPhases
{
    /// <summary>Memo of a tool task that made a nested call, in any attempt, committed before the first admission of each attempt: only
    /// then can <c>pi.live</c> hold slots below it, which settling removes. A crash between the two only costs a scan.</summary>
    internal const string MadeNestedCalls = "pi.tool.madeNestedCalls";

    /// <summary>Error text a nested call's summary keeps.</summary>
    private const int MaxSummaryErrorChars = 500;

    /// <summary>How a tool task ends; the result is settled either way. <c>failed</c> (execution threw or was interrupted) records
    /// cancellation intent for the conversations the call owns; a result with <c>IsError</c> still completes (upstream <c>Ending</c>).</summary>
    private sealed record Ending(TaskOutcomeStatus Status, string? Message = null, string? Reason = null)
    {
        public static Ending Completed { get; } = new(TaskOutcomeStatus.Completed);
        public static Ending Aborted { get; } = new(TaskOutcomeStatus.Aborted);
        /// <summary>A failure; <paramref name="reason"/>, when given, is its error's <c>detail.reason</c>.</summary>
        public static Ending Failed(string message, string? reason = null) => new(TaskOutcomeStatus.Failed, message, reason);
    }

    /// <summary>Arguments, or why they are invalid (upstream <c>Checked</c>).</summary>
    private readonly record struct Checked(JsonValue? Args, string? Error);

    /// <summary>The task input (upstream <c>ToolTaskInput</c>): a model-issued call, read from its assistant entry, or a nested call, whose
    /// call the input carries because no entry holds it. <c>Unoffered</c>: a model-issued call to a tool its request did not offer (upstream
    /// <c>isUnoffered</c>, spec §8.3), which settles <c>tool_unavailable</c> without resolving the name.</summary>
    internal sealed record ToolInput(bool Nested, EntryId Assistant, string CallId, TaskId Parent, string ParentCallId, string Key,
        JsonObjectValue? Call, bool Progress, bool Unoffered = false)
    {
        public static ToolInput Parse(JsonValue input)
        {
            var fields = (JsonObjectValue)input;
            if (fields.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "nested" })
                return new ToolInput(true, default, "", new TaskId(Id(fields["parent"])), ((JsonStringValue)fields["parentCallId"]).Value,
                    ((JsonStringValue)fields["key"]).Value, (JsonObjectValue)fields["call"],
                    !(fields.TryGetValue("progress", out var progress) && progress is JsonBooleanValue { Value: false }));
            return new ToolInput(false, new EntryId(Id(fields["assistant"])), ((JsonStringValue)fields["callId"]).Value, default, "", "", null, true,
                fields.TryGetValue("unoffered", out var unoffered) && unoffered is JsonBooleanValue { Value: true });
        }
    }

    /// <summary>The hooks a result rebuilt from the slot passes: the tool whose output limits bound it, null for the defaults (upstream's
    /// <c>hooks</c> argument of <c>settleFromSlot</c>). No value: the result settles as built, since the hooks may be what cuts the task
    /// off.</summary>
    private readonly record struct SlotHooks(ToolRegistration? Tool);

    // ─── Phases ─────────────────────────────────────────────────────────────

    public static async Task CallAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var input = ToolInput.Parse(task.Input);
        var call = await ReadCallAsync(runtime, input, context).ConfigureAwait(false);
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, input, call, context).ConfigureAwait(false);
            return;
        }
        var name = Name(call);
        var tool = input.Unoffered ? null : await ResolveToolAsync(runtime, input, name, context).ConfigureAwait(false);
        if (tool is null)
        {
            var unavailable = ToolResults.HarnessError("tool_unavailable", $"Tool {name} is not available");
            await RejectAsync(runtime, input, call, null, unavailable, context).ConfigureAwait(false);
            return;
        }
        var prepared = Prepare(tool, call["arguments"]);
        var checkedArgs = prepared.Error is null ? Validate(tool, call, prepared.Args!) : prepared;
        if (checkedArgs.Error is { } invalid)
        {
            await RejectAsync(runtime, input, call, tool, Invalid(invalid), context).ConfigureAwait(false);
            return;
        }
        var args = checkedArgs.Args!;
        string? block = null;
        GateSleep? sleep = null;
        await runtime.Hooks.EachAsync<ToolHooks, Func<JsonObjectValue, IHookApi, DurableContext, Task<BeforeToolDecision?>>>(hooks => hooks.BeforeTool,
            async hook =>
            {
                if (block is not null || sleep is not null) return;
                try
                {
                    var decision = await hook(call.SetItem("arguments", args), runtime, context).ConfigureAwait(false);
                    if (decision?.Block is not null) block = decision.Block;
                    else if (decision?.Sleep is not null) sleep = decision.Sleep;
                    else if (decision?.Arguments is not null) args = decision.Arguments;
                }
                catch (Exception error)
                {
                    if (runtime.Signal.IsCancellationRequested) throw;
                    block = DurableErrors.MessageOf(error);
                }
            }).ConfigureAwait(false);
        if (block is not null)
        {
            var blocked = ToolResults.HarnessError("blocked", $"Tool call blocked: {block}");
            await RejectAsync(runtime, input, call, tool, blocked, context).ConfigureAwait(false);
            return;
        }
        if (sleep is not null)
        {
            await ParkAtGateAsync(runtime, input, call, tool, sleep, context).ConfigureAwait(false);
            return;
        }
        var validated = Validate(tool, call, args);
        if (validated.Error is { } rejected)
        {
            await RejectAsync(runtime, input, call, tool, Invalid(rejected), context).ConfigureAwait(false);
            return;
        }
        var final = validated.Args!;
        await CommitIntentAsync(runtime, Intent(final, tool, null, null), final, context).ConfigureAwait(false);
        await RunAsync(runtime, input, call, tool, final, default, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Woken from a park: resolves the tool again, records intent with the wake, and runs <c>Execute</c> again (upstream <c>parked</c>). A
    /// tool that no longer resolves cannot run or clean up; the call ends <c>failed</c>, so what it owns is aborted.
    /// </summary>
    public static async Task ParkedAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var input = ToolInput.Parse(task.Input);
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var args = checkpoint["arguments"];
        var parked = checkpoint.TryGetValue("checkpoint", out var stored) ? stored : null;
        var wake = task.Wake;
        var call = await ReadCallAsync(runtime, input, context).ConfigureAwait(false);
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, input, call, context).ConfigureAwait(false);
            return;
        }
        var name = Name(call);
        var tool = await ResolveToolAsync(runtime, input, name, context).ConfigureAwait(false);
        if (tool is null)
        {
            var message = $"Tool {name} is not available";
            runtime.Report(new InvalidOperationException($"{message}: its parked call {CallIdOf(call)} cannot run again"));
            await SettleFromSlotAsync(runtime, input, call, new SlotHooks(null), Ending.Failed(message), "tool_unavailable", message, context)
                .ConfigureAwait(false);
            return;
        }
        await CommitIntentAsync(runtime, Intent(args, tool, wake, parked), args, context).ConfigureAwait(false);
        await RunAsync(runtime, input, call, tool, args, new Resumed(wake, parked), context).ConfigureAwait(false);
    }

    /// <summary>The <c>execute</c> checkpoint: the final arguments and the replay policy recorded before execution, and for a run after a
    /// park, why it woke and what it parked with.</summary>
    private static JsonObjectValue Intent(JsonValue args, ToolRegistration tool, WakeReason? wake, JsonValue? checkpoint) => new RecordJson.Writer()
        .Add("phase", "execute")
        .Add("arguments", args)
        .Add("replay", WireName.Of(tool.Replay ?? ToolReplay.Unsafe))
        .Add("wake", wake?.ToJson())
        .Add("checkpoint", checkpoint)
        .Build();

    /// <summary>
    /// Records intent (upstream <c>commitIntent</c>): the <c>execute</c> checkpoint, durable before the tool runs, and the slot showing the
    /// call <c>running</c> (and a nested call the arguments it runs with), stored like progress (<see cref="Live.ProgressCommit"/>), so
    /// parallel calls of one round share no immediate write to <c>pi.live</c>. Upstream commits the slot first; the intent goes first here so
    /// the agent events read its arguments when the slot turns running (see the file header).
    /// </summary>
    private static async Task CommitIntentAsync(ITaskRuntime runtime, JsonObjectValue intent, JsonValue args, DurableContext context)
    {
        await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Running(intent), context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var slot = Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId, out var nestedSlot);
            slot?.Set("status", "running");
            // A nested slot shows the arguments the call runs with, which repair, hooks, and coercion may have changed. Assigned whole: an
            // equal value records no change, and a rerun corrects what an earlier attempt showed.
            if (slot is not null && nestedSlot) slot.Set("arguments", args);
        }, context, Live.ProgressCommit).ConfigureAwait(false);
    }

    /// <summary>
    /// Parks the call at its gates, before it runs (spec suspendable §5.1; upstream <c>parkAtGate</c>): the task sleeps at <c>call</c>, its
    /// slot <c>sleeping</c>, and every wake runs the whole phase again. A sleep the commit refuses (no <c>until</c> or signals, a used key, a
    /// bad deadline) ends the call <c>invalid_park</c>, through <c>afterTool</c>.
    /// </summary>
    private static async Task ParkAtGateAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, ToolRegistration tool, GateSleep sleep,
        DurableContext context)
    {
        try
        {
            await runtime.CommitAsync(async (tx, _) =>
            {
                var slot = Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId);
                slot?.Set("status", "sleeping");
                return (TaskState?)new SleepingTaskState(JsonValue.Parse("""{"phase":"call"}"""), sleep.Until, sleep.Signals);
            }, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A close seals the Session before it signals the run.
            if (runtime.Signal.IsCancellationRequested || DurableTaskScheduler.CloseSignalOf(runtime).Token.IsCancellationRequested) throw;
            var invalid = ToolResults.HarnessError("invalid_park", $"Tool call {CallIdOf(call)} cannot wait at its gates: {DurableErrors.MessageOf(error)}");
            await RejectAsync(runtime, input, call, tool, invalid, context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a sleeping task is a tool call parked at its gates, before its intent (spec suspendable §5.2; upstream <c>gatedCall</c>): the
    /// registry, and every open, wake it, so the gates installed then decide.
    /// </summary>
    internal static bool GatedCall(TaskRecord record) =>
        record.Kind == HarnessTasks.ToolTask.Definition.Name && record.State is SleepingTaskState asleep && PhaseOf(asleep.Checkpoint) == "call";

    /// <summary>
    /// Whether a task's <c>abandonOnRestart</c> children survive a restart (spec suspendable §2.5; upstream <c>keepsChildren</c>): it is a
    /// tool call that was not cut off while running, parked or not started yet, whatever its status.
    /// </summary>
    internal static bool KeepsChildren(TaskRecord record)
    {
        if (record.Kind != HarnessTasks.ToolTask.Definition.Name) return false;
        // A tool task's checkpoint; absent once it holds or has its outcome.
        return (record.State as LiveTaskState)?.Checkpoint is { } checkpoint && PhaseOf(checkpoint) is "call" or "parked";
    }

    private static string? PhaseOf(JsonValue checkpoint) =>
        checkpoint is JsonObjectValue value && value.TryGetValue("phase", out var phase) && phase is JsonStringValue text ? text.Value : null;

    private static string CallIdOf(JsonObjectValue call) => ((JsonStringValue)call["id"]).Value;

    /// <summary>Recovery after intent: rerun only when the stored and the current policy both say <c>safe</c>.</summary>
    public static async Task ExecuteAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var input = ToolInput.Parse(task.Input);
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var args = checkpoint["arguments"];
        var replay = checkpoint["replay"] is JsonStringValue { Value: var stored } ? stored : null;
        var wake = checkpoint.TryGetValue("wake", out var woke) ? WakeReason.FromJson(woke) : null;
        var parked = checkpoint.TryGetValue("checkpoint", out var kept) ? kept : null;
        var call = await ReadCallAsync(runtime, input, context).ConfigureAwait(false);
        // Also before an interruption result: its afterTool hooks may be what cut the task off.
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, input, call, context).ConfigureAwait(false);
            return;
        }
        var name = Name(call);
        var tool = await ResolveToolAsync(runtime, input, name, context).ConfigureAwait(false);
        if (replay == "safe" && tool?.Replay == ToolReplay.Safe)
        {
            // The rerun reports from scratch: clear what the interrupted attempt published, and show the slot running with the arguments it
            // runs with again, as the deferred commit that showed them may have been lost with the crash. A woken run keeps the details and
            // diagnostics earlier runs left, as a park does.
            await runtime.CommitAsync(async (tx, _) =>
            {
                if (Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId, out var nestedSlot) is { } slot)
                {
                    if (wake is null) Live.ClearProgress(slot);
                    else Live.ClearOutput(slot);
                    slot.Set("status", "running");
                    if (nestedSlot) slot.Set("arguments", args);
                }
            }, context).ConfigureAwait(false);
            await RunAsync(runtime, input, call, tool, args, new Resumed(wake, parked), context).ConfigureAwait(false);
            return;
        }
        // A woken run may have restarted external work that nothing will collect now.
        if (wake is not null && tool is not null) await CancelAsync(runtime, input, call, tool, args, parked, context).ConfigureAwait(false);
        var message = $"Tool {name} was interrupted and may have partially run";
        // `failed` records cancellation intent, so the call's owned conversations, left unsupervised, are aborted. Not cut off too often
        // (checked above), so the hooks run.
        await SettleFromSlotAsync(runtime, input, call, new SlotHooks(tool), Ending.Failed(message), "interrupted", message, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a call cut off <c>settings.RestartLimit</c> times in a row (spec §5.4) with an error result, built from the slot like an
    /// interrupted call's, instead of running it again (upstream <c>endCutOff</c>). <c>failed</c>, like an interruption, so its owned work
    /// is aborted. Past the limit the hooks may be what keeps cutting the task off (<see cref="Live.Salvageable"/>), so they run only at it.
    /// </summary>
    private static async Task EndCutOffAsync(ITaskRuntime runtime, RunningTask task, ToolInput input, JsonObjectValue call, DurableContext context)
    {
        var message = $"Tool {Name(call)} was interrupted {Live.CutOffCount(task)}; it is not run again";
        SlotHooks? hooks = Live.Salvageable(task, runtime) ? new SlotHooks(await ToolForLimitsAsync(runtime, input, Name(call), context).ConfigureAwait(false)) : null;
        await SettleFromSlotAsync(runtime, input, call, hooks, Ending.Failed(message, "restart_limit"), "restart_limit", message, context).ConfigureAwait(false);
    }

    public static async Task AbortAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var input = ToolInput.Parse(task.Input);
        var call = await ReadCallAsync(runtime, input, context).ConfigureAwait(false);
        var name = Name(call);
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var phase = task.Phase;
        // Abandoned after a restart (TaskOptions.AbandonOnRestart): say whether the call may have run. An unoffered call could not run in any
        // case, and its result says so.
        var (code, message) = input.Unoffered
            ? ("tool_unavailable", $"Tool {name} is not available")
            : task.AbortReason != TaskAbortReason.Restart
                ? ("aborted", $"Tool {name} was aborted")
                : phase == "execute"
                    ? ("interrupted", $"Tool {name} was interrupted by a restart and may have partially run")
                    : phase == "parked"
                        ? ("abandoned", $"Tool {name} was parked: its caller ended with a restart")
                        : ("abandoned", $"Tool {name} was not started: its caller ended with a restart");
        var tool = await ToolForLimitsAsync(runtime, input, name, context).ConfigureAwait(false);
        // External work may be live while no code of the tool runs: parked, or woken and cut off before it parked again. The scheduler ends
        // an abort handler cut off restartLimit times itself, so Cancel cannot keep it from ending.
        if (phase == "parked" || (phase == "execute" && checkpoint.ContainsKey("wake")))
        {
            if (tool is not null)
                await CancelAsync(runtime, input, call, tool, checkpoint["arguments"],
                    checkpoint.TryGetValue("checkpoint", out var parked) ? parked : null, context).ConfigureAwait(false);
            else
                runtime.Report(new InvalidOperationException($"Tool {name} is not available: its parked call {CallIdOf(call)} was not cancelled"));
        }
        // The scheduler faults the task without a result when its last abort attempt is cut off. That attempt skips the hooks, which may
        // be what cuts it off.
        SlotHooks? hooks = (task.Restarts ?? 0) < runtime.Settings.RestartLimit - 1 ? new SlotHooks(tool) : null;
        await SettleFromSlotAsync(runtime, input, call, hooks, Ending.Aborted, code, message, context).ConfigureAwait(false);
    }

    /// <summary>Settles a result the Harness decided in the <c>call</c> phase, without running the tool: the tool did not resolve, the
    /// arguments are invalid, or <c>beforeTool</c> blocked the call. <c>afterTool</c> applies as to an executed call's result (upstream
    /// <c>reject</c>).</summary>
    private static async Task RejectAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, ToolRegistration? tool,
        ToolExecutionResult result, DurableContext context)
    {
        var settled = await AfterHarnessResultAsync(runtime, input, call, tool, result, null, context).ConfigureAwait(false);
        await SettleAsync(runtime, input, call, Ending.Completed, settled, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles an error result built from the call's slot (<see cref="FromSlot"/>): an interruption, the restart limit, or an abort (upstream
    /// <c>settleFromSlot</c>). The tool is not running, so the slot read before the commit is still current when it commits. With
    /// <paramref name="hooks"/>, the result passes <c>afterTool</c> and is bounded with the limits of its tool (the defaults when it is null);
    /// without, it is settled as built, when the hooks may be what cuts the task off.
    /// </summary>
    private static async Task SettleFromSlotAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, SlotHooks? hooks, Ending ending,
        string code, string message, DurableContext context)
    {
        var live = await runtime.SnapshotAsync(Live.LiveDoc, runtime.ConversationId, context).ConfigureAwait(false);
        var (result, truncation) = FromSlot(live is null ? null : Live.ToolSlotOf(live, runtime.TaskId), code, message);
        var settled = hooks is { } with
            ? await AfterHarnessResultAsync(runtime, input, call, with.Tool, result, truncation, context).ConfigureAwait(false)
            : truncation is null ? result : result with { Diagnostics = [.. result.Diagnostics ?? [], truncation] };
        await SettleAsync(runtime, input, call, ending, settled, context).ConfigureAwait(false);
    }

    /// <summary>The tool whose output limits bound a hooks' result: the called tool, or none when it, or the agent, fails to resolve, and for
    /// an unoffered call, which never resolves its name (upstream <c>toolForLimits</c>).</summary>
    private static async Task<ToolRegistration?> ToolForLimitsAsync(ITaskRuntime runtime, ToolInput input, string name, DurableContext context)
    {
        if (input.Unoffered) return null;
        try
        {
            return await ResolveToolAsync(runtime, input, name, context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ─── Resolution and validation ──────────────────────────────────────────

    /// <summary>The called tool: among the tools the model is offered for a model-issued call, else among those tools may call (upstream
    /// <c>resolveTool</c>).</summary>
    private static async Task<ToolRegistration?> ResolveToolAsync(ITaskRuntime runtime, ToolInput input, string name, DurableContext context)
    {
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        return (input.Nested ? agent.Callable : agent.Tools).FirstOrDefault(tool => tool.Name == name);
    }

    /// <summary>The tool's output limits, the defaults filled in; all defaults when the tool did not resolve (upstream
    /// <c>outputLimits</c>).</summary>
    private static OutputLimits OutputLimitsOf(ToolRegistration? tool) => new(
        tool?.OutputLimits?.MaxBytes ?? Truncation.DefaultMaxBytes,
        tool?.OutputLimits?.MaxLines ?? Truncation.DefaultMaxLines,
        tool?.OutputLimits?.Retain ?? ToolOutputRetention.Head);

    /// <summary>The call: from the input for a nested call, which hooks see with its parent, else the tool call <c>callId</c> of the
    /// assistant entry (upstream <c>readCall</c>).</summary>
    private static async Task<JsonObjectValue> ReadCallAsync(ITaskRuntime runtime, ToolInput input, DurableContext context)
    {
        if (input.Nested)
        {
            TraceCall(context, input.Call!);
            return input.Call!.SetItem("parent", JsonValue.Object([
                KeyValuePair.Create("taskId", (JsonValue)input.Parent.ToJson()),
                KeyValuePair.Create("callId", (JsonValue)input.ParentCallId),
            ]));
        }
        var entry = await runtime.EntryAsync(Entries.AssistantEntry, input.Assistant, context).ConfigureAwait(false);
        var message = entry?.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
        var call = message is not null && message.TryGetValue("role", out var role) && role is JsonStringValue { Value: "assistant" } &&
            message.TryGetValue("content", out var content) && content is JsonArrayValue blocks
            ? blocks.OfType<JsonObjectValue>().FirstOrDefault(block =>
                block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "toolCall" } &&
                block.TryGetValue("id", out var id) && id is JsonStringValue { Value: var value } && value == input.CallId)
            : null;
        if (call is null) throw new InvalidOperationException($"Entry {input.Assistant.Value} has no tool call {input.CallId}");
        TraceCall(context, call);
        return call;
    }

    /// <summary>The called tool's name and the call ID on the phase span; nothing of the arguments.</summary>
    private static void TraceCall(DurableContext context, JsonObjectValue call) =>
        context.Telemetry.SetAttributes(new PiSharp.Telemetry.SpanAttributes
        {
            ["tool.name"] = call.TryGetValue("name", out var name) && name is JsonStringValue nameText ? nameText.Value : null,
            ["tool.call_id"] = call.TryGetValue("id", out var id) && id is JsonStringValue idText ? idText.Value : null,
        });

    private static string Name(JsonObjectValue call) => ((JsonStringValue)call["name"]).Value;

    /// <summary>The call's arguments as repaired by the tool; a throwing repair makes them invalid (upstream <c>prepare</c>).</summary>
    private static Checked Prepare(ToolRegistration tool, JsonValue args)
    {
        if (tool.PrepareArguments is null) return new Checked(args, null);
        try
        {
            return new Checked(tool.PrepareArguments(args), null);
        }
        catch (Exception error)
        {
            return new Checked(null, DurableErrors.MessageOf(error));
        }
    }

    /// <summary>Arguments validated and coerced against the implementation's schema (upstream <c>validate</c>): the call with these
    /// arguments, so a call whose argument text was malformed fails with its <c>argumentsError</c>.</summary>
    private static Checked Validate(ToolRegistration tool, JsonObjectValue call, JsonValue args)
    {
        try
        {
            var argumentsError = call.TryGetValue("argumentsError", out var error) && error is JsonStringValue text ? text.Value : null;
            var result = ToolArgumentValidation.Validate(Name(call), tool.Parameters, args, tool.ParametersOrigin, argumentsError);
            if (!result.IsValid) return new Checked(null, result.ErrorMessage);
            return new Checked(result.Value, null);
        }
        catch (Exception error)
        {
            return new Checked(null, DurableErrors.MessageOf(error));
        }
    }

    private static ToolExecutionResult Invalid(string message) => ToolResults.HarnessError("invalid_arguments", message);

}
