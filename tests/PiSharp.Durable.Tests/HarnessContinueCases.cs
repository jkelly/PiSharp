// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-continue.test.ts (added in fb524dd2e), ported in the Pi
// v1.5.0 sync (area admission): SubmissionDraft.Continue(), a run without a new user message (#11043). describe("continue") is
// harness.continue.*, on MemoryStorage and a SQLite file as upstream; the store pass (StoreStorageNode.cs) reruns it as
// harness.continue.store.*, opening the file as a StoreStorage and running the MemoryStorage cases on a fresh StoreStorage, as areas sleep
// and tool-tasks do. Verbatim apart from the adaptations in ChatSupport.cs, HarnessSupport.cs and HarnessRestartFailureCases.cs, and these:
// - open's Proxy and kill() are KilledStorages.Killable (HarnessRestartFailureCases.cs), the killed storages closed after the case as
//   upstream's afterEach does; openNodeSqliteStorage is the openFile the group takes.
// - `answering(text, seen)` records each request's message roles in a SyncList; toMatchObject is ChatTests.MatchJson over a record's JSON.
// - `failure` is a response factory (upstream passes the function as the step), so each request gets its own message.
// - The tools' parameters are pi-schema's obj({}), the JSON Schema of an object without properties; `aborted(signal)` is Aborted(token).
// - `telemetry.startSpan({ name: "caller" }, ...)` is InMemoryTelemetryContext.StartSpanAsync("caller", ...), and `withTelemetry(caller,
//   context)` is Context.WithTelemetry(caller); carriers compare by their keys and values (upstream's toEqual on the carrier objects).
// - "starts the inputs queued behind a continue with nothing to answer" holds the continue's run at its model lookup (ModelsOverride's
//   BeforeGetModel) until the followUp is queued, which upstream's single-threaded loop guarantees (Pi v1.5.0 sync, area live-turns).
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Telemetry;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessContinueCases() =>
        ContinueTests.Cases("harness.continue.", () => new MemoryStorage(), async path => await SqliteStorage.OpenAsync(path));
}

/// <summary>The harness.continue.* cases (upstream harness-continue.test.ts).</summary>
internal static class ContinueTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-continue"), "session.sqlite");

    private sealed record Opened(IHarness Harness, IConversation Root, Action Kill);

    /// <summary>Opens a chat over the file at <paramref name="path"/> and starts scheduling; <c>Kill</c> simulates a crash (upstream
    /// <c>open</c>).</summary>
    private static async Task<Opened> Open(Func<string, Task<IStorage>> openFile, KilledStorages killed, string path, ChatSetup setup)
    {
        var (storage, kill) = killed.Killable(await openFile(path));
        var opened = await OpenChat(storage, setup);
        opened.Harness.Resume();
        return new Opened(opened.Harness, opened.Root, kill);
    }

    private static JsonObjectValue Failure() => FauxAssistantMessage([], stopReason: "error", errorMessage: "Invalid request");

    /// <summary>Upstream's <c>failure</c> as a step: a factory.</summary>
    private static FauxStep Failing() => FauxStep.From(_ => Failure());

    /// <summary>A response step that records the roles of the context it was asked with, then answers <paramref name="text"/> (upstream
    /// <c>answering</c>).</summary>
    private static FauxStep Answering(string text, SyncList<List<string?>> seen) => FauxStep.From(request =>
    {
        seen.Add([.. request.Messages.Select(RoleOf)]);
        return FauxAssistantMessage([FauxText(text)]);
    });

    private static string? RoleOf(JsonValue message) =>
        message is JsonObjectValue value && value.TryGetValue("role", out var role) && role is JsonStringValue text ? text.Value : null;

    private static async Task<string?> AnswerText(IConversation root, SubmissionRecord settled)
    {
        if (settled.Status != SubmissionStatus.Done || settled.Type == SubmissionType.Write) throw new InvalidOperationException($"Unexpected {settled.Status}");
        var answer = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), Context);
        return TextOf(answer?.Model is { Count: > 0 } model ? model[0] : null);
    }

    private static async Task<int> UserEntries(IConversation root) => (await AllEntries(root)).Count(entry => entry.Kind == Entries.UserEntry.Kind);

    private static async Task<JsonObjectValue> LiveOf(IHarness harness, IConversation conversation) =>
        await harness.SnapshotAsync(Live.LiveDoc, conversation.Id, Context) ?? JsonObjectValue.Empty;

    private static JsonValue? Field(JsonObjectValue? value, string key) => value is not null && value.TryGetValue(key, out var item) ? item : null;

    private static string? RunFailureReason(JsonObjectValue live) =>
        Field(Field(live, "runFailure") as JsonObjectValue, "reason") is JsonStringValue reason ? reason.Value : null;

    private static async Task<JsonArrayValue> InboxItems(IHarness harness, IConversation conversation) =>
        Field(await harness.SnapshotAsync(Inbox.InboxDoc, conversation.Id, Context), "items") as JsonArrayValue ?? JsonValue.Array([]);

    private static Task<ISubmission> Continue(IConversation conversation, string? requestId = null) =>
        conversation.SubmitAsync(SubmissionDraft.Continue(requestId), Context);

    private static string? Str(RecordedTelemetrySpan span, string name) =>
        span.Attributes[name] is { Type: TelemetryAttributeType.String } value ? value.GetString() : null;

    private static double? Num(RecordedTelemetrySpan span, string name) =>
        span.Attributes[name] is { Type: TelemetryAttributeType.Number } value ? value.GetNumber() : null;

    private static bool SameCarrier(IReadOnlyDictionary<string, string>? expected, IReadOnlyDictionary<string, string>? actual) =>
        expected is null ? actual is null : actual is not null && expected.Count == actual.Count &&
            expected.All(item => actual.TryGetValue(item.Key, out var value) && value == item.Value);

    /// <param name="prefix">The case ID prefix: harness.continue., or harness.continue.store. for the store pass (StoreStorageNode.cs).</param>
    /// <param name="memory">Upstream's <c>new MemoryStorage()</c>.</param>
    /// <param name="openFile">Upstream's openNodeSqliteStorage: opens the storage over the file at a path.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> memory, Func<string, Task<IStorage>> openFile)
    {
        // #11043
        yield return Case(prefix + "answers-a-run-that-failed-without-a-new-user-message", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<List<string?>>();
            setup.Faux.SetResponses(Failing(), Answering("recovered", seen));
            var (harness, root) = await OpenChat(memory(), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("Analyze this"), Context);
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await first.WaitAsync(Context)).ToJson(), "first");
            Equal("model_error", RunFailureReason(await LiveOf(harness, root)), "runFailure reason");

            var resumed = await Continue(root);
            Equal(SubmissionStatus.Placed, (await resumed.StatusAsync(Context)).Status, "placed");
            var settled = await resumed.WaitAsync(Context);
            Equal(SubmissionType.Continue, settled.Type, "type");
            Equal("recovered", await AnswerText(root, settled), "answer");
            Equal("user", seen.Snapshot()[^1][^1], "last role");
            // The failed input stays unanswered; no user entry was added.
            MatchJson("""{"status":"unanswered"}""", (await first.StatusAsync(Context)).ToJson(), "first after");
            Equal(1, await UserEntries(root), "user entries");
            Equal<JsonValue?>(null, Field(await LiveOf(harness, root), "runFailure"), "runFailure");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "answers-the-tool-results-of-a-round-aborted-midway", async () =>
        {
            var setup = Setup();
            var started = new Deferred();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "wait",
                Description = "Waits until aborted",
                Parameters = NoParameters,
                Execute = async (_, _, callContext) =>
                {
                    started.Resolve();
                    await Aborted(callContext.CancellationToken);
                    throw new InvalidOperationException("unreachable");
                },
            }));
            var seen = new SyncList<List<string?>>();
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("wait", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                Answering("after the abort", seen));
            var (harness, root) = await OpenChat(memory(), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            await root.AbortAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await first.WaitAsync(Context)).ToJson(), "first");
            var settled = await (await Continue(root)).WaitAsync(Context);
            Equal("after the abort", await AnswerText(root, settled), "answer");
            Equal("toolResult", seen.Snapshot()[^1][^1], "last role");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "skips-system-messages-when-deciding-there-is-something-to-answer", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<List<string?>>();
            setup.Faux.SetResponses(FauxAssistantMessage("first answer"), Failing(), Answering("resumed", seen));
            var (harness, root) = await OpenChat(memory(), setup);
            await root.ConfigureAsync(new AgentChange { Instructions = Patch<string>.Set("Be thorough.") }, Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("one"), Context)).WaitAsync(Context);
            // Changed instructions append a system delta after the next user message, before its request.
            await root.ConfigureAsync(new AgentChange { Instructions = Patch<string>.Set("Be brief.") }, Context);
            var second = await root.SubmitAsync(SubmissionDraft.Input("two"), Context);
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await second.WaitAsync(Context)).ToJson(), "second");
            var roles = (await root.ContextAsync(Context)).Messages.Select(RoleOf).ToList();
            Equal("system", roles[^1], "last role");
            var settled = await (await Continue(root)).WaitAsync(Context);
            Equal("resumed", await AnswerText(root, settled), "answer");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "is-rejected-while-busy-after-request-id-deduplication", async () =>
        {
            var setup = Setup();
            var busy = Unanswered();
            setup.Faux.SetResponses(Failing(), busy.Step);
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var resumed = await Continue(root, "resume-1");
            await busy.Reached;
            await ThrowsAsync<ConversationBusyException>(() => Continue(root), "busy continue");
            // The same request ID returns the existing submission instead of rejecting.
            Equal(resumed.Id, (await Continue(root, "resume-1")).Id, "deduplicated");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "ends-without-a-request-when-there-is-nothing-to-answer", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage("answered"));
            var (harness, root) = await OpenChat(memory(), setup);
            var empty = await (await Continue(root)).WaitAsync(Context);
            MatchJson("""{"type":"continue","status":"unanswered","reason":"nothing_to_answer"}""", empty.ToJson(), "empty");
            await (await root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).WaitAsync(Context);
            var calls = setup.Faux.State.CallCount;
            // A context ending in an answer has nothing to continue.
            var after = await (await Continue(root)).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"nothing_to_answer"}""", after.ToJson(), "after");
            Equal(calls, setup.Faux.State.CallCount, "calls");
            Equal("nothing_to_answer", RunFailureReason(await LiveOf(harness, root)), "runFailure reason");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "continues-an-answer-with-tool-calls-cut-off-at-its-output-limit-which-ends-in-their-missing-results", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<List<string?>>();
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText("calling"), FauxToolCall("echo", JsonObjectValue.Empty, "c1")], stopReason: "length"),
                Answering("resumed", seen));
            var (harness, root) = await OpenChat(memory(), setup);
            // The run answered: a cut-off answer is an answer, and its calls never ran.
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "first");
            var settled = await (await Continue(root)).WaitAsync(Context);
            Equal("resumed", await AnswerText(root, settled), "answer");
            Seq(["assistant", "toolResult"], seen.Snapshot()[^1].TakeLast(2), "last roles");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "does-not-continue-a-text-answer-cut-off-at-its-output-limit", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText("cut")], stopReason: "length"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("write a lot"), Context)).WaitAsync(Context);
            var settled = await (await Continue(root)).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"nothing_to_answer"}""", settled.ToJson(), "settled");
            Equal(1, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return KilledStorages.Case(prefix + "answers-a-run-interrupted-by-a-host-that-died-while-the-model-answered", async killed =>
        {
            var path = SqlitePath();
            var first = Setup(new FauxOptions { TokensPerSecond = 20, MinTokenSize = 1, MaxTokenSize = 1 });
            first.Faux.SetResponses(FauxAssistantMessage(new string('z', 400)));
            first.Progress = new ProgressSettings { PersistWithinMs = 0 };
            var opened = await Open(openFile, killed, path, first);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).Id;
            var crashing = opened;
            await Eventually(async () => Field(Field(await LiveOf(crashing.Harness, crashing.Root), "generation") as JsonObjectValue, "message") is not null);
            await Task.Delay(50);
            opened.Kill();

            var setup = Setup();
            setup.Retry = new RetrySettings { Enabled = false };
            var seen = new SyncList<List<string?>>();
            setup.Faux.SetResponses(Answering("after the crash", seen));
            opened = await Open(openFile, killed, path, setup);
            var interrupted = await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"interrupted"}""", interrupted.ToJson(), "interrupted");
            var settled = await (await Continue(opened.Root)).WaitAsync(Context);
            Equal("after the crash", await AnswerText(opened.Root, settled), "answer");
            // The aborted partial is not model context: the request ends in the user message.
            Equal("user", seen.Snapshot()[^1][^1], "last role");
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "places-a-reset-a-failed-run-kept-and-finds-nothing-to-answer-in-the-context-it-starts", async () =>
        {
            var setup = Setup();
            var gate = new Deferred();
            var reached = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                reached.Resolve();
                await gate.Task;
                return Failure();
            }));
            var (harness, root) = await OpenChat(memory(), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
            await reached.Task;
            await root.ResetAsync(null, Context);
            gate.Resolve();
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await first.WaitAsync(Context)).ToJson(), "first");
            Equal(1, (await InboxItems(harness, root)).Count, "inbox items");

            var settled = await (await Continue(root)).WaitAsync(Context);
            MatchJson("""{"status":"unanswered","reason":"nothing_to_answer"}""", settled.ToJson(), "settled");
            SameJson("[]", await InboxItems(harness, root), "inbox after");
            SameJson("[]", JsonValue.Array((await root.ContextAsync(Context)).Messages.Where(message => RoleOf(message) != "system")), "messages");
            Equal(1, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "starts-the-inputs-queued-behind-a-continue-with-nothing-to-answer-under-afterfailedrun-continue", async () =>
        {
            var setup = Setup();
            setup.AfterFailedRun = AfterFailedRun.Continue;
            setup.Faux.SetResponses(FauxAssistantMessage("answered"), FauxAssistantMessage("second answered"));
            // The continue's run task waits at its model lookup until the followUp is queued: upstream's single-threaded loop cannot end
            // that run between the two submits, PiSharp's thread-pool scheduler can (the followUp is then placed, not queued).
            using var followUpQueued = new ManualResetEventSlim(true);
            setup.Models = new ModelsOverride(setup.Models) { BeforeGetModel = () => followUpQueued.Wait(TimeSpan.FromSeconds(30)) };
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("one"), Context)).WaitAsync(Context);
            followUpQueued.Reset();
            var resumed = await Continue(root);
            var queued = await root.SubmitAsync(SubmissionDraft.Input("two", WhenBusy.FollowUp), Context);
            var status = (await queued.StatusAsync(Context)).Status;
            followUpQueued.Set();
            Equal(SubmissionStatus.Queued, status, "queued");
            MatchJson("""{"status":"unanswered","reason":"nothing_to_answer"}""", (await resumed.WaitAsync(Context)).ToJson(), "resumed");
            var settled = await queued.WaitAsync(Context);
            Equal("second answered", await AnswerText(root, settled), "answer");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "is-submitted-by-a-tool-through-the-handle-of-a-conversation-it-owns-and-rejected-there-while-busy", async () =>
        {
            var setup = Setup();
            var busy = new Deferred();
            var release = new Deferred();
            object? busyOutcome = null;
            string? firstOutcome = null, resumedOutcome = null;
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "delegate",
                Description = "Delegates",
                Parameters = NoParameters,
                Execute = async (_, api, callContext) =>
                {
                    var child = await api.CommitAsync(async tx =>
                    {
                        var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                        await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Model = new ModelRef("faux", "faux-1") });
                        return created.Id;
                    }, callContext);
                    var handle = (await api.ConversationAsync(child, callContext))!;
                    var first = await (await handle.SubmitAsync(SubmissionDraft.Input("work"), callContext)).WaitAsync(callContext);
                    firstOutcome = WireName(first.Status);
                    var resumed = await handle.SubmitAsync(SubmissionDraft.Continue(), callContext);
                    await busy.Task;
                    try { busyOutcome = await handle.SubmitAsync(SubmissionDraft.Continue(), callContext); }
                    catch (Exception error) { busyOutcome = error; }
                    release.Resolve();
                    var settled = await resumed.WaitAsync(callContext);
                    resumedOutcome = WireName(settled.Status);
                    return new ToolExecutionResult { Output = JsonValue.Array(FauxText(resumedOutcome)) };
                },
            }));
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("delegate", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                Failing(),
                new FauxResponseFactory(async _ =>
                {
                    busy.Resolve();
                    await release.Task;
                    return FauxAssistantMessage("child recovered");
                }),
                FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(memory(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal("unanswered", firstOutcome, "first");
            Equal("done", resumedOutcome, "resumed");
            Check(busyOutcome is ConversationBusyException, $"busy: {busyOutcome}");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "starts-a-root-trace-linked-to-its-caller-as-an-input-does", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            setup.Faux.SetResponses(Failing(), FauxAssistantMessage("recovered"));
            var (harness, root) = await OpenChat(memory(), setup, telemetry: telemetry);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var id = await telemetry.StartSpanAsync("caller", async caller =>
            {
                var callContext = Context.WithTelemetry(caller);
                var resumed = await root.SubmitAsync(SubmissionDraft.Continue(), callContext);
                await resumed.WaitAsync(callContext);
                return resumed.Id;
            });
            await harness.WaitForIdleAsync(Context);
            await Eventually(() => telemetry.GetSpans().All(span => span.Settled));
            var spans = telemetry.GetSpans();
            var callerSpan = spans.First(span => span.Name == "caller");
            var submit = spans.First(span => span.Name == "pi.durable.submit" && Num(span, "submission.id") == id.Value);
            Check(submit.ParentId is null && Str(submit, "result") == "placed", $"submit: parent {submit.ParentId}, result {Str(submit, "result")}");
            Check(submit.Links is [var link] && SameCarrier(callerSpan.Context, link.Context) && link.Attributes.Count == 0, "submit links");
            Check(SameCarrier(submit.Context, (await (await harness.SubmissionAsync(id, Context))!.StatusAsync(Context)).Trace), "record trace");
            Check(spans.Any(span => span.Name == "pi.durable.phase" && Str(span, "phase") == "prepare" && SameCarrier(submit.Context, span.ParentContext)),
                "a prepare phase in the continue's trace");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "places-inputs-a-failed-run-left-queued-first-and-they-join-its-run", async () =>
        {
            var setup = Setup();
            var gate = new Deferred();
            var reached = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                reached.Resolve();
                await gate.Task;
                return Failure();
            }), FauxAssistantMessage("both handled"));
            var (harness, root) = await OpenChat(memory(), setup);
            await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
            await reached.Task;
            var queued = await root.SubmitAsync(SubmissionDraft.Input("second", WhenBusy.FollowUp), Context);
            gate.Resolve();
            await harness.WaitForIdleAsync(Context);
            Equal(SubmissionStatus.Queued, (await queued.StatusAsync(Context)).Status, "queued");

            var resumed = await Continue(root);
            var live = await LiveOf(harness, root);
            SameJson($"[{queued.Id.Value},{resumed.Id.Value}]", Field(Field(live, "run") as JsonObjectValue, "inputs"), "run inputs");
            var byQueued = await queued.WaitAsync(Context);
            var byContinue = await resumed.WaitAsync(Context);
            Equal("both handled", await AnswerText(root, byContinue), "answer");
            MatchJson($$"""{"status":"done","answer":{{byContinue.Answer!.Value.Value}}}""", byQueued.ToJson(), "queued settled");
            await harness.CloseAsync(Context);
        });
    }

    private static string WireName(SubmissionStatus status) => status.ToString().ToLowerInvariant();
}
