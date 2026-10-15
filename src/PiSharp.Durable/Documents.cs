// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/documents.ts, plus the document definition and token types of
// packages/durable/src/types.ts (spec §3.1, §3.6). Upstream's defineDoc/defineDocFamily overloads pick the token type from the definition's
// scope and history literals; here one factory per scope returns that token type, so the Session's typed access overloads (P3) are
// selected by token type exactly as upstream's are. Document values are JsonObjectValue; upstream's static value type T is not carried.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>Converts a stored value of an older version to a complete current-version value; pure.</summary>
public delegate JsonObjectValue DocumentMigration(JsonObjectValue value, int fromVersion);

/// <summary>Returns true to store an ordinary change as a complete base instead of a delta (spec §3.5).</summary>
public delegate bool DocumentCheckpointPredicate(JsonObjectValue value, IReadOnlyList<Op> ops, CheckpointInfo info);

/// <summary>The erased definition of a singleton document or document family (upstream <c>AnyDocDefinition</c>).</summary>
public sealed class DocDefinition
{
    private readonly Func<JsonValue?, JsonObjectValue> initial;

    internal DocDefinition(string kind, int version, DocumentScopeKind scope, DocumentHistory? history, DocumentFork? fork, bool family,
        Func<JsonValue?, JsonObjectValue> initial, DocumentMigration? migrate, DocumentCheckpointPredicate? checkpointWhen)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(initial);
        if (version < 1) throw new ArgumentException($"Document {kind} version must be a positive integer");
        if (history == DocumentHistory.Latest && fork == DocumentFork.AsOf)
            throw new ArgumentException($"Document {kind} cannot fork \"asOf\" without history \"rewindable\"");
        if (!Enum.IsDefined(scope) || (history is { } h && !Enum.IsDefined(h)) || (fork is { } f && !Enum.IsDefined(f)))
            throw new ArgumentException($"Document {kind} has invalid semantics");
        Kind = kind; Version = version; Scope = scope; History = history; Fork = fork; Family = family;
        this.initial = initial; Migrate = migrate; CheckpointWhen = checkpointWhen;
    }

    /// <summary>Stable persisted kind; part of the public protocol.</summary>
    public string Kind { get; }
    /// <summary>Positive integer version of the stored value shape.</summary>
    public int Version { get; }
    /// <summary>The scope.</summary>
    public DocumentScopeKind Scope { get; }
    /// <summary>History of a conversation document; null for other scopes.</summary>
    public DocumentHistory? History { get; }
    /// <summary>Fork behavior of a conversation document; null for other scopes.</summary>
    public DocumentFork? Fork { get; }
    /// <summary>Whether this is a keyed family.</summary>
    public bool Family { get; }
    /// <summary>Converts an older stored version; absent when older versions are rejected.</summary>
    public DocumentMigration? Migrate { get; }
    /// <summary>Selects complete bases for ordinary changes; absent means never.</summary>
    public DocumentCheckpointPredicate? CheckpointWhen { get; }

    /// <summary>The initial value: <c>initial()</c> for a singleton, <c>initial(seed)</c> for a family member.</summary>
    public JsonObjectValue Initial(JsonValue? seed = null) =>
        initial(seed) ?? throw new InvalidOperationException($"Document {Kind} initial() must return a JSON object");
}

/// <summary>A typed document token passed explicitly to typed access (upstream <c>DocToken</c> or <c>DocFamilyToken</c>).</summary>
public abstract class DocumentToken
{
    private protected DocumentToken(DocDefinition definition) { Definition = definition; }
    /// <summary>The definition.</summary>
    public DocDefinition Definition { get; }
    /// <inheritdoc/>
    public override string ToString() => Definition.Kind;
}

/// <summary>A singleton document token (upstream <c>DocToken</c>).</summary>
public abstract class DocToken : DocumentToken { private protected DocToken(DocDefinition definition) : base(definition) { } }

/// <summary>A keyed document family token (upstream <c>DocFamilyToken</c>); <c>initial(seed)</c> runs only when a member is absent.</summary>
public abstract class DocFamilyToken : DocumentToken { private protected DocFamilyToken(DocDefinition definition) : base(definition) { } }

/// <summary>A Session-scoped singleton.</summary>
public sealed class SessionDocToken : DocToken { internal SessionDocToken(DocDefinition definition) : base(definition) { } }

/// <summary>A conversation singleton, latest or rewindable.</summary>
public class ConversationDocToken : DocToken { private protected ConversationDocToken(DocDefinition definition) : base(definition) { }
    internal static ConversationDocToken Latest(DocDefinition definition) => new(definition); }

/// <summary>A rewindable conversation singleton, which also supports as-of reads.</summary>
public sealed class RewindableConversationDocToken : ConversationDocToken { internal RewindableConversationDocToken(DocDefinition definition) : base(definition) { } }

/// <summary>A task-scoped singleton.</summary>
public sealed class TaskDocToken : DocToken { internal TaskDocToken(DocDefinition definition) : base(definition) { } }

/// <summary>A Session-scoped family.</summary>
public sealed class SessionDocFamilyToken : DocFamilyToken { internal SessionDocFamilyToken(DocDefinition definition) : base(definition) { } }

/// <summary>A conversation family, latest or rewindable.</summary>
public class ConversationDocFamilyToken : DocFamilyToken { private protected ConversationDocFamilyToken(DocDefinition definition) : base(definition) { }
    internal static ConversationDocFamilyToken Latest(DocDefinition definition) => new(definition); }

/// <summary>A rewindable conversation family, which also supports as-of reads.</summary>
public sealed class RewindableConversationDocFamilyToken : ConversationDocFamilyToken { internal RewindableConversationDocFamilyToken(DocDefinition definition) : base(definition) { } }

/// <summary>A task-scoped family.</summary>
public sealed class TaskDocFamilyToken : DocFamilyToken { internal TaskDocFamilyToken(DocDefinition definition) : base(definition) { } }

/// <summary>A logical address plus its string identity for maps.</summary>
/// <param name="Address">The address.</param>
/// <param name="Id">Its stable string identity (<see cref="Documents.AddressId"/>).</param>
internal sealed record ResolvedAddress(DocumentAddress Address, string Id);

/// <summary>Document definitions (upstream <c>defineDoc</c> and <c>defineDocFamily</c>) and the address and version checks typed access
/// shares.</summary>
public static class Documents
{
    /// <summary>Defines a Session-scoped singleton document.</summary>
    public static SessionDocToken DefineSessionDoc(string kind, int version, Func<JsonObjectValue> initial,
        DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        new(Singleton(kind, version, DocumentScopeKind.Session, null, null, initial, migrate, checkpointWhen));

    /// <summary>Defines a latest-only conversation singleton document; <paramref name="fork"/> is <see cref="DocumentFork.Current"/> or
    /// <see cref="DocumentFork.Initial"/>.</summary>
    public static ConversationDocToken DefineConversationDoc(string kind, int version, DocumentFork fork, Func<JsonObjectValue> initial,
        DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        ConversationDocToken.Latest(Singleton(kind, version, DocumentScopeKind.Conversation, DocumentHistory.Latest, fork, initial, migrate, checkpointWhen));

    /// <summary>Defines a rewindable conversation singleton document.</summary>
    public static RewindableConversationDocToken DefineRewindableConversationDoc(string kind, int version, DocumentFork fork,
        Func<JsonObjectValue> initial, DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        new(Singleton(kind, version, DocumentScopeKind.Conversation, DocumentHistory.Rewindable, fork, initial, migrate, checkpointWhen));

    /// <summary>Defines a task-scoped singleton document.</summary>
    public static TaskDocToken DefineTaskDoc(string kind, int version, Func<JsonObjectValue> initial,
        DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        new(Singleton(kind, version, DocumentScopeKind.Task, null, null, initial, migrate, checkpointWhen));

    /// <summary>Defines a Session-scoped document family.</summary>
    public static SessionDocFamilyToken DefineSessionDocFamily(string kind, int version, Func<JsonValue, JsonObjectValue> initial,
        DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        new(Family(kind, version, DocumentScopeKind.Session, null, null, initial, migrate, checkpointWhen));

    /// <summary>Defines a latest-only conversation document family; <paramref name="fork"/> is <see cref="DocumentFork.Current"/> or
    /// <see cref="DocumentFork.Initial"/>.</summary>
    public static ConversationDocFamilyToken DefineConversationDocFamily(string kind, int version, DocumentFork fork,
        Func<JsonValue, JsonObjectValue> initial, DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        ConversationDocFamilyToken.Latest(Family(kind, version, DocumentScopeKind.Conversation, DocumentHistory.Latest, fork, initial, migrate, checkpointWhen));

    /// <summary>Defines a rewindable conversation document family.</summary>
    public static RewindableConversationDocFamilyToken DefineRewindableConversationDocFamily(string kind, int version, DocumentFork fork,
        Func<JsonValue, JsonObjectValue> initial, DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        new(Family(kind, version, DocumentScopeKind.Conversation, DocumentHistory.Rewindable, fork, initial, migrate, checkpointWhen));

    /// <summary>Defines a task-scoped document family.</summary>
    public static TaskDocFamilyToken DefineTaskDocFamily(string kind, int version, Func<JsonValue, JsonObjectValue> initial,
        DocumentMigration? migrate = null, DocumentCheckpointPredicate? checkpointWhen = null) =>
        new(Family(kind, version, DocumentScopeKind.Task, null, null, initial, migrate, checkpointWhen));

    private static DocDefinition Singleton(string kind, int version, DocumentScopeKind scope, DocumentHistory? history, DocumentFork? fork,
        Func<JsonObjectValue> initial, DocumentMigration? migrate, DocumentCheckpointPredicate? checkpointWhen)
    {
        ArgumentNullException.ThrowIfNull(initial);
        return new(kind, version, scope, history, fork, family: false, _ => initial(), migrate, checkpointWhen);
    }

    private static DocDefinition Family(string kind, int version, DocumentScopeKind scope, DocumentHistory? history, DocumentFork? fork,
        Func<JsonValue, JsonObjectValue> initial, DocumentMigration? migrate, DocumentCheckpointPredicate? checkpointWhen)
    {
        ArgumentNullException.ThrowIfNull(initial);
        return new(kind, version, scope, history, fork, family: true,
            seed => initial(seed ?? throw new ArgumentNullException(nameof(seed), $"Document family {kind} requires a seed")), migrate, checkpointWhen);
    }

    /// <summary>Resolves a typed access's owner and family key to a logical address (upstream <c>resolveAddress</c>, whose argument list
    /// the typed overloads replace).</summary>
    internal static ResolvedAddress ResolveAddress(DocDefinition definition, long? ownerId, string? key)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var scope = definition.Scope switch
        {
            DocumentScopeKind.Session => DocumentScope.Session,
            DocumentScopeKind.Conversation => DocumentScope.Conversation(new ConversationId(OwnerId(ownerId, definition))),
            _ => DocumentScope.Task(new TaskId(OwnerId(ownerId, definition))),
        };
        if (definition.Family && key is null) throw new ArgumentException($"Document {definition.Kind} requires a family key");
        var address = new DocumentAddress(definition.Kind, scope, definition.Family ? key : null);
        return new(address, AddressId(address));
    }

    private static long OwnerId(long? value, DocDefinition definition) => value is { } id && Ids.IsSafeInteger(id)
        ? id : throw new ArgumentException($"Document {definition.Kind} requires a {Internal.WireName.Of(definition.Scope)} ID");

    /// <summary>Stable string identity of one logical address: <c>JSON.stringify([kind, scopeKind, owner, key])</c>.</summary>
    internal static string AddressId(DocumentAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        JsonValue owner = address.Scope.Kind == DocumentScopeKind.Session ? JsonValue.Null : JsonValue.Number(address.Scope.OwnerId);
        return JsonValue.Array(JsonValue.String(address.Kind), JsonValue.String(Internal.WireName.Of(address.Scope.Kind)), owner,
            address.Key is null ? JsonValue.Null : JsonValue.String(address.Key)).ToJson();
    }

    /// <summary>The storage create record for a new incarnation at <paramref name="address"/> (upstream <c>documentCreate</c>).</summary>
    internal static DocumentCreate CreateRecord(DocDefinition definition, DocumentAddress address, DocumentId id) => new()
    {
        Id = id,
        Kind = address.Kind,
        Key = address.Key,
        Scope = address.Scope,
        History = address.Scope.Kind == DocumentScopeKind.Conversation ? definition.History : null,
        Fork = address.Scope.Kind == DocumentScopeKind.Conversation ? definition.Fork : null,
    };

    /// <summary>Rejects typed access whose definition disagrees with the persisted scope, history or fork semantics.</summary>
    internal static void CheckRecordScope(DocDefinition definition, IDocumentDescriptor record)
    {
        if (record.Scope.Kind != definition.Scope ||
            (record.Scope.Kind == DocumentScopeKind.Conversation && (record.History != definition.History || record.Fork != definition.Fork)))
            throw new ArgumentException($"Document {record.Id} ({record.Kind}) does not match the supplied definition semantics");
    }

    /// <summary>Rejects typed access to a stored version the definition cannot use.</summary>
    internal static void CheckRecordVersion(DocDefinition definition, IDocumentDescriptor record, int version)
    {
        if (version > definition.Version)
            throw new InvalidOperationException($"Document {record.Id} ({record.Kind}) has newer version {version} than {definition.Version}");
        if (version < definition.Version && definition.Migrate is null)
            throw new InvalidOperationException($"Document {record.Id} ({record.Kind}) requires migration from version {version}");
    }

    /// <summary>Validates and materializes a detached stored value for typed access.</summary>
    internal static JsonObjectValue MaterializeDocument(DocDefinition definition, StoredDocument stored) =>
        MaterializeDocumentValue(definition, stored.Record, stored.Version, stored.Value);

    /// <summary>Validates and materializes one value, migrating an older version (in memory only).</summary>
    internal static JsonObjectValue MaterializeDocumentValue(DocDefinition definition, IDocumentDescriptor record, int version, JsonObjectValue value)
    {
        CheckRecordScope(definition, record);
        CheckRecordVersion(definition, record, version);
        if (version == definition.Version) return value;
        // Upstream copies the migrated value into strict JSON; an immutable JsonObjectValue already is.
        return definition.Migrate!(value, version) ?? throw new InvalidOperationException($"Document {record.Kind} migrate() must return a JSON object");
    }
}
