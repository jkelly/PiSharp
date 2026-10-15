// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/events.ts (AgentEvent, MessageChange, SnapshotEvent,
// AgentEventStream, watchEvents, translate, messageChanges, toolUpdate).
//
// Mappings: each AgentEvent variant is a sealed record whose ToJson() is upstream's object with upstream's key order, so a JSON-mode host
// prints what upstream prints. pi-ai messages, usage, tool slots, compaction statuses and the pi.agent / pi.usage states are wire JSON, as
// everywhere in this port. Upstream compares view parts with `===`: objects by identity, which the view's structural sharing makes
// meaningful (an untouched part keeps its instance), and strings and numbers by value; Same() below does exactly that.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>
/// Experimental agent event of one conversation, shaped like the coding agent's session events (upstream <c>AgentEvent</c>, spec §9.4). Its
/// protocol may change without notice. <see cref="ToJson"/> is upstream's JSON object.
/// </summary>
public abstract record AgentEvent
{
    private protected AgentEvent() { }

    /// <summary>The event's <c>type</c>, such as <c>"message_update"</c>.</summary>
    public abstract string Type { get; }

    /// <summary>Upstream's JSON object, <c>type</c> first.</summary>
    public abstract JsonObjectValue ToJson();

    /// <summary>A writer that starts with <c>type</c>.</summary>
    private protected RecordJson.Writer Writer() => new RecordJson.Writer().Add("type", Type);

    private protected static JsonArrayValue Ids(ImmutableArray<SubmissionId> ids) => JsonValue.Array(ids.Select(id => (JsonValue)id.ToJson()));
}

/// <summary>A queued inbox item as events show it: <c>{id, mode}</c>, where mode is <c>"steer"</c>, <c>"followUp"</c> or <c>"write"</c>.</summary>
public sealed record QueuedItem(SubmissionId Id, string Mode)
{
    /// <summary><c>{id, mode}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("id", Id.ToJson()).Add("mode", Mode).Build();
}

/// <summary>
/// The current state at attachment, or after an overflow (upstream <c>SnapshotEvent</c>): <c>{type, entries, run?, generation?, tools,
/// compactions, inbox, agent, usage}</c>. Every other event applies on top of it.
/// </summary>
public sealed record SnapshotEvent : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "snapshot";
    /// <summary>The raw active entries.</summary>
    public required ImmutableArray<EntryRecord> Entries { get; init; }
    /// <summary>The inputs of the current run, <c>run.inputs</c>; null without a run.</summary>
    public ImmutableArray<SubmissionId>? RunInputs { get; init; }
    /// <summary>Current generation attempt <c>{attempt, message?, retry?: {at, error}, deferred?: {pollAt}}</c>: its in-flight partial, retry
    /// backoff, or deferred poll.</summary>
    public JsonObjectValue? Generation { get; init; }
    /// <summary>The tool slots of the current round (<c>pi.live.tools</c>).</summary>
    public required JsonArrayValue Tools { get; init; }
    /// <summary><c>pi.live.compactions</c>: live compactions with their attempt and retry backoff.</summary>
    public required JsonArrayValue Compactions { get; init; }
    /// <summary>The queued inbox items.</summary>
    public required ImmutableArray<QueuedItem> Inbox { get; init; }
    /// <summary><c>pi.agent</c>; <c>{}</c> when absent.</summary>
    public required JsonObjectValue Agent { get; init; }
    /// <summary><c>pi.usage</c>; its initial value when absent.</summary>
    public required JsonObjectValue Usage { get; init; }

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer()
        .Add("entries", JsonValue.Array(Entries.Select(entry => (JsonValue)entry.ToJson())))
        .Add("run", RunInputs is { } inputs ? JsonValue.Object([KeyValuePair.Create("inputs", (JsonValue)Ids(inputs))]) : null)
        .Add("generation", Generation).Add("tools", Tools).Add("compactions", Compactions)
        .Add("inbox", JsonValue.Array(Inbox.Select(item => (JsonValue)item.ToJson()))).Add("agent", Agent).Add("usage", Usage).Build();
}

/// <summary><c>{type: "run_start", inputs}</c>: <c>pi.live.run</c> appears, or a successor run with another first input replaces it.</summary>
public sealed record RunStartEvent(ImmutableArray<SubmissionId> Inputs) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "run_start";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("inputs", Ids(Inputs)).Build();
}

/// <summary><c>{type: "run_end", inputs}</c>: <c>pi.live.run</c> is removed or replaced by a successor run with another first input.</summary>
public sealed record RunEndEvent(ImmutableArray<SubmissionId> Inputs) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "run_end";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("inputs", Ids(Inputs)).Build();
}

/// <summary><c>{type: "turn_start"}</c>: a <c>pi.generation</c> task is created and takes the run.</summary>
public sealed record TurnStartEvent : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "turn_start";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Build();
}

/// <summary><c>{type: "turn_end"}</c>: a generation's outcome is committed, at its <c>completing</c> hold or at terminal, whichever comes
/// first.</summary>
public sealed record TurnEndEvent : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "turn_end";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Build();
}

/// <summary><c>{type: "message_start", message}</c>: the first committed partial of an attempt, or a message entry without one. The message
/// is pi-ai wire JSON of any role.</summary>
public sealed record MessageStartEvent(JsonObjectValue Message) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "message_start";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("message", Message).Build();
}

/// <summary><c>{type: "message_update", usage, changes}</c>: changes of the committed partial; <c>usage</c> is the partial's current usage, as
/// in the coding agent's JSON mode.</summary>
public sealed record MessageUpdateEvent(JsonObjectValue Usage, ImmutableArray<MessageChange> Changes) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "message_update";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        Writer().Add("usage", Usage).Add("changes", JsonValue.Array(Changes.Select(change => (JsonValue)change.ToJson()))).Build();
}

/// <summary><c>{type: "message_end", entry}</c>: an entry with <c>model</c> messages was appended.</summary>
public sealed record MessageEndEvent(EntryRecord Entry) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "message_end";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("entry", Entry.ToJson()).Build();
}

/// <summary><c>{type: "tool_execution_start", toolCallId, toolName, args}</c>: a slot becomes <c>running</c>; <c>args</c> come from the tool
/// task's intent checkpoint.</summary>
public sealed record ToolExecutionStartEvent(string ToolCallId, string ToolName, JsonObjectValue Args) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "tool_execution_start";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("toolCallId", ToolCallId).Add("toolName", ToolName).Add("args", Args).Build();
}

/// <summary>
/// <c>{type: "tool_execution_update", toolCallId, toolName, output?, details?, diagnostics?}</c>: the running slot's output window, details or
/// diagnostics changed. <see cref="Details"/> is <see cref="JsonValue.Null"/> and <see cref="Diagnostics"/> empty when a safe replay removed
/// them; a null property is absent.
/// </summary>
public sealed record ToolExecutionUpdateEvent(string ToolCallId, string ToolName, ToolOutputChange? Output = null, JsonValue? Details = null,
    JsonArrayValue? Diagnostics = null) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "tool_execution_update";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("toolCallId", ToolCallId).Add("toolName", ToolName).Add("output", Output?.ToJson())
        .Add("details", Details).Add("diagnostics", Diagnostics).Build();
}

/// <summary>A change of a running tool's retained output window: a front trim and then an append (<see cref="ToolOutputDelta"/>), or its
/// replacement (<see cref="ToolOutputSet"/>).</summary>
public abstract record ToolOutputChange
{
    private protected ToolOutputChange() { }

    /// <summary>The window after this change, from the window before it.</summary>
    public abstract string Apply(string window);

    /// <summary><c>{trimStart?, append?}</c> or <c>{set}</c>.</summary>
    public abstract JsonObjectValue ToJson();
}

/// <summary><c>{trimStart?, append?}</c>: drop <see cref="TrimStart"/> characters from the front, then append <see cref="Append"/>.</summary>
public sealed record ToolOutputDelta(long? TrimStart, string? Append) : ToolOutputChange
{
    /// <inheritdoc/>
    public override string Apply(string window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window[(int)Math.Min(TrimStart ?? 0, window.Length)..] + (Append ?? "");
    }

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Number("trimStart", TrimStart).Add("append", Append).Build();
}

/// <summary><c>{set}</c>: the window is replaced by <see cref="Set"/>.</summary>
public sealed record ToolOutputSet(string Set) : ToolOutputChange
{
    /// <inheritdoc/>
    public override string Apply(string window) => Set;

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("set", Set).Build();
}

/// <summary><c>{type: "tool_execution_end", toolCallId, toolName, entry?}</c>: the slot is done, with its <c>pi.tool-result</c> entry, or
/// without one when the tool task faulted or was orphaned.</summary>
public sealed record ToolExecutionEndEvent(string ToolCallId, string ToolName, EntryRecord? Entry = null) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "tool_execution_end";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        Writer().Add("toolCallId", ToolCallId).Add("toolName", ToolName).Add("entry", Entry?.ToJson()).Build();
}

/// <summary><c>{type: "inbox_update", items}</c>: <c>pi.inbox</c> changed.</summary>
public sealed record InboxUpdateEvent(ImmutableArray<QueuedItem> Items) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "inbox_update";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("items", JsonValue.Array(Items.Select(item => (JsonValue)item.ToJson()))).Build();
}

/// <summary><c>{type: "submission", record}</c>: a submission record of the conversation was written.</summary>
public sealed record SubmissionEvent(SubmissionRecord Record) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "submission";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("record", Record.ToJson()).Build();
}

/// <summary><c>{type: "auto_retry_start", attempt, at, errorMessage}</c>: <c>pi.live.generation</c> gained a retry backoff until
/// <see cref="At"/> (epoch milliseconds).</summary>
public sealed record AutoRetryStartEvent(int Attempt, double At, string ErrorMessage) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "auto_retry_start";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Number("attempt", Attempt).Number("at", At).Add("errorMessage", ErrorMessage).Build();
}

/// <summary><c>{type: "auto_retry_end", attempt}</c>: <c>pi.live.generation</c> dropped its retry backoff.</summary>
public sealed record AutoRetryEndEvent(int Attempt) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "auto_retry_end";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Number("attempt", Attempt).Build();
}

/// <summary><c>{type: "deferred_poll", pollAt}</c>: <c>pi.live.generation</c> gained a deferred poll or moved its <c>pollAt</c>.</summary>
public sealed record DeferredPollEvent(double PollAt) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "deferred_poll";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Number("pollAt", PollAt).Build();
}

/// <summary><c>{type: "entry_appended", entry}</c>: an entry without <c>model</c> messages was appended.</summary>
public sealed record EntryAppendedEvent(EntryRecord Entry) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "entry_appended";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("entry", Entry.ToJson()).Build();
}

/// <summary><c>{type: "agent_changed", agent}</c>: <c>pi.agent</c> changed; a retired one reads as <c>{}</c>.</summary>
public sealed record AgentChangedEvent(JsonObjectValue Agent) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "agent_changed";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("agent", Agent).Build();
}

/// <summary><c>{type: "usage_changed", usage}</c>: <c>pi.usage</c> changed; a retired one reads as its initial value.</summary>
public sealed record UsageChangedEvent(JsonObjectValue Usage) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "usage_changed";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("usage", Usage).Build();
}

/// <summary><c>{type: "task_failed", taskId, kind, message}</c>: a task of the conversation settled <c>faulted</c> or <c>orphaned</c>.</summary>
public sealed record TaskFailedEvent(TaskId TaskId, string Kind, string Message) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "task_failed";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("taskId", TaskId.ToJson()).Add("kind", Kind).Add("message", Message).Build();
}

/// <summary><c>{type: "compaction_start", taskId, reason, blocking}</c>: a status appeared in <c>pi.live.compactions</c>.</summary>
public sealed record CompactionStartEvent(TaskId TaskId, string Reason, bool Blocking) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "compaction_start";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("taskId", TaskId.ToJson()).Add("reason", Reason).Add("blocking", Blocking).Build();
}

/// <summary><c>{type: "compaction_end", taskId, reason}</c>: a status left <c>pi.live.compactions</c>; the task's receipt tells whether it
/// produced a summary, and the summary entry has its own events.</summary>
public sealed record CompactionEndEvent(TaskId TaskId, string Reason) : AgentEvent
{
    /// <inheritdoc/>
    public override string Type => "compaction_end";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => Writer().Add("taskId", TaskId.ToJson()).Add("reason", Reason).Build();
}

/// <summary>One change to the in-flight assistant message, relative to that message (upstream <c>MessageChange</c>). Blocks and messages are
/// pi-ai wire JSON.</summary>
public abstract record MessageChange
{
    private protected MessageChange() { }

    /// <summary><c>text_start</c>, <c>thinking_start</c>, <c>toolcall_start</c>, <c>text_delta</c>, <c>thinking_delta</c>,
    /// <c>toolcall_delta</c>, <c>block</c> or <c>message</c>.</summary>
    public abstract string Type { get; }

    /// <summary>Upstream's JSON object, <c>type</c> first.</summary>
    public abstract JsonObjectValue ToJson();
}

/// <summary><c>{type: "text_start" | "thinking_start" | "toolcall_start", contentIndex, block}</c>: a block was inserted at
/// <see cref="ContentIndex"/>.</summary>
public sealed record BlockStartChange(string Kind, int ContentIndex, JsonObjectValue Block) : MessageChange
{
    /// <summary><see cref="Kind"/>: <c>text_start</c>, <c>thinking_start</c> or <c>toolcall_start</c>.</summary>
    public override string Type => Kind;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("type", Type).Number("contentIndex", ContentIndex).Add("block", Block).Build();
}

/// <summary><c>{type: "text_delta" | "thinking_delta", contentIndex, delta}</c>: text appended to a block's <c>text</c> or
/// <c>thinking</c>.</summary>
public sealed record TextDeltaChange(string Kind, int ContentIndex, string Delta) : MessageChange
{
    /// <summary><see cref="Kind"/>: <c>text_delta</c> or <c>thinking_delta</c>.</summary>
    public override string Type => Kind;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("type", Type).Number("contentIndex", ContentIndex).Add("delta", Delta).Build();
}

/// <summary><c>{type: "toolcall_delta", contentIndex, path, delta}</c>: text appended to the string at <see cref="Path"/> inside a tool call's
/// <c>arguments</c>.</summary>
public sealed record ToolCallDeltaChange(int ContentIndex, ImmutableArray<PathSegment> Path, string Delta) : MessageChange
{
    /// <inheritdoc/>
    public override string Type => "toolcall_delta";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("type", Type).Number("contentIndex", ContentIndex)
        .Add("path", DeltaPath.ToJson(Path)).Add("delta", Delta).Build();
}

/// <summary><c>{type: "block", contentIndex, block}</c>: any other change inside a block; the block already holds the batch's later changes
/// to it.</summary>
public sealed record BlockChange(int ContentIndex, JsonValue Block) : MessageChange
{
    /// <inheritdoc/>
    public override string Type => "block";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("type", Type).Number("contentIndex", ContentIndex).Add("block", Block).Build();
}

/// <summary><c>{type: "message", message}</c>: any other change of the message; the whole message.</summary>
public sealed record WholeMessageChange(JsonObjectValue Message) : MessageChange
{
    /// <inheritdoc/>
    public override string Type => "message";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("type", Type).Add("message", Message).Build();
}

/// <summary>Serialized stream of one conversation's event batches, one per commit (upstream <c>AgentEventStream</c>).</summary>
public interface IAgentEventStream
{
    /// <summary>The <c>snapshot</c> event at attachment.</summary>
    SnapshotEvent Snapshot { get; }

    /// <summary>Installs the sole asynchronous listener; it is never invoked inline. Throws when already started or stopped.</summary>
    void Start(Func<IReadOnlyList<AgentEvent>, DurableContext, Task> listener);

    /// <summary>Idempotently stops future batches and returns the stream's terminal result.</summary>
    Task<WatchEnd> StopAsync();

    /// <summary>Completes when the stream terminates.</summary>
    Task<WatchEnd> Closed { get; }
}

/// <summary>The experimental agent event adapter (upstream <c>events.ts</c>, spec §9.4).</summary>
public static class AgentEvents
{
    private const string GenerationKind = "pi.generation";
    private static readonly ImmutableArray<PathSegment> PartialPath = Path("docs", "pi.live", "generation", "message");

    /// <summary>
    /// Experimental: attaches to one conversation's agent events (upstream <c>watchEvents</c>). The snapshot and the registration for later
    /// commits are captured atomically on the Session line; overflow replaces undelivered batches with one snapshot. Like a watch, cancelling
    /// <paramref name="context"/> stops the stream.
    /// </summary>
    public static async Task<IAgentEventStream> WatchEventsAsync(this IHarness harness, ConversationId conversationId, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(context);
        var views = ConversationViews.Of(harness);
        var (stream, _) = await views.AttachAsync(conversationId, async (initial, release, storage) =>
        {
            // Generations whose held outcome already ended their turn, read on the line with the snapshot.
            var query = new TaskQuery { ConversationId = conversationId, Kind = GenerationKind, Status = DurableTaskStatus.Completing };
            var completing = await HarnessUtil.ScanAll(cursor => storage.ScanTasksAsync(query, 100, cursor, context)).ConfigureAwait(false);
            var held = new HashSet<TaskId>(completing.Select(record => record.Id));
            var current = initial;
            // Batches are the watch's values; an overflow delivers a snapshot of the newest view instead.
            var watch = new CommittedWatch<IReadOnlyList<AgentEvent>>([], release, views.Schedule, views.Report, () => [SnapshotOf(current)],
                events => JsonValue.Array(events.Select(item => (JsonValue)item.ToJson())));
            var observer = new EventsObserver(watch, (before, after, ops, publication, commitContext) =>
            {
                current = after;
                var events = Translate(conversationId, before, after, ops, publication, held);
                if (events.Count > 0) watch.Advance(events, [], commitContext);
            });
            return (new EventStream(SnapshotOf(initial), watch), (ViewObserver)observer);
        }, context).ConfigureAwait(false);
        // Like a watch, the acquisition context governs the stream's lifetime.
        if (context.IsCancellationRequested)
        {
            stream.Watch.Cancel();
            context.ThrowIfCancellationRequested();
        }
        if (context.HasCancellation) stream.Watch.ObserveCancellation(context);
        return stream;
    }

    private sealed class EventStream(SnapshotEvent snapshot, CommittedWatch<IReadOnlyList<AgentEvent>> watch) : IAgentEventStream
    {
        public CommittedWatch<IReadOnlyList<AgentEvent>> Watch { get; } = watch;

        public SnapshotEvent Snapshot { get; } = snapshot;

        public void Start(Func<IReadOnlyList<AgentEvent>, DurableContext, Task> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            Watch.Start((events, _, deliveryContext) => listener(events, deliveryContext));
        }

        public Task<WatchEnd> StopAsync() => Watch.StopAsync();

        public Task<WatchEnd> Closed => Watch.Closed;
    }

    private sealed class EventsObserver(CommittedWatch<IReadOnlyList<AgentEvent>> watch,
        Action<ConversationView, ConversationView, ImmutableArray<Op>, CommitPublication, DurableContext> publication) : ViewObserver
    {
        public override void Publication(ConversationView before, ConversationView after, ImmutableArray<Op> ops, CommitPublication commit,
            DurableContext context) => publication(before, after, ops, commit, context);

        public override void CloseSession(Exception? failure) => watch.CloseSession(failure);
    }

    // ─── The typed parts of a view the events read ──────────────────────────

    private sealed record Parts(JsonObjectValue Live, JsonObjectValue? Inbox, JsonObjectValue? Agent, JsonObjectValue? Usage)
    {
        public JsonObjectValue? Run => Live.TryGetValue("run", out var run) ? run as JsonObjectValue : null;
        public JsonObjectValue? Generation => Live.TryGetValue("generation", out var generation) ? generation as JsonObjectValue : null;
        public JsonArrayValue? Tools => Live.TryGetValue("tools", out var tools) ? tools as JsonArrayValue : null;
        public JsonArrayValue? Compactions => Live.TryGetValue("compactions", out var compactions) ? compactions as JsonArrayValue : null;
    }

    private static Parts PartsOf(ConversationView view) =>
        new(view.Doc("pi.live") ?? JsonObjectValue.Empty, view.Doc("pi.inbox"), view.Doc("pi.agent"), view.Doc("pi.usage"));

    private static SnapshotEvent SnapshotOf(ConversationView view)
    {
        var parts = PartsOf(view);
        return new SnapshotEvent
        {
            Entries = view.Entries,
            RunInputs = parts.Run is { } run ? Inputs(run) : null,
            Generation = parts.Generation,
            Tools = parts.Tools ?? JsonArrayValue.Empty,
            Compactions = parts.Compactions ?? JsonArrayValue.Empty,
            Inbox = Queued(parts.Inbox),
            Agent = parts.Agent ?? Agents.AgentDoc.Definition.Initial(),
            Usage = parts.Usage ?? UsageLedger.UsageDoc.Definition.Initial(),
        };
    }

    private static ImmutableArray<QueuedItem> Queued(JsonObjectValue? inbox) =>
        inbox is not null && inbox.TryGetValue("items", out var items) && items is JsonArrayValue list
            ? [.. list.Cast<JsonObjectValue>().Select(item => new QueuedItem(new SubmissionId(Integer(item["id"])), Text(item["mode"])))]
            : [];

    private static ImmutableArray<SubmissionId> Inputs(JsonObjectValue run) =>
        [.. ((JsonArrayValue)run["inputs"]).Select(id => new SubmissionId(Integer(id)))];

    // ─── translate ──────────────────────────────────────────────────────────

    /// <summary>The tool result for <paramref name="callId"/> among <paramref name="entries"/>.</summary>
    private static EntryRecord? ResultOf(List<EntryRecord> entries, string callId) => entries.Find(entry =>
        FirstMessage(entry) is { } message && Field(message, "role") is JsonStringValue { Value: "toolResult" } &&
        Field(message, "toolCallId") is JsonStringValue id && id.Value == callId);

    /// <summary>Every event one publication causes, in the order of spec §9.4.</summary>
    private static List<AgentEvent> Translate(ConversationId conversationId, ConversationView before, ConversationView after,
        ImmutableArray<Op> viewOps, CommitPublication publication, HashSet<TaskId> held)
    {
        var entries = new List<EntryRecord>();
        // In the order the commit first touched each task, as upstream's Map keeps it.
        var tasks = new List<TaskRecord>();
        var taskIndex = new Dictionary<TaskId, int>();
        var submissions = new List<SubmissionRecord>();
        foreach (var change in publication.Changes)
        {
            switch (change)
            {
                case EntryWrite { Value: var entry } when entry.ConversationId == conversationId:
                    entries.Add(entry);
                    break;
                case TaskWrite { Value: var task } when task.ConversationId == conversationId:
                    if (taskIndex.TryGetValue(task.Id, out var index)) tasks[index] = task;
                    else
                    {
                        taskIndex[task.Id] = tasks.Count;
                        tasks.Add(task);
                    }
                    break;
                case SubmissionWrite { Value: var submission } when submission.ConversationId == conversationId:
                    submissions.Add(submission);
                    break;
            }
        }
        if (viewOps.IsEmpty && entries.Count == 0 && tasks.Count == 0 && submissions.Count == 0) return [];
        TaskRecord? TaskOf(TaskId id) => taskIndex.TryGetValue(id, out var index) ? tasks[index] : null;
        // Entries are appended in ID order; submission records are published in the order the commit first touched them.
        var sortedSubmissions = submissions.OrderBy(record => record.Id.Value).ToList();
        var was = PartsOf(before);
        var now = PartsOf(after);
        var events = new List<AgentEvent>();

        // Progress: tool starts, the in-flight message, tool updates, retry and deferred state.
        var slotsBefore = new Dictionary<string, JsonObjectValue>(StringComparer.Ordinal);
        var callOrder = new List<string>();
        foreach (var slot in Slots(was.Tools))
        {
            var callId = Text(slot["callId"]);
            if (!slotsBefore.ContainsKey(callId)) callOrder.Add(callId);
            slotsBefore[callId] = slot;
        }
        var slots = Slots(now.Tools);
        foreach (var slot in slots)
        {
            var callId = Text(slot["callId"]);
            if (Status(slot) != "running" || (slotsBefore.TryGetValue(callId, out var previous) && Status(previous) == "running")) continue;
            var checkpoint = Field(slot, "taskId") is { } taskId && TaskOf(new TaskId(Integer(taskId)))?.State is LiveTaskState live
                ? live.Checkpoint : null;
            var args = checkpoint is JsonObjectValue state && state.TryGetValue("arguments", out var arguments) && arguments is JsonObjectValue given
                ? given : JsonObjectValue.Empty;
            events.Add(new ToolExecutionStartEvent(callId, Text(slot["name"]), args));
        }
        var partialBefore = Field(was.Generation, "message") as JsonObjectValue;
        var partial = Field(now.Generation, "message") as JsonObjectValue;
        if (partial is not null && partialBefore is null) events.Add(new MessageStartEvent(partial));
        else if (partial is not null && !ReferenceEquals(partial, partialBefore))
            events.Add(new MessageUpdateEvent((JsonObjectValue)partial["usage"], MessageChanges(viewOps, partial)));
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            if (Status(slot) != "running" || !slotsBefore.TryGetValue(Text(slot["callId"]), out var previous) || Status(previous) != "running") continue;
            if (ToolUpdate(viewOps, index, slot, previous) is { } update) events.Add(update);
        }
        var generation = now.Generation;
        var generationBefore = was.Generation;
        var retry = Field(generation, "retry") as JsonObjectValue;
        var retryBefore = Field(generationBefore, "retry");
        if (retry is not null && retryBefore is null)
            events.Add(new AutoRetryStartEvent((int)Integer(generation!["attempt"]), Number(retry["at"]), Text(retry["error"])));
        if (retryBefore is not null && retry is null) events.Add(new AutoRetryEndEvent((int)Integer(generationBefore!["attempt"])));
        if (Field(generation, "deferred") is JsonObjectValue deferred &&
            !Same(deferred["pollAt"], Field(Field(generationBefore, "deferred"), "pollAt")))
            events.Add(new DeferredPollEvent(Number(deferred["pollAt"])));

        // Tools that end in this commit: a slot that becomes done, one created done (a call not offered), or an unfinished one that vanishes
        // because its run ended. A done slot that vanishes ended earlier.
        var toolEnds = new List<ToolExecutionEndEvent>();
        void EndTool(string callId, string name, long? entryId)
        {
            var entry = entryId is { } id ? entries.Find(candidate => candidate.Id.Value == id) : null;
            toolEnds.Add(new ToolExecutionEndEvent(callId, name, entry));
        }
        foreach (var callId in callOrder)
        {
            var previous = slotsBefore[callId];
            if (Status(previous) == "done") continue;
            var slot = slots.Find(candidate => Text(candidate["callId"]) == callId);
            if (slot is not null && Status(slot) == "done") EndTool(callId, Text(previous["name"]), EntryOf(slot));
            // A slot whose run ended in this commit may have had its result appended with it, as for unstarted calls.
            else if (slot is null) EndTool(callId, Text(previous["name"]), ResultOf(entries, callId)?.Id.Value);
        }
        foreach (var slot in slots)
            if (Status(slot) == "done" && !slotsBefore.ContainsKey(Text(slot["callId"]))) EndTool(Text(slot["callId"]), Text(slot["name"]), EntryOf(slot));

        // Entries in append order; a tool's end directly precedes its result's message, as in the coding agent.
        var assistantAppended = false;
        foreach (var entry in entries)
        {
            events.AddRange(toolEnds.Where(end => ReferenceEquals(end.Entry, entry)));
            if (FirstMessage(entry) is not { } message)
            {
                events.Add(new EntryAppendedEvent(entry));
                continue;
            }
            var assistant = Field(message, "role") is JsonStringValue { Value: "assistant" };
            // A streamed answer already started with its first partial.
            var streamed = assistant && partialBefore is not null && !assistantAppended;
            if (assistant) assistantAppended = true;
            if (!streamed) events.Add(new MessageStartEvent((JsonObjectValue)message));
            events.Add(new MessageEndEvent(entry));
        }
        // Ends without a result entry: a faulted or orphaned tool, or one whose run ended.
        events.AddRange(toolEnds.Where(end => end.Entry is null));

        // Compaction ends, task failures, then turn and run ends.
        var compactionsBefore = Slots(was.Compactions);
        var compactions = Slots(now.Compactions);
        foreach (var status in compactionsBefore)
        {
            if (!compactions.Any(candidate => Same(candidate["taskId"], status["taskId"])))
                events.Add(new CompactionEndEvent(new TaskId(Integer(status["taskId"])), Text(status["reason"])));
        }
        // A generation's turn ends when its outcome is committed: at a `completing` hold or at terminal, whichever comes first, so a
        // successor created at the hold starts after it.
        var turnEnded = false;
        foreach (var task in tasks)
        {
            var status = task.State.Status;
            if (task.Kind == GenerationKind && status == DurableTaskStatus.Completing && held.Add(task.Id)) turnEnded = true;
            if (status != DurableTaskStatus.Terminal) continue;
            if (task.Kind == GenerationKind && !held.Remove(task.Id)) turnEnded = true;
            switch (((SettlingTaskState)task.State).Outcome)
            {
                case FaultedTaskOutcome faulted:
                    events.Add(new TaskFailedEvent(task.Id, task.Kind, faulted.Error.Message));
                    break;
                case OrphanedTaskOutcome orphaned:
                    events.Add(new TaskFailedEvent(task.Id, task.Kind, orphaned.Reason));
                    break;
            }
        }
        if (turnEnded) events.Add(new TurnEndEvent());
        var run = now.Run;
        var runBefore = was.Run;
        var runChanged = !Same(FirstInput(run), FirstInput(runBefore));
        if (runBefore is not null && runChanged) events.Add(new RunEndEvent(Inputs(runBefore)));

        // Submissions, document state, then what began.
        foreach (var record in sortedSubmissions) events.Add(new SubmissionEvent(record));
        if (!ReferenceEquals(now.Inbox, was.Inbox)) events.Add(new InboxUpdateEvent(Queued(now.Inbox)));
        // A retired document reads as its initial value, as in a snapshot.
        if (!ReferenceEquals(now.Agent, was.Agent)) events.Add(new AgentChangedEvent(now.Agent ?? Agents.AgentDoc.Definition.Initial()));
        if (!ReferenceEquals(now.Usage, was.Usage)) events.Add(new UsageChangedEvent(now.Usage ?? UsageLedger.UsageDoc.Definition.Initial()));
        foreach (var status in compactions)
        {
            if (!compactionsBefore.Any(candidate => Same(candidate["taskId"], status["taskId"])))
            {
                events.Add(new CompactionStartEvent(new TaskId(Integer(status["taskId"])), Text(status["reason"]),
                    status["blocking"] is JsonBooleanValue { Value: true }));
            }
        }
        if (run is not null && runChanged) events.Add(new RunStartEvent(Inputs(run)));
        if (run is not null && !Same(run["taskId"], Field(runBefore, "taskId")) && TaskOf(new TaskId(Integer(run["taskId"])))?.Kind == GenerationKind)
            events.Add(new TurnStartEvent());
        return events;
    }

    /// <summary>Translates the view operations on the in-flight message into message changes (upstream <c>messageChanges</c>).</summary>
    private static ImmutableArray<MessageChange> MessageChanges(ImmutableArray<Op> viewOps, JsonObjectValue message)
    {
        var changes = ImmutableArray.CreateBuilder<MessageChange>();
        // A block sent whole already holds every later change to it in this batch.
        var whole = new HashSet<long>();
        foreach (var op in viewOps)
        {
            // View operations never replace the root.
            var path = op is PathOp pathOp ? pathOp.Path : [];
            if (!StartsWith(path, PartialPath))
            {
                // The whole message or generation was replaced.
                if (StartsWith(PartialPath, path)) return [new WholeMessageChange(message)];
                continue;
            }
            var rest = path.AsSpan()[PartialPath.Length..];
            if (rest.Length > 0 && IsKey(rest[0], "usage")) continue;
            if (rest.Length == 0 || !IsKey(rest[0], "content")) return [new WholeMessageChange(message)];
            if (rest.Length == 1)
            {
                if (op is not SpliceOp { Remove: 0 } splice) return [new WholeMessageChange(message)];
                for (var offset = 0; offset < splice.Items.Length; offset++)
                {
                    var block = (JsonObjectValue)splice.Items[offset];
                    var kind = Field(block, "type") switch
                    {
                        JsonStringValue { Value: "text" } => "text_start",
                        JsonStringValue { Value: "thinking" } => "thinking_start",
                        _ => "toolcall_start",
                    };
                    changes.Add(new BlockStartChange(kind, (int)splice.Index + offset, block));
                }
                continue;
            }
            var contentIndex = rest[1].Index;
            var field = rest.Length > 2 ? rest[2] : (PathSegment?)null;
            if (whole.Contains(contentIndex)) continue;
            if (op is AppendOp append && rest.Length == 3 && field is { } textField && (IsKey(textField, "text") || IsKey(textField, "thinking")))
            {
                changes.Add(new TextDeltaChange(IsKey(textField, "text") ? "text_delta" : "thinking_delta", (int)contentIndex, append.Text));
            }
            else if (op is AppendOp argument && field is { } argumentsField && IsKey(argumentsField, "arguments"))
            {
                changes.Add(new ToolCallDeltaChange((int)contentIndex, [.. rest[3..]], argument.Text));
            }
            else
            {
                whole.Add(contentIndex);
                changes.Add(new BlockChange((int)contentIndex, ((JsonArrayValue)message["content"])[(int)contentIndex]));
            }
        }
        return changes.ToImmutable();
    }

    /// <summary>Output, details, and diagnostics changes of a running slot, from the view operations on it (upstream <c>toolUpdate</c>).</summary>
    private static ToolExecutionUpdateEvent? ToolUpdate(ImmutableArray<Op> viewOps, int index, JsonObjectValue slot, JsonObjectValue previous)
    {
        var outputPath = Path("docs", "pi.live", "tools", index, "output");
        long trimStart = 0;
        var append = "";
        var set = false;
        foreach (var op in viewOps)
        {
            if (op is not PathOp pathOp || !StartsWith(pathOp.Path, outputPath)) continue;
            if (op is TruncateOp trim) trimStart += trim.Count;
            else if (op is AppendOp text) append += text.Text;
            else set = true;
        }
        var outputNow = Field(slot, "output") as JsonStringValue;
        var outputBefore = Field(previous, "output") as JsonStringValue;
        ToolOutputChange? output = null;
        if (set || (outputNow?.Value != outputBefore?.Value && trimStart == 0 && append == "")) output = new ToolOutputSet(outputNow?.Value ?? "");
        else if (trimStart > 0 || append != "") output = new ToolOutputDelta(trimStart > 0 ? trimStart : null, append == "" ? null : append);
        // A safe replay clears a running slot's progress: removed details send `null`, removed diagnostics `[]`.
        var details = Field(slot, "details");
        var diagnostics = Field(slot, "diagnostics");
        var detailsChanged = !Same(details, Field(previous, "details"));
        var diagnosticsChanged = !Same(diagnostics, Field(previous, "diagnostics"));
        if (output is null && !detailsChanged && !diagnosticsChanged) return null;
        return new ToolExecutionUpdateEvent(Text(slot["callId"]), Text(slot["name"]), output,
            detailsChanged ? details ?? JsonValue.Null : null,
            diagnosticsChanged ? diagnostics as JsonArrayValue ?? JsonArrayValue.Empty : null);
    }

    // ─── JSON helpers ───────────────────────────────────────────────────────

    /// <summary>JavaScript <c>===</c> on JSON values: strings, numbers, booleans and null by value, objects and arrays by identity; both absent
    /// is equal.</summary>
    private static bool Same(JsonValue? left, JsonValue? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (JsonObjectValue or JsonArrayValue, _) or (_, JsonObjectValue or JsonArrayValue) => ReferenceEquals(left, right),
        _ => JsonValue.DeepEquals(left, right),
    };

    private static JsonValue? Field(JsonValue? value, string key) =>
        value is JsonObjectValue item && item.TryGetValue(key, out var field) ? field : null;

    private static JsonValue? FirstMessage(EntryRecord entry) => entry.Model is { Count: > 0 } model ? model[0] : null;

    private static JsonValue? FirstInput(JsonObjectValue? run) =>
        Field(run, "inputs") is JsonArrayValue { Count: > 0 } inputs ? inputs[0] : null;

    private static List<JsonObjectValue> Slots(JsonArrayValue? items) => items is null ? [] : [.. items.Cast<JsonObjectValue>()];

    private static string? Status(JsonObjectValue slot) => (Field(slot, "status") as JsonStringValue)?.Value;

    private static long? EntryOf(JsonObjectValue slot) => Field(slot, "entry") is JsonNumberValue id ? (long)id.Value : null;

    private static string Text(JsonValue value) => ((JsonStringValue)value).Value;

    private static double Number(JsonValue value) => ((JsonNumberValue)value).Value;

    private static long Integer(JsonValue value) => (long)((JsonNumberValue)value).Value;

    private static bool IsKey(PathSegment segment, string key) => segment.Key == key;

    private static bool StartsWith(ImmutableArray<PathSegment> path, ImmutableArray<PathSegment> prefix) =>
        prefix.Length <= path.Length && path.AsSpan(0, prefix.Length).SequenceEqual(prefix.AsSpan());

    private static ImmutableArray<PathSegment> Path(params object[] segments) =>
        [.. segments.Select(segment => segment is string key ? PathSegment.Of(key) : PathSegment.Of((int)segment))];
}
