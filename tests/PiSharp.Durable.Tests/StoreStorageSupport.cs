// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/store-storage-support.ts
//
// StoreStorage over a SQLite file for tests: a store opened on the file, Durable's storage in it under a test owner name, and a close that
// closes the store too (the app's part: in tests the storage is the store's only owner). Upstream wraps the storage in a Proxy whose close
// is replaced; here StoreOwningStorage delegates every member and closes the store after the storage. The store's backend is
// PiSharp.Durable.Sqlite's connection database through PiSharp.Durable.Store's adapter, as an app opens it.
using PiSharp.Chord;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Store;
using IStore = PiSharp.Chord.Store.IStore;
using Tables = PiSharp.Chord.Store.Tables;

internal static partial class Program
{
    /// <summary>The owner name the test storages use (upstream <c>TEST_OWNER</c>).</summary>
    internal const string StoreTestOwner = "durable.test";

    /// <summary>A StoreStorage on a store over the SQLite file at <paramref name="path"/> (<c>:memory:</c> too) whose close closes the
    /// store too (upstream <c>openStoreStorage</c>).</summary>
    internal static async Task<IStorage> OpenStoreStorageAsync(string path, SqliteStorageOptions? options = null)
    {
        var store = await Tables.OpenStoreAsync(await StoreBackends.OpenSqliteAsync(path, options), ChordContext.Background);
        StoreStorage storage;
        try
        {
            storage = await StoreStorage.OpenAsync(store, StoreTestOwner, DurableContext.Background);
        }
        catch
        {
            await store.CloseAsync(ChordContext.Background);
            throw;
        }
        return new StoreOwningStorage(storage, store);
    }
}

/// <summary>A StoreStorage whose close also closes its store, once.</summary>
internal sealed class StoreOwningStorage(StoreStorage storage, IStore store) : IStorage
{
    private readonly Lock gate = new();
    private Task? closing;

    /// <summary>The storage itself.</summary>
    public StoreStorage Storage => storage;

    /// <summary>Its store.</summary>
    public IStore Store => store;

    public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        storage.CommitAsync(batches, receive, context);

    public void Publishing(Action publish, DurableContext context) => storage.Publishing(publish, context);

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => storage.MintIdAsync<TId>();

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => storage.GetConversationAsync(id, context);

    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanConversationsAsync(query, limit, cursor, context);

    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => storage.GetEntryAsync(id, context);

    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        storage.GetEntryAsync(conversationId, id, context);

    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        storage.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);

    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanEntriesAsync(query, limit, cursor, context);

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => storage.GetTaskAsync(id, context);

    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanTasksAsync(query, limit, cursor, context);

    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => storage.GetSubmissionAsync(id, context);

    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanSubmissionsAsync(query, limit, cursor, context);

    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        storage.GetSubmissionByRequestAsync(conversationId, requestId, context);

    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        storage.FindDocumentAsync(address, at, context);

    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => storage.GetDocumentAsync(id, at, context);

    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        storage.ScanDocumentsAsync(query, limit, cursor, context);

    public Task CloseAsync(DurableContext context)
    {
        lock (gate) return closing ??= CloseBothAsync(context);
    }

    private async Task CloseBothAsync(DurableContext context)
    {
        await storage.CloseAsync(context);
        await store.CloseAsync(ChordContext.Background);
    }
}
