// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/persistence.ts
//
// Persisted documents' store side (slice 6a §8, §10): a slot's address in the documents engine, what is stored of each loaded document
// (Durable's LoadedDocument fields), its loads with the version and fingerprint checks and the checked migrate, and the writes one commit
// stages for a changed or retired document. Areas cv2-documents-core (Stored, which a keyed resident keeps) and cv2-documents-slots (the
// rest) ported it. Mappings:
// - Numbers are the documents engine's (Store/Engine.cs): ids, revisions and versions are longs, the delta count an int. Upstream's
//   undefined is null.
// - StoredChange, upstream's union of { kind: "retire" }, { kind: "create", version, created }, { kind: "change", stored } and { kind:
//   "held", stored, withinMs }, is a record per kind; a creation's `created: { id }`, which the step fills in when it applies, is a
//   CreatedId box. Upstream's free functions checkReadable and storedAfter are DocumentsInternal's.
// - JSON.stringify of a value or of ops is ChordJson.Stringify (JavaScript's text); a value JSON cannot encode (upstream's TypeError) throws
//   its ArgumentException there, at staging, never in the step (rule 8). A delta's ops are stored as their JSON tuples
//   (DocumentContent.Delta); a base holds the prepared value by reference, which the write's JSON copies.
// - Upstream's Errors (checkReadable's) are InvalidOperationExceptions with its messages.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Documents;

/// <summary>What is stored of a loaded document (upstream <c>Stored</c>): its record's id and revision, its base's version, the deltas
/// after it.</summary>
/// <param name="Id">The document record's id.</param>
/// <param name="Revision">The record's revision.</param>
/// <param name="Version">The version of the stored base: below the definition's, the next change stores a base (rule 1).</param>
/// <param name="DeltasSinceBase">The deltas stored after the base.</param>
internal sealed record Stored(long Id, long Revision, long Version, int DeltasSinceBase);

/// <summary>A live document found without loading it (upstream <c>FoundDocument</c>): what is stored of it and the fingerprint it was
/// stored with.</summary>
internal sealed record FoundDocument(Stored Stored, string Fingerprint);

/// <summary>A loaded document (upstream <c>Loaded</c>): its value at the definition's version, and what is stored of it.</summary>
internal sealed record Loaded(JsonObject Value, Stored Stored);

/// <summary>One transaction's change of one document (upstream <c>DocumentChange</c>): its prepared draft (if changed), and whether it
/// retires it.</summary>
internal sealed record DocumentChange(Prepared<JsonObject>? Prepared, bool Retires);

/// <summary>A run state's held ops (D8), stored before the next change's own; <paramref name="Base"/> when a held change asked for a base
/// (upstream <c>Held</c>).</summary>
internal sealed record Held(ImmutableArray<Op> Ops, bool Base);

/// <summary>The id the step assigns a created document, once it applied (upstream's <c>created: { id }</c>).</summary>
internal sealed class CreatedId
{
    public long Id { get; set; }
}

/// <summary>
/// What one commit stores of a document, in the store writes' words (upstream <c>StoredChange</c>): a retirement (nothing stored after it),
/// a creation whose id the step assigns, a change of the record <c>Stored</c> was prepared on, or nothing yet (a deferred change of a run's
/// state, D8, kept in memory over <c>Stored</c>).
/// </summary>
internal abstract record StoredChange
{
    private StoredChange()
    {
    }

    /// <summary><c>retire</c>, <c>create</c>, <c>change</c> or <c>held</c>.</summary>
    public abstract string Kind { get; }

    /// <summary><c>{ kind: "retire" }</c>.</summary>
    public sealed record Retire : StoredChange
    {
        public override string Kind => "retire";
    }

    /// <summary><c>{ kind: "create", version, created }</c>: <see cref="Created"/> holds the id once the step applied.</summary>
    public sealed record Create(long Version, CreatedId Created) : StoredChange
    {
        public override string Kind => "create";
    }

    /// <summary><c>{ kind: "change", stored }</c>: what is stored after the change.</summary>
    public sealed record Change(Stored Stored) : StoredChange
    {
        public override string Kind => "change";
    }

    /// <summary><c>{ kind: "held", stored, withinMs }</c>: a deferred change kept in memory over <see cref="Stored"/>.</summary>
    public sealed record HeldChange(Stored Stored, double WithinMs) : StoredChange
    {
        public override string Kind => "held";
    }
}

/// <summary>
/// The documents of one persisted name on one object (upstream <c>Persistence</c>): <c>(&lt;object type&gt;, namespace, name, key)</c>, the
/// namespace <c>chord</c> for <c>self.state</c>, <c>chord.jobs</c> and <c>chord.job-states</c> for persisted job runs (slice 6b D5).
/// </summary>
internal sealed class Persistence
{
    private readonly DocumentAddress address;

    /// <summary><paramref name="owner"/> is the object's type (R2); <paramref name="kind"/> the definition's or the job's name.</summary>
    public Persistence(ChordStore store, string owner, string @namespace, string kind)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        address = new DocumentAddress(owner, @namespace, kind);
    }

    public ChordStore Store { get; }

    /// <summary>
    /// The stored document at <paramref name="key"/>, null when absent: found and loaded with plain reads (a store error rejects as the
    /// store's). Counted (<c>chord.store.loads</c>).
    /// </summary>
    public async Task<StoredDocument?> ReadAsync(string key, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var at = At(key);
        var document = await Store.ReadAsync(async executor =>
        {
            var record = await DocumentEngine.FindDocumentAsync(executor, at).ConfigureAwait(false);
            return record is null ? null : await DocumentEngine.LoadDocumentAsync(executor, record).ConfigureAwait(false);
        }, context).ConfigureAwait(false);
        if (document is not null) context.Count("chord.store.loads");
        return document;
    }

    /// <summary>
    /// <paramref name="document"/> as <paramref name="definition"/> reads it: a stored version newer than the code's, or the same version
    /// with another fingerprint (D9), throws; an older one is migrated and the result checked once (R8). What throws here is the code's (a
    /// failing migrate too, whatever it throws), never the store's.
    /// </summary>
    public Loaded Decode(StoredDocument document, StateDefinition definition, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definition);
        var record = document.Record;
        DocumentsInternal.CheckReadable(record.Version, record.Fingerprint, definition);
        // The engine stores what commits gave it: JSON objects of the record's version.
        var stored = (JsonObject)document.Value!;
        var value = record.Version == definition.Version ? stored : definition.Migrate(stored, record.Version, context);
        return new Loaded(value, new Stored(record.Id, record.Revision, record.Version, document.DeltasSinceBase));
    }

    /// <summary>
    /// What is stored of the live document at <paramref name="key"/> without loading it (a retirement's find) and the schema fingerprint it
    /// was stored with (<see cref="DocumentsInternal.CheckReadable"/> takes both), null when absent.
    /// </summary>
    public async Task<FoundDocument?> FindAsync(string key, ChordContext context)
    {
        var at = At(key);
        var record = await Store.ReadAsync(executor => DocumentEngine.FindDocumentAsync(executor, at), context).ConfigureAwait(false);
        if (record is null) return null;
        // The change counter is all a retirement needs; DeltasSinceBase is not read for one.
        return new FoundDocument(new Stored(record.Id, record.Revision, record.Version, 0), record.Fingerprint);
    }

    /// <summary>Up to <paramref name="limit"/> keys of live documents after <paramref name="after"/> (from the first when null), in code
    /// point order.</summary>
    public Task<IReadOnlyList<string>> KeysAsync(string? after, long limit, ChordContext context) =>
        Store.ReadAsync(executor => StoreInternal.ListDocumentKeysAsync(executor, address, after, limit), context);

    /// <summary>
    /// Stages one commit's writes of the document at <paramref name="key"/> into <paramref name="writes"/> (rule 8: serialized here, before
    /// the queue): its retirement when <paramref name="stored"/> is, then its creation, or the change of <paramref name="stored"/> (a base
    /// when created, when the stored version is older, or when checkpointWhen asks; a delta otherwise), <paramref name="held"/> ops stored
    /// before the change's own (D8: a held base makes it a base). Returns what the commit stores.
    /// </summary>
    public StoredChange Stage(string key, StateDefinition definition, Stored? stored, DocumentChange change, List<IStagedWrite> writes, Held? held = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(writes);
        var (prepared, retires) = (change.Prepared, change.Retires);
        if (retires && stored is not null) writes.Add(StoreInternal.RetireDocument(stored.Id, stored.Revision));
        if (prepared is null) return new StoredChange.Retire();
        var (version, fingerprint) = (definition.Version, definition.Fingerprint);
        if (retires || stored is null)
        {
            var created = new CreatedId();
            var write = StoreInternal.CreateDocument(At(key), new DocumentBase(version, fingerprint, prepared.Value), ChordJson.Stringify(prepared.Value),
                id => created.Id = id);
            writes.Add(write);
            return new StoredChange.Create(version, created);
        }
        var info = new CheckpointInfo(stored.DeltasSinceBase);
        var isBase = held?.Base == true || stored.Version != version || definition.CheckpointWhen(prepared.Value, prepared.Ops, info);
        var ops = held is null ? prepared.Ops : held.Ops.AddRange(prepared.Ops);
        string text;
        DocumentContent content;
        if (isBase)
        {
            text = ChordJson.Stringify(prepared.Value);
            content = new DocumentContent.Base(version, fingerprint, prepared.Value);
        }
        else
        {
            var tuples = new JsonArray([.. ops.Select(op => (JsonNode)op.ToJson())]);
            text = ChordJson.Stringify(tuples);
            content = new DocumentContent.Delta(version, tuples);
        }
        writes.Add(StoreInternal.ChangeDocument(stored.Id, stored.Revision, content, text));
        var deltasSinceBase = isBase ? 0 : stored.DeltasSinceBase + 1;
        return new StoredChange.Change(new Stored(stored.Id, stored.Revision + 1, version, deltasSinceBase));
    }

    /// <summary>The address of <paramref name="key"/>: a singleton's (<c>""</c>) has no key.</summary>
    public DocumentAddress At(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Length == 0 ? address : address with { Key = key };
    }
}

internal static partial class DocumentsInternal
{
    /// <summary>
    /// Throws when <paramref name="definition"/> cannot read a document stored at <paramref name="version"/> with
    /// <paramref name="fingerprint"/>: a newer version than the code's, or the same version with another fingerprint (D9). A load and a
    /// retirement check alike (R15; upstream <c>checkReadable</c>).
    /// </summary>
    internal static void CheckReadable(long version, string fingerprint, StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = definition.Name;
        if (version > definition.Version)
            throw new InvalidOperationException($"{name}: stored at version {version}, newer than the code's {definition.Version}");
        if (version == definition.Version && !string.Equals(fingerprint, definition.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: stored with another schema at version {definition.Version} (a schema changed without a bump)");
    }

    /// <summary>What is stored after a commit's <paramref name="change"/> was adopted (upstream <c>storedAfter</c>).</summary>
    internal static Stored? StoredAfter(StoredChange change) => change switch
    {
        StoredChange.Retire => null,
        StoredChange.Create create => new Stored(create.Created.Id, 1, create.Version, 0),
        StoredChange.Change changed => changed.Stored,
        StoredChange.HeldChange held => held.Stored,
        _ => throw new InvalidOperationException("An unknown stored change."),
    };
}
