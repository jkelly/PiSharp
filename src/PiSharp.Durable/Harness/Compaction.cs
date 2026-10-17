// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/compaction.ts.
//
// Mappings:
// - Input, checkpoints and results are upstream's JSON in its key order: input {reason, instructions?}; checkpoints {phase: "select"},
//   {phase: "summarize", attempt, model, thinkingLevel, streamOptions, maxTokens, tail, firstKept} and the same request followed by
//   `until` for {phase: "retry"}; the result is {entryId} (blocking), {submissionId} (conversation-owned) or {} (nothing compacted).
// - The live status is upstream's {taskId, reason, blocking, attempt, retry?: {at, error}} in pi.live.compactions (Live.cs); a failure stays
//   in pi.live.compactionFailure {taskId, reason, error} until a compaction produces a summary.
// - The summarization request crosses IDurableModels.CompleteSimpleAsync with pi-ai wire messages; its answer comes back as a
//   PiSharp.Contracts message and is classified as wire JSON (GenerationPhases.ToWire). `runtime.signal` is ITaskRuntime.Signal, and
//   `signal.throwIfAborted()` throws OperationCanceledException.
// - The beforeCompact hook receives a CompactionRequest (upstream's `compaction` object) and returns a CompactionDecision
//   (upstream's `{decline: true} | {summary}`).
// - JSON.stringify of a tool call argument is JsonValue.ToJson (JavaScript formatting); String.prototype.trim is Js.Trim.
using System.Collections.Immutable;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the compaction task (upstream <c>CompactionTask</c>, kind <c>pi.compaction</c>, version 1).</summary>
public static partial class HarnessTasks
{
    /// <summary>
    /// Built-in compaction task (upstream <c>CompactionTask</c>, spec §8.7): selects an old prefix of the model context, summarizes it, and
    /// places a <c>pi.compaction</c> summary entry whose <c>head</c> is the first kept entry. A compaction its generation owns blocks that
    /// generation and appends directly; a conversation-owned one places its summary through a write submission. Its input is
    /// <c>{reason, instructions?}</c>; it starts at <c>{phase: "select"}</c>.
    /// </summary>
    public static DurableTask CompactionTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.compaction",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"select"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
        {
            ["select"] = Compaction.SelectAsync,
            ["summarize"] = Compaction.SummarizeAsync,
            ["retry"] = Compaction.RetryAsync,
        },
        Abort = Compaction.AbortAsync,
    });
}

/// <summary>The compaction task's phases and the helpers shared with generation (upstream <c>compaction.ts</c>).</summary>
internal static class Compaction
{
    /// <summary>Longest tool result text a serialized summary source keeps.</summary>
    private const int ToolResultMaxChars = 2000;

    private const string SummaryPrefix = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";
    private const string SummarySuffix = "\n</summary>";

    internal const string SummarizationSystemPrompt =
        "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n" +
        "\n" +
        "Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";

    internal const string SummarizationPrompt =
        "The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work. If the conversation starts with an earlier summary, preserve its information and fold the newer messages into it.\n" +
        "\n" +
        "Use this EXACT format:\n" +
        "\n" +
        "## Goal\n" +
        "[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]\n" +
        "\n" +
        "## Constraints & Preferences\n" +
        "- [Any constraints, preferences, or requirements mentioned by user]\n" +
        "- [Or \"(none)\" if none were mentioned]\n" +
        "\n" +
        "## Progress\n" +
        "### Done\n" +
        "- [x] [Completed tasks/changes]\n" +
        "\n" +
        "### In Progress\n" +
        "- [ ] [Current work]\n" +
        "\n" +
        "### Blocked\n" +
        "- [Issues preventing progress, if any]\n" +
        "\n" +
        "## Key Decisions\n" +
        "- **[Decision]**: [Brief rationale]\n" +
        "\n" +
        "## Next Steps\n" +
        "1. [Ordered list of what should happen next]\n" +
        "\n" +
        "## Critical Context\n" +
        "- [Any data, examples, or references needed to continue]\n" +
        "- [Or \"(none)\" if not applicable]\n" +
        "\n" +
        "Keep each section concise. Preserve exact file paths, function names, and error messages.";

    /// <summary>The pinned summarization request (upstream <c>SummaryRequest</c>).</summary>
    private sealed record SummaryRequest(int Attempt, ModelRef Model, string ThinkingLevel, JsonObjectValue StreamOptions, double MaxTokens,
        EntryId Tail, EntryId FirstKept)
    {
        /// <summary><c>{phase, attempt, model, thinkingLevel, streamOptions, maxTokens, tail, firstKept, until?}</c>.</summary>
        public JsonObjectValue Checkpoint(string phase, double? until = null) => new RecordJson.Writer()
            .Add("phase", phase)
            .Number("attempt", Attempt)
            .Add("model", Model.ToJson())
            .Add("thinkingLevel", ThinkingLevel)
            .Add("streamOptions", StreamOptions)
            .Number("maxTokens", MaxTokens)
            .Add("tail", Tail.ToJson())
            .Add("firstKept", FirstKept.ToJson())
            .Number("until", until)
            .Build();

        public static SummaryRequest From(JsonObjectValue checkpoint) => new(
            (int)((JsonNumberValue)checkpoint["attempt"]).Value,
            ModelRef.FromJson(checkpoint["model"]) ?? throw new FormatException("Invalid compaction checkpoint: model"),
            ((JsonStringValue)checkpoint["thinkingLevel"]).Value,
            checkpoint.TryGetValue("streamOptions", out var options) && options is JsonObjectValue pinned ? pinned : JsonObjectValue.Empty,
            ((JsonNumberValue)checkpoint["maxTokens"]).Value,
            new EntryId(Id(checkpoint["tail"])),
            new EntryId(Id(checkpoint["firstKept"])));
    }

    // ─── Phases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the committed context and selects the range (spec §8.7). Nothing to compact completes with <c>{}</c>; otherwise the
    /// <c>beforeCompact</c> hooks decide, or one commit moves to <c>summarize</c> with the pinned request.
    /// </summary>
    public static async Task SelectAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var conversationId = runtime.ConversationId;
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var settings = runtime.Settings;
        var reference = agent.Model;
        var model = reference is null ? null : runtime.Models.GetModel(reference.Provider, reference.ModelId);
        if (reference is null || model is null)
        {
            await FailNoModelAsync(runtime, reference, context).ConfigureAwait(false);
            return;
        }
        var policy = settings.Compaction;
        var view = await runtime.ContextAsync(conversationId, context).ConfigureAwait(false);
        if (SelectCut(view, policy.KeepRecentTokens) is not { } cut)
        {
            await CompleteAsync(runtime, context).ConfigureAwait(false);
            return;
        }
        var firstKept = view.Entries[cut].Id;
        var (reason, instructions) = Input(task.Input);
        var compaction = new CompactionRequest
        {
            Reason = reason,
            Entries = view.Entries[..cut],
            Messages = SummarizedMessages(view, cut),
            FirstKept = firstKept,
            Instructions = instructions,
        };
        CompactionDecision? decision = null;
        await runtime.Hooks.EachAsync<CompactionHooks, Func<CompactionRequest, IHookApi, DurableContext, Task<CompactionDecision?>>>(
            hooks => hooks.BeforeCompact, async hook =>
            {
                if (decision is null) decision = await hook(compaction, runtime, context).ConfigureAwait(false);
            }).ConfigureAwait(false);
        if (decision is { Decline: true })
        {
            await CompleteAsync(runtime, context).ConfigureAwait(false);
            return;
        }
        if (decision is { Summary: { } supplied })
        {
            await PlaceAsync(runtime, firstKept, supplied, context).ConfigureAwait(false);
            return;
        }
        var tail = firstKept;
        foreach (var entry in view.Entries) if (entry.Id.Value > tail.Value) tail = entry.Id;
        var request = new SummaryRequest(
            1,
            reference,
            agent.ThinkingLevel,
            GenerationPhases.StreamOptionsJson(settings.Stream),
            Math.Min(Math.Floor(0.8 * policy.ReserveTokens), model.MaxTokens > 0 ? model.MaxTokens : double.PositiveInfinity),
            tail,
            firstKept);
        await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Running(request.Checkpoint("summarize")), context).ConfigureAwait(false);
    }

    /// <summary>Sends the pinned summarization request and classifies its answer in one commit that also records its usage.</summary>
    public static async Task SummarizeAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var request = SummaryRequest.From((JsonObjectValue)task.Checkpoint);
        var model = runtime.Models.GetModel(request.Model.Provider, request.Model.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, request.Model, context).ConfigureAwait(false);
            return;
        }
        // The context at `tail` is immutable, so this is the range `select` chose.
        var view = await runtime.ContextAsync(runtime.ConversationId, context, request.Tail).ConfigureAwait(false);
        var cut = -1;
        for (var index = 0; index < view.Entries.Length; index++)
        {
            if (view.Entries[index].Id != request.FirstKept) continue;
            cut = index;
            break;
        }
        var now = runtime.Now();
        var (_, instructions) = Input(task.Input);
        ImmutableArray<JsonValue> messages =
        [
            JsonValue.Object([
                KeyValuePair.Create("role", (JsonValue)"system"),
                KeyValuePair.Create("content", (JsonValue)SummarizationSystemPrompt),
                KeyValuePair.Create("timestamp", (JsonValue)now),
            ]),
            JsonValue.Object([
                KeyValuePair.Create("role", (JsonValue)"user"),
                KeyValuePair.Create("content", (JsonValue)JsonValue.Array(JsonValue.Object([
                    KeyValuePair.Create("type", (JsonValue)"text"),
                    KeyValuePair.Create("text", (JsonValue)SummaryPrompt(SummarizedMessages(view, cut), instructions)),
                ]))),
                KeyValuePair.Create("timestamp", (JsonValue)now),
            ]),
        ];
        var options = GenerationPhases.StreamOptions(request.StreamOptions) with
        {
            Deferred = null,
            CacheRetention = "none",
            MaxTokens = request.MaxTokens,
            SessionId = await ProviderIdentity.EnsureProviderSessionIdAsync(runtime, context).ConfigureAwait(false),
            Reasoning = request.ThinkingLevel == "off" ? null : request.ThinkingLevel,
        };
        var answer = await runtime.Models.CompleteSimpleAsync(model, messages, options, runtime.Signal).ConfigureAwait(false);
        // An abort mark or close: the abort invocation or the reopened task handles the committed state.
        runtime.Signal.ThrowIfCancellationRequested();
        var message = GenerationPhases.ToWire(answer);
        var summary = SummaryText(message);
        var policy = runtime.Settings.Retry;
        var retry = Str(message, "stopReason") == "error" && PiAiRetry.IsRetryableAssistantError(message) && policy.Enabled &&
            request.Attempt <= policy.MaxRetries;
        var until = retry ? runtime.Now() + PiAiRetry.RetryDelayMs(policy.BaseDelayMs, policy.MaxAgentDelayMs, request.Attempt) : 0;
        await runtime.CommitAsync(async (tx, current) =>
        {
            await UsageLedger.RecordUsageAsync(tx, runtime.ConversationId, "models", $"{Text(message, "provider")}/{Text(message, "model")}",
                (JsonObjectValue)message["usage"]).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            if (summary is not null) return await PlaceSummaryAsync(tx, runtime, current, live, request.FirstKept, summary).ConfigureAwait(false);
            if (retry)
            {
                if (Live.CompactionStatus(live, runtime.TaskId) is { } status)
                    status.Set("retry", JsonValue.Object([
                        KeyValuePair.Create("at", (JsonValue)until),
                        KeyValuePair.Create("error", (JsonValue)(Str(message, "errorMessage") ?? "")),
                    ]));
                return NextTaskState.Running(request.Checkpoint("retry", until));
            }
            var text = SummaryFailure(message);
            Live.FailCompactionStatus(live, runtime.TaskId, text);
            return (TaskState?)NextTaskState.Failed(text, Reason("model_error"));
        }, context).ConfigureAwait(false);
    }

    /// <summary>Sleeps until <c>until</c>, then returns to <c>summarize</c> with the next attempt and the same pinned request.</summary>
    public static async Task RetryAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var request = SummaryRequest.From(checkpoint);
        await runtime.SleepAsync(((JsonNumberValue)checkpoint["until"]).Value, context).ConfigureAwait(false);
        var attempt = request.Attempt + 1;
        await runtime.CommitAsync(async (tx, _) =>
        {
            if (Live.CompactionStatus(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId) is { } status)
            {
                status.Set("attempt", attempt);
                status.Delete("retry");
            }
            return (TaskState?)NextTaskState.Running((request with { Attempt = attempt }).Checkpoint("summarize"));
        }, context).ConfigureAwait(false);
    }

    /// <summary>Removes the status and ends <c>aborted</c>.</summary>
    public static Task AbortAsync(RunningTask task, ITaskRuntime runtime, DurableContext context) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            Live.RemoveCompactionStatus(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId);
            return (TaskState?)NextTaskState.Aborted();
        }, context);

    // ─── Creation and range selection ───────────────────────────────────────

    /// <summary>
    /// Creates a compaction task with its status in this commit (upstream <c>createCompaction</c>). <paramref name="owner"/> is the
    /// generation that waits for it (a blocking compaction); without one it is conversation-owned, and background unless it is manual. The
    /// input is <c>{reason, instructions?}</c>.
    /// </summary>
    public static async Task<TaskId> CreateCompactionAsync(ITx tx, ConversationId conversationId, string reason, string? instructions = null,
        TaskId? owner = null)
    {
        var ownership = owner is { } taskId ? TaskOwnership.Task(taskId) : TaskOwnership.Conversation;
        var background = owner is null && reason != "manual";
        var input = new RecordJson.Writer().Add("reason", reason).Add("instructions", instructions).Build();
        var options = new TaskOptions(ownership) { ConversationId = conversationId, Background = background };
        var id = await tx.CreateTaskAsync(HarnessTasks.CompactionTask, input, options).ConfigureAwait(false);
        var status = new RecordJson.Writer().Add("taskId", id.ToJson()).Add("reason", reason).Add("blocking", owner is not null)
            .Number("attempt", 1).Build();
        Live.AddCompactionStatus(await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false), status);
        return id;
    }

    /// <summary>
    /// Index in <c>view.Entries</c> of the first entry a summary keeps, or null when there is nothing to compact (upstream <c>selectCut</c>,
    /// spec §8.7). Walks back from the tail until <paramref name="keepRecentTokens"/> are kept, then cuts at the first candidate at or after
    /// that entry: an entry whose contribution starts with a user or assistant message, never a tool result, and never a user entry that a
    /// result of the preceding assistant's calls still follows.
    /// </summary>
    public static int? SelectCut(ContextView view, double keepRecentTokens)
    {
        var contributions = view.Contributions;
        var start = view.Head is null ? 0 : 1;
        var candidates = new List<int>();
        for (var index = start; index < contributions.Length; index++) if (IsCandidate(contributions, index)) candidates.Add(index);
        double kept = 0;
        int? cut = null;
        for (var index = contributions.Length - 1; index >= start; index--)
        {
            foreach (var message in contributions[index]) kept += PiAiEstimate.EstimateMessageTokens(message);
            if (kept < keepRecentTokens) continue;
            var at = index;
            cut = candidates.Where(candidate => candidate >= at).Select(candidate => (int?)candidate).FirstOrDefault() ??
                (candidates.Count > 0 ? candidates[^1] : null);
            break;
        }
        if (cut is not { } found) return null;
        for (var index = start; index < found; index++) if (contributions[index].Length > 0) return found;
        return null;
    }

    private static bool IsCandidate(ImmutableArray<ImmutableArray<JsonValue>> contributions, int index)
    {
        var first = contributions[index].IsEmpty ? null : contributions[index][0];
        if (Role(first) == "assistant") return true;
        if (Role(first) != "user") return false;
        // A result of the preceding assistant's calls that follows this entry, before the next assistant, belongs before it.
        var calls = new HashSet<string>(StringComparer.Ordinal);
        for (var before = index - 1; before >= 0; before--)
        {
            var assistant = contributions[before].LastOrDefault(message => Role(message) == "assistant");
            if (assistant is null) continue;
            foreach (var content in Blocks((JsonObjectValue)assistant))
                if (Str(content, "type") == "toolCall" && Str(content, "id") is { } id) calls.Add(id);
            break;
        }
        if (calls.Count == 0) return true;
        for (var after = index; after < contributions.Length; after++)
        {
            for (var position = 0; position < contributions[after].Length; position++)
            {
                var message = contributions[after][position];
                if (Role(message) == "assistant" && (after > index || position > 0)) return true;
                if (Role(message) == "toolResult" && Str((JsonObjectValue)message, "toolCallId") is { } callId && calls.Contains(callId)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Model messages of the entries before <paramref name="cut"/>: the head marker first, ordered like model context (upstream
    /// <c>summarizedMessages</c>, spec §2.1). A negative cut counts from the end, as <c>Array.prototype.slice</c> does.
    /// </summary>
    public static ImmutableArray<JsonValue> SummarizedMessages(ContextView view, int cut)
    {
        var end = cut < 0 ? Math.Max(0, view.Contributions.Length + cut) : Math.Min(cut, view.Contributions.Length);
        return ConversationContext.OrderToolResults([.. view.Contributions[..end].SelectMany(messages => messages)]);
    }

    /// <summary>
    /// Size of a request over <paramref name="view"/> followed by <paramref name="extra"/> (upstream <c>estimateContext</c>, spec §8.3): the
    /// usage of the newest assistant appended after the head marker, whose request included the marker, plus estimates of the messages after
    /// it; without one, estimates of every message.
    /// </summary>
    public static double EstimateContext(ContextView view, IEnumerable<JsonValue> extra)
    {
        JsonObjectValue? measured = null;
        var after = view.Head?.Id.Value ?? long.MinValue;
        for (var index = view.Entries.Length - 1; index >= 0 && measured is null; index--)
        {
            if (view.Entries[index].Id.Value <= after) continue;
            measured = view.Contributions[index].OfType<JsonObjectValue>().LastOrDefault(message =>
                Role(message) == "assistant" && message.TryGetValue("usage", out var usage) && usage is JsonObjectValue counts &&
                PiAiEstimate.CalculateContextTokens(counts) > 0);
        }
        var from = 0;
        if (measured is not null)
            for (var index = view.Messages.Length - 1; index >= 0; index--)
                if (ReferenceEquals(view.Messages[index], measured)) { from = index + 1; break; }
        var tokens = measured is null ? 0 : PiAiEstimate.CalculateContextTokens((JsonObjectValue)measured["usage"]);
        for (var index = from; index < view.Messages.Length; index++) tokens += PiAiEstimate.EstimateMessageTokens(view.Messages[index]);
        foreach (var message in extra) tokens += PiAiEstimate.EstimateMessageTokens(message);
        return tokens;
    }

    // ─── Summaries ──────────────────────────────────────────────────────────

    /// <summary>The summary of a clean <c>stop</c> with text and no tool call; anything else is not a summary.</summary>
    private static string? SummaryText(JsonObjectValue message)
    {
        if (Str(message, "stopReason") != "stop" || Blocks(message).Any(content => Str(content, "type") == "toolCall")) return null;
        var text = Js.Trim(string.Join("\n", Blocks(message).Where(content => Str(content, "type") == "text").Select(content => Text(content, "text"))));
        return text.Length == 0 ? null : text;
    }

    private static string SummaryFailure(JsonObjectValue message)
    {
        var stopReason = Str(message, "stopReason");
        if (stopReason is "error" or "aborted") return $"Summarization failed: {Str(message, "errorMessage") ?? stopReason}";
        if (stopReason == "length") return "Summarization hit the token limit; the summary is incomplete";
        if (Blocks(message).Any(content => Str(content, "type") == "toolCall")) return "Summarization attempted to call a tool";
        return "Summarization produced no text";
    }

    /// <summary>The summarizer's user message: the serialized conversation, the prompt, and any instructions.</summary>
    private static string SummaryPrompt(ImmutableArray<JsonValue> messages, string? instructions)
    {
        var focus = instructions is null ? "" : $"\n\nAdditional focus: {instructions}";
        return $"<conversation>\n{SerializeConversation(messages)}\n</conversation>\n\n{SummarizationPrompt}{focus}";
    }

    /// <summary>Messages as plain text, so the summarizer reads a transcript instead of continuing it. System messages are omitted (upstream
    /// <c>serializeConversation</c>).</summary>
    public static string SerializeConversation(IEnumerable<JsonValue> messages)
    {
        var parts = new List<string>();
        foreach (var item in messages)
        {
            if (item is not JsonObjectValue message) continue;
            switch (Str(message, "role"))
            {
                case "user":
                {
                    var text = ContentText(message.GetValueOrDefault("content"));
                    if (text.Length > 0) parts.Add($"[User]: {text}");
                    break;
                }
                case "assistant":
                {
                    var blocks = Blocks(message);
                    var text = blocks.Where(content => Str(content, "type") == "text").Select(content => Text(content, "text")).ToList();
                    var calls = blocks.Where(content => Str(content, "type") == "toolCall").Select(Call).ToList();
                    if (text.Count > 0) parts.Add($"[Assistant]: {string.Join("\n", text)}");
                    if (calls.Count > 0) parts.Add($"[Assistant tool calls]: {string.Join("; ", calls)}");
                    break;
                }
                case "toolResult":
                {
                    var text = ContentText(message.GetValueOrDefault("content"));
                    if (text.Length > 0) parts.Add($"[Tool result]: {Truncate(text, ToolResultMaxChars)}");
                    break;
                }
            }
        }
        return string.Join("\n\n", parts);

        static string Call(JsonObjectValue content)
        {
            var arguments = content.TryGetValue("arguments", out var value) && value is JsonObjectValue keyed ? keyed : JsonObjectValue.Empty;
            var rendered = new StringBuilder();
            foreach (var (key, argument) in arguments)
            {
                if (rendered.Length > 0) rendered.Append(", ");
                rendered.Append(key).Append('=').Append(argument.ToJson());
            }
            return $"{Text(content, "name")}({rendered})";
        }
    }

    private static string ContentText(JsonValue? content)
    {
        if (content is JsonStringValue text) return text.Value;
        if (content is not JsonArrayValue blocks) return "";
        return string.Join("\n", blocks.OfType<JsonObjectValue>()
            .Where(block => Str(block, "type") == "text" && block.TryGetValue("text", out var value) && value is JsonStringValue)
            .Select(block => Text(block, "text")));
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : $"{text[..maxChars]}\n\n[... {text.Length - maxChars} more characters truncated]";

    /// <summary>Places a summary supplied by a hook in its own commit.</summary>
    private static Task PlaceAsync(ITaskRuntime runtime, EntryId firstKept, string summary, DurableContext context) =>
        runtime.CommitAsync(async (tx, current) => await PlaceSummaryAsync(tx, runtime, current,
            await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), firstKept, summary).ConfigureAwait(false), context);

    /// <summary>
    /// Places the summary entry and completes (spec §8.7). A blocking compaction, owned by its generation, appends it: the generation holds
    /// the run and waits. A conversation-owned one admits it as a write submission, placed at once when idle, otherwise at the next
    /// boundary, or settled <c>stale</c>.
    /// REMINDER: nothing else may append to a busy conversation, so every non-blocking summary goes through admission.
    /// </summary>
    private static async Task<TaskState?> PlaceSummaryAsync(ITx tx, ITaskRuntime runtime, RunningTask current, DraftObject live, EntryId firstKept,
        string summary)
    {
        Live.RemoveCompactionStatus(live, runtime.TaskId);
        live.Delete("compactionFailure");
        var (reason, _) = Input(current.Input);
        var entry = new EntryDraft
        {
            Kind = Entries.CompactionEntry.Kind,
            Head = EntryHead.At(firstKept),
            Model = JsonValue.Array(JsonValue.Object([
                KeyValuePair.Create("role", (JsonValue)"user"),
                KeyValuePair.Create("content", (JsonValue)JsonValue.Array(JsonValue.Object([
                    KeyValuePair.Create("type", (JsonValue)"text"),
                    KeyValuePair.Create("text", (JsonValue)$"{SummaryPrefix}{summary}{SummarySuffix}"),
                ]))),
                KeyValuePair.Create("timestamp", (JsonValue)runtime.Now()),
            ])),
            Data = JsonValue.Object([KeyValuePair.Create("reason", (JsonValue)reason)]),
        };
        var result = current.Owner is null
            ? new CompactionResult(SubmissionId: await Submissions.AdmitSubmissionAsync(tx, runtime.ConversationId,
                SubmissionDraft.Write(entry, $"compaction:{runtime.TaskId}"), runtime.Now(), runtime.Settings).ConfigureAwait(false))
            : new CompactionResult(EntryId: (await tx.AppendEntryAsync(runtime.ConversationId, entry).ConfigureAwait(false)).Id);
        return NextTaskState.Completed(result.ToJson());
    }

    /// <summary>Removes the status and completes without a summary.</summary>
    private static Task CompleteAsync(ITaskRuntime runtime, DurableContext context) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            Live.RemoveCompactionStatus(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId);
            return (TaskState?)NextTaskState.Completed(JsonObjectValue.Empty);
        }, context);

    private static Task FailNoModelAsync(ITaskRuntime runtime, ModelRef? reference, DurableContext context)
    {
        var message = reference is null ? "No model is configured" : $"Model {reference.Provider}/{reference.ModelId} is not available";
        return runtime.CommitAsync(async (tx, _) =>
        {
            Live.FailCompactionStatus(await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false), runtime.TaskId, message);
            return (TaskState?)NextTaskState.Failed(message, Reason("no_model"));
        }, context);
    }

    // ─── JSON helpers ───────────────────────────────────────────────────────

    /// <summary>The task input <c>{reason, instructions?}</c>.</summary>
    private static (string Reason, string? Instructions) Input(JsonValue input)
    {
        var value = (JsonObjectValue)input;
        return (((JsonStringValue)value["reason"]).Value, Str(value, "instructions"));
    }

    private static JsonObjectValue Reason(string reason) => JsonValue.Object([KeyValuePair.Create("reason", (JsonValue)reason)]);

    private static IEnumerable<JsonObjectValue> Blocks(JsonObjectValue message) =>
        message.TryGetValue("content", out var content) && content is JsonArrayValue blocks ? blocks.OfType<JsonObjectValue>() : [];

    private static long Id(JsonValue value) => (long)((JsonNumberValue)value).Value;

    private static string? Role(JsonValue? message) => message is JsonObjectValue value ? Str(value, "role") : null;

    private static string? Str(JsonObjectValue value, string key) => value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : null;

    /// <summary>A string field as a template literal shows it: <c>undefined</c> when absent.</summary>
    private static string Text(JsonObjectValue value, string key) =>
        value.TryGetValue(key, out var field) ? field is JsonStringValue text ? text.Value : field.ToJson() : "undefined";
}
