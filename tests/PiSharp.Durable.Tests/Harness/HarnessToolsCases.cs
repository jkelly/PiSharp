// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-tools.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - Backends. Upstream opens every case on MemoryStorage. harness.tools.* does the same; harness.tools.jsonl.* and harness.tools.sqlite.*
//   rerun every case on a fresh JSONL directory and a fresh SQLite file.
// - EchoParameters is the JSON TypeBox prints for `Type.Object({ text: Type.Optional(Type.String()) })`; ToolRegistration.ParametersOrigin
//   stays TypeBox, so validation converts as pi-ai does for it.
// - `new NodeExecutionEnv({ cwd })`, read only for `env.cwd`, is ToolTestEnv (ToolEnvSupport.cs).
// - A `.then(push)` on a details() promise is a continuation that runs when the promise settles; Progress settles waiters inline and in order,
//   as promise reactions run, so the recorded order is upstream's.
// - "drops control keys set to undefined instead of faulting": a C# ToolControl has no undefined fields; the case returns
//   `{addTools: ["extra"]}` with Terminate unset and checks the same stored filter.
// - "uses explicit null details": `details: null` is JsonValue.Null (a C# null is upstream's undefined).
// - The two "coding tools" cases run CodingTools over a LocalExecutionEnv (upstream NodeExecutionEnv) in a fresh directory, on each backend
//   (upstream: MemoryStorage only); the shell is Git Bash on Windows.
using System.Collections.Immutable;
using System.Text;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessToolsCases() =>
    [
        .. ToolsTests.Cases("harness.tools.", () => new MemoryStorage()),
        .. ToolsTests.Cases("harness.tools.jsonl.",
            () => JsonlStorage.OpenAsync(Temp("tools-jsonl"), DurableContext.Background).GetAwaiter().GetResult()),
        .. ToolsTests.Cases("harness.tools.sqlite.",
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("tools-sqlite"), "session.sqlite")).GetAwaiter().GetResult()),
    ];
}

/// <summary>The harness.tools.* cases (upstream harness-tools.test.ts).</summary>
internal static class ToolsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary><c>Type.Object({ text: Type.Optional(Type.String()) })</c>.</summary>
    internal static readonly JsonObjectValue EchoParameters = O("""{"type":"object","properties":{"text":{"type":"string"}}}""");

    internal static ToolRegistration Tool(string name, ToolExecute execute, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration { Name = name, Description = $"The {name} tool", Parameters = EchoParameters, Execute = execute });
        return extra is null ? tool : extra(tool);
    }

    internal static Task<ToolExecutionResult> Result(ToolExecutionResult result) => Task.FromResult(result);

    internal static readonly ToolExecutionResult NoContent = new() { Output = JsonArrayValue.Empty };

    internal static JsonArrayValue TextContent(string text) => JsonValue.Array(FauxText(text));

    /// <summary>A tool-calling answer with one call per <c>[name, args, id]</c>.</summary>
    internal static JsonObjectValue Calls(params (string Name, JsonObjectValue Args, string Id)[] list) =>
        FauxAssistantMessage(list.Select(call => FauxToolCall(call.Name, call.Args, call.Id)), stopReason: "toolUse");

    internal static readonly JsonObjectValue Done = FauxAssistantMessage("done");

    internal sealed record Ran(IHarness Harness, IConversation Root, List<EntryRecord> Entries, SubmissionStatus Status);

    internal static async Task<Ran> Run(Func<IStorage> fresh, ChatSetup setup, FauxStep[] responses, Func<IHarness, IConversation, Task>? prepare = null,
        Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>? env = null)
    {
        setup.Faux.SetResponses(responses);
        var (harness, root) = await OpenChat(fresh(), setup, env);
        if (prepare is not null) await prepare(harness, root);
        var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        return new Ran(harness, root, await AllEntries(root), settled.Status);
    }

    internal static List<JsonObjectValue> Results(IEnumerable<EntryRecord> entries) =>
        [.. entries.Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0])];

    internal static string ResultText(JsonObjectValue message) =>
        string.Join("|", ((JsonArrayValue)message["content"]).Select(item => (JsonObjectValue)item)
            .Select(item => S(item["type"]) == "text" ? S(item["text"]) : $"[{S(item["type"])}]"));

    private static Dictionary<string, JsonObjectValue> ById(IEnumerable<EntryRecord> entries) =>
        Results(entries).ToDictionary(result => S(result["toolCallId"]));

    private static bool IsError(JsonObjectValue result) => result["isError"] is JsonBooleanValue { Value: true };

    private static async Task<List<TaskRecord>> Tasks(IHarness harness, ConversationId conversation, string? kind = null) =>
        [.. (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = conversation, Kind = kind }, 20), Context)).Items];

    private static List<EntryRecord> Systems(IEnumerable<EntryRecord> entries) => [.. entries.Where(entry => entry.Kind == "pi.system")];

    private static JsonObjectValue SystemMessage(EntryRecord entry) => (JsonObjectValue)entry.Model![0];

    private static Task Sleep(int ms) => Task.Delay(ms);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh)
    {
        // ─── tool round ─────────────────────────────────────────────────────

        yield return Case(prefix + "runs-input-tool-call-tool-result-and-answer-and-settles-the-input", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("echo", (args, _, _) => Result(new ToolExecutionResult
            {
                Output = TextContent($"echo {(((JsonObjectValue)args).TryGetValue("text", out var text) ? S(text) : "undefined")}"),
            })));
            var (harness, root, entries, status) = await Run(fresh, setup, [Calls(("echo", Obj(("text", "hi")), "c1")), Done]);
            Equal(SubmissionStatus.Done, status, "status");
            Seq(["pi.user", "pi.system", "pi.assistant", "pi.tool-result", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            var system = SystemMessage(entries[1]);
            var added = (JsonArrayValue)system["toolsAdded"];
            Equal(1, added.Count, "toolsAdded");
            var declared = (JsonObjectValue)added[0];
            Check(declared.Count == 3 && S(declared["name"]) == "echo" && S(declared["description"]) == "The echo tool" && declared["parameters"] is JsonObjectValue,
                $"toolsAdded: {added.ToJson()}");
            var result = Results(entries)[0];
            MatchJson("""{"toolCallId":"c1","toolName":"echo","isError":false}""", result, "result");
            Equal("echo hi", ResultText(result), "text");
            SameJson("""{"diagnostics":[]}""", entries[3].Data, "data");
            Check(entries[3].ByTaskId is not null, "byTaskId");
            // The second request sees the tool result right after its call.
            Equal(2, setup.Faux.State.CallCount, "calls");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        // #10395
        yield return Case(prefix + "gives-tools-and-hooks-the-harness-s-models", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<IDurableModels>();
            AddTool(setup.Registry, Tool("echo", (_, api, _) =>
            {
                seen.Add(api.Models);
                return Result(NoContent);
            }));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (_, api, _) =>
                {
                    seen.Add(api.Models);
                    return Task.FromResult<BeforeToolDecision?>(null);
                },
            });
            var ran = await Run(fresh, setup, [Calls(("echo", Obj(), "c1")), Done]);
            Equal(SubmissionStatus.Done, ran.Status, "status");
            var models = seen.Snapshot();
            Equal(2, models.Count, "seen");
            Check(models.All(each => ReferenceEquals(each, setup.Models)), "every models is the Harness's");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-calls-to-tools-the-request-did-not-offer-without-a-task", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Result(NoContent)));
            var (harness, root, entries, status) = await Run(fresh, setup, [Calls(("ghost", Obj(), "c1"), ("echo", Obj(), "c2")), Done]);
            Equal(SubmissionStatus.Done, status, "status");
            var results = Results(entries);
            MatchJson("""{"toolCallId":"c1","isError":true}""", results[0], "ghost");
            Equal("<harness>\n[error] Tool ghost is not available\n</harness>", ResultText(results[0]), "ghost text");
            MatchJson("""{"toolCallId":"c2","isError":false}""", results[1], "echo");
            var ghostEntry = entries.First(entry => entry.Kind == "pi.tool-result");
            SameJson("""{"diagnostics":[{"severity":"error","code":"tool_unavailable","message":"Tool ghost is not available"}]}""", ghostEntry.Data, "data");
            Equal(1, (await Tasks(harness, root.Id)).Count(task => task.Kind == "pi.tool"), "tool tasks");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-a-call-to-a-tool-deactivated-after-preparation-with-tool-unavailable", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<string>();
            AddTool(setup.Registry, Tool("echo", (_, _, _) =>
            {
                seen.Add("ran");
                return Result(NoContent);
            }));
            IConversation? root = null;
            FauxStep deactivate = new FauxResponseFactory(async _ =>
            {
                await root!.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only() }, Context);
                return Calls(("echo", Obj(), "c1"));
            });
            var result = await Run(fresh, setup, [deactivate, Done], (_, conversation) =>
            {
                root = conversation;
                return Task.CompletedTask;
            });
            Equal(0, seen.Count, "ran");
            Equal("<harness>\n[error] Tool echo is not available\n</harness>", ResultText(Results(result.Entries)[0]), "text");
            // The next preparation removes it.
            SameJson("""[{"name":"echo"}]""", SystemMessage(Systems(result.Entries)[^1])["toolsRemoved"], "toolsRemoved");
            await result.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "removes-unregistered-active-tools-from-the-offer-and-adds-them-back-after-re-registration", async () =>
        {
            var setup = Setup();
            var echo = Tool("echo", (_, _, _) => Result(NoContent));
            var registration = AddTool(setup.Registry, echo);
            var first = await Run(fresh, setup, [Done]);
            registration.Dispose();
            setup.Faux.SetResponses(Calls(("echo", Obj(), "c1")), Done);
            var second = await (await first.Root.SubmitAsync(SubmissionDraft.Input("again"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, second.Status, "second");
            var entries = await AllEntries(first.Root);
            SameJson("""[{"name":"echo"}]""", SystemMessage(Systems(entries)[^1])["toolsRemoved"], "toolsRemoved");
            Check(IsError(Results(entries)[0]), "unavailable result");
            // The stored agent is not rewritten; the tool is only not resolved.
            Equal(0, (await first.Root.AgentAsync(Context)).Tools.Length, "agent tools");

            AddTool(setup.Registry, echo);
            setup.Faux.SetResponses(Done);
            await (await first.Root.SubmitAsync(SubmissionDraft.Input("back"), Context)).WaitAsync(Context);
            entries = await AllEntries(first.Root);
            var addition = SystemMessage(Systems(entries)[^1]);
            Seq(["echo"], ((JsonArrayValue)addition["toolsAdded"]).Select(declared => S(((JsonObjectValue)declared)["name"])), "toolsAdded");
            await first.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "produces-tool-unavailable-when-the-implementation-is-unregistered-before-its-task-runs", async () =>
        {
            var setup = Setup();
            Installed? second = null;
            AddTool(setup.Registry, Tool("first", (_, _, _) =>
            {
                second!.Dispose();
                return Result(NoContent);
            }, tool => tool with { ExecutionMode = ToolExecutionMode.Sequential }));
            second = AddTool(setup.Registry, Tool("second", (_, _, _) => Result(new ToolExecutionResult { Output = TextContent("ran") })));
            var ran = await Run(fresh, setup, [Calls(("first", Obj(), "c1"), ("second", Obj(), "c2")), Done]);
            Equal(SubmissionStatus.Done, ran.Status, "status");
            Equal("<harness>\n[error] Tool second is not available\n</harness>", ResultText(Results(ran.Entries)[1]), "late");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reads-the-execution-mode-when-a-round-starts-and-keeps-it-for-the-round", async () =>
        {
            var setup = Setup();
            var events = new SyncList<string>();
            ToolExecute Slow(string name) => async (_, _, _) =>
            {
                events.Add($"start {name}");
                // Changed after the round started: not seen by this round.
                setup.ToolExecution = ToolExecutionMode.Parallel;
                await Sleep(20);
                events.Add($"end {name}");
                return NoContent;
            };
            AddTool(setup.Registry, Tool("a", Slow("a")));
            AddTool(setup.Registry, Tool("b", Slow("b")));
            // Changed while the model request runs: the round that follows uses it.
            var request = FauxStep.From(_ =>
            {
                setup.ToolExecution = ToolExecutionMode.Sequential;
                return Calls(("a", Obj(), "c1"), ("b", Obj(), "c2"));
            });
            var ran = await Run(fresh, setup, [request, Done]);
            Seq(["start a", "end a", "start b", "end b"], events.Snapshot(), "events");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-a-round-in-parallel-by-default-and-sequentially-when-configured-or-required-by-a-tool", async () =>
        {
            // overlap: a waits (bounded) until b has started instead of sleeping 20 ms. Upstream's 20 ms is enough under Node's event loop,
            // where both calls start before any timer fires; here b's start follows its own commits, which a loaded runner can delay past
            // 20 ms. A sequential round never starts b while a runs, so a's wait times out and the order is still start a, end a.
            async Task<List<string>> Trace(ChatSetup setup, bool overlap = false)
            {
                var events = new SyncList<string>();
                var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ToolExecute Slow(string name) => async (_, _, _) =>
                {
                    events.Add($"start {name}");
                    if (name == "b") bStarted.TrySetResult();
                    if (overlap && name == "a") await Task.WhenAny(bStarted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                    else await Sleep(20);
                    events.Add($"end {name}");
                    return NoContent;
                };
                if (setup.Registry.Snapshot().GetExtension("tool:a") is null) AddTool(setup.Registry, Tool("a", Slow("a")));
                if (setup.Registry.Snapshot().GetExtension("tool:b") is null) AddTool(setup.Registry, Tool("b", Slow("b")));
                var result = await Run(fresh, setup, [Calls(("a", Obj(), "c1"), ("b", Obj(), "c2")), Done]);
                await result.Harness.CloseAsync(Context);
                return events.Snapshot();
            }
            Seq(["start a", "start b"], (await Trace(Setup(), overlap: true)).Take(2), "parallel");
            var configured = Setup();
            configured.ToolExecution = ToolExecutionMode.Sequential;
            Seq(["start a", "end a", "start b", "end b"], await Trace(configured), "sequential");
            var perTool = Setup();
            var events = new SyncList<string>();
            AddTool(perTool.Registry, Tool("a", async (_, _, _) =>
            {
                await Sleep(20);
                events.Add("a");
                return NoContent;
            }, tool => tool with { ExecutionMode = ToolExecutionMode.Sequential }));
            AddTool(perTool.Registry, Tool("b", (_, _, _) =>
            {
                events.Add("b");
                return Result(NoContent);
            }));
            var ran = await Run(fresh, perTool, [Calls(("a", Obj(), "c1"), ("b", Obj(), "c2")), Done]);
            var tasks = await Tasks(ran.Harness, ran.Root.Id);
            var tools = tasks.Where(task => task.Kind == "pi.tool").OrderBy(task => task.Id.Value).ToList();
            // The generation owns both tools and creates the second only after the first ended.
            var generation = tasks.First(task => task.Kind == "pi.generation");
            Seq([generation.Id, generation.Id], tools.Select(task => task.Owner!.Value), "owners");
            Seq(["a", "b"], events.Snapshot(), "events");
            await ran.Harness.CloseAsync(Context);
        });

        // ─── tool results ───────────────────────────────────────────────────

        yield return Case(prefix + "uses-retained-output-and-the-last-details-when-the-result-omits-them-with-diagnostics-in-order", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("log", async (_, api, _) =>
            {
                api.Output("line 1\n");
                api.Output(Encoding.UTF8.GetBytes("line 2\nline 3\n"));
                api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "from api"));
                await api.DetailsAsync(Obj(("step", 1)), Context);
                await api.DetailsAsync(Obj(("step", 2)), Context);
                return new ToolExecutionResult { Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Warn, "from result")] };
            }, tool => tool with { OutputLimits = new ToolOutputLimits { MaxLines = 2 } }));
            var ran = await Run(fresh, setup, [Calls(("log", Obj(), "c1")), Done]);
            var result = Results(ran.Entries)[0];
            SameJson("""{"step":2}""", result["details"], "details");
            Equal("line 1\nline 2\n|<harness>\n[info] from api\n[warn] from result\n[warn] Output truncated to its beginning: 1 lines, 7 bytes dropped\n</harness>",
                ResultText(result), "text");
            var entry = ran.Entries.First(candidate => candidate.Kind == "pi.tool-result");
            Seq([null, null, "truncated"], ((JsonArrayValue)((JsonObjectValue)entry.Data!)["diagnostics"])
                .Select(diagnostic => ((JsonObjectValue)diagnostic).TryGetValue("code", out var code) ? S(code) : null), "codes");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "offers-the-tail-window-with-the-configured-pace-not-a-head-window-and-accepts-skipped-output", async () =>
        {
            var setup = Setup().WithFreshSettings();
            setup.Progress = new ProgressSettings { OutputIntervalMs = 250 };
            var windows = new SyncList<ShellOutputWindow?>();
            AddTool(setup.Registry, Tool("tailed", (_, api, _) =>
            {
                windows.Add(api.OutputWindow);
                api.Output("dropped\n");
                api.Output("x\ny\n", new ShellOutputSkip(8, 1, true));
                return Result(new ToolExecutionResult());
            }, tool => tool with { OutputLimits = new ToolOutputLimits { MaxLines = 1, Retain = ToolOutputRetention.Tail } }));
            AddTool(setup.Registry, Tool("headed", (_, api, _) =>
            {
                windows.Add(api.OutputWindow);
                return Result(new ToolExecutionResult());
            }, tool => tool with { OutputLimits = new ToolOutputLimits { MaxLines = 1 } }));
            var ran = await Run(fresh, setup, [Calls(("tailed", Obj(), "c1"), ("headed", Obj(), "c2")), Done]);
            Check(windows.Snapshot().Contains(new ShellOutputWindow(50 * 1024, 1, 250, 100 * 1024)), "tail window");
            Check(windows.Snapshot().Contains(null), "no head window");
            // 8 bytes written, 8 skipped, then "x\n" dropped by the window: 3 lines, 18 bytes in all.
            Equal("y\n|<harness>\n[warn] Output truncated to its end: 3 lines, 18 bytes dropped\n</harness>",
                ResultText(ById(ran.Entries)["c1"]), "text");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "bounds-explicit-text-content-and-keeps-other-content", async () =>
        {
            var setup = Setup();
            var image = O("""{"type":"image","data":"AAAA","mimeType":"image/png"}""");
            AddTool(setup.Registry, Tool("big", (_, _, _) => Result(new ToolExecutionResult
            {
                Output = JsonValue.Array(FauxText("a\nb\n"), image, FauxText("c\nd\n")),
            }), tool => tool with { OutputLimits = new ToolOutputLimits { MaxLines = 2, Retain = ToolOutputRetention.Tail } }));
            var ran = await Run(fresh, setup, [Calls(("big", Obj(), "c1")), Done]);
            Equal("[image]|c\nd\n|<harness>\n[warn] Output truncated to its end: 2 lines, 4 bytes dropped\n</harness>", ResultText(Results(ran.Entries)[0]), "text");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "turns-a-throw-into-a-tool-error-result-with-the-partial-output", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("fail", (_, api, _) =>
            {
                api.Output("partial\n");
                throw new InvalidOperationException("boom");
            }));
            var ran = await Run(fresh, setup, [Calls(("fail", Obj(), "c1")), Done]);
            Equal(SubmissionStatus.Done, ran.Status, "status");
            var result = Results(ran.Entries)[0];
            Check(IsError(result), "isError");
            Equal("partial\n|<harness>\n[error] boom\n</harness>", ResultText(result), "text");
            await ran.Harness.CloseAsync(Context);
        });

        // #10549
        yield return Case(prefix + "records-how-long-execute-took-excluding-hooks-and-nothing-for-calls-that-did-not-run", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("slow", async (_, _, _) =>
            {
                await Sleep(30);
                return NoContent;
            }));
            AddTool(setup.Registry, Tool("thrower", async (_, _, _) =>
            {
                await Sleep(30);
                throw new InvalidOperationException("boom");
            }));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (call, _, _) =>
                {
                    // Upstream sleeps 100 ms here and bounds durationMs below 100. A loaded macOS runner measured a 30 ms execute at 138 ms, so
                    // the hook delay and the bound are scaled together: a duration that included the hook would still be at least the bound.
                    await Sleep(1000);
                    return S(call["id"]) == "blocked" ? new BeforeToolDecision(Block: "no") : null;
                },
            });
            var ran = await Run(fresh, setup, [Calls(("slow", Obj(), "slow"), ("thrower", Obj(), "thrower"), ("slow", Obj(), "blocked")), Done]);
            var byId = ById(ran.Entries);
            foreach (var id in new[] { "slow", "thrower" })
            {
                var duration = N(byId[id]["durationMs"]);
                Check(duration >= 25 && duration < 1000, $"{id} durationMs {duration}");
            }
            Check(IsError(byId["thrower"]), "thrower isError");
            Check(!byId["blocked"].ContainsKey("durationMs"), "blocked has no durationMs");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "validates-arguments-before-and-after-beforetool-and-applies-blocks-and-replacements", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<string>();
            AddTool(setup.Registry, Tool("echo", (args, _, _) =>
            {
                seen.Add(args.ToJson());
                return Result(NoContent);
            }));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (call, _, _) =>
                {
                    var id = S(call["id"]);
                    if (id == "block") return Task.FromResult<BeforeToolDecision?>(new BeforeToolDecision(Block: "not today"));
                    if (id == "throw") throw new InvalidOperationException("hook failed");
                    if (id == "bad") return Task.FromResult<BeforeToolDecision?>(new BeforeToolDecision(O("""{"text":{"not":"a string"}}""")));
                    var text = ((JsonObjectValue)call["arguments"]).TryGetValue("text", out var value) ? S(value) : "undefined";
                    return Task.FromResult<BeforeToolDecision?>(new BeforeToolDecision(Obj(("text", $"{text}!"))));
                },
            });
            var ran = await Run(fresh, setup,
            [
                Calls(
                    ("echo", Obj(("text", 1)), "coerced"),
                    ("echo", O("""{"text":{"not":"a string"}}"""), "invalid"),
                    ("echo", Obj(), "block"),
                    ("echo", Obj(), "throw"),
                    ("echo", Obj(("text", "x")), "bad"),
                    ("echo", Obj(("text", "x")), "ok")),
                Done,
            ]);
            // Parallel tools append their results in completion order.
            var byId = ById(ran.Entries);
            Check(IsError(byId["block"]) && ResultText(byId["block"]) == "<harness>\n[error] Tool call blocked: not today\n</harness>", "block");
            Check(IsError(byId["throw"]) && ResultText(byId["throw"]) == "<harness>\n[error] Tool call blocked: hook failed\n</harness>", "throw");
            Check(IsError(byId["bad"]) && ResultText(byId["bad"]).Contains("Validation failed", StringComparison.Ordinal), "bad: " + ResultText(byId["bad"]));
            Check(!IsError(byId["ok"]) && ResultText(byId["ok"]) == "", "ok");
            Check(IsError(byId["invalid"]) && ResultText(byId["invalid"]).Contains("Validation failed", StringComparison.Ordinal), "invalid");
            // pi-ai coerces a number to a string before the first validation.
            Check(!IsError(byId["coerced"]) && ResultText(byId["coerced"]) == "", "coerced");
            var calls = seen.Snapshot();
            Check(calls.Contains("""{"text":"1!"}""") && calls.Contains("""{"text":"x!"}"""), $"seen [{string.Join(", ", calls)}]");
            Equal(2, calls.Count, "seen count");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "repairs-arguments-with-preparearguments-before-validation-and-a-throwing-repair-is-invalid", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<string>();
            AddTool(setup.Registry, Tool("echo", (args, _, _) =>
            {
                seen.Add(args.ToJson());
                return Result(NoContent);
            }, tool => tool with
            {
                PrepareArguments = args =>
                {
                    var text = ((JsonObjectValue)args).TryGetValue("text", out var value) ? value : null;
                    if (text is JsonStringValue { Value: "throw" }) throw new InvalidOperationException("cannot repair");
                    return text is JsonNumberValue number ? Obj(("text", $"#{number.Value}")) : args;
                },
            }));
            var ran = await Run(fresh, setup, [Calls(("echo", Obj(("text", 7)), "fixed"), ("echo", Obj(("text", "throw")), "broken")), Done]);
            var byId = ById(ran.Entries);
            Equal("", ResultText(byId["fixed"]), "fixed");
            Equal("<harness>\n[error] cannot repair\n</harness>", ResultText(byId["broken"]), "broken");
            Seq(["""{"text":"#7"}"""], seen.Snapshot(), "seen");
            // The stored call keeps what the model sent.
            var call = ((JsonArrayValue)((JsonObjectValue)ran.Entries[2].Model![0])["content"]).OfType<JsonObjectValue>().First(item => S(item["type"]) == "toolCall");
            SameJson("""{"text":7}""", call["arguments"], "stored arguments");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-the-first-beforetool-block-win-and-skips-later-handlers", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Result(NoContent)));
            var asked = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (_, _, _) =>
                {
                    asked.Add("first");
                    return Task.FromResult<BeforeToolDecision?>(new BeforeToolDecision(Block: "first says no"));
                },
            });
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (_, _, _) =>
                {
                    asked.Add("second");
                    return Task.FromResult<BeforeToolDecision?>(new BeforeToolDecision(Block: "second says no"));
                },
            });
            var ran = await Run(fresh, setup, [Calls(("echo", Obj(), "c1")), Done]);
            Seq(["first"], asked.Snapshot(), "asked");
            Equal("<harness>\n[error] Tool call blocked: first says no\n</harness>", ResultText(Results(ran.Entries)[0]), "text");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "chains-aftertool-replacements-and-observes-the-round-with-aftertools", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Result(new ToolExecutionResult { Output = TextContent("raw") })));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (_, result, _, _) => Task.FromResult<ToolExecutionResult?>(result with { Output = TextContent("first") }),
            });
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (_, result, _, _) => Task.FromResult<ToolExecutionResult?>(result with
                {
                    Details = Obj(("replaced", string.Join("|", (result.Output ?? JsonArrayValue.Empty).Select(item =>
                        item is JsonObjectValue block && S(block["type"]) == "text" ? S(block["text"]) : "")))),
                }),
            });
            var observed = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                AfterTools = (assistant, entries, _, _) =>
                {
                    observed.Add($"{assistant.Value}:[{string.Join(",", entries.Select(entry => entry.Value))}]");
                    return Task.CompletedTask;
                },
            });
            var ran = await Run(fresh, setup, [Calls(("echo", Obj(), "c1")), Done]);
            var result = Results(ran.Entries)[0];
            Equal("first", ResultText(result), "text");
            SameJson("""{"replaced":"first"}""", result["details"], "details");
            var resultEntry = ran.Entries.First(entry => entry.Kind == "pi.tool-result");
            Seq([$"{ran.Entries[2].Id.Value}:[{resultEntry.Id.Value}]"], observed.Snapshot(), "observed");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-the-hooks-of-the-selected-extensions-a-task-owned-child-copies-its-owner-s-selection", async () =>
        {
            var setup = Setup();
            var calledIn = new SyncList<ConversationId>();
            var echo = Define.Extension(new Extension { Name = "echo", Tools = [Tool("echo", (_, _, _) => Result(NoContent))] });
            var audit = Define.Extension(new Extension
            {
                Name = "audit",
                Hooks = [Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = (_, api, _) =>
                    {
                        calledIn.Add(api.ConversationId);
                        return Task.FromResult<BeforeToolDecision?>(null);
                    },
                })],
            });
            setup.Registry.Install(echo);
            setup.Registry.Install(audit);
            // Audit is installed but not in the default selection.
            setup.Extensions = [echo];
            var first = await Run(fresh, setup, [Done]);
            await first.Root.ConfigureAsync(new AgentChange { Extensions = ExtensionSelection.Edit(add: [audit]) }, Context);
            // An owner task no registered definition takes stays live and pending.
            var owner = global::PiSharp.Durable.Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.owner",
                Version = 1,
                Initial = _ => O("""{"phase":"never"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["never"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var childId = await first.Harness.CommitAsync(async tx =>
            {
                var taskId = await tx.CreateTaskAsync(owner, JsonObjectValue.Empty, new TaskOptions(TaskOwnership.Conversation) { ConversationId = first.Root.Id });
                return (await tx.CreateConversationAsync(ConversationOwnership.Task(taskId))).Id;
            }, Context);
            var child = (await first.Harness.ConversationAsync(childId, Context))!;
            var other = await first.Harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
            {
                Agent = new AgentChange { Model = new ModelRef("faux", "faux-1") },
            }, Context);
            foreach (var conversation in new[] { first.Root, child, other })
            {
                setup.Faux.SetResponses(Calls(("echo", Obj(), "c1")), Done);
                await (await conversation.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            }
            Seq([first.Root.Id, child.Id], calledIn.Snapshot(), "calledIn");
            await first.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "applies-addtools-and-terminates-only-when-every-result-of-the-round-asks-to", async () =>
        {
            var setup = Setup();
            var stop = Tool("stop", (_, _, _) => Result(NoContent with { Control = new ToolControl { Terminate = true } }));
            var grow = Tool("grow", (_, _, _) => Result(NoContent with { Control = new ToolControl { AddTools = ["extra", "stop"] } }));
            AddTool(setup.Registry, stop);
            AddTool(setup.Registry, grow);
            AddTool(setup.Registry, Tool("extra", (_, _, _) => Result(NoContent)));
            var first = await Run(fresh, setup, [Calls(("stop", Obj(), "c1"))], (_, root) =>
                root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(stop, grow) }, Context));
            Equal(SubmissionStatus.Done, first.Status, "first");
            Equal("pi.tool-result", first.Entries[^1].Kind, "last kind");
            var settled = await Tasks(first.Harness, first.Root.Id);
            Check(settled.All(task => task.State.Status == DurableTaskStatus.Terminal), "every task terminal");

            setup.Faux.SetResponses(Calls(("stop", Obj(), "c1"), ("grow", Obj(), "c2")), Done);
            var second = await (await first.Root.SubmitAsync(SubmissionDraft.Input("again"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, second.Status, "second");
            Equal("pi.assistant", (await AllEntries(first.Root))[^1].Kind, "last kind after the second round");
            // addTools appends to the stored tool array, skipping names it already holds.
            SameJson("""["stop","grow","extra"]""", (await first.Harness.SnapshotAsync(Agents.AgentDoc, first.Root.Id, Context))?["tools"], "stored tools");
            await first.Harness.CloseAsync(Context);
        });

        // ─── generation hooks ───────────────────────────────────────────────

        yield return Case(prefix + "replaces-request-messages-observes-responses-and-continues-on-yield", async () =>
        {
            var setup = Setup();
            var requests = new SyncList<List<string>>();
            FauxStep record = FauxStep.From(request =>
            {
                requests.Add([.. request.Messages.Select(message => $"{S(((JsonObjectValue)message)["role"])}:{TextOf(message) ?? ""}")]);
                return FauxAssistantMessage($"answer {requests.Count}");
            });
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                BeforeRequest = (messages, _, _) =>
                    Task.FromResult<ImmutableArray<JsonValue>?>([.. messages, Obj(("role", "user"), ("content", "injected"), ("timestamp", 0))]),
            });
            var responses = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                AfterResponse = (message, _, _) =>
                {
                    responses.Add(TextOf(message) ?? "");
                    return Task.CompletedTask;
                },
            });
            var yields = 0;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) => Task.FromResult<JsonValue?>(yields++ == 0 ? JsonValue.Array(FauxText("keep going")) : null),
            });
            var ran = await Run(fresh, setup, [record, record]);
            Equal(SubmissionStatus.Done, ran.Status, "status");
            Seq(["user:go", "user:injected"], requests[0].TakeLast(2), "first request");
            Seq(["answer 1", "answer 2"], responses.Snapshot(), "responses");
            Seq(["pi.user", "pi.assistant", "pi.user", "pi.assistant"], ran.Entries.Select(entry => entry.Kind), "kinds");
            // The injected message was used for the request only.
            Check(!ran.Entries.Any(entry => TextOf(entry.Model?[0]) == "injected"), "no injected entry");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-run-s-input-open-across-an-onyield-continuation-and-answers-it-with-the-final-answer", async () =>
        {
            var setup = Setup();
            var yields = 0;
            IHarness? harness = null;
            SubmissionId? input = null;
            SubmissionStatus? statusAtSecondRequest = null;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) => Task.FromResult<JsonValue?>(yields++ == 0 ? (JsonValue)"again" : null),
            });
            FauxStep second = new FauxResponseFactory(async _ =>
            {
                var live = await harness!.SnapshotAsync(Live.LiveDoc, ConversationId.Root, Context);
                input = new SubmissionId((long)N(((JsonArrayValue)((JsonObjectValue)live!["run"])["inputs"])[0]));
                statusAtSecondRequest = (await (await harness.SubmissionAsync(input.Value, Context))!.StatusAsync(Context)).Status;
                return FauxAssistantMessage("second");
            });
            var result = await Run(fresh, setup, [FauxAssistantMessage("first"), second], (opened, _) =>
            {
                harness = opened;
                return Task.CompletedTask;
            });
            Equal(SubmissionStatus.Placed, statusAtSecondRequest, "status at the second request");
            var answers = result.Entries.Where(entry => entry.Kind == "pi.assistant").ToList();
            MatchJson($$"""{"status":"done","answer":{{answers[1].Id.Value}}}""",
                (await (await harness!.SubmissionAsync(input!.Value, Context))!.StatusAsync(Context)).ToJson(), "input");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "observes-responses-that-arrive-by-polling-a-deferred-request", async () =>
        {
            var setup = Setup(new FauxOptions { PendingFetches = 1, PollAfterMs = 1 });
            var observed = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                AfterResponse = (message, _, _) =>
                {
                    observed.Add($"{S(message["stopReason"])}:{TextOf(message) ?? ""}");
                    return Task.CompletedTask;
                },
            });
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            var result = await Run(fresh, setup, [FauxAssistantMessage("late")]);
            Equal(SubmissionStatus.Done, result.Status, "status");
            // The still-deferred results are not terminal.
            Seq(["stop:late"], observed.Snapshot(), "observed");
            await result.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-the-first-onyield-continuation-win-and-reports-throws-without-stopping-later-handlers", async () =>
        {
            var setup = Setup();
            var called = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                AfterResponse = (_, _, _) =>
                {
                    called.Add("throwing observer");
                    throw new InvalidOperationException("observer failed");
                },
            });
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                AfterResponse = (_, _, _) =>
                {
                    called.Add("next observer");
                    return Task.CompletedTask;
                },
            });
            var yields = 0;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) => Task.FromResult<JsonValue?>(yields++ == 0 ? (JsonValue)"first" : null),
            });
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) =>
                {
                    called.Add("second onYield");
                    return Task.FromResult<JsonValue?>(called.Snapshot().Count(name => name == "second onYield") == 1 ? (JsonValue)"second" : null);
                },
            });
            var ran = await Run(fresh, setup, [FauxAssistantMessage("a"), FauxAssistantMessage("b"), FauxAssistantMessage("c")]);
            // The first continuation skips the second handler; on the next answer the second handler's continuation wins.
            Seq(["go", "first", "second"], ran.Entries.Where(entry => entry.Kind == "pi.user").Select(entry => TextOf(entry.Model![0])), "users");
            Equal(2, called.Snapshot().Count(name => name == "second onYield"), "second onYield");
            Equal(3, called.Snapshot().Count(name => name == "next observer"), "next observer");
            Check(setup.Reports.Snapshot().Any(error => error.Message == "observer failed"), "reported");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-durable-hook-decisions-in-task-memos", async () =>
        {
            var setup = Setup();
            var asked = 0;
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Result(NoContent)));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (_, api, callContext) =>
                {
                    Interlocked.Increment(ref asked);
                    var decision = await api.MemoAsync("approval:decision", "approved", callContext);
                    Check(JsonValue.DeepEquals(decision, await api.MemoAsync("approval:decision", "denied", callContext)), "memo");
                    return null;
                },
            });
            var ran = await Run(fresh, setup, [Calls(("echo", Obj(), "c1")), Done]);
            Equal(1, asked, "asked");
            await ran.Harness.CloseAsync(Context);
        });

        // ─── tool execution api ─────────────────────────────────────────────

        yield return Case(prefix + "builds-the-environment-per-call-from-the-conversation-s-cwd-and-runs-commits-memos-and-child-tasks", async () =>
        {
            var setup = Setup();
            var child = global::PiSharp.Durable.Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.child",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = (task, runtime, callContext) => runtime.CommitAsync((_, _) =>
                        (TaskState?)NextTaskState.Completed(N(((JsonObjectValue)task.Input)["n"]) * 2), callContext),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            AddTask(setup.Registry, child);
            var seen = new SyncList<string>();
            var probe = Tool("probe", async (_, api, _) =>
            {
                seen.Add(api.Env?.Cwd ?? "undefined");
                seen.Add(string.Join(",", (await api.AgentAsync(Context)).Tools.Select(each => each.Name)));
                seen.Add((api.Registry.GetExtension("tool:probe") is not null).ToString());
                var entry = await api.CommitAsync(async tx =>
                {
                    // The next call runs in the new directory: its environment is built when it executes.
                    await Agents.ConfigureAsync(tx, api.ConversationId, new AgentChange { Cwd = "/" });
                    return await tx.AppendEntryAsync(api.ConversationId, new EntryDraft { Kind = "test.note", Data = api.CallId });
                }, Context);
                seen.Add((entry.ByTaskId == api.TaskId).ToString());
                seen.Add((await api.MemoAsync("m", 1, Context)).ToJson());
                seen.Add((await api.MemoAsync("m", 2, Context)).ToJson());
                var id = await api.CreateTaskAsync(child, Obj(("n", 21)), new TaskOptions(TaskOwnership.Conversation), Context);
                var done = await api.WaitForTaskAsync(id, Context);
                seen.Add(OutcomeJson(done).ToJson());
                return NoContent;
            });
            AddTool(setup.Registry, probe);
            setup.Faux.SetResponses(Calls(("probe", Obj(), "c1"), ("probe", Obj(), "c2")), Done);
            setup.ToolExecution = ToolExecutionMode.Sequential;
            var targets = new SyncList<(ConversationId, string?)>();
            var (harness, root) = await OpenChat(fresh(), setup, (target, _) =>
            {
                targets.Add((target.ConversationId, target.Cwd));
                return Task.FromResult<IExecutionEnv?>(new ToolTestEnv(target.Cwd ?? "/tmp"));
            });
            await root.ConfigureAsync(new AgentChange { Cwd = "/tmp" }, Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            const string outcome = """{"status":"completed","result":42}""";
            string[] Call(string cwd) => [cwd, "probe", "True", "True", "1", "1", outcome];
            Seq([.. Call("/tmp"), .. Call("/")], seen.Snapshot(), "seen");
            Check(targets.Snapshot().Contains((root.Id, "/tmp")), "target /tmp");
            Check(targets.Snapshot().Contains((root.Id, "/")), "target /");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-a-throwing-environment-with-a-tool-error-result-and-reports-it-once-while-preparing", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("probe", (_, _, _) => Result(new ToolExecutionResult { Output = TextContent("ran") })));
            var ran = await Run(fresh, setup, [Calls(("probe", Obj(), "c1")), Done], null,
                (_, _) => throw new InvalidOperationException("no sandbox"));
            var result = Results(ran.Entries)[0];
            Check(IsError(result), "isError");
            Equal("<harness>\n[error] no sandbox\n</harness>", ResultText(result), "text");
            // Each preparation reports the failure and renders without an environment.
            Equal(2, setup.Reports.Snapshot().Count(error => error.Message == "no sandbox"), "reports");
            await ran.Harness.CloseAsync(Context);
        });

        // ─── coding tools ───────────────────────────────────────────────────

        yield return Case(prefix + "answers-a-failing-command-with-its-retained-tail-and-diagnostics-in-order", async () =>
        {
            var directory = Temp("coding");
            try
            {
                var setup = Setup();
                AddTool(setup.Registry, CodingTools.CreateBashTool());
                const string command = "i=1; while [ $i -le 3000 ]; do echo line-$i; i=$((i + 1)); done; exit 7";
                setup.Faux.SetResponses(Calls(("bash", Obj(("command", command)), "b")), Done);
                var env = new LocalExecutionEnv(directory);
                var (harness, root) = await OpenChat(fresh(), setup, (_, _) => Task.FromResult<IExecutionEnv?>(env));
                await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                var entry = (await AllEntries(root)).First(candidate => Entries.ToolResultEntry.Is(candidate));
                var result = Results([entry])[0];
                Check(IsError(result), "isError");
                var text = ResultText(result);
                Check(text.StartsWith("line-1001\n", StringComparison.Ordinal), "retained tail: " + text[..Math.Min(40, text.Length)]);
                Seq(["full_output", "exit_code", "truncated"], ((JsonArrayValue)((JsonObjectValue)entry.Data!)["diagnostics"])
                    .Select(diagnostic => ((JsonObjectValue)diagnostic).TryGetValue("code", out var code) ? S(code) : null), "diagnostic codes");
                Check(text.Contains("line-3000\n|<harness>\n[info] Full output: ", StringComparison.Ordinal), "spill notice: " + text[^300..]);
                Check(text.Contains("\n[error] Command exited with code 7\n[warn] Output truncated to its end: 1000 lines, ", StringComparison.Ordinal),
                    "error and truncation: " + text[^300..]);
                await harness.CloseAsync(Context);
            }
            finally { await RemoveTempDirAsync(directory); }
        });

        yield return Case(prefix + "reads-edits-and-runs-a-command-in-one-run-then-answers", async () =>
        {
            var directory = Temp("coding");
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "hello world\n");
                var setup = Setup();
                setup.Registry.Install(Define.Extension(new Extension
                {
                    Name = "coding",
                    Tools = [CodingTools.CreateReadTool(), CodingTools.CreateEditTool(), CodingTools.CreateBashTool()],
                }));
                setup.Faux.SetResponses(
                    Calls(("read", Obj(("path", "notes.txt")), "r")),
                    Calls(("edit", O("""{"path":"notes.txt","edits":[{"oldText":"world","newText":"durable"}]}"""), "e")),
                    Calls(("bash", Obj(("command", "cat notes.txt")), "b")),
                    Done);
                var env = new LocalExecutionEnv(directory);
                var (harness, root) = await OpenChat(fresh(), setup, (_, _) => Task.FromResult<IExecutionEnv?>(env));
                var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                Equal(SubmissionStatus.Done, settled.Status, "status");
                var entries = await AllEntries(root);
                Seq(["read|False|hello world\n", "edit|False|Successfully replaced 1 block(s) in notes.txt.", "bash|False|hello durable\n"],
                    Results(entries).Select(result => $"{S(result["toolName"])}|{IsError(result)}|{ResultText(result)}"), "results");
                Equal("pi.assistant", entries[^1].Kind, "last entry");
                Equal("hello durable\n", await File.ReadAllTextAsync(Path.Combine(directory, "notes.txt")), "file");
                await harness.CloseAsync(Context);
            }
            finally { await RemoveTempDirAsync(directory); }
        });

        // ─── tool progress and lifetime ─────────────────────────────────────

        yield return Case(prefix + "applies-the-default-output-limits", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("lines", (_, api, _) =>
            {
                for (var index = 1; index <= 2500; index++) api.Output($"{index}\n");
                return Result(new ToolExecutionResult());
            }));
            var ran = await Run(fresh, setup, [Calls(("lines", Obj(), "c1")), Done]);
            var text = ResultText(Results(ran.Entries)[0]);
            Check(text.StartsWith("1\n2\n", StringComparison.Ordinal), "start");
            Check(text.EndsWith("\n2000\n|<harness>\n[warn] Output truncated to its beginning: 500 lines, 2500 bytes dropped\n</harness>", StringComparison.Ordinal),
                "end: " + text[^120..]);
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sanitizes-running-output-but-keeps-explicit-result-content-as-the-tool-returned-it", async () =>
        {
            var setup = Setup();
            string? slotOutput = null;
            AddTool(setup.Registry, Tool("noisy", async (_, api, _) =>
            {
                api.Output("a\u0007b\r\n");
                await api.DetailsAsync(Obj(("ready", true)), Context);
                var live = await api.SnapshotAsync(Live.LiveDoc, api.ConversationId, Context);
                slotOutput = S(((JsonObjectValue)((JsonArrayValue)live!["tools"])[0])["output"]);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("explicit", (_, _, _) => Result(new ToolExecutionResult { Output = TextContent("c\u001bd") })));
            var ran = await Run(fresh, setup, [Calls(("noisy", Obj(), "c1"), ("explicit", Obj(), "c2")), Done]);
            Equal("ab\n", slotOutput, "slot output");
            var byId = ById(ran.Entries);
            Equal("ab\n", ResultText(byId["c1"]), "noisy");
            Equal("c\u001bd", ResultText(byId["c2"]), "explicit");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "drops-control-keys-set-to-undefined-instead-of-faulting", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("grow", (_, _, _) => Result(NoContent with { Control = new ToolControl { AddTools = ["extra"] } })));
            var extra = Tool("extra", (_, _, _) => Result(NoContent));
            AddTool(setup.Registry, extra);
            var ran = await Run(fresh, setup, [Calls(("grow", Obj(), "c1")), Done], (_, conversation) =>
                conversation.ConfigureAsync(new AgentChange { Tools = ToolSelection.Without(extra) }, Context));
            Equal(SubmissionStatus.Done, ran.Status, "status");
            // addTools deletes the name from a stored `{ remove }` filter.
            SameJson("""{"remove":[]}""", (await ran.Harness.SnapshotAsync(Agents.AgentDoc, ran.Root.Id, Context))?["tools"], "stored tools");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "uses-explicit-null-details-instead-of-the-last-reported-value", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("null", async (_, api, _) =>
            {
                await api.DetailsAsync(Obj(("old", 1)), Context);
                return NoContent with { Details = JsonValue.Null };
            }));
            var ran = await Run(fresh, setup, [Calls(("null", Obj(), "c1")), Done]);
            Check(Results(ran.Entries)[0].TryGetValue("details", out var details) && details is JsonNullValue, "details is null");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "settles-details-promises-with-coalesced-progress-commits-and-the-terminal-commit", async () =>
        {
            var setup = Setup();
            var settled = new SyncList<string>();
            AddTool(setup.Registry, Tool("details", async (_, api, _) =>
            {
                // Three updates in one throttle window coalesce; the last is still pending when execute() returns.
                var first = api.DetailsAsync(Obj(("n", 1)), Context).ContinueWith(_ => settled.Add("first"), TaskContinuationOptions.ExecuteSynchronously);
                var second = api.DetailsAsync(Obj(("n", 2)), Context).ContinueWith(_ => settled.Add("second"), TaskContinuationOptions.ExecuteSynchronously);
                await first;
                _ = second;
                _ = api.DetailsAsync(Obj(("n", 3)), Context).ContinueWith(_ => settled.Add("third"), TaskContinuationOptions.ExecuteSynchronously);
                return NoContent;
            }));
            var ran = await Run(fresh, setup, [Calls(("details", Obj(), "c1")), Done]);
            Seq(["first", "second", "third"], settled.Snapshot(), "settled");
            SameJson("""{"n":3}""", Results(ran.Entries)[0]["details"], "details");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "finishes-a-call-under-the-implementation-it-resolved-when-the-tool-is-replaced-mid-call", async () =>
        {
            var setup = Setup();
            var started = new HarnessTests.Deferred();
            var finished = new HarnessTests.Deferred();
            AddTool(setup.Registry, Tool("work", async (_, _, _) =>
            {
                started.Resolve();
                await finished.Task;
                return new ToolExecutionResult { Output = TextContent("v1") };
            }));
            setup.Faux.SetResponses(Calls(("work", Obj(), "c1")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            // The same extension name replaces the old one in place.
            AddTool(setup.Registry, Tool("work", (_, _, _) => Result(new ToolExecutionResult { Output = TextContent("v2") })));
            finished.Resolve();
            await submission.WaitAsync(Context);
            Equal("v1", ResultText(Results(await AllEntries(root))[0]), "text");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "uses-a-section-and-hook-extension-reloaded-mid-run-from-the-run-s-next-request", async () =>
        {
            var setup = Setup();
            var requests = new SyncList<string>();
            Extension Prompt(string version) => Define.Extension(new Extension
            {
                Name = "prompt",
                Sections = [Define.Section("mode", version)],
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    BeforeRequest = (_, _, _) =>
                    {
                        requests.Add(version);
                        return Task.FromResult<ImmutableArray<JsonValue>?>(null);
                    },
                })],
            });
            setup.Registry.Install(Prompt("v1"));
            var running = new HarnessTests.Deferred();
            var reloaded = new HarnessTests.Deferred();
            AddTool(setup.Registry, Tool("work", async (_, _, _) =>
            {
                running.Resolve();
                await reloaded.Task;
                return NoContent;
            }));
            setup.Faux.SetResponses(Calls(("work", Obj(), "c1")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await running.Task;
            setup.Registry.Install(Prompt("v2"));
            reloaded.Resolve();
            await submission.WaitAsync(Context);
            var sections = (await AllEntries(root)).Select(entry => entry.Model is { Count: > 0 } model ? (JsonObjectValue)model[0] : null)
                .Where(message => message is not null && S(message["role"]) == "system" && message.ContainsKey("sections"))
                .Select(message => message!["sections"].ToJson());
            Seq(["""{"mode":"<mode>\nv1\n</mode>"}""", """{"mode":"<mode>\nv2\n</mode>"}"""], sections, "sections");
            Seq(["v1", "v2"], requests.Snapshot(), "requests");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-invocation-bound-waits-and-stops-watches-when-the-tool-s-invocation-ends", async () =>
        {
            var setup = Setup();
            var never = global::PiSharp.Durable.Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.never",
                Version = 1,
                Initial = _ => O("""{"phase":"never"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["never"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            Task? wait = null;
            Task? watchClosed = null;
            AddTool(setup.Registry, Tool("detach", async (_, api, _) =>
            {
                // The child's definition is not registered, so it stays pending.
                var child = await api.CreateTaskAsync(never, JsonObjectValue.Empty, new TaskOptions(TaskOwnership.Conversation), Context);
                wait = api.WaitForTaskAsync(child, Context);
                _ = wait.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                var watch = await api.WatchDocAsync(Live.LiveDoc, api.ConversationId, Context);
                watchClosed = watch!.Closed;
                return NoContent;
            }));
            var ran = await Run(fresh, setup, [Calls(("detach", Obj(), "c1")), Done]);
            await RejectsAsync(() => wait!, "invocation has ended", "wait");
            await watchClosed!;
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-details-still-waiting-when-the-call-is-aborted-during-aftertool", async () =>
        {
            var setup = Setup();
            Task? pendingDetails = null;
            var inAfterTool = new HarnessTests.Deferred();
            AddTool(setup.Registry, Tool("slow", (_, api, _) =>
            {
                api.Output("first\n");
                // The output commit is in flight, so these details wait for the next throttle window.
                pendingDetails = api.DetailsAsync(Obj(("step", 1)), Context);
                _ = pendingDetails.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return Result(new ToolExecutionResult());
            }));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = async (_, _, _, callContext) =>
                {
                    inAfterTool.Resolve();
                    await Aborted(callContext.CancellationToken);
                    return null;
                },
            });
            setup.Faux.SetResponses(Calls(("slow", Obj(), "c1")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await inAfterTool.Task;
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
            var taskId = new TaskId((long)N(((JsonObjectValue)((JsonArrayValue)live!["tools"])[0])["taskId"]));
            await harness.AbortTaskAsync(taskId, Context);
            try
            {
                await pendingDetails!;
                throw new InvalidOperationException("expected the details to reject");
            }
            catch (Exception error) when (error.Message != "expected the details to reject") { }
            await submission.WaitAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-an-aborted-tool-with-only-its-durable-output-discarding-buffered-output", async () =>
        {
            var setup = Setup();
            var buffered = new HarnessTests.Deferred();
            AddTool(setup.Registry, Tool("slow", async (_, api, callContext) =>
            {
                api.Output("durable\n");
                // The first output commits at once; this one waits for the next throttle window.
                await Sleep(20);
                api.Output("buffered\n");
                buffered.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Calls(("slow", Obj(), "c1")), Done);
            // Upstream relies on the abort landing within the output throttle window. A loaded macOS runner let that window pass first and
            // committed "buffered"; a manual clock that never advances keeps the second output buffered until the abort discards it.
            var (harness, root) = await OpenChat(fresh(), setup, timeProvider: new ManualTimeProvider());
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await buffered.Task;
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
            var taskId = new TaskId((long)N(((JsonObjectValue)((JsonArrayValue)live!["tools"])[0])["taskId"]));
            await harness.AbortTaskAsync(taskId, Context);
            await submission.WaitAsync(Context);
            Equal("durable\n|<harness>\n[error] Tool slow was aborted\n</harness>", ResultText(Results(await AllEntries(root))[0]), "text");
            await harness.CloseAsync(Context);
        });
    }
}
