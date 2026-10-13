// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-generation.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - Backends. Upstream runs this file on MemoryStorage (ControlledStorage over memory where it inspects commits). harness.generation.* does
//   the same; harness.generation.jsonl.* and harness.generation.sqlite.* rerun every storage-backed case on a fresh JSONL directory and a
//   fresh SQLite file. The two cases that open no storage ("resolves settings over the built-in defaults", "rejects a registry without the
//   built-in tasks") run once.
// - Scripted streams (upstream's `{ [Symbol.asyncIterator], result }` objects passed through a Proxy) are ChatTests.ScriptedStream through
//   ModelsOverride. Upstream's opening event carries the partial text; here the text follows an empty start frame (see ScriptedStream).
// - "faults a run task...": upstream's final message carries a function, which is not strict JSON, and the fault detail contains
//   "non-JSON function". PiSharp.Contracts messages cannot carry a function; the C# message carries a non-finite usage count, which is not
//   strict JSON either, and the detail contains Chord's "non-finite number".
// - `expect(seen[0]!.signal).toBeInstanceOf(AbortSignal)` checks that the request's cancellation token can be cancelled.
// - `toContainEqual(new Error(text))` checks for a report whose message is the text.
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

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessGenerationCases() =>
    [
        .. GenerationTests.StorageCases("harness.generation.", () => new MemoryStorage()),
        .. GenerationTests.StorageCases("harness.generation.jsonl.",
            () => JsonlStorage.OpenAsync(Temp("generation-jsonl"), DurableContext.Background).GetAwaiter().GetResult()),
        .. GenerationTests.StorageCases("harness.generation.sqlite.",
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("generation-sqlite"), "session.sqlite")).GetAwaiter().GetResult()),
        .. GenerationTests.StorageFreeCases("harness.generation."),
    ];
}

/// <summary>The harness.generation.* cases (upstream harness-generation.test.ts).</summary>
internal static class GenerationTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly JsonObjectValue Error503 = FauxAssistantMessage([], stopReason: "error", errorMessage: "503 Service Unavailable");

    private static Task<JsonObjectValue?> LiveOf(IHarness harness, IConversation conversation) =>
        harness.SnapshotAsync(Live.LiveDoc, conversation.Id, Context);

    private static async Task<TaskId> RunTask(IHarness harness, IConversation conversation)
    {
        TaskId? taskId = null;
        await Eventually(async () =>
        {
            var live = await LiveOf(harness, conversation);
            if (live is not null && live.TryGetValue("run", out var run)) taskId = new TaskId((long)N(((JsonObjectValue)run)["taskId"]));
            return taskId is not null;
        });
        return taskId!.Value;
    }

    /// <summary>Every published <c>pi.live</c> value (upstream <c>livePublications</c>).</summary>
    private static SyncList<JsonObjectValue> LivePublications(IHarness harness)
    {
        var values = new SyncList<JsonObjectValue>();
        harness.SubscribeCommits((publication, _) =>
        {
            foreach (var change in publication.Changes)
                if (change is DocumentCommitChange { Record.Kind: "pi.live", Value: { } value }) values.Add(value);
        });
        return values;
    }

    private static JsonValue? Generation(JsonObjectValue value, string key) =>
        value.TryGetValue("generation", out var generation) && ((JsonObjectValue)generation).TryGetValue(key, out var field) ? field : null;

    private static Task<ISubmission> Submit(IConversation conversation, string text, WhenBusy? whenBusy = null) =>
        conversation.SubmitAsync(SubmissionDraft.Input(text, whenBusy), Context);

    private static async Task<List<TaskRecord>> TasksOf(IHarness harness, IConversation conversation) =>
        [.. (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = conversation.Id }, 10), Context)).Items];

    private static ValueTask<string?> Text(string? text) => ValueTask.FromResult(text);

    /// <summary>A final message whose usage is not strict JSON, after one committed partial (upstream <c>invalidFinalStream</c>).</summary>
    internal static IAsyncEnumerable<StreamEvent> InvalidFinalStream(CancellationToken cancellationToken)
    {
        var final = Native(FauxAssistantMessage("final"));
        return ScriptedStream("partial", 300, final with { Usage = final.Usage with { Input = double.NaN } }, cancellationToken);
    }

    internal static IEnumerable<(string, Func<Task>)> StorageCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "answers-an-input-and-settles-its-submission", async () =>
        {
            var setup = Setup();
            AddSection(setup.Registry, "preamble", (_, _) => Text("You are helpful."), tag: false);
            setup.Faux.SetResponses(FauxAssistantMessage("Hello there"));
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            var submission = await Submit(root, "hi");
            var settled = await submission.WaitAsync(Context);
            if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input) throw new InvalidOperationException($"Unexpected {settled.Status}");

            var answer = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context);
            Equal("Hello there", TextOf(answer?.Model?[0]), "answer text");
            var entries = await AllEntries(root);
            Seq(["pi.user", "pi.system", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            Equal(settled.Entry, entries[0].Id, "user entry");
            MatchExactly("""[{"role":"system","content":"","sections":{"preamble":"You are helpful."},"timestamp":"<number>"}]""", entries[1].Model, "system entry");
            SameJson("{}", await LiveOf(harness, root), "live");
            var task = (await TasksOf(harness, root))[0];
            Equal("pi.generation", task.Kind, "task kind");
            // Entries written by the generation are attributed to it; the admitted user entry is not task work.
            Seq(new TaskId?[] { null, task.Id, task.Id }, entries.Select(entry => entry.ByTaskId), "byTaskId");
            SameJson("""{"status":"terminal","outcome":{"status":"completed","result":{"entryId":""" + settled.Answer + "}}}", task.State.ToJson(), "task state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stores-partials-as-deltas-and-a-complete-base-once-nothing-is-in-flight", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 200, MinTokenSize = 1, MaxTokenSize = 1 });
            setup.Faux.SetResponses(FauxAssistantMessage(new string('w', 200)));
            var storage = new ControlledStorage(fresh());
            var (harness, root) = await OpenChat(storage, setup);
            var record = await storage.FindDocumentAsync(new DocumentAddress("pi.live", DocumentScope.Conversation(root.Id)), DocumentPoint.Current, Context);
            harness.Resume();
            await (await Submit(root, "hi")).WaitAsync(Context);
            var contents = storage.Commits.SelectMany(writes => writes.OfType<DocumentChangeWrite>().Where(write => write.Id == record!.Id)
                .Select(write => write.Content is DocumentDelta ? "delta" : "base")).ToList();
            // Streaming writes deltas; the commit that settles the answer clears generation and writes a base.
            Check(contents.Contains("delta"), $"a delta: [{string.Join(", ", contents)}]");
            Equal("base", contents[^1], "last content");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "still-ends-a-run-whose-input-something-else-already-settled", async () =>
        {
            var setup = Setup();
            var busy = Unanswered();
            setup.Faux.SetResponses(busy.Step);
            var (harness, root) = await OpenChat(fresh(), setup);
            var submission = await Submit(root, "hi");
            await busy.Reached;
            await root.CommitAsync(tx => tx.SettleSubmission(submission.Id, SubmissionSettlement.Unanswered("withdrawn")), Context);
            var taskId = await RunTask(harness, root);
            await harness.AbortTaskAsync(taskId, Context);
            SameJson("""{"status":"aborted"}""", OutcomeJson(await harness.WaitForTaskAsync(taskId, Context)), "outcome");
            // The earlier settlement stays; the run's own settlement leaves it unchanged.
            MatchJson("""{"status":"unanswered","reason":"withdrawn"}""", (await submission.StatusAsync(Context)).ToJson(), "submission");
            SameJson("{}", await LiveOf(harness, root), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-with-no-model-when-no-model-is-configured-or-the-model-is-unknown", async () =>
        {
            var setup = Setup();
            var (harness, root) = await OpenChat(fresh(), setup);
            var plain = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            harness.Resume();
            var unset = await (await Submit(plain, "hi")).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"no_model"}""", unset.ToJson(), "unset");

            await root.ConfigureAsync(new AgentChange { Model = new ModelRef("faux", "missing") }, Context);
            var unknown = await (await Submit(root, "hi")).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"no_model","entry":"<number>"}""", unknown.ToJson(), "unknown");
            Seq(["pi.user"], await Kinds(root), "kinds");
            var tasks = await TasksOf(harness, root);
            SameJson("""{"status":"terminal","outcome":{"status":"failed","error":{"message":"Model faux/missing is not available","detail":{"reason":"no_model"}}}}""",
                tasks[0].State.ToJson(), "task state");
            SameJson("{}", await LiveOf(harness, root), "root live");
            SameJson("{}", await LiveOf(harness, plain), "plain live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "retries-a-retryable-error-after-a-durable-backoff-and-then-answers", async () =>
        {
            var setup = Setup();
            AddSection(setup.Registry, "preamble", (_, _) => Text("p"), tag: false);
            setup.Faux.SetResponses(Error503, FauxAssistantMessage("recovered"));
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 3, BaseDelayMs = 1 };
            var (harness, root) = await OpenChat(fresh(), setup);
            var values = LivePublications(harness);
            harness.Resume();
            var settled = await (await Submit(root, "hi")).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            var entries = await AllEntries(root);
            Seq(["pi.user", "pi.system", "pi.assistant", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            Equal("error", S(((JsonObjectValue)entries[2].Model![0])["stopReason"]), "first stop reason");
            Check(values.Snapshot().Any(value => Generation(value, "retry") is JsonObjectValue retry && S(retry["error"]) == "503 Service Unavailable"),
                "a published retry");
            Check(values.Snapshot().Any(value => Generation(value, "attempt") is JsonNumberValue { Value: 2 }), "a published attempt 2");
            SameJson("{}", await LiveOf(harness, root), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-with-model-error-once-retries-are-exhausted", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(Error503, Error503, FauxAssistantMessage("never"));
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 1, BaseDelayMs = 1 };
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            var settled = await (await Submit(root, "hi")).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"model_error","detail":"503 Service Unavailable"}""", settled.ToJson(), "settled");
            Seq(["pi.user", "pi.assistant", "pi.assistant"], await Kinds(root), "kinds");
            Equal(1, setup.Faux.PendingResponseCount, "pending responses");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-a-retryable-error-without-retrying-when-the-retry-policy-is-disabled", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(Error503, FauxAssistantMessage("never"));
            setup.Retry = new RetrySettings { Enabled = false };
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            var settled = await (await Submit(root, "hi")).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", settled.ToJson(), "settled");
            Equal(1, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-section-wrapper-failures-while-preparing", async () =>
        {
            var setup = Setup();
            AddSection(setup.Registry, "cwd", (_, _) => Text("/repo"));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "broken",
                Wraps = [Define.WrapSection("cwd", _ => throw new InvalidOperationException("wrapper failed"))],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("ok"));
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            Equal(SubmissionStatus.Done, (await (await Submit(root, "hi")).WaitAsync(Context)).Status, "status");
            Check(setup.Reports.Snapshot().Any(error => error.Message == "wrapper failed"), "the wrapper failure is reported");
            // The failed section is absent, so nothing was rendered.
            Seq(["pi.user", "pi.assistant"], await Kinds(root), "kinds");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-a-non-retryable-error-without-retrying", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage([], stopReason: "error", errorMessage: "Invalid request"), FauxAssistantMessage("never"));
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            var settled = await (await Submit(root, "hi")).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"model_error","detail":"Invalid request"}""", settled.ToJson(), "settled");
            var tasks = await TasksOf(harness, root);
            MatchJson("""{"outcome":{"status":"failed","error":{"message":"Invalid request","detail":{"reason":"model_error"}}}}""", tasks[0].State.ToJson(), "state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "polls-a-deferred-response-until-it-is-ready", async () =>
        {
            var setup = Setup(new FauxOptions { PendingFetches = 1, PollAfterMs = 1 });
            setup.Faux.SetResponses(FauxAssistantMessage("deferred answer"));
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            var (harness, root) = await OpenChat(fresh(), setup);
            var values = LivePublications(harness);
            harness.Resume();
            var settled = await (await Submit(root, "hi")).WaitAsync(Context);
            if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input) throw new InvalidOperationException($"Unexpected {settled.Status}");
            Equal(2, setup.Faux.State.DeferredFetchCount, "fetches");
            var pollTimes = values.Snapshot().SelectMany(value => Generation(value, "deferred") is JsonObjectValue deferred ? [N(deferred["pollAt"])] : Array.Empty<double>())
                .ToList();
            Equal(2, pollTimes.Count, "poll times");
            Check(pollTimes[1] > pollTimes[0], $"later poll: {pollTimes[0]}, {pollTimes[1]}");
            var answer = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context);
            Equal("deferred answer", TextOf(answer?.Model?[0]), "answer");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "converts-the-committed-partial-when-aborted-during-streaming", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 20, MinTokenSize = 1, MaxTokenSize = 1 });
            setup.Faux.SetResponses(FauxAssistantMessage(new string('x', 400)));
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            var submission = await Submit(root, "hi");
            var taskId = await RunTask(harness, root);
            await Eventually(async () => await LiveOf(harness, root) is { } live && Generation(live, "message") is { } message && TextOf(message) is not null);
            var partial = Generation((await LiveOf(harness, root))!, "message");
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(taskId, Context), "abort");
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
            var entries = await AllEntries(root);
            Seq(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            var converted = (JsonObjectValue)entries[1].Model![0];
            Equal("aborted", S(converted["stopReason"]), "stop reason");
            Check(TextOf(converted)!.StartsWith(TextOf(partial)!, StringComparison.Ordinal), "the converted entry extends the partial");
            SameJson("{}", await LiveOf(harness, root), "live");
            SameJson("""{"status":"aborted"}""", OutcomeJson(await harness.WaitForTaskAsync(taskId, Context)), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cancels-a-deferred-response-when-aborted-during-polling", async () =>
        {
            var setup = Setup(new FauxOptions { PendingFetches = 100, PollAfterMs = 60_000 });
            setup.Faux.SetResponses(FauxAssistantMessage("never"));
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            var submission = await Submit(root, "hi");
            var taskId = await RunTask(harness, root);
            await Eventually(async () => await LiveOf(harness, root) is { } live && Generation(live, "deferred") is not null);
            await harness.AbortTaskAsync(taskId, Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
            Equal(1, setup.Faux.State.CancelledDeferred.Count, "cancelled");
            SameJson("{}", await LiveOf(harness, root), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-a-failed-deferred-cancellation-and-still-ends-the-run-aborted", async () =>
        {
            var setupBase = Setup(new FauxOptions { PendingFetches = 100, PollAfterMs = 60_000 });
            setupBase.Faux.SetResponses(FauxAssistantMessage("never"));
            var models = new ModelsOverride(setupBase.Models)
            {
                CancelDeferredOverride = (_, _, _) => Task.FromException(new InvalidOperationException("cancel failed")),
            };
            var setup = setupBase.WithModels(models);
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            var (harness, root) = await OpenChat(fresh(), setup);
            harness.Resume();
            var submission = await Submit(root, "hi");
            var taskId = await RunTask(harness, root);
            await Eventually(async () => await LiveOf(harness, root) is { } live && Generation(live, "deferred") is not null);
            await harness.AbortTaskAsync(taskId, Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
            Check(setup.Reports.Snapshot().Any(error => error.Message == "cancel failed"), "the cancel failure is reported");
            SameJson("{}", await LiveOf(harness, root), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "forwards-stream-options-and-the-thinking-level", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<FauxRequest>();
            setup.Faux.SetResponses(
                FauxStep.From(request => { seen.Add(request); return FauxAssistantMessage("a"); }),
                FauxStep.From(request => { seen.Add(request); return FauxAssistantMessage("b"); }));
            setup.Stream = new ConversationStreamOptions { TimeoutMs = 1234, Headers = new Dictionary<string, string> { ["x-test"] = "1" } };
            var (harness, root) = await OpenChat(fresh(), setup);
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = "high" }, Context);
            harness.Resume();
            await (await Submit(root, "one")).WaitAsync(Context);
            // Both are read at the next preparation: the thinking level from pi.agent, the stream options live from settings.
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = Patch<string>.Clear }, Context);
            setup.Stream = new ConversationStreamOptions { TimeoutMs = 99 };
            await (await Submit(root, "two")).WaitAsync(Context);
            var sessionId = S((await harness.SnapshotAsync(ProviderIdentity.ProviderDoc, root.Id, Context))!["sessionId"]);
            var first = seen[0].Options!;
            Equal(1234.0, first.TimeoutMs, "first timeout");
            Equal("1", first.Headers?["x-test"], "first header");
            Equal("high", first.Reasoning, "first reasoning");
            Equal(sessionId, first.SessionId, "first session");
            Check(seen[0].Signal.CanBeCanceled, "the request carries the invocation signal");
            var second = seen[1].Options!;
            Equal(null, second.Reasoning, "second reasoning");
            Equal(99.0, second.TimeoutMs, "second timeout");
            Equal(sessionId, second.SessionId, "second session");
            Check(second.Headers is null, "second headers");
            await harness.CloseAsync(Context);
        });

        // Regression coverage for #10424.
        yield return Case(prefix + "keeps-provider-session-ids-request-local-across-concurrent-conversations", async () =>
        {
            var setup = Setup();
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, SyncList<string>>();
            FauxStep Capture() => FauxStep.From(request =>
            {
                var message = request.Messages.OfType<JsonObjectValue>().LastOrDefault(candidate => S(candidate["role"]) == "user");
                var text = message?["content"] is JsonStringValue content ? content.Value : "";
                seen.GetOrAdd(text, _ => new SyncList<string>()).Add(request.Options?.SessionId ?? "");
                return FauxAssistantMessage($"answer:{text}");
            });
            setup.Faux.SetResponses(Capture(), Capture(), Capture(), Capture());
            var (harness, root) = await OpenChat(fresh(), setup);
            var child = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
            {
                Agent = new AgentChange { Model = new ModelRef("faux", "faux-1") },
            }, Context);
            harness.Resume();
            IConversation[] both = [root, child];
            await Task.WhenAll(both.Select(async (conversation, index) => await (await Submit(conversation, $"first-{index}")).WaitAsync(Context)));
            await Task.WhenAll(both.Select(async (conversation, index) => await (await Submit(conversation, $"second-{index}")).WaitAsync(Context)));
            var rootId = S((await harness.SnapshotAsync(ProviderIdentity.ProviderDoc, root.Id, Context))!["sessionId"]);
            var childId = S((await harness.SnapshotAsync(ProviderIdentity.ProviderDoc, child.Id, Context))!["sessionId"]);
            Check(rootId != childId, "distinct session IDs");
            Seq([rootId], seen["first-0"].Snapshot(), "first-0");
            Seq([rootId], seen["second-0"].Snapshot(), "second-0");
            Seq([childId], seen["first-1"].Snapshot(), "first-1");
            Seq([childId], seen["second-1"].Snapshot(), "second-1");
            await harness.CloseAsync(Context);
        });

        // Regression coverage for #10424.
        yield return Case(prefix + "creates-and-persists-provider-state-before-a-legacy-conversations-request", async () =>
        {
            var setup = Setup();
            string? sent = null;
            setup.Faux.SetResponses(FauxStep.From(request =>
            {
                sent = request.Options?.SessionId;
                return FauxAssistantMessage("ok");
            }));
            var (harness, root) = await OpenChat(fresh(), setup);
            await root.CommitAsync(tx => tx.RetireDocAsync(ProviderIdentity.ProviderDoc, root.Id), Context);
            Check(await harness.SnapshotAsync(ProviderIdentity.ProviderDoc, root.Id, Context) is null, "retired");
            harness.Resume();
            await (await Submit(root, "legacy")).WaitAsync(Context);
            var stored = await harness.SnapshotAsync(ProviderIdentity.ProviderDoc, root.Id, Context);
            var sessionId = S(stored?["sessionId"]);
            Check(Regex.IsMatch(sessionId, "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"), $"a UUIDv7: {sessionId}");
            Equal(sessionId, sent, "sent session ID");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reads-settings-through-getters-at-every-decision", async () =>
        {
            var setup = Setup();
            var timeoutMs = 111.0;
            var seen = new SyncList<double?>();
            setup.Faux.SetResponses(
                FauxStep.From(request =>
                {
                    seen.Add(request.Options?.TimeoutMs);
                    // The user changes the setting while the first attempt runs.
                    timeoutMs = 222;
                    return Error503;
                }),
                FauxStep.From(request =>
                {
                    seen.Add(request.Options?.TimeoutMs);
                    return FauxAssistantMessage("ok");
                }));
            var settings = new GetterSettings(() => new ConversationStreamOptions { TimeoutMs = timeoutMs }, new RetrySettings { BaseDelayMs = 1 });
            var harness = await Harness.OpenAsync(fresh(), new HarnessOptions { Models = setup.Models, Registry = setup.Registry, Settings = settings }, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
            harness.Resume();
            Equal(SubmissionStatus.Done, (await (await Submit(root, "hi")).WaitAsync(Context)).Status, "status");
            // The retry prepares again, so it resolves the settings again and sends the new timeout.
            Seq([111.0, 222.0], seen.Snapshot(), "timeouts");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "commits-partials-no-more-often-than-progress-partialintervalms", async () =>
        {
            async Task<bool> PublishedPartial(ProgressSettings? progress)
            {
                var setupBase = Setup();
                var models = new ModelsOverride(setupBase.Models)
                {
                    // Longer than the default 100 ms, shorter than the configured interval.
                    StreamSimpleOverride = (_, _, _, token) => ScriptedStream("partial", 300, Native(FauxAssistantMessage("final")), token),
                };
                var setup = setupBase.WithFreshSettings().WithModels(models);
                setup.Progress = progress;
                var (harness, root) = await OpenChat(fresh(), setup);
                var values = LivePublications(harness);
                harness.Resume();
                var submission = await Submit(root, "hi");
                MatchJson("""{"status":"done"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
                await harness.CloseAsync(Context);
                return values.Snapshot().Any(value => TextOf(Generation(value, "message")) == "partial");
            }
            Check(await PublishedPartial(null), "a partial under the default interval");
            Check(!await PublishedPartial(new ProgressSettings { PartialIntervalMs = 5000 }), "no partial under a 5 s interval");
        });

        yield return Case(prefix + "renders-sections-that-read-conversation-documents-through-input-read", async () =>
        {
            var agentDoc = Documents.DefineConversationDoc("test.agent", 1, DocumentFork.Current, () => O("""{"cwd":"/","kind":"main"}"""));
            var setup = Setup();
            AddSection(setup.Registry, "cwd", async (input, ctx) =>
                (await input.Read.SnapshotAsync(agentDoc, input.ConversationId, ctx)) is { } state ? S(state["cwd"]) : null);
            AddSection(setup.Registry, "agents", async (input, ctx) =>
                (await input.Read.SnapshotAsync(agentDoc, input.ConversationId, ctx)) is { } state && S(state["kind"]) == "sub" ? null : "Read AGENTS.md");
            setup.Faux.SetResponses(FauxAssistantMessage("a"), FauxAssistantMessage("b"));
            var (harness, root) = await OpenChat(fresh(), setup);
            await root.CommitAsync(async tx => (await tx.DocAsync(agentDoc, root.Id)).Set("cwd", "/repo"), Context);
            var sub = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
            {
                Agent = new AgentChange { Model = new ModelRef("faux", "faux-1") },
                Init = async (tx, id) =>
                {
                    var agent = await tx.DocAsync(agentDoc, id);
                    agent.Set("kind", "sub");
                    agent.Set("cwd", "/sub");
                },
            }, Context);
            harness.Resume();
            await (await Submit(root, "one")).WaitAsync(Context);
            await (await Submit(sub, "two")).WaitAsync(Context);
            async Task<JsonValue?> Sections(IConversation conversation) =>
                (await AllEntries(conversation)).FirstOrDefault(entry => entry.Kind == "pi.system")?.Model?[0];
            MatchJson("""{"sections":{"cwd":"<cwd>\n/repo\n</cwd>","agents":"<agents>\nRead AGENTS.md\n</agents>"}}""", await Sections(root), "root sections");
            MatchExactly("""{"role":"system","content":"","sections":{"cwd":"<cwd>\n/sub\n</cwd>"},"timestamp":"<number>"}""", await Sections(sub), "sub sections");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "commits-no-partial-for-a-response-that-turns-deferred-after-an-empty-start-event", async () =>
        {
            var setupBase = Setup();
            var handle = O("""{"provider":"faux","modelId":"faux-1","api":"faux","id":"handle-1","pollAfterMs":60000}""");
            var models = new ModelsOverride(setupBase.Models)
            {
                // Longer than the partial throttle: an empty partial would be committed here.
                StreamSimpleOverride = (_, _, _, token) =>
                    ScriptedStream(null, 300, Native(FauxAssistantMessage([], stopReason: "deferred", deferred: handle)), token),
            };
            var setup = setupBase.WithModels(models);
            var (harness, root) = await OpenChat(fresh(), setup);
            var values = LivePublications(harness);
            var submission = await Submit(root, "hi");
            await Eventually(async () => await LiveOf(harness, root) is { } live && Generation(live, "deferred") is not null);
            Check(!values.Snapshot().Any(value => Generation(value, "message") is not null), "no partial was published");
            var run = (JsonObjectValue)(await LiveOf(harness, root))!["run"];
            await harness.AbortTaskAsync(new TaskId((long)N(run["taskId"])), Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
            Seq(["pi.user"], await Kinds(root), "kinds");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "faults-a-run-task-settling-its-inputs-and-converting-the-committed-partial", async () =>
        {
            var setupBase = Setup();
            var setup = setupBase.WithModels(new ModelsOverride(setupBase.Models) { StreamSimpleOverride = (_, _, _, token) => InvalidFinalStream(token) });
            var (harness, root) = await OpenChat(fresh(), setup);
            var values = LivePublications(harness);
            harness.Resume();
            var submission = await Submit(root, "hi");
            var settled = (await submission.WaitAsync(Context)).ToJson();
            MatchJson("""{"status":"unanswered","reason":"faulted"}""", settled, "settled");
            Rejects(new InvalidOperationException(S(settled["detail"])), "non-finite number", "fault detail");
            Check(values.Snapshot().Any(value => TextOf(Generation(value, "message")) == "partial"), "the partial was published");
            var entries = await AllEntries(root);
            Seq(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            MatchJson("""{"role":"assistant","stopReason":"aborted"}""", entries[1].Model![0], "converted");
            Equal("partial", TextOf(entries[1].Model![0]), "converted text");
            SameJson("{}", await LiveOf(harness, root), "live");
            var tasks = await TasksOf(harness, root);
            MatchJson("""{"status":"terminal","outcome":{"status":"faulted"}}""", tasks[0].State.ToJson(), "state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "orphans-a-blocked-run-task-with-full-run-cleanup", async () =>
        {
            var setup = Setup();
            var (harness, root) = await OpenChat(fresh(), setup);
            // A run whose task was stored by a newer generation definition this process cannot run.
            var definition = HarnessTasks.GenerationTask.Definition;
            var newer = Tasks.DefineTask(new TaskDefinition
            {
                Name = definition.Name, Version = 2, Initial = definition.Initial, Phases = definition.Phases, Abort = definition.Abort,
            });
            var (taskId, submissionId) = await ((DurableHarness)harness).CommitWithAsync(async tx =>
            {
                var entry = await tx.AppendEntryAsync(Entries.UserEntry, root.Id,
                    new TypedEntryDraft { Model = JsonValue.Array(Obj(("role", "user"), ("content", "hi"), ("timestamp", 1))) });
                var submission = await tx.CreateSubmissionAsync(new SubmissionCreate
                {
                    ConversationId = root.Id, Type = SubmissionType.Input, Status = SubmissionStatus.Placed, Entry = entry.Id,
                });
                var id = await tx.CreateTaskAsync(newer, JsonObjectValue.Empty, new TaskOptions(TaskOwnership.Conversation) { ConversationId = root.Id });
                (await tx.DocAsync(Live.LiveDoc, root.Id)).Set("run", Obj(("taskId", id.ToJson()), ("inputs", JsonValue.Array(submission.Id.ToJson()))));
                return (id, submission.Id);
            }, Context);
            harness.Resume();
            await RejectsAsync(() => Submit(root, "busy", WhenBusy.Reject), "is busy", "busy input");
            Equal(TaskAbortResult.Marked, await harness.AbortTaskAsync(taskId, Context), "abort");
            SameJson("""{"status":"orphaned","reason":"task_too_old"}""", OutcomeJson(await harness.WaitForTaskAsync(taskId, Context)), "outcome");
            MatchJson("""{"status":"unanswered","reason":"task_too_old"}""",
                (await (await harness.SubmissionAsync(submissionId, Context))!.StatusAsync(Context)).ToJson(), "submission");
            SameJson("{}", await LiveOf(harness, root), "live");
            await harness.CloseAsync(Context);
        });
    }

    internal static IEnumerable<(string, Func<Task>)> StorageFreeCases(string prefix)
    {
        yield return Case(prefix + "resolves-settings-over-the-built-in-defaults", () =>
        {
            SameJson("""
                {"stream":{},"retry":{"enabled":true,"maxRetries":3,"baseDelayMs":2000,"maxAgentDelayMs":60000},
                 "compaction":{"enabled":true,"reserveTokens":16384,"keepRecentTokens":20000,"backgroundTokens":32768},
                 "progress":{"partialIntervalMs":100,"outputIntervalMs":100},"toolExecution":"parallel","steeringMode":"one-at-a-time",
                 "followUpMode":"one-at-a-time","contextRetentionMs":600000}
                """, SettingsJson(Agents.ResolveSettings(null)), "defaults");
            MatchJson("""{"retry":{"enabled":false,"maxRetries":3,"baseDelayMs":2000},"compaction":{"enabled":true,"backgroundTokens":0}}""",
                SettingsJson(Agents.ResolveSettings(new HarnessSettings
                {
                    Retry = new RetrySettings { Enabled = false },
                    Compaction = new CompactionSettings { BackgroundTokens = 0 },
                })), "overrides");
            SameJson("""{"partialIntervalMs":100,"outputIntervalMs":500}""",
                SettingsJson(Agents.ResolveSettings(new HarnessSettings { Progress = new ProgressSettings { OutputIntervalMs = 500 } }))["progress"], "progress");
        });

        yield return Case(prefix + "rejects-a-registry-without-the-built-in-tasks", async () =>
        {
            var empty = PiSharp.Durable.Registry.Create().Snapshot();
            var reader = new SnapshotReader(new WithoutGeneration(empty));
            await RejectsAsync(() => Harness.OpenAsync(new MemoryStorage(), new HarnessOptions { Models = Setup().Models, Registry = reader }, Context),
                "Registry lacks built-in tasks pi.generation", "open");
        });
    }

    /// <summary>Resolved settings as upstream's <c>Settings</c> JSON.</summary>
    private static JsonObjectValue SettingsJson(Settings settings)
    {
        var properties = new List<(string, JsonValue)>
        {
            ("stream", GenerationPhases.StreamOptionsJson(settings.Stream)),
            ("retry", settings.Retry.MaxAgentDelayMs is { } max
                ? Obj(("enabled", settings.Retry.Enabled), ("maxRetries", settings.Retry.MaxRetries), ("baseDelayMs", settings.Retry.BaseDelayMs), ("maxAgentDelayMs", max))
                : Obj(("enabled", settings.Retry.Enabled), ("maxRetries", settings.Retry.MaxRetries), ("baseDelayMs", settings.Retry.BaseDelayMs))),
            ("compaction", Obj(("enabled", settings.Compaction.Enabled), ("reserveTokens", settings.Compaction.ReserveTokens),
                ("keepRecentTokens", settings.Compaction.KeepRecentTokens), ("backgroundTokens", settings.Compaction.BackgroundTokens))),
            ("progress", Obj(("partialIntervalMs", settings.Progress.PartialIntervalMs), ("outputIntervalMs", settings.Progress.OutputIntervalMs))),
            ("toolExecution", settings.ToolExecution == ToolExecutionMode.Parallel ? "parallel" : "sequential"),
            ("steeringMode", settings.SteeringMode == QueueMode.All ? "all" : "one-at-a-time"),
            ("followUpMode", settings.FollowUpMode == QueueMode.All ? "all" : "one-at-a-time"),
            ("contextRetentionMs", settings.ContextRetentionMs),
        };
        return Obj([.. properties]);
    }

    /// <summary>Settings whose stream options come from a getter (upstream's `get stream()`).</summary>
    private sealed class GetterSettings(Func<ConversationStreamOptions> stream, RetrySettings retry) : HarnessSettings
    {
        public override ConversationStreamOptions? Stream { get => stream(); init { } }
        public override RetrySettings? Retry { get => retry; init { } }
    }

    private sealed class SnapshotReader(IRegistrySnapshot snapshot) : IRegistryReader
    {
        public IRegistrySnapshot Snapshot() => snapshot;
        public IDisposable Subscribe(Action listener) => new Disposer(() => { });
    }

    private sealed class WithoutGeneration(IRegistrySnapshot empty) : IRegistrySnapshot
    {
        public IReadOnlyList<Extension> Installed() => [];
        public Extension? GetExtension(string name) => null;
        public IReadOnlyList<RegisteredTool> Tools() => [];
        public IReadOnlyList<RegisteredSection> Sections() => [];
        public IReadOnlyList<DurableTask> Tasks() => [.. empty.Tasks().Where(task => task.Definition.Name != "pi.generation")];
        public DurableTask? GetTask(string name) => name == "pi.generation" ? null : empty.GetTask(name);
    }
}
