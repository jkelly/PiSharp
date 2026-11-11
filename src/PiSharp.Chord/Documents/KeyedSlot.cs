// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/keyed-slot.ts
//
// Keyed slots (slice 6a §1.2, §4, §10, §11): the documents of one keyed name on one object, by key. In memory, every present document
// stays; persisted, a document is loaded on demand (a cold read is a job on the object's line, one load in flight per key, shared) and leaves
// memory when unpinned and idle for the retention (Residency.cs). A key read through at(key) or a watch has its own source, which exists
// while the key is pinned. Mappings:
// - Access, whom a read is for, is a record of the handle's seal token and version. Upstream's Maps are dictionaries; the pinned keys'
//   sources keep their insertion order (an OrderedDictionary), as a new definition retakes them in that order.
// - Promises are Tasks: a refused key (StoredText) still reaches an async caller as a faulted task. keys() is an IAsyncEnumerable whose
//   checks run at its first MoveNextAsync, as a generator's do. awaitWithContext is Task.WaitAsync on the context's token (StoreInternal
//   .AwaitWithContext's form): a cancelled wait fails with the token's cancellation, where upstream rejects with the signal's reason.
// - Threads: upstream runs on one JavaScript thread. Here the slot belongs to its object's host, which serializes the calls that reach it,
//   but a watch's end releases its pin and a shared load leaves the slot from the thread that settles them; the three maps are therefore
//   under a short lock, which no call out of the slot (the residency, the owner, a source, the store) is made under.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Documents;

/// <summary>Whom a read is for (upstream <c>Access</c>): a handle's seal token and the version it was declared with (R15).</summary>
internal sealed record Access(SealToken Token, long Version);

internal abstract record ChangedDocument
{
    internal sealed record Absent : ChangedDocument;
    internal sealed record Unchanged : ChangedDocument;
    internal sealed record Present(Loaded Loaded) : ChangedDocument;
    internal sealed record Failed(ChordError Error) : ChangedDocument;
}

/// <summary>A keyed slot (upstream <c>KeyedSlot</c>): one resident per document in memory, one source per pinned key.</summary>
internal sealed class KeyedSlot : IDocumentSlot, IPersistedSlot, IDeclaredSlot, IResidentSlot
{
    /// <summary>The keys <c>Keys</c> reads per page from the store (upstream <c>KEYS_PAGE</c>).</summary>
    internal const int KeysPage = 100;

    private readonly Persistence? persistence;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Resident> residents = new(StringComparer.Ordinal);
    // The sources of pinned keys: a key is pinned while it has one.
    private readonly OrderedDictionary<string, KeySource> sources = new(StringComparer.Ordinal);
    // Cold loads in flight, by key: shared by every reader of the key.
    private readonly Dictionary<string, SharedLoad> loads = new(StringComparer.Ordinal);
    // Names a refused key (the slot's name is the same at every version): made once, not per key check.
    private readonly string keyWhat;

    public KeyedSlot(Documents owner, StateDefinition definition, Persistence? persistence)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(definition);
        Owner = owner;
        Definition = new SlotDefinition(definition);
        keyWhat = $"{definition.Name}: key";
        this.persistence = persistence;
    }

    public Documents Owner { get; }

    public SlotDefinition Definition { get; }

    public IPersistedSlot? Persisted => persistence is null ? null : this;

    // Only reached through Persisted, which a persisted slot alone has.
    public Persistence Persistence => persistence!;

    public bool IsPersisted => persistence is not null;

    internal bool Holds(string key)
    {
        lock (gate) return residents.ContainsKey(key) || sources.ContainsKey(key);
    }

    internal async Task<ChangedDocument> ReadChangedAsync(string key, Step step, ChordContext context)
    {
        var record = await DocumentEngine.FindDocumentAsync(step, Persistence.At(key)).ConfigureAwait(false);
        if (record is null) return new ChangedDocument.Absent();
        var stored = Stored(key);
        if (stored?.Id == record.Id && stored.Revision == record.Revision) return new ChangedDocument.Unchanged();
        var document = await DocumentEngine.LoadDocumentAsync(step, record).ConfigureAwait(false);
        try { return new ChangedDocument.Present(Persistence.Decode(document, Definition.Current, context)); }
        catch (Exception error) { return new ChangedDocument.Failed(DataInternal.AsChordError(error, Owner.Label)); }
    }

    internal void AdoptChanged(string key, ChangedDocument changed)
    {
        if (changed is ChangedDocument.Unchanged or ChangedDocument.Failed) return;
        Resident? before;
        KeySource? source;
        lock (gate)
        {
            residents.Remove(key, out before);
            sources.TryGetValue(key, out source);
        }
        if (before is not null) Owner.Residency.Forget(before);
        if (changed is ChangedDocument.Absent)
        {
            if (before is not null) source?.Retire();
            if (source is not null) source.Ready = true;
            return;
        }
        var loaded = ((ChangedDocument.Present)changed).Loaded;
        var resident = new Resident(this, key, Tracker.Track(loaded.Value), loaded.Stored, Definition.Current.Version);
        lock (gate) residents[key] = resident;
        if (source is null) Touch(resident);
        else { source.Ready = true; source.Replaced(); }
    }

    /// <summary>
    /// The value at <paramref name="raw"/>, null when absent. A document in memory is read at once (also through a sealed handle); a cold
    /// persisted one is loaded on the line (refused <c>released</c> once the access's token is sealed), and <paramref name="context"/> ends
    /// only this caller's wait. An absent key is not cached (rule 20).
    /// </summary>
    public Task<JsonObject?> GetAsync(string raw, Access access, ChordContext context)
    {
        // `GetKeyAsync` is the one async layer; a refused key still reaches the caller as a faulted task.
        try
        {
            return GetKeyAsync(Key(raw), access, context);
        }
        catch (Exception error)
        {
            return Task.FromException<JsonObject?>(error);
        }
    }

    /// <summary>The present keys in code point order: in memory, the residents; persisted, the store's, a page per line job.</summary>
    public async IAsyncEnumerable<string> Keys(Access access, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(access);
        Owner.AssertLive();
        Definition.AssertVersion(access.Version);
        var stored = persistence;
        if (stored is null)
        {
            List<string> present;
            lock (gate) present = [.. residents.Keys];
            present.Sort(Tables.CompareText);
            foreach (var key in present) yield return key;
            yield break;
        }
        string? after = null;
        while (true)
        {
            var from = after;
            var page = await Owner.OnLineAsync(() => stored.KeysAsync(from, KeysPage, context), access.Token).ConfigureAwait(false);
            foreach (var key in page) yield return key;
            if (page.Count < KeysPage) yield break;
            after = page[^1];
        }
    }

    /// <summary>
    /// A watch of the document at <paramref name="raw"/>, null when absent (after a cold load); it follows the address (a retirement is a
    /// frame with the <c>retired</c> flag) and pins the key until it ends.
    /// </summary>
    public async Task<IWatchHandle<JsonNode?>?> WatchAsync(string raw, Access access, ChordContext context)
    {
        var key = Key(raw);
        if (await GetKeyAsync(key, access, context).ConfigureAwait(false) is null) return null;
        var release = Pin(key, context);
        KeySource? source;
        lock (gate) source = residents.ContainsKey(key) ? sources.GetValueOrDefault(key) : null;
        // Retired between the load and now: absent.
        if (source is null)
        {
            release();
            return null;
        }
        Watch<JsonNode?> watch;
        try
        {
            watch = source.OpenWatch(context);
        }
        catch
        {
            release();
            throw;
        }
        _ = watch.Closed.ContinueWith(_ => release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return watch;
    }

    /// <summary>
    /// Pins <paramref name="raw"/>: its source (made by the first pin) and its document stay in memory until the returned release
    /// (idempotent) runs; a persisted key not in memory is loaded by the next activation load (<see cref="LoadDeclaredAsync"/>).
    /// </summary>
    public Action Pin(string raw, ChordContext context)
    {
        var key = Key(raw);
        KeySource? pinned;
        Resident? resident;
        lock (gate) pinned = sources.GetValueOrDefault(key);
        // A new key's initial() runs outside the lock (plugin code); a source another call made meanwhile wins.
        var made = pinned is null ? Definition.Current.Initial(context) : null;
        lock (gate)
        {
            if (!sources.TryGetValue(key, out pinned))
            {
                var ready = persistence is null || residents.ContainsKey(key);
                pinned = new KeySource(this, key, made!, ready);
                sources[key] = pinned;
            }
            pinned.Pins++;
            residents.TryGetValue(key, out resident);
        }
        if (resident is not null) Owner.Residency.Forget(resident);
        var released = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref released, 1) == 1) return;
            Resident? idle;
            lock (gate)
            {
                if (--pinned.Pins > 0 || !sources.TryGetValue(key, out var current) || !ReferenceEquals(current, pinned)) return;
                sources.Remove(key);
                residents.TryGetValue(key, out idle);
            }
            if (idle is not null) Touch(idle);
        };
    }

    /// <summary>Loads pinned <paramref name="raw"/> on the line unless it was loaded since it was pinned (a consumer's at(key), slice 6a
    /// §12).</summary>
    public async Task EnsureAsync(string raw, ChordContext context)
    {
        var key = Key(raw);
        KeySource? source;
        lock (gate) sources.TryGetValue(key, out source);
        if (source?.Ready != false) return;
        await Owner.OnLineAsync(() => LoadAsync(key, context)).ConfigureAwait(false);
    }

    /// <summary>The source of pinned <paramref name="key"/>, null when it is not pinned.</summary>
    public KeySource? Source(string key)
    {
        var at = Key(key);
        lock (gate) return sources.GetValueOrDefault(at);
    }

    /// <summary>
    /// <paramref name="key"/> as the slot and the store keep it: the <c>str()</c> text rules (slice 6a §8), a lone surrogate replaced by
    /// U+FFFD, NUL refused <c>invalid/args</c>. Plugin keys are a trust boundary; every entry point takes them through here, so memory and
    /// the store agree on one key.
    /// </summary>
    public string Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return StoreInternal.StoredText(key, keyWhat);
    }

    /// <summary>Loads every pinned key not loaded since it was pinned (activation, holding the line).</summary>
    public async Task LoadDeclaredAsync(ChordContext context)
    {
        List<KeyValuePair<string, KeySource>> pinned;
        lock (gate) pinned = [.. sources];
        foreach (var (key, source) in pinned)
            if (!source.Ready) await LoadAsync(key, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the document at <paramref name="key"/> (the caller holds the line) and installs it while it is still absent from memory (a
    /// commit's adopt always wins). A stored version newer than the code's, another fingerprint or a failing migrate fails the owner
    /// (slice 6a §1.4) and rejects with the cause.
    /// </summary>
    public async Task LoadAsync(string key, ChordContext context, SealToken? token = null)
    {
        lock (gate)
        {
            if (residents.ContainsKey(key)) return;
        }
        // A store error rejects as the store's (storage_failed, aborted).
        var document = await Persistence.ReadAsync(key, context).ConfigureAwait(false);
        // The transaction was cut meanwhile: what it read may be stale already.
        if (token?.Sealed == true) return;
        Loaded? loaded;
        try
        {
            loaded = document is null ? null : Persistence.Decode(document, Definition.Current, context);
        }
        catch (Exception error)
        {
            // What the code cannot read (a failing migrate, whatever it throws) fails the owner.
            var failure = DataInternal.AsChordError(error, Owner.Label);
            Owner.Fail(failure);
            throw failure;
        }
        Resident? installed = null;
        lock (gate)
        {
            if (loaded is not null && !residents.ContainsKey(key))
            {
                installed = new Resident(this, key, Tracker.Track(loaded.Value), loaded.Stored, Definition.Current.Version);
                residents[key] = installed;
            }
            if (sources.TryGetValue(key, out var source)) source.Ready = true;
        }
        if (installed is not null) Touch(installed);
    }

    public bool Cold(string key)
    {
        if (persistence is null) return false;
        lock (gate) return !residents.ContainsKey(key);
    }

    public Stored? Stored(string key)
    {
        lock (gate) return residents.GetValueOrDefault(key)?.Stored;
    }

    public StoredChange Stage(string key, DocumentChange change, Stored? was, List<IStagedWrite> writes, CommitPersistence persistence) =>
        Persistence.Stage(key, Definition.Current, was, change, writes);

    /// <summary>
    /// Takes the attempt's declaration: a newer version migrates each document in memory on its next access (persisted ones store a base
    /// with their next change); the pinned keys are watched, so they migrate now, all or none, and take <c>initial()</c> again.
    /// </summary>
    public void Hydrate(StateDefinition definition, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var from = Definition.Hydrated;
        Definition.Hydrate(definition);
        if (from is null || from.Version == definition.Version) return;
        List<Resident> pinned;
        List<KeySource> retaken;
        lock (gate)
        {
            pinned = [.. sources.Keys.Select(key => residents.GetValueOrDefault(key)).OfType<Resident>()];
            retaken = [.. sources.Values];
        }
        var migrated = pinned.Select(resident => (Resident: resident, Tracker: Migrated(resident, context))).ToList();
        foreach (var (resident, tracker) in migrated)
        {
            resident.Tracker = tracker;
            resident.Version = definition.Version;
        }
        Owner.Batch.Commit(() =>
        {
            foreach (var source in retaken) source.Retake(definition.Initial(context));
        }, context);
    }

    public Tracker<JsonObject>? Current(string key) => ResidentAt(key)?.Tracker;

    /// <summary>The value a draft of an absent document starts from: a fresh <c>initial()</c>.</summary>
    public JsonObject Initial(ChordContext context) => Definition.Current.Initial(context);

    public void Adopt(string key, Tracker<JsonObject> tracker, Prepared<JsonObject> prepared, StoredChange? change)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(prepared);
        var stored = change is null ? null : DocumentsInternal.StoredAfter(change);
        KeySource? source;
        lock (gate) sources.TryGetValue(key, out source);
        var before = source?.Peek();
        tracker.Adopt(prepared);
        var version = Definition.Current.Version;
        Resident resident;
        lock (gate)
        {
            if (!residents.TryGetValue(key, out var present))
            {
                present = new Resident(this, key, tracker, stored, version);
                residents[key] = present;
            }
            else
            {
                present.Tracker = tracker;
                present.Stored = stored;
                present.Version = version;
            }
            resident = present;
        }
        if (source is null) Touch(resident);
        else source.Written(before!, prepared);
    }

    /// <summary>Retires the document at <paramref name="key"/> (inside the batch): out of memory, its source reads <c>initial()</c> with the
    /// flag.</summary>
    public void Retire(string key)
    {
        Resident? resident;
        KeySource? source;
        lock (gate)
        {
            residents.Remove(key, out resident);
            sources.TryGetValue(key, out source);
        }
        if (resident is not null) Owner.Residency.Forget(resident);
        source?.Retire();
    }

    /// <summary>The sweep found <paramref name="resident"/> idle past the retention: it leaves memory.</summary>
    public void Evict(Resident resident)
    {
        ArgumentNullException.ThrowIfNull(resident);
        lock (gate)
        {
            if (residents.TryGetValue(resident.Key, out var present) && ReferenceEquals(present, resident)) residents.Remove(resident.Key);
        }
    }

    /// <summary>Its object ended or no attempt declares it any more: the documents go and the keys' watches end.</summary>
    public void Drop()
    {
        List<Resident> dropped;
        List<KeySource> ended;
        lock (gate)
        {
            dropped = [.. residents.Values];
            residents.Clear();
            ended = [.. sources.Values];
            sources.Clear();
        }
        foreach (var resident in dropped) Owner.Residency.Forget(resident);
        foreach (var source in ended) source.End();
    }

    /// <summary><see cref="GetAsync"/> of a key that went through <see cref="Key"/> already.</summary>
    private async Task<JsonObject?> GetKeyAsync(string key, Access access, ChordContext context)
    {
        Owner.AssertLive();
        Definition.AssertVersion(access.Version);
        if (ResidentAt(key) is { } resident)
        {
            Touch(resident);
            return resident.Tracker.Value;
        }
        if (persistence is null) return null;
        await LoadSharedAsync(key, access.Token, context).ConfigureAwait(false);
        lock (gate) return residents.GetValueOrDefault(key)?.Tracker.Value;
    }

    /// <summary>
    /// A cold persisted key's load, one in flight per key, on the line under the owner's context (rule 11). The load is every waiter's: when
    /// it takes the line, a waiter whose handle was sealed meanwhile (its attempt failed live, the object still open) is refused
    /// <c>released</c>, and with no other waiter the load does not run (rule 4).
    /// </summary>
    private async Task LoadSharedAsync(string key, SealToken token, ChordContext context)
    {
        // Also when a load is in flight: a sealed handle's cold read is refused (rule 4).
        Owner.AssertAdmitted(token);
        SharedLoad? load;
        var started = false;
        lock (gate)
        {
            if (!loads.TryGetValue(key, out load))
            {
                load = new SharedLoad();
                loads[key] = load;
                started = true;
            }
            load.Tokens.Add(token);
            // The job runs once the line is taken, after the load is in the map with its first waiter.
            if (started) load.Done = Owner.OnLineAsync(() => SharedJobAsync(key, load));
        }
        if (started) Settle(key, load);
        await (context.AbortSignal is { } signal ? AwaitWithContext(load.Done!, signal) : load.Done!).ConfigureAwait(false);
        if (load.Refused?.Contains(token) == true) throw Owner.Released();
    }

    /// <summary>The shared load's job on the line: the waiters sealed by now are refused, and with no other waiter nothing loads.</summary>
    private async Task SharedJobAsync(string key, SharedLoad load)
    {
        HashSet<SealToken> refused;
        int waiters;
        lock (gate)
        {
            refused = new HashSet<SealToken>(load.Tokens.Where(token => token.Sealed), ReferenceEqualityComparer.Instance);
            waiters = load.Tokens.Count;
        }
        load.Refused = refused;
        if (refused.Count == waiters) throw Owner.Released();
        await LoadAsync(key, Owner.Context).ConfigureAwait(false);
    }

    /// <summary>The shared load of <paramref name="key"/> leaves the loads when it settles.</summary>
    private void Settle(string key, SharedLoad load) =>
        _ = load.Done!.ContinueWith(_ =>
        {
            lock (gate)
            {
                if (loads.TryGetValue(key, out var current) && ReferenceEquals(current, load)) loads.Remove(key);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary><paramref name="task"/>, or a cancellation once <paramref name="signal"/> aborts: an aborted signal fails at once, even for a
    /// settled task (upstream <c>awaitWithContext</c>).</summary>
    private static Task AwaitWithContext(Task task, CancellationToken signal) =>
        signal.IsCancellationRequested ? Task.FromCanceled(signal) : task.WaitAsync(signal);

    /// <summary>The resident at <paramref name="key"/>, migrated to the slot's version first when it is older (migration on access).</summary>
    private Resident? ResidentAt(string key)
    {
        Resident? resident;
        lock (gate) residents.TryGetValue(key, out resident);
        var version = Definition.Current.Version;
        if (resident is null || resident.Version == version) return resident;
        resident.Tracker = Migrated(resident, Owner.Context);
        resident.Version = version;
        return resident;
    }

    /// <summary>
    /// <paramref name="resident"/>'s value migrated from its version to the slot's (checked, R8). A throwing migrate fails the live owner
    /// <c>broken</c> (slice 6a §1.4) and throws the failure (an activation fails with it).
    /// </summary>
    private Tracker<JsonObject> Migrated(Resident resident, ChordContext context)
    {
        try
        {
            return Tracker.Track(Definition.Current.Migrate(resident.Tracker.Value, resident.Version, context));
        }
        catch (Exception error)
        {
            var failure = DataInternal.AsChordError(error, Owner.Label);
            Owner.Fail(failure);
            throw failure;
        }
    }

    /// <summary>A use of <paramref name="resident"/> while unpinned: persisted, it is idle from now (Durable's granule rule).</summary>
    private void Touch(Resident resident)
    {
        if (persistence is null) return;
        bool pinned;
        lock (gate) pinned = sources.ContainsKey(resident.Key);
        if (!pinned) Owner.Residency.Touch(resident);
    }

    /// <summary>
    /// A cold load in flight, shared by its waiters (upstream <c>SharedLoad</c>): their handles' tokens, and those already sealed when the
    /// load took the line (null until then), whose waiters it refuses (rule 4).
    /// </summary>
    private sealed class SharedLoad
    {
        public Task? Done { get; set; }

        /// <summary>Its waiters' tokens (under the slot's lock).</summary>
        public HashSet<SealToken> Tokens { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The waiters sealed when the load took the line; null until then.</summary>
        public HashSet<SealToken>? Refused { get => refused; set => refused = value; }

        private volatile HashSet<SealToken>? refused;
    }
}

/// <summary>
/// One pinned key of a keyed slot as a source (upstream <c>KeySource</c>; handle.at(key), a keyed member's at(key), watch(key)): the
/// document's value, <c>initial()</c> while it is absent; retirements arrive with the <c>retired</c> flag.
/// </summary>
internal sealed class KeySource : AddressSource
{
    private readonly KeyedSlot slot;
    private volatile JsonObject initial;
    private volatile bool ready;

    public KeySource(KeyedSlot slot, string key, JsonObject initial, bool ready)
        : base(slot.Owner.Batch)
    {
        this.slot = slot;
        Key = key;
        this.initial = initial;
        this.ready = ready;
    }

    public string Key { get; }

    /// <summary>Its pins: at 0 the slot forgets it (changed under the slot's lock).</summary>
    public int Pins { get; set; }

    /// <summary>Whether the key was loaded since it was pinned (always, in memory): its value is known.</summary>
    public bool Ready
    {
        get => ready;
        set => ready = value;
    }

    /// <inheritdoc/>
    public override JsonObject Value
    {
        get
        {
            slot.Owner.AssertLive();
            if (!ready) throw new ChordError("not_ready", $"{slot.Definition.Current.Name}({Key}): read before it was loaded");
            return Peek();
        }
    }

    /// <summary>The value without the checks (inside its slot's commits).</summary>
    public JsonObject Peek() => slot.Current(Key)?.Value ?? initial;

    /// <summary>The document was retired: <c>initial()</c> from now, with the flag.</summary>
    public void Retire() => Retired(initial);

    /// <summary>A new definition took over (inside a batch): <c>initial()</c> again, the value as a root replacement.</summary>
    public void Retake(JsonObject initial)
    {
        this.initial = initial;
        if (ready) Replaced();
    }

    /// <summary>Its slot was dropped: the watches end.</summary>
    public void End() => EndWatches();
}
