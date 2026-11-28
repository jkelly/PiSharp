// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/harness-tool-parks.test.ts. Tools that park (spec suspendable, section 4): a
// sleep on signals or time, a wait on owned tasks, the parked slot, the run after the wake, invalid parks, cleanup through `cancel`,
// recovery, and wrappers. Verbatim apart from the adaptations in HarnessSupport.cs/ChatSupport.cs, and these:
// - pi-schema builders are their JSON Schema: the signal's obj({text: str()}) is a closed object with `text` required, and tool()'s
//   obj({}) is ToolsSchema150.EmptyObject.
// - A park is the result's Park (Harness/Types.cs): `{ sleep }` is ToolPark.Sleeping(...), `{ wait }` is ToolPark.Waiting(...), and the
//   malformed parks upstream builds with casts are results built directly (both Sleep and Wait; a park with Output).
// - A Run holds what one run saw as JSON text: `api.wake` is api.Wake's JSON, `api.checkpoint` api.Checkpoint's; undefined is null.
//   toEqual and toMatchObject of states, slots and results compare their JSON.
// - `harness.snapshot(LiveDoc, 1 as never, ...)` reads the root conversation's pi.live (ConversationId.Root); waitFor is Eventually;
//   `aborted(ctx.abortSignal!)` is Aborted(ctx.CancellationToken).
// - openNodeSqliteStorage is SqliteStorage.OpenAsync over a file in a fresh temporary directory.
// - `{ cwd: "/work" } as ExecutionEnv` is ToolTestEnv (ToolEnvSupport.cs), read only for its Cwd.
// - The two crash cases ("The process dies here: another one opens the same database"): upstream leaves the first Harness open while
//   another opens the same SQLite file. Here the first Harness fails (the internal Session.Fail, as harness.tools-recovery.* and
//   harness.deferred-progress.* crash), so nothing more of it reaches Storage, and it is not closed again at the end.
// - "runs in the run itself when the park's commit finds the call abort-marked": upstream asserts inside `cancel` that its context is
//   not aborted. A throw from Cancel is only reported, so the case records the context's state there and checks it after the run.
// - "parks a nested call ...": the caller keeps the nested result itself and the case reads its IsError and DurationMs;
//   `String(result.structuredOutput)` is the string value (StringOf).
// - Wrappers: `{ sleep: { ...returned.sleep } }` is a new ToolPark with a copy of the base's sleep; `{ ...inner }` is `inner with { }`;
//   the one park object a base returns to calls running at once is one result carrying one ToolPark; the park a wrapper kept is the
//   result its base returned in the earlier run (the Harness tells parks apart by reference, as upstream does).
// - A `cancel` that throws returns a faulted task, as upstream's async function rejects.
// Every case of the upstream file is ported.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160ToolParksCases() => ToolParkTests.Cases();
}

/// <summary>The sync160.tool-parks.* cases (upstream harness-tool-parks.test.ts).</summary>
internal static class ToolParkTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Upstream <c>Done</c>: <c>defineSignal({ name: "test.done", version: 1, schema: obj({ text: str() }) })</c>.</summary>
    private static readonly SignalToken Done = Signals.DefineSignal(new SignalDefinition
    {
        Name = "test.done",
        Version = 1,
        Schema = O("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}"""),
    });

    /// <summary>Upstream <c>DONE</c>: the answer that ends a run.</summary>
    private static readonly JsonObjectValue DoneAnswer = FauxAssistantMessage("done");

    /// <summary>The wake of a run woken by <c>Done</c> under the key <c>done</c>, as JSON text.</summary>
    private const string SignalWake = """{"reason":"signal","signal":"test.done","key":"done"}""";

    /// <summary>The checkpoint <c>"first"</c>, as JSON text.</summary>
    private const string FirstCheckpoint = "\"first\"";

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-parks"), "session.sqlite");

    /// <summary>A tool without parameters (upstream <c>tool</c>).</summary>
    private static ToolRegistration Tool(string name, ToolExecute execute, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration { Name = name, Description = name, Parameters = ToolsSchema150.EmptyObject, Execute = execute });
        return extra is null ? tool : extra(tool);
    }

    /// <summary>A tool-calling answer with one call (upstream <c>call</c>).</summary>
    private static JsonObjectValue Call(string name, string id = "c1") =>
        FauxAssistantMessage([FauxToolCall(name, JsonObjectValue.Empty, id)], stopReason: "toolUse");

    /// <summary>The tool result messages of <paramref name="entries"/> (upstream <c>results</c>).</summary>
    private static List<JsonObjectValue> Results(IEnumerable<EntryRecord> entries) =>
        [.. entries.Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0])];

    /// <summary>The first tool result message of a conversation (upstream <c>results(await allEntries(root))[0]</c>).</summary>
    private static async Task<JsonObjectValue> FirstResult(IConversation root)
    {
        var results = Results(await AllEntries(root));
        Check(results.Count > 0, "the conversation has no tool result entry");
        return results[0];
    }

    /// <summary>The text items of a result message, joined (upstream <c>text</c>).</summary>
    private static string Text(JsonObjectValue? message) => message is null ? "" :
        string.Join("|", ((JsonArrayValue)message["content"]).OfType<JsonObjectValue>().Select(item => S(item["type"]) == "text" ? S(item["text"]) : ""));

    private static void Contains(string text, string part, string what) =>
        Check(text.Contains(part, StringComparison.Ordinal), $"{what}: expected to contain <{part}>, actual <{text}>");

    /// <summary>JavaScript's <c>String(value)</c> of a nested call's structured output, when a string.</summary>
    private static string StringOf(JsonValue? value) => value switch
    {
        null => "undefined",
        JsonStringValue text => text.Value,
        _ => value.ToJson(),
    };

    /// <summary>The first tool slot of the root conversation (upstream <c>slotOf</c>).</summary>
    private static async Task<JsonObjectValue?> SlotOf(IHarness harness)
    {
        var live = await harness.SnapshotAsync(Live.LiveDoc, ConversationId.Root, Context);
        return live is not null && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots ? slots[0] as JsonObjectValue : null;
    }

    private static TaskId? TaskOf(JsonObjectValue? slot) =>
        slot is not null && slot.TryGetValue("taskId", out var taskId) ? new TaskId((long)N(taskId)) : (TaskId?)null;

    private static bool HasStatus(JsonObjectValue? slot, string status) =>
        slot is not null && slot.TryGetValue("status", out var value) && value is JsonStringValue text && text.Value == status;

    /// <summary>The tool task of the first slot, once the slot names it.</summary>
    private static async Task<TaskId> SlotTask(IHarness harness)
    {
        TaskId? id = null;
        await Eventually(async () =>
        {
            id = TaskOf(await SlotOf(harness));
            return id is not null;
        });
        return id!.Value;
    }

    /// <summary>The tool task of the first slot, once it is <paramref name="status"/> (upstream <c>parkedTask</c>).</summary>
    private static async Task<TaskId> ParkedTask(IHarness harness, DurableTaskStatus status = DurableTaskStatus.Sleeping)
    {
        TaskId? id = null;
        await Eventually(async () =>
        {
            id = TaskOf(await SlotOf(harness));
            return id is { } found && (await harness.GetTaskAsync(found, Context))?.State.Status == status;
        });
        return id!.Value;
    }

    /// <summary>What one run of a parking tool saw (upstream <c>Run</c>): its wake and its checkpoint as JSON text, null for undefined.</summary>
    private sealed record Run(string? Wake, string? Checkpoint);

    private static Run RunOf(IToolExecutionApi api) => new(api.Wake?.ToJson().ToJson(), api.Checkpoint?.ToJson());

    /// <summary>A tool that reports, then parks on <c>Done</c> until the key holds a value, and answers with it (upstream
    /// <c>signalTool</c>).</summary>
    private static ToolRegistration SignalTool(SyncList<Run> runs, Func<ToolRegistration, ToolRegistration>? extra = null) =>
        Tool("ci", async (_, api, ctx) =>
        {
            runs.Add(RunOf(api));
            var run = runs.Count;
            if (await api.ReadSignalAsync(Done, "done", ctx) is { Value: JsonObjectValue value })
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText(S(value["text"]))) };
            api.Output("started\n");
            api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, $"run {run}"));
            await api.DetailsAsync(Obj(("build", run)), ctx);
            return ToolPark.Sleeping(signals: [Done.Key("done")], checkpoint: Obj(("build", run)));
        }, extra);

    /// <summary>A <c>cancel</c> that records what it saw.</summary>
    private static ToolCancel Recording(SyncList<Run> cancels) => (_, api, _) =>
    {
        cancels.Add(RunOf(api));
        return Task.CompletedTask;
    };

    /// <summary>Opens the chat with scheduling enabled (upstream <c>open</c>).</summary>
    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage? storage = null,
        Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>? env = null)
    {
        var opened = await OpenChat(storage ?? new MemoryStorage(), setup, env);
        opened.Harness.Resume();
        return opened;
    }

    private static Task<ISubmission> Go(IConversation root) => root.SubmitAsync(SubmissionDraft.Input("go"), Context);

    private static async Task<SubmissionStatus> Settled(IHarness harness, SubmissionId id) =>
        (await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status;

    private static JsonObjectValue Said(string text) => Obj(("text", text));

    /// <summary>The process dies: the Harness fails, so nothing more of it reaches Storage (see the file header).</summary>
    private static async Task Crash(IHarness harness)
    {
        ((Session)harness).Fail(new InvalidOperationException("crash"));
        await harness.Closed;
    }

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "sync160.tool-parks.";

        // --- parking on a signal ---

        yield return Case(prefix + "parking-on-a-signal.holds-no-invocation-shows-the-slot-sleeping-without-output-and-runs-again-once-woken", async () =>
        {
            var setup = Setup();
            var runs = new SyncList<Run>();
            AddTool(setup.Registry, SignalTool(runs));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            var id = await ParkedTask(harness);
            SameJson("""{"status":"sleeping","checkpoint":{"phase":"parked","arguments":{},"replay":"unsafe","checkpoint":{"build":1}},"signals":[{"signal":"test.done","key":"done"}]}""",
                (await harness.GetTaskAsync(id, Context))?.State.ToJson(), "parked state");
            // Details and diagnostics stay, the output goes.
            var slot = await SlotOf(harness);
            MatchJson("""{"status":"sleeping","details":{"build":1},"diagnostics":[{"severity":"info","message":"run 1"}]}""", slot, "slot");
            Check(slot is not null && !slot.ContainsKey("output"), "the slot has no output");
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false), harness.Wake(), "wake");

            Equal(SignalResult.Delivered, await harness.SignalAsync(id, Done, "done", Said("passed"), Context), "delivered");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Seq([new Run(null, null), new Run(SignalWake, """{"build":1}""")], runs.Snapshot(), "runs");
            var result = await FirstResult(root);
            // The result keeps the details and diagnostics the parked run reported.
            MatchJson("""{"isError":false,"details":{"build":1}}""", result, "result");
            Equal("passed|<harness>\n[info] run 1\n</harness>", Text(result), "text");
            Check(N(result["durationMs"]) >= 0, $"durationMs {result["durationMs"].ToJson()}");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "parking-on-a-signal.parks-on-time-and-wakes-with-reason-time", async () =>
        {
            var setup = Setup();
            double clock = 1_000;
            setup.Now = () => Volatile.Read(ref clock);
            var wakes = new SyncList<string?>();
            AddTool(setup.Registry, Tool("poll", (_, api, _) =>
            {
                wakes.Add(api.Wake?.ToJson().ToJson());
                return Task.FromResult(api.Wake is null
                    ? (ToolExecutionResult)ToolPark.Sleeping(until: api.Now() + 500)
                    : new ToolExecutionResult { Output = JsonArrayValue.Empty });
            }));
            setup.Faux.SetResponses(Call("poll"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            await ParkedTask(harness);
            await Eventually(() => harness.Wake().At == 1_500 && !harness.Wake().Active);
            Volatile.Write(ref clock, 1_500);
            harness.Resume();
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Seq<string?>([null, """{"reason":"time"}"""], wakes.Snapshot(), "wakes");
            var result = await FirstResult(root);
            // The span from the call's first run to its result, on the Harness clock.
            Equal(500d, N(result["durationMs"]), "durationMs");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "parking-on-a-signal.stays-parked-across-reopen-running-no-tool-code-and-wakes-on-a-later-delivery", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var runs = new SyncList<Run>();
            AddTool(setup.Registry, SignalTool(runs));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var opened = await Open(setup, await SqliteStorage.OpenAsync(path));
            var submissionId = (await Go(opened.Root)).Id;
            var id = await ParkedTask(opened.Harness);
            await opened.Harness.CloseAsync(Context);
            opened = await Open(setup, await SqliteStorage.OpenAsync(path));
            await Eventually(() => !opened.Harness.Wake().Active);
            Equal(1, runs.Count, "runs before the delivery");
            await opened.Harness.SignalAsync(id, Done, "done", Said("later"), Context);
            Equal(SubmissionStatus.Done, await Settled(opened.Harness, submissionId), "status");
            Equal(2, runs.Count, "runs");
            await opened.Harness.CloseAsync(Context);
        });

        // --- parking nested calls ---

        yield return Case(prefix + "parking-nested-calls.parks-a-nested-call-shown-sleeping-while-its-caller-awaits-it-and-returns-its-result-once-woken", async () =>
        {
            var setup = Setup();
            // A clock that runs, as progress pacing needs, and jumps two seconds while the call is parked.
            long offset = 0;
            setup.Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Interlocked.Read(ref offset);
            var runs = new SyncList<Run>();
            AddTool(setup.Registry, SignalTool(runs));
            NestedToolExecutionResult? nested = null;
            AddTool(setup.Registry, Tool("outer", async (_, api, ctx) =>
            {
                var result = await api.ExecuteToolAsync("ci", JsonObjectValue.Empty, ctx, new NestedCallOptions { Key = "ci" });
                nested = result;
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText(StringOf(result.StructuredOutput))) };
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("outer"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            TaskId? inner = null;
            await Eventually(async () =>
            {
                var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
                var slot = live is not null && live.TryGetValue("nestedTools", out var nestedTools) && nestedTools is JsonArrayValue { Count: > 0 } slots
                    ? slots[0] as JsonObjectValue
                    : null;
                inner = TaskOf(slot);
                return HasStatus(slot, "sleeping");
            });
            Interlocked.Exchange(ref offset, 2_000);
            await harness.SignalAsync(inner!.Value, Done, "done", Said("nested passed"), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Check(nested is { IsError: false }, "the nested call's result is no error");
            Check(nested?.DurationMs >= 2_000, $"nested durationMs {nested?.DurationMs}");
            Equal("nested passed", Text(await FirstResult(root)), "text");
            Equal(2, runs.Count, "runs");
            await harness.CloseAsync(Context);
        });

        // --- parking on tasks ---

        yield return Case(prefix + "parking-on-tasks.waits-for-a-child-it-owns-with-policy-any-and-runs-again-with-the-ended-task-in-its-wake", async () =>
        {
            var setup = Setup();
            var childTask = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.child",
                Version = 1,
                Initial = _ => O("""{"phase":"sleep"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["sleep"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Sleeping(O("""{"phase":"done"}"""), null, [Done.Key("child")]), ctx),
                    ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed((JsonValue)"child done"), ctx),
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
            });
            AddTask(setup.Registry, childTask);
            TaskId? child = null;
            var wakes = new SyncList<string?>();
            AddTool(setup.Registry, Tool("spawn", async (_, api, ctx) =>
            {
                wakes.Add(api.Wake?.ToJson().ToJson());
                if (api.Wake is not null) return new ToolExecutionResult { Output = JsonValue.Array(FauxText("joined")) };
                var created = await api.CreateTaskAsync(childTask, JsonValue.Null, new TaskOptions(TaskOwnership.Task(api.TaskId)), ctx);
                child = created;
                return ToolPark.Waiting([created], JoinPolicy.Any);
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("spawn"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            await ParkedTask(harness, DurableTaskStatus.Waiting);
            Check(HasStatus(await SlotOf(harness), "waiting"), "the slot is waiting");
            await harness.SignalAsync(child!.Value, Done, "child", Said("go"), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Seq<string?>([null, $$"""{"reason":"tasks","ended":[{{child!.Value.Value}}]}"""], wakes.Snapshot(), "wakes");
            await harness.CloseAsync(Context);
        });

        // --- invalid parks ---

        yield return Case(prefix + "invalid-parks.ends-the-call-with-an-invalid-park-result-through-aftertool-and-fails-its-task", async () =>
        {
            var parks = new (string Name, ToolExecutionResult Returned)[]
            {
                ("unknown", new ToolPark { Kind = "other" }),
                ("result", ToolPark.Sleeping(until: 1) with { Output = JsonArrayValue.Empty }),
                ("empty", (ToolExecutionResult)ToolPark.Sleeping()),
                ("unowned", (ToolExecutionResult)ToolPark.Waiting([new TaskId(1)], JoinPolicy.Any)),
            };
            foreach (var (name, returned) in parks)
            {
                var setup = Setup();
                AddTool(setup.Registry, Tool(name, (_, _, _) => Task.FromResult(returned)));
                setup.Faux.SetResponses(Call(name), DoneAnswer);
                var (harness, root) = await Open(setup);
                var submission = await Go(root);
                Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, $"{name} status");
                var result = await FirstResult(root);
                Check(result["isError"] is JsonBooleanValue { Value: true }, $"{name} isError");
                Contains(Text(result), $"Tool {name} returned an invalid park", $"{name} text");
                Equal<TaskId?>(null, TaskOf(await SlotOf(harness)), $"{name} slot task");
                await harness.CloseAsync(Context);
            }
        });

        // --- invalid parks the commit refuses ---

        yield return Case(prefix + "invalid-parks-the-commit-refuses.ends-a-call-that-sleeps-again-on-the-key-that-woke-it-with-invalid-park", async () =>
        {
            var setup = Setup();
            var runs = 0;
            AddTool(setup.Registry, Tool("again", (_, _, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult<ToolExecutionResult>(ToolPark.Sleeping(signals: [Done.Key("k")]));
            }));
            setup.Faux.SetResponses(Call("again"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            var id = await ParkedTask(harness);
            await harness.SignalAsync(id, Done, "k", Said("x"), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Equal(2, Volatile.Read(ref runs), "runs");
            Contains(Text(await FirstResult(root)), "it already woke a wait", "text");
            MatchJson("""{"outcome":{"status":"failed"}}""", (await harness.GetTaskAsync(id, Context))?.State.ToJson(), "state");
            await harness.CloseAsync(Context);
        });

        // --- cancel ---

        yield return Case(prefix + "cancel.runs-when-a-parked-call-is-aborted-with-the-park-s-checkpoint-and-the-environment-and-the-call-ends-aborted", async () =>
        {
            var setup = Setup();
            var runs = new SyncList<Run>();
            var cancels = new SyncList<Run>();
            var envs = new SyncList<string?>();
            AddTool(setup.Registry, SignalTool(runs, tool => tool with
            {
                Cancel = (_, api, _) =>
                {
                    cancels.Add(RunOf(api));
                    envs.Add(api.Env?.Cwd);
                    return Task.CompletedTask;
                },
            }));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var (harness, root) = await Open(setup, new MemoryStorage(), (_, _) => Task.FromResult<IExecutionEnv?>(new ToolTestEnv("/work")));
            var submission = await Go(root);
            await ParkedTask(harness);
            await root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await submission.WaitAsync(Context)).Status, "status");
            Seq([new Run(null, """{"build":1}""")], cancels.Snapshot(), "cancels");
            Seq<string?>(["/work"], envs.Snapshot(), "envs");
            Contains(Text(await FirstResult(root)), "Tool ci was aborted", "text");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cancel.runs-also-under-restartlimit-1-and-a-throw-from-it-is-reported-while-the-call-still-ends-aborted", async () =>
        {
            var setup = Setup();
            setup.RestartLimit = 1;
            var cancels = 0;
            AddTool(setup.Registry, SignalTool(new SyncList<Run>(), tool => tool with
            {
                Cancel = (_, _, _) =>
                {
                    Interlocked.Increment(ref cancels);
                    return Task.FromException(new InvalidOperationException("cancel failed"));
                },
            }));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            var id = await ParkedTask(harness);
            await root.AbortAsync(Context);
            await submission.WaitAsync(Context);
            Equal(1, Volatile.Read(ref cancels), "cancels");
            MatchJson("""{"outcome":{"status":"aborted"}}""", (await harness.GetTaskAsync(id, Context))?.State.ToJson(), "state");
            Check(setup.Reports.Snapshot().Any(error => error.Message == "cancel failed"),
                "reports: " + string.Join("|", setup.Reports.Snapshot().Select(error => error.Message)));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cancel.runs-after-an-abort-that-cut-off-a-woken-run-which-cleans-up-through-its-context-too", async () =>
        {
            var setup = Setup();
            var cancels = new SyncList<Run>();
            var running = new Deferred();
            var cleaned = new SyncList<string>();
            AddTool(setup.Registry, Tool("ci", async (_, api, ctx) =>
            {
                if (api.Wake is null) return ToolPark.Sleeping(signals: [Done.Key("done")], checkpoint: "first");
                running.Resolve();
                try
                {
                    await Aborted(ctx.CancellationToken);
                }
                catch (Exception)
                {
                    cleaned.Add("execute");
                    throw;
                }
                return new ToolExecutionResult();
            }, tool => tool with { Cancel = Recording(cancels) }));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            var id = await ParkedTask(harness);
            await harness.SignalAsync(id, Done, "done", Said("x"), Context);
            await running.Task;
            await root.AbortAsync(Context);
            await submission.WaitAsync(Context);
            Seq(["execute"], cleaned.Snapshot(), "cleaned");
            Seq([new Run(null, FirstCheckpoint)], cancels.Snapshot(), "cancels");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cancel.does-not-run-for-a-parked-call-whose-tool-is-gone-the-abort-is-reported", async () =>
        {
            var setup = Setup();
            var cancels = new SyncList<Run>();
            var installed = AddTool(setup.Registry, SignalTool(new SyncList<Run>(), tool => tool with
            {
                Cancel = (_, api, _) =>
                {
                    cancels.Add(new Run(api.Wake?.ToJson().ToJson(), "null"));
                    return Task.CompletedTask;
                },
            }));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            await ParkedTask(harness);
            installed.Dispose();
            await root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await submission.WaitAsync(Context)).Status, "status");
            Seq([], cancels.Snapshot(), "cancels");
            Check(setup.Reports.Snapshot().Any(error => error.Message == "Tool ci is not available: its parked call c1 was not cancelled"),
                "reports: " + string.Join("|", setup.Reports.Snapshot().Select(error => error.Message)));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cancel.runs-in-the-run-itself-when-the-park-s-commit-finds-the-call-abort-marked", async () =>
        {
            var setup = Setup();
            var release = new Deferred();
            var entered = new Deferred();
            var cancels = new SyncList<Run>();
            var cancelAborted = new SyncList<bool>();
            AddTool(setup.Registry, Tool("race", async (_, _, _) =>
            {
                entered.Resolve();
                // The abort arrives while the tool still runs; it ignores the signal and parks anyway.
                await release.Task;
                return ToolPark.Sleeping(signals: [Done.Key("k")], checkpoint: "started");
            }, tool => tool with
            {
                Cancel = (_, api, ctx) =>
                {
                    cancels.Add(RunOf(api));
                    // Checked after the run (see the file header).
                    cancelAborted.Add(ctx.IsCancellationRequested);
                    return Task.CompletedTask;
                },
            }));
            setup.Faux.SetResponses(Call("race"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            await entered.Task;
            var id = await SlotTask(harness);
            var aborting = harness.AbortTaskAsync(id, Context);
            await Eventually(async () => (await harness.GetTaskAsync(id, Context))?.AbortRequested == true);
            release.Resolve();
            await aborting;
            await harness.WaitForTaskAsync(id, Context);
            // The run's cancel with the park's checkpoint; the abort handler finds `execute` without a wake and runs none.
            Seq([new Run(null, "\"started\"")], cancels.Snapshot(), "cancels");
            Seq([false], cancelAborted.Snapshot(), "cancel's context is not aborted");
            await root.AbortAsync(Context);
            await submission.WaitAsync(Context);
            await harness.CloseAsync(Context);
        });

        // --- waking ---

        yield return Case(prefix + "waking.ends-a-woken-call-whose-tool-is-gone-with-tool-unavailable-failed-and-reports-it", async () =>
        {
            var setup = Setup();
            var runs = new SyncList<Run>();
            var installed = AddTool(setup.Registry, SignalTool(runs));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            var id = await ParkedTask(harness);
            installed.Dispose();
            await harness.SignalAsync(id, Done, "done", Said("late"), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            var settled = await harness.WaitForTaskAsync(id, Context);
            MatchJson("""{"status":"failed"}""", OutcomeJson(settled), "outcome");
            var result = await FirstResult(root);
            // Built from the slot: the details and diagnostics the parked run left stay.
            MatchJson("""{"isError":true,"details":{"build":1}}""", result, "result");
            Contains(Text(result), "[info] run 1", "the parked run's diagnostic");
            Contains(Text(result), "Tool ci is not available", "text");
            Check(setup.Reports.Snapshot().Any(error => error.Message == "Tool ci is not available: its parked call c1 cannot run again"),
                "reports: " + string.Join("|", setup.Reports.Snapshot().Select(error => error.Message)));
            Equal(1, runs.Count, "runs");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "waking.settles-a-woken-unsafe-run-cut-off-by-a-crash-interrupted-after-cancel", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var cancels = new SyncList<Run>();
            var started = new Deferred();
            var runs = 0;
            AddTool(setup.Registry, Tool("ci", async (_, api, ctx) =>
            {
                Interlocked.Increment(ref runs);
                if (api.Wake is null) return ToolPark.Sleeping(signals: [Done.Key("done")], checkpoint: "first");
                api.Output("rerun\n");
                await api.DetailsAsync(Obj(("woken", true)), ctx);
                started.Resolve();
                await Aborted(ctx.CancellationToken);
                return new ToolExecutionResult();
            }, tool => tool with { Cancel = Recording(cancels) }));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var crashed = await Open(setup, await SqliteStorage.OpenAsync(path));
            var submissionId = (await Go(crashed.Root)).Id;
            var id = await ParkedTask(crashed.Harness);
            await crashed.Harness.SignalAsync(id, Done, "done", Said("x"), Context);
            await started.Task;
            // The process dies here: another one opens the same database.
            await Crash(crashed.Harness);
            var next = await Open(setup, await SqliteStorage.OpenAsync(path));
            Equal(SubmissionStatus.Done, await Settled(next.Harness, submissionId), "status");
            Equal(2, Volatile.Read(ref runs), "runs");
            Seq([new Run(null, FirstCheckpoint)], cancels.Snapshot(), "cancels");
            Contains(Text(await FirstResult(next.Root)), "Tool ci was interrupted and may have partially run", "text");
            await next.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "waking.reruns-a-woken-safe-run-cut-off-by-a-crash-keeping-the-slot-s-details-and-diagnostics-not-its-output", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var started = new Deferred();
            var seen = new SyncList<Run>();
            AddTool(setup.Registry, Tool("ci", async (_, api, ctx) =>
            {
                seen.Add(RunOf(api));
                var run = seen.Count;
                if (api.Wake is null)
                {
                    api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "parked"));
                    await api.DetailsAsync(Obj(("build", 1)), ctx);
                    return ToolPark.Sleeping(signals: [Done.Key("done")], checkpoint: "first");
                }
                if (run == 2)
                {
                    api.Output("cut off\n");
                    started.Resolve();
                    await Aborted(ctx.CancellationToken);
                }
                return new ToolExecutionResult();
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var crashed = await Open(setup, await SqliteStorage.OpenAsync(path));
            var submissionId = (await Go(crashed.Root)).Id;
            var id = await ParkedTask(crashed.Harness);
            await crashed.Harness.SignalAsync(id, Done, "done", Said("x"), Context);
            await started.Task;
            // The process dies here: another one opens the same database.
            await Crash(crashed.Harness);
            var next = await Open(setup, await SqliteStorage.OpenAsync(path));
            Equal(SubmissionStatus.Done, await Settled(next.Harness, submissionId), "status");
            Seq([new Run(null, null), new Run(SignalWake, FirstCheckpoint), new Run(SignalWake, FirstCheckpoint)], seen.Snapshot(), "seen");
            // The park stored the details and diagnostics; the rerun starts from them, without the cut-off run's output.
            var result = await FirstResult(next.Root);
            MatchJson("""{"isError":false,"details":{"build":1}}""", result, "result");
            Equal("<harness>\n[info] parked\n</harness>", Text(result), "text");
            await next.Harness.CloseAsync(Context);
        });

        // --- wrappers ---

        yield return Case(prefix + "wrappers.pass-a-base-s-park-through-and-a-park-of-their-own-is-invalid", async () =>
        {
            foreach (var own in new[] { false, true })
            {
                var setup = Setup();
                var runs = new SyncList<Run>();
                var baseTool = SignalTool(runs);
                AddTool(setup.Registry, baseTool);
                setup.Registry.Install(Define.Extension(new Extension
                {
                    Name = "wrapper",
                    Wraps =
                    [
                        Define.WrapTool(baseTool, inner => inner with
                        {
                            Execute = async (args, api, ctx) =>
                            {
                                var returned = await inner.Execute(args, api, ctx);
                                return own && returned is ToolPark { Kind: "sleep" } park ? park with { } : returned;
                            },
                        }),
                    ],
                }));
                setup.Faux.SetResponses(Call("ci"), DoneAnswer);
                var (harness, root) = await Open(setup);
                var submission = await Go(root);
                if (own)
                {
                    Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "own: status");
                    Contains(Text(await FirstResult(root)), "a wrapper returned a park its base did not return", "own: text");
                }
                else
                {
                    var id = await ParkedTask(harness);
                    await harness.SignalAsync(id, Done, "done", Said("wrapped"), Context);
                    Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "base: status");
                    Equal(2, runs.Count, "base: runs");
                }
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "wrappers.accept-one-park-object-its-base-returns-to-calls-running-at-once", async () =>
        {
            var setup = Setup();
            ToolExecutionResult shared = ToolPark.Sleeping(signals: [Done.Key("done")]);
            var baseTool = Tool("ci", async (_, api, ctx) =>
            {
                var state = await api.ReadSignalAsync(Done, "done", ctx);
                return state is null ? shared : new ToolExecutionResult { Output = JsonValue.Array(FauxText("ok")) };
            });
            AddTool(setup.Registry, baseTool);
            setup.Registry.Install(Define.Extension(new Extension { Name = "wrapper", Wraps = [Define.WrapTool(baseTool, inner => inner with { })] }));
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("ci", JsonObjectValue.Empty, "a"), FauxToolCall("ci", JsonObjectValue.Empty, "b")], stopReason: "toolUse"),
                DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            List<TaskId> ids = [];
            await Eventually(async () =>
            {
                var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
                var slots = live is not null && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue listed ? listed.OfType<JsonObjectValue>() : [];
                ids = [.. slots.Where(slot => HasStatus(slot, "sleeping")).Select(slot => new TaskId((long)N(slot["taskId"])))];
                return ids.Count == 2;
            });
            foreach (var id in ids) await harness.SignalAsync(id, Done, "done", Said("x"), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Seq([false, false], Results(await AllEntries(root)).Select(result => result["isError"] is JsonBooleanValue { Value: true }), "isError");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "wrappers.refuse-a-park-the-wrapper-kept-from-an-earlier-run-instead-of-asking-its-base", async () =>
        {
            var setup = Setup();
            var baseTool = SignalTool(new SyncList<Run>());
            AddTool(setup.Registry, baseTool);
            ToolExecutionResult? kept = null;
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "wrapper",
                Wraps =
                [
                    Define.WrapTool(baseTool, inner => inner with
                    {
                        Execute = async (args, api, ctx) =>
                        {
                            if (kept is { } earlier) return earlier;
                            var returned = await inner.Execute(args, api, ctx);
                            if (returned is ToolPark { Kind: "sleep" }) kept = returned;
                            return returned;
                        },
                    }),
                ],
            }));
            setup.Faux.SetResponses(Call("ci"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await Go(root);
            var id = await ParkedTask(harness);
            await harness.SignalAsync(id, Done, "done", Said("x"), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Contains(Text(await FirstResult(root)), "a wrapper returned a park its base did not return", "text");
            await harness.CloseAsync(Context);
        });
    }
}
