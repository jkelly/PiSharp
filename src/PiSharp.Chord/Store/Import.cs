// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/import.ts
//
// Tables.ImportStoreAsync(backend, parts, ctx, { Outbox }) (slice 6a §13, rules 12, 24, 25): a store made from an archive, a trust
// boundary. Every part is checked against the archive schema (Archive.cs), its place in the archive's order and its content (names, rows
// against their table's recorded columns, ids and seqs, lifetimes at one address never overlapping, revisions in seq order from a base,
// versions, duplicates, the end count, the manifest); everything is applied in one backend transaction with the applier (Applier.cs), so a
// refused import leaves the backend as it was. An input that stalls is cancelled by ctx. Upstream's parts are any values, sync or async;
// here JSON values (JsonNode?, null for JSON null) from an IAsyncEnumerable or an IEnumerable. Upstream closes a stalled input with
// return() without awaiting it; a C# async iterator refuses DisposeAsync while its MoveNextAsync runs, so such an input is disposed once
// that call settles (a hand-written enumerator at once). The cancellation's error is ChordError aborted with the token's
// OperationCanceledException as its cause (a token carries no reason).
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Sqlite;

namespace PiSharp.Chord.Store;

public static partial class Tables
{
    /// <summary>A store holding the archive <paramref name="parts"/> on <paramref name="backend"/>, which must not hold a store yet: at the
    /// manifest's <c>lastSeq</c>, its outbox (when <c>options.Outbox</c> is true) starting after it. A refused archive, a stalled input
    /// cancelled by <paramref name="context"/> or a backend error fails; the backend is closed then (rule 12; upstream
    /// <c>importStore</c>).</summary>
    public static Task<IStore> ImportStoreAsync(Backend backend, IAsyncEnumerable<JsonNode?> parts, ChordContext context, StoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return StoreInternal.ImportAsync(backend, () => parts.GetAsyncEnumerator(), context, options);
    }

    /// <summary><see cref="ImportStoreAsync(Backend, IAsyncEnumerable{JsonNode}, ChordContext, StoreOptions)"/> of parts in memory.</summary>
    public static Task<IStore> ImportStoreAsync(Backend backend, IEnumerable<JsonNode?> parts, ChordContext context, StoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return StoreInternal.ImportAsync(backend, () => new StoreInternal.SyncParts(parts.GetEnumerator()), context, options);
    }
}

internal static partial class StoreInternal
{
    /// <summary>The import (upstream <c>importStore</c>'s body): the backend checked to hold no store, the store's schema and the archive
    /// applied in one backend transaction, the store opened on what it stored.</summary>
    internal static async Task<IStore> ImportAsync(Backend backend, Func<IAsyncEnumerator<JsonNode?>> parts, ChordContext context, StoreOptions? options)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(context);
        var db = backend.Db;
        try
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
            var existing = await db.GetAsync("SELECT name FROM sqlite_master WHERE name = ?", StoreTables.Meta).ConfigureAwait(false);
            if (existing is not null) throw DataInternal.Invalid("args", "import: the backend holds a store already");
            var meta = await db.TransactionAsync(async executor =>
            {
                var meta = await ApplyStoreSchemaAsync(new InTransaction(executor)).ConfigureAwait(false);
                var archive = new ArchiveImport(executor, context);
                await EachAsync(parts(), context, archive.TakeAsync).ConfigureAwait(false);
                return await archive.FinishAsync(meta, options?.Outbox == true).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return new ChordStore(db, meta, await ReadCatalogAsync(db).ConfigureAwait(false));
        }
        catch (Exception)
        {
            try
            {
                await db.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The import's own failure is the one to report.
            }
            throw;
        }
    }

    /// <summary>Runs <paramref name="take"/> on each part in turn; a wait for the next part ends when <paramref name="context"/> aborts (the
    /// input is closed; upstream <c>each</c>).</summary>
    private static async Task EachAsync(IAsyncEnumerator<JsonNode?> iterator, ChordContext context, Func<JsonNode?, Task> take)
    {
        var done = false;
        Task<bool>? pending = null;
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    pending = iterator.MoveNextAsync().AsTask();
                    more = await (context.AbortSignal is { } signal ? AwaitWithContext(pending, signal) : pending).ConfigureAwait(false);
                }
                catch (Exception) when (context.AbortSignal is { IsCancellationRequested: true } signal)
                {
                    throw new ChordError("aborted", "import: aborted", cause: new OperationCanceledException(signal));
                }
                if (!more)
                {
                    done = true;
                    break;
                }
                await take(iterator.Current).ConfigureAwait(false);
            }
        }
        finally
        {
            // A stalled input is not awaited: the backend transaction ends now.
            if (!done) _ = CloseInput(iterator, pending);
        }
        await iterator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Closes an input the import stopped reading, its failure dropped: at once, or once its running MoveNextAsync settled when it
    /// refuses a DisposeAsync meanwhile (a C# async iterator does).</summary>
    private static async Task CloseInput(IAsyncEnumerator<JsonNode?> iterator, Task<bool>? pending)
    {
        try
        {
            await iterator.DisposeAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception) when (pending is { IsCompleted: false })
        {
            // Retried below, once the call settled.
        }
        catch (Exception)
        {
            return;
        }
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The input's own failure: nobody reads it.
        }
        await DisposeQuietly(iterator).ConfigureAwait(false);
    }

    /// <summary>A synchronous input as an asynchronous one (upstream's <c>parts[Symbol.iterator]()</c>, whose <c>next()</c> settles at
    /// once).</summary>
    internal sealed class SyncParts(IEnumerator<JsonNode?> parts) : IAsyncEnumerator<JsonNode?>
    {
        public JsonNode? Current => parts.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            try
            {
                return ValueTask.FromResult(parts.MoveNext());
            }
            catch (Exception error)
            {
                return ValueTask.FromException<bool>(error);
            }
        }

        public ValueTask DisposeAsync()
        {
            parts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The store's schema migrations run inside the import's backend transaction (they open none of their own; upstream
    /// <c>inTransaction</c>).</summary>
    private sealed class InTransaction(ISqliteExecutor executor) : ISqliteDatabase
    {
        public Task ExecAsync(string sql) => executor.ExecAsync(sql);

        public Task RunAsync(string sql, params object?[] parameters) => executor.RunAsync(sql, parameters);

        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => executor.RunCountedAsync(sql, parameters);

        public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => executor.GetAsync(sql, parameters);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) => executor.AllAsync(sql, parameters);

        public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback) => callback(executor);

        public Task CloseAsync() => Task.FromException(new InvalidOperationException("import: the backend closes after the import"));
    }

    /// <summary>A place in the archive's order: before the manifest, in a table group's puts or indexes, the documents, the end.</summary>
    private enum Place { Manifest, Puts, Indexes, Documents, Ended }

    /// <summary>The record whose revisions may follow, with the last revision's seq and its base's version.</summary>
    private sealed class ImportedDocument(DocumentRecord record)
    {
        internal DocumentRecord Record { get; } = record;

        internal long Seq { get; set; }

        internal long? Base { get; set; }
    }

    /// <summary>One archive being imported: the place reached in its order (the manifest, then table groups of a <c>table</c> write, its
    /// <c>put</c>s and its <c>index</c> writes, then the documents, then <c>end</c>), what was seen (tables, ids, live addresses, the write
    /// count) and the step its writes are applied in (upstream <c>ArchiveImport</c>).</summary>
    private sealed class ArchiveImport(ISqliteExecutor executor, ChordContext context)
    {
        private long position;
        private ArchiveManifest? manifest;
        private Step? step;
        /// <summary>Where the next part may go.</summary>
        private Place place = Place.Manifest;
        /// <summary>The table whose group is open.</summary>
        private string? table;
        private readonly HashSet<string> tables = new(StringComparer.Ordinal);
        private readonly HashSet<long> ids = [];
        /// <summary>The lifetimes seen at each address, <c>[createdAt, retiredAt)</c> (<see cref="long.MaxValue"/>, upstream's Infinity,
        /// while live).</summary>
        private readonly Dictionary<(string Owner, string Namespace, string Kind, string Key), List<(long From, long To)>> lifetimes = [];
        private ImportedDocument? document;
        private long writes;

        /// <summary>Checks the next part and applies it.</summary>
        internal async Task TakeAsync(JsonNode? value)
        {
            var at = position++;
            var part = CheckPart(value, at, context);
            var kind = (string)part["kind"]!;
            var what = $"import: part {at} ({kind})";
            if (place == Place.Ended) throw DataInternal.Invalid("args", $"{what}: after the end");
            if (kind == "manifest" || step is null)
            {
                Start(part, kind, what);
                return;
            }
            if (kind == "end")
            {
                var counted = Integer(part["writes"]);
                if (counted != writes) throw DataInternal.Invalid("args", $"{what}: counts {counted} writes, the archive has {writes}");
                EndDocument(what);
                place = Place.Ended;
                return;
            }
            place = PlaceOf(part, kind, what);
            writes++;
            await ApplyWriteAsync(step, part, what).ConfigureAwait(false);
        }

        /// <summary>The stored metadata once the archive ended: its <c>lastSeq</c>, the allocator's next id and the outbox setting.</summary>
        internal async Task<MetaRow> FinishAsync(MetaRow meta, bool outbox)
        {
            if (place != Place.Ended || step is null || manifest is null) throw DataInternal.Invalid("args", "import: the archive has no end part");
            var (lastSeq, nextId) = (manifest.LastSeq, manifest.NextId);
            await WriteCatalogAsync(step).ConfigureAwait(false);
            await executor.RunAsync($"UPDATE {StoreTables.Meta} SET last_seq = ?, next_id = ?, outbox = ?, outbox_floor = ? WHERE singleton = 1",
                lastSeq, nextId, outbox ? 1L : 0L, lastSeq).ConfigureAwait(false);
            return meta with { LastSeq = lastSeq, NextId = nextId, Outbox = outbox ? 1 : 0, OutboxFloor = lastSeq };
        }

        /// <summary>The manifest, first: a format and schema this code reads.</summary>
        private void Start(JsonObject part, string kind, string what)
        {
            if (kind != "manifest" || step is not null) throw DataInternal.Invalid("args", $"{what}: the manifest comes first, once");
            var format = Integer(part["format"]);
            if (format != ArchiveFormat) throw DataInternal.Invalid("args", $"{what}: format {format} is not {ArchiveFormat}");
            var schema = Integer(part["schema"]);
            if (schema > StoreSchema) throw DataInternal.Invalid("args", $"{what}: schema {schema} is newer than {StoreSchema}");
            manifest = new ArchiveManifest(format, schema, Integer(part["lastSeq"]), Integer(part["nextId"]));
            step = new Step(executor, manifest.LastSeq, new IdAllocator(manifest.NextId), new Dictionary<string, TableRecord>(StringComparer.Ordinal));
        }

        /// <summary>Where <paramref name="part"/> puts the archive, refused when out of order, a duplicate, or outside the manifest's
        /// ranges.</summary>
        private Place PlaceOf(JsonObject part, string kind, string what)
        {
            switch (kind)
            {
                case "table":
                {
                    var name = (string)part["table"]!;
                    if (place == Place.Documents) throw DataInternal.Invalid("args", $"{what}: tables come before the documents");
                    if (!tables.Add(name)) throw DataInternal.Invalid("args", $"{what}: table {name} occurs twice");
                    table = name;
                    return Place.Puts;
                }
                case "put":
                    if (place != Place.Puts || (string)part["table"]! != table)
                        throw DataInternal.Invalid("args", $"{what}: a put follows its table's table write, before its indexes");
                    return Place.Puts;
                case "index":
                    if ((place != Place.Puts && place != Place.Indexes) || (string)part["table"]! != table)
                        throw DataInternal.Invalid("args", $"{what}: an index follows its table's table write and puts");
                    return Place.Indexes;
                case "document.create":
                    CheckDocument(part, what);
                    return Place.Documents;
                case "document.revision":
                    CheckRevision(part, what);
                    return Place.Documents;
                default:
                    // The part schema admits no other kind.
                    throw new InvalidOperationException($"{what}: an unknown part");
            }
        }

        /// <summary>A record within the manifest's ids and seqs (an id equal to a saturated <c>nextId</c> too), unique, its lifetime
        /// overlapping no other at its address; its address and meta well-formed text without NUL (SQLite would cut a NUL off, slice 6a
        /// §8). The record before it must have ended well (<see cref="EndDocument"/>).</summary>
        private void CheckDocument(JsonObject part, string what)
        {
            EndDocument(what);
            var record = RecordOfJson(part["record"]!.AsObject());
            var (lastSeq, nextId) = (manifest!.LastSeq, manifest.NextId);
            var saturated = nextId == MaxSafeIntegerLong && record.Id == nextId;
            if (record.Id >= nextId && !saturated) throw DataInternal.Invalid("args", $"{what}: id {record.Id} is not below {nextId}");
            if (ids.Contains(record.Id)) throw DataInternal.Invalid("args", $"{what}: id {record.Id} occurs twice");
            var (createdAt, retiredAt) = (record.CreatedAt, record.RetiredAt);
            if (createdAt > lastSeq || (retiredAt is { } retired && (retired < createdAt || retired > lastSeq)))
                throw DataInternal.Invalid("args", $"{what}: a lifetime outside the manifest's lastSeq");
            // A singleton's record has no key; its column keeps "".
            var key = record.Key ?? "";
            foreach (var text in new[] { record.Namespace, record.Owner, record.Kind, key })
            {
                if (StoredText(text, what) != text) throw DataInternal.Invalid("args", $"{what}: an address is not well-formed text");
            }
            if (record.Meta is { } meta && StoredText(meta, what) != meta) throw DataInternal.Invalid("args", $"{what}: the meta is not well-formed text");
            var address = (record.Owner, record.Namespace, record.Kind, key);
            var lifetime = (From: createdAt, To: retiredAt ?? long.MaxValue);
            if (!lifetimes.TryGetValue(address, out var seen)) lifetimes[address] = seen = [];
            // Two live records are two infinite lifetimes, which overlap; an empty one (createdAt = retiredAt) overlaps none.
            if (lifetime.From < lifetime.To && seen.Any(other => other.From < lifetime.To && lifetime.From < other.To))
                throw DataInternal.Invalid("args", $"{what}: a second record at one address for an overlapping lifetime");
            seen.Add(lifetime);
            ids.Add(record.Id);
            document = new ImportedDocument(record);
        }

        /// <summary>A revision of the record before it, in seq order within its lifetime, from a base, each delta at its base's
        /// version.</summary>
        private void CheckRevision(JsonObject part, string what)
        {
            var current = document;
            if (current is null || current.Record.Id != Integer(part["id"])) throw DataInternal.Invalid("args", $"{what}: a revision follows its record's create");
            var record = current.Record;
            if (record.RetiredAt is not null && record.History != DocumentHistory.Rewindable)
                throw DataInternal.Invalid("args", $"{what}: a retired {Name(record.History)} record keeps no revisions");
            var seq = Integer(part["seq"]);
            if (seq <= current.Seq || seq < record.CreatedAt || seq > (record.RetiredAt ?? manifest!.LastSeq))
                throw DataInternal.Invalid("args", $"{what}: a revision out of seq order or outside its lifetime");
            var content = part["content"]!.AsObject();
            var version = Integer(content["version"]);
            if ((string)content["kind"]! == "base") current.Base = version;
            else if (current.Base != version) throw DataInternal.Invalid("args", $"{what}: a delta without a base at its version");
            current.Seq = seq;
        }

        /// <summary>The record before ends well: its last base is at the record's version; only a retired <c>latest</c> record has none (its
        /// content was dropped at retirement; a retired <c>rewindable</c> one keeps its history).</summary>
        private void EndDocument(string what)
        {
            var ended = document;
            document = null;
            if (ended is null) return;
            var record = ended.Record;
            var contentless = record.RetiredAt is not null && record.History == DocumentHistory.Latest;
            if (ended.Base is not { } @base ? !contentless : @base != record.Version)
                throw DataInternal.Invalid("args", $"{what}: record {record.Id} has no base at its version");
        }
    }
}
