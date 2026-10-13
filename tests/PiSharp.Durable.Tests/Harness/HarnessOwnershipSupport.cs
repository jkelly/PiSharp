// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): the support half of packages/durable/test/harness-ownership.test.ts: the held test.hold
// task, test.waiter, their gates and run counts, and the helpers its two describes share. The cases are in HarnessOwnershipCases.cs.
//
// Adaptations (beyond those of HarnessSupport.cs and ChatSupport.cs):
// - Upstream keeps gates and run counts at module scope and clears them after each test. Here each case builds a fresh Holds fixture,
//   whose test.hold definition closes over that case's gates and counts, so cases share nothing.
// - waitUntil (a 5 ms poll for at most 2.5 s) is Eventually (a bounded poll of about 20 s): only how long a slow machine may take differs.
// - Upstream's `class Rejecting extends MemoryStorage` and its patched `storage.commit` are MarkRejectingStorage, a decorator over any
//   IStorage, so the cases run on every backend.
// - test.hold's slow abort handler also ends when its signal aborts, which only close does (see there).
// - The internal `tx.setTask` of a Harness commit is DurableHarness.CommitWithAsync's Transaction.SetTask in the conversation's scope.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static HarnessTests;
using static Program;
using static SessionTests;

/// <summary>Support of the harness.ownership.* cases (upstream harness-ownership.test.ts).</summary>
internal static partial class OwnershipTests
{
    internal static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Outcome a held task commits once its gate opens (upstream <c>Ending</c>).</summary>
    internal enum Ending
    {
        Completed,
        Failed,
    }

    /// <summary>Upstream <c>Tree</c>: an owner task, the conversation it owns, and the task held in that conversation.</summary>
    internal sealed record Tree(TaskId Owner, ConversationId Child, TaskId Inner);

    /// <summary>A Harness running test.hold and test.waiter, its root, registry and reports.</summary>
    internal sealed record Opened(IHarness Harness, IConversation Root, IRegistry Registry, SyncList<Exception> Reports);

    /// <summary>Upstream <c>OWN_CONVERSATION</c>-style options: conversation-owned, optionally background or in another conversation.</summary>
    internal static TaskOptions OwnConversation(ConversationId? conversation = null, bool background = false) =>
        new(TaskOwnership.Conversation) { ConversationId = conversation, Background = background };

    /// <summary>One case's test.hold and test.waiter definitions with their gates and run counts.</summary>
    internal sealed class Holds
    {
        private readonly ConcurrentDictionary<string, Deferred<Ending>> gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> runs = new(StringComparer.Ordinal);

        public Holds()
        {
            // A task that holds until its named gate opens or it is aborted. With slowAbort, its abort handler first waits for the gate
            // abort.<name> (upstream Hold).
            Hold = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.hold",
                Version = 1,
                Initial = _ => O("""{"phase":"hold"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["hold"] = async (task, runtime, ctx) =>
                    {
                        var name = NameOf(task.Input);
                        runs.AddOrUpdate(name, 1, (_, count) => count + 1);
                        var gate = Gate(name).Task;
                        var aborted = Aborted(runtime.Signal);
                        // Promise.race: an abort rejects with OperationCanceledException (upstream's AbortError).
                        if (await Task.WhenAny(gate, aborted) == aborted) await aborted;
                        var ending = await gate;
                        await runtime.CommitAsync((_, _) => ending == Ending.Completed
                            ? NextTaskState.Completed(JsonValue.Null)
                            : NextTaskState.Failed("gate failed"), ctx);
                    },
                },
                Abort = async (task, runtime, ctx) =>
                {
                    if (((JsonObjectValue)task.Input).TryGetValue("slowAbort", out var slow) && slow is JsonBooleanValue { Value: true })
                    {
                        // Upstream awaits only the gate. Only close aborts an abort invocation's signal, and upstream's close never meets a
                        // started slow handler: its abort reservation runs after close seals. Here the reservation can win that race, and a
                        // handler that ignored its signal would hold close forever; ending with the signal leaves the same durable state.
                        var gate = Gate($"abort.{NameOf(task.Input)}").Task;
                        var aborted = Aborted(runtime.Signal);
                        if (await Task.WhenAny(gate, aborted) == aborted) await aborted;
                    }
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            // Waits on the tasks in its input, then completes; its abort handler ends it aborted (upstream Waiter).
            Waiter = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.waiter",
                Version = 1,
                Initial = _ => O("""{"phase":"wait"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Waiting(O("""{"phase":"done"}"""),
                        ((JsonArrayValue)((JsonObjectValue)task.Input)["on"]).Select(id => new TaskId((long)N(id))), JoinPolicy.AllSettled), ctx),
                    ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx),
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
        }

        /// <summary>Upstream <c>Hold</c>.</summary>
        public DurableTask Hold { get; }

        /// <summary>Upstream <c>Waiter</c>.</summary>
        public DurableTask Waiter { get; }

        /// <summary>Upstream <c>gate(name)</c>.</summary>
        public Deferred<Ending> Gate(string name) => gates.GetOrAdd(name, _ => new Deferred<Ending>());

        /// <summary>Upstream <c>open(name, ending)</c>.</summary>
        public void Open(string name, Ending ending = Ending.Completed) => Gate(name).Resolve(ending);

        /// <summary>Upstream <c>runs.get(name)</c>: how often the named task started its run phase.</summary>
        public int Runs(string name) => runs.GetValueOrDefault(name);

        /// <summary>The input of a held task named <paramref name="name"/>.</summary>
        public static JsonObjectValue Input(string name, bool slowAbort = false) =>
            slowAbort ? Obj(("name", name), ("slowAbort", true)) : Obj(("name", name));

        /// <summary>The input of a waiter on <paramref name="on"/>.</summary>
        public static JsonObjectValue WaiterInput(params TaskId[] on) => Obj(("on", JsonValue.Array(on.Select(id => (JsonValue)id.ToJson()))));

        /// <summary>
        /// Upstream <c>ownedChild(parent, name, options)</c>: in <paramref name="parent"/>, stages task <c>owner</c> and a conversation it owns
        /// holding task <c>inner</c>, in one commit.
        /// </summary>
        public Task<Tree> OwnedChild(IConversation parent, string name, bool background = false, bool slowInner = false, bool slowOwner = false) =>
            parent.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(Hold, Input(name, slowOwner), OwnConversation(background: background));
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(owner));
                var inner = await tx.CreateTaskAsync(Hold, Input($"{name}.inner", slowInner), OwnConversation(child.Id));
                return new Tree(owner, child.Id, inner);
            }, Context);

        /// <summary>Upstream <c>openHarness(storage)</c>: a resumed Harness running test.hold and test.waiter, and its root.</summary>
        public async Task<Opened> OpenHarness(IStorage storage)
        {
            var opened = await OpenTasks(storage, [Hold, Waiter]);
            var root = await opened.Harness.RootAsync(Context);
            opened.Harness.Resume();
            return new Opened(opened.Harness, root, opened.Registry, opened.Reports);
        }

        private static string NameOf(JsonValue input) => S(((JsonObjectValue)input)["name"]);
    }

    /// <summary>
    /// Upstream <c>gated(message)</c>: a faux response held until <c>Release</c> or cancellation; <c>Reached</c> completes when the request is
    /// sent.
    /// </summary>
    internal static (FauxStep Step, Task Reached, Action Release) Gated(JsonObjectValue message)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FauxStep step = new FauxResponseFactory(async request =>
        {
            reached.TrySetResult();
            var aborted = Aborted(request.Signal);
            if (await Task.WhenAny(gate.Task, aborted) == aborted) await aborted;
            return message;
        });
        return (step, reached.Task, () => gate.TrySetResult());
    }

    /// <summary>Upstream <c>busy(conversation, task)</c>: marks a conversation busy with <paramref name="task"/> standing in for its run, so
    /// submissions queue.</summary>
    internal static Task Busy(IConversation conversation, TaskId task) => conversation.CommitAsync(async tx =>
        (await tx.DocAsync(Live.LiveDoc, conversation.Id)).Set("run", Obj(("taskId", task.ToJson()), ("inputs", JsonArrayValue.Empty))), Context);

    /// <summary>Upstream <c>status(harness, id)</c>: the task's state.</summary>
    internal static async Task<TaskState> StateOf(IHarness harness, TaskId id) => (await harness.GetTaskAsync(id, Context))!.State;

    /// <summary>The status of the task's state.</summary>
    internal static async Task<DurableTaskStatus> StatusOf(IHarness harness, TaskId id) => (await StateOf(harness, id)).Status;

    /// <summary><c>(await harness.waitForTask(id)).state.outcome.status</c>.</summary>
    internal static async Task<string> OutcomeOf(IHarness harness, TaskId id) =>
        StructuredTests.Wire(((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status);

    /// <summary>The outcome status of a settling state, such as a completing owner holding its outcome.</summary>
    internal static string HeldOutcome(TaskState state) => StructuredTests.Wire(((SettlingTaskState)state).Outcome.Status);

    /// <summary>The committed record of a submission.</summary>
    internal static async Task<SubmissionRecord> RecordOf(IHarness harness, SubmissionId id) =>
        await (await harness.SubmissionAsync(id, Context))!.StatusAsync(Context);

    /// <summary><c>toMatchObject({status: "unanswered", reason: "aborted"})</c>.</summary>
    internal static void UnansweredAborted(SubmissionRecord record, string what) =>
        Check(record.Status == SubmissionStatus.Unanswered && record.Reason == "aborted",
            $"{what}: expected unanswered aborted, actual {record.ToJson().ToJson()}");

    /// <summary>Replaces a task record through the internal task write in <paramref name="conversation"/>'s scope (upstream's
    /// <c>(tx as ...).setTask(...)</c> in <c>conversation.commit</c>).</summary>
    internal static Task SetTasks(IHarness harness, IConversation conversation, Func<Transaction, Task> change) =>
        ((DurableHarness)harness).CommitWithAsync(change, Context, new TransactionScope(conversation.Id));
}

/// <summary>
/// Rejects, once, the first commit that writes the abort mark of the task <see cref="Reject"/> names with
/// <see cref="StorageRejectedException"/>, then clears it (upstream's <c>class Rejecting extends MemoryStorage</c> and patched
/// <c>storage.commit</c> of harness-ownership, as a decorator).
/// </summary>
internal sealed class MarkRejectingStorage(IStorage inner) : IStorage
{
    private long reject = -1;

    /// <summary>The task whose next marking commit is rejected once; null once rejected.</summary>
    public TaskId? Reject
    {
        get => Interlocked.Read(ref reject) is var value and >= 0 ? new TaskId(value) : null;
        set => Interlocked.Exchange(ref reject, value?.Value ?? -1);
    }

    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        var target = Reject;
        var marks = target is { } id && writes.Any(write => write is TaskWrite task && task.Value.Id == id && task.Value.AbortRequested);
        if (marks && Interlocked.CompareExchange(ref reject, -1, target!.Value.Value) == target.Value.Value)
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
