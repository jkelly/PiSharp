// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/testing/conformance.ts, packages/telemetry/src/testing/types.ts.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Telemetry.Testing;

/// <summary>types.ts <c>TelemetryAdapterFixture</c>: a fresh adapter instance and normalized snapshot readers owned by one conformance
/// case. <see cref="GetMetrics"/> is required when <see cref="Metrics"/> is set.</summary>
public sealed class TelemetryAdapterFixture : IAsyncDisposable
{
    public required ITelemetryContext Context { get; init; }
    public ITelemetryPropagation? Propagation { get; init; }
    public ITelemetryMetrics? Metrics { get; init; }
    public Func<Task<IReadOnlyList<RecordedTelemetryMeasurement>>>? GetMetrics { get; init; }
    public required Func<Task<IReadOnlyList<RecordedTelemetrySpan>>> GetSpans { get; init; }
    public Func<ValueTask>? Dispose { get; init; }

    public ValueTask DisposeAsync() => Dispose?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>types.ts <c>TelemetryAdapterFixtureFactory</c>: creates an isolated adapter fixture for one conformance case.</summary>
public delegate Task<TelemetryAdapterFixture> TelemetryAdapterFixtureFactory();

/// <summary>types.ts <c>TelemetryAdapterConformanceCase</c>: a runner-independent case that any test runner can register. A failed
/// expectation throws <see cref="TelemetryConformanceException"/>.</summary>
public sealed class TelemetryAdapterConformanceCase
{
    private readonly Func<Task> _run;

    internal TelemetryAdapterConformanceCase(string group, string name, Func<Task> run)
    {
        Group = group;
        Name = name;
        _run = run;
    }

    public string Group { get; }
    public string Name { get; }
    public Task RunAsync() => _run();
}

/// <summary>A failed conformance expectation.</summary>
public sealed class TelemetryConformanceException(string message) : Exception(message);

/// <summary>conformance.ts <c>createTelemetryAdapterConformance</c>: cases for the callback telemetry adapter contract. Cases whose
/// capability the fixture lacks (metrics, propagation) return without checking anything. JavaScript's non-Error rejection values have
/// no C# form: the rejection cases use exceptions, including a cancellation.</summary>
public static class TelemetryAdapterConformance
{
    public static IReadOnlyList<TelemetryAdapterConformanceCase> Create(TelemetryAdapterFixtureFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return
        [
            Case(factory, "capabilities", "carries adapter capabilities through contexts and spans", async fixture =>
            {
                var metrics = fixture.Metrics ?? fixture.Context.Metrics;
                var propagation = fixture.Propagation ?? fixture.Context.Propagation;
                void AssertCapabilities(ITelemetryContext context)
                {
                    Same(context.Metrics, metrics, "metrics capability");
                    Same(context.Propagation, propagation, "propagation capability");
                }
                AssertCapabilities(fixture.Context);
                ITelemetrySpan? settled = null;
                await fixture.Context.StartSpanAsync("capabilities", async span =>
                {
                    settled = span;
                    AssertCapabilities(span);
                    await span.StartSpanAsync("capabilities.child", AssertCapabilities);
                    if (propagation is not null)
                    {
                        var parent = propagation.Extract(propagation.Inject(span));
                        AssertCapabilities(parent);
                        await parent.StartSpanAsync("capabilities.extracted", AssertCapabilities);
                    }
                });
                Ok(settled is not null, "Expected the callback span");
                await settled!.StartSpanAsync("capabilities.inert", AssertCapabilities);
            }),
            Case(factory, "metrics", "records ordered measurements and optional histogram exemplars", async fixture =>
            {
                if (fixture.Metrics is null) return;
                Ok(fixture.GetMetrics is not null, "Metrics fixtures require a normalized measurement reader");
                fixture.Metrics.Counter("bytes", new InstrumentOptions { Description = "Encoded bytes", Unit = "By" })
                    .Add(12, new SpanAttributes { ["direction"] = "out" });
                var active = fixture.Metrics.UpDownCounter("active");
                active.Add(1);
                active.Add(-1);
                var histogram = fixture.Metrics.Histogram("duration", new HistogramOptions { Unit = "ms", Boundaries = [1, 10] });
                await fixture.Context.StartSpanAsync("measured", span => histogram.Record(5, new SpanAttributes { ["method"] = "read" }, span));
                var measurements = await fixture.GetMetrics!();
                DeepEqual(measurements.Select(item => new object[] { item.Kind, item.Name, item.Value }),
                    new object[][]
                    {
                        [TelemetryMetricKind.Counter, "bytes", 12.0],
                        [TelemetryMetricKind.UpDownCounter, "active", 1.0],
                        [TelemetryMetricKind.UpDownCounter, "active", -1.0],
                        [TelemetryMetricKind.Histogram, "duration", 5.0]
                    }, "measurements");
                DeepEqual(measurements[0].Attributes, new SpanAttributes { ["direction"] = "out" }, "counter attributes");
                Equal(measurements[0].Description, "Encoded bytes", "counter description");
                Equal(measurements[0].Unit, "By", "counter unit");
                DeepEqual(measurements[3].Boundaries, new[] { 1.0, 10.0 }, "histogram boundaries");
                DeepEqual(measurements[3].Context, FindSpan(await fixture.GetSpans(), "measured").Context, "histogram exemplar");
            }),
            Case(factory, "metrics", "copies instrument metadata and measurement attributes", async fixture =>
            {
                if (fixture.Metrics is null) return;
                Ok(fixture.GetMetrics is not null, "Metrics fixtures require a normalized measurement reader");
                var boundaries = new List<double> { 1, 10 };
                var histogram = fixture.Metrics.Histogram("snapshot", new HistogramOptions { Description = "Original", Boundaries = boundaries });
                boundaries.Add(100);
                var attributes = new SpanAttributes { ["tags"] = new[] { "original" } };
                histogram.Record(-1, attributes);
                attributes["tags"] = new[] { "original", "changed" };
                var measurements = await fixture.GetMetrics!();
                Equal(measurements.Count, 1, "measurement count");
                DeepEqual(measurements[0].Attributes, new SpanAttributes { ["tags"] = new[] { "original" } }, "attributes");
                DeepEqual(measurements[0].Boundaries, new[] { 1.0, 10.0 }, "boundaries");
                Equal(measurements[0].Description, "Original", "description");
                Ok(measurements[0].Context is null, "Expected no exemplar context");
            }),
            Case(factory, "links", "records initial and late links without changing parents", async fixture =>
            {
                ITelemetrySpan? target = null;
                await fixture.Context.StartSpanAsync("target", span => { target = span; });
                if (target is null) throw new TelemetryConformanceException("Expected target span");
                var attributes = new SpanAttributes { ["reason"] = new[] { "initial" } };
                await fixture.Context.StartSpanAsync(new SpanOptions("linked") { Links = [new SpanLink(target, attributes)] }, span =>
                {
                    attributes["reason"] = new[] { "initial", "mutated" };
                    span.AddLink(new SpanLink(target, new SpanAttributes { ["reason"] = "late" }));
                    span.AddLink(new SpanLink(NoopTelemetry.Context));
                });
                var spans = await fixture.GetSpans();
                var linked = FindSpan(spans, "linked");
                Ok(linked.ParentId is null, "Expected no local parent");
                Ok(linked.ParentContext is null, "Expected no parent context");
                var targetContext = FindSpan(spans, "target").Context;
                DeepEqual(linked.Links, new[]
                {
                    new RecordedTelemetryLink(targetContext, new SpanAttributes { ["reason"] = new[] { "initial" } }),
                    new RecordedTelemetryLink(targetContext, new SpanAttributes { ["reason"] = "late" })
                }, "links");
            }),
            Case(factory, "links", "ignores links added after source settlement", async fixture =>
            {
                ITelemetrySpan? source = null;
                await fixture.Context.StartSpanAsync("link-source", span => { source = span; });
                if (source is null) throw new TelemetryConformanceException("Expected source span");
                source.AddLink(new SpanLink(source));
                DeepEqual(FindSpan(await fixture.GetSpans(), "link-source").Links, Array.Empty<RecordedTelemetryLink>(), "links");
            }),
            Case(factory, "propagation", "roundtrips parent identity through JSON without retaining the carrier", async fixture =>
            {
                if (fixture.Propagation is null) return;
                var propagation = fixture.Propagation;
                await fixture.Context.StartSpanAsync("sender", async span =>
                {
                    var carrier = propagation.Inject(span);
                    Ok(carrier is not null, "Expected a carrier");
                    var wire = JsonRoundtrip(carrier!);
                    var parent = propagation.Extract(wire);
                    foreach (var key in wire.Keys.ToArray()) wire[key] = "mutated";
                    await parent.StartSpanAsync("receiver", _ => 42);
                    DeepEqual(propagation.Inject(span), carrier, "carrier after extraction");
                });
                var spans = await fixture.GetSpans();
                DeepEqual(FindSpan(spans, "receiver").ParentContext, FindSpan(spans, "sender").Context, "receiver parent");
            }),
            Case(factory, "propagation", "absent and unknown carriers produce recorded roots", async fixture =>
            {
                if (fixture.Propagation is null) return;
                Ok(fixture.Propagation.Inject(NoopTelemetry.Context) is null, "Expected no carrier for a foreign context");
                IReadOnlyDictionary<string, string>?[] carriers = [null, new Dictionary<string, string>(), new Dictionary<string, string> { ["unknown"] = "format" }];
                for (var index = 0; index < carriers.Length; index++)
                {
                    var context = fixture.Propagation.Extract(carriers[index]);
                    Equal(await context.StartSpanAsync($"fallback-{index}", _ => 42), 42, "fallback result");
                }
                foreach (var span in await fixture.GetSpans()) Ok(span.ParentContext is null, "Expected a recorded root");
            }),
            Case(factory, "propagation", "propagates across fresh adapters and multiple hops", async fixture =>
            {
                if (fixture.Propagation is null) return;
                await using var receiver = await factory();
                await using var worker = await factory();
                if (receiver.Propagation is null || worker.Propagation is null)
                    throw new TelemetryConformanceException("Expected propagation on each fixture");
                IReadOnlyDictionary<string, string>? senderCarrier = null;
                await fixture.Context.StartSpanAsync("sender", span => { senderCarrier = fixture.Propagation.Inject(span); });
                Ok(senderCarrier is not null, "Expected a sender carrier");
                IReadOnlyDictionary<string, string>? receiverCarrier = null;
                await receiver.Propagation.Extract(JsonRoundtrip(senderCarrier!))
                    .StartSpanAsync("receiver", span => { receiverCarrier = receiver.Propagation.Inject(span); });
                Ok(receiverCarrier is not null, "Expected a receiver carrier");
                await worker.Propagation.Extract(JsonRoundtrip(receiverCarrier!)).StartSpanAsync("worker", _ => { });
                DeepEqual(FindSpan(await receiver.GetSpans(), "receiver").ParentContext, senderCarrier, "receiver parent");
                DeepEqual(FindSpan(await worker.GetSpans(), "worker").ParentContext, receiverCarrier, "worker parent");
            }),
            Case(factory, "callback lifecycle", "admits once synchronously and preserves the result", async fixture =>
            {
                var admitted = false;
                var calls = 0;
                var expected = new object();
                var result = fixture.Context.StartSpanAsync("success", _ =>
                {
                    admitted = true;
                    calls++;
                    return expected;
                });
                Ok(admitted, "Expected synchronous admission");
                Equal(calls, 1, "callback calls");
                Same(await result, expected, "callback result");
                DeepEqual(FindSpan(await fixture.GetSpans(), "success").Status, SpanStatus.Ok, "status");
                Ok(FindSpan(await fixture.GetSpans(), "success").Settled, "Expected a settled span");
            }),
            Case(factory, "callback lifecycle", "preserves synchronous and asynchronous rejection values", async fixture =>
            {
                var syncError = new InvalidOperationException("sync");
                await RejectsWithSameValue(fixture.Context.StartSpanAsync<object>("sync-error", _ => throw syncError), syncError);
                var asyncError = new FormatException("async");
                await RejectsWithSameValue(fixture.Context.StartSpanAsync<object>("async-error", async _ =>
                {
                    await Task.Yield();
                    throw asyncError;
                }), asyncError);
                var canceled = new OperationCanceledException("canceled");
                await RejectsWithSameValue(fixture.Context.StartSpanAsync<object>("canceled-error", async _ =>
                {
                    await Task.Yield();
                    throw canceled;
                }), canceled);
                var spans = await fixture.GetSpans();
                foreach (var name in new[] { "sync-error", "async-error", "canceled-error" })
                    Ok(FindSpan(spans, name).Status.IsError, $"Expected an error status on {name}");
            }),
            Case(factory, "status", "uses last explicit status without automatic overwrite", async fixture =>
            {
                await fixture.Context.StartSpanAsync("last-status", span =>
                {
                    span.SetStatus(SpanStatus.Failed("Expected", "first"));
                    span.SetStatus(SpanStatus.Ok);
                });
                var thrown = new InvalidOperationException("after explicit status");
                await RejectsWithSameValue(fixture.Context.StartSpanAsync<object>("explicit-before-throw", span =>
                {
                    span.SetStatus(SpanStatus.Ok);
                    throw thrown;
                }), thrown);
                var rejected = new InvalidOperationException("after async explicit status");
                await RejectsWithSameValue(fixture.Context.StartSpanAsync<object>("explicit-before-rejection", span =>
                {
                    span.SetStatus(SpanStatus.Failed("Expected", "async failure"));
                    return Task.FromException<object>(rejected);
                }), rejected);
                await fixture.Context.StartSpanAsync("expected-failure", span =>
                {
                    span.SetStatus(SpanStatus.Failed("Expected", "returned failure"));
                    return new { ok = false };
                });
                var spans = await fixture.GetSpans();
                DeepEqual(FindSpan(spans, "last-status").Status, SpanStatus.Ok, "last-status");
                DeepEqual(FindSpan(spans, "explicit-before-throw").Status, SpanStatus.Ok, "explicit-before-throw");
                DeepEqual(FindSpan(spans, "explicit-before-rejection").Status, SpanStatus.Failed("Expected", "async failure"), "explicit-before-rejection");
                DeepEqual(FindSpan(spans, "expected-failure").Status, SpanStatus.Failed("Expected", "returned failure"), "expected-failure");
            }),
            Case(factory, "recording", "merges attributes and records ordered events", async fixture =>
            {
                await fixture.Context.StartSpanAsync(new SpanOptions("recording")
                {
                    Attributes = new SpanAttributes { ["start"] = "value", ["overwrite"] = "start", ["ignored"] = null }
                }, span =>
                {
                    span.SetAttributes(new SpanAttributes { ["count"] = 1, ["overwrite"] = "middle" });
                    span.SetAttributes(new SpanAttributes { ["count"] = null, ["overwrite"] = "end" });
                    span.AddEvent("first", new SpanAttributes { ["index"] = 1, ["ignored"] = null });
                    span.AddEvent("second", new SpanAttributes { ["index"] = 2 });
                });
                var recorded = FindSpan(await fixture.GetSpans(), "recording");
                DeepEqual(recorded.Attributes, new SpanAttributes { ["start"] = "value", ["overwrite"] = "end", ["count"] = 1 }, "attributes");
                DeepEqual(recorded.Events, new[]
                {
                    new RecordedTelemetryEvent("first", new SpanAttributes { ["index"] = 1 }),
                    new RecordedTelemetryEvent("second", new SpanAttributes { ["index"] = 2 })
                }, "events");
            }),
            Case(factory, "recording", "makes calls after settlement inert", async fixture =>
            {
                ITelemetrySpan? settledSpan = null;
                await fixture.Context.StartSpanAsync(new SpanOptions("settled") { Attributes = new SpanAttributes { ["value"] = "initial" } },
                    span => { settledSpan = span; });
                if (settledSpan is null) throw new TelemetryConformanceException("Expected callback span");
                settledSpan.SetAttributes(new SpanAttributes { ["value"] = "late" });
                settledSpan.AddEvent("late", new SpanAttributes { ["value"] = true });
                settledSpan.SetStatus(SpanStatus.Failed());
                var childAdmitted = false;
                var childResult = settledSpan.StartSpanAsync("late-child", _ =>
                {
                    childAdmitted = true;
                    return 7;
                });
                Ok(childAdmitted, "Expected synchronous admission");
                Equal(await childResult, 7, "late child result");
                var spans = await fixture.GetSpans();
                Equal(spans.Count, 1, "span count");
                DeepEqual(spans[0].Attributes, new SpanAttributes { ["value"] = "initial" }, "attributes");
                DeepEqual(spans[0].Events, Array.Empty<RecordedTelemetryEvent>(), "events");
                DeepEqual(spans[0].Status, SpanStatus.Ok, "status");
            }),
            Case(factory, "parentage", "records nested and concurrent child relationships", async fixture =>
            {
                var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await fixture.Context.StartSpanAsync("parent", async parent =>
                {
                    var first = parent.StartSpanAsync("first-child", async _ => { await firstGate.Task; });
                    var second = parent.StartSpanAsync("second-child", _ => "done");
                    Equal(await second, "done", "second child result");
                    firstGate.SetResult();
                    await first;
                });
                var spans = await fixture.GetSpans();
                var parentSpan = FindSpan(spans, "parent");
                var firstSpan = FindSpan(spans, "first-child");
                var secondSpan = FindSpan(spans, "second-child");
                Ok(parentSpan.ParentId is null, "Expected a root parent");
                Equal(firstSpan.ParentId, parentSpan.Id, "first child parent");
                Equal(secondSpan.ParentId, parentSpan.Id, "second child parent");
                Ok(secondSpan.EndSequence is not null && firstSpan.EndSequence is not null && parentSpan.EndSequence is not null,
                    "Expected end sequences");
                Ok(secondSpan.EndSequence < firstSpan.EndSequence, "Expected the second child to settle first");
                Ok(firstSpan.EndSequence < parentSpan.EndSequence, "Expected the first child to settle before its parent");
            })
        ];
    }

    private static TelemetryAdapterConformanceCase Case(TelemetryAdapterFixtureFactory factory, string group, string name,
        Func<TelemetryAdapterFixture, Task> test) =>
        new(group, name, async () =>
        {
            await using var fixture = await factory();
            await test(fixture);
        });

    private static RecordedTelemetrySpan FindSpan(IReadOnlyList<RecordedTelemetrySpan> spans, string name) =>
        spans.FirstOrDefault(candidate => candidate.Name == name) ?? throw new TelemetryConformanceException($"Expected recorded span {name}");

    private static async Task RejectsWithSameValue(Task operation, Exception expected)
    {
        try
        {
            await operation;
        }
        catch (Exception error)
        {
            Same(error, expected, "rejection value");
            return;
        }
        throw new TelemetryConformanceException("Expected operation to reject");
    }

    private static Dictionary<string, string> JsonRoundtrip(IReadOnlyDictionary<string, string> carrier) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(carrier))!;

    private static void Ok(bool condition, string message)
    {
        if (!condition) throw new TelemetryConformanceException(message);
    }

    private static void Same(object? actual, object? expected, string what)
    {
        if (!ReferenceEquals(actual, expected)) throw new TelemetryConformanceException($"Expected the same {what} instance.");
    }

    private static void Equal<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new TelemetryConformanceException($"Expected {what} {expected}, got {actual}.");
    }

    /// <summary>deepStrictEqual through the values' JSON forms, ignoring object key order.</summary>
    private static void DeepEqual(object? actual, object? expected, string what)
    {
        var left = JsonSerializer.SerializeToNode(actual);
        var right = JsonSerializer.SerializeToNode(expected);
        if (!JsonNode.DeepEquals(left, right))
            throw new TelemetryConformanceException($"Expected {what} {right?.ToJsonString() ?? "null"}, got {left?.ToJsonString() ?? "null"}.");
    }
}
