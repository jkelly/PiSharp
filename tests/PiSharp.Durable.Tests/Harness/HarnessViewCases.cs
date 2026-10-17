// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-view.test.ts. Verbatim apart from the adaptations in
// ChatSupport.cs and SessionSupport.cs, and these:
// - Backends. Upstream runs this file on MemoryStorage; nothing in it depends on the backend, so HarnessViewCases() runs it on MemoryStorage
//   (harness.view.*), HarnessViewJsonlCases() on a fresh JSONL directory (harness.view.jsonl.*) and HarnessViewSqliteCases() on a fresh
//   SQLite file (harness.view.sqlite.*).
// - A ConversationView is a typed class; toEqual on views compares their ToJson(), toBe is ReferenceEquals (ImmutableArray equality for the
//   entries, which compares the underlying array), and replay() applies each frame's operations to the previous revision's ToJson().
// - drained() (a setTimeout(0) turn so watch callbacks run) is Drained(): a Session line barrier. Watch deliveries a commit schedules run
//   in that commit's line job, and a watch started from outside the line delivers in its own line job, so every delivery of a commit
//   enqueued before the barrier has run when it completes (the listeners here complete synchronously).
// - A held commit is a commit whose callback awaits a deferred; an aborted controller is a DurableCancellation cancelled with
//   Exception("cancelled"), which is what the acquisition rejects with.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessViewCases() => ViewTests.Cases("harness.view.", () => Task.FromResult<IStorage>(new MemoryStorage()));

    private static IEnumerable<(string, Func<Task>)> HarnessViewJsonlCases() =>
        ViewTests.Cases("harness.view.jsonl.", async () => await JsonlStorage.OpenAsync(Temp("view-jsonl"), DurableContext.Background));

    private static IEnumerable<(string, Func<Task>)> HarnessViewSqliteCases() =>
        ViewTests.Cases("harness.view.sqlite.", async () => await SqliteStorage.OpenAsync(Path.Combine(Temp("view-sqlite"), "view.sqlite")));
}

/// <summary>The harness.view.* cases and their support.</summary>
internal static partial class ViewTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly string[] MountedKinds = ["pi.agent", "pi.inbox", "pi.live", "pi.provider", "pi.usage"];

    internal sealed record Frame(ConversationView Value, ImmutableArray<Op> Ops);

    internal sealed record Recording(ConversationView Initial, SyncList<Frame> Frames, Func<Task<WatchEnd>> Stop);

    /// <summary>Starts a watch of <paramref name="conversation"/> that records its acquisition revision and every delivered frame (upstream
    /// <c>record</c>).</summary>
    internal static async Task<Recording> Record(IConversation conversation)
    {
        var watch = await conversation.WatchAsync(Context);
        var initial = watch.Value;
        var frames = new SyncList<Frame>();
        watch.Start((value, ops, _) =>
        {
            frames.Add(new Frame(value, ops));
            return Task.CompletedTask;
        });
        return new Recording(initial, frames, watch.StopAsync);
    }

    /// <summary>A freshly built view of <paramref name="conversation"/>, for comparing with an advanced one (upstream <c>fresh</c>).</summary>
    internal static async Task<ConversationView> Fresh(IConversation conversation)
    {
        var state = await conversation.ViewStateAsync(Context);
        var value = state.Value;
        state.Dispose();
        return value;
    }

    /// <summary>The view as committed state defines it, read without any mount: the active entries and the built-in documents (upstream
    /// <c>committed</c>).</summary>
    internal static async Task<JsonObjectValue> Committed(IHarness harness, IConversation conversation, ConversationRecord record)
    {
        var docs = new List<KeyValuePair<string, JsonValue>>();
        foreach (var token in new ConversationDocToken[] { Agents.AgentDoc, Live.LiveDoc, Inbox.InboxDoc, ProviderIdentity.ProviderDoc, UsageLedger.UsageDoc })
        {
            if (await harness.SnapshotAsync(token, conversation.Id, Context) is { } value) docs.Add(KeyValuePair.Create(token.Definition.Kind, (JsonValue)value));
        }
        var entries = (await conversation.ContextAsync(Context)).Entries;
        return JsonValue.Object([
            KeyValuePair.Create("conversation", (JsonValue)record.ToJson()),
            KeyValuePair.Create("entries", (JsonValue)JsonValue.Array(entries.Select(entry => (JsonValue)entry.ToJson()))),
            KeyValuePair.Create("docs", (JsonValue)JsonValue.Object(docs)),
        ]);
    }

    /// <summary>Replays every frame's operations from <paramref name="initial"/>, checking each delivered revision on the way (upstream
    /// <c>replay</c>).</summary>
    internal static JsonValue Replay(ConversationView initial, IEnumerable<Frame> frames)
    {
        JsonValue value = initial.ToJson();
        var index = 0;
        foreach (var frame in frames)
        {
            value = DeltaApply.Apply(value, frame.Ops);
            Check(JsonValue.DeepEquals(value, frame.Value.ToJson()), $"replayed frame {index}: expected {frame.Value.ToJson().ToJson()}, actual {value.ToJson()}");
            index++;
        }
        return value;
    }

    internal static void SameView(JsonValue expected, ConversationView actual, string what) =>
        Check(JsonValue.DeepEquals(expected, actual.ToJson()), $"{what}: expected {expected.ToJson()}, actual {actual.ToJson().ToJson()}");

    /// <summary>Lets watch callbacks, which run after the commit, catch up (upstream <c>drained</c>); see the file header.</summary>
    internal static Task Drained(IHarness harness) => ((Session)harness).FlushLineAsync();

    /// <summary>Counts the publications that touch <paramref name="conversation"/>'s view (upstream <c>touches</c>).</summary>
    internal static Func<int> Touches(IHarness harness, IConversation conversation)
    {
        var count = 0;
        harness.SubscribeCommits((publication, _) =>
        {
            var touched = publication.Changes.Any(change =>
                (change is EntryWrite entry && entry.Value.ConversationId == conversation.Id) ||
                (change is DocumentCommitChange document && document.ConversationId == conversation.Id &&
                    MountedKinds.Contains(document.Record.Kind) && document.Ops.Length > 0));
            if (touched) Interlocked.Increment(ref count);
        });
        return () => Volatile.Read(ref count);
    }

    internal static JsonObjectValue Answer(string text) => FauxAssistantMessage([FauxText(text)]);

    internal static Task<EntryRecord> Note(IConversation conversation, string kind, EntryHead? head = null) =>
        conversation.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = kind, Head = head }), Context);

    internal static List<string> KindsOf(ConversationView view) => [.. view.Entries.Select(entry => entry.Kind)];

    /// <summary>A commit that holds the Session line until released.</summary>
    internal static (Action Release, Task Blocking) Hold(IConversation conversation)
    {
        var release = new HarnessTests.Deferred();
        var blocking = conversation.CommitAsync(async (ITx _) => await release.Task, Context);
        return (release.Resolve, blocking);
    }

    private static readonly ConversationCreateOptions Ownerless = new(ConversationOwnership.Ownerless);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh) =>
    [
        .. View(prefix, fresh),
        .. ResetCases(prefix, fresh, root => root.ResetAsync(null, Context)),
    ];

    private static IEnumerable<(string, Func<Task>)> View(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "hydrates-the-active-entries-and-the-built-in-documents", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(Answer("hello"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).WaitAsync(Context);
            var view = await Fresh(root);
            SameJson("{\"id\":" + root.Id.Value + "}", view.Conversation.ToJson(), "conversation");
            SameJson(JsonValue.Array((await AllEntries(root)).Select(entry => (JsonValue)entry.ToJson())).ToJson(),
                JsonValue.Array(view.Entries.Select(entry => (JsonValue)entry.ToJson())), "entries");
            Seq(MountedKinds, view.Docs.Keys.Order(StringComparer.Ordinal), "mounted kinds");
            SameJson("{}", view.Docs["pi.live"], "pi.live");
            SameJson("{\"items\":[]}", view.Docs["pi.inbox"], "pi.inbox");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "publishes-one-frame-per-touching-commit-whose-operations-rebuild-every-revision", async () =>
        {
            var setup = Setup();
            var release = new HarnessTests.Deferred();
            FauxStep step = new FauxResponseFactory(async request =>
            {
                await await Task.WhenAny(release.Task, HarnessTests.Aborted(request.Signal));
                return Answer("a longer answer");
            });
            setup.Faux.SetResponses(step);
            var (harness, root) = await OpenChat(await fresh(), setup);
            var (initial, frames, stop) = await Record(root);
            var touching = Touches(harness, root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await HarnessTests.Eventually(() => frames.Snapshot().Any(frame => frame.Value.Doc("pi.live")!.ContainsKey("generation")));
            release.Resolve();
            await submission.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await Drained(harness);
            var all = frames.Snapshot();
            Equal(touching(), all.Count, "frames");
            SameView(Replay(initial, all), await Fresh(root), "replayed");
            Check(JsonValue.DeepEquals(Replay(initial, all), await Committed(harness, root, initial.Conversation)), "replay equals committed state");
            Check(all[0].Ops.Any(op => op is SpliceOp { Index: 0, Remove: 0, Items: [JsonObjectValue { } item] } splice &&
                splice.Path.SequenceEqual([PathSegment.Of("entries")]) && item["kind"] is JsonStringValue { Value: "pi.user" }),
                "the first frame inserts the user entry: " + OpsJson(all[0].Ops));
            // Document operations keep their exact shape under the mount path.
            Check(all.SelectMany(frame => frame.Ops).Any(op => op.ToJson().ToJson() == """["s",["docs","pi.live","generation"],{"attempt":1}]"""),
                "a generation set under the mount path");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shares-unchanged-parts-between-revisions-and-skips-commits-that-touch-nothing-mounted", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var other = await harness.CreateConversationAsync(Ownerless, Context);
            var (initial, frames, stop) = await Record(root);
            await Note(other, "note");
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = "high" }, Context);
            await Note(root, "note");
            await Drained(harness);
            var all = frames.Snapshot();
            Equal(2, all.Count, "frames");
            Equal("""[["s",["docs","pi.agent","thinkingLevel"],"high"]]""", OpsJson(all[0].Ops), "agent ops");
            Check(all[0].Value.Entries == initial.Entries, "entries are shared");
            Same(initial.Docs["pi.live"], all[0].Value.Docs["pi.live"], "pi.live is shared");
            Same(all[0].Value.Docs, all[1].Value.Docs, "docs are shared");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "cuts-a-forks-view-into-its-inherited-entries", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var a = await Note(root, "a");
            var b = await Note(root, "b");
            var fork = await root.ForkAsync(b.Id, Ownerless, Context);
            var (initial, frames, stop) = await Record(fork);
            await Note(fork, "summary", b.Id);
            await Drained(harness);
            Seq([a.Id, b.Id], initial.Entries.Select(entry => entry.Id), "inherited entries");
            var first = frames.Snapshot()[0].Value;
            Seq(["summary", "b"], KindsOf(first), "kinds");
            SameView(await Committed(harness, fork, initial.Conversation), first, "committed");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shows-a-forks-inherited-entries-and-follows-only-the-forks-own-commits", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var first = await Note(root, "first");
            var fork = await root.ForkAsync(first.Id, Ownerless, Context);
            var (initial, frames, stop) = await Record(fork);
            Seq(["first"], KindsOf(initial), "inherited");
            SameJson("{\"conversationId\":" + root.Id.Value + ",\"at\":" + first.Id.Value + "}", initial.Conversation.Parent!.ToJson(), "parent");
            await Note(root, "parent");
            await Note(fork, "child");
            await Drained(harness);
            var all = frames.Snapshot();
            Equal(1, all.Count, "frames");
            Seq(["first", "child"], KindsOf(all[0].Value), "kinds");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "unmounts-a-retired-document-and-mounts-its-recreation-whole", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var (_, frames, stop) = await Record(root);
            await root.CommitAsync(tx => tx.RetireDocAsync(Live.LiveDoc, root.Id), Context);
            await root.CommitAsync(async tx => (await tx.DocAsync(Live.LiveDoc, root.Id)).Set("tools", JsonArrayValue.Empty), Context);
            await Drained(harness);
            var all = frames.Snapshot();
            Seq(["""[["d",["docs","pi.live"]]]""", """[["s",["docs","pi.live"],{"tools":[]}]]"""], all.Select(frame => OpsJson(frame.Ops)), "ops");
            Check(all[0].Value.Doc("pi.live") is null, "pi.live is unmounted");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "replaces-undelivered-frames-with-the-newest-view-after-100-pending-frames", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var watch = await root.WatchAsync(Context);
            for (var index = 0; index < 101; index++) await Note(root, "note");
            var frames = new SyncList<Frame>();
            watch.Start((value, ops, _) =>
            {
                frames.Add(new Frame(value, ops));
                return Task.CompletedTask;
            });
            await Drained(harness);
            var all = frames.Snapshot();
            Equal(1, all.Count, "frames");
            Equal("[[\"r\"," + all[0].Value.ToJson().ToJson() + "]]", OpsJson(all[0].Ops), "replacement");
            Equal(101, all[0].Value.Entries.Length, "entries");
            await watch.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-states-and-watches-of-one-conversation-independent-and-remounts-after-the-last-one-detaches", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var state = await root.ViewStateAsync(Context);
            var (_, frames, stop) = await Record(root);
            await Note(root, "one");
            await Drained(harness);
            Seq(["one"], KindsOf(state.Value), "state after one");
            await stop();
            Equal(1, frames.Count, "frames");
            await Note(root, "two");
            await Drained(harness);
            Seq(["one", "two"], KindsOf(state.Value), "state after two");
            var last = state.Value;
            state.Dispose();
            // No observer is left, so the mount was dropped: a new observer builds a new revision from committed state.
            var rebuilt = await Fresh(root);
            SameView(last.ToJson(), rebuilt, "rebuilt");
            Check(!ReferenceEquals(rebuilt, last), "rebuilt is a new revision");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-states-and-watches-at-close-and-rejects-later-acquisition", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var watch = await root.WatchAsync(Context);
            var state = await root.ViewStateAsync(Context);
            await harness.CloseAsync(Context);
            Equal(WatchEnd.SessionClosed, await watch.Closed, "watch end");
            Equal(0, state.Value.Entries.Length, "state entries");
            await ThrowsAsync<Exception>(() => root.WatchAsync(Context), "watch after close");
            await ThrowsAsync<Exception>(() => root.ViewStateAsync(Context), "state after close");
        });

        yield return Case(prefix + "rejects-an-acquisition-cancelled-or-closed-while-it-waits-for-the-session-line", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var held = Hold(root);
            var controller = Context.WithCancel();
            var cancelled = root.WatchAsync(controller.Context);
            controller.Cancel(new Exception("cancelled"));
            held.Release();
            await held.Blocking;
            await RejectsAsync(() => cancelled, "cancelled", "cancelled acquisition");

            held = Hold(root);
            var closedWhileQueued = root.WatchAsync(Context);
            var closing = harness.CloseAsync(Context);
            held.Release();
            await held.Blocking;
            await RejectsAsync(() => closedWhileQueued, "Harness is closed", "acquisition closed while queued");
            await closing;
        });

        yield return Case(prefix + "shares-one-mount-between-concurrent-observers-and-isolates-a-failing-listener", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var failing = await root.WatchAsync(Context);
            var (_, frames, stop) = await Record(root);
            var shared = await root.ViewStateAsync(Context);
            Same(shared.Value, failing.Value, "one mount");
            shared.Dispose();
            failing.Start((_, _, _) => throw new InvalidOperationException("listener failed"));
            await Note(root, "one");
            await Note(root, "two");
            await Drained(harness);
            Equal(WatchEndReason.ListenerError, (await failing.Closed).Reason, "failing end");
            Equal(2, frames.Count, "frames");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "publishes-one-frame-for-a-commit-that-appends-several-entries-and-edits-a-document", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var other = Documents.DefineConversationDoc("app.other", 1, DocumentFork.Initial, () => O("{\"n\":0}"));
            var (initial, frames, stop) = await Record(root);
            await root.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "a" });
                (await tx.DocAsync(Live.LiveDoc, root.Id)).Set("tools", JsonArrayValue.Empty);
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "b" });
            }, Context);
            // A document that is not mounted publishes nothing.
            await root.CommitAsync(async tx => (await tx.DocAsync(other, root.Id)).Set("n", 1), Context);
            await Drained(harness);
            var all = frames.Snapshot();
            Equal(1, all.Count, "frames");
            Seq(["a", "b"], KindsOf(all[0].Value), "kinds");
            Check(JsonValue.DeepEquals(Replay(initial, all), await Committed(harness, root, initial.Conversation)), "replay equals committed state");
            await stop();
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>The two cases that call <c>root.reset()</c>, parameterized by the reset.</summary>
    internal static IEnumerable<(string, Func<Task>)> ResetCases(string prefix, Func<Task<IStorage>> fresh, Func<IConversation, Task> reset)
    {
        yield return Case(prefix + "cuts-the-entries-at-a-head-marker-keeping-the-entries-from-its-head", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            await Note(root, "a");
            var b = await Note(root, "b");
            await Note(root, "c");
            var (initial, frames, stop) = await Record(root);
            var summary = await Note(root, "summary", b.Id);
            await Note(root, "d");
            await reset(root);
            await Drained(harness);
            var all = frames.Snapshot();
            Seq(["summary,b,c", "summary,b,c,d", "pi.reset"], all.Select(frame => string.Join(",", KindsOf(frame.Value))), "kinds");
            Equal("[[\"p\",[\"entries\"],0,1,[" + summary.ToJson().ToJson() + "]]]", OpsJson(all[0].Ops), "head marker ops");
            Check(JsonValue.DeepEquals(Replay(initial, all), await Committed(harness, root, initial.Conversation)), "replay equals committed state");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-only-mounted-entries-for-a-raw-head-write-that-targets-before-the-active-range", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var old = await Note(root, "old");
            await reset(root);
            var (_, frames, stop) = await Record(root);
            await Note(root, "summary", old.Id);
            await Drained(harness);
            // Model context now starts at `old` again, but the mount never held it (spec §12); a rebuilt mount shows it.
            Seq(["summary"], KindsOf(frames.Snapshot()[0].Value), "mounted kinds");
            await stop();
            Seq(["summary", "old"], KindsOf(await Fresh(root)), "rebuilt kinds");
            await harness.CloseAsync(Context);
        });
    }
}

