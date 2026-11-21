// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/telemetry.ts.
// Authored native regression; not an upstream execution capture. The 1.6.0 schema rows: the query counters a commit's read index counts
// on (chord.query.probes, chord.query.skips, chord.query.rows), the event counters (chord.events.emitted, sent, dropped, encodes) and the
// span event chord.admit. Upstream's telemetry.test.ts delta in v1.5.2..v1.6.0 is one re-hold case (an object's first hold is a root
// linked to its getter), which belongs to shared holds (chord-remote); the layers that emit these rows test their counts.
using PiSharp.Chord;
using PiSharp.Telemetry;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Telemetry160Cases()
    {
        yield return Case("telemetry.160.schema-rows-for-queries-events-and-admission", () =>
        {
            var schema = ChordTelemetry.Schema;
            Equal(14, schema.Spans.Count, "span count");
            Equal(32, schema.Metrics!.Count, "metric count");
            foreach (var name in new[]
            {
                "chord.query.probes", "chord.query.skips", "chord.query.rows",
                "chord.events.emitted", "chord.events.sent", "chord.events.dropped", "chord.events.encodes",
            })
            {
                Check(schema.Metrics.TryGetValue(name, out var metric), name + " is declared");
                Equal(TelemetryMetricKind.Counter, metric!.Kind, name + " is a counter");
            }
            Equal("Keyed probes a commit asked before running a live query again.", schema.Metrics["chord.query.probes"].Description, "probes");
            Equal("Live queries a commit writing their tables did not run again.", schema.Metrics["chord.query.skips"].Description, "skips");
            Equal("Rows live query runs returned (output, not what the backend scanned).", schema.Metrics["chord.query.rows"].Description, "rows");
            // The admission's decision is an event of the three spans it can be read under.
            foreach (var span in new[] { "chord.relay", "chord.hold", "chord.activate" })
                Check(schema.Spans[span].Events is { } events && events.TryGetValue("chord.admit", out var admit) && admit.Attributes.ContainsKey("outcome"),
                    span + " carries chord.admit with its outcome");
            Check(schema.Spans["chord.activate"].Events!.ContainsKey("chord.park") && schema.Spans["chord.activate"].Events!.ContainsKey("chord.retry"),
                "an attempt's other events stay");
            Check(schema.Spans.Where(pair => pair.Key is not ("chord.relay" or "chord.hold" or "chord.activate"))
                .All(pair => pair.Value.Events?.ContainsKey("chord.admit") != true), "no other span carries it");
        });
        yield return Case("telemetry.160.instruments-count-and-the-dropped-reason-is-required", () =>
        {
            var memory = new InMemoryTelemetryContext();
            var metrics = ChordTelemetry.Metrics(memory);
            metrics.Counter("chord.query.probes").Add(2);
            metrics.Counter("chord.query.skips").Add(1);
            metrics.Counter("chord.query.rows").Add(40);
            metrics.Counter("chord.events.emitted").Add(1);
            metrics.Counter("chord.events.sent").Add(3);
            metrics.Counter("chord.events.encodes").Add(1);
            foreach (var reason in new[] { "window", "too_large", "mapping" })
                metrics.Counter("chord.events.dropped").Add(1, ChordTelemetry.EventsDropped(reason));
            Refuses(() => metrics.Counter("chord.events.dropped").Add(1), "reason");
            Refuses(() => metrics.Counter("chord.events.dropped").Add(1, ChordTelemetry.EventsDropped("other")), "reason");
            var totals = memory.GetMetrics().GroupBy(measurement => measurement.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(measurement => measurement.Value), StringComparer.Ordinal);
            Sequence([2.0, 1.0, 40.0, 1.0, 3.0, 1.0, 3.0], new[]
            {
                "chord.query.probes", "chord.query.skips", "chord.query.rows", "chord.events.emitted", "chord.events.sent", "chord.events.encodes",
                "chord.events.dropped",
            }.Select(name => totals[name]), "the counts");
            // The names are counters only: another instrument kind is refused, as for every Chord metric.
            Refuses(() => metrics.Histogram("chord.query.rows"), "chord.query.rows");
        });
        yield return Case("telemetry.160.admitted-records-the-decision-on-its-span", async () =>
        {
            var memory = new InMemoryTelemetryContext();
            var allowed = new OpenSpan(memory, "admitting", new());
            ChordTelemetry.Admitted(allowed.Span, true);
            ChordTelemetry.Admitted(allowed.Span, false);
            allowed.End(); await allowed.Completion;
            var span = (await memory.FindTracesAsync()).Traces.Single().Spans.Single(recorded => recorded.Name == "admitting");
            Sequence(["chord.admit", "chord.admit"], span.Events.Select(recorded => recorded.Name), "the events");
            Sequence(["allow", "deny"], span.Events.Select(recorded => recorded.Attributes["outcome"]!.GetString()!), "the outcomes");
        });
    }
}
