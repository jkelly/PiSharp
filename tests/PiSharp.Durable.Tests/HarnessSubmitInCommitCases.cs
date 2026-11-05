// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-submit-in-commit.test.ts (added in 6a2825113 and
// cc9c52310), ported in the Pi v1.5.0 sync (area admission): admission for hosts that must not start runs, WhenIdle.Reject, and
// SubmitAsync(tx, draft) inside the caller's commit (#11149). describe("whenIdle reject") is harness.submit-in-commit.when-idle-reject.* and
// describe("submit inside the caller's commit") harness.submit-in-commit.inside-the-caller-s-commit.*, on ControlledStorage over
// MemoryStorage and on MemoryStorage as upstream. Upstream's vitest.store.config.ts does not list the file, so it has no store pass.
// Verbatim apart from the adaptations in ChatSupport.cs, HarnessSupport.cs and SessionSupport.cs, and these:
// - `defineDoc<{ ids: number[] }>({ checkpointWhen: () => false, ... })` is Documents.DefineConversationDoc with the same kind, version,
//   fork and initial value; `storage.commits.length` is ControlledStorage.CommitCount and `storage.commits.at(-1)` LastCommit.
// - `expect(promise).rejects.toBeInstanceOf(X)` is ThrowsAsync<X>; toMatchObject is ChatTests.MatchJson over a record's JSON;
//   `rejects.toThrow()` without a message is any exception.
// - `storage.reopen()` needs ControlledStorage.KeepOpenForReopen, set before the Session closes the storage (SessionSupport.cs).
// - `new Proxy(tx, {})` is a DispatchProxy over ITx that forwards every call, and `{ doc: tx.doc.bind(tx) } as unknown as Tx` one that
//   forwards only DocAsync: neither is a transaction of the Session (Transaction.OwnedBy's type test is upstream's brand check).
// - openTraced is OpenChat with HarnessOptions.Telemetry; `telemetry.startSpan({ name: "caller" }, ...)` is
//   InMemoryTelemetryContext.StartSpanAsync("caller", ...), and carriers compare by their keys and values.
// - A tool's `callContext.abortSignal.addEventListener("abort", ...)` is a registration on its cancellation token; the tools' parameters are
//   pi-schema's obj({}), the JSON Schema of an object without properties; defineTask's phase and abort are a TaskDefinition's.
using System.Collections.Immutable;
using System.Reflection;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Telemetry;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessSubmitInCommitCases() => SubmitInCommitTests.Cases("harness.submit-in-commit.");
}

/// <summary>The harness.submit-in-commit.* cases (upstream harness-submit-in-commit.test.ts).</summary>
internal static class SubmitInCommitTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly ModelRef Model = new("faux", "faux-1");

    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    private static JsonObjectValue Failure() => FauxAssistantMessage([], stopReason: "error", errorMessage: "Invalid request");

    /// <summary>A host's own conversation document (upstream <c>Sent</c>).</summary>
    private static readonly ConversationDocToken Sent = Documents.DefineConversationDoc("test.sent", 1, DocumentFork.Initial, () => O("""{"ids":[]}"""),
        checkpointWhen: (_, _, _) => false);

    private sealed record Busy(IHarness Harness, IConversation Root, ControlledStorage Storage, ISubmission First);

    /// <summary>Opens a chat whose run hangs until aborted, and waits until it works on its first input (upstream <c>openBusy</c>).</summary>
    private static async Task<Busy> OpenBusy(ControlledStorage? storage = null)
    {
        storage ??= new ControlledStorage(new MemoryStorage());
        var setup = Setup();
        var busy = Unanswered();
        setup.Faux.SetResponses(busy.Step);
        var (harness, root) = await OpenChat(storage, setup);
        var first = await root.SubmitAsync(SubmissionDraft.Input("work"), Context);
        await busy.Reached;
        return new Busy(harness, root, storage, first);
    }

    private sealed record Hold(Deferred Reached, Deferred Release, JsonObjectValue Call);

    /// <summary>Installs tool <c>hold</c>, which reports its call and waits for <c>Release</c>; <c>Call</c> calls it (upstream
    /// <c>addHold</c>).</summary>
    private static Hold AddHold(ChatSetup setup)
    {
        var reached = new Deferred();
        var release = new Deferred();
        AddTool(setup.Registry, Define.Tool(new ToolRegistration
        {
            Name = "hold",
            Description = "Holds until released",
            Parameters = NoParameters,
            Execute = async (_, _, _) =>
            {
                reached.Resolve();
                await release.Task;
                return new ToolExecutionResult { Output = JsonValue.Array([]) };
            },
        }));
        return new Hold(reached, release, FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"));
    }

    /// <summary>Settled status of submission <paramref name="id"/>, polled without asking for progress (<c>WaitAsync</c> would; upstream
    /// <c>settledStatus</c>).</summary>
    private static async Task<SubmissionRecord> SettledStatus(IHarness harness, SubmissionId id)
    {
        var submission = (await harness.SubmissionAsync(id, Context))!;
        await Eventually(async () => (await submission.StatusAsync(Context)).Status is SubmissionStatus.Done or SubmissionStatus.Unanswered);
        return await submission.StatusAsync(Context);
    }

    private static async Task<JsonObjectValue> LiveOf(IHarness harness, ConversationId id) =>
        await harness.SnapshotAsync(Live.LiveDoc, id, Context) ?? JsonObjectValue.Empty;

    private static JsonValue? Field(JsonObjectValue? value, string key) => value is not null && value.TryGetValue(key, out var item) ? item : null;

    private static async Task<JsonValue?> RunOf(IHarness harness, ConversationId id) => Field(await LiveOf(harness, id), "run");

    private static async Task<JsonArrayValue?> InboxItems(IHarness harness, ConversationId id) =>
        Field(await harness.SnapshotAsync(Inbox.InboxDoc, id, Context), "items") as JsonArrayValue;

    private static IEnumerable<long> ItemIds(JsonArrayValue? items) =>
        items?.OfType<JsonObjectValue>().Select(item => (long)((JsonNumberValue)item["id"]).Value) ?? [];

    private static string? Str(RecordedTelemetrySpan span, string name) =>
        span.Attributes[name] is { Type: TelemetryAttributeType.String } value ? value.GetString() : null;

    private static double? Num(RecordedTelemetrySpan span, string name) =>
        span.Attributes[name] is { Type: TelemetryAttributeType.Number } value ? value.GetNumber() : null;

    private static bool SameCarrier(IReadOnlyDictionary<string, string>? expected, IReadOnlyDictionary<string, string>? actual) =>
        expected is null ? actual is null : actual is not null && expected.Count == actual.Count &&
            expected.All(item => actual.TryGetValue(item.Key, out var value) && value == item.Value);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        var whenIdle = prefix + "when-idle-reject.";

        // #11149
        yield return Case(whenIdle + "rejects-input-to-an-idle-conversation-without-writing", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await OpenChat(storage, Setup());
            var commits = storage.CommitCount;
            var rejected = root.SubmitAsync(SubmissionDraft.Input("hi", WhenBusy.Steer, whenIdle: WhenIdle.Reject), Context);
            var error = await ThrowsAsync<ConversationIdleException>(() => rejected, "rejected");
            Equal(root.Id, error.ConversationId, "conversationId");
            Equal(commits, storage.CommitCount, "commits");
            Equal<JsonValue?>(null, await RunOf(harness, root.Id), "run");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(whenIdle + "rejects-input-to-an-idle-conversation-that-keeps-queued-items-after-a-failed-run", async () =>
        {
            var setup = Setup();
            var failing = new Deferred();
            var requested = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                requested.Resolve();
                await failing.Task;
                return Failure();
            }));
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
            await requested.Task;
            var queued = await root.SubmitAsync(SubmissionDraft.Input("queued"), Context);
            failing.Resolve();
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await first.WaitAsync(Context)).ToJson(), "first");
            await harness.WaitForIdleAsync(Context);
            await ThrowsAsync<ConversationIdleException>(
                () => root.SubmitAsync(SubmissionDraft.Input("steer", WhenBusy.Steer, whenIdle: WhenIdle.Reject), Context), "rejected");
            MatchJson("""{"status":"queued"}""", (await queued.StatusAsync(Context)).ToJson(), "queued");
            Equal<JsonValue?>(null, await RunOf(harness, root.Id), "run");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(whenIdle + "steers-a-working-run", async () =>
        {
            var (harness, root, _, first) = await OpenBusy();
            var steer = await root.SubmitAsync(SubmissionDraft.Input("also this", WhenBusy.Steer, whenIdle: WhenIdle.Reject), Context);
            MatchJson("""{"type":"input","status":"queued"}""", (await steer.StatusAsync(Context)).ToJson(), "steer");
            SameJson($$"""[{"id":{{steer.Id.Value}},"mode":"steer","content":"also this"}]""", await InboxItems(harness, root.Id), "inbox");
            SameJson($"[{first.Id.Value}]", Field(await RunOf(harness, root.Id) as JsonObjectValue, "inputs"), "run inputs");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(whenIdle + "returns-the-submission-of-a-known-request-id-also-once-idle", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("work", requestId: "r1"), Context);
            await first.WaitAsync(Context);
            var draft = SubmissionDraft.Input("work", WhenBusy.Steer, "r1", whenIdle: WhenIdle.Reject);
            Equal(first.Id, (await root.SubmitAsync(draft, Context)).Id, "submit");
            // Deduplication runs before the idle rejection in the caller's commit too.
            Equal(first.Id, await harness.CommitAsync(tx => root.SubmitAsync(tx, draft), Context), "submit in commit");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(whenIdle + "rejects-through-a-tool-s-conversation-handle", async () =>
        {
            var setup = Setup();
            IConversation? other = null;
            Exception? rejection = null;
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "nudge",
                Description = "Steers another conversation",
                Parameters = NoParameters,
                Execute = async (_, api, callContext) =>
                {
                    var handle = (await api.ConversationAsync(other!.Id, callContext))!;
                    var draft = SubmissionDraft.Input("nudge", WhenBusy.Steer, whenIdle: WhenIdle.Reject);
                    try { await handle.SubmitAsync(draft, callContext); }
                    catch (Exception error) { rejection = error; }
                    return new ToolExecutionResult { Output = JsonValue.Array(FauxText("nudged")) };
                },
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("nudge", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("ok")]));
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless) { Agent = new AgentChange { Model = Model } },
                Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Check(rejection is ConversationIdleException, $"rejection: {rejection}");
            Equal<JsonValue?>(null, await RunOf(harness, other.Id), "other run");
            await harness.CloseAsync(Context);
        });

        var inCommit = prefix + "inside-the-caller-s-commit.";

        // #11149
        yield return Case(inCommit + "admits-with-the-host-s-own-check-and-writes-in-one-commit", async () =>
        {
            var (harness, root, storage, first) = await OpenBusy();
            var commits = storage.CommitCount;
            var id = await harness.CommitAsync(async tx =>
            {
                if (!(await tx.DocAsync(Live.LiveDoc, root.Id)).ContainsKey("run")) throw new InvalidOperationException("not working");
                // The host's own document write may come before the admission.
                var sent = await tx.DocAsync(Sent, root.Id);
                var admitted = await root.SubmitAsync(tx, SubmissionDraft.Input("steer", WhenBusy.Steer));
                sent.GetArray("ids").Add(admitted.ToJson());
                // The host's own table write comes after it.
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.sent", Data = Obj(("submission", admitted.ToJson())) });
                return admitted;
            }, Context);
            Equal(commits + 1, storage.CommitCount, "commits");
            var writes = storage.LastCommit;
            Check(writes.Any(write => write is SubmissionWrite { Value: var record } && record.Id == id), "submission write");
            Check(writes.Any(write => write is EntryWrite { Value.Kind: "app.sent" }), "entry write");
            SameJson($"[{id.Value}]", Field(await harness.SnapshotAsync(Sent, root.Id, Context), "ids"), "sent");
            SameJson($$"""[{"id":{{id.Value}},"mode":"steer","content":"steer"}]""", await InboxItems(harness, root.Id), "inbox");
            SameJson($"[{first.Id.Value}]", Field(await RunOf(harness, root.Id) as JsonObjectValue, "inputs"), "run inputs");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "is-rolled-back-with-the-commit-when-the-callback-throws-later", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await OpenChat(storage, Setup());
            var commits = storage.CommitCount;
            await RejectsAsync(() => harness.CommitAsync<int>(async tx =>
            {
                await root.SubmitAsync(tx, SubmissionDraft.Input("hi", requestId: "r1"));
                throw new InvalidOperationException("host check failed");
            }, Context), "host check failed", "failed");
            Equal(commits, storage.CommitCount, "commits");
            Equal<JsonValue?>(null, await RunOf(harness, root.Id), "run");
            Equal<SubmissionRecord?>(null, await harness.CommitAsync(tx => tx.GetSubmissionByRequestAsync(root.Id, "r1"), Context), "by request");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "leaves-no-submission-and-no-run-when-storage-fails-the-commit-also-after-reopening", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
            var setup = Setup();
            var (harness, root) = await OpenChat(storage, setup);
            var failure = new InvalidOperationException("disk full");
            storage.FailNextCommit(failure);
            try
            {
                await harness.CommitAsync(async tx =>
                {
                    await root.SubmitAsync(tx, SubmissionDraft.Input("hi", requestId: "r1"));
                    await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.sent" });
                    return 0;
                }, Context);
                throw new InvalidOperationException("failed: expected a rejection");
            }
            catch (Exception error) when (!error.Message.StartsWith("failed:", StringComparison.Ordinal))
            {
                Same(failure, error, "failed");
            }
            await harness.Closed;
            var reopened = await OpenChat(storage.Reopen(), setup);
            Equal(root.Id, reopened.Root.Id, "root");
            var (submission, tasks, entries) = await reopened.Harness.CommitAsync(async tx =>
            {
                var found = await tx.GetSubmissionByRequestAsync(root.Id, "r1");
                var taskPage = await tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 10);
                var entryPage = await tx.ScanEntriesAsync(new EntryQuery(root.Id), 10);
                return (found, taskPage.Items.Count(), entryPage.Items.Count());
            }, Context);
            Equal<SubmissionRecord?>(null, submission, "submission");
            Equal(0, tasks, "tasks");
            Equal(0, entries, "entries");
            Equal<JsonValue?>(null, await RunOf(reopened.Harness, root.Id), "run");
            await reopened.Harness.CloseAsync(Context);
            await storage.Inner.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "starts-the-run-of-an-idle-input-once-the-commit-is-stored", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText("answer")]));
            // No Resume(): the admission asks for progress itself.
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            var id = await harness.CommitAsync(tx => root.SubmitAsync(tx, SubmissionDraft.Input("hi")), Context);
            MatchJson("""{"type":"input","status":"done"}""", (await SettledStatus(harness, id)).ToJson(), "settled");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "queues-a-model-visible-write-while-busy-and-places-it-at-the-next-boundary", async () =>
        {
            var setup = Setup();
            var hold = AddHold(setup);
            setup.Faux.SetResponses(hold.Call, FauxAssistantMessage([FauxText("answer")]));
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("work"), Context);
            await hold.Reached.Task;
            var note = O("""{"role":"user","content":"a note the model sees","timestamp":1}""");
            var id = await harness.CommitAsync(tx => root.SubmitAsync(tx, SubmissionDraft.Write(new EntryDraft { Kind = "test.note", Model = JsonValue.Array(note) })),
                Context);
            Seq([id.Value], ItemIds(await InboxItems(harness, root.Id)), "inbox ids");
            hold.Release.Resolve();
            MatchJson("""{"status":"done"}""", (await first.WaitAsync(Context)).ToJson(), "first");
            MatchJson("""{"type":"write","status":"done","entry":"<number>"}""", (await SettledStatus(harness, id)).ToJson(), "settled");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "deduplicates-request-ids", async () =>
        {
            var (harness, root, storage, _) = await OpenBusy();
            var draft = SubmissionDraft.Input("steer", WhenBusy.Steer, "r1");
            var first = await harness.CommitAsync(tx => root.SubmitAsync(tx, draft), Context);
            var commits = storage.CommitCount;
            var again = await harness.CommitAsync(tx => root.SubmitAsync(tx, draft), Context);
            Equal(first, again, "again");
            Equal(commits, storage.CommitCount, "commits");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "rejects-before-any-write-so-the-callback-may-catch-the-rejection", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await OpenChat(storage, Setup());
            var commits = storage.CommitCount;
            var outcome = await harness.CommitAsync(async tx =>
            {
                try
                {
                    await root.SubmitAsync(tx, SubmissionDraft.Input("hi", WhenBusy.Steer, whenIdle: WhenIdle.Reject));
                    return "admitted";
                }
                catch (Exception error)
                {
                    return error is ConversationIdleException ? "idle" : "other";
                }
            }, Context);
            Equal("idle", outcome, "outcome");
            Equal(commits, storage.CommitCount, "commits");
            Equal<JsonValue?>(null, await RunOf(harness, root.Id), "run");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "throws-readafterwrite-after-a-table-write-of-the-commit-on-every-path", async () =>
        {
            var (harness, root, _, _) = await OpenBusy();
            // A busy input without request ID or trace reads no table, and is still refused.
            var afterAppend = await ThrowsAsync<ReadAfterWriteException>(() => harness.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" });
                await root.SubmitAsync(tx, SubmissionDraft.Input("steer", WhenBusy.Steer));
                return 0;
            }, Context), "after append");
            Rejects(afterAppend, "submit(tx, draft) cannot read tables after the first table write", "after append message");
            await ThrowsAsync<ReadAfterWriteException>(() => harness.CommitAsync(async tx =>
            {
                await root.SubmitAsync(tx, SubmissionDraft.Input("one", WhenBusy.Steer));
                await root.SubmitAsync(tx, SubmissionDraft.Input("two", WhenBusy.Steer));
                return 0;
            }, Context), "second");
            SameJson("[]", await InboxItems(harness, root.Id), "inbox");

            var idle = await OpenChat(new ControlledStorage(new MemoryStorage()), Setup());
            await ThrowsAsync<ReadAfterWriteException>(() => idle.Harness.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(idle.Root.Id, new EntryDraft { Kind = "app.note" });
                await idle.Root.SubmitAsync(tx, SubmissionDraft.Input("hi"));
                return 0;
            }, Context), "late");
            await idle.Harness.CloseAsync(Context);
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "continues-a-failed-run-and-must-precede-the-commit-s-table-writes", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxStep.From(_ => Failure()), FauxAssistantMessage([FauxText("recovered")]));
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("Analyze this"), Context);
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await first.WaitAsync(Context)).ToJson(), "first");

            await ThrowsAsync<ReadAfterWriteException>(() => harness.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" });
                await root.SubmitAsync(tx, SubmissionDraft.Continue());
                return 0;
            }, Context), "late");

            var id = await harness.CommitAsync(async tx =>
            {
                var continued = await root.SubmitAsync(tx, SubmissionDraft.Continue());
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" });
                return continued;
            }, Context);
            MatchJson("""{"type":"continue","status":"done"}""", (await SettledStatus(harness, id)).ToJson(), "settled");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "rejects-a-busy-continue-and-steers-with-whenidle-inside-the-commit", async () =>
        {
            var (harness, root, _, _) = await OpenBusy();
            await ThrowsAsync<ConversationBusyException>(() => harness.CommitAsync(tx => root.SubmitAsync(tx, SubmissionDraft.Continue()), Context), "busy");
            var steer = await harness.CommitAsync(
                tx => root.SubmitAsync(tx, SubmissionDraft.Input("steer", WhenBusy.Steer, whenIdle: WhenIdle.Reject)), Context);
            Seq([steer.Value], ItemIds(await InboxItems(harness, root.Id)), "inbox ids");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "throws-for-a-transaction-of-another-session-or-a-wrapped-one", async () =>
        {
            var a = await OpenChat(new ControlledStorage(new MemoryStorage()), Setup());
            var b = await OpenChat(new ControlledStorage(new MemoryStorage()), Setup());
            var draft = SubmissionDraft.Input("hi");
            await RejectsAsync(() => b.Harness.CommitAsync(tx => a.Root.SubmitAsync(tx, draft), Context), "The transaction belongs to another Session", "foreign");
            await RejectsAsync(() => a.Harness.CommitAsync(tx => a.Root.SubmitAsync(TxProxy.Wrap(tx, forwardAll: true), draft), Context),
                "The transaction belongs to another Session", "proxied");
            await RejectsAsync(() => a.Harness.CommitAsync(tx => a.Root.SubmitAsync(TxProxy.Wrap(tx, forwardAll: false), draft), Context),
                "The transaction belongs to another Session", "plain");
            Equal<JsonValue?>(null, await RunOf(a.Harness, a.Root.Id), "run");
            await a.Harness.CloseAsync(Context);
            await b.Harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "records-the-trace-carrier-and-links-a-steer-to-its-caller-and-the-running-trace", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var busy = Unanswered();
            setup.Faux.SetResponses(busy.Step);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup, telemetry: telemetry);
            await root.SubmitAsync(SubmissionDraft.Input("work"), Context);
            await busy.Reached;
            var run = (await harness.InspectAsync(Context)).Tasks.First(task => task.Record.Kind == "pi.generation").Record;
            var id = await telemetry.StartSpanAsync("caller", caller => harness.CommitAsync(
                tx => root.SubmitAsync(tx, SubmissionDraft.Input("steer", WhenBusy.Steer)), Context.WithTelemetry(caller)));
            var spans = telemetry.GetSpans();
            var callerSpan = spans.First(span => span.Name == "caller");
            var submit = spans.First(span => span.Name == "pi.durable.submit" && Num(span, "submission.id") == id.Value);
            Check(submit.ParentId is null && Str(submit, "result") == "queued", $"submit: parent {submit.ParentId}, result {Str(submit, "result")}");
            Check(submit.Links is [var toCaller, var toRun] && SameCarrier(callerSpan.Context, toCaller.Context) && toCaller.Attributes.Count == 0 &&
                SameCarrier(run.Trace, toRun.Context) && toRun.Attributes.Count == 0, "submit links");
            Check(SameCarrier(submit.Context, (await (await harness.SubmissionAsync(id, Context))!.StatusAsync(Context)).Trace), "record trace");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "starts-an-idle-input-s-run-in-the-admission-s-trace", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var busy = Unanswered();
            setup.Faux.SetResponses(busy.Step);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup, telemetry: telemetry);
            var id = await telemetry.StartSpanAsync("caller", caller =>
                harness.CommitAsync(tx => root.SubmitAsync(tx, SubmissionDraft.Input("hi")), Context.WithTelemetry(caller)));
            await busy.Reached;
            var submit = telemetry.GetSpans().First(span => span.Name == "pi.durable.submit" && Num(span, "submission.id") == id.Value);
            var record = await (await harness.SubmissionAsync(id, Context))!.StatusAsync(Context);
            Check(SameCarrier(submit.Context, record.Trace), "record trace");
            var run = (await harness.InspectAsync(Context)).Tasks.First(task => task.Record.Kind == "pi.generation").Record;
            Check(SameCarrier(record.Trace, run.Trace), "run trace");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "makes-a-write-a-child-of-its-caller-and-leaves-the-commit-s-events-to-the-caller-s-commit", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var (harness, root) = await OpenChat(new MemoryStorage(), setup, telemetry: telemetry);
            var id = await telemetry.StartSpanAsync("caller", caller => harness.CommitAsync(
                tx => root.SubmitAsync(tx, SubmissionDraft.Write(new EntryDraft { Kind = "app.note" })), Context.WithTelemetry(caller)));
            var spans = telemetry.GetSpans();
            var callerSpan = spans.First(span => span.Name == "caller");
            var submit = spans.First(span => span.Name == "pi.durable.submit");
            Check(SameCarrier(callerSpan.Context, submit.ParentContext) && submit.Settled && Num(submit, "submission.id") == id.Value &&
                Str(submit, "result") == "done" && submit.Events.Count == 0, $"submit: settled {submit.Settled}, result {Str(submit, "result")}, " +
                $"events {submit.Events.Count}");
            Seq(["pi.durable.entry", "pi.durable.submission"], callerSpan.Events.Select(item => item.Name), "caller events");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "ends-its-span-before-the-commit-and-a-later-throw-leaves-no-submission", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var (harness, root) = await OpenChat(new MemoryStorage(), setup, telemetry: telemetry);
            await telemetry.StartSpanAsync("caller", async caller =>
            {
                await RejectsAsync(() => harness.CommitAsync<int>(async tx =>
                {
                    await root.SubmitAsync(tx, SubmissionDraft.Write(new EntryDraft { Kind = "app.note" }));
                    throw new InvalidOperationException("later");
                }, Context.WithTelemetry(caller)), "later", "failed");
            });
            var spans = telemetry.GetSpans();
            var submit = spans.First(span => span.Name == "pi.durable.submit");
            // The span reported an admission the commit then discarded.
            Check(submit.Settled && Str(submit, "result") == "done", $"submit: settled {submit.Settled}, result {Str(submit, "result")}");
            var id = new SubmissionId((long)Num(submit, "submission.id")!.Value);
            Equal<ISubmission?>(null, await harness.SubmissionAsync(id, Context), "submission");
            Equal(0, spans.First(span => span.Name == "caller").Events.Count, "caller events");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "admits-atomically-from-a-tool-s-commit-through-its-conversation-handle", async () =>
        {
            var setup = Setup();
            IConversation? other = null;
            TaskId? toolTask = null;
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "delegate",
                Description = "Delegates to another conversation",
                Parameters = NoParameters,
                Execute = async (_, api, callContext) =>
                {
                    toolTask = api.TaskId;
                    var handle = (await api.ConversationAsync(other!.Id, callContext))!;
                    var id = await api.CommitAsync(async tx =>
                    {
                        var admitted = await handle.SubmitAsync(tx, SubmissionDraft.Input("from the tool"));
                        await tx.AppendEntryAsync(other!.Id, new EntryDraft { Kind = "app.delegated", Data = Obj(("submission", admitted.ToJson())) });
                        return admitted;
                    }, callContext);
                    return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"submitted {id.Value}")) };
                },
            }));
            var reply = FauxStep.From(request =>
            {
                var last = request.Messages.IsEmpty ? null : request.Messages[^1] as JsonObjectValue;
                return last is not null && last["role"] is JsonStringValue { Value: "user" } && last["content"] is JsonStringValue { Value: "delegate" }
                    ? FauxAssistantMessage([FauxToolCall("delegate", JsonObjectValue.Empty, "c1")], stopReason: "toolUse")
                    : FauxAssistantMessage([FauxText("ok")]);
            });
            setup.Faux.SetResponses(reply, reply, reply);
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless) { Agent = new AgentChange { Model = Model } },
                Context);
            var run = await root.SubmitAsync(SubmissionDraft.Input("delegate"), Context);
            MatchJson("""{"status":"done"}""", (await run.WaitAsync(Context)).ToJson(), "run");
            await harness.WaitForIdleAsync(Context);
            var entries = (await other.EntriesAsync(new EntryQueryBounds(), 100, null, Context)).Items.Reverse().ToList();
            var delegated = entries.First(entry => entry.Kind == "app.delegated");
            var submission = new SubmissionId((long)((JsonNumberValue)((JsonObjectValue)delegated.Data!)["submission"]).Value);
            var settled = await SettledStatus(harness, submission);
            if (settled.Type != SubmissionType.Input || settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"Unexpected {settled.Status}");
            // The placed input carries the tool task's byTaskId, as the commit's own appends do.
            var user = await other.CommitAsync(tx => tx.GetEntryAsync(Entries.UserEntry, settled.Entry!.Value), Context);
            Equal(toolTask, user?.ByTaskId, "byTaskId");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "admits-inside-a-task-s-terminal-commit", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText("answer")]));
            var admitting = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.admitting",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, runtime, taskContext) =>
                    {
                        var handle = (await runtime.ConversationAsync(runtime.ConversationId, taskContext))!;
                        await runtime.CommitAsync(async (tx, _) =>
                        {
                            var id = await handle.SubmitAsync(tx, SubmissionDraft.Input("from the task"));
                            return (TaskState?)NextTaskState.Completed(id.ToJson());
                        }, taskContext);
                    },
                },
                Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext),
            });
            AddTask(setup.Registry, admitting);
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            var task = await root.CommitAsync(tx => tx.CreateTaskAsync(admitting, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            var settled = await harness.WaitForTaskAsync(task, Context);
            if (((SettlingTaskState)settled.State).Outcome is not CompletedTaskOutcome { Result: JsonNumberValue result })
                throw new InvalidOperationException(((SettlingTaskState)settled.State).Outcome.Status.ToString());
            var status = await SettledStatus(harness, new SubmissionId((long)result.Value));
            if (status.Type != SubmissionType.Input || status.Status != SubmissionStatus.Done) throw new InvalidOperationException($"Unexpected {status.Status}");
            var user = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.UserEntry, status.Entry!.Value), Context);
            Equal<TaskId?>(task, user?.ByTaskId, "byTaskId");
            await harness.CloseAsync(Context);
        });

        // #11149
        yield return Case(inCommit + "rejects-through-a-handle-once-its-invocation-is-aborted-and-once-it-ended", async () =>
        {
            var setup = Setup();
            IConversation? other = null;
            IConversationHandle? handle = null;
            var started = new Deferred();
            var abortSeen = new Deferred();
            var release = new Deferred();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "hold",
                Description = "Holds until released, also when aborted",
                Parameters = NoParameters,
                Execute = async (_, api, callContext) =>
                {
                    handle = (await api.ConversationAsync(other!.Id, callContext))!;
                    using var registration = callContext.CancellationToken.Register(abortSeen.Resolve);
                    started.Resolve();
                    await release.Task;
                    return new ToolExecutionResult { Output = JsonValue.Array([]) };
                },
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("ok")]));
            var (harness, root) = await OpenChat(new ControlledStorage(new MemoryStorage()), setup);
            other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless) { Agent = new AgentChange { Model = Model } },
                Context);
            var run = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            var tool = (await harness.InspectAsync(Context)).Tasks.First(task => task.Record.Kind == "pi.tool").Record;
            var aborting = harness.AbortTaskAsync(tool.Id, Context);
            await abortSeen.Task;
            var draft = SubmissionDraft.Input("late");
            await ThrowsAsync<Exception>(() => handle!.SubmitAsync(draft, Context), "submit once aborted");
            await ThrowsAsync<Exception>(() => harness.CommitAsync(tx => handle!.SubmitAsync(tx, draft), Context), "submit in commit once aborted");
            Equal<JsonValue?>(null, await RunOf(harness, other.Id), "run once aborted");
            release.Resolve();
            await aborting;
            await run.WaitAsync(Context);
            await ThrowsAsync<Exception>(() => harness.CommitAsync(tx => handle!.SubmitAsync(tx, draft), Context), "submit in commit once ended");
            Equal<JsonValue?>(null, await RunOf(harness, other.Id), "run once ended");
            await harness.CloseAsync(Context);
        });
    }
}

/// <summary>A wrapper of a transaction that is not one (upstream's <c>new Proxy(tx, {})</c>, and <c>{ doc: tx.doc.bind(tx) }</c>):
/// <c>forwardAll</c> forwards every call, otherwise only <c>DocAsync</c>.</summary>
internal class TxProxy : DispatchProxy
{
    private ITx? inner;
    private bool forwardAll;

    public static ITx Wrap(ITx tx, bool forwardAll)
    {
        var proxy = Create<ITx, TxProxy>();
        var self = (TxProxy)(object)proxy;
        self.inner = tx;
        self.forwardAll = forwardAll;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (!forwardAll && targetMethod.Name != "DocAsync") throw new NotSupportedException($"{targetMethod.Name} is not a function");
        try { return targetMethod.Invoke(inner, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { throw error.InnerException; }
    }
}
