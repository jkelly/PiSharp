// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-live-deltas.test.ts. Verbatim apart from the adaptations
// in ChatSupport.cs and HarnessSupport.cs, and these:
// - Backends. Upstream runs this file on MemoryStorage; HarnessLiveDeltasCases() does the same (harness.live-deltas.*), and the JSONL and
//   SQLite groups (harness.live-deltas.jsonl.*, harness.live-deltas.sqlite.*) rerun every case but "keeps a complete base exactly while
//   nothing runs", which reads no storage.
// - Operations compare as their wire JSON. expect.arrayContaining is ContainsAll, expect.any(Number) the "<number>" placeholder of
//   MatchExactly.
// - drive()'s action queue is a Channel the tool reads under its invocation's cancellation, as upstream's tool awaits a wake promise that
//   rejects on abort. waitFor is Eventually: output progress crosses the Harness's real-time throttle, as upstream's does.
// - RecordingStorage (a MemoryStorage subclass that remembers base or delta per commit) is ControlledStorage: Session commits run one at a
//   time on the line and publish right after Storage admits them, so a commit listener reads its own commit's batch as LastCommit.
// - "starts calls the request did not offer ...": upstream's "bad" tool returns a function in `details`, which is not strict JSON, so its
//   result commit throws. A C# result holds only JSON values; as in HarnessToolsRecoveryCases.cs, the result carries a null tool name in
//   `control.addTools`, which is not strict JSON either and makes the same commit throw.
using System.Collections.Immutable;
using System.Threading.Channels;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessLiveDeltasCases() =>
        [.. LiveDeltaTests.Cases("harness.live-deltas.", () => Task.FromResult<IStorage>(new MemoryStorage())), LiveDeltaTests.BaseWhileNothingRuns("harness.live-deltas.")];

    private static IEnumerable<(string, Func<Task>)> HarnessLiveDeltasJsonlCases() =>
        LiveDeltaTests.Cases("harness.live-deltas.jsonl.", async () => await JsonlStorage.OpenAsync(Temp("live-deltas-jsonl"), DurableContext.Background));

    private static IEnumerable<(string, Func<Task>)> HarnessLiveDeltasSqliteCases() =>
        LiveDeltaTests.Cases("harness.live-deltas.sqlite.", async () => await SqliteStorage.OpenAsync(Path.Combine(Temp("live-deltas-sqlite"), "live.sqlite")));
}

/// <summary>The harness.live-deltas.* cases and their support.</summary>
internal static partial class LiveDeltaTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const string Output = """["tools",0,"output"]""";

    private static ToolRegistration ToolOf(string name, ToolExecute execute, ToolOutputLimits? limits = null) => Define.Tool(new ToolRegistration
    {
        Name = name,
        Description = name,
        OutputLimits = limits,
        Execute = execute,
    });

    /// <summary>Records the operations of every <c>pi.live</c> commit that has some.</summary>
    private static SyncList<ImmutableArray<Op>> LiveCommits(IHarness harness)
    {
        var commits = new SyncList<ImmutableArray<Op>>();
        harness.SubscribeCommits((publication, _) =>
        {
            foreach (var change in DocumentChanges(publication))
                if (change.Record.Kind == "pi.live" && change.Ops.Length > 0) commits.Add(change.Ops);
        });
        return commits;
    }

    private static Task<JsonObjectValue?> LiveOf(IHarness harness) => harness.SnapshotAsync(Live.LiveDoc, ConversationId.Root, Context);

    private static async Task<JsonObjectValue?> FirstSlot(IHarness harness) =>
        await LiveOf(harness) is { } live && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots
            ? (JsonObjectValue)slots[0] : null;

    private static bool IsOutput(Op op) => op is PathOp path && DeltaPath.ToJson(path.Path).ToJson() == Output;

    private static string Json(IEnumerable<Op> ops) => OpsJson(ops);

    /// <summary><c>expect(ops).toEqual(expect.arrayContaining(expected))</c>, with MatchExactly placeholders.</summary>
    private static void ContainsAll(IEnumerable<Op> ops, string expected, string what)
    {
        var actual = ops.Select(op => (JsonValue)op.ToJson()).ToList();
        foreach (var item in (JsonArrayValue)J(expected))
        {
            Check(actual.Any(op => Matches(item, op)), $"{what}: expected {item.ToJson()} among {JsonValue.Array(actual).ToJson()}");
        }
    }

    private static bool Matches(JsonValue expected, JsonValue actual)
    {
        try
        {
            MatchExactly(expected.ToJson(), actual, "op");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal sealed class Driven(IHarness harness, SyncList<ImmutableArray<Op>> commits, Func<Func<IToolExecutionApi, Task<bool>>, Task> push,
        Task<SubmissionRecord> submission)
    {
        public IHarness Harness { get; } = harness;
        public SyncList<ImmutableArray<Op>> Commits { get; } = commits;

        /// <summary>Runs one action inside the tool and returns the operations of the commit it caused.</summary>
        public async Task<ImmutableArray<Op>> Step(Func<IToolExecutionApi, Task> action)
        {
            var before = Commits.Count;
            await push(async api =>
            {
                await action(api);
                return true;
            });
            await HarnessTests.Eventually(() => Commits.Count > before);
            return Commits.Snapshot()[^1];
        }

        public Task<ImmutableArray<Op>> Step(Action<IToolExecutionApi> action) => Step(api =>
        {
            action(api);
            return Task.CompletedTask;
        });

        /// <summary>Lets the tool return and the run finish; returns the commits made meanwhile.</summary>
        public async Task<List<ImmutableArray<Op>>> Finish()
        {
            var before = Commits.Count;
            await push(_ => Task.FromResult(false));
            await submission;
            return [.. Commits.Snapshot().Skip(before)];
        }
    }

    /// <summary>Drives one tool call step by step and captures the exact Chord operations of every <c>pi.live</c> commit (upstream
    /// <c>drive</c>).</summary>
    private static async Task<Driven> Drive(Func<Task<IStorage>> fresh, ToolOutputLimits? limits = null)
    {
        var setup = Setup();
        var actions = Channel.CreateUnbounded<Func<IToolExecutionApi, Task<bool>>>();
        HarnessTests.AddTool(setup.Registry, ToolOf("drive", async (_, api, callContext) =>
        {
            while (true)
            {
                var action = await actions.Reader.ReadAsync(callContext.CancellationToken);
                if (!await action(api)) return new ToolExecutionResult();
            }
        }, limits ?? new ToolOutputLimits()));
        setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("drive", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
            FauxAssistantMessage([FauxText("done")]));
        var (harness, root) = await OpenChat(await fresh(), setup);
        var commits = LiveCommits(harness);
        var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
        await HarnessTests.Eventually(async () => await FirstSlot(harness) is { } slot && slot["status"] is JsonStringValue { Value: "running" });
        return new Driven(harness, commits, action => actions.Writer.WriteAsync(action).AsTask(), submission.WaitAsync(Context));
    }

    internal static (string, Func<Task>) BaseWhileNothingRuns(string prefix) =>
        Case(prefix + "keeps-a-complete-base-exactly-while-nothing-runs", () =>
        {
            static bool Base(string value) => Live.LiveDoc.Definition.CheckpointWhen!(O(value), [], new CheckpointInfo(1000));
            const string slotPending = """{"callId":"c","name":"n","status":"pending"}""";
            const string slotRunning = """{"callId":"c","name":"n","status":"running"}""";
            const string slotDone = """{"callId":"c","name":"n","status":"done"}""";
            const string run = """{"taskId":1,"inputs":[]}""";
            Equal(true, Base("{}"), "empty");
            Equal(false, Base("{\"run\":" + run + ",\"generation\":{\"attempt\":1}}"), "generation");
            Equal(true, Base("{\"run\":" + run + ",\"tools\":[" + slotPending + "," + slotDone + "]}"), "pending and done");
            Equal(false, Base("{\"run\":" + run + ",\"tools\":[" + slotDone + "," + slotRunning + "]}"), "running");
            Equal(true, Base("{\"run\":" + run + "}"), "run only");
        });

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "hands-a-generation-over-to-its-tool-round-and-starts-a-tool-with-one-field-write-each", async () =>
        {
            var setup = Setup();
            HarnessTests.AddTool(setup.Registry, ToolOf("noop", (_, _, _) => Task.FromResult(new ToolExecutionResult { Content = JsonArrayValue.Empty })));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("noop", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var commits = LiveCommits(harness);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var tasks = (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 20), Context)).Items;
            List<long> Ids(string kind) => [.. tasks.Where(task => task.Kind == kind).Select(task => task.Id.Value).Order()];
            var (firstGeneration, secondGeneration) = (Ids("pi.generation")[0], Ids("pi.generation")[1]);
            var tool = Ids("pi.tool")[0];
            var result = (await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context)).Items.First(entry => entry.Kind == "pi.tool-result").Id.Value;
            var all = commits.Snapshot();
            Equal(8, all.Count, "commits: " + string.Join(" | ", all.Select(ops => Json(ops))));
            // submission
            MatchExactly($$"""[["s",["run"],{"taskId":{{firstGeneration}},"inputs":["<number>"]}]]""", J(Json(all[0])), "submission");
            // request
            Equal("""[["s",["generation"],{"attempt":1}]]""", Json(all[1]), "request");
            // the generation starts its tool round and keeps the run
            ContainsAll(all[2], $$"""[["d",["generation"]],["s",["tools"],[{"callId":"c1","name":"noop","taskId":{{tool}},"status":"pending"}]]]""", "handover");
            // intent
            Equal("""[["s",["tools",0,"status"],"running"]]""", Json(all[3]), "intent");
            // result
            ContainsAll(all[4], $$"""[["s",["tools",0,"status"],"done"],["s",["tools",0,"entry"],{{result}}]]""", "result");
            // the generation's tools phase hands the run to the next generation
            ContainsAll(all[5], $$"""[["d",["tools"]],["s",["run","taskId"],{{secondGeneration}}]]""", "next generation");
            Equal("""[["s",["generation"],{"attempt":1}]]""", Json(all[6]), "second request");
            // the answer ends the run
            ContainsAll(all[7], """[["d",["run"]],["d",["generation"]]]""", "run end");
            Equal(2, all[2].Length, "handover length");
            Equal(2, all[4].Length, "result length");
            Equal(2, all[5].Length, "next generation length");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "appends-head-output-and-then-only-updates-the-dropped-counts-once-the-window-is-full", async () =>
        {
            var run = await Drive(fresh, new ToolOutputLimits { MaxLines = 2 });
            Equal($$"""[["s",{{Output}},"one\n"]]""", Json(await run.Step(api => api.Output("one\n"))), "one");
            Equal($$"""[["a",{{Output}},"two\n"]]""", Json(await run.Step(api => api.Output("two\n"))), "two");
            // The window is full: the retained text stays; only the counts change.
            Equal("""[["s",["tools",0,"droppedBytes"],6],["s",["tools",0,"droppedLines"],1]]""", Json(await run.Step(api => api.Output("three\n"))), "three");
            Equal("""[["s",["tools",0,"droppedBytes"],11],["s",["tools",0,"droppedLines"],2]]""", Json(await run.Step(api => api.Output("four\n"))), "four");
            await run.Finish();
            await run.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "slides-a-tail-window-as-a-front-trim-plus-an-append", async () =>
        {
            var run = await Drive(fresh, new ToolOutputLimits { MaxLines = 3, Retain = ToolOutputRetention.Tail });
            Equal($$"""[["s",{{Output}},"line 1\nline 2\nline 3\n"]]""", Json(await run.Step(api => api.Output("line 1\nline 2\nline 3\n"))), "first");
            Equal($$"""[["t",{{Output}},7],["a",{{Output}},"line 4\n"],["s",["tools",0,"droppedBytes"],7],["s",["tools",0,"droppedLines"],1]]""",
                Json(await run.Step(api => api.Output("line 4\n"))), "slide");
            // The buffer keeps only the window, and later slides stay minimal and exact.
            Equal($$"""[["t",{{Output}},14],["a",{{Output}},"line 5\nline 6\n"],["s",["tools",0,"droppedBytes"],21],["s",["tools",0,"droppedLines"],3]]""",
                Json(await run.Step(api => api.Output("line 5\nline 6\n"))), "second slide");
            Equal("line 4\nline 5\nline 6\n", S((await FirstSlot(run.Harness))!["output"]), "window");
            await run.Finish();
            await run.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "writes-the-whole-window-when-chords-overlap-search-cannot-find-the-shared-part", async () =>
        {
            // A retained window beyond the 64 KiB overlap scan.
            var wide = await Drive(fresh, new ToolOutputLimits { MaxBytes = 100 * 1024, MaxLines = 1_000_000, Retain = ToolOutputRetention.Tail });
            static string Line(int index) => $"{index.ToString().PadLeft(10, '0')} {new string('x', 989)}\n";
            var text = string.Concat(Enumerable.Range(0, 100).Select(Line));
            await wide.Step(api => api.Output(text));
            var slid = await wide.Step(api => api.Output(Line(100) + Line(101) + Line(102) + Line(103)));
            Seq(["s"], slid.Where(IsOutput).Select(op => op.ToJson()[0] is JsonStringValue code ? code.Value : "?"), "wide op codes");
            await wide.Finish();
            await wide.Harness.CloseAsync(Context);

            // Repetitive output still finds an overlap here; Chord's bounded candidate search can give up on other inputs and then writes one
            // window.
            var repetitive = await Drive(fresh, new ToolOutputLimits { MaxLines = 50, Retain = ToolOutputRetention.Tail });
            await repetitive.Step(api => api.Output(string.Concat(Enumerable.Repeat("y\n", 50))));
            var repeated = await repetitive.Step(api => api.Output("z\n"));
            Equal($$"""[["t",{{Output}},2],["a",{{Output}},"z\n"]]""", Json(repeated.Where(IsOutput)), "repetitive");
            await repetitive.Finish();
            await repetitive.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "diffs-details-leaf-by-leaf-and-appends-diagnostics", async () =>
        {
            var run = await Drive(fresh);
            const string details = """["tools",0,"details"]""";
            Equal($$"""[["s",{{details}},{"step":1,"log":"a"}]]""", Json(await run.Step(api => api.DetailsAsync(O("""{"step":1,"log":"a"}"""), Context))), "first details");
            ContainsAll(await run.Step(api => api.DetailsAsync(O("""{"step":2,"log":"ab"}"""), Context)),
                """[["s",["tools",0,"details","step"],2],["a",["tools",0,"details","log"],"b"]]""", "second details");
            Equal("""[["d",["tools",0,"details","log"]]]""", Json(await run.Step(api => api.DetailsAsync(O("""{"step":2}"""), Context))), "third details");
            const string diagnostics = """["tools",0,"diagnostics"]""";
            const string first = """{"severity":"info","message":"first"}""";
            const string second = """{"severity":"warn","message":"second"}""";
            Equal($$"""[["s",{{diagnostics}},[{{first}}]]]""",
                Json(await run.Step(api => api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "first")))), "first diagnostic");
            Equal($$"""[["p",{{diagnostics}},1,0,[{{second}}]]]""",
                Json(await run.Step(api => api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Warn, "second")))), "second diagnostic");
            // Settlement moves everything into the result entry and keeps the slot small.
            var settled = (await run.Finish())[0];
            ContainsAll(settled, $$"""[["s",["tools",0,"status"],"done"],["d",{{details}}],["d",{{diagnostics}}]]""", "settled");
            await run.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "streams-partial-text-as-appends", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 400, MinTokenSize = 4, MaxTokenSize = 4 });
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText(string.Concat(Enumerable.Repeat("word ", 250)))]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var commits = LiveCommits(harness);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var all = commits.Snapshot();
            var partials = all.Skip(2).Take(all.Count - 3).ToList();
            Check(partials.Count > 2, $"more than two partials: {partials.Count}");
            Check(partials[0] is [SetOp { Value: JsonObjectValue } set] && DeltaPath.ToJson(set.Path).ToJson() == """["generation","message"]""",
                "first partial: " + Json(partials[0]));
            foreach (var ops in partials.Skip(1))
            {
                Check(ops is [AppendOp append] && DeltaPath.ToJson(append.Path).ToJson() == """["generation","message","content",0,"text"]""",
                    "append: " + Json(ops));
            }
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stores-a-complete-base-exactly-in-the-commits-where-nothing-runs", async () =>
        {
            var storage = new ControlledStorage(await fresh());
            var setup = Setup();
            foreach (var name in new[] { "first", "second" })
            {
                HarnessTests.AddTool(setup.Registry, ToolOf(name, async (_, api, _) =>
                {
                    api.Output($"{name} output\n");
                    await Task.Delay(150);
                    api.Output($"{name} more\n");
                    return new ToolExecutionResult();
                }));
            }
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("first", JsonObjectValue.Empty, "a"), FauxToolCall("second", JsonObjectValue.Empty, "b")],
                stopReason: "toolUse"), FauxAssistantMessage([FauxText("done")]));
            setup.ToolExecution = ToolExecutionMode.Sequential;
            var (harness, root) = await OpenChat(storage, setup);
            var written = new SyncList<(string Kind, JsonObjectValue Value)>();
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                {
                    if (change.Record.Kind != "pi.live" || change.Value is not { } value) continue;
                    // Session commits run one at a time, so this commit's batch is the last one Storage admitted.
                    var write = storage.LastCommit.OfType<DocumentChangeWrite>().FirstOrDefault(item => item.Id == change.Record.Id);
                    if (write is not null) written.Add((write.Content is DocumentDelta ? "delta" : "base", value));
                }
            });
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            static bool NothingRuns(JsonObjectValue value) => !value.ContainsKey("generation") &&
                !(value.TryGetValue("tools", out var tools) && ((JsonArrayValue)tools).Any(slot => ((JsonObjectValue)slot)["status"] is JsonStringValue { Value: "running" }));
            var kinds = written.Snapshot();
            foreach (var (kind, value) in kinds) Equal(NothingRuns(value), kind == "base", $"{kind} for {value.ToJson()}");
            // Bases at the handover, after each sequential tool, after the round, and when the run ends.
            Check(kinds.Count(item => item.Kind == "base") >= 5, $"at least five bases: {string.Join(", ", kinds.Select(item => item.Kind))}");
            Check(kinds.Any(item => item.Kind == "delta"), "a delta");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "starts-calls-the-request-did-not-offer-as-done-and-marks-a-faulted-tools-slot-done-without-an-entry", async () =>
        {
            var setup = Setup();
            // Not strict JSON: the result commit throws and the scheduler faults the task.
            HarnessTests.AddTool(setup.Registry, ToolOf("bad", (_, _, _) =>
                Task.FromResult(new ToolExecutionResult { Content = JsonArrayValue.Empty, Control = new ToolControl { AddTools = [null!] } })));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("ghost", JsonObjectValue.Empty, "g"), FauxToolCall("bad", JsonObjectValue.Empty, "b")],
                stopReason: "toolUse"), FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            var commits = LiveCommits(harness);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var ghostResult = (await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context)).Items.First(entry => entry.Kind == "pi.tool-result").Id.Value;
            var all = commits.Snapshot();
            var handover = all.First(ops => ops.Any(op => op is SetOp set && set.Path.Length > 0 && set.Path[0].Key == "tools"));
            ContainsAll(handover, $$"""[["s",["tools"],[{"callId":"g","name":"ghost","status":"done","entry":{{ghostResult}}},{"callId":"b","name":"bad","taskId":"<number>","status":"pending"}]]]""",
                "handover");
            // The fault cleanup writes only the status.
            Check(all.Any(ops => Json(ops) == """[["s",["tools",1,"status"],"done"]]"""), "fault cleanup: " + string.Join(" | ", all.Select(ops => Json(ops))));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "commits-the-tool-calling-answer-its-tool-tasks-the-generations-wait-and-the-tool-round-in-one-commit", async () =>
        {
            var setup = Setup();
            HarnessTests.AddTool(setup.Registry, ToolOf("noop", (_, _, _) => Task.FromResult(new ToolExecutionResult { Content = JsonArrayValue.Empty })));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("noop", JsonObjectValue.Empty, "a"), FauxToolCall("noop", JsonObjectValue.Empty, "b")],
                stopReason: "toolUse"), FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(await fresh(), setup);
            CommitPublication? handover = null;
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                {
                    if (change.Record.Kind == "pi.live" && change.Value is { } value && value.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: 2 })
                        Interlocked.CompareExchange(ref handover, publication, null);
                }
            });
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var kinds = handover!.Changes.SelectMany(change => change switch
            {
                EntryWrite entry => [entry.Value.Kind],
                TaskWrite task => [task.Value.Kind],
                _ => Array.Empty<string>(),
            }).Order(StringComparer.Ordinal);
            Seq(["pi.assistant", "pi.generation", "pi.tool", "pi.tool"], kinds, "kinds");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "writes-an-aborted-tools-slot-with-field-level-ops", async () =>
        {
            var run = await Drive(fresh);
            await run.Step(api => api.Output("partial\n"));
            await run.Step(api => api.DetailsAsync(O("""{"n":1}"""), Context));
            var taskId = new TaskId((long)N((await FirstSlot(run.Harness))!["taskId"]));
            var before = run.Commits.Count;
            await run.Harness.AbortTaskAsync(taskId, Context);
            static bool IsAbortCommit(ImmutableArray<Op> ops) =>
                ops.Any(op => op is SetOp set && DeltaPath.ToJson(set.Path).ToJson() == """["tools",0,"status"]""");
            await HarnessTests.Eventually(() => run.Commits.Snapshot().Skip(before).Any(IsAbortCommit));
            var abortCommit = run.Commits.Snapshot().Skip(before).First(IsAbortCommit);
            ContainsAll(abortCommit, $$"""[["s",["tools",0,"status"],"done"],["s",["tools",0,"entry"],"<number>"],["d",{{Output}}],["d",["tools",0,"details"]]]""",
                "abort commit");
            Equal(4, abortCommit.Length, "abort commit length");
            await run.Harness.CloseAsync(Context);
        });
    }
}
