// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-compaction.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - Tools return their results as ToolExecutionResult.Content: upstream's v1.2.0 `output` is area a's rename of that field, which renames
//   it in every test at once.
// - Backends. Upstream opens its chats on MemoryStorage and reopens a SQLite file for the recovery and blocked cases. harness.compaction.*
//   does the same; harness.compaction.jsonl.* reruns every storage-backed case on a fresh JSONL directory (reopening the same directory in
//   the reopen cases), and harness.compaction.sqlite.* reruns the cases upstream runs on MemoryStorage on a fresh SQLite file (its reopen
//   cases are the first pass's). The range selection and serialization cases open no storage and run once.
// - The scripted faux model is Script: two queues of steps (a message, or a function of the request) and the requests each side received.
//   A Request carries the request's cancellation token for upstream's `options.signal`.
// - chatSetup({ models: [...] }) is FauxOptions.Models. `setup.models.getModel = ...` and `setup.models.completeSimple = ...` are
//   SwitchableModels, a decorator installed before the Harness opens whose overrides the case sets and clears.
// - `outcome.result.submissionId` reads the completed outcome's JSON result; CompactionResult.FromJson checks its shape.
// - "is ordinary work: idle waits include it": upstream's 20 ms setTimeout before a negative check is Settled (Flush) on the idle wait.
// - "keeps a background summary queued through a retry backoff while a blocking compaction wins": upstream's 300 ms real backoff races the
//   test's steps during it. Here the Harness runs on a ManualTimeProvider (TimeProvider and Now), so the backoff ends only when the case
//   advances the clock after its steps: a deterministic barrier in place of the real-time window.
// - `hookContext.abortSignal` is the hook context's CancellationToken.
// - `expect.any(Number)` is the "<number>" placeholder of MatchJson/MatchExactly.
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessCompactionCases()
    {
        static Func<Task<IStorage>> SqliteFile()
        {
            var path = Path.Combine(Temp("compaction-reopen-sqlite"), "session.sqlite");
            return async () => await SqliteStorage.OpenAsync(path);
        }
        static Func<Task<IStorage>> JsonlDirectory()
        {
            var directory = Temp("compaction-reopen-jsonl");
            return async () => await JsonlStorage.OpenAsync(directory, DurableContext.Background);
        }
        return
        [
            .. CompactionTests.PureCases("harness.compaction."),
            .. CompactionTests.Cases("harness.compaction.", () => new MemoryStorage(), SqliteFile),
            .. CompactionTests.Cases("harness.compaction.jsonl.",
                () => JsonlStorage.OpenAsync(Temp("compaction-jsonl"), DurableContext.Background).GetAwaiter().GetResult(), JsonlDirectory),
            .. CompactionTests.Cases("harness.compaction.sqlite.",
                () => SqliteStorage.OpenAsync(Path.Combine(Temp("compaction-sqlite"), "session.sqlite")).GetAwaiter().GetResult(), null),
        ];
    }
}

/// <summary>The harness.compaction.* cases (upstream harness-compaction.test.ts).</summary>
internal static class CompactionTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private const string Overflow = "prompt is too long: 250000 tokens > 200000 maximum";

    private const string SummaryPrefix = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";

    /// <summary>Small thresholds: no automatic compaction unless a case enables it.</summary>
    private static readonly CompactionSettings Manual = new() { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 150, BackgroundTokens = 0 };

    /// <summary>Background threshold at 500 and blocking threshold at 1500 tokens of a 2000-token window.</summary>
    private static readonly CompactionSettings Background = new() { Enabled = true, ReserveTokens = 500, KeepRecentTokens = 150, BackgroundTokens = 1000 };

    /// <summary>Blocking threshold at 700 tokens of a 1000-token window, no background compaction.</summary>
    private static readonly CompactionSettings Blocking = new() { Enabled = true, ReserveTokens = 300, KeepRecentTokens = 150, BackgroundTokens = 0 };

    private static readonly CompactionSettings Enabled = Manual with { Enabled = true };

    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    /// <summary>Text of about <paramref name="tokens"/> estimated tokens, starting with <paramref name="label"/>.</summary>
    internal static string Text(string label, int tokens) => $"{label} {new string('x', Math.Max(0, tokens * 4 - label.Length - 1))}";

    // ─── Scripted faux model ────────────────────────────────────────────────

    internal sealed record Request(ImmutableArray<JsonValue> Messages, DurableStreamOptions? Options, string Model, CancellationToken Signal);

    /// <summary>A scripted answer: a message, or a function of the request (upstream <c>Step</c>).</summary>
    internal sealed class Step(Func<Request, Task<JsonObjectValue>> run)
    {
        public Func<Request, Task<JsonObjectValue>> Run { get; } = run;
        public static implicit operator Step(JsonObjectValue message) => new(_ => Task.FromResult(message));
    }

    /// <summary>A queue the case pushes to and requests shift from.</summary>
    internal sealed class Steps
    {
        private readonly List<Step> items = [];
        public void Push(params Step[] steps) { lock (items) items.AddRange(steps); }
        public Step? Shift()
        {
            lock (items)
            {
                if (items.Count == 0) return null;
                var first = items[0];
                items.RemoveAt(0);
                return first;
            }
        }
    }

    /// <summary>Scripted faux model that answers agent requests and summarization requests from separate queues (upstream <c>Script</c>).</summary>
    internal sealed class Script
    {
        public Steps Agent { get; } = new();
        public Steps Summaries { get; } = new();
        public SyncList<Request> AgentRequests { get; } = new();
        public SyncList<Request> SummaryRequests { get; } = new();
    }

    private static bool IsSummaryRequest(ImmutableArray<JsonValue> messages) =>
        messages.Length > 0 && messages[0] is JsonObjectValue first && first["role"] is JsonStringValue { Value: "system" } &&
        first["content"] is JsonStringValue { Value: var content } && content.StartsWith("You are a context summarization assistant", StringComparison.Ordinal);

    private static Script NewScript(ChatSetup setup)
    {
        var result = new Script();
        setup.Faux.SetResponses([.. Enumerable.Range(0, 500).Select(_ => (FauxStep)new FauxResponseFactory(async faux =>
        {
            var request = new Request(faux.Messages, faux.Options, faux.Model.Id, faux.Signal);
            var summary = IsSummaryRequest(request.Messages);
            (summary ? result.SummaryRequests : result.AgentRequests).Add(request);
            var step = (summary ? result.Summaries : result.Agent).Shift() ??
                throw new InvalidOperationException($"No scripted {(summary ? "summary" : "agent")} response");
            return await step.Run(request);
        }))]);
        return result;
    }

    /// <summary>A step that waits for <paramref name="gate"/>, or rejects when the request is cancelled.</summary>
    private static Step Gated(Deferred gate, JsonObjectValue message, Deferred? reached = null) => new(async request =>
    {
        reached?.Resolve();
        await await Task.WhenAny(gate.Task, Aborted(request.Signal));
        return message;
    });

    private static Step From(Func<Request, Task<JsonObjectValue>> run) => new(run);

    private static JsonObjectValue Answer(string content) => FauxAssistantMessage(content);
    private static JsonObjectValue Failure(string errorMessage) => FauxAssistantMessage("", stopReason: "error", errorMessage: errorMessage);
    private static JsonObjectValue Summary(string content = "SUMMARY") => Answer(content);

    /// <summary>Models whose lookups and summarization requests a case can replace (upstream's assignments to <c>setup.models</c>).</summary>
    private sealed class SwitchableModels(IDurableModels inner) : IDurableModels
    {
        public volatile Func<string, string, DurableModel?>? GetModelOverride;
        public volatile Func<Task<AssistantMessage>>? CompleteSimpleOverride;

        public DurableModel? GetModel(string provider, string modelId) =>
            GetModelOverride is { } replaced ? replaced(provider, modelId) : inner.GetModel(provider, modelId);

        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => inner.StreamSimple(model, messages, options, cancellationToken);

        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) =>
            CompleteSimpleOverride is { } replaced ? replaced() : inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.StreamDeferred(model, handle, cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.FetchDeferredAsync(model, handle, cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.CancelDeferredAsync(model, handle, cancellationToken);
    }

    // ─── Chats ──────────────────────────────────────────────────────────────

    private sealed record Chat(IHarness Harness, IConversation Root, ChatSetup Setup, Script Faux);

    private static ChatSetup ChatSetupWith(double contextWindow = 100_000) =>
        ChatTests.Setup(new FauxOptions { Models = [new FauxModelOptions("faux-1", contextWindow, 900)] });

    private static ValueTask<string?> Rendered(string? text) => ValueTask.FromResult(text);

    private static async Task<Chat> Open(Func<IStorage> storage, CompactionSettings? policy = null, double? contextWindow = null,
        ChatSetup? setup = null, IStorage? opened = null, TimeProvider? time = null)
    {
        setup ??= ChatSetupWith(contextWindow ?? 100_000);
        var faux = NewScript(setup);
        AddSection(setup.Registry, "preamble", (_, _) => Rendered("You are helpful."), tag: false);
        var (harness, root) = await OpenChat(opened ?? storage(), setup, timeProvider: time);
        // These cases exercise compaction accounting and thresholds, not the faux provider's cache simulation.
        setup.Stream = new ConversationStreamOptions { CacheRetention = "none" };
        setup.Compaction = policy ?? Manual;
        setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 1 };
        harness.Resume();
        return new Chat(harness, root, setup, faux);
    }

    /// <summary>Runs one turn: <paramref name="user"/> answered by <paramref name="reply"/>.</summary>
    private static async Task Turn(Chat chat, string user, string reply)
    {
        chat.Faux.Agent.Push(Answer(reply));
        var submission = await chat.Root.SubmitAsync(SubmissionDraft.Input(user), Context);
        Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, $"turn \"{user[..Math.Min(user.Length, 8)]}\"");
    }

    /// <summary>Three turns of about 100-token messages.</summary>
    private static async Task History(Chat chat)
    {
        await Turn(chat, Text("u1", 100), Text("a1", 100));
        await Turn(chat, Text("u2", 100), Text("a2", 100));
        await Turn(chat, Text("u3", 100), Text("a3", 100));
    }

    /// <summary>The terminal outcome's JSON.</summary>
    private static async Task<JsonObjectValue> Result(Chat chat, TaskId id) =>
        (JsonObjectValue)OutcomeJson(await chat.Harness.WaitForTaskAsync(id, Context));

    /// <summary><c>outcome.result.submissionId</c> of a completed outcome.</summary>
    private static SubmissionId SubmissionOf(JsonObjectValue outcome)
    {
        Equal("completed", S(outcome["status"]), "outcome status");
        return CompactionResult.FromJson(outcome["result"]).SubmissionId ?? throw new InvalidOperationException($"No submissionId in {outcome.ToJson()}");
    }

    private static async Task<ISubmission> SubmissionFor(Chat chat, JsonObjectValue outcome) =>
        (await chat.Harness.SubmissionAsync(SubmissionOf(outcome), Context))!;

    private static Task<List<string>> KindsOf(IConversation conversation) => Kinds(conversation);

    private static async Task<JsonObjectValue> LiveOf(Chat chat) =>
        await chat.Harness.SnapshotAsync(Live.LiveDoc, chat.Root.Id, Context) ?? JsonObjectValue.Empty;

    private static async Task<JsonValue?> Compactions(Chat chat) => (await LiveOf(chat)).TryGetValue("compactions", out var value) ? value : null;

    private static string UserText(JsonValue? message) => TextOf(message) ?? "";

    private static string? Role(JsonValue message) => message is JsonObjectValue value && value.TryGetValue("role", out var role) ? S(role) : null;

    /// <summary>Text of the first user message; a leading baseline system message comes before it.</summary>
    private static string FirstUserText(IEnumerable<JsonValue> messages) => UserText(messages.FirstOrDefault(message => Role(message) == "user"));

    private static async Task<JsonObjectValue> ModelUsage(Chat chat, string key = "faux/faux-1") =>
        (JsonObjectValue)((JsonObjectValue)(await chat.Harness.SnapshotAsync(UsageLedger.UsageDoc, chat.Root.Id, Context))!["models"])[key];

    private static JsonValue EntriesJson(IEnumerable<EntryRecord> entries) => JsonValue.Array(entries.Select(entry => (JsonValue)entry.ToJson()));

    private static void SameEntries(IEnumerable<EntryRecord> expected, IEnumerable<EntryRecord> actual, string what) =>
        Check(JsonValue.DeepEquals(EntriesJson(expected), EntriesJson(actual)), $"{what}: expected {EntriesJson(expected).ToJson()}, actual {EntriesJson(actual).ToJson()}");

    private static async Task<List<TaskRecord>> CompactionTasks(Chat chat) =>
        [.. (await chat.Harness.InspectAsync(Context)).Tasks.Select(task => task.Record).Where(record => record.Kind == "pi.compaction")];

    private static Installed Hook(Chat chat, Func<CompactionRequest, IHookApi, DurableContext, Task<CompactionDecision?>> beforeCompact) =>
        AddHooks(chat.Setup.Registry, HarnessTasks.CompactionTask, new CompactionHooks { BeforeCompact = beforeCompact });

    private static Installed Decline(Chat chat) => Hook(chat, (_, _, _) => Task.FromResult<CompactionDecision?>(CompactionDecision.Declined));

    private static TaskRecord Single(List<TaskRecord> records, string what)
    {
        Check(records.Count >= 1, $"{what}: no compaction task");
        return records[0];
    }

    /// <summary>Events delivered to a watchEvents listener.</summary>
    private sealed class Listening(IAgentEventStream stream)
    {
        public IAgentEventStream Stream { get; } = stream;
        public SyncList<IReadOnlyList<AgentEvent>> Batches { get; } = new();
        public List<AgentEvent> Events() => [.. Batches.Snapshot().SelectMany(batch => batch)];
        public List<string> Types() => [.. Events().Select(item => item.Type)];
    }

    private static async Task<Listening> Listen(Chat chat)
    {
        var listening = new Listening(await chat.Harness.WatchEventsAsync(chat.Root.Id, Context));
        listening.Stream.Start((batch, _) =>
        {
            listening.Batches.Add([.. batch]);
            return Task.CompletedTask;
        });
        return listening;
    }

    private static Task<TaskId> Compact(IConversation conversation, string? instructions = null) => conversation.CompactAsync(instructions, Context);

    private static Task<ISubmission> Submit(Chat chat, string text) => chat.Root.SubmitAsync(SubmissionDraft.Input(text), Context);

    // ─── Range selection ──────────────────────────────────────────────────────

    private static long nextId = 1;

    private static EntryRecord Entry(string kind, JsonObjectValue[] model, long? head = null) => new()
    {
        Id = new EntryId(Interlocked.Increment(ref nextId) - 1),
        ConversationId = new ConversationId(1),
        Kind = kind,
        Model = JsonValue.Array(model),
        Head = head is { } id ? new EntryId(id) : null,
    };

    private static JsonObjectValue UserMessage(string content) => Obj(("role", "user"), ("content", content), ("timestamp", 0));

    private static JsonObjectValue AssistantMessage(string content, string[]? calls = null, string stopReason = "stop") =>
        FauxAssistantMessage([FauxText(content), .. (calls ?? []).Select(id => FauxToolCall("read", JsonObjectValue.Empty, id))], stopReason: stopReason);

    private static JsonObjectValue ToolResultMessage(string callId, string content) => Obj(("role", "toolResult"), ("toolCallId", callId),
        ("toolName", "read"), ("content", JsonValue.Array(Obj(("type", "text"), ("text", content)))), ("isError", false), ("timestamp", 0));

    /// <summary>A view over <paramref name="entries"/> whose contributions are their models, with excluded assistants removed.</summary>
    private static ContextView View(List<EntryRecord> entries, EntryRecord? head = null)
    {
        List<EntryRecord> all = head is null ? entries : [head, .. entries];
        var contributions = all.Select(record => (record.Model ?? JsonArrayValue.Empty).Where(message =>
            Role(message) != "assistant" || !(((JsonObjectValue)message)["stopReason"] is JsonStringValue { Value: "error" or "aborted" or "deferred" }))
            .ToImmutableArray()).ToImmutableArray();
        return new ContextView
        {
            Head = head,
            Entries = [.. all],
            Contributions = contributions,
            Messages = ConversationContext.OrderToolResults([.. contributions.SelectMany(messages => messages)]),
        };
    }

    internal static IEnumerable<(string, Func<Task>)> PureCases(string prefix)
    {
        yield return Case(prefix + "range-selection.keeps-about-keeprecenttokens-and-cuts-at-the-first-candidate-at-or-after-the-budget", () =>
        {
            List<EntryRecord> entries =
            [
                Entry("pi.user", [UserMessage(Text("1", 10))]),
                Entry("pi.assistant", [AssistantMessage("2", ["c1"])]),
                Entry("pi.tool-result", [ToolResultMessage("c1", Text("3", 3000))]),
                Entry("pi.assistant", [AssistantMessage(Text("4", 10))]),
                Entry("pi.user", [UserMessage(Text("5", 10))]),
                Entry("pi.assistant", [AssistantMessage(Text("6", 10))]),
            ];
            Equal(3, Compaction.SelectCut(View(entries), 2000), "cut");
        });

        yield return Case(prefix + "range-selection.cuts-at-a-user-entry", () =>
        {
            List<EntryRecord> entries =
            [
                Entry("pi.user", [UserMessage(Text("u1", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a1", 100))]),
                Entry("pi.user", [UserMessage(Text("u2", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a2", 100))]),
            ];
            Equal(2, Compaction.SelectCut(View(entries), 150), "cut");
        });

        yield return Case(prefix + "range-selection.cuts-at-an-assistant-in-the-middle-of-one-long-run-and-never-at-a-tool-result", () =>
        {
            List<EntryRecord> entries = [Entry("pi.user", [UserMessage("do it")])];
            for (var index = 0; index < 5; index++)
            {
                entries.Add(Entry("pi.assistant", [AssistantMessage($"step {index}", [$"c{index}"])]));
                entries.Add(Entry("pi.tool-result", [ToolResultMessage($"c{index}", Text($"r{index}", 100))]));
            }
            var cut = Compaction.SelectCut(View(entries), 150)!.Value;
            // The budget is reached at the fourth result; the cut is the last call, whose result it keeps.
            Equal("pi.assistant", entries[cut].Kind, "cut kind");
            Equal(9, cut, "cut");
        });

        yield return Case(prefix + "range-selection.keeps-a-huge-last-tool-result-together-with-its-assistant", () =>
        {
            List<EntryRecord> entries =
            [
                Entry("pi.user", [UserMessage("u")]),
                Entry("pi.assistant", [AssistantMessage("a", ["c"])]),
                Entry("pi.tool-result", [ToolResultMessage("c", Text("big", 5000))]),
            ];
            Equal(1, Compaction.SelectCut(View(entries), 100), "cut");
        });

        yield return Case(prefix + "range-selection.never-cuts-at-a-system-entry-or-an-excluded-error-or-aborted-answer", () =>
        {
            List<EntryRecord> entries =
            [
                Entry("pi.user", [UserMessage(Text("u1", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a1", 100))]),
                Entry("pi.system", [Obj(("role", "system"), ("content", ""), ("sections", Obj(("s", Text("s", 100)))), ("timestamp", 0))]),
                Entry("pi.assistant", [AssistantMessage(Text("err", 100), [], "error")]),
                Entry("pi.assistant", [AssistantMessage(Text("stopped", 100), [], "aborted")]),
                Entry("pi.assistant", [AssistantMessage(Text("a2", 100))]),
            ];
            // The walk reaches 150 at the system entry; the excluded answers after it contribute nothing.
            Equal(5, Compaction.SelectCut(View(entries), 150), "cut");
        });

        yield return Case(prefix + "range-selection.never-cuts-at-a-display-only-entry-where-the-budget-is-reached-10407", () =>
        {
            List<EntryRecord> entries =
            [
                Entry("pi.user", [UserMessage(Text("u1", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a1", 100))]),
                Entry("chat.message", []),
                Entry("pi.assistant", [AssistantMessage(Text("a2", 100))]),
            ];
            // The budget is reached at a1; the display-only entry after it is skipped for the assistant a2.
            Equal(1, Compaction.SelectCut(View(entries), 150), "cut at 150");
            Equal(3, Compaction.SelectCut(View(entries), 50), "cut at 50");
        });

        yield return Case(prefix + "range-selection.follows-edited-contributions-an-omitted-entry-adds-nothing-and-is-no-candidate", () =>
        {
            List<EntryRecord> entries =
            [
                Entry("pi.user", [UserMessage(Text("u1", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a1", 100))]),
                Entry("pi.user", [UserMessage(Text("u2", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a2", 100))]),
            ];
            var plain = View(entries);
            Equal(2, Compaction.SelectCut(plain, 150), "plain cut");
            var contributions = plain.Contributions.Select((messages, index) => index == 2 ? [] : messages).ToImmutableArray();
            var omitted = plain with
            {
                Contributions = contributions,
                Messages = ConversationContext.OrderToolResults([.. contributions.SelectMany(messages => messages)]),
            };
            Equal(1, Compaction.SelectCut(omitted, 150), "omitted cut");
        });

        yield return Case(prefix + "range-selection.does-not-cut-at-a-user-entry-that-a-result-of-the-preceding-call-still-follows", () =>
        {
            List<EntryRecord> entries =
            [
                Entry("pi.user", [UserMessage(Text("u1", 100))]),
                Entry("pi.assistant", [AssistantMessage("a", ["c"])]),
                Entry("pi.user", [UserMessage(Text("steer", 100))]),
                Entry("pi.tool-result", [ToolResultMessage("c", Text("r", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a2", 100))]),
            ];
            // The budget is reached at the steer; its result follows it, so the cut moves to the next assistant.
            Equal(4, Compaction.SelectCut(View(entries), 250), "cut");
        });

        yield return Case(prefix + "range-selection.finds-nothing-when-the-budget-is-never-reached-or-only-the-marker-precedes-the-cut", () =>
        {
            List<EntryRecord> small = [Entry("pi.user", [UserMessage("hi")]), Entry("pi.assistant", [AssistantMessage("hello")])];
            Equal(null, Compaction.SelectCut(View(small), 150), "small");
            var marker = Entry("pi.compaction", [UserMessage("summary")], 0);
            // The budget is reached at the only entry after the marker, so the marker alone would be summarized.
            Equal(null, Compaction.SelectCut(View([Entry("pi.user", [UserMessage(Text("u", 200))])], marker), 150), "marker only");
        });

        yield return Case(prefix + "range-selection.summarizes-an-earlier-summary-marker-first", () =>
        {
            var marker = Entry("pi.compaction", [UserMessage("EARLIER")], 0);
            List<EntryRecord> kept =
            [
                Entry("pi.user", [UserMessage(Text("u1", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a1", 100))]),
                Entry("pi.user", [UserMessage(Text("u2", 100))]),
                Entry("pi.assistant", [AssistantMessage(Text("a2", 100))]),
            ];
            var selected = View(kept, marker);
            Equal(3, Compaction.SelectCut(selected, 150), "cut");
            Check(Compaction.SerializeConversation(selected.Contributions[..3].SelectMany(messages => messages)).StartsWith("[User]: EARLIER",
                StringComparison.Ordinal), "the marker is serialized first");
        });

        yield return Case(prefix + "serialization.writes-a-transcript-truncates-tool-results-and-omits-system-messages-and-thinking", () =>
        {
            var call = FauxToolCall("read", Obj(("path", "a.ts")), "c");
            JsonValue[] messages =
            [
                Obj(("role", "system"), ("content", ""), ("sections", Obj(("s", "hidden"))), ("timestamp", 0)),
                UserMessage("hello"),
                FauxAssistantMessage([FauxThinking("hmm"), FauxText("sure"), call]),
                ToolResultMessage("c", new string('y', 2500)),
            ];
            var serialized = Compaction.SerializeConversation(messages);
            Check(!serialized.Contains("hidden", StringComparison.Ordinal), "system omitted");
            Check(serialized.Contains("[User]: hello", StringComparison.Ordinal), "user");
            Check(!serialized.Contains("hmm", StringComparison.Ordinal), "thinking omitted");
            Check(!serialized.Contains("[Assistant thinking]", StringComparison.Ordinal), "no thinking label");
            Check(serialized.Contains("[Assistant]: sure", StringComparison.Ordinal), "text");
            Check(serialized.Contains("[Assistant tool calls]: read(path=\"a.ts\")", StringComparison.Ordinal), "calls");
            Check(serialized.Contains($"[Tool result]: {new string('y', 2000)}\n\n[... 500 more characters truncated]", StringComparison.Ordinal), "truncated");
        });
    }

    // ─── Storage-backed cases ───────────────────────────────────────────────

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh, Func<Func<Task<IStorage>>>? reopenable)
    {
        foreach (var item in ManualCases(prefix + "manual-compaction.", fresh)) yield return item;
        foreach (var item in OutcomeCases(prefix + "compaction-outcomes.", fresh)) yield return item;
        foreach (var item in BackgroundCases(prefix + "background-threshold-compaction.", fresh)) yield return item;
        foreach (var item in BlockingCases(prefix + "blocking-threshold-compaction.", fresh)) yield return item;
        foreach (var item in OverflowCases(prefix + "overflow-compaction.", fresh)) yield return item;
        foreach (var item in EstimateCases(prefix + "compaction-estimates-and-interactions.", fresh)) yield return item;
        foreach (var item in EventCases(prefix + "compaction-events-and-live-status.", fresh)) yield return item;
        if (reopenable is not null)
        {
            foreach (var item in RecoveryCases(prefix + "compaction-recovery.", reopenable)) yield return item;
            foreach (var item in BlockedCases(prefix + "blocked-compaction.", reopenable)) yield return item;
        }
        foreach (var item in InboxCases(prefix + "compaction-and-the-inbox.", fresh)) yield return item;
        foreach (var item in EdgeCases(prefix + "compaction-edge-cases.", fresh)) yield return item;
        foreach (var item in TogetherCases(prefix + "blocking-and-manual-compaction-together.", fresh)) yield return item;
        foreach (var item in PinningCases(prefix + "compaction-pinning-silent-overflow-and-late-policy-changes.", fresh)) yield return item;
        foreach (var item in ContributionCases(prefix + "context-contributions.", fresh)) yield return item;
    }

    // ─── Manual compaction ────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> ManualCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "places-the-summary-at-once-when-idle-and-keeps-raw-history", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var before = await AllEntries(chat.Root);
            var usageBefore = await ModelUsage(chat);
            chat.Faux.Summaries.Push(Summary());

            var id = await Compact(chat.Root, "focus on files");
            var outcome = await Result(chat, id);
            Equal("completed", S(outcome["status"]), "outcome");
            var placed = await (await SubmissionFor(chat, outcome)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, placed.Status, "placed");

            // Raw history is unchanged; one summary entry heads the first kept entry, u3.
            var after = await AllEntries(chat.Root);
            SameEntries(before, after.Take(before.Count), "raw history");
            var marker = after[^1];
            var u3 = before.First(record => UserText(record.Model?[0]).StartsWith("u3", StringComparison.Ordinal));
            MatchJson($$$"""{"kind":"pi.compaction","head":{{{u3.Id.Value}}},"data":{"reason":"manual"}}""", marker.ToJson(), "marker");
            Equal(marker.Id, placed.Entry, "placed entry");
            Equal(SummaryPrefix + "SUMMARY\n</summary>", UserText(marker.Model?[0]), "summary text");

            // The model context is the summary followed by the kept entries.
            var view = await chat.Root.ContextAsync(Context);
            Seq([UserText(marker.Model?[0]), Text("u3", 100), Text("a3", 100)], view.Messages.Select(UserText), "context");

            // The summarizer saw the serialized prefix, the prompt, and the instructions, without tools or caching.
            var request = chat.Faux.SummaryRequests[0];
            Equal(2, request.Messages.Length, "summary request messages");
            var prompt = UserText(request.Messages[1]);
            Check(Regex.IsMatch(prompt, "^<conversation>\n\\[User\\]: u1 "), "prompt starts with u1");
            Check(prompt.Contains("[Assistant]: a2 ", StringComparison.Ordinal), "prompt has a2");
            Check(!prompt.Contains("u3 ", StringComparison.Ordinal), "prompt lacks u3");
            Check(prompt.Contains("## Goal", StringComparison.Ordinal), "prompt has the format");
            Check(prompt.EndsWith("\n\nAdditional focus: focus on files", StringComparison.Ordinal), "prompt ends with the focus");
            var provider = await chat.Harness.SnapshotAsync(ProviderIdentity.ProviderDoc, chat.Root.Id, Context);
            Equal("none", request.Options?.CacheRetention, "cacheRetention");
            Equal(800.0, request.Options?.MaxTokens, "maxTokens");
            Equal(S(provider!["sessionId"]), request.Options?.SessionId, "sessionId");
            Check(request.Options?.Deferred is null, "deferred is absent");

            // The summarizer's spend is in the ledger, and nothing counts it again later.
            var usage = await ModelUsage(chat);
            Check(N(usage["input"]) > N(usageBefore["input"]), "the summary's input is counted");
            await Turn(chat, "next", "done");
            var agent = chat.Faux.AgentRequests.Snapshot()[^1].Messages;
            // The next request: the summary, the kept turn, the new input, then one complete system baseline.
            Seq(["user", "user", "assistant", "user", "system"], agent.Select(Role), "next request");
            MatchJson("""{"sections":{"preamble":"You are helpful."}}""", agent[4], "baseline");
            await chat.Harness.CloseAsync(Context);
        });

        // Regression coverage for #10424.
        yield return Case(prefix + "creates-provider-state-before-a-legacy-conversations-summarization-request", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            await chat.Root.CommitAsync(tx => tx.RetireDocAsync(ProviderIdentity.ProviderDoc, chat.Root.Id), Context);
            Check(await chat.Harness.SnapshotAsync(ProviderIdentity.ProviderDoc, chat.Root.Id, Context) is null, "retired");
            chat.Faux.Summaries.Push(Summary());
            Equal("completed", S((await Result(chat, await Compact(chat.Root)))["status"]), "outcome");
            var stored = await chat.Harness.SnapshotAsync(ProviderIdentity.ProviderDoc, chat.Root.Id, Context);
            Check(stored is not null && Regex.IsMatch(S(stored["sessionId"]), "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"),
                $"a UUIDv7 session ID: {stored?.ToJson()}");
            Equal(S(stored!["sessionId"]), chat.Faux.SummaryRequests[0].Options?.SessionId, "request sessionId");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-working-while-busy-and-places-the-summary-at-the-next-final-boundary", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Agent.Push(Gated(gate, Answer("late answer"), reached));
            var input = await Submit(chat, "busy");
            await reached.Task;
            chat.Faux.Summaries.Push(Summary());
            var outcome = await Result(chat, await Compact(chat.Root));
            var submission = await SubmissionFor(chat, outcome);
            Equal(SubmissionStatus.Queued, (await submission.StatusAsync(Context)).Status, "queued");
            gate.Resolve();
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "placed");
            // The summary follows the answer; the kept range still includes the busy turn.
            Seq(["pi.user", "pi.assistant", "pi.compaction"], (await KindsOf(chat.Root)).TakeLast(3), "kinds");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-a-queued-summary-at-posttools-and-the-run-continues-in-the-compacted-context", async () =>
        {
            var chat = await Open(fresh);
            AddTool(chat.Setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "wait",
                Description = "wait",
                Parameters = NoParameters,
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText("waited")) }),
            }));
            await chat.Root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(ToolsNamed(chat.Setup, "wait")) }, Context);
            await History(chat);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Agent.Push(Gated(gate, FauxAssistantMessage([FauxToolCall("wait", JsonObjectValue.Empty)], stopReason: "toolUse"), reached));
            chat.Faux.Agent.Push(Answer("after tools"));
            var input = await Submit(chat, "use a tool");
            await reached.Task;
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            gate.Resolve();
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            // The continuation request starts with the summary.
            var continuation = chat.Faux.AgentRequests.Snapshot()[^1].Messages;
            Check(UserText(continuation[0]).Contains("<summary>\nSUMMARY\n</summary>", StringComparison.Ordinal), "continuation starts with the summary");
            Seq(["pi.tool-result", "pi.compaction", "pi.system", "pi.assistant"], (await KindsOf(chat.Root)).TakeLast(4), "kinds");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-follow-ups-left-by-a-failed-run-after-placing-the-summary", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Agent.Push(Gated(gate, Failure("bad request"), reached));
            var failed = await Submit(chat, "fails");
            await reached.Task;
            var followUp = await Submit(chat, "follow-up");
            gate.Resolve();
            Equal(SubmissionStatus.Unanswered, (await failed.WaitAsync(Context)).Status, "failed");
            Equal(SubmissionStatus.Queued, (await followUp.StatusAsync(Context)).Status, "follow-up queued");

            chat.Faux.Summaries.Push(Summary());
            chat.Faux.Agent.Push(Answer("followed"));
            await Result(chat, await Compact(chat.Root));
            Equal(SubmissionStatus.Done, (await followUp.WaitAsync(Context)).Status, "follow-up");
            var request = chat.Faux.AgentRequests.Snapshot()[^1].Messages;
            Check(FirstUserText(request).Contains("SUMMARY", StringComparison.Ordinal), "summary first");
            Check(request.Any(message => UserText(message) == "follow-up"), "follow-up sent");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "settles-stale-when-a-reset-lands-while-it-summarizes", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary(), reached));
            var id = await Compact(chat.Root);
            await reached.Task;
            await chat.Root.ResetAsync(null, Context);
            gate.Resolve();
            var outcome = await Result(chat, id);
            MatchJson("""{"status":"unanswered","reason":"stale"}""", (await (await SubmissionFor(chat, outcome)).StatusAsync(Context)).ToJson(), "stale");
            Equal("pi.reset", (await KindsOf(chat.Root))[^1], "last kind");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-make-the-conversation-busy-a-submission-during-summarization-starts-its-run-at-once", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var summaryGate = new Deferred();
            var summaryReached = new Deferred();
            chat.Faux.Summaries.Push(Gated(summaryGate, Summary(), summaryReached));
            var id = await Compact(chat.Root);
            await summaryReached.Task;
            var answerGate = new Deferred();
            var answerReached = new Deferred();
            chat.Faux.Agent.Push(Gated(answerGate, Answer("a4"), answerReached));
            var input = await Submit(chat, "u4");
            // Placed and answered immediately with the uncompacted context, not queued behind the compaction.
            Equal(SubmissionStatus.Placed, (await input.StatusAsync(Context)).Status, "placed");
            await answerReached.Task;
            Equal(Text("u1", 100), FirstUserText(chat.Faux.AgentRequests.Snapshot()[^1].Messages), "uncompacted");
            // The summary is ready while the run is busy, so it queues and lands after the answer.
            summaryGate.Resolve();
            var submission = await SubmissionFor(chat, await Result(chat, id));
            Equal(SubmissionStatus.Queued, (await submission.StatusAsync(Context)).Status, "queued");
            answerGate.Resolve();
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "summary");
            Seq(["pi.user", "pi.assistant", "pi.compaction"], (await KindsOf(chat.Root)).TakeLast(3), "kinds");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "counts-the-spend-of-a-summary-that-ends-stale-and-writes-no-entry-for-it", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var before = N((await ModelUsage(chat))["input"]);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary(), reached));
            var id = await Compact(chat.Root);
            await reached.Task;
            await chat.Root.ResetAsync(null, Context);
            gate.Resolve();
            var submission = await SubmissionFor(chat, await Result(chat, id));
            MatchJson("""{"status":"unanswered","reason":"stale"}""", (await submission.StatusAsync(Context)).ToJson(), "stale");
            Check(N((await ModelUsage(chat))["input"]) > before, "spend counted");
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary entry");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lets-the-compaction-that-cuts-furthest-win-whatever-finishes-first", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var first = new Deferred();
            var firstReached = new Deferred();
            chat.Faux.Summaries.Push(Gated(first, Summary("FIRST"), firstReached));
            var early = await Compact(chat.Root);
            await firstReached.Task;
            await Turn(chat, Text("u4", 100), Text("a4", 100));
            chat.Faux.Summaries.Push(Summary("SECOND"));
            var later = await Result(chat, await Compact(chat.Root));
            Equal("completed", S(later["status"]), "later");
            first.Resolve();
            var outcome = await Result(chat, early);
            // The early compaction cut at u3, before the later cut at u4.
            MatchJson("""{"status":"unanswered","reason":"stale"}""", (await (await SubmissionFor(chat, outcome)).StatusAsync(Context)).ToJson(), "early");
            Check(FirstUserText((await chat.Root.ContextAsync(Context)).Messages).Contains("SECOND", StringComparison.Ordinal), "SECOND wins");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-an-older-selected-summary-that-cuts-later-than-the-newer-one", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            // A: small budget, late cut; selected first, finishes last.
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary("A"), reached));
            var a = await Compact(chat.Root);
            await reached.Task;
            // B: larger budget, earlier cut; placed first.
            chat.Setup.Compaction = Manual with { KeepRecentTokens = 350 };
            chat.Faux.Summaries.Push(Summary("B"));
            await Result(chat, await Compact(chat.Root));
            Check(FirstUserText((await chat.Root.ContextAsync(Context)).Messages).Contains('B'), "B placed");
            gate.Resolve();
            var outcome = await Result(chat, a);
            Equal(SubmissionStatus.Done, (await (await SubmissionFor(chat, outcome)).StatusAsync(Context)).Status, "A placed");
            var messages = (await chat.Root.ContextAsync(Context)).Messages;
            Check(FirstUserText(messages).Contains("<summary>\nA\n</summary>", StringComparison.Ordinal), "A first");
            Seq([Text("u3", 100), Text("a3", 100)], messages.Skip(1).Select(UserText), "kept");
            await chat.Harness.CloseAsync(Context);
        });

        foreach (var (order, keeps, stale) in new[] { ("before", new double[] { 150, 350 }, true), ("at", [150, 150], false), ("after", [350, 150], false) })
        {
            yield return Case(prefix + $"places-two-queued-summaries-in-one-boundary-when-the-second-cuts-{order}-the-first", async () =>
            {
                var chat = await Open(fresh);
                await History(chat);
                var gate = new Deferred();
                var reached = new Deferred();
                chat.Faux.Agent.Push(Gated(gate, Answer("done"), reached));
                var input = await Submit(chat, "busy");
                await reached.Task;
                var submissions = new List<ISubmission>();
                for (var index = 0; index < keeps.Length; index++)
                {
                    chat.Setup.Compaction = Manual with { KeepRecentTokens = keeps[index] };
                    chat.Faux.Summaries.Push(Summary($"S{index}"));
                    submissions.Add(await SubmissionFor(chat, await Result(chat, await Compact(chat.Root))));
                }
                gate.Resolve();
                await input.WaitAsync(Context);
                var statuses = new List<SubmissionStatus>();
                foreach (var submission in submissions) statuses.Add((await submission.WaitAsync(Context)).Status);
                Seq([SubmissionStatus.Done, stale ? SubmissionStatus.Unanswered : SubmissionStatus.Done], statuses, "statuses");
                await chat.Harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "is-aborted-by-conversation-abort-an-already-queued-summary-survives-it", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            var id = await Compact(chat.Root);
            await reached.Task;
            Equal(1, ((JsonArrayValue)(await Compactions(chat))!).Count, "one status");
            await chat.Root.AbortAsync(Context);
            Equal("aborted", S((await Result(chat, id))["status"]), "aborted");
            Check(await Compactions(chat) is null, "statuses removed");
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary");

            // Queued while busy, then Esc: the queued write stays and lands with the next run's boundary.
            var gate = new Deferred();
            var busy = new Deferred();
            chat.Faux.Agent.Push(Gated(gate, Answer("never"), busy));
            await Submit(chat, "busy");
            await busy.Task;
            chat.Faux.Summaries.Push(Summary());
            var queued = await Result(chat, await Compact(chat.Root));
            await chat.Root.AbortAsync(Context);
            var submission = await SubmissionFor(chat, queued);
            Equal(SubmissionStatus.Queued, (await submission.StatusAsync(Context)).Status, "still queued");
            Equal(SubmissionAbortResult.Aborted, await submission.AbortAsync(Context), "abort");
            await Turn(chat, "next", "ok");
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.StatusAsync(Context)).ToJson(), "aborted write");
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary entry");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "is-ordinary-work-idle-waits-include-it", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary(), reached));
            var id = await Compact(chat.Root);
            await reached.Task;
            var wait = chat.Root.WaitForIdleAsync(Context);
            Check(!await Settled(wait), "idle before the compaction ends");
            gate.Resolve();
            await wait;
            Equal(DurableTaskStatus.Terminal, (await chat.Harness.GetTaskAsync(id, Context))?.State.Status, "terminal");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "enables-scheduling-right-after-open", async () =>
        {
            var setup = ChatTests.Setup();
            var faux = NewScript(setup);
            var (harness, root) = await OpenChat(fresh(), setup);
            setup.Compaction = Manual with { KeepRecentTokens = 10 };
            var id = await Compact(root);
            // GetTaskAsync() only reads, so progress here comes from CompactAsync() itself.
            await Eventually(async () => (await harness.GetTaskAsync(id, Context))?.State.Status == DurableTaskStatus.Terminal);
            MatchJson("""{"outcome":{"status":"completed","result":{}}}""", (await harness.GetTaskAsync(id, Context))!.State.ToJson(), "state");
            Equal(0, faux.SummaryRequests.Count, "summary requests");
            await harness.CloseAsync(Context);
        });
    }

    // ─── Compaction outcomes ──────────────────────────────────────────────────

    /// <summary>Input tokens a compaction added to the ledger.</summary>
    private static async Task<double> CompactionInput(Chat chat, Func<Task> run)
    {
        var before = N((await ModelUsage(chat))["input"]);
        await run();
        return N((await ModelUsage(chat))["input"]) - before;
    }

    private static IEnumerable<(string, Func<Task>)> OutcomeCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "completes-without-a-summary-when-there-is-nothing-to-compact", async () =>
        {
            var chat = await Open(fresh);
            await Turn(chat, "hi", "hello");
            SameJson("""{"status":"completed","result":{}}""", await Result(chat, await Compact(chat.Root)), "outcome");
            Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
            Check(await Compactions(chat) is null, "no status");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "asks-beforecompact-the-first-decision-wins-a-throw-is-reported-and-skipped", async () =>
        {
            var chat = await Open(fresh);
            var seen = new SyncList<CompactionRequest>();
            Hook(chat, (_, _, _) => throw new InvalidOperationException("hook broke"));
            Hook(chat, (compaction, _, _) =>
            {
                seen.Add(compaction);
                return Task.FromResult<CompactionDecision?>(CompactionDecision.FromSummary("FROM HOOK"));
            });
            Decline(chat);
            await History(chat);
            var outcome = await Result(chat, await Compact(chat.Root, "why"));
            Equal("completed", S(outcome["status"]), "outcome");
            Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
            Check(FirstUserText((await chat.Root.ContextAsync(Context)).Messages).Contains("<summary>\nFROM HOOK\n</summary>", StringComparison.Ordinal),
                "hook summary placed");
            Seq(["hook broke"], chat.Setup.Reports.Snapshot().Select(error => error.Message), "reports");
            var compaction = seen[0];
            Equal("manual", compaction.Reason, "reason");
            Equal("why", compaction.Instructions, "instructions");
            Seq(["pi.user", "pi.system", "pi.assistant", "pi.user", "pi.assistant"], compaction.Entries.Select(record => record.Kind), "entries");
            Seq([Text("u1", 100), Text("a1", 100), Text("u2", 100), Text("a2", 100)],
                compaction.Messages.Where(message => Role(message) != "system").Select(UserText), "messages");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "completes-without-a-summary-when-a-hook-declines", async () =>
        {
            var chat = await Open(fresh);
            Decline(chat);
            await History(chat);
            SameJson("""{"status":"completed","result":{}}""", await Result(chat, await Compact(chat.Root)), "outcome");
            Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-with-no-model-without-a-configured-model", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            await chat.Root.ConfigureAsync(new AgentChange { Model = Patch<ModelRef>.Clear }, Context);
            MatchJson("""{"status":"failed","error":{"detail":{"reason":"no_model"}}}""", await Result(chat, await Compact(chat.Root)), "outcome");
            Check(await Compactions(chat) is null, "no status");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "retries-a-retryable-error-with-the-pinned-request-and-counts-every-attempt-once", async () =>
        {
            var single = await Open(fresh);
            await History(single);
            await single.Root.ConfigureAsync(new AgentChange { ThinkingLevel = Patch<string>.Set("high") }, Context);
            single.Faux.Summaries.Push(Summary());
            var once = await CompactionInput(single, async () => await Result(single, await Compact(single.Root)));
            Check(once > 0, "one attempt's input");
            await single.Harness.CloseAsync(Context);

            var chat = await Open(fresh);
            await History(chat);
            await chat.Root.ConfigureAsync(new AgentChange { ThinkingLevel = Patch<string>.Set("high") }, Context);
            chat.Setup.Stream = new ConversationStreamOptions { TimeoutMs = 1234, Deferred = J("true") };
            chat.Faux.Summaries.Push(From(async _ =>
            {
                // Changed during the attempt: the retry still uses the pinned request.
                await chat.Root.ConfigureAsync(new AgentChange { ThinkingLevel = Patch<string>.Set("low") }, Context);
                chat.Setup.Stream = new ConversationStreamOptions { TimeoutMs = 1 };
                return Failure("overloaded");
            }));
            chat.Faux.Summaries.Push(Summary());
            var twice = await CompactionInput(chat, async () =>
                Equal("completed", S((await Result(chat, await Compact(chat.Root)))["status"]), "outcome"));
            Equal(2, chat.Faux.SummaryRequests.Count, "summary requests");
            Equal(2 * once, twice, "every attempt counted once");
            var sessionId = S((await chat.Harness.SnapshotAsync(ProviderIdentity.ProviderDoc, chat.Root.Id, Context))!["sessionId"]);
            foreach (var request in chat.Faux.SummaryRequests.Snapshot())
            {
                Equal("high", request.Options?.Reasoning, "reasoning");
                Equal(1234.0, request.Options?.TimeoutMs, "timeoutMs");
                Equal("none", request.Options?.CacheRetention, "cacheRetention");
                Equal(sessionId, request.Options?.SessionId, "sessionId");
                Check(request.Options?.Deferred is null, "deferred is absent");
            }
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "adds-no-usage-when-a-hook-declines-or-supplies-the-summary", async () =>
        {
            foreach (var decision in new[] { CompactionDecision.Declined, CompactionDecision.FromSummary("HOOK") })
            {
                var chat = await Open(fresh);
                Hook(chat, (_, _, _) => Task.FromResult<CompactionDecision?>(decision));
                await History(chat);
                Equal(0.0, await CompactionInput(chat, async () => await Result(chat, await Compact(chat.Root))), "usage");
                await chat.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "caps-maxtokens-at-the-models-output-limit-and-sends-no-tools", async () =>
        {
            var chat = await Open(fresh);
            AddTool(chat.Setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "read",
                Description = "read",
                Parameters = NoParameters,
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty }),
            }));
            await chat.Root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(ToolsNamed(chat.Setup, "read")) }, Context);
            await History(chat);
            chat.Setup.Compaction = Manual with { ReserveTokens = 2000 };
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            var request = chat.Faux.SummaryRequests[0];
            // 0.8 * 2000 = 1600, above the model's 900.
            Equal(900.0, request.Options?.MaxTokens, "maxTokens");
            Equal(2, request.Messages.Length, "messages");
            Check(!request.Messages.Any(message => Role(message) == "system" && ((JsonObjectValue)message).ContainsKey("toolsAdded")), "no tools");
            await chat.Harness.CloseAsync(Context);
        });

        foreach (var (name, slug, response, message) in new (string, string, JsonObjectValue, string)[]
        {
            ("retries run out", "retries-run-out", Failure("overloaded"), "Summarization failed: overloaded"),
            ("a non-retryable error", "a-non-retryable-error", Failure("bad request"), "Summarization failed: bad request"),
            ("a length stop", "a-length-stop", FauxAssistantMessage("partial", stopReason: "length"),
                "Summarization hit the token limit; the summary is incomplete"),
            ("a tool call", "a-tool-call", FauxAssistantMessage([FauxToolCall("read", JsonObjectValue.Empty)], stopReason: "stop"),
                "Summarization attempted to call a tool"),
            ("empty text", "empty-text", Answer("  "), "Summarization produced no text"),
        })
        {
            yield return Case(prefix + $"fails-with-model-error-on-{slug}", async () =>
            {
                var chat = await Open(fresh);
                await History(chat);
                for (var index = 0; index < 3; index++) chat.Faux.Summaries.Push(response);
                var outcome = await Result(chat, await Compact(chat.Root));
                var expected = Obj(("status", "failed"), ("error", Obj(("message", message), ("detail", Obj(("reason", "model_error"))))));
                MatchJson(expected.ToJson(), outcome, "outcome");
                // The retry policy allows two retries after the first attempt.
                Equal(name == "retries run out" ? 3 : 1, chat.Faux.SummaryRequests.Count, "summary requests");
                Check(await Compactions(chat) is null, "no status");
                Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary");
                await chat.Harness.CloseAsync(Context);
            });
        }
    }

    // ─── Background threshold compaction ──────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> BackgroundCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "starts-above-the-background-threshold-without-blocking-the-run-idle-waits-and-esc-ignore-it", async () =>
        {
            var chat = await Open(fresh, contextWindow: 2000);
            await History(chat);
            chat.Setup.Compaction = Background;
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary(), reached));
            await Turn(chat, Text("u4", 100), Text("a4", 100));
            await reached.Task;
            var task = Single(await CompactionTasks(chat), "background");
            MatchJson("""{"background":true,"input":{"reason":"threshold"}}""", task.ToJson(), "task");
            Equal(null, task.Owner, "owner");
            SameJson($$"""[{"taskId":{{task.Id.Value}},"reason":"threshold","blocking":false,"attempt":1}]""", await Compactions(chat), "statuses");
            // Background work: neither conversation idle nor Esc waits for or stops it.
            await chat.Root.WaitForIdleAsync(Context);
            await chat.Root.AbortAsync(Context);
            Equal(DurableTaskStatus.Running, (await chat.Harness.GetTaskAsync(task.Id, Context))?.State.Status, "still running");
            gate.Resolve();
            Equal("completed", S((await Result(chat, task.Id))["status"]), "outcome");
            Equal("pi.compaction", (await KindsOf(chat.Root))[^1], "last kind");
            Check(FirstUserText((await chat.Root.ContextAsync(Context)).Messages).Contains("SUMMARY", StringComparison.Ordinal), "summary first");
            await chat.Harness.CloseAsync(Context);
        });

        foreach (var (slug, policy) in new[]
        {
            ("disabled", Background with { Enabled = false }),
            ("backgroundtokens-is-0", Background with { BackgroundTokens = 0 }),
            ("there-is-no-cut", Background with { KeepRecentTokens = 100_000 }),
        })
        {
            yield return Case(prefix + $"does-not-start-when-{slug}", async () =>
            {
                var chat = await Open(fresh, contextWindow: 2000);
                await History(chat);
                chat.Setup.Compaction = policy;
                await Turn(chat, Text("u4", 100), Text("a4", 100));
                Equal(0, (await CompactionTasks(chat)).Count, "compaction tasks");
                Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
                await chat.Harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "does-not-start-while-another-compaction-is-listed", async () =>
        {
            var chat = await Open(fresh, contextWindow: 2000);
            await History(chat);
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            var manual = await Compact(chat.Root);
            await reached.Task;
            chat.Setup.Compaction = Background;
            await Turn(chat, Text("u4", 100), Text("a4", 100));
            Seq([manual], (await CompactionTasks(chat)).Select(task => task.Id), "tasks");
            await chat.Harness.AbortTaskAsync(manual, Context);
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "records-a-failure-in-pi-live-the-snapshot-and-compaction-end-until-a-summary-is-produced", async () =>
        {
            var chat = await Open(fresh, contextWindow: 2000);
            await History(chat);
            chat.Setup.Compaction = Background;
            var listening = await Listen(chat);
            chat.Faux.Summaries.Push(Failure("bad request"));
            await Turn(chat, Text("u4", 100), Text("a4", 100));
            await Eventually(() => listening.Types().Contains("compaction_end"));
            var recorded = (JsonObjectValue)(await LiveOf(chat))["compactionFailure"];
            MatchJson("""{"reason":"threshold","error":"Summarization failed: bad request"}""", recorded, "recorded");
            SameJson($$"""{"type":"compaction_end","taskId":{{N(recorded["taskId"])}},"reason":"threshold","error":"Summarization failed: bad request"}""",
                listening.Events().First(item => item.Type == "compaction_end").ToJson(), "compaction_end");
            var reopened = await chat.Harness.WatchEventsAsync(chat.Root.Id, Context);
            SameJson(recorded.ToJson(), reopened.Snapshot.CompactionFailure, "snapshot");
            await reopened.StopAsync();
            // A compaction that produces a summary removes the failure, and ends without an error.
            chat.Setup.Compaction = Manual;
            chat.Faux.Summaries.Push(Summary());
            var manual = await Compact(chat.Root);
            Equal("completed", S((await Result(chat, manual))["status"]), "manual");
            Check(!(await LiveOf(chat)).ContainsKey("compactionFailure"), "failure removed");
            await Eventually(() => listening.Events().Any(item => item is CompactionEndEvent end && end.TaskId == manual));
            var manualEnd = (CompactionEndEvent)listening.Events().First(item => item is CompactionEndEvent end && end.TaskId == manual);
            Check(!manualEnd.ToJson().ContainsKey("error"), "no error");
            await listening.Stream.StopAsync();
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stops-through-aborttask-and-conversation-abort-with-background", async () =>
        {
            foreach (var stop in new[] { "task", "conversation" })
            {
                var chat = await Open(fresh, contextWindow: 2000);
                await History(chat);
                chat.Setup.Compaction = Background;
                var reached = new Deferred();
                chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
                await Turn(chat, Text("u4", 100), Text("a4", 100));
                await reached.Task;
                var task = Single(await CompactionTasks(chat), stop);
                if (stop == "task") await chat.Harness.AbortTaskAsync(task.Id, Context);
                else await chat.Root.AbortAsync(Context, new ConversationAbortOptions { Background = true });
                Equal("aborted", S((await Result(chat, task.Id))["status"]), $"{stop} outcome");
                Check(await Compactions(chat) is null, "no status");
                await chat.Harness.CloseAsync(Context);
            }
        });
    }

    // ─── Blocking threshold compaction ────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> BlockingCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "waits-for-its-compaction-which-appends-the-summary-before-the-request", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            // A new section: preparation has a system entry to append, but must not append it before the wait.
            AddSection(chat.Setup.Registry, "extra", (_, _) => Rendered("EXTRA"));
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary(), reached));
            chat.Faux.Agent.Push(Answer("a4"));
            var input = await Submit(chat, Text("u4", 200));
            await reached.Task;
            var child = Single(await CompactionTasks(chat), "blocking");
            var generation = N(At(await LiveOf(chat), "run", "taskId"));
            MatchJson($$$"""{"owner":{{{generation}}},"background":false,"input":{"reason":"threshold"}}""", child.ToJson(), "child");
            MatchJson($$$"""{"status":"waiting","on":[{{{child.Id.Value}}}],"checkpoint":{"phase":"prepare","attempt":1,"compacted":{{{child.Id.Value}}}}}""",
                (await chat.Harness.GetTaskAsync(new TaskId((long)generation), Context))!.State.ToJson(), "generation");
            SameJson($$"""[{"taskId":{{child.Id.Value}},"reason":"threshold","blocking":true,"attempt":1}]""", await Compactions(chat), "statuses");
            // Nothing was appended before the wait.
            Equal("pi.user", (await KindsOf(chat.Root))[^1], "last kind");
            gate.Resolve();
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            Equal("completed", S((await Result(chat, child.Id))["status"]), "child outcome");
            Seq(["pi.compaction", "pi.system", "pi.assistant"], (await KindsOf(chat.Root)).TakeLast(3), "kinds");
            var request = chat.Faux.AgentRequests.Snapshot()[^1].Messages;
            Check(FirstUserText(request).Contains("SUMMARY", StringComparison.Ordinal), "summary first");
            var systems = request.Where(message => Role(message) == "system").ToList();
            Equal(1, systems.Count, "one system message");
            MatchJson("""{"sections":{"preamble":"You are helpful.","extra":"<extra>\nEXTRA\n</extra>"}}""", systems[0], "baseline");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sends-the-request-once-without-a-second-compaction-when-the-kept-part-is-still-above-the-threshold", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking with { KeepRecentTokens = 700 };
            chat.Faux.Summaries.Push(Summary());
            await Turn(chat, Text("u4", 400), "a4");
            Equal(1, chat.Faux.SummaryRequests.Count, "summary requests");
            Equal(1, (await KindsOf(chat.Root)).Count(kind => kind == "pi.compaction"), "summaries");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sends-the-request-anyway-when-its-compaction-declines", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            Decline(chat);
            await Turn(chat, Text("u4", 200), "a4");
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary");
            Equal(Text("u1", 100), FirstUserText(chat.Faux.AgentRequests.Snapshot()[^1].Messages), "uncompacted request");
            Check(!(await LiveOf(chat)).ContainsKey("compactionFailure"), "no failure");
            await chat.Harness.CloseAsync(Context);
        });

        // A failed compaction leaves a context it judged too large; sending it once filled a 1M window and got an empty `length` answer on
        // every turn while the failure stayed invisible.
        yield return Case(prefix + "fails-the-run-with-the-compactions-error-without-a-request-when-its-compaction-fails", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            chat.Faux.Summaries.Push(Failure("bad request"));
            var requests = chat.Faux.AgentRequests.Count;
            var input = await Submit(chat, Text("u4", 200));
            MatchJson("""{"status":"unanswered","reason":"model_error","detail":"Compaction failed: Summarization failed: bad request"}""",
                (await input.WaitAsync(Context)).ToJson(), "input");
            Equal(requests, chat.Faux.AgentRequests.Count, "no request");
            Equal("pi.user", (await KindsOf(chat.Root))[^1], "last kind");
            var state = await LiveOf(chat);
            Check(!state.ContainsKey("compactions"), "no status");
            MatchJson("""{"reason":"threshold","error":"Summarization failed: bad request"}""", state["compactionFailure"], "failure");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-the-run-when-its-compaction-is-aborted-directly", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            var requests = chat.Faux.AgentRequests.Count;
            var input = await Submit(chat, Text("u4", 200));
            await reached.Task;
            var child = Single(await CompactionTasks(chat), "blocking");
            await chat.Harness.AbortTaskAsync(child.Id, Context);
            MatchJson("""{"status":"unanswered","reason":"model_error","detail":"Compaction was aborted"}""", (await input.WaitAsync(Context)).ToJson(), "input");
            Equal(requests, chat.Faux.AgentRequests.Count, "no request");
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary");
            // Aborting is not a failure.
            Check(!(await LiveOf(chat)).ContainsKey("compactionFailure"), "no failure");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "is-aborted-with-its-generation-by-esc-before-the-generations-abort-handler", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            var listening = await Listen(chat);
            var input = await Submit(chat, Text("u4", 200));
            await reached.Task;
            var child = Single(await CompactionTasks(chat), "blocking");
            await chat.Root.AbortAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await input.WaitAsync(Context)).ToJson(), "input");
            MatchJson("""{"outcome":{"status":"aborted"}}""", (await chat.Harness.GetTaskAsync(child.Id, Context))!.State.ToJson(), "child");
            await Eventually(() => listening.Types().Contains("run_end"));
            var types = listening.Types();
            Check(types.IndexOf("compaction_end") is >= 0 and var end && end < types.IndexOf("run_end"), $"compaction_end before run_end: {string.Join(", ", types)}");
            await listening.Stream.StopAsync();
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "wins-over-a-background-compaction-still-in-flight-which-then-settles-stale", async () =>
        {
            var chat = await Open(fresh, contextWindow: 2000);
            await History(chat);
            chat.Setup.Compaction = Background;
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary("BACKGROUND"), reached));
            await Turn(chat, Text("u4", 100), Text("a4", 100));
            await reached.Task;
            var background = Single(await CompactionTasks(chat), "background");
            chat.Faux.Summaries.Push(Summary("BLOCKING"));
            await Turn(chat, Text("u5", 1000), "a5");
            Check(FirstUserText((await chat.Root.ContextAsync(Context)).Messages).Contains("BLOCKING", StringComparison.Ordinal), "blocking first");
            gate.Resolve();
            var outcome = await Result(chat, background.Id);
            MatchJson("""{"status":"unanswered","reason":"stale"}""", (await (await SubmissionFor(chat, outcome)).StatusAsync(Context)).ToJson(), "stale");
            await chat.Harness.CloseAsync(Context);
        });
    }

    // ─── Overflow compaction ──────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> OverflowCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "compacts-and-retries-with-the-same-attempt-leaving-the-error-out-of-the-retry", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            chat.Setup.Compaction = Enabled;
            chat.Faux.Summaries.Push(Summary());
            double? attempt = null;
            chat.Faux.Agent.Push(Failure(Overflow));
            chat.Faux.Agent.Push(From(async _ =>
            {
                attempt = (await LiveOf(chat)).TryGetValue("generation", out var generation) ? N(((JsonObjectValue)generation)["attempt"]) : null;
                return Answer("fits");
            }));
            var input = await Submit(chat, Text("u4", 100));
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            Equal(1.0, attempt, "attempt");
            Seq(["pi.user", "pi.assistant", "pi.compaction", "pi.system", "pi.assistant"], (await KindsOf(chat.Root)).TakeLast(5), "kinds");
            var compaction = (await AllEntries(chat.Root)).First(record => record.Kind == "pi.compaction");
            MatchJson("""{"data":{"reason":"overflow"}}""", compaction.ToJson(), "summary");
            var retry = chat.Faux.AgentRequests.Snapshot()[^1].Messages;
            Check(UserText(retry[0]).Contains("SUMMARY", StringComparison.Ordinal), "summary first");
            Check(!retry.Any(message => Role(message) == "assistant" && ((JsonObjectValue)message)["stopReason"] is JsonStringValue { Value: "error" }),
                "the error is left out");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-a-second-overflow-with-its-error-entry", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            chat.Setup.Compaction = Enabled;
            chat.Faux.Summaries.Push(Summary());
            chat.Faux.Agent.Push(Failure(Overflow), Failure(Overflow));
            var input = await Submit(chat, Text("u4", 100));
            MatchJson($$"""{"status":"unanswered","reason":"model_error","detail":"{{Overflow}}"}""", (await input.WaitAsync(Context)).ToJson(), "input");
            Equal(1, (await KindsOf(chat.Root)).Count(kind => kind == "pi.compaction"), "summaries");
            Equal("pi.assistant", (await KindsOf(chat.Root))[^1], "last kind");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-without-compacting-when-compaction-is-disabled-even-for-a-retryable-looking-overflow", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            chat.Faux.Agent.Push(Failure($"overloaded: {Overflow}"));
            var input = await Submit(chat, Text("u4", 100));
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await input.WaitAsync(Context)).ToJson(), "input");
            Equal(4, chat.Faux.AgentRequests.Count, "agent requests");
            Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-after-a-blocking-threshold-compaction-in-the-same-generation", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            chat.Faux.Summaries.Push(Summary());
            chat.Faux.Agent.Push(Failure(Overflow));
            var input = await Submit(chat, Text("u4", 200));
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await input.WaitAsync(Context)).ToJson(), "input");
            Equal(1, chat.Faux.SummaryRequests.Count, "summary requests");
            await chat.Harness.CloseAsync(Context);
        });

        foreach (var (slug, prepare, requests) in new (string, Action<Chat>, int)[]
        {
            ("declines", chat => Decline(chat), 0),
            ("fails", chat => chat.Faux.Summaries.Push(Failure("bad request")), 1),
            // Classification finds no cut, so no compaction starts and the ordinary failure carries the text.
            ("cannot-cut", chat => chat.Setup.Compaction = Enabled with { KeepRecentTokens = 100_000 }, 0),
        })
        {
            yield return Case(prefix + $"fails-with-the-overflow-text-when-compaction-{slug}", async () =>
            {
                var chat = await Open(fresh);
                await Turn(chat, Text("u1", 100), Text("a1", 100));
                chat.Setup.Compaction = Enabled;
                prepare(chat);
                chat.Faux.Agent.Push(Failure(Overflow));
                var input = await Submit(chat, Text("u4", 100));
                MatchJson($$"""{"status":"unanswered","reason":"model_error","detail":"{{Overflow}}"}""", (await input.WaitAsync(Context)).ToJson(), "input");
                Equal(requests, chat.Faux.SummaryRequests.Count, "summary requests");
                await chat.Harness.CloseAsync(Context);
            });
        }
    }

    // ─── Compaction estimates and interactions ────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> EstimateCases(string prefix, Func<IStorage> fresh)
    {
        foreach (var clock in new[] { "real", "fixed" })
        {
            yield return Case(prefix + $"ignores-usage-measured-before-a-summary-placed-mid-run-{clock}-clock", async () =>
            {
                var setup = ChatSetupWith(2000);
                if (clock == "fixed") setup.Now = () => 1_000;
                var chat = await Open(fresh, contextWindow: 2000, setup: setup);
                var toolGate = new Deferred();
                var toolReached = new Deferred();
                AddTool(chat.Setup.Registry, Define.Tool(new ToolRegistration
                {
                    Name = "slow",
                    Description = "slow",
                    Parameters = NoParameters,
                    Execute = async (_, _, _) =>
                    {
                        toolReached.Resolve();
                        await toolGate.Task;
                        return new ToolExecutionResult { Output = JsonValue.Array(FauxText(Text("result", 200))) };
                    },
                }));
                await chat.Root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(ToolsNamed(chat.Setup, "slow")) }, Context);
                await Turn(chat, Text("u1", 220), Text("a1", 220));
                await Turn(chat, Text("u2", 220), Text("a2", 220));
                await Turn(chat, Text("u3", 220), Text("a3", 220));
                // Background at 900, blocking at 1500: the tool call's usage plus its result would cross 1500.
                chat.Setup.Compaction = Background with { BackgroundTokens = 600 };
                // The request starts a background compaction; its tool call's usage measures the whole context.
                chat.Faux.Agent.Push(FauxAssistantMessage([FauxToolCall("slow", JsonObjectValue.Empty)], stopReason: "toolUse"));
                chat.Faux.Agent.Push(Answer("done"));
                var summaryGate = new Deferred();
                var summaryReached = new Deferred();
                chat.Faux.Summaries.Push(Gated(summaryGate, Summary(), summaryReached));
                var input = await Submit(chat, Text("u4", 50));
                await Task.WhenAll(toolReached.Task, summaryReached.Task);
                var background = Single(await CompactionTasks(chat), "background");
                summaryGate.Resolve();
                // Queued: the run is busy in its tool round.
                var queued = await Result(chat, background.Id);
                Equal(SubmissionStatus.Queued, (await (await SubmissionFor(chat, queued)).StatusAsync(Context)).Status, "queued");
                toolGate.Resolve();
                Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
                // The summary landed at postTools; the successor saw a small context and did not compact again.
                Equal(1, chat.Faux.SummaryRequests.Count, "summary requests");
                Equal(0, (await CompactionTasks(chat)).Count, "compaction tasks");
                Check(FirstUserText(chat.Faux.AgentRequests.Snapshot()[^1].Messages).Contains("SUMMARY", StringComparison.Ordinal), "summary first");
                await chat.Harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "rebaselines-the-system-prompt-over-kept-system-deltas", async () =>
        {
            var chat = await Open(fresh);
            var mood = "cheerful";
            AddSection(chat.Setup.Registry, "mood", (_, _) => Rendered(Volatile.Read(ref mood)), tag: false);
            await Turn(chat, Text("u1", 100), Text("a1", 100));
            await Turn(chat, Text("u2", 100), Text("a2", 100));
            Volatile.Write(ref mood, "terse");
            await Turn(chat, Text("u3", 100), Text("a3", 100));
            chat.Setup.Compaction = Manual with { KeepRecentTokens = 250 };
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            // The kept range holds the delta for the terse mood; the next request has one complete baseline.
            Check((await chat.Root.ContextAsync(Context)).Entries.Any(record => record.Kind == "pi.system"), "a kept system entry");
            await Turn(chat, "next", "ok");
            var systems = chat.Faux.AgentRequests.Snapshot()[^1].Messages.Where(message => Role(message) == "system").ToList();
            Equal(1, systems.Count, "one system message");
            MatchJson("""{"sections":{"preamble":"You are helpful.","mood":"terse"}}""", systems[0], "baseline");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "summarizes-a-replaced-entrys-replacement-and-shows-it-to-the-hook", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var u1 = (await AllEntries(chat.Root))[0];
            await chat.Root.SubmitAsync(SubmissionDraft.Write(new EntryDraft
            {
                Kind = "app.redact",
                Edits = [ContextEdit.Replace(u1.Id, JsonValue.Array(UserMessage("REDACTED")))],
            }), Context);
            ImmutableArray<JsonValue> messages = [];
            Hook(chat, (compaction, _, _) =>
            {
                messages = compaction.Messages;
                return Task.FromResult<CompactionDecision?>(null);
            });
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            Equal("REDACTED", FirstUserText(messages), "hook messages");
            Check(UserText(chat.Faux.SummaryRequests[0].Messages[1]).Contains("[User]: REDACTED", StringComparison.Ordinal), "summarized replacement");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "compacts-a-fork-whose-cut-falls-on-a-parent-entry", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var entries = await AllEntries(chat.Root);
            var fork = await chat.Root.ForkAsync(entries[^1].Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            chat.Faux.Summaries.Push(Summary());
            var outcome = await Result(chat, await Compact(fork));
            Equal(SubmissionStatus.Done, (await (await SubmissionFor(chat, outcome)).WaitAsync(Context)).Status, "placed");
            var u3 = entries.First(record => UserText(record.Model?[0]).StartsWith("u3", StringComparison.Ordinal));
            var view = await fork.ContextAsync(Context);
            MatchJson($$"""{"kind":"pi.compaction","head":{{u3.Id.Value}},"conversationId":{{fork.Id.Value}}}""", view.Head?.ToJson(), "head");
            Seq([Text("u3", 100), Text("a3", 100)], view.Messages.Skip(1).Select(UserText), "kept");
            // The parent is untouched.
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "parent untouched");
            // The fork's view keeps the parent entries the summary kept.
            using var state = await fork.ViewStateAsync(Context);
            SameEntries(view.Entries, state.Value.Entries, "view entries");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "settles-stale-in-a-fork-reset-while-it-summarizes", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var entries = await AllEntries(chat.Root);
            var fork = await chat.Root.ForkAsync(entries[^1].Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary(), reached));
            var id = await Compact(fork);
            await reached.Task;
            await fork.ResetAsync(null, Context);
            gate.Resolve();
            var submission = await SubmissionFor(chat, await Result(chat, id));
            MatchJson("""{"status":"unanswered","reason":"stale"}""", (await submission.StatusAsync(Context)).ToJson(), "stale");
            Equal("pi.reset", (await fork.ContextAsync(Context)).Head?.Kind, "fork head");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-an-older-queued-summary-and-the-current-one-together-when-idle-admission-drains-the-inbox", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Agent.Push(Gated(gate, Failure("bad request"), reached));
            var failed = await Submit(chat, "fails");
            await reached.Task;
            chat.Faux.Summaries.Push(Summary("OLDER"));
            var older = await Result(chat, await Compact(chat.Root));
            gate.Resolve();
            await failed.WaitAsync(Context);
            // Idle now, with the older summary still queued; the current one queues behind it and a final boundary runs.
            chat.Faux.Summaries.Push(Summary("CURRENT"));
            var current = await Result(chat, await Compact(chat.Root));
            var statuses = new List<SubmissionStatus>();
            foreach (var outcome in new[] { older, current }) statuses.Add((await (await SubmissionFor(chat, outcome)).StatusAsync(Context)).Status);
            Seq([SubmissionStatus.Done, SubmissionStatus.Done], statuses, "statuses");
            Check(FirstUserText((await chat.Root.ContextAsync(Context)).Messages).Contains("CURRENT", StringComparison.Ordinal), "current first");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-a-hooks-summary-and-holds-while-work-the-hook-created-runs", async () =>
        {
            var chat = await Open(fresh);
            var childGate = new Deferred();
            var child = DefineChildTask(childGate);
            AddTask(chat.Setup.Registry, child);
            Hook(chat, async (_, api, hookContext) =>
            {
                await chat.Harness.CommitAsync(async tx =>
                {
                    await tx.CreateTaskAsync(child, JsonObjectValue.Empty, new TaskOptions(TaskOwnership.Task(api.TaskId)) { ConversationId = chat.Root.Id });
                }, hookContext);
                return CompactionDecision.FromSummary("HOOK");
            });
            await History(chat);
            var id = await Compact(chat.Root);
            await Eventually(async () => (await chat.Harness.GetTaskAsync(id, Context))?.State.Status == DurableTaskStatus.Completing);
            // The summary and the status removal landed at the hold.
            Equal("pi.compaction", (await KindsOf(chat.Root))[^1], "last kind");
            Check(await Compactions(chat) is null, "no status");
            childGate.Resolve();
            Equal("completed", S((await Result(chat, id))["status"]), "outcome");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "removes-the-status-of-a-faulted-compaction", async () =>
        {
            var setup = ChatSetupWith();
            var models = new SwitchableModels(setup.Faux);
            setup.Models = models;
            var chat = await Open(fresh, setup: setup);
            await History(chat);
            models.GetModelOverride = (_, _) => throw new InvalidOperationException("models broke");
            var outcome = await Result(chat, await Compact(chat.Root));
            models.GetModelOverride = null;
            MatchJson("""{"status":"faulted","error":{"message":"models broke"}}""", outcome, "outcome");
            Check(await Compactions(chat) is null, "no status");
            await chat.Harness.CloseAsync(Context);
        });
    }

    private static DurableTask DefineChildTask(Deferred gate) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.child",
        Version = 1,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
        {
            ["run"] = async (_, runtime, taskContext) =>
            {
                await gate.Task;
                await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Completed(JsonValue.Null), taskContext);
            },
        },
        Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted(), taskContext),
    });

    // ─── Compaction events and live status ────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> EventCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "reports-start-and-end-and-the-retry-backoff-in-a-late-joiners-snapshot", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            chat.Setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 60_000 };
            var listening = await Listen(chat);
            chat.Faux.Summaries.Push(Failure("overloaded"));
            var id = await Compact(chat.Root);
            await Eventually(async () => await Compactions(chat) is JsonArrayValue { Count: > 0 } statuses &&
                ((JsonObjectValue)statuses[0]).ContainsKey("retry"));
            var late = await chat.Harness.WatchEventsAsync(chat.Root.Id, Context);
            MatchExactly($$$"""[{"taskId":{{{id.Value}}},"reason":"manual","blocking":false,"attempt":1,"retry":{"at":"<number>","error":"overloaded"}}]""",
                late.Snapshot.Compactions, "late snapshot");
            await late.StopAsync();
            await chat.Harness.AbortTaskAsync(id, Context);
            await Eventually(() => listening.Types().Contains("compaction_end"));
            SameJson($$"""[{"type":"compaction_start","taskId":{{id.Value}},"reason":"manual","blocking":false},{"type":"compaction_end","taskId":{{id.Value}},"reason":"manual"}]""",
                JsonValue.Array(listening.Events().Where(item => item.Type.StartsWith("compaction_", StringComparison.Ordinal)).Select(item => (JsonValue)item.ToJson())),
                "events");
            await listening.Stream.StopAsync();
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "lists-concurrent-compactions-in-task-id-order", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var first = new Deferred();
            var second = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), first), Gated(new Deferred(), Summary(), second));
            var a = await Compact(chat.Root);
            var b = await Compact(chat.Root);
            await Task.WhenAll(first.Task, second.Task);
            Seq([a.Value, b.Value], ((JsonArrayValue)(await Compactions(chat))!).Select(status => (long)N(((JsonObjectValue)status)["taskId"])), "order");
            await chat.Root.AbortAsync(Context);
            Check(await Compactions(chat) is null, "no status");
            await chat.Harness.CloseAsync(Context);
        });
    }

    // ─── Recovery ─────────────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> RecoveryCases(string prefix, Func<Func<Task<IStorage>>> reopenable)
    {
        static async Task<Chat> Reopen(Func<Task<IStorage>> open, ChatSetup setup, Script faux)
        {
            var (harness, root) = await OpenChat(await open(), setup);
            harness.Resume();
            return new Chat(harness, root, setup, faux);
        }

        static async Task<Chat> First(Func<Task<IStorage>> open, ChatSetup? configured = null)
        {
            var setup = configured ?? ChatTests.Setup();
            AddSection(setup.Registry, "preamble", (_, _) => Rendered("You are helpful."), tag: false);
            var faux = NewScript(setup);
            var (harness, root) = await OpenChat(await open(), setup);
            setup.Compaction = Manual;
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 1 };
            harness.Resume();
            var chat = new Chat(harness, root, setup, faux);
            await History(chat);
            return chat;
        }

        yield return Case(prefix + "repeats-nothing-after-reopen-once-the-summary-is-placed", async () =>
        {
            var open = reopenable();
            var chat = await First(open);
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            var entries = await AllEntries(chat.Root);
            var usage = await chat.Harness.SnapshotAsync(UsageLedger.UsageDoc, chat.Root.Id, Context);
            await chat.Harness.CloseAsync(Context);
            chat = await Reopen(open, chat.Setup, chat.Faux);
            await chat.Harness.WaitForIdleAsync(Context);
            Equal(1, chat.Faux.SummaryRequests.Count, "summary requests");
            SameEntries(entries, await AllEntries(chat.Root), "entries");
            Check(JsonValue.DeepEquals(usage, await chat.Harness.SnapshotAsync(UsageLedger.UsageDoc, chat.Root.Id, Context)), "usage unchanged");
            var inspection = await chat.Harness.InspectAsync(Context);
            Equal(0, inspection.Tasks.Length, "tasks");
            Equal(0, inspection.Submissions.Length, "submissions");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-an-overflow-run-with-its-text-when-its-compaction-fails-after-reopen", async () =>
        {
            var open = reopenable();
            var chat = await First(open);
            chat.Setup.Compaction = Enabled;
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            chat.Faux.Agent.Push(Failure(Overflow));
            var input = await Submit(chat, Text("u4", 100));
            await reached.Task;
            var generation = (long)N(At(await LiveOf(chat), "run", "taskId"));
            MatchJson($$$"""{"status":"waiting","checkpoint":{"phase":"prepare","overflow":"{{{Overflow}}}"}}""",
                (await chat.Harness.GetTaskAsync(new TaskId(generation), Context))!.State.ToJson(), "generation");
            await chat.Harness.CloseAsync(Context);
            chat = await Reopen(open, chat.Setup, chat.Faux);
            chat.Faux.Summaries.Push(Failure("bad request"));
            var settled = await (await chat.Harness.SubmissionAsync(input.Id, Context))!.WaitAsync(Context);
            MatchJson($$"""{"status":"unanswered","reason":"model_error","detail":"{{Overflow}}"}""", settled.ToJson(), "settled");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reruns-select-and-its-hook-after-a-crash-in-select", async () =>
        {
            var open = reopenable();
            var chat = await First(open);
            var calls = 0;
            var reached = new Deferred();
            AddHooks(chat.Setup.Registry, HarnessTasks.CompactionTask, new CompactionHooks
            {
                BeforeCompact = async (_, _, hookContext) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        reached.Resolve();
                        await Aborted(hookContext.CancellationToken);
                    }
                    return null;
                },
            });
            var id = await Compact(chat.Root);
            await reached.Task;
            await chat.Harness.CloseAsync(Context);
            chat = await Reopen(open, chat.Setup, chat.Faux);
            chat.Faux.Summaries.Push(Summary());
            Equal("completed", S((await Result(chat, id))["status"]), "outcome");
            Equal(2, Volatile.Read(ref calls), "hook calls");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resends-an-interrupted-summarization-once-and-counts-only-the-answered-attempt", async () =>
        {
            var open = reopenable();
            var chat = await First(open);
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            var id = await Compact(chat.Root);
            await reached.Task;
            var usageBefore = await ModelUsage(chat);
            MatchJson("""{"checkpoint":{"phase":"summarize"}}""", (await chat.Harness.GetTaskAsync(id, Context))!.State.ToJson(), "summarizing");
            await chat.Harness.CloseAsync(Context);
            chat = await Reopen(open, chat.Setup, chat.Faux);
            chat.Faux.Summaries.Push(Summary());
            Equal("completed", S((await Result(chat, id))["status"]), "outcome");
            Equal(2, chat.Faux.SummaryRequests.Count, "summary requests");
            var requests = chat.Faux.SummaryRequests.Snapshot();
            // The same request, apart from the messages' timestamps.
            static JsonValue Untimed(IEnumerable<JsonValue> messages) =>
                JsonValue.Array(messages.Select(message => (JsonValue)((JsonObjectValue)message).SetItem("timestamp", 0)));
            Check(requests[1].Messages.All(message => ((JsonObjectValue)message)["timestamp"] is JsonNumberValue), "resent timestamps are numbers");
            Check(JsonValue.DeepEquals(Untimed(requests[0].Messages), Untimed(requests[1].Messages)), "resent request");
            var usage = await ModelUsage(chat);
            Check(N(usage["input"]) > N(usageBefore["input"]), "input counted");
            Equal(2.0, N(usage["output"]) - N(usageBefore["output"]), "only the answered attempt's output");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-a-retry-backoff-after-reopen", async () =>
        {
            var open = reopenable();
            var chat = await First(open);
            var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            chat.Setup.Now = () => Volatile.Read(ref now);
            chat.Setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 60_000 };
            chat.Faux.Summaries.Push(Failure("overloaded"));
            var id = await Compact(chat.Root);
            await Eventually(async () => await Compactions(chat) is JsonArrayValue { Count: > 0 } statuses &&
                ((JsonObjectValue)statuses[0]).ContainsKey("retry"));
            MatchJson("""{"checkpoint":{"phase":"retry","attempt":1}}""", (await chat.Harness.GetTaskAsync(id, Context))!.State.ToJson(), "retrying");
            await chat.Harness.CloseAsync(Context);
            Volatile.Write(ref now, now + 120_000);
            chat = await Reopen(open, chat.Setup, chat.Faux);
            chat.Faux.Summaries.Push(Summary());
            Equal("completed", S((await Result(chat, id))["status"]), "outcome");
            Equal(2, chat.Faux.SummaryRequests.Count, "summary requests");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-generation-waiting-on-its-blocking-compaction-across-reopen", async () =>
        {
            var open = reopenable();
            var chat = await First(open);
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            // The faux model window is 128k; this blocking threshold needs a small context window.
            chat.Setup.Compaction = Blocking with { ReserveTokens = 128_000 - 700 };
            var input = await Submit(chat, Text("u4", 200));
            await reached.Task;
            await chat.Harness.CloseAsync(Context);
            chat = await Reopen(open, chat.Setup, chat.Faux);
            chat.Faux.Summaries.Push(Summary());
            chat.Faux.Agent.Push(Answer("a4"));
            Equal(SubmissionStatus.Done, (await (await chat.Harness.SubmissionAsync(input.Id, Context))!.WaitAsync(Context)).Status, "input");
            Seq(["pi.compaction", "pi.system", "pi.assistant"], (await KindsOf(chat.Root)).TakeLast(3), "kinds");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-queued-summary-across-reopen-and-places-it-at-the-next-boundary", async () =>
        {
            var open = reopenable();
            var chat = await First(open);
            var reached = new Deferred();
            chat.Faux.Agent.Push(Gated(new Deferred(), Answer("never"), reached));
            var input = await Submit(chat, "busy");
            await reached.Task;
            chat.Faux.Summaries.Push(Summary());
            var outcome = await Result(chat, await Compact(chat.Root));
            await chat.Harness.CloseAsync(Context);
            chat = await Reopen(open, chat.Setup, chat.Faux);
            chat.Faux.Agent.Push(Answer("answered"));
            Equal(SubmissionStatus.Done, (await (await chat.Harness.SubmissionAsync(input.Id, Context))!.WaitAsync(Context)).Status, "input");
            Equal(SubmissionStatus.Done, (await (await SubmissionFor(chat, outcome)).WaitAsync(Context)).Status, "summary");
            await chat.Harness.CloseAsync(Context);
        });
    }

    // ─── Compaction and the inbox ─────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> InboxCases(string prefix, Func<IStorage> fresh)
    {
        static async Task<ISubmission> QueuedSummary(Chat chat, string content = "SUMMARY")
        {
            chat.Faux.Summaries.Push(Summary(content));
            return await SubmissionFor(chat, await Result(chat, await Compact(chat.Root)));
        }

        static async Task<(ISubmission Input, Action Release)> Busy(Chat chat, JsonObjectValue? reply = null)
        {
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Agent.Push(Gated(gate, reply ?? Answer("done"), reached));
            var input = await Submit(chat, "busy");
            await reached.Task;
            return (input, gate.Resolve);
        }

        yield return Case(prefix + "places-a-reset-queued-after-the-summary-last-and-makes-a-summary-queued-after-a-reset-stale", async () =>
        {
            foreach (var order in new[] { "summary first", "reset first" })
            {
                var chat = await Open(fresh);
                await History(chat);
                var run = await Busy(chat);
                ISubmission submission;
                if (order == "summary first")
                {
                    submission = await QueuedSummary(chat);
                    await chat.Root.ResetAsync(null, Context);
                }
                else
                {
                    await chat.Root.ResetAsync(null, Context);
                    submission = await QueuedSummary(chat);
                }
                run.Release();
                await run.Input.WaitAsync(Context);
                var settled = await submission.WaitAsync(Context);
                Equal(order == "summary first" ? SubmissionStatus.Done : SubmissionStatus.Unanswered, settled.Status, order);
                Equal("pi.reset", (await KindsOf(chat.Root))[^1], $"{order}: last kind");
                Equal("pi.reset", (await chat.Root.ContextAsync(Context)).Head?.Kind, $"{order}: head");
                await chat.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "places-a-summary-left-queued-by-a-failed-run-at-the-next-submission-before-its-input", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var run = await Busy(chat, Failure("bad request"));
            var submission = await QueuedSummary(chat);
            run.Release();
            Equal(SubmissionStatus.Unanswered, (await run.Input.WaitAsync(Context)).Status, "failed run");
            Equal(SubmissionStatus.Queued, (await submission.StatusAsync(Context)).Status, "queued");
            await Turn(chat, "again", "ok");
            Equal(SubmissionStatus.Done, (await submission.StatusAsync(Context)).Status, "placed");
            var request = chat.Faux.AgentRequests.Snapshot()[^1].Messages;
            Check(FirstUserText(request).Contains("SUMMARY", StringComparison.Ordinal), "summary first");
            Check(request.Select(UserText).Contains("again"), "input sent");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-full-retry-budget-after-an-overflow-compaction", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            chat.Setup.Compaction = Enabled;
            chat.Faux.Summaries.Push(Summary());
            chat.Faux.Agent.Push(Failure("prompt is too long"), Failure("overloaded"), Failure("overloaded"), Answer("finally"));
            var input = await Submit(chat, Text("u4", 100));
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "loses-an-application-edit-placed-while-a-compaction-summarizes-spec-12", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var u1 = (await AllEntries(chat.Root))[0];
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary(), reached));
            var id = await Compact(chat.Root);
            await reached.Task;
            await chat.Root.SubmitAsync(SubmissionDraft.Write(new EntryDraft
            {
                Kind = "app.redact",
                Edits = [ContextEdit.Replace(u1.Id, JsonValue.Array(UserMessage("REDACTED")))],
            }), Context);
            gate.Resolve();
            await Result(chat, id);
            // The summary was made from the unredacted entry, and the edit's target left the range.
            Check(UserText(chat.Faux.SummaryRequests[0].Messages[1]).Contains("[User]: u1 ", StringComparison.Ordinal), "unredacted");
            Check(!(await chat.Root.ContextAsync(Context)).Messages.Select(UserText).Contains("REDACTED"), "edit lost");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-kept-entries-mounted-in-the-conversation-view", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            using var state = await chat.Root.ViewStateAsync(Context);
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            await Eventually(() => state.Value.Entries is { Length: > 0 } entries && entries[0].Kind == "pi.compaction");
            SameEntries((await chat.Root.ContextAsync(Context)).Entries, state.Value.Entries, "mounted entries");
            await chat.Harness.CloseAsync(Context);
        });
    }

    // ─── Compaction edge cases ────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> EdgeCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "keeps-the-one-compaction-limit-through-a-retry-backoff", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            var asked = 0;
            Hook(chat, (_, _, _) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<CompactionDecision?>(CompactionDecision.Declined);
            });
            chat.Faux.Agent.Push(Failure("overloaded"));
            await Turn(chat, Text("u4", 200), "a4");
            // One blocking compaction, declined; the retried preparation, still above the threshold, started no other.
            Equal(1, Volatile.Read(ref asked), "hook calls");
            Equal(5, chat.Faux.AgentRequests.Count, "agent requests");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-start-a-background-compaction-when-a-manual-one-was-admitted-during-preparation", async () =>
        {
            var chat = await Open(fresh, contextWindow: 2000);
            await History(chat);
            chat.Setup.Compaction = Background;
            var rendering = new Deferred();
            var release = new Deferred();
            var hold = 1;
            AddSection(chat.Setup.Registry, "slow", async (_, _) =>
            {
                if (Interlocked.Exchange(ref hold, 0) == 1)
                {
                    rendering.Resolve();
                    await release.Task;
                }
                return "slow";
            });
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            chat.Faux.Agent.Push(Answer("a4"));
            var input = await Submit(chat, Text("u4", 100));
            await rendering.Task;
            var manual = await Compact(chat.Root);
            await reached.Task;
            release.Resolve();
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            Seq([manual], (await CompactionTasks(chat)).Select(task => task.Id), "tasks");
            // A background compaction would have sent its own summarization request.
            Equal(1, chat.Faux.SummaryRequests.Count, "summary requests");
            await chat.Harness.AbortTaskAsync(manual, Context);
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "starts-no-background-compaction-after-a-blocking-one-in-the-same-generation", async () =>
        {
            var chat = await Open(fresh, contextWindow: 2000);
            await History(chat);
            // Blocking at 1500, background at 200: the kept part stays above the background threshold.
            chat.Setup.Compaction = Background with { BackgroundTokens = 1300, KeepRecentTokens = 400 };
            chat.Faux.Summaries.Push(Summary());
            await Turn(chat, Text("u4", 1000), "a4");
            Equal(1, chat.Faux.SummaryRequests.Count, "summary requests");
            Equal(0, (await CompactionTasks(chat)).Count, "compaction tasks");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-the-run-when-its-blocking-compaction-faults-and-records-the-fault", async () =>
        {
            var setup = ChatSetupWith(1000);
            var models = new SwitchableModels(setup.Faux);
            setup.Models = models;
            var chat = await Open(fresh, contextWindow: 1000, setup: setup);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            models.CompleteSimpleOverride = () => throw new InvalidOperationException("no credentials");
            var input = await Submit(chat, Text("u4", 200));
            MatchJson("""{"status":"unanswered","reason":"model_error","detail":"Compaction failed: no credentials"}""", (await input.WaitAsync(Context)).ToJson(),
                "input");
            models.CompleteSimpleOverride = null;
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary");
            var state = await LiveOf(chat);
            Check(!state.ContainsKey("compactions"), "no status");
            MatchJson("""{"reason":"threshold","error":"no credentials"}""", state["compactionFailure"], "failure");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-an-overflow-run-with-its-text-when-its-compaction-is-aborted-directly-or-itself-overflows", async () =>
        {
            foreach (var how in new[] { "abort", "overflow" })
            {
                var chat = await Open(fresh);
                await History(chat);
                chat.Setup.Compaction = Enabled;
                var reached = new Deferred();
                if (how == "abort") chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
                else chat.Faux.Summaries.Push(Failure("prompt is too long for the summary"));
                chat.Faux.Agent.Push(Failure(Overflow));
                var input = await Submit(chat, Text("u4", 100));
                if (how == "abort")
                {
                    await reached.Task;
                    var child = Single(await CompactionTasks(chat), how);
                    await chat.Harness.AbortTaskAsync(child.Id, Context);
                }
                MatchJson($$"""{"status":"unanswered","reason":"model_error","detail":"{{Overflow}}"}""", (await input.WaitAsync(Context)).ToJson(), how);
                Check(await Compactions(chat) is null, $"{how}: no status");
                await chat.Harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "treats-a-length-stop-as-an-ordinary-answer-not-an-overflow", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            chat.Setup.Compaction = Enabled;
            chat.Faux.Agent.Push(FauxAssistantMessage("cut short", stopReason: "length", errorMessage: Overflow));
            var input = await Submit(chat, "go");
            Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
            Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "orders-the-events-of-a-summary-placed-at-once", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var listening = await Listen(chat);
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            await Eventually(() => listening.Types().Contains("compaction_end"));
            var types = listening.Types();
            var end = types.IndexOf("compaction_end");
            Seq(["message_start", "message_end", "compaction_end", "submission", "usage_changed"], types.Skip(end - 2).Take(5), "batch");
            await listening.Stream.StopAsync();
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "puts-compaction-start-last-in-the-batch-of-the-preparation-commit", async () =>
        {
            var chat = await Open(fresh, contextWindow: 2000);
            await History(chat);
            chat.Setup.Compaction = Background;
            var listening = await Listen(chat);
            // A new section makes the preparation commit append a system entry next to the compaction's status.
            AddSection(chat.Setup.Registry, "extra", (_, _) => Rendered("extra"));
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary()));
            await Turn(chat, Text("u4", 100), "a4");
            await Eventually(() => listening.Batches.Snapshot().Any(batch => batch.Any(item => item.Type == "compaction_start")));
            var batch = listening.Batches.Snapshot().First(candidate => candidate.Any(item => item.Type == "compaction_start"));
            Seq(["message_start", "message_end", "compaction_start"], batch.Select(item => item.Type), "batch");
            await listening.Stream.StopAsync();
            await chat.Harness.AbortTaskAsync(Single(await CompactionTasks(chat), "background").Id, Context);
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-background-summary-queued-through-a-retry-backoff-while-a-blocking-compaction-wins", async () =>
        {
            // The backoff ends only when the clock advances (see the file header).
            var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var setup = ChatSetupWith(2000);
            setup.Now = time.NowMs;
            var chat = await Open(fresh, contextWindow: 2000, setup: setup, time: time);
            await History(chat);
            chat.Setup.Compaction = Background;
            chat.Setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 300 };
            var summaryGate = new Deferred();
            var summaryReached = new Deferred();
            chat.Faux.Summaries.Push(Gated(summaryGate, Summary("BACKGROUND"), summaryReached));
            chat.Faux.Summaries.Push(Summary("BLOCKING"));
            var failed = new Deferred();
            chat.Faux.Agent.Push(From(_ =>
            {
                failed.Resolve();
                return Task.FromResult(Failure("overloaded"));
            }));
            chat.Faux.Agent.Push(Answer("a4"));
            var input = await Submit(chat, Text("u4", 100));
            await Task.WhenAll(failed.Task, summaryReached.Task);
            // During the backoff: the background summary queues, and the thresholds drop so the retry blocks.
            await Eventually(async () => (await LiveOf(chat)).TryGetValue("generation", out var generation) &&
                ((JsonObjectValue)generation).ContainsKey("retry"));
            var background = Single(await CompactionTasks(chat), "background");
            summaryGate.Resolve();
            var queued = await Result(chat, background.Id);
            var submission = await SubmissionFor(chat, queued);
            Equal(SubmissionStatus.Queued, (await submission.StatusAsync(Context)).Status, "queued");
            chat.Setup.Compaction = Background with { ReserveTokens = 1500, KeepRecentTokens = 50 };
            // The retry's backoff timer may not exist yet when the live retry status is visible (a loaded macos-14 runner advanced the
            // clock first, so the timer was due 300 ms after the advanced time and never fired). Advance until the input settles.
            var settled = input.WaitAsync(Context);
            for (var step = 0; !settled.IsCompleted && step < 400; step++)
            {
                time.Advance(TimeSpan.FromMilliseconds(300));
                await Task.WhenAny(settled, Task.Delay(50));
            }
            Equal(SubmissionStatus.Done, (await settled).Status, "input");
            Check(FirstUserText(chat.Faux.AgentRequests.Snapshot()[^1].Messages).Contains("BLOCKING", StringComparison.Ordinal), "blocking first");
            MatchJson("""{"status":"unanswered","reason":"stale"}""", (await submission.WaitAsync(Context)).ToJson(), "stale");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "summarizes-the-previous-summary-in-a-second-compaction", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            chat.Faux.Summaries.Push(Summary("FIRST"));
            await Result(chat, await Compact(chat.Root));
            await Turn(chat, Text("u4", 100), Text("a4", 100));
            await Turn(chat, Text("u5", 100), Text("a5", 100));
            chat.Faux.Summaries.Push(Summary("SECOND"));
            await Result(chat, await Compact(chat.Root));
            var prompt = UserText(chat.Faux.SummaryRequests[1].Messages[1]);
            Check(Regex.IsMatch(prompt, "^<conversation>\n\\[User\\]: The conversation history before this point was compacted"), "earlier summary first");
            Check(prompt.Contains("FIRST", StringComparison.Ordinal), "FIRST summarized");
            Check(FirstUserText((await chat.Root.ContextAsync(Context)).Messages).Contains("SECOND", StringComparison.Ordinal), "SECOND first");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "leaves-no-usage-submission-or-summary-when-the-classifying-commit-fails-which-fails-the-harness", async () =>
        {
            var storage = new ControlledStorage(fresh()) { KeepOpenForReopen = true };
            var chat = await Open(fresh, opened: storage);
            await History(chat);
            var before = await ModelUsage(chat);
            var failure = new IOException("disk gone");
            chat.Faux.Summaries.Push(From(_ =>
            {
                storage.FailNextCommit(failure);
                return Task.FromResult(Summary());
            }));
            var id = await Compact(chat.Root);
            Equal(new SessionEnd(SessionEndReason.Failed, failure), await chat.Harness.Closed, "closed");
            await chat.Harness.CloseAsync(Context);
            // Nothing of the failed commit landed: the task is still live, without usage, entry, or submission.
            storage.Reopen();
            Check(await storage.GetSubmissionByRequestAsync(chat.Root.Id, $"compaction:{id.Value}", Context) is null, "no summary submission");
            Check((await storage.GetTaskAsync(id, Context))?.State.Status != DurableTaskStatus.Terminal, "task still live");
            var (harness, root) = await OpenChat(storage, chat.Setup);
            var usage = (JsonObjectValue)((JsonObjectValue)(await harness.SnapshotAsync(UsageLedger.UsageDoc, root.Id, Context))!["models"])["faux/faux-1"];
            Check(JsonValue.DeepEquals(before, usage), "usage unchanged");
            Check(!(await KindsOf(root)).Contains("pi.compaction"), "no summary");
            await harness.CloseAsync(Context);
            await storage.Inner.CloseAsync(Context);
        });
    }

    // ─── Blocking and manual compaction together ──────────────────────────────

    private static IEnumerable<(string, Func<Task>)> TogetherCases(string prefix, Func<IStorage> fresh)
    {
        /// A run whose generation waits on a blocking compaction whose summary is held; returns the input and gate.
        static async Task<(ISubmission Input, Deferred Gate, TaskId Blocking)> BlockingRun(Chat chat)
        {
            await History(chat);
            chat.Setup.Compaction = Blocking;
            var gate = new Deferred();
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(gate, Summary("BLOCKING"), reached));
            var input = await Submit(chat, Text("u4", 200));
            await reached.Task;
            return (input, gate, Single(await CompactionTasks(chat), "blocking").Id);
        }

        yield return Case(prefix + "places-a-manual-summary-selected-before-the-blocking-one-landed-its-equal-cut-replaces-it", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            var run = await BlockingRun(chat);
            // Selected from the same context as the blocking compaction, so it cuts at the same entry.
            chat.Faux.Summaries.Push(Summary("MANUAL"));
            var submission = await SubmissionFor(chat, await Result(chat, await Compact(chat.Root)));
            Equal(SubmissionStatus.Queued, (await submission.StatusAsync(Context)).Status, "queued");
            chat.Faux.Agent.Push(Answer("a4"));
            run.Gate.Resolve();
            Equal(SubmissionStatus.Done, (await run.Input.WaitAsync(Context)).Status, "input");
            // The request after the blocking compaction used its summary; the manual one landed at the final boundary.
            Check(FirstUserText(chat.Faux.AgentRequests.Snapshot()[^1].Messages).Contains("BLOCKING", StringComparison.Ordinal), "blocking request");
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "manual placed");
            var markers = (await AllEntries(chat.Root)).Where(record => record.Kind == "pi.compaction").ToList();
            Equal(2, markers.Count, "markers");
            Equal(markers[0].Head, markers[1].Head, "equal cut");
            var messages = (await chat.Root.ContextAsync(Context)).Messages;
            Check(FirstUserText(messages).Contains("MANUAL", StringComparison.Ordinal), "manual first");
            Check(!messages.Any(message => UserText(message).Contains("BLOCKING", StringComparison.Ordinal)), "blocking replaced");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "finds-nothing-to-compact-for-a-manual-compaction-selected-after-the-blocking-summary-landed", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            var run = await BlockingRun(chat);
            var answerGate = new Deferred();
            var answerReached = new Deferred();
            chat.Faux.Agent.Push(Gated(answerGate, Answer("a4"), answerReached));
            run.Gate.Resolve();
            await answerReached.Task;
            SameJson("""{"status":"completed","result":{}}""", await Result(chat, await Compact(chat.Root)), "outcome");
            Equal(1, chat.Faux.SummaryRequests.Count, "summary requests");
            answerGate.Resolve();
            Equal(SubmissionStatus.Done, (await run.Input.WaitAsync(Context)).Status, "input");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "aborts-the-run-and-both-compactions-on-esc-and-appends-nothing", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            var run = await BlockingRun(chat);
            var manualReached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary("MANUAL"), manualReached));
            var manual = await Compact(chat.Root);
            await manualReached.Task;
            Seq([true, false], ((JsonArrayValue)(await Compactions(chat))!).Select(status => ((JsonObjectValue)status)["blocking"] is JsonBooleanValue { Value: true }),
                "blocking flags");
            await chat.Root.AbortAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await run.Input.WaitAsync(Context)).ToJson(), "input");
            Equal("aborted", S((await Result(chat, run.Blocking))["status"]), "blocking");
            Equal("aborted", S((await Result(chat, manual))["status"]), "manual");
            Check(await Compactions(chat) is null, "no status");
            Check(!(await LiveOf(chat)).ContainsKey("run"), "no run");
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary");
            Equal(0, (await chat.Harness.InspectAsync(Context)).Submissions.Length, "submissions");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-a-summary-that-survived-esc-at-the-next-submission-before-its-input", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var reached = new Deferred();
            chat.Faux.Agent.Push(Gated(new Deferred(), Answer("never"), reached));
            var busy = await Submit(chat, "busy");
            await reached.Task;
            chat.Faux.Summaries.Push(Summary());
            var submission = await SubmissionFor(chat, await Result(chat, await Compact(chat.Root)));
            await chat.Root.AbortAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await busy.WaitAsync(Context)).ToJson(), "busy");
            Equal(SubmissionStatus.Queued, (await submission.StatusAsync(Context)).Status, "queued");
            await Turn(chat, "u5", "a5");
            Equal(SubmissionStatus.Done, (await submission.StatusAsync(Context)).Status, "placed");
            var request = chat.Faux.AgentRequests.Snapshot()[^1].Messages;
            Check(FirstUserText(request).Contains("SUMMARY", StringComparison.Ordinal), "summary first");
            Check(request.Select(UserText).Contains("u5"), "input sent");
            Seq(["pi.compaction", "pi.user", "pi.system", "pi.assistant"], (await AllEntries(chat.Root)).TakeLast(4).Select(record => record.Kind), "tail");
            await chat.Harness.CloseAsync(Context);
        });
    }

    // ─── Compaction pinning, silent overflow, and late policy changes ─────────

    private static IEnumerable<(string, Func<Task>)> PinningCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "keeps-the-pinned-model-through-a-retry-and-advances-the-live-attempt", async () =>
        {
            var setup = ChatTests.Setup(new FauxOptions
            {
                Models = [new FauxModelOptions("faux-1", 100_000, 900), new FauxModelOptions("faux-2", 100_000, 900)],
            });
            var chat = await Open(fresh, setup: setup);
            await History(chat);
            double? attempt = null;
            chat.Faux.Summaries.Push(From(async _ =>
            {
                // Switched during the attempt: the retry still uses the pinned model.
                await chat.Root.ConfigureAsync(new AgentChange { Model = Patch<ModelRef>.Set(new ModelRef("faux", "faux-2")) }, Context);
                return Failure("overloaded");
            }));
            chat.Faux.Summaries.Push(From(async _ =>
            {
                attempt = await Compactions(chat) is JsonArrayValue { Count: > 0 } statuses ? N(((JsonObjectValue)statuses[0])["attempt"]) : null;
                return Summary();
            }));
            Equal("completed", S((await Result(chat, await Compact(chat.Root)))["status"]), "outcome");
            Seq(["faux-1", "faux-1"], chat.Faux.SummaryRequests.Snapshot().Select(request => request.Model), "models");
            Equal(2.0, attempt, "live attempt");
            var usage = (JsonObjectValue)(await chat.Harness.SnapshotAsync(UsageLedger.UsageDoc, chat.Root.Id, Context))!["models"];
            Seq(["faux/faux-1"], usage.Keys, "usage keys");
            await chat.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shows-a-late-joiner-a-compaction-that-is-summarizing", async () =>
        {
            var chat = await Open(fresh);
            await History(chat);
            var reached = new Deferred();
            chat.Faux.Summaries.Push(Gated(new Deferred(), Summary(), reached));
            var id = await Compact(chat.Root);
            await reached.Task;
            var stream = await chat.Harness.WatchEventsAsync(chat.Root.Id, Context);
            SameJson($$"""[{"taskId":{{id.Value}},"reason":"manual","blocking":false,"attempt":1}]""", stream.Snapshot.Compactions, "snapshot");
            await stream.StopAsync();
            await chat.Harness.AbortTaskAsync(id, Context);
            await chat.Harness.CloseAsync(Context);
        });

        foreach (var (slug, response) in new[]
        {
            ("a-stop-whose-input-exceeds-the-window", Answer("fine")),
            ("a-length-stop-that-fills-the-window-without-output", FauxAssistantMessage("", stopReason: "length")),
        })
        {
            yield return Case(prefix + $"treats-silent-overflow-as-an-ordinary-answer-{slug}", async () =>
            {
                var chat = await Open(fresh, contextWindow: 300);
                await History(chat);
                // Thresholds out of reach, so only overflow classification could compact.
                chat.Setup.Compaction = new CompactionSettings { Enabled = true, ReserveTokens = -100_000, KeepRecentTokens = 150, BackgroundTokens = 0 };
                chat.Faux.Agent.Push(response);
                var input = await Submit(chat, "go");
                Equal(SubmissionStatus.Done, (await input.WaitAsync(Context)).Status, "input");
                var last = (await AllEntries(chat.Root))[^1];
                var message = (JsonObjectValue)last.Model![0];
                Check(N(((JsonObjectValue)message["usage"])["input"]) >= 300, $"input above the window: {message["usage"].ToJson()}");
                Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
                await chat.Harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "sends-the-request-when-a-blocking-compaction-finds-nothing-under-a-policy-changed-after-preparation", async () =>
        {
            var chat = await Open(fresh, contextWindow: 1000);
            await History(chat);
            chat.Setup.Compaction = Blocking;
            var changed = 0;
            AddSection(chat.Setup.Registry, "policy", (_, _) =>
            {
                // Rendering runs after preparation read the policy; the compaction reads this one.
                if (Interlocked.Exchange(ref changed, 1) == 0) chat.Setup.Compaction = Blocking with { KeepRecentTokens = 100_000 };
                return Rendered("p");
            });
            await Turn(chat, Text("u4", 200), "a4");
            Equal(0, (await CompactionTasks(chat)).Count, "compaction tasks");
            Equal(0, chat.Faux.SummaryRequests.Count, "summary requests");
            Check(!(await KindsOf(chat.Root)).Contains("pi.compaction"), "no summary");
            await chat.Harness.CloseAsync(Context);
        });
    }

    // ─── Blocked compaction ───────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> BlockedCases(string prefix, Func<Func<Task<IStorage>>> reopenable)
    {
        yield return Case(prefix + "survives-reopen-blocked-and-is-orphaned-on-abort-with-its-status-removed", async () =>
        {
            var open = reopenable();
            var setup = ChatTests.Setup();
            var (harness, root) = await OpenChat(await open(), setup);
            // A compaction stored by a newer version than this process registers, for example after a downgrade.
            var definition = HarnessTasks.CompactionTask.Definition;
            var newer = Tasks.DefineTask(new TaskDefinition
            {
                Name = definition.Name,
                Version = 2,
                Initial = definition.Initial,
                Phases = definition.Phases,
                Abort = definition.Abort,
            });
            var id = await root.CommitAsync(async tx =>
            {
                var taskId = await tx.CreateTaskAsync(newer, O("""{"reason":"manual"}"""), new TaskOptions(TaskOwnership.Conversation));
                var live = await tx.DocAsync(Live.LiveDoc, root.Id);
                live.Set("compactions", J($$"""[{"taskId":{{taskId.Value}},"reason":"manual","blocking":false,"attempt":1}]"""));
                return taskId;
            }, Context);
            await harness.CloseAsync(Context);
            (harness, root) = await OpenChat(await open(), setup);
            harness.Resume();
            var chat = new Chat(harness, root, setup, NewScript(setup));
            var listening = await Listen(chat);
            var inspection = await chat.Harness.InspectAsync(Context);
            var state = inspection.Tasks.First(task => task.Record.Id == id).State;
            Equal(TaskInspectionKind.Blocked, state.Kind, "blocked");
            Equal(BlockedReason.TaskTooOld, state.Reason, "reason");
            await chat.Harness.AbortTaskAsync(id, Context);
            SameJson("""{"status":"orphaned","reason":"task_too_old"}""", OutcomeJson(await chat.Harness.WaitForTaskAsync(id, Context)), "outcome");
            Check(await Compactions(chat) is null, "no status");
            await Eventually(() => listening.Types().Contains("compaction_end"));
            await listening.Stream.StopAsync();
            await chat.Harness.CloseAsync(Context);
        });
    }

    // ─── Context contributions ────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> ContributionCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "apply-edits-carried-by-an-older-head-marker-in-the-range", async () =>
        {
            var chat = await Open(fresh);
            var ids = await chat.Root.CommitAsync(async tx =>
            {
                var a = await tx.AppendEntryAsync(chat.Root.Id, new EntryDraft { Kind = "app.note", Model = JsonValue.Array(UserMessage("a")) });
                var b = await tx.AppendEntryAsync(chat.Root.Id, new EntryDraft { Kind = "app.note", Model = JsonValue.Array(UserMessage("b")) });
                // An older marker that omits b, then a newer one whose range still contains the older marker.
                await tx.AppendEntryAsync(chat.Root.Id, new EntryDraft { Kind = "app.head", Head = EntryHead.At(a.Id), Edits = [ContextEdit.Omit(b.Id)] });
                var c = await tx.AppendEntryAsync(chat.Root.Id, new EntryDraft { Kind = "app.note", Model = JsonValue.Array(UserMessage("c")) });
                await tx.AppendEntryAsync(chat.Root.Id, new EntryDraft { Kind = "app.head", Head = EntryHead.At(a.Id), Model = JsonValue.Array(UserMessage("H")) });
                return (A: a.Id, B: b.Id, C: c.Id);
            }, Context);
            var view = await chat.Root.ContextAsync(Context);
            Seq([ids.A, ids.B, ids.C], view.Entries.Skip(1).Select(record => record.Id), "entries");
            SameJson("""[["H"],["a"],[],["c"]]""",
                JsonValue.Array(view.Contributions.Select(messages => (JsonValue)JsonValue.Array(messages.Select(message => (JsonValue)UserText(message))))),
                "contributions");
            Seq(["H", "a", "c"], view.Messages.Select(UserText), "messages");
            // The summarizer sees the same contributions: the omitted entry stays out.
            chat.Setup.Compaction = Manual with { KeepRecentTokens = 1 };
            chat.Faux.Summaries.Push(Summary());
            await Result(chat, await Compact(chat.Root));
            var prompt = UserText(chat.Faux.SummaryRequests[0].Messages[1]);
            Check(prompt.Contains("<conversation>\n[User]: H\n\n[User]: a\n</conversation>", StringComparison.Ordinal), $"prompt: {prompt[..Math.Min(prompt.Length, 120)]}");
            await chat.Harness.CloseAsync(Context);
        });
    }
}
