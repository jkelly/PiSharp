// Pi 6c2fd73b0c545a4034672cb60e34d53e035dd751 (MIT): packages/durable/test/harness-deferred-progress.test.ts, new in Pi v1.5.0 (sync area
// deferred-commits), with e5c6182ba's change to it. Deferred progress commits (Live.ProgressCommit): the partial answer and tool output reach
// watches at once and Storage at the next commit that reads or writes pi.live or within settings.progress.persistWithinMs. Crashes are
// simulated by failing the Harness: from that moment nothing more reaches Storage, as when the process dies; the reopened Harness resumes from
// what was stored. Verbatim apart from the adaptations in HarnessSupport.cs, ChatSupport.cs and SessionSupport.cs, and these:
// - Upstream's ControlledStorage extends MemoryStorage; here it decorates one, kept open for reopen (SessionSupport.cs).
// - `aborted(signal)` is Aborted(token); waitFor is Eventually; `harness.fail()` is the internal Session.Fail.
// - The memory partials come from SessionTests.ObserveCommits, upstream 6c2fd73b0's internal observeCommits; the replica reads
//   SubscribeCommits, 6c2fd73b0's name.
// - "leaves no aborted entry for a stream that crashed before its first store" expects no stored generation at all: e5c6182ba defers the
//   attempt too (durable@1.5.0 Changed#5), where 6c2fd73b0 stored `{ generation: { attempt: 1 } }`.
// The v1.5.0 blob (07116cc08) differs by later areas only: observers (followCommits, observeCommits({ advance, end })), vocabulary
// (change.kind) and pi-schema (obj({}) for Type.Object({})). Pi v1.5.0 sync (area restarts, b3a29f111): slowSetup retries at once, as a
// request a crash interrupted is a failed attempt; "converts what was stored ... and retries the charged attempt" is 6c2fd73b0's "... and
// resends" renamed; "never counts progress commits as progress of a task cut off again and again", which replaces 6c2fd73b0's "never
// counts partial commits ...", is ported. A crash here is Session.Fail, whose close writes nothing back, so the tasks it cut off count a
// restart as upstream's do.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using static ToolsTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessDeferredProgressCases() => DeferredProgressTests.Cases("harness.deferred-progress.");
}

/// <summary>The harness.deferred-progress.* cases (upstream harness-deferred-progress.test.ts).</summary>
internal static class DeferredProgressTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>A slow stream: 4 characters 80 times a second.</summary>
    private static ChatSetup SlowSetup(double persistWithinMs)
    {
        var setup = Setup(new FauxOptions { TokensPerSecond = 80, MinTokenSize = 4, MaxTokenSize = 4 });
        setup.Progress = new ProgressSettings { PersistWithinMs = persistWithinMs };
        // A request a crash interrupted is a failed attempt (#11139): retry at once.
        setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 5, BaseDelayMs = 1 };
        return setup;
    }

    private static readonly string Long = string.Concat(Enumerable.Repeat("streamed ", 400));

    private static ControlledStorage NewStorage() => new(new MemoryStorage()) { KeepOpenForReopen = true };

    /// <summary>A Harness opened over the storage and running tasks, and the partial texts memory adopted.</summary>
    private sealed record Opened(IHarness Harness, IConversation Root, List<string> Memory)
    {
        public string? LastMemory
        {
            get { lock (Memory) return Memory.Count == 0 ? null : Memory[^1]; }
        }

        public List<string> MemorySnapshot()
        {
            lock (Memory) return [.. Memory];
        }
    }

    /// <summary>Open (or reopen) a Harness over <paramref name="storage"/> and let it run tasks.</summary>
    private static async Task<Opened> Open(ControlledStorage storage, ChatSetup setup)
    {
        var opened = await OpenChat(storage.Reopen(), setup);
        opened.Harness.Resume();
        return new Opened(opened.Harness, opened.Root, MemoryPartials(opened.Harness));
    }

    /// <summary>The partial text of every pi.live revision memory adopted, the last one last.</summary>
    private static List<string> MemoryPartials(IHarness harness)
    {
        var texts = new List<string>();
        ObserveCommits(harness, (publication, _) =>
        {
            foreach (var change in DocumentChanges(publication))
            {
                if (change.Record.Kind != "pi.live") continue;
                if (Message(change.Value) is { } message)
                    lock (texts) texts.Add(PartialText(message) ?? "");
            }
        });
        return texts;
    }

    /// <summary>Fail the Harness now: nothing more is stored, held changes are lost with it.</summary>
    private static async Task Crash(IHarness harness)
    {
        // Fail() is internal; every Harness is a Session.
        ((Session)harness).Fail(new InvalidOperationException("crash"));
        await harness.Closed;
    }

    /// <summary>pi.live as Storage has it, read past the Session, also after a crash closed it.</summary>
    private static async Task<JsonObjectValue?> StoredLive(ControlledStorage storage, ConversationId conversationId)
    {
        storage.Reopen();
        var record = await storage.FindDocumentAsync(new DocumentAddress("pi.live", DocumentScope.Conversation(conversationId)), DocumentPoint.Current, Context);
        if (record is null) return null;
        return (await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, Context))?.Value;
    }

    private static JsonObjectValue? Field(JsonObjectValue? value, string key) =>
        value is not null && value.TryGetValue(key, out var field) ? field as JsonObjectValue : null;

    private static JsonObjectValue? Message(JsonObjectValue? live) => Field(Field(live, "generation"), "message");

    /// <summary>The text of a partial message's first text block.</summary>
    private static string? PartialText(JsonObjectValue? message)
    {
        if (message is null || !message.TryGetValue("content", out var content) || content is not JsonArrayValue blocks) return null;
        return blocks.OfType<JsonObjectValue>().FirstOrDefault(block => block["type"] is JsonStringValue { Value: "text" }) is { } found
            ? S(found["text"]) : null;
    }

    private static string? StoredText(JsonObjectValue? live) => Message(live) is { } message ? PartialText(message) ?? "" : null;

    /// <summary>Assistant entries: their text and stop reason.</summary>
    private static string Answers(IEnumerable<EntryRecord> entries) => JsonValue.Array(entries.Where(entry => entry.Kind == "pi.assistant").Select(entry =>
    {
        var message = (JsonObjectValue)entry.Model![0];
        return (JsonValue)Obj(("text", TextOf(message) is { } text ? text : JsonValue.Null),
            ("stopReason", message.TryGetValue("stopReason", out var reason) ? reason : JsonValue.Null));
    })).ToJson();

    private static string Answer(string text, string stopReason) => Obj(("text", text), ("stopReason", stopReason)).ToJson();

    private static async Task<SubmissionRecord> WaitFor(IHarness harness, SubmissionId id) => await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context);

    private static Task<ISubmission> Submit(IConversation conversation, string text) => conversation.SubmitAsync(SubmissionDraft.Input(text), Context);

    private static async Task<int> StoredLength(ControlledStorage storage, ConversationId id) => StoredText(await StoredLive(storage, id))?.Length ?? 0;

    /// <summary>
    /// A tool that writes a line every 20 ms until its invocation is cancelled on its first run. A later run waits for cancellation while
    /// <c>Stall</c> is set (<c>Stalled</c> resolves then), and otherwise writes one line and returns.
    /// </summary>
    private sealed class StreamingTool
    {
        private int runs;
        private readonly List<string> lines = [];

        public StreamingTool(ToolReplay replay)
        {
            Registration = Define.Tool(new ToolRegistration
            {
                Name = "work",
                Description = "work",
                Replay = replay,
                Execute = async (_, api, callContext) =>
                {
                    var run = Interlocked.Increment(ref runs);
                    var signal = callContext.CancellationToken;
                    if (run > 1 && Stall)
                    {
                        Stalled.Resolve();
                        await Aborted(signal);
                    }
                    if (run > 1)
                    {
                        api.Output($"rerun {run}\n");
                        return new ToolExecutionResult();
                    }
                    for (var line = 0; !signal.IsCancellationRequested; line++)
                    {
                        api.Output($"line {line}\n");
                        lock (lines) lines.Add($"line {line}\n");
                        await Task.Delay(20, CancellationToken.None);
                    }
                    await Aborted(signal);
                    return new ToolExecutionResult();
                },
            });
        }

        public ToolRegistration Registration { get; }
        public int Runs => Volatile.Read(ref runs);
        public string Lines
        {
            get { lock (lines) return string.Concat(lines); }
        }
        public volatile bool Stall;
        public HarnessTests.Deferred Stalled { get; } = new();
    }

    private static async Task<string?> StoredOutput(ControlledStorage storage, ConversationId conversationId)
    {
        var live = await StoredLive(storage, conversationId);
        if (live is null || !live.TryGetValue("tools", out var tools) || tools is not JsonArrayValue { Count: > 0 } slots) return null;
        return ((JsonObjectValue)slots[0]).TryGetValue("output", out var output) ? S(output) : null;
    }

    private static List<string> ToolTexts(IEnumerable<EntryRecord> entries) =>
        [.. Results(entries).Select(message => string.Join("|", ((JsonArrayValue)message["content"]).Select(item => (JsonObjectValue)item)
            .Select(item => S(item["type"]) == "text" ? S(item["text"]) : "")))];

    private static JsonObjectValue Call(string name, string id) => FauxAssistantMessage([FauxToolCall(name, JsonObjectValue.Empty, id)], stopReason: "toolUse");

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // ─── generation partials ──────────────────────────────────────────────────

        yield return Case(prefix + "hold-partials-in-memory-and-store-them-together-at-most-every-persistwithinms", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(400);
            setup.Faux.SetResponses(FauxAssistantMessage(Long[..640]));
            var opened = await Open(storage, setup);
            var from = storage.CommitCount;
            await (await Submit(opened.Root, "hi")).WaitAsync(Context);
            var writesLive = storage.Commits.Skip(from).Count(batch => batch.Any(write => write is DocumentChangeWrite));
            // About 2 s of streaming at 100 ms per partial: some 20 partials in memory, a store about every 400 ms.
            var memory = opened.MemorySnapshot().Count;
            Check(memory > 12, $"partials in memory: {memory}");
            Check(writesLive < memory / 1.5, $"stored commits that write documents: {writesLive} for {memory} partials");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "converts-what-was-stored-into-the-aborted-entry-after-a-crash-and-retries-the-charged-attempt", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(150);
            setup.Faux.SetResponses(FauxAssistantMessage(Long));
            var opened = await Open(storage, setup);
            // A replica fed by SubscribeCommits() alone, as a replication feed would be.
            JsonValue? replica = await StoredLive(storage, opened.Root.Id);
            var replicaGate = new Lock();
            opened.Harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                    if (change.Record.Kind == "pi.live")
                        lock (replicaGate) replica = DeltaApply.Apply(replica, change.Ops);
            });
            var submission = await Submit(opened.Root, "hi");
            await Eventually(async () => await StoredLength(storage, opened.Root.Id) > 40);
            var inMemory = opened.LastMemory!;
            await Crash(opened.Harness);
            var stored = StoredText(await StoredLive(storage, opened.Root.Id))!;
            // What Storage has is a state memory had, behind it by at most the window, and what the replica has.
            Check(opened.MemorySnapshot().Contains(stored), "memory had the stored partial");
            Check(inMemory.StartsWith(stored, StringComparison.Ordinal), "the stored partial is behind memory's");
            var storedLive = await StoredLive(storage, opened.Root.Id) ?? (JsonValue)JsonValue.Null;
            lock (replicaGate) Equal(storedLive.ToJson(), (replica ?? JsonValue.Null).ToJson(), "replica");

            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            opened = await Open(storage, setup);
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, submission.Id)).Status, "status");
            Equal($"[{Answer(stored, "aborted")},{Answer("answer", "stop")}]", Answers(await AllEntries(opened.Root)), "answers");
            SameJson("{}", await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context), "live");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "leaves-no-aborted-entry-for-a-stream-that-crashed-before-its-first-store", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(double.PositiveInfinity);
            setup.Faux.SetResponses(FauxAssistantMessage(Long));
            var opened = await Open(storage, setup);
            var submission = await Submit(opened.Root, "hi");
            await Eventually(() => (opened.LastMemory?.Length ?? 0) > 40);
            await Crash(opened.Harness);
            // The attempt is presentation, deferred like the partials: none of it was stored (e5c6182ba).
            Check(Field(await StoredLive(storage, opened.Root.Id), "generation") is null, "no stored generation");
            Equal(null, StoredText(await StoredLive(storage, opened.Root.Id)), "stored text");

            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            opened = await Open(storage, setup);
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, submission.Id)).Status, "status");
            Equal($"[{Answer("answer", "stop")}]", Answers(await AllEntries(opened.Root)), "answers");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "converts-a-stored-partial-once-through-two-crashes-before-and-after-the-conversion", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(150);
            setup.Faux.SetResponses(FauxAssistantMessage(Long));
            var opened = await Open(storage, setup);
            var submission = await Submit(opened.Root, "hi");
            await Eventually(async () => await StoredLength(storage, opened.Root.Id) > 40);
            await Crash(opened.Harness);
            var stored = StoredText(await StoredLive(storage, opened.Root.Id))!;

            // Crash again before anything ran: nothing was converted, so nothing is lost or doubled.
            var unresumed = await OpenChat(storage.Reopen(), setup);
            await Crash(unresumed.Harness);
            Equal(stored, StoredText(await StoredLive(storage, unresumed.Root.Id)), "stored after the second crash");

            // Crash again while the resend streams: its conversion is stored, its own partials only held.
            setup.Progress = new ProgressSettings { PersistWithinMs = double.PositiveInfinity };
            setup.Faux.SetResponses(FauxAssistantMessage(Long));
            opened = await Open(storage, setup);
            var resend = opened;
            await Eventually(() => (resend.LastMemory?.Length ?? 0) > 40);
            await Crash(opened.Harness);
            Equal(null, StoredText(await StoredLive(storage, opened.Root.Id)), "stored after the third crash");

            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            opened = await Open(storage, setup);
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, submission.Id)).Status, "status");
            Equal($"[{Answer(stored, "aborted")},{Answer("answer", "stop")}]", Answers(await AllEntries(opened.Root)), "answers");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stores-everything-held-at-a-clean-close", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(double.PositiveInfinity);
            setup.Faux.SetResponses(FauxAssistantMessage(Long));
            var opened = await Open(storage, setup);
            var submission = await Submit(opened.Root, "hi");
            await Eventually(() => (opened.LastMemory?.Length ?? 0) > 40);
            await opened.Harness.CloseAsync(Context);
            var atClose = opened.LastMemory!;
            Equal(atClose, StoredText(await StoredLive(storage, opened.Root.Id)), "stored at close");

            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            opened = await Open(storage, setup);
            await WaitFor(opened.Harness, submission.Id);
            Equal(Answer(atClose, "aborted"), JsonValue.Parse(Answers(await AllEntries(opened.Root))) is JsonArrayValue { Count: > 0 } all ? all[0].ToJson() : "",
                "first answer");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-polling-a-deferred-response-after-a-crash", async () =>
        {
            var storage = NewStorage();
            var setup = Setup(new FauxOptions { PollAfterMs = 60_000 });
            double now = 1_000;
            setup.Now = () => Volatile.Read(ref now);
            setup.Faux.SetResponses(FauxAssistantMessage("deferred answer"));
            var opened = await Open(storage, setup);
            setup.Stream = new ConversationStreamOptions { Deferred = true };
            var id = (await Submit(opened.Root, "hi")).Id;
            await Eventually(async () => Field(Field(await StoredLive(storage, opened.Root.Id), "generation"), "deferred") is not null);
            await Crash(opened.Harness);
            opened = await Open(storage, setup);
            Volatile.Write(ref now, 61_000);
            opened.Harness.Resume();
            var settled = await WaitFor(opened.Harness, id);
            if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input) throw new InvalidOperationException($"Unexpected {settled.Status}");
            var answer = await opened.Root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context);
            Equal("deferred answer", TextOf(answer?.Model?[0]), "answer");
            Equal(1, setup.Faux.State.DeferredFetchCount, "fetches");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── tool output ──────────────────────────────────────────────────────────

        // #11139: a generation's request is charged at once after a crash, so its partials never stream with a count; a replay-safe tool
        // reruns and streams its output with one.
        yield return Case(prefix + "never-counts-progress-commits-as-progress-of-a-task-cut-off-again-and-again", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(50);
            var runs = 0;
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "work",
                Description = "work",
                Replay = ToolReplay.Safe,
                Execute = async (_, api, callContext) =>
                {
                    Interlocked.Increment(ref runs);
                    var signal = callContext.CancellationToken;
                    for (var line = 0; !signal.IsCancellationRequested; line++)
                    {
                        api.Output($"run {Volatile.Read(ref runs)} line {line}\n");
                        await Task.Delay(20, CancellationToken.None);
                    }
                    await Aborted(signal);
                    return new ToolExecutionResult();
                },
            }));
            setup.Faux.SetResponses(Call("work", "c1"));
            var opened = await Open(storage, setup);
            await Submit(opened.Root, "go");
            for (var crashes = 1; crashes <= 2; crashes++)
            {
                // This run stored output of its own, a progress commit, before it was cut off.
                var prefixOfRun = $"run {crashes} ";
                var current = opened;
                await Eventually(async () => (await StoredOutput(storage, current.Root.Id))?.StartsWith(prefixOfRun, StringComparison.Ordinal) == true);
                await Crash(opened.Harness);
                opened = await Open(storage, setup);
                var tool = (await opened.Harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.tool" }, 1), Context)).Items[0];
                Equal(crashes, tool.Restarts, "restarts");
            }
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-a-tool-without-replay-that-crashed-with-the-output-stored-by-then", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(150);
            var tool = new StreamingTool(ToolReplay.Unsafe);
            AddTool(setup.Registry, tool.Registration);
            setup.Faux.SetResponses(Call("work", "c1"));
            var opened = await Open(storage, setup);
            var submission = await Submit(opened.Root, "go");
            await Eventually(async () => ((await StoredOutput(storage, opened.Root.Id))?.Length ?? 0) > 20);
            await Crash(opened.Harness);
            var stored = (await StoredOutput(storage, opened.Root.Id))!;
            Check(tool.Lines.StartsWith(stored, StringComparison.Ordinal), "the stored output is what the tool wrote by then");

            setup.Faux.SetResponses(FauxAssistantMessage([FauxText("done")]));
            opened = await Open(storage, setup);
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, submission.Id)).Status, "status");
            Equal(1, tool.Runs, "runs");
            Equal($"{stored}|<harness>\n[error] Tool work was interrupted and may have partially run\n</harness>",
                string.Join("\n---\n", ToolTexts(await AllEntries(opened.Root))), "results");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reruns-a-replay-safe-tool-after-a-crash-with-its-stored-progress-cleared-also-through-a-second-crash", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(150);
            var tool = new StreamingTool(ToolReplay.Safe);
            AddTool(setup.Registry, tool.Registration);
            setup.Faux.SetResponses(Call("work", "c1"));
            var opened = await Open(storage, setup);
            var submission = await Submit(opened.Root, "go");
            await Eventually(async () => ((await StoredOutput(storage, opened.Root.Id))?.Length ?? 0) > 20);
            await Crash(opened.Harness);

            // The rerun's first commit stores the cleared progress; it crashes again before writing any of its own.
            tool.Stall = true;
            opened = await Open(storage, setup);
            await tool.Stalled.Task;
            Equal(null, await StoredOutput(storage, opened.Root.Id), "output after the rerun started");
            await Crash(opened.Harness);
            Equal(null, await StoredOutput(storage, opened.Root.Id), "output after the second crash");

            tool.Stall = false;
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText("done")]));
            opened = await Open(storage, setup);
            Equal(SubmissionStatus.Done, (await WaitFor(opened.Harness, submission.Id)).Status, "status");
            Equal(3, tool.Runs, "runs");
            Equal("rerun 3\n", string.Join("\n---\n", ToolTexts(await AllEntries(opened.Root))), "results");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── compaction ───────────────────────────────────────────────────────────

        yield return Case(prefix + "reruns-a-summarization-cut-off-by-a-crash-from-its-checkpoint", async () =>
        {
            var storage = NewStorage();
            var setup = Setup();
            setup.Compaction = new CompactionSettings { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 150, BackgroundTokens = 0 };
            var summarizing = new HarnessTests.Deferred();
            var words = string.Concat(Enumerable.Repeat("word ", 100));
            setup.Faux.SetResponses([
                .. new[] { "a1", "a2", "a3" }.Select(label => (FauxStep)FauxAssistantMessage($"{label} {words}")),
                new FauxResponseFactory(async request =>
                {
                    summarizing.Resolve();
                    await Aborted(request.Signal);
                    throw new InvalidOperationException("unreachable");
                }),
            ]);
            var opened = await Open(storage, setup);
            foreach (var label in new[] { "u1", "u2", "u3" })
                await (await Submit(opened.Root, $"{label} {words}")).WaitAsync(Context);
            var id = await opened.Root.CompactAsync(null, Context);
            await summarizing.Task;
            await Crash(opened.Harness);

            setup.Faux.SetResponses(FauxAssistantMessage("SUMMARY"));
            opened = await Open(storage, setup);
            MatchJson("""{"status":"completed"}""", OutcomeJson(await opened.Harness.WaitForTaskAsync(id, Context)), "outcome");
            var reopened = opened;
            await Eventually(async () => (await AllEntries(reopened.Root)).Any(entry => entry.Kind == "pi.compaction"));
            SameJson("{}", await opened.Harness.SnapshotAsync(Live.LiveDoc, opened.Root.Id, Context), "live");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── reading pi.live in a commit ──────────────────────────────────────────

        yield return Case(prefix + "stores-the-held-partial-a-third-party-task-s-commit-read-with-what-it-derived", async () =>
        {
            var storage = NewStorage();
            var setup = SlowSetup(double.PositiveInfinity);
            var audit = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.audit",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal)
                {
                    ["run"] = async (task, runtime, taskContext) =>
                    {
                        await runtime.CommitAsync(async (tx, _) =>
                        {
                            var conversationId = new ConversationId((long)N(((JsonObjectValue)task.Record.Input)["conversationId"]));
                            var live = await tx.DocAsync(Live.LiveDoc, conversationId);
                            var text = PartialText(Field(live.Get("generation") as JsonObjectValue, "message")) ?? "";
                            await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "test.audit", Data = text });
                            return (TaskState?)NextTaskState.Completed(JsonValue.Null);
                        }, taskContext);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            AddTask(setup.Registry, audit);
            setup.Faux.SetResponses(FauxAssistantMessage(Long));
            var opened = await Open(storage, setup);
            var submission = await Submit(opened.Root, "hi");
            await Eventually(() => (opened.LastMemory?.Length ?? 0) > 40);
            var conversationId = opened.Root.Id;
            var auditId = await opened.Harness.CommitAsync(tx => tx.CreateTaskAsync(audit, Obj(("conversationId", conversationId.Value)),
                new TaskOptions(TaskOwnership.Conversation) { ConversationId = conversationId }), Context);
            await opened.Harness.WaitForTaskAsync(auditId, Context);
            var derived = S((await AllEntries(opened.Root)).First(entry => entry.Kind == "test.audit").Data!);
            Check(derived.Length > 40, $"derived {derived.Length} characters");
            // The read stored the partial it saw, in the same storage transaction as the entry.
            var batch = storage.Commits.First(writes => writes.Any(write => write is EntryWrite { Value.Kind: "test.audit" }));
            Check(batch.Any(write => write is DocumentChangeWrite), "the entry's batch stores a document change");
            await Crash(opened.Harness);
            Equal(derived, StoredText(await StoredLive(storage, conversationId)), "stored text");

            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            opened = await Open(storage, setup);
            await WaitFor(opened.Harness, submission.Id);
            Equal(Answer(derived, "aborted"), JsonValue.Parse(Answers(await AllEntries(opened.Root))) is JsonArrayValue { Count: > 0 } all ? all[0].ToJson() : "",
                "first answer");
            await opened.Harness.CloseAsync(Context);
        });
    }
}
