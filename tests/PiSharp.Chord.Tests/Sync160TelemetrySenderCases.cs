// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/telemetry-ingest/sender.test.ts, packages/chord/src/telemetry-ingest/sender.ts.
// The 1.6.0 delta of the sender cases: the client records to the recorder it sends from, so the sender's own hold (shared work, a
// chord.hold root on the client's adapter) is sent with the rest. "transported()" is a bounded wait for the first batch to land.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.TelemetryIngest;

internal static partial class Program
{
    private static async Task<(List<TelemetryBatch> Batches, TelemetryRecorder Recorder, ChordHost Client, ChordHost Server, Action Connect)> Sync160Sending(
        JsonObject principal, Func<string, string> scope)
    {
        var batches = new List<TelemetryBatch>();
        var server = Hosts.Create(new() { Address = new HostKind("server").Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
        var loaded = await server.LoadAsync([new("chord.telemetry", TelemetryIngest.CreateFacet(new()
        {
            Target = new IngestBatches(batches), Admit = _ => true,
            WorkerHost = who => who?["kind"]?.GetValue<string>() == "worker" ? "project|p1" : null, PrincipalAttributes = _ => new()
        }))], ChordContext.Background);
        Equal(0, loaded.Failed.Count, "ingest facet served");
        var incarnation = "";
        var recorder = new TelemetryRecorder(() => scope(incarnation), 100);
        var client = Hosts.Create(new() { Telemetry = recorder, OnError = _ => { } });
        incarnation = client.Incarnation;
        await client.LoadAsync([], ChordContext.Background);
        void Connect()
        {
            var pair = MemoryLinkPair.Create();
            server.Accept(pair.First, new() { Principal = principal }, ChordContext.Background);
            client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
        }
        return (batches, recorder, client, server, Connect);
    }

    /// <summary>Sends until a batch lands (the object binds after the link settles), at most five seconds.</summary>
    private static async Task Sync160SendUntilDelivered(TelemetrySender sender, List<TelemetryBatch> batches)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (batches.Count == 0 && DateTime.UtcNow < deadline) { await sender.SendAsync(true); if (batches.Count == 0) await Task.Delay(10); }
        Check(batches.Count > 0, "a batch was delivered");
    }

    private static IEnumerable<(string, Func<Task>)> Sync160TelemetrySenderCases()
    {
        yield return Case("telemetry-ingest.sender.160.page-sends-its-own-hold-with-its-spans", async () =>
        {
            var (batches, recorder, client, server, connect) = await Sync160Sending(new() { ["kind"] = "owner" }, incarnation => "browser:" + incarnation);
            await using var _server = server; await using var _client = client;
            var sender = TelemetrySenders.SendPageTelemetry(client, recorder, ChordContext.Background);
            await recorder.StartSpanAsync("page.click", _ => Task.FromResult(0));
            recorder.Counter("clicks").Add(1);
            await sender.SendAsync(true);
            Equal(0, batches.Count, "nothing is sent while unbound");
            connect();
            await Sync160SendUntilDelivered(sender, batches);
            Equal(1, batches.Count, "one batch");
            Equal("page.click,chord.hold", string.Join(",", batches[0].Spans.Select(span => span.Name)), "the page's span and the sender's own hold");
            Equal(0, batches[0].Metrics.Count, "a page sends no metrics");
            Equal(0, recorder.QueuedSpans, "nothing left queued");
            await sender.SendAsync(true);
            Equal(1, batches.Count, "nothing waits: no call");
            sender.Stop();
        });
        yield return Case("telemetry-ingest.sender.160.worker-sends-its-own-hold-with-its-spans", async () =>
        {
            var (batches, recorder, client, server, connect) = await Sync160Sending(new() { ["kind"] = "worker" }, _ => "w");
            await using var _server = server; await using var _client = client;
            connect();
            var sender = TelemetrySenders.SendWorkerTelemetry(client, recorder, ChordContext.Background);
            // transported(): the sender's hold binds (its chord.hold root ends) before the job runs.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (recorder.QueuedSpans == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            Equal(1, recorder.QueuedSpans, "the bound hold's span is queued");
            await recorder.StartSpanAsync("worker.job", _ => Task.FromResult(0));
            recorder.Counter("runs").Add(2);
            await Sync160SendUntilDelivered(sender, batches);
            Equal("chord.hold,worker.job", string.Join(",", batches[0].Spans.Select(span => span.Name)), "the sender's own hold, then the job");
            Equal(2d, batches[0].Metrics.Single(metric => metric.Name == "runs").Sum, "the worker's metrics");
            sender.Stop();
        });
    }
}
