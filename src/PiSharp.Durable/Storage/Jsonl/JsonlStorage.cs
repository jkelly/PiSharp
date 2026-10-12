// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/storage/jsonl/storage.ts (and its index.ts exports).
//
// Portable JSONL storage over IFileSystem (spec §11.3). The directory holds main.jsonl, with one commit marker line per commit, and
// sidecars doc-<id>.jsonl (one document incarnation's bases and deltas) and task-<id>.jsonl (live task replacements). A commit appends
// complete records to every affected sidecar, optionally flushes them, appends one marker to main.jsonl and only then publishes in memory;
// an uncertain append failure poisons the open storage. Opening removes torn final lines, ignores and truncates unconfirmed sidecar tails,
// replays confirmed records, and reclaims sidecar records that a later latest base, retirement or terminal task made unnecessary.
// main.jsonl is never compacted. Lines are byte-for-byte what upstream writes: the same file names, key orders and JSON text, so Pi and
// PiSharp open each other's directories. This file uses no local runtime API; JsonlStorage.Local.cs adds the local-file-system opener.
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
}

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
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex SidecarFileName = new(@"^(?:doc|task)-(?:0|[1-9][0-9]*)\.jsonl$", RegexOptions.CultureInvariant);
    private static readonly Regex ReclaimFileName = new(@"^(?:doc|task)-(?:0|[1-9][0-9]*)\.jsonl\.reclaim$", RegexOptions.CultureInvariant);

    private readonly IFileSystem fs;
    private readonly string directory;
    private readonly string mainPath;
    private readonly bool fsync;
    private readonly MemoryStorage memory = new();
    private readonly HashSet<long> currentOnlyDocuments = [];
    private readonly HashSet<long> liveTaskSidecars = [];
    private bool closed;
    private JsonlStoragePoisonedException? poisonError;

    private JsonlStorage(IFileSystem fs, string directory, string mainPath, JsonlStorageOptions options)
    {
        this.fs = fs;
        this.directory = directory;
        this.mainPath = mainPath;
        fsync = options.Fsync;
    }

    /// <summary>Opens or creates a JSONL storage directory using the supplied file system.</summary>
    public static async Task<JsonlStorage> OpenAsync(string directory, IFileSystem fs, DurableContext context, JsonlStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(context);
        var absolute = await fs.AbsolutePathAsync(directory, context);
        if (!absolute.IsOk) throw ErrorFromFile("path resolution", absolute.Error);
        var created = await fs.CreateDirAsync(absolute.Value, new CreateDirOptions { Recursive = true }, context);
        if (!created.IsOk) throw ErrorFromFile("directory creation", created.Error);
        var mainPathResult = await fs.JoinPathAsync([absolute.Value, MainFile], context);
        if (!mainPathResult.IsOk) throw ErrorFromFile("path join", mainPathResult.Error);
        var storage = new JsonlStorage(fs, absolute.Value, mainPathResult.Value, options ?? new JsonlStorageOptions());
        await storage.RecoverAsync(context);
        return storage;
    }

    /// <inheritdoc/>
    public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(writes);
        AssertUsable();
        var prepared = memory.PrepareCommit(writes);
        var encoded = EncodeCommit(prepared.Seq, prepared.Writes, writes);
        var reclamations = PlanReclamations(prepared.Writes, encoded);
        var sidecars = new List<(string File, byte[] Content, string Path)>();
        foreach (var (file, content) in encoded.Sidecars) sidecars.Add((file, Utf8.GetBytes(content), await ResolveFileAsync(file, context)));

        foreach (var sidecar in sidecars)
        {
            var result = await fs.AppendFileAsync(sidecar.Path, sidecar.Content, context);
            if (!result.IsOk) throw Poison(ErrorFromFile($"append to {sidecar.File}", result.Error));
        }
        if (fsync)
        {
            foreach (var sidecar in sidecars)
            {
                var result = await fs.FlushFileAsync(sidecar.Path, context);
                if (!result.IsOk) throw Poison(ErrorFromFile($"flush of {sidecar.File}", result.Error));
            }
        }
        var marker = await fs.AppendFileAsync(mainPath, Utf8.GetBytes(encoded.Marker), context);
        if (!marker.IsOk) throw Poison(ErrorFromFile($"append to {MainFile}", marker.Error));
        var seq = prepared.Apply();
        AdoptSidecarState(prepared.Writes);
        await ReclaimSidecarsAsync(reclamations, context);
        return seq;
    }

    /// <inheritdoc/>
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => Store(store => store.MintIdAsync<TId>());

    /// <inheritdoc/>
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) =>
        Store(store => store.GetConversationAsync(id, context));

    /// <inheritdoc/>
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Store(store => store.ScanConversationsAsync(query, limit, cursor, context));

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => Store(store => store.GetEntryAsync(id, context));

    /// <inheritdoc/>
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        Store(store => store.GetEntryAsync(conversationId, id, context));

    /// <inheritdoc/>
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        Store(store => store.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context));

    /// <inheritdoc/>
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Store(store => store.ScanEntriesAsync(query, limit, cursor, context));

    /// <inheritdoc/>
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Store(store => store.GetTaskAsync(id, context));

    /// <inheritdoc/>
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Store(store => store.ScanTasksAsync(query, limit, cursor, context));

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) =>
        Store(store => store.GetSubmissionAsync(id, context));

    /// <inheritdoc/>
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Store(store => store.ScanSubmissionsAsync(query, limit, cursor, context));

    /// <inheritdoc/>
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        Store(store => store.GetSubmissionByRequestAsync(conversationId, requestId, context));

    /// <inheritdoc/>
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        Store(store => store.FindDocumentAsync(address, at, context));

    /// <inheritdoc/>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        Store(store => store.GetDocumentAsync(id, at, context));

    /// <inheritdoc/>
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Store(store => store.ScanDocumentsAsync(query, limit, cursor, context));

    /// <inheritdoc/>
    public async Task CloseAsync(DurableContext context)
    {
        if (closed) return;
        closed = true;
        await memory.CloseAsync(context);
    }

    private sealed record EncodedCommit(string Marker, OrderedDictionary<string, string> Sidecars);

    private EncodedCommit EncodeCommit(Seq seq, ImmutableArray<StorageWrite> writes, IReadOnlyList<StorageWrite> submitted)
    {
        var mainWrites = new List<JsonValue>();
        var records = new OrderedDictionary<string, List<JsonObjectValue>>(StringComparer.Ordinal);
        var nextOrdinal = 0L;
        long AddSidecar(string file, JsonObjectValue payload)
        {
            var ordinal = nextOrdinal++;
            if (!records.TryGetValue(file, out var fileRecords)) records.Add(file, fileRecords = []);
            fileRecords.Add(Object(("format", JsonValue.Number(FormatVersion)), ("type", JsonValue.String("record")), ("seq", seq.ToJson()),
                ("ordinal", JsonValue.Number(ordinal)), ("payload", payload)));
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
                        var ordinal = AddSidecar(SidecarName("task", task.Value.Id.Value),
                            Object(("type", JsonValue.String("task")), ("value", task.Value.ToJson())));
                        mainWrites.Add(Object(("type", JsonValue.String("task.sidecar")), ("id", task.Value.Id.ToJson()), ("ordinal", JsonValue.Number(ordinal))));
                    }
                    break;
                case DocumentCreateWrite create:
                {
                    // MemoryStorage resolves a copy to a create whose base upstream spells {kind, version, value}; the Session spells
                    // the bases it builds {version, kind, value}. Keep each spelling so the sidecar line is upstream's byte for byte.
                    var content = index < submitted.Count && submitted[index] is DocumentCopyWrite
                        ? Object(("kind", JsonValue.String("base")), ("version", JsonValue.Number(create.Content.Version)), ("value", create.Content.Value))
                        : create.Content.ToJson();
                    var ordinal = AddSidecar(SidecarName("doc", create.Record.Id.Value),
                        Object(("type", JsonValue.String("document")), ("id", create.Record.Id.ToJson()), ("content", content)));
                    mainWrites.Add(Object(("type", JsonValue.String("document.create")), ("record", create.Record.ToJson()), ("ordinal", JsonValue.Number(ordinal))));
                    break;
                }
                case DocumentChangeWrite change:
                {
                    var ordinal = AddSidecar(SidecarName("doc", change.Id.Value),
                        Object(("type", JsonValue.String("document")), ("id", change.Id.ToJson()), ("content", change.Content.ToJson())));
                    mainWrites.Add(Object(("type", JsonValue.String("document.change")), ("id", change.Id.ToJson()), ("ordinal", JsonValue.Number(ordinal))));
                    break;
                }
                default:
                    throw new InvalidOperationException("Prepared document copy was not resolved");
            }
        }

        var sidecars = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, fileRecords) in records) sidecars.Add(file, string.Concat(fileRecords.Select(JsonLine)));
        var marker = Object(("format", JsonValue.Number(FormatVersion)), ("type", JsonValue.String("commit")), ("seq", seq.ToJson()),
            ("writes", JsonValue.Array(mainWrites)));
        return new(JsonLine(marker), sidecars);
    }

    private OrderedDictionary<string, string> PlanReclamations(ImmutableArray<StorageWrite> writes, EncodedCommit encoded)
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

        var replacements = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        bool IsCurrentOnlyDocument(long id) => currentOnlyDocuments.Contains(id) || createdCurrentOnlyDocuments.Contains(id);
        foreach (var id in retiredDocuments)
            if (IsCurrentOnlyDocument(id)) replacements[SidecarName("doc", id)] = "";
        foreach (var id in baseDocuments)
        {
            if (!IsCurrentOnlyDocument(id) || retiredDocuments.Contains(id)) continue;
            var file = SidecarName("doc", id);
            if (encoded.Sidecars.TryGetValue(file, out var content)) replacements[file] = content;
        }
        foreach (var (id, task) in finalTasks)
        {
            if (task.State.Status == DurableTaskStatus.Terminal && (liveTaskSidecars.Contains(id) || encoded.Sidecars.ContainsKey(SidecarName("task", id))))
                replacements[SidecarName("task", id)] = "";
        }
        return replacements;
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

    /// <summary>The marker already published this state, so reclamation is retryable best-effort maintenance.</summary>
    private async Task ReclaimSidecarsAsync(OrderedDictionary<string, string> replacements, DurableContext context)
    {
        if (replacements.Count == 0) return;
        if (fsync)
        {
            var flushed = await fs.FlushFileAsync(mainPath, context);
            if (!flushed.IsOk) return;
        }
        foreach (var (file, content) in replacements) await ReplaceSidecarAsync(file, content, context);
    }

    private async Task ReplaceSidecarAsync(string file, string content, DurableContext context)
    {
        var path = await fs.JoinPathAsync([directory, file], context);
        if (!path.IsOk) return;
        if (content.Length == 0)
        {
            await fs.RemoveAsync(path.Value, new RemoveOptions { Force = true }, context);
            return;
        }
        var temporaryPath = await fs.JoinPathAsync([directory, file + ReclaimSuffix], context);
        if (!temporaryPath.IsOk) return;
        var written = await fs.WriteFileAsync(temporaryPath.Value, Utf8.GetBytes(content), context);
        if (!written.IsOk) return;
        if (fsync)
        {
            var flushed = await fs.FlushFileAsync(temporaryPath.Value, context);
            if (!flushed.IsOk) return;
        }
        await fs.RenameFileAsync(temporaryPath.Value, path.Value, context);
    }

    private async Task RecoverAsync(DurableContext context)
    {
        var main = await ReadLinesAsync(fs, mainPath, MainFile, context, ParseMainMarker);
        var previousSeq = 0L;
        foreach (var marker in main.Lines)
        {
            if (marker.Value.Seq.Value <= previousSeq) throw new JsonlCorruptionException($"Commit sequence does not strictly increase in {MainFile}");
            previousSeq = marker.Value.Seq.Value;
        }

        var listed = await fs.ListDirAsync(directory, context);
        if (!listed.IsOk) throw ErrorFromFile("directory listing", listed.Error);
        foreach (var info in listed.Value)
            if (info.Kind == FileKind.File && ReclaimFileName.IsMatch(info.Name)) await fs.RemoveAsync(info.Path, new RemoveOptions { Force = true }, context);
        var sidecarFiles = listed.Value.Where(info => info.Kind == FileKind.File && SidecarFileName.IsMatch(info.Name))
            .Select(info => info.Name).Order(StringComparer.Ordinal).ToList();
        var parsedFiles = new OrderedDictionary<string, ParsedFile<SidecarRecord>>(StringComparer.Ordinal);
        var recordByKey = new Dictionary<(string File, long Seq, long Ordinal), ParsedLine<SidecarRecord>>();
        foreach (var file in sidecarFiles)
        {
            var pathResult = await fs.JoinPathAsync([directory, file], context);
            if (!pathResult.IsOk) throw ErrorFromFile("path join", pathResult.Error);
            var parsed = await ReadLinesAsync(fs, pathResult.Value, file, context, (text, line) => ParseSidecarRecord(text, file, line));
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
            try
            {
                foreach (var operation in marker.Writes)
                {
                    switch (operation.Type)
                    {
                        case MainOperationType.Conversation or MainOperationType.Entry or MainOperationType.Submission or
                            MainOperationType.Task or MainOperationType.DocumentRetire:
                            writes.Add(operation.Write!);
                            break;
                        case MainOperationType.TaskSidecar:
                        {
                            var optional = terminalTasks.Contains(operation.Id);
                            var record = ConfirmRecord(marker, operation.Ordinal, SidecarName("task", operation.Id), recordByKey, confirmed, optional);
                            if (record is not null)
                            {
                                if (!record.IsTask || record.PayloadId != operation.Id)
                                    throw new JsonlCorruptionException($"Confirmed task sidecar data does not match commit {marker.Seq}");
                                if (!optional) writes.Add(new TaskWrite(TaskRecord.FromJson(record.Payload)));
                            }
                            break;
                        }
                        case MainOperationType.DocumentCreate or MainOperationType.DocumentChange:
                        {
                            var id = operation.Id;
                            var reclaimed = retiredCurrentOnlyDocuments.Contains(id) || IsBeforeLatestBase(id, marker.Seq, operation.Ordinal);
                            var record = ConfirmRecord(marker, operation.Ordinal, SidecarName("doc", id), recordByKey, confirmed, reclaimed);
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
                            }
                            else if (!reclaimed && record is not null)
                            {
                                writes.Add(new DocumentChangeWrite(new DocumentId(id), DocumentContent.FromJson(record.Payload)));
                            }
                            break;
                        }
                    }
                }
                memory.PrepareCommit(writes, marker.Seq).Apply();
            }
            catch (Exception error) when (error is not JsonlCorruptionException)
            {
                throw new JsonlCorruptionException($"Invalid committed state at sequence {marker.Seq}", error);
            }
        }

        var reclamations = new OrderedDictionary<string, string>(StringComparer.Ordinal);
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
            if (unconfirmedAt is { } size)
            {
                var truncated = await fs.TruncateFileAsync(parsed.Path, size, context);
                if (!truncated.IsOk) throw ErrorFromFile($"tail truncation of {file}", truncated.Error);
            }

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
                reclamations[file] = string.Concat(retainedLines.Select(line => JsonLine(line.Value.Raw)));
        }
        await ReclaimSidecarsAsync(reclamations, context);

        foreach (var id in currentOnly) currentOnlyDocuments.Add(id);
        foreach (var (id, live) in finalTaskIsLive)
            if (live) liveTaskSidecars.Add(id);
    }

    private static SidecarRecord? ConfirmRecord(MainMarker marker, long ordinal, string file,
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
        return line.Value;
    }

    private static async Task<ParsedFile<T>> ReadLinesAsync<T>(IFileSystem fs, string path, string name, DurableContext context, Func<string, int, T> parse)
    {
        var read = await fs.ReadBinaryFileAsync(path, context);
        if (!read.IsOk)
        {
            if (read.Error.Code == FileErrorCode.NotFound) return new(path, []);
            throw ErrorFromFile($"read of {name}", read.Error);
        }
        var bytes = read.Value;
        var completeSize = bytes.Length;
        if (completeSize > 0 && bytes[completeSize - 1] != (byte)'\n')
        {
            completeSize = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            var truncated = await fs.TruncateFileAsync(path, completeSize, context);
            if (!truncated.IsOk) throw ErrorFromFile($"torn-line truncation of {name}", truncated.Error);
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
                // A fatal TextDecoder, which drops a leading byte-order mark from each decoded line.
                var span = bytes.AsSpan(start, end - start);
                if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..];
                text = StrictUtf8.GetString(span);
            }
            catch (DecoderFallbackException error)
            {
                throw new JsonlCorruptionException($"Invalid UTF-8 in complete {name} line {lineNumber}", error);
            }
            lines.Add(new(parse(text, lineNumber), start));
            start = end + 1;
            lineNumber++;
        }
        return new(path, lines);
    }

    private async Task<string> ResolveFileAsync(string file, DurableContext context)
    {
        var path = await fs.JoinPathAsync([directory, file], context);
        if (!path.IsOk) throw ErrorFromFile("path join", path.Error);
        return path.Value;
    }

    private Task<T> Store<T>(Func<MemoryStorage, Task<T>> read)
    {
        try { AssertUsable(); }
        catch (Exception error) { return Task.FromException<T>(error); }
        return read(memory);
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
    /// <see cref="ContentKind"/> and content as <see cref="Payload"/>). <see cref="Raw"/> is the parsed line, rewritten on reclamation.</summary>
    private sealed record SidecarRecord(Seq Seq, long Ordinal, bool IsTask, long PayloadId, string? ContentKind, JsonValue Payload, JsonObjectValue Raw);

    private sealed record ParsedLine<T>(T Value, long Start);

    private sealed record ParsedFile<T>(string Path, List<ParsedLine<T>> Lines);

    private static string SidecarName(string kind, long id) => $"{kind}-{id}.jsonl";

    private static bool IsCurrentOnly(DocumentCreate record) =>
        record.Scope.Kind != DocumentScopeKind.Conversation || record.History == DocumentHistory.Latest;

    private static string JsonLine(JsonValue value) => value.ToJson() + "\n";

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

    private static string ValidateDocumentContent(JsonValue? value, string description)
    {
        if (value is not JsonObjectValue || !IsSafeInteger(Field(value, "version"), out var version) || version < 1)
            throw new JsonlCorruptionException($"Invalid document content in {description}");
        var kind = StringField(value, "kind");
        if (kind == "base" && Field(value, "value") is JsonObjectValue) return kind;
        if (kind == "delta" && Field(value, "ops") is JsonArrayValue) return kind;
        throw new JsonlCorruptionException($"Invalid document content in {description}");
    }

    private static SidecarRecord ParseSidecarRecord(string text, string file, int line)
    {
        var description = $"{file} line {line}";
        var value = ParseJson(text, description);
        var payload = Field(value, "payload");
        if (value is not JsonObjectValue raw || Field(value, "format") is not JsonNumberValue { Value: FormatVersion } ||
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
            return new(new Seq(seq), ordinal, true, taskId, null, task, raw);
        }
        if (type == "document")
        {
            if (!IsSafeInteger(Field(payload, "id"), out var documentId)) throw new JsonlCorruptionException($"Invalid document record in {description}");
            var content = Field(payload, "content");
            var kind = ValidateDocumentContent(content, description);
            return new(new Seq(seq), ordinal, false, documentId, kind, content!, raw);
        }
        throw new JsonlCorruptionException($"Unknown sidecar record type in {description}");
    }
}
