// Authored for PiSharp (no upstream test file): the scenarios of the real-process crash tests (decision 0005, "Crash tests"). The test
// executable relaunches itself as a child (`--crash-child <scenario> <backend> <storage path> <checkpoint>`), which opens a Harness on the
// storage, runs one scenario with a scripted faux model and test tools, and prints `CHECKPOINT <name>` when it reaches the named
// checkpoint. CrashCases.cs is the parent: it kills the child's process tree at the sentinel, reopens the storage in process and asserts
// the recovery guarantees of upstream packages/durable/docs/spec.md.
//
// Child protocol (stdout, one line each, flushed): `CHECKPOINT <name>` once the checkpoint is reached (the child then blocks, or for a
// keep-going checkpoint carries on writing); `IDLE` when the scenario finished without reaching it (the parent fails the case); `ERROR
// <text>` when the child failed. Diagnostics go to stderr. A child kills itself after two minutes in case the parent died.
//
// The same scenario code runs in both processes: BuildSetup registers identical tools, hooks, sections and the deterministic faux model, as
// a host process registers the same code again after a crash. Gates and holds only act in the child.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static SessionTests;

/// <summary>Checkpoints of the crash child. In the parent (<see cref="Parent"/>) every gate is open.</summary>
internal sealed class CrashGate(string? target)
{
    private static readonly Lock OutputLock = new();

    public static CrashGate Parent { get; } = new(null);

    /// <summary>The checkpoint the child stops at; null in the parent.</summary>
    public string? Target { get; } = target;
    public bool Child => Target is not null;

    /// <summary>Child holds: the scenario's blocked tool started, its blocked request was sent, or its follow-up was queued.</summary>
    public TaskCompletionSource ToolStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RequestReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Writes one protocol line to stdout and flushes it.</summary>
    public static void Emit(string line)
    {
        lock (OutputLock)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    /// <summary>A task that never completes: the child waits there to be killed.</summary>
    public static Task Never() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    /// <summary>A blocking checkpoint: at the target, prints the sentinel and never returns.</summary>
    public Task At(string name)
    {
        if (Target != name) return Task.CompletedTask;
        Emit("CHECKPOINT " + name);
        return Never();
    }

    /// <summary>A keep-going checkpoint: at the target, prints the sentinel and carries on.</summary>
    public void Mark(string name)
    {
        if (Target == name) Emit("CHECKPOINT " + name);
    }
}

/// <summary>Kills at a commit boundary: the first batch <paramref name="matches"/> accepts blocks forever, before it reaches the inner
/// storage or right after it landed.</summary>
internal sealed class CrashStorage(IStorage inner, Func<IReadOnlyList<StorageWrite>, bool> matches, bool after, string checkpoint) : IStorage
{
    private int fired;

    public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        var hit = Volatile.Read(ref fired) == 0 && matches(writes) && Interlocked.Exchange(ref fired, 1) == 0;
        if (hit && !after)
        {
            CrashGate.Emit("CHECKPOINT " + checkpoint);
            await CrashGate.Never();
        }
        var seq = await inner.CommitAsync(writes, context);
        if (hit)
        {
            CrashGate.Emit("CHECKPOINT " + checkpoint);
            await CrashGate.Never();
        }
        return seq;
    }

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanConversationsAsync(query, limit, cursor, context);
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        inner.GetEntryAsync(conversationId, id, context);
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanEntriesAsync(query, limit, cursor, context);
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        inner.FindDocumentAsync(address, at, context);
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanDocumentsAsync(query, limit, cursor, context);
    public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
}

/// <summary>The crash scenarios, shared by the child and the parent.</summary>
internal static class CrashScenarios
{
    internal static readonly DurableContext Ctx = DurableContext.Background;

    /// <summary>Appended to every chat answer, so the slow child stream of <c>chat.stream</c> commits many partials.</summary>
    internal static readonly string Padding = " " + new string('.', 400);

    /// <summary>A commit-boundary checkpoint: the batch it matches, and whether the kill comes after the batch landed.</summary>
    internal sealed record CommitTrigger(Func<IReadOnlyList<StorageWrite>, bool> Matches, bool After);

    /// <summary>Opens a backend the way a host (<c>pisharp durable --continue</c>) does: no special recovery steps.</summary>
    internal static async Task<IStorage> OpenStorage(string backend, string path) => backend switch
    {
        "sqlite" => await SqliteStorage.OpenAsync(path),
        "jsonl" => await JsonlStorage.OpenAsync(path, Ctx),
        _ => throw new ArgumentException("Unknown crash backend " + backend),
    };

    /// <summary>The marker file a test tool appends one line to per execution; it survives the kill.</summary>
    internal static string RunsMarker(string workDir) => Path.Combine(workDir, "tool-runs.txt");

    internal static int ToolRuns(string workDir) => File.Exists(RunsMarker(workDir)) ? File.ReadAllLines(RunsMarker(workDir)).Length : 0;

    // ─── Commit predicates ──────────────────────────────────────────────────

    internal static bool StopAnswer(IReadOnlyList<StorageWrite> writes) => writes.OfType<EntryWrite>().Any(write =>
        write.Value.Kind == "pi.assistant" && write.Value.Model is [JsonObjectValue message, ..] &&
        message["stopReason"] is JsonStringValue { Value: "stop" });

    internal static bool ToolIntent(IReadOnlyList<StorageWrite> writes) => writes.OfType<TaskWrite>().Any(write =>
        write.Value.Kind == "pi.tool" && write.Value.State is LiveTaskState { Checkpoint: JsonObjectValue checkpoint } &&
        checkpoint.TryGetValue("phase", out var phase) && phase is JsonStringValue { Value: "execute" });

    internal static bool ToolResultEntry(IReadOnlyList<StorageWrite> writes) => writes.OfType<EntryWrite>().Any(write => write.Value.Kind == "pi.tool-result");

    internal static Func<IReadOnlyList<StorageWrite>, bool> Admission(string requestId) => writes =>
        writes.OfType<SubmissionWrite>().Any(write => write.Value.RequestId == requestId);

    internal static bool AbortMark(IReadOnlyList<StorageWrite> writes) => writes.OfType<TaskWrite>().Any(write =>
        write.Value.AbortRequested && write.Value.State is LiveTaskState);

    /// <summary>The commit trigger of a checkpoint; null for checkpoints the scenario code reaches itself.</summary>
    internal static CommitTrigger? Trigger(string scenario, string checkpoint) => (scenario, checkpoint) switch
    {
        ("chat", "answer-before") => new(StopAnswer, false),
        ("chat", "answer-after") => new(StopAnswer, true),
        ("tool-unsafe" or "tool-safe", "intent-after") => new(ToolIntent, true),
        ("tool-unsafe", "result-after") => new(ToolResultEntry, true),
        ("followup", "first-answer-after") => new(StopAnswer, true),
        ("submit", "admit-before") => new(Admission("r1"), false),
        ("submit", "admit-after") => new(Admission("r1"), true),
        ("submit", "queued-admit-after") => new(Admission("r2"), true),
        ("abort", _) => new(AbortMark, true),
        _ => null,
    };

    internal static bool UsesTool(string scenario) => scenario is "tool-unsafe" or "tool-safe" or "abort";

    // ─── Setup ──────────────────────────────────────────────────────────────

    /// <summary>The models, registry and settings of a scenario. Identical in both processes except for the child's gates, holds and its
    /// slow <c>chat.stream</c> model.</summary>
    internal static ChatSetup BuildSetup(string scenario, string checkpoint, CrashGate gate, string workDir)
    {
        var slow = gate.Child && scenario == "chat" && checkpoint == "stream";
        var setup = new ChatSetup(new FauxModels(new FauxOptions
        {
            Api = "faux-crash",
            TokensPerSecond = slow ? 100 : null,
            MinTokenSize = slow ? 1 : null,
            MaxTokenSize = slow ? 1 : null,
        }));
        setup.Progress = new ProgressSettings { PartialIntervalMs = 5 };
        if (scenario == "chat")
        {
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 60_000 };
            HarnessTests.AddSection(setup.Registry, "preamble", async (_, _) =>
            {
                await gate.At("prepare");
                return "Answer briefly.";
            }, tag: false);
        }
        if (UsesTool(scenario))
        {
            HarnessTests.AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "work",
                Description = "A test tool that counts its executions in a marker file",
                Replay = scenario == "tool-safe" ? ToolReplay.Safe : null,
                Execute = async (_, _, callContext) =>
                {
                    File.AppendAllText(RunsMarker(workDir), "run\n");
                    var run = ToolRuns(workDir);
                    if (scenario == "abort" && gate.Child)
                    {
                        gate.ToolStarted.TrySetResult();
                        await HarnessTests.Aborted(callContext.CancellationToken);
                    }
                    await gate.At("execute");
                    return new ToolExecutionResult { Content = ToolsTests.TextContent($"ran {run}") };
                },
            }));
            HarnessTests.AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (_, _, _) =>
                {
                    await gate.At("before-tool");
                    return null;
                },
            });
            HarnessTests.AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                AfterTools = (_, _, _, _) => gate.At("after-tools"),
            });
        }
        var retried = 0;
        FauxStep step = new FauxResponseFactory(async request =>
        {
            var messages = request.Messages.OfType<JsonObjectValue>().ToList();
            var users = messages.Count(message => S(message["role"]) == "user");
            var last = messages[^1];
            if (gate.Child)
            {
                if (scenario == "chat" && checkpoint == "retry" && Interlocked.Exchange(ref retried, 1) == 0)
                    return FauxAssistantMessage([], stopReason: "error", errorMessage: "503 Service Unavailable");
                if (scenario == "abort" && checkpoint == "request-mark")
                {
                    gate.RequestReached.TrySetResult();
                    await HarnessTests.Aborted(request.Signal);
                }
                if (scenario == "submit" && checkpoint == "queued-admit-after") await CrashGate.Never();
                if (scenario == "followup" && users == 1) await gate.SecondQueued.Task;
            }
            await gate.At(scenario == "followup" ? (users == 1 ? "first-request" : "second-request") : "request");
            if (S(last["role"]) == "toolResult") return FauxAssistantMessage("done");
            if (UsesTool(scenario)) return FauxAssistantMessage([FauxToolCall("work", JsonObjectValue.Empty, "c1")], stopReason: "toolUse");
            return FauxAssistantMessage("answer: " + TextOf(last) + (scenario == "chat" ? Padding : ""));
        });
        setup.Faux.SetResponses([.. Enumerable.Repeat(step, 16)]);
        return setup;
    }

    // ─── Live document reads ────────────────────────────────────────────────

    internal static Task<JsonObjectValue?> LiveOf(IHarness harness, IConversation root) => harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);

    internal static JsonValue? Field(JsonObjectValue? value, params string[] path)
    {
        JsonValue? current = value;
        foreach (var key in path)
            current = current is JsonObjectValue item && item.TryGetValue(key, out var next) ? next : null;
        return current;
    }

    internal static async Task<TaskId> RunTaskId(IHarness harness, IConversation root) =>
        new((long)N(Field(await LiveOf(harness, root), "run", "taskId")));

    /// <summary>Polls a condition for at most 30 seconds.</summary>
    internal static async Task Until(Func<Task<bool>> check, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Not reached: " + what);
            await Task.Delay(5);
        }
    }

    // ─── Child ──────────────────────────────────────────────────────────────

    /// <summary><c>--crash-child &lt;scenario&gt; &lt;backend&gt; &lt;storage path&gt; &lt;checkpoint&gt;</c>.</summary>
    internal static async Task<int> RunChildAsync(string scenario, string backend, string path, string checkpoint)
    {
        // A child whose parent died must not linger.
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Environment.Exit(3), TaskScheduler.Default);
        try
        {
            var gate = new CrashGate(checkpoint);
            var workDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var storage = await OpenStorage(backend, path);
            if (Trigger(scenario, checkpoint) is { } trigger) storage = new CrashStorage(storage, trigger.Matches, trigger.After, checkpoint);
            var setup = BuildSetup(scenario, checkpoint, gate, workDir);
            var opened = await OpenChat(storage, setup);
            var (harness, root) = (opened.Harness, opened.Root);
            harness.Resume();
            switch (scenario)
            {
                case "chat":
                    await root.SubmitAsync(SubmissionDraft.Input("go", requestId: "r1"), Ctx);
                    if (checkpoint == "retry")
                    {
                        await Until(async () => Field(await LiveOf(harness, root), "generation", "retry") is not null, "retry");
                        await gate.At("retry");
                    }
                    if (checkpoint == "stream")
                    {
                        await Until(async () => TextOf(Field(await LiveOf(harness, root), "generation", "message")) is { Length: > 0 }, "partial");
                        gate.Mark("stream");
                    }
                    break;
                case "tool-unsafe" or "tool-safe":
                    await root.SubmitAsync(SubmissionDraft.Input("go", requestId: "r1"), Ctx);
                    break;
                case "followup" or "submit":
                    await root.SubmitAsync(SubmissionDraft.Input(scenario == "followup" || checkpoint == "queued-admit-after" ? "one" : "go", requestId: "r1"), Ctx);
                    if (scenario == "followup" || checkpoint == "queued-admit-after")
                    {
                        await root.SubmitAsync(SubmissionDraft.Input("two", WhenBusy.FollowUp, "r2"), Ctx);
                        gate.SecondQueued.TrySetResult();
                    }
                    break;
                case "abort":
                    await root.SubmitAsync(SubmissionDraft.Input("go", requestId: "r1"), Ctx);
                    await (checkpoint == "request-mark" ? gate.RequestReached.Task : gate.ToolStarted.Task);
                    if (checkpoint == "conversation-mark") await root.AbortAsync(Ctx);
                    else await harness.AbortTaskAsync(await RunTaskId(harness, root), Ctx);
                    break;
                default:
                    throw new ArgumentException("Unknown crash scenario " + scenario);
            }
            await harness.WaitForIdleAsync(Ctx);
            CrashGate.Emit("IDLE");
            await CrashGate.Never();
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            CrashGate.Emit("ERROR " + error.GetType().Name + ": " + error.Message.ReplaceLineEndings(" "));
            return 1;
        }
    }
}
