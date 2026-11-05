// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/generation.ts. v1.2.0: the prepare checkpoint's
// threshold flag and a failed blocking threshold compaction failing the run with model_error (area j), and tool task v2 (area a): a round's
// tool tasks take {kind: "model", assistant, callId}, a round reads controls only from model-issued results (a version 1 result has no
// kind), and handing a round over removes pi.live.nestedTools with its slots. Pi v1.5.0 sync (area harness-options): the threshold check
// and overflow recovery use the per-model compaction policy (upstream 6085986e4, #10942); the citation stays on v1.2.0 until the file's
// other areas land. Pi v1.5.0 sync (area model-requests): classify records the requested thinking level on every provider message it
// handles, and a poll checkpoint pins it (upstream d5b3de22d); a request passes the beforeProviderRequest chain as its onPayload
// (upstream d280e1ffa), only when the phase's agent has such a hook. Pi v1.5.0 sync (area sleep): retry backoffs and deferred polls
// sleep instead of waiting in a running phase, and a retry or poll run before its time (an early wake, or a record an earlier version
// stored as running) sleeps again; poll checks pollAt before the model lookup (f75b3240a, ec1be23eb). The partial throttle arms
// runtime.SetTimer. Pi v1.5.0 sync (area reports): settings.afterFailedRun decides the inputs queued behind a run that fails with
// model_error or no_model (prepareFailure, read before the commit's first table write, and endFailedRun; a30c2a81d); the environment
// builder's failure reports host_error env, a section's extension_error with its extension, and a deferred cancel's host_error models.
// Pi v1.5.0 sync (area telemetry, 2ec55d808): a request, a deferred poll and a deferred cancel pass the phase's span as their telemetry
// parent, and a run's generation is created with its inputs' submission IDs, so its trace is the first admission's and the others link.
// Pi v1.5.0 sync (area partial-commits, da412b039): partial commits append what text and thinking grew by and apply the argument changes
// of their own parse of the streamed argument text (PartialChanges), so a committed partial now carries a streaming tool call's parsed
// arguments, as upstream's does. Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): partial commits are deferred (Live.ProgressCommit),
// and so is the request phase's attempt commit unless it converts an interrupted attempt's partial (e5c6182ba, durable@1.5.0 Changed#5).
// Pi v1.5.0 sync (area run-failure, f3255de76): a run that ends without an answer records pi.live.runFailure (Live.EndRun with a RunEnd):
// model_error with the error text, no_model naming the missing model (its inputs' settlement keeps no detail), aborted and reset;
// StartRunAsync removes it, except for the run afterFailedRun: continue or a reset's queued input starts in the same commit (keepFailure).
// Pi v1.5.0 sync (area restarts, b3a29f111): a request or poll the host died during (TaskRecord.Restarts) is a failed attempt under
// settings.retry, not a resend (Interrupted): the committed partial becomes an aborted entry, the next attempt follows the backoff (a
// poll's polls its deferred response again: the retry checkpoint keeps {model, thinkingLevel?, cutoff, handle} as poll, and its retry
// phase moves to poll), and with no retry left the run ends runFailure interrupted, cancelling a polled deferred response. Every phase
// first ends a generation cut off settings.restartLimit times (EndCutOff): runFailure restart_limit, keeping the partial and cancelling the
// deferred response only at the limit, not past it. The abort handler cancels the deferred response a retry would poll again (DeferredOf).
// The finish and drain phases end themselves too (EndAnsweredCutOffAsync, area run-end). PiSharp-only (decision 0011): the next attempt after
// an interrupted request is that request recovered, so it keeps the stream metadata the request was prepared with (a host's op and
// account, SessionStreamMetadata): the retry checkpoint and the next prepare checkpoint carry it as metadata, pinned only when the
// interrupted request had any, so upstream records keep their shape; the other stream options are read again at the next prepare, as
// upstream's are.
// Pi v1.5.0 sync (area tool-tasks, 1c6928b30, #11043): pi.generation is version 2, and its migrate keeps a version 1 record. Every call of a
// round gets a pi.tool task owned by the generation, in call order: a call to a tool its request did not offer is flagged unoffered: true in
// its task input and its pi.live.tools slot, and its task settles tool_unavailable through afterTool, where v1.2.0 appended its result in the
// round's first commit without a task. A sequential round keeps such a call pending, flagged, until its turn (which a version 1 Harness would
// start unflagged, so it leaves version 2 blocked: task_too_old). The abort handler answers an unstarted flagged call tool_unavailable, not
// aborted, and finishToolRound reads the controls in call order (the slots' order), every slot's counting toward terminate.
// Pi v1.5.0 sync (area admission, fb524dd2e, #11043): a run whose context does not end in a user message or tool results (system messages
// aside) ends before any request, unanswered with nothing_to_answer (FailNothingToAnswerAsync), as a continue after an answer or in an empty
// conversation does; the request commit's cutoff then always finds an entry. StartRunAsync's inputs may include a continue.
// Pi v1.5.0 sync (area live-turns, f6a9db1d8, #11043): a tool round is one turn of its run (pi.live.run.turns, 0 at every run start), and
// the round that reaches the phase agent's maxTurns runs its tools and then ends the run like a failure, unanswered with turn_limit and
// detail "1 turn" or "N turns": settings.afterFailedRun decides the queued inputs, and a continue resumes on the round's results. answer
// decides an onYield continuation from the inbox before the final boundary places anything (TakesContinuation), and the continuation starts
// the count over.
// Pi v1.5.0 sync (area run-end, 38ff99177, 7e15f9601, #11141): a tool round's handoff appends its reset first and then continues the run in
// the new context at the postTools boundary (a terminate round still ends it at the final boundary, and the turn limit and a queued reset
// still end it), with queued writes and selected steers placed after the reset. With an afterRun hook selected (AsksAfterRunAsync), a run
// that answers (a final answer without a taken continuation, or a terminate round) holds pi.live.run in commit A (HoldRun): its inputs
// settle done with the answer, run.inputs becomes [], run.afterRun {}, the presentation goes, and the generation moves to finish
// {phase, run: RunSummary}. finish asks the hooks (a crash there asks again) and checks what they asked for off the line
// (PrepareRunEndTasks: not built-in, installed, initial() run once on the installed definition and starting at one of its phases; a
// rejected request is skipped, and one hook's rejections throw together, reported against its extension); with none it ends the run, else
// it creates them as its children, lists them in run.afterRun.tasks and waits in drain {phase, answer, tasks}, allSettled. drain then
// ends the run in commit B (EndAnsweredRunAsync): the final boundary places what it selects, the run ends done, and selected user items
// start the next run. Cut off restartLimit times in finish or drain, the generation ends completed without its tasks, the run without a
// failure and queued items left queued, marked so the scheduler reports task_interrupted restart_limit (EndedByCrashes). An abort in
// either ends the run without a failure (Live.EndRun). pi.live.run.first is the first entry the boundary that started a run placed
// (BoundaryResult.First), a continue's the failed run's (Submissions.cs), otherwise its first tool-calling answer (StartToolRoundAsync);
// RunSummary.first falls back to the answer. pi.generation stays version 2, as upstream adds the phases without a bump.
//
// Mappings:
// - Checkpoints, live state and results are upstream's JSON, built in upstream's key order: prepare {phase, attempt, compacted?, overflow?},
//   request {phase, attempt, compacted?, model, thinkingLevel, streamOptions, cutoff}, retry {phase, attempt, compacted?, until, poll?:
//   {model, thinkingLevel?, cutoff, handle}}, poll
//   {phase, attempt, compacted?, model, thinkingLevel?, cutoff, handle, pollAt}, tools {phase, assistant, tools, pending}; the result is
//   {entryId}.
// - Provider messages cross IDurableModels as PiSharp.Contracts values and are stored as pi-ai wire JSON (PiWireJson). Stream events are
//   compact frames, not upstream's events carrying a `partial`; the partial is the snapshot of a PiSharp.AI AssistantStreamReducer fed
//   every non-terminal event, which a partial commit converts only after a block starts or ends (PartialMessage: between those, text and
//   thinking grow by appends, as upstream's copyJson shares the provider's strings). A message with a non-finite usage count is not strict
//   JSON and throws Chord's copyJson error, as upstream's copy does; PiWireJson alone would write it as null.
// - The partial throttle's timers are runtime.SetTimer's (HarnessOptions.SetTimer), so fake time drives them as vi fake timers would. Its
//   one commit in flight is tracked under a lock: timer callbacks run on other threads.
// - The invocation signal is ITaskRuntime.Signal: it is passed to the stream and polls as their cancellation token, and
//   `signal.throwIfAborted()` throws OperationCanceledException.
using System.Collections.Immutable;
using System.Text;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the generation task (upstream <c>GenerationTask</c>, kind <c>pi.generation</c>, version 2).</summary>
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
        Version = 2,
        Initial = _ => JsonValue.Parse("""{"phase":"prepare","attempt":1}"""),
        // Version 2 leaves calls to tools not offered in a sequential round's `pending`, flagged `unoffered` in their slots. A version 1
        // Harness would start their tool tasks unflagged, resolving the name, so it leaves version 2 blocked instead. A version 1 checkpoint
        // has no such calls and is kept as it is.
        Migrate = (input, checkpoint, _) => new TaskMigrationResult(input, checkpoint),
        Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
        {
            ["prepare"] = GenerationPhases.PrepareAsync,
            ["request"] = GenerationPhases.RequestAsync,
            ["retry"] = GenerationPhases.RetryAsync,
            ["poll"] = GenerationPhases.PollAsync,
            ["tools"] = GenerationPhases.ToolsAsync,
            ["finish"] = GenerationPhases.FinishAsync,
            ["drain"] = GenerationPhases.DrainAsync,
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

    /// <summary>Starts a run for <paramref name="inputs"/>, placed input and continue submissions, whose first entry is <paramref name="first"/>
    /// (<c>LiveRun.first</c>): a new generation takes <c>pi.live.run</c>, <c>{taskId, inputs, turns: 0, first?}</c> (upstream <c>startRun</c>).
    /// Removes <c>runFailure</c>, unless <paramref name="keepFailure"/>: a run started in the commit that ended the failed one keeps its
    /// failure.</summary>
    internal static async Task StartRunAsync(ITx tx, ConversationId conversationId, DraftObject live, IEnumerable<SubmissionId> inputs,
        EntryId? first, bool keepFailure = false)
    {
        var ids = inputs as IReadOnlyList<SubmissionId> ?? [.. inputs];
        inputs = ids;
        var taskId = await CreateGenerationAsync(tx, conversationId, ids).ConfigureAwait(false);
        live.Set("run", JsonValue.Object([
            KeyValuePair.Create("taskId", (JsonValue)taskId.ToJson()),
            KeyValuePair.Create("inputs", (JsonValue)JsonValue.Array(inputs.Select(id => (JsonValue)id.ToJson()))),
            KeyValuePair.Create("turns", (JsonValue)0),
            .. first is { } start ? [KeyValuePair.Create("first", (JsonValue)start.ToJson())] : Array.Empty<KeyValuePair<string, JsonValue>>(),
        ]));
        if (!keepFailure) live.Delete("runFailure");
    }

    /// <summary>The tool rounds the run owned by <paramref name="taskId"/> took so far (<c>pi.live.run.turns</c>); 0 when another task owns
    /// the run, or none runs.</summary>
    internal static long RunTurns(DraftObject live, TaskId taskId) =>
        RunTaskId(live) == taskId.Value && ((JsonObjectValue)live.Get("run")!).TryGetValue("turns", out var turns) && turns is JsonNumberValue count
            ? (long)count.Value : 0;

    /// <summary>Sets the turn count of the run owned by <paramref name="taskId"/>, if it owns the run.</summary>
    internal static void SetRunTurns(DraftObject live, TaskId taskId, long turns)
    {
        if (RunTaskId(live) == taskId.Value) live.GetObject("run").Set("turns", JsonValue.Number(turns));
    }

    /// <summary>A generation owned by its conversation (upstream <c>createGeneration</c>); a run's generation is traced from its inputs'
    /// admissions (<paramref name="submissionIds"/>, upstream <c>TaskCreationOptions.submissionIds</c>).</summary>
    internal static Task<TaskId> CreateGenerationAsync(ITx tx, ConversationId conversationId, IReadOnlyList<SubmissionId>? submissionIds = null)
    {
        var options = new TaskOptions(TaskOwnership.Conversation) { ConversationId = conversationId };
        return tx is Transaction transaction && submissionIds is not null
            ? transaction.CreateTaskAsync(GenerationTask, JsonObjectValue.Empty, options, submissionIds)
            : tx.CreateTaskAsync(GenerationTask, JsonObjectValue.Empty, options);
    }

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
    private sealed record Request(int Attempt, TaskId? Compacted, ModelRef Model, string? ThinkingLevel, EntryId Cutoff)
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
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }
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
        var recovered = RecoveredMetadata(checkpoint);
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
        // The model owes an answer only to a context ending in a user message or tool results; system messages say nothing it answers.
        // Otherwise, as for a continue after an answer or in an empty conversation, end the run before any request.
        var last = view.Messages.LastOrDefault(message => RoleOf(message) != "system");
        if (RoleOf(last) is not ("user" or "toolResult"))
        {
            await FailNothingToAnswerAsync(runtime, context).ConfigureAwait(false);
            return;
        }
        var shown = HarnessPrompt.ReplaySections(view.Messages);
        Env.IExecutionEnv? env = null;
        try
        {
            env = await runtime.EnvAsync(context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (context.IsCancellationRequested) throw;
            runtime.Report(error, ReportFrom.Host(HostCallback.Env));
        }
        var input = new PromptInput { ConversationId = conversationId, Agent = agent, Env = env, Shown = shown, Read = runtime };
        var desired = await HarnessPrompt.RenderSectionsAsync(agent.Sections, input, shown,
            (error, section) => runtime.Report(error, ReportFrom.FromExtension(Agents.SectionExtension(agent, section.Key))), context).ConfigureAwait(false);
        var entries = HarnessPrompt.PlanSystemEntries(view, desired, agent.Tools, runtime.Now());
        var threshold = compacted is null ? ThresholdCompaction(view, entries, resolved.ContextWindow, Compaction.PolicyFor(settings, model)) : null;
        if (threshold == "blocking")
        {
            // Compact first and prepare again; the transcript is unchanged until the compaction appends.
            await runtime.CommitAsync(async (tx, _) =>
            {
                var child = await Compaction.CreateCompactionAsync(tx, conversationId, "threshold", owner: runtime.TaskId).ConfigureAwait(false);
                return (TaskState?)NextTaskState.Waiting(Prepare(attempt, child, null, threshold: true, recovered), [child], JoinPolicy.AllSettled);
            }, context).ConfigureAwait(false);
            return;
        }
        await runtime.CommitAsync(async (tx, _) =>
        {
            // The context ends in a message the model answers, so the conversation has an entry.
            var tail = (await tx.ScanEntriesAsync(new EntryQuery(conversationId), 1).ConfigureAwait(false)).Items[0].Id;
            foreach (var entry in entries) tail = (await tx.AppendEntryAsync(Entries.SystemEntry, conversationId, entry).ConfigureAwait(false)).Id;
            // Checked in this commit, so a compaction admitted during preparation counts.
            if (threshold == "background" && !(await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false)).ContainsKey("compactions"))
                await Compaction.CreateCompactionAsync(tx, conversationId, "threshold").ConfigureAwait(false);
            var request = new RecordJson.Writer()
                .Add("phase", "request")
                .Number("attempt", attempt)
                .Add("compacted", compacted?.ToJson())
                .Add("model", model.ToJson())
                .Add("thinkingLevel", agent.ThinkingLevel)
                .Add("streamOptions", StreamOptionsJson(recovered is null ? settings.Stream : settings.Stream with { Metadata = recovered }))
                // PiSharp-only (decision 0007): pinned only when set, so upstream records keep their shape.
                .Add("serviceTier", agent.ServiceTier)
                .Add("cutoff", tail.ToJson())
                .Build();
            return (TaskState?)NextTaskState.Running(request);
        }, context).ConfigureAwait(false);
    }

    public static async Task RequestAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        // Charging the attempt may itself be what keeps failing, such as converting a huge partial: the limit ends it.
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        // The host stopped while this request was in flight: a failed attempt, not a resend (spec §8.3).
        if (task.Restarts is not null)
        {
            await InterruptedAsync(runtime, checkpoint, context).ConfigureAwait(false);
            return;
        }
        var attempt = Attempt(checkpoint);
        var compacted = OptionalTask(checkpoint, "compacted");
        var reference = ModelRef.FromJson(checkpoint["model"])!;
        var thinkingLevel = ((JsonStringValue)checkpoint["thinkingLevel"]).Value;
        var serviceTier = checkpoint.TryGetValue("serviceTier", out var tier) && tier is JsonStringValue tierText ? tierText.Value : null;
        var streamOptions = checkpoint.TryGetValue("streamOptions", out var pinned) && pinned is JsonObjectValue options ? options : JsonObjectValue.Empty;
        var cutoff = new EntryId(Id(checkpoint["cutoff"]));
        var conversationId = runtime.ConversationId;
        // The attempt shown is presentation, stored like progress (Live.ProgressCommit); converting a partial left by an interrupted attempt
        // appends an entry, which makes the commit immediate.
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await HarnessTasks.ConvertPartialAsync(tx, live, conversationId).ConfigureAwait(false);
            live.Set("generation", GenerationState(attempt));
        }, context, Live.ProgressCommit).ConfigureAwait(false);
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
            ServiceTier = serviceTier,
            OnPayload = await ProviderPayloadAsync(runtime, context).ConfigureAwait(false),
            Telemetry = context.Telemetry,
        };
        var message = await StreamResponseAsync(runtime, model, messages, request, attempt, context).ConfigureAwait(false);
        await ClassifyAsync(runtime, new Request(attempt, compacted, reference, thinkingLevel, cutoff) { Messages = view.Messages }, message, context)
            .ConfigureAwait(false);
    }

    /// <summary>After the backoff's sleep; a run before <c>until</c> (an early wake, or a record of an earlier version) sleeps again.</summary>
    public static async Task RetryAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var attempt = Attempt(checkpoint);
        var compacted = OptionalTask(checkpoint, "compacted");
        var until = ((JsonNumberValue)checkpoint["until"]).Value;
        var poll = checkpoint.TryGetValue("poll", out var polled) && polled is JsonObjectValue deferred ? deferred : null;
        var recovered = RecoveredMetadata(checkpoint);
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }
        if (until > runtime.Now())
        {
            await runtime.SleepUntilAsync(until, checkpoint, context).ConfigureAwait(false);
            return;
        }
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            // A poll the host died during polls its response again, as the next attempt.
            if (poll is not null)
            {
                var pollAt = runtime.Now();
                live.Set("generation", new RecordJson.Writer().Number("attempt", attempt + 1)
                    .Add("deferred", JsonValue.Object([KeyValuePair.Create("pollAt", (JsonValue)pollAt)])).Build());
                var next = new RecordJson.Writer().Add("phase", "poll").Number("attempt", attempt + 1).Add("compacted", compacted?.ToJson());
                foreach (var (key, value) in poll) next.Add(key, value);
                return (TaskState?)NextTaskState.Running(next.Number("pollAt", pollAt).Build());
            }
            live.Set("generation", GenerationState(attempt + 1));
            return (TaskState?)NextTaskState.Running(Prepare(attempt + 1, compacted, null, metadata: recovered));
        }, context).ConfigureAwait(false);
    }

    /// <summary>After the sleep until <c>pollAt</c>; a run before it (an early wake, or a record of an earlier version) sleeps again.</summary>
    public static async Task PollAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var pollAt = ((JsonNumberValue)checkpoint["pollAt"]).Value;
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }
        // The host stopped while the poll's fetch was in flight: a failed attempt, as for a request. Checked before the time, which may have
        // moved backwards on a replacement host; a sleeping poll is never cut off.
        if (task.Restarts is not null)
        {
            await InterruptedAsync(runtime, checkpoint, context).ConfigureAwait(false);
            return;
        }
        if (pollAt > runtime.Now())
        {
            await runtime.SleepUntilAsync(pollAt, checkpoint, context).ConfigureAwait(false);
            return;
        }
        var reference = ModelRef.FromJson(checkpoint["model"])!;
        var model = runtime.Models.GetModel(reference.Provider, reference.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, reference, context).ConfigureAwait(false);
            return;
        }
        var handle = new DurableDeferredHandle((JsonObjectValue)checkpoint["handle"]);
        var fetched = await runtime.Models.FetchDeferredAsync(model, handle, new DurableRequestOptions { Telemetry = context.Telemetry },
            runtime.Signal).ConfigureAwait(false);
        // A checkpoint an earlier version wrote has no thinking level, and its answer records none.
        var thinkingLevel = checkpoint.TryGetValue("thinkingLevel", out var level) && level is JsonStringValue levelText ? levelText.Value : null;
        var request = new Request(Attempt(checkpoint), OptionalTask(checkpoint, "compacted"), reference, thinkingLevel,
            new EntryId(Id(checkpoint["cutoff"])))
        {
            PollAt = pollAt,
        };
        await ClassifyAsync(runtime, request, ToWire(fetched), context).ConfigureAwait(false);
    }

    public static async Task ToolsAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }
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
            var slots = live.ContainsKey("tools") && live.Get("tools") is JsonArrayValue ? live.GetArray("tools") : null;
            var index = -1;
            for (var at = 0; slots is not null && at < slots.Count; at++)
            {
                var candidate = (JsonObjectValue)slots.Get(at);
                if (candidate.TryGetValue("callId", out var callId) && callId is JsonStringValue { Value: var id } && id == next && !candidate.ContainsKey("taskId"))
                {
                    index = at;
                    break;
                }
            }
            var unoffered = index >= 0 && ((JsonObjectValue)slots!.Get(index)).TryGetValue("unoffered", out var flag) && flag is JsonBooleanValue { Value: true };
            var taskId = await CreateToolTaskAsync(tx, runtime, assistant, next, unoffered).ConfigureAwait(false);
            if (index >= 0) slots!.GetObject(index).Set("taskId", taskId.ToJson());
            return (TaskState?)NextTaskState.Waiting(ToolsCheckpoint(assistant, [.. tools, taskId], pending.Skip(1)), [taskId], JoinPolicy.AllSettled);
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// The run answered (upstream <c>finish</c>, spec §8.3): asks the <c>afterRun</c> hooks for tasks and creates them, as the generation's
    /// children, in the commit that moves to <c>drain</c>; without any, ends the run now. A crash here asks them again: nothing was created
    /// yet.
    /// </summary>
    public static async Task FinishAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var run = RunSummary.FromJson(((JsonObjectValue)task.Checkpoint)["run"]);
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndAnsweredCutOffAsync(runtime, task, run.Answer, context).ConfigureAwait(false);
            return;
        }
        var prepared = new List<PreparedTask>();
        await runtime.Hooks.EachAsync<GenerationHooks, Func<RunSummary, IHookApi, DurableContext, Task<IReadOnlyList<RunEndTask>?>>>(
            hooks => hooks.AfterRun, async hook =>
            {
                if (await hook(run, runtime, context).ConfigureAwait(false) is { } asked) PrepareRunEndTasks(runtime, asked, prepared);
            }).ConfigureAwait(false);
        if (prepared.Count == 0)
        {
            await EndAnsweredRunAsync(runtime, run.Answer, context).ConfigureAwait(false);
            return;
        }
        await runtime.CommitAsync(async (tx, _) =>
        {
            var tasks = new List<TaskId>();
            foreach (var (definition, input) in prepared)
                tasks.Add(await tx.CreateTaskAsync(definition, input, new TaskOptions(TaskOwnership.Task(runtime.TaskId))).ConfigureAwait(false));
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            if (HarnessTasks.RunTaskId(live) == runtime.TaskId.Value && live.GetObject("run").ContainsKey("afterRun"))
                live.GetObject("run").GetObject("afterRun").Set("tasks", JsonValue.Array(tasks.Select(id => (JsonValue)id.ToJson())));
            var checkpoint = JsonValue.Object([
                KeyValuePair.Create("phase", (JsonValue)"drain"),
                KeyValuePair.Create("answer", (JsonValue)run.Answer.ToJson()),
                KeyValuePair.Create("tasks", (JsonValue)JsonValue.Array(tasks.Select(id => (JsonValue)id.ToJson()))),
            ]);
            return (TaskState?)NextTaskState.Waiting(checkpoint, tasks, JoinPolicy.AllSettled);
        }, context).ConfigureAwait(false);
    }

    /// <summary>The after-run tasks are terminal, whatever their outcomes: ends the run (upstream <c>drain</c>).</summary>
    public static async Task DrainAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var answer = new EntryId(Id(((JsonObjectValue)task.Checkpoint)["answer"]));
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndAnsweredCutOffAsync(runtime, task, answer, context).ConfigureAwait(false);
            return;
        }
        await EndAnsweredRunAsync(runtime, answer, context).ConfigureAwait(false);
    }

    public static async Task AbortAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var phase = task.Phase;
        if (DeferredOf(checkpoint) is { } polled) await CancelDeferredAsync(runtime, polled, context).ConfigureAwait(false);
        var conversationId = runtime.ConversationId;
        // Runs after the round's tool tasks are terminal; calls never started get `aborted` results (spec §8.5), unoffered ones
        // `tool_unavailable`.
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
                // A call its request did not offer could not have run either way.
                var unoffered = live.ContainsKey("tools") && live.Get("tools") is JsonArrayValue slots && slots.OfType<JsonObjectValue>().Any(slot =>
                    slot.TryGetValue("callId", out var callId) && JsonValue.DeepEquals(callId, call["id"]) &&
                    slot.TryGetValue("unoffered", out var flag) && flag is JsonBooleanValue { Value: true });
                var name = ((JsonStringValue)call["name"]).Value;
                var result = unoffered
                    ? ToolResults.HarnessError("tool_unavailable", $"Tool {name} is not available")
                    : ToolResults.HarnessError("aborted", $"Tool {name} was aborted");
                await ToolResults.AppendToolResultAsync(tx, conversationId, call, result, runtime.Now()).ConfigureAwait(false);
            }
            // In finish and drain the run answered already: it ends without a failure, its inputs stay done.
            Live.EndRun(tx, live, runtime.TaskId, RunEnd.Unanswered(RunFailureReason.Aborted));
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

    /// <summary>A tool task for call <paramref name="callId"/>, owned by the generation; <paramref name="unoffered"/> for a call to a tool its
    /// request did not offer (upstream <c>createToolTask</c>).</summary>
    private static Task<TaskId> CreateToolTaskAsync(ITx tx, ITaskRuntime runtime, EntryId assistant, string callId, bool unoffered)
    {
        var input = new RecordJson.Writer().Add("kind", "model").Add("assistant", assistant.ToJson()).Add("callId", callId);
        if (unoffered) input.Add("unoffered", true);
        return tx.CreateTaskAsync(HarnessTasks.ToolTask, input.Build(), new TaskOptions(TaskOwnership.Task(runtime.TaskId)));
    }

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

    /// <summary>
    /// The host stopped while this attempt's request or poll was in flight (<see cref="TaskRecord.Restarts"/>): a failed attempt under
    /// <c>settings.Retry</c> (upstream <c>interrupted</c>, spec §8.3). The committed partial becomes an aborted entry; the next attempt follows
    /// the backoff, a poll's by polling its deferred response again, or, with no retry left, the run ends <c>interrupted</c>.
    /// </summary>
    private static async Task InterruptedAsync(ITaskRuntime runtime, JsonObjectValue checkpoint, DurableContext context)
    {
        var attempt = Attempt(checkpoint);
        var compacted = OptionalTask(checkpoint, "compacted");
        var policy = runtime.Settings.Retry;
        var retry = policy.Enabled && attempt <= policy.MaxRetries;
        var until = retry ? runtime.Now() + PiAiRetry.RetryDelayMs(policy.BaseDelayMs, policy.MaxAgentDelayMs, attempt) : 0;
        var polled = Str(checkpoint, "phase") == "poll" ? checkpoint : null;
        var text = polled is null
            ? $"The host stopped while the model was answering, on attempt {attempt}"
            : $"The host stopped while the deferred answer was fetched, on attempt {attempt}";
        // A run that ends does not leave the provider's job running.
        if (!retry && polled is not null) await CancelDeferredAsync(runtime, polled, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            // Read before the first table write; only a failure uses it.
            var failure = retry ? null : await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            await HarnessTasks.ConvertPartialAsync(tx, live, runtime.ConversationId).ConfigureAwait(false);
            if (failure is null)
            {
                var retryState = JsonValue.Object([
                    KeyValuePair.Create("at", (JsonValue)until),
                    KeyValuePair.Create("error", (JsonValue)text),
                ]);
                live.Set("generation", new RecordJson.Writer().Number("attempt", attempt).Add("retry", retryState).Build());
                var poll = polled is null ? null : new RecordJson.Writer().Add("model", polled["model"])
                    .Add("thinkingLevel", polled.TryGetValue("thinkingLevel", out var level) ? level : null)
                    .Add("cutoff", polled["cutoff"]).Add("handle", polled["handle"]).Build();
                // PiSharp-only (decision 0011): the request's stream metadata, for the next attempt (see the file header).
                var metadata = polled is null && checkpoint.TryGetValue("streamOptions", out var pinned) && pinned is JsonObjectValue options &&
                    options.TryGetValue("metadata", out var data) && data is JsonObjectValue kept ? kept : null;
                var next = new RecordJson.Writer().Add("phase", "retry").Number("attempt", attempt).Add("compacted", compacted?.ToJson())
                    .Number("until", until).Add("poll", poll).Add("metadata", metadata).Build();
                return (TaskState?)NextTaskState.Sleeping(next, until);
            }
            await EndFailedRunAsync(tx, runtime, live, failure, RunEnd.Unanswered(RunFailureReason.Interrupted, text)).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Failed(text, Reason("interrupted"));
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a run whose generation was cut off <c>settings.RestartLimit</c> times in a row (upstream <c>endCutOff</c>, spec §5.4). At the
    /// limit it still keeps a committed partial as an aborted entry and cancels a deferred response it polled; past the limit that cleanup
    /// may be what cut it off, so it ends without it.
    /// </summary>
    private static async Task EndCutOffAsync(ITaskRuntime runtime, RunningTask task, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var text = $"The generation was interrupted {Live.CutOffCount(task)} in its {task.Phase} phase";
        var salvage = Live.Salvageable(task, runtime);
        if (salvage && DeferredOf(checkpoint) is { } polled) await CancelDeferredAsync(runtime, polled, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            if (salvage) await HarnessTasks.ConvertPartialAsync(tx, live, runtime.ConversationId).ConfigureAwait(false);
            await EndFailedRunAsync(tx, runtime, live, failure, RunEnd.Unanswered(RunFailureReason.RestartLimit, text)).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Failed(text, Reason("restart_limit"));
        }, context).ConfigureAwait(false);
    }

    /// <summary>The deferred response a checkpoint polls, or will poll again after a retry's backoff (upstream <c>deferredOf</c>): an object
    /// with its <c>model</c> and <c>handle</c>.</summary>
    private static JsonObjectValue? DeferredOf(JsonObjectValue checkpoint) => Str(checkpoint, "phase") switch
    {
        "poll" => checkpoint,
        "retry" => checkpoint.TryGetValue("poll", out var poll) && poll is JsonObjectValue deferred ? deferred : null,
        _ => null,
    };

    /// <summary>Cancels a deferred response no attempt will poll again; a failure is reported, never thrown (upstream
    /// <c>cancelDeferred</c>).</summary>
    private static async Task CancelDeferredAsync(ITaskRuntime runtime, JsonObjectValue polled, DurableContext context)
    {
        if (ModelRef.FromJson(polled["model"]) is not { } reference || runtime.Models.GetModel(reference.Provider, reference.ModelId) is not { } model)
            return;
        try
        {
            await runtime.Models.CancelDeferredAsync(model, new DurableDeferredHandle((JsonObjectValue)polled["handle"]),
                new DurableRequestOptions { Telemetry = context.Telemetry }, runtime.Signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            runtime.Report(error, ReportFrom.Host(HostCallback.Models));
        }
    }

    /// <summary>Settles the run's inputs <c>unanswered</c> with <c>model_error</c> and fails with <paramref name="text"/> (upstream
    /// <c>failModelError</c>).</summary>
    private static Task FailModelErrorAsync(ITaskRuntime runtime, string text, DurableContext context) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            await EndFailedRunAsync(tx, runtime, live, failure, RunEnd.Unanswered(RunFailureReason.ModelError, text)).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Failed(text, Reason("model_error"));
        }, context);

    /// <summary>Settles the run's inputs <c>unanswered</c> with <c>no_model</c> and fails (upstream <c>failNoModel</c>); the run's failure
    /// names the missing model, and the settlement stays without a detail.</summary>
    private static Task FailNoModelAsync(ITaskRuntime runtime, ModelRef? reference, DurableContext context)
    {
        var message = reference is null ? "No model is configured" : $"Model {reference.Provider}/{reference.ModelId} is not available";
        return runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            await EndFailedRunAsync(tx, runtime, live, failure,
                RunEnd.Unanswered(RunFailureReason.NoModel, message, SubmissionSettlement.Unanswered("no_model"))).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Failed(message, Reason("no_model"));
        }, context);
    }

    /// <summary>Ends the run without a request: its context has nothing the model would answer (upstream <c>failNothingToAnswer</c>).</summary>
    private static Task FailNothingToAnswerAsync(ITaskRuntime runtime, DurableContext context)
    {
        const string message = "The conversation has nothing to answer";
        return runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            await EndFailedRunAsync(tx, runtime, live, failure, RunEnd.Unanswered(RunFailureReason.NothingToAnswer)).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Failed(message, Reason("nothing_to_answer"));
        }, context);
    }

    /// <summary>A model message's <c>role</c>, or null.</summary>
    private static string? RoleOf(JsonValue? message) =>
        message is JsonObjectValue value && value.TryGetValue("role", out var role) && role is JsonStringValue text ? text.Value : null;

    /// <summary>What a failed run's end needs from the table, read before the commit's first table write (upstream <c>Failure</c>).</summary>
    private sealed record Failure(AfterFailedRun Policy, Inbox.Boundary? Boundary);

    /// <summary>Reads <c>settings.afterFailedRun</c>, and for <c>continue</c> prepares the final boundary (upstream
    /// <c>prepareFailure</c>).</summary>
    private static async Task<Failure> PrepareFailureAsync(ITx tx, ITaskRuntime runtime)
    {
        var settings = runtime.Settings;
        var policy = settings.AfterFailedRun;
        if (policy != AfterFailedRun.Continue) return new Failure(policy, null);
        return new Failure(policy, await Inbox.PrepareBoundaryAsync(tx, runtime.ConversationId, settings).ConfigureAwait(false));
    }

    /// <summary>
    /// Ends a failed run as <paramref name="end"/> says, and does with the inputs queued behind it what <c>settings.afterFailedRun</c> says
    /// (upstream <c>endFailedRun</c>, spec §6): keep them, start the next run with them, or withdraw them. A withdrawal leaves queued writes
    /// for later placement. A run <c>continue</c> starts here keeps the failure, which a later run start removes.
    /// </summary>
    private static async Task EndFailedRunAsync(ITx tx, ITaskRuntime runtime, DraftObject live, Failure failure, RunEnd end)
    {
        var conversationId = runtime.ConversationId;
        Live.EndRun(tx, live, runtime.TaskId, end);
        if (failure.Policy == AfterFailedRun.Withdraw) await Inbox.WithdrawQueuedInputsAsync(tx, conversationId, "run_failed").ConfigureAwait(false);
        if (failure.Boundary is not { } boundary) return;
        var (users, first, _) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, runtime.Now()).ConfigureAwait(false);
        if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, live, users, first, keepFailure: true).ConfigureAwait(false);
    }

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
        var policy = Compaction.PolicyFor(settings, reference);
        if (overflow && compacted is null && policy.Enabled)
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
    /// A final answer; the final boundary places queued items (upstream <c>answer</c>, spec §6). The first <c>onYield</c> continuation appends
    /// a user message and hands the run to a successor generation, starting its turn count over, but only when the inbox holds no user item
    /// and no reset (<see cref="TakesContinuation"/>). Otherwise the run's inputs settle <c>done</c>; with <c>afterRun</c> hooks selected the
    /// run waits for the tasks they ask for, in <c>finish</c> and <c>drain</c>, before the final boundary; without, selected user items start
    /// the next run now.
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
        var afterRun = await AsksAfterRunAsync(runtime, context).ConfigureAwait(false);
        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            // Queue modes are read on the Session line, when the boundary is decided.
            var boundary = await Inbox.PrepareBoundaryAsync(tx, conversationId, runtime.Settings).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await HarnessTasks.AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            var result = NextTaskState.Completed(EntryResult(entry.Id));
            var continues = TakesContinuation(continuation, boundary) ? continuation : null;
            if (continues is null && afterRun && HoldRun(tx, live, runtime, RunSummaryReason.Answer, entry.Id, entry.Id) is { } held)
                return (TaskState?)held;
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
                return (TaskState?)result;
            }
            Live.EndRun(tx, live, runtime.TaskId, RunEnd.Done(entry.Id));
            if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, live, users, first).ConfigureAwait(false);
            return (TaskState?)result;
        }, context).ConfigureAwait(false);
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

    /// <summary>A task an <c>afterRun</c> hook asked for, checked and ready to create (upstream <c>PreparedTask</c>).</summary>
    private sealed record PreparedTask(DurableTask Task, JsonValue Input);

    /// <summary>
    /// Checks the tasks one <c>afterRun</c> hook asked for, off the line, and adds those that pass to <paramref name="prepared"/>, so a broken
    /// request is skipped instead of failing the creating commit (upstream <c>prepareRunEndTasks</c>): its kind must be an installed task
    /// that is not built-in, and its definition's <c>Initial</c> must return a JSON checkpoint of one of its phases. The task is created from
    /// the installed definition, as every task runs under it, with that checkpoint, so <c>Initial</c> runs once. Then throws the rejections,
    /// which the hook runner reports against the hook's extension. A C# <see cref="JsonValue"/> input is strict JSON by construction (a
    /// non-finite number cannot be built), so upstream's copyJson check of the input rejects only a missing request, task or input here.
    /// </summary>
    private static void PrepareRunEndTasks(ITaskRuntime runtime, IReadOnlyList<RunEndTask> requests, List<PreparedTask> prepared)
    {
        var rejected = new List<Exception>();
        foreach (var request in requests)
        {
            try
            {
                if (request?.Task is not { } requested) throw new ArgumentException("An afterRun task request names no task");
                var name = requested.Definition.Name;
                if (name.StartsWith("pi.", StringComparison.Ordinal)) throw new InvalidOperationException($"An afterRun hook cannot start the built-in task {name}");
                var installed = runtime.Registry.GetTask(name) ?? throw new InvalidOperationException($"Task {name} is not installed");
                var input = request.Input ?? throw new ArgumentException($"The afterRun request for task {name} has no input; expected strict JSON");
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
        if (rejected.Count > 1) throw new AggregateException($"{rejected.Count} afterRun task requests were rejected", rejected);
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

    /// <summary>
    /// The round's tools are terminal: applies their controls, appends a <c>handoff</c>'s reset, and then ends the run at the final boundary
    /// (<c>terminate</c>), ends it like a failure at the conversation's turn limit (<c>turn_limit</c>), ends it at a queued reset, or hands it
    /// to the next generation at the <c>postTools</c> boundary, in the handoff's new context when there is one (upstream
    /// <c>finishToolRound</c>, spec §8.5). A terminate round with <c>afterRun</c> hooks selected holds the run instead (<see cref="HoldRun"/>).
    /// </summary>
    private static async Task FinishToolRoundAsync(ITaskRuntime runtime, EntryId assistant, IReadOnlyList<TaskId> tools, DurableContext context)
    {
        var conversationId = runtime.ConversationId;
        var controls = new OrderedDictionary<TaskId, JsonObjectValue?>();
        var outcomes = await runtime.OutcomesAsync(tools, context).ConfigureAwait(false);
        for (var index = 0; index < tools.Count; index++)
            // Version 1 results have no kind; every result a generation's round reads is a model-issued call's.
            controls[tools[index]] = outcomes[index] is CompletedTaskOutcome { Result: JsonObjectValue result } &&
                !(result.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "nested" }) &&
                result.TryGetValue("control", out var control) && control is JsonObjectValue value ? value : null;
        var live = await runtime.SnapshotAsync(Live.LiveDoc, conversationId, context).ConfigureAwait(false);
        var slots = live is not null && live.TryGetValue("tools", out var listed) && listed is JsonArrayValue array
            ? array.OfType<JsonObjectValue>().ToList() : [];
        var results = slots.Where(slot => slot.ContainsKey("entry")).Select(slot => new EntryId(Id(slot["entry"]))).ToImmutableArray();
        await runtime.Hooks.EachAsync<GenerationHooks, Func<EntryId, ImmutableArray<EntryId>, IHookApi, DurableContext, Task>>(hooks => hooks.AfterTools,
            hook => hook(assistant, results, runtime, context)).ConfigureAwait(false);
        // In call order, the slots' order; a slot without a task (faulted or orphaned before it got one) has no control.
        var ordered = slots.Select(slot => slot.TryGetValue("taskId", out var taskId) ? controls.GetValueOrDefault(new TaskId(Id(taskId))) : null).ToList();
        // Every call of the round must ask to terminate.
        var terminate = slots.Count > 0 && ordered.All(control => control is not null && control.TryGetValue("terminate", out var flag) &&
            flag is JsonBooleanValue { Value: true });
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
