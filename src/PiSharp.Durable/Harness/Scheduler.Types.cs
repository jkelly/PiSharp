using System.Collections;
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>Terminal outcomes the scheduler writes without running task code: faulted or orphaned (upstream <c>SchedulerOutcome</c>).</summary>
internal delegate Task SettleOutcome(Transaction tx, TaskRecord record, TaskOutcome outcome);

/// <summary>An invocation a conversation handle is bound to: its signal, and a check that throws once it ended (upstream
/// <c>InvocationBinding</c>).</summary>
internal sealed record InvocationBinding(CancellationSignal Signal, Action Check);

/// <summary>Sizes of the scheduler's ownership indexes, for tests that bound its memory (upstream <c>TaskScheduler.indexSizes</c>, read
/// through the Harness's <c>SCHEDULER_INDEX_SIZES</c>). <see cref="Below"/> counts the listed child nodes; <see cref="Pins"/> the nodes chain
/// loads keep for commits not published yet.</summary>
internal readonly record struct SchedulerIndexSizes(int Live, int Settled, int Edges, int Below, int Roots, int Unloaded, int DropQueue,
    int Pins);

/// <summary>The kept contexts' idle state, for tests (upstream <c>TaskScheduler.idleState</c>, read through the Harness's
/// <c>SCHEDULER_IDLE_STATE</c>): how many are kept and how many idle, how many disagree with a full recompute, and how many idle checks
/// covered every scope.</summary>
internal readonly record struct SchedulerIdleState(int Contexts, int IdleContexts, int IdleMismatches, int FullIdleChecks);

/// <summary>Options of the task scheduler (upstream <c>TaskSchedulerOptions</c>).</summary>
/// <summary>What <see cref="DurableTaskScheduler.BlockedReasonOf"/> reads of a task: the task graph keeps only this of each record
/// (upstream <c>BlockedSubject</c>).</summary>
internal readonly record struct BlockedSubject(TaskId Id, string Kind, int Version, bool AbortRequested, TaskAbortReason? AbortReason,
    DurableTaskStatus Status)
{
    /// <summary>The part of <paramref name="record"/> <see cref="DurableTaskScheduler.BlockedReasonOf"/> reads (upstream
    /// <c>blockedSubject</c>).</summary>
    public static BlockedSubject Of(TaskRecord record) =>
        new(record.Id, record.Kind, record.Version, record.AbortRequested, record.AbortReason, record.State.Status);
}

internal sealed record DurableTaskSchedulerOptions
{
    public required Session Session { get; init; }
    public required IStorage Storage { get; init; }
    public required IRegistryReader Registry { get; init; }
    /// <summary>The Harness's models, read at each access (upstream <c>currentModels</c>).</summary>
    public required Func<IDurableModels> Models { get; init; }
    /// <summary>Resolves a conversation's agent against a snapshot; the runtime calls it at most once per phase.</summary>
    public required Func<ConversationId, IRegistrySnapshot, DurableContext, Task<Agent>> Agent { get; init; }
    /// <summary>Resolves the settings; read at each access.</summary>
    public required Func<Settings> Settings { get; init; }
    /// <summary>Builds a conversation's environment with <c>HarnessOptions.Env</c>.</summary>
    public required Func<ConversationId, DurableContext, Task<IExecutionEnv?>> Env { get; init; }
    public required Func<double> Now { get; init; }
    /// <summary><see cref="HarnessOptions.OnWake"/>: replaces the timer for sleeping tasks.</summary>
    public Action<Wake>? OnWake { get; init; }
    /// <summary>The host's timers (<see cref="HarnessOptions.SetTimer"/>).</summary>
    public required HostTimers Timers { get; init; }
    /// <summary>The Harness's report: never throws.</summary>
    public required ReportSink Report { get; init; }
    /// <summary><see cref="HarnessOptions.Telemetry"/>: phase spans, phase durations and active tasks. None records nothing.</summary>
    public ITelemetryContext? Telemetry { get; init; }
    /// <summary>Harness cleanup staged in the commit that makes an outcome the scheduler wrote itself terminal.</summary>
    public required SettleOutcome SettleOutcome { get; init; }
    /// <summary>Withdraws a conversation's queued inputs, for conversation abort and abort cascades.</summary>
    public required Func<Transaction, ConversationId, Task> WithdrawInputs { get; init; }
    /// <summary>Invocation-bound handle of an existing conversation, for task runtimes and tools.</summary>
    public required Func<ConversationId, InvocationBinding, DurableContext, Task<IConversationHandle?>> Conversation { get; init; }
    /// <summary>Context for scheduler commits and invocations; carries no caller cancellation.</summary>
    public required DurableContext Context { get; init; }
    /// <summary><see cref="HarnessOptions.ReadOnly"/>: open loads live tasks as committed, <c>running</c> ones included, and the scheduler
    /// never commits or runs anything.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Task kinds whose run phases end the task themselves once it was cut off <c>settings.RestartLimit</c> times (the built-in
    /// tasks, from <see cref="TaskRecord.Restarts"/>); the scheduler ends a task of any other kind <c>faulted</c> instead. Null: none.</summary>
    public Func<string, bool>? LimitsRestarts { get; init; }
    /// <summary>Whether the <c>abandonOnRestart</c> children of an owner are kept after a restart (spec suspendable §2.5): its record says
    /// it was not cut off while running, as a tool call parked or not started yet. Wins over the child's flag. Null: none.</summary>
    public Func<TaskRecord, bool>? KeepsAbandoned { get; init; }
    /// <summary>Whether a sleeping task is parked at gates its phase asks again on every wake (spec suspendable §5.2), as a tool call
    /// before its intent: a registry publication, open, and a sleep committed under an outdated snapshot wake it. Null: none.</summary>
    public Func<TaskRecord, bool>? Gated { get; init; }
}

/// <summary>What the Harness reads of the runtime of a task its scheduler runs (upstream's <c>closeSignals</c> and
/// <c>providerSessionsByRuntime</c> WeakMaps, keyed by the runtime).</summary>
internal interface ISchedulerRuntime
{
    /// <summary>Aborted once the Harness running the task closes, or its Session fails, unlike the runtime's own signal, which an abort
    /// mark aborts too: for cleanup a run does after its own abort mark, such as a tool's <c>Cancel</c> (upstream <c>closeSignal</c>).</summary>
    CancellationSignal CloseSignal { get; }
    /// <summary>Records a provider session ID the task used, which the Harness cleans up at close.</summary>
    void UseProviderSession(string sessionId);
}

internal sealed partial class DurableTaskScheduler
{
    // ─── Types ───────────────────────────────────────────────────────────────

    private enum InvocationMode { Run, Abort }

    /// <summary>One in-memory execution of a task in run or abort mode.</summary>
    private sealed class Invocation(TaskId taskId, string kind, ConversationId conversationId, string? extension, InvocationMode mode,
        CancellationSignal controller, DurableContext context)
    {
        private static long started;
        /// <summary>When it started, among all invocations: the join writes back in this order, as upstream's insertion-ordered
        /// <c>#invocations</c> Map lists them (a Dictionary reuses the slots of removed entries).</summary>
        public long Started { get; } = Interlocked.Increment(ref started);
        public TaskId TaskId { get; } = taskId;
        public string Kind { get; } = kind;
        public ConversationId ConversationId { get; } = conversationId;
        /// <summary>The extension that installed the definition this invocation runs, at reservation; none for a built-in task.</summary>
        public string? Extension { get; } = extension;
        public InvocationMode Mode { get; } = mode;
        public CancellationSignal Controller { get; } = controller;
        private volatile DurableContext context = context;
        /// <summary>Context passed to handlers; cancelled by <see cref="Controller"/>. While a phase runs, it carries the phase span.</summary>
        public DurableContext Context { get => context; set => context = value; }
        /// <summary>Creation links, for the spans of a never-started task's first invocation; cleared when the invocation finishes.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, string>>? Links { get; set; }
        /// <summary>The task's record as last committed while the invocation was registered, so the terminal state stays available after it
        /// leaves the live index (upstream <c>invocation.record</c>). Guarded by the scheduler lock; released when the invocation finishes.</summary>
        public TaskRecord? Record { get; set; }
        /// <summary>The fault the invocation's step decided, whose terminal record is published after the invocation ended.</summary>
        public FaultedTaskOutcome? Fault { get; set; }
        /// <summary>Watches acquired through the runtime; stopped at invocation end. Guarded by the scheduler lock.</summary>
        public HashSet<IWatchHandle<JsonObjectValue>> Watches { get; } = [];
        /// <summary>Timers armed through the runtime; disarmed at invocation end. Guarded by the scheduler lock.</summary>
        public HashSet<TimerSlot> Timers { get; } = [];
        private volatile bool ended;
        public bool Ended { get => ended; set => ended = value; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>What a runtime reads for the phase handler it serves: the phase's snapshot and task, and its lazily resolved agent.</summary>
    private sealed class Phase(Func<IRegistrySnapshot> snapshot, Func<DurableTask> task)
    {
        private readonly Lock gate = new();
        private Task<Agent>? agent;
        public Func<IRegistrySnapshot> Snapshot { get; } = snapshot;
        public Func<DurableTask> Task { get; } = task;

        public void ResetAgent()
        {
            lock (gate) agent = null;
        }

        public Task<Agent> Agent(Func<Task<Agent>> resolve)
        {
            lock (gate)
            {
                if (agent is not null) return agent;
                agent = resolve();
                // A caller that stops waiting must not leave the shared resolution's failure unobserved.
                agent.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
                return agent;
            }
        }
    }

    private sealed class RunState(DurableTask task, IRegistrySnapshot snapshot)
    {
        public DurableTask Task { get; set; } = task;
        public IRegistrySnapshot Snapshot { get; set; } = snapshot;
        /// <summary>Registry publications seen when <see cref="Snapshot"/> was taken.</summary>
        public long Publication { get; set; }
        public ReportedTask? Reported { get; set; }
    }

    /// <summary>Replacement definition already reported as unable to take over, per invocation.</summary>
    private sealed record ReportedTask(DurableTask? Task);

    /// <param name="Publication">Registry publications seen when <paramref name="Snapshot"/> was taken.</param>
    private sealed record Reservation(Invocation Invocation, DurableTask Task, IRegistrySnapshot Snapshot, long Publication)
    {
        /// <summary>An abort invocation's committed record, read when its pass's invocations start (upstream's <c>#runAbort</c> reads it
        /// first thing).</summary>
        public TaskRecord? Record { get; set; }
    }

    /// <summary>Outcome of the phase that just returned, judged by the next step.</summary>
    private sealed record PhaseResult(JsonValue Checkpoint, Exception? Failure);

    private enum StepPlanKind { Continue, Progress, End, Cascade, Fault }

    /// <summary>Step decision, made in a decision region (upstream <c>StepPlan</c>): continue with the next phase, decide progress (rule 5)
    /// once the decision stands, end the invocation, end it and schedule the cascade an owner's intent implies, or end it by writing
    /// faulted.</summary>
    private readonly record struct StepPlan(StepPlanKind Kind, Exception? Error)
    {
        /// <summary>Continue with the next phase, the first one.</summary>
        public static StepPlan Continue => new(StepPlanKind.Continue, null);
        /// <summary>Rule 5: the phase made durable progress; <see cref="ProgressOf"/> decides once the decision stands.</summary>
        public static StepPlan Progress => new(StepPlanKind.Progress, null);
        /// <summary>End the invocation without a write.</summary>
        public static StepPlan End => new(StepPlanKind.End, null);
        /// <summary>End it: an owner's cancellation intent reaches it before its cascade did; schedule the cascade.</summary>
        public static StepPlan Cascade => new(StepPlanKind.Cascade, null);
        public static StepPlan Fault(Exception error) => new(StepPlanKind.Fault, error);
    }

    /// <summary><see cref="PlanMarksLocked"/>'s result: the marks and the <c>failFast</c> checks it read, or the members it needs from
    /// Storage first.</summary>
    private sealed record MarksPlan(OrderedDictionary<TaskId, (TaskRecord Record, bool Restart)>? Marks, List<TaskId>? Checked, List<TaskId>? Missing);

    /// <summary><see cref="PlanRoundLocked"/>'s result: the tasks it finalizes with their terminal records, the holders to check next, and the
    /// tasks it checked.</summary>
    private sealed record RoundPlan(List<(TaskRecord Record, TaskRecord Terminal)> Finalized, List<TaskId> Next, List<TaskId> Checked);

    /// <summary><see cref="PlanReservationLocked"/>'s result.</summary>
    private sealed record ReservationPlan(List<TaskId> Dropped, bool Cascade, List<(TaskRecord Record, Woke? Woke)> Ready);

    /// <summary>How a reservation in run mode ends a wait or a sleep: its wake, and the signal memos it marks consumed or expired (upstream
    /// <c>Woke</c>).</summary>
    private readonly record struct Woke(WakeReason Wake, ImmutableArray<KeyValuePair<string, JsonValue>> Memos);

    /// <summary>A definition that can take a record, or why none can; deciding it runs no task code.</summary>
    private readonly record struct FitResult(DurableTask? Task, bool Migrates, BlockedReason? Reason, Exception? Error);

    private readonly record struct Resolution(DurableTask? Task, TaskRecord? Record, BlockedReason? Blocked);

    /// <summary>The immutable ownership fields of a task.</summary>
    private readonly record struct TaskNode(ConversationId ConversationId, TaskId? Owner, bool Background);

    /// <summary>A node of the downward index: a task, or a conversation (upstream <c>TaskId | `c${number}`</c>).</summary>
    private readonly record struct Node(long Value, bool IsConversation)
    {
        public static Node Of(TaskId task) => new(task.Value, false);
        public static Node Of(ConversationId conversation) => new(conversation.Value, true);
        public TaskId Task => new(Value);
        public ConversationId Conversation => new(Value);
    }

    /// <summary>Where a walk up the ownership tree continues: an owner task, or a conversation.</summary>
    private readonly record struct Up(bool IsTask, TaskId Task, ConversationId Conversation)
    {
        public static Up Of(TaskId task) => new(true, task, default);
        public static Up Of(ConversationId conversation) => new(false, default, conversation);
    }

    private enum StepKind { Task, Conversation, Unknown }

    /// <summary>One step of a walk up: an owner task with its node, a conversation, or an owner edge that is not loaded yet.</summary>
    private readonly record struct Step(StepKind Kind, TaskId Task, TaskNode Node, ConversationId Conversation)
    {
        public static Step Unknown => new(StepKind.Unknown, default, default, default);
        public static Step OfTask(TaskId task, TaskNode node) => new(StepKind.Task, task, node, default);
        public static Step OfConversation(ConversationId conversation) => new(StepKind.Conversation, default, default, conversation);
    }

    /// <summary>Candidate records a commit staged; they override committed records in ownership walks.</summary>
    private sealed class Overlay(Dictionary<TaskId, TaskRecord> tasks, Dictionary<ConversationId, TaskId?> edges)
    {
        public Dictionary<TaskId, TaskRecord> Tasks { get; } = tasks;
        public Dictionary<ConversationId, TaskId?> Edges { get; } = edges;
        /// <summary>Links to the staged tasks <c>below</c> does not list, by parent node, for walks down; built at first use.</summary>
        public Dictionary<Node, OrderedSet<Node>>? Links { get; set; }
    }

    /// <summary>Where ordinary ownership traversal starts: one conversation, or every ownerless conversation.</summary>
    private readonly record struct Scope(ConversationId? Conversation)
    {
        public static Scope Roots => new(null);
        public static Scope Of(ConversationId conversation) => new(conversation);
    }

    private sealed class KeptContext(ContextRange range, double? idleSince)
    {
        /// <summary>Replaced when a publication advances it (AdvanceContextsLocked). Guarded by the scheduler's lock.</summary>
        public ContextRange Range { get; set; } = range;
        public double? IdleSince { get; set; } = idleSince;
        /// <summary>When an idle range expires, fixed when it went idle (upstream <c>expiresAt</c>), so a publication that advances it reads
        /// no settings.</summary>
        public double? ExpiresAt { get; set; }
    }

    /// <summary>A scheduler timer: when it is due, and its disarm once armed.</summary>
    private sealed class ArmedTimer(double at)
    {
        public double At { get; } = at;
        public Action? Disarm { get; set; }
    }

    /// <summary>A timer armed through a task runtime. Guarded by the scheduler lock: <see cref="Done"/> once disarmed, fired, or ended with
    /// its invocation.</summary>
    private sealed class TimerSlot
    {
        public Action? Disarm { get; set; }
        public bool Done { get; set; }
    }

    /// <summary>An insertion-ordered map, as a JavaScript Map iterates: setting a key that is present keeps its place; removal is O(1)
    /// (upstream's <c>#idleOrder</c> Map). Its iterations are not counted, as upstream's tests count only Set iterations.</summary>
    private sealed class OrderedMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
    {
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> index = [];
        private readonly LinkedList<KeyValuePair<TKey, TValue>> order = new();

        public int Count => index.Count;

        /// <summary>The first entry in order, or null when empty.</summary>
        public KeyValuePair<TKey, TValue>? First => order.First?.Value;

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (index.TryGetValue(key, out var node))
            {
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            if (index.TryGetValue(key, out var node)) node.Value = KeyValuePair.Create(key, value);
            else index[key] = order.AddLast(KeyValuePair.Create(key, value));
        }

        public bool Remove(TKey key)
        {
            if (!index.Remove(key, out var node)) return false;
            order.Remove(node);
            return true;
        }

        public void Clear()
        {
            index.Clear();
            order.Clear();
        }

        public LinkedList<KeyValuePair<TKey, TValue>>.Enumerator GetEnumerator() => order.GetEnumerator();

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Counts the members the scheduler's sets yield; they are iterated under the scheduler lock.</summary>
    private sealed class IterationCounter
    {
        public long Entries;
    }

    /// <summary>An insertion-ordered set, as a JavaScript Set iterates: re-adding a member keeps its place; removal is O(1). Every member an
    /// iteration yields is counted.</summary>
    private sealed class OrderedSet<T>(IterationCounter counter) : IEnumerable<T> where T : notnull
    {
        private readonly Dictionary<T, LinkedListNode<T>> index = [];
        private readonly LinkedList<T> order = new();

        public int Count => index.Count;

        public bool Contains(T value) => index.ContainsKey(value);

        public bool Add(T value)
        {
            if (index.ContainsKey(value)) return false;
            index[value] = order.AddLast(value);
            return true;
        }

        public bool Remove(T value)
        {
            if (!index.Remove(value, out var node)) return false;
            order.Remove(node);
            return true;
        }

        public void Clear()
        {
            index.Clear();
            order.Clear();
        }

        public Enumerator GetEnumerator() => new(order.GetEnumerator(), counter);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            private LinkedList<T>.Enumerator inner;
            private readonly IterationCounter counter;

            internal Enumerator(LinkedList<T>.Enumerator inner, IterationCounter counter)
            {
                this.inner = inner;
                this.counter = counter;
            }

            public readonly T Current => inner.Current;

            readonly object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (!inner.MoveNext()) return false;
                Interlocked.Increment(ref counter.Entries);
                return true;
            }

            void IEnumerator.Reset() => ((IEnumerator)inner).Reset();

            public void Dispose() => inner.Dispose();
        }
    }

}
