// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): the support half of packages/durable/test/harness-structured.test.ts: the scriptable
// test.node task, its gates and log, and the helpers its task-runtime describes share. The cases are in HarnessStructuredCases.cs.
//
// Adaptations (beyond those of HarnessSupport.cs):
// - Upstream keeps behaviors, gates and the log at module scope and clears them after each test. Here each case builds a fresh Nodes
//   fixture, whose test.node definition closes over that case's behaviors, gates and log, so cases share nothing.
// - Phase and abort handlers run on thread-pool threads, so the log is a SyncList and the maps are concurrent.
// - until() (chat-support's waitFor, a 5 ms poll) is Eventually.
// - Upstream's `class Rejecting extends MemoryStorage` is RejectingStorage, a decorator over any IStorage, so the case runs on every
//   backend.
// - Recovery seeds write records through a bare Session over the reopenable storage and its internal Transaction.SetTask, as upstream
//   writes them through createSession and the internal tx.setTask.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static HarnessTests;
using static Program;
using static SessionTests;

/// <summary>Support of the harness.structured.* cases (upstream harness-structured.test.ts).</summary>
internal static partial class StructuredTests
{
    internal static readonly DurableContext Context = DurableContext.Background;

    /// <summary>How a default run ends once its gate opens: its outcome, or a throw that faults it (upstream <c>Ending</c>).</summary>
    internal enum Ending
    {
        Completed,
        Failed,
        Throw,
    }

    /// <summary>Per-name behavior of test.node; unscripted parts use the defaults (upstream <c>Behavior</c>).</summary>
    internal sealed record Behavior
    {
        public Func<ITaskRuntime, DurableContext, Task>? Run { get; init; }
        public Func<ITaskRuntime, DurableContext, int, Task>? Resume { get; init; }
        public Func<ITaskRuntime, DurableContext, Task>? Abort { get; init; }
    }

    /// <summary>Upstream <c>OWN_CONVERSATION</c>.</summary>
    internal static readonly TaskOptions OwnConversation = new(TaskOwnership.Conversation);

    /// <summary>Upstream <c>owned(owner)</c>.</summary>
    internal static TaskOptions Owned(TaskId owner) => new(TaskOwnership.Task(owner));

    /// <summary>Upstream <c>OWN_CONVERSATION</c> in <paramref name="conversation"/>.</summary>
    internal static TaskOptions In(ConversationId conversation) => OwnConversation with { ConversationId = conversation };

    /// <summary>A background conversation-owned task.</summary>
    internal static readonly TaskOptions Background = OwnConversation with { Background = true };

    /// <summary>Never registered: aborting it can only orphan it (upstream <c>Unregistered</c>).</summary>
    internal static readonly DurableTask Unregistered = Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.unregistered",
        Version = 1,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
        Abort = (_, _, _) => Task.CompletedTask,
    });

    /// <summary>Upstream <c>TaskNotes</c>.</summary>
    internal static readonly TaskDocToken TaskNotes = Documents.DefineTaskDoc("test.task-notes", 1, () => O("""{"text":""}"""));

    /// <summary>The input of a node named <paramref name="name"/>.</summary>
    internal static JsonObjectValue NodeInput(string name) => Obj(("name", name));

    /// <summary>Upstream <c>end(ending, name)</c>.</summary>
    internal static TaskState End(Ending ending, string name) => ending == Ending.Completed
        ? NextTaskState.Completed(name)
        : NextTaskState.Failed($"{name} failed");

    /// <summary>Upstream <c>waitOn(on, policy, round)</c>: wait on <paramref name="on"/>, resuming in round <paramref name="round"/>.</summary>
    internal static TaskState WaitOn(IEnumerable<TaskId> on, JoinPolicy policy, int round = 1) =>
        NextTaskState.Waiting(Obj(("phase", "resume"), ("round", round)), on, policy);

    /// <summary><c>{status: "running", checkpoint: {phase: "resume", round}}</c>.</summary>
    internal static TaskState ResumeAt(int round) => NextTaskState.Running(Obj(("phase", "resume"), ("round", round)));

    /// <summary>The persisted name of an enum member.</summary>
    internal static string Wire<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Upstream <c>state(harness, id)</c>.</summary>
    internal static async Task<TaskState> State(IHarness harness, TaskId id) => (await harness.GetTaskAsync(id, Context))!.State;

    /// <summary>The status of a task's state.</summary>
    internal static async Task<DurableTaskStatus> StatusOf(IHarness harness, TaskId id) => (await State(harness, id)).Status;

    /// <summary>Upstream <c>outcomeOf(harness, id)</c>: the outcome status of the terminal receipt.</summary>
    internal static async Task<string> OutcomeOf(IHarness harness, TaskId id) =>
        Wire(((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status);

    /// <summary>The outcome of a settling record.</summary>
    internal static TaskOutcome OutcomeIn(TaskRecord record) => ((SettlingTaskState)record.State).Outcome;

    /// <summary>The outcome of a settling state.</summary>
    internal static TaskOutcome OutcomeIn(TaskState state) => ((SettlingTaskState)state).Outcome;

    /// <summary><c>toMatchObject({status: "faulted", error: {message: expect.stringContaining(fragment)}})</c>.</summary>
    internal static void Faulted(TaskOutcome outcome, string fragment, string what)
    {
        Check(outcome is FaultedTaskOutcome, $"{what}: expected faulted, actual {outcome.ToJson().ToJson()}");
        Rejects(new InvalidOperationException(((FaultedTaskOutcome)outcome).Error.Message), fragment, what);
    }

    /// <summary>Upstream's <c>TaskInspection["state"]</c> as JSON, for toEqual against the object upstream writes.</summary>
    internal static JsonObjectValue InspectionJson(TaskInspectionState state)
    {
        var properties = new List<(string, JsonValue)> { ("kind", Wire(state.Kind)) };
        if (state.Migrates) properties.Add(("migrates", true));
        if (state.Kind == TaskInspectionKind.Waiting) properties.Add(("on", JsonValue.Array(state.On.Select(id => (JsonValue)id.ToJson()))));
        if (state.Reason is { } reason) properties.Add(("reason", TaskInspectionState.ReasonName(reason)));
        if (state.Error is { } error) properties.Add(("error", error.Message));
        return Obj([.. properties]);
    }

    /// <summary>The inspected state of <paramref name="id"/> as JSON; null when it is not live.</summary>
    internal static async Task<JsonObjectValue?> Inspected(IHarness harness, TaskId id) =>
        (await harness.InspectAsync(Context)).Tasks.FirstOrDefault(task => task.Record.Id == id) is { } found ? InspectionJson(found.State) : null;

    /// <summary>Waits until <paramref name="id"/> is assigned and its record is committed.</summary>
    /// <remarks>
    /// Upstream assigns IDs inside a commit callback and polls for the assignment with a 5 ms timer, by which time its in-memory commit has
    /// long landed. Handlers here run on other threads, so the barrier also waits for the record.
    /// </remarks>
    internal static Task Committed(IHarness harness, Func<TaskId?> id) =>
        Eventually(async () => id() is { } found && await harness.GetTaskAsync(found, Context) is not null);

    /// <summary>A Harness running test.node, its root conversation, and its registry and reports (upstream <c>Opened</c>).</summary>
    internal sealed record Opened(IHarness Harness, IConversation Root, IRegistry Registry, SyncList<Exception> Reports);

    /// <summary>One case's test.node definition with its scripts, gates and handler log.</summary>
    internal sealed class Nodes
    {
        private readonly ConcurrentDictionary<string, Behavior> behaviors = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Deferred<Ending>> gates = new(StringComparer.Ordinal);

        public Nodes()
        {
            Node = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.node",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (task, runtime, ctx) =>
                    {
                        var name = NameOf(task.Input);
                        Log.Add($"run:{name}");
                        var behavior = behaviors.GetValueOrDefault(name)?.Run;
                        await (behavior is null ? DefaultRun(runtime, ctx, name) : behavior(runtime, ctx));
                    },
                    ["resume"] = async (task, runtime, ctx) =>
                    {
                        var name = NameOf(task.Input);
                        Log.Add($"resume:{name}");
                        var behavior = behaviors.GetValueOrDefault(name)?.Resume;
                        if (behavior is not null)
                        {
                            await behavior(runtime, ctx, (int)N(((JsonObjectValue)task.Checkpoint)["round"]));
                            return;
                        }
                        await runtime.CommitAsync((_, _) => End(Ending.Completed, name), ctx);
                    },
                },
                Abort = async (task, runtime, ctx) =>
                {
                    var name = NameOf(task.Input);
                    Log.Add($"abort:{name}");
                    var behavior = behaviors.GetValueOrDefault(name)?.Abort;
                    if (behavior is not null)
                    {
                        await behavior(runtime, ctx);
                        return;
                    }
                    await runtime.CommitAsync((_, _) => NextTaskState.Terminal(new AbortedTaskOutcome(null, name)), ctx);
                },
            });
        }

        /// <summary>A task that runs its name's behavior (upstream <c>Node</c>).</summary>
        public DurableTask Node { get; }

        /// <summary>Handler starts in order, such as <c>run:a</c>, <c>abort:a</c>, plus what scripts add (upstream <c>log</c>).</summary>
        public SyncList<string> Log { get; } = new();

        /// <summary>The log lines.</summary>
        public List<string> Lines => Log.Snapshot();

        /// <summary>Whether the log holds <paramref name="line"/>.</summary>
        public bool Logged(string line) => Log.Snapshot().Contains(line);

        /// <summary>The log lines starting with <c>abort:</c>.</summary>
        public List<string> Aborts => [.. Log.Snapshot().Where(line => line.StartsWith("abort:", StringComparison.Ordinal))];

        /// <summary>Upstream <c>gate(name)</c>.</summary>
        public Deferred<Ending> Gate(string name) => gates.GetOrAdd(name, _ => new Deferred<Ending>());

        /// <summary>Upstream <c>open(name, ending)</c>.</summary>
        public void Open(string name, Ending ending = Ending.Completed) => Gate(name).Resolve(ending);

        /// <summary>Upstream <c>script(name, behavior)</c>.</summary>
        public void Script(string name, Behavior behavior) => behaviors[name] = behavior;

        /// <summary>Upstream <c>spawn(tx, owner, name)</c>: a child task named <paramref name="name"/>, owned by <paramref name="owner"/>.</summary>
        public Task<TaskId> Spawn(ITx tx, TaskId owner, string name) => tx.CreateTaskAsync(Node, NodeInput(name), Owned(owner));

        /// <summary>Upstream <c>start(conversation, name, options)</c>.</summary>
        public Task<TaskId> Start(IConversation conversation, string name, TaskOptions? options = null) =>
            conversation.CommitAsync(tx => tx.CreateTaskAsync(Node, NodeInput(name), options ?? OwnConversation), Context);

        /// <summary>Upstream <c>openNodes(storage)</c>: a resumed Harness running test.node and its root.</summary>
        public async Task<Opened> OpenNodes(IStorage storage)
        {
            var opened = await OpenTasks(storage, [Node]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            return new Opened(opened.Harness, root, opened.Registry, opened.Reports);
        }

        /// <summary>
        /// Upstream <c>parentOf(parent, children, policy)</c>: scripts <paramref name="parent"/> to spawn <paramref name="children"/> in one
        /// commit and wait on them with <paramref name="policy"/>; it records their outcomes.
        /// </summary>
        public Found ParentOf(string parent, IReadOnlyList<string> children, JoinPolicy policy)
        {
            var found = new Found();
            Script(parent, new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    foreach (var name in children) found.Add(await Spawn(tx, runtime.TaskId, name));
                    return WaitOn(found.Ids, policy);
                }, ctx),
                Resume = async (runtime, ctx, _) =>
                {
                    found.Outcomes = [.. (await runtime.OutcomesAsync(found.Ids, ctx)).Select(outcome => Wire(outcome.Status))];
                    await runtime.CommitAsync((_, _) => End(Ending.Completed, parent), ctx);
                },
            });
            return found;
        }

        /// <summary>Upstream <c>spawnAndFinish(name, child)</c>: scripts <paramref name="name"/> to spawn <paramref name="child"/> and
        /// then complete, holding while the child lives.</summary>
        public Slot SpawnAndFinish(string name, string child)
        {
            var found = new Slot();
            Script(name, new Behavior
            {
                Run = (runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    found.Id = await Spawn(tx, runtime.TaskId, child);
                    return ResumeAt(1);
                }, ctx),
            });
            return found;
        }

        /// <summary>Upstream <c>defaultRun</c>: wait for the gate or the abort signal, then end as the gate says.</summary>
        private async Task DefaultRun(ITaskRuntime runtime, DurableContext ctx, string name)
        {
            var gate = Gate(name).Task;
            var aborted = Aborted(runtime.Signal);
            // Promise.race: an abort rejects with OperationCanceledException (upstream's AbortError).
            if (await Task.WhenAny(gate, aborted) == aborted) await aborted;
            var ending = await gate;
            if (ending == Ending.Throw) throw new InvalidOperationException($"{name} threw");
            await runtime.CommitAsync((_, _) => End(ending, name), ctx);
        }

        private static string NameOf(JsonValue input) => S(((JsonObjectValue)input)["name"]);
    }

    /// <summary>Children a scripted parent spawned and the outcomes it read.</summary>
    internal sealed class Found
    {
        private readonly Lock gate = new();
        private List<TaskId> ids = [];
        private List<string> outcomes = [];

        public List<TaskId> Ids
        {
            get { lock (gate) return [.. ids]; }
        }

        public List<string> Outcomes
        {
            get { lock (gate) return [.. outcomes]; }
            set { lock (gate) outcomes = value; }
        }

        public void Add(TaskId id)
        {
            lock (gate) ids.Add(id);
        }

        /// <summary>Upstream <c>found.ids.splice(0, found.ids.length, ...on)</c>.</summary>
        public void Replace(IEnumerable<TaskId> on)
        {
            lock (gate) ids = [.. on];
        }
    }

    /// <summary>A task ID a handler assigns (upstream's <c>let child: TaskId | undefined</c> and <c>{ id?: TaskId }</c>).</summary>
    internal sealed class Slot
    {
        private long id = -1;

        public TaskId? Id
        {
            get => Interlocked.Read(ref id) is var value and >= 0 ? new TaskId(value) : null;
            set => Interlocked.Exchange(ref id, value?.Value ?? -1);
        }
    }

    /// <summary>A conversation ID a handler assigns.</summary>
    internal sealed class ConversationSlot
    {
        private long id = -1;

        public ConversationId? Id
        {
            get => Interlocked.Read(ref id) is var value and >= 0 ? new ConversationId(value) : null;
            set => Interlocked.Exchange(ref id, value?.Value ?? -1);
        }
    }

    // ─── Recovery ───────────────────────────────────────────────────────────

    /// <summary>
    /// A storage location that can be opened again after its Harness closed (upstream's SQLite file for the reopen cases); each call of
    /// the backend's factory makes a fresh location.
    /// </summary>
    internal delegate Task<IStorage> OpenLocation();

    /// <summary>Upstream <c>Seeded</c>.</summary>
    internal sealed record Seeded(TaskId Parent, List<TaskId> Children);

    /// <summary>Replaces a task's record through the internal task write (upstream <c>set(id, patch)</c>).</summary>
    internal delegate Task SetTask(TaskId id, Func<TaskRecord, TaskRecord> patch);

    /// <summary>
    /// Writes records a crash could leave, without a Harness: <c>parent</c> with children named <paramref name="children"/>, then
    /// <paramref name="edit"/> replaces states through the internal task write (upstream <c>seed</c>).
    /// </summary>
    internal static async Task<Seeded> Seed(Nodes nodes, OpenLocation location, IReadOnlyList<string> children, Func<SetTask, Seeded, Task> edit)
    {
        var session = new PiSharp.Durable.Session(await location());
        var seeded = await session.CommitAsync(async tx =>
        {
            var root = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
            var parent = await tx.CreateTaskAsync(nodes.Node, NodeInput("parent"), In(root.Id));
            var ids = new List<TaskId>();
            foreach (var name in children) ids.Add(await nodes.Spawn(tx, parent, name));
            return new Seeded(parent, ids);
        }, Context);
        await edit(async (id, patch) => await session.CommitWithAsync(async tx =>
        {
            var record = (await tx.GetTaskAsync(id))!;
            tx.SetTask(patch(record));
        }, Context), seeded);
        await session.CloseAsync(Context);
        return seeded;
    }

    /// <summary>A record with <paramref name="state"/> (upstream's <c>{ ...record, state }</c>).</summary>
    internal static Func<TaskRecord, TaskRecord> WithState(TaskState state) => record => record with { State = state };

    /// <summary>Upstream <c>COMPLETED</c>.</summary>
    internal static TaskState CompletedDone => NextTaskState.Completed("done");

    /// <summary>Upstream <c>FAILED</c>.</summary>
    internal static TaskState FailedDeclined => NextTaskState.Failed("declined");

    /// <summary>A seeded waiting state on <paramref name="on"/>, resuming in round 1.</summary>
    internal static TaskState SeededWait(IEnumerable<TaskId> on, JoinPolicy policy) => WaitOn(on, policy);
}

/// <summary>
/// Rejects the first commit that finalizes the task <see cref="Reject"/> names with <see cref="StorageRejectedException"/>, then clears it
/// (upstream's <c>class Rejecting extends MemoryStorage</c>, as a decorator).
/// </summary>
internal sealed class RejectingStorage(IStorage inner) : IStorage
{
    private long reject = -1;

    /// <summary>The task whose next finalizing commit is rejected once.</summary>
    public TaskId? Reject
    {
        get => Interlocked.Read(ref reject) is var value and >= 0 ? new TaskId(value) : null;
        set => Interlocked.Exchange(ref reject, value?.Value ?? -1);
    }

    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        var target = Reject;
        var finalizes = target is { } id && writes.Any(write => write is TaskWrite task && task.Value.Id == id && task.Value.State.Status == DurableTaskStatus.Terminal);
        if (finalizes && Interlocked.CompareExchange(ref reject, -1, target!.Value.Value) == target.Value.Value)
            return Task.FromException<Seq>(new StorageRejectedException("rejected once"));
        return inner.CommitAsync(writes, context);
    }

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        inner.FindDocumentAsync(address, at, context);
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanConversationsAsync(query, limit, cursor, context);
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        inner.GetEntryAsync(conversationId, id, context);
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanEntriesAsync(query, limit, cursor, context);
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanDocumentsAsync(query, limit, cursor, context);
    public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
}
