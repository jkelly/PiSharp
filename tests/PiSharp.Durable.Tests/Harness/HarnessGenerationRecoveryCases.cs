// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-generation-recovery.test.ts. Verbatim apart from the
// adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - Backends. Upstream reopens a SQLite file; harness.generation-recovery.* does the same, and harness.generation-recovery.jsonl.* reruns
//   every case reopening a JSONL directory.
// - `aborted(ctx.abortSignal!)` in a section is Aborted(ctx.CancellationToken); `aborted(options!.signal!)` in a faux response is
//   Aborted(request.Signal).
// - Pi v1.5.0 sync (area model-requests): "polls a checkpoint stored without a thinking level and records none" (d5b3de22d), which is
//   harness.generation-recovery.polls-a-checkpoint-stored-without-a-thinking-level-and-records-none. Upstream removes the field with
//   SQLite's json_remove; here the task record is read from the closed storage and written back without it, so the case runs on both
//   backends.
// - Pi v1.5.0 sync (area sleep): f75b3240a and ec1be23eb's changes. A retry backoff and a deferred poll sleep, so the reopened records
//   are sleeping and Harness.Wake() names their time; "sleeps again for a retry stored as a running phase by an earlier version" rewrites
//   the record as the thinking-level case does, where upstream's SQL turns the stored status back to running; no_model's poll runs once
//   the clock reaches its time. The `eventually(() => !harness.wake().active)` waits are 440a5fcdb's (area reports) and hold at once
//   until it lands. The citation stays on v1.2.0 until the file's other areas land.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Testing;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessGenerationRecoveryCases() =>
    [
        .. GenerationRecoveryTests.Cases("harness.generation-recovery.", () =>
        {
            var path = Path.Combine(Temp("generation-recovery-sqlite"), "session.sqlite");
            return () => SqliteStorage.OpenAsync(path).GetAwaiter().GetResult();
        }),
        .. GenerationRecoveryTests.Cases("harness.generation-recovery.jsonl.", () =>
        {
            var directory = Temp("generation-recovery-jsonl");
            return () => JsonlStorage.OpenAsync(directory, DurableContext.Background).GetAwaiter().GetResult();
        }),
    ];
}

/// <summary>The harness.generation-recovery.* cases (upstream harness-generation-recovery.test.ts).</summary>
internal static class GenerationRecoveryTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static async Task<TaskId> RunTaskId(IHarness harness)
    {
        var live = await harness.SnapshotAsync(Live.LiveDoc, ConversationId.Root, Context);
        return new TaskId((long)N(((JsonObjectValue)live!["run"])["taskId"]));
    }

    private static async Task<JsonValue?> Checkpoint(IHarness harness, TaskId id)
    {
        var record = await harness.GetTaskAsync(id, Context);
        return record?.State is LiveTaskState live ? live.Checkpoint : null;
    }

    private static Task<JsonObjectValue?> LiveOf(OpenedChat opened) => opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context);

    private static JsonValue? Generation(JsonObjectValue? live, string key) =>
        live is not null && live.TryGetValue("generation", out var generation) && ((JsonObjectValue)generation).TryGetValue(key, out var field) ? field : null;

    private static Task<ISubmission> Submit(IConversation conversation, string text) => conversation.SubmitAsync(SubmissionDraft.Input(text), Context);

    private static async Task<SubmissionRecord> WaitFor(IHarness harness, SubmissionId id) =>
        await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context);

    private static ValueTask<string?> Text(string text) => ValueTask.FromResult<string?>(text);

    /// <param name="location">A fresh reopenable location: each call of the returned function opens its storage.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Func<IStorage>> location)
    {
        yield return Case(prefix + "reruns-preparation-interrupted-before-its-commit", async () =>
        {
            var open = location();
            var setup = Setup();
            var reached = new HarnessTests.Deferred();
            var block = true;
            AddSection(setup.Registry, "preamble", async (_, ctx) =>
            {
                if (block)
                {
                    block = false;
                    reached.Resolve();
                    await Aborted(ctx.CancellationToken);
                }
                return "p";
            });
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var opened = await OpenChat(open(), setup);
            opened.Harness.Resume();
            var id = (await Submit(opened.Root, "hi")).Id;
            await reached.Task;
            var taskId = await RunTaskId(opened.Harness);
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            SameJson("""{"phase":"prepare","attempt":1}""", await Checkpoint(opened.Harness, taskId), "checkpoint");
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Seq(["pi.user", "pi.system", "pi.assistant"], await Kinds(opened.Root), "kinds");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resends-a-request-interrupted-before-any-partial-without-repeating-preparation", async () =>
        {
            var open = location();
            var setup = Setup();
            AddSection(setup.Registry, "preamble", (_, _) => Text("p"), tag: false);
            var reached = new HarnessTests.Deferred();
            var sent = new SyncList<string[]>();
            var timeouts = new SyncList<double?>();
            setup.Faux.SetResponses(
                new FauxResponseFactory(async request =>
                {
                    reached.Resolve();
                    await Aborted(request.Signal);
                    throw new InvalidOperationException("unreachable");
                }),
                FauxStep.From(request =>
                {
                    sent.Add([.. request.Messages.Select(message => S(((JsonObjectValue)message)["role"]))]);
                    timeouts.Add(request.Options?.TimeoutMs);
                    return FauxAssistantMessage("answer");
                }));
            var opened = await OpenChat(open(), setup);
            setup.Stream = new ConversationStreamOptions { TimeoutMs = 1234 };
            opened.Harness.Resume();
            var id = (await Submit(opened.Root, "hi")).Id;
            await reached.Task;
            var taskId = await RunTaskId(opened.Harness);
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            MatchJson("""{"phase":"request","attempt":1,"thinkingLevel":"off","streamOptions":{"timeoutMs":1234}}""",
                await Checkpoint(opened.Harness, taskId), "checkpoint");
            // The resend uses the pinned request, not options changed after preparation.
            setup.Stream = new ConversationStreamOptions { TimeoutMs = 999 };
            MatchJson("""{"generation":{"attempt":1}}""", await LiveOf(opened), "live");
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Equal(1, sent.Count, "requests");
            Seq(["system", "user"], sent[0], "sent roles");
            Seq([1234.0], timeouts.Snapshot().Select(timeout => timeout ?? double.NaN), "timeouts");
            Seq(["pi.user", "pi.system", "pi.assistant"], await Kinds(opened.Root), "kinds");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "converts-a-committed-partial-into-an-aborted-entry-and-resends-the-same-messages", async () =>
        {
            var open = location();
            var slow = Setup(new FauxOptions { TokensPerSecond = 20, MinTokenSize = 1, MaxTokenSize = 1 });
            slow.Faux.SetResponses(FauxAssistantMessage(new string('z', 400)));
            var opened = await OpenChat(open(), slow);
            opened.Harness.Resume();
            var watch = (await opened.Harness.WatchDocAsync(Live.LiveDoc, opened.Root.Id, Context))!;
            string? watched = null;
            watch.Start((call, _) =>
            {
                watched = TextOf(Generation(Change(call).Value, "message")) ?? watched;
                return Task.CompletedTask;
            });
            var id = (await Submit(opened.Root, "hi")).Id;
            await Eventually(() => Volatile.Read(ref watched) is not null);
            await opened.Harness.CloseAsync(Context);

            var setup = Setup();
            var sent = new SyncList<string[]>();
            setup.Faux.SetResponses(FauxStep.From(request =>
            {
                sent.Add([.. request.Messages.Select(message => S(((JsonObjectValue)message)["role"]))]);
                return FauxAssistantMessage("answer");
            }));
            opened = await OpenChat(open(), setup);
            var stored = await LiveOf(opened);
            var partial = TextOf(Generation(stored, "message"))!;
            // Everything observers saw before the crash is durable.
            Check(partial.StartsWith(watched!, StringComparison.Ordinal), $"the stored partial extends the watched one: {partial} / {watched}");
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Equal(1, sent.Count, "requests");
            Seq(["user"], sent[0], "sent roles");
            var entries = await AllEntries(opened.Root);
            Seq(["pi.user", "pi.assistant", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            var converted = (JsonObjectValue)entries[1].Model![0];
            Equal("aborted", S(converted["stopReason"]), "stop reason");
            Equal(partial, TextOf(converted), "converted text");
            SameJson("{}", await LiveOf(opened), "live");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-a-retry-backoff-after-reopen", async () =>
        {
            var open = location();
            var setup = Setup();
            double now = 1_000;
            setup.Now = () => Volatile.Read(ref now);
            setup.Faux.SetResponses(FauxAssistantMessage([], stopReason: "error", errorMessage: "503 Service Unavailable"), FauxAssistantMessage("recovered"));
            var opened = await OpenChat(open(), setup);
            opened.Harness.Resume();
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 60_000 };
            var id = (await Submit(opened.Root, "hi")).Id;
            await Eventually(async () => Generation(await LiveOf(opened), "retry") is not null);
            var taskId = await RunTaskId(opened.Harness);
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            SameJson("""{"status":"sleeping","checkpoint":{"phase":"retry","attempt":1,"until":61000},"until":61000}""",
                (await opened.Harness.GetTaskAsync(taskId, Context))?.State.ToJson(), "state");
            await Eventually(() => !opened.Harness.Wake().Active);
            Equal(new Wake(false, 61_000), opened.Harness.Wake(), "wake");
            // An early wake retries nothing.
            opened.Harness.Resume();
            await Task.Delay(20);
            Equal(1, setup.Faux.State.CallCount, "calls before its time");
            SameJson("""{"run":{"taskId":""" + taskId + ""","inputs":[""" + id + """]},"generation":{"attempt":1,"retry":{"at":61000,"error":"503 Service Unavailable"}}}""",
                await LiveOf(opened), "live");
            Volatile.Write(ref now, 61_000);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Seq(["pi.user", "pi.assistant", "pi.assistant"], await Kinds(opened.Root), "kinds");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "sleeps-again-for-a-retry-stored-as-a-running-phase-by-an-earlier-version", async () =>
        {
            var open = location();
            var setup = Setup();
            double now = 1_000;
            setup.Now = () => Volatile.Read(ref now);
            setup.Faux.SetResponses(FauxAssistantMessage([], stopReason: "error", errorMessage: "503 Service Unavailable"), FauxAssistantMessage("recovered"));
            var opened = await OpenChat(open(), setup);
            opened.Harness.Resume();
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 60_000 };
            var id = (await Submit(opened.Root, "hi")).Id;
            await Eventually(async () => (await opened.Harness.GetTaskAsync(await RunTaskId(opened.Harness), Context))?.State.Status == DurableTaskStatus.Sleeping);
            var taskId = await RunTaskId(opened.Harness);
            await opened.Harness.CloseAsync(Context);
            // Earlier versions slept inside a running `retry` phase.
            var storage = open();
            var record = (await storage.GetTaskAsync(taskId, Context))!;
            await storage.CommitWritesAsync([new TaskWrite(record with { State = new RunningTaskState(((SleepingTaskState)record.State).Checkpoint) })], Context);
            await storage.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            opened.Harness.Resume();
            await Eventually(async () => (await opened.Harness.GetTaskAsync(taskId, Context))?.State.Status == DurableTaskStatus.Sleeping);
            MatchJson("""{"status":"sleeping","until":61000}""", (await opened.Harness.GetTaskAsync(taskId, Context))?.State.ToJson(), "state");
            Equal(1, setup.Faux.State.CallCount, "calls before its time");
            Volatile.Write(ref now, 61_000);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, id)).Status, "status");
            Equal(2, setup.Faux.State.CallCount, "calls");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-polling-a-deferred-response-after-reopen", async () =>
        {
            var open = location();
            var setup = Setup(new FauxOptions { PollAfterMs = 60_000 });
            double now = 1_000;
            setup.Now = () => Volatile.Read(ref now);
            setup.Faux.SetResponses(FauxAssistantMessage("deferred answer"));
            var opened = await OpenChat(open(), setup);
            opened.Harness.Resume();
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            var id = (await Submit(opened.Root, "hi")).Id;
            await Eventually(async () => Generation(await LiveOf(opened), "deferred") is not null);
            var taskId = await RunTaskId(opened.Harness);
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            MatchJson("""{"phase":"poll","attempt":1,"pollAt":61000}""", await Checkpoint(opened.Harness, taskId), "checkpoint");
            MatchJson("""{"status":"sleeping","until":61000}""", (await opened.Harness.GetTaskAsync(taskId, Context))?.State.ToJson(), "state");
            await Eventually(() => !opened.Harness.Wake().Active);
            Equal(new Wake(false, 61_000), opened.Harness.Wake(), "wake");
            Volatile.Write(ref now, 61_000);
            opened.Harness.Resume();
            var settled = await WaitFor(opened.Harness, id);
            if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input) throw new InvalidOperationException($"Unexpected {settled.Status}");
            var answer = await opened.Root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context);
            Equal("deferred answer", TextOf(answer?.Model?[0]), "answer");
            Equal(1, setup.Faux.State.DeferredFetchCount, "fetches");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "polls-a-checkpoint-stored-without-a-thinking-level-and-records-none", async () =>
        {
            var open = location();
            var setup = Setup(new FauxOptions { PollAfterMs = 60_000 });
            double now = 1_000;
            setup.Now = () => Volatile.Read(ref now);
            setup.Faux.SetResponses(FauxAssistantMessage("deferred answer"));
            var opened = await OpenChat(open(), setup);
            await opened.Root.ConfigureAsync(new AgentChange { ThinkingLevel = "high" }, Context);
            opened.Harness.Resume();
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            var id = (await Submit(opened.Root, "hi")).Id;
            await Eventually(async () => Generation(await LiveOf(opened), "deferred") is not null);
            var taskId = await RunTaskId(opened.Harness);
            MatchJson("""{"phase":"poll","thinkingLevel":"high"}""", await Checkpoint(opened.Harness, taskId), "checkpoint");
            await opened.Harness.CloseAsync(Context);
            // Earlier versions stored poll checkpoints without the field.
            var storage = open();
            var record = (await storage.GetTaskAsync(taskId, Context))!;
            var stored = ((JsonObjectValue)((LiveTaskState)record.State).Checkpoint).Remove("thinkingLevel");
            TaskState state = record.State switch
            {
                PendingTaskState => new PendingTaskState(stored),
                RunningTaskState => new RunningTaskState(stored),
                SleepingTaskState sleeping => new SleepingTaskState(stored, sleeping.Until),
                var other => throw new InvalidOperationException($"unexpected state {other.Status}"),
            };
            await storage.CommitWritesAsync([new TaskWrite(record with { State = state })], Context);
            await storage.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            Check(await Checkpoint(opened.Harness, taskId) is JsonObjectValue reopened && !reopened.ContainsKey("thinkingLevel"), "no stored thinking level");
            Volatile.Write(ref now, 61_000);
            opened.Harness.Resume();
            var settled = await WaitFor(opened.Harness, id);
            if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input) throw new InvalidOperationException($"Unexpected {settled.Status}");
            var answer = (await opened.Root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context))?.Model?[0];
            Equal("deferred answer", TextOf(answer), "answer");
            Check(answer is JsonObjectValue message && !message.ContainsKey("thinkingLevel"), "no recorded thinking level");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-no-model-when-the-pinned-model-is-gone-after-reopen-in-request-and-in-poll", async () =>
        {
            var open = location();
            var setup = Setup(new FauxOptions { PollAfterMs = 60_000 });
            double now = 1_000;
            setup.Now = () => Volatile.Read(ref now);
            var busy = Unanswered();
            setup.Faux.SetResponses(busy.Step, FauxAssistantMessage("deferred"));
            var opened = await OpenChat(open(), setup);
            opened.Harness.Resume();
            var requesting = (await Submit(opened.Root, "one")).Id;
            await busy.Reached;
            await opened.Harness.CloseAsync(Context);

            // Reopened without the faux provider: the request's pinned model is unknown.
            var empty = setup.WithModels(EmptyDurableModels.Instance);
            opened = await OpenChat(open(), empty);
            opened.Harness.Resume();
            MatchJson("""{"status":"unanswered","reason":"no_model"}""", (await WaitFor(opened.Harness, requesting)).ToJson(), "requesting");
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            opened.Harness.Resume();
            var polling = (await Submit(opened.Root, "two")).Id;
            await Eventually(async () => Generation(await LiveOf(opened), "deferred") is not null);
            await opened.Harness.CloseAsync(Context);

            // The poll sleeps until it is due; then the model is resolved.
            Volatile.Write(ref now, 61_000);
            opened = await OpenChat(open(), empty);
            opened.Harness.Resume();
            MatchJson("""{"status":"unanswered","reason":"no_model"}""", (await WaitFor(opened.Harness, polling)).ToJson(), "polling");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-a-print-style-turn-and-reads-the-durable-answer-after-reopen", async () =>
        {
            var open = location();
            var setup = Setup();
            AddSection(setup.Registry, "preamble", (_, _) => Text("You are terse."), tag: false);
            setup.Faux.SetResponses(FauxAssistantMessage("42"));
            var opened = await OpenChat(open(), setup);
            opened.Harness.Resume();
            var submission = await Submit(opened.Root, "answer?");
            var settled = await submission.WaitAsync(Context);
            if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input) throw new InvalidOperationException($"Unexpected {settled.Status}");
            var answer = await opened.Root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context);
            Equal("42", TextOf(answer?.Model?[0]), "answer");
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(open(), setup);
            SameJson(settled.ToJson().ToJson(), (await (await opened.Harness.SubmissionAsync(submission.Id, Context))!.StatusAsync(Context)).ToJson(), "status");
            SameJson(answer!.ToJson().ToJson(), (await opened.Root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context))?.ToJson(),
                "entry");
            await opened.Harness.CloseAsync(Context);
        });
    }
}
