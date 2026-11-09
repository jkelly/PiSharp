// Native C# integration of chord/telemetry-sqlite with the existing Durable SQLite facade and a real Chord host.
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.TelemetrySqlite;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Store;
using PiSharp.Telemetry;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SqliteTelemetryIntegrationCases()
    {
        yield return Case("telemetry-sqlite.native-host-binding", async () =>
        {
            var native = await SqliteConnectionDatabase.OpenAsync(":memory:");
            var db = new StoreSqliteDatabase(native);
            try
            {
                await db.ExecAsync("CREATE TABLE business (value TEXT)");
                const string value = "private ' ? value";
                await db.RunAsync("INSERT INTO business VALUES (?)", value);
                await using var telemetry = await SqliteTelemetry.CreateAsync(db);
                var type = Contract.Local("telemetry.integration", new()
                { Version = 1, Params = new(), Members = new() { ["read"] = Method(null, PiSharp.Chord.Contracts.Schemas.Str()) } });
                await using (var host = Hosts.Create(new() { Telemetry = telemetry, OnError = error => throw new InvalidOperationException(error.Kind) }))
                {
                    await host.LoadAsync([new("telemetry.integration", Facet.Define((facet, _) => facet.Serve(type, (_, _, _) =>
                        new(new Dictionary<string, object?>
                        {
                            ["read"] = (HostMethod)(async (_, _) => (await db.GetAsync("SELECT value FROM business"))!["value"])
                        }))))], ChordContext.Background);
                    using var reference = await host.GetAsync(type, new(), ChordContext.Background);
                    var result = await telemetry.StartSpanAsync("host.read", span => reference.CallAsync("read", null,
                        ChordContext.WithTelemetry(span, ChordContext.Background)));
                    Equal(value, (string)result!, "native SQL result passes through real host unchanged");
                }
                await telemetry.CloseAsync();
                var trace = (await telemetry.FindTracesAsync(new() { Name = "host.read" })).Traces.Single();
                Check(trace.Spans.Single().Status == SpanStatus.Ok, "host call recorded");
                Equal(value, (string)(await native.GetAsync("SELECT value FROM business"))!["value"]!, "closing recorder and host leaves database open");
                Check(!trace.Spans.Single().Attributes.ToString().Contains(value, StringComparison.Ordinal), "business SQL values are never automatically captured");
            }
            finally { await native.CloseAsync(); }
        });
    }
}
