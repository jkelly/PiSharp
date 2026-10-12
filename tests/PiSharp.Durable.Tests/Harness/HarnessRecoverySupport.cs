// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): the support of packages/durable/test/harness-tasks-recovery.test.ts (its sqlitePath,
// createIn, TransferService, transferTask and versioned helpers, plus vitest's toMatchObject). The harness.tasks-recovery.* groups live in
// their own class so their helpers never collide with other harness.* groups; the shared support of HarnessSupport.cs is reached through
// using static, and SessionTests.Deferred is shadowed by an alias.
//
// Backends: upstream runs its reopen scenarios on a SQLite file (openNodeSqliteStorage) and everything else on MemoryStorage. The first pass
// (harness.tasks-recovery.*) does the same; the storage-agnostic scenarios rerun on JSONL (every case, reopening the same directory) and
// SQLite (the cases upstream runs on MemoryStorage), as HarnessBackendCases.cs does for harness.tasks.*. A reopened storage is always
// closed before the next open of the same file, as Windows requires.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessRecoveryCases() =>
    [
        .. HarnessRecoveryTests.RecoveryCases("harness.tasks-recovery.", HarnessRecoveryTests.Backend.Upstream),
        .. HarnessRecoveryTests.RecoveryCases("harness.tasks-recovery.jsonl.", HarnessRecoveryTests.Backend.Jsonl),
        .. HarnessRecoveryTests.RecoveryCases("harness.tasks-recovery.sqlite.", HarnessRecoveryTests.Backend.Sqlite),
    ];
}

/// <summary>The harness.tasks-recovery.* case groups and their support.</summary>
internal static partial class HarnessRecoveryTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly JsonValue RunStep = O("""{"phase":"run"}""");

    /// <summary>
    /// Where a pass keeps its storages: <see cref="Fresh"/> opens a new storage for a single-Harness case, and <see cref="Reopenable"/> returns
    /// an opener for one new location, which opens the same file again on each call; null skips the reopen cases.
    /// </summary>
    internal sealed record Backend(Func<IStorage> Fresh, Func<Func<Task<IStorage>>>? Reopenable)
    {
        /// <summary>Upstream's storages: MemoryStorage, and a SQLite file where it reopens.</summary>
        public static Backend Upstream { get; } = new(() => new MemoryStorage(), SqliteFile);

        /// <summary>A fresh JSONL directory for every storage, reopened in place.</summary>
        public static Backend Jsonl { get; } = new(
            () => JsonlStorage.OpenAsync(Temp("recovery-jsonl"), DurableContext.Background).GetAwaiter().GetResult(),
            () =>
            {
                var directory = Temp("recovery-jsonl");
                return async () => await JsonlStorage.OpenAsync(directory, DurableContext.Background);
            });

        /// <summary>A fresh SQLite file for the cases upstream runs on MemoryStorage; its reopen cases are the upstream pass.</summary>
        public static Backend Sqlite { get; } = new(
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("recovery-sqlite"), "session.sqlite")).GetAwaiter().GetResult(), null);

        /// <summary>Upstream <c>sqlitePath()</c> plus <c>openNodeSqliteStorage(path)</c>.</summary>
        private static Func<Task<IStorage>> SqliteFile()
        {
            var path = Path.Combine(Temp("recovery-sqlite"), "session.sqlite");
            return async () => await SqliteStorage.OpenAsync(path);
        }
    }

    /// <summary>Creates a conversation-owned task in the root conversation (upstream <c>createIn</c>).</summary>
    private static async Task<TaskId> CreateIn(IHarness harness, DurableTask task, JsonValue? input = null)
    {
        var root = await harness.RootAsync(Context);
        return await root.CommitAsync(tx => tx.CreateTaskAsync(task, input ?? JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
    }

    /// <summary>A promise that never settles (upstream <c>new Promise(() => {})</c>).</summary>
    private static Task Never() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    /// <summary>The terminal outcome of a settled record.</summary>
    private static JsonValue OutcomeOf(TaskRecord record) => ((SettlingTaskState)record.State).Outcome.ToJson();

    /// <summary>vitest's <c>toMatchObject</c>: every property <paramref name="expected"/> names matches, recursively; arrays match
    /// element-wise with equal length; other values are deep-equal.</summary>
    private static void MatchJson(string expected, JsonValue? actual, string what) =>
        Check(actual is not null && Matches(J(expected), actual), $"{what}: expected a match of {expected}, actual {actual?.ToJson() ?? "undefined"}");

    private static bool Matches(JsonValue expected, JsonValue actual) => (expected, actual) switch
    {
        (JsonObjectValue subset, JsonObjectValue value) =>
            subset.All(property => value.TryGetValue(property.Key, out var item) && Matches(property.Value, item)),
        (JsonArrayValue items, JsonArrayValue values) =>
            items.Count == values.Count && Enumerable.Range(0, items.Count).All(index => Matches(items[index], values[index])),
        _ => JsonValue.DeepEquals(expected, actual),
    };

    /// <summary>The <c>error.cause</c> a scheduler report carries.</summary>
    private static object? Cause(Exception report) => report.Data["cause"];

    /// <summary>Upstream <c>String(error)</c>: the error's name and message.</summary>
    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";

    /// <summary>Fake external service whose operations are idempotent by request key.</summary>
    private sealed class TransferService
    {
        private readonly Dictionary<string, double> applied = [];
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public int AppliedCount
        {
            get { lock (applied) return applied.Count; }
        }

        public Task<double> ApplyAsync(string key, double amount)
        {
            Interlocked.Increment(ref calls);
            lock (applied)
            {
                if (!applied.ContainsKey(key)) applied[key] = amount * 10;
                return Task.FromResult(applied[key]);
            }
        }
    }

    /// <summary>Upstream's <c>{ first: boolean }</c> interrupt flag.</summary>
    private sealed class Interrupt
    {
        private int first = 1;

        /// <summary>Clears the flag; true when it was set.</summary>
        public bool TakeFirst() => Interlocked.Exchange(ref first, 0) == 1;
    }

    /// <summary>
    /// Intent/effect/outcome task: <c>prepare</c> commits the intent, <c>apply</c> performs the effect and commits the outcome. While
    /// <paramref name="interrupt"/> is set, the first <c>apply</c> blocks after the effect until the invocation is signalled.
    /// </summary>
    private static DurableTask TransferTask(TransferService service, Interrupt interrupt) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.transfer",
        Version = 1,
        Initial = _ => O("""{"phase":"prepare"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["prepare"] = async (task, runtime, ctx) =>
            {
                await runtime.MemoAsync("requested", ((JsonObjectValue)task.Input)["amount"], ctx);
                await runtime.CommitAsync((_, _) => NextTaskState.Running(Obj(("phase", "apply"), ("key", $"transfer-{task.Id}"))), ctx);
            },
            ["apply"] = async (task, runtime, ctx) =>
            {
                var receipt = await service.ApplyAsync(S(((JsonObjectValue)task.Checkpoint)["key"]), N(((JsonObjectValue)task.Input)["amount"]));
                if (interrupt.TakeFirst()) await Aborted(runtime.Signal);
                await runtime.CommitAsync((_, _) => Completed(Obj(("receipt", receipt))), ctx);
            },
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
    });

    /// <summary>A versioned one-phase task that completes with <paramref name="result"/>, optionally migrating older records (upstream
    /// <c>versioned</c>).</summary>
    private static DurableTask Versioned(int version, string result, TaskMigration? migrate = null) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.versioned",
        Version = version,
        Initial = _ => RunStep,
        Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(result), ctx) },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith(result), ctx),
        Migrate = migrate,
    });

    /// <summary>Every write of task <paramref name="id"/> committed to <paramref name="storage"/>, in commit order. Read after close, so no
    /// commit lands meanwhile; close writes no task state.</summary>
    private static List<TaskRecord> WritesOf(ControlledStorage storage, TaskId id)
    {
        var count = storage.CommitCount;
        return [.. storage.Commits.Take(count).SelectMany(batch => batch.OfType<TaskWrite>().Select(write => write.Value).Where(record => record.Id == id))];
    }

    /// <summary>The lower-camel wire name of a task status.</summary>
    private static string Wire(DurableTaskStatus status)
    {
        var name = status.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
