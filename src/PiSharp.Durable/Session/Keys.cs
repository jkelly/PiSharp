// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/session/keys.ts (taskKey, conversationKey, entryKey, appendKey,
// submissionKey, requestKey, documentKey, incarnationKey, scopeKey, ownedKey, ROOTS_KEY, INTENT_KEY, ALL_KEYS, tableKeys, GatedStorage).
// The keys of the Session line (docs/group-commit.md section 2): what a commit writes and a job reads, so a read of an unpublished write
// waits for it. IDs print in invariant culture (their ToString), so a key is the same string on every thread and culture.
using System.Globalization;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>The keys of the Session line (upstream <c>session/keys.ts</c>).</summary>
internal static class LineKeys
{
    /// <summary>Every unpublished write: what a read that cannot be keyed, such as a scan, touches (upstream <c>ALL_KEYS</c>).</summary>
    public const string All = "*";

    /// <summary>The ownerless conversations with live work below them, which global idle walks from (upstream <c>ROOTS_KEY</c>).</summary>
    public const string Roots = "roots";

    /// <summary>The live tasks with cancellation intent: abort marks, and held outcomes other than completed (upstream <c>INTENT_KEY</c>).</summary>
    public const string Intent = "intent";

    /// <summary>A task record.</summary>
    public static string Task(TaskId id) => "task:" + id;

    /// <summary>A conversation record.</summary>
    public static string Conversation(ConversationId id) => "conv:" + id;

    /// <summary>An entry record.</summary>
    public static string Entry(EntryId id) => "entry:" + id;

    /// <summary>The newest entries of a conversation: appended blind, read by scans, the newest entry, and head markers.</summary>
    public static string Append(ConversationId id) => "entry-append:" + id;

    /// <summary>A submission record.</summary>
    public static string Submission(SubmissionId id) => "sub:" + id;

    /// <summary>The submission a conversation's request ID names.</summary>
    public static string Request(ConversationId conversationId, string requestId) => "req:" + conversationId + ":" + requestId;

    /// <summary>A document address (<c>Documents.AddressId</c>): its current incarnation, value, and held changes.</summary>
    public static string Document(string addressId) => "doc:" + addressId;

    /// <summary>A document incarnation, for reads by its ID.</summary>
    public static string Incarnation(DocumentId id) => "docid:" + id;

    /// <summary>The incarnations alive in a scope: created and retired blind, read by scans and the task-document index.</summary>
    public static string Scope(DocumentScope scope) => scope.Kind switch
    {
        DocumentScopeKind.Session => "docscope:session",
        DocumentScopeKind.Conversation => "docscope:c" + scope.ConversationId,
        _ => "docscope:t" + scope.TaskId,
    };

    /// <summary>
    /// A node of the scheduler's ownership tree (a task ID, or <c>c</c> and a conversation ID): the live work listed below it (upstream
    /// <c>ownedKey</c>). For the scheduler's membership keys (area group-commit-scheduler).
    /// </summary>
    public static string Owned(string node) => "owned:" + node;

    /// <summary><see cref="Owned(string)"/> of a task node.</summary>
    public static string Owned(TaskId id) => Owned(id.ToString());

    /// <summary>The keys of the table records in <paramref name="writes"/>; document writes are keyed by the transaction, which knows
    /// addresses (upstream <c>tableKeys</c>).</summary>
    public static void TableKeys(IReadOnlyList<StorageWrite> writes, List<string> keys)
    {
        foreach (var write in writes)
        {
            switch (write)
            {
                case ConversationWrite conversation:
                    keys.Add(Conversation(conversation.Value.Id));
                    break;
                case EntryWrite entry:
                    keys.Add(Entry(entry.Value.Id));
                    keys.Add(Append(entry.Value.ConversationId));
                    break;
                case TaskWrite task:
                    keys.Add(Task(task.Value.Id));
                    break;
                case SubmissionWrite submission:
                    keys.Add(Submission(submission.Value.Id));
                    if (submission.Value.RequestId is { } requestId) keys.Add(Request(submission.Value.ConversationId, requestId));
                    break;
            }
        }
    }

    /// <summary>Invariant text of a number, for keys built from one.</summary>
    internal static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The Storage a job on the line reads through (upstream <c>GatedStorage</c>): every read first touches its key, which waits while the key
/// has an unpublished writer, so the read sees what the line admitted before it. Scans that cannot be keyed touch <see cref="LineKeys.All"/>.
/// Commits, the publication scope, ID minting and close pass through; the line never commits through it.
/// </summary>
internal sealed class GatedStorage(IStorage storage, Func<string, Task?> touch) : IStorage
{
    public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        storage.CommitAsync(batches, receive, context);

    public void Publishing(Action publish, DurableContext context) => storage.Publishing(publish, context);

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => storage.MintIdAsync<TId>();

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) =>
        After(LineKeys.Conversation(id), () => storage.GetConversationAsync(id, context));

    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        After(LineKeys.All, () => storage.ScanConversationsAsync(query, limit, cursor, context));

    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) =>
        After(LineKeys.Entry(id), () => storage.GetEntryAsync(id, context));

    /// <summary>Visibility follows the conversation's immutable ancestry; the conversation may be unpublished itself.</summary>
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        After(LineKeys.Conversation(conversationId), () => After(LineKeys.Entry(id), () => storage.GetEntryAsync(conversationId, id, context)));

    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        AfterEntries(conversationId, () => storage.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context));

    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        AfterEntries(query.ConversationId, () => storage.ScanEntriesAsync(query, limit, cursor, context));

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
        After(LineKeys.Task(id), () => storage.GetTaskAsync(id, context));

    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        After(LineKeys.All, () => storage.ScanTasksAsync(query, limit, cursor, context));

    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) =>
        After(LineKeys.Submission(id), () => storage.GetSubmissionAsync(id, context));

    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        After(LineKeys.All, () => storage.ScanSubmissionsAsync(query, limit, cursor, context));

    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        After(LineKeys.Request(conversationId, requestId), () => storage.GetSubmissionByRequestAsync(conversationId, requestId, context));

    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        After(LineKeys.Document(Documents.AddressId(address)), () => storage.FindDocumentAsync(address, at, context));

    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        After(LineKeys.Incarnation(id), () => storage.GetDocumentAsync(id, at, context));

    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        After(LineKeys.Scope(query.Scope), () => storage.ScanDocumentsAsync(query, limit, cursor, context));

    public Task CloseAsync(DurableContext context) => storage.CloseAsync(context);

    /// <summary>Runs a read of a conversation's entries once neither its appends nor its creation has an unpublished writer: Storage refuses
    /// the read of a conversation it does not have yet.</summary>
    private Task<T> AfterEntries<T>(ConversationId conversationId, Func<Task<T>> read) =>
        After(LineKeys.Conversation(conversationId), () => After(LineKeys.Append(conversationId), read));

    /// <summary>Runs <paramref name="read"/> once <paramref name="key"/> has no unpublished writer.</summary>
    private Task<T> After<T>(string key, Func<Task<T>> read)
    {
        var gate = touch(key);
        return gate is null ? read() : Then(gate, read);
    }

    private static async Task<T> Then<T>(Task gate, Func<Task<T>> read)
    {
        await gate.ConfigureAwait(false);
        return await read().ConfigureAwait(false);
    }
}
