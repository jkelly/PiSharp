// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry.ts.
// Authored native regression; not an upstream execution capture.
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Telemetry;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Telemetry152Cases()
    {
        yield return Case("telemetry.152.schema-and-cached-instruments", () =>
        {
            Equal(14, ChordTelemetry.Schema.Spans.Count, "span count");
            Equal(25, ChordTelemetry.Schema.Metrics!.Count, "metric count");
            Check(ChordTelemetry.Schema.Spans.All(pair => pair.Value.StartAttributes.ContainsKey("chord.host")), "every span names its host");
            Check(ChordTelemetry.Schema.Spans["chord.method"].Events!["chord.commit"].Attributes.ContainsKey("rows"), "commits count table rows");
            var memory = new InMemoryTelemetryContext();
            var metrics = ChordTelemetry.Metrics(memory);
            Check(ReferenceEquals(metrics, ChordTelemetry.Metrics(memory)), "same metrics wrapper");
            Check(ReferenceEquals(metrics.Counter("chord.link.bytes"), metrics.Counter("chord.link.bytes")), "same instrument");
            metrics.Counter("chord.link.bytes").Add(3, new() { ["direction"] = "sent" });
            Equal("By", memory.GetMetrics().Single().Unit, "schema supplies unit");
            Refuses(() => metrics.Counter("chord.link.bytes").Add(1), "direction");
            Check(ChordTelemetry.IsUntraced(ChordTelemetry.CountingOnly(memory)), "counting scope is untraced");
            Check(ChordTelemetry.CarrierOf(NoopTelemetry.Context) is null, "no-op has no carrier");
        });
        yield return Case("telemetry.152.open-span-status-and-propagation", async () =>
        {
            var memory = new InMemoryTelemetryContext();
            var open = new OpenSpan(memory, "operation", new());
            Equal(0, (await memory.FindTracesAsync()).Traces.Count, "open span not stored");
            var carrier = ChordTelemetry.CarrierOf(open.Span);
            Check(carrier is not null, "open span carrier");
            await ChordTelemetry.ParentOf(carrier, memory).StartSpanAsync("child", _ => { });
            open.EndWhen(Task.FromException(new ChordError("aborted", "cancelled")), "call_failed");
            await open.Completion;
            open.End();
            var traces = await memory.FindTracesAsync();
            Equal(1, traces.Traces.Count, "parentage preserved");
            var parent = traces.Traces.Single().Spans.Single(span => span.Name == "operation");
            Check(!parent.Status.IsError, "cancel is OK");
            Equal("aborted", parent.Attributes["outcome"]!.GetString(), "cancel outcome");
            var failed = new OpenSpan(memory, "failure", new());
            ChordTelemetry.SpanFailed(failed.Span, "method_failed", new Exception(new string('x', 600)));
            failed.End(); await failed.Completion;
            var status = (await memory.FindTracesAsync()).Traces.SelectMany(trace => trace.Spans).Single(span => span.Name == "failure").Status;
            Equal("method_failed", status.Error!.Name, "fixed category");
            Equal(500, status.Error.Message.Length, "bounded message");
        });
    }
}
