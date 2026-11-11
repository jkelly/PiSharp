using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Telemetry;

// The pi-telemetry package: packages/telemetry/test/telemetry.test.ts, metrics.test.ts, propagation.test.ts, render.test.ts, store.test.ts
// and store-conformance.ts at Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT). Object.isFrozen has no C# counterpart (the no-op span and
// instruments hold no state); the remaining expectations are ported, compile-time ones as the typed schemas' ArgumentException.
internal static partial class Program
{
    private static Dictionary<string, TelemetryAttributeDefinition> Attributes(params (string Name, TelemetryAttributeDefinition Definition)[] items) =>
        items.ToDictionary(item => item.Name, item => item.Definition, StringComparer.Ordinal);

    private static TelemetryAttributeDefinition Attribute(TelemetryAttributeType type, string description, bool? required = null,
        params AttributeValue[] values) =>
        new(type, description) { Required = required, Values = values.Length == 0 ? null : values };

    // metrics.test.ts `traffic` and `latency`.
    private static readonly TelemetrySchemaDefinition Traffic = TelemetrySchema.Define(new(1, new Dictionary<string, TelemetrySpanDefinition>())
    {
        Metrics = new Dictionary<string, TelemetryMetricDefinition>
        {
            ["network.bytes"] = new(TelemetryMetricKind.Counter, "Encoded bytes", Attributes(("direction",
                new(TelemetryAttributeType.String, "Direction") { Values = ["in", "out"], Required = true, Cardinality = TelemetryCardinality.Low })))
            { Unit = "By" },
            ["network.active"] = new(TelemetryMetricKind.UpDownCounter, "Open links", Attributes())
        }
    });

    private static readonly TelemetrySchemaDefinition Latency = TelemetrySchema.Define(new(1, new Dictionary<string, TelemetrySpanDefinition>())
    {
        Metrics = new Dictionary<string, TelemetryMetricDefinition>
        {
            ["call.duration"] = new(TelemetryMetricKind.Histogram, "Call duration",
                Attributes(("method", Attribute(TelemetryAttributeType.String, "Method", required: false))))
            { Unit = "ms", Boundaries = [1, 10, 100] }
        }
    });

    private static StoredSpan Stored(string id, string? parentId = null, string? name = null, double startedMs = 0, double endedMs = 10,
        SpanAttributes? attributes = null, IReadOnlyList<StoredSpanEvent>? events = null, IReadOnlyList<StoredSpanLink>? links = null,
        SpanStatus? status = null, string traceId = "trace") =>
        new(id, traceId, parentId, name ?? id, startedMs, endedMs, attributes ?? new(), events ?? [], links ?? [], status ?? SpanStatus.Ok);

    private static Dictionary<string, string> Carrier(InMemoryTelemetryContext telemetry, ITelemetryContext context) =>
        new(telemetry.Inject(context) ?? throw new InvalidOperationException("Expected a carrier."), StringComparer.Ordinal);

    private static async Task<IReadOnlyList<string>> TraceNames(ITelemetryStore store, TraceQuery? query) =>
        [.. (await store.FindTracesAsync(query)).Traces.Select(trace => trace.Spans[0].Name)];

    private static IEnumerable<(string Id, Func<Task> Run)> PackageCases =>
    [
        // telemetry.test.ts
        ("telemetry.schema.serializable-exact-attributes", async () =>
        {
            var definition = new TelemetrySchemaDefinition(1, new Dictionary<string, TelemetrySpanDefinition>
            {
                ["operation"] = new("Test operation", TelemetryParentDefinition.Any,
                    Attributes(("kind", Attribute(TelemetryAttributeType.String, "Kind", true, "read", "write"))), Attributes(),
                    new("The operation fails"))
                {
                    Events = new Dictionary<string, TelemetryEventDefinition>
                    {
                        ["result"] = new("Result", Attributes(("outcome", Attribute(TelemetryAttributeType.String, "Outcome", true, "ok", "error"))))
                    }
                }
            });
            var schema = TelemetrySchema.Define(definition);
            Same(definition, schema, "defineTelemetrySchema returns the definition");
            Json(JsonNode.Parse("""
                {"version":1,"spans":{"operation":{"description":"Test operation","parents":{"kind":"any"},
                "startAttributes":{"kind":{"type":"string","required":true,"values":["read","write"],"description":"Kind"}},"endAttributes":{},
                "events":{"result":{"description":"Result","attributes":{"outcome":{"type":"string","required":true,"values":["ok","error"],"description":"Outcome"}}}},
                "status":{"default":"ok","errorWhen":"The operation fails"}}}}
                """), schema, "serialized schema");
            var telemetry = new InMemoryTelemetryContext();
            var start = TelemetrySchema.CreateTypedSpanStarter(telemetry, schema);
            await start.StartSpanAsync("operation", new SpanAttributes { ["kind"] = "read" }, (span, _) =>
            {
                span.AddEvent("result", new SpanAttributes { ["outcome"] = "ok" });
                // @ts-expect-error: required event attributes, closed-set event values, undeclared events, empty end schemas.
                Throws<ArgumentException>(() => span.AddEvent("result"), "required event attribute");
                Throws<ArgumentException>(() => span.AddEvent("result", new SpanAttributes { ["outcome"] = "other" }), "closed-set event value");
                Throws<ArgumentException>(() => span.AddEvent("unknown", new SpanAttributes()), "undeclared event");
                Throws<ArgumentException>(() => span.SetAttributes(new SpanAttributes { ["unknown"] = true }), "empty end schema");
            });
            var recorded = telemetry.GetSpans().Single();
            Json(new { kind = "read" }, recorded.Attributes, "start attributes");
            Json(new[] { new { name = "result", attributes = new { outcome = "ok" } } }, recorded.Events, "only the valid event");
        }),
        ("telemetry.schema.combined-vocabularies-and-child-starters", async () =>
        {
            var operationSchema = TelemetrySchema.Define(new(1, new Dictionary<string, TelemetrySpanDefinition>
            {
                ["operation"] = new("Operation", TelemetryParentDefinition.RootOrExternal,
                    Attributes(("kind", Attribute(TelemetryAttributeType.String, "Kind", true, "read", "write"))), Attributes(),
                    new("The operation fails"))
            }));
            var requestSchema = TelemetrySchema.Define(new(3, new Dictionary<string, TelemetrySpanDefinition>
            {
                ["request"] = new("Request", TelemetryParentDefinition.Of("operation"),
                    Attributes(("provider", Attribute(TelemetryAttributeType.String, "Provider", true))),
                    Attributes(("response", Attribute(TelemetryAttributeType.String, "Response kind"))), new("The request fails"))
            }));
            var telemetry = new InMemoryTelemetryContext();
            var start = TelemetrySchema.CreateTypedSpanStarter(telemetry, operationSchema, requestSchema);
            var result = await start.StartSpanAsync("operation", new SpanAttributes { ["kind"] = "read" }, (_, startChild) =>
                startChild.StartSpanAsync("request", new SpanAttributes { ["provider"] = "example" }, (requestSpan, _) =>
                {
                    requestSpan.SetAttributes(new SpanAttributes { ["response"] = "cached" });
                    return 42;
                }));
            Equal(42, result, "result");
            var spans = telemetry.GetSpans();
            var operation = spans.Single(span => span.Name == "operation");
            var request = spans.Single(span => span.Name == "request");
            Check(operation.ParentId is null, "operation is a root");
            Equal<int?>(operation.Id, request.ParentId, "request parent");
            Json(new { provider = "example", response = "cached" }, request.Attributes, "request attributes");

            var syncError = new InvalidOperationException("sync");
            var syncResult = start.StartSpanAsync("operation", new SpanAttributes { ["kind"] = "write" },
                (Func<SchemaTelemetrySpan, TypedSpanStarter, Task<object>>)((_, _) => throw syncError));
            await Rejects(syncResult, syncError, "synchronous rejection");
            var asyncError = new FormatException("async");
            var asyncResult = start.StartSpanAsync("request", new SpanAttributes { ["provider"] = "example" },
                (Func<SchemaTelemetrySpan, TypedSpanStarter, Task<object>>)(async (_, _) => { await Task.Yield(); throw asyncError; }));
            await Rejects(asyncResult, asyncError, "asynchronous rejection");

            // @ts-expect-error cases. Union-valued names have no C# counterpart: a name is one string, checked when the span starts.
            Throws<ArgumentException>(() => start.StartSpanAsync("request", new SpanAttributes { ["provider"] = "example", ["unknown"] = true }, (_, _) => { }), "unknown attribute");
            Throws<ArgumentException>(() => start.StartSpanAsync("unknown", new SpanAttributes(), (_, _) => { }), "unknown span name");
            Throws<ArgumentException>(() => start.StartSpanAsync("request", new SpanAttributes { ["kind"] = "read" }, (_, _) => { }), "another schema's attributes");
            Throws<ArgumentException>(() => start.StartSpanAsync("operation", null, (_, _) => { }), "missing required attribute");
            Throws<ArgumentException>(() => start.StartSpanAsync("operation", new SpanAttributes { ["kind"] = 1 }, (_, _) => { }), "attribute type");
            Throws<ArgumentException>(() => TelemetrySchema.CreateTypedSpanStarter(telemetry, operationSchema, operationSchema), "duplicate span names");
            Equal(4, telemetry.GetSpans().Count, "rejected starts record nothing");
        }),
        ("telemetry.noop.admits-synchronously-and-reuses-one-span", async () =>
        {
            var admitted = false;
            ITelemetrySpan? first = null;
            var result = NoopTelemetry.Context.StartSpanAsync("first", async span =>
            {
                admitted = true;
                first = span;
                var child = await span.StartSpanAsync("child", childSpan => childSpan);
                Same(span, child, "child span");
                return 42;
            });
            Check(admitted, "admitted synchronously");
            Equal(42, await result, "result");
            Same(NoopTelemetry.Context, first, "the one inert span");
            Same(NoopTelemetry.Metrics, NoopTelemetry.Context.Metrics, "context metrics");
            Same(NoopTelemetry.Propagation, NoopTelemetry.Context.Propagation, "context propagation");
            Same(NoopTelemetry.Metrics, first!.Metrics, "span metrics");
            Same(NoopTelemetry.Propagation, first.Propagation, "span propagation");
        }),
        ("telemetry.noop.preserves-rejection-values", async () =>
        {
            var syncError = new InvalidOperationException("sync");
            var sync = NoopTelemetry.Context.StartSpanAsync("sync", (Func<ITelemetrySpan, Task<object>>)(_ => throw syncError));
            await Rejects(sync, syncError, "synchronous rejection");
            var asyncError = new FormatException("async");
            var asyncResult = NoopTelemetry.Context.StartSpanAsync("async",
                (Func<ITelemetrySpan, Task<object>>)(async _ => { await Task.Yield(); throw asyncError; }));
            await Rejects(asyncResult, asyncError, "asynchronous rejection");
        }),
        ("telemetry.noop.ignores-recording-payloads", async () =>
        {
            var result = await NoopTelemetry.Context.StartSpanAsync(new SpanOptions("operation") { Attributes = new() { ["kind"] = "test" } }, span =>
            {
                span.AddEvent("event", new SpanAttributes { ["count"] = 1 });
                span.SetAttributes(new SpanAttributes { ["complete"] = true });
                span.SetStatus(SpanStatus.Failed("Expected", "test"));
                span.AddLink(new SpanLink(span));
                return 42;
            });
            Equal(42, result, "result");
        }),

        // metrics.test.ts
        ("telemetry.metrics.binds-names-kinds-metadata-and-attributes", async () =>
        {
            var adapter = new InMemoryTelemetryContext();
            var metrics = TelemetrySchema.CreateTypedMetrics(adapter, Traffic, Latency);
            metrics.Counter("network.bytes").Add(128, new SpanAttributes { ["direction"] = "out" });
            metrics.UpDownCounter("network.active").Add(1);
            metrics.UpDownCounter("network.active").Add(-1);
            await adapter.StartSpanAsync("request", span =>
            {
                metrics.Histogram("call.duration").Record(12, new SpanAttributes { ["method"] = "read" }, span);
                metrics.Histogram("call.duration").Record(13);
                metrics.Histogram("call.duration").Record(14, null, span);
            });
            var measurements = adapter.GetMetrics();
            var context = adapter.GetSpans()[0].Context;
            Subset(new { kind = "counter", name = "network.bytes", description = "Encoded bytes", unit = "By", value = 128, attributes = new { direction = "out" } },
                measurements[0], "counter measurement");
            Subset(new { kind = "upDownCounter", name = "network.active", value = -1 }, measurements[2], "up-down measurement");
            Subset(new { kind = "histogram", boundaries = new[] { 1, 10, 100 }, context }, measurements[3], "histogram measurement");
            Check(measurements[4].Context is null, "no exemplar");
            Json(context, measurements[5].Context, "exemplar without attributes");

            // @ts-expect-error cases.
            Throws<ArgumentException>(() => metrics.Counter("call.duration"), "counter names are counters");
            Throws<ArgumentException>(() => metrics.Histogram("network.bytes"), "histogram names are histograms");
            Throws<ArgumentException>(() => metrics.UpDownCounter("unknown"), "unknown names");
            Throws<ArgumentException>(() => metrics.Counter("network.bytes").Add(1), "required metric attributes");
            Throws<ArgumentException>(() => metrics.Counter("network.bytes").Add(1, new SpanAttributes { ["direction"] = "sideways" }), "closed-set values");
            Throws<ArgumentException>(() => metrics.Counter("network.bytes").Add(1, new SpanAttributes { ["direction"] = "in", ["unknown"] = true }), "unknown attributes");
            Throws<ArgumentException>(() => metrics.UpDownCounter("network.active").Add(1, new SpanAttributes { ["unknown"] = true }), "empty attribute schema");
            Throws<ArgumentException>(() => TelemetrySchema.CreateTypedMetrics(adapter, Traffic, Traffic), "duplicate metric names");
            Throws<ArgumentException>(() => TelemetrySchema.Define(new(1, new Dictionary<string, TelemetrySpanDefinition>())
            {
                Metrics = new Dictionary<string, TelemetryMetricDefinition>
                {
                    ["invalid"] = new(TelemetryMetricKind.Counter, "Invalid", Attributes(("request",
                        new(TelemetryAttributeType.String, "Request") { Required = false, Cardinality = TelemetryCardinality.High })))
                }
            }), "high-cardinality metric attribute");
            Throws<ArgumentException>(() => TelemetrySchema.Define(new(1, new Dictionary<string, TelemetrySpanDefinition>())
            {
                Metrics = new Dictionary<string, TelemetryMetricDefinition>
                { ["invalid"] = new(TelemetryMetricKind.Counter, "Invalid", Attributes()) { Boundaries = [1] } }
            }), "boundaries belong to histograms");
            Equal(6, adapter.GetMetrics().Count, "rejected measurements record nothing");
            Json(JsonNode.Parse("""
                {"version":1,"spans":{},"metrics":{"network.bytes":{"kind":"counter","description":"Encoded bytes","unit":"By",
                "attributes":{"direction":{"type":"string","values":["in","out"],"required":true,"description":"Direction","cardinality":"low"}}},
                "network.active":{"kind":"upDownCounter","description":"Open links","attributes":{}}}}
                """), Traffic, "serialized metric schema");
        }),
        ("telemetry.metrics.requires-declared-histogram-attributes", Sync(() =>
        {
            var schema = TelemetrySchema.Define(new(1, new Dictionary<string, TelemetrySpanDefinition>())
            {
                Metrics = new Dictionary<string, TelemetryMetricDefinition>
                {
                    ["required"] = new(TelemetryMetricKind.Histogram, "Required tag", Attributes(("cache", Attribute(TelemetryAttributeType.Boolean, "Cache hit", true))))
                }
            });
            var adapter = new InMemoryTelemetryContext();
            var metrics = TelemetrySchema.CreateTypedMetrics(adapter, schema);
            metrics.Histogram("required").Record(1, new SpanAttributes { ["cache"] = true });
            Throws<ArgumentException>(() => metrics.Histogram("required").Record(1), "histogram attributes are required");
            Throws<ArgumentException>(() => metrics.Histogram("required").Record(1, new SpanAttributes { ["cache"] = "true" }), "attribute types");
            Throws<ArgumentException>(() => metrics.Histogram("required").Record(1, null, NoopTelemetry.Context), "an exemplar is not an attribute");
            Equal(1, adapter.GetMetrics().Count, "one measurement");
        })),
        ("telemetry.metrics.detached-snapshots-with-exemplars-and-boundaries", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var histogram = telemetry.Histogram("duration", new HistogramOptions { Boundaries = [1, 2] });
            await telemetry.StartSpanAsync("request", span => histogram.Record(1, new SpanAttributes { ["tags"] = new[] { "original" } }, span));
            var measurement = telemetry.GetMetrics()[0];
            Check(measurement.Context is not null, "Expected measurement context");
            ((double[])measurement.Boundaries!)[0] = 3;
            measurement.Attributes["tags"] = new[] { "mutated" };
            ((Dictionary<string, string>)measurement.Context!)["pi-memory-span"] = "999";
            Subset(new { boundaries = new[] { 1, 2 }, attributes = new { tags = new[] { "original" } }, context = telemetry.GetSpans()[0].Context },
                telemetry.GetMetrics()[0], "fresh measurement");
        }),
        ("telemetry.metrics.shared-noop-instruments", Sync(() =>
        {
            var options = new InstrumentOptions { Unit = "By" };
            var attributes = new SpanAttributes { ["direction"] = "out" };
            var counter = NoopTelemetry.Metrics.Counter("bytes", options);
            var active = NoopTelemetry.Metrics.UpDownCounter("active", options);
            var histogram = NoopTelemetry.Metrics.Histogram("duration", new HistogramOptions { Unit = "By" });
            Same(counter, NoopTelemetry.Metrics.Counter("other"), "one shared instrument");
            Same(counter, active, "one shared up-down counter");
            Same(counter, histogram, "one shared histogram");
            counter.Add(1, attributes);
            active.Add(-1, attributes);
            histogram.Record(1, attributes, NoopTelemetry.Context);
        })),

        // propagation.test.ts
        ("telemetry.propagation.continues-a-parent-in-another-process", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            Dictionary<string, string>? carrier = null;
            await telemetry.StartSpanAsync("caller", span => { carrier = Carrier(telemetry, span); });
            var recorded = JsonNode.Parse(await RunWorker(carrier!))!.AsArray();
            Equal(1, recorded.Count, "worker spans");
            Json(carrier, recorded[0]!["parentContext"], "remote parent context");
            Check(recorded[0]!.AsObject().ContainsKey("parentId") && recorded[0]!["parentId"] is null, "no local parent in the worker");
            Check(!JsonNode.DeepEquals(Node(carrier), recorded[0]!["context"]), "the worker span has its own context");
            Equal("remote", recorded[0]!["name"]!.GetValue<string>(), "worker span name");
        }),
        ("telemetry.propagation.isolates-crossed-concurrent-remote-parents", async () =>
        {
            var caller = new InMemoryTelemetryContext();
            var provider = new InMemoryTelemetryContext();
            ITelemetrySpan? activation = null;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? shared = null;
            await Task.WhenAll(new[] { "first", "later" }.Select(name => caller.StartSpanAsync(name, async trigger =>
            {
                var parent = provider.Extract(caller.Inject(trigger));
                await parent.StartSpanAsync($"{name}.method", async getter =>
                {
                    shared ??= provider.StartSpanAsync(new SpanOptions("activation") { Links = [new SpanLink(getter)] }, async span =>
                    {
                        activation = span;
                        await gate.Task;
                    });
                    if (activation is not null) getter.AddLink(new SpanLink(activation));
                    if (name == "later") gate.TrySetResult();
                    await shared;
                });
            })));
            var spans = provider.GetSpans();
            var root = spans.Single(span => span.Name == "activation");
            Check(root.ParentContext is null, "the shared activation is a root");
            var first = spans.Single(span => span.Name == "first.method");
            var later = spans.Single(span => span.Name == "later.method");
            Json(caller.GetSpans()[0].Context, first.ParentContext, "first remote parent");
            Json(caller.GetSpans()[1].Context, later.ParentContext, "later remote parent");
            Json(first.Context, root.Links[0].Context, "activation links its first getter");
            Json(root.Context, later.Links[0].Context, "later getter links the activation");
        }),
        ("telemetry.propagation.detaches-carriers-and-links-and-retains-settled-targets", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            ITelemetrySpan? target = null;
            await telemetry.StartSpanAsync("target", span => { target = span; });
            var original = telemetry.Inject(target!);
            var injected = telemetry.Inject(target!) ?? throw new InvalidOperationException("Expected carrier");
            ((Dictionary<string, string>)injected)["pi-memory-span"] = "999";
            Json(original, telemetry.Inject(target!), "carrier after mutation");
            await telemetry.StartSpanAsync(new SpanOptions("linked")
            { Links = [new SpanLink(target!, new SpanAttributes { ["tags"] = new[] { "original" } })] }, _ => { });
            var spans = telemetry.GetSpans();
            ((Dictionary<string, string>)spans[0].Context)["pi-memory-span"] = "123";
            ((Dictionary<string, string>)spans[1].Links[0].Context)["pi-memory-span"] = "456";
            spans[1].Links[0].Attributes["tags"] = new[] { "mutated" };
            Json(new[] { new { context = original, attributes = new { tags = new[] { "original" } } } }, telemetry.GetSpans()[1].Links, "fresh links");
        }),
        ("telemetry.propagation.links-a-new-root-to-an-extracted-remote-target", async () =>
        {
            var sender = new InMemoryTelemetryContext();
            var receiver = new InMemoryTelemetryContext();
            Dictionary<string, string>? carrier = null;
            await sender.StartSpanAsync("trigger", span => { carrier = Carrier(sender, span); });
            var target = receiver.Extract(Roundtrip(carrier!));
            await receiver.StartSpanAsync(new SpanOptions("background")
            { Links = [new SpanLink(target, new SpanAttributes { ["reason"] = "restart" })] }, span => span.AddLink(new SpanLink(target)));
            var recorded = receiver.GetSpans()[0];
            Check(recorded.ParentContext is null, "a new root");
            Json(new object[] { new { context = carrier, attributes = new { reason = "restart" } }, new { context = carrier, attributes = new { } } },
                recorded.Links, "links to the remote target");
        }),
        ("telemetry.propagation.uses-the-root-for-absent-and-unknown-carriers", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            IReadOnlyDictionary<string, string>?[] carriers = [null, new Dictionary<string, string>(), new Dictionary<string, string> { ["unknown"] = "format" }];
            foreach (var carrier in carriers) Same(telemetry, telemetry.Extract(carrier), "root context");
            Check(NoopTelemetry.Propagation.Inject(NoopTelemetry.Context) is null, "no-op inject");
            Same(NoopTelemetry.Context, NoopTelemetry.Propagation.Extract(new Dictionary<string, string> { ["unknown"] = "format" }), "no-op extract");
            await NoopTelemetry.Context.StartSpanAsync("noop", span => span.AddLink(new SpanLink(span)));
        }),
        ("telemetry.propagation.treats-recognized-identifiers-as-opaque-strings", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var carrier = new Dictionary<string, string>
            {
                ["pi-memory-scope"] = "external-recorder", ["pi-memory-span"] = "external-span", ["pi-memory-trace"] = "external-trace"
            };
            await telemetry.Extract(carrier).StartSpanAsync("receiver", _ => { });
            Json(carrier, telemetry.GetSpans()[0].ParentContext, "opaque parent context");
            Check(telemetry.GetSpans()[0].ParentId is null, "no local parent");
        }),

        // render.test.ts
        ("telemetry.render.unordered-trace-with-attributes-durations-errors-events-links", Sync(() =>
        {
            var root = Stored("root", startedMs: 1000, endedMs: 1020,
                attributes: new()
                {
                    ["request"] = "a b", ["attempt"] = 2, ["cached"] = false, ["tags"] = new[] { "a", "b" }, ["sizes"] = new double[] { 1, 2 },
                    ["flags"] = new[] { true, false }
                },
                events: [new("checkpoint", 2, new() { ["phase"] = "tools" }), new("tick", 3.5, new())],
                links: [new("remote-trace", "remote", new() { ["reason"] = "steer" }), new("other-trace", "other")]);
            var first = Stored("first", "root", startedMs: 1005, endedMs: 1009.5, status: SpanStatus.Failed("Error", "failed call"));
            var nested = Stored("nested", "first", startedMs: 1006, endedMs: 1007);
            var second = Stored("second", "root", startedMs: 1010, endedMs: 1012, status: SpanStatus.Failed());
            StoredSpan[] spans = [second, nested, first, root];
            var before = Text(spans);
            Equal(string.Join("\n",
                "root request=\"a b\" attempt=2 cached=false tags=[\"a\",\"b\"] sizes=[1,2] flags=[true,false] (20ms)",
                "  · checkpoint phase=\"tools\" (+2ms)",
                "  · tick (+3.5ms)",
                "  → link trace remote-trace span remote reason=\"steer\"",
                "  → link trace other-trace span other",
                "  first (4.5ms) error Error: failed call",
                "    nested (1ms)",
                "  second (2ms) error"), TraceRenderer.RenderTrace(spans), "rendered trace");
            Equal(before, Text(spans), "input unchanged");
        })),
        ("telemetry.render.missing-parents-are-roots-ordered-by-start", Sync(() =>
            Equal(string.Join("\n", "earlier (5ms)", "  → link trace trace span later", "later (10ms)", "  child (5ms)"),
                TraceRenderer.RenderTrace([
                    Stored("later", "external", startedMs: 20, endedMs: 30),
                    Stored("child", "later", startedMs: 25, endedMs: 30),
                    Stored("earlier", startedMs: 10, endedMs: 15, links: [new("trace", "later")])
                ]), "rendered trace"))),
        ("telemetry.render.prints-cross-trace-links-without-following-cycles", Sync(() =>
            Equal(string.Join("\n", "server (10ms)", "  → link trace worker-trace span worker:1", "worker (10ms)", "  → link trace server-trace span server:1"),
                TraceRenderer.RenderTrace([
                    Stored("server:1", name: "server", traceId: "server-trace", links: [new("worker-trace", "worker:1")]),
                    Stored("worker:1", name: "worker", traceId: "worker-trace", links: [new("server-trace", "server:1")])
                ]), "rendered trace"))),
        ("telemetry.render.deduplicates-merged-spans-by-global-id", Sync(() =>
        {
            var root = Stored("server:1", name: "root");
            var child = Stored("worker:1", "server:1", "child", events: [new("done", 3, new())]);
            var grandchild = Stored("worker:2", "worker:1", "grandchild");
            Equal("root (10ms)\n  child (10ms)\n    · done (+3ms)\n    grandchild (10ms)",
                TraceRenderer.RenderTrace([child, root, grandchild, root with { }, child with { }]), "rendered trace");
        })),
        ("telemetry.render.preserves-input-order-for-equal-starts", Sync(() =>
        {
            StoredSpan[] spans = [Stored("root"), Stored("z", "root"), Stored("a", "root"), Stored("other")];
            var before = Text(spans);
            Equal("root (10ms)\n  z (10ms)\n  a (10ms)\nother (10ms)", TraceRenderer.RenderTrace(spans), "rendered trace");
            Equal(before, Text(spans), "input unchanged");
        })),
        ("telemetry.render.escapes-string-values", Sync(() =>
            Equal("root text=\"line one\\n\\\"line two\\\"\" (10ms)",
                TraceRenderer.RenderTrace([Stored("root", attributes: new() { ["text"] = "line one\n\"line two\"" })]), "rendered trace"))),
        ("telemetry.render.deep-trees-without-recursion", Sync(() =>
        {
            var spans = Enumerable.Range(0, 2000).Select(index => Stored(index.ToString(), index == 0 ? null : (index - 1).ToString())).ToArray();
            var rendered = TraceRenderer.RenderTrace(spans);
            Equal(spans.Length, rendered.Split('\n').Length, "one line per span");
            Check(rendered.EndsWith(string.Concat(Enumerable.Repeat("  ", 1999)) + "1999 (10ms)", StringComparison.Ordinal), "deepest indent");
        })),
        ("telemetry.render.empty-input", Sync(() => Equal("", TraceRenderer.RenderTrace([]), "empty render"))),

        // store.test.ts
        ("telemetry.store.wall-clock-boundaries-and-relative-event-times", async () =>
        {
            var time = new ManualTime(nowMs: 1000, clockMs: 100);
            var telemetry = new InMemoryTelemetryContext(time);
            var id = "";
            await telemetry.StartSpanAsync("timed", span =>
            {
                var carrier = Carrier(telemetry, span);
                id = $"{carrier["pi-memory-scope"]}:{carrier["pi-memory-span"]}";
                time.ClockMs = 110;
                span.AddEvent("event", new SpanAttributes { ["count"] = 1 });
                time.ClockMs = 125;
            });
            Json(new
            {
                traceId = id,
                spans = new[]
                {
                    new
                    {
                        id, traceId = id, name = "timed", startedMs = 1000, endedMs = 1025, attributes = new { },
                        events = new[] { new { name = "event", timeMs = 10, attributes = new { count = 1 } } }, links = Array.Empty<object>(),
                        status = new { status = "ok" }
                    }
                }
            }, await telemetry.GetTraceAsync(id), "stored trace");
            Json(new[] { new { name = "event", attributes = new { count = 1 } } }, telemetry.GetSpans()[0].Events, "normalized events");
            Check(Node(telemetry.GetSpans()[0])!.AsObject() is var snapshot && !snapshot.ContainsKey("startedMs"), "no private timing");
        }),
        ("telemetry.store.count-one-metric-rows-without-private-timing", async () =>
        {
            var telemetry = new InMemoryTelemetryContext(new ManualTime(nowMs: 20_001));
            telemetry.Counter("calls").Add(2);
            telemetry.Counter("calls").Add(3);
            Json(new[] { new[] { 20_000, 1, 2 }, new[] { 20_000, 1, 3 } },
                (await telemetry.FindMetricsAsync(new MetricQuery("calls"))).Select(row => new[] { row.IntervalMs, row.Count, row.Sum }), "metric rows");
            Json(new[]
            {
                new { kind = "counter", name = "calls", value = 2, attributes = new { }, context = (object?)null },
                new { kind = "counter", name = "calls", value = 3, attributes = new { }, context = (object?)null }
            }, telemetry.GetMetrics(), "raw measurements");
        }),
        ("telemetry.store.detaches-link-attributes-and-error-details", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            ITelemetrySpan target = null!;
            await telemetry.StartSpanAsync("target", span => { target = span; });
            await telemetry.StartSpanAsync(new SpanOptions("source")
            { Links = [new SpanLink(target, new SpanAttributes { ["tags"] = new[] { "original" } })] },
                span => span.SetStatus(SpanStatus.Failed("Expected", "original")));
            var trace = (await telemetry.FindTracesAsync(new TraceQuery { Name = "source" })).Traces[0];
            trace.Spans[0].Links[0].Attributes!["tags"] = new[] { "changed" };
            // A SpanError is an immutable record: its message cannot be changed through a result.
            var fresh = (await telemetry.GetTraceAsync(trace.TraceId))!.Spans[0];
            Json(new[] { "original" }, fresh.Links[0].Attributes!["tags"], "fresh link attributes");
            Json(new { status = "error", error = new { name = "Expected", message = "original" } }, fresh.Status, "fresh status");
        }),

        // store-conformance.ts, against the in-memory store.
        ("telemetry.store-conformance.settled-traces-complete-snapshots-detached-results", async () =>
        {
            var f = new InMemoryTelemetryContext(new ManualTime(nowMs: 20_000));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = f.StartSpanAsync(new SpanOptions("root") { Attributes = new() { ["tags"] = new[] { "original" } } }, async root =>
            {
                root.AddEvent("event", new SpanAttributes { ["tags"] = new[] { "original" } });
                await gate.Task;
                await root.StartSpanAsync("child", _ => { });
            });
            Equal(0, (await f.FindTracesAsync()).Traces.Count, "no unsettled traces");
            Check(await f.GetTraceAsync("missing") is null, "missing trace");
            gate.SetResult();
            await active;
            var trace = (await f.FindTracesAsync()).Traces[0];
            var root = trace.Spans.Single(span => span.Name == "root");
            var child = trace.Spans.Single(span => span.Name == "child");
            Check(Regex.IsMatch(root.Id, "^[0-9a-f-]{36}:[1-9][0-9]*$"), "global span id " + root.Id);
            Equal(root.Id, root.TraceId, "root trace id");
            Check(root.ParentId is null, "root has no parent");
            Equal(root.Id, child.ParentId, "child parent");
            Equal(root.TraceId, child.TraceId, "child trace");
            Equal(20_000d, root.StartedMs, "root start");
            Check(root.EndedMs >= root.StartedMs, "root end");
            Check(root.Events[0].TimeMs >= 0, "event time");
            root.Attributes["tags"] = new[] { "changed" };
            root.Events[0].Attributes["tags"] = new[] { "changed" };
            var fresh = (await f.GetTraceAsync(trace.TraceId))!.Spans.Single(span => span.Id == root.Id);
            Json(new[] { "original" }, fresh.Attributes["tags"], "fresh attributes");
            Json(new[] { "original" }, fresh.Events[0].Attributes["tags"], "fresh event attributes");
        }),
        ("telemetry.store-conformance.filters-name-start-errors-and-one-span-attributes", async () =>
        {
            var time = new ManualTime(nowMs: 10_000);
            var f = new InMemoryTelemetryContext(time);
            await f.StartSpanAsync("old", async root =>
            {
                time.NowMs = 30_000;
                await root.StartSpanAsync(new SpanOptions("recent-child") { Attributes = new() { ["left"] = 1 } }, _ => { });
                await root.StartSpanAsync(new SpanOptions("split") { Attributes = new() { ["right"] = 2 } }, _ => { });
            });
            time.NowMs = 20_000;
            await f.StartSpanAsync("wanted", async root =>
            {
                time.NowMs = 20_001;
                await root.StartSpanAsync(new SpanOptions("failure")
                { Attributes = new() { ["left"] = 1, ["right"] = 2, ["valid"] = true, ["odd.\"key"] = "value" } }, child => child.SetStatus(SpanStatus.Failed()));
            });
            time.NowMs = 40_000;
            await f.StartSpanAsync(new SpanOptions("new") { Attributes = new() { ["valid"] = 1 } }, _ => { });
            Json(new[] { "wanted" }, await TraceNames(f, new TraceQuery
            { Name = "wanted", Attributes = new() { ["left"] = 1, ["right"] = 2 }, Errors = true, StartMin = 20_000, StartMax = 20_000 }), "all filters");
            Json(Array.Empty<string>(), await TraceNames(f, new TraceQuery { Name = "failure" }), "first-span name");
            Json(new[] { "wanted" }, await TraceNames(f, new TraceQuery { Attributes = new() { ["left"] = 1, ["right"] = 2 } }), "attributes on one span");
            Json(new[] { "wanted" }, await TraceNames(f, new TraceQuery { Attributes = new() { ["valid"] = true, ["odd.\"key"] = "value" } }), "typed and odd keys");
            Json(new[] { "new" }, await TraceNames(f, new TraceQuery { Attributes = new() { ["valid"] = 1 } }), "number is not boolean");
            Json(new[] { "wanted" }, await TraceNames(f, new TraceQuery { Errors = true }), "errors");
            Json(new[] { "new", "wanted" }, await TraceNames(f, new TraceQuery { StartMin = 20_000 }), "start minimum");
            Json(new[] { "old" }, await TraceNames(f, new TraceQuery { StartMax = 10_000 }), "start maximum");
        }),
        ("telemetry.store-conformance.pages-complete-traces-when-starts-tie", async () =>
        {
            var time = new ManualTime(nowMs: 20_000);
            var f = new InMemoryTelemetryContext(time);
            for (var index = 0; index < 25; index++)
            {
                time.NowMs = 20_000;
                // Span starts follow the monotonic clock within the wall clock's 10ms tolerance.
                // Explicitly model tied roots and later children with both clocks.
                time.ClockMs = 0;
                await f.StartSpanAsync(new SpanOptions("root") { Attributes = new() { ["index"] = index } }, async root =>
                {
                    time.NowMs = 20_001;
                    time.ClockMs = 1;
                    await root.StartSpanAsync("child", _ => { });
                });
            }
            var first = await f.FindTracesAsync();
            Equal(20, first.Traces.Count, "first page");
            Check(first.Cursor is not null, "first cursor");
            var rest = await f.FindTracesAsync(new TraceQuery { Cursor = first.Cursor });
            Equal(5, rest.Traces.Count, "second page");
            Check(rest.Cursor is null, "no further cursor");
            var all = (await f.FindTracesAsync(new TraceQuery { Limit = 100 })).Traces;
            Json(all, first.Traces.Concat(rest.Traces), "pages concatenate to all traces");
            Check(all.All(trace => trace.Spans.Count == 2), "complete traces");
            var ids = new List<string>();
            string? cursor = null;
            do
            {
                var page = await f.FindTracesAsync(new TraceQuery { Name = "root", Limit = 3, Cursor = cursor });
                ids.AddRange(page.Traces.Select(trace => trace.TraceId));
                cursor = page.Cursor;
            } while (cursor is not null);
            Json(all.Select(trace => trace.TraceId), ids, "small pages");
            Throws<ArgumentOutOfRangeException>(() => f.FindTracesAsync(new TraceQuery { Limit = 0 }), "positive page size");
        }),
        ("telemetry.store-conformance.inherits-remote-trace-ids-and-keeps-remote-parents-and-links", async () =>
        {
            var time = new ManualTime(nowMs: 20_000);
            var sender = new InMemoryTelemetryContext(time);
            var receiver = new InMemoryTelemetryContext(time);
            Dictionary<string, string> carrier = null!;
            await sender.StartSpanAsync("sender", span => { carrier = Carrier(sender, span); });
            var sent = (await sender.FindTracesAsync()).Traces[0];
            var remote = receiver.Extract(Roundtrip(carrier));
            await remote.StartSpanAsync("receiver", _ => { });
            await receiver.StartSpanAsync(new SpanOptions("linked")
            { Links = [new SpanLink(remote, new SpanAttributes { ["reason"] = "background" })] }, _ => { });
            var continued = await receiver.GetTraceAsync(sent.TraceId);
            Equal(1, continued?.Spans.Count, "continued spans");
            Subset(new { traceId = sent.TraceId, parentId = sent.Spans[0].Id, name = "receiver" }, continued!.Spans[0], "continued span");
            Check(continued.Spans[0].Id != sent.Spans[0].Id, "receiver span id");
            var linked = (await receiver.FindTracesAsync(new TraceQuery { Name = "linked" })).Traces[0];
            Check(linked.TraceId != sent.TraceId, "linked root starts its own trace");
            Json(new[] { new { traceId = sent.TraceId, spanId = sent.Spans[0].Id, attributes = new { reason = "background" } } }, linked.Spans[0].Links, "cross-trace link");
            foreach (var key in carrier.Keys)
            {
                var incomplete = new Dictionary<string, string>(carrier);
                incomplete.Remove(key);
                await receiver.Extract(incomplete).StartSpanAsync("fallback", _ => { });
                // JavaScript's non-string identity: here a null value.
                var nonString = new Dictionary<string, string>(carrier) { [key] = null! };
                await receiver.Extract(nonString).StartSpanAsync("fallback", _ => { });
            }
            var fallbacks = (await receiver.FindTracesAsync(new TraceQuery { Name = "fallback" })).Traces;
            Equal(carrier.Count * 2, fallbacks.Count, "fallback roots");
            Check(fallbacks.All(trace => trace.Spans[0].ParentId is null && trace.TraceId == trace.Spans[0].Id), "fallbacks are roots");
        }),
        ("telemetry.store-conformance.accepts-external-string-identities", async () =>
        {
            var f = new InMemoryTelemetryContext();
            Dictionary<string, string> carrier = null!;
            await f.StartSpanAsync("source", span => { carrier = Carrier(f, span); });
            var external = carrier.Keys.ToDictionary(key => key, key => $"external-{key}", StringComparer.Ordinal);
            var parent = f.Extract(external);
            Json(external, f.Inject(parent), "extracted identity reinjects");
            await parent.StartSpanAsync("continued", _ => { });
            var trace = (await f.FindTracesAsync(new TraceQuery { Name = "continued" })).Traces[0];
            Check(trace.TraceId.StartsWith("external-", StringComparison.Ordinal), "external trace id");
            Check(trace.Spans[0].ParentId?.StartsWith("external-", StringComparison.Ordinal) == true, "external parent id");
        }),
        ("telemetry.store-conformance.metric-names-interval-bounds-typed-equality-detached-values", async () =>
        {
            var time = new ManualTime(nowMs: 20_001);
            var f = new InMemoryTelemetryContext(time);
            var counter = f.Metrics!.Counter("hits", new InstrumentOptions { Unit = "By" });
            counter.Add(2, new SpanAttributes { ["method"] = "read", ["valid"] = true });
            counter.Add(3, new SpanAttributes { ["valid"] = true, ["method"] = "read" });
            counter.Add(11, new SpanAttributes { ["method"] = "write", ["valid"] = 1 });
            f.Metrics.Counter("other").Add(100, new SpanAttributes { ["method"] = "read", ["valid"] = true });
            time.NowMs = 30_001;
            counter.Add(7, new SpanAttributes { ["method"] = "read", ["valid"] = true });
            time.NowMs = 40_001;
            var query = new MetricQuery("hits") { StartMin = 20_000, StartMax = 20_000, Attributes = new() { ["method"] = "read", ["valid"] = true } };
            var rows = await f.FindMetricsAsync(query);
            Equal(2d, rows.Sum(row => row.Count), "count");
            Equal(5d, rows.Sum(row => row.Sum), "sum");
            Equal(2d, rows.Min(row => row.Min), "min");
            Equal(3d, rows.Max(row => row.Max), "max");
            Check(rows.All(row => row.Kind == TelemetryMetricKind.Counter && row.Unit == "By" && row.IntervalMs == 20_000), "row metadata");
            rows[0].Attributes["method"] = "changed";
            Equal("read", (await f.FindMetricsAsync(query))[0].Attributes["method"]?.GetString(), "detached row attributes");
            Equal(0, (await f.FindMetricsAsync(new MetricQuery("missing"))).Count, "missing metric");
            Equal(11d, (await f.FindMetricsAsync(new MetricQuery("hits") { Attributes = new() { ["valid"] = 1 } })).Sum(row => row.Sum), "typed equality");
            Equal(7d, (await f.FindMetricsAsync(new MetricQuery("hits") { StartMin = 30_000 })).Sum(row => row.Sum), "interval minimum");
        }),
    ];
}
