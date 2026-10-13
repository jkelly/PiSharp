// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/inbox.ts. Items are stored as upstream's JSON:
// {id, mode: "steer" | "followUp", content} or {id, mode: "write", entry}, where entry is the write's EntryDraft as plain JSON
// {kind, head?, model?, data?, edits?} (head an entry ID or "self"). The conversation view of the inbox is view.ts's.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>The built-in inbox (upstream <c>inbox.ts</c>).</summary>
public static class Inbox
{
    /// <summary>Built-in queue of one conversation's submissions waiting for a boundary, in ID order: <c>{items: []}</c> (upstream
    /// <c>InboxDoc</c>).</summary>
    public static ConversationDocToken InboxDoc { get; } = Documents.DefineConversationDoc("pi.inbox", 1, DocumentFork.Initial,
        () => JsonValue.Object([KeyValuePair.Create("items", (JsonValue)JsonArrayValue.Empty)]),
        checkpointWhen: (value, _, _) => value["items"] is JsonArrayValue { Count: 0 });

    /// <summary>
    /// What a boundary reads before the commit's first table write, and the newest head it has seen so far (upstream <c>Boundary</c>).
    /// </summary>
    internal sealed class Boundary(ConversationId conversationId, DraftObject inbox, QueueMode steeringMode, QueueMode followUpMode, EntryId? head)
    {
        public ConversationId ConversationId { get; } = conversationId;
        public DraftObject Inbox { get; } = inbox;
        public QueueMode SteeringMode { get; } = steeringMode;
        public QueueMode FollowUpMode { get; } = followUpMode;
        /// <summary>Start of the active range, the newest head marker's <c>head</c>; advanced by heads written in this commit.</summary>
        public EntryId? Head { get; set; } = head;
    }

    /// <summary>Selected user items, in ID order, and whether a <c>head: "self"</c> write (a reset) was placed (upstream
    /// <c>BoundaryResult</c>).</summary>
    internal sealed record BoundaryResult(ImmutableArray<SubmissionId> Users, bool Reset);

    /// <summary>
    /// Reads what a boundary needs, with the queue modes of <paramref name="settings"/> (upstream <c>prepareBoundary</c>). Table reads must
    /// precede the commit's first table write, so callers prepare the boundary at the start of their commit.
    /// </summary>
    internal static async Task<Boundary> PrepareBoundaryAsync(ITx tx, ConversationId conversationId, Settings settings)
    {
        var head = (await tx.LatestHeadMarkerAsync(conversationId).ConfigureAwait(false))?.Head;
        var inbox = await tx.DocAsync(InboxDoc, conversationId).ConfigureAwait(false);
        return new Boundary(conversationId, inbox, settings.SteeringMode, settings.FollowUpMode, head);
    }

    /// <summary>
    /// Places the queued items a boundary selects (upstream <c>applyBoundary</c>, spec §6): every write, the first or all steers, and at
    /// <c>final</c> the first or all follow-ups. A selected reset turns a <c>postTools</c> boundary into <c>final</c>. Writes are placed
    /// first and user items after them, each in ID order, so user items queued before a reset run in the new context. A write whose head
    /// targets an entry before the active range, including a range started earlier in this commit, is stale. Selected and stale items are
    /// removed positionally.
    /// </summary>
    internal static async Task<BoundaryResult> ApplyBoundaryAsync(ITx tx, Boundary boundary, bool final, double now)
    {
        var items = boundary.Inbox.GetArray("items");
        var snapshot = Enumerable.Range(0, items.Count).Select(index => (JsonObjectValue)items.Get(index)).ToList();
        var reset = snapshot.Any(item => Mode(item) == "write" && ((JsonObjectValue)item["entry"]).TryGetValue("head", out var head) &&
            head is JsonStringValue { Value: "self" });
        final = final || reset;
        List<int> Pick(string mode, QueueMode queueMode)
        {
            var indexes = snapshot.Select((item, index) => (item, index)).Where(pair => Mode(pair.item) == mode).Select(pair => pair.index).ToList();
            return queueMode == QueueMode.All ? indexes : [.. indexes.Take(1)];
        }
        var writes = snapshot.Select((item, index) => (item, index)).Where(pair => Mode(pair.item) == "write").Select(pair => pair.index).ToList();
        var users = Pick("steer", boundary.SteeringMode).Concat(final ? Pick("followUp", boundary.FollowUpMode) : []).Order().ToList();

        foreach (var index in writes)
        {
            var item = snapshot[index];
            var draft = EntryDraftFromJson((JsonObjectValue)item["entry"]);
            if (IsStale(boundary, draft))
            {
                tx.SettleSubmission(ItemSubmission(item), SubmissionSettlement.Unanswered("stale"));
                continue;
            }
            var entry = await tx.AppendEntryAsync(boundary.ConversationId, draft).ConfigureAwait(false);
            if (draft.Head is { } head) boundary.Head = head.IsSelf ? entry.Id : head.Id;
            tx.PlaceSubmission(ItemSubmission(item), entry.Id);
        }
        var placed = ImmutableArray.CreateBuilder<SubmissionId>();
        foreach (var index in users)
        {
            var item = snapshot[index];
            var message = JsonValue.Object([
                KeyValuePair.Create("role", (JsonValue)"user"),
                KeyValuePair.Create("content", item["content"]),
                KeyValuePair.Create("timestamp", (JsonValue)now),
            ]);
            var entry = await tx.AppendEntryAsync(Entries.UserEntry, boundary.ConversationId, new TypedEntryDraft { Model = JsonValue.Array(message) })
                .ConfigureAwait(false);
            tx.PlaceSubmission(ItemSubmission(item), entry.Id);
            placed.Add(ItemSubmission(item));
        }
        foreach (var index in writes.Concat(users).OrderDescending()) items.RemoveAt(index);
        return new BoundaryResult(placed.ToImmutable(), reset);
    }

    /// <summary>Whether a head write targets an entry before the active range, so placing it would bring back cut history (upstream
    /// <c>isStale</c>).</summary>
    internal static bool IsStale(Boundary boundary, EntryDraft entry) =>
        entry.Head is { IsSelf: false } head && boundary.Head is { } start && head.Id!.Value.Value < start.Value;

    /// <summary>Removes a withdrawn submission's item; the caller settles the submission (upstream <c>removeInboxItem</c>).</summary>
    public static async Task RemoveInboxItemAsync(ITx tx, ConversationId conversationId, SubmissionId id)
    {
        var items = (await tx.DocAsync(InboxDoc, conversationId).ConfigureAwait(false)).GetArray("items");
        for (var index = 0; index < items.Count; index++)
        {
            if (ItemId(items.Get(index)) != id.Value) continue;
            items.RemoveAt(index);
            return;
        }
    }

    /// <summary>
    /// Withdraws every queued input of a conversation, as <c>Conversation.AbortAsync()</c> and abort cascades do (upstream
    /// <c>withdrawQueuedInputs</c>): each settles <c>unanswered</c> with <c>aborted</c> and leaves the inbox; queued writes stay for later
    /// placement.
    /// </summary>
    public static async Task WithdrawQueuedInputsAsync(ITx tx, ConversationId conversationId)
    {
        var items = (await tx.DocAsync(InboxDoc, conversationId).ConfigureAwait(false)).GetArray("items");
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = (JsonObjectValue)items.Get(index);
            if (item["mode"] is JsonStringValue { Value: "write" }) continue;
            tx.SettleSubmission(new SubmissionId(ItemId(item)), SubmissionSettlement.Unanswered("aborted"));
            items.RemoveAt(index);
        }
    }

    /// <summary>
    /// An entry draft as the plain JSON an inbox write item stores: <c>{kind, head?, model?, data?, edits?}</c>. Upstream copies the caller's
    /// object, keeping its key order; this is the order of upstream's own write drafts, a reset (<c>{kind, head, model}</c>) and a compaction
    /// summary (<c>{kind, head, model, data}</c>).
    /// </summary>
    internal static JsonObjectValue EntryDraftToJson(EntryDraft draft) => new RecordJson.Writer()
        .Add("kind", draft.Kind)
        .Add("head", draft.Head is { } head ? head.IsSelf ? JsonValue.String("self") : head.Id!.Value.ToJson() : null)
        .Add("model", draft.Model)
        .Add("data", draft.Data)
        .Add("edits", draft.Edits is { } edits ? JsonValue.Array(edits.Select(edit => (JsonValue)edit.ToJson())) : null)
        .Build();

    /// <summary>The entry draft an inbox write item stores.</summary>
    internal static EntryDraft EntryDraftFromJson(JsonObjectValue value) => new()
    {
        Kind = ((JsonStringValue)value["kind"]).Value,
        Model = value.TryGetValue("model", out var model) ? (JsonArrayValue)model : null,
        Data = value.TryGetValue("data", out var data) ? data : null,
        Head = value.TryGetValue("head", out var head)
            ? head is JsonStringValue { Value: "self" } ? EntryHead.Self : EntryHead.At(new EntryId((long)((JsonNumberValue)head).Value))
            : null,
        Edits = value.TryGetValue("edits", out var edits) ? [.. ((JsonArrayValue)edits).Select(ContextEdit.FromJson)] : null,
    };

    private static string? Mode(JsonObjectValue item) => item.TryGetValue("mode", out var mode) && mode is JsonStringValue text ? text.Value : null;

    private static SubmissionId ItemSubmission(JsonObjectValue item) => new(ItemId(item));

    private static long ItemId(JsonValue item) => (long)((JsonNumberValue)((JsonObjectValue)item)["id"]).Value;
}
