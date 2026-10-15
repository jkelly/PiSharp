// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): a differential check of conversation views and agent events against the pinned upstream.
// There is no upstream test file: Fixtures/v1.2.0/views.upstream.json holds what the pinned pi-durable delivers to a conversation watch (the
// acquisition view, each frame's operations, the final view) and to an agent event stream (the snapshot and each batch), both attached
// before the run, for the scenarios below with pi-ai's faux provider on MemoryStorage, as printed by Fixtures/capture-views.mjs. It is
// normalized as HarnessChatDifferentialCases.cs's fixture is and compared as JSON values and by their exact text; every event's keys must
// also come in upstream's order. The scenarios run here on memory, JSONL and SQLite and must deliver the same frames and batches, IDs included. Upstream's
// `setTimeout(0)` before reading the last frame is ViewTests.Drained (a Session line barrier).
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
    private static IEnumerable<(string, Func<Task>)> HarnessViewsDifferentialCases() =>
    [
        .. ViewsDifferentialTests.Cases("harness.views-differential.", () => Task.FromResult<IStorage>(new MemoryStorage())),
        .. ViewsDifferentialTests.Cases("harness.views-differential.jsonl.",
            async () => await JsonlStorage.OpenAsync(Temp("views-differential-jsonl"), DurableContext.Background)),
        .. ViewsDifferentialTests.Cases("harness.views-differential.sqlite.",
            async () => await SqliteStorage.OpenAsync(Path.Combine(Temp("views-differential-sqlite"), "session.sqlite"))),
    ];
}

/// <summary>The harness.views-differential.* cases.</summary>
internal static class ViewsDifferentialTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const double T = 1000;

    private static readonly Lazy<JsonObjectValue> Upstream = new(() => (JsonObjectValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.2.0", "views.upstream.json"))));

    /// <summary><c>Type.Object({ text: Type.String() })</c>, as TypeBox prints it.</summary>
    private static readonly JsonObjectValue TextParameters = O("""{"type":"object","required":["text"],"properties":{"text":{"type":"string"}}}""");

    private sealed record Observed(IHarness Harness, IConversation Root, Func<Task<JsonValue>> Finish);

    private static async Task<Observed> Open(Func<Task<IStorage>> fresh, IRegistry registry, FauxModels faux)
    {
        var harness = await Harness.OpenAsync(await fresh(), new HarnessOptions { Models = faux, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        var watch = await root.WatchAsync(Context);
        var frames = new SyncList<JsonValue>();
        watch.Start((_, ops, _) =>
        {
            frames.Add(JsonValue.Array(ops.Select(op => (JsonValue)op.ToJson())));
            return Task.CompletedTask;
        });
        var stream = await harness.WatchEventsAsync(root.Id, Context);
        var batches = new SyncList<JsonValue>();
        stream.Start((events, _) =>
        {
            batches.Add(JsonValue.Array(events.Select(item => (JsonValue)item.ToJson())));
            return Task.CompletedTask;
        });
        var initial = watch.Value;
        var snapshot = stream.Snapshot;
        async Task<JsonValue> Finish()
        {
            await harness.WaitForIdleAsync(Context);
            await ViewTests.Drained(harness);
            var last = watch.Value;
            await watch.StopAsync();
            await stream.StopAsync();
            await harness.CloseAsync(Context);
            return ChatDifferentialTests.Normalize(Obj(("initial", initial.ToJson()), ("frames", JsonValue.Array(frames.Snapshot())), ("final", last.ToJson()),
                ("snapshot", snapshot.ToJson()), ("batches", JsonValue.Array(batches.Snapshot()))));
        }
        return new Observed(harness, root, Finish);
    }

    private static void Same(string scenario, JsonValue actual)
    {
        var expected = (JsonObjectValue)Upstream.Value[scenario];
        var got = (JsonObjectValue)actual;
        foreach (var part in new[] { "initial", "frames", "final", "snapshot", "batches" })
        {
            Check(JsonValue.DeepEquals(expected[part], got[part]), $"{scenario}.{part}: expected {expected[part].ToJson()}, actual {got[part].ToJson()}");
            Equal(expected[part].ToJson(), got[part].ToJson(), $"{scenario}.{part}: key order");
        }
        // Events are built here, so their keys come in upstream's order too.
        var expectedEvents = ((JsonArrayValue)expected["batches"]).SelectMany(batch => (JsonArrayValue)batch).Prepend(expected["snapshot"]);
        var actualEvents = ((JsonArrayValue)got["batches"]).SelectMany(batch => (JsonArrayValue)batch).Prepend(got["snapshot"]);
        foreach (var (left, right) in expectedEvents.Zip(actualEvents))
            Equal(string.Join(",", ((JsonObjectValue)left).Keys), string.Join(",", ((JsonObjectValue)right).Keys), $"{scenario}: event key order");
    }

    private static JsonObjectValue Calls(params (string Name, JsonObjectValue Args, string Id)[] list) =>
        FauxAssistantMessage(list.Select(call => FauxToolCall(call.Name, call.Args, call.Id)), stopReason: "toolUse", timestamp: T);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "tool-turn", async () =>
        {
            // A tool turn: one call whose tool publishes running details, then the answer.
            var faux = new FauxModels(new FauxOptions { Api = "faux" });
            var registry = PiSharp.Durable.Registry.Create();
            registry.Install(Define.Extension(new Extension
            {
                Name = "tools",
                Tools =
                [
                    Define.Tool(new ToolRegistration
                    {
                        Name = "echo",
                        Description = "Echoes its text",
                        Parameters = TextParameters,
                        Execute = async (args, api, callContext) =>
                        {
                            await api.DetailsAsync(Obj(("step", 1)), callContext);
                            return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"echo {S(((JsonObjectValue)args)["text"])}")) };
                        },
                    }),
                ],
            }));
            faux.SetResponses(Calls(("echo", Obj(("text", "hi")), "c1")), FauxAssistantMessage("done", timestamp: T));
            var observed = await Open(fresh, registry, faux);
            await (await observed.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Same("toolTurn", await observed.Finish());
        });

        yield return Case(prefix + "aborted-round", async () =>
        {
            // An aborted sequential round: the first tool blocks after publishing details; aborting the generation answers the unstarted
            // calls.
            var faux = new FauxModels(new FauxOptions { Api = "faux" });
            var registry = PiSharp.Durable.Registry.Create();
            var published = new HarnessTests.Deferred();
            ToolRegistration Blocking(string name) => Define.Tool(new ToolRegistration
            {
                Name = name,
                Description = $"The {name} tool",
                ExecutionMode = ToolExecutionMode.Sequential,
                Execute = async (_, api, callContext) =>
                {
                    await api.DetailsAsync(Obj(("phase", "running")), callContext);
                    published.Resolve();
                    await HarnessTests.Aborted(callContext.CancellationToken);
                    return new ToolExecutionResult();
                },
            });
            registry.Install(Define.Extension(new Extension { Name = "tools", Tools = [Blocking("one"), Blocking("two")] }));
            faux.SetResponses(Calls(("one", JsonObjectValue.Empty, "c1"), ("two", JsonObjectValue.Empty, "c2"), ("two", JsonObjectValue.Empty, "c3")));
            var observed = await Open(fresh, registry, faux);
            var submission = await observed.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await published.Task;
            var live = (await observed.Harness.SnapshotAsync(Live.LiveDoc, observed.Root.Id, Context))!;
            await observed.Harness.AbortTaskAsync(new TaskId((long)N(((JsonObjectValue)live["run"])["taskId"])), Context);
            await submission.WaitAsync(Context);
            Same("abortedRound", await observed.Finish());
        });
    }
}
