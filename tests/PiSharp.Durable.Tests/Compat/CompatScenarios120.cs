// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): the scenarios of tools/DurableFixtures/scenarios-v1.2.0.mjs that the v1.2.0 sync
// areas have ported so far, run on PiSharp against Fixtures/v1.2.0/upstream-sessions (CompatCases.cs): read, raw, harness and written, as
// for the v1.1.0 scenarios, and observed (what calls returned or rejected with, observed.json) and events (the root conversation's agent
// events, events.json). Keep each scenario in step with its upstream twin: same faux responses, same calls in the same order.
//
// Events: scenarios.mjs `open()` attaches watchEvents to the root conversation when the capture records events, pushes the snapshot and
// then every event delivered, and its wrapped close() waits a macrotask and stops the stream before closing. Here Open attaches a
// CompatEvents recorder when the case expects events, and Close waits until the expected number of events arrived (bounded), lets the
// Session line drain, then stops the stream and closes; the case compares the whole sequence, batch boundaries left out as upstream does.
// A scenario's `aborted(signal)` resolves on abort, as scenarios.mjs's does.
//
// Concurrent nested calls: Node runs the invocations of nested-tools' two parallel echo calls in lockstep on its event loop (one intent,
// the other's intent, one result, the other's), while PiSharp's invocations run on the thread pool, a step and phase inline when the line
// is idle. The C# scenario holds them to Node's order without changing what they do: a beforeTool hook holds the second call until the
// first's slot is running, the first returns once the second's slot is running, and the second once the first's is done. Hooks and waits
// leave no record, so the records, events and observed results stay upstream's.
//
// Read only (no Run): deferred-tool-search (areas a and k) needs tool_search, which work list area k ports; its read, raw and harness cases
// run. codemode (area d) is not listed yet.
//
// read-image (area e) writes its two files to a fresh directory and gives the Harness a LocalExecutionEnv there (upstream NodeExecutionEnv).
// pixelPng's IDAT is Node's deflateSync output, which .NET's zlib need not reproduce byte for byte, so the PNG is the captured file's bytes
// (the image block in records.json carries them); pixelBmp is built as upstream builds it.
//
// Not checked:
// - session-failed's observed `idle`. capture.mjs's open() replaces harness.close with a wrapper that first waits a macrotask and stops the
//   event stream, and the Session's own fail() calls that replaced close: the capture seals the Harness a macrotask late, so its
//   waitForIdle() after the failure resolves (`{ "value": null }`). Without the wrapper it rejects with SessionFailed, as upstream's
//   harness-storage-failure.test.ts asserts ("ends everything with the first error ...: ... a wait that starts now"); PiSharp's Fail()
//   seals at once and the case asserts that rejection. Every other observed field is compared.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using static ChatTests;
using static SessionTests;

/// <summary>Records a scenario's agent events for comparison with events.json (see the file header).</summary>
internal sealed class CompatEvents
{
    private static readonly AsyncLocal<CompatEvents?> Current = new();
    private readonly JsonArrayValue expected;
    private readonly SyncList<JsonValue> recorded = new();
    private IAgentEventStream? stream;
    private IHarness? harness;

    private CompatEvents(JsonArrayValue expected) { this.expected = expected; }

    /// <summary>A recorder expecting <paramref name="phase"/>'s events of events.json; null when there are none.</summary>
    public static CompatEvents? Expect(JsonObjectValue? events, string phase) =>
        events is not null && events.TryGetValue(phase, out var list) ? new CompatEvents((JsonArrayValue)list) : null;

    /// <summary>Runs <paramref name="run"/> with <paramref name="events"/> recording the Harness it opens.</summary>
    public static async Task<T> Record<T>(CompatEvents? events, Func<Task<T>> run)
    {
        Current.Value = events;
        try { return await run(); }
        finally { Current.Value = null; }
    }

    /// <summary>scenarios.mjs <c>open()</c>'s stream: the snapshot, then every event delivered.</summary>
    public static async Task AttachAsync(IHarness harness, ConversationId root)
    {
        if (Current.Value is not { } events || events.stream is not null) return;
        events.harness = harness;
        events.stream = await harness.WatchEventsAsync(root, DurableContext.Background);
        events.recorded.Add(events.stream.Snapshot.ToJson());
        events.stream.Start((batch, _) =>
        {
            foreach (var item in batch) events.recorded.Add(item.ToJson());
            return Task.CompletedTask;
        });
    }

    /// <summary>scenarios.mjs's wrapped <c>close()</c>: stops the stream, then closes.</summary>
    public static async Task CloseAsync(IHarness harness)
    {
        if (Current.Value is { } events && ReferenceEquals(events.harness, harness) && events.stream is { } stream)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (events.recorded.Count < events.expected.Count && DateTime.UtcNow < deadline) await Task.Delay(5);
            try { await ((Session)harness).FlushLineAsync(); } catch (Exception) { /* a failed Session */ }
            await Task.Delay(20);
            await stream.StopAsync();
        }
        await harness.CloseAsync(DurableContext.Background);
    }

    /// <summary>The recorded events are the expected ones, in order.</summary>
    public void Check(string what) => CompatTests.SameJson(expected, JsonValue.Array(recorded.Snapshot()), what);
}

/// <summary>One v1.2.0 scenario: <see cref="Run"/> writes the session, closes it and returns what it observed (or null); <see cref="Resume"/>,
/// when present, reopens and finishes it. Without <see cref="Run"/> the scenario is read only (see the file header).</summary>
internal sealed record CompatScenario120(string Name, string[] Areas, Func<Func<Task<IStorage>>, Task<JsonValue?>>? Run,
    Func<Func<Task<IStorage>>, Task<JsonValue?>>? Resume = null, IReadOnlyList<string>? UnobservedFields = null);

/// <summary>The ported scenarios of tools/DurableFixtures/scenarios-v1.2.0.mjs.</summary>
internal static class CompatScenarios120
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const double T = 1000;

    private static JsonObjectValue Answer(string text) => FauxAssistantMessage(text, timestamp: T);

    private static ChatSetup Chat(FauxOptions? options = null) =>
        new(new FauxModels((options ?? new FauxOptions()) with { Api = "faux" })) { Now = () => CompatScenarios.Now };

    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage storage, Action<Exception>? onReport = null, IExecutionEnv? env = null)
    {
        var harness = await Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings, Now = () => setup.Now(), OnReport = onReport,
            Env = env is null ? null : (_, _) => Task.FromResult<IExecutionEnv?>(env),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        await CompatEvents.AttachAsync(harness, root.Id);
        return new OpenedChat(harness, root);
    }

    /// <summary>Closes a Harness Open returned (scenarios.mjs's wrapped close).</summary>
    private static Task Close(IHarness harness) => CompatEvents.CloseAsync(harness);

    /// <summary>scenarios-v1.2.0.mjs <c>described</c>: an error as observed, with upstream's error names.</summary>
    internal static JsonObjectValue Described(Exception error)
    {
        var name = error switch
        {
            SessionFailedException => "SessionFailed",
            StorageRequestException => "StorageRequestError",
            ConversationBusyException => "ConversationBusy",
            ArgumentException => "TypeError",
            _ => "Error",
        };
        var properties = new List<(string, JsonValue)> { ("name", name), ("message", error.Message) };
        if (error.InnerException is { } cause) properties.Add(("cause", cause.Message));
        return Obj([.. properties]);
    }

    /// <summary>scenarios-v1.2.0.mjs <c>settle</c>: <c>{ value }</c> (null for any value a scenario does not record) or <c>{ error }</c>.</summary>
    internal static async Task<JsonObjectValue> Settle(Func<Task> run)
    {
        try
        {
            await run();
            return Obj(("value", JsonValue.Null));
        }
        catch (Exception error)
        {
            return Obj(("error", Described(error)));
        }
    }

    /// <summary>Fails the next commit with <see cref="FailNext"/> (the scenario's patched <c>storage.commit</c>).</summary>
    private sealed class FailNextStorage(IStorage inner) : DelegatingStorage(inner)
    {
        private Exception? failNext;

        public Exception? FailNext
        {
            set => Volatile.Write(ref failNext, value);
        }

        public override Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
        {
            var error = Interlocked.Exchange(ref failNext, null);
            return error is not null ? Task.FromException<Seq>(error) : base.CommitAsync(writes, context);
        }
    }

    /// <summary>scenarios-v1.2.0.mjs <c>pixelPng</c>: a 1x1 red PNG (RGB, deflated scanline, real CRCs), as captured (see the file header).</summary>
    private static readonly byte[] PixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

    /// <summary>scenarios-v1.2.0.mjs <c>pixelBmp</c>: a 1x1 24-bit BMP, a red pixel, its row padded to four bytes.</summary>
    private static byte[] PixelBmp()
    {
        var bytes = new byte[58];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.TryWriteBytes(bytes.AsSpan(2), 58u);
        BitConverter.TryWriteBytes(bytes.AsSpan(10), 54u);
        BitConverter.TryWriteBytes(bytes.AsSpan(14), 40u);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), 1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), 1);
        BitConverter.TryWriteBytes(bytes.AsSpan(26), (ushort)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), (ushort)24);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), 4u);
        bytes[56] = 255;
        return bytes;
    }

    private static InputEntry Mention(string text) => new("chat.mention", Obj(("author", "ann"), ("text", text)));

    /// <summary>scenarios-v1.2.0.mjs <c>compactingChat</c>: the faux model answers agent and summarization requests from separate queues.</summary>
    private sealed class CompactingChat
    {
        public CompactingChat(double contextWindow)
        {
            Setup = Chat(new FauxOptions { Models = [new FauxModelOptions("faux-1", contextWindow, 900)] });
            Setup.Stream = new ConversationStreamOptions { CacheRetention = "none" };
            Setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 2, BaseDelayMs = 1 };
            Setup.Compaction = new CompactionSettings { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 150, BackgroundTokens = 0 };
            Setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "section:preamble",
                Sections = [Define.Section("preamble", (_, _) => ValueTask.FromResult<string?>("You are helpful."), false)],
            }));
            Setup.Faux.SetResponses([.. Enumerable.Range(0, 100).Select(_ => (FauxStep)new FauxResponseFactory(async request =>
            {
                var summary = request.Messages.Length > 0 && request.Messages[0] is JsonObjectValue first && first["role"] is JsonStringValue { Value: "system" } &&
                    first["content"] is JsonStringValue { Value: var content } &&
                    content.StartsWith("You are a context summarization assistant", StringComparison.Ordinal);
                var queue = summary ? Summaries : Agent;
                Func<Task<JsonObjectValue>> step;
                lock (queue)
                {
                    if (queue.Count == 0) throw new InvalidOperationException($"No scripted {(summary ? "summary" : "agent")} response");
                    step = queue[0];
                    queue.RemoveAt(0);
                }
                return await step();
            }))]);
        }

        public ChatSetup Setup { get; }
        public List<Func<Task<JsonObjectValue>>> Agent { get; } = [];
        public List<Func<Task<JsonObjectValue>>> Summaries { get; } = [];

        public static void Push(List<Func<Task<JsonObjectValue>>> queue, Func<Task<JsonObjectValue>> step)
        {
            lock (queue) queue.Add(step);
        }

        public static int Count(List<Func<Task<JsonObjectValue>>> queue)
        {
            lock (queue) return queue.Count;
        }
    }

    // ─── Tool scenarios (tool task v2, work list area a) ─────────────────────

    /// <summary>scenarios-v1.2.0.mjs <c>textTool</c>'s parameters: <c>Type.Object({ text: Type.Optional(Type.String()), id:
    /// Type.Optional(Type.Number()) })</c>, as TypeBox prints it.</summary>
    private static readonly JsonObjectValue TextParameters = O("""{"type":"object","properties":{"text":{"type":"string"},"id":{"type":"number"}}}""");

    /// <summary><c>Type.Object({ count: Type.Number() })</c>, as TypeBox prints it.</summary>
    private static readonly JsonObjectValue Count = O("""{"type":"object","required":["count"],"properties":{"count":{"type":"number"}}}""");

    /// <summary>scenarios-v1.2.0.mjs <c>textTool</c>.</summary>
    private static ToolRegistration TextTool(string name, string description, ToolExecute execute, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration { Name = name, Description = description, Parameters = TextParameters, Execute = execute });
        return extra is null ? tool : extra(tool);
    }

    /// <summary>scenarios.mjs <c>result</c> at v1.2.0: <c>{ output, ...rest }</c>.</summary>
    private static ToolExecutionResult Result(IEnumerable<JsonObjectValue> output) => new() { Output = JsonValue.Array(output) };

    /// <summary>scenarios-v1.2.0.mjs <c>install</c>.</summary>
    private static void Install(ChatSetup setup, string name, IReadOnlyList<ToolRegistration> tools, IReadOnlyList<HookRegistration>? hooks = null) =>
        setup.Registry.Install(Define.Extension(new Extension { Name = name, Tools = [.. tools], Hooks = hooks is null ? null : [.. hooks] }));

    /// <summary>scenarios-v1.2.0.mjs <c>call</c>: a faux reply making one tool call.</summary>
    private static JsonObjectValue Call(string name, string id = "c1") => Calls((name, JsonObjectValue.Empty, id));

    private static JsonObjectValue Calls(params (string Name, JsonObjectValue Args, string Id)[] list) =>
        FauxAssistantMessage(list.Select(call => FauxToolCall(call.Name, call.Args, call.Id)), stopReason: "toolUse", timestamp: T);

    /// <summary>scenarios-v1.2.0.mjs <c>nestedResult</c>: a nested call result without its task ID and duration.</summary>
    private static JsonValue NestedResult(NestedToolExecutionResult result) =>
        JsonValue.Object(result.ToJson().Where(property => property.Key is not ("taskId" or "durationMs")));

    private static string Text(JsonValue args) =>
        ((JsonObjectValue)args).TryGetValue("text", out var text) && text is JsonStringValue { Value: var value } ? value : "";

    private static JsonArrayValue Names(IEnumerable<ToolRegistration> tools) => JsonValue.Array(tools.Select(tool => (JsonValue)tool.Name));

    /// <summary>Waits until <c>pi.live</c> shows the nested slot <paramref name="callId"/> with <paramref name="status"/>.</summary>
    private static async Task SlotStatus(IDocumentReader reader, string callId, string status, DurableContext context)
    {
        var conversation = reader is IHookApi hook ? hook.ConversationId : ((IToolExecutionApi)reader).ConversationId;
        while (true)
        {
            var live = await reader.SnapshotAsync(Live.LiveDoc, conversation, context);
            if (live is not null && live.TryGetValue("nestedTools", out var slots) && ((JsonArrayValue)slots).Cast<JsonObjectValue>()
                .Any(slot => S(slot["callId"]) == callId && S(slot["status"]) == status)) return;
            await Task.Delay(2, context.CancellationToken);
        }
    }

    /// <summary>scenarios.mjs <c>aborted</c>: resolves once <paramref name="signal"/> aborts.</summary>
    private static async Task Aborted(CancellationToken signal)
    {
        try { await HarnessTests.Aborted(signal); }
        catch (OperationCanceledException) { }
    }

    /// <summary>scenarios-v1.2.0.mjs <c>restartTools</c>: <c>batch</c> calls <c>started</c> (replay-safe, runs) and <c>gated</c> (held by a
    /// hook).</summary>
    private sealed class RestartTools
    {
        private int started;
        private int gated;

        public RestartTools(bool block)
        {
            Setup = Chat();
            Install(Setup, "tools", [
                TextTool("started", "started", async (_, api, callContext) =>
                {
                    Interlocked.Increment(ref started);
                    api.Output("partial\n");
                    await api.DetailsAsync(Obj(("step", 1)), callContext);
                    Running.Resolve();
                    if (block) await Aborted(callContext.CancellationToken);
                    return Result([]);
                }, tool => tool with { Replay = ToolReplay.Safe }),
                TextTool("gated", "gated", (_, _, _) =>
                {
                    Interlocked.Increment(ref gated);
                    return Task.FromResult(Result([]));
                }),
                Define.Tool(new ToolRegistration
                {
                    Name = "batch",
                    Description = "batch",
                    Parameters = ToolRegistration.EmptyParameters,
                    Execute = async (_, api, callContext) =>
                    {
                        await Task.WhenAll(api.ExecuteToolAsync("started", JsonObjectValue.Empty, callContext),
                            api.ExecuteToolAsync("gated", JsonObjectValue.Empty, callContext));
                        return Result([]);
                    },
                }),
            ], [Define.Hook(HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (hookCall, _, hookContext) =>
                {
                    if (S(hookCall["name"]) != "gated" || !block) return null;
                    Gated.Resolve();
                    await Aborted(hookContext.CancellationToken);
                    return null;
                },
            })]);
        }

        public ChatSetup Setup { get; }
        public HarnessTests.Deferred Running { get; } = new();
        public HarnessTests.Deferred Gated { get; } = new();
        public JsonObjectValue Observed() => Obj(("runs", Obj(("started", Volatile.Read(ref started)), ("gated", Volatile.Read(ref gated)))));
    }

    /// <summary>scenarios-v1.2.0.mjs <c>abandonTools</c>: a replay-unsafe tool creates a child task with <c>api.createTask()</c> and awaits it
    /// in memory.</summary>
    private sealed class AbandonTools
    {
        private readonly SyncList<string> phases = new();

        public AbandonTools()
        {
            Setup = Chat();
            var worker = Tasks.DefineTask(new TaskDefinition
            {
                Name = "compat.direct-worker",
                Version = 1,
                Initial = _ => Obj(("phase", "work")),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["work"] = async (_, runtime, _) =>
                    {
                        phases.Add("work");
                        Started.Resolve();
                        await Aborted(runtime.Signal);
                    },
                },
                Abort = async (task, runtime, abortContext) =>
                {
                    phases.Add($"abort {(task.AbortReason is { } reason ? StructuredTests.Wire(reason) : "undefined")}");
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), abortContext);
                },
            });
            Setup.Registry.Install(Define.Extension(new Extension { Name = "direct-worker", Tasks = [worker] }));
            Install(Setup, "tools", [TextTool("work", "work", async (_, api, callContext) =>
            {
                var id = await api.CreateTaskAsync(worker, JsonValue.Null, new TaskOptions(TaskOwnership.Task(api.TaskId)), callContext);
                await api.WaitForTaskAsync(id, callContext);
                return Result([]);
            })]);
        }

        public ChatSetup Setup { get; }
        public HarnessTests.Deferred Started { get; } = new();
        public JsonObjectValue Observed() => Obj(("phases", JsonValue.Array(phases.Snapshot().Select(phase => (JsonValue)phase))));
    }

    private static string Tokens(string label, int count) => $"{label} {new string('x', Math.Max(0, count * 4 - label.Length - 1))}";

    internal static readonly IReadOnlyList<CompatScenario120> All =
    [
        new("input-entries", ["i"], async open =>
        {
            var setup = Chat();
            var reached = new HarnessTests.Deferred();
            setup.Faux.SetResponses(Answer("hello"), new FauxResponseFactory(async request =>
            {
                reached.Resolve();
                try { await HarnessTests.Aborted(request.Signal); } catch (OperationCanceledException) { }
                return Answer("never");
            }));
            var (harness, root) = await Open(setup, await open());
            await (await root.SubmitAsync(SubmissionDraft.Input("ann: hi", requestId: "r1", entry: Mention("hi")), Context)).WaitAsync(Context);
            await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            await reached.Task;
            await root.SubmitAsync(SubmissionDraft.Input("s", WhenBusy.Steer, entry: new InputEntry("p2.input")), Context);
            await root.SubmitAsync(SubmissionDraft.Input("f", WhenBusy.FollowUp, entry: Mention("f")), Context);
            var rejected = await Settle(() => root.SubmitAsync(SubmissionDraft.Input("x", entry: new InputEntry("pi.custom")), Context));
            await Close(harness);
            return Obj(("rejected", rejected));
        }, async open =>
        {
            var setup = Chat();
            setup.Faux.SetResponses(Answer("first again"), Answer("for s"), Answer("for f"), Answer("spare"));
            var (harness, _) = await Open(setup, await open());
            harness.Resume();
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return null;
        }),
        new("compaction-failure", ["j"], async open =>
        {
            var chat = new CompactingChat(2000);
            var setup = chat.Setup;
            JsonObjectValue Failure(string message) => FauxAssistantMessage("", stopReason: "error", errorMessage: message, timestamp: T);
            var (harness, root) = await Open(setup, await open());
            async Task<SubmissionRecord> Turn(string user, string? reply)
            {
                if (reply is not null) CompactingChat.Push(chat.Agent, () => Task.FromResult(Answer(reply)));
                return await (await root.SubmitAsync(SubmissionDraft.Input(user), Context)).WaitAsync(Context);
            }
            foreach (var n in new[] { 1, 2, 3 }) await Turn(Tokens($"u{n}", 100), Tokens($"a{n}", 100));
            setup.Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 500, KeepRecentTokens = 150, BackgroundTokens = 1000 };
            // As capture-compaction.mjs orders a background compaction: the answer waits until the summarization request is sent, and the
            // summarization fails only once the turn has ended.
            var reached = new HarnessTests.Deferred();
            var held = new HarnessTests.Deferred();
            CompactingChat.Push(chat.Summaries, async () =>
            {
                reached.Resolve();
                await held.Task;
                return Failure("bad request");
            });
            CompactingChat.Push(chat.Agent, async () =>
            {
                await reached.Task;
                return Answer(Tokens("a4", 100));
            });
            var background = await Turn(Tokens("u4", 100), null);
            held.Resolve();
            await HarnessTests.Eventually(async () => (await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))?.ContainsKey("compactionFailure") == true);
            await harness.WaitForIdleAsync(Context);
            setup.Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 500, KeepRecentTokens = 150, BackgroundTokens = 0 };
            CompactingChat.Push(chat.Summaries, () => Task.FromResult(Failure("still bad")));
            var blocking = await Turn(Tokens("u5", 900), null);
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return Obj(("background", background.ToJson()), ("blocking", blocking.ToJson()),
                ("unusedAgentReplies", CompactingChat.Count(chat.Agent)), ("unusedSummaries", CompactingChat.Count(chat.Summaries)));
        }),
        new("display-only-write", ["i"], async open =>
        {
            var setup = Chat();
            var reached = new HarnessTests.Deferred();
            var held = new HarnessTests.Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                reached.Resolve();
                await held.Task;
                return Answer("first");
            }), Answer("second"));
            var (harness, root) = await Open(setup, await open());
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            await reached.Task;
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("f"), Context);
            var model = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft
            {
                Kind = "note", Model = JsonValue.Array(Obj(("role", "user"), ("content", "m"), ("timestamp", T))),
            }), Context);
            var user = (await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 10, null, Context)).Items.First(entry => entry.Kind == "pi.user");
            var edit = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "redact", Edits = [ContextEdit.Omit(user.Id)] }), Context);
            var note = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "note", Data = "n" }), Context);
            var noteAtOnce = await note.StatusAsync(Context);
            await root.ResetAsync("handoff", Context);
            var chat = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "chat.message", Data = "hi" }), Context);
            var chatBehindReset = await chat.StatusAsync(Context);
            var inbox = (JsonArrayValue)(await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Context))!["items"];
            held.Resolve();
            foreach (var submission in new[] { input, followUp, model, edit, chat }) await submission.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return Obj(("noteAtOnce", noteAtOnce.ToJson()), ("chatBehindReset", chatBehindReset.ToJson()),
                ("inbox", JsonValue.Array(inbox.Cast<JsonObjectValue>().Select(item => (JsonValue)JsonValue.Array(item["id"], item["mode"])))));
        }),
        new("session-failed", ["b"], async open =>
        {
            var setup = Chat();
            setup.Faux.SetResponses(Answer("one answered"), Answer("never asked"));
            var storage = new FailNextStorage(await open());
            var reports = new SyncList<JsonValue>();
            var (harness, root) = await Open(setup, storage, error => reports.Add(Described(error)));
            await (await root.SubmitAsync(SubmissionDraft.Input("one"), Context)).WaitAsync(Context);
            storage.FailNext = new Exception("disk gone");
            var failed = await Settle(() => root.SubmitAsync(SubmissionDraft.Input("two"), Context));
            var later = await Settle(() => root.SubmitAsync(SubmissionDraft.Input("three"), Context));
            var idle = await Settle(() => harness.WaitForIdleAsync(Context));
            var closed = await harness.Closed;
            await Close(harness);
            var closedJson = closed.Error is null
                ? Obj(("reason", closed.Reason == SessionEndReason.Failed ? "failed" : "closed"))
                : Obj(("reason", closed.Reason == SessionEndReason.Failed ? "failed" : "closed"), ("error", Described(closed.Error)));
            // See the file header: the capture's close wrapper lets its waitForIdle() resolve; the library's own semantics reject it.
            Program.Equal("SessionFailed", S(At(idle, "error", "name")), "idle after the failure");
            return Obj(("failed", failed), ("later", later), ("idle", idle), ("closed", closedJson), ("reports", JsonValue.Array(reports.Snapshot())));
        }, UnobservedFields: ["idle"]),
        new("nested-tools", ["a"], async open =>
        {
            var setup = Chat();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            // See the file header: the two concurrent echo calls interleave as on Node's event loop.
            var echo = TextTool("echo", "Echoes its text", async (args, api, callContext) =>
            {
                if (Text(args) == "a") await SlotStatus(api, "c1/named", "running", callContext);
                if (Text(args) == "b") await SlotStatus(api, "c1/1", "done", callContext);
                return Result([FauxText($"echo {Text(args)}")]);
            }, tool => tool with { Callers = [ToolCaller.Tools] });
            var counter = Define.Tool(new ToolRegistration
            {
                Name = "counter",
                Description = "Counts things",
                Parameters = ToolRegistration.EmptyParameters,
                StructuredOutputSchema = Count,
                Execute = (_, _, _) => Task.FromResult(Result([FauxText("3 things")]) with { StructuredOutput = Obj(("count", 3)) }),
            });
            var received = new SyncList<NestedToolExecutionResult>();
            var batch = Define.Tool(new ToolRegistration
            {
                Name = "batch",
                Description = "Calls other tools",
                Parameters = ToolRegistration.EmptyParameters,
                Execute = async (_, api, callContext) =>
                {
                    foreach (var result in await Task.WhenAll(api.ExecuteToolAsync("echo", Obj(("text", "a")), callContext),
                        api.ExecuteToolAsync("echo", Obj(("text", "b")), callContext, new NestedCallOptions { Key = "named" })))
                        received.Add(result);
                    received.Add(await api.ExecuteToolAsync("counter", JsonObjectValue.Empty, callContext));
                    received.Add(await api.ExecuteToolAsync("missing", JsonObjectValue.Empty, callContext));
                    received.Add(await api.ExecuteToolAsync("echo", Obj(("text", 5)), callContext));
                    return Result([FauxText(JsonValue.Array(received.Snapshot().Select(NestedResult)).ToJson())]);
                },
            });
            Install(setup, "tools", [echo, counter, batch], [Define.Hook(HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = async (hookCall, api, hookContext) =>
                {
                    if (hookCall["arguments"] is JsonObjectValue { } arguments && Text(arguments) == "b") await SlotStatus(api, "c1/1", "running", hookContext);
                    return null;
                },
            })]);
            setup.Faux.SetResponses(Call("batch"), Answer("done"));
            var (harness, root) = await Open(setup, await open());
            var agent = await root.AgentAsync(Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return Obj(("offered", Names(agent.Tools)), ("callable", Names(agent.Callable)),
                ("received", JsonValue.Array(received.Snapshot().Select(result => (JsonValue)result.ToJson()))));
        }),
        new("structured-output", ["a"], async open =>
        {
            var setup = Chat();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            ToolRegistration Returning(string name, ToolExecutionResult value, JsonObjectValue? schema = null) => Define.Tool(new ToolRegistration
            {
                Name = name, Description = name, Parameters = ToolRegistration.EmptyParameters, StructuredOutputSchema = schema,
                Execute = (_, _, _) => Task.FromResult(value),
            });
            var received = new SyncList<NestedToolExecutionResult>();
            Install(setup, "tools", [
                Returning("counter", Result([FauxText("3 things")]) with { StructuredOutput = Obj(("count", 3)) }, Count),
                Returning("wrong", new ToolExecutionResult { StructuredOutput = Obj(("count", "three")) }, Count),
                Returning("picture", Result([Obj(("type", "image"), ("data", "AAAA"), ("mimeType", "image/png"))])),
                Returning("bad", Result([FauxText("bad")]) with { IsError = true }),
                Returning("failing", Result([FauxText("failed")]) with { StructuredOutput = Obj(("count", 0)), IsError = true }, Count),
                Define.Tool(new ToolRegistration
                {
                    Name = "probe",
                    Description = "Calls the others",
                    Parameters = ToolRegistration.EmptyParameters,
                    Execute = async (_, api, callContext) =>
                    {
                        foreach (var name in new[] { "picture", "bad", "wrong", "failing", "counter" })
                            received.Add(await api.ExecuteToolAsync(name, JsonObjectValue.Empty, callContext));
                        return Result([FauxText(JsonValue.Array(received.Snapshot().Select(NestedResult)).ToJson())]);
                    },
                }),
            ]);
            setup.Faux.SetResponses(Calls(("counter", JsonObjectValue.Empty, "c1"), ("wrong", JsonObjectValue.Empty, "c2")), Call("probe", "c3"),
                Answer("done"));
            var reports = new SyncList<JsonValue>();
            var (harness, root) = await Open(setup, await open(), error => reports.Add(Described(error)));
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return Obj(("received", JsonValue.Array(received.Snapshot().Select(result => (JsonValue)result.ToJson()))),
                ("reports", JsonValue.Array(reports.Snapshot())));
        }),
        new("deferred-tool-search", ["a", "k"], null),
        new("read-image", ["e"], async open =>
        {
            var directory = Program.Temp("compat-read-image");
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, "pixel.png"), PixelPng);
                await File.WriteAllBytesAsync(Path.Combine(directory, "pixel.bmp"), PixelBmp());
                var env = new PiSharp.Durable.Tools.Env.LocalExecutionEnv(directory);
                var setup = Chat();
                setup.ToolExecution = ToolExecutionMode.Sequential;
                Install(setup, "coding", [PiSharp.Durable.Tools.CodingTools.CreateReadTool()]);
                setup.Faux.SetResponses(Calls(("read", Obj(("path", "pixel.png")), "c1"), ("read", Obj(("path", "pixel.bmp")), "c2")), Answer("a red pixel"));
                var (harness, root) = await Open(setup, await open(), env: env);
                await (await root.SubmitAsync(SubmissionDraft.Input("look"), Context)).WaitAsync(Context);
                await harness.WaitForIdleAsync(Context);
                await Close(harness);
                return null;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }),
        new("nested-tools-restart", ["a", "c"], async open =>
        {
            var tools = new RestartTools(block: true);
            tools.Setup.Faux.SetResponses(Call("batch"));
            var (harness, root) = await Open(tools.Setup, await open());
            await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Task.WhenAll(tools.Running.Task, tools.Gated.Task);
            await Close(harness);
            return tools.Observed();
        }, async open =>
        {
            var tools = new RestartTools(block: false);
            tools.Setup.Faux.SetResponses(Answer("done"));
            var (harness, _) = await Open(tools.Setup, await open());
            harness.Resume();
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return tools.Observed();
        }),
        new("abandon-on-restart", ["c"], async open =>
        {
            var worker = new AbandonTools();
            worker.Setup.Faux.SetResponses(Call("work"));
            var (harness, root) = await Open(worker.Setup, await open());
            await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await worker.Started.Task;
            await Close(harness);
            return worker.Observed();
        }, async open =>
        {
            var worker = new AbandonTools();
            worker.Setup.Faux.SetResponses(Answer("done"));
            var (harness, _) = await Open(worker.Setup, await open());
            harness.Resume();
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return worker.Observed();
        }),
        new("wait-cycle", ["c"], async open =>
        {
            var setup = Chat();
            var ids = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskId>(StringComparer.Ordinal);
            var bMayWait = new HarnessTests.Deferred();
            static TaskState WaitOn(TaskId on) => NextTaskState.Waiting(Obj(("phase", "join")), [on], JoinPolicy.AllSettled);
            var a = Tasks.DefineTask(new TaskDefinition
            {
                Name = "compat.cycle-a",
                Version = 1,
                Initial = _ => Obj(("phase", "wait")),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => WaitOn(ids["b"]), ctx),
                    ["join"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(Obj(("a", "joined"))), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var b = Tasks.DefineTask(new TaskDefinition
            {
                Name = "compat.cycle-b",
                Version = 1,
                Initial = _ => Obj(("phase", "wait")),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = async (_, runtime, ctx) =>
                    {
                        await bMayWait.Task;
                        await runtime.CommitAsync((_, _) => WaitOn(ids["a"]), ctx);
                    },
                    ["join"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Completed(Obj(("b", "joined"))), ctx),
                },
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "tasks", Tasks = [a, b] }));
            var (harness, root) = await Open(setup, await open());
            await root.CommitAsync(async tx =>
            {
                ids["b"] = await tx.CreateTaskAsync(b, Obj(("name", "b")), new TaskOptions(TaskOwnership.Conversation));
                ids["a"] = await tx.CreateTaskAsync(a, Obj(("name", "a")), new TaskOptions(TaskOwnership.Conversation));
            }, Context);
            harness.Resume();
            await HarnessTests.Eventually(async () => (await harness.GetTaskAsync(ids["a"], Context))?.State.Status == DurableTaskStatus.Waiting);
            bMayWait.Resolve();
            var bRecord = await harness.WaitForTaskAsync(ids["b"], Context);
            var aRecord = await harness.WaitForTaskAsync(ids["a"], Context);
            await harness.WaitForIdleAsync(Context);
            await Close(harness);
            return Obj(("a", ((TerminalTaskState)aRecord.State).Outcome.ToJson()), ("b", ((TerminalTaskState)bRecord.State).Outcome.ToJson()));
        }),
    ];
}
