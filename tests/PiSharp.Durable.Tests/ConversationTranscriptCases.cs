// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/conversation-transcript.test.ts (added in f3255de76, area
// view). Verbatim apart from the adaptations in ChatSupport.cs and SessionSupport.cs, and these:
// - describe("ConversationTranscript") is conversation-transcript.*, "ConversationTranscript loads" conversation-transcript.loads.* and
//   "ConversationTranscript over a Harness" conversation-transcript.over-a-harness.* (MemoryStorage, as upstream).
// - Items are ConversationTranscriptItem records; shape() is the same compact list as text. toBe on items is ImmutableArray equality (the
//   underlying array) and on views and loads ReferenceEquals; a cursor is any JSON object ({"more": true} for upstream's "more").
// - A loader that throws synchronously is a delegate that throws before it returns a task; drained() (setTimeout(0)) is a Session line
//   barrier (ViewTests.Drained).
// - Pi v1.5.0 sync (area vocabulary, 5f312dac4): the over-a-harness watch listener takes the tagged call; upstream's onValue(...) is
//   SessionSupport.cs' Change(call), which a Durable watch only ever hands a value call.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ConversationTranscriptCases() => TranscriptTests.Cases("conversation-transcript.");
}

/// <summary>The conversation-transcript.* cases and their support.</summary>
internal static class TranscriptTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly ConversationRecord Conversation = new(new ConversationId(1));
    private static readonly JsonObjectValue More = (JsonObjectValue)JsonValue.Parse("""{"more":true}""");

    /// <summary>Entries with IDs that have holes: every third ID is missing.</summary>
    private static List<EntryRecord> Transcript(int count)
    {
        var entries = new List<EntryRecord>();
        for (var id = 1L; entries.Count < count; id++)
            if (id % 3 != 0) entries.Add(new EntryRecord { Id = new EntryId(id), ConversationId = Conversation.Id, Kind = "note" });
        return entries;
    }

    /// <summary>A loader over <see cref="Entries"/> with <see cref="IConversation.EntriesAsync"/> semantics, counting its calls (upstream
    /// <c>loader</c>).</summary>
    private sealed class Loader(IReadOnlyList<EntryRecord> entries)
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task<Page<EntryRecord>> Load(EntryQueryBounds query, int limit, DurableContext context)
        {
            Interlocked.Increment(ref calls);
            var matching = entries.Where(entry =>
                (query.MinEntryId is not { } min || entry.Id.Value >= min.Value) && (query.MaxEntryId is not { } max || entry.Id.Value <= max.Value)).ToList();
            var ordered = query.Order == ScanOrder.Ascending ? matching : Enumerable.Reverse(matching).ToList();
            return Task.FromResult(new Page<EntryRecord>([.. ordered.Take(limit)], ordered.Count > limit ? More : null));
        }
    }

    /// <summary>The view of a window: <paramref name="all"/>[<paramref name="first"/>..<paramref name="last"/>] with the entry before them
    /// (upstream <c>windowOf</c>).</summary>
    private static ConversationView WindowOf(List<EntryRecord> all, int first, int last, JsonObjectValue? docs = null) =>
        new(Conversation, [.. all.Skip(first).Take(last - first + 1)], first > 0 ? all[first - 1].Id : null, docs ?? JsonObjectValue.Empty);

    /// <summary>Items as a compact list: entry IDs, <c>gap after..upTo</c>, and <c>earlier</c> (upstream <c>shape</c>).</summary>
    private static List<string> Shape(ConversationTranscriptView view) => [.. view.Items.Select(item => item switch
    {
        ConversationTranscriptEntry entry => entry.Entry.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ConversationTranscriptGap gap => $"gap {gap.After.Value}..{gap.UpTo.Value}",
        _ => "earlier",
    })];

    private static List<string> IdsOf(IEnumerable<EntryRecord> entries) =>
        [.. entries.Select(entry => entry.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    private static ConversationTranscriptGap? GapOf(ConversationTranscriptView view) => view.Items.OfType<ConversationTranscriptGap>().FirstOrDefault();

    private static JsonObjectValue Docs(string json) => (JsonObjectValue)JsonValue.Parse(json);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix) =>
        [.. Transcripts(prefix), .. Loads(prefix + "loads."), .. OverHarness(prefix + "over-a-harness.")];

    private static IEnumerable<(string, Func<Task>)> Transcripts(string prefix)
    {
        yield return Case(prefix + "shows-a-first-window-under-an-earlier-marker-or-alone-when-it-starts-the-transcript", () =>
        {
            var all = Transcript(20);
            var later = new ConversationTranscript(new Loader(all).Load);
            Seq(["earlier", .. IdsOf(all.Skip(15))], Shape(later.Merge(WindowOf(all, 15, 19))), "a later window");
            Equal<EntryId?>(all[19].Id, later.Newest, "newest");
            var whole = new ConversationTranscript(new Loader(all).Load);
            Seq(IdsOf(all), Shape(whole.Merge(WindowOf(all, 0, 19))), "the whole transcript");
        });

        yield return Case(prefix + "appends-keeps-entries-the-window-dropped-and-returns-the-same-items-when-nothing-is-added", () =>
        {
            var all = Transcript(30);
            var helper = new ConversationTranscript(new Loader(all).Load);
            var docsA = Docs("""{"a":{"n":1}}""");
            var first = helper.Merge(WindowOf(all, 10, 19, docsA));
            var grown = helper.Merge(WindowOf(all, 10, 21, docsA));
            Seq(["earlier", .. IdsOf(all.Skip(10).Take(12))], Shape(grown), "grown");
            // The window dropped its oldest; the transcript keeps them.
            var spliced = helper.Merge(WindowOf(all, 18, 21, docsA));
            Check(spliced.Items == grown.Items, "the same items");
            Same(grown, spliced, "the same view");
            // A docs-only change: a new view, the same items.
            var docs = Docs("""{"a":{"n":2}}""");
            var streamed = helper.Merge(WindowOf(all, 18, 21, docs));
            Check(!ReferenceEquals(streamed, grown), "a new view");
            Check(streamed.Items == grown.Items, "the same items after a docs change");
            Same(docs, streamed.Docs, "the new docs");
            Check(first.Items != grown.Items, "new items after an append");
        });

        yield return Case(prefix + "opens-a-gap-when-a-window-does-not-continue-the-transcript-and-fills-it-page-by-page", async () =>
        {
            var all = Transcript(200);
            var load = new Loader(all);
            var helper = new ConversationTranscript(load.Load, new ConversationTranscriptOptions { PageSize = 50 });
            helper.Merge(WindowOf(all, 150, 159));
            // A stall: the next window starts well past the newest entry held.
            var stalled = helper.Merge(WindowOf(all, 190, 199));
            var gap = GapOf(stalled)!;
            Equal(new ConversationTranscriptGap(all[159].Id, all[190].Id), gap, "the gap");
            Seq(["earlier", .. IdsOf(all.Skip(150).Take(10)), $"gap {all[159].Id.Value}..{all[190].Id.Value}", .. IdsOf(all.Skip(190))],
                Shape(stalled), "stalled");
            // 30 entries are missing; one page loads them all and closes the gap.
            var filled = await helper.FillGapAsync(gap, Context);
            Seq(["earlier", .. IdsOf(all.Skip(150))], Shape(filled), "filled");
            Equal(1, load.Calls, "loads");
        });

        yield return Case(prefix + "shrinks-a-gap-from-its-start-while-more-than-a-page-is-missing", async () =>
        {
            var all = Transcript(200);
            var helper = new ConversationTranscript(new Loader(all).Load, new ConversationTranscriptOptions { PageSize = 10 });
            helper.Merge(WindowOf(all, 100, 109));
            var view = helper.Merge(WindowOf(all, 190, 199));
            view = await helper.FillGapAsync(GapOf(view)!, Context);
            Equal(new ConversationTranscriptGap(all[119].Id, all[190].Id), GapOf(view), "the shrunk gap");
            while (GapOf(view) is { } gap) view = await helper.FillGapAsync(gap, Context);
            Seq(["earlier", .. IdsOf(all.Skip(100))], Shape(view), "filled");
        });

        yield return Case(prefix + "loads-earlier-pages-until-the-start-then-drops-the-marker", async () =>
        {
            var all = Transcript(45);
            var load = new Loader(all);
            var helper = new ConversationTranscript(load.Load, new ConversationTranscriptOptions { PageSize = 20 });
            helper.Merge(WindowOf(all, 40, 44));
            var view = await helper.LoadEarlierAsync(Context);
            Seq(["earlier", .. IdsOf(all.Skip(20))], Shape(view), "one page earlier");
            // A page without a next cursor reaches the start.
            view = await helper.LoadEarlierAsync(Context);
            Seq(IdsOf(all), Shape(view), "the start");
            var calls = load.Calls;
            Same(view, await helper.LoadEarlierAsync(Context), "nothing earlier");
            Equal(calls, load.Calls, "no load");
        });

        yield return Case(prefix + "shares-one-load-between-concurrent-calls-of-loadearlier-and-of-fillgap-for-one-gap", async () =>
        {
            var all = Transcript(100);
            var inner = new Loader(all);
            var gate = new HarnessTests.Deferred();
            EntryLoader load = async (query, limit, callContext) =>
            {
                await gate.Task;
                return await inner.Load(query, limit, callContext);
            };
            var helper = new ConversationTranscript(load);
            helper.Merge(WindowOf(all, 40, 49));
            var stalled = helper.Merge(WindowOf(all, 90, 99));
            Task<ConversationTranscriptView>[] earlier = [helper.LoadEarlierAsync(Context), helper.LoadEarlierAsync(Context)];
            Task<ConversationTranscriptView>[] fills = [helper.FillGapAsync(GapOf(stalled)!, Context), helper.FillGapAsync(GapOf(stalled)!, Context)];
            Same(earlier[0], earlier[1], "one earlier load");
            Same(fills[0], fills[1], "one gap load");
            gate.Resolve();
            await Task.WhenAll([.. earlier, .. fills]);
            Equal(2, inner.Calls, "loads");
            Seq(IdsOf(all), Shape(await helper.LoadEarlierAsync(Context)), "everything");
        });

        yield return Case(prefix + "resumes-after-newest-an-empty-window-right-after-it-adds-nothing-one-past-it-opens-a-gap", () =>
        {
            var all = Transcript(30);
            var helper = new ConversationTranscript(new Loader(all).Load);
            var view = helper.Merge(WindowOf(all, 10, 19));
            var resumed = new ConversationView(Conversation, [], helper.Newest!.Value, JsonObjectValue.Empty);
            Check(helper.Merge(resumed).Items == view.Items, "an empty window right after the newest adds nothing");
            // A window that is empty but starts past `newest`: the entries up to its `before` are missing.
            var past = helper.Merge(new ConversationView(Conversation, [], all[25].Id, JsonObjectValue.Empty));
            Equal($"gap {all[19].Id.Value}..{all[25].Id.Value + 1}", Shape(past)[^1], "the gap");
            Equal<EntryId?>(all[25].Id, helper.Newest, "newest");
            // The next window continues it.
            Seq([$"gap {all[19].Id.Value}..{all[25].Id.Value + 1}", .. IdsOf([all[26], all[27]])], Shape(helper.Merge(WindowOf(all, 26, 27))).TakeLast(3),
                "continued");
        });

        yield return Case(prefix + "fills-a-gap-from-a-window-that-covers-it", () =>
        {
            var all = Transcript(100);
            var helper = new ConversationTranscript(new Loader(all).Load);
            helper.Merge(WindowOf(all, 40, 49));
            helper.Merge(WindowOf(all, 90, 99));
            // A wider window, such as a new watch with a larger `max`, covers the second half of the gap.
            var view = helper.Merge(WindowOf(all, 70, 99));
            Seq(["earlier", .. IdsOf(all.Skip(40).Take(10)), $"gap {all[49].Id.Value}..{all[70].Id.Value}", .. IdsOf(all.Skip(70))], Shape(view),
                "the shrunk gap");
        });

        yield return Case(prefix + "rejects-loadearlier-before-the-first-merge-and-returns-the-view-for-a-gap-that-is-gone", async () =>
        {
            var all = Transcript(10);
            var helper = new ConversationTranscript(new Loader(all).Load);
            await RejectsAsync(() => helper.LoadEarlierAsync(Context), "merge a view first", "before a merge");
            var view = helper.Merge(WindowOf(all, 0, 9));
            var stale = new ConversationTranscriptGap(new EntryId(1), new EntryId(2));
            Same(view, await helper.FillGapAsync(stale, Context), "a gap that is gone");
        });
    }

    private static IEnumerable<(string, Func<Task>)> Loads(string prefix)
    {
        yield return Case(prefix + "retries-a-load-whose-loader-threw-synchronously-for-loadearlier-and-fillgap", async () =>
        {
            var all = Transcript(100);
            var inner = new Loader(all);
            var offline = true;
            var calls = 0;
            EntryLoader load = (query, limit, callContext) =>
            {
                Interlocked.Increment(ref calls);
                // An adapter that checks its connection before it returns a task.
                if (Volatile.Read(ref offline)) throw new InvalidOperationException("temporarily offline");
                return inner.Load(query, limit, callContext);
            };
            var helper = new ConversationTranscript(load);
            helper.Merge(WindowOf(all, 40, 49));
            var stalled = helper.Merge(WindowOf(all, 90, 99));
            await RejectsAsync(() => helper.LoadEarlierAsync(Context), "temporarily offline", "loadEarlier offline");
            await RejectsAsync(() => helper.FillGapAsync(GapOf(stalled)!, Context), "temporarily offline", "fillGap offline");
            Volatile.Write(ref offline, false);
            Seq([.. IdsOf(all.Take(50)), $"gap {all[49].Id.Value}..{all[90].Id.Value}", .. IdsOf(all.Skip(90))],
                Shape(await helper.LoadEarlierAsync(Context)), "loaded earlier");
            Seq(IdsOf(all), Shape(await helper.FillGapAsync(GapOf(stalled)!, Context)), "filled");
            Equal(4, Volatile.Read(ref calls), "loader calls");
        });

        yield return Case(prefix + "retries-a-load-that-rejected", async () =>
        {
            var all = Transcript(30);
            var inner = new Loader(all);
            var fail = true;
            EntryLoader load = async (query, limit, callContext) =>
            {
                await Task.Yield();
                if (Volatile.Read(ref fail)) throw new TimeoutException("timeout");
                return await inner.Load(query, limit, callContext);
            };
            var helper = new ConversationTranscript(load);
            helper.Merge(WindowOf(all, 20, 29));
            await RejectsAsync(() => helper.LoadEarlierAsync(Context), "timeout", "rejected");
            Volatile.Write(ref fail, false);
            Seq(IdsOf(all), Shape(await helper.LoadEarlierAsync(Context)), "retried");
        });

        yield return Case(prefix + "keeps-one-gap-for-an-empty-window-whose-before-lies-inside-it", () =>
        {
            var all = Transcript(100);
            var helper = new ConversationTranscript(new Loader(all).Load);
            helper.Merge(WindowOf(all, 40, 49));
            var before = helper.Merge(WindowOf(all, 90, 99));
            var view = helper.Merge(new ConversationView(Conversation, [], all[65].Id, before.Docs));
            Check(view.Items == before.Items, "the same items");
            Equal(1, view.Items.Count(item => item is ConversationTranscriptGap), "gaps");
        });

        yield return Case(prefix + "shares-one-load-between-a-stale-gap-descriptor-and-the-gap-it-shrank-into", async () =>
        {
            var all = Transcript(100);
            var inner = new Loader(all);
            var gate = new HarnessTests.Deferred();
            EntryLoader load = async (query, limit, callContext) =>
            {
                await gate.Task;
                return await inner.Load(query, limit, callContext);
            };
            var helper = new ConversationTranscript(load, new ConversationTranscriptOptions { PageSize = 10 });
            helper.Merge(WindowOf(all, 10, 19));
            var stale = GapOf(helper.Merge(WindowOf(all, 90, 99)))!;
            gate.Resolve();
            var current = GapOf(await helper.FillGapAsync(stale, Context))!;
            Check(current != stale, "the gap shrank");
            var loads = inner.Calls;
            var (a, b) = (helper.FillGapAsync(stale, Context), helper.FillGapAsync(current, Context));
            Same(a, b, "one load");
            await a;
            Equal(loads + 1, inner.Calls, "loads");
        });

        yield return Case(prefix + "treats-an-empty-page-with-a-next-cursor-as-the-end-of-the-range", async () =>
        {
            var all = Transcript(60);
            // A remote loader that always returns a cursor.
            var inner = new Loader(all);
            EntryLoader load = async (query, limit, callContext) => (await inner.Load(query, limit, callContext)) with { Next = More };
            var helper = new ConversationTranscript(load, new ConversationTranscriptOptions { PageSize = 100 });
            helper.Merge(WindowOf(all, 10, 19));
            var view = helper.Merge(WindowOf(all, 40, 49));
            view = await helper.FillGapAsync(GapOf(view)!, Context);
            // The first page held every missing entry; the next one is empty and closes the gap.
            view = await helper.FillGapAsync(GapOf(view)!, Context);
            Check(GapOf(view) is null, "no gap");
            await helper.LoadEarlierAsync(Context);
            view = await helper.LoadEarlierAsync(Context);
            Seq(IdsOf(all.Take(50)), Shape(view), "the start");
        });

        yield return Case(prefix + "resumes-with-resume-undefined-before-a-merge-for-an-empty-transcript-from-after-the-newest", () =>
        {
            var all = Transcript(30);
            var helper = new ConversationTranscript(new Loader(all).Load);
            Equal<ConversationWindow?>(null, helper.Resume, "before a merge");
            helper.Merge(new ConversationView(Conversation, [], null, JsonObjectValue.Empty));
            Equal<ConversationWindow?>(new ConversationWindow(), helper.Resume, "an empty transcript");
            helper.Merge(WindowOf(all, 0, 9));
            Equal<ConversationWindow?>(new ConversationWindow { From = new EntryId(all[9].Id.Value + 1) }, helper.Resume, "after the newest");
        });

        yield return Case(prefix + "rejects-a-page-size-below-one", () =>
        {
            // PiSharp-only: upstream's RangeError for a page size that is not an integer >= 1 (a fraction cannot be passed here).
            Throws(() => new ConversationTranscript(new Loader([]).Load, new ConversationTranscriptOptions { PageSize = 0 }), "Page size must be an integer >= 1, got 0",
                "page size 0");
        });
    }

    private static IEnumerable<(string, Func<Task>)> OverHarness(string prefix)
    {
        yield return Case(prefix + "follows-the-walkthrough-live-appends-a-stall-fillgap-loadearlier-and-a-resume-with-from", async () =>
        {
            var (harness, root) = await OpenChat(new MemoryStorage(), Setup());
            Task<EntryRecord> Note() => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            for (var index = 0; index < 120; index++) await Note();
            var helper = new ConversationTranscript((query, limit, callContext) => root.EntriesAsync(query, limit, null, callContext));
            var window = new ConversationWindow { Min = 10, Max = 20 };
            var watch = await root.WatchAsync(Context, window);
            var view = helper.Merge(watch.Value);
            watch.Start((call, _) =>
            {
                Volatile.Write(ref view, helper.Merge(Change(call).Value));
                return Task.CompletedTask;
            });
            for (var index = 0; index < 15; index++) await Note();
            await ViewTests.Drained(harness);
            view = Volatile.Read(ref view);
            Check(!view.Items.Any(item => item is ConversationTranscriptGap), "no gap");
            Equal(25, view.Items.Count(item => item is ConversationTranscriptEntry), "entries");
            // A drop: the client misses 40 entries, more than `max`, and resumes after the newest it has.
            await watch.StopAsync();
            for (var index = 0; index < 40; index++) await Note();
            var resumed = await root.WatchAsync(Context, window with { From = helper.Resume!.From });
            view = helper.Merge(resumed.Value);
            Equal(1, view.Items.Count(item => item is ConversationTranscriptGap), "one gap");
            while (GapOf(view) is { } gap) view = await helper.FillGapAsync(gap, Context);
            while (view.Items is [ConversationTranscriptEarlier, ..]) view = await helper.LoadEarlierAsync(Context);
            Seq(IdsOf(await AllEntries(root)), IdsOf(view.Items.OfType<ConversationTranscriptEntry>().Select(item => item.Entry)), "every entry");
            // A short drop resumes without a gap.
            for (var index = 0; index < 3; index++) await Note();
            var again = await root.WatchAsync(Context, window with { From = helper.Resume!.From });
            Equal(3, again.Value.Entries.Length, "the missed entries");
            view = helper.Merge(again.Value);
            Check(view.Items.All(item => item is ConversationTranscriptEntry), "only entries");
            await resumed.StopAsync();
            await again.StopAsync();
            await harness.CloseAsync(Context);
        });
    }
}
