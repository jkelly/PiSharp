// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): a differential check of conversation views and agent events against the pinned upstream.
// There is no upstream test file: Fixtures/v1.2.0/views.upstream.json holds what the pinned pi-durable delivers to a conversation watch (the
// acquisition view, each frame's operations, the final view) and to an agent event stream (the snapshot and each batch), both attached
// before the run, for the scenarios below with pi-ai's faux provider on MemoryStorage, as printed by Fixtures/capture-views.mjs. It is
// normalized as HarnessChatDifferentialCases.cs's fixture is and compared as JSON values and by their exact text; every event's keys must
// also come in upstream's order. The scenarios run here on memory, JSONL and SQLite and must deliver the same frames and batches, IDs included. Upstream's
// `setTimeout(0)` before reading the last frame is ViewTests.Drained (a Session line barrier).
// Pi v1.5.0 sync (area model-requests): the cases compare with the capture's authored v1.5.0 form (ModelRequests150.cs), not the
// capture itself: v1.5.0 records the requested thinking level on each message it classifies. A byte comparison needs a recapture.
// Pi v1.5.0 sync (area view, f3255de76): the watch frames compare with an authored v1.5.0 form too. v1.5.0's windowed view appends a
// commit's entries in one splice (["p",["entries"],length,0,inserted]), where v1.2.0 spliced each entry in on its own; Splices150 joins
// each run of consecutive entry splices of a captured frame into that one splice. The scenarios hold fewer entries than the default window
// (50), so no frame drops entries or sets "before".
// Pi v1.5.0 sync (area run-failure, f3255de76): and with RunFailure150.cs's: abortedRound's frame that ends the run sets
// pi.live.runFailure before it removes run, its final view holds it, and its run_end event carries it (PiSharp's event projection).
// Pi v1.5.0 sync (area events, f3255de76): v1.5.0 removed watchEvents; the event half compares PiSharp's projection (PiSharp.Durable.Hosting's
// AgentEvents.cs, PiSharp-only) with the v1.2.0 capture, which stays honest only while the projection reproduces v1.2.0's events.
// Pi v1.5.0 sync (area live-turns, f6a9db1d8): and with LiveV3150.cs's: the frame that sets pi.live.run gives it turns 0, and the frame of
// the commit that starts a tool round sets run.turns after its pi.live ops. The events keep v1.2.0's wire (no turns).
// Pi v1.5.0 sync (area run-end, 7e15f9601): and the run's set carries first, the input entry its frame appends, as abortedRound's
// runFailure does (LiveV3150.cs). The events keep the wire without it (AgentEvents.cs).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
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

    // Pi v1.5.0 (area model-requests): the capture's authored v1.5.0 form (see the file header).
    private static readonly Lazy<JsonObjectValue> Upstream = new(() => LiveV3150.Views(RunFailure150.Views(Splices150((JsonObjectValue)ModelRequests150.Authored(JsonValue.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.2.0", "views.upstream.json"))))))));

    /// <summary>Pi v1.5.0 (area view): every scenario's frames with each run of consecutive entry splices joined into one (see the file
    /// header).</summary>
    private static JsonObjectValue Splices150(JsonObjectValue capture) => JsonObjectValue.From(capture.Select(scenario =>
        KeyValuePair.Create(scenario.Key, scenario.Value is JsonObjectValue parts && parts.TryGetValue("frames", out var frames)
            ? (JsonValue)JsonObjectValue.From(parts.Select(part => KeyValuePair.Create(part.Key, part.Key == "frames"
                ? (JsonValue)JsonValue.Array(((JsonArrayValue)frames).Select(frame => (JsonValue)JoinedSplices((JsonArrayValue)frame)))
                : part.Value)))
            : scenario.Value)));

    private static JsonArrayValue JoinedSplices(JsonArrayValue frame)
    {
        static bool IsAppend(JsonValue op) => op is JsonArrayValue { Count: 5 } splice && splice[0] is JsonStringValue { Value: "p" } &&
            splice[1] is JsonArrayValue { Count: 1 } path && path[0] is JsonStringValue { Value: "entries" } && splice[3] is JsonNumberValue { Value: 0 };
        var ops = new List<JsonValue>();
        foreach (var op in frame)
        {
            if (IsAppend(op) && ops.Count > 0 && IsAppend(ops[^1]))
            {
                var last = (JsonArrayValue)ops[^1];
                var inserted = (JsonArrayValue)last[4];
                if (N(((JsonArrayValue)op)[2]) == N(last[2]) + inserted.Count)
                {
                    ops[^1] = JsonValue.Array([last[0], last[1], last[2], last[3], JsonValue.Array([.. inserted, .. (JsonArrayValue)((JsonArrayValue)op)[4]])]);
                    continue;
                }
            }
            ops.Add(op);
        }
        return JsonValue.Array(ops);
    }

    /// <summary><c>Type.Object({ text: Type.String() })</c>, as TypeBox prints it.</summary>
    private static readonly JsonObjectValue TextParameters = O("""{"type":"object","required":["text"],"properties":{"text":{"type":"string"}}}""");

    private sealed record Observed(IHarness Harness, IConversation Root, Func<Task<JsonValue>> Finish);

    private static async Task<Observed> Open(Func<Task<IStorage>> fresh, IRegistry registry, FauxModels faux)
    {
        var harness = await Harness.OpenAsync(await fresh(), new HarnessOptions { Models = faux, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        var watch = await root.WatchAsync(Context);
        var frames = new SyncList<JsonValue>();
        watch.Start((call, _) =>
        {
            frames.Add(JsonValue.Array(Change(call).Ops.Select(op => (JsonValue)op.ToJson())));
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
