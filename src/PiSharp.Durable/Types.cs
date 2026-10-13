// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/types.ts (table records: conversations, entries, submissions,
// tasks). The rest of types.ts is split across Ids.cs (IDs and sequences), TypesDocuments.cs (document records and content) and
// TypesStorage.cs (scans, writes and the Storage contract); the Session, Tx and task-runtime surfaces join with P3 and P7.
//
// Records hold JSON as immutable JsonValue trees, so storage needs none of upstream's structuredClone/freeze detachment: a value cannot
// change after it is handed over. Each record has ToJson/FromJson with a fixed key order (see each type) and JavaScript number and
// string formatting; FromJson enforces the discriminated shapes that upstream's types enforce at compile time. Record equality is
// JavaScript deep equality of that JSON (vitest's toEqual).
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>Base of the closed record unions: equality is deep JSON equality of <see cref="ToJson"/>.</summary>
public abstract class DurableValue : IEquatable<DurableValue>
{
    private protected DurableValue() { }

    /// <summary>The persisted JSON.</summary>
    public abstract JsonValue ToJson();

    /// <inheritdoc/>
    public bool Equals(DurableValue? other) =>
        other is not null && other.GetType() == GetType() && JsonValue.DeepEquals(ToJson(), other.ToJson());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DurableValue);

    /// <inheritdoc/>
    public override int GetHashCode() => GetType().GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>The fork source of a conversation: inherited history runs through <see cref="At"/>, inclusive.</summary>
public sealed record ConversationParent(ConversationId ConversationId, EntryId At)
{
    /// <summary><c>{conversationId, at}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("conversationId", ConversationId.ToJson()).Add("at", At.ToJson()).Build();

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static ConversationParent FromJson(JsonValue json)
    {
        const string what = "conversation parent";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "conversationId", "at");
        return new(RecordJson.Id<ConversationId>(value, "conversationId", what), RecordJson.Id<EntryId>(value, "at", what));
    }
}

/// <summary>The creator edge of a conversation, used for attribution, subtree abort and subtree idle waits.</summary>
public sealed record ConversationOwner(ConversationId ConversationId, TaskId TaskId)
{
    /// <summary><c>{conversationId, taskId}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("conversationId", ConversationId.ToJson()).Add("taskId", TaskId.ToJson()).Build();

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static ConversationOwner FromJson(JsonValue json)
    {
        const string what = "conversation owner";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "conversationId", "taskId");
        return new(RecordJson.Id<ConversationId>(value, "conversationId", what), RecordJson.Id<TaskId>(value, "taskId", what));
    }
}

/// <summary>Immutable identity, history ancestry and task ownership of a transcript scope (upstream <c>ConversationRecord</c>).
/// JSON key order: <c>id, parent, owner</c>, as the Session builds it.</summary>
public sealed record ConversationRecord(ConversationId Id, ConversationParent? Parent = null, ConversationOwner? Owner = null)
{
    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("id", Id.ToJson()).Add("parent", Parent?.ToJson()).Add("owner", Owner?.ToJson()).Build();

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static ConversationRecord FromJson(JsonValue json)
    {
        const string what = "conversation record";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "id", "parent", "owner");
        return new(RecordJson.Id<ConversationId>(value, "id", what),
            RecordJson.Optional(value, "parent") is { } parent ? ConversationParent.FromJson(parent) : null,
            RecordJson.Optional(value, "owner") is { } owner ? ConversationOwner.FromJson(owner) : null);
    }
}

/// <summary>Ownership selected explicitly whenever a conversation is created: ownerless, or owned by a task.</summary>
public sealed record ConversationOwnership
{
    private ConversationOwnership(TaskId? taskId) { TaskId = taskId; }

    /// <summary>An ownerless conversation.</summary>
    public static ConversationOwnership Ownerless { get; } = new((TaskId?)null);

    /// <summary>A conversation owned by <paramref name="taskId"/>.</summary>
    public static ConversationOwnership Task(TaskId taskId) => new(taskId);

    /// <summary>The owning task, or null when ownerless.</summary>
    public TaskId? TaskId { get; }
}

/// <summary>Who owns a task: its conversation (a top-level task) or another task of the same conversation (a child task).</summary>
public sealed record TaskOwnership
{
    private TaskOwnership(TaskId? taskId) { TaskId = taskId; }

    /// <summary>A task its conversation owns.</summary>
    public static TaskOwnership Conversation { get; } = new((TaskId?)null);

    /// <summary>A child task of <paramref name="taskId"/>.</summary>
    public static TaskOwnership Task(TaskId taskId) => new(taskId);

    /// <summary>The owning task, or null for a conversation-owned task.</summary>
    public TaskId? TaskId { get; }
}

/// <summary>Creation options for a durable task (upstream <c>TaskOptions</c>).</summary>
public sealed record TaskOptions(TaskOwnership Ownership)
{
    /// <summary>Default: the owner task's conversation, or the transaction's bound conversation.</summary>
    public ConversationId? ConversationId { get; init; }
    /// <summary>Conversation-owned tasks only: excluded from ordinary idle waits, conversation aborts and cascades.</summary>
    public bool? Background { get; init; }
}

/// <summary>Whether a context edit omits or replaces its target's model messages.</summary>
public enum ContextEditAction
{
    /// <summary><c>"omit"</c>.</summary>
    Omit,
    /// <summary><c>"replace"</c>.</summary>
    Replace,
}

/// <summary>An immutable override of one visible entry's contribution to model context. JSON key order: <c>target, action, messages</c>.</summary>
public sealed class ContextEdit : DurableValue
{
    private ContextEdit(EntryId target, ContextEditAction action, JsonArrayValue? messages)
    {
        Target = target; Action = action; Messages = messages;
    }

    /// <summary>Omits the target's model messages.</summary>
    public static ContextEdit Omit(EntryId target) => new(target, ContextEditAction.Omit, null);

    /// <summary>Contributes <paramref name="messages"/> (wire messages) instead of the target's model messages.</summary>
    public static ContextEdit Replace(EntryId target, JsonArrayValue messages) =>
        new(target, ContextEditAction.Replace, messages ?? throw new ArgumentNullException(nameof(messages)));

    /// <summary>The entry whose model messages are omitted or replaced.</summary>
    public EntryId Target { get; }
    /// <summary>The action.</summary>
    public ContextEditAction Action { get; }
    /// <summary>The replacement wire messages; null for <see cref="ContextEditAction.Omit"/>.</summary>
    public JsonArrayValue? Messages { get; }

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("target", Target.ToJson()).Add("action", WireName.Of(Action)).Add("messages", Messages).Build();

    /// <summary>Decodes <see cref="ToJson"/>; a replacement requires messages and an omission carries none.</summary>
    public static ContextEdit FromJson(JsonValue json)
    {
        const string what = "context edit";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "target", "action", "messages");
        var target = RecordJson.Id<EntryId>(value, "target", what);
        if (RecordJson.Enum<ContextEditAction>(value, "action", what) == ContextEditAction.Omit)
        {
            RecordJson.Absent(value, "messages", what);
            return Omit(target);
        }
        return Replace(target, RecordJson.Array(RecordJson.Required(value, "messages", what), "messages", what));
    }
}

/// <summary>An immutable transcript event with separate model-facing and application-facing payloads (upstream <c>EntryRecord</c>).
/// JSON key order is upstream's: the Session builds a record as <c>{ ...draft (without head), id, conversationId, head, byTaskId }</c>, so the
/// draft's fields come first in the draft's own order (an untyped draft literal is <c>kind, model, data, edits</c>; a typed append
/// <c>{ ...draft, kind }</c> puts <c>kind</c> last). <see cref="KeyOrder"/> carries that order from the appending path, or from the decoded
/// JSON, so a record read back writes the same text.</summary>
public sealed record EntryRecord
{
    /// <summary>The entry ID.</summary>
    public required EntryId Id { get; init; }
    /// <summary>The conversation that appended it.</summary>
    public required ConversationId ConversationId { get; init; }
    /// <summary>Application-defined entry discriminator.</summary>
    public required string Kind { get; init; }
    /// <summary>Wire messages contributed to model context; absent for display or bookkeeping entries.</summary>
    public JsonArrayValue? Model { get; init; }
    /// <summary>JSON payload consumed by views, extensions or bookkeeping logic (JSON <c>null</c> is <see cref="JsonValue.Null"/>).</summary>
    public JsonValue? Data { get; init; }
    /// <summary>First entry in the active context selected by this entry.</summary>
    public EntryId? Head { get; init; }
    /// <summary>Context-only overrides of earlier visible entries.</summary>
    public ImmutableArray<ContextEdit>? Edits { get; init; }
    /// <summary>Task that appended this entry, when durable work produced it.</summary>
    public TaskId? ByTaskId { get; init; }
    /// <summary>The JSON key order of the record's creation (see <see cref="RecordJson.Ordered"/>); default: an untyped draft's.</summary>
    internal ImmutableArray<string> KeyOrder { get; init; }

    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() => RecordJson.Ordered(new RecordJson.Writer()
        .Add("kind", Kind).Add("model", Model).Add("data", Data)
        .Add("edits", Edits is { } edits ? JsonValue.Array(edits.Select(edit => edit.ToJson())) : null)
        .Add("id", Id.ToJson()).Add("conversationId", ConversationId.ToJson()).Add("head", Head?.ToJson())
        .Add("byTaskId", ByTaskId?.ToJson()).Build(), KeyOrder);

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static EntryRecord FromJson(JsonValue json)
    {
        const string what = "entry record";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "id", "conversationId", "kind", "model", "data", "head", "edits", "byTaskId");
        return new()
        {
            Id = RecordJson.Id<EntryId>(value, "id", what),
            ConversationId = RecordJson.Id<ConversationId>(value, "conversationId", what),
            Kind = RecordJson.String(value, "kind", what),
            Model = RecordJson.Optional(value, "model") is { } model ? RecordJson.Array(model, "model", what) : null,
            Data = RecordJson.Optional(value, "data"),
            Head = RecordJson.OptionalId<EntryId>(value, "head", what),
            Edits = RecordJson.Optional(value, "edits") is { } edits ? [.. RecordJson.Array(edits, "edits", what).Select(ContextEdit.FromJson)] : null,
            ByTaskId = RecordJson.OptionalId<TaskId>(value, "byTaskId", what),
            KeyOrder = RecordJson.KeysOf(value),
        };
    }

    /// <inheritdoc/>
    public bool Equals(EntryRecord? other) => other is not null && JsonValue.DeepEquals(ToJson(), other.ToJson());
    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode();
    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>Whether a submission is user input or a passive entry write.</summary>
public enum SubmissionType
{
    /// <summary><c>"input"</c>.</summary>
    Input,
    /// <summary><c>"write"</c>.</summary>
    Write,
}

/// <summary>The lifecycle status of a submission.</summary>
public enum SubmissionStatus
{
    /// <summary>Admitted but not yet represented in the transcript.</summary>
    Queued,
    /// <summary>An input added to the transcript and owned by an active run.</summary>
    Placed,
    /// <summary>An answered input or an appended passive write.</summary>
    Done,
    /// <summary>Terminal: the submission can no longer be answered or placed.</summary>
    Unanswered,
}

/// <summary>The fields of a submission (upstream <c>SubmissionCreate</c> has these, <see cref="SubmissionRecord"/> adds the ID).</summary>
public interface ISubmissionFields
{
    /// <summary>The conversation.</summary>
    ConversationId ConversationId { get; }
    /// <summary>Host-provided deduplication key, scoped to the conversation.</summary>
    string? RequestId { get; }
    /// <summary>Input or passive write.</summary>
    SubmissionType Type { get; }
    /// <summary>The lifecycle status.</summary>
    SubmissionStatus Status { get; }
    /// <summary>The transcript entry; required when placed or done, optional for an unanswered input, otherwise absent.</summary>
    EntryId? Entry { get; }
    /// <summary>The answer entry of a done input; otherwise absent.</summary>
    EntryId? Answer { get; }
    /// <summary>Why an unanswered submission was not answered; otherwise absent.</summary>
    string? Reason { get; }
    /// <summary>Optional diagnostic data of an unanswered submission; otherwise absent.</summary>
    JsonValue? Detail { get; }
}

/// <summary>Durable lifecycle of one admitted user input or passive entry write (upstream <c>SubmissionRecord</c>). Which fields a
/// status allows follows upstream's union exactly; <see cref="Validate"/> and <see cref="FromJson"/> enforce it. JSON key order is
/// upstream's: the Session creates <c>{ ...create, id }</c> (<c>conversationId, requestId, type, status, entry, answer, reason, detail</c>,
/// each when present, then <c>id</c>), and a placement or settlement spreads the current record, so a field it adds (a queued input's
/// <c>entry</c>, an <c>answer</c>, a <c>reason</c>) follows <c>id</c>. <see cref="KeyOrder"/> carries the creation order, or the decoded
/// JSON's, through <c>with</c>.</summary>
public sealed record SubmissionRecord : ISubmissionFields
{
    /// <summary>The submission ID.</summary>
    public required SubmissionId Id { get; init; }
    /// <inheritdoc/>
    public required ConversationId ConversationId { get; init; }
    /// <inheritdoc/>
    public string? RequestId { get; init; }
    /// <inheritdoc/>
    public required SubmissionType Type { get; init; }
    /// <inheritdoc/>
    public required SubmissionStatus Status { get; init; }
    /// <inheritdoc/>
    public EntryId? Entry { get; init; }
    /// <inheritdoc/>
    public EntryId? Answer { get; init; }
    /// <inheritdoc/>
    public string? Reason { get; init; }
    /// <inheritdoc/>
    public JsonValue? Detail { get; init; }

    /// <summary>The JSON key order of the record's creation (see <see cref="RecordJson.Ordered"/>); default: the create's fields, then <c>id</c>.</summary>
    internal ImmutableArray<string> KeyOrder { get; init; }

    /// <summary>Throws when the fields do not form one of upstream's submission shapes.</summary>
    public SubmissionRecord Validate() { SubmissionShape.Validate(this, "submission record"); return this; }

    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() =>
        RecordJson.Ordered(SubmissionShape.Write(new RecordJson.Writer(), this).Add("id", Id.ToJson()).Build(), KeyOrder);

    /// <summary>Decodes and validates <see cref="ToJson"/>.</summary>
    public static SubmissionRecord FromJson(JsonValue json)
    {
        const string what = "submission record";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "id", "conversationId", "requestId", "type", "status", "entry", "answer", "reason", "detail");
        var create = SubmissionShape.Read(value, what);
        return (create.WithId(RecordJson.Id<SubmissionId>(value, "id", what)) with { KeyOrder = RecordJson.KeysOf(value) }).Validate();
    }

    /// <inheritdoc/>
    public bool Equals(SubmissionRecord? other) => other is not null && JsonValue.DeepEquals(ToJson(), other.ToJson());
    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode();
    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>Submission fields supplied before the Session assigns an ID (upstream <c>SubmissionCreate</c>).</summary>
public sealed record SubmissionCreate : ISubmissionFields
{
    /// <inheritdoc/>
    public required ConversationId ConversationId { get; init; }
    /// <inheritdoc/>
    public string? RequestId { get; init; }
    /// <inheritdoc/>
    public required SubmissionType Type { get; init; }
    /// <inheritdoc/>
    public required SubmissionStatus Status { get; init; }
    /// <inheritdoc/>
    public EntryId? Entry { get; init; }
    /// <inheritdoc/>
    public EntryId? Answer { get; init; }
    /// <inheritdoc/>
    public string? Reason { get; init; }
    /// <inheritdoc/>
    public JsonValue? Detail { get; init; }

    /// <summary>Throws when the fields do not form one of upstream's submission shapes.</summary>
    public SubmissionCreate Validate() { SubmissionShape.Validate(this, "submission create"); return this; }

    /// <summary>The record with <paramref name="id"/>: upstream's <c>{ ...create, id }</c>, whose later fields follow <c>id</c>.</summary>
    public SubmissionRecord WithId(SubmissionId id) => new()
    {
        Id = id, ConversationId = ConversationId, RequestId = RequestId, Type = Type, Status = Status, Entry = Entry, Answer = Answer,
        Reason = Reason, Detail = Detail, KeyOrder = [.. ToJson().Keys, "id"],
    };

    /// <summary>The JSON, without an ID.</summary>
    public JsonObjectValue ToJson() => SubmissionShape.Write(new RecordJson.Writer(), this).Build();

    /// <summary>Decodes and validates <see cref="ToJson"/>; an <c>id</c> is rejected because the Session assigns it.</summary>
    public static SubmissionCreate FromJson(JsonValue json)
    {
        const string what = "submission create";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "conversationId", "requestId", "type", "status", "entry", "answer", "reason", "detail");
        return SubmissionShape.Read(value, what).Validate();
    }

    /// <inheritdoc/>
    public bool Equals(SubmissionCreate? other) => other is not null && JsonValue.DeepEquals(ToJson(), other.ToJson());
    /// <inheritdoc/>
    public override int GetHashCode() => ConversationId.GetHashCode();
}

/// <summary>Terminal status staged for a submission; identity, type and entry come from its current record.</summary>
public sealed class SubmissionSettlement : DurableValue
{
    private SubmissionSettlement(SubmissionStatus status, EntryId? answer, string? reason, JsonValue? detail)
    {
        Status = status; Answer = answer; Reason = reason; Detail = detail;
    }

    /// <summary>An answered input.</summary>
    public static SubmissionSettlement Done(EntryId answer) => new(SubmissionStatus.Done, answer, null, null);

    /// <summary>A submission that can no longer be answered.</summary>
    public static SubmissionSettlement Unanswered(string reason, JsonValue? detail = null) =>
        new(SubmissionStatus.Unanswered, null, reason ?? throw new ArgumentNullException(nameof(reason)), detail);

    /// <summary><see cref="SubmissionStatus.Done"/> or <see cref="SubmissionStatus.Unanswered"/>.</summary>
    public SubmissionStatus Status { get; }
    /// <summary>The answer of a done settlement.</summary>
    public EntryId? Answer { get; }
    /// <summary>The reason of an unanswered settlement.</summary>
    public string? Reason { get; }
    /// <summary>Optional detail of an unanswered settlement.</summary>
    public JsonValue? Detail { get; }

    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", WireName.Of(Status)).Add("answer", Answer?.ToJson())
        .Add("reason", Reason).Add("detail", Detail).Build();
}

internal static class SubmissionShape
{
    public static RecordJson.Writer Write(RecordJson.Writer writer, ISubmissionFields value) => writer
        .Add("conversationId", value.ConversationId.ToJson()).Add("requestId", value.RequestId).Add("type", WireName.Of(value.Type))
        .Add("status", WireName.Of(value.Status)).Add("entry", value.Entry?.ToJson()).Add("answer", value.Answer?.ToJson())
        .Add("reason", value.Reason).Add("detail", value.Detail);

    public static SubmissionCreate Read(JsonObjectValue value, string what) => new()
    {
        ConversationId = RecordJson.Id<ConversationId>(value, "conversationId", what),
        RequestId = RecordJson.OptionalString(value, "requestId", what),
        Type = RecordJson.Enum<SubmissionType>(value, "type", what),
        Status = RecordJson.Enum<SubmissionStatus>(value, "status", what),
        Entry = RecordJson.OptionalId<EntryId>(value, "entry", what),
        Answer = RecordJson.OptionalId<EntryId>(value, "answer", what),
        Reason = RecordJson.OptionalString(value, "reason", what),
        Detail = RecordJson.Optional(value, "detail"),
    };

    /// <summary>Upstream's union: which of entry/answer/reason/detail each type and status requires, allows or forbids.</summary>
    public static void Validate(ISubmissionFields value, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (entry, answer, reason, detail) = (value.Type, value.Status) switch
        {
            (SubmissionType.Input, SubmissionStatus.Queued) => (Field.Forbidden, Field.Forbidden, Field.Forbidden, Field.Forbidden),
            (SubmissionType.Input, SubmissionStatus.Placed) => (Field.Required, Field.Forbidden, Field.Forbidden, Field.Forbidden),
            (SubmissionType.Input, SubmissionStatus.Done) => (Field.Required, Field.Required, Field.Forbidden, Field.Forbidden),
            (SubmissionType.Input, SubmissionStatus.Unanswered) => (Field.Optional, Field.Forbidden, Field.Required, Field.Optional),
            (SubmissionType.Write, SubmissionStatus.Queued) => (Field.Forbidden, Field.Forbidden, Field.Forbidden, Field.Forbidden),
            (SubmissionType.Write, SubmissionStatus.Done) => (Field.Required, Field.Forbidden, Field.Forbidden, Field.Forbidden),
            (SubmissionType.Write, SubmissionStatus.Unanswered) => (Field.Forbidden, Field.Forbidden, Field.Required, Field.Optional),
            _ => throw RecordJson.Invalid(what, $"a {WireName.Of(value.Type)} submission cannot be {WireName.Of(value.Status)}"),
        };
        Check(entry, value.Entry is not null, "entry");
        Check(answer, value.Answer is not null, "answer");
        Check(reason, value.Reason is not null, "reason");
        Check(detail, value.Detail is not null, "detail");

        void Check(Field rule, bool present, string field)
        {
            if (rule == Field.Required && !present) throw RecordJson.Invalid(what, $"a {Describe()} submission requires \"{field}\"");
            if (rule == Field.Forbidden && present) throw RecordJson.Invalid(what, $"a {Describe()} submission cannot carry \"{field}\"");
        }

        string Describe() => WireName.Of(value.Status) + " " + WireName.Of(value.Type);
    }

    private enum Field { Forbidden, Optional, Required }
}

/// <summary>JSON-safe error snapshot persisted instead of a runtime exception. JSON key order: <c>message, detail</c>.</summary>
public sealed record TaskOutcomeError(string Message, JsonValue? Detail = null)
{
    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("message", Message).Add("detail", Detail).Build();

    /// <summary>Decodes <see cref="ToJson"/>.</summary>
    public static TaskOutcomeError FromJson(JsonValue json)
    {
        const string what = "task outcome error";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "message", "detail");
        return new(RecordJson.String(value, "message", what), RecordJson.Optional(value, "detail"));
    }

    /// <inheritdoc/>
    public bool Equals(TaskOutcomeError? other) => other is not null && JsonValue.DeepEquals(ToJson(), other.ToJson());
    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Message);
}

/// <summary>Why a task became terminal.</summary>
public enum TaskOutcomeStatus
{
    /// <summary>Completed with a result.</summary>
    Completed,
    /// <summary>An expected failure its implementation committed.</summary>
    Failed,
    /// <summary>Explicit cancellation handled by the task's abort protocol.</summary>
    Aborted,
    /// <summary>Cannot resume because its definition or migration is unavailable.</summary>
    Orphaned,
    /// <summary>A runtime-detected contract failure, such as an uncaught throw or no durable progress.</summary>
    Faulted,
}

/// <summary>Durable reason and optional result recorded when a task becomes terminal (upstream <c>TaskOutcome</c>). JSON key order:
/// <c>status</c> then the variant's fields in declaration order.</summary>
public abstract class TaskOutcome : DurableValue
{
    private protected TaskOutcome() { }

    /// <summary>The outcome status.</summary>
    public abstract TaskOutcomeStatus Status { get; }
    /// <summary>The result, when the variant carries one.</summary>
    public virtual JsonValue? Result => null;

    /// <summary>Decodes <see cref="DurableValue.ToJson"/>.</summary>
    public static TaskOutcome FromJson(JsonValue json)
    {
        const string what = "task outcome";
        var value = RecordJson.Object(json, what);
        switch (RecordJson.Enum<TaskOutcomeStatus>(value, "status", what))
        {
            case TaskOutcomeStatus.Completed:
                RecordJson.OnlyKeys(value, what, "status", "result");
                // A task whose result is undefined persists none (JSON.stringify drops it).
                return new CompletedTaskOutcome(RecordJson.Optional(value, "result"));
            case TaskOutcomeStatus.Failed:
                RecordJson.OnlyKeys(value, what, "status", "error", "result");
                return new FailedTaskOutcome(TaskOutcomeError.FromJson(RecordJson.Required(value, "error", what)), RecordJson.Optional(value, "result"));
            case TaskOutcomeStatus.Aborted:
                RecordJson.OnlyKeys(value, what, "status", "reason", "result");
                return new AbortedTaskOutcome(RecordJson.OptionalString(value, "reason", what), RecordJson.Optional(value, "result"));
            case TaskOutcomeStatus.Orphaned:
                RecordJson.OnlyKeys(value, what, "status", "reason");
                return new OrphanedTaskOutcome(RecordJson.String(value, "reason", what));
            default:
                RecordJson.OnlyKeys(value, what, "status", "error");
                return new FaultedTaskOutcome(TaskOutcomeError.FromJson(RecordJson.Required(value, "error", what)));
        }
    }
}

/// <summary><c>{status: "completed", result}</c>.</summary>
public sealed class CompletedTaskOutcome(JsonValue? result) : TaskOutcome
{
    /// <inheritdoc/>
    public override TaskOutcomeStatus Status => TaskOutcomeStatus.Completed;
    /// <inheritdoc/>
    public override JsonValue? Result { get; } = result;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", "completed").Add("result", Result).Build();
}

/// <summary><c>{status: "failed", error, result?}</c>.</summary>
public sealed class FailedTaskOutcome(TaskOutcomeError error, JsonValue? result = null) : TaskOutcome
{
    /// <inheritdoc/>
    public override TaskOutcomeStatus Status => TaskOutcomeStatus.Failed;
    /// <summary>The error.</summary>
    public TaskOutcomeError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
    /// <inheritdoc/>
    public override JsonValue? Result { get; } = result;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("status", "failed").Add("error", Error.ToJson()).Add("result", Result).Build();
}

/// <summary><c>{status: "aborted", reason?, result?}</c>.</summary>
public sealed class AbortedTaskOutcome(string? reason = null, JsonValue? result = null) : TaskOutcome
{
    /// <inheritdoc/>
    public override TaskOutcomeStatus Status => TaskOutcomeStatus.Aborted;
    /// <summary>The reason.</summary>
    public string? Reason { get; } = reason;
    /// <inheritdoc/>
    public override JsonValue? Result { get; } = result;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", "aborted").Add("reason", Reason).Add("result", Result).Build();
}

/// <summary><c>{status: "orphaned", reason}</c>.</summary>
public sealed class OrphanedTaskOutcome(string reason) : TaskOutcome
{
    /// <inheritdoc/>
    public override TaskOutcomeStatus Status => TaskOutcomeStatus.Orphaned;
    /// <summary>The reason.</summary>
    public string Reason { get; } = reason ?? throw new ArgumentNullException(nameof(reason));
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", "orphaned").Add("reason", Reason).Build();
}

/// <summary><c>{status: "faulted", error}</c>.</summary>
public sealed class FaultedTaskOutcome(TaskOutcomeError error) : TaskOutcome
{
    /// <inheritdoc/>
    public override TaskOutcomeStatus Status => TaskOutcomeStatus.Faulted;
    /// <summary>The error.</summary>
    public TaskOutcomeError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", "faulted").Add("error", Error.ToJson()).Build();
}

/// <summary>The status of a task record (upstream <c>TaskState["status"]</c>; named to avoid <see cref="System.Threading.Tasks.TaskStatus"/>).</summary>
public enum DurableTaskStatus
{
    /// <summary>Eligible for scheduling.</summary>
    Pending,
    /// <summary>Reserved by one in-memory task invocation.</summary>
    Running,
    /// <summary>Parked until every task it waits on is terminal.</summary>
    Waiting,
    /// <summary>Outcome decided; terminal once no ordinary owned work below is live.</summary>
    Completing,
    /// <summary>Permanently settled.</summary>
    Terminal,
}

/// <summary>How a waiting task treats the tasks it waits on (spec §5.5).</summary>
public enum JoinPolicy
{
    /// <summary><c>"failFast"</c>.</summary>
    FailFast,
    /// <summary><c>"allSettled"</c>.</summary>
    AllSettled,
}

/// <summary>Complete durable execution state of a task (upstream <c>TaskState</c>): a live state carries a checkpoint, a settling state
/// an outcome. JSON key order: <c>status, checkpoint, on, policy</c> or <c>status, outcome</c>.</summary>
public abstract class TaskState : DurableValue
{
    private protected TaskState() { }

    /// <summary>The status.</summary>
    public abstract DurableTaskStatus Status { get; }

    /// <summary>Decodes <see cref="DurableValue.ToJson"/>; a live state cannot carry an outcome and a settling one no checkpoint.</summary>
    public static TaskState FromJson(JsonValue json)
    {
        const string what = "task state";
        var value = RecordJson.Object(json, what);
        var status = RecordJson.Enum<DurableTaskStatus>(value, "status", what);
        switch (status)
        {
            case DurableTaskStatus.Pending or DurableTaskStatus.Running:
            {
                RecordJson.OnlyKeys(value, what, "status", "checkpoint");
                var checkpoint = RecordJson.Required(value, "checkpoint", what);
                return status == DurableTaskStatus.Pending ? new PendingTaskState(checkpoint) : new RunningTaskState(checkpoint);
            }
            case DurableTaskStatus.Waiting:
                RecordJson.OnlyKeys(value, what, "status", "checkpoint", "on", "policy");
                return new WaitingTaskState(RecordJson.Required(value, "checkpoint", what),
                    [.. RecordJson.Array(RecordJson.Required(value, "on", what), "on", what).Select(id => TaskId.FromNumber(RecordJson.Integer(id, "on", what)))],
                    RecordJson.Enum<JoinPolicy>(value, "policy", what));
            default:
            {
                RecordJson.OnlyKeys(value, what, "status", "outcome");
                var outcome = TaskOutcome.FromJson(RecordJson.Required(value, "outcome", what));
                return status == DurableTaskStatus.Completing ? new CompletingTaskState(outcome) : new TerminalTaskState(outcome);
            }
        }
    }
}

/// <summary>A state that can still run: pending, running or waiting. It carries the complete durable state execution resumes from.</summary>
public abstract class LiveTaskState : TaskState
{
    private protected LiveTaskState(JsonValue checkpoint) { Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)); }
    /// <summary>The checkpoint.</summary>
    public JsonValue Checkpoint { get; }
}

/// <summary>A state whose outcome is decided: completing or terminal. It runs no more code.</summary>
public abstract class SettlingTaskState : TaskState
{
    private protected SettlingTaskState(TaskOutcome outcome) { Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome)); }
    /// <summary>The outcome.</summary>
    public TaskOutcome Outcome { get; }
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", WireName.Of(Status)).Add("outcome", Outcome.ToJson()).Build();
}

/// <summary><c>{status: "pending", checkpoint}</c>: eligible for scheduling.</summary>
public sealed class PendingTaskState(JsonValue checkpoint) : LiveTaskState(checkpoint)
{
    /// <inheritdoc/>
    public override DurableTaskStatus Status => DurableTaskStatus.Pending;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", "pending").Add("checkpoint", Checkpoint).Build();
}

/// <summary><c>{status: "running", checkpoint}</c>: reserved by one in-memory invocation.</summary>
public sealed class RunningTaskState(JsonValue checkpoint) : LiveTaskState(checkpoint)
{
    /// <inheritdoc/>
    public override DurableTaskStatus Status => DurableTaskStatus.Running;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", "running").Add("checkpoint", Checkpoint).Build();
}

/// <summary><c>{status: "waiting", checkpoint, on, policy}</c>: parked until every task in <see cref="On"/> is terminal.</summary>
public sealed class WaitingTaskState(JsonValue checkpoint, ImmutableArray<TaskId> on, JoinPolicy policy) : LiveTaskState(checkpoint)
{
    /// <inheritdoc/>
    public override DurableTaskStatus Status => DurableTaskStatus.Waiting;
    /// <summary>The awaited tasks.</summary>
    public ImmutableArray<TaskId> On { get; } = on.IsDefault ? throw new ArgumentNullException(nameof(on)) : on;
    /// <summary>The join policy.</summary>
    public JoinPolicy Policy { get; } = policy;
    /// <inheritdoc/>
    public override JsonObjectValue ToJson() => new RecordJson.Writer().Add("status", "waiting").Add("checkpoint", Checkpoint)
        .Add("on", JsonValue.Array(On.Select(id => (JsonValue)id.ToJson()))).Add("policy", WireName.Of(Policy)).Build();
}

/// <summary><c>{status: "completing", outcome}</c>: outcome decided, terminal once no ordinary owned work below is live.</summary>
public sealed class CompletingTaskState(TaskOutcome outcome) : SettlingTaskState(outcome)
{
    /// <inheritdoc/>
    public override DurableTaskStatus Status => DurableTaskStatus.Completing;
}

/// <summary><c>{status: "terminal", outcome}</c>: the permanently settled durable result receipt.</summary>
public sealed class TerminalTaskState(TaskOutcome outcome) : SettlingTaskState(outcome)
{
    /// <inheritdoc/>
    public override DurableTaskStatus Status => DurableTaskStatus.Terminal;
}

/// <summary>Complete replacement record for one durable task state machine (upstream <c>TaskRecord&lt;JsonValue, JsonValue,
/// JsonValue&gt;</c>; typed task views join with the runtime in P7). JSON key order is upstream's: <c>id, conversationId, kind, version, input,
/// owner, background, abortRequested, state</c> as the Session creates a record, then <c>startedAt</c> (stamped on the first change to
/// running), <c>memos</c> (added by a running invocation, dropped with the outcome) and <c>endedAt</c>, each appended by a spread of the
/// current record. <see cref="KeyOrder"/> keeps a decoded record's own order through <c>with</c>.</summary>
public sealed record TaskRecord
{
    /// <summary>The task ID.</summary>
    public required TaskId Id { get; init; }
    /// <summary>The task's conversation.</summary>
    public required ConversationId ConversationId { get; init; }
    /// <summary>Registered task definition name.</summary>
    public required string Kind { get; init; }
    /// <summary>Definition version used to migrate live input and checkpoints.</summary>
    public required int Version { get; init; }
    /// <summary>Original task input retained while the task is live or terminal.</summary>
    public required JsonValue Input { get; init; }
    /// <summary>Owning task of a child task; absent for a task its conversation owns. Immutable.</summary>
    public TaskId? Owner { get; init; }
    /// <summary>Whether this conversation-owned task is excluded from ordinary idle waits, conversation aborts and cascades.</summary>
    public required bool Background { get; init; }
    /// <summary>Durable abort mark checked before run-mode progress is committed.</summary>
    public required bool AbortRequested { get; init; }
    /// <summary>The state.</summary>
    public required TaskState State { get; init; }
    /// <summary>Small first-writer-wins values retained while the task can run; a completing or terminal record has none.</summary>
    public JsonObjectValue? Memos { get; init; }
    /// <summary>Wall-clock milliseconds of the first change to running, stamped by the Session.</summary>
    public double? StartedAt { get; init; }
    /// <summary>Wall-clock milliseconds of the change to terminal, stamped by the Session.</summary>
    public double? EndedAt { get; init; }
    /// <summary>The JSON key order of a decoded record (see <see cref="RecordJson.Ordered"/>); default: the creation order above.</summary>
    internal ImmutableArray<string> KeyOrder { get; init; }

    /// <summary>Throws when a completing or terminal record retains memos.</summary>
    public TaskRecord Validate()
    {
        if (State is SettlingTaskState && Memos is not null) throw RecordJson.Invalid("task record", "a completing or terminal task cannot retain memos");
        return this;
    }

    /// <summary>The persisted JSON.</summary>
    public JsonObjectValue ToJson() => RecordJson.Ordered(new RecordJson.Writer()
        .Add("id", Id.ToJson()).Add("conversationId", ConversationId.ToJson()).Add("kind", Kind).Number("version", Version).Add("input", Input)
        .Add("owner", Owner?.ToJson()).Add("background", Background).Add("abortRequested", AbortRequested).Add("state", State.ToJson())
        .Number("startedAt", StartedAt).Add("memos", Memos).Number("endedAt", EndedAt).Build(), KeyOrder);

    /// <summary>Decodes and validates <see cref="ToJson"/>.</summary>
    public static TaskRecord FromJson(JsonValue json)
    {
        const string what = "task record";
        var value = RecordJson.Object(json, what);
        RecordJson.OnlyKeys(value, what, "id", "conversationId", "kind", "version", "input", "owner", "background", "abortRequested", "state",
            "memos", "startedAt", "endedAt");
        return new TaskRecord
        {
            Id = RecordJson.Id<TaskId>(value, "id", what),
            ConversationId = RecordJson.Id<ConversationId>(value, "conversationId", what),
            Kind = RecordJson.String(value, "kind", what),
            Version = RecordJson.Version(value, "version", what),
            Input = RecordJson.Required(value, "input", what),
            Owner = RecordJson.OptionalId<TaskId>(value, "owner", what),
            Background = RecordJson.Boolean(value, "background", what),
            AbortRequested = RecordJson.Boolean(value, "abortRequested", what),
            State = TaskState.FromJson(RecordJson.Required(value, "state", what)),
            Memos = RecordJson.Optional(value, "memos") is { } memos ? RecordJson.Object(memos, "task memos") : null,
            StartedAt = RecordJson.OptionalNumber(value, "startedAt", what),
            EndedAt = RecordJson.OptionalNumber(value, "endedAt", what),
            KeyOrder = RecordJson.KeysOf(value),
        }.Validate();
    }

    /// <inheritdoc/>
    public bool Equals(TaskRecord? other) => other is not null && JsonValue.DeepEquals(ToJson(), other.ToJson());
    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode();
    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}
