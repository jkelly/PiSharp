// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-inbox.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - Backends. Upstream opens these on MemoryStorage and reopens a SQLite file for the reopen cases. harness.inbox.* does the same;
//   harness.inbox.jsonl.* and harness.inbox.sqlite.* rerun every case on a fresh JSONL directory and a fresh SQLite file, and
//   harness.inbox.jsonl.* reopens a JSONL directory for the reopen cases (harness.inbox.sqlite.* leaves them to harness.inbox.*).
// - Inbox items `[id, mode]` compare as the strings "id mode".
// - `Type.Object({})` is NoParameters, the JSON TypeBox prints for it.
// - "reads queue modes when the final boundary's commit runs on the Session line": upstream waits for onYield and then flushes once, so the
//   answer's boundary commit has queued behind the held commit before the test changes the mode. Here the onYield hook records the Session
//   line's backlog, and the test waits (a deterministic barrier) until a further job, the boundary commit, waits on the line.
// - `class RecordingStorage extends MemoryStorage { override commit() }` is RecordingStorage, a decorator over the case's storage.
// - The stubborn and handoff tools return their empty results as ToolExecutionResult.Content: upstream's v1.2.0 `output` is area a's
//   rename of that field, which renames it in every test at once.
// - The usage cases call UsageLedger.RecordUsageAsync for upstream's `recordUsage`, and HarnessOptions.Settings' Retry for
//   `setup.settings.retry`.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessInboxCases()
    {
        static Func<IStorage> SqliteFile()
        {
            var path = Path.Combine(Temp("inbox-reopen-sqlite"), "session.sqlite");
            return () => SqliteStorage.OpenAsync(path).GetAwaiter().GetResult();
        }
        static Func<IStorage> JsonlDirectory()
        {
            var directory = Temp("inbox-reopen-jsonl");
            return () => JsonlStorage.OpenAsync(directory, DurableContext.Background).GetAwaiter().GetResult();
        }
        return
        [
            .. InboxTests.Cases("harness.inbox.", () => new MemoryStorage(), SqliteFile),
            .. InboxTests.Cases("harness.inbox.jsonl.",
                () => JsonlStorage.OpenAsync(Temp("inbox-jsonl"), DurableContext.Background).GetAwaiter().GetResult(), JsonlDirectory),
            .. InboxTests.Cases("harness.inbox.sqlite.",
                () => SqliteStorage.OpenAsync(Path.Combine(Temp("inbox-sqlite"), "session.sqlite")).GetAwaiter().GetResult(), null),
        ];
    }
}

/// <summary>The harness.inbox.* cases (upstream harness-inbox.test.ts).</summary>
internal static class InboxTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary><c>Type.Object({})</c>.</summary>
    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    private static readonly JsonObjectValue Usage1 =
        O("""{"input":1,"output":2,"cacheRead":3,"cacheWrite":4,"totalTokens":10,"cost":{"input":0.1,"output":0.2,"cacheRead":0.3,"cacheWrite":0.4,"total":1}}""");

    private static JsonObjectValue Answer(string text) => FauxAssistantMessage(text);

    /// <summary>A faux response held until <c>Release</c> or cancellation; <c>Reached</c> completes when the request is sent (upstream
    /// <c>gated</c>).</summary>
    private sealed record GatedStep(FauxStep Step, Task Reached, Action Release);

    private static GatedStep Gated(JsonObjectValue message)
    {
        var reached = new HarnessTests.Deferred();
        var gate = new HarnessTests.Deferred();
        FauxStep step = new FauxResponseFactory(async request =>
        {
            reached.Resolve();
            await await Task.WhenAny(gate.Task, Aborted(request.Signal));
            return message;
        });
        return new GatedStep(step, reached.Task, gate.Resolve);
    }

    private static ToolExecutionResult Empty => new() { Output = JsonArrayValue.Empty };

    /// <summary>Registers a <c>hold</c> tool whose calls wait for <paramref name="gate"/> and then return <paramref name="result"/> (upstream
    /// <c>holdTool</c>).</summary>
    private static void HoldTool(ChatSetup setup, HarnessTests.Deferred gate, ToolExecutionResult? result = null) =>
        AddTool(setup.Registry, Define.Tool(new ToolRegistration
        {
            Name = "hold",
            Description = "Waits for the test",
            Parameters = NoParameters,
            Execute = async (_, _, _) =>
            {
                await gate.Task;
                return result ?? Empty;
            },
        }));

    private static readonly JsonObjectValue Hold = FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse");

    private static Task<SubmissionRecord> Status(ISubmission submission) => submission.StatusAsync(Context);

    private static Task<ISubmission> Submit(IConversation conversation, string text, WhenBusy? whenBusy = null, string? requestId = null) =>
        conversation.SubmitAsync(SubmissionDraft.Input(text, whenBusy, requestId), Context);

    private static Task<ISubmission> Write(IConversation conversation, EntryDraft entry) => conversation.SubmitAsync(SubmissionDraft.Write(entry), Context);

    /// <summary>A write that affects model context, so it queues while busy; a data-only write would be appended at once (upstream
    /// <c>queuedNote</c>).</summary>
    private static EntryDraft QueuedNote(string text) =>
        new() { Kind = "note", Model = JsonValue.Array(Obj(("role", "user"), ("content", text), ("timestamp", 0))) };

    /// <summary>Kind and text of each entry, skipping system entries (upstream <c>transcript</c>).</summary>
    private static List<string> Transcript(IEnumerable<EntryRecord> entries) =>
    [
        .. entries.Where(entry => entry.Kind != "pi.system").Select(entry =>
        {
            var message = entry.Model is { Count: > 0 } model ? model[0] : null;
            var text = message is JsonObjectValue value && value["role"] is JsonStringValue { Value: "toolResult" } ? null : TextOf(message);
            return text is null ? entry.Kind : $"{entry.Kind}:{text}";
        }),
    ];

    /// <summary>The inbox items as "id mode" (upstream <c>inbox</c>).</summary>
    private static async Task<List<string>> InboxItems(IHarness harness, IConversation root)
    {
        var inbox = (await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Context))!;
        return [.. ((JsonArrayValue)inbox["items"]).Select(item => $"{N(((JsonObjectValue)item)["id"])} {S(((JsonObjectValue)item)["mode"])}")];
    }

    /// <summary>A value of the conversation's <c>pi.live</c> at <paramref name="path"/>, or null when absent (upstream's optional chains).</summary>
    private static async Task<JsonValue?> LiveAt(IHarness harness, IConversation root, params object[] path)
    {
        JsonValue? value = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
        foreach (var segment in path)
            value = (value, segment) switch
            {
                (JsonObjectValue item, string key) when item.TryGetValue(key, out var found) => found,
                (JsonArrayValue items, int index) when index < items.Count => items[index],
                _ => null,
            };
        return value;
    }

    private static Task ToolRunning(IHarness harness, IConversation root) =>
        Eventually(async () => await LiveAt(harness, root, "tools", 0, "status") is JsonStringValue { Value: "running" });

    /// <summary>
    /// Deterministic barrier: upstream's wait resumes after every listener of the settling commit ran (one JavaScript thread); here a wait
    /// resumes once its own listener resolved it, while the commit's job may still run later listeners. Flushing the Session line waits for
    /// that job before a case reads what its own listener recorded.
    /// </summary>
    private static Task Delivered(IHarness harness) => ((DurableHarness)harness).FlushLineAsync();

    private static List<EntryRecord> Assistants(IEnumerable<EntryRecord> entries) => [.. entries.Where(entry => entry.Kind == "pi.assistant")];

    /// <summary><c>expect(actual).toEqual({ ...settled, id, entry: expect.any(Number) })</c>.</summary>
    private static void SettledLike(SubmissionRecord settled, SubmissionId id, SubmissionRecord actual, string what) =>
        MatchExactly(settled.ToJson().SetItem("id", id.ToJson()).SetItem("entry", "<number>").ToJson(), actual.ToJson(), what);

    private static void Done(SubmissionRecord record, EntryId? answer, string what) =>
        MatchJson(answer is { } id ? $$"""{"status":"done","answer":{{id}}}""" : """{"status":"done"}""", record.ToJson(), what);

    private static void Unanswered(SubmissionRecord record, string reason, string what) =>
        MatchJson($$"""{"status":"unanswered","reason":"{{reason}}"}""", record.ToJson(), what);

    /// <param name="fresh">A fresh storage for one case.</param>
    /// <param name="reopenable">A fresh reopenable location for the reopen cases; null skips them.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh, Func<Func<IStorage>>? reopenable)
    {
        // ─── inbox ──────────────────────────────────────────────────────────

        yield return Case(prefix + "queues-busy-submissions-and-places-writes-before-user-items-at-the-final-boundary-one-follow-up-per-run", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("second"), Answer("third"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            var f1 = await Submit(root, "f1");
            var write = await Write(root, QueuedNote("w"));
            var f2 = await Submit(root, "f2", WhenBusy.FollowUp);
            foreach (var submission in new[] { f1, write, f2 }) Equal(SubmissionStatus.Queued, (await Status(submission)).Status, "queued");
            Seq([$"{f1.Id} followUp", $"{write.Id} write", $"{f2.Id} followUp"], await InboxItems(harness, root), "inbox");
            Seq(["pi.user:a"], Transcript(await AllEntries(root)), "transcript while busy");

            first.Release();
            await f2.WaitAsync(Context);
            Seq(["pi.user:a", "pi.assistant:first", "note:w", "pi.user:f1", "pi.assistant:second", "pi.user:f2", "pi.assistant:third"],
                Transcript(await AllEntries(root)), "transcript");
            var answers = Assistants(await AllEntries(root));
            Done(await Status(input), answers[0].Id, "input");
            Done(await Status(write), null, "write");
            Done(await Status(f1), answers[1].Id, "f1");
            Done(await Status(f2), answers[2].Id, "f2");
            Seq([], await InboxItems(harness, root), "inbox after");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-every-follow-up-in-one-successor-run-with-followupmode-all", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("both"));
            var (harness, root) = await OpenChat(fresh(), setup);
            setup.FollowUpMode = QueueMode.All;
            await Submit(root, "a");
            await first.Reached;
            var f1 = await Submit(root, "f1");
            var f2 = await Submit(root, "f2");
            first.Release();
            var settled = await f2.WaitAsync(Context);
            SettledLike(settled, f1.Id, await Status(f1), "f1");
            Seq(["pi.user:a", "pi.assistant:first", "pi.user:f1", "pi.user:f2", "pi.assistant:both"], Transcript(await AllEntries(root)), "transcript");
            Equal(2, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reads-queue-modes-when-the-final-boundary-s-commit-runs-on-the-session-line", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("both"));
            DurableHarness? session = null;
            // Resolved with the line's backlog when onYield runs, before the answer's boundary commit is requested.
            var yielded = new HarnessTests.Deferred<int>();
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) =>
                {
                    yielded.Resolve(session!.LineBacklog);
                    return Task.FromResult<JsonValue?>(null);
                },
            });
            var storage = new ControlledStorage(fresh());
            var (harness, root) = await OpenChat(storage, setup);
            session = (DurableHarness)harness;
            await Submit(root, "a");
            await first.Reached;
            var f1 = await Submit(root, "f1");
            var f2 = await Submit(root, "f2");
            // Occupy the line, let the answer queue its boundary commit behind it, then change the mode.
            var held = storage.HoldCommits();
            var marker = Documents.DefineSessionDoc("test.marker", 1, () => O("""{"n":0}"""));
            var occupying = root.CommitAsync(async tx =>
            {
                var doc = await tx.DocAsync(marker);
                doc.Set("n", N(doc.Get("n")) + 1);
            }, Context);
            await held.Entered;
            first.Release();
            var backlog = await yielded.Task;
            // Deterministic barrier for upstream's flush(): the boundary commit waits on the line behind the held commit.
            await Eventually(() => session.LineBacklog > backlog);
            await Flush();
            setup.FollowUpMode = QueueMode.All;
            held.Release();
            await occupying;
            var settled = await f2.WaitAsync(Context);
            SettledLike(settled, f1.Id, await Status(f1), "f1");
            Equal(2, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "adds-steers-to-the-run-at-the-posttools-boundary-and-holds-follow-ups-for-the-final-boundary", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HoldTool(setup, gate);
            setup.Faux.SetResponses(Hold, Answer("after tools"), Answer("follow-up"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await ToolRunning(harness, root);
            var steer = await Submit(root, "s", WhenBusy.Steer);
            var followUp = await Submit(root, "f");
            gate.Resolve();
            await followUp.WaitAsync(Context);
            Seq(["pi.user:a", "pi.assistant", "pi.tool-result", "pi.user:s", "pi.assistant:after tools", "pi.user:f", "pi.assistant:follow-up"],
                Transcript(await AllEntries(root)), "transcript");
            var answers = Assistants(await AllEntries(root));
            Done(await Status(input), answers[1].Id, "input");
            Done(await Status(steer), answers[1].Id, "steer");
            Done(await Status(followUp), answers[2].Id, "follow-up");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-run-at-a-queued-reset-after-tools-and-runs-earlier-follow-ups-in-the-new-context", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HoldTool(setup, gate);
            var requests = new SyncList<List<string>>();
            var record = FauxStep.From(request =>
            {
                requests.Add([.. request.Messages.Select(message => $"{S(((JsonObjectValue)message)["role"])}:{TextOf(message) ?? ""}")]);
                return Answer("fresh");
            });
            setup.Faux.SetResponses(Hold, record);
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await ToolRunning(harness, root);
            var followUp = await Submit(root, "f");
            await root.ResetAsync(null, Context);
            gate.Resolve();
            await followUp.WaitAsync(Context);
            Unanswered(await Status(input), "reset", "input");
            Seq(["pi.user:a", "pi.assistant", "pi.tool-result", "pi.reset", "pi.user:f", "pi.assistant:fresh"], Transcript(await AllEntries(root)),
                "transcript");
            var reset = (await AllEntries(root)).First(entry => Entries.ResetEntry.Is(entry));
            Equal(reset.Id, reset.Head, "reset head");
            // The follow-up's request starts at the reset: the complete system baseline after the cut leads the follow-up.
            Equal(1, requests.Count, "requests");
            Seq(["system:", "user:f"], requests[0], "request");
            Equal(2, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-a-queued-reset-after-the-answer-at-the-final-boundary", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            await root.ResetAsync("handoff", Context);
            first.Release();
            await input.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            Done(await Status(input), null, "input");
            Seq(["pi.user:a", "pi.assistant:first", "pi.reset:handoff"], Transcript(await AllEntries(root)), "transcript");
            MatchExactly("""[{"role":"user","content":"handoff","timestamp":"<number>"}]""",
                JsonValue.Array((await root.ContextAsync(Context)).Messages), "messages");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resets-an-idle-conversation-at-once-with-or-without-handoff-text", async () =>
        {
            var setup = Setup();
            setup.Now = () => 7;
            var (harness, root) = await OpenChat(fresh(), setup);
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            await root.ResetAsync(null, Context);
            var view = await root.ContextAsync(Context);
            Equal("pi.reset", view.Head?.Kind, "head kind");
            Check(view.Head?.Model is null, "head model");
            Equal(0, view.Messages.Length, "messages");
            await root.ResetAsync("carry on", Context);
            view = await root.ContextAsync(Context);
            Equal(view.Head?.Id, view.Head?.Head, "head");
            SameJson("""[{"role":"user","content":"carry on","timestamp":7}]""", JsonValue.Array(view.Messages), "messages");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-queued-head-write-stale-when-it-targets-an-entry-before-the-active-range", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("second"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var old = await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            await root.ResetAsync(null, Context);
            var reset = (await root.ContextAsync(Context)).Head!;
            var input = await Submit(root, "a");
            await first.Reached;
            var stale = await Write(root, new EntryDraft { Kind = "summary", Head = EntryHead.At(old.Id) });
            var current = await Write(root, new EntryDraft { Kind = "summary", Head = EntryHead.At(reset.Id) });
            first.Release();
            await input.WaitAsync(Context);
            Unanswered(await Status(stale), "stale", "stale");
            Done(await Status(current), null, "fresh");
            Seq([], await InboxItems(harness, root), "inbox");

            // The fresh summary's marker starts the range at the reset: a target inside the range is not stale, even when it is older than
            // the marker itself. A reset placed earlier in the same boundary makes it stale.
            var inside = (await AllEntries(root)).First(entry => entry.Kind == "pi.user");
            var second = await Submit(root, "b");
            var kept = await Write(root, new EntryDraft { Kind = "summary", Head = EntryHead.At(inside.Id) });
            await second.WaitAsync(Context);
            Done(await Status(kept), null, "kept");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-head-write-stale-behind-a-reset-placed-earlier-in-the-same-boundary", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            var target = (await AllEntries(root))[0];
            await root.ResetAsync(null, Context);
            var summary = await Write(root, new EntryDraft { Kind = "summary", Head = EntryHead.At(target.Id) });
            first.Release();
            await input.WaitAsync(Context);
            Unanswered(await Status(summary), "stale", "summary");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-the-run-with-a-pi-reset-entry-when-a-tool-requests-a-handoff", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            gate.Resolve();
            HoldTool(setup, gate, Empty with { Control = new ToolControl { Handoff = "continue here" } });
            setup.Faux.SetResponses(Hold);
            var (harness, root) = await OpenChat(fresh(), setup);
            var settled = await (await Submit(root, "a")).WaitAsync(Context);
            var entries = await AllEntries(root);
            var calling = entries.First(entry => entry.Kind == "pi.assistant");
            Done(settled, calling.Id, "settled");
            Seq(["pi.user:a", "pi.assistant", "pi.tool-result", "pi.reset:continue here"], Transcript(entries), "transcript");
            Equal(entries[^1].Id, entries[^1].Head, "reset head");
            Equal(1, setup.Faux.State.CallCount, "calls");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "drops-an-onyield-continuation-when-the-final-boundary-selects-a-follow-up", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("second"));
            var yields = 0;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) => Task.FromResult<JsonValue?>(Interlocked.Increment(ref yields) == 1 ? (JsonValue)"more" : null),
            });
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            var followUp = await Submit(root, "f");
            first.Release();
            await followUp.WaitAsync(Context);
            Done(await Status(input), null, "input");
            Seq(["pi.user:a", "pi.assistant:first", "pi.user:f", "pi.assistant:second"], Transcript(await AllEntries(root)), "transcript");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "withdraws-a-queued-submission-and-removes-its-item", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            var kept = await Write(root, QueuedNote("kept"));
            var withdrawn = await Submit(root, "f");
            Equal(SubmissionAbortResult.Aborted, await withdrawn.AbortAsync(Context), "abort");
            Unanswered(await withdrawn.WaitAsync(Context), "aborted", "withdrawn");
            Seq([$"{kept.Id} write"], await InboxItems(harness, root), "inbox");
            first.Release();
            await input.WaitAsync(Context);
            Done(await Status(kept), null, "kept");
            Equal(1, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "leaves-queued-items-after-a-failed-run-until-the-next-submission-places-them-in-order", async () =>
        {
            var setup = Setup();
            var failing = Gated(FauxAssistantMessage(Array.Empty<JsonObjectValue>(), stopReason: "error", errorMessage: "invalid request"));
            setup.Faux.SetResponses(failing.Step, Answer("for f"), Answer("for g"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await failing.Reached;
            var f = await Submit(root, "f");
            failing.Release();
            Unanswered(await input.WaitAsync(Context), "model_error", "input");
            await harness.WaitForIdleAsync(Context);
            Equal(SubmissionStatus.Queued, (await Status(f)).Status, "f queued");
            Seq([$"{f.Id} followUp"], await InboxItems(harness, root), "inbox");

            // Idle with a queued item: the new input queues behind it, and a final boundary places the older one first.
            var g = await Submit(root, "g", WhenBusy.Reject);
            await g.WaitAsync(Context);
            Done(await Status(f), null, "f");
            Seq(["pi.user:f", "pi.assistant:for f", "pi.user:g", "pi.assistant:for g"], Transcript(await AllEntries(root)).TakeLast(4), "transcript");
            await harness.CloseAsync(Context);
        });

        if (reopenable is not null)
            yield return Case(prefix + "keeps-queued-submissions-across-reopen-and-settles-them-afterwards", async () =>
            {
                var open = reopenable();
                var setup = Setup();
                var first = Gated(Answer("first"));
                setup.Faux.SetResponses(first.Step, Answer("first again"), Answer("f"));
                var opened = await OpenChat(open(), setup);
                await Submit(opened.Root, "a");
                await first.Reached;
                var f = (await Submit(opened.Root, "f")).Id;
                await opened.Harness.CloseAsync(Context);

                opened = await OpenChat(open(), setup);
                var settled = await (await opened.Harness.SubmissionAsync(f, Context))!.WaitAsync(Context);
                Done(settled, null, "settled");
                Seq(["pi.user:f", "pi.assistant:f"], Transcript(await AllEntries(opened.Root)).TakeLast(2), "transcript");
                await opened.Harness.CloseAsync(Context);
            });

        yield return Case(prefix + "commits-inbox-changes-as-positional-chord-operations-and-a-base-when-empty", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("second"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var ops = new SyncList<JsonValue>();
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                    if (change.Record.Kind == "pi.inbox" && change.Ops.Length > 0) ops.Add(J(OpsJson(change.Ops)));
            });
            var input = await Submit(root, "a");
            await first.Reached;
            var w1 = await Write(root, QueuedNote("w"));
            var f1 = await Submit(root, "f1");
            var w2 = await Write(root, QueuedNote("w"));
            var f2 = await Submit(root, "f2");
            var w3 = await Write(root, QueuedNote("w"));
            const string note = """{"kind":"note","model":[{"role":"user","content":"w","timestamp":0}]}""";
            var pushed = ops.Snapshot();
            Equal(5, pushed.Count, "pushes");
            SameJson($$$"""[["p",["items"],0,0,[{"id":{{{w1.Id}}},"mode":"write","entry":{{{note}}}}]]]""", pushed[0], "w1");
            SameJson($$$"""[["p",["items"],1,0,[{"id":{{{f1.Id}}},"mode":"followUp","content":"f1"}]]]""", pushed[1], "f1");
            SameJson($$$"""[["p",["items"],2,0,[{"id":{{{w2.Id}}},"mode":"write","entry":{{{note}}}}]]]""", pushed[2], "w2");
            SameJson($$$"""[["p",["items"],3,0,[{"id":{{{f2.Id}}},"mode":"followUp","content":"f2"}]]]""", pushed[3], "f2");
            SameJson($$$"""[["p",["items"],4,0,[{"id":{{{w3.Id}}},"mode":"write","entry":{{{note}}}}]]]""", pushed[4], "w3");
            first.Release();
            await input.WaitAsync(Context);
            await Delivered(harness);
            // Every write and the first follow-up leave; only f2 at index 3 remains. No retained value is carried.
            var removal = (JsonArrayValue)ops[5];
            Check(removal.All(op => op is JsonArrayValue { Count: 5 } item && item[0] is JsonStringValue { Value: "p" } &&
                item[4] is JsonArrayValue { Count: 0 }), $"removal: {removal.ToJson()}");
            Check(removal.Any(op => JsonValue.DeepEquals(op, J("""["p",["items"],4,1,[]]"""))), $"removal of w3: {removal.ToJson()}");
            Check(!removal.ToJson().Contains("f2", StringComparison.Ordinal), $"removal carries f2: {removal.ToJson()}");
            await f2.WaitAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "settles-a-stale-write-at-once-while-idle-and-a-queued-one-behind-waiting-items", async () =>
        {
            var setup = Setup();
            var failing = Gated(FauxAssistantMessage(Array.Empty<JsonObjectValue>(), stopReason: "error", errorMessage: "invalid request"));
            setup.Faux.SetResponses(failing.Step, Answer("for f"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var old = await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            await root.ResetAsync(null, Context);
            var before = (await AllEntries(root)).Count;
            var idle = await Write(root, new EntryDraft { Kind = "summary", Head = EntryHead.At(old.Id) });
            Unanswered(await idle.WaitAsync(Context), "stale", "idle");
            Equal(before, (await AllEntries(root)).Count, "entries");

            // After a failed run, a follow-up waits; a stale write queues behind it and the boundary rejects it.
            var input = await Submit(root, "a");
            await failing.Reached;
            var f = await Submit(root, "f");
            failing.Release();
            await input.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            var queued = await Write(root, new EntryDraft { Kind = "summary", Head = EntryHead.At(old.Id) });
            Unanswered(await queued.WaitAsync(Context), "stale", "queued");
            Done(await f.WaitAsync(Context), null, "f");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-an-onyield-continuation-across-a-queued-plain-write-with-the-run-s-original-input", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("second"));
            var yields = 0;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) => Task.FromResult<JsonValue?>(Interlocked.Increment(ref yields) == 1 ? (JsonValue)"more" : null),
            });
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            var write = await Write(root, QueuedNote("w"));
            first.Release();
            await input.WaitAsync(Context);
            var entries = await AllEntries(root);
            Seq(["pi.user:a", "pi.assistant:first", "note:w", "pi.user:more", "pi.assistant:second"], Transcript(entries), "transcript");
            Done(await Status(input), entries[^1].Id, "input");
            Done(await Status(write), null, "write");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "drops-an-onyield-continuation-for-a-queued-reset", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks { OnYield = (_, _, _) => Task.FromResult<JsonValue?>("more") });
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            await root.ResetAsync(null, Context);
            first.Release();
            await input.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            Done(await Status(input), null, "input");
            Seq(["pi.user:a", "pi.assistant:first", "pi.reset"], Transcript(await AllEntries(root)), "transcript");
            Equal(1, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "adds-every-steer-to-the-run-at-the-posttools-boundary-with-steeringmode-all", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HoldTool(setup, gate);
            setup.Faux.SetResponses(Hold, Answer("after tools"), Answer("follow-up"));
            var (harness, root) = await OpenChat(fresh(), setup);
            setup.SteeringMode = QueueMode.All;
            var input = await Submit(root, "a");
            await ToolRunning(harness, root);
            var s1 = await Submit(root, "s1", WhenBusy.Steer);
            var f = await Submit(root, "f");
            var s2 = await Submit(root, "s2", WhenBusy.Steer);
            gate.Resolve();
            await f.WaitAsync(Context);
            var entries = await AllEntries(root);
            Seq(["pi.user:a", "pi.assistant", "pi.tool-result", "pi.user:s1", "pi.user:s2", "pi.assistant:after tools", "pi.user:f", "pi.assistant:follow-up"],
                Transcript(entries), "transcript");
            var answers = Assistants(entries);
            foreach (var submission in new[] { input, s1, s2 }) Done(await Status(submission), answers[1].Id, $"submission {submission.Id}");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "queues-an-idle-steer-behind-waiting-items-and-places-it-with-the-first-follow-up-in-id-order", async () =>
        {
            var setup = Setup();
            var failing = Gated(FauxAssistantMessage(Array.Empty<JsonObjectValue>(), stopReason: "error", errorMessage: "invalid request"));
            setup.Faux.SetResponses(failing.Step, Answer("both"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await failing.Reached;
            var f = await Submit(root, "f");
            failing.Release();
            await input.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            var steer = await Submit(root, "s", WhenBusy.Steer);
            var settled = await steer.WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "steer");
            Done(await Status(f), settled.Answer, "f");
            Seq(["pi.user:f", "pi.user:s", "pi.assistant:both"], Transcript(await AllEntries(root)).TakeLast(3), "transcript");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "starts-a-queued-follow-up-after-a-terminating-round", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HoldTool(setup, gate, Empty with { Control = new ToolControl { Terminate = true } });
            setup.Faux.SetResponses(Hold, Answer("follow-up"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await ToolRunning(harness, root);
            var f = await Submit(root, "f");
            gate.Resolve();
            await f.WaitAsync(Context);
            var calling = (await AllEntries(root)).First(entry => entry.Kind == "pi.assistant");
            Done(await Status(input), calling.Id, "input");
            Seq(["pi.user:a", "pi.assistant", "pi.tool-result", "pi.user:f", "pi.assistant:follow-up"], Transcript(await AllEntries(root)), "transcript");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "writes-the-last-handoff-in-call-order-and-then-runs-queued-follow-ups-in-the-new-context", async () =>
        {
            var setup = Setup();
            var firstGate = new HarnessTests.Deferred();
            var secondGate = new HarnessTests.Deferred();
            HoldTool(setup, firstGate, Empty with { Control = new ToolControl { Handoff = "one" } });
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "later",
                Description = "Finishes first",
                Parameters = NoParameters,
                Execute = async (_, _, _) =>
                {
                    await secondGate.Task;
                    return Empty with { Control = new ToolControl { Handoff = "two" } };
                },
            }));
            var round = FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1"), FauxToolCall("later", JsonObjectValue.Empty, "c2")],
                stopReason: "toolUse");
            setup.Faux.SetResponses(round, Answer("follow-up"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await ToolRunning(harness, root);
            var f = await Submit(root, "f");
            secondGate.Resolve();
            await Eventually(async () => await LiveAt(harness, root, "tools", 1, "status") is JsonStringValue { Value: "done" });
            firstGate.Resolve();
            await f.WaitAsync(Context);
            Done(await Status(input), null, "input");
            Seq(["pi.tool-result", "pi.reset:two", "pi.user:f", "pi.assistant:follow-up"], Transcript(await AllEntries(root)).TakeLast(4), "transcript");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "leaves-the-inbox-alone-when-the-run-s-task-is-aborted", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("never"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(fresh(), setup);
            var input = await Submit(root, "a");
            await first.Reached;
            var f = await Submit(root, "f");
            var taskId = new TaskId((long)N(await LiveAt(harness, root, "run", "taskId")));
            await harness.AbortTaskAsync(taskId, Context);
            Unanswered(await input.WaitAsync(Context), "aborted", "input");
            await harness.WaitForIdleAsync(Context);
            Equal(SubmissionStatus.Queued, (await Status(f)).Status, "f");
            Seq([$"{f.Id} followUp"], await InboxItems(harness, root), "inbox");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "returns-a-queued-submission-for-its-repeated-request-id-without-a-second-item", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(fresh(), setup);
            await Submit(root, "a");
            await first.Reached;
            var queued = await Submit(root, "f", requestId: "r");
            var again = await Submit(root, "f", requestId: "r");
            Equal(queued.Id, again.Id, "deduplicated");
            Seq([$"{queued.Id} followUp"], await InboxItems(harness, root), "inbox");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "withdraws-a-middle-item-with-one-positional-removal", async () =>
        {
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step);
            var (harness, root) = await OpenChat(fresh(), setup);
            await Submit(root, "a");
            await first.Reached;
            var items = new List<ISubmission>();
            foreach (var text in new[] { "x", "y", "z" }) items.Add(await Submit(root, text));
            var ops = new SyncList<string>();
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                    if (change.Record.Kind == "pi.inbox" && change.Ops.Length > 0) ops.Add(OpsJson(change.Ops));
            });
            await items[1].AbortAsync(Context);
            Equal(1, ops.Count, "inbox changes");
            SameJson("""[["p",["items"],1,1,[]]]""", J(ops[0]), "removal");
            Seq([$"{items[0].Id} followUp", $"{items[2].Id} followUp"], await InboxItems(harness, root), "inbox");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "stores-the-inbox-as-a-base-exactly-when-it-becomes-empty", async () =>
        {
            var storage = new RecordingStorage(fresh());
            var setup = Setup();
            var first = Gated(Answer("first"));
            setup.Faux.SetResponses(first.Step, Answer("second"));
            var (harness, root) = await OpenChat(storage, setup);
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                    if (change.Record.Kind == "pi.inbox") storage.InboxId = change.Record.Id;
            });
            var input = await Submit(root, "a");
            await first.Reached;
            await Submit(root, "f1");
            var f2 = await Submit(root, "f2");
            first.Release();
            await input.WaitAsync(Context);
            await f2.WaitAsync(Context);
            // The inbox ID is learned from the f1 push; later writes: the f2 push, removing f1, and emptying the inbox.
            Seq(["delta", "delta", "base empty"], storage.Written.Snapshot(), "inbox writes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-complete-inbox-base-exactly-while-it-is-empty-and-a-usage-base-on-every-change", () =>
        {
            var info = new CheckpointInfo(1000);
            var item = O("""{"id":1,"mode":"followUp","content":"x"}""");
            Check(Inbox.InboxDoc.Definition.CheckpointWhen!(O("""{"items":[]}"""), [], info), "empty inbox");
            Check(!Inbox.InboxDoc.Definition.CheckpointWhen!(Obj(("items", JsonValue.Array(item))), [], info), "inbox with an item");
            Check(UsageLedger.UsageDoc.Definition.CheckpointWhen!(O("""{"models":{},"tools":{}}"""), [], info), "usage");
        });

        // ─── usage ──────────────────────────────────────────────────────────

        yield return Case(prefix + "usage.totals-assistant-usage-per-model-and-tool-usage-per-tool-and-sums-the-session", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            gate.Resolve();
            HoldTool(setup, gate, Empty with { Usage = Usage1 });
            setup.Faux.SetResponses(Hold, Answer("done"), Answer("other"));
            var (harness, root) = await OpenChat(fresh(), setup);
            await (await Submit(root, "a")).WaitAsync(Context);
            var entries = await AllEntries(root);
            var assistants = Assistants(entries).Select(entry => (JsonObjectValue)((JsonObjectValue)entry.Model![0])["usage"]).ToList();
            double Sum(string field) => assistants.Sum(usage => N(usage[field]));
            var usage = (await harness.SnapshotAsync(UsageLedger.UsageDoc, root.Id, Context))!;
            MatchJson($$"""{"input":{{Sum("input")}},"output":{{Sum("output")}},"totalTokens":{{Sum("totalTokens")}}}""",
                ((JsonObjectValue)usage["models"])["faux/faux-1"], "model usage");
            SameJson($$"""{"hold":{{Usage1.ToJson()}}}""", usage["tools"], "tool usage");
            var result = (JsonObjectValue)entries.First(entry => entry.Kind == "pi.tool-result").Model![0];
            SameJson(Usage1.ToJson(), result["usage"], "result usage");

            // A fork starts at zero; the Session total adds every conversation once.
            var fork = await root.ForkAsync(entries[^1].Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            SameJson("""{"models":{},"tools":{}}""", await harness.SnapshotAsync(UsageLedger.UsageDoc, fork.Id, Context), "fork usage");
            await (await Submit(fork, "b")).WaitAsync(Context);
            var forkUsage = (JsonObjectValue)((JsonObjectValue)(await harness.SnapshotAsync(UsageLedger.UsageDoc, fork.Id, Context))!["models"])["faux/faux-1"];
            var total = await harness.UsageAsync(Context);
            Equal(Sum("output") + N(forkUsage["output"]), N(At(total, "models", "faux/faux-1", "output")), "total output");
            SameJson($$"""{"hold":{{Usage1.ToJson()}}}""", total["tools"], "total tools");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "usage.counts-failed-attempts-converted-partials-and-tool-usage-replaced-by-aftertool", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 200, MinTokenSize = 1, MaxTokenSize = 1 });
            var spent = O("""{"input":5,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":5,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""");
            var gate = new HarnessTests.Deferred();
            gate.Resolve();
            HoldTool(setup, gate);
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (_, result, _, _) => Task.FromResult<ToolExecutionResult?>(result with { Usage = spent }),
            });
            var retryable = FauxAssistantMessage(Array.Empty<JsonObjectValue>(), stopReason: "error", errorMessage: "503 Service Unavailable");
            setup.Faux.SetResponses(retryable, Hold, Answer("done"), Answer(new string('x', 400)));
            var (harness, root) = await OpenChat(fresh(), setup);
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 1, BaseDelayMs = 1 };
            await (await Submit(root, "a")).WaitAsync(Context);
            SameJson($$"""{"hold":{{spent.ToJson()}}}""", (await harness.SnapshotAsync(UsageLedger.UsageDoc, root.Id, Context))!["tools"], "tool usage");

            // A partial committed while streaming becomes an aborted entry on abort; its usage counts too.
            var input = await Submit(root, "b");
            await Eventually(async () => await LiveAt(harness, root, "generation", "message") is not null);
            await harness.AbortTaskAsync(new TaskId((long)N(await LiveAt(harness, root, "run", "taskId"))), Context);
            await input.WaitAsync(Context);
            var assistants = Assistants(await AllEntries(root)).Select(entry => (JsonObjectValue)entry.Model![0]).ToList();
            Seq(["error", "toolUse", "stop", "aborted"], assistants.Select(message => S(message["stopReason"])), "stop reasons");
            var output = assistants.Sum(message => N(At(message, "usage", "output")));
            Equal(output, N(At((await harness.SnapshotAsync(UsageLedger.UsageDoc, root.Id, Context))!, "models", "faux/faux-1", "output")), "output");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "usage.keeps-tools-named-like-object-prototype-keys-in-the-ledger-and-the-session-total", async () =>
        {
            var (harness, root) = await OpenChat(fresh(), Setup());
            var usage = O("""{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"totalTokens":2,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""");
            string[] names = ["constructor", "__proto__", "toString"];
            foreach (var name in names)
            {
                await root.CommitAsync(tx => UsageLedger.RecordUsageAsync(tx, root.Id, "tools", name, usage), Context);
                await root.CommitAsync(tx => UsageLedger.RecordUsageAsync(tx, root.Id, "tools", name, usage), Context);
            }
            var total = await harness.UsageAsync(Context);
            var tools = (JsonObjectValue)total["tools"];
            Seq(names, tools.Keys, "keys");
            foreach (var name in names) Equal(2.0, N(At(tools, name, "output")), name);
            await harness.CloseAsync(Context);
        });

        if (reopenable is not null)
            yield return Case(prefix + "usage.keeps-usage-totals-exact-across-reopen-counting-a-partial-converted-after-reopen-once", async () =>
            {
                var open = reopenable();
                var setup = Setup(new FauxOptions { TokensPerSecond = 200, MinTokenSize = 1, MaxTokenSize = 1 });
                setup.Faux.SetResponses(Answer("first"), Answer(new string('x', 400)), Answer("again"));
                var opened = await OpenChat(open(), setup);
                await (await Submit(opened.Root, "a")).WaitAsync(Context);
                await Submit(opened.Root, "b");
                var live = opened;
                await Eventually(async () => await LiveAt(live.Harness, live.Root, "generation", "message") is not null);
                // Closing mid-stream keeps the committed partial; the reopened request converts it into an aborted entry.
                await opened.Harness.CloseAsync(Context);
                opened = await OpenChat(open(), setup);
                opened.Harness.Resume();
                await opened.Harness.WaitForIdleAsync(Context);
                var assistants = Assistants(await AllEntries(opened.Root)).Select(entry => (JsonObjectValue)entry.Model![0]).ToList();
                Seq(["stop", "aborted", "stop"], assistants.Select(message => S(message["stopReason"])), "stop reasons");
                var total = (JsonObjectValue)At(await opened.Harness.UsageAsync(Context), "models", "faux/faux-1");
                foreach (var field in new[] { "input", "output", "totalTokens" })
                    Equal(assistants.Sum(message => N(At(message, "usage", field))), N(total[field]), field);
                await opened.Harness.CloseAsync(Context);
            });

        yield return Case(prefix + "usage.records-usage-as-numeric-sets-on-the-ledger-in-the-entry-s-commit", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(Answer("one"), Answer("two"));
            var (harness, root) = await OpenChat(fresh(), setup);
            var commits = new SyncList<(int Entries, JsonArrayValue Ops)>();
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                {
                    if (change.Record.Kind != "pi.usage") continue;
                    var entries = publication.Changes.Count(other => other.Type == "entry");
                    commits.Add((entries, (JsonArrayValue)J(OpsJson(change.Ops))));
                }
            });
            await (await Submit(root, "a")).WaitAsync(Context);
            await (await Submit(root, "b")).WaitAsync(Context);
            await Delivered(harness);
            var recorded = commits.Snapshot();
            Equal(2, recorded.Count, "usage commits");
            var firstOps = recorded[0].Ops;
            Check(firstOps.Count == 1 && firstOps[0] is JsonArrayValue { Count: 3 } set && JsonValue.DeepEquals(set[0], "s") &&
                JsonValue.DeepEquals(set[1], J("""["models","faux/faux-1"]""")) && set[2] is JsonObjectValue, $"first ops: {firstOps.ToJson()}");
            Check(recorded[1].Ops.All(op => op is JsonArrayValue item && JsonValue.DeepEquals(item[0], "s") &&
                item[1] is JsonArrayValue path && JsonValue.DeepEquals(path[1], "faux/faux-1")), $"second ops: {recorded[1].Ops.ToJson()}");
            Check(recorded.All(commit => commit.Entries >= 1), "entry commits");
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>Records each <c>pi.inbox</c> change write once its ID is known (upstream <c>RecordingStorage</c>): "delta", "base", or
    /// "base empty".</summary>
    private sealed class RecordingStorage(IStorage inner) : IStorage
    {
        private readonly Lock gate = new();
        private DocumentId? inboxId;

        public DocumentId? InboxId
        {
            get { lock (gate) return inboxId; }
            set { lock (gate) inboxId = value; }
        }
        public SyncList<string> Written { get; } = new();

        public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
        {
            foreach (var write in writes)
            {
                if (write is not DocumentChangeWrite change || change.Id != InboxId) continue;
                Written.Add(change.Content switch
                {
                    DocumentBase { Value: var value } when value["items"] is JsonArrayValue { Count: 0 } => "base empty",
                    DocumentBase => "base",
                    _ => "delta",
                });
            }
            return inner.CommitAsync(writes, context);
        }

        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            inner.FindDocumentAsync(address, at, context);
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
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanDocumentsAsync(query, limit, cursor, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
    }
}
