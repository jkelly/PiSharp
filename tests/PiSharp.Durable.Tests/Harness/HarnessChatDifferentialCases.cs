// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): a differential check of chat turns against the pinned upstream. There is no upstream test
// file: Fixtures/chat-turns.upstream.json holds the committed records (every entry, task and submission, and the conversation's pi.live,
// pi.inbox, pi.usage, pi.agent and pi.provider documents) that the pinned pi-durable writes for the scenarios below with pi-ai's faux
// provider on MemoryStorage, as printed by Fixtures/chat-turns.upstream.ts. Times and the provider session ID are normalized; task
// startedAt/endedAt are dropped. The scenarios run here on memory, JSONL and SQLite and must produce the same records, IDs included.
// Records compare as JSON values (vitest's toEqual): PiSharp.Contracts writes an assistant message's optional fields after its timestamp,
// where the faux provider writes errorMessage before it, and the record types write their fields in declaration order where upstream's
// storage keeps each object's insertion order. The Harness documents also compare by their exact JSON text, key order included.
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
    private static IEnumerable<(string, Func<Task>)> HarnessChatDifferentialCases() =>
    [
        .. ChatDifferentialTests.Cases("harness.chat-differential.", () => new MemoryStorage()),
        .. ChatDifferentialTests.Cases("harness.chat-differential.jsonl.",
            () => JsonlStorage.OpenAsync(Temp("chat-differential-jsonl"), DurableContext.Background).GetAwaiter().GetResult()),
        .. ChatDifferentialTests.Cases("harness.chat-differential.sqlite.",
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("chat-differential-sqlite"), "session.sqlite")).GetAwaiter().GetResult()),
    ];
}

/// <summary>The harness.chat-differential.* cases.</summary>
internal static class ChatDifferentialTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const double T = 1000;
    private static readonly string[] Times = ["timestamp", "at", "until", "pollAt", "startedAt", "endedAt", "durationMs"];

    private static readonly Lazy<JsonObjectValue> Upstream = new(() => (JsonObjectValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "chat-turns.upstream.json"))));

    internal static JsonValue Normalize(JsonValue value, string? key = null) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => Normalize(item))),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Normalize(property.Value, property.Key)))),
        JsonNumberValue when key is not null && Times.Contains(key) => "<time>",
        JsonStringValue when key == "sessionId" => "<session>",
        _ => value,
    };

    internal static async Task<JsonValue> Dump(IHarness harness, IStorage storage, ConversationId conversation)
    {
        var entries = await storage.ScanEntriesAsync(new EntryQuery(conversation) { Order = ScanOrder.Ascending }, 1000, null, Context);
        var docs = new List<(string, JsonValue)>();
        foreach (var (name, doc) in new (string, ConversationDocToken)[]
                 {
                     ("pi.live", Live.LiveDoc), ("pi.inbox", Inbox.InboxDoc), ("pi.usage", UsageLedger.UsageDoc), ("pi.agent", Agents.AgentDoc),
                     ("pi.provider", ProviderIdentity.ProviderDoc),
                 })
            docs.Add((name, (JsonValue?)await harness.SnapshotAsync(doc, conversation, Context) ?? JsonValue.Null));
        var tasks = await storage.ScanTasksAsync(new TaskQuery(), 1000, null, Context);
        var submissions = await storage.ScanSubmissionsAsync(new SubmissionQuery(), 1000, null, Context);
        return Normalize(Obj(
            ("conversations", Obj((conversation.ToString(), Obj(
                ("entries", JsonValue.Array(entries.Items.Select(entry => (JsonValue)entry.ToJson()))),
                ("docs", Obj([.. docs])))))),
            ("tasks", JsonValue.Array(tasks.Items.Select(task => (JsonValue)task.ToJson().Remove("startedAt").Remove("endedAt")))),
            ("submissions", JsonValue.Array(submissions.Items.Select(submission => (JsonValue)submission.ToJson())))));
    }

    private static void Same(string scenario, JsonValue actual, params string[] path)
    {
        var expected = path.Aggregate((JsonValue)Upstream.Value[scenario], (value, key) => ((JsonObjectValue)value)[key]);
        Check(JsonValue.DeepEquals(expected, actual), $"{scenario}: expected {expected.ToJson()}, actual {actual.ToJson()}");
        // The documents are built by the Harness, so their key order is upstream's too.
        foreach (var (expectedDocs, actualDocs) in Documents(expected).Zip(Documents(actual)))
            Equal(expectedDocs.ToJson(), actualDocs.ToJson(), $"{scenario}: document key order");
    }

    internal static IEnumerable<JsonValue> Documents(JsonValue dump)
    {
        var value = (JsonObjectValue)dump;
        if (value.TryGetValue("inbox", out var inbox)) yield return inbox;
        if (!value.TryGetValue("conversations", out var conversations)) yield break;
        foreach (var conversation in ((JsonObjectValue)conversations).Values)
            foreach (var name in new[] { "pi.live", "pi.inbox", "pi.usage", "pi.agent", "pi.provider" })
                yield return ((JsonObjectValue)((JsonObjectValue)conversation)["docs"])[name];
    }

    private static ChatSetup Chat() => new(new FauxModels(new FauxOptions { Api = "faux" }));

    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage storage, bool model = true)
    {
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings }, Context);
        var root = await harness.RootAsync(Context, model ? new AgentChange { Model = new ModelRef("faux", "faux-1") } : null);
        return new OpenedChat(harness, root);
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "answer", async () =>
        {
            // A plain answer with tagged and untagged sections and an offered tool.
            var setup = Chat();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "base",
                Sections = [Define.Section("preamble", "You are terse.", tag: false), Define.Section("cwd", "/repo")],
                Tools = [Define.Tool(new ToolRegistration
                {
                    Name = "noop", Description = "Does nothing",
                    Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Content = JsonArrayValue.Empty }),
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("Hello there", timestamp: T));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            await (await root.SubmitAsync(SubmissionDraft.Input("hi", requestId: "r1"), Context)).WaitAsync(Context);
            Same("answer", await Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "retry", async () =>
        {
            // A retryable error, then the answer.
            var setup = Chat();
            setup.Retry = new RetrySettings { BaseDelayMs = 1 };
            setup.Faux.SetResponses(FauxAssistantMessage([], stopReason: "error", errorMessage: "503 Service Unavailable", timestamp: T),
                FauxAssistantMessage("recovered", timestamp: T));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            await (await root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).WaitAsync(Context);
            Same("retry", await Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "unavailable-tool", async () =>
        {
            // A call to a tool the request did not offer: a tool_unavailable result, then the next generation answers.
            var setup = Chat();
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("missing", Obj(("x", 1)), "call-1")], stopReason: "toolUse", timestamp: T),
                FauxAssistantMessage("done", timestamp: T));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Same("unavailable", await Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "follow-up", async () =>
        {
            // A follow-up queued while busy runs after the answer; a queued write is placed at the same boundary.
            var setup = Chat();
            var release = new HarnessTests.Deferred();
            setup.Faux.SetResponses(
                new FauxResponseFactory(async _ =>
                {
                    await release.Task;
                    return FauxAssistantMessage("first", timestamp: T);
                }),
                FauxAssistantMessage("second", timestamp: T));
            var storage = fresh();
            var (harness, root) = await Open(setup, storage);
            var one = await root.SubmitAsync(SubmissionDraft.Input("one"), Context);
            var two = await root.SubmitAsync(SubmissionDraft.Input("two", WhenBusy.FollowUp), Context);
            var note = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "note", Data = O("""{"text":"x"}""") }), Context);
            // While busy: the inbox and the submissions (the run's own progress is timing-dependent).
            var submissions = await storage.ScanSubmissionsAsync(new SubmissionQuery(), 1000, null, Context);
            Same("followUp", Normalize(Obj(
                ("inbox", (JsonValue?)await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Context) ?? JsonValue.Null),
                ("submissions", JsonValue.Array(submissions.Items.Select(submission => (JsonValue)submission.ToJson()))))), "queued");
            release.Resolve();
            await one.WaitAsync(Context);
            await two.WaitAsync(Context);
            await note.WaitAsync(Context);
            Same("followUp", await Dump(harness, storage, root.Id), "done");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "no-model", async () =>
        {
            // No model: the run fails with no_model.
            var setup = Chat();
            var storage = fresh();
            var (harness, root) = await Open(setup, storage, model: false);
            await (await root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).WaitAsync(Context);
            Same("noModel", await Dump(harness, storage, root.Id));
            await harness.CloseAsync(Context);
        });
    }
}
