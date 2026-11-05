// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-restart-failure.test.ts (b3a29f111), ported in the Pi
// v1.5.0 sync (area restarts). What a crash (a host that dies, is evicted, or is reset) does to work that was running: a generation's
// interrupted request is a failed attempt under the retry policy, and every other task ends visibly once it was cut off
// settings.restartLimit times in a row (#11139). Verbatim apart from the adaptations in HarnessSupport.cs, ChatSupport.cs and
// SessionSupport.cs, and these:
// - openNodeSqliteStorage is SqliteStorage.OpenAsync over a file in a fresh temporary directory. The store pass (StoreStorageNode.cs) reruns
//   the cases as harness.restart-failure.store.*, opening every file as a StoreStorage, as upstream's vitest.store.config.ts does.
// - open's kill() is KillableStorage: once killed, every call of the storage hangs, so the Harness writes nothing more, not even the pending
//   records a close writes for the tasks it stops. The killed Harness is never closed; its storage is closed after the case, as upstream's
//   afterEach does.
// - liveValues follows commits as memory adopts them (SessionTests.FollowCommits, upstream followCommits, over the document changes of kind
//   change); waitFor is Eventually; `aborted(signal)` is Aborted(token); commitWrites is
//   PiSharp.Durable.Testing.CommitWrites.CommitWritesAsync.
// - The Proxy over the models is ModelsOverride (FetchDeferredOverride, CancelDeferredOverride); the Proxy over the settings that counts
//   resolutions is a HarnessSettings whose Extensions getter counts, as every resolution reads it once.
// - Later areas' parts of the v1.5.0 blob are left out: pi.live.runFailure's `first` (area run-end, 7e15f9601) is not asserted, as PiSharp's
//   RunFailure has no such field yet, and compactionFailure keeps v1.2.0's `reason` where 5f312dac4's pi.live version 2 (area live-turns)
//   renames it `trigger`.
//   The settings bound (49 resolutions a round, #11327) is upstream's with group commit (e5c6182ba, area group-commit-scheduler), which
//   coalesces some reconcile passes; this port, without group commit yet, measured 36 a round, so upstream's bound holds as it is.
// - PiSharp-only (decision 0011), not upstream's: "keeps the interrupted request's stream metadata for the next attempt" checks that the
//   attempt after an interrupted request carries the metadata the request was prepared with (a host's op and account), while its other
//   stream options and every new request read the settings again (Generation.cs).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;
using PiSharp.Contracts;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessRestartFailureCases() =>
        RestartFailureTests.Cases("harness.restart-failure.", async path => await SqliteStorage.OpenAsync(path));
}

/// <summary>
/// A storage whose every call hangs once killed (upstream's Proxy in harness-restart-failure.test.ts' <c>open</c> and harness-blocked's
/// <c>openWith</c>): a crash, after which the Harness writes nothing more, not even the <c>pending</c> records a close writes for the tasks
/// it stops.
/// </summary>
internal sealed class KillableStorage(IStorage inner) : DelegatingStorage(inner)
{
    private volatile bool dead;

    /// <summary>Every later call hangs.</summary>
    public void Kill() => dead = true;

    private static Task<T> Hang<T>() => new TaskCompletionSource<T>().Task;

    public override Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        dead ? Hang<CommitResult>() : base.CommitAsync(batches, receive, context);
    public override void Publishing(Action publish, DurableContext context)
    {
        if (!dead) base.Publishing(publish, context);
    }
    public override Task<TId> MintIdAsync<TId>() => dead ? Hang<TId>() : base.MintIdAsync<TId>();
    public override Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) =>
        dead ? Hang<ConversationRecord?>() : base.GetConversationAsync(id, context);
    public override Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        dead ? Hang<Page<ConversationRecord>>() : base.ScanConversationsAsync(query, limit, cursor, context);
    public override Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) =>
        dead ? Hang<CommittedEntry?>() : base.GetEntryAsync(id, context);
    public override Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        dead ? Hang<CommittedEntry?>() : base.GetEntryAsync(conversationId, id, context);
    public override Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        dead ? Hang<EntryRecord?>() : base.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public override Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        dead ? Hang<Page<EntryRecord>>() : base.ScanEntriesAsync(query, limit, cursor, context);
    public override Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
        dead ? Hang<TaskRecord?>() : base.GetTaskAsync(id, context);
    public override Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        dead ? Hang<Page<TaskRecord>>() : base.ScanTasksAsync(query, limit, cursor, context);
    public override Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) =>
        dead ? Hang<SubmissionRecord?>() : base.GetSubmissionAsync(id, context);
    public override Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        dead ? Hang<Page<SubmissionRecord>>() : base.ScanSubmissionsAsync(query, limit, cursor, context);
    public override Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        dead ? Hang<SubmissionRecord?>() : base.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public override Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        dead ? Hang<DocumentRecord?>() : base.FindDocumentAsync(address, at, context);
    public override Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        dead ? Hang<StoredDocument?>() : base.GetDocumentAsync(id, at, context);
    public override Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        dead ? Hang<Page<DocumentRecord>>() : base.ScanDocumentsAsync(query, limit, cursor, context);
    public override Task CloseAsync(DurableContext context) => dead ? Hang<bool>() : base.CloseAsync(context);
}

/// <summary>The storages of killed Harnesses, never closed by them: closed after the case (upstream's <c>killed</c> and its
/// <c>afterEach</c>).</summary>
internal sealed class KilledStorages
{
    private readonly ConcurrentBag<IStorage> killed = [];

    /// <summary>Opens <paramref name="inner"/> behind a <see cref="KillableStorage"/>; the returned kill() makes every later call hang.</summary>
    public (KillableStorage Storage, Action Kill) Killable(IStorage inner)
    {
        var storage = new KillableStorage(inner);
        return (storage, () =>
        {
            storage.Kill();
            killed.Add(inner);
        });
    }

    public async Task CloseAsync()
    {
        foreach (var storage in killed)
        {
            try { await storage.CloseAsync(DurableContext.Background); }
            catch (Exception) { /* closed after the case, as upstream's `.catch(() => {})` */ }
        }
    }

    /// <summary>A case whose killed storages are closed after it, whatever its result.</summary>
    public static (string, Func<Task>) Case(string id, Func<KilledStorages, Task> body) => Program.Case(id, async () =>
    {
        var killed = new KilledStorages();
        try { await body(killed); }
        finally { await killed.CloseAsync(); }
    });
}

/// <summary>The harness.restart-failure.* cases (upstream harness-restart-failure.test.ts).</summary>
internal static class RestartFailureTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-restart"), "session.sqlite");

    private sealed record Opened(IHarness Harness, IConversation Root, Action Kill);

    /// <summary>Opens a chat over the file at <paramref name="path"/>; <c>Kill</c> simulates a crash (upstream <c>open</c>).</summary>
    private static async Task<Opened> Open(Func<string, Task<IStorage>> openFile, KilledStorages killed, string path, ChatSetup setup)
    {
        var (storage, kill) = killed.Killable(await openFile(path));
        var opened = await OpenChat(storage, setup);
        return new Opened(opened.Harness, opened.Root, kill);
    }

    private static async Task<JsonObjectValue> Live(IHarness harness) =>
        await harness.SnapshotAsync(PiSharp.Durable.Live.LiveDoc, ConversationId.Root, Context) ?? JsonObjectValue.Empty;

    /// <summary>Every pi.live value from now on, as memory adopts it (upstream <c>liveValues</c>).</summary>
    private static SyncList<JsonObjectValue> LiveValues(IHarness harness)
    {
        var values = new SyncList<JsonObjectValue>();
        FollowCommits(harness, (publication, _) =>
        {
            foreach (var change in DocumentValues(publication))
                if (change.Record.Kind == "pi.live") values.Add(change.Value);
        });
        return values;
    }

    /// <summary>Lets deferred progress commits (persistWithinMs 0) reach Storage before a crash (upstream <c>stored</c>).</summary>
    private static Task Stored() => Task.Delay(50);

    private static JsonObjectValue? Field(JsonObjectValue? value, string key) =>
        value is not null && value.TryGetValue(key, out var field) ? field as JsonObjectValue : null;

    private static JsonObjectValue? Generation(JsonObjectValue live) => Field(live, "generation");

    private static JsonObjectValue? PartialMessage(JsonObjectValue live) => Field(Generation(live), "message");

    private static TaskId RunTaskId(JsonObjectValue live) => new((long)((JsonNumberValue)Field(live, "run")!["taskId"]).Value);

    private static double? Number(JsonObjectValue? value, string key) =>
        value is not null && value.TryGetValue(key, out var field) && field is JsonNumberValue number ? number.Value : null;

    /// <summary>A setup whose model streams a long answer slowly; partials are stored as soon as they are committed (upstream
    /// <c>streaming</c>).</summary>
    private static ChatSetup Streaming()
    {
        var setup = Setup(new FauxOptions { TokensPerSecond = 20, MinTokenSize = 1, MaxTokenSize = 1 });
        setup.Faux.SetResponses(FauxAssistantMessage(new string('z', 400)));
        setup.Progress = new ProgressSettings { PersistWithinMs = 0 };
        return setup;
    }

    /// <summary>Submits an input to a fresh chat at <paramref name="path"/> and crashes once its streamed partial is stored (upstream
    /// <c>crashWhileStreaming</c>).</summary>
    private static async Task<(SubmissionId Id, TaskId TaskId)> CrashWhileStreaming(Func<string, Task<IStorage>> openFile, KilledStorages killed,
        string path, ChatSetup setup)
    {
        var opened = await Open(openFile, killed, path, setup);
        opened.Harness.Resume();
        var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
        await Eventually(async () => PartialMessage(await Live(opened.Harness)) is not null);
        var taskId = RunTaskId(await Live(opened.Harness));
        await Stored();
        opened.Kill();
        return (id, taskId);
    }

    /// <summary>Reopens after a crash in the middle of a request, with another streaming answer, and crashes again (upstream
    /// <c>crashAgain</c>).</summary>
    private static async Task CrashAgain(Func<string, Task<IStorage>> openFile, KilledStorages killed, string path, ChatSetup setup, TaskId taskId)
    {
        var opened = await Open(openFile, killed, path, setup);
        opened.Harness.Resume();
        // The first attempt was charged; the second one streams.
        await Eventually(async () => (await opened.Harness.GetTaskAsync(taskId, Context))?.State is RunningTaskState { Checkpoint: JsonObjectValue checkpoint } &&
            checkpoint["phase"] is JsonStringValue { Value: "request" } && Number(checkpoint, "attempt") == 2);
        await Eventually(async () => PartialMessage(await Live(opened.Harness)) is not null);
        await Stored();
        opened.Kill();
    }

    private static async Task<SubmissionRecord> Settled(IHarness harness, SubmissionId id) =>
        await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context);

    private static RetrySettings Retry(int maxRetries, double baseDelayMs) => new() { Enabled = true, MaxRetries = maxRetries, BaseDelayMs = baseDelayMs };

    private static readonly RetrySettings NoRetry = new() { Enabled = false };

    private static string RunFailure(TaskId taskId, SubmissionId id, string reason, string detail) =>
        Obj(("runFailure", Obj(("taskId", taskId.Value), ("inputs", JsonValue.Array((JsonValue)id.Value)), ("reason", reason), ("detail", detail))))
            .ToJson();

    /// <summary>
    /// A setup whose model answers deferred, and whose first fetch of each deferred response is in flight until the Harness stops; later
    /// fetches answer. <c>Fetching</c> resolves when the first fetch began (upstream <c>deferredSetup</c>).
    /// </summary>
    private static (ChatSetup Setup, HarnessTests.Deferred Fetching, Func<int> Fetches) DeferredSetup(double pollAfterMs = 10)
    {
        var baseSetup = Setup(new FauxOptions { PollAfterMs = pollAfterMs });
        baseSetup.Stream = new ConversationStreamOptions { Deferred = true };
        baseSetup.Faux.SetResponses(FauxAssistantMessage("deferred answer"));
        var fetching = new HarnessTests.Deferred();
        var fetches = 0;
        var models = new ModelsOverride(baseSetup.Faux)
        {
            FetchDeferredOverride = async (model, handle, signal) =>
            {
                if (Interlocked.Increment(ref fetches) > 1) return await baseSetup.Faux.FetchDeferredAsync(model, handle, signal);
                fetching.Resolve();
                await Aborted(signal);
                throw new InvalidOperationException("unreachable");
            },
        };
        return (baseSetup.WithModels(models), fetching, () => Volatile.Read(ref fetches));
    }

    /// <summary>Submits to a fresh chat at <paramref name="path"/>, lets its deferred response be fetched, and crashes during that fetch
    /// (upstream <c>crashWhilePolling</c>).</summary>
    private static async Task<(SubmissionId Id, TaskId TaskId)> CrashWhilePolling(Func<string, Task<IStorage>> openFile, KilledStorages killed,
        string path, ChatSetup setup, Task fetching)
    {
        var opened = await Open(openFile, killed, path, setup);
        opened.Harness.Resume();
        var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
        await fetching;
        var taskId = RunTaskId(await Live(opened.Harness));
        MatchJson("""{"status":"running","checkpoint":{"phase":"poll"}}""", (await opened.Harness.GetTaskAsync(taskId, Context))?.State.ToJson(), "polling");
        opened.Kill();
        return (id, taskId);
    }

    private static string Roles(ImmutableArray<JsonValue> messages) => string.Join(",", messages.Select(message => S(((JsonObjectValue)message)["role"])));

    private static ReportInfo Interrupted(TaskId id, string kind, ReportReason reason, int restarts) => new(ReportKind.TaskInterrupted)
    {
        Task = new ReportTask(id, kind, ConversationId.Root),
        Reason = reason,
        Restarts = restarts,
    };

    /// <summary>Host settings that count their resolutions: every resolution reads <see cref="HarnessSettings.Extensions"/> once.</summary>
    private sealed class CountingSettings : HarnessSettings
    {
        private int resolutions;
        public int Resolutions { get => Volatile.Read(ref resolutions); set => Volatile.Write(ref resolutions, value); }
        public override IReadOnlyList<Extension>? Extensions
        {
            get
            {
                Interlocked.Increment(ref resolutions);
                return null;
            }
            init { }
        }
    }

    /// <param name="prefix">The case ID prefix: harness.restart-failure., or harness.restart-failure.store. for the store pass
    /// (StoreStorageNode.cs).</param>
    /// <param name="openFile">Upstream's openNodeSqliteStorage: opens the storage over the file at a path.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<string, Task<IStorage>> openFile)
    {
        // ─── a generation interrupted while the model answers ───────────────────

        var interrupted = prefix + "a-generation-interrupted-while-the-model-answers.";

        yield return KilledStorages.Case(interrupted + "charges-the-attempt-keeps-the-partial-waits-for-the-retry-backoff-and-answers-on-the-next-attempt", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.Retry = Retry(2, 1);
            var (id, taskId) = await CrashWhileStreaming(openFile, killed, path, first);

            var setup = Setup();
            setup.Retry = Retry(2, 1);
            var sent = new SyncList<string>();
            setup.Faux.SetResponses(FauxStep.From(request =>
            {
                sent.Add(Roles(request.Messages));
                return FauxAssistantMessage("answer");
            }));
            var opened = await Open(openFile, killed, path, setup);
            var values = LiveValues(opened.Harness);
            var partial = TextOf(PartialMessage(await Live(opened.Harness)))!;
            Equal(1, (await opened.Harness.GetTaskAsync(taskId, Context))?.Restarts, "restarts");
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            // The backoff showed in pi.live with why.
            var retry = values.Snapshot().Select(Generation).FirstOrDefault(generation => generation?.ContainsKey("retry") == true);
            MatchExactly("""{"attempt":1,"retry":{"at":"<number>","error":"The host stopped while the model was answering, on attempt 1"}}""", retry, "retry");
            Check(values.Snapshot().Any(value => Number(Generation(value), "attempt") == 2), "attempt 2 shown");
            var entries = await AllEntries(opened.Root);
            Seq(["pi.user", "pi.assistant", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            var converted = (JsonObjectValue)entries[1].Model![0];
            Equal("aborted", S(converted["stopReason"]), "stop reason");
            Equal(partial, TextOf(converted), "partial");
            // The aborted partial is excluded from the next request.
            Seq(["user"], sent.Snapshot(), "sent");
            SameJson("{}", await Live(opened.Harness), "live");
            await opened.Harness.CloseAsync(Context);
        });

        // PiSharp-only (decision 0011; see the file header).
        yield return KilledStorages.Case(interrupted + "keeps-the-interrupted-request-s-stream-metadata-for-the-next-attempt", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.Retry = Retry(2, 1);
            first.Stream = new ConversationStreamOptions { Metadata = O("""{"pisharp":{"account":"acct-1","op":"first"}}"""), TimeoutMs = 1000 };
            var (id, _) = await CrashWhileStreaming(openFile, killed, path, first);

            var setup = Setup();
            setup.Retry = Retry(2, 1);
            setup.Stream = new ConversationStreamOptions { Metadata = O("""{"pisharp":{"account":"acct-2","op":"second"}}"""), TimeoutMs = 2000 };
            var sent = new SyncList<DurableStreamOptions?>();
            setup.Faux.SetResponses(
                FauxStep.From(request =>
                {
                    sent.Add(request.Options);
                    return FauxAssistantMessage("answer");
                }),
                FauxStep.From(request =>
                {
                    sent.Add(request.Options);
                    return FauxAssistantMessage("again");
                }));
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            var next = (await opened.Root.SubmitAsync(SubmissionDraft.Input("again"), Context)).Id;
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, next)).Status, "the next input");
            var options = sent.Snapshot();
            Equal(2, options.Count, "requests");
            // The attempt that recovers the interrupted request: its metadata, the settings' other options.
            SameJson("""{"pisharp":{"account":"acct-1","op":"first"}}""", options[0]?.Metadata, "the recovered attempt's metadata");
            Equal(2000d, options[0]?.TimeoutMs, "the recovered attempt's timeout");
            // A new request: the settings' metadata.
            SameJson("""{"pisharp":{"account":"acct-2","op":"second"}}""", options[1]?.Metadata, "a new request's metadata");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(interrupted + "sleeps-the-backoff-of-settings-retry-before-the-next-attempt", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.Retry = Retry(2, 60_000);
            var (_, taskId) = await CrashWhileStreaming(openFile, killed, path, first);

            var setup = Setup();
            setup.Retry = Retry(2, 60_000);
            const double now = 1_000_000;
            setup.Now = () => now;
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            await Eventually(async () => (await opened.Harness.GetTaskAsync(taskId, Context))?.State.Status == DurableTaskStatus.Sleeping);
            var record = await opened.Harness.GetTaskAsync(taskId, Context);
            MatchJson($$$$"""{"status":"sleeping","until":{{{{now + 60_000}}}},"checkpoint":{"phase":"retry"}}""", record?.State.ToJson(), "state");
            Equal(null, record?.Restarts, "restarts");
            SameJson($$$$"""{"attempt":1,"retry":{"at":{{{{now + 60_000}}}},"error":"The host stopped while the model was answering, on attempt 1"}}""",
                Generation(await Live(opened.Harness)), "generation");
            Equal(new Wake(false, now + 60_000), opened.Harness.Wake(), "wake");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(interrupted + "ends-the-run-interrupted-once-no-retry-is-left-keeping-every-partial", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.Retry = Retry(1, 1);
            var (id, taskId) = await CrashWhileStreaming(openFile, killed, path, first);
            // Attempt 2 is interrupted too.
            var second = Streaming();
            second.Retry = Retry(1, 1);
            await CrashAgain(openFile, killed, path, second, taskId);

            var setup = Setup();
            setup.Retry = Retry(1, 1);
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            const string text = "The host stopped while the model was answering, on attempt 2";
            MatchJson($$$$"""{"status":"unanswered","reason":"interrupted","detail":"{{{{text}}}}"}""", (await Settled(opened.Harness, id)).ToJson(), "settled");
            SameJson(RunFailure(taskId, id, "interrupted", text), await Live(opened.Harness), "live");
            SameJson($$$$"""{"status":"failed","error":{"message":"{{{{text}}}}","detail":{"reason":"interrupted"}}}""",
                OutcomeJson(await opened.Harness.WaitForTaskAsync(taskId, Context)), "outcome");
            var entries = await AllEntries(opened.Root);
            Seq(["pi.user", "pi.assistant", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            Seq(["aborted", "aborted"], entries.Skip(1).Select(entry => S(((JsonObjectValue)entry.Model![0])["stopReason"])), "stop reasons");
            // No request was sent after the second crash.
            Equal(0, setup.Faux.State.CallCount, "calls");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(interrupted + "ends-the-run-at-once-when-retries-are-disabled", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            var (id, _) = await CrashWhileStreaming(openFile, killed, path, first);
            var setup = Setup();
            setup.Retry = NoRetry;
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            MatchJson("""{"status":"unanswered","reason":"interrupted"}""", (await Settled(opened.Harness, id)).ToJson(), "settled");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(interrupted + "resends-after-a-clean-close-a-close-is-not-a-crash", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.Retry = NoRetry;
            var opened = await Open(openFile, killed, path, first);
            opened.Harness.Resume();
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
            await Eventually(async () => PartialMessage(await Live(opened.Harness)) is not null);
            var taskId = RunTaskId(await Live(opened.Harness));
            await opened.Harness.CloseAsync(Context);

            var setup = Setup();
            setup.Retry = NoRetry;
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var reopened = await Open(openFile, killed, path, setup);
            Equal(null, (await reopened.Harness.GetTaskAsync(taskId, Context))?.Restarts, "restarts");
            reopened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await Settled(reopened.Harness, id)).Status, "status");
            await reopened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(interrupted + "does-not-charge-a-generation-that-sleeps-in-a-retry-or-waits-for-its-tools", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.Retry = Retry(1, 60_000);
            var gate = new HarnessTests.Deferred();
            var started = new HarnessTests.Deferred();
            var hold = Define.Tool(new ToolRegistration
            {
                Name = "hold",
                Description = "Waits",
                Replay = ToolReplay.Safe,
                Execute = async (_, _, _) =>
                {
                    started.Resolve();
                    await gate.Task;
                    return new ToolExecutionResult();
                },
            });
            AddTool(setup.Registry, hold);
            var error = FauxAssistantMessage(Array.Empty<JsonObjectValue>(), stopReason: "error", errorMessage: "503 Service Unavailable");
            setup.Faux.SetResponses(error);
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
            var taskId = RunTaskId(await Live(opened.Harness));
            await Eventually(async () => (await opened.Harness.GetTaskAsync(taskId, Context))?.State.Status == DurableTaskStatus.Sleeping);
            opened.Kill();

            // Asleep in its retry: not running, so not cut off.
            var later = Setup();
            later.Retry = Retry(1, 60_000);
            later.Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120_000;
            AddTool(later.Registry, setup.Registry.Snapshot().Tools()[0].Tool);
            later.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            opened = await Open(openFile, killed, path, later);
            Equal(null, (await opened.Harness.GetTaskAsync(taskId, Context))?.Restarts, "restarts while sleeping");
            opened.Harness.Resume();
            // Waiting for its tool round: not running either.
            await started.Task;
            var waiting = await opened.Harness.GetTaskAsync(taskId, Context);
            MatchJson("""{"status":"waiting","checkpoint":{"phase":"tools"}}""", waiting?.State.ToJson(), "waiting");
            opened.Kill();

            opened = await Open(openFile, killed, path, later);
            Equal(null, (await opened.Harness.GetTaskAsync(taskId, Context))?.Restarts, "restarts while waiting");
            gate.Resolve();
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(interrupted + "ends-a-run-whose-generation-is-cut-off-restartlimit-times-outside-a-request", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.RestartLimit = 1;
            var reached = new HarnessTests.Deferred();
            // Preparation never finishes, as one that exhausts the host's memory would not.
            AddSection(setup.Registry, "preamble", async (_, callContext) =>
            {
                reached.Resolve();
                await Aborted(callContext.CancellationToken);
                return null;
            });
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
            await reached.Task;
            var taskId = RunTaskId(await Live(opened.Harness));
            opened.Kill();

            opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            const string text = "The generation was interrupted once in its prepare phase";
            MatchJson($$$$"""{"status":"unanswered","reason":"restart_limit","detail":"{{{{text}}}}"}""", (await Settled(opened.Harness, id)).ToJson(), "settled");
            SameJson(RunFailure(taskId, id, "restart_limit", text), await Live(opened.Harness), "live");
            await Eventually(() => setup.Infos.Count > 0);
            Check(setup.Infos.Snapshot().Any(info => info is { Kind: ReportKind.TaskInterrupted, Reason: ReportReason.RestartLimit, Restarts: 1 }),
                $"task_interrupted reported: {string.Join(", ", setup.Infos.Snapshot())}");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── other tasks cut off restartLimit times in a row ─────────────────────

        var others = prefix + "other-tasks-cut-off-restartlimit-times-in-a-row.";

        yield return KilledStorages.Case(others + "ends-a-tool-call-with-an-error-result-instead-of-running-it-again", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.RestartLimit = 2;
            setup.Progress = new ProgressSettings { OutputIntervalMs = 0, PersistWithinMs = 0 };
            var runs = 0;
            var running = new HarnessTests.Deferred();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "heavy",
                Description = "Never finishes",
                Replay = ToolReplay.Safe,
                Execute = async (_, api, callContext) =>
                {
                    Interlocked.Increment(ref runs);
                    api.Output("partial\n");
                    Volatile.Read(ref running).Resolve();
                    await Aborted(callContext.CancellationToken);
                    return new ToolExecutionResult();
                },
            }));
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("heavy", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxStep.From(request => FauxAssistantMessage($"saw: {TextOf(request.Messages[^1])}")));
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            for (var crash = 1; crash <= 2; crash++)
            {
                await Volatile.Read(ref running).Task;
                Volatile.Write(ref running, new HarnessTests.Deferred());
                await Stored();
                opened.Kill();
                opened = await Open(openFile, killed, path, setup);
                opened.Harness.Resume();
            }
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            Equal(2, Volatile.Read(ref runs), "runs");
            var result = (await AllEntries(opened.Root)).First(entry => entry.Kind == "pi.tool-result");
            var message = (JsonObjectValue)result.Model![0];
            Equal(true, message["isError"] is JsonBooleanValue { Value: true }, "isError");
            MatchJson("""{"diagnostics":[{"severity":"error","code":"restart_limit","message":"Tool heavy was interrupted 2 times in a row; it is not run again"}]}""",
                result.Data, "data");
            // The slot's partial output is kept, as for an interrupted call.
            Check(TextOf(message)?.Contains("partial", StringComparison.Ordinal) == true, $"partial output kept: {TextOf(message)}");
            var toolTask = (await opened.Harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.tool" }, 10), Context)).Items[0];
            MatchJson("""{"status":"terminal","outcome":{"status":"failed","error":{"detail":{"reason":"restart_limit"}}}}""", toolTask.State.ToJson(), "tool task");
            Check(setup.Infos.Snapshot().Contains(Interrupted(toolTask.Id, "pi.tool", ReportReason.RestartLimit, 2)),
                $"task_interrupted reported: {string.Join(", ", setup.Infos.Snapshot())}");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(others + "fails-a-compaction-into-compactionfailure", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.RestartLimit = 1;
            // Little recent context is kept, so the first turn is summarized.
            setup.Compaction = new CompactionSettings { Enabled = false, KeepRecentTokens = 10 };
            setup.Faux.SetResponses(FauxAssistantMessage("a1"), FauxAssistantMessage("a2"));
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input(string.Concat(Enumerable.Repeat("u1 ", 200))), Context)).WaitAsync(Context);
            await (await opened.Root.SubmitAsync(SubmissionDraft.Input(string.Concat(Enumerable.Repeat("u2 ", 200))), Context)).WaitAsync(Context);
            // The summary request never returns.
            var (step, reached) = Unanswered();
            setup.Faux.SetResponses(step);
            var compaction = await opened.Root.CompactAsync(null, Context);
            await reached;
            opened.Kill();

            opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            const string error = "Compaction was interrupted once";
            SameJson($$$$"""{"status":"failed","error":{"message":"{{{{error}}}}","detail":{"reason":"restart_limit"}}}""",
                OutcomeJson(await opened.Harness.WaitForTaskAsync(compaction, Context)), "outcome");
            // v1.5.0 names the field `trigger` (area vocabulary); this port keeps v1.2.0's `reason`.
            SameJson($$$$"""{"taskId":{{{{compaction.Value}}}},"reason":"manual","error":"{{{{error}}}}"}""", Field(await Live(opened.Harness), "compactionFailure"),
                "compaction failure");
            Check(!(await Live(opened.Harness)).ContainsKey("compactions"), "no compaction status");
            await Eventually(() => setup.Infos.Count > 0);
            Check(setup.Infos.Snapshot().Contains(Interrupted(compaction, "pi.compaction", ReportReason.RestartLimit, 1)),
                $"task_interrupted reported: {string.Join(", ", setup.Infos.Snapshot())}");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── crash edges ───────────────────────────────────────────────────────────

        var edges = prefix + "crash-edges.";

        yield return KilledStorages.Case(edges + "charges-a-crash-during-a-poll-s-fetch-then-polls-the-same-deferred-response-again", async killed =>
        {
            var path = SqlitePath();
            var (setup, fetching, fetches) = DeferredSetup();
            setup.Retry = Retry(2, 1);
            var (id, taskId) = await CrashWhilePolling(openFile, killed, path, setup, fetching.Task);

            var opened = await Open(openFile, killed, path, setup);
            var values = LiveValues(opened.Harness);
            Equal(1, (await opened.Harness.GetTaskAsync(taskId, Context))?.Restarts, "restarts");
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            MatchExactly("""{"attempt":1,"retry":{"at":"<number>","error":"The host stopped while the deferred answer was fetched, on attempt 1"}}""",
                values.Snapshot().Select(Generation).FirstOrDefault(generation => generation?.ContainsKey("retry") == true), "retry");
            Check(values.Snapshot().Select(Generation).Any(generation => Number(generation, "attempt") == 2 && generation!.ContainsKey("deferred")),
                "attempt 2 polls");
            // One request, polled twice: the provider's job is neither dropped nor submitted again.
            Equal(1, setup.Faux.State.CallCount, "calls");
            Equal(2, fetches(), "fetches");
            Equal(0, setup.Faux.State.CancelledDeferred.Count, "cancelled");
            var entries = await AllEntries(opened.Root);
            Equal("deferred answer", TextOf(entries[^1].Model![0]), "answer");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(edges + "cancels-the-deferred-response-when-a-crash-during-its-poll-ends-the-run", async killed =>
        {
            var path = SqlitePath();
            var (setup, fetching, _) = DeferredSetup();
            setup.Retry = NoRetry;
            var (id, taskId) = await CrashWhilePolling(openFile, killed, path, setup, fetching.Task);
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            const string text = "The host stopped while the deferred answer was fetched, on attempt 1";
            MatchJson($$$$"""{"status":"unanswered","reason":"interrupted","detail":"{{{{text}}}}"}""", (await Settled(opened.Harness, id)).ToJson(), "settled");
            SameJson(RunFailure(taskId, id, "interrupted", text), await Live(opened.Harness), "live");
            Equal(1, setup.Faux.State.CancelledDeferred.Count, "cancelled");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(edges + "charges-an-interrupted-poll-even-when-the-clock-moved-backwards", async killed =>
        {
            var path = SqlitePath();
            var (setup, fetching, _) = DeferredSetup(10);
            double now = 1000;
            setup.Now = () => Volatile.Read(ref now);
            setup.Retry = NoRetry;
            var first = await Open(openFile, killed, path, setup);
            first.Harness.Resume();
            var id = (await first.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
            await Eventually(async () => Field(Generation(await Live(first.Harness)), "deferred") is not null);
            Volatile.Write(ref now, 1010);
            first.Harness.Resume();
            await fetching.Task;
            first.Kill();
            // A replacement host whose clock is behind: the poll looks not due yet, but its fetch was cut off.
            Volatile.Write(ref now, 1000);
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            MatchJson("""{"status":"unanswered","reason":"interrupted"}""", (await Settled(opened.Harness, id)).ToJson(), "settled");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(edges + "charges-a-request-once-when-a-crash-is-followed-by-a-clean-close-before-the-charge", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.Retry = Retry(3, 1);
            var (id, taskId) = await CrashWhileStreaming(openFile, killed, path, first);
            var setup = Setup();
            setup.Retry = Retry(3, 1);
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var opened = await Open(openFile, killed, path, setup);
            await opened.Harness.CloseAsync(Context);
            opened = await Open(openFile, killed, path, setup);
            Equal(1, (await opened.Harness.GetTaskAsync(taskId, Context))?.Restarts, "restarts");
            var values = LiveValues(opened.Harness);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            var charged = values.Snapshot().Select(Generation).Where(generation => generation?.ContainsKey("retry") == true);
            Seq([1.0], charged.Select(generation => Number(generation, "attempt") ?? 0), "charged attempts");
            Seq(["pi.user", "pi.assistant", "pi.assistant"], await Kinds(opened.Root), "kinds");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(edges + "ends-a-run-restart_limit-when-charging-the-interrupted-request-keeps-being-cut-off", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.RestartLimit = 1;
            var (id, taskId) = await CrashWhileStreaming(openFile, killed, path, first);
            // At the limit: the partial is still kept.
            var setup = Setup();
            setup.RestartLimit = 1;
            var opened = await Open(openFile, killed, path, setup);
            var partial = TextOf(PartialMessage(await Live(opened.Harness)))!;
            opened.Harness.Resume();
            const string text = "The generation was interrupted once in its request phase";
            MatchJson($$$$"""{"status":"unanswered","reason":"restart_limit","detail":"{{{{text}}}}"}""", (await Settled(opened.Harness, id)).ToJson(), "settled");
            SameJson(RunFailure(taskId, id, "restart_limit", text), await Live(opened.Harness), "live");
            var entries = await AllEntries(opened.Root);
            Seq(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            Equal(partial, TextOf(entries[1].Model![0]), "partial");
            Equal(0, setup.Faux.State.CallCount, "calls");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(edges + "drops-the-partial-past-the-limit-where-converting-it-may-be-what-keeps-failing", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            var (id, taskId) = await CrashWhileStreaming(openFile, killed, path, first);
            // As if the charge had been cut off twice before: the open after this crash counts 3, past the limit of 2.
            var raw = await openFile(path);
            var record = await raw.GetTaskAsync(taskId, Context);
            await raw.CommitWritesAsync([new TaskWrite(record! with { Restarts = 2 })], Context);
            await raw.CloseAsync(Context);
            var setup = Setup();
            setup.RestartLimit = 2;
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            MatchJson("""{"status":"unanswered","reason":"restart_limit","detail":"The generation was interrupted 3 times in a row in its request phase"}""",
                (await Settled(opened.Harness, id)).ToJson(), "settled");
            Seq(["pi.user"], await Kinds(opened.Root), "kinds");
            Equal(null, Generation(await Live(opened.Harness)), "generation");
            await opened.Harness.CloseAsync(Context);
        });

        yield return KilledStorages.Case(edges + "ends-a-run-someone-aborted-as-aborted-when-its-abort-handler-is-cut-off-restartlimit-times", async killed =>
        {
            var path = SqlitePath();
            var cancels = 0;
            var baseSetup = Setup(new FauxOptions { PollAfterMs = 3_600_000 });
            // The abort handler of a polling generation cancels its response: make that hang.
            var setup = baseSetup.WithModels(new ModelsOverride(baseSetup.Faux)
            {
                CancelDeferredOverride = (_, _, _) =>
                {
                    Interlocked.Increment(ref cancels);
                    return new TaskCompletionSource().Task;
                },
            });
            setup.RestartLimit = 2;
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            setup.Faux.SetResponses(FauxAssistantMessage("never"));
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
            var taskId = RunTaskId(await Live(opened.Harness));
            await Eventually(async () => (await opened.Harness.GetTaskAsync(taskId, Context))?.State.Status == DurableTaskStatus.Sleeping);
            _ = opened.Root.AbortAsync(Context).ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            for (var crash = 1; crash <= 2; crash++)
            {
                var expected = crash;
                await Eventually(() => Volatile.Read(ref cancels) == expected);
                opened.Kill();
                opened = await Open(openFile, killed, path, setup);
                Equal(crash, (await opened.Harness.GetTaskAsync(taskId, Context))?.Restarts, $"restarts after crash {crash}");
                opened.Harness.Resume();
            }
            var detail = $"Abort handler of task {taskId.Value} (pi.generation) was cut off 2 times";
            MatchJson($$$$"""{"status":"unanswered","reason":"aborted","detail":"{{{{detail}}}}"}""", (await Settled(opened.Harness, id)).ToJson(), "settled");
            SameJson(RunFailure(taskId, id, "aborted", detail), await Live(opened.Harness), "live");
            MatchJson("""{"status":"faulted","error":{"detail":{"reason":"restart_limit"}}}""",
                OutcomeJson(await opened.Harness.WaitForTaskAsync(taskId, Context)), "outcome");
            await Eventually(() => setup.Infos.Snapshot().Any(info => info.Kind == ReportKind.TaskFaulted));
            Check(setup.Infos.Snapshot().Any(info => info is { Kind: ReportKind.TaskFaulted, Reason: ReportReason.RestartLimit }),
                $"task_faulted restart_limit reported: {string.Join(", ", setup.Infos.Snapshot())}");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── reports of work crashes ended ─────────────────────────────────────────

        yield return KilledStorages.Case(prefix + "reports-of-work-crashes-ended.reports-task_interrupted-for-a-run-that-ends-interrupted-and-for-a-task-that-ends-at-the-limit", async killed =>
        {
            var path = SqlitePath();
            var first = Streaming();
            first.Retry = NoRetry;
            var (_, taskId) = await CrashWhileStreaming(openFile, killed, path, first);
            var setup = Setup();
            setup.Retry = NoRetry;
            var opened = await Open(openFile, killed, path, setup);
            opened.Harness.Resume();
            await opened.Harness.WaitForTaskAsync(taskId, Context);
            await Eventually(() => setup.Infos.Count > 0);
            Seq([Interrupted(taskId, "pi.generation", ReportReason.Interrupted, 1)], setup.Infos.Snapshot(), "infos");
            Check(setup.Reports[0].Message.Contains("The host stopped while the model was answering, on attempt 1", StringComparison.Ordinal),
                $"report: {setup.Reports[0].Message}");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── the restart limit on the normal path ──────────────────────────────────

        yield return Case(prefix + "the-restart-limit-on-the-normal-path.resolves-the-settings-no-more-often-than-before-only-a-task-that-was-cut-off-reads-the-limit", async () =>
        {
            var setup = Setup();
            var settings = new CountingSettings();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "t",
                Description = "t",
                Replay = ToolReplay.Safe,
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult()),
            }));
            const int rounds = 5;
            var responses = new List<FauxStep>();
            for (var round = 0; round <= rounds; round++)
            {
                var call = $"c{round}";
                responses.Add(FauxStep.From(_ => FauxAssistantMessage([FauxToolCall("t", JsonObjectValue.Empty, call)], stopReason: "toolUse")));
                responses.Add(FauxStep.From(_ => FauxAssistantMessage([FauxText("done")])));
            }
            setup.Faux.SetResponses([.. responses]);
            var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
            {
                Models = setup.Models,
                Registry = setup.Registry,
                Settings = settings,
                Now = () => setup.Now(),
                OnReport = setup.Record,
            }, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
            await (await root.SubmitAsync(SubmissionDraft.Input("warm up"), Context)).WaitAsync(Context);
            settings.Resolutions = 0;
            for (var round = 0; round < rounds; round++)
                await (await root.SubmitAsync(SubmissionDraft.Input($"round {round}"), Context)).WaitAsync(Context);
            // One generation round with one tool call, no more often than before the restart limit applied to every kind
            // (#11327: 49; group commit coalesces some reconcile passes).
            var perRound = settings.Resolutions / (double)rounds;
            Check(perRound <= 49, $"settings resolved {perRound} times a round, more than 49");
            await harness.CloseAsync(Context);
        });
    }
}
