// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/host/host-data.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostDataCases() =>
    [
        Case("host.host-data.gate-drains-readers-and-excludes-new-loads", async () =>
        {
            var gate = new HostDataGate();
            Check(gate.Enter() is null, "first load enters synchronously");
            var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = gate.TurnAsync(turn => turn.ExclusiveAsync(async () =>
            {
                writerEntered.SetResult();
                await releaseWriter.Task;
                return 1;
            }), ChordContext.Background);
            var nextReader = gate.Enter();
            Check(nextReader is not null && !nextReader.IsCompleted && !writerEntered.Task.IsCompleted, "writer waits old reader and new reader waits writer");
            gate.Leave();
            await writerEntered.Task;
            Check(!nextReader!.IsCompleted, "new reader still excluded");
            releaseWriter.SetResult();
            Equal(1, await writer, "writer result");
            await nextReader;
            gate.Leave();
        }),
        Case("host.host-data.cancelled-queued-turn-does-not-overtake-predecessor", async () =>
        {
            var gate = new HostDataGate();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = gate.TurnAsync(async _ => { await release.Task; return 1; }, ChordContext.Background);
            using var cancellation = new CancellationTokenSource();
            var cancelled = gate.TurnAsync(_ => Task.FromResult(2), ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background));
            cancellation.Cancel();
            var thirdEntered = false;
            var third = gate.TurnAsync(_ => { thirdEntered = true; return Task.FromResult(3); }, ChordContext.Background);
            try { await cancelled; throw new InvalidOperationException("expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(!thirdEntered, "later turn remains behind first");
            release.SetResult();
            Equal(1, await first, "first result");
            Equal(3, await third, "third result");
        }),
        Case("host.host-data.import-replace-delete-refresh-resident-pinned-and-query-data", async () =>
        {
            await using var store = await MemoryStore();
            var tenant = new HostKind("tenant", new() { ["id"] = Str() });
            var a = tenant.Address(("id", "a"));
            var b = tenant.Address(("id", "b"));
            var c = tenant.Address(("id", "c"));
            var definition = StateDefinition.Define("values", new()
            {
                Version = 1, KeyHost = tenant, Persist = true, CheckpointWhen = (_, _, _) => false,
                Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 },
            });
            var tableDefinition = Tables.DefineTable("rows", new()
            {
                Version = 1, Columns = new() { ["host"] = Tables.Host(tenant), ["id"] = Str(), ["n"] = Int() }, Key = ["host", "id"],
            });
            var type = Contract.Local("host.data", new()
            {
                Version = 1, Params = new(), Members = new() { ["values"] = Keyed(tenant, Obj(("n", Int()))), ["a"] = Obj(("n", Int())), ["c"] = Obj(("n", Int())), ["rows"] = Arr(Unknown()) },
            });
            IStoreSelf? self = null;
            IHostKeyedHandle? values = null;
            ITable? table = null;
            IWatchable<JsonNode?>? query = null;
            await using var host = Hosts.Create(new() { Store = store, OnError = _ => { } });
            await host.LoadAsync([new("host-data", Facet.Define((facet, _) => facet.Serve(type, (_, _, owner) =>
            {
                self = (IStoreSelf)owner;
                values = owner.HostKeyedState(definition);
                table = self.Table(tableDefinition);
                query = self.Query(table, Tables.Query(tableDefinition), 20);
                return new(new Dictionary<string, object?> { ["values"] = values, ["a"] = values.At(a), ["c"] = values.At(c), ["rows"] = query });
            })))], ChordContext.Background);
            using var reference = await host.GetAsync(type, new(), ChordContext.Background);
            async Task Put(Address address, int number) => await self!.TxAsync(async tx =>
            {
                (await tx.DraftAsync(values!, address))["n"] = number;
                tx.Put(table!, new JsonObject { ["host"] = address.ToJson(), ["id"] = "one", ["n"] = number });
                return true;
            }, ChordContext.Background);
            await Put(a, 1);
            await Put(b, 2);
            var archive = new List<JsonNode?>();
            await foreach (var part in host.ExportHostData(a, ChordContext.Background)) archive.Add(part);
            await Put(a, 9);
            var refused = await Rejection(host.ImportHostDataAsync(archive, ChordContext.Background));
            Rejected(refused, "invalid", "args", "existing default refuses");
            Equal(9, reference.Member("a").Value!["n"]!.GetValue<int>(), "refusal leaves live value unchanged");
            var replaced = await host.ImportHostDataAsync(archive, ChordContext.Background, new() { Existing = HostExisting.Replace });
            Equal(1L, replaced.Documents, "replaced document count");
            Equal(1L, replaced.Rows, "replaced row count");
            Equal(1, reference.Member("a").Value!["n"]!.GetValue<int>(), "resident document updated");
            Equal(2, (await reference.HostKeyedMember("values").GetAsync(b, ChordContext.Background))!["n"]!.GetValue<int>(), "other address isolated");
            Check(((JsonArray)query!.Value!).Any(row => ChordJson.AsNumber(row!["n"]) == 1), "query refreshed after replace");
            await host.ImportHostDataAsync(archive, ChordContext.Background, new() { Address = c });
            Equal(1, reference.Member("c").Value!["n"]!.GetValue<int>(), "cold pinned absence becomes imported document");
            var removed = await host.DeleteHostDataAsync(a, ChordContext.Background);
            Equal(1L, removed.Documents, "deleted document count");
            Equal(1L, removed.Rows, "deleted row count");
            Equal(0, reference.Member("a").Value!["n"]!.GetValue<int>(), "resident retirement reads initial");
            Equal(2, ((JsonArray)query.Value!).Count, "query drops only deleted address");
            var keys = new List<string>();
            await foreach (var address in values!.Keys(ChordContext.Background)) keys.Add(address.Key);
            Sequence(new[] { b.Key, c.Key }, keys, "host keys decoded and ordered");
        }),
        Case("host.host-data.cancelled-uncommitted-transaction-reports-once", async () =>
        {
            var errors = new List<HostError>();
            var definition = StateDefinition.Define("value", new() { Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 } });
            var type = Contract.Local("host.cancel-tx", new() { Version = 1, Params = new(), Members = new() { ["value"] = definition.Schema } });
            ISelf? self = null;
            ISingletonHandle? value = null;
            await using var host = Hosts.Create(new() { OnError = error => errors.Add(error) });
            await host.LoadAsync([new("cancel-tx", Facet.Define((facet, _) => facet.Serve(type, (_, _, owner) =>
            { self = owner; value = owner.State(definition); return new(new Dictionary<string, object?> { ["value"] = value }); })))], ChordContext.Background);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var transaction = self!.TxAsync(async tx =>
            {
                (await tx.DraftAsync(value!))["n"] = 7;
                entered.SetResult();
                await finish.Task;
                return true;
            }, ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background));
            await entered.Task;
            cancellation.Cancel();
            finish.SetResult();
            _ = await Rejection(transaction);
            Equal(0, value!.Value!["n"]!.GetValue<int>(), "cancelled draft discarded");
            Equal(1, errors.Count(error => error.Kind == "transaction_cancelled"), "reported once");
            Check(errors.Single(error => error.Kind == "transaction_cancelled").Error is OperationCanceledException,
                "diagnostic reports the context cancellation");
        }),
    ];
}
