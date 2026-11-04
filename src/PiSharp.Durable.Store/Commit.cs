// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/store/commit.ts
//
// One Durable commit as one commit of the store's series (slice 6b §2.3): staged before the queue (rows encoded, contents serialized,
// explicit ids raising the store's allocator), then inside the series' transaction what SqliteStorage does inside its own, with the step's
// reader: Durable's checks of ids and documents and the copies resolved from their sources (its messages, a refusal), then its one
// composite write: the table puts, and the documents in two passes, existing ones first (their content, then their retirement, which frees
// the addresses this commit's creations take), then the creations and copies. Upstream's Error is an InvalidOperationException (its
// cause, an inner exception), as MemoryStorage and SqliteStorage throw Durable's messages; Durable's record types are StorageTable. A
// value crosses into the engine as its JSON text (JSON.stringify of a Durable value is its ToJson), parsed into the engine's JsonNode where
// the outbox records it.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using PiSharp.Contracts;
using PiSharp.Durable.Storage;
using DurableJson = PiSharp.Durable.Chord.JsonValue;
using DurableObject = PiSharp.Durable.Chord.JsonObjectValue;
using EngineBase = PiSharp.Chord.Store.DocumentBase;
using EngineContent = PiSharp.Chord.Store.DocumentContent;
using EngineRecord = PiSharp.Chord.Store.DocumentRecord;

namespace PiSharp.Durable.Store;

/// <summary>The storage's tables, as a commit writes them (upstream <c>CommitTables</c>).</summary>
internal sealed record StoreCommitTables(OwnerTable RecordIds, OwnerTable Conversations, OwnerTable Entries, OwnerTable EntryHeads, OwnerTable Tasks,
    OwnerTable Submissions);

/// <summary>A Durable commit staged for the store's series: its checks, then itself as its one write (upstream <c>DurableCommit</c>).</summary>
internal sealed class DurableCommit : ISeriesCommit, IStagedWrite
{
    private readonly string owner;
    private readonly StoreCommitTables tables;
    private readonly ChordContext context;
    /// <summary>Every id a write claims, in write order (documents created or copied included), for the id checks.</summary>
    private readonly List<(long Id, StorageTable Type)> claims = [];
    private readonly List<RowWrite> rows = [];
    private readonly OrderedDictionary<long, DocumentAction> actions = new();
    /// <summary>What <see cref="CheckAsync"/> found for <see cref="ApplyAsync"/>, in the same step: the stored types of the claimed ids,
    /// the changed documents.</summary>
    private (Dictionary<long, StorageTable> Claimed, OrderedDictionary<long, EngineRecord> Existing)? checkedState;

    /// <summary>Stages <paramref name="writes"/> (rule 8: rows and contents encoded here, before the queue); <paramref name="raise"/> makes
    /// room above each explicit id. A malformed batch (two content commands for one document, a second retirement) throws Durable's
    /// message.</summary>
    internal DurableCommit(IReadOnlyList<StorageWrite> writes, string owner, StoreCommitTables tables, Action<long> raise, ChordContext context)
    {
        this.owner = owner;
        this.tables = tables;
        this.context = context;
        foreach (var write in writes) Stage(write, raise);
        Writes = [this];
    }

    /// <summary>A commit's one write: this one.</summary>
    public WriteOrder Order => WriteOrder.Write;

    /// <inheritdoc/>
    public IReadOnlyList<IStagedWrite> Writes { get; }

    /// <summary>Durable's checks against the state the series' earlier commits left, writing nothing: the ids, the documents and the
    /// copies; a throw refuses the commit.</summary>
    public async Task CheckAsync(Step step)
    {
        var claimed = await CheckIdsAsync(step).ConfigureAwait(false);
        var existing = await CheckDocumentsAsync(step).ConfigureAwait(false);
        await ResolveCopiesAsync(step).ConfigureAwait(false);
        checkedState = (claimed, existing);
    }

    /// <summary>The commit's writes, after its <see cref="CheckAsync"/> in the same step.</summary>
    public async Task<IReadOnlyList<StoreWrite>> ApplyAsync(Step step)
    {
        var recorded = new List<StoreWrite>();
        var (claimed, existing) = checkedState!.Value;
        foreach (var write in rows)
        {
            if (!claimed.ContainsKey(write.Id)) recorded.Add(await ClaimAsync(step, write.Id, write.Type).ConfigureAwait(false));
            claimed[write.Id] = write.Type;
            var stamp = ReferenceEquals(write.Table, tables.Entries) ? "commit_seq" : null;
            recorded.Add(await write.Table.PutAsync(step, write.Row, stamp).ConfigureAwait(false));
        }
        // Existing documents first: a retirement frees its address before a creation of this commit takes it.
        foreach (var (id, record) in existing) recorded.AddRange(await ChangeAsync(step, record, actions[id]).ConfigureAwait(false));
        foreach (var (id, action) in actions)
        {
            if (action.Create is null) continue;
            recorded.Add(await ClaimAsync(step, id, StorageTable.Document).ConfigureAwait(false));
            recorded.Add(await CreateAsync(step, action.Create, action.Base!, action.Retire).ConfigureAwait(false));
        }
        return recorded;
    }

    /// <summary>Resolves the copies (S11): each source read at its point and checked against the copy's record, entered as a creation
    /// carrying the source's value as its base.</summary>
    private async Task ResolveCopiesAsync(Step step)
    {
        foreach (var (id, action) in actions)
        {
            if (action.Copy is not { } source) continue;
            if (actions.ContainsKey(source.Id.Value)) throw new InvalidOperationException($"Document copy {id} source is changed in the copy batch");
            try
            {
                var read = await StoreDocuments.ReadAtAsync(step, source.Id, source.At, owner).ConfigureAwait(false);
                if (read.CurrentOnly is { } currentOnly) throw new StorageRequestException($"Document {currentOnly} does not retain historical content");
                var stored = read.Document ?? throw new InvalidOperationException($"Fork source document {source.Id} cannot be read");
                StorageRules.CheckCopySource(stored, action.Create!);
                action.Base = new StagedBase(stored.Version, stored.Value, stored.Value.ToJson());
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Document copy {id} was rejected", error);
            }
        }
    }

    /// <summary>Stages one write: a table row encoded, a document command into its action.</summary>
    private void Stage(StorageWrite write, Action<long> raise)
    {
        switch (write)
        {
            case ConversationWrite conversation:
                Row(conversation.Value.Id.Value, StorageTable.Conversation, tables.Conversations, StoreTables.ConversationRow(conversation.Value), raise);
                return;
            case EntryWrite entry:
            {
                var value = entry.Value;
                Row(value.Id.Value, StorageTable.Entry, tables.Entries, StoreTables.EntryRow(value), raise);
                // Its claim is the entry's: ApplyAsync claims an id once.
                if (value.Head is not null)
                {
                    var row = tables.EntryHeads.Encode(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                    {
                        ["id"] = value.Id.Value,
                        ["conversation_id"] = value.ConversationId.Value,
                    });
                    rows.Add(new RowWrite(value.Id.Value, StorageTable.Entry, tables.EntryHeads, row));
                }
                return;
            }
            case TaskWrite task:
                Row(task.Value.Id.Value, StorageTable.Task, tables.Tasks, StoreTables.TaskRow(task.Value), raise);
                return;
            case SubmissionWrite submission:
                Row(submission.Value.Id.Value, StorageTable.Submission, tables.Submissions, StoreTables.SubmissionRow(submission.Value), raise);
                return;
            case DocumentCreateWrite or DocumentCopyWrite:
            {
                var record = write is DocumentCreateWrite created ? created.Record : ((DocumentCopyWrite)write).Record;
                var id = record.Id.Value;
                var action = Action(id);
                if (action.Create is not null || action.Content is not null || action.Copy is not null)
                    throw new InvalidOperationException($"Document {id} has more than one content command");
                action.Create = record;
                if (write is DocumentCopyWrite copy) action.Copy = copy.Source;
                else
                {
                    var content = ((DocumentCreateWrite)write).Content;
                    action.Base = new StagedBase(content.Version, content.Value, content.Value.ToJson());
                }
                claims.Add((id, StorageTable.Document));
                raise(id);
                return;
            }
            case DocumentChangeWrite change:
            {
                var action = Action(change.Id.Value);
                // A creation's base is its content command (SqliteStorage counts it so too), in either order.
                if (action.Create is not null || action.Content is not null || action.Copy is not null)
                    throw new InvalidOperationException($"Document {change.Id} has more than one content command");
                var text = change.Content switch
                {
                    Durable.DocumentBase value => value.Value.ToJson(),
                    DocumentDelta delta => DurableJson.Array(delta.Ops.Select(op => (DurableJson)op.ToJson())).ToJson(),
                    _ => throw new InvalidOperationException("An unknown document content."),
                };
                action.Content = new StagedContent(change.Content, text);
                return;
            }
            case DocumentRetireWrite retire:
            {
                var action = Action(retire.Id.Value);
                if (action.Retire) throw new InvalidOperationException($"Document {retire.Id} is retired more than once");
                action.Retire = true;
                return;
            }
            default:
                throw new InvalidOperationException($"An unknown storage write: {write.Type}");
        }
    }

    private void Row(long id, StorageTable type, OwnerTable table, IReadOnlyDictionary<string, JsonNode?> row, Action<long> raise)
    {
        claims.Add((id, type));
        rows.Add(new RowWrite(id, type, table, table.Encode(row)));
        raise(id);
    }

    private DocumentAction Action(long id)
    {
        if (!actions.TryGetValue(id, out var action))
        {
            action = new DocumentAction();
            actions.Add(id, action);
        }
        return action;
    }

    /// <summary>Durable's global id checks (SqliteStorage's CheckGlobalIds): a conversation, entry or document id claimed by anything, or
    /// written twice; a task or submission id claimed by another type. Returns the stored types.</summary>
    private async Task<Dictionary<long, StorageTable>> CheckIdsAsync(Step step)
    {
        var stored = new Dictionary<long, StorageTable?>();
        var claimed = new Dictionary<long, StorageTable>();
        foreach (var (id, type) in claims)
        {
            if (!stored.TryGetValue(id, out var existing))
            {
                var row = await tables.RecordIds.GetAsync(step, [id], context).ConfigureAwait(false);
                existing = row?["record_type"] is { } recordType ? StoreRecordTypes.Parse((string)recordType!) : null;
                stored[id] = existing;
            }
            var hasEarlier = claimed.TryGetValue(id, out var earlier);
            if (type is StorageTable.Conversation or StorageTable.Entry or StorageTable.Document)
            {
                if (existing is { } owned) throw new InvalidOperationException($"ID {id} already belongs to {StorageRules.Name(owned)}");
                if (hasEarlier) throw new InvalidOperationException($"ID {id} is written more than once");
            }
            else
            {
                if (existing is { } owned && owned != type) throw new InvalidOperationException($"ID {id} already belongs to {StorageRules.Name(owned)}");
                if (hasEarlier && earlier != type) throw new InvalidOperationException($"ID {id} is written as two record types");
            }
            claimed[id] = type;
        }
        var types = new Dictionary<long, StorageTable>();
        foreach (var (id, type) in stored)
        {
            if (type is { } known) types[id] = known;
        }
        return types;
    }

    /// <summary>Durable's document checks (SqliteStorage's CheckDocumentActions): a change or retirement of an unknown or retired document,
    /// a delta on another version, more than one live incarnation per address after the commit. Returns the existing records the commit
    /// changes or retires, in action order.</summary>
    private async Task<OrderedDictionary<long, EngineRecord>> CheckDocumentsAsync(Step step)
    {
        var existing = new OrderedDictionary<long, EngineRecord>();
        foreach (var (id, action) in actions)
        {
            if (action.Create is not null) continue;
            var stored = await DocumentEngine.ReadDocumentAsync(step, id).ConfigureAwait(false);
            var record = stored is null ? null : StoreDocuments.DurableRecord(stored, owner);
            if (record is null) throw new InvalidOperationException($"Unknown document: {id}");
            if (record.RetiredAt is not null) throw new InvalidOperationException($"Document {id} is retired");
            if (action.Content?.Content is DocumentDelta delta && delta.Version != stored!.Version)
                throw new InvalidOperationException($"Document {id} version transition requires a base");
            existing.Add(id, stored!);
        }
        // Per address a live creation takes: its live incarnations after the commit so far.
        var live = new Dictionary<(string Namespace, string Kind, string? Key), int>();
        foreach (var action in actions.Values)
        {
            if (action.Create is null || action.Retire) continue;
            var address = StoreDocuments.AddressOf(action.Create, owner);
            var key = (address.Namespace, address.Kind, address.Key);
            if (!live.TryGetValue(key, out var count))
            {
                // The incarnation live there now counts unless this commit retires it.
                var current = await DocumentEngine.FindDocumentAsync(step, address).ConfigureAwait(false);
                count = current is null || (actions.TryGetValue(current.Id, out var retiring) && retiring.Retire) ? 0 : 1;
            }
            if (++count > 1) throw new InvalidOperationException("Document address already has a current incarnation");
            live[key] = count;
        }
        return existing;
    }

    /// <summary>Puts <paramref name="id"/>'s <c>record_ids</c> row: its record type.</summary>
    private Task<StoreWrite> ClaimAsync(Step step, long id, StorageTable type)
    {
        var table = tables.RecordIds;
        return table.PutAsync(step, table.Encode(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["record_type"] = StorageRules.Name(type),
        }));
    }

    /// <summary>An existing document's content, then its retirement, as their writes.</summary>
    private static async Task<List<StoreWrite>> ChangeAsync(Step step, EngineRecord record, DocumentAction action)
    {
        var writes = new List<StoreWrite>();
        var revision = record.Revision;
        if (action.Content is { } content)
        {
            var stored = EngineContentOf(content);
            await DocumentEngine.WriteContentAsync(step, record.Id, record.History, revision, stored, content.Text).ConfigureAwait(false);
            writes.Add(new DocumentWrite.Change(record.Id, revision, stored));
            revision++;
        }
        if (action.Retire)
        {
            await DocumentEngine.RetireRecordAsync(step, record).ConfigureAwait(false);
            writes.Add(new DocumentWrite.Retire(record.Id, revision));
        }
        return writes;
    }

    /// <summary>A creation or a resolved copy (retired at once for a create-plus-retire, an empty lifetime), as its write.</summary>
    private async Task<StoreWrite> CreateAsync(Step step, DocumentCreate create, StagedBase stagedBase, bool retired)
    {
        var record = StoreDocuments.EngineRecordOf(create, owner, stagedBase.Version, step.Seq, retired);
        await DocumentEngine.InsertDocumentAsync(step, record, stagedBase.Text).ConfigureAwait(false);
        return new DocumentWrite.Create(record, new EngineBase(stagedBase.Version, "", StoreTables.Node(stagedBase.Value)));
    }

    /// <summary>A Durable content as the engine stores it: its documents have no schema fingerprint.</summary>
    private static EngineContent EngineContentOf(StagedContent content) => content.Content switch
    {
        Durable.DocumentBase value => new EngineContent.Base(value.Version, "", StoreTables.Node(value.Value)),
        DocumentDelta delta =>
            new EngineContent.Delta(delta.Version, StoreTables.Node(DurableJson.Array(delta.Ops.Select(op => (DurableJson)op.ToJson())))!.AsArray()),
        _ => throw new InvalidOperationException("Unknown document content"),
    };

    /// <summary>A table write of the commit: its record's id and type, its table and its row encoded at staging.</summary>
    private sealed record RowWrite(long Id, StorageTable Type, OwnerTable Table, EncodedRow Row);

    /// <summary>A base as the commit stores it: its version and value, and the value serialized.</summary>
    private sealed record StagedBase(long Version, DurableObject Value, string Text);

    /// <summary>A change's content and its value or ops serialized.</summary>
    private sealed record StagedContent(DocumentContent Content, string Text);

    /// <summary>What one commit does with one document, as SqliteStorage's action map has it.</summary>
    private sealed class DocumentAction
    {
        public DocumentCreate? Create { get; set; }

        public DocumentCopySource? Copy { get; set; }

        /// <summary>A creation's base: the write's own, or a copy's once resolved.</summary>
        public StagedBase? Base { get; set; }

        public StagedContent? Content { get; set; }

        public bool Retire { get; set; }
    }
}

/// <summary>A batch whose staging threw: a refusal in its turn, the batches before it stored (upstream's object literal
/// <c>{ check: () =&gt; Promise.reject(error), writes: [] }</c>).</summary>
internal sealed class RefusedCommit(Exception error) : ISeriesCommit
{
    public Task CheckAsync(Step step) => Task.FromException(error);

    public IReadOnlyList<IStagedWrite> Writes => [];
}

/// <summary>Durable's record types as <c>record_ids</c> names them.</summary>
internal static class StoreRecordTypes
{
    internal static StorageTable Parse(string name) =>
        Internal.WireName.TryParse<StorageTable>(name, out var type) ? type : throw new InvalidOperationException($"Unknown record type: {name}");
}
