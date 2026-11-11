// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/host/unbound-clock.ts.
// Authored native regression; not an upstream execution capture.
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> UnboundClockCases() =>
    [
        Case("host.unbound-clock.reports-in-start-order-with-one-timer", () =>
        {
            var time = new FakeTimers();
            var reports = new List<HostError>();
            using var clock = new UnboundClock(time, action => action(), reports.Add);
            var error = new ChordError("unavailable", "not served");
            var a = new object(); var b = new object(); var stopped = new object();
            clock.Start(a, "app.a", error);
            time.AdvanceTimersByTime(2000);
            clock.Start(b, "app.b", error);
            clock.Start(stopped, "app.stopped", error);
            clock.Stop(stopped);
            Equal(1, time.TimerCount, "one host timer for multiple requests");
            time.AdvanceTimersByTime(2999);
            Equal(0, reports.Count, "not reported early");
            time.AdvanceTimersByTime(1);
            Equal(1, reports.Count, "first wait is due");
            Equal("app.a", reports[0].WaitingFor, "first wait");
            Equal<double?>(0, reports[0].Since, "original timestamp");
            time.AdvanceTimersByTime(2000);
            Sequence(new[] { "app.a", "app.b" }, reports.Select(item => item.WaitingFor!), "wait order");
            Equal(0, time.TimerCount, "reported waits release timer");
            time.AdvanceTimersByTime(10000);
            Equal(2, reports.Count, "each episode reported only once");
        }),
        Case("host.unbound-clock.updated-error-does-not-reset-episode", () =>
        {
            var time = new FakeTimers();
            var reports = new List<HostError>();
            using var clock = new UnboundClock(time, action => action(), reports.Add);
            var request = new object();
            var old = new ChordError("unavailable", "old");
            var latest = new ChordError("contract_mismatch", "latest");
            clock.Start(request, "registry app.r", old);
            time.AdvanceTimersByTime(1000);
            clock.Start(request, "ignored replacement label", old);
            clock.Failed(request, latest);
            time.AdvanceTimersByTime(4000);
            Equal(1, reports.Count, "original deadline");
            Check(ReferenceEquals(latest, reports[0].Error), "latest failure is reported");
            Equal("registry app.r", reports[0].WaitingFor, "original label");
            Equal<double?>(0, reports[0].Since, "original since");
            clock.Start(request, "next episode", old);
            time.AdvanceTimersByTime(5000);
            Equal(2, reports.Count, "reported request may begin another episode");
            Equal<double?>(5000, reports[1].Since, "new episode timestamp");
        }),
        Case("host.unbound-clock.stale-dispatch-cannot-report-new-episode", () =>
        {
            var time = new FakeTimers();
            var queued = new Queue<Action>();
            var reports = new List<HostError>();
            using var clock = new UnboundClock(time, queued.Enqueue, reports.Add);
            var request = new object();
            var error = new ChordError("unavailable", "not served");
            clock.Start(request, "old", error);
            time.AdvanceTimersByTime(5000);
            Equal(1, queued.Count, "timer callback queued for host dispatcher");
            clock.Stop(request);
            clock.Start(request, "new", error);
            queued.Dequeue()();
            Equal(0, reports.Count, "stale callback ignored");
            Equal(1, time.TimerCount, "new episode retains its timer");
            time.AdvanceTimersByTime(5000);
            queued.Dequeue()();
            Equal("new", reports.Single().WaitingFor, "new episode reported at its own deadline");
            clock.Start(new object(), "closed", error);
            time.AdvanceTimersByTime(5000);
            clock.Close();
            queued.Dequeue()();
            Equal(1, reports.Count, "queued callback after close ignored");
            Equal(0, time.TimerCount, "close releases timer");
        }),
        Case("host.unbound-clock.report-callback-may-stop-or-close", () =>
        {
            var time = new FakeTimers();
            var reports = new List<HostError>();
            var later = new object();
            UnboundClock? clock = null;
            using (clock = new UnboundClock(time, action => action(), item =>
            {
                reports.Add(item);
                clock!.Stop(later);
                clock.Close();
            }))
            {
                var error = new ChordError("unavailable", "not served");
                clock.Start(new object(), "first", error);
                clock.Start(later, "stopped in callback", error);
                time.AdvanceTimersByTime(5000);
                Equal(1, reports.Count, "reentrant stop prevents later due report");
                Equal(0, time.TimerCount, "reentrant close cancels all timers");
            }
        }),
    ];
}
