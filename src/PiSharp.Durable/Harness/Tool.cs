// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/tool.ts.
//
// Mappings:
// - Input, checkpoints and results are upstream's JSON in its key order: input {assistant, callId}; checkpoints {phase: "call"} and
//   {phase: "execute", arguments, replay}; result {entryId, control?}, where control is {addTools?, terminate?, handoff?}.
// - Calls are pi-ai ToolCall wire JSON. Arguments are validated by Internal/PiAi/Validation.cs, an internal copy of PiSharp.Agent's
//   ToolArgumentValidation (the agent's port of pi-ai's validateToolArguments, packages/ai/src/utils/validation.ts; the core does not
//   reference PiSharp.Agent), with the tool's ParametersOrigin (default TypeBox, as upstream tools declare TypeBox schemas).
// - `runtime.signal.aborted` is ITaskRuntime.Signal; an abort reaches a running tool as its context's cancellation, so the tool rejects
//   with an OperationCanceledException (upstream's AbortError) or the reason.
// - The tool reports from any thread; what it reported (output buffer, diagnostics, details, the ended flag) sits behind one lock, and the
//   progress commits run on the Harness TimeProvider's timers (output.ts).
// - performance.now() is a Stopwatch; Math.round is round half up.
// - A ToolExecutionResult field left null is upstream's undefined: Content null takes the retained output, Details null the last running
//   details; JsonValue.Null is an explicit null.
// - Upstream's copyJson of a control drops undefined keys; a C# ToolControl has none, and an unset Terminate is omitted.
using System.Collections.Immutable;
using System.Diagnostics;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the tool task (upstream <c>ToolTask</c>, kind <c>pi.tool</c>, version 1).</summary>
public static partial class HarnessTasks
{
    /// <summary>
    /// Built-in tool task (upstream <c>ToolTask</c>): resolves the called tool among its phase agent's tools, validates, runs
    /// <c>beforeTool</c>, records intent, executes, runs <c>afterTool</c>, and appends the result, all in one <c>call</c> handler so nothing
    /// separates resolution from settlement. <c>execute</c> is reached only by recovery and applies the replay rule: a call reruns only when
    /// both its recorded and its current registration say <see cref="ToolReplay.Safe"/>; otherwise it settles <c>failed</c> with an
    /// <c>interrupted</c> result. Its input is <c>{assistant, callId}</c> and its result <c>{entryId, control?}</c>.
    /// </summary>
    public static DurableTask ToolTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.tool",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"call"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
        {
            ["call"] = ToolPhases.CallAsync,
            ["execute"] = ToolPhases.ExecuteAsync,
        },
        Abort = ToolPhases.AbortAsync,
    });
}

/// <summary>The tool task's phases and helpers (upstream <c>tool.ts</c>).</summary>
internal static class ToolPhases
{
    /// <summary>How a tool task ends; the result entry is appended either way. <c>failed</c> (execution threw or was interrupted) records
    /// cancellation intent for the conversations the call owns; a result with <c>IsError</c> still completes (upstream <c>Ending</c>).</summary>
    private sealed record Ending(TaskOutcomeStatus Status, string? Message = null)
    {
        public static Ending Completed { get; } = new(TaskOutcomeStatus.Completed);
        public static Ending Aborted { get; } = new(TaskOutcomeStatus.Aborted);
        public static Ending Failed(string message) => new(TaskOutcomeStatus.Failed, message);
    }

    /// <summary>Arguments, or why they are invalid (upstream <c>Checked</c>).</summary>
    private readonly record struct Checked(JsonValue? Args, string? Error);

    // ─── Phases ─────────────────────────────────────────────────────────────

    public static async Task CallAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var call = await ReadCallAsync(runtime, task.Input, context).ConfigureAwait(false);
        var name = Name(call);
        var tool = (await runtime.AgentAsync(context).ConfigureAwait(false)).Tools.FirstOrDefault(each => each.Name == name);
        if (tool is null)
        {
            var unavailable = ToolResults.HarnessError("tool_unavailable", $"Tool {name} is not available");
            await SettleAsync(runtime, call, Ending.Completed, _ => unavailable, context).ConfigureAwait(false);
            return;
        }
        var prepared = Prepare(tool, call["arguments"]);
        var checkedArgs = prepared.Error is null ? Validate(tool, prepared.Args!) : prepared;
        if (checkedArgs.Error is { } invalid)
        {
            await SettleAsync(runtime, call, Ending.Completed, _ => Invalid(invalid), context).ConfigureAwait(false);
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
                    block = ErrorText(error);
                }
            }).ConfigureAwait(false);
        if (block is not null)
        {
            var blocked = ToolResults.HarnessError("blocked", $"Tool call blocked: {block}");
            await SettleAsync(runtime, call, Ending.Completed, _ => blocked, context).ConfigureAwait(false);
            return;
        }
        var validated = Validate(tool, args);
        if (validated.Error is { } rejected)
        {
            await SettleAsync(runtime, call, Ending.Completed, _ => Invalid(rejected), context).ConfigureAwait(false);
            return;
        }
        var final = validated.Args!;
        await runtime.CommitAsync(async (tx, _) =>
        {
            if (Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId) is { } slot)
                slot.Set("status", "running");
            var intent = new RecordJson.Writer()
                .Add("phase", "execute")
                .Add("arguments", final)
                .Add("replay", WireName.Of(tool.Replay ?? ToolReplay.Unsafe))
                .Build();
            return (TaskState?)NextTaskState.Running(intent);
        }, context).ConfigureAwait(false);
        await RunAsync(runtime, call, tool, final, context).ConfigureAwait(false);
    }

    /// <summary>Recovery after intent: rerun only when the stored and the current policy both say <c>safe</c>.</summary>
    public static async Task ExecuteAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var args = checkpoint["arguments"];
        var replay = checkpoint["replay"] is JsonStringValue { Value: var stored } ? stored : null;
        var call = await ReadCallAsync(runtime, task.Input, context).ConfigureAwait(false);
        var name = Name(call);
        var tool = (await runtime.AgentAsync(context).ConfigureAwait(false)).Tools.FirstOrDefault(each => each.Name == name);
        if (replay == "safe" && tool?.Replay == ToolReplay.Safe)
        {
            // The rerun reports from scratch; clear what the interrupted attempt published.
            await runtime.CommitAsync(async (tx, _) =>
            {
                if (Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId) is { } slot)
                    Live.ClearProgress(slot);
            }, context).ConfigureAwait(false);
            await RunAsync(runtime, call, tool, args, context).ConfigureAwait(false);
            return;
        }
        var message = $"Tool {name} was interrupted and may have partially run";
        // `failed` records cancellation intent, so the call's owned conversations, left unsupervised, are aborted.
        await SettleAsync(runtime, call, Ending.Failed(message), slot => FromSlot(slot, "interrupted", message), context).ConfigureAwait(false);
    }

    public static async Task AbortAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var call = await ReadCallAsync(runtime, task.Input, context).ConfigureAwait(false);
        var message = $"Tool {Name(call)} was aborted";
        await SettleAsync(runtime, call, Ending.Aborted, slot => FromSlot(slot, "aborted", message), context).ConfigureAwait(false);
    }

    // ─── Resolution and validation ──────────────────────────────────────────

    /// <summary>The tool call <c>callId</c> of the assistant entry (upstream <c>readCall</c>).</summary>
    private static async Task<JsonObjectValue> ReadCallAsync(ITaskRuntime runtime, JsonValue input, DurableContext context)
    {
        var fields = (JsonObjectValue)input;
        var assistant = (long)((JsonNumberValue)fields["assistant"]).Value;
        var callId = ((JsonStringValue)fields["callId"]).Value;
        var entry = await runtime.EntryAsync(Entries.AssistantEntry, new EntryId(assistant), context).ConfigureAwait(false);
        var message = entry?.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
        var call = message is not null && message.TryGetValue("role", out var role) && role is JsonStringValue { Value: "assistant" } &&
            message.TryGetValue("content", out var content) && content is JsonArrayValue blocks
            ? blocks.OfType<JsonObjectValue>().FirstOrDefault(block =>
                block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "toolCall" } &&
                block.TryGetValue("id", out var id) && id is JsonStringValue { Value: var value } && value == callId)
            : null;
        return call ?? throw new InvalidOperationException($"Entry {assistant} has no tool call {callId}");
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
            return new Checked(null, ErrorText(error));
        }
    }

    /// <summary>Arguments validated and coerced against the implementation's schema (upstream <c>validate</c>).</summary>
    private static Checked Validate(ToolRegistration tool, JsonValue args)
    {
        try
        {
            var result = Internal.PiAi.Validation.ToolArgumentValidation.ValidateJson(tool.Name, tool.Parameters.ToJson(), args.ToJson(),
                tool.ParametersOrigin);
            if (!result.IsValid) return new Checked(null, result.ErrorMessage);
            return new Checked(result.ArgumentsJson is { } json ? JsonValue.Parse(json) : JsonValue.Null, null);
        }
        catch (Exception error)
        {
            return new Checked(null, ErrorText(error));
        }
    }

    private static ToolExecutionResult Invalid(string message) => ToolResults.HarnessError("invalid_arguments", message);

    // ─── Execution ──────────────────────────────────────────────────────────

    /// <summary>Executes with the resolved implementation, then settles its result (upstream <c>run</c>).</summary>
    private static async Task RunAsync(ITaskRuntime runtime, JsonObjectValue call, ToolRegistration tool, JsonValue args, DurableContext context)
    {
        var limits = new OutputLimits(
            tool.OutputLimits?.MaxBytes ?? Truncation.DefaultMaxBytes,
            tool.OutputLimits?.MaxLines ?? Truncation.DefaultMaxLines,
            tool.OutputLimits?.Retain ?? ToolOutputRetention.Head);
        var reported = new ToolReport(new OutputBuffer(limits), limits);
        var progress = PublishProgress(runtime, reported, context);
        ToolExecutionResult result;
        var ending = Ending.Completed;
        // Execution time of this attempt; a rerun after recovery measures only itself.
        double? durationMs = null;
        try
        {
            // Built for this call, so a rerun after recovery gets the conversation's environment at that time.
            var env = await runtime.EnvAsync(context).ConfigureAwait(false);
            var api = new ToolExecutionApi(runtime, ((JsonStringValue)call["id"]).Value, reported, progress, env);
            var started = Stopwatch.GetTimestamp();
            try
            {
                result = await tool.Execute(args, api, context).ConfigureAwait(false);
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
            result = new ToolExecutionResult { IsError = true, Diagnostics = [ToolResults.Diagnostic("tool_error", ErrorText(error))] };
            // A throw, from Execute or from building the environment, ends the task `failed`, which cancels what the call owned; it no longer
            // supervises it. The error text is already in the result entry.
            ending = Ending.Failed($"Tool {Name(call)} threw");
        }
        reported.End();
        // Details still waiting for a progress commit settle with the terminal commit, the final flush.
        var pending = await progress.StopAsync().ConfigureAwait(false);
        try
        {
            var settled = await FinalResultAsync(runtime, call, result, reported, context).ConfigureAwait(false);
            await SettleAsync(runtime, call, ending, _ => settled, context, durationMs).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            foreach (var waiter in pending) waiter.TrySetException(error);
            throw;
        }
        foreach (var waiter in pending) waiter.TrySetResult();
    }

    /// <summary>Throttled commits of what the tool reported into its <c>pi.live.tools</c> slot, each writing only what changed since the last
    /// one (upstream <c>publishProgress</c>).</summary>
    private static Progress PublishProgress(ITaskRuntime runtime, ToolReport reported, DurableContext context)
    {
        var written = (Text: "", Details: (JsonValue?)null, Diagnostics: 0);
        return new Progress(() =>
        {
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
    /// through the api, <c>afterTool</c> applied, and explicit text bounded, with the Harness's truncation diagnostic last (upstream
    /// <c>finalResult</c>).
    /// </summary>
    private static async Task<ToolExecutionResult> FinalResultAsync(ITaskRuntime runtime, JsonObjectValue call, ToolExecutionResult result,
        ToolReport reported, DurableContext context)
    {
        var harness = new List<ToolDiagnostic>();
        var (retained, details, diagnostics) = reported.Final(result.Content is null);
        var content = retained is not { } kept ? result.Content! : kept.Text.Length == 0 ? JsonValue.Array() : JsonValue.Array(Text(kept.Text));
        var final = result with
        {
            Content = content,
            Details = result.Details ?? details,
            Diagnostics = [.. diagnostics, .. result.Diagnostics ?? []],
        };
        await runtime.Hooks.EachAsync<ToolHooks, Func<JsonObjectValue, ToolExecutionResult, IHookApi, DurableContext, Task<ToolExecutionResult?>>>(
            hooks => hooks.AfterTool,
            async hook => final = await hook(call, final, runtime, context).ConfigureAwait(false) ?? final).ConfigureAwait(false);
        // The retained output's truncation applies only while afterTool kept that content.
        if (retained is { DroppedBytes: > 0 } output && ReferenceEquals(final.Content, content))
            harness.Add(Truncated(output.DroppedLines, output.DroppedBytes, reported.Limits.Retain));
        var bounded = BoundContent(final.Content ?? JsonValue.Array(), reported.Limits);
        if (bounded.DroppedBytes > 0) harness.Add(Truncated(bounded.DroppedLines, bounded.DroppedBytes, reported.Limits.Retain));
        return final with { Content = bounded.Content, Diagnostics = [.. final.Diagnostics ?? [], .. harness] };
    }

    /// <summary>
    /// Commits the tool's terminal state: appends its result entry, marks its slot done, and completes or ends aborted with the entry ID
    /// (upstream <c>settle</c>). <paramref name="build"/> receives the slot so interruption and abort can report the durable partial output.
    /// </summary>
    private static Task SettleAsync(ITaskRuntime runtime, JsonObjectValue call, Ending ending, Func<DraftObject?, ToolExecutionResult> build,
        DurableContext context, double? durationMs = null) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            var slot = Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId);
            var result = build(slot);
            var entry = await ToolResults.AppendToolResultAsync(tx, runtime.ConversationId, call, result, runtime.Now(), durationMs).ConfigureAwait(false);
            if (slot is not null) Live.FinishSlot(slot, entry.Id);
            var entryId = KeyValuePair.Create("entryId", (JsonValue)entry.Id.ToJson());
            if (ending.Status == TaskOutcomeStatus.Aborted)
                return (TaskState?)NextTaskState.Terminal(new AbortedTaskOutcome(null, JsonValue.Object([entryId])));
            if (ending.Status == TaskOutcomeStatus.Failed)
                return NextTaskState.Terminal(new FailedTaskOutcome(new TaskOutcomeError(ending.Message!), JsonValue.Object([entryId])));
            // Tools build control objects freely; the task result is strict JSON.
            JsonObjectValue completed = result.Control is null
                ? JsonValue.Object([entryId])
                : JsonValue.Object([entryId, KeyValuePair.Create("control", (JsonValue)ToolResults.ControlJson(result.Control))]);
            return NextTaskState.Completed(completed);
        }, context);

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
            Content = string.IsNullOrEmpty(output) ? JsonValue.Array() : JsonValue.Array(Text(output)),
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

    private static bool IsText(JsonObjectValue item) => item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" };

    private static JsonObjectValue Text(string text) => JsonValue.Object([
        KeyValuePair.Create("type", (JsonValue)"text"),
        KeyValuePair.Create("text", (JsonValue)text),
    ]);

    internal static string ErrorText(Exception error) => error.Message;
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

/// <summary>The tool execution api of one call (upstream's <c>api</c> object built by <c>run</c>).</summary>
internal sealed class ToolExecutionApi : IToolExecutionApi
{
    private readonly ITaskRuntime runtime;
    private readonly ToolReport reported;
    private readonly Progress progress;

    public ToolExecutionApi(ITaskRuntime runtime, string callId, ToolReport reported, Progress progress, IExecutionEnv? env)
    {
        this.runtime = runtime;
        this.reported = reported;
        this.progress = progress;
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
    /// <see cref="TaskOptions.AbandonOnRestart"/>, as such a caller never resumes after a restart; an explicit value wins. Durable work list
    /// area c (the scheduler) added the option and this hook; area a (tool task v2) applies it in <see cref="CreateTaskAsync"/>, with
    /// <c>resumes</c> = the tool's <see cref="ToolReplay.Safe"/>, and gives nested calls the same default.
    /// </summary>
    internal static TaskOptions ChildTaskOptions(TaskOptions options, bool resumes) =>
        options.Ownership.TaskId is not null && !resumes && options.AbandonOnRestart is null ? options with { AbandonOnRestart = true } : options;

    public async Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context)
    {
        // Area a: options = ChildTaskOptions(options, resumes) here (see ChildTaskOptions).
        TaskId id = default;
        await runtime.CommitAsync(async (tx, _) => { id = await tx.CreateTaskAsync(task, input, options).ConfigureAwait(false); }, context)
            .ConfigureAwait(false);
        return id;
    }

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => runtime.GetTaskAsync(id, context);
    public Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context) => runtime.WaitForTaskAsync(id, context);
    public Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context) => runtime.ConversationAsync(id, context);

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
        Content = JsonValue.Array(),
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
        var content = (result.Content ?? JsonArrayValue.Empty).ToList();
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
