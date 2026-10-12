// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/types.ts (entry drafts and typed entries, commit publications,
// the Tx and Session surfaces, document states and watches; split from Types.cs). Upstream's overloads keyed by token type become one C#
// overload per token type; its `(...) => void` disposers become IDisposable; its promises become tasks. A document value is a
// JsonObjectValue, and null stands for upstream's retired `null` value.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>Where a drafted entry starts the active context: an earlier entry, or <see cref="Self"/> (upstream <c>EntryId | "self"</c>).</summary>
public readonly record struct EntryHead
{
    private EntryHead(EntryId? id) { Id = id; }

    /// <summary><c>"self"</c>: the active context starts at the newly assigned entry ID.</summary>
    public static EntryHead Self => default;

    /// <summary>An earlier entry.</summary>
    public static EntryHead At(EntryId id) => new(id);

    /// <summary>An earlier entry.</summary>
    public static implicit operator EntryHead(EntryId id) => new(id);

    /// <summary>The earlier entry; null for <see cref="Self"/>.</summary>
    public EntryId? Id { get; }

    /// <summary>Whether this is <see cref="Self"/>.</summary>
    public bool IsSelf => Id is null;

    /// <summary>The head of an entry whose ID is <paramref name="self"/>.</summary>
    internal EntryId Resolve(EntryId self) => Id ?? self;
}

/// <summary>Entry content supplied before the Session assigns identity and task attribution (upstream <c>EntryDraft</c>).</summary>
public sealed record EntryDraft
{
    /// <summary>Application-defined entry discriminator.</summary>
    public required string Kind { get; init; }
    /// <summary>Wire messages contributed to model context; absent for display or bookkeeping entries.</summary>
    public JsonArrayValue? Model { get; init; }
    /// <summary>JSON payload; null omits it (JSON <c>null</c> is <see cref="JsonValue.Null"/>).</summary>
    public JsonValue? Data { get; init; }
    /// <summary>First entry in the active context selected by this entry; <see cref="EntryHead.Self"/> selects the new entry itself.</summary>
    public EntryHead? Head { get; init; }
    /// <summary>Context-only overrides of earlier visible entries.</summary>
    public ImmutableArray<ContextEdit>? Edits { get; init; }
}

/// <summary>Entry content of a data-less typed kind; the token supplies the kind (upstream <c>TypedEntryDraft&lt;never&gt;</c>).</summary>
public sealed record TypedEntryDraft
{
    /// <summary>Wire messages contributed to model context.</summary>
    public JsonArrayValue? Model { get; init; }
    /// <summary>First entry in the active context selected by this entry.</summary>
    public EntryHead? Head { get; init; }
    /// <summary>Context-only overrides of earlier visible entries.</summary>
    public ImmutableArray<ContextEdit>? Edits { get; init; }
}

/// <summary>Entry content of a typed kind carrying <typeparamref name="TData"/>; the token supplies the kind (upstream
/// <c>TypedEntryDraft&lt;D&gt;</c>).</summary>
public sealed record TypedEntryDraft<TData> where TData : JsonValue
{
    /// <summary>The typed payload.</summary>
    public required TData Data { get; init; }
    /// <summary>Wire messages contributed to model context.</summary>
    public JsonArrayValue? Model { get; init; }
    /// <summary>First entry in the active context selected by this entry.</summary>
    public EntryHead? Head { get; init; }
    /// <summary>Context-only overrides of earlier visible entries.</summary>
    public ImmutableArray<ContextEdit>? Edits { get; init; }
}

/// <summary>An entry whose data has type <typeparamref name="TData"/> (upstream <c>TypedEntry&lt;D&gt;</c>). A data-less kind's typed entry is
/// the plain <see cref="EntryRecord"/>.</summary>
public sealed class TypedEntry<TData> where TData : JsonValue
{
    internal TypedEntry(EntryRecord record)
    {
        Record = record;
        Data = record.Data as TData ?? throw new InvalidOperationException(
            $"Entry {record.Id} ({record.Kind}) carries no {typeof(TData).Name} data");
    }

    /// <summary>The underlying record.</summary>
    public EntryRecord Record { get; }
    /// <summary>The typed payload.</summary>
    public TData Data { get; }
    /// <summary>The entry ID.</summary>
    public EntryId Id => Record.Id;
    /// <summary>The conversation that appended it.</summary>
    public ConversationId ConversationId => Record.ConversationId;
    /// <summary>The entry kind.</summary>
    public string Kind => Record.Kind;
    /// <summary>Wire messages contributed to model context.</summary>
    public JsonArrayValue? Model => Record.Model;
    /// <summary>First entry in the active context selected by this entry.</summary>
    public EntryId? Head => Record.Head;
    /// <summary>Context-only overrides of earlier visible entries.</summary>
    public ImmutableArray<ContextEdit>? Edits => Record.Edits;
    /// <summary>Task that appended this entry.</summary>
    public TaskId? ByTaskId => Record.ByTaskId;

    /// <inheritdoc/>
    public override string ToString() => Record.ToString();
}

/// <summary>One change of a successful Session commit (upstream <c>CommitChange</c>): a complete table record write
/// (<see cref="ConversationWrite"/>, <see cref="EntryWrite"/>, <see cref="TaskWrite"/> or <see cref="SubmissionWrite"/>, the exact
/// write Storage admitted), a <see cref="DocumentCommitChange"/> or a <see cref="DocumentCopyCommitChange"/>.</summary>
public interface ICommitChange
{
    /// <summary><c>"conversation"</c>, <c>"entry"</c>, <c>"task"</c>, <c>"submission"</c>, <c>"document"</c> or <c>"document.copy"</c>.</summary>
    string Type { get; }
}

/// <summary>Committed change of one document incarnation (upstream <c>DocumentCommitChange</c> of type <c>"document"</c>).</summary>
/// <param name="Record">The incarnation, as committed by this commit.</param>
/// <param name="ConversationId">Conversation owning the document; task documents derive it from their task record. Null only for Session
/// documents.</param>
/// <param name="Version">Definition version of <paramref name="Value"/>; null when this commit retired the incarnation.</param>
/// <param name="Value">Exact adopted immutable revision, or null when this commit retired the incarnation.</param>
/// <param name="Ops">Exact adopted operations for an ordinary update; empty for creation and retirement.</param>
public sealed record DocumentCommitChange(DocumentRecord Record, ConversationId? ConversationId, int? Version, JsonObjectValue? Value,
    ImmutableArray<Op> Ops) : ICommitChange
{
    /// <inheritdoc/>
    public string Type => "document";
}

/// <summary>Definition-free child initialization by a fork (upstream <c>DocumentCommitChange</c> of type <c>"document.copy"</c>); consumers
/// hydrate through state or watch acquisition.</summary>
/// <param name="Record">The new child incarnation.</param>
/// <param name="ConversationId">The child conversation.</param>
/// <param name="Source">The exact stored source.</param>
public sealed record DocumentCopyCommitChange(DocumentRecord Record, ConversationId ConversationId, DocumentCopySource Source) : ICommitChange
{
    /// <inheritdoc/>
    public string Type => "document.copy";
}

/// <summary>Every immutable change from one successful Session commit; change order is unspecified (upstream <c>CommitPublication</c>).</summary>
/// <param name="Seq">The commit sequence.</param>
/// <param name="Changes">The changes.</param>
public sealed record CommitPublication(Seq Seq, IReadOnlyList<ICommitChange> Changes);

/// <summary>Why a watch terminated.</summary>
public enum WatchEndReason
{
    /// <summary><c>stop()</c> was called.</summary>
    Stopped,
    /// <summary>The acquisition context was cancelled.</summary>
    Cancelled,
    /// <summary>The Session closed.</summary>
    SessionClosed,
    /// <summary>The observed incarnation was retired and its retirement delivered.</summary>
    Retired,
    /// <summary>The listener failed; <see cref="WatchEnd.Error"/> holds the failure.</summary>
    ListenerError,
}

/// <summary>Terminal result of one watch (upstream <c>WatchEnd</c>).</summary>
/// <param name="Reason">Why it ended.</param>
/// <param name="Error">The listener failure of <see cref="WatchEndReason.ListenerError"/>; otherwise null.</param>
public sealed record WatchEnd(WatchEndReason Reason, Exception? Error = null)
{
    /// <summary><c>{reason: "stopped"}</c>.</summary>
    public static WatchEnd Stopped { get; } = new(WatchEndReason.Stopped);
    /// <summary><c>{reason: "cancelled"}</c>.</summary>
    public static WatchEnd Cancelled { get; } = new(WatchEndReason.Cancelled);
    /// <summary><c>{reason: "session_closed"}</c>.</summary>
    public static WatchEnd SessionClosed { get; } = new(WatchEndReason.SessionClosed);
    /// <summary><c>{reason: "retired"}</c>.</summary>
    public static WatchEnd Retired { get; } = new(WatchEndReason.Retired);
}

/// <summary>Serialized exact-frame observation of an immutable value with bounded pending delivery (upstream <c>WatchHandle</c>).</summary>
/// <typeparam name="T">The immutable value type; null retires the watch.</typeparam>
public interface IWatchHandle<T>
{
    /// <summary>The acquisition revision before <see cref="Start"/>; the latest delivered immutable revision afterward.</summary>
    T Value { get; }

    /// <summary>Installs the sole asynchronous listener. It is never invoked inline. Throws when already started or stopped.</summary>
    void Start(Func<T, ImmutableArray<Op>, DurableContext, Task> listener);

    /// <summary>Idempotently stops future callbacks and returns this watch's terminal result (always the same task).</summary>
    Task<WatchEnd> StopAsync();

    /// <summary>Completes when the watch terminates; an already-running callback remains caller-owned.</summary>
    Task<WatchEnd> Closed { get; }
}

/// <summary>Committed document reads (upstream <c>DocumentReader</c>). Reads never create a document and return null when it is absent;
/// an older stored version is migrated in memory only.</summary>
public interface IDocumentReader
{
    /// <summary>The current shareable immutable revision of a Session singleton.</summary>
    Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context);
    /// <summary>The current revision of a conversation singleton.</summary>
    Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context);
    /// <summary>The current revision of a task singleton.</summary>
    Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context);
    /// <summary>The current revision of a Session family member.</summary>
    Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context);
    /// <summary>The current revision of a conversation family member.</summary>
    Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context);
    /// <summary>The current revision of a task family member.</summary>
    Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context);

    /// <summary>A rewindable conversation singleton as of the commit of the visible entry <paramref name="at"/>, from the ancestor that owns
    /// the entry; null when no incarnation was alive then.</summary>
    Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context);
    /// <summary>A rewindable conversation family member as of the commit of the visible entry <paramref name="at"/>.</summary>
    Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
        DurableContext context);
}

/// <summary>Non-creating document watch acquisition shared by the Session and later invocation APIs (upstream <c>DocumentObserver</c>).
/// A watch binds one existing incarnation and returns null when it is absent; the acquisition context's cancellation stops it.</summary>
public interface IDocumentObserver
{
    /// <summary>Watches a Session singleton.</summary>
    Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocToken token, DurableContext context);
    /// <summary>Watches a conversation singleton.</summary>
    Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context);
    /// <summary>Watches a task singleton.</summary>
    Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context);
    /// <summary>Watches a Session family member.</summary>
    Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context);
    /// <summary>Watches a conversation family member.</summary>
    Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context);
    /// <summary>Watches a task family member.</summary>
    Task<IWatchHandle<JsonObjectValue?>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context);
}

/// <summary>
/// Owner of one mutation line, its records and its tracked documents (upstream <c>Session</c>). Every commit runs on one FIFO line;
/// only committed state is observable.
/// </summary>
public interface ISession : IDocumentObserver, IDocumentReader
{
    /// <summary>Runs one atomic transaction on the Session mutation line and returns the callback's result.</summary>
    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context);
    /// <summary>Runs one atomic asynchronous transaction on the Session mutation line.</summary>
    Task CommitAsync(Func<ITx, Task> change, DurableContext context);
    /// <summary>Runs one atomic synchronous transaction on the Session mutation line and returns the callback's result.</summary>
    Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context);
    /// <summary>Runs one atomic synchronous transaction on the Session mutation line.</summary>
    Task CommitAsync(Action<ITx> change, DurableContext context);

    /// <summary>Seals admission, settles admitted commits, then closes storage. Cancelling the wait does not reopen admission.</summary>
    Task CloseAsync(DurableContext context);

    /// <summary>Observes complete commits synchronously after adoption. The listener must not throw, block or call Session APIs.</summary>
    IDisposable SubscribeCommits(Action<CommitPublication, DurableContext> listener);

    /// <summary>Observes close synchronously when it begins. The listener must not throw, block or call Session APIs.</summary>
    IDisposable SubscribeClose(Action listener);

    /// <summary>A disposable read-only Chord state bound to the current incarnation of a Session singleton; null when absent.</summary>
    Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(SessionDocToken token, DurableContext context);
    /// <summary>A document state of a conversation singleton.</summary>
    Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context);
    /// <summary>A document state of a task singleton.</summary>
    Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(TaskDocToken token, TaskId taskId, DurableContext context);
    /// <summary>A document state of a Session family member.</summary>
    Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(SessionDocFamilyToken token, string key, DurableContext context);
    /// <summary>A document state of a conversation family member.</summary>
    Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context);
    /// <summary>A document state of a task family member.</summary>
    Task<AttachedReplicatedState<JsonObjectValue?>?> DocumentStateAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context);
}

/// <summary>
/// Transaction surface of one Session commit callback (upstream <c>Tx</c>). Table reads are allowed only before the first table write
/// (<see cref="ReadAfterWriteException"/>); document access stays available. Every operation rejects once the callback settles, and every
/// draft it returned is revoked. Table reads and creation results are trusted immutable values shared with commit listeners.
/// </summary>
public interface ITx
{
    /// <summary>The committed conversation.</summary>
    Task<ConversationRecord?> GetConversationAsync(ConversationId id);
    /// <summary>The committed entry.</summary>
    Task<EntryRecord?> GetEntryAsync(EntryId id);
    /// <summary>The committed entry when it has the token's kind; null when absent or of another kind.</summary>
    Task<EntryRecord?> GetEntryAsync(Entry token, EntryId id);
    /// <summary>The committed entry when it has the token's kind, with typed data; null when absent or of another kind.</summary>
    Task<TypedEntry<TData>?> GetEntryAsync<TData>(Entry<TData> token, EntryId id) where TData : JsonValue;
    /// <summary>The committed task.</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId id);
    /// <summary>Scans conversations with the caller's limit and cursor.</summary>
    Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor = null);
    /// <summary>Scans a conversation's visible entries with the caller's limit and cursor.</summary>
    Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor = null);
    /// <summary>The newest visible entry of the conversation that carries a head.</summary>
    Task<EntryRecord?> LatestHeadMarkerAsync(ConversationId conversationId);
    /// <summary>Scans tasks with the caller's limit and cursor.</summary>
    Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor = null);
    /// <summary>The committed submission with a conversation-scoped request ID.</summary>
    Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId);

    /// <summary>Creates a conversation with explicitly selected ownership.</summary>
    Task<ConversationRecord> CreateConversationAsync(ConversationOwnership ownership);
    /// <summary>Creates a history fork at one concrete visible entry with explicitly selected ownership; conversation documents are copied
    /// by their fork policy.</summary>
    Task<ConversationRecord> ForkConversationAsync(ConversationId parentConversationId, EntryId at, ConversationOwnership ownership);
    /// <summary>Appends an entry; the returned record is a Session-owned immutable value.</summary>
    Task<EntryRecord> AppendEntryAsync(ConversationId conversationId, EntryDraft value);
    /// <summary>Appends an entry of a data-less kind; the token supplies the kind.</summary>
    Task<EntryRecord> AppendEntryAsync(Entry token, ConversationId conversationId, TypedEntryDraft value);
    /// <summary>Appends an entry of a typed kind; the token supplies the kind and types the data.</summary>
    Task<TypedEntry<TData>> AppendEntryAsync<TData>(Entry<TData> token, ConversationId conversationId, TypedEntryDraft<TData> value)
        where TData : JsonValue;
    /// <summary>Creates a pending task with the definition's initial checkpoint. Execution belongs to the task runtime.</summary>
    Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options);
    /// <summary>Creates a raw submission record with a fresh ID. No admission rules apply: no busy check, inbox queueing or placement.</summary>
    Task<SubmissionRecord> CreateSubmissionAsync(SubmissionCreate create);
    /// <summary>Settles a queued or placed submission; only a placed input can be answered and a settled submission stays unchanged.
    /// Resolved against this transaction's latest record of the submission, so it works after table writes.</summary>
    void SettleSubmission(SubmissionId id, SubmissionSettlement settlement);
    /// <summary>Places a queued submission at <paramref name="entry"/>: an input becomes placed, a write done. Resolved like
    /// <see cref="SettleSubmission"/>.</summary>
    void PlaceSubmission(SubmissionId id, EntryId entry);

    /// <summary>Gets or creates the draft of a Session singleton.</summary>
    Task<DraftObject> DocAsync(SessionDocToken token);
    /// <summary>Gets or creates the draft of a conversation singleton.</summary>
    Task<DraftObject> DocAsync(ConversationDocToken token, ConversationId conversationId);
    /// <summary>Gets or creates the draft of a task singleton.</summary>
    Task<DraftObject> DocAsync(TaskDocToken token, TaskId taskId);
    /// <summary>Gets or creates the draft of a Session family member; <paramref name="seed"/> is used only when it is absent.</summary>
    Task<DraftObject> DocAsync(SessionDocFamilyToken token, string key, JsonValue seed);
    /// <summary>Gets or creates the draft of a conversation family member.</summary>
    Task<DraftObject> DocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, JsonValue seed);
    /// <summary>Gets or creates the draft of a task family member.</summary>
    Task<DraftObject> DocAsync(TaskDocFamilyToken token, TaskId taskId, string key, JsonValue seed);

    /// <summary>Retires a Session singleton (no-op when absent); an acquired draft persists its final content first.</summary>
    Task RetireDocAsync(SessionDocToken token);
    /// <summary>Retires a conversation singleton.</summary>
    Task RetireDocAsync(ConversationDocToken token, ConversationId conversationId);
    /// <summary>Retires a task singleton.</summary>
    Task RetireDocAsync(TaskDocToken token, TaskId taskId);
    /// <summary>Retires a Session family member.</summary>
    Task RetireDocAsync(SessionDocFamilyToken token, string key);
    /// <summary>Retires a conversation family member.</summary>
    Task RetireDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key);
    /// <summary>Retires a task family member.</summary>
    Task RetireDocAsync(TaskDocFamilyToken token, TaskId taskId, string key);
}
