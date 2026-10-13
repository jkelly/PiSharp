// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/task-graph.ts (TaskGraphState, TaskGraphNode, TaskGraph,
// TaskGraphWatch, TaskGraphView, advance, nodeOf, stateOf, phaseOf).
//
// Upstream's TaskGraph is a plain JSON object {tasks: {[decimal id]: node}}; here it is a typed immutable class whose ToJson() is that
// object, and every operation a graph frame carries is relative to that JSON shape (["tasks", id] sets and deletes, ["tasks", id,
// "conversations"] sets), so applying a frame's operations to the previous revision's ToJson() yields the next revision's ToJson(). Task
// IDs are integers, which a JavaScript object enumerates in ascending numeric order whatever the insertion order; Tasks is sorted by ID
// and ToJson() writes it in that order. Revisions share unchanged nodes by reference, as upstream's applyImmutable does.
//
// Threading: as in View.cs, publications and attachment run on the Session line, while a close listener and an observer's detach (a
// disposed state, a stopped watch) may run on any thread, so the mount and its observer list are guarded by one lock that is never held
// while observer code runs.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>
/// A live task's durable status without its checkpoint and outcome payloads (upstream <c>TaskGraphState</c>, spec §9.5). JSON form
/// (<see cref="ToJson"/>): <c>{status, phase}</c> for pending and running, <c>{status: "waiting", phase, on, policy}</c>, or
/// <c>{status: "completing", outcome}</c>.
/// </summary>
public sealed class TaskGraphState : IEquatable<TaskGraphState>
{
    private readonly JsonObjectValue json;

    private TaskGraphState(DurableTaskStatus status, string? phase, ImmutableArray<TaskId> on, JoinPolicy? policy, TaskOutcomeStatus? outcome)
    {
        Status = status;
        Phase = phase;
        On = on;
        Policy = policy;
        Outcome = outcome;
        var writer = new RecordJson.Writer().Add("status", WireName.Of(status));
        if (outcome is { } held) writer.Add("outcome", WireName.Of(held));
        else
        {
            writer.Add("phase", phase);
            if (policy is { } join)
                writer.Add("on", JsonValue.Array(on.Select(id => (JsonValue)id.ToJson()))).Add("policy", WireName.Of(join));
        }
        json = writer.Build();
    }

    /// <summary><see cref="DurableTaskStatus.Pending"/>, <see cref="DurableTaskStatus.Running"/>, <see cref="DurableTaskStatus.Waiting"/>
    /// or <see cref="DurableTaskStatus.Completing"/>; terminal tasks leave the graph.</summary>
    public DurableTaskStatus Status { get; }

    /// <summary>The checkpoint's phase; null for a completing task.</summary>
    public string? Phase { get; }

    /// <summary>For a waiting task: the stored wait set. Empty otherwise.</summary>
    public ImmutableArray<TaskId> On { get; }

    /// <summary>For a waiting task: the stored join policy.</summary>
    public JoinPolicy? Policy { get; }

    /// <summary>For a completing task: the held outcome's status.</summary>
    public TaskOutcomeStatus? Outcome { get; }

    /// <summary>The state as upstream's JSON object.</summary>
    public JsonObjectValue ToJson() => json;

    /// <summary>Derives the graph state of a live record (upstream <c>stateOf</c>).</summary>
    internal static TaskGraphState Of(TaskState state) => state switch
    {
        WaitingTaskState waiting => new(DurableTaskStatus.Waiting, PhaseOf(waiting.Checkpoint), waiting.On, waiting.Policy, null),
        LiveTaskState live => new(live.Status, PhaseOf(live.Checkpoint), [], null, null),
        // Terminal records never reach here: they leave the graph.
        SettlingTaskState settling => new(DurableTaskStatus.Completing, null, [], null, settling.Outcome.Status),
        _ => throw new ArgumentException($"Unknown task state {state}", nameof(state)),
    };

    /// <summary>Upstream <c>phaseOf</c>: the checkpoint's <c>phase</c>; absent (null) when the checkpoint names none.</summary>
    private static string? PhaseOf(JsonValue checkpoint) =>
        checkpoint is JsonObjectValue value && value.TryGetValue("phase", out var phase) && phase is JsonStringValue text ? text.Value : null;

    /// <inheritdoc/>
    public bool Equals(TaskGraphState? other) => other is not null && JsonValue.DeepEquals(json, other.json);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as TaskGraphState);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Status, Phase);
    /// <inheritdoc/>
    public override string ToString() => json.ToJson();
}

/// <summary>
/// One live task of the graph (upstream <c>TaskGraphNode</c>): the committed record without its input, checkpoint and outcome payloads and
/// memos, plus the conversations it owns. JSON key order: <c>id, kind, conversationId, owner?, background, abortRequested, state,
/// conversations</c>.
/// </summary>
public sealed class TaskGraphNode : IEquatable<TaskGraphNode>
{
    private JsonObjectValue? json;

    internal TaskGraphNode(TaskId id, string kind, ConversationId conversationId, TaskId? owner, bool background, bool abortRequested,
        TaskGraphState state, ImmutableArray<ConversationId> conversations)
    {
        Id = id;
        Kind = kind;
        ConversationId = conversationId;
        Owner = owner;
        Background = background;
        AbortRequested = abortRequested;
        State = state;
        Conversations = conversations;
    }

    /// <summary>The task ID.</summary>
    public TaskId Id { get; }
    /// <summary>The registered task definition name.</summary>
    public string Kind { get; }
    /// <summary>The task's conversation.</summary>
    public ConversationId ConversationId { get; }
    /// <summary>Owner task; null for a conversation-owned task.</summary>
    public TaskId? Owner { get; }
    /// <summary>Whether the task is a background task.</summary>
    public bool Background { get; }
    /// <summary>Whether the durable abort mark is set.</summary>
    public bool AbortRequested { get; }
    /// <summary>The durable status.</summary>
    public TaskGraphState State { get; }
    /// <summary>Conversations this task owns, in ID order.</summary>
    public ImmutableArray<ConversationId> Conversations { get; }

    /// <summary>The node as upstream's JSON object; computed once.</summary>
    public JsonObjectValue ToJson()
    {
        if (Volatile.Read(ref json) is { } built) return built;
        var value = new RecordJson.Writer().Add("id", Id.ToJson()).Add("kind", Kind).Add("conversationId", ConversationId.ToJson())
            .Add("owner", Owner?.ToJson()).Add("background", Background).Add("abortRequested", AbortRequested).Add("state", State.ToJson())
            .Add("conversations", JsonValue.Array(Conversations.Select(id => (JsonValue)id.ToJson()))).Build();
        return Interlocked.CompareExchange(ref json, value, null) ?? value;
    }

    /// <summary>Upstream <c>nodeOf</c>.</summary>
    internal static TaskGraphNode Of(TaskRecord record, ImmutableArray<ConversationId> conversations) =>
        new(record.Id, record.Kind, record.ConversationId, record.Owner, record.Background, record.AbortRequested, TaskGraphState.Of(record.State),
            conversations);

    internal TaskGraphNode WithConversations(ImmutableArray<ConversationId> conversations) =>
        new(Id, Kind, ConversationId, Owner, Background, AbortRequested, State, conversations);

    /// <summary>Structural equality: equal JSON (upstream compares <c>JSON.stringify</c> of two nodes).</summary>
    public bool Equals(TaskGraphNode? other) =>
        other is not null && (ReferenceEquals(this, other) || string.Equals(ToJson().ToJson(), other.ToJson().ToJson(), StringComparison.Ordinal));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as TaskGraphNode);
    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode();
    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>
/// Every live task of the Session (upstream <c>TaskGraph</c>, spec §9.5). JSON form (<see cref="ToJson"/>): <c>{tasks}</c>, keyed by decimal
/// task ID in ascending order.
/// </summary>
public sealed class TaskGraph
{
    internal static readonly IComparer<TaskId> ById = Comparer<TaskId>.Create((left, right) => left.Value.CompareTo(right.Value));
    internal static readonly TaskGraph Empty = new(ImmutableSortedDictionary.Create<TaskId, TaskGraphNode>(ById));

    private JsonObjectValue? json;

    internal TaskGraph(ImmutableSortedDictionary<TaskId, TaskGraphNode> tasks) => Tasks = tasks;

    /// <summary>Every live task by ID, in ID order.</summary>
    public ImmutableSortedDictionary<TaskId, TaskGraphNode> Tasks { get; }

    /// <summary>The node of <paramref name="id"/>; null when the task is not live.</summary>
    public TaskGraphNode? Node(TaskId id) => Tasks.TryGetValue(id, out var node) ? node : null;

    /// <summary>The graph as upstream's JSON object <c>{tasks}</c>, which the operations of graph frames address. Computed once per revision.</summary>
    public JsonObjectValue ToJson()
    {
        if (Volatile.Read(ref json) is { } built) return built;
        var tasks = JsonValue.Object(Tasks.Select(pair => KeyValuePair.Create(Key(pair.Key), (JsonValue)pair.Value.ToJson())));
        var value = JsonValue.Object([KeyValuePair.Create("tasks", (JsonValue)tasks)]);
        return Interlocked.CompareExchange(ref json, value, null) ?? value;
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();

    internal static string Key(TaskId id) => id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The Harness's task graph mount (upstream <c>TaskGraphView</c>): built on the Session line by its first observer and dropped with its
/// last. It advances from the Session's commit publications, which are durable.
/// </summary>
internal sealed class TaskGraphView
{
    private static readonly DurableTaskStatus[] LiveStatuses =
        [DurableTaskStatus.Pending, DurableTaskStatus.Running, DurableTaskStatus.Waiting, DurableTaskStatus.Completing];
    private const int ScanPageSize = 256;

    private readonly DurableHarness session;
    private readonly IStorage storage;
    private readonly Lock gate = new();
    // Guarded by gate.
    private Mount? mount;
    private bool closed;

    public TaskGraphView(DurableHarness session, IStorage storage)
    {
        this.session = session;
        this.storage = storage;
        session.SubscribeCommits((publication, context) =>
        {
            Mount? current;
            lock (gate) current = mount;
            if (current is not null) Advance(current, publication, context);
        });
        session.SubscribeClose(() =>
        {
            Mount? current;
            lock (gate)
            {
                closed = true;
                current = mount;
                mount = null;
            }
            if (current is null) return;
            foreach (var observer in current.Snapshot(gate)) observer.CloseSession();
        });
    }

    /// <summary>A disposable read-only Chord state of the graph (upstream <c>state</c>).</summary>
    public async Task<AttachedReplicatedState<TaskGraph>> StateAsync(DurableContext context)
    {
        var (source, detach) = await AttachAsync((value, release) => new CommittedStateSource<TaskGraph>(value, release, session.Defer), context)
            .ConfigureAwait(false);
        try
        {
            return ReplicatedState.Attach<TaskGraph>(source);
        }
        catch
        {
            detach();
            throw;
        }
    }

    /// <summary>A serialized exact-frame watch of the graph; cancelling <paramref name="context"/> stops it (upstream <c>watch</c>).</summary>
    public async Task<IWatchHandle<TaskGraph>> WatchAsync(DurableContext context)
    {
        var (watch, _) = await AttachAsync(
            (value, release) => new CommittedWatch<TaskGraph>(value, release, session.Defer, json: graph => graph.ToJson()), context)
            .ConfigureAwait(false);
        if (context.IsCancellationRequested)
        {
            watch.Cancel();
            context.ThrowIfCancellationRequested();
        }
        if (context.HasCancellation) watch.ObserveCancellation(context);
        return watch;
    }

    /// <summary>Registers an observer created from the current revision, atomically on the Session line (upstream <c>attach</c>).</summary>
    private Task<(T Observer, Action Detach)> AttachAsync<T>(Func<TaskGraph, Action, T> create, DurableContext context)
        where T : ICommittedObserver<TaskGraph> =>
        session.ReadOnLineAsync(async () =>
        {
            Mount? existing;
            lock (gate) existing = mount;
            var target = existing ?? new Mount(await BuildAsync(context).ConfigureAwait(false));
            ICommittedObserver<TaskGraph>? registered = null;
            void Detach()
            {
                lock (gate)
                {
                    if (registered is not null) target.Observers.Remove(registered);
                    if (target.Observers.Count == 0 && ReferenceEquals(mount, target)) mount = null;
                }
            }
            var observer = create(target.Value, Detach);
            registered = observer;
            lock (gate)
            {
                // Close or cancellation may begin while the mount builds; register nothing then.
                if (closed) throw HarnessUtil.ClosedError();
                context.ThrowIfCancellationRequested();
                mount = target;
                target.Observers.Add(observer);
            }
            return (observer, (Action)Detach);
        });

    private async Task<TaskGraph> BuildAsync(DurableContext context)
    {
        var records = new List<TaskRecord>();
        foreach (var status in LiveStatuses)
            records.AddRange(await HarnessUtil.ScanAll(cursor =>
                storage.ScanTasksAsync(new TaskQuery { Status = status }, ScanPageSize, cursor, context)).ConfigureAwait(false));
        records.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        var tasks = ImmutableSortedDictionary.CreateBuilder<TaskId, TaskGraphNode>(TaskGraph.ById);
        foreach (var record in records)
        {
            var owned = await HarnessUtil.ScanAll(cursor =>
                storage.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = record.Id }, ScanPageSize, cursor, context)).ConfigureAwait(false);
            tasks[record.Id] = TaskGraphNode.Of(record, [.. owned.Select(conversation => conversation.Id).OrderBy(id => id.Value)]);
        }
        return new TaskGraph(tasks.ToImmutable());
    }

    /// <summary>Derives the mount's operations from one publication, applies them, and hands the revision to every observer (upstream
    /// <c>advance</c>). Runs on the Session line.</summary>
    private void Advance(Mount target, CommitPublication publication, DurableContext context)
    {
        var ops = new List<Op>();
        // Nodes this publication set or deleted, over the mount's value.
        var changed = new Dictionary<TaskId, TaskGraphNode?>();
        var tasks = target.Value.Tasks;
        TaskGraphNode? Node(TaskId id) => changed.TryGetValue(id, out var node) ? node : tasks.GetValueOrDefault(id);
        foreach (var change in publication.Changes)
        {
            if (change is not TaskWrite { Value: var record }) continue;
            var key = TaskGraph.Key(record.Id);
            var previous = Node(record.Id);
            if (record.State is TerminalTaskState)
            {
                if (previous is null) continue;
                ops.Add(new DeleteOp([PathSegment.Of("tasks"), PathSegment.Of(key)]));
                changed[record.Id] = null;
                continue;
            }
            var next = TaskGraphNode.Of(record, previous?.Conversations ?? []);
            if (previous is not null && next.Equals(previous)) continue;
            ops.Add(new SetOp([PathSegment.Of("tasks"), PathSegment.Of(key)], next.ToJson()));
            changed[record.Id] = next;
        }
        // After the tasks, so a conversation created with its owner task in one commit finds the owner's node. Change order within a
        // publication is unspecified, so each owner's list is sorted again. Owners keep their first-seen order, as upstream's Map does.
        var created = new List<(TaskId Owner, List<ConversationId> Ids)>();
        foreach (var change in publication.Changes)
        {
            if (change is not ConversationWrite { Value: { Owner: { } owner } conversation }) continue;
            if (Node(owner.TaskId) is null) continue;
            var index = created.FindIndex(entry => entry.Owner == owner.TaskId);
            if (index < 0) created.Add((owner.TaskId, [conversation.Id]));
            else created[index].Ids.Add(conversation.Id);
        }
        foreach (var (owner, ids) in created)
        {
            var node = Node(owner)!;
            ImmutableArray<ConversationId> conversations = [.. node.Conversations.Concat(ids).OrderBy(id => id.Value)];
            ops.Add(new SetOp([PathSegment.Of("tasks"), PathSegment.Of(TaskGraph.Key(owner)), PathSegment.Of("conversations")],
                JsonValue.Array(conversations.Select(id => (JsonValue)id.ToJson()))));
            changed[owner] = node.WithConversations(conversations);
        }
        if (ops.Count == 0) return;
        var builder = tasks.ToBuilder();
        foreach (var (id, node) in changed)
        {
            if (node is null) builder.Remove(id);
            else builder[id] = node;
        }
        target.Value = new TaskGraph(builder.ToImmutable());
        ImmutableArray<Op> frame = [.. ops];
        var frameContext = context.WithoutCancellation();
        foreach (var observer in target.Snapshot(gate)) observer.Advance(target.Value, frame, frameContext);
    }

    /// <summary>The mount: its current revision and its observers.</summary>
    private sealed class Mount(TaskGraph value)
    {
        /// <summary>Written on the Session line only.</summary>
        public TaskGraph Value { get; set; } = value;
        /// <summary>In registration order; guarded by the view's lock.</summary>
        public List<ICommittedObserver<TaskGraph>> Observers { get; } = [];

        public ICommittedObserver<TaskGraph>[] Snapshot(Lock gate)
        {
            lock (gate) return [.. Observers];
        }
    }
}
