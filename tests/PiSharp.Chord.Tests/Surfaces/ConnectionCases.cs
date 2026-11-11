// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/connection.ts, packages/chord/src/surfaces/client.ts.
// Authored native regression; not an upstream execution capture.
using PiSharp.Chord.Surfaces;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SurfacesConnectionCases() =>
    [
        Case("surfaces.connection.backoff-cap-and-stable-reset", () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>();
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => Task.FromResult(true), OnState = _ => { },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); return link; },
            });
            double[] delays = [250, 500, 1000, 2000, 4000, 5000, 5000];
            foreach (var delay in delays)
            {
                links[^1].Close(); var count = links.Count;
                environment.Advance(delay - 1); Equal(count, links.Count, "no early retry");
                environment.Advance(1); Equal(count + 1, links.Count, "retry after backoff");
            }
            links[^1].Events.Opened(); environment.Advance(10000); links[^1].Close();
            var before = links.Count; environment.Advance(249); Equal(before, links.Count, "stable resets first pause");
            environment.Advance(1); Equal(before + 1, links.Count, "stable retry is 250 ms");
        }),
        Case("surfaces.connection.connect-timeout-offline-and-wake", () =>
        {
            var environment = new SurfaceClock { IsOnline = false }; var links = new List<SurfaceLink>();
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => Task.FromResult(true), OnState = _ => { },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); return link; },
            });
            environment.Advance(19999); Check(!links[0].Closed, "connecting still has its timeout");
            environment.Advance(1); Check(links[0].Closed, "twenty-second connect timeout closes");
            environment.Advance(4999); Equal(1, links.Count, "offline always pauses five seconds");
            environment.IsOnline = true; environment.Wake(); Equal(2, links.Count, "online wake retries immediately");
        }),
        Case("surfaces.connection.wake-grace-receives-queued-ping", () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>();
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => Task.FromResult(true), OnState = _ => { },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); events.Opened(); return link; },
            });
            environment.Advance(30000); environment.Wake(); environment.Advance(999);
            Check(!links[0].Closed, "wake leaves one second for queued traffic");
            links[0].Events.Received(); environment.Advance(1); Check(!links[0].Closed, "queued ping preserves link");
            environment.Advance(26000); environment.Wake(); environment.Advance(1000);
            Check(links[0].Closed, "silent stale link replaced");
            environment.Advance(250); Equal(2, links.Count, "one replacement only");
        }),
        Case("surfaces.connection.can-retry-close-and-stop", () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>(); var checks = 0;
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => { checks++; return Task.FromResult(false); }, OnState = _ => { },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); return link; },
            });
            links[0].Close(); environment.Advance(60000); environment.Wake();
            Equal(1, checks, "retry policy checked after close"); Equal(1, links.Count, "false prevents retry");
            loop.Stop(); environment.Advance(60000); Equal(0, environment.ActiveTimers, "stop owns timers");
        }),
        Case("surfaces.connection.wake-during-asynchronous-decision", async () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>();
            var decide = new TaskCompletionSource<bool>();
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => decide.Task, OnState = _ => { },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); return link; },
            });
            links[0].Close(); environment.Wake(); decide.SetResult(true);
            // The continuation can run inline; wait for its scheduling without advancing virtual time.
            for (var i = 0; environment.ActiveTimers == 0 && i < 100; i++) await Task.Yield();
            environment.Advance(0); Equal(2, links.Count, "wake while deciding uses zero delay");
        }),
    ];

    private sealed class SurfaceLink(ISocketEvents events) : ILoopLink
    {
        private Action? onClose;
        public ISocketEvents Events { get; } = events;
        public bool Closed { get; private set; }
        public void OnClose(Action listener) { if (Closed) listener(); else onClose = listener; }
        public void Close() { if (Closed) return; Closed = true; onClose?.Invoke(); }
    }

    private sealed class SurfaceClock : ILoopEnvironment
    {
        private double now; private long sequence;
        private readonly List<ScheduledSurface> timers = [];
        private event Action? Woken;
        public Action<double>? Scheduled { get; init; }
        public bool IsOnline { get; set; } = true;
        public double Now() => now;
        public bool Online() => IsOnline;
        public int ActiveTimers => timers.Count(timer => !timer.Cancelled);
        public Action OnWake(Action listener) { Woken += listener; return () => Woken -= listener; }
        public void Wake() => Woken?.Invoke();
        public Action After(double milliseconds, Action callback)
        {
            var timer = new ScheduledSurface(now + milliseconds, sequence++, callback); timers.Add(timer);
            Scheduled?.Invoke(milliseconds);
            return () => timer.Cancelled = true;
        }
        public void Advance(double milliseconds)
        {
            var until = now + milliseconds;
            while (timers.Where(timer => !timer.Cancelled && timer.At <= until).OrderBy(timer => timer.At).ThenBy(timer => timer.Sequence).FirstOrDefault() is { } next)
            { next.Cancelled = true; now = next.At; next.Callback(); }
            now = until;
        }
        private sealed class ScheduledSurface(double at, long sequence, Action callback)
        {
            public double At { get; } = at;
            public long Sequence { get; } = sequence;
            public Action Callback { get; } = callback;
            public bool Cancelled { get; set; }
        }
    }
}
