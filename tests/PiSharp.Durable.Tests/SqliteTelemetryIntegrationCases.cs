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
        yield return Case("telemetry-sqlite.native-read-only-file-and-connection-ownership", async () =>
        {
            var directory = Temp("telemetry-read-only");
            var path = Path.Combine(directory, "telemetry.sqlite");
            var native = await SqliteConnectionDatabase.OpenAsync(path);
            var recorder = await SqliteTelemetry.CreateAsync(new StoreSqliteDatabase(native));
            try
            {
                await recorder.StartSpanAsync("before", _ => 1); await recorder.FlushAsync();
                var raw = await SqliteConnectionDatabase.OpenAsync(path, new() { OpenMode = SqliteDatabaseOpenMode.ReadOnly });
                await ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => raw.ExecAsync("CREATE TABLE forbidden (id INTEGER)"), "native connection enforces read-only");
                await raw.CloseAsync();
                await using (var reader = await NativeSqliteTelemetry.OpenReaderAsync(path))
                    Equal(1, (await reader.FindTracesAsync()).Traces.Count, "read-only transaction can query live writer");
                await recorder.StartSpanAsync("after", _ => 2); await recorder.FlushAsync();
                Equal(2, (await recorder.FindTracesAsync()).Traces.Count, "reader close leaves recorder connection open");
                await recorder.CloseAsync(); await native.CloseAsync();
                var before = await File.ReadAllBytesAsync(path);
                await using (var reader = await NativeSqliteTelemetry.OpenReaderAsync(path))
                    Equal(2, (await reader.FindTracesAsync()).Traces.Count, "closed writer file remains readable");
                var after = await File.ReadAllBytesAsync(path);
                Check(before.SequenceEqual(after), "reader never rewrites the database file");
            }
            finally
            {
                await recorder.CloseAsync(); await native.CloseAsync();
                Directory.Delete(directory, true);
            }
        });

        yield return Case("telemetry-sqlite.native-read-only-missing-old-newer", async () =>
        {
            var directory = Temp("telemetry-schema-check");
            try
            {
                var missingDirectory = Path.Combine(directory, "missing");
                await ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => NativeSqliteTelemetry.OpenReaderAsync(Path.Combine(missingDirectory, "missing.sqlite")), "missing file refused");
                Check(!Directory.Exists(missingDirectory), "read-only open never creates directories");
                foreach (var version in new[] { 0, 1, 3 })
                {
                    var path = Path.Combine(directory, version + ".sqlite");
                    var db = await SqliteConnectionDatabase.OpenAsync(path);
                    if (version != 0)
                    {
                        await db.ExecAsync("CREATE TABLE telemetry_schema (singleton INTEGER PRIMARY KEY, version INTEGER)");
                        await db.RunAsync("INSERT INTO telemetry_schema VALUES (1, ?)", version);
                    }
                    await db.CloseAsync();
                    var before = await File.ReadAllBytesAsync(path);
                    await ThrowsAsync<InvalidOperationException>(() => NativeSqliteTelemetry.OpenReaderAsync(path), "missing, old and future telemetry schemas refused");
                    var after = await File.ReadAllBytesAsync(path);
                    Check(before.SequenceEqual(after), "failed reader leaves file bytes unchanged");
                    File.Move(path, path + ".closed");
                }
            }
            finally { Directory.Delete(directory, true); }
        });

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
                var root = trace.Spans.Single(span => span.Name == "host.read");
                var method = trace.Spans.Single(span => span.Name == "chord.method");
                Check(root.Status == SpanStatus.Ok && method.Status == SpanStatus.Ok, "host call and provider method recorded");
                Equal(root.Id, method.ParentId, "local provider method continues the caller's span");
                Equal("read", method.Attributes["member"]!.GetString(), "provider method identifies the member");
                Equal(value, (string)(await native.GetAsync("SELECT value FROM business"))!["value"]!, "closing recorder and host leaves database open");
                Check(trace.Spans.All(span => !span.Attributes.ToString().Contains(value, StringComparison.Ordinal)), "business SQL values are never automatically captured");
            }
            finally { await native.CloseAsync(); }
        });
    }
}
