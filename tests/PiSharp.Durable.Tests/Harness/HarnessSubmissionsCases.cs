// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-submissions.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - Backends. Upstream runs these on ControlledStorage over memory and reopens a SQLite file for the reopen case. harness.submissions.*
//   does the same; harness.submissions.jsonl.* and harness.submissions.sqlite.* rerun the storage-agnostic cases on ControlledStorage over a
//   fresh JSONL directory and a fresh SQLite file, and harness.submissions.jsonl.* reopens a JSONL directory for the reopen case.
// - `class HeldReads extends ControlledStorage { override submission() }` is HeldReads, a decorator over ControlledStorage.
// - `{ ...context, abortSignal: controller.signal }` is a DurableContext.WithCancel() context; `controller.abort(new Error(text))` cancels it
//   with that exception as the reason.
// - `defineEntry<{ n: number }>` is a JsonObjectValue-typed entry kind; `const n: number = counter.data.n` reads the number.
using System.Collections.Immutable;
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
    private static IEnumerable<(string, Func<Task>)> HarnessSubmissionsCases()
    {
        static Func<IStorage> SqliteFile()
        {
            var path = Path.Combine(Temp("submissions-reopen-sqlite"), "session.sqlite");
            return () => SqliteStorage.OpenAsync(path).GetAwaiter().GetResult();
        }
        static Func<IStorage> JsonlDirectory()
        {
            var directory = Temp("submissions-reopen-jsonl");
            return () => JsonlStorage.OpenAsync(directory, DurableContext.Background).GetAwaiter().GetResult();
        }
        return
        [
            .. SubmissionsTests.Cases("harness.submissions.", () => new MemoryStorage(), SqliteFile),
            .. SubmissionsTests.Cases("harness.submissions.jsonl.",
                () => JsonlStorage.OpenAsync(Temp("submissions-jsonl"), DurableContext.Background).GetAwaiter().GetResult(), JsonlDirectory),
            .. SubmissionsTests.Cases("harness.submissions.sqlite.",
                () => SqliteStorage.OpenAsync(Path.Combine(Temp("submissions-sqlite"), "session.sqlite")).GetAwaiter().GetResult(), null),
        ];
    }
}

/// <summary>The harness.submissions.* cases (upstream harness-submissions.test.ts).</summary>
internal static class SubmissionsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static Task<ISubmission> Submit(IConversation conversation, string text, WhenBusy? whenBusy = null, string? requestId = null) =>
        conversation.SubmitAsync(SubmissionDraft.Input(text, whenBusy, requestId), Context);

    private static Task<ISubmission> Write(IConversation conversation, EntryDraft entry, string? requestId = null) =>
        conversation.SubmitAsync(SubmissionDraft.Write(entry, requestId), Context);

    /// <param name="fresh">A fresh storage for one case.</param>
    /// <param name="reopenable">A fresh reopenable location for the reopen case; null skips it.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh, Func<Func<IStorage>>? reopenable)
    {
        ControlledStorage Controlled() => new(fresh());

        yield return Case(prefix + "appends-an-idle-write-and-settles-it-done-without-a-turn", async () =>
        {
            var (harness, root) = await OpenChat(Controlled(), Setup());
            var submission = await Write(root, new EntryDraft { Kind = "note", Data = O("""{"text":"x"}""") });
            var settled = await submission.WaitAsync(Context);
            MatchExactly($$"""{"id":{{submission.Id}},"conversationId":{{root.Id}},"type":"write","status":"done","entry":"<number>"}""",
                settled.ToJson(), "settled");
            var entries = await AllEntries(root);
            SameJson($$$"""[{"id":{{{settled.Entry}}},"conversationId":{{{root.Id}}},"kind":"note","data":{"text":"x"}}]""",
                JsonValue.Array(entries.Select(entry => (JsonValue)entry.ToJson())), "entries");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            var tasks = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 10), Context);
            Equal(0, tasks.Items.Count(), "tasks");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "places-idle-input-and-rejects-busy-input-with-whenbusy-reject-without-writing", async () =>
        {
            var storage = Controlled();
            var setup = Setup();
            setup.Now = () => 42;
            var busy = Unanswered();
            setup.Faux.SetResponses(busy.Step);
            var (harness, root) = await OpenChat(storage, setup);
            var submission = await Submit(root, "hi");
            await busy.Reached;
            var record = await submission.StatusAsync(Context);
            if (record.Status != SubmissionStatus.Placed) throw new InvalidOperationException($"Unexpected {record.Status}");
            var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.UserEntry, record.Entry!.Value), Context);
            SameJson("""[{"role":"user","content":"hi","timestamp":42}]""", entry?.Model, "user entry");
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
            var run = (JsonObjectValue)live!["run"];
            SameJson($"[{submission.Id}]", run["inputs"], "run inputs");
            Equal("pi.generation", (await harness.GetTaskAsync(new TaskId((long)N(run["taskId"])), Context))?.Kind, "run task");

            var commits = storage.CommitCount;
            var rejected = await ThrowsAsync<ConversationBusyException>(() => Submit(root, "again", WhenBusy.Reject), "busy input");
            Equal(root.Id, rejected.ConversationId, "busy conversation");
            Equal(commits, storage.CommitCount, "commits");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "deduplicates-request-ids-per-conversation-before-any-write", async () =>
        {
            var storage = Controlled();
            var setup = Setup();
            var busy = Unanswered();
            setup.Faux.SetResponses(busy.Step);
            var (harness, root) = await OpenChat(storage, setup);
            var first = await Submit(root, "hi", requestId: "r1");
            await busy.Reached;
            var commits = storage.CommitCount;
            // Deduplication runs before the busy check.
            var again = await Submit(root, "different", requestId: "r1");
            Equal(first.Id, again.Id, "deduplicated");
            Equal(commits, storage.CommitCount, "commits");
            await RejectsAsync(() => Write(root, new EntryDraft { Kind = "note" }, "r1"), "Request r1 already identifies a submission of type input", "write");
            MatchJson("""{"requestId":"r1","status":"placed"}""", (await first.StatusAsync(Context)).ToJson(), "first");

            var other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var write = await Write(other, new EntryDraft { Kind = "note" }, "r1");
            Check(write.Id != first.Id, "another conversation's request ID");
            Equal(write.Id, (await Write(other, new EntryDraft { Kind = "note" }, "r1")).Id, "deduplicated write");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-abort-results-and-looks-submissions-up-by-conversation", async () =>
        {
            var setup = Setup();
            var release = new HarnessTests.Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                await release.Task;
                return FauxAssistantMessage("answer");
            }));
            var (harness, root) = await OpenChat(Controlled(), setup);
            var submission = await Submit(root, "hi");
            Equal(SubmissionAbortResult.AlreadyPlaced, await submission.AbortAsync(Context), "abort placed");
            Equal(SubmissionAbortResult.AlreadyPlaced, await harness.AbortSubmissionAsync(submission.Id, Context, root.Id), "abort by conversation");
            var other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            Equal(SubmissionAbortResult.NotFound, await harness.AbortSubmissionAsync(submission.Id, Context, other.Id), "other conversation");
            Equal(SubmissionAbortResult.NotFound, await harness.AbortSubmissionAsync(new SubmissionId(999_999), Context), "unknown");
            Check(await harness.SubmissionAsync(new SubmissionId(999_999), Context) is null, "unknown lookup");

            release.Resolve();
            await submission.WaitAsync(Context);
            Equal(SubmissionAbortResult.Settled, await submission.AbortAsync(Context), "abort settled");
            Equal(SubmissionAbortResult.Settled, await harness.AbortSubmissionAsync(submission.Id, Context), "abort settled by id");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cancels-only-a-wait-and-rejects-pending-waits-on-close", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(Unanswered().Step);
            var (harness, root) = await OpenChat(Controlled(), setup);
            var submission = await Submit(root, "hi");
            var controller = Context.WithCancel();
            var cancelled = submission.WaitAsync(controller.Context);
            var pending = submission.WaitAsync(Context);
            controller.Cancel(new InvalidOperationException("stop waiting"));
            await RejectsAsync(() => cancelled, "stop waiting", "cancelled wait");
            Equal(SubmissionStatus.Placed, (await submission.StatusAsync(Context)).Status, "status");
            await harness.CloseAsync(Context);
            await RejectsAsync(() => pending, "Harness is closed", "pending wait");
        });

        yield return Case(prefix + "rejects-a-wait-whose-submission-read-spans-the-start-of-close", async () =>
        {
            var storage = new HeldReads(Controlled());
            var setup = Setup();
            setup.Faux.SetResponses(Unanswered().Step);
            var (harness, root) = await OpenChat(storage, setup);
            var submission = await Submit(root, "hi");
            storage.Hold = true;
            var waiting = submission.WaitAsync(Context);
            await storage.Entered.Task;
            var closing = harness.CloseAsync(Context);
            storage.Release.Resolve();
            await RejectsAsync(() => waiting, "Harness is closed", "wait");
            await closing;
        });

        if (reopenable is not null)
            yield return Case(prefix + "reacquires-a-submission-after-reopen-and-settles-it-durably", async () =>
            {
                var open = reopenable();
                var setup = Setup();
                // The first process never answers; the reopened one does.
                var busy = Unanswered();
                setup.Faux.SetResponses(busy.Step, FauxAssistantMessage("after reopen"));
                var opened = await OpenChat(open(), setup);
                var id = (await Submit(opened.Root, "hi", requestId: "print")).Id;
                await busy.Reached;
                await opened.Harness.CloseAsync(Context);

                opened = await OpenChat(open(), setup);
                var submission = (await opened.Harness.SubmissionAsync(id, Context))!;
                Equal(SubmissionStatus.Placed, (await submission.StatusAsync(Context)).Status, "status after reopen");
                opened.Harness.Resume();
                var settled = await submission.WaitAsync(Context);
                if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input) throw new InvalidOperationException($"Unexpected {settled.Status}");
                await opened.Harness.CloseAsync(Context);

                opened = await OpenChat(open(), setup);
                SameJson(settled.ToJson().ToJson(), (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).ToJson(), "settled");
                var again = await Submit(opened.Root, "hi", requestId: "print");
                Equal(id, again.Id, "deduplicated after reopen");
                await opened.Harness.CloseAsync(Context);
            });

        yield return Case(prefix + "enables-scheduling-when-a-caller-submits-or-waits", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var (harness, root) = await OpenChat(Controlled(), setup);
            // No Resume(): submitting asks for progress.
            Equal(SubmissionStatus.Done, (await (await Submit(root, "hi")).WaitAsync(Context)).Status, "status");
            await harness.CloseAsync(Context);

            var passive = await OpenChat(Controlled(), Setup());
            var taskId = await passive.Root.CommitAsync(tx => tx.CreateTaskAsync(HarnessTasks.GenerationTask, JsonObjectValue.Empty,
                new TaskOptions(TaskOwnership.Conversation)), Context);
            // A committed task alone does not start scheduling; waiting for it does.
            Equal(DurableTaskStatus.Pending, (await passive.Harness.GetTaskAsync(taskId, Context))?.State.Status, "pending");
            Equal(DurableTaskStatus.Terminal, (await passive.Harness.WaitForTaskAsync(taskId, Context)).State.Status, "terminal");
            await passive.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "settles-submissions-by-their-current-record-in-the-transaction", async () =>
        {
            var (harness, root) = await OpenChat(Controlled(), Setup());
            var session = (DurableHarness)harness;
            var entry = await root.CommitAsync(async tx => (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" })).Id, Context);
            Task<SubmissionId> Create(SubmissionCreate record) => session.CommitWithAsync(async tx => (await tx.CreateSubmissionAsync(record)).Id, Context);
            var queued = await Create(new SubmissionCreate { ConversationId = root.Id, Type = SubmissionType.Input, Status = SubmissionStatus.Queued });
            var write = await Create(new SubmissionCreate { ConversationId = root.Id, Type = SubmissionType.Write, Status = SubmissionStatus.Queued });
            var answer = SubmissionSettlement.Done(entry);
            await RejectsAsync(() => root.CommitAsync(tx => tx.SettleSubmission(queued, answer), Context), "is not a placed input", "queued input");
            await RejectsAsync(() => root.CommitAsync(tx => tx.SettleSubmission(write, answer), Context), "is not a placed input", "queued write");
            await RejectsAsync(() => root.CommitAsync(tx => tx.SettleSubmission(new SubmissionId(999_999), SubmissionSettlement.Unanswered("x")), Context),
                "does not exist", "unknown");

            // A submission created earlier in the same commit settles; a second settlement leaves the first.
            var placed = await session.CommitWithAsync(async tx =>
            {
                var created = await tx.CreateSubmissionAsync(new SubmissionCreate
                {
                    ConversationId = root.Id, Type = SubmissionType.Input, Status = SubmissionStatus.Placed, Entry = entry,
                });
                tx.SettleSubmission(created.Id, answer);
                tx.SettleSubmission(created.Id, SubmissionSettlement.Unanswered("late"));
                return created.Id;
            }, Context);
            MatchJson($$"""{"status":"done","entry":{{entry}},"answer":{{entry}}}""",
                (await (await harness.SubmissionAsync(placed, Context))!.StatusAsync(Context)).ToJson(), "placed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "appends-and-reads-typed-entries-through-tokens", async () =>
        {
            var counterKind = Entries.DefineEntry<JsonObjectValue>("app.counter");
            var markerKind = Entries.DefineEntry("app.marker");
            var (harness, root) = await OpenChat(Controlled(), Setup());
            var counter = await root.CommitAsync(tx => tx.AppendEntryAsync(counterKind, root.Id, new TypedEntryDraft<JsonObjectValue> { Data = O("""{"n":1}""") }),
                Context);
            var n = N(counter.Data["n"]);
            Equal(1.0, n, "n");
            SameJson($$$"""{"id":{{{counter.Id}}},"conversationId":{{{root.Id}}},"kind":"app.counter","data":{"n":1}}""", counter.Record.ToJson(), "counter");
            var marker = await root.CommitAsync(tx => tx.AppendEntryAsync(markerKind, root.Id, new TypedEntryDraft()), Context);
            Equal("app.marker", marker.Kind, "marker kind");
            var read = await root.CommitAsync(tx => tx.GetEntryAsync(counterKind, counter.Id), Context);
            SameJson(counter.Record.ToJson().ToJson(), read?.Record.ToJson(), "read counter");
            Check(await root.CommitAsync(tx => tx.GetEntryAsync(markerKind, counter.Id), Context) is null, "marker of a counter");
            Check(await root.CommitAsync(tx => tx.GetEntryAsync(counterKind, new EntryId(999_999)), Context) is null, "unknown counter");
            Check(counterKind.Is(counter.Record), "counter guard");
            Check(!Entries.AssistantEntry.Is(counter.Record), "assistant guard");
            Seq(["pi.user", "pi.assistant"], new[] { Entries.UserEntry.Kind, Entries.AssistantEntry.Kind }, "built-in kinds");
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>Holds the first submission read after <see cref="Hold"/> is set until <see cref="Release"/> (upstream <c>HeldReads</c>).</summary>
    private sealed class HeldReads(ControlledStorage inner) : IStorage
    {
        public volatile bool Hold;
        public HarnessTests.Deferred Entered { get; } = new();
        public HarnessTests.Deferred Release { get; } = new();

        public async Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context)
        {
            if (Hold)
            {
                Entered.Resolve();
                await Release.Task;
            }
            return await inner.GetSubmissionAsync(id, context);
        }

        public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) => inner.CommitAsync(writes, context);
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
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanDocumentsAsync(query, limit, cursor, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
    }
}
