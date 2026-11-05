// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/view.ts (ConversationView, ConversationViews,
// conversationViews, advance, prefixed).
//
// Upstream's ConversationView is a plain JSON object {conversation, entries, docs}; here it is a typed immutable class whose ToJson() is
// that object, and every operation a view frame carries is relative to that JSON shape (["entries"] splices, ["docs", kind, ...] document
// operations), so applying a frame's operations to the previous revision's ToJson() yields the next revision's ToJson(). Revisions share
// unchanged parts by reference, as upstream's applyImmutable does: an untouched Entries array, Docs object or document is the same
// instance in the next revision.
//
// Threading: upstream runs on one event loop. Here publications and attachment run on the Session line, while a close listener and an
// observer's detach (a stopped watch) may run on any thread, so the mount table and each mount's observer list are guarded by one lock
// that is never held while observer code runs.
//
// Pi v1.5.0 sync (area watches, 92ab53602 and 42cb822f1): a view watch is Chord's Watch (Chord/Watch.cs) on the Session line's scheduler,
// ViewObserver.closeSession is end(failure), and the view state getter (Conversation.viewState()) is removed.
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): views observe the Session's commits as memory adopts them (Session.FollowCommits),
// deferred ones included, so a view shows a held partial or tool output at once.
// Pi v1.5.0 sync (area observers, 8b0caefc2 with v1.5.0's shapes): Harness.observeConversation() attaches a host's Observer<T> to a mount
// behind GuardedObserver<T> (ObserveAsync, upstream observe); a mount's observers are SyncObserver<ConversationView>s, watches
// (WatchObserver<T>), guarded host observers, and ViewObservers (PiSharp.Durable.Hosting's event projection, which keeps upstream
// 8b0caefc2's publication hook), and each publication hands all of them one value call. The Session's close ends a watch or a ViewObserver ended and a host
// observer failed with "session closed" (CloseEnds).
// Pi v1.5.0 sync (area view, f3255de76 with v1.5.0's shapes): ConversationView.Entries is a window over the fork-aware transcript
// (IConversation.EntriesAsync), in entry ID order, head markers in place, with Before the entry just before it. Each observer has its own
// window (ConversationWindow: From, Min default 50, Max default 2 x Min), read with bounded scans when it attaches; a mount shares only the
// documents. A publication hands each observer its own value call: the documents' operations, one splice for the entries it appended, and
// past Max one splice that drops its oldest down to Min with a set of ["before"]. Bounds that are not integers cannot be expressed here
// (int and EntryId); Min below 1, Max below Min and a From beyond JavaScript's safe integers reject with ArgumentOutOfRangeException
// (upstream RangeError). Area events (f3255de76): upstream removed watchEvents and with it the publication hook of its ViewObserver; PiSharp
// keeps the hook, internal, for PiSharp.Durable.Hosting's PiSharp-only event projection (AgentEvents.cs, decision 0013), which keeps its own
// active transcript.
// Pi v1.5.0 sync (area vocabulary, 5f312dac4): the Session's own observers are FollowCommits (upstream followCommits), and a mounted
// document's retirement is a DocumentRetireChange (change.kind === "retire", was a null value).
// Pi v1.5.0 sync (area group-commit-scheduler, e5c6182ba): a mount hydrates, and an observer's window and its create callback read, on
// the line through the line's Storage (ReadOnLineAsync's argument), which waits for what an unpublished commit wrote, so nothing a
// publication then applies is in the mount already; the guarded Storage field is gone.
// Matches view.ts at v1.5.0 apart from the adaptations above and one PiSharp addition: the internal ViewObserver (Hosting's projection) is
// a watch for the Session's close (SyncObserver<T>.IsWatch), so close ends it ended where upstream, which has no such observer, would end
// a non-watch observer failed with "session closed" (area registry-reader, the re-pin).
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>
/// Structural view of one conversation: a window over its transcript and its built-in documents (upstream <c>ConversationView</c>, spec
/// §9.3). JSON form (<see cref="ToJson"/>): <c>{conversation, entries, before?, docs}</c>.
/// </summary>
public sealed class ConversationView
{
    private JsonObjectValue? json;

    /// <summary>A view revision; <paramref name="before"/> is null when the window starts the transcript.</summary>
    public ConversationView(ConversationRecord conversation, ImmutableArray<EntryRecord> entries, EntryId? before, JsonObjectValue docs)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(docs);
        Conversation = conversation;
        Entries = entries.IsDefault ? [] : entries;
        Before = before;
        Docs = docs;
    }

    /// <summary>The conversation record.</summary>
    public ConversationRecord Conversation { get; }

    /// <summary>
    /// A window over the transcript, its fork-aware history (<see cref="IConversation.EntriesAsync"/>), in entry ID order. Head markers such
    /// as compaction summaries sit where they were written; their <see cref="EntryRecord.Head"/> tells where the entries they summarize end.
    /// What the model sees is <see cref="IConversation.ContextAsync"/>.
    /// </summary>
    public ImmutableArray<EntryRecord> Entries { get; }

    /// <summary>ID of the entry just before the window; null when the window starts the transcript.</summary>
    public EntryId? Before { get; }

    /// <summary>
    /// Built-in conversation documents keyed by kind (<c>pi.agent</c>, <c>pi.live</c>, <c>pi.inbox</c>, <c>pi.provider</c>,
    /// <c>pi.usage</c>); absent documents are absent. Each value is a JSON object.
    /// </summary>
    public JsonObjectValue Docs { get; }

    /// <summary>The mounted document of <paramref name="kind"/>; null when absent.</summary>
    public JsonObjectValue? Doc(string kind) => Docs.TryGetValue(kind, out var value) ? (JsonObjectValue)value : null;

    /// <summary>The mounted document of <paramref name="token"/>; null when absent or not a mounted kind.</summary>
    public JsonObjectValue? Doc(ConversationDocToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Doc(token.Definition.Kind);
    }

    /// <summary>
    /// The view as upstream's JSON object <c>{conversation, entries, before?, docs}</c> (<c>before</c> a key only when set), which the
    /// operations of view frames address. Computed once per revision.
    /// </summary>
    public JsonObjectValue ToJson()
    {
        if (Volatile.Read(ref json) is { } built) return built;
        List<KeyValuePair<string, JsonValue>> members =
        [
            KeyValuePair.Create("conversation", (JsonValue)Conversation.ToJson()),
            KeyValuePair.Create("entries", (JsonValue)JsonValue.Array(Entries.Select(entry => (JsonValue)entry.ToJson()))),
        ];
        if (Before is { } before) members.Add(KeyValuePair.Create("before", (JsonValue)before.ToJson()));
        members.Add(KeyValuePair.Create("docs", (JsonValue)Docs));
        var value = JsonValue.Object(members);
        return Interlocked.CompareExchange(ref json, value, null) ?? value;
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>
/// Which entries a view's window holds (upstream <c>ConversationWindow</c>, spec §9.3). The window holds between <see cref="Min"/> and
/// <see cref="Max"/> entries: it starts with the newest <see cref="Min"/> entries, takes appends, and once it would hold more than
/// <see cref="Max"/>, drops its oldest in one splice down to <see cref="Min"/>. With <see cref="From"/>, it starts at <see cref="From"/>
/// instead when at most <see cref="Max"/> entries are at or after it.
/// </summary>
public sealed record ConversationWindow
{
    /// <summary>
    /// Where the first value's window starts, inclusive, such as right after the newest entry a client has
    /// (<see cref="ConversationTranscript.Resume"/>). It selects only that first value: every entry committed after the attachment is
    /// appended, whatever its ID. A <see cref="From"/> past the newest entry starts the window empty, with
    /// <see cref="ConversationView.Before"/> the newest entry.
    /// </summary>
    public EntryId? From { get; init; }

    /// <summary>Default 50; at least 1.</summary>
    public int? Min { get; init; }

    /// <summary>Default <c>2 * Min</c>; at least <see cref="Min"/>.</summary>
    public int? Max { get; init; }
}

/// <summary>
/// A mount's observer that also gets every publication (upstream 8b0caefc2's <c>ViewObserver</c> with its <c>publication</c> hook, which
/// f3255de76 removed with watchEvents; PiSharp-only since): PiSharp.Durable.Hosting's agent event projection. Its next revisions come as
/// one value call per publication that changed its window; the Session's close ends it as a watch (<c>ended</c>, or <c>failed</c> with the
/// error that failed the Session).
/// </summary>
internal abstract class ViewObserver : SyncObserver<ConversationView>
{
    /// <summary>A next revision of its window and the operations that produced it.</summary>
    public override void Advance(WatchCall<ConversationView> call, DurableContext context) { }

    /// <summary>Every publication, after the mount took it; <paramref name="before"/> and <paramref name="after"/> are its window's
    /// revisions and <paramref name="ops"/> the operations between them, possibly none.</summary>
    public virtual void Publication(ConversationView before, ConversationView after, ImmutableArray<Op> ops, ObservedCommit publication,
        DurableContext context) { }

    public override bool IsWatch => true;
}

/// <summary>
/// The Harness's conversation view mounts (upstream <c>ConversationViews</c>): at most one per conversation, built on the Session line by its
/// first observer and dropped with its last. Each mount advances from the Session's commits as memory adopts them, deferred ones before
/// they are stored, and hands each observer the next revision of its own window.
/// </summary>
internal sealed class ConversationViews
{
    /// <summary>Default <see cref="ConversationWindow.Min"/>.</summary>
    internal const int DefaultWindowMin = 50;
    private const int ScanPageSize = 256;
    /// <summary>JavaScript's <c>Number.MAX_SAFE_INTEGER</c>, the bound of upstream's integer checks.</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly ConversationDocToken[] Mounted =
        [Agents.AgentDoc, Live.LiveDoc, Inbox.InboxDoc, ProviderIdentity.ProviderDoc, UsageLedger.UsageDoc];
    private static readonly HashSet<string> MountedKinds = new(Mounted.Select(token => token.Definition.Kind), StringComparer.Ordinal);
    private static readonly ImmutableArray<PathSegment> EntriesPath = [PathSegment.Of("entries")];
    private static readonly ImmutableArray<PathSegment> BeforePath = [PathSegment.Of("before")];

    private readonly DurableHarness session;
    private readonly Lock gate = new();
    private readonly Dictionary<ConversationId, Mount> mounts = [];
    // Guarded by gate.
    private bool closed;

    public ConversationViews(DurableHarness session)
    {
        this.session = session;
        session.FollowCommits((publication, context) =>
        {
            KeyValuePair<ConversationId, Mount>[] current;
            lock (gate) current = [.. mounts];
            foreach (var (id, mount) in current) AdvanceMount(id, mount, publication, context, Report);
        });
        session.SubscribeClose(() =>
        {
            Mount[] current;
            lock (gate)
            {
                closed = true;
                current = [.. mounts.Values];
                mounts.Clear();
            }
            var ends = CloseEnds.Of(session.Failure);
            foreach (var mount in current)
                foreach (var windowed in mount.Snapshot(gate))
                    Session.Contained(() => windowed.Observer.End(ends.For(windowed.Observer.IsWatch)), Report);
        });
    }

    /// <summary>Reports an observer's listener error through the Harness (upstream <c>report</c>).</summary>
    public void Report(Exception error) => session.Report(error);

    /// <summary>The watch scheduler of the Session (upstream <c>queueMicrotask</c>).</summary>
    public Action<Action> Schedule => session.Defer;

    /// <summary>The view mounts of <paramref name="harness"/>, for adapters built on them (upstream <c>conversationViews</c>).</summary>
    public static ConversationViews Of(IHarness harness) =>
        harness is DurableHarness durable ? durable.Views : throw new ArgumentException("Not a Harness", nameof(harness));

    /// <summary>
    /// Attaches a synchronous observer of the view (<see cref="IHarness.ObserveConversationAsync"/>), behind
    /// <see cref="GuardedObserver{T}"/>, with its own <paramref name="window"/>; <paramref name="context"/> governs only the attachment
    /// (upstream <c>observe</c>).
    /// </summary>
    public async Task<Observation<ConversationView>> ObserveAsync(ConversationId id, Observer<ConversationView> observer, DurableContext context,
        ConversationWindow? window = null)
    {
        var (value, guarded) = await AttachCoreAsync(id, window, (current, release, _) =>
            Task.FromResult((current, new GuardedObserver<ConversationView>(observer, release, (error, _) => Report(error)))), context)
            .ConfigureAwait(false);
        if (context.IsCancellationRequested)
        {
            guarded.Detach();
            context.ThrowIfCancellationRequested();
        }
        return new Observation<ConversationView>(value, guarded);
    }

    /// <summary>A serialized watch of the view, Chord's <see cref="Watch{T}"/>, an observer of its mount with its own
    /// <paramref name="window"/>; cancelling <paramref name="context"/> ends it aborted (upstream <c>watch</c>).</summary>
    public async Task<IWatchHandle<ConversationView>> WatchAsync(ConversationId id, DurableContext context, ConversationWindow? window = null)
    {
        var (watch, _) = await AttachCoreAsync(id, window, (value, release, _) =>
        {
            var created = new Watch<ConversationView>(value, release, Report, view => view.ToJson(), Schedule);
            return Task.FromResult((created, new WatchObserver<ConversationView>(created)));
        }, context).ConfigureAwait(false);
        if (context.IsCancellationRequested)
        {
            watch.Abort();
            context.ThrowIfCancellationRequested();
        }
        if (context.HasCancellation) watch.ObserveAbort(context.CancellationToken);
        return watch;
    }

    /// <summary>
    /// Registers an observer created from its window's first revision, atomically on the Session line: it sees every later publication and
    /// nothing earlier (upstream <c>attach</c>). <paramref name="create"/> may read committed Storage, still on the line. The returned detach
    /// drops the observer, and the mount with its last observer.
    /// </summary>
    public async Task<(T Result, Action Detach)> AttachAsync<T>(ConversationId id,
        Func<ConversationView, Action, IStorage, Task<(T Result, ViewObserver Observer)>> create, DurableContext context,
        ConversationWindow? window = null)
    {
        var (result, _, detach) = await AttachObserverAsync(id, window, create, context).ConfigureAwait(false);
        return (result, detach);
    }

    /// <summary><see cref="AttachAsync"/> for the view's own observers, which keep what they attached.</summary>
    private async Task<(T Result, TObserver Observer)> AttachCoreAsync<T, TObserver>(ConversationId id, ConversationWindow? window,
        Func<ConversationView, Action, IStorage, Task<(T Result, TObserver Observer)>> create, DurableContext context)
        where TObserver : SyncObserver<ConversationView>
    {
        var (result, observer, _) = await AttachObserverAsync(id, window, create, context).ConfigureAwait(false);
        return (result, observer);
    }

    private async Task<(T Result, TObserver Observer, Action Detach)> AttachObserverAsync<T, TObserver>(ConversationId id,
        ConversationWindow? window, Func<ConversationView, Action, IStorage, Task<(T Result, TObserver Observer)>> create, DurableContext context)
        where TObserver : SyncObserver<ConversationView>
    {
        var bounds = ResolveWindow(window);
        return await session.ReadOnLineAsync(async line =>
        {
            Mount? existing;
            lock (gate) mounts.TryGetValue(id, out existing);
            var mount = existing ?? await BuildAsync(line, id, context).ConfigureAwait(false);
            var (entries, before) = await LoadWindowAsync(line, id, bounds, context).ConfigureAwait(false);
            var value = new ConversationView(mount.Conversation, entries, before, mount.Docs);
            Windowed? windowed = null;
            void Detach()
            {
                lock (gate)
                {
                    if (windowed is not null) mount.Observers.Remove(windowed);
                    if (mount.Observers.Count == 0 && mounts.TryGetValue(id, out var current) && current == mount) mounts.Remove(id);
                }
            }
            var (result, created) = await create(value, Detach, line).ConfigureAwait(false);
            lock (gate)
            {
                // Close or cancellation may begin while the mount hydrates; register nothing then.
                if (closed) throw HarnessUtil.ClosedError(session);
                context.ThrowIfCancellationRequested();
                windowed = new Windowed(bounds.Min, bounds.Max, value, created);
                mounts[id] = mount;
                mount.Observers.Add(windowed);
            }
            return (result, created, (Action)Detach);
        }).ConfigureAwait(false);
    }

    /// <summary>A window with its defaults applied (upstream <c>WindowBounds</c>).</summary>
    private readonly record struct WindowBounds(EntryId? From, int Min, int Max);

    /// <summary><paramref name="window"/> with its defaults (upstream <c>resolveWindow</c>); throws <see cref="ArgumentOutOfRangeException"/>
    /// (upstream <c>RangeError</c>) for <c>Min</c> below 1, <c>Max</c> below <c>Min</c>, or a <c>From</c> beyond JavaScript's safe
    /// integers.</summary>
    private static WindowBounds ResolveWindow(ConversationWindow? window)
    {
        var min = window?.Min ?? DefaultWindowMin;
        if (min < 1) throw new ArgumentOutOfRangeException(nameof(window), min, $"Window min must be an integer >= 1, got {min}");
        // 2 * min past int's range is more entries than a window can hold: int's maximum.
        var max = window?.Max ?? (int)Math.Min(2L * min, int.MaxValue);
        if (max < min) throw new ArgumentOutOfRangeException(nameof(window), max, $"Window max must be an integer >= min ({min}), got {max}");
        var from = window?.From;
        if (from is { } start && (start.Value > MaxSafeInteger || start.Value < -MaxSafeInteger))
            throw new ArgumentOutOfRangeException(nameof(window), start.Value, $"Window from must be an entry ID, got {start.Value}");
        return new WindowBounds(from, min, max);
    }

    /// <summary>Hydrates a mount on the line, through the line's <paramref name="storage"/>.</summary>
    private async Task<Mount> BuildAsync(IStorage storage, ConversationId id, DurableContext context)
    {
        var conversation = await storage.GetConversationAsync(id, context).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Conversation {id} does not exist");
        var docs = new List<KeyValuePair<string, JsonValue>>();
        var incarnations = new Dictionary<string, (DocumentId Id, int Version)>(StringComparer.Ordinal);
        foreach (var token in Mounted)
        {
            if (await session.ConversationDocumentOnLineAsync(token, id, context).ConfigureAwait(false) is not { } loaded) continue;
            docs.Add(KeyValuePair.Create(token.Definition.Kind, (JsonValue)loaded.Value));
            incarnations[token.Definition.Kind] = (loaded.Record.Id, loaded.Version);
        }
        return new Mount(conversation, JsonObjectValue.From(docs), incarnations);
    }

    /// <summary>
    /// A new observer's window, read on the Session line with bounded scans (upstream <c>loadWindow</c>): the entries from <c>From</c> when
    /// at most <c>Max</c> are at or after it, otherwise the newest <c>Min</c>, and the entry before them.
    /// </summary>
    private static async Task<(ImmutableArray<EntryRecord> Entries, EntryId? Before)> LoadWindowAsync(IStorage storage, ConversationId id,
        WindowBounds bounds, DurableContext context)
    {
        if (bounds.From is { } from)
        {
            var since = await ScanUpToAsync(storage, new EntryQuery(id) { MinEntryId = from, Order = ScanOrder.Ascending }, bounds.Max + 1L,
                context).ConfigureAwait(false);
            if (since.Count <= bounds.Max)
            {
                var query = new EntryQuery(id) { MaxEntryId = new EntryId(from.Value - 1) };
                var previous = (await storage.ScanEntriesAsync(query, 1, null, context).ConfigureAwait(false)).Items;
                return ([.. since], previous.IsEmpty ? null : previous[0].Id);
            }
        }
        var newest = await ScanUpToAsync(storage, new EntryQuery(id), bounds.Min + 1L, context).ConfigureAwait(false);
        EntryId? before = null;
        if (newest.Count > bounds.Min)
        {
            before = newest[^1].Id;
            newest.RemoveAt(newest.Count - 1);
        }
        newest.Reverse();
        return ([.. newest], before);
    }

    /// <summary>Up to <paramref name="limit"/> entries of <paramref name="query"/>, following cursors past short pages (upstream
    /// <c>scanUpTo</c>).</summary>
    private static async Task<List<EntryRecord>> ScanUpToAsync(IStorage storage, EntryQuery query, long limit, DurableContext context)
    {
        var entries = new List<EntryRecord>();
        JsonObjectValue? cursor = null;
        do
        {
            var page = await storage.ScanEntriesAsync(query, (int)Math.Min(ScanPageSize, limit - entries.Count), cursor, context)
                .ConfigureAwait(false);
            entries.AddRange(page.Items);
            cursor = page.Next;
        } while (cursor is not null && entries.Count < limit);
        return entries;
    }

    /// <summary>
    /// Derives the mount's operations from one publication and hands each observer the next revision of its window; one that throws is
    /// reported through <paramref name="report"/>, and the others still get theirs (upstream <c>advance</c>). Documents are applied once and
    /// shared; each window takes the appended entries and, past its <c>Max</c>, drops its oldest down to its <c>Min</c> in one splice. View
    /// observers then get the publication. Runs on the Session line.
    /// </summary>
    private void AdvanceMount(ConversationId id, Mount mount, ObservedCommit publication, DurableContext context, Action<Exception> report)
    {
        var docOps = new List<Op>();
        // Entry writes are published in ID order.
        var appended = new List<EntryRecord>();
        foreach (var change in publication.Changes)
        {
            if (change is EntryWrite { Value: var entry } && entry.ConversationId == id)
            {
                appended.Add(entry);
                continue;
            }
            if (change is not DocumentCommitChange document || document.ConversationId != id) continue;
            var kind = document.Record.Kind;
            if (!MountedKinds.Contains(kind) || document.Record.Key is not null) continue;
            ImmutableArray<PathSegment> path = [PathSegment.Of("docs"), PathSegment.Of(kind)];
            var isMounted = mount.Incarnations.TryGetValue(kind, out var mounted);
            if (document is not DocumentValueChange valued)
            {
                if (!isMounted || mounted.Id != document.Record.Id) continue;
                mount.Incarnations.Remove(kind);
                docOps.Add(new DeleteOp(path));
            }
            else if (isMounted && mounted.Id == valued.Record.Id && mounted.Version == valued.Version)
            {
                foreach (var op in valued.Ops) docOps.Add(Prefixed(op, path));
            }
            else
            {
                mount.Incarnations[kind] = (valued.Record.Id, valued.Version);
                docOps.Add(new SetOp(path, valued.Value));
            }
        }
        var observers = mount.Snapshot(gate);
        var frameContext = context.WithoutCancellation();
        var touched = docOps.Count > 0 || appended.Count > 0;
        // Document operations address ["docs", kind, ...]; Docs is the "docs" object itself.
        if (docOps.Count > 0) mount.Docs = (JsonObjectValue)DeltaApplyTrusted.Apply(mount.Docs, docOps.Select(Unprefixed));
        ImmutableArray<JsonValue> inserted = [.. appended.Select(entry => (JsonValue)entry.ToJson())];
        var previous = new ConversationView[observers.Length];
        var changes = new ImmutableArray<Op>[observers.Length];
        for (var index = 0; index < observers.Length; index++)
        {
            var windowed = observers[index];
            previous[index] = windowed.Value;
            changes[index] = [];
            if (!touched) continue;
            var entries = windowed.Value.Entries;
            var before = windowed.Value.Before;
            var ops = new List<Op>(docOps);
            if (appended.Count > 0)
            {
                ops.Add(new SpliceOp(EntriesPath, entries.Length, 0, inserted));
                entries = entries.AddRange(appended);
                if (entries.Length > windowed.Max)
                {
                    var drop = entries.Length - windowed.Min;
                    var first = entries[drop - 1].Id;
                    before = first;
                    entries = entries[drop..];
                    ops.Add(new SpliceOp(EntriesPath, 0, drop, []));
                    ops.Add(new SetOp(BeforePath, first.ToJson()));
                }
            }
            windowed.Value = new ConversationView(mount.Conversation, entries, before, mount.Docs);
            changes[index] = [.. ops];
            // One call per observer: each has its own window.
            var call = new WatchValueCall<ConversationView>(windowed.Value, changes[index]);
            Session.Contained(() => windowed.Observer.Advance(call, frameContext), report);
        }
        for (var index = 0; index < observers.Length; index++)
        {
            if (observers[index].Observer is not ViewObserver view) continue;
            var (before, after, ops) = (previous[index], observers[index].Value, changes[index]);
            Session.Contained(() => view.Publication(before, after, ops, publication, frameContext), report);
        }
    }

    /// <summary><paramref name="op"/> moved under <paramref name="prefix"/>; a root replacement becomes a set of the prefix (upstream
    /// <c>prefixed</c>).</summary>
    private static Op Prefixed(Op op, ImmutableArray<PathSegment> prefix) => op switch
    {
        ReplaceOp replace => new SetOp(prefix, replace.Value),
        PathOp path => path with { Path = [.. prefix, .. path.Path] },
        _ => throw new ArgumentException($"Unknown operation {op}", nameof(op)),
    };

    /// <summary>A document operation relative to the <c>docs</c> object: its path without the leading <c>"docs"</c>.</summary>
    private static Op Unprefixed(Op op) => op is PathOp path ? path with { Path = path.Path.RemoveAt(0) } :
        throw new ArgumentException($"A view operation never replaces the root: {op}", nameof(op));

    /// <summary>One observer of a mount and its window: the bounds and the revision it was last given (upstream <c>Windowed</c>).</summary>
    private sealed class Windowed(int min, int max, ConversationView value, SyncObserver<ConversationView> observer)
    {
        public int Min { get; } = min;
        public int Max { get; } = max;
        /// <summary>Written on the Session line only.</summary>
        public ConversationView Value { get; set; } = value;
        public SyncObserver<ConversationView> Observer { get; } = observer;
    }

    /// <summary>One conversation's mount: the documents it shows, shared by every observer, and the observers, each with its own window
    /// over the entries (upstream <c>Mount</c>).</summary>
    private sealed class Mount(ConversationRecord conversation, JsonObjectValue docs,
        Dictionary<string, (DocumentId Id, int Version)> incarnations)
    {
        public ConversationRecord Conversation { get; } = conversation;
        /// <summary>Written on the Session line only.</summary>
        public JsonObjectValue Docs { get; set; } = docs;
        /// <summary>Mounted incarnation and definition version per kind; another incarnation or version is set whole. Session line
        /// only.</summary>
        public Dictionary<string, (DocumentId Id, int Version)> Incarnations { get; } = incarnations;
        /// <summary>Watches, guarded host observers and view observers with their windows, in registration order; guarded by the views'
        /// lock.</summary>
        public List<Windowed> Observers { get; } = [];

        public Windowed[] Snapshot(Lock gate)
        {
            lock (gate) return [.. Observers];
        }
    }
}
