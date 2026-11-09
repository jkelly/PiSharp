namespace PiSharp.Chord.Legacy;

/// <summary>Shared consumer bindings. Only hydrated idle bindings linger; least-recently-released bindings leave first.</summary>
public sealed class LegacyBindingRetention<TKey, T> : IDisposable where TKey : notnull where T : class
{
    private readonly object gate = new();
    private readonly Dictionary<TKey, Entry> entries;
    private readonly LinkedList<Entry> idle = [];
    private readonly Func<TKey, T> create;
    private readonly Action<T> close;
    private readonly Func<T, bool> retainable;
    private readonly Func<TKey, TimeSpan> linger;
    private readonly TimeProvider clock;
    private readonly int capacity;
    private readonly Action<Exception>? onError;
    private bool disposed;
    public LegacyBindingRetention(Func<TKey, T> create, Action<T> close, Func<T, bool> retainable,
        Func<TKey, TimeSpan> linger, int capacity = 4, TimeProvider? timeProvider = null, IEqualityComparer<TKey>? comparer = null,
        Action<Exception>? onError = null)
    {
        this.create = create; this.close = close; this.retainable = retainable; this.linger = linger;
        this.capacity = capacity; clock = timeProvider ?? TimeProvider.System; entries = new(comparer); this.onError = onError;
    }
    public int Count { get { lock (gate) return entries.Count; } }
    public int Lingering { get { lock (gate) return idle.Count; } }
    public Lease Acquire(TKey key)
    {
        Entry entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out entry!)) { entry = new(key, () => create(key)); entries.Add(key, entry); }
            entry.References++;
            if (entry.Idle is not null) { idle.Remove(entry.Idle); entry.Idle = null; }
            entry.Timer?.Dispose(); entry.Timer = null;
        }
        try
        {
            _ = entry.Value.Value;
            bool removed; lock (gate) removed = entry.Removed;
            if (removed) { Close([entry]); throw new ObjectDisposedException(nameof(LegacyBindingRetention<TKey, T>)); }
            return new(this, entry);
        }
        catch { Remove(entry); throw; }
    }
    /// <summary>A released lease drops its binding immediately, even if the lease object itself remains reachable.</summary>
    public sealed class Lease : IDisposable
    {
        private LegacyBindingRetention<TKey, T>? owner;
        private Entry? entry;
        private readonly object releaseGate = new();
        internal Lease(LegacyBindingRetention<TKey, T> owner, Entry entry) { this.owner = owner; this.entry = entry; }
        public T Value => Volatile.Read(ref entry)?.Value.Value ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            Entry? releasing; LegacyBindingRetention<TKey, T>? pool;
            lock (releaseGate) { releasing = entry; pool = owner; entry = null; owner = null; }
            if (releasing is not null && pool is not null) pool.Release(releasing);
        }
    }
    private void Release(Entry entry)
    {
        List<Entry> remove = [];
        lock (gate)
        {
            if (entry.Removed || --entry.References > 0) return;
            var delay = linger(entry.Key);
            if (disposed || capacity <= 0 || delay <= TimeSpan.Zero || !retainable(entry.Value.Value))
                Detach(entry, remove);
            else
            {
                entry.Idle = idle.AddLast(entry);
                entry.Timer = clock.CreateTimer(_ =>
                {
                    try { RemoveIdle(entry); } catch (Exception error) { try { onError?.Invoke(error); } catch (Exception) { } }
                }, null, delay, Timeout.InfiniteTimeSpan);
                while (idle.Count > capacity) Detach(idle.First!.Value, remove);
            }
        }
        Close(remove);
    }
    private void RemoveIdle(Entry entry)
    {
        List<Entry> remove = [];
        lock (gate) { if (!entry.Removed && entry.References == 0) Detach(entry, remove); }
        Close(remove);
    }
    private void Remove(Entry entry)
    { List<Entry> remove = []; lock (gate) Detach(entry, remove); Close(remove); }
    private void Detach(Entry entry, List<Entry> removed)
    {
        if (entry.Removed) return;
        entry.Removed = true; entries.Remove(entry.Key);
        if (entry.Idle is not null) { idle.Remove(entry.Idle); entry.Idle = null; }
        entry.Timer?.Dispose(); entry.Timer = null; removed.Add(entry);
    }
    private void Close(IEnumerable<Entry> removed)
    {
        List<Exception>? errors = null;
        foreach (var entry in removed)
            if (entry.Value.IsValueCreated && Interlocked.Exchange(ref entry.Closed, 1) == 0)
                try { close(entry.Value.Value); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Legacy binding disposal failed.", errors);
    }
    public void Dispose()
    {
        List<Entry> remove = [];
        lock (gate) { if (disposed) return; disposed = true; foreach (var entry in entries.Values.ToArray()) Detach(entry, remove); }
        Close(remove);
    }
    internal sealed class Entry(TKey key, Func<T> create)
    {
        internal TKey Key { get; } = key;
        internal Lazy<T> Value { get; } = new(create, LazyThreadSafetyMode.ExecutionAndPublication);
        internal int References;
        internal bool Removed;
        internal int Closed;
        internal ITimer? Timer;
        internal LinkedListNode<Entry>? Idle;
    }
}
