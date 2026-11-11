// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/definition.ts
//
// Document definitions: defineState(name, ...) (StateDefinition.Define), its fingerprint, the one check of a definition's initial() (R8),
// the checked migrate of an older value and, for a persisted document, its checkpoint rule (slice 6a §10). Mappings:
// - A document root is a JsonObject: upstream's JsonObject type is System.Text.Json.Nodes.JsonObject here. initial() and migrate() make a
//   fresh one each call; once handed to Chord a value is immutable by convention, as upstream's are.
// - StateDefinition<T, K, P> is one StateDefinition. T is a static type, which C# has no counterpart for (a document is a JsonObject), and
//   K and P are type-only markers (upstream's "~keyed" and "~persist"), here the Keyed and Persist flags; the handle a declaration gets
//   (HandleOf) follows Keyed when it is declared.
// - What the types rule out becomes a refusal when Define runs (PiSharp-only, ArgumentException, as cv2-evolve's editors): persist: true
//   without checkpointWhen, and checkpointWhen without persist: true. A name outside the restricted names throws as checkName does; a
//   null name is the untyped door upstream's `42 as never` opens.
// - Upstream's Errors at use (an initial() or migrate() result outside the schema, a missing migrate) are InvalidOperationExceptions with
//   its messages. Versions are longs; ops are Delta's Op records (Delta/Ops.cs).
// - CommitPersistence, upstream's union { kind: "immediate" } | { kind: "deferred", withinMs? }, is a record whose Kind says which.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;

namespace PiSharp.Chord.Documents;

/// <summary>
/// When a commit reaches the store (upstream <c>CommitPersistence</c>, Durable's): <see cref="Immediate"/>, the default, before the commit
/// completes; <see cref="Deferred"/> (a persisted run's state change, D8): adopted and published at once, stored with the next immediate
/// change, the run's outcome, the object's close, or about <see cref="WithinMs"/> (default 1,000 ms) later.
/// </summary>
public sealed record CommitPersistence
{
    private CommitPersistence(string kind, double? withinMs)
    {
        Kind = kind;
        WithinMs = withinMs;
    }

    /// <summary><c>immediate</c> or <c>deferred</c>.</summary>
    public string Kind { get; }

    /// <summary>A deferred change's delay in milliseconds; null for the default (and for an immediate one).</summary>
    public double? WithinMs { get; }

    /// <summary><c>{ kind: "immediate" }</c>.</summary>
    public static CommitPersistence Immediate { get; } = new("immediate", null);

    /// <summary><c>{ kind: "deferred", withinMs }</c>; a null <paramref name="withinMs"/> is upstream's absent one.</summary>
    public static CommitPersistence Deferred(double? withinMs = null) => new("deferred", withinMs);
}

/// <summary>What a persisted document's checkpoint rule learns of its stored revisions (upstream <c>CheckpointInfo</c>).</summary>
/// <param name="DeltasSinceBase">The deltas stored after its newest base, without the change being decided.</param>
public sealed record CheckpointInfo(int DeltasSinceBase);

/// <summary>Whether a commit's change of a persisted document is stored as a whole new base (true) or as a delta (upstream
/// <c>CheckpointWhen</c>).</summary>
public delegate bool CheckpointWhen(JsonObject value, ImmutableArray<Op> ops, CheckpointInfo info);

/// <summary>What <see cref="StateDefinition.Define"/> takes (upstream's <c>StateOptions</c> and <c>PersistOptions</c>).</summary>
public sealed class StateOptions
{
    /// <summary>The document's schema; its root describes a JSON object.</summary>
    public required JsonNode Schema { get; init; }

    /// <summary>The definition's version: a bump with a <see cref="Migrate"/> changes the documents' shape.</summary>
    public required long Version { get; init; }

    /// <summary>A fresh value of an absent document.</summary>
    public required Func<JsonObject> Initial { get; init; }

    /// <summary>Maps a value of an older version (its second argument) to this version's shape; pure. Required to read older values.</summary>
    public Func<JsonObject, long, JsonObject>? Migrate { get; init; }

    /// <summary>Many documents per object, each by a string key, instead of a singleton.</summary>
    public bool Keyed { get; init; }

    /// <summary>Keys are addresses of this host kind. Implies <see cref="Keyed"/>.</summary>
    public HostKind? KeyHost { get; init; }

    /// <summary>Stored in the host's store; only in <c>local</c> types with empty params (P3). Requires <see cref="CheckpointWhen"/>.</summary>
    public bool Persist { get; init; }

    /// <summary>A persisted document's checkpoint rule, called once per commit per changed document; creations and version changes always
    /// store a base. Only with <see cref="Persist"/>.</summary>
    public CheckpointWhen? CheckpointWhen { get; init; }
}

/// <summary>A declared document (upstream <c>StateDefinition</c>): a singleton per object, or with <see cref="Keyed"/> many per object,
/// each by a string key; with <see cref="Persist"/> stored in the host's store.</summary>
public sealed class StateDefinition
{
    private static readonly CheckpointWhen Never = static (_, _, _) => false;

    private readonly Func<JsonObject> initial;
    private readonly Func<JsonObject, long, JsonObject>? migrate;
    private readonly CheckpointWhen checkpointWhen;
    private volatile bool initialChecked;

    private StateDefinition(string name, JsonNode schema, long version, Func<JsonObject> initial, Func<JsonObject, long, JsonObject>? migrate,
        bool keyed, bool persist, CheckpointWhen? checkpointWhen, HostKind? keyHost)
    {
        Name = name;
        Schema = schema;
        Version = version;
        this.initial = initial;
        this.migrate = migrate;
        Keyed = keyed;
        KeyHost = keyHost;
        Persist = persist;
        this.checkpointWhen = checkpointWhen ?? Never;
        Fingerprint = ContractsInternal.FingerprintOf(ContractsInternal.SnapshotSchema(schema, name).Normal);
    }

    /// <summary>The document's name: a restricted name (lowercase <c>[a-z0-9@/._~-]</c>).</summary>
    public string Name { get; }

    /// <summary>The schema as declared.</summary>
    public JsonNode Schema { get; }

    /// <summary>The definition's version.</summary>
    public long Version { get; }

    /// <summary>Whether its documents are many per object, by key.</summary>
    public bool Keyed { get; }

    /// <summary>The address kind used for document keys, or null for ordinary string keys.</summary>
    public HostKind? KeyHost { get; }

    /// <summary>Whether its documents are stored in the host's store.</summary>
    public bool Persist { get; }

    /// <summary>
    /// <c>hash64</c> of the schema's normal form (descriptions and titles ignored), computed once: the same version with another
    /// fingerprint is a schema changed without a version bump.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Declares a document (upstream <c>defineState</c>): a singleton per object, or with <see cref="StateOptions.Keyed"/> many per
    /// object, each by a string key; with <see cref="StateOptions.Persist"/> stored in the host's store. <paramref name="name"/> is a
    /// restricted name (lowercase <c>[a-z0-9@/._~-]</c>).
    /// </summary>
    public static StateDefinition Define(string name, StateOptions options)
    {
        ContractsInternal.CheckName(name, "A state name");
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Initial, nameof(options.Initial));
        // Upstream's types: `persist: true` takes a required checkpointWhen, `persist: false` none.
        if (options.Persist && options.CheckpointWhen is null) throw new ArgumentException($"{name}: persist: true requires checkpointWhen");
        if (!options.Persist && options.CheckpointWhen is not null) throw new ArgumentException($"{name}: checkpointWhen requires persist: true");
        if (options.KeyHost is not null) ContractsInternal.KindParams(options.KeyHost);
        return new StateDefinition(name, options.Schema, options.Version, options.Initial, options.Migrate, options.Keyed || options.KeyHost is not null, options.Persist,
            options.CheckpointWhen, options.KeyHost);
    }

    /// <summary>Whether a persisted document's change is stored as a new base (its checkpoint rule); never asked in memory.</summary>
    internal bool CheckpointWhen(JsonObject value, ImmutableArray<Op> ops, CheckpointInfo info) => checkpointWhen(value, ops, info);

    /// <summary>A fresh <c>initial()</c> value; the first is checked against the schema (counted), later ones are not (R8).</summary>
    internal JsonObject Initial(ChordContext context)
    {
        var value = initial();
        if (initialChecked) return value;
        Check(value, "initial()", context);
        initialChecked = true;
        return value;
    }

    /// <summary>
    /// <paramref name="old"/>, a value of version <paramref name="from"/>, migrated to this version and checked against the schema once, as
    /// it is produced (R8, counted). Throws without a migrate (the access is refused, the data kept) and when the result does not match.
    /// </summary>
    internal JsonObject Migrate(JsonObject old, long from, ChordContext context)
    {
        if (migrate is null) throw new InvalidOperationException($"{Name}: no migrate from version {from} to {Version}");
        var value = migrate(old, from);
        Check(value, $"migrate() from version {from}", context);
        return value;
    }

    private void Check(JsonObject? value, string what, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Count("chord.data.validations");
        if (!DataInternal.ValidatorFor(this, Schema, context).Check(value)) throw new InvalidOperationException($"{Name}: {what} does not match the schema");
    }
}
