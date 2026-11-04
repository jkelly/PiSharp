// PiSharp-only (decision 0010): the session-scoped document families that keep application records after the task that wrote them
// settles. A task's own documents retire with it; these do not, and they are not bounded by the 30-card run window.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>The session document families of judgments, artifacts, evidence and execution-start receipts.</summary>
internal static class SessionRecords
{
    /// <summary>A judgment: <c>{judgment}</c>, keyed by judgment ID.</summary>
    public static SessionDocFamilyToken JudgmentDoc { get; } = Documents.DefineSessionDocFamily("pisharp.judgment", 1, _ => JsonObjectValue.Empty,
        checkpointWhen: (_, _, _) => false);

    /// <summary>An artifact envelope: <c>{artifact}</c>, keyed by artifact ID; written once.</summary>
    public static SessionDocFamilyToken ArtifactDoc { get; } = Documents.DefineSessionDocFamily("pisharp.artifact", 1, _ => JsonObjectValue.Empty,
        checkpointWhen: (_, _, _) => false);

    /// <summary>An evidence reference: <c>{evidence}</c>, keyed by evidence ID; written once.</summary>
    public static SessionDocFamilyToken EvidenceDoc { get; } = Documents.DefineSessionDocFamily("pisharp.evidence", 1, _ => JsonObjectValue.Empty,
        checkpointWhen: (_, _, _) => false);

    /// <summary>An execution start receipt: <c>{kind, requestHash, taskId, agentId}</c>, keyed by the request's Idempotency-Key.</summary>
    public static SessionDocFamilyToken OperationDoc { get; } = Documents.DefineSessionDocFamily("pisharp.operation", 1, _ => JsonObjectValue.Empty,
        checkpointWhen: (_, _, _) => false);

    /// <summary>The newest records of a session, for lists: <c>{artifacts: [id], judgments: [id], evidence: [id]}</c>, each at most
    /// <see cref="MaxIndex"/>, newest first. The records themselves stay readable by ID after they leave the index.</summary>
    public static SessionDocToken IndexDoc { get; } = Documents.DefineSessionDoc("pisharp.records", 1,
        () => JsonValue.Object([new("artifacts", JsonArrayValue.Empty), new("judgments", JsonArrayValue.Empty), new("evidence", JsonArrayValue.Empty)]),
        checkpointWhen: (_, _, _) => false);

    /// <summary>How many IDs of each kind the index keeps.</summary>
    public const int MaxIndex = 500;

    /// <summary>Adds <paramref name="id"/> to the front of the index list <paramref name="kind"/>.</summary>
    public static async Task IndexAsync(ITx tx, string kind, string id)
    {
        var doc = await tx.DocAsync(IndexDoc).ConfigureAwait(false);
        var current = Jobs.Items(doc.ToValue() as JsonObjectValue, kind).Where(item => item is not JsonStringValue text || text.Value != id);
        doc.Set(kind, JsonValue.Array(new JsonValue[] { id }.Concat(current).Take(MaxIndex)));
    }

    /// <summary>The value under <paramref name="field"/> of a record document; null when the record does not exist.</summary>
    public static JsonObjectValue? Field(JsonObjectValue? doc, string field) =>
        doc is not null && doc.TryGetValue(field, out var value) && value is JsonObjectValue record ? record : null;
}
