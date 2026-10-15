// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/generation.ts.
// Pi v1.2.0's area j parts of generation.ts are in (durable-sync-1.2.0-worklist.json): the prepare checkpoint's threshold flag and a
// failed blocking threshold compaction failing the run with model_error. The header moves to v1.2.0 with area a, which owns this row.
//
// Mappings:
// - Checkpoints, live state and results are upstream's JSON, built in upstream's key order: prepare {phase, attempt, compacted?, overflow?},
//   request {phase, attempt, compacted?, model, thinkingLevel, streamOptions, cutoff}, retry {phase, attempt, compacted?, until}, poll
//   {phase, attempt, compacted?, model, cutoff, handle, pollAt}, tools {phase, assistant, tools, pending}; the result is {entryId}.
// - Provider messages cross IDurableModels as PiSharp.Contracts values and are stored as pi-ai wire JSON (PiWireJson). Stream events are
//   compact frames, not upstream's events carrying a `partial`; the partial is the snapshot of a PiSharp.AI AssistantStreamReducer fed
//   every non-terminal event. A message with a non-finite usage count is not strict JSON and throws Chord's copyJson error, as upstream's
//   copy does; PiWireJson alone would write it as null.
// - The partial throttle's setTimeout is a timer of the Harness TimeProvider (the scheduler's), so fake time drives it as vi fake timers
//   would. Its one commit in flight is tracked under a lock: timer callbacks run on other threads.
// - The invocation signal is ITaskRuntime.Signal: it is passed to the stream and polls as their cancellation token, and
//   `signal.throwIfAborted()` throws OperationCanceledException.
using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the generation task (upstream <c>GenerationTask</c>, kind <c>pi.generation</c>, version 1).</summary>
public static partial class HarnessTasks
{
    /// <summary>
    /// Built-in generation task (upstream <c>GenerationTask</c>): prepares the positional system prompt and tool loadout, requests or polls
    /// the model, retries, and classifies the response. Its input is <c>{}</c>; the run's inputs live in <c>pi.live.run</c>. It starts at
    /// <c>{phase: "prepare", attempt: 1}</c>.
    /// </summary>
    public static DurableTask GenerationTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.generation",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"prepare","attempt":1}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
        {
            ["prepare"] = GenerationPhases.PrepareAsync,
            ["request"] = GenerationPhases.RequestAsync,
            ["retry"] = GenerationPhases.RetryAsync,
            ["poll"] = GenerationPhases.PollAsync,
            ["tools"] = GenerationPhases.ToolsAsync,
        },
        Abort = GenerationPhases.AbortAsync,
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

    /// <summary>
    /// Appends a provider result and adds its usage to <c>pi.usage</c> under <c>provider/model</c> in the same commit (upstream
    /// <c>appendAssistant</c>). Every built-in writer of assistant entries goes through here, so the usage ledger stays complete.
    /// </summary>
    internal static async Task<EntryRecord> AppendAssistantAsync(ITx tx, ConversationId conversationId, JsonObjectValue message)
    {
        var key = $"{Text(message, "provider")}/{Text(message, "model")}";
        await UsageLedger.RecordUsageAsync(tx, conversationId, "models", key, (JsonObjectValue)message["usage"]).ConfigureAwait(false);
        return await tx.AppendEntryAsync(Entries.AssistantEntry, conversationId, new TypedEntryDraft { Model = JsonValue.Array(message) })
            .ConfigureAwait(false);
    }

    /// <summary>Starts a run for <paramref name="inputs"/>, placed input submissions: a new generation takes <c>pi.live.run</c>
    /// (upstream <c>startRun</c>).</summary>
    internal static async Task StartRunAsync(ITx tx, ConversationId conversationId, DraftObject live, IEnumerable<SubmissionId> inputs)
    {
        var taskId = await CreateGenerationAsync(tx, conversationId).ConfigureAwait(false);
        live.Set("run", JsonValue.Object([
            KeyValuePair.Create("taskId", (JsonValue)taskId.ToJson()),
            KeyValuePair.Create("inputs", (JsonValue)JsonValue.Array(inputs.Select(id => (JsonValue)id.ToJson()))),
        ]));
    }

    /// <summary>A generation owned by its conversation (upstream <c>createGeneration</c>).</summary>
    internal static Task<TaskId> CreateGenerationAsync(ITx tx, ConversationId conversationId) =>
        tx.CreateTaskAsync(GenerationTask, JsonObjectValue.Empty, new TaskOptions(TaskOwnership.Conversation) { ConversationId = conversationId });

    /// <summary>Hands run control from <paramref name="from"/> to <paramref name="to"/>; the run's inputs move with it (upstream
    /// <c>handOver</c>).</summary>
    internal static void HandOver(DraftObject live, TaskId from, TaskId to)
    {
        if (RunTaskId(live) == from.Value) live.GetObject("run").Set("taskId", to.ToJson());
    }

    internal static long? RunTaskId(DraftObject live) =>
        live.ContainsKey("run") && live.Get("run") is JsonObjectValue run && run.TryGetValue("taskId", out var id) && id is JsonNumberValue number
            ? (long)number.Value : null;

    private static string Text(JsonObjectValue value, string key) =>
        value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : field?.ToJson() ?? "undefined";
}

/// <summary>The generation task's phases and helpers (upstream <c>generation.ts</c>).</summary>
internal static class GenerationPhases
{
    private const double DefaultPollAfterMs = 5000;

    /// <summary>What classification needs from the request that produced a message (upstream <c>Request</c>).</summary>
    private sealed record Request(int Attempt, TaskId? Compacted, ModelRef Model, EntryId Cutoff)
    {
        /// <summary>Committed model context through <see cref="Cutoff"/>, when the phase already derived it.</summary>
        public ImmutableArray<JsonValue>? Messages { get; init; }
        /// <summary>Set when the message came from polling, so a still deferred result polls strictly later.</summary>
        public double? PollAt { get; init; }
    }

    // ─── Phases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the system prompt and tool loadout and appends the positional <c>pi.system</c> entries they need, then moves to
    /// <c>request</c>. The agent and settings resolved here are fixed for this request. Only the Harness writes to a busy conversation, so
    /// the transcript read here is still the tail at the commit.
    /// </summary>
    public static async Task PrepareAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var conversationId = runtime.ConversationId;
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var settings = runtime.Settings;
        var model = agent.Model;
        var resolved = model is null ? null : runtime.Models.GetModel(model.Provider, model.ModelId);
        if (model is null || resolved is null)
        {
            await FailNoModelAsync(runtime, model, context).ConfigureAwait(false);
            return;
        }
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var attempt = Attempt(checkpoint);
        var compacted = OptionalTask(checkpoint, "compacted");
        var overflow = checkpoint.TryGetValue("overflow", out var text) && text is JsonStringValue overflowText ? overflowText.Value : null;
        if (compacted is { } compaction && overflow is not null)
        {
            var outcome = (await runtime.OutcomesAsync([compaction], context).ConfigureAwait(false))[0];
            if (outcome is not CompletedTaskOutcome { Result: JsonObjectValue result } || !result.ContainsKey("entryId"))
            {
                await FailModelErrorAsync(runtime, overflow, context).ConfigureAwait(false);
                return;
            }
        }
        // A threshold compaction that found nothing to compact or was declined leaves a request that may still fit. One that failed left a
        // context it judged too large: sending it would at best leave no room for the answer.
        if (compacted is { } blocking && checkpoint.TryGetValue("threshold", out var blocked) && blocked is JsonBooleanValue { Value: true })
        {
            var outcome = (await runtime.OutcomesAsync([blocking], context).ConfigureAwait(false))[0];
            if (outcome is not CompletedTaskOutcome)
            {
                await FailModelErrorAsync(runtime, CompactionFailure(outcome), context).ConfigureAwait(false);
                return;
            }
        }
        var view = await runtime.ContextAsync(conversationId, context).ConfigureAwait(false);
        var shown = HarnessPrompt.ReplaySections(view.Messages);
        void Report(Exception error) => runtime.Report(error);
        Env.IExecutionEnv? env = null;
        try
        {
            env = await runtime.EnvAsync(context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (context.IsCancellationRequested) throw;
            Report(error);
        }
        var input = new PromptInput { ConversationId = conversationId, Agent = agent, Env = env, Shown = shown, Read = runtime };
        var desired = await HarnessPrompt.RenderSectionsAsync(agent.Sections, input, shown, Report, context).ConfigureAwait(false);
        var entries = HarnessPrompt.PlanSystemEntries(view, desired, agent.Tools, runtime.Now());
        var threshold = compacted is null ? ThresholdCompaction(view, entries, resolved.ContextWindow, settings.Compaction) : null;
        if (threshold == "blocking")
        {
            // Compact first and prepare again; the transcript is unchanged until the compaction appends.
            await runtime.CommitAsync(async (tx, _) =>
            {
                var child = await Compaction.CreateCompactionAsync(tx, conversationId, "threshold", owner: runtime.TaskId).ConfigureAwait(false);
                return (TaskState?)NextTaskState.Waiting(Prepare(attempt, child, null, threshold: true), [child], JoinPolicy.AllSettled);
            }, context).ConfigureAwait(false);
            return;
        }
        await runtime.CommitAsync(async (tx, _) =>
        {
            var cutoff = (await tx.ScanEntriesAsync(new EntryQuery(conversationId), 1).ConfigureAwait(false)).Items.FirstOrDefault()?.Id;
            foreach (var entry in entries) cutoff = (await tx.AppendEntryAsync(Entries.SystemEntry, conversationId, entry).ConfigureAwait(false)).Id;
            if (cutoff is not { } tail) throw new InvalidOperationException($"Conversation {conversationId} has no entries to send");
            // Checked in this commit, so a compaction admitted during preparation counts.
            if (threshold == "background" && !(await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false)).ContainsKey("compactions"))
                await Compaction.CreateCompactionAsync(tx, conversationId, "threshold").ConfigureAwait(false);
            var request = new RecordJson.Writer()
                .Add("phase", "request")
                .Number("attempt", attempt)
                .Add("compacted", compacted?.ToJson())
                .Add("model", model.ToJson())
                .Add("thinkingLevel", agent.ThinkingLevel)
                .Add("streamOptions", StreamOptionsJson(settings.Stream))
                .Add("cutoff", tail.ToJson())
                .Build();
            return (TaskState?)NextTaskState.Running(request);
        }, context).ConfigureAwait(false);
    }

    public static async Task RequestAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var attempt = Attempt(checkpoint);
        var compacted = OptionalTask(checkpoint, "compacted");
        var reference = ModelRef.FromJson(checkpoint["model"])!;
        var thinkingLevel = ((JsonStringValue)checkpoint["thinkingLevel"]).Value;
        var streamOptions = checkpoint.TryGetValue("streamOptions", out var pinned) && pinned is JsonObjectValue options ? options : JsonObjectValue.Empty;
        var cutoff = new EntryId(Id(checkpoint["cutoff"]));
        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await HarnessTasks.ConvertPartialAsync(tx, live, conversationId).ConfigureAwait(false);
            live.Set("generation", GenerationState(attempt));
        }, context).ConfigureAwait(false);
        var model = runtime.Models.GetModel(reference.Provider, reference.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, reference, context).ConfigureAwait(false);
            return;
        }
        var view = await runtime.ContextAsync(conversationId, context, cutoff).ConfigureAwait(false);
        var messages = view.Messages;
        await runtime.Hooks.EachAsync<GenerationHooks, Func<ImmutableArray<JsonValue>, IHookApi, DurableContext, Task<ImmutableArray<JsonValue>?>>>(
            hooks => hooks.BeforeRequest, async hook =>
            {
                if (await hook(messages, runtime, context).ConfigureAwait(false) is { } replaced) messages = replaced;
            }).ConfigureAwait(false);
        var request = StreamOptions(streamOptions) with
        {
            SessionId = await ProviderIdentity.EnsureProviderSessionIdAsync(runtime, context).ConfigureAwait(false),
            Reasoning = thinkingLevel == "off" ? null : thinkingLevel,
        };
        var message = await StreamResponseAsync(runtime, model, messages, request, attempt, context).ConfigureAwait(false);
        await ClassifyAsync(runtime, new Request(attempt, compacted, reference, cutoff) { Messages = view.Messages }, message, context).ConfigureAwait(false);
    }

    public static async Task RetryAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var attempt = Attempt(checkpoint);
        var compacted = OptionalTask(checkpoint, "compacted");
        await runtime.SleepAsync(((JsonNumberValue)checkpoint["until"]).Value, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            (await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false)).Set("generation", GenerationState(attempt + 1));
            return (TaskState?)NextTaskState.Running(Prepare(attempt + 1, compacted, null));
        }, context).ConfigureAwait(false);
    }

    public static async Task PollAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var reference = ModelRef.FromJson(checkpoint["model"])!;
        var model = runtime.Models.GetModel(reference.Provider, reference.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, reference, context).ConfigureAwait(false);
            return;
        }
        var pollAt = ((JsonNumberValue)checkpoint["pollAt"]).Value;
        await runtime.SleepAsync(pollAt, context).ConfigureAwait(false);
        var handle = new DurableDeferredHandle((JsonObjectValue)checkpoint["handle"]);
        var fetched = await runtime.Models.FetchDeferredAsync(model, handle, runtime.Signal).ConfigureAwait(false);
        var request = new Request(Attempt(checkpoint), OptionalTask(checkpoint, "compacted"), reference, new EntryId(Id(checkpoint["cutoff"])))
        {
            PollAt = pollAt,
        };
        await ClassifyAsync(runtime, request, ToWire(fetched), context).ConfigureAwait(false);
    }

    public static async Task ToolsAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var assistant = new EntryId(Id(checkpoint["assistant"]));
        var tools = ((JsonArrayValue)checkpoint["tools"]).Select(id => new TaskId(Id(id))).ToList();
        var pending = ((JsonArrayValue)checkpoint["pending"]).Select(id => ((JsonStringValue)id).Value).ToList();
        if (pending.Count == 0)
        {
            await FinishToolRoundAsync(runtime, assistant, tools, context).ConfigureAwait(false);
            return;
        }
        var next = pending[0];
        // Sequential round: start the next call and wait for it.
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            var taskId = await CreateToolTaskAsync(tx, runtime, assistant, next).ConfigureAwait(false);
            if (live.ContainsKey("tools") && live.Get("tools") is JsonArrayValue)
            {
                var slots = live.GetArray("tools");
                for (var index = 0; index < slots.Count; index++)
                {
                    var slot = (JsonObjectValue)slots.Get(index);
                    if (slot.TryGetValue("callId", out var callId) && callId is JsonStringValue { Value: var id } && id == next && !slot.ContainsKey("taskId"))
                    {
                        slots.GetObject(index).Set("taskId", taskId.ToJson());
                        break;
                    }
                }
            }
            return (TaskState?)NextTaskState.Waiting(ToolsCheckpoint(assistant, [.. tools, taskId], pending.Skip(1)), [taskId], JoinPolicy.AllSettled);
        }, context).ConfigureAwait(false);
    }

    public static async Task AbortAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var phase = task.Phase;
        if (phase == "poll" && ModelRef.FromJson(checkpoint["model"]) is { } reference &&
            runtime.Models.GetModel(reference.Provider, reference.ModelId) is { } model)
        {
            try
            {
                await runtime.Models.CancelDeferredAsync(model, new DurableDeferredHandle((JsonObjectValue)checkpoint["handle"]), runtime.Signal)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                runtime.Report(error);
            }
        }
        var conversationId = runtime.ConversationId;
        // Runs after the round's tool tasks are terminal; calls never started get `aborted` results (spec §8.5).
        var unstarted = phase == "tools"
            ? await ReadCallsAsync(runtime, new EntryId(Id(checkpoint["assistant"])),
                [.. ((JsonArrayValue)checkpoint["pending"]).Select(id => ((JsonStringValue)id).Value)], context).ConfigureAwait(false)
            : [];
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await HarnessTasks.ConvertPartialAsync(tx, live, conversationId).ConfigureAwait(false);
            foreach (var call in unstarted)
            {
                var result = ToolResults.HarnessError("aborted", $"Tool {((JsonStringValue)call["name"]).Value} was aborted");
                await ToolResults.AppendToolResultAsync(tx, conversationId, call, result, runtime.Now()).ConfigureAwait(false);
            }
            Live.EndRun(tx, live, runtime.TaskId, SubmissionSettlement.Unanswered("aborted"));
            return (TaskState?)NextTaskState.Aborted();
        }, context).ConfigureAwait(false);
    }

    // ─── Classification ─────────────────────────────────────────────────────

    /// <summary>The calls <paramref name="callIds"/> of the assistant entry, in the given order (upstream <c>readCalls</c>).</summary>
    private static async Task<List<JsonObjectValue>> ReadCallsAsync(ITaskRuntime runtime, EntryId assistant, IReadOnlyList<string> callIds,
        DurableContext context)
    {
        var entry = await runtime.EntryAsync(Entries.AssistantEntry, assistant, context).ConfigureAwait(false);
        var message = entry?.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
        var calls = message is not null && Str(message, "role") == "assistant" ? ToolCalls(message) : [];
        return [.. callIds.SelectMany(id => calls.Where(call => Str(call, "id") == id).Take(1))];
    }

    /// <summary>A tool task for call <paramref name="callId"/>, owned by the generation (upstream <c>createToolTask</c>).</summary>
    private static Task<TaskId> CreateToolTaskAsync(ITx tx, ITaskRuntime runtime, EntryId assistant, string callId) =>
        tx.CreateTaskAsync(HarnessTasks.ToolTask,
            JsonValue.Object([KeyValuePair.Create("assistant", (JsonValue)assistant.ToJson()), KeyValuePair.Create("callId", (JsonValue)callId)]),
            new TaskOptions(TaskOwnership.Task(runtime.TaskId)));

    /// <summary>
    /// Which threshold compaction preparation starts before its request (upstream <c>thresholdCompaction</c>, spec §8.3): <c>blocking</c>
    /// above <c>contextWindow - reserveTokens</c>, <c>background</c> above the background threshold, and only when range selection finds a
    /// cut. The caller starts a background one only while no compaction is listed.
    /// </summary>
    private static string? ThresholdCompaction(ContextView view, IReadOnlyList<TypedEntryDraft> planned, double contextWindow, CompactionPolicy policy)
    {
        if (!policy.Enabled || contextWindow <= 0) return null;
        var tokens = Compaction.EstimateContext(view, planned.SelectMany(entry => (IEnumerable<JsonValue>?)entry.Model ?? []));
        var blocking = contextWindow - policy.ReserveTokens;
        var background = blocking - policy.BackgroundTokens;
        var over = tokens > blocking ? "blocking" : policy.BackgroundTokens > 0 && tokens > background ? "background" : null;
        if (over is null || Compaction.SelectCut(view, policy.KeepRecentTokens) is null) return null;
        return over;
    }

    /// <summary>Settles the run's inputs <c>unanswered</c> with <c>model_error</c> and fails with <paramref name="text"/> (upstream
    /// <c>failModelError</c>).</summary>
    private static Task FailModelErrorAsync(ITaskRuntime runtime, string text, DurableContext context) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.EndRun(tx, live, runtime.TaskId, SubmissionSettlement.Unanswered("model_error", text));
            return (TaskState?)NextTaskState.Failed(text, Reason("model_error"));
        }, context);

    /// <summary>Settles the run's inputs <c>unanswered</c> with <c>no_model</c> and fails (upstream <c>failNoModel</c>).</summary>
    private static Task FailNoModelAsync(ITaskRuntime runtime, ModelRef? reference, DurableContext context)
    {
        var message = reference is null ? "No model is configured" : $"Model {reference.Provider}/{reference.ModelId} is not available";
        return runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.EndRun(tx, live, runtime.TaskId, SubmissionSettlement.Unanswered("no_model"));
            return (TaskState?)NextTaskState.Failed(message, Reason("no_model"));
        }, context);
    }

    /// <summary>
    /// Streams one request and returns the terminal message (upstream <c>streamResponse</c>). Partials commit as trailing writes at most
    /// every <c>progress.partialIntervalMs</c> (default 100 ms) with one commit in flight; <c>finally</c> stops the throttle and awaits that
    /// commit, so no stale partial lands after the outcome.
    /// </summary>
    private static async Task<JsonObjectValue> StreamResponseAsync(ITaskRuntime runtime, DurableModel model, ImmutableArray<JsonValue> messages,
        DurableStreamOptions options, int attempt, DurableContext context)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(0, runtime.Settings.Progress.PartialIntervalMs));
        var time = (runtime as ITaskTimeSource)?.TimeProvider ?? TimeProvider.System;
        var gate = new Lock();
        AssistantMessage? pending = null;
        ITimer? timer = null;
        Task? inFlight = null;
        var stopped = false;

        void Schedule() => timer = time.CreateTimer(_ => Flush(), null, interval, Timeout.InfiniteTimeSpan);

        void Flush()
        {
            lock (gate)
            {
                timer?.Dispose();
                timer = null;
                var partial = pending;
                pending = null;
                if (partial is null || stopped) return;
                // Started under the lock, so its completion (which takes the lock) always sees itself as the commit in flight.
                inFlight = Task.Run(() => CommitPartialAsync(partial));
            }
        }

        async Task CommitPartialAsync(AssistantMessage partial)
        {
            try
            {
                var message = ToWire(partial);
                await runtime.CommitAsync(async (tx, _) =>
                {
                    var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
                    if (!live.ContainsKey("generation")) live.Set("generation", GenerationState(attempt));
                    HarnessJson.AssignJson(live.GetObject("generation"), "message", message);
                }, context).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Rejections after an abort mark or close are expected; the committed state stays consistent.
                if (!runtime.Signal.IsCancellationRequested)
                {
                    try { runtime.Report(error); }
                    catch (Exception) { /* the invocation ended */ }
                }
            }
            finally
            {
                lock (gate)
                {
                    inFlight = null;
                    if (pending is not null && !stopped) Schedule();
                }
            }
        }

        try
        {
            var reducer = new AssistantStreamReducer(new AssistantMessage(model.Api, model.Provider, model.Id, (long)runtime.Now(), [],
                TokenUsage.Zero, StopReason.Pending));
            StreamTerminalEvent? terminal = null;
            await foreach (var item in runtime.Models.StreamSimple(model, messages, options, runtime.Signal).WithCancellation(runtime.Signal)
                .ConfigureAwait(false))
            {
                if (item is StreamTerminalEvent end)
                {
                    terminal = end;
                    continue;
                }
                reducer.Apply(item);
                var partial = reducer.Snapshot();
                // A partial without content, such as the opening `start` event, shows nothing; a deferred response never gets past it, so
                // it never leaves a partial.
                if (partial.Content.IsEmpty) continue;
                lock (gate)
                {
                    pending = partial;
                    if (timer is null && inFlight is null) Schedule();
                }
            }
            if (terminal is null) throw new InvalidOperationException($"The {model.Provider}/{model.Id} stream ended without a terminal event");
            return ToWire(terminal.Message);
        }
        finally
        {
            Task? flushing;
            lock (gate)
            {
                stopped = true;
                timer?.Dispose();
                timer = null;
                flushing = inFlight;
            }
            if (flushing is not null) await flushing.ConfigureAwait(false);
        }
    }

    /// <summary>Classifies a terminal provider message in one commit that also clears the partial (upstream <c>classify</c>).</summary>
    private static async Task ClassifyAsync(ITaskRuntime runtime, Request request, JsonObjectValue message, DurableContext context)
    {
        // An abort mark or close: the abort invocation or the reopened run handles the committed state.
        runtime.Signal.ThrowIfCancellationRequested();
        var conversationId = runtime.ConversationId;
        var (attempt, compacted, reference, cutoff) = (request.Attempt, request.Compacted, request.Model, request.Cutoff);
        var stopReason = Str(message, "stopReason");
        if (stopReason == "deferred" && message.TryGetValue("deferred", out var deferred) && deferred is JsonObjectValue handle)
        {
            var pollAfter = handle.TryGetValue("pollAfterMs", out var after) && after is JsonNumberValue ms ? ms.Value : DefaultPollAfterMs;
            var pollAt = Math.Max(runtime.Now() + pollAfter, request.PollAt is { } previous ? previous + 1 : double.NegativeInfinity);
            await runtime.CommitAsync(async (tx, _) =>
            {
                var state = new RecordJson.Writer().Number("attempt", attempt)
                    .Add("deferred", JsonValue.Object([KeyValuePair.Create("pollAt", (JsonValue)pollAt)])).Build();
                (await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false)).Set("generation", state);
                var checkpoint = new RecordJson.Writer()
                    .Add("phase", "poll")
                    .Number("attempt", attempt)
                    .Add("compacted", compacted?.ToJson())
                    .Add("model", reference.ToJson())
                    .Add("cutoff", cutoff.ToJson())
                    .Add("handle", handle)
                    .Number("pollAt", pollAt)
                    .Build();
                return (TaskState?)NextTaskState.Running(checkpoint);
            }, context).ConfigureAwait(false);
            return;
        }
        await runtime.Hooks.EachAsync<GenerationHooks, Func<JsonObjectValue, IHookApi, DurableContext, Task>>(hooks => hooks.AfterResponse,
            hook => hook(message, runtime, context)).ConfigureAwait(false);
        var calls = ToolCalls(message);
        if (stopReason == "toolUse" && calls.Count > 0)
        {
            await StartToolRoundAsync(runtime, request, message, calls, context).ConfigureAwait(false);
            return;
        }
        if (stopReason is "stop" or "length" or "toolUse")
        {
            await AnswerAsync(runtime, message, context).ConfigureAwait(false);
            return;
        }
        // The retry and compaction policies govern the next attempt, so they are read now rather than pinned at preparation.
        var settings = runtime.Settings;
        var overflow = stopReason == "error" && PiAiOverflow.IsContextOverflow(message);
        if (overflow && compacted is null && settings.Compaction.Enabled)
        {
            var policy = settings.Compaction;
            var view = await runtime.ContextAsync(conversationId, context, cutoff).ConfigureAwait(false);
            if (Compaction.SelectCut(view, policy.KeepRecentTokens) is not null)
            {
                var text = Str(message, "errorMessage") ?? "Context overflow";
                await runtime.CommitAsync(async (tx, _) =>
                {
                    var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                    await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
                    live.Delete("generation");
                    var child = await Compaction.CreateCompactionAsync(tx, conversationId, "overflow", owner: runtime.TaskId).ConfigureAwait(false);
                    return (TaskState?)NextTaskState.Waiting(Prepare(attempt, child, text), [child], JoinPolicy.AllSettled);
                }, context).ConfigureAwait(false);
                return;
            }
        }
        var retryPolicy = settings.Retry;
        // An overflow is never retried: only a compaction can make the next request fit.
        var retry = stopReason == "error" && !overflow && PiAiRetry.IsRetryableAssistantError(message) && retryPolicy.Enabled &&
            attempt <= retryPolicy.MaxRetries;
        var until = retry ? runtime.Now() + PiAiRetry.RetryDelayMs(retryPolicy.BaseDelayMs, retryPolicy.MaxAgentDelayMs, attempt) : 0;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            if (retry)
            {
                var retryState = JsonValue.Object([
                    KeyValuePair.Create("at", (JsonValue)until),
                    KeyValuePair.Create("error", (JsonValue)(Str(message, "errorMessage") ?? "")),
                ]);
                live.Set("generation", new RecordJson.Writer().Number("attempt", attempt).Add("retry", retryState).Build());
                var checkpoint = new RecordJson.Writer().Add("phase", "retry").Number("attempt", attempt).Add("compacted", compacted?.ToJson())
                    .Number("until", until).Build();
                return (TaskState?)NextTaskState.Running(checkpoint);
            }
            var text = Str(message, "errorMessage") ?? $"Model response ended with stop reason {stopReason ?? "undefined"}";
            Live.EndRun(tx, live, runtime.TaskId, SubmissionSettlement.Unanswered("model_error", text));
            return (TaskState?)NextTaskState.Failed(text, Reason("model_error"));
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// A final answer; the final boundary places queued items (upstream <c>answer</c>, spec §6). The first <c>onYield</c> continuation appends
    /// a user message and hands the run to a successor generation, but only when the boundary selected no user item and no reset.
    /// Otherwise the run's inputs settle <c>done</c>, and selected user items start the next run.
    /// </summary>
    private static async Task AnswerAsync(ITaskRuntime runtime, JsonObjectValue message, DurableContext context)
    {
        JsonValue? continuation = null;
        await runtime.Hooks.EachAsync<GenerationHooks, Func<JsonObjectValue, IHookApi, DurableContext, Task<JsonValue?>>>(hooks => hooks.OnYield,
            async hook =>
            {
                if (continuation is not null) return;
                continuation = await hook(message, runtime, context).ConfigureAwait(false);
            }).ConfigureAwait(false);
        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            // Queue modes are read on the Session line, when the boundary is decided.
            var boundary = await Inbox.PrepareBoundaryAsync(tx, conversationId, runtime.Settings).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            var result = NextTaskState.Completed(EntryResult(entry.Id));
            var (users, reset) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, runtime.Now()).ConfigureAwait(false);
            if (continuation is not null && users.IsEmpty && !reset)
            {
                var user = JsonValue.Object([
                    KeyValuePair.Create("role", (JsonValue)"user"),
                    KeyValuePair.Create("content", continuation),
                    KeyValuePair.Create("timestamp", (JsonValue)runtime.Now()),
                ]);
                await tx.AppendEntryAsync(Entries.UserEntry, conversationId, new TypedEntryDraft { Model = JsonValue.Array(user) }).ConfigureAwait(false);
                HarnessTasks.HandOver(live, runtime.TaskId, await HarnessTasks.CreateGenerationAsync(tx, conversationId).ConfigureAwait(false));
                live.Delete("generation");
                return (TaskState?)result;
            }
            Live.EndRun(tx, live, runtime.TaskId, SubmissionSettlement.Done(entry.Id));
            if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, live, users).ConfigureAwait(false);
            return (TaskState?)result;
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends the tool-calling answer and starts its tool round in one commit (upstream <c>startToolRound</c>, spec §8.3). A call to a tool
    /// the request did not offer gets its <c>tool_unavailable</c> result here; every other call gets a tool task owned by the generation,
    /// only the first one now when the round is sequential. The generation then waits for them in its <c>tools</c> phase, keeping the run.
    /// </summary>
    private static async Task StartToolRoundAsync(ITaskRuntime runtime, Request request, JsonObjectValue message, List<JsonObjectValue> calls,
        DurableContext context)
    {
        var conversationId = runtime.ConversationId;
        var messages = request.Messages ?? (await runtime.ContextAsync(conversationId, context, request.Cutoff).ConfigureAwait(false)).Messages;
        var offered = PiAiTranscript.GetCurrentTools(messages).Select(tool => Str(tool, "name")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        // Read as the round starts; a tool is resolved as its tool task resolves it.
        var registered = (await runtime.AgentAsync(context).ConfigureAwait(false)).Tools;
        var sequential = runtime.Settings.ToolExecution == ToolExecutionMode.Sequential || calls.Any(call =>
            Str(call, "name") is { } name && offered.Contains(name) &&
            registered.FirstOrDefault(tool => tool.Name == name)?.ExecutionMode == ToolExecutionMode.Sequential);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            var slots = new List<JsonValue>();
            var tools = new List<TaskId>();
            var pending = new List<string>();
            foreach (var call in calls)
            {
                var callId = call["id"];
                var name = call["name"];
                if (!offered.Contains(Str(call, "name") ?? ""))
                {
                    var unavailable = ToolResults.HarnessError("tool_unavailable", $"Tool {Str(call, "name")} is not available");
                    var result = await ToolResults.AppendToolResultAsync(tx, conversationId, call, unavailable, runtime.Now()).ConfigureAwait(false);
                    slots.Add(new RecordJson.Writer().Add("callId", callId).Add("name", name).Add("status", "done").Add("entry", result.Id.ToJson()).Build());
                    continue;
                }
                if (sequential && tools.Count > 0)
                {
                    pending.Add(Str(call, "id")!);
                    slots.Add(new RecordJson.Writer().Add("callId", callId).Add("name", name).Add("status", "pending").Build());
                    continue;
                }
                var taskId = await CreateToolTaskAsync(tx, runtime, entry.Id, Str(call, "id")!).ConfigureAwait(false);
                tools.Add(taskId);
                slots.Add(new RecordJson.Writer().Add("callId", callId).Add("name", name).Add("taskId", taskId.ToJson()).Add("status", "pending").Build());
            }
            live.Delete("generation");
            live.Set("tools", JsonValue.Array(slots));
            return (TaskState?)NextTaskState.Waiting(ToolsCheckpoint(entry.Id, tools, pending), tools, JoinPolicy.AllSettled);
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// The round's tools are terminal: applies their controls and either ends the run at the final boundary (<c>terminate</c>,
    /// <c>handoff</c>, or a queued reset) or hands it to the next generation at the <c>postTools</c> boundary (upstream
    /// <c>finishToolRound</c>, spec §8.5).
    /// </summary>
    private static async Task FinishToolRoundAsync(ITaskRuntime runtime, EntryId assistant, IReadOnlyList<TaskId> tools, DurableContext context)
    {
        var conversationId = runtime.ConversationId;
        var controls = new OrderedDictionary<TaskId, JsonObjectValue?>();
        var outcomes = await runtime.OutcomesAsync(tools, context).ConfigureAwait(false);
        for (var index = 0; index < tools.Count; index++)
            controls[tools[index]] = outcomes[index] is CompletedTaskOutcome { Result: JsonObjectValue result } &&
                result.TryGetValue("control", out var control) && control is JsonObjectValue value ? value : null;
        var live = await runtime.SnapshotAsync(Live.LiveDoc, conversationId, context).ConfigureAwait(false);
        var slots = live is not null && live.TryGetValue("tools", out var listed) && listed is JsonArrayValue array
            ? array.OfType<JsonObjectValue>().ToList() : [];
        var results = slots.Where(slot => slot.ContainsKey("entry")).Select(slot => new EntryId(Id(slot["entry"]))).ToImmutableArray();
        await runtime.Hooks.EachAsync<GenerationHooks, Func<EntryId, ImmutableArray<EntryId>, IHookApi, DurableContext, Task>>(hooks => hooks.AfterTools,
            hook => hook(assistant, results, runtime, context)).ConfigureAwait(false);
        // Every call of the round, including those answered without a task, must ask to terminate.
        var terminate = slots.Count > 0 && slots.All(slot => slot.TryGetValue("taskId", out var taskId) &&
            controls.GetValueOrDefault(new TaskId(Id(taskId))) is { } control && control.TryGetValue("terminate", out var flag) &&
            flag is JsonBooleanValue { Value: true });
        var added = controls.Values.SelectMany(control => control is not null && control.TryGetValue("addTools", out var names) && names is JsonArrayValue list
            ? list.OfType<JsonStringValue>().Select(name => name.Value) : []).ToList();
        // The last handoff in call order wins.
        var handoff = controls.Values.LastOrDefault(control => control is not null && control.TryGetValue("handoff", out var text) && text is JsonStringValue)
            is { } last ? ((JsonStringValue)last["handoff"]).Value : null;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var boundary = await Inbox.PrepareBoundaryAsync(tx, conversationId, runtime.Settings).ConfigureAwait(false);
            if (added.Count > 0) await Agents.AddToolsAsync(tx, conversationId, added).ConfigureAwait(false);
            var state = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var now = runtime.Now();
            if (terminate || handoff is not null)
            {
                if (handoff is not null)
                {
                    var message = JsonValue.Object([
                        KeyValuePair.Create("role", (JsonValue)"user"),
                        KeyValuePair.Create("content", (JsonValue)handoff),
                        KeyValuePair.Create("timestamp", (JsonValue)now),
                    ]);
                    var entry = await tx.AppendEntryAsync(Entries.ResetEntry, conversationId,
                        new TypedEntryDraft { Head = EntryHead.Self, Model = JsonValue.Array(message) }).ConfigureAwait(false);
                    boundary.Head = entry.Id;
                }
                var (users, _) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, now).ConfigureAwait(false);
                Live.EndRun(tx, state, runtime.TaskId, SubmissionSettlement.Done(assistant));
                if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, state, users).ConfigureAwait(false);
            }
            else
            {
                var (users, reset) = await Inbox.ApplyBoundaryAsync(tx, boundary, false, now).ConfigureAwait(false);
                if (reset)
                {
                    // The queued reset cut the run's context before an answer.
                    Live.EndRun(tx, state, runtime.TaskId, SubmissionSettlement.Unanswered("reset"));
                    if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, state, users).ConfigureAwait(false);
                }
                else
                {
                    state.Delete("tools");
                    if (HarnessTasks.RunTaskId(state) == runtime.TaskId.Value)
                        state.GetObject("run").GetArray("inputs").AddRange(users.Select(id => (JsonValue)id.ToJson()));
                    HarnessTasks.HandOver(state, runtime.TaskId, await HarnessTasks.CreateGenerationAsync(tx, conversationId).ConfigureAwait(false));
                }
            }
            return (TaskState?)NextTaskState.Completed(EntryResult(assistant));
        }, context).ConfigureAwait(false);
    }

    // ─── JSON helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// pi-ai's assistant message key order, as its faux provider builds the message (<c>fauxAssistantMessage</c>): the optional
    /// <c>deferred</c>, <c>errorMessage</c> and <c>responseId</c> come before <c>timestamp</c>. PiSharp.Contracts' wire writer puts them after
    /// it; the order is restored here, at the durable boundary, so a persisted entry is upstream's text. Keys not listed keep their order after.
    /// </summary>
    private static readonly ImmutableArray<string> AssistantKeyOrder =
        ["role", "content", "api", "provider", "model", "usage", "stopReason", "deferred", "errorMessage", "responseId", "timestamp"];

    /// <summary>A provider message as pi-ai wire JSON. A non-finite usage count is not strict JSON (Chord's copyJson error).</summary>
    internal static JsonObjectValue ToWire(AssistantMessage message)
    {
        var usage = message.Usage;
        if (!double.IsFinite(usage.Input) || !double.IsFinite(usage.Output) || !double.IsFinite(usage.CacheRead) ||
            !double.IsFinite(usage.CacheWrite) || !double.IsFinite(usage.TotalTokens))
            throw new ArgumentException("Value contains a non-finite number and is not strict JSON");
        return RecordJson.Ordered((JsonObjectValue)JsonValue.FromData(PiWireJson.WriteMessage(message)), AssistantKeyOrder);
    }

    /// <summary>The settings' request options as the JSON a <c>request</c> checkpoint pins, in upstream's
    /// <c>ConversationStreamOptions</c> field order.</summary>
    internal static JsonObjectValue StreamOptionsJson(ConversationStreamOptions options) => new RecordJson.Writer()
        .Add("transport", options.Transport)
        .Number("timeoutMs", options.TimeoutMs)
        .Number("maxRetries", options.MaxRetries)
        .Number("maxRetryDelayMs", options.MaxRetryDelayMs)
        .Add("headers", options.Headers is { } headers
            ? JsonValue.Object(headers.Select(header => KeyValuePair.Create(header.Key, (JsonValue)header.Value))) : null)
        .Add("metadata", options.Metadata)
        .Add("cacheRetention", options.CacheRetention)
        .Add("deferred", options.Deferred)
        .Build();

    /// <summary>The request options a pinned <c>streamOptions</c> value carries.</summary>
    internal static DurableStreamOptions StreamOptions(JsonObjectValue pinned) => new()
    {
        Transport = OptionalString(pinned, "transport"),
        TimeoutMs = OptionalNumber(pinned, "timeoutMs"),
        MaxRetries = OptionalNumber(pinned, "maxRetries") is { } retries ? (int)retries : null,
        MaxRetryDelayMs = OptionalNumber(pinned, "maxRetryDelayMs"),
        Headers = pinned.TryGetValue("headers", out var headers) && headers is JsonObjectValue values
            ? values.Where(header => header.Value is JsonStringValue).ToDictionary(header => header.Key, header => ((JsonStringValue)header.Value).Value)
            : null,
        Metadata = pinned.TryGetValue("metadata", out var metadata) && metadata is JsonObjectValue data ? data : null,
        CacheRetention = OptionalString(pinned, "cacheRetention"),
        Deferred = pinned.TryGetValue("deferred", out var deferred) ? deferred : null,
    };

    private static JsonObjectValue Prepare(int attempt, TaskId? compacted, string? overflow, bool threshold = false) => new RecordJson.Writer()
        .Add("phase", "prepare").Number("attempt", attempt).Add("compacted", compacted?.ToJson()).Add("overflow", overflow)
        .Add("threshold", threshold ? (JsonValue)true : null).Build();

    /// <summary>Error text for a generation whose blocking compaction ended without completing (upstream <c>compactionFailure</c>).</summary>
    private static string CompactionFailure(TaskOutcome outcome) => outcome switch
    {
        FailedTaskOutcome failed => $"Compaction failed: {failed.Error.Message}",
        FaultedTaskOutcome faulted => $"Compaction failed: {faulted.Error.Message}",
        OrphanedTaskOutcome orphaned => $"Compaction failed: {orphaned.Reason}",
        _ => "Compaction was aborted",
    };

    private static JsonObjectValue ToolsCheckpoint(EntryId assistant, IEnumerable<TaskId> tools, IEnumerable<string> pending) => new RecordJson.Writer()
        .Add("phase", "tools")
        .Add("assistant", assistant.ToJson())
        .Add("tools", JsonValue.Array(tools.Select(id => (JsonValue)id.ToJson())))
        .Add("pending", JsonValue.Array(pending.Select(id => (JsonValue)id)))
        .Build();

    private static JsonObjectValue GenerationState(int attempt) => new RecordJson.Writer().Number("attempt", attempt).Build();

    private static JsonObjectValue EntryResult(EntryId entryId) => JsonValue.Object([KeyValuePair.Create("entryId", (JsonValue)entryId.ToJson())]);

    private static JsonObjectValue Reason(string reason) => JsonValue.Object([KeyValuePair.Create("reason", (JsonValue)reason)]);

    private static List<JsonObjectValue> ToolCalls(JsonObjectValue message) =>
        message.TryGetValue("content", out var content) && content is JsonArrayValue blocks
            ? [.. blocks.OfType<JsonObjectValue>().Where(block => Str(block, "type") == "toolCall")]
            : [];

    private static int Attempt(JsonObjectValue checkpoint) => (int)((JsonNumberValue)checkpoint["attempt"]).Value;

    private static TaskId? OptionalTask(JsonObjectValue checkpoint, string key) =>
        checkpoint.TryGetValue(key, out var id) && id is JsonNumberValue ? new TaskId(Id(id)) : null;

    private static long Id(JsonValue value) => (long)((JsonNumberValue)value).Value;

    private static string? Str(JsonObjectValue value, string key) => value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : null;

    private static string? OptionalString(JsonObjectValue value, string key) => Str(value, key);

    private static double? OptionalNumber(JsonObjectValue value, string key) =>
        value.TryGetValue(key, out var field) && field is JsonNumberValue number ? number.Value : null;
}

/// <summary>The timers of a task runtime: the Harness TimeProvider, which drives the partial throttle as upstream's setTimeout.</summary>
internal interface ITaskTimeSource
{
    /// <summary>The Harness TimeProvider.</summary>
    TimeProvider TimeProvider { get; }
}
