// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): a differential check of tool rounds against the pinned upstream. There is no upstream test
// file: Fixtures/tool-rounds.upstream.json holds the committed records (every entry, task and submission, and the conversation's pi.live,
// pi.inbox, pi.usage, pi.agent and pi.provider documents) that the pinned pi-durable writes for the scenarios below with pi-ai's faux
// provider on MemoryStorage, as printed by Fixtures/capture-tool-rounds.mjs. It is normalized as HarnessChatDifferentialCases.cs's fixture
// is, and compared the same way: as JSON values, and the Harness documents by their exact text. The scenarios run here on memory, JSONL and
// SQLite and must produce the same records, IDs included. Parameters are the JSON TypeBox prints for the scenarios' schemas.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessToolsDifferentialCases() =>
    [
        .. ToolsDifferentialTests.Cases("harness.tools-differential.", () => new MemoryStorage()),
        .. ToolsDifferentialTests.Cases("harness.tools-differential.jsonl.",
            () => JsonlStorage.OpenAsync(Temp("tools-differential-jsonl"), DurableContext.Background).GetAwaiter().GetResult()),
        .. ToolsDifferentialTests.Cases("harness.tools-differential.sqlite.",
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("tools-differential-sqlite"), "session.sqlite")).GetAwaiter().GetResult()),
    ];
}

/// <summary>The harness.tools-differential.* cases.</summary>
internal static class ToolsDifferentialTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const double T = 1000;

    private static readonly Lazy<JsonObjectValue> Upstream = new(() => (JsonObjectValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "tool-rounds.upstream.json"))));

    private static readonly JsonObjectValue Usage =
        O("""{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":3,"cost":{"input":0.5,"output":0.25,"cacheRead":0,"cacheWrite":0,"total":0.75}}""");

    /// <summary><c>Type.Object({ text: Type.String() })</c>.</summary>
    private static readonly JsonObjectValue TextParameters = O("""{"type":"object","required":["text"],"properties":{"text":{"type":"string"}}}""");

    private static readonly ToolRegistration Echo = Define.Tool(new ToolRegistration
    {
        Name = "echo",
        Description = "Echoes its text",
        Parameters = TextParameters,
        Execute = (args, _, _) => Task.FromResult(new ToolExecutionResult
        {
            Content = JsonValue.Array(FauxText($"echo {S(((JsonObjectValue)args)["text"])}")),
            Usage = Usage,
        }),
    });

    private static void Same(string scenario, JsonValue actual, params string[] path)
    {
        var expected = path.Aggregate((JsonValue)Upstream.Value[scenario], (value, key) => ((JsonObjectValue)value)[key]);
        Check(JsonValue.DeepEquals(expected, actual), $"{scenario}: expected {expected.ToJson()}, actual {actual.ToJson()}");
        // The documents are built by the Harness, so their key order is upstream's too.
        foreach (var (expectedDocs, actualDocs) in ChatDifferentialTests.Documents(expected).Zip(ChatDifferentialTests.Documents(actual)))
            Equal(expectedDocs.ToJson(), actualDocs.ToJson(), $"{scenario}: document key order");
    }

    private static ChatSetup Chat() => new(new FauxModels(new FauxOptions { Api = "faux" }));

    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage storage)
    {
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        return new OpenedChat(harness, root);
    }

    private static JsonObjectValue Calls(params (string Name, JsonObjectValue Args, string Id)[] list) =>
        FauxAssistantMessage(list.Select(call => FauxToolCall(call.Name, call.Args, call.Id)), stopReason: "toolUse", timestamp: T);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "round", async () =>
        {
            // A sequential round: explicit content with usage, then running output bounded to its first lines, an api diagnostic, running
            // details, a result diagnostic and an addTools control; then the answer.
            var setup = Chat();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            var log = Define.Tool(new ToolRegistration
            {
                Name = "log",
                Description = "Logs lines",
                OutputLimits = new ToolOutputLimits { MaxLines = 2 },
                Execute = async (_, api, _) =>
                {
                    api.Output("line 1\n");
                    api.Output("line 2\nline 3\n"u8);
                    api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, "from api", "note"));
                    await api.DetailsAsync(Obj(("step", 1)), Context);
                    return new ToolExecutionResult
                    {
                        Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Warn, "from result")],
                        Control = new ToolControl { AddTools = ["echo"] },
                    };
                },
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "tools", Tools = [Echo, log] }));
            setup.Faux.SetResponses(Calls(("echo", Obj(("text", "hi")), "c1"), ("log", JsonObjectValue.Empty, "c2")), FauxAssistantMessage("done", timestamp: T));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(log) }, Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Same("round", await ChatDifferentialTests.Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejections", async () =>
        {
            // Invalid arguments with pi-ai's validation message, a beforeTool block, a throwing tool that ends its task failed with the
            // result entry, and a call whose usage enters pi.usage.tools; then the answer.
            var setup = Chat();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            var thrower = Define.Tool(new ToolRegistration
            {
                Name = "thrower",
                Description = "Throws",
                Execute = (_, api, _) =>
                {
                    api.Output("partial\n");
                    throw new InvalidOperationException("boom");
                },
            });
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "tools",
                Tools = [Echo, thrower],
                Hooks = [Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = (call, _, _) =>
                        Task.FromResult(S(call["id"]) == "blocked" ? new BeforeToolDecision(Block: "not today") : null),
                })],
            }));
            setup.Faux.SetResponses(
                Calls(("echo", O("""{"text":{"not":"a string"}}"""), "invalid"), ("echo", Obj(("text", "x")), "blocked"), ("thrower", JsonObjectValue.Empty, "c3"),
                    ("echo", Obj(("text", "ok")), "c4")),
                FauxAssistantMessage("done", timestamp: T));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Same("rejections", await ChatDifferentialTests.Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborted", async () =>
        {
            // A tool aborted after it published output and details: an aborted result from its durable slot, then the answer.
            var setup = Chat();
            var published = new HarnessTests.Deferred();
            var slow = Define.Tool(new ToolRegistration
            {
                Name = "slow",
                Description = "Waits",
                Execute = async (_, api, callContext) =>
                {
                    api.Output("partial\n");
                    await api.DetailsAsync(Obj(("phase", "running")), callContext);
                    published.Resolve();
                    await HarnessTests.Aborted(callContext.CancellationToken);
                    return new ToolExecutionResult();
                },
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "tools", Tools = [slow] }));
            setup.Faux.SetResponses(Calls(("slow", JsonObjectValue.Empty, "c1")), FauxAssistantMessage("done", timestamp: T));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await published.Task;
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
            var taskId = new TaskId((long)N(((JsonObjectValue)((JsonArrayValue)live!["tools"])[0])["taskId"]));
            var marked = await harness.AbortTaskAsync(taskId, Context);
            await submission.WaitAsync(Context);
            Equal(S(((JsonObjectValue)Upstream.Value["aborted"])["marked"]), marked == TaskAbortResult.Marked ? "marked" : "terminal", "aborted: marked");
            Same("aborted", await ChatDifferentialTests.Dump(harness, storage, root.Id), "done");
            await harness.CloseAsync(Context);
        });
    }
}
