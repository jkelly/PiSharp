// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-inspect.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - Backends. Upstream runs this file on MemoryStorage; nothing in it depends on the backend, so HarnessInspectCases() runs it on
//   MemoryStorage (harness.inspect.*), HarnessInspectJsonlCases() on a fresh JSONL directory (harness.inspect.jsonl.*) and
//   HarnessInspectSqliteCases() on a fresh SQLite file (harness.inspect.sqlite.*).
// - toEqual on an inspection state compares Kind, Migrates, On and Reason; vitest compares an Error by its message, so error: new Error(m)
//   is a check of State.Error.Message. waitFor and eventually are Eventually.
// - The test.missing record is created by a definition the registry never holds, as upstream does.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessInspectCases() =>
        InspectTests.Cases("harness.inspect.", () => Task.FromResult<IStorage>(new MemoryStorage()));

    private static IEnumerable<(string, Func<Task>)> HarnessInspectJsonlCases() =>
        InspectTests.Cases("harness.inspect.jsonl.", async () => await JsonlStorage.OpenAsync(Temp("inspect-jsonl"), DurableContext.Background));

    private static IEnumerable<(string, Func<Task>)> HarnessInspectSqliteCases() =>
        InspectTests.Cases("harness.inspect.sqlite.", async () => await SqliteStorage.OpenAsync(Path.Combine(Temp("inspect-sqlite"), "inspect.sqlite")));
}

/// <summary>The harness.inspect.* cases.</summary>
internal static class InspectTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly JsonValue RunStep = O("""{"phase":"run"}""");

    /// <summary>A one-phase task that completes once <paramref name="gate"/> resolves; <paramref name="migrate"/> runs when an older stored
    /// version migrates (upstream <c>task</c>).</summary>
    private static DurableTask Step(string name, int version, Task? gate = null, Action? migrate = null) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = version,
        Initial = _ => RunStep,
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (_, runtime, ctx) =>
            {
                if (gate is not null) await gate;
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            },
        },
        Abort = (_, _, _) => Task.CompletedTask,
        Migrate = migrate is null ? null : (_, _, _) =>
        {
            migrate();
            return new TaskMigrationResult(JsonValue.Null, RunStep);
        },
    });

    private static TaskInspectionState? StateOf(ImmutableArray<TaskInspection> tasks, TaskId id) =>
        tasks.FirstOrDefault(entry => entry.Record.Id == id)?.State;

    /// <summary>toEqual on an inspection state (see the file header); <paramref name="error"/> is the expected error message.</summary>
    private static void SameState(TaskInspectionState expected, TaskInspectionState? actual, string what, string? error = null)
    {
        Check(actual is not null, what + ": no state");
        Equal(expected.Kind, actual!.Kind, what + " kind");
        Equal(expected.Migrates, actual.Migrates, what + " migrates");
        Seq(expected.On, actual.On, what + " on");
        Equal(expected.Reason, actual.Reason, what + " reason");
        Equal(error, actual.Error?.Message, what + " error");
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "derives-every-live-tasks-state-without-running-task-code", async () =>
        {
            var gate = new Deferred();
            var gateTask = Step("test.gate", 1, gate.Task);
            TaskId? gateId = null;
            var dependent = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.dependent",
                Version = 1,
                Initial = _ => O("""{"phase":"wait"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = async (_, runtime, ctx) =>
                    {
                        var checkpoint = O("""{"phase":"done"}""");
                        await runtime.CommitAsync((_, _) => NextTaskState.Waiting(checkpoint, [gateId!.Value], JoinPolicy.AllSettled), ctx);
                    },
                    ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(null), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var migrations = 0;
            DurableTask[] registered =
            [
                gateTask,
                dependent,
                Step("test.migrating", 2, migrate: () => Interlocked.Increment(ref migrations)),
                Step("test.no-migration", 2),
                Step("test.failing", 2, migrate: () => throw new InvalidOperationException("cannot migrate")),
                Step("test.too-old", 1),
            ];
            var (harness, _, _) = await OpenTasks(await fresh(), registered);
            var root = await harness.RootAsync(Context);
            var conversationOwned = new TaskOptions(TaskOwnership.Conversation);
            var ids = await root.CommitAsync(async tx =>
            {
                gateId = await tx.CreateTaskAsync(gateTask, JsonValue.Null, conversationOwned);
                return new
                {
                    Gate = gateId.Value,
                    Dependent = await tx.CreateTaskAsync(dependent, JsonValue.Null, conversationOwned),
                    // Stored by definitions other than the registered ones.
                    Migrating = await tx.CreateTaskAsync(Step("test.migrating", 1), JsonValue.Null, conversationOwned),
                    NoMigration = await tx.CreateTaskAsync(Step("test.no-migration", 1), JsonValue.Null, conversationOwned),
                    Failing = await tx.CreateTaskAsync(Step("test.failing", 1), JsonValue.Null, conversationOwned),
                    TooOld = await tx.CreateTaskAsync(Step("test.too-old", 2), JsonValue.Null, conversationOwned),
                    Missing = await tx.CreateTaskAsync(Step("test.missing", 1), JsonValue.Null, conversationOwned),
                };
            }, Context);

            var paused = await harness.InspectAsync(Context);
            Equal(HarnessScheduling.Paused, paused.Scheduling, "paused scheduling");
            Seq([ids.Gate, ids.Dependent, ids.Migrating, ids.NoMigration, ids.Failing, ids.TooOld, ids.Missing],
                paused.Tasks.Select(entry => entry.Record.Id), "paused tasks");
            SameState(new(TaskInspectionKind.Ready), StateOf(paused.Tasks, ids.Gate), "gate");
            SameState(new(TaskInspectionKind.Ready), StateOf(paused.Tasks, ids.Dependent), "dependent");
            SameState(new(TaskInspectionKind.Ready) { Migrates = true }, StateOf(paused.Tasks, ids.Migrating), "migrating");
            // A migration that was never tried is not run to find out.
            SameState(new(TaskInspectionKind.Ready) { Migrates = true }, StateOf(paused.Tasks, ids.Failing), "failing");
            SameState(new(TaskInspectionKind.Blocked) { Reason = BlockedReason.MigrationFailed }, StateOf(paused.Tasks, ids.NoMigration), "no migration",
                "Task test.no-migration version 2 has no migration from 1");
            SameState(new(TaskInspectionKind.Blocked) { Reason = BlockedReason.TaskTooOld }, StateOf(paused.Tasks, ids.TooOld), "too old");
            SameState(new(TaskInspectionKind.Blocked) { Reason = BlockedReason.MissingTask }, StateOf(paused.Tasks, ids.Missing), "missing");
            Equal(0, Volatile.Read(ref migrations), "migrations while paused");
            Equal(HarnessScheduling.Paused, (await harness.InspectAsync(Context)).Scheduling, "still paused");

            harness.Resume();
            await Eventually(() => Volatile.Read(ref migrations) == 1);
            await harness.WaitForTaskAsync(ids.Migrating, Context);
            var running = await harness.InspectAsync(Context);
            await Eventually(async () =>
            {
                running = await harness.InspectAsync(Context);
                return StateOf(running.Tasks, ids.Gate)?.Kind == TaskInspectionKind.Running &&
                    StateOf(running.Tasks, ids.Dependent)?.Kind == TaskInspectionKind.Waiting;
            });
            SameState(new(TaskInspectionKind.Waiting) { On = [ids.Gate] }, StateOf(running.Tasks, ids.Dependent), "waiting dependent");
            Equal(HarnessScheduling.Running, running.Scheduling, "running scheduling");
            SameState(new(TaskInspectionKind.Running), StateOf(running.Tasks, ids.Gate), "running gate");
            Check(StateOf(running.Tasks, ids.Migrating) is null, "the migrated task finished");
            SameState(new(TaskInspectionKind.Blocked) { Reason = BlockedReason.MigrationFailed }, StateOf(running.Tasks, ids.Failing), "failed migration",
                "cannot migrate");

            gate.Resolve();
            await harness.WaitForTaskAsync(ids.Dependent, Context);
            var settled = await harness.InspectAsync(Context);
            Seq([ids.NoMigration, ids.Failing, ids.TooOld, ids.Missing], settled.Tasks.Select(entry => entry.Record.Id), "settled tasks");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lists-unsettled-submissions", async () =>
        {
            var setup = Setup();
            var (step, reached) = Unanswered();
            setup.Faux.SetResponses(step);
            var (harness, root) = await OpenChat(await fresh(), setup);
            var other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            await other.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "note" }), Context);
            var input = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await reached;

            var inspection = await harness.InspectAsync(Context);
            Seq([(await input.StatusAsync(Context)).ToJson().ToJson()], inspection.Submissions.Select(record => record.ToJson().ToJson()), "submissions");
            Seq(["pi.generation running"], inspection.Tasks.Select(entry => $"{entry.Record.Kind} {entry.State.Kind.ToString().ToLowerInvariant()}"), "tasks");
            await harness.CloseAsync(Context);
        });
    }
}
