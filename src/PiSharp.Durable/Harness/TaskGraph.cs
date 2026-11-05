// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/task-graph.ts (TaskGraphState, TaskGraphNode, TaskGraph,
// TaskGraphWatch, TaskGraphView, advance, nodeOf, stateOf, phaseOf).
//
// Upstream's TaskGraph is a plain JSON object {tasks: {[decimal id]: node}}; here it is a typed immutable class whose ToJson() is that
// object, and every operation a graph frame carries is relative to that JSON shape (["tasks", id] sets and deletes, ["tasks", id,
// "conversations"] sets), so applying a frame's operations to the previous revision's ToJson() yields the next revision's ToJson(). Task
// IDs are integers, which a JavaScript object enumerates in ascending numeric order whatever the insertion order; Tasks is sorted by ID
// and ToJson() writes it in that order. Revisions share unchanged nodes by reference, as upstream's applyImmutable does.
//
// Threading: as in View.cs, publications and attachment run on the Session line, while a close listener and an observer's detach (a
// stopped watch) may run on any thread, so the mount and its observer list are guarded by one lock that is never held while observer code
// runs.
//
// Pi v1.5.0 sync (area reports): TaskGraphNode.blocked (a30c2a81d, 440a5fcdb). The mount keeps what the scheduler's blocked reason reads
// of each live task (BlockedSubject), in upstream's Map order; a registry change or a failed migration derives every reason again on the
// Session line and publishes the changed ones as set or delete ops on ["tasks", id, "blocked"], with the background context.
// Pi v1.5.0 sync (area watches, 92ab53602 and 42cb822f1): a task graph watch is Chord's Watch (Chord/Watch.cs) on the Session line's
// scheduler, and the task graph state getter (Harness.taskGraph()) is removed.
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): the graph observes the Session's commits as memory adopts them
// (Session.FollowCommits) and skips a held one, which changes conversation documents only.
// Pi v1.5.0 sync (area observers, 8b0caefc2 with v1.5.0's shapes): Harness.observeTaskGraph() attaches a host's Observer<T> to the mount
// behind GuardedObserver<T> (ObserveAsync, upstream observe); the mount's observers are SyncObserver<TaskGraph>s, watches (WatchObserver<T>)
// and guarded host observers, which a publication hands one value call. The Session's close ends a watch ended and a host observer failed
// with "session closed" (CloseEnds). Pi v1.5.0 sync (area vocabulary, 5f312dac4): the internal observeCommits is followCommits
// (Session.FollowCommits). The citation stays on v1.2.0 until the file's other areas land.
// Pi v1.5.0 sync (area events, f3255de76): the task graph view is spec §9.4, renumbered from §9.5 with the agent events' section removed.
// Pi v1.5.0 sync (area group-commit-scheduler, e5c6182ba): the mount is built on the line through the line's Storage (ReadOnLineAsync's
// argument), whose scans wait for every unpublished commit, so nothing a publication then applies is in the mount already; the guarded
// Storage field is gone.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>
/// A live task's durable status without its checkpoint and outcome payloads (upstream <c>TaskGraphState</c>, spec §9.4). JSON form
/// (<see cref="ToJson"/>): <c>{status, phase}</c> for pending and running, <c>{status: "waiting", phase, on, policy}</c>,
/// <c>{status: "sleeping", phase, until}</c>, or <c>{status: "completing", outcome}</c>.
/// </summary>
public sealed class TaskGraphState : IEquatable<TaskGraphState>
{
    private readonly JsonObjectValue json;

    private TaskGraphState(DurableTaskStatus status, string? phase, ImmutableArray<TaskId> on, JoinPolicy? policy, TaskOutcomeStatus? outcome,
        double? until = null)
    {
        Status = status;
        Phase = phase;
        On = on;
        Policy = policy;
        Outcome = outcome;
        Until = until;
        var writer = new RecordJson.Writer().Add("status", WireName.Of(status));
        if (outcome is { } held) writer.Add("outcome", WireName.Of(held));
        else
        {
            writer.Add("phase", phase);
            if (policy is { } join)
                writer.Add("on", JsonValue.Array(on.Select(id => (JsonValue)id.ToJson()))).Add("policy", WireName.Of(join));
            writer.Number("until", until);
        }
        json = writer.Build();
    }

    /// <summary><see cref="DurableTaskStatus.Pending"/>, <see cref="DurableTaskStatus.Running"/>, <see cref="DurableTaskStatus.Waiting"/>,
    /// <see cref="DurableTaskStatus.Sleeping"/> or <see cref="DurableTaskStatus.Completing"/>; terminal tasks leave the graph.</summary>
    public DurableTaskStatus Status { get; }

    /// <summary>The checkpoint's phase; null for a completing task.</summary>
    public string? Phase { get; }

    /// <summary>For a waiting task: the stored wait set. Empty otherwise.</summary>
    public ImmutableArray<TaskId> On { get; }

    /// <summary>For a waiting task: the stored join policy.</summary>
    public JoinPolicy? Policy { get; }

    /// <summary>For a completing task: the held outcome's status.</summary>
    public TaskOutcomeStatus? Outcome { get; }

    /// <summary>For a sleeping task: when it is due, on the Harness clock.</summary>
    public double? Until { get; }

    /// <summary>The state as upstream's JSON object.</summary>
    public JsonObjectValue ToJson() => json;

    /// <summary>Derives the graph state of a live record (upstream <c>stateOf</c>).</summary>
    internal static TaskGraphState Of(TaskState state) => state switch
    {
        WaitingTaskState waiting => new(DurableTaskStatus.Waiting, PhaseOf(waiting.Checkpoint), waiting.On, waiting.Policy, null),
        SleepingTaskState sleeping => new(DurableTaskStatus.Sleeping, PhaseOf(sleeping.Checkpoint), [], null, null, sleeping.Until),
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
/// memos, plus the conversations it owns and why it cannot run here, if it cannot. JSON key order: <c>id, kind, conversationId, owner?,
/// background, abortRequested, state, conversations, blocked?</c>.
/// </summary>
public sealed class TaskGraphNode : IEquatable<TaskGraphNode>
{
    private JsonObjectValue? json;

    internal TaskGraphNode(TaskId id, string kind, ConversationId conversationId, TaskId? owner, bool background, bool abortRequested,
        TaskGraphState state, ImmutableArray<ConversationId> conversations, BlockedReason? blocked = null)
    {
        Id = id;
        Kind = kind;
        ConversationId = conversationId;
        Owner = owner;
        Background = background;
        AbortRequested = abortRequested;
        State = state;
        Conversations = conversations;
        Blocked = blocked;
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
    /// <summary>
    /// Why it cannot run here, when it cannot: no registered definition takes it (<see cref="BlockedReason.MissingTask"/>,
    /// <see cref="BlockedReason.TaskTooOld"/>, <see cref="BlockedReason.MigrationFailed"/>). Follows registry changes. Null otherwise; JSON
    /// <c>blocked</c>, absent when null.
    /// </summary>
    public BlockedReason? Blocked { get; }

    /// <summary>The node as upstream's JSON object; computed once.</summary>
    public JsonObjectValue ToJson()
    {
        if (Volatile.Read(ref json) is { } built) return built;
        var value = new RecordJson.Writer().Add("id", Id.ToJson()).Add("kind", Kind).Add("conversationId", ConversationId.ToJson())
            .Add("owner", Owner?.ToJson()).Add("background", Background).Add("abortRequested", AbortRequested).Add("state", State.ToJson())
            .Add("conversations", JsonValue.Array(Conversations.Select(id => (JsonValue)id.ToJson())))
            .Add("blocked", Blocked is { } blocked ? TaskInspectionState.ReasonName(blocked) : null).Build();
        return Interlocked.CompareExchange(ref json, value, null) ?? value;
    }

    /// <summary>Upstream <c>nodeOf</c>.</summary>
    internal static TaskGraphNode Of(TaskRecord record, ImmutableArray<ConversationId> conversations, BlockedReason? blocked) =>
        new(record.Id, record.Kind, record.ConversationId, record.Owner, record.Background, record.AbortRequested, TaskGraphState.Of(record.State),
            conversations, blocked);

    internal TaskGraphNode WithConversations(ImmutableArray<ConversationId> conversations) =>
        new(Id, Kind, ConversationId, Owner, Background, AbortRequested, State, conversations, Blocked);

    internal TaskGraphNode WithBlocked(BlockedReason? blocked) =>
        new(Id, Kind, ConversationId, Owner, Background, AbortRequested, State, Conversations, blocked);

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
/// Every live task of the Session (upstream <c>TaskGraph</c>, spec §9.4). JSON form (<see cref="ToJson"/>): <c>{tasks}</c>, keyed by decimal
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
        [DurableTaskStatus.Pending, DurableTaskStatus.Running, DurableTaskStatus.Waiting, DurableTaskStatus.Sleeping, DurableTaskStatus.Completing];
    private const int ScanPageSize = 256;

    private readonly DurableHarness session;
    /// <summary>The scheduler's blocked reason of a task (upstream <c>TaskGraphBlocking.blocked</c>).</summary>
    private readonly Func<BlockedSubject, BlockedReason?> blocked;
    private readonly Lock gate = new();
    // Guarded by gate.
    private Mount? mount;
    private bool closed;

    /// <param name="session">The Harness.</param>
    /// <param name="blocked">A task's blocked reason (upstream <c>TaskGraphBlocking.blocked</c>).</param>
    /// <param name="subscribeBlocked">Subscribes to changes of blocked reasons without a commit (upstream
    /// <c>TaskGraphBlocking.subscribeBlocked</c>).</param>
    public TaskGraphView(DurableHarness session, Func<BlockedSubject, BlockedReason?> blocked, Func<Action, Action> subscribeBlocked)
    {
        this.session = session;
        this.blocked = blocked;
        session.FollowCommits((publication, context) =>
        {
            // A commit held in memory changes documents only, which the graph does not show.
            if (publication.IsMemory) return;
            Mount? current;
            lock (gate) current = mount;
            if (current is not null) Advance(current, publication, context, session.Report);
        });
        subscribeBlocked(RefreshBlocked);
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
            var ends = CloseEnds.Of(session.Failure);
            foreach (var observer in current.Snapshot(gate)) Session.Contained(() => observer.End(ends.For(observer.IsWatch)), session.Report);
        });
    }

    /// <summary>
    /// Attaches a synchronous observer of the graph (<see cref="IHarness.ObserveTaskGraphAsync"/>), behind <see cref="GuardedObserver{T}"/>;
    /// <paramref name="context"/> governs only the attachment (upstream <c>observe</c>).
    /// </summary>
    public async Task<Observation<TaskGraph>> ObserveAsync(Observer<TaskGraph> observer, DurableContext context)
    {
        TaskGraph? value = null;
        var (guarded, _) = await AttachAsync((current, release) =>
        {
            value = current;
            return new GuardedObserver<TaskGraph>(observer, release, (error, _) => session.Report(error));
        }, context).ConfigureAwait(false);
        if (context.IsCancellationRequested)
        {
            guarded.Detach();
            context.ThrowIfCancellationRequested();
        }
        return new Observation<TaskGraph>(value!, guarded);
    }

    /// <summary>A serialized watch of the graph, Chord's <see cref="Watch{T}"/>, an observer of its mount; cancelling
    /// <paramref name="context"/> ends it aborted (upstream <c>watch</c>).</summary>
    public async Task<IWatchHandle<TaskGraph>> WatchAsync(DurableContext context)
    {
        var (observer, _) = await AttachAsync(
            (value, release) => new WatchObserver<TaskGraph>(
                new Watch<TaskGraph>(value, release, session.Report, graph => graph.ToJson(), session.Defer)), context)
            .ConfigureAwait(false);
        var watch = observer.Watch;
        if (context.IsCancellationRequested)
        {
            watch.Abort();
            context.ThrowIfCancellationRequested();
        }
        if (context.HasCancellation) watch.ObserveAbort(context.CancellationToken);
        return watch;
    }

    /// <summary>Registers an observer created from the current revision, atomically on the Session line (upstream <c>attach</c>).</summary>
    private Task<(TObserver Observer, Action Detach)> AttachAsync<TObserver>(Func<TaskGraph, Action, TObserver> create, DurableContext context)
        where TObserver : SyncObserver<TaskGraph> =>
        session.ReadOnLineAsync(async line =>
        {
            Mount? existing;
            lock (gate) existing = mount;
            var target = existing ?? await BuildAsync(line, context).ConfigureAwait(false);
            SyncObserver<TaskGraph>? registered = null;
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
                if (closed) throw HarnessUtil.ClosedError(session);
                context.ThrowIfCancellationRequested();
                mount = target;
                target.Observers.Add(observer);
            }
            return (observer, (Action)Detach);
        });

    /// <summary>On the Session line: derives every node's blocked reason again, and publishes the ones that changed (upstream
    /// <c>#refreshBlocked</c>).</summary>
    private void RefreshBlocked()
    {
        lock (gate) if (mount is null || closed) return;
        // Rejected only once the Session is closing or failed.
        Session.Observe(session.ReadOnLineAsync(() =>
        {
            Mount? current;
            lock (gate) current = mount;
            if (current is null) return Task.FromResult(false);
            var ops = new List<Op>();
            var changed = new Dictionary<TaskId, TaskGraphNode?>();
            foreach (var (id, subject) in current.Records)
            {
                var reason = blocked(subject);
                if (current.Value.Node(id) is not { } node || reason == node.Blocked) continue;
                var path = ImmutableArray.Create(PathSegment.Of("tasks"), PathSegment.Of(TaskGraph.Key(id)), PathSegment.Of("blocked"));
                ops.Add(reason is { } set ? new SetOp(path, TaskInspectionState.ReasonName(set)) : new DeleteOp(path));
                changed[id] = node.WithBlocked(reason);
            }
            Publish(current, ops, changed, DurableContext.Background, session.Report);
            return Task.FromResult(true);
        }), _ => { });
    }

    /// <summary>Reads the graph on the line, through the line's <paramref name="storage"/>.</summary>
    private async Task<Mount> BuildAsync(IStorage storage, DurableContext context)
    {
        var records = new List<TaskRecord>();
        foreach (var status in LiveStatuses)
            records.AddRange(await HarnessUtil.ScanAll(cursor =>
                storage.ScanTasksAsync(new TaskQuery { Status = status }, ScanPageSize, cursor, context)).ConfigureAwait(false));
        records.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        var tasks = ImmutableSortedDictionary.CreateBuilder<TaskId, TaskGraphNode>(TaskGraph.ById);
        var live = new OrderedDictionary<TaskId, BlockedSubject>();
        foreach (var record in records)
        {
            var owned = await HarnessUtil.ScanAll(cursor =>
                storage.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = record.Id }, ScanPageSize, cursor, context)).ConfigureAwait(false);
            var subject = BlockedSubject.Of(record);
            tasks[record.Id] = TaskGraphNode.Of(record, [.. owned.Select(conversation => conversation.Id).OrderBy(id => id.Value)], blocked(subject));
            live[record.Id] = subject;
        }
        return new Mount(new TaskGraph(tasks.ToImmutable()), live);
    }

    /// <summary>Derives the mount's operations from one publication, applies them, and hands the revision to every observer (upstream
    /// <c>advance</c>). Runs on the Session line.</summary>
    private void Advance(Mount target, ObservedCommit publication, DurableContext context, Action<Exception> report)
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
                target.Records.Remove(record.Id);
                if (previous is null) continue;
                ops.Add(new DeleteOp([PathSegment.Of("tasks"), PathSegment.Of(key)]));
                changed[record.Id] = null;
                continue;
            }
            var subject = BlockedSubject.Of(record);
            target.Records[record.Id] = subject;
            var next = TaskGraphNode.Of(record, previous?.Conversations ?? [], blocked(subject));
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
        Publish(target, ops, changed, context, report);
    }

    /// <summary>Applies <paramref name="ops"/>, whose nodes are <paramref name="changed"/>, to the mount and hands the revision to every
    /// observer; one that throws is reported (upstream <c>publish</c>). Runs on the Session line.</summary>
    private void Publish(Mount target, List<Op> ops, Dictionary<TaskId, TaskGraphNode?> changed, DurableContext context, Action<Exception> report)
    {
        if (ops.Count == 0) return;
        var builder = target.Value.Tasks.ToBuilder();
        foreach (var (id, node) in changed)
        {
            if (node is null) builder.Remove(id);
            else builder[id] = node;
        }
        target.Value = new TaskGraph(builder.ToImmutable());
        ImmutableArray<Op> frame = [.. ops];
        var frameContext = context.WithoutCancellation();
        var call = new WatchValueCall<TaskGraph>(target.Value, frame);
        foreach (var observer in target.Snapshot(gate)) Session.Contained(() => observer.Advance(call, frameContext), report);
    }

    /// <summary>The mount: its current revision, its observers, and what the blocked reason reads of each live task, so a registry change
    /// can derive it again without payloads.</summary>
    private sealed class Mount(TaskGraph value, OrderedDictionary<TaskId, BlockedSubject> records)
    {
        /// <summary>Written on the Session line only.</summary>
        public TaskGraph Value { get; set; } = value;
        /// <summary>Upstream's <c>records</c>, in its Map order. Read and written on the Session line only.</summary>
        public OrderedDictionary<TaskId, BlockedSubject> Records { get; } = records;
        /// <summary>Watches and guarded host observers, in registration order; guarded by the view's lock.</summary>
        public List<SyncObserver<TaskGraph>> Observers { get; } = [];

        public SyncObserver<TaskGraph>[] Snapshot(Lock gate)
        {
            lock (gate) return [.. Observers];
        }
    }
}
