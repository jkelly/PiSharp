// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): the shared parts of packages/durable/test/harness-scheduler-indexes.test.ts,
// harness-scheduler-retention.test.ts and harness-cancellation-barrier.test.ts: gated test tasks, a SQLite path per case, storages whose reads
// and commits a case observes, and the scheduler's index sizes.
//
// Adaptations (beyond those of HarnessSupport.cs):
// - Upstream keeps gates, run counts and probe results at module scope and clears them after each test; here each case builds a fresh
//   SchedulerGates, whose task definitions close over it.
// - Upstream patches storage methods on the instance (storage.commit = ..., storage.conversation = ...); here HookedStorage, a
//   DelegatingStorage, calls the hooks a case sets.
// - Upstream reads the index sizes through the Harness's SCHEDULER_INDEX_SIZES getter right after an await; their passes run in microtasks
//   it has already drained. Here reconciliation, reservation and the sweep job run on the thread pool, so Sizes first waits until none is
//   scheduled or running and the line is flushed (DurableTaskScheduler.QuiesceAsync), then reads DurableHarness.SchedulerIndexSizes.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

/// <summary>Support of the harness.scheduler-indexes.*, harness.scheduler-retention.* and harness.cancellation-barrier.* cases.</summary>
internal static partial class SchedulerTests
{
    internal static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Conversation-owned task options, optionally in another conversation or background.</summary>
    internal static TaskOptions OwnConversation(ConversationId? conversation = null, bool background = false, bool? abandonOnRestart = null) =>
        new(TaskOwnership.Conversation) { ConversationId = conversation, Background = background ? true : null, AbandonOnRestart = abandonOnRestart };

    /// <summary>Task-owned task options.</summary>
    internal static TaskOptions OwnedBy(TaskId owner, bool? abandonOnRestart = null) =>
        new(TaskOwnership.Task(owner)) { AbandonOnRestart = abandonOnRestart };

    internal static JsonObjectValue Named(string name) => Obj(("name", name));

    internal static string NameOf(JsonValue input) => S(((JsonObjectValue)input)["name"]);

    /// <summary>A fresh SQLite file path in its own temporary directory (upstream <c>sqlitePath()</c>).</summary>
    internal static string SqlitePath(string label) => Path.Combine(Temp(label), "session.sqlite");

    internal static async Task<HookedStorage> OpenSqlite(string path) => new(await SqliteStorage.OpenAsync(path));

    /// <summary>The scheduler's index sizes once its pending passes have run (upstream <c>harness[SCHEDULER_INDEX_SIZES]</c>).</summary>
    internal static async Task<SchedulerIndexSizes> Sizes(IHarness harness)
    {
        var durable = (DurableHarness)harness;
        await durable.Scheduler.QuiesceAsync();
        return durable.SchedulerIndexSizes;
    }

    /// <summary>The task's committed record.</summary>
    internal static async Task<TaskRecord> RecordOf(IHarness harness, TaskId id) => (await harness.GetTaskAsync(id, Context))!;

    /// <summary>The outcome status of a task once it is terminal.</summary>
    internal static async Task<string> OutcomeOf(IHarness harness, TaskId id) =>
        StructuredTests.Wire(((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status);

    /// <summary>Replaces task records through the internal task write (upstream's <c>(tx as ...).setTask(...)</c>).</summary>
    internal static Task SetTasks(IHarness harness, Func<Transaction, Task> change) => ((DurableHarness)harness).CommitWithAsync(change, Context);

    /// <summary>A commit on a plain Session with the internal transaction.</summary>
    internal static Task<T> SessionCommit<T>(ISession session, Func<Transaction, Task<T>> change) => ((Session)session).CommitWithAsync(change, Context);

    /// <summary>Upstream <c>defineTask</c> of a task that runs until aborted and whose abort handler ends it <c>aborted</c>
    /// (harness-cancellation-barrier's <c>Dormant</c>).</summary>
    internal static DurableTask Dormant(string name = "test.dormant") => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => O("""{"phase":"work"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["work"] = async (_, runtime, _) => await Aborted(runtime.Signal),
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
    });
}

/// <summary>One case's gates, run counts and gated task definitions.</summary>
internal sealed class SchedulerGates
{
    private readonly ConcurrentDictionary<string, Deferred> gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> runs = new(StringComparer.Ordinal);

    /// <summary>Upstream <c>gate(name)</c>.</summary>
    public Deferred Gate(string name) => gates.GetOrAdd(name, _ => new Deferred());

    /// <summary>Upstream <c>gate(name).resolve()</c>.</summary>
    public void Open(string name) => Gate(name).Resolve();

    /// <summary>Upstream <c>gates.clear()</c>, between a close and a reopen.</summary>
    public void Clear() => gates.Clear();

    /// <summary>Upstream <c>runs.get(name) ?? 0</c>.</summary>
    public int Runs(string name) => runs.GetValueOrDefault(name);

    /// <summary>Waits for <paramref name="gate"/>; false once <paramref name="signal"/> aborts (upstream's race with <c>aborted()</c>).</summary>
    public static async Task<bool> Opened(Task gate, CancellationToken signal)
    {
        var aborted = Aborted(signal);
        var first = await Task.WhenAny(gate, aborted);
        _ = aborted.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return first == gate;
    }

    /// <summary>The input of a held task whose abort handler first waits for the gate <c>abort.&lt;name&gt;</c>.</summary>
    public static JsonObjectValue SlowAbort(string name) => Obj(("name", name), ("slowAbort", true));

    /// <summary>Holds until its gate opens, then completes; its abort handler ends it <c>aborted</c> (upstream <c>Hold</c>). It counts the
    /// runs of its phase by name. With <see cref="SlowAbort"/> input, the abort handler first waits for <c>abort.&lt;name&gt;</c> or its
    /// signal.</summary>
    public DurableTask Hold(string kind) => Tasks.DefineTask(new TaskDefinition
    {
        Name = kind,
        Version = 1,
        Initial = _ => O("""{"phase":"hold"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["hold"] = async (task, runtime, ctx) =>
            {
                var name = SchedulerTests.NameOf(task.Input);
                runs.AddOrUpdate(name, 1, (_, count) => count + 1);
                if (!await Opened(Gate(name).Task, runtime.Signal)) return;
                await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx);
            },
        },
        Abort = async (task, runtime, ctx) =>
        {
            if (((JsonObjectValue)task.Input).TryGetValue("slowAbort", out var slow) && slow is JsonBooleanValue { Value: true })
                await Opened(Gate($"abort.{SchedulerTests.NameOf(task.Input)}").Task, runtime.Signal);
            await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
        },
    });
}

/// <summary>A storage whose commits and reads a case observes or holds (upstream's patched <c>storage.commit</c>,
/// <c>storage.conversation</c>, <c>storage.task</c> and <c>storage.scanSubmissions</c>).</summary>
internal sealed class HookedStorage(IStorage inner) : DelegatingStorage(inner)
{
    public Func<IReadOnlyList<StorageWrite>, Task>? OnCommit { get; set; }
    public Func<ConversationId, Task>? OnConversation { get; set; }
    public Action<TaskId>? OnTask { get; set; }
    public Action<SubmissionQuery>? OnScanSubmissions { get; set; }

    public override async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        if (OnCommit is { } hook) await hook(writes);
        return await base.CommitAsync(writes, context);
    }

    public override async Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context)
    {
        if (OnConversation is { } hook) await hook(id);
        return await base.GetConversationAsync(id, context);
    }

    public override Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
    {
        OnTask?.Invoke(id);
        return base.GetTaskAsync(id, context);
    }

    public override Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        OnScanSubmissions?.Invoke(query);
        return base.ScanSubmissionsAsync(query, limit, cursor, context);
    }
}
