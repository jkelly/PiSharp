// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/host-data.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Host;

/// <summary>FIFO operations, with exclusive sections that drain and exclude activation loads.</summary>
internal sealed class HostDataGate
{
    private readonly Lock sync = new();
    private Task tail = Task.CompletedTask;
    private int readers;
    private bool exclusive;
    private TaskCompletionSource? opened;
    private TaskCompletionSource? drained;

    internal Task? Enter()
    {
        lock (sync)
        {
            if (!exclusive) { readers++; return null; }
        }
        return EnterLaterAsync();
    }

    private async Task EnterLaterAsync()
    {
        while (true)
        {
            Task waiting;
            lock (sync)
            {
                if (!exclusive) { readers++; return; }
                waiting = opened!.Task;
            }
            await waiting;
        }
    }

    internal void Leave()
    {
        lock (sync)
        {
            if (readers <= 0) throw new InvalidOperationException("Unbalanced host-data load gate");
            if (--readers == 0) drained?.TrySetResult();
        }
    }

    internal async Task<T> TurnAsync<T>(Func<Turn, Task<T>> work, ChordContext context)
    {
        Task previous;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            previous = tail;
            // An abandoned turn must not let its successor overtake an earlier operation.
            tail = PassAsync(previous, finished.Task);
        }
        try
        {
            await AwaitAsync(previous, context);
            return await work(new Turn(this, context));
        }
        finally { finished.TrySetResult(); }
    }

    private static async Task PassAsync(Task previous, Task finished) { await previous; await finished; }

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> section, ChordContext context)
    {
        Task? pending;
        lock (sync)
        {
            if (exclusive) throw new InvalidOperationException("Nested host-data exclusive section");
            exclusive = true;
            opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = readers == 0 ? null : (drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        try
        {
            if (pending is not null) await AwaitAsync(pending, context);
            context.AbortSignal?.ThrowIfCancellationRequested();
            return await section();
        }
        finally
        {
            lock (sync)
            {
                exclusive = false;
                drained = null;
                opened!.TrySetResult();
                opened = null;
            }
        }
    }

    private static Task AwaitAsync(Task task, ChordContext context) => context.AbortSignal is { } signal
        ? signal.IsCancellationRequested ? Task.FromCanceled(signal) : task.WaitAsync(signal)
        : task;

    internal sealed class Turn(HostDataGate gate, ChordContext context)
    {
        internal Task<T> ExclusiveAsync<T>(Func<Task<T>> section) => gate.ExclusiveAsync(section, context);
    }
}

public sealed partial class ChordHost
{
    internal readonly HostDataGate HostDataGate = new();

    /// <summary>Exports the documents and rows owned by one address.</summary>
    public StoreExport ExportHostData(Address address, ChordContext context) => Dispatcher.Invoke(() =>
    {
        var store = HostDataStore("exportHostData", context);
        return Tables.ExportHostData(store, address, context);
    });

    /// <summary>Imports an eagerly validated archive and atomically refreshes live documents and queries.</summary>
    public Task<HostDataCounts> ImportHostDataAsync(IEnumerable<JsonNode?> parts, ChordContext context, HostImportOptions? options = null) =>
        Dispatcher.RunAsync(async () =>
        {
            HostDataStore("importHostData", context);
            return await ImportHostArchiveAsync(await Tables.ReadHostArchiveAsync(parts, context), context, options ?? new());
        });

    /// <summary>Imports a streamed archive after validating it in full.</summary>
    public Task<HostDataCounts> ImportHostDataAsync(IAsyncEnumerable<JsonNode?> parts, ChordContext context, HostImportOptions? options = null) =>
        Dispatcher.RunAsync(async () =>
        {
            HostDataStore("importHostData", context);
            return await ImportHostArchiveAsync(await Tables.ReadHostArchiveAsync(parts, context), context, options ?? new());
        });

    /// <summary>Deletes data owned by an address and refreshes its live observers in the commit batch; with
    /// <see cref="HostDeleteOptions.Plugin"/>, only the records and rows the objects of that plugin own (slice 14 §6).</summary>
    public Task<HostDataCounts> DeleteHostDataAsync(Address address, ChordContext context, HostDeleteOptions? options = null) => Dispatcher.RunAsync(async () =>
    {
        var store = HostDataStore("deleteHostData", context);
        ArgumentNullException.ThrowIfNull(address);
        HashSet<string>? only = null;
        if (options?.Plugin is { } plugin)
        {
            if (!Plugins.ContainsKey(plugin)) throw DataInternal.Invalid("args", $"deleteHostData: plugin \"{plugin}\" is not loaded here");
            // A plugin's data is what the types it serves own: their names are the owners in the store.
            only = new HashSet<string>(Types.Where(type => type.Value.Plugin == plugin).Select(type => type.Key), StringComparer.Ordinal);
        }
        var key = StoreInternal.StoredText(address.Key, "deleteHostData: address");
        return await HostDataGate.TurnAsync(turn => turn.ExclusiveAsync(async () =>
        {
            var owners = await StoreInternal.HostDataOwnersAsync(store, key, null, context);
            if (only is not null) owners.IntersectWith(only);
            return await CoordinatedHostDataAsync(owners, key, context,
                commit => StoreInternal.DeleteHostDataAsync(store, key, commit, ChordContext.WithoutAbortSignal(context), only));
        }), context);
    });

    private ChordStore HostDataStore(string operation, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AssertOpen();
        HostTransactions.AssertOutside(operation);
        Batch.AssertOutside(operation);
        CallContext.Validate(this, context);
        return Store ?? throw DataInternal.Invalid("args", $"{operation}: a host store is required");
    }

    private Task<HostDataCounts> ImportHostArchiveAsync(HostArchive archive, ChordContext context, HostImportOptions options)
    {
        var store = Store!;
        var target = options.Address is null ? archive.Host : StoreInternal.StoredText(options.Address.Key, "importHostData: address");
        if (ContractsInternal.KindOfKey(target) != ContractsInternal.KindOfKey(archive.Host))
            throw DataInternal.Invalid("args", "importHostData: the target is a different host kind");
        return HostDataGate.TurnAsync(async turn =>
        {
            var activated = new Dictionary<string, Action>(StringComparer.Ordinal);
            try
            {
                while (true)
                {
                    var section = await turn.ExclusiveAsync(async () =>
                    {
                        var migrating = StoreInternal.TablesToMigrate(store, archive);
                        var histories = new Dictionary<string, TableDefinition>(StringComparer.Ordinal);
                        foreach (var name in migrating)
                            foreach (var owner in Objects.Values)
                                if (owner.Current is { Sealed: false } attempt)
                                    foreach (var table in attempt.Self.Tables)
                                        if (table.Sql.Table == name) histories[name] = table.Definition;
                        var missing = migrating.Where(name => !histories.ContainsKey(name))
                            .Select(name => name[..name.IndexOf(':')]).Distinct(StringComparer.Ordinal).ToArray();
                        if (missing.Length > 0) return (Missing: missing, Counts: (HostDataCounts?)null);
                        var owners = await StoreInternal.HostDataOwnersAsync(store, target, archive, context);
                        var plan = new HostImportPlan(target, options.Existing, histories);
                        var counts = await CoordinatedHostDataAsync(owners, target, context,
                            commit => StoreInternal.ImportHostDataAsync(store, archive, plan, commit, ChordContext.WithoutAbortSignal(context)));
                        return (Missing: Array.Empty<string>(), Counts: counts);
                    });
                    if (section.Counts is not null) return section.Counts;
                    foreach (var owner in section.Missing)
                    {
                        if (activated.ContainsKey(owner) || !Types.TryGetValue(owner, out var served) || !served.HostLifetime)
                            throw DataInternal.Invalid("args", $"importHostData: {owner} is not served here to migrate its tables");
                        var reference = Acquire(served.Contract, new JsonObject(), OwnCaller, context);
                        try { await reference.ReadyAsync(context); }
                        catch { reference.Release(); throw; }
                        activated.Add(owner, reference.Release);
                    }
                }
            }
            finally { foreach (var release in activated.Values) release(); }
        }, context);
    }

    private async Task<HostDataCounts> CoordinatedHostDataAsync(IEnumerable<string> owners, string key, ChordContext context,
        Func<HostCommit, Task<HostDataCounts>> change)
    {
        var kind = ContractsInternal.KindOfKey(key);
        var ownerSet = owners.ToHashSet(StringComparer.Ordinal);
        var objects = Objects.Values.Where(owner => ownerSet.Contains(owner.Served.Contract.Name) || owner.Documents.HostSlots(kind).Count > 0)
            .OrderBy(owner => owner.Identity, StringComparer.Ordinal).ToArray();
        var releases = new List<Action>();
        try
        {
            foreach (var owner in objects) releases.Add(await AcquireLineAsync(owner.Documents, context));
            var slots = objects.SelectMany(owner => owner.Documents.HostSlots(kind)).Where(slot => slot.Holds(key)).ToArray();
            var changed = new ChangedDocument[slots.Length];
            var quiet = ChordContext.WithoutAbortSignal(context);
            // Every read over a table with a host column of the kind, joined and exists tables included (slice 14 §5).
            var queries = Store!.ReadIndex.OnHostKind(kind);
            var runs = new ReadRun[queries.Count];
            var read = new StagedWrite(WriteOrder.Write, async step =>
            {
                for (var index = 0; index < slots.Length; index++) changed[index] = await slots[index].ReadChangedAsync(key, step, quiet);
                for (var index = 0; index < queries.Count; index++)
                {
                    runs[index] = await queries[index].RunAsync(step, quiet);
                    queries[index].Ran(step.Transaction, runs[index]);
                }
                return Array.Empty<StoreWrite>();
            });
            var fence = new CommitFence(() => !Closed && context.AbortSignal?.IsCancellationRequested != true, (_, _) =>
            {
                Batch.Commit(() =>
                {
                    for (var index = 0; index < slots.Length; index++) slots[index].AdoptChanged(key, changed[index]);
                    // Each read ran in the step before COMMIT.
                    for (var index = 0; index < queries.Count; index++) queries[index].Adopt(runs[index]);
                }, quiet);
                for (var index = 0; index < slots.Length; index++)
                    if (changed[index] is ChangedDocument.Failed failed) slots[index].Owner.Fail(failed.Error);
            });
            return await change(new HostCommit(fence, [read]));
        }
        finally { foreach (var release in releases) release(); }
    }

    private static async Task<Action> AcquireLineAsync(Documents.Documents documents, ChordContext context)
    {
        var acquiring = documents.Line.AcquireAsync();
        try
        {
            if (context.AbortSignal is { } signal)
            {
                signal.ThrowIfCancellationRequested();
                return await acquiring.WaitAsync(signal);
            }
            return await acquiring;
        }
        catch
        {
            _ = ReleaseWhenAcquiredAsync();
            throw;
        }
        async Task ReleaseWhenAcquiredAsync() { (await acquiring)(); }
    }
}

internal sealed partial class Self
{
    public IHostKeyedHandle HostKeyedState(StateDefinition definition)
    {
        if (definition.KeyHost is null) throw new ArgumentException("HostKeyedState requires an address-keyed definition");
        return (IHostKeyedHandle)StateCore(definition, true);
    }

    public IWatchable<JsonNode?> Query(ITable table, Query query, long limit)
    {
        Declare("query");
        var label = Attempt.Owner.Label;
        // Plugin code may pass any table: only a handle this attempt declared is taken (its object's own table).
        if (table is not TableHandle handle || !Tables.Contains(handle))
            throw new ArgumentException($"{label}: self.query: the table is not one this factory declared");
        StoreInternal.CheckLiveQuery(handle.Definition, query, limit, label);
        // The owner's window: its value the rows themselves.
        var live = new LiveQuery(new ReadSite(handle.Store, Host.Batch), new QueryOwner(this, handle.Definition.Name), new LiveShape.Rows(new ReadRange(limit)));
        var rows = handle.Definition.Rows;
        // The query is of the handle's table (checked above); every column of the row is read.
        var target = new LiveTarget(new QuerySpec(query, handle.Sql.Table, Projection.All, handle), (stored, context) =>
        {
            var row = new JsonObject();
            foreach (var (column, cell) in rows.Decode(stored, context)) row[column] = cell;
            return row;
        });
        Queries.Add((live, target));
        return live;
    }

    // A failed run of a live query after its object went live: query_failed, once per failure episode (the query is named by its table).
    private sealed class QueryOwner(Self self, string query) : IFramedOwner
    {
        public string Label => self.Attempt.Owner.Label;
        public void Report(Exception error) => self.Report(new("query_failed", error, Object: Label, Query: query));
    }

    // Every read over a table that is created or migrated now waits until it is ready (slice 14 §5): a migration's chunks are not one
    // commit, and a failed one leaves the table held.
    internal void HoldTables()
    {
        var store = Host.Store!;
        foreach (var table in Tables)
            if (store.Table(table.Sql.Table) is not { } recorded || recorded.Version != table.Definition.Version || recorded.State != TableState.Ready)
                store.ReadIndex.Hold(table.Sql.Table);
    }

    // The bindings of column sets follow this attempt's declarations (the committed code, slice 14 §5): each set a table exposes is bound to
    // it, a set its object bound before and no table exposes now is dropped; then the tables are ready, and the reads over them compile
    // again and run once. An attempt that declares no table drops what its object had bound.
    internal void BindSets()
    {
        if (Host.Store?.ReadIndex is not { } reads) return;
        var owner = Attempt.Owner;
        var exposed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var table in Tables)
            {
                if (table.Definition.Exposes?.Name is not { } set) continue;
                exposed.Add(set);
                reads.Bind(set, new Bound(table.Sql.Table, table.Definition), owner);
            }
        }
        catch (ArgumentException error) { throw new BrokenOpen(error); }
        foreach (var set in reads.BoundBy(owner)) if (!exposed.Contains(set)) reads.Unbind(set, owner);
        foreach (var table in Tables) reads.Ready(table.Sql.Table);
    }

    // The first run of each live query, before the object goes live: a failure fails the activation broken.
    internal async Task LoadQueriesAsync()
    {
        foreach (var (live, target) in Queries)
        {
            try { await live.LoadAsync(target, Context); }
            catch (Exception error) when (!Attempt.Sealed && error is not OperationCanceledException) { throw new BrokenOpen(error); }
        }
    }

    // Its live queries end (reads throw the object's error, their watches end; no commit runs them again), and the queries its tables
    // offered are offered no more (slice 14 §3.2): their readers' reads leave the store's index before a successor prepares the tables.
    internal void RetireReads(Exception error)
    {
        foreach (var (live, _) in Queries) live.Detach(error);
        foreach (var table in Tables) table.Store.ReadIndex.Retire(table);
    }
}

internal sealed partial class RecordingSelf
{
    public IHostKeyedHandle HostKeyedState(StateDefinition definition)
    {
        if (definition.KeyHost is null) throw new ArgumentException("HostKeyedState requires an address-keyed definition");
        RecordState(definition, true);
        return new HostKeyed(RecordingValue.Instance, definition.KeyHost);
    }

    public IWatchable<JsonNode?> Query(ITable table, Query query, long limit)
    {
        Check("query");
        if (table is not RecordingTable token || token.Definition is null || !tables.Values.Contains(token.Definition))
            throw new ArgumentException($"{owner.Label}: self.query: the table is not one this factory declared");
        StoreInternal.CheckLiveQuery(token.Definition, query, limit, owner.Label);
        return RecordingValue.Instance;
    }
}

internal sealed partial class Binding
{
    private readonly Dictionary<string, IHostKeyedHandle> hostKeyed = new(StringComparer.Ordinal);

    public IHostKeyedHandle HostKeyedMember(string name) => Host.Dispatcher.Invoke(() =>
    {
        AssertUsable();
        if (Contract.Kind != ContractKind.Local || !ContractsInternal.CurrentVariants(Contract)[ContractsInternal.Local].Keyed.TryGetValue(name, out var snapshot) || snapshot.KeyHost is null)
            throw HostErrors.InvalidMember(name);
        if (!hostKeyed.TryGetValue(name, out var handle))
            hostKeyed.Add(name, handle = new HostKeyed(new ReferenceKeyed(this, name), snapshot.KeyHost));
        return handle;
    });
}
