using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;
using PiSharp.Telemetry;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/support.ts, the test support every Chord v2 test imports.
// Ported here: the GC check (expectCollected) and the shared declarations of the slice tests (Server, SERVER_ADDRESS, Notes and its
// variants, Session, PlacedNotes, NoteStore, Restartable, StoreRestartable), and wireNames without its `named` (which reads link parts);
// area cv2-data added the counters (counted, metered) and rejection; area cv2-state the watch frames and changes (framesOf, changesOf,
// valuesOf; delivered() flushes a host's scheduler, State/StateHost.cs); area cv2-store-engine the store helpers (memoryStore,
// faultyDatabase, faultyStore, holdNextCommit, tableOwner, tableIn, ArchiveTables, archiveStore; the Program block at the end of this
// file), over TestSqliteDatabase, which stands in for src/sqlite/node.ts's NodeSqliteDatabase (see its summary); area cv2-store-archive
// exported (with the store helpers), flushed and settledHeap (a macrotask turn and a settled heap, which the export cases use first).
// Equal<A, B> is a type-level check with no C# counterpart. The rest needs layers later areas bring, and each comes with the first area
// whose tests use it: the remaining declarations (NotesDoc; the documents areas), and the hosts, links, relays and raw peers (failLive,
// restartable, storeRestartable, loaded, loadedPlugins, fakeTimers, failures, linked, RawPeer, partValue, CODECS, forEachCodec, rawLinked,
// transported, serveNotes, addNote, rawProvider, rawParent, statesOf, EDITOR, bindNext, bindHold, instancesOf, relayed, rawWorkers,
// incarnationOf, countInflated), which build on createHost and the link layer (phase 6).
internal static class ChordSupport
{
    /// <summary>Expects every referent to be collected once nothing else holds it (upstream runs gc() between macrotasks).</summary>
    internal static async Task ExpectCollected(params WeakReference[] references)
    {
        for (var round = 0; round < 10 && references.Any(reference => reference.IsAlive); round++)
        {
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Program.Sequence(references.Select(_ => true), references.Select(reference => !reference.IsAlive), "every referent is collected");
    }

    /// <summary>WeakReferences to the values <paramref name="make"/> returns, made in a frame of its own so no local keeps them alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WeakReference[] Weakly(Func<object[]> make) => [.. make().Select(value => new WeakReference(value))];

    /// <summary>Starts <paramref name="watch"/> and collects its frames as <c>(value, ops)</c> (upstream <c>framesOf</c>).</summary>
    internal static List<(T Value, ImmutableArray<Op> Ops)> FramesOf<T>(IWatchHandle<T> watch)
    {
        var frames = new List<(T, ImmutableArray<Op>)>();
        watch.Start((call, _) =>
        {
            if (call is WatchValueCall<T> value) frames.Add((value.Value, value.Ops));
            return Task.CompletedTask;
        });
        return frames;
    }

    /// <summary>Starts <paramref name="watch"/> and collects every call it gets: values (the retired flag included) and failures (upstream
    /// <c>changesOf</c>).</summary>
    internal static List<WatchCall<T>> ChangesOf<T>(IWatchHandle<T> watch)
    {
        var calls = new List<WatchCall<T>>();
        watch.Start((call, _) =>
        {
            calls.Add(call);
            return Task.CompletedTask;
        });
        return calls;
    }

    /// <summary>The values of <paramref name="calls"/>, a failure as <c>{ failed: message }</c> (upstream <c>valuesOf</c>).</summary>
    internal static List<object?> ValuesOf<T>(IEnumerable<WatchCall<T>> calls) =>
        [.. calls.Select(call => call is WatchValueCall<T> value ? value.Value : (object)new { failed = ((Exception)((WatchFailedCall<T>)call).Error!).Message })];

    /// <summary>A watch call as upstream's tests compare it: <c>{"kind":"value","value":…,"ops":[…],"retired":…}</c>, or
    /// <c>{"kind":"failed","error":"&lt;message&gt;"}</c> (toEqual on an Error compares its message).</summary>
    internal static string CallJson(WatchCall<JsonNode?> call) => call switch
    {
        WatchValueCall<JsonNode?> value => ChordJson.Stringify(new JsonObject
        {
            ["kind"] = "value",
            ["value"] = value.Value?.DeepClone(),
            ["ops"] = OpsJson(value.Ops),
            ["retired"] = value.Retired,
        }),
        WatchFailedCall<JsonNode?> failed => ChordJson.Stringify(new JsonObject { ["kind"] = "failed", ["error"] = ((Exception)failed.Error!).Message }),
        _ => throw new InvalidOperationException("an unknown call"),
    };

    /// <summary>A frame as upstream's tests compare it: <c>[value, ops]</c>.</summary>
    internal static string FrameJson((JsonNode? Value, ImmutableArray<Op> Ops) frame) =>
        ChordJson.Stringify(new JsonArray(frame.Value?.DeepClone(), OpsJson(frame.Ops)));

    /// <summary>Ops as their JSON tuples.</summary>
    internal static JsonArray OpsJson(ImmutableArray<Op> ops) => [.. ops.Select(op => (JsonNode)op.ToJson())];

    /// <summary>The variants of the notes types: an editor variant for owners, a reader one for guests.</summary>
    private static OrderedDictionary<string, VariantDeclaration> NotesVariants() => new()
    {
        ["editor"] = new()
        {
            State = { ["notes"] = Arr(Obj(("text", Str()))), ["count"] = Int() },
            Methods =
            {
                ["add"] = Method(Obj(("text", Str())), Int()),
                ["fail"] = Method(Obj(("how", Str()))),
                ["wait"] = Method(),
                ["echo"] = Method(Obj(("data", Bytes())), Bytes()),
            },
        },
        ["reader"] = new() { State = { ["notes"] = Arr(Obj(("text", Str()))), ["count"] = Int() }, Methods = { ["wait"] = Method() } },
    };

    /// <summary>The host kind of the test servers, where every test type but <see cref="PlacedNotes"/> lives.</summary>
    internal static readonly HostKind Server = new("server");

    /// <summary><c>Server()</c> as a hold names it on the wire: the address of every test type served on the server.</summary>
    internal const string ServerAddress = """{"kind":"server","params":{}}""";

    /// <summary>The remote type of the link tests: notes per session, an editor variant for owners, a reader one for guests.</summary>
    internal static readonly Contract Notes = Contract.Remote("app.notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        HostAddress = _ => Server.Address(),
        Variants = NotesVariants(),
    });

    /// <summary>The kind of the relay tests' workers: one per session.</summary>
    internal static readonly HostKind Session = new("session", new() { ["id"] = Str() });

    /// <summary><see cref="Notes"/> placed on the session's worker: the relay tests' type.</summary>
    internal static readonly Contract PlacedNotes = Contract.Remote("app.placed-notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        HostAddress = values => Session.Address(("id", values["session"])),
        Variants = NotesVariants(),
    });

    /// <summary>The shared store behind <see cref="Notes"/> (§6): the notes of a session, once, whoever views them.</summary>
    internal static readonly Contract NoteStore = Contract.Local("app.note-store", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        Members = new() { ["list"] = Arr(Obj(("text", Str()))), ["add"] = Method(Obj(("text", Str())), Int()) },
    });

    /// <summary>A local type whose objects' attempts a test ends by hand (upstream's <c>restartable</c>, phase 6); it ends with its last
    /// reference.</summary>
    internal static readonly Contract Restartable = Contract.Local("app.restartable", new()
    {
        Version = 1,
        Params = new() { ["id"] = Int() },
        Members = new() { ["touch"] = Method() },
    });

    /// <summary>A local type with empty params whose attempts a test ends by hand (<c>storeRestartable</c>, phase 6): it may persist (P3).</summary>
    internal static readonly Contract StoreRestartable = Contract.Local("app.store", new()
    {
        Version = 1,
        Params = new(),
        Members = new() { ["touch"] = Method() },
    });

    /// <summary>The wire's indexes of a remote contract's variant (2b): the variant's, and its state members' and methods' by name.</summary>
    internal static (int Variant, IReadOnlyDictionary<string, int> State, IReadOnlyDictionary<string, int> Methods) WireNames(Contract type, string variant)
    {
        var names = ContractsInternal.VariantNamesOf(ContractsInternal.CurrentVariants(type)[variant]);
        static IReadOnlyDictionary<string, int> Indexes(IReadOnlyList<string> list) => list.Select((name, index) => (name, index)).ToDictionary(entry => entry.name, entry => entry.index);
        return (ContractsInternal.VariantOrder(type).ToList().IndexOf(variant), Indexes(names.State), Indexes(names.Methods));
    }

    /// <summary>A JSON node from its text (the expected values of a case).</summary>
    internal static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    /// <summary>Counts of the <c>chord.data.*</c> counters (upstream <c>Counts</c>).</summary>
    internal readonly record struct Counts(double Walks, double Validations, double Compilations);

    /// <summary>Runs <paramref name="work"/> with a counting context and returns the counters it added (upstream <c>counted</c>).</summary>
    internal static async Task<Counts> Counted(Action<ChordContext> work)
    {
        var telemetry = new InMemoryTelemetryContext();
        await telemetry.StartSpanAsync("test", span => work(ChordContext.WithTelemetry(span, ChordContext.Background)));
        double walks = 0, validations = 0, compilations = 0;
        foreach (var measurement in telemetry.GetMetrics())
        {
            switch (measurement.Name)
            {
                case "chord.data.walks": walks += measurement.Value; break;
                case "chord.data.validations": validations += measurement.Value; break;
                case "chord.data.compilations": compilations += measurement.Value; break;
                default: throw new InvalidOperationException("an unexpected counter: " + measurement.Name);
            }
        }
        return new Counts(walks, validations, compilations);
    }

    /// <summary>A telemetry adapter, a context counting through it, and the counters recorded so far, by name (upstream <c>metered</c>).</summary>
    internal static async Task<(InMemoryTelemetryContext Telemetry, ChordContext Context, Func<IReadOnlyDictionary<string, double>> Counts)> Metered()
    {
        var telemetry = new InMemoryTelemetryContext();
        // A settled span still records through its adapter's metrics.
        var span = await telemetry.StartSpanAsync("test", span => span);
        IReadOnlyDictionary<string, double> Counts() =>
            telemetry.GetMetrics().GroupBy(measurement => measurement.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(measurement => measurement.Value), StringComparer.Ordinal);
        return (telemetry, ChordContext.WithTelemetry(span, ChordContext.Background), Counts);
    }

    /// <summary>Lets pending continuations run (upstream <c>flushed</c>, one macrotask turn): continuations here run on the thread pool, so
    /// a short delay stands in for setImmediate.</summary>
    internal static Task Flushed() => Task.Delay(20);

    /// <summary>The heap in use once garbage settled (upstream <c>settledHeap</c>, five rounds of a macrotask turn and gc() before
    /// process.memoryUsage().heapUsed): the managed heap after full collections.</summary>
    internal static async Task<long> SettledHeap()
    {
        for (var round = 0; round < 5; round++)
        {
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>The error <paramref name="task"/> fails with, as ChordError JSON when it is one (upstream <c>rejection</c>).</summary>
    internal static async Task<object> Rejection(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception error)
        {
            return error is ChordError chord ? chord.ToJson() : error;
        }
        throw new InvalidOperationException("expected a rejection");
    }
}

// The store helpers of support.ts. Upstream opens each store over node:sqlite (openNodeSqliteDatabase, src/sqlite/node.ts); here over
// TestSqliteDatabase, the test project's ISqliteDatabase over Microsoft.Data.Sqlite (PiSharp.Chord references no SQLite driver).
// faultyDatabase's statement failures and the outbox tests' replaced db.transaction and db.all (a test reassigns the facade's methods) are
// the database's own seams here: Fail, AfterTransaction and BeforeAll. holdNextCommit replaces store.commit for one call; here it sets
// ChordStore.NextCommit, which the next commit takes at its call, as the replaced method does.
internal static partial class Program
{
    /// <summary>A store on a fresh SQLite <c>:memory:</c> database, as Chord's layers see it (upstream <c>memoryStore</c>).</summary>
    internal static Task<ChordStore> MemoryStore(bool? outbox = null) => StoreOver(new TestSqliteDatabase(":memory:"), outbox);

    /// <summary>A store opened over <paramref name="db"/> (upstream's <c>openStore(sqliteBackend(db), context, { outbox })</c>, as a
    /// ChordStore).</summary>
    internal static async Task<ChordStore> StoreOver(TestSqliteDatabase db, bool? outbox = null) =>
        (ChordStore)await Tables.OpenStoreAsync(Tables.SqliteBackend(db), ChordContext.Background, new StoreOptions { Outbox = outbox });

    /// <summary>A database on <c>:memory:</c> whose next <c>statement</c> (<c>BEGIN IMMEDIATE</c>, <c>COMMIT</c> or <c>ROLLBACK</c>)
    /// throws, once per <see cref="TestSqliteDatabase.Fail"/> (upstream <c>faultyDatabase</c>).</summary>
    internal static TestSqliteDatabase FaultyDatabase() => new(":memory:");

    /// <summary>A store over <see cref="FaultyDatabase"/>, and its <c>fail</c> (upstream <c>faultyStore</c>).</summary>
    internal static async Task<(ChordStore Store, Action<string> Fail)> FaultyStore(bool? outbox = null)
    {
        var db = FaultyDatabase();
        return (await StoreOver(db, outbox), db.Fail);
    }

    /// <summary>Holds <paramref name="store"/>'s next commit step inside its backend transaction, after its admission: <c>Entered</c>
    /// completes there, <c>Release</c> lets it finish; later commits run as usual (upstream <c>holdNextCommit</c>).</summary>
    internal static (Task Entered, Action Release) HoldNextCommit(ChordStore store)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new StagedWrite(WriteOrder.Write, async _ =>
        {
            entered.TrySetResult();
            await gate.Task;
            return [new TableWrite.DropIndex("x", "y")];
        });
        store.NextCommit = writes => [.. writes, held];
        return (entered.Task, () => gate.TrySetResult());
    }

    /// <summary>The owner of a test's table handles: its failures collected (upstream <c>tableOwner</c>).</summary>
    internal static TestTableOwner TableOwner() => new();

    /// <summary>Creates <paramref name="definition"/> (and its indexes) in <paramref name="store"/> as <c>app.store:&lt;name&gt;</c> and
    /// returns a handle to it (upstream <c>tableIn</c>).</summary>
    internal static async Task<TableHandle> TableIn(ChordStore store, TableDefinition definition, TestTableOwner? owner = null)
    {
        var table = $"app.store:{definition.Name}";
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(definition, table)], store, CommitFence.Open, "app.store", ChordContext.Background);
        return new TableHandle(definition, table, store, owner ?? TableOwner());
    }

    /// <summary>The tables of <see cref="ArchiveStore"/>: edits (v1 with an index; v2 adds <c>done</c>) and notes, whose v2 migration
    /// crashed (upstream <c>ArchiveTables</c>).</summary>
    internal static class ArchiveTables
    {
        internal static readonly TableDefinition EditsV1 = Tables.DefineTable("edits", new TableOptions
        {
            Version = 1,
            Columns = StoreColumns(("id", Str()), ("path", Str()), ("at", Int()), ("note", Opt(Str())),
                ("meta", Opt(Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 1 })))),
            Key = ["id"],
            Indexes = StoreIndexes(("by_path", ["path", "at"])),
        });

        internal static readonly TableDefinition EditsV2 = Tables.EvolveTable(EditsV1, t => t.Add("done", Bool(@default: false)));

        internal static readonly TableDefinition NotesV1 = Tables.DefineTable("notes", new TableOptions
        {
            Version = 1,
            Columns = StoreColumns(("id", Int()), ("text", Str())),
            Key = ["id"],
        });

        internal static readonly TableDefinition NotesV2 = Tables.EvolveTable(NotesV1, t => t.Rows(row =>
        {
            if (row["id"]!.ToJsonString() == "120") throw new InvalidOperationException("crash");
            return new JsonObject { ["text"] = $"{(string)row["text"]!}!" };
        }));
    }

    /// <summary>A store holding a bit of everything an archive carries: <c>app.store:edits</c> at version 2 with 150 rows (two export
    /// pages; some written at version 1, <c>NULL</c> and <c>json(...)</c> cells, an index), <c>app.store:notes</c> left <c>migrating</c>
    /// at version 2 after one committed chunk, and documents: a singleton, keyed ones with deltas after their base, and a retired one
    /// (upstream <c>archiveStore</c>).</summary>
    internal static async Task<ChordStore> ArchiveStore(bool? outbox = null)
    {
        var context = ChordContext.Background;
        var store = await MemoryStore(outbox);
        var fence = CommitFence.Open;
        var edits = await ArchiveTableAt(store, "app.store:edits", ArchiveTables.EditsV1);
        var notes = await ArchiveTableAt(store, "app.store:notes", ArchiveTables.NotesV1);
        var staged = new List<IStagedWrite>();
        var editChanges = new TableChanges(edits, staged);
        for (var i = 0; i < 150; i++)
        {
            var row = new JsonObject { ["id"] = "e" + i.ToString("000", CultureInfo.InvariantCulture), ["path"] = $"p/{i % 4}", ["at"] = i };
            if (i % 2 == 0) row["note"] = $"n{i}";
            if (i % 3 == 0) row["meta"] = new JsonObject { ["a"] = i };
            editChanges.Put(row);
        }
        var noteChanges = new TableChanges(notes, staged);
        for (var i = 0; i < 150; i++) noteChanges.Put(new JsonObject { ["id"] = i, ["text"] = $"t{i}" });
        await store.CommitAsync(staged, fence, context);
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(ArchiveTables.EditsV2, "app.store:edits")], store, fence, "app", context);
        try
        {
            await StoreInternal.PrepareTablesAsync([new DeclaredTable(ArchiveTables.NotesV2, "app.store:notes")], store, fence, "app", context);
        }
        catch (Exception)
        {
            // The crash leaves the notes migrating (upstream's .catch(() => {})).
        }
        IReadOnlyList<long> ids = [];
        var created = new CommitFence(() => true, (_, made) => ids = made);
        string[] keys = ["", "a", "b", "gone"];
        await store.CommitAsync([.. keys.Select(key => StoreInternal.CreateDocument(
            new DocumentAddress("app.store", "chord", "memo", key), new DocumentBase(1, "f1", new JsonObject { ["title"] = key }),
            ChordJson.Stringify(new JsonObject { ["title"] = key })))], created, context);
        var (a, b, gone) = (ids[1], ids[2], ids[3]);
        IStagedWrite Delta(long id, long revision, string title)
        {
            var ops = new JsonArray(new JsonArray("s", new JsonArray("title"), title));
            return StoreInternal.ChangeDocument(id, revision, new DocumentContent.Delta(1, ops), ChordJson.Stringify(ops));
        }
        await store.CommitAsync([Delta(a, 1, "a2"), StoreInternal.RetireDocument(gone, 1)], fence, context);
        await store.CommitAsync([Delta(a, 2, "a3")], fence, context);
        await store.CommitAsync([Delta(b, 1, "b2")], fence, context);
        return store;
    }

    /// <summary>Every part of <paramref name="store"/>'s export, in order (upstream <c>exported</c>).</summary>
    internal static async Task<List<JsonObject>> Exported(IStore store)
    {
        var parts = new List<JsonObject>();
        await foreach (var part in store.Export(ChordContext.Background)) parts.Add(part);
        return parts;
    }

    /// <summary>toEqual on archives: the parts as one JSON array (copies of them), compared with <see cref="Json"/>.</summary>
    internal static void SameParts(IEnumerable<JsonNode?> expected, IEnumerable<JsonNode?> actual, string what) =>
        Json(PartsJson(expected).ToJsonString(), PartsJson(actual), what);

    /// <summary>Archive parts as a JSON array of copies of them.</summary>
    internal static JsonArray PartsJson(IEnumerable<JsonNode?> parts) => [.. parts.Select(part => part?.DeepClone())];

    /// <summary><paramref name="definition"/> prepared as <paramref name="table"/> in <paramref name="store"/>, and a handle to it (upstream
    /// <c>tableAt</c>).</summary>
    private static async Task<TableHandle> ArchiveTableAt(ChordStore store, string table, TableDefinition definition)
    {
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(definition, table)], store, CommitFence.Open, "app", ChordContext.Background);
        return new TableHandle(definition, table, store, TableOwner());
    }
}

/// <summary>A table owner whose failures are collected (upstream <c>tableOwner()</c>'s result).</summary>
internal sealed class TestTableOwner : ITableOwner
{
    public string Label => "app.store";

    public object Scope { get; } = new();

    /// <summary>The failures the handles reported.</summary>
    internal List<ChordError> Failures { get; } = [];

    public void AssertLive()
    {
    }

    public void Fail(ChordError error) => Failures.Add(error);
}

/// <summary>An <see cref="ISqliteDatabase"/> over Microsoft.Data.Sqlite on a file or <c>:memory:</c>, as src/sqlite/node.ts's
/// NodeSqliteDatabase is over node:sqlite: one operation or transaction at a time, in call order; a transaction is <c>BEGIN
/// IMMEDIATE</c>, the callback and <c>COMMIT</c>, and on a throw <c>ROLLBACK</c> and the throw (both failing: an AggregateException of
/// the two); a transaction's handle refuses calls once it ended; positional <c>?</c> parameters numbered in order; rows as maps of column
/// to SQLite value in the statement's column order. Its seams stand in for what upstream's tests do to the facade: <see cref="Fail"/>
/// (faultyDatabase: a failed ROLLBACK still rolls back, so the database stays usable), <see cref="AfterTransaction"/> (a replaced
/// db.transaction awaiting after the real one returned), <see cref="BeforeAll"/> (a replaced db.all awaiting before the real one) and
/// <see cref="OnTransactionStatement"/> (a test acting while a transaction runs, between its statements).</summary>
internal sealed class TestSqliteDatabase : ISqliteDatabase
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Executor executor;
    private readonly HashSet<string> failing = new(StringComparer.Ordinal);
    private bool closed;

    internal TestSqliteDatabase(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        executor = new Executor(this, null);
    }

    /// <summary>Awaited after a transaction committed or rolled back, before it returns (null: none).</summary>
    internal Func<Task>? AfterTransaction { get; set; }

    /// <summary>Awaited before a database-level <see cref="AllAsync"/> runs (null: none).</summary>
    internal Func<Task>? BeforeAll { get; set; }

    /// <summary>Called with each statement a transaction's handle runs, before it runs (null: none).</summary>
    internal Action<string>? OnTransactionStatement { get; set; }

    /// <summary>Makes the next <paramref name="statement"/> of a transaction (<c>BEGIN IMMEDIATE</c>, <c>COMMIT</c> or <c>ROLLBACK</c>)
    /// throw, once.</summary>
    internal void Fail(string statement)
    {
        lock (failing) failing.Add(statement);
    }

    public Task ExecAsync(string sql) => Queued(() => executor.ExecAsync(sql));

    public Task RunAsync(string sql, params object?[] parameters) => Queued(() => executor.RunAsync(sql, parameters));

    public Task<long> RunCountedAsync(string sql, params object?[] parameters) => Queued(() => executor.RunCountedAsync(sql, parameters));

    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => Queued(() => executor.GetAsync(sql, parameters));

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters)
    {
        if (BeforeAll is { } before) await before();
        return await Queued(() => executor.AllAsync(sql, parameters));
    }

    public async Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback)
    {
        T result;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Transaction("BEGIN IMMEDIATE");
            var scope = new Executor(this, OnTransactionStatement ?? (_ => { }));
            try
            {
                result = await callback(scope).ConfigureAwait(false);
                scope.End();
                Transaction("COMMIT");
            }
            catch (Exception error)
            {
                scope.End();
                try
                {
                    Transaction("ROLLBACK");
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("SQLite transaction failed and rollback failed", error, rollback);
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
        if (AfterTransaction is { } after) await after();
        return result;
    }

    public async Task CloseAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            closed = true;
            connection.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A transaction statement, unless <see cref="Fail"/> made it fail (a failed ROLLBACK still rolls back).</summary>
    private void Transaction(string sql)
    {
        bool fails;
        lock (failing) fails = failing.Remove(sql);
        if (!fails)
        {
            Execute(sql, []);
            return;
        }
        if (sql == "ROLLBACK") Execute(sql, []);
        throw new InvalidOperationException($"injected: {sql} failed");
    }

    private async Task Queued(Func<Task> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> Queued<T>(Func<Task<T>> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private int Execute(string sql, object?[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private List<IReadOnlyDictionary<string, object?>> Query(string sql, object?[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.GetValue(index);
                row[reader.GetName(index)] = value is DBNull ? null : value;
            }
            rows.Add(row);
        }
        return rows;
    }

    private SqliteCommand Command(string sql, object?[] parameters)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        var command = connection.CreateCommand();
        command.CommandText = parameters.Length == 0 ? sql : Numbered(sql);
        for (var index = 0; index < parameters.Length; index++)
        {
            command.Parameters.Add(new SqliteParameter("?" + (index + 1).ToString(CultureInfo.InvariantCulture), parameters[index] switch
            {
                null => DBNull.Value,
                int integer => (long)integer,
                var value => value,
            }));
        }
        return command;
    }

    /// <summary>Numbers each <c>?</c> outside quoted text and identifiers (the store's SQL has no comments).</summary>
    private static string Numbered(string sql)
    {
        var text = new StringBuilder(sql.Length + 8);
        var number = 0;
        char? quote = null;
        foreach (var character in sql)
        {
            if (quote is { } open)
            {
                if (character == open) quote = null;
                text.Append(character);
            }
            else if (character is '\'' or '"')
            {
                quote = character;
                text.Append(character);
            }
            else if (character == '?') text.Append('?').Append((++number).ToString(CultureInfo.InvariantCulture));
            else text.Append(character);
        }
        return text.ToString();
    }

    /// <summary>The database's executor (<paramref name="statement"/> null), or a transaction's handle (<paramref name="statement"/> the
    /// test hook), which refuses calls once its transaction ended.</summary>
    private sealed class Executor(TestSqliteDatabase database, Action<string>? statement) : ISqliteExecutor
    {
        private volatile bool ended;

        internal void End() => ended = true;

        public Task ExecAsync(string sql) => Run(() => database.Execute(sql, []), sql);

        public Task RunAsync(string sql, params object?[] parameters) => Run(() => database.Execute(sql, parameters), sql);

        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => Run(() => (long)database.Execute(sql, parameters), sql);

        public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) =>
            Run(() => database.Query(sql, parameters).FirstOrDefault(), sql);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) =>
            Run<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(() => database.Query(sql, parameters), sql);

        private Task Run(Action operation, string sql) => Run(() =>
        {
            operation();
            return true;
        }, sql);

        private Task<T> Run<T>(Func<T> operation, string sql)
        {
            try
            {
                if (ended) throw new InvalidOperationException("SQLite transaction handle is no longer active");
                statement?.Invoke(sql);
                return Task.FromResult(operation());
            }
            catch (Exception error)
            {
                return Task.FromException<T>(error);
            }
        }
    }
}
