// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/store/host-data.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;

namespace PiSharp.Chord.Store;

/// <summary>What an import does when the target address already owns data.</summary>
public enum HostExisting { Refuse, Replace }

/// <summary>Options for importing a host archive, optionally under another address of the same kind.</summary>
public sealed record HostImportOptions
{
    public Address? Address { get; init; }
    public HostExisting Existing { get; init; } = HostExisting.Refuse;
}

/// <summary>The document and table-row counts changed by a host data operation.</summary>
public sealed record HostDataCounts(long Documents, long Rows);

/// <summary>A checked, materialized archive of one host's data. Obtain it through <see cref="Tables.ReadHostArchiveAsync(IEnumerable{JsonNode}, ChordContext)"/>.</summary>
public sealed class HostArchive
{
    internal HostArchive(string host, IReadOnlyList<HostTable> tables, IReadOnlyList<HostDocument> documents)
    {
        Host = host;
        Tables = tables;
        Documents = documents;
    }

    /// <summary>The canonical address key whose data was exported.</summary>
    public string Host { get; }
    internal IReadOnlyList<HostTable> Tables { get; }
    internal IReadOnlyList<HostDocument> Documents { get; }
}

internal sealed record HostTable(TableWrite.Definition Write, string Column, IReadOnlyList<TableWrite.Put> Puts,
    IReadOnlyList<TableWrite.Index> Indexes);
internal sealed record HostDocument(DocumentRecord Record, JsonNode? Value);
internal sealed record HostCommit(CommitFence Fence, IReadOnlyList<IStagedWrite> After);
internal sealed record HostImportPlan(string Target, HostExisting Existing, IReadOnlyDictionary<string, TableDefinition> Histories);

public static partial class Tables
{
    /// <summary>Exports only the records and rows belonging to <paramref name="address"/>, while holding the store's commit queue.</summary>
    public static StoreExport ExportHostData(IStore store, Address address, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(address);
        var chord = (ChordStore)store;
        chord.AssertUsable();
        return new StoreExport(chord, context, StoreInternal.StoredText(address.Key, "exportHostData: address"));
    }

    /// <summary>Reads and validates a complete host archive in memory before any import can change a store.</summary>
    public static Task<HostArchive> ReadHostArchiveAsync(IAsyncEnumerable<JsonNode?> parts, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return StoreInternal.ReadHostArchiveAsync(parts.GetAsyncEnumerator(), context);
    }

    /// <summary>Reads and validates host archive parts already in memory.</summary>
    public static Task<HostArchive> ReadHostArchiveAsync(IEnumerable<JsonNode?> parts, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return StoreInternal.ReadHostArchiveAsync(new StoreInternal.SyncParts(parts.GetEnumerator()), context);
    }
}

internal static partial class StoreInternal
{
    internal static async Task<HostArchive> ReadHostArchiveAsync(IAsyncEnumerator<JsonNode?> parts, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reader = new HostArchiveReader(context);
        await EachAsync(parts, context, part =>
        {
            reader.Take(part);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return reader.Finish();
    }

    private enum HostArchivePlace { Manifest, Tables, Documents, Ended }

    private sealed class HostArchiveReader(ChordContext context)
    {
        private long position;
        private HostArchivePlace place;
        private string host = "";
        private long writes;
        private readonly List<HostTable> tables = [];
        private readonly List<HostDocument> documents = [];
        private readonly HashSet<(string Owner, string Namespace, string Kind)> addresses = [];
        private HostReadingDocument? document;

        internal void Take(JsonNode? value)
        {
            var at = position++;
            var part = CheckPart(value, at, context);
            var kind = (string)part["kind"]!;
            var what = $"importHostData: part {at} ({kind})";
            if (place == HostArchivePlace.Ended) throw DataInternal.Invalid("args", $"{what}: after the end");
            if (place == HostArchivePlace.Manifest || kind == "manifest")
            {
                Manifest(part, kind, what);
                return;
            }
            if (kind == "end")
            {
                if (Integer(part["writes"]) != writes)
                    throw DataInternal.Invalid("args", $"{what}: counts {Integer(part["writes"])} writes, the archive has {writes}");
                EndDocument(what);
                place = HostArchivePlace.Ended;
                return;
            }
            writes++;
            switch (kind)
            {
                case "table": Table(DefinitionOf(part), what); break;
                case "put": Put(part, what); break;
                case "index": Index(part, what); break;
                case "document.create": Create(RecordOfJson(part["record"]!.AsObject()), what); break;
                case "document.revision": Revision(part, what); break;
            }
        }

        internal HostArchive Finish()
        {
            if (place != HostArchivePlace.Ended) throw DataInternal.Invalid("args", "importHostData: the archive has no end part");
            return new HostArchive(host, tables.AsReadOnly(), documents.AsReadOnly());
        }

        private void Manifest(JsonObject part, string kind, string what)
        {
            if (kind != "manifest" || place != HostArchivePlace.Manifest)
                throw DataInternal.Invalid("args", $"{what}: the manifest comes first, once");
            if (Integer(part["format"]) != ArchiveFormat)
                throw DataInternal.Invalid("args", $"{what}: format {Integer(part["format"])} is not {ArchiveFormat}");
            if (Integer(part["schema"]) > StoreSchema)
                throw DataInternal.Invalid("args", $"{what}: schema {Integer(part["schema"])} is newer than {StoreSchema}");
            var key = ChordJson.AsString(part["host"]);
            if (key is null || key.Length == 0 || StoredText(key, what) != key)
                throw DataInternal.Invalid("args", $"{what}: not a host's data archive (no host)");
            try { ContractsInternal.CheckName(ContractsInternal.KindOfKey(key), $"{what}: host kind"); }
            catch (ArgumentException error) { throw DataInternal.Invalid("args", error.Message); }
            host = key;
            place = HostArchivePlace.Tables;
        }

        private void Table(TableWrite.Definition write, string what)
        {
            if (place != HostArchivePlace.Tables) throw DataInternal.Invalid("args", $"{what}: tables come before the documents");
            if (tables.Any(table => table.Write.Table == write.Table)) throw DataInternal.Invalid("args", $"{what}: table {write.Table} occurs twice");
            var column = HostColumn(write.Columns, ContractsInternal.KindOfKey(host));
            if (write.Columns.Values.Count(each => each.Host is not null) != 1 || column is null)
                throw DataInternal.Invalid("args", $"{what}: table {write.Table} has no single host column of {ContractsInternal.KindOfKey(host)}");
            CheckTable(write, what);
            tables.Add(new HostTable(write, column, new List<TableWrite.Put>(), new List<TableWrite.Index>()));
        }

        private void Put(JsonObject part, string what)
        {
            var table = tables.LastOrDefault();
            if (place != HostArchivePlace.Tables || table is null || table.Write.Table != (string)part["table"]! || table.Indexes.Count != 0)
                throw DataInternal.Invalid("args", $"{what}: a put follows its table's table write, before its indexes");
            var row = new OrderedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (name, value) in part["row"]!.AsObject()) row[name] = value?.DeepClone();
            if (ChordJson.AsString(row.GetValueOrDefault(table.Column)) != host)
                throw DataInternal.Invalid("args", $"{what}: a row that does not belong to {host}");
            var record = HostArchiveTableRecord(table);
            var (physical, _) = RecordedPut(record.Table, record.Columns);
            _ = PutParams(record, part, physical, what);
            var columns = new OrderedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var (name, value) in part["columns"]!.AsObject()) columns[name] = Integer(value);
            ((List<TableWrite.Put>)table.Puts).Add(new TableWrite.Put(table.Write.Table, row, Integer(part["version"]), columns));
        }

        private void Index(JsonObject part, string what)
        {
            var table = tables.LastOrDefault();
            if (place != HostArchivePlace.Tables || table is null || table.Write.Table != (string)part["table"]!)
                throw DataInternal.Invalid("args", $"{what}: an index follows its table's table write and puts");
            var index = new TableWrite.Index(table.Write.Table, (string)part["name"]!, Names(part["columns"]));
            CheckIndex(HostArchiveTableRecord(table), index, what);
            ((List<TableWrite.Index>)table.Indexes).Add(index);
        }

        private void Create(DocumentRecord record, string what)
        {
            EndDocument(what);
            place = HostArchivePlace.Documents;
            if (record.Host != host || record.Key != host) throw DataInternal.Invalid("args", $"{what}: a record that does not belong to {host}");
            if (record.History != DocumentHistory.None || record.RetiredAt is not null || record.Meta is not null)
                throw DataInternal.Invalid("args", $"{what}: not a live Chord record");
            foreach (var text in new[] { record.Owner, record.Namespace, record.Kind })
                if (StoredText(text, what) != text) throw DataInternal.Invalid("args", $"{what}: an address is not well-formed text");
            if (!addresses.Add((record.Owner, record.Namespace, record.Kind))) throw DataInternal.Invalid("args", $"{what}: a second record at one address");
            document = new HostReadingDocument(record);
        }

        private void Revision(JsonObject part, string what)
        {
            var current = document;
            var seq = Integer(part["seq"]);
            if (current is null || current.Record.Id != Integer(part["id"]) || seq <= current.Seq)
                throw DataInternal.Invalid("args", $"{what}: a revision follows its record's create, in seq order");
            var content = part["content"]!.AsObject();
            var version = Integer(content["version"]);
            if ((string)content["kind"]! == "base")
            {
                current.Base = version;
                current.Value = content["value"]?.DeepClone();
            }
            else if (current.Base != version) throw DataInternal.Invalid("args", $"{what}: a delta without a base at its version");
            else
            {
                try { current.Value = DeltaApply.Apply(current.Value, content["ops"]!.AsArray()); }
                catch (Exception error) { throw DataInternal.Invalid("args", $"{what}: a delta that does not apply ({error.Message})"); }
            }
            current.Seq = seq;
        }

        private void EndDocument(string what)
        {
            var ended = document;
            document = null;
            if (ended is null) return;
            if (ended.Base != ended.Record.Version) throw DataInternal.Invalid("args", $"{what}: record {ended.Record.Id} has no base at its version");
            documents.Add(new HostDocument(ended.Record, ended.Value));
        }
    }

    private static TableRecord HostArchiveTableRecord(HostTable table) => new(table.Write.Table, table.Write.Version,
        table.Write.State, table.Write.Columns, table.Write.Key, table.Write.Fingerprint,
        table.Indexes.ToDictionary(index => index.Name, index => index.Columns, StringComparer.Ordinal));

    private sealed class HostReadingDocument(DocumentRecord record)
    {
        internal DocumentRecord Record { get; } = record;
        internal long Seq { get; set; }
        internal long? Base { get; set; }
        internal JsonNode? Value { get; set; }
    }

    internal static IReadOnlyList<string> TablesToMigrate(IStore store, HostArchive archive)
    {
        var chord = (ChordStore)store;
        return [.. archive.Tables.Where(table => chord.Table(table.Write.Table) is { } recorded &&
            (recorded.Version > table.Write.Version || table.Write.State == TableState.Migrating)).Select(table => table.Write.Table)];
    }

    internal static async Task<HashSet<string>> HostDataOwnersAsync(IStore store, string key, HostArchive? archive, ChordContext context)
    {
        var chord = (ChordStore)store;
        var kind = ContractsInternal.KindOfKey(key);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        static string OwnerOf(string table) => table[..table.IndexOf(':')];
        foreach (var record in chord.Catalog.Values)
            if (HostColumn(record, kind) is not null) owners.Add(OwnerOf(record.Table));
        if (archive is not null)
        {
            foreach (var table in archive.Tables) owners.Add(OwnerOf(table.Write.Table));
            foreach (var document in archive.Documents) owners.Add(document.Record.Owner);
        }
        var rows = await chord.ReadAsync(executor => executor.AllAsync($"SELECT DISTINCT owner FROM {StoreTables.Documents} WHERE host = ?", key), context).ConfigureAwait(false);
        foreach (var row in rows) owners.Add((string)row["owner"]!);
        return owners;
    }

    internal static async Task<HostDataCounts> ImportHostDataAsync(IStore store, HostArchive archive, HostImportPlan plan, HostCommit commit, ChordContext context)
    {
        if (ContractsInternal.KindOfKey(plan.Target) != ContractsInternal.KindOfKey(archive.Host))
            throw DataInternal.Invalid("args", $"importHostData: data of {archive.Host} cannot belong to {plan.Target}, another kind");
        HostDataCounts counts = new(0, 0);
        await CommitHostOperationAsync(store, async (step, writes) =>
        {
            if (plan.Existing == HostExisting.Replace) await RemoveHostDataAsync(step, plan.Target, writes).ConfigureAwait(false);
            else if (await HasHostDataAsync(step, plan.Target).ConfigureAwait(false))
                throw DataInternal.Invalid("args", $"importHostData: {plan.Target} has data in this store (import with existing: replace)");
            long rows = 0;
            foreach (var table in archive.Tables)
                rows += await ImportHostTableAsync(step, table, archive.Host, plan, writes, context).ConfigureAwait(false);
            foreach (var document in archive.Documents)
            {
                var record = document.Record;
                var address = new DocumentAddress(record.Owner, record.Namespace, record.Kind, plan.Target, plan.Target);
                var @base = new DocumentBase(record.Version, record.Fingerprint, document.Value);
                writes.AddRange(await CreateDocument(address, @base, ChordJson.Stringify(document.Value)).ApplyAsync(step).ConfigureAwait(false));
            }
            counts = new HostDataCounts(archive.Documents.Count, rows);
        }, commit, context).ConfigureAwait(false);
        return counts;
    }

    internal static async Task<HostDataCounts> DeleteHostDataAsync(IStore store, string key, HostCommit commit, ChordContext context)
    {
        HostDataCounts counts = new(0, 0);
        await CommitHostOperationAsync(store, async (step, writes) => counts = await RemoveHostDataAsync(step, key, writes).ConfigureAwait(false), commit, context).ConfigureAwait(false);
        return counts;
    }

    private static Task<long> CommitHostOperationAsync(IStore store, Func<Step, List<StoreWrite>, Task> apply, HostCommit commit, ChordContext context)
    {
        var write = new StagedWrite(WriteOrder.Write, async step =>
        {
            var writes = new List<StoreWrite>();
            await apply(step, writes).ConfigureAwait(false);
            return writes;
        });
        return ((ChordStore)store).CommitAsync([write, .. commit.After], commit.Fence, context);
    }

    private static async Task<long> ImportHostTableAsync(Step step, HostTable table, string source, HostImportPlan plan, List<StoreWrite> writes, ChordContext context)
    {
        var write = table.Write;
        var what = $"importHostData: table {write.Table}";
        var puts = table.Puts.Select(put =>
        {
            var row = new OrderedDictionary<string, JsonNode?>(put.Row, StringComparer.Ordinal) { [table.Column] = JsonValue.Create(plan.Target) };
            return put with { Row = row };
        }).ToArray();
        IReadOnlyList<TableWrite.Put> placed = puts;
        var recorded = step.Table(write.Table);
        if (recorded is null)
        {
            foreach (var each in new TableWrite[] { write }.Concat(table.Indexes))
            {
                await ApplyWriteAsync(step, each.ToJson(), what).ConfigureAwait(false);
                writes.Add(each);
            }
        }
        else
        {
            if (recorded.State != TableState.Ready) throw DataInternal.Invalid("args", $"{what}: migrating here; open its owner first");
            if (write.Version > recorded.Version) throw DataInternal.Invalid("args", $"{what}: at version {write.Version} in the archive, newer than {recorded.Version} here");
            if (write.Version < recorded.Version || write.State == TableState.Migrating)
            {
                if (!plan.Histories.TryGetValue(write.Table, out var history))
                    throw DataInternal.Invalid("args", $"{what}: at version {write.Version}, {recorded.Version} here; its owner is not live");
                placed = MigrateRows(write, puts, history, recorded, context);
            }
        }
        var record = step.Table(write.Table)!;
        var column = HostColumn(record, ContractsInternal.KindOfKey(source));
        if (column is null) throw DataInternal.Invalid("args", $"{what}: no host column of {ContractsInternal.KindOfKey(source)} here");
        var exists = $"SELECT 1 AS found FROM {Quote(write.Table)} WHERE {string.Join(" AND ", record.Key.Select(name => $"{Quote(name)} = ?"))}";
        foreach (var put in placed)
        {
            if (ChordJson.AsString(put.Row.GetValueOrDefault(column)) != plan.Target)
                throw DataInternal.Invalid("args", $"{what}: a row that does not belong to {plan.Target}");
            var key = record.Key.Select(name => ToSqlite(CheckScalar(record.Columns[name].Type, put.Row.GetValueOrDefault(name), $"{what}: key"))).ToArray();
            if (await step.GetAsync(exists, key).ConfigureAwait(false) is not null)
                throw DataInternal.Invalid("args", $"{what}: a row with this key exists (of another address)");
            await ApplyWriteAsync(step, put.ToJson(), what).ConfigureAwait(false);
            step.Rows++;
            writes.Add(put);
        }
        return placed.Count;
    }

    private static async Task<HostDataCounts> RemoveHostDataAsync(Step step, string key, List<StoreWrite> writes)
    {
        var records = await DocumentEngine.ListHostRecordsAsync(step, key, 0, MaxSafeIntegerLong).ConfigureAwait(false);
        foreach (var record in records) writes.AddRange(await RetireDocument(record.Id, record.Revision).ApplyAsync(step).ConfigureAwait(false));
        long rows = 0;
        foreach (var (table, column) in HostTables(step, ContractsInternal.KindOfKey(key)))
        {
            var count = await step.RunCountedAsync($"DELETE FROM {Quote(table)} WHERE {Quote(column)} = ?", key).ConfigureAwait(false);
            step.Rows += count;
            rows += count;
            writes.Add(new TableWrite.DeleteWhere(table, new WhereNode.Compare(Comparison.Eq, column, key), count));
        }
        return new HostDataCounts(records.Count, rows);
    }

    private static async Task<bool> HasHostDataAsync(Step step, string key)
    {
        if ((await DocumentEngine.ListHostRecordsAsync(step, key, 0, 1).ConfigureAwait(false)).Count != 0) return true;
        foreach (var (table, column) in HostTables(step, ContractsInternal.KindOfKey(key)))
            if (await step.GetAsync($"SELECT 1 AS found FROM {Quote(table)} WHERE {Quote(column)} = ? LIMIT 1", key).ConfigureAwait(false) is not null) return true;
        return false;
    }

    private static IEnumerable<(string Table, string Column)> HostTables(Step step, string kind) =>
        step.Records().Select(record => (record.Table, Column: HostColumn(record, kind))).Where(each => each.Column is not null).Select(each => (each.Table, each.Column!));
}
