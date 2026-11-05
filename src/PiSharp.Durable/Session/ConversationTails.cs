// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/session/conversation-tails.ts (ConversationTails, HeadMarker's
// capacities, use, put). The newest entry and newest head marker of each recently used conversation, kept current from the Session's
// commits (area publication-caches, e5c6182ba).
//
// Concurrency. Upstream reads and applies on the Session's one thread. Here Apply runs at a publication, on the writer's thread, while line
// jobs read other conversations on theirs, so one short lock guards both maps; it is never held while a read runs. A read of a
// conversation still keeps what Storage returned, as upstream's does: it runs on the line after touching the conversation's appends
// (Session.TouchEntries), so no commit that appends to it is unpublished, and no later one is admitted while the job holds the line. Entry
// records are immutable here, so a kept marker needs no freeze.
namespace PiSharp.Durable;

/// <summary>
/// The newest entry ID and newest head marker per conversation, for the Session that owns its Storage (upstream <c>ConversationTails</c>):
/// every entry is written by one of its commits, so <see cref="Apply"/> keeps both current, and a value read from Storage stays valid until
/// the next entry of that conversation. Read only on the Session line after touching the conversation's appends, so no commit that appends
/// to it is unpublished and a value read from Storage can be kept. The newest entry visible from a conversation is its own newest, or for a
/// fork without entries of its own the parent's entry at the fork point: IDs grow, so a conversation's own new entry is always the newest.
/// Each map is bounded by a count, the least recently used conversation leaving first.
/// </summary>
internal sealed class ConversationTails
{
    /// <summary>Conversations whose newest entry ID is kept: IDs only, so many (upstream <c>LATEST_CAPACITY</c>).</summary>
    internal const int LatestCapacity = 4096;

    /// <summary>Conversations whose newest head marker is kept: whole entries (a compaction summary is one), so fewer (upstream
    /// <c>MARKER_CAPACITY</c>).</summary>
    internal const int MarkerCapacity = 256;

    private readonly Lock gate = new();
    // Newest entry ID per conversation; a known conversation without entries has a null ID. Guarded by gate.
    private readonly Bounded<KnownId> latest = new(LatestCapacity);
    // Newest head marker per conversation; a known conversation without one has a null marker. Guarded by gate.
    private readonly Bounded<KnownMarker> markers = new(MarkerCapacity);

    /// <summary>The newest entry ID visible from <paramref name="conversationId"/>, read with <paramref name="read"/> when not known
    /// (upstream <c>latest</c>).</summary>
    public async Task<EntryId?> LatestAsync(ConversationId conversationId, Func<Task<EntryId?>> read)
    {
        lock (gate)
            if (latest.Use(conversationId) is { } known) return known.Value;
        var found = await read().ConfigureAwait(false);
        lock (gate) latest.Put(conversationId, new KnownId(found));
        return found;
    }

    /// <summary>
    /// The newest visible head marker at or before <paramref name="atOrBefore"/> (any when null), read with <paramref name="read"/> when not
    /// known (upstream <c>marker</c>). The newest marker answers every cutoff at or after it, and no marker answers every cutoff.
    /// </summary>
    public async Task<EntryRecord?> MarkerAsync(ConversationId conversationId, EntryId? atOrBefore, Func<Task<EntryRecord?>> read)
    {
        KnownId? newest;
        lock (gate)
        {
            if (markers.Use(conversationId) is { } marker)
            {
                if (marker.Value is not { } kept) return null;
                if (atOrBefore is not { } cutoff || cutoff.Value >= kept.Id.Value) return kept;
            }
            newest = latest.Peek(conversationId);
        }
        var found = await read().ConfigureAwait(false);
        // Only a read that no newer entry can change: no cutoff, or one at or after the newest entry.
        if (atOrBefore is not { } at || (newest is { } known && (known.Value?.Value ?? 0) <= at.Value))
            lock (gate) markers.Put(conversationId, new KnownMarker(found));
        return found;
    }

    /// <summary>The newest entry ID of <paramref name="conversationId"/> if known, without reading (upstream <c>peek</c>): false when unknown;
    /// else <paramref name="newest"/> is it, null for none.</summary>
    public bool TryPeek(ConversationId conversationId, out EntryId? newest)
    {
        lock (gate)
        {
            if (latest.Peek(conversationId) is { } known)
            {
                newest = known.Value;
                return true;
            }
        }
        newest = null;
        return false;
    }

    /// <summary>At a commit's publication: applies a durable batch (upstream <c>apply</c>).</summary>
    public void Apply(IReadOnlyList<StorageWrite> writes)
    {
        lock (gate)
        {
            foreach (var write in writes)
            {
                if (write is ConversationWrite conversation)
                {
                    // A new root conversation sees no entries; a fork sees its parent's up to the fork point.
                    if (conversation.Value.Parent is not null) continue;
                    latest.Put(conversation.Value.Id, new KnownId(null));
                    markers.Put(conversation.Value.Id, new KnownMarker(null));
                }
                else if (write is EntryWrite { Value: var entry })
                {
                    latest.Put(entry.ConversationId, new KnownId(entry.Id));
                    if (entry.Head is not null) markers.Put(entry.ConversationId, new KnownMarker(entry));
                }
            }
        }
    }

    /// <summary>How many conversations have their newest entry kept, for tests that bound memory (upstream <c>size</c>).</summary>
    public int Count
    {
        get { lock (gate) return latest.Count; }
    }

    /// <summary>How many conversations have their newest head marker kept, for tests that bound memory (upstream <c>markers</c>).</summary>
    public int Markers
    {
        get { lock (gate) return markers.Count; }
    }

    /// <summary>A known newest entry ID, null for none (upstream's <c>null</c>, where a missing map entry is upstream's
    /// <c>undefined</c>).</summary>
    private sealed record KnownId(EntryId? Value);

    /// <summary>A known newest head marker, null for none.</summary>
    private sealed record KnownMarker(EntryRecord? Value);

    /// <summary>Per conversation, bounded to a count, the least recently used leaving first (upstream's <c>use</c> and <c>put</c> over a
    /// Map).</summary>
    private sealed class Bounded<T>(int capacity) where T : class
    {
        private readonly Lru<ConversationId, T> map = new(capacity);

        public int Count => map.Count;

        /// <summary>The value for <paramref name="conversationId"/>, made the most recently used (upstream <c>use</c>).</summary>
        public T? Use(ConversationId conversationId) => map.Get(conversationId);

        /// <summary>The value for <paramref name="conversationId"/>, its place unchanged (upstream's <c>Map.get</c>).</summary>
        public T? Peek(ConversationId conversationId) => map.Peek(conversationId);

        /// <summary>Sets the value as the most recently used, dropping the least recently used beyond the capacity (upstream <c>put</c>).</summary>
        public void Put(ConversationId conversationId, T value) => map.Set(conversationId, value);
    }
}
