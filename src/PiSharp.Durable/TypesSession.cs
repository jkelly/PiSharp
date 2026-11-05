// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/types.ts (entry drafts and typed entries, commit publications,
// the Tx and Session surfaces, document states and watches; split from Types.cs). Upstream's overloads keyed by token type become one C#
// overload per token type; its `(...) => void` disposers become IDisposable; its promises become tasks. A document value is a
// JsonObjectValue.
// Pi v1.5.0 sync (area watches, 92ab53602 and 42cb822f1): the watch types are Chord's (Chord/Watch.cs: IWatchHandle<T>, WatchEnd,
// WatchCall<T> and NativeWatchCall<T>, which upstream's index re-exports; in C# they stay in PiSharp.Durable.Chord), and the document
// state getters and their type are removed (Session.documentState(), DocumentState). Area watch-folding (8b0caefc2 and d8d99950a): a
// document watch follows its address and its value is never null (DocumentWatch<T> = WatchHandle<Readonly<T>>), so WatchDocAsync returns
// IWatchHandle<JsonObjectValue>. Area deferred-commits (6c2fd73b0): a commit takes CommitOptions, whose CommitPersistence may defer storing
// it (spec §4.1), and a CommitPublication is what one storage transaction stored. Area observers (8b0caefc2, with v1.5.0's shapes):
// Observer<T> and Observation<T>, and ISession.ObserveDocAsync; Observer<T> is a sealed class over two callbacks (a synchronous advance,
// or one returning a task, whose fault counts as upstream's rejected promise) instead of a structural type. Area vocabulary (5f312dac4):
// SubscribeCommits(listener) is ObserveCommits(CommitObserver), returning a CommitObservation (CommitObserver is a sealed class over two
// callbacks, as Observer<T> is); SessionEnd is tagged by Kind (SessionEndKind, was Reason); and DocumentCommitChange is tagged: an abstract
// record whose Kind says which of its two records it is, DocumentValueChange (a creation or an update, with its version, value and ops) or
// DocumentRetireChange (a retirement), where a retirement used to be a null value. The citation stays on v1.2.0 until the file's other
// areas land.
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
    /// <summary>
    /// The draft's JSON key order, which leads the appended record's (see <see cref="EntryRecord"/>): a draft decoded from JSON (an inbox write
    /// item) keeps its own, a typed append is <see cref="TypedOrder"/>, and the default is an untyped draft literal's, <see cref="DefaultOrder"/>.
    /// </summary>
    internal ImmutableArray<string> KeyOrder { get; init; }

    /// <summary>Upstream's untyped draft literals: <c>{ kind, head, model, data }</c> (compaction, reset, write submissions).</summary>
    internal static readonly ImmutableArray<string> DefaultOrder = ["kind", "head", "model", "data", "edits"];

    /// <summary>A typed append, <c>{ ...draft, kind: token.kind }</c>, over upstream's typed draft literals (<c>{ model }</c>, <c>{ model, data }</c>,
    /// <c>{ head, model }</c>).</summary>
    internal static readonly ImmutableArray<string> TypedOrder = ["head", "model", "data", "edits", "kind"];
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

/// <summary>Which committed change of a document incarnation a <see cref="DocumentCommitChange"/> is (upstream
/// <c>DocumentCommitChange["kind"]</c>).</summary>
public enum DocumentCommitChangeKind
{
    /// <summary><c>change</c>: a creation or an update (<see cref="DocumentValueChange"/>).</summary>
    Change,
    /// <summary><c>retire</c>: this commit retired the incarnation (<see cref="DocumentRetireChange"/>).</summary>
    Retire,
}

/// <summary>
/// Committed change of one document incarnation (upstream <c>DocumentCommitChange</c> of type <c>"document"</c>), tagged by
/// <see cref="Kind"/>: a <see cref="DocumentValueChange"/> (<c>kind: "change"</c>, a creation or an update) or a
/// <see cref="DocumentRetireChange"/> (<c>kind: "retire"</c>, this commit retired the incarnation). Only a change carries a value.
/// </summary>
/// <param name="Record">The incarnation, as committed by this commit.</param>
/// <param name="ConversationId">Conversation owning the document; task documents derive it from their task record. Null only for Session
/// documents.</param>
public abstract record DocumentCommitChange(DocumentRecord Record, ConversationId? ConversationId) : ICommitChange
{
    /// <inheritdoc/>
    public string Type => "document";

    /// <summary>A change or a retirement.</summary>
    public abstract DocumentCommitChangeKind Kind { get; }
}

/// <summary>A document creation or update (upstream <c>DocumentCommitChange</c> of kind <c>"change"</c>).</summary>
/// <param name="Record">The incarnation, as committed by this commit.</param>
/// <param name="ConversationId">Conversation owning the document; null only for Session documents.</param>
/// <param name="Version">Definition version of <paramref name="Value"/>.</param>
/// <param name="Value">Exact adopted immutable revision.</param>
/// <param name="Ops">Exact adopted operations for an ordinary update; empty for creation.</param>
public sealed record DocumentValueChange(DocumentRecord Record, ConversationId? ConversationId, int Version, JsonObjectValue Value,
    ImmutableArray<Op> Ops) : DocumentCommitChange(Record, ConversationId)
{
    /// <inheritdoc/>
    public override DocumentCommitChangeKind Kind => DocumentCommitChangeKind.Change;
}

/// <summary>A document retirement (upstream <c>DocumentCommitChange</c> of kind <c>"retire"</c>): this commit retired the
/// incarnation.</summary>
/// <param name="Record">The incarnation, as committed by this commit.</param>
/// <param name="ConversationId">Conversation owning the document; null only for Session documents.</param>
public sealed record DocumentRetireChange(DocumentRecord Record, ConversationId? ConversationId) : DocumentCommitChange(Record, ConversationId)
{
    /// <inheritdoc/>
    public override DocumentCommitChangeKind Kind => DocumentCommitChangeKind.Retire;
}

/// <summary>Definition-free child initialization by a fork (upstream <c>DocumentCommitChange</c> of type <c>"document.copy"</c>); consumers
/// hydrate through watch acquisition.</summary>
/// <param name="Record">The new child incarnation.</param>
/// <param name="ConversationId">The child conversation.</param>
/// <param name="Source">The exact stored source.</param>
public sealed record DocumentCopyCommitChange(DocumentRecord Record, ConversationId ConversationId, DocumentCopySource Source) : ICommitChange
{
    /// <inheritdoc/>
    public string Type => "document.copy";
}

/// <summary>
/// Every immutable change one Session storage transaction stored, with its <c>seq</c>; change order is unspecified (upstream
/// <c>CommitPublication</c>). A commit held in memory (<see cref="CommitPersistence"/>) is published once it is stored: by the next commit
/// that reads or writes its document, alone (a <c>seq</c> of its own), or with that commit, whose document change then carries the held
/// operations followed by its own.
/// </summary>
/// <param name="Seq">The commit sequence.</param>
/// <param name="Changes">The changes.</param>
public sealed record CommitPublication(Seq Seq, IReadOnlyList<ICommitChange> Changes);

/// <summary>
/// When a commit reaches Storage (upstream <c>CommitPersistence</c>). <see cref="Immediate"/>, the default: before the commit completes.
/// <see cref="Deferred"/>: when every write of the commit changes an existing conversation document with history <c>latest</c> and fork
/// <c>initial</c> (no creation, migration or retirement), the Session adopts it in memory, shows it to watches and views, and completes, but
/// stores it later: with the next commit that reads (<c>tx.DocAsync</c>) or writes that document, before the document leaves memory or
/// retires, at close, and at the latest about <see cref="WithinMs"/> after the commit (default
/// <see cref="ProgressSettings.PersistWithinMs"/>, 1,000 ms; <see cref="double.PositiveInfinity"/> waits for one of the others). A commit
/// that writes anything else is immediate. A crash loses what is held, so only state whose loss is acceptable, such as streamed progress,
/// should be deferred, and nothing durable may depend on it without reading it in a commit.
/// </summary>
/// <param name="Kind"><c>immediate</c> or <c>deferred</c>.</param>
/// <param name="WithinMs">For a deferred commit, how long it may stay in memory: a number of milliseconds, 0 or more; null for the default.
/// Anything else (NaN, a negative value) fails the commit with <see cref="ArgumentOutOfRangeException"/> before it is queued (upstream's
/// <c>TypeError</c>).</param>
public sealed record CommitPersistence(CommitPersistenceKind Kind, double? WithinMs = null)
{
    /// <summary><c>{kind: "immediate"}</c>.</summary>
    public static CommitPersistence Immediate { get; } = new(CommitPersistenceKind.Immediate);

    /// <summary><c>{kind: "deferred", withinMs}</c>.</summary>
    public static CommitPersistence Deferred(double? withinMs = null) => new(CommitPersistenceKind.Deferred, withinMs);
}

/// <summary>When a commit reaches Storage (upstream <c>CommitPersistence["kind"]</c>).</summary>
public enum CommitPersistenceKind
{
    /// <summary><c>immediate</c>: stored before the commit completes.</summary>
    Immediate,
    /// <summary><c>deferred</c>: held in memory when it may be (<see cref="CommitPersistence"/>).</summary>
    Deferred,
}

/// <summary>Options of one Session commit (upstream <c>CommitOptions</c>).</summary>
/// <param name="Persistence">When it reaches Storage; null: immediately.</param>
public sealed record CommitOptions(CommitPersistence? Persistence = null);

/// <summary>Why a Session ended, by <see cref="Kind"/>: <c>close()</c>, or a failed Storage call, whose error it carries (upstream
/// <c>SessionEnd</c>).</summary>
public sealed record SessionEnd
{
    private SessionEnd(SessionEndKind kind, Exception? error)
    {
        Kind = kind;
        Error = error;
    }

    /// <summary><c>{kind: "closed"}</c>.</summary>
    public static SessionEnd Closed { get; } = new(SessionEndKind.Closed, null);

    /// <summary><c>{kind: "failed", error}</c>.</summary>
    public static SessionEnd Failed(Exception error) => new(SessionEndKind.Failed, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary><see cref="SessionEndKind.Closed"/> or <see cref="SessionEndKind.Failed"/>.</summary>
    public SessionEndKind Kind { get; }

    /// <summary>What failed the Session; null when it closed.</summary>
    public Exception? Error { get; }
}

/// <summary>Why a Session ended (upstream <c>SessionEnd["kind"]</c>).</summary>
public enum SessionEndKind
{
    /// <summary><c>close()</c> closed it.</summary>
    Closed,
    /// <summary>A failed Storage call failed it, and it closed itself.</summary>
    Failed,
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

/// <summary>
/// Non-creating document watch acquisition shared by the Session and later invocation APIs (upstream <c>DocumentObserver</c>). A watch
/// (upstream <c>DocumentWatch</c>, Chord's <see cref="IWatchHandle{T}"/> of a value that is never null) follows one document address
/// (kind, scope and key) from the incarnation found at acquisition on, and acquisition returns null when the address is absent; cancelling
/// the acquisition context ends it <see cref="WatchEndKind.Aborted"/>. Its listener gets <see cref="WatchValueCall{T}"/>s, never a failed
/// call; commits made while the listener runs, or before <see cref="IWatchHandle{T}.Start"/>, arrive folded into one call. A retirement is
/// a call with <c>Retired</c> set, the last value the watch was given and no ops (a change made in the retiring commit itself is not
/// shown: that commit publishes the retirement only); the watch goes on, and a document created at the address later arrives as
/// <c>[["r", value]]</c>, folded with what is pending. It ends <see cref="WatchEndKind.Ended"/> when its scope's owner ends: a task
/// document's watch once the retirement call of the commit that makes its task terminal returned, and every watch at the Session's close
/// (a pending retirement dropped); <see cref="WatchEndKind.Failed"/> with the error that failed the Session. A watch of an address that
/// stays retired lives until it is stopped or the Session ends.
/// </summary>
public interface IDocumentObserver
{
    /// <summary>Watches a Session singleton.</summary>
    Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocToken token, DurableContext context);
    /// <summary>Watches a conversation singleton.</summary>
    Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context);
    /// <summary>Watches a task singleton.</summary>
    Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context);
    /// <summary>Watches a Session family member.</summary>
    Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context);
    /// <summary>Watches a conversation family member.</summary>
    Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context);
    /// <summary>Watches a task family member.</summary>
    Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context);
}

/// <summary>
/// A synchronous observer of committed Durable state, for adapters such as a Chord source (upstream <c>Observer&lt;T&gt;</c>):
/// <see cref="ISession.ObserveDocAsync(SessionDocToken, Observer{JsonObjectValue}, DurableContext)"/>,
/// <see cref="IHarness.ObserveConversationAsync"/> and <see cref="IHarness.ObserveTaskGraphAsync"/>, with the shapes of Chord's watch: the
/// advance callback gets a <see cref="WatchCall{T}"/>, the end callback a <see cref="WatchEnd"/>. Advance gets every change, unfolded, as
/// memory adopts it: for a stored commit at its publication once its Storage call settled, for a deferred one
/// (<see cref="CommitPersistence"/>) before it is stored. A view or the graph gets at most one call per commit, a document observer one per
/// change at its address (two for a commit that retires and recreates it); the task graph also advances outside any commit's publication,
/// on the Session line, when a registry change or a failed migration changes a <c>blocked</c> reason. Durable's failures are final, so
/// every call is a <see cref="WatchValueCall{T}"/>; a failure ends the observer. Calls may come before the attaching task completes; their
/// ops apply from <see cref="Observation{T}.Value"/>.
/// </summary>
/// <remarks>
/// <para>Advance must only record the call, synchronously and quickly: never block, wait, call the Session, or throw. A throw (or, for the
/// <see cref="Observer{T}(Func{WatchCall{T}, DurableContext, Task}, Action{WatchEnd})"/> form, a task that faults or is cancelled: upstream's
/// rejected promise) is reported as <see cref="ReportKind.ListenerError"/>, detaches the observer, and ends it
/// <see cref="WatchEndKind.ListenerError"/> with the error. End is called once: <see cref="WatchEndKind.Ended"/> when the observed scope's
/// owner ends (a task document's task, right after its retirement call), <see cref="WatchEndKind.Failed"/> with the error "session closed"
/// when the Session closes (an adapter follows the next Session) or with the error that failed the Session; never after
/// <see cref="Observation{T}.Detach"/>. A throwing end is reported.</para>
/// <para>PiSharp: the Session line is not one event loop, so a detach from another thread while a call runs on the line does not interrupt
/// that call; no call starts once the detach returned.</para>
/// </remarks>
public sealed class Observer<T>
{
    private readonly Action<WatchCall<T>, DurableContext>? advance;
    private readonly Func<WatchCall<T>, DurableContext, Task>? advanceAsync;
    private readonly Action<WatchEnd> end;

    /// <summary>An observer of synchronous callbacks.</summary>
    public Observer(Action<WatchCall<T>, DurableContext> advance, Action<WatchEnd> end)
    {
        ArgumentNullException.ThrowIfNull(advance);
        ArgumentNullException.ThrowIfNull(end);
        this.advance = advance;
        this.end = end;
    }

    /// <summary>An observer whose advance returns a task, as upstream's may return a promise: it still runs synchronously, the task is never
    /// awaited, and one that faults or is cancelled counts as a throw, after the fact.</summary>
    public Observer(Func<WatchCall<T>, DurableContext, Task> advance, Action<WatchEnd> end)
    {
        ArgumentNullException.ThrowIfNull(advance);
        ArgumentNullException.ThrowIfNull(end);
        advanceAsync = advance;
        this.end = end;
    }

    /// <summary>Runs the advance callback; the task of the task-returning form, null for the synchronous one.</summary>
    internal Task? Advance(WatchCall<T> call, DurableContext context)
    {
        if (advance is not null)
        {
            advance(call, context);
            return null;
        }
        return advanceAsync!(call, context);
    }

    /// <summary>Runs the end callback.</summary>
    internal void End(WatchEnd watchEnd) => end(watchEnd);
}

/// <summary>An attached <see cref="Observer{T}"/> (upstream <c>Observation&lt;T&gt;</c>): the value at attachment, which every later call starts
/// from, and how to detach it.</summary>
public sealed class Observation<T>
{
    private readonly IDetachable guarded;

    internal Observation(T value, IDetachable guarded)
    {
        Value = value;
        this.guarded = guarded;
    }

    /// <summary>The value at attachment.</summary>
    public T Value { get; }

    /// <summary>Detaches the observer. Idempotent: no advance or end follows it, not even for the rest of a commit underway.</summary>
    public void Detach() => guarded.Detach();
}

/// <summary>Internal: what an <see cref="Observation{T}"/> detaches (upstream <c>{ detach(): void }</c>).</summary>
internal interface IDetachable
{
    void Detach();
}

/// <summary>
/// A synchronous observer of stored commits (upstream <c>CommitObserver</c>, <see cref="ISession.ObserveCommits"/>): the advance callback
/// gets each <see cref="CommitPublication"/>, the end callback the Session's close as a <see cref="WatchEnd"/>.
/// </summary>
/// <remarks>
/// Advance runs synchronously at the commit's publication, on the Session line, and must not block or call Session APIs. A throw (or, for
/// the <see cref="CommitObserver(Func{CommitPublication, DurableContext, Task}, Action{WatchEnd})"/> form, a task that faults or is
/// cancelled: upstream's rejected promise) is reported as <see cref="ReportKind.ListenerError"/>, detaches the observer, and ends it
/// <see cref="WatchEndKind.ListenerError"/> with the error, where a throwing listener used to stay subscribed. End is called once:
/// <see cref="WatchEndKind.Failed"/> with the error "session closed" when the Session closes, after its last publication (a commit observer
/// is a synchronous observer, so an adapter follows the next Session), or with the error that failed the Session; never after
/// <see cref="CommitObservation.Detach"/>. A throwing end is reported.
/// </remarks>
public sealed class CommitObserver
{
    private readonly Action<CommitPublication, DurableContext>? advance;
    private readonly Func<CommitPublication, DurableContext, Task>? advanceAsync;
    private readonly Action<WatchEnd> end;

    /// <summary>An observer of synchronous callbacks.</summary>
    public CommitObserver(Action<CommitPublication, DurableContext> advance, Action<WatchEnd> end)
    {
        ArgumentNullException.ThrowIfNull(advance);
        ArgumentNullException.ThrowIfNull(end);
        this.advance = advance;
        this.end = end;
    }

    /// <summary>An observer whose advance returns a task, as upstream's may return a promise: it still runs synchronously, the task is never
    /// awaited, and one that faults or is cancelled counts as a throw, after the fact.</summary>
    public CommitObserver(Func<CommitPublication, DurableContext, Task> advance, Action<WatchEnd> end)
    {
        ArgumentNullException.ThrowIfNull(advance);
        ArgumentNullException.ThrowIfNull(end);
        advanceAsync = advance;
        this.end = end;
    }

    /// <summary>Runs the advance callback; the task of the task-returning form, null for the synchronous one.</summary>
    internal Task? Advance(CommitPublication publication, DurableContext context)
    {
        if (advance is not null)
        {
            advance(publication, context);
            return null;
        }
        return advanceAsync!(publication, context);
    }

    /// <summary>Runs the end callback.</summary>
    internal void End(WatchEnd watchEnd) => end(watchEnd);
}

/// <summary>An attached <see cref="CommitObserver"/> (upstream <c>CommitObservation</c>): how to detach it. It has no value: commits are
/// events, not a state, and the Session does not know Storage's newest <c>seq</c> before its own first commit.</summary>
public sealed class CommitObservation
{
    private readonly IDetachable guarded;

    internal CommitObservation(IDetachable guarded) => this.guarded = guarded;

    /// <summary>Detaches the observer. Idempotent: no advance or end follows it.</summary>
    public void Detach() => guarded.Detach();
}

/// <summary>
/// Owner of one mutation line, its records and its tracked documents (upstream <c>Session</c>). Every commit runs on one FIFO line;
/// only committed state is observable.
/// </summary>
public interface ISession : IDocumentObserver, IDocumentReader
{
    /// <summary>
    /// Attaches a synchronous <see cref="Observer{T}"/> to a Session singleton's address, atomically on the Session line, as
    /// <see cref="IDocumentObserver.WatchDocAsync(SessionDocToken, DurableContext)"/> acquires a watch: the observation's value includes
    /// every commit before it, and the observer gets every later one, retirements and later incarnations included. Null when the address
    /// is absent. <paramref name="context"/> governs only the attachment: cancelled while attaching, the task faults with the cancellation
    /// error and nothing stays attached. A closed Session or an invalid argument faults the task; the call never throws.
    /// </summary>
    Task<Observation<JsonObjectValue>?> ObserveDocAsync(SessionDocToken token, Observer<JsonObjectValue> observer, DurableContext context);
    /// <summary>Observes a conversation singleton's address.</summary>
    Task<Observation<JsonObjectValue>?> ObserveDocAsync(ConversationDocToken token, ConversationId conversationId, Observer<JsonObjectValue> observer,
        DurableContext context);
    /// <summary>Observes a task singleton's address.</summary>
    Task<Observation<JsonObjectValue>?> ObserveDocAsync(TaskDocToken token, TaskId taskId, Observer<JsonObjectValue> observer, DurableContext context);
    /// <summary>Observes a Session family member's address.</summary>
    Task<Observation<JsonObjectValue>?> ObserveDocAsync(SessionDocFamilyToken token, string key, Observer<JsonObjectValue> observer,
        DurableContext context);
    /// <summary>Observes a conversation family member's address.</summary>
    Task<Observation<JsonObjectValue>?> ObserveDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        Observer<JsonObjectValue> observer, DurableContext context);
    /// <summary>Observes a task family member's address.</summary>
    Task<Observation<JsonObjectValue>?> ObserveDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, Observer<JsonObjectValue> observer,
        DurableContext context);

    /// <summary>Runs one atomic transaction on the Session mutation line and returns the callback's result. Inside the callback read only
    /// through the transaction: <c>SnapshotAsync</c>, <c>WatchDocAsync</c> and the other Session and runtime reads may queue on the line the
    /// callback holds, and then never complete. <paramref name="options"/> may defer storing it (<see cref="CommitPersistence"/>).</summary>
    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context, CommitOptions? options = null);
    /// <summary>Runs one atomic asynchronous transaction on the Session mutation line.</summary>
    Task CommitAsync(Func<ITx, Task> change, DurableContext context, CommitOptions? options = null);
    /// <summary>Runs one atomic synchronous transaction on the Session mutation line and returns the callback's result.</summary>
    Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context, CommitOptions? options = null);
    /// <summary>Runs one atomic synchronous transaction on the Session mutation line.</summary>
    Task CommitAsync(Action<ITx> change, DurableContext context, CommitOptions? options = null);

    /// <summary>Seals admission, settles admitted commits, then closes storage. Cancelling the wait does not reopen admission.</summary>
    Task CloseAsync(DurableContext context);

    /// <summary>
    /// Settles once the Session has closed, Storage included: after <see cref="CloseAsync"/>, or after the first failed Storage call, which
    /// closes the Session itself. A failed Session is reopened from Storage; nothing else is left to clean up. Never faults.
    /// </summary>
    Task<SessionEnd> Closed { get; }

    /// <summary>
    /// Attaches a synchronous <see cref="CommitObserver"/> of stored commits (upstream <c>observeCommits</c>), each with exactly what it
    /// stored, except that a document the commit retires is published as its retirement only; a commit held in memory is published when it
    /// is stored (<see cref="CommitPublication"/>). Advance runs at the commit's publication, on the Session line, before every wait the
    /// commit resolves and every abort it queues, in no order with watch listeners; it must not block or call Session APIs. A throw, or a
    /// faulted task it returns, is reported (<c>HarnessOptions.OnReport</c>), detaches the observer and ends it
    /// <see cref="WatchEndKind.ListenerError"/>; the other observers still run. End comes once, at the Session's close after its last
    /// publication or at its failure (<see cref="CommitObserver"/>), never after <see cref="CommitObservation.Detach"/>. Attaches
    /// synchronously; throws on a closed Session.
    /// </summary>
    CommitObservation ObserveCommits(CommitObserver observer);

    /// <summary>Observes close synchronously when it begins, also when a failed Storage call closes the Session. The listener must not
    /// block or call Session APIs; a throw is reported and the other listeners still run.</summary>
    IDisposable SubscribeClose(Action listener);

    /// <summary>An asynchronous close listener: a faulted task it returns is reported without being awaited.</summary>
    IDisposable SubscribeClose(Func<Task> listener);
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
    /// <summary>ID of the newest entry visible from the conversation; null when it has none (Pi v1.5.0's <c>tx.latestEntryId</c>).</summary>
    Task<EntryId?> LatestEntryIdAsync(ConversationId conversationId);
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
    /// <summary>Settles a queued or placed submission; only a placed input or continue can be answered and a settled submission stays unchanged.
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
