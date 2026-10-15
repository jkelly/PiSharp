// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-input-entries.test.ts. Inputs placed as application
// entries, and data-only writes appended at once (#10407). Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and
// these:
// - An InputEntry is typed: its kind is a string and it has no model, head or edits. "rejects an input entry whose kind is not a non-empty
//   string ..." checks null, "", "pi.user" and "pi.compaction" (upstream's 123 has no C# counterpart), and "takes only kind and data from
//   an untyped caller's entry" checks that what the inbox and the transcript keep is the kind and data alone, which the type makes the only
//   thing a caller can pass.
// - The reopen case reopens a SQLite file, as upstream does.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessInputEntriesCases() => InputEntriesTests.Cases("harness.input-entries.");
}

/// <summary>The harness.input-entries.* cases.</summary>
internal static class InputEntriesTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    private static JsonObjectValue Answer(string text) => FauxAssistantMessage([FauxText(text)]);

    private sealed record GatedStep(FauxStep Step, Task Reached, Action Release);

    /// <summary>A faux response held until <c>Release</c> or cancellation; <c>Reached</c> completes when the request is sent.</summary>
    private static GatedStep Gated(JsonObjectValue message)
    {
        var reached = new Deferred();
        var gate = new Deferred();
        FauxStep step = new FauxResponseFactory(async request =>
        {
            reached.Resolve();
            await await Task.WhenAny(gate.Task, Aborted(request.Signal));
            return message;
        });
        return new GatedStep(step, reached.Task, gate.Resolve);
    }

    /// <summary>Registers a <c>hold</c> tool whose calls wait for <paramref name="gate"/>.</summary>
    private static void HoldTool(ChatSetup setup, Deferred gate) =>
        AddTool(setup.Registry, Define.Tool(new ToolRegistration
        {
            Name = "hold",
            Description = "Waits for the test",
            Parameters = NoParameters,
            Execute = async (_, _, _) =>
            {
                await gate.Task;
                return new ToolExecutionResult { Content = JsonValue.Array(FauxText("held")) };
            },
        }));

    private static readonly JsonObjectValue Hold = FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse");

    /// <summary>Role and text of each request message, as the provider receives them.</summary>
    private static FauxStep RequestLog(SyncList<List<string>> requests, JsonObjectValue reply) => new FauxResponseFactory(request =>
    {
        requests.Add([.. request.Messages.Select(message => $"{S(((JsonObjectValue)message)["role"])}:{TextOf(message) ?? ""}")]);
        return Task.FromResult(reply);
    });

    /// <summary>Kind, application data and model text of each entry, skipping system entries, as JSON arrays.</summary>
    private static JsonValue Transcript(IEnumerable<EntryRecord> entries) => JsonValue.Array(entries.Where(entry => entry.Kind != "pi.system").Select(entry =>
    {
        var message = entry.Model is { Count: > 0 } model ? model[0] : null;
        var text = message is null || message is JsonObjectValue value && value["role"] is JsonStringValue { Value: "toolResult" } ? null : TextOf(message);
        var row = new List<JsonValue> { entry.Kind };
        if (entry.Data is { } data && !entry.Kind.StartsWith("pi.", StringComparison.Ordinal)) row.Add(data);
        if (text is not null) row.Add(text);
        return (JsonValue)JsonValue.Array(row);
    }));

    private static async Task<JsonArrayValue> InboxItems(IHarness harness, IConversation root) =>
        (JsonArrayValue)(await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Context))!["items"];

    private static Task ToolRunning(IHarness harness, IConversation root) => Eventually(async () =>
        await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context) is { } live && live.TryGetValue("tools", out var tools) &&
        tools is JsonArrayValue { Count: > 0 } slots && ((JsonObjectValue)slots[0]).TryGetValue("status", out var status) &&
        status is JsonStringValue { Value: "running" });

    private static InputEntry Mention(string text) => new("chat.mention", Obj(("author", "ann"), ("text", text)));

    private static Task<ISubmission> Input(IConversation root, string content, InputEntry? entry = null, WhenBusy? whenBusy = null,
        string? requestId = null) => root.SubmitAsync(SubmissionDraft.Input(content, whenBusy, requestId, entry), Context);

    private static Task<ISubmission> Write(IConversation root, EntryDraft entry, string? requestId = null) =>
        root.SubmitAsync(SubmissionDraft.Write(entry, requestId), Context);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // ─── input entries ───

        yield return Case(prefix + "places-an-idle-input-as-its-entry-the-given-kind-and-data-with-the-content-as-its-user-message", async () =>
        {
            var setup = Setup();
            setup.Now = () => 42;
            var requests = new SyncList<List<string>>();
            setup.Faux.SetResponses(RequestLog(requests, Answer("hi ann")));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await Input(root, "ann: hi", Mention("hi"));
            var settled = await submission.WaitAsync(Context);
            var entries = await AllEntries(root);
            SameJson("""[["chat.mention",{"author":"ann","text":"hi"},"ann: hi"],["pi.assistant","hi ann"]]""", Transcript(entries), "transcript");
            var placed = entries.Single(entry => entry.Kind == "chat.mention");
            SameJson("""[{"role":"user","content":"ann: hi","timestamp":42}]""", placed.Model, "model");
            MatchJson($$"""{"status":"done","entry":{{placed.Id}},"answer":{{entries[^1].Id}}}""", settled.ToJson(), "settled");
            Seq(["user:ann: hi"], requests.Snapshot().Single(), "requests");
            // The context of a fork at the input is the same as for a pi.user entry.
            var view = await root.ContextAsync(Context, placed.Id);
            SameJson(placed.Model!.ToJson(), JsonValue.Array(view.Messages.Where(message => S(((JsonObjectValue)message)["role"]) != "system")), "context");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-an-input-entry-whose-kind-is-not-a-non-empty-string-or-starts-with-pi-writing-nothing", async () =>
        {
            var setup = Setup();
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var before = (await AllEntries(root)).Count;
            foreach (var kind in new string?[] { null, "", "pi.user", "pi.compaction" })
                await RejectsAsync(() => Input(root, "x", new InputEntry(kind!)), "An input entry's kind must be", $"kind {kind ?? "null"}");
            Equal(before, (await AllEntries(root)).Count, "entries");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            // Writes keep their kinds: reset() writes pi.reset.
            await root.ResetAsync(null, Context);
            Equal("pi.reset", (await AllEntries(root))[^1].Kind, "reset");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "takes-only-kind-and-data-from-an-untyped-callers-entry", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(Answer("idle"), first.Step, Answer("queued"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            // An InputEntry carries a kind and data only; upstream's untyped extra fields cannot be passed.
            Check(typeof(InputEntry).GetProperties().Select(property => property.Name).Order().SequenceEqual(["Data", "Kind"]), "InputEntry members");
            await (await Input(root, "idle", new InputEntry("chat.mention"))).WaitAsync(Context);
            await Input(root, "a");
            await first.Reached;
            var queued = await Input(root, "later", new InputEntry("chat.mention"));
            SameJson($$$"""{"id":{{{queued.Id}}},"mode":"followUp","content":"later","entry":{"kind":"chat.mention"}}""", (await InboxItems(harness, root))[^1],
                "queued item");
            first.Release();
            await queued.WaitAsync(Context);
            var mentions = (await AllEntries(root)).Where(entry => entry.Kind == "chat.mention").ToList();
            Seq(["- - idle 1", "- - later 1"], mentions.Select(entry =>
                $"{(entry.Head is null ? "-" : "head")} {(entry.Edits is null ? "-" : "edits")} {TextOf(entry.Model![0])} {entry.Model.Count}"), "mentions");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "queues-busy-inputs-with-their-entries-in-the-inbox-and-places-them-as-those-entries-at-boundaries", async () =>
        {
            var setup = Setup();
            var gate = new Deferred();
            HoldTool(setup, gate);
            setup.Faux.SetResponses(Hold, Answer("after tools"), Answer("follow-up"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var input = await Input(root, "a");
            await ToolRunning(harness, root);
            var steer = await Input(root, "s", Mention("s"), WhenBusy.Steer, "r-s");
            var followUp = await Input(root, "f", new InputEntry("p2.input"), WhenBusy.FollowUp);
            SameJson($$$"""
                [{"id":{{{steer.Id}}},"mode":"steer","content":"s","entry":{"kind":"chat.mention","data":{"author":"ann","text":"s"} } },
                 {"id":{{{followUp.Id}}},"mode":"followUp","content":"f","entry":{"kind":"p2.input"}}]
                """, await InboxItems(harness, root), "inbox");
            // The request ID deduplicates a queued input with an entry as any other.
            var again = await Input(root, "other", Mention("other"), WhenBusy.Steer, "r-s");
            Equal(steer.Id, again.Id, "deduplicated");
            await ThrowsAsync<ConversationBusyException>(() => Input(root, "x", Mention("x"), WhenBusy.Reject), "reject");
            Equal(2, (await InboxItems(harness, root)).Count, "inbox items");

            gate.Resolve();
            await followUp.WaitAsync(Context);
            var entries = await AllEntries(root);
            SameJson("""
                [["pi.user","a"],["pi.assistant"],["pi.tool-result"],["chat.mention",{"author":"ann","text":"s"},"s"],["pi.assistant","after tools"],
                 ["p2.input","f"],["pi.assistant","follow-up"]]
                """, Transcript(entries), "transcript");
            var answers = entries.Where(entry => entry.Kind == "pi.assistant").ToList();
            EntryId Placed(string kind) => entries.Single(entry => entry.Kind == kind).Id;
            MatchJson($$"""{"status":"done","answer":{{answers[1].Id}}}""", (await input.StatusAsync(Context)).ToJson(), "input");
            MatchJson($$"""{"status":"done","entry":{{Placed("chat.mention")}},"answer":{{answers[1].Id}}}""", (await steer.StatusAsync(Context)).ToJson(), "steer");
            MatchJson($$"""{"status":"done","entry":{{Placed("p2.input")}},"answer":{{answers[2].Id}}}""", (await followUp.StatusAsync(Context)).ToJson(),
                "follow-up");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "withdraws-queued-inputs-with-entries-one-by-one-and-by-conversation-abort", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var input = await Input(root, "a");
            await first.Reached;
            var withdrawn = await Input(root, "w", Mention("w"));
            var aborted = await Input(root, "x", Mention("x"));
            Equal(SubmissionAbortResult.Aborted, await withdrawn.AbortAsync(Context), "abort");
            Seq([(double)aborted.Id.Value], (await InboxItems(harness, root)).Select(item => N(((JsonObjectValue)item)["id"])), "inbox");
            await root.AbortAsync(Context);
            foreach (var submission in new[] { withdrawn, aborted })
                MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await submission.WaitAsync(Context)).ToJson(), $"submission {submission.Id}");
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await input.WaitAsync(Context)).ToJson(), "input");
            Equal(0, (await InboxItems(harness, root)).Count, "inbox");
            Check(!(await AllEntries(root)).Any(entry => entry.Kind == "chat.mention"), "no mention placed");
            await harness.CloseAsync(Context);
        });

        // ─── data-only writes ───

        yield return Case(prefix + "appends-a-data-only-write-mid-run-in-time-order-leaving-the-models-messages-unchanged", async () =>
        {
            var setup = Setup();
            var gate = new Deferred();
            HoldTool(setup, gate);
            var requests = new SyncList<List<string>>();
            setup.Faux.SetResponses(RequestLog(requests, Hold), RequestLog(requests, Answer("done")));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var events = new SyncList<AgentEvent>();
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            stream.Start((batch, _) =>
            {
                foreach (var item in batch) events.Add(item);
                return Task.CompletedTask;
            });
            var input = await Input(root, "a");
            await ToolRunning(harness, root);
            var chat = new EntryDraft { Kind = "chat.message", Data = Obj(("author", "bob"), ("text", "lunch?")) };
            var write = await Write(root, chat, "bob:1");
            // Settled and appended in its own commit, between the call and its still-running tool's result.
            var settled = await write.StatusAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            Equal(0, (await InboxItems(harness, root)).Count, "inbox");
            SameJson("""[["pi.user","a"],["pi.assistant"],["chat.message",{"author":"bob","text":"lunch?"}]]""", Transcript(await AllEntries(root)),
                "transcript while busy");
            await Eventually(() => events.Snapshot().Any(item => item is EntryAppendedEvent));
            var appended = (EntryAppendedEvent)events.Snapshot().First(item => item is EntryAppendedEvent);
            Equal("chat.message", appended.Entry.Kind, "appended kind");
            Equal(settled.Entry, appended.Entry.Id, "appended entry");
            Check(!events.Snapshot().Any(item => item.Type == "run_end"), "run still going");
            // A repeated request ID returns the same write without a second entry.
            Equal(write.Id, (await Write(root, chat, "bob:1")).Id, "deduplicated");

            gate.Resolve();
            await input.WaitAsync(Context);
            SameJson("""
                [["pi.user","a"],["pi.assistant"],["chat.message",{"author":"bob","text":"lunch?"}],["pi.tool-result"],["pi.assistant","done"]]
                """, Transcript(await AllEntries(root)), "transcript");
            var seen = requests.Snapshot();
            Equal(2, seen.Count, "requests");
            Seq(["system:", "user:a"], seen[0], "first request");
            Seq(["system:", "user:a", "assistant:", "toolResult:held"], seen[1], "second request");
            await stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "queues-a-data-only-write-behind-a-queued-reset-so-it-lands-after-the-reset-and-stays-visible", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var input = await Input(root, "a");
            await first.Reached;
            await root.ResetAsync(null, Context);
            var chat = await Write(root, new EntryDraft { Kind = "chat.message", Data = "hi" });
            Equal(SubmissionStatus.Queued, (await chat.StatusAsync(Context)).Status, "queued behind the reset");
            first.Release();
            await input.WaitAsync(Context);
            var settled = await chat.WaitAsync(Context);
            var entries = await AllEntries(root);
            var reset = entries.Single(entry => entry.Kind == "pi.reset");
            Equal(SubmissionStatus.Done, settled.Status, "settled");
            Check(settled.Entry!.Value.Value > reset.Id.Value, "after the reset");
            Seq(["pi.reset", "chat.message"], (await root.ContextAsync(Context)).Entries.Select(entry => entry.Kind), "context entries");
            // Once the reset is placed, display-only writes go in at once again.
            var later = await Write(root, new EntryDraft { Kind = "chat.message", Data = "again" });
            Equal(SubmissionStatus.Done, (await later.StatusAsync(Context)).Status, "at once again");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "leaves-the-request-unchanged-by-a-data-only-write-that-lands-while-the-generation-prepares", async () =>
        {
            var setup = Setup();
            var rendering = new Deferred();
            var render = new Deferred();
            var renders = 0;
            AddSection(setup.Registry, "slow", async (_, _) =>
            {
                if (Interlocked.Increment(ref renders) == 2)
                {
                    rendering.Resolve();
                    await render.Task;
                }
                return "SLOW";
            });
            var requests = new SyncList<List<string>>();
            setup.Faux.SetResponses(RequestLog(requests, Answer("one")), RequestLog(requests, Answer("two")));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await Input(root, "a")).WaitAsync(Context);
            var second = await Input(root, "b");
            await rendering.Task;
            await Write(root, new EntryDraft { Kind = "chat.message", Data = "hi" });
            render.Resolve();
            await second.WaitAsync(Context);
            Seq(["system:", "user:a", "assistant:one", "user:b"], requests.Snapshot()[1], "second request");
            // The unchanged prompt adds no second pi.system entry.
            Seq(["pi.user", "pi.system", "pi.assistant", "pi.user", "chat.message", "pi.assistant"], (await AllEntries(root)).Select(entry => entry.Kind),
                "kinds");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "appends-a-data-only-write-while-idle-after-a-failed-run-without-placing-the-queued-items", async () =>
        {
            var setup = Setup();
            var failing = Gated(FauxAssistantMessage(Array.Empty<JsonObjectValue>(), stopReason: "error", errorMessage: "invalid request"));
            setup.Faux.SetResponses(failing.Step, Answer("for f"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var input = await Input(root, "a");
            await failing.Reached;
            var f = await Input(root, "f", Mention("f"));
            failing.Release();
            MatchJson("""{"status":"unanswered","reason":"model_error"}""", (await input.WaitAsync(Context)).ToJson(), "input");
            await harness.WaitForIdleAsync(Context);
            var note = await Write(root, new EntryDraft { Kind = "note", Data = "n" });
            Equal(SubmissionStatus.Done, (await note.StatusAsync(Context)).Status, "note");
            await harness.WaitForIdleAsync(Context);
            Equal(SubmissionStatus.Queued, (await f.StatusAsync(Context)).Status, "f");
            Seq([(double)f.Id.Value], (await InboxItems(harness, root)).Select(item => N(((JsonObjectValue)item)["id"])), "inbox");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            Equal(1, setup.Faux.State.CallCount, "calls");
            // The next input places the waiting mention first.
            var g = await Input(root, "g");
            await g.WaitAsync(Context);
            var kept = ((JsonArrayValue)Transcript(await AllEntries(root))).Where(row => S(((JsonArrayValue)row)[0]) != "pi.assistant");
            SameJson("""[["pi.user","a"],["note","n"],["chat.mention",{"author":"ann","text":"f"},"f"],["pi.user","g"]]""", JsonValue.Array(kept), "transcript");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-queued-inputs-entry-across-reopen-and-places-it-as-that-entry", async () =>
        {
            var path = Path.Combine(Temp("input-entries-reopen"), "session.sqlite");
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("first again"), Answer("for f"));
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            await Input(opened.Root, "a");
            await first.Reached;
            var f = (await Input(opened.Root, "f", Mention("f"))).Id;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            var item = (await InboxItems(opened.Harness, opened.Root)).Cast<JsonObjectValue>().Single(value => N(value["id"]) == f.Value);
            SameJson("""{"kind":"chat.mention","data":{"author":"ann","text":"f"}}""", item["entry"], "queued entry");
            var settled = await (await opened.Harness.SubmissionAsync(f, Context))!.WaitAsync(Context);
            var placed = (await AllEntries(opened.Root)).Single(entry => entry.Kind == "chat.mention");
            MatchJson($$"""{"status":"done","entry":{{placed.Id}}}""", settled.ToJson(), "settled");
            MatchJson("""{"data":{"author":"ann","text":"f"},"model":[{"role":"user","content":"f"}]}""", placed.ToJson(), "placed");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "appends-a-data-only-write-at-once-behind-a-non-empty-inbox-and-still-queues-writes-with-model-or-edits", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("second"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var input = await Input(root, "a");
            await first.Reached;
            var followUp = await Input(root, "f");
            var model = await Write(root, new EntryDraft { Kind = "note", Model = JsonValue.Array(Obj(("role", "user"), ("content", "m"), ("timestamp", 0))) });
            var target = (await AllEntries(root)).Single(entry => entry.Kind == "pi.user");
            var edit = await Write(root, new EntryDraft { Kind = "redact", Edits = [ContextEdit.Omit(target.Id)] });
            var note = await Write(root, new EntryDraft { Kind = "note", Data = "n" });
            Equal(SubmissionStatus.Done, (await note.StatusAsync(Context)).Status, "note");
            Seq([$"{followUp.Id} followUp", $"{model.Id} write", $"{edit.Id} write"], (await InboxItems(harness, root)).Cast<JsonObjectValue>()
                .Select(item => $"{N(item["id"])} {S(item["mode"])}"), "inbox");
            first.Release();
            await followUp.WaitAsync(Context);
            SameJson("""
                [["pi.user","a"],["note","n"],["pi.assistant","first"],["note","m"],["redact"],["pi.user","f"],["pi.assistant","second"]]
                """, Transcript(await AllEntries(root)), "transcript");
            MatchJson("""{"status":"done"}""", (await input.StatusAsync(Context)).ToJson(), "input");
            await harness.CloseAsync(Context);
        });
    }
}
