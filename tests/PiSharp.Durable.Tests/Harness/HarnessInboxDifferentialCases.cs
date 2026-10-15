// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): a differential check of the inbox against the pinned upstream. There is no upstream test
// file: Fixtures/v1.2.0/inbox.upstream.json holds what the pinned pi-durable commits for the scenarios below with pi-ai's faux provider on
// MemoryStorage, as printed by Fixtures/capture-inbox.mjs: the inbox and the submissions while the conversation is busy, then every entry,
// task and submission, the conversation's pi.live, pi.inbox, pi.usage, pi.agent and pi.provider documents, and Harness.usage(). It is
// normalized as HarnessChatDifferentialCases.cs's fixture is, and compared the same way: as JSON values and by their exact text, and the documents built by the
// Harness (the busy inbox included) and the usage total by their exact text. The scenarios run here on memory, JSONL and SQLite and must
// produce the same records, IDs included.
// Until area a (tool task v2) lands, the steer scenario's one pi.tool task is compared in its v1 shape: PendingToolV2 checks that the captured
// record differs from v1 exactly by version 2 and the `kind: "model"` of its input and result, and drops those; area a removes it.
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
    private static IEnumerable<(string, Func<Task>)> HarnessInboxDifferentialCases() =>
    [
        .. InboxDifferentialTests.Cases("harness.inbox-differential.", () => new MemoryStorage()),
        .. InboxDifferentialTests.Cases("harness.inbox-differential.jsonl.",
            () => JsonlStorage.OpenAsync(Temp("inbox-differential-jsonl"), DurableContext.Background).GetAwaiter().GetResult()),
        .. InboxDifferentialTests.Cases("harness.inbox-differential.sqlite.",
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("inbox-differential-sqlite"), "session.sqlite")).GetAwaiter().GetResult()),
    ];
}

/// <summary>The harness.inbox-differential.* cases.</summary>
internal static class InboxDifferentialTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const double T = 1000;

    private static readonly Lazy<JsonObjectValue> Upstream = new(() => (JsonObjectValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.2.0", "inbox.upstream.json"))));

    /// <summary>The captured records with each pi.tool task in its v1 shape; see the file header.</summary>
    private static JsonValue PendingToolV2(JsonValue records)
    {
        if (records is not JsonObjectValue value || !value.TryGetValue("tasks", out var tasks)) return records;
        return value.SetItem("tasks", JsonValue.Array(((JsonArrayValue)tasks).Select(item =>
        {
            var task = (JsonObjectValue)item;
            if (S(task["kind"]) != "pi.tool") return item;
            Check(N(task["version"]) == 2, "a v2 tool task");
            var input = (JsonObjectValue)task["input"];
            Check(S(input["kind"]) == "model", "a model-issued call");
            var state = (JsonObjectValue)task["state"];
            var outcome = (JsonObjectValue)state["outcome"];
            var result = (JsonObjectValue)outcome["result"];
            Check(S(result["kind"]) == "model", "a model-issued result");
            return (JsonValue)task.SetItem("version", 1).SetItem("input", input.Remove("kind"))
                .SetItem("state", state.SetItem("outcome", outcome.SetItem("result", result.Remove("kind"))));
        })));
    }

    private static ChatSetup Chat() => new(new FauxModels(new FauxOptions { Api = "faux" }));

    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage storage)
    {
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        return new OpenedChat(harness, root);
    }

    private static JsonObjectValue Answer(string text) => FauxAssistantMessage(text, timestamp: T);

    /// <summary>A faux answer held until <c>Release</c>; <c>Reached</c> completes when the request is sent.</summary>
    private static (FauxStep Step, Task Reached, Action Release) Gated(string text)
    {
        var reached = new HarnessTests.Deferred();
        var held = new HarnessTests.Deferred();
        FauxStep step = new FauxResponseFactory(async _ =>
        {
            reached.Resolve();
            await held.Task;
            return Answer(text);
        });
        return (step, reached.Task, held.Resolve);
    }

    /// <summary>The busy inbox and the submissions.</summary>
    private static async Task<JsonValue> Queued(IHarness harness, IStorage storage, ConversationId conversation)
    {
        var submissions = await storage.ScanSubmissionsAsync(new SubmissionQuery(), 1000, null, Context);
        return ChatDifferentialTests.Normalize(Obj(
            ("inbox", (JsonValue?)await harness.SnapshotAsync(Inbox.InboxDoc, conversation, Context) ?? JsonValue.Null),
            ("submissions", JsonValue.Array(submissions.Items.Select(submission => (JsonValue)submission.ToJson())))));
    }

    /// <summary>Every record, as HarnessChatDifferentialCases.cs dumps them, and the Session usage total.</summary>
    private static async Task<JsonValue> Dump(IHarness harness, IStorage storage, ConversationId conversation) =>
        ((JsonObjectValue)await ChatDifferentialTests.Dump(harness, storage, conversation))
            .SetItem("usage", ChatDifferentialTests.Normalize(await harness.UsageAsync(Context)));

    private static void Same(string scenario, JsonValue busy, JsonValue done)
    {
        var expected = (JsonObjectValue)Upstream.Value[scenario];
        foreach (var (name, actual) in new[] { ("queued", busy), ("done", done) })
        {
            var value = PendingToolV2(expected[name]);
            Check(JsonValue.DeepEquals(value, actual), $"{scenario} {name}: expected {value.ToJson()}, actual {actual.ToJson()}");
            Equal(value.ToJson(), actual.ToJson(), $"{scenario} {name}: key order");
            // The documents and the usage total are built by the Harness, so their key order is upstream's too.
            foreach (var (expectedDoc, actualDoc) in ChatDifferentialTests.Documents(value).Zip(ChatDifferentialTests.Documents(actual)))
                Equal(expectedDoc.ToJson(), actualDoc.ToJson(), $"{scenario} {name}: document key order");
            if (((JsonObjectValue)value).TryGetValue("usage", out var usage))
                Equal(usage.ToJson(), ((JsonObjectValue)actual)["usage"].ToJson(), $"{scenario}: usage key order");
        }
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "steer", async () =>
        {
            // A steer, a follow-up and a write queued while a tool runs: the steer joins the run after tools, the write and the follow-up
            // wait for the final boundary.
            var setup = Chat();
            var held = new HarnessTests.Deferred();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "tools",
                Tools = [Define.Tool(new ToolRegistration
                {
                    Name = "hold",
                    Description = "Waits for the test",
                    Parameters = O("""{"type":"object","properties":{}}"""),
                    Execute = async (_, _, _) =>
                    {
                        await held.Task;
                        return new ToolExecutionResult { Content = JsonValue.Array(FauxText("held")) };
                    },
                })],
            }));
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse", timestamp: T),
                Answer("after tools"),
                Answer("follow-up"));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            await HarnessTests.Eventually(async () => (await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context)) is { } live &&
                live.TryGetValue("tools", out var tools) && At(tools, 0, "status") is JsonStringValue { Value: "running" });
            await root.SubmitAsync(SubmissionDraft.Input("s", WhenBusy.Steer), Context);
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("f"), Context);
            var note = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "note", Data = O("""{"text":"x"}""") }), Context);
            var busy = await Queued(harness, storage, root.Id);
            held.Resolve();
            await input.WaitAsync(Context);
            await followUp.WaitAsync(Context);
            await note.WaitAsync(Context);
            Same("steer", busy, await Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reset", async () =>
        {
            // A reset with handoff text queued behind a follow-up: placed first at the final boundary, so the follow-up runs in the new
            // context. Then an idle reset without handoff, and an idle write whose head reaches before the active range.
            var setup = Chat();
            var first = Gated("first");
            setup.Faux.SetResponses(first.Step, Answer("second"));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            await first.Reached;
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("f"), Context);
            await root.ResetAsync("handoff", Context);
            var busy = await Queued(harness, storage, root.Id);
            first.Release();
            await input.WaitAsync(Context);
            await followUp.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await root.ResetAsync(null, Context);
            var target = (await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 1, null, Context)).Items.First();
            await (await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "summary", Head = EntryHead.At(target.Id) }), Context))
                .WaitAsync(Context);
            Same("reset", busy, await Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "modes", async () =>
        {
            // Queue modes all: every steer and follow-up queued behind the answer starts one successor run, in ID order.
            var setup = Chat();
            setup.SteeringMode = QueueMode.All;
            setup.FollowUpMode = QueueMode.All;
            var first = Gated("first");
            setup.Faux.SetResponses(first.Step, Answer("all of them"));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            await first.Reached;
            var later = new List<ISubmission>();
            foreach (var (content, whenBusy) in new[] { ("f1", WhenBusy.FollowUp), ("s1", WhenBusy.Steer), ("f2", WhenBusy.FollowUp), ("s2", WhenBusy.Steer) })
                later.Add(await root.SubmitAsync(SubmissionDraft.Input(content, whenBusy), Context));
            var busy = await Queued(harness, storage, root.Id);
            first.Release();
            await input.WaitAsync(Context);
            foreach (var submission in later) await submission.WaitAsync(Context);
            Same("modes", busy, await Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });
    }
}
