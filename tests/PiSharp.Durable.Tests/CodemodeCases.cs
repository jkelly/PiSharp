// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/codemode.test.ts ("durable code mode" as codemode.<case>,
// "durable code mode output and tool search" as codemode.output.<case>). Verbatim apart from the adaptations in HarnessSupport.cs and
// ChatSupport.cs, and these:
// - `openNodeSqliteStorage(path)` is SqliteStorage.OpenAsync over a file in a fresh temporary directory; NodeExecutionEnv is
//   LocalExecutionEnv.
// - The sandbox is PiSharp.Codemode's CodemodeSandbox (64 MiB, engines on threads as the test host sets no worker launcher, as upstream's run
//   on worker threads). Upstream's wrapper of `sandbox.start`, which counts runs and hands each to `vm.onStart`, is
//   CodemodeToolOptions.OnRunStarted.
// - The tools' Parameters are the JSON TypeBox prints for `Type.Object({ text: Type.Optional(Type.String()), id: Type.Optional(Type.Number())
//   })`; a test's own Exit schema likewise.
// - toMatchObject of the details' calls checks the listed fields; `harness.close(context).catch(() => {})` ignores the close's failure.
// - The journal and run documents (CodemodeJournalDoc, CodemodeRunDoc) are DurableCodemode.JournalDoc and RunDoc; a test's
//   `journal.entries[0]!.issuedAfter = 7` and the like are draft writes in a Harness commit.
// - journals-results-that-finish-together-in-one-commit checks how many results each journal commit adds (1, then 49) instead of upstream's
//   count of all journal commits (fewer than 10). The other commits only record acknowledged counts, and their number is timing: a commit
//   here runs off the call's scheduler, so the VM acknowledges results while it is in flight, and those make one more commit after it (5 to
//   54 journal commits in all, by VM and storage speed; 10 on a macOS runner).
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiSharp.Codemode;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Env;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CodemodeCases() => CodemodeTests.Cases();
}

internal static partial class CodemodeTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary><c>Type.Object({ text: Type.Optional(Type.String()), id: Type.Optional(Type.Number()) })</c>.</summary>
    internal static readonly JsonObjectValue Parameters = O("""{"type":"object","properties":{"text":{"type":"string"},"id":{"type":"number"}}}""");

    internal static ToolRegistration Tool(string name, ToolExecute execute, ToolReplay replay = ToolReplay.Unsafe) =>
        Define.Tool(new ToolRegistration { Name = name, Description = name, Parameters = Parameters, Execute = execute, Replay = replay });

    internal static ToolExecutionResult TextResult(string text, bool isError = false) =>
        new() { Output = JsonValue.Array(FauxText(text)), IsError = isError ? true : null };

    internal static string? TextArg(JsonValue args, string key = "text") =>
        ((JsonObjectValue)args).TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;

    internal static string IdArg(JsonValue args) =>
        ((JsonObjectValue)args).TryGetValue("id", out var value) && value is JsonNumberValue number ? number.ToJson() : "";

    internal static readonly JsonObjectValue Done = FauxAssistantMessage("done");

    internal static JsonObjectValue Script(string code) =>
        FauxAssistantMessage([FauxToolCall("codemode", Obj(("code", code)), "s1")], stopReason: "toolUse");

    internal static string SqlitePath() => Path.Combine(Temp("codemode"), "session.sqlite");

    internal static CodemodeSandbox NewSandbox() => new(new CodemodeSandboxOptions { MemoryLimitBytes = 64 * 1024 * 1024 });

    /// <summary>Blocks while <see cref="Block"/> is set, until aborted; counts runs.</summary>
    internal sealed class Holds
    {
        private int runs;
        public volatile bool Block = true;
        public Deferred Reached = new();
        public int Runs => Volatile.Read(ref runs);
        public void Ran() => Interlocked.Increment(ref runs);
    }

    internal sealed class Slows
    {
        private readonly ConcurrentDictionary<long, Deferred> gates = new();
        public SyncList<long> Finished { get; } = new();
        public Deferred Gate(long id) => gates.GetOrAdd(id, _ => new Deferred());
        public void Release(long id) => Gate(id).Resolve();
    }

    /// <summary>
    /// A chat with the code mode tool and test tools (upstream <c>codemodeChat</c>): <c>echo</c> returns <c>echo &lt;text&gt;</c> and counts its
    /// runs by text; <c>hold</c> (replay-safe) waits for an abort while <c>holds.Block</c> is set, then returns <c>held &lt;id&gt;</c>; <c>slow</c>
    /// returns <c>slow &lt;id&gt;</c> once <c>slows.Release(id)</c> was called.
    /// </summary>
    internal sealed class Chat
    {
        private int starts;

        public Chat(IReadOnlyList<CodemodeGlobal>? globals = null, IReadOnlyList<CodemodeNamespace>? namespaces = null)
        {
            Setup = ChatTests.Setup();
            Sandbox = NewSandbox();
            AddTool(Setup.Registry, DurableCodemode.CreateTool(new CodemodeToolOptions
            {
                Sandbox = Sandbox,
                Globals = globals ?? [],
                Namespaces = namespaces ?? [],
                OnRunStarted = run =>
                {
                    Interlocked.Increment(ref starts);
                    OnStart?.Invoke(run);
                },
            }));
            AddTool(Setup.Registry, Tool("echo", (args, _, _) =>
            {
                var text = TextArg(args) ?? "";
                Echo.AddOrUpdate(text, 1, (_, count) => count + 1);
                return Task.FromResult(TextResult($"echo {text}"));
            }));
            AddTool(Setup.Registry, Tool("hold", async (args, _, callContext) =>
            {
                Holds.Ran();
                if (Holds.Block)
                {
                    Holds.Reached.Resolve();
                    await Aborted(callContext.CancellationToken);
                }
                return TextResult($"held {IdArg(args)}");
            }, ToolReplay.Safe));
            AddTool(Setup.Registry, Tool("slow", async (args, _, _) =>
            {
                var id = ((JsonObjectValue)args).TryGetValue("id", out var value) && value is JsonNumberValue number ? (long)number.Value : 0;
                await Slows.Gate(id).Task;
                Slows.Finished.Add(id);
                return TextResult($"slow {IdArg(args)}");
            }));
        }

        public ChatSetup Setup { get; }
        public CodemodeSandbox Sandbox { get; }
        public int VmStarts => Volatile.Read(ref starts);
        public Action<CodemodeRun>? OnStart { get; set; }
        public ConcurrentDictionary<string, int> Echo { get; } = new();
        public Holds Holds { get; } = new();
        public Slows Slows { get; } = new();

        public Dictionary<string, int> EchoCounts() => Echo.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    internal static async Task<TaskId> ScriptTask(IHarness harness) =>
        (await NestedToolsTests.ToolTasks(harness)).First(task => !NestedToolsTests.IsNested(task)).Id;

    internal static async Task<int> NestedCount(IHarness harness) => (await NestedToolsTests.ToolTasks(harness)).Count(NestedToolsTests.IsNested);

    private static readonly Regex Header = new(@"^Script (completed|failed)\nWall time [\d.]+ seconds\nOutput:\n", RegexOptions.CultureInvariant);

    internal sealed record ResultText(string Text, bool IsError);

    /// <summary>The code mode call's result text, without the "Script completed" header and its wall time, which tests of it check
    /// themselves.</summary>
    internal static async Task<ResultText> ResultOf(IConversation root)
    {
        var entry = (await AllEntries(root)).First(candidate => candidate.Kind == "pi.tool-result");
        var message = (JsonObjectValue)entry.Model![0];
        var text = string.Concat(((JsonArrayValue)message["content"]).Cast<JsonObjectValue>().Select(item => S(item["type"]) == "text" ? S(item["text"]) : ""));
        return new ResultText(Header.Replace(text, "", 1), message["isError"] is JsonBooleanValue { Value: true });
    }

    private static void SameText(ResultText expected, ResultText actual, string what)
    {
        Equal(expected.Text, actual.Text, what + " text");
        Equal(expected.IsError, actual.IsError, what + " isError");
    }

    private static void Contains(string text, string part, string what) => Check(text.Contains(part, StringComparison.Ordinal), $"{what}: <{text}> lacks <{part}>");

    private static void SameCounts(Dictionary<string, int> expected, Dictionary<string, int> actual, string what) =>
        Equal(string.Join(",", expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")),
            string.Join(",", actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")), what);

    internal sealed record Restarted(OpenedChat Opened, ResultText Result)
    {
        public IHarness Harness => Opened.Harness;
    }

    /// <summary>
    /// Run <paramref name="code"/> until <c>holds.Reached</c>, close the Harness (a restart), let <paramref name="between"/> change the stored
    /// state, then reopen with <c>hold</c> no longer blocking and wait for the answer (upstream <c>restart</c>).
    /// </summary>
    internal static async Task<Restarted> Restart(Chat chat, string code, Func<IHarness, TaskId, Task>? between = null,
        Func<IHarness, TaskId, Task>? beforeClose = null)
    {
        var path = SqlitePath();
        chat.Setup.Faux.SetResponses(Script(code), Done);
        var opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
        var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
        await chat.Holds.Reached.Task;
        if (beforeClose is not null) await beforeClose(opened.Harness, await ScriptTask(opened.Harness));
        await opened.Harness.CloseAsync(Context);
        chat.Holds.Block = false;
        opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
        if (between is not null) await between(opened.Harness, await ScriptTask(opened.Harness));
        opened.Harness.Resume();
        Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
        return new Restarted(opened, await ResultOf(opened.Root));
    }

    /// <summary>Changes the stored run or journal of the script's task in one commit.</summary>
    internal static Task Change(IHarness harness, TaskId task, Func<DraftObject, DraftObject, Task> change) => harness.CommitAsync(async tx =>
    {
        var run = await tx.DocAsync(DurableCodemode.RunDoc, task);
        var journal = await tx.DocAsync(DurableCodemode.JournalDoc, task);
        await change(run, journal);
        return 0;
    }, Context);

    internal static async Task<JsonObjectValue?> Journal(IHarness harness, TaskId task) => await harness.SnapshotAsync(DurableCodemode.JournalDoc, task, Context);

    private static async Task<bool> IssuedAfter(IHarness harness, TaskId task, long count) =>
        (await Journal(harness, task)) is { } journal && journal["entries"] is JsonArrayValue { Count: > 0 } entries &&
        ((JsonObjectValue)entries[0]).TryGetValue("issuedAfter", out var after) && after is JsonNumberValue { Value: var value } && value == count;

    private static async Task<bool> FirstIssued(IHarness harness, TaskId task, long count) =>
        (await Journal(harness, task)) is { } journal && journal.TryGetValue("firstIssued", out var first) && first is JsonNumberValue { Value: var value } && value == count;

    private static CodemodeGlobal Global(string name, Func<JsonValue?, Task<JsonValue?>> execute) => new(name, (args, _, _) => execute(args));

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "codemode.";

        yield return Case(prefix + "runs-a-script-that-calls-tools-as-nested-calls-and-journals-each-result-before-delivering-it", async () =>
        {
            var chat = new Chat();
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(
                Script("""const a = await tools.echo({ text: "a" }); const b = await tools.echo({ text: "b" }); return [a, b];"""), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameText(new("""["echo a","echo b"]""", false), await ResultOf(root), "result");
            Equal(2, await NestedCount(harness), "nested calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-a-script-after-a-restart-without-running-its-finished-calls-again", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """
                const a = await tools.echo({ text: "a" });
                				const held = await tools.hold({ id: 1 });
                				const b = await tools.echo({ text: "b" });
                				return [a, held, b, Date.now() > 0];
                """);
            SameText(new("""["echo a","held 1","echo b",true]""", false), restarted.Result, "result");
            SameCounts(new() { ["a"] = 1, ["b"] = 1 }, chat.EchoCounts(), "echo");
            // The VM ran twice; hold, replay-safe, reran once after the restart.
            Equal(2, chat.VmStarts, "VM starts");
            Equal(2, chat.Holds.Runs, "hold runs");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "replays-results-in-the-order-they-arrived-not-the-order-the-calls-were-made", async () =>
        {
            var chat = new Chat();
            const string code = """
                const order = [];
                				await Promise.all([tools.slow({ id: 1 }).then(() => order.push(1)), tools.slow({ id: 2 }).then(() => order.push(2))]);
                				await tools.echo({ text: order.join(",") });
                				await tools.hold({});
                				return order;
                """;
            // 2 finishes first, then 1.
            chat.Slows.Release(2);
            _ = Eventually(() => chat.Slows.Finished.Snapshot().Contains(2)).ContinueWith(_ => chat.Slows.Release(1), TaskScheduler.Default);
            var restarted = await Restart(chat, code);
            SameText(new("[2,1]", false), restarted.Result, "result");
            SameCounts(new() { ["2,1"] = 1 }, chat.EchoCounts(), "echo");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "journals-globals-with-their-results-and-hands-a-replay-the-recorded-ones", async () =>
        {
            var counter = 0;
            var chat = new Chat([Global("next", _ => Task.FromResult<JsonValue?>(Interlocked.Increment(ref counter)))]);
            var restarted = await Restart(chat, "const n = await next(); await tools.hold({}); return [n, await next()];");
            // The first next() ran once and was replayed from the journal; the second ran after the restart.
            SameText(new("[1,2]", false), restarted.Result, "result");
            Equal(2, Volatile.Read(ref counter), "counter");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-script-s-clock-and-randomness-across-a-restart", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """
                const before = [Date.now(), Math.random()];
                				await tools.hold({});
                				await tools.echo({ text: String(before[0]) + "/" + String(before[1]) });
                				return "ok";
                """);
            Equal("ok", restarted.Result.Text, "result");
            // One echo: the same arguments, so the rerun admitted no second call.
            Equal("1", string.Join(",", chat.Echo.Values), "echo runs");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-resumed-script-made-a-different-number-of-calls-than-before", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """await tools.echo({ text: "a" }); await tools.hold({});""",
                (opened, task) => Change(opened, task, (_, journal) =>
                {
                    journal.GetArray("entries").GetObject(0).Set("issuedAfter", 7);
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "after result 1 it had made 2 calls, where it had made 7", "result");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-at-once-when-a-resumed-script-waits-without-making-a-call-it-made-before", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """await tools.echo({ text: "a" }); await tools.hold({});""",
                (opened, task) => Change(opened, task, (_, journal) =>
                {
                    journal.GetArray("entries").GetObject(0).Set("id", 99);
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "it waits without making call 99", "result");
            // hold, admitted before the restart, resumed on its own; no call was admitted by the replay.
            Equal(2, await NestedCount(restarted.Harness), "nested calls");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-resumed-script-calls-a-global-with-other-arguments", async () =>
        {
            var chat = new Chat([Global("next", _ => Task.FromResult<JsonValue?>(1))]);
            var restarted = await Restart(chat, "await next(); await tools.hold({});",
                (opened, task) => Change(opened, task, (_, journal) =>
                {
                    journal.GetArray("entries").GetObject(0).GetObject("global").Set("args", "other");
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "call 1 is next", "result");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "admits-no-call-of-a-diverging-replay-even-one-the-script-never-made-before", async () =>
        {
            var chat = new Chat();
            // With k set, the replay makes three extra calls after result 1, which the live run did not make; the count in result 1's
            // acknowledgment shows the divergence only after they were issued.
            var restarted = await Restart(chat, """
                await tools.echo({ text: "a" });
                				const extra = load("k") ?? 0;
                				for (let i = 0; i < extra; i++) tools.echo({ text: "extra" + i });
                				await tools.hold({});
                """,
                (opened, task) => Change(opened, task, (run, _) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":3}"""));
                    return Task.CompletedTask;
                }),
                (opened, task) => Eventually(() => IssuedAfter(opened, task, 2)));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "after result 1 it had made 5 calls, where it had made 2", "result");
            SameCounts(new() { ["a"] = 1 }, chat.EchoCounts(), "echo");
            Equal(2, await NestedCount(restarted.Harness), "nested calls");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-resumed-script-ends-where-it-went-on-before", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """await tools.echo({ text: "a" }); if (load("k")) return "early"; await tools.hold({});""",
                (opened, task) => Change(opened, task, (run, _) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":true}"""));
                    return Task.CompletedTask;
                }),
                // The live run acknowledged result 1 before the restart: it went on after it.
                (opened, task) => Eventually(() => IssuedAfter(opened, task, 2)));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "it ended after result 1, where it went on before", "result");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-resumed-script-ends-before-calls-the-live-run-made-even-without-a-recorded-count", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat,
                """await tools.echo({ text: "a" }); if (load("k")) return "early"; await tools.echo({ text: "b" }); await tools.hold({});""",
                (opened, task) => Change(opened, task, (run, journal) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":true}"""));
                    // As if the live run crashed before recording result 1's count.
                    journal.GetArray("entries").GetObject(0).Delete("issuedAfter");
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "it ended after result 1, where it went on before", "result");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-resumed-script-makes-a-call-an-earlier-attempt-admitted-with-other-arguments", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """await tools.echo({ text: load("k") ?? "a" }); await tools.hold({});""",
                (opened, task) => Change(opened, task, (run, journal) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":"other"}"""));
                    // As if the restart came before the first journal commit: the rerun runs live from the start.
                    journal.GetArray("entries").SetLength(0);
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "different path", "result");
            Contains(restarted.Result.Text, "call 1 is tools.echo", "result");
            SameCounts(new() { ["a"] = 1 }, chat.EchoCounts(), "echo");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "records-a-count-the-live-run-never-recorded-as-soon-as-a-replay-acknowledges-it", async () =>
        {
            var chat = new Chat();
            var path = SqlitePath();
            chat.Setup.Faux.SetResponses(Script("""await tools.echo({ text: "a" }); await tools.hold({});"""), Done);
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await chat.Holds.Reached.Task;
            await opened.Harness.CloseAsync(Context);
            // As if result 1's count was never recorded; hold blocks again after the restart.
            chat.Holds.Reached = new Deferred();
            opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
            var task = await ScriptTask(opened.Harness);
            await Change(opened.Harness, task, (_, journal) =>
            {
                journal.GetArray("entries").GetObject(0).Delete("issuedAfter");
                return Task.CompletedTask;
            });
            opened.Harness.Resume();
            // The replay acknowledges result 1 and goes live; the script waits on hold, and the count is already recorded.
            await chat.Holds.Reached.Task;
            var harness = opened.Harness;
            await Eventually(() => IssuedAfter(harness, task, 2));
            await opened.Harness.CloseAsync(Context);
            chat.Holds.Block = false;
            opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lists-the-script-s-tool-calls-in-its-result-s-details", async () =>
        {
            var chat = new Chat();
            AddTool(chat.Setup.Registry, Tool("fails", (_, _, _) => Task.FromResult(TextResult("permission denied", isError: true))));
            chat.Setup.Faux.SetResponses(
                Script("""await tools.echo({ text: "a" }); try { await tools.fails({}); } catch {} tools.hold({ id: 9 }); return 1;"""), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var entry = (await AllEntries(root)).First(candidate => candidate.Kind == "pi.tool-result");
            var details = (JsonObjectValue)((JsonObjectValue)entry.Model![0])["details"];
            var calls = ((JsonArrayValue)details["calls"]).Cast<JsonObjectValue>().ToList();
            // A call still running, or not yet launched, when the script ended shows as cancelled.
            Equal(3, calls.Count, "calls");
            MatchJson("""{"id":1,"name":"echo","args":"{\"text\":\"a\"}","status":"ok"}""", calls[0], "echo row");
            MatchJson("""{"id":2,"name":"fails","args":"{}","status":"error","error":"permission denied"}""", calls[1], "fails row");
            MatchJson("""{"id":3,"name":"hold","args":"{\"id\":9}","status":"cancelled"}""", calls[2], "hold row");
            Check(calls[0]["durationMs"] is JsonNumberValue { Value: >= 0 }, "echo's duration");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stores-no-outcome-when-the-host-closes-the-sandbox-and-replays-the-script-after-a-restart", async () =>
        {
            var chat = new Chat();
            var path = SqlitePath();
            chat.Setup.Faux.SetResponses(Script("""await tools.echo({ text: "a" }); await tools.hold({}); return "done";"""), Done);
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await chat.Holds.Reached.Task;
            var task = await ScriptTask(opened.Harness);
            // A host shutting down closes the sandbox before the Harness.
            await chat.Sandbox.CloseAsync();
            await Task.Delay(100);
            Check((await opened.Harness.SnapshotAsync(DurableCodemode.RunDoc, task, Context))?.ContainsKey("result") != true, "no stored result");
            await opened.Harness.CloseAsync(Context);
            // The next process has a new sandbox.
            chat.Holds.Block = false;
            AddTool(chat.Setup.Registry, DurableCodemode.CreateTool(new CodemodeToolOptions { Sandbox = NewSandbox() }));
            opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            SameText(new("done", false), await ResultOf(opened.Root), "result");
            SameCounts(new() { ["a"] = 1 }, chat.EchoCounts(), "echo");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-call-still-running-when-the-script-ends", async () =>
        {
            var chat = new Chat();
            chat.Setup.Faux.SetResponses(Script("""tools.hold({}); await tools.echo({ text: "a" }); return 1;"""), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameText(new("1", false), await ResultOf(root), "result");
            var hold = (await NestedToolsTests.ToolTasks(harness)).First(task => NestedToolsTests.IsNested(task) &&
                S(((JsonObjectValue)((JsonObjectValue)task.Input)["call"])["name"]) == "hold");
            Equal(1, chat.Holds.Runs, "hold runs");
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", hold.State.ToJson(), "hold state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "admits-no-call-once-the-script-has-ended-even-one-it-made-before-ending", async () =>
        {
            var chat = new Chat();
            chat.Holds.Block = false;
            var storage = new ControlledStorage(new PiSharp.Durable.Storage.MemoryStorage());
            // Hold the fingerprint commit, and so the admission of the script's call, until the script has ended.
            chat.OnStart = run =>
            {
                var held = storage.HoldCommits();
                _ = run.Result.ContinueWith(_ => held.Release(), TaskScheduler.Default);
            };
            chat.Setup.Faux.SetResponses(Script("""tools.echo({ text: "late" }); return 1;"""), Done);
            var (harness, root) = await OpenChat(storage, chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameText(new("1", false), await ResultOf(root), "result");
            Equal(0, chat.Echo.Count, "echo runs");
            Equal(0, await NestedCount(harness), "nested calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "checks-a-rerun-s-calls-against-an-earlier-attempt-s-before-admitting-any-later-call", async () =>
        {
            var chat = new Chat();
            // With k set, call 1 has other arguments and call 2 is another tool than before; call 3 would be new.
            var restarted = await Restart(chat, """
                const k = load("k");
                				tools.echo({ text: k ?? "a" });
                				if (k) tools.echo({ text: "new" });
                				await tools.hold({});
                """,
                (opened, task) => Change(opened, task, (run, journal) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":"x"}"""));
                    // As if the restart came before the first journal commit.
                    journal.GetArray("entries").SetLength(0);
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            // The first run already made more calls than before.
            Contains(restarted.Result.Text, "its first run made 3 calls, where it had made 2", "result");
            Equal(2, await NestedCount(restarted.Harness), "nested calls");
            SameCounts(new() { ["a"] = 1 }, chat.EchoCounts(), "echo");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-rerun-makes-a-global-call-where-an-earlier-attempt-made-a-tool-call", async () =>
        {
            var chat = new Chat([Global("pick", _ => Task.FromResult<JsonValue?>(1))]);
            var restarted = await Restart(chat, """if (load("k")) { await pick(); await tools.echo({ text: "new" }); } else { await tools.hold({}); }""",
                (opened, task) => Change(opened, task, (run, _) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":true}"""));
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "call 1 is pick(), but was tools.hold({})", "result");
            Equal(0, chat.Echo.Count, "echo runs");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-rerun-s-first-run-makes-fewer-calls-than-an-earlier-attempt-admitted", async () =>
        {
            // Live, the first run admits hold 1 and hold 2. With k set, it makes only hold 1, and after its result a global and a new tool
            // call: the global's ID is hold 2's key, which no check after the release would see.
            var chat = new Chat([Global("pick", _ => Task.FromResult<JsonValue?>(1))]);
            var restarted = await Restart(chat, """
                const k = load("k");
                				const first = tools.hold({ id: 1 });
                				if (!k) tools.hold({ id: 2 });
                				await first;
                				await pick();
                				await tools.echo({ text: "new" });
                """,
                (opened, task) => Change(opened, task, (run, _) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":true}"""));
                    return Task.CompletedTask;
                }),
                (opened, task) => Eventually(() => FirstIssued(opened, task, 2)));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "its first run made 1 calls, where it had made 2", "result");
            Equal(0, chat.Echo.Count, "echo runs");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-call-when-a-rerun-without-journal-entries-ends-in-its-first-run-where-it-went-on-before", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """if (load("k")) { store("x", 1); return "early"; } await tools.hold({});""",
                (opened, task) => Change(opened, task, (run, _) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":true}"""));
                    return Task.CompletedTask;
                }),
                (opened, task) => Eventually(() => FirstIssued(opened, task, 1)));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "it ended after result 0, where it went on before", "result");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "checks-every-call-a-rerun-holds-before-launching-any-of-them", async () =>
        {
            // Live, call 1 is a global that never finishes and call 2 is hold; with k set, call 1 is a new tool call and call 2 holds with
            // other arguments. Checking call 1 alone would launch it before call 2's conflict shows.
            var chat = new Chat([new CodemodeGlobal("forever", async (_, _, callContext) =>
            {
                await Aborted(callContext.CancellationToken);
                return JsonValue.Null;
            })]);
            var restarted = await Restart(chat, """
                const k = load("k");
                				if (k) tools.echo({ text: "new" }); else forever();
                				await tools.hold({ id: k ? 2 : 1 });
                """,
                (opened, task) => Change(opened, task, (run, _) =>
                {
                    run.GetObject("start").Set("store", O("""{"k":true}"""));
                    return Task.CompletedTask;
                }));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, """call 2 is tools.hold({"id":2}), but was tools.hold({"id":1})""", "result");
            Equal(0, chat.Echo.Count, "echo runs");
            Equal(1, await NestedCount(restarted.Harness), "nested calls");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-global-s-undefined-result-undefined-live-and-replayed", async () =>
        {
            var chat = new Chat([Global("nothing", _ => Task.FromResult<JsonValue?>(null))]);
            var restarted = await Restart(chat,
                "const before = await nothing(); await tools.hold({}); return [before === undefined, (await nothing()) === undefined];");
            SameText(new("[true,true]", false), restarted.Result, "result");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-a-failed-tool-call-with-its-error-text-and-structured-output", async () =>
        {
            var chat = new Chat();
            AddTool(chat.Setup.Registry, Tool("fails", (_, _, _) => Task.FromResult(TextResult("permission denied", isError: true))));
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(Script("try { await tools.fails({}); } catch (e) { return [e.message, e.structuredOutput]; }"), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameText(new("""["permission denied","permission denied"]""", false), await ResultOf(root), "result");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resolves-a-schema-tool-s-error-result-that-carries-its-value-and-rejects-one-without-with-its-text", async () =>
        {
            var chat = new Chat();
            var exit = O("""{"type":"object","required":["output","exitCode"],"properties":{"output":{"type":"string"},"exitCode":{"type":"number"}}}""");
            AddTool(chat.Setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "run",
                Description = "run",
                StructuredOutputSchema = exit,
                // Like bash on a nonzero exit: an error result with its value.
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult
                {
                    Output = JsonValue.Array(FauxText("no matches")), StructuredOutput = O("""{"output":"no matches","exitCode":1}"""), IsError = true,
                }),
            }));
            AddTool(chat.Setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "denied",
                Description = "denied",
                StructuredOutputSchema = exit,
                Execute = (_, _, _) => Task.FromResult(TextResult("permission denied", isError: true)),
            }));
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(Script("""
                const r = await tools.run({});
                					let e; try { await tools.denied({}); } catch (error) { e = [error.message, error.structuredOutput ?? null]; }
                					return [r.exitCode, r.output, e];
                """), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameText(new("""[1,"no matches",["permission denied",null]]""", false), await ResultOf(root), "result");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resolves-a-schema-less-tool-with-its-text-as-its-declaration-says-also-when-it-returned-images", async () =>
        {
            var chat = new Chat();
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
            AddTool(chat.Setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "picture",
                Description = "picture",
                Parameters = O("""{"type":"object","required":["caption"],"properties":{"caption":{"type":"boolean"}}}"""),
                Execute = (args, _, _) => Task.FromResult(new ToolExecutionResult
                {
                    Output = JsonValue.Array(
                    [
                        .. ((JsonObjectValue)args)["caption"] is JsonBooleanValue { Value: true } ? [FauxText("a cat")] : Array.Empty<JsonValue>(),
                        Obj(("type", "image"), ("data", png), ("mimeType", "image/png")),
                    ]),
                }),
            }));
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(Script("return [await tools.picture({ caption: true }), await tools.picture({ caption: false })];"), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameText(new("""["a cat",""]""", false), await ResultOf(root), "result");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "delivers-a-result-only-after-its-journal-commit", async () =>
        {
            var storage = new ControlledStorage(new PiSharp.Durable.Storage.MemoryStorage());
            var seen = new SyncList<string>();
            var chat = new Chat(
            [
                Global("first", _ =>
                {
                    seen.Add("first");
                    // The next commit is this result's journal commit: it never lands.
                    storage.FailNextCommit(new InvalidOperationException("lost"));
                    return Task.FromResult<JsonValue?>(1);
                }),
                Global("second", _ =>
                {
                    seen.Add("second");
                    return Task.FromResult<JsonValue?>(2);
                }),
            ]);
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(Script("await first(); await second();"), Done);
            var (harness, root) = await OpenChat(storage, chat.Setup);
            await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Eventually(() => seen.Snapshot().Contains("first"));
            await Task.Delay(100);
            Equal("first", string.Join(",", seen.Snapshot()), "seen");
            try { await harness.CloseAsync(Context); } catch (Exception) { /* the close's failure is ignored, as upstream's catch(() => {}) */ }
        });

        yield return Case(prefix + "refuses-to-resume-when-the-sandbox-s-fingerprint-or-the-code-mode-version-changed", async () =>
        {
            foreach (var field in new[] { "fingerprint", "version" })
            {
                var chat = new Chat();
                var restarted = await Restart(chat, """await tools.hold({}); await tools.echo({ text: "late" });""",
                    (opened, task) => Change(opened, task, (run, _) =>
                    {
                        if (field == "fingerprint") run.GetObject("start").Set("fingerprint", "another sandbox");
                        else run.GetObject("start").Set("version", 0);
                        return Task.CompletedTask;
                    }));
                Equal(true, restarted.Result.IsError, field + " isError");
                Contains(restarted.Result.Text, "cannot resume", field + " result");
                Equal(0, chat.Echo.Count, field + " echo runs");
                await restarted.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "times-a-script-out-across-a-restart-without-admitting-new-calls", async () =>
        {
            var chat = new Chat();
            var restarted = await Restart(chat, """
                // @options: {"timeout_ms": 300}
                				await tools.hold({}); await tools.echo({ text: "late" });
                """, (_, _) => Task.Delay(400));
            Equal(true, restarted.Result.IsError, "isError");
            Contains(restarted.Result.Text, "deadline passed", "result");
            Equal(0, chat.Echo.Count, "echo runs");
            Equal(1, chat.VmStarts, "VM starts");
            await restarted.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "returns-a-decided-outcome-after-a-restart-without-running-the-script-again", async () =>
        {
            var chat = new Chat();
            var path = SqlitePath();
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(Script("""return "first" """), Done);
            // Stop after execute() returned, so after the outcome is stored, and before the result commits.
            var stopped = new Holds();
            AddHooks(chat.Setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = async (call, _, _, hookContext) =>
                {
                    if (S(call["name"]) != "codemode" || !stopped.Block) return null;
                    stopped.Reached.Resolve();
                    await Aborted(hookContext.CancellationToken);
                    return null;
                },
            });
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await stopped.Reached.Task;
            var task = await ScriptTask(opened.Harness);
            Check((await opened.Harness.SnapshotAsync(DurableCodemode.RunDoc, task, Context))?.ContainsKey("result") == true, "the outcome is stored");
            await opened.Harness.CloseAsync(Context);
            stopped.Block = false;
            opened = await OpenChat(await SqliteStorage.OpenAsync(path), chat.Setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            SameText(new("first", false), await ResultOf(opened.Root), "result");
            Equal(1, chat.VmStarts, "VM starts");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-store-values-per-conversation-as-of-a-fork-s-entry", async () =>
        {
            var chat = new Chat();
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(Script("""store("k", 1); return load("k")"""), Done, Script("""return [load("k") ?? null]"""), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("first"), Context)).WaitAsync(Context);
            var firstUser = (await AllEntries(root)).First(entry => entry.Kind == "pi.user");
            await (await root.SubmitAsync(SubmissionDraft.Input("second"), Context)).WaitAsync(Context);
            static string Second(EntryRecord entry) => S(((JsonObjectValue)((JsonArrayValue)((JsonObjectValue)entry.Model![0])["content"])[1])["text"]);
            var texts = (await AllEntries(root)).Where(entry => entry.Kind == "pi.tool-result").Select(Second).ToList();
            Equal("1|[1]", string.Join("|", texts), "texts");
            // A fork at the first input is from before the first script stored anything.
            chat.Setup.Faux.SetResponses(Script("""return [load("k") ?? null]"""), Done);
            var fork = await root.ForkAsync(firstUser.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            await (await fork.SubmitAsync(SubmissionDraft.Input("fork"), Context)).WaitAsync(Context);
            var forkResult = (await AllEntries(fork)).Last(entry => entry.Kind == "pi.tool-result");
            Equal("[null]", Second(forkResult), "fork");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "journals-results-that-finish-together-in-one-commit", async () =>
        {
            var release = new Deferred();
            var waiting = 0;
            var chat = new Chat([Global("wait", async _ =>
            {
                if (Interlocked.Increment(ref waiting) == 50) release.Resolve();
                await release.Task;
                return JsonValue.Null;
            })]);
            chat.Holds.Block = false;
            chat.Setup.Faux.SetResponses(Script("return (await Promise.all(Array.from({ length: 50 }, () => wait()))).length"), Done);
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup);
            // After each journal commit: its entries, and the acknowledged counts it holds (firstIssued and each issuedAfter).
            var journals = new ConcurrentQueue<(int Entries, int Counts)>();
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in publication.Changes)
                    if (change is DocumentCommitChange { Record.Kind: "pi.codemode.journal", Value: { } journal })
                    {
                        var entries = ((JsonArrayValue)journal["entries"]).Cast<JsonObjectValue>().ToList();
                        journals.Enqueue((entries.Count, (journal.ContainsKey("firstIssued") ? 1 : 0) + entries.Count(entry => entry.ContainsKey("issuedAfter"))));
                    }
            });
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameText(new("50", false), await ResultOf(root), "result");
            var commits = journals.ToList();
            Equal((0, 0), commits[0], "the journal's creation");
            var groups = new List<int>();
            for (var index = 1; index < commits.Count; index++)
            {
                if (commits[index].Entries > commits[index - 1].Entries) groups.Add(commits[index].Entries - commits[index - 1].Entries);
                else Check(commits[index].Counts > commits[index - 1].Counts, $"journal commit {index} records nothing");
            }
            // The first result to finish starts a commit, and the other 49 finish while it is in flight and join the next one: two groups, not
            // one commit per result. The commits that only record acknowledged counts are not counted: how many there are depends on how
            // many results the VM processes while each is in flight.
            Equal("1,49", string.Join(",", groups), "results per journal commit");
            await harness.CloseAsync(Context);
        });

        foreach (var output in OutputCases()) yield return output;
    }

    // ─── durable code mode output and tool search ──────────────────────────

    internal sealed record Ran(string Text, JsonObjectValue Message)
    {
        public JsonObjectValue? Details => Message.TryGetValue("details", out var details) ? details as JsonObjectValue : null;
        public string? FullOutputPath => Details?.TryGetValue("fullOutputPath", out var path) == true ? S(path) : null;
        public bool IsError => Message["isError"] is JsonBooleanValue { Value: true };
    }

    /// <summary>The text of a tool result message: its text items, and <c>&lt;type&gt;</c> for others (upstream's join).</summary>
    internal static string ContentText(JsonObjectValue message) => string.Concat(((JsonArrayValue)message["content"]).Cast<JsonObjectValue>()
        .Select(item => item.TryGetValue("text", out var text) ? S(text) : $"<{S(item["type"])}>"));

    /// <summary>Run <paramref name="code"/> once in a chat with a local environment; the result's content and details (upstream
    /// <c>runScript</c>).</summary>
    internal static async Task<Ran> RunScript(string code, Chat? chat = null)
    {
        chat ??= new Chat();
        chat.Holds.Block = false;
        var cwd = Temp("codemode-env");
        chat.Setup.Faux.SetResponses(Script(code), Done);
        var env = new LocalExecutionEnv(cwd);
        var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(SqlitePath()), chat.Setup, (_, _) => Task.FromResult<IExecutionEnv?>(env));
        await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        var entry = (await AllEntries(root)).First(candidate => candidate.Kind == "pi.tool-result");
        var message = (JsonObjectValue)entry.Model![0];
        await harness.CloseAsync(Context);
        return new Ran(ContentText(message), message);
    }

    /// <summary>The process dies after the full output was written and before the result was committed.</summary>
    private sealed class CrashAfterWrite(string cwd, Deferred<string> written) : LocalExecutionEnv(cwd)
    {
        public override async Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context)
        {
            var result = await base.WriteFileAsync(path, content, context);
            written.Resolve(path);
            await HarnessTests.Aborted(context.CancellationToken);
            return result;
        }
    }

    private static IEnumerable<(string, Func<Task>)> OutputCases()
    {
        const string prefix = "codemode.output.";

        yield return Case(prefix + "lays-out-text-items-console-lines-and-the-returned-value-under-a-header", async () =>
        {
            var ran = await RunScript("""text("one"); console.log("log", 1); text({ two: 2 }); return "three";""");
            Check(Header.IsMatch(ran.Text), "header: " + ran.Text);
            Contains(ran.Text, "==> text 1/3 <==\none\n==> text 2/3 <==\n{\"two\":2}\n==> text 3/3 <==\nthree", "text items");
            Contains(ran.Text, "<console_output>\nlog 1\n</console_output>", "console");
        });

        yield return Case(prefix + "cuts-text-past-the-token-budget-to-its-start-and-end-with-the-full-text-in-a-file", async () =>
        {
            var ran = await RunScript("""
                // @options: {"max_output_tokens": 20}
                				text("BEGIN" + "x".repeat(1000) + "END");
                """);
            Contains(ran.Text, "Warning: truncated output", "warning");
            Contains(ran.Text, "BEGIN", "start");
            Contains(ran.Text, "END", "end");
            var path = ran.FullOutputPath!;
            Contains(ran.Text, $"[Full output: {path}", "path");
            Equal("BEGIN" + new string('x', 1000) + "END", await File.ReadAllTextAsync(path), "full output");
        });

        yield return Case(prefix + "keeps-a-result-inside-the-tool-s-output-limits-whatever-max-output-tokens-says-spilling-the-rest", async () =>
        {
            // 6 MiB of text: over the Harness's 4 MiB limit, which would cut it without a spill file.
            var big = await RunScript("""
                // @options: {"max_output_tokens": 2000000}
                				return "x".repeat(6 * 1024 * 1024);
                """);
            Contains(big.Text, "Warning: truncated output", "big warning");
            Check(big.Text.Length <= 1024 * 1024 + 1000, $"big length {big.Text.Length}");
            Equal(6 * 1024 * 1024, (await File.ReadAllTextAsync(big.FullOutputPath!)).Length, "big spill");
            // 200,000 short lines: within the byte limit, over the 100,000 lines one.
            var lines = await RunScript("""
                // @options: {"max_output_tokens": 2000000}
                				return Array.from({ length: 200000 }, () => "a").join("\n");
                """);
            Contains(lines.Text, "Warning: truncated output", "lines warning");
            Check(lines.Text.Split('\n').Length < 100_000, "lines count");
            Check(lines.FullOutputPath is not null, "lines spill");
        });

        yield return Case(prefix + "writes-the-result-s-files-to-the-same-paths-when-it-reruns-after-a-crash", async () =>
        {
            var chat = new Chat();
            chat.Holds.Block = false;
            var cwd = Temp("codemode-env");
            var written = new Deferred<string>();
            var storage = SqlitePath();
            chat.Setup.Faux.SetResponses(Script("// @options: {\"max_output_tokens\": 20}\nreturn \"x\".repeat(1000);"), Done);
            IExecutionEnv crashing = new CrashAfterWrite(cwd, written);
            var opened = await OpenChat(await SqliteStorage.OpenAsync(storage), chat.Setup, (_, _) => Task.FromResult<IExecutionEnv?>(crashing));
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            var first = await written.Task;
            await opened.Harness.CloseAsync(Context);
            IExecutionEnv plain = new LocalExecutionEnv(cwd);
            opened = await OpenChat(await SqliteStorage.OpenAsync(storage), chat.Setup, (_, _) => Task.FromResult<IExecutionEnv?>(plain));
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            var entry = (await AllEntries(opened.Root)).First(candidate => candidate.Kind == "pi.tool-result");
            var details = (JsonObjectValue)((JsonObjectValue)entry.Model![0])["details"];
            Equal(first, S(details["fullOutputPath"]), "fullOutputPath");
            var directory = Path.GetDirectoryName(first)!;
            Equal("output.txt", string.Join(",", Directory.GetFiles(directory).Select(Path.GetFileName)), "files");
            await opened.Harness.CloseAsync(Context);
            Directory.Delete(directory, true);
        });

        yield return Case(prefix + "saves-images-to-files-and-names-them-next-to-the-image", async () =>
        {
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
            var ran = await RunScript($"""image("data:image/png;base64,{png}");""");
            var saved = Regex.Match(ran.Text, @"\[Image saved to (\S+) \(image/png, \d+ bytes\)\]", RegexOptions.CultureInvariant);
            Check(saved.Success, "saved: " + ran.Text);
            Equal(png, Convert.ToBase64String(await File.ReadAllBytesAsync(saved.Groups[1].Value)), "file");
            Check(((JsonArrayValue)ran.Message["content"]).Cast<JsonObjectValue>().Any(item => S(item["type"]) == "image"), "image block");
        });

        yield return Case(prefix + "calls-a-deferred-tool-the-model-is-not-offered-and-finds-it-in-all-tools", async () =>
        {
            var chat = new Chat();
            AddTool(chat.Setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "later",
                Description = "A deferred tool",
                Deferred = true,
                Execute = (_, _, _) => Task.FromResult(TextResult("later ran")),
            }));
            var ran = await RunScript("""return [await tools.later({}), ALL_TOOLS.some((tool) => tool.name === "later")];""", chat);
            Contains(ran.Text, """["later ran",true]""", "result");
        });

        yield return Case(prefix + "reports-a-failed-script-with-its-error-and-the-calls-already-made", async () =>
        {
            var ran = await RunScript("""await tools.echo({ text: "a" }); throw new Error("boom");""");
            Equal(true, ran.IsError, "isError");
            Check(ran.Text.StartsWith("Script failed\n", StringComparison.Ordinal), "header: " + ran.Text);
            Contains(ran.Text, "Error: boom", "error");
            Contains(ran.Text, "Tool calls made before the failure (they are not undone): echo (ok)", "calls");
        });

        yield return Case(prefix + "finds-tools-with-searchtools-describetool-and-describenamespace-and-lists-them-in-all-tools", async () =>
        {
            var chat = new Chat([], [new CodemodeNamespace("github", "mcp__github__") { Description = "GitHub MCP server" }]);
            AddTool(chat.Setup.Registry, Tool("mcp__github__list_issues", (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty })));
            var ran = await RunScript("""
                const found = await searchTools("issues");
                				const inNamespace = await searchTools("echo", { namespace: "github" });
                				return {
                					found: found.map((tool) => tool.name),
                					inNamespace,
                					described: (await describeTool("echo")).includes("echo(args:"),
                					missing: (await describeTool("nope")) ?? null,
                					namespace: await describeNamespace("github"),
                					all: ALL_TOOLS.map((tool) => tool.name).includes("mcp__github__list_issues"),
                				};
                """, chat);
            SameJson("""{"found":["mcp__github__list_issues"],"inNamespace":[],"described":true,"missing":null,"namespace":{"name":"github","description":"GitHub MCP server","tools":["mcp__github__list_issues"]},"all":true}""",
                JsonValue.Parse(Header.Replace(ran.Text, "", 1)), "result");
        });
    }
}
