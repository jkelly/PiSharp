// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): the scenarios of tools/DurableFixtures/scenarios-v1.2.0.mjs that the v1.2.0 sync
// areas have ported so far, run on PiSharp against Fixtures/v1.2.0/upstream-sessions. compat.* (CompatCases.cs) reads the v1.1.0 set for the
// scenarios both pins capture until area a switches it (Fixtures/v1.2.0/index.json, "switching"); these scenarios exist only at v1.2.0, so
// their cases read the v1.2.0 set now: read, raw, harness and written, as for the v1.1.0 scenarios, and observed (what calls returned or
// rejected with, observed.json). Keep each scenario in step with its upstream twin: same faux responses, same calls in the same order.
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
/// when present, reopens and finishes it.</summary>
internal sealed record CompatScenario120(string Name, string[] Areas, Func<Func<Task<IStorage>>, Task<JsonValue?>> Run,
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
    ];
}
