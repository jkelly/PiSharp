// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/types.ts (document semantics, records and content; split from
// Types.cs). JSON key orders follow the Session (documentCreate() in documents.ts) and MemoryStorage (createdAt/retiredAt stamped last).
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>Who owns a document and how long it lives.</summary>
public enum DocumentScopeKind
{
    /// <summary>Belongs to the Session; current-only; survives close and reopen.</summary>
    Session,
    /// <summary>Belongs to one conversation and declares history and fork behavior.</summary>
    Conversation,
    /// <summary>Belongs to one task; current-only; retired when the task becomes terminal.</summary>
    Task,
}

/// <summary>How much history a conversation document keeps.</summary>
public enum DocumentHistory
{
    /// <summary>Only the current state.</summary>
    Latest,
    /// <summary>The history needed for as-of reads.</summary>
    Rewindable,
}

/// <summary>How a conversation fork initializes a conversation document.</summary>
public enum DocumentFork
{
    /// <summary>The parent's value at the fork entry's commit; rewindable documents only.</summary>
    AsOf,
    /// <summary>The committed parent value when the fork commit runs.</summary>
    Current,
    /// <summary>No copy; the initializer runs on first child access.</summary>
    Initial,
}

/// <summary>The exact scope of a document: the Session, one conversation or one task. JSON: <c>{kind}</c>, <c>{kind, conversationId}</c> or
/// <c>{kind, taskId}</c>.</summary>
public sealed record DocumentScope
{
    private DocumentScope(DocumentScopeKind kind, long ownerId) { Kind = kind; OwnerId = ownerId; }

    /// <summary>The Session scope.</summary>
    public static DocumentScope Session { get; } = new(DocumentScopeKind.Session, 0);
    /// <summary>One conversation's scope.</summary>
    public static DocumentScope Conversation(ConversationId conversationId) => new(DocumentScopeKind.Conversation, conversationId.Value);
    /// <summary>One task's scope.</summary>
    public static DocumentScope Task(TaskId taskId) => new(DocumentScopeKind.Task, taskId.Value);

    /// <summary>The scope kind.</summary>
    public DocumentScopeKind Kind { get; }
    /// <summary>The owning conversation or task ID; 0 for the Session.</summary>
    public long OwnerId { get; }
    /// <summary>The owning conversation of a conversation scope.</summary>
    public ConversationId? ConversationId => Kind == DocumentScopeKind.Conversation ? new ConversationId(OwnerId) : null;
    /// <summary>The owning task of a task scope.</summary>
    public TaskId? TaskId => Kind == DocumentScopeKind.Task ? new TaskId(OwnerId) : null;

    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() => Kind switch
    {
        DocumentScopeKind.Session => new RecordJson.Writer().Add("kind", "session").Build(),
        DocumentScopeKind.Conversation => new RecordJson.Writer().Add("kind", "conversation").Number("conversationId", OwnerId).Build(),
        _ => new RecordJson.Writer().Add("kind", "task").Number("taskId", OwnerId).Build(),
    };

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static DocumentScope FromJson(JsonValue json)
    {
        const string what = "document scope";
        var value = RecordJson.Object(json, what);
        switch (RecordJson.Enum<DocumentScopeKind>(value, "kind", what))
        {
            case DocumentScopeKind.Session:
                RecordJson.OnlyKeys(value, what, "kind");
                return Session;
            case DocumentScopeKind.Conversation:
                RecordJson.OnlyKeys(value, what, "kind", "conversationId");
                return Conversation(RecordJson.Id<ConversationId>(value, "conversationId", what));
            default:
                RecordJson.OnlyKeys(value, what, "kind", "taskId");
                return Task(RecordJson.Id<TaskId>(value, "taskId", what));
        }
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>Identity and semantics shared by <see cref="DocumentCreate"/> and <see cref="DocumentRecord"/>.</summary>
public interface IDocumentDescriptor
{
    /// <summary>Unique incarnation ID; never reused when the same logical document is recreated.</summary>
    DocumentId Id { get; }
    /// <summary>Stable document definition kind.</summary>
    string Kind { get; }
    /// <summary>Family member key; absent for singleton documents.</summary>
    string? Key { get; }
    /// <summary>The scope.</summary>
    DocumentScope Scope { get; }
    /// <summary>History of a conversation document; absent for other scopes.</summary>
    DocumentHistory? History { get; }
    /// <summary>Fork behavior of a conversation document; absent for other scopes.</summary>
    DocumentFork? Fork { get; }
}

/// <summary>Fields supplied when storage creates and stamps a new <see cref="DocumentRecord"/> (upstream <c>DocumentCreate</c>). JSON key
/// order: <c>id, kind, key, scope, history, fork</c>.</summary>
public sealed record DocumentCreate : IDocumentDescriptor
{
    /// <inheritdoc/>
    public required DocumentId Id { get; init; }
    /// <inheritdoc/>
    public required string Kind { get; init; }
    /// <inheritdoc/>
    public string? Key { get; init; }
    /// <inheritdoc/>
    public required DocumentScope Scope { get; init; }
    /// <inheritdoc/>
    public DocumentHistory? History { get; init; }
    /// <inheritdoc/>
    public DocumentFork? Fork { get; init; }

    /// <summary>Throws unless exactly conversation scopes declare history and fork, and <c>asOf</c> forks are rewindable.</summary>
    public DocumentCreate Validate() { DocumentShape.Validate(this, "document create"); return this; }

    /// <summary>The record storage stamps for this creation.</summary>
    public DocumentRecord ToRecord(Seq createdAt, Seq? retiredAt = null) => new()
    {
        Id = Id, Kind = Kind, Key = Key, Scope = Scope, History = History, Fork = Fork, CreatedAt = createdAt, RetiredAt = retiredAt,
    };

    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() => DocumentShape.Write(this).Build();

    /// <summary>Decodes and validates <see cref="ToJson"/>; <c>createdAt</c> and <c>retiredAt</c> are rejected because storage stamps them.</summary>
    public static DocumentCreate FromJson(JsonValue json)
    {
        const string what = "document create";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "id", "kind", "key", "scope", "history", "fork");
        return DocumentShape.Read(value, what).Validate();
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>Persisted lifecycle record for one create-to-retire document incarnation (upstream <c>DocumentRecord</c>). Membership is the
/// half-open interval <c>createdAt &lt;= at &lt; retiredAt</c>. JSON key order: <c>id, kind, key, scope, history, fork, createdAt,
/// retiredAt</c>.</summary>
public sealed record DocumentRecord : IDocumentDescriptor
{
    /// <inheritdoc/>
    public required DocumentId Id { get; init; }
    /// <inheritdoc/>
    public required string Kind { get; init; }
    /// <inheritdoc/>
    public string? Key { get; init; }
    /// <inheritdoc/>
    public required DocumentScope Scope { get; init; }
    /// <inheritdoc/>
    public DocumentHistory? History { get; init; }
    /// <inheritdoc/>
    public DocumentFork? Fork { get; init; }
    /// <summary>Commit that created the incarnation, stamped by storage.</summary>
    public required Seq CreatedAt { get; init; }
    /// <summary>Commit that retired the incarnation; absent while it is current.</summary>
    public Seq? RetiredAt { get; init; }

    /// <summary>Throws unless exactly conversation scopes declare history and fork, and <c>asOf</c> forks are rewindable.</summary>
    public DocumentRecord Validate() { DocumentShape.Validate(this, "document record"); return this; }

    /// <summary>The creation fields.</summary>
    public DocumentCreate ToCreate() => new() { Id = Id, Kind = Kind, Key = Key, Scope = Scope, History = History, Fork = Fork };

    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() =>
        DocumentShape.Write(this).Add("createdAt", CreatedAt.ToJson()).Add("retiredAt", RetiredAt?.ToJson()).Build();

    /// <summary>Decodes and validates <see cref="ToJson"/>.</summary>
    public static DocumentRecord FromJson(JsonValue json)
    {
        const string what = "document record";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "id", "kind", "key", "scope", "history", "fork", "createdAt", "retiredAt");
        var created = DocumentShape.Read(value, what);
        return created.ToRecord(new Seq(RecordJson.Integer(value, "createdAt", what)),
            RecordJson.OptionalInteger(value, "retiredAt", what) is { } retiredAt ? new Seq(retiredAt) : null).Validate();
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

internal static class DocumentShape
{
    public static RecordJson.Writer Write(IDocumentDescriptor value) => new RecordJson.Writer()
        .Add("id", value.Id.ToJson()).Add("kind", value.Kind).Add("key", value.Key).Add("scope", value.Scope.ToJson())
        .Add("history", value.History is { } history ? WireName.Of(history) : null).Add("fork", value.Fork is { } fork ? WireName.Of(fork) : null);

    public static DocumentCreate Read(JsonObjectValue value, string what) => new()
    {
        Id = RecordJson.Id<DocumentId>(value, "id", what),
        Kind = RecordJson.String(value, "kind", what),
        Key = RecordJson.OptionalString(value, "key", what),
        Scope = DocumentScope.FromJson(RecordJson.Required(value, "scope", what)),
        History = RecordJson.OptionalString(value, "history", what) is { } history ? WireName.Parse<DocumentHistory>(history, what) : null,
        Fork = RecordJson.OptionalString(value, "fork", what) is { } fork ? WireName.Parse<DocumentFork>(fork, what) : null,
    };

    public static void Validate(IDocumentDescriptor value, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Kind);
        ArgumentNullException.ThrowIfNull(value.Scope);
        if (value.Scope.Kind != DocumentScopeKind.Conversation)
        {
            if (value.History is not null || value.Fork is not null)
                throw RecordJson.Invalid(what, $"{WireName.Of(value.Scope.Kind)} documents do not declare history or fork behavior");
            return;
        }
        if (value.History is not { } history || value.Fork is not { } fork)
            throw RecordJson.Invalid(what, "conversation documents require history and fork policies");
        if (history == DocumentHistory.Latest && fork == DocumentFork.AsOf)
            throw RecordJson.Invalid(what, "fork \"asOf\" requires history \"rewindable\"");
    }
}

/// <summary>Stored replay state supplied to a document's checkpoint predicate.</summary>
/// <param name="DeltasSinceBase">Deltas already stored after the newest base, excluding the change being evaluated.</param>
public readonly record struct CheckpointInfo(int DeltasSinceBase);

/// <summary>Whether stored content is a complete base or an operation batch.</summary>
public enum DocumentContentKind
{
    /// <summary>A complete value.</summary>
    Base,
    /// <summary>A Chord operation batch.</summary>
    Delta,
}

/// <summary>A complete checkpoint or Chord operation batch selected by the owning Session (upstream <c>DocumentContent</c>). JSON key order:
/// <c>version, kind, value</c> or <c>version, kind, ops</c>, as the Session builds it.</summary>
public abstract class DocumentContent : DurableValue
{
    private protected DocumentContent(int version)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version), version, "Document versions are positive integers");
        Version = version;
    }

    /// <summary>The definition version of the stored value shape.</summary>
    public int Version { get; }
    /// <summary>Base or delta.</summary>
    public abstract DocumentContentKind Kind { get; }

    /// <summary>A complete base.</summary>
    public static DocumentBase Base(int version, JsonObjectValue value) => new(version, value);
    /// <summary>An operation batch.</summary>
    public static DocumentDelta Delta(int version, IEnumerable<Op> ops) => new(version, [.. ops]);

    /// <summary>Decodes <see cref="DurableValue.ToJson"/>; a base carries no operations and a delta no value.</summary>
    public static DocumentContent FromJson(JsonValue json)
    {
        const string what = "document content";
        var value = RecordJson.Object(json, what);
        var version = RecordJson.Version(value, "version", what);
        if (RecordJson.Enum<DocumentContentKind>(value, "kind", what) == DocumentContentKind.Base)
        {
            RecordJson.OnlyKeys(value, what, "version", "kind", "value");
            return new DocumentBase(version, RecordJson.Object(RecordJson.Required(value, "value", what), what))
            {
                KindFirst = value.Keys.Count > 0 && value.Keys[0] == "kind",
            };
        }
        RecordJson.OnlyKeys(value, what, "version", "kind", "ops");
        return new DocumentDelta(version, [.. RecordJson.Array(RecordJson.Required(value, "ops", what), "ops", what).Select(Op.FromJson)]);
    }
}

/// <summary><c>{version, kind: "base", value}</c>: a complete value. A base upstream's storage builds itself from a stored document (a fork
/// copy) is <c>{kind: "base", version, value}</c>; JsonlStorage writes that spelling for a resolved copy, and a base decoded in it keeps it
/// (<see cref="KindFirst"/>), so it encodes back to the same text.</summary>
public sealed class DocumentBase(int version, JsonObjectValue value) : DocumentContent(version)
{
    /// <inheritdoc/>
    public override DocumentContentKind Kind => DocumentContentKind.Base;
    /// <summary>The value.</summary>
    public JsonObjectValue Value { get; } = value ?? throw new ArgumentNullException(nameof(value));
    /// <summary>Whether the JSON leads with <c>kind</c>, as decoded from upstream storage's own <c>{ kind: "base", version, value }</c>.</summary>
    internal bool KindFirst { get; init; }
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => KindFirst
        ? new RecordJson.Writer().Add("kind", "base").Number("version", Version).Add("value", Value).Build()
        : new RecordJson.Writer().Number("version", Version).Add("kind", "base").Add("value", Value).Build();
}

/// <summary><c>{version, kind: "delta", ops}</c>: a Chord operation batch.</summary>
public sealed class DocumentDelta(int version, ImmutableArray<Op> ops) : DocumentContent(version)
{
    /// <inheritdoc/>
    public override DocumentContentKind Kind => DocumentContentKind.Delta;
    /// <summary>The operations.</summary>
    public ImmutableArray<Op> Ops { get; } = ops.IsDefault ? throw new ArgumentNullException(nameof(ops)) : ops;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Number("version", Version).Add("kind", "delta")
        .Add("ops", JsonValue.Array(Ops.Select(op => (JsonValue)op.ToJson()))).Build();
}

/// <summary>A detached materialized value and its stored definition version at a selected point (upstream <c>StoredDocument</c>).</summary>
/// <param name="Record">The incarnation.</param>
/// <param name="Version">The stored definition version of <paramref name="Value"/>.</param>
/// <param name="Value">The materialized value.</param>
/// <param name="DeltasSinceBase">Deltas replayed after the selected base to materialize <paramref name="Value"/>.</param>
public sealed record StoredDocument(DocumentRecord Record, int Version, JsonObjectValue Value, int DeltasSinceBase)
{
    /// <inheritdoc/>
    public bool Equals(StoredDocument? other) => other is not null && Record == other.Record && Version == other.Version &&
        DeltasSinceBase == other.DeltasSinceBase && JsonValue.DeepEquals(Value, other.Value);
    /// <inheritdoc/>
    public override int GetHashCode() => Record.Id.GetHashCode();
}
