// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/tool.ts.
//
// Mappings:
// - Input, checkpoints and results are upstream's JSON in its key order. Input (version 2) {kind: "model", assistant, callId} or {kind:
//   "nested", parent, parentCallId, key, call, progress?}; checkpoints {phase: "call"} and {phase: "execute", arguments, replay}; result
//   {kind: "model", entryId, control?} (control {addTools?, terminate?, handoff?}) or {kind: "nested"}. Migrate turns version 1 input
//   {assistant, callId} into {kind: "model", assistant, callId}; a version 1 result has no kind and is a model-issued call's.
// - NestedCallDoc (pi.tool.nested-call, a task-scoped family of the caller) holds {taskId?, result?}, the result being
//   NestedToolExecutionResult.ToJson.
// - Calls are pi-ai ToolCall wire JSON; a nested call's hook call adds `parent: {taskId, callId}`. Arguments are validated by
//   Internal/PiAi/Validation.cs, an internal copy of PiSharp.Agent's ToolArgumentValidation (the agent's port of pi-ai's
//   validateToolArguments, packages/ai/src/utils/validation.ts; the core does not reference PiSharp.Agent), with the tool's ParametersOrigin
//   (default TypeBox, as upstream tools declare TypeBox schemas). Structured output is checked with the same file's TypeBox Compile port
//   (Check, and the first of Errors for the message).
// - `runtime.signal.aborted` is ITaskRuntime.Signal; an abort reaches a running tool as its context's cancellation, so the tool rejects
//   with an OperationCanceledException (upstream's AbortError) or the reason.
// - The tool reports from any thread; what it reported (output buffer, diagnostics, details, the ended flag) sits behind one lock, and the
//   progress commits run on the Harness TimeProvider's timers (output.ts). A nested call is admitted under that lock, so none starts after
//   the call ended, as upstream's single thread guarantees.
// - Upstream chains each nested call's admission on the attempt's madeNestedCalls memo, so admissions commit in call order, back to back
//   once the memo committed. NestedCalls queues them behind the memo and enqueues their commits in call order under one lock, so the
//   commits (and the task and document IDs they allocate) are upstream's on any thread.
// - performance.now() is a Stopwatch; Math.round is round half up.
// - A ToolExecutionResult field left null is upstream's undefined: Output null takes the retained output, Details null the last running
//   details; JsonValue.Null is an explicit null. A C# JsonValue cannot hold undefined, so upstream's copyJson(..., {omitUndefinedProperties})
//   of arguments, diagnostics and details has nothing to drop.
// - Upstream's copyJson of a control drops undefined keys; a C# ToolControl has none, and an unset Terminate is omitted.
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

/// <summary>Built-in tasks: the tool task (upstream <c>ToolTask</c>, kind <c>pi.tool</c>, version 2) and its nested call document.</summary>
public static partial class HarnessTasks
{
    /// <summary>
    /// Built-in tool task (upstream <c>ToolTask</c>): resolves the called tool among its phase agent's tools, validates, runs
    /// <c>beforeTool</c>, records intent, executes, runs <c>afterTool</c>, and settles the result, all in one <c>call</c> handler so nothing
    /// separates resolution from settlement. <c>execute</c> is reached only by recovery and applies the replay rule: a call reruns only when
    /// both its recorded and its current registration say <see cref="ToolReplay.Safe"/>; otherwise it settles <c>failed</c> with an
    /// <c>interrupted</c> result. A model-issued call settles by appending its result entry; a nested call by writing its result to its
    /// caller's <see cref="NestedCallDoc"/>. Version 1 tasks, which had only model-issued calls, migrate.
    /// </summary>
    public static DurableTask ToolTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.tool",
        Version = 2,
        Initial = _ => JsonValue.Parse("""{"phase":"call"}"""),
        // Version 1 had only model-issued calls.
        Migrate = (input, checkpoint, _) => new TaskMigrationResult(
            JsonValue.Object([KeyValuePair.Create("kind", (JsonValue)"model"), .. (JsonObjectValue)input]), checkpoint),
        Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
        {
            ["call"] = ToolPhases.CallAsync,
            ["execute"] = ToolPhases.ExecuteAsync,
        },
        Abort = ToolPhases.AbortAsync,
    });

    /// <summary>
    /// The nested calls of a tool call (upstream <c>NestedCallDoc</c>, kind <c>pi.tool.nested-call</c>): a task-scoped family of the calling
    /// task keyed by the nested call's key, so a rerun finds the calls it already made, and the caller reads each result. Each member is
    /// <c>{taskId?, result?}</c>: created with the nested task; the nested call's terminal commit adds its result
    /// (<see cref="NestedToolExecutionResult.ToJson"/>), absent for a call the scheduler faulted or orphaned. One document per call, so no
    /// commit rewrites the others. Retires with the caller.
    /// </summary>
    public static TaskDocFamilyToken NestedCallDoc { get; } = Documents.DefineTaskDocFamily("pi.tool.nested-call", 1, _ => JsonObjectValue.Empty);
}

/// <summary>The tool task's phases and helpers (upstream <c>tool.ts</c>).</summary>
internal static class ToolPhases
{
    /// <summary>Memo of a tool task that made a nested call, in any attempt, committed before the first admission of each attempt: only
    /// then can <c>pi.live</c> hold slots below it, which settling removes. A crash between the two only costs a scan.</summary>
    internal const string MadeNestedCalls = "pi.tool.madeNestedCalls";

    /// <summary>Error text a nested call's summary keeps.</summary>
    private const int MaxSummaryErrorChars = 500;

    /// <summary>How a tool task ends; the result is settled either way. <c>failed</c> (execution threw or was interrupted) records
    /// cancellation intent for the conversations the call owns; a result with <c>IsError</c> still completes (upstream <c>Ending</c>).</summary>
    private sealed record Ending(TaskOutcomeStatus Status, string? Message = null)
    {
        public static Ending Completed { get; } = new(TaskOutcomeStatus.Completed);
        public static Ending Aborted { get; } = new(TaskOutcomeStatus.Aborted);
        public static Ending Failed(string message) => new(TaskOutcomeStatus.Failed, message);
    }

    /// <summary>Arguments, or why they are invalid (upstream <c>Checked</c>).</summary>
    private readonly record struct Checked(JsonValue? Args, string? Error);

    /// <summary>The task input (upstream <c>ToolTaskInput</c>): a model-issued call, read from its assistant entry, or a nested call, whose
    /// call the input carries because no entry holds it.</summary>
    internal sealed record ToolInput(bool Nested, EntryId Assistant, string CallId, TaskId Parent, string ParentCallId, string Key,
        JsonObjectValue? Call, bool Progress)
    {
        public static ToolInput Parse(JsonValue input)
        {
            var fields = (JsonObjectValue)input;
            if (fields.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "nested" })
                return new ToolInput(true, default, "", new TaskId(Id(fields["parent"])), ((JsonStringValue)fields["parentCallId"]).Value,
                    ((JsonStringValue)fields["key"]).Value, (JsonObjectValue)fields["call"],
                    !(fields.TryGetValue("progress", out var progress) && progress is JsonBooleanValue { Value: false }));
            return new ToolInput(false, new EntryId(Id(fields["assistant"])), ((JsonStringValue)fields["callId"]).Value, default, "", "", null, true);
        }
    }

    // ─── Phases ─────────────────────────────────────────────────────────────

    public static async Task CallAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var input = ToolInput.Parse(task.Input);
        var call = await ReadCallAsync(runtime, input, context).ConfigureAwait(false);
        var name = Name(call);
        var tool = await ResolveToolAsync(runtime, input, name, context).ConfigureAwait(false);
        if (tool is null)
        {
            var unavailable = ToolResults.HarnessError("tool_unavailable", $"Tool {name} is not available");
            await SettleAsync(runtime, input, call, Ending.Completed, _ => unavailable, context).ConfigureAwait(false);
            return;
        }
        var prepared = Prepare(tool, call["arguments"]);
        var checkedArgs = prepared.Error is null ? Validate(tool, prepared.Args!) : prepared;
        if (checkedArgs.Error is { } invalid)
        {
            await SettleAsync(runtime, input, call, Ending.Completed, _ => Invalid(invalid), context).ConfigureAwait(false);
            return;
        }
        var args = checkedArgs.Args!;
        string? block = null;
        await runtime.Hooks.EachAsync<ToolHooks, Func<JsonObjectValue, IHookApi, DurableContext, Task<BeforeToolDecision?>>>(hooks => hooks.BeforeTool,
            async hook =>
            {
                if (block is not null) return;
                try
                {
                    var decision = await hook(call.SetItem("arguments", args), runtime, context).ConfigureAwait(false);
                    if (decision?.Block is not null) block = decision.Block;
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
            await SettleAsync(runtime, input, call, Ending.Completed, _ => blocked, context).ConfigureAwait(false);
            return;
        }
        var validated = Validate(tool, args);
        if (validated.Error is { } rejected)
        {
            await SettleAsync(runtime, input, call, Ending.Completed, _ => Invalid(rejected), context).ConfigureAwait(false);
            return;
        }
        var final = validated.Args!;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var slot = Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId);
            slot?.Set("status", "running");
            // A nested slot shows the arguments the call runs with, which repair, hooks, and coercion may have changed. Compared with the
            // input, which the slot got at admission.
            var changed = input.Nested && !JsonValue.DeepEquals(input.Call!["arguments"], final);
            if (changed && slot is not null && Live.IsNested(slot)) slot.Set("arguments", final);
            var intent = new RecordJson.Writer()
                .Add("phase", "execute")
                .Add("arguments", final)
                .Add("replay", WireName.Of(tool.Replay ?? ToolReplay.Unsafe))
                .Build();
            return (TaskState?)NextTaskState.Running(intent);
        }, context).ConfigureAwait(false);
        await RunAsync(runtime, input, call, tool, final, context).ConfigureAwait(false);
    }

    /// <summary>Recovery after intent: rerun only when the stored and the current policy both say <c>safe</c>.</summary>
    public static async Task ExecuteAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var input = ToolInput.Parse(task.Input);
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var args = checkpoint["arguments"];
        var replay = checkpoint["replay"] is JsonStringValue { Value: var stored } ? stored : null;
        var call = await ReadCallAsync(runtime, input, context).ConfigureAwait(false);
        var name = Name(call);
        var tool = await ResolveToolAsync(runtime, input, name, context).ConfigureAwait(false);
        if (replay == "safe" && tool?.Replay == ToolReplay.Safe)
        {
            // The rerun reports from scratch; clear what the interrupted attempt published.
            await runtime.CommitAsync(async (tx, _) =>
            {
                if (Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId) is { } slot)
                    Live.ClearProgress(slot);
            }, context).ConfigureAwait(false);
            await RunAsync(runtime, input, call, tool, args, context).ConfigureAwait(false);
            return;
        }
        var message = $"Tool {name} was interrupted and may have partially run";
        // `failed` records cancellation intent, so the call's owned conversations, left unsupervised, are aborted.
        await SettleAsync(runtime, input, call, Ending.Failed(message), slot => FromSlot(slot, "interrupted", message), context).ConfigureAwait(false);
    }

    public static async Task AbortAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var input = ToolInput.Parse(task.Input);
        var call = await ReadCallAsync(runtime, input, context).ConfigureAwait(false);
        var name = Name(call);
        // Abandoned after a restart (TaskOptions.AbandonOnRestart): say whether the call may have run.
        var (code, message) = task.AbortReason != TaskAbortReason.Restart
            ? ("aborted", $"Tool {name} was aborted")
            : task.Phase == "execute"
                ? ("interrupted", $"Tool {name} was interrupted by a restart and may have partially run")
                : ("abandoned", $"Tool {name} was not started: its caller ended with a restart");
        await SettleAsync(runtime, input, call, Ending.Aborted, slot => FromSlot(slot, code, message), context).ConfigureAwait(false);
    }

    // ─── Resolution and validation ──────────────────────────────────────────

    /// <summary>The called tool: among the tools the model is offered for a model-issued call, else among those tools may call (upstream
    /// <c>resolveTool</c>).</summary>
    private static async Task<ToolRegistration?> ResolveToolAsync(ITaskRuntime runtime, ToolInput input, string name, DurableContext context)
    {
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        return (input.Nested ? agent.Callable : agent.Tools).FirstOrDefault(tool => tool.Name == name);
    }

    /// <summary>The call: from the input for a nested call, which hooks see with its parent, else the tool call <c>callId</c> of the
    /// assistant entry (upstream <c>readCall</c>).</summary>
    private static async Task<JsonObjectValue> ReadCallAsync(ITaskRuntime runtime, ToolInput input, DurableContext context)
    {
        if (input.Nested)
            return input.Call!.SetItem("parent", JsonValue.Object([
                KeyValuePair.Create("taskId", (JsonValue)input.Parent.ToJson()),
                KeyValuePair.Create("callId", (JsonValue)input.ParentCallId),
            ]));
        var entry = await runtime.EntryAsync(Entries.AssistantEntry, input.Assistant, context).ConfigureAwait(false);
        var message = entry?.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
        var call = message is not null && message.TryGetValue("role", out var role) && role is JsonStringValue { Value: "assistant" } &&
            message.TryGetValue("content", out var content) && content is JsonArrayValue blocks
            ? blocks.OfType<JsonObjectValue>().FirstOrDefault(block =>
                block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "toolCall" } &&
                block.TryGetValue("id", out var id) && id is JsonStringValue { Value: var value } && value == input.CallId)
            : null;
        return call ?? throw new InvalidOperationException($"Entry {input.Assistant.Value} has no tool call {input.CallId}");
    }

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

    /// <summary>Arguments validated and coerced against the implementation's schema (upstream <c>validate</c>).</summary>
    private static Checked Validate(ToolRegistration tool, JsonValue args)
    {
        try
        {
            var result = ToolArgumentValidation.ValidateJson(tool.Name, tool.Parameters.ToJson(), args.ToJson(), tool.ParametersOrigin);
            if (!result.IsValid) return new Checked(null, result.ErrorMessage);
            return new Checked(result.ArgumentsJson is { } json ? JsonValue.Parse(json) : JsonValue.Null, null);
        }
        catch (Exception error)
        {
            return new Checked(null, DurableErrors.MessageOf(error));
        }
    }

    private static ToolExecutionResult Invalid(string message) => ToolResults.HarnessError("invalid_arguments", message);

    // ─── Execution ──────────────────────────────────────────────────────────

    /// <summary>Executes with the resolved implementation, then settles its result (upstream <c>run</c>).</summary>
    private static async Task RunAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, ToolRegistration tool, JsonValue args,
        DurableContext context)
    {
        var limits = new OutputLimits(
            tool.OutputLimits?.MaxBytes ?? Truncation.DefaultMaxBytes,
            tool.OutputLimits?.MaxLines ?? Truncation.DefaultMaxLines,
            tool.OutputLimits?.Retain ?? ToolOutputRetention.Head);
        var reported = new ToolReport(new OutputBuffer(limits), limits);
        var streams = !input.Nested || input.Progress;
        var progress = PublishProgress(runtime, reported, streams, context);
        // A caller that can rerun reattaches to its nested calls and child tasks after a restart; any other caller never resumes, so they
        // are abandoned (TaskOptions.AbandonOnRestart).
        var nested = new NestedCalls(runtime, reported, ((JsonStringValue)call["id"]).Value, resumes: tool.Replay == ToolReplay.Safe);
        ToolExecutionResult result;
        var ending = Ending.Completed;
        // Execution time of this attempt; a rerun after recovery measures only itself.
        double? durationMs = null;
        try
        {
            // Built for this call, so a rerun after recovery gets the conversation's environment at that time.
            var env = await runtime.EnvAsync(context).ConfigureAwait(false);
            var api = new ToolExecutionApi(runtime, nested.CallId, reported, progress, env, nested);
            var started = Stopwatch.GetTimestamp();
            try
            {
                Task<ToolExecutionResult> executing;
                nested.BeginPrefix();
                try
                {
                    executing = tool.Execute(args, api, context);
                }
                finally
                {
                    nested.EndPrefix();
                }
                result = await executing.ConfigureAwait(false);
            }
            finally
            {
                durationMs = Math.Floor(Stopwatch.GetElapsedTime(started).TotalMilliseconds + 0.5);
            }
        }
        catch (Exception error)
        {
            if (runtime.Signal.IsCancellationRequested)
            {
                reported.MarkEnded();
                foreach (var waiter in await progress.StopAsync().ConfigureAwait(false)) waiter.TrySetException(error);
                throw;
            }
            result = new ToolExecutionResult { IsError = true, Diagnostics = [ToolResults.Diagnostic("tool_error", DurableErrors.MessageOf(error))] };
            // A throw, from Execute or from building the environment, ends the task `failed`, which cancels what the call owned; it no longer
            // supervises it. The error text is already in the result.
            ending = Ending.Failed($"Tool {Name(call)} threw");
        }
        reported.MarkEnded();
        // No admission starts after the call ended; let those underway commit, so settlement aborts and lists their calls.
        await nested.AdmissionsSettledAsync().ConfigureAwait(false);
        reported.End();
        // Details still waiting for a progress commit settle with the terminal commit, the final flush.
        var pending = await progress.StopAsync().ConfigureAwait(false);
        try
        {
            var settled = await FinalResultAsync(runtime, input, call, tool, result, reported, context).ConfigureAwait(false);
            await SettleAsync(runtime, input, call, ending, _ => settled, context, durationMs).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            foreach (var waiter in pending) waiter.TrySetException(error);
            throw;
        }
        foreach (var waiter in pending) waiter.TrySetResult();
    }

    /// <summary>Throttled commits of what the tool reported into its slot, in <c>pi.live.tools</c> or <c>pi.live.nestedTools</c>, each
    /// writing only what changed since the last one; none when <paramref name="enabled"/> is false (upstream <c>publishProgress</c>).</summary>
    private static Progress PublishProgress(ITaskRuntime runtime, ToolReport reported, bool enabled, DurableContext context)
    {
        var written = (Text: "", Details: (JsonValue?)null, Diagnostics: 0);
        return new Progress(() =>
        {
            if (!enabled) return Task.FromResult(0L);
            // Capture everything synchronously: the tool keeps reporting while the commit is in flight.
            var (snapshot, details, added, count) = reported.Capture(written.Diagnostics);
            var detailsChanged = !ReferenceEquals(details, written.Details);
            // What the commit writes, as Chord diffs the string: an append, a trim plus an append of what follows the shared part, or the
            // whole window when its bounded overlap search finds nothing.
            long bytes = 0;
            if (snapshot.Text != written.Text)
            {
                var shared = snapshot.Text.StartsWith(written.Text, StringComparison.Ordinal)
                    ? written.Text.Length
                    : DeltaValidation.Overlap(written.Text, snapshot.Text, 65_536);
                bytes += Truncation.Utf8ByteLength(snapshot.Text[shared..]);
            }
            if (detailsChanged) bytes += Truncation.Utf8ByteLength((details ?? JsonValue.Null).ToJson());
            if (added.Count > 0) bytes += Truncation.Utf8ByteLength(JsonValue.Array(added.Select(ToolResults.DiagnosticJson)).ToJson());
            return Commit();

            async Task<long> Commit()
            {
                await runtime.CommitAsync(async (tx, _) =>
                {
                    var slot = Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId);
                    if (slot is null) return;
                    // REMINDER: assign `output` as one string field. Chord then diffs it into an append, or a trim plus an append for a
                    // sliding tail; replacing the slot object would record the whole window on every commit.
                    var output = slot.ContainsKey("output") && slot.Get("output") is JsonStringValue { Value: var text } ? text : "";
                    if (output != snapshot.Text) slot.Set("output", snapshot.Text);
                    if (snapshot.DroppedBytes > 0) slot.Set("droppedBytes", snapshot.DroppedBytes);
                    if (snapshot.DroppedLines > 0) slot.Set("droppedLines", snapshot.DroppedLines);
                    // Diff details leaf by leaf and append new diagnostics, so each commit writes only what changed.
                    if (detailsChanged && details is not null) HarnessJson.AssignJson(slot, "details", details);
                    if (added.Count > 0)
                    {
                        if (!slot.ContainsKey("diagnostics")) slot.Set("diagnostics", JsonArrayValue.Empty);
                        var diagnostics = slot.GetArray("diagnostics");
                        foreach (var diagnostic in added) diagnostics.Add(ToolResults.DiagnosticJson(diagnostic));
                    }
                }, context).ConfigureAwait(false);
                written = (snapshot.Text, details, count);
                return bytes;
            }
        }, error =>
        {
            // Rejections after an abort mark or close are expected; the committed state stays consistent.
            if (runtime.Signal.IsCancellationRequested) return;
            try { runtime.Report(error); }
            catch (Exception) { /* the invocation ended */ }
        }, runtime.Settings.Progress.OutputIntervalMs, (runtime as ITaskTimeSource)?.TimeProvider);
    }

    /// <summary>
    /// The settled result: the tool's result with the retained output and last details as fallbacks, its diagnostics after those reported
    /// through the api, <c>afterTool</c> applied, explicit text bounded, and <c>StructuredOutput</c> checked against the tool's schema, with
    /// the Harness's diagnostics last (upstream <c>finalResult</c>). A nested call whose structured output breaks the contract gets an error
    /// result without it; a model-issued call only loses it, and the break is reported to the host.
    /// </summary>
    private static async Task<ToolExecutionResult> FinalResultAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call,
        ToolRegistration tool, ToolExecutionResult result, ToolReport reported, DurableContext context)
    {
        var harness = new List<ToolDiagnostic>();
        var (retained, details, diagnostics) = reported.Final(result.Output is null);
        var output = retained is not { } kept ? result.Output! : kept.Text.Length == 0 ? JsonValue.Array() : JsonValue.Array(Text(kept.Text));
        var final = result with
        {
            Output = output,
            Details = result.Details ?? details,
            Diagnostics = [.. diagnostics, .. result.Diagnostics ?? []],
        };
        await runtime.Hooks.EachAsync<ToolHooks, Func<JsonObjectValue, ToolExecutionResult, IHookApi, DurableContext, Task<ToolExecutionResult?>>>(
            hooks => hooks.AfterTool,
            async hook => final = await hook(call, final, runtime, context).ConfigureAwait(false) ?? final).ConfigureAwait(false);
        // The retained output's truncation applies only while afterTool kept that output.
        if (retained is { DroppedBytes: > 0 } dropped && ReferenceEquals(final.Output, output))
            harness.Add(Truncated(dropped.DroppedLines, dropped.DroppedBytes, reported.Limits.Retain));
        var bounded = BoundContent(final.Output ?? JsonValue.Array(), reported.Limits);
        if (bounded.DroppedBytes > 0) harness.Add(Truncated(bounded.DroppedLines, bounded.DroppedBytes, reported.Limits.Retain));
        if (StructuredOutputError(tool, final) is { } broken)
        {
            var rest = final with { StructuredOutput = null };
            if (input.Nested)
            {
                final = rest with { IsError = true };
                harness.Add(ToolResults.Diagnostic("invalid_structured_output", broken));
            }
            else
            {
                // The model never sees structured output, so its call stands; the host learns of the broken tool.
                final = rest;
                runtime.Report(new InvalidOperationException(broken));
            }
        }
        // Programs get the tool's own value: without a schema, its output. The model reads only `output`, so model-issued calls skip both.
        if (input.Nested && tool.StructuredOutputSchema is null) final = final with { StructuredOutput = OutputValue(bounded.Content) };
        // A schema tool's error result without structured output has its text only in the output, which programs never get: it becomes an
        // error diagnostic, as a thrown error's message does, unless one says what went wrong already.
        if (input.Nested && tool.StructuredOutputSchema is not null && final.IsError == true && final.StructuredOutput is null &&
            !(final.Diagnostics ?? []).Concat(harness).Any(diagnostic => diagnostic.Severity == ToolDiagnosticSeverity.Error))
        {
            var text = string.Join("\n", bounded.Content.OfType<JsonObjectValue>().Where(IsText)
                .Select(item => item["text"] is JsonStringValue { Value: var value } ? value : ""));
            if (text.Length > 0) harness.Add(ToolResults.Diagnostic("tool_error", text));
        }
        return final with { Output = bounded.Content, Diagnostics = [.. final.Diagnostics ?? [], .. harness] };
    }

    private static readonly ConditionalWeakTable<JsonObjectValue, Validator> StructuredValidators = new();

    /// <summary>Why a result's structured output breaks the tool's contract, or null when it keeps it (upstream
    /// <c>structuredOutputError</c>).</summary>
    private static string? StructuredOutputError(ToolRegistration tool, ToolExecutionResult result)
    {
        var schema = tool.StructuredOutputSchema;
        var value = result.StructuredOutput;
        if (schema is null)
            return value is null ? null : $"Tool {tool.Name} returned structuredOutput but declares no structuredOutputSchema";
        if (value is null) return result.IsError == true ? null : $"Tool {tool.Name} returned no structuredOutput";
        var validator = StructuredValidators.GetValue(schema, static key => Validator.Compile(Js.Parse(key.ToJson())));
        var candidate = Js.Parse(value.ToJson());
        if (validator.Check(candidate)) return null;
        var first = validator.Errors(candidate).FirstOrDefault();
        var path = first is null ? "" : (first.InstancePath.StartsWith('/') ? first.InstancePath[1..] : first.InstancePath).Replace('/', '.');
        if (path.Length == 0) path = "root";
        return $"Tool {tool.Name} returned structuredOutput that does not match its schema: {path}: {first?.Message ?? "invalid"}";
    }

    /// <summary>
    /// Commits the tool's terminal state and marks its slot done (upstream <c>settle</c>). A model-issued call appends its result entry and
    /// ends with the entry ID; a nested call writes its result to the caller's <c>NestedCallDoc</c>, records its usage, and ends with a small
    /// receipt. Nested calls this call left running are aborted first, so they report into their slots before the slots below the call leave
    /// <c>pi.live</c>. <paramref name="build"/> receives the slot so interruption and abort can report the durable partial output.
    /// </summary>
    private static async Task SettleAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, Ending ending,
        Func<DraftObject?, ToolExecutionResult> build, DurableContext context, double? durationMs = null)
    {
        // A crash in between leaves the call in `execute`: a safe rerun reattaches to the calls, an interruption ends here. Every live nested
        // call the task owns, also those an earlier attempt made and this one never called again.
        var nestedIds = (await runtime.OwnedTasksAsync(context).ConfigureAwait(false))
            .Where(record => record.Kind == HarnessTasks.ToolTask.Definition.Name && ToolInput.Parse(record.Input).Nested)
            .Select(record => record.Id).ToList();
        // Read before the settling commit: no admission starts after the call ended, and those underway were awaited.
        var madeNested = await runtime.MemoAsync(MadeNestedCalls, context).ConfigureAwait(false) is JsonBooleanValue { Value: true };
        // Mark them all before waiting for any: one may run until a sibling is cancelled.
        await Task.WhenAll(nestedIds.Select(id => runtime.AbortOwnedAsync(id, context))).ConfigureAwait(false);
        var name = Name(call);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var conversationId = runtime.ConversationId;
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var slot = Live.ToolSlot(live, runtime.TaskId);
            var result = build(slot);
            JsonObjectValue settled;
            if (input.Nested)
            {
                var nested = NestedResult(runtime.TaskId, result, durationMs);
                if (nested.Usage is { } usage) await UsageLedger.RecordUsageAsync(tx, conversationId, "tools", name, usage).ConfigureAwait(false);
                // The caller is never terminal before its owned nested calls, so its document is open; another task in it is a bug, not a race.
                var state = await tx.DocAsync(HarnessTasks.NestedCallDoc, input.Parent, input.Key, JsonValue.Null).ConfigureAwait(false);
                if (!(state.Get("taskId") is JsonNumberValue member && (long)member.Value == runtime.TaskId.Value))
                    throw new InvalidOperationException($"Nested call {((JsonStringValue)call["id"]).Value} is not in its caller's index");
                state.Set("result", nested.ToJson());
                if (slot is not null && Live.IsNested(slot))
                {
                    Live.FinishSlot(slot);
                    slot.Set("summary", SummaryOf(nested, result.Output ?? JsonArrayValue.Empty));
                }
                settled = JsonValue.Object([KeyValuePair.Create("kind", (JsonValue)"nested")]);
            }
            else
            {
                var entry = await ToolResults.AppendToolResultAsync(tx, conversationId, call, result, runtime.Now(), durationMs).ConfigureAwait(false);
                if (slot is not null && !Live.IsNested(slot))
                {
                    Live.FinishSlot(slot);
                    slot.Set("entry", entry.Id.ToJson());
                }
                // Tools build control objects freely; the task result is strict JSON.
                var writer = new RecordJson.Writer().Add("kind", "model").Add("entryId", entry.Id.ToJson());
                if (result.Control is not null && ending.Status == TaskOutcomeStatus.Completed) writer.Add("control", ToolResults.ControlJson(result.Control));
                settled = writer.Build();
            }
            // A call that made no nested calls, in any attempt, has no slots below it; skipping the scan keeps many leaves linear. Committed
            // before the read: no nested call is admitted after the call ended.
            if (madeNested) Live.RemoveNestedSlots(live, runtime.TaskId);
            if (ending.Status == TaskOutcomeStatus.Aborted) return (TaskState?)NextTaskState.Terminal(new AbortedTaskOutcome(null, settled));
            if (ending.Status == TaskOutcomeStatus.Failed)
                return NextTaskState.Terminal(new FailedTaskOutcome(new TaskOutcomeError(ending.Message!), settled));
            return NextTaskState.Completed(settled);
        }, context).ConfigureAwait(false);
    }

    /// <summary>A nested call's result, as stored and returned: without <c>output</c>, which only the model reads, and without
    /// <c>control</c>, which only model-issued calls apply (upstream <c>nestedResult</c>).</summary>
    private static NestedToolExecutionResult NestedResult(TaskId taskId, ToolExecutionResult result, double? durationMs) => new()
    {
        TaskId = taskId,
        StructuredOutput = result.StructuredOutput,
        IsError = result.IsError ?? false,
        Details = result.Details,
        Diagnostics = result.Diagnostics ?? [],
        Usage = result.Usage,
        DurationMs = durationMs,
    };

    /// <summary>
    /// Commits the <see cref="MadeNestedCalls"/> memo (as <c>runtime.memo(name, true)</c> does: a memo already set wins) and, inside that commit
    /// on the Session line, runs <paramref name="committed"/>, which enqueues the waiting admissions behind it (NestedCalls).
    /// </summary>
    internal static Task CommitMadeNestedCallsAsync(ITaskRuntime runtime, Action committed, DurableContext context) =>
        runtime.CommitAsync((tx, current) =>
        {
            if (MemoOf(current) is null)
                ((Transaction)tx).SetTask(current.Record with { Memos = (current.Memos ?? JsonObjectValue.Empty).SetItem(MadeNestedCalls, true) });
            committed();
        }, context);

    private static JsonValue? MemoOf(RunningTask task) => task.Memos is { } memos && memos.TryGetValue(MadeNestedCalls, out var memo) ? memo : null;

    /// <summary>
    /// Creates nested call <paramref name="key"/> of the call <paramref name="parentCallId"/> in one commit: its tool task, owned by the
    /// calling task, its slot, and its <c>NestedCallDoc</c> (upstream <c>startNestedCall</c>). A key already made returns that call's task
    /// when it names the same tool and arguments.
    /// </summary>
    internal static async Task<TaskId> StartNestedCallAsync(ITaskRuntime runtime, string parentCallId, string name, JsonValue args,
        string key, bool? progress, bool abandonOnRestart, DurableContext context)
    {
        var call = new RecordJson.Writer()
            .Add("type", "toolCall")
            .Add("id", $"{parentCallId}/{key}")
            .Add("name", name)
            .Add("arguments", args)
            .Build();
        TaskId id = default;
        await runtime.CommitAsync(async (tx, current) =>
        {
            // Upstream admits a nested call only once the memo committed.
            if (MemoOf(current) is null) throw new InvalidOperationException($"Tool call {parentCallId} has not committed {MadeNestedCalls}");
            // Opened, or created empty for a key not made yet.
            var state = await tx.DocAsync(HarnessTasks.NestedCallDoc, runtime.TaskId, key, JsonValue.Null).ConfigureAwait(false);
            if (state.Get("taskId") is JsonNumberValue existing)
            {
                var existingId = new TaskId((long)existing.Value);
                var stored = (await tx.GetTaskAsync(existingId).ConfigureAwait(false))?.Input as JsonObjectValue;
                var same = stored is not null && stored.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "nested" } &&
                    stored["call"] is JsonObjectValue made && made["name"] is JsonStringValue { Value: var madeName } && madeName == name &&
                    JsonValue.DeepEquals(made["arguments"], args);
                if (!same) throw new InvalidOperationException($"Nested call {parentCallId}/{key} was already made with another tool or other arguments");
                id = existingId;
                return;
            }
            var input = new RecordJson.Writer()
                .Add("kind", "nested")
                .Add("parent", runtime.TaskId.ToJson())
                .Add("parentCallId", parentCallId)
                .Add("key", key)
                .Add("call", call);
            if (progress == false) input.Add("progress", false);
            var created = await tx.CreateTaskAsync(HarnessTasks.ToolTask, input.Build(),
                new TaskOptions(TaskOwnership.Task(runtime.TaskId)) { AbandonOnRestart = abandonOnRestart }).ConfigureAwait(false);
            state.Set("taskId", created.ToJson());
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            if (!live.ContainsKey("nestedTools")) live.Set("nestedTools", JsonArrayValue.Empty);
            live.GetArray("nestedTools").Add(new RecordJson.Writer()
                .Add("callId", $"{parentCallId}/{key}")
                .Add("parentCallId", parentCallId)
                .Add("parentTaskId", runtime.TaskId.ToJson())
                .Add("name", name)
                .Add("taskId", created.ToJson())
                .Add("arguments", args)
                .Add("status", "pending")
                .Build());
            id = created;
        }, context).ConfigureAwait(false);
        return id;
    }

    /// <summary>What programs get of a tool's output when it declares no schema: one text item as its string, one image as itself, nothing
    /// as <c>""</c>, and anything else as the content list (upstream <c>outputValue</c>).</summary>
    private static JsonValue OutputValue(JsonArrayValue output)
    {
        if (output.Count == 0) return "";
        if (output.Count > 1) return output;
        var only = output[0];
        return only is JsonObjectValue item && IsText(item) ? item["text"] : only;
    }

    /// <summary>What the caller gets for a nested call without a stored result: one the scheduler faulted or orphaned (upstream
    /// <c>fallbackResult</c>).</summary>
    internal static NestedToolExecutionResult FallbackResult(TaskId taskId, string name, TaskOutcome outcome)
    {
        var message = outcome switch
        {
            FaultedTaskOutcome faulted => $"Tool {name} failed: {faulted.Error.Message}",
            OrphanedTaskOutcome orphaned => $"Tool {name} could not resume: {orphaned.Reason}",
            _ => $"Tool {name} ended without a result",
        };
        var error = ToolResults.HarnessError(WireName.Of(outcome.Status), message);
        return new NestedToolExecutionResult { TaskId = taskId, IsError = true, Diagnostics = error.Diagnostics ?? [] };
    }

    /// <summary>An error result from the slot's durable partial output, details and diagnostics (upstream <c>fromSlot</c>).</summary>
    private static ToolExecutionResult FromSlot(DraftObject? slot, string code, string message)
    {
        var diagnostics = new List<ToolDiagnostic>();
        if (slot is not null && slot.ContainsKey("diagnostics") && slot.Get("diagnostics") is JsonArrayValue stored)
            diagnostics.AddRange(stored.Select(ToolResults.ParseDiagnostic));
        var droppedBytes = Number(slot, "droppedBytes") ?? 0;
        if (droppedBytes > 0) diagnostics.Add(Truncated(Number(slot, "droppedLines") ?? 0, droppedBytes, null));
        diagnostics.Add(ToolResults.Diagnostic(code, message));
        var output = slot is not null && slot.ContainsKey("output") && slot.Get("output") is JsonStringValue { Value: var text } ? text : null;
        return new ToolExecutionResult
        {
            Output = string.IsNullOrEmpty(output) ? JsonValue.Array() : JsonValue.Array(Text(output)),
            IsError = true,
            Details = slot is not null && slot.ContainsKey("details") ? slot.Get("details") : null,
            Diagnostics = [.. diagnostics],
        };
    }

    private static long? Number(DraftObject? slot, string key) =>
        slot is not null && slot.ContainsKey(key) && slot.Get(key) is JsonNumberValue number ? (long)number.Value : null;

    /// <summary>The Harness's truncation diagnostic; <paramref name="retain"/> is unknown when rebuilt from a slot after recovery (upstream
    /// <c>truncated</c>).</summary>
    private static ToolDiagnostic Truncated(long droppedLines, long droppedBytes, ToolOutputRetention? retain)
    {
        var kept = retain is null ? "" : $" to its {(retain == ToolOutputRetention.Head ? "beginning" : "end")}";
        return new ToolDiagnostic(ToolDiagnosticSeverity.Warn, $"Output truncated{kept}: {droppedLines} lines, {droppedBytes} bytes dropped", "truncated");
    }

    /// <summary>How a nested call ended, for its slot: error text, from its diagnostics or else its output, bounded (upstream
    /// <c>summaryOf</c>): <c>{isError, durationMs?, usage?, error?}</c>.</summary>
    private static JsonObjectValue SummaryOf(NestedToolExecutionResult result, JsonArrayValue output)
    {
        var error = result.IsError ? ErrorTextOf(result, output) : "";
        if (error.Length > MaxSummaryErrorChars) error = error[..MaxSummaryErrorChars];
        return new RecordJson.Writer()
            .Add("isError", result.IsError)
            .Number("durationMs", result.DurationMs)
            .Add("usage", result.Usage)
            .Add("error", error.Length == 0 ? null : error)
            .Build();
    }

    /// <summary>The error messages of a nested result, else the text items of its output, joined with newlines (upstream
    /// <c>errorTextOf</c>).</summary>
    private static string ErrorTextOf(NestedToolExecutionResult result, JsonArrayValue output)
    {
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == ToolDiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) return string.Join("\n", errors.Select(diagnostic => diagnostic.Message));
        return string.Join("\n", output.OfType<JsonObjectValue>().Where(IsText).Select(item => item["text"] is JsonStringValue { Value: var text } ? text : ""));
    }

    /// <summary>
    /// Bounds the text of result content (upstream <c>boundContent</c>). When the joined text exceeds the limits, the text items are replaced
    /// by one bounded item at the position of the first (head) or last (tail) text item; other content is kept.
    /// </summary>
    private static (JsonArrayValue Content, long DroppedBytes, long DroppedLines) BoundContent(JsonArrayValue content, OutputLimits limits)
    {
        var texts = content.OfType<JsonObjectValue>().Where(IsText).ToList();
        var bounded = ToolOutput.BoundOutput(string.Concat(texts.Select(item => item["text"] is JsonStringValue { Value: var text } ? text : "")), limits);
        if (bounded.DroppedBytes == 0) return (content, 0, 0);
        var keep = limits.Retain == ToolOutputRetention.Head ? texts[0] : texts[^1];
        var result = new List<JsonValue>();
        foreach (var item in content)
        {
            if (item is not JsonObjectValue block || !IsText(block)) result.Add(item);
            else if (ReferenceEquals(block, keep)) result.Add(block.SetItem("text", bounded.Text));
        }
        return (JsonValue.Array(result), bounded.DroppedBytes, bounded.DroppedLines);
    }

    /// <summary>
    /// Explicit nested call keys are path segments of the call ID, so IDs of different nested calls never collide, and are never plain
    /// positive integers, which default keys use. <c>__proto__</c> stays reserved, as it was when keys indexed a JSON object (upstream
    /// <c>checkKey</c>).
    /// </summary>
    internal static void CheckKey(string key)
    {
        if (key.Length == 0 || key.Contains('/', StringComparison.Ordinal) || key == "__proto__" || IsPositiveInteger(key))
            throw new InvalidOperationException(
                $"Nested call key {Js.Stringify(key, 0)} must be non-empty, without \"/\", not \"__proto__\", and not a positive integer");
    }

    private static bool IsPositiveInteger(string key) => key.Length > 0 && key[0] is >= '1' and <= '9' && key.All(c => c is >= '0' and <= '9');

    private static bool IsText(JsonObjectValue item) => item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" };

    private static JsonObjectValue Text(string text) => JsonValue.Object([
        KeyValuePair.Create("type", (JsonValue)"text"),
        KeyValuePair.Create("text", (JsonValue)text),
    ]);

    private static long Id(JsonValue value) => (long)((JsonNumberValue)value).Value;
}

/// <summary>What a running tool reported through its api: output, the last details and diagnostics, behind one lock (upstream
/// <c>Reported</c>, plus the <c>ended</c> flag).</summary>
internal sealed class ToolReport(OutputBuffer output, OutputLimits limits)
{
    private readonly Lock gate = new();
    private readonly List<ToolDiagnostic> diagnostics = [];
    private JsonValue? details;
    private bool ended;

    public OutputLimits Limits { get; } = limits;

    /// <summary>Rejects a report once the call has settled (upstream <c>assertLive</c>).</summary>
    private void AssertLive(string callId)
    {
        if (ended) throw new InvalidOperationException($"Tool call {callId} has settled");
    }

    /// <summary>Runs <paramref name="action"/> while the call is live, atomically with its end: nothing it registers can start after
    /// <see cref="MarkEnded"/>.</summary>
    public T WhileLive<T>(string callId, Func<T> action)
    {
        lock (gate)
        {
            AssertLive(callId);
            return action();
        }
    }

    public bool Push(string callId, string chunk, ShellOutputSkip? skipped)
    {
        lock (gate)
        {
            AssertLive(callId);
            return output.Push(chunk, skipped);
        }
    }

    public bool Push(string callId, ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped)
    {
        lock (gate)
        {
            AssertLive(callId);
            return output.Push(chunk, skipped);
        }
    }

    /// <summary>The output retained so far and whether earlier output was dropped (upstream <c>retainedOutput()</c>).</summary>
    public RetainedOutput Retained(string callId)
    {
        lock (gate)
        {
            AssertLive(callId);
            var retained = output.Snapshot();
            return new RetainedOutput(retained.Text, retained.DroppedBytes > 0);
        }
    }

    public void Diagnostic(string callId, ToolDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        lock (gate)
        {
            AssertLive(callId);
            diagnostics.Add(diagnostic);
        }
    }

    public void Details(string callId, JsonValue value, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            AssertLive(callId);
            context.ThrowIfCancellationRequested();
            details = value;
        }
    }

    /// <summary>The current snapshot, details, and the diagnostics added after the first <paramref name="from"/>.</summary>
    public (BoundedOutput Snapshot, JsonValue? Details, List<ToolDiagnostic> Added, int Count) Capture(int from)
    {
        lock (gate) return (output.Snapshot(), details, diagnostics.GetRange(from, diagnostics.Count - from), diagnostics.Count);
    }

    /// <summary>The retained output (when <paramref name="retain"/>), the last details and every diagnostic, once the call has ended.</summary>
    public (BoundedOutput? Retained, JsonValue? Details, List<ToolDiagnostic> Diagnostics) Final(bool retain)
    {
        lock (gate) return (retain ? output.Snapshot() : null, details, [.. diagnostics]);
    }

    /// <summary>Ends the call: later reports throw.</summary>
    public void MarkEnded()
    {
        lock (gate) ended = true;
    }

    /// <summary>Ends the call and flushes the output's incomplete trailing character.</summary>
    public void End()
    {
        lock (gate)
        {
            ended = true;
            output.End();
        }
    }
}

/// <summary>
/// The nested calls of one tool invocation (upstream <c>run</c>'s <c>admissions</c>, <c>sequence</c> and <c>madeNestedMemo</c>): default
/// keys by call order, the attempt's <c>madeNestedCalls</c> memo, and every admission it started. Upstream chains each admission on the
/// memo's promise, so the admissions of one synchronous stretch of the tool (such as a <c>Promise.all</c> of calls) enqueue their commits
/// back to back once the memo committed, before the scheduler reacts to any of them. Here the calls a tool makes before <c>Execute</c> first
/// returns its task (its synchronous prefix, on the invoking thread) are held and admitted together when it returns; admissions waiting
/// for the memo are started together, in call order, under one lock, which also orders them before any admission made later.
/// </summary>
internal sealed class NestedCalls(ITaskRuntime runtime, ToolReport reported, string callId, bool resumes)
{
    private readonly Lock gate = new();
    private readonly List<Task> admissions = [];
    private readonly List<Admission> waiting = [];
    private List<Admission>? held;
    private int sequence;
    [ThreadStatic] private static NestedCalls? prefix;
    /// <summary>The attempt's memo commit; null before the first call and after a failed one.</summary>
    private Task? madeNestedMemo;
    private bool memoCommitted;

    public string CallId { get; } = callId;

    /// <summary>Whether the caller is replay-safe: it reattaches to its nested calls and child tasks after a restart (upstream
    /// <c>resumes</c>).</summary>
    public bool Resumes { get; } = resumes;

    /// <summary>One nested call waiting for its admission commit.</summary>
    private sealed class Admission(string name, JsonValue args, string key, bool? progress, DurableContext context)
    {
        public TaskCompletionSource<TaskId> Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name { get; } = name;
        public JsonValue Args { get; } = args;
        public string Key { get; } = key;
        public bool? Progress { get; } = progress;
        public DurableContext Context { get; } = context;
    }

    /// <summary>Admits a nested call (upstream <c>executeTool</c> up to its admission): throws once the call settled, picks the key, and
    /// registers the admission so cleanup waits for it.</summary>
    public (string Key, Task<TaskId> Admission) Admit(string name, JsonValue args, NestedCallOptions? options, DurableContext context) =>
        reported.WhileLive(CallId, () =>
        {
            var key = options?.Key ?? (++sequence).ToString(CultureInfo.InvariantCulture);
            if (options?.Key is not null) ToolPhases.CheckKey(options.Key);
            var admission = new Admission(name, args, key, options?.Progress, context);
            lock (gate)
            {
                admissions.Add(admission.Admitted.Task);
                if (ReferenceEquals(prefix, this))
                {
                    (held ??= []).Add(admission);
                    return (key, admission.Admitted.Task);
                }
            }
            Admit([admission]);
            return (key, admission.Admitted.Task);
        });

    /// <summary>Marks the start of the tool's synchronous prefix on this thread (see the class summary).</summary>
    public void BeginPrefix() => prefix = this;

    /// <summary>Ends the prefix and admits the calls it made, in order.</summary>
    public void EndPrefix()
    {
        prefix = null;
        List<Admission>? calls;
        lock (gate)
        {
            calls = held;
            held = null;
        }
        if (calls is not null) Admit(calls);
    }

    private void Admit(List<Admission> calls)
    {
        Task? memo = null;
        lock (gate)
        {
            if (memoCommitted)
            {
                StartAll(calls);
                return;
            }
            waiting.AddRange(calls);
            // The memo first, once per attempt; a failed one is tried again by the next call.
            if (madeNestedMemo is null) madeNestedMemo = memo = ToolPhases.CommitMadeNestedCallsAsync(runtime, StartWaiting, calls[0].Context);
        }
        memo?.ContinueWith(MemoSettled, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Starts the admissions that waited for the memo, in call order. Runs inside the memo's commit, on the Session line: their commits queue
    /// right behind it, before the scheduler can react to the memo's publication, as upstream's run once the memo resolves. Each admission
    /// checks the memo is committed before it creates anything.
    /// </summary>
    private void StartWaiting()
    {
        lock (gate)
        {
            memoCommitted = true;
            var queued = waiting.ToList();
            waiting.Clear();
            StartAll(queued);
        }
    }

    /// <summary>A memo commit that failed fails the admissions still waiting for it; the next call tries the memo again.</summary>
    private void MemoSettled(Task memo)
    {
        if (memo.IsCompletedSuccessfully) return;
        lock (gate)
        {
            memoCommitted = false;
            madeNestedMemo = null;
            var queued = waiting.ToList();
            waiting.Clear();
            var error = memo.Exception?.InnerException ?? new OperationCanceledException();
            foreach (var admission in queued) admission.Admitted.TrySetException(error);
        }
    }

    /// <summary>
    /// Enqueues the admissions' commits (upstream <c>startNestedCall</c>), in call order, back to back on the Session line
    /// (<see cref="SessionLine.Together"/>): an idle line would otherwise run the first at once and let the scheduler's reaction to it in
    /// before the next, and a job another thread enqueues meanwhile (a reservation pass the thread pool started, say) could queue between
    /// them, so the second call's documents were created a commit later than upstream's. Called under the lock.
    /// </summary>
    private void StartAll(List<Admission> calls)
    {
        if (calls.Count == 0) return;
        SessionLine.Together(() =>
        {
            foreach (var admission in calls) Start(admission);
        });
    }

    private void Start(Admission admission)
    {
        Task<TaskId> started;
        try
        {
            started = ToolPhases.StartNestedCallAsync(runtime, CallId, admission.Name, admission.Args, admission.Key, admission.Progress,
                abandonOnRestart: !Resumes, admission.Context);
        }
        catch (Exception error)
        {
            started = Task.FromException<TaskId>(error);
        }
        started.ContinueWith(static (task, state) =>
        {
            var admitted = ((Admission)state!).Admitted;
            if (task.IsCompletedSuccessfully) admitted.TrySetResult(task.GetAwaiter().GetResult());
            else if (task.IsCanceled) admitted.TrySetCanceled();
            else admitted.TrySetException(task.Exception!.InnerExceptions);
        }, admission, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits for every admission started, whatever its outcome (upstream <c>Promise.allSettled(admissions)</c>).</summary>
    public async Task AdmissionsSettledAsync()
    {
        Task[] started;
        lock (gate) started = [.. admissions];
        foreach (var admission in started)
            await admission.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .ConfigureAwait(false);
    }
}

/// <summary>The tool execution api of one call (upstream's <c>api</c> object built by <c>run</c>).</summary>
internal sealed class ToolExecutionApi : IToolExecutionApi
{
    private readonly ITaskRuntime runtime;
    private readonly ToolReport reported;
    private readonly Progress progress;
    private readonly NestedCalls nested;

    public ToolExecutionApi(ITaskRuntime runtime, string callId, ToolReport reported, Progress progress, IExecutionEnv? env, NestedCalls nested)
    {
        this.runtime = runtime;
        this.reported = reported;
        this.progress = progress;
        this.nested = nested;
        CallId = callId;
        Env = env;
        Registry = runtime.Registry;
        OutputWindow = reported.Limits.Retain == ToolOutputRetention.Tail
            ? new ShellOutputWindow(reported.Limits.MaxBytes, reported.Limits.MaxLines, runtime.Settings.Progress.OutputIntervalMs,
                ToolOutput.ProgressBytesPerSecond)
            : null;
    }

    public TaskId TaskId => runtime.TaskId;
    public ConversationId ConversationId => runtime.ConversationId;
    public string CallId { get; }
    public IRegistrySnapshot Registry { get; }
    public IDurableModels Models => runtime.Models;
    public IExecutionEnv? Env { get; }
    public ShellOutputWindow? OutputWindow { get; }

    public Task<Agent> AgentAsync(DurableContext context) => runtime.AgentAsync(context);

    public void Output(string chunk, ShellOutputSkip? skipped = null)
    {
        if (reported.Push(CallId, chunk, skipped)) progress.Mark();
    }

    public void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null)
    {
        if (reported.Push(CallId, chunk, skipped)) progress.Mark();
    }

    public RetainedOutput RetainedOutput() => reported.Retained(CallId);

    public void Diagnostic(ToolDiagnostic diagnostic)
    {
        reported.Diagnostic(CallId, diagnostic);
        progress.Mark();
    }

    public Task DetailsAsync(JsonValue value, DurableContext context)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(context);
            reported.Details(CallId, value, context);
            var committed = progress.MarkAndWait();
            // Cancelling the wait leaves the update in place; the commit's own outcome stays observed.
            _ = committed.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return context.WaitAsync(committed);
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    public async Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        T result = default!;
        await runtime.CommitAsync(async (tx, _) => { result = await change(tx).ConfigureAwait(false); }, context).ConfigureAwait(false);
        return result;
    }

    public Task CommitAsync(Func<ITx, Task> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return runtime.CommitAsync(async (tx, _) => await change(tx).ConfigureAwait(false), context);
    }

    public Task<JsonValue?> MemoAsync(string name, DurableContext context) => runtime.MemoAsync(name, context);
    public Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context) => runtime.MemoAsync(name, candidate, context);

    /// <summary>
    /// Pi v1.2.0's default for <c>api.createTask()</c> (tool.ts): a child task of a tool that is not replay-safe gets
    /// <see cref="TaskOptions.AbandonOnRestart"/>, as such a caller never resumes after a restart; an explicit value wins (upstream
    /// <c>{ abandonOnRestart: true, ...options }</c>).
    /// </summary>
    internal static TaskOptions ChildTaskOptions(TaskOptions options, bool resumes) =>
        options.Ownership.TaskId is not null && !resumes && options.AbandonOnRestart is null ? options with { AbandonOnRestart = true } : options;

    public async Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        var child = ChildTaskOptions(options, nested.Resumes);
        TaskId id = default;
        await runtime.CommitAsync(async (tx, _) => { id = await tx.CreateTaskAsync(task, input, child).ConfigureAwait(false); }, context)
            .ConfigureAwait(false);
        return id;
    }

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => runtime.GetTaskAsync(id, context);
    public Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context) => runtime.WaitForTaskAsync(id, context);
    public Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context) => runtime.ConversationAsync(id, context);

    /// <summary>
    /// Runs <paramref name="calls"/>, which makes nested calls of <paramref name="api"/>'s tool call on this thread, and admits those calls
    /// together, as the calls a tool makes in one synchronous stretch: upstream's <c>executeTool()</c> calls of one stretch chain on the
    /// attempt's memo promise, so their admission commits enqueue back to back, in call order, before the scheduler reacts to any of them.
    /// For a tool whose calls come after an await, such as code mode launching the calls its journal commit covered (NestedCalls admits
    /// such calls one at a time otherwise). An <see cref="IToolExecutionApi"/> that is not the Harness's own just runs
    /// <paramref name="calls"/>.
    /// </summary>
    internal static void AdmitTogether(IToolExecutionApi api, Action calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (api is not ToolExecutionApi own)
        {
            calls();
            return;
        }
        own.nested.BeginPrefix();
        try
        {
            calls();
        }
        finally
        {
            own.nested.EndPrefix();
        }
    }

    public Task<NestedToolExecutionResult> ExecuteToolAsync(string name, JsonObjectValue arguments, DurableContext context,
        NestedCallOptions? options = null) => ExecuteToolValueAsync(name, arguments, context, options);

    /// <summary>
    /// <see cref="IToolExecutionApi.ExecuteToolAsync"/> with arguments of any JSON kind, as upstream's <c>executeTool()</c> passes whatever
    /// its caller gives: a code mode script's <c>tools.x("text")</c> reaches the nested call as is, where the tool's <c>prepareArguments</c>
    /// may repair it and validation reports what is not an object. An <see cref="IToolExecutionApi"/> that is not the Harness's own takes
    /// only objects.
    /// </summary>
    internal static Task<NestedToolExecutionResult> ExecuteToolAsync(IToolExecutionApi api, string name, JsonValue arguments, DurableContext context,
        NestedCallOptions? options = null) => api switch
        {
            ToolExecutionApi own => own.ExecuteToolValueAsync(name, arguments, context, options),
            _ when arguments is JsonObjectValue keyed => api.ExecuteToolAsync(name, keyed, context, options),
            _ => Task.FromException<NestedToolExecutionResult>(new ArgumentException("Nested call arguments must be a JSON object", nameof(arguments))),
        };

    private async Task<NestedToolExecutionResult> ExecuteToolValueAsync(string name, JsonValue arguments, DurableContext context,
        NestedCallOptions? options)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var (key, admission) = nested.Admit(name, arguments, options, context);
        var id = await admission.ConfigureAwait(false);
        var settled = await runtime.WaitForTaskAsync(id, context).ConfigureAwait(false);
        var stored = await runtime.SnapshotAsync(HarnessTasks.NestedCallDoc, runtime.TaskId, key, context).ConfigureAwait(false);
        // A nested call the scheduler faulted or orphaned stored no result.
        return stored is not null && stored.TryGetValue("result", out var result)
            ? NestedToolExecutionResult.FromJson(result)
            : ToolPhases.FallbackResult(id, name, ((SettlingTaskState)settled.State).Outcome);
    }

    public Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => runtime.SnapshotAsync(token, context);
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        runtime.SnapshotAsync(token, conversationId, context);
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => runtime.SnapshotAsync(token, taskId, context);
    public Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => runtime.SnapshotAsync(token, key, context);
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
        runtime.SnapshotAsync(token, conversationId, key, context);
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        runtime.SnapshotAsync(token, taskId, key, context);
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context) =>
        runtime.SnapshotAsOfAsync(token, conversationId, at, context);
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
        DurableContext context) => runtime.SnapshotAsOfAsync(token, conversationId, key, at, context);

    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocToken token, DurableContext context) => runtime.WatchDocAsync(token, context);
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        runtime.WatchDocAsync(token, conversationId, context);
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        runtime.WatchDocAsync(token, taskId, context);
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        runtime.WatchDocAsync(token, key, context);
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context) => runtime.WatchDocAsync(token, conversationId, key, context);
    public Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        runtime.WatchDocAsync(token, taskId, key, context);
}

/// <summary>
/// A tool execution api that forwards every member to another (upstream's <c>{ ...api, output, outputWindow }</c>). A tool wrapper derives
/// from it and overrides what it changes; one that replaces <see cref="Output(string, ShellOutputSkip?)"/> and transforms text must also
/// return null from <see cref="OutputWindow"/>.
/// </summary>
public class DelegatingToolExecutionApi(IToolExecutionApi inner) : IToolExecutionApi
{
    /// <summary>The api this one forwards to.</summary>
    protected IToolExecutionApi Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public virtual TaskId TaskId => Inner.TaskId;
    /// <inheritdoc/>
    public virtual ConversationId ConversationId => Inner.ConversationId;
    /// <inheritdoc/>
    public virtual string CallId => Inner.CallId;
    /// <inheritdoc/>
    public virtual IRegistrySnapshot Registry => Inner.Registry;
    /// <inheritdoc/>
    public virtual IDurableModels Models => Inner.Models;
    /// <inheritdoc/>
    public virtual IExecutionEnv? Env => Inner.Env;
    /// <inheritdoc/>
    public virtual ShellOutputWindow? OutputWindow => Inner.OutputWindow;
    /// <inheritdoc/>
    public virtual RetainedOutput RetainedOutput() => Inner.RetainedOutput();
    /// <inheritdoc/>
    public virtual Task<Agent> AgentAsync(DurableContext context) => Inner.AgentAsync(context);
    /// <inheritdoc/>
    public virtual void Output(string chunk, ShellOutputSkip? skipped = null) => Inner.Output(chunk, skipped);
    /// <inheritdoc/>
    public virtual void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null) => Inner.Output(chunk, skipped);
    /// <inheritdoc/>
    public virtual void Diagnostic(ToolDiagnostic diagnostic) => Inner.Diagnostic(diagnostic);
    /// <inheritdoc/>
    public virtual Task DetailsAsync(JsonValue value, DurableContext context) => Inner.DetailsAsync(value, context);
    /// <inheritdoc/>
    public virtual Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context) => Inner.CommitAsync(change, context);
    /// <inheritdoc/>
    public virtual Task CommitAsync(Func<ITx, Task> change, DurableContext context) => Inner.CommitAsync(change, context);
    /// <inheritdoc/>
    public virtual Task<JsonValue?> MemoAsync(string name, DurableContext context) => Inner.MemoAsync(name, context);
    /// <inheritdoc/>
    public virtual Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context) => Inner.MemoAsync(name, candidate, context);
    /// <inheritdoc/>
    public virtual Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context) =>
        Inner.CreateTaskAsync(task, input, options, context);
    /// <inheritdoc/>
    public virtual Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Inner.GetTaskAsync(id, context);
    /// <inheritdoc/>
    public virtual Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context) => Inner.WaitForTaskAsync(id, context);
    /// <inheritdoc/>
    public virtual Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context) => Inner.ConversationAsync(id, context);
    /// <inheritdoc/>
    public virtual Task<NestedToolExecutionResult> ExecuteToolAsync(string name, JsonObjectValue arguments, DurableContext context,
        NestedCallOptions? options = null) => Inner.ExecuteToolAsync(name, arguments, context, options);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => Inner.SnapshotAsync(token, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        Inner.SnapshotAsync(token, conversationId, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => Inner.SnapshotAsync(token, taskId, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => Inner.SnapshotAsync(token, key, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
        Inner.SnapshotAsync(token, conversationId, key, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        Inner.SnapshotAsync(token, taskId, key, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at,
        DurableContext context) => Inner.SnapshotAsOfAsync(token, conversationId, at, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key,
        EntryId at, DurableContext context) => Inner.SnapshotAsOfAsync(token, conversationId, key, at, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocToken token, DurableContext context) => Inner.WatchDocAsync(token, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        Inner.WatchDocAsync(token, conversationId, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        Inner.WatchDocAsync(token, taskId, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        Inner.WatchDocAsync(token, key, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context) => Inner.WatchDocAsync(token, conversationId, key, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        Inner.WatchDocAsync(token, taskId, key, context);
}

/// <summary>Tool result helpers (upstream <c>tool.ts</c>).</summary>
internal static class ToolResults
{
    /// <summary>An error result the Harness writes itself: no content and one <c>error</c> diagnostic with <paramref name="code"/> (upstream
    /// <c>harnessError</c>).</summary>
    public static ToolExecutionResult HarnessError(string code, string message) => new()
    {
        Output = JsonValue.Array(),
        IsError = true,
        Diagnostics = [Diagnostic(code, message)],
    };

    /// <summary>An <c>error</c> diagnostic (upstream <c>toolDiagnostic</c>).</summary>
    public static ToolDiagnostic Diagnostic(string code, string message) => new(ToolDiagnosticSeverity.Error, message, code);

    /// <summary>
    /// Appends a <c>pi.tool-result</c> entry (upstream <c>appendToolResult</c>). The content ends with the rendered diagnostics, so the
    /// stored message is exactly what the model sees; <c>data</c> keeps the structured list. A result's usage is added to <c>pi.usage</c> in
    /// the same commit. <paramref name="call"/> is the pi-ai <c>ToolCall</c>.
    /// </summary>
    public static async Task<TypedEntry<JsonObjectValue>> AppendToolResultAsync(ITx tx, ConversationId conversationId, JsonObjectValue call,
        ToolExecutionResult result, double timestamp, double? durationMs = null)
    {
        var diagnostics = (result.Diagnostics ?? []).Select(DiagnosticJson).ToList();
        var content = (result.Output ?? JsonArrayValue.Empty).ToList();
        if (diagnostics.Count > 0)
            content.Add(new RecordJson.Writer().Add("type", "text").Add("text", RenderDiagnostics(result.Diagnostics!.Value)).Build());
        var name = ((JsonStringValue)call["name"]).Value;
        var message = new RecordJson.Writer()
            .Add("role", "toolResult")
            .Add("toolCallId", call["id"])
            .Add("toolName", name)
            .Add("content", JsonValue.Array(content))
            .Add("details", result.Details)
            .Add("usage", result.Usage)
            .Add("isError", result.IsError ?? false)
            .Number("durationMs", durationMs)
            .Number("timestamp", timestamp)
            .Build();
        if (result.Usage is { } usage) await UsageLedger.RecordUsageAsync(tx, conversationId, "tools", name, usage).ConfigureAwait(false);
        var data = JsonValue.Object([KeyValuePair.Create("diagnostics", (JsonValue)JsonValue.Array(diagnostics))]);
        return await tx.AppendEntryAsync(Entries.ToolResultEntry, conversationId,
            new TypedEntryDraft<JsonObjectValue> { Model = JsonValue.Array(message), Data = data }).ConfigureAwait(false);
    }

    /// <summary>A diagnostic's JSON in upstream <c>toolDiagnostic</c>'s key order: <c>{severity, code?, message}</c>.</summary>
    public static JsonValue DiagnosticJson(ToolDiagnostic diagnostic) => new RecordJson.Writer()
        .Add("severity", WireName.Of(diagnostic.Severity)).Add("code", diagnostic.Code).Add("message", diagnostic.Message).Build();

    /// <summary>Reads a diagnostic a slot stored as <see cref="DiagnosticJson"/>.</summary>
    public static ToolDiagnostic ParseDiagnostic(JsonValue json)
    {
        var value = (JsonObjectValue)json;
        return new ToolDiagnostic(
            WireName.Parse<ToolDiagnosticSeverity>(((JsonStringValue)value["severity"]).Value, "tool diagnostic"),
            ((JsonStringValue)value["message"]).Value,
            value.TryGetValue("code", out var code) && code is JsonStringValue text ? text.Value : null);
    }

    /// <summary>A control's JSON: <c>{addTools?, terminate?, handoff?}</c>, an unset field omitted.</summary>
    public static JsonObjectValue ControlJson(ToolControl control)
    {
        var writer = new RecordJson.Writer();
        if (control.AddTools is { } names)
            writer.Add("addTools", JsonValue.Array(names.Select(name => (JsonValue)JsonValue.String(name ??
                throw new ArgumentException("Tool control addTools contains null and is not strict JSON")))));
        if (control.Terminate) writer.Add("terminate", true);
        return writer.Add("handoff", control.Handoff).Build();
    }

    private static string RenderDiagnostics(IEnumerable<ToolDiagnostic> diagnostics) =>
        $"<harness>\n{string.Join("\n", diagnostics.Select(diagnostic => $"[{WireName.Of(diagnostic.Severity)}] {diagnostic.Message}"))}\n</harness>";
}
