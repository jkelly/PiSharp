// Decision 0012: subagents. A subagent's conversation is owned by an anchor, a background task of its parent's conversation that ends at
// once, so the subagent outlives the call that started it and the parent's Esc and idle waits stop at it. Each task given to a subagent is
// a request: a session document holding its input and a state that one commit at a time moves forward:
//   waiting  -> a call waits for the answer (delegate, ask_agent);
//   detached -> nobody waits; the request's watcher reports the answer to the parent as a message;
//   tool / reporter -> claimed by the waiting call or by the watcher (first writer wins, so an answer is delivered once);
//   aborted / expired -> nothing to deliver.
// The watcher is a background task of the parent's conversation. It also supervises: when the call that waited ends without claiming the
// answer (it was aborted), the watcher aborts the subagent's work. Blocking calls record wait edges in the directory; a call that would
// close a cycle is refused, unless the agent it would wait on is itself waiting interruptibly (an interactive delegate or wait_agent), in
// which case the question goes to that waiting call, which returns it to its model.
using System.Globalization;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.CustomAgents.Teams;

/// <summary>The request documents: one per task given to a subagent or question asked.</summary>
internal static class Requests
{
    public const string Waiting = "waiting";
    public const string Detached = "detached";
    public const string Tool = "tool";
    public const string Reporter = "reporter";
    public const string Aborted = "aborted";
    public const string Expired = "expired";

    /// <summary><c>{state, kind, from, fromConversationId?, to, child, input, entry, messageId?, toolTaskId?, routed?}</c>, keyed by request ID.</summary>
    public static SessionDocFamilyToken Doc { get; } = Documents.DefineSessionDocFamily("pisharp.agents.request", 1,
        seed => seed as JsonObjectValue ?? JsonObjectValue.Empty);

    public static Task<DraftObject> OpenAsync(ITx tx, string id) => tx.DocAsync(Doc, id, JsonObjectValue.Empty);

    public static string? State(JsonObjectValue? doc) => doc is not null ? Mailboxes.Str(doc, "state") : null;

    public static string? State(DraftObject doc) => doc.Get("state") is JsonStringValue text ? text.Value : null;
}

/// <summary>One blocking call: <see cref="From"/> waits on <see cref="To"/>.</summary>
internal sealed record WaitEdge(TaskId Tool, string From, string To, string Kind, bool Interruptible);

internal static class WaitGraph
{
    public static IReadOnlyList<WaitEdge> Edges(JsonObjectValue? directory) =>
        directory is not null && directory.TryGetValue("waits", out var waits) && waits is JsonObjectValue map
            ? [.. map.Where(pair => pair.Value is JsonObjectValue).Select(pair =>
            {
                var edge = (JsonObjectValue)pair.Value;
                return new WaitEdge(new TaskId(long.Parse(pair.Key, CultureInfo.InvariantCulture)), Mailboxes.Str(edge, "from") ?? "",
                    Mailboxes.Str(edge, "to") ?? "", Mailboxes.Str(edge, "kind") ?? "", edge.TryGetValue("interruptible", out var flag) && flag is JsonBooleanValue { Value: true });
            })]
            : [];

    /// <summary>The live edges: those whose call is still running. A table read, so call it before the commit's first table write.</summary>
    public static async Task<IReadOnlyList<WaitEdge>> LiveAsync(ITx tx, JsonObjectValue directory)
    {
        var live = new List<WaitEdge>();
        foreach (var edge in Edges(directory))
            if (await tx.GetTaskAsync(edge.Tool).ConfigureAwait(false) is { State: not SettlingTaskState }) live.Add(edge);
        return live;
    }

    /// <summary>Replaces the edges with <paramref name="live"/> (dropping stale ones), then adds <paramref name="added"/>.</summary>
    public static void Write(DraftObject directory, IEnumerable<WaitEdge> live, WaitEdge? added = null, TaskId? removed = null)
    {
        var edges = live.Where(edge => removed is null || edge.Tool != removed).ToList();
        if (added is not null) edges.Add(added);
        directory.Set("waits", JsonValue.Object(edges.Select(edge => KeyValuePair.Create(edge.Tool.Value.ToString(CultureInfo.InvariantCulture),
            (JsonValue)RunDocs.Object(("from", edge.From), ("to", edge.To), ("kind", edge.Kind), ("interruptible", edge.Interruptible))))));
    }

    /// <summary>Removes the edge of <paramref name="tool"/> (a document write only).</summary>
    public static void Remove(DraftObject directory, TaskId tool)
    {
        if (directory.Get("waits") is JsonObjectValue) directory.GetObject("waits").Delete(tool.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Whether <paramref name="from"/> waits, directly or through others, on <paramref name="to"/>.</summary>
    public static bool Reaches(IReadOnlyList<WaitEdge> edges, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([from]);
        while (queue.TryDequeue(out var current))
        {
            if (current == to) return true;
            if (!seen.Add(current)) continue;
            foreach (var edge in edges.Where(edge => edge.From == current)) queue.Enqueue(edge.To);
        }
        return false;
    }
}

internal static class Orchestration
{
    /// <summary>Owns a subagent's conversation; ends at once (the conversation outlives it).</summary>
    public static DurableTask Anchor { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pisharp.agents.anchor",
        Version = 1,
        Initial = _ => JsonValue.Object([new("phase", "done")]),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["done"] = (_, runtime, context) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Completed(JsonValue.Null), context) },
        Abort = (_, runtime, context) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted(), context),
    });

    /// <summary>Supervises one request and reports a detached answer once.</summary>
    public static DurableTask Watcher { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pisharp.agents.watcher",
        Version = 1,
        Initial = _ => JsonValue.Object([new("phase", "watch")]),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["watch"] = WatchAsync, ["report"] = ReportAsync },
        Abort = (_, runtime, context) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted(), context),
    });

    // ─── Watcher ────────────────────────────────────────────────────────────

    private static async Task WatchAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var requestId = Mailboxes.Str((JsonObjectValue)task.Input, "requestId")!;
        var request = await runtime.SnapshotAsync(Requests.Doc, requestId, context).ConfigureAwait(false) ?? JsonObjectValue.Empty;
        IConversationHandle? handle = request.TryGetValue("child", out var child) && child is JsonNumberValue childId
            ? await runtime.ConversationAsync(new ConversationId((long)childId.Value), context).ConfigureAwait(false)
            : null;
        var settle = handle is null ? null : (await SubmitAsync(handle.SubmitAsync, request, requestId, context).ConfigureAwait(false)).WaitAsync(context);
        TaskId? caller = null;
        Task<TaskRecord>? callerEnded = null;
        var changed = new SemaphoreSlim(0);
        var watch = await runtime.WatchDocAsync(Requests.Doc, requestId, context).ConfigureAwait(false);
        watch?.Start((_, _, _) => { changed.Release(); return Task.CompletedTask; });
        try
        {
            // Each commit below either moves the task on (and the handler returns) or finds the request moved by someone else first and
            // leaves the task as it was (and the loop looks again): a phase must not return without durable progress.
            while (true)
            {
                var current = await runtime.SnapshotAsync(Requests.Doc, requestId, context).ConfigureAwait(false);
                var state = Requests.State(current);
                if (state is Requests.Tool or Requests.Reporter or Requests.Aborted or Requests.Expired || settle is null)
                {
                    await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Completed(JsonValue.Null), context).ConfigureAwait(false);
                    return;
                }
                // The call waiting now: delegate's, or a wait_agent's that re-attached.
                var waiting = current is not null && current.TryGetValue("toolTaskId", out var tool) && tool is JsonNumberValue toolId
                    ? new TaskId((long)toolId.Value)
                    : (TaskId?)null;
                if (waiting != caller)
                {
                    caller = waiting;
                    callerEnded = caller is { } callerId ? runtime.WaitForTaskAsync(callerId, context) : null;
                }
                if (state == Requests.Detached && settle.IsCompleted)
                {
                    var settled = await settle.ConfigureAwait(false);
                    var moved = false;
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        var answer = settled.Answer is { } entryId ? await tx.GetEntryAsync(Entries.AssistantEntry, entryId).ConfigureAwait(false) : null;
                        var doc = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
                        if (Requests.State(doc) != Requests.Detached) return (TaskState?)null;
                        moved = true;
                        if (settled.Status != SubmissionStatus.Done && settled.Reason == "aborted")
                        {
                            doc.Set("state", Requests.Aborted);
                            return NextTaskState.Completed(JsonValue.Null);
                        }
                        doc.Set("state", Requests.Reporter);
                        var text = Answer(answer) ?? $"[{Mailboxes.Str(request, "to")} could not answer: {Why(settled)}]";
                        return NextTaskState.Running(RunDocs.Object(("phase", "report"), ("text", text)));
                    }, context).ConfigureAwait(false);
                    if (moved) return;
                    continue;
                }
                if (state == Requests.Waiting && callerEnded is { IsCompleted: true })
                {
                    // The call that waited ended without claiming the answer: it was aborted. A delegate's subagent stops with it; after a
                    // wait_agent, the subagent keeps working and its answer is reported.
                    var abort = Mailboxes.Str(current!, "onCallerEnd") != "detach";
                    if (abort && handle is not null) await handle.AbortAsync(context).ConfigureAwait(false);
                    var moved = false;
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        var doc = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
                        if (Requests.State(doc) != Requests.Waiting || doc.Get("toolTaskId") is not JsonNumberValue still || (long)still.Value != caller!.Value.Value)
                            return (TaskState?)null;
                        if (!abort)
                        {
                            doc.Set("state", Requests.Detached);
                            doc.Delete("toolTaskId");
                            return null;
                        }
                        moved = true;
                        doc.Set("state", Requests.Aborted);
                        return NextTaskState.Completed(JsonValue.Null);
                    }, context).ConfigureAwait(false);
                    if (moved) return;
                    continue;
                }
                var waits = new List<Task> { changed.WaitAsync(context.CancellationToken) };
                if (!settle.IsCompleted) waits.Add(settle);
                if (callerEnded is { IsCompleted: false }) waits.Add(callerEnded);
                await Task.WhenAny(waits).ConfigureAwait(false);
                context.CancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (watch is not null) await watch.StopAsync().ConfigureAwait(false);
        }
    }

    internal static string? Answer(EntryRecord? entry) =>
        entry?.Model is { Count: > 0 } messages && messages[0] is JsonObjectValue message ? ModelCalls.Text(message) : null;

    internal static string Why(SubmissionRecord settled) => settled.Reason ?? settled.Status.ToString().ToLowerInvariant();

    private static async Task ReportAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var requestId = Mailboxes.Str((JsonObjectValue)task.Input, "requestId")!;
        var request = await runtime.SnapshotAsync(Requests.Doc, requestId, context).ConfigureAwait(false) ?? JsonObjectValue.Empty;
        var text = Mailboxes.Str((JsonObjectValue)task.Checkpoint, "text") ?? "";
        var from = Mailboxes.Str(request, "to") ?? "";
        var to = Mailboxes.Str(request, "from") ?? "";
        var message = new AgentMessage("r." + requestId, from, to, AgentMessageKind.Result, text, requestId, runtime.Now(), false);
        await runtime.CommitAsync(async (tx, _) => { Mailboxes.Append(await Mailboxes.OpenAsync(tx, to).ConfigureAwait(false), message, requestId); }, context)
            .ConfigureAwait(false);
        if (request.TryGetValue("fromConversationId", out var conversation) && conversation is JsonNumberValue number &&
            await runtime.ConversationAsync(new ConversationId((long)number.Value), context).ConfigureAwait(false) is { } parent)
            await parent.SubmitAsync(SubmissionDraft.Input(Mailboxes.Header(message), WhenBusy.FollowUp, "agents.report:" + requestId,
                new InputEntry(Mailboxes.EntryKind, Mailboxes.EntryData(message))), context).ConfigureAwait(false);
        await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Completed(JsonValue.Null), context).ConfigureAwait(false);
    }

    /// <summary>Submits the request's input to its subagent; a known request ID returns the first submission.</summary>
    private static Task<ISubmission> SubmitAsync(Func<SubmissionDraft, DurableContext, Task<ISubmission>> submit, JsonObjectValue request, string requestId,
        DurableContext context) =>
        submit(SubmissionDraft.Input(Mailboxes.Str(request, "input") ?? "", WhenBusy.FollowUp, requestId,
            new InputEntry(Mailboxes.EntryKind, request.TryGetValue("entry", out var entry) ? entry : null)), context);

    // ─── Starting subagents ─────────────────────────────────────────────────

    /// <summary>The result of starting a request.</summary>
    internal sealed record Started(string Address, ConversationId Child, JsonObjectValue Request, bool Existing);

    /// <summary>
    /// Gives <paramref name="text"/> to a subagent of <paramref name="definition"/> in one commit: a new one (anchor, conversation, agent,
    /// directory entry) or the caller's child at <paramref name="name"/>; records the request, a wait edge when the call will wait, and its
    /// watcher. Idempotent by <paramref name="requestId"/>.
    /// </summary>
    public static Task<Started> StartAsync(AgentTeam team, IToolExecutionApi api, Sender caller, AgentDefinition definition, string? name, string requestId,
        string text, string kind, bool wait, bool interruptible, DurableContext context) =>
        api.CommitAsync(async tx =>
        {
            var directory = await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false);
            var snapshot = (JsonObjectValue)directory.ToValue();
            var live = await WaitGraph.LiveAsync(tx, snapshot).ConfigureAwait(false);
            var request = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
            if (request.Get("to") is JsonStringValue known && request.Get("child") is JsonNumberValue knownChild)
                return new Started(known.Value, new ConversationId((long)knownChild.Value), (JsonObjectValue)request.ToValue(), true);
            var entry = caller.Entry!;
            // The caller watches its mailbox while it waits; a watch binds only an existing document.
            await Mailboxes.EnsureAsync(tx, entry.Address).ConfigureAwait(false);
            AgentEntry? target = name is null ? null : AgentDirectory.Find(snapshot, name);
            if (target is not null)
            {
                if (target.Parent != entry.Address || target.Closed)
                    throw new AgentMessagingException($"'{name}' is taken by another agent; choose another name.");
                if (target.Definition != definition.Name)
                    throw new AgentMessagingException($"'{name}' is a {target.Definition}, not a {definition.Name}.");
            }
            else
            {
                var agents = AgentDirectory.All(snapshot);
                if (agents.Count(agent => agent.Parent == entry.Address && !agent.Closed) >= team.Options.MaxChildrenPerAgent)
                    throw new AgentMessagingException($"You already have {team.Options.MaxChildrenPerAgent} subagents; stop one or give a task to an existing one by name.");
                if (agents.Count(agent => !agent.Closed) >= team.Options.MaxAgents)
                    throw new AgentMessagingException($"The team already has {team.Options.MaxAgents} agents.");
                if (entry.Depth + 1 > team.Options.MaxDepth)
                    throw new AgentMessagingException("Subagents may not start further subagents at this depth.");
                if (name is not null && !AgentDirectory.IsAddress(name)) throw new AgentMessagingException($"'{name}' is not a valid agent name.");
                var address = name ?? await AgentDirectory.NextAddressAsync(tx, definition.Name).ConfigureAwait(false);
                var anchor = await tx.CreateTaskAsync(Anchor, RunDocs.Object(("address", address)),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = entry.ConversationId, Background = true }).ConfigureAwait(false);
                var created = await tx.CreateConversationAsync(ConversationOwnership.Task(anchor)).ConfigureAwait(false);
                await PiSharp.Durable.Agents.ConfigureAsync(tx, created.Id, team.Catalog.ToAgentChange(definition, new AgentPlacement(entry.Depth + 1)))
                    .ConfigureAwait(false);
                target = new AgentEntry(address, created.Id, definition.Name, AgentKind.Child, entry.Address, entry.Depth + 1, false);
                await AgentDirectory.AddAsync(tx, target).ConfigureAwait(false);
            }
            var message = new AgentMessage("m." + requestId, entry.Address, target.Address, AgentMessageKind.Task, text, null, NowMs(team), false);
            var input = Mailboxes.Header(message);
            request.Set("state", wait ? Requests.Waiting : Requests.Detached);
            request.Set("kind", kind);
            request.Set("from", entry.Address);
            request.Set("fromConversationId", entry.ConversationId.Value);
            request.Set("to", target.Address);
            request.Set("child", target.ConversationId.Value);
            request.Set("input", input);
            request.Set("entry", Mailboxes.EntryData(message));
            if (wait) request.Set("toolTaskId", api.TaskId.Value);
            directory.GetObject("agents").Set(target.Address, ((JsonObjectValue)directory.GetObject("agents").Get(target.Address)!).SetItem("lastRequestId", requestId));
            WaitGraph.Write(directory, live, wait ? new WaitEdge(api.TaskId, entry.Address, target.Address, kind, interruptible) : null);
            Mailboxes.Append(await Mailboxes.OpenAsync(tx, target.Address).ConfigureAwait(false), message, requestId);
            await tx.CreateTaskAsync(Watcher, RunDocs.Object(("requestId", requestId)),
                new TaskOptions(TaskOwnership.Conversation) { ConversationId = entry.ConversationId, Background = true }).ConfigureAwait(false);
            return new Started(target.Address, target.ConversationId, (JsonObjectValue)request.ToValue(), false);
        }, context);

    /// <summary><c>delegate</c>: gives a task and waits for the answer (interactive: returns early on a question or the deadline).</summary>
    public static async Task<string> DelegateAsync(AgentTeam team, IToolExecutionApi api, Sender caller, AgentDefinition definition, string task, string? name,
        TimeSpan? timeout, DurableContext context)
    {
        var interactive = caller.Definition?.Delegation != DelegationMode.Strict;
        var requestId = "agents.delegate:" + api.TaskId.Value.ToString(CultureInfo.InvariantCulture);
        var started = await StartAsync(team, api, caller, definition, name, requestId, task, "delegate", true, interactive, context).ConfigureAwait(false);
        var handle = await api.ConversationAsync(started.Child, context).ConfigureAwait(false) ?? throw new AgentMessagingException("The subagent's conversation is missing.");
        var submission = await SubmitAsync(handle.SubmitAsync, started.Request, requestId, context).ConfigureAwait(false);
        return await AwaitAnswerAsync(team, api, caller, started.Address, requestId, submission, interactive, timeout ?? team.Options.AskTimeout, context)
            .ConfigureAwait(false);
    }

    /// <summary><c>spawn</c>: gives a task and returns at once; the answer arrives as a message.</summary>
    public static async Task<string> SpawnAsync(AgentTeam team, IToolExecutionApi api, Sender caller, AgentDefinition definition, string task, string? name,
        DurableContext context)
    {
        var requestId = "agents.spawn:" + api.TaskId.Value.ToString(CultureInfo.InvariantCulture);
        var started = await StartAsync(team, api, caller, definition, name, requestId, task, "spawn", false, false, context).ConfigureAwait(false);
        var handle = await api.ConversationAsync(started.Child, context).ConfigureAwait(false) ?? throw new AgentMessagingException("The subagent's conversation is missing.");
        await SubmitAsync(handle.SubmitAsync, started.Request, requestId, context).ConfigureAwait(false);
        return $"Started {started.Address} on the task; its answer will arrive as a message from {started.Address} (or call wait_agent(\"{started.Address}\")).";
    }

    /// <summary><c>wait_agent</c>: waits for the answer to a subagent's latest detached task. It re-attaches: the request is waiting again, on
    /// this call, so its watcher does not report the answer as well; if this call ends without the answer, the request detaches again.</summary>
    public static async Task<string> WaitAgentAsync(AgentTeam team, IToolExecutionApi api, Sender caller, string address, TimeSpan? timeout, DurableContext context)
    {
        var directory = await api.SnapshotAsync(AgentDirectory.Doc, context).ConfigureAwait(false);
        var entry = AgentDirectory.Find(directory, address);
        if (entry is null || entry.Parent != caller.Address) return $"'{address}' is not one of your subagents.";
        var requestId = directory!["agents"] is JsonObjectValue agents && agents[address] is JsonObjectValue value ? Mailboxes.Str(value, "lastRequestId") : null;
        if (requestId is null) return $"{address} has no task.";
        var (state, request) = await api.CommitAsync(async tx =>
        {
            var doc = await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false);
            var live = await WaitGraph.LiveAsync(tx, (JsonObjectValue)doc.ToValue()).ConfigureAwait(false);
            var request = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
            await Mailboxes.EnsureAsync(tx, caller.Address).ConfigureAwait(false);
            var state = Requests.State(request);
            var mine = request.Get("toolTaskId") is JsonNumberValue waiting && (long)waiting.Value == api.TaskId.Value;
            if (state == Requests.Detached || (state == Requests.Waiting && mine))
            {
                request.Set("state", Requests.Waiting);
                request.Set("toolTaskId", api.TaskId.Value);
                request.Set("onCallerEnd", "detach");
                WaitGraph.Write(doc, live, new WaitEdge(api.TaskId, caller.Address, address, "wait", true));
                return (Requests.Waiting, (JsonObjectValue)request.ToValue());
            }
            return (state, (JsonObjectValue)request.ToValue());
        }, context).ConfigureAwait(false);
        switch (state)
        {
            case Requests.Tool or Requests.Reporter: return $"{address}'s answer was already delivered (as a message, if not to a call).";
            case Requests.Aborted: return $"{address}'s task was stopped.";
            case Requests.Waiting: break;
            default: return $"Another call is already waiting for {address}.";
        }
        var handle = await api.ConversationAsync(entry.ConversationId, context).ConfigureAwait(false) ?? throw new AgentMessagingException("The subagent's conversation is missing.");
        var submission = await SubmitAsync(handle.SubmitAsync, request, requestId, context).ConfigureAwait(false);
        return await AwaitAnswerAsync(team, api, caller, address, requestId, submission, true, timeout ?? team.Options.AskTimeout, context).ConfigureAwait(false);
    }

    /// <summary><c>stop_agent</c>: aborts a subagent's current work.</summary>
    public static async Task<string> StopAgentAsync(IToolExecutionApi api, Sender caller, string address, DurableContext context)
    {
        var entry = AgentDirectory.Find(await api.SnapshotAsync(AgentDirectory.Doc, context).ConfigureAwait(false), address);
        if (entry is null || entry.Parent != caller.Address) return $"'{address}' is not one of your subagents.";
        if (await api.ConversationAsync(entry.ConversationId, context).ConfigureAwait(false) is { } handle) await handle.AbortAsync(context).ConfigureAwait(false);
        return $"Stopped {address}'s current work; it keeps its history and can take another task.";
    }

    /// <summary>
    /// Waits for <paramref name="submission"/> to settle and claims its answer. Interactive waits also return when the subagent asks this
    /// agent a question (routed to the mailbox) or at the deadline; both detach the request, so its watcher reports the answer later.
    /// Notes the subagent sent meanwhile are included.
    /// </summary>
    private static async Task<string> AwaitAnswerAsync(AgentTeam team, IToolExecutionApi api, Sender caller, string address, string requestId,
        ISubmission submission, bool interactive, TimeSpan timeout, DurableContext context)
    {
        var deadline = await DeadlineAsync(team, api, "deadline:" + requestId, timeout, context).ConfigureAwait(false);
        var changed = new SemaphoreSlim(0);
        var watch = interactive ? await api.WatchDocAsync(Mailboxes.Doc, caller.Address, context).ConfigureAwait(false) : null;
        watch?.Start((_, _, _) => { changed.Release(); return Task.CompletedTask; });
        var settle = submission.WaitAsync(context);
        try
        {
            while (true)
            {
                if (settle.IsCompleted) return await ClaimAsync(api, caller, address, requestId, await settle.ConfigureAwait(false), context).ConfigureAwait(false);
                if (interactive)
                {
                    var mailbox = await api.SnapshotAsync(Mailboxes.Doc, caller.Address, context).ConfigureAwait(false);
                    if (Mailboxes.Read(mailbox).FirstOrDefault(message => message.From == address && message.Kind == AgentMessageKind.Question && !message.Acknowledged)
                        is { } question)
                    {
                        await DetachAsync(api, caller, requestId, question.Id, context).ConfigureAwait(false);
                        return $"{address} asks you (message id {question.Id}): {question.Text}\nAnswer with reply(\"{question.Id}\", text). {address} keeps " +
                            $"working; its answer will arrive as a message, or call wait_agent(\"{address}\").";
                    }
                    if (NowMs(team) >= deadline)
                    {
                        await DetachAsync(api, caller, requestId, null, context).ConfigureAwait(false);
                        return $"{address} is still working. Its answer will arrive as a message from {address}, or call wait_agent(\"{address}\").";
                    }
                }
                var waits = new List<Task> { settle };
                if (interactive)
                {
                    waits.Add(changed.WaitAsync(context.CancellationToken));
                    waits.Add(Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, deadline - NowMs(team))), team.Options.TimeProvider ?? TimeProvider.System,
                        context.CancellationToken));
                }
                await Task.WhenAny(waits).ConfigureAwait(false);
                context.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (watch is not null) await watch.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Claims the answer for the waiting call (one commit: state, wait edge, notes) and returns it.</summary>
    private static async Task<string> ClaimAsync(IToolExecutionApi api, Sender caller, string address, string requestId, SubmissionRecord settled,
        DurableContext context)
    {
        var (claimed, text, notes) = await api.CommitAsync(async tx =>
        {
            var answer = settled.Answer is { } entryId ? await tx.GetEntryAsync(Entries.AssistantEntry, entryId).ConfigureAwait(false) : null;
            var request = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
            var directory = await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false);
            WaitGraph.Remove(directory, api.TaskId);
            var state = Requests.State(request);
            if (state != Requests.Waiting && state != Requests.Tool) return (false, "", (IReadOnlyList<AgentMessage>)[]);
            request.Set("state", Requests.Tool);
            var mailbox = await Mailboxes.OpenAsync(tx, caller.Address).ConfigureAwait(false);
            var pending = Mailboxes.Read((JsonObjectValue)mailbox.ToValue())
                .Where(message => message.From == address && message.Kind == AgentMessageKind.Message && !message.Acknowledged).ToList();
            foreach (var note in pending) Mailboxes.Acknowledge(mailbox, note.Id);
            var reply = answer?.Model is { Count: > 0 } messages && messages[0] is JsonObjectValue message ? ModelCalls.Text(message) : null;
            return (true, reply ?? $"{address} did not answer ({settled.Reason ?? settled.Status.ToString().ToLowerInvariant()}).", (IReadOnlyList<AgentMessage>)pending);
        }, context).ConfigureAwait(false);
        if (!claimed) return $"{address}'s answer was already delivered as a message.";
        var result = $"{address} answered:\n{text}";
        if (notes.Count > 0) result += "\n\nNotes " + address + " sent meanwhile:\n" + string.Join("\n", notes.Select(note => "- " + note.Text));
        return result;
    }

    private static Task DetachAsync(IToolExecutionApi api, Sender caller, string requestId, string? acknowledge, DurableContext context) =>
        api.CommitAsync(async tx =>
        {
            var request = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
            if (Requests.State(request) is Requests.Waiting)
            {
                request.Set("state", Requests.Detached);
                request.Delete("toolTaskId");
            }
            WaitGraph.Remove(await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false), api.TaskId);
            if (acknowledge is not null) Mailboxes.Acknowledge(await Mailboxes.OpenAsync(tx, caller.Address).ConfigureAwait(false), acknowledge);
        }, context);

    /// <summary>An absolute deadline recorded on the call, so a rerun keeps the first one.</summary>
    private static async Task<double> DeadlineAsync(AgentTeam team, IToolExecutionApi api, string name, TimeSpan timeout, DurableContext context)
    {
        var candidate = JsonValue.Object([new("at", NowMs(team) + timeout.TotalMilliseconds)]);
        var recorded = (JsonObjectValue)await api.MemoAsync(name, candidate, context).ConfigureAwait(false);
        return ((JsonNumberValue)recorded["at"]).Value;
    }

    private static double NowMs(AgentTeam team) => (team.Options.TimeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();

    // ─── Questions ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ask_agent</c>: a question to an agent (or the host). Refused when the target already waits, directly or through others, on the
    /// asker, unless its own wait on the asker is interruptible: the question then goes to that waiting call. Otherwise the question is
    /// submitted to the target, and the asker waits for a reply record or the answer of the run the question starts. At the deadline the
    /// request detaches and its watcher reports the late answer once.
    /// </summary>
    public static async Task<string> AskAsync(AgentTeam team, MessagingPort port, Sender asker, string to, string question, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var context = port.Context.WithCancellation(cancellationToken);
        var host = to == MessagingCore.HostAddress;
        if (host && !asker.Trusted && asker.Definition?.CanAskHost != true)
            throw new AgentMessagingException("You may not ask the host (the agent definition does not allow it).");
        var requestId = "agents.ask:" + (port.ToolTask is { } toolTask ? toolTask.Value.ToString(CultureInfo.InvariantCulture) : port.NextId());
        var messageId = "q." + requestId;
        var (target, routed, request) = await port.CommitAsync(async tx =>
        {
            var directory = await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false);
            var snapshot = (JsonObjectValue)directory.ToValue();
            var live = await WaitGraph.LiveAsync(tx, snapshot).ConfigureAwait(false);
            var doc = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
            await Mailboxes.EnsureAsync(tx, asker.Address).ConfigureAwait(false);
            AgentEntry? entry = host ? null : AgentDirectory.Find(snapshot, to) ?? throw new AgentMessagingException($"There is no agent '{to}'; use list_agents.");
            if (entry is { Closed: true }) throw new AgentMessagingException($"Agent '{to}' was stopped.");
            if (entry is not null && asker.Entry is not null && !MessagingCore.CanReach(team, asker, entry))
                throw new AgentMessagingException($"You may not message '{to}' (outside your messaging scope).");
            if (doc.Get("to") is JsonStringValue)
                return (entry, doc.Get("routed") is JsonBooleanValue { Value: true }, (JsonObjectValue)doc.ToValue());
            var isRouted = false;
            if (entry is not null && WaitGraph.Reaches(live, to, asker.Address))
            {
                if (!live.Any(edge => edge.From == to && edge.To == asker.Address && edge.Interruptible))
                    throw new AgentMessagingException($"{to} is waiting on you, so it cannot answer a question now. Finish your work (your answer goes " +
                        $"to {to}), or reply to its question with reply(id, text); use send_message for a note that needs no answer.");
                isRouted = true;
            }
            var message = new AgentMessage(messageId, asker.Address, to, AgentMessageKind.Question, question, null, port.Now(), false);
            doc.Set("state", Requests.Waiting);
            doc.Set("kind", "ask");
            doc.Set("from", asker.Address);
            if (asker.Entry is not null) doc.Set("fromConversationId", asker.Entry.ConversationId.Value);
            doc.Set("to", to);
            if (entry is not null) doc.Set("child", entry.ConversationId.Value);
            doc.Set("input", Mailboxes.Header(message));
            doc.Set("entry", Mailboxes.EntryData(message));
            doc.Set("messageId", messageId);
            if (isRouted) doc.Set("routed", true);
            if (port.ToolTask is { } tool)
            {
                doc.Set("toolTaskId", tool.Value);
                WaitGraph.Write(directory, live, new WaitEdge(tool, asker.Address, to, "ask", false));
            }
            Mailboxes.Append(await Mailboxes.OpenAsync(tx, to).ConfigureAwait(false), message, requestId, isRouted);
            return (entry, isRouted, (JsonObjectValue)doc.ToValue());
        }, cancellationToken).ConfigureAwait(false);

        Task<SubmissionRecord>? settle = null;
        if (target is not null && !routed && await port.ConversationAsync(target.ConversationId, cancellationToken).ConfigureAwait(false) is { } handle)
            settle = (await SubmitAsync(handle.SubmitAsync, request, requestId, context).ConfigureAwait(false)).WaitAsync(context);
        var deadline = port.Now() + timeout.TotalMilliseconds;
        if (port.ToolTask is not null && port.Reader is IToolExecutionApi api)
            deadline = await DeadlineAsync(team, api, "deadline:" + requestId, timeout, context).ConfigureAwait(false);
        var changed = new SemaphoreSlim(0);
        var watch = await port.Observer.WatchDocAsync(Mailboxes.Doc, asker.Address, context).ConfigureAwait(false);
        watch?.Start((_, _, _) => { changed.Release(); return Task.CompletedTask; });
        try
        {
            while (true)
            {
                var mailbox = await port.Reader.SnapshotAsync(Mailboxes.Doc, asker.Address, context).ConfigureAwait(false);
                if (Mailboxes.Read(mailbox).FirstOrDefault(message => message.Kind == AgentMessageKind.Reply && message.CorrelationId == messageId) is { } reply)
                {
                    await FinishAskAsync(port, asker, requestId, Requests.Tool, reply.Id, false, cancellationToken).ConfigureAwait(false);
                    return reply.Text;
                }
                if (settle is { IsCompleted: true })
                {
                    var settled = await settle.ConfigureAwait(false);
                    var text = await port.CommitAsync(async tx =>
                    {
                        var answer = settled.Answer is { } entryId ? await tx.GetEntryAsync(Entries.AssistantEntry, entryId).ConfigureAwait(false) : null;
                        var doc = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
                        if (Requests.State(doc) == Requests.Waiting) doc.Set("state", Requests.Tool);
                        if (port.ToolTask is { } tool) WaitGraph.Remove(await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false), tool);
                        return answer?.Model is { Count: > 0 } messages && messages[0] is JsonObjectValue message ? ModelCalls.Text(message) : null;
                    }, cancellationToken).ConfigureAwait(false);
                    return text ?? $"{to} did not answer ({settled.Reason ?? settled.Status.ToString().ToLowerInvariant()}).";
                }
                if (port.Now() >= deadline)
                {
                    var report = settle is not null && asker.Entry is not null;
                    await FinishAskAsync(port, asker, requestId, report ? Requests.Detached : Requests.Expired, null, report, cancellationToken).ConfigureAwait(false);
                    if (asker.Trusted && asker.Entry is null) throw new TimeoutException($"{to} did not answer within {timeout}.");
                    return report
                        ? $"No answer from {to} within {timeout.TotalSeconds:0} s. Its answer will arrive as a message from {to}."
                        : $"No reply from {to} within {timeout.TotalSeconds:0} s.";
                }
                var waits = new List<Task>
                {
                    changed.WaitAsync(cancellationToken),
                    Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, deadline - port.Now())), port.Clock, cancellationToken),
                };
                if (settle is not null) waits.Add(settle);
                await Task.WhenAny(waits).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (watch is not null) await watch.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Ends an ask: sets its state, removes its wait edge, acknowledges the reply, and (when detaching) starts its watcher.</summary>
    private static Task FinishAskAsync(MessagingPort port, Sender asker, string requestId, string state, string? acknowledge, bool watch,
        CancellationToken cancellationToken) =>
        port.CommitAsync(async tx =>
        {
            var doc = await Requests.OpenAsync(tx, requestId).ConfigureAwait(false);
            var current = Requests.State(doc);
            if (current == Requests.Waiting) doc.Set("state", state);
            if (port.ToolTask is { } tool) WaitGraph.Remove(await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false), tool);
            if (acknowledge is not null) Mailboxes.Acknowledge(await Mailboxes.OpenAsync(tx, asker.Address).ConfigureAwait(false), acknowledge);
            if (watch && current == Requests.Waiting && asker.Entry is not null)
            {
                // The watcher must not wait on this call (it is ending now), so drop the call from the request first.
                doc.Delete("toolTaskId");
                await tx.CreateTaskAsync(Watcher, RunDocs.Object(("requestId", requestId)),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = asker.Entry.ConversationId, Background = true }).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);
}
