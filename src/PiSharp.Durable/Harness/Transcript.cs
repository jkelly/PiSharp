// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/transcript.ts (TranscriptItem, TranscriptView, EntryLoader,
// ConversationTranscriptOptions, ConversationTranscript, gapOf, mergeById); added in f3255de76 (area view).
//
// Mappings: upstream's TranscriptItem is ConversationTranscriptItem, a sealed record per kind (ConversationTranscriptEntry,
// ConversationTranscriptGap, ConversationTranscriptEarlier) whose ToJson() is upstream's object, and TranscriptView is
// ConversationTranscriptView: PiSharp.Durable.Client's TranscriptItem (decision 0008's renderable items) and PiSharp.Contracts'
// TranscriptEntry already take the short names. "The same items array" is the same ImmutableArray (its underlying array), and the earlier
// marker is one instance. Upstream's bounds are JavaScript numbers with -Infinity for an unknown start; here they are doubles too, exact
// for entry IDs, which are safe integers. Upstream runs on one event loop; here merge(), the loads' results and the getters take one
// lock, the loader runs outside it on the thread pool (upstream's later microtask, so even a synchronous throw settles a load that is
// already shared), and a shared load is forgotten in the load itself, before callers see it settle (upstream's settled handler, which runs
// before their continuations).
using System.Collections.Immutable;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>One row of a client's transcript (upstream <c>TranscriptItem</c>): an entry, a gap, or the earlier marker.</summary>
public abstract record ConversationTranscriptItem
{
    private protected ConversationTranscriptItem() { }

    /// <summary><c>"entry"</c>, <c>"gap"</c> or <c>"earlier"</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>Upstream's object: <c>{kind: "entry", entry}</c>, <c>{kind: "gap", after, upTo}</c> or <c>{kind: "earlier"}</c>.</summary>
    public abstract JsonObjectValue ToJson();
}

/// <summary>An entry the client has.</summary>
public sealed record ConversationTranscriptEntry(EntryRecord Entry) : ConversationTranscriptItem
{
    /// <inheritdoc/>
    public override string Kind => "entry";

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        JsonValue.Object([KeyValuePair.Create("kind", (JsonValue)Kind), KeyValuePair.Create("entry", (JsonValue)Entry.ToJson())]);
}

/// <summary>Entries strictly between <see cref="After"/> and <see cref="UpTo"/> are missing: render "load more" and call
/// <see cref="ConversationTranscript.FillGapAsync"/>.</summary>
public sealed record ConversationTranscriptGap(EntryId After, EntryId UpTo) : ConversationTranscriptItem
{
    /// <inheritdoc/>
    public override string Kind => "gap";

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => JsonValue.Object([
        KeyValuePair.Create("kind", (JsonValue)Kind),
        KeyValuePair.Create("after", (JsonValue)After.ToJson()),
        KeyValuePair.Create("upTo", (JsonValue)UpTo.ToJson()),
    ]);
}

/// <summary>The transcript continues above: render "load earlier" and call <see cref="ConversationTranscript.LoadEarlierAsync"/>. Always
/// the first item; one instance.</summary>
public sealed record ConversationTranscriptEarlier : ConversationTranscriptItem
{
    private ConversationTranscriptEarlier() { }

    /// <summary>The earlier marker.</summary>
    public static ConversationTranscriptEarlier Instance { get; } = new();

    /// <inheritdoc/>
    public override string Kind => "earlier";

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => JsonValue.Object([KeyValuePair.Create("kind", (JsonValue)Kind)]);
}

/// <summary>The conversation view as a renderer sees it (upstream <c>TranscriptView</c>): <see cref="Items"/>, the client's whole
/// transcript, instead of the window.</summary>
public sealed record ConversationTranscriptView(ConversationRecord Conversation, JsonObjectValue Docs,
    ImmutableArray<ConversationTranscriptItem> Items)
{
    /// <summary>Upstream's object <c>{conversation, docs, items}</c>.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([
        KeyValuePair.Create("conversation", (JsonValue)Conversation.ToJson()),
        KeyValuePair.Create("docs", (JsonValue)Docs),
        KeyValuePair.Create("items", (JsonValue)JsonValue.Array(Items.Select(item => (JsonValue)item.ToJson()))),
    ]);
}

/// <summary>Pages a conversation's entries, as <see cref="IConversation.EntriesAsync"/> does without a cursor (upstream
/// <c>EntryLoader</c>).</summary>
public delegate Task<Page<EntryRecord>> EntryLoader(EntryQueryBounds query, int limit, DurableContext context);

/// <summary>Options of a <see cref="ConversationTranscript"/> (upstream <c>ConversationTranscriptOptions</c>).</summary>
public sealed record ConversationTranscriptOptions
{
    /// <summary>Entries <see cref="ConversationTranscript.LoadEarlierAsync"/> and <see cref="ConversationTranscript.FillGapAsync"/> load
    /// per call; default 50.</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// What a client has of one conversation: every entry it got from a view or fetched, and the gaps between (upstream
/// <c>ConversationTranscript</c>, spec §9.3). A layer over any <see cref="ConversationView"/>, wherever it comes from: call
/// <see cref="Merge"/> with every value of the view; it returns the view with the client's whole transcript as
/// <see cref="ConversationTranscriptView.Items"/> instead of the window. Entries never change once committed, so the transcript only
/// grows; a window that dropped its oldest entries keeps them here.
/// </summary>
/// <remarks>
/// A window whose <see cref="ConversationView.Before"/> lies past the newest entry the client has does not continue it, after a stall, an
/// overflow, or a reconnect: the entries in between become a gap. Nothing counts entries or computes ranges from entry IDs, which have
/// holes; only exclusive bounds are turned into the inclusive bounds of <see cref="EntryQueryBounds"/>. After a drop, a new watch with
/// <see cref="Resume"/> as its window continues the transcript. A merge costs O(items) and never re-sorts or re-fetches; a merge,
/// <see cref="LoadEarlierAsync"/>, or <see cref="FillGapAsync"/> that adds nothing returns the previous items array. Concurrent calls of
/// <see cref="LoadEarlierAsync"/>, or of <see cref="FillGapAsync"/> for the same gap, share one load, so a placeholder that stays visible
/// does not fetch twice. Thread-safe.
/// </remarks>
public sealed class ConversationTranscript
{
    private readonly EntryLoader load;
    private readonly int pageSize;
    private readonly Lock gate = new();
    // Everything below is guarded by gate.
    /// <summary>Every entry held, in ID order, as items.</summary>
    private ImmutableArray<ConversationTranscriptEntry> entries = [];
    private readonly HashSet<EntryId> ids = [];
    /// <summary>Gaps in ID order; each is an immutable item, replaced when it shrinks.</summary>
    private ImmutableArray<ConversationTranscriptGap> gaps = [];
    /// <summary>Whether anything was merged yet.</summary>
    private bool started;
    /// <summary>Exclusive lower bound of the known range: entries at or below it are not loaded; -Infinity at the start.</summary>
    private double low = double.NegativeInfinity;
    /// <summary>Inclusive upper bound of the known range: the newest entry, or a later empty window's <c>before</c>.</summary>
    private double tail = double.NegativeInfinity;
    private ImmutableArray<ConversationTranscriptItem> items = [];
    private ConversationView? last;
    private ConversationTranscriptView? view;
    private Task<ConversationTranscriptView>? earlier;
    private readonly Dictionary<(double After, double UpTo), Task<ConversationTranscriptView>> filling = [];

    /// <summary>A transcript that loads through <paramref name="load"/>, such as
    /// <c>(query, limit, context) => conversation.EntriesAsync(query, limit, null, context)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A page size below 1 (upstream <c>RangeError</c>).</exception>
    public ConversationTranscript(EntryLoader load, ConversationTranscriptOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        var size = options?.PageSize ?? 50;
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(options), size, $"Page size must be an integer >= 1, got {size}");
        this.load = load;
        pageSize = size;
    }

    /// <summary>The ID the transcript reaches: its newest entry, or the <c>before</c> of a later window that was empty. Null before the
    /// first merge and for an empty transcript.</summary>
    public EntryId? Newest
    {
        get
        {
            lock (gate) return NewestCore();
        }
    }

    /// <summary>
    /// The window that resumes this transcript after a drop: <c>{From}</c> right after <see cref="Newest"/>, so a new watch returns only
    /// what was missed when it fits its window, otherwise its newest entries and a gap; <c>{}</c> for an empty transcript; null before the
    /// first merge. <c>conversation.WatchAsync(context, transcript.Resume)</c>.
    /// </summary>
    public ConversationWindow? Resume
    {
        get
        {
            lock (gate)
            {
                if (!started) return null;
                return NewestCore() is { } newest ? new ConversationWindow { From = new EntryId(newest.Value + 1) } : new ConversationWindow();
            }
        }
    }

    /// <summary>Takes one value of the view: its window's entries join the transcript.</summary>
    public ConversationTranscriptView Merge(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (gate)
        {
            last = view;
            var before = view.Before is { } id ? id.Value : double.NegativeInfinity;
            var hi = view.Entries.IsEmpty ? before : view.Entries[^1].Id.Value;
            Cover(before, hi, view.Entries);
            return Result();
        }
    }

    /// <summary>Loads the page before the oldest entry held. Without anything earlier, returns the current view. Faults with
    /// <see cref="InvalidOperationException"/> before the first merge.</summary>
    public Task<ConversationTranscriptView> LoadEarlierAsync(DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            if (last is null) return NotMerged();
            if (double.IsNegativeInfinity(low)) return Task.FromResult(Result());
            if (earlier is not null) return earlier;
            var from = low;
            // The loader runs later, on the thread pool, so even its synchronous throw settles a load that is already shared.
            earlier = Task.Run(async () =>
            {
                try
                {
                    var query = new EntryQueryBounds { MaxEntryId = new EntryId((long)from), Order = ScanOrder.Descending };
                    var page = await load(query, pageSize, context).ConfigureAwait(false);
                    var loaded = page.Items.Reverse().ToImmutableArray();
                    lock (gate)
                    {
                        // No next page, or an empty one: the transcript starts here.
                        if (page.Next is null || loaded.IsEmpty) Cover(double.NegativeInfinity, from, loaded);
                        else Cover(loaded[0].Id.Value - 1, from, loaded);
                        return Result();
                    }
                }
                finally
                {
                    // While it runs, every call shares it, so it is the one shared.
                    lock (gate) earlier = null;
                }
            });
            return earlier;
        }
    }

    /// <summary>
    /// Loads the next page of <paramref name="gap"/>, a gap item of the items, shrinking it from its start, or removing it once nothing is
    /// missing. A gap that has shrunk since loads the gap it became; one that is no longer there returns the current view. Faults with
    /// <see cref="InvalidOperationException"/> before the first merge.
    /// </summary>
    public Task<ConversationTranscriptView> FillGapAsync(ConversationTranscriptGap gap, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            ConversationTranscriptGap? current = null;
            foreach (var each in gaps)
                if (each.After == gap.After && each.UpTo == gap.UpTo) { current = each; break; }
            if (current is null)
                foreach (var each in gaps)
                    if (each.After.Value < gap.UpTo.Value && gap.After.Value < each.UpTo.Value) { current = each; break; }
            if (current is null)
            {
                if (last is null) return NotMerged();
                return Task.FromResult(Result());
            }
            // Keyed by the gap the call resolved to, so a stale descriptor and the current one share the load.
            var key = ((double)current.After.Value, (double)current.UpTo.Value);
            if (filling.TryGetValue(key, out var running)) return running;
            var (after, upTo) = key;
            var task = Task.Run(async () =>
            {
                try
                {
                    var query = new EntryQueryBounds
                    {
                        MinEntryId = new EntryId((long)after + 1),
                        MaxEntryId = new EntryId((long)upTo - 1),
                        Order = ScanOrder.Ascending,
                    };
                    var page = await load(query, pageSize, context).ConfigureAwait(false);
                    lock (gate)
                    {
                        // No next page, or an empty one: nothing else is missing.
                        if (page.Next is null || page.Items.IsEmpty) Cover(after, upTo - 1, page.Items);
                        else Cover(after, page.Items[^1].Id.Value, page.Items);
                        return Result();
                    }
                }
                finally
                {
                    lock (gate) filling.Remove(key);
                }
            });
            filling[key] = task;
            return task;
        }
    }

    private static Task<ConversationTranscriptView> NotMerged() =>
        Task.FromException<ConversationTranscriptView>(new InvalidOperationException("ConversationTranscript: merge a view first"));

    private EntryId? NewestCore() => double.IsNegativeInfinity(tail) ? null : new EntryId((long)tail);

    /// <summary>
    /// Records that every entry with an ID in (<paramref name="lo"/>, <paramref name="hi"/>] is known, <paramref name="added"/> being those
    /// entries in ID order: holds the new ones, shrinks the gaps they fill, extends the known range, and opens a gap where the range does not
    /// continue it (upstream <c>#cover</c>).
    /// </summary>
    private void Cover(double lo, double hi, ImmutableArray<EntryRecord> added)
    {
        var fresh = new List<ConversationTranscriptEntry>();
        foreach (var entry in added)
            if (ids.Add(entry.Id)) fresh.Add(new ConversationTranscriptEntry(entry));
        var held = entries;
        // New entries past every held one: the items grow at their end.
        var appended = fresh.Count == 0 || held.IsEmpty || fresh[0].Entry.Id.Value > held[^1].Entry.Id.Value;
        if (fresh.Count > 0) entries = appended ? held.AddRange(fresh) : MergeById(held, fresh);
        var rebuild = false;
        if (!started || (held.IsEmpty && gaps.IsEmpty && lo >= tail))
        {
            // The first range, or one after an empty transcript: it is all the client knows.
            rebuild = !started || lo != low;
            started = true;
            low = lo;
            tail = Math.Max(tail, hi);
        }
        else if (lo > tail)
        {
            // Past the newest known ID: what lies between is missing.
            var upTo = added.IsEmpty ? lo + 1 : added[0].Id.Value;
            gaps = gaps.Add(GapOf(tail, upTo));
            tail = hi;
            rebuild = true;
        }
        else if (!double.IsNegativeInfinity(hi) && hi < low)
        {
            // Below the oldest known ID: what lies between is missing.
            double upTo = low + 1;
            foreach (var item in held)
                if (item.Entry.Id.Value > hi) { upTo = item.Entry.Id.Value; break; }
            gaps = gaps.Insert(0, GapOf(hi, upTo));
            low = lo;
            rebuild = true;
        }
        else
        {
            var shrunk = Subtract(lo, hi);
            if (shrunk != gaps)
            {
                gaps = shrunk;
                rebuild = true;
            }
            if (lo < low)
            {
                low = lo;
                rebuild = true;
            }
            if (hi > tail) tail = hi;
        }
        if (rebuild || !appended) items = Build();
        else if (fresh.Count > 0) items = items.AddRange(fresh);
    }

    /// <summary>The gaps without (<paramref name="lo"/>, <paramref name="hi"/>]; the same array when none changes, as when the range is
    /// empty (upstream <c>#subtract</c>).</summary>
    private ImmutableArray<ConversationTranscriptGap> Subtract(double lo, double hi)
    {
        if (hi <= lo) return gaps;
        var changed = false;
        var result = ImmutableArray.CreateBuilder<ConversationTranscriptGap>(gaps.Length + 1);
        foreach (var gap in gaps)
        {
            // Missing: IDs in (after, upTo); known now: IDs in (lo, hi].
            if (hi <= gap.After.Value || lo + 1 >= gap.UpTo.Value)
            {
                result.Add(gap);
                continue;
            }
            changed = true;
            if (lo > gap.After.Value)
            {
                var firstKnown = lo + 1;
                foreach (var item in entries)
                    if (item.Entry.Id.Value > lo) { firstKnown = item.Entry.Id.Value; break; }
                result.Add(GapOf(gap.After.Value, Math.Min(gap.UpTo.Value, firstKnown)));
            }
            if (hi + 1 < gap.UpTo.Value) result.Add(GapOf(hi, gap.UpTo.Value));
        }
        return changed ? result.ToImmutable() : gaps;
    }

    /// <summary>Items in ID order: the earlier marker until the start is known, then entries with each gap before the entry after it
    /// (upstream <c>#build</c>).</summary>
    private ImmutableArray<ConversationTranscriptItem> Build()
    {
        var built = ImmutableArray.CreateBuilder<ConversationTranscriptItem>(entries.Length + gaps.Length + 1);
        if (!double.IsNegativeInfinity(low)) built.Add(ConversationTranscriptEarlier.Instance);
        var next = 0;
        foreach (var item in entries)
        {
            while (next < gaps.Length && gaps[next].After.Value < item.Entry.Id.Value) built.Add(gaps[next++]);
            built.Add(item);
        }
        while (next < gaps.Length) built.Add(gaps[next++]);
        return built.ToImmutable();
    }

    /// <summary>The view with the current items; the previous one when nothing changed (upstream <c>#result</c>).</summary>
    private ConversationTranscriptView Result()
    {
        var current = last!;
        if (view is not null && view.Items == items && ReferenceEquals(view.Docs, current.Docs) &&
            ReferenceEquals(view.Conversation, current.Conversation))
            return view;
        view = new ConversationTranscriptView(current.Conversation, current.Docs, items);
        return view;
    }

    private static ConversationTranscriptGap GapOf(double after, double upTo) => new(new EntryId((long)after), new EntryId((long)upTo));

    /// <summary>Two ID-ordered item lists merged in one pass (upstream <c>mergeById</c>).</summary>
    private static ImmutableArray<ConversationTranscriptEntry> MergeById(ImmutableArray<ConversationTranscriptEntry> left,
        List<ConversationTranscriptEntry> right)
    {
        var merged = ImmutableArray.CreateBuilder<ConversationTranscriptEntry>(left.Length + right.Count);
        int i = 0, j = 0;
        while (i < left.Length && j < right.Count)
            merged.Add(left[i].Entry.Id.Value < right[j].Entry.Id.Value ? left[i++] : right[j++]);
        while (i < left.Length) merged.Add(left[i++]);
        while (j < right.Count) merged.Add(right[j++]);
        return merged.ToImmutable();
    }
}
