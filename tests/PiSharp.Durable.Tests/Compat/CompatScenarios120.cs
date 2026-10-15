// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): the scenarios of tools/DurableFixtures/scenarios-v1.2.0.mjs that the v1.2.0 sync
// areas have ported so far, run on PiSharp against Fixtures/v1.2.0/upstream-sessions. compat.* (CompatCases.cs) reads the v1.1.0 set for the
// scenarios both pins capture until area a switches it (Fixtures/v1.2.0/index.json, "switching"); these scenarios exist only at v1.2.0, so
// their cases read the v1.2.0 set now: read, raw, harness and written, as for the v1.1.0 scenarios, and observed (what calls returned or
// rejected with, observed.json). Keep each scenario in step with its upstream twin: same faux responses, same calls in the same order.
//
// Read only: nested-tools-restart (areas a and c) and abandon-on-restart (area c) are checked by read, raw and harness: their records carry
// abandonOnRestart and abortReason "restart", which area c ports. Their run and resume make tool calls whose pi.tool tasks are version 2 and
// whose nested calls and api.createTask() default need tool task v2, so area a adds the run (written) and resume cases; until then the
// harness.nested-tools-restart.* case ports the abandon-on-restart scenario's semantics on PiSharp's version 1 tool task.
//
// Not checked yet:
// - events.json (the agent events of the root conversation). Its snapshot carries pi.live's nestedTools, which arrives with area a (tool
//   task v2); area a adds the events check for every scenario.
// - session-failed's observed `idle`. capture.mjs's open() replaces harness.close with a wrapper that first waits a macrotask and stops the
//   event stream, and the Session's own fail() calls that replaced close: the capture seals the Harness a macrotask late, so its
//   waitForIdle() after the failure resolves (`{ "value": null }`). Without the wrapper it rejects with SessionFailed, as upstream's
//   harness-storage-failure.test.ts asserts ("ends everything with the first error ...: ... a wait that starts now"); PiSharp's Fail()
//   seals at once and the case asserts that rejection. Every other observed field is compared.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static ChatTests;
using static SessionTests;

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

    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage storage, Action<Exception>? onReport = null)
    {
        var harness = await Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings, Now = () => setup.Now(), OnReport = onReport,
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        return new OpenedChat(harness, root);
    }

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
            await harness.CloseAsync(Context);
            return Obj(("rejected", rejected));
        }, async open =>
        {
            var setup = Chat();
            setup.Faux.SetResponses(Answer("first again"), Answer("for s"), Answer("for f"), Answer("spare"));
            var (harness, _) = await Open(setup, await open());
            harness.Resume();
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
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
            await harness.CloseAsync(Context);
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
            await harness.CloseAsync(Context);
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
            await harness.CloseAsync(Context);
            var closedJson = closed.Error is null
                ? Obj(("reason", closed.Reason == SessionEndReason.Failed ? "failed" : "closed"))
                : Obj(("reason", closed.Reason == SessionEndReason.Failed ? "failed" : "closed"), ("error", Described(closed.Error)));
            // See the file header: the capture's close wrapper lets its waitForIdle() resolve; the library's own semantics reject it.
            Program.Equal("SessionFailed", S(At(idle, "error", "name")), "idle after the failure");
            return Obj(("failed", failed), ("later", later), ("idle", idle), ("closed", closedJson), ("reports", JsonValue.Array(reports.Snapshot())));
        }, UnobservedFields: ["idle"]),
        new("nested-tools-restart", ["a", "c"], null),
        new("abandon-on-restart", ["c"], null),
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
            await harness.CloseAsync(Context);
            return Obj(("a", ((TerminalTaskState)aRecord.State).Outcome.ToJson()), ("b", ((TerminalTaskState)bRecord.State).Outcome.ToJson()));
        }),
    ];
}
