// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-tools-recovery.test.ts. Verbatim apart from the
// adaptations in HarnessSupport.cs, ChatSupport.cs and HarnessToolsCases.cs, and these:
// - Backends. Upstream reopens a SQLite file; harness.tools-recovery.* does the same, and harness.tools-recovery.jsonl.* reruns every case
//   reopening a JSONL directory.
// - `aborted(callContext.abortSignal!)` is Aborted(callContext.CancellationToken); waitFor is Eventually.
// - `new NodeExecutionEnv({ cwd })`, read only for `env.cwd`, is ToolTestEnv (ToolEnvSupport.cs).
// - "lets context derivation answer a faulted tool": upstream's result carries a function in `details`, which is not strict JSON, so the
//   result commit throws. A C# result holds only JSON values; the case's result carries a null tool name in `control.addTools`, which is not
//   strict JSON either and makes the same commit throw.
// - "answers a real bash command interrupted by close and reopen, then finishes the run" runs CodingTools' bash over a LocalExecutionEnv
//   (upstream NodeExecutionEnv) in its own fresh directory (upstream: the SQLite file's directory); the shell is Git Bash on Windows.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using static ToolsTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessToolsRecoveryCases() =>
    [
        .. ToolsRecoveryTests.Cases("harness.tools-recovery.", () =>
        {
            var path = Path.Combine(Temp("tools-recovery-sqlite"), "session.sqlite");
            return () => SqliteStorage.OpenAsync(path).GetAwaiter().GetResult();
        }),
        .. ToolsRecoveryTests.Cases("harness.tools-recovery.jsonl.", () =>
        {
            var directory = Temp("tools-recovery-jsonl");
            return () => JsonlStorage.OpenAsync(directory, DurableContext.Background).GetAwaiter().GetResult();
        }),
    ];
}

/// <summary>The harness.tools-recovery.* cases (upstream harness-tools-recovery.test.ts).</summary>
internal static class ToolsRecoveryTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static async Task<OpenedChat> Open(Func<IStorage> open, ChatSetup setup, Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>? env = null)
    {
        var opened = await OpenChat(open(), setup, env);
        opened.Harness.Resume();
        return opened;
    }

    private static ToolRegistration RecoveryTool(string name, ToolExecute execute, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration { Name = name, Description = name, Execute = execute });
        return extra is null ? tool : extra(tool);
    }

    private static JsonObjectValue Call(string name, string id = "c1") =>
        FauxAssistantMessage([FauxToolCall(name, JsonObjectValue.Empty, id)], stopReason: "toolUse");

    private static string Text(JsonObjectValue? message) =>
        message is null ? "" : string.Join("|", ((JsonArrayValue)message["content"]).Select(item => (JsonObjectValue)item)
            .Select(item => S(item["type"]) == "text" ? S(item["text"]) : ""));

    private static async Task<TaskId> ToolTaskId(IHarness harness)
    {
        TaskId? id = null;
        await Eventually(async () =>
        {
            var live = await harness.SnapshotAsync(Live.LiveDoc, ConversationId.Root, Context);
            if (live is not null && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots &&
                ((JsonObjectValue)slots[0]).TryGetValue("taskId", out var taskId))
                id = new TaskId((long)N(taskId));
            return id is not null;
        });
        return id!.Value;
    }

    private static async Task<SubmissionRecord> WaitFor(IHarness harness, SubmissionId id) => await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context);

    /// <summary>A tool that writes output, then blocks until its invocation is cancelled the first <c>Blocking</c> times it runs.
    /// <c>Started</c> resolves once the output is durable.</summary>
    private sealed class BlockingTool
    {
        private int runs;

        public BlockingTool(string name, Func<ToolRegistration, ToolRegistration>? extra = null)
        {
            Registration = RecoveryTool(name, async (_, api, callContext) =>
            {
                var run = Interlocked.Increment(ref runs);
                api.Output($"run {run}\n");
                await api.DetailsAsync(Obj(("run", run)), callContext);
                if (run <= Blocking)
                {
                    Started.Resolve();
                    await Aborted(callContext.CancellationToken);
                }
                return new ToolExecutionResult();
            }, extra);
        }

        public ToolRegistration Registration { get; }
        public HarnessTests.Deferred Started { get; } = new();
        public int Runs => Volatile.Read(ref runs);
        public int Blocking { get; init; } = 1;
    }

    /// <param name="location">A fresh reopenable location: each call of the returned function opens its storage.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Func<IStorage>> location)
    {
        yield return Case(prefix + "answers-an-unsafe-tool-interrupted-after-intent-with-its-durable-partial-output", async () =>
        {
            var open = location();
            var setup = Setup();
            var blocking = new BlockingTool("work");
            AddTool(setup.Registry, blocking.Registration);
            setup.Faux.SetResponses(Call("work"), Done);
            var opened = await Open(open, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await blocking.Started.Task;
            var taskId = await ToolTaskId(opened.Harness);
            await opened.Harness.CloseAsync(Context);

            opened = await Open(open, setup);
            MatchJson("""{"checkpoint":{"phase":"execute","arguments":{},"replay":"unsafe"}}""",
                (await opened.Harness.GetTaskAsync(taskId, Context))?.State.ToJson(), "state");
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Equal(1, blocking.Runs, "runs");
            var result = Results(await AllEntries(opened.Root))[0];
            MatchJson("""{"isError":true,"details":{"run":1}}""", result, "result");
            Equal("run 1\n|<harness>\n[error] Tool work was interrupted and may have partially run\n</harness>", Text(result), "text");
            SameJson("{}", await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context), "live");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reruns-a-tool-only-when-both-the-stored-and-the-current-replay-policy-are-safe", async () =>
        {
            (ToolReplay Stored, ToolReplay Current, bool Reruns)[] cases =
            [
                (ToolReplay.Safe, ToolReplay.Safe, true),
                (ToolReplay.Safe, ToolReplay.Unsafe, false),
                (ToolReplay.Unsafe, ToolReplay.Safe, false),
            ];
            foreach (var (stored, current, reruns) in cases)
            {
                var open = location();
                var setup = Setup();
                var blocking = new BlockingTool("work", tool => tool with { Replay = stored });
                var registration = AddTool(setup.Registry, blocking.Registration);
                setup.Faux.SetResponses(Call("work"), Done);
                var opened = await Open(open, setup);
                var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
                await blocking.Started.Task;
                await opened.Harness.CloseAsync(Context);

                registration.Dispose();
                AddTool(setup.Registry, blocking.Registration with { Replay = current });
                opened = await Open(open, setup);
                Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, $"{stored}/{current} status");
                var result = Results(await AllEntries(opened.Root))[0];
                Equal(reruns ? 2 : 1, blocking.Runs, $"{stored}/{current} runs");
                Equal(!reruns, result["isError"] is JsonBooleanValue { Value: true }, $"{stored}/{current} isError");
                if (reruns) Equal("run 2\n", Text(result), $"{stored}/{current} text");
                await opened.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "reruns-a-safe-tool-with-the-environment-of-the-conversation-s-cwd-at-rerun-and-not-once-it-is-deselected", async () =>
        {
            foreach (var change in new[] { "cwd", "deselect" })
            {
                var open = location();
                var setup = Setup();
                var cwds = new SyncList<string?>();
                var started = new HarnessTests.Deferred();
                var work = RecoveryTool("work", async (_, api, callContext) =>
                {
                    cwds.Add(api.Env?.Cwd);
                    if (cwds.Count == 1)
                    {
                        started.Resolve();
                        await Aborted(callContext.CancellationToken);
                    }
                    return new ToolExecutionResult();
                }, tool => tool with { Replay = ToolReplay.Safe });
                AddTool(setup.Registry, work);
                setup.Faux.SetResponses(Call("work"), Done);
                Func<EnvTarget, DurableContext, Task<IExecutionEnv?>> env = (target, _) =>
                    Task.FromResult<IExecutionEnv?>(new ToolTestEnv(target.Cwd ?? "/"));
                var opened = await Open(open, setup, env);
                await opened.Root.ConfigureAsync(new AgentChange { Cwd = "/one" }, Context);
                var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
                await started.Task;
                await opened.Root.ConfigureAsync(change == "cwd" ? new AgentChange { Cwd = "/two" } : new AgentChange { Extensions = ExtensionSelection.Only() },
                    Context);
                await opened.Harness.CloseAsync(Context);

                opened = await Open(open, setup, env);
                Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, $"{change} status");
                var result = Results(await AllEntries(opened.Root))[0];
                if (change == "cwd")
                {
                    Seq(["/one", "/two"], cwds.Snapshot(), "cwds");
                    Check(result["isError"] is JsonBooleanValue { Value: false }, "isError");
                }
                else
                {
                    // A tool that no longer resolves is treated as unsafe: interrupted, not rerun.
                    Seq(["/one"], cwds.Snapshot(), "cwds");
                    Check(Text(result).Contains("was interrupted", StringComparison.Ordinal), "text: " + Text(result));
                }
                await opened.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "reruns-beforetool-when-interrupted-before-intent-and-executes-once", async () =>
        {
            var open = location();
            var setup = Setup();
            var runs = 0;
            AddTool(setup.Registry, RecoveryTool("work", (_, _, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult(NoContent);
            }));
            var reached = new HarnessTests.Deferred();
            var asked = 0;
            var decisions = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (_, api, callContext) =>
                {
                    var attempt = Interlocked.Increment(ref asked);
                    // A durable first-writer-wins decision survives the rerun.
                    decisions.Add(S(await api.MemoAsync("test:decision", $"attempt {attempt}", callContext)));
                    if (attempt == 1)
                    {
                        reached.Resolve();
                        await Aborted(callContext.CancellationToken);
                    }
                    return null;
                },
            });
            setup.Faux.SetResponses(Call("work"), Done);
            var opened = await Open(open, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await reached.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await Open(open, setup);
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Seq(new[] { 2, 1 }, new[] { asked, runs }, "[asked, runs]");
            Seq(["attempt 1", "attempt 1"], decisions.Snapshot(), "decisions");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reruns-the-generation-tools-phase-interrupted-before-its-commit", async () =>
        {
            var open = location();
            var setup = Setup();
            AddTool(setup.Registry, RecoveryTool("work", (_, _, _) => Task.FromResult(NoContent)));
            var reached = new HarnessTests.Deferred();
            var observed = 0;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                AfterTools = async (_, _, _, callContext) =>
                {
                    if (Interlocked.Increment(ref observed) == 1)
                    {
                        reached.Resolve();
                        await Aborted(callContext.CancellationToken);
                    }
                },
            });
            setup.Faux.SetResponses(Call("work"), Done);
            var opened = await Open(open, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await reached.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await Open(open, setup);
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Equal(2, observed, "observed");
            Seq(["pi.user", "pi.system", "pi.assistant", "pi.tool-result", "pi.assistant"], await Kinds(opened.Root), "kinds");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-an-aborted-tool-with-its-partial-output-and-continues-the-run", async () =>
        {
            var open = location();
            var setup = Setup();
            var blocking = new BlockingTool("work");
            AddTool(setup.Registry, blocking.Registration);
            setup.Faux.SetResponses(Call("work"), Done);
            var opened = await Open(open, setup);
            var submission = await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await blocking.Started.Task;
            var taskId = await ToolTaskId(opened.Harness);
            Equal(TaskAbortResult.Marked, await opened.Harness.AbortTaskAsync(taskId, Context), "abort");
            MatchJson("""{"status":"aborted","result":{"entryId":"<number>"}}""", OutcomeJson(await opened.Harness.WaitForTaskAsync(taskId, Context)), "outcome");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Equal("run 1\n|<harness>\n[error] Tool work was aborted\n</harness>", Text(Results(await AllEntries(opened.Root))[0]), "text");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-context-derivation-answer-a-faulted-tool-and-continues-the-run", async () =>
        {
            var open = location();
            var setup = Setup();
            // A result that is not strict JSON makes the result commit throw, so the scheduler faults the task.
            AddTool(setup.Registry, RecoveryTool("bad", (_, _, _) =>
                Task.FromResult(NoContent with { Control = new ToolControl { AddTools = [null!] } })));
            var requests = new SyncList<string>();
            setup.Faux.SetResponses(Call("bad"), FauxStep.From(request =>
            {
                var result = request.Messages.OfType<JsonObjectValue>().FirstOrDefault(message => S(message["role"]) == "toolResult");
                requests.Add(result is not null ? Text(result) : "none");
                return Done;
            }));
            var opened = await Open(open, setup);
            var settled = await (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            Equal(0, Results(await AllEntries(opened.Root)).Count, "results");
            Seq(["Tool result unavailable: history ends before this call completed."], requests.Snapshot(), "requests");
            var tasks = await opened.Harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = opened.Root.Id }, 20), Context);
            var faulted = tasks.Items.First(task => task.Kind == "pi.tool");
            MatchJson("""{"status":"terminal","outcome":{"status":"faulted"}}""", faulted.State.ToJson(), "state");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-a-real-bash-command-interrupted-by-close-and-reopen-then-finishes-the-run", async () =>
        {
            var open = location();
            var directory = Temp("tools-recovery-bash");
            try
            {
                var env = new LocalExecutionEnv(directory);
                Task<IExecutionEnv?> Env(EnvTarget target, DurableContext envContext) => Task.FromResult<IExecutionEnv?>(env);
                var setup = Setup();
                AddTool(setup.Registry, CodingTools.CreateBashTool());
                setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("bash", Obj(("command", "echo started; sleep 30")), "b")], stopReason: "toolUse"),
                    Done);
                var opened = await Open(open, setup, Env);
                var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
                await Eventually(async () =>
                {
                    var live = await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context);
                    return live is not null && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots &&
                        ((JsonObjectValue)slots[0]).TryGetValue("output", out var output) && output is JsonStringValue { Value: "started\n" };
                });
                await opened.Harness.CloseAsync(Context);

                opened = await Open(open, setup, Env);
                Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
                Equal("started\n|<harness>\n[error] Tool bash was interrupted and may have partially run\n</harness>",
                    Text(Results(await AllEntries(opened.Root))[0]), "text");
                await opened.Harness.CloseAsync(Context);
            }
            finally { await RemoveTempDirAsync(directory); }
        });

        yield return Case(prefix + "clears-the-interrupted-attempt-s-progress-before-a-safe-rerun", async () =>
        {
            var open = location();
            var setup = Setup();
            HarnessTests.Deferred[] started = [new(), new()];
            var runs = 0;
            AddTool(setup.Registry, RecoveryTool("work", async (_, api, callContext) =>
            {
                var run = Interlocked.Increment(ref runs) - 1;
                if (run == 0)
                {
                    api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "first a"));
                    api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "first b"));
                    await api.DetailsAsync(Obj(("run", 1), ("extra", true)), callContext);
                }
                else
                {
                    api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "second"));
                    await api.DetailsAsync(Obj(("run", 2)), callContext);
                }
                started[run].Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("work"), Done);
            var opened = await Open(open, setup);
            await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started[0].Task;
            await opened.Harness.CloseAsync(Context);

            opened = await Open(open, setup);
            await started[1].Task;
            var slot = (JsonObjectValue)((JsonArrayValue)(await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context))!["tools"])[0];
            SameJson("""[{"severity":"info","message":"second"}]""", slot["diagnostics"], "diagnostics");
            SameJson("""{"run":2}""", slot["details"], "details");
            await opened.Harness.CloseAsync(Context);
        });
    }
}
