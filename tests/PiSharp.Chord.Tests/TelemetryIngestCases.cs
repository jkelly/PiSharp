// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry-ingest/recorder.ts, packages/chord/src/telemetry-ingest/sender.ts, packages/chord/src/telemetry-ingest/facet.ts.
// Authored native regression; not an upstream execution capture.
// Authored from Pi 1.5.2 telemetry-ingest tests and the reviewed native port contract; offline only.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Chord.TelemetryIngest;
using PiSharp.Telemetry;

internal static partial class Program
{
    private static StoredSpan IngestSpan(string id, string? trace = null, string? parent = null) =>
        new(id, trace ?? id, parent, "operation", 0, 1, new(), [], [], SpanStatus.Ok);
    private static JsonObject IngestPage(params StoredSpan[] spans) => new() { ["spans"] = JsonSerializer.SerializeToNode(spans) };
    private static JsonObject IngestWorker(IReadOnlyList<StoredSpan> spans, IReadOnlyList<StoredMetric>? metrics = null) => new()
    { ["spans"] = JsonSerializer.SerializeToNode(spans), ["metrics"] = JsonSerializer.SerializeToNode(metrics ?? []) };
    private static IEnumerable<(string, Func<Task>)> TelemetryIngestCases() =>
    [
        Case("telemetry-ingest.recorder.cross-instance-propagation-links-lazy-scope", async () =>
        {
            var reads = 0; var first = new TelemetryRecorder(() => { reads++; return "one"; }, 10);
            var second = new TelemetryRecorder(() => "two", 10);
            Equal(0, reads, "scope is lazy");
            ITelemetrySpan? remembered = null;
            await first.StartSpanAsync("root", async span =>
            {
                remembered = span;
                Check(second.Inject(span) is not null, "all recorders recognize spans");
                var remote = second.Extract(first.Inject(span));
                await remote.StartSpanAsync(new SpanOptions("child") { Links = [new(span)] }, child => Task.FromResult(0));
                return 0;
            });
            await remembered!.StartSpanAsync("too late", _ => Task.FromResult(1));
            var root = first.Take(true).Spans.Single(); var child = second.Take(true).Spans.Single();
            Equal(1, reads, "scope read once"); Equal("one:1", root.Id, "scope id");
            Equal(root.TraceId, child.TraceId, "trace continues"); Equal(root.Id, child.ParentId, "parent crosses adapters");
            Equal(root.Id, child.Links.Single().SpanId, "cross-adapter link copied");
            Equal("too late", await remembered.StartSpanAsync("ignored", _ => Task.FromResult("too late")), "settled child callback executes");
            Check(ReferenceEquals(second, second.Extract(new Dictionary<string, string>())), "unknown carrier falls back to root");
        }),
        Case("telemetry-ingest.recorder.capacity-retained-during-inflight", async () =>
        {
            var recorder = new TelemetryRecorder(() => "cap", 2);
            await recorder.StartSpanAsync("a", _ => Task.FromResult(0));
            await recorder.StartSpanAsync("b", _ => Task.FromResult(0));
            Equal("a", recorder.Take(false, 1).Spans.Single().Name, "take oldest only");
            Equal(1, recorder.QueuedSpans, "queue excludes inflight");
            Equal(0, recorder.Ingest(new([IngestSpan("dropped")], [])), "ingest shares inflight bound");
            await recorder.StartSpanAsync("dropped too", _ => Task.FromResult(0));
            Equal(2L, recorder.DroppedSpans, "both rejected spans counted");
            recorder.Release(); Equal(1, recorder.Ingest(new([IngestSpan("fits")], [])), "release returns capacity");
            Sequence(new[] { "b", "operation" }, recorder.Take(true).Spans.Select(s => s.Name), "remaining order");
        }),
        Case("telemetry-ingest.recorder.metric-merge-order-interval-and-capacity", () =>
        {
            var clock = new IngestClock(); var recorder = new TelemetryRecorder(() => "metrics", 1, clock);
            var counter = recorder.Counter("count");
            counter.Add(2, new() { ["z"] = 1d, ["a"] = "x" });
            counter.Add(3, new() { ["a"] = "x", ["z"] = 1d });
            Check(!recorder.Pending(false) && recorder.Pending(true), "current intervals held unless partial");
            Equal(0, recorder.Take(false).Metrics.Count, "unfinished interval omitted"); recorder.Release();
            clock.Advance(10_000); var row = recorder.Take(false).Metrics.Single();
            Equal(2d, row.Count, "matching series merged"); Equal(5d, row.Sum, "sum merged"); Equal(2d, row.Min, "minimum"); Equal(3d, row.Max, "maximum");
            Sequence(new[] { "a", "z" }, row.Attributes.Keys, "stored attributes canonical");
            counter.Add(1); Equal(1L, recorder.DroppedMeasurements, "inflight metric consumes series capacity");
            recorder.Release(); recorder.Ingest(new([], [row, row with { Sum = 7, Count = 1, Min = 7, Max = 7 }]));
            var merged = recorder.Take(true).Metrics.Single(); Equal(3d, merged.Count, "ingested aggregates merge"); Equal(12d, merged.Sum, "aggregate sum");
            Equal("[\"count\",\"counter\",null,\"{\\\"a\\\":\\\"x\\\",\\\"z\\\":1}\"]", TelemetryRecorder.SeriesKey(row), "portable canonical key");
        }),
        Case("telemetry-ingest.recorder.stop-keeps-or-discards-and-preserves-errors", async () =>
        {
            var recorder = new TelemetryRecorder(() => "stop", 10);
            var error = new InvalidOperationException("same error");
            try { await recorder.StartSpanAsync<int>("failed", _ => throw error); }
            catch (InvalidOperationException actual) { Check(ReferenceEquals(error, actual), "original error rethrown"); }
            recorder.Stop("keep"); var ran = false;
            await recorder.StartSpanAsync("ignored", _ => { ran = true; return Task.FromResult(0); });
            recorder.Counter("ignored").Add(1);
            Check(ran && recorder.Stopped, "stopped spans execute callbacks");
            var saved = recorder.Take(true).Spans.Single(); Check(saved.Status.IsError, "failed span retained"); recorder.Release();
            var discard = new TelemetryRecorder(() => "discard", 2);
            discard.Ingest(new([saved], [])); discard.Stop("discard"); Check(!discard.Pending(true), "discard clears pending data");
        }),
        Case("telemetry-ingest.recorder.microseconds-and-active-stop", async () =>
        {
            var clock = new IngestClock(); var recorder = new TelemetryRecorder(() => "micro", 5, clock);
            await recorder.StartSpanAsync("timed", span =>
            {
                clock.Advance(0.1234); span.AddEvent("submillisecond"); clock.Advance(0.0002);
                return Task.FromResult(0);
            });
            var recorded = recorder.Take(true).Spans.Single(); recorder.Release();
            Equal(0.123d, recorded.Events.Single().TimeMs, "event rounds to microsecond");
            Equal(0.124d, recorded.EndedMs, "end rounds to microsecond");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = recorder.StartSpanAsync("discarded active", async _ => { await release.Task; return 0; });
            recorder.Stop("keep"); release.SetResult(); await active;
            Check(!recorder.Pending(true), "stop suppresses still-running span settlement");
        }),
        Case("telemetry-ingest.sender.unbound-requeues-at-back-and-failure-drops", async () =>
        {
            var recorder = new TelemetryRecorder(() => "send", 2);
            recorder.Ingest(new([IngestSpan("first")], []));
            var sender = new TelemetrySender(recorder, false, _ =>
            {
                recorder.Ingest(new([IngestSpan("second")], []));
                return null;
            });
            await sender.SendAsync();
            Sequence(new[] { "second", "first" }, recorder.Take(true).Spans.Select(s => s.Id), "unbound requeues behind arrivals"); recorder.Release();
            recorder.Ingest(new([IngestSpan("refused")], []));
            var failure = new TelemetrySender(recorder, false, _ => Task.FromException(new InvalidOperationException("refused")));
            await failure.SendAsync(); Check(!recorder.Pending(true), "failed transport gives up batch");
            Equal(2, recorder.Ingest(new([IngestSpan("a"), IngestSpan("b")], [])), "failed send releases capacity");
        }),
        Case("telemetry-ingest.sender.serializes-bounds-payload-and-stops", async () =>
        {
            var recorder = new TelemetryRecorder(() => "send", 3000);
            recorder.Ingest(new(Enumerable.Range(0, 1100).Select(i => IngestSpan("s" + i)).ToArray(),
                Enumerable.Range(0, 1100).Select(i => new StoredMetric(0, "m" + i, TelemetryMetricKind.Counter, null, new(), 1, 1, 1, 1)).ToArray()));
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var released = 0;
            var sender = new TelemetrySender(recorder, true, batch =>
            {
                calls++; Check(batch.Spans.Count <= 1000 && batch.Metrics.Count <= 1000, "worker payload bounded");
                return calls == 1 ? first.Task : Task.CompletedTask;
            }, () => released++);
            var sending = sender.SendAsync(true); var next = sender.SendAsync(true);
            Equal(1, calls, "second send waits"); first.SetResult(); await Task.WhenAll(sending, next);
            Equal(2, calls, "both serialized sends ran"); sender.Stop(); sender.Stop();
            Equal(1, released, "held reference released once"); await sender.SendAsync(true); Equal(2, calls, "stopped sender inert");
            var page = new TelemetryRecorder(() => "page", 5); page.Counter("private").Add(1); page.Ingest(new([IngestSpan("p")], []));
            var pageSender = new TelemetrySender(page, false, batch => { Equal(0, batch.Metrics.Count, "page emits no metrics"); return Task.CompletedTask; });
            await pageSender.SendAsync(true); Check(!page.Pending(true), "taken page metrics discarded");
        }),
        Case("telemetry-ingest.contracts.bound-untrusted-shapes", () =>
        {
            var valid = IngestPage(IngestSpan("browser:p:1"));
            bool Page(JsonObject value) => SchemaCheck(TelemetryIngest.PageBatch, value.ToJsonString());
            Check(Page(valid), "basic page valid");
            var bad = (JsonObject)valid.DeepClone(); bad["spans"]![0]!["id"] = "contains space"; Check(!Page(bad), "id ASCII and no spaces");
            bad = (JsonObject)valid.DeepClone(); bad["spans"]![0]!["name"] = "escape\u001b"; Check(!Page(bad), "control characters refused");
            bad = (JsonObject)valid.DeepClone(); bad["spans"]![0]!["attributes"]!["array"] = new JsonArray(1, 2); Check(!Page(bad), "page arrays refused");
            bad = IngestPage(Enumerable.Repeat(IngestSpan("b"), 101).ToArray()); Check(!Page(bad), "page batch bound");
            var worker = IngestWorker([IngestSpan("w")]); worker["spans"]![0]!["attributes"]!["array"] = new JsonArray("x", 1);
            Check(SchemaCheck(TelemetryIngest.WorkerBatch, worker.ToJsonString()), "worker mixed string-number array allowed");
            worker["spans"]![0]!["attributes"]!["array"] = new JsonArray(true);
            Check(!SchemaCheck(TelemetryIngest.WorkerBatch, worker.ToJsonString()), "worker boolean arrays refused");
            worker = IngestWorker([], [new(0.5, "count", TelemetryMetricKind.Counter, null, new(), 1, 0, 0, 0)]);
            Check(!SchemaCheck(TelemetryIngest.WorkerBatch, worker.ToJsonString()), "metric interval integer required");
        }),
        Case("telemetry-ingest.attributes.mixed-array-lossless-roundtrip", () =>
        {
            const string json = "[\"before\",2,\"after\",-3]";
            var value = JsonSerializer.Deserialize<AttributeValue>(json)!;
            Equal(TelemetryAttributeType.StringNumberArray, value.Type, "mixed wire type represented");
            Equal(json, JsonSerializer.Serialize(value), "JSON preserves value types and order");
            Equal(json, value.ToString(), "rendering preserves mixed array");
            Check(value.Equals(JsonSerializer.Deserialize<AttributeValue>(json)), "mixed array structural equality");
            Equal(value.GetHashCode(), JsonSerializer.Deserialize<AttributeValue>(json)!.GetHashCode(), "equal mixed arrays hash equally");
            Equal(4, value.Elements().Count(), "mixed array elements exposed in order");
            var source = new AttributeValue[] { "original", 2d };
            var copied = AttributeValue.FromStringNumberArray(source); source[0] = "changed";
            Equal("[\"original\",2]", copied.ToString(), "factory copies mutable input");
            Equal(TelemetryAttributeType.StringArray, JsonSerializer.Deserialize<AttributeValue>("[\"x\"]")!.Type, "homogeneous strings unchanged");
            Equal(TelemetryAttributeType.NumberArray, JsonSerializer.Deserialize<AttributeValue>("[1]")!.Type, "homogeneous numbers unchanged");
            Equal(TelemetryAttributeType.BooleanArray, JsonSerializer.Deserialize<AttributeValue>("[true]")!.Type, "homogeneous booleans unchanged");
            Throws<JsonException>(() => JsonSerializer.Deserialize<AttributeValue>("[true,1]"), "mixed booleans still refused");
        }),
        Case("telemetry-ingest.attributes.mixed-array-definition-closed-sets", () =>
        {
            TelemetrySchemaDefinition Schema(TelemetryAttributeDefinition attribute) => new(1, new Dictionary<string, TelemetrySpanDefinition>())
            {
                Metrics = new Dictionary<string, TelemetryMetricDefinition>
                {
                    ["m"] = new(TelemetryMetricKind.Counter, "measured", new Dictionary<string, TelemetryAttributeDefinition> { ["tags"] = attribute })
                }
            };
            var definition = new TelemetryAttributeDefinition(TelemetryAttributeType.StringNumberArray, "mixed values") { ElementValues = ["x", 2d] };
            var schema = TelemetrySchema.Define(Schema(definition));
            var recorder = new TelemetryRecorder(() => "mixed", 10);
            var counter = TelemetrySchema.CreateTypedMetrics(recorder, schema).Counter("m");
            counter.Add(1, new() { ["tags"] = AttributeValue.FromStringNumberArray(["x", 2d]) });
            counter.Add(1, new() { ["tags"] = new[] { "x" } });
            counter.Add(1, new() { ["tags"] = new[] { 2d } });
            Throws<ArgumentException>(() => counter.Add(1, new() { ["tags"] = new[] { "y" } }), "element outside closed set refused");
            Throws<ArgumentException>(() => counter.Add(1, new() { ["tags"] = new[] { true } }), "boolean array not accepted by union");
            Throws<ArgumentException>(() => TelemetrySchema.Define(Schema(definition with { ElementValues = [true] })), "closed set boolean refused");
            Throws<ArgumentException>(() => TelemetrySchema.Define(Schema(definition with { ElementValues = null, Values = ["x"] })), "scalar closed set refused for array");
            var rows = recorder.Take(true).Metrics;
            Sequence(new[] { TelemetryAttributeType.StringNumberArray, TelemetryAttributeType.StringArray, TelemetryAttributeType.NumberArray },
                rows.Select(row => row.Attributes["tags"]!.Type), "accepted arrays retain actual types without coercion");
            var homogeneous = TelemetrySchema.CreateTypedMetrics(recorder, Schema(new(TelemetryAttributeType.StringArray, "strings"))).Counter("m");
            Throws<ArgumentException>(() => homogeneous.Add(1, new() { ["tags"] = AttributeValue.FromStringNumberArray(["x", 2d]) }), "existing homogeneous definition stays strict");
        }),
        Case("telemetry-ingest.facet.page-spoof-stamp-rate-and-reset", async () =>
        {
            var recorder = new TelemetryRecorder(() => "server", 5000); var clock = new IngestClock(); var hooks = 0;
            await using var host = Hosts.Create(new() { Address = new HostKind("server").Address(), Principal = new() { ["kind"] = "user" }, OnError = _ => { } });
            await host.LoadAsync([new("telemetry", TelemetryIngest.CreateFacet(new()
            {
                Target = recorder, WorkerHost = _ => null, TimeProvider = clock,
                PrincipalAttributes = _ => { hooks++; return new() { ["chord.principal"] = "trusted" }; }
            }))], ChordContext.Background);
            using var reference = await host.GetAsync(TelemetryIngest.PageTelemetry, new(), ChordContext.Background);
            var own = "browser:" + host.Incarnation + ":";
            var span = IngestSpan(own + "1") with { Attributes = new() { ["chord.host"] = "spoof", ["chord.private"] = "spoof", ["chord.principal"] = "spoof", ["custom"] = 7d } };
            var receipt = (JsonObject)(await reference.CallAsync("ingest", IngestPage(span, IngestSpan("spoof"), span with { ParentId = "other" }, span with { EndedMs = -0d, StartedMs = 1 }), ChordContext.Background))!;
            Equal(1, receipt["written"]!.GetValue<int>(), "only own valid interval queued"); Equal(3, receipt["dropped"]!.GetValue<int>(), "spoofs in receipt");
            var accepted = recorder.Take(true).Spans.Single(); recorder.Release();
            Equal("browser", accepted.Attributes["chord.host"]!.GetString(), "host stamped"); Equal("trusted", accepted.Attributes["chord.principal"]!.GetString(), "principal stamped");
            Check(!accepted.Attributes.ContainsKey("chord.private"), "all user chord attributes stripped"); Equal(7d, accepted.Attributes["custom"]!.GetNumber(), "user attribute retained");
            for (var i = 0; i < 12; i++) await reference.CallAsync("ingest", IngestPage(Enumerable.Repeat(span, 100).ToArray()), ChordContext.Background);
            Equal(1200, hooks, "hook invoked per accepted span only");
            receipt = (JsonObject)(await reference.CallAsync("ingest", IngestPage(span), ChordContext.Background))!;
            Equal(0, receipt["written"]!.GetValue<int>(), "minute exhausted");
            clock.Advance(60_000); receipt = (JsonObject)(await reference.CallAsync("ingest", IngestPage(span), ChordContext.Background))!;
            Equal(1, receipt["written"]!.GetValue<int>(), "minute budget resets");
        }),
        Case("telemetry-ingest.facet.worker-authorized-variant-stamps-host-and-forwards-metrics", async () =>
        {
            var recorder = new TelemetryRecorder(() => "server", 10);
            await using var host = Hosts.Create(new() { Address = new HostKind("server").Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
            var loaded = await host.LoadAsync([new("telemetry", TelemetryIngest.CreateFacet(new()
            {
                Target = recorder, WorkerHost = p => p?["kind"]?.GetValue<string>() == "worker" ? "worker|7" : null,
                PrincipalAttributes = _ => new()
            }))], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "worker ingest facet served");
            using var other = await host.GetAsync(TelemetryIngest.WorkerTelemetry, new(), ChordContext.Background);
            Equal("other", other.Variant, "nonworker cannot ingest");
            await host.RootAsync(async ctx =>
            {
                var worker = await ctx.GetAsync(TelemetryIngest.WorkerTelemetry, new()); Equal("worker", worker.Variant, "worker admitted");
                var span = IngestSpan("worker:1") with { Attributes = new() { ["chord.host"] = "forged", ["custom"] = true } };
                var arguments = IngestWorker([span], [new(0, "count", TelemetryMetricKind.Counter, null, new(), 2, 3, 1, 2)]);
                arguments["spans"]![0]!["attributes"]!["mixed"] = new JsonArray("first", 2);
                await worker.CallAsync("ingest", arguments, ctx.Context);
                return 0;
            }, ChordContext.Background, new() { Principal = new() { ["kind"] = "worker" } });
            var batch = recorder.Take(true); Equal("worker|7", batch.Spans.Single().Attributes["chord.host"]!.GetString(), "worker host verified");
            Equal(3d, batch.Metrics.Single().Sum, "worker metrics forwarded");
            Equal("[\"first\",2]", batch.Spans.Single().Attributes["mixed"]!.ToString(), "schema-valid mixed array preserved");
        }),
        Case("telemetry-ingest.facet.page-budgets-evict-oldest-and-reset", () =>
        {
            var budgets = new TelemetryIngest.PageBudgets(); var oldest = budgets.Of("old", 0); oldest.Spans = 1200;
            for (var i = 0; i < 999; i++) budgets.Of("page" + i, 1);
            Check(ReferenceEquals(oldest, budgets.Of("old", 2)), "access preserves original insertion order");
            budgets.Of("new", 3);
            Equal(0, budgets.Of("old", 4).Spans, "1001st page evicts oldest budget");
            var page = budgets.Of("reset", 100); page.Spans = 7;
            Equal(7, budgets.Of("reset", 60_099).Spans, "minute boundary anchored to first page request");
            Equal(0, budgets.Of("reset", 60_100).Spans, "minute resets at exact boundary");
        }),
        Case("telemetry-ingest.sender.page-through-memory-link-no-ingest-feedback", async () =>
        {
            var stored = new TelemetryRecorder(() => "server", 100); var serverTelemetry = new InMemoryTelemetryContext();
            await using var server = Hosts.Create(new() { Address = new HostKind("server").Address(), Principal = new() { ["kind"] = "server" }, Telemetry = serverTelemetry, OnError = _ => { } });
            var loaded = await server.LoadAsync([new("telemetry", TelemetryIngest.CreateFacet(new()
            {
                Target = stored, WorkerHost = _ => null, PrincipalAttributes = _ => new()
            }))], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "page ingest facet served");
            await using var client = Hosts.Create(new() { OnError = _ => { } }); await client.LoadAsync([], ChordContext.Background);
            var page = new TelemetryRecorder(() => "browser:" + client.Incarnation, 10);
            var sender = TelemetrySenders.SendPageTelemetry(client, page, ChordContext.Background);
            await page.StartSpanAsync("page.click", _ => Task.FromResult(0)); page.Counter("click").Add(1);
            await sender.SendAsync(true); Equal(1, page.QueuedSpans, "unbound actual host retains span");
            var pair = MemoryLinkPair.Create();
            server.Accept(pair.First, new() { Principal = new() { ["kind"] = "user" } }, ChordContext.Background);
            client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
            using var ready = await client.GetAsync(TelemetryIngest.PageTelemetry, new(), ChordContext.Background);
            await sender.SendAsync(true);
            var batch = stored.Take(true); Equal("page.click", batch.Spans.Single().Name, "real sender delivers page batch");
            Equal(0, batch.Metrics.Count, "real page sender emits no metrics");
            Check(!serverTelemetry.GetSpans().Any(span => span.Name == "chord.method"), "untraced ingest creates no method span");
            sender.Stop();
        }),
        Case("telemetry-ingest.sender.stop-releases-pending-remote-ingest", async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = Hosts.Create(new() { Address = new HostKind("server").Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
            var loaded = await server.LoadAsync([new("telemetry", Facet.Define((host, _) => host.Serve(TelemetryIngest.WorkerTelemetry,
                (_, _, _) => new(new Dictionary<string, object?>
                {
                    ["ingest"] = (HostMethod)(async (_, ctx) =>
                    {
                        started.TrySetResult();
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.AbortSignal); }
                        finally { aborted.TrySetResult(); }
                        return new JsonObject { ["written"] = 1, ["dropped"] = 0 };
                    })
                }, "worker"), new(Untraced: true))))], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "pending ingest facet served");
            await using var client = Hosts.Create(new() { OnError = _ => { } }); await client.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create();
            server.Accept(pair.First, new() { Principal = new() { ["kind"] = "worker" } }, ChordContext.Background);
            client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
            var recorder = new TelemetryRecorder(() => "worker", 1);
            var sender = TelemetrySenders.SendWorkerTelemetry(client, recorder, ChordContext.Background);
            using (await client.GetAsync(TelemetryIngest.WorkerTelemetry, new(), ChordContext.Background)) { }
            recorder.Ingest(new([IngestSpan("worker:1")], []));
            var sending = sender.SendAsync(true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            sender.Stop();
            await sending.WaitAsync(TimeSpan.FromSeconds(2));
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(1, recorder.Ingest(new([IngestSpan("worker:2")], [])), "stopped remote call releases taken capacity");
        }),
    ];
    private sealed class IngestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(double milliseconds) => ticks += (long)(milliseconds * TimeSpan.TicksPerMillisecond);
    }
}
