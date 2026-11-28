// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/harness-hook-tasks.test.ts. Hooks that start tasks (spec
// suspendable, section 6): afterTools tasks that end the run or answer it with a steer, and onYield tasks that decide whether an answer
// stands while the run's inputs stay open. Verbatim apart from the adaptations in HarnessSupport.cs/ChatSupport.cs, and these:
// - What a hook returns: afterTools returns the requests themselves, null for none (GenerationHooks.AfterTools), where upstream returns
//   { tasks }; onYield returns OnYieldResult.Tasks(...), null for none; afterRun returns null for none.
// - What a decider completes with (upstream's Plan.result) is JSON: new AfterToolsDecision(true).ToJson() for { terminate: true },
//   new YieldDecision(x).ToJson() for { continue: x }, JsonValue.Null for null, and the raw JSON for the continuations that hold no valid
//   user input ({ continue: 5 } and the like), which upstream builds with a cast.
// - obj({}) is its JSON Schema; the noop tool leaves ToolRegistration.Parameters at its default, a tool without parameters, and its
//   `execute: async () => ({})` is an empty ToolExecutionResult.
// - pi.live is JSON: `live?.run?.yield?.answer` is Field(live, "run", "yield", "answer"), an EntryId when it is a number. seen.yields is a
//   SyncList of EntryId? (null: undefined); seen.asked and the recovery case's `asked` are counted with Interlocked, as hooks run on the
//   thread pool.
// - `settled.status === "done" && settled.type === "input" ? settled.answer : undefined` is AnswerOf(settled).
// - expect(setup.infos).toContainEqual(expect.objectContaining({ kind: "extension_error", extension: "deciders" })) is a search of
//   ChatSetup.Infos for a ReportInfo of that kind and extension.
// - openNodeSqliteStorage is SqliteStorage.OpenAsync over session.sqlite in a fresh temporary directory; commitWrites is
//   PiSharp.Durable.Testing.CommitWrites.CommitWritesAsync; waitFor is Eventually.
// - "asks the afterTools hooks again after a crash before their tasks exist, not after": upstream leaves the first Harness open, its hook
//   held by a gate, opens the next one over the same file, and at the end resolves the gate and closes the first Harness. Here the first
//   Harness's storage is a KillableStorage (HarnessRestartFailureCases.cs) that is killed once its hook was asked, as the other ports
//   simulate a process that dies (HarnessAfterRunCases.cs): every later call of that storage hangs, so the dead process can write nothing
//   into the file the next ones own. Its gate is never resolved and its Harness never closed; its storage is closed after the case
//   (KilledStorages), as upstream's afterEach removes the directory.
// - settleSchedulerOutcome(tx, generation, { status: "faulted", error: { message: "broken" } }) is Live.SettleSchedulerOutcomeAsync with
//   a FaultedTaskOutcome, called in a Harness commit as upstream calls it.
// Every upstream case is ported.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160HookTasksCases() => HookTaskTests.Cases();
}

/// <summary>The sync160.hook-tasks.* cases (upstream harness-hook-tasks.test.ts).</summary>
internal static class HookTaskTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-hook-tasks"), "session.sqlite");

    /// <summary>A tool-calling answer with one call of <c>noop</c> (upstream <c>CALL</c>).</summary>
    private static readonly JsonObjectValue CallNoop = FauxAssistantMessage([FauxToolCall("noop", JsonObjectValue.Empty, "c1")], stopReason: "toolUse");

    /// <summary>What a hook task completes with, an optional steer it submits in its terminal commit, and an optional gate it waits for
    /// (upstream <c>Plan</c>).</summary>
    private sealed record Plan(JsonValue Result, string? Steer = null, Deferred? Gate = null);

    /// <summary>What the deciders and the hook of a setup saw (upstream <c>seen</c>).</summary>
    private sealed class Seen
    {
        private int asked;

        /// <summary><c>pi.live.run.yield.answer</c> as each decider read it; null: none.</summary>
        public SyncList<EntryId?> Yields { get; } = new();
        /// <summary>Counts one call of the hook and returns how many there were.</summary>
        public int Ask() => Interlocked.Increment(ref asked);
    }

    private static JsonValue? Field(JsonValue? value, params string[] path)
    {
        foreach (var key in path) value = value is JsonObjectValue fields && fields.TryGetValue(key, out var found) ? found : null;
        return value;
    }

    private static EntryId? EntryOf(JsonValue? value) => value is JsonNumberValue number ? new EntryId((long)number.Value) : null;

    private static Task<JsonObjectValue?> LiveOf(OpenedChat opened) => opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context);

    /// <summary>Whether the run waits for its onYield tasks (upstream <c>...?.run?.yield !== undefined</c>).</summary>
    private static async Task<bool> Yielded(OpenedChat opened) => Field(await LiveOf(opened), "run", "yield") is not null;

    /// <summary>The answer of a done input; null otherwise.</summary>
    private static EntryId? AnswerOf(SubmissionRecord settled) =>
        settled.Status == SubmissionStatus.Done && settled.Type == SubmissionType.Input ? settled.Answer : null;

    private static JsonValue? FirstMessage(EntryRecord entry) => entry.Model is { Count: > 0 } model ? model[0] : null;

    private static ToolRegistration Noop() => Define.Tool(new ToolRegistration
    {
        Name = "noop",
        Description = "noop",
        Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult()),
    });

    /// <summary>A task that waits for its plan's gate, submits its steer, and completes with its plan's result (upstream
    /// <c>deciderTask</c>).</summary>
    private static DurableTask DeciderTask(Plan plan, Seen seen) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.decider",
        Version = 1,
        Initial = _ => O("""{"phase":"decide"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["decide"] = async (_, runtime, ctx) =>
            {
                if (plan.Gate is { } gate) await await Task.WhenAny(gate.Task, Aborted(runtime.Signal));
                // The answer a yield task decides about is in `run.yield`.
                seen.Yields.Add(EntryOf(Field(await runtime.SnapshotAsync(Live.LiveDoc, runtime.ConversationId, ctx), "run", "yield", "answer")));
                var handle = (await runtime.ConversationAsync(runtime.ConversationId, ctx))!;
                await runtime.CommitAsync(async (tx, _) =>
                {
                    if (plan.Steer is { } steer) await handle.AdmitAsync(tx, SubmissionDraft.Input(steer, WhenBusy.Steer));
                    return (TaskState?)Completed(plan.Result);
                }, ctx);
            },
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted(), ctx),
    });

    private enum HookName
    {
        AfterTools,
        OnYield,
    }

    /// <summary>A setup whose <paramref name="hook"/> asks for one decider the first time it is asked, and for nothing after (upstream
    /// <c>setupWith</c>).</summary>
    private static (ChatSetup Setup, Seen Seen) SetupWith(HookName hook, Plan plan)
    {
        var setup = Setup();
        var seen = new Seen();
        var decider = DeciderTask(plan, seen);
        IReadOnlyList<HookTask>? Request() => seen.Ask() == 1 ? [Define.HookTask(decider, JsonValue.Null)] : null;
        var handlers = hook == HookName.AfterTools
            ? new GenerationHooks { AfterTools = (_, _, _, _) => Task.FromResult<IReadOnlyList<HookTask>?>(Request()) }
            : new GenerationHooks { OnYield = (_, _, _) => Task.FromResult<OnYieldResult?>(Request() is { } tasks ? OnYieldResult.Tasks(tasks) : null) };
        setup.Registry.Install(Define.Extension(new Extension
        {
            Name = "deciders",
            Tools = [Noop()],
            Tasks = [decider],
            Hooks = [Define.Hook(HarnessTasks.GenerationTask, handlers)],
        }));
        return (setup, seen);
    }

    /// <summary>Opens a chat on a fresh MemoryStorage with scheduling enabled (upstream <c>open</c>).</summary>
    private static async Task<OpenedChat> Open(ChatSetup setup)
    {
        var opened = await OpenChat(new MemoryStorage(), setup);
        opened.Harness.Resume();
        return opened;
    }

    /// <summary>The text of every entry whose first model message has one, oldest first (upstream <c>texts</c>).</summary>
    private static async Task<List<string>> Texts(OpenedChat opened) =>
        [.. (await AllEntries(opened.Root)).Select(entry => TextOf(FirstMessage(entry))).OfType<string>()];

    /// <summary>The key the parking deciders sleep on (upstream <c>Go</c>).</summary>
    private static readonly SignalToken Go = Signals.DefineSignal(new SignalDefinition
    {
        Name = "test.go",
        Version = 1,
        Schema = O("""{"type":"object","properties":{},"required":[],"additionalProperties":false}"""),
    });

    /// <summary>A decider that parks on <c>Go</c> until a delivery, then completes with <paramref name="result"/> (upstream
    /// <c>parkingDecider</c>).</summary>
    private static DurableTask ParkingDecider(JsonValue result) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.parking-decider",
        Version = 1,
        Initial = _ => O("""{"phase":"park"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["park"] = (_, runtime, ctx) =>
                runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Sleeping(O("""{"phase":"done"}"""), null, [Go.Key("go")]), ctx),
            ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => (TaskState?)Completed(result), ctx),
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted(), ctx),
    });

    /// <summary>Installs the <c>deciders</c> extension of the yield recovery cases: <paramref name="decider"/>, and an onYield hook that
    /// always asks for it.</summary>
    private static void InstallYieldDeciders(ChatSetup setup, DurableTask decider) => setup.Registry.Install(Define.Extension(new Extension
    {
        Name = "deciders",
        Tasks = [decider],
        Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
        {
            OnYield = (_, _, _) => Task.FromResult<OnYieldResult?>(OnYieldResult.Tasks([Define.HookTask(decider, JsonValue.Null)])),
        })],
    }));

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "sync160.hook-tasks.";

        // --- afterTools tasks ---

        yield return Case(prefix + "after-tools.end-the-run-with-terminate-true-as-a-round-whose-calls-all-asked-to-terminate", async () =>
        {
            var (setup, _) = SetupWith(HookName.AfterTools, new Plan(new AfterToolsDecision(true).ToJson()));
            setup.Faux.SetResponses(CallNoop);
            var opened = await Open(setup);
            var settled = await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            // Answered with the tool-calling answer; no further request.
            Equal<EntryId?>((await AllEntries(opened.Root)).FirstOrDefault(entry => entry.Kind == "pi.assistant")?.Id, AnswerOf(settled), "answer");
            Equal(1, setup.Faux.State.CallCount, "calls");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "after-tools.answer-the-run-with-a-steer-the-next-request-sees", async () =>
        {
            var (setup, _) = SetupWith(HookName.AfterTools, new Plan(JsonValue.Null, Steer: "also check the tests"));
            setup.Faux.SetResponses(CallNoop, FauxAssistantMessage("checked"));
            var opened = await Open(setup);
            Equal(SubmissionStatus.Done, (await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "status");
            Seq(["go", "also check the tests", "checked"], await Texts(opened), "texts");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "after-tools.report-a-result-that-is-no-after-tools-decision-and-go-on", async () =>
        {
            var (setup, _) = SetupWith(HookName.AfterTools, new Plan(new YieldDecision("x").ToJson()));
            setup.Faux.SetResponses(CallNoop, FauxAssistantMessage("done"));
            var opened = await Open(setup);
            Equal(SubmissionStatus.Done, (await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "status");
            var infos = setup.Infos.Snapshot();
            Check(infos.Any(info => info.Kind == ReportKind.ExtensionError && info.Extension == "deciders"),
                "an extension_error report of the deciders extension; infos: " + string.Join(" | ", infos));
            Equal(2, setup.Faux.State.CallCount, "calls");
            await opened.Harness.CloseAsync(Context);
        });

        // --- onYield tasks ---

        yield return Case(prefix + "on-yield.keep-the-inputs-open-while-they-decide-and-continue-the-run-with-continue", async () =>
        {
            var gate = new Deferred();
            var (setup, seen) = SetupWith(HookName.OnYield, new Plan(new YieldDecision("review: add a test").ToJson(), Gate: gate));
            setup.Faux.SetResponses(FauxAssistantMessage("first"), FauxAssistantMessage("second"));
            var opened = await Open(setup);
            var submission = await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Eventually(() => Yielded(opened));
            // The answer is appended, the input not yet answered.
            Equal(SubmissionStatus.Placed, (await submission.StatusAsync(Context)).Status, "status while the task decides");
            gate.Resolve();
            var settled = await submission.WaitAsync(Context);
            Seq(["go", "first", "review: add a test", "second"], await Texts(opened), "texts");
            var second = (await AllEntries(opened.Root))[^1].Id;
            Equal<EntryId?>(second, AnswerOf(settled), "answer");
            var first = (await AllEntries(opened.Root)).First(entry => TextOf(FirstMessage(entry)) == "first").Id;
            Seq<EntryId?>([first], seen.Yields.Snapshot(), "yields the decider read");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "on-yield.ignore-a-continuation-that-holds-no-valid-user-input", async () =>
        {
            foreach (var input in new[] { "5", "[]", """[{"type":"text"}]""", """[{"type":"image","data":"x"}]""" })
            {
                var (setup, _) = SetupWith(HookName.OnYield, new Plan(Obj(("continue", J(input)))));
                setup.Faux.SetResponses(FauxAssistantMessage("first"));
                var opened = await Open(setup);
                Equal(SubmissionStatus.Done, (await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status,
                    $"status for {input}");
                Seq(["go", "first"], await Texts(opened), $"texts for {input}");
                await opened.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "on-yield.let-an-answer-that-stands-run-its-after-run-tasks-next-without-run-yield", async () =>
        {
            var (setup, _) = SetupWith(HookName.OnYield, new Plan(JsonValue.Null));
            var asked = new SyncList<JsonValue?>();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "after-run",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = async (_, api, ctx) =>
                    {
                        asked.Add(Field(await api.SnapshotAsync(Live.LiveDoc, api.ConversationId, ctx), "run", "yield"));
                        return null;
                    },
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("first"));
            var opened = await Open(setup);
            Equal(SubmissionStatus.Done, (await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "status");
            await opened.Root.WaitForIdleAsync(Context);
            var yields = asked.Snapshot();
            Check(yields.Count == 1 && yields[0] is null,
                $"run.yield as the afterRun hook read it: expected [undefined], actual [{string.Join(", ", yields.Select(value => value?.ToJson() ?? "undefined"))}]");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "on-yield.let-the-answer-stand-without-a-continuation", async () =>
        {
            var (setup, _) = SetupWith(HookName.OnYield, new Plan(JsonValue.Null));
            setup.Faux.SetResponses(FauxAssistantMessage("first"));
            var opened = await Open(setup);
            var settled = await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            Seq(["go", "first"], await Texts(opened), "texts");
            Equal<JsonValue?>(null, Field(await LiveOf(opened), "run"), "run");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "on-yield.lose-their-continuation-to-a-user-message-queued-meanwhile", async () =>
        {
            var gate = new Deferred();
            var (setup, _) = SetupWith(HookName.OnYield, new Plan(new YieldDecision("review: add a test").ToJson(), Gate: gate));
            setup.Faux.SetResponses(FauxAssistantMessage("first"), FauxAssistantMessage("to you"));
            var opened = await Open(setup);
            var submission = await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Eventually(() => Yielded(opened));
            var queued = await opened.Root.SubmitAsync(SubmissionDraft.Input("a question", WhenBusy.FollowUp), Context);
            gate.Resolve();
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "the run's input");
            Equal(SubmissionStatus.Done, (await queued.WaitAsync(Context)).Status, "the queued input");
            Seq(["go", "first", "a question", "to you"], await Texts(opened), "texts");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "on-yield.settle-the-inputs-done-with-the-answer-when-the-run-is-aborted-while-they-decide", async () =>
        {
            var gate = new Deferred();
            var (setup, _) = SetupWith(HookName.OnYield, new Plan(new YieldDecision("never").ToJson(), Gate: gate));
            setup.Faux.SetResponses(FauxAssistantMessage("first"));
            var opened = await Open(setup);
            var submission = await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Eventually(() => Yielded(opened));
            await opened.Root.AbortAsync(Context);
            var settled = await submission.WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            var live = await LiveOf(opened);
            Equal<JsonValue?>(null, Field(live, "run"), "run");
            Equal<JsonValue?>(null, Field(live, "runFailure"), "runFailure");
            await opened.Harness.CloseAsync(Context);
        });

        // --- recovery ---

        yield return KilledStorages.Case(prefix + "recovery.asks-the-after-tools-hooks-again-after-a-crash-before-their-tasks-exist-not-after", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var decider = ParkingDecider(JsonValue.Null);
            // Never resolved: the process whose hook waits for it is dead (see the file header).
            var gate = new Deferred();
            var asked = 0;
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "deciders",
                Tools = [Noop()],
                Tasks = [decider],
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterTools = async (_, _, _, _) =>
                    {
                        // The first process dies while its hook runs.
                        if (Interlocked.Increment(ref asked) == 1) await gate.Task;
                        return [Define.HookTask(decider, JsonValue.Null)];
                    },
                })],
            }));
            setup.Faux.SetResponses(CallNoop, FauxAssistantMessage("done"));
            var (storage, kill) = killed.Killable(await SqliteStorage.OpenAsync(path));
            var crashed = await OpenChat(storage, setup);
            crashed.Harness.Resume();
            var id = (await crashed.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await Eventually(() => Volatile.Read(ref asked) == 1);
            kill();
            var next = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            next.Harness.Resume();
            // Asked again: nothing was created before the crash.
            await Eventually(() => Volatile.Read(ref asked) == 2);
            TaskId? parked = null;
            await Eventually(async () =>
            {
                parked = (await next.Harness.InspectAsync(Context)).Tasks.FirstOrDefault(task => task.Record.Kind == decider.Definition.Name)?.Record.Id;
                return parked is { } found && (await next.Harness.GetTaskAsync(found, Context))?.State.Status == DurableTaskStatus.Sleeping;
            });
            // With the task waiting, the next process resumes the wait without asking.
            await next.Harness.CloseAsync(Context);
            var third = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            third.Harness.Resume();
            await third.Harness.SignalAsync(parked!.Value, Go, "go", JsonObjectValue.Empty, Context);
            Equal(SubmissionStatus.Done, (await (await third.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "settled");
            Equal(2, Volatile.Read(ref asked), "asked");
            await third.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "recovery.settles-a-yields-inputs-done-with-its-answer-at-the-restart-limit-without-a-run-failure", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            InstallYieldDeciders(setup, ParkingDecider(new YieldDecision("never").ToJson()));
            setup.Faux.SetResponses(FauxAssistantMessage("first"));
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            opened.Harness.Resume();
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            EntryId? answer = null;
            await Eventually(async () =>
            {
                answer = EntryOf(Field(await LiveOf(opened), "run", "yield", "answer"));
                return answer is not null;
            });
            var generation = new TaskId((long)N(Field(await LiveOf(opened), "run", "taskId")));
            await opened.Harness.CloseAsync(Context);
            // Cut off in `yield` as often as the limit allows: as a dead process leaves it, `running` with its count.
            IStorage storage = await SqliteStorage.OpenAsync(path);
            var record = (await storage.GetTaskAsync(generation, Context))!;
            var running = record with { State = new RunningTaskState(((LiveTaskState)record.State).Checkpoint), Restarts = 5 };
            await storage.CommitWritesAsync([new TaskWrite(running)], Context);
            opened = await OpenChat(storage, setup);
            opened.Harness.Resume();
            var settled = await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context);
            Equal(answer, AnswerOf(settled), "answer");
            var live = await LiveOf(opened);
            Equal<JsonValue?>(null, Field(live, "run"), "run");
            Equal<JsonValue?>(null, Field(live, "runFailure"), "runFailure");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "recovery.settles-a-yields-inputs-done-with-its-answer-when-the-scheduler-ends-the-generation", async () =>
        {
            var setup = Setup();
            InstallYieldDeciders(setup, ParkingDecider(new YieldDecision("never").ToJson()));
            setup.Faux.SetResponses(FauxAssistantMessage("first"));
            var opened = await Open(setup);
            var submission = await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Eventually(() => Yielded(opened));
            var live = (await LiveOf(opened))!;
            var generation = (await opened.Harness.GetTaskAsync(new TaskId((long)N(Field(live, "run", "taskId"))), Context))!;
            // The Harness cleanup the scheduler stages with a `faulted` outcome it writes.
            await opened.Harness.CommitAsync(async tx =>
            {
                await Live.SettleSchedulerOutcomeAsync(tx, generation, new FaultedTaskOutcome(new TaskOutcomeError("broken")));
            }, Context);
            var settled = await submission.WaitAsync(Context);
            Equal(EntryOf(Field(live, "run", "yield", "answer")), AnswerOf(settled), "answer");
            Equal<JsonValue?>(null, Field(await LiveOf(opened), "runFailure"), "runFailure");
            await opened.Harness.CloseAsync(Context);
        });
    }
}
