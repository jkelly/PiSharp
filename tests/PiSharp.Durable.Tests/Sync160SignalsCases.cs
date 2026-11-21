// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/harness-signals.test.ts. Signals and the wakes of parked tasks:
// delivery results, sleeps on signals and deadlines, `any` waits, kept wakes. Verbatim apart from the adaptations in HarnessSupport.cs, and
// these:
// - pi-schema builders are their JSON Schema: obj({text: str()}) is a closed object with `text` required.
// - A task's `state.wake` is RunningTask.Wake; toEqual and toMatchObject of states and outcomes compare their JSON (an undefined `wake` or
//   state is left out of a result, as JSON.stringify leaves it out).
// - openNodeSqliteStorage is SqliteStorage.OpenAsync over a file in a fresh temporary directory.
// - "reads the task, so it throws ReadAfterWrite after a table write": upstream delivers twice in a row and the second delivery throws,
//   as its own first delivery was a table write. Here a delivery passes after the writes of earlier deliveries of its transaction
//   (Session/Transaction.cs: .NET tasks can complete synchronously, so deliveries "started together" cannot be told from sequential
//   ones), and throws after any other table write. The case checks that, that sequential deliveries are decided against each other, and
//   that a table read after a delivery throws.
// - Not portable: the delivery of a value holding NaN ("the schema admits it, but NaN is not JSON"). A JsonValue cannot hold a non-finite
//   number, so the value cannot be built; Signals.Deliver has no such check to make.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160SignalsCases() => SignalTests.Cases();
}

/// <summary>The sync160.signals.* cases (upstream harness-signals.test.ts).</summary>
internal static class SignalTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>pi-schema's <c>obj({ [name]: { type } })</c>: a closed object with the property required.</summary>
    internal static JsonObjectValue ObjectSchema(string name, string type) =>
        O($$$"""{"type":"object","properties":{"{{{name}}}":{"type":"{{{type}}}"}},"required":["{{{name}}}"],"additionalProperties":false}""");

    internal static readonly SignalToken Answer =
        Signals.DefineSignal(new SignalDefinition { Name = "test.answer", Version = 1, Schema = ObjectSchema("text", "string") });

    private static JsonObjectValue Text(string text) => Obj(("text", text));

    private static IEnumerable<string> Keys(RunningTask task) => ((JsonArrayValue)((JsonObjectValue)task.Input)["keys"]).Select(key => ((JsonStringValue)key).Value);

    private static JsonValue StateJson(SignalState? state) =>
        state is null ? JsonValue.Null : state.Expired ? O("""{"expired":true}""") : Obj(("value", state.Value!));

    /// <summary>Sleeps on <c>Answer</c> under <c>keys</c> (and <c>until</c>), then completes with its wake and every key's state (null:
    /// none) (upstream <c>Ask</c>).</summary>
    internal static readonly DurableTask Ask = Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.ask",
        Version = 1,
        Initial = _ => O("""{"phase":"ask"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["ask"] = (task, runtime, ctx) =>
            {
                var until = ((JsonObjectValue)task.Input).TryGetValue("until", out var time) ? ((JsonNumberValue)time).Value : (double?)null;
                return runtime.CommitAsync((_, _) => NextTaskState.Sleeping(O("""{"phase":"answered"}"""), until, Keys(task).Select(Answer.Key)), ctx);
            },
            ["answered"] = async (task, runtime, ctx) =>
            {
                var states = new List<JsonValue>();
                foreach (var key in Keys(task)) states.Add(StateJson(await runtime.ReadSignalAsync(Answer, key, ctx)));
                var result = new List<KeyValuePair<string, JsonValue>>();
                if (task.Wake is { } wake) result.Add(new("wake", wake.ToJson()));
                result.Add(new("states", JsonValue.Array(states)));
                await runtime.CommitAsync((_, _) => Completed(JsonValue.Object(result)), ctx);
            },
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
    });

    private static JsonObjectValue AskInput(string[] keys, double? until = null) => until is { } time
        ? Obj(("keys", JsonValue.Array(keys.Select(key => (JsonValue)key))), ("until", time))
        : Obj(("keys", JsonValue.Array(keys.Select(key => (JsonValue)key))));

    private static async Task<TaskId> Start(IHarness harness, DurableTask task, JsonValue input)
    {
        var root = await harness.RootAsync(Context);
        return await root.CommitAsync(tx => tx.CreateTaskAsync(task, input, new TaskOptions(TaskOwnership.Conversation)), Context);
    }

    private static Task<TaskId> StartAsk(IHarness harness, string[] keys, double? until = null) => Start(harness, Ask, AskInput(keys, until));

    private static async Task<DurableTaskStatus?> StatusOf(IHarness harness, TaskId id) => (await harness.GetTaskAsync(id, Context))?.State.Status;

    private static async Task<JsonValue?> OutcomeOf(IHarness harness, TaskId id) =>
        ((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.ToJson();

    /// <summary>The wake of a <c>pending</c> or <c>running</c> state (upstream <c>wakeOf</c>).</summary>
    private static WakeReason? WakeOf(TaskState? state) => state switch
    {
        PendingTaskState pending => pending.Wake,
        RunningTaskState running => running.Wake,
        _ => null,
    };

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-signals"), "session.sqlite");

    private static DurableTask Simple(string name, string first, Dictionary<string, TaskPhaseHandler> phases) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => Obj(("phase", first)),
        Phases = phases,
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
    });

    /// <summary>Waits <c>any</c> on two <c>Ask</c> children; once woken, aborts the one still live and completes with its wake (upstream
    /// <c>Parent</c>).</summary>
    private static readonly DurableTask Parent = Simple("test.any", "spawn", new Dictionary<string, TaskPhaseHandler>
    {
        ["spawn"] = (_, runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
        {
            var own = new TaskOptions(TaskOwnership.Task(runtime.TaskId));
            TaskId[] on = [await tx.CreateTaskAsync(Ask, AskInput(["a"]), own), await tx.CreateTaskAsync(Ask, AskInput(["a"]), own)];
            var checkpoint = Obj(("phase", "joined"), ("on", JsonValue.Array(on.Select(id => (JsonValue)id.ToJson()))));
            return (TaskState?)NextTaskState.Waiting(checkpoint, on, JoinPolicy.Any);
        }, ctx),
        ["joined"] = async (task, runtime, ctx) =>
        {
            var wake = task.Wake;
            foreach (var id in (JsonArrayValue)((JsonObjectValue)task.Checkpoint)["on"])
                await runtime.AbortOwnedAsync(new TaskId((long)((JsonNumberValue)id).Value), ctx);
            await runtime.CommitAsync((_, _) => Completed(wake?.ToJson()), ctx);
        },
    });

    /// <summary>Starts <c>Parent</c> and waits until it waits on its two children, both asleep (upstream <c>startParent</c>).</summary>
    private static async Task<(TaskId Id, TaskId First, TaskId Second)> StartParent(IHarness harness)
    {
        var id = await Start(harness, Parent, JsonValue.Null);
        harness.Resume();
        await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Waiting);
        var on = ((WaitingTaskState)(await harness.GetTaskAsync(id, Context))!.State).On;
        await Eventually(async () => await StatusOf(harness, on[0]) == DurableTaskStatus.Sleeping);
        await Eventually(async () => await StatusOf(harness, on[1]) == DurableTaskStatus.Sleeping);
        return (id, on[0], on[1]);
    }

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "sync160.signals.";

        yield return Case(prefix + "define.rejects-names-with-a-slash-bad-versions-non-json-schemas-and-empty-keys", () =>
        {
            var text = O("""{"type":"string"}""");
            Check(Throws<ArgumentException>(() => Signals.DefineSignal(new SignalDefinition { Name = "a/b", Version = 1, Schema = text }),
                "slash").Message.Contains("without \"/\"", StringComparison.Ordinal), "slash message");
            Check(Throws<ArgumentException>(() => Signals.DefineSignal(new SignalDefinition { Name = "a", Version = 0, Schema = text }),
                "version").Message.Contains("positive integer", StringComparison.Ordinal), "version message");
            Check(Throws<ArgumentException>(() => Signals.DefineSignal(new SignalDefinition { Name = "a", Version = 1, Schema = ObjectSchema("n", "bigint") }),
                "bigint").Message.Contains("bigint", StringComparison.Ordinal), "bigint message");
            Check(Throws<ArgumentException>(() => Signals.DefineSignal(new SignalDefinition { Name = "a", Version = 1, Schema = ObjectSchema("b", "bytes") }),
                "bytes").Message.Contains("bytes", StringComparison.Ordinal), "bytes message");
            var dependent = Obj(("type", "object"), ("dependencies", Obj(("a", JsonValue.Array("b")), ("b", ObjectSchema("n", "bigint")))));
            Check(Throws<ArgumentException>(() => Signals.DefineSignal(new SignalDefinition { Name = "a", Version = 1, Schema = dependent }),
                "dependencies").Message.Contains("bigint", StringComparison.Ordinal), "dependencies message");
            // Annotations are values, not schemas.
            Signals.DefineSignal(new SignalDefinition
            {
                Name = "a", Version = 1,
                Schema = ObjectSchema("type", "string").SetItem("examples", JsonValue.Array(O("""{"type":"bigint"}"""))),
            });
            Check(Throws<ArgumentException>(() => Answer.Key(""), "empty key").Message.Contains("non-empty", StringComparison.Ordinal), "empty key message");
            SameJson("""{"signal":"test.answer","key":"k"}""", Answer.Key("k").ToJson(), "key");
        });

        yield return Case(prefix + "delivery.answers-not-found-invalid-delivered-duplicate-and-closed", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask]);
            var id = await StartAsk(harness, ["a"]);
            Equal(SignalResult.NotFound, await harness.SignalAsync(new TaskId(999_999), Answer, "a", Text("x"), Context), "not found");
            Equal(SignalResult.Invalid, await harness.SignalAsync(id, Answer, "a", Obj(("text", 1)), Context), "invalid");
            Equal(SignalResult.Delivered, await harness.SignalAsync(id, Answer, "a", Text("yes"), Context), "delivered");
            Equal(SignalResult.Duplicate, await harness.SignalAsync(id, Answer, "a", Text("again"), Context), "duplicate");
            MatchJson("""{"status":"completed"}""", await OutcomeOf(harness, id), "outcome");
            Equal(SignalResult.Closed, await harness.SignalAsync(id, Answer, "b", Text("late"), Context), "closed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "delivery.answers-closed-for-an-abort-marked-task", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask]);
            var id = await StartAsk(harness, ["a"]);
            await harness.AbortTaskAsync(id, Context);
            Equal(SignalResult.Closed, await harness.SignalAsync(id, Answer, "a", Text("yes"), Context), "closed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "delivery.decides-deliveries-started-together-in-one-commit-against-each-other", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask]);
            var id = await StartAsk(harness, ["a", "b"]);
            var results = await harness.CommitAsync(tx => Task.WhenAll(
                tx.SignalAsync(id, Answer, "a", Text("first")),
                tx.SignalAsync(id, Answer, "a", Text("second")),
                tx.SignalAsync(id, Answer, "b", Text("bee"))), Context);
            Check(results.SequenceEqual([SignalResult.Delivered, SignalResult.Duplicate, SignalResult.Delivered]), "results: " + string.Join(",", results));
            MatchJson("""{"result":{"states":[{"value":{"text":"first"}},{"value":{"text":"bee"}}]}}""", await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "delivery.reads-the-task-so-it-throws-read-after-write-after-a-table-write", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask]);
            var id = await StartAsk(harness, ["a"]);
            // Adapted (see the file header): the table write before the delivery is another task's creation, not a first delivery.
            await ThrowsAsync<ReadAfterWriteException>(() => harness.CommitAsync(async tx =>
            {
                await tx.CreateTaskAsync(Ask, AskInput(["z"]), new TaskOptions(TaskOwnership.Conversation) { ConversationId = ConversationId.Root });
                await tx.SignalAsync(id, Answer, "b", Text("two"));
            }, Context), "delivery after a table write");
            // Deliveries of one commit made one after another are decided against each other, as those started together are.
            var results = await harness.CommitAsync(async tx =>
                new[] { await tx.SignalAsync(id, Answer, "a", Text("one")), await tx.SignalAsync(id, Answer, "a", Text("two")) }, Context);
            Check(results.SequenceEqual([SignalResult.Delivered, SignalResult.Duplicate]), "sequential deliveries: " + string.Join(",", results));
            // A table read after a delivery still throws: it would not see the staged value.
            await ThrowsAsync<ReadAfterWriteException>(() => harness.CommitAsync(async tx =>
            {
                await tx.SignalAsync(id, Answer, "b", Text("bee"));
                await tx.GetTaskAsync(id);
            }, Context), "read after a delivery");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.keeps-a-value-delivered-before-the-sleep-which-then-wakes-at-once", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask]);
            var id = await StartAsk(harness, ["a"]);
            // Not running yet: the value waits in the task.
            Equal(SignalResult.Delivered, await harness.SignalAsync(id, Answer, "a", Text("early"), Context), "delivered");
            SameJson("""{"status":"completed","result":{"wake":{"reason":"signal","signal":"test.answer","key":"a"},"states":[{"value":{"text":"early"}}]}}""",
                await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.holds-no-invocation-while-it-waits-and-wakes-on-the-first-listed-key-holding-a-value", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask], onWake: _ => { });
            var graph = await harness.WatchTaskGraphAsync(Context);
            graph.Start((_, _) => Task.CompletedTask);
            var id = await StartAsk(harness, ["a", "b"]);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            await Eventually(() => !harness.Wake().Active);
            SameJson("""{"status":"sleeping","phase":"answered","signals":[{"signal":"test.answer","key":"a"},{"signal":"test.answer","key":"b"}]}""",
                graph.Value.Node(id)?.State.ToJson(), "graph state");
            await graph.StopAsync();
            // Without `until` there is no deadline to wake for.
            Equal(new Wake(false), harness.Wake(), "wake");
            var inspected = (await harness.InspectAsync(Context)).Tasks.First(task => task.Record.Id == id).State;
            Equal(TaskInspectionKind.Sleeping, inspected.Kind, "inspection kind");
            Equal<double?>(null, inspected.Until, "inspection until");
            Equal(SignalResult.Delivered, await harness.SignalAsync(id, Answer, "b", Text("bee"), Context), "delivered");
            SameJson("""{"status":"completed","result":{"wake":{"reason":"signal","signal":"test.answer","key":"b"},"states":[null,{"value":{"text":"bee"}}]}}""",
                await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.expires-its-keys-at-the-deadline-so-a-late-delivery-is-closed", async () =>
        {
            double clock = 1_000;
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask], now: () => Volatile.Read(ref clock), onWake: _ => { });
            var id = await StartAsk(harness, ["a"], 2_000);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false, 2_000), harness.Wake(), "wake while asleep");
            Volatile.Write(ref clock, 2_000);
            harness.Resume();
            SameJson("""{"status":"completed","result":{"wake":{"reason":"expired"},"states":[{"expired":true}]}}""", await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.answers-a-delivery-after-the-deadline-closed-and-expires-the-key-before-the-sleep-is-reserved", async () =>
        {
            double clock = 1_000;
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask], now: () => Volatile.Read(ref clock), onWake: _ => { });
            var id = await StartAsk(harness, ["a"], 2_000);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            // Due, but no pass ran since: the delivery itself finds the deadline passed.
            Volatile.Write(ref clock, 2_500);
            Equal(SignalResult.Closed, await harness.SignalAsync(id, Answer, "a", Text("late"), Context), "late delivery");
            MatchJson("""{"result":{"wake":{"reason":"expired"},"states":[{"expired":true}]}}""", await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.rejects-a-sleep-on-a-key-that-already-woke-a-wait-and-one-without-until-or-signals", async () =>
        {
            var outcomes = new SyncList<string>();
            var twice = Simple("test.twice", "first", new Dictionary<string, TaskPhaseHandler>
            {
                ["first"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) =>
                    ((JsonObjectValue)task.Input)["signals"] is JsonBooleanValue { Value: true }
                        ? NextTaskState.Sleeping(O("""{"phase":"second"}"""), null, [Answer.Key("a")])
                        : new SleepingTaskState(O("""{"phase":"second"}"""), null), ctx),
                ["second"] = async (_, runtime, ctx) =>
                {
                    try
                    {
                        await runtime.CommitAsync((_, _) => NextTaskState.Sleeping(O("""{"phase":"first"}"""), null, [Answer.Key("a")]), ctx);
                    }
                    catch (Exception error)
                    {
                        outcomes.Add(error.Message);
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    }
                },
            });
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [twice]);
            var consumed = await Start(harness, twice, O("""{"signals":true}"""));
            await harness.SignalAsync(consumed, Answer, "a", Text("once"), Context);
            await harness.WaitForTaskAsync(consumed, Context);
            Check(outcomes.Snapshot().SequenceEqual([$"Task {consumed} cannot sleep on signal test.answer key a: it already woke a wait"]),
                "consumed: " + string.Join("|", outcomes.Snapshot()));
            var empty = await Start(harness, twice, O("""{"signals":false}"""));
            MatchJson($$$"""{"status":"faulted","error":{"message":"Task {{{empty}}} cannot sleep without until or signals"}}""",
                await OutcomeOf(harness, empty), "empty sleep");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.wakes-on-the-first-listed-key-in-order-and-keeps-the-other-keys-value-for-a-later-sleep", async () =>
        {
            static JsonArrayValue Wakes(RunningTask task) => (JsonArrayValue)((JsonObjectValue)task.Checkpoint)["wakes"];
            var two = Simple("test.two", "first", new Dictionary<string, TaskPhaseHandler>
            {
                ["first"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) =>
                    NextTaskState.Sleeping(O("""{"phase":"second","wakes":[]}"""), null, [Answer.Key("a"), Answer.Key("b")]), ctx),
                ["second"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) =>
                    NextTaskState.Sleeping(Obj(("phase", "done"), ("wakes", JsonValue.Array(task.Wake!.ToJson()))), null, [Answer.Key("b")]), ctx),
                ["done"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(JsonValue.Array([.. Wakes(task), task.Wake!.ToJson()])), ctx),
            });
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [two]);
            var id = await Start(harness, two, JsonValue.Null);
            // Both before the first sleep; `b` is delivered first, but `a` is listed first.
            await harness.CommitAsync(tx => tx.SignalAsync(id, Answer, "b", Text("bee")), Context);
            await harness.CommitAsync(tx => tx.SignalAsync(id, Answer, "a", Text("ay")), Context);
            SameJson("""{"status":"completed","result":[{"reason":"signal","signal":"test.answer","key":"a"},{"reason":"signal","signal":"test.answer","key":"b"}]}""",
                await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.rejects-a-sleep-on-a-key-that-expired", async () =>
        {
            double clock = 1_000;
            var errors = new SyncList<string>();
            var again = Simple("test.again", "first", new Dictionary<string, TaskPhaseHandler>
            {
                ["first"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) =>
                    NextTaskState.Sleeping(O("""{"phase":"second"}"""), 2_000, [Answer.Key("a")]), ctx),
                ["second"] = async (_, runtime, ctx) =>
                {
                    try
                    {
                        await runtime.CommitAsync((_, _) => NextTaskState.Sleeping(O("""{"phase":"first"}"""), null, [Answer.Key("a")]), ctx);
                    }
                    catch (Exception error)
                    {
                        errors.Add(error.Message);
                    }
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            });
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [again], now: () => Volatile.Read(ref clock), onWake: _ => { });
            var id = await Start(harness, again, JsonValue.Null);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            Volatile.Write(ref clock, 2_000);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            Check(errors.Snapshot().SequenceEqual([$"Task {id} cannot sleep on signal test.answer key a: it expired"]), "errors: " + string.Join("|", errors.Snapshot()));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.ends-a-sleep-on-signals-at-once-when-aborted", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask]);
            var id = await StartAsk(harness, ["a"]);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            await harness.AbortTaskAsync(id, Context);
            SameJson("""{"status":"aborted","reason":"test"}""", await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.throws-on-reading-a-value-of-a-newer-version-or-of-an-older-one-without-migrate", async () =>
        {
            var v1 = Signals.DefineSignal(new SignalDefinition { Name = "test.versioned", Version = 1, Schema = ObjectSchema("n", "integer") });
            var v2 = Signals.DefineSignal(new SignalDefinition { Name = "test.versioned", Version = 2, Schema = ObjectSchema("n", "integer") });
            var errors = new SyncList<string>();
            var read = Simple("test.read-versions", "read", new Dictionary<string, TaskPhaseHandler>
            {
                ["read"] = async (task, runtime, ctx) =>
                {
                    try
                    {
                        await runtime.ReadSignalAsync(((JsonNumberValue)((JsonObjectValue)task.Input)["token"]).Value == 1 ? v1 : v2, "k", ctx);
                    }
                    catch (Exception error)
                    {
                        errors.Add(error.Message);
                    }
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            });
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [read]);
            var newer = await Start(harness, read, O("""{"token":1}"""));
            await harness.SignalAsync(newer, v2, "k", O("""{"n":2}"""), Context);
            var older = await Start(harness, read, O("""{"token":2}"""));
            await harness.SignalAsync(older, v1, "k", O("""{"n":1}"""), Context);
            await harness.WaitForTaskAsync(newer, Context);
            await harness.WaitForTaskAsync(older, Context);
            Check(errors.Snapshot().SequenceEqual([
                "Signal test.versioned value has version 2, newer than 1",
                "Signal test.versioned has no migration from version 1",
            ]), "errors: " + string.Join("|", errors.Snapshot()));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.migrates-a-value-stored-by-an-older-version-when-it-is-read", async () =>
        {
            var old = Signals.DefineSignal(new SignalDefinition { Name = "test.count", Version = 1, Schema = ObjectSchema("n", "string") });
            var current = Signals.DefineSignal(new SignalDefinition
            {
                Name = "test.count", Version = 2, Schema = ObjectSchema("n", "integer"),
                Migrate = (value, _) => Obj(("n", double.Parse(((JsonStringValue)((JsonObjectValue)value)["n"]).Value, System.Globalization.CultureInfo.InvariantCulture))),
            });
            var reads = new SyncList<JsonValue>();
            var read = Simple("test.read", "read", new Dictionary<string, TaskPhaseHandler>
            {
                ["read"] = async (_, runtime, ctx) =>
                {
                    reads.Add(StateJson(await runtime.ReadSignalAsync(current, "k", ctx)));
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            });
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [read]);
            var id = await Start(harness, read, JsonValue.Null);
            Equal(SignalResult.Delivered, await harness.SignalAsync(id, old, "k", O("""{"n":"42"}"""), Context), "delivered");
            await harness.WaitForTaskAsync(id, Context);
            SameJson("""[{"value":{"n":42}}]""", JsonValue.Array(reads.Snapshot()), "reads");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeping.composes-a-signal-with-the-tasks-own-state-change-in-one-commit", async () =>
        {
            var delivered = new SyncList<SignalResult>();
            var self = Simple("test.self", "start", new Dictionary<string, TaskPhaseHandler>
            {
                ["start"] = (_, runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                {
                    delivered.Add(await tx.SignalAsync(runtime.TaskId, Answer, "self", Text("me")));
                    return (TaskState?)NextTaskState.Sleeping(O("""{"phase":"woke"}"""), null, [Answer.Key("self")]);
                }, ctx),
                ["woke"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(task.Wake?.ToJson()), ctx),
            });
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [self]);
            var id = await Start(harness, self, JsonValue.Null);
            SameJson("""{"status":"completed","result":{"reason":"signal","signal":"test.answer","key":"self"}}""", await OutcomeOf(harness, id), "outcome");
            Check(delivered.Snapshot().SequenceEqual([SignalResult.Delivered]), "delivered in the commit");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "any.resumes-once-one-task-in-on-is-terminal-with-the-ended-ones-as-its-wake", async () =>
        {
            var (harness, _, _) = await OpenTasks(new MemoryStorage(), [Ask, Parent]);
            var (id, first, second) = await StartParent(harness);
            await harness.SignalAsync(second, Answer, "a", Text("done"), Context);
            SameJson($$$"""{"status":"completed","result":{"reason":"tasks","ended":[{{{second}}}]}}""", await OutcomeOf(harness, id), "outcome");
            MatchJson("""{"outcome":{"status":"aborted"}}""", (await harness.GetTaskAsync(first, Context))?.State.ToJson(), "the other child");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "any.is-active-in-wake-once-a-task-in-on-is-terminal-not-while-all-are-live", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
            var (harness, _, _) = await OpenTasks(storage, [Ask, Parent]);
            var (id, first, _) = await StartParent(harness);
            await harness.CloseAsync(Context);
            // Reopened paused, without `Ask`: the children are blocked, and only the waiter can become active.
            (harness, _, _) = await OpenTasks(storage.Reopen(), [Parent], onWake: _ => { });
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false), harness.Wake(), "wake while all are live");
            // Aborting a blocked child settles it `orphaned` at once, without scheduling.
            await harness.AbortTaskAsync(first, Context);
            Equal<DurableTaskStatus?>(DurableTaskStatus.Terminal, await StatusOf(harness, first), "orphaned child");
            await Eventually(() => harness.Wake().Active);
            harness.Resume();
            SameJson($$$"""{"status":"completed","result":{"reason":"tasks","ended":[{{{first}}}]}}""", await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "wakes.keeps-a-sleep-on-signals-asleep-across-reopen-active-once-a-value-is-delivered", async () =>
        {
            var path = SqlitePath();
            var (harness, _, _) = await OpenTasks(await SqliteStorage.OpenAsync(path), [Ask], onWake: _ => { });
            var id = await StartAsk(harness, ["a"]);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            await harness.CloseAsync(Context);
            (harness, _, _) = await OpenTasks(await SqliteStorage.OpenAsync(path), [Ask], onWake: _ => { });
            // Scheduling is paused: once open's recovery pass landed, the sleep needs no process.
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false), harness.Wake(), "wake asleep");
            // A commit of its own delivers without enabling scheduling: the task can run now.
            await harness.CommitAsync(tx => tx.SignalAsync(id, Answer, "a", Text("later")), Context);
            await Eventually(() => harness.Wake().Active);
            harness.Resume();
            MatchJson("""{"result":{"states":[{"value":{"text":"later"}}]}}""", await OutcomeOf(harness, id), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "wakes.counts-no-progress-for-a-woken-task-that-commits-its-checkpoint-again", async () =>
        {
            var path = SqlitePath();
            var restarts = new SyncList<int?>();
            var wakes = new SyncList<string?>();
            var again = Simple("test.recommit", "woke", new Dictionary<string, TaskPhaseHandler>
            {
                ["woke"] = async (_, runtime, ctx) =>
                {
                    await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"woke"}""")), ctx);
                    var record = await runtime.GetTaskAsync(runtime.TaskId, ctx);
                    restarts.Add(record?.Restarts);
                    wakes.Add(WakeOf(record?.State)?.ToJson().ToJson());
                    await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"next"}""")), ctx);
                },
                ["next"] = async (_, runtime, ctx) =>
                {
                    restarts.Add((await runtime.GetTaskAsync(runtime.TaskId, ctx))?.Restarts);
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            });
            var first = await OpenTasks(await SqliteStorage.OpenAsync(path), [again]);
            await first.Harness.RootAsync(Context);
            await first.Harness.CloseAsync(Context);
            // A woken task cut off by a crash: `running` with its wake, as a dead process left it.
            var storage = await SqliteStorage.OpenAsync(path);
            var id = await storage.MintIdAsync<TaskId>();
            var record = new TaskRecord
            {
                Id = id, ConversationId = ConversationId.Root, Kind = "test.recommit", Version = 1, Input = JsonValue.Null, Background = false,
                AbortRequested = false, State = new RunningTaskState(O("""{"phase":"woke"}"""), WakeReason.Time),
            };
            await storage.CommitWritesAsync([new TaskWrite(record)], Context);
            var (harness, _, _) = await OpenTasks(storage, [again]);
            SameJson("""{"status":"pending","checkpoint":{"phase":"woke"},"wake":{"reason":"time"}}""", (await harness.GetTaskAsync(id, Context))?.State.ToJson(),
                "pending again with its wake");
            await harness.WaitForTaskAsync(id, Context);
            // The same checkpoint again keeps the count; the next one starts it over.
            Check(restarts.Snapshot().SequenceEqual([1, null]), "restarts: " + string.Join(",", restarts.Snapshot()));
            Check(wakes.Snapshot().SequenceEqual(["""{"reason":"time"}"""]), "wake kept: " + string.Join(",", wakes.Snapshot()));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "wakes.keeps-the-wake-through-a-commit-of-the-same-checkpoint-and-a-reopen-and-drops-it-with-the-next-checkpoint", async () =>
        {
            var path = SqlitePath();
            var seen = new SyncList<string?>();
            var runs = 0;
            var keep = Simple("test.keep", "sleep", new Dictionary<string, TaskPhaseHandler>
            {
                ["sleep"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Sleeping(O("""{"phase":"woke"}"""), null, [Answer.Key("k")]), ctx),
                ["woke"] = async (task, runtime, ctx) =>
                {
                    seen.Add(task.Wake?.ToJson().ToJson());
                    // Intent: the same checkpoint again keeps the wake.
                    await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"woke"}""")), ctx);
                    seen.Add(WakeOf((await runtime.GetTaskAsync(runtime.TaskId, ctx))?.State)?.ToJson().ToJson());
                    // The first run is stopped by the close.
                    if (Interlocked.Increment(ref runs) == 1) await Aborted(runtime.Signal);
                    await runtime.CommitAsync((_, _) => NextTaskState.Running(O("""{"phase":"after"}""")), ctx);
                },
                ["after"] = async (_, runtime, ctx) =>
                {
                    seen.Add(WakeOf((await runtime.GetTaskAsync(runtime.TaskId, ctx))?.State)?.ToJson().ToJson());
                    await runtime.CommitAsync((_, _) => Completed(null), ctx);
                },
            });
            var (harness, _, _) = await OpenTasks(await SqliteStorage.OpenAsync(path), [keep]);
            var id = await Start(harness, keep, JsonValue.Null);
            await harness.SignalAsync(id, Answer, "k", Text("go"), Context);
            await Eventually(() => seen.Snapshot().Count == 2);
            const string wake = """{"reason":"signal","signal":"test.answer","key":"k"}""";
            SameJson($$$"""{"status":"running","checkpoint":{"phase":"woke"},"wake":{{{wake}}}}""", (await harness.GetTaskAsync(id, Context))?.State.ToJson(),
                "running with its wake");
            // Close stops the phase and writes the task back as pending, with its wake.
            await harness.CloseAsync(Context);
            Check(seen.Snapshot().SequenceEqual([wake, wake]), "seen before the close: " + string.Join("|", seen.Snapshot()));
            (harness, _, _) = await OpenTasks(await SqliteStorage.OpenAsync(path), [keep]);
            SameJson($$$"""{"status":"pending","checkpoint":{"phase":"woke"},"wake":{{{wake}}}}""", (await harness.GetTaskAsync(id, Context))?.State.ToJson(),
                "pending with its wake");
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            Check(seen.Snapshot().SequenceEqual([wake, wake, wake, wake, null]), "seen: " + string.Join("|", seen.Snapshot()));
            await harness.CloseAsync(Context);
        });
    }
}
