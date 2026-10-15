// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/types.ts (scans, cursors, document addressing, storage writes
// and the Storage interface; split from Types.cs). Spec §10 is the normative contract.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>ID order of a scan: ascending is oldest first, descending newest first.</summary>
public enum ScanOrder
{
    /// <summary>Oldest first.</summary>
    Ascending,
    /// <summary>Newest first.</summary>
    Descending,
}

/// <summary>One ordered scan result and its optional continuation (upstream <c>Page&lt;T, Cursor&gt;</c>). A cursor is a backend-owned
/// JSON object that callers only round-trip to the same scan on the same storage; it carries the scan's order.</summary>
/// <param name="Items">At most the requested limit of items.</param>
/// <param name="Next">The continuation, or null when the scan is complete.</param>
public sealed record Page<T>(ImmutableArray<T> Items, JsonObjectValue? Next);

/// <summary>Optional filters for an ordered conversation scan; owner filters are conjunctive.</summary>
public sealed record ConversationQuery
{
    /// <summary>Only conversations whose owner edge names this conversation.</summary>
    public ConversationId? OwnerConversationId { get; init; }
    /// <summary>Only conversations whose owner edge names this task.</summary>
    public TaskId? OwnerTaskId { get; init; }
    /// <summary>Default ascending; with a cursor, the cursor's order.</summary>
    public ScanOrder? Order { get; init; }
}

/// <summary>Inclusive ID bounds for a scan of one conversation's fork-aware history.</summary>
public sealed record EntryQuery(ConversationId ConversationId)
{
    /// <summary>Oldest entry ID that may be returned.</summary>
    public EntryId? MinEntryId { get; init; }
    /// <summary>Newest entry ID that may be returned.</summary>
    public EntryId? MaxEntryId { get; init; }
    /// <summary>Default descending; with a cursor, the cursor's order.</summary>
    public ScanOrder? Order { get; init; }
}

/// <summary>Optional filters for an ordered scan of task records; every supplied filter must match.</summary>
public sealed record TaskQuery
{
    /// <summary>Only this conversation's tasks.</summary>
    public ConversationId? ConversationId { get; init; }
    /// <summary>Only this kind.</summary>
    public string? Kind { get; init; }
    /// <summary>Only this status.</summary>
    public DurableTaskStatus? Status { get; init; }
    /// <summary>Only tasks with or without an abort mark.</summary>
    public bool? AbortRequested { get; init; }
    /// <summary>Only background or foreground tasks.</summary>
    public bool? Background { get; init; }
    /// <summary>Default ascending; with a cursor, the cursor's order.</summary>
    public ScanOrder? Order { get; init; }
}

/// <summary>Optional filters for an ordered scan of submission records.</summary>
public sealed record SubmissionQuery
{
    /// <summary>Only this conversation's submissions.</summary>
    public ConversationId? ConversationId { get; init; }
    /// <summary>Only this status.</summary>
    public SubmissionStatus? Status { get; init; }
    /// <summary>Default ascending; with a cursor, the cursor's order.</summary>
    public ScanOrder? Order { get; init; }
}

/// <summary>Current state or one historical commit sequence, for document membership and content reads (upstream <c>Seq | "current"</c>).
/// The default value is <see cref="Current"/>.</summary>
public readonly record struct DocumentPoint
{
    private readonly Seq? seq;
    private DocumentPoint(Seq seq) { this.seq = seq; }

    /// <summary>Current state.</summary>
    public static DocumentPoint Current => default;
    /// <summary>The state after the commit with sequence <paramref name="seq"/>.</summary>
    public static DocumentPoint At(Seq seq) => new(seq);
    /// <summary>A historical point.</summary>
    public static implicit operator DocumentPoint(Seq seq) => new(seq);

    /// <summary>Whether this is the current point.</summary>
    public bool IsCurrent => seq is null;
    /// <summary>The historical sequence, or null for the current point.</summary>
    public Seq? Seq => seq;

    /// <summary><c>"current"</c> or the sequence number.</summary>
    public JsonValue ToJson() => seq is { } value ? value.ToJson() : JsonValue.String("current");

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static DocumentPoint FromJson(JsonValue json) => json switch
    {
        JsonStringValue { Value: "current" } => Current,
        JsonNumberValue => new(new Seq(RecordJson.Integer(json, "at", "document point"))),
        _ => throw RecordJson.Invalid("document point", "expected \"current\" or a sequence"),
    };

    /// <inheritdoc/>
    public override string ToString() => seq?.ToString() ?? "current";
}

/// <summary>Exact logical identity of a singleton (no key) or one keyed family member. JSON key order: <c>kind, scope, key</c>.</summary>
public sealed record DocumentAddress(string Kind, DocumentScope Scope, string? Key = null)
{
    /// <summary>The address of <paramref name="document"/>.</summary>
    public static DocumentAddress Of(IDocumentDescriptor document) => new(document.Kind, document.Scope, document.Key);

    /// <summary>The JSON.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("kind", Kind).Add("scope", Scope.ToJson()).Add("key", Key).Build();
}

/// <summary>Ordered scan of document incarnations alive in one exact scope at one point, optionally of one kind.</summary>
public sealed record DocumentQuery(DocumentScope Scope, DocumentPoint At)
{
    /// <summary>Only this kind (one singleton or family).</summary>
    public string? Kind { get; init; }
}

/// <summary>Exact persisted source selected for a definition-free document copy. JSON key order: <c>id, at</c>.</summary>
public sealed record DocumentCopySource(DocumentId Id, DocumentPoint At)
{
    /// <summary>The JSON.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("id", Id.ToJson()).Add("at", At.ToJson()).Build();

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static DocumentCopySource FromJson(JsonValue json)
    {
        const string what = "document copy source";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "id", "at");
        return new(RecordJson.Id<DocumentId>(value, "id", what), DocumentPoint.FromJson(RecordJson.Required(value, "at", what)));
    }
}

/// <summary>One entry and the sequence of the commit that persisted it.</summary>
public sealed record CommittedEntry(EntryRecord Entry, Seq CommitSeq);

/// <summary>One record or document mutation in an atomic storage commit (upstream <c>StorageWrite</c>). JSON key order: <c>type</c>, then
/// the variant's fields in declaration order.</summary>
public abstract record StorageWrite
{
    private protected StorageWrite() { }

    /// <summary>The persisted discriminator, such as <c>"entry"</c> or <c>"document.create"</c>.</summary>
    public abstract string Type { get; }

    /// <summary>The JSON.</summary>
    public abstract JsonObjectValue ToJson();

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static StorageWrite FromJson(JsonValue json)
    {
        const string what = "storage write";
        var value = RecordJson.Object(json, what);
        var type = RecordJson.String(value, "type", what);
        switch (type)
        {
            case "conversation" or "entry" or "task" or "submission":
            {
                RecordJson.OnlyKeys(value, what, "type", "value");
                var record = RecordJson.Required(value, "value", what);
                return type switch
                {
                    "conversation" => new ConversationWrite(ConversationRecord.FromJson(record)),
                    "entry" => new EntryWrite(EntryRecord.FromJson(record)),
                    "task" => new TaskWrite(TaskRecord.FromJson(record)),
                    _ => new SubmissionWrite(SubmissionRecord.FromJson(record)),
                };
            }
            case "document.create":
            {
                RecordJson.OnlyKeys(value, what, "type", "record", "content");
                var content = DocumentContent.FromJson(RecordJson.Required(value, "content", what)) as DocumentBase ??
                    throw RecordJson.Invalid(what, "document creation always starts from a complete base");
                return new DocumentCreateWrite(DocumentCreate.FromJson(RecordJson.Required(value, "record", what)), content);
            }
            case "document.copy":
                RecordJson.OnlyKeys(value, what, "type", "record", "source");
                return new DocumentCopyWrite(DocumentCreate.FromJson(RecordJson.Required(value, "record", what)),
                    DocumentCopySource.FromJson(RecordJson.Required(value, "source", what)));
            case "document.change":
                RecordJson.OnlyKeys(value, what, "type", "id", "content");
                return new DocumentChangeWrite(RecordJson.Id<DocumentId>(value, "id", what), DocumentContent.FromJson(RecordJson.Required(value, "content", what)));
            case "document.retire":
                RecordJson.OnlyKeys(value, what, "type", "id");
                return new DocumentRetireWrite(RecordJson.Id<DocumentId>(value, "id", what));
            default:
                throw RecordJson.Invalid(what, $"unknown type \"{type}\"");
        }
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary><c>{type: "conversation", value}</c>: creates a conversation; conversations are immutable.</summary>
public sealed record ConversationWrite(ConversationRecord Value) : StorageWrite, ICommitChange
{
    /// <inheritdoc/>
    public override string Type => "conversation";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("type", Type).Add("value", Value.ToJson()).Build();
}

/// <summary><c>{type: "entry", value}</c>: appends an entry; entries are immutable.</summary>
public sealed record EntryWrite(EntryRecord Value) : StorageWrite, ICommitChange
{
    /// <inheritdoc/>
    public override string Type => "entry";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("type", Type).Add("value", Value.ToJson()).Build();
}

/// <summary><c>{type: "task", value}</c>: creates or completely replaces a task record.</summary>
public sealed record TaskWrite(TaskRecord Value) : StorageWrite, ICommitChange
{
    /// <inheritdoc/>
    public override string Type => "task";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("type", Type).Add("value", Value.ToJson()).Build();
}

/// <summary><c>{type: "submission", value}</c>: creates or completely replaces a submission record.</summary>
public sealed record SubmissionWrite(SubmissionRecord Value) : StorageWrite, ICommitChange
{
    /// <inheritdoc/>
    public override string Type => "submission";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("type", Type).Add("value", Value.ToJson()).Build();
}

/// <summary><c>{type: "document.create", record, content}</c>: creates an incarnation from a complete base.</summary>
public sealed record DocumentCreateWrite(DocumentCreate Record, DocumentBase Content) : StorageWrite
{
    /// <inheritdoc/>
    public override string Type => "document.create";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("type", Type).Add("record", Record.ToJson()).Add("content", Content.ToJson()).Build();
}

/// <summary><c>{type: "document.copy", record, source}</c>: creates an incarnation from a stored source's committed pre-batch value and
/// version.</summary>
public sealed record DocumentCopyWrite(DocumentCreate Record, DocumentCopySource Source) : StorageWrite
{
    /// <inheritdoc/>
    public override string Type => "document.copy";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("type", Type).Add("record", Record.ToJson()).Add("source", Source.ToJson()).Build();
}

/// <summary><c>{type: "document.change", id, content}</c>: stores a base or a delta for a current incarnation.</summary>
public sealed record DocumentChangeWrite(DocumentId Id, DocumentContent Content) : StorageWrite
{
    /// <inheritdoc/>
    public override string Type => "document.change";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("type", Type).Add("id", Id.ToJson()).Add("content", Content.ToJson()).Build();
}

/// <summary><c>{type: "document.retire", id}</c>: retires a current incarnation after any content in the same batch.</summary>
public sealed record DocumentRetireWrite(DocumentId Id) : StorageWrite
{
    /// <inheritdoc/>
    public override string Type => "document.retire";
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("type", Type).Add("id", Id.ToJson()).Build();
}

/// <summary>
/// Atomic persistence boundary for Session records (upstream <c>Storage</c>, spec §10).
/// <para>Storage trusts the owning Session to supply semantically valid records, references, ancestry and transitions. Implementations
/// enforce atomicity, global ID ownership, immutable conversation/entry creation, document record consistency and detached values; the
/// Session serializes commits, so implementations add no caller-facing commit mutex.</para>
/// <para>An exception a method throws is final: it fails the Session, which nothing retries, so retry transient failures inside the method.
/// Two exceptions of a read fail only that read: a <see cref="StorageRequestException"/> for an invalid request (an unknown conversation,
/// a foreign or malformed cursor, history a document does not keep), which must have no durable effect, and a rejection because its
/// caller's context was cancelled. Any exception from <see cref="CommitAsync"/> or <see cref="MintIdAsync{TId}"/> is fatal.</para>
/// </summary>
public interface IStorage
{
    /// <summary>Atomically persists one batch and returns its sequence. Once it completes, later reads through this storage observe it.</summary>
    Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context);

    /// <summary>Returns a fresh candidate from the Session-global numeric ID namespace, greater than every ID minted or stored before, also
    /// across reopen: scans order by ID as creation order, and the Harness relies on it.</summary>
    Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId>;

    /// <summary>Looks up one conversation by exact ID.</summary>
    Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context);

    /// <summary>Scans conversations in <c>query.Order</c> (default ascending) by ID.</summary>
    Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context);

    /// <summary>Looks up one global entry and the sequence of the commit that persisted it.</summary>
    Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context);

    /// <summary>Looks up one entry only when it is visible through the conversation's ancestry; rejects an unknown conversation.</summary>
    Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context);

    /// <summary>Returns the newest visible entry with a head at or below the optional inclusive cutoff. The returned entry is the marker;
    /// its <see cref="EntryRecord.Head"/> (never null) is the range's actual lower bound.</summary>
    Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context);

    /// <summary>Scans the inclusive visible range in <c>query.Order</c> (default descending), returning at most <paramref name="limit"/>
    /// entries.</summary>
    Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context);

    /// <summary>Looks up the latest complete record for one task.</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context);

    /// <summary>Scans task records matching every supplied filter in <c>query.Order</c> (default ascending) by ID.</summary>
    Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context);

    /// <summary>Looks up the latest complete record for one admitted submission.</summary>
    Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context);

    /// <summary>Scans submissions matching every supplied filter in <c>query.Order</c> (default ascending) by ID.</summary>
    Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context);

    /// <summary>Finds a submission by its conversation-scoped host deduplication key.</summary>
    Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context);

    /// <summary>Resolves the incarnation occupying one exact logical address at the selected point.</summary>
    Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context);

    /// <summary>Materializes one specific incarnation by ID at the selected point without following a replacement at its address.</summary>
    Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context);

    /// <summary>Scans incarnations alive in one exact scope at the selected point, in ascending ID order.</summary>
    Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context);

    /// <summary>Releases backend resources; all later operations reject.</summary>
    Task CloseAsync(DurableContext context);
}
