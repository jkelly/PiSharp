// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/ids.ts, plus the Id, Seq and ROOT_CONVERSATION_ID
// declarations of packages/durable/src/types.ts. Upstream brands plain numbers at compile time only; here each kind is its own struct
// over a long, so a conversation ID cannot be passed as a task ID or a commit sequence. Values stay ordinary JSON numbers.
using System.Globalization;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>An ID from the Session-global numeric namespace (upstream <c>Id&lt;Kind&gt;</c>).</summary>
public interface IDurableId
{
    /// <summary>The number.</summary>
    long Value { get; }
}

/// <summary>An ID kind that a trusted allocation or decoding boundary can create from a number (upstream <c>idFromNumber</c>).</summary>
public interface IDurableId<TSelf> : IDurableId, IComparable<TSelf> where TSelf : struct, IDurableId<TSelf>
{
    /// <summary>Applies this kind to a number.</summary>
    static abstract TSelf FromNumber(long value);
}

/// <summary>Identifies a conversation.</summary>
public readonly record struct ConversationId(long Value) : IDurableId<ConversationId>
{
    /// <summary>The reserved root conversation (upstream <c>ROOT_CONVERSATION_ID</c>).</summary>
    public static ConversationId Root => new(1);
    /// <inheritdoc/>
    public static ConversationId FromNumber(long value) => new(value);
    /// <inheritdoc/>
    public int CompareTo(ConversationId other) => Value.CompareTo(other.Value);
    /// <summary>The JSON number.</summary>
    public JsonNumberValue ToJson() => JsonValue.Number(Value);
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Identifies an entry.</summary>
public readonly record struct EntryId(long Value) : IDurableId<EntryId>
{
    /// <inheritdoc/>
    public static EntryId FromNumber(long value) => new(value);
    /// <inheritdoc/>
    public int CompareTo(EntryId other) => Value.CompareTo(other.Value);
    /// <summary>The JSON number.</summary>
    public JsonNumberValue ToJson() => JsonValue.Number(Value);
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Identifies a task. Upstream's <c>TaskId&lt;Result&gt;</c> also carries the result type at compile time; the typed form
/// joins the task runtime (P7).</summary>
public readonly record struct TaskId(long Value) : IDurableId<TaskId>
{
    /// <inheritdoc/>
    public static TaskId FromNumber(long value) => new(value);
    /// <inheritdoc/>
    public int CompareTo(TaskId other) => Value.CompareTo(other.Value);
    /// <summary>The JSON number.</summary>
    public JsonNumberValue ToJson() => JsonValue.Number(Value);
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Identifies a submission.</summary>
public readonly record struct SubmissionId(long Value) : IDurableId<SubmissionId>
{
    /// <inheritdoc/>
    public static SubmissionId FromNumber(long value) => new(value);
    /// <inheritdoc/>
    public int CompareTo(SubmissionId other) => Value.CompareTo(other.Value);
    /// <summary>The JSON number.</summary>
    public JsonNumberValue ToJson() => JsonValue.Number(Value);
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Identifies one document incarnation.</summary>
public readonly record struct DocumentId(long Value) : IDurableId<DocumentId>
{
    /// <inheritdoc/>
    public static DocumentId FromNumber(long value) => new(value);
    /// <inheritdoc/>
    public int CompareTo(DocumentId other) => Value.CompareTo(other.Value);
    /// <summary>The JSON number.</summary>
    public JsonNumberValue ToJson() => JsonValue.Number(Value);
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Strictly increasing sequence assigned to one atomic storage commit; gaps are permitted. Not an ID: an entity ID cannot be
/// used as a document commit point.</summary>
public readonly record struct Seq(long Value) : IComparable<Seq>
{
    /// <inheritdoc/>
    public int CompareTo(Seq other) => Value.CompareTo(other.Value);
    /// <summary>Whether <paramref name="left"/> is earlier.</summary>
    public static bool operator <(Seq left, Seq right) => left.Value < right.Value;
    /// <summary>Whether <paramref name="left"/> is later.</summary>
    public static bool operator >(Seq left, Seq right) => left.Value > right.Value;
    /// <summary>Whether <paramref name="left"/> is not later.</summary>
    public static bool operator <=(Seq left, Seq right) => left.Value <= right.Value;
    /// <summary>Whether <paramref name="left"/> is not earlier.</summary>
    public static bool operator >=(Seq left, Seq right) => left.Value >= right.Value;
    /// <summary>The JSON number.</summary>
    public JsonNumberValue ToJson() => JsonValue.Number(Value);
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Trusted ID boundaries (upstream <c>ids.ts</c>) and the reserved root conversation.</summary>
public static class Ids
{
    /// <summary>JavaScript's <c>Number.MAX_SAFE_INTEGER</c>: IDs and sequences are safe integers.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>The root conversation always uses this reserved ID (upstream <c>ROOT_CONVERSATION_ID</c>).</summary>
    public static ConversationId RootConversationId => ConversationId.Root;

    /// <summary>Applies an ID kind at a trusted numeric allocation or decoding boundary (upstream <c>idFromNumber</c>).</summary>
    public static TId FromNumber<TId>(long value) where TId : struct, IDurableId<TId> => TId.FromNumber(value);

    /// <summary>Applies the commit-sequence kind at a trusted storage boundary (upstream <c>seqFromNumber</c>).</summary>
    public static Seq SeqFromNumber(long value) => new(value);

    /// <summary>Whether <paramref name="value"/> is a safe integer (<c>Number.isSafeInteger</c>).</summary>
    public static bool IsSafeInteger(long value) => value is >= -MaxSafeInteger and <= MaxSafeInteger;
}
