// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/session/forks.ts (prepareForkDocumentCopies, collectCopies).
// Spec §3.7: an "asOf" document is copied from the ancestor that owns the fork entry at that entry's commit, a "current" one from the
// immediate parent's current state; "initial" documents are not copied. Copies are definition-free: Storage materializes each source.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>One definition-free document copy to create with a forked conversation (upstream <c>ForkDocumentCopy</c>).</summary>
/// <param name="Record">The child's create record.</param>
/// <param name="Source">The exact stored source.</param>
internal sealed record ForkDocumentCopy(DocumentCreate Record, DocumentCopySource Source);

/// <summary>Fork document selection.</summary>
internal static class Forks
{
    private const int ScanPageSize = 256;

    /// <summary>Selects every persisted conversation document copied by one fork (upstream <c>prepareForkDocumentCopies</c>).</summary>
    public static async Task<IReadOnlyList<ForkDocumentCopy>> PrepareForkDocumentCopiesAsync(IStorage storage, ConversationId parentConversationId,
        EntryId at, ConversationId childConversationId, DurableContext context)
    {
        var entry = await storage.GetEntryAsync(parentConversationId, at, context).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Entry {at} is not visible from conversation {parentConversationId}");
        var copies = new List<ForkDocumentCopy>();
        var copiedAddresses = new HashSet<string>(StringComparer.Ordinal);
        await CollectCopiesAsync(storage, DocumentScope.Conversation(entry.Entry.ConversationId), entry.CommitSeq, DocumentFork.AsOf,
            childConversationId, copies, copiedAddresses, context).ConfigureAwait(false);
        await CollectCopiesAsync(storage, DocumentScope.Conversation(parentConversationId), DocumentPoint.Current, DocumentFork.Current,
            childConversationId, copies, copiedAddresses, context).ConfigureAwait(false);
        return copies;
    }

    private static async Task CollectCopiesAsync(IStorage storage, DocumentScope scope, DocumentPoint at, DocumentFork policy,
        ConversationId childConversationId, List<ForkDocumentCopy> copies, HashSet<string> copiedAddresses, DurableContext context)
    {
        JsonObjectValue? cursor = null;
        do
        {
            var page = await storage.ScanDocumentsAsync(new DocumentQuery(scope, at), ScanPageSize, cursor, context).ConfigureAwait(false);
            foreach (var source in page.Items)
            {
                if (source.Scope.Kind != DocumentScopeKind.Conversation || source.Fork != policy) continue;
                var id = await storage.MintIdAsync<DocumentId>().ConfigureAwait(false);
                var record = new DocumentCreate
                {
                    Id = id, Kind = source.Kind, Key = source.Key, Scope = DocumentScope.Conversation(childConversationId),
                    History = source.History, Fork = source.Fork,
                };
                if (!copiedAddresses.Add(Documents.AddressId(DocumentAddress.Of(record))))
                {
                    var member = record.Key is null ? record.Kind : $"{record.Kind}/{record.Key}";
                    throw new InvalidOperationException($"Fork selects multiple source documents for {member}");
                }
                copies.Add(new ForkDocumentCopy(record, new DocumentCopySource(source.Id, at)));
            }
            cursor = page.Next;
        } while (cursor is not null);
    }
}
