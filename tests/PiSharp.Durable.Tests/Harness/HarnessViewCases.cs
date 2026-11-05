// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-view.test.ts. Verbatim apart from the adaptations in
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
// Pi v1.5.0 sync (area watches, 92ab53602): Conversation.viewState() is gone, so fresh() reads a watch's acquisition value, the cases that
// kept a state beside the watches use watches alone (renamed with them), a close ends a watch Ended and a listener takes one value call.
// Chord's Watch folds the commits made while a listener is not yet started, so the 101-commit case takes upstream d8d99950a's expectation
// (work list area watch-folding): one call whose ops reach the newest view.
// Pi v1.5.0 sync (area deferred-commits, e5c6182ba's deferred attempt): "publishes one frame per touching commit ..." expects one frame
// more than the stored commits that touch the view: the attempt is a deferred commit, stored with the answer.
// Pi v1.5.0 sync (area view, f3255de76 in v1.5.0's form): the view is a window over the transcript. committed() is the newest `size`
// entries and the entry before them; head markers are appended in place (v1.2.0's "cuts the entries at a head marker ...", "keeps only
// mounted entries for a raw head write ..." and "cuts a fork's view into its inherited entries" are v1.5.0's "keeps head markers in place
// ..." and "shows a fork's inherited entries in ID order ..."); "conversation view window" is harness.view.window.*. Adapted: window
// bounds are ints and an EntryId, so 1.5 and NaN cannot be passed; the rejection case passes a From beyond JavaScript's safe integers for
// NaN, and RangeError is ArgumentOutOfRangeException. The 6,000-commit overflow case and the long-lived watch case run in the MemoryStorage
// pass only, as upstream runs each once; the long-lived watch case opens its own SQLite file there (upstream openNodeSqliteStorage), and
// GC.Collect is upstream's gc(); WeakReference is WeakRef.
// Pi v1.5.0 sync (area vocabulary, 5f312dac4): touches() counts with an ObserveCommits observer (upstream observeCommits({ advance, end }))
// the document changes of kind change (DocumentValueChange); the watch listeners already take value calls (Change(call), onValue) and
// WatchEnd kinds (area chord-watch).
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
    private static IEnumerable<(string, Func<Task>)> HarnessViewCases() => ViewTests.Cases("harness.view.",
        () => Task.FromResult<IStorage>(new MemoryStorage()), memoryPass: true,
        openSqlite: async path => await SqliteStorage.OpenAsync(path));

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
    internal static async Task<Recording> Record(IConversation conversation, ConversationWindow? window = null)
    {
        var watch = await conversation.WatchAsync(Context, window);
        var initial = watch.Value;
        var frames = new SyncList<Frame>();
        watch.Start((call, _) =>
        {
            var (value, ops, _) = Change(call);
            frames.Add(new Frame(value, ops));
            return Task.CompletedTask;
        });
        return new Recording(initial, frames, watch.StopAsync);
    }

    /// <summary>A freshly built view of <paramref name="conversation"/>, for comparing with an advanced one (upstream <c>fresh</c>).</summary>
    internal static async Task<ConversationView> Fresh(IConversation conversation, ConversationWindow? window = null)
    {
        var watch = await conversation.WatchAsync(Context, window);
        var value = watch.Value;
        await watch.StopAsync();
        return value;
    }

    /// <summary>The view as committed state defines it, read without any mount: the newest <paramref name="size"/> entries, the entry before
    /// them, and the built-in documents (upstream <c>committed</c>).</summary>
    internal static async Task<JsonObjectValue> Committed(IHarness harness, IConversation conversation, ConversationRecord record, int size)
    {
        var docs = new List<KeyValuePair<string, JsonValue>>();
        foreach (var token in new ConversationDocToken[] { Agents.AgentDoc, Live.LiveDoc, Inbox.InboxDoc, ProviderIdentity.ProviderDoc, UsageLedger.UsageDoc })
        {
            if (await harness.SnapshotAsync(token, conversation.Id, Context) is { } value) docs.Add(KeyValuePair.Create(token.Definition.Kind, (JsonValue)value));
        }
        var newest = (await conversation.EntriesAsync(new EntryQueryBounds(), size + 1, null, Context)).Items.ToList();
        EntryId? before = null;
        if (newest.Count > size)
        {
            before = newest[^1].Id;
            newest.RemoveAt(newest.Count - 1);
        }
        newest.Reverse();
        return new ConversationView(record, [.. newest], before, JsonObjectValue.From(docs)).ToJson();
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

    private static void SameValue(JsonValue expected, JsonValue actual, string what) =>
        Check(JsonValue.DeepEquals(expected, actual), $"{what}: expected {expected.ToJson()}, actual {actual.ToJson()}");

    /// <summary>Lets watch callbacks, which run after the commit, catch up (upstream <c>drained</c>); see the file header.</summary>
    internal static Task Drained(IHarness harness) => ((Session)harness).FlushLineAsync();

    /// <summary>Counts the publications that touch <paramref name="conversation"/>'s view (upstream <c>touches</c>).</summary>
    internal static Func<int> Touches(IHarness harness, IConversation conversation)
    {
        var count = 0;
        harness.ObserveCommits(new CommitObserver((publication, _) =>
        {
            var touched = publication.Changes.Any(change =>
                (change is EntryWrite entry && entry.Value.ConversationId == conversation.Id) ||
                (change is DocumentValueChange document && document.ConversationId == conversation.Id &&
                    MountedKinds.Contains(document.Record.Kind) && document.Ops.Length > 0));
            if (touched) Interlocked.Increment(ref count);
        }, _ => { }));
        return () => Volatile.Read(ref count);
    }

    internal static JsonObjectValue Answer(string text) => FauxAssistantMessage([FauxText(text)]);

    internal static Task<EntryRecord> Note(IConversation conversation, string kind, EntryHead? head = null) =>
        conversation.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = kind, Head = head }), Context);

    /// <summary>Appends one entry of <paramref name="kind"/> per commit; returns the entries (upstream <c>notes</c>).</summary>
    internal static async Task<List<EntryRecord>> Notes(IConversation conversation, int count, string kind = "note")
    {
        var entries = new List<EntryRecord>();
        for (var index = 0; index < count; index++)
        {
            var data = index;
            entries.Add(await conversation.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = kind, Data = data }), Context));
        }
        return entries;
    }

    internal static List<string> KindsOf(ConversationView view) => [.. view.Entries.Select(entry => entry.Kind)];

    private static List<EntryId> Ids(IEnumerable<EntryRecord> entries) => [.. entries.Select(entry => entry.Id)];

    /// <summary>A commit that holds the Session line until released.</summary>
    internal static (Action Release, Task Blocking) Hold(IConversation conversation)
    {
        var release = new HarnessTests.Deferred();
        var blocking = conversation.CommitAsync(async (ITx _) => await release.Task, Context);
        return (release.Resolve, blocking);
    }

    private static readonly ConversationCreateOptions Ownerless = new(ConversationOwnership.Ownerless);

    /// <summary>The cases on <paramref name="fresh"/> storage. <paramref name="memoryPass"/> adds the case upstream runs on MemoryStorage
    /// only for its cost; <paramref name="openSqlite"/>, the long-lived watch case on a file it opens.</summary>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh, bool memoryPass = false,
        Func<string, Task<IStorage>>? openSqlite = null) =>
        [.. View(prefix, fresh), .. Window(prefix + "window.", fresh, memoryPass, openSqlite)];

    private static IEnumerable<(string, Func<Task>)> View(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "hydrates-the-newest-entries-and-the-built-in-documents", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(Answer("hello"));
            var (harness, root) = await OpenChat(await fresh(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).WaitAsync(Context);
            var view = await Fresh(root);
            SameJson("{\"id\":" + root.Id.Value + "}", view.Conversation.ToJson(), "conversation");
            SameJson(JsonValue.Array((await AllEntries(root)).Select(entry => (JsonValue)entry.ToJson())).ToJson(),
                JsonValue.Array(view.Entries.Select(entry => (JsonValue)entry.ToJson())), "entries");
            Equal<EntryId?>(null, view.Before, "before");
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
            // One frame per stored commit, and one for the attempt, a deferred commit stored with the answer.
            Equal(touching() + 1, all.Count, "frames");
            var last = Replay(initial, all);
            SameValue(await Committed(harness, root, initial.Conversation, all[^1].Value.Entries.Length), last, "replay equals committed state");
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

        yield return Case(prefix + "keeps-head-markers-in-place-a-summary-and-a-reset-are-appended-entries", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            await Note(root, "a");
            var b = await Note(root, "b");
            await Note(root, "c");
            var (initial, frames, stop) = await Record(root);
            var summary = await Note(root, "summary", b.Id);
            await Note(root, "d");
            await root.ResetAsync(null, Context);
            await Drained(harness);
            var all = frames.Snapshot();
            Seq(["a,b,c,summary", "a,b,c,summary,d", "a,b,c,summary,d,pi.reset"], all.Select(frame => string.Join(",", KindsOf(frame.Value))), "kinds");
            Equal("[[\"p\",[\"entries\"],3,0,[" + summary.ToJson().ToJson() + "]]]", OpsJson(all[0].Ops), "summary ops");
            // The summary's `head` tells a renderer where the entries it summarizes end.
            Equal<EntryId?>(b.Id, all[0].Value.Entries[3].Head, "the summary's head");
            SameValue(await Committed(harness, root, initial.Conversation, 6), Replay(initial, all), "replay equals committed state");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shows-a-forks-inherited-entries-in-id-order-its-head-markers-in-place", async () =>
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
            Seq(["a", "b", "summary"], KindsOf(first), "kinds");
            SameView(await Committed(harness, fork, initial.Conversation, 3), first, "committed");
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

        yield return Case(prefix + "folds-undelivered-commits-into-one-call-whose-ops-reach-the-newest-view", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var watch = await root.WatchAsync(Context);
            var initial = watch.Value;
            for (var index = 0; index < 101; index++) await Note(root, "note");
            var frames = new SyncList<Frame>();
            watch.Start((call, _) =>
            {
                var (value, ops, _) = Change(call);
                frames.Add(new Frame(value, ops));
                return Task.CompletedTask;
            });
            await Drained(harness);
            var all = frames.Snapshot();
            Equal(1, all.Count, "frames");
            Check(all[0].Ops is not [ReplaceOp, ..], "folded ops, not a replacement: " + OpsJson(all[0].Ops));
            SameView(DeltaApply.Apply(initial.ToJson(), all[0].Ops), all[0].Value, "the ops reach the newest view");
            // The default window took 100 appends, then dropped its oldest 51 at the 101st, down to 50.
            SameView(await Committed(harness, root, initial.Conversation, 50), all[0].Value, "the newest 50");
            await watch.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-watches-of-one-conversation-independent-and-remounts-after-the-last-one-detaches", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var kept = await Record(root);
            var (_, frames, stop) = await Record(root);
            await Note(root, "one");
            await Drained(harness);
            Seq(["one"], KindsOf(kept.Frames.Snapshot()[^1].Value), "kept after one");
            await stop();
            Equal(1, frames.Count, "frames");
            await Note(root, "two");
            await Drained(harness);
            var last = kept.Frames.Snapshot()[^1].Value;
            Seq(["one", "two"], KindsOf(last), "kept after two");
            await kept.Stop();
            // No observer is left, so the mount was dropped: a new observer builds a new revision from committed state.
            var rebuilt = await Fresh(root);
            SameView(last.ToJson(), rebuilt, "rebuilt");
            Check(!ReferenceEquals(rebuilt, last), "rebuilt is a new revision");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-watches-at-close-and-rejects-later-acquisition", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var watch = await root.WatchAsync(Context);
            await harness.CloseAsync(Context);
            Equal(WatchEnd.Ended, await watch.Closed, "watch end");
            Equal(0, watch.Value.Entries.Length, "watch entries");
            await ThrowsAsync<Exception>(() => root.WatchAsync(Context), "watch after close");
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
            var shared = await root.WatchAsync(Context);
            // Each observer has its own window; the documents are the mount's.
            SameView(shared.Value.ToJson(), failing.Value, "the same view");
            Same(shared.Value.Docs, failing.Value.Docs, "one mount's documents");
            await shared.StopAsync();
            failing.Start((_, _) => throw new InvalidOperationException("listener failed"));
            await Note(root, "one");
            await Note(root, "two");
            await Drained(harness);
            Equal(WatchEndKind.ListenerError, (await failing.Closed).Kind, "failing end");
            Equal(2, frames.Count, "frames");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "publishes-one-frame-for-a-commit-that-appends-several-entries-and-edits-a-document", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var other = Documents.DefineConversationDoc("app.other", 1, DocumentFork.Initial, () => O("{\"n\":0}"),
                checkpointWhen: (_, _, _) => false);
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
            SameValue(await Committed(harness, root, initial.Conversation, 2), Replay(initial, all), "replay equals committed state");
            await stop();
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>Upstream's "conversation view window" cases.</summary>
    private static IEnumerable<(string, Func<Task>)> Window(string prefix, Func<Task<IStorage>> fresh, bool memoryPass,
        Func<string, Task<IStorage>>? openSqlite)
    {
        yield return Case(prefix + "starts-with-the-newest-min-entries-and-the-entry-before-them-or-the-whole-short-transcript", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var written = await Notes(root, 10);
            var view = await Fresh(root, new ConversationWindow { Min = 4 });
            Seq(Ids(written.Skip(6)), Ids(view.Entries), "the newest four");
            Equal<EntryId?>(written[5].Id, view.Before, "before");
            // The default window: the newest 50, here all ten, starting the transcript.
            var all = await Fresh(root);
            Seq(Ids(written), Ids(all.Entries), "all ten");
            Equal<EntryId?>(null, all.Before, "no before");
            Check(!all.ToJson().ContainsKey("before"), "no before key: " + all.ToJson().ToJson());
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "takes-appends-up-to-max-then-drops-its-oldest-in-one-splice-down-to-min", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var written = await Notes(root, 4);
            var (initial, frames, stop) = await Record(root, new ConversationWindow { Min = 3, Max = 5 });
            Seq(Ids(written.Skip(1)), Ids(initial.Entries), "initial");
            var appended = await Notes(root, 3);
            await Drained(harness);
            var all = frames.Snapshot();
            Seq([4, 5, 3], all.Select(frame => frame.Value.Entries.Length), "lengths");
            var (e5, e6, e7) = (appended[0], appended[1], appended[2]);
            Equal("[[\"p\",[\"entries\"],3,0,[" + e5.ToJson().ToJson() + "]]]", OpsJson(all[0].Ops), "an append");
            Equal("[[\"p\",[\"entries\"],5,0,[" + e7.ToJson().ToJson() + "]],[\"p\",[\"entries\"],0,3,[]],[\"s\",[\"before\"]," +
                written[3].Id.Value + "]]", OpsJson(all[2].Ops), "an append, then a drop");
            Seq(Ids([e5, e6, e7]), Ids(all[2].Value.Entries), "after the drop");
            Equal<EntryId?>(written[3].Id, all[2].Value.Before, "before");
            SameValue(await Committed(harness, root, initial.Conversation, 3), Replay(initial, all), "replay equals committed state");
            // Entries that stay keep their identity: a splice moves them, it never copies them.
            Same(all[1].Value.Entries[3], all[2].Value.Entries[0], "a kept entry");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-newest-min-of-a-commit-that-appends-more-than-max-at-once", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            await Notes(root, 2);
            var (initial, frames, stop) = await Record(root, new ConversationWindow { Min = 2, Max = 3 });
            var burst = await root.CommitAsync(async tx =>
            {
                var entries = new List<EntryRecord>();
                for (var index = 0; index < 5; index++) entries.Add(await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "burst" }));
                return entries;
            }, Context);
            await Drained(harness);
            var all = frames.Snapshot();
            Equal(1, all.Count, "frames");
            Seq(Ids(burst.Skip(3)), Ids(all[0].Value.Entries), "the newest two");
            Equal<EntryId?>(burst[2].Id, all[0].Value.Before, "before");
            SameValue(await Committed(harness, root, initial.Conversation, 2), Replay(initial, all), "replay equals committed state");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "starts-at-from-when-the-entries-since-fit-in-max-otherwise-at-the-newest-min", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var written = await Notes(root, 30);
            var window = new ConversationWindow { Min = 5, Max = 10 };
            // Ten entries since `from`: all of them, with the entry before `from`.
            var within = await Fresh(root, window with { From = written[20].Id });
            Seq(Ids(written.Skip(20)), Ids(within.Entries), "within");
            Equal<EntryId?>(written[19].Id, within.Before, "within before");
            // Fewer than `min` since `from`: only those.
            var shortWindow = await Fresh(root, window with { From = written[28].Id });
            Seq(Ids(written.Skip(28)), Ids(shortWindow.Entries), "short");
            Equal<EntryId?>(written[27].Id, shortWindow.Before, "short before");
            // Resuming after the newest entry: an empty window right after it.
            var resumed = await Fresh(root, window with { From = new EntryId(written[29].Id.Value + 1) });
            Equal(0, resumed.Entries.Length, "resumed");
            Equal<EntryId?>(written[29].Id, resumed.Before, "resumed before");
            // Eleven since `from`: more than `max`, so the newest `min`; `before` shows the client a gap.
            var beyond = await Fresh(root, window with { From = written[19].Id });
            Seq(Ids(written.Skip(25)), Ids(beyond.Entries), "beyond");
            Equal<EntryId?>(written[24].Id, beyond.Before, "beyond before");
            // From the transcript's start.
            var whole = await Fresh(root, new ConversationWindow { Min = 40, From = written[0].Id });
            Seq(Ids(written), Ids(whole.Entries), "whole");
            Equal<EntryId?>(null, whole.Before, "whole before");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "advances-a-resumed-window-by-appends-from-its-start", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var written = await Notes(root, 5);
            var (initial, frames, stop) = await Record(root, new ConversationWindow { From = new EntryId(written[4].Id.Value + 1) });
            var later = await Notes(root, 2);
            await Drained(harness);
            var all = frames.Snapshot();
            Seq(Ids(later), Ids(all[^1].Value.Entries), "the later entries");
            Equal<EntryId?>(written[4].Id, all[^1].Value.Before, "before");
            SameView(Replay(initial, all), all[^1].Value, "replay");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "uses-from-only-for-the-first-value-a-from-past-the-newest-entry-starts-empty-and-takes-every-append", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var written = await Notes(root, 3);
            var from = new EntryId(written[2].Id.Value + 1000);
            var (initial, frames, stop) = await Record(root, new ConversationWindow { From = from });
            Equal(0, initial.Entries.Length, "initial entries");
            Equal<EntryId?>(written[2].Id, initial.Before, "initial before");
            // An entry below `from` is still appended: the window continues from its attachment.
            var next = (await Notes(root, 1))[0];
            Check(next.Id.Value < from.Value, $"{next.Id} is below {from}");
            await Drained(harness);
            var last = frames.Snapshot()[^1].Value;
            SameJson(JsonValue.Array((JsonValue)next.ToJson()).ToJson(), JsonValue.Array(last.Entries.Select(entry => (JsonValue)entry.ToJson())),
                "the next entry");
            Equal<EntryId?>(written[2].Id, last.Before, "before");
            await stop();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-window-bounds-that-are-not-integers-or-put-max-below-min", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            await ThrowsAsync<ArgumentOutOfRangeException>(() => root.WatchAsync(Context, new ConversationWindow { Min = 0 }), "min 0");
            // Upstream's { min: 1.5 } cannot be expressed: Min is an int.
            await ThrowsAsync<ArgumentOutOfRangeException>(() => root.WatchAsync(Context, new ConversationWindow { Min = 10, Max = 9 }), "max below min");
            // Upstream's { from: NaN }: a From that is not an entry ID JavaScript can hold.
            await ThrowsAsync<ArgumentOutOfRangeException>(() => root.WatchAsync(Context, new ConversationWindow { From = new EntryId(long.MaxValue) }),
                "from beyond safe integers");
            var recorder = new Observer<ConversationView>((_, _) => { }, _ => { });
            await ThrowsAsync<ArgumentOutOfRangeException>(() => harness.ObserveConversationAsync(root.Id, recorder, Context, new ConversationWindow { Max = 0 }),
                "observe with max 0");
            // `max` may equal `min`.
            await (await root.WatchAsync(Context, new ConversationWindow { Min = 3, Max = 3 })).StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "gives-each-observer-its-own-window-over-one-mount", async () =>
        {
            var (harness, root) = await OpenChat(await fresh(), Setup());
            var written = await Notes(root, 6);
            var changes = new SyncList<ConversationView>();
            var observation = await harness.ObserveConversationAsync(root.Id, new Observer<ConversationView>((call, _) =>
            {
                if (call is WatchValueCall<ConversationView> value) changes.Add(value.Value);
            }, _ => { }), Context, new ConversationWindow { Min = 2, Max = 3 });
            var wide = await Record(root, new ConversationWindow { Min = 5 });
            Seq(Ids(written.Skip(4)), Ids(observation.Value.Entries), "the narrow window");
            Seq(Ids(written.Skip(1)), Ids(wide.Initial.Entries), "the wide window");
            var next = (await Notes(root, 1))[0];
            await Drained(harness);
            Seq(Ids([.. written.Skip(4), next]), Ids(changes.Snapshot()[0].Entries), "the narrow window after");
            Seq(Ids([.. written.Skip(1), next]), Ids(wide.Frames.Snapshot()[0].Value.Entries), "the wide window after");
            Same(changes.Snapshot()[0].Docs, wide.Frames.Snapshot()[0].Value.Docs, "one mount's documents");
            observation.Detach();
            await wide.Stop();
            await harness.CloseAsync(Context);
        });

        if (memoryPass)
        {
            yield return Case(prefix + "bounds-an-overflow-replacement-by-max", async () =>
            {
                var (harness, root) = await OpenChat(await fresh(), Setup());
                var watch = await root.WatchAsync(Context, new ConversationWindow { Min = 5, Max = 6 });
                // Past 10,000 pending operations, the next call is one root replacement: here an append per commit, and a splice and a
                // `before` every other commit.
                for (var index = 0; index < 6000; index++) await Note(root, "note");
                var frames = new SyncList<Frame>();
                watch.Start((call, _) =>
                {
                    var (value, ops, _) = Change(call);
                    frames.Add(new Frame(value, ops));
                    return Task.CompletedTask;
                });
                await Drained(harness);
                var all = frames.Snapshot();
                Equal(1, all.Count, "frames");
                Check(all[0].Ops is [ReplaceOp replace] && JsonValue.DeepEquals(replace.Value, all[0].Value.ToJson()),
                    "one root replacement: " + OpsJson(all[0].Ops)[..Math.Min(200, OpsJson(all[0].Ops).Length)]);
                Check(all[0].Value.Entries.Length <= 6, $"{all[0].Value.Entries.Length} entries");
                await watch.StopAsync();
                await harness.CloseAsync(Context);
            });
        }

        if (openSqlite is not null)
        {
            yield return Case(prefix + "lets-a-long-lived-watch-drop-the-entries-that-left-its-window", async () =>
            {
                // On SQLite, which keeps no entry objects, so only the watch can hold the first one.
                var storage = await openSqlite(Path.Combine(Temp("view-window-gc"), "session.sqlite"));
                var (harness, root) = await OpenChat(storage, Setup());
                var watch = await root.WatchAsync(Context, new ConversationWindow { Min = 5, Max = 10 });
                WeakReference<EntryRecord>? first = null;
                var count = 0;
                watch.Start((call, _) =>
                {
                    var value = Change(call).Value;
                    Volatile.Write(ref count, value.Entries.Length);
                    if (Volatile.Read(ref first) is null && value.Entries.Length > 0) Volatile.Write(ref first, new WeakReference<EntryRecord>(value.Entries[0]));
                    return Task.CompletedTask;
                });
                await Notes(root, 1);
                await HarnessTests.Eventually(() => Volatile.Read(ref first) is not null);
                await Notes(root, 200);
                await Drained(harness);
                Check(Volatile.Read(ref count) <= 10, $"{count} entries");
                for (var round = 0; round < 3 && Alive(first!); round++)
                {
                    await Drained(harness);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                Check(!Alive(first!), "the first entry was collected");
                await watch.StopAsync();
                await harness.CloseAsync(Context);
            });
        }
    }

    /// <summary>Whether <paramref name="reference"/>'s target is alive; its own frame, so no caller keeps the target.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool Alive(WeakReference<EntryRecord> reference) => reference.TryGetTarget(out _);
}
