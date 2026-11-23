// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/jsonl/storage.ts (and its index.ts exports).
// Read-only opening follows Pi b4afdddf44e059c67ccf5097e5317d913521b470, including bounded retries when a writer reclaims during recovery.
//
// Invalid read requests (an unknown conversation, a malformed or foreign cursor, history a document does not keep) throw
// StorageRequestException; upstream's own StorageRequestError, "Storage.entry() requires an entry ID", has no counterpart, since
// the C# overloads of GetEntryAsync are typed. Every other exception, file failures included, fails the owning Session.
//
// Portable JSONL storage over IFileSystem (spec §11.3). The directory holds main.jsonl, with one commit marker line per commit, and
// sidecars doc-<id>.jsonl (one document incarnation's bases and deltas) and task-<id>.jsonl (live task replacements). Rather than
// retaining every entry, task, submission and document JSON payload in an unbounded in-memory MemoryStorage mirror, the storage maintains
// a compact metadata and byte-offset index with a bounded byte-budgeted LRU cache and reads full payloads lazily via IBinaryReader.
// A batch commit validates and encodes each batch, updates the compact index while reads wait, appends sidecar records and main markers,
// delivers receipts, and reclaims sidecars best-effort (rebasing cached offsets on rewrite). Lines remain byte-for-byte identical to
// upstream so Pi and PiSharp open each other's directories interchangeably.
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Storage.Jsonl;

/// <summary>Options of <see cref="JsonlStorage"/> (upstream <c>JsonlStorageOptions</c>).</summary>
public sealed record JsonlStorageOptions
{
    /// <summary>Flush every affected sidecar before appending the main marker. Defaults to false.</summary>
    public bool Fsync { get; init; }

    /// <summary>Opens an existing directory without writing, for a viewer of a directory another process writes. Recovery skips torn
    /// lines and unconfirmed tails in memory, and reclaims nothing. Reads hold the state committed at open; commits reject. Defaults to false.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>A commit was attempted on storage opened read-only. Nothing was written (upstream <c>JsonlStorageReadOnlyError</c>).</summary>
public sealed class JsonlStorageReadOnlyException() : Exception("JSONL storage is open read-only");

/// <summary>A JSONL storage directory holds data that no commit could have written (upstream <c>JsonlCorruptionError</c>).</summary>
public sealed class JsonlCorruptionException : Exception
{
    /// <summary>A corruption.</summary>
    public JsonlCorruptionException(string message) : base(message) { }
    /// <summary>A corruption found through <paramref name="cause"/>.</summary>
    public JsonlCorruptionException(string message, Exception? cause) : base(message, cause) { }
}

/// <summary>An append failed with an uncertain effect, so the open storage rejects every later operation (upstream
/// <c>JsonlStoragePoisonedError</c>). Reopening recovers the confirmed state.</summary>
public sealed class JsonlStoragePoisonedException(Exception cause) : Exception("JSONL storage is poisoned and must be reopened", cause);

/// <summary>Portable JSONL implementation of the storage contract (upstream <c>JsonlStorage</c>).</summary>
public sealed partial class JsonlStorage : IStorage
{
    private const int FormatVersion = 1;
    private const string MainFile = "main.jsonl";
    private const string ReclaimSuffix = ".reclaim";
    private const int ReadOnlyOpenAttempts = 5;
    private const int DefaultMaxMainCacheEntries = 256;
    private const long DefaultMaxMainCacheBytes = 384 * 1024;
    private const int DefaultMaxSidecarCacheEntries = 256;
    private const long DefaultMaxSidecarCacheBytes = 384 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex SidecarFileName = new(@"^(?:doc|task)-(?:0|[1-9][0-9]*)\.jsonl$", RegexOptions.CultureInvariant);
    private static readonly Regex ReclaimFileName = new(@"^(?:doc|task)-(?:0|[1-9][0-9]*)\.jsonl\.reclaim$", RegexOptions.CultureInvariant);

    private readonly IFileSystem fs;
    private readonly string directory;
    private readonly string mainPath;
    private readonly bool fsync;
    private readonly bool readOnly;

    // Compact metadata + byte-offset index guarded by indexGate.
    private readonly Lock indexGate = new();
    private readonly Dictionary<string, string> stringPool = new(StringComparer.Ordinal);
    private readonly Dictionary<long, StorageTable> recordTypes = [];
    private readonly Dictionary<long, ConversationRecord> conversations = [];
    private readonly List<long> conversationIds = [];
    private readonly Dictionary<long, List<long>> conversationIdsByOwnerConversation = [];
    private readonly Dictionary<long, List<long>> conversationIdsByOwnerTask = [];
    private readonly Dictionary<long, EntryLocation> entries = [];
    private readonly Dictionary<long, List<long>> entryIds = [];
    private readonly Dictionary<long, List<long>> headEntryIds = [];
    private readonly Dictionary<long, TaskLocation> tasks = [];
    private readonly List<long> taskIds = [];
    private readonly Dictionary<DurableTaskStatus, List<long>> taskIdsByStatus = Enum.GetValues<DurableTaskStatus>().ToDictionary(status => status, _ => new List<long>());
    private readonly Dictionary<long, SubmissionLocation> submissions = [];
    private readonly List<long> submissionIds = [];
    private readonly Dictionary<SubmissionStatus, List<long>> submissionIdsByStatus = Enum.GetValues<SubmissionStatus>().ToDictionary(status => status, _ => new List<long>());
    private readonly Dictionary<long, Dictionary<string, long>> submissionIdsByRequest = [];
    private readonly Dictionary<long, StoredDocumentIndex> documents = [];
    private readonly Dictionary<string, DocumentAddressIndex> documentAddresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<long>> documentIdsByScope = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> fileSizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> sidecarGenerations = new(StringComparer.Ordinal);
    private readonly HashSet<long> currentOnlyDocuments = [];
    private readonly HashSet<long> liveTaskSidecars = [];
    private long nextId = 2;
    private long nextSeq = 1;

    // Read-only snapshot pinning for mutable sidecars (live tasks & active current-only documents) so a concurrent writer
    // reclaiming those sidecars after open cannot invalidate the viewer's opening snapshot. main.jsonl and rewindable
    // document sidecars are strictly append-only and still read lazily by byte offset.
    private readonly Dictionary<long, TaskRecord>? pinnedReadOnlyTasks;
    private readonly Dictionary<long, List<(Seq Seq, DocumentContent Content)>>? pinnedReadOnlyCurrentDocs;

    // Bounded LRU caches for lazily read lines from main.jsonl and sidecars.
    private readonly ByteBudgetLruCache<(long Offset, int Length), JsonArrayValue> mainLineCache = new(DefaultMaxMainCacheEntries, DefaultMaxMainCacheBytes);
    private readonly ByteBudgetLruCache<(string File, int Generation, long Offset, int Length), object> sidecarCache = new(DefaultMaxSidecarCacheEntries, DefaultMaxSidecarCacheBytes);

    // Lazily opened reader for append-only main.jsonl (closed in CloseAsync).
    private readonly SemaphoreSlim mainReaderGate = new(1, 1);
    private IBinaryReader? mainReader;

    private bool closed;
    private long mainSize;
    private JsonlStoragePoisonedException? poisonError;
    private readonly Lock readGate = new();
    /// <summary>Completes once the batches being appended are; index metadata runs ahead while appends run.</summary>
    private Task? writing;
    /// <summary>Per-sidecar reader/reclaimer coordination so reclamation never deletes or replaces a sidecar while a cold read holds it.</summary>
    private readonly Dictionary<string, SidecarGate> sidecarGates = new(StringComparer.Ordinal);

    private sealed class SidecarGate
    {
        public int Readers;
        public bool Reclaiming;
        public TaskCompletionSource? ReadersDrained;
        public TaskCompletionSource? ReclaimDone;
    }

    private readonly struct SidecarReadLease(JsonlStorage owner, string file) : IDisposable
    {
        public void Dispose() => owner?.ReleaseSidecarRead(file);
    }

    private readonly struct SidecarReclaimLease(JsonlStorage owner, string file) : IDisposable
    {
        public void Dispose() => owner?.ReleaseSidecarReclaim(file);
    }

    private JsonlStorage(IFileSystem fs, string directory, string mainPath, JsonlStorageOptions options)
    {
        this.fs = fs;
        this.directory = directory;
        this.mainPath = mainPath;
        fsync = options.Fsync;
        readOnly = options.ReadOnly;
        if (readOnly)
        {
            pinnedReadOnlyTasks = [];
            pinnedReadOnlyCurrentDocs = [];
        }
    }

    /// <summary>Opens or creates a JSONL storage directory using the supplied file system. A read-only open creates and writes nothing;
    /// a recovery failure caused by a concurrent writer's commit is retried.</summary>
    public static async Task<JsonlStorage> OpenAsync(string directory, IFileSystem fs, DurableContext context, JsonlStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(context);
        var absolute = await fs.AbsolutePathAsync(directory, context).ConfigureAwait(false);
        if (!absolute.IsOk) throw ErrorFromFile("path resolution", absolute.Error);
        options ??= new JsonlStorageOptions();
        if (!options.ReadOnly)
        {
            var created = await fs.CreateDirAsync(absolute.Value, new CreateDirOptions { Recursive = true }, context).ConfigureAwait(false);
            if (!created.IsOk) throw ErrorFromFile("directory creation", created.Error);
        }
        var mainPathResult = await fs.JoinPathAsync([absolute.Value, MainFile], context).ConfigureAwait(false);
        if (!mainPathResult.IsOk) throw ErrorFromFile("path join", mainPathResult.Error);
        for (var attempt = 1; ; attempt++)
        {
            var storage = new JsonlStorage(fs, absolute.Value, mainPathResult.Value, options);
            try
            {
                await storage.RecoverAsync(context).ConfigureAwait(false);
                return storage;
            }
            catch (JsonlCorruptionException)
            {
                if (!storage.readOnly || attempt == ReadOnlyOpenAttempts || !await storage.MainChangedAsync(context).ConfigureAwait(false)) throw;
            }
        }
    }

    /// <summary>
    /// Commits <paramref name="batches"/> with one append per affected file: every batch's sidecar records first, then their markers in one
    /// write of main.jsonl. Each batch is checked against the state the earlier ones left, so index metadata applies each one as it passes,
    /// and reads wait until the appends are done; the first batch refused ends the batch. A torn append of the markers recovers a shorter
    /// complete prefix; any failure once a batch applied poisons the storage.
    /// </summary>
    public async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context)
    {
        StorageRules.CheckCommitArguments(batches, receive);
        AssertUsable();
        if (readOnly) throw new JsonlStorageReadOnlyException();
        var seqs = new List<Seq>();
        var encoded = new List<EncodedCommit>();
        var reclamations = new OrderedDictionary<string, ReclaimPlan>(StringComparer.Ordinal);
        Dictionary<(string File, long Offset, int Length), byte[]>? pendingSidecarBytes = batches.Count > 1 ? [] : null;
        Exception? refused = null;
        var writingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (readGate) writing = writingTcs.Task;
        try
        {
            foreach (var writes in batches)
            {
                PreparedBatch prepared;
                try
                {
                    prepared = await PrepareCommitAsync(writes, pendingSidecarBytes, context).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    refused = error;
                    break;
                }
                EncodedCommit commit;
                lock (indexGate)
                {
                    var mainOffset = fileSizes.GetValueOrDefault(MainFile);
                    commit = EncodeCommit(prepared.Seq, prepared.Writes, writes, mainOffset);
                    // A sidecar an earlier batch replaces keeps what later batches append, unless they replace it again;
                    // a later batch appending new live sidecar records cancels any pending deletion from an earlier batch.
                    foreach (var (file, sidecar) in commit.Sidecars)
                    {
                        if (!reclamations.TryGetValue(file, out var existingPlan)) continue;
                        if (existingPlan.Bytes.Length > 0) reclamations[file] = AppendToReclaimPlan(existingPlan, sidecar);
                        else reclamations.Remove(file);
                    }
                    foreach (var (file, plan) in PlanReclamations(prepared.Writes, commit)) reclamations[file] = plan;
                    ApplyCommittedBatch(prepared.Seq, prepared.Writes, prepared.Actions, commit, pendingSidecarBytes);
                    AdoptSidecarState(prepared.Writes);
                    seqs.Add(prepared.Seq);
                    encoded.Add(commit);
                }
            }
            if (encoded.Count > 0) await AppendAsync(encoded, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Index metadata runs ahead of the file: once a batch applied, any failure poisons the storage before reads waiting on the append
            // proceed, so none of them returns uncommitted state.
            if (seqs.Count == 0) throw;
            throw Poison(error);
        }
        finally
        {
            lock (readGate) if (writing == writingTcs.Task) writing = null;
            writingTcs.SetResult();
        }
        // The markers are appended: the batches are durable. Reclaiming sidecars comes after the receipts.
        for (var index = 0; index < seqs.Count; index++) receive(index, seqs[index]);
        await ReclaimSidecarsAsync(reclamations, context).ConfigureAwait(false);
        return new CommitResult(seqs.Count, refused);
    }

    /// <summary>Appends the sidecar records of <paramref name="commits"/>, flushed when <see cref="JsonlStorageOptions.Fsync"/> is set, then
    /// their markers in one write of main.jsonl.</summary>
    private async Task AppendAsync(List<EncodedCommit> commits, DurableContext context)
    {
        var contents = new OrderedDictionary<string, List<byte[]>>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            foreach (var (file, sidecar) in commit.Sidecars)
            {
                if (!contents.TryGetValue(file, out var chunks)) contents.Add(file, chunks = []);
                chunks.Add(sidecar.Bytes);
            }
        }
        var resolving = new List<Task<string>>(contents.Count);
        foreach (var (file, _) in contents) resolving.Add(ResolveFileAsync(file, context));
        var paths = await Task.WhenAll(resolving).ConfigureAwait(false);
        var sidecars = new List<(string File, byte[] Content, string Path)>(contents.Count);
        var at = 0;
        foreach (var (file, chunks) in contents) sidecars.Add((file, ConcatBytes(chunks), paths[at++]));

        foreach (var sidecar in sidecars)
        {
            var result = await fs.AppendFileAsync(sidecar.Path, sidecar.Content, context).ConfigureAwait(false);
            if (!result.IsOk) throw Poison(ErrorFromFile($"append to {sidecar.File}", result.Error));
        }
        if (fsync)
        {
            foreach (var sidecar in sidecars)
            {
                var result = await fs.FlushFileAsync(sidecar.Path, context).ConfigureAwait(false);
                if (!result.IsOk) throw Poison(ErrorFromFile($"flush of {sidecar.File}", result.Error));
            }
        }
        var markerBytes = commits.Count == 1 ? commits[0].MarkerBytes : ConcatBytes(commits.Select(commit => commit.MarkerBytes));
        var marker = await fs.AppendFileAsync(mainPath, markerBytes, context).ConfigureAwait(false);
        if (!marker.IsOk) throw Poison(ErrorFromFile($"append to {MainFile}", marker.Error));
    }

    /// <inheritdoc/>
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId>
    {
        try
        {
            AssertUsable();
            lock (indexGate)
            {
                if (!Ids.IsSafeInteger(nextId)) throw new InvalidOperationException("ID space is exhausted");
                return Task.FromResult(TId.FromNumber(nextId++));
            }
        }
        catch (Exception error)
        {
            return Task.FromException<TId>(error);
        }
    }

    /// <inheritdoc/>
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) =>
        ReadableSync(() => conversations.GetValueOrDefault(id.Value));

    /// <inheritdoc/>
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        ReadableSync(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            IReadOnlyList<long> ids = query.OwnerTaskId is { } ownerTask ? conversationIdsByOwnerTask.GetValueOrDefault(ownerTask.Value) ?? []
                : query.OwnerConversationId is { } ownerConversation ? conversationIdsByOwnerConversation.GetValueOrDefault(ownerConversation.Value) ?? []
                : conversationIds;
            var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
            var values = new List<ConversationRecord>();
            foreach (var index in JsonlSortedIndex.ScanIndexes(ids, start))
            {
                if (values.Count > limit) break;
                var value = conversations[ids[index]];
                if (query.OwnerConversationId is { } owner && value.Owner?.ConversationId != owner) continue;
                values.Add(value);
            }
            return StorageRules.MakePage(values, limit, start.Order, value => value.Id.Value);
        });

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) =>
        ReadableSnapshotAsync(
            () => entries.TryGetValue(id.Value, out var location) ? location : (EntryLocation?)null,
            async found => found is { } location
                ? new CommittedEntry(await LoadEntryAsync(location, context).ConfigureAwait(false), location.CommitSeq)
                : null);

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        ReadableSnapshotAsync(
            () => FindVisibleEntryLocation(conversationId, id.Value),
            async found => found is { } location
                ? new CommittedEntry(await LoadEntryAsync(location, context).ConfigureAwait(false), location.CommitSeq)
                : null);

    /// <inheritdoc/>
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        ReadableSnapshotAsync(
            () =>
            {
                if (!conversations.ContainsKey(conversationId.Value))
                    throw new StorageRequestException($"Unknown conversation: {conversationId}");
                var currentId = conversationId.Value;
                var upperEntryId = atOrBeforeEntryId?.Value ?? long.MaxValue;
                while (true)
                {
                    var ids = headEntryIds.GetValueOrDefault(currentId) ?? [];
                    var index = JsonlSortedIndex.UpperBound(ids, upperEntryId) - 1;
                    if (index >= 0) return (EntryLocation?)entries[ids[index]];
                    var conversation = conversations[currentId];
                    if (conversation.Parent is not { } parent) return null;
                    upperEntryId = Math.Min(upperEntryId, parent.At.Value);
                    currentId = parent.ConversationId.Value;
                }
            },
            async found => found is { } location ? await LoadEntryAsync(location, context).ConfigureAwait(false) : null);

    /// <inheritdoc/>
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (order, after) = StorageScan.Start(query.Order, cursor, ScanOrder.Descending);
        var minEntryId = query.MinEntryId?.Value;
        var maxEntryId = query.MaxEntryId?.Value;
        if (after is { } descendingAfter && order == ScanOrder.Descending) maxEntryId = Math.Min(maxEntryId ?? descendingAfter, descendingAfter - 1);
        if (after is { } ascendingAfter && order == ScanOrder.Ascending) minEntryId = Math.Max(minEntryId ?? ascendingAfter, ascendingAfter + 1);
        return ReadableSnapshotAsync(
            () =>
            {
                var locations = new List<EntryLocation>();
                var ids = order == ScanOrder.Descending
                    ? VisibleEntryIdsDescending(query.ConversationId, minEntryId ?? long.MinValue, maxEntryId ?? long.MaxValue)
                    : VisibleEntryIdsAscending(query.ConversationId, minEntryId ?? long.MinValue, maxEntryId ?? long.MaxValue);
                foreach (var entryId in ids)
                {
                    locations.Add(entries[entryId]);
                    if (locations.Count > limit) break;
                }
                return locations;
            },
            async locations =>
            {
                var visible = new List<EntryRecord>(locations.Count);
                foreach (var location in locations) visible.Add(await LoadEntryAsync(location, context).ConfigureAwait(false));
                return StorageRules.MakePage(visible, limit, order, entry => entry.Id.Value);
            });
    }

    /// <inheritdoc/>
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
        ReadableSnapshotAsync(
            () => tasks.TryGetValue(id.Value, out var location)
                ? (Found: true, Location: location, Generation: sidecarGenerations.GetValueOrDefault(SidecarName("task", id.Value)))
                : (Found: false, Location: default(TaskLocation), Generation: 0),
            async snapshot => snapshot.Found
                ? await LoadTaskAsync(id.Value, snapshot.Location, snapshot.Generation, context).ConfigureAwait(false)
                : null);

    /// <inheritdoc/>
    public async Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        while (true)
        {
            var matched = await ReadableSnapshotAsync(
                () =>
                {
                    var selected = new List<(long Id, TaskLocation Location, int Generation)>();
                    var ids = query.Status is { } status ? taskIdsByStatus[status] : taskIds;
                    foreach (var index in JsonlSortedIndex.ScanIndexes(ids, start))
                    {
                        if (selected.Count > limit) break;
                        var taskId = ids[index];
                        var meta = tasks[taskId];
                        if (query.ConversationId is { } conversationId && meta.ConversationId != conversationId) continue;
                        if (query.Kind is { } kind && !string.Equals(meta.Kind, kind, StringComparison.Ordinal)) continue;
                        if (query.AbortRequested is { } abortRequested && meta.AbortRequested != abortRequested) continue;
                        if (query.Background is { } background && meta.Background != background) continue;
                        selected.Add((taskId, meta, meta.InMain ? 0 : sidecarGenerations.GetValueOrDefault(SidecarName("task", taskId))));
                    }
                    return selected;
                },
                static selected => Task.FromResult(selected)).ConfigureAwait(false);

            var values = new List<TaskRecord>(matched.Count);
            var stale = false;
            foreach (var (taskId, location, generation) in matched)
            {
                var (loaded, record) = await TryLoadTaskAtSnapshotAsync(taskId, location, generation, context).ConfigureAwait(false);
                if (!loaded)
                {
                    stale = true;
                    break;
                }
                values.Add(record!);
            }
            if (!stale) return StorageRules.MakePage(values, limit, start.Order, value => value.Id.Value);
        }
    }

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) =>
        ReadableSnapshotAsync(
            () => submissions.TryGetValue(id.Value, out var location) ? location : (SubmissionLocation?)null,
            async found => found is { } location ? await LoadSubmissionAsync(location, context).ConfigureAwait(false) : null);

    /// <inheritdoc/>
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Ascending);
        return ReadableSnapshotAsync(
            () =>
            {
                var matched = new List<SubmissionLocation>();
                var ids = query.Status is { } status ? submissionIdsByStatus[status] : submissionIds;
                foreach (var index in JsonlSortedIndex.ScanIndexes(ids, start))
                {
                    if (matched.Count > limit) break;
                    var meta = submissions[ids[index]];
                    if (query.ConversationId is { } conversationId && meta.ConversationId != conversationId) continue;
                    matched.Add(meta);
                }
                return matched;
            },
            async matched =>
            {
                var values = new List<SubmissionRecord>(matched.Count);
                foreach (var location in matched) values.Add(await LoadSubmissionAsync(location, context).ConfigureAwait(false));
                return StorageRules.MakePage(values, limit, start.Order, value => value.Id.Value);
            });
    }

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        return ReadableSnapshotAsync(
            () => submissionIdsByRequest.TryGetValue(conversationId.Value, out var requests) && requests.TryGetValue(requestId, out var id)
                ? submissions[id]
                : (SubmissionLocation?)null,
            async found => found is { } location ? await LoadSubmissionAsync(location, context).ConfigureAwait(false) : null);
    }

    /// <inheritdoc/>
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        ReadableSync(() =>
        {
            ArgumentNullException.ThrowIfNull(address);
            var index = documentAddresses.GetValueOrDefault(StorageRules.AddressKey(address.Kind, address.Scope, address.Key));
            if (at.IsCurrent) return index?.CurrentId is { } currentId ? documents[currentId].Record : null;
            foreach (var id in index?.Ids ?? [])
            {
                var record = documents[id].Record;
                if (StorageRules.IsAliveAt(record, at)) return record;
            }
            return null;
        });

    /// <inheritdoc/>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        MaterializeDocumentAsync(id, at, pendingSidecarBytes: null, insideCommit: false, context);

    /// <inheritdoc/>
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        ReadableSync(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var ids = documentIdsByScope.GetValueOrDefault(StorageRules.ScopeKey(query.Scope)) ?? [];
            var after = StorageRules.CursorId(cursor);
            var values = new List<DocumentRecord>();
            for (var index = after is { } last ? JsonlSortedIndex.UpperBound(ids, last) : 0; index < ids.Count && values.Count <= limit; index++)
            {
                var record = documents[ids[index]].Record;
                if (query.Kind is { } kind && !string.Equals(record.Kind, kind, StringComparison.Ordinal)) continue;
                if (StorageRules.IsAliveAt(record, query.At)) values.Add(record);
            }
            return StorageRules.MakePage(values, limit, ScanOrder.Ascending, record => record.Id.Value);
        });

    /// <inheritdoc/>
    public async Task CloseAsync(DurableContext context)
    {
        if (closed) return;
        closed = true;
        mainLineCache.Clear();
        sidecarCache.Clear();
        IBinaryReader? readerToClose;
        await mainReaderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            readerToClose = mainReader;
            mainReader = null;
        }
        finally
        {
            mainReaderGate.Release();
        }
        if (readerToClose is not null) await readerToClose.CloseAsync(context).ConfigureAwait(false);
    }

    // ---- Batch preparation & encoding ----

    private sealed record PreparedBatch(Seq Seq, ImmutableArray<StorageWrite> Writes, OrderedDictionary<long, DocumentBatchAction> Actions);

    private sealed record EncodedSidecarLine(long Offset, int Length, byte[] Bytes, Seq Seq, int Version, bool IsBase, long RecordId);

    private sealed record EncodedSidecar(byte[] Bytes, List<EncodedSidecarLine> Lines);

    private sealed record EncodedCommit(
        byte[] MarkerBytes,
        long MainLineOffset,
        int MainLineLength,
        OrderedDictionary<string, EncodedSidecar> Sidecars,
        Dictionary<int, EncodedSidecarLine> SidecarLinesByWriteIndex);

    private sealed record ReclaimPlan(long DocumentId, byte[] Bytes, List<RevisionLocation>? RewrittenRevisions);

    private async Task<PreparedBatch> PrepareCommitAsync(IReadOnlyList<StorageWrite> writes,
        Dictionary<(string File, long Offset, int Length), byte[]>? pendingSidecarBytes, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(writes);
        foreach (var write in writes) ArgumentNullException.ThrowIfNull(write, nameof(writes));

        var detached = await ResolveDocumentCopiesAsync(writes, pendingSidecarBytes, context).ConfigureAwait(false);
        lock (indexGate)
        {
            AssertUsable();
            var commitSeq = new Seq(nextSeq);
            if (!Ids.IsSafeInteger(commitSeq.Value) || commitSeq.Value < nextSeq)
                throw new InvalidOperationException($"Commit sequence {commitSeq} does not strictly increase");
            StorageRules.CheckGlobalIds(detached, id => recordTypes.TryGetValue(id, out var table) ? table : null);
            var actions = StorageRules.PrepareDocumentActions(detached);
            StorageRules.CheckDocumentActions(actions,
                id => documents.TryGetValue(id, out var stored) ? new DocumentCheckState(stored.Record, stored.Revisions.Count > 0 ? stored.Revisions[^1].Version : null) : null,
                key => documentAddresses.GetValueOrDefault(key)?.CurrentId);
            return new PreparedBatch(commitSeq, detached, actions);
        }
    }

    private async Task<ImmutableArray<StorageWrite>> ResolveDocumentCopiesAsync(IReadOnlyList<StorageWrite> writes,
        Dictionary<(string File, long Offset, int Length), byte[]>? pendingSidecarBytes, DurableContext context)
    {
        if (!writes.Any(write => write is DocumentCopyWrite)) return [.. writes];
        var changedDocumentIds = new HashSet<long>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case DocumentCreateWrite create: changedDocumentIds.Add(create.Record.Id.Value); break;
                case DocumentCopyWrite copy: changedDocumentIds.Add(copy.Record.Id.Value); break;
                case DocumentChangeWrite change: changedDocumentIds.Add(change.Id.Value); break;
                case DocumentRetireWrite retire: changedDocumentIds.Add(retire.Id.Value); break;
            }
        }
        var resolved = ImmutableArray.CreateBuilder<StorageWrite>(writes.Count);
        foreach (var write in writes)
        {
            if (write is not DocumentCopyWrite copy)
            {
                resolved.Add(write);
                continue;
            }
            try
            {
                var sourceId = copy.Source.Id;
                if (changedDocumentIds.Contains(sourceId.Value))
                    throw new InvalidOperationException($"Fork source document {sourceId} is changed in the copy batch");
                var stored = await MaterializeDocumentAsync(sourceId, copy.Source.At, pendingSidecarBytes, insideCommit: true, context).ConfigureAwait(false) ??
                    throw new InvalidOperationException($"Fork source document {sourceId} cannot be read");
                StorageRules.CheckCopySource(stored, copy.Record);
                resolved.Add(new DocumentCreateWrite(copy.Record, DocumentContent.Base(stored.Version, stored.Value)));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Document copy {copy.Record.Id} was rejected", error);
            }
        }
        return resolved.MoveToImmutable();
    }

    private EncodedCommit EncodeCommit(Seq seq, ImmutableArray<StorageWrite> writes, IReadOnlyList<StorageWrite> submitted, long mainOffset)
    {
        var mainWrites = new List<JsonValue>(writes.Length);
        var perFileLines = new OrderedDictionary<string, List<EncodedSidecarLine>>(StringComparer.Ordinal);
        var perFileOffset = new Dictionary<string, long>(StringComparer.Ordinal);
        var byWriteIndex = new Dictionary<int, EncodedSidecarLine>();
        var nextOrdinal = 0L;

        long AddSidecar(int writeIndex, string file, JsonObjectValue payload, int version, bool isBase, long recordId)
        {
            var ordinal = nextOrdinal++;
            var lineJson = Object(("format", JsonValue.Number(FormatVersion)), ("type", JsonValue.String("record")), ("seq", seq.ToJson()),
                ("ordinal", JsonValue.Number(ordinal)), ("payload", payload));
            var lineBytes = Utf8.GetBytes(JsonLine(lineJson));
            if (!perFileOffset.TryGetValue(file, out var offset)) offset = fileSizes.GetValueOrDefault(file);
            var encodedLine = new EncodedSidecarLine(offset, lineBytes.Length - 1, lineBytes, seq, version, isBase, recordId);
            perFileOffset[file] = offset + lineBytes.Length;
            if (!perFileLines.TryGetValue(file, out var list)) perFileLines.Add(file, list = []);
            list.Add(encodedLine);
            byWriteIndex[writeIndex] = encodedLine;
            return ordinal;
        }

        for (var index = 0; index < writes.Length; index++)
        {
            switch (writes[index])
            {
                case ConversationWrite or EntryWrite or SubmissionWrite or DocumentRetireWrite:
                    mainWrites.Add(writes[index].ToJson());
                    break;
                case TaskWrite task:
                    if (task.Value.State.Status == DurableTaskStatus.Terminal) mainWrites.Add(task.ToJson());
                    else
                    {
                        var ordinal = AddSidecar(index, SidecarName("task", task.Value.Id.Value),
                            Object(("type", JsonValue.String("task")), ("value", task.Value.ToJson())),
                            version: 0, isBase: false, recordId: task.Value.Id.Value);
                        mainWrites.Add(Object(("type", JsonValue.String("task.sidecar")), ("id", task.Value.Id.ToJson()), ("ordinal", JsonValue.Number(ordinal))));
                    }
                    break;
                case DocumentCreateWrite create:
                {
                    // Keep upstream's exact key spelling: resolved copies spell {kind, version, value}, direct bases spell {version, kind, value}.
                    var content = index < submitted.Count && submitted[index] is DocumentCopyWrite
                        ? Object(("kind", JsonValue.String("base")), ("version", JsonValue.Number(create.Content.Version)), ("value", create.Content.Value))
                        : create.Content.ToJson();
                    var ordinal = AddSidecar(index, SidecarName("doc", create.Record.Id.Value),
                        Object(("type", JsonValue.String("document")), ("id", create.Record.Id.ToJson()), ("content", content)),
                        create.Content.Version, isBase: true, recordId: create.Record.Id.Value);
                    mainWrites.Add(Object(("type", JsonValue.String("document.create")), ("record", create.Record.ToJson()), ("ordinal", JsonValue.Number(ordinal))));
                    break;
                }
                case DocumentChangeWrite change:
                {
                    var ordinal = AddSidecar(index, SidecarName("doc", change.Id.Value),
                        Object(("type", JsonValue.String("document")), ("id", change.Id.ToJson()), ("content", change.Content.ToJson())),
                        change.Content.Version, isBase: change.Content.Kind == DocumentContentKind.Base, recordId: change.Id.Value);
                    mainWrites.Add(Object(("type", JsonValue.String("document.change")), ("id", change.Id.ToJson()), ("ordinal", JsonValue.Number(ordinal))));
                    break;
                }
                default:
                    throw new InvalidOperationException("Prepared document copy was not resolved");
            }
        }

        var sidecars = new OrderedDictionary<string, EncodedSidecar>(StringComparer.Ordinal);
        foreach (var (file, lines) in perFileLines)
        {
            var combined = lines.Count == 1 ? lines[0].Bytes : ConcatBytes(lines.Select(line => line.Bytes));
            sidecars.Add(file, new EncodedSidecar(combined, lines));
        }
        var marker = Object(("format", JsonValue.Number(FormatVersion)), ("type", JsonValue.String("commit")), ("seq", seq.ToJson()),
            ("writes", JsonValue.Array(mainWrites)));
        var markerBytes = Utf8.GetBytes(JsonLine(marker));
        return new EncodedCommit(markerBytes, mainOffset, markerBytes.Length - 1, sidecars, byWriteIndex);
    }

    private static ReclaimPlan AppendToReclaimPlan(ReclaimPlan existing, EncodedSidecar appended)
    {
        var combined = ConcatBytes([existing.Bytes, appended.Bytes]);
        if (existing.RewrittenRevisions is null) return existing with { Bytes = combined };
        var offset = (long)existing.Bytes.Length;
        var rewritten = new List<RevisionLocation>(existing.RewrittenRevisions.Count + appended.Lines.Count);
        rewritten.AddRange(existing.RewrittenRevisions);
        foreach (var line in appended.Lines)
        {
            rewritten.Add(new RevisionLocation(line.Seq, line.Version, line.IsBase, offset, line.Length));
            offset += line.Bytes.Length;
        }
        return existing with { Bytes = combined, RewrittenRevisions = rewritten };
    }

    private OrderedDictionary<string, ReclaimPlan> PlanReclamations(ImmutableArray<StorageWrite> writes, EncodedCommit encoded)
    {
        var createdCurrentOnlyDocuments = new HashSet<long>();
        var retiredDocuments = new List<long>();
        var baseDocuments = new List<long>();
        var finalTasks = new OrderedDictionary<long, TaskRecord>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case DocumentCreateWrite create:
                    if (IsCurrentOnly(create.Record)) createdCurrentOnlyDocuments.Add(create.Record.Id.Value);
                    break;
                case DocumentChangeWrite change:
                    if (change.Content.Kind == DocumentContentKind.Base && !baseDocuments.Contains(change.Id.Value)) baseDocuments.Add(change.Id.Value);
                    break;
                case DocumentRetireWrite retire:
                    if (!retiredDocuments.Contains(retire.Id.Value)) retiredDocuments.Add(retire.Id.Value);
                    break;
                case TaskWrite task:
                    finalTasks[task.Value.Id.Value] = task.Value;
                    break;
            }
        }

        var replacements = new OrderedDictionary<string, ReclaimPlan>(StringComparer.Ordinal);
        bool IsCurrentOnlyDocument(long id) => currentOnlyDocuments.Contains(id) || createdCurrentOnlyDocuments.Contains(id);
        foreach (var id in retiredDocuments)
            if (IsCurrentOnlyDocument(id)) replacements[SidecarName("doc", id)] = new ReclaimPlan(id, [], null);
        foreach (var id in baseDocuments)
        {
            if (!IsCurrentOnlyDocument(id) || retiredDocuments.Contains(id)) continue;
            var file = SidecarName("doc", id);
            if (encoded.Sidecars.TryGetValue(file, out var sidecar))
            {
                var rewritten = new List<RevisionLocation>(sidecar.Lines.Count);
                var offset = 0L;
                foreach (var line in sidecar.Lines)
                {
                    rewritten.Add(new RevisionLocation(line.Seq, line.Version, line.IsBase, offset, line.Length));
                    offset += line.Bytes.Length;
                }
                replacements[file] = new ReclaimPlan(id, sidecar.Bytes, rewritten);
            }
        }
        foreach (var (id, task) in finalTasks)
        {
            if (task.State.Status == DurableTaskStatus.Terminal && (liveTaskSidecars.Contains(id) || encoded.Sidecars.ContainsKey(SidecarName("task", id))))
                replacements[SidecarName("task", id)] = new ReclaimPlan(-1, [], null);
        }
        return replacements;
    }

    private void ApplyCommittedBatch(Seq seq, ImmutableArray<StorageWrite> writes, OrderedDictionary<long, DocumentBatchAction> documentActions,
        EncodedCommit commit, Dictionary<(string File, long Offset, int Length), byte[]>? pendingSidecarBytes)
    {
        var docRevisionsById = new Dictionary<long, RevisionLocation>();
        for (var index = 0; index < writes.Length; index++)
        {
            switch (writes[index])
            {
                case ConversationWrite { Value: var value }:
                    ApplyConversation(value);
                    break;
                case EntryWrite { Value: var value }:
                    ApplyEntry(value, new EntryLocation(seq, commit.MainLineOffset, commit.MainLineLength, index));
                    break;
                case TaskWrite { Value: var value }:
                    if (value.State.Status == DurableTaskStatus.Terminal)
                    {
                        ApplyTask(value, new TaskLocation(value.ConversationId, Intern(value.Kind), value.State.Status, value.AbortRequested,
                            value.Background, InMain: true, commit.MainLineOffset, commit.MainLineLength, index));
                    }
                    else
                    {
                        var line = commit.SidecarLinesByWriteIndex[index];
                        ApplyTask(value, new TaskLocation(value.ConversationId, Intern(value.Kind), value.State.Status, value.AbortRequested,
                            value.Background, InMain: false, line.Offset, line.Length, WriteIndex: -1));
                    }
                    break;
                case SubmissionWrite { Value: var value }:
                    ApplySubmission(value, new SubmissionLocation(value.ConversationId, value.Status, value.RequestId,
                        commit.MainLineOffset, commit.MainLineLength, index));
                    break;
                case DocumentCreateWrite create:
                {
                    var line = commit.SidecarLinesByWriteIndex[index];
                    docRevisionsById[create.Record.Id.Value] = new RevisionLocation(seq, create.Content.Version, IsBase: true, line.Offset, line.Length);
                    break;
                }
                case DocumentChangeWrite change:
                {
                    var line = commit.SidecarLinesByWriteIndex[index];
                    docRevisionsById[change.Id.Value] = new RevisionLocation(seq, change.Content.Version, change.Content.Kind == DocumentContentKind.Base, line.Offset, line.Length);
                    break;
                }
            }
        }
        ApplyDocumentActions(documentActions, seq, docRevisionsById);
        nextSeq = seq.Value + 1;
        fileSizes[MainFile] = commit.MainLineOffset + commit.MarkerBytes.Length;
        foreach (var (file, sidecar) in commit.Sidecars)
        {
            fileSizes[file] = fileSizes.GetValueOrDefault(file) + sidecar.Bytes.Length;
            if (pendingSidecarBytes is not null)
            {
                foreach (var line in sidecar.Lines)
                    pendingSidecarBytes[(file, line.Offset, line.Length)] = line.Bytes[..line.Length];
            }
        }
    }

    private void AdoptSidecarState(ImmutableArray<StorageWrite> writes)
    {
        foreach (var write in writes)
        {
            if (write is DocumentCreateWrite create)
            {
                if (IsCurrentOnly(create.Record)) currentOnlyDocuments.Add(create.Record.Id.Value);
            }
            else if (write is TaskWrite task)
            {
                if (task.Value.State.Status == DurableTaskStatus.Terminal) liveTaskSidecars.Remove(task.Value.Id.Value);
                else liveTaskSidecars.Add(task.Value.Id.Value);
            }
        }
    }

    // ---- Index mutation helpers (called under indexGate or during single-threaded RecoverAsync) ----

    private string Intern(string value)
    {
        if (stringPool.TryGetValue(value, out var existing)) return existing;
        stringPool[value] = value;
        return value;
    }

    private void ApplyConversation(ConversationRecord value)
    {
        var id = value.Id.Value;
        recordTypes[id] = StorageTable.Conversation;
        conversations[id] = value;
        JsonlSortedIndex.InsertSorted(conversationIds, id);
        if (value.Owner is { } owner)
        {
            JsonlSortedIndex.InsertMapId(conversationIdsByOwnerConversation, owner.ConversationId.Value, id);
            JsonlSortedIndex.InsertMapId(conversationIdsByOwnerTask, owner.TaskId.Value, id);
        }
        nextId = Math.Max(nextId, id + 1);
    }

    private void ApplyEntry(EntryRecord value, EntryLocation location)
    {
        var id = value.Id.Value;
        recordTypes[id] = StorageTable.Entry;
        entries[id] = location;
        JsonlSortedIndex.InsertMapId(entryIds, value.ConversationId.Value, id);
        if (value.Head is not null) JsonlSortedIndex.InsertMapId(headEntryIds, value.ConversationId.Value, id);
        nextId = Math.Max(nextId, id + 1);
    }

    private void ApplyTask(TaskRecord value, TaskLocation location)
    {
        var id = value.Id.Value;
        recordTypes[id] = StorageTable.Task;
        if (!tasks.TryGetValue(id, out var previous))
        {
            JsonlSortedIndex.InsertSorted(taskIds, id);
            JsonlSortedIndex.InsertSorted(taskIdsByStatus[location.Status], id);
        }
        else if (previous.Status != location.Status)
        {
            JsonlSortedIndex.RemoveSorted(taskIdsByStatus[previous.Status], id);
            JsonlSortedIndex.InsertSorted(taskIdsByStatus[location.Status], id);
        }
        tasks[id] = location;
        nextId = Math.Max(nextId, id + 1);
    }

    private void ApplySubmission(SubmissionRecord value, SubmissionLocation location)
    {
        var id = value.Id.Value;
        recordTypes[id] = StorageTable.Submission;
        var hasPrevious = submissions.TryGetValue(id, out var previous);
        if (!hasPrevious)
        {
            JsonlSortedIndex.InsertSorted(submissionIds, id);
            JsonlSortedIndex.InsertSorted(submissionIdsByStatus[location.Status], id);
        }
        else if (previous.Status != location.Status)
        {
            JsonlSortedIndex.RemoveSorted(submissionIdsByStatus[previous.Status], id);
            JsonlSortedIndex.InsertSorted(submissionIdsByStatus[location.Status], id);
        }
        if (hasPrevious && previous.RequestId is { } previousRequest &&
            submissionIdsByRequest.TryGetValue(previous.ConversationId.Value, out var previousRequests) &&
            previousRequests.TryGetValue(previousRequest, out var previousOwner) && previousOwner == id)
        {
            previousRequests.Remove(previousRequest);
            if (previousRequests.Count == 0) submissionIdsByRequest.Remove(previous.ConversationId.Value);
        }
        submissions[id] = location;
        if (location.RequestId is { } requestId)
        {
            if (!submissionIdsByRequest.TryGetValue(location.ConversationId.Value, out var requests))
                submissionIdsByRequest[location.ConversationId.Value] = requests = new(StringComparer.Ordinal);
            requests[requestId] = id;
        }
        nextId = Math.Max(nextId, id + 1);
    }

    private void ApplyDocumentActions(OrderedDictionary<long, DocumentBatchAction> actions, Seq seq, Dictionary<long, RevisionLocation> revisionsById)
    {
        foreach (var (id, action) in actions)
        {
            StoredDocumentIndex stored;
            if (action.Create is not null)
            {
                var created = action.Create with { Kind = Intern(action.Create.Kind) };
                var record = created.ToRecord(seq, action.Retire ? seq : null);
                var revisions = revisionsById.TryGetValue(id, out var revision) ? new List<RevisionLocation>(1) { revision } : [];
                stored = new StoredDocumentIndex(record, revisions);
                recordTypes[id] = StorageTable.Document;
                documents[id] = stored;
                var key = StorageRules.AddressKey(record.Kind, record.Scope, record.Key);
                if (!documentAddresses.TryGetValue(key, out var address)) documentAddresses[key] = address = new DocumentAddressIndex();
                JsonlSortedIndex.InsertSorted(address.Ids, id);
                var scopeKey = StorageRules.ScopeKey(record.Scope);
                if (!documentIdsByScope.TryGetValue(scopeKey, out var scopeIds)) documentIdsByScope[scopeKey] = scopeIds = [];
                JsonlSortedIndex.InsertSorted(scopeIds, id);
                nextId = Math.Max(nextId, id + 1);
            }
            else
            {
                stored = documents[id];
                if (action.Content is not null && revisionsById.TryGetValue(id, out var revision))
                {
                    if (revision.IsBase && StorageRules.IsCurrentOnly(stored.Record))
                        stored.Revisions = new List<RevisionLocation>(1) { revision };
                    else
                        stored.Revisions.Add(revision);
                }
            }

            if (action.Retire && action.Create is null) stored.Record = stored.Record with { RetiredAt = seq };
            if (action.Retire && StorageRules.IsCurrentOnly(stored.Record)) stored.Revisions = [];
            if (action.Create is not null || action.Retire)
            {
                var address = documentAddresses[StorageRules.AddressKey(stored.Record.Kind, stored.Record.Scope, stored.Record.Key)];
                if (action.Retire && address.CurrentId == id) address.CurrentId = null;
                if (action.Create is not null && !action.Retire) address.CurrentId = id;
            }
        }
    }

    // ---- Sidecar reclamation ----

    /// <summary>The marker already published this state, so reclamation is retryable best-effort maintenance.</summary>
    private async Task ReclaimSidecarsAsync(OrderedDictionary<string, ReclaimPlan> replacements, DurableContext context)
    {
        if (replacements.Count == 0) return;
        if (fsync)
        {
            var flushed = await fs.FlushFileAsync(mainPath, context).ConfigureAwait(false);
            if (!flushed.IsOk) return;
        }
        foreach (var (file, plan) in replacements) await ReplaceSidecarAsync(file, plan, context).ConfigureAwait(false);
    }

    private async Task ReplaceSidecarAsync(string file, ReclaimPlan plan, DurableContext context)
    {
        var path = await fs.JoinPathAsync([directory, file], context).ConfigureAwait(false);
        if (!path.IsOk) return;
        if (plan.Bytes.Length == 0)
        {
            using (await AcquireSidecarReclaimAsync(file).ConfigureAwait(false))
            {
                var removed = await fs.RemoveAsync(path.Value, new RemoveOptions { Force = true }, context).ConfigureAwait(false);
                if (removed.IsOk)
                {
                    ClearRemovedSidecarIndex(file, plan.DocumentId);
                }
                else
                {
                    var info = await fs.FileInfoAsync(path.Value, context).ConfigureAwait(false);
                    if (info.IsOk)
                    {
                        // Confirmed still present on disk: retain its actual size so future appends index at EOF.
                        lock (indexGate) fileSizes[file] = info.Value.Size;
                    }
                    else if (info.Error.Code == FileErrorCode.NotFound)
                    {
                        // Post-removal failure (FailureMode.After): the file is confirmed absent.
                        ClearRemovedSidecarIndex(file, plan.DocumentId);
                    }
                    else
                    {
                        // On-disk state cannot be verified; require reopening before further reads/appends use stale sizes.
                        Poison(ErrorFromFile($"reclaim removal check of {file}", info.Error));
                    }
                }
            }
            return;
        }
        var temporaryPath = await fs.JoinPathAsync([directory, file + ReclaimSuffix], context).ConfigureAwait(false);
        if (!temporaryPath.IsOk) return;
        var written = await fs.WriteFileAsync(temporaryPath.Value, plan.Bytes, context).ConfigureAwait(false);
        if (!written.IsOk) return;
        if (fsync)
        {
            var flushed = await fs.FlushFileAsync(temporaryPath.Value, context).ConfigureAwait(false);
            if (!flushed.IsOk) return;
        }

        using (await AcquireSidecarReclaimAsync(file).ConfigureAwait(false))
        {
            var renamed = await fs.RenameFileAsync(temporaryPath.Value, path.Value, context).ConfigureAwait(false);
            if (renamed.IsOk)
            {
                ApplyReclaimedSidecarIndex(file, plan);
            }
            else
            {
                var info = await fs.FileInfoAsync(path.Value, context).ConfigureAwait(false);
                long previousSize;
                lock (indexGate) previousSize = fileSizes.GetValueOrDefault(file, -1);
                if (info.IsOk && info.Value.Size == plan.Bytes.Length && info.Value.Size != previousSize)
                {
                    // Confirmed post-rename failure (FailureMode.After): file was replaced on disk.
                    ApplyReclaimedSidecarIndex(file, plan);
                }
                else if (info.IsOk && info.Value.Size == previousSize && info.Value.Size != plan.Bytes.Length)
                {
                    // Confirmed pre-rename failure (FailureMode.Before): original file and offsets remain intact.
                }
                else
                {
                    // Inconclusive rename state: do not allow subsequent reads/appends to use potentially stale offsets.
                    Poison(ErrorFromFile($"reclaim rename verification of {file}", info.IsOk ? renamed.Error : info.Error));
                }
            }
        }
    }

    private void ClearRemovedSidecarIndex(string file, long documentId)
    {
        lock (indexGate)
        {
            fileSizes.Remove(file);
            sidecarGenerations[file] = sidecarGenerations.GetValueOrDefault(file) + 1;
            if (documentId >= 0 && documents.TryGetValue(documentId, out var doc)) doc.Generation++;
        }
        sidecarCache.RemoveWhere(key => string.Equals(key.File, file, StringComparison.Ordinal));
    }

    private void ApplyReclaimedSidecarIndex(string file, ReclaimPlan plan)
    {
        lock (indexGate)
        {
            fileSizes[file] = plan.Bytes.Length;
            sidecarGenerations[file] = sidecarGenerations.GetValueOrDefault(file) + 1;
            if (plan.DocumentId >= 0 && plan.RewrittenRevisions is { } rewritten && documents.TryGetValue(plan.DocumentId, out var doc))
            {
                doc.Revisions = [.. rewritten];
                doc.Generation++;
            }
        }
        sidecarCache.RemoveWhere(key => string.Equals(key.File, file, StringComparison.Ordinal));
    }

    // ---- Recovery ----

    private async Task RecoverAsync(DurableContext context)
    {
        var main = await ReadLinesAsync(mainPath, MainFile, context, ParseMainMarker).ConfigureAwait(false);
        mainSize = main.Size;
        var previousSeq = 0L;
        foreach (var marker in main.Lines)
        {
            if (marker.Value.Seq.Value <= previousSeq) throw new JsonlCorruptionException($"Commit sequence does not strictly increase in {MainFile}");
            previousSeq = marker.Value.Seq.Value;
        }

        var listed = await fs.ListDirAsync(directory, context).ConfigureAwait(false);
        if (!listed.IsOk) throw ErrorFromFile("directory listing", listed.Error);
        foreach (var info in listed.Value)
            if (!readOnly && info.Kind == FileKind.File && ReclaimFileName.IsMatch(info.Name))
                await fs.RemoveAsync(info.Path, new RemoveOptions { Force = true }, context).ConfigureAwait(false);
        var sidecarFiles = listed.Value.Where(info => info.Kind == FileKind.File && SidecarFileName.IsMatch(info.Name))
            .Select(info => info.Name).Order(StringComparer.Ordinal).ToList();
        var parsedFiles = new OrderedDictionary<string, ParsedFile<SidecarRecord>>(StringComparer.Ordinal);
        var recordByKey = new Dictionary<(string File, long Seq, long Ordinal), ParsedLine<SidecarRecord>>();
        foreach (var file in sidecarFiles)
        {
            var pathResult = await fs.JoinPathAsync([directory, file], context).ConfigureAwait(false);
            if (!pathResult.IsOk) throw ErrorFromFile("path join", pathResult.Error);
            var parsed = await ReadLinesAsync(pathResult.Value, file, context, (text, line) => ParseSidecarRecord(text, file, line)).ConfigureAwait(false);
            parsedFiles.Add(file, parsed);
            SidecarRecord? previous = null;
            foreach (var line in parsed.Lines)
            {
                if (previous is not null && (line.Value.Seq.Value < previous.Seq.Value ||
                    (line.Value.Seq == previous.Seq && line.Value.Ordinal <= previous.Ordinal)))
                    throw new JsonlCorruptionException($"Sidecar records are out of order in {file}");
                previous = line.Value;
                recordByKey[(file, line.Value.Seq.Value, line.Value.Ordinal)] = line;
            }
        }

        var currentOnly = new HashSet<long>();
        var retiredDocuments = new HashSet<long>();
        var finalTaskIsLive = new OrderedDictionary<long, bool>();
        foreach (var marker in main.Lines)
        {
            foreach (var operation in marker.Value.Writes)
            {
                switch (operation.Type)
                {
                    case MainOperationType.DocumentCreate:
                        if (IsCurrentOnly(operation.Record!)) currentOnly.Add(operation.Id);
                        break;
                    case MainOperationType.DocumentRetire: retiredDocuments.Add(operation.Id); break;
                    case MainOperationType.Task: finalTaskIsLive[operation.Id] = false; break;
                    case MainOperationType.TaskSidecar: finalTaskIsLive[operation.Id] = true; break;
                }
            }
        }
        var retiredCurrentOnlyDocuments = retiredDocuments.Where(currentOnly.Contains).ToHashSet();

        var latestBases = new Dictionary<long, SidecarRecord>();
        foreach (var marker in main.Lines)
        {
            foreach (var operation in marker.Value.Writes)
            {
                if (operation.Type is not (MainOperationType.DocumentCreate or MainOperationType.DocumentChange)) continue;
                var id = operation.Id;
                if (!currentOnly.Contains(id)) continue;
                var record = recordByKey.GetValueOrDefault((SidecarName("doc", id), marker.Value.Seq.Value, operation.Ordinal))?.Value;
                if (record is not { IsTask: false, ContentKind: "base" } || record.PayloadId != id) continue;
                if (!latestBases.TryGetValue(id, out var previous) || record.Seq > previous.Seq ||
                    (record.Seq == previous.Seq && record.Ordinal > previous.Ordinal))
                    latestBases[id] = record;
            }
        }

        bool IsBeforeLatestBase(long id, Seq seq, long ordinal) =>
            latestBases.TryGetValue(id, out var latest) && (seq < latest.Seq || (seq == latest.Seq && ordinal < latest.Ordinal));
        var terminalTasks = finalTaskIsLive.Where(pair => !pair.Value).Select(pair => pair.Key).ToHashSet();
        var confirmed = new HashSet<(string File, long Seq, long Ordinal)>();

        foreach (var line in main.Lines)
        {
            var marker = line.Value;
            var writes = new List<StorageWrite>();
            var writeLocations = new List<(int MainWriteIndex, ParsedLine<SidecarRecord>? SidecarLine)>();
            try
            {
                for (var writeIndex = 0; writeIndex < marker.Writes.Length; writeIndex++)
                {
                    var operation = marker.Writes[writeIndex];
                    switch (operation.Type)
                    {
                        case MainOperationType.Conversation or MainOperationType.Entry or MainOperationType.Submission or
                            MainOperationType.Task or MainOperationType.DocumentRetire:
                            writes.Add(operation.Write!);
                            writeLocations.Add((writeIndex, null));
                            break;
                        case MainOperationType.TaskSidecar:
                        {
                            var optional = terminalTasks.Contains(operation.Id);
                            var sidecarLine = ConfirmRecord(marker, operation.Ordinal, SidecarName("task", operation.Id), recordByKey, confirmed, optional);
                            if (sidecarLine is not null)
                            {
                                var record = sidecarLine.Value;
                                if (!record.IsTask || record.PayloadId != operation.Id)
                                    throw new JsonlCorruptionException($"Confirmed task sidecar data does not match commit {marker.Seq}");
                                if (!optional)
                                {
                                    writes.Add(new TaskWrite(TaskRecord.FromJson(record.Payload)));
                                    writeLocations.Add((writeIndex, sidecarLine));
                                }
                            }
                            break;
                        }
                        case MainOperationType.DocumentCreate or MainOperationType.DocumentChange:
                        {
                            var id = operation.Id;
                            var reclaimed = retiredCurrentOnlyDocuments.Contains(id) || IsBeforeLatestBase(id, marker.Seq, operation.Ordinal);
                            var sidecarLine = ConfirmRecord(marker, operation.Ordinal, SidecarName("doc", id), recordByKey, confirmed, reclaimed);
                            var record = sidecarLine?.Value;
                            if (record is not null && (record.IsTask || record.PayloadId != id))
                                throw new JsonlCorruptionException($"Confirmed document sidecar data does not match commit {marker.Seq}");
                            if (operation.Type == MainOperationType.DocumentCreate)
                            {
                                if (record is not null && record.ContentKind != "base")
                                    throw new JsonlCorruptionException($"Document creation lacks a confirmed base in commit {marker.Seq}");
                                var content = reclaimed || record is null
                                    ? DocumentContent.Base(1, JsonObjectValue.Empty)
                                    : (DocumentBase)DocumentContent.FromJson(record.Payload);
                                writes.Add(new DocumentCreateWrite(operation.Record!, content));
                                writeLocations.Add((writeIndex, reclaimed ? null : sidecarLine));
                            }
                            else if (!reclaimed && record is not null)
                            {
                                writes.Add(new DocumentChangeWrite(new DocumentId(id), DocumentContent.FromJson(record.Payload)));
                                writeLocations.Add((writeIndex, sidecarLine));
                            }
                            break;
                        }
                    }
                }
                ApplyRecoveredCommit(marker.Seq, line.Start, line.Length, writes, writeLocations, currentOnly);
            }
            catch (Exception error) when (error is not JsonlCorruptionException)
            {
                throw new JsonlCorruptionException($"Invalid committed state at sequence {marker.Seq}", error);
            }
        }

        fileSizes[MainFile] = main.CompleteSize;
        var reclamations = new OrderedDictionary<string, ReclaimPlan>(StringComparer.Ordinal);
        foreach (var (file, parsed) in parsedFiles)
        {
            long? unconfirmedAt = null;
            foreach (var line in parsed.Lines)
            {
                if (confirmed.Contains((file, line.Value.Seq.Value, line.Value.Ordinal)))
                {
                    if (unconfirmedAt is not null) throw new JsonlCorruptionException($"Confirmed record follows an unconfirmed tail in {file}");
                }
                else unconfirmedAt ??= line.Start;
            }
            if (unconfirmedAt is { } size && !readOnly)
            {
                var truncated = await fs.TruncateFileAsync(parsed.Path, size, context).ConfigureAwait(false);
                if (!truncated.IsOk) throw ErrorFromFile($"tail truncation of {file}", truncated.Error);
            }
            fileSizes[file] = unconfirmedAt ?? parsed.CompleteSize;

            var numericId = long.TryParse(file.AsSpan(file.IndexOf('-') + 1, file.Length - file.IndexOf('-') - 1 - ".jsonl".Length), out var parsedId) ? parsedId : -1;
            var confirmedLines = parsed.Lines.Where(line => confirmed.Contains((file, line.Value.Seq.Value, line.Value.Ordinal))).ToList();
            List<ParsedLine<SidecarRecord>>? retainedLines = null;
            if (file.StartsWith("task-", StringComparison.Ordinal) && terminalTasks.Contains(numericId)) retainedLines = [];
            else if (file.StartsWith("doc-", StringComparison.Ordinal))
            {
                if (retiredCurrentOnlyDocuments.Contains(numericId)) retainedLines = [];
                else if (latestBases.ContainsKey(numericId))
                    retainedLines = [.. confirmedLines.Where(line => !IsBeforeLatestBase(numericId, line.Value.Seq, line.Value.Ordinal))];
            }
            if (retainedLines is not null && (retainedLines.Count < confirmedLines.Count || retainedLines.Count == 0))
            {
                if (retainedLines.Count == 0)
                {
                    reclamations[file] = new ReclaimPlan(file.StartsWith("doc-", StringComparison.Ordinal) ? numericId : -1, [], null);
                }
                else
                {
                    var chunks = new List<byte[]>(retainedLines.Count);
                    var rewritten = new List<RevisionLocation>(retainedLines.Count);
                    var offset = 0L;
                    foreach (var retained in retainedLines)
                    {
                        var rawLine = parsed.RawBytes.AsSpan((int)retained.Start, retained.Length);
                        if (rawLine.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) rawLine = rawLine[3..];
                        var withNewline = new byte[rawLine.Length + 1];
                        rawLine.CopyTo(withNewline);
                        withNewline[^1] = (byte)'\n';
                        chunks.Add(withNewline);
                        rewritten.Add(new RevisionLocation(retained.Value.Seq, retained.Value.Version, retained.Value.ContentKind == "base", offset, rawLine.Length));
                        offset += withNewline.Length;
                    }
                    reclamations[file] = new ReclaimPlan(numericId, ConcatBytes(chunks), rewritten);
                }
            }
        }
        if (!readOnly) await ReclaimSidecarsAsync(reclamations, context).ConfigureAwait(false);

        foreach (var id in currentOnly) currentOnlyDocuments.Add(id);
        foreach (var (id, live) in finalTaskIsLive)
            if (live) liveTaskSidecars.Add(id);
    }

    private void ApplyRecoveredCommit(Seq commitSeq, long mainLineOffset, int mainLineLength, List<StorageWrite> writes,
        List<(int MainWriteIndex, ParsedLine<SidecarRecord>? SidecarLine)> locations, HashSet<long> currentOnly)
    {
        if (!Ids.IsSafeInteger(commitSeq.Value) || commitSeq.Value < nextSeq)
            throw new InvalidOperationException($"Commit sequence {commitSeq} does not strictly increase");
        StorageRules.CheckGlobalIds(writes, id => recordTypes.TryGetValue(id, out var table) ? table : null);
        var actions = StorageRules.PrepareDocumentActions(writes);
        StorageRules.CheckDocumentActions(actions,
            id => documents.TryGetValue(id, out var stored) ? new DocumentCheckState(stored.Record, stored.Revisions.Count > 0 ? stored.Revisions[^1].Version : null) : null,
            key => documentAddresses.GetValueOrDefault(key)?.CurrentId);

        var docRevisionsById = new Dictionary<long, RevisionLocation>();
        for (var index = 0; index < writes.Count; index++)
        {
            var (mainWriteIndex, sidecarLine) = locations[index];
            switch (writes[index])
            {
                case ConversationWrite { Value: var value }:
                    ApplyConversation(value);
                    break;
                case EntryWrite { Value: var value }:
                    ApplyEntry(value, new EntryLocation(commitSeq, mainLineOffset, mainLineLength, mainWriteIndex));
                    break;
                case TaskWrite { Value: var value }:
                    if (value.State.Status == DurableTaskStatus.Terminal)
                    {
                        ApplyTask(value, new TaskLocation(value.ConversationId, Intern(value.Kind), value.State.Status, value.AbortRequested,
                            value.Background, InMain: true, mainLineOffset, mainLineLength, mainWriteIndex));
                        pinnedReadOnlyTasks?.Remove(value.Id.Value);
                    }
                    else
                    {
                        ApplyTask(value, new TaskLocation(value.ConversationId, Intern(value.Kind), value.State.Status, value.AbortRequested,
                            value.Background, InMain: false, sidecarLine!.Start, sidecarLine.Length, WriteIndex: -1));
                        if (pinnedReadOnlyTasks is not null) pinnedReadOnlyTasks[value.Id.Value] = value;
                    }
                    break;
                case SubmissionWrite { Value: var value }:
                    ApplySubmission(value, new SubmissionLocation(value.ConversationId, value.Status, value.RequestId,
                        mainLineOffset, mainLineLength, mainWriteIndex));
                    break;
                case DocumentCreateWrite create:
                {
                    var docId = create.Record.Id.Value;
                    if (sidecarLine is not null)
                        docRevisionsById[docId] = new RevisionLocation(commitSeq, create.Content.Version, IsBase: true, sidecarLine.Start, sidecarLine.Length);
                    else
                        docRevisionsById[docId] = new RevisionLocation(commitSeq, create.Content.Version, IsBase: true, Offset: -1, Length: 0);
                    if (pinnedReadOnlyCurrentDocs is not null && currentOnly.Contains(docId))
                        pinnedReadOnlyCurrentDocs[docId] = [(commitSeq, create.Content)];
                    break;
                }
                case DocumentChangeWrite change:
                {
                    var docId = change.Id.Value;
                    var isBase = change.Content.Kind == DocumentContentKind.Base;
                    docRevisionsById[docId] = new RevisionLocation(commitSeq, change.Content.Version, isBase, sidecarLine!.Start, sidecarLine.Length);
                    if (pinnedReadOnlyCurrentDocs is not null && currentOnly.Contains(docId))
                    {
                        if (isBase || !pinnedReadOnlyCurrentDocs.TryGetValue(docId, out var list))
                            pinnedReadOnlyCurrentDocs[docId] = [(commitSeq, change.Content)];
                        else
                            list.Add((commitSeq, change.Content));
                    }
                    break;
                }
                case DocumentRetireWrite retire:
                    pinnedReadOnlyCurrentDocs?.Remove(retire.Id.Value);
                    break;
            }
        }
        ApplyDocumentActions(actions, commitSeq, docRevisionsById);
        nextSeq = commitSeq.Value + 1;
    }

    private static ParsedLine<SidecarRecord>? ConfirmRecord(MainMarker marker, long ordinal, string file,
        Dictionary<(string File, long Seq, long Ordinal), ParsedLine<SidecarRecord>> recordByKey, HashSet<(string File, long Seq, long Ordinal)> confirmed,
        bool optional)
    {
        var key = (file, marker.Seq.Value, ordinal);
        if (confirmed.Contains(key)) throw new JsonlCorruptionException("Sidecar record is confirmed more than once");
        if (!recordByKey.TryGetValue(key, out var line))
        {
            if (optional) return null;
            throw new JsonlCorruptionException($"Missing confirmed sidecar record {file} at sequence {marker.Seq}");
        }
        confirmed.Add(key);
        return line;
    }

    private async Task<ParsedFile<T>> ReadLinesAsync<T>(string path, string name, DurableContext context, Func<string, int, T> parse)
    {
        var read = await fs.ReadBinaryFileAsync(path, context).ConfigureAwait(false);
        if (!read.IsOk)
        {
            if (read.Error.Code == FileErrorCode.NotFound) return new(path, [], 0, 0, []);
            throw ErrorFromFile($"read of {name}", read.Error);
        }
        var bytes = read.Value;
        var completeSize = bytes.Length;
        if (completeSize > 0 && bytes[completeSize - 1] != (byte)'\n')
        {
            completeSize = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            if (!readOnly)
            {
                var truncated = await fs.TruncateFileAsync(path, completeSize, context).ConfigureAwait(false);
                if (!truncated.IsOk) throw ErrorFromFile($"torn-line truncation of {name}", truncated.Error);
            }
        }
        var lines = new List<ParsedLine<T>>();
        var start = 0;
        var lineNumber = 1;
        for (var end = 0; end < completeSize; end++)
        {
            if (bytes[end] != (byte)'\n') continue;
            string text;
            try
            {
                text = DecodeUtf8Line(bytes.AsSpan(start, end - start));
            }
            catch (DecoderFallbackException error)
            {
                throw new JsonlCorruptionException($"Invalid UTF-8 in complete {name} line {lineNumber}", error);
            }
            lines.Add(new(parse(text, lineNumber), start, end - start));
            start = end + 1;
            lineNumber++;
        }
        return new(path, lines, bytes.Length, completeSize, bytes);
    }

    // ---- Lazy disk readers + bounded LRU cache ----

    private async Task<JsonArrayValue> LoadMainWritesAsync(long offset, int length, DurableContext context)
    {
        var key = (offset, length);
        if (mainLineCache.TryGet(key, out var cached)) return cached;
        var reader = await GetMainReaderAsync(context).ConfigureAwait(false);
        var read = await reader.ReadAsync(offset, length, context).ConfigureAwait(false);
        if (!read.IsOk) throw ErrorFromFile($"positional read of {MainFile}", read.Error);
        if (read.Value.Length < length) throw new JsonlCorruptionException($"Truncated read in {MainFile} at offset {offset}");
        var text = DecodeUtf8Line(read.Value.AsSpan(0, length));
        var marker = (JsonObjectValue)JsonValue.Parse(text);
        var writes = (JsonArrayValue)marker["writes"];
        mainLineCache.Set(key, writes, length);
        return writes;
    }

    private async Task<IBinaryReader> GetMainReaderAsync(DurableContext context)
    {
        var existing = Volatile.Read(ref mainReader);
        if (existing is not null) return existing;
        await mainReaderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AssertUsable();
            if (mainReader is not null) return mainReader;
            var opened = await fs.OpenBinaryReaderAsync(mainPath, null, context).ConfigureAwait(false);
            if (!opened.IsOk) throw ErrorFromFile($"open of {MainFile}", opened.Error);
            mainReader = opened.Value;
            return mainReader;
        }
        finally
        {
            mainReaderGate.Release();
        }
    }

    private async Task<EntryRecord> LoadEntryAsync(EntryLocation location, DurableContext context)
    {
        var writes = await LoadMainWritesAsync(location.LineOffset, location.LineLength, context).ConfigureAwait(false);
        return ((EntryWrite)StorageWrite.FromJson(writes[location.WriteIndex])).Value;
    }

    private async Task<SubmissionRecord> LoadSubmissionAsync(SubmissionLocation location, DurableContext context)
    {
        var writes = await LoadMainWritesAsync(location.LineOffset, location.LineLength, context).ConfigureAwait(false);
        return ((SubmissionWrite)StorageWrite.FromJson(writes[location.WriteIndex])).Value;
    }

    private async Task<(bool Loaded, TaskRecord? Record)> TryLoadTaskAtSnapshotAsync(long taskId, TaskLocation location, int generation, DurableContext context)
    {
        var file = SidecarName("task", taskId);
        if (location.InMain)
        {
            var writes = await LoadMainWritesAsync(location.Offset, location.Length, context).ConfigureAwait(false);
            return (true, ((TaskWrite)StorageWrite.FromJson(writes[location.WriteIndex])).Value);
        }
        if (pinnedReadOnlyTasks is not null && pinnedReadOnlyTasks.TryGetValue(taskId, out var pinned)) return (true, pinned);
        var key = (file, generation, location.Offset, location.Length);
        if (sidecarCache.TryGet(key, out var cached)) return (true, (TaskRecord)cached);

        await WaitForWritingAsync().ConfigureAwait(false);
        AssertUsable();
        using (await AcquireSidecarReadAsync(file).ConfigureAwait(false))
        {
            lock (readGate)
            {
                if (writing is not null) return (false, null);
                AssertUsable();
                lock (indexGate)
                {
                    if (!tasks.TryGetValue(taskId, out var current) || current != location || sidecarGenerations.GetValueOrDefault(file) != generation)
                        return (false, null);
                }
            }
            if (sidecarCache.TryGet(key, out cached)) return (true, (TaskRecord)cached);

            var path = await ResolveFileAsync(file, context).ConfigureAwait(false);
            var opened = await fs.OpenBinaryReaderAsync(path, null, context).ConfigureAwait(false);
            if (!opened.IsOk) throw ErrorFromFile($"open of {file}", opened.Error);
            byte[] bytes;
            try
            {
                var read = await opened.Value.ReadAsync(location.Offset, location.Length, context).ConfigureAwait(false);
                if (!read.IsOk) throw ErrorFromFile($"positional read of {file}", read.Error);
                if (read.Value.Length < location.Length) throw new JsonlCorruptionException($"Truncated read in {file} at offset {location.Offset}");
                bytes = read.Value;
            }
            finally
            {
                await opened.Value.CloseAsync(context).ConfigureAwait(false);
            }
            var record = ParseSidecarTask(bytes.AsSpan(0, location.Length));
            sidecarCache.Set(key, record, location.Length);
            return (true, record);
        }
    }

    private async Task<TaskRecord> LoadTaskAsync(long taskId, TaskLocation location, int generation, DurableContext context)
    {
        var file = SidecarName("task", taskId);
        while (true)
        {
            if (location.InMain)
            {
                var writes = await LoadMainWritesAsync(location.Offset, location.Length, context).ConfigureAwait(false);
                return ((TaskWrite)StorageWrite.FromJson(writes[location.WriteIndex])).Value;
            }
            if (pinnedReadOnlyTasks is not null && pinnedReadOnlyTasks.TryGetValue(taskId, out var pinned)) return pinned;
            var key = (file, generation, location.Offset, location.Length);
            if (sidecarCache.TryGet(key, out var cached)) return (TaskRecord)cached;

            await WaitForWritingAsync().ConfigureAwait(false);
            AssertUsable();
            using (await AcquireSidecarReadAsync(file).ConfigureAwait(false))
            {
                var retry = false;
                lock (readGate)
                {
                    if (writing is not null)
                    {
                        retry = true;
                    }
                    else
                    {
                        AssertUsable();
                        lock (indexGate)
                        {
                            location = tasks[taskId];
                            generation = sidecarGenerations.GetValueOrDefault(file);
                        }
                    }
                }
                if (retry || location.InMain) continue;
                key = (file, generation, location.Offset, location.Length);
                if (sidecarCache.TryGet(key, out cached)) return (TaskRecord)cached;

                var path = await ResolveFileAsync(file, context).ConfigureAwait(false);
                var opened = await fs.OpenBinaryReaderAsync(path, null, context).ConfigureAwait(false);
                if (!opened.IsOk) throw ErrorFromFile($"open of {file}", opened.Error);
                byte[] bytes;
                try
                {
                    var read = await opened.Value.ReadAsync(location.Offset, location.Length, context).ConfigureAwait(false);
                    if (!read.IsOk) throw ErrorFromFile($"positional read of {file}", read.Error);
                    if (read.Value.Length < location.Length) throw new JsonlCorruptionException($"Truncated read in {file} at offset {location.Offset}");
                    bytes = read.Value;
                }
                finally
                {
                    await opened.Value.CloseAsync(context).ConfigureAwait(false);
                }
                var record = ParseSidecarTask(bytes.AsSpan(0, location.Length));
                sidecarCache.Set(key, record, location.Length);
                return record;
            }
        }
    }

    private bool TrySnapshotDocumentLocked(DocumentId id, DocumentPoint at, string file,
        out DocumentRecord record, out StoredDocument? immediate, out List<RevisionLocation> needed, out int generation)
    {
        record = null!;
        immediate = null;
        needed = null!;
        generation = 0;
        if (!documents.TryGetValue(id.Value, out var stored)) return false;
        if (!at.IsCurrent && StorageRules.IsCurrentOnly(stored.Record))
            throw new StorageRequestException($"Document {id} does not retain historical content");
        if (!StorageRules.IsAliveAt(stored.Record, at)) return false;
        record = stored.Record;
        if (pinnedReadOnlyCurrentDocs is not null && pinnedReadOnlyCurrentDocs.TryGetValue(id.Value, out var pinnedRevisions))
        {
            immediate = MaterializeFromContents(id, record, at, pinnedRevisions);
            return false;
        }
        var revisions = at.Seq is { } seq
            ? stored.Revisions.Where(revision => revision.Seq <= seq).ToList()
            : stored.Revisions;
        var baseIndex = revisions.Count - 1;
        while (baseIndex >= 0 && !revisions[baseIndex].IsBase) baseIndex--;
        if (baseIndex < 0) throw new InvalidOperationException($"Document {id} is missing a required base");
        needed = revisions.GetRange(baseIndex, revisions.Count - baseIndex);
        generation = sidecarGenerations.GetValueOrDefault(file);
        return true;
    }

    private async Task<StoredDocument?> MaterializeDocumentAsync(DocumentId id, DocumentPoint at,
        Dictionary<(string File, long Offset, int Length), byte[]>? pendingSidecarBytes, bool insideCommit, DurableContext context)
    {
        var file = SidecarName("doc", id.Value);
        while (true)
        {
            if (!insideCommit)
            {
                await WaitForWritingAsync().ConfigureAwait(false);
                AssertUsable();
            }
            using var readLease = insideCommit ? default : await AcquireSidecarReadAsync(file).ConfigureAwait(false);
            DocumentRecord record = null!;
            List<RevisionLocation> needed = null!;
            var generation = 0;
            if (insideCommit)
            {
                lock (indexGate)
                {
                    if (!TrySnapshotDocumentLocked(id, at, file, out record, out var immediate, out needed, out generation))
                        return immediate;
                }
            }
            else
            {
                var retry = false;
                var found = false;
                StoredDocument? immediate = null;
                lock (readGate)
                {
                    if (writing is not null)
                    {
                        retry = true;
                    }
                    else
                    {
                        AssertUsable();
                        lock (indexGate)
                        {
                            found = TrySnapshotDocumentLocked(id, at, file, out record, out immediate, out needed, out generation);
                        }
                    }
                }
                if (retry) continue;
                if (!found) return immediate;
            }

            var loaded = new DocumentContent[needed.Count];
            var missing = new List<int>();
            for (var index = 0; index < needed.Count; index++)
            {
                var rev = needed[index];
                if (pendingSidecarBytes is not null && pendingSidecarBytes.TryGetValue((file, rev.Offset, rev.Length), out var pending))
                {
                    loaded[index] = ParseSidecarDocument(pending);
                    continue;
                }
                if (sidecarCache.TryGet((file, generation, rev.Offset, rev.Length), out var cached))
                {
                    loaded[index] = (DocumentContent)cached;
                    continue;
                }
                missing.Add(index);
            }

            if (missing.Count > 0)
            {
                var path = await ResolveFileAsync(file, context).ConfigureAwait(false);
                var opened = await fs.OpenBinaryReaderAsync(path, null, context).ConfigureAwait(false);
                if (!opened.IsOk) throw ErrorFromFile($"open of {file}", opened.Error);
                try
                {
                    // Coalesce contiguous/small spans into one positional read when multiple revisions miss cache.
                    var firstOffset = needed[missing[0]].Offset;
                    var lastRev = needed[missing[^1]];
                    var spanEnd = lastRev.Offset + lastRev.Length;
                    var totalSpan = spanEnd - firstOffset;
                    if (missing.Count > 1 && totalSpan > 0 && totalSpan <= 4 * 1024 * 1024)
                    {
                        var bulk = await opened.Value.ReadAsync(firstOffset, totalSpan, context).ConfigureAwait(false);
                        if (!bulk.IsOk) throw ErrorFromFile($"positional read of {file}", bulk.Error);
                        if (bulk.Value.Length < totalSpan)
                            throw new JsonlCorruptionException($"Truncated read in {file} at offset {firstOffset}");
                        foreach (var index in missing)
                        {
                            var rev = needed[index];
                            var slice = bulk.Value.AsSpan((int)(rev.Offset - firstOffset), rev.Length);
                            var content = ParseSidecarDocument(slice);
                            loaded[index] = content;
                            sidecarCache.Set((file, generation, rev.Offset, rev.Length), content, rev.Length);
                        }
                    }
                    else
                    {
                        foreach (var index in missing)
                        {
                            var rev = needed[index];
                            var read = await opened.Value.ReadAsync(rev.Offset, rev.Length, context).ConfigureAwait(false);
                            if (!read.IsOk) throw ErrorFromFile($"positional read of {file}", read.Error);
                            if (read.Value.Length < rev.Length)
                                throw new JsonlCorruptionException($"Truncated read in {file} at offset {rev.Offset}");
                            var content = ParseSidecarDocument(read.Value.AsSpan(0, rev.Length));
                            loaded[index] = content;
                            sidecarCache.Set((file, generation, rev.Offset, rev.Length), content, rev.Length);
                        }
                    }
                }
                finally
                {
                    await opened.Value.CloseAsync(context).ConfigureAwait(false);
                }
            }

            if (loaded[0] is not DocumentBase documentBase)
                throw new InvalidOperationException($"Document {id} is missing a required base");
            var value = StorageRules.Replay(id, documentBase.Version, documentBase.Value, loaded.Skip(1),
                content => (content is DocumentDelta, content.Version), content => ((DocumentDelta)content).Ops);
            return new StoredDocument(record, documentBase.Version, value, loaded.Length - 1);
        }
    }

    private static StoredDocument MaterializeFromContents(DocumentId id, DocumentRecord record, DocumentPoint at,
        List<(Seq Seq, DocumentContent Content)> revisions)
    {
        var slice = at.Seq is { } seq ? revisions.Where(r => r.Seq <= seq).ToList() : revisions;
        var baseIndex = slice.Count - 1;
        while (baseIndex >= 0 && slice[baseIndex].Content is not DocumentBase) baseIndex--;
        if (baseIndex < 0 || slice[baseIndex].Content is not DocumentBase documentBase)
            throw new InvalidOperationException($"Document {id} is missing a required base");
        var value = StorageRules.Replay(id, documentBase.Version, documentBase.Value, slice.Skip(baseIndex + 1),
            revision => (revision.Content is DocumentDelta, revision.Content.Version), revision => ((DocumentDelta)revision.Content).Ops);
        return new StoredDocument(record, documentBase.Version, value, slice.Count - baseIndex - 1);
    }

    private async Task WaitForWritingAsync()
    {
        while (true)
        {
            Task? pending;
            lock (readGate) pending = writing;
            if (pending is null) return;
            await pending.ConfigureAwait(false);
        }
    }

    private async Task<SidecarReadLease> AcquireSidecarReadAsync(string file)
    {
        while (true)
        {
            Task? waitReclaim = null;
            lock (readGate)
            {
                if (!sidecarGates.TryGetValue(file, out var gate)) sidecarGates[file] = gate = new SidecarGate();
                if (!gate.Reclaiming)
                {
                    gate.Readers++;
                    return new SidecarReadLease(this, file);
                }
                waitReclaim = gate.ReclaimDone!.Task;
            }
            await waitReclaim.ConfigureAwait(false);
        }
    }

    private void ReleaseSidecarRead(string file)
    {
        TaskCompletionSource? drained = null;
        lock (readGate)
        {
            if (file is null || !sidecarGates.TryGetValue(file, out var gate)) return;
            gate.Readers--;
            if (gate.Readers == 0)
            {
                drained = gate.ReadersDrained;
                gate.ReadersDrained = null;
                if (!gate.Reclaiming) sidecarGates.Remove(file);
            }
        }
        drained?.TrySetResult();
    }

    private async Task<SidecarReclaimLease> AcquireSidecarReclaimAsync(string file)
    {
        while (true)
        {
            Task? wait;
            var owned = false;
            lock (readGate)
            {
                if (!sidecarGates.TryGetValue(file, out var gate)) sidecarGates[file] = gate = new SidecarGate();
                if (gate.Reclaiming)
                {
                    wait = gate.ReclaimDone!.Task;
                }
                else
                {
                    gate.Reclaiming = true;
                    gate.ReclaimDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    owned = true;
                    if (gate.Readers == 0) return new SidecarReclaimLease(this, file);
                    gate.ReadersDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = gate.ReadersDrained.Task;
                }
            }
            await wait.ConfigureAwait(false);
            if (owned) return new SidecarReclaimLease(this, file);
        }
    }

    private void ReleaseSidecarReclaim(string file)
    {
        TaskCompletionSource? done = null;
        lock (readGate)
        {
            if (file is null || !sidecarGates.TryGetValue(file, out var gate)) return;
            gate.Reclaiming = false;
            done = gate.ReclaimDone;
            gate.ReclaimDone = null;
            if (gate.Readers == 0) sidecarGates.Remove(file);
        }
        done?.TrySetResult();
    }

    // ---- Fork-chain visibility queries (called under indexGate) ----

    private EntryLocation? FindVisibleEntryLocation(ConversationId conversationId, long targetEntryId)
    {
        if (!conversations.ContainsKey(conversationId.Value))
            throw new StorageRequestException($"Unknown conversation: {conversationId}");
        var currentId = conversationId.Value;
        var upperEntryId = targetEntryId;
        while (true)
        {
            var ids = entryIds.GetValueOrDefault(currentId) ?? [];
            var index = JsonlSortedIndex.UpperBound(ids, upperEntryId) - 1;
            if (index >= 0 && ids[index] == targetEntryId) return entries[targetEntryId];
            var conversation = conversations[currentId];
            if (conversation.Parent is not { } parent) return null;
            upperEntryId = Math.Min(upperEntryId, parent.At.Value);
            if (upperEntryId < targetEntryId) return null;
            currentId = parent.ConversationId.Value;
        }
    }

    private IEnumerable<long> VisibleEntryIdsDescending(ConversationId conversationId, long minEntryId, long maxEntryId)
    {
        if (!conversations.ContainsKey(conversationId.Value))
            throw new StorageRequestException($"Unknown conversation: {conversationId}");
        var currentId = conversationId.Value;
        var upperEntryId = maxEntryId;
        while (true)
        {
            var ids = entryIds.GetValueOrDefault(currentId) ?? [];
            for (var index = JsonlSortedIndex.UpperBound(ids, upperEntryId) - 1; index >= 0; index--)
            {
                var id = ids[index];
                if (id < minEntryId) break;
                yield return id;
            }
            var conversation = conversations[currentId];
            if (conversation.Parent is not { } parent) break;
            upperEntryId = Math.Min(upperEntryId, parent.At.Value);
            if (upperEntryId < minEntryId) break;
            currentId = parent.ConversationId.Value;
        }
    }

    private IEnumerable<long> VisibleEntryIdsAscending(ConversationId conversationId, long minEntryId, long maxEntryId)
    {
        if (!conversations.ContainsKey(conversationId.Value))
            throw new StorageRequestException($"Unknown conversation: {conversationId}");
        var segments = new List<(long ConversationId, long Upper)>();
        var currentId = conversationId.Value;
        var upperEntryId = maxEntryId;
        while (true)
        {
            segments.Add((currentId, upperEntryId));
            var conversation = conversations[currentId];
            if (conversation.Parent is not { } parent) break;
            upperEntryId = Math.Min(upperEntryId, parent.At.Value);
            if (upperEntryId < minEntryId) break;
            currentId = parent.ConversationId.Value;
        }
        segments.Reverse();
        foreach (var (segmentConversationId, upper) in segments)
        {
            var ids = entryIds.GetValueOrDefault(segmentConversationId) ?? [];
            for (var index = JsonlSortedIndex.LowerBound(ids, minEntryId); index < ids.Count; index++)
            {
                var id = ids[index];
                if (id > upper) break;
                yield return id;
            }
        }
    }

    // ---- Read gating & usability ----

    // A writer only reclaims a sidecar after appending the marker that permits it. A changed main size explains a recovery race.
    private async Task<bool> MainChangedAsync(DurableContext context)
    {
        var info = await fs.FileInfoAsync(mainPath, context).ConfigureAwait(false);
        return info.IsOk && info.Value.Size != mainSize;
    }

    private async Task<string> ResolveFileAsync(string file, DurableContext context)
    {
        var path = await fs.JoinPathAsync([directory, file], context).ConfigureAwait(false);
        if (!path.IsOk) throw ErrorFromFile("path join", path.Error);
        return path.Value;
    }

    private Task<T> ReadableSync<T>(Func<T> read)
    {
        Task? pending;
        lock (readGate)
        {
            pending = writing;
            if (pending is null)
            {
                try
                {
                    AssertUsable();
                    lock (indexGate) return Task.FromResult(read());
                }
                catch (Exception error)
                {
                    return Task.FromException<T>(error);
                }
            }
        }
        return ReadSyncAfterAsync(pending, read);
    }

    private async Task<T> ReadSyncAfterAsync<T>(Task pending, Func<T> read)
    {
        await pending.ConfigureAwait(false);
        return await ReadableSync(read).ConfigureAwait(false);
    }

    private async Task<TResult> ReadableSnapshotAsync<TSnapshot, TResult>(Func<TSnapshot> snapshot, Func<TSnapshot, Task<TResult>> load)
    {
        TSnapshot captured;
        while (true)
        {
            Task? pending;
            lock (readGate)
            {
                pending = writing;
                if (pending is null)
                {
                    AssertUsable();
                    lock (indexGate) captured = snapshot();
                    break;
                }
            }
            await pending.ConfigureAwait(false);
        }
        return await load(captured).ConfigureAwait(false);
    }

    private JsonlStoragePoisonedException Poison(Exception cause) => poisonError ??= new JsonlStoragePoisonedException(cause);

    private void AssertUsable()
    {
        if (closed) throw new InvalidOperationException("JsonlStorage is closed");
        if (poisonError is not null) throw poisonError;
    }

    // ---- Line formats ----

    private enum MainOperationType { Conversation, Entry, Submission, Task, DocumentRetire, TaskSidecar, DocumentCreate, DocumentChange }

    /// <summary>One write of a commit marker: a table write or retirement carried inline (<see cref="Write"/>), a live task replacement
    /// or document content stored in a sidecar at <see cref="Ordinal"/>, or a document creation's <see cref="Record"/>.</summary>
    private sealed record MainOperation(MainOperationType Type, long Id, long Ordinal = 0, StorageWrite? Write = null, DocumentCreate? Record = null);

    private sealed record MainMarker(Seq Seq, ImmutableArray<MainOperation> Writes);

    /// <summary>One sidecar record: a live task (<see cref="IsTask"/>, its record as <see cref="Payload"/>) or document content (its
    /// <see cref="ContentKind"/>, <see cref="Version"/> and content as <see cref="Payload"/>).</summary>
    private sealed record SidecarRecord(Seq Seq, long Ordinal, bool IsTask, long PayloadId, string? ContentKind, int Version, JsonValue Payload);

    private sealed record ParsedLine<T>(T Value, long Start, int Length);

    private sealed record ParsedFile<T>(string Path, List<ParsedLine<T>> Lines, long Size, long CompleteSize, byte[] RawBytes);

    private static string SidecarName(string kind, long id) => $"{kind}-{id}.jsonl";

    private static bool IsCurrentOnly(DocumentCreate record) =>
        record.Scope.Kind != DocumentScopeKind.Conversation || record.History == DocumentHistory.Latest;

    private static string JsonLine(JsonValue value) => value.ToJson() + "\n";

    private static string DecodeUtf8Line(ReadOnlySpan<byte> span)
    {
        // A fatal TextDecoder, which drops a leading byte-order mark from each decoded line.
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..];
        return StrictUtf8.GetString(span);
    }

    private static TaskRecord ParseSidecarTask(ReadOnlySpan<byte> span)
    {
        var obj = (JsonObjectValue)JsonValue.Parse(DecodeUtf8Line(span));
        var payload = (JsonObjectValue)obj["payload"];
        return TaskRecord.FromJson(payload["value"]);
    }

    private static DocumentContent ParseSidecarDocument(ReadOnlySpan<byte> span)
    {
        var obj = (JsonObjectValue)JsonValue.Parse(DecodeUtf8Line(span));
        var payload = (JsonObjectValue)obj["payload"];
        return DocumentContent.FromJson(payload["content"]);
    }

    private static byte[] ConcatBytes(IEnumerable<byte[]> chunks)
    {
        var list = chunks as IReadOnlyList<byte[]> ?? chunks.ToList();
        if (list.Count == 1) return list[0];
        var total = 0;
        for (var i = 0; i < list.Count; i++) total += list[i].Length;
        var buffer = new byte[total];
        var offset = 0;
        for (var i = 0; i < list.Count; i++)
        {
            list[i].CopyTo(buffer, offset);
            offset += list[i].Length;
        }
        return buffer;
    }

    private static JsonObjectValue Object(params (string Key, JsonValue Value)[] properties)
    {
        var builder = JsonObjectValue.CreateBuilder();
        foreach (var (key, value) in properties) builder[key] = value;
        return builder.ToImmutable();
    }

    private static IOException ErrorFromFile(string action, FileError error) => new($"JSONL {action} failed: {error.Message}", error);

    private static JsonValue ParseJson(string text, string description)
    {
        try { return JsonValue.Parse(text); }
        catch (FormatException error) { throw new JsonlCorruptionException($"Malformed complete {description}", error); }
    }

    private static bool IsSafeInteger(JsonValue? value, out long number)
    {
        if (value is JsonNumberValue { Value: var raw } && Math.Floor(raw) == raw && Math.Abs(raw) <= Ids.MaxSafeInteger)
        {
            number = (long)raw;
            return true;
        }
        number = 0;
        return false;
    }

    private static JsonValue? Field(JsonValue? value, string key) => value is JsonObjectValue obj && obj.TryGetValue(key, out var field) ? field : null;

    private static string? StringField(JsonValue? value, string key) => Field(value, key) is JsonStringValue text ? text.Value : null;

    private static MainOperation ValidateMainOperation(JsonValue value, string description)
    {
        if (value is not JsonObjectValue || StringField(value, "type") is not { } type) throw new JsonlCorruptionException($"Invalid write in {description}");
        T Decode<T>(Func<T> decode, string message)
        {
            try { return decode(); }
            catch (Exception error) when (error is not JsonlCorruptionException)
            {
                throw new JsonlCorruptionException(message, error);
            }
        }
        long id, ordinal;
        switch (type)
        {
            case "conversation" or "entry" or "submission":
            {
                var message = $"Invalid {type} write in {description}";
                if (Field(value, "value") is not JsonObjectValue || !IsSafeInteger(Field(Field(value, "value"), "id"), out id))
                    throw new JsonlCorruptionException(message);
                var write = Decode(() => StorageWrite.FromJson(value), message);
                return new(type switch { "conversation" => MainOperationType.Conversation, "entry" => MainOperationType.Entry, _ => MainOperationType.Submission },
                    id, Write: write);
            }
            case "task":
            {
                var message = $"Invalid terminal task write in {description}";
                var task = Field(value, "value");
                if (task is not JsonObjectValue || !IsSafeInteger(Field(task, "id"), out id) || Field(task, "state") is not JsonObjectValue ||
                    StringField(Field(task, "state"), "status") != "terminal")
                    throw new JsonlCorruptionException(message);
                return new(MainOperationType.Task, id, Write: Decode(() => StorageWrite.FromJson(value), message));
            }
            case "document.retire":
            {
                var message = $"Invalid document retirement in {description}";
                if (!IsSafeInteger(Field(value, "id"), out id)) throw new JsonlCorruptionException(message);
                return new(MainOperationType.DocumentRetire, id, Write: Decode(() => StorageWrite.FromJson(value), message));
            }
            case "task.sidecar":
                if (!IsSafeInteger(Field(value, "id"), out id) || !IsSafeInteger(Field(value, "ordinal"), out ordinal) || ordinal < 0)
                    throw new JsonlCorruptionException($"Invalid task sidecar write in {description}");
                return new(MainOperationType.TaskSidecar, id, ordinal);
            case "document.create":
            {
                var message = $"Invalid document creation in {description}";
                var record = Field(value, "record");
                if (record is not JsonObjectValue || !IsSafeInteger(Field(record, "id"), out id) ||
                    !IsSafeInteger(Field(value, "ordinal"), out ordinal) || ordinal < 0)
                    throw new JsonlCorruptionException(message);
                return new(MainOperationType.DocumentCreate, id, ordinal, Record: Decode(() => DocumentCreate.FromJson(record), message));
            }
            case "document.change":
                if (!IsSafeInteger(Field(value, "id"), out id) || !IsSafeInteger(Field(value, "ordinal"), out ordinal) || ordinal < 0)
                    throw new JsonlCorruptionException($"Invalid document change in {description}");
                return new(MainOperationType.DocumentChange, id, ordinal);
            default:
                throw new JsonlCorruptionException($"Unknown write type in {description}");
        }
    }

    private static MainMarker ParseMainMarker(string text, int line)
    {
        var description = $"{MainFile} line {line}";
        var value = ParseJson(text, description);
        if (value is not JsonObjectValue || Field(value, "format") is not JsonNumberValue { Value: FormatVersion } ||
            StringField(value, "type") != "commit" || !IsSafeInteger(Field(value, "seq"), out var seq) || seq < 1 ||
            Field(value, "writes") is not JsonArrayValue writes)
            throw new JsonlCorruptionException($"Invalid commit marker in {description}");
        return new(new Seq(seq), [.. writes.Select(write => ValidateMainOperation(write, description))]);
    }

    private static (string Kind, int Version) ValidateDocumentContent(JsonValue? value, string description)
    {
        if (value is not JsonObjectValue || !IsSafeInteger(Field(value, "version"), out var version) || version < 1)
            throw new JsonlCorruptionException($"Invalid document content in {description}");
        var kind = StringField(value, "kind");
        if (kind == "base" && Field(value, "value") is JsonObjectValue) return (kind, (int)version);
        if (kind == "delta" && Field(value, "ops") is JsonArrayValue) return (kind, (int)version);
        throw new JsonlCorruptionException($"Invalid document content in {description}");
    }

    private static SidecarRecord ParseSidecarRecord(string text, string file, int line)
    {
        var description = $"{file} line {line}";
        var value = ParseJson(text, description);
        var payload = Field(value, "payload");
        if (value is not JsonObjectValue || Field(value, "format") is not JsonNumberValue { Value: FormatVersion } ||
            StringField(value, "type") != "record" || !IsSafeInteger(Field(value, "seq"), out var seq) || seq < 1 ||
            !IsSafeInteger(Field(value, "ordinal"), out var ordinal) || ordinal < 0 || payload is not JsonObjectValue ||
            StringField(payload, "type") is not { } type)
            throw new JsonlCorruptionException($"Invalid sidecar record in {description}");
        if (type == "task")
        {
            var task = Field(payload, "value");
            if (task is not JsonObjectValue || !IsSafeInteger(Field(task, "id"), out var taskId) || Field(task, "state") is not JsonObjectValue ||
                StringField(Field(task, "state"), "status") == "terminal")
                throw new JsonlCorruptionException($"Invalid live task record in {description}");
            return new(new Seq(seq), ordinal, true, taskId, null, 0, task);
        }
        if (type == "document")
        {
            if (!IsSafeInteger(Field(payload, "id"), out var documentId)) throw new JsonlCorruptionException($"Invalid document record in {description}");
            var content = Field(payload, "content");
            var (kind, version) = ValidateDocumentContent(content, description);
            return new(new Seq(seq), ordinal, false, documentId, kind, version, content!);
        }
        throw new JsonlCorruptionException($"Unknown sidecar record type in {description}");
    }
}
