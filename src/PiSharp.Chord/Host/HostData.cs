// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/host/host-data.ts
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

    /// <summary>Deletes data owned by an address and refreshes its live observers in the commit batch.</summary>
    public Task<HostDataCounts> DeleteHostDataAsync(Address address, ChordContext context) => Dispatcher.RunAsync(async () =>
    {
        var store = HostDataStore("deleteHostData", context);
        ArgumentNullException.ThrowIfNull(address);
        var key = StoreInternal.StoredText(address.Key, "deleteHostData: address");
        return await HostDataGate.TurnAsync(turn => turn.ExclusiveAsync(async () =>
        {
            var owners = await StoreInternal.HostDataOwnersAsync(store, key, null, context);
            return await CoordinatedHostDataAsync(owners, key, context,
                commit => StoreInternal.DeleteHostDataAsync(store, key, commit, ChordContext.WithoutAbortSignal(context)));
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
            var queries = new QueryRefresh(objects.SelectMany(owner => owner.LiveQueries.OnHostKind(kind)), quiet);
            var read = new StagedWrite(WriteOrder.Write, async step =>
            {
                for (var index = 0; index < slots.Length; index++) changed[index] = await slots[index].ReadChangedAsync(key, step, quiet);
                return Array.Empty<StoreWrite>();
            });
            var fence = new CommitFence(() => !Closed && context.AbortSignal?.IsCancellationRequested != true, (_, _) =>
            {
                Batch.Commit(() =>
                {
                    for (var index = 0; index < slots.Length; index++) slots[index].AdoptChanged(key, changed[index]);
                    queries.Adopt();
                }, quiet);
                for (var index = 0; index < slots.Length; index++)
                    if (changed[index] is ChangedDocument.Failed failed) slots[index].Owner.Fail(failed.Error);
            });
            return await change(new HostCommit(fence, [read, queries.Write]));
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
        if (table is not TableHandle handle || !Tables.Contains(handle))
            throw DataInternal.Invalid("args", $"{Attempt.Owner.Label}: self.query takes its own declared table");
        var live = new LiveQuery(handle, query, limit, new QueryOwner(this, handle.Definition.Name), Host.Batch);
        Queries.Add(live);
        Attempt.Owner.LiveQueries.Add(live);
        return live;
    }

    private sealed class QueryOwner(Self self, string table) : IQueryOwner
    {
        public string Label => self.Attempt.Owner.Label;
        public void Report(Exception error) => self.Report(new("query_failed", error, Object: Label, Table: table));
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
            throw DataInternal.Invalid("args", "self.query takes its own declared table");
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
