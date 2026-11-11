// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/telemetry/src/index.ts, packages/telemetry/src/render.ts.
// Authored native regression; not an upstream execution capture.
using PiSharp.Telemetry;

internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> ClockRender152Cases =>
    [
        ("telemetry.152.clock-monotonic-anchor-and-wall-step", Sync(() =>
        {
            var time = new ManualTime(1000, 30);
            Equal(1000d, TelemetryClock.Now(time), "initial anchor");
            time.ClockMs += 0.1234;
            Equal(1000.123d, TelemetryClock.Now(time), "submillisecond monotonic clock");
            time.NowMs = 1010;
            Equal(1000.123d, TelemetryClock.Now(time), "within tolerance keeps anchor");
            time.NowMs = 1011;
            Equal(1011d, TelemetryClock.Now(time), "forward wall step resets anchor");
            time.NowMs = 800;
            Equal(800d, TelemetryClock.Now(time), "backward wall step resets anchor");
            Equal(-0.001d, TelemetryClock.RoundMillisecondsToMicrosecond(-0.0015), "ties toward positive infinity");
        })),
        ("telemetry.152.stored-microsecond-times", async () =>
        {
            var time = new ManualTime(1000, 0);
            var telemetry = new InMemoryTelemetryContext(time);
            await telemetry.StartSpanAsync("span", span =>
            {
                time.ClockMs = 0.1234; span.AddEvent("event"); time.ClockMs = 0.5678;
            });
            var span = (await telemetry.FindTracesAsync()).Traces.Single().Spans.Single();
            Equal(1000d, span.StartedMs, "start");
            Equal(1000.568d, span.EndedMs, "rounded end");
            Equal(0.123d, span.Events.Single().TimeMs, "rounded event");
        }),
        ("telemetry.152.render-control-fields-and-rounding", Sync(() =>
        {
            var controls = new string(Enumerable.Range(0, 32).Concat(Enumerable.Range(127, 33)).Select(value => (char)value).ToArray());
            var text = TraceRenderer.RenderTrace([Stored("id", name: "name" + controls, endedMs: 0.123456,
                attributes: new SpanAttributes { ["key" + controls] = "safe" },
                events: [new("event" + controls, 0.98765, new())],
                links: [new("trace" + controls, "span" + controls)],
                status: SpanStatus.Failed("error" + controls, "message" + controls))]);
            Check(!text.Any(character => character is <= '\u001f' and not '\n' || character is >= '\u007f' and <= '\u009f'), "no raw controls except renderer line breaks");
            foreach (var character in controls) Check(text.Contains("\\u" + ((int)character).ToString("x4")), "escaped control " + (int)character);
            Check(text.Contains("(0.123ms)") && text.Contains("(+0.988ms)"), "microsecond durations");
        }))
    ];
}
