// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/export.ts
//
// store.Export(ctx) (slice 6a §13, rule 26): the store's state after its lastSeq as archive parts, read page by page with plain reads while
// the export holds the commit queue (no commit can interleave, no long read transaction). The queue is taken at the first MoveNextAsync and
// given back when the iteration ends, throws, is disposed (upstream's return()) or ctx aborts; an abort while the consumer is suspended
// gives it back at once and drops the pages and records the export held, and an abort while it waits for the queue ends the wait. Closing
// the store ends it too (released). A part is its JSON (Archive.cs). Upstream's AsyncIterableIterator is an IAsyncEnumerator that is its
// own IAsyncEnumerable (one pass, as [Symbol.asyncIterator]() returns this); the context is its only cancellation (GetAsyncEnumerator's
// token is not used). The parts are a C# async iterator, which refuses DisposeAsync while one of its MoveNextAsync calls runs: where
// upstream's #end calls return() at once (a running generator queues it), the export disposes them once that call settled. An abort is a
// CancellationToken, which carries no reason: an aborted export's error has the token's OperationCanceledException as its cause, and
// awaitWithContext (context/index.ts) is Task.WaitAsync on the token.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Sqlite;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>An export of a store under a context: an async iterator of its parts, each the part's JSON (upstream <c>StoreExport</c>). One
/// pass; after an abort <see cref="MoveNextAsync"/> fails <c>aborted</c>, after the store closed <c>released</c>.</summary>
public sealed class StoreExport : IAsyncEnumerable<JsonObject>, IAsyncEnumerator<JsonObject>
{
    private readonly ChordContext context;
    private readonly Lock gate = new();
    /// <summary>The parts, until the export ends; dropped then, with the pages they hold.</summary>
    private IAsyncEnumerator<JsonObject>? parts;
    /// <summary>The parts while one of their MoveNextAsync calls runs (they are disposed only once it settled).</summary>
    private IAsyncEnumerator<JsonObject>? moving;
    private ExportState state = ExportState.Open;
    /// <summary>Gives the commit queue back while the export holds it.</summary>
    private Action? release;
    /// <summary>The abort listener while the export holds the queue.</summary>
    private CancellationTokenRegistration abort;
    private JsonObject? current;

    internal StoreExport(ChordStore store, ChordContext context, string? host = null)
    {
        this.context = context;
        parts = StoreInternal.ExportParts(store, context, Hold, () => End(ExportState.Closed), host).GetAsyncEnumerator();
    }

    private enum ExportState { Open, Aborted, Closed, Ended }

    /// <summary>The part the last <see cref="MoveNextAsync"/> reached.</summary>
    public JsonObject Current
    {
        get
        {
            lock (gate) return current!;
        }
    }

    /// <summary>This export (one pass). <paramref name="cancellationToken"/> is not used: the export's context cancels it.</summary>
    public IAsyncEnumerator<JsonObject> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

    /// <summary>The next part (upstream <c>next()</c>): false once the parts ended; fails <c>aborted</c> after an abort and
    /// <c>released</c> after the store closed, also for a part that needed no read.</summary>
    public async ValueTask<bool> MoveNextAsync()
    {
        if (context.AbortSignal is { IsCancellationRequested: true }) End(ExportState.Aborted);
        AssertOpen();
        IAsyncEnumerator<JsonObject> source;
        lock (gate)
        {
            if (parts is null)
            {
                current = null;
                return false;
            }
            source = parts;
            moving = source;
        }
        var pending = Next(source);
        bool holding;
        lock (gate) holding = release is not null;
        JsonObject? part;
        try
        {
            // Until the export holds the queue only `context` ends the wait; from then on its abort listener does.
            part = await (holding || context.AbortSignal is not { } signal ? pending : StoreInternal.AwaitWithContext(pending, signal)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!pending.IsCompleted) _ = pending.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            if (context.AbortSignal is { IsCancellationRequested: true }) End(ExportState.Aborted);
            AssertOpen();
            End(ExportState.Ended);
            throw;
        }
        // Ended meanwhile (an abort, the store's close): no part after that, even one that needed no read.
        AssertOpen();
        if (part is null)
        {
            End(ExportState.Ended);
            return false;
        }
        lock (gate) current = part;
        return true;
    }

    /// <summary>Ends the export and gives the queue back (upstream <c>return()</c>).</summary>
    public ValueTask DisposeAsync()
    {
        End(ExportState.Ended);
        return ValueTask.CompletedTask;
    }

    /// <summary>The next part of <paramref name="source"/>, or null at its end; disposes it once this call settled if the export ended
    /// meanwhile.</summary>
    private async Task<JsonObject?> Next(IAsyncEnumerator<JsonObject> source)
    {
        try
        {
            return await source.MoveNextAsync().ConfigureAwait(false) ? source.Current : null;
        }
        finally
        {
            bool ended;
            lock (gate)
            {
                moving = null;
                ended = !ReferenceEquals(parts, source);
            }
            if (ended) _ = StoreInternal.DisposeQuietly(source);
        }
    }

    /// <summary>The parts took the queue: the export holds it until it ends, and an abort ends it; ended already, it gives it back.</summary>
    private void Hold(Action release)
    {
        bool held;
        lock (gate)
        {
            held = state == ExportState.Open;
            if (held) this.release = release;
        }
        if (!held)
        {
            release();
            return;
        }
        if (context.AbortSignal is not { } signal) return;
        // Registering on a cancelled token runs the listener at once, which ends the export.
        var registration = signal.Register(static export => ((StoreExport)export!).End(ExportState.Aborted), this);
        bool open;
        lock (gate)
        {
            open = state == ExportState.Open;
            if (open) abort = registration;
        }
        if (!open) registration.Unregister();
    }

    /// <summary>Ends the export once: the queue given back, the abort listener removed, the parts disposed (once a running MoveNextAsync of
    /// theirs settled) and dropped.</summary>
    private void End(ExportState ended)
    {
        Action? given;
        CancellationTokenRegistration listener;
        IAsyncEnumerator<JsonObject>? dispose = null;
        lock (gate)
        {
            if (state != ExportState.Open) return;
            state = ended;
            given = release;
            release = null;
            listener = abort;
            abort = default;
            if (parts is { } source && !ReferenceEquals(moving, source)) dispose = source;
            parts = null;
            current = null;
        }
        given?.Invoke();
        // Unregister, not Dispose: the listener may be running this, and Dispose would wait for it.
        listener.Unregister();
        if (dispose is not null) _ = StoreInternal.DisposeQuietly(dispose);
    }

    /// <summary>Throws <c>aborted</c> after an abort, <c>released</c> after the store closed.</summary>
    private void AssertOpen()
    {
        ExportState now;
        lock (gate) now = state;
        if (now == ExportState.Aborted) throw StoreInternal.ExportAborted(context);
        if (now == ExportState.Closed) throw new ChordError("released", "store: closed", "closed");
    }
}

internal static partial class StoreInternal
{
    /// <summary>The rows and documents an export reads per page (upstream <c>EXPORT_PAGE</c>).</summary>
    internal const int ExportPage = 100;

    /// <summary><paramref name="task"/>, or a cancellation once <paramref name="signal"/> aborts (upstream <c>awaitWithContext</c>, which the
    /// store's export and import use): an aborted signal fails at once, even for a settled task.</summary>
    internal static Task<T> AwaitWithContext<T>(Task<T> task, CancellationToken signal) =>
        signal.IsCancellationRequested ? Task.FromCanceled<T>(signal) : task.WaitAsync(signal);

    /// <summary>The error of a use of an aborted export: <c>aborted</c>, the token's cancellation as the cause (upstream <c>aborted</c>, whose
    /// cause is the signal's reason).</summary>
    internal static ChordError ExportAborted(ChordContext context) =>
        new("aborted", "store: export aborted", cause: new OperationCanceledException(context.AbortSignal ?? CancellationToken.None));

    /// <summary>Disposes <paramref name="enumerator"/>, its failure dropped (upstream's <c>void parts.return().catch(() =&gt; {})</c>).</summary>
    internal static async Task DisposeQuietly<T>(IAsyncEnumerator<T> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An export's or an input's close reports nothing.
        }
    }

    /// <summary>The parts in archive order, after taking the queue (handed to <paramref name="hold"/>; the store's close calls
    /// <paramref name="end"/>); the queue is given back when they end (upstream <c>parts</c>).</summary>
    internal static async IAsyncEnumerable<JsonObject> ExportParts(ChordStore store, ChordContext context, Action<Action> hold, Action end, string? host = null)
    {
        var release = await store.TakeAsync(end).ConfigureAwait(false);
        hold(release);
        try
        {
            if (context.AbortSignal is { IsCancellationRequested: true }) throw ExportAborted(context);
            var (lastSeq, nextId) = (store.LastSeq, store.NextId);
            yield return new ArchiveManifest(ArchiveFormat, StoreSchema, lastSeq, nextId, host).ToJson();
            long writes = 0;
            var tables = store.Catalog.Values.Where(record => host is null || HostColumn(record, ContractsInternal.KindOfKey(host)) is not null)
                .OrderBy(record => record.Table, StringComparer.Ordinal).ToList();
            foreach (var record in tables)
            {
                yield return new TableWrite.Definition(record.Table, record.Version, record.State, record.Columns, record.Key, record.Fingerprint).ToJson();
                writes++;
                await foreach (var put in ExportPuts(store, record, context, host).ConfigureAwait(false))
                {
                    yield return put;
                    writes++;
                }
                foreach (var name in JavaScriptOrder(record.Indexes.Keys))
                {
                    yield return new TableWrite.Index(record.Table, name, record.Indexes[name]).ToJson();
                    writes++;
                }
            }
            await foreach (var part in ExportDocuments(store, context, host).ConfigureAwait(false))
            {
                yield return part;
                writes++;
            }
            yield return new JsonObject { ["kind"] = "end", ["writes"] = writes };
        }
        finally
        {
            release();
        }
    }

    /// <summary>The <c>put</c>s of a recorded table in key order, a page per read: the stored values and their versions (upstream
    /// <c>puts</c>).</summary>
    private static async IAsyncEnumerable<JsonObject> ExportPuts(ChordStore store, TableRecord record, ChordContext context, string? host)
    {
        var (table, columns, key) = (record.Table, record.Columns, record.Key);
        var select = string.Join(", ", RecordedPut(table, columns).Physical.Select(Quote));
        var order = string.Join(", ", key.Select(Quote));
        object?[]? after = null;
        do
        {
            var page = after;
            var conditions = new List<string>();
            if (host is not null) conditions.Add($"{Quote(HostColumn(record, ContractsInternal.KindOfKey(host))!)} = ?");
            if (page is not null) conditions.Add($"({order}) > ({string.Join(", ", page.Select(_ => "?"))})");
            var from = conditions.Count == 0 ? "" : $" WHERE {string.Join(" AND ", conditions)}";
            var sql = $"SELECT {select} FROM {Quote(table)}{from} ORDER BY {order} LIMIT ?";
            object?[] hostParameters = host is null ? [] : [host];
            object?[] parameters = [.. hostParameters, .. page ?? [], ExportPage];
            var rows = await store.ReadAsync(executor => executor.AllAsync(sql, parameters), context).ConfigureAwait(false);
            foreach (var stored in rows) yield return PutOf(record, stored);
            after = rows.Count < ExportPage ? null : [.. key.Select(name => rows[^1][name])];
        } while (after is not null);
    }

    /// <summary>A stored row as its <c>put</c>: <c>NULL</c> cells absent, booleans from 0 and 1, <c>json(...)</c> cells parsed with their
    /// versions (upstream <c>putOf</c>).</summary>
    private static JsonObject PutOf(TableRecord record, IReadOnlyDictionary<string, object?> stored)
    {
        var row = new OrderedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var versions = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, column) in record.Columns)
        {
            if (!stored.TryGetValue(name, out var value) || value is null) continue;
            // The recorded columns' types say what SQLite stored (STRICT tables): text for json(...) with its version.
            if (column.Type == ColumnType.Bool) row[name] = value is long flag ? flag == 1 : value is double number && number == 1;
            else if (column.Type == ColumnType.Json)
            {
                row[name] = JsonUtf16.MutableNode((string)value);
                versions[name] = SqliteValues.Integer(stored[VersionColumn(name)]);
            }
            else row[name] = value switch
            {
                string text => JsonValue.Create(text),
                long integer => JsonValue.Create(integer),
                double number => JsonValue.Create(number),
                _ => throw new InvalidOperationException($"store: a {value.GetType().Name} in column {ChordJson.Quote(name)}"),
            };
        }
        return new TableWrite.Put(record.Table, row, SqliteValues.Integer(stored[RowVersion]), versions).ToJson();
    }

    /// <summary>Every stored record by id, live and retired, as its creation without a base, then its kept revisions in seq order: a
    /// <c>none</c> or <c>latest</c> record's current base and its deltas, a retired <c>latest</c> one none, a <c>rewindable</c> one all
    /// (upstream <c>documents</c>).</summary>
    private static async IAsyncEnumerable<JsonObject> ExportDocuments(ChordStore store, ChordContext context, string? host)
    {
        long after = 0;
        while (true)
        {
            var from = after;
            var records = await store.ReadAsync(executor => host is null ? ListRecordsAsync(executor, from, ExportPage) : ListHostRecordsAsync(executor, host, from, ExportPage), context).ConfigureAwait(false);
            foreach (var record in records)
            {
                yield return new DocumentWrite.Create(record, null).ToJson();
                await foreach (var revision in ExportRevisions(store, record.Id, context).ConfigureAwait(false)) yield return revision;
            }
            if (records.Count < ExportPage) yield break;
            after = records[^1].Id;
        }
    }

    /// <summary>Document <paramref name="id"/>'s kept revisions in seq order, read page by page (a long history is never one list; upstream
    /// <c>revisions</c>).</summary>
    private static async IAsyncEnumerable<JsonObject> ExportRevisions(ChordStore store, long id, ChordContext context)
    {
        long after = 0;
        while (true)
        {
            var from = after;
            var page = await store.ReadAsync(executor => ListRevisionsAsync(executor, id, from, ExportPage), context).ConfigureAwait(false);
            foreach (var revision in page) yield return new DocumentWrite.KeptRevision(revision.Id, revision.Seq, revision.Content).ToJson();
            if (page.Count < ExportPage) yield break;
            after = page[^1].Seq;
        }
    }
}
