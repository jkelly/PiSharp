// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/durable/src/storage/sqlite/commit.ts and statements.ts.
// Bounded prefetch, sequential semantic checks, and per-table pending writes. Copies flush pending writes before reading.
using System.Collections.Immutable;
using System.Text;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Sqlite;

public sealed partial class SqliteStorage
{
    private sealed class CommitState
    {
        private readonly Dictionary<long, StorageTable> owners = [];
        private readonly Dictionary<long, DocumentRecord> documents = [];
        private readonly Dictionary<long, int> versions = [];
        private readonly Dictionary<string, long?> current = new(StringComparer.Ordinal);

        public static async Task<CommitState> Read(ISqliteExecutor executor, SqliteTableNames tables,
            IReadOnlyList<IReadOnlyList<StorageWrite>> batches)
        {
            var state = new CommitState();
            var ids = new HashSet<long>();
            var documentIds = new HashSet<long>();
            var deltaIds = new HashSet<long>();
            var addresses = new Dictionary<string, AddressParts>(StringComparer.Ordinal);
            foreach (var writes in batches)
            foreach (var write in writes)
            {
                if (StorageRules.Claim(write) is { } claim) ids.Add(claim.Id);
                switch (write)
                {
                    case DocumentCreateWrite create: AddCreate(create.Record); break;
                    case DocumentCopyWrite copy: AddCreate(copy.Record); break;
                    case DocumentChangeWrite change:
                        documentIds.Add(change.Id.Value);
                        if (change.Content is DocumentDelta) deltaIds.Add(change.Id.Value);
                        break;
                    case DocumentRetireWrite retire: documentIds.Add(retire.Id.Value); break;
                }
            }
            foreach (var row in await ReadRows(executor,
                $"SELECT id, record_type FROM {tables.RecordIds} WHERE id IN (SELECT value FROM json_each(?))", ids.Cast<object?>()).ConfigureAwait(false))
                state.owners.Add(row.GetInt64("id"), ParseTable(row.GetString("record_type")));
            foreach (var row in await ReadRows(executor,
                $"SELECT record FROM {tables.Documents} WHERE id IN (SELECT value FROM json_each(?))", documentIds.Cast<object?>()).ConfigureAwait(false))
            {
                var record = DocumentRecord.FromJson(Record(row));
                state.documents.Add(record.Id.Value, record);
                addresses[StorageRules.AddressKey(record)] = AddressParts.Of(record.Kind, record.Scope, record.Key);
            }
            foreach (var row in await ReadRows(executor, $"""
                SELECT requested.value AS document_id, (SELECT version FROM {tables.DocumentRevisions}
                WHERE document_id = requested.value ORDER BY seq DESC LIMIT 1) AS version FROM json_each(?) AS requested
                """, deltaIds.Cast<object?>()).ConfigureAwait(false))
            {
                var id = row.GetInt64("document_id");
                if (row.GetNullableInt64("version") is { } version) state.versions[id] = checked((int)version);
                else if (state.documents.TryGetValue(id, out var document) && document.RetiredAt is null)
                    throw new InvalidOperationException($"Document {id} is missing a required revision");
            }
            foreach (var key in addresses.Keys) state.current[key] = null;
            foreach (var row in await ReadRows(executor, $"""
                SELECT json_extract(address.value, '$[0]') AS address_key, document.id
                FROM json_each(?) AS address JOIN {tables.Documents} AS document
                ON document.kind = json_extract(address.value, '$[1]') AND document.scope_kind = json_extract(address.value, '$[2]')
                AND document.owner_id = json_extract(address.value, '$[3]') AND document.family = json_extract(address.value, '$[4]')
                AND document.key_value = json_extract(address.value, '$[5]') WHERE document.retired_at IS NULL
                """, addresses.Select(pair => (object?)new object?[]
                    { pair.Key, pair.Value.Kind, pair.Value.ScopeKind, pair.Value.OwnerId, pair.Value.Family, pair.Value.KeyValue })).ConfigureAwait(false))
                state.current[row.GetString("address_key")] = row.GetInt64("id");
            return state;

            void AddCreate(DocumentCreate record)
            {
                documentIds.Add(record.Id.Value);
                addresses[StorageRules.AddressKey(record)] = AddressParts.Of(record.Kind, record.Scope, record.Key);
            }
        }

        public void Check(IReadOnlyList<StorageWrite> writes, OrderedDictionary<long, DocumentBatchAction> actions)
        {
            StorageRules.CheckGlobalIds(writes, id => owners.TryGetValue(id, out var owner) ? owner : null);
            StorageRules.CheckDocumentActions(actions, id => documents.TryGetValue(id, out var record)
                ? new DocumentCheckState(record, versions.TryGetValue(id, out var version) ? version : null) : null,
                key => current.GetValueOrDefault(key));
        }

        public DocumentRecord Document(long id) => documents[id];

        public void Apply(IReadOnlyList<StorageWrite> writes, OrderedDictionary<long, DocumentBatchAction> actions, Seq seq)
        {
            foreach (var write in writes)
                if (StorageRules.Claim(write) is { } claim) owners[claim.Id] = claim.Table;
            foreach (var (id, action) in actions)
            {
                if (action.Create is { } create)
                {
                    var record = create.ToRecord(seq, action.Retire ? seq : null);
                    documents[id] = record;
                    if (!action.Retire) current[StorageRules.AddressKey(record)] = id;
                }
                else if (action.Retire)
                {
                    var record = documents[id] with { RetiredAt = seq };
                    documents[id] = record;
                    var key = StorageRules.AddressKey(record);
                    if (current.GetValueOrDefault(key) == id) current[key] = null;
                }
                if (action.Content is { } content) versions[id] = content.Version;
            }
        }
    }

    private sealed class PendingWrites
    {
        private readonly Dictionary<long, StorageTable> owners = [];
        private readonly List<object?[]> conversations = [];
        private readonly List<object?[]> entries = [];
        private readonly Dictionary<long, object?[]> tasks = [];
        private readonly Dictionary<long, object?[]> submissions = [];
        private readonly Dictionary<long, DocumentRecord> documents = [];
        private readonly HashSet<long> cleared = [];
        private readonly Dictionary<long, List<object?[]>> revisions = [];

        public void Add(IReadOnlyList<StorageWrite> writes, OrderedDictionary<long, DocumentBatchAction> actions, Seq seq, CommitState state)
        {
            foreach (var write in writes)
            {
                if (StorageRules.Claim(write) is { } claim) owners[claim.Id] = claim.Table;
                switch (write)
                {
                    case ConversationWrite { Value: var value }:
                        conversations.Add([value.Id.Value, value.Owner?.ConversationId.Value, value.Owner?.TaskId.Value, value.ToJson().ToJson()]);
                        break;
                    case EntryWrite { Value: var value }:
                        entries.Add([value.Id.Value, value.ConversationId.Value, value.Head?.Value, seq.Value, value.ToJson().ToJson()]);
                        break;
                    case TaskWrite { Value: var value }:
                        tasks[value.Id.Value] = [value.Id.Value, value.ConversationId.Value, EncodeIndexedString(value.Kind), WireName(value.State.Status),
                            value.AbortRequested ? 1L : 0L, value.Background ? 1L : 0L, value.ToJson().ToJson()];
                        break;
                    case SubmissionWrite { Value: var value }:
                        submissions[value.Id.Value] = [value.Id.Value, value.ConversationId.Value,
                            value.RequestId is null ? null : EncodeIndexedString(value.RequestId), WireName(value.Status), value.ToJson().ToJson()];
                        break;
                }
            }
            foreach (var (id, action) in actions)
            {
                var record = state.Document(id);
                if (action.Create is not null || action.Retire) documents[id] = record;
                if (action.Content is { } content)
                {
                    if (content is DocumentBase && StorageRules.IsCurrentOnly(record)) Clear(id);
                    if (!revisions.TryGetValue(id, out var rows)) revisions[id] = rows = [];
                    var encoded = content switch
                    {
                        DocumentBase value => value.Value.ToJson(),
                        DocumentDelta delta => JsonValue.Array(delta.Ops.Select(op => (JsonValue)op.ToJson())).ToJson(),
                        _ => throw new InvalidOperationException($"Document {id} has unknown content"),
                    };
                    rows.Add([id, seq.Value, content is DocumentBase ? "base" : "delta", (long)content.Version, encoded]);
                }
                if (action.Retire && StorageRules.IsCurrentOnly(record)) Clear(id);
            }
        }

        private void Clear(long id) { cleared.Add(id); revisions.Remove(id); }

        public async Task Flush(ISqliteExecutor executor, SqliteTableNames tables)
        {
            await InsertRows(executor, tables.RecordIds, ["id", "record_type"],
                owners.Select(pair => new object?[] { pair.Key, StorageRules.Name(pair.Value) }), "ON CONFLICT(id) DO NOTHING").ConfigureAwait(false);
            await InsertRows(executor, tables.Conversations, ["id", "owner_conversation_id", "owner_task_id", "record"], conversations).ConfigureAwait(false);
            await InsertRows(executor, tables.Entries, ["id", "conversation_id", "head", "commit_seq", "record"], entries).ConfigureAwait(false);
            await InsertRows(executor, tables.Tasks, ["id", "conversation_id", "kind", "status", "abort_requested", "background", "record"], tasks.Values,
                "ON CONFLICT(id) DO UPDATE SET conversation_id = excluded.conversation_id, kind = excluded.kind, status = excluded.status, " +
                "abort_requested = excluded.abort_requested, background = excluded.background, record = excluded.record").ConfigureAwait(false);
            await InsertRows(executor, tables.Submissions, ["id", "conversation_id", "request_id", "status", "record"], submissions.Values,
                "ON CONFLICT(id) DO UPDATE SET conversation_id = excluded.conversation_id, request_id = excluded.request_id, status = excluded.status, record = excluded.record").ConfigureAwait(false);
            await InsertRows(executor, tables.Documents, ["id", "kind", "family", "key_value", "scope_kind", "owner_id", "created_at", "retired_at", "record"],
                documents.Values.Select(record =>
                {
                    var parts = AddressParts.Of(record.Kind, record.Scope, record.Key);
                    return new object?[] { record.Id.Value, parts.Kind, parts.Family, parts.KeyValue, parts.ScopeKind, parts.OwnerId,
                        record.CreatedAt.Value, record.RetiredAt?.Value, record.ToJson().ToJson() };
                }), "ON CONFLICT(id) DO UPDATE SET retired_at = excluded.retired_at, record = excluded.record").ConfigureAwait(false);
            foreach (var json in JsonArrays(cleared.Cast<object?>()))
                await executor.RunAsync($"DELETE FROM {tables.DocumentRevisions} WHERE document_id IN (SELECT value FROM json_each(?))", json).ConfigureAwait(false);
            await InsertRows(executor, tables.DocumentRevisions, ["document_id", "seq", "kind", "version", "content"], revisions.Values.SelectMany(rows => rows)).ConfigureAwait(false);
            owners.Clear(); conversations.Clear(); entries.Clear(); tasks.Clear(); submissions.Clear(); documents.Clear(); cleared.Clear(); revisions.Clear();
        }
    }

    // Small rows share one SQL shape per table; large rows avoid escaping their JSON text a second time.
    private static async Task InsertRows(ISqliteExecutor executor, string table, string[] columns,
        IEnumerable<object?[]> source, string conflict = "")
    {
        var small = new List<object?>();
        var large = new List<object?[]>();
        foreach (var row in source)
            if (row.OfType<string>().Sum(text => (long)text.Length) > 8 * 1024) large.Add(row); else small.Add(row);
        var names = string.Join(", ", columns);
        var cells = string.Join(", ", columns.Select((_, index) => $"json_extract(item.value, '$[{index}]')"));
        foreach (var json in JsonArrays(small))
            await executor.RunAsync($"INSERT INTO {table} ({names}) SELECT {cells} FROM json_each(?) AS item WHERE true {conflict}", json).ConfigureAwait(false);
        var size = 1;
        while (size * 2 * columns.Length <= 100) size *= 2;
        var rowSql = "(" + string.Join(", ", columns.Select(_ => "?")) + ")";
        for (var offset = 0; offset < large.Count; offset += size)
        {
            while (size > large.Count - offset) size /= 2;
            await executor.RunAsync($"INSERT INTO {table} ({names}) VALUES {string.Join(", ", Enumerable.Repeat(rowSql, size))} {conflict}",
                large.Skip(offset).Take(size).SelectMany(row => row).ToArray()).ConfigureAwait(false);
        }
    }

    private static async Task<List<SqliteRow>> ReadRows(ISqliteExecutor executor, string sql, IEnumerable<object?> values)
    {
        var rows = new List<SqliteRow>();
        foreach (var json in JsonArrays(values)) rows.AddRange(await executor.AllAsync(sql, json).ConfigureAwait(false));
        return rows;
    }

    // Use the existing JSON scalar encoder, including its lone-surrogate handling, without reflection-based serialization.
    private static string EncodeCell(object? value) => value switch
    {
        null => "null",
        string text => JsonValue.String(text).ToJson(),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        object?[] row => "[" + string.Join(",", row.Select(EncodeCell)) + "]",
        _ => throw new InvalidOperationException("Unsupported SQLite bulk cell"),
    };

    private static IEnumerable<string> JsonArrays(IEnumerable<object?> values)
    {
        var chunk = new StringBuilder("[");
        var count = 0;
        foreach (var value in values)
        {
            var part = EncodeCell(value);
            if (count > 0 && chunk.Length + part.Length + 2 > 512 * 1024)
            {
                yield return chunk.Append(']').ToString();
                chunk.Clear().Append('[');
                count = 0;
            }
            if (count++ > 0) chunk.Append(',');
            chunk.Append(part);
        }
        if (count > 0) yield return chunk.Append(']').ToString();
    }
}
