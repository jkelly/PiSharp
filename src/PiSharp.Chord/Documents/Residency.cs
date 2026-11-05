// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/residency.ts
//
// Residency of keyed documents (slice 6a §10, rule 23): which keyed documents are in memory, and the eviction of persisted ones nobody
// pinned and nobody read or wrote for the retention. One idle structure and one timer per object, Durable's (Session.TouchDocument in
// PiSharp.Durable's Session.cs): an insertion-ordered list whose oldest entry the timer waits for, a touch moving an entry to the back at
// most once per granule (O(1) per touch, no timer per key). Mappings:
// - Date.now and setTimeout are a TimeProvider's clock and timers (TimeProvider.System unless one is given, upstream's fake timers in the
//   tests), in milliseconds as doubles, as Durable's Session reads them; clearTimeout is the timer's Dispose.
// - Upstream's insertion-ordered Map of residents is a linked list with an index (reference identity), as in Durable's Session.
// - The timer fires on the thread pool while touches come from the object's calls, so the idle structure and the pending sweep are under a
//   short lock; evictions run outside it, oldest first, as upstream's loop does them.
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;

namespace PiSharp.Chord.Documents;

/// <summary>What a resident's slot does when the sweep evicts it (upstream's <c>{ evict(resident) }</c>): the keyed slot drops it.</summary>
internal interface IResidentSlot
{
    /// <summary>The sweep found <paramref name="resident"/> idle past the retention: it leaves memory.</summary>
    void Evict(Resident resident);
}

/// <summary>One keyed document in memory (upstream <c>Resident</c>): its slot, its tracker and, persisted, what is stored of it.</summary>
internal sealed class Resident
{
    public Resident(IResidentSlot slot, string key, Tracker<JsonObject> tracker, Stored? stored, long version)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tracker);
        Slot = slot;
        Key = key;
        Tracker = tracker;
        Stored = stored;
        Version = version;
    }

    public IResidentSlot Slot { get; }

    public string Key { get; }

    public Tracker<JsonObject> Tracker { get; set; }

    /// <summary>What is stored of it; null in memory only.</summary>
    public Stored? Stored { get; set; }

    /// <summary>The definition version the tracker's value is at: below the slot's, it migrates on its next access.</summary>
    public long Version { get; set; }
}

/// <summary>The idle persisted keyed documents of one object, oldest first, and the one timer evicting them (upstream
/// <c>Residency</c>).</summary>
internal sealed class Residency
{
    /// <summary>How long an unpinned persisted keyed document stays in memory after its last read or write (upstream <c>RETENTION_MS</c>,
    /// slice 6a §10): ten minutes.</summary>
    internal const double RetentionMs = 10 * 60_000;

    /// <summary>A touch moves an entry at most once per granule: a tenth of the retention, up to 1 s (Durable's rule).</summary>
    private static readonly double GranuleMs = Math.Min(1_000, RetentionMs / 10);

    // Runs a sweep on the object's line.
    private readonly Action<Action> onLine;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly LinkedList<(Resident Resident, double Since)> idle = new();
    private readonly Dictionary<Resident, LinkedListNode<(Resident Resident, double Since)>> index = new(ReferenceEqualityComparer.Instance);

    // The pending sweep: when it is due and its timer; stays set until its job runs on the line.
    private Sweep? sweep;
    private bool ended;

    /// <summary>A residency whose sweeps <paramref name="onLine"/> runs on the object's line, timed by <paramref name="time"/> (by default
    /// the system's clock and timers).</summary>
    public Residency(Action<Action> onLine, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        this.onLine = onLine;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>A use of an unpinned <paramref name="resident"/>: it becomes the newest idle one (once per granule), and a sweep is
    /// scheduled.</summary>
    public void Touch(Resident resident)
    {
        ArgumentNullException.ThrowIfNull(resident);
        var now = Now();
        lock (gate)
        {
            if (index.TryGetValue(resident, out var node))
            {
                // A hot document moves at most once per granule: it may then leave up to a granule early (a reload).
                if (now - node.Value.Since < GranuleMs) return;
                idle.Remove(node);
            }
            index[resident] = idle.AddLast((resident, now));
            if (sweep is null) ScheduleLocked();
        }
    }

    /// <summary><paramref name="resident"/> is pinned or left memory: it is no longer idle.</summary>
    public void Forget(Resident resident)
    {
        ArgumentNullException.ThrowIfNull(resident);
        lock (gate)
        {
            if (index.Remove(resident, out var node)) idle.Remove(node);
        }
    }

    /// <summary>The object ended: nothing is evicted any more, the timer is cleared.</summary>
    public void End()
    {
        lock (gate)
        {
            ended = true;
            idle.Clear();
            index.Clear();
            sweep?.Timer?.Dispose();
            sweep = null;
        }
    }

    private double Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Schedules the eviction of the oldest idle document when its retention expires; under the lock.</summary>
    private void ScheduleLocked()
    {
        if (ended || idle.First is not { } oldest) return;
        var at = oldest.Value.Since + RetentionMs;
        var next = new Sweep();
        sweep = next;
        // A timer firing early sweeps nothing and schedules the next one. Its callback only queues the sweep on the line.
        next.Timer = time.CreateTimer(static state =>
        {
            var (residency, due) = ((Residency, Sweep))state!;
            residency.onLine(() =>
            {
                lock (residency.gate)
                {
                    if (residency.sweep == due) residency.sweep = null;
                }
                due.Timer?.Dispose();
                residency.EvictExpired();
            });
        }, (this, next), TimeSpan.FromMilliseconds(Math.Max(0, at - Now())), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// On the line: evicts the idle documents whose retention passed, oldest first, then schedules the next sweep. No load in flight needs a
    /// pin against it: cold loads run on the line too, so a sweep never runs during one, and a load installing an unpinned key touches it
    /// (it is then the newest idle one).
    /// </summary>
    private void EvictExpired()
    {
        var expired = new List<Resident>();
        lock (gate)
        {
            if (ended) return;
            var limit = Now() - RetentionMs;
            while (idle.First is { } oldest && oldest.Value.Since <= limit)
            {
                idle.RemoveFirst();
                index.Remove(oldest.Value.Resident);
                expired.Add(oldest.Value.Resident);
            }
        }
        foreach (var resident in expired) resident.Slot.Evict(resident);
        lock (gate)
        {
            if (sweep is null) ScheduleLocked();
        }
    }

    /// <summary>A pending sweep: its timer.</summary>
    private sealed class Sweep
    {
        public ITimer? Timer { get; set; }
    }
}
