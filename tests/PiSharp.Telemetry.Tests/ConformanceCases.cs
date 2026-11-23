using System.Diagnostics;
using System.Diagnostics.Metrics;
using PiSharp.Telemetry;
using PiSharp.Telemetry.Testing;

// packages/telemetry/test/conformance.test.ts at Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): the adapter conformance kit
// (PiSharp.Telemetry.Testing) against the in-memory adapter, and the snapshot case. PiSharp also runs the kit's capability group
// against the no-op adapter, which records nothing for the other groups to read, and runs the full conformance kit against
// DiagnosticsTelemetry via System.Diagnostics.ActivityListener and MeterListener.
internal static partial class Program
{
    private static Task<TelemetryAdapterFixture> MemoryFixture()
    {
        var context = new InMemoryTelemetryContext();
        return Task.FromResult(new TelemetryAdapterFixture
        {
            Context = context,
            Propagation = context,
            Metrics = context,
            GetMetrics = () => Task.FromResult(context.GetMetrics()),
            GetSpans = () => Task.FromResult(context.GetSpans())
        });
    }

    private static Task<TelemetryAdapterFixture> NoopFixture() => Task.FromResult(new TelemetryAdapterFixture
    {
        Context = NoopTelemetry.Context,
        GetSpans = () => Task.FromResult<IReadOnlyList<RecordedTelemetrySpan>>([])
    });

    private static Task<TelemetryAdapterFixture> DiagnosticsFixture()
    {
        var sourceName = "PiSharp.Telemetry.Conformance." + Guid.NewGuid().ToString("N");
        var adapter = new DiagnosticsTelemetry(sourceName, "1.0.0");
        var gate = new Lock();
        var nextSpanId = 1;
        var nextEndSeq = 1;
        var localIds = new Dictionary<ActivitySpanId, int>();
        var started = new List<(int Id, Activity Activity)>();
        var endSeq = new Dictionary<ActivitySpanId, int>();
        var measurements = new List<RecordedTelemetryMeasurement>();

        static Dictionary<string, string> CarrierOf(ActivityContext ctx)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["traceparent"] = $"00-{ctx.TraceId.ToHexString()}-{ctx.SpanId.ToHexString()}-{(byte)ctx.TraceFlags:x2}",
            };
            if (!string.IsNullOrEmpty(ctx.TraceState)) dict["tracestate"] = ctx.TraceState;
            return dict;
        }

        static AttributeValue? FromObject(object? value) => value switch
        {
            null => null,
            string s => s,
            double d => d,
            int i => (double)i,
            long l => (double)l,
            bool b => b,
            string[] sa => sa,
            double[] da => da,
            bool[] ba => ba,
            object[] oa => AttributeValue.FromStringNumberArray(oa.Select(item => FromObject(item)!)),
            _ => value.ToString(),
        };

        static SpanAttributes FromTags(IEnumerable<KeyValuePair<string, object?>> tags)
        {
            var attrs = new SpanAttributes();
            foreach (var (key, value) in tags)
            {
                if (FromObject(value) is { } converted) attrs[key] = converted;
            }
            return attrs;
        }

        var activityListener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, adapter.ActivitySource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                lock (gate)
                {
                    var id = nextSpanId++;
                    localIds[activity.SpanId] = id;
                    started.Add((id, activity));
                }
            },
            ActivityStopped = activity =>
            {
                lock (gate) endSeq[activity.SpanId] = nextEndSeq++;
            },
        };
        ActivitySource.AddActivityListener(activityListener);

        var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, adapter.Meter)) listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var kind = instrument switch
            {
                Counter<double> => TelemetryMetricKind.Counter,
                UpDownCounter<double> => TelemetryMetricKind.UpDownCounter,
                Histogram<double> => TelemetryMetricKind.Histogram,
                _ => throw new InvalidOperationException($"Unexpected instrument {instrument.GetType().Name}"),
            };
            IReadOnlyList<double>? boundaries = instrument is Histogram<double> h && h.Advice?.HistogramBucketBoundaries is { } b
                ? [.. b] : null;
            var attrs = new SpanAttributes();
            foreach (var (k, v) in tags)
            {
                if (FromObject(v) is { } converted) attrs[k] = converted;
            }
            var exemplar = Activity.Current is { } current ? CarrierOf(current.Context) : null;
            lock (gate)
            {
                measurements.Add(new RecordedTelemetryMeasurement(kind, instrument.Name, instrument.Description, instrument.Unit,
                    boundaries, value, attrs, exemplar));
            }
        });
        meterListener.Start();

        return Task.FromResult(new TelemetryAdapterFixture
        {
            Context = adapter,
            Propagation = adapter,
            Metrics = adapter,
            GetMetrics = () =>
            {
                lock (gate) return Task.FromResult<IReadOnlyList<RecordedTelemetryMeasurement>>([.. measurements]);
            },
            GetSpans = () =>
            {
                lock (gate)
                {
                    var spans = started.Select(entry =>
                    {
                        var a = entry.Activity;
                        int? parentId = a.ParentSpanId != default && localIds.TryGetValue(a.ParentSpanId, out var pid) ? pid : null;
                        IReadOnlyDictionary<string, string>? parentContext = a.ParentSpanId != default
                            ? CarrierOf(new ActivityContext(a.TraceId, a.ParentSpanId, a.ActivityTraceFlags, a.TraceStateString))
                            : null;
                        var links = a.Links.Select(l => new RecordedTelemetryLink(CarrierOf(l.Context), FromTags(l.Tags ?? []))).ToArray();
                        var events = a.Events.Select(e => new RecordedTelemetryEvent(e.Name, FromTags(e.Tags))).ToArray();
                        var status = a.Status == ActivityStatusCode.Error
                            ? SpanStatus.Failed(a.GetCustomProperty("pi.telemetry.error") as SpanError)
                            : SpanStatus.Ok;
                        var settled = endSeq.TryGetValue(a.SpanId, out var seq);
                        return new RecordedTelemetrySpan(entry.Id, parentId, CarrierOf(a.Context), parentContext, links,
                            a.DisplayName, FromTags(a.TagObjects), events, status, settled, settled ? seq : null);
                    }).ToArray();
                    return Task.FromResult<IReadOnlyList<RecordedTelemetrySpan>>(spans);
                }
            },
            Dispose = () =>
            {
                meterListener.Dispose();
                activityListener.Dispose();
                adapter.Dispose();
                return ValueTask.CompletedTask;
            },
        });
    }

    private static IEnumerable<(string Id, Func<Task> Run)> ConformanceCases =>
    [
        .. TelemetryAdapterConformance.Create(MemoryFixture)
            .Select(testCase => ($"telemetry.conformance.memory: {testCase.Group}: {testCase.Name}", (Func<Task>)testCase.RunAsync)),
        .. TelemetryAdapterConformance.Create(NoopFixture).Where(testCase => testCase.Group == "capabilities")
            .Select(testCase => ($"telemetry.conformance.noop: {testCase.Group}: {testCase.Name}", (Func<Task>)testCase.RunAsync)),
        .. TelemetryAdapterConformance.Create(DiagnosticsFixture)
            .Select(testCase => ($"telemetry.conformance.diagnostics: {testCase.Group}: {testCase.Name}", (Func<Task>)testCase.RunAsync)),
        ("telemetry.diagnostics: unsampled without listener stays inert and preserves capabilities and result", async () =>
        {
            using var adapter = new DiagnosticsTelemetry("PiSharp.Telemetry.Unsampled." + Guid.NewGuid().ToString("N"));
            var result = await adapter.StartSpanAsync("unsampled", span =>
            {
                Same(adapter, span.Metrics, "metrics capability");
                Same(adapter, span.Propagation, "propagation capability");
                span.SetAttributes(new SpanAttributes { ["k"] = "v" });
                span.AddEvent("ev");
                span.SetStatus(SpanStatus.Ok);
                return 99;
            });
            Equal(99, result, "unsampled result");
        }),
        ("telemetry.conformance.memory: returns detached snapshots without exposing mutable recording state", async () =>
        {
            var context = new InMemoryTelemetryContext();
            bool? openSettled = null;
            int? openEndSequence = -1;
            await context.StartSpanAsync(new SpanOptions("snapshot") { Attributes = new() { ["tags"] = new[] { "initial" } } }, span =>
            {
                span.AddEvent("event", new SpanAttributes { ["value"] = 1 });
                var open = context.GetSpans()[0];
                openSettled = open.Settled;
                openEndSequence = open.EndSequence;
            });
            Equal(false, openSettled, "open span is unsettled");
            Check(openEndSequence is null, "open span has no end sequence");
            var first = context.GetSpans()[0];
            Check(first.Settled, "settled");
            Equal<int?>(1, first.EndSequence, "end sequence");
            first.Attributes["tags"] = new[] { "mutated" };
            first.Events[0].Attributes["value"] = 2;
            var second = context.GetSpans()[0];
            Json(new { tags = new[] { "initial" } }, second.Attributes, "fresh attributes");
            Json(new[] { new { name = "event", attributes = new { value = 1 } } }, second.Events, "fresh events");
        }),
    ];
}
