// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/inbox.ts (InboxDoc, removeInboxItem,
// withdrawQueuedInputs). Items are stored as upstream's JSON: {id, mode: "steer" | "followUp", content} or {id, mode: "write", entry}.
// TODO(P10): boundaries (prepareBoundary, applyBoundary, isStale) join with the inbox phase.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>The built-in inbox (upstream <c>inbox.ts</c>).</summary>
public static class Inbox
{
    /// <summary>Built-in queue of one conversation's submissions waiting for a boundary, in ID order: <c>{items: []}</c> (upstream
    /// <c>InboxDoc</c>).</summary>
    public static ConversationDocToken InboxDoc { get; } = Documents.DefineConversationDoc("pi.inbox", 1, DocumentFork.Initial,
        () => JsonValue.Object([KeyValuePair.Create("items", (JsonValue)JsonArrayValue.Empty)]),
        checkpointWhen: (value, _, _) => value["items"] is JsonArrayValue { Count: 0 });

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

    private static long ItemId(JsonValue item) => (long)((JsonNumberValue)((JsonObjectValue)item)["id"]).Value;
}
