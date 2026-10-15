// No upstream counterpart: the nested-call benchmarks' patched `storage.commit` (scheduler-fanout-bench.ts, nested-tools-bench.ts,
// nested-images-bench.ts), as a decorator. It counts commits and the logical bytes written per write type, the length of each write's JSON
// (upstream `JSON.stringify(write).length`), and runs an optional callback on each commit.
using System.Collections.Concurrent;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Counts what a storage commits (see the file header).</summary>
internal sealed class CountingStorage(IStorage inner, Action? onCommit = null) : IStorage
{
    /// <summary>Upstream's write types, in its order.</summary>
    public static IReadOnlyList<string> WriteTypes { get; } =
        ["conversation", "entry", "task", "submission", "document.create", "document.copy", "document.change", "document.retire"];

    private int commits;
    private readonly ConcurrentDictionary<string, long> written = new(StringComparer.Ordinal);

    /// <summary>Commits taken so far.</summary>
    public int Commits => Volatile.Read(ref commits);

    /// <summary>Logical bytes written so far, by write type.</summary>
    public Dictionary<string, long> Written() => WriteTypes.ToDictionary(type => type, type => written.GetValueOrDefault(type), StringComparer.Ordinal);

    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        Interlocked.Increment(ref commits);
        onCommit?.Invoke();
        foreach (var write in writes) written.AddOrUpdate(write.Type, write.ToJson().ToJson().Length, (_, total) => total + write.ToJson().ToJson().Length);
        return inner.CommitAsync(writes, context);
    }

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        inner.FindDocumentAsync(address, at, context);
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanConversationsAsync(query, limit, cursor, context);
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        inner.GetEntryAsync(conversationId, id, context);
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanEntriesAsync(query, limit, cursor, context);
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanDocumentsAsync(query, limit, cursor, context);
    public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
}
