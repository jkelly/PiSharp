// Authored for PiSharp: `pisharp durable` on the pi-durable 1.2.0 library (durable work list area l; upstream deleted its durable agent in
// v1.2.0, so these cases have no upstream twin). A storage error fails the session and the view says so; a failed compaction is recorded in
// pi.live.compactionFailure, not reported twice; tool_search is installed but not offered without deferred tools; and a subagent's child
// conversation, owned by the call's tool task, behaves sensibly when the call is aborted and when the process restarts mid-call.
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RuntimeLibraryCases() =>
    [
        Case("runtime.session-failure.storage-error-fails-the-view", async () =>
        {
            await using var agent = new TestAgent("failure");
            FailingStorage? storage = null;
            var host = agent.Host(ScriptedStreams.Queue(ScriptedReply.Answer("first answer"), ScriptedReply.Answer("never committed"))) with
            {
                OpenStorage = async path => storage = new FailingStorage(await SqliteStorage.OpenAsync(path)),
            };
            var durable = (DurableSession)await DurableRuntime.OpenAsync(new OpenDurableOptions(), host);
            string directory;
            try
            {
                directory = durable.Location.Directory;
                await durable.Controller.SubmitAsync("hi", WhenBusy.Steer);
                await UntilView(durable.View, view => LastAnswer(view) == "first answer" && Idle(view), "answered");
                Equal<string?>(null, durable.View.Current.Failure, "no failure yet");
                storage!.Fail = true;
                await durable.Controller.SubmitAsync("again", WhenBusy.Steer);
                var view = await UntilView(durable.View, current => current.Failure is not null, "the session failed");
                Equal("disk is full", view.Failure, "the storage error");
                var end = await durable.Harness.Closed.WaitAsync(TimeSpan.FromSeconds(20));
                Equal(SessionEndReason.Failed, end.Reason, "the Harness closed failed");
                // The failed call's error, the Harness's report of it and later refused requests are no notices: the failure says it once.
                await durable.Controller.SubmitAsync("more", WhenBusy.Steer);
                await durable.Controller.CompactAsync(null);
                await Task.Delay(300);
                Check(durable.View.Current.Notices.IsEmpty, "no notices: " + string.Join(" | ", durable.View.Current.Notices.Select(notice => notice.Message)));
                Equal("Session failed: disk is full. Nothing more is saved; exit and run `pisharp durable --continue` to reopen it.",
                    DurableFailure.Message(view.Failure!), "the message");
            }
            finally
            {
                // Closing a failed session neither throws nor hangs, and releases the lock.
                await durable.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            }
            Check(!Directory.Exists(directory + ".lock"), "dispose released the lock");
            // Reopening recovers what was committed.
            await using var reopened = await agent.OpenAsync(ScriptedStreams.Queue(), continueSession: true);
            Equal(directory, reopened.Location.Directory, "the same session");
            Equal("first answer", LastAnswer(reopened.View.Current), "the committed transcript");
            Equal<string?>(null, reopened.View.Current.Failure, "a healthy session");
        }),

        // End to end as DurableMain runs it, on the virtual terminal: the TUI shows the failure, takes no more input, and exits with 1 without
        // hanging; closing the failed session releases its lock.
        Case("runtime.e2e.tui-session-failure-exits-1", async () =>
        {
            await using var agent = new TestAgent("e2e-failure");
            FailingStorage? storage = null;
            var host = agent.Host(ScriptedStreams.Queue(ScriptedReply.Answer("Hello before the failure."), ScriptedReply.Answer("never shown"))) with
            {
                OpenStorage = async path => storage = new FailingStorage(await SqliteStorage.OpenAsync(path)),
            };
            var durable = (DurableSession)await DurableRuntime.OpenAsync(new OpenDurableOptions(), host);
            var terminal = new VirtualTerminal(120, 40);
            int code;
            try
            {
                var run = StartTui(durable, terminal, agent.AgentDir);
                await terminal.WaitForTextAsync("/compact · /tasks");
                await Type(terminal, "say hello");
                await terminal.WaitForTextAsync("Hello before the failure.");
                await UntilView(durable.View, Idle, "idle");
                storage!.Fail = true;
                await Type(terminal, "and again");
                var message = DurableFailure.Message("disk is full");
                var screen = await terminal.WaitForAsync(text => TuiTests.Flat(text).Contains(message, StringComparison.Ordinal), "the failure");
                Check(!screen.Contains("never shown", StringComparison.Ordinal), "nothing after the failure:\n" + screen);
                terminal.Send("\u0003");
                code = await run.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { await durable.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
            Equal(1, code, "exit code");
            Check(!Directory.Exists(durable.Location.Directory + ".lock"), "dispose released the lock");
        }),

        Case("runtime.compact.failure-is-the-live-compaction-failure", async () =>
        {
            // Retries off, so the summarizer's error fails the compaction at once; a small kept window, so there is something to compact.
            await using var agent = new TestAgent("compact-failure", settings: """
                {"defaultProvider":"faux","defaultModel":"faux-1","retry":{"enabled":false},"compaction":{"keepRecentTokens":1}}
                """);
            var streams = ScriptedStreams.Queue(ScriptedReply.Answer("one"), ScriptedReply.Answer("two"), ScriptedReply.Failure("summarizer down"));
            await using var durable = await agent.OpenAsync(streams);
            await durable.Controller.SubmitAsync("first", WhenBusy.Steer);
            await UntilView(durable.View, view => LastAnswer(view) == "one" && Idle(view), "first answer");
            await durable.Controller.SubmitAsync("second", WhenBusy.Steer);
            await UntilView(durable.View, view => LastAnswer(view) == "two" && Idle(view), "second answer");
            await durable.Controller.CompactAsync(null);
            var failed = await UntilView(durable.View, view => view.Conversation.Doc("pi.live") is { } live &&
                live.TryGetValue("compactionFailure", out var failure) && failure is JsonObjectValue, "pi.live.compactionFailure");
            var recorded = (JsonObjectValue)failed.Conversation.Doc("pi.live")!["compactionFailure"];
            Equal("\"manual\"", recorded["reason"].ToJson(), "reason");
            Check(((JsonStringValue)recorded["error"]).Value.Contains("summarizer down", StringComparison.Ordinal), "error: " + recorded.ToJson());
            await Task.Delay(300);
            Check(!durable.View.Current.Notices.Any(notice => notice.Message.StartsWith("Compaction", StringComparison.Ordinal)),
                "no notice besides the live failure: " + string.Join(" | ", durable.View.Current.Notices.Select(notice => notice.Message)));
        }),

        Case("runtime.tool-search.installed-but-not-offered", async () =>
        {
            await using var agent = new TestAgent("tool-search");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue());
            var root = await durable.Harness.ConversationAsync(ConversationId.Root, DurableRuntime.Context);
            var resolved = await root!.AgentAsync(DurableRuntime.Context);
            Equal("read,write,edit,bash,subagent", string.Join(",", resolved.Tools.Select(tool => tool.Name)), "the tools the model is offered");
            Check(resolved.Deferred.IsEmpty, "nothing deferred");
            Check(!resolved.Extensions.Any(extension => extension.Name == "tool-search"), "tool-search not selected");
        }),

        // The subagent's child conversation is owned by the call's tool task. Aborting the parent's turn aborts the call, the call's wait for
        // the child, and the child's turn: everything ends idle, with no error notice.
        Case("runtime.subagent.abort-ends-the-call-and-the-child", async () =>
        {
            await using var agent = new TestAgent("subagent-abort");
            var streams = new ScriptedStreams(request => ScriptedRequest.Text(request.Last) == "Count forever."
                ? ScriptedReply.Endless(TimeSpan.FromMilliseconds(20))
                : ScriptedRequest.Role(request.Last!) == "toolResult"
                    ? ScriptedReply.Answer("unexpected")
                    : ScriptedReply.Call("subagent", """{"task":"Count forever."}""", "call-1"));
            await using var durable = await agent.OpenAsync(streams);
            await durable.Controller.SubmitAsync("Delegate the counting.", WhenBusy.Steer);
            var view = await UntilView(durable.View, current => current.Conversations.Any(summary => summary.Id != ConversationId.Root), "child created");
            var child = view.Conversations.Single(summary => summary.Id != ConversationId.Root).Id;
            var childConversation = (await durable.Harness.ConversationAsync(child, DurableRuntime.Context))!;
            await UntilAsync(async () => Streaming(await ViewOf(childConversation)), "the child streams");
            await durable.Controller.AbortAsync();
            var after = await UntilView(durable.View, Idle, "the parent's turn ended");
            var result = after.Conversation.Entries.Single(entry => entry.Kind == "pi.tool-result");
            Check(result.Model is { Count: > 0 } messages && messages[0] is JsonObjectValue message &&
                message["isError"] is JsonBooleanValue { Value: true }, "the call ended in error: " + result.Model?.ToJson());
            await UntilAsync(async () => !Running(await ViewOf(childConversation)), "the child's turn ended");
            Check(!durable.View.Current.Notices.Any(notice => notice.Level == DurableNoticeLevel.Error),
                "no error notice: " + string.Join(" | ", durable.View.Current.Notices.Select(notice => notice.Message)));
            Check(!streams.Requests.Any(request => ScriptedRequest.Role(request.Last!) == "toolResult"), "no answer after the abort");
        }),

        // The subagent tool is replay-safe: after a restart mid-call it reruns, finds the child conversation its task owns and the input it
        // submitted there (request id subagent:<taskId>), and waits for the child's resumed turn. One child, one input, one answer.
        Case("runtime.subagent.restart-resumes-the-call-and-the-child", async () =>
        {
            await using var agent = new TestAgent("subagent-restart");
            var first = new ScriptedStreams(request => ScriptedRequest.Text(request.Last) == "Name three prime numbers."
                ? ScriptedReply.Endless(TimeSpan.FromMilliseconds(20))
                : ScriptedReply.Call("subagent", """{"task":"Name three prime numbers."}""", "call-1"));
            ConversationId child;
            string directory;
            await using (var durable = await agent.OpenAsync(first))
            {
                directory = durable.Location.Directory;
                await durable.Controller.SubmitAsync("Use the subagent to find three primes.", WhenBusy.Steer);
                var view = await UntilView(durable.View, current => current.Conversations.Any(summary => summary.Id != ConversationId.Root), "child created");
                child = view.Conversations.Single(summary => summary.Id != ConversationId.Root).Id;
                var childConversation = (await durable.Harness.ConversationAsync(child, DurableRuntime.Context))!;
                await UntilAsync(async () => Streaming(await ViewOf(childConversation)), "the child streams");
            }
            // Closing wrote no outcome, as a crash would not: --continue resumes both turns.
            var second = new ScriptedStreams(request =>
            {
                var last = request.Last;
                if (last is not null && ScriptedRequest.Role(last) == "toolResult") return ScriptedReply.Answer("The subagent says: " + ScriptedRequest.Text(last));
                return ScriptedRequest.Text(last) == "Name three prime numbers." ? ScriptedReply.Answer("2, 3, and 5.") : ScriptedReply.Failure("unexpected request");
            });
            await using var reopened = await agent.OpenAsync(second, continueSession: true);
            Equal(directory, reopened.Location.Directory, "the same session");
            var done = await UntilView(reopened.View, current => LastAnswer(current) == "The subagent says: 2, 3, and 5." && Idle(current), "answered");
            Equal(child, done.Conversations.Single(summary => summary.Id != ConversationId.Root).Id, "the same child, no second one");
            var childView = await ViewOf((await reopened.Harness.ConversationAsync(child, DurableRuntime.Context))!);
            Equal(1, childView.Entries.Count(entry => entry.Kind == "pi.user"), "one input in the child");
            Equal("2, 3, and 5.", LastAnswer(done with { Conversation = childView }), "the child's resumed answer");
            Equal(1, done.Conversation.Entries.Count(entry => entry.Kind == "pi.tool-result"), "one result for the call");
            Check(!done.Notices.Any(notice => notice.Level == DurableNoticeLevel.Error),
                "no error notice: " + string.Join(" | ", done.Notices.Select(notice => notice.Message)));
        }),
    ];

    /// <summary>Whether <paramref name="view"/> has a committed streaming partial.</summary>
    private static bool Streaming(ConversationView view) => view.Doc("pi.live") is { } live && live.TryGetValue("generation", out var generation) &&
        generation is JsonObjectValue current && current.TryGetValue("message", out var message) && message is JsonObjectValue partial &&
        ScriptedRequest.Text(partial).Length > 0;

    /// <summary>The conversation's view now.</summary>
    private static async Task<ConversationView> ViewOf(IConversation conversation)
    {
        using var state = await conversation.ViewStateAsync(DurableRuntime.Context);
        return state.Value;
    }

    /// <summary>Whether <paramref name="view"/> runs a turn.</summary>
    private static bool Running(ConversationView view) => view.Doc("pi.live") is { } live && live.TryGetValue("run", out var run) && run is not JsonNullValue;

    /// <summary>Polls <paramref name="condition"/> until it holds.</summary>
    private static async Task UntilAsync(Func<Task<bool>> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what + ": not reached");
            await Task.Delay(20);
        }
    }

    /// <summary>A storage whose commits fail with "disk is full" once <see cref="Fail"/> is set: a Storage error fails the Session.</summary>
    private sealed class FailingStorage(IStorage inner) : IStorage
    {
        private volatile bool fail;

        public bool Fail { set => fail = value; }

        public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) =>
            fail ? Task.FromException<Seq>(new IOException("disk is full")) : inner.CommitAsync(writes, context);
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
}
