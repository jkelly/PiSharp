// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): the batch validation, document addressing and paging rules that
// packages/durable/src/storage/memory.ts and storage/sqlite/storage.ts each spell out. Upstream repeats them per backend; this port keeps
// one copy, which MemoryStorage and SqliteStorage both call. Each backend still runs its own lookups and keeps upstream's order of checks.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable.Storage;

/// <summary>The record table that owns a global ID. Persisted names are upstream's <c>record_type</c> values.</summary>
internal enum StorageTable { Conversation, Entry, Task, Submission, Document }

/// <summary>Every command one batch gives one document ID (upstream <c>DocumentAction</c>).</summary>
internal sealed class DocumentBatchAction
{
    public DocumentCreate? Create { get; set; }
    public DocumentCopySource? Copy { get; set; }
    public DocumentContent? Content { get; set; }
    public bool Retire { get; set; }
}

/// <summary>What a backend knows about one stored document when a batch is checked.</summary>
/// <param name="Record">The stored record.</param>
/// <param name="LastVersion">The version of its newest stored revision, or null when it has none.</param>
internal readonly record struct DocumentCheckState(DocumentRecord Record, int? LastVersion);

internal static class StorageRules
{
    /// <summary>The table and ID a write claims; document changes and retirements claim none.</summary>
    public static (StorageTable Table, long Id)? Claim(StorageWrite write) => write switch
    {
        ConversationWrite conversation => (StorageTable.Conversation, conversation.Value.Id.Value),
        EntryWrite entry => (StorageTable.Entry, entry.Value.Id.Value),
        TaskWrite task => (StorageTable.Task, task.Value.Id.Value),
        SubmissionWrite submission => (StorageTable.Submission, submission.Value.Id.Value),
        DocumentCreateWrite create => (StorageTable.Document, create.Record.Id.Value),
        DocumentCopyWrite copy => (StorageTable.Document, copy.Record.Id.Value),
        _ => null,
    };

    /// <summary>The persisted name of <paramref name="table"/>, such as <c>"conversation"</c>.</summary>
    public static string Name(StorageTable table) => WireName.Of(table);

    /// <summary>
    /// Enforces the one global ID namespace (upstream <c>checkGlobalIds</c>): conversations, entries and documents are created once; tasks
    /// and submissions may be replaced, but only by the same record type. <paramref name="existing"/> returns the table that already owns
    /// an ID.
    /// </summary>
    public static void CheckGlobalIds(IEnumerable<StorageWrite> writes, Func<long, StorageTable?> existing)
    {
        var claimed = new Dictionary<long, StorageTable>();
        foreach (var write in writes)
        {
            if (Claim(write) is not { } claim) continue;
            var (table, id) = claim;
            var owner = existing(id);
            var hasEarlier = claimed.TryGetValue(id, out var earlier);
            if (table is StorageTable.Conversation or StorageTable.Entry or StorageTable.Document)
            {
                if (owner is { } stored) throw new InvalidOperationException($"ID {id} already belongs to {Name(stored)}");
                if (hasEarlier) throw new InvalidOperationException($"ID {id} is written more than once");
            }
            else
            {
                if (owner is { } stored && stored != table) throw new InvalidOperationException($"ID {id} already belongs to {Name(stored)}");
                if (hasEarlier && earlier != table) throw new InvalidOperationException($"ID {id} is written as two record types");
            }
            claimed[id] = table;
        }
    }

    /// <summary>Groups a batch's document commands by ID, rejecting two content commands or two retirements of one document (upstream
    /// <c>prepareDocumentActions</c>). A copy counts as the document's content command.</summary>
    public static OrderedDictionary<long, DocumentBatchAction> PrepareDocumentActions(IEnumerable<StorageWrite> writes)
    {
        var actions = new OrderedDictionary<long, DocumentBatchAction>();
        foreach (var write in writes)
        {
            var id = write switch
            {
                DocumentCreateWrite create => create.Record.Id,
                DocumentCopyWrite copy => copy.Record.Id,
                DocumentChangeWrite change => change.Id,
                DocumentRetireWrite retire => retire.Id,
                _ => (DocumentId?)null,
            };
            if (id is not { } documentId) continue;
            if (!actions.TryGetValue(documentId.Value, out var action)) actions.Add(documentId.Value, action = new DocumentBatchAction());
            switch (write)
            {
                case DocumentCreateWrite create:
                    if (action.Create is not null || action.Content is not null || action.Copy is not null)
                        throw new InvalidOperationException($"Document {documentId} has more than one content command");
                    action.Create = create.Record;
                    action.Content = create.Content;
                    break;
                case DocumentCopyWrite copy:
                    if (action.Create is not null || action.Content is not null || action.Copy is not null)
                        throw new InvalidOperationException($"Document {documentId} has more than one content command");
                    action.Create = copy.Record;
                    action.Copy = copy.Source;
                    break;
                case DocumentChangeWrite change:
                    if (action.Content is not null || action.Copy is not null)
                        throw new InvalidOperationException($"Document {documentId} has more than one content command");
                    action.Content = change.Content;
                    break;
                default:
                    if (action.Retire) throw new InvalidOperationException($"Document {documentId} is retired more than once");
                    action.Retire = true;
                    break;
            }
        }
        return actions;
    }

    /// <summary>
    /// Checks a batch's document commands against stored state (upstream <c>checkDocumentActions</c>): existence, retirement, the delta
    /// version chain, and at most one current incarnation per logical address after the batch. <paramref name="stored"/> returns a stored
    /// document; <paramref name="currentId"/> returns the current incarnation at an <see cref="AddressKey(IDocumentDescriptor)"/>.
    /// </summary>
    public static void CheckDocumentActions(OrderedDictionary<long, DocumentBatchAction> actions, Func<long, DocumentCheckState?> stored,
        Func<string, long?> currentId)
    {
        var liveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, action) in actions)
        {
            if (action.Copy is { } copy && actions.ContainsKey(copy.Id.Value))
                throw new InvalidOperationException($"Document copy {id} source is changed in the copy batch");
            var existing = stored(id);
            if (action.Create is null && existing is null) throw new InvalidOperationException($"Unknown document: {id}");
            if (action.Create is not null && existing is not null) throw new InvalidOperationException($"Document {id} already exists");
            if (existing?.Record.RetiredAt is not null) throw new InvalidOperationException($"Document {id} is retired");
            if (action.Content is DocumentDelta delta)
            {
                if (existing?.LastVersion is not { } previous) throw new InvalidOperationException($"Document {id} delta has no base");
                if (previous != delta.Version) throw new InvalidOperationException($"Document {id} version transition requires a base");
            }

            IDocumentDescriptor descriptor = action.Create is not null ? action.Create : existing!.Value.Record;
            var key = AddressKey(descriptor);
            var current = currentId(key);
            if (!liveCounts.TryGetValue(key, out var live)) live = current is null ? 0 : 1;
            if (action.Retire && current == id) live--;
            if (action.Create is not null && !action.Retire) live++;
            liveCounts[key] = live;
        }
        foreach (var live in liveCounts.Values)
            if (live > 1) throw new InvalidOperationException("Document address already has a current incarnation");
    }

    /// <summary>Rejects a copy whose stored source is not the same definition in a conversation scope.</summary>
    public static void CheckCopySource(StoredDocument source, DocumentCreate copy)
    {
        if (source.Record.Scope.Kind != DocumentScopeKind.Conversation || copy.Scope.Kind != DocumentScopeKind.Conversation ||
            !string.Equals(source.Record.Kind, copy.Kind, StringComparison.Ordinal) ||
            !string.Equals(source.Record.Key, copy.Key, StringComparison.Ordinal) ||
            source.Record.History != copy.History || source.Record.Fork != copy.Fork)
            throw new InvalidOperationException($"Fork source document {source.Record.Id} does not match the copied record");
    }

    /// <summary>Replays the deltas after a base. Each revision's header is checked before its operations are read, so a stored version
    /// boundary without a base is reported before any operation is decoded.</summary>
    public static JsonObjectValue Replay<TRevision>(DocumentId id, int baseVersion, JsonObjectValue baseValue, IEnumerable<TRevision> tail,
        Func<TRevision, (bool IsDelta, int Version)> header, Func<TRevision, IEnumerable<Op>> ops)
    {
        var value = DeltaApply.ApplyBatches(baseValue, Batches());
        return value as JsonObjectValue ?? throw new InvalidOperationException($"Document {id} does not replay to a JSON object");

        IEnumerable<IEnumerable<Op>> Batches()
        {
            foreach (var revision in tail)
            {
                var (isDelta, version) = header(revision);
                if (!isDelta || version != baseVersion)
                    throw new InvalidOperationException($"Document {id} crosses a stored version boundary without a base");
                yield return ops(revision);
            }
        }
    }

    /// <summary>The exact-scope key of <paramref name="scope"/>.</summary>
    public static string ScopeKey(DocumentScope scope) => scope.Kind switch
    {
        DocumentScopeKind.Session => JsonValue.Array(JsonValue.String("session")).ToJson(),
        DocumentScopeKind.Conversation => JsonValue.Array(JsonValue.String("conversation"), JsonValue.Number(scope.OwnerId)).ToJson(),
        _ => JsonValue.Array(JsonValue.String("task"), JsonValue.Number(scope.OwnerId)).ToJson(),
    };

    /// <summary>The logical-address key of a document: its kind, exact scope, and singleton or family key.</summary>
    public static string AddressKey(string kind, DocumentScope scope, string? key) => JsonValue.Array(
        JsonValue.String(kind), JsonValue.String(ScopeKey(scope)),
        key is null ? JsonValue.Array(JsonValue.String("singleton")) : JsonValue.Array(JsonValue.String("family"), JsonValue.String(key))).ToJson();

    /// <summary>The logical-address key of <paramref name="document"/>.</summary>
    public static string AddressKey(IDocumentDescriptor document) => AddressKey(document.Kind, document.Scope, document.Key);

    /// <summary>Whether an incarnation is alive at a point: current until retired; historically within its half-open lifetime.</summary>
    public static bool IsAliveAt(DocumentRecord record, DocumentPoint at) => at.Seq is not { } seq
        ? record.RetiredAt is null
        : record.CreatedAt <= seq && (record.RetiredAt is not { } retiredAt || seq < retiredAt);

    /// <summary>Whether only current content is retained: every non-conversation document, and conversation documents with latest
    /// history.</summary>
    public static bool IsCurrentOnly(DocumentRecord record) =>
        record.Scope.Kind != DocumentScopeKind.Conversation || record.History == DocumentHistory.Latest;

    /// <summary>The ID a document scan cursor continues after (upstream <c>cursorId</c>).</summary>
    public static long? CursorId(JsonObjectValue? cursor)
    {
        if (cursor is null || !cursor.TryGetValue("after", out var after)) return null;
        if (after is not JsonNumberValue { Value: var number } || Math.Floor(number) != number || Math.Abs(number) > Ids.MaxSafeInteger)
            throw new StorageRequestException("Invalid storage cursor");
        return (long)number;
    }

    /// <summary>The first <paramref name="limit"/> values, and a cursor after the last one when more were found (upstream <c>page</c>).</summary>
    public static Page<T> MakePage<T>(IReadOnlyList<T> values, int limit, ScanOrder order, Func<T, long> id)
    {
        if (limit < 0 || (limit == 0 && values.Count > 0))
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A scan that returns items needs a positive limit");
        var items = values.Count <= limit ? [.. values] : values.Take(limit).ToImmutableArray();
        return values.Count <= limit ? new(items, null) : new(items, StorageScan.NextCursor(id(items[^1]), order));
    }
}
