using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Delta;
using PiSharp.Telemetry;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/watch.test.ts, for Chord v2's copy of the watch port (Watch.cs,
// area cv2-state). The copy is PiSharp.Durable's port on ChordContext, whose cases (tests/PiSharp.Durable.Tests/Chord/WatchCases.cs) cover
// the logic both share; the state cases (State/*Cases.cs) run this copy through its sources. The cases here cover what the copy changed:
// the context a call gets (ChordContext.WithoutAbortSignal; upstream's case, with a telemetry span standing in for upstream's context
// value, which ChordContext does not have) and a throwing report, which escapes the delivery as upstream's does where Durable's port
// contains it (PiSharp).
internal static partial class Program
{
    private static WatchValueCall<JsonNode?> WatchFrame(double value) => new(value, [new ReplaceOp(value)]);

    private static IEnumerable<(string, Func<Task>)> WatchCases() =>
    [
        Case("watch.delivery.delivers-the-newest-frames-context-without-its-abort-signal", async () =>
        {
            var jobs = new StateJobs();
            var watch = new Watch<JsonNode?>(0, () => { }, _ => { }, schedule: jobs.Post);
            var span = await new InMemoryTelemetryContext().StartSpanAsync("second", span => span);
            using var controller = new CancellationTokenSource();
            var delivered = new List<ChordContext>();
            watch.Start((_, context) =>
            {
                delivered.Add(context);
                return Task.CompletedTask;
            });
            watch.Advance(WatchFrame(1), StateBg);
            watch.Advance(WatchFrame(2), ChordContext.WithAbortSignal(controller.Token, ChordContext.WithTelemetry(span, StateBg)));
            jobs.Flush();
            Equal(1, delivered.Count, "one call");
            Check(ReferenceEquals(span, delivered[0].Telemetry), "the newest frame's context");
            Check(delivered[0].AbortSignal is null, "without its abort signal");
        }),
        Case("watch.ends.a-throwing-report-escapes-the-delivery-and-leaves-the-watch-open-as-upstreams-does-pisharp", async () =>
        {
            var jobs = new StateJobs();
            var reports = new List<Exception>();
            var watch = new Watch<JsonNode?>(0, () => { }, error =>
            {
                reports.Add(error);
                throw new InvalidOperationException("report threw");
            }, schedule: jobs.Post);
            var threw = new InvalidOperationException("listener threw");
            watch.Start((_, _) => throw threw);
            watch.Advance(WatchFrame(1), StateBg);
            // The drain's task faults (unobserved); the scheduler's job returns.
            Accepts(jobs.Flush, "the delivery");
            Check(reports.Count == 1 && ReferenceEquals(threw, reports[0]), "reported once");
            Check(!watch.Closed.IsCompleted, "the watch is not ended by it");
            Equal(WatchEnd.Stopped, await watch.StopAsync(), "it still stops");
        }),
    ];
}
