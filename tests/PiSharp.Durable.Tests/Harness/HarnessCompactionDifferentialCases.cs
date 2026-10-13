// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): a differential check of compaction against the pinned upstream. There is no upstream test
// file: Fixtures/compaction.upstream.json holds what the pinned pi-durable commits for the scenarios below with pi-ai's faux provider on
// MemoryStorage, as printed by Fixtures/capture-compaction.mjs: every entry, task and submission, the conversation's pi.live, pi.inbox,
// pi.usage, pi.agent and pi.provider documents, Harness.usage(), and each summarization request's messages and options (without its
// signal). It is normalized as HarnessChatDifferentialCases.cs's fixture is, and compared the same way: as JSON values, and the documents
// built by the Harness and the usage total by their exact text. The summary's usage is the faux provider's estimate of the request, so the
// fixture also pins the summarization prompts and the serialized conversation. The scenarios run here on memory, JSONL and SQLite and must
// produce the same records, IDs included.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessCompactionDifferentialCases() =>
    [
        .. CompactionDifferentialTests.Cases("harness.compaction-differential.", () => new MemoryStorage()),
        .. CompactionDifferentialTests.Cases("harness.compaction-differential.jsonl.",
            () => JsonlStorage.OpenAsync(Temp("compaction-differential-jsonl"), DurableContext.Background).GetAwaiter().GetResult()),
        .. CompactionDifferentialTests.Cases("harness.compaction-differential.sqlite.",
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("compaction-differential-sqlite"), "session.sqlite")).GetAwaiter().GetResult()),
    ];
}

/// <summary>The harness.compaction-differential.* cases.</summary>
internal static class CompactionDifferentialTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const double T = 1000;
    private const string Overflow = "prompt is too long: 250000 tokens > 200000 maximum";
    private static readonly CompactionSettings Manual = new() { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 150, BackgroundTokens = 0 };

    private static readonly Lazy<JsonObjectValue> Upstream = new(() => (JsonObjectValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "compaction.upstream.json"))));

    private static string Text(string label, int tokens) => CompactionTests.Text(label, tokens);
    private static JsonObjectValue Answer(string content) => FauxAssistantMessage(content, timestamp: T);
    private static JsonObjectValue Failure(string errorMessage) => FauxAssistantMessage("", stopReason: "error", errorMessage: errorMessage, timestamp: T);

    private sealed class Chat(IHarness harness, IConversation root, IStorage storage, ChatSetup setup)
    {
        public IHarness Harness { get; } = harness;
        public IConversation Root { get; } = root;
        public IStorage Storage { get; } = storage;
        public ChatSetup Setup { get; } = setup;
        public List<Func<Task<JsonObjectValue>>> Agent { get; } = [];
        public List<Func<Task<JsonObjectValue>>> Summaries { get; } = [];
        public SyncList<FauxRequest> SummaryRequests { get; } = new();

        public static Func<Task<JsonObjectValue>> Of(JsonObjectValue message) => () => Task.FromResult(message);
    }

    /// <summary>A chat whose faux model answers agent and summarization requests from separate queues.</summary>
    private static async Task<Chat> Open(Func<IStorage> fresh, double contextWindow)
    {
        var setup = new ChatSetup(new FauxModels(new FauxOptions { Api = "faux", Models = [new FauxModelOptions("faux-1", contextWindow, 900)] }));
        setup.Registry.Install(Define.Extension(new Extension
        {
            Name = "section:preamble",
            Sections = [Define.Section("preamble", (_, _) => ValueTask.FromResult<string?>("You are helpful."), false)],
        }));
        setup.Stream = new ConversationStreamOptions { CacheRetention = "none" };
        setup.Compaction = Manual;
        setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 1 };
        var storage = fresh();
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        var chat = new Chat(harness, root, storage, setup);
        setup.Faux.SetResponses([.. Enumerable.Range(0, 100).Select(_ => (FauxStep)new FauxResponseFactory(async request =>
        {
            var summary = request.Messages.Length > 0 && request.Messages[0] is JsonObjectValue first && first["role"] is JsonStringValue { Value: "system" } &&
                first["content"] is JsonStringValue { Value: var content } && content.StartsWith("You are a context summarization assistant", StringComparison.Ordinal);
            if (summary) chat.SummaryRequests.Add(request);
            var queue = summary ? chat.Summaries : chat.Agent;
            Func<Task<JsonObjectValue>> step;
            lock (queue)
            {
                if (queue.Count == 0) throw new InvalidOperationException($"No scripted {(summary ? "summary" : "agent")} response");
                step = queue[0];
                queue.RemoveAt(0);
            }
            return await step();
        }))]);
        return chat;
    }

    private static async Task Turn(Chat chat, string user, string reply)
    {
        lock (chat.Agent) chat.Agent.Add(Chat.Of(Answer(reply)));
        var settled = await (await chat.Root.SubmitAsync(SubmissionDraft.Input(user), Context)).WaitAsync(Context);
        Equal(SubmissionStatus.Done, settled.Status, $"turn {user[..Math.Min(4, user.Length)]}");
    }

    private static async Task History(Chat chat)
    {
        await Turn(chat, Text("u1", 100), Text("a1", 100));
        await Turn(chat, Text("u2", 100), Text("a2", 100));
        await Turn(chat, Text("u3", 100), Text("a3", 100));
    }

    /// <summary>The options a summarization request carried, as upstream's options object without its signal.</summary>
    private static JsonValue OptionsJson(DurableStreamOptions? options) => options is null ? JsonObjectValue.Empty : new RecordJson.Writer()
        .Add("transport", options.Transport)
        .Number("timeoutMs", options.TimeoutMs)
        .Number("maxRetries", options.MaxRetries)
        .Number("maxRetryDelayMs", options.MaxRetryDelayMs)
        .Add("headers", options.Headers is { } headers ? JsonValue.Object(headers.Select(header => KeyValuePair.Create(header.Key, (JsonValue)header.Value))) : null)
        .Add("metadata", options.Metadata)
        .Add("cacheRetention", options.CacheRetention)
        .Add("deferred", options.Deferred)
        .Number("maxTokens", options.MaxTokens)
        .Add("sessionId", options.SessionId)
        .Add("reasoning", options.Reasoning)
        .Build();

    private static async Task<JsonValue> Dump(Chat chat) =>
        ((JsonObjectValue)await ChatDifferentialTests.Dump(chat.Harness, chat.Storage, chat.Root.Id))
            .SetItem("usage", ChatDifferentialTests.Normalize(await chat.Harness.UsageAsync(Context)))
            .SetItem("requests", ChatDifferentialTests.Normalize(JsonValue.Array(chat.SummaryRequests.Snapshot().Select(request => (JsonValue)Obj(
                ("messages", JsonValue.Array(request.Messages)), ("options", OptionsJson(request.Options)))))));

    private static void Same(string scenario, JsonValue actual)
    {
        var expected = Upstream.Value[scenario];
        Check(JsonValue.DeepEquals(expected, actual), $"{scenario}: expected {expected.ToJson()}, actual {actual.ToJson()}");
        // The documents and the usage total are built by the Harness, so their key order is upstream's too.
        foreach (var (expectedDoc, actualDoc) in ChatDifferentialTests.Documents(expected).Zip(ChatDifferentialTests.Documents(actual)))
            Equal(expectedDoc.ToJson(), actualDoc.ToJson(), $"{scenario}: document key order");
        Equal(((JsonObjectValue)expected)["usage"].ToJson(), ((JsonObjectValue)actual)["usage"].ToJson(), $"{scenario}: usage key order");
    }

    private static async Task<SubmissionId> SubmissionOf(Chat chat, TaskId id) =>
        CompactionResult.FromJson(((CompletedTaskOutcome)((SettlingTaskState)(await chat.Harness.WaitForTaskAsync(id, Context)).State).Outcome).Result!)
            .SubmissionId!.Value;

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "manual", async () =>
        {
            // Placed at once while idle, then one more turn over the compacted context.
            var chat = await Open(fresh, 100_000);
            await History(chat);
            lock (chat.Summaries) chat.Summaries.Add(Chat.Of(Answer("SUMMARY")));
            var id = await chat.Root.CompactAsync("focus on files", Context);
            await (await chat.Harness.SubmissionAsync(await SubmissionOf(chat, id), Context))!.WaitAsync(Context);
            await Turn(chat, "next", "done");
            await chat.Harness.WaitForIdleAsync(Context);
            Same("manual", await Dump(chat));
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "blocking", async () =>
        {
            // Preparation compacts first, and the summary is appended before the request.
            var chat = await Open(fresh, 1000);
            await History(chat);
            chat.Setup.Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 300, KeepRecentTokens = 150, BackgroundTokens = 0 };
            lock (chat.Summaries) chat.Summaries.Add(Chat.Of(Answer("BLOCKING")));
            await Turn(chat, Text("u4", 200), "a4");
            await chat.Harness.WaitForIdleAsync(Context);
            Same("blocking", await Dump(chat));
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "background", async () =>
        {
            // The compaction summarizes while the turn ends, and its summary lands at once on the idle conversation.
            var chat = await Open(fresh, 2000);
            await History(chat);
            chat.Setup.Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 500, KeepRecentTokens = 150, BackgroundTokens = 1000 };
            var held = new HarnessTests.Deferred();
            var reached = new HarnessTests.Deferred();
            lock (chat.Summaries)
                chat.Summaries.Add(async () =>
                {
                    reached.Resolve();
                    await held.Task;
                    return Answer("BACKGROUND");
                });
            // The answer waits until the summarization request is sent, so the compaction selected its range before the answer was appended.
            lock (chat.Agent)
                chat.Agent.Add(async () =>
                {
                    await reached.Task;
                    return Answer(Text("a4", 100));
                });
            Equal(SubmissionStatus.Done, (await (await chat.Root.SubmitAsync(SubmissionDraft.Input(Text("u4", 100)), Context)).WaitAsync(Context)).Status,
                "turn u4");
            var live = (await chat.Harness.SnapshotAsync(Live.LiveDoc, chat.Root.Id, Context))!;
            var id = new TaskId((long)N(At(live, "compactions", 0, "taskId")));
            held.Resolve();
            await (await chat.Harness.SubmissionAsync(await SubmissionOf(chat, id), Context))!.WaitAsync(Context);
            Same("background", await Dump(chat));
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "overflow", async () =>
        {
            // The provider rejects the request as too long; generation compacts and retries the same attempt.
            var chat = await Open(fresh, 100_000);
            await History(chat);
            chat.Setup.Compaction = Manual with { Enabled = true };
            lock (chat.Summaries) chat.Summaries.Add(Chat.Of(Answer("OVERFLOW SUMMARY")));
            lock (chat.Agent) chat.Agent.Add(Chat.Of(Failure(Overflow)));
            await Turn(chat, Text("u4", 100), "fits");
            await chat.Harness.WaitForIdleAsync(Context);
            Same("overflow", await Dump(chat));
            await chat.Harness.CloseAsync(Context);
        });
    }
}
