// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/slot.ts
//
// Document slots (slice 6a §1.2): one per declared document name on its object, across the object's attempts. A slot holds the documents'
// trackers, the version and definition its values are of, what is stored of persisted ones, and the watches; each attempt's handles are
// thin views of it. This file: what every slot is (DocumentSlot, PersistedSlot), the definition a slot's values are of (SlotDefinition) and
// the singleton's slot (SingletonSlot); the keyed one is KeyedSlot.cs. Areas cv2-documents-core (SlotDefinition) and cv2-documents-slots
// (the rest) ported it. Mappings:
// - Upstream's AnyDefinition, a definition whatever its value and keyedness, is StateDefinition itself (Definition.cs). DocumentSlot and
//   PersistedSlot are IDocumentSlot and IPersistedSlot; a slot is also the engine's IDeclaredSlot (Documents.cs). A singleton's key is
//   always "".
// - A declaration's refusal is upstream's Error, an InvalidOperationException here (the attempt fails broken); the version fence and the
//   reads before activation throw ChordErrors.
// - Threads: a slot belongs to its object's host, which serializes the calls that reach it; its fields are volatile, as an adoption may run
//   on another thread than a later read (the transaction's continuation).
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Documents;

/// <summary>
/// What a transaction drafts and adopts (upstream <c>DocumentSlot</c>): documents of one object by key (a singleton ignores the key). The
/// slots of <c>self.state</c>, and Chord's own (a job's run state documents).
/// </summary>
internal interface IDocumentSlot
{
    Documents Owner { get; }

    /// <summary>The persisted slot's store side; null for documents in memory only.</summary>
    IPersistedSlot? Persisted { get; }

    /// <summary>The tracker of the document at <paramref name="key"/>, null while absent.</summary>
    Tracker<JsonObject>? Current(string key);

    /// <summary>The value a draft of an absent document starts from.</summary>
    JsonObject Initial(ChordContext context);

    /// <summary>Adopts a commit of the document at <paramref name="key"/> (inside the batch), with what the commit stored of it.</summary>
    void Adopt(string key, Tracker<JsonObject> tracker, Prepared<JsonObject> prepared, StoredChange? stored);

    /// <summary>Retires the document at <paramref name="key"/> (inside the batch).</summary>
    void Retire(string key);
}

/// <summary>What a transaction needs of a persisted slot (upstream <c>PersistedSlot</c>, slice 6a §4, §10).</summary>
internal interface IPersistedSlot
{
    Persistence Persistence { get; }

    SlotDefinition Definition { get; }

    /// <summary>Whether the document at <paramref name="key"/> may be stored without being in memory (a persisted keyed document not
    /// loaded).</summary>
    bool Cold(string key);

    /// <summary>What is stored of the document at <paramref name="key"/> as memory knows it; null when absent or cold.</summary>
    Stored? Stored(string key);

    /// <summary>
    /// Stages one commit's writes of the document at <paramref name="key"/> into <paramref name="writes"/> (<paramref name="was"/>: what is
    /// stored of it, a cold one's as the transaction found it); <paramref name="persistence"/> is the transaction's (a deferred change of a
    /// run's state is held, D8).
    /// </summary>
    StoredChange Stage(string key, DocumentChange change, Stored? was, List<IStagedWrite> writes, CommitPersistence persistence);

    /// <summary>
    /// Loads the document at <paramref name="key"/> (its caller holds the line): installed when present and still absent from memory, and,
    /// for a transaction's load (<paramref name="token"/>), while the token is not sealed (a cut transaction released the line, so a
    /// successor may have changed or retired the key meanwhile).
    /// </summary>
    Task LoadAsync(string key, ChordContext context, SealToken? token = null);
}

/// <summary>
/// The definition a slot's values are of (upstream <c>SlotDefinition</c>): the one that hydrated it last, the first declaration until then.
/// It checks a new attempt's declaration and fences the old handles (R15).
/// </summary>
internal sealed class SlotDefinition
{
    private volatile StateDefinition current;
    private volatile bool hydrated;

    public SlotDefinition(StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        current = definition;
    }

    /// <summary>The definition the values are of.</summary>
    public StateDefinition Current => current;

    /// <summary>The definition the values were hydrated with, null before the first hydration.</summary>
    public StateDefinition? Hydrated => hydrated ? current : null;

    /// <summary>
    /// Checks a declaration of this name by a new attempt: a flipped keyed or persist, a version older than the values (old code) or the same
    /// version with another fingerprint (a schema changed without a bump) throw (the attempt fails broken).
    /// </summary>
    public void Check(StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var (name, version, keyed, persist, fingerprint) = (current.Name, current.Version, current.Keyed, current.Persist, current.Fingerprint);
        if (definition.Keyed != keyed)
            throw new InvalidOperationException($"{name}: declared {(definition.Keyed ? "keyed" : "singleton")}, its documents are not");
        if (definition.Persist != persist)
            throw new InvalidOperationException($"{name}: declared {(definition.Persist ? "persisted" : "in memory")}, its documents are not");
        if (definition.Version < version)
            throw new InvalidOperationException($"{name}: version {definition.Version} is older than its documents' ({version})");
        if (definition.Version == version && !string.Equals(definition.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: the schema changed without a version bump (version {version})");
    }

    /// <summary>Records the hydration with <paramref name="definition"/>.</summary>
    public void Hydrate(StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        current = definition;
        hydrated = true;
    }

    /// <summary>
    /// Throws <c>released</c> for a handle of <paramref name="version"/> once the values migrated past it (old code reads no new shape), and
    /// <c>not_ready</c> for a handle of a newer version until its activation migrated them (new code reads no old shape: a retry's factory).
    /// </summary>
    public void AssertVersion(long version)
    {
        var definition = current;
        if (version == definition.Version) return;
        var message = $"{definition.Name}: a handle of version {version}; the documents are at version {definition.Version}";
        if (version < definition.Version) throw new ChordError("released", message, "version");
        throw new ChordError("not_ready", $"{message} until the activation migrates them");
    }
}

/// <summary>
/// A singleton's slot (upstream <c>SingletonSlot</c>): one tracker while written, <c>initial()</c> while absent; the source its handles and
/// members watch. Persisted, it loads once per object (again after a failed load) and stays in memory while the object lives.
/// </summary>
internal sealed class SingletonSlot : AddressSource, IDocumentSlot, IPersistedSlot, IDeclaredSlot
{
    private readonly Persistence? persistence;
    // initial(), taken at hydration; null before the first.
    private volatile JsonObject? initial;
    private volatile Tracker<JsonObject>? tracker;
    // What is stored of it (persisted); null while absent.
    private volatile Stored? stored;
    // In memory: always; persisted: once loaded.
    private volatile bool loaded;

    public SingletonSlot(Documents owner, StateDefinition definition, Persistence? persistence)
        : base(owner.Batch)
    {
        Owner = owner;
        Definition = new SlotDefinition(definition);
        this.persistence = persistence;
        loaded = persistence is null;
    }

    public Documents Owner { get; }

    public SlotDefinition Definition { get; }

    public IPersistedSlot? Persisted => persistence is null ? null : this;

    // Only reached through Persisted, which a persisted slot alone has.
    public Persistence Persistence => persistence!;

    public bool IsPersisted => persistence is not null;

    /// <inheritdoc/>
    public override JsonObject Value
    {
        get
        {
            Owner.AssertLive();
            if (initial is not { } first || !loaded) throw new ChordError("not_ready", $"{Definition.Current.Name}: read before activation");
            return tracker?.Value ?? first;
        }
    }

    /// <summary>
    /// Takes the attempt's declaration <paramref name="definition"/> (the check accepted it): the first takes <c>initial()</c>; a newer
    /// version migrates the value in memory (a persisted one stores a base with its next change) and takes <c>initial()</c> again, and a
    /// value that changed reaches watches as a root replacement; the same version keeps everything.
    /// </summary>
    public void Hydrate(StateDefinition definition, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var from = Definition.Hydrated;
        if (from is not null && from.Version == definition.Version)
        {
            Definition.Hydrate(definition);
            return;
        }
        var before = from is null || !loaded ? null : Value;
        var current = tracker;
        var migrated = current is null || from is null ? current : Tracker.Track(definition.Migrate(current.Value, from.Version, context));
        initial = definition.Initial(context);
        tracker = migrated;
        Definition.Hydrate(definition);
        if (before is not null) Batch.Commit(Replaced, context);
    }

    /// <summary>Loads the persisted document unless it is loaded already (the caller holds the line).</summary>
    public async Task LoadAsync(string key, ChordContext context, SealToken? token = null)
    {
        if (loaded) return;
        var document = await Persistence.ReadAsync("", context).ConfigureAwait(false);
        // Singletons load at activation, on the line: what the code cannot read fails the activation.
        var found = document is null ? null : Persistence.Decode(document, Definition.Current, context);
        if (found is not null)
        {
            tracker = Tracker.Track(found.Value);
            stored = found.Stored;
        }
        loaded = true;
    }

    /// <summary>Loads the persisted document unless it is loaded already (activation, holding the line).</summary>
    public Task LoadDeclaredAsync(ChordContext context) => LoadAsync("", context);

    /// <summary>A singleton has one document: its key is always <c>""</c>.</summary>
    public string Key(string key) => "";

    public bool Cold(string key) => false;

    public Stored? Stored(string key) => stored;

    public StoredChange Stage(string key, DocumentChange change, Stored? was, List<IStagedWrite> writes, CommitPersistence persistence) =>
        Persistence.Stage(key, Definition.Current, was, change, writes);

    public Tracker<JsonObject>? Current(string key) => tracker;

    /// <summary>The value a draft of the absent document starts from: the <c>initial()</c> taken at hydration.</summary>
    public JsonObject Initial(ChordContext context) => initial!;

    public void Adopt(string key, Tracker<JsonObject> tracker, Prepared<JsonObject> prepared, StoredChange? stored)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(prepared);
        // Unchecked: a step admitted before the object ended adopts after its seal (slice 6a §1.3).
        var before = this.tracker?.Value ?? initial!;
        tracker.Adopt(prepared);
        this.tracker = tracker;
        this.stored = stored is null ? null : DocumentsInternal.StoredAfter(stored);
        Written(before, prepared);
    }

    /// <summary>Retires the document: it reads <c>initial()</c> again, and the frame says it was retired.</summary>
    public void Retire(string key)
    {
        tracker = null;
        stored = null;
        Retired(initial!);
    }

    /// <summary>Its object ended or no attempt declares it any more: the document goes and its watches end.</summary>
    public void Drop()
    {
        tracker = null;
        stored = null;
        EndWatches();
    }
}
