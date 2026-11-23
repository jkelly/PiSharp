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

internal static partial class ToolPhases
{
    // ─── Execution ──────────────────────────────────────────────────────────

    /// <summary>What a run of <c>Execute</c> starts from after a park: why it woke, and what the call last parked with (upstream
    /// <c>Resumed</c>).</summary>
    internal readonly record struct Resumed(WakeReason? Wake, JsonValue? Checkpoint);

    /// <summary>One run of a tool's code (<c>Execute</c> or <c>Cancel</c>): its api, what it reported, and its progress commits (upstream
    /// <c>Attempt</c>).</summary>
    private sealed class Attempt(ToolExecutionApi api, ToolReport reported, Progress progress, NestedCalls nested)
    {
        public ToolExecutionApi Api { get; } = api;
        public ToolReport Reported { get; } = reported;
        public Progress Progress { get; } = progress;
        public NestedCalls Nested { get; } = nested;

        /// <summary>Ends the attempt: lets admitted nested calls commit, ends the output, and stops progress; returns its waiters.</summary>
        public async Task<IReadOnlyList<TaskCompletionSource>> EndAsync()
        {
            Reported.MarkEnded();
            // No admission starts after the attempt ended; let those underway commit, so settlement aborts and lists their calls.
            await Nested.AdmissionsSettledAsync().ConfigureAwait(false);
            Reported.End();
            return await Progress.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Executes with the resolved implementation, then settles its result or parks the call (upstream <c>run</c>).</summary>
    private static async Task RunAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, ToolRegistration tool, JsonValue args,
        Resumed resumed, DurableContext context)
    {
        var attempt = await StartAttemptAsync(runtime, input, call, tool, resumed, context).ConfigureAwait(false);
        ToolExecutionResult returned;
        var ending = Ending.Completed;
        // Execution time of this attempt, for a record without startedAt (SettleAsync).
        double? measured = null;
        try
        {
            // Built for this run, so a rerun after recovery gets the conversation's environment at that time.
            attempt.Api.Env = await runtime.EnvAsync(context).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            try
            {
                Task<ToolExecutionResult> executing;
                attempt.Nested.BeginPrefix();
                try
                {
                    executing = tool.Execute(args, attempt.Api, context);
                }
                finally
                {
                    attempt.Nested.EndPrefix();
                }
                returned = await executing.ConfigureAwait(false);
            }
            finally
            {
                measured = Math.Floor(Stopwatch.GetElapsedTime(started).TotalMilliseconds + 0.5);
            }
        }
        catch (Exception error)
        {
            if (runtime.Signal.IsCancellationRequested)
            {
                attempt.Reported.MarkEnded();
                foreach (var waiter in await attempt.Progress.StopAsync().ConfigureAwait(false)) waiter.TrySetException(error);
                throw;
            }
            returned = new ToolExecutionResult { IsError = true, Diagnostics = [ToolResults.Diagnostic("tool_error", DurableErrors.MessageOf(error))] };
            // A throw, from Execute or from building the environment, ends the task `failed`, which cancels what the call owned; it no longer
            // supervises it. The error text is already in the result.
            ending = Ending.Failed($"Tool {Name(call)} threw");
        }
        // Details still waiting for a progress commit settle with the next commit, the park's or the result's.
        var pending = await attempt.EndAsync().ConfigureAwait(false);
        try
        {
            if (Parks.IsPark(returned))
            {
                var invalid = await ParkAsync(runtime, input, call, tool, args, returned, attempt, context).ConfigureAwait(false);
                if (invalid is null)
                {
                    foreach (var waiter in pending) waiter.TrySetResult();
                    return;
                }
                returned = ToolResults.HarnessError("invalid_park", $"Tool {Name(call)} returned an invalid park: {invalid}");
                ending = Ending.Failed($"Tool {Name(call)} returned an invalid park");
            }
            var settled = await FinalResultAsync(runtime, input, call, tool, returned, attempt.Reported, context).ConfigureAwait(false);
            await SettleAsync(runtime, input, call, ending, settled, context, measured).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            foreach (var waiter in pending) waiter.TrySetException(error);
            throw;
        }
        foreach (var waiter in pending) waiter.TrySetResult();
    }

    /// <summary>
    /// Starts a run of the tool's code (upstream <c>startAttempt</c>). A woken run adds to the details and diagnostics earlier runs left in
    /// the slot (spec suspendable §4.2), so it starts from them; a nested call without progress keeps none there.
    /// </summary>
    private static async Task<Attempt> StartAttemptAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, ToolRegistration tool,
        Resumed resumed, DurableContext context)
    {
        var limits = OutputLimitsOf(tool);
        var streams = !input.Nested || input.Progress;
        var live = resumed.Wake is not null && streams
            ? await runtime.SnapshotAsync(Live.LiveDoc, runtime.ConversationId, context).ConfigureAwait(false)
            : null;
        var kept = live is null ? null : Live.ToolSlotOf(live, runtime.TaskId);
        var diagnostics = kept is not null && kept.TryGetValue("diagnostics", out var listed) && listed is JsonArrayValue stored
            ? stored.Select(ToolResults.ParseDiagnostic)
            : [];
        var reported = new ToolReport(new OutputBuffer(limits), limits, diagnostics,
            kept is not null && kept.TryGetValue("details", out var details) ? details : null);
        var progress = PublishProgress(runtime, reported, streams, context);
        // A caller that can rerun reattaches to its nested calls and child tasks after a restart; any other caller never resumes, so they
        // are abandoned (TaskOptions.AbandonOnRestart). After a wake a position may mean another call, so default keys are refused.
        var nested = new NestedCalls(runtime, reported, CallIdOf(call), resumes: tool.Replay == ToolReplay.Safe, keyed: resumed.Wake is not null);
        return new Attempt(new ToolExecutionApi(runtime, nested.CallId, reported, progress, nested, resumed), reported, progress, nested);
    }

    /// <summary>
    /// Parks the call (spec suspendable §4.2; upstream <c>park</c>). One commit persists the details and diagnostics the attempt reported,
    /// shows the slot <c>sleeping</c> or <c>waiting</c> without output, and stores the <c>parked</c> checkpoint with the park's sleep or
    /// wait; the call's live nested calls keep running. Returns why the park is invalid instead, when it is. When the commit is refused
    /// because the call was abort-marked meanwhile, <c>Cancel</c> cleans up what the run started before the error ends the run.
    /// </summary>
    private static async Task<string?> ParkAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, ToolRegistration tool, JsonValue args,
        ToolExecutionResult returned, Attempt attempt, DurableContext context)
    {
        if (Parks.ParkError(tool, returned, attempt.Api) is { } shape) return shape;
        var park = returned.Park!;
        var sleep = park.Sleep;
        var wait = park.Wait;
        if (wait is not null)
        {
            if (wait.On.IsDefault) return "a wait names the tasks it waits on";
            foreach (var id in wait.On)
                if ((await runtime.GetTaskAsync(id, context).ConfigureAwait(false))?.Owner != runtime.TaskId) return $"task {id} is not owned by the call";
        }
        var checkpoint = sleep is not null ? sleep.Checkpoint : wait!.Checkpoint;
        var parked = new RecordJson.Writer()
            .Add("phase", "parked")
            .Add("arguments", args)
            .Add("replay", WireName.Of(tool.Replay ?? ToolReplay.Unsafe))
            .Add("checkpoint", checkpoint)
            .Build();
        var streams = !input.Nested || input.Progress;
        var (_, details, diagnostics) = attempt.Reported.Final(false);
        try
        {
            await runtime.CommitAsync(async (tx, _) =>
            {
                var slot = Live.ToolSlot(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId);
                if (slot is not null)
                {
                    if (streams && details is not null) HarnessJson.AssignJson(slot, "details", details);
                    if (streams && diagnostics.Count > 0)
                        HarnessJson.AssignJson(slot, "diagnostics", JsonValue.Array(diagnostics.Select(ToolResults.DiagnosticJson)));
                    Live.ClearOutput(slot);
                    slot.Set("status", sleep is not null ? "sleeping" : "waiting");
                }
                return (TaskState?)(sleep is not null
                    ? new SleepingTaskState(parked, sleep.Until, sleep.Signals)
                    : new WaitingTaskState(parked, wait!.On, wait.Policy));
            }, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The sleep or wait broke a rule the commit checks: a deadline, a used key, a missing task, a wait cycle. A close seals the
            // Session before it signals the run.
            var closing = DurableTaskScheduler.CloseSignalOf(runtime);
            if (!runtime.Signal.IsCancellationRequested && !closing.Token.IsCancellationRequested) return DurableErrors.MessageOf(error);
            // Abort-marked meanwhile, unlike a close, which leaves the run to recovery: the park is not stored, so clean up with its
            // checkpoint now, in a context only the Harness's close cancels.
            var cleanup = context.WithoutCancellation().WithSignal(closing);
            if ((await runtime.GetTaskAsync(runtime.TaskId, cleanup).ConfigureAwait(false))?.AbortRequested == true)
                await CancelAsync(runtime, input, call, tool, args, checkpoint, cleanup).ConfigureAwait(false);
            throw;
        }
        return null;
    }

    /// <summary>
    /// Runs the tool's <c>Cancel</c>, if it has one, to clean up the external work of a call that parked (spec suspendable §4.2; upstream
    /// <c>cancel</c>): <c>api.Checkpoint</c> is the last park's, <c>api.Wake</c> null, and the api starts no nested calls or tasks. A throw is
    /// reported; the call ends either way.
    /// </summary>
    private static async Task CancelAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, ToolRegistration tool, JsonValue args,
        JsonValue? checkpoint, DurableContext context)
    {
        if (tool.Cancel is null) return;
        var attempt = await StartAttemptAsync(runtime, input, call, tool, new Resumed(null, checkpoint), context).ConfigureAwait(false);
        try
        {
            attempt.Api.Env = await runtime.EnvAsync(context).ConfigureAwait(false);
            await tool.Cancel(args, new CancelApi(attempt.Api, Name(call)), context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Only the Harness's close cancels a cleanup.
            if (context.IsCancellationRequested) throw;
            await ReportToolErrorAsync(runtime, tool, error, context).ConfigureAwait(false);
        }
        foreach (var waiter in await attempt.EndAsync().ConfigureAwait(false)) waiter.TrySetResult();
    }

    /// <summary>The api of a <c>Cancel</c> run: the attempt's, refusing nested calls and tasks.</summary>
    private sealed class CancelApi(IToolExecutionApi inner, string name) : DelegatingToolExecutionApi(inner)
    {
        private InvalidOperationException Refused() => new($"Tool {name} cannot start nested calls or tasks in cancel");

        public override Task<NestedToolExecutionResult> ExecuteToolAsync(string name, JsonObjectValue arguments, DurableContext context,
            NestedCallOptions? options = null) => throw Refused();
        public override Task<NestedToolCall> StartToolAsync(string name, JsonObjectValue arguments, DurableContext context,
            NestedCallOptions? options = null) => throw Refused();
        public override Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context) => throw Refused();
        public override Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, string key, DurableContext context) =>
            throw Refused();
    }

    /// <summary>Reports a failure of the tool's code as an <c>extension_error</c>, naming its extension when the agent resolves (upstream
    /// <c>reportToolError</c>).</summary>
    private static async Task ReportToolErrorAsync(ITaskRuntime runtime, ToolRegistration tool, Exception error, DurableContext context)
    {
        // Only names the extension: a failed resolution leaves the report without the name.
        string? extension;
        try
        {
            extension = Agents.ToolExtension(await runtime.AgentAsync(context).ConfigureAwait(false), tool.Name);
        }
        catch (Exception)
        {
            extension = null;
        }
        runtime.Report(error, ReportFrom.FromExtension(extension));
    }

    /// <summary>Throttled commits of what the tool reported into its slot, in <c>pi.live.tools</c> or <c>pi.live.nestedTools</c>, each
    /// writing only what changed since the last one, deferred (<see cref="Live.ProgressCommit"/>); none when <paramref name="enabled"/> is
    /// false (upstream <c>publishProgress</c>).</summary>
    private static Progress PublishProgress(ITaskRuntime runtime, ToolReport reported, bool enabled, DurableContext context)
    {
        // The slot holds what the attempt started from (StartAttemptAsync).
        var (_, startDetails, _, startCount) = reported.Capture(0);
        var written = (Text: "", Details: startDetails, Diagnostics: startCount);
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
                }, context, Live.ProgressCommit).ConfigureAwait(false);
                written = (snapshot.Text, details, count);
                return bytes;
            }
        }, error =>
        {
            // Rejections after an abort mark or close are expected; the committed state stays consistent.
            if (runtime.Signal.IsCancellationRequested) return;
            try { runtime.Report(error); }
            catch (Exception) { /* the invocation ended */ }
        }, runtime.Settings.Progress.OutputIntervalMs, ProgressClockOf(runtime));
    }

    /// <summary>The Harness clock and timers for progress pacing (upstream <c>progressClock</c>). After the invocation ends nothing more
    /// commits, and the clock answers with the last time it read.</summary>
    private static ProgressClock ProgressClockOf(ITaskRuntime runtime)
    {
        var last = 0d;
        return new ProgressClock(() =>
        {
            try
            {
                Interlocked.Exchange(ref last, runtime.Now());
            }
            catch (Exception)
            {
                // The invocation ended.
            }
            return Interlocked.CompareExchange(ref last, 0, 0);
        }, runtime.SetTimer);
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
                await ReportToolErrorAsync(runtime, tool, new InvalidOperationException(broken), context).ConfigureAwait(false);
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

    /// <summary>
    /// A result the Harness wrote itself, without executing the tool, after the <c>afterTool</c> chain, normalized like an executed call's
    /// (upstream <c>afterHarnessResult</c>): the output bounded with the tool's limits (the defaults when it did not resolve), a truncation
    /// diagnostic when that dropped something, and the hooks' <c>StructuredOutput</c> dropped, since the tool never produced one. A nested call
    /// that the hooks turned into a success gets the bounded output as its value, as a schema-less tool's success does, unless the tool
    /// declares a schema. <c>Usage</c> is kept and recorded; <c>Control</c> applies only when the task completes (SettleAsync).
    /// <paramref name="truncation"/> describes what the result's own output lost before (a slot's retained output); it is kept only while the
    /// hooks keep that output.
    /// </summary>
    private static async Task<ToolExecutionResult> AfterHarnessResultAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call,
        ToolRegistration? tool, ToolExecutionResult result, ToolDiagnostic? truncation, DurableContext context)
    {
        var final = result;
        await runtime.Hooks.EachAsync<ToolHooks, Func<JsonObjectValue, ToolExecutionResult, IHookApi, DurableContext, Task<ToolExecutionResult?>>>(
            hooks => hooks.AfterTool,
            async hook => final = await hook(call, final, runtime, context).ConfigureAwait(false) ?? final).ConfigureAwait(false);
        var harness = new List<ToolDiagnostic>();
        if (truncation is not null && ReferenceEquals(final.Output, result.Output)) harness.Add(truncation);
        var limits = OutputLimitsOf(tool);
        var bounded = BoundContent(final.Output ?? JsonValue.Array(), limits);
        if (bounded.DroppedBytes > 0) harness.Add(Truncated(bounded.DroppedLines, bounded.DroppedBytes, limits.Retain));
        var value = input.Nested && final.IsError != true && tool?.StructuredOutputSchema is null ? OutputValue(bounded.Content) : null;
        return final with { StructuredOutput = value, Output = bounded.Content, Diagnostics = [.. final.Diagnostics ?? [], .. harness] };
    }

    private static readonly ConditionalWeakTable<JsonObjectValue, StructuredOutputValidator> StructuredValidators = new();

    /// <summary>Why a result's structured output breaks the tool's contract, or null when it keeps it (upstream
    /// <c>structuredOutputError</c>).</summary>
    private static string? StructuredOutputError(ToolRegistration tool, ToolExecutionResult result)
    {
        var schema = tool.StructuredOutputSchema;
        var value = result.StructuredOutput;
        if (schema is null)
            return value is null ? null : $"Tool {tool.Name} returned structuredOutput but declares no structuredOutputSchema";
        if (value is null) return result.IsError == true ? null : $"Tool {tool.Name} returned no structuredOutput";
        var validator = StructuredValidators.GetValue(schema, StructuredOutputValidator.Compile);
        if (validator.FirstError(value) is not { } first) return null;
        return $"Tool {tool.Name} returned structuredOutput that does not match its schema: {first.Path}: {first.Message}";
    }

    /// <summary>
    /// Commits the tool's terminal state and marks its slot done (upstream <c>settle</c>). A model-issued call appends its result entry and
    /// ends with the entry ID; a nested call writes its result to the caller's <c>NestedCallDoc</c>, records its usage, and ends with a small
    /// receipt. Nested calls this call left running are aborted first, so they report into their slots before the slots below the call leave
    /// <c>pi.live</c>. <c>Control</c> applies only when the task completes.
    /// </summary>
    private static async Task SettleAsync(ITaskRuntime runtime, ToolInput input, JsonObjectValue call, Ending ending,
        ToolExecutionResult result, DurableContext context, double? measured = null)
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
        await runtime.CommitAsync(async (tx, current) =>
        {
            var conversationId = runtime.ConversationId;
            // The call's whole span, parks, reruns, and downtime included (spec suspendable §4.2); a record from before lifecycle times
            // keeps the last attempt's execution time.
            var durationMs = current.Record.StartedAt is { } startedAt ? runtime.Now() - startedAt : measured;
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var slot = Live.ToolSlot(live, runtime.TaskId);
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
            {
                var detail = ending.Reason is { } reason ? new RecordJson.Writer().Add("reason", reason).Build() : null;
                return NextTaskState.Terminal(new FailedTaskOutcome(new TaskOutcomeError(ending.Message!, detail), settled));
            }
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
        var replayed = false;
        await runtime.CommitAsync(async (tx, current) =>
        {
            // Upstream admits a nested call only once the memo committed.
            if (MemoOf(current) is null) throw new InvalidOperationException($"Tool call {parentCallId} has not committed {MadeNestedCalls}");
            // Opened, or created empty for a key not made yet.
            var state = await tx.DocAsync(HarnessTasks.NestedCallDoc, runtime.TaskId, key, JsonValue.Null).ConfigureAwait(false);
            if (state.Get("taskId") is JsonNumberValue existing)
            {
                var existingId = new TaskId((long)existing.Value);
                // A member written before keyed tasks has no kind and is a nested call's.
                var stored = state.Get("kind") is JsonStringValue { Value: "task" }
                    ? null
                    : (await tx.GetTaskAsync(existingId).ConfigureAwait(false))?.Input as JsonObjectValue;
                var same = stored is not null && stored.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "nested" } &&
                    stored["call"] is JsonObjectValue made && made["name"] is JsonStringValue { Value: var madeName } && madeName == name &&
                    JsonValue.DeepEquals(made["arguments"], args);
                if (!same) throw new InvalidOperationException($"Nested call {parentCallId}/{key} was already made with another tool or other arguments");
                id = existingId;
                replayed = state.ContainsKey("result");
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
            state.Set("kind", "nested");
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
        if (replayed) context.Telemetry.AddEvent("pi.durable.replay", new PiSharp.Telemetry.SpanAttributes { ["tool.name"] = name });
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

    /// <summary>An error result from the slot's durable partial output, details and diagnostics, ending with <paramref name="code"/>; and the
    /// truncation diagnostic of that output, when its bounds dropped some, which the caller adds after <c>afterTool</c> (upstream
    /// <c>fromSlot</c>).</summary>
    private static (ToolExecutionResult Built, ToolDiagnostic? Truncation) FromSlot(JsonObjectValue? slot, string code, string message)
    {
        var diagnostics = new List<ToolDiagnostic>();
        if (slot is not null && slot.TryGetValue("diagnostics", out var listed) && listed is JsonArrayValue stored)
            diagnostics.AddRange(stored.Select(ToolResults.ParseDiagnostic));
        diagnostics.Add(ToolResults.Diagnostic(code, message));
        var output = slot is not null && slot.TryGetValue("output", out var retained) && retained is JsonStringValue { Value: var text } ? text : null;
        var result = new ToolExecutionResult
        {
            Output = string.IsNullOrEmpty(output) ? JsonValue.Array() : JsonValue.Array(Text(output)),
            IsError = true,
            Details = slot is not null && slot.TryGetValue("details", out var details) ? details : null,
            Diagnostics = [.. diagnostics],
        };
        var droppedBytes = Number(slot, "droppedBytes") ?? 0;
        return (result, droppedBytes > 0 ? Truncated(Number(slot, "droppedLines") ?? 0, droppedBytes, null) : null);
    }

    private static long? Number(JsonObjectValue? slot, string key) =>
        slot is not null && slot.TryGetValue(key, out var value) && value is JsonNumberValue number ? (long)number.Value : null;

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
                $"Nested call key {((JsonValue)key).ToJson()} must be non-empty, without \"/\", not \"__proto__\", and not a positive integer");
    }

    private static bool IsPositiveInteger(string key) => key.Length > 0 && key[0] is >= '1' and <= '9' && key.All(c => c is >= '0' and <= '9');

    private static bool IsText(JsonObjectValue item) => item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" };

    private static JsonObjectValue Text(string text) => JsonValue.Object([
        KeyValuePair.Create("type", (JsonValue)"text"),
        KeyValuePair.Create("text", (JsonValue)text),
    ]);

    private static long Id(JsonValue value) => (long)((JsonNumberValue)value).Value;
}
