using System.Collections.Immutable;
using System.Text;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi;

namespace PiSharp.Durable;

internal static partial class GenerationPhases
{
    /// <summary>
    /// Streams one request and returns the terminal message (upstream <c>streamResponse</c>). Partials commit as trailing writes at most
    /// every <c>progress.partialIntervalMs</c> (default 100 ms) with one commit in flight, deferred (<see cref="Live.ProgressCommit"/>);
    /// <c>finally</c> stops the throttle and awaits that
    /// commit, so no stale partial lands after the outcome. A commit appends what text and thinking grew by and applies the argument
    /// changes since the last one (<see cref="PartialChanges"/>), so its cost follows the new text, not the strings' length.
    /// </summary>
    private static async Task<JsonObjectValue> StreamResponseAsync(ITaskRuntime runtime, DurableModel model, ImmutableArray<JsonValue> messages,
        DurableStreamOptions options, int attempt, DurableContext context)
    {
        var interval = runtime.Settings.Progress.PartialIntervalMs;
        // Guards the reducer, the partial message and the throttle: the stream applies events on its thread, a flush reads them on the
        // timer's.
        var gate = new Lock();
        var reducer = new AssistantStreamReducer(new AssistantMessage(model.Api, model.Provider, model.Id, (long)runtime.Now(), [],
            TokenUsage.Zero, StopReason.Pending));
        var partial = new PartialMessage();
        var changes = new PartialChanges();
        // Whether the partial changed since the last flush took it.
        var pending = false;
        // Disarms the armed timer.
        Action? timer = null;
        Task? inFlight = null;
        var stopped = false;

        // Called under the lock: runtime.SetTimer arms the host's timer, which must not block, and a timer that fires synchronously finds
        // the lock reentrant.
        void Schedule() => timer = runtime.SetTimer(Flush, interval);

        void Flush()
        {
            lock (gate)
            {
                timer = null;
                if (!pending || stopped) return;
                pending = false;
                // Taken now, as upstream copies the partial synchronously: the stream keeps changing it. The changes since the last commit
                // belong to this one.
                PartialCommit? taken = null;
                Exception? failed = null;
                try { taken = changes.Take(partial.Build(reducer)); }
                catch (Exception error) { failed = error; }
                // Started under the lock, so its completion (which takes the lock) always sees itself as the commit in flight.
                inFlight = Task.Run(() => CommitPartialAsync(taken, failed));
            }
        }

        async Task CommitPartialAsync(PartialCommit? taken, Exception? failed)
        {
            try
            {
                if (failed is not null) throw failed;
                try
                {
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
                        if (!live.ContainsKey("generation")) live.Set("generation", GenerationState(attempt));
                        changes.Commit(live.GetObject("generation"), taken!);
                    }, context, Live.ProgressCommit).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    changes.Restore(taken!);
                    throw;
                }
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
                    if (pending && !stopped) Schedule();
                }
            }
        }

        try
        {
            StreamTerminalEvent? terminal = null;
            await foreach (var item in runtime.Models.StreamSimple(model, messages, options, runtime.Signal).WithCancellation(runtime.Signal)
                .ConfigureAwait(false))
            {
                if (item is StreamTerminalEvent end)
                {
                    terminal = end;
                    continue;
                }
                lock (gate)
                {
                    reducer.Apply(item);
                    changes.Observe(item);
                    partial.Observe(item);
                    // A partial without content, such as the opening `start` event, shows nothing; a deferred response never gets past it,
                    // so it never leaves a partial.
                    if (!partial.HasContent) continue;
                    pending = true;
                    if (timer is null && inFlight is null) Schedule();
                }
            }
            if (terminal is null) throw new InvalidOperationException($"The {model.Provider}/{model.Id} stream ended without a terminal event");
            return ToWire(terminal.Message);
        }
        finally
        {
            Task? flushing;
            Action? disarm;
            lock (gate)
            {
                stopped = true;
                disarm = timer;
                timer = null;
                flushing = inFlight;
            }
            disarm?.Invoke();
            if (flushing is not null) await flushing.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The partial message a partial commit writes, as pi-ai wire JSON: PiSharp's counterpart of upstream's <c>copyJson(partial)</c>. That
    /// copy shares the provider's strings, so it costs the message's nodes, not its characters; here the partial is an
    /// <see cref="AssistantStreamReducer"/> snapshot converted with <see cref="ToWire"/>, which copies every string. So after events that
    /// only grew text or thinking, the next message is the last one with the new text appended (a deferred concatenation, see
    /// <see cref="JsonStringValue"/>), and it is built from a snapshot again after any other event: a block that starts or ends, or a
    /// provider replacing what it streamed. A tool call's streamed argument text changes no snapshot (an unfinished call keeps the
    /// arguments it started with; <see cref="PartialChanges"/> commits the parsed ones).
    /// </summary>
    private sealed class PartialMessage
    {
        private readonly Dictionary<int, (string Field, StringBuilder Text)> grown = [];
        private JsonObjectValue? last;
        private bool rebuild = true;

        /// <summary>Whether the message has a block: every event but the opening one starts or changes one.</summary>
        public bool HasContent { get; private set; }

        /// <summary>Record what an event the reducer accepted changed.</summary>
        public void Observe(StreamEvent item)
        {
            switch (item)
            {
                case StreamStarted: return;
                case TextDelta delta: Grow(delta.ContentIndex, "text", delta.Delta); break;
                case ThinkingDelta delta: Grow(delta.ContentIndex, "thinking", delta.Delta); break;
                case ToolCallDelta or ToolCallCheckpoint: break;
                default: rebuild = true; break;
            }
            HasContent = true;
        }

        private void Grow(int index, string field, string delta)
        {
            if (grown.TryGetValue(index, out var text)) text.Text.Append(delta);
            else grown[index] = (field, new StringBuilder(delta));
        }

        /// <summary>The partial message now; called under the stream's lock.</summary>
        public JsonObjectValue Build(AssistantStreamReducer reducer)
        {
            if (rebuild || last is null)
            {
                last = ToWire(reducer.Snapshot());
                rebuild = false;
                grown.Clear();
                return last;
            }
            if (grown.Count == 0) return last;
            var content = ((JsonArrayValue)last["content"]).Items;
            foreach (var (index, (field, text)) in grown)
            {
                var block = (JsonObjectValue)content[index];
                content = content.SetItem(index, block.SetItem(field, JsonStringValue.Concat((JsonStringValue)block[field], text.ToString())));
            }
            grown.Clear();
            last = last.SetItem("content", JsonValue.Array(content));
            return last;
        }
    }

    /// <summary>Classifies a terminal provider message in one commit that also clears the partial (upstream <c>classify</c>).</summary>
    private static async Task ClassifyAsync(ITaskRuntime runtime, Request request, JsonObjectValue message, DurableContext context)
    {
        // An abort mark or close: the abort invocation or the reopened run handles the committed state.
        runtime.Signal.ThrowIfCancellationRequested();
        var conversationId = runtime.ConversationId;
        var (attempt, compacted, reference, thinkingLevel, cutoff) = (request.Attempt, request.Compacted, request.Model, request.ThinkingLevel, request.Cutoff);
        // Record the requested level, as pi-agent-core's agent loop does.
        if (thinkingLevel is not null) message = message.SetItem("thinkingLevel", thinkingLevel);
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
                    .Add("thinkingLevel", thinkingLevel)
                    .Add("cutoff", cutoff.ToJson())
                    .Add("handle", handle)
                    .Number("pollAt", pollAt)
                    .Build();
                return (TaskState?)NextTaskState.Sleeping(checkpoint, pollAt);
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
        var policy = overflow && compacted is null
            ? Compaction.PolicyFor(settings, reference, (await runtime.AgentAsync(context).ConfigureAwait(false)).Compaction)
            : null;
        if (policy?.Enabled == true)
        {
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
            // Read before the first table write; only a failure uses it.
            var failure = retry ? null : await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            if (failure is null)
            {
                var retryState = JsonValue.Object([
                    KeyValuePair.Create("at", (JsonValue)until),
                    KeyValuePair.Create("error", (JsonValue)(Str(message, "errorMessage") ?? "")),
                ]);
                live.Set("generation", new RecordJson.Writer().Number("attempt", attempt).Add("retry", retryState).Build());
                var checkpoint = new RecordJson.Writer().Add("phase", "retry").Number("attempt", attempt).Add("compacted", compacted?.ToJson())
                    .Number("until", until).Build();
                return (TaskState?)NextTaskState.Sleeping(checkpoint, until);
            }
            var text = Str(message, "errorMessage") ?? $"Model response ended with stop reason {stopReason ?? "undefined"}";
            await EndFailedRunAsync(tx, runtime, live, failure, RunEnd.Unanswered(RunFailureReason.ModelError, text)).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Failed(text, Reason("model_error"));
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// A final answer; the final boundary places queued items (upstream <c>answer</c>, spec §6). The first <c>onYield</c> result wins: a
    /// continuation appends a user message and hands the run to a successor generation, starting its turn count over, but only when the
    /// inbox holds no user item and no reset (<see cref="TakesContinuation"/>); tasks decide whether the answer stands, in <c>yield</c>.
    /// Otherwise the run's inputs settle <c>done</c>; with <c>afterRun</c> hooks selected the run waits for the tasks they ask for, in
    /// <c>finish</c> and <c>drain</c>, before the final boundary; without, selected user items start the next run now.
    /// </summary>
    private static async Task AnswerAsync(ITaskRuntime runtime, JsonObjectValue message, DurableContext context)
    {
        var decided = false;
        JsonValue? continuation = null;
        var prepared = new List<PreparedTask>();
        await runtime.Hooks.EachAsync<GenerationHooks, Func<JsonObjectValue, IHookApi, DurableContext, Task<OnYieldResult?>>>(hooks => hooks.OnYield,
            async hook =>
            {
                if (decided) return;
                if (await hook(message, runtime, context).ConfigureAwait(false) is not { } decision) return;
                decided = true;
                if (decision.HookTasks is { } asked) PrepareHookTasks(runtime, asked, prepared);
                else continuation = decision.Continuation;
            }).ConfigureAwait(false);
        var conversationId = runtime.ConversationId;
        if (prepared.Count > 0)
        {
            // The answer waits for its deciders: appended, no boundary, the inputs still open.
            await runtime.CommitAsync(async (tx, _) =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                var entry = await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
                var tasks = await CreateHookTasksAsync(tx, runtime, prepared).ConfigureAwait(false);
                var listed = JsonValue.Array(tasks.Select(id => (JsonValue)id.ToJson()));
                live.Delete("generation");
                if (HarnessTasks.RunTaskId(live) == runtime.TaskId.Value)
                    live.GetObject("run").Set("yield", JsonValue.Object([
                        KeyValuePair.Create("answer", (JsonValue)entry.Id.ToJson()),
                        KeyValuePair.Create("tasks", (JsonValue)listed),
                    ]));
                var checkpoint = JsonValue.Object([
                    KeyValuePair.Create("phase", (JsonValue)"yield"),
                    KeyValuePair.Create("answer", (JsonValue)entry.Id.ToJson()),
                    KeyValuePair.Create("tasks", (JsonValue)listed),
                ]);
                return (TaskState?)NextTaskState.Waiting(checkpoint, tasks, JoinPolicy.AllSettled);
            }, context).ConfigureAwait(false);
            return;
        }
        var afterRun = await AsksAfterRunAsync(runtime, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            // Queue modes are read on the Session line, when the boundary is decided.
            var boundary = await Inbox.PrepareBoundaryAsync(tx, conversationId, runtime.Settings).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            return (TaskState?)await ConcludeAnswerAsync(tx, runtime, live, boundary, entry.Id, continuation, afterRun).ConfigureAwait(false);
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles an answer that stands, or takes its continuation: shared by <see cref="AnswerAsync"/> and the <c>yield</c> phase (upstream
    /// <c>concludeAnswer</c>). A continuation is taken only when the inbox holds no user item and no reset (<see cref="TakesContinuation"/>).
    /// </summary>
    private static async Task<TaskState> ConcludeAnswerAsync(ITx tx, ITaskRuntime runtime, DraftObject live, Inbox.Boundary boundary, EntryId answer,
        JsonValue? continuation, bool afterRun)
    {
        var conversationId = runtime.ConversationId;
        var result = NextTaskState.Completed(EntryResult(answer));
        var continues = TakesContinuation(continuation, boundary) ? continuation : null;
        if (continues is null && afterRun && HoldRun(tx, live, runtime, RunSummaryReason.Answer, answer, answer) is { } held) return held;
        var (users, first, _) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, runtime.Now()).ConfigureAwait(false);
        if (continues is not null)
        {
            var user = JsonValue.Object([
                KeyValuePair.Create("role", (JsonValue)"user"),
                KeyValuePair.Create("content", continues),
                KeyValuePair.Create("timestamp", (JsonValue)runtime.Now()),
            ]);
            await tx.AppendEntryAsync(Entries.UserEntry, conversationId, new TypedEntryDraft { Model = JsonValue.Array(user) }).ConfigureAwait(false);
            // The continuation starts the run's turn count over: bounding onYield loops is the extension's job.
            HarnessTasks.SetRunTurns(live, runtime.TaskId, 0);
            HarnessTasks.HandOver(live, runtime.TaskId, await HarnessTasks.CreateGenerationAsync(tx, conversationId).ConfigureAwait(false));
            live.Delete("generation");
            return result;
        }
        Live.EndRun(tx, live, runtime.TaskId, RunEnd.Done(answer));
        if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, live, users, first).ConfigureAwait(false);
        return result;
    }

    /// <summary>The continuation an <c>onYield</c> task completed with (<see cref="YieldDecision"/>), when it holds a valid user input
    /// (upstream <c>yieldContinuation</c>). Task results are task code's: the trust boundary of the decision's shape.</summary>
    private static JsonValue? YieldContinuation(TaskOutcome outcome)
    {
        if (outcome is not CompletedTaskOutcome { Result: JsonObjectValue result } || !result.TryGetValue("continue", out var input)) return null;
        if (input is JsonStringValue) return input;
        return input is JsonArrayValue { Count: > 0 } items && items.All(IsUserContent) ? input : null;
    }

    /// <summary>A text item with its text, or an image item with its data and MIME type (upstream <c>isUserContent</c>).</summary>
    private static bool IsUserContent(JsonValue item)
    {
        if (item is not JsonObjectValue fields) return false;
        if (Str(fields, "type") == "text") return fields.TryGetValue("text", out var text) && text is JsonStringValue;
        return Str(fields, "type") == "image" && fields.TryGetValue("data", out var data) && data is JsonStringValue &&
            fields.TryGetValue("mimeType", out var mimeType) && mimeType is JsonStringValue;
    }

    /// <summary>An <c>afterTools</c> task's result as an <see cref="AfterToolsDecision"/>: none (JSON <c>null</c>) is one; anything else is
    /// not (upstream <c>afterToolsDecision</c>).</summary>
    private static AfterToolsDecision? AfterToolsDecisionOf(JsonValue? result)
    {
        if (result is JsonNullValue) return new AfterToolsDecision();
        if (result is not JsonObjectValue fields) return null;
        if (fields.Count == 0) return new AfterToolsDecision();
        return fields.Count == 1 && fields.TryGetValue("terminate", out var terminate) && terminate is JsonBooleanValue { Value: true }
            ? new AfterToolsDecision(true)
            : null;
    }

    /// <summary>Whether the phase's agent selects an <c>afterRun</c> hook, so a run that answers waits for its tasks (upstream
    /// <c>asksAfterRun</c>, spec §8.3).</summary>
    private static async Task<bool> AsksAfterRunAsync(ITaskRuntime runtime, DurableContext context) =>
        Agents.AgentHooks(await runtime.AgentAsync(context).ConfigureAwait(false), HarnessTasks.GenerationTask.Definition.Name)
            .Any(hook => hook.Handlers is GenerationHooks { AfterRun: not null });

    /// <summary>
    /// Commit A of a run that answered with <c>afterRun</c> hooks selected (upstream <c>holdRun</c>, spec §8.3): settles the run's inputs
    /// <c>done</c> with the answer and keeps <c>pi.live.run</c> for the after-run tasks, then asks the hooks in <c>finish</c>. The final
    /// boundary waits for the run's end, so queued items stay queued. Null when the generation does not own the run.
    /// </summary>
    private static RunningTaskState? HoldRun(ITx tx, DraftObject live, ITaskRuntime runtime, RunSummaryReason reason, EntryId answer, EntryId last)
    {
        if (HarnessTasks.RunTaskId(live) != runtime.TaskId.Value) return null;
        var run = live.GetObject("run");
        foreach (var id in ((JsonArrayValue)run.GetArray("inputs").ToValue()).Select(id => new SubmissionId(Id(id))))
            tx.SettleSubmission(id, SubmissionSettlement.Done(answer));
        run.Set("inputs", JsonArrayValue.Empty);
        run.Set("afterRun", JsonObjectValue.Empty);
        live.Delete("generation");
        live.Delete("tools");
        live.Delete("nestedTools");
        // A run started before first was recorded has its answer as its first entry.
        var first = ((JsonObjectValue)live.Get("run")!).TryGetValue("first", out var start) && start is JsonNumberValue ? new EntryId(Id(start)) : answer;
        var summary = new RunSummary(reason, first, answer, last);
        return NextTaskState.Running(JsonValue.Object([
            KeyValuePair.Create("phase", (JsonValue)"finish"),
            KeyValuePair.Create("run", (JsonValue)summary.ToJson()),
        ]));
    }

    /// <summary>A task a hook asked for, checked and ready to create (upstream <c>PreparedTask</c>).</summary>
    private sealed record PreparedTask(DurableTask Task, JsonValue Input);

    /// <summary>
    /// Checks the tasks one hook (<c>afterTools</c>, <c>onYield</c>, <c>afterRun</c>) asked for, off the line, and adds those that pass to
    /// <paramref name="prepared"/>, so a broken
    /// request is skipped instead of failing the creating commit (upstream <c>prepareHookTasks</c>): its kind must be an installed task
    /// that is not built-in, and its definition's <c>Initial</c> must return a JSON checkpoint of one of its phases. The task is created from
    /// the installed definition, as every task runs under it, with that checkpoint, so <c>Initial</c> runs once. Then throws the rejections,
    /// which the hook runner reports against the hook's extension. A C# <see cref="JsonValue"/> input is strict JSON by construction (a
    /// non-finite number cannot be built), so upstream's copyJson check of the input rejects only a missing request, task or input here.
    /// </summary>
    private static void PrepareHookTasks(ITaskRuntime runtime, IReadOnlyList<HookTask> requests, List<PreparedTask> prepared)
    {
        var rejected = new List<Exception>();
        foreach (var request in requests)
        {
            try
            {
                if (request?.Task is not { } requested) throw new ArgumentException("A hook task request names no task");
                var name = requested.Definition.Name;
                if (name.StartsWith("pi.", StringComparison.Ordinal)) throw new InvalidOperationException($"A hook cannot start the built-in task {name}");
                var installed = runtime.Registry.GetTask(name) ?? throw new InvalidOperationException($"Task {name} is not installed");
                var input = request.Input ?? throw new ArgumentException($"The hook's request for task {name} has no input; expected strict JSON");
                var definition = installed.Definition;
                var checkpoint = definition.Initial(input);
                var phase = checkpoint is JsonObjectValue fields && fields.TryGetValue("phase", out var named) ? named : null;
                if (phase is not JsonStringValue { Value: var start } || !definition.Phases.ContainsKey(start))
                    throw new InvalidOperationException($"Task {name} starts at the unknown phase {PhaseText(phase)}");
                prepared.Add(new PreparedTask(Tasks.DefineTask(new TaskDefinition
                {
                    Name = definition.Name, Version = definition.Version, Initial = _ => checkpoint, Phases = definition.Phases,
                    Abort = definition.Abort, Migrate = definition.Migrate, Hooks = definition.Hooks,
                }), input));
            }
            catch (Exception error)
            {
                rejected.Add(error);
            }
        }
        if (rejected.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(rejected[0]);
        if (rejected.Count > 1) throw new AggregateException($"{rejected.Count} hook task requests were rejected", rejected);
    }

    /// <summary>Creates the tasks hooks asked for, prepared by <see cref="PrepareHookTasks"/>, as children of the generation (upstream
    /// <c>createHookTasks</c>).</summary>
    private static async Task<List<TaskId>> CreateHookTasksAsync(ITx tx, ITaskRuntime runtime, IReadOnlyList<PreparedTask> prepared)
    {
        var tasks = new List<TaskId>();
        foreach (var (definition, input) in prepared)
            tasks.Add(await tx.CreateTaskAsync(definition, input, new TaskOptions(TaskOwnership.Task(runtime.TaskId))).ConfigureAwait(false));
        return tasks;
    }

    /// <summary>A checkpoint's phase as upstream's <c>String(checkpoint.phase)</c> renders it.</summary>
    private static string PhaseText(JsonValue? phase) => phase switch
    {
        null => "undefined",
        JsonStringValue text => text.Value,
        _ => phase.ToJson(),
    };

    /// <summary>
    /// Commit B of a run that answered (upstream <c>endAnsweredRun</c>, spec §8.3): its after-run tasks are terminal, or there were none.
    /// Places what the final boundary selects, ends the run, and starts the next one for selected user items.
    /// </summary>
    private static Task EndAnsweredRunAsync(ITaskRuntime runtime, EntryId answer, DurableContext context)
    {
        var conversationId = runtime.ConversationId;
        return runtime.CommitAsync(async (tx, _) =>
        {
            var boundary = await Inbox.PrepareBoundaryAsync(tx, conversationId, runtime.Settings).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var (users, first, _) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, runtime.Now()).ConfigureAwait(false);
            Live.EndRun(tx, live, runtime.TaskId, RunEnd.Done(answer));
            if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, live, users, first).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Completed(EntryResult(answer));
        }, context);
    }

    /// <summary>
    /// Ends a run that answered once its <c>finish</c> or <c>drain</c> phase was cut off <c>settings.RestartLimit</c> times (upstream
    /// <c>endAnsweredCutOff</c>, spec §5.4): the run answered, so it ends <c>completed</c>, without a failure and without the tasks it would
    /// still have waited for, marked so the scheduler reports it <c>task_interrupted</c>. Queued items stay queued, as after a failed run.
    /// </summary>
    private static Task EndAnsweredCutOffAsync(ITaskRuntime runtime, RunningTask task, EntryId answer, DurableContext context)
    {
        var text = $"The generation was interrupted {Live.CutOffCount(task)} in its {task.Phase} phase";
        return runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.EndRun(tx, live, runtime.TaskId, RunEnd.Done(answer));
            return (TaskState?)DurableTaskScheduler.EndedByCrashes(NextTaskState.Completed(EntryResult(answer)), text);
        }, context);
    }

    /// <summary>
    /// Whether an answer's <c>onYield</c> continuation is taken (upstream <c>takesContinuation</c>, spec §8.3), decided from the inbox before
    /// the final boundary places anything: only when it holds no user item, of which the boundary would select one, and no reset, which would
    /// otherwise run first. A continuation starts the turn count over.
    /// </summary>
    private static bool TakesContinuation(JsonValue? continuation, Inbox.Boundary boundary)
    {
        if (continuation is null) return false;
        var items = boundary.Inbox.GetArray("items");
        var snapshot = Enumerable.Range(0, items.Count).Select(index => (JsonObjectValue)items.Get(index)).ToList();
        return !snapshot.Any(item => item.TryGetValue("mode", out var mode) && mode is JsonStringValue { Value: "steer" or "followUp" }) &&
            !Inbox.HasQueuedReset(snapshot);
    }

    /// <summary>
    /// Appends the tool-calling answer and starts its tool round in one commit (upstream <c>startToolRound</c>, spec §8.3). Every call gets a
    /// tool task owned by the generation, in call order, only the first one now when the round is sequential; a call to a tool the request did
    /// not offer is flagged, and its task settles <c>tool_unavailable</c>. The generation then waits for them in its <c>tools</c> phase,
    /// keeping the run.
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
                var unoffered = !offered.Contains(Str(call, "name") ?? "");
                if (sequential && tools.Count > 0)
                {
                    pending.Add(Str(call, "id")!);
                    var waiting = new RecordJson.Writer().Add("callId", callId).Add("name", name).Add("status", "pending");
                    if (unoffered) waiting.Add("unoffered", true);
                    slots.Add(waiting.Build());
                    continue;
                }
                var taskId = await CreateToolTaskAsync(tx, runtime, entry.Id, Str(call, "id")!, unoffered).ConfigureAwait(false);
                tools.Add(taskId);
                var started = new RecordJson.Writer().Add("callId", callId).Add("name", name).Add("taskId", taskId.ToJson()).Add("status", "pending");
                if (unoffered) started.Add("unoffered", true);
                slots.Add(started.Build());
            }
            live.Delete("generation");
            live.Set("tools", JsonValue.Array(slots));
            if (HarnessTasks.RunTaskId(live) == runtime.TaskId.Value)
            {
                // A tool round is one turn of the run (AgentState.maxTurns).
                HarnessTasks.SetRunTurns(live, runtime.TaskId, HarnessTasks.RunTurns(live, runtime.TaskId) + 1);
                // A run its boundary started without placing anything, as a continue after an answer, begins here.
                var run = live.GetObject("run");
                if (!run.ContainsKey("first")) run.Set("first", entry.Id.ToJson());
            }
            return (TaskState?)NextTaskState.Waiting(ToolsCheckpoint(entry.Id, tools, pending), tools, JoinPolicy.AllSettled);
        }, context).ConfigureAwait(false);
    }

    /// <summary>The round's tools are terminal: asks the <c>afterTools</c> hooks, and waits for the tasks they ask for in <c>afterTools</c>,
    /// or ends the round now (upstream <c>finishToolRound</c>).</summary>
    private static async Task FinishToolRoundAsync(ITaskRuntime runtime, EntryId assistant, IReadOnlyList<TaskId> tools, DurableContext context)
    {
        var (_, results) = await RoundSlotsAsync(runtime, context).ConfigureAwait(false);
        var prepared = new List<PreparedTask>();
        await runtime.Hooks.EachAsync<GenerationHooks, Func<EntryId, ImmutableArray<EntryId>, IHookApi, DurableContext, Task<IReadOnlyList<HookTask>?>>>(
            hooks => hooks.AfterTools, async hook =>
            {
                if (await hook(assistant, results, runtime, context).ConfigureAwait(false) is { } asked) PrepareHookTasks(runtime, asked, prepared);
            }).ConfigureAwait(false);
        if (prepared.Count == 0)
        {
            await EndToolRoundAsync(runtime, assistant, tools, false, context).ConfigureAwait(false);
            return;
        }
        await runtime.CommitAsync(async (tx, _) =>
        {
            var tasks = await CreateHookTasksAsync(tx, runtime, prepared).ConfigureAwait(false);
            var checkpoint = new RecordJson.Writer()
                .Add("phase", "afterTools")
                .Add("assistant", assistant.ToJson())
                .Add("tools", JsonValue.Array(tools.Select(id => (JsonValue)id.ToJson())))
                .Add("tasks", JsonValue.Array(tasks.Select(id => (JsonValue)id.ToJson())))
                .Build();
            return (TaskState?)NextTaskState.Waiting(checkpoint, tasks, JoinPolicy.AllSettled);
        }, context).ConfigureAwait(false);
    }

    /// <summary>The round's slots in call order, and the result entries they hold (upstream <c>roundSlots</c>).</summary>
    private static async Task<(List<JsonObjectValue> Slots, ImmutableArray<EntryId> Results)> RoundSlotsAsync(ITaskRuntime runtime, DurableContext context)
    {
        var live = await runtime.SnapshotAsync(Live.LiveDoc, runtime.ConversationId, context).ConfigureAwait(false);
        var slots = live is not null && live.TryGetValue("tools", out var listed) && listed is JsonArrayValue array
            ? array.OfType<JsonObjectValue>().ToList() : [];
        return (slots, slots.Where(slot => slot.ContainsKey("entry")).Select(slot => new EntryId(Id(slot["entry"]))).ToImmutableArray());
    }

    /// <summary>
    /// Ends a tool round: applies its controls, appends a <c>handoff</c>'s reset, and then ends the run at the final boundary
    /// (<c>terminate</c>, asked by every call or by an <c>afterTools</c> task), ends it like a failure at the conversation's turn limit
    /// (<c>turn_limit</c>), ends it at a queued reset, or hands it to the next generation at the <c>postTools</c> boundary, in the handoff's
    /// new context when there is one (upstream <c>endToolRound</c>, spec §8.5). A terminate round with <c>afterRun</c> hooks selected holds
    /// the run instead (<see cref="HoldRun"/>).
    /// </summary>
    private static async Task EndToolRoundAsync(ITaskRuntime runtime, EntryId assistant, IReadOnlyList<TaskId> tools, bool taskTerminates,
        DurableContext context)
    {
        var conversationId = runtime.ConversationId;
        var controls = new OrderedDictionary<TaskId, JsonObjectValue?>();
        var outcomes = await runtime.OutcomesAsync(tools, context).ConfigureAwait(false);
        for (var index = 0; index < tools.Count; index++)
            // Version 1 results have no kind; every result a generation's round reads is a model-issued call's.
            controls[tools[index]] = outcomes[index] is CompletedTaskOutcome { Result: JsonObjectValue result } &&
                !(result.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "nested" }) &&
                result.TryGetValue("control", out var control) && control is JsonObjectValue value ? value : null;
        var (slots, results) = await RoundSlotsAsync(runtime, context).ConfigureAwait(false);
        // In call order, the slots' order; a slot without a task (faulted or orphaned before it got one) has no control.
        var ordered = slots.Select(slot => slot.TryGetValue("taskId", out var taskId) ? controls.GetValueOrDefault(new TaskId(Id(taskId))) : null).ToList();
        // Every call of the round must ask to terminate, unless an afterTools task asked for it.
        var terminate = taskTerminates || (slots.Count > 0 && ordered.All(control => control is not null &&
            control.TryGetValue("terminate", out var flag) && flag is JsonBooleanValue { Value: true }));
        var added = ordered.SelectMany(control => control is not null && control.TryGetValue("addTools", out var names) && names is JsonArrayValue list
            ? list.OfType<JsonStringValue>().Select(name => name.Value) : []).ToList();
        // The last handoff in call order wins.
        var handoff = ordered.LastOrDefault(control => control is not null && control.TryGetValue("handoff", out var text) && text is JsonStringValue)
            is { } last ? ((JsonStringValue)last["handoff"]).Value : null;
        // Read from the phase's agent, as every agent field; a configure() during the round applies at its end.
        var maxTurns = (await runtime.AgentAsync(context).ConfigureAwait(false)).MaxTurns;
        var afterRun = terminate && await AsksAfterRunAsync(runtime, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var boundary = await Inbox.PrepareBoundaryAsync(tx, conversationId, runtime.Settings).ConfigureAwait(false);
            if (added.Count > 0) await Agents.AddToolsAsync(tx, conversationId, added).ConfigureAwait(false);
            var state = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var now = runtime.Now();
            // The run's last entry so far: the newest result, or the assistant when no result has an entry.
            var last = results.Aggregate(assistant, (newest, id) => id.Value > newest.Value ? id : newest);
            if (handoff is not null)
            {
                // The handoff's reset precedes every queued item, so queued writes and selected steers land in its context.
                var message = JsonValue.Object([
                    KeyValuePair.Create("role", (JsonValue)"user"),
                    KeyValuePair.Create("content", (JsonValue)handoff),
                    KeyValuePair.Create("timestamp", (JsonValue)now),
                ]);
                var entry = await tx.AppendEntryAsync(Entries.ResetEntry, conversationId,
                    new TypedEntryDraft { Head = EntryHead.Self, Model = JsonValue.Array(message) }).ConfigureAwait(false);
                boundary.Head = entry.Id;
                last = entry.Id;
            }
            var turns = HarnessTasks.RunTurns(state, runtime.TaskId);
            if (afterRun && HoldRun(tx, state, runtime, RunSummaryReason.Terminate, assistant, last) is { } held) return (TaskState?)held;
            if (terminate)
            {
                var (users, first, _) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, now).ConfigureAwait(false);
                Live.EndRun(tx, state, runtime.TaskId, RunEnd.Done(assistant));
                if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, state, users, first).ConfigureAwait(false);
            }
            else if (maxTurns is { } limit && turns >= limit)
            {
                // The turn limit ends the run like a failure: afterFailedRun decides the queued items, and a continue resumes it on the
                // round's results. The table was read for the boundary already, before any write.
                var policy = runtime.Settings.AfterFailedRun;
                await EndFailedRunAsync(tx, runtime, state, new Failure(policy, policy == AfterFailedRun.Continue ? boundary : null),
                    RunEnd.Unanswered(RunFailureReason.TurnLimit,
                        turns == 1 ? "1 turn" : $"{turns.ToString(System.Globalization.CultureInfo.InvariantCulture)} turns")).ConfigureAwait(false);
            }
            else
            {
                var (users, first, reset) = await Inbox.ApplyBoundaryAsync(tx, boundary, false, now).ConfigureAwait(false);
                if (reset)
                {
                    // The queued reset cut the run's context before an answer.
                    Live.EndRun(tx, state, runtime.TaskId, RunEnd.Unanswered(RunFailureReason.Reset));
                    if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, state, users, first, keepFailure: true).ConfigureAwait(false);
                }
                else
                {
                    state.Delete("tools");
                    state.Delete("nestedTools");
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
        .Number("websocketConnectTimeoutMs", options.WebsocketConnectTimeoutMs)
        .Add("thinkingBudgets", options.ThinkingBudgets)
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
        WebsocketConnectTimeoutMs = OptionalNumber(pinned, "websocketConnectTimeoutMs"),
        ThinkingBudgets = pinned.TryGetValue("thinkingBudgets", out var budgets) && budgets is JsonObjectValue thinking ? thinking : null,
        MaxRetries = OptionalNumber(pinned, "maxRetries") is { } retries ? (int)retries : null,
        MaxRetryDelayMs = OptionalNumber(pinned, "maxRetryDelayMs"),
        Headers = pinned.TryGetValue("headers", out var headers) && headers is JsonObjectValue values
            ? values.Where(header => header.Value is JsonStringValue).ToDictionary(header => header.Key, header => ((JsonStringValue)header.Value).Value)
            : null,
        Metadata = pinned.TryGetValue("metadata", out var metadata) && metadata is JsonObjectValue data ? data : null,
        CacheRetention = OptionalString(pinned, "cacheRetention"),
        Deferred = pinned.TryGetValue("deferred", out var deferred) ? deferred : null,
    };

    /// <summary>
    /// The request's <c>onPayload</c> (upstream <c>providerPayload</c>): the beforeProviderRequest hooks chained over each provider request
    /// body, in registry order, each seeing the body the previous left. Null when the phase's agent has none: its hooks are fixed for the
    /// phase, so a chain of none would leave every body unchanged, and the model adapter need not read the body at all.
    /// </summary>
    private static async Task<Func<JsonValue, DurableModel, CancellationToken, Task<JsonValue?>>?> ProviderPayloadAsync(ITaskRuntime runtime,
        DurableContext context)
    {
        var any = false;
        await runtime.Hooks.EachAsync<GenerationHooks, Func<DurableModel, JsonValue, IHookApi, DurableContext, Task<JsonValue?>>>(
            hooks => hooks.BeforeProviderRequest, _ =>
            {
                any = true;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        if (!any) return null;
        return async (payload, model, _) =>
        {
            var current = payload;
            await runtime.Hooks.EachAsync<GenerationHooks, Func<DurableModel, JsonValue, IHookApi, DurableContext, Task<JsonValue?>>>(
                hooks => hooks.BeforeProviderRequest, async hook =>
                {
                    if (await hook(model, current, runtime, context).ConfigureAwait(false) is { } replaced) current = replaced;
                }).ConfigureAwait(false);
            return current;
        };
    }

    private static JsonObjectValue Prepare(int attempt, TaskId? compacted, string? overflow, bool threshold = false, JsonObjectValue? metadata = null) =>
        new RecordJson.Writer()
            .Add("phase", "prepare").Number("attempt", attempt).Add("compacted", compacted?.ToJson()).Add("overflow", overflow)
            .Add("threshold", threshold ? (JsonValue)true : null)
            // PiSharp-only (decision 0011): an interrupted request's stream metadata, for the attempt that recovers it.
            .Add("metadata", metadata).Build();

    /// <summary>The stream metadata a retry or prepare checkpoint carries from an interrupted request (PiSharp-only, decision 0011; see the
    /// file header); null when it carries none.</summary>
    private static JsonObjectValue? RecoveredMetadata(JsonObjectValue checkpoint) =>
        checkpoint.TryGetValue("metadata", out var metadata) && metadata is JsonObjectValue data ? data : null;

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
