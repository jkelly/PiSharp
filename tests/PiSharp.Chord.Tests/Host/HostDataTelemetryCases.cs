// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/host/host-data.ts, packages/chord/src/telemetry.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Store;
using PiSharp.Telemetry;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostDataTelemetryCases() =>
    [
        Case("host.host-data.telemetry-counts-committed-documents-ops-rows-and-skips-noop", async () =>
        {
            await using var store = await MemoryStore();
            var hostTelemetry = new InMemoryTelemetryContext();
            var callerTelemetry = new InMemoryTelemetryContext();
            var definition = StateDefinition.Define("value", new()
            {
                Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 },
            });
            var tableDefinition = Tables.DefineTable("rows", new()
            {
                Version = 1, Columns = new() { ["id"] = Str(), ["n"] = Int() }, Key = ["id"],
            });
            var type = Contract.Local("host.commit", new() { Version = 1, Params = new(), Members = new() { ["value"] = definition.Schema } });
            IStoreSelf? self = null;
            ISingletonHandle? value = null;
            ITable? table = null;
            await using var host = Hosts.Create(new() { Store = store, Telemetry = hostTelemetry, OnError = _ => { } });
            await host.LoadAsync([new("commit", Facet.Define((facet, _) => facet.Serve(type, (_, _, owner) =>
            {
                self = (IStoreSelf)owner;
                value = owner.State(definition);
                table = self.Table(tableDefinition);
                return new(new Dictionary<string, object?> { ["value"] = value });
            })))], ChordContext.Background);
            await callerTelemetry.StartSpanAsync(new SpanOptions("test.commit"), async span =>
            {
                var context = ChordContext.WithTelemetry(span, ChordContext.Background);
                await self!.TxAsync(async tx => { _ = await tx.DraftAsync(value!); return true; }, context);
                await self.TxAsync(async tx =>
                {
                    (await tx.DraftAsync(value!))["n"] = 1;
                    tx.Put(table!, new JsonObject { ["id"] = "a", ["n"] = 1 });
                    tx.Put(table!, new JsonObject { ["id"] = "b", ["n"] = 2 });
                    return true;
                }, context);
                return true;
            });
            var events = callerTelemetry.GetSpans().Single().Events;
            Equal(1, events.Count, "no-op records no commit event");
            Equal("chord.commit", events[0].Name, "commit event name");
            Equal(1d, events[0].Attributes["documents"]!.GetNumber(), "adopted document count");
            Equal(1d, events[0].Attributes["ops"]!.GetNumber(), "prepared op count");
            Equal(2d, events[0].Attributes["rows"]!.GetNumber(), "actual changed row count");
            Check(events[0].Attributes["duration"]!.GetNumber() >= 0, "nonnegative elapsed duration");
            Equal(1, hostTelemetry.GetMetrics().Count(metric => metric.Name == "chord.commit.duration"), "host owns duration metric");
            Equal(0, callerTelemetry.GetMetrics().Count(metric => metric.Name == "chord.commit.duration"), "caller does not own host duration metric");
            await self!.TxAsync(async tx => { (await tx.DraftAsync(value!))["n"] = 3; return true; }, ChordContext.Background);
            Equal(2, hostTelemetry.GetMetrics().Count(metric => metric.Name == "chord.commit.duration"), "untraced caller still records host duration");
        }),
    ];
}
