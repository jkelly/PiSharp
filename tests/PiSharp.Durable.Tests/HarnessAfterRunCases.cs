// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-after-run.test.ts (added in 7e15f9601, #11141), ported
// in the Pi v1.5.0 sync (area run-end). GenerationHooks.AfterRun: once a run answered, the tasks its hooks ask for run as the generation's
// children, the run busy until they end; and Live.EndRun's two unit cases. describe("afterRun") is harness.after-run.* and
// describe("endRun") harness.after-run.end-run.*, on MemoryStorage and a SQLite file as upstream; the store pass (StoreStorageNode.cs) reruns
// them as harness.after-run.store.*, opening every file as a StoreStorage and running the MemoryStorage cases on a fresh StoreStorage, as
// upstream's vitest.store.config.ts lists the file. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - openKillable opens the file behind a KillableStorage (HarnessRestartFailureCases.cs), upstream's Proxy whose calls hang once dead; the
//   killed storages are closed after the case (KilledStorages), as upstream's afterEach closes them. openNodeSqliteStorage is the openFile
//   the group takes; sqlitePath is session.sqlite in a fresh temporary directory.
// - The probe's lists are SyncLists, and its gates and admit are read when the task or hook runs, as upstream's mutable fields are.
// - afterRun returns the requests themselves, null for none (GenerationHooks.AfterRun), where upstream returns { tasks }.
// - "reports broken requests and hooks and creates the rest": a C# JsonValue is strict JSON, so upstream's input holding a function cannot
//   be built. A request with a null input stands in for it, rejected with its own message ("The afterRun request for task test.run-tests
//   has no input; expected strict JSON", where upstream's copyJson says "Value contains a non-JSON function; expected strict JSON"). The
//   not-a-list hook returns a list holding null, as a C# list cannot be a string: a request without a task. AggregateError is
//   AggregateException, its errors InnerExceptions. Infos and Reports are ChatSetup's, at the same index.
// - "lets a hook read the run's history, tool results included" records the answer's kind for the case to check after the run, where
//   upstream expects inside the hook (a failing expect there would be reported, not fail the case).
// - `new Promise(() => {})` is a task that never completes; `setup.settings.restartLimit = undefined` is ChatSetup.RestartLimit = null.
// - The endRun cases call Live.EndRun on the root's pi.live draft inside a Harness commit, where upstream passes a plain object and a stub
//   tx: their runs have no inputs, so EndRun settles nothing. They read the draft's value in that commit.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessAfterRunCases() =>
        AfterRunTests.Cases("harness.after-run.", () => new MemoryStorage(), async path => await SqliteStorage.OpenAsync(path));
}

/// <summary>The harness.after-run.* cases (upstream harness-after-run.test.ts).</summary>
internal static class AfterRunTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-after-run"), "session.sqlite");

    private sealed record Opened(IHarness Harness, IConversation Root, Action Kill);

    /// <summary>Opens a chat over the file at <paramref name="path"/>; <c>Kill</c> simulates a crash: every later storage call hangs, so
    /// nothing more lands (upstream <c>openKillable</c>).</summary>
    private static async Task<Opened> OpenKillable(Func<string, Task<IStorage>> openFile, KilledStorages killed, string path, ChatSetup setup)
    {
        var (storage, kill) = killed.Killable(await openFile(path));
        var (harness, root) = await OpenChat(storage, setup);
        return new Opened(harness, root, kill);
    }

    /// <summary>What the after-run task and hook of a case did, and how they behave (upstream <c>Probe</c>).</summary>
    private sealed class Probe
    {
        private int aborted;

        /// <summary>Labels of the after-run task's runs, in order, reruns included.</summary>
        public SyncList<string> Runs { get; } = new();
        /// <summary>Summaries the afterRun hook was asked with.</summary>
        public SyncList<RunSummary> Asked { get; } = new();
        public int AbortCount => Volatile.Read(ref aborted);
        public void Aborted() => Interlocked.Increment(ref aborted);
        /// <summary>Holds every run of the after-run task until resolved.</summary>
        public volatile Deferred? Gate;
        /// <summary>Holds the hook until resolved.</summary>
        public volatile Deferred? HookGate;
        /// <summary>Admitted in the after-run task's commit, before its own entry.</summary>
        public volatile Func<IConversationHandle, ITx, Task>? Admit;
    }

    /// <summary>The after-run task: records its run, waits for the gate, then appends a <c>test.tests</c> entry and completes (upstream
    /// <c>testsTask</c>).</summary>
    private static DurableTask TestsTask(Probe state) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.run-tests",
        Version = 1,
        Initial = _ => Obj(("phase", "run")),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (task, runtime, context) =>
            {
                var label = S(((JsonObjectValue)task.Input)["label"]);
                state.Runs.Add(label);
                if (state.Gate is { } gate) await await Task.WhenAny(gate.Task, HarnessTests.Aborted(runtime.Signal));
                var admit = state.Admit;
                var handle = admit is null ? null : await runtime.ConversationAsync(runtime.ConversationId, context);
                await runtime.CommitAsync(async (tx, _) =>
                {
                    if (admit is not null) await admit(handle!, tx);
                    await tx.AppendEntryAsync(runtime.ConversationId, new EntryDraft { Kind = "test.tests", Data = label });
                    return (TaskState?)Completed(label);
                }, context);
            },
        },
        Abort = async (_, runtime, context) =>
        {
            state.Aborted();
            await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted(), context);
        },
    });

    private delegate Task<IReadOnlyList<RunEndTask>?> AfterRunHook(RunSummary run, IHookApi api, DurableContext context);

    /// <summary>Installs the after-run extension: its task and an afterRun hook, by default asking for one task per run. Returns the task
    /// (upstream <c>install</c>).</summary>
    private static DurableTask Install(ChatSetup setup, Probe state, AfterRunHook? afterRun = null)
    {
        var tests = TestsTask(state);
        AfterRunHook handler = afterRun ?? (async (run, _, _) =>
        {
            state.Asked.Add(run);
            if (state.HookGate is { } gate) await gate.Task;
            return [Define.RunEndTask(tests, Obj(("label", $"run {state.Asked.Count}")))];
        });
        setup.Registry.Install(Define.Extension(new Extension
        {
            Name = "after-run",
            Tasks = [tests],
            Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks { AfterRun = (run, api, context) => handler(run, api, context) })],
        }));
        return tests;
    }

    private static readonly JsonObjectValue Parameters = ToolsSchema150.EmptyObject;

    private static ToolExecutionResult Output(string text) => new() { Output = JsonValue.Array(Obj(("type", "text"), ("text", text))) };

    private static ToolRegistration Tool(string name, string description, ToolExecute execute) =>
        Define.Tool(new ToolRegistration { Name = name, Description = description, Parameters = Parameters, Execute = execute });

    /// <summary>The tools extension: edit, finish (terminates) and handoff (terminates in a new context).</summary>
    private static Extension Tools() => Define.Extension(new Extension
    {
        Name = "tools",
        Tools =
        [
            Tool("edit", "Edit a file", (_, _, _) => Task.FromResult(Output("edited"))),
            Tool("finish", "End the run", (_, _, _) => Task.FromResult(Output("finished") with { Control = new ToolControl { Terminate = true } })),
            Tool("handoff", "End the run in a new context",
                (_, _, _) => Task.FromResult(Output("handed off") with { Control = new ToolControl { Terminate = true, Handoff = "fresh start" } })),
        ],
    });

    private static JsonObjectValue Call(string name, string id) => FauxAssistantMessage([FauxToolCall(name, JsonObjectValue.Empty, id)], stopReason: "toolUse");

    private static List<EntryRecord> KindOf(IEnumerable<EntryRecord> entries, string kind) => [.. entries.Where(entry => entry.Kind == kind)];

    /// <summary>Texts of the user messages of the entries, in order (upstream <c>userTexts</c>).</summary>
    private static List<string> UserTexts(IEnumerable<EntryRecord> entries) =>
    [
        .. entries.SelectMany(entry => (IEnumerable<JsonValue>?)entry.Model ?? [])
            .Where(message => message is JsonObjectValue value && value["role"] is JsonStringValue { Value: "user" })
            .Select(message => TextOf(message) ?? ""),
    ];

    private static List<string> KindsBut(IEnumerable<EntryRecord> entries, string skipped) => [.. entries.Where(entry => entry.Kind != skipped).Select(entry => entry.Kind)];

    private static async Task<JsonObjectValue> LiveOf(IHarness harness, ConversationId id) =>
        await harness.SnapshotAsync(Live.LiveDoc, id, Context) ?? JsonObjectValue.Empty;

    private static JsonValue? Field(JsonValue? value, params string[] path)
    {
        foreach (var key in path) value = value is JsonObjectValue fields && fields.TryGetValue(key, out var found) ? found : null;
        return value;
    }

    private static TaskId Id(JsonValue? value) => new((long)N(value!));

    private static List<TaskId> AfterRunTasks(JsonObjectValue live) => [.. ((JsonArrayValue)Field(live, "run", "afterRun", "tasks")!).Select(id => Id(id))];

    private static void Asked(Probe state, params RunSummary[] expected) => Seq(expected, state.Asked.Snapshot(), "asked");

    private static RunSummary Answered(EntryId first, EntryId answer, EntryId? last = null) => new(RunSummaryReason.Answer, first, answer, last ?? answer);

    private static bool Has(SyncList<ReportInfo> infos, ReportKind kind, ReportReason reason) => infos.Snapshot().Any(info => info.Kind == kind && info.Reason == reason);

    /// <param name="prefix">The case ID prefix.</param>
    /// <param name="memory">Upstream's <c>new MemoryStorage()</c>: a fresh storage for each chat.</param>
    /// <param name="openFile">Upstream's openNodeSqliteStorage: opens the storage over the file at a path.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> memory, Func<string, Task<IStorage>> openFile)
    {
        // #11141
        yield return Case(prefix + "waits-for-the-tasks-an-answer-s-hooks-ask-for-inputs-done-at-the-answer-the-run-busy-until-they-end", async () =>
        {
            var setup = Setup();
            var state = new Probe { Gate = new Deferred() };
            Install(setup, state);
            setup.Registry.Install(Tools());
            setup.Faux.SetResponses(Call("edit", "a"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            // The input settles with the answer, before the task finishes.
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "settled");
            await Eventually(() => state.Runs.Count == 1);
            var live = await LiveOf(harness, root.Id);
            SameJson("[]", Field(live, "run", "inputs"), "run inputs");
            Equal(1, AfterRunTasks(live).Count, "after-run tasks");
            var generation = Id(Field(live, "run", "taskId"));
            var child = await harness.GetTaskAsync(AfterRunTasks(live)[0], Context);
            Equal<TaskId?>(generation, child?.Owner, "owner");
            Equal(false, child?.Background, "background");
            var idle = root.WaitForIdleAsync(Context);
            Equal(false, await Settled(idle), "idle while the task runs");

            var entries = await AllEntries(root);
            var answer = KindOf(entries, "pi.assistant")[^1];
            Asked(state, Answered(KindOf(entries, "pi.user")[0].Id, answer.Id));
            state.Gate!.Resolve();
            await idle;
            Equal<JsonValue?>(null, Field(await LiveOf(harness, root.Id), "run"), "run after the tasks");
            Equal<JsonValue?>(null, Field(await LiveOf(harness, root.Id), "runFailure"), "runFailure");
            Equal("test.tests", (await AllEntries(root))[^1].Kind, "last entry");
            MatchJson("""{"status":"terminal","outcome":{"status":"completed","result":{"entryId":""" + answer.Id.Value + "}}}",
                (await harness.GetTaskAsync(generation, Context))?.State.ToJson(), "generation");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "asks-after-a-terminate-round-the-run-s-last-entry-its-newest-result-or-its-handoff-s-reset", async () =>
        {
            foreach (var (tool, lastKind) in new[] { ("finish", "pi.tool-result"), ("handoff", "pi.reset") })
            {
                var setup = Setup();
                var state = new Probe();
                Install(setup, state);
                setup.Registry.Install(Tools());
                setup.Faux.SetResponses(Call(tool, "a"));
                var (harness, root) = await OpenChat(memory(), setup);
                await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                await root.WaitForIdleAsync(Context);
                var entries = await AllEntries(root);
                var assistant = KindOf(entries, "pi.assistant")[^1];
                Asked(state, new RunSummary(RunSummaryReason.Terminate, KindOf(entries, "pi.user")[0].Id, assistant.Id, KindOf(entries, lastKind)[^1].Id));
                Seq(["run 1"], state.Runs.Snapshot(), $"{tool} runs");
                Equal<JsonValue?>(null, Field(await LiveOf(harness, root.Id), "run"), $"{tool} run");
                await harness.CloseAsync(Context);
            }
        });

        // #11141
        yield return Case(prefix + "ends-the-run-in-one-commit-without-an-afterrun-hook-and-after-the-hook-when-it-asks-for-nothing", async () =>
        {
            var plain = Setup();
            var state = new Probe();
            plain.Registry.Install(Define.Extension(new Extension { Name = "tasks-only", Tasks = [TestsTask(state)] }));
            plain.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenChat(memory(), plain);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            // No hook: the answer's commit ended the run.
            SameJson("{}", await LiveOf(opened.Harness, opened.Root.Id), "live without a hook");
            await opened.Harness.CloseAsync(Context);

            var setup = Setup();
            Install(setup, state, (run, _, _) =>
            {
                state.Asked.Add(run);
                return Task.FromResult<IReadOnlyList<RunEndTask>?>(null);
            });
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            Equal(1, state.Asked.Count, "asked");
            Seq([], state.Runs.Snapshot(), "runs");
            SameJson("{}", await LiveOf(harness, root.Id), "live");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "queues-input-during-the-tasks-and-places-it-after-them-writes-first-then-steers-and-follow-ups", async () =>
        {
            var setup = Setup();
            var state = new Probe { Gate = new Deferred() };
            var tests = TestsTask(state);
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "after-run",
                Tasks = [tests],
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = (run, _, _) =>
                    {
                        state.Asked.Add(run);
                        // Only the first run gets a task.
                        return Task.FromResult<IReadOnlyList<RunEndTask>?>(state.Asked.Count == 1 ? [Define.RunEndTask(tests, Obj(("label", "run 1")))] : null);
                    },
                })],
            }));
            // The task admits its result for the model in its own commit.
            state.Admit = (handle, tx) => handle.SubmitAsync(tx, SubmissionDraft.Write(new EntryDraft
            {
                Kind = "test.result",
                Model = JsonValue.Array(Obj(("role", "user"), ("content", "tests passed"), ("timestamp", 1))),
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("first"), FauxAssistantMessage("second"), FauxAssistantMessage("third"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Runs.Count == 1);

            var steer = await root.SubmitAsync(SubmissionDraft.Input("steer", WhenBusy.Steer), Context);
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("later"), Context);
            await ThrowsAsync<ConversationBusyException>(() => root.SubmitAsync(SubmissionDraft.Input("x", WhenBusy.Reject), Context), "reject");
            await ThrowsAsync<ConversationBusyException>(() => root.SubmitAsync(SubmissionDraft.Continue(), Context), "continue");
            // Busy, so not idle: it queues and starts the next run after the tasks.
            var idleSteer = await root.SubmitAsync(SubmissionDraft.Input("steer 2", WhenBusy.Steer, whenIdle: WhenIdle.Reject), Context);
            var note = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "test.note" }), Context);
            Equal(SubmissionStatus.Done, (await note.StatusAsync(Context)).Status, "note");
            Equal(SubmissionStatus.Queued, (await steer.StatusAsync(Context)).Status, "steer");
            Equal(3, ((JsonArrayValue)(await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Context))!["items"]).Count, "inbox items");

            state.Gate!.Resolve();
            await root.WaitForIdleAsync(Context);
            foreach (var submission in new[] { steer, followUp, idleSteer })
                Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, $"submission {submission.Id.Value}");
            var entries = await AllEntries(root);
            Seq(["pi.user", "pi.assistant", "test.note", "test.tests", "test.result", "pi.user", "pi.user", "pi.assistant", "pi.user", "pi.assistant"],
                KindsBut(entries, "pi.system"), "kinds");
            Seq(["go", "tests passed", "steer", "later", "steer 2"], UserTexts(entries), "user texts");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "places-a-reset-queued-during-the-tasks-after-them", async () =>
        {
            var setup = Setup();
            var state = new Probe { Gate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Runs.Count == 1);
            await root.ResetAsync(null, Context);
            Equal(0, KindOf(await AllEntries(root), "pi.reset").Count, "resets while the tasks run");
            state.Gate!.Resolve();
            await root.WaitForIdleAsync(Context);
            Seq(["test.tests", "pi.reset"], (await Kinds(root)).TakeLast(2), "last kinds");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "asks-once-a-run-really-ends-not-while-onyield-continues-it-and-when-a-queued-steer-drops-the-continuation", async () =>
        {
            var setup = Setup();
            var state = new Probe();
            Install(setup, state);
            var yields = 0;
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "again",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    OnYield = (_, _, _) => Task.FromResult<JsonValue?>(Interlocked.Increment(ref yields) <= 2 ? (JsonValue)"again" : null),
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("1"), FauxAssistantMessage("2"), FauxAssistantMessage("3"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            var entries = await AllEntries(root);
            Asked(state, Answered(KindOf(entries, "pi.user")[0].Id, KindOf(entries, "pi.assistant")[^1].Id));
            await harness.CloseAsync(Context);

            // A steer queued while the answer's hooks run: the continuation is dropped, and the run ends after its task.
            var dropped = Setup();
            var second = new Probe();
            Install(dropped, second);
            IConversation? root2 = null;
            dropped.Registry.Install(Define.Extension(new Extension
            {
                Name = "steering",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    OnYield = async (_, _, _) =>
                    {
                        if (second.Asked.Count > 0) return null;
                        await root2!.SubmitAsync(SubmissionDraft.Input("steer", WhenBusy.Steer), Context);
                        return "again";
                    },
                })],
            }));
            dropped.Faux.SetResponses(FauxAssistantMessage("1"), FauxAssistantMessage("2"));
            var opened = await OpenChat(memory(), dropped);
            root2 = opened.Root;
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await opened.Root.WaitForIdleAsync(Context);
            Equal(2, second.Asked.Count, "asked with a steer");
            Seq(["run 1", "run 2"], second.Runs.Snapshot(), "runs");
            Seq(["go", "steer"], UserTexts(await AllEntries(opened.Root)), "user texts");
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "asks-the-hooks-again-after-a-crash-before-the-tasks-exist-and-never-once-they-do", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var state = new Probe { HookGate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenKillable(openFile, killed, path, setup);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Asked.Count == 1);
            opened.Kill(); // a crash while the hook runs, in `finish`
            state.HookGate = null;
            state.Gate = new Deferred();
            opened = await OpenKillable(openFile, killed, path, setup);
            opened.Harness.Resume();
            await Eventually(() => state.Runs.Count == 1);
            Equal(2, state.Asked.Count, "asked after the first crash");
            opened.Kill(); // a crash while the task runs, in `drain`
            state.Gate = null;
            opened = await OpenKillable(openFile, killed, path, setup);
            await opened.Root.WaitForIdleAsync(Context);
            // The task ran again; the hook was not asked again, and only one task exists.
            Equal(2, state.Asked.Count, "asked after the second crash");
            Seq(["run 2", "run 2"], state.Runs.Snapshot(), "runs");
            Equal(1, KindOf(await AllEntries(opened.Root), "test.tests").Count, "test.tests entries");
            Equal(0, (await opened.Harness.InspectAsync(Context)).Tasks.Length, "live tasks");
            await opened.Harness.CloseAsync(Context);

            // An orderly close in `drain` resumes the same way.
            var closing = SqlitePath();
            var other = new Probe { Gate = new Deferred() };
            var setup2 = Setup();
            Install(setup2, other);
            setup2.Faux.SetResponses(FauxAssistantMessage("done"));
            var reopened = await OpenKillable(openFile, killed, closing, setup2);
            await (await reopened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => other.Runs.Count == 1);
            await reopened.Harness.CloseAsync(Context);
            other.Gate = null;
            reopened = await OpenKillable(openFile, killed, closing, setup2);
            await reopened.Root.WaitForIdleAsync(Context);
            Equal(1, other.Asked.Count, "asked across the close");
            Seq(["run 1", "run 1"], other.Runs.Snapshot(), "runs across the close");
            await reopened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "ends-an-answered-run-without-a-failure-when-stop-comes-while-the-hooks-run-or-the-tasks-run", async () =>
        {
            foreach (var stage in new[] { "finish", "drain" })
            {
                var setup = Setup();
                var state = new Probe { Gate = new Deferred() };
                var tests = TestsTask(state);
                setup.Registry.Install(Define.Extension(new Extension
                {
                    Name = "after-run",
                    Tasks = [tests],
                    Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                    {
                        AfterRun = async (run, _, context) =>
                        {
                            state.Asked.Add(run);
                            if (stage == "finish") await HarnessTests.Aborted(context.CancellationToken);
                            return [Define.RunEndTask(tests, Obj(("label", "run 1")))];
                        },
                    })],
                }));
                setup.Faux.SetResponses(FauxAssistantMessage("done"));
                var (harness, root) = await OpenChat(memory(), setup);
                var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
                await submission.WaitAsync(Context);
                await Eventually(() => stage == "finish" ? state.Asked.Count == 1 : state.Runs.Count == 1);
                await root.AbortAsync(Context);
                var live = await LiveOf(harness, root.Id);
                Equal<JsonValue?>(null, Field(live, "run"), $"{stage} run");
                Equal<JsonValue?>(null, Field(live, "runFailure"), $"{stage} runFailure");
                Equal(SubmissionStatus.Done, (await submission.StatusAsync(Context)).Status, $"{stage} submission");
                Equal(stage == "drain" ? 1 : 0, state.AbortCount, $"{stage} aborted");
                Seq(stage == "drain" ? ["run 1"] : [], state.Runs.Snapshot(), $"{stage} runs");
                await harness.CloseAsync(Context);
            }
        });

        // #11141
        yield return Case(prefix + "ends-the-run-normally-when-one-of-its-tasks-is-aborted-on-its-own", async () =>
        {
            var setup = Setup();
            var state = new Probe { Gate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Runs.Count == 1);
            var live = await LiveOf(harness, root.Id);
            await harness.AbortTaskAsync(AfterRunTasks(live)[0], Context);
            await root.WaitForIdleAsync(Context);
            Equal(1, state.AbortCount, "aborted");
            SameJson("{}", await LiveOf(harness, root.Id), "live");
            MatchJson("""{"status":"terminal","outcome":{"status":"completed"}}""",
                (await harness.GetTaskAsync(Id(Field(live, "run", "taskId")), Context))?.State.ToJson(), "generation");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "reports-broken-requests-and-hooks-and-creates-the-rest", async () =>
        {
            var setup = Setup();
            var state = new Probe();
            var tests = TestsTask(state);
            var throwing = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.throwing",
                Version = 1,
                Initial = _ => throw new InvalidOperationException("initial failed"),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            // Its Initial starts at a phase the definition does not have.
            var badStart = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.bad-start",
                Version = 1,
                Initial = _ => Obj(("phase", "nowhere")),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var unknown = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.unknown",
                Version = tests.Definition.Version,
                Initial = tests.Definition.Initial,
                Phases = tests.Definition.Phases,
                Abort = tests.Definition.Abort,
            });
            IReadOnlyList<RunEndTask> requests =
            [
                Define.RunEndTask(unknown, JsonValue.Null),
                Define.RunEndTask(HarnessTasks.GenerationTask, JsonObjectValue.Empty),
                // Upstream's input holding a function, which a JsonValue cannot (see the file header).
                new RunEndTask(tests, null!),
                Define.RunEndTask(throwing, JsonValue.Null),
                Define.RunEndTask(badStart, JsonValue.Null),
                Define.RunEndTask(tests, Obj(("label", "ok"))),
            ];
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "after-run",
                Tasks = [tests, throwing, badStart],
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks { AfterRun = (_, _, _) => Task.FromResult<IReadOnlyList<RunEndTask>?>(requests) })],
            }));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "broken",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = (_, _, _) => throw new InvalidOperationException("hook failed"),
                })],
            }));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "not-a-list",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = (_, _, _) => Task.FromResult<IReadOnlyList<RunEndTask>?>([null!]),
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            Seq(["ok"], state.Runs.Snapshot(), "runs");
            // One report per hook, against the hook's extension; the requests of one hook come together.
            var infos = setup.Infos.Snapshot();
            Seq(["after-run", "broken", "not-a-list"], infos.Where(info => info.Kind == ReportKind.ExtensionError).Select(info => info.Extension), "extension errors");
            var reports = setup.Reports.Snapshot();
            var rejected = (AggregateException)reports[0];
            Seq([
                "Task test.unknown is not installed",
                "An afterRun hook cannot start the built-in task pi.generation",
                "The afterRun request for task test.run-tests has no input; expected strict JSON",
                "initial failed",
                "Task test.bad-start starts at the unknown phase nowhere",
            ], rejected.InnerExceptions.Select(error => error.Message), "rejections");
            Equal("hook failed", reports[1].Message, "hook error");
            SameJson("{}", await LiveOf(harness, root.Id), "live");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "creates-a-requested-task-from-the-installed-definition-calling-its-initial-once-off-the-line", async () =>
        {
            var setup = Setup();
            var installedCalls = 0;
            var tokenCalls = 0;
            DurableTask Versioned(int version, Action count) => Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.versioned",
                Version = version,
                Initial = _ =>
                {
                    count();
                    return Obj(("phase", "run"));
                },
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = (_, runtime, context) => runtime.CommitAsync((_, _) => (TaskState?)Completed(null), context),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var installed = Versioned(2, () => Interlocked.Increment(ref installedCalls));
            // The hook's extension holds an older copy of the task.
            var stale = Versioned(1, () => Interlocked.Increment(ref tokenCalls));
            setup.Registry.Install(Define.Extension(new Extension { Name = "tasks", Tasks = [installed] }));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "asker",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = (_, _, _) => Task.FromResult<IReadOnlyList<RunEndTask>?>([Define.RunEndTask(stale, JsonValue.Null)]),
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            var created = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id, Kind = "test.versioned" }, 10), Context);
            Equal(1, created.Items.Length, "created");
            Equal(2, created.Items[0].Version, "version");
            MatchJson("""{"status":"terminal"}""", created.Items[0].State.ToJson(), "state");
            Equal(1, Volatile.Read(ref installedCalls), "installed initial calls");
            Equal(0, Volatile.Read(ref tokenCalls), "token initial calls");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "lets-a-hook-read-the-run-s-history-tool-results-included", async () =>
        {
            var setup = Setup();
            var kinds = new SyncList<string>();
            string? answerKind = null;
            setup.Registry.Install(Tools());
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "reader",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = async (run, api, context) =>
                    {
                        var page = await api.EntriesAsync(new EntryQueryBounds { MinEntryId = run.First, MaxEntryId = run.Last, Order = ScanOrder.Ascending },
                            100, null, context);
                        foreach (var entry in page.Items) kinds.Add(entry.Kind);
                        answerKind = (await api.EntryAsync(run.Answer, context))?.Kind;
                        return null;
                    },
                })],
            }));
            setup.Faux.SetResponses(Call("edit", "a"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            Equal("pi.assistant", answerKind, "the answer's kind");
            Seq(["pi.user", "pi.assistant", "pi.tool-result", "pi.assistant"], kinds.Snapshot().Where(kind => kind != "pi.system"), "kinds");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "lets-a-hook-read-the-run-s-history-after-a-compaction-summarized-the-start-of-the-run", async () =>
        {
            var setup = Setup();
            setup.Compaction = new CompactionSettings { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 1, BackgroundTokens = 0 };
            var gate = new Deferred();
            var started = new Deferred();
            var runs = new SyncList<List<string>>();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "slow",
                Tools =
                [
                    Tool("edit", "Edit a file", async (_, _, _) =>
                    {
                        started.Resolve();
                        await gate.Task;
                        return Output("edited");
                    }),
                ],
                Hooks =
                [
                    Define.Hook(HarnessTasks.CompactionTask, new CompactionHooks
                    {
                        BeforeCompact = (_, _, _) => Task.FromResult<CompactionDecision?>(CompactionDecision.FromSummary("earlier work")),
                    }),
                    Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                    {
                        AfterRun = async (run, api, context) =>
                        {
                            var page = await api.EntriesAsync(new EntryQueryBounds { MinEntryId = run.First, MaxEntryId = run.Last, Order = ScanOrder.Ascending },
                                100, null, context);
                            runs.Add([.. page.Items.Select(entry => entry.Kind)]);
                            return null;
                        },
                    }),
                ],
            }));
            setup.Faux.SetResponses(Call("edit", "a"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            // Its summary is placed at the round's post-tools boundary, in the middle of the run.
            var compaction = await root.CompactAsync(null, Context);
            await harness.WaitForTaskAsync(compaction, Context);
            gate.Resolve();
            await submission.WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            var active = (await root.ContextAsync(Context)).Entries.Select(entry => entry.Kind).ToList();
            // The active context lost the run's input to the summary; the run's history did not.
            Check(!active.Contains("pi.user"), $"active context: [{string.Join(", ", active)}] holds pi.user");
            Equal(1, runs.Count, "asked");
            Seq(["pi.user", "pi.assistant", "pi.tool-result", "pi.compaction", "pi.assistant"], runs[0].Where(kind => kind != "pi.system"), "kinds");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "ends-an-answered-run-completed-without-a-failure-once-finish-was-cut-off-restartlimit-times", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.RestartLimit = 1;
            var state = new Probe { HookGate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenKillable(openFile, killed, path, setup);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Asked.Count == 1);
            await opened.Root.SubmitAsync(SubmissionDraft.Input("later"), Context);
            opened.Kill();
            opened = await OpenKillable(openFile, killed, path, setup);
            await opened.Root.WaitForIdleAsync(Context);
            Equal(1, state.Asked.Count, "asked");
            var live = await LiveOf(opened.Harness, opened.Root.Id);
            Equal<JsonValue?>(null, Field(live, "run"), "run");
            Equal<JsonValue?>(null, Field(live, "runFailure"), "runFailure");
            // Queued items stay, as after a failed run.
            Equal(1, ((JsonArrayValue)(await opened.Harness.SnapshotAsync(Inbox.InboxDoc, opened.Root.Id, Context))!["items"]).Count, "inbox items");
            Check(Has(setup.Infos, ReportKind.TaskInterrupted, ReportReason.RestartLimit), "a task_interrupted report with reason restart_limit");
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "keeps-the-run-busy-while-an-after-run-task-cannot-run-until-stop-orphans-it", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var state = new Probe { Gate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenKillable(openFile, killed, path, setup);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Runs.Count == 1);
            opened.Kill();
            // Reopened without the extension: its task is blocked.
            var bare = Setup();
            opened = await OpenKillable(openFile, killed, path, bare);
            opened.Harness.Resume();
            var live = await LiveOf(opened.Harness, opened.Root.Id);
            Equal(1, AfterRunTasks(live).Count, "after-run tasks");
            var harness = opened.Harness;
            await Eventually(async () => (await harness.InspectAsync(Context)).Tasks.Any(task => task.State.Kind == TaskInspectionKind.Blocked));
            Equal(false, await Settled(opened.Root.WaitForIdleAsync(Context)), "idle while blocked");
            await opened.Root.AbortAsync(Context);
            var after = await LiveOf(opened.Harness, opened.Root.Id);
            Equal<JsonValue?>(null, Field(after, "run"), "run");
            Equal<JsonValue?>(null, Field(after, "runFailure"), "runFailure");
            MatchJson("""{"status":"terminal","outcome":{"status":"orphaned"}}""",
                (await opened.Harness.GetTaskAsync(AfterRunTasks(live)[0], Context))?.State.ToJson(), "after-run task");
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "starts-a-fix-run-with-a-follow-up-a-task-admits-after-the-tasks", async () =>
        {
            var setup = Setup();
            var state = new Probe();
            var tests = TestsTask(state);
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "after-run",
                Tasks = [tests],
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = (run, _, _) =>
                    {
                        state.Asked.Add(run);
                        return Task.FromResult<IReadOnlyList<RunEndTask>?>(state.Asked.Count == 1 ? [Define.RunEndTask(tests, Obj(("label", "run 1")))] : null);
                    },
                })],
            }));
            state.Admit = (handle, tx) => handle.SubmitAsync(tx, SubmissionDraft.Input("fix the tests", WhenBusy.FollowUp, "fix-1"));
            setup.Faux.SetResponses(FauxAssistantMessage("done"), FauxAssistantMessage("fixed"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            var entries = await AllEntries(root);
            Seq(["pi.user", "pi.assistant", "test.tests", "pi.user", "pi.assistant"], KindsBut(entries, "pi.system"), "kinds");
            Seq(["go", "fix the tests"], UserTexts(entries), "user texts");
            Equal(2, state.Asked.Count, "asked");
            // The fix run's history starts with its own input.
            Equal(KindOf(entries, "pi.user")[1].Id, state.Asked[1].First, "fix run's first");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "gives-a-continue-after-a-failed-run-the-failed-run-s-history", async () =>
        {
            var setup = Setup();
            var state = new Probe();
            Install(setup, state);
            setup.Registry.Install(Tools());
            setup.Faux.SetResponses(Call("edit", "a"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await root.ConfigureAsync(new AgentChange { MaxTurns = 1 }, Context);
            var failed = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Unanswered, failed.Status, "failed");
            var user = KindOf(await AllEntries(root), "pi.user")[0];
            MatchJson($$"""{"reason":"turn_limit","first":{{user.Id.Value}}}""", Field(await LiveOf(harness, root.Id), "runFailure"), "runFailure");
            Asked(state);
            await harness.CommitAsync(tx => root.SubmitAsync(tx, SubmissionDraft.Continue()), Context);
            await root.WaitForIdleAsync(Context);
            var answer = KindOf(await AllEntries(root), "pi.assistant")[^1];
            Asked(state, Answered(user.Id, answer.Id));
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "ends-the-run-without-asking-when-the-hooks-are-deselected-before-finish-runs", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var state = new Probe { HookGate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenKillable(openFile, killed, path, setup);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Asked.Count == 1);
            opened.Kill();
            setup.Registry.Uninstall(new Extension { Name = "after-run" });
            opened = await OpenKillable(openFile, killed, path, setup);
            await opened.Root.WaitForIdleAsync(Context);
            Equal(1, state.Asked.Count, "asked");
            Seq([], state.Runs.Snapshot(), "runs");
            SameJson("{}", await LiveOf(opened.Harness, opened.Root.Id), "live");
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "starts-a-fork-of-a-conversation-running-its-after-run-tasks-idle", async () =>
        {
            var setup = Setup();
            var state = new Probe { Gate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Runs.Count == 1);
            await root.SubmitAsync(SubmissionDraft.Input("later"), Context);
            var answer = KindOf(await AllEntries(root), "pi.assistant")[^1];
            var fork = await root.ForkAsync(answer.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            Equal<JsonValue?>(null, Field(await LiveOf(harness, fork.Id), "run"), "fork run");
            var forkInbox = await harness.SnapshotAsync(Inbox.InboxDoc, fork.Id, Context);
            Equal(0, Field(forkInbox, "items") is JsonArrayValue items ? items.Count : 0, "fork inbox items");
            state.Gate!.Resolve();
            await root.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "gives-a-run-without-a-recorded-first-entry-its-answer-as-first", async () =>
        {
            var setup = Setup();
            var state = new Probe();
            Install(setup, state);
            IConversation? root = null;
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "legacy",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    // As a run started before `first` existed.
                    OnYield = async (_, _, _) =>
                    {
                        await root!.CommitAsync(async tx => (await tx.DocAsync(Live.LiveDoc, root.Id)).GetObject("run").Delete("first"), Context);
                        return null;
                    },
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenChat(memory(), setup);
            root = opened.Root;
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            var answer = KindOf(await AllEntries(root), "pi.assistant")[^1];
            Asked(state, Answered(answer.Id, answer.Id));
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "ends-the-run-normally-when-its-after-run-task-is-cut-off-restartlimit-times", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.RestartLimit = 1;
            var state = new Probe { Gate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenKillable(openFile, killed, path, setup);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Runs.Count == 1);
            var generation = Id(Field(await LiveOf(opened.Harness, opened.Root.Id), "run", "taskId"));
            // The task is cut off, and the waiting generation is not: the scheduler faults the task, and the run ends.
            opened.Kill();
            state.Gate = null;
            opened = await OpenKillable(openFile, killed, path, setup);
            await opened.Root.WaitForIdleAsync(Context);
            MatchJson("""{"status":"terminal","outcome":{"status":"completed"}}""", (await opened.Harness.GetTaskAsync(generation, Context))?.State.ToJson(),
                "generation");
            Equal<JsonValue?>(null, Field(await LiveOf(opened.Harness, opened.Root.Id), "runFailure"), "runFailure");
            Seq(["run 1"], state.Runs.Snapshot(), "runs");
            Check(Has(setup.Infos, ReportKind.TaskFaulted, ReportReason.RestartLimit), "a task_faulted report with reason restart_limit");
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "reports-no-task-interrupted-for-a-task-that-ends-normally-after-the-limit-was-lowered", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var started = 0;
            var worker = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.worker",
                Version = 1,
                Initial = _ => Obj(("phase", "run")),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, runtime, context) =>
                    {
                        // The run a crash cuts off.
                        if (Interlocked.Increment(ref started) == 1) await new TaskCompletionSource().Task;
                        // The host lowers the limit while the rerun runs.
                        setup.RestartLimit = 1;
                        await runtime.CommitAsync((_, _) => (TaskState?)Completed(null), context);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "worker", Tasks = [worker] }));
            var opened = await OpenKillable(openFile, killed, path, setup);
            var id = await opened.Root.CommitAsync(tx => tx.CreateTaskAsync(worker, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            opened.Harness.Resume();
            await Eventually(() => Volatile.Read(ref started) == 1);
            opened.Kill();
            opened = await OpenKillable(openFile, killed, path, setup);
            MatchJson("""{"status":"terminal","outcome":{"status":"completed"}}""", (await opened.Harness.WaitForTaskAsync(id, Context)).State.ToJson(), "worker");
            Equal(0, setup.Infos.Snapshot().Count(info => info.Kind == ReportKind.TaskInterrupted), "task_interrupted reports");
            setup.RestartLimit = null;
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "reports-no-task-interrupted-for-a-generation-that-ends-normally-after-the-limit-was-lowered-in-finish", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var state = new Probe { HookGate = new Deferred() };
            Install(setup, state);
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var opened = await OpenKillable(openFile, killed, path, setup);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Asked.Count == 1);
            opened.Kill(); // `finish` cut off once
            var rerun = new Deferred();
            state.HookGate = rerun;
            opened = await OpenKillable(openFile, killed, path, setup);
            opened.Harness.Resume();
            await Eventually(() => state.Asked.Count == 2);
            // The host lowers the limit while the hook runs; the phase had started under the old one and ends normally.
            setup.RestartLimit = 1;
            rerun.Resolve();
            await opened.Root.WaitForIdleAsync(Context);
            Seq(["run 2"], state.Runs.Snapshot(), "runs");
            Equal(0, setup.Infos.Snapshot().Count(info => info.Kind == ReportKind.TaskInterrupted), "task_interrupted reports");
            await opened.Harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "asks-after-a-terminate-round-at-the-turn-limit-terminate-wins", async () =>
        {
            var setup = Setup();
            var state = new Probe();
            Install(setup, state);
            setup.Registry.Install(Tools());
            setup.Faux.SetResponses(Call("finish", "a"));
            var (harness, root) = await OpenChat(memory(), setup);
            await root.ConfigureAsync(new AgentChange { MaxTurns = 1 }, Context);
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "settled");
            await root.WaitForIdleAsync(Context);
            Equal(1, state.Asked.Count, "asked");
            Equal(RunSummaryReason.Terminate, state.Asked[0].Reason, "reason");
            Seq(["run 1"], state.Runs.Snapshot(), "runs");
            Equal<JsonValue?>(null, Field(await LiveOf(harness, root.Id), "runFailure"), "runFailure");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "starts-a-continue-s-history-at-its-first-tool-round-when-nothing-failed-and-nothing-was-placed", async () =>
        {
            var setup = Setup();
            var kinds = new SyncList<List<string>>();
            setup.Registry.Install(Tools());
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "reader",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = async (run, api, context) =>
                    {
                        var range = new EntryQueryBounds { MinEntryId = run.First, MaxEntryId = run.Last, Order = ScanOrder.Ascending };
                        kinds.Add([.. (await api.EntriesAsync(range, 100, null, context)).Items.Select(entry => entry.Kind)]);
                        return null;
                    },
                })],
            }));
            setup.Faux.SetResponses(Call("edit", "a"), FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            // A host writes the user message itself, then continues.
            await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft
            {
                Kind = "app.prompt",
                Model = JsonValue.Array(Obj(("role", "user"), ("content", "go"), ("timestamp", 1))),
            }), Context);
            await (await root.SubmitAsync(SubmissionDraft.Continue(), Context)).WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            Equal(1, kinds.Count, "asked");
            Seq(["pi.assistant", "pi.tool-result", "pi.assistant"], kinds[0], "kinds");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "leaves-a-summary-queued-during-the-tasks-stale-behind-a-terminate-round-s-handoff", async () =>
        {
            var setup = Setup();
            setup.Compaction = new CompactionSettings { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 1, BackgroundTokens = 0 };
            var state = new Probe();
            Install(setup, state);
            var gate = new Deferred();
            var started = new Deferred();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "slow-handoff",
                Tools =
                [
                    Tool("handoff", "End the run in a new context", async (_, _, _) =>
                    {
                        started.Resolve();
                        await gate.Task;
                        return Output("handed off") with { Control = new ToolControl { Terminate = true, Handoff = "fresh start" } };
                    }),
                ],
                Hooks =
                [
                    Define.Hook(HarnessTasks.CompactionTask, new CompactionHooks
                    {
                        BeforeCompact = (_, _, _) => Task.FromResult<CompactionDecision?>(CompactionDecision.FromSummary("earlier work")),
                    }),
                ],
            }));
            setup.Faux.SetResponses(Call("handoff", "a"));
            var (harness, root) = await OpenChat(memory(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            var compaction = await root.CompactAsync(null, Context);
            await harness.WaitForTaskAsync(compaction, Context);
            gate.Resolve();
            await submission.WaitAsync(Context);
            await root.WaitForIdleAsync(Context);
            var entries = await AllEntries(root);
            // The handoff's reset landed with the answer; the summary, cutting before it, settled stale at the run's end.
            Equal(0, KindOf(entries, "pi.compaction").Count, "summaries");
            Seq(["pi.reset", "test.tests"], entries.Select(entry => entry.Kind).TakeLast(2), "last kinds");
            Equal(RunSummaryReason.Terminate, state.Asked[0].Reason, "reason");
            Equal(KindOf(entries, "pi.reset")[0].Id, state.Asked[0].Last, "last");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return KilledStorages.Case(prefix + "stores-the-answer-s-held-progress-with-commit-a-and-asks-again-after-an-orderly-close-in-finish", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup(new FauxOptions { TokensPerSecond = 200, MinTokenSize = 1, MaxTokenSize = 1 });
            setup.Progress = new ProgressSettings { PersistWithinMs = double.PositiveInfinity };
            var state = new Probe();
            var tests = TestsTask(state);
            Deferred? hookGate = new();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "after-run",
                Tasks = [tests],
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = async (run, _, context) =>
                    {
                        state.Asked.Add(run);
                        if (Volatile.Read(ref hookGate) is { } gate) await await Task.WhenAny(gate.Task, HarnessTests.Aborted(context.CancellationToken));
                        return [Define.RunEndTask(tests, Obj(("label", "run")))];
                    },
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage(new string('x', 40)));
            var opened = await OpenKillable(openFile, killed, path, setup);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await Eventually(() => state.Asked.Count == 1);
            opened.Kill();
            opened = await OpenKillable(openFile, killed, path, setup);
            var stored = await LiveOf(opened.Harness, opened.Root.Id);
            Equal<JsonValue?>(null, Field(stored, "generation"), "stored generation");
            SameJson("{}", Field(stored, "run", "afterRun"), "stored afterRun");
            Equal(new string('x', 40), TextOf(KindOf(await AllEntries(opened.Root), "pi.assistant")[0].Model![0]), "stored answer");
            opened.Harness.Resume();
            await Eventually(() => state.Asked.Count == 2);
            await opened.Harness.CloseAsync(Context);
            Volatile.Write(ref hookGate, null);
            opened = await OpenKillable(openFile, killed, path, setup);
            await opened.Root.WaitForIdleAsync(Context);
            Equal(3, state.Asked.Count, "asked");
            Seq(["run"], state.Runs.Snapshot(), "runs");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── endRun ─────────────────────────────────────────────────────────────

        // #11141
        yield return Case(prefix + "end-run.leaves-a-run-it-does-not-own-alone-and-ends-an-answered-run-without-a-failure", async () =>
        {
            var (harness, root) = await OpenChat(memory(), Setup());
            var (kept, ended) = await harness.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, root.Id);
                live.Set("run", O("""{"taskId":1,"inputs":[],"turns":0,"afterRun":{"tasks":[5]}}"""));
                Live.EndRun(tx, live, new TaskId(2), RunEnd.Unanswered(RunFailureReason.Faulted));
                var kept = Field(live.ToValue(), "run", "afterRun");
                Live.EndRun(tx, live, new TaskId(1), RunEnd.Unanswered(RunFailureReason.Aborted));
                return (kept, (JsonObjectValue)live.ToValue());
            }, Context);
            SameJson("""{"tasks":[5]}""", kept, "a run of another task");
            Equal<JsonValue?>(null, Field(ended, "run"), "run");
            Equal<JsonValue?>(null, Field(ended, "runFailure"), "runFailure");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "end-run.copies-a-failed-run-s-first-entry-into-runfailure", async () =>
        {
            var (harness, root) = await OpenChat(memory(), Setup());
            var ended = await harness.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, root.Id);
                live.Set("run", O("""{"taskId":1,"inputs":[],"turns":0,"first":7}"""));
                Live.EndRun(tx, live, new TaskId(1), RunEnd.Unanswered(RunFailureReason.ModelError));
                return (JsonObjectValue)live.ToValue();
            }, Context);
            MatchJson("""{"reason":"model_error","first":7}""", Field(ended, "runFailure"), "runFailure");
            await harness.CloseAsync(Context);
        });
    }
}
