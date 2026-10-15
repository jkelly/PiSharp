// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-nested-tools.test.ts ("nested tool calls", "tool task
// version 1 records"), as harness.nested-tools.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - Parameters is the JSON TypeBox prints for `Type.Object({ text: Type.Optional(Type.String()) })`; `text()` reads a result's text as
//   upstream's does. A result's toEqual/toMatchObject compare its JSON (NestedToolExecutionResult.ToJson), with "<number>" for
//   expect.any(Number); `not.toHaveProperty("structuredOutput")` is a null StructuredOutput (absent).
// - "stores one document per nested call": upstream patches storage.commit; here a DelegatingStorage records the document creations.
// - "rejects a nested call of an aborted caller": upstream's `tx.setTask` is the internal Transaction.SetTask, through the Harness's own
//   commit (HarnessOwnershipSupport.cs's SetTasks).
// - "stops only the wait when a caller cancels it": `withAbortSignal(cancel.signal, callContext)` is a cancellation of the call's context,
//   cancelled with the same error.
// - "runs a nested call whose repaired arguments set an optional property to undefined": a C# JsonValue cannot hold undefined, so the repair
//   returns what upstream's copyJson({ text: undefined }, { omitUndefinedProperties: true }) leaves, `{}`; the case checks the call runs
//   with it and succeeds, as upstream's does.
// - "gives callers a result with its task ID and only diagnostics for a nested call the scheduler faulted": upstream's details hold a BigInt,
//   which is not JSON, so the result commit throws. A C# result holds only JSON values; the case's result carries a diagnostic whose severity
//   is not a ToolDiagnosticSeverity member, which makes the same commit throw while encoding the stored result.
// - The cases upstream opens on SQLite through openNodeSqliteStorage use SqliteStorage in a fresh temporary directory.
// - Upstream runs the others on MemoryStorage; so do they here.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using static OwnershipTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessNestedToolsCases() => NestedToolsTests.Cases("harness.nested-tools.");
}

/// <summary>The harness.nested-tools.* cases (upstream harness-nested-tools.test.ts).</summary>
internal static class NestedToolsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary><c>Type.Object({ text: Type.Optional(Type.String()) })</c>.</summary>
    internal static readonly JsonObjectValue Parameters = O("""{"type":"object","properties":{"text":{"type":"string"}}}""");

    internal static ToolRegistration Tool(string name, ToolExecute execute, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration { Name = name, Description = name, Parameters = Parameters, Execute = execute });
        return extra is null ? tool : extra(tool);
    }

    /// <summary>Returns <c>echo &lt;text&gt;</c> and counts its runs.</summary>
    internal sealed class EchoTool
    {
        private int runs;

        public EchoTool(Func<ToolRegistration, ToolRegistration>? extra = null)
        {
            Registration = Tool("echo", (args, _, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText($"echo {Arg(args)}")) });
            }, extra);
        }

        public ToolRegistration Registration { get; }
        public int Runs => Volatile.Read(ref runs);
    }

    internal static string Arg(JsonValue args, string key = "text") =>
        ((JsonObjectValue)args).TryGetValue(key, out var value) && value is JsonStringValue { Value: var text } ? text : "";

    internal static JsonObjectValue Call(string name, JsonObjectValue? args = null, string id = "c1") =>
        FauxAssistantMessage([FauxToolCall(name, args ?? JsonObjectValue.Empty, id)], stopReason: "toolUse");

    internal static readonly JsonObjectValue Done = FauxAssistantMessage("done");

    internal static List<JsonObjectValue> Results(IEnumerable<EntryRecord> entries) =>
        [.. entries.Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0])];

    /// <summary>The value a schema-less nested call gives programs, when a string.</summary>
    internal static string Text(NestedToolExecutionResult? result) => result?.StructuredOutput is JsonStringValue { Value: var text } ? text : "";

    /// <summary>The text of a result message.</summary>
    internal static string Text(JsonObjectValue? message) => message is null ? "" :
        string.Join("|", ((JsonArrayValue)message["content"]).Cast<JsonObjectValue>().Select(item => S(item["type"]) == "text" ? S(item["text"]) : ""));

    internal static async Task<List<TaskRecord>> ToolTasks(IHarness harness) =>
        [.. (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.tool" }, 100), Context)).Items];

    internal static bool IsNested(TaskRecord task) => task.Input is JsonObjectValue input && input.TryGetValue("kind", out var kind) && S(kind) == "nested";

    internal sealed record Ran(IHarness Harness, IConversation Root, SubmissionStatus Status, List<EntryRecord> Entries);

    internal static async Task<Ran> RunOnce(ChatSetup setup, params FauxStep[] responses)
    {
        setup.Faux.SetResponses(responses);
        var (harness, root) = await OpenChat(new MemoryStorage(), setup);
        var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        return new Ran(harness, root, settled.Status, await AllEntries(root));
    }

    internal static JsonValue OutcomeOf(TaskRecord task) => ((SettlingTaskState)task.State).Outcome.ToJson();

    private static string SqlitePath(string name) => Path.Combine(Temp(name), "session.sqlite");

    /// <summary>Observes a call left running (upstream's <c>.catch(() => {})</c>).</summary>
    private static void Ignore(Task task) =>
        task.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>A storage that records the kind and key of every document it creates.</summary>
    private sealed class CreationsStorage(IStorage inner) : DelegatingStorage(inner)
    {
        public SyncList<(string Kind, string? Key)> Created { get; } = new();

        public override Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
        {
            foreach (var write in writes)
                if (write is DocumentCreateWrite create) Created.Add((create.Record.Kind, create.Record.Key));
            return base.CommitAsync(writes, context);
        }
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "stores-one-document-per-nested-call-keyed-by-its-key-written-only-by-that-call", async () =>
        {
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await Task.WhenAll(api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext),
                    api.ExecuteToolAsync("echo", Obj(("text", "b")), callContext, new NestedCallOptions { Key = "named" }));
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var storage = new CreationsStorage(new MemoryStorage());
            var (harness, root) = await OpenChat(storage, setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var nested = storage.Created.Snapshot().Where(write => write.Kind.StartsWith("pi.tool.", StringComparison.Ordinal)).ToList();
            Seq([("pi.tool.nested-call", (string?)"1"), ("pi.tool.nested-call", "named")], nested, "nested documents");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-a-nested-call-of-an-aborted-caller-and-leaves-no-document-for-it", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, new EchoTool().Registration);
            var started = new Deferred();
            var marked = new Deferred();
            Exception? rejected = null;
            TaskId? caller = null;
            AddTool(setup.Registry, Tool("late", async (_, api, _) =>
            {
                caller = api.TaskId;
                started.Resolve();
                await marked.Task;
                // Its context is aborted too, so pass one that is not: the admission itself must refuse.
                try
                {
                    await api.ExecuteToolAsync("echo", Obj(("text", "a")), Context);
                }
                catch (Exception error)
                {
                    rejected = error;
                }
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("late"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            await SetTasks(harness, root, async tx =>
            {
                var record = (await tx.GetTaskAsync(caller!.Value))!;
                tx.SetTask(record with { AbortRequested = true });
            });
            marked.Resolve();
            await submission.WaitAsync(Context);
            Check(rejected is not null && System.Text.RegularExpressions.Regex.IsMatch(rejected.ToString(), "abort mark|aborted|has settled"),
                "rejected: " + rejected);
            Equal<JsonObjectValue?>(null, await harness.SnapshotAsync(HarnessTasks.NestedCallDoc, caller!.Value, "1", Context), "no document");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-a-nested-call-as-its-own-tool-task-and-returns-its-result-to-the-caller-not-the-transcript", async () =>
        {
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            NestedToolExecutionResult? nested = null;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                nested = await api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext);
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"got {Text(nested)}")) };
            }));
            var (harness, root, status, entries) = await RunOnce(setup, Call("batch"), Done);
            Equal(SubmissionStatus.Done, status, "status");
            var tasks = await ToolTasks(harness);
            var (parent, child) = (tasks[0], tasks[1]);
            MatchExactly(Obj(("taskId", child.Id.ToJson()), ("structuredOutput", "echo a"), ("isError", false), ("diagnostics", JsonArrayValue.Empty),
                ("durationMs", "<number>")).ToJson(), nested!.ToJson(), "nested");
            // One result entry: the model-issued call's, which records nothing of its nested calls.
            Seq(["c1|got echo a"], Results(entries).Select(result => $"{S(result["toolCallId"])}|{Text(result)}"), "results");
            Check(!Results(entries)[0].ContainsKey("nestedCalls"), "no nestedCalls");
            MatchJson("""{"kind":"model","callId":"c1"}""", parent.Input, "parent input");
            Equal(parent.Id, child.Owner, "owner");
            SameJson(Obj(("kind", "nested"), ("parent", parent.Id.ToJson()), ("parentCallId", "c1"), ("key", "1"),
                ("call", Obj(("type", "toolCall"), ("id", "c1/1"), ("name", "echo"), ("arguments", Obj(("text", "a")))))).ToJson(), child.Input, "child input");
            // The receipt stays small: the result lived in the caller's documents, which retired with it.
            SameJson("""{"status":"terminal","outcome":{"status":"completed","result":{"kind":"nested"}}}""", child.State.ToJson(), "child state");
            Equal<JsonObjectValue?>(null, await harness.SnapshotAsync(HarnessTasks.NestedCallDoc, parent.Id, "1", Context), "retired document");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "gives-each-nested-call-its-own-task-and-call-id", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<(TaskId TaskId, string CallId)>();
            AddTool(setup.Registry, Tool("who", (_, api, _) =>
            {
                seen.Add((api.TaskId, api.CallId));
                return Task.FromResult(new ToolExecutionResult());
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await Task.WhenAll(api.ExecuteToolAsync("who", JsonObjectValue.Empty, callContext, new NestedCallOptions { Key = "a" }),
                    api.ExecuteToolAsync("who", JsonObjectValue.Empty, callContext, new NestedCallOptions { Key = "b" }));
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Seq(["c1/a", "c1/b"], seen.Snapshot().Select(each => each.CallId).Order(StringComparer.Ordinal), "call IDs");
            Equal(2, seen.Snapshot().Select(each => each.TaskId).Distinct().Count(), "tasks");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-the-tool-hooks-on-nested-calls-which-see-their-parent", async () =>
        {
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            var parents = new SyncList<JsonValue?>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (hookCall, _, _) =>
                {
                    var parent = hookCall.TryGetValue("parent", out var value) ? value : null;
                    parents.Add(parent);
                    return Task.FromResult(parent is not null && Arg(hookCall["arguments"]) == "secret" ? new BeforeToolDecision(Block: "no secrets") : null);
                },
                AfterTool = (hookCall, result, _, _) => Task.FromResult(hookCall.ContainsKey("parent")
                    ? result with { Output = JsonValue.Array(FauxText("rewritten")) } : null),
            });
            var nested = new SyncList<NestedToolExecutionResult>();
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                nested.Add(await api.ExecuteToolAsync("echo", Obj(("text", "secret")), callContext));
                nested.Add(await api.ExecuteToolAsync("echo", Obj(("text", "public")), callContext));
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            var parent = (await ToolTasks(ran.Harness))[0];
            var expectedParent = Obj(("taskId", parent.Id.ToJson()), ("callId", "c1")).ToJson();
            Seq(["undefined", expectedParent, expectedParent], parents.Snapshot().Select(value => value?.ToJson() ?? "undefined"), "parents");
            MatchJson("""{"isError":true,"diagnostics":[{"severity":"error","code":"blocked","message":"Tool call blocked: no secrets"}]}""",
                nested.Snapshot()[0].ToJson(), "blocked");
            Equal("rewritten", Text(nested.Snapshot()[1]), "rewritten");
            Equal(1, echo.Runs, "runs");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "returns-an-error-result-for-an-unavailable-tool-and-invalid-arguments", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, new EchoTool().Registration);
            var nested = new SyncList<NestedToolExecutionResult>();
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                nested.Add(await api.ExecuteToolAsync("missing", JsonObjectValue.Empty, callContext));
                nested.Add(await api.ExecuteToolAsync("echo", Obj(("text", Obj(("not", "a string")))), callContext));
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Seq(["True tool_unavailable", "True invalid_arguments"], nested.Snapshot().Select(result => $"{result.IsError} {result.Diagnostics[0].Code}"), "results");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "returns-the-call-a-key-already-names-and-rejects-reusing-the-key-for-another-call", async () =>
        {
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            NestedToolExecutionResult? reused = null;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                var options = new NestedCallOptions { Key = "k" };
                await api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext, options);
                reused = await api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext, options);
                await api.ExecuteToolAsync("echo", Obj(("text", "b")), callContext, options);
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Equal(1, echo.Runs, "runs");
            Equal("echo a", Text(reused), "reused");
            Contains(Text(Results(ran.Entries)[0]), "Nested call c1/k was already made with another tool or other arguments", "result");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "matches-a-reused-key-s-arguments-regardless-of-key-order", async () =>
        {
            var setup = Setup();
            var runs = 0;
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "pair",
                Description = "pair",
                Parameters = O("""{"type":"object","required":["a","b"],"properties":{"a":{"type":"string"},"b":{"type":"string"}}}"""),
                Execute = (args, _, _) =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText(Arg(args, "a") + Arg(args, "b"))) });
                },
            }));
            var nested = new SyncList<NestedToolExecutionResult>();
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                var options = new NestedCallOptions { Key = "k" };
                nested.Add(await api.ExecuteToolAsync("pair", Obj(("a", "x"), ("b", "y")), callContext, options));
                nested.Add(await api.ExecuteToolAsync("pair", Obj(("b", "y"), ("a", "x")), callContext, options));
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Equal(1, runs, "runs");
            Seq(["xy", "xy"], nested.Snapshot().Select(Text), "results");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-the-nested-calls-a-caller-left-running-when-it-returns-keeping-their-details", async () =>
        {
            var setup = Setup();
            var running = new Deferred();
            AddTool(setup.Registry, Tool("hang", async (_, api, callContext) =>
            {
                api.Output("partial\n");
                await api.DetailsAsync(Obj(("step", 1)), callContext);
                running.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                var abandoned = api.ExecuteToolAsync("hang", JsonObjectValue.Empty, callContext);
                Ignore(abandoned);
                await running.Task;
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText("returned early")) };
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            var ends = new SyncList<ToolExecutionEndEvent>();
            stream.Start((batch, _) =>
            {
                foreach (var item in batch) if (item is ToolExecutionEndEvent end) ends.Add(end);
                return Task.CompletedTask;
            });
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            await harness.WaitForIdleAsync(Context);
            await stream.StopAsync();
            Equal("returned early", Text(Results(await AllEntries(root))[0]), "result");
            var child = (await ToolTasks(harness))[1];
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted","result":{"kind":"nested"}}}""", child.State.ToJson(), "child state");
            // The aborted call's result keeps its details; programs get no partial output, which only the model read.
            var childEnd = ends.Snapshot().First(end => end.ToolCallId == "c1/1");
            MatchJson("""{"result":{"isError":true,"details":{"step":1}}}""", childEnd.ToJson(), "child end");
            Equal<JsonValue?>(null, childEnd.Result!.StructuredOutput, "no structuredOutput");
            // The nested call ends, with its result, before its caller.
            Seq(["c1/1 True", "c1 False"], ends.Snapshot().Select(end => $"{end.ToolCallId} {end.ToJson().ContainsKey("result")}"), "ends");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "records-a-nested-call-s-usage-under-its-tool", async () =>
        {
            var setup = Setup();
            var usage = O("""{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":3,"cost":{"input":0.1,"output":0.2,"cacheRead":0,"cacheWrite":0,"total":0.3}}""");
            AddTool(setup.Registry, Tool("paid", (_, _, _) => Task.FromResult(new ToolExecutionResult { Usage = usage })));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("paid", JsonObjectValue.Empty, callContext);
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            SameJson(Obj(("paid", usage)).ToJson(), (await ran.Harness.SnapshotAsync(UsageLedger.UsageDoc, ran.Root.Id, Context))?["tools"], "usage");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-nested-calls-in-pi-live-nestedtools-and-as-tool-events-with-their-parent", async () =>
        {
            var setup = Setup();
            var release = new Deferred();
            var running = new Deferred();
            AddTool(setup.Registry, Tool("slow", async (_, api, callContext) =>
            {
                api.Output("working\n");
                await api.DetailsAsync(Obj(("step", 1)), callContext);
                running.Resolve();
                await release.Task;
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText("slow done")) };
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                var result = await api.ExecuteToolAsync("slow", JsonObjectValue.Empty, callContext);
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText(Text(result))) };
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            SameJson("[]", stream.Snapshot.NestedTools, "snapshot nestedTools");
            var events = new SyncList<AgentEvent>();
            stream.Start((batch, _) =>
            {
                foreach (var item in batch) events.Add(item);
                return Task.CompletedTask;
            });
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await running.Task;
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
            MatchExactly("""[{"callId":"c1/1","parentCallId":"c1","parentTaskId":"<number>","name":"slow","taskId":"<number>","arguments":{},"status":"running","output":"working\n","details":{"step":1}}]""",
                live?["nestedTools"], "nestedTools");
            release.Resolve();
            await submission.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await stream.StopAsync();
            var all = events.Snapshot();
            Seq(["tool_execution_start c1 undefined", "tool_execution_start c1/1 c1", "tool_execution_end c1/1 c1", "tool_execution_end c1 undefined"],
                all.OfType<ToolEvent>().Where(item => item is not ToolExecutionUpdateEvent)
                    .Select(item => $"{item.Type} {item.ToolCallId} {item.ParentToolCallId ?? "undefined"}"), "tool events");
            var nestedEnd = all.OfType<ToolExecutionEndEvent>().First(item => item.ToolCallId == "c1/1");
            MatchJson("""{"result":{"structuredOutput":"slow done","isError":false}}""", nestedEnd.ToJson(), "nested end");
            // Events name the call's task and, for a nested call, the calling one.
            var tasks = await ToolTasks(harness);
            var (parent, child) = (tasks[0], tasks[1]);
            var parentEnd = all.OfType<ToolExecutionEndEvent>().First(item => item.ToolCallId == "c1");
            Equal<TaskId?>(child.Id, nestedEnd.TaskId, "nested end taskId");
            Equal<TaskId?>(parent.Id, nestedEnd.ParentTaskId, "nested end parentTaskId");
            Equal<TaskId?>(parent.Id, parentEnd.TaskId, "parent end taskId");
            Check(!parentEnd.ToJson().ContainsKey("parentTaskId"), "parent end has no parentTaskId");
            Check(all.OfType<ToolExecutionUpdateEvent>().Any(item => item.ToolCallId == "c1/1" && item.ParentToolCallId == "c1"), "a nested update");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-nested-calls-with-their-caller", async () =>
        {
            var setup = Setup();
            var running = new Deferred();
            AddTool(setup.Registry, Tool("hang", async (_, _, callContext) =>
            {
                running.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("hang", JsonObjectValue.Empty, callContext);
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await running.Task;
            await root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await submission.WaitAsync(Context)).Status, "status");
            var tasks = await ToolTasks(harness);
            MatchJson("""{"status":"aborted","result":{"kind":"nested"}}""", OutcomeOf(tasks[1]), "child");
            MatchJson("""{"status":"aborted","result":{"kind":"model"}}""", OutcomeOf(tasks[0]), "parent");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-running-nested-call-when-its-caller-throws", async () =>
        {
            var setup = Setup();
            var running = new Deferred();
            AddTool(setup.Registry, Tool("hang", async (_, _, callContext) =>
            {
                running.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                Ignore(api.ExecuteToolAsync("hang", JsonObjectValue.Empty, callContext));
                await running.Task;
                throw new InvalidOperationException("caller failed");
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Equal(SubmissionStatus.Done, ran.Status, "status");
            Contains(Text(Results(ran.Entries)[0]), "caller failed", "result");
            var child = (await ToolTasks(ran.Harness))[1];
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", child.State.ToJson(), "child");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reattaches-a-replay-safe-caller-to-the-nested-calls-it-made-before-a-restart", async () =>
        {
            var path = SqlitePath("nested-tools");
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            var blocked = new Deferred();
            var runs = 0;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                var run = Interlocked.Increment(ref runs);
                var first = await api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext);
                if (run == 1)
                {
                    blocked.Resolve();
                    await Aborted(callContext.CancellationToken);
                }
                // A call new to the rerun: its slot, after the reattached one's, is found and finishes.
                var second = await api.ExecuteToolAsync("echo", Obj(("text", "b")), callContext);
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"run {run}: {Text(first)}, {Text(second)}")) };
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await blocked.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            Equal(2, runs, "caller runs");
            Equal(2, echo.Runs, "echo runs");
            Equal("run 2: echo a, echo b", Text(Results(await AllEntries(opened.Root))[0]), "result");
            var tasks = await ToolTasks(opened.Harness);
            Equal(2, tasks.Count(IsNested), "nested tasks");
            MatchJson("""{"result":{"kind":"nested"}}""", OutcomeOf(tasks[1]), "nested outcome");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stops-only-the-wait-when-a-caller-cancels-it-the-call-runs-on-and-its-key-returns-its-result", async () =>
        {
            var setup = Setup();
            DurableCancellation? cancel = null;
            var release = new Deferred();
            var attempts = new SyncList<string>();
            NestedToolExecutionResult? retry = null;
            AddTool(setup.Registry, Tool("slow", async (_, _, _) =>
            {
                cancel!.Cancel(new InvalidOperationException("cancelled"));
                await release.Task;
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText("slow done")) };
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                cancel = callContext.WithCancel();
                var options = new NestedCallOptions { Key = "same" };
                try
                {
                    await api.ExecuteToolAsync("slow", JsonObjectValue.Empty, cancel.Context, options);
                    attempts.Add("first done");
                }
                catch (Exception)
                {
                    attempts.Add("first cancelled");
                }
                release.Resolve();
                retry = await api.ExecuteToolAsync("slow", JsonObjectValue.Empty, callContext, options);
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Seq(["first cancelled"], attempts.Snapshot(), "attempts");
            MatchJson("""{"isError":false,"structuredOutput":"slow done"}""", retry!.ToJson(), "retry");
            Equal(2, (await ToolTasks(ran.Harness)).Count, "tasks");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "admits-one-nested-call-for-a-key-used-by-concurrent-calls", async () =>
        {
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            NestedToolExecutionResult[] both = [];
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                var options = new NestedCallOptions { Key = "k" };
                both = await Task.WhenAll(api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext, options),
                    api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext, options));
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Equal(1, echo.Runs, "runs");
            Seq(["echo a", "echo a"], both.Select(Text), "results");
            Equal(2, (await ToolTasks(ran.Harness)).Count, "tasks");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-left-running-nested-calls-of-a-caller-interrupted-while-it-cleaned-them-up", async () =>
        {
            var path = SqlitePath("nested-tools");
            var setup = Setup();
            var running = new Deferred();
            var cleaning = new Deferred();
            var release = new Deferred();
            var hangRuns = 0;
            AddTool(setup.Registry, Tool("hang", async (_, api, callContext) =>
            {
                Interlocked.Increment(ref hangRuns);
                api.Output("partial\n");
                await api.DetailsAsync(Obj(("step", 1)), callContext);
                running.Resolve();
                try { await Aborted(callContext.CancellationToken); } catch (OperationCanceledException) { }
                // The caller's cleanup waits for this abort; hold it there until the Harness closes.
                cleaning.Resolve();
                await release.Task;
                throw new InvalidOperationException("aborted");
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                Ignore(api.ExecuteToolAsync("hang", JsonObjectValue.Empty, callContext));
                await running.Task;
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText("returned early")) };
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await cleaning.Task;
            var closed = opened.Harness.CloseAsync(Context);
            release.Resolve();
            await closed;

            opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "status");
            await opened.Harness.WaitForIdleAsync(Context);
            // The caller is replay-unsafe: it reports the interruption, and its cleanup aborts the nested call.
            var result = Results(await AllEntries(opened.Root))[0];
            MatchJson("""{"toolCallId":"c1","isError":true}""", result, "result");
            Contains(Text(result), "Tool batch was interrupted", "result text");
            var child = (await ToolTasks(opened.Harness))[1];
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", child.State.ToJson(), "child");
            Equal(1, hangRuns, "hang runs");
            SameJson("{}", await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context), "live");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-a-nested-call-whose-admission-was-still-committing-when-its-caller-returned", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, new EchoTool().Registration);
            AddTool(setup.Registry, Tool("hang", async (_, _, callContext) =>
            {
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("echo", Obj(("text", "warm")), callContext, new NestedCallOptions { Key = "warm" });
                Ignore(api.ExecuteToolAsync("hang", JsonObjectValue.Empty, callContext, new NestedCallOptions { Key = "late" }));
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Equal(SubmissionStatus.Done, ran.Status, "status");
            var late = (await ToolTasks(ran.Harness)).First(task => IsNested(task) &&
                S(((JsonObjectValue)((JsonObjectValue)task.Input)["call"])["name"]) == "hang");
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", late.State.ToJson(), "late");
            SameJson("{}", await ran.Harness.SnapshotAsync(Live.LiveDoc, ran.Root.Id, Context), "live");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-every-left-running-nested-call-before-waiting-for-any", async () =>
        {
            var setup = Setup();
            var bAborted = new Deferred();
            var startedA = new Deferred();
            var startedB = new Deferred();
            // A only stops once B was told to stop.
            AddTool(setup.Registry, Tool("a", async (_, _, _) =>
            {
                startedA.Resolve();
                await bAborted.Task;
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("b", async (_, _, callContext) =>
            {
                startedB.Resolve();
                try { await Aborted(callContext.CancellationToken); } catch (OperationCanceledException) { }
                bAborted.Resolve();
                throw new InvalidOperationException("aborted");
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                Ignore(api.ExecuteToolAsync("a", JsonObjectValue.Empty, callContext, new NestedCallOptions { Key = "a" }));
                Ignore(api.ExecuteToolAsync("b", JsonObjectValue.Empty, callContext, new NestedCallOptions { Key = "b" }));
                await Task.WhenAll(startedA.Task, startedB.Task);
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            Equal(SubmissionStatus.Done, ran.Status, "status");
            Seq([DurableTaskStatus.Terminal, DurableTaskStatus.Terminal], (await ToolTasks(ran.Harness)).Where(IsNested).Select(task => task.State.Status), "nested");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-explicit-keys-that-are-empty-have-a-slash-are-proto-or-are-positive-integers", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, new EchoTool().Registration);
            var errors = new SyncList<string>();
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                foreach (var key in new[] { "", "a/b", "__proto__", "7" })
                {
                    try { await api.ExecuteToolAsync("echo", JsonObjectValue.Empty, callContext, new NestedCallOptions { Key = key }); }
                    catch (Exception error) { errors.Add(error.Message); }
                }
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            const string rule = "must be non-empty, without \"/\", not \"__proto__\", and not a positive integer";
            Seq([$"Nested call key \"\" {rule}", $"Nested call key \"a/b\" {rule}", $"Nested call key \"__proto__\" {rule}", $"Nested call key \"7\" {rule}"],
                errors.Snapshot(), "errors");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-nested-calls-of-nested-calls-and-ends-them-children-first", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, new EchoTool().Registration);
            AddTool(setup.Registry, Tool("inner", async (_, api, callContext) =>
            {
                var result = await api.ExecuteToolAsync("echo", Obj(("text", "deep")), callContext, new NestedCallOptions { Key = "x" });
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText(Text(result))) };
            }));
            AddTool(setup.Registry, Tool("outer", async (_, api, callContext) =>
            {
                var result = await api.ExecuteToolAsync("inner", JsonObjectValue.Empty, callContext);
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText(Text(result))) };
            }));
            setup.Faux.SetResponses(Call("outer"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            var tools = new SyncList<string>();
            stream.Start((batch, _) =>
            {
                foreach (var item in batch)
                    if (item is ToolExecutionStartEvent or ToolExecutionEndEvent)
                    {
                        var tool = (ToolEvent)item;
                        tools.Add($"{tool.Type} {tool.ToolCallId} {tool.ParentToolCallId ?? "undefined"}");
                    }
                return Task.CompletedTask;
            });
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await stream.StopAsync();
            Equal("echo deep", Text(Results(await AllEntries(root))[0]), "result");
            var tasks = await ToolTasks(harness);
            var (outer, inner, deep) = (tasks[0], tasks[1], tasks[2]);
            Equal(outer.Id, inner.Owner, "inner owner");
            Equal(inner.Id, deep.Owner, "deep owner");
            Seq([
                "tool_execution_start c1 undefined",
                "tool_execution_start c1/1 c1",
                "tool_execution_start c1/1/x c1/1",
                "tool_execution_end c1/1/x c1/1",
                "tool_execution_end c1/1 c1",
                "tool_execution_end c1 undefined",
            ], tools.Snapshot(), "tool events");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-the-output-of-several-running-nested-calls-and-shows-them-to-a-late-subscriber", async () =>
        {
            var setup = Setup();
            var release = new Deferred();
            var running = new Dictionary<string, Deferred> { ["one"] = new(), ["two"] = new() };
            var wrote = new Dictionary<string, Deferred> { ["one"] = new(), ["two"] = new() };
            AddTool(setup.Registry, Tool("slow", async (args, api, callContext) =>
            {
                var name = Arg(args);
                running[name].Resolve();
                await wrote[name].Task;
                api.Output($"{name}\n");
                await api.DetailsAsync(Obj(("name", name)), callContext);
                await release.Task;
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await Task.WhenAll(api.ExecuteToolAsync("slow", Obj(("text", "one")), callContext),
                    api.ExecuteToolAsync("slow", Obj(("text", "two")), callContext));
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Task.WhenAll(running["one"].Task, running["two"].Task);
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            Seq(["c1/1 running", "c1/2 running"], stream.Snapshot.NestedTools.Cast<JsonObjectValue>().Select(slot => $"{S(slot["callId"])} {S(slot["status"])}"),
                "snapshot nestedTools");
            var updates = new SyncList<string>();
            stream.Start((batch, _) =>
            {
                foreach (var item in batch)
                    if (item is ToolExecutionUpdateEvent { Output: { } output } update) updates.Add($"{update.ToolCallId} {output.ToJson().ToJson()}");
                return Task.CompletedTask;
            });
            // The second slot writes first, so a wrong slot index in the update path would show.
            wrote["two"].Resolve();
            await Eventually(() => updates.Count == 1);
            wrote["one"].Resolve();
            await Eventually(() => updates.Count == 2);
            release.Resolve();
            await submission.WaitAsync(Context);
            await stream.StopAsync();
            Seq(["c1/2 {\"set\":\"two\\n\"}", "c1/1 {\"set\":\"one\\n\"}"], updates.Snapshot(), "updates");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "commits-only-the-status-of-a-nested-call-made-with-progress-false-and-keeps-its-output-in-the-result", async () =>
        {
            var setup = Setup();
            var running = new Deferred();
            var release = new Deferred();
            AddTool(setup.Registry, Tool("chatty", async (_, api, callContext) =>
            {
                api.Output("line\n");
                api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "note"));
                await api.DetailsAsync(Obj(("step", 1)), callContext);
                running.Resolve();
                await release.Task;
                return new ToolExecutionResult();
            }));
            NestedToolExecutionResult? result = null;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                result = await api.ExecuteToolAsync("chatty", JsonObjectValue.Empty, callContext, new NestedCallOptions { Progress = false });
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            // DetailsAsync resolved before this, so a progress commit would have landed already.
            await running.Task;
            var slot = ((JsonArrayValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["nestedTools"])[0];
            MatchExactly("""{"callId":"c1/1","parentCallId":"c1","parentTaskId":"<number>","name":"chatty","taskId":"<number>","arguments":{},"status":"running"}""",
                slot, "slot");
            release.Resolve();
            await submission.WaitAsync(Context);
            MatchJson("""{"structuredOutput":"line\n","details":{"step":1},"diagnostics":[{"severity":"info","message":"note"}]}""", result!.ToJson(), "result");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "loses-the-running-output-of-a-nested-call-made-with-progress-false-when-it-is-aborted", async () =>
        {
            var setup = Setup();
            var running = new Deferred();
            AddTool(setup.Registry, Tool("chatty", async (_, api, callContext) =>
            {
                api.Output("line\n");
                api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "note"));
                await api.DetailsAsync(Obj(("step", 1)), callContext);
                running.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                Ignore(api.ExecuteToolAsync("chatty", JsonObjectValue.Empty, callContext, new NestedCallOptions { Progress = false }));
                await running.Task;
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            var ends = new SyncList<ToolExecutionEndEvent>();
            stream.Start((batch, _) =>
            {
                foreach (var item in batch) if (item is ToolExecutionEndEvent end) ends.Add(end);
                return Task.CompletedTask;
            });
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await stream.StopAsync();
            // The abort's result is built from the slot, which never got the output.
            var result = ends.Snapshot().First(end => end.ToolCallId == "c1/1").Result!;
            Equal(true, result.IsError, "isError");
            Equal<JsonValue?>(null, result.StructuredOutput, "structuredOutput");
            Equal<JsonValue?>(null, result.Details, "details");
            Seq(["aborted"], result.Diagnostics.Select(diagnostic => diagnostic.Code), "codes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shows-a-nested-call-s-arguments-in-its-slot-as-made-then-unbounded-as-it-runs-with-them", async () =>
        {
            var setup = Setup();
            var hookEntered = new Deferred();
            var hookRelease = new Deferred();
            var running = new Deferred();
            var release = new Deferred();
            var large = new string('x', 64 * 1024);
            AddTool(setup.Registry, Tool("slow", async (_, _, _) =>
            {
                running.Resolve();
                await release.Task;
                return new ToolExecutionResult();
            }));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (hookCall, _, _) =>
                {
                    if (!hookCall.ContainsKey("parent")) return null;
                    hookEntered.Resolve();
                    await hookRelease.Task;
                    return new BeforeToolDecision(Arguments: Obj(("text", large)));
                },
            });
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("slow", Obj(("text", "asked")), callContext);
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            var starts = new SyncList<ToolExecutionStartEvent>();
            stream.Start((batch, _) =>
            {
                foreach (var item in batch) if (item is ToolExecutionStartEvent start) starts.Add(start);
                return Task.CompletedTask;
            });
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            async Task<JsonValue> Slot() => ((JsonArrayValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["nestedTools"])[0];
            await hookEntered.Task;
            MatchJson("""{"status":"pending","arguments":{"text":"asked"}}""", await Slot(), "pending slot");
            hookRelease.Resolve();
            await running.Task;
            MatchJson(Obj(("status", "running"), ("arguments", Obj(("text", large)))).ToJson(), await Slot(), "running slot");
            release.Resolve();
            await submission.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await stream.StopAsync();
            // The start event carries the arguments the call runs with; the task input keeps the ones it was made with.
            var start = starts.Snapshot().First(item => item.ToolCallId == "c1/1");
            SameJson(Obj(("text", large)).ToJson(), start.Args, "start args");
            var nested = (await ToolTasks(harness)).First(IsNested);
            MatchJson("""{"call":{"arguments":{"text":"asked"}}}""", nested.Input, "input");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-a-nested-call-whose-repaired-arguments-set-an-optional-property-to-undefined", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<JsonValue>();
            AddTool(setup.Registry, Tool("repaired", (args, _, _) =>
            {
                seen.Add(args);
                return Task.FromResult(new ToolExecutionResult());
            }, tool => tool with { PrepareArguments = _ => JsonObjectValue.Empty }));
            NestedToolExecutionResult? result = null;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                result = await api.ExecuteToolAsync("repaired", Obj(("text", "x")), callContext);
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            MatchJson("""{"isError":false}""", result!.ToJson(), "result");
            Seq(["{}"], seen.Snapshot().Select(args => args.ToJson()), "seen");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "finds-each-nested-call-s-slot-after-slots-in-the-middle-were-removed", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, new EchoTool().Registration);
            AddTool(setup.Registry, Tool("inner", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("echo", Obj(("text", "m1")), callContext);
                await api.ExecuteToolAsync("echo", Obj(("text", "m2")), callContext);
                return new ToolExecutionResult();
            }));
            var before = new Deferred();
            var checkedSlots = new Deferred();
            JsonArrayValue slots = JsonArrayValue.Empty;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("echo", Obj(("text", "n1")), callContext);
                // n2 makes m1 and m2 and settles, which removes them from between n2 and n3.
                await api.ExecuteToolAsync("inner", JsonObjectValue.Empty, callContext);
                await api.ExecuteToolAsync("echo", Obj(("text", "n3")), callContext);
                await api.ExecuteToolAsync("echo", Obj(("text", "n4")), callContext);
                var live = await api.SnapshotAsync(Live.LiveDoc, api.ConversationId, callContext);
                slots = JsonValue.Array(((JsonArrayValue?)(live?.TryGetValue("nestedTools", out var listed) == true ? listed : null) ?? JsonArrayValue.Empty)
                    .Cast<JsonObjectValue>().Select(slot => (JsonValue)Obj(("callId", slot["callId"]), ("status", slot["status"]), ("summary", slot["summary"]))));
                before.Resolve();
                await checkedSlots.Task;
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await before.Task;
            MatchExactly(JsonValue.Array(new[] { "c1/1", "c1/2", "c1/3", "c1/4" }.Select(callId => (JsonValue)Obj(("callId", callId), ("status", "done"),
                ("summary", Obj(("isError", false), ("durationMs", "<number>")))))).ToJson(), slots, "slots");
            checkedSlots.Resolve();
            await submission.WaitAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "gives-callers-a-result-with-its-task-id-and-only-diagnostics-for-a-nested-call-the-scheduler-faulted", async () =>
        {
            var setup = Setup();
            // See the file header: a diagnostic no JSON can encode makes the result commit throw, so the task faults and stores no result.
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Diagnostics = [new ToolDiagnostic((ToolDiagnosticSeverity)99, "unencodable")],
            })));
            NestedToolExecutionResult? result = null;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                result = await api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext);
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var child = (await ToolTasks(harness))[1];
            MatchJson("""{"status":"terminal","outcome":{"status":"faulted"}}""", child.State.ToJson(), "child");
            var json = result!.ToJson();
            Equal(Obj(("taskId", child.Id.ToJson()), ("isError", true)).ToJson(),
                JsonValue.Object(json.Where(property => property.Key != "diagnostics")).ToJson(), "result");
            Equal(1, result.Diagnostics.Length, "diagnostics");
            Equal(ToolDiagnosticSeverity.Error, result.Diagnostics[0].Severity, "severity");
            Equal("faulted", result.Diagnostics[0].Code, "code");
            Contains(result.Diagnostics[0].Message, "Tool echo failed", "message");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "gives-callers-what-a-schema-less-tool-streamed-before-it-threw", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("fails", (_, api, _) =>
            {
                api.Output("half way\n");
                throw new InvalidOperationException("broke");
            }));
            NestedToolExecutionResult? result = null;
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                result = await api.ExecuteToolAsync("fails", JsonObjectValue.Empty, callContext);
                return new ToolExecutionResult();
            }));
            var ran = await RunOnce(setup, Call("batch"), Done);
            MatchJson("""{"isError":true,"structuredOutput":"half way\n","diagnostics":[{"code":"tool_error","message":"broke"}]}""", result!.ToJson(), "result");
            await ran.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shows-a-finished-nested-call-as-a-done-slot-with-its-summary-until-its-caller-settles", async () =>
        {
            var setup = Setup();
            var release = new Deferred();
            var checkedSlots = new Deferred();
            // Diagnostics win over output for the error text, which is cut to 500 characters.
            AddTool(setup.Registry, Tool("verbose", (_, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Output = JsonValue.Array(FauxText("ignored")),
                IsError = true,
                Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Error, new string('e', 600))],
            })));
            var usage = O("""{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":3,"cost":{"input":0.1,"output":0.2,"cacheRead":0,"cacheWrite":0,"total":0.3}}""");
            AddTool(setup.Registry, new EchoTool().Registration);
            AddTool(setup.Registry, Tool("paid", (_, _, _) => Task.FromResult(new ToolExecutionResult { Usage = usage })));
            // An error with mixed content: programs get the list, the summary its text.
            AddTool(setup.Registry, Tool("broken", (_, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Output = JsonValue.Array(FauxText("bad image"), Obj(("type", "image"), ("data", "AAAA"), ("mimeType", "image/png"))),
                IsError = true,
            })));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext);
                await api.ExecuteToolAsync("missing", JsonObjectValue.Empty, callContext);
                await api.ExecuteToolAsync("paid", JsonObjectValue.Empty, callContext);
                await api.ExecuteToolAsync("broken", JsonObjectValue.Empty, callContext);
                await api.ExecuteToolAsync("verbose", JsonObjectValue.Empty, callContext);
                checkedSlots.Resolve();
                await release.Task;
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Call("batch"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await checkedSlots.Task;
            var slots = (JsonArrayValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["nestedTools"];
            MatchExactly(JsonValue.Array(
                Obj(("callId", "c1/1"), ("status", "done"), ("summary", Obj(("isError", false), ("durationMs", "<number>")))),
                Obj(("callId", "c1/2"), ("status", "done"), ("summary", Obj(("isError", true), ("error", "Tool missing is not available")))),
                Obj(("callId", "c1/3"), ("status", "done"), ("summary", Obj(("isError", false), ("durationMs", "<number>"), ("usage", usage)))),
                Obj(("callId", "c1/4"), ("status", "done"), ("summary", Obj(("isError", true), ("durationMs", "<number>"), ("error", "bad image")))),
                Obj(("callId", "c1/5"), ("status", "done"), ("summary", Obj(("isError", true), ("durationMs", "<number>"), ("error", new string('e', 500)))))
            ).ToJson(), JsonValue.Array(slots.Cast<JsonObjectValue>().Select(slot =>
                (JsonValue)Obj(("callId", slot["callId"]), ("status", slot["status"]), ("summary", slot["summary"])))), "slots");
            release.Resolve();
            await submission.WaitAsync(Context);
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "migrates-version-1-tool-task-input-to-a-model-issued-call", () =>
        {
            var migrate = HarnessTasks.ToolTask.Definition.Migrate!;
            var migrated = migrate(O("""{"assistant":3,"callId":"c1"}"""), O("""{"phase":"call"}"""), 1);
            Equal("""{"kind":"model","assistant":3,"callId":"c1"}""", migrated.Input.ToJson(), "input");
            Equal("""{"phase":"call"}""", migrated.Checkpoint.ToJson(), "checkpoint");
        });

        // ─── tool task version 1 records ────────────────────────────────────

        yield return Case(prefix + "version-1.migrates-a-pending-version-1-task-and-runs-it-as-a-model-issued-call", async () =>
        {
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            var (path, id) = await StoreVersion1(setup, O("""{"phase":"call"}"""));
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            Equal(1, (await harness.GetTaskAsync(id, Context))?.Version, "stored version");
            harness.Resume();
            var settled = await harness.WaitForTaskAsync(id, Context);
            Equal(2, settled.Version, "version");
            MatchJson("""{"kind":"model","callId":"c1"}""", settled.Input, "input");
            MatchJson("""{"status":"completed","result":{"kind":"model"}}""", OutcomeOf(settled), "outcome");
            Equal(1, echo.Runs, "runs");
            var result = Results(await AllEntries(root))[0];
            Equal("c1 echo old", $"{S(result["toolCallId"])} {Text(result)}", "result");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "version-1.migrates-a-version-1-task-interrupted-after-intent-and-settles-it-interrupted", async () =>
        {
            var setup = Setup();
            var echo = new EchoTool();
            AddTool(setup.Registry, echo.Registration);
            var (path, id) = await StoreVersion1(setup, O("""{"phase":"execute","arguments":{"text":"old"},"replay":"unsafe"}"""));
            var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            harness.Resume();
            var settled = await harness.WaitForTaskAsync(id, Context);
            Equal(2, settled.Version, "version");
            MatchJson("""{"status":"failed","result":{"kind":"model"}}""", OutcomeOf(settled), "outcome");
            Equal(0, echo.Runs, "runs");
            var result = Results(await AllEntries(root))[0];
            MatchJson("""{"toolCallId":"c1","isError":true}""", result, "result");
            Contains(Text(result), "Tool echo was interrupted", "text");
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>
    /// Stores a live tool task for call <c>c1</c> of an assistant entry, closes, and rewrites its record as version 1 stored it:
    /// <c>{assistant, callId}</c> input at version 1, with <paramref name="checkpoint"/>. Returns the storage path and the task ID.
    /// </summary>
    private static async Task<(string Path, TaskId Id)> StoreVersion1(ChatSetup setup, JsonObjectValue checkpoint)
    {
        var path = SqlitePath("tool-v1");
        // No submission, so the Harness never starts scheduling and the task stays pending.
        var (harness, root) = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
        var id = await root.CommitAsync(async tx =>
        {
            var entry = await tx.AppendEntryAsync(Entries.AssistantEntry, root.Id,
                new TypedEntryDraft { Model = JsonValue.Array(Call("echo", Obj(("text", "old")))) });
            return await tx.CreateTaskAsync(HarnessTasks.ToolTask,
                Obj(("kind", "model"), ("assistant", entry.Id.ToJson()), ("callId", "c1")), new TaskOptions(TaskOwnership.Conversation));
        }, Context);
        await harness.CloseAsync(Context);
        var storage = await SqliteStorage.OpenAsync(path);
        var record = (await storage.GetTaskAsync(id, Context))!;
        var input = (JsonObjectValue)record.Input;
        var state = record.State switch
        {
            PendingTaskState => (TaskState)new PendingTaskState(checkpoint),
            RunningTaskState => new RunningTaskState(checkpoint),
            var other => throw new InvalidOperationException($"unexpected state {other.Status}"),
        };
        await storage.CommitAsync([new TaskWrite(record with { Version = 1, Input = input.Remove("kind"), State = state })], Context);
        await storage.CloseAsync(Context);
        return (path, id);
    }

    private static void Contains(string text, string part, string what) =>
        Check(text.Contains(part, StringComparison.Ordinal), $"{what}: expected to contain <{part}>, actual <{text}>");
}
